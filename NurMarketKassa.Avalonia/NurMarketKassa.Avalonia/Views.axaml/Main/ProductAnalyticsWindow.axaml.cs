using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Разбор одного товара: сколько его продали, когда и что с ним делать.
///
/// Открывается нажатием на столбец диаграммы Парето или на строку таблицы ABC. Смысл в том,
/// что ABC отвечает «какие товары важны», но не отвечает «что именно с этим товаром
/// происходит»: растёт он или падает, в какие месяцы его берут, хватит ли остатка. Раньше за
/// этим приходилось уходить в «Продажи» и «Склад» и сопоставлять цифры вручную.
///
/// Считается по локальной истории продаж (SoldLineItems) и каталогу, поэтому работает и без
/// интернета.</summary>
public sealed class ProductAnalyticsWindow : Window
{
    private static readonly string[] MonthShort = Tr.T(
        "янв,фев,мар,апр,май,июн,июл,авг,сен,окт,ноя,дек",
        "янв,фев,мар,апр,май,июн,июл,авг,сен,окт,ноя,дек",
        "Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec",
        "Oca,Şub,Mar,Nis,May,Haz,Tem,Ağu,Eyl,Eki,Kas,Ara",
        "Yan,Fev,Mar,Apr,May,Iyn,Iyl,Avg,Sen,Okt,Noy,Dek").Split(',');

    public ProductAnalyticsWindow(string productName, DateTime periodFrom, DateTime periodTo)
    {
        Title = Tr.T("Разбор товара", "Товардын чечмелөөсү", "Product breakdown",
                     "Ürün ayrıntısı", "Mahsulot tahlili");
        Width = 860;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("BrushWindow") as IBrush ?? Brushes.White;

        Content = new ScrollViewer
        {
            Padding = new Avalonia.Thickness(18),
            Content = Build(productName, periodFrom, periodTo),
        };
    }

    private Control Build(string productName, DateTime periodFrom, DateTime periodTo)
    {
        var root = new StackPanel { Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = productName,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Text(),
        });

        var all = SoldLineItemsStore
            .LoadWithPriceSince(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Where(l => string.Equals(l.ProductName, productName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (all.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = Tr.T(
                    "По этой строке нет истории продаж по названию товара. Так бывает у срезов «Категории» и «Бренды»: там строка — это группа товаров, а не один товар.",
                    "Бул сапта товардын аталышы боюнча сатуу тарыхы жок. «Категориялар» жана «Бренддер» кесилиштеринде ушундай болот: ал жерде сап — бир товар эмес, товарлардын тобу.",
                    "There is no sales history under this row's product name. That happens on the Categories and Brands slices, where a row is a group of products rather than one product.",
                    "Bu satırın ürün adı altında satış geçmişi yok. Kategoriler ve Markalar dilimlerinde böyle olur: orada satır tek ürün değil, ürün grubudur.",
                    "Bu satrda mahsulot nomi bo'yicha sotuv tarixi yo'q. «Kategoriyalar» va «Brendlar» kesimlarida shunday bo'ladi: u yerda satr bitta mahsulot emas, mahsulotlar guruhi."),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            });
            return root;
        }

        var fromUtc = periodFrom.ToUniversalTime();
        var toUtc = periodTo.Date.AddDays(1).ToUniversalTime();
        var inPeriod = all.Where(l => l.SoldAt >= fromUtc && l.SoldAt < toUtc).ToList();

        var product = CatalogCacheService.Products
            .FirstOrDefault(p => string.Equals(p.Title, productName, StringComparison.OrdinalIgnoreCase));

        var writeOffs = DatabaseService.Instance.LoadWriteOffsForProduct(productName);

        root.Children.Add(SummaryRow(inPeriod, product, periodFrom, periodTo));
        root.Children.Add(MonthCard(all));
        root.Children.Add(MovementCard(all, writeOffs, product));
        root.Children.Add(HistoryCard(all));
        root.Children.Add(AdviceCard(all, inPeriod, product, periodFrom, periodTo));
        return root;
    }

    /// <summary>Итоги за выбранный период — те же цифры, по которым товар попал в свою группу
    /// ABC, чтобы не приходилось сверять с таблицей.</summary>
    private Control SummaryRow(
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines,
        Models.Pos.CatalogProductTileVm? product,
        DateTime from,
        DateTime to)
    {
        var qty = lines.Sum(l => l.Quantity);
        var revenue = lines.Sum(l => l.Quantity * l.UnitPrice);
        var purchase = product?.PurchasePrice ?? 0;
        var profit = purchase > 0 ? revenue - qty * purchase : (double?)null;
        var days = Math.Max(1, (to.Date - from.Date).Days + 1);

        var panel = new WrapPanel();
        panel.Children.Add(Tile(
            Tr.T("Продано", "Сатылды", "Sold", "Satıldı", "Sotildi"),
            qty.ToString("0.###", CultureInfo.InvariantCulture)));
        panel.Children.Add(Tile(
            Tr.T("Выручка", "Түшкөн акча", "Revenue", "Ciro", "Tushum"),
            Money(revenue)));
        panel.Children.Add(Tile(
            Tr.T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda"),
            profit is null
                ? Tr.T("нет закупочной цены", "сатып алуу баасы жок", "no purchase price",
                       "alış fiyatı yok", "xarid narxi yo'q")
                : Money(profit.Value)));
        panel.Children.Add(Tile(
            Tr.T("В среднем в день", "Күнүнө орточо", "Average per day", "Günlük ortalama", "Kuniga o'rtacha"),
            (qty / days).ToString("0.##", CultureInfo.InvariantCulture)));
        panel.Children.Add(Tile(
            Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"),
            product is null
                ? Tr.T("нет в каталоге", "каталогдо жок", "not in the catalogue", "katalogda yok", "katalogda yo'q")
                : product.Quantity.ToString("0.###", CultureInfo.InvariantCulture)));

        return panel;
    }

    /// <summary>Продажи по месяцам за всю историю: по ним видно, сезонный товар или ровный,
    /// и растёт он или затухает.</summary>
    private Control MonthCard(
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> all)
    {
        var byMonth = new double[12];
        foreach (var line in all)
            byMonth[line.SoldAt.ToLocalTime().Month - 1] += line.Quantity * line.UnitPrice;

        var items = new List<(string Label, double Value, string ValueText)>();
        for (var m = 0; m < 12; m++)
            items.Add((MonthShort[m], byMonth[m], Money(byMonth[m])));

        var body = new StackPanel();
        BarChartRenderer.Render(body, items);
        return Card(Tr.T("Выручка по месяцам (вся история)", "Айлар боюнча түшкөн акча (бүт тарых)",
                         "Revenue by month (all history)", "Aylara göre ciro (tüm geçmiş)",
                         "Oylar bo'yicha tushum (butun tarix)"), body);
    }

    /// <summary>Короткий вывод словами. Цифры выше уже есть — здесь то, ради чего на товар
    /// нажали: хватит ли остатка и когда его берут.</summary>
    private Control AdviceCard(
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> all,
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> inPeriod,
        Models.Pos.CatalogProductTileVm? product,
        DateTime from,
        DateTime to)
    {
        var body = new StackPanel { Spacing = 6 };
        var days = Math.Max(1, (to.Date - from.Date).Days + 1);
        var perDay = inPeriod.Sum(l => l.Quantity) / days;

        if (product is not null && perDay > 0)
        {
            var cover = product.Quantity / perDay;
            body.Children.Add(Line(
                Tr.T("Остатка хватит примерно на", "Калдык болжол менен жетет",
                     "The stock lasts about", "Stok yaklaşık şu kadar yeter",
                     "Qoldiq taxminan yetadi")
                + " " + cover.ToString("0", CultureInfo.InvariantCulture) + " "
                + Tr.T("дн.", "күн", "d.", "gün", "kun")
                + " " + Tr.T("при нынешнем темпе продаж.", "азыркы сатуу темпинде.",
                             "at the current pace of sales.", "mevcut satış hızıyla.",
                             "hozirgi sotuv sur'atida.")));
        }

        var first = all.Min(l => l.SoldAt).ToLocalTime();
        var last = all.Max(l => l.SoldAt).ToLocalTime();
        body.Children.Add(Line(
            Tr.T("Первая продажа", "Биринчи сатуу", "First sale", "İlk satış", "Birinchi sotuv")
            + ": " + first.ToString("dd.MM.yyyy") + ", "
            + Tr.T("последняя", "акыркысы", "last", "son", "oxirgi")
            + ": " + last.ToString("dd.MM.yyyy") + "."));

        var idle = (DateTime.Now - last).Days;
        if (idle >= 14)
        {
            body.Children.Add(Line(
                Tr.T("Не продавался", "Сатылган жок", "Not sold for", "Satılmadı", "Sotilmadi")
                + " " + idle.ToString(CultureInfo.InvariantCulture) + " "
                + Tr.T("дн. — проверьте, лежит ли он на месте и не пора ли уценить.",
                       "күн — ордунда турганын жана арзандатуу убактысы келгенин текшериңиз.",
                       "days - check whether it is still on the shelf and whether to mark it down.",
                       "gün - rafta duruyor mu, indirim zamanı mı diye bakın.",
                       "kun - javonda turganini va chegirma vaqti kelganini tekshiring.")));
        }

        return Card(Tr.T("Что это значит", "Бул эмнени билдирет", "What this means",
                         "Bu ne anlama geliyor", "Bu nimani anglatadi"), body);
    }

    /// <summary>Движение остатка: каждая продажа и каждое списание как строка со знаком, а
    /// рядом — сколько товара оставалось после этого события.
    ///
    /// Остаток на каждый момент считается не «по документам», а назад от нынешнего: склад
    /// знает только текущее количество, и честно восстановить прошлое можно лишь обратным
    /// ходом по событиям. Если остаток правили вручную или был приход, строки выше правки
    /// будут смещены — поэтому колонка так и называется, «Остаток после», а не «Было/стало».
    ///
    /// Приходы в эту картину не попадают: локально касса их не хранит, они живут на сервере
    /// в складских документах.</summary>
    private Control MovementCard(
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> sales,
        IReadOnlyList<(double Quantity, string Reason, DateTime CreatedAt)> writeOffs,
        Models.Pos.CatalogProductTileVm? product)
    {
        var events = new List<MovementVm>();
        foreach (var sale in sales)
            events.Add(new MovementVm
            {
                When = sale.SoldAt.ToLocalTime(),
                Kind = Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"),
                Delta = -sale.Quantity,
                Note = Money(sale.Quantity * sale.UnitPrice),
            });

        foreach (var writeOff in writeOffs)
            events.Add(new MovementVm
            {
                When = writeOff.CreatedAt.ToLocalTime(),
                Kind = Tr.T("Списание", "Эсептен чыгаруу", "Write-off", "Zayiat", "Hisobdan chiqarish"),
                Delta = -writeOff.Quantity,
                Note = writeOff.Reason,
            });

        events = events.OrderByDescending(e => e.When).ToList();

        // Идём сверху вниз, то есть от свежего к старому: для верхней строки остаток после
        // события — это то, что на складе сейчас.
        var running = product?.Quantity ?? 0;
        foreach (var item in events)
        {
            item.DeltaText = (item.Delta > 0 ? "+" : "") + item.Delta.ToString("0.###", CultureInfo.InvariantCulture);
            item.DeltaBrush = item.Delta < 0 ? Brushes.IndianRed : Brushes.SeaGreen;
            item.WhenText = item.When.ToString("dd.MM.yyyy HH:mm");
            item.RestText = product is null ? "—" : running.ToString("0.###", CultureInfo.InvariantCulture);
            running -= item.Delta;   // шаг назад во времени
        }

        var body = new StackPanel { Spacing = 6 };
        if (events.Count == 0)
        {
            body.Children.Add(Muted(Tr.T("Движения по этому товару нет.", "Бул товар боюнча кыймыл жок.",
                "There is no movement for this product.", "Bu ürün için hareket yok.",
                "Bu mahsulot bo'yicha harakat yo'q.")));
            return Card(MovementTitle(), body);
        }

        var soldTotal = sales.Sum(l => l.Quantity);
        var writtenOff = writeOffs.Sum(w => w.Quantity);
        body.Children.Add(Muted(
            Tr.T("Продано", "Сатылды", "Sold", "Satıldı", "Sotildi") + ": "
            + soldTotal.ToString("0.###", CultureInfo.InvariantCulture)
            + "   ·   " + Tr.T("Списано", "Эсептен чыгарылды", "Written off", "Zayi edildi", "Hisobdan chiqarildi")
            + ": " + writtenOff.ToString("0.###", CultureInfo.InvariantCulture)));

        body.Children.Add(Grid(events, restColumn: true));
        return Card(MovementTitle(), body);
    }

    private static string MovementTitle() => Tr.T(
        "Движение товара", "Товардын кыймылы", "Product movement", "Ürün hareketi", "Mahsulot harakati");

    /// <summary>История продаж построчно: когда, сколько и по какой цене. Ровно то, чего не
    /// видно ни в ABC (там итог за период), ни в чеках (там чек целиком).</summary>
    private Control HistoryCard(
        List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> sales)
    {
        var rows = sales
            .OrderByDescending(l => l.SoldAt)
            .Select(l => new MovementVm
            {
                WhenText = l.SoldAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                Kind = l.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                DeltaText = Money(l.UnitPrice),
                DeltaBrush = Text(),
                Note = Money(l.Quantity * l.UnitPrice),
            })
            .ToList();

        var body = new StackPanel { Spacing = 6 };
        if (rows.Count == 0)
        {
            body.Children.Add(Muted(Tr.T("Продаж не было.", "Сатуу болгон эмес.", "There were no sales.",
                "Satış olmadı.", "Sotuv bo'lmagan.")));
        }
        else
        {
            body.Children.Add(Muted(Tr.T("Всего записей", "Баары жазуу", "Entries in total",
                "Toplam kayıt", "Jami yozuv") + ": " + rows.Count.ToString(CultureInfo.InvariantCulture)));
            body.Children.Add(Grid(rows, restColumn: false, priceHeaders: true));
        }

        return Card(Tr.T("История продаж", "Сатуу тарыхы", "Sales history", "Satış geçmişi",
                         "Sotuvlar tarixi"), body);
    }

    /// <summary>Одна таблица на две карточки: колонки называются по-разному, но строка
    /// устроена одинаково, и разводить два почти одинаковых DataGrid смысла нет.</summary>
    private DataGrid Grid(IReadOnlyList<MovementVm> rows, bool restColumn, bool priceHeaders = false)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            MaxHeight = 260,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = rows,
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = Tr.T("Когда", "Качан", "When", "Ne zaman", "Qachon"),
            Width = new DataGridLength(140),
            Binding = new Avalonia.Data.Binding(nameof(MovementVm.WhenText)),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = priceHeaders
                ? Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni")
                : Tr.T("Событие", "Окуя", "Event", "Olay", "Hodisa"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Avalonia.Data.Binding(nameof(MovementVm.Kind)),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = priceHeaders
                ? Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx")
                : Tr.T("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish"),
            Width = new DataGridLength(120),
            Binding = new Avalonia.Data.Binding(nameof(MovementVm.DeltaText)),
        });

        if (restColumn)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = Tr.T("Остаток после", "Андан кийинки калдык", "Stock after",
                               "Sonraki stok", "Keyingi qoldiq"),
                Width = new DataGridLength(130),
                Binding = new Avalonia.Data.Binding(nameof(MovementVm.RestText)),
            });
        }

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = priceHeaders
                ? Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")
                : Tr.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"),
            Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
            Binding = new Avalonia.Data.Binding(nameof(MovementVm.Note)),
        });

        return grid;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Gray,
    };

    private sealed class MovementVm
    {
        public DateTime When { get; init; }
        public string WhenText { get; set; } = "";
        public string Kind { get; init; } = "";
        public double Delta { get; init; }
        public string DeltaText { get; set; } = "";
        public IBrush DeltaBrush { get; set; } = Brushes.Gray;
        public string RestText { get; set; } = "";
        public string Note { get; init; } = "";
    }

    private static string Money(double value) =>
        value.ToString("N2", CultureInfo.CurrentCulture) + " " + Tr.T("сом", "сом", "KGS", "KGS", "KGS");

    private TextBlock Line(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Text(),
    };

    private Border Tile(string caption, string value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = Brushes.Gray });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = Text(),
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Padding = new Avalonia.Thickness(12, 8),
            Margin = new Avalonia.Thickness(0, 0, 10, 10),
            MinWidth = 150,
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = this.FindResource("BrushPanel") as IBrush ?? Brushes.WhiteSmoke,
            BorderBrush = this.FindResource("BrushBorder") as IBrush ?? Brushes.LightGray,
            BorderThickness = new Avalonia.Thickness(1),
            Child = panel,
        };
    }

    private Border Card(string title, Control body)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
            Foreground = Text(),
        });
        panel.Children.Add(body);

        return new Border
        {
            Padding = new Avalonia.Thickness(12),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = this.FindResource("BrushPanel") as IBrush ?? Brushes.WhiteSmoke,
            BorderBrush = this.FindResource("BrushBorder") as IBrush ?? Brushes.LightGray,
            BorderThickness = new Avalonia.Thickness(1),
            Child = panel,
        };
    }

    private IBrush Text() => this.FindResource("BrushText") as IBrush ?? Brushes.Black;
}
