using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Отдельное модальное окно выбора клиента (2026-09-05, по просьбе пользователя: список
/// клиентов в диалоге оплаты "не помещается") — поиск/список/форма нового клиента раньше были
/// встроены прямо в CheckoutDialog и обрезались на невысоких экранах. Делит один и тот же
/// CheckoutViewModel с диалогом оплаты — вся бизнес-логика (поиск, добавление клиента) не
/// дублируется, здесь просто другой UI поверх тех же свойств/команд.
/// </summary>
public partial class ClientPickerDialog : Window
{
    private readonly CheckoutViewModel _viewModel;

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public ClientPickerDialog() : this(new CheckoutViewModel(new CartTotalsCalculator.CartTotals(), "", ""))
    {
    }

    public ClientPickerDialog(CheckoutViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public static System.Threading.Tasks.Task Open(Window owner, CheckoutViewModel viewModel) =>
        new ClientPickerDialog(viewModel).ShowDialog(owner);

    /// <summary>Закрывается сразу, как только клиент выбран или только что добавлен — кассиру
    /// не нужно ещё одно подтверждающее нажатие после того, как он уже кликнул по клиенту.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CheckoutViewModel.HasSelectedClient) && _viewModel.HasSelectedClient)
            Close();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
