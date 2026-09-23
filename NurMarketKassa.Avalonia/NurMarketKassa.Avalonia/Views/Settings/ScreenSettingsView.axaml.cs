using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class ScreenSettingsView : UserControl
{
    /// <summary>Id == null represents "Авто (первая активная касса)".</summary>
    public sealed record CashboxOption(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    // Слайдер с Minimum="50" force-коэрсит значение по умолчанию (0) до 50 синхронно во время
    // InitializeComponent() — до того, как тело конструктора успело бы выставить флаг, поэтому
    // он выставлен по умолчанию инициализатором поля (см. тот же приём и объяснение в
    // ThemeSettingsDialog.axaml.cs для FontSizeSlider).
    private bool _suppressUiScaleChange = true;

    public event EventHandler? SaveRequested;

    /// <summary>Раздельно от SaveRequested — масштаб применяется мгновенно при перетаскивании
    /// ползунка (не только по кнопке "Сохранить"), см. UiScaleSlider_ValueChanged. Хозяин этого
    /// UserControl (PosSettingsWindow) подписывается на это, чтобы масштабировать и себя тоже,
    /// не только MainWindow за собой.</summary>
    public event EventHandler? UiScaleChanged;

    public ScreenSettingsView()
    {
        InitializeComponent();
        CashboxCombo.ItemsSource = new[] { new CashboxOption(null, "Авто (первая активная касса)") };
        CashboxCombo.SelectedIndex = 0;

        _suppressUiScaleChange = true;
        UiScaleSlider.Value = UserPreferences.Instance.UiScalePercent;
        UpdateUiScaleValueText(UserPreferences.Instance.UiScalePercent);
        _suppressUiScaleChange = false;
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    public double UiScalePercent => UiScaleSlider.Value;

    private void UpdateUiScaleValueText(double percent) =>
        UiScaleValueText.Text = $"{percent:F0}%";

    private CancellationTokenSource? _uiScaleSaveDebounceCts;

    private void UiScaleSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateUiScaleValueText(e.NewValue);
        if (_suppressUiScaleChange)
            return;

        // Живое применение сразу при перетаскивании ползунка (та же схема, что уже
        // используется для GlassOpacitySlider/LiquidGlassToggle в MarketplaceView) —
        // не нужно ждать "Сохранить", чтобы увидеть эффект. Само масштабирование —
        // дешёвая операция (просто ScaleTransform), но запись на диск на каждый
        // промежуточный тик перетаскивания ползунка (десятки раз в секунду) грузила
        // диск/UI-поток на слабых устройствах — SaveToDisk откладываем до остановки.
        UserPreferences.Instance.UiScalePercent = e.NewValue;
        App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
        UiScaleChanged?.Invoke(this, EventArgs.Empty);
        ScheduleUiScaleSaveDebounce();
    }

    private void ScheduleUiScaleSaveDebounce()
    {
        var cts = new CancellationTokenSource();
        _uiScaleSaveDebounceCts?.Cancel();
        _uiScaleSaveDebounceCts = cts;
        _ = RunDebouncedUiScaleSaveAsync(cts);
    }

    private async Task RunDebouncedUiScaleSaveAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
            UserPreferences.Instance.SaveToDisk();
    }

    /// <summary>Выбранная касса: null означает "Авто".</summary>
    public CashboxOption SelectedCashbox =>
        CashboxCombo.SelectedItem as CashboxOption ?? new CashboxOption(null, "Авто (первая активная касса)");

    /// <summary>Ставит текущее сохранённое значение как выбранное (после первой загрузки списка
    /// список содержит только "Авто" — выбор синхронизируется повторно после RefreshCashboxes_Click).</summary>
    public void PreselectCashbox(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            CashboxCombo.SelectedIndex = 0;
            return;
        }

        if (CashboxCombo.ItemsSource is IEnumerable<CashboxOption> options)
        {
            var match = options.FirstOrDefault(o => o.Id == id);
            if (match is not null)
            {
                CashboxCombo.SelectedItem = match;
                return;
            }
        }

        // Список ещё не загружен с сервера — показываем сохранённое имя как временный пункт,
        // чтобы не выглядело как "сброшено на Авто" пока кассир не нажмёт "Обновить".
        var placeholder = new CashboxOption(id, name ?? id);
        CashboxCombo.ItemsSource = new[] { new CashboxOption(null, "Авто (первая активная касса)"), placeholder };
        CashboxCombo.SelectedItem = placeholder;
    }

    private async void RefreshCashboxes_Click(object? sender, RoutedEventArgs e)
    {
        RefreshCashboxesButton.IsEnabled = false;
        try
        {
            var previousId = SelectedCashbox.Id;
            var raw = await App.ShiftApi.ConstructionCashboxesListAsync().ConfigureAwait(true);
            var boxes = CartDisplayHelper.ListCashboxes(raw);

            var options = new List<CashboxOption> { new(null, "Авто (первая активная касса)") };
            options.AddRange(boxes.Select(b =>
                new CashboxOption(b.Id, b.IsActive ? b.DisplayName : $"{b.DisplayName} (неактивна)")));

            CashboxCombo.ItemsSource = options;
            CashboxCombo.SelectedItem = options.FirstOrDefault(o => o.Id == previousId) ?? options[0];
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cashbox list refresh failed: {ex.Message}", "SETTINGS");
        }
        finally
        {
            RefreshCashboxesButton.IsEnabled = true;
        }
    }
}
