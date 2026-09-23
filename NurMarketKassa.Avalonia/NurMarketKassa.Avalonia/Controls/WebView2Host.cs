using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Controls;

/// <summary>
/// Embeds a native WebView2 (Chromium/Edge) surface into the Avalonia visual tree
/// via <see cref="NativeControlHost"/>. WebView2 is a Win32 child window, not an
/// Avalonia control, so it cannot be hosted any other way on this platform.
/// </summary>
public sealed class WebView2Host : NativeControlHost
{
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _environment;
    private string? _pendingUrl;

    public event Action<CoreWebView2>? WebViewReady;
    public event Action<string>? InitializationFailed;

    public void Navigate(string url)
    {
        if (_controller?.CoreWebView2 is { } webView)
            webView.Navigate(url);
        else
            _pendingUrl = url;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _ = InitializeAsync(handle.Handle);
        return handle;
    }

    private async System.Threading.Tasks.Task InitializeAsync(IntPtr hwnd)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NurMarketKassa", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder)
                .ConfigureAwait(true);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)Bounds.Width, (int)Bounds.Height);
            _controller.IsVisible = true;

            if (!string.IsNullOrWhiteSpace(_pendingUrl))
                _controller.CoreWebView2.Navigate(_pendingUrl);

            WebViewReady?.Invoke(_controller.CoreWebView2);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WebView2 init failed: {ex}", "ERROR");
            InitializationFailed?.Invoke(ex.Message);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_controller is not null)
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)Bounds.Width, (int)Bounds.Height);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            _controller?.Close();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WebView2 dispose failed: {ex}", "WARNING");
        }
        _controller = null;
        base.DestroyNativeControlCore(control);
    }
}
