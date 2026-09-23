using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Один пункт выпадающего списка шаблонов ценника.</summary>
internal sealed record PriceTagKindOption(PriceTagKind Kind, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public partial class PriceTagPrintDialog : Window
{
    private readonly CatalogProductTileVm _product;
    private LabelTemplate _customTemplate = LabelTemplate.CreateDefault();
    private bool _isInitializing = true;

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public PriceTagPrintDialog() : this(new CatalogProductTileVm("", "—", "", false))
    {
    }

    public PriceTagPrintDialog(CatalogProductTileVm product)
    {
        _product = product;
        _customTemplate = PriceTagTemplateStore.Load();
        InitializeComponent();

        ProductNameText.Text = _product.Title;

        KindCombo.ItemsSource = new[]
        {
            new PriceTagKindOption(PriceTagKind.Simple, Tr.T("Простой (название + цена)", "Жөнөкөй (аталышы + баасы)", "Simple (name + price)", "Basit (ad + fiyat)", "Oddiy (nomi + narxi)")),
            new PriceTagKindOption(PriceTagKind.WithBarcode, Tr.T("Со штрих-кодом", "Штрих-код менен", "With barcode", "Barkodlu", "Shtrix-kod bilan")),
            new PriceTagKindOption(PriceTagKind.Promotional, Tr.T("Акционный (старая цена + скидка)", "Акциялык (эски баа + арзандатуу)", "Promo (old price + discount)", "Promosyon (eski fiyat + indirim)", "Aksiya (eski narx + chegirma)")),
            new PriceTagKindOption(PriceTagKind.PromotionalColored, Tr.T("Акционный с цветным фоном", "Түстүү фондо акциялык", "Promo with colored background", "Renkli arka planlı promosyon", "Rangli fonli aksiya")),
            new PriceTagKindOption(PriceTagKind.Detailed, Tr.T("Подробный (магазин, категория, штрих-код)", "Кеңири (дүкөн, категория, штрих-код)", "Detailed (store, category, barcode)", "Detaylı (mağaza, kategori, barkod)", "Batafsil (do'kon, kategoriya, shtrix-kod)")),
            new PriceTagKindOption(PriceTagKind.WithQr, Tr.T("С QR-кодом", "QR-код менен", "With QR code", "QR kodlu", "QR-kod bilan")),
            new PriceTagKindOption(PriceTagKind.Custom, Tr.T("🎨 Пользовательский шаблон", "🎨 Колдонуучунун шаблону", "🎨 Custom template", "🎨 Özel şablon", "🎨 Foydalanuvchi shabloni")),
        };

        var hasBarcode = !string.IsNullOrWhiteSpace(_product.Barcode);
        KindCombo.SelectedIndex = hasBarcode ? 1 : 0;

        RefreshPrinterList();

        _isInitializing = false;
        ApplyKindDefaults();
    }

    private void KindCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyKindDefaults();

    private void ApplyKindDefaults()
    {
        if (_isInitializing || KindCombo.SelectedItem is not PriceTagKindOption option)
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
            PromoFieldsPanel.IsVisible = false;
        }
        else
        {
            var (w, h) = PriceTagService.GetDefaultSize(option.Kind);
            WidthBox.Value = (decimal)w;
            HeightBox.Value = (decimal)h;
            PromoFieldsPanel.IsVisible = option.Kind is PriceTagKind.Promotional or PriceTagKind.PromotionalColored;
        }

        RefreshPreview();
    }

    private async void EditTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new LabelTemplateEditorDialog(
            _customTemplate, _product.Title, _product.Barcode ?? "4870145004807", _product.PriceLine,
            PriceTagTemplateStore.Save, Tr.T("Редактор ценника", "Ценник редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorlig'i muharriri"));
        await editor.ShowDialog(this).ConfigureAwait(true);
        if (editor.Saved)
        {
            _customTemplate = PriceTagTemplateStore.Load();
            ApplyKindDefaults();
        }
    }

    private void Size_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => RefreshPreview();

    private void PromoFields_Changed(object? sender, Avalonia.Controls.TextChangedEventArgs e) => RefreshPreview();

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

        if (printers.Count == 0)
            StatusText.Text = isA4
                ? Tr.T("Не найдено ни одного установленного в Windows принтера.", "Windows'то орнотулган принтер табылган жок.", "No printers installed in Windows were found.", "Windows'ta yüklü hiçbir yazıcı bulunamadı.", "Windows'da o'rnatilgan printer topilmadi.")
                : Tr.T("Не найдено ни одного принтера.", "Принтер табылган жок.", "No printers found.", "Hiç yazıcı bulunamadı.", "Hech qanday printer topilmadi.");
    }

    private PriceTagData BuildData()
    {
        var kind = (KindCombo.SelectedItem as PriceTagKindOption)?.Kind ?? PriceTagKind.Simple;
        string? oldPrice = null;
        string? discount = null;
        if (kind is PriceTagKind.Promotional or PriceTagKind.PromotionalColored)
        {
            oldPrice = string.IsNullOrWhiteSpace(OldPriceBox.Text) ? null : OldPriceBox.Text;
            discount = string.IsNullOrWhiteSpace(DiscountBox.Text) ? null : DiscountBox.Text;
        }

        return new PriceTagData(
            ProductName: _product.Title,
            Barcode: _product.Barcode,
            PriceText: _product.PriceLine,
            OldPriceText: oldPrice,
            DiscountText: discount,
            Sku: _product.Article,
            Unit: _product.Unit,
            Category: _product.Category ?? _product.Brand,
            StoreName: UserPreferences.Instance.StoreName);
    }

    private void RefreshPreview()
    {
        if (_isInitializing || KindCombo.SelectedItem is not PriceTagKindOption option)
            return;

        try
        {
            var widthMm = (double)(WidthBox.Value ?? 40m);
            var heightMm = (double)(HeightBox.Value ?? 30m);
            using var bmp = option.Kind == PriceTagKind.Custom
                ? BarcodeLabelService.GenerateLabelBitmap(
                    _product.Title, _product.Barcode ?? "", _product.PriceLine, _customTemplate,
                    sku: _product.Article, unit: _product.Unit, storeName: UserPreferences.Instance.StoreName)
                : PriceTagService.GenerateBitmap(option.Kind, BuildData(), widthMm, heightMm);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            PreviewImage.Source = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag preview render failed: {ex}", "WARNING");
            StatusText.Text = Tr.T("Не удалось построить предпросмотр ценника.", "Ценниктин алдын ала көрүнүшүн түзүү мүмкүн болгон жок.", "Could not build the price tag preview.", "Fiyat etiketi önizlemesi oluşturulamadı.", "Narx yorlig'i ko'rinishini yaratib bo'lmadi.");
        }
    }

    private async void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        if (KindCombo.SelectedItem is not PriceTagKindOption option)
            return;

        if (PrinterCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            StatusText.Text = Tr.T("Выберите принтер.", "Принтерди тандаңыз.", "Choose a printer.", "Bir yazıcı seçin.", "Printerni tanlang.");
            return;
        }

        PrintButton.IsEnabled = false;
        StatusText.Text = Tr.T("Печать…", "Басып чыгарылууда…", "Printing…", "Yazdırılıyor…", "Chop etilmoqda…");
        try
        {
            // 2026-09-16, живой баг из журнала кассы ("не работает печать на принтер выдает
            // ошибки" — CRITICAL Call from invalid thread в GetValue у WidthBox/HeightBox/
            // OldPriceBox/DiscountBox): все значения из полей формы читаются здесь, на UI-потоке,
            // ДО ухода в Task.Run — Avalonia бросает исключение при обращении к свойствам
            // контрола с фонового потока, а BuildData()/WidthBox.Value/HeightBox.Value раньше
            // вычислялись ВНУТРИ лямбды, переданной в Task.Run, то есть уже на чужом потоке.
            var target = TargetA4Radio.IsChecked == true ? PriceTagPrintTarget.A4 : PriceTagPrintTarget.Thermal;
            var copies = (int)(CopiesBox.Value ?? 1m);
            var data = BuildData();
            var widthMm = (double)(WidthBox.Value ?? 40m);
            var heightMm = (double)(HeightBox.Value ?? 30m);

            var result = option.Kind == PriceTagKind.Custom
                ? await System.Threading.Tasks.Task.Run(() => PriceTagService.PrintBatchCustomTemplate(
                    new[] { (data, copies) }, _customTemplate, printer.DevicePath, target)).ConfigureAwait(true)
                : await System.Threading.Tasks.Task.Run(() => PriceTagService.Print(new PriceTagPrintRequest(
                    option.Kind, data, widthMm, heightMm,
                    copies, printer.DevicePath, target))).ConfigureAwait(true);
            StatusText.Text = result switch
            {
                LabelPrintResult.Success => Tr.T("Ценник отправлен на печать.", "Ценник басып чыгарууга жөнөтүлдү.", "Price tag sent to print.", "Fiyat etiketi yazdırmaya gönderildi.", "Narx yorlig'i chop etishga yuborildi."),
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
