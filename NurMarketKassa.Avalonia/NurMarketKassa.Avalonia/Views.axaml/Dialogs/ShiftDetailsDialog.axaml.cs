using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        if (RefreshFromServer)
            _ = RefreshFromServerAsync(shift.Id);
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
        ShiftProductsNote.Text = result.OrderDiscount > 0.005m
            ? Tr.T($"По товарам {lines} − скидка на чек {discount} = {total} {Som}. Продажи в долг и возвраты сюда не входят.",
                $"Товарлар боюнча {lines} − чекке арзандатуу {discount} = {total} {Som}. Карызга сатуу жана кайтаруулар кирбейт.",
                $"Products {lines} − receipt discount {discount} = {total} {Som}. Credit sales and returns are not included.",
                $"Ürünler {lines} − fiş indirimi {discount} = {total} {Som}. Veresiye satışlar ve iadeler dahil değildir.",
                $"Mahsulotlar {lines} − chekka chegirma {discount} = {total} {Som}. Qarzga sotuvlar va qaytarishlar kirmaydi.")
            : Tr.T($"Итого по товарам: {total} {Som}. Продажи в долг и возвраты сюда не входят.",
                $"Товарлар боюнча жыйынтык: {total} {Som}. Карызга сатуу жана кайтаруулар кирбейт.",
                $"Products total: {total} {Som}. Credit sales and returns are not included.",
                $"Ürünler toplamı: {total} {Som}. Veresiye satışlar ve iadeler dahil değildir.",
                $"Mahsulotlar bo'yicha jami: {total} {Som}. Qarzga sotuvlar va qaytarishlar kirmaydi.");
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
        CashierText.Text = string.IsNullOrWhiteSpace(shift.Cashier) ? "—" : shift.Cashier;
        // Статус приходит данными («Открыта»/«Закрыта» или open/closed) — на экран на языке программы.
        var rawStatus = shift.Status ?? "";
        StatusText.Text = rawStatus.Length == 0 ? "—"
            : rawStatus.Contains("Закрыт", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Закрыта", "Жабык", "Closed", "Kapalı", "Yopiq")
            : rawStatus.Contains("Открыт", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("open", StringComparison.OrdinalIgnoreCase)
                ? Tr.T("Открыта", "Ачык", "Open", "Açık", "Ochiq")
            : rawStatus;
        RevenueText.Text = $"{shift.Revenue:N2} {Som}";
        SalesCountText.Text = shift.SalesCount?.ToString() ?? "—";
        CashText.Text = shift.CashSales is { } cash ? $"{cash:N2} {Som}" : "—";
        CardText.Text = shift.NonCashSales is { } card ? $"{card:N2} {Som}" : "—";
        DebtText.Text = shift.DebtSales is { } debt ? $"{debt:N2} {Som}" : "—";

        // Смешанная оплата: её наличная часть уже в «Наличных», безналичная — в «Безналичных»;
        // строкой ниже — сколько из них пришло смешанной (раньше сервер разбивки не хранил и
        // смешанная целиком уходила в безнал).
        var mixedCash = _report?.MixedCash ?? 0m;
        var mixedCard = _report?.MixedCard ?? 0m;
        CashMixedText.IsVisible = mixedCash > 0m;
        CashMixedText.Text = MixedLine(mixedCash);
        CardMixedText.IsVisible = mixedCard > 0m;
        CardMixedText.Text = MixedLine(mixedCard);

        BindExtraTotals(shift.Id, shift.ExpenseTotal);

        // Внесения и изъятия — с сервера (он знает и о сделанных на других компьютерах), без
        // отчёта сервера — как раньше, по журналу этой кассы.
        var (deposits, withdrawals) = _report is { } fromServer
            ? (fromServer.Deposits, fromServer.Withdrawals)
            : ShiftCashOperationsStore.SumsForShift(shift.Id);
        CashOpsPanel.IsVisible = deposits > 0m || withdrawals > 0m;
        DepositsText.Text = $"+{deposits:N2} {Som}";
        WithdrawalsText.Text = $"-{withdrawals:N2} {Som}";

        if (shift.IsActive)
        {
            StatusBadge.Background = ThemeBrush("BrushSuccessSoft", Brushes.DarkGreen);
            StatusDot.Fill = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            StatusText.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
        }
        else
        {
            StatusBadge.Background = ThemeBrush("BrushDangerSoft", Brushes.DarkRed);
            StatusDot.Fill = ThemeBrush("BrushDanger", Brushes.Red);
            StatusText.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        }
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
        var debtPaid = Get(ShiftEventsStore.KindDebtPayment);

        ReturnsText.Text = returns > 0.005 ? Money(returns) : "—";
        WriteOffsText.Text = writeOffs > 0.005 ? Money(writeOffs) : "—";
        ExpenseText.Text = expenses > 0.005 ? Money(expenses) : "—";
        DebtPaidText.Text = debtPaid > 0.005 ? Money(debtPaid) : "—";

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

    /// <summary>Подпись под плиткой «Наличные»/«Безналичные»: сколько из суммы — смешанной оплатой.</summary>
    private static string MixedLine(decimal amount) => Tr.T(
        $"в т. ч. смешанная: {amount:N2}",
        $"анын ичинде аралаш: {amount:N2}",
        $"incl. mixed: {amount:N2}",
        $"karışık dahil: {amount:N2}",
        $"shu jumladan aralash: {amount:N2}");

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
        DebtText.Text = $"{debt:N2} {Som}";
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
            DebtText.Text = debt is { } value ? $"{value:N2} {Som}" : "—";
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

        PosDialogHost.Show(new ShiftDrillDownDialog(_shift, kind, IsServerShift(_shift.Id) ? SaleRows() : null), this);
    }

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
            var report = BuildPrintableReport(_shift, _report, returns);
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
    private static string BuildPrintableReport(ShiftModel shift, ServerShiftReport? server = null, (int Count, decimal Sum)? returns = null)
    {
        var shortNumber = string.IsNullOrWhiteSpace(shift.ShiftNumber)
            ? "—"
            : (shift.ShiftNumber.Length > 8 ? shift.ShiftNumber[..8].ToUpperInvariant() : shift.ShiftNumber.ToUpperInvariant());

        var sb = new StringBuilder();
        sb.AppendLine("*** ОТЧЁТ ПО СМЕНЕ ***");
        sb.AppendLine($"Смена: {shortNumber}");
        sb.AppendLine($"Кассир: {(string.IsNullOrWhiteSpace(shift.Cashier) ? "—" : shift.Cashier)}");
        sb.AppendLine($"Открыта: {shift.OpenedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—"}");
        sb.AppendLine($"Закрыта: {shift.ClosedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—"}");
        sb.AppendLine("------------------------------");
        sb.AppendLine($"Чеков: {shift.SalesCount?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        sb.AppendLine($"Выручка: {shift.Revenue.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (shift.CashSales is { } cash)
            sb.AppendLine($"  наличные: {cash.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (server is { MixedCash: > 0m })
            sb.AppendLine($"    в т.ч. смешанная: {server.MixedCash.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (shift.NonCashSales is { } card)
            sb.AppendLine($"  безналичные: {card.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (server is { MixedCard: > 0m })
            sb.AppendLine($"    в т.ч. смешанная: {server.MixedCard.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (shift.DebtSales is { } debt)
            sb.AppendLine($"  в долг: {debt.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        // Скидки и возвраты — строками отчёта сервера (2026-09-28); без него их печатал только
        // X/Z-отчёт по счётчикам кассы.
        if (server is { Discounts: > 0m })
        {
            sb.AppendLine($"Скидки: {server.Discounts.ToString("0.00", CultureInfo.InvariantCulture)} сом");
            if (server.BonusRedeemed > 0m)
                sb.AppendLine($"  из них бонусами: {server.BonusRedeemed.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        }
        if (returns is { Sum: > 0m } ret)
            sb.AppendLine($"Возвраты: {ret.Count} на {ret.Sum.ToString("0.00", CultureInfo.InvariantCulture)} сом");
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
        if (server is not null)
        {
            deposits = server.Deposits;
            withdrawals = server.Withdrawals;
            otherIncome = Math.Max(0m, server.IncomeTotal - server.Deposits);
        }
        if (deposits > 0m || withdrawals > 0m || otherIncome > 0m)
        {
            if (deposits > 0m)
                sb.AppendLine($"Внесения: +{deposits.ToString("0.00", CultureInfo.InvariantCulture)} сом");
            if (otherIncome > 0m)
                // Погашения долгов сервер сам пишет в смену (debt_repayment) — они здесь, одной строкой
                // сервера; локальный счётчик «Оплата долгов» к ней не прибавляется.
                sb.AppendLine($"Прочие приходы (оплата долгов и др.): +{otherIncome.ToString("0.00", CultureInfo.InvariantCulture)} сом");
            if (withdrawals > 0m)
                sb.AppendLine($"Изъятия (расход): -{withdrawals.ToString("0.00", CultureInfo.InvariantCulture)} сом");
            sb.AppendLine("------------------------------");
        }

        if (shift.OpeningCash is { } opening)
        {
            sb.AppendLine($"Начальная сумма: {opening.ToString("0.00", CultureInfo.InvariantCulture)} сом");
            if (shift.ClosingCash is { } actual)
            {
                // Ожидаемый остаток тоже с сервера, если он его прислал: свой расчёт
                // повторял бы серверный и расходился с ним ровно на те операции, о которых
                // касса не знает.
                // Формула ровно та, по которой считает сервер (проверено на живых сменах
                // 2026-09-23): начальная сумма плюс наличная выручка минус расход. Прочие
                // приходы в ожидаемый остаток сервер НЕ включает и показывает отдельной
                // строкой — повторяем это, иначе касса опять разойдётся с сайтом.
                var expected = shift.ExpectedCash
                    ?? (shift.ExpenseTotal is not null
                        ? opening + (shift.CashSales ?? 0m) - withdrawals
                        : opening + (shift.CashSales ?? 0m) + deposits - withdrawals);
                var diff = actual - expected;
                sb.AppendLine($"Ожидаемый остаток: {expected.ToString("0.00", CultureInfo.InvariantCulture)} сом");
                sb.AppendLine($"Фактический остаток: {actual.ToString("0.00", CultureInfo.InvariantCulture)} сом");
                sb.AppendLine($"Расхождение: {diff.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} сом");
            }
        }
        else if (shift.ClosingCash is { } actualOnly)
        {
            sb.AppendLine($"Фактический остаток: {actualOnly.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        }
        sb.AppendLine("------------------------------");
        sb.AppendLine("NurMarket Kassa");
        return sb.ToString();
    }
}
