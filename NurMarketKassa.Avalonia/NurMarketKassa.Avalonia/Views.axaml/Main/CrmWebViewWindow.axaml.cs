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
public partial class CrmWebViewWindow : Window, IOwnerSection
{
    // 2026-10-31 (живой обход, В-14): раздел «NurCRM» открывал публичный лендинг nurcrm.kg («Логин», «Оставить заявку») — владелец уже вошёл, ему нужен кабинет app.nurcrm.kg.
    private const string NurCrmUrl = "https://app.nurcrm.kg/";

    /// <summary>2026-10-05, владелец: «к десктопу добавь … WhatsApp Web» — то же окно со встроенным браузером
    /// для web.whatsapp.com (вход по QR-коду один раз — профиль WebView2 постоянный).</summary>
    public const string WhatsAppWebUrl = "https://web.whatsapp.com";

    private readonly string _homeUrl;
    private CoreWebView2? _webView;

    public CrmWebViewWindow() : this(NurCrmUrl, "NurCRM — app.nurcrm.kg")
    {
    }

    public CrmWebViewWindow(string homeUrl, string caption)
    {
        _homeUrl = homeUrl;
        InitializeComponent();
        if (!string.Equals(homeUrl, NurCrmUrl, StringComparison.OrdinalIgnoreCase))
        {
            Title = caption;
            AddressLabel.Text = caption;
        }
        ApplyPurpose();
        WebHost.WebViewReady += OnWebViewReady;
        WebHost.InitializationFailed += OnInitializationFailed;
        WebHost.Navigate(homeUrl);
    }

    private bool IsWhatsApp => _homeUrl.StartsWith(WhatsAppWebUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>2026-10-21 (пакет 4, п. 130; аудит: «нет пояснения, зачем раздел»): у WhatsApp Web — строка под кнопками, зачем
    /// он в программе и как войти; «Открыть в браузере» и тексты загрузки/ошибки — про WhatsApp, а не про nurcrm.kg.</summary>
    private void ApplyPurpose()
    {
        var browser = Tr.T("Открыть в браузере", "Браузерде ачуу", "Open in browser", "Tarayıcıda aç", "Brauzerda ochish");
        OpenInBrowserButton.Content = browser;
        Avalonia.Automation.AutomationProperties.SetName(OpenInBrowserButton, browser);
        if (!IsWhatsApp)
            return;
        PurposeText.IsVisible = true;
        PurposeText.Text = Tr.T(
            "WhatsApp магазина прямо в программе — переписка с покупателями и напоминания должникам. Если ниже QR-код — войдите один раз: на телефоне WhatsApp → «Связанные устройства» → «Привязка устройства» и наведите камеру на код.",
            "Дүкөндүн WhatsApp'ы түз программада — кардарлар менен кат алышуу жана карыздарга эскертүү. Төмөндө QR-код болсо — бир жолу кириңиз: телефондо WhatsApp → «Байланышкан түзмөктөр» → «Түзмөктү байланыштыруу» жана камераны кодго буруңуз.",
            "The shop's WhatsApp right in the program — chat with customers and remind debtors. If you see a QR code below, sign in once: on your phone open WhatsApp → “Linked devices” → “Link a device” and point the camera at the code.",
            "Mağazanın WhatsApp'ı doğrudan programda — müşterilerle yazışma ve borçlulara hatırlatma. Aşağıda QR kod varsa bir kez giriş yapın: telefonda WhatsApp → «Bağlı cihazlar» → «Cihaz bağla» ve kamerayı koda tutun.",
            "Do'konning WhatsApp'i to'g'ridan-to'g'ri dasturda — xaridorlar bilan yozishma va qarzdorlarga eslatma. Quyida QR-kod bo'lsa — bir marta kiring: telefonda WhatsApp → «Bog'langan qurilmalar» → «Qurilmani bog'lash» va kamerani kodga qarating.");
        LoadingText.Text = Tr.T("Загрузка WhatsApp Web…", "WhatsApp Web жүктөлүүдө…", "Loading WhatsApp Web…", "WhatsApp Web yükleniyor…", "WhatsApp Web yuklanmoqda…");
        ErrorOpenButton.Content = Tr.T("Открыть WhatsApp Web в браузере", "WhatsApp Web'ди браузерде ачуу", "Open WhatsApp Web in the browser",
            "WhatsApp Web'i tarayıcıda aç", "WhatsApp Web'ni brauzerda ochish");
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

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): надпись «NurCRM —
    /// nurcrm.kg» повторяла название раздела «NurCRM», а ✕ закрывала бы раздел. Остаются «назад»,
    /// «вперёд» и «обновить страницу» — без них сайтом внутри программы не пользоваться.</summary>
    public void AsOwnerSection()
    {
        AddressLabel.IsVisible = false;
        CloseButton.IsVisible = false;
        ToolbarBorder.Padding = new Avalonia.Thickness(12, 6);
    }

    private void OpenInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_webView?.Source is { Length: > 0 } current ? current : _homeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Failed to open nurcrm.kg in system browser: {ex}", "WARNING");
        }
    }
}
