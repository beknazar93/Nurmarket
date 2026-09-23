using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// Встроенное окно NurCRM (nurcrm.kg) внутри кассы — открывается из бургер-меню.
/// Не связано с бизнес-логикой кассы: отдельное окно на базе WebView2, без доступа
/// к корзине/чекам/оплате.
///
/// Логин не подставляется автоматически: WebView2 хранит cookies/сессию в
/// постоянном профиле (%AppData%\NurMarketKassa\WebView2, см. <see cref="WebView2Host"/>),
/// поэтому кассир вводит логин и пароль на сайте один раз, а дальше сайт сам
/// узнаёт его при следующих открытиях — как в обычном браузере.
/// </summary>
public partial class CrmWebViewWindow : Window
{
    private const string HomeUrl = "https://nurcrm.kg";

    private CoreWebView2? _webView;

    public CrmWebViewWindow()
    {
        InitializeComponent();
        WebHost.WebViewReady += OnWebViewReady;
        WebHost.InitializationFailed += OnInitializationFailed;
        WebHost.Navigate(HomeUrl);
    }

    private void OnWebViewReady(CoreWebView2 webView)
    {
        _webView = webView;
        webView.NavigationCompleted += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            LoadingPanel.IsVisible = false;
        });
    }

    private void OnInitializationFailed(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadingPanel.IsVisible = false;
            ErrorPanel.IsVisible = true;
            ErrorDetailsText.Text = message;
        });
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_webView?.CanGoBack == true)
            _webView.GoBack();
    }

    private void Forward_Click(object? sender, RoutedEventArgs e)
    {
        if (_webView?.CanGoForward == true)
            _webView.GoForward();
    }

    private void Reload_Click(object? sender, RoutedEventArgs e) => _webView?.Reload();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(HomeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Failed to open nurcrm.kg in system browser: {ex}", "WARNING");
        }
    }
}
