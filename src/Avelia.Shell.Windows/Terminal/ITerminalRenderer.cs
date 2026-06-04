using System;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// The visual half of the terminal — an xterm.js instance in a WebView2 in the
/// real app, a fake in tests. <see cref="TerminalBridge"/> drives output into it
/// and listens for the user's keystrokes and resizes. Keeping this an interface
/// lets the bridge's pump logic be tested without standing up WebView2.
/// </summary>
internal interface ITerminalRenderer
{
    /// <summary>Push a batch of terminal output bytes to the renderer (xterm <c>write()</c>).</summary>
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>User typed into the terminal — bytes destined for the child's stdin.</summary>
    event EventHandler<ReadOnlyMemory<byte>> InputReceived;

    /// <summary>The renderer's viewport changed size (xterm <c>onResize</c>).</summary>
    event EventHandler<TerminalSize> ResizeRequested;
}
