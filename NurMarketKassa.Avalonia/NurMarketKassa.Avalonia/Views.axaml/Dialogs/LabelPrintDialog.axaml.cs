using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class LabelPrintDialog : Window, INotifyPropertyChanged
{
    private readonly CatalogProductTileVm _product;
    private LabelTemplate _template;
    private decimal? _copies = 1;
    private DiscoveredPrinter? _selectedPrinter;
    private string _statusMessage = "";
    private bool _isBusy;
    private Bitmap? _preview;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DiscoveredPrinter> Printers { get; } = new();

    public string ProductName => _product.Title;
    public string Barcode => _product.Barcode ?? "";
    public string PriceText => _product.PriceLine;
    public bool HasBarcode => !string.IsNullOrWhiteSpace(Barcode);
    private string? Sku => _product.Article;
    private string? Unit => _product.Unit;
    private string? StoreName => UserPreferences.Instance.StoreName;

    public decimal? Copies
    {
        get => _copies;
        set
        {
            var clamped = Math.Clamp(value ?? 1, 1, 99);
            if (_copies == clamped)
                return;
            _copies = clamped;
            OnPropertyChanged();
        }
    }

    public DiscoveredPrinter? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (_selectedPrinter == value)
                return;
            _selectedPrinter = value;
            OnPropertyChanged();
            if (value is not null)
            {
                UserPreferences.Instance.LabelPrinterDevicePath = value.DevicePath;
                UserPreferences.Instance.SaveToDisk();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public Bitmap? Preview
    {
        get => _preview;
        private set { _preview = value; OnPropertyChanged(); }
    }

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public LabelPrintDialog() : this(new CatalogProductTileVm("", "—", "", false))
    {
    }

    public LabelPrintDialog(CatalogProductTileVm product)
    {
        _product = product;
        _template = LabelTemplateStore.Load();
        InitializeComponent();
        DataContext = this;

        try
        {
            foreach (var printer in BarcodeLabelService.GetAvailablePrinters())
                Printers.Add(printer);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label dialog: printer enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        var savedPath = UserPreferences.Instance.LabelPrinterDevicePath;
        SelectedPrinter = (savedPath is not null
            ? Printers.FirstOrDefault(p => string.Equals(p.DevicePath, savedPath, StringComparison.OrdinalIgnoreCase))
            : null) ?? Printers.FirstOrDefault();

        if (!HasBarcode)
            StatusMessage = "У товара не указан штрих-код — печать недоступна.";
        else if (Printers.Count == 0)
            StatusMessage = "Не найдено ни одного установленного принтера в Windows.";

        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!HasBarcode)
            return;

        try
        {
            using var bmp = BarcodeLabelService.GenerateLabelBitmap(ProductName, Barcode, PriceText, _template, Sku, Unit, StoreName);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            Preview = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label preview render failed: {ex}", "WARNING");
            StatusMessage = "Не удалось построить предпросмотр этикетки.";
        }
    }

    private async void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasBarcode)
            return;

        if (SelectedPrinter is null)
        {
            StatusMessage = "Выберите принтер.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Печать…";
        try
        {
            var request = new LabelPrintRequest(ProductName, Barcode, PriceText, (int)(Copies ?? 1), SelectedPrinter.DevicePath, _template, Sku, Unit, StoreName);
            var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
            StatusMessage = result switch
            {
                LabelPrintResult.Success => "Этикетка отправлена на печать.",
                LabelPrintResult.PrinterNotFound => "Принтер не найден — обновите список.",
                _ => "Ошибка печати. Подробности в журнале приложения.",
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void EditTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new LabelTemplateEditorDialog(_template, ProductName, HasBarcode ? Barcode : "4870145004807", PriceText);
        await editor.ShowDialog(this).ConfigureAwait(true);
        if (editor.Saved)
        {
            _template = LabelTemplateStore.Load();
            RefreshPreview();
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
