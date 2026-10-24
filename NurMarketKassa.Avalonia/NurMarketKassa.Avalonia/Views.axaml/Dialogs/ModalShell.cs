using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-16, редизайн (этап 1): общая оболочка модального окна по правилам макетов.
///
/// Шапка (заголовок и подзаголовок слева, ✕ справа) и низ (подсказка или итог слева, кнопки справа)
/// не прокручиваются; между ними — содержимое окна, оно прокручивается, если не влезает. Высота
/// карточки — не больше 85 % рабочей области экрана кассы, поэтому на 1024×768 кнопки всегда видны.
/// Ширину задаёт само окно: карточка 480 / 640 / 880 + поля 2×16 под тень = Width 512 / 672 / 912.
///
/// Кнопки кладёт само окно в <see cref="Footer"/> (классы modal-cancel и modal-primary, у опасного
/// действия ещё danger — стили в PosDialogStyles.axaml), поэтому их имена и обработчики остаются в
/// окне. Esc — кнопка с IsCancel, Enter — кнопка с IsDefault (как и раньше). ✕ нажимает ту же кнопку
/// с IsCancel, что и Esc: у каждого окна своя логика отмены, оболочка её не дублирует.</summary>
public class ModalShell : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ModalShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<ModalShell, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<ModalShell, object?>(nameof(Footer));

    public static readonly StyledProperty<object?> HintProperty =
        AvaloniaProperty.Register<ModalShell, object?>(nameof(Hint));

    public static readonly StyledProperty<bool> ShowCloseProperty =
        AvaloniaProperty.Register<ModalShell, bool>(nameof(ShowClose), true);

    /// <summary>Доля рабочей области экрана по высоте (правило макетов — 85 %).</summary>
    public static readonly StyledProperty<double> ScreenHeightShareProperty =
        AvaloniaProperty.Register<ModalShell, double>(nameof(ScreenHeightShare), 0.85);

    /// <summary>2026-10-17, этап 3: прокрутка содержимого. Auto (по умолчанию) — содержимое прокручивается
    /// между шапкой и низом. Disabled — у окна свой список на всю оставшуюся высоту (выбор клиента,
    /// отложенные чеки): он получает конечную высоту и прокручивается сам, поле поиска над ним на месте.</summary>
    public static readonly StyledProperty<ScrollBarVisibility> ContentScrollProperty =
        AvaloniaProperty.Register<ModalShell, ScrollBarVisibility>(nameof(ContentScroll), ScrollBarVisibility.Auto);

    /// <summary>2026-10-22 (модалки, пакет 0): что получает фокус при открытии окна — имя элемента (x:Name в XAML,
    /// Name у элемента, собранного в коде) или сам элемент. Поле ввода — ещё и с выделением значения (SelectAll),
    /// чтобы кассир сразу печатал новое. Не задано (по умолчанию) — оболочка фокус не трогает, как раньше.</summary>
    public static readonly StyledProperty<object?> InitialFocusProperty =
        AvaloniaProperty.Register<ModalShell, object?>(nameof(InitialFocus));

    /// <summary>2026-10-22 (модалки, пакет 0): идёт операция (оплата, запрос, печать). Пока true: кнопки подвала и ✕
    /// выключены, Enter и Esc окна ничего не нажимают (повторный Enter не проводит операцию второй раз), окно не
    /// закрывается Alt+F4/системой. Закрытие из кода (Close(...) после успешной операции) — работает.</summary>
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<ModalShell, bool>(nameof(IsBusy));

    public object? InitialFocus
    {
        get => GetValue(InitialFocusProperty);
        set => SetValue(InitialFocusProperty, value);
    }

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>2026-10-22 (модалки, пакет 0): занять окно на время операции. false — уже занято (второй Enter/клик
    /// во время запроса) — обработчик должен просто выйти. Пара — <see cref="EndBusy"/> в finally.</summary>
    public bool TryBeginBusy()
    {
        if (IsBusy)
            return false;
        IsBusy = true;
        return true;
    }

    public void EndBusy() => IsBusy = false;

    /// <summary>2026-10-22 (модалки, пакет 0): выполнить операцию, заняв окно (<see cref="IsBusy"/>); повторный вызов,
    /// пока первая не закончилась, ничего не делает и возвращает false.</summary>
    public async Task<bool> RunBusyAsync(Func<Task> action)
    {
        if (!TryBeginBusy())
            return false;
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        finally
        {
            EndBusy();
        }
    }

    public ScrollBarVisibility ContentScroll
    {
        get => GetValue(ContentScrollProperty);
        set => SetValue(ContentScrollProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Кнопки справа внизу (обычно StackPanel: «Отмена», затем главная).</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>Подсказка или итог слева на строке кнопок («Enter — открыть», «Расхождение: …»).</summary>
    public object? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool ShowClose
    {
        get => GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    public double ScreenHeightShare
    {
        get => GetValue(ScreenHeightShareProperty);
        set => SetValue(ScreenHeightShareProperty, value);
    }

    private Button? _closeButton;
    private Window? _window;
    private bool _footerWasEnabled = true;
    private bool _closeWasEnabled = true;
    private Grid? _footerGrid;
    private Control? _hintPresenter;
    private Control? _footerPresenter;
    private bool _hintStacked;

    /// <summary>Уже этого подсказка слева от кнопок не ставится — уходит строкой над ними.</summary>
    private const double MinHintWidth = 140;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_closeButton is not null)
            _closeButton.Click -= CloseButton_Click;
        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");
        if (_closeButton is not null)
        {
            _closeButton.Click += CloseButton_Click;
            // 2026-10-24 (аудит круга 8, М-33, П-10): в доступности ✕ назывался «Avalonia.Controls.PathIcon» — даём имя «Закрыть».
            Avalonia.Automation.AutomationProperties.SetName(_closeButton, NurMarketKassa.Services.Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
            // 2026-10-22 (модалки, пакет 0): шаблон применили посреди операции — ✕ сразу выключен.
            _closeWasEnabled = _closeButton.IsEnabled;
            if (IsBusy)
                _closeButton.IsEnabled = false;
        }

        if (_footerGrid is not null)
            _footerGrid.SizeChanged -= Footer_SizeChanged;
        if (_footerPresenter is not null)
            _footerPresenter.SizeChanged -= Footer_SizeChanged;
        _footerGrid = e.NameScope.Find<Grid>("PART_FooterGrid");
        _hintPresenter = e.NameScope.Find<Control>("PART_Hint");
        _footerPresenter = e.NameScope.Find<Control>("PART_Footer");
        _hintStacked = false;
        if (_footerGrid is not null)
            _footerGrid.SizeChanged += Footer_SizeChanged;
        if (_footerPresenter is not null)
            _footerPresenter.SizeChanged += Footer_SizeChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FooterProperty || change.Property == HintProperty)
        {
            // Кнопки и подсказка — логические дети оболочки (как Header у HeaderedContentControl). Иначе они
            // попадают в логическое дерево окна только при применении шаблона, и {DynamicResource} у кнопки
            // срабатывает ПОСЛЕ кода конструктора окна — затирал бы надпись, заданную в коде
            // (PosConfirmDialog «Удалить», PosAlertDialog «Закрыть»). Проверено на снимках 16.10.
            if (change.OldValue is ILogical oldChild)
                LogicalChildren.Remove(oldChild);
            if (change.NewValue is ILogical newChild && newChild.LogicalParent is null)
                LogicalChildren.Add(newChild);
        }
        if (change.Property == HintProperty)
            UpdateFooterLayout();
        if (change.Property == IsBusyProperty)
            ApplyBusy(change.GetNewValue<bool>());
        else if (change.Property == FooterProperty && IsBusy)
        {
            // 2026-10-22 (модалки, пакет 0): окно поменяло кнопки посреди операции — новые тоже выключены,
            // старым возвращаем прежнее состояние.
            if (change.OldValue is InputElement oldFooter)
                oldFooter.IsEnabled = _footerWasEnabled;
            if (change.NewValue is InputElement newFooter)
            {
                _footerWasEnabled = newFooter.IsEnabled;
                newFooter.IsEnabled = false;
            }
        }
    }

    /// <summary>2026-10-22 (модалки, пакет 0): выключает/включает подвал и ✕. Прежнее состояние запоминается: если окно
    /// само выключило подвал, после операции он останется выключенным. Кнопки внутри подвала не трогаем — их
    /// IsEnabled остаётся за окном.</summary>
    private void ApplyBusy(bool busy)
    {
        if (Footer is InputElement footer)
        {
            if (busy)
            {
                _footerWasEnabled = footer.IsEnabled;
                footer.IsEnabled = false;
            }
            else
                footer.IsEnabled = _footerWasEnabled;
        }
        if (_closeButton is not null)
        {
            if (busy)
            {
                _closeWasEnabled = _closeButton.IsEnabled;
                _closeButton.IsEnabled = false;
            }
            else
                _closeButton.IsEnabled = _closeWasEnabled;
        }
    }

    private void Footer_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateFooterLayout();

    /// <summary>Подсказка на строке кнопок, пока ей хватает места (≥ <see cref="MinHintWidth"/>);
    /// иначе — отдельной строкой над кнопками во всю ширину. Ширина кнопок от места подсказки не
    /// зависит, поэтому переключение не «качается».</summary>
    private void UpdateFooterLayout()
    {
        if (_footerGrid is null || _hintPresenter is null || _footerPresenter is null)
            return;
        var available = _footerGrid.Bounds.Width;
        if (available <= 0)
            return;
        // 2026-10-17, этап 3: слева может стоять и кнопка («Сбросить», «Назад») — ей хватает её MinWidth,
        // переносить её строкой выше не нужно, пока она помещается рядом.
        var minHint = Hint is Control { MinWidth: > 0 } hintControl ? Math.Min(MinHintWidth, hintControl.MinWidth) : MinHintWidth;
        var stacked = Hint is not null && available - _footerPresenter.DesiredSize.Width - 12 < minHint;
        if (stacked == _hintStacked)
            return;
        _hintStacked = stacked;
        Grid.SetColumnSpan(_hintPresenter, stacked ? 2 : 1);
        _hintPresenter.Margin = stacked ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 12, 0);
        Grid.SetRow(_footerPresenter, stacked ? 1 : 0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FitToScreenHeight();
        AttachToWindow(TopLevel.GetTopLevel(this) as Window);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AttachToWindow(null);
    }

    /// <summary>2026-10-22 (модалки, пакет 0): подписка на окно — Enter/Esc и закрытие во время операции (IsBusy),
    /// фокус при открытии (InitialFocus).</summary>
    private void AttachToWindow(Window? window)
    {
        if (ReferenceEquals(window, _window))
            return;
        if (_window is not null)
        {
            _window.RemoveHandler(KeyDownEvent, Window_PreviewKeyDown);
            _window.Closing -= Window_Closing;
            _window.Opened -= Window_Opened;
        }
        _window = window;
        if (window is null)
            return;
        // Туннель: раньше, чем окно передаст Enter/Esc кнопке с IsDefault/IsCancel.
        window.AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        window.Closing += Window_Closing;
        if (window.IsVisible)
            Dispatcher.UIThread.Post(OnWindowOpened, DispatcherPriority.Loaded); // оболочку вставили в уже открытое окно
        else
            window.Opened += Window_Opened;
    }

    private void Window_Opened(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Opened -= Window_Opened;
        // После обработчиков Opened самого окна и после раскладки — элемент уже виден и может взять фокус.
        Dispatcher.UIThread.Post(OnWindowOpened, DispatcherPriority.Loaded);
    }

    private void OnWindowOpened()
    {
        try
        {
            ApplyInitialFocus();
            WarnIfManyPrimary();
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"ModalShell: открытие окна: {ex.Message}", "WARNING");
        }
    }

    private void ApplyInitialFocus()
    {
        var target = InitialFocus switch
        {
            Control control => control,
            string name when !string.IsNullOrWhiteSpace(name) => FindByName(name),
            _ => null,
        };
        if (target is null)
        {
            if (InitialFocus is string missing && !string.IsNullOrWhiteSpace(missing))
                NurMarketKassa.Services.PosLogger.Log($"ModalShell: InitialFocus «{missing}» не найден в окне {_window?.GetType().Name}", "WARNING");
            return;
        }
        if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled)
            return;
        target.Focus(NavigationMethod.Tab);
        if (target is TextBox box)
            box.SelectAll();
    }

    /// <summary>Сначала имя в области имён окна (XAML), затем — среди логических потомков (окна, собранные в коде).</summary>
    private Control? FindByName(string name)
    {
        if (this.FindNameScope()?.Find(name) is Control scoped)
            return scoped;
        foreach (var item in this.GetLogicalDescendants())
            if (item is Control { Name: { } n } control && string.Equals(n, name, StringComparison.Ordinal))
                return control;
        return null;
    }

    /// <summary>Правило стандарта: одна главная (жёлтая) кнопка на окно. Только запись в журнал — для отладки.</summary>
    private void WarnIfManyPrimary()
    {
        if (Footer is not ILogical footer)
            return;
        var count = Footer is Button direct && direct.Classes.Contains("modal-primary") ? 1 : 0;
        foreach (var item in footer.GetLogicalDescendants())
            if (item is Button { IsVisible: true } button && button.Classes.Contains("modal-primary"))
                count++;
        if (count > 1)
            NurMarketKassa.Services.PosLogger.Log($"ModalShell: в окне {_window?.GetType().Name} «{Title}» {count} главных кнопок (modal-primary) — по стандарту одна", "DEBUG");
    }

    /// <summary>Во время операции Enter и Esc не нажимают кнопки окна (IsDefault/IsCancel). Enter в многострочном поле —
    /// перенос строки, его не трогаем.</summary>
    private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsBusy || e.Handled)
            return;
        if (e.Key == Key.Escape)
            e.Handled = true;
        else if (e.Key == Key.Enter
                 && !(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: true }))
            e.Handled = true;
    }

    /// <summary>Во время операции пользователь окно не закрывает (Alt+F4, панель задач); закрытие из кода, вместе
    /// с окном-владельцем и при выходе из программы — как раньше.</summary>
    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (IsBusy && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
            e.Cancel = true;
    }

    /// <summary>Ограничивает высоту карточки долей рабочей области экрана кассы. На Android размер
    /// окна задаёт WindowLayerHost (см. DialogScreenFit.FitToKassaScreen) — там не трогаем.</summary>
    private void FitToScreenHeight()
    {
        if (OperatingSystem.IsAndroid() || TopLevel.GetTopLevel(this) is not Window window)
            return;
        try
        {
            var screen = DialogScreenFit.KassaScreen(window);
            if (screen is null)
                return;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var workingHeight = screen.WorkingArea.Height / scaling;
            if (workingHeight <= 0)
                return;
            var share = Math.Clamp(ScreenHeightShare, 0.5, 1.0);
            // 2026-10-22 (модалки, пакет 0): и не выше того, что остаётся в рабочей области за вычетом отступа окна (16),
            // рамки ОС (если окно вдруг с ней) и полей под тень — при доле 100 % подвал тоже не уходит под панель задач.
            var room = workingHeight - 16 - DialogScreenFit.ChromeSize(window).Height - Margin.Top - Margin.Bottom;
            var cap = Math.Floor(Math.Min(workingHeight * share, room));
            // Не меньше 300: на совсем маленьком экране шапка и низ всё равно должны влезть.
            MaxHeight = Math.Max(300, cap);
            // 2026-10-24 (аудит круга 8, С-7, П-10): и по ширине — карточка не шире рабочей области за вычетом отступа окна (32),
            // рамки ОС и полей под тень. Табель, Прокат, Логи, Некорректные чеки (1112 px) на экране 1024 уходили за край;
            // широкое содержимое (таблицы) получает горизонтальную прокрутку своего DataGrid/ScrollViewer.
            var workingWidth = screen.WorkingArea.Width / scaling;
            if (workingWidth > 0)
                MaxWidth = Math.Max(320, Math.Floor(workingWidth - 32 - DialogScreenFit.ChromeSize(window).Width - Margin.Left - Margin.Right));
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"ModalShell: высота окна под экран: {ex.Message}", "WARNING");
        }
    }

    /// <summary>✕ — ровно как Esc: нажимаем кнопку отмены окна (IsCancel) так же, как её нажимает Esc
    /// (Click и Command кнопки). 2026-10-17, ревью этапа 1: раньше выключенная кнопка отмены (идёт оплата,
    /// печать, запрос) пропускалась и ✕ закрывал окно через Close(false) посреди операции, а Close(false) у
    /// окна, показанного ShowDialog&lt;T&gt; с T не bool, падал InvalidCastException. Теперь: кнопка отмены
    /// есть, но выключена или скрыта — ничего не делаем (Esc тоже ничего не делает); кнопки нет совсем —
    /// Close() без результата (ShowDialog&lt;T&gt; получает default(T)).</summary>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsBusy)
            return; // 2026-10-22 (модалки, пакет 0): во время операции ✕ ничего не делает
        if (FindCancelButton() is { } cancel)
        {
            if (cancel.IsEffectivelyVisible && cancel.IsEffectivelyEnabled)
                PressButton(cancel);
            return;
        }
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    /// <summary>Нажатие кнопки так же, как Button.OnClick (его зовёт и Esc у IsCancel): событие Click, затем
    /// Command, если обработчик Click его не перехватил.</summary>
    internal static void PressButton(Button button)
    {
        if (!button.IsEffectivelyEnabled)
            return;
        var click = new RoutedEventArgs(Button.ClickEvent);
        button.RaiseEvent(click);
        var command = button.Command;
        var parameter = button.CommandParameter;
        if (!click.Handled && command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }

    /// <summary>Кнопка отмены в Footer: сначала видимая, иначе любая (скрытая — значит, отменять сейчас нельзя).</summary>
    private Button? FindCancelButton()
    {
        if (Footer is Button { IsCancel: true } direct)
            return direct;
        Button? hidden = null;
        if (Footer is ILogical footer)
            foreach (var item in footer.GetLogicalDescendants())
                if (item is Button { IsCancel: true } button)
                {
                    // 2026-10-17, ревью этапа 3: видимость с учётом родителей (кнопка в скрытой строке — скрыта).
                    if (button.IsEffectivelyVisible)
                        return button;
                    hidden ??= button;
                }
        return hidden;
    }
}
