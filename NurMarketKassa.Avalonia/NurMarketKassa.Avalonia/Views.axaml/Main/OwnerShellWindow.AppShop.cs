using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, запрос команды NurCRM: «заметка для владельцев: „Ваш магазин в приложении NurCRM бесплатно“». Пока магазин
/// не показан в приложении NurCRM (app-shop-settings → show_in_app = false), над показателями «Сводки» — заметка с кнопкой
/// «Подключить» (раздел «Магазин в приложении»). Проверка не чаще раза в 10 минут.</summary>
public partial class OwnerShellWindow
{
    private Border? _appShopNote;
    private DateTime _appShopCheckedAt = DateTime.MinValue;

    private async Task RefreshAppShopNoteAsync()
    {
        if (DateTime.UtcNow - _appShopCheckedAt < TimeSpan.FromMinutes(10))
            return;
        _appShopCheckedAt = DateTime.UtcNow;
        try
        {
            var data = await App.GetRequiredService<NurMarketApiClient>().RequestAsync(HttpMethod.Get, "api/main/app-shop-settings/", null, null,
                CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var shown = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("show_in_app", out var s) && s.ValueKind == JsonValueKind.True;
            ShowAppShopNote(!shown && !SectionVisibility.IsHidden("appshop"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: состояние «Магазина в приложении» не получено ({ex.Message}).", "WARNING");
        }
    }

    private void ShowAppShopNote(bool show)
    {
        if (!show)
        {
            if (_appShopNote is not null)
                _appShopNote.IsVisible = false;
            return;
        }
        if (_appShopNote is null && OverviewKpiGrid.Parent is StackPanel overview)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = "★ " + AppShopWindow.FreeNote, FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
            title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var hint = new TextBlock
            {
                Text = Tr.T("Покупатели найдут магазин на карте, увидят товары и будут копить баллы. Подключение — пара минут.",
                    "Кардарлар дүкөндү картадан таап, товарларды көрүп, упай топтошот. Кошуу — бир нече мүнөт.",
                    "Customers will find the shop on the map, see your products and earn points. Connecting takes a couple of minutes.",
                    "Müşteriler mağazayı haritada bulur, ürünleri görür ve puan biriktirir. Bağlanmak birkaç dakika sürer.",
                    "Xaridorlar do'konni xaritada topadi, mahsulotlarni ko'radi va ball yig'adi. Ulash — bir-ikki daqiqa."),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            };
            hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            texts.Children.Add(title);
            texts.Children.Add(hint);
            row.Children.Add(texts);
            var connect = UiKit.Primary(this, Tr.T("Подключить", "Кошуу", "Connect", "Bağla", "Ulash"));
            connect.Margin = new Thickness(12, 0, 0, 0);
            connect.VerticalAlignment = VerticalAlignment.Center;
            connect.Click += (_, _) => OpenSection("appshop", () => new AppShopWindow());
            Grid.SetColumn(connect, 1);
            row.Children.Add(connect);
            _appShopNote = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), BorderThickness = new Thickness(1), Child = row };
            _appShopNote.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccentSoft"));
            _appShopNote.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushAccentStrong"));
            overview.Children.Insert(0, _appShopNote);
        }
        if (_appShopNote is not null)
            _appShopNote.IsVisible = true;
    }
}
