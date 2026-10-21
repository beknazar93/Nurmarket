using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-03, владелец: «если в убыток продаёт со скидкой — фиксировать это в админке». Раздел программы
/// владельца «Продажи в убыток»: чеки, где со скидкой товар ушёл дешевле закупки (журнал LossSalesStore, пишет
/// касса при оплате). Период, итог убытка, по каждому чеку — кассир и строки «закупка → продано → убыток».
/// Окно собрано в коде, как «Прибыль и деньги»; цвета — из темы.
/// 2026-10-18, редизайн владельца (этап О3, макет LossSales.dc.html): без своего крупного заголовка (он повторял название
/// раздела), пояснение слева и период сегментами справа, итог — плитками «Чеков в убыток» / «Убыток всего» (убыток — красным).
/// Данные и расчёт — прежние.</summary>
public sealed class LossSalesWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    // 2026-10-19, этап О10: третья плитка «Товаров в убыток» — ширина под три плитки (было 640 на две, справа пусто).
    private readonly KpiTileGrid _totals = new() { MinTileWidth = 200, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _hint = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
    private string _period = "month";
    private int _renderGen;

    // 2026-10-05: сервер NurCRM считает продажи в убыток по всем кассам компании (ТЗ-BE-2026-08, раздел 4 —
    // GET /api/main/analytics/loss-sales/, появилось 05.10). Есть ответ сервера — показываем его; нет (нет связи,
    // старый сервер) — журнал кассы этого компьютера, как раньше.
    private sealed record ServerLossLine(string Title, double Quantity, double Cost, double Net, double Loss, bool Wholesale);

    private sealed record ServerLossSale(DateTimeOffset At, string Number, string Cashier, string Branch, double Loss, List<ServerLossLine> Lines);

    public LossSalesWindow()
    {
        Title = Tr.T("Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar");
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        // 2026-10-18, этап О3: шапка одной строкой — пояснение слева (переносится), период сегментами справа; под ней — плитки
        // итога (или строка «За этот период продаж в убыток нет.»), ниже — чеки.
        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        var hint = _hint;
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        hint.Margin = new Thickness(0, 0, 16, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        var periods = UiKit.WithPeriodLabel(UiKit.Segment(_periods), _periodLabel);
        Grid.SetColumn(periods, 1);
        header.Children.Add(hint);
        header.Children.Add(periods);
        _summary.Margin = new Thickness(0, 0, 0, 12);
        Use(_summary, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetRow(_totals, 1);
        Grid.SetRow(_summary, 2);
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3);
        root.Children.Add(header);
        root.Children.Add(_totals);
        root.Children.Add(_summary);
        root.Children.Add(scroll);
        Content = root;

        BuildPeriods();
        Opened += (_, _) => Render();
        LossSalesStore.Changed += OnChanged;
        Closed += (_, _) => LossSalesStore.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Render);

    // 2026-10-20, проверка фильтров: «Всё (180 дней)» — именно 180 дней и в журнале этого ПК (без связи показывался весь журнал);
    // «7 дней» / «Этот месяц» — общие правила PeriodRange; подпись дат рядом с сегментами.
    private readonly TextBlock _periodLabel = new();

    private DateTimeOffset From() => _period switch
    {
        "today" => DateTime.Today,
        "week" => PeriodRange.WeekFrom(DateTime.Today),
        "all" => DateTime.Today.AddDays(-180),
        _ => PeriodRange.MonthFrom(DateTime.Today),
    };

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        void Add(string key, string text)
        {
            // 2026-10-18, этап О3: сегмент; выбранный — тёмно-синий (было: серые кнопки и светло-жёлтая выбранная).
            var b = UiKit.Seg(text, key == _period);
            b.Click += (_, _) =>
            {
                _period = key;
                BuildPeriods();
                Render();
            };
            _periods.Children.Add(b);
        }

        Add("today", Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"));
        Add("week", Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"));
        Add("month", Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy"));
        Add("all", Tr.T("Всё (180 дней)", "Баары (180 күн)", "All (180 days)", "Tümü (180 gün)", "Hammasi (180 kun)"));
        _periodLabel.Text = PeriodRange.Label(From().Date, DateTime.Today);
    }

    private async void Render()
    {
        var gen = ++_renderGen;
        var from = From();
        List<ServerLossSale>? server = null;
        try
        {
            server = await LoadServerAsync(from).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Продажи в убыток: отчёт сервера не получен ({ex.GetType().Name}: {ex.Message}) — журнал кассы.", "WARNING");
        }
        if (gen != _renderGen)
            return;
        if (server != null)
            RenderServer(server);
        else
            RenderLocal(from);
    }

    private static async Task<List<ServerLossSale>?> LoadServerAsync(DateTimeOffset from)
    {
        var start = from == DateTimeOffset.MinValue ? DateTime.Today.AddDays(-180) : from.Date;
        var query = new Dictionary<string, string>
        {
            ["date_from"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date_to"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        JsonElement data;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            data = await App.GetRequiredService<NurMarketApiClient>()
                .RequestAsync(HttpMethod.Get, "api/main/analytics/loss-sales/", null, query, cts.Token).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("sales", out var sales) || sales.ValueKind != JsonValueKind.Array)
            return null;

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
        static double Num(JsonElement e, string name) =>
            double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

        var result = new List<ServerLossSale>();
        foreach (var sale in sales.EnumerateArray())
        {
            var lines = new List<ServerLossLine>();
            if (sale.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    var qty = Num(it, "quantity");
                    lines.Add(new ServerLossLine(Str(it, "name"), qty, Num(it, "cost_price") * qty, Num(it, "line_total"), Num(it, "loss"),
                        it.TryGetProperty("is_wholesale", out var w) && w.ValueKind == JsonValueKind.True));
                }
            }
            DateTimeOffset.TryParse(Str(sale, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
            result.Add(new ServerLossSale(at, Str(sale, "doc_number"), Str(sale, "cashier"), Str(sale, "branch_name"), Num(sale, "total_loss"), lines));
        }
        return result.OrderByDescending(r => r.At).ToList();
    }

    private void RenderServer(List<ServerLossSale> records)
    {
        _hint.Text = Tr.T("Чеки, где из-за скидки товар продан дешевле закупочной цены, — по всем кассам компании (считает сервер NurCRM).",
            "Арзандатуудан улам товар сатып алуу баасынан арзан сатылган чектер — компаниянын бардык кассалары боюнча (NurCRM сервери эсептейт).",
            "Receipts where a discount sold an item below its purchase price — across all company tills (calculated by the NurCRM server).",
            "İndirim yüzünden ürünün alış fiyatının altında satıldığı fişler — şirketin tüm kasalarında (NurCRM sunucusu hesaplar).",
            "Chegirma tufayli mahsulot xarid narxidan arzon sotilgan cheklar — kompaniyaning barcha kassalari bo'yicha (NurCRM serveri hisoblaydi).");
        _list.Children.Clear();
        var total = records.Sum(r => r.Loss);
        ShowTotals(records.Count, total);
        ShowByProduct(records.SelectMany(r => r.Lines.Select(l => (l.Title, l.Loss))));
        foreach (var r in records.Take(300))
        {
            var who = string.Join(" · ", new[] { r.Number.Length > 0 ? "№" + r.Number : "", r.Cashier, r.Branch }.Where(x => !string.IsNullOrWhiteSpace(x)));
            AddCard(r.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Ru) + (who.Length > 0 ? " · " + who : ""), r.Loss,
                r.Lines.Select(l => (l.Title + (l.Wholesale ? Tr.T(" (опт)", " (дүң)", " (wholesale)", " (toptan)", " (ulgurji)") : ""), l.Quantity, "", l.Cost, l.Net, l.Loss)));
        }
    }

    private void RenderLocal(DateTimeOffset from)
    {
        _hint.Text = Tr.T("Чеки, где из-за скидки товар продан дешевле закупочной цены. Записывает касса этого компьютера при оплате.",
            "Арзандатуудан улам товар сатып алуу баасынан арзан сатылган чектер. Бул компьютердеги касса төлөөдө жазат.",
            "Receipts where a discount sold an item below its purchase price. Recorded by this computer's till at payment.",
            "İndirim yüzünden ürünün alış fiyatının altında satıldığı fişler. Bu bilgisayardaki kasa ödeme sırasında kaydeder.",
            "Chegirma tufayli mahsulot xarid narxidan arzon sotilgan cheklar. Bu kompyuterdagi kassa to'lovda yozadi.");
        _list.Children.Clear();
        var records = LossSalesStore.ReadAll().Where(r => r.At >= from).ToList();
        var total = records.Sum(r => r.Loss);
        ShowTotals(records.Count, total);
        ShowByProduct(records.SelectMany(r => r.Lines.Select(l => (l.Title, l.Loss))));

        foreach (var r in records.Take(300))
            AddCard(r.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Ru) + (string.IsNullOrWhiteSpace(r.Cashier) ? "" : " · " + r.Cashier), r.Loss,
                r.Lines.Select(l => (l.Title, l.Quantity, l.Unit, l.UnitCost * l.Quantity, l.Net, l.Loss)));
    }

    /// <summary>2026-10-18, этап О3: итог периода — плитками (число чеков и убыток всего, убыток — красным); нет чеков —
    /// строка «За этот период продаж в убыток нет.». Те же числа, что раньше были одной красной строкой.</summary>
    private void ShowTotals(int count, double total)
    {
        _totals.Children.Clear();
        _totals.IsVisible = count > 0;
        _summary.IsVisible = count == 0;
        _summary.Text = count == 0
            ? Tr.T("За этот период продаж в убыток нет.", "Бул мезгилде зыян менен сатуу жок.", "No sales at a loss for this period.", "Bu dönemde zararına satış yok.", "Bu davrda zarariga sotuv yo'q.")
            : "";
        if (count == 0)
            return;
        _totals.Children.Add(UiKit.Kpi(Tr.T("Чеков в убыток", "Зыян менен чектер", "Receipts at a loss", "Zararına fişler", "Zarariga cheklar"),
            count.ToString(Ru)));
        _totals.Children.Add(UiKit.Kpi(Tr.T("Убыток всего", "Жалпы зыян", "Total loss", "Toplam zarar", "Jami zarar"), "−" + Money(total), danger: true));
    }

    /// <summary>2026-10-19, этап О10 (аудит: «один и тот же товар повторяется десятки раз — нет сводки по товарам и совета»):
    /// над чеками — «Чаще всего в убыток»: до 5 товаров, сколько раз и на сколько, и что сделать. Из тех же строк, что ниже.</summary>
    private void ShowByProduct(IEnumerable<(string Title, double Loss)> lines)
    {
        var byProduct = lines.Where(l => !string.IsNullOrWhiteSpace(l.Title))
            .GroupBy(l => l.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Title: g.Key, Times: g.Count(), Loss: g.Sum(x => x.Loss)))
            .OrderByDescending(x => x.Loss).ThenByDescending(x => x.Times)
            .ToList();
        if (_totals.IsVisible && byProduct.Count > 0)
            _totals.Children.Add(UiKit.Kpi(Tr.T("Товаров в убыток", "Зыян менен товарлар", "Products at a loss", "Zararına ürünler", "Zarariga mahsulotlar"),
                byProduct.Count.ToString(Ru)));
        if (byProduct.Count == 0)
            return;

        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(16, 12) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        var body = new StackPanel { Spacing = 6 };
        var title = new TextBlock { Text = Tr.T("Чаще всего в убыток", "Көбүнчө зыян менен", "Most often at a loss", "En sık zararına", "Ko'pincha zarariga"), FontSize = 15, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        body.Children.Add(title);
        foreach (var p in byProduct.Take(5))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 28 };
            var name = new TextBlock
            {
                // 2026-10-20, аудит программы владельца: «2 раз» — падеж по числу (1 раз / 2–4 раза / 5 раз).
                Text = p.Title + "  ·  " + Tr.T($"{p.Times} {TimesRu(p.Times)}", $"{p.Times} жолу", p.Times == 1 ? "once" : $"{p.Times} times", $"{p.Times} kez", $"{p.Times} marta"),
                FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            var sum = new TextBlock { Text = "−" + Money(p.Loss), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Use(sum, TextBlock.ForegroundProperty, "BrushDanger");
            Grid.SetColumn(sum, 1);
            row.Children.Add(name);
            row.Children.Add(sum);
            body.Children.Add(row);
        }
        var advice = new TextBlock
        {
            Text = Tr.T("Что сделать: проверьте цену продажи и скидку на эти товары — со скидкой они уходят дешевле закупки. Поднимите цену или ограничьте скидку.",
                "Эмне кылуу керек: бул товарлардын сатуу баасын жана арзандатуусун текшериңиз — арзандатуу менен алар сатып алуу баасынан арзан кетет. Бааны көтөрүңүз же арзандатууну чектеңиз.",
                "What to do: check the selling price and discount of these products — with the discount they sell below cost. Raise the price or limit the discount.",
                "Ne yapmalı: bu ürünlerin satış fiyatını ve indirimini kontrol edin — indirimle alış fiyatının altında satılıyorlar. Fiyatı yükseltin veya indirimi sınırlayın.",
                "Nima qilish kerak: bu mahsulotlarning sotuv narxi va chegirmasini tekshiring — chegirma bilan ular xarid narxidan arzon ketadi. Narxni oshiring yoki chegirmani cheklang."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };
        Use(advice, TextBlock.ForegroundProperty, "BrushTextSoft");
        body.Children.Add(advice);
        card.Child = body;
        _list.Children.Add(card);
    }

    private void AddCard(string headText, double lossTotal, IEnumerable<(string Title, double Quantity, string Unit, double Cost, double Net, double Loss)> lines)
    {
        var card = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(16, 12) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        var body = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var when = new TextBlock { Text = headText, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(when, TextBlock.ForegroundProperty, "BrushText");
        var loss = new TextBlock { Text = "−" + Money(lossTotal), FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(12, 0, 0, 0) };
        Use(loss, TextBlock.ForegroundProperty, "BrushDanger");
        Grid.SetColumn(loss, 1);
        head.Children.Add(when);
        head.Children.Add(loss);
        body.Children.Add(head);
        foreach (var l in lines)
        {
            var unit = string.IsNullOrWhiteSpace(l.Unit) ? "" : " " + l.Unit;
            var line = new TextBlock
            {
                Text = Tr.T(
                    $"• {l.Title}: {l.Quantity:0.###}{unit} · закупка {Money(l.Cost)} · продано за {Money(l.Net)} · убыток {Money(l.Loss)}",
                    $"• {l.Title}: {l.Quantity:0.###}{unit} · сатып алуу {Money(l.Cost)} · сатылды {Money(l.Net)} · зыян {Money(l.Loss)}",
                    $"• {l.Title}: {l.Quantity:0.###}{unit} · cost {Money(l.Cost)} · sold for {Money(l.Net)} · loss {Money(l.Loss)}",
                    $"• {l.Title}: {l.Quantity:0.###}{unit} · alış {Money(l.Cost)} · satış {Money(l.Net)} · zarar {Money(l.Loss)}",
                    $"• {l.Title}: {l.Quantity:0.###}{unit} · xarid {Money(l.Cost)} · sotildi {Money(l.Net)} · zarar {Money(l.Loss)}"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            };
            Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
            body.Children.Add(line);
        }
        card.Child = body;
        _list.Children.Add(card);
    }

    /// <summary>2026-10-20: «раз» / «раза» по числу (2, 3, 4, 22… — «раза»; 12–14 — «раз»).</summary>
    private static string TimesRu(long n) =>
        n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "раза" : "раз";

    private static string Money(double v) => v.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
