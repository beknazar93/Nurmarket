using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class BasketPanelView : UserControl
{
    public BasketPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            HookLines();
            HookViewModel();
        };
        // 2026-10-20, живая проверка: «Отложить ч…» и подсказка поля кода не помещались на 1296 точек — короткие
        // подписи здесь (полный текст «Отложить чек» остаётся в подсказке кнопки).
        ApplyShortLabels();
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnLanguageChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            ApplyShortLabels();
            UpdatePayKeyBadge();
        };
        DetachedFromVisualTree += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
        // 2026-10-20 (живой аудит кассы, К10): «Ещё» — всплывающее окно; нажатая в нём кнопка закрывает его (до своей команды).
        MoreActionsContent.AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (DataContext is BasketPanelViewModel vm)
                vm.IsMoreActionsVisible = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // 2026-11-06 (В7, Д4-03): признак «одна строка Скан или поиск» (файл single-input-row.on в %LOCALAPPDATA%\NurMarketKassa) — нижняя строка ввода чека скрыта,
        // код и название вводятся в поиск каталога (там Enter добавляет в чек, см. CatalogPanelView). Без файла — строка на месте, как раньше.
        if (KassaMainViewRules.SingleInputRowEnabled())
            CartEntryRow.IsVisible = false;
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        ApplyShortLabels();
        UpdatePayKeyBadge();
    });

    // ── 2026-11-06 (В7): связь вида с моделью чека — «Вернуть» после очистки (Д4-40), подсветка добавленной строки (Д4-06), значок клавиши на «Оплатить» (Д4-05) ──

    private BasketPanelViewModel? _vm;
    private Window? _hostWindow;
    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        LayoutUpdated -= OnLayoutUpdatedCheck;
        LayoutUpdated += OnLayoutUpdatedCheck;
        if (_hostWindow is not null)
            _hostWindow.Activated -= OnHostActivated;
        _hostWindow = e.Root as Window;
        if (_hostWindow is not null)
            _hostWindow.Activated += OnHostActivated;
        UpdatePayKeyBadge();
        UpdateUiReady();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        LayoutUpdated -= OnLayoutUpdatedCheck;
        if (_hostWindow is not null)
            _hostWindow.Activated -= OnHostActivated;
        _hostWindow = null;
        UpdateUiReady();
        base.OnDetachedFromVisualTree(e);
    }

    // IsEffectivelyVisible в Avalonia 11.2 — не свойство с событием, поэтому проверяем после каждой раскладки (одно сравнение bool; смена раскладки кассы всегда даёт проход раскладки).
    private void OnLayoutUpdatedCheck(object? sender, EventArgs e) => UpdateUiReady();

    // Кассир мог сменить клавишу оплаты в «Горячих» — после возврата в окно значок обновляется.
    private void OnHostActivated(object? sender, EventArgs e) => UpdatePayKeyBadge();

    private void HookViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnBasketPropertyChanged;
            // Старая модель больше не показывается этим видом: «Очистить чек» у неё снова спрашивает, подсветки нет.
            _vm.ClearUndoUiReady = false;
            _vm.AddFlashUiReady = false;
        }
        _vm = DataContext as BasketPanelViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnBasketPropertyChanged;
        UpdateUiReady();
        UpdateUndoCountdown();
    }

    /// <summary>Вид «готов» только пока он на экране (виден и в дереве окна): модель чека общая для всех раскладок, и в других раскладках плашки «Вернуть» и подсветки нет —
    /// там «Очистить чек» по-прежнему спрашивает «Очистить текущий чек?» (стереть чек без возврата одним нажатием нельзя).</summary>
    private void UpdateUiReady()
    {
        if (_vm is null)
            return;
        var ready = _attached && IsEffectivelyVisible;
        _vm.ClearUndoUiReady = ready;
        _vm.AddFlashUiReady = ready;
    }

    private void OnBasketPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BasketPanelViewModel.IsClearUndoVisible):
                Dispatcher.UIThread.Post(UpdateUndoCountdown);
                break;
            case nameof(BasketPanelViewModel.AddFlashVersion):
                Dispatcher.UIThread.Post(FlashAddedLine);
                break;
        }
    }

    // «Вернуть (5)» — число секунд убывает, пока плашка видна. Окно — BasketPanelViewModel.ClearUndoWindow (5 с).
    private DispatcherTimer? _undoTimer;
    private DateTime _undoShownUtc;

    private void UpdateUndoCountdown()
    {
        if (_vm is { IsClearUndoVisible: true })
        {
            _undoShownUtc = DateTime.UtcNow;
            RefreshUndoLabel();
            _undoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _undoTimer.Tick -= OnUndoTick;
            _undoTimer.Tick += OnUndoTick;
            _undoTimer.Start();
        }
        else
        {
            _undoTimer?.Stop();
        }
    }

    private void OnUndoTick(object? sender, EventArgs e) => RefreshUndoLabel();

    private void RefreshUndoLabel()
    {
        if (_vm is null)
            return;
        var left = KassaMainViewRules.UndoSecondsLeft(_undoShownUtc, BasketPanelViewModel.ClearUndoWindow, DateTime.UtcNow);
        ClearUndoLabel.Text = KassaMainViewRules.UndoButtonLabel(_vm.UndoClearText, left);
    }

    /// <summary>Подсветка строки, которую только что добавили или у которой выросло количество (модель говорит какая: LastAddedItemId). Повторный скан — ещё и «× N» цветом.</summary>
    private void FlashAddedLine()
    {
        var vm = _vm;
        if (vm is null || string.IsNullOrEmpty(vm.LastAddedItemId))
            return;
        var id = vm.LastAddedItemId;
        var repeat = vm.LastAddedWasRepeat;
        var index = -1;
        for (var i = 0; i < vm.Lines.Count; i++)
        {
            if (string.Equals(vm.Lines[i].ItemId, id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return;
        if (!TryFlashRow(index, repeat))
        {
            // Строка вне экрана (повторный скан товара из середины длинного чека) — показываем её и подсвечиваем, когда она появится.
            CartLinesList.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() => TryFlashRow(index, repeat), DispatcherPriority.Background);
        }
    }

    private bool TryFlashRow(int index, bool repeat)
    {
        if (CartLinesList.ContainerFromIndex(index) is not { } container
            || container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("cart-line")) is not { } row)
            return false;
        FlashRow(row, repeat);
        return true;
    }

    /// <summary>Подсветка на 0,7 с; меняется только цвет (класс fresh / fresh-repeat), размер строки не трогаем. Токен защищает от раннего снятия, если подсветили второй раз подряд.</summary>
    private static void FlashRow(Border row, bool repeat)
    {
        var token = new object();
        row.Tag = token;
        row.Classes.Add("fresh");
        if (repeat)
            row.Classes.Add("fresh-repeat");
        DispatcherTimer.RunOnce(() =>
        {
            if (!ReferenceEquals(row.Tag, token))
                return;
            row.Classes.Remove("fresh");
            row.Classes.Remove("fresh-repeat");
        }, TimeSpan.FromMilliseconds(700));
    }

    /// <summary>Значок клавиши на «Оплатить»: назначенная в «Горячих» клавиша действия «Оплата» (по умолчанию Enter). Пусто (назначение снято) и на Android — значка нет.</summary>
    private void UpdatePayKeyBadge()
    {
        var text = "";
        try
        {
            var gesture = new PosHotkeyService().GetGesture(PosHotkeyAction.Pay);
            if (!string.IsNullOrWhiteSpace(gesture))
                text = PosHotkeyService.Display(gesture);
        }
        catch
        {
            // подсказка клавиши не должна мешать продаже — без значка
        }
        PayKeyBadgeText.Text = text;
        PayKeyBadge.IsVisible = text.Length > 0 && !OperatingSystem.IsAndroid();
    }

    private void ApplyShortLabels()
    {
        DeferLabel.Text = Tr.T("Отложить", "Калтыруу", "Hold", "Beklet", "Kechiktirish");
        // 2026-10-23 (аудит, круг 7): на 1024 px «Код или название то…» обрезалось — подсказка ещё короче, полная — во всплывающей подсказке.
        BarcodeInputBox.Watermark = Tr.T("Код или название", "Код же аты", "Code or name", "Kod veya ad", "Kod yoki nom");
        ToolTip.SetTip(BarcodeInputBox, Tr.T("Код или название товара", "Товардын коду же аты", "Product code or name", "Ürün kodu veya adı", "Mahsulot kodi yoki nomi"));
    }

    // 2026-10-20, живая проверка (большой чек): строки чека больше не пересоздаются на каждый скан (вставляется
    // только новая строка сверху), и список, прокрутка которого «держится» за видимые строки, уезжал от новой
    // строки — сверху её не было видно. Новая строка сверху — список показывает её, как раньше.
    private System.Collections.ObjectModel.ObservableCollection<CartLineItemVm>? _lines;

    private void HookLines()
    {
        if (_lines is not null)
            _lines.CollectionChanged -= OnLinesChanged;
        _lines = (DataContext as BasketPanelViewModel)?.Lines;
        if (_lines is not null)
            _lines.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewStartingIndex != 0)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lines is { Count: > 0 })
                CartLinesList.ScrollIntoView(0);
            // 2026-11-05 (аудит №4, Д4-06): добавленную строку подсвечиваем на 0,7 с — при скане подряд видно, что товар пробился и какой.
            if (CartLinesList.ContainerFromIndex(0) is { } container
                && container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("cart-line")) is { } row)
            {
                FlashRow(row, repeat: false); // 2026-11-06 (В7): общая подсветка (токен), вместе с подсветкой по LastAddedItemId
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // Cashiers often click away (e.g. straight to "Оплатить") instead of pressing Enter
    // after typing a quantity — commit on blur too, not just on the Enter key binding.
    private void QuantityInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CartLineItemVm line } && line.SetQuantityCommand?.CanExecute(line) == true)
            line.SetQuantityCommand.Execute(line);
    }

    /// <summary>2026-11-05 (аудит №4, Д4-04): удаление строки — двойное нажатие. Первое нажатие делает кнопку красной «Удалить?»
    /// на 3 секунды, второе — удаляет (прежнее поведение: коды доступа, причина, журнал — в RemoveCommand). Промах по «+» больше не убирает товар.</summary>
    // 2026-11-05 (ревью волн 0–3, п. 18): «взвод» держим по строке чека (ссылка + время), а не по классу кнопки: при прокрутке кнопку переиспользует другая строка, и одно нажатие
    // удалило бы чужой товар. По таймеру/при нажатии на другой строке возвращаем прежнюю надпись.
    private CartLineItemVm? _armedLine;
    private DateTime _armedUtc;
    private Button? _armedButton;
    private TextBlock? _armedLabel;
    private string? _armedOriginalText;

    private void DisarmDelete()
    {
        if (_armedButton is not null)
        {
            _armedButton.Classes.Remove("armed");
            // 2026-11-06 (В7, ДС-2): имя вернулось с «Подтвердить удаление: …» на «Удалить из чека: …» (SetCurrentValue — привязка имени сохраняется).
            if (_armedLine is not null)
                _armedButton.SetCurrentValue(Avalonia.Automation.AutomationProperties.NameProperty, _armedLine.RemoveName);
        }
        if (_armedLabel is not null && _armedOriginalText is not null)
            _armedLabel.Text = _armedOriginalText;
        _armedLine = null;
        _armedButton = null;
        _armedLabel = null;
        _armedOriginalText = null;
    }

    private void CartDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CartLineItemVm line } btn)
            return;
        if (!(ReferenceEquals(_armedLine, line) && DateTime.UtcNow - _armedUtc < TimeSpan.FromSeconds(3)))
        {
            DisarmDelete();
            if (btn.Content is StackPanel { Children.Count: > 1 } panel && panel.Children[1] is TextBlock ask)
            {
                _armedLabel = ask;
                _armedOriginalText = ask.Text;
                ask.Text = Tr.T("Удалить?", "Өчүрөбү?", "Delete?", "Silinsin mi?", "O'chirilsinmi?");
            }
            btn.Classes.Add("armed");
            btn.SetCurrentValue(Avalonia.Automation.AutomationProperties.NameProperty, line.RemoveConfirmName); // 2026-11-06 (В7, ДС-2): у «Удалить?» есть имя для диктора и UIA
            _armedLine = line;
            _armedUtc = DateTime.UtcNow;
            _armedButton = btn;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(_armedLine, line))
                    DisarmDelete();
            }, TimeSpan.FromSeconds(3));
            return;
        }
        DisarmDelete();
        if (line.RemoveCommand?.CanExecute(line) == true)
            line.RemoveCommand.Execute(line);
    }

    /// <summary>«Списание» в меню «Ещё» (2026-09-27). Логика — в <see cref="BasketExtraActions"/>
    /// (2026-09-28): то же меню есть у других раскладок кассы.</summary>
    private async void WriteOff_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.WriteOffAsync(this, DataContext as BasketPanelViewModel).ConfigureAwait(true);

    private void Rental_Click(object? sender, RoutedEventArgs e) =>
        BasketExtraActions.Rental(DataContext as BasketPanelViewModel);

    /// <summary>2026-11-12 (КТ2): «Списки к школе» (сфера «Канцтовары»).</summary>
    private void SchoolLists_Click(object? sender, RoutedEventArgs e) =>
        _ = NurMarketKassa.AvaloniaHost.Views.Dialogs.SchoolListLauncher.OpenAsync(this, DataContext as BasketPanelViewModel);

    /// <summary>2026-11-12 (КТ3): «Счёт из этого чека» (сфера «Канцтовары»).</summary>
    private void InvoiceFromReceipt_Click(object? sender, RoutedEventArgs e) =>
        NurMarketKassa.AvaloniaHost.Views.InvoiceFromReceipt.Open(this, DataContext as BasketPanelViewModel);

    /// <summary>«Печать последнего чека» в меню «Ещё» (2026-09-27), см. <see cref="BasketExtraActions"/>.</summary>
    private async void PrintLastReceipt_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.PrintLastReceiptAsync(sender as Button, DataContext as BasketPanelViewModel).ConfigureAwait(true);

    /// <summary>2026-10-04, Android: камера как сканер — код уходит в чек как скан USB-сканера.</summary>
    private async void CameraScanButton_Click(object? sender, RoutedEventArgs e) =>
        await CameraScan.ScanIntoKassaAsync().ConfigureAwait(true);
}
