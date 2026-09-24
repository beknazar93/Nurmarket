using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ShiftDetailsDialog : Window
{
    public bool? DialogResult { get; set; }
    private ShiftModel? _shift;

    public ShiftDetailsDialog() => InitializeComponent();

    public ShiftDetailsDialog(object? model) : this()
    {
        if (model is ShiftModel shift)
            BindShift(shift);
    }

    public ShiftDetailsDialog(ShiftModel shift) : this() => BindShift(shift);

    private sealed record ShiftProductRow(string Name, string QuantityText, string RevenueText);

    /// <summary>Товары за время смены — из локальной истории продаж, по убыванию выручки.</summary>
    private void BindShiftProducts(ShiftModel shift)
    {
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

            ShiftProductsTitle.Text = $"Товары за смену: {rows.Count} поз., {rows.Sum(r => r.Qty):0.###} ед.";
            ShiftProductsList.ItemsSource = rows
                .Select(r => new ShiftProductRow(r.Name, $"{r.Qty:0.###}", $"{r.Revenue:N2} сом"))
                .ToList();
            ShiftProductsPanel.IsVisible = true;
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Shift details: products list skipped: {ex.Message}", "SHIFTS");
        }
    }

    private void BindShift(ShiftModel shift)
    {
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
        StatusText.Text = string.IsNullOrWhiteSpace(shift.Status) ? "—" : shift.Status;
        RevenueText.Text = $"{shift.Revenue:N2} сом";
        SalesCountText.Text = shift.SalesCount?.ToString() ?? "—";
        CashText.Text = shift.CashSales is { } cash ? $"{cash:N2} сом" : "—";
        CardText.Text = shift.NonCashSales is { } card ? $"{card:N2} сом" : "—";
        DebtText.Text = shift.DebtSales is { } debt ? $"{debt:N2} сом" : "—";

        BindExtraTotals(shift.Id, shift.ExpenseTotal);

        var (deposits, withdrawals) = ShiftCashOperationsStore.SumsForShift(shift.Id);
        CashOpsPanel.IsVisible = deposits > 0m || withdrawals > 0m;
        DepositsText.Text = $"+{deposits:N2} сом";
        WithdrawalsText.Text = $"-{withdrawals:N2} сом";

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
        static string Money(double value) => $"{value:N2} сом";

        // Тариф: на «Старт» расширенные итоги — платная доп. услуга. Ряд плиток прячем целиком,
        // а не показываем прочерками: прочерк означает «операций не было», и спутать эти два
        // случая нельзя.
        ExtraTotalsRow.IsVisible = TariffGate.CanUseShiftAnalytics;
        if (!ExtraTotalsRow.IsVisible)
            return;

        var events = ShiftEventsStore.TotalsForShift(shiftId);
        double Get(string kind) => events.TryGetValue(kind, out var value) ? value : 0;

        var returns = Get(ShiftEventsStore.KindReturn);
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
        DiscountsText.Text = adjustments.Discounts > 0.005 ? Money(adjustments.Discounts) : "—";

        // Оплату бонусами показываем отдельной строкой под скидкой: она входит в общую сумму
        // скидок, и без пояснения владелец считал бы её дважды.
        PointsRedeemedText.IsVisible = adjustments.PointsRedeemed > 0.005;
        PointsRedeemedText.Text = $"из них бонусами: {adjustments.PointsRedeemed:N2}";
    }

    /// <summary>2026-09-15, живой баг ("Долг для уже закрытых смен в Истории смен показывает
    /// не то") — первичное значение shift.DebtSales может быть из непроверенного поля сервера;
    /// вызывающий код (ShiftHistoryViewModel) уточняет его асинхронно тем же надёжным способом,
    /// что уже работает для только что закрытой смены, и подставляет сюда, если диалог ещё открыт.</summary>
    public void RefreshDebtDisplay(decimal debt) => DebtText.Text = $"{debt:N2} сом";

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;

    private void Tile_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_shift is null || sender is not Control { Tag: string kind })
            return;

        PosDialogHost.Show(new ShiftDrillDownDialog(_shift, kind), this);
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
        if (_shift is not { } shift || PrintButton is null)
            return;

        PrintButton.IsEnabled = false;
        try
        {
            var report = BuildPrintableReport(shift);
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

    private static string BuildPrintableReport(ShiftModel shift)
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
        if (shift.NonCashSales is { } card)
            sb.AppendLine($"  безналичные: {card.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        if (shift.DebtSales is { } debt)
            sb.AppendLine($"  в долг: {debt.ToString("0.00", CultureInfo.InvariantCulture)} сом");
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
        if (deposits > 0m || withdrawals > 0m)
        {
            if (deposits > 0m)
                sb.AppendLine($"Внесения: +{deposits.ToString("0.00", CultureInfo.InvariantCulture)} сом");
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
