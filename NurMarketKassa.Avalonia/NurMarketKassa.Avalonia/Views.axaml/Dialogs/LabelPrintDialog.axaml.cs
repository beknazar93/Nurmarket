using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Services;
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
    private string? _barcodeWarning;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DiscoveredPrinter> Printers { get; } = new();

    public string ProductName => _product.Title;
    public string Barcode => _product.Barcode ?? "";
    public string PriceText => _product.PriceLine;
    public bool HasBarcode => !string.IsNullOrWhiteSpace(Barcode);
    /// <summary>2026-10-26: штрих-код нужен, только если он включён в шаблоне (печать одного названия или одной цены).</summary>
    public bool CanPrint => HasBarcode || !_template.Barcode.Enabled;
    /// <summary>2026-11-06: В6 Д4-24 — «Печать» неактивна, пока принтер не выбран (раньше о пустом принтере узнавали после нажатия). Где печать вообще не поддержана — кнопка активна: нажатие объясняет причину.</summary>
    public bool CanPrintNow => CanPrint && (SelectedPrinter is not null || !LabelPrinterSelector.PrintingSupported);
    /// <summary>2026-11-06: В6 Д4-24 — подсказка в пустом списке принтеров.</summary>
    public string PrinterPlaceholder => Tr.T("Выберите принтер этикеток", "Этикетка принтерин тандаңыз", "Select a label printer", "Etiket yazıcısını seçin", "Yorliq printerini tanlang");
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

    private bool _initializing; // 2026-10-27: стартовая подстановка принтера в настройки не пишется

    public DiscoveredPrinter? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (_selectedPrinter == value)
                return;
            _selectedPrinter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPrintNow));
            // 2026-10-27 (приёмка 26.10): в настройки владельца попадал «Microsoft Print to PDF» — окно само подставляло первый принтер из списка.
            // Запоминаем только выбор кассира (не стартовую подстановку) и только реальный принтер этикеток, не PDF/XPS/OneNote/факс.
            if (value is not null && !_initializing && !value.IsVirtual
                && !LabelPrinterSelector.IsSameAsReceiptPrinter(value.DevicePath)) // 2026-11-05 (аудит №3, С2-29): принтер чеков не запоминаем как принтер этикеток (после этого «одна кнопка» слала бы этикетки на чековый); в этом окне печать на нём остаётся доступной.
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

        // 2026-11-05 (аудит №3, С2-26): перебор принтеров (USB/LPT/COM/сеть, до секунд) раньше шёл в конструкторе на потоке окна — «Этикетка» замирала. Теперь окно
        // открывается сразу, список принтеров и выбор по настройкам подгружаются в фоне (LoadPrintersAsync).
        if (!CanPrint)
            StatusMessage = Tr.T("У товара не указан штрих-код — печать недоступна.", "Товардын штрихкоду көрсөтүлгөн эмес — басып чыгаруу мүмкүн эмес.", "The product has no barcode — printing is unavailable.", "Ürünün barkodu yok — yazdırılamaz.", "Mahsulotda shtrix-kod ko'rsatilmagan — chop etib bo'lmaydi.");

        RefreshPreview();
        _ = LoadPrintersAsync();
    }

    /// <summary>2026-11-05 (С2-26): список принтеров этикеток — в фоне; по готовности подставляется принтер из Настроек и подсказка (как раньше делал конструктор).</summary>
    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<DiscoveredPrinter> list = Array.Empty<DiscoveredPrinter>();
        try
        {
            // 2026-11-01: список принтеров этикеток — без PDF/XPS/OneNote/факса.
            list = await Task.Run(() => LabelPrinterSelector.GetChoices()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label dialog: printer enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        foreach (var printer in list)
            Printers.Add(printer);

        // 2026-11-01 (два принтера): печать — сразу на принтер этикеток из Настроек; «первый попавшийся» из списка больше не
        // подставляется (им часто оказывался принтер чеков). Не выбран/не найден — подсказка, список ниже остаётся для выбора.
        var resolution = LabelPrinterSelector.Resolve(Printers);
        _initializing = true;
        SelectedPrinter = resolution.Printer;
        _initializing = false;

        // Подсказки — только если окно ещё ничего не сказало (нет штрихкода / предупреждение предпросмотра важнее).
        if (!string.IsNullOrEmpty(StatusMessage))
            return;
        if (Printers.Count == 0)
            StatusMessage = Tr.T("Не найдено ни одного установленного принтера в Windows.", "Windows'то бир дагы орнотулган принтер табылган жок.", "No printers are installed in Windows.", "Windows'ta yüklü yazıcı bulunamadı.", "Windows'da birorta ham o'rnatilgan printer topilmadi.");
        else if (!resolution.IsReady)
            StatusMessage = LabelPrinterSelector.HintText(resolution.State);
        else if (LabelPrinterSelector.LooksLikeTextOnlyQueue(resolution.Printer?.Label))
            StatusMessage = LabelPrinterSelector.TextOnlyWarning; // 2026-11-01 (С-8)
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(CanPrint));
        OnPropertyChanged(nameof(CanPrintNow));
        if (!CanPrint)
            return;

        try
        {
            // 2026-10-29: Android/Linux — System.Drawing недоступен, картинку рисует Avalonia (LabelSceneRenderer).
            Preview = LabelSceneRenderer.LabelPreview(ProductName, Barcode, PriceText, _template, Sku, Unit, StoreName);

            // Штрих-код не в выбранном формате / не напечатается — объясняем почему.
            var warning = BarcodeLabelService.DescribeBarcodeProblem(Barcode, _template);
            if (warning is not null)
                StatusMessage = warning;
            else if (_barcodeWarning is not null && StatusMessage == _barcodeWarning)
                StatusMessage = "";
            _barcodeWarning = warning;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label preview render failed: {ex}", "WARNING");
            StatusMessage = Tr.T("Не удалось построить предпросмотр этикетки.", "Этикетканы алдын ала көрүүнү түзүү мүмкүн болгон жок.", "Could not build the label preview.", "Etiket önizlemesi oluşturulamadı.", "Yorliqni oldindan ko'rishni yaratib bo'lmadi.");
        }
    }

    private async void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanPrint)
            return;

        if (!BarcodePrintAccess.IsAllowed()) // 2026-11-01 (П-10)
        {
            StatusMessage = BarcodePrintAccess.DeniedText;
            return;
        }
        if (!LabelPrinterSelector.PrintingSupported)
        {
            StatusMessage = LabelPrinterSelector.UnsupportedText; // 2026-11-01 (С-9)
            return;
        }
        if (SelectedPrinter is null)
        {
            StatusMessage = LabelPrinterSelector.MissingHint();
            return;
        }

        IsBusy = true;
        StatusMessage = Tr.T("Печать…", "Басып чыгарылууда…", "Printing…", "Yazdırılıyor…", "Chop etilmoqda…");
        try
        {
            var request = new LabelPrintRequest(ProductName, Barcode, PriceText, (int)(Copies ?? 1), SelectedPrinter.DevicePath, _template, Sku, Unit, StoreName);
            var printerUsed = SelectedPrinter;
            var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
            if (result == LabelPrintResult.Success)
                LabelPrinterSelector.LogSent(printerUsed, printerUsed.DevicePath, request.Copies);
            StatusMessage = result switch
            {
                LabelPrintResult.Success => LabelPrinterSelector.SentText(printerUsed.Label),
                LabelPrintResult.PartiallySent => LabelPrinterSelector.PartialText(printerUsed.DevicePath),
                LabelPrintResult.PrinterNotFound => Tr.T("Принтер не найден — обновите список.", "Принтер табылган жок — тизмени жаңыртыңыз.", "Printer not found — refresh the list.", "Yazıcı bulunamadı — listeyi yenileyin.", "Printer topilmadi — ro'yxatni yangilang."),
                _ => Tr.T("Ошибка печати. Подробности в журнале приложения.", "Басып чыгаруу катасы. Чоо-жайы колдонмонун журналында.", "Print error. See the application log for details.", "Yazdırma hatası. Ayrıntılar uygulama günlüğünde.", "Chop etish xatosi. Tafsilotlar dastur jurnalida."),
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void EditTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-27 (аудит редакторов, Б-2): редактор этикеток платный — из этого бесплатного окна он открывался без проверки покупки.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.LabelEditor, this))
            return;
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
