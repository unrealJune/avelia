using System;

namespace Avelia.Shell.Windows.Services;

/// <summary>
/// Tiny abstraction over WinUI's <c>DispatcherQueue</c>. Lets view-models
/// marshal subscription callbacks to the UI thread without referencing
/// <c>Microsoft.UI.Dispatching</c> directly — which would prevent the VM file
/// from link-compiling into the net10.0 test project.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Post an action for execution on the UI thread. Implementations may run
    /// the action synchronously if the caller is already on the UI thread, or
    /// queue it otherwise.
    /// </summary>
    void Post(Action action);
}

/// <summary>
/// Synchronous in-process dispatcher used by unit tests. Runs the action
/// immediately on the caller's thread; serializing through a real
/// <c>DispatcherQueue</c> would require a WinUI host.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
