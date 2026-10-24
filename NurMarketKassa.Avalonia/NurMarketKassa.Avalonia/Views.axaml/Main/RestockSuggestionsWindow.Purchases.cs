using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-22 (закупки как в вебе): «Пополнение» и «Закупки» — одно дело. Собранный здесь заказ (позиции «В заказе») — кнопкой
/// «Сделать закупку» сразу в «Приём товара» раздела «Закупки» (строки с количеством, поставщик — если он у всех строк один). Это теперь
/// главная кнопка панели заказа (жёлтая), «Копировать заказ» — обводкой (одна жёлтая кнопка на экран).</summary>
public partial class RestockSuggestionsWindow
{
    private void InitPurchaseLink()
    {
        if (OrderBar.Child is not Grid bar)
            return;
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        CopyOrderButton.Classes.Remove("PrimaryActionButton");
        CopyOrderButton.Classes.Add("sec-outline");
        CopyOrderButton.Margin = new Thickness(0, 0, 8, 0);
        var make = new Button { Content = Tr.T("Сделать закупку", "Сатып алуу жасоо", "Make a purchase", "Alım yap", "Xarid qilish") };
        make.Classes.Add("PrimaryActionButton");
        Avalonia.Automation.AutomationProperties.SetName(make, Tr.T("Сделать закупку из заказа — приём товара в «Закупках»", "Буйрутмадан сатып алуу — «Сатып алууларда» кабыл алуу",
            "Make a purchase from the order — receiving in “Purchases”", "Siparişten alım yap — «Satın alma»da kabul", "Buyurtmadan xarid — «Xaridlar»da qabul"));
        make.IsVisible = !TariffGate.IsStartTariff;
        make.Click += (_, _) => MakePurchase();
        Grid.SetColumn(make, 3);
        bar.Children.Add(make);
    }

    private void MakePurchase()
    {
        var rows = OrderedRows();
        if (rows.Count == 0)
            return;
        var suppliers = rows.Select(r => r.SupplierName.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var prefill = new ReceivePrefill(null, suppliers.Count == 1 && rows.All(r => r.SupplierName.Trim().Length > 0) ? suppliers[0] : null, null, null,
            rows.Select(r => (r.ProductId, r.ProductName, r.Unit, r.RecommendedQty, (double?)null)).ToList());
        PosLogger.Log($"Пополнение: заказ ({rows.Count} поз.) → «Закупки», приём товара{(prefill.SupplierName is { } s ? ", поставщик «" + s + "»" : "")}.", "STOCK");
        PurchasesWindow.Open(this, receive: true, prefill);
    }
}
