using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class BulkPriceTagPrintDialog : Window
{
    private sealed class SelectionRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required CatalogProductTileVm Product { get; init; }
        public string Title => Product.Title;
        public string Barcode => Product.Barcode ?? "";
        public string PriceLine => Product.PriceLine;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<SelectionRow> _allRows;
    private LabelTemplate _customTemplate;

    public BulkPriceTagPrintDialog()
    {
        InitializeComponent();

        _customTemplate = PriceTagTemplateStore.Load();

        _allRows = CatalogCacheService.Products
            .Select(p => new SelectionRow { Product = p })
            .ToList();
        ProductsGrid.ItemsSource = _allRows;

        // Акционные шаблоны не предлагаем в массовой печати — старая цена/скидка у каждого
        // товара своя, а общего поля для их ввода на весь список нет (см. PriceTagPrintDialog,
        // где это делается для одного товара).
        var kindOptions = new[]
        {
            new PriceTagKindOption(PriceTagKind.Simple, Tr.T("Простой (название + цена)", "Жөнөкөй (аталышы + баасы)", "Simple (name + price)", "Basit (ad + fiyat)", "Oddiy (nomi + narxi)")),
            new PriceTagKindOption(PriceTagKind.WithBarcode, Tr.T("Со штрих-кодом", "Штрих-код менен", "With barcode", "Barkodlu", "Shtrix-kod bilan")),
            new PriceTagKindOption(PriceTagKind.Detailed, Tr.T("Подробный (магазин, категория, штрих-код)", "Кеңири (дүкөн, категория, штрих-код)", "Detailed (store, category, barcode)", "Detaylı (mağaza, kategori, barkod)", "Batafsil (do'kon, kategoriya, shtrix-kod)")),
            new PriceTagKindOption(PriceTagKind.WithQr, Tr.T("С QR-кодом", "QR-код менен", "With QR code", "QR kodlu", "QR-kod bilan")),
            new PriceTagKindOption(PriceTagKind.Custom, Tr.T("🎨 Пользовательский шаблон", "🎨 Колдонуучунун шаблону", "🎨 Custom template", "🎨 Özel şablon", "🎨 Foydalanuvchi shabloni")),
        };
        KindCombo.ItemsSource = kindOptions;

        // Запоминаем последние настройки печати между открытиями окна (2026-09-05, по просьбе
        // пользователя) — если раньше не печатали, остаётся прежнее поведение по умолчанию
        // (Со штрих-кодом, размер шаблона, термопринтер, 1 копия).
        var prefs = UserPreferences.Instance;
        var savedKindIndex = prefs.BulkPriceTagKind is { } savedKind
            ? Array.FindIndex(kindOptions, o => (int)o.Kind == savedKind)
            : -1;
        KindCombo.SelectedIndex = savedKindIndex >= 0 ? savedKindIndex : 1;

        var selectedKind = ((PriceTagKindOption)KindCombo.SelectedItem!).Kind;
        var (defaultW, defaultH) = selectedKind == PriceTagKind.Custom
            ? (_customTemplate.WidthMm, _customTemplate.HeightMm)
            : PriceTagService.GetDefaultSize(selectedKind);
        WidthBox.Value = (decimal)(prefs.BulkPriceTagWidthMm ?? defaultW);
        HeightBox.Value = (decimal)(prefs.BulkPriceTagHeightMm ?? defaultH);
        CopiesBox.Value = prefs.BulkPriceTagCopies ?? 1;
        if (prefs.BulkPriceTagTargetIsA4)
            TargetA4Radio.IsChecked = true;

        RefreshPrinterList();
        UpdateSelectionCount();
        ApplyKindUi();
    }

    public static async Task Open(Window? owner)
    {
        var dialog = new BulkPriceTagPrintDialog();
        await dialog.ShowDialog(owner!).ConfigureAwait(true);
    }

    private void SearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        var query = (SearchBox.Text ?? "").Trim();
        ProductsGrid.ItemsSource = query.Length == 0
            ? _allRows
            : _allRows.Where(r =>
                r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in (IEnumerable<SelectionRow>)ProductsGrid.ItemsSource!)
            row.IsSelected = true;
        UpdateSelectionCount();
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _allRows)
            row.IsSelected = false;
        UpdateSelectionCount();
    }

    private void KindCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionCount();
        ApplyKindUi();
    }

    private void ApplyKindUi()
    {
        if (KindCombo.SelectedItem is not PriceTagKindOption option)
            return;

        var isCustom = option.Kind == PriceTagKind.Custom;
        EditTemplateButton.IsVisible = isCustom;
        // Размер пользовательского шаблона настраивается внутри самого редактора — здесь только
        // отображаем его, без второго независимого источника истины для ширины/высоты.
        WidthBox.IsEnabled = !isCustom;
        HeightBox.IsEnabled = !isCustom;
        if (isCustom)
        {
            WidthBox.Value = (decimal)_customTemplate.WidthMm;
            HeightBox.Value = (decimal)_customTemplate.HeightMm;
        }
    }

    private async void EditTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new LabelTemplateEditorDialog(
            _customTemplate, Tr.T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Namunaviy mahsulot"), "4870145004807", "285 сом",
            PriceTagTemplateStore.Save, Tr.T("Редактор ценника", "Ценник редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorlig'i muharriri"));
        await editor.ShowDialog(this).ConfigureAwait(true);
        if (editor.Saved)
        {
            _customTemplate = PriceTagTemplateStore.Load();
            ApplyKindUi();
        }
    }

    private void Target_Changed(object? sender, RoutedEventArgs e) => RefreshPrinterList();

    private void RefreshPrinterList()
    {
        var isA4 = TargetA4Radio.IsChecked == true;
        var previouslySelected = PrinterCombo.SelectedItem as DiscoveredPrinter;

        var printers = PriceTagService.GetAvailablePrinters();
        if (isA4)
            printers = printers.Where(p => !BarcodeLabelService.IsRawDevicePath(p.DevicePath)).ToList();

        PrinterCombo.ItemsSource = printers;
        PrinterCombo.SelectedItem = previouslySelected is not null && printers.Any(p => p.DevicePath == previouslySelected.DevicePath)
            ? previouslySelected
            : printers.FirstOrDefault();
    }

    private void UpdateSelectionCount()
    {
        var count = _allRows.Count(r => r.IsSelected);
        SelectionCountText.Text = count == 0
            ? Tr.T("Товары не выбраны.", "Товарлар тандалган жок.", "No products selected.", "Ürün seçilmedi.", "Mahsulotlar tanlanmagan.")
            : Tr.T($"Выбрано товаров: {count}.", $"Тандалган товарлар: {count}.",
                $"Products selected: {count}.", $"Seçilen ürün: {count}.", $"Tanlangan mahsulotlar: {count}.");
    }

    private async void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        // IsSelected меняется через binding из DataGrid — пересчитываем счётчик на всякий
        // случай перед печатью, если пользователь кликал прямо перед нажатием кнопки.
        UpdateSelectionCount();

        if (KindCombo.SelectedItem is not PriceTagKindOption option)
            return;

        var selected = _allRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        if (PrinterCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            StatusText.Text = Tr.T("Выберите принтер.", "Принтерди тандаңыз.", "Choose a printer.", "Bir yazıcı seçin.", "Printerni tanlang.");
            return;
        }

        PrintButton.IsEnabled = false;
        StatusText.Text = Tr.T($"Печать {selected.Count} ценников…", $"{selected.Count} ценник басып чыгарылууда…",
            $"Printing {selected.Count} price tags…", $"{selected.Count} fiyat etiketi yazdırılıyor…", $"{selected.Count} ta narx yorlig'i chop etilmoqda…");
        try
        {
            var widthMm = (double)(WidthBox.Value ?? 40m);
            var heightMm = (double)(HeightBox.Value ?? 30m);
            var copies = (int)(CopiesBox.Value ?? 1m);
            var isA4 = TargetA4Radio.IsChecked == true;
            var target = isA4 ? PriceTagPrintTarget.A4 : PriceTagPrintTarget.Thermal;
            var storeName = UserPreferences.Instance.StoreName;

            var prefs = UserPreferences.Instance;
            prefs.BulkPriceTagKind = (int)option.Kind;
            prefs.BulkPriceTagWidthMm = widthMm;
            prefs.BulkPriceTagHeightMm = heightMm;
            prefs.BulkPriceTagTargetIsA4 = isA4;
            prefs.BulkPriceTagCopies = copies;
            prefs.SaveToDisk();

            var dataList = selected.Select(row => new PriceTagData(
                ProductName: row.Product.Title,
                Barcode: row.Product.Barcode,
                PriceText: row.Product.PriceLine,
                Sku: row.Product.Article,
                Unit: row.Product.Unit,
                Category: row.Product.Category ?? row.Product.Brand,
                StoreName: storeName)).ToList();

            var result = option.Kind == PriceTagKind.Custom
                ? await Task.Run(() => PriceTagService.PrintBatchCustomTemplate(
                    dataList.Select(d => (d, copies)).ToList(), _customTemplate, printer.DevicePath, target)).ConfigureAwait(true)
                : await Task.Run(() => PriceTagService.PrintBatch(
                    dataList.Select(d => (option.Kind, d, copies)).ToList(), widthMm, heightMm, printer.DevicePath, target)).ConfigureAwait(true);

            StatusText.Text = result switch
            {
                LabelPrintResult.Success => Tr.T($"Готово — отправлено на печать {selected.Count} ценников.", $"Даяр — {selected.Count} ценник басып чыгарууга жөнөтүлдү.",
                    $"Done — {selected.Count} price tags sent to print.", $"Tamamlandı — {selected.Count} fiyat etiketi yazdırmaya gönderildi.",
                    $"Tayyor — {selected.Count} ta narx yorlig'i chop etishga yuborildi."),
                LabelPrintResult.PrinterNotFound => Tr.T("Принтер не найден — обновите список.", "Принтер табылган жок — тизмени жаңыртыңыз.", "Printer not found — refresh the list.", "Yazıcı bulunamadı — listeyi yenileyin.", "Printer topilmadi — ro'yxatni yangilang."),
                _ => Tr.T("Ошибка печати. Подробности в журнале приложения.", "Басып чыгаруу катасы. Толук маалымат колдонмонун журналында.", "Printing error. See details in the app log.", "Yazdırma hatası. Ayrıntılar için uygulama günlüğüne bakın.", "Chop etishda xato. Batafsil ma'lumot ilova jurnalida."),
            };
        }
        finally
        {
            PrintButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
