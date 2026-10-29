using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-28, запрос владельца «Общая аналитика по бонусам (десктоп)»: раздел «Бонусы» группы «Отчёты» программы владельца.
/// За период (сегодня / 7 дней / этот месяц / свой период): остаток бонусов у клиентов (обязательство магазина), сколько
/// списано бонусами и какую долю продаж они закрыли, выручка и средний чек с карточкой клиента и без неё, активные клиенты с
/// бонусами, топ-10 по накоплению и по покупкам, динамика по дням. Выгрузка — Excel, Word и CSV.
/// Чего сервер не отдаёт («начислено», «сгорело», средний чек с оплатой бонусами, траты бонусов по клиентам), показано как
/// «нет данных на сервере» — цифры не выдуманы (потребность — Б-99 в ТЗ Умару). Выручка — «как в Сводке» (ServerSalesSummary).
/// Окно собрано в коде; цвета — из темы, диаграммы — BarChartRenderer / ChartPalette, как в «Сводке».</summary>
public sealed class BonusAnalyticsWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _periodLabel = new();
    private readonly CalendarDatePicker _fromPicker = new() { Width = 130, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center, SelectedDateFormat = CalendarDatePickerFormat.Short };
    private readonly CalendarDatePicker _toPicker = new() { Width = 130, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center, SelectedDateFormat = CalendarDatePickerFormat.Short };
    private readonly StackPanel _customRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false };
    private readonly Button _refresh, _excel, _word, _csv;
    private string _period = "month";
    private CancellationTokenSource? _cts;
    private bool _syncingPickers;

    /// <summary>Последний рассчитанный отчёт — его же берёт выгрузка (одни цифры на экране и в файле).</summary>
    public BonusAnalyticsData? Data { get; private set; }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    public BonusAnalyticsWindow()
    {
        Title = Tr.T("Бонусы", "Бонустар", "Bonuses", "Bonuslar", "Bonuslar");
        Width = 1296;
        Height = 879;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        _refresh = MakeButton(Tr.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"), primary: true);
        _excel = MakeButton("Excel", primary: false);
        _word = MakeButton("Word", primary: false);
        _csv = MakeButton("CSV", primary: false);
        _refresh.Click += (_, _) => _ = ReloadAsync();
        _excel.Click += async (_, _) => await ExportAsync("xlsx");
        _word.Click += async (_, _) => await ExportAsync("docx");
        _csv.Click += async (_, _) => await ExportAsync("csv");

        _fromPicker.SelectedDateChanged += OnCustomDatesChanged;
        _toPicker.SelectedDateChanged += OnCustomDatesChanged;
        _customRow.Children.Add(_fromPicker);
        _customRow.Children.Add(new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center });
        _customRow.Children.Add(_toPicker);

        // Шапка: период (сегменты + свои даты) слева, действия справа; узко — переносится (WrapPanel).
        var left = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var segments = UiKit.WithPeriodLabel(UiKit.Segment(_periods), _periodLabel);
        segments.Margin = new Thickness(0, 0, 12, 6);
        _customRow.Margin = new Thickness(0, 0, 12, 6);
        left.Children.Add(segments);
        left.Children.Add(_customRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _excel, _word, _csv, _refresh } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumn(actions, 1);
        header.Children.Add(left);
        header.Children.Add(actions);

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.Margin = new Thickness(0, 0, 0, 8);
        _loading.Bind(ProgressBar.ForegroundProperty, _loading.GetResourceObservable("BrushAccent"));

        var scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 12),
        };
        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(_loading, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(scroll, 3);
        root.Children.Add(header);
        root.Children.Add(_loading);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        Content = root;

        BuildPeriods();
        Opened += (_, _) => _ = ReloadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    // ------------------------------------------------------------------ период

    private (DateTime From, DateTime To) Range()
    {
        var today = DateTime.Today;
        return _period switch
        {
            "today" => (today, today),
            "week" => (PeriodRange.WeekFrom(today), today),
            "custom" => CustomRange(),
            _ => (PeriodRange.MonthFrom(today), today),
        };
    }

    private (DateTime From, DateTime To) CustomRange()
    {
        var from = (_fromPicker.SelectedDate ?? DateTime.Today.AddDays(-29)).Date;
        var to = (_toPicker.SelectedDate ?? DateTime.Today).Date;
        return from <= to ? (from, to) : (to, from);
    }

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        void Add(string key, string text)
        {
            var b = UiKit.Seg(text, key == _period);
            b.MinHeight = 44;
            b.Click += (_, _) =>
            {
                _period = key;
                if (key == "custom" && (_fromPicker.SelectedDate == null || _toPicker.SelectedDate == null))
                {
                    _syncingPickers = true;
                    _fromPicker.SelectedDate = DateTime.Today.AddDays(-29);
                    _toPicker.SelectedDate = DateTime.Today;
                    _syncingPickers = false;
                }
                BuildPeriods();
                _ = ReloadAsync();
            };
            _periods.Children.Add(b);
        }

        Add("today", Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"));
        Add("week", Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"));
        Add("month", Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy"));
        Add("custom", Tr.T("Свой период", "Өз мезгилим", "Custom period", "Özel dönem", "O'z davrim"));
        _customRow.IsVisible = _period == "custom";
        var (f, t) = Range();
        _periodLabel.Text = PeriodRange.Label(f, t);
    }

    private void OnCustomDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPickers || _period != "custom" || _fromPicker.SelectedDate == null || _toPicker.SelectedDate == null)
            return;
        var (f, t) = Range();
        _periodLabel.Text = PeriodRange.Label(f, t);
        _ = ReloadAsync();
    }

    private Button MakeButton(string text, bool primary)
    {
        var b = new Button { Content = text, MinHeight = 44, MinWidth = 44, Padding = new Thickness(14, 0) };
        b.Classes.Add(primary ? "sec-primary" : "sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    // ------------------------------------------------------------------ загрузка

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var (from, to) = Range();
        _periodLabel.Text = PeriodRange.Label(from, to);
        _status.Text = Tr.T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
        _loading.IsVisible = true;
        SetActionsEnabled(false);
        try
        {
            // Выручка и чеки «как в Сводке» — тот же запрос, что у Сводки и «Продаж и финансов».
            var summary = await ServerSalesSummary.FetchAsync(from, to, cts.Token).ConfigureAwait(true);
            var data = await BonusAnalyticsData.BuildAsync(from, to, App.SalesApi, App.ShiftApi, App.GetRequiredService<IClientsApiService>(),
                summary?.Revenue is { } rv ? (double)rv : null, summary?.Transactions, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            Data = data;
            _body.Children.Clear();
            if (data is null)
            {
                _status.Text = Tr.T("Нет связи с сервером NurCRM — аналитика бонусов считается только по серверу, чтобы цифры совпадали со «Сводкой». Проверьте интернет и нажмите «Обновить».",
                    "NurCRM серверине байланыш жок — бонустар аналитикасы «Жыйынтык» менен дал келиши үчүн сервер боюнча гана эсептелет. Интернетти текшериңиз да «Жаңыртуу» басыңыз.",
                    "No connection to the NurCRM server — bonus analytics is calculated from the server only, so the figures match “Summary”. Check the internet and press “Refresh”.",
                    "NurCRM sunucusuna bağlanılamadı — bonus analizi yalnızca sunucudan hesaplanır, böylece rakamlar «Özet» ile aynı olur. İnterneti kontrol edip «Yenile»ye basın.",
                    "NurCRM serveriga ulanib bo'lmadi — bonuslar tahlili «Xulosa» bilan mos bo'lishi uchun faqat serverdan hisoblanadi. Internetni tekshirib, «Yangilash»ni bosing.");
                return;
            }
            _status.Text = "";
            ShowReport(data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Аналитика бонусов: {ex}", "WARNING");
            _status.Text = Tr.T("Не удалось посчитать: ", "Эсептөө мүмкүн болгон жок: ", "Could not calculate: ", "Hesaplanamadı: ", "Hisoblab bo'lmadi: ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _loading.IsVisible = false;
                SetActionsEnabled(true);
            }
        }
    }

    private void SetActionsEnabled(bool on)
    {
        _refresh.IsEnabled = on;
        _excel.IsEnabled = on;
        _word.IsEnabled = on;
        _csv.IsEnabled = on;
    }

    // ------------------------------------------------------------------ вид

    private TextBlock Soft(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static string Money(double v) => v.ToString("N0", Ru) + " " + Som;
    private static string Pct(double v) => v.ToString("0.#", Ru) + " %";
    private static string NoData => BonusAnalyticsData.NoDataText;

    private Border Tile(string label, string value, string? note = null, bool noData = false)
    {
        var tile = UiKit.Kpi(label, value);
        if (tile.Child is StackPanel sp)
        {
            if (noData)
                sp.Children.Add(Soft(Tr.T("сервер пока не отдаёт — см. Б-99 в ТЗ", "сервер азырынча бербейт — ТЗдеги Б-99", "the server does not provide it yet — see B-99 in the spec", "sunucu henüz vermiyor — şartnamedeki B-99", "server hozircha bermaydi — TZdagi B-99"), 12));
            else if (!string.IsNullOrEmpty(note))
                sp.Children.Add(Soft(note, 12));
        }
        return tile;
    }

    private Control Card(string title, params Control[] rows)
    {
        var head = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(head);
        foreach (var r in rows)
            stack.Children.Add(r);
        return SectionCards.Group(this, stack);
    }

    private void ShowReport(BonusAnalyticsData d)
    {
        _body.Children.Clear();
        var revenue = d.SummaryRevenue ?? (d.SalesLoaded ? d.Revenue : (double?)null);
        var receipts = d.SummaryReceipts ?? (d.SalesLoaded ? d.Receipts : (int?)null);
        var redeemedKnown = d.RedeemedSource != BonusAnalyticsData.RedeemSource.None;

        // ---- пояснение настройки магазина
        var settings = new List<string>();
        if (d.PointsEnabled == false)
            settings.Add(Tr.T("Бонусы выключены в настройке магазина — новые баллы не начисляются.", "Бонустар дүкөндүн жөндөөсүндө өчүрүлгөн — жаңы упайлар эсептелбейт.", "Bonuses are switched off in the shop settings — new points are not accrued.", "Bonuslar mağaza ayarında kapalı — yeni puan kazanılmaz.", "Bonuslar do'kon sozlamasida o'chirilgan — yangi ballar hisoblanmaydi."));
        else if (d.PointsPercent is { } pp)
            settings.Add(Tr.T($"Начисление: {pp:0.##} % от суммы чека (настройка магазина). 1 бонус = 1 сом.", $"Эсептөө: чектин суммасынан {pp:0.##} % (дүкөндүн жөндөөсү). 1 бонус = 1 сом.", $"Accrual: {pp:0.##} % of the receipt (shop setting). 1 bonus = 1 som.", $"Kazanım: fiş tutarının %{pp:0.##}'i (mağaza ayarı). 1 bonus = 1 som.", $"Hisoblash: chek summasining {pp:0.##} % (do'kon sozlamasi). 1 bonus = 1 so'm."));
        if (settings.Count > 0)
            _body.Children.Add(Soft(string.Join(" ", settings)));

        // ---- плитки
        var tiles = new KpiTileGrid { MinTileWidth = 220 };
        tiles.Children.Add(d.ClientsLoaded
            ? Tile(Tr.T("Остаток бонусов у клиентов", "Кардарлардагы бонус калдыгы", "Bonus balance held by customers", "Müşterilerdeki bonus bakiyesi", "Mijozlardagi bonus qoldig'i"), Money(d.BonusBalanceTotal),
                Tr.T("обязательство магазина перед клиентами", "дүкөндүн кардарлар алдындагы милдеттенмеси", "the shop's liability to customers", "mağazanın müşterilere yükümlülüğü", "do'konning mijozlar oldidagi majburiyati"))
            : Tile(Tr.T("Остаток бонусов у клиентов", "Кардарлардагы бонус калдыгы", "Bonus balance held by customers", "Müşterilerdeki bonus bakiyesi", "Mijozlardagi bonus qoldig'i"), NoData, noData: true));
        tiles.Children.Add(d.ClientsLoaded
            ? Tile(Tr.T("Клиентов с бонусами", "Бонусу бар кардарлар", "Customers with bonuses", "Bonuslu müşteriler", "Bonusli mijozlar"), d.ClientsWithBonus.ToString("N0", Ru),
                Tr.T($"из {d.ClientsTotal:N0} клиентов", $"{d.ClientsTotal:N0} кардардан", $"of {d.ClientsTotal:N0} customers", $"{d.ClientsTotal:N0} müşteriden", $"{d.ClientsTotal:N0} mijozdan"))
            : Tile(Tr.T("Клиентов с бонусами", "Бонусу бар кардарлар", "Customers with bonuses", "Bonuslu müşteriler", "Bonusli mijozlar"), NoData, noData: true));
        tiles.Children.Add(d.ClientsLoaded && d.SalesLoaded && d.ClientFieldKnown
            ? Tile(Tr.T("Активных клиентов с бонусами", "Бонусу бар активдүү кардарлар", "Active customers with bonuses", "Bonuslu aktif müşteriler", "Bonusli faol mijozlar"), d.ActiveWithBonus.ToString("N0", Ru),
                Tr.T("купили за период и у них есть бонусы", "мезгилде сатып алган жана бонусу бар", "bought in the period and hold bonuses", "dönemde alışveriş yaptı ve bonusu var", "davrda xarid qilgan va bonusi bor"))
            : Tile(Tr.T("Активных клиентов с бонусами", "Бонусу бар активдүү кардарлар", "Active customers with bonuses", "Bonuslu aktif müşteriler", "Bonusli faol mijozlar"), NoData, noData: true));
        tiles.Children.Add(redeemedKnown
            ? Tile(Tr.T("Списано бонусами (скидка в сомах)", "Бонус менен төлөндү (сом менен арзандатуу)", "Paid with bonuses (discount in som)", "Bonusla ödenen (som indirimi)", "Bonus bilan to'langan (so'mda chegirma)"), Money(d.Redeemed),
                d.RedeemedSource == BonusAnalyticsData.RedeemSource.Server
                    ? Tr.T($"отчёты смен сервера ({d.ShiftsChecked})", $"сервердин смена отчёттору ({d.ShiftsChecked})", $"server shift reports ({d.ShiftsChecked})", $"sunucu vardiya raporları ({d.ShiftsChecked})", $"server smena hisobotlari ({d.ShiftsChecked})")
                    : Tr.T("журнал кассы на этом компьютере (только эта касса)", "бул компьютердеги касса журналы (бул касса гана)", "till log on this computer (this till only)", "bu bilgisayardaki kasa günlüğü (yalnızca bu kasa)", "shu kompyuterdagi kassa jurnali (faqat shu kassa)"))
            : Tile(Tr.T("Списано бонусами (скидка в сомах)", "Бонус менен төлөндү (сом менен арзандатуу)", "Paid with bonuses (discount in som)", "Bonusla ödenen (som indirimi)", "Bonus bilan to'langan (so'mda chegirma)"),
                d.ShiftsChecked > 0 ? Money(0) : NoData, noData: d.ShiftsChecked == 0,
                note: d.ShiftsChecked > 0 ? Tr.T("за период бонусами не платили", "мезгилде бонус менен төлөшкөн жок", "no bonus payments in the period", "dönemde bonusla ödeme yok", "davrda bonus bilan to'lanmagan") : null));
        tiles.Children.Add(d.RedeemedSharePct is { } share
            ? Tile(Tr.T("Доля продаж, закрытая бонусами", "Бонус менен жабылган сатуулардын үлүшү", "Share of sales covered by bonuses", "Bonusla karşılanan satış payı", "Bonus bilan yopilgan sotuv ulushi"), Pct(share),
                Tr.T("списано / (выручка + списано)", "төлөнгөн / (түшүм + төлөнгөн)", "paid with bonuses / (revenue + paid with bonuses)", "bonusla ödenen / (ciro + bonusla ödenen)", "bonus bilan to'langan / (tushum + bonus bilan to'langan)"))
            : Tile(Tr.T("Доля продаж, закрытая бонусами", "Бонус менен жабылган сатуулардын үлүшү", "Share of sales covered by bonuses", "Bonusla karşılanan satış payı", "Bonus bilan yopilgan sotuv ulushi"), d.ShiftsChecked > 0 && revenue > 0 ? Pct(0) : NoData, noData: d.ShiftsChecked == 0));
        tiles.Children.Add(Tile(Tr.T("Начислено бонусов", "Эсептелген бонустар", "Bonuses earned", "Kazanılan bonus", "Hisoblangan bonuslar"), NoData, noData: true));
        tiles.Children.Add(Tile(Tr.T("Сгорело бонусов", "Күйүп кеткен бонустар", "Bonuses expired", "Yanan bonuslar", "Kuygan bonuslar"), NoData, noData: true));
        tiles.Children.Add(Tile(Tr.T("Выручка (как в «Сводке»)", "Түшүм («Жыйынтыктагыдай»)", "Revenue (as in “Summary”)", "Ciro («Özet» ile aynı)", "Tushum («Xulosa»dagidek)"),
            revenue is { } rv ? Money(rv) : "—", receipts is { } rc ? Tr.T($"чеков: {rc:N0}", $"чектер: {rc:N0}", $"receipts: {rc:N0}", $"fiş: {rc:N0}", $"cheklar: {rc:N0}") : null));
        if (d.SalesLoaded && d.ClientFieldKnown)
        {
            tiles.Children.Add(Tile(Tr.T("Средний чек с карточкой клиента", "Кардар карточкасы бар орточо чек", "Average receipt with a customer card", "Müşteri kartlı ortalama fiş", "Mijoz kartochkali o'rtacha chek"), d.ClientReceipts > 0 ? Money(d.AvgReceiptClient) : "—",
                Tr.T($"чеков: {d.ClientReceipts:N0} ({d.ClientShareReceiptsPct:0.#} %)", $"чектер: {d.ClientReceipts:N0} ({d.ClientShareReceiptsPct:0.#} %)", $"receipts: {d.ClientReceipts:N0} ({d.ClientShareReceiptsPct:0.#} %)", $"fiş: {d.ClientReceipts:N0} ({d.ClientShareReceiptsPct:0.#} %)", $"cheklar: {d.ClientReceipts:N0} ({d.ClientShareReceiptsPct:0.#} %)")));
            tiles.Children.Add(Tile(Tr.T("Средний чек без карточки клиента", "Кардар карточкасы жок орточо чек", "Average receipt without a customer card", "Müşteri kartsız ortalama fiş", "Mijoz kartochkasiz o'rtacha chek"), d.Receipts - d.ClientReceipts > 0 ? Money(d.AvgReceiptNoClient) : "—",
                Tr.T($"чеков: {d.Receipts - d.ClientReceipts:N0}", $"чектер: {d.Receipts - d.ClientReceipts:N0}", $"receipts: {d.Receipts - d.ClientReceipts:N0}", $"fiş: {d.Receipts - d.ClientReceipts:N0}", $"cheklar: {d.Receipts - d.ClientReceipts:N0}")));
        }
        tiles.Children.Add(Tile(Tr.T("Средний чек с оплатой бонусами", "Бонус менен төлөнгөн орточо чек", "Average receipt paid with bonuses", "Bonusla ödenen ortalama fiş", "Bonus bilan to'langan o'rtacha chek"), NoData, noData: true));
        _body.Children.Add(tiles);

        // ---- сверка со «Сводкой»
        if (d.SalesLoaded)
        {
            if (d.SalesTruncated)
                _body.Children.Add(Soft(Tr.T("Период очень большой: список чеков загружен не весь — доли и средние чеки по клиентам приблизительные. Выручка и чеки — из «Сводки», полные.",
                    "Мезгил өтө чоң: чектердин тизмеси толук жүктөлгөн жок — кардарлар боюнча үлүштөр жана орточо чектер болжолдуу. Түшүм жана чектер — «Жыйынтыктан», толук.",
                    "The period is very large: the receipt list is not fully loaded — customer shares and averages are approximate. Revenue and receipts come from “Summary” and are complete.",
                    "Dönem çok büyük: fiş listesi tam yüklenmedi — müşteri payları ve ortalamalar yaklaşıktır. Ciro ve fişler «Özet»ten gelir, eksiksizdir.",
                    "Davr juda katta: cheklar ro'yxati to'liq yuklanmadi — mijozlar ulushi va o'rtachalar taxminiy. Tushum va cheklar «Xulosa»dan, to'liq.")));
            else if (d.MatchesSummary == false && d.SummaryRevenue is { } sr)
                _body.Children.Add(Soft(Tr.T($"Внимание: выручка по списку чеков ({Money(d.Revenue)}) отличается от «Сводки» ({Money(sr)}). Доли по клиентам считаются по списку чеков.",
                    $"Көңүл буруңуз: чектер тизмеси боюнча түшүм ({Money(d.Revenue)}) «Жыйынтыктан» ({Money(sr)}) айырмаланат. Кардарлар боюнча үлүштөр чектер тизмеси боюнча эсептелет.",
                    $"Note: revenue from the receipt list ({Money(d.Revenue)}) differs from “Summary” ({Money(sr)}). Customer shares are calculated from the receipt list.",
                    $"Dikkat: fiş listesi cirosu ({Money(d.Revenue)}) «Özet» ({Money(sr)}) ile farklı. Müşteri payları fiş listesinden hesaplanır.",
                    $"Diqqat: cheklar ro'yxati bo'yicha tushum ({Money(d.Revenue)}) «Xulosa»dan ({Money(sr)}) farq qiladi. Mijozlar ulushi cheklar ro'yxati bo'yicha hisoblanadi.")));
            // 2026-10-29 (приёмка 1.17.64, В-1): плитка «Выручка» берёт число чеков из «Сводки» (569), а «с карточкой» + «без карточки» — из списка (592):
            // у сервера разные правила подсчёта. Теперь расхождение названо прямо, а не выглядит как арифметическая ошибка.
            else if (!d.SalesTruncated && d.SummaryReceipts is { } sumRc && sumRc != d.Receipts)
                _body.Children.Add(Soft(Tr.T($"Число чеков в списке ({d.Receipts:N0}) отличается от «Сводки» ({sumRc:N0}): «с карточкой» и «без карточки» считаются по списку чеков.",
                    $"Чектер тизмесиндеги чектердин саны ({d.Receipts:N0}) «Жыйынтыктан» ({sumRc:N0}) айырмаланат: «карточкасы бар» жана «карточкасы жок» чектер тизмеси боюнча эсептелет.",
                    $"The number of receipts in the list ({d.Receipts:N0}) differs from “Summary” ({sumRc:N0}): “with a card” and “without a card” are counted from the receipt list.",
                    $"Listedeki fiş sayısı ({d.Receipts:N0}) «Özet» ({sumRc:N0}) ile farklı: «kartlı» ve «kartsız» fişler listeden hesaplanır.",
                    $"Ro'yxatdagi cheklar soni ({d.Receipts:N0}) «Xulosa»dan ({sumRc:N0}) farq qiladi: «kartochkali» va «kartochkasiz» cheklar ro'yxat bo'yicha hisoblanadi.")));
            if (!d.ClientFieldKnown)
                _body.Children.Add(Soft(Tr.T("В списке чеков сервера нет клиента — доли и средние чеки «с карточкой» и «без» — нет данных на сервере.",
                    "Сервердин чектер тизмесинде кардар жок — карточкасы бар жана жок үлүштөр жана орточо чектер — серверде маалымат жок.",
                    "The server's receipt list has no customer — shares and averages “with” and “without” a card: no data on the server.",
                    "Sunucunun fiş listesinde müşteri yok — kartlı ve kartsız paylar ve ortalamalar: sunucuda veri yok.",
                    "Server cheklar ro'yxatida mijoz yo'q — kartochkali va kartochkasiz ulushlar va o'rtachalar: serverda ma'lumot yo'q.")));
        }
        else
            _body.Children.Add(Soft(Tr.T("Список чеков с сервера не получен — доли и средние чеки по клиентам не показаны.", "Сервердин чектер тизмеси алынган жок — кардарлар боюнча үлүштөр жана орточо чектер көрсөтүлгөн жок.", "The receipt list was not received — customer shares and averages are not shown.", "Fiş listesi alınamadı — müşteri payları ve ortalamalar gösterilmiyor.", "Cheklar ro'yxati olinmadi — mijozlar ulushi va o'rtachalar ko'rsatilmadi.")));

        // ---- динамика по дням (временной ряд — как в «Сводке»)
        var buckets = d.Buckets();
        if (buckets.Any(b => b.Revenue > 0))
        {
            var chart = new StackPanel { Spacing = 4 };
            BarChartRenderer.RenderTimeSeriesBars(chart, buckets.Select(b => (b.Label, b.Revenue, Money(b.Revenue))).ToList(),
                currentIndex: _period == "today" || Range().To != DateTime.Today ? -1 : buckets.Count - 1);
            _body.Children.Add(Card(Tr.T("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum"), chart));
        }
        if (buckets.Any(b => b.Redeemed is > 0))
        {
            var chart = new StackPanel { Spacing = 4 };
            BarChartRenderer.RenderTimeSeriesBars(chart, buckets.Select(b => (b.Label, b.Redeemed ?? 0, Money(b.Redeemed ?? 0))).ToList());
            _body.Children.Add(Card(Tr.T("Списано бонусами по дням", "Күндөр боюнча бонус менен төлөнгөн", "Paid with bonuses by day", "Günlük bonusla ödenen", "Kunlar bo'yicha bonus bilan to'langan"), chart,
                Soft(Tr.T("День — дата открытия смены.", "Күн — смена ачылган күн.", "The day is the shift opening date.", "Gün, vardiyanın açılış tarihidir.", "Kun — smena ochilgan sana."), 12)));
        }

        // ---- структура (круговые, цвета по ChartPalette)
        if (d.SalesLoaded && d.ClientFieldKnown && d.Revenue > 0)
        {
            var pie = new StackPanel { Spacing = 4 };
            BarChartRenderer.RenderPie(pie, new List<(string, double, string)>
            {
                (Tr.T("С карточкой клиента", "Кардар карточкасы менен", "With a customer card", "Müşteri kartlı", "Mijoz kartochkasi bilan"), d.ClientRevenue, Money(d.ClientRevenue)),
                (Tr.T("Без карточки клиента", "Кардар карточкасы жок", "Without a customer card", "Müşteri kartsız", "Mijoz kartochkasiz"), Math.Max(0, d.Revenue - d.ClientRevenue), Money(Math.Max(0, d.Revenue - d.ClientRevenue))),
            });
            _body.Children.Add(Card(Tr.T("Выручка: с карточкой клиента и без", "Түшүм: кардар карточкасы менен жана жок", "Revenue: with and without a customer card", "Ciro: müşteri kartlı ve kartsız", "Tushum: mijoz kartochkasi bilan va kartochkasiz"), pie));
        }
        if (redeemedKnown && d.Redeemed > 0)
        {
            var revenueNow = revenue ?? d.Revenue;
            var pie = new StackPanel { Spacing = 4 };
            BarChartRenderer.RenderPie(pie, new List<(string, double, string)>
            {
                (Tr.T("Деньгами", "Акча менен", "Paid in money", "Parayla", "Pul bilan"), revenueNow, Money(revenueNow)),
                (Tr.T("Бонусами", "Бонус менен", "Paid with bonuses", "Bonusla", "Bonus bilan"), d.Redeemed, Money(d.Redeemed)),
            });
            _body.Children.Add(Card(Tr.T("Как оплачены продажи: деньгами и бонусами", "Сатуулар кантип төлөндү: акча жана бонус менен", "How sales were paid: money and bonuses", "Satışlar nasıl ödendi: para ve bonus", "Sotuvlar qanday to'langan: pul va bonus"), pie));
        }

        // ---- топ-10
        if (d.TopBalances.Count > 0)
        {
            var chart = new StackPanel { Spacing = 4 };
            BarChartRenderer.Render(chart, d.TopBalances.Select(c => (Label(c), c.Balance, Money(c.Balance))).ToList());
            _body.Children.Add(Card(Tr.T("Топ-10 клиентов по накоплению (остаток бонусов)", "Топтоо боюнча топ-10 кардар (бонус калдыгы)", "Top 10 customers by accumulated bonuses", "Biriken bonusa göre ilk 10 müşteri", "To'plangan bonus bo'yicha top-10 mijoz"), chart));
        }
        else if (d.ClientsLoaded)
            _body.Children.Add(Card(Tr.T("Топ-10 клиентов по накоплению", "Топтоо боюнча топ-10 кардар", "Top 10 customers by accumulation", "Birikime göre ilk 10 müşteri", "To'plash bo'yicha top-10 mijoz"),
                Soft(Tr.T("Ни у одного клиента нет бонусов.", "Эч бир кардарда бонус жок.", "No customer has bonuses.", "Hiçbir müşterinin bonusu yok.", "Hech bir mijozda bonus yo'q."))));
        if (d.TopBuyers.Count > 0)
        {
            var chart = new StackPanel { Spacing = 4 };
            BarChartRenderer.Render(chart, d.TopBuyers.Select(c => (Label(c), c.Spent, Money(c.Spent) + " · " + c.Receipts)).ToList());
            _body.Children.Add(Card(Tr.T("Топ-10 клиентов по тратам (покупки за период)", "Сарптоо боюнча топ-10 кардар (мезгилдеги сатып алуулар)", "Top 10 customers by spending (purchases in the period)", "Harcamaya göre ilk 10 müşteri (dönemdeki alışverişler)", "Sarf bo'yicha top-10 mijoz (davrdagi xaridlar)"), chart,
                Soft(Tr.T("Траты именно бонусов по клиентам сервер не отдаёт — показаны суммы покупок и число чеков.", "Кардарлар боюнча так бонус сарптоону сервер бербейт — сатып алуу суммалары жана чектердин саны көрсөтүлдү.", "The server does not give bonus spending by customer — purchase totals and receipt counts are shown.", "Sunucu müşteri başına bonus harcamasını vermiyor — alışveriş toplamları ve fiş sayısı gösterilir.", "Server mijozlar bo'yicha bonus sarfini bermaydi — xarid summalari va cheklar soni ko'rsatildi."), 12)));
        }

        // ---- по филиалам (сеть магазинов: бонус общий на компанию, у клиента один bonus_balance)
        RenderBranches(d);

        // ---- пометки
        _body.Children.Add(Soft(Tr.T("Откуда цифры: остаток бонусов — карточки клиентов (1 бонус = 1 сом); выручка и чеки — «Сводка» (оплаченные и частично возвращённые чеки, как в «Продажах и финансах»); «списано бонусами» — отчёты смен сервера по смене, открытой в периоде (нет их — журнал кассы этого компьютера); доли по клиентам — список чеков периода. «Начислено», «сгорело», средний чек с бонусами и траты бонусов по клиентам сервер пока не отдаёт — цифры не выдуманы.",
            "Сандар кайдан: бонус калдыгы — кардарлардын карточкалары (1 бонус = 1 сом); түшүм жана чектер — «Жыйынтык» (төлөнгөн жана жарым-жартылай кайтарылган чектер, «Сатуулар жана каржыдагыдай»); «бонус менен төлөндү» — мезгилде ачылган смена боюнча сервердин смена отчёттору (алар жок болсо — бул компьютердин касса журналы); кардарлар боюнча үлүштөр — мезгилдин чектер тизмеси. «Эсептелди», «күйүп кетти», бонустары бар орточо чек жана кардарлар боюнча бонус сарптоону сервер азырынча бербейт — сандар ойдон чыгарылган эмес.",
            "Where the figures come from: bonus balance — customer cards (1 bonus = 1 som); revenue and receipts — “Summary” (paid and partly returned receipts, as in “Sales & finance”); “paid with bonuses” — server shift reports for shifts opened in the period (otherwise this computer's till log); customer shares — the period's receipt list. “Earned”, “expired”, average receipt with bonuses and bonus spending by customer are not provided by the server yet — nothing is invented.",
            "Rakamlar nereden: bonus bakiyesi — müşteri kartları (1 bonus = 1 som); ciro ve fişler — «Özet» (ödenmiş ve kısmen iade edilmiş fişler, «Satışlar ve finans» gibi); «bonusla ödenen» — dönemde açılan vardiyalar için sunucu vardiya raporları (yoksa bu bilgisayarın kasa günlüğü); müşteri payları — dönemin fiş listesi. «Kazanılan», «yanan», bonuslu ortalama fiş ve müşteri başına bonus harcaması henüz sunucudan gelmiyor — rakam uydurulmaz.",
            "Raqamlar qayerdan: bonus qoldig'i — mijoz kartochkalari (1 bonus = 1 so'm); tushum va cheklar — «Xulosa» (to'langan va qisman qaytarilgan cheklar, «Sotuvlar va moliya»dagidek); «bonus bilan to'langan» — davrda ochilgan smenalar uchun server smena hisobotlari (bo'lmasa — shu kompyuterning kassa jurnali); mijozlar ulushi — davrning cheklar ro'yxati. «Hisoblangan», «kuygan», bonusli o'rtacha chek va mijozlar bo'yicha bonus sarfini server hozircha bermaydi — raqam to'qilmagan."), 12));
    }

    /// <summary>2026-10-28 (сеть магазинов): блок «По филиалам». Бонус хранится у клиента и общий на компанию, поэтому филиалы делят
    /// «начислили в X — потратили в Y». Что сервер даёт сегодня: филиал чека (выручка, чеки) и филиал смены (списано бонусами).
    /// Филиала самой бонусной операции (где начислили) нет — «начислено по филиалам» и взаиморасчёты: «нет данных на сервере» (Б-99).
    /// Правило «расход бонуса несёт: где потратили / где начислили» — настройка в своём JSON (BonusBranchRule), по умолчанию «где потратили».</summary>
    private void RenderBranches(BonusAnalyticsData d)
    {
        var whereSpent = BonusBranchRule.BearsWhereSpent;
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Button Choice(string text, bool spent)
        {
            var b = UiKit.Seg(text, whereSpent == spent);
            b.MinHeight = 44;
            b.Click += (_, _) =>
            {
                BonusBranchRule.Save(spent);
                if (Data is { } data)
                    ShowReport(data);
            };
            return b;
        }
        seg.Children.Add(Choice(Tr.T("Где потратили", "Кайда сарпташса", "Where spent", "Harcandığı yer", "Sarflangan joy"), true));
        seg.Children.Add(Choice(Tr.T("Где начислили", "Кайда эсептелсе", "Where earned", "Kazanıldığı yer", "Hisoblangan joy"), false));
        var rule = new StackPanel { Spacing = 4 };
        var ruleLabel = Soft(Tr.T("Расход бонуса несёт магазин (филиал):", "Бонус чыгымын дүкөн (филиал) көтөрөт:", "The bonus cost is borne by the shop (branch):", "Bonus giderini şu mağaza (şube) üstlenir:", "Bonus xarajatini do'kon (filial) ko'taradi:"));
        rule.Children.Add(ruleLabel);
        rule.Children.Add(UiKit.Segment(seg));

        var rows = new List<Control> { rule };
        if (d.BranchKnown)
        {
            rows.Add(SectionCards.LinesTable(this,
                d.Branches.Select(b => (b.Name, b.Receipts.ToString("N0", Ru), Money(b.Revenue), b.Redeemed is { } r ? Money(r) : "—")).ToList(),
                Tr.T("Филиал", "Филиал", "Branch", "Şube", "Filial"), Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"),
                Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"),
                whereSpent
                    ? Tr.T("Потрачено бонусами", "Бонус менен сарпталган", "Spent in bonuses", "Bonusla harcanan", "Bonus bilan sarflangan")
                    : Tr.T("Ему должны начислители", "Ага эсептегендер карыздар", "Owed to it by earners", "Kazandıran şubeler borçlu", "Unga hisoblaganlar qarzdor")));
            rows.Add(Soft(whereSpent
                ? Tr.T("По правилу «где потратили» расход бонусов несёт филиал, в котором клиент ими заплатил, — последний столбец.",
                    "«Кайда сарптаса» эрежеси боюнча бонус чыгымын клиент төлөгөн филиал көтөрөт — акыркы мамыча.",
                    "By the “where spent” rule the branch where the customer paid with bonuses bears the cost — the last column.",
                    "«Harcandığı yer» kuralına göre bonus giderini müşterinin bonusla ödediği şube üstlenir — son sütun.",
                    "«Sarflangan joy» qoidasiga ko'ra bonus xarajatini mijoz bonus bilan to'lagan filial ko'taradi — oxirgi ustun.")
                : Tr.T("По правилу «где начислили» расход несёт филиал-начислитель: он должен филиалу, где бонусы потратили, сумму потраченных там бонусов (1 бонус = 1 сом) — последний столбец. Какие именно филиалы-начислители должны: " + BonusAnalyticsData.NoDataText + " — у бонусной операции нет филиала начисления (Б-99).",
                    "«Кайда эсептесе» эрежеси боюнча чыгымды бонус эсептеген филиал көтөрөт: ал бонус сарпталган филиалга ошол жерде сарпталган бонустардын суммасын берет (1 бонус = 1 сом) — акыркы мамыча. Кайсы филиалдар карыздар: " + BonusAnalyticsData.NoDataText + " — бонус операциясында эсептелген филиал жок (Б-99).",
                    "By the “where earned” rule the earning branch bears the cost: it owes the branch where bonuses were spent the amount spent there (1 bonus = 1 som) — the last column. Which earning branches owe it: " + BonusAnalyticsData.NoDataText + " — a bonus operation has no earning branch (B-99).",
                    "«Kazanıldığı yer» kuralına göre gideri bonusu kazandıran şube üstlenir: bonusların harcandığı şubeye orada harcanan tutarı borçludur (1 bonus = 1 som) — son sütun. Hangi şubelerin borçlu olduğu: " + BonusAnalyticsData.NoDataText + " — bonus işleminde kazanılan şube yok (B-99).",
                    "«Hisoblangan joy» qoidasiga ko'ra xarajatni bonus hisoblangan filial ko'taradi: u bonus sarflangan filialga o'sha yerda sarflangan summani qarzdor (1 bonus = 1 so'm) — oxirgi ustun. Qaysi filiallar qarzdor: " + BonusAnalyticsData.NoDataText + " — bonus amalida hisoblangan filial yo'q (B-99).")));
        }
        else
            rows.Add(Soft(Tr.T("Филиалы чеков и смен: " + BonusAnalyticsData.NoDataText + " — в ответе нет названия филиала (Б-99).",
                "Чектердин жана смендердин филиалдары: " + BonusAnalyticsData.NoDataText + " — жоопто филиалдын аталышы жок (Б-99).",
                "Branches of receipts and shifts: " + BonusAnalyticsData.NoDataText + " — the response has no branch name (B-99).",
                "Fiş ve vardiya şubeleri: " + BonusAnalyticsData.NoDataText + " — yanıtta şube adı yok (B-99).",
                "Chek va smena filiallari: " + BonusAnalyticsData.NoDataText + " — javobda filial nomi yo'q (B-99).")));

        rows.Add(Line(Tr.T("Начислено в филиале", "Филиалда эсептелген", "Earned in the branch", "Şubede kazanılan", "Filialda hisoblangan"), BonusAnalyticsData.NoDataText));
        rows.Add(Line(Tr.T("Взаиморасчёты: потрачены бонусы, начисленные в другом филиале", "Өз ара эсептешүү: башка филиалда эсептелген бонустар сарпталган", "Settlements: bonuses earned in another branch were spent", "Karşılıklı hesaplaşma: başka şubede kazanılan bonus harcandı", "O'zaro hisob-kitob: boshqa filialda hisoblangan bonuslar sarflangan"), BonusAnalyticsData.NoDataText));
        _body.Children.Add(Card(Tr.T("По филиалам", "Филиалдар боюнча", "By branch", "Şubelere göre", "Filiallar bo'yicha"), rows.ToArray()));
    }

    private static Grid Line(string left, string right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 30 };
        var l = new TextBlock { Text = left, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var r = new TextBlock { Text = right, VerticalAlignment = VerticalAlignment.Center };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushText"));
        r.Bind(TextBlock.ForegroundProperty, r.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumn(r, 1);
        g.Children.Add(l);
        g.Children.Add(r);
        return g;
    }

    private static string Label(BonusAnalyticsData.ClientRow c) =>
        string.IsNullOrWhiteSpace(c.Phone) ? c.Name : c.Name + " · " + c.Phone;

    // ------------------------------------------------------------------ выгрузка

    private async Task ExportAsync(string extension)
    {
        var data = Data;
        if (data is null)
        {
            _status.Text = Tr.T("Сначала дождитесь расчёта.", "Алгач эсептелгенин күтүңүз.", "Wait for the calculation first.", "Önce hesaplamanın bitmesini bekleyin.", "Avval hisob-kitob tugashini kuting.");
            return;
        }
        if (!TariffGate.CanUseAnalyticsExport)
        {
            _status.Text = TariffGate.AnalyticsExportLockedMessage;
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить отчёт по бонусам", "Бонустар боюнча отчётту сактоо", "Save the bonus report", "Bonus raporunu kaydet", "Bonuslar hisobotini saqlash"),
            SuggestedFileName = AnalyticsExportService.SuggestFileName(BonusAnalyticsExport.FileTitle, data.From, data.To, UserPreferences.Instance.StoreName, extension),
            FileTypeChoices = [new FilePickerFileType(extension == "xlsx" ? "Excel" : extension == "docx" ? "Word" : "CSV") { Patterns = [$"*.{extension}"] }],
        });
        if (file is null)
            return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            _status.Text = Tr.T("Не удалось определить путь файла — выберите папку на этом компьютере.", "Файлдын жолун аныктоо мүмкүн болгон жок — ушул компьютердеги папканы тандаңыз.", "Could not determine the file path — choose a folder on this computer.", "Dosya yolu belirlenemedi — bu bilgisayarda bir klasör seçin.", "Fayl yo'lini aniqlab bo'lmadi — shu kompyuterdagi papkani tanlang.");
            return;
        }

        try
        {
            _status.Text = Tr.T("Готовлю отчёт…", "Отчёт даярдалууда…", "Preparing the report…", "Rapor hazırlanıyor…", "Hisobot tayyorlanmoqda…");
            var shop = UserPreferences.Instance.StoreName;
            await Task.Run(() =>
            {
                if (extension == "docx")
                    BonusAnalyticsExport.ExportToWord(path, data, shop);
                else if (extension == "xlsx")
                    BonusAnalyticsExport.ExportToExcel(path, data, shop);
                else
                    BonusAnalyticsExport.ExportToCsv(path, data);
            }).ConfigureAwait(true);
            _status.Text = Tr.T($"Отчёт сохранён: {path}", $"Отчёт сакталды: {path}", $"Report saved: {path}", $"Rapor kaydedildi: {path}", $"Hisobot saqlandi: {path}");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка аналитики бонусов не удалась: {ex}", "WARNING");
            _status.Text = Tr.T("Не удалось сохранить отчёт: ", "Отчётту сактоо мүмкүн болгон жок: ", "Could not save the report: ", "Rapor kaydedilemedi: ", "Hisobotni saqlab bo'lmadi: ") + ex.Message;
        }
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
