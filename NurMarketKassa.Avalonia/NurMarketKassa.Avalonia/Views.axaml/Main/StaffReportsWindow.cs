using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using static NurMarketKassa.Services.OwnerReportsMath;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-13 (Л-13 «Отчёты владельца», цель 7,5 → 9): раздел программы владельца «Кассиры и часы» — два отчёта по чекам всех касс компании
/// (список продаж сервера, <see cref="OwnerSalesReportData"/>), расчёты — в <see cref="OwnerReportsMath"/> (с тестами):
/// • «По кассирам»: выручка, чеки, средний чек, возвраты, скидки, «удалено из чека» (журнал этого ПК) и изменение выручки к прошлому периоду;
/// • «Часы и дни недели»: тепловая карта 7 × 24 по выручке, итоги по дням недели и лучшие день/час.
/// Сравнение — переключатель «Без сравнения / С прошлым периодом / С годом назад» (стрелки ↑↓ и %). Excel — общая кнопка оболочки владельца
/// (<see cref="IOwnerSectionExcel"/>): выгружается открытая вкладка. Графика — без новых пакетов (рамки и цвет ячеек).</summary>
public sealed class StaffReportsWindow : Window, IOwnerSectionRefresh, IOwnerSectionExcel
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _compares = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _periodLabel = new();
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly StackPanel _body = new() { Spacing = 14 };
    private CancellationTokenSource? _cts;
    private string _tab = "cashiers";
    private string _period = "week";
    private CompareBase _compare = CompareBase.Previous;
    private DateTime _loadedUtc = DateTime.UtcNow;

    // Данные последней загрузки (для Excel и перерисовки без запроса).
    private IReadOnlyList<SaleFact>? _cur;
    private IReadOnlyList<SaleFact>? _prev;
    private IReadOnlyDictionary<string, int> _removals = new Dictionary<string, int>();
    private (DateTime From, DateTime To) _curRange;
    private (DateTime From, DateTime To) _prevRange;
    private bool _truncated;

    public StaffReportsWindow()
    {
        Title = TitleText;
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new StackPanel { Margin = OwnerSectionLayout.Margin, Spacing = 0 };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (segment, gap) in new (Control, double)[] { (UiKit.Segment(_tabs), 12), (UiKit.WithPeriodLabel(UiKit.Segment(_periods), _periodLabel), 12), (UiKit.Segment(_compares), 0) })
        {
            segment.Margin = new Thickness(0, 0, gap, 8);
            bar.Children.Add(segment);
        }
        root.Children.Add(bar);
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(_status);
        root.Children.Add(_body);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        BuildBar();
        Opened += (_, _) => _ = LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    public static string TitleText => Tr.T("Кассиры и часы", "Кассирлер жана сааттар", "Cashiers and hours", "Kasiyerler ve saatler", "Kassirlar va soatlar");

    // ── Шапка ──

    private (DateTime From, DateTime To, PeriodKind Kind) Range()
    {
        var today = ServerClock.Today;
        return _period switch
        {
            "today" => (today, today, PeriodKind.Day),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1), PeriodKind.Day),
            "month" => (PeriodRange.MonthFrom(today), today, PeriodKind.Month),
            "d30" => (today.AddDays(-29), today, PeriodKind.Custom),
            _ => (PeriodRange.WeekFrom(today), today, PeriodKind.Week),
        };
    }

    private void BuildBar()
    {
        _tabs.Children.Clear();
        _periods.Children.Clear();
        _compares.Children.Clear();
        void Add(Panel host, string text, bool active, Action pick)
        {
            var b = UiKit.Seg(text, active);
            b.Click += (_, _) =>
            {
                pick();
                BuildBar();
            };
            host.Children.Add(b);
        }
        Add(_tabs, Tr.T("По кассирам", "Кассирлер боюнча", "By cashier", "Kasiyere göre", "Kassirlar bo'yicha"), _tab == "cashiers", () => { _tab = "cashiers"; Render(); });
        Add(_tabs, Tr.T("Часы и дни недели", "Сааттар жана апта күндөрү", "Hours and weekdays", "Saatler ve günler", "Soatlar va hafta kunlari"), _tab == "hours", () => { _tab = "hours"; Render(); });
        foreach (var (key, text) in new[]
                 {
                     ("today", Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")),
                     ("yesterday", Tr.T("Вчера", "Кечээ", "Yesterday", "Dün", "Kecha")),
                     ("week", Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun")),
                     ("month", Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy")),
                     ("d30", Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun")),
                 })
            Add(_periods, text, _period == key, () => { _period = key; _ = LoadAsync(); });
        Add(_compares, Tr.T("Без сравнения", "Салыштырусуз", "No comparison", "Karşılaştırmasız", "Taqqoslashsiz"), _compare == CompareBase.None, () => { _compare = CompareBase.None; _ = LoadAsync(); });
        Add(_compares, Tr.T("С прошлым периодом", "Мурунку мезгил менен", "With previous period", "Önceki dönemle", "Oldingi davr bilan"), _compare == CompareBase.Previous, () => { _compare = CompareBase.Previous; _ = LoadAsync(); });
        Add(_compares, Tr.T("С годом назад", "Былтыркы менен", "With a year ago", "Geçen yılla", "O'tgan yil bilan"), _compare == CompareBase.YearAgo, () => { _compare = CompareBase.YearAgo; _ = LoadAsync(); });
        var (from, to, _) = Range();
        _periodLabel.Text = PeriodRange.Label(from, to);
    }

    public DateTime? RefreshOnShow()
    {
        if (DateTime.UtcNow - _loadedUtc <= TimeSpan.FromSeconds(60))
            return null;
        var dataAt = _loadedUtc.ToLocalTime();
        _ = LoadAsync();
        return dataAt;
    }

    // ── Загрузка ──

    private bool _preview;

    /// <summary>2026-11-13: снимки и UI-тесты — показать готовые чеки без сервера (смена вкладки и сравнения тогда только перерисовывает). В программе не вызывается.</summary>
    public void ShowForPreview(string tab, IReadOnlyList<SaleFact> current, IReadOnlyList<SaleFact>? previous, DateTime from, DateTime to, CompareBase basis,
        IReadOnlyDictionary<string, int>? removals = null)
    {
        _preview = true;
        _tab = tab;
        _compare = basis;
        _cur = current;
        _prev = previous;
        _removals = removals ?? new Dictionary<string, int>();
        _curRange = (from, to);
        _prevRange = ComparePeriod(from, to, PeriodKind.Week, basis == CompareBase.None ? CompareBase.Previous : basis);
        _truncated = false;
        BuildBar();
        Render();
    }

    private async Task LoadAsync()
    {
        if (_preview)
        {
            Render();
            return;
        }
        _loadedUtc = DateTime.UtcNow;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var (from, to, kind) = Range();
        var basis = _compare;
        var prev = basis == CompareBase.None ? ((DateTime, DateTime)?)null : ComparePeriod(from, to, kind, basis);
        _body.Children.Clear();
        _status.Text = Tr.T("Загружаю чеки с сервера NurCRM…", "NurCRM серверинен чектер жүктөлүүдө…", "Loading receipts from the NurCRM server…", "Fişler NurCRM sunucusundan yükleniyor…", "Cheklar NurCRM serveridan yuklanmoqda…");
        try
        {
            var curTask = OwnerSalesReportData.LoadAsync(from, to, ct);
            var prevTask = prev is { } p ? OwnerSalesReportData.LoadAsync(p.Item1, p.Item2, ct) : null;
            var removalsTask = Task.Run(() => OwnerSalesReportData.RemovalsByCashier(from, to), ct);
            var cur = await curTask.ConfigureAwait(true);
            // Прошлый период не ответил — отчёт показываем без сравнения, с пояснением (а не пустой экран).
            (IReadOnlyList<SaleFact> Sales, bool Truncated)? previous = null;
            var prevFailed = false;
            if (prevTask is not null)
            {
                try
                {
                    previous = await prevTask.ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    prevFailed = true;
                    PosLogger.Log($"Кассиры и часы: прошлый период не получен ({ex.Message}).", "WARNING");
                }
            }
            if (ct.IsCancellationRequested)
                return;
            _cur = cur.Sales;
            _prev = previous?.Sales;
            _removals = await removalsTask.ConfigureAwait(true);
            _curRange = (from, to);
            _prevRange = prev is { } pr ? (pr.Item1, pr.Item2) : default;
            _truncated = cur.Truncated || previous is { Truncated: true };
            Render(prevFailed ? Tr.T(" Прошлый период не загрузился — сравнения нет.", " Мурунку мезгил жүктөлгөн жок — салыштыруу жок.", " The previous period did not load — no comparison.",
                " Önceki dönem yüklenemedi — karşılaştırma yok.", " Oldingi davr yuklanmadi — taqqoslash yo'q.") : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _cur = null;
            _body.Children.Clear();
            _status.Text = "";
            _body.Children.Add(StateBlock.Failure(this, ex, Tr.T("Отчёт не построен", "Отчёт түзүлгөн жок", "The report was not built", "Rapor oluşturulamadı", "Hisobot tuzilmadi"), () => _ = LoadAsync()));
            PosLogger.Log($"Кассиры и часы: отчёт не построен ({ex.Message}).", "WARNING");
        }
    }

    // ── Показ ──

    private void Render(string extraStatus = "")
    {
        if (_cur is null)
            return;
        _body.Children.Clear();
        var withCompare = _compare != CompareBase.None && _prev is not null;
        var (_, _, kind) = Range();
        var cmpName = withCompare ? CompareName(_compare, kind) : "";
        _status.Text = Tr.T($"Чеков за период: {TotalsOf(_cur).Checks}.", $"Мезгилдеги чектер: {TotalsOf(_cur).Checks}.", $"Receipts in the period: {TotalsOf(_cur).Checks}.",
                $"Dönemdeki fişler: {TotalsOf(_cur).Checks}.", $"Davrdagi cheklar: {TotalsOf(_cur).Checks}.")
            + (withCompare ? Tr.T($" Сравнение: {PeriodRange.Label(_prevRange.From, _prevRange.To)} ({cmpName}).", $" Салыштыруу: {PeriodRange.Label(_prevRange.From, _prevRange.To)} ({cmpName}).",
                $" Comparison: {PeriodRange.Label(_prevRange.From, _prevRange.To)} ({cmpName}).", $" Karşılaştırma: {PeriodRange.Label(_prevRange.From, _prevRange.To)} ({cmpName}).",
                $" Taqqoslash: {PeriodRange.Label(_prevRange.From, _prevRange.To)} ({cmpName}).") : "")
            + extraStatus
            + (_truncated ? Tr.T($" Чеков больше {OwnerSalesReportData.MaxPages * 200} — отчёт неполный, выберите период короче.", $" Чектер {OwnerSalesReportData.MaxPages * 200}дөн ашты — отчёт толук эмес, мезгилди кыскартыңыз.",
                $" More than {OwnerSalesReportData.MaxPages * 200} receipts — the report is incomplete, choose a shorter period.", $" {OwnerSalesReportData.MaxPages * 200} fişten fazla — rapor eksik, daha kısa dönem seçin.",
                $" {OwnerSalesReportData.MaxPages * 200} tadan ko'p chek — hisobot to'liq emas, qisqaroq davr tanlang.") : "");
        if (_cur.Count == 0)
        {
            _body.Children.Add(StateBlock.Empty(Tr.T("За этот период чеков нет", "Бул мезгилде чектер жок", "No receipts in this period", "Bu dönemde fiş yok", "Bu davrda cheklar yo'q"),
                Tr.T("Выберите другой период.", "Башка мезгилди тандаңыз.", "Choose another period.", "Başka bir dönem seçin.", "Boshqa davrni tanlang.")));
            return;
        }
        if (_tab == "cashiers")
            RenderCashiers(withCompare);
        else
            RenderHours(withCompare);
    }

    private IReadOnlyList<CashierRow> CashierRows(IReadOnlyList<SaleFact> sales, bool withErrors)
    {
        var rows = ByCashier(sales);
        return withErrors ? WithErrors(rows, _removals) : rows;
    }

    private void RenderCashiers(bool withCompare)
    {
        var rows = CashierRows(_cur!, true);
        var prevRows = withCompare ? CashierRows(_prev!, false).ToDictionary(r => r.Cashier, StringComparer.CurrentCultureIgnoreCase) : null;
        var total = Total(rows);

        var tiles = new KpiTileGrid { MinTileWidth = 170, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left };
        tiles.Children.Add(UiKit.Kpi(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), MoneyText.Som(total.Revenue)));
        tiles.Children.Add(UiKit.Kpi(Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), total.Checks.ToString("N0", Ru)));
        tiles.Children.Add(UiKit.Kpi(Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), MoneyText.Som(total.AvgCheck)));
        tiles.Children.Add(UiKit.Kpi(Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"), total.Returns.ToString("N0", Ru) + " · " + MoneyText.Som(total.ReturnsSum), danger: total.Returns > 0));
        _body.Children.Add(tiles);

        if (withCompare)
        {
            var lines = CompareTotals(TotalsOf(_cur!), TotalsOf(_prev!));
            _body.Children.Add(CompareCard(lines));
        }

        // Таблица. Колонки: кассир | выручка | чеков | средний чек | возвраты | скидки | удалено из чека | (изменение выручки)
        var cols = withCompare ? "*,130,70,110,130,110,90,110" : "*,130,70,110,130,110,90";
        var table = new StackPanel { Spacing = 2, MinWidth = withCompare ? 860 : 740 };
        var headTexts = new List<string>
        {
            ReportLabels.Cashier, Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"),
            Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"),
            Tr.T("Скидки", "Арзандатуулар", "Discounts", "İndirimler", "Chegirmalar"), Tr.T("Удалено", "Өчүрүлгөн", "Removed", "Silinen", "O'chirilgan"),
        };
        if (withCompare)
            headTexts.Add(Tr.T("К прошлому", "Мурункуга", "vs previous", "Öncekine göre", "Oldingiga"));
        table.Children.Add(TableRow(cols, headTexts, null, header: true));
        foreach (var r in rows)
        {
            var cells = new List<string>
            {
                r.Cashier, MoneyText.Amount(r.Revenue), r.Checks.ToString("N0", Ru), MoneyText.Amount(r.AvgCheck),
                r.Returns == 0 ? "—" : r.Returns + " · " + MoneyText.Amount(r.ReturnsSum),
                r.DiscountSum > 0 ? MoneyText.Amount(r.DiscountSum) : "—",
                r.Errors > 0 ? r.Errors.ToString("N0", Ru) : "—",
            };
            decimal? pct = null;
            if (withCompare)
            {
                pct = prevRows!.TryGetValue(r.Cashier, out var pr) ? DeltaPercent(r.Revenue, pr.Revenue) : null;
                cells.Add(Arrow(pct));
            }
            table.Children.Add(TableRow(cols, cells, pct, header: false, deltaLast: withCompare));
        }
        table.Children.Add(TableRow(cols, new[] { ReportLabels.Total, MoneyText.Amount(total.Revenue), total.Checks.ToString("N0", Ru), MoneyText.Amount(total.AvgCheck),
            total.Returns == 0 ? "—" : total.Returns + " · " + MoneyText.Amount(total.ReturnsSum), total.DiscountSum > 0 ? MoneyText.Amount(total.DiscountSum) : "—",
            total.Errors > 0 ? total.Errors.ToString("N0", Ru) : "—" }.Concat(withCompare ? new[] { Arrow(DeltaPercent(total.Revenue, TotalsOf(_prev!).Revenue)) } : []).ToList(),
            withCompare ? DeltaPercent(total.Revenue, TotalsOf(_prev!).Revenue) : null, header: false, bold: true, deltaLast: withCompare));
        var card = Card(Tr.T("Кассиры", "Кассирлер", "Cashiers", "Kasiyerler", "Kassirlar"), new ScrollViewer
        {
            Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        _body.Children.Add(card);
        _body.Children.Add(Soft(Tr.T(
            "Выручка и средний чек — по оплаченным и частично возвращённым чекам, как плитка «Сводки». «Возвраты» — чеки, возвращённые целиком или отменённые (число и сумма чека). «Скидки» — в чеках выручки. «Удалено» — удаления из чека по журналу этого компьютера (на других кассах журнал свой).",
            "Түшүм жана орточо чек — төлөнгөн жана жарым-жартылай кайтарылган чектер боюнча, «Жыйынтык» плиткасындай. «Кайтаруулар» — толугу менен кайтарылган же жокко чыгарылган чектер (саны жана суммасы). «Арзандатуулар» — түшүм чектеринде. «Өчүрүлгөн» — ушул компьютердин журналы боюнча чектен өчүрүүлөр (башка кассаларда журнал өзүнчө).",
            "Revenue and average receipt count paid and partially returned receipts, like the Overview tile. “Returns” are receipts fully returned or canceled (count and receipt amount). “Discounts” are in revenue receipts. “Removed” is removals from a receipt per this computer's log (other tills keep their own log).",
            "Ciro ve ortalama fiş, Özet kutucuğu gibi ödenmiş ve kısmen iade edilmiş fişlere göre. «İadeler» tamamen iade edilen veya iptal edilen fişler (sayı ve fiş tutarı). «İndirimler» ciro fişlerindeki. «Silinen» bu bilgisayarın kaydına göre fişten silmeler (diğer kasaların kaydı ayrı).",
            "Tushum va o'rtacha chek — to'langan va qisman qaytarilgan cheklar bo'yicha, «Xulosa» plitkasidagidek. «Qaytarishlar» — to'liq qaytarilgan yoki bekor qilingan cheklar (soni va summasi). «Chegirmalar» — tushum cheklarida. «O'chirilgan» — shu kompyuter jurnali bo'yicha chekdan o'chirishlar (boshqa kassalarda jurnal alohida).")));
    }

    private void RenderHours(bool withCompare)
    {
        var map = BuildHeatMap(_cur!);
        var prevMap = withCompare ? BuildHeatMap(_prev!) : null;
        if (map.TotalChecks == 0)
        {
            _body.Children.Add(StateBlock.Empty(Tr.T("За этот период продаж нет", "Бул мезгилде сатуу жок", "No sales in this period", "Bu dönemde satış yok", "Bu davrda sotuvlar yo'q")));
            return;
        }
        var tiles = new KpiTileGrid { MinTileWidth = 200, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left };
        if (map.BestDay() is { } bd)
            tiles.Children.Add(UiKit.Kpi(Tr.T("Лучший день недели", "Аптанын эң мыкты күнү", "Best weekday", "En iyi gün", "Eng yaxshi hafta kuni"), WeekdayLong(bd)));
        if (map.BestHour() is { } bh)
            tiles.Children.Add(UiKit.Kpi(Tr.T("Лучший час", "Эң мыкты саат", "Best hour", "En iyi saat", "Eng yaxshi soat"), $"{bh:00}:00–{(bh + 1) % 24:00}:00"));
        if (map.Peak() is { } pk)
            tiles.Children.Add(UiKit.Kpi(Tr.T("Пик продаж", "Сатуунун чокусу", "Sales peak", "Satış zirvesi", "Sotuv cho'qqisi"), $"{WeekdayShort(pk.Day)} {pk.Hour:00}:00"));
        tiles.Children.Add(UiKit.Kpi(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), MoneyText.Som(map.TotalRevenue)));
        _body.Children.Add(tiles);
        if (withCompare)
            _body.Children.Add(CompareCard(CompareTotals(TotalsOf(_cur!), TotalsOf(_prev!))));

        _body.Children.Add(Card(Tr.T("Тепловая карта: когда покупают", "Жылуулук картасы: качан сатып алышат", "Heat map: when customers buy", "Isı haritası: ne zaman alışveriş yapılır", "Issiqlik xaritasi: qachon xarid qilinadi"), HeatGrid(map)));
        _body.Children.Add(Card(Tr.T("По дням недели", "Апта күндөрү боюнча", "By weekday", "Haftanın günlerine göre", "Hafta kunlari bo'yicha"), WeekdayRows(map, prevMap)));
        _body.Children.Add(Soft(Tr.T(
            "Цвет ячейки — выручка в этот час этого дня недели, за весь период (чем темнее, тем больше). Наведите на ячейку — сумма и число чеков. Возвраты и продажи в долг не считаются.",
            "Уяча түсү — бардык мезгилде аптанын ушул күнүндө ушул саатта түшкөн түшүм (кара болсо, көп). Уячага курсорду алып барыңыз — сумма жана чектердин саны. Кайтаруулар жана карызга сатуулар эсептелбейт.",
            "Cell colour is the revenue at this hour of this weekday over the whole period (darker is more). Hover a cell for the amount and receipt count. Returns and credit sales are not counted.",
            "Hücre rengi, tüm dönemde haftanın bu gününde bu saatteki cirodur (koyu = daha fazla). Tutar ve fiş sayısı için hücrenin üzerine gelin. İadeler ve veresiye satışlar sayılmaz.",
            "Katak rangi — butun davr bo'yicha hafta shu kunining shu soatidagi tushum (to'qroq bo'lsa, ko'proq). Summa va cheklar soni uchun katak ustiga olib boring. Qaytarishlar va qarzga sotuvlar hisoblanmaydi.")));
    }

    private Control HeatGrid(HeatMap map)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto" + string.Concat(Enumerable.Repeat(",*", 24))), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"), MinWidth = 560 };
        for (var h = 0; h < 24; h++)
        {
            var t = new TextBlock { Text = h.ToString(), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, h + 1);
            grid.Children.Add(t);
        }
        for (var d = 0; d < 7; d++)
        {
            var label = new TextBlock { Text = WeekdayShort(d), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Use(label, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetRow(label, d + 1);
            grid.Children.Add(label);
            for (var h = 0; h < 24; h++)
            {
                var intensity = map.Intensity(d, h);
                var cell = new Border { Height = 30, Margin = new Thickness(1), CornerRadius = new CornerRadius(3) };
                if (intensity > 0)
                {
                    Use(cell, Border.BackgroundProperty, "BrushAccent");
                    cell.Opacity = 0.12 + 0.88 * intensity;
                    ToolTip.SetTip(cell, $"{WeekdayShort(d)} {h:00}:00 — {MoneyText.Som(map.Revenue[d, h])}, " + Tr.T($"чеков: {map.Checks[d, h]}", $"чектер: {map.Checks[d, h]}", $"receipts: {map.Checks[d, h]}", $"fiş: {map.Checks[d, h]}", $"cheklar: {map.Checks[d, h]}"));
                }
                else
                    Use(cell, Border.BackgroundProperty, "BrushPanelSoft");
                Avalonia.Automation.AutomationProperties.SetName(cell, $"{WeekdayShort(d)} {h:00}:00 {MoneyText.Amount(map.Revenue[d, h])}");
                Grid.SetRow(cell, d + 1);
                Grid.SetColumn(cell, h + 1);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private Control WeekdayRows(HeatMap map, HeatMap? prevMap)
    {
        var stack = new StackPanel { Spacing = 4, MinWidth = 560 };
        var maxDay = Enumerable.Range(0, 7).Max(map.DayRevenue);
        for (var d = 0; d < 7; d++)
        {
            var revenue = map.DayRevenue(d);
            var frac = maxDay > 0 ? (double)(revenue / maxDay) : 0;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,130,70,90"), MinHeight = 28 };
            row.Children.Add(Cell(WeekdayLong(d), 0, HorizontalAlignment.Left, "BrushText"));
            var barHost = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(frac, 0.001).ToString(Inv)}*,{Math.Max(1 - frac, 0.001).ToString(Inv)}*"), Margin = new Thickness(0, 6, 12, 6) };
            var bar = new Border { CornerRadius = new CornerRadius(3) };
            Use(bar, Border.BackgroundProperty, "BrushAccent");
            barHost.Children.Add(bar);
            Grid.SetColumn(barHost, 1);
            row.Children.Add(barHost);
            row.Children.Add(Cell(MoneyText.Amount(revenue), 2, HorizontalAlignment.Right, "BrushText"));
            row.Children.Add(Cell(map.DayChecks(d).ToString("N0", Ru), 3, HorizontalAlignment.Right, "BrushTextSoft"));
            if (prevMap is not null)
            {
                var pct = DeltaPercent(revenue, prevMap.DayRevenue(d));
                var delta = Cell(Arrow(pct), 4, HorizontalAlignment.Right, DeltaBrush(pct));
                row.Children.Add(delta);
            }
            stack.Children.Add(row);
        }
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    // ── Блоки ──

    private Control CompareCard(IReadOnlyList<CompareLine> lines)
    {
        var table = new StackPanel { Spacing = 2, MinWidth = 520 };
        const string cols = "*,150,150,110";
        table.Children.Add(TableRow(cols, new[] { Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), Tr.T("Сейчас", "Азыр", "Now", "Şimdi", "Hozir"),
            Tr.T("Было", "Болгон", "Before", "Önce", "Oldin"), Tr.T("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish") }, null, header: true));
        foreach (var l in lines)
            table.Children.Add(TableRow(cols, new[] { l.Label, l.IsMoney ? MoneyText.Som(l.Current) : l.Current.ToString("N0", Ru), l.IsMoney ? MoneyText.Som(l.Previous) : l.Previous.ToString("N0", Ru), Arrow(l.Percent) }, l.Percent, header: false, deltaLast: true));
        return Card(Tr.T("Сравнение периодов", "Мезгилдерди салыштыруу", "Period comparison", "Dönem karşılaştırması", "Davrlarni taqqoslash"),
            new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }

    /// <summary>Строка таблицы. deltaLast — последняя колонка «изменение»: цвет по <paramref name="pct"/> (зелёный рост, красный падение).</summary>
    private Grid TableRow(string cols, IReadOnlyList<string> texts, decimal? pct, bool header, bool bold = false, bool deltaLast = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
        for (var i = 0; i < texts.Count; i++)
        {
            var t = new TextBlock
            {
                Text = texts[i], FontSize = header ? 12.5 : 14, FontWeight = header || bold ? FontWeight.SemiBold : FontWeight.Normal,
                // «…» — только у названия; числа и суммы не обрезаются (правило аудита 3).
                TextTrimming = i == 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, i == texts.Count - 1 ? 0 : 12, 3),
            };
            Use(t, TextBlock.ForegroundProperty, header ? "BrushTextSoft" : deltaLast && i == texts.Count - 1 ? DeltaBrush(pct) : "BrushText");
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    private static string DeltaBrush(decimal? pct) => Direction(pct) switch { 1 => "BrushSuccess", -1 => "BrushDanger", _ => "BrushTextSoft" };

    private Border Card(string title, Control body)
    {
        var card = new Border { Padding = new Thickness(16, 12), BorderThickness = new Thickness(1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        card.Child = new StackPanel { Spacing = 4, Children = { head, body } };
        return card;
    }

    private TextBlock Cell(string text, int column, HorizontalAlignment align, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = 14, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, brush);
        Grid.SetColumn(t, column);
        return t;
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    // ── Excel (кнопка оболочки владельца) ──

    public SectionTable? BuildExportTable()
    {
        if (_cur is null || _cur.Count == 0)
            return null;
        var period = $"{_curRange.From:dd.MM.yyyy} — {_curRange.To:dd.MM.yyyy}";
        var withCompare = _compare != CompareBase.None && _prev is not null;
        var som = ReportLabels.Currency;
        if (_tab == "cashiers")
        {
            var rows = CashierRows(_cur, true);
            var prevRows = withCompare ? CashierRows(_prev!, false).ToDictionary(r => r.Cashier, StringComparer.CurrentCultureIgnoreCase) : null;
            var columns = new List<XlColumn>
            {
                new(ReportLabels.Cashier),
                new(ReportLabels.WithUnit(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), som), XlKind.Money, XlTotal.Sum),
                new(Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), XlKind.Integer, XlTotal.Sum),
                new(ReportLabels.WithUnit(Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), som), XlKind.Money),
                new(Tr.T("Возвратов, шт.", "Кайтаруулар, даана", "Returns, pcs", "İade, adet", "Qaytarishlar, dona"), XlKind.Integer, XlTotal.Sum),
                new(ReportLabels.WithUnit(Tr.T("Сумма возвратов", "Кайтаруулар суммасы", "Returns amount", "İade tutarı", "Qaytarishlar summasi"), som), XlKind.Money, XlTotal.Sum),
                new(ReportLabels.WithUnit(Tr.T("Скидки", "Арзандатуулар", "Discounts", "İndirimler", "Chegirmalar"), som), XlKind.Money, XlTotal.Sum),
                new(Tr.T("Удалено из чека, раз", "Чектен өчүрүлгөн, жолу", "Removed from receipt, times", "Fişten silinen, kez", "Chekdan o'chirilgan, marta"), XlKind.Integer, XlTotal.Sum),
            };
            if (withCompare)
            {
                columns.Add(new XlColumn(ReportLabels.WithUnit(Tr.T("Выручка в прошлом периоде", "Мурунку мезгилдеги түшүм", "Revenue in the previous period", "Önceki dönem cirosu", "Oldingi davr tushumi"), som), XlKind.Money, XlTotal.Sum));
                columns.Add(new XlColumn(Tr.T("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish")));
            }
            var data = rows.Select(r =>
            {
                var line = new List<object?> { r.Cashier, r.Revenue, r.Checks, r.AvgCheck, r.Returns, r.ReturnsSum, r.DiscountSum, r.Errors };
                if (withCompare)
                {
                    var had = prevRows!.TryGetValue(r.Cashier, out var pr);
                    line.Add(had ? pr!.Revenue : 0m);
                    line.Add(Arrow(had ? DeltaPercent(r.Revenue, pr!.Revenue) : null));
                }
                return line.ToArray();
            }).ToList();
            return new SectionTable(Tr.T("Отчёт по кассирам", "Кассирлер боюнча отчёт", "Report by cashier", "Kasiyer raporu", "Kassirlar bo'yicha hisobot"), period, columns, data);
        }

        var map = BuildHeatMap(_cur);
        var cols = new List<XlColumn> { new(Tr.T("День недели", "Апта күнү", "Weekday", "Gün", "Hafta kuni")) };
        for (var h = 0; h < 24; h++)
            cols.Add(new XlColumn($"{h:00}:00", XlKind.Money, XlTotal.Sum, 10));
        cols.Add(new XlColumn(ReportLabels.WithUnit(ReportLabels.Total, som), XlKind.Money, XlTotal.Sum));
        cols.Add(new XlColumn(Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), XlKind.Integer, XlTotal.Sum));
        var heatRows = new List<object?[]>();
        for (var d = 0; d < 7; d++)
        {
            var line = new List<object?> { WeekdayLong(d) };
            for (var h = 0; h < 24; h++)
                line.Add(map.Revenue[d, h]);
            line.Add(map.DayRevenue(d));
            line.Add(map.DayChecks(d));
            heatRows.Add(line.ToArray());
        }
        return new SectionTable(Tr.T("Выручка по часам и дням недели", "Сааттар жана апта күндөрү боюнча түшүм", "Revenue by hour and weekday", "Saat ve güne göre ciro", "Soat va hafta kunlari bo'yicha tushum"), period, cols, heatRows);
    }
}
