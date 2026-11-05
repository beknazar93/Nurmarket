using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NurMarketKassa.AvaloniaHost.Services;
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
            new PriceTagKindOption(PriceTagKind.PromotionalColored, Tr.T("Акционный с цветным фоном", "Акциялык, түстүү фон менен", "Promo with colored background", "Renkli arka planlı promosyon", "Rangli fonli aksiya")),
            new PriceTagKindOption(PriceTagKind.Detailed, Tr.T("Подробный (магазин, категория, штрих-код)", "Толук (дүкөн, категория, штрих-код)", "Detailed (store, category, barcode)", "Detaylı (mağaza, kategori, barkod)", "Batafsil (do'kon, kategoriya, shtrix-kod)")),
            new PriceTagKindOption(PriceTagKind.WithQr, Tr.T("С QR-кодом", "QR-код менен", "With QR code", "QR kodlu", "QR-kod bilan")),
            new PriceTagKindOption(PriceTagKind.Custom, Tr.T("🎨 Пользовательский шаблон", "🎨 Колдонуучунун шаблону", "🎨 Custom template", "🎨 Özel şablon", "🎨 Foydalanuvchi shabloni")),
        };

        var hasBarcode = !string.IsNullOrWhiteSpace(_product.Barcode);
        KindCombo.SelectedIndex = hasBarcode ? 1 : 0;

        PrinterCombo.SelectionChanged += PrinterCombo_SelectionChanged;
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
            _customTemplate, _product.Title, _product.Barcode ?? "4870145004807", PriceDisplay,
            PriceTagTemplateStore.Save, Tr.T("Редактор ценника", "Баа белгиси редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorlig'i muharriri"));
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

    // 2026-11-01 (проверка массовой печати, В-2/С-4/С-6): принтер хранится по режиму отдельно (термо / A4), список ищется не на потоке интерфейса.
    private bool _shownA4;
    private string? _thermalChoice, _a4Choice;
    private bool _printersLoaded, _listGuard;
    private int _listVersion;

    private void PrinterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Принтер этикеток запоминаем только по явному выбору кассира в режиме «Термо» (не по факту печати, не при подстановке списка).
        if (_listGuard || !_printersLoaded || TargetA4Radio.IsChecked == true)
            return;
        if (PrinterCombo.SelectedItem is DiscoveredPrinter p && !p.IsVirtual
            && !LabelPrinterSelector.IsSameAsReceiptPrinter(p.DevicePath) // 2026-11-05 (аудит №3, С2-29): принтер чеков не запоминаем как принтер этикеток (после этого «одна кнопка» слала бы этикетки на чековый); в этом окне печать на нём остаётся доступной.
            && !LabelPrinterSelector.SamePrinter(p.DevicePath, UserPreferences.Instance.LabelPrinterDevicePath))
        {
            UserPreferences.Instance.LabelPrinterDevicePath = p.DevicePath;
            UserPreferences.Instance.SaveToDisk();
        }
    }

    private async void RefreshPrinterList()
    {
        var isA4 = TargetA4Radio.IsChecked == true;
        if (PrinterCombo.SelectedItem is DiscoveredPrinter prev)
        {
            if (_shownA4) _a4Choice = prev.DevicePath; else _thermalChoice = prev.DevicePath;
        }
        _shownA4 = isA4;
        _printersLoaded = false;
        var version = ++_listVersion;
        IReadOnlyList<DiscoveredPrinter> printers;
        try
        {
            printers = await Task.Run(() => isA4
                ? (IReadOnlyList<DiscoveredPrinter>)PriceTagService.GetAvailablePrinters().Where(p => !BarcodeLabelService.IsRawDevicePath(p.DevicePath)).ToList()
                : LabelPrinterSelector.GetChoices()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag printer list failed: {ex.GetType().Name}", "WARNING");
            printers = Array.Empty<DiscoveredPrinter>();
        }
        if (version != _listVersion)
            return; // за время поиска режим сменили — результат устарел
        _listGuard = true;
        PrinterCombo.ItemsSource = printers;
        var wanted = isA4 ? _a4Choice : _thermalChoice;
        var keep = wanted is null ? null : printers.FirstOrDefault(p => LabelPrinterSelector.SamePrinter(p.DevicePath, wanted));
        var resolution = isA4 ? null : LabelPrinterSelector.Resolve(printers);
        PrinterCombo.SelectedItem = isA4
            ? keep ?? LabelPrinterSelector.PickA4Default(printers, BarcodePrintSettingsStore.Load().A4PrinterName)
            : keep ?? resolution!.Printer;
        _listGuard = false;
        _printersLoaded = true;
        if (!isA4 && PrinterCombo.SelectedItem is null && printers.Count > 0)
            StatusText.Text = LabelPrinterSelector.HintText(resolution!.State);
        if (printers.Count == 0)
            StatusText.Text = isA4
                ? Tr.T("Не найдено ни одного установленного в Windows принтера.", "Windows'то орнотулган принтер табылган жок.", "No printers installed in Windows were found.", "Windows'ta yüklü hiçbir yazıcı bulunamadı.", "Windows'da o'rnatilgan printer topilmadi.")
                : Tr.T("Не найдено ни одного принтера.", "Принтер табылган жок.", "No printers found.", "Hiç yazıcı bulunamadı.", "Hech qanday printer topilmadi.");
    }

    /// <summary>2026-10-20, аудит программы владельца: ценник показывал «85.00 сом» (точка, как хранится PriceLine), а везде —
    /// запятая. На ценнике и в предпросмотре — с запятой; сам PriceLine (его разбирают другие места) не меняется.</summary>
    private string PriceDisplay =>
        System.Text.RegularExpressions.Regex.Replace(_product.PriceLine ?? "", @"(?<=\d)\.(?=\d)", ",");

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
            PriceText: PriceDisplay,
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
            // 2026-10-29: Android/Linux — System.Drawing недоступен, картинку рисует Avalonia (LabelSceneRenderer).
            PreviewImage.Source = option.Kind == PriceTagKind.Custom
                ? LabelSceneRenderer.LabelPreview(
                    _product.Title, _product.Barcode ?? "", PriceDisplay, _customTemplate,
                    sku: _product.Article, unit: _product.Unit, storeName: UserPreferences.Instance.StoreName)
                : LabelSceneRenderer.PriceTagPreview(option.Kind, BuildData(), widthMm, heightMm);

            // Штрих-код, который напечатается не как выбрано (Code 128 вместо EAN) или не
            // напечатается вовсе (кириллица, не влезает), — объясняем в строке состояния.
            var warning = option.Kind == PriceTagKind.Custom
                ? BarcodeLabelService.DescribeBarcodeProblem(_product.Barcode, _customTemplate)
                : PriceTagService.DescribeBarcodeProblem(option.Kind, _product.Barcode, widthMm, heightMm);
            if (warning is not null)
                StatusText.Text = warning;
            else if (_barcodeWarning is not null && StatusText.Text == _barcodeWarning)
                StatusText.Text = "";
            _barcodeWarning = warning;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag preview render failed: {ex}", "WARNING");
            StatusText.Text = Tr.T("Не удалось построить предпросмотр ценника.", "Баа белгисинин алдын ала көрүнүшүн түзүү мүмкүн болгон жок.", "Could not build the price tag preview.", "Fiyat etiketi önizlemesi oluşturulamadı.", "Narx yorlig'ini oldindan ko'rishni yaratib bo'lmadi.");
        }
    }

    /// <summary>Последнее показанное предупреждение о штрих-коде — чтобы убрать только его,
    /// не стирая другие сообщения (результат печати, «принтер не найден»).</summary>
    private string? _barcodeWarning;

    private async void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        if (KindCombo.SelectedItem is not PriceTagKindOption option)
            return;

        if (!BarcodePrintAccess.IsAllowed()) // 2026-11-01 (П-10)
        {
            StatusText.Text = BarcodePrintAccess.DeniedText;
            return;
        }
        if (!LabelPrinterSelector.PrintingSupported)
        {
            StatusText.Text = LabelPrinterSelector.UnsupportedText; // 2026-11-01 (С-9)
            return;
        }
        if (PrinterCombo.SelectedItem is null && !_printersLoaded)
        {
            StatusText.Text = Tr.T("Ищу принтеры… повторите печать через секунду.", "Принтерлер изделүүдө… бир секунддан кийин кайра басыңыз.", "Looking for printers… try printing again in a second.", "Yazıcılar aranıyor… bir saniye sonra tekrar deneyin.", "Printerlar qidirilmoqda… bir soniyadan keyin qayta urinib ko'ring.");
            return;
        }
        if (PrinterCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            StatusText.Text = TargetA4Radio.IsChecked == true
                ? Tr.T("Выберите принтер.", "Принтерди тандаңыз.", "Select a printer.", "Bir yazıcı seçin.", "Printerni tanlang.")
                : LabelPrinterSelector.MissingHint();
            return;
        }

        // 2026-10-22 (модалки, пакет 3): второй клик во время печати ничего не делает, окно не закрывается (IsBusy).
        if (!Shell.TryBeginBusy())
            return;
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
            // 2026-11-01 (В-2): принтер этикеток больше не запоминается по факту печати (лист A4 на офисный принтер портил настройку); в журнал — одна строка.
            if (result == LabelPrintResult.Success && target == PriceTagPrintTarget.Thermal)
                LabelPrinterSelector.LogSent(printer, printer.DevicePath, copies);
            StatusText.Text = result switch
            {
                LabelPrintResult.Success => LabelPrinterSelector.SentText(printer.Label),
                LabelPrintResult.PartiallySent => LabelPrinterSelector.PartialText(printer.DevicePath),
                LabelPrintResult.PrinterNotFound => Tr.T("Принтер не найден — обновите список.", "Принтер табылган жок — тизмени жаңыртыңыз.", "Printer not found — refresh the list.", "Yazıcı bulunamadı — listeyi yenileyin.", "Printer topilmadi — ro'yxatni yangilang."),
                _ => Tr.T("Ошибка печати. Подробности в журнале приложения.", "Басып чыгаруу катасы. Толук маалымат колдонмонун журналында.", "Printing error. See the app log for details.", "Yazdırma hatası. Ayrıntılar için uygulama günlüğüne bakın.", "Chop etishda xato. Batafsil ma'lumot ilova jurnalida."),
            };
        }
        finally
        {
            PrintButton.IsEnabled = true;
            Shell.EndBusy();
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
