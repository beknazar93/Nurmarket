using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-29, жалоба владельца «в добавлении товаров (ручном и массовом) нет цены, количества и единицы» и ТЗ «Склад — массовое добавление»
/// (26.10.2026, В-1: количество из файла ПРИБАВЛЯЕТСЯ — это приход): загрузка товаров из Excel (.xlsx), CSV и буфера обмена.
/// Файл → таблица (ProductFileReader: кодировка, разделитель, лист) → сопоставление столбцов («Цена», «Количество», «Единица (шт/кг/л…)», «Закупка», «Наценка»,
/// «Категория»; заголовки ru/ky/uz/en/tr запоминаются по поставщику) → проверка каждой строки (BulkImportPlan: единица по справочнику UnitCatalog, цена продажи
/// из файла / наценки / автонаценки приёмки, дробное количество только у дробных единиц) → таблица с правкой ячеек и действиями над отмеченными строками
/// → «Загрузить N»: строки уходят в фоновую очередь прихода частями по 100 (BulkReceivingFeeder, ключи повтора — как у приёмки), окно можно закрыть.
/// Окно денег само не проводит: проведение — PurchaseReceivingService.PostAsync через очередь. Старая «Массовая загрузка» (замена остатка CSV) —
/// по ссылке «Старая загрузка».</summary>
public sealed class BulkImportWizardWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Стенд проверки: не обновлять каталог с сервера перед проверкой файла.</summary>
    public static bool RefreshCatalogBeforeCheck { get; set; } = true;

    /// <summary>Стенд проверки: не спрашивать подтверждения «цена продажи изменится».</summary>
    public static bool SkipConfirmations { get; set; }

    private enum Filter { All, Receipt, New, Error }

    private ProductFileTable? _table;
    private ProductImportMapping _mapping = new();
    private BulkImportPlan? _plan;
    private string _fileName = "";
    private List<ImportRowVm> _rows = [];
    private Filter _filter = Filter.All;
    private bool _building;
    private bool _started;
    private string? _batchId;
    private CancellationTokenSource? _cts;

    private readonly Button _pickFile, _paste, _tplXlsx, _tplCsv, _oldImport;
    private readonly TextBlock _fileInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _fileWarn = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _mapPanel = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly WrapPanel _mapWrap = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<ProductImportField, ColumnChoice> _choices = new();
    private readonly TextBox _markupBox = new() { MinHeight = 44, Width = 90, MaxLength = 6, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _markupHint = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _defUnitCheck = new() { MinHeight = 44 };
    private readonly UnitPickerBox _defUnit = new() { Width = 190, SecondaryClass = "modal-secondary", PrimaryClass = "modal-primary" };
    private readonly RadioButton _dupSum = new() { GroupName = "BulkWizardDup", IsChecked = true, MinHeight = 44 };
    private readonly RadioButton _dupFirst = new() { GroupName = "BulkWizardDup", MinHeight = 44 };
    private readonly Button _checkBtn;
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal, IsVisible = false };
    private readonly Dictionary<Filter, Button> _tileButtons = new();
    private readonly Border _opsPanel = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _opsInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new();
    private readonly TextBlock _summary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly Button _errorsBtn, _loadBtn, _mapToggle, _opsToggle;
    private StackPanel? _top;

    public BulkImportWizardWindow()
    {
        Title = T("Массовая загрузка товаров", "Товарларды жаппай жүктөө", "Bulk product import", "Toplu ürün yükleme", "Mahsulotlarni ommaviy yuklash");
        SectionCards.AsModal(this, 1240);
        MinWidth = 640;

        _pickFile = SectionCards.Secondary(T("Выбрать файл…", "Файлды тандоо…", "Choose file…", "Dosya seç…", "Faylni tanlash…"));
        _paste = SectionCards.Secondary(T("Вставить из буфера", "Буферден коюу", "Paste from clipboard", "Panodan yapıştır", "Buferdan qo'yish"));
        _tplXlsx = SectionCards.Secondary(T("Шаблон Excel", "Excel үлгүсү", "Excel template", "Excel şablonu", "Excel shabloni"));
        _tplCsv = SectionCards.Secondary(T("Шаблон CSV", "CSV үлгүсү", "CSV template", "CSV şablonu", "CSV shabloni"));
        _oldImport = SectionCards.Ghost(T("Старая загрузка (замена остатка)", "Эски жүктөө (калдыкты алмаштыруу)", "Old import (replace stock)", "Eski yükleme (stok değiştirme)", "Eski yuklash (qoldiqni almashtirish)"));
        foreach (var b in new[] { _pickFile, _paste, _tplXlsx, _tplCsv, _oldImport })
            b.MinHeight = 44;
        _pickFile.Click += async (_, _) => await PickFileAsync().ConfigureAwait(true);
        _paste.Click += async (_, _) => await PasteAsync().ConfigureAwait(true);
        _tplXlsx.Click += async (_, _) => await SaveTemplateAsync(xlsx: true).ConfigureAwait(true);
        _tplCsv.Click += async (_, _) => await SaveTemplateAsync(xlsx: false).ConfigureAwait(true);
        _oldImport.Click += (_, _) => BulkImportWindow.Open(Owner as Window);
        ToolTip.SetTip(_oldImport, T("Прежнее окно: количество из CSV ЗАМЕНЯЕТ остаток товара (не прибавляется).",
            "Мурунку терезе: CSV'деги сан товардын калдыгын АЛМАШТЫРАТ (кошулбайт).",
            "The previous window: the quantity from CSV REPLACES the product stock (it is not added).",
            "Önceki pencere: CSV'deki miktar ürün stokunu DEĞİŞTİRİR (eklenmez).",
            "Avvalgi oyna: CSV dagi miqdor mahsulot qoldig'ini ALMASHTIRADI (qo'shilmaydi)."));

        _fileInfo.Bind(TextBlock.ForegroundProperty, _fileInfo.GetResourceObservable("BrushTextSoft"));
        _fileWarn.Bind(TextBlock.ForegroundProperty, _fileWarn.GetResourceObservable("BrushDanger"));
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushText"));
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        _opsInfo.Bind(TextBlock.ForegroundProperty, _opsInfo.GetResourceObservable("BrushTextSoft"));
        _markupHint.Bind(TextBlock.ForegroundProperty, _markupHint.GetResourceObservable("BrushTextSoft"));
        foreach (var panel in new[] { _mapPanel, _opsPanel })
        {
            panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("BrushPanelSoft"));
            panel.Bind(Border.BorderBrushProperty, panel.GetResourceObservable("BrushBorder"));
        }

        // ---- шаг 1: файл
        var step1 = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { _pickFile, _paste, _tplXlsx, _tplCsv, _oldImport })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            step1.Children.Add(c);
        }
        _fileInfo.Margin = new Thickness(0, 0, 8, 6);
        step1.Children.Add(_fileInfo); // сведения о файле — в той же строке, чтобы таблице осталось больше места

        // ---- шаг 2: столбцы и параметры
        var mapTitle = Caption(T("Какой столбец файла что означает", "Файлдын кайсы мамычасы эмнени билдирет", "Which file column means what", "Dosyanın hangi sütunu ne anlama geliyor", "Fayl ustuni nimani bildiradi"));
        _markupBox.Watermark = T("напр. 25", "мис. 25", "e.g. 25", "örn. 25", "masalan 25");
        _defUnitCheck.Content = T("Если единица пустая — ставить:", "Бирдик бош болсо — коюу:", "If the unit is empty, use:", "Birim boşsa şunu kullan:", "Birlik bo'sh bo'lsa — qo'yish:");
        _defUnit.InputBox.MinHeight = 44;
        _dupSum.Content = T("повтор штрихкода: сложить количество", "штрих-код кайталанса: санын кошуу", "repeated barcode: add up the quantity", "tekrarlanan barkod: miktarı topla", "takroriy shtrix-kod: miqdorni qo'shish");
        _dupFirst.Content = T("брать первую строку", "биринчи сапты алуу", "take the first row", "ilk satırı al", "birinchi qatorni olish");
        _checkBtn = SectionCards.Secondary(T("Проверить заново", "Кайра текшерүү", "Check again", "Yeniden kontrol et", "Qayta tekshirish"));
        _checkBtn.MinHeight = 44;
        _checkBtn.Click += async (_, _) => await CheckAsync().ConfigureAwait(true);

        var settings = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        void AddSetting(Control c)
        {
            c.Margin = new Thickness(0, 0, 18, 6);
            settings.Children.Add(c);
        }
        AddSetting(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Caption(T("Наценка для строк без цены продажи, %", "Сатуу баасы жок саптарга кошумча, %", "Markup for rows without a sale price, %", "Satış fiyatı olmayan satırlar için kâr, %", "Sotuv narxi yo'q qatorlar uchun ustama, %")), _markupBox, _markupHint },
        });
        AddSetting(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _defUnitCheck, _defUnit } });
        AddSetting(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dupSum, _dupFirst } });
        AddSetting(_checkBtn);
        _mapPanel.Child = new StackPanel { Spacing = 4, Children = { mapTitle, _mapWrap, settings } };

        // ---- шаг 3: итог, действия, таблица
        foreach (var (filter, _) in new[] { (Filter.All, 0), (Filter.Receipt, 0), (Filter.New, 0), (Filter.Error, 0) })
        {
            var tile = SectionCards.Secondary("");
            tile.MinHeight = 44;
            tile.Margin = new Thickness(0, 0, 8, 6);
            var f = filter;
            tile.Click += (_, _) =>
            {
                _filter = f;
                ApplyFilter();
            };
            _tileButtons[filter] = tile;
            _tiles.Children.Add(tile);
        }
        // Столбцы/параметры и действия над отмеченными сворачиваются: после проверки на экране главное — таблица строк.
        _mapToggle = SectionCards.Ghost("");
        _opsToggle = SectionCards.Ghost("");
        foreach (var toggle in new[] { _mapToggle, _opsToggle })
        {
            toggle.MinHeight = 44;
            toggle.Margin = new Thickness(0, 0, 8, 6);
            _tiles.Children.Add(toggle);
        }
        _mapToggle.Click += (_, _) =>
        {
            _mapPanel.IsVisible = !_mapPanel.IsVisible;
            UpdateToggles();
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        };
        _opsToggle.Click += (_, _) =>
        {
            _opsPanel.IsVisible = !_opsPanel.IsVisible;
            UpdateToggles();
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        };
        BuildOpsPanel();
        BuildGrid();
        UpdateToggles();
        _grid.Height = 330;
        _grid.IsVisible = false;

        var root = new StackPanel { Spacing = 2 };
        _top = root;
        root.Children.Add(step1);
        root.Children.Add(_fileWarn);
        root.Children.Add(_mapPanel);
        root.Children.Add(_status);
        root.Children.Add(_bar);
        root.Children.Add(_tiles);
        root.Children.Add(_opsPanel);
        root.Children.Add(_grid);

        // ---- низ окна
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        _errorsBtn = SectionCards.Secondary(T("Скачать строки с ошибками", "Катасы бар саптарды жүктөп алуу", "Download rows with errors", "Hatalı satırları indir", "Xatoli qatorlarni yuklab olish"));
        _errorsBtn.MinHeight = 44;
        _errorsBtn.IsEnabled = false;
        _errorsBtn.Click += async (_, _) => await SaveErrorsAsync().ConfigureAwait(true);
        _loadBtn = SectionCards.Primary(T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash"));
        _loadBtn.MinHeight = 44;
        _loadBtn.IsEnabled = false;
        _loadBtn.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        Content = SectionCards.Shell(Title ?? "", T("Excel, CSV или таблица из буфера. Количество из файла прибавляется к остатку (приход).",
                "Excel, CSV же буфердеги таблица. Файлдагы сан калдыкка кошулат (кирешe).",
                "Excel, CSV or a table from the clipboard. The quantity in the file is added to the stock (a receipt).",
                "Excel, CSV veya panodaki tablo. Dosyadaki miktar stoğa eklenir (mal kabul).",
                "Excel, CSV yoki buferdagi jadval. Fayldagi miqdor qoldiqqa qo'shiladi (kirim)."),
            root, SectionCards.Buttons(close, _errorsBtn, _loadBtn), _summary);

        Closed += (_, _) =>
        {
            _cts?.Cancel();
            BulkReceivingFeeder.Instance.BatchChanged -= OnBatchChanged;
            BulkReceivingFeeder.Instance.BatchFinished -= OnBatchFinished;
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        RefreshTexts();
    }

    public static void Open(Window? owner)
    {
        var window = new BulkImportWizardWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    // ------------------------------------------------------------------ для стенда проверки

    /// <summary>Итог проверки (null — файл ещё не проверен).</summary>
    public BulkImportPlan? Plan => _plan;

    /// <summary>Строки таблицы окна (для стенда).</summary>
    public int GridRowCount => _rows.Count;

    /// <summary>Подогнать высоту таблицы под окно (карточка не выше 85 % экрана); стенд зовёт после установки высоты.</summary>
    public void Refit()
    {
        if (Content is not ModalShell shell)
            return;
        var cap = double.IsInfinity(shell.MaxHeight) ? 800 : shell.MaxHeight;
        if (cap < 640 && shell.Subtitle is not null)
            shell.Subtitle = null; // 1024×600: подзаголовок уступает место таблице
        UpdateLayout();
        var topHeight = (_top?.Bounds.Height ?? 200) - (_grid.IsVisible ? _grid.Bounds.Height : 0);
        var chrome = 180; // шапка окна, подвал с кнопками, поля тени
        _grid.Height = Math.Clamp(cap - topHeight - chrome, 170, 520);
    }

    // ------------------------------------------------------------------ тексты и мелочи

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    private static TextBlock H(string text) => new()
    {
        Text = text, TextWrapping = text.Length > 9 ? TextWrapping.Wrap : TextWrapping.NoWrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxHeight = 34,
    };

    private void UpdateToggles()
    {
        _mapToggle.Content = (_mapPanel.IsVisible ? "▴ " : "▾ ") + T("Столбцы и параметры", "Мамычалар жана параметрлер", "Columns and options", "Sütunlar ve seçenekler", "Ustunlar va parametrlar");
        _opsToggle.Content = (_opsPanel.IsVisible ? "▴ " : "▾ ") + T("Действия над отмеченными", "Белгиленгендер үчүн аракеттер", "Actions on ticked rows", "İşaretlilerde işlemler", "Belgilanganlar ustida amallar");
    }

    private void RefreshTexts()
    {
        var auto = ReceivingMarkupSettingsStore.Current;
        _markupHint.Text = auto.AutoEnabled
            ? T($"пусто — по настройке приёмки ({auto.DefaultPercent:0.##} %)", $"бош — кабыл алуу жөндөөсү боюнча ({auto.DefaultPercent:0.##} %)",
                $"empty — by the receiving setting ({auto.DefaultPercent:0.##}%)", $"boşsa — mal kabul ayarına göre (%{auto.DefaultPercent:0.##})", $"bo'sh — qabul sozlamasi bo'yicha ({auto.DefaultPercent:0.##} %)")
            : T("пусто — без цены (строка с ошибкой)", "бош — баасыз (катасы бар сап)", "empty — no price (row with an error)", "boşsa — fiyatsız (hatalı satır)", "bo'sh — narxsiz (xatoli qator)");
        _status.Text = T("Выберите файл Excel или CSV — или вставьте таблицу из буфера. В файле нужны столбцы «Штрихкод» и «Количество»; для новых товаров ещё «Название», «Единица» (шт, кг, л…), «Закупка» и «Цена продажи» (или «Наценка»).",
            "Excel же CSV файлын тандаңыз — же буфердеги таблицаны коюңуз. Файлда «Штрих-код» жана «Саны» мамычалары керек; жаңы товарларга дагы «Аталышы», «Бирдик» (шт, кг, л…), «Сатып алуу баасы» жана «Сатуу баасы» (же «Үстөк»).",
            "Choose an Excel or CSV file — or paste a table from the clipboard. The file needs “Barcode” and “Quantity” columns; new products also need “Name”, “Unit” (pcs, kg, l…), “Purchase price” and “Sale price” (or “Markup”).",
            "Bir Excel veya CSV dosyası seçin — ya da panodan bir tablo yapıştırın. Dosyada «Barkod» ve «Miktar» sütunları gerekir; yeni ürünler için ayrıca «Ürün adı», «Birim» (adet, kg, l…), «Alış fiyatı» ve «Satış fiyatı» (veya «Kâr oranı»).",
            "Excel yoki CSV faylni tanlang — yoki buferdan jadval qo'ying. Faylda «Shtrix-kod» va «Miqdor» ustunlari kerak; yangi mahsulotlar uchun yana «Nomi», «Birlik» (dona, kg, l…), «Xarid narxi» va «Sotuv narxi» (yoki «Ustama»).");
        UpdateTiles();
    }

    // ------------------------------------------------------------------ шаг 1: файл

    private async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Выберите файл с товарами", "Товарлары бар файлды тандаңыз", "Choose a file with products", "Ürünlerin bulunduğu dosyayı seçin", "Mahsulotlar bor faylni tanlang"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Excel / CSV") { Patterns = ["*.xlsx", "*.csv", "*.txt"] },
            ],
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;
        await LoadPathAsync(path).ConfigureAwait(true);
    }

    private async Task PasteAsync()
    {
        string? text = null;
        try
        {
            if (Clipboard is { } clip)
                text = await clip.GetTextAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: буфер обмена не прочитан: {ex.Message}", "WARNING");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = T("В буфере обмена нет таблицы. Скопируйте ячейки в Excel (Ctrl+C) и нажмите «Вставить из буфера».",
                "Буферде таблица жок. Excel'де уячейкаларды көчүрүп (Ctrl+C), «Буферден коюу» басыңыз.",
                "There is no table in the clipboard. Copy the cells in Excel (Ctrl+C) and press “Paste from clipboard”.",
                "Panoda tablo yok. Excel'de hücreleri kopyalayın (Ctrl+C) ve «Panodan yapıştır»a basın.",
                "Buferda jadval yo'q. Excel'da kataklarni nusxalang (Ctrl+C) va «Buferdan qo'yish»ni bosing.");
            return;
        }
        await LoadResultAsync(ProductFileReader.ReadClipboard(text), T("из буфера обмена", "буферден", "from the clipboard", "panodan", "buferdan")).ConfigureAwait(true);
    }

    /// <summary>Прочитать файл и сразу проверить (стенд зовёт напрямую).</summary>
    public async Task LoadPathAsync(string path)
    {
        _status.Text = T("Читаю файл…", "Файл окулууда…", "Reading the file…", "Dosya okunuyor…", "Fayl o'qilmoqda…");
        var result = await Task.Run(() => ProductFileReader.ReadFile(path)).ConfigureAwait(true);
        await LoadResultAsync(result, Path.GetFileName(path)).ConfigureAwait(true);
    }

    private async Task LoadResultAsync(ProductFileReadResult result, string name)
    {
        if (_started)
            return;
        if (!result.Ok || result.Table is null)
        {
            _table = null;
            _plan = null;
            _fileInfo.Text = "";
            _fileWarn.IsVisible = true;
            _fileWarn.Text = result.Error ?? "";
            _status.Text = "";
            _mapPanel.IsVisible = false;
            ClearResult();
            return;
        }

        _table = result.Table;
        _fileName = name;
        _fileWarn.IsVisible = result.Table.Warnings.Count > 0;
        _fileWarn.Text = string.Join(" ", result.Table.Warnings);
        _mapping = ProductImportMapping.Resolve(_table.Headers);
        var sheet = _table.SheetName is { Length: > 0 } s ? T($" · лист «{s}»", $" · «{s}» барагы", $" · sheet “{s}”", $" · «{s}» sayfası", $" · «{s}» varag'i") : "";
        var enc = _table.EncodingName is { Length: > 0 } e ? $" · {e}" : "";
        _fileInfo.Text = T($"«{name}»{sheet}{enc} · строк: {_table.DataRowCount}, столбцов: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · саптар: {_table.DataRowCount}, мамычалар: {_table.ColumnCount}",
            $"“{name}”{sheet}{enc} · rows: {_table.DataRowCount}, columns: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · satır: {_table.DataRowCount}, sütun: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · qatorlar: {_table.DataRowCount}, ustunlar: {_table.ColumnCount}");
        BuildMappingChoices();
        _mapPanel.IsVisible = true;
        await CheckAsync().ConfigureAwait(true);
    }

    private async Task SaveTemplateAsync(bool xlsx)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = xlsx ? T("Сохранить шаблон Excel", "Excel үлгүсүн сактоо", "Save Excel template", "Excel şablonunu kaydet", "Excel shablonini saqlash")
                : T("Сохранить шаблон CSV", "CSV үлгүсүн сактоо", "Save CSV template", "CSV şablonunu kaydet", "CSV shablonini saqlash"),
            SuggestedFileName = xlsx ? "products_template.xlsx" : "products_template.csv",
            FileTypeChoices = [xlsx ? new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] } : new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            if (xlsx)
                ProductImportTemplate.SaveXlsx(path);
            else
                ProductImportTemplate.SaveCsv(path);
            _status.Text = T($"Шаблон сохранён: {path}", $"Үлгү сакталды: {path}", $"Template saved: {path}", $"Şablon kaydedildi: {path}", $"Shablon saqlandi: {path}");
        }
        catch (Exception ex)
        {
            _status.Text = T($"Не удалось сохранить шаблон: {ex.Message}", $"Үлгүнү сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the template: {ex.Message}",
                $"Şablon kaydedilemedi: {ex.Message}", $"Shablonni saqlab bo'lmadi: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ шаг 2: столбцы

    private static readonly ProductImportField[] MappedFields =
    [
        ProductImportField.Barcode, ProductImportField.Name, ProductImportField.Quantity, ProductImportField.Unit, ProductImportField.Purchase,
        ProductImportField.Markup, ProductImportField.Sale, ProductImportField.Category, ProductImportField.Weight,
    ];

    private static bool IsRequired(ProductImportField f) => f is ProductImportField.Barcode or ProductImportField.Quantity;

    private static string ColumnLetter(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private void BuildMappingChoices()
    {
        _mapWrap.Children.Clear();
        _choices.Clear();
        if (_table is null)
            return;
        var headers = _table.Headers;
        var items = new List<string>();
        for (var i = 0; i < headers.Count; i++)
            items.Add(string.IsNullOrWhiteSpace(headers[i]) ? ColumnLetter(i) : $"{ColumnLetter(i)}: {headers[i].Trim()}");
        var sample = _table.DataRows.FirstOrDefault();
        foreach (var field in MappedFields)
        {
            var choice = new ColumnChoice(items, T("— не загружать —", "— жүктөбөө —", "— do not load —", "— yükleme —", "— yuklamaslik —"))
            {
                Width = 230,
                Selected = _mapping.ColumnOf(field),
            };
            var f = field;
            choice.SelectedChanged += col =>
            {
                _mapping.Set(f, col);
                SyncChoices();
            };
            _choices[field] = choice;
            var label = ProductImportMapping.FieldLabel(field) + (IsRequired(field) ? " *" : "");
            var caption = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 2) };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));
            var stack = new StackPanel { Margin = new Thickness(0, 6, 12, 4), Children = { caption, choice } };
            _mapWrap.Children.Add(stack);
        }
        _ = sample;
    }

    private void SyncChoices()
    {
        foreach (var (field, choice) in _choices)
            choice.Selected = _mapping.ColumnOf(field);
    }

    // ------------------------------------------------------------------ шаг 3: проверка

    private BulkImportSettings ReadSettings()
    {
        double? markup = null;
        if (CheckoutValidation.TryParseAmount(_markupBox.Text, out var m, 2) == CheckoutValidation.AmountParse.Ok && m >= 0)
            markup = m;
        return new BulkImportSettings
        {
            DefaultMarkupPercent = markup,
            Duplicates = _dupFirst.IsChecked == true ? BulkImportDuplicateMode.FirstRow : BulkImportDuplicateMode.SumQuantity,
            DefaultUnit = _defUnitCheck.IsChecked == true ? _defUnit.Value : null,
            UseReceivingMarkup = true,
        };
    }

    /// <summary>Проверить файл заново (после смены столбцов или параметров). Вызывается и автоматически после выбора файла.</summary>
    public async Task CheckAsync()
    {
        if (_table is null || _building || _started)
            return;
        _building = true;
        _checkBtn.IsEnabled = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var total = _table.DataRowCount;
            if (RefreshCatalogBeforeCheck && BulkImportPlan.CatalogNeedsRefresh)
            {
                _status.Text = T("Обновляю каталог товаров…", "Товарлар каталогу жаңыртылууда…", "Refreshing the product catalog…", "Ürün kataloğu güncelleniyor…", "Mahsulotlar katalogi yangilanmoqda…");
                try
                {
                    await CatalogCacheService.RefreshFromApiAsync().WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    PosLogger.Log($"Массовая загрузка: каталог перед проверкой не обновлён: {ex.Message}", "WARNING");
                }
            }

            _bar.IsVisible = total > 500;
            _bar.Maximum = Math.Max(1, total);
            var progress = new Progress<int>(n =>
            {
                _bar.Value = n;
                _status.Text = T($"Проверяю {n} из {total}…", $"Текшерилүүдө {n} / {total}…", $"Checking {n} of {total}…", $"Kontrol ediliyor: {n} / {total}…", $"Tekshirilmoqda: {n} / {total}…");
            });
            var plan = await BulkImportPlan.BuildAsync(_table, _mapping, ReadSettings(), null, progress, token).ConfigureAwait(true);
            _bar.IsVisible = false;
            _plan = plan;
            if (plan.Problem is { } problem)
            {
                _status.Text = problem;
                ClearResult();
                return;
            }
            BuildRows();
            _status.Text = "";
            // Столбцы сопоставлены и проверка прошла — панель столбцов сворачивается, остаётся таблица.
            _mapPanel.IsVisible = false;
            _opsPanel.IsVisible = false;
            UpdateToggles();
        }
        catch (OperationCanceledException)
        {
            // окно закрыли или запустили проверку заново
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: проверка файла не удалась: {ex}", "WAREHOUSE");
            _status.Text = T($"Не удалось проверить файл: {ex.Message}", $"Файлды текшерүү мүмкүн болгон жок: {ex.Message}", $"Could not check the file: {ex.Message}",
                $"Dosya kontrol edilemedi: {ex.Message}", $"Faylni tekshirib bo'lmadi: {ex.Message}");
        }
        finally
        {
            _building = false;
            _checkBtn.IsEnabled = true;
        }
    }

    private void ClearResult()
    {
        _rows = [];
        _grid.ItemsSource = null;
        _grid.IsVisible = false;
        _tiles.IsVisible = false;
        _opsPanel.IsVisible = false;
        _loadBtn.IsEnabled = false;
        _errorsBtn.IsEnabled = false;
        _summary.Text = "";
    }

    private void BuildRows()
    {
        if (_plan is null)
            return;
        var m = _mapping;
        var cols = (Barcode: m.ColumnOf(ProductImportField.Barcode), Name: m.ColumnOf(ProductImportField.Name), Qty: m.ColumnOf(ProductImportField.Quantity),
            Purchase: m.ColumnOf(ProductImportField.Purchase), Sale: m.ColumnOf(ProductImportField.Sale), Markup: m.ColumnOf(ProductImportField.Markup), Unit: m.ColumnOf(ProductImportField.Unit));
        _rows = _plan.Rows.Select(r => new ImportRowVm(r, cols.Barcode, cols.Name, cols.Qty, cols.Purchase, cols.Sale, cols.Markup, cols.Unit)).ToList();
        _checkedCount = 0;
        foreach (var vm in _rows)
            vm.PropertyChanged += OnRowChanged;
        _grid.IsVisible = true;
        _tiles.IsVisible = true;
        ApplyFilter();
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
    }

    private int _checkedCount;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportRowVm.IsChecked) || sender is not ImportRowVm vm)
            return;
        _checkedCount += vm.IsChecked ? 1 : -1; // счётчик, а не пересчёт всех строк: «Все» на 15 000 строк не должно виснуть
        UpdateOpsInfo();
    }

    private void ApplyFilter()
    {
        IEnumerable<ImportRowVm> list = _rows;
        list = _filter switch
        {
            Filter.Receipt => list.Where(r => r.Row.Kind == BulkImportRowKind.Receipt),
            Filter.New => list.Where(r => r.Row.Kind == BulkImportRowKind.New),
            Filter.Error => list.Where(r => r.Row.Kind == BulkImportRowKind.Error),
            _ => list,
        };
        _grid.ItemsSource = list.ToList();
        UpdateTiles();
    }

    private void UpdateTiles()
    {
        var plan = _plan;
        int all = plan?.Rows.Count ?? 0, receipt = plan?.ReceiptCount ?? 0, created = plan?.NewCount ?? 0, errors = plan?.ErrorCount ?? 0;
        string Mark(Filter f, string text) => _filter == f ? "● " + text : text;
        _tileButtons[Filter.All].Content = Mark(Filter.All, T($"Все {all}", $"Баары {all}", $"All {all}", $"Tümü {all}", $"Hammasi {all}"));
        _tileButtons[Filter.Receipt].Content = Mark(Filter.Receipt, T($"Приход к товарам склада {receipt}", $"Кампадагы товарларга кирешe {receipt}", $"Receipt to stock products {receipt}",
            $"Depo ürünlerine giriş {receipt}", $"Ombor mahsulotlariga kirim {receipt}"));
        _tileButtons[Filter.New].Content = Mark(Filter.New, T($"Новые {created}", $"Жаңылар {created}", $"New {created}", $"Yeni {created}", $"Yangilar {created}"));
        _tileButtons[Filter.Error].Content = Mark(Filter.Error, T($"Ошибки {errors}", $"Каталар {errors}", $"Errors {errors}", $"Hatalar {errors}", $"Xatolar {errors}"));
        var ready = plan?.ReadyCount ?? 0;
        _summary.Text = plan is null || plan.Rows.Count == 0
            ? ""
            : T($"Строк готово к добавлению: {ready}, с ошибками: {errors}" + (plan.MergedCount > 0 ? $", повторов сложено: {plan.MergedCount}" : ""),
                $"Кошууга даяр саптар: {ready}, каталуу: {errors}" + (plan.MergedCount > 0 ? $", кайталоолор кошулду: {plan.MergedCount}" : ""),
                $"Rows ready to add: {ready}, with errors: {errors}" + (plan.MergedCount > 0 ? $", repeats added up: {plan.MergedCount}" : ""),
                $"Eklemeye hazır satır: {ready}, hatalı: {errors}" + (plan.MergedCount > 0 ? $", birleştirilen tekrarlar: {plan.MergedCount}" : ""),
                $"Qo'shishga tayyor qatorlar: {ready}, xatoli: {errors}" + (plan.MergedCount > 0 ? $", qo'shilgan takrorlar: {plan.MergedCount}" : ""));
        _loadBtn.Content = ready > 0 ? T($"Загрузить {ready}", $"Жүктөө {ready}", $"Load {ready}", $"Yükle {ready}", $"Yuklash {ready}") : T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash");
        _loadBtn.IsEnabled = ready > 0 && !_started;
        _errorsBtn.IsEnabled = errors > 0;
    }

    // ------------------------------------------------------------------ таблица и правки

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush brush ? brush : null;

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.CanUserSortColumns = false;
        _grid.CanUserReorderColumns = false;
        _grid.RowHeight = 46;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _grid.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = H(""), Width = new DataGridLength(46), Binding = new Binding(nameof(ImportRowVm.IsChecked)) { Mode = BindingMode.TwoWay } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H("№"), Width = new DataGridLength(56), IsReadOnly = true, Binding = new Binding(nameof(ImportRowVm.FileRow)) });
        _grid.Columns.Add(Column(T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), 150, nameof(ImportRowVm.Barcode), ProductImportField.Barcode, readOnly: true));
        _grid.Columns.Add(Column(T("Название", "Аталышы", "Name", "Ad", "Nomi"), 220, nameof(ImportRowVm.Name), ProductImportField.Name, star: true));
        _grid.Columns.Add(Column(T("Единица", "Бирдик", "Unit", "Birim", "Birlik"), 104, nameof(ImportRowVm.UnitText), ProductImportField.Unit));
        _grid.Columns.Add(Column(T("Кол-во", "Саны", "Qty", "Miktar", "Soni"), 90, nameof(ImportRowVm.QuantityText), ProductImportField.Quantity, comma: true));
        _grid.Columns.Add(Column(T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"), 100, nameof(ImportRowVm.PurchaseText), ProductImportField.Purchase, comma: true));
        _grid.Columns.Add(Column(T("Наценка %", "Үстөк %", "Markup %", "Kâr %", "Ustama %"), 112, nameof(ImportRowVm.MarkupText), ProductImportField.Markup, comma: true));
        _grid.Columns.Add(Column(T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), 112, nameof(ImportRowVm.SaleText), ProductImportField.Sale, comma: true));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = H(T("Что будет / ошибка", "Эмне болот / ката", "What happens / error", "Ne olacak / hata", "Nima bo'ladi / xato")),
            Width = new DataGridLength(260), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<ImportRowVm>((_, _) => new RowCell(this, vm => vm.StatusText, null, status: true), supportsRecycling: false),
        });

        _grid.BeginningEdit += (_, e) =>
        {
            if (e.Row.DataContext is not ImportRowVm vm || _started)
            {
                e.Cancel = true;
                return;
            }
            var header = (e.Column.Header as TextBlock)?.Text;
            // Править можно только строки, у которых есть строка прихода; название и единицу — только у новых товаров (у товара склада они в его карточке).
            if (vm.Row.Line is null || vm.Row.Kind == BulkImportRowKind.Merged
                || ((e.Column.Tag as string is nameof(ImportRowVm.Name) or nameof(ImportRowVm.UnitText)) && !vm.Row.Line.IsNew))
                e.Cancel = true;
            _ = header;
        };
        _grid.CellEditEnded += (_, e) =>
        {
            if (e.Row.DataContext is ImportRowVm vm)
                Dispatcher.UIThread.Post(() => AfterEdit(vm), DispatcherPriority.Background);
        };
    }

    private DataGridColumn Column(string header, double width, string path, ProductImportField field, bool readOnly = false, bool comma = false, bool star = false)
    {
        return new DataGridTemplateColumn
        {
            Header = H(header),
            Tag = path,
            IsReadOnly = readOnly,
            Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            MinWidth = star ? 160 : width,
            CellTemplate = new FuncDataTemplate<ImportRowVm>((_, _) => new RowCell(this, vm => vm.Get(path), field, status: false), supportsRecycling: false),
            CellEditingTemplate = new FuncDataTemplate<ImportRowVm>((_, _) =>
            {
                var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 40 };
                var binding = new Binding(path) { Mode = BindingMode.TwoWay };
                if (comma)
                    binding.Converter = WarehouseWindow.CommaDecimal;
                box.Bind(TextBox.TextProperty, binding);
                return box;
            }, supportsRecycling: false),
        };
    }

    /// <summary>После правки ячейки: строки заново проверяются (BulkImportPlan.Recompute), плитки и кнопка «Загрузить N» обновляются.</summary>
    private void AfterEdit(ImportRowVm vm)
    {
        if (_plan is null)
            return;
        _plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        if (_filter != Filter.All && !MatchesFilter(vm))
            ApplyFilter();
    }

    private bool MatchesFilter(ImportRowVm vm) => _filter switch
    {
        Filter.Receipt => vm.Row.Kind == BulkImportRowKind.Receipt,
        Filter.New => vm.Row.Kind == BulkImportRowKind.New,
        Filter.Error => vm.Row.Kind == BulkImportRowKind.Error,
        _ => true,
    };

    // ------------------------------------------------------------------ действия над отмеченными строками

    private UnitPickerBox _opsUnit = null!;
    private TextBox _opsMarkup = null!, _opsPrice = null!, _opsQty = null!;

    private void BuildOpsPanel()
    {
        var all = new CheckBox { Content = T("Все", "Баары", "All", "Tümü", "Hammasi"), MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        all.IsCheckedChanged += (_, _) =>
        {
            foreach (var r in (_grid.ItemsSource as IEnumerable<ImportRowVm>) ?? _rows)
                r.IsChecked = all.IsChecked == true;
        };
        _opsUnit = new UnitPickerBox { Width = 190, SecondaryClass = "modal-secondary", PrimaryClass = "modal-primary" };
        var unitApply = SectionCards.Secondary(T("Установить единицу", "Бирдикти коюу", "Set unit", "Birimi ayarla", "Birlikni qo'yish"));
        unitApply.Click += (_, _) => ApplyToChecked("unit", _opsUnit.Value);
        _opsMarkup = new TextBox { MinHeight = 44, Width = 100, Watermark = T("Наценка, %", "Үстөк, %", "Markup, %", "Kâr, %", "Ustama, %"), VerticalContentAlignment = VerticalAlignment.Center };
        var markupApply = SectionCards.Secondary(T("Установить наценку", "Үстөктү коюу", "Set markup", "Kârı ayarla", "Ustamani qo'yish"));
        markupApply.Click += (_, _) => ApplyToChecked("markup", _opsMarkup.Text ?? "");
        _opsPrice = new TextBox { MinHeight = 44, Width = 120, Watermark = T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), VerticalContentAlignment = VerticalAlignment.Center };
        var priceApply = SectionCards.Secondary(T("Установить цену", "Баа коюу", "Set price", "Fiyatı ayarla", "Narxni qo'yish"));
        priceApply.Click += (_, _) => ApplyToChecked("price", _opsPrice.Text ?? "");
        _opsQty = new TextBox { MinHeight = 44, Width = 100, Watermark = T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), VerticalContentAlignment = VerticalAlignment.Center };
        var qtyApply = SectionCards.Secondary(T("Установить количество", "Санды коюу", "Set quantity", "Miktarı ayarla", "Miqdorni qo'yish"));
        qtyApply.Click += (_, _) => ApplyToChecked("qty", _opsQty.Text ?? "");
        foreach (var b in new[] { unitApply, markupApply, priceApply, qtyApply })
            b.MinHeight = 44;

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { all, Caption(T("Для отмеченных строк:", "Белгиленген саптар үчүн:", "For the ticked rows:", "İşaretli satırlar için:", "Belgilangan qatorlar uchun:")),
                     _opsUnit, unitApply, _opsMarkup, markupApply, _opsPrice, priceApply, _opsQty, qtyApply })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            wrap.Children.Add(c);
        }
        _opsPanel.Child = new StackPanel { Spacing = 2, Children = { wrap, _opsInfo } };
        UpdateOpsInfo();
    }

    private void UpdateOpsInfo()
    {
        var n = _checkedCount;
        _opsInfo.Text = n == 0
            ? T("Отметьте строки галочками в первом столбце — и нажмите «Установить …». Ячейки таблицы можно править двойным щелчком.",
                "Биринчи мамычада саптарды белгилеңиз — «… коюу» басыңыз. Таблицанын уячейкаларын эки жолу чыкылдатып оңдоого болот.",
                "Tick rows in the first column, then press “Set …”. Table cells can be edited with a double click.",
                "İlk sütunda satırları işaretleyin ve «… ayarla»ya basın. Tablo hücreleri çift tıklayarak düzenlenir.",
                "Birinchi ustunda qatorlarni belgilang va «… qo'yish»ni bosing. Jadval kataklarini ikki marta bosib tahrirlash mumkin.")
            : T($"Отмечено строк: {n}.", $"Белгиленген саптар: {n}.", $"Rows ticked: {n}.", $"İşaretli satır: {n}.", $"Belgilangan qatorlar: {n}.");
    }

    private void ApplyToChecked(string what, string text)
    {
        if (_plan is null || _started)
            return;
        var rows = _rows.Where(r => r.IsChecked && r.Row.Line is not null && r.Row.Kind != BulkImportRowKind.Merged).ToList();
        if (rows.Count == 0)
        {
            _opsInfo.Text = T("Сначала отметьте строки (с готовой строкой прихода) галочками.", "Адегенде саптарды белгилеңиз.", "Tick some rows first.", "Önce satırları işaretleyin.", "Avval qatorlarni belgilang.");
            return;
        }
        double number = 0;
        if (what != "unit")
        {
            var decimals = what == "qty" ? 3 : 2;
            var parse = CheckoutValidation.TryParseAmount(text, out number, decimals);
            if (parse != CheckoutValidation.AmountParse.Ok || (what != "markup" && number <= 0))
            {
                _opsInfo.Text = parse == CheckoutValidation.AmountParse.Empty || (parse == CheckoutValidation.AmountParse.Ok && number <= 0)
                    ? T("Впишите число больше нуля.", "Нөлдөн чоң сан жазыңыз.", "Enter a number greater than zero.", "Sıfırdan büyük bir sayı girin.", "Noldan katta son kiriting.")
                    : CheckoutValidation.AmountProblemText(parse, decimals) ?? "";
                return;
            }
        }

        int ok = 0, skipped = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var vm in rows)
        {
            var line = vm.Row.Line!;
            switch (what)
            {
                case "unit":
                    if (vm.Row.SetUnit(text))
                        ok++;
                    else
                        skipped++;
                    break;
                case "markup":
                    if (line.PurchasePrice > 0)
                    {
                        line.MarkupText = number.ToString("0.##", inv);
                        ok++;
                    }
                    else
                        skipped++;
                    break;
                case "price":
                    line.SetSaleText(number.ToString("0.##", inv));
                    ok++;
                    break;
                default:
                    line.QuantityText = number.ToString("0.###", inv);
                    ok++;
                    break;
            }
        }
        _plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        ApplyFilterIfNeeded();
        var skipText = skipped > 0
            ? T($", пропущено {skipped} ({(what == "unit" ? "товары склада" : "нет закупки")})", $", {skipped} өткөрүлдү", $", skipped {skipped}", $", {skipped} atlandı", $", {skipped} o'tkazildi")
            : "";
        _opsInfo.Text = T($"Готово: изменено {ok} строк{skipText}.", $"Даяр: {ok} сап өзгөрдү{skipText}.", $"Done: {ok} rows changed{skipText}.", $"Tamam: {ok} satır değişti{skipText}.", $"Tayyor: {ok} qator o'zgardi{skipText}.");
    }

    private void ApplyFilterIfNeeded()
    {
        if (_filter != Filter.All)
            ApplyFilter();
    }

    // ------------------------------------------------------------------ скачать ошибки и загрузить

    private async Task SaveErrorsAsync()
    {
        if (_plan is null || _plan.ErrorCount == 0)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Сохранить строки с ошибками", "Катасы бар саптарды сактоо", "Save rows with errors", "Hatalı satırları kaydet", "Xatoli qatorlarni saqlash"),
            SuggestedFileName = "import_errors.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            await File.WriteAllBytesAsync(path, ProductImportTemplate.CsvBytes(_plan.BuildErrorsCsv())).ConfigureAwait(true);
            _status.Text = T($"Строки с ошибками сохранены: {path}. Исправьте их и загрузите файл снова.", $"Катасы бар саптар сакталды: {path}. Оңдоп, файлды кайра жүктөңүз.",
                $"Rows with errors saved: {path}. Fix them and load the file again.", $"Hatalı satırlar kaydedildi: {path}. Düzeltip dosyayı yeniden yükleyin.", $"Xatoli qatorlar saqlandi: {path}. Tuzatib, faylni qayta yuklang.");
        }
        catch (Exception ex)
        {
            _status.Text = T($"Не удалось сохранить: {ex.Message}", $"Сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save: {ex.Message}", $"Kaydedilemedi: {ex.Message}", $"Saqlab bo'lmadi: {ex.Message}");
        }
    }

    private async Task LoadAsync()
    {
        if (_plan is null || _started || _plan.Lines.Count == 0)
            return;
        var feeder = BulkReceivingFeeder.Instance;
        if (feeder.IsClosed)
        {
            _status.Text = feeder.ClosedText;
            return;
        }

        // Цена продажи у товаров склада изменится — спрашиваем (как «Провести» в приёмке).
        var changes = _plan.SalePriceChangeCount;
        if (changes > 0 && !SkipConfirmations)
        {
            var go = await PosConfirmDialog.ShowModalAsync(this,
                T("Цена продажи изменится", "Сатуу баасы өзгөрөт", "Sale prices will change", "Satış fiyatları değişecek", "Sotuv narxlari o'zgaradi"),
                T($"Цена продажи изменится у {changes} товаров склада. Загрузить?", $"Кампадагы {changes} товардын сатуу баасы өзгөрөт. Жүктөйбүзбү?",
                    $"The sale price will change for {changes} stock products. Load?", $"{changes} depo ürününün satış fiyatı değişecek. Yüklensin mi?", $"Ombordagi {changes} mahsulotning sotuv narxi o'zgaradi. Yuklaymizmi?"),
                T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash"), T("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga")).ConfigureAwait(true);
            if (!go)
                return;
        }

        try
        {
            feeder.BatchChanged += OnBatchChanged;
            feeder.BatchFinished += OnBatchFinished;
            _batchId = feeder.Start(_plan.Lines, T($"Импорт: {_fileName}", $"Импорт: {_fileName}", $"Import: {_fileName}", $"İçe aktarma: {_fileName}", $"Import: {_fileName}"), _plan.FileRows);
        }
        catch (Exception ex)
        {
            feeder.BatchChanged -= OnBatchChanged;
            feeder.BatchFinished -= OnBatchFinished;
            _status.Text = ex.Message;
            return;
        }

        _started = true;
        _loadBtn.IsEnabled = false;
        _checkBtn.IsEnabled = false;
        _grid.IsReadOnly = true;
        if (_table is not null)
            _mapping.Remember(_table.Headers); // тот же поставщик в следующий раз — без ручного выбора столбцов
        _status.Text = T($"Загрузка в фоне: 0 из {_plan.Lines.Count} · можно закрыть окно — загрузка продолжится.",
            $"Фондо жүктөө: 0 / {_plan.Lines.Count} · терезени жабууга болот — жүктөө уланат.",
            $"Loading in the background: 0 of {_plan.Lines.Count} · you can close the window, loading continues.",
            $"Arka planda yükleniyor: 0 / {_plan.Lines.Count} · pencereyi kapatabilirsiniz, yükleme sürer.",
            $"Fonda yuklanmoqda: 0 / {_plan.Lines.Count} · oynani yopish mumkin, yuklash davom etadi.");
        _bar.IsVisible = true;
        _bar.Maximum = Math.Max(1, _plan.Lines.Count);
        _bar.Value = 0;
    }

    private void OnBatchChanged(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.Maximum = Math.Max(1, info.TotalLines);
            _bar.Value = info.ProcessedLines;
            _status.Text = T($"Загрузка в фоне: {info.ProcessedLines} из {info.TotalLines} · {info.SummaryText}", $"Фондо жүктөө: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Loading in the background: {info.ProcessedLines} of {info.TotalLines} · {info.SummaryText}", $"Arka planda yükleniyor: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Fonda yuklanmoqda: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}");
        });
    }

    private void OnBatchFinished(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.IsVisible = false;
            _status.Text = T($"Готово: {info.SummaryText}. Строки, которые не приняты, — в «Приходах в фоне» (можно повторить).", $"Даяр: {info.SummaryText}. Кабыл алынбаган саптар — «Фондогу кирешелерде» (кайталоого болот).",
                $"Done: {info.SummaryText}. Rows that were not accepted are in “Background receipts” (you can retry them).", $"Tamam: {info.SummaryText}. Kabul edilmeyen satırlar «Arka plan girişleri»nde (tekrar denenebilir).",
                $"Tayyor: {info.SummaryText}. Qabul qilinmagan qatorlar «Fondagi kirimlar»da (qayta urinish mumkin).");
        });
    }

    // ------------------------------------------------------------------ вспомогательные типы

    /// <summary>Строка таблицы окна: оболочка над строкой плана с текстами для ячеек и правкой через строку прихода (ReceivingLineVm).</summary>
    internal sealed class ImportRowVm : INotifyPropertyChanged
    {
        private readonly int _cBarcode, _cName, _cQty, _cPurchase, _cSale, _cMarkup, _cUnit;
        private bool _checked;

        public ImportRowVm(BulkImportRow row, int cBarcode, int cName, int cQty, int cPurchase, int cSale, int cMarkup, int cUnit)
        {
            Row = row;
            (_cBarcode, _cName, _cQty, _cPurchase, _cSale, _cMarkup, _cUnit) = (cBarcode, cName, cQty, cPurchase, cSale, cMarkup, cUnit);
        }

        public BulkImportRow Row { get; }

        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                Raise(nameof(IsChecked));
            }
        }

        public int FileRow => Row.FileRow;

        private string Raw(int column) => column >= 0 && column < Row.SourceCells.Length ? Row.SourceCells[column] : "";

        public string Barcode => Row.Barcode.Length > 0 ? Row.Barcode : Raw(_cBarcode);

        public string Name
        {
            get => Row.Line?.ProductName ?? Row.Name;
            set
            {
                if (Row.Line is { IsNew: true } line)
                    line.ProductName = value ?? "";
            }
        }

        public string UnitText
        {
            get => Row.Line is { } line ? (Row.ErrorField == ProductImportField.Unit && Row.Kind == BulkImportRowKind.Error ? "" : UnitCatalog.Display(line.Unit)) : Raw(_cUnit);
            set => Row.SetUnit(value);
        }

        public string QuantityText
        {
            get => Row.Line?.QuantityText ?? Raw(_cQty);
            set
            {
                if (Row.Line is { } line)
                    line.QuantityText = value ?? "";
            }
        }

        public string PurchaseText
        {
            get => Row.Line is { } line ? (line.PurchasePrice > 0 ? line.PurchasePriceText : "") : Raw(_cPurchase);
            set
            {
                if (Row.Line is { } line)
                    line.SetPurchaseText(value);
            }
        }

        public string MarkupText
        {
            get => Row.Line is { } line ? line.MarkupText : Raw(_cMarkup);
            set
            {
                if (Row.Line is { } line)
                    line.MarkupText = value ?? "";
            }
        }

        public string SaleText
        {
            get => Row.Line is { } line ? (line.SalePrice > 0 ? line.SalePriceText : "") : Raw(_cSale);
            set
            {
                if (Row.Line is { } line)
                    line.SetSaleText(value);
            }
        }

        public string StatusText
        {
            get
            {
                if (Row.Kind == BulkImportRowKind.Error)
                    return "⚠ " + Row.Error;
                var text = Row.Note;
                return string.IsNullOrEmpty(Row.Warning) ? text : text + " · " + Row.Warning;
            }
        }

        /// <summary>Текст ячейки; числа — с запятой, как во всей программе (внутри строки прихода они с точкой).</summary>
        public string Get(string path) => path switch
        {
            nameof(Barcode) => Barcode,
            nameof(Name) => Name,
            nameof(UnitText) => UnitText,
            nameof(QuantityText) => QuantityText.Replace('.', ','),
            nameof(PurchaseText) => PurchaseText.Replace('.', ','),
            nameof(MarkupText) => MarkupText.Replace('.', ','),
            nameof(SaleText) => SaleText.Replace('.', ','),
            _ => "",
        };

        public void RaiseAll()
        {
            foreach (var name in new[] { nameof(Name), nameof(UnitText), nameof(QuantityText), nameof(PurchaseText), nameof(MarkupText), nameof(SaleText), nameof(StatusText), "Row" })
                Raise(name);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Ячейка таблицы: текст строки и красная подсветка, если ошибка именно в этом поле (или в строке — для столбца статуса).</summary>
    private sealed class RowCell : Border
    {
        private readonly BulkImportWizardWindow _owner;
        private readonly Func<ImportRowVm, string> _text;
        private readonly ProductImportField? _field;
        private readonly bool _status;
        private readonly TextBlock _block = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
        private ImportRowVm? _vm;

        public RowCell(BulkImportWizardWindow owner, Func<ImportRowVm, string> text, ProductImportField? field, bool status)
        {
            _owner = owner;
            _text = text;
            _field = field;
            _status = status;
            Child = _block;
            if (status)
            {
                _block.TextWrapping = TextWrapping.Wrap;
                _block.TextTrimming = TextTrimming.None;
                _block.FontSize = 12.5;
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as ImportRowVm;
            if (_vm is not null)
                _vm.PropertyChanged += OnChanged;
            Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnChanged;
            _vm = null;
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (_vm is null)
                return;
            _block.Text = _text(_vm);
            var isError = _vm.Row.Kind == BulkImportRowKind.Error;
            var cellError = isError && (_status || (_field is not null && _vm.Row.ErrorField == _field));
            Background = cellError ? _owner.Brush("BrushDangerSoft") : null;
            var key = _status ? (isError ? "BrushDanger" : "BrushTextSoft") : "BrushText";
            _block.Foreground = _owner.Brush(cellError && !_status ? "BrushDangerOnSoft" : key);
        }
    }

    /// <summary>Выбор столбца файла: кнопка с текущим выбором и список под ней (не ComboBox/Popup: в окнах без системной рамки они не открываются).</summary>
    private sealed class ColumnChoice : UserControl
    {
        private readonly IReadOnlyList<string> _items;
        private readonly string _none;
        private readonly Button _button;
        private readonly Border _panel;
        private readonly StackPanel _list = new();
        private int _selected = -1;
        private TopLevel? _top;

        public event Action<int>? SelectedChanged;

        public ColumnChoice(IReadOnlyList<string> items, string none)
        {
            _items = items;
            _none = none;
            _button = new Button { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            _button.Classes.Add("modal-secondary");
            _button.Click += (_, _) => _panel!.IsVisible = !_panel.IsVisible && Rebuild();
            _panel = new Border
            {
                IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 0),
                Child = new ScrollViewer { MaxHeight = 220, Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            };
            _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("BrushPanel"));
            _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("BrushBorder"));
            Content = new StackPanel { Children = { _button, _panel } };
            Show();
        }

        public int Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Show();
            }
        }

        private void Show()
        {
            _button.Content = _selected >= 0 && _selected < _items.Count ? _items[_selected] : _none;
        }

        private bool Rebuild()
        {
            _list.Children.Clear();
            void Add(string text, int index)
            {
                var b = new Button { Content = text, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false };
                b.Classes.Add(index == _selected ? "modal-primary" : "modal-secondary");
                b.Margin = new Thickness(0, 0, 0, 3);
                b.Click += (_, _) =>
                {
                    _selected = index;
                    Show();
                    _panel.IsVisible = false;
                    SelectedChanged?.Invoke(index);
                };
                _list.Children.Add(b);
            }
            Add(_none, -1);
            for (var i = 0; i < _items.Count; i++)
                Add(_items[i], i);
            return true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _top = TopLevel.GetTopLevel(this);
            _top?.AddHandler(PointerPressedEvent, OnTopPressed, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _top?.RemoveHandler(PointerPressedEvent, OnTopPressed);
            _top = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTopPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (!_panel.IsVisible)
                return;
            for (var v = e.Source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            {
                if (ReferenceEquals(v, this))
                    return;
            }
            _panel.IsVisible = false;
        }
    }
}
