using System;

namespace Avelia.Shell.Windows.Services;

/// <summary>
/// Tiny abstraction over OS toast notifications. Kept interface-only and pure
/// .NET — no <c>Microsoft.UI.*</c> / <c>Microsoft.Windows.*</c> references — so
/// the view-models that raise notifications still link-compile into the
/// net10.0 test project (same constraint that drives <see cref="IUiDispatcher"/>).
/// The production implementation lives in
/// <c>WindowsAppNotificationService</c>.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Raise an OS notification announcing that the agent finished a
    /// conversation turn. Implementations decide whether to suppress the
    /// notification (e.g. when the app is already in the foreground).
    /// </summary>
    /// <param name="conversationTitle">
    /// Title of the conversation whose turn completed, shown as the
    /// notification heading. May be empty.
    /// </param>
    void NotifyTurnCompleted(string conversationTitle);
}

/// <summary>
/// No-op <see cref="INotificationService"/>. Used by unit tests and design-time
/// / stub hosts where surfacing real OS toasts is undesirable.
/// </summary>
public sealed class NullNotificationService : INotificationService
{
    public void NotifyTurnCompleted(string conversationTitle) { }
}
