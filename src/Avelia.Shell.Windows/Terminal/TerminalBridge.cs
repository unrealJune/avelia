using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Wires a live <see cref="ITerminalSession"/> (ConPTY) to an
/// <see cref="ITerminalRenderer"/> (xterm.js) and, optionally, tees output to an
/// <see cref="IAsciiCastWriter"/> for record/replay.
///
/// Output path: read ConPTY bytes → coalesce on a frame timer
/// (<see cref="TerminalOutputBatcher"/>) → one renderer write per frame, while
/// each raw chunk is also appended to the cast with its elapsed timestamp.
/// Input path: the renderer's keystrokes and resizes are forwarded straight to
/// the session. Recording is best-effort and never blocks or breaks the live
/// stream.
/// </summary>
internal sealed class TerminalBridge : IAsyncDisposable
{
    private readonly ITerminalSession _session;
    private readonly ITerminalRenderer _renderer;
    private readonly IAsciiCastWriter? _recorder;
    private readonly TerminalOutputBatcher _batcher = new();
    private readonly TimeSpan _flushInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();

    private Task? _readLoop;
    private Task? _flushLoop;
    private int _started;

    public TerminalBridge(
        ITerminalSession session,
        ITerminalRenderer renderer,
        IAsciiCastWriter? recorder = null,
        TimeSpan? flushInterval = null
    )
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _recorder = recorder;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(8);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("TerminalBridge.Start may be called only once.");

        _clock.Start();
        _renderer.InputReceived += OnInputReceived;
        _renderer.ResizeRequested += OnResizeRequested;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _session.ReadAllAsync(ct).WithCancellation(ct))
            {
                _batcher.Append(chunk.Span);

                if (_recorder is not null)
                {
                    try
                    {
                        await _recorder.AppendAsync(chunk, _clock.Elapsed, ct);
                    }
                    catch
                    {
                        // Recording is best-effort; a disk hiccup must not stall
                        // the live terminal.
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        // Final flush so a fast-exiting child's tail output still reaches the UI.
        await FlushOnceAsync(CancellationToken.None);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_flushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await FlushOnceAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushOnceAsync(CancellationToken ct)
    {
        if (!_batcher.TryDrain(out var batch))
            return;

        try
        {
            await _renderer.WriteAsync(batch, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async void OnInputReceived(object? sender, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            await _session.WriteAsync(bytes, _cts.Token);
        }
        catch
        {
            // Session may be tearing down; dropping a late keystroke is fine.
        }
    }

    private async void OnResizeRequested(object? sender, TerminalSize size)
    {
        try
        {
            await _session.ResizeAsync(size, _cts.Token);
        }
        catch
        {
            // Resize races with teardown are benign.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _renderer.InputReceived -= OnInputReceived;
        _renderer.ResizeRequested -= OnResizeRequested;

        if (!_cts.IsCancellationRequested)
            _cts.Cancel();

        foreach (var loop in new[] { _readLoop, _flushLoop })
        {
            if (loop is null)
                continue;
            try
            {
                await loop;
            }
            catch { }
        }

        if (_recorder is not null)
            await _recorder.DisposeAsync();

        _cts.Dispose();
    }
}
