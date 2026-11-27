using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-26 (просьба владельца: бот, бот-продавец, ЭСФ, онлайн-ККМ, рассылки, 1С, «заполним базу», QR, электронный чек, сканер на складе — карточки Маркетплейса):
// действие карточки. «Экспорт в 1С» и «Загрузим вашу базу» открывают своё окно; остальным показываем «Где найти» (шаги из описания страницы).
// Часть класса MarketplaceView; сами карточки собирает MarketplaceFeatureCards.
public partial class MarketplaceView
{
    private void OpenLocalFeature(string id, Window? owner)
    {
        if (!MarketplaceFeatureCards.IsFeature(id))
            return;
        if (id == MarketplaceFeatureCards.OneCExport && OperationsSettingsView.CanOneCExport())
        {
            OneCExportWindow.Open(owner);
            return;
        }
        if (id == MarketplaceFeatureCards.FillBase)
        {
            FillBaseRequestWindow.Open(owner);
            return;
        }
        var (title, body) = MarketplaceFeatureCards.OpenInfo(id);
        PosAlertDialog.Show(owner, title, body, PosAlertKind.Info);
    }
}
