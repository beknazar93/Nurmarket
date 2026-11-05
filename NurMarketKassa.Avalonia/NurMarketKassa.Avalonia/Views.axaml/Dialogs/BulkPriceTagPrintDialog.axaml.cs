using NurMarketKassa.Core.Application;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Services;
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
            new PriceTagKindOption(PriceTagKind.Detailed, Tr.T("Подробный (магазин, категория, штрих-код)", "Толук (дүкөн, категория, штрих-код)", "Detailed (store, category, barcode)", "Detaylı (mağaza, kategori, barkod)", "Batafsil (do'kon, kategoriya, shtrix-kod)")),
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

        PrinterCombo.SelectionChanged += PrinterCombo_SelectionChanged;
        CancelPrintButton.Content = Tr.T("Остановить печать", "Басууну токтотуу", "Stop printing", "Yazdırmayı durdur", "Chop etishni to'xtatish");
        RefreshPrinterList();
        UpdateSelectionCount();
        ApplyKindUi();
        UpdateEmptyText();
    }

    /// <summary>2026-10-26: из Склада и Маркетплейса теперь открывается единое окно «Печать штрихкодов и ценников»
    /// (<see cref="BarcodePrintWindow"/>, как на сайте); это окно с готовыми видами ценников доступно из него кнопкой «Виды ценников…».</summary>
    public static Task Open(Window? owner) => BarcodePrintWindow.Open(owner);

    /// <summary>Прежнее окно массовой печати (готовые виды ценников) — как было до 26.10.2026.</summary>
    public static async Task OpenLegacy(Window? owner)
    {
        if (!BarcodePrintAccess.Ensure(owner)) // 2026-11-01 (П-10): право «Печать штрих-кодов»
            return;
        // 2026-10-27 (аудит редакторов, Б-2): та же проверка покупки массовой печати, что и у нового окна.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return;
        var dialog = new BulkPriceTagPrintDialog();
        await dialog.ShowDialog(owner!).ConfigureAwait(true);
    }

    private void SearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        var query = (SearchBox.Text ?? "").Trim();
        // 2026-10-21: скан доп. штрихкода, 12 ↔ 13 цифр, размера или артикула — тот же товар, что нашла бы касса
        // (ProductCodeLookup; склад не находит товар по скану).
        var exactId = query.Length >= 4 ? ProductCodeLookup.FindLocal(query)?.Product.Id : null;
        ProductsGrid.ItemsSource = query.Length == 0
            ? _allRows
            : _allRows.Where(r =>
                SearchMatch.Matches(query, r.Title, r.Barcode) || // 2026-10-29: единый поиск — слова в любом порядке, ё=е, раскладка
                (exactId != null && string.Equals(r.Product.Id, exactId, StringComparison.OrdinalIgnoreCase))).ToList();
        UpdateEmptyText();
    }

    /// <summary>2026-10-20, этап О9б: пустой список — подсказка поверх таблицы: каталог ещё не загрузился или поиск ничего не нашёл.</summary>
    private void UpdateEmptyText()
    {
        var empty = ProductsGrid.ItemsSource is not System.Collections.ICollection { Count: > 0 };
        EmptyListText.IsVisible = empty;
        if (empty)
            EmptyListText.Text = (_allRows.Count == 0 ? this.FindResource("priceTagBulk.noProducts") : this.FindResource("priceTagBulk.notFound")) as string ?? "";
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in (IEnumerable<SelectionRow>)ProductsGrid.ItemsSource!)
            row.IsSelected = true;
        UpdateSelectionCount();
    }

    // 2026-10-20, проверка фильтров: «Снять» — как «Выбрать все», по найденным строкам (было: снимал и скрытые поиском).
    private void SelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in (IEnumerable<SelectionRow>)ProductsGrid.ItemsSource!)
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
            _customTemplate, Tr.T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"), "4870145004807", "285 сом",
            PriceTagTemplateStore.Save, Tr.T("Редактор ценника", "Баа белгиси редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorlig'i muharriri"));
        await editor.ShowDialog(this).ConfigureAwait(true);
        if (editor.Saved)
        {
            _customTemplate = PriceTagTemplateStore.Load();
            ApplyKindUi();
        }
    }

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
    }

    private void UpdateSelectionCount()
    {
        var count = _allRows.Count(r => r.IsSelected);
        var total = _allRows.Count;
        // 2026-10-20, проверка фильтров: «Выбрано N из M» — сколько отмечено из всех товаров (и скрытых поиском).
        SelectionCountText.Text = count == 0
            ? Tr.T("Товары не выбраны.", "Товарлар тандалган жок.", "No products selected.", "Ürün seçilmedi.", "Mahsulotlar tanlanmagan.")
            : Tr.T($"Выбрано {count} из {total}", $"{total} ичинен {count} тандалды", $"Selected {count} of {total}",
                $"{total} üründen {count} seçildi", $"{total} tadan {count} tanlandi");
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

        // 2026-11-01 (С-5): большая пачка — подтверждение с числом этикеток и оценкой ленты/листов.
        var totalLabels = selected.Count * (int)(CopiesBox.Value ?? 1m);
        if (totalLabels > LabelPrinterSelector.BigBatchLabels)
        {
            var bigQuestion = LabelPrinterSelector.BigBatchQuestion(totalLabels, (double)(HeightBox.Value ?? 30m), UserPreferences.Instance.LabelGapMm, 0, printer.Label);
            if (!await PosDialogs.ConfirmYesNoModalAsync(this, bigQuestion, Tr.T("Большая печать", "Чоң басуу", "Large print job", "Büyük baskı", "Katta chop etish"),
                    Tr.T($"Напечатать {totalLabels}", $"{totalLabels} басуу", $"Print {totalLabels}", $"{totalLabels} yazdır", $"{totalLabels} ta chop etish"),
                    Tr.T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true))
                return;
        }

        // 2026-10-22 (модалки, пакет 3): второй клик/Enter во время печати ничего не делает, окно не закрывается (IsBusy).
        if (!Shell.TryBeginBusy())
            return;
        PrintButton.IsEnabled = false;
        StatusText.Text = Tr.T($"Печать {selected.Count} ценников…", $"{selected.Count} баа белгиси басып чыгарылууда…",
            $"Printing {selected.Count} price tags…", $"{selected.Count} fiyat etiketi yazdırılıyor…", $"{selected.Count} ta narx yorlig'i chop etilmoqda…");
        var doneRows = new List<SelectionRow>();
        string? stoppedAt = null;
        var cancelled = false;
        var result = LabelPrintResult.Success;
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

            // 2026-11-01 (Н-4): цена с запятой, как в остальных окнах печати (BarcodePrintCatalog.LabelPrice).
            var dataList = selected.Select(row => new PriceTagData(
                ProductName: row.Product.Title,
                Barcode: row.Product.Barcode,
                PriceText: BarcodePrintCatalog.LabelPrice(row.Product),
                Sku: row.Product.Article,
                Unit: row.Product.Unit,
                Category: row.Product.Category ?? row.Product.Brand,
                StoreName: storeName)).ToList();

            if (isA4)
            {
                // Лист A4 — один документ на все этикетки: частичного результата и отмены нет.
                result = option.Kind == PriceTagKind.Custom
                    ? await Task.Run(() => PriceTagService.PrintBatchCustomTemplate(
                        dataList.Select(d => (d, copies)).ToList(), _customTemplate, printer.DevicePath, target)).ConfigureAwait(true)
                    : await Task.Run(() => PriceTagService.PrintBatch(
                        dataList.Select(d => (option.Kind, d, copies)).ToList(), widthMm, heightMm, printer.DevicePath, target)).ConfigureAwait(true);
                if (result == LabelPrintResult.Success)
                    doneRows.AddRange(selected);
            }
            else
            {
                // 2026-11-01 (С-6/В-3): термопечать — по товару, с ходом, отменой и учётом напечатанного.
                _printCts = new System.Threading.CancellationTokenSource();
                var ct = _printCts.Token;
                CancelPrintButton.IsVisible = true;
                CancelPrintButton.IsEnabled = true;
                var total = selected.Count;
                await Task.Run(() =>
                {
                    for (var i = 0; i < selected.Count; i++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            stoppedAt = selected[i].Product.Title;
                            break;
                        }
                        var data = dataList[i];
                        var r = option.Kind == PriceTagKind.Custom
                            ? PriceTagService.PrintBatchCustomTemplate(new[] { (data, copies) }, _customTemplate, printer.DevicePath, target)
                            : PriceTagService.Print(new PriceTagPrintRequest(option.Kind, data, widthMm, heightMm, copies, printer.DevicePath, target));
                        if (r != LabelPrintResult.Success)
                        {
                            result = r;
                            stoppedAt = selected[i].Product.Title;
                            break;
                        }
                        doneRows.Add(selected[i]);
                        var n = i + 1;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = Tr.T($"Печать… {n} из {total}", $"Басып жатат… {n}/{total}", $"Printing… {n} of {total}", $"Yazdırılıyor… {n}/{total}", $"Chop etilmoqda… {n}/{total}"));
                    }
                }).ConfigureAwait(true);
                if (doneRows.Count > 0)
                    LabelPrinterSelector.LogSent(printer, printer.DevicePath, doneRows.Count * copies);
            }

            // Товары, чей штрих-код напечатан иначе (Code 128 вместо EAN) или не напечатан
            // (кириллица, не влезает). Раньше один такой товар ронял всю массовую печать.
            var problemCount = selected.Count(row => (option.Kind == PriceTagKind.Custom
                ? BarcodeLabelService.DescribeBarcodeProblem(row.Product.Barcode, _customTemplate)
                : PriceTagService.DescribeBarcodeProblem(option.Kind, row.Product.Barcode, widthMm, heightMm)) is not null);
            var problemNote = problemCount == 0 ? "" : " " + Tr.T(
                $"Замечания по штрих-коду у {problemCount} товаров (неверный EAN — напечатан как Code 128, кириллица, слишком длинный или плотный код) — подробности в окне печати ценника этого товара.",
                $"{problemCount} товардын штрих-кодунда эскертүү бар (туура эмес EAN — Code 128 катары басылды, кирилл тамгалары, өтө узун же тыгыз код) — чоо-жайы ошол товардын баа белгисин басып чыгаруу терезесинде.",
                $"Barcode notes for {problemCount} products (invalid EAN printed as Code 128, Cyrillic, code too long or too dense) — see details in that product's price tag print window.",
                $"{problemCount} üründe barkod uyarısı var (geçersiz EAN Code 128 olarak basıldı, Kiril, çok uzun veya çok sık kod) — ayrıntılar o ürünün fiyat etiketi yazdırma penceresinde.",
                $"{problemCount} ta mahsulotda shtrix-kod bo'yicha eslatma bor (noto'g'ri EAN Code 128 sifatida chop etildi, kirill, juda uzun yoki zich kod) — tafsilotlar shu mahsulotning narx yorlig'ini chop etish oynasida.");

            // 2026-11-01 (Н-5): «Со штрих-кодом»/«Подробный»/QR — товары без штрих-кода печатаются без него; считаем их и пишем.
            var needsCode = option.Kind is PriceTagKind.WithBarcode or PriceTagKind.Detailed or PriceTagKind.WithQr
                || (option.Kind == PriceTagKind.Custom && _customTemplate.Barcode.Enabled);
            var noCode = needsCode ? selected.Count(row => string.IsNullOrWhiteSpace(row.Product.Barcode)) : 0;
            if (noCode > 0)
                problemNote += " " + Tr.T($"У {noCode} товаров нет штрих-кода — на их ценниках его нет.", $"{noCode} товардо штрих-код жок — алардын баа белгилеринде ал жок.",
                    $"{noCode} products have no barcode — their tags are printed without it.", $"{noCode} ürünün barkodu yok — etiketleri barkodsuz basıldı.", $"{noCode} ta mahsulotda shtrix-kod yo'q — ularning narx yorliqlarida u yo'q.");

            if (result != LabelPrintResult.Success || cancelled)
            {
                // 2026-11-01 (В-3): с напечатанных строк выбор снимается — повторное «Печать» не печатает их заново.
                foreach (var row in doneRows)
                    row.IsSelected = false;
                UpdateSelectionCount();
            }
            var partial = Tr.T($"Напечатано {doneRows.Count} из {selected.Count}, остановились на «{stoppedAt}». Выбор снят с напечатанных — повторное «Печать» допечатает остальные.",
                $"{doneRows.Count}/{selected.Count} басылды, «{stoppedAt}» боюнча токтоду. Басылгандардан тандоо алынды — кайра «Басуу» калгандарын басат.",
                $"Printed {doneRows.Count} of {selected.Count}, stopped at “{stoppedAt}”. Printed rows were deselected — pressing Print again prints only the rest.",
                $"{doneRows.Count}/{selected.Count} basıldı, «{stoppedAt}» üründe durdu. Yazdırılanların seçimi kaldırıldı — tekrar «Yazdır» kalanları yazdırır.",
                $"{doneRows.Count}/{selected.Count} chop etildi, «{stoppedAt}» da to'xtadi. Chop etilganlardan tanlov olib tashlandi — qayta «Chop etish» qolganlarini chop etadi.");
            StatusText.Text = cancelled
                ? Tr.T("Печать остановлена. ", "Басуу токтотулду. ", "Printing stopped. ", "Yazdırma durduruldu. ", "Chop etish to'xtatildi. ") + partial
                : result switch
                {
                    LabelPrintResult.Success => Tr.T($"Готово — отправлено на печать {selected.Count} ценников.", $"Даяр — {selected.Count} баа белгиси басып чыгарууга жөнөтүлдү.",
                        $"Done — {selected.Count} price tags sent to the printer.", $"Tamamlandı — {selected.Count} fiyat etiketi yazdırmaya gönderildi.",
                        $"Tayyor — {selected.Count} ta narx yorlig'i chop etishga yuborildi.") + " " + LabelPrinterSelector.SentText(printer.Label) + "." + problemNote,
                    LabelPrintResult.PrinterNotFound => Tr.T("Принтер не найден — обновите список.", "Принтер табылган жок — тизмени жаңыртыңыз.", "Printer not found — refresh the list.", "Yazıcı bulunamadı — listeyi yenileyin.", "Printer topilmadi — ro'yxatni yangilang."),
                    _ => (doneRows.Count > 0 || stoppedAt is not null
                            ? Tr.T("Ошибка печати. ", "Басуу катасы. ", "Printing error. ", "Yazdırma hatası. ", "Chop etishda xato. ") + partial
                            : Tr.T("Ошибка печати. Подробности в журнале приложения.", "Басып чыгаруу катасы. Толук маалымат колдонмонун журналында.", "Printing error. See the app log for details.", "Yazdırma hatası. Ayrıntılar için uygulama günlüğüne bakın.", "Chop etishda xato. Batafsil ma'lumot ilova jurnalida.")),
                };
        }
        finally
        {
            _printCts?.Dispose();
            _printCts = null;
            CancelPrintButton.IsVisible = false;
            PrintButton.IsEnabled = true;
            Shell.EndBusy();
        }
    }

    private System.Threading.CancellationTokenSource? _printCts;

    private void CancelPrint_Click(object? sender, RoutedEventArgs e)
    {
        _printCts?.Cancel();
        CancelPrintButton.IsEnabled = false;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
