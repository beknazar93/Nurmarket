using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ, если поместится!»): управление упаковкой на верхней панели обеих приёмок («Склад → Приёмка» и «Закупки → Приём товара»).
/// Два вида одного и того же состояния:
/// • <see cref="Wide"/> — «Приём: [В штуках | Упаковками | Поштучная продажа]  [Ко всем строкам]  [шт в упак.]» (сегменты как в остальных разделах);
/// • <see cref="Compact"/> — одна кнопка «Приём: В штуках ▾» с выпадающим окном (три пункта, «шт в упак.», «Ко всем строкам») — когда сегменты не помещаются.
/// Какой из видов показан, решает окно (<see cref="ShowWide"/>): по измеренной ширине с запасом (гистерезис), БЕЗ переноса панели на вторую строку.
/// Выбранный способ применяется к НОВЫМ строкам (<see cref="ReceivingPackDefaults.ApplyToNew"/>), к существующим — только кнопкой «Ко всем строкам» (с вопросом, если там уже введены числа).
/// «шт в упак. по умолчанию» запоминается локально (<see cref="ReceivingViewPrefs"/>), в черновик приёмки не пишется. Расчёты упаковок — в ReceivingLineVm/ReceivingPacks.
/// 2026-11-12 (аудит Л-9, решение владельца): СПОСОБ живёт одно открытие приёмки и один день — новый вид стартует «В штуках», после проведения прихода и в новый день способ сбрасывается;
/// пока выбрано не «В штуках» — панель подсвечена тёплым цветом, у поля скана стоит метка (<see cref="ScanBadge"/>). F4 / Shift+F4 — следующий / прежний способ с клавиатуры (У-1).</summary>
internal sealed class ReceivingPackBar
{
    private static int s_groups;

    /// <summary>Часы для проверки «новый день» (стенд подменяет).</summary>
    internal static Func<DateTime> Clock = () => DateTime.Now;

    private DateTime _modeDay;
    private bool _existingNoticeShown;
    private ReceivingPackDefaults.Snapshot? _undo;
    private readonly Border _scanBadge;
    private readonly TextBlock _scanBadgeText;
    private readonly Border _segBox;
    private readonly TextBlock _label;
    private readonly Dictionary<(Control, AvaloniaProperty), IDisposable> _accents = new();
    private readonly Flyout _undoFlyout;
    private readonly TextBlock _undoText;

    private readonly Func<IReadOnlyList<ReceivingLineVm>> _lines;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, bool> _notify;
    private readonly Action _changed;
    private readonly Action _focusScan;
    private readonly Func<string, Button> _makeButton;

    private readonly Dictionary<ReceivingPackDefaultMode, RadioButton> _segments = new();
    private readonly Dictionary<ReceivingPackDefaultMode, Button> _menuItems = new();
    private readonly TextBox _sizeWide;
    private readonly TextBox _sizeMenu;
    private readonly Control _sizeWideHolder;
    private readonly Control _sizeMenuRow;
    private readonly Button _compact;
    private readonly Flyout _flyout;
    private bool _syncing;

    public ReceivingPackBar(Func<IReadOnlyList<ReceivingLineVm>> lines, Func<string, string, Task<bool>> confirm, Action<string, bool> notify, Action changed, Action focusScan, Func<string, Button> makeButton)
    {
        _lines = lines;
        _confirm = confirm;
        _notify = notify;
        _changed = changed;
        _focusScan = focusScan;
        _makeButton = makeButton;
        Mode = ReceivingPackDefaultMode.Pieces; // 2026-11-12 (Л-9): каждое новое открытие приёмки начинается «В штуках» — способ со вчера больше не применяется молча
        _modeDay = Clock().Date;
        DefaultSize = ReceivingViewPrefs.PackSize;

        var group = "ReceivingPackMode" + (++s_groups);
        // ---- вид 1: сегменты
        var label = _label = new TextBlock { Text = LabelText, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in new[] { ReceivingPackDefaultMode.Pieces, ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale })
        {
            var m = mode;
            // MinHeight 40 (а не 44 как у сегментов других разделов): рамка группы 40 + 2 + 2 = 44 — панель «Наценка / Сразу к скану» не растёт по высоте (раскладка Склада).
            var radio = new RadioButton { GroupName = group, Classes = { "sec-seg" }, Content = ModeName(m), MinHeight = 40, Padding = new Thickness(12, 0), IsChecked = m == Mode, Focusable = true };
            // 2026-11-12 (У-1): сегмент доступен с клавиатуры — Tab, стрелки влево/вправо меняют способ (фокус остаётся на панели), F4 — по кругу из любого места приёмки.
            radio.KeyDown += (_, e) => ArrowKey(e);
            ToolTip.SetTip(radio, ModeHint(m));
            Avalonia.Automation.AutomationProperties.SetName(radio, ModeName(m));
            radio.IsCheckedChanged += (_, _) =>
            {
                if (!_syncing && radio.IsChecked == true)
                    Choose(m);
            };
            _segments[m] = radio;
            segPanel.Children.Add(radio);
        }
        var segBox = _segBox = new Border { Classes = { "sec-segment" }, Padding = new Thickness(1), Child = segPanel };
        var applyWide = _makeButton(ApplyText);
        applyWide.Height = 40;
        applyWide.MinHeight = 0;
        ToolTip.SetTip(applyWide, ApplyHint);
        Avalonia.Automation.AutomationProperties.SetName(applyWide, ApplyHint);
        applyWide.Click += async (_, _) => await ApplyToAllAsync().ConfigureAwait(true);
        _sizeWide = SizeBox();
        // Место под поле размера занято всегда (в «В штуках» оно прозрачно и не нажимается): ширина вида не прыгает, сегменты и соседи не сдвигаются.
        _sizeWideHolder = new Border { Child = _sizeWide };
        Wide = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { label, segBox, applyWide, _sizeWideHolder },
        };

        // ---- вид 2: кнопка и окно
        _compact = _makeButton("");
        _compact.MinHeight = 44;
        _compact.Focusable = true; // 2026-11-12 (У-1): кнопку «Приём ▾» можно достать Tab и открыть Пробелом / Enter
        _compact.KeyDown += (_, e) => ArrowKey(e);
        ToolTip.SetTip(_compact, CompactHint);
        Avalonia.Automation.AutomationProperties.SetName(_compact, PackName);
        var menu = new StackPanel { Spacing = 6, Width = 340 };
        menu.Children.Add(new TextBlock { Text = MenuTitle, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var mode in new[] { ReceivingPackDefaultMode.Pieces, ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale })
        {
            var m = mode;
            var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var desc = new TextBlock { Text = ModeHint(m), FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            var item = new Button
            {
                Content = new StackPanel { Spacing = 2, Children = { title, desc } }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 44, Padding = new Thickness(12, 6), Focusable = true, Tag = title,
            };
            item.Classes.Add("sec-outline");
            Avalonia.Automation.AutomationProperties.SetName(item, ModeName(m));
            item.Click += (_, _) =>
            {
                Choose(m);
                _flyout!.Hide();
            };
            _menuItems[m] = item;
            menu.Children.Add(item);
        }
        _sizeMenu = SizeBox();
        _sizeMenu.Width = 96;
        _sizeMenuRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = SizeLabel, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 }, _sizeMenu },
        };
        menu.Children.Add(_sizeMenuRow);
        var applyMenu = _makeButton(ApplyText);
        applyMenu.MinHeight = 44;
        applyMenu.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(applyMenu, ApplyHint);
        applyMenu.Click += async (_, _) =>
        {
            _flyout!.Hide();
            await ApplyToAllAsync().ConfigureAwait(true);
        };
        menu.Children.Add(applyMenu);
        _flyout = new Flyout { Content = menu, Placement = PlacementMode.BottomEdgeAlignedLeft };
        _compact.Click += (_, _) => _flyout.ShowAt(_compact);
        Compact = _compact;

        Host = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Content = Wide, IsTabStop = false, Focusable = false };

        // 2026-11-12 (Л-9): метка у поля скана — пока способ не «В штуках», она горит рядом с полем, где кассир сканирует (окно кладёт её через AttachScanBox).
        _scanBadgeText = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        _scanBadge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Child = _scanBadgeText };
        Avalonia.Automation.AutomationProperties.SetName(_scanBadge, PackName);
        _scanBadge.BorderThickness = new Thickness(1);
        Accent(_scanBadge, Border.BackgroundProperty, "BrushWarningSoft", true);
        Accent(_scanBadge, Border.BorderBrushProperty, "BrushWarning", true);
        Accent(_scanBadgeText, TextBlock.ForegroundProperty, "BrushText", true);

        // 2026-11-12 (У-2): после «Ко всем строкам» — окно «Изменено строк: N · Вернуть» (10 с): прежний способ и числа строк возвращаются.
        _undoText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        var undoButton = _makeButton(UndoLabel);
        undoButton.Focusable = true;
        undoButton.MinHeight = 40;
        Avalonia.Automation.AutomationProperties.SetName(undoButton, UndoHint);
        undoButton.Click += (_, _) => UndoLastApply();
        _undoFlyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { _undoText, undoButton } }, Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Transient }; // Transient — не уводит курсор из поля скана

        IsWide = true;
        Sync();
        TryHookKeys();
    }

    // ------------------------------------------------------------------ клавиатура (У-1)

    private bool _keysHooked;

    /// <summary>F4 / Shift+F4 — следующий / прежний способ, пока панель на экране (любой фокус внутри приёмки). Подписка — на окно, когда панель в него попала.</summary>
    private void TryHookKeys()
    {
        Host.AttachedToVisualTree += (_, _) =>
        {
            if (_keysHooked || TopLevel.GetTopLevel(Host) is not { } top)
                return;
            _keysHooked = true;
            top.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Handled || e.Key != Avalonia.Input.Key.F4 || (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Shift) != Avalonia.Input.KeyModifiers.None || !Host.IsEffectivelyVisible)
                    return;
                e.Handled = true;
                CycleMode(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? -1 : 1, focusScan: true);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        };
    }

    private static readonly ReceivingPackDefaultMode[] Order = { ReceivingPackDefaultMode.Pieces, ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale };

    /// <summary>Следующий (+1) или прежний (−1) способ по кругу «В штуках → Упаковками → Поштучная продажа».</summary>
    public void CycleMode(int step, bool focusScan)
    {
        var index = Array.IndexOf(Order, Mode);
        SetMode(Order[((index + step) % Order.Length + Order.Length) % Order.Length], focusScan);
    }

    private void ArrowKey(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is not (Avalonia.Input.Key.Left or Avalonia.Input.Key.Right) || e.KeyModifiers != Avalonia.Input.KeyModifiers.None)
            return;
        e.Handled = true;
        var rtl = Host.FlowDirection == FlowDirection.RightToLeft;
        CycleMode((e.Key == Avalonia.Input.Key.Right) != rtl ? 1 : -1, focusScan: false);
    }

    // ------------------------------------------------------------------ состояние

    /// <summary>Способ приёмки по умолчанию для новых строк.</summary>
    public ReceivingPackDefaultMode Mode { get; private set; }

    /// <summary>«Штук в упаковке по умолчанию»; 0 — не задано.</summary>
    public double DefaultSize { get; private set; }

    /// <summary>Вид с сегментами (шире).</summary>
    public Control Wide { get; }

    /// <summary>Вид с кнопкой «Приём: … ▾».</summary>
    public Control Compact { get; }

    /// <summary>Место на панели: сюда кладётся один из двух видов (окно вставляет <see cref="Host"/> в свою раскладку).</summary>
    public ContentControl Host { get; }

    /// <summary>Сейчас показан вид с сегментами.</summary>
    public bool IsWide { get; private set; }

    /// <summary>Ширина вида с сегментами (последняя измеренная; 0 — ещё не измеряли). Нужна окну для решения «поместится ли».</summary>
    public double WideWidth { get; private set; }

    /// <summary>Показать вид с сегментами (true) или кнопку (false).</summary>
    public void ShowWide(bool wide)
    {
        if (IsWide == wide && ReferenceEquals(Host.Content, wide ? Wide : Compact))
            return;
        IsWide = wide;
        Host.Content = wide ? Wide : Compact;
    }

    /// <summary>Окно измерило вид с сегментами (когда он на экране): запомнить ширину.</summary>
    public void NoteWideMeasured()
    {
        if (IsWide && Wide.DesiredSize.Width > 1)
            WideWidth = Wide.DesiredSize.Width;
    }

    /// <summary>Выбрать способ (сегмент, пункт окна или программно): обновить оба вида, вернуть курсор в поле скана.</summary>
    public void Choose(ReceivingPackDefaultMode mode) => SetMode(mode, focusScan: true);

    /// <summary>Выбор способа; <paramref name="focusScan"/> — вернуть курсор в поле скана (после мыши — да, после стрелок по панели и сброса — нет).</summary>
    private void SetMode(ReceivingPackDefaultMode mode, bool focusScan)
    {
        if (Mode == mode)
        {
            Sync();
            return;
        }
        Mode = mode;
        _modeDay = Clock().Date;
        _existingNoticeShown = false;
        Sync();
        if (focusScan)
            Dispatcher.UIThread.Post(_focusScan, DispatcherPriority.Background);
        else
            Dispatcher.UIThread.Post(KeepFocusOnPanel, DispatcherPriority.Background);
    }

    /// <summary>После выбора стрелкой фокус остаётся на выбранном сегменте (RadioButton при смене группы мог его потерять).</summary>
    private void KeepFocusOnPanel()
    {
        if (IsWide && _segments.TryGetValue(Mode, out var radio) && radio.IsEffectivelyVisible)
            radio.Focus();
        else if (!IsWide && _compact.IsEffectivelyVisible)
            _compact.Focus();
    }

    /// <summary>2026-11-12 (Л-9): приход проведён (поставлен в очередь) — способ возвращается «В штуках», следующий приход начинается как обычно.</summary>
    public void ResetAfterPosting()
    {
        _undo = null;
        if (Mode != ReceivingPackDefaultMode.Pieces)
            SetMode(ReceivingPackDefaultMode.Pieces, focusScan: false);
    }

    /// <summary>Способ действует только в тот день, когда его выбрали: окно, простоявшее через полночь, начинает «В штуках» (проверка перед каждой новой строкой).</summary>
    private void EnsureFresh()
    {
        if (Mode != ReceivingPackDefaultMode.Pieces && ReceivingPackDefaults.EffectiveMode(Mode, _modeDay, Clock()) == ReceivingPackDefaultMode.Pieces)
            SetMode(ReceivingPackDefaultMode.Pieces, focusScan: false);
    }

    /// <summary>Новой строке — способ по умолчанию (вызывает окно до вставки строки в список). Товар склада при «Поштучной продаже» остаётся в штуках — об этом один раз говорим (Л-10).</summary>
    public void ApplyToNew(ReceivingLineVm line)
    {
        EnsureFresh();
        if (ReceivingPackDefaults.ApplyToNew(line, Mode, DefaultSize))
            return;
        if (!_existingNoticeShown && ReceivingPackDefaults.IsSkippedExistingProduct(line, Mode))
        {
            _existingNoticeShown = true;
            _notify(ExistingProductNotice, false);
        }
    }

    /// <summary>Положить метку «упаковками» в поле скана (справа внутри поля). Вызов повторять можно.</summary>
    public void AttachScanBox(TextBox box)
    {
        if (!ReferenceEquals(box.InnerRightContent, _scanBadge))
            box.InnerRightContent = _scanBadge;
    }

    /// <summary>Метка у поля скана (для окна, которое кладёт её само).</summary>
    public Control ScanBadge => _scanBadge;

    /// <summary>Подсветка включена (способ не «В штуках»).</summary>
    public bool IsHighlighted => ReceivingPackDefaults.IsNonDefault(Mode);

    private void Accent(Control control, AvaloniaProperty property, string resource, bool on)
    {
        var key = (control, property);
        if (_accents.Remove(key, out var old))
            old.Dispose();
        if (on)
            _accents[key] = control.Bind(property, control.GetResourceObservable(resource));
        else
            control.ClearValue(property);
    }

    /// <summary>Ввод в поле «шт в упак.»: число сразу идёт в новые строки, файл пишется при выходе из поля.</summary>
    private void SizeTyped(TextBox box, bool persist)
    {
        if (_syncing)
            return;
        var text = (box.Text ?? "").Trim().Replace(',', '.');
        double size = 0;
        if (text.Length > 0 && !(double.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out size) && ReceivingPackDefaults.IsValidSizeFor(Mode, size)))
        {
            if (persist)
            {
                // 2026-11-12 (Л-24): для «Поштучной продажи» только целое от 2 — 0,5 и 1 не принимаем и говорим почему (раньше строка потом не проводилась).
                if (Mode == ReceivingPackDefaultMode.PackSale && double.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var typed) && ReceivingPackDefaults.IsValidSize(typed))
                    _notify(PackSaleSizeText, true);
                box.Text = DefaultSize > 0 ? DefaultSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : ""; // что-то не то набрали — показать принятое прежнее число
            }
            return;
        }
        SetDefaultSize(size, persist, box);
    }

    /// <summary>«Штук в упаковке по умолчанию» (0 — не задано): новым строкам подставится сразу; в файл — когда persist.</summary>
    public void SetDefaultSize(double size, bool persist = true, TextBox? typingIn = null)
    {
        DefaultSize = ReceivingPackDefaults.IsValidSizeFor(Mode, size) ? size : 0;
        if (persist)
            ReceivingViewPrefs.SetPackSize(DefaultSize);
        Sync(except: typingIn);
    }

    /// <summary>Привести оба вида к состоянию (<paramref name="except"/> — поле, в котором сейчас печатают, его текст не трогаем).</summary>
    private void Sync(TextBox? except = null)
    {
        _syncing = true;
        try
        {
            foreach (var (mode, radio) in _segments)
                if (radio.IsChecked != (mode == Mode))
                    radio.IsChecked = mode == Mode;
            foreach (var (mode, item) in _menuItems)
                if (item.Tag is TextBlock title)
                    title.Text = (mode == Mode ? "✓  " : "     ") + ModeName(mode);
            var sizeText = DefaultSize > 0 ? DefaultSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
            // Текст другого поля меняем, только если число в нём другое («2.» и «2» — одно число: набор точки не стирается; TextChanged приходит позже флага _syncing).
            foreach (var box in new[] { _sizeWide, _sizeMenu })
                if (!ReferenceEquals(box, except) && ParsedSize(box) != DefaultSize)
                    box.Text = sizeText;
            var packing = Mode != ReceivingPackDefaultMode.Pieces;
            _sizeWideHolder.Opacity = packing ? 1 : 0;
            _sizeWideHolder.IsHitTestVisible = packing;
            _sizeWide.Focusable = packing;
            _sizeMenuRow.IsVisible = packing;
            _compact.Content = LabelText + " " + ModeShort(Mode) + " ▾";
            // 2026-11-12 (Л-9): не «В штуках» — панель и кнопка тёплого цвета, у поля скана метка.
            var hot = IsHighlighted;
            Accent(_segBox, Border.BackgroundProperty, "BrushWarningSoft", hot);
            Accent(_segBox, Border.BorderBrushProperty, "BrushWarning", hot);
            Accent(_compact, TemplatedControl.BackgroundProperty, "BrushWarningSoft", hot);
            Accent(_compact, TemplatedControl.BorderBrushProperty, "BrushWarning", hot);
            _compact.FontWeight = hot ? FontWeight.Bold : FontWeight.Normal;
            _scanBadge.IsVisible = hot;
            _scanBadgeText.Text = ScanBadgeText(Mode);
            ToolTip.SetTip(_scanBadge, ScanBadgeHint(Mode));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Число в поле размера (0 — пусто или не число).</summary>
    private static double ParsedSize(TextBox box) =>
        double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var v) && ReceivingPackDefaults.IsValidSize(v) ? v : 0;

    private TextBox SizeBox()
    {
        var box = new TextBox
        {
            Width = 92, Height = 40, MinHeight = 0, FontSize = 14, Padding = new Thickness(8, 0), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = SizeWatermark, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(box, SizeHint);
        Avalonia.Automation.AutomationProperties.SetName(box, SizeLabel);
        box.TextChanged += (_, _) => SizeTyped(box, persist: false);
        box.LostFocus += (_, _) => SizeTyped(box, persist: true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter)
                return;
            e.Handled = true;
            SizeTyped(box, persist: true);
            _focusScan();
        };
        return box;
    }

    // ------------------------------------------------------------------ «Ко всем строкам»

    /// <summary>Применить способ ко всем строкам списка. Строки, где уже введены числа и способ меняется, — с вопросом.</summary>
    public async Task ApplyToAllAsync()
    {
        var lines = _lines();
        var mode = Mode;
        var (willChange, withValues) = ReceivingPackDefaults.Preview(lines, mode);
        if (willChange == 0)
        {
            _notify(NothingToApplyText(lines, mode), false);
            return;
        }
        if (withValues > 0 && !await _confirm(ConfirmText(willChange, withValues, mode), ApplyShortOk).ConfigureAwait(true))
        {
            _focusScan();
            return;
        }
        var snapshot = ReceivingPackDefaults.Capture(lines, mode); // 2026-11-12 (У-2): запомнить, что менялось, чтобы «Вернуть»
        var outcome = ReceivingPackDefaults.ApplyToAll(lines, mode, DefaultSize);
        _changed();
        _notify(OutcomeText(outcome, mode), outcome.Applied == 0);
        _focusScan();
        if (outcome.Applied > 0)
            OfferUndo(snapshot, outcome.Applied);
    }

    private DispatcherTimer? _undoTimer;

    private void OfferUndo(ReceivingPackDefaults.Snapshot snapshot, int applied)
    {
        _undo = snapshot;
        _undoText.Text = UndoTitle(applied);
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer?.Stop();
            _undo = null;
            _undoFlyout.Hide();
        };
        _undoTimer.Start();
        var anchor = IsWide ? Wide : Compact;
        if (anchor.IsEffectivelyVisible)
            _undoFlyout.ShowAt(anchor);
    }

    /// <summary>У-2: «Вернуть» после «Ко всем строкам» — способ и числа изменённых строк как были. true — было что возвращать.</summary>
    public bool UndoLastApply()
    {
        var snapshot = _undo;
        _undo = null;
        _undoTimer?.Stop();
        _undoFlyout.Hide();
        if (snapshot is null || snapshot.Count == 0)
            return false;
        var restored = ReceivingPackDefaults.Restore(snapshot);
        _changed();
        _notify(UndoDoneText(restored), false);
        _focusScan();
        return true;
    }

    /// <summary>Есть что вернуть (окно «Вернуть» ещё живо).</summary>
    public bool CanUndo => _undo is { Count: > 0 };

    // ------------------------------------------------------------------ надписи (5 языков)

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string LabelText => T("Приём:", "Кабыл алуу:", "Receive:", "Kabul:", "Qabul:");
    private static string PackName => T("Упаковка", "Таңгак", "Packaging", "Koli", "Qadoq");

    private static string ModeName(ReceivingPackDefaultMode m) => m switch
    {
        ReceivingPackDefaultMode.Pieces => T("В штуках", "Даана менен", "In pieces", "Adetle", "Donalab"),
        ReceivingPackDefaultMode.Packs => T("Упаковками", "Таңгак менен", "By packs", "Kolilerle", "Qadoq bilan"),
        _ => T("Поштучная продажа", "Даана менен сатуу", "Single-piece sale", "Adetli satış", "Donalab sotish"),
    };

    private static string ModeShort(ReceivingPackDefaultMode m) => m switch
    {
        ReceivingPackDefaultMode.Pieces => T("в штуках", "даана", "pieces", "adet", "dona"),
        ReceivingPackDefaultMode.Packs => T("упаковками", "таңгак", "packs", "koli", "qadoq"),
        _ => T("поштучно", "даана сатуу", "by piece", "adetli", "donalab"),
    };

    private static string ModeHint(ReceivingPackDefaultMode m) => m switch
    {
        ReceivingPackDefaultMode.Pieces => T("В штуках: количество и цена закупки — за штуку.",
            "Даана менен: саны жана сатып алуу баасы — даана үчүн.",
            "In pieces: quantity and purchase price are per piece.",
            "Adetle: miktar ve alış fiyatı — adet başına.",
            "Donalab: miqdor va xarid narxi — dona uchun."),
        ReceivingPackDefaultMode.Packs => T("Упаковками: вводите упаковки и «шт в упак.», цену — за упаковку. На склад идут штуки и цена за штуку.",
            "Таңгак менен: таңгакты жана «даана/таңгакты» киргизиңиз, баасы — таңгак үчүн. Кампага даана жана даанасынын баасы барат.",
            "By packs: enter packs and “pcs/pack”, the price is per pack. The warehouse gets pieces and the price per piece.",
            "Kolilerle: koli sayısını ve «adet/koli»yi girin, fiyat koli başına. Depoya adet ve adet fiyatı gider.",
            "Qadoq bilan: qadoq sonini va «dona/qadoq»ni kiriting, narx — qadoq uchun. Omborga dona va dona narxi boradi."),
        _ => T("Поштучная продажа из упаковки: остаток ведётся в упаковках, товар можно продать поштучно. Только новому товару; у товара склада — в самой строке («упак. ▾») с подтверждением «Было → станет».",
            "Таңгактан даана менен сатуу: калдык таңгактарда, товарды даана менен сатса болот. Жаңы товарга гана; кампанын товарында — саптын өзүндө («таңгак ▾»), «Болгон → болот» тастыктоосу менен.",
            "Single-piece sale from a pack: the stock is kept in packs, the product can be sold by the piece. New products only; for a warehouse product — in the line itself (“pack ▾”) with the “Was → will be” confirmation.",
            "Koliden adetli satış: stok kolilerle tutulur, ürün adet adet satılabilir. Yalnızca yeni ürün; depodaki üründe — satırın kendisinde («koli ▾»), «Önce → sonra» onayıyla.",
            "Qadoqdan donalab sotish: qoldiq qadoqlarda yuritiladi, mahsulotni donalab sotish mumkin. Faqat yangi mahsulot uchun; ombor mahsulotida — qatorning o'zida («qadoq ▾»), «Avval → bo'ladi» tasdig'i bilan."),
    };

    private static string MenuTitle => T("Как принимать новые строки (F4 — следующий способ)", "Жаңы саптарды кандай кабыл алуу (F4 — кийинки ыкма)", "How to receive new lines (F4 — next way)", "Yeni satırlar nasıl alınsın (F4 — sonraki yol)", "Yangi qatorlar qanday qabul qilinsin (F4 — keyingi usul)");

    private static string CompactHint => T("Упаковка: способ приёмки для новых строк. Нажмите — выбрать «В штуках», «Упаковками» или «Поштучная продажа».",
        "Таңгак: жаңы саптар үчүн кабыл алуу ыкмасы. Басыңыз — «Даана менен», «Таңгак менен» же «Даана менен сатуу» тандаңыз.",
        "Packaging: how new lines are received. Press to choose “In pieces”, “By packs” or “Single-piece sale”.",
        "Koli: yeni satırlar için kabul yolu. Basın — «Adetle», «Kolilerle» veya «Adetli satış» seçin.",
        "Qadoq: yangi qatorlar uchun qabul usuli. Bosing — «Donalab», «Qadoq bilan» yoki «Donalab sotish»ni tanlang.");

    private static string ScanBadgeText(ReceivingPackDefaultMode m) => m switch
    {
        ReceivingPackDefaultMode.Packs => T("УПАКОВКАМИ", "ТАҢГАК МЕНЕН", "BY PACKS", "KOLİLERLE", "QADOQ BILAN"),
        ReceivingPackDefaultMode.PackSale => T("ПОШТУЧНАЯ ПРОДАЖА", "ДААНА МЕНЕН САТУУ", "SINGLE-PIECE SALE", "ADETLİ SATIŞ", "DONALAB SOTISH"),
        _ => "",
    };

    private static string ScanBadgeHint(ReceivingPackDefaultMode m) => m == ReceivingPackDefaultMode.Pieces ? "" :
        T($"Способ приёма «{ModeName(m)}»: каждый скан нового товара — одна упаковка, а не штука. Вернуть «В штуках» — F4 или переключатель «Приём».",
            $"Кабыл алуу ыкмасы «{ModeName(m)}»: жаңы товарды ар бир сканерлөө — бир таңгак, даана эмес. «Даана менен» кайтаруу — F4 же «Кабыл алуу» которгучу.",
            $"Receive mode “{ModeName(m)}”: every scan of a new product is one pack, not one piece. Back to “In pieces” — F4 or the “Receive” switch.",
            $"Kabul yolu «{ModeName(m)}»: yeni ürünün her okutması bir koli, bir adet değil. «Adetle» için — F4 veya «Kabul» anahtarı.",
            $"Qabul usuli «{ModeName(m)}»: yangi mahsulotni har bir skanerlash — bitta qadoq, dona emas. «Donalab»ga qaytish — F4 yoki «Qabul» almashtirgichi.");

    private static string ExistingProductNotice => T("«Поштучная продажа» к товару склада не применяется — включите её в самой строке: «упак. ▾» → «Поштучная продажа» (с подтверждением «Было → станет»).",
        "«Даана менен сатуу» кампанын товарына колдонулбайт — аны саптын өзүндө иштетиңиз: «таңгак ▾» → «Даана менен сатуу» («Болгон → болот» тастыктоосу менен).",
        "“Single-piece sale” is not applied to a warehouse product — turn it on in the line itself: “pack ▾” → “Single-piece sale” (with the “Was → will be” confirmation).",
        "«Adetli satış» depodaki ürüne uygulanmaz — satırın kendisinde açın: «koli ▾» → «Adetli satış» («Önce → sonra» onayıyla).",
        "«Donalab sotish» ombor mahsulotiga qo'llanmaydi — uni qatorning o'zida yoqing: «qadoq ▾» → «Donalab sotish» («Avval → bo'ladi» tasdig'i bilan).");

    private static string PackSaleSizeText => T("Для «Поштучной продажи» штук в упаковке — целое число от 2.", "«Даана менен сатуу» үчүн таңгактагы даана — 2ден баштап бүтүн сан.",
        "For “Single-piece sale” pieces per pack must be a whole number, 2 or more.", "«Adetli satış» için koli başına adet 2 veya daha büyük bir tam sayı olmalı.", "«Donalab sotish» uchun qadoqdagi dona soni — 2 dan boshlab butun son.");

    private static string UndoLabel => T("Вернуть", "Кайтаруу", "Undo", "Geri al", "Qaytarish");

    private static string UndoHint => T("Вернуть способ и числа строк, изменённых кнопкой «Ко всем строкам»", "«Бардык саптарга» баскычы өзгөрткөн саптардын ыкмасын жана сандарын кайтаруу",
        "Bring back the way and numbers of the lines changed by “To all lines”", "«Tüm satırlara» düğmesinin değiştirdiği satırların yolunu ve sayılarını geri al", "«Barcha qatorlarga» tugmasi o'zgartirgan qatorlarning usuli va sonlarini qaytarish");

    private static string UndoTitle(int n) => T($"Изменено строк: {n}.", $"Өзгөргөн саптар: {n}.", $"Lines changed: {n}.", $"Değişen satır: {n}.", $"O'zgargan qatorlar: {n}.");

    private static string UndoDoneText(int n) => T($"Возвращено строк: {n}.", $"Кайтарылган саптар: {n}.", $"Lines restored: {n}.", $"Geri alınan satır: {n}.", $"Qaytarilgan qatorlar: {n}.");

    private static string ApplyText => T("Ко всем строкам", "Бардык саптарга", "To all lines", "Tüm satırlara", "Barcha qatorlarga");

    private static string ApplyShortOk => T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash");

    private static string ApplyHint => T("Применить выбранный способ ко всем строкам списка (новые строки получают его сами). Товар с упаковкой в карточке, весовой и с размерами остаются как есть; «поштучная продажа» — только новому товару.",
        "Тандалган ыкманы тизмедеги бардык саптарга колдонуу (жаңы саптар аны өзү алат). Картасында таңгагы бар, салмактуу жана өлчөмдүү товарлар өзгөрбөйт; «даана менен сатуу» — жаңы товарга гана.",
        "Apply the chosen way to all lines of the list (new lines get it by themselves). A product with a pack in its card, a weighed one and one with sizes stay as they are; “single-piece sale” — new products only.",
        "Seçilen yolu listedeki tüm satırlara uygula (yeni satırlar kendisi alır). Kartında koli olan, tartılı ve bedenli ürünler olduğu gibi kalır; «adetli satış» — yalnızca yeni ürün.",
        "Tanlangan usulni ro'yxatdagi barcha qatorlarga qo'llash (yangi qatorlar uni o'zi oladi). Kartasida qadoqi bor, vaznli va o'lchamli mahsulotlar o'zgarmaydi; «donalab sotish» — faqat yangi mahsulot uchun.");

    // Подсказка поля НЕ совпадает с подсказкой поля «шт в упак.» в строках таблицы (ReceivingByPacksUiTests и др. ищут строчное поле по ней).
    private static string SizeWatermark => T("шт/упак.", "дн/таңгак", "pcs/pk.", "ad/koli", "dn/qadoq");
    private static string SizeLabel => T("Штук в упаковке по умолчанию", "Таңгактагы даана (демейки)", "Pieces per pack by default", "Varsayılan koli başına adet", "Standart qadoqdagi dona");

    private static string SizeHint => T("Штук в упаковке по умолчанию: подставляется в новые строки (у товара, который уже принимали упаковками, — прежнее число). Запоминается.",
        "Таңгактагы даана (демейки): жаңы саптарга коюлат (мурда таңгак менен кабыл алынган товарда — мурунку сан). Эстелет.",
        "Pieces per pack by default: filled into new lines (a product already received in packs keeps its previous number). Remembered.",
        "Varsayılan koli başına adet: yeni satırlara konur (daha önce kolilerle alınan üründe — önceki sayı). Hatırlanır.",
        "Standart qadoqdagi dona: yangi qatorlarga qo'yiladi (avval qadoq bilan qabul qilingan mahsulotda — oldingi son). Eslab qolinadi.");

    private static string ConfirmText(int willChange, int withValues, ReceivingPackDefaultMode mode) =>
        T($"Способ «{ModeName(mode)}» будет применён к {willChange} {Tr.Plural(willChange, "строке", "строкам", "строкам")}, в {withValues} из них уже введены количества или упаковки — они пересчитаются. Продолжить?",
            $"«{ModeName(mode)}» ыкмасы {willChange} сапка колдонулат, алардын {withValues} сабында сан же таңгак киргизилген — алар кайра эсептелет. Улантасызбы?",
            $"“{ModeName(mode)}” will be applied to {willChange} {Tr.PluralEn(willChange, "line", "lines")}; {withValues} of them already have quantities or packs entered — they will be recalculated. Continue?",
            $"«{ModeName(mode)}» {willChange} satıra uygulanacak, bunların {withValues} tanesinde miktar veya koli girilmiş — yeniden hesaplanacak. Devam edilsin mi?",
            $"«{ModeName(mode)}» {willChange} qatorga qo'llanadi, ulardan {withValues} tasida son yoki qadoq kiritilgan — qayta hisoblanadi. Davom etasizmi?");

    private static string NothingToApplyText(IReadOnlyList<ReceivingLineVm> lines, ReceivingPackDefaultMode mode) =>
        lines.Count == 0
            ? T("В списке пока нет строк — способ «" + ModeName(mode) + "» получат новые строки.", "Тизмеде саптар жок — «" + ModeName(mode) + "» ыкмасын жаңы саптар алат.",
                "The list has no lines yet — new lines will get “" + ModeName(mode) + "”.", "Listede henüz satır yok — «" + ModeName(mode) + "» yolunu yeni satırlar alır.", "Ro'yxatda hali qator yo'q — «" + ModeName(mode) + "» usulini yangi qatorlar oladi.")
            : T("Ничего менять не нужно: у всех строк, которым это можно, уже «" + ModeName(mode) + "».", "Эч нерсени өзгөртүүнүн кереги жок: мүмкүн болгон бардык саптарда «" + ModeName(mode) + "» бар.",
                "Nothing to change: every line that can have it already has “" + ModeName(mode) + "”.", "Değiştirilecek bir şey yok: olabilen tüm satırlarda zaten «" + ModeName(mode) + "» var.", "O'zgartiradigan narsa yo'q: bo'ladigan barcha qatorlarda allaqachon «" + ModeName(mode) + "» bor.");

    private static string OutcomeText(ReceivingPackDefaults.Outcome o, ReceivingPackDefaultMode mode)
    {
        var done = T($"«{ModeName(mode)}»: изменено строк — {o.Applied}.", $"«{ModeName(mode)}»: өзгөргөн саптар — {o.Applied}.", $"“{ModeName(mode)}”: lines changed — {o.Applied}.",
            $"«{ModeName(mode)}»: değişen satır — {o.Applied}.", $"«{ModeName(mode)}»: o'zgargan qatorlar — {o.Applied}.");
        if (o.Skipped == 0)
            return done;
        var parts = new List<string>();
        if (o.SkippedCardPack > 0)
            parts.Add(T($"с упаковкой в карточке — {o.SkippedCardPack}", $"картасында таңгагы бар — {o.SkippedCardPack}", $"with a pack in the card — {o.SkippedCardPack}", $"kartında koli olan — {o.SkippedCardPack}", $"kartasida qadoqi bor — {o.SkippedCardPack}"));
        if (o.SkippedUnsupported > 0)
            parts.Add(T($"весовых или с размерами — {o.SkippedUnsupported}", $"салмактуу же өлчөмдүү — {o.SkippedUnsupported}", $"weighed or with sizes — {o.SkippedUnsupported}", $"tartılı veya bedenli — {o.SkippedUnsupported}", $"vaznli yoki o'lchamli — {o.SkippedUnsupported}"));
        if (o.SkippedExistingProduct > 0)
            parts.Add(T($"товаров склада (поштучную продажу включайте в самой строке) — {o.SkippedExistingProduct}", $"кампанын товарлары (даана менен сатууну саптын өзүндө иштетиңиз) — {o.SkippedExistingProduct}",
                $"warehouse products (turn single-piece sale on in the line itself) — {o.SkippedExistingProduct}", $"depo ürünleri (adetli satışı satırın kendisinde açın) — {o.SkippedExistingProduct}", $"ombor mahsulotlari (donalab sotishni qatorning o'zida yoqing) — {o.SkippedExistingProduct}"));
        return done + " " + T("Не тронуты: ", "Тийилген жок: ", "Left as they were: ", "Dokunulmadı: ", "Tegilmadi: ") + string.Join("; ", parts) + ".";
    }
}
