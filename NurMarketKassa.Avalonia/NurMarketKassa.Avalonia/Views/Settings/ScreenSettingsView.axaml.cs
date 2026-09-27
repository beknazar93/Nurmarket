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

        BuildLayoutPicker();
        // Миниатюры нарисованы цветами темы и подписаны на языке программы — после смены
        // языка перестраиваем.
        Tr.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnLanguageChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            BuildLayoutPicker();
        };
    }

    private void OnLanguageChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(BuildLayoutPicker);

    /// <summary>Выбор вида кассы (2026-09-28): карточка на каждую раскладку — миниатюра цветами
    /// текущей темы, название и одна строка о том, чем она отличается. Нажатие применяет раскладку
    /// сразу (App.ApplyMainLayoutMode) — касса за окном настроек перестраивается на глазах.</summary>
    private void BuildLayoutPicker()
    {
        LayoutPickerPanel.Children.Clear();
        var current = KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode);

        foreach (var option in KassaLayouts.All)
        {
            var isActive = option.Id == current;
            var title = new Avalonia.Controls.TextBlock
            {
                Text = option.Label(),
                FontSize = 14,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            title.Bind(Avalonia.Controls.TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 10, 0, 4) };
            header.Children.Add(title);
            if (isActive)
            {
                var badgeText = new Avalonia.Controls.TextBlock
                {
                    Text = Tr.T("✓ Выбрана", "✓ Тандалган", "✓ Selected", "✓ Seçili", "✓ Tanlangan"),
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                };
                badgeText.Bind(Avalonia.Controls.TextBlock.ForegroundProperty, this.GetResourceObservable("BrushAccentForeground"));
                var badge = new Border
                {
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(8, 2),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = badgeText,
                };
                badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccent"));
                Grid.SetColumn(badge, 1);
                header.Children.Add(badge);
            }

            var description = new Avalonia.Controls.TextBlock
            {
                Text = option.Description(),
                FontSize = 11.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxLines = 5,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(description, option.Description());
            description.Bind(Avalonia.Controls.TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

            var card = new Button
            {
                Tag = option.Id,
                Width = 252,
                Margin = new Avalonia.Thickness(0, 0, 10, 10),
                Padding = new Avalonia.Thickness(12),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
                BorderThickness = new Avalonia.Thickness(isActive ? 2 : 1),
                CornerRadius = new Avalonia.CornerRadius(12),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Children = { KassaLayouts.BuildPreview(option.Id, 226, 134), header, description },
                },
            };
            card.Classes.Add("LayoutCard");
            card.Bind(Button.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
            card.Bind(Button.BorderBrushProperty, this.GetResourceObservable(isActive ? "BrushAccentStrong" : "BrushBorder"));
            Avalonia.Automation.AutomationProperties.SetName(card, option.Label());
            card.Click += LayoutCard_Click;
            LayoutPickerPanel.Children.Add(card);
        }
    }

    private void LayoutCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        App.ApplyMainLayoutMode(id);
        BuildLayoutPicker();
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
