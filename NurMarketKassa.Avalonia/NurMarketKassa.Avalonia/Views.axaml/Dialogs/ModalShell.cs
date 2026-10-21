using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

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
            _closeButton.Click += CloseButton_Click;

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
            // Не меньше 300: на совсем маленьком экране шапка и низ всё равно должны влезть.
            MaxHeight = Math.Max(300, Math.Floor(workingHeight * share));
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
