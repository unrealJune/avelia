using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avelia.Core.Abstractions;
using Avelia.Shell.Windows.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Avelia.Shell.Windows.Controls;

/// <summary>
/// xterm.js terminal renderer hosted in a WebView2. Implements
/// <see cref="ITerminalRenderer"/> so <see cref="TerminalBridge"/> can drive a
/// live ConPTY session into it without any WebView2 knowledge.
///
/// Output is posted to the page as a base64 web message (decoded and handed to
/// <c>xterm.write()</c>); input and resize travel back as small JSON web
/// messages. The plan's SharedArrayBuffer fast path is a contained follow-up —
/// it would replace only <see cref="WriteAsync"/> and the page's message
/// handler, leaving the bridge untouched (see docs/plans/backend.md, B-7).
/// </summary>
public sealed partial class TerminalView : UserControl, ITerminalRenderer
{
    private const string VirtualHost = "avelia.terminal";

    private CoreWebView2? _core;
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Completes once the WebView2 + xterm.js page is ready to render.</summary>
    public Task Ready => _ready.Task;

    public event EventHandler<ReadOnlyMemory<byte>>? InputReceived;
    public event EventHandler<TerminalSize>? ResizeRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
            _core = Web.CoreWebView2;

            // Serve the packaged terminal assets from a virtual https origin so
            // xterm.js loads under a normal web security context.
            string assetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            _core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                assetRoot,
                CoreWebView2HostResourceAccessKind.Allow
            );
            _core.WebMessageReceived += OnWebMessageReceived;

            Web.Source = new Uri($"https://{VirtualHost}/terminal.html");
            _core.NavigationCompleted += OnNavigationCompleted;
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private void OnNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        if (args.IsSuccess)
            _ready.TrySetResult();
        else
            _ready.TrySetException(new InvalidOperationException("terminal.html failed to load."));
    }

    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        string payload = Convert.ToBase64String(bytes.Span);
        var tcs = new TaskCompletionSource();

        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Output messages are the raw base64 string; the page decodes
                // and writes them. Input/resize use JSON in the other direction.
                _core?.PostWebMessageAsString(payload);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!queued)
            tcs.TrySetResult(); // UI gone (teardown) — drop the frame, don't hang the pump

        return tcs.Task;
    }

    private void OnWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args
    )
    {
        string json;
        try
        {
            json = args.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string kind = root.GetProperty("t").GetString() ?? "";

            switch (kind)
            {
                case "i":
                    string data = root.GetProperty("d").GetString() ?? "";
                    InputReceived?.Invoke(this, Convert.FromBase64String(data));
                    break;
                case "r":
                    int cols = root.GetProperty("c").GetInt32();
                    int rows = root.GetProperty("r").GetInt32();
                    ResizeRequested?.Invoke(this, new TerminalSize(cols, rows));
                    break;
            }
        }
        catch
        {
            // Malformed message from the page — ignore rather than crash the UI.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationCompleted -= OnNavigationCompleted;
        }
    }
}
