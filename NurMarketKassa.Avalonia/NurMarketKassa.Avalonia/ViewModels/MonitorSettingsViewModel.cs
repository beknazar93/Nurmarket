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
            ["Product"] = Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"),
            ["Quantity"] = Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
            ["Price"] = Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx"),
            ["Discount"] = Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"),
            ["Total"] = Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"),
            ["Barcode"] = Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"),
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
        new(CustomerDisplayWindowMode.FullScreen, Tr.T("Полноэкранный", "Толук экран", "Full screen", "Tam ekran", "To'liq ekran")),
        new(CustomerDisplayWindowMode.WorkingArea, Tr.T("На весь рабочий стол", "Бүт иш столуна", "Entire desktop", "Tüm masaüstü", "Butun ish stoli bo'ylab")),
        new(CustomerDisplayWindowMode.Windowed, Tr.T("Оконный режим", "Терезе режими", "Windowed mode", "Pencere modu", "Oyna rejimi")),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayColumnMode>> ColumnModes { get; } =
    [
        new(CustomerDisplayColumnMode.Auto, Tr.T("Автоматически", "Автоматтык түрдө", "Automatic", "Otomatik", "Avtomatik")),
        new(CustomerDisplayColumnMode.Two, "2"),
        new(CustomerDisplayColumnMode.Three, "3"),
        new(CustomerDisplayColumnMode.Four, "4"),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayTheme>> Themes { get; } =
    [
        new(CustomerDisplayTheme.Light, Tr.T("Светлая", "Ачык", "Light", "Açık", "Yorug'")),
        new(CustomerDisplayTheme.Dark, Tr.T("Тёмная", "Караңгы", "Dark", "Koyu", "Qorong'i")),
        new(CustomerDisplayTheme.System, Tr.T("Системная", "Системалык", "System", "Sistem", "Tizim")),
    ];
    public IReadOnlyList<MonitorChoice<CustomerDisplayAdvertisementPosition>> AdvertisementPositions { get; } =
    [
        new(CustomerDisplayAdvertisementPosition.Right, Tr.T("Справа", "Оң жакта", "Right", "Sağda", "O'ngda")),
        new(CustomerDisplayAdvertisementPosition.Bottom, Tr.T("Снизу", "Ылдыйда", "Bottom", "Altta", "Pastda")),
        new(CustomerDisplayAdvertisementPosition.EmptyScreen, Tr.T("На пустом экране", "Бош экранда", "On the empty screen", "Boş ekranda", "Bo'sh ekranda")),
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
            ? Tr.T("Не удалось получить список экранов.", "Экрандардын тизмесин алуу мүмкүн болгон жок.", "Couldn't get the list of screens.", "Ekran listesi alınamadı.", "Ekranlar ro'yxatini olib bo'lmadi.")
            : savedId is not null && SelectedScreen?.Id != savedId
                ? Tr.T("Сохранённый экран не найден. Выбран доступный экран.", "Сакталган экран табылган жок. Жеткиликтүү экран тандалды.", "The saved screen wasn't found. An available screen was selected.", "Kaydedilen ekran bulunamadı. Kullanılabilir bir ekran seçildi.", "Saqlangan ekran topilmadi. Mavjud ekran tanlandi.")
                : Tr.T($"{Screens.Count} экран(а) доступно.", $"Жеткиликтүү экрандар: {Screens.Count}.", $"Screens available: {Screens.Count}.", $"Kullanılabilir ekran sayısı: {Screens.Count}.", $"Mavjud ekranlar soni: {Screens.Count}.");
    }

    public void Preview()
    {
        SyncColumns();
        StatusMessage = Tr.T("Предпросмотр открыт: ", "Алдын ала көрүү ачылды: ", "Preview opened: ", "Önizleme açıldı: ", "Oldindan ko'rish ochildi: ") + _service.Preview(Settings);
    }

    public void OpenDisplay()
    {
        SyncColumns();
        _service.ApplySettings(Settings);
        _service.OpenManually();
        StatusMessage = Tr.T("Экран покупателя открыт.", "Сатып алуучунун экраны ачылды.", "Customer display opened.", "Müşteri ekranı açıldı.", "Xaridor ekrani ochildi.");
    }

    public void ShowDisplay()
    {
        SyncColumns();
        _service.ApplySettings(Settings);
        _service.ShowManually();
        StatusMessage = Tr.T("Экран перемещён на выбранный монитор.", "Экран тандалган мониторго жылдырылды.", "The display was moved to the selected monitor.", "Ekran seçilen monitöre taşındı.", "Ekran tanlangan monitorga ko'chirildi.");
    }

    public void Save()
    {
        SyncColumns();
        StatusMessage = _service.ApplySettings(Settings);
    }

    public void CloseDisplay()
    {
        _service.CloseDisplay();
        StatusMessage = Tr.T("Окно покупателя закрыто.", "Сатып алуучунун терезеси жабылды.", "Customer window closed.", "Müşteri penceresi kapatıldı.", "Xaridor oynasi yopildi.");
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
