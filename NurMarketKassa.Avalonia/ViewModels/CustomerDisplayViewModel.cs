using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed class CustomerDisplayViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CustomerDisplayStateService _state;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _clearTimer;
    private CustomerDisplayCartSnapshot _snapshot = new();
    private bool _previewMode;
    private string _statusText = "";
    private IBrush _statusBrush = Brushes.Gray;

    public CustomerDisplayViewModel(CustomerDisplayStateService state)
    {
        _state = state;
        Settings = UserPreferences.Instance.CustomerDisplay.Clone();
        CloseCustomerDisplayCommand = new RelayCommand(() => CloseRequested?.Invoke());
        _state.StateChanged += Refresh;
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => OnPropertyChanged(nameof(DateTimeText)));
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            _snapshot = new CustomerDisplayCartSnapshot();
            Lines.Clear();
            StatusText = "";
            RaisePresentationChanged();
        };
        _clockTimer.Start();
        Refresh();
    }

    public CustomerDisplaySettings Settings { get; private set; }
    public ICommand CloseCustomerDisplayCommand { get; }
    public Action? CloseRequested { get; set; }
    public ObservableCollection<CustomerDisplayItemViewModel> Lines { get; } = [];
    public ObservableCollection<CustomerDisplayTableCellViewModel> TableHeaders { get; } = [];
    public string StoreName => string.IsNullOrWhiteSpace(UserPreferences.Instance.StoreName)
        ? "MARKET PLUS"
        : UserPreferences.Instance.StoreName;
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; OnPropertyChanged(); }
    }
    public string TotalText => $"{_snapshot.Total:0.00} сом";
    public string SubtotalText => Tr.T($"Промежуточный итог: {_snapshot.Subtotal:0.00} сом", $"Аралык жыйынтык: {_snapshot.Subtotal:0.00} сом");
    public string DiscountText => Tr.T($"Скидка: {_snapshot.Discount:0.00} сом", $"Арзандатуу: {_snapshot.Discount:0.00} сом");
    public string SubtotalAmountText => $"{_snapshot.Subtotal:0.00} сом";
    public string DiscountAmountText => $"-{_snapshot.Discount:0.00} сом";
    public string ItemCountText => Tr.T($"{Lines.Count} поз.", $"{Lines.Count} позиция");
    public string DateTimeText => DateTime.Now.ToString("dd.MM.yyyy  HH:mm");
    public bool IsEmpty => Lines.Count == 0;
    public bool HasItems => !IsEmpty;
    public bool IsCardLayout => Settings.LayoutMode == CustomerDisplayLayoutMode.Cards;
    public bool IsTableLayout => !IsCardLayout;
    public bool ShowRightAdvertisement => Settings.AdvertisementEnabled && HasItems &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.Right;
    public bool ShowBottomAdvertisement => Settings.AdvertisementEnabled && HasItems &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.Bottom;
    public bool ShowEmptyAdvertisement => Settings.AdvertisementEnabled &&
        Settings.AdvertisementPosition == CustomerDisplayAdvertisementPosition.EmptyScreen;
    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(Settings.BackgroundImagePath);
    public string InformationImagePath
    {
        get
        {
            var preferences = UserPreferences.Instance;
            var bankQr = preferences.BankQrPaths?.Values
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (!string.IsNullOrWhiteSpace(bankQr))
                return bankQr;
            if (!string.IsNullOrWhiteSpace(preferences.QrCodePath) && File.Exists(preferences.QrCodePath))
                return preferences.QrCodePath;
            if (Settings.AdvertisementEnabled &&
                !string.IsNullOrWhiteSpace(Settings.AdvertisementImagePath) &&
                File.Exists(Settings.AdvertisementImagePath))
                return Settings.AdvertisementImagePath;
            return "";
        }
    }
    public bool HasInformationImage => !string.IsNullOrWhiteSpace(InformationImagePath);
    public bool HasPaymentQr =>
        (UserPreferences.Instance.BankQrPaths?.Values.Any(path =>
             !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? false) ||
        (!string.IsNullOrWhiteSpace(UserPreferences.Instance.QrCodePath) &&
         File.Exists(UserPreferences.Instance.QrCodePath));
    public string InformationEyebrow => HasPaymentQr ? Tr.T("ОПЛАТА ПО QR", "QR АРКЫЛУУ ТӨЛӨӨ") : Tr.T("ИНФОРМАЦИЯ", "МААЛЫМАТ");
    public string InformationTitle => HasPaymentQr
        ? Tr.T("Наведите камеру", "Камераны багыттаңыз")
        : string.IsNullOrWhiteSpace(Settings.AdvertisementTitle)
            ? Tr.T("Полезная информация", "Пайдалуу маалымат")
            : Settings.AdvertisementTitle;
    public string InformationDescription => HasPaymentQr
        ? Tr.T($"После подтверждения кассиром оплатите {_snapshot.Total:0.00} сом", $"Кассир ырастагандан кийин {_snapshot.Total:0.00} сом төлөңүз")
        : string.IsNullOrWhiteSpace(Settings.AdvertisementDescription)
            ? Tr.T("Здесь может отображаться акция, реклама или QR-код оплаты.", "Бул жерде акция, жарнама же төлөм QR-коду көрсөтүлүшү мүмкүн.")
            : Settings.AdvertisementDescription;
    public string ScannerStatusText => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Tr.T("Обработка оплаты", "Төлөм иштелүүдө"),
        CustomerDisplayPaymentStatus.Success => Tr.T("Оплата принята", "Төлөм кабыл алынды"),
        CustomerDisplayPaymentStatus.Failed => Tr.T("Оплата не прошла", "Төлөм өтпөй калды"),
        _ => Tr.T("Сканер готов", "Сканер даяр"),
    };
    public string ScannerStatusDescription => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Tr.T("Пожалуйста, подождите", "Күтө туруңуз"),
        CustomerDisplayPaymentStatus.Success => Tr.T("Спасибо за покупку", "Сатып алганыңыз үчүн рахмат"),
        CustomerDisplayPaymentStatus.Failed => Tr.T("Обратитесь к кассиру", "Кассирге кайрылыңыз"),
        _ => Tr.T("Можно сканировать следующий товар", "Кийинки товарды сканерлесе болот"),
    };
    public bool IsPreviewMode => _previewMode;
    public CornerRadius DisplayCornerRadius => new(Settings.CornerRadius);
    public Thickness PageMargin => new(24 * Settings.Scale);
    public double HeaderFontSize => 24 * Settings.Scale;
    public double BodyFontSize => 18 * Settings.Scale;
    public double EmptyTitleFontSize => 36 * Settings.Scale;
    public double TotalFontSize => 38 * Settings.Scale;
    public double CardWidth => Settings.CardColumns switch
    {
        CustomerDisplayColumnMode.Two => 380 * Settings.CardSize,
        CustomerDisplayColumnMode.Three => 300 * Settings.CardSize,
        CustomerDisplayColumnMode.Four => 240 * Settings.CardSize,
        _ => 280 * Settings.CardSize,
    };
    public double CardHeight => 225 * Settings.CardSize;
    public IBrush AccentBrush => Brush(Settings.AccentColor, "#FACC15");
    public IBrush AccentSoftBrush => WithAlpha(Settings.AccentColor, 42);
    public IBrush BackgroundBrush => Brush(Settings.BackgroundColor, "#F8FAFC");
    public IBrush SurfaceBrush => Settings.Theme == CustomerDisplayTheme.Dark
        ? Brush("#18202D", "#18202D") : Brushes.White;
    public IBrush SurfaceAltBrush => Settings.Theme == CustomerDisplayTheme.Dark
        ? Brush("#222C3A", "#222C3A") : Brush("#F8FAFC", "#F8FAFC");
    public IBrush TextBrush => Settings.Theme == CustomerDisplayTheme.Dark ? Brushes.White : Brush("#0F172A", "#0F172A");
    public IBrush SecondaryTextBrush => Settings.Theme == CustomerDisplayTheme.Dark
        ? Brush("#CBD5E1", "#CBD5E1") : Brush("#64748B", "#64748B");
    public IBrush BorderBrushValue => Settings.Theme == CustomerDisplayTheme.Dark
        ? Brush("#334155", "#334155") : Brush("#E2E8F0", "#E2E8F0");
    public IBrush DiscountBrush => Brush("#E11D48", "#E11D48");
    public IBrush DiscountSoftBrush => WithAlpha("#E11D48", 28);
    public IBrush ScannerStatusBrush => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => Brush("#F59E0B", "#F59E0B"),
        CustomerDisplayPaymentStatus.Success => Brush("#10B981", "#10B981"),
        CustomerDisplayPaymentStatus.Failed => Brush("#EF4444", "#EF4444"),
        _ => Brush("#10B981", "#10B981"),
    };
    public IBrush ScannerStatusSoftBrush => _state.CurrentStatus switch
    {
        CustomerDisplayPaymentStatus.Processing => WithAlpha("#F59E0B", 32),
        CustomerDisplayPaymentStatus.Success => WithAlpha("#10B981", 32),
        CustomerDisplayPaymentStatus.Failed => WithAlpha("#EF4444", 32),
        _ => WithAlpha("#10B981", 32),
    };

    public event EventHandler? PresentationChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplySettings(CustomerDisplaySettings settings)
    {
        Settings = settings.Clone();
        Settings.Normalize();
        OnPropertyChanged(nameof(Settings));
        Refresh();
    }

    public void SetPreviewMode(bool enabled)
    {
        _previewMode = enabled;
        OnPropertyChanged(nameof(IsPreviewMode));
        Refresh();
    }

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        _snapshot = _state.CurrentSnapshot;
        var columns = GetVisibleColumns();
        Lines.Clear();
        TableHeaders.Clear();
        foreach (var column in columns)
            TableHeaders.Add(new CustomerDisplayTableCellViewModel(ColumnLabel(column), ColumnWidth(column)));
        foreach (var line in _snapshot.Lines)
            Lines.Add(new CustomerDisplayItemViewModel(line, columns));

        if (_previewMode && Lines.Count == 0)
        {
            Lines.Add(new CustomerDisplayItemViewModel("Кофе натуральный", "4601234567890", 2, "шт", 320, columns));
            Lines.Add(new CustomerDisplayItemViewModel("Молоко 1 л", "4870123456789", 1, "шт", 95, columns));
            _snapshot = new CustomerDisplayCartSnapshot { Subtotal = 415, Discount = 15, Total = 400 };
        }

        (StatusText, StatusBrush) = _state.CurrentStatus switch
        {
            CustomerDisplayPaymentStatus.Processing => (_state.StatusMessage ?? Tr.T("Идёт оплата…", "Төлөм жүрүп жатат…"), Brushes.DarkOrange),
            CustomerDisplayPaymentStatus.Success => (_state.StatusMessage ?? Settings.SuccessText, Brushes.DarkGreen),
            CustomerDisplayPaymentStatus.Failed => (_state.StatusMessage ?? Tr.T("Ошибка оплаты", "Төлөм катасы"), Brushes.DarkRed),
            _ => ("", SecondaryTextBrush),
        };

        _clearTimer.Stop();
        if (_state.CurrentStatus == CustomerDisplayPaymentStatus.Success && Settings.ClearDelaySeconds > 0)
        {
            _clearTimer.Interval = TimeSpan.FromSeconds(Settings.ClearDelaySeconds);
            _clearTimer.Start();
        }
        RaisePresentationChanged();
    }

    private void RaisePresentationChanged()
    {
        foreach (var property in typeof(CustomerDisplayViewModel).GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0))
            OnPropertyChanged(property.Name);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<string> GetVisibleColumns() =>
        Settings.TableColumnOrder.Where(column => column switch
        {
            "Product" => Settings.ShowTableProduct,
            "Quantity" => Settings.ShowTableQuantity,
            "Price" => Settings.ShowTablePrice,
            "Discount" => Settings.ShowTableDiscount,
            "Total" => Settings.ShowTableTotal,
            "Barcode" => Settings.ShowTableBarcode,
            _ => false,
        }).ToArray();

    private static string ColumnLabel(string key) => key switch
    {
        "Product" => Tr.T("Товар", "Товар"), "Quantity" => Tr.T("Количество", "Саны"), "Price" => Tr.T("Цена", "Баасы"),
        "Discount" => Tr.T("Скидка", "Арзандатуу"), "Total" => Tr.T("Сумма", "Суммасы"), "Barcode" => Tr.T("Штрихкод", "Штрихкод"), _ => key,
    };
    private static double ColumnWidth(string key) => key == "Product" ? 280 : key == "Barcode" ? 180 : 145;

    private static IBrush Brush(string value, string fallback)
    {
        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return new SolidColorBrush(Color.Parse(fallback)); }
    }

    private static IBrush WithAlpha(string value, byte alpha)
    {
        try
        {
            var color = Color.Parse(value);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }
        catch { return new SolidColorBrush(Color.FromArgb(alpha, 250, 204, 21)); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _state.StateChanged -= Refresh;
        _clockTimer.Stop();
        _clearTimer.Stop();
    }
}

public sealed class CustomerDisplayItemViewModel
{
    public CustomerDisplayItemViewModel(CustomerDisplayLine line, IReadOnlyList<string> columns)
        : this(line.Title, line.Barcode, line.Quantity, line.Unit, line.LineTotal, columns) { }

    public CustomerDisplayItemViewModel(
        string title, string? barcode, double quantity, string unit, double lineTotal,
        IReadOnlyList<string> columns)
    {
        Title = title;
        Barcode = barcode?.Trim() ?? "";
        QuantityText = $"×{quantity.ToString("0.###", CultureInfo.InvariantCulture)} {unit}";
        UnitPriceText = $"{(quantity == 0 ? 0 : lineTotal / quantity):0.00} сом";
        TotalText = $"{lineTotal:0.00} сом";
        foreach (var column in columns)
        {
            var value = column switch
            {
                "Product" => Title, "Quantity" => QuantityText, "Price" => UnitPriceText,
                "Discount" => "—", "Total" => TotalText, "Barcode" => Barcode, _ => "",
            };
            TableCells.Add(new CustomerDisplayTableCellViewModel(value, ColumnWidth(column)));
        }
    }

    public string Title { get; }
    public string Barcode { get; }
    public bool HasBarcode => !string.IsNullOrWhiteSpace(Barcode);
    public string BarcodeDisplay => Tr.T($"ШК: {Barcode}", $"ШК: {Barcode}");
    public string QuantityText { get; }
    public string UnitPriceText { get; }
    public string TotalText { get; }
    public ObservableCollection<CustomerDisplayTableCellViewModel> TableCells { get; } = [];
    private static double ColumnWidth(string key) => key == "Product" ? 280 : key == "Barcode" ? 180 : 145;
}

public sealed record CustomerDisplayTableCellViewModel(string Text, double Width);
