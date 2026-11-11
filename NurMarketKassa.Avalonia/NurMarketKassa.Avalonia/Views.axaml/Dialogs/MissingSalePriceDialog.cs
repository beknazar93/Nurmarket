using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-06 (жалоба «Орук маркет», приход №48: программа молча ставила «закупка + 20 %» товарам без цены продажи): перед отправкой прихода — окно
/// «У N позиций нет цены продажи». По каждой позиции — ввод цены продажи или наценки %. «Применить» ставит введённые цены в строки прихода (как введённые руками);
/// «Оставить без цены» — приход уходит как есть, цена товара не трогается (PATCH цены не отправляется); «Отмена» — вернуться к списку.
/// Общее для «Закупки → Приём товара», «Склад → Приёмка» и закупки по накладной.</summary>
public sealed class MissingSalePriceDialog : Window
{
    /// <summary>Что выбрал кассир.</summary>
    public enum Choice
    {
        /// <summary>Закрыли окно / «Отмена» — приход не отправляем.</summary>
        Cancel,
        /// <summary>«Применить» — введённые цены поставлены в строки, остальные позиции остаются без цены.</summary>
        Applied,
        /// <summary>«Оставить без цены» — цены товаров не трогаем.</summary>
        LeaveWithout,
    }

    private sealed class Entry
    {
        public required ReceivingLineVm Line { get; init; }
        public required TextBox Price { get; init; }
        public required TextBox Markup { get; init; }
        public bool Syncing;
        // 2026-11-11: TextChanged в Avalonia 11.2 приходит позже (через Dispatcher), когда Syncing уже снят: текст, поставленный окном в соседнее поле, разбирался как ввод
        // и пересчитывал обратно (наценка 33 % → цена 10 → наценка 42,86 % → цена 11 при округлении вверх, текст менялся под пальцами). Запоминаем поставленное окном.
        public string? PriceEcho, MarkupEcho;
    }

    private readonly List<Entry> _entries = new();
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    public Choice Result { get; private set; } = Choice.Cancel;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public MissingSalePriceDialog(IReadOnlyList<ReceivingLineVm> missing)
    {
        var n = missing.Count;
        Title = T($"У {n} {Tr.Plural(n, "позиции", "позиций", "позиций")} нет цены продажи", $"{n} позициянын сатуу баасы жок", $"{n} {Tr.PluralEn(n, "item has", "items have")} no sale price",
            $"{n} kalemin satış fiyatı yok", $"{n} ta pozitsiyaning sotuv narxi yo'q");
        SectionCards.AsModal(this, 860);

        var header = Row(null, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"),
            T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), T("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"));
        var rows = new StackPanel { Spacing = 4 };
        foreach (var line in missing)
            rows.Children.Add(Row(line, string.IsNullOrWhiteSpace(line.ProductName) ? line.Barcode : line.ProductName, ReceivingLineVm.FormatPrice(line.PurchasePrice).Replace('.', ','), null, null));
        var scroll = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = rows };
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));
        var root = new StackPanel { Spacing = 10, Children = { header, scroll, _status } };
        UpdateStatus();

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var without = SectionCards.Secondary(T("Оставить без цены", "Баасыз калтыруу", "Leave without price", "Fiyatsız bırak", "Narxsiz qoldirish"));
        ToolTip.SetTip(without, T("Приход уйдёт как есть. Цена продажи этих товаров не изменится.", "Кирүү өзгөрүүсүз жөнөтүлөт. Бул товарлардын сатуу баасы өзгөрбөйт.",
            "The delivery is sent as it is. The sale price of these products will not change.", "Giriş olduğu gibi gönderilir. Bu ürünlerin satış fiyatı değişmez.", "Kirim o'zgarishsiz yuboriladi. Bu mahsulotlarning sotuv narxi o'zgarmaydi."));
        without.Click += (_, _) =>
        {
            Result = Choice.LeaveWithout;
            Close();
        };
        var apply = SectionCards.Primary(T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"), isDefault: true);
        apply.Click += (_, _) =>
        {
            foreach (var e in _entries)
                if (ReceivingPricing.ResolveTypedSale(e.Line.PurchasePrice, e.Price.Text, e.Markup.Text) is { } sale)
                {
                    e.Line.SetSaleText(sale.ToString("0.##", CultureInfo.InvariantCulture)); // как введённая руками: режим «вручную», общая наценка её не пересчитает
                    e.Line.CommitText();
                }
            Result = Choice.Applied;
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, without, apply } };
        Content = SectionCards.Shell(Title,
            T("Программа не придумывает цену сама. Введите цену продажи или наценку для каждой позиции — или оставьте товар без цены.",
                "Программа баасын өзү ойлоп таппайт. Ар бир позиция үчүн сатуу баасын же кошумчаны киргизиңиз — же товарды баасыз калтырыңыз.",
                "The program does not invent a price. Enter a sale price or a markup for each item — or leave the product without a price.",
                "Program fiyatı kendisi uydurmaz. Her kalem için satış fiyatı veya kâr girin — ya da ürünü fiyatsız bırakın.",
                "Dastur narxni o'zi o'ylab topmaydi. Har bir pozitsiya uchun sotuv narxi yoki ustamani kiriting — yoki mahsulotni narxsiz qoldiring."),
            root, footer);
    }

    private void UpdateStatus()
    {
        var filled = _entries.Count(e => ReceivingPricing.ResolveTypedSale(e.Line.PurchasePrice, e.Price.Text, e.Markup.Text) is not null);
        _status.Text = T($"Цена введена у {filled} из {_entries.Count}. Остальные останутся без цены.", $"{_entries.Count} позициянын {filled} үчүн баа киргизилди. Калгандары баасыз калат.",
            $"Price entered for {filled} of {_entries.Count}. The rest stay without a price.", $"{_entries.Count} kalemin {filled} tanesi için fiyat girildi. Kalanlar fiyatsız kalır.",
            $"{_entries.Count} tadan {filled} tasi uchun narx kiritildi. Qolganlari narxsiz qoladi.");
    }

    private Control Row(ReceivingLineVm? line, string name, string purchase, string? priceHeader, string? markupHeader)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,150,130"), MinHeight = line is null ? 0 : 44, Margin = new Thickness(0, 2) };
        TextBlock Cell(int col, string text, bool soft, bool trim)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = line is null ? 12 : 13.5, FontWeight = line is null ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0),
            };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
            return tb;
        }
        if (line is null)
        {
            // Заголовок: name/purchase/priceHeader/markupHeader переданы позиционно (см. вызов): name = «Товар», purchase = «Закупка», далее подписи двух полей.
            Cell(0, name, true, false);
            Cell(1, purchase, true, false);
            Cell(2, priceHeader ?? "", true, false);
            Cell(3, markupHeader ?? "", true, false);
            return grid;
        }
        Cell(0, name, false, true);
        Cell(1, purchase, true, false);
        var price = Box(T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi") + " " + name);
        var markup = Box(T("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %") + " " + name);
        Grid.SetColumn(price, 2);
        Grid.SetColumn(markup, 3);
        grid.Children.Add(price);
        grid.Children.Add(markup);
        var entry = new Entry { Line = line, Price = price, Markup = markup };
        _entries.Add(entry);
        // Наценка → цена и цена → наценка считаются на глазах (кассир видит итог до отправки).
        markup.TextChanged += (_, _) =>
        {
            if (entry.Syncing || (entry.MarkupEcho is { } me && me == (markup.Text ?? "")))
                return;
            entry.MarkupEcho = null;
            entry.Syncing = true;
            try
            {
                price.Text = entry.PriceEcho = ReceivingPricing.ResolveTypedSale(line.PurchasePrice, null, markup.Text) is { } sale ? sale.ToString("0.##", Ru) : "";
            }
            finally
            {
                entry.Syncing = false;
            }
            UpdateStatus();
        };
        price.TextChanged += (_, _) =>
        {
            if (entry.Syncing || (entry.PriceEcho is { } pe && pe == (price.Text ?? "")))
                return;
            entry.PriceEcho = null;
            entry.Syncing = true;
            try
            {
                var sale = ReceivingPricing.ResolveTypedSale(line.PurchasePrice, price.Text, null);
                markup.Text = entry.MarkupEcho = sale is { } s && ReceivingPricing.MarkupPercent(line.PurchasePrice, s) is { } m ? m.ToString("0.##", Ru) : "";
            }
            finally
            {
                entry.Syncing = false;
            }
            UpdateStatus();
        };
        return grid;
    }

    private static TextBox Box(string automationName)
    {
        var box = new TextBox
        {
            Watermark = "0", Height = 40, MinHeight = 40, FontSize = 14, Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(box, automationName);
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    /// <summary>Для экранов приёма: если у строк нет цены продажи — показать окно. true — продолжать (цены введены, выбрано «Оставить без цены» или все цены уже есть),
    /// false — кассир отменил, приход не отправляем.</summary>
    public static async Task<bool> AskAsync(Window owner, IReadOnlyList<ReceivingLineVm> lines)
    {
        var missing = ReceivingPricing.MissingSalePrice(lines);
        if (missing.Count == 0)
            return true;
        var dialog = new MissingSalePriceDialog(missing);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Result == Choice.Cancel)
            return false;
        PosLogger.Log($"Приход: у {missing.Count} позиций нет цены продажи — выбор кассира: {(dialog.Result == Choice.Applied ? "цены введены" : "оставить без цены")}; программа цену сама не ставит.", "STOCK");
        return true;
    }
}
