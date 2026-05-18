using System.Runtime.InteropServices;

namespace Avelia.Shell.Windows.Helpers;

/// <summary>
/// Boilerplate from the Windows App SDK samples: certain SystemBackdrops controllers
/// (MicaController, DesktopAcrylicController) require a DispatcherQueueController on
/// the calling thread. The high-level <c>MicaBackdrop</c> XAML element doesn't strictly
/// need this — WinUI's window machinery creates one — but invoking the helper is
/// defensive and harmless when one already exists.
/// </summary>
internal sealed class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController
    );

    private object? _dispatcherQueueController;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() != null)
        {
            // One already exists for this thread — nothing to do.
            return;
        }

        if (_dispatcherQueueController is null)
        {
            DispatcherQueueOptions options;
            options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
            options.threadType = 2; // DQTYPE_THREAD_CURRENT
            options.apartmentType = 2; // DQTAT_COM_STA

            _ = CreateDispatcherQueueController(options, out _dispatcherQueueController);
        }
    }
}
