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
using NurMarketKassa.AvaloniaHost.Services; // 2026-10-29: PosDialogs (без этого сборка падала)
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

    private static string PauseText => T("Пауза", "Тыныгуу", "Pause", "Duraklat", "Pauza");
    private static string ResumeText => T("Продолжить", "Улантуу", "Resume", "Devam", "Davom etish");

    /// <summary>2026-10-30: « · 38 строк/с · осталось ~6 мин» — скорость и оценка времени для строки хода загрузки. Пока скорость не известна — пусто.</summary>
    internal static string RateEtaText(double rowsPerSecond, int remaining)
    {
        if (rowsPerSecond < 0.05)
            return "";
        var secs = remaining <= 0 ? 0 : (int)Math.Ceiling(remaining / rowsPerSecond);
        string eta;
        if (secs < 60)
            eta = T($"{Math.Max(1, secs)} с", $"{Math.Max(1, secs)} сек", $"{Math.Max(1, secs)} s", $"{Math.Max(1, secs)} sn", $"{Math.Max(1, secs)} s");
        else if (secs < 3600)
        {
            var m = (secs + 59) / 60;
            eta = T($"{m} мин", $"{m} мүн", $"{m} min", $"{m} dk", $"{m} daq");
        }
        else
        {
            var h = secs / 3600;
            var m = (secs % 3600 + 59) / 60;
            eta = T($"{h} ч {m} мин", $"{h} саат {m} мүн", $"{h} h {m} min", $"{h} sa {m} dk", $"{h} soat {m} daq");
        }
        var rate = rowsPerSecond >= 10 ? $"{rowsPerSecond:0}" : $"{rowsPerSecond:0.#}";
        return T($" · {rate} строк/с · осталось ~{eta}", $" · {rate} сап/сек · калды ~{eta}", $" · {rate} rows/s · ~{eta} left", $" · {rate} satır/sn · ~{eta} kaldı", $" · {rate} qator/s · ~{eta} qoldi");
    }

    /// <summary>Скорость по скользящему окну (не короче 1,5 с), сглаженная: не прыгает на каждом обновлении.</summary>
    private sealed class RateMeter
    {
        private long _ticks = Environment.TickCount64;
        private int _n = -1;
        private double _rate;

        public double Feed(int n)
        {
            var now = Environment.TickCount64;
            if (_n < 0)
            {
                _n = n;
                _ticks = now;
                return _rate;
            }
            var dt = now - _ticks;
            if (dt >= 1500)
            {
                var inst = (n - _n) * 1000.0 / dt;
                _rate = _rate <= 0 ? inst : _rate * 0.7 + inst * 0.3;
                _n = n;
                _ticks = now;
            }
            return _rate;
        }
    }

    /// <summary>Стенд проверки: не обновлять каталог с сервера перед проверкой файла.</summary>
    public static bool RefreshCatalogBeforeCheck { get; set; } = true;

    /// <summary>Стенд проверки: не спрашивать подтверждения «цена продажи изменится».</summary>
    public static bool SkipConfirmations { get; set; }

    private enum Filter { All, Receipt, New, Error, Warning, Merged }

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
    // 2026-10-30: режим «только карточки и цены», объединение одинаковых названий, быстрый поиск, группы ошибок, пример файла.
    private readonly CheckBox _cardsCheck = new() { MinHeight = 44 };
    private readonly CheckBox _mergeNameCheck = new() { MinHeight = 44 };
    private readonly TextBox _search = new() { MinHeight = 44, Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _errGroups = new() { Orientation = Orientation.Horizontal, IsVisible = false };
    private readonly Border _hintPanel = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly Button _hintToggle;
    private readonly TextBlock _modeNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private Avalonia.Threading.DispatcherTimer? _recheckTimer;
    private string? _errorGroup;
    private string _searchText = "";
    private bool _loadingFile;
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal, IsVisible = false };
    private readonly Dictionary<Filter, Button> _tileButtons = new();
    private readonly Border _opsPanel = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _opsInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new();
    private readonly TextBlock _summary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly Button _errorsBtn, _loadBtn, _mapToggle, _opsToggle;
    private Button _cancelBtn = null!;
    private Button _pauseBtn = null!; // 2026-10-30: «Пауза» / «Продолжить» для идущей загрузки карточек
    private readonly BulkLoadControl _loadControl = new();
    private CancellationTokenSource? _loadCts;
    private StackPanel? _top;
    // 2026-10-30 (приёмка 1.17.65): журнал загрузки этого файла (повтор без дублей, «Продолжить загрузку» после обрыва).
    private BulkImportJournal? _journal;
    private int _journalDone;

    public BulkImportWizardWindow()
    {
        Title = T("Массовая загрузка товаров", "Товарларды жаппай жүктөө", "Bulk product import", "Toplu ürün yükleme", "Mahsulotlarni ommaviy yuklash");
        SectionCards.AsModal(this, 1240);
        MinWidth = 640;

        _pickFile = SectionCards.Secondary(T("Выбрать файл…", "Файлды тандоо…", "Choose file…", "Dosya seç…", "Faylni tanlash…"));
        _paste = SectionCards.Secondary(T("Вставить из буфера", "Буферден коюу", "Paste from clipboard", "Panodan yapıştır", "Buferdan qo'yish"));
        _tplXlsx = SectionCards.Secondary(T("Шаблон Excel", "Excel үлгүсү", "Excel template", "Excel şablonu", "Excel shabloni"));
        _tplCsv = SectionCards.Secondary(T("Шаблон CSV", "CSV үлгүсү", "CSV template", "CSV şablonu", "CSV shabloni"));
        _hintToggle = SectionCards.Ghost(T("Какой файл подходит?", "Кандай файл ылайыктуу?", "Which file fits?", "Hangi dosya uygun?", "Qanday fayl mos?"));
        _hintToggle.MinHeight = 44;
        _hintToggle.Click += (_, _) =>
        {
            _hintPanel.IsVisible = !_hintPanel.IsVisible;
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        };
        BuildHintPanel();
        _oldImport = SectionCards.Ghost(T("Старая загрузка (замена остатка)", "Эски жүктөө (калдыкты алмаштыруу)", "Old import (replace stock)", "Eski yükleme (stok değiştirme)", "Eski yuklash (qoldiqni almashtirish)"));
        foreach (var b in new[] { _pickFile, _paste, _tplXlsx, _tplCsv, _oldImport })
            b.MinHeight = 44;
        _pickFile.Click += async (_, _) => await GuardAsync(PickFileAsync, "выбор файла").ConfigureAwait(true);
        _paste.Click += async (_, _) => await GuardAsync(PasteAsync, "вставка из буфера").ConfigureAwait(true);
        _tplXlsx.Click += async (_, _) => await GuardAsync(() => SaveTemplateAsync(xlsx: true), "шаблон Excel").ConfigureAwait(true);
        _tplCsv.Click += async (_, _) => await GuardAsync(() => SaveTemplateAsync(xlsx: false), "шаблон CSV").ConfigureAwait(true);
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
        foreach (var c in new Control[] { _pickFile, _paste, _tplXlsx, _tplCsv, _hintToggle, _oldImport })
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
        _checkBtn = SectionCards.Secondary(T("Проверить", "Текшерүү", "Check", "Kontrol et", "Tekshirish"));
        _checkBtn.MinHeight = 44;
        // 2026-10-30: кнопка «Проверить» — проверка и сворачивание панели столбцов (после смены столбцов/параметров проверка идёт сама, без сворачивания).
        _checkBtn.Click += async (_, _) => await GuardAsync(() => CheckAsync(collapse: true), "проверка").ConfigureAwait(true);
        _cardsCheck.Content = T("Только карточки и цены (без прихода)", "Карточкалар жана баалар гана (кирешесиз)", "Cards and prices only (no receipt)", "Yalnızca kart ve fiyatlar (giriş yok)", "Faqat kartochkalar va narxlar (kirimsiz)");
        ToolTip.SetTip(_cardsCheck, T("Остаток не меняется: новые товары создаются с нулевым остатком, у товаров склада обновляются цены и доп. штрихкоды.",
            "Калдык өзгөрбөйт: жаңы товарлар нөл калдык менен түзүлөт, кампадагы товарларда баалар жана кошумча штрих-коддор жаңырат.",
            "The stock does not change: new products are created with zero stock, prices and extra barcodes of stock products are updated.",
            "Stok değişmez: yeni ürünler sıfır stokla oluşturulur, depo ürünlerinde fiyatlar ve ek barkodlar güncellenir.",
            "Qoldiq o'zgarmaydi: yangi mahsulotlar nol qoldiq bilan yaratiladi, ombor mahsulotlarida narxlar va qo'shimcha shtrix-kodlar yangilanadi."));
        _mergeNameCheck.Content = T("Одинаковое название, разные штрихкоды — один товар (доп. штрихкоды)", "Аталышы бирдей, штрих-коддору ар түрдүү — бир товар (кошумча штрих-коддор)",
            "Same name, different barcodes — one product (extra barcodes)", "Aynı ad, farklı barkodlar — tek ürün (ek barkodlar)", "Nomi bir xil, shtrix-kodlar boshqa — bitta mahsulot (qo'shimcha shtrix-kodlar)");
        _search.Watermark = T("Поиск по таблице", "Таблицадан издөө", "Search the table", "Tabloda ara", "Jadvaldan qidirish");
        _search.TextChanged += (_, _) =>
        {
            _searchText = (_search.Text ?? "").Trim();
            ScheduleRefilter();
        };
        _cardsCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _mergeNameCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _markupBox.TextChanged += (_, _) => ScheduleRecheck();
        _defUnitCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _dupSum.IsCheckedChanged += (_, _) => ScheduleRecheck();

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
        AddSetting(_cardsCheck);
        AddSetting(_mergeNameCheck);
        AddSetting(_checkBtn);
        _mapPanel.Child = new StackPanel { Spacing = 4, Children = { mapTitle, _mapWrap, _modeNote, settings } };

        // ---- шаг 3: итог, действия, таблица
        foreach (var (filter, _) in new[] { (Filter.All, 0), (Filter.Receipt, 0), (Filter.New, 0), (Filter.Error, 0), (Filter.Warning, 0), (Filter.Merged, 0) })
        {
            var tile = SectionCards.Secondary("");
            tile.MinHeight = 44;
            tile.Margin = new Thickness(0, 0, 8, 6);
            var f = filter;
            tile.Click += (_, _) =>
            {
                _filter = f;
                _errorGroup = null;
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
        _search.Margin = new Thickness(0, 0, 8, 6);
        _tiles.Children.Add(_search);
        BuildOpsPanel();
        BuildGrid();
        UpdateToggles();
        _grid.Height = 330;
        _grid.IsVisible = false;

        var root = new StackPanel { Spacing = 2 };
        _top = root;
        root.Children.Add(step1);
        root.Children.Add(_fileWarn);
        root.Children.Add(_hintPanel);
        root.Children.Add(_mapPanel);
        root.Children.Add(_status);
        root.Children.Add(_bar);
        root.Children.Add(_tiles);
        root.Children.Add(_errGroups);
        root.Children.Add(_opsPanel);
        root.Children.Add(_grid);

        // ---- низ окна
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        _errorsBtn = SectionCards.Secondary(T("Скачать строки с ошибками", "Катасы бар саптарды жүктөп алуу", "Download rows with errors", "Hatalı satırları indir", "Xatoli qatorlarni yuklab olish"));
        _errorsBtn.MinHeight = 44;
        _errorsBtn.IsEnabled = false;
        _errorsBtn.Click += async (_, _) => await GuardAsync(SaveErrorsAsync, "строки с ошибками").ConfigureAwait(true);
        // 2026-10-30: «Отмена» — остановить идущую загрузку карточек (уже записанное остаётся).
        _cancelBtn = SectionCards.Secondary(T("Остановить загрузку", "Жүктөөнү токтотуу", "Stop loading", "Yüklemeyi durdur", "Yuklashni to'xtatish"));
        _cancelBtn.MinHeight = 44;
        _cancelBtn.IsVisible = false;
        // 2026-10-30 (приёмка 1.17.65): «Остановить» прерывает и режим прихода — отменяет ещё не поставленные части очереди (поставленные доходят до конца).
        _cancelBtn.Click += (_, _) =>
        {
            _loadCts?.Cancel();
            if (_batchId is not null)
            {
                BulkReceivingFeeder.Instance.CancelPending(_batchId);
                _cancelBtn.IsEnabled = false;
                _status.Text = T("Останавливаю загрузку…", "Жүктөө токтотулууда…", "Stopping the load…", "Yükleme durduruluyor…", "Yuklash to'xtatilmoqda…");
            }
        };
        // 2026-10-30 (скорость загрузки): пауза идущей загрузки карточек — уже отправленные строки дописываются, новые не берутся; «Продолжить» возобновляет.
        _pauseBtn = SectionCards.Secondary(PauseText);
        _pauseBtn.MinHeight = 44;
        _pauseBtn.IsVisible = false;
        _pauseBtn.Click += (_, _) =>
        {
            _loadControl.Paused = !_loadControl.Paused;
            _pauseBtn.Content = _loadControl.Paused ? ResumeText : PauseText;
            if (_loadControl.Paused)
                _status.Text = T("Пауза. Нажмите «Продолжить», чтобы загружать дальше.", "Тыныгуу. Жүктөөнү улантуу үчүн «Улантуу» басыңыз.", "Paused. Press “Resume” to continue loading.",
                    "Duraklatıldı. Devam etmek için «Devam»a basın.", "Pauza. Yuklashni davom ettirish uchun «Davom etish»ni bosing.");
        };
        _loadBtn = SectionCards.Primary(T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash"));
        _loadBtn.MinHeight = 44;
        _loadBtn.IsEnabled = false;
        _loadBtn.Click += async (_, _) => await GuardAsync(LoadAsync, "загрузка").ConfigureAwait(true);

        Content = SectionCards.Shell(Title ?? "", T("Excel, CSV или таблица из буфера. Количество из файла прибавляется к остатку (приход).",
                "Excel, CSV же буфердеги таблица. Файлдагы сан калдыкка кошулат (кирешe).",
                "Excel, CSV or a table from the clipboard. The quantity in the file is added to the stock (a receipt).",
                "Excel, CSV veya panodaki tablo. Dosyadaki miktar stoğa eklenir (mal kabul).",
                "Excel, CSV yoki buferdagi jadval. Fayldagi miqdor qoldiqqa qo'shiladi (kirim)."),
            root, SectionCards.Buttons(close, _cancelBtn, _pauseBtn, _errorsBtn, _loadBtn), _summary);

        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _loadCts?.Cancel(); // 2026-10-30: закрытие окна останавливает загрузку карточек (приход в фоне идёт дальше — так и задумано)
            BulkReceivingFeeder.Instance.BatchChanged -= OnBatchChanged;
            BulkReceivingFeeder.Instance.BatchFinished -= OnBatchFinished;
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        RefreshTexts();
    }

    public static void Open(Window? owner)
    {
        // 2026-10-29: Android — ошибка при создании/показе окна уходила в общий диалог «Произошла ошибка в программе» без причины.
        // Теперь причина (с вложенным исключением) пишется в журнал, а кассир видит понятный текст.
        try
        {
            var window = new BulkImportWizardWindow();
            if (owner != null)
                window.Show(owner);
            else
                window.Show();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: окно не открылось: {DescribeException(ex)}", "ERROR");
            PosDialogs.Error(owner, T($"Не удалось открыть окно массовой загрузки: {ex.GetBaseException().Message}",
                $"Жаппай жүктөө терезесин ачуу мүмкүн болгон жок: {ex.GetBaseException().Message}",
                $"Could not open the bulk import window: {ex.GetBaseException().Message}",
                $"Toplu yükleme penceresi açılamadı: {ex.GetBaseException().Message}",
                $"Ommaviy yuklash oynasini ochib bo'lmadi: {ex.GetBaseException().Message}"),
                T("Ошибка", "Ката", "Error", "Hata", "Xato"));
        }
    }

    /// <summary>2026-10-29: тип и сообщение исключения вместе со всей цепочкой вложенных (TargetInvocationException сам ничего не говорит).</summary>
    private static string DescribeException(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" -> ", parts) + Environment.NewLine + ex;
    }

    /// <summary>2026-10-29: обработчики кнопок окна — async-лямбды; любое исключение в них уходило в общий диалог ошибки программы.
    /// Теперь оно пишется в журнал и показывается в строке состояния окна.</summary>
    private async Task GuardAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: {what}: {DescribeException(ex)}", "ERROR");
            try
            {
                _status.Text = T($"Не удалось выполнить действие: {ex.GetBaseException().Message}", $"Аракетти аткаруу мүмкүн болгон жок: {ex.GetBaseException().Message}",
                    $"Could not complete the action: {ex.GetBaseException().Message}", $"İşlem tamamlanamadı: {ex.GetBaseException().Message}", $"Amalni bajarib bo'lmadi: {ex.GetBaseException().Message}");
            }
            catch
            {
                // окно уже закрыто — достаточно журнала
            }
        }
    }

    /// <summary>2026-10-29: Android отдаёт файл как content:// без локального пути — копируем во временный файл в кэше приложения
    /// (раньше «Выбрать файл» молча ничего не делал). Имя сохраняем: по расширению читаются Excel/CSV.</summary>
    private static async Task<string?> CopyToTempAsync(IStorageFile file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-import");
        Directory.CreateDirectory(dir);
        var safe = string.Concat((string.IsNullOrWhiteSpace(file.Name) ? "import.csv" : file.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(dir, safe);
        await using var input = await file.OpenReadAsync().ConfigureAwait(true);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output).ConfigureAwait(true);
        return path;
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
        _grid.Height = Math.Clamp(cap - topHeight - chrome, 170, 900); // 2026-10-30: таблица занимает всё свободное место (панель столбцов свёрнута)
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
        _status.Text = T("Выберите файл Excel или CSV — или вставьте таблицу из буфера. Обязателен только «Штрихкод»; для новых товаров ещё «Название» и «Цена продажи». Нет «Количества» — загрузятся только карточки и цены, остаток не изменится.",
            "Excel же CSV файлын тандаңыз — же буфердеги таблицаны коюңуз. Милдеттүү — «Штрих-код» гана; жаңы товарларга дагы «Аталышы» жана «Сатуу баасы». «Саны» жок болсо — карточкалар жана баалар гана жүктөлөт, калдык өзгөрбөйт.",
            "Choose an Excel or CSV file — or paste a table from the clipboard. Only “Barcode” is required; new products also need “Name” and “Sale price”. With no “Quantity” only cards and prices are loaded, the stock does not change.",
            "Bir Excel veya CSV dosyası seçin — ya da panodan bir tablo yapıştırın. Yalnızca «Barkod» zorunlu; yeni ürünler için ayrıca «Ürün adı» ve «Satış fiyatı». «Miktar» yoksa yalnızca kart ve fiyatlar yüklenir, stok değişmez.",
            "Excel yoki CSV faylni tanlang — yoki buferdan jadval qo'ying. Faqat «Shtrix-kod» majburiy; yangi mahsulotlar uchun yana «Nomi» va «Sotuv narxi». «Miqdor» bo'lmasa — faqat kartochkalar va narxlar yuklanadi, qoldiq o'zgarmaydi.");
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
        var path = files[0].TryGetLocalPath() ?? await CopyToTempAsync(files[0]).ConfigureAwait(true);
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
        _mapping = ProductImportMapping.Resolve(_table.Headers, _table); // 2026-10-30: с проверкой по данным столбцов
        _loadingFile = true;
        _cardsCheck.IsChecked = !_mapping.HasQuantity; // нет столбца количества — «только карточки и цены» по умолчанию
        _loadingFile = false;
        _hintPanel.IsVisible = false;
        var sheet = _table.SheetName is { Length: > 0 } s ? T($" · лист «{s}»", $" · «{s}» барагы", $" · sheet “{s}”", $" · «{s}» sayfası", $" · «{s}» varag'i") : "";
        var enc = _table.EncodingName is { Length: > 0 } e ? $" · {e}" : "";
        _fileInfo.Text = T($"«{name}»{sheet}{enc} · строк: {_table.DataRowCount}, столбцов: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · саптар: {_table.DataRowCount}, мамычалар: {_table.ColumnCount}",
            $"“{name}”{sheet}{enc} · rows: {_table.DataRowCount}, columns: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · satır: {_table.DataRowCount}, sütun: {_table.ColumnCount}",
            $"«{name}»{sheet}{enc} · qatorlar: {_table.DataRowCount}, ustunlar: {_table.ColumnCount}");
        BuildMappingChoices();
        _mapPanel.IsVisible = true; // развёрнута до «Проверить», потом сворачивается
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
        if (file is null)
            return;
        var path = file.TryGetLocalPath();
        try
        {
            // 2026-10-29: Android — локального пути нет: шаблон собираем во временный файл и копируем в выбранное место потоком.
            var target = path ?? Path.Combine(Path.GetTempPath(), xlsx ? "products_template.xlsx" : "products_template.csv");
            if (xlsx)
                ProductImportTemplate.SaveXlsx(target);
            else
                ProductImportTemplate.SaveCsv(target);
            if (path is null)
            {
                await using var output = await file.OpenWriteAsync().ConfigureAwait(true);
                if (output.CanSeek)
                    output.SetLength(0);
                await using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                await input.CopyToAsync(output).ConfigureAwait(true);
                path = file.Name;
            }
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
        ProductImportField.Barcode, ProductImportField.Name, ProductImportField.AltBarcodes, ProductImportField.Quantity, ProductImportField.Unit, ProductImportField.Purchase,
        ProductImportField.Markup, ProductImportField.Sale, ProductImportField.Category, ProductImportField.Weight,
    ];

    private static bool IsRequired(ProductImportField f) => f is ProductImportField.Barcode; // 2026-10-30: количество необязательно

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
                if (!_mapping.HasQuantity)
                {
                    _loadingFile = true;
                    _cardsCheck.IsChecked = true;
                    _loadingFile = false;
                }
                ScheduleRecheck(); // 2026-10-30: проверка заново сама (с задержкой), не вручную
            };
            _choices[field] = choice;
            var label = ProductImportMapping.FieldLabel(field) + (IsRequired(field) ? " *" : "");
            // 2026-10-30: «Уверенность» автоподбора; спорные (угадано по данным или по части заголовка) подсвечиваются.
            var conf = _mapping.ConfidenceOf(field);
            if (conf != MappingConfidence.None)
                label += conf switch
                {
                    MappingConfidence.High => "",
                    MappingConfidence.Medium => T("  · уверенность 70 % — проверьте", "  · ишеним 70 % — текшериңиз", "  · confidence 70% — check", "  · güven %70 — kontrol edin", "  · ishonch 70 % — tekshiring"),
                    _ => T("  · уверенность 40 % — проверьте!", "  · ишеним 40 % — текшериңиз!", "  · confidence 40% — check!", "  · güven %40 — kontrol edin!", "  · ishonch 40 % — tekshiring!"),
                };
            var caption = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 2) };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable(conf is MappingConfidence.Medium or MappingConfidence.Low ? "BrushDanger" : "BrushTextSoft"));
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
            CardsOnly = _cardsCheck.IsChecked == true || !_mapping.HasQuantity,
            MergeSameName = _mergeNameCheck.IsChecked == true,
        };
    }

    /// <summary>Проверить файл заново (после смены столбцов или параметров). Вызывается и автоматически после выбора файла.</summary>
    public async Task CheckAsync(bool collapse = false)
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
                _status.Text = T($"Разбор {n:N0} из {total:N0}…", $"Талдоо {n:N0} / {total:N0}…", $"Parsing {n:N0} of {total:N0}…", $"Ayrıştırma {n:N0} / {total:N0}…", $"Tahlil {n:N0} / {total:N0}…");
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
            RefreshJournal();
            BuildRows();
            _status.Text = ResumeNote();
            // 2026-10-30: панель столбцов сворачивается только по кнопке «Проверить» — чтобы владелец успел посмотреть подбор столбцов.
            if (collapse)
            {
                _mapPanel.IsVisible = false;
                _opsPanel.IsVisible = false;
            }
            UpdateToggles();
            UpdateModeNote();
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
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
            _bar.IsVisible = false;
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.65): найти журнал этого файла (хэш содержимого + режим). Пачка прихода, закончившаяся пока окно было закрыто,
    /// сворачивается в «записано» здесь же. Журнал не открылся — работаем без него (как раньше).</summary>
    private void RefreshJournal()
    {
        _journal = null;
        _journalDone = 0;
        if (_table is null || _plan is null || _plan.Problem is not null)
            return;
        try
        {
            var mode = _plan.CardsOnly ? "cards" : "receipt";
            var j = BulkImportJournal.Open(BulkImportJournal.HashOf(_table, mode), mode, _fileName);
            if (j.ReceiptBatchId is { } id)
                j.FoldReceipt(BulkReceivingFeeder.Instance.Get(id));
            _journal = j;
            _journalDone = j.DoneCount == 0 ? 0 : _plan.Lines.Count(l => j.IsDone(l.Barcode));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: журнал файла не открыт: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сколько строк ещё осталось записать (режим карточек — по списку загрузчика, режим прихода — по строкам прихода).</summary>
    private int LeftToLoad()
    {
        var plan = _plan;
        if (plan is null)
            return 0;
        if (plan.CardsOnly)
        {
            var sel = BulkCardsLoader.Select(plan);
            return _journalDone == 0 ? sel.Count : sel.Count(r => r.Line is { } l && !(_journal?.IsDone(l.Barcode) ?? false));
        }
        return Math.Max(0, plan.ReadyCount - _journalDone);
    }

    private string ResumeNote()
    {
        if (_journal is null || _journalDone == 0 || _plan is null)
            return "";
        if (_journal.ReceiptBatchId is not null)
            return T("Этот файл ещё загружается в фоне — дождитесь итога.", "Бул файл дагы фондо жүктөлүп жатат — жыйынтыкты күтүңүз.", "This file is still loading in the background — wait for the result.",
                "Bu dosya hâlâ arka planda yükleniyor — sonucu bekleyin.", "Bu fayl hali fonda yuklanmoqda — natijani kuting.");
        if (LeftToLoad() == 0)
            return T($"Этот файл уже загружен ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Повторно загружать не нужно — иначе остаток прибавится ещё раз.",
                $"Бул файл жүктөлгөн ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Кайра жүктөөнүн кереги жок — болбосо калдык дагы бир жолу кошулат.",
                $"This file is already loaded ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). No need to load it again — the stock would be added twice.",
                $"Bu dosya zaten yüklendi ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Tekrar yüklemeye gerek yok — stok iki kez eklenir.",
                $"Bu fayl allaqachon yuklangan ({_journal.UpdatedAt:dd.MM.yyyy HH:mm}). Qayta yuklash shart emas — qoldiq ikki marta qo'shiladi.");
        return T($"Прошлая загрузка этого файла не закончена: записано {_journalDone}. Нажмите «Продолжить загрузку» — уже записанное пропустится.",
            $"Бул файлдын мурунку жүктөөсү бүтө элек: жазылды {_journalDone}. «Жүктөөнү улантуу» басыңыз — жазылганы өткөрүлөт.",
            $"The previous load of this file did not finish: {_journalDone} written. Press “Continue loading” — what is written will be skipped.",
            $"Bu dosyanın önceki yüklemesi bitmedi: yazılan {_journalDone}. «Yüklemeye devam et»e basın — yazılanlar atlanır.",
            $"Bu faylning oldingi yuklashi tugamagan: yozildi {_journalDone}. «Yuklashni davom ettirish»ni bosing — yozilganlari o'tkaziladi.");
    }

    /// <summary>2026-10-30: проверка заново после смены столбцов/параметров — сама, через 0,7 с после последней правки (дебаунс). Идёт проверка —
    /// отменяется и начинается новая.</summary>
    private void ScheduleRecheck()
    {
        if (_loadingFile || _table is null || _started)
            return;
        _recheckTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _recheckTimer.Tick -= OnRecheckTick;
        _recheckTimer.Tick += OnRecheckTick;
        _recheckTimer.Stop();
        _recheckTimer.Start();
    }

    private async void OnRecheckTick(object? sender, EventArgs e)
    {
        _recheckTimer?.Stop();
        if (_table is null || _started)
            return;
        if (_building)
        {
            _cts?.Cancel(); // текущая проверка устарела
            _recheckTimer?.Start();
            return;
        }
        await GuardAsync(() => CheckAsync(), "проверка").ConfigureAwait(true);
    }

    private Avalonia.Threading.DispatcherTimer? _refilterTimer;

    private void ScheduleRefilter()
    {
        _refilterTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refilterTimer.Tick -= OnRefilterTick;
        _refilterTimer.Tick += OnRefilterTick;
        _refilterTimer.Stop();
        _refilterTimer.Start();
    }

    private void OnRefilterTick(object? sender, EventArgs e)
    {
        _refilterTimer?.Stop();
        if (_plan is not null && _rows.Count > 0)
            ApplyFilter();
    }

    private void UpdateModeNote()
    {
        var cards = _plan?.CardsOnly == true;
        _modeNote.IsVisible = _table is not null;
        _modeNote.Text = cards
            ? (_mapping.HasQuantity
                ? T("Режим «только карточки и цены»: количества из файла игнорируются — остаток не изменится.", "«Карточкалар жана баалар гана» режими: файлдагы сандар эске алынбайт — калдык өзгөрбөйт.",
                    "“Cards and prices only” mode: quantities in the file are ignored — the stock does not change.", "«Yalnızca kart ve fiyatlar» modu: dosyadaki miktarlar yok sayılır — stok değişmez.", "«Faqat kartochkalar va narxlar» rejimi: fayldagi miqdorlar hisobga olinmaydi — qoldiq o'zgarmaydi.")
                : T("Количества нет в файле — остаток не изменится. Загрузятся только карточки и цены.", "Файлда сандар жок — калдык өзгөрбөйт. Карточкалар жана баалар гана жүктөлөт.",
                    "There is no quantity in the file — the stock will not change. Only cards and prices are loaded.", "Dosyada miktar yok — stok değişmez. Yalnızca kart ve fiyatlar yüklenir.", "Faylda miqdor yo'q — qoldiq o'zgarmaydi. Faqat kartochkalar va narxlar yuklanadi."))
            : T("Количество из файла прибавляется к остатку (приход).", "Файлдагы сан калдыкка кошулат (кирешe).", "The quantity from the file is added to the stock (a receipt).", "Dosyadaki miktar stoğa eklenir (mal kabul).", "Fayldagi miqdor qoldiqqa qo'shiladi (kirim).");
        _modeNote.Foreground = Brush("BrushTextSoft");
    }

    /// <summary>2026-10-30: «Какой файл подходит» — колонки и пример.</summary>
    private void BuildHintPanel()
    {
        _hintPanel.Bind(Border.BackgroundProperty, _hintPanel.GetResourceObservable("BrushPanelSoft"));
        _hintPanel.Bind(Border.BorderBrushProperty, _hintPanel.GetResourceObservable("BrushBorder"));
        var title = Caption(T("Какой файл подходит", "Кандай файл ылайыктуу", "Which file fits", "Hangi dosya uygun", "Qanday fayl mos"));
        var text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        text.Text = T("Первая строка — заголовки столбцов: Штрихкод, Название, Доп. штрихкоды, Количество, Единица, Закупка, Наценка %, Цена продажи, Категория, Весовой. "
                + "Обязателен только «Штрихкод» (для новых товаров ещё «Название» и «Цена продажи»). «Количество» можно не писать — тогда остаток не изменится. "
                + "«Доп. штрихкоды» — несколько кодов в одной ячейке через ; , пробел или |. Штрихкоды — только цифры, 4–14 знаков; столбец штрихкода сохраняйте как текст. Для примера нажмите «Шаблон Excel».",
            "Биринчи сап — мамычалардын аталыштары: Штрих-код, Аталышы, Кошумча штрих-коддор, Саны, Бирдик, Сатып алуу баасы, Үстөк %, Сатуу баасы, Категория, Весовой. "
                + "Милдеттүү — «Штрих-код» гана (жаңы товарларга дагы «Аталышы» жана «Сатуу баасы»). «Саны» болбосо — калдык өзгөрбөйт. "
                + "«Кошумча штрих-коддор» — бир уячада бир нече код ; , боштук же | менен. Штрих-коддор — цифралар гана, 4–14 белги; мамычаны текст катары сактаңыз. Үлгү үчүн «Excel үлгүсү» басыңыз.",
            "The first row holds column headers: Barcode, Name, Extra barcodes, Quantity, Unit, Purchase, Markup %, Sale price, Category, By weight. "
                + "Only “Barcode” is required (new products also need “Name” and “Sale price”). “Quantity” may be left out — the stock then does not change. "
                + "“Extra barcodes” — several codes in one cell separated by ; , space or |. Barcodes are digits only, 4–14 characters; save the barcode column as text. Press “Excel template” for an example.",
            "İlk satır sütun başlıklarıdır: Barkod, Ürün adı, Ek barkodlar, Miktar, Birim, Alış, Kâr %, Satış fiyatı, Kategori, Tartılı. "
                + "Yalnızca «Barkod» zorunlu (yeni ürünler için ayrıca «Ürün adı» ve «Satış fiyatı»). «Miktar» yazılmayabilir — stok değişmez. "
                + "«Ek barkodlar» — tek hücrede ; , boşluk veya | ile ayrılmış birkaç kod. Barkodlar yalnızca rakam, 4–14 karakter; sütunu metin olarak kaydedin. Örnek için «Excel şablonu»na basın.",
            "Birinchi qator — ustun sarlavhalari: Shtrix-kod, Nomi, Qo'shimcha shtrix-kodlar, Miqdor, Birlik, Xarid, Ustama %, Sotuv narxi, Kategoriya, Vaznli. "
                + "Faqat «Shtrix-kod» majburiy (yangi mahsulotlar uchun yana «Nomi» va «Sotuv narxi»). «Miqdor»ni yozmasa ham bo'ladi — qoldiq o'zgarmaydi. "
                + "«Qo'shimcha shtrix-kodlar» — bir katakda ; , bo'shliq yoki | bilan bir nechta kod. Shtrix-kodlar faqat raqam, 4–14 belgi; ustunni matn sifatida saqlang. Namuna uchun «Excel shabloni»ni bosing.");
        _hintPanel.Child = new StackPanel { Spacing = 4, Children = { title, text } };
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
            Filter.Error => list.Where(r => r.Row.Kind == BulkImportRowKind.Error && (_errorGroup is null || r.Row.ErrorKey == _errorGroup)),
            Filter.Warning => list.Where(r => !string.IsNullOrEmpty(r.Row.Warning)),
            Filter.Merged => list.Where(r => r.Row.Kind == BulkImportRowKind.Merged || r.Row.HasMerged),
            _ => list,
        };
        // 2026-10-30: быстрый поиск по таблице (штрихкод, название, причина).
        if (_searchText.Length > 0)
        {
            var q = _searchText;
            list = list.Where(r => r.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (r.Row.Error ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        _grid.ItemsSource = list.ToList();
        UpdateTiles();
        UpdateErrorGroups();
    }

    /// <summary>2026-10-30: ошибки по типу: «15 412: укажите закупку… → показать». Видны на фильтре «Ошибки».</summary>
    private void UpdateErrorGroups()
    {
        _errGroups.Children.Clear();
        _errGroups.IsVisible = false;
        if (_plan is null || _filter != Filter.Error || _plan.ErrorCount == 0)
            return;
        foreach (var (key, text, count) in _plan.ErrorGroups().Take(8))
        {
            var shown = text.Length > 60 ? text[..60] + "…" : text;
            var b = _errorGroup == key ? SectionCards.Primary("") : SectionCards.Secondary("");
            b.MinHeight = 40;
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Content = T($"{count:N0}: {shown} → показать", $"{count:N0}: {shown} → көрсөтүү", $"{count:N0}: {shown} → show", $"{count:N0}: {shown} → göster", $"{count:N0}: {shown} → ko'rsatish");
            var k = key;
            b.Click += (_, _) =>
            {
                _errorGroup = _errorGroup == k ? null : k;
                ApplyFilter();
            };
            _errGroups.Children.Add(b);
        }
        _errGroups.IsVisible = _errGroups.Children.Count > 0;
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
        var warns = plan?.WarningCount ?? 0;
        var mergedN = plan?.MergedCount ?? 0;
        _tileButtons[Filter.Warning].Content = Mark(Filter.Warning, T($"Предупреждения {warns}", $"Эскертүүлөр {warns}", $"Warnings {warns}", $"Uyarılar {warns}", $"Ogohlantirishlar {warns}"));
        _tileButtons[Filter.Merged].Content = Mark(Filter.Merged, T($"Объединено {mergedN}", $"Бириктирилди {mergedN}", $"Merged {mergedN}", $"Birleştirilen {mergedN}", $"Birlashtirilgan {mergedN}"));
        _tileButtons[Filter.Merged].IsVisible = mergedN > 0;
        if (plan?.CardsOnly == true)
            _tileButtons[Filter.Receipt].Content = Mark(Filter.Receipt, T($"Обновить товары склада {receipt - plan.UnchangedCount} (без изменений {plan.UnchangedCount})", $"Кампадагы товарларды жаңыртуу {receipt - plan.UnchangedCount} (өзгөрүүсүз {plan.UnchangedCount})",
                $"Update stock products {receipt - plan.UnchangedCount} (unchanged {plan.UnchangedCount})", $"Depo ürünlerini güncelle {receipt - plan.UnchangedCount} (değişmeyen {plan.UnchangedCount})", $"Ombor mahsulotlarini yangilash {receipt - plan.UnchangedCount} (o'zgarishsiz {plan.UnchangedCount})"));
        var ready = plan?.ReadyCount ?? 0;
        _summary.Text = plan is null || plan.Rows.Count == 0
            ? ""
            : T($"Строк готово к добавлению: {ready}, с ошибками: {errors}" + (plan.MergedCount > 0 ? $", повторов сложено: {plan.MergedCount}" : ""),
                $"Кошууга даяр саптар: {ready}, каталуу: {errors}" + (plan.MergedCount > 0 ? $", кайталоолор кошулду: {plan.MergedCount}" : ""),
                $"Rows ready to add: {ready}, with errors: {errors}" + (plan.MergedCount > 0 ? $", repeats added up: {plan.MergedCount}" : ""),
                $"Eklemeye hazır satır: {ready}, hatalı: {errors}" + (plan.MergedCount > 0 ? $", birleştirilen tekrarlar: {plan.MergedCount}" : ""),
                $"Qo'shishga tayyor qatorlar: {ready}, xatoli: {errors}" + (plan.MergedCount > 0 ? $", qo'shilgan takrorlar: {plan.MergedCount}" : ""));
        _loadBtn.Content = ready > 0 ? T($"Загрузить {ready}", $"Жүктөө {ready}", $"Load {ready}", $"Yükle {ready}", $"Yuklash {ready}") : T("Загрузить", "Жүктөө", "Load", "Yükle", "Yuklash");
        var canLoad = ready > 0 && !_started;
        // 2026-10-30: журнал знает часть строк — кнопка «Продолжить загрузку N» (остаток), а если всё уже записано — недоступна (повтор не прибавит остаток второй раз).
        if (ready > 0 && _journalDone > 0)
        {
            var left = LeftToLoad();
            _loadBtn.Content = left > 0
                ? T($"Продолжить загрузку {left}", $"Жүктөөнү улантуу {left}", $"Continue loading {left}", $"Yüklemeye devam et {left}", $"Yuklashni davom ettirish {left}")
                : T("Уже загружено", "Мурунтан жүктөлгөн", "Already loaded", "Zaten yüklendi", "Allaqachon yuklangan");
            canLoad = canLoad && left > 0 && _journal?.ReceiptBatchId is null;
        }
        _loadBtn.IsEnabled = canLoad;
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
        Filter.Warning => !string.IsNullOrEmpty(vm.Row.Warning),
        Filter.Merged => vm.Row.Kind == BulkImportRowKind.Merged,
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
        if (file is null)
            return;
        var path = file.TryGetLocalPath() ?? file.Name;
        try
        {
            var bytes = ProductImportTemplate.CsvBytes(_plan.BuildErrorsCsv());
            if (file.TryGetLocalPath() is { } localPath)
                await File.WriteAllBytesAsync(localPath, bytes).ConfigureAwait(true);
            else
            {
                // 2026-10-29: Android — без локального пути пишем потоком провайдера файлов.
                await using var output = await file.OpenWriteAsync().ConfigureAwait(true);
                if (output.CanSeek)
                    output.SetLength(0);
                await output.WriteAsync(bytes).ConfigureAwait(true);
            }
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

        // 2026-10-30: «только карточки и цены» — без прихода, свой загрузчик пакетами по 100 (BulkCardsLoader).
        if (_plan.CardsOnly)
        {
            await RunCardsAsync(final: true).ConfigureAwait(true);
            return;
        }
        // 2026-10-30: в режиме прихода доп. штрихкоды пишет тот же загрузчик (до прихода), сам приход ведёт очередь.
        if (BulkCardsLoader.Select(_plan).Count > 0)
        {
            var barcodes = await RunCardsAsync(final: false).ConfigureAwait(true);
            // 2026-10-30 (приёмка 1.17.65): «Остановить» на этапе доп. штрихкодов останавливает всю загрузку, приход в очередь не уходит.
            if (barcodes is null || barcodes.Cancelled || _plan.Lines.Count == 0)
                return;
        }

        // 2026-10-30: повтор того же файла — строки, которые журнал уже знает (приход принят очередью), второй раз не отправляются: иначе остаток прибавился бы дважды.
        IReadOnlyList<ReceivingLineVm> sendLines = _plan.Lines;
        IReadOnlyList<int>? sendRows = _plan.FileRows;
        var journal = _journal;
        if (journal is not null)
        {
            if (journal.ReceiptBatchId is not null)
            {
                _status.Text = ResumeNote();
                return;
            }
            if (journal.DoneCount > 0)
            {
                var keep = Enumerable.Range(0, sendLines.Count).Where(i => !journal.IsDone(sendLines[i].Barcode)).ToList();
                sendRows = sendRows is { } fr && fr.Count == sendLines.Count ? keep.Select(i => fr[i]).ToList() : null;
                sendLines = keep.Select(i => sendLines[i]).ToList();
                if (sendLines.Count == 0)
                {
                    _status.Text = ResumeNote();
                    return;
                }
            }
            journal.ReceiptBatchId = "pending";
            journal.ReceiptOrder = sendLines.Select(l => l.Barcode).ToList();
            journal.Total = sendLines.Count;
            journal.Completed = false;
            journal.Save();
        }

        try
        {
            feeder.BatchChanged += OnBatchChanged;
            feeder.BatchFinished += OnBatchFinished;
            _batchId = feeder.Start(sendLines, T($"Импорт: {_fileName}", $"Импорт: {_fileName}", $"Import: {_fileName}", $"İçe aktarma: {_fileName}", $"Import: {_fileName}"), sendRows);
        }
        catch (Exception ex)
        {
            feeder.BatchChanged -= OnBatchChanged;
            feeder.BatchFinished -= OnBatchFinished;
            if (journal is not null)
            {
                journal.ReceiptBatchId = null;
                journal.ReceiptOrder = [];
                journal.Save();
            }
            _status.Text = ex.Message;
            return;
        }

        if (journal is not null)
        {
            journal.ReceiptBatchId = _batchId;
            journal.Save();
        }
        _cancelBtn.IsEnabled = true;
        _cancelBtn.IsVisible = true; // «Остановить загрузку» работает и на приходе в фоне
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

    /// <summary>2026-10-30: загрузка карточек/доп. штрихкодов пакетами по 100: ход «Загрузка N из M», остановка кнопкой, итог
    /// «Создано N, обновлено M, пропущено K, ошибок E». final = true — это вся загрузка (режим «только карточки»).</summary>
    private async Task<BulkCardsResult?> RunCardsAsync(bool final)
    {
        var plan = _plan;
        var api = PosApp.CatalogApi;
        if (plan is null || api is null)
        {
            _status.Text = T("Нет связи с NurCRM — войдите в программу заново.", "NurCRM менен байланыш жок — программага кайра кириңиз.", "No connection to NurCRM — sign in again.", "NurCRM bağlantısı yok — yeniden giriş yapın.", "NurCRM bilan aloqa yo'q — qayta kiring.");
            return null;
        }
        var total = BulkCardsLoader.Select(plan).Count;
        _started = true;
        _loadBtn.IsEnabled = false;
        _checkBtn.IsEnabled = false;
        _grid.IsReadOnly = true;
        _cancelBtn.IsVisible = true;
        _loadControl.Paused = false;
        _pauseBtn.Content = PauseText;
        _pauseBtn.IsVisible = true;
        _bar.IsVisible = true;
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = 0;
        _loadCts = new CancellationTokenSource();
        // 2026-10-30 (скорость загрузки): «Загружено 1 240 из 15 433 · 38 строк/с · осталось ~6 мин»; загрузчик сообщает ход не чаще раза в 120 мс.
        var meter = new RateMeter();
        var progress = new Progress<int>(n =>
        {
            _bar.Value = n;
            if (_loadControl.Paused)
                return; // на паузе строку «Пауза…» не затираем
            _status.Text = T($"Загружено {n:N0} из {total:N0}", $"Жүктөлдү {n:N0} / {total:N0}", $"Loaded {n:N0} of {total:N0}", $"Yüklendi {n:N0} / {total:N0}", $"Yuklandi {n:N0} / {total:N0}")
                + RateEtaText(meter.Feed(n), total - n);
        });
        BulkCardsResult result;
        try
        {
            // 2026-10-30: журнал — только у настоящей загрузки карточек (final); этап доп. штрихкодов перед приходом безопасно повторяется (список объединяется).
            result = await BulkCardsLoader.RunAsync(plan, api, progress, _loadCts.Token, final ? _journal : null, _loadControl).ConfigureAwait(true);
        }
        finally
        {
            _cancelBtn.IsVisible = false;
            _pauseBtn.IsVisible = false;
            _loadControl.Paused = false;
            _bar.IsVisible = false;
        }

        if (_table is not null)
            _mapping.Remember(_table.Headers);
        plan.Recompute();
        foreach (var row in _rows)
            row.RaiseAll();
        UpdateTiles();
        if (final)
        {
            // 2026-10-30: остановили (кнопкой, закрытием окна, 401) — «Остановлено, загружено N из M»; журнал остаётся для «Продолжить загрузку».
            // Всё записано без ошибок — журнал карточек не нужен (повтор и так безопасен: каталог сравнится с файлом).
            if (result.Cancelled)
                _status.Text = result.StoppedText + (result.AuthExpired ? T(". Сессия истекла — войдите заново.", ". Сессия бүттү — кайра кириңиз.", ". The session expired — sign in again.", ". Oturum süresi doldu — yeniden giriş yapın.", ". Sessiya tugadi — qayta kiring.") : "")
                    + T(". Нажмите «Проверить», затем «Продолжить загрузку».", ". «Текшерүү», андан «Жүктөөнү улантуу» басыңыз.", ". Press “Check”, then “Continue loading”.", ". «Kontrol et», sonra «Yüklemeye devam et»e basın.", ". «Tekshirish», so'ng «Yuklashni davom ettirish»ni bosing.");
            else
            {
                _status.Text = T("Готово: ", "Даяр: ", "Done: ", "Tamam: ", "Tayyor: ") + result.SummaryText + T(". Чтобы дозагрузить остальное, нажмите «Проверить» — загруженное будет «без изменений».",
                    ". Калганын жүктөө үчүн «Текшерүү» басыңыз — жүктөлгөнү «өзгөрүүсүз» болот.", ". To load the rest press “Check” — what is loaded will show as “no changes”.",
                    ". Kalanı yüklemek için «Kontrol et»e basın — yüklenenler «değişiklik yok» olur.", ". Qolganini yuklash uchun «Tekshirish»ni bosing — yuklanganlari «o'zgarishsiz» bo'ladi.");
                if (result.Failed == 0)
                {
                    _journal?.Delete();
                    _journal = null;
                    _journalDone = 0;
                }
            }
            _summary.Text = result.SummaryText;
            _started = false; // можно проверить заново и дозагрузить: каталог обновится, загруженное станет «без изменений»
            _checkBtn.IsEnabled = true;
            try
            {
                await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовая загрузка: каталог после загрузки не обновлён: {ex.Message}", "WARNING");
            }
        }
        else if (result.Cancelled)
        {
            // 2026-10-30: стоп на этапе доп. штрихкодов — приход не стартует; окно снова доступно для проверки и «Продолжить».
            _status.Text = result.StoppedText + (result.AuthExpired ? T(". Сессия истекла — войдите заново.", ". Сессия бүттү — кайра кириңиз.", ". The session expired — sign in again.", ". Oturum süresi doldu — yeniden giriş yapın.", ". Sessiya tugadi — qayta kiring.") : "");
            _started = false;
            _checkBtn.IsEnabled = true;
            _grid.IsReadOnly = false;
            UpdateTiles();
        }
        else if (result.Failed > 0)
            _status.Text = T("Доп. штрихкоды: ", "Кошумча штрих-коддор: ", "Extra barcodes: ", "Ek barkodlar: ", "Qo'shimcha shtrix-kodlar: ") + result.SummaryText;
        return result;
    }

    private void OnBatchChanged(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.Maximum = Math.Max(1, info.TotalLines);
            _bar.Value = info.ProcessedLines;
            // 2026-10-30: + скорость и оценка времени (по времени от постановки пачки; первые секунды не показываем — цифра была бы случайной).
            var secs = (DateTime.Now - info.CreatedAt).TotalSeconds;
            var eta = secs >= 8 && info.ProcessedLines > 0 ? RateEtaText(info.ProcessedLines / secs, info.TotalLines - info.ProcessedLines) : "";
            _status.Text = T($"Загрузка в фоне: {info.ProcessedLines} из {info.TotalLines} · {info.SummaryText}", $"Фондо жүктөө: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Loading in the background: {info.ProcessedLines} of {info.TotalLines} · {info.SummaryText}", $"Arka planda yükleniyor: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}",
                $"Fonda yuklanmoqda: {info.ProcessedLines} / {info.TotalLines} · {info.SummaryText}") + eta;
        });
    }

    private void OnBatchFinished(BulkBatchInfo info)
    {
        if (info.Id != _batchId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _bar.IsVisible = false;
            _cancelBtn.IsVisible = false;
            _journal?.FoldReceipt(info); // 2026-10-30: принятые строки уходят в журнал — повтор файла их не пошлёт
            // 2026-10-30: строки, которые очередь не приняла, помечаются ошибкой с причиной — «Скачать строки с ошибками» их выдаст.
            if (_plan is not null && info.FailedRows.Count > 0)
            {
                var byRow = _plan.Rows.GroupBy(r => r.FileRow).ToDictionary(g => g.Key, g => g.First());
                foreach (var failed in info.FailedRows)
                {
                    if (failed.FileRow is { } fr && byRow.TryGetValue(fr, out var planRow))
                        planRow.SetLoadError(failed.Error);
                }
                _plan.Recompute();
                foreach (var row in _rows)
                    row.RaiseAll();
                UpdateTiles();
            }
            if (info.Cancelled && info.NotSent > 0)
            {
                // 2026-10-30: остановили — «Остановлено, загружено N из M»; журнал знает принятое, «Проверить» → «Продолжить загрузку» допишет остальное.
                _status.Text = T($"Остановлено, загружено {info.Received} из {info.TotalLines} · {info.SummaryText}. Нажмите «Проверить», затем «Продолжить загрузку».",
                    $"Токтотулду, жүктөлдү {info.Received} / {info.TotalLines} · {info.SummaryText}. «Текшерүү», андан «Жүктөөнү улантуу» басыңыз.",
                    $"Stopped, loaded {info.Received} of {info.TotalLines} · {info.SummaryText}. Press “Check”, then “Continue loading”.",
                    $"Durduruldu, yüklenen {info.Received} / {info.TotalLines} · {info.SummaryText}. «Kontrol et», sonra «Yüklemeye devam et»e basın.",
                    $"To'xtatildi, yuklandi {info.Received} / {info.TotalLines} · {info.SummaryText}. «Tekshirish», so'ng «Yuklashni davom ettirish»ni bosing.");
                _started = false;
                _checkBtn.IsEnabled = true;
                _grid.IsReadOnly = false;
                return;
            }
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
