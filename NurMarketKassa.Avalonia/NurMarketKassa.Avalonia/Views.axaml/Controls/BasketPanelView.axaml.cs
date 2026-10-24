using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class BasketPanelView : UserControl
{
    public BasketPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookLines();
        // 2026-10-20, живая проверка: «Отложить ч…» и подсказка поля кода не помещались на 1296 точек — короткие
        // подписи здесь (полный текст «Отложить чек» остаётся в подсказке кнопки).
        ApplyShortLabels();
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnLanguageChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            ApplyShortLabels();
        };
        DetachedFromVisualTree += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
        // 2026-10-20 (живой аудит кассы, К10): «Ещё» — всплывающее окно; нажатая в нём кнопка закрывает его (до своей команды).
        MoreActionsContent.AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (DataContext is BasketPanelViewModel vm)
                vm.IsMoreActionsVisible = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyShortLabels);

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
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // Cashiers often click away (e.g. straight to "Оплатить") instead of pressing Enter
    // after typing a quantity — commit on blur too, not just on the Enter key binding.
    private void QuantityInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CartLineItemVm line } && line.SetQuantityCommand?.CanExecute(line) == true)
            line.SetQuantityCommand.Execute(line);
    }

    /// <summary>«Списание» в меню «Ещё» (2026-09-27). Логика — в <see cref="BasketExtraActions"/>
    /// (2026-09-28): то же меню есть у других раскладок кассы.</summary>
    private async void WriteOff_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.WriteOffAsync(this, DataContext as BasketPanelViewModel).ConfigureAwait(true);

    private void Rental_Click(object? sender, RoutedEventArgs e) =>
        BasketExtraActions.Rental(DataContext as BasketPanelViewModel);

    /// <summary>«Печать последнего чека» в меню «Ещё» (2026-09-27), см. <see cref="BasketExtraActions"/>.</summary>
    private async void PrintLastReceipt_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.PrintLastReceiptAsync(sender as Button, DataContext as BasketPanelViewModel).ConfigureAwait(true);

    /// <summary>2026-10-04, Android: камера как сканер — код уходит в чек как скан USB-сканера.</summary>
    private async void CameraScanButton_Click(object? sender, RoutedEventArgs e) =>
        await CameraScan.ScanIntoKassaAsync().ConfigureAwait(true);
}
