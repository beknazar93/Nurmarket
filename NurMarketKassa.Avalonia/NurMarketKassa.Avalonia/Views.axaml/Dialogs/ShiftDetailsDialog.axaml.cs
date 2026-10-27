using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ShiftDetailsDialog : Window
{
    public bool? DialogResult { get; set; }
    private ShiftModel? _shift;

    /// <summary>2026-09-27, жалоба владельца «медленно открывает отчёты / Z-отчёт на смене».
    /// Окно показывается сразу с тем, что уже известно, а сервер догружается в фоне:
    /// цифры смены сверяются одним запросом этой смены (~0,3 с, см. RefreshFromServerAsync),
    /// чеки смены для плиток начинают грузиться заранее и одни на все плитки.
    /// false — не сверять: отчёт закрытия смены собран из ответа самого закрытия (MainWindow).</summary>
    public bool RefreshFromServer { get; init; } = true;

    private readonly CancellationTokenSource _cts = new();
    private Task<List<ShiftReportData.ShiftSale>>? _saleRows;
    private Task? _pendingDebt;
    private double _serverDiscounts;

    /// <summary>2026-09-28, доработка NurCRM (BE-10): отчёт смены с сервера одним запросом —
    /// выручка, оплаты (смешанная раздельно), долг, скидки, возвраты, внесения/изъятия, ожидаемый
    /// остаток. Пока он есть, цифры окна и печати — из него; локальные счётчики кассы остаются
    /// запасом на случай, когда сервера нет (офлайн-смена, нет связи).</summary>
    private ServerShiftReport? _report;
    private Task<ServerShiftReport?>? _reportTask;

    /// <summary>Возвраты смены по списку возвратов сервера — пока сам отчёт их не считает
    /// (у возвратов на сервере пустое поле shift, см. NurCrmReportsApi.ReturnsForShiftAsync).</summary>
    private (int Count, decimal Sum)? _serverShiftReturns;

    /// <summary>Отчёт смены с сервера, который окно загрузило при открытии (null — сервера нет).</summary>
    public Task<ServerShiftReport?> ServerReportTask => _reportTask ?? Task.FromResult<ServerShiftReport?>(null);

    public ShiftDetailsDialog()
    {
        InitializeComponent();
        Opened += (_, _) => OnOpened();
        Closed += (_, _) => _cts.Cancel();
        // 2026-09-30: «Товары за смену» догружаются после открытия, и окно растёт вниз от места,
        // куда его поставили при открытии, — нижний край с кнопками уходил за экран. После каждого
        // изменения размера окно снова ужимается до экрана кассы и сдвигается, если вылезло.
        SizeChanged += (_, _) => this.FitToKassaScreen();
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Д): заголовок «X-отчёт» (Title, заданный снаружи) доходил только до панели задач, а в шапке
    /// оболочки стояло «Просмотр смены» — переносим Title в шапку.</summary>
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty && Content is ModalShell shell && change.NewValue is string t && !string.IsNullOrWhiteSpace(t))
            shell.Title = t;
    }

    public ShiftDetailsDialog(object? model) : this()
    {
        if (model is ShiftModel shift)
            BindShift(shift);
    }

    public ShiftDetailsDialog(ShiftModel shift) : this() => BindShift(shift);

    private void OnOpened()
    {
        if (_shift is not { } shift || !IsServerShift(shift.Id))
            return;

        // Список продаж смены нужен любой плитке «Продажи/Наличные/…» — к нажатию он уже здесь.
        SaleRows();
        _ = ShowServerDiscountsAsync();
        // «Товары за смену» — с сервера, по тем же чекам смены (2026-09-28).
        _ = LoadServerProductsAsync();
        // Отчёт смены сервера — и для отчёта закрытия (RefreshFromServer=false): после закрытия
        // он уже с пересчитанной суммой и расхождением, а внесения/возвраты касса сама не знает,
        // если их делали на другом компьютере.
        _reportTask = LoadServerReportAsync(shift.Id);
        _rentalsTask = LoadRentalsAsync(shift);
        if (RefreshFromServer)
            _ = RefreshFromServerAsync(shift.Id);
    }

    private Task<RentalShiftSummary?>? _rentalsTask;

    /// <summary>2026-10-04, клиент: «на Z-отчёт — информацию об аренде». Прокат за время смены (сервер NurCRM).</summary>
    private async Task<RentalShiftSummary?> LoadRentalsAsync(ShiftModel shift)
    {
        if (TelegramAssistant.RentalsLoader is not { } loader)
            return null;
        try
        {
            var all = await loader(null, _cts.Token).ConfigureAwait(true);
            var summary = RentalShiftSummary.Compute(all, shift.OpenedAt, shift.ClosedAt);
            if (_cts.IsCancellationRequested || summary.IsEmpty)
                return null;
            RentalsText.Text = summary.DisplayText();
            RentalsPanel.IsVisible = true;
            return summary;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены: прокат не загружен: {ex.Message}", "SHIFTS");
            return null;
        }
    }

    /// <summary>Загружает отчёт смены сервера (BE-10) и перерисовывает окно по нему.</summary>
    private async Task<ServerShiftReport?> LoadServerReportAsync(string shiftId)
    {
        try
        {
            var report = await NurCrmReportsApi.GetShiftReportAsync(shiftId, _cts.Token).ConfigureAwait(true);
            if (report is null || _cts.IsCancellationRequested || _shift is null)
                return report;

            _report = report;
            BindShift(_shift);

            // Возвраты смены: пока сервер не пишет смену возврата, returns_total в отчёте 0 —
            // досчитываем по списку возвратов сервера (кассир смены, время смены).
            if (report.ReturnsTotal <= 0m)
            {
                _serverShiftReturns = await NurCrmReportsApi.ReturnsForShiftAsync(report, _cts.Token).ConfigureAwait(true);
                if (!_cts.IsCancellationRequested && _shift is { } shown)
                    BindExtraTotals(shown.Id, shown.ExpenseTotal);
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены: отчёт сервера не применён: {ex.Message}", "SHIFTS");
            return null;
        }
    }

    /// <summary>Смена, открытая без связи, на сервере не существует — спрашивать о ней нечего.</summary>
    private static bool IsServerShift(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.StartsWith("offline-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Чеки смены без товаров — одни на все плитки этого окна; после ошибки — заново.</summary>
    private Task<List<ShiftReportData.ShiftSale>> SaleRows()
    {
        if (_saleRows is null || _saleRows.IsFaulted || _saleRows.IsCanceled)
        {
            _saleRows = ShiftReportData.LoadSaleRowsAsync(_shift!.Id, _shift.OpenedAt, _shift.ClosedAt, _cts.Token);
            // Ошибку заранее начатой загрузки плитка покажет сама; здесь — только в журнал.
            _saleRows.ContinueWith(
                t => PosLogger.Log($"Отчёт смены: чеки смены не загружены: {t.Exception?.GetBaseException().Message}", "SHIFTS"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        return _saleRows;
    }

    /// <summary>Сверка цифр с сервером одним запросом этой смены. Окно могло открыться по
    /// снимку списка смен, скачанному минуты назад (ShiftHistoryService.LoadAsync), а у
    /// открытой смены за это время могли пройти продажи. Долг сервер не присылает — его
    /// уточняет код, открывший окно (RefreshDebtDisplay), поэтому он не трогается.</summary>
    private async Task RefreshFromServerAsync(string shiftId)
    {
        var entry = await ShiftHistoryService.LoadOneAsync(shiftId, _cts.Token).ConfigureAwait(true);
        if (entry is null || _cts.IsCancellationRequested || _shift is not { } shown)
            return;

        var fresh = ShiftModel.FromEntry(entry);
        fresh.DebtSales = shown.DebtSales;
        BindShift(fresh);
    }

    private sealed record ShiftProductRow(string Name, string QuantityText, string RevenueText);

    /// <summary>2026-09-28: «Товары за смену» серверной смены — с сервера (позиции продаж смены,
    /// см. ShiftProductsSummary); null — ещё не загружены или сервер недоступен.</summary>
    private ShiftProductsSummary.Result? _serverProducts;

    /// <summary>Сервер не дал товары смены — показываем локальную историю с пометкой.</summary>
    private bool _serverProductsFailed;

    /// <summary>2026-09-28: товары смены с сервера. Раньше список считался только по локальной
    /// истории кассы за время смены и расходился с выручкой (смена d723b36b: 2 930 против
    /// 2 534,50 — чужие чеки того же времени, офлайн-чек дважды, скидка сервера, см.
    /// ShiftProductsSummary). Локальная история — запасом, если сервер не ответил.</summary>
    private async Task LoadServerProductsAsync()
    {
        try
        {
            var sales = await SaleRows().ConfigureAwait(true);
            var result = await ShiftReportData.LoadProductsAsync(sales, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested)
                return;
            _serverProducts = result;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены: товары смены с сервера не получены, показываю историю кассы: {ex.Message}", "SHIFTS");
            _serverProductsFailed = true;
        }

        if (_shift is { } shown)
            BindShiftProducts(shown);
    }

    /// <summary>Товары за смену, по убыванию выручки: у серверной смены — с сервера, иначе (и если
    /// сервер не ответил) — из локальной истории продаж за время смены.</summary>
    private void BindShiftProducts(ShiftModel shift)
    {
        if (IsServerShift(shift.Id) && !_serverProductsFailed)
        {
            // Пока товары с сервера грузятся, список не показываем: локальные цифры, сменившиеся
            // через секунду на другие, только сбивали бы с толку.
            if (_serverProducts is { } fromServer)
                ShowServerProducts(fromServer);
            return;
        }

        if (shift.OpenedAt is not { } opened)
            return;

        try
        {
            var until = (shift.ClosedAt ?? DateTime.Now).ToUniversalTime();
            var rows = NurMarketKassa.Services.SoldLineItemsStore
                .LoadWithPriceSince(opened.ToUniversalTime(), until)
                .GroupBy(l => string.IsNullOrWhiteSpace(l.ProductId) ? l.ProductName : l.ProductId)
                .Select(g => (Name: g.First().ProductName, Qty: g.Sum(l => l.Quantity), Revenue: g.Sum(l => l.Quantity * l.UnitPrice)))
                .OrderByDescending(r => r.Revenue)
                .ToList();
            if (rows.Count == 0)
                return;

            var totalQty = rows.Sum(r => r.Qty);
            ShiftProductsTitle.Text = Tr.T($"Товары за смену: {rows.Count} поз., {totalQty:0.###} ед.",
                $"Сменадагы товарлар: {rows.Count} поз., {totalQty:0.###} бирд.",
                $"Products this shift: {rows.Count} item(s), {totalQty:0.###} unit(s)",
                $"Vardiyadaki ürünler: {rows.Count} kalem, {totalQty:0.###} birim",
                $"Smenadagi mahsulotlar: {rows.Count} ta pozitsiya, {totalQty:0.###} birlik");
            ShiftProductsList.ItemsSource = rows
                .Select(r => new ShiftProductRow(r.Name, $"{r.Qty:0.###}", $"{r.Revenue:N2} {Som}"))
                .ToList();
            // 2026-09-28: локальная история — только запас (смена без связи или сервер не ответил):
            // в ней чеки всех касс за это время и офлайн-чеки, поэтому сумма может не совпасть.
            ShiftProductsNote.Text = Tr.T("По истории этой кассы: сервер недоступен, сумма может не совпасть с выручкой.",
                "Ушул кассанын тарыхы боюнча: сервер жеткиликсиз, сумма түшкөн акчага дал келбеши мүмкүн.",
                "From this till's history: the server is unavailable, the sum may differ from revenue.",
                "Bu kasanın geçmişine göre: sunucuya ulaşılamıyor, toplam ciroyla uyuşmayabilir.",
                "Shu kassa tarixi bo'yicha: server mavjud emas, summa tushumga mos kelmasligi mumkin.");
            ShiftProductsNote.IsVisible = true;
            ShiftProductsPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Shift details: products list skipped: {ex.Message}", "SHIFTS");
        }
    }

    /// <summary>2026-09-28: товары смены с сервера и итог под заголовком: сумма строк (уже со
    /// скидками на строку) минус скидка на чек = выручка смены; долг и возвраты — отдельно.</summary>
    private void ShowServerProducts(ShiftProductsSummary.Result result)
    {
        if (result.Rows.Count == 0)
        {
            ShiftProductsPanel.IsVisible = false;
            return;
        }

        var count = result.Rows.Count;
        var totalQty = result.Rows.Sum(r => r.Quantity);
        ShiftProductsTitle.Text = Tr.T($"Товары за смену: {count} поз., {totalQty:0.###} ед.",
            $"Сменадагы товарлар: {count} поз., {totalQty:0.###} бирд.",
            $"Products this shift: {count} item(s), {totalQty:0.###} unit(s)",
            $"Vardiyadaki ürünler: {count} kalem, {totalQty:0.###} birim",
            $"Smenadagi mahsulotlar: {count} ta pozitsiya, {totalQty:0.###} birlik");
        ShiftProductsList.ItemsSource = result.Rows
            .Select(r => new ShiftProductRow(r.Name, $"{r.Quantity:0.###}", $"{r.Revenue:N2} {Som}"))
            .ToList();

        var lines = $"{result.LinesTotal:N2}";
        var discount = $"{result.OrderDiscount:N2}";
        var total = $"{result.SalesTotal:N2}";
        // 2026-10-23 (аудит Б-8): выборка — те же статусы, что в выручке сайта (paid/partially_returned), долговой чек в ней есть; подпись говорит как есть.
        ShiftProductsNote.Text = result.OrderDiscount > 0.005m
            ? Tr.T($"По товарам {lines} − скидка на чек {discount} = {total} {Som}. Чеки в долг входят, как в выручке смены; возвраты не входят.",
                $"Товарлар боюнча {lines} − чекке арзандатуу {discount} = {total} {Som}. Карызга чектер сменанын түшүмүндөгүдөй кирет; кайтаруулар кирбейт.",
                $"Products {lines} − receipt discount {discount} = {total} {Som}. Credit receipts are included, as in the shift revenue; returns are not.",
                $"Ürünler {lines} − fiş indirimi {discount} = {total} {Som}. Veresiye fişler vardiya cirosundaki gibi dahildir; iadeler dahil değildir.",
                $"Mahsulotlar {lines} − chekka chegirma {discount} = {total} {Som}. Qarzga cheklar smena tushumidagidek kiradi; qaytarishlar kirmaydi.")
            : Tr.T($"Итого по товарам: {total} {Som}. Чеки в долг входят, как в выручке смены; возвраты не входят.",
                $"Товарлар боюнча жыйынтык: {total} {Som}. Карызга чектер сменанын түшүмүндөгүдөй кирет; кайтаруулар кирбейт.",
                $"Products total: {total} {Som}. Credit receipts are included, as in the shift revenue; returns are not.",
                $"Ürünler toplamı: {total} {Som}. Veresiye fişler vardiya cirosundaki gibi dahildir; iadeler dahil değildir.",
                $"Mahsulotlar bo'yicha jami: {total} {Som}. Qarzga cheklar smena tushumidagidek kiradi; qaytarishlar kirmaydi.");
        ShiftProductsNote.IsVisible = true;
        ShiftProductsPanel.IsVisible = true;
    }

    private void BindShift(ShiftModel shift)
    {
        // 2026-09-28: цифры — из отчёта смены сервера, если он уже пришёл (см. _report); смена,
        // пришедшая позже (RefreshFromServerAsync), тоже показывается с его цифрами.
        if (_report is { } report)
            shift = report.ApplyTo(shift);
        _shift = shift;
        BindShiftProducts(shift);
        // 2026-09-15, по просьбе пользователя ("номер смены исправь") — сырой GUID нечитаем на
        // экране (36 символов). Тот же приём, что уже проверен в FinanceWindow.ParseShiftRow:
        // первые 8 символов заглавными как короткий номер смены.
        ShiftNumberText.Text = string.IsNullOrWhiteSpace(shift.ShiftNumber)
            ? "—"
            : (shift.ShiftNumber.Length > 8 ? shift.ShiftNumber[..8].ToUpperInvariant() : shift.ShiftNumber.ToUpperInvariant());
        OpenedAtText.Text = shift.OpenedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        ClosedAtText.Text = shift.ClosedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        // 2026-10-23 (аудит, круг 8, В-7): после закрытия смены «Кассир: —». Если имени в смене нет, а смена своя (активная на этой кассе,
        // только что закрытая — отчёт собран из ответа закрытия, или кассир отчёта сервера = текущий) — берём имя текущего кассира.
        var cashierName = shift.Cashier;
        if (string.IsNullOrWhiteSpace(cashierName) || cashierName.Trim() == "—")
        {
            var current = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var isOwn = !RefreshFromServer
                || string.Equals(shift.Id, NurMarketKassa.PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase)
                || (_report?.CashierId is { Length: > 0 } reportCashier
                    && string.Equals(reportCashier, NurMarketKassa.PosApp.CurrentUserId, StringComparison.OrdinalIgnoreCase));
            cashierName = isOwn && !string.IsNullOrWhiteSpace(current) ? current : "—";
        }
        CashierText.Text = cashierName;
        // Статус приходит данными («Открыта»/«Закрыта» или open/closed) — на экран на языке программы.
        var rawStatus = shift.Status ?? "";
        StatusText.Text = rawStatus.Length == 0 ? "—"
            : rawStatus.Contains("Закрыт", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Закрыта", "Жабык", "Closed", "Kapalı", "Yopiq")
            // 2026-10-27 (Н-12): «Активна» (так ShiftHistoryService помечает открытую смену) в X-отчёте оставалось по-русски на всех языках.
            : rawStatus.Contains("Активн", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("active", StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Активна", "Активдүү", "Active", "Aktif", "Faol")
            : rawStatus.Contains("Открыт", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("open", StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Открыта", "Ачык", "Open", "Açık", "Ochiq")
            : rawStatus;
        // 2026-10-23 (аудит К5, п. 3): выручка — без продаж в долг (как «Сводка» владельца и сайт); долговые продажи — строкой ниже.
        RevenueText.Text = $"{shift.RevenueNoDebt:N2} {Som}";
        CreditSalesText.IsVisible = shift.CreditSales > 0m;
        CreditSalesText.Text = Tr.T($"Продажи в долг: {shift.CreditSales:N2} {Som}", $"Карызга сатуулар: {shift.CreditSales:N2} {Som}",
            $"Credit sales: {shift.CreditSales:N2} {Som}", $"Veresiye satışlar: {shift.CreditSales:N2} {Som}", $"Qarzga sotuvlar: {shift.CreditSales:N2} {Som}");
        SalesCountText.Text = shift.SalesCount?.ToString() ?? "—";
        CashText.Text = shift.CashSales is { } cash ? $"{cash:N2} {Som}" : "—";
        CardText.Text = shift.NonCashSales is { } card ? $"{card:N2} {Som}" : "—";
        SetDebtText(shift.DebtSales); // 2026-10-22 (аудит 1.17.59, П-Д): нулевой долг — не красной плиткой

        // Смешанная оплата: её наличная часть уже в «Наличных», безналичная — в «Безналичных»;
        // строкой ниже — сколько из них пришло смешанной (раньше сервер разбивки не хранил и
        // смешанная целиком уходила в безнал).
        var mixedCash = _report?.MixedCash ?? 0m;
        var mixedCard = _report?.MixedCard ?? 0m;
        // 2026-10-04, стресс-тест (К4): «Наличные»/«Безналичные» теперь с предоплатой долга, как в окне
        // закрытия смены и у сервера (ServerShiftReport.ApplyTo), — сама предоплата видна отдельной
        // строкой под плиткой, а не теряется внутри суммы.
        var prepaidCash = _report?.DebtPrepaymentsCash ?? 0m;
        var prepaidCard = _report?.DebtPrepaymentsNonCash ?? 0m;
        CashMixedText.IsVisible = mixedCash > 0m || prepaidCash > 0m;
        CashMixedText.Text = SubLines(mixedCash, prepaidCash);
        CardMixedText.IsVisible = mixedCard > 0m || prepaidCard > 0m;
        CardMixedText.Text = SubLines(mixedCard, prepaidCard);

        BindExtraTotals(shift.Id, shift.ExpenseTotal);

        // Внесения и изъятия — с сервера (он знает и о сделанных на других компьютерах), без
        // отчёта сервера — как раньше, по журналу этой кассы.
        var (deposits, withdrawals) = _report is { } fromServer
            ? (fromServer.Deposits, fromServer.Withdrawals)
            : ShiftCashOperationsStore.SumsForShift(shift.Id);
        CashOpsPanel.IsVisible = deposits > 0m || withdrawals > 0m;
        DepositsText.Text = $"+{deposits:N2} {Som}";
        WithdrawalsText.Text = $"-{withdrawals:N2} {Som}";

        // 2026-10-23 (аудит Б-8): «в ящике должно быть / факт / расхождение» — формула та же, что в печатном Z-отчёте ниже.
        // Фактическая сумма известна только у закрытой смены; у открытой (X-отчёт) показываем ожидаемую и «ещё не пересчитано».
        if (shift.OpeningCash is { } drawerOpening)
        {
            // 2026-10-27 (аудит кассы, С-7): запасная формула (нет ожидаемой суммы сервера) раньше не знала внесений, оплат долгов
            // и наличных возвратов — Z и X расходились с окном закрытия. Теперь одна формула (ShiftDrawerMath).
            var cashReturnsForExpected = ReturnsForExpected(shift, _report, _serverShiftReturns);
            var drawerExpected = shift.ExpectedCash
                ?? FallbackExpected(shift, _report, drawerOpening, deposits, withdrawals, cashReturnsForExpected);
            DrawerExpectedLabel.Text = Tr.T("В ящике должно быть", "Кассада болушу керек", "Should be in the drawer", "Çekmecede olması gereken", "Qutida bo'lishi kerak");
            DrawerActualLabel.Text = Tr.T("Факт", "Чындыгында", "Actual", "Gerçek", "Haqiqatda");
            DrawerDiffLabel.Text = Tr.T("Расхождение", "Айырма", "Difference", "Fark", "Farq");
            DrawerExpectedText.Text = $"{drawerExpected:N2} {Som}";
            // 2026-10-27 (ревью, См-4): запасная формула вычитает только НАЛИЧНЫЕ возвраты; раскладка получала все (ReturnsTotal), и при безналичном
            // возврате строки не давали итог (лишняя «Корректировка»). Без суммы сервера в раскладку идёт та же наличная часть, что и в итог.
            DrawerBreakdownBuilder.Fill(this, DrawerBreakdown, drawerOpening, shift.CashSales ?? 0m, _report?.DebtPaymentsCash ?? 0m, deposits, withdrawals,
                shift.ExpectedCash is null ? cashReturnsForExpected : _report?.ReturnsTotal ?? 0m, drawerExpected);
            AppendNotOnServerRow(shift, drawerExpected);
            if (shift.ClosingCash is { } drawerActual)
            {
                var drawerDiff = drawerActual - drawerExpected;
                DrawerActualText.Text = $"{drawerActual:N2} {Som}";
                DrawerDiffText.Text = $"{(drawerDiff > 0m ? "+" : "")}{drawerDiff:N2} {Som}";
            }
            else
            {
                DrawerActualText.Text = Tr.T("не пересчитано", "саналган жок", "not counted", "sayılmadı", "sanalmagan");
                DrawerDiffText.Text = "—";
            }
            DrawerCheckPanel.IsVisible = true;
        }
        else
            DrawerCheckPanel.IsVisible = false;

        if (shift.IsActive)
        {
            StatusBadge.Background = ThemeBrush("BrushSuccessSoft", Brushes.DarkGreen);
            StatusDot.Fill = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            StatusText.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
        }
        else
        {
            // 2026-10-21 (пакет 6, хвост пакета 5): закрытая смена — обычное состояние, значок нейтральный (был красный;
            // красный — только долг и недостача).
            StatusBadge.Background = ThemeBrush("BrushPanelSoft", Brushes.LightGray);
            StatusDot.Fill = ThemeBrush("BrushTextMuted", Brushes.Gray);
            StatusText.Foreground = ThemeBrush("BrushTextSoft", Brushes.DimGray);
        }
    }

    /// <summary>2026-10-27 (аудит кассы, С-7): наличная часть возвратов смены для «в ящике должно быть» без ответа сервера.
    /// Журнал кассы знает, чем вернули (cash=… в примечании); нет записей — возвраты сервера целиком.</summary>
    private static decimal ReturnsForExpected(ShiftModel shift, ServerShiftReport? server, (int Count, decimal Sum)? serverReturns)
    {
        var local = ShiftEventsStore.ListForShift(shift.Id, ShiftEventsStore.KindReturn);
        if (local.Count > 0)
            return local.Sum(e => (decimal)(CloseShiftDialog.ParseNote(e.Note, "cash") ?? e.Amount));
        if (server is { ReturnsTotal: > 0m } fromServer)
            return fromServer.ReturnsTotal;
        return serverReturns?.Sum ?? 0m;
    }

    /// <summary>2026-10-27 (С-7): запасная ожидаемая сумма в ящике — общая формула ShiftDrawerMath с оплатами долгов наличными из
    /// отчёта сервера или журнала кассы.</summary>
    private static decimal FallbackExpected(ShiftModel shift, ServerShiftReport? server, decimal opening, decimal deposits, decimal withdrawals, decimal cashReturns)
    {
        var debtPaid = server?.DebtPaymentsCash
                       ?? (decimal)ShiftEventsStore.TotalsForShift(shift.Id).GetValueOrDefault(ShiftEventsStore.KindDebtPayment);
        return ShiftDrawerMath.Expected(opening, shift.CashSales ?? 0m, debtPaid, deposits, withdrawals, cashReturns);
    }

    /// <summary>2026-10-27 (аудит кассы, С-9): «Касса: N» в шапке и окно закрытия считают и то, что ещё не дошло до сервера
    /// (внесения/изъятия без связи, чеки офлайн-очереди), а X-отчёт — по ответу сервера и расходился с шапкой без объяснений.
    /// Строка под раскладкой: сколько «ещё не на сервере» и сколько будет в ящике с этим.</summary>
    private void AppendNotOnServerRow(ShiftModel shift, decimal expected)
    {
        if (!shift.IsActive || !IsServerShift(shift.Id))
            return;
        var (pendingDeposits, pendingWithdrawals) = ShiftCashOperationsStore.PendingSumsForShift(shift.Id);
        var queuedCash = OfflineQueueCash.Snapshot(shift.Id).Cash.Values.Sum();
        var notOnServer = ShiftDrawerMath.NotOnServer(pendingDeposits, pendingWithdrawals, queuedCash);
        if (notOnServer == 0m)
            return;
        var sign = notOnServer > 0m ? "+" : "−";
        var note = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = Tr.T($"Ещё не на сервере: {sign}{Math.Abs(notOnServer):N2} {Som} (внесения/изъятия без связи, чеки в очереди). С ними в ящике: {expected + notOnServer:N2} {Som} — столько показывает «Касса» в шапке.",
                $"Серверде дагы жок: {sign}{Math.Abs(notOnServer):N2} {Som} (байланышсыз киргизүү/алуулар, кезектеги чектер). Алар менен кассада: {expected + notOnServer:N2} {Som} — башкы тилкедеги «Касса» ушуну көрсөтөт.",
                $"Not on the server yet: {sign}{Math.Abs(notOnServer):N2} {Som} (offline deposits/withdrawals, queued receipts). With them the drawer should hold {expected + notOnServer:N2} {Som} — this is what “Cash” in the header shows.",
                $"Henüz sunucuda değil: {sign}{Math.Abs(notOnServer):N2} {Som} (bağlantısız para giriş/çıkışları, kuyruktaki fişler). Bunlarla çekmecede {expected + notOnServer:N2} {Som} olmalı — üstteki «Kasa» bunu gösterir.",
                $"Hali serverda yo'q: {sign}{Math.Abs(notOnServer):N2} {Som} (aloqasiz kiritish/olishlar, navbatdagi cheklar). Ular bilan qutida {expected + notOnServer:N2} {Som} bo'lishi kerak — tepadagi «Kassa» shuni ko'rsatadi.")
        };
        note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        DrawerBreakdown.Children.Add(note);
    }

    /// <summary>Возвраты, списания, расход, оплата долгов и скидки за смену.
    ///
    /// Всё это касса считает у себя: в итогах смены на сервере (api/construction/shifts/) таких
    /// полей нет — там только выручка, наличные/безналичные и расход. Прочерк вместо нуля
    /// означает «операций такого рода не было», а у смен, закрытых до появления этого учёта,
    /// он будет стоять всегда — цифры копятся с версии 1.16.86.</summary>
    private void BindExtraTotals(string? shiftId, decimal? serverExpense)
    {
        static string Money(double value) => $"{value:N2} {Som}";

        // Тариф: на «Старт» расширенные итоги — платная доп. услуга. Ряд плиток прячем целиком,
        // а не показываем прочерками: прочерк означает «операций не было», и спутать эти два
        // случая нельзя.
        ExtraTotalsRow.IsVisible = TariffGate.CanUseShiftAnalytics;
        if (!ExtraTotalsRow.IsVisible)
            return;

        var events = ShiftEventsStore.TotalsForShift(shiftId);
        double Get(string kind) => events.TryGetValue(kind, out var value) ? value : 0;

        var returns = Get(ShiftEventsStore.KindReturn);
        // 2026-09-28 (BE-09/BE-10): возвраты — с сервера: из отчёта смены, а пока он их не считает
        // (returns_total = 0, у возвратов нет смены) — по списку возвратов сервера. Локальный
        // журнал — только если сервера нет.
        if (_report is { ReturnsTotal: > 0m } withReturns)
            returns = (double)withReturns.ReturnsTotal;
        else if (_serverShiftReturns is { } serverReturns)
            returns = (double)serverReturns.Sum;
        var writeOffs = Get(ShiftEventsStore.KindWriteOff);
        // Расход — с сервера, если он его прислал: касса видит только свои операции, и
        // экран расходился бы с печатным чеком, где эта цифра уже серверная.
        var expenses = serverExpense is { } fromServer ? (double)fromServer : Get(ShiftEventsStore.KindExpense);
        // 2026-10-04, стресс-тест (К5): expense_total сервера — это в том числе изъятия из ящика, а они уже
        // показаны своей строкой «Изъятия» (смена 7e33b63b: расход 40 = изъятие 40 — одни и те же 40 сом
        // дважды). В плитке «Расход» — только то, что сверх изъятий.
        if (serverExpense is not null)
        {
            var shownWithdrawals = _report is { } withWithdrawals
                ? withWithdrawals.Withdrawals
                : ShiftCashOperationsStore.SumsForShift(shiftId).Withdrawals;
            expenses = Math.Max(0, expenses - (double)shownWithdrawals);
        }
        // 2026-10-23 (аудит, круг 8, В-7/М-32): изъятие по статье расхода («Хоз. нужды 1») вычиталось выше как «уже показанное
        // строкой Изъятия», и плитка «Расход» оставалась «—». Если расхода сверх изъятий нет, а изъятия по статьям расхода
        // (без инкассации и без «без статьи») есть — показываем их сумму.
        if (expenses <= 0.005)
        {
            var noCategory = Tr.T("без статьи", "беренесиз", "no category", "kalemsiz", "moddasiz");
            var byArticle = ShiftCashOperationsStore.ArticleSumsForShift(shiftId, withdrawal: true)
                .Where(a => !a.IsCollection && a.Title != noCategory)
                .Sum(a => (double)a.Amount);
            if (byArticle > 0.005)
                expenses = byArticle;
        }
        var debtPaid = Get(ShiftEventsStore.KindDebtPayment);
        // 2026-10-04, стресс-тест: оплата долгов смены есть и в отчёте сервера (debt_payments_cash) —
        // в программе владельца и на другой кассе локального журнала нет, плитка показывала «—»
        // при оплате 30 сом в смене. Берём большее. 2026-10-18, второй круг ревью: и журнал кассы здесь — только наличные
        // (безнал — свой вид события, подписью ниже), так что обе цифры — наличные в ящике.
        if (_report is { DebtPaymentsCash: > 0m } withDebtPayments)
            debtPaid = Math.Max(debtPaid, (double)withDebtPayments.DebtPaymentsCash!.Value);

        ReturnsText.Text = returns > 0.005 ? Money(returns) : "—";
        // 2026-10-22 (аудит 1.17.59, П-Д): сервер списывает при возврате больше, чем касса выдала (скидка на чек, ТЗ 19.9) —
        // та же строка-пояснение, что в окне закрытия смены (CloseShiftDialog, server_over в записи возврата).
        var serverOver = ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn)
            .Sum(r => CloseShiftDialog.ParseNote(r.Note, "server_over") ?? 0);
        ReturnsOverText.IsVisible = serverOver > 0.005;
        ReturnsOverText.Text = Tr.T($"Сервер записал возвраты больше выданного — {Money(serverOver)}",
            $"Сервер кайтарууларды берилгенден көп жазды — {Money(serverOver)}",
            $"The server recorded returns above what was paid out — {Money(serverOver)}",
            $"Sunucu iadeleri verilenden fazla kaydetti — {Money(serverOver)}",
            $"Server qaytarishlarni berilganidan ko'p yozdi — {Money(serverOver)}");
        WriteOffsText.Text = writeOffs > 0.005 ? Money(writeOffs) : "—";
        ExpenseText.Text = expenses > 0.005 ? Money(expenses) : "—";
        DebtPaidText.Text = debtPaid > 0.005 ? Money(debtPaid) : "—";
        // 2026-10-18, ревью: плитка выше — наличные (локальный журнал или debt_payments_cash сервера); безнал — отдельно.
        var debtPaidNonCash = Get(ShiftEventsStore.KindDebtPaymentNonCash);
        DebtPaidNonCashText.IsVisible = debtPaidNonCash > 0.005;
        DebtPaidNonCashText.Text = Tr.T("+ безналом: ", "+ накталай эмес: ", "+ non-cash: ", "+ nakitsiz: ", "+ naqd emas: ") + Money(debtPaidNonCash);

        var adjustments = ClientLoyaltyStore.AdjustmentsForShift(shiftId);
        var discounts = Math.Max(adjustments.Discounts, _serverDiscounts);
        var pointsRedeemed = adjustments.PointsRedeemed;
        // 2026-09-28: скидки смены (на строку и на чек, с любой кассы) и оплата бонусами — из
        // отчёта смены сервера. Бонусы, списанные только в локальной программе лояльности, сервер
        // не видит — их берём у кассы, если сервер своих не прислал.
        if (_report is { } report)
        {
            discounts = (double)report.Discounts;
            if (report.BonusRedeemed > 0m)
                pointsRedeemed = (double)report.BonusRedeemed;
        }
        DiscountsText.Text = discounts > 0.005 ? Money(discounts) : "—";

        // Оплату бонусами показываем отдельной строкой под скидкой: она входит в общую сумму
        // скидок, и без пояснения владелец считал бы её дважды.
        PointsRedeemedText.IsVisible = pointsRedeemed > 0.005;
        PointsRedeemedText.Text = Tr.T($"из них бонусами: {pointsRedeemed:N2}",
            $"анын ичинен бонус менен: {pointsRedeemed:N2}",
            $"of which paid with points: {pointsRedeemed:N2}",
            $"puanla ödenen: {pointsRedeemed:N2}",
            $"shundan bonus bilan: {pointsRedeemed:N2}");
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Д): плитка «Долг» красная только когда долг есть; «Долг 0,00» — обычная плитка
    /// (красный — для долга, правило 4). null — «—» (долг неизвестен), плитка обычная.</summary>
    private void SetDebtText(decimal? debt)
    {
        DebtText.Text = debt is { } value ? $"{value:N2} {Som}" : "—";
        // 2026-10-23 (аудит К6): «Долг 0,00» рядом с «Продажи в долг: 3,00» читался как противоречие — плитка называется «Остаток долга».
        DebtLabelText.Text = Tr.T("Остаток долга", "Карыздын калдыгы", "Debt balance", "Borç bakiyesi", "Qarz qoldig'i");
        ToolTip.SetTip(DebtTile, Tr.T("Сколько клиенты ещё должны по долговым чекам смены. «Продажи в долг» — сколько выдано в долг за смену; часть могла быть уже оплачена.",
            "Сменанын карызга чектери боюнча кардарлар дагы канча карыз. «Карызга сатуулар» — смена ичинде карызга берилгени; бир бөлүгү төлөнгөн болушу мүмкүн.",
            "How much customers still owe on the shift's credit receipts. “Credit sales” is what was given on credit during the shift; part of it may already be paid.",
            "Müşterilerin vardiyadaki veresiye fişlerinde hâlâ borçlu olduğu tutar. “Veresiye satışlar” vardiyada borca verilen tutardır; bir kısmı ödenmiş olabilir.",
            "Mijozlar smenadagi qarz cheklari bo'yicha hali qancha qarzdor. «Qarzga sotuvlar» — smenada qarzga berilgani; bir qismi to'langan bo'lishi mumkin."));
        var has = debt is > 0.005m;
        DebtTile.Bind(Border.BackgroundProperty, this.GetResourceObservable(has ? "BrushDangerSoft" : "BrushSurfaceSubtle"));
        DebtTile.Bind(Border.BorderBrushProperty, this.GetResourceObservable(has ? "BrushDanger" : "BrushBorder"));
        DebtLabelText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(has ? "BrushDanger" : "BrushTextSoft"));
        DebtText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(has ? "BrushDanger" : "BrushText"));
    }

    /// <summary>Подпись под плиткой «Наличные»/«Безналичные»: сколько из суммы — смешанной оплатой.</summary>
    private static string MixedLine(decimal amount) => Tr.T(
        $"в т. ч. смешанная: {amount:N2}",
        $"анын ичинде аралаш: {amount:N2}",
        $"incl. mixed: {amount:N2}",
        $"karışık dahil: {amount:N2}",
        $"shu jumladan aralash: {amount:N2}");

    /// <summary>2026-10-04, К4: сколько из суммы плитки внесено при продаже в долг (предоплата).</summary>
    private static string PrepaymentLine(decimal amount) => Tr.T(
        $"в т. ч. предоплата долга: {amount:N2}",
        $"анын ичинде карыздын алдын ала төлөмү: {amount:N2}",
        $"incl. debt prepayment: {amount:N2}",
        $"borç ön ödemesi dahil: {amount:N2}",
        $"shu jumladan qarz oldindan to'lovi: {amount:N2}");

    /// <summary>Строки под плиткой: смешанная и предоплата долга — каждая своей строкой, если есть.</summary>
    private static string SubLines(decimal mixed, decimal prepaid)
    {
        var lines = new List<string>(2);
        if (mixed > 0m)
            lines.Add(MixedLine(mixed));
        if (prepaid > 0m)
            lines.Add(PrepaymentLine(prepaid));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>2026-09-28, регресс 1.17.19: в смене была скидка 10 % на строку, а плитка
    /// «Скидки» показывала прочерк — она считала только скидки программы лояльности
    /// (ClientLoyaltyStore), а обычные скидки кассира в неё не попадали. Нажатие на ту же
    /// плитку при этом показывало скидку: детализация берёт discount_total чеков смены.
    /// Теперь плитка — та же сумма, что и в детализации (отменённые/возвращённые чеки не в
    /// счёт). Бонусы сервер тоже видит скидкой по строкам, поэтому берём большее из двух.</summary>
    private async Task ShowServerDiscountsAsync()
    {
        try
        {
            var sales = await SaleRows().ConfigureAwait(true);
            var fromServer = sales
                .Where(s => !string.Equals(s.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Discount);
            _serverDiscounts = fromServer;
            // Отчёт смены сервера уже показал скидки (2026-09-28) — свой подсчёт по чекам не нужен.
            if (_report is not null)
                return;
            var discounts = Math.Max(fromServer, ClientLoyaltyStore.AdjustmentsForShift(_shift?.Id).Discounts);
            if (ExtraTotalsRow.IsVisible && discounts > 0.005)
                DiscountsText.Text = $"{discounts:N2} {Som}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ошибку загрузки чеков уже записал SaleRows(); плитка остаётся как была.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>2026-09-15, живой баг ("Долг для уже закрытых смен в Истории смен показывает
    /// не то") — первичное значение shift.DebtSales может быть из непроверенного поля сервера;
    /// вызывающий код (ShiftHistoryViewModel) уточняет его асинхронно тем же надёжным способом,
    /// что уже работает для только что закрытой смены, и подставляет сюда, если диалог ещё открыт.</summary>
    public void RefreshDebtDisplay(decimal debt)
    {
        // 2026-09-28: долг смены уже дал отчёт смены сервера (остаток долга по продажам смены) —
        // его и оставляем, чтобы окно и печать показывали одну и ту же цифру сервера.
        if (_report is not null)
            return;
        // Окно могло заменить свою копию смены свежей с сервера — долг нужен и ей (печать).
        if (_shift is not null)
            _shift.DebtSales = debt;
        SetDebtText(debt);
    }

    /// <summary>2026-09-27: Z-отчёт после закрытия смены ждал расчёта долга (список продаж и
    /// сделка по каждой долговой продаже — от 0,4 до нескольких секунд) и только потом
    /// открывался. Теперь он открывается сразу, в плитке «Долг» — «…», а долг подставляется,
    /// когда посчитается; <paramref name="fallback"/> — если посчитать не удалось (как раньше).
    /// Печать дожидается долга, чтобы бумажный отчёт был тем же, что и раньше.</summary>
    public Task ApplyDebtWhenResolvedAsync(Task<decimal?> pending, decimal? fallback)
    {
        DebtText.Text = "…";
        return _pendingDebt = ApplyAsync();

        async Task ApplyAsync()
        {
            decimal? debt = null;
            try
            {
                debt = await pending.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Отчёт смены: долг не посчитан: {ex.Message}", "SHIFT");
            }

            debt ??= fallback;
            // Отчёт смены сервера пришёл раньше — долг из него (2026-09-28, см. RefreshDebtDisplay).
            if (_report is { } report)
                debt = report.Debt;
            if (_shift is not null)
                _shift.DebtSales = debt;
            SetDebtText(debt);
        }
    }

    /// <summary>Подпись валюты на экране (на печатном отчёте остаётся «сом»).</summary>
    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;

    private void Tile_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_shift is null || sender is not Control { Tag: string kind })
            return;
        // 2026-10-05: как плитки «Финансов» (FinanceWindow.Kpi_PointerPressed) — на телефоне касания во время
        // открытия окна снова попадали в плитку и открывали окно за окном. Окно — после касания, одно.
        e.Handled = true;
        OpenTile(kind);
    }

    /// <summary>2026-10-22 (модалки, пакет 2): плитка в фокусе (Tab) открывается Enter или пробелом — как касанием.</summary>
    private void Tile_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space) || _shift is null || sender is not Control { Tag: string kind })
            return;
        e.Handled = true;
        OpenTile(kind);
    }

    private void OpenTile(string kind)
    {
        if (_drillDownOpen)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_drillDownOpen || _shift is null)
                return;
            _drillDownOpen = true;
            try
            {
                PosDialogHost.Show(new ShiftDrillDownDialog(_shift, kind, IsServerShift(_shift.Id) ? SaleRows() : null), this);
            }
            finally
            {
                _drillDownOpen = false;
            }
        });
    }

    private bool _drillDownOpen;

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }

    /// <summary>2026-09-14, по просьбе пользователя ("нужен подробный отчёт при закрытии
    /// смены, как на вебке") — печатает ту же разбивку, что показана на экране, плюс
    /// начальную сумму и расхождение факта с ожидаемым остатком (нужны для сверки кассы, но
    /// на самом диалоге не показываются — там уже есть отдельный расчёт до закрытия, см.
    /// CloseShiftDialog).</summary>
    private async void Print_Click(object? sender, RoutedEventArgs e)
    {
        if (_shift is null || PrintButton is null)
            return;

        PrintButton.IsEnabled = false;
        try
        {
            // Долг закрытой смены ещё считается — печатаем, когда он придёт (см. ApplyDebtWhenResolvedAsync).
            if (_pendingDebt is { IsCompleted: false } pendingDebt)
                await pendingDebt.ConfigureAwait(true);
            // Отчёт смены сервера ещё грузится — печатаем с его цифрами, как на экране (2026-09-28).
            if (_reportTask is { IsCompleted: false } pendingReport)
                await pendingReport.ConfigureAwait(true);

            var returns = _report is { ReturnsTotal: > 0m } r
                ? (r.ReturnsCount, r.ReturnsTotal)
                : _serverShiftReturns;
            var rentals = _rentalsTask is { } rentalsTask ? await rentalsTask.ConfigureAwait(true) : null;
            var report = BuildPrintableReport(_shift, _report, returns, rentals);
            var ok = await App.GetRequiredService<ICashShiftService>().PrintReportAsync(report).ConfigureAwait(true);
            if (!ok)
                PosMessageBox.Show(this,
                    Tr.T("Не удалось напечатать отчёт — проверьте подключение принтера.",
                        "Отчётту басып чыгаруу мүмкүн болгон жок — принтердин туташканын текшериңиз.",
                        "Could not print the report — check the printer connection.",
                        "Rapor yazdırılamadı — yazıcı bağlantısını kontrol edin.",
                        "Hisobotni chop etib bo'lmadi — printer ulanishini tekshiring."),
                    Tr.T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PrintButton.IsEnabled = true;
        }
    }

    /// <param name="server">2026-09-28: отчёт смены сервера (BE-10); с ним печатаются смешанная
    /// оплата раздельно, скидки, возвраты, внесения/изъятия и ожидаемый остаток сервера. null —
    /// как раньше, по итогам смены и журналу этой кассы.</param>
    /// <param name="returns">Возвраты смены с сервера (число, сумма); null — не известны.</param>
    private static string BuildPrintableReport(ShiftModel shift, ServerShiftReport? server = null, (int Count, decimal Sum)? returns = null,
        RentalShiftSummary? rentals = null)
    {
        var shortNumber = string.IsNullOrWhiteSpace(shift.ShiftNumber)
            ? "—"
            : (shift.ShiftNumber.Length > 8 ? shift.ShiftNumber[..8].ToUpperInvariant() : shift.ShiftNumber.ToUpperInvariant());

        // 2026-10-27 (аудит кассы, С-8, Н-19): раньше и X, и Z печатались одним заголовком «ОТЧЁТ ПО СМЕНЕ» и только по-русски;
        // X (смена открыта) вообще не печатал «в ящике должно быть». Теперь заголовок — X или Z, подписи на 5 языках (Tr.T),
        // ожидаемая сумма в ящике печатается всегда, фактическая и расхождение — когда смена закрыта (пересчёт сделан).
        var closed = shift.ClosedAt is not null
                     || (shift.Status ?? "").Contains("Закрыт", StringComparison.OrdinalIgnoreCase)
                     || (shift.Status ?? "").Contains("closed", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(closed
            ? Tr.T("*** Z-ОТЧЁТ (смена закрыта) ***", "*** Z-ОТЧЁТ (смена жабылды) ***", "*** Z-REPORT (shift closed) ***", "*** Z RAPORU (vardiya kapandı) ***", "*** Z-HISOBOT (smena yopildi) ***")
            : Tr.T("*** X-ОТЧЁТ (смена открыта) ***", "*** X-ОТЧЁТ (смена ачык) ***", "*** X-REPORT (shift open) ***", "*** X RAPORU (vardiya açık) ***", "*** X-HISOBOT (smena ochiq) ***"));
        sb.AppendLine($"{Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena")}: {shortNumber}");
        sb.AppendLine($"{Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")}: {(string.IsNullOrWhiteSpace(shift.Cashier) ? "—" : shift.Cashier)}");
        sb.AppendLine($"{Tr.T("Открыта", "Ачылган", "Opened", "Açılış", "Ochilgan")}: {shift.OpenedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—"}");
        sb.AppendLine($"{Tr.T("Закрыта", "Жабылган", "Closed", "Kapanış", "Yopilgan")}: {shift.ClosedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—"}");
        sb.AppendLine("------------------------------");
        sb.AppendLine($"{Tr.T("Чеков", "Чектер", "Receipts", "Fiş sayısı", "Cheklar")}: {shift.SalesCount?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        // 2026-10-23 (аудит К5, п. 3): выручка без долга + продажи в долг отдельной строкой.
        sb.AppendLine($"{Tr.T("Выручка (без долга)", "Түшүм (карызсыз)", "Revenue (excl. debt)", "Ciro (borçsuz)", "Tushum (qarzsiz)")}: {shift.RevenueNoDebt.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (shift.CreditSales > 0m)
            sb.AppendLine($"  {Tr.T("продажи в долг", "карызга сатуулар", "credit sales", "veresiye satışlar", "qarzga sotuvlar")}: {shift.CreditSales.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (shift.CashSales is { } cash)
            sb.AppendLine($"  {Tr.T("наличные", "накталай", "cash", "nakit", "naqd")}: {cash.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (server is { MixedCash: > 0m })
            sb.AppendLine($"    {Tr.T("в т.ч. смешанная", "анын ичинде аралаш", "incl. mixed", "karışık dahil", "shu jumladan aralash")}: {server.MixedCash.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        // 2026-10-04, стресс-тест (К4): наличные — с предоплатой долга (как в окне закрытия и у сервера),
        // сама предоплата — строкой «в т.ч.», а не отдельным приходом ниже (было: «Внесено при продаже в долг»).
        if (server is { DebtPrepaymentsCash: > 0m })
            sb.AppendLine($"    {Tr.T("в т.ч. предоплата долга", "анын ичинде карыздын алдын ала төлөмү", "incl. debt prepayment", "borç ön ödemesi dahil", "shu jumladan qarz oldindan to'lovi")}: {server.DebtPrepaymentsCash!.Value.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (shift.NonCashSales is { } card)
            sb.AppendLine($"  {Tr.T("безналичные", "накталай эмес", "non-cash", "nakitsiz", "naqd emas")}: {card.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (server is { MixedCard: > 0m })
            sb.AppendLine($"    {Tr.T("в т.ч. смешанная", "анын ичинде аралаш", "incl. mixed", "karışık dahil", "shu jumladan aralash")}: {server.MixedCard.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (server is { DebtPrepaymentsNonCash: > 0m })
            sb.AppendLine($"    {Tr.T("в т.ч. предоплата долга", "анын ичинде карыздын алдын ала төлөмү", "incl. debt prepayment", "borç ön ödemesi dahil", "shu jumladan qarz oldindan to'lovi")}: {server.DebtPrepaymentsNonCash!.Value.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        if (shift.DebtSales is { } debt)
            sb.AppendLine($"  {Tr.T("долг (остаток)", "карыз (калдык)", "debt (balance)", "borç (bakiye)", "qarz (qoldiq)")}: {debt.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        // Скидки и возвраты — строками отчёта сервера (2026-09-28); без него их печатал только
        // X/Z-отчёт по счётчикам кассы.
        if (server is { Discounts: > 0m })
        {
            sb.AppendLine($"{Tr.T("Скидки", "Арзандатуулар", "Discounts", "İndirimler", "Chegirmalar")}: {server.Discounts.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            if (server.BonusRedeemed > 0m)
                sb.AppendLine($"  {Tr.T("из них бонусами", "анын ичинде бонус менен", "of which points", "puanla ödenen", "shundan bonus bilan")}: {server.BonusRedeemed.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        }
        if (returns is { Sum: > 0m } ret)
            sb.AppendLine($"{Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar")}: {ret.Count} / {ret.Sum.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        sb.AppendLine("------------------------------");

        // Внесения и изъятия из денежного ящика. Раньше отчёт их не знал вовсе: «ожидаемый
        // остаток» считался как начальная сумма плюс наличная выручка, поэтому изъятие 40 000
        // в середине смены печаталось как расхождение на те же 40 000 — и противоречило окну
        // закрытия смены, которое эти операции учитывает (см. MainWindow.CloseShiftAsync).
        // Приходы и расходы берём у сервера: он знает обо всех операциях, включая сделанные
        // на вебе и на других кассах. Локальный cash_history.json — только запасной вариант на
        // случай, когда сервер этих полей не прислал (старая версия API или работа офлайн).
        var (localDeposits, localWithdrawals) = ShiftCashOperationsStore.SumsForShift(shift.Id);
        var deposits = shift.IncomeTotal ?? localDeposits;
        var withdrawals = shift.ExpenseTotal ?? localWithdrawals;
        // 2026-09-28: с отчётом смены сервера внесения и изъятия — его отдельные поля deposits/
        // withdrawals (income_total туда же включает, например, наличную оплату долгов — она
        // печатается своей строкой «Прочие приходы»).
        var otherIncome = 0m;
        // 2026-10-01, ТЗ-BE-2026-04 AN-02 (сделано на сервере): наличные, внесённые при продаже в долг,
        // и оплата долгов наличными — отдельными строками; обе входят в ожидаемую наличность сервера.
        var debtPrepaidCash = server?.DebtPrepaymentsCash ?? 0m;
        var debtPaidCash = server?.DebtPaymentsCash ?? 0m;
        if (server is not null)
        {
            deposits = server.Deposits;
            withdrawals = server.Withdrawals;
            // 2026-10-04, стресс-тест: income_total сервера включает и наличные, внесённые при продаже в
            // долг (смена 7e33b63b: 150 = внесение 100 + оплата долгов 30 + предоплата 20). Без вычета
            // предоплаты она печаталась дважды — своей строкой и ещё раз в «Прочих приходах».
            otherIncome = Math.Max(0m, server.IncomeTotal - server.Deposits - debtPaidCash - debtPrepaidCash);
        }
        // 2026-10-04 (К4): предоплата долга наличными уже в «наличных» выше (строкой «в т.ч.») — здесь её
        // больше не печатаем, иначе читатель сложил бы её второй раз. Из «Прочих приходов» она по-прежнему
        // вычитается.
        if (deposits > 0m || withdrawals > 0m || otherIncome > 0m || debtPaidCash > 0m)
        {
            if (deposits > 0m)
                sb.AppendLine($"{Tr.T("Внесения", "Киргизүүлөр", "Deposits", "Para girişleri", "Kiritishlar")}: +{deposits.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            if (debtPaidCash > 0m)
                sb.AppendLine($"{Tr.T("Оплата долгов наличными", "Карыздарды накталай төлөө", "Debt payments in cash", "Nakit borç ödemeleri", "Qarz to'lovlari naqd")}: +{debtPaidCash.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            if (otherIncome > 0m)
                // Погашения долгов сервер сам пишет в смену (debt_repayment) — они здесь, одной строкой
                // сервера; локальный счётчик «Оплата долгов» к ней не прибавляется.
                sb.AppendLine($"{Tr.T("Прочие приходы (оплата долгов и др.)", "Башка түшүүлөр (карыз төлөө ж.б.)", "Other income (debt payments etc.)", "Diğer gelirler (borç ödemeleri vb.)", "Boshqa tushumlar (qarz to'lovlari va b.)")}: +{otherIncome.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            if (withdrawals > 0m)
                sb.AppendLine($"{Tr.T("Изъятия (расход)", "Алуулар (чыгым)", "Withdrawals (expense)", "Para çıkışları (gider)", "Olishlar (xarajat)")}: -{withdrawals.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            sb.AppendLine("------------------------------");
        }

        if (shift.OpeningCash is { } opening)
        {
            sb.AppendLine($"{Tr.T("Начальная сумма", "Баштапкы сумма", "Opening amount", "Başlangıç tutarı", "Boshlang'ich summa")}: {opening.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            // Ожидаемый остаток тоже с сервера (или из окна закрытия — MainWindow кладёт его в ExpectedCash), если он есть: свой расчёт
            // повторял бы серверный и расходился с ним ровно на те операции, о которых касса не знает.
            // 2026-10-27 (С-7): запасная формула — общая (ShiftDrawerMath): начало + наличные + оплаты долгов + внесения − изъятия − наличные возвраты.
            // Раньше она не знала внесений, оплат долгов и возвратов, и Z без ответа сервера расходился с окном закрытия.
            var fallbackDeposits = server?.Deposits ?? localDeposits;
            var expected = shift.ExpectedCash
                ?? FallbackExpected(shift, server, opening, fallbackDeposits, withdrawals, ReturnsForExpected(shift, server, returns));
            sb.AppendLine($"{Tr.T("В ящике должно быть", "Кассада болушу керек", "Should be in the drawer", "Çekmecede olması gereken", "Qutida bo'lishi kerak")}: {expected.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            if (shift.ClosingCash is { } actual)
            {
                var diff = actual - expected;
                sb.AppendLine($"{Tr.T("Фактический остаток", "Чыныгы калдык", "Actual balance", "Fiili bakiye", "Haqiqiy qoldiq")}: {actual.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
                sb.AppendLine($"{Tr.T("Расхождение", "Айырма", "Difference", "Fark", "Farq")}: {diff.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} {Som}");
            }
            else if (!closed && IsServerShift(shift.Id))
            {
                // 2026-10-27 (С-9): X-отчёт — по ответу сервера; «Касса» в шапке считает ещё и то, что до сервера не дошло.
                var (pendingDeposits, pendingWithdrawals) = ShiftCashOperationsStore.PendingSumsForShift(shift.Id);
                var notOnServer = ShiftDrawerMath.NotOnServer(pendingDeposits, pendingWithdrawals, OfflineQueueCash.Snapshot(shift.Id).Cash.Values.Sum());
                if (notOnServer != 0m)
                    sb.AppendLine($"{Tr.T("Ещё не на сервере", "Серверде дагы жок", "Not on the server yet", "Henüz sunucuda değil", "Hali serverda yo'q")}: {notOnServer.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} {Som}; "
                                  + $"{Tr.T("с ними в ящике", "алар менен кассада", "with them in the drawer", "bunlarla çekmecede", "ular bilan qutida")}: {(expected + notOnServer).ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
            }
        }
        else if (shift.ClosingCash is { } actualOnly)
        {
            sb.AppendLine($"{Tr.T("Фактический остаток", "Чыныгы калдык", "Actual balance", "Fiili bakiye", "Haqiqiy qoldiq")}: {actualOnly.ToString("0.00", CultureInfo.InvariantCulture)} {Som}");
        }
        // 2026-10-04, клиент: прокат в Z-отчёте.
        if (rentals is { IsEmpty: false })
        {
            sb.AppendLine("------------------------------");
            foreach (var line in rentals.PrintLines())
                sb.AppendLine(line);
        }
        sb.AppendLine("------------------------------");
        sb.AppendLine("NurMarket Kassa");
        return sb.ToString();
    }
}
