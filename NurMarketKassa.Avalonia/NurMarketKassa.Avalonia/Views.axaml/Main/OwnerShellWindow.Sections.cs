using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «к десктопу добавь воронку и WhatsApp Web» — разделы программы владельца.</summary>
public partial class OwnerShellWindow
{
    /// <summary>WhatsApp: в Windows — WhatsApp Web во встроенном браузере (вход по QR-коду один раз); на Android и
    /// Linux встроенного браузера нет — открывается приложение WhatsApp (или сайт в браузере).</summary>
    private async void OpenWhatsApp()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(OperatingSystem.IsAndroid() ? "https://wa.me/" : CrmWebViewWindow.WhatsAppWebUrl)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: WhatsApp не открылся ({ex.Message}).", "WARNING");
            }
            return;
        }
        OpenSection("whatsapp", () => new CrmWebViewWindow(CrmWebViewWindow.WhatsAppWebUrl, "WhatsApp Web"));
    }
}
