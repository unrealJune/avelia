using System;
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
            var workingDir = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;
            ITerminalSession session = ConPtySession.Start(commandLine, size, workingDir);
            return Task.FromResult(OperationResult<ITerminalSession>.NewSuccess(session));
        }
        catch (Exception ex)
        {
            // Pseudo-console / process creation failed (bad command, missing
            // binary, ConPTY unavailable). Surface as an External error the
            // driver renders rather than crashing the app.
            return Task.FromResult(
                OperationResult<ITerminalSession>.NewFailure(AveliaError.NewExternal("conpty", ex.Message))
            );
        }
    }
}
