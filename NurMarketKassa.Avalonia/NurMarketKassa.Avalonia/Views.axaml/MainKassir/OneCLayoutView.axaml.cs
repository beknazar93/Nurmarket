using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>Code-behind раскладки «1С» (2026-09-07) — три вещи, которые нельзя выразить привязками:
/// 1) активная строка чека следует за сканером: после добавления позиции выбирается самая новая
///    (Lines.Insert(0, …) → индекс 0), как в 1С, где карточка всегда показывает только что
///    пробитый товар; 2) поле сканера получает фокус при показе раскладки; 3) если фокус ушёл на
///    кнопку (клик по пилюле/вкладке), первая цифра со сканера возвращает фокус в поле и не
///    теряется — иначе сканер «стрелял в пустоту», пока кассир не кликнет в поле.</summary>
public partial class OneCLayoutView : UserControl
{
    private ObservableCollection<CartLineItemVm>? _lines;

    public OneCLayoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookLines();
        AttachedToVisualTree += (_, _) => FocusScanner();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            FocusScanner();
    }

    private void HookLines()
    {
        if (_lines is not null)
            _lines.CollectionChanged -= OnLinesChanged;

        _lines = (DataContext as MainWindowViewModel)?.Basket.Lines;
        if (_lines is null)
            return;

        _lines.CollectionChanged += OnLinesChanged;
        SelectNewest();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add || ReceiptList.SelectedItem is null)
            SelectNewest();
    }

    private void SelectNewest()
    {
        if (_lines is { Count: > 0 })
            ReceiptList.SelectedIndex = 0;
    }

    private void FocusScanner() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                ScannerBox.Focus();
        }, DispatcherPriority.Background);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ScannerBox.IsFocused)
            return;

        var digit = KeyToDigit(e.Key);
        if (digit is null)
            return;

        // Другие текстовые поля (например, количество в диалоге) не трогаем.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        ScannerBox.Focus();
        ScannerBox.Text = (ScannerBox.Text ?? string.Empty) + digit;
        ScannerBox.CaretIndex = ScannerBox.Text.Length;
        e.Handled = true;
    }

    private static char? KeyToDigit(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        _ => null,
    };
}
