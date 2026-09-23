using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CloseShiftDialog : Window
{
    // Поле заполняется значением в инвариантной культуре (точка), поэтому и разбор
    // должен быть инвариантным — иначе на ru-RU/de-DE сумма теряется или растёт в 100 раз.
    public decimal? ClosingCash =>
        decimal.TryParse(ClosingCashBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public decimal? SuggestedBalance { get; set; }

    /// <summary>2026-09-12, по просьбе пользователя ("сделай так же как в вебе") — разбивка
    /// (начальная сумма/продажи/наличные/безналичные), заполняется вызывающим кодом так же, как
    /// SuggestedBalance: сначала кэш при открытии диалога, затем свежее значение из фона через
    /// UpdateTotals, чтобы не блокировать сам клик "Закрыть смену".</summary>
    public ShiftBalanceHelper.ShiftTotals? Totals { get; set; }

    public CloseShiftDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            ClosingCashBox.Text = SuggestedBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        ApplyTotals(Totals);
        ClosingCashBox.Focus();
        ClosingCashBox.SelectAll();
        UpdateDiscrepancy();
    }

    /// <summary>Обновление разбивки из фона (см. Totals) — параллельно UpdateSystemBalance.</summary>
    public void UpdateTotals(ShiftBalanceHelper.ShiftTotals? totals)
    {
        Totals = totals;
        ApplyTotals(totals);
    }

    private void ApplyTotals(ShiftBalanceHelper.ShiftTotals? totals)
    {
        if (TotalsBorder is null)
            return;

        // Хотя бы одно значение известно — показываем карточку, остальные строки покажут "—".
        var hasAny = totals is { OpeningCash: not null } or { TotalSales: not null }
            or { CashSales: not null } or { NonCashSales: not null };
        // 2026-09-12, временная диагностика по жалобе "не отображаешь подробно" — снять после
        // подтверждения, что разбивка реально доходит до диалога и карточка становится видимой.
        PosLogger.Log($"ApplyTotals: hasAny={hasAny}, totalsIsNull={totals is null}", "SHIFT");
        TotalsBorder.IsVisible = hasAny;
        if (!hasAny)
            return;

        OpeningCashText.Text = ShiftBalanceHelper.FormatBalance(totals!.OpeningCash);
        TotalSalesText.Text = ShiftBalanceHelper.FormatBalance(totals.TotalSales);
        CashSalesText.Text = ShiftBalanceHelper.FormatBalance(totals.CashSales);
        NonCashSalesText.Text = ShiftBalanceHelper.FormatBalance(totals.NonCashSales);
    }

    /// <summary>2026-09-12, по просьбе пользователя (сравнение со скриншотами веб-версии) —
    /// живой расчёт расхождения фактической суммы с ожидаемой (как на сайте): отрицательное —
    /// недостача, положительное — излишек.</summary>
    private void ClosingCashBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateDiscrepancy();

    /// <summary>2026-09-12: диалог открывается СРАЗУ с уже известным (возможно чуть устаревшим)
    /// балансом — вызывающий код (MainWindow.CloseShiftAsync) в фоне подтягивает свежий с
    /// сервера и вызывает этот метод, когда он придёт, не блокируя сам клик "Закрыть смену".
    /// Если кассир уже начал вручную менять "Фактический остаток" — не перезаписываем его ввод,
    /// только саму строку "Остаток по системе" и расхождение относительно неё.</summary>
    public void UpdateSystemBalance(decimal balance)
    {
        var previousSuggested = SuggestedBalance;
        SuggestedBalance = balance;
        SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(balance);

        var stillDefault = previousSuggested.HasValue
            && decimal.TryParse(ClosingCashBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var current)
            && current == previousSuggested.Value;
        if (stillDefault)
            ClosingCashBox.Text = balance.ToString("0.00", CultureInfo.InvariantCulture);

        UpdateDiscrepancy();
    }

    private void UpdateDiscrepancy()
    {
        if (DiscrepancyBorder is null || DiscrepancyText is null)
            return;

        if (!SuggestedBalance.HasValue || ClosingCash is not { } actual)
        {
            DiscrepancyBorder.IsVisible = false;
            return;
        }

        var discrepancy = actual - SuggestedBalance.Value;
        DiscrepancyBorder.IsVisible = true;
        DiscrepancyText.Text = (discrepancy >= 0 ? "+" : "") + discrepancy.ToString("0.00", CultureInfo.InvariantCulture) + " сом";

        var (background, foreground) = discrepancy switch
        {
            0m => (ThemeBrush("BrushSuccessSoft", "BrushPanelSoft"), ThemeBrush("BrushSuccess", "BrushText")),
            _ => (ThemeBrush("BrushDangerSoft", "BrushPanelSoft"), ThemeBrush("BrushDanger", "BrushText")),
        };
        DiscrepancyBorder.Background = background;
        DiscrepancyText.Foreground = foreground;
    }

    private IBrush ThemeBrush(string key, string fallbackKey) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : Application.Current?.TryFindResource(fallbackKey, ActualThemeVariant, out var fallback) == true && fallback is IBrush fallbackBrush
                ? fallbackBrush
                : Brushes.Gray;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ClosingCashBox.Text) &&
            !decimal.TryParse(ClosingCashBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            PosMessageBox.Show(this,
                Tr.T("Введите корректную сумму.", "Туура суманы киргизиңиз.", "Enter a valid amount.",
                    "Geçerli bir tutar girin.", "To'g'ri summani kiriting."),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
