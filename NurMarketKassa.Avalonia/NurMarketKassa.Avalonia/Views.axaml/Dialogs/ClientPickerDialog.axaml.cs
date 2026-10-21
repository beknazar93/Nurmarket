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
        // 2026-10-21 (пакет 6, п. 26): пустой результат поиска — «не найдено» с подсказкой, а не пустая рамка.
        _viewModel.ClientSearchResults.CollectionChanged += OnClientResultsChanged;
        UpdateNotFound();
        NewClientExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
                OnNewClientExpanded();
        };
    }

    /// <summary>2026-10-21 (пакет 6, п. 26): форма «Новый клиент» — вместо списка (на экране 768 они вместе не помещались,
    /// и список сжимался в полоску 10 px); свернули форму — список снова виден.</summary>
    private void OnNewClientExpanded()
    {
        var expanded = NewClientExpander.IsExpanded;
        ClientListBorder.IsVisible = !expanded;
        PickerGrid.RowDefinitions[2].Height = expanded ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    private void OnClientResultsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateNotFound();

    private void UpdateNotFound()
    {
        var empty = _viewModel.ClientSearchResults.Count == 0 && !_viewModel.IsLoadingClients
            && string.IsNullOrEmpty(_viewModel.ErrorMessage);
        NotFoundText.IsVisible = empty;
        if (!empty)
            return;
        var query = (_viewModel.ClientSearchText ?? "").Trim();
        NotFoundText.Text = query.Length > 0
            ? Tr.T($"Клиент «{query}» не найден. Проверьте имя или телефон — или добавьте нового клиента ниже.",
                $"«{query}» кардары табылган жок. Атын же телефонун текшериңиз — же төмөндө жаңы кардар кошуңуз.",
                $"No client “{query}” found. Check the name or phone — or add a new client below.",
                $"«{query}» müşterisi bulunamadı. İsmi veya telefonu kontrol edin — ya da aşağıdan yeni müşteri ekleyin.",
                $"«{query}» mijozi topilmadi. Ism yoki telefonni tekshiring — yoki quyida yangi mijoz qo'shing.")
            : Tr.T("Клиентов пока нет — добавьте нового ниже.", "Азырынча кардарлар жок — төмөндө жаңысын кошуңуз.",
                "No clients yet — add a new one below.", "Henüz müşteri yok — aşağıdan yeni ekleyin.", "Hozircha mijozlar yo'q — quyida yangisini qo'shing.");
    }

    public static System.Threading.Tasks.Task Open(Window owner, CheckoutViewModel viewModel) =>
        new ClientPickerDialog(viewModel).ShowDialog(owner);

    /// <summary>2026-10-04, ТЗ разработчика NurCRM: скан QR клиента «NURCRM…», а клиента с таким телефоном
    /// в базе нет — то же окно, но сразу раскрыт «Новый клиент» (телефон уже подставлен в модели), курсор в
    /// поле имени: ФИО вводит кассир. Можно и выбрать клиента из списка или просто закрыть окно.</summary>
    public static System.Threading.Tasks.Task OpenForNewClient(Window owner, CheckoutViewModel viewModel)
    {
        var dialog = new ClientPickerDialog(viewModel);
        dialog.NewClientExpander.IsExpanded = true;
        dialog.OnNewClientExpanded();
        dialog.Opened += (_, _) => dialog.NewClientNameBox.Focus();
        return dialog.ShowDialog(owner);
    }

    /// <summary>Закрывается сразу, как только клиент выбран или только что добавлен — кассиру
    /// не нужно ещё одно подтверждающее нажатие после того, как он уже кликнул по клиенту.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CheckoutViewModel.HasSelectedClient) && _viewModel.HasSelectedClient)
            Close();
        else if (e.PropertyName is nameof(CheckoutViewModel.IsLoadingClients) or nameof(CheckoutViewModel.ErrorMessage))
            UpdateNotFound();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.ClientSearchResults.CollectionChanged -= OnClientResultsChanged;
        base.OnClosed(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
