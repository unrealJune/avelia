using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Avelia.Shell.Windows.Services;

/// <summary>
/// Production <see cref="INotificationService"/> built on the Windows App SDK
/// <see cref="AppNotificationManager"/>. Requires the manager to have been
/// registered at startup (see <c>App.OnLaunched</c>).
///
/// References <c>Microsoft.Windows.AppNotifications</c>, so — unlike
/// <see cref="INotificationService"/> — this type does not link-compile into
/// the net10.0 test project. The interface keeps the view-models testable.
/// </summary>
public sealed class WindowsAppNotificationService : INotificationService
{
    private readonly Func<bool> _isAppInForeground;

    /// <param name="isAppInForeground">
    /// Reports whether the app's window is currently active/foreground.
    /// Notifications are suppressed when it returns <c>true</c> so the user
    /// isn't interrupted while already watching the conversation.
    /// </param>
    public WindowsAppNotificationService(Func<bool> isAppInForeground)
    {
        _isAppInForeground = isAppInForeground;
    }

    public void NotifyTurnCompleted(string conversationTitle)
    {
        // Don't interrupt the user when they're already looking at the app.
        if (_isAppInForeground())
        {
            return;
        }

        var heading = string.IsNullOrWhiteSpace(conversationTitle) ? "Avelia" : conversationTitle;

        var notification = new AppNotificationBuilder()
            .AddText(heading)
            .AddText("The agent finished this turn.")
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
