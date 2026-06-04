using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;
using Microsoft.Win32.SafeHandles;
using static Avelia.Shell.Windows.Terminal.ConPtyNative;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Windows realization of the F# <see cref="ITerminalSession"/> contract over
/// ConPTY. Hosts a child process attached to a pseudo-console: bytes written go
/// to the child's stdin, the combined stdout/stderr stream back out. No
/// knowledge of xterm.js, WebView2, or the renderer — the shell's terminal view
/// (chunk B-7) wires this to the UI; here we only move bytes.
///
/// Threading: the type is safe to call from any thread. Output is delivered via
/// a single-consumer <see cref="ReadAllAsync"/>; the child's exit is observed
/// once on a thread-pool wait and cached in <see cref="WaitForExitAsync"/>.
/// </summary>
internal sealed class ConPtySession : ITerminalSession
{
    // 0x03 = ETX. ConPTY translates a lone ETX on the input pipe into a
    // CTRL_C_EVENT for the child's process group. See SendInterruptAsync.
    private static readonly byte[] InterruptSequence = { 0x03 };

    private readonly IntPtr _hPC;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly TaskCompletionSource<ProcessExit> _exitTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private IntPtr _hProcess;
    private readonly ManualResetEvent _processWaitHandle;
    private readonly RegisteredWaitHandle _registeredWait;

    private TerminalSize _size;
    private int _readerStarted;
    private int _forciblyTerminated;
    private int _pcClosed;
    private int _disposed;

    private ConPtySession(
        IntPtr hPC,
        FileStream input,
        FileStream output,
        IntPtr hProcess,
        TerminalSize size
    )
    {
        _hPC = hPC;
        _input = input;
        _output = output;
        _hProcess = hProcess;
        _size = size;

        // Wait on the process handle without owning it (we close it ourselves
        // in DisposeAsync). When it signals, record the exit and release the
        // pseudo console so the reader drains to EOF.
        _processWaitHandle = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(hProcess, ownsHandle: false),
        };
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _processWaitHandle,
            OnProcessExited,
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true
        );
    }

    public TerminalSize Size => _size;

    /// <summary>
    /// Launch <paramref name="commandLine"/> attached to a fresh pseudo-console
    /// sized <paramref name="size"/>. Throws <see cref="Win32Exception"/> if any
    /// of the pipe / pseudo-console / process-creation steps fail.
    /// </summary>
    public static ConPtySession Start(
        string commandLine,
        TerminalSize size,
        string? workingDirectory = null
    )
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new ArgumentException("Command line must be non-empty.", nameof(commandLine));

        // Input pipe: we keep the (overlapped) write end; ConPTY reads the other.
        var (inputWrite, inputRead) = CreateOverlappedPipe(serverReads: false);
        // Output pipe: we keep the (overlapped) read end; ConPTY writes the other.
        var (outputRead, outputWrite) = CreateOverlappedPipe(serverReads: true);

        IntPtr hPC = IntPtr.Zero;
        try
        {
            var coord = new COORD { X = (short)size.Cols, Y = (short)size.Rows };
            int hr = CreatePseudoConsole(coord, inputRead, outputWrite, dwFlags: 0, out hPC);
            if (hr != 0)
                throw new Win32Exception(hr, "CreatePseudoConsole failed");

            // ConPTY duplicated the handles it needs; release our copies of the
            // ends we handed it so the child sees EOF / SIGHUP correctly later.
            inputRead.Dispose();
            outputWrite.Dispose();

            IntPtr hProcess = StartProcess(commandLine, hPC, workingDirectory);

            var input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 1, isAsync: true);
            var output = new FileStream(
                outputRead,
                FileAccess.Read,
                bufferSize: PIPE_BUFFER_SIZE,
                isAsync: true
            );
            return new ConPtySession(hPC, input, output, hProcess, size);
        }
        catch
        {
            // Best-effort cleanup on the failure path; the happy path transfers
            // ownership of these handles to the FileStreams / the instance.
            if (hPC != IntPtr.Zero)
                ClosePseudoConsole(hPC);
            inputWrite.Dispose();
            if (!inputRead.IsClosed)
                inputRead.Dispose();
            outputRead.Dispose();
            if (!outputWrite.IsClosed)
                outputWrite.Dispose();
            throw;
        }
    }

    private static IntPtr StartProcess(string commandLine, IntPtr hPC, string? workingDirectory)
    {
        IntPtr attrList = IntPtr.Zero;
        try
        {
            // Two-call idiom: first call sizes the attribute list.
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attrList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed"
                );

            // lpValue IS the HPCON value (a pointer), not a pointer to it.
            if (
                !UpdateProcThreadAttribute(
                    attrList,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero
                )
            )
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "UpdateProcThreadAttribute failed"
                );

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            bool ok = CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref si,
                lpProcessInformation: out PROCESS_INFORMATION pi
            );
            if (!ok)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"CreateProcess failed for: {commandLine}"
                );

            // We don't track the primary thread; the process handle is enough.
            CloseHandle(pi.hThread);
            return pi.hProcess;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a lone <c>0x03</c> to the input pipe — the seam both
    /// <see cref="SendInterruptAsync"/> and the fast-tier round-trip test drive,
    /// so the test exercises the exact bytes the child will receive.
    /// </summary>
    internal static async Task WriteInterruptAsync(
        Stream input,
        CancellationToken cancellationToken
    )
    {
        await input
            .WriteAsync(InterruptSequence.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SendInterruptAsync(CancellationToken cancellationToken) =>
        WriteInterruptAsync(_input, cancellationToken);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken)
    {
        // Guard eagerly (on call, not on first MoveNextAsync) so a second
        // consumer fails fast at the call site per the contract.
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0)
            throw new InvalidOperationException(
                "ReadAllAsync may be consumed only once per session."
            );

        return ReadCore(cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadCore(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var buffer = new byte[PIPE_BUFFER_SIZE];
        while (true)
        {
            int read;
            try
            {
                read = await _output
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Pipe broken / stream disposed during teardown — normal end.
                yield break;
            }

            if (read == 0)
                yield break; // EOF: ConPTY output handle closed after child exit.

            var chunk = new byte[read];
            Array.Copy(buffer, chunk, read);
            yield return chunk;
        }
    }

    public Task ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _pcClosed) != 0)
            throw new ObjectDisposedException(nameof(ConPtySession));

        var coord = new COORD { X = (short)size.Cols, Y = (short)size.Rows };
        int hr = ResizePseudoConsole(_hPC, coord);
        if (hr != 0)
            throw new Win32Exception(hr, "ResizePseudoConsole failed");

        _size = size;
        return Task.CompletedTask;
    }

    public Task<ProcessExit> WaitForExitAsync(CancellationToken cancellationToken) =>
        _exitTcs.Task.WaitAsync(cancellationToken);

    private void OnProcessExited(object? state, bool timedOut)
    {
        uint code = 1;
        try
        {
            GetExitCodeProcess(_hProcess, out code);
        }
        catch
        {
            // Handle already gone; fall back to the default non-zero code.
        }

        bool clean = Volatile.Read(ref _forciblyTerminated) == 0;
        _exitTcs.TrySetResult(new ProcessExit((int)code, clean));

        // Release the pseudo console so the reader drains buffered output and
        // then observes EOF.
        ClosePseudoConsoleOnce();
    }

    private void ClosePseudoConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pcClosed, 1) == 0)
            ClosePseudoConsole(_hPC);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // A child still running at disposal is a forced teardown — mark it so
        // the exit callback reports ProcessExit.IsClean = false.
        if (!_exitTcs.Task.IsCompleted)
        {
            Volatile.Write(ref _forciblyTerminated, 1);
            try
            {
                TerminateProcess(_hProcess, 1);
            }
            catch
            {
                // Already exiting; the wait callback will record the real code.
            }
        }

        // Break the pipes first so ClosePseudoConsole can't block waiting for an
        // absent reader to drain output.
        try
        {
            _output.Dispose();
        }
        catch { }
        try
        {
            _input.Dispose();
        }
        catch { }

        ClosePseudoConsoleOnce();

        // Give the exit callback a bounded window to record ProcessExit.
        try
        {
            await _exitTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Timed out — proceed with teardown regardless.
            _exitTcs.TrySetResult(new ProcessExit(1, false));
        }

        _registeredWait.Unregister(null);
        _processWaitHandle.Dispose();

        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }
}
