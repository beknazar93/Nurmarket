using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System;

// 2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч», возврат от клиента): у выбранного чека — его прошлые возвраты (refunds[] карточки чека)
// с кнопками «Удалить» / «История» и плашкой «АННУЛИРОВАН» из общих ReturnEditFlows. Всё только при признаке сервера pos_return_edit_24h и разрешении
// в самом документе (can_void / can_edit); без признака панель пустая и скрытая — касса ведёт себя как раньше. Окна правки возврата от клиента пока нет (edit: null).
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private StackPanel? _returnDocsPanel;

    private void UpdateReturnDocs(JsonElement sale)
    {
        if (!ReturnEditGate.FeatureOn(ReturnDocKind.Customer))
        {
            if (_returnDocsPanel is not null)
                _returnDocsPanel.IsVisible = false;
            return;
        }
        if (_returnDocsPanel is null)
        {
            _returnDocsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            if (SelectedChequeBar.Child is StackPanel bar)
                bar.Children.Add(_returnDocsPanel);
        }
        _returnDocsPanel.Children.Clear();
        if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("refunds", out var refunds) || refunds.ValueKind != JsonValueKind.Array)
        {
            _returnDocsPanel.IsVisible = false;
            return;
        }
        var received = DateTime.UtcNow;
        var saleId = _currentSaleId;
        foreach (var r in refunds.EnumerateArray())
        {
            var id = DocStr(r, "id");
            if (id.Length == 0)
                continue;
            var state = ReturnEditState.Parse(r, received);
            var number = DocStr(r, "number") is { Length: > 0 } n ? n : (id.Length > 8 ? id[..8] : id);
            var total = (double.TryParse(DocStr(r, "amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : 0);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var caption = new TextBlock
            {
                Text = Tr.T($"Возврат №{number} · {MoneyText.Som((decimal)total)}", $"Кайтаруу №{number} · {MoneyText.Som((decimal)total)}", $"Return No. {number} · {MoneyText.Som((decimal)total)}",
                    $"İade No. {number} · {MoneyText.Som((decimal)total)}", $"Qaytarish №{number} · {MoneyText.Som((decimal)total)}"),
                FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushText"));
            head.Children.Add(caption);
            if (ReturnEditFlows.VoidedPill(this, state) is { } pill)
                head.Children.Add(pill);
            _returnDocsPanel.Children.Add(head);
            var doc = new ReturnDocRef(ReturnDocKind.Customer, id, number, total, state);
            _returnDocsPanel.Children.Add(ReturnEditFlows.Bar(this, doc, edit: null, reload: () => RefreshCurrentSaleAsync(saleId)));
        }
        _returnDocsPanel.IsVisible = _returnDocsPanel.Children.Count > 0;
    }

    private static string DocStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
}
