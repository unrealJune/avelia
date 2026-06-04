using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Win32 P/Invoke surface for the ConPTY pseudo-console API plus the bits of
/// pipe / process plumbing <see cref="ConPtySession"/> needs. Deliberately free
/// of any WinUI / <c>Microsoft.UI.Xaml</c> reference: the F# core owns the
/// <c>ITerminalSession</c> contract and this file is the Windows realization of
/// it, so it can be link-compiled into the (non-WinUI) test assembly the same
/// way the platform-independent view-models already are.
///
/// References: the canonical Microsoft "GUEzh" ConPTY sample
/// (CreatePseudoConsole + STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE).
/// We own ~300 lines here rather than pin a dormant dependency (Pty.Net's last
/// release predates .NET 8) — see docs/plans/backend.md, chunk B-6.
/// </summary>
internal static class ConPtyNative
{
    // ---- Pseudo console ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC
    );

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    // ---- Pipes -------------------------------------------------------------
    //
    // Anonymous pipes (CreatePipe) cannot be opened in overlapped mode, so a
    // FileStream over them can never honor a CancellationToken on a pending
    // read — a hard requirement in this codebase (CLAUDE.md rule 6). We mint a
    // uniquely-named pipe via CreateNamedPipe with FILE_FLAG_OVERLAPPED for the
    // end we keep, and connect the other end with CreateFile. The kept end is
    // wrapped in an async FileStream; the handed-off end is given to ConPTY.

    internal const uint PIPE_ACCESS_INBOUND = 0x00000001;
    internal const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const uint PIPE_TYPE_BYTE = 0x00000000;
    internal const uint PIPE_WAIT = 0x00000000;
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint OPEN_EXISTING = 3;
    internal const int PIPE_BUFFER_SIZE = 64 * 1024;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    /// <summary>
    /// Create a unidirectional byte pipe. <paramref name="serverReads"/> picks
    /// the direction of the <em>kept</em> (server) end: <c>true</c> gives us an
    /// overlapped read handle (for ConPTY output we consume); <c>false</c> an
    /// overlapped write handle (for input we feed the child). The returned
    /// <c>client</c> end is the non-overlapped counterpart handed to ConPTY.
    /// </summary>
    internal static (SafeFileHandle server, SafeFileHandle client) CreateOverlappedPipe(
        bool serverReads
    )
    {
        // A GUID name makes the pipe effectively unguessable and collision-free
        // across concurrent sessions in the same process.
        string name = $@"\\.\pipe\avelia-conpty-{Guid.NewGuid():N}";

        uint serverAccess = serverReads ? PIPE_ACCESS_INBOUND : PIPE_ACCESS_OUTBOUND;
        SafeFileHandle server = CreateNamedPipeW(
            name,
            serverAccess | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_WAIT,
            nMaxInstances: 1,
            nOutBufferSize: PIPE_BUFFER_SIZE,
            nInBufferSize: PIPE_BUFFER_SIZE,
            nDefaultTimeOut: 0,
            lpSecurityAttributes: IntPtr.Zero
        );
        if (server.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipe failed");

        // The client end's access mirrors the server: if we read, ConPTY writes.
        uint clientAccess = serverReads ? GENERIC_WRITE : GENERIC_READ;
        SafeFileHandle client = CreateFileW(
            name,
            clientAccess,
            dwShareMode: 0,
            lpSecurityAttributes: IntPtr.Zero,
            OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            hTemplateFile: IntPtr.Zero
        );
        if (client.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            server.Dispose();
            throw new Win32Exception(err, "CreateFile (pipe connect) failed");
        }

        return (server, client);
    }

    // ---- Process creation with a pseudo-console attached -------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    internal const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbValue,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation
    );

    // ---- Process exit / termination ---------------------------------------

    internal const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
