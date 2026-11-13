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
// в самом документе (can_void / can_edit); без признака панель пустая и скрытая — касса ведёт себя как раньше.
// 2026-11-11 (сервер Умара): возвраты чека берутся из GET pos/returns/?sale=…&status=all (там can_edit/can_void/edit_block_reason; в refunds[] чека их может не быть),
// добавлена кнопка «Редактировать» (CustomerReturnEditDialog) и пояснение, почему кнопок нет (чек в долг, 24 ч, смена закрыта…). После правки/удаления — чек перечитывается.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private StackPanel? _returnDocsPanel;
    private int _returnDocsGen;

    private void UpdateReturnDocs(JsonElement sale)
    {
        var gen = ++_returnDocsGen;
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
        _returnDocsPanel.IsVisible = false;
        var saleId = _currentSaleId;
        // у чека без возвратов (нет refunds[] и статуса возврата) — запроса нет
        var hasRefunds = sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("refunds", out var refunds) && refunds.ValueKind == JsonValueKind.Array && refunds.GetArrayLength() > 0;
        var returnStatus = DocStr(sale, "return_status");
        if (string.IsNullOrWhiteSpace(saleId) || (!hasRefunds && returnStatus is "" or "none"))
            return;
        _ = LoadReturnDocsAsync(saleId, gen);
    }

    private async System.Threading.Tasks.Task LoadReturnDocsAsync(string saleId, int gen)
    {
        System.Collections.Generic.List<NurCrmReportsApi.PosReturn>? list;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            list = await ReturnEditFlows.LoadSaleReturnsAsync(saleId, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возврат: возвраты чека {saleId} для правки не загружены ({ex.GetType().Name}).", "WARNING");
            return;
        }
        if (gen != _returnDocsGen || _returnDocsPanel is null || list is null || !string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase))
            return;
        _returnDocsPanel.Children.Clear();
        foreach (var r in list)
        {
            var doc = ReturnEditFlows.CustomerDoc(r);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var caption = new TextBlock
            {
                Text = Tr.T($"Возврат {doc.Number} · {MoneyText.Som(r.Amount)}", $"Кайтаруу {doc.Number} · {MoneyText.Som(r.Amount)}", $"Return {doc.Number} · {MoneyText.Som(r.Amount)}",
                    $"İade {doc.Number} · {MoneyText.Som(r.Amount)}", $"Qaytarish {doc.Number} · {MoneyText.Som(r.Amount)}"),
                FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushText"));
            head.Children.Add(caption);
            if (ReturnEditFlows.VoidedPill(this, doc.State) is { } pill)
                head.Children.Add(pill);
            _returnDocsPanel.Children.Add(head);
            _returnDocsPanel.Children.Add(ReturnEditFlows.CustomerBar(this, doc, reload: () => RefreshCurrentSaleAsync(saleId)));
        }
        _returnDocsPanel.IsVisible = _returnDocsPanel.Children.Count > 0;
    }

    private static string DocStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
}
