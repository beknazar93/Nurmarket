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
using Avalonia.VisualTree;
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
public sealed partial class BulkImportWizardWindow : Window
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

    private enum Filter { All, Receipt, New, Error, Warning, Merged, Skipped }

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
    private readonly Border _mapPanel = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1) };
    private readonly WrapPanel _mapWrap = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<ProductImportField, ColumnChoice> _choices = new();
    private readonly TextBox _markupBox = new() { MinHeight = 44, Width = 90, MaxLength = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _markupHint = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _defUnitCheck = new() { MinHeight = 44 };
    private readonly UnitPickerBox _defUnit = new() { Width = 190, HorizontalAlignment = HorizontalAlignment.Left, SecondaryClass = "modal-secondary", PrimaryClass = "modal-primary" };
    private readonly RadioButton _dupSum = new() { GroupName = "BulkWizardDup", IsChecked = true, MinHeight = 44 };
    private readonly RadioButton _dupFirst = new() { GroupName = "BulkWizardDup", MinHeight = 44 };
    private readonly Button _checkBtn;
    // 2026-10-30: режим «только карточки и цены», объединение одинаковых названий, быстрый поиск, группы ошибок, пример файла.
    private readonly CheckBox _mergeNameCheck = new() { MinHeight = 44 };
    // 2026-11-20: доп. штрихкоды товарам склада (по умолчанию да) и «плохой доп. код пропустить только его»
    private readonly CheckBox _altExistingCheck = new() { MinHeight = 44, IsChecked = true };
    private readonly CheckBox _altSkipBadCheck = new() { MinHeight = 44 };
    // 2026-11-20 (CT MAX): служебным кодам («*10», без цифр) — внутренний штрихкод NurCRM; виден только у источника с такими кодами
    private readonly CheckBox _internalCodeCheck = new() { MinHeight = 44, IsChecked = true, IsVisible = false };
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
    private Grid? _top;
    // 2026-10-30 (приёмка 1.17.65): журнал загрузки этого файла (повтор без дублей, «Продолжить загрузку» после обрыва).
    private BulkImportJournal? _journal;
    private int _journalDone;

    public BulkImportWizardWindow()
    {
        Title = T("Массовая загрузка товаров", "Товарларды жаппай жүктөө", "Bulk product import", "Toplu ürün yükleme", "Mahsulotlarni ommaviy yuklash");
        SectionCards.AsModal(this, 1240);
        MinWidth = 640;
        InitOnboarding(); // 2026-11-13 (Л-10): источник файла, демо-магазин, клиенты, «что дальше»

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
        // 2026-11-19 (список владельца, п. 7): владелец старого окна — сам мастер (раньше его владелец, и окно пряталось под мастером).
        _oldImport.Click += (_, _) => BulkImportWindow.Open(this);
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
        _mergeNameCheck.Content = T("Одинаковое название, разные штрихкоды — один товар (доп. штрихкоды)", "Аталышы бирдей, штрих-коддору ар түрдүү — бир товар (кошумча штрих-коддор)",
            "Same name, different barcodes — one product (extra barcodes)", "Aynı ad, farklı barkodlar — tek ürün (ek barkodlar)", "Nomi bir xil, shtrix-kodlar boshqa — bitta mahsulot (qo'shimcha shtrix-kodlar)");
        _search.Watermark = T("Поиск по таблице", "Таблицадан издөө", "Search the table", "Tabloda ara", "Jadvaldan qidirish");
        _search.TextChanged += (_, _) =>
        {
            _searchText = (_search.Text ?? "").Trim();
            ScheduleRefilter();
        };
        _altExistingCheck.Content = T("Добавлять доп. штрихкоды товарам, которые уже есть на складе", "Кампада бар товарларга кошумча штрих-коддорду кошуу", "Add extra barcodes to products already in stock",
            "Depoda olan ürünlere ek barkod ekle", "Omborda bor mahsulotlarga qo'shimcha shtrix-kodlarni qo'shish");
        _altSkipBadCheck.Content = T("Плохой доп. код (занят, не цифры) — пропустить только его, товар загрузить", "Жараксыз кошумча код (бош эмес, цифра эмес) — ошону гана өткөрүп, товарды жүктөө",
            "Bad extra code (taken, not digits) — skip only that code and load the product", "Kötü ek kod (dolu, rakam değil) — yalnızca onu atla, ürünü yükle",
            "Yaroqsiz qo'shimcha kod (band, raqam emas) — faqat uni o'tkazib, mahsulotni yuklash");
        ToolTip.SetTip(_altSkipBadCheck, T("Выключено: строка с плохим доп. кодом целиком получает ошибку. Включено: код пропускается с предупреждением, товар загружается.",
            "Өчүрүлгөн: жараксыз кошумча коду бар сап толугу менен ката алат. Күйгүзүлгөн: код эскертүү менен өткөрүлөт, товар жүктөлөт.",
            "Off: a row with a bad extra code gets an error as a whole. On: the code is skipped with a warning and the product is loaded.",
            "Kapalı: kötü ek kodlu satır tümüyle hata alır. Açık: kod uyarıyla atlanır, ürün yüklenir.",
            "O'chiq: yaroqsiz qo'shimcha kodli qator butunlay xato oladi. Yoqiq: kod ogohlantirish bilan o'tkaziladi, mahsulot yuklanadi."));
        _mergeNameCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _internalCodeCheck.Content = T("Служебным кодам (*10, без цифр) дать внутренний штрихкод NurCRM; прежний код — в артикул", "Кызматтык коддорго (*10, цифрасыз) ички NurCRM штрих-кодун берүү; мурунку код — артикулга",
            "Give service codes (*10, no digits) an internal NurCRM barcode; the old code goes to the article", "Hizmet kodlarına (*10, rakamsız) NurCRM iç barkodu ver; eski kod ürün koduna",
            "Xizmat kodlariga (*10, raqamsiz) NurCRM ichki shtrix-kodini berish; eski kod artikulga");
        _internalCodeCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _altExistingCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _altSkipBadCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _markupBox.TextChanged += (_, _) => ScheduleRecheck();
        _defUnitCheck.IsCheckedChanged += (_, _) => ScheduleRecheck();
        _dupSum.IsCheckedChanged += (_, _) => ScheduleRecheck();

        var settings = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) }; // 2026-11-20: боковая полоса узкая — параметры столбиком, подпись над полем
        void AddSetting(Control c)
        {
            c.Margin = new Thickness(0, 0, 0, 6);
            settings.Children.Add(c);
        }
        AddSetting(new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 4,
            Children = { Caption(T("Наценка для строк без цены продажи, %", "Сатуу баасы жок саптарга кошумча, %", "Markup for rows without a sale price, %", "Satış fiyatı olmayan satırlar için kâr, %", "Sotuv narxi yo'q qatorlar uchun ustama, %")), _markupBox, _markupHint },
        });
        AddSetting(new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Children = { _defUnitCheck, _defUnit } });
        AddSetting(new StackPanel { Orientation = Orientation.Vertical, Spacing = 0, Children = { _dupSum, _dupFirst } });
        AddSetting(_mergeNameCheck);
        AddSetting(_altExistingCheck);
        AddSetting(_altSkipBadCheck);
        AddSetting(_internalCodeCheck);
        AddSetting(_checkBtn);
        _mapPanel.Child = new StackPanel { Spacing = 4, Children = { mapTitle, _mapWrap, BuildAutoPartsBox(), settings } };

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
        // 2026-11-20: «Дополнительно» (столбцы и параметры) и действия над отмеченными — в боковой полосе шага «Проверьте» (BuildSteps); таблицу не выдавливают.
        _mapToggle = SectionCards.Ghost("");
        _opsToggle = SectionCards.Ghost(""); // в мастере не показывается (действия над отмеченными лежат в боковой полосе), поле оставлено для UpdateToggles
        _mapToggle.MinHeight = 44;
        _mapToggle.Click += (_, _) =>
        {
            SetAdvanced(!_advCol.IsVisible);
            _mapOpenedByUser = _advCol.IsVisible;
        };
        BuildOpsPanel();
        BuildGrid();
        UpdateToggles();
        _grid.IsVisible = false;


        // ---- низ окна
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.MinHeight = 44;
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

        BuildSteps(close); // 2026-11-20: страницы «Откуда / Файл / Проверьте / Загрузка», индикатор шагов и нижняя панель кнопок

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

    // 2026-11-01 (контрольный аудит, С2-16): мастер один на программу — двойной клик по кнопке не открывает второе окно с тем же файлом (двойной приход).
    private static BulkImportWizardWindow? _openWizard;

    public static void Open(Window? owner)
    {
        if (!ImportAccess.Allowed) // 2026-11-21: импорт — только в программе владельца; в кассе окно не открывается ни кнопкой, ни ссылкой, ни клавишей
            return;
        // 2026-10-29: Android — ошибка при создании/показе окна уходила в общий диалог «Произошла ошибка в программе» без причины.
        // Теперь причина (с вложенным исключением) пишется в журнал, а кассир видит понятный текст.
        try
        {
            if (_openWizard is { IsVisible: true } already)
            {
                already.Activate();
                return;
            }
            var window = new BulkImportWizardWindow();
            _openWizard = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_openWizard, window)) _openWizard = null; };
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

    /// <summary>2026-11-20: подогнать высоту рабочей области мастера под окно (карточка не выше доли экрана). Страницы — Grid со строкой «*»: таблице
    /// шага «Проверьте» достаётся всё, что не заняли итог и переключатель; боковая полоса «Дополнительно» высоту не отнимает. Стенд зовёт после смены размера.</summary>
    public void Refit()
    {
        if (Content is not ModalShell shell || _top is null)
            return;
        var cap = double.IsInfinity(shell.MaxHeight) ? 700 : shell.MaxHeight;
        if (SizeToContent == SizeToContent.Manual && Bounds.Height > 0)
            cap = Math.Min(cap, Bounds.Height - shell.Margin.Top - shell.Margin.Bottom); // окно задано размером (стенд, Android) — не выше него
        _sourceSub.IsVisible = cap >= 640; // 2026-11-20: на низком экране подсказка шага «Откуда» уступает место плиткам
        if (cap < 640 && shell.Subtitle is not null)
            shell.Subtitle = null; // 1024×600: подзаголовок уступает место таблице
        UpdateLayout();
        double chrome = 150; // шапка окна, подвал с кнопками, поля тени
        if (_top.FindAncestorOfType<ScrollViewer>() is { Bounds.Height: > 0 } scroll && shell.Bounds.Height > 0 && _top.Bounds.Height > 0)
        {
            // поля вокруг содержимого видны только пока оно выше окна (Extent больше Viewport) — запоминаем
            if (scroll.Extent.Height > scroll.Viewport.Height + 0.5)
                _bodyExtra = Math.Max(0, scroll.Extent.Height - _top.Bounds.Height);
            chrome = Math.Max(100, shell.Bounds.Height - scroll.Bounds.Height + _bodyExtra);
        }
        var h = Math.Max(300, cap - chrome);
        if (double.IsNaN(_top.Height) || Math.Abs(_top.Height - h) > 0.5)
        {
            _top.Height = h;
            UpdateLayout();
        }
        // не вышли ли за окно (появилась прокрутка) — убрать излишек
        if (_top.FindAncestorOfType<ScrollViewer>() is { } sv && sv.Extent.Height > sv.Viewport.Height + 0.5)
        {
            _top.Height = Math.Max(300, _top.Height - (sv.Extent.Height - sv.Viewport.Height));
            UpdateLayout();
        }
    }

    private double _bodyExtra;
    private bool _mapOpenedByUser;

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
        // 2026-11-20: «Дополнительно: сопоставление столбцов» — сворачиваемая боковая полоса шага «Проверьте» (по умолчанию свёрнута).
        // 2026-11-27: подпись короче («Дополнительно»), полный текст — в подсказке: кнопка шириной 359 px сжимала итог до 274 px (4 строки), и таблица на 1024×600 теряла 10–30 px.
        _mapToggle.Content = (_advCol.IsVisible ? "▾ " : "▸ ") + T("Дополнительно", "Кошумча", "Advanced", "Gelişmiş", "Qo'shimcha");
        ToolTip.SetTip(_mapToggle, T("Дополнительно: сопоставление столбцов", "Кошумча: мамычаларды дал келтирүү", "Advanced: column mapping", "Gelişmiş: sütun eşleme", "Qo'shimcha: ustunlarni moslash"));
        _opsToggle.Content = T("Действия над отмеченными", "Белгиленгендер үчүн аракеттер", "Actions on ticked rows", "İşaretlilerde işlemler", "Belgilanganlar ustida amallar");
    }

    private void RefreshTexts()
    {
        var auto = ReceivingMarkupSettingsStore.Current;
        _markupHint.Text = auto.AutoEnabled
            ? T($"пусто — по настройке приёмки ({auto.DefaultPercent:0.##} %)", $"бош — кабыл алуу жөндөөсү боюнча ({auto.DefaultPercent:0.##} %)",
                $"empty — by the receiving setting ({auto.DefaultPercent:0.##}%)", $"boşsa — mal kabul ayarına göre (%{auto.DefaultPercent:0.##})", $"bo'sh — qabul sozlamasi bo'yicha ({auto.DefaultPercent:0.##} %)")
            : T("пусто — без цены (строка с ошибкой)", "бош — баасыз (катасы бар сап)", "empty — no price (row with an error)", "boşsa — fiyatsız (hatalı satır)", "bo'sh — narxsiz (xatoli qator)");
        _status.Text = ""; // 2026-11-20: длинная подсказка уехала на шаги «Откуда» и «Файл»
        UpdateTiles();
    }

}
