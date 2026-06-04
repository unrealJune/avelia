using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Terminal;
using Xunit;

namespace Avelia.Shell.Windows.Tests.Terminal;

public class TerminalBridgeTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Output_flows_from_session_to_renderer_and_recorder()
    {
        var session = new FakeSession(new[] { "hel"u8.ToArray(), "lo"u8.ToArray() });
        var renderer = new FakeRenderer();
        var recorder = new FakeRecorder();

        await using var bridge = new TerminalBridge(
            session,
            renderer,
            recorder,
            TimeSpan.FromMilliseconds(5)
        );
        bridge.Start();

        await WaitUntilAsync(() => renderer.Text == "hello" && recorder.Text == "hello");

        Assert.Equal("hello", renderer.Text);
        Assert.Equal("hello", recorder.Text);
    }

    [Fact]
    public async Task Input_from_the_renderer_is_forwarded_to_the_session()
    {
        var session = new FakeSession(Array.Empty<byte[]>());
        var renderer = new FakeRenderer();

        await using var bridge = new TerminalBridge(
            session,
            renderer,
            flushInterval: TimeSpan.FromMilliseconds(5)
        );
        bridge.Start();

        renderer.RaiseInput("ls\r"u8.ToArray());

        await WaitUntilAsync(() => session.WrittenText == "ls\r");
        Assert.Equal("ls\r", session.WrittenText);
    }

    [Fact]
    public async Task Resize_from_the_renderer_is_forwarded_to_the_session()
    {
        var session = new FakeSession(Array.Empty<byte[]>());
        var renderer = new FakeRenderer();

        await using var bridge = new TerminalBridge(
            session,
            renderer,
            flushInterval: TimeSpan.FromMilliseconds(5)
        );
        bridge.Start();

        renderer.RaiseResize(new TerminalSize(120, 40));

        await WaitUntilAsync(() => session.LastResize is { Cols: 120, Rows: 40 });
        Assert.Equal(new TerminalSize(120, 40), session.LastResize);
    }

    [Fact]
    public async Task Disposing_the_bridge_disposes_the_recorder()
    {
        var session = new FakeSession(Array.Empty<byte[]>());
        var renderer = new FakeRenderer();
        var recorder = new FakeRecorder();

        var bridge = new TerminalBridge(session, renderer, recorder, TimeSpan.FromMilliseconds(5));
        bridge.Start();
        await bridge.DisposeAsync();

        Assert.True(recorder.Disposed);
    }

    [Fact]
    public void Start_twice_throws()
    {
        var bridge = new TerminalBridge(new FakeSession(Array.Empty<byte[]>()), new FakeRenderer());
        bridge.Start();

        Assert.Throws<InvalidOperationException>(() => bridge.Start());
    }

    // -- fakes ---------------------------------------------------------------

    private sealed class FakeSession : ITerminalSession
    {
        private readonly byte[][] _chunks;
        private readonly List<byte> _written = new();

        public FakeSession(byte[][] chunks) => _chunks = chunks;

        public TerminalSize Size { get; private set; } = new(80, 24);
        public TerminalSize? LastResize { get; private set; }

        public string WrittenText
        {
            get
            {
                lock (_written)
                    return Encoding.UTF8.GetString(_written.ToArray());
            }
        }

        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            lock (_written)
                _written.AddRange(bytes.ToArray());
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken
        )
        {
            foreach (var chunk in _chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }

        public Task ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
        {
            LastResize = size;
            Size = size;
            return Task.CompletedTask;
        }

        public Task SendInterruptAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessExit(0, true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRenderer : ITerminalRenderer
    {
        private readonly List<byte> _written = new();

        public string Text
        {
            get
            {
                lock (_written)
                    return Encoding.UTF8.GetString(_written.ToArray());
            }
        }

        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            lock (_written)
                _written.AddRange(bytes.ToArray());
            return Task.CompletedTask;
        }

        public event EventHandler<ReadOnlyMemory<byte>>? InputReceived;
        public event EventHandler<TerminalSize>? ResizeRequested;

        public void RaiseInput(byte[] bytes) => InputReceived?.Invoke(this, bytes);

        public void RaiseResize(TerminalSize size) => ResizeRequested?.Invoke(this, size);
    }

    private sealed class FakeRecorder : IAsciiCastWriter
    {
        private readonly List<byte> _appended = new();

        public bool Disposed { get; private set; }

        public string Text
        {
            get
            {
                lock (_appended)
                    return Encoding.UTF8.GetString(_appended.ToArray());
            }
        }

        public Task AppendAsync(
            ReadOnlyMemory<byte> bytes,
            TimeSpan elapsed,
            CancellationToken cancellationToken
        )
        {
            lock (_appended)
                _appended.AddRange(bytes.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
