using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed record MonitorChoice<T>(T Value, string Label);

public sealed class MonitorColumnOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    public required string Key { get; init; }
    public required string Label { get; init; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; PropertyChanged?.Invoke(this, new(nameof(IsEnabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MonitorSettingsViewModel : INotifyPropertyChanged
{
    private readonly Window _owner;
    private readonly AvaloniaCustomerDisplayService _service;
    private DisplayScreenOption? _selectedScreen;
    private string _statusMessage = "";

    public MonitorSettingsViewModel(Window owner, AvaloniaCustomerDisplayService service)
    {
        _owner = owner;
        _service = service;
        Settings = UserPreferences.Instance.CustomerDisplay.Clone();
        Settings.Normalize();

        var labels = new Dictionary<string, string>
        {
            ["Product"] = "Товар", ["Quantity"] = "Количество", ["Price"] = "Цена",
            ["Discount"] = "Скидка", ["Total"] = "Сумма", ["Barcode"] = "Штрихкод",
        };
        foreach (var key in Settings.TableColumnOrder.Concat(labels.Keys).Distinct())
            TableColumns.Add(new MonitorColumnOption
            {
                Key = key,
                Label = labels.GetValueOrDefault(key, key),
                IsEnabled = IsColumnEnabled(key),
            });

        RefreshScreens();
    }

    public CustomerDisplaySettings Settings { get; }
    public ObservableCollection<DisplayScreenOption> Screens { get; } = [];
    public ObservableCollection<MonitorColumnOption> TableColumns { get; } = [];

    public IReadOnlyList<MonitorChoice<CustomerDisplayWindowMode>> WindowModes { get; } =
    [
        new(CustomerDisplayWindowMode.FullScreen, "Полноэкранный"),
        new(CustomerDisplayWindowMode.WorkingArea, "На весь рабочий стол"),
        new(CustomerDisplayWindowMode.Windowed, "Оконный режим"),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayColumnMode>> ColumnModes { get; } =
    [
        new(CustomerDisplayColumnMode.Auto, "Автоматически"),
        new(CustomerDisplayColumnMode.Two, "2"),
        new(CustomerDisplayColumnMode.Three, "3"),
        new(CustomerDisplayColumnMode.Four, "4"),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayTheme>> Themes { get; } =
    [
        new(CustomerDisplayTheme.Light, "Светлая"),
        new(CustomerDisplayTheme.Dark, "Тёмная"),
        new(CustomerDisplayTheme.System, "Системная"),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayAdvertisementPosition>> AdvertisementPositions { get; } =
    [
        new(CustomerDisplayAdvertisementPosition.Right, "Справа"),
        new(CustomerDisplayAdvertisementPosition.Bottom, "Снизу"),
        new(CustomerDisplayAdvertisementPosition.EmptyScreen, "На пустом экране"),
    ];

    public DisplayScreenOption? SelectedScreen
    {
        get => _selectedScreen;
        set
        {
            if (ReferenceEquals(_selectedScreen, value)) return;
            _selectedScreen = value;
            Settings.SelectedScreenId = value?.Id;
            OnPropertyChanged();
        }
    }

    public bool IsCardLayoutSelected
    {
        get => Settings.LayoutMode == CustomerDisplayLayoutMode.Cards;
        set
        {
            if (value) Settings.LayoutMode = CustomerDisplayLayoutMode.Cards;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTableLayoutSelected));
        }
    }

    public bool IsTableLayoutSelected
    {
        get => Settings.LayoutMode == CustomerDisplayLayoutMode.Table;
        set
        {
            if (value) Settings.LayoutMode = CustomerDisplayLayoutMode.Table;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCardLayoutSelected));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool RequiresDisableConfirmation => !Settings.IsEnabled && _service.IsDisplayVisible;

    public void RefreshScreens()
    {
        var savedId = Settings.SelectedScreenId;
        Screens.Clear();
        foreach (var screen in _service.GetScreens(_owner))
            Screens.Add(screen);

        SelectedScreen = Screens.FirstOrDefault(x => x.Id == savedId)
                         ?? Screens.FirstOrDefault(x => !x.IsPrimary)
                         ?? Screens.FirstOrDefault();
        StatusMessage = Screens.Count == 0
            ? "Не удалось получить список экранов."
            : savedId is not null && SelectedScreen?.Id != savedId
                ? "Сохранённый экран не найден. Выбран доступный экран."
                : $"{Screens.Count} экран(а) доступно.";
    }

    public void Preview()
    {
        SyncColumns();
        StatusMessage = $"Предпросмотр открыт: {_service.Preview(Settings)}";
    }

    public void OpenDisplay()
    {
        SyncColumns();
        _service.ApplySettings(Settings);
        _service.OpenManually();
        StatusMessage = "Экран покупателя открыт.";
    }

    public void ShowDisplay()
    {
        SyncColumns();
        _service.ApplySettings(Settings);
        _service.ShowManually();
        StatusMessage = "Экран перемещён на выбранный монитор.";
    }

    public void Save()
    {
        SyncColumns();
        StatusMessage = _service.ApplySettings(Settings);
    }

    public void CloseDisplay()
    {
        _service.CloseDisplay();
        StatusMessage = "Окно покупателя закрыто.";
    }

    public void MoveColumn(MonitorColumnOption? column, int direction)
    {
        if (column is null) return;
        var index = TableColumns.IndexOf(column);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= TableColumns.Count) return;
        TableColumns.Move(index, target);
    }

    private bool IsColumnEnabled(string key) => key switch
    {
        "Product" => Settings.ShowTableProduct,
        "Quantity" => Settings.ShowTableQuantity,
        "Price" => Settings.ShowTablePrice,
        "Discount" => Settings.ShowTableDiscount,
        "Total" => Settings.ShowTableTotal,
        "Barcode" => Settings.ShowTableBarcode,
        _ => false,
    };

    private void SyncColumns()
    {
        Settings.TableColumnOrder = TableColumns.Select(x => x.Key).ToList();
        Settings.ShowTableProduct = TableColumns.First(x => x.Key == "Product").IsEnabled;
        Settings.ShowTableQuantity = TableColumns.First(x => x.Key == "Quantity").IsEnabled;
        Settings.ShowTablePrice = TableColumns.First(x => x.Key == "Price").IsEnabled;
        Settings.ShowTableDiscount = TableColumns.First(x => x.Key == "Discount").IsEnabled;
        Settings.ShowTableTotal = TableColumns.First(x => x.Key == "Total").IsEnabled;
        Settings.ShowTableBarcode = TableColumns.First(x => x.Key == "Barcode").IsEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
