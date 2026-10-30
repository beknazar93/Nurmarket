using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-24, ТЗ «Склад», пакет Р2: одна строка списка ревизии (название, штрихкод, учёт, факт, расхождение; в окне итога — ещё «В акт»).
/// Список только для чтения — факт вводится в карточке над ним (ТЗ 2.2), поэтому ячеек для правки здесь нет. Строка сама подписана на
/// <see cref="RevisionRow"/> только пока находится в окне (подписка снимается при уходе из визуального дерева — прокрутка 15 000 строк не копит
/// обработчики) и обновляется на потоке окна, даже если число изменил фоновый «Применить». Недостача — красным, излишек — зелёным.</summary>
internal sealed class RevisionRowView : Border
{
    public const double BarcodeWidth = 130;
    public const double NumberWidth = 84;
    public const double RowHeight = 36;
    public const double CostWidth = 84;
    public const double MoneyWidth = 122;

    /// <summary>2026-10-29 (владелец: «где приходные цены?»): показывать колонки «Закупка» и «Сумма разницы». Ставит окно склада
    /// (<see cref="WarehouseWindow.RevisionShowPurchase"/>: владелец, администратор, автономный режим) до сборки списка и окна итога.</summary>
    public static bool ShowMoney { get; set; }

    private readonly TextBlock _name = new();
    private readonly TextBlock _barcode = new();
    private readonly TextBlock _book = new();
    private readonly TextBlock _fact = new();
    private readonly TextBlock _diff = new();
    private readonly TextBlock? _cost;
    private readonly TextBlock? _money;
    private readonly TextBlock? _send;
    private readonly IReadOnlyDictionary<string, double>? _sendMap;
    private readonly RevisionRow _row;

    public RevisionRow Row => _row;

    public RevisionRowView(RevisionRow row, IReadOnlyDictionary<string, double>? sendMap = null)
    {
        _row = row;
        _sendMap = sendMap;
        Height = RowHeight;
        Background = Brushes.Transparent; // чтобы нажатие попадало в строку, а не сквозь пустоту
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));

        var money = ShowMoney;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns(money, sendMap is not null)), Margin = new Thickness(8, 0, 16, 0) };
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.FontSize = 14;
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.Margin = new Thickness(0, 0, 8, 0);
        _name.Bind(TextBlock.ForegroundProperty, _name.GetResourceObservable("BrushText"));
        Configure(_barcode, BarcodeWidth, HorizontalAlignment.Left, 12.5);
        _barcode.Bind(TextBlock.ForegroundProperty, _barcode.GetResourceObservable("BrushTextSoft"));
        Configure(_book, NumberWidth, HorizontalAlignment.Right, 14);
        _book.Bind(TextBlock.ForegroundProperty, _book.GetResourceObservable("BrushTextSoft"));
        Configure(_fact, NumberWidth, HorizontalAlignment.Right, 14);
        Configure(_diff, NumberWidth, HorizontalAlignment.Right, 14);
        grid.Children.Add(_name);
        Grid.SetColumn(_barcode, 1);
        Grid.SetColumn(_book, 2);
        Grid.SetColumn(_fact, 3);
        Grid.SetColumn(_diff, 4);
        grid.Children.Add(_barcode);
        grid.Children.Add(_book);
        grid.Children.Add(_fact);
        grid.Children.Add(_diff);
        if (money)
        {
            _cost = new TextBlock();
            Configure(_cost, CostWidth, HorizontalAlignment.Right, 13);
            _cost.Bind(TextBlock.ForegroundProperty, _cost.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(_cost, 5);
            grid.Children.Add(_cost);
            _money = new TextBlock();
            Configure(_money, MoneyWidth, HorizontalAlignment.Right, 13);
            Grid.SetColumn(_money, 6);
            grid.Children.Add(_money);
        }

        if (sendMap is not null)
        {
            _send = new TextBlock();
            Configure(_send, NumberWidth, HorizontalAlignment.Right, 14);
            _send.Bind(TextBlock.ForegroundProperty, _send.GetResourceObservable("BrushText"));
            Grid.SetColumn(_send, money ? 7 : 5);
            grid.Children.Add(_send);
        }

        Child = grid;
        Update();
    }

    private static void Configure(TextBlock t, double width, HorizontalAlignment align, double size)
    {
        t.Width = width;
        t.FontSize = size;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private static string Columns(bool money, bool withSend) =>
        "*,Auto,Auto,Auto,Auto" + (money ? ",Auto,Auto" : "") + (withSend ? ",Auto" : "");

    /// <summary>2026-10-29: деньги по расхождению: Разница × закупка; нет закупки — Разница × цена продажи (пометка «*»). null — не считали / нет цен.</summary>
    public static (double Amount, bool Fallback)? DiffMoney(RevisionRow r, double diff)
    {
        if (Math.Abs(diff) <= 0.0005)
            return (0, false);
        if (r.HasPurchase)
            return (Math.Round(diff * r.PurchasePrice, 2), false);
        if (r.SalePrice > 0.0001)
            return (Math.Round(diff * r.SalePrice, 2), true);
        return null;
    }

    /// <summary>2026-10-29: денежные итоги ревизии одной строкой на пункт — для подвала окна и окна «Завершить ревизию»
    /// (Недостача / Излишек / Чистый результат / «Не хватает закупки»). Пусто, если расхождений нет.</summary>
    public static List<(string Text, string Brush, string? Tip)> MoneyLines(RevisionTotals t)
    {
        var list = new List<(string, string, string?)>();
        if (t.WithDifference == 0)
            return list;
        var som = Tr.T("сом", "сом", "som", "TL", "so'm");
        var pcs = Tr.T("шт", "дана", "pcs", "adet", "dona");
        string Q(double v) => ReportFormat.Quantity(v);
        string M(double v) => ReportFormat.Money(v);
        if (t.ShortageQty > 0)
            list.Add((Tr.T($"Недостача: {Q(t.ShortageQty)} {pcs} · −{M(t.ShortageAtCost)} {som} (по закупке) · −{M(t.ShortageAtSale)} {som} (по продаже)",
                $"Жетишсиздик: {Q(t.ShortageQty)} {pcs} · −{M(t.ShortageAtCost)} {som} (сатып алуу боюнча) · −{M(t.ShortageAtSale)} {som} (сатуу боюнча)",
                $"Shortage: {Q(t.ShortageQty)} {pcs} · −{M(t.ShortageAtCost)} {som} (at cost) · −{M(t.ShortageAtSale)} {som} (at sale price)",
                $"Eksik: {Q(t.ShortageQty)} {pcs} · −{M(t.ShortageAtCost)} {som} (alış fiyatıyla) · −{M(t.ShortageAtSale)} {som} (satış fiyatıyla)",
                $"Kamomad: {Q(t.ShortageQty)} {pcs} · −{M(t.ShortageAtCost)} {som} (xarid narxida) · −{M(t.ShortageAtSale)} {som} (sotuv narxida)"), "BrushDanger", null));
        if (t.SurplusQty > 0)
            list.Add((Tr.T($"Излишек: {Q(t.SurplusQty)} {pcs} · +{M(t.SurplusAtCost)} {som} (по закупке) · +{M(t.SurplusAtSale)} {som} (по продаже)",
                $"Ашыкча: {Q(t.SurplusQty)} {pcs} · +{M(t.SurplusAtCost)} {som} (сатып алуу боюнча) · +{M(t.SurplusAtSale)} {som} (сатуу боюнча)",
                $"Surplus: {Q(t.SurplusQty)} {pcs} · +{M(t.SurplusAtCost)} {som} (at cost) · +{M(t.SurplusAtSale)} {som} (at sale price)",
                $"Fazla: {Q(t.SurplusQty)} {pcs} · +{M(t.SurplusAtCost)} {som} (alış fiyatıyla) · +{M(t.SurplusAtSale)} {som} (satış fiyatıyla)",
                $"Ortiqcha: {Q(t.SurplusQty)} {pcs} · +{M(t.SurplusAtCost)} {som} (xarid narxida) · +{M(t.SurplusAtSale)} {som} (sotuv narxida)"), "BrushSuccess", null));
        var net = Math.Round(t.NetAtCost, 2);
        var netText = SignedMoney(net) + " " + som;
        list.Add((Tr.T($"Чистый результат: {netText} (по закупке)", $"Таза жыйынтык: {netText} (сатып алуу боюнча)", $"Net result: {netText} (at cost)",
            $"Net sonuç: {netText} (alış fiyatıyla)", $"Sof natija: {netText} (xarid narxida)"), net < -0.004 ? "BrushDanger" : net > 0.004 ? "BrushSuccess" : "BrushText", null));
        if (t.NoCostCount > 0)
        {
            var n = t.NoCostCount;
            var tip = Tr.T($"У этих товаров нет цены закупки: в суммы «по закупке» они не вошли. Их разница по цене продажи: недостача −{M(t.ShortageNoCostAtSale)} {som}, излишек +{M(t.SurplusNoCostAtSale)} {som}. В таблице их сумма помечена «*».",
                $"Бул товарлардын сатып алуу баасы жок: «сатып алуу боюнча» суммага кирген жок. Сатуу баасы боюнча айырмасы: жетишсиздик −{M(t.ShortageNoCostAtSale)} {som}, ашыкча +{M(t.SurplusNoCostAtSale)} {som}. Таблицада алардын суммасы «*» менен белгиленген.",
                $"These products have no purchase price, so they are not in the “at cost” totals. Their difference at sale price: shortage −{M(t.ShortageNoCostAtSale)} {som}, surplus +{M(t.SurplusNoCostAtSale)} {som}. In the table their amount is marked “*”.",
                $"Bu ürünlerin alış fiyatı yok: «alış fiyatıyla» toplamlara dahil değil. Satış fiyatıyla farkları: eksik −{M(t.ShortageNoCostAtSale)} {som}, fazla +{M(t.SurplusNoCostAtSale)} {som}. Tabloda tutarları «*» ile işaretli.",
                $"Bu mahsulotlarda xarid narxi yo'q: «xarid narxida» summalarga kirmagan. Sotuv narxidagi farqi: kamomad −{M(t.ShortageNoCostAtSale)} {som}, ortiqcha +{M(t.SurplusNoCostAtSale)} {som}. Jadvalda summasi «*» bilan belgilangan.");
            var ruWord = n % 10 == 1 && n % 100 != 11 ? "товара" : "товаров"; // «у 1 товара», «у 2 товаров» (родительный падеж)
            list.Add((Tr.T($"⚠ Не хватает закупки у {n} {ruWord}", $"⚠ Сатып алуу баасы жок: {n} товар",
                $"⚠ No purchase price for {n} {Tr.PluralEn(n, "product", "products")}", $"⚠ Alış fiyatı olmayan ürün: {n}", $"⚠ Xarid narxi yo'q: {n} ta mahsulot"), "BrushWarning", tip));
        }

        return list;
    }

    public static string SignedMoney(double v) => v > 0.004 ? "+" + ReportFormat.Money(v) : v < -0.004 ? "−" + ReportFormat.Money(-v) : "0";

    /// <summary>Шапка столбцов над списком (те же ширины, что у строки).</summary>
    public static Control BuildHeader(bool withSend)
    {
        var money = ShowMoney;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns(money, withSend)), Margin = new Thickness(8, 0, 16, 0) };
        void Add(string text, int col, double width, TextAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (col > 0)
                t.Width = width;
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }

        Add(Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi"), 0, 0, TextAlignment.Left);
        Add(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 1, BarcodeWidth, TextAlignment.Left);
        Add(Tr.T("Учёт", "Эсеп", "Book", "Sistem", "Hisob"), 2, NumberWidth, TextAlignment.Right);
        Add(Tr.T("Факт", "Факт", "Counted", "Sayılan", "Haqiqiy"), 3, NumberWidth, TextAlignment.Right);
        Add(Tr.T("Разница", "Айырма", "Diff.", "Fark", "Farq"), 4, NumberWidth, TextAlignment.Right);
        if (money)
        {
            Add(Tr.T("Закупка", "Сатып алуу", "Cost", "Alış", "Xarid"), 5, CostWidth, TextAlignment.Right);
            Add(Tr.T("Сумма разницы", "Айырма суммасы", "Diff. amount", "Fark tutarı", "Farq summasi"), 6, MoneyWidth, TextAlignment.Right);
        }

        if (withSend)
            Add(Tr.T("В акт", "Актыга", "In act", "Tutanağa", "Aktga"), money ? 7 : 5, NumberWidth, TextAlignment.Right);
        var box = new Border { Height = 30, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("BrushBorder"));
        return box;
    }

    private static string Qty(double v) => ReportFormat.Quantity(v);

    private IBrush? Res(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    public void Update()
    {
        var r = _row;
        _name.Text = r.IsReadOnly
            ? r.Name + "  ·  " + Tr.T("по размерам", "өлчөмдөр боюнча", "by sizes", "bedenlere göre", "o'lchamlar bo'yicha")
            : r.Name;
        _barcode.Text = r.Barcode;
        _book.Text = Qty(r.IsCounted ? r.ExpectedAtCount : r.Expected);
        _fact.Text = r.Fact is { } f ? Qty(f) : "—";
        if (r.IsCounted)
        {
            var d = r.Difference;
            _diff.Text = d > 0.0005 ? "+" + Qty(d) : Math.Abs(d) <= 0.0005 ? "0" : Qty(d);
            var color = d < -0.0005 ? Res("BrushDanger") : d > 0.0005 ? Res("BrushSuccess") : Res("BrushText");
            _diff.Foreground = color;
            _fact.Foreground = color;
            var bold = r.HasDifference ? FontWeight.Bold : FontWeight.SemiBold;
            _diff.FontWeight = bold;
            _fact.FontWeight = bold;
        }
        else
        {
            _diff.Text = "";
            _fact.Foreground = Res("BrushTextSoft");
            _fact.FontWeight = FontWeight.Normal;
        }

        if (_cost is not null && _money is not null)
        {
            _cost.Text = r.HasPurchase ? ReportFormat.Money(r.PurchasePrice) : "—";
            if (r.IsCounted && DiffMoney(r, r.Difference) is { } m)
            {
                _money.Text = SignedMoney(m.Amount) + (m.Fallback && Math.Abs(m.Amount) > 0.004 ? "*" : "");
                _money.Foreground = m.Amount < -0.004 ? Res("BrushDanger") : m.Amount > 0.004 ? Res("BrushSuccess") : Res("BrushText");
                _money.FontWeight = r.HasDifference ? FontWeight.Bold : FontWeight.Normal;
                _money.FontStyle = m.Fallback && r.HasDifference ? FontStyle.Italic : FontStyle.Normal;
                ToolTip.SetTip(_money, m.Fallback && r.HasDifference
                    ? Tr.T("Закупки у товара нет — сумма посчитана по цене продажи.", "Товардын сатып алуу баасы жок — сумма сатуу баасы боюнча эсептелген.",
                        "No purchase price for this product — the amount uses the selling price.", "Ürünün alış fiyatı yok — tutar satış fiyatıyla hesaplandı.", "Mahsulotda xarid narxi yo'q — summa sotuv narxida hisoblangan.")
                    : null);
            }
            else
            {
                _money.Text = "";
                ToolTip.SetTip(_money, null);
            }
        }

        Opacity = r.IsReadOnly ? 0.6 : 1;
        if (_send is not null)
            _send.Text = _sendMap!.TryGetValue(r.ProductId, out var s) ? Qty(s) : "";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Update();
        else
            Dispatcher.UIThread.Post(Update);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _row.PropertyChanged -= OnRowChanged;
        _row.PropertyChanged += OnRowChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Update(); // пока строки не было в окне, её значения могли поменяться
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _row.PropertyChanged -= OnRowChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Update();
}
