using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Shell-side <see cref="ITerminalSessionFactory"/> backing the platform-agnostic
/// agent drivers: spawns the requested command in a ConPTY pseudo-terminal. This
/// is the seam that lets the F# Copilot driver host the <c>copilot</c> CLI in a
/// terminal without referencing this Windows-only P/Invoke.
/// </summary>
internal sealed class ConPtyTerminalSessionFactory : ITerminalSessionFactory
{
    public Task<OperationResult<ITerminalSession>> StartAsync(
        string commandLine,
        TerminalSize size,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // CreateProcessW does no PATH/PATHEXT lookup, so a bare command name
            // (e.g. "copilot", really copilot.cmd on PATH) fails with "CreateProcess
            // failed for: copilot". Resolve it to an absolute path up front; a raw
            // command line that already names a path is left untouched.
            var resolved = ResolveCommandLine(commandLine);
            if (resolved is null)
            {
                var command = FirstToken(commandLine);
                return Task.FromResult(
                    OperationResult<ITerminalSession>.NewFailure(
                        AveliaError.NewExternal("conpty", $"'{command}' was not found on PATH.")
                    )
                );
            }

            var workingDir = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;
            ITerminalSession session = ConPtySession.Start(resolved, size, workingDir);
            return Task.FromResult(OperationResult<ITerminalSession>.NewSuccess(session));
        }
        catch (Exception ex)
        {
            // Pseudo-console / process creation failed (bad command, missing
            // binary, ConPTY unavailable). Surface as an External error the
            // driver renders rather than crashing the app.
            return Task.FromResult(
                OperationResult<ITerminalSession>.NewFailure(
                    AveliaError.NewExternal("conpty", ex.Message)
                )
            );
        }
    }

    /// <summary>
    /// Resolve <paramref name="commandLine"/> to something <c>CreateProcessW</c>
    /// can actually launch. A bare command name is resolved against
    /// <c>PATH</c> × <c>PATHEXT</c> (preferring an executable variant such as
    /// <c>copilot.cmd</c> over an extensionless Unix shim of the same name).
    /// Because <c>CreateProcessW</c> only loads PE images, a resolved (or
    /// explicitly named) <c>.cmd</c>/<c>.bat</c> shim is wrapped in
    /// <c>%ComSpec% /s /c</c> so the command processor runs it. Returns
    /// <c>null</c> when a bare name cannot be found on <c>PATH</c>.
    /// </summary>
    internal static string? ResolveCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return commandLine;
        }

        var trimmed = commandLine.TrimStart();

        // Split the program token from its arguments, honoring a leading quote.
        string command;
        string rest;
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote < 0)
            {
                return commandLine; // malformed quoting — leave it to CreateProcessW
            }
            command = trimmed[1..endQuote];
            rest = trimmed[(endQuote + 1)..];
        }
        else
        {
            var spaceIdx = trimmed.IndexOf(' ');
            command = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
            rest = spaceIdx < 0 ? string.Empty : trimmed[spaceIdx..];
        }

        // A bare command (no directory separator) is resolved against PATH; an
        // explicit path is trusted as-is. Either way a .cmd/.bat target is then
        // routed through the command processor.
        var hasSeparator = command.Contains('\\') || command.Contains('/');
        string program;
        if (hasSeparator)
        {
            program = command;
        }
        else
        {
            var resolved = ResolveOnPath(command);
            if (resolved is null)
            {
                return null;
            }
            program = resolved;
        }

        return WrapForExec(program, rest);
    }

    /// <summary>
    /// Quote <paramref name="program"/> ahead of <paramref name="rest"/>,
    /// routing <c>.cmd</c>/<c>.bat</c> shims through the command processor since
    /// <c>CreateProcessW</c> cannot execute batch files directly.
    /// </summary>
    private static string WrapForExec(string program, string rest)
    {
        var ext = Path.GetExtension(program);
        if (
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
        )
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec))
            {
                comspec = "cmd.exe";
            }
            // cmd /s /c "<everything>": with /s, cmd strips the outermost quote
            // pair and runs the remainder verbatim, so the quoted shim path and
            // its args survive intact.
            return $"\"{comspec}\" /s /c \"\"{program}\"{rest}\"";
        }

        return $"\"{program}\"{rest}";
    }

    private static string FirstToken(string commandLine)
    {
        var trimmed = (commandLine ?? string.Empty).TrimStart();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote < 0 ? trimmed : trimmed[1..endQuote];
        }
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
    }

    private static string? ResolveOnPath(string command)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var dirs = pathVar.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        var hasExtension = Path.HasExtension(command);
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var exts = pathExt.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var dir in dirs)
        {
            if (hasExtension)
            {
                // Caller named the extension (e.g. "copilot.cmd") — exact match.
                var direct = Path.Combine(dir, command);
                if (File.Exists(direct))
                {
                    return direct;
                }
            }
            else
            {
                // Prefer an executable PATHEXT variant (copilot.exe / copilot.cmd)
                // over a bare extensionless file of the same name, which on
                // Windows is typically a Unix shell script CreateProcessW can't
                // launch.
                foreach (var ext in exts)
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
