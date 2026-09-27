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
        CashboxCombo.ItemsSource = new[] { new CashboxOption(null, Tr.T("Авто (первая активная касса)", "Авто (биринчи активдүү касса)", "Auto (first active till)", "Otomatik (ilk aktif kasa)", "Avto (birinchi faol kassa)")) };
        CashboxCombo.SelectedIndex = 0;

        _suppressUiScaleChange = true;
        UiScaleSlider.Value = UserPreferences.Instance.UiScalePercent;
        UpdateUiScaleValueText(UserPreferences.Instance.UiScalePercent);
        TileSizeSlider.Value = UserPreferences.Instance.CatalogTileScalePercent;
        TileSizeValueText.Text = $"{UserPreferences.Instance.CatalogTileScalePercent:F0}%";
        _suppressUiScaleChange = false;

        // Окно настроек подгоняется под экран уже после конструктора — подпись с реальным
        // масштабом обновляем, когда страница оказалась на экране.
        AttachedToVisualTree += (_, _) => UpdateUiScaleValueText(UiScaleSlider.Value);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    public double UiScalePercent => UiScaleSlider.Value;

    /// <summary>Ползунок — это выбор кассира, а на экране может стоять меньше: масштаб никогда
    /// не выходит за то, что помещается на экран (см. UiScaleHelper.ComputeScale). Раньше здесь
    /// показывалось только значение ползунка — «100%» при реальных 78% на 1024×768, и было не
    /// понять, почему касса вдруг выросла или почему ползунок «не работает».</summary>
    private void UpdateUiScaleValueText(double percent)
    {
        var text = $"{percent:F0}%";
        var actual = TryGetAppliedUiScalePercent();
        if (actual is { } applied && Math.Abs(applied - percent) >= 0.5)
        {
            text = Tr.T(
                $"{percent:F0}% (на этом экране {applied:F0}%)",
                $"{percent:F0}% (бул экранда {applied:F0}%)",
                $"{percent:F0}% ({applied:F0}% on this screen)",
                $"{percent:F0}% (bu ekranda {applied:F0}%)",
                $"{percent:F0}% (bu ekranda {applied:F0}%)");
        }

        UiScaleValueText.Text = text;
    }

    /// <summary>Реально применённый масштаб: у кассы, если она открыта, иначе (программа
    /// владельца) — у самого окна настроек.</summary>
    private double? TryGetAppliedUiScalePercent()
    {
        try
        {
            return UiScaleHelper.GetAppliedPercent(App.GetRequiredService<MainWindowHostBridge>().Window)
                   ?? UiScaleHelper.GetAppliedPercent(TopLevel.GetTopLevel(this) as Control);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private CancellationTokenSource? _uiScaleSaveDebounceCts;

    private void UiScaleSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiScaleChange)
        {
            UpdateUiScaleValueText(e.NewValue);
            return;
        }

        // Живое применение сразу при перетаскивании ползунка (та же схема, что уже
        // используется для GlassOpacitySlider/LiquidGlassToggle в MarketplaceView) —
        // не нужно ждать "Сохранить", чтобы увидеть эффект. Само масштабирование —
        // дешёвая операция (просто ScaleTransform), но запись на диск на каждый
        // промежуточный тик перетаскивания ползунка (десятки раз в секунду) грузила
        // диск/UI-поток на слабых устройствах — SaveToDisk откладываем до остановки.
        UserPreferences.Instance.UiScalePercent = e.NewValue;
        App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
        UiScaleChanged?.Invoke(this, EventArgs.Empty);
        // После применения — чтобы подпись показала, что реально получилось на этом экране.
        UpdateUiScaleValueText(e.NewValue);
        ScheduleUiScaleSaveDebounce();
    }

    /// <summary>Размер карточек применяется сразу, пока тянут ползунок, — как и масштаб.</summary>
    private void TileSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        TileSizeValueText.Text = $"{e.NewValue:F0}%";
        if (_suppressUiScaleChange)
            return;

        UserPreferences.Instance.CatalogTileScalePercent = e.NewValue;
        AccentThemeService.ApplyCatalogTileSize();
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
        CashboxCombo.SelectedItem as CashboxOption ?? new CashboxOption(null, Tr.T("Авто (первая активная касса)", "Авто (биринчи активдүү касса)", "Auto (first active till)", "Otomatik (ilk aktif kasa)", "Avto (birinchi faol kassa)"));

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
        CashboxCombo.ItemsSource = new[] { new CashboxOption(null, Tr.T("Авто (первая активная касса)", "Авто (биринчи активдүү касса)", "Auto (first active till)", "Otomatik (ilk aktif kasa)", "Avto (birinchi faol kassa)")), placeholder };
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

            var options = new List<CashboxOption> { new(null, Tr.T("Авто (первая активная касса)", "Авто (биринчи активдүү касса)", "Auto (first active till)", "Otomatik (ilk aktif kasa)", "Avto (birinchi faol kassa)")) };
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
