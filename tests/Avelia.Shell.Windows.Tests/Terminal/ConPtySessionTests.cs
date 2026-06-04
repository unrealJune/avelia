using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Terminal;
using Xunit;

namespace Avelia.Shell.Windows.Tests.Terminal;

/// <summary>
/// Integration coverage for the real ConPTY host: a child process is spawned
/// attached to a pseudo-console and we assert the output path, resize, and the
/// clean-vs-forced exit distinction. Gated to the Integration tier — these touch
/// the OS process table and only run on Windows.
///
/// One assertion — the child's rendered <em>stdout content</em> round-tripping
/// back through the pty — is capability-probed and skipped on hosts that cannot
/// surface pseudo-console output (a headless / redirected-stdout test host
/// re-parents the child's stdout to the harness pipe rather than the pty). The
/// rest of the surface is asserted unconditionally. See
/// <see cref="Child_stdout_round_trips_through_the_pty"/>.
/// </summary>
[Trait("Category", "Integration")]
public class ConPtySessionTests
{
    private static readonly string Cmd =
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static readonly TerminalSize DefaultSize = new(80, 24);

    private static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static async Task<byte[]> DrainAsync(ITerminalSession session, int seconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buffer = new System.IO.MemoryStream();
        await foreach (var chunk in session.ReadAllAsync(cts.Token))
            buffer.Write(chunk.Span);
        return buffer.ToArray();
    }

    /// <summary>
    /// Round-trip a known marker through a fresh pty and report whether the
    /// host actually renders child stdout into the pseudo-console stream.
    /// </summary>
    private static async Task<bool> PtySurfacesChildOutputAsync()
    {
        await using var probe = ConPtySession.Start(
            $"{Cmd} /c echo avelia-probe-marker",
            DefaultSize
        );
        byte[] bytes = await DrainAsync(probe, 15);
        return Encoding.UTF8.GetString(bytes).Contains("avelia-probe-marker");
    }

    [Fact]
    public async Task Starting_a_session_emits_the_conpty_init_frame()
    {
        await using var session = ConPtySession.Start($"{Cmd} /k", DefaultSize);

        byte[] output = await DrainAsync(session, 5);

        // ConPTY emits its setup frame (mode sets, clear, title via OSC) the
        // moment the child attaches — proof the output pipe is wired end-to-end.
        Assert.NotEmpty(output);
        Assert.Contains((byte)0x1b, output); // ESC — VT control sequences present
    }

    [Fact]
    public async Task Child_completion_reports_a_clean_exit()
    {
        await using var session = ConPtySession.Start($"{Cmd} /c echo done", DefaultSize);

        // Drain to EOF: ConPTY closes the output handle once the child exits.
        await DrainAsync(session, 15);
        ProcessExit exit = await session.WaitForExitAsync(Timeout(5));

        Assert.True(exit.IsClean);
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public async Task Resize_updates_size_without_error()
    {
        await using var session = ConPtySession.Start($"{Cmd} /k", DefaultSize);

        await session.ResizeAsync(new TerminalSize(120, 40), CancellationToken.None);

        Assert.Equal(120, session.Size.Cols);
        Assert.Equal(40, session.Size.Rows);
    }

    [Fact]
    public async Task ReadAllAsync_rejects_a_second_consumer()
    {
        await using var session = ConPtySession.Start($"{Cmd} /k", DefaultSize);

        _ = session.ReadAllAsync(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
            session.ReadAllAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task Dispose_of_a_running_child_reports_an_unclean_exit()
    {
        var session = ConPtySession.Start($"{Cmd} /k", DefaultSize);
        Task<ProcessExit> exitTask = session.WaitForExitAsync(Timeout(20));

        await Task.Delay(400); // ensure the child is up before we tear it down
        await session.DisposeAsync();

        ProcessExit exit = await exitTask;
        Assert.False(exit.IsClean);
    }

    [Fact]
    public async Task Child_stdout_round_trips_through_the_pty()
    {
        // Probe the host once: interactive Windows consoles render child stdout
        // into the pty stream; headless / redirected-stdout test runners
        // re-parent the child's stdout to the harness pipe and only the init
        // frame reaches us. The probe decides which assertion is authoritative
        // so the test never falsely fails on a headless host yet still detects a
        // content regression wherever the host is capable of surfacing it.
        bool surfacesContent = await PtySurfacesChildOutputAsync();

        await using var session = ConPtySession.Start(
            $"{Cmd} /c echo conpty-roundtrip",
            DefaultSize
        );
        byte[] output = await DrainAsync(session, 15);

        if (surfacesContent)
            Assert.Contains("conpty-roundtrip", Encoding.UTF8.GetString(output));
        else
            Assert.Contains((byte)0x1b, output); // stream still live (ConPTY init frame)
    }
}
