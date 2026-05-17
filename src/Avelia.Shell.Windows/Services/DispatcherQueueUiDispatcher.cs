using System;
using Microsoft.UI.Dispatching;

namespace Avelia.Shell.Windows.Services;

/// <summary>
/// Production <see cref="IUiDispatcher"/> built on a captured WinUI
/// <see cref="DispatcherQueue"/>. Constructed at window creation when the
/// dispatcher is reachable; cached for the lifetime of the view-model.
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue;
    }

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }
        _queue.TryEnqueue(() => action());
    }
}
