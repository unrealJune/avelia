using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Avelia.Shell.Windows.Terminal;

/// <summary>
/// Warms the WebView2 runtime once at app start (splash screen) so the first
/// terminal open isn't gated on cold WebView2 initialization. Creating the
/// default environment ahead of time primes the runtime that every
/// <see cref="Controls.TerminalView"/> later reuses.
/// </summary>
public static class WebView2Warmer
{
    private static Task<CoreWebView2Environment>? _warm;
    private static readonly object Gate = new();

    /// <summary>
    /// Begin (or join) warming. Idempotent — repeated calls share one
    /// environment task. Safe to fire-and-forget from app startup.
    /// </summary>
    public static Task WarmAsync()
    {
        lock (Gate)
        {
            _warm ??= CoreWebView2Environment.CreateAsync().AsTask();
            return _warm;
        }
    }
}
