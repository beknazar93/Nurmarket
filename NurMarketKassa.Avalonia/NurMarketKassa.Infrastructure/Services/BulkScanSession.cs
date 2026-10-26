using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-23, просьба владельца: «на складе массовая загрузка товаров через сканер тоже сделай». Режим «Массовое добавление сканером»:
// оператор подряд сканирует штрихкоды, программа набирает таблицу — известный товар +1 к количеству, неизвестный — новая строка
// (название, закупка, продажа, количество, категория, единица). «Загрузить всё» создаёт новые товары и принимает количество ТЕМ ЖЕ
// сервисом, что обычная приёмка склада (PurchaseReceivingService.PostAsync без поставщика): ни денег, ни остатков здесь своей логики нет.
//
// 2026-10-26, ТЗ «Склад — массовое добавление» (docs/ТЗ Склад — массовое добавление — скан 3 колонки, Excel, CSV (26.10.2026).md), пакет М2:
// три группы строк (на складе / база CRM / неизвестный) поверх прежнего общего списка Rows, быстрый скан без ожидания сервера
// (проверка незнакомого кода идёт в фоне, не больше двух запросов сразу), проведение через BulkReceivingFeeder (М1), а не по строке в окне.
// Вида здесь нет: три колонки рисует М4.

/// <summary>Что случилось со сканом (для звука/подсветки и фокуса в окне).</summary>
public enum BulkScanOutcome
{
    /// <summary>Код слишком короткий — пропущен.</summary>
    Ignored,
    /// <summary>Известный товар добавлен новой строкой (количество 1).</summary>
    Added,
    /// <summary>Повторный скан: количество строки +1.</summary>
    Repeated,
    /// <summary>Неизвестный штрихкод: новая строка «Неизвестный» (пока «проверяю…»), курсор — в «Название».</summary>
    NewRow,
    /// <summary>2026-10-26 (М2): проверка в фоне закончилась — строка переехала в «Есть на складе» или «Есть в базе CRM» (или слилась с другой).</summary>
    Resolved,
}

/// <summary>2026-10-26 (М2): в какой из трёх колонок строка.</summary>
public enum BulkScanGroup
{
    Warehouse,
    Crm,
    Unknown,
}

/// <summary>Строка таблицы массового добавления: строка приёмки + то, что нужно только виду (ошибка, подсветка).</summary>
public sealed class BulkScanRow : INotifyPropertyChanged
{
    private ReceivingLineVm _line;
    private string _error = "";
    private bool _flash;
    private bool _checking;
    private bool _notVerified;
    private bool _saleManual;

    /// <summary>Известный товар пришёл без цены закупки или продажи — поля цен остаются на виду, пока строку не поправят (не пропадают при вводе).</summary>
    private bool _priceFix;

    public BulkScanRow(ReceivingLineVm line)
    {
        _line = line;
        _line.PropertyChanged += OnLineChanged;
        _priceFix = !line.IsNew && (line.PurchasePrice <= 0 || line.SalePrice <= 0);
        ScannedCode = line.Barcode;
    }

    /// <summary>Строка приёмки. Один и тот же объект живёт до успеха: на нём держится защита приёмки от двойного прихода.</summary>
    public ReceivingLineVm Line
    {
        get => _line;
        private set
        {
            _line.PropertyChanged -= OnLineChanged;
            _line = value;
            _line.PropertyChanged += OnLineChanged;
            _priceFix = !value.IsNew && (value.PurchasePrice <= 0 || value.SalePrice <= 0);
            Raise(nameof(Line));
            OnLineChanged(null, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Товар на сервере нашёлся только при загрузке (не было на этом ПК) — строка становится приходом существующего.</summary>
    internal void ReplaceLine(ReceivingLineVm line) => Line = line;

    /// <summary>Правка строки (вид → черновик).</summary>
    public event Action? Edited;

    /// <summary>2026-10-26 (М2): у строки поменялся источник (Source) — сессия переносит её в другую колонку.</summary>
    internal event Action<BulkScanRow>? GroupChanged;

    /// <summary>Товара у магазина ещё нет (новый или из общей базы CRM) — доступны название, цены, категория, единица.</summary>
    public bool IsNewProduct => _line.IsNew;

    public bool IsKnown => !_line.IsNew;

    /// <summary>Показывать поля «Закупка» и «Продажа»: у нового товара всегда, у известного — только если цен у него нет.</summary>
    public bool ShowPrices => _line.IsNew || _priceFix;

    public string KindText => _line.Source switch
    {
        ReceivingSource.Warehouse => Tr.T("На складе", "Кампада", "In stock", "Depoda", "Omborda"),
        ReceivingSource.GlobalBase => Tr.T("База CRM", "CRM базасы", "CRM catalog", "CRM veritabanı", "CRM bazasi"),
        _ => Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot"),
    };

    /// <summary>2026-10-26 (М2): колонка строки по её источнику.</summary>
    public BulkScanGroup Group => _line.Source switch
    {
        ReceivingSource.Warehouse => BulkScanGroup.Warehouse,
        ReceivingSource.GlobalBase => BulkScanGroup.Crm,
        _ => BulkScanGroup.Unknown,
    };

    /// <summary>Код, как его отсканировали. Сопоставление повторного скана идёт и по нему: неизвестная строка, принятая как приход
    /// к другому товару (AcceptAsExisting), получает штрихкод того товара, а повтор исходного кода должен снова дать +1 к ней.</summary>
    public string ScannedCode { get; internal set; }

    /// <summary>2026-10-26 (М2): идёт проверка кода на сервере («проверяю в базе…»).</summary>
    public bool IsChecking
    {
        get => _checking;
        internal set
        {
            if (_checking == value)
                return;
            _checking = value;
            Raise(nameof(IsChecking));
            Raise(nameof(StatusText));
            Raise(nameof(HasStatus));
        }
    }

    /// <summary>2026-10-26 (М2): сервер не ответил (нет связи / авария) — «не проверено», кнопка «Проверить снова» (BulkScanSession.RecheckAsync).
    /// При проведении штрихкод всё равно проверяется (BulkReceivingFeeder: подготовка задачи).</summary>
    public bool NotVerified
    {
        get => _notVerified;
        internal set
        {
            if (_notVerified == value)
                return;
            _notVerified = value;
            Raise(nameof(NotVerified));
            Raise(nameof(StatusText));
            Raise(nameof(HasStatus));
        }
    }

    public string StatusText => _checking
        ? Tr.T("проверяю в базе…", "базадан текшерип жатам…", "checking the database…", "veritabanında kontrol ediliyor…", "bazadan tekshirilmoqda…")
        : _notVerified
            ? Tr.T("не проверено (нет связи)", "текшерилген жок (байланыш жок)", "not verified (no connection)", "doğrulanmadı (bağlantı yok)", "tekshirilmadi (aloqa yo'q)")
            : "";

    public bool HasStatus => _checking || _notVerified;

    /// <summary>2026-10-26 (М2): цену продажи правили руками (или наценкой строки) — «Наценка для всех» её не трогает.</summary>
    public bool SaleEditedManually
    {
        get => _saleManual;
        internal set => _saleManual = value;
    }

    /// <summary>Сессия сама ставит продажу от наценки — это не «ручная» правка.</summary>
    internal bool SuppressManualFlag { get; set; }

    /// <summary>Строку убрали из списка (проведена, ✕, очистка) — запоздавший ответ проверки её не трогает.</summary>
    internal bool IsRemoved { get; set; }

    /// <summary>В какой колонке строка сейчас лежит (чтобы перенести при смене источника).</summary>
    internal BulkScanGroup? PlacedIn { get; set; }

    /// <summary>Известный товар: «остаток 5 → 6».</summary>
    public string StockInfo => _line.Source == ReceivingSource.Warehouse
        ? Tr.T($"остаток {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"калдык {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"stock {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"stok {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"qoldiq {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}")
        : "";

    public string Category
    {
        get => _line.CategoryName ?? "";
        set
        {
            _line.CategoryName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Raise(nameof(Category));
            Edited?.Invoke();
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (_error == value)
                return;
            _error = value;
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    public bool IsFlash
    {
        get => _flash;
        set
        {
            if (_flash == value)
                return;
            _flash = value;
            Raise(nameof(IsFlash));
        }
    }

    internal void SetError(string message) => Error = message;
    internal void ClearError() => Error = "";

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 2026-10-23: id созданного товара проставляет PurchaseReceivingService.PostAsync с фонового потока — вид его не показывает,
        // а событие с чужого потока роняло привязки («Call from invalid thread») и обрывало строку после создания товара.
        if (e.PropertyName == nameof(ReceivingLineVm.ProductId))
            return;
        // 2026-10-26 (М2): продажу правили не мы (сессия ставит её от наценки под флагом SuppressManualFlag) — значит руками.
        if (e.PropertyName == nameof(ReceivingLineVm.SalePrice) && !SuppressManualFlag)
            _saleManual = true;
        Raise(nameof(IsNewProduct));
        Raise(nameof(IsKnown));
        Raise(nameof(ShowPrices));
        Raise(nameof(KindText));
        Raise(nameof(StockInfo));
        if (e.PropertyName is nameof(ReceivingLineVm.Source) or null)
        {
            Raise(nameof(Group));
            GroupChanged?.Invoke(this);
        }
        Edited?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Сессия массового добавления: сканы, три колонки, проведение через подающего, черновик. Все методы — с потока интерфейса.</summary>
public sealed class BulkScanSession : INotifyPropertyChanged, IDisposable
{
    /// <summary>Не дольше столько ждём сервер при проверке незнакомого кода: строка останется «не проверено», название можно вписать руками.</summary>
    public static TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>2026-10-26 (М2, Н-8): сколько проверок кода идёт на сервере одновременно (остальные ждут своей очереди; строка уже в списке).</summary>
    public static int MaxParallelChecks { get; set; } = 2;

    /// <summary>Только для стенда проверки: не обновлять каталог кассы с сервера после создания товаров.</summary>
    public static bool CatalogRefreshEnabled { get; set; } = true;

    public ObservableCollection<BulkScanRow> Rows { get; } = new();

    /// <summary>2026-10-26 (М2): те же строки по трём колонкам; новая или изменённая строка — сверху. Источник правды — Rows (черновик, счётчики).</summary>
    public ObservableCollection<BulkScanRow> WarehouseRows { get; } = new();
    public ObservableCollection<BulkScanRow> CrmRows { get; } = new();
    public ObservableCollection<BulkScanRow> UnknownRows { get; } = new();

    /// <summary>Ошибки последней загрузки (по строкам).</summary>
    public ObservableCollection<string> Errors { get; } = new();

    private readonly SemaphoreSlim _checkGate = new(Math.Max(1, MaxParallelChecks));
    private int _pendingChecks;
    private TaskCompletionSource? _idle;
    private int _saveVersion;
    private bool _isLoading;
    private string _progressText = "";
    private double _progressValue, _progressMax = 1;
    private string _summaryText = "";
    private string _backgroundText = "";
    private double? _crmMarkup;
    private readonly HashSet<string> _myBatches = new(StringComparer.Ordinal);
    private bool _subscribed;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>Сохранять черновик (на стенде проверки можно выключить).</summary>
    public bool PersistDraft { get; set; } = true;

    public BulkScanSession()
    {
        Rows.CollectionChanged += OnRowsChanged;
        WarehouseRows.CollectionChanged += (_, _) => OnGroupsChanged();
        CrmRows.CollectionChanged += (_, _) => OnGroupsChanged();
        UnknownRows.CollectionChanged += (_, _) => OnGroupsChanged();
    }

    /// <summary>Скан обработан: строка (null — пропущен) и что произошло. Окно играет звук/подсветку и ставит курсор.</summary>
    public event Action<BulkScanRow?, BulkScanOutcome>? Scanned;

    /// <summary>Пачка проведена (итог пришёл от подающего): созданных товаров, принятых строк. Окно обновляет каталог и остатки.</summary>
    public event Action<int, int>? Completed;

    /// <summary>2026-10-26 (М2): итог пачки целиком (в потоке интерфейса, после возврата ошибочных строк в колонки) — для тоста и плашки.</summary>
    public event Action<BulkBatchInfo>? BatchCompleted;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; Raise(); Raise(nameof(CanLoad)); } }
    }

    public bool CanLoad => !_isLoading && Rows.Count > 0;

    public string ProgressText { get => _progressText; private set { _progressText = value; Raise(); } }
    public double ProgressValue { get => _progressValue; private set { _progressValue = value; Raise(); } }
    public double ProgressMax { get => _progressMax; private set { _progressMax = Math.Max(1, value); Raise(); } }

    public string SummaryText { get => _summaryText; private set { _summaryText = value; Raise(); Raise(nameof(HasSummary)); } }
    public bool HasSummary => _summaryText.Length > 0;
    public bool HasFailedRows => Rows.Any(r => r.HasError);

    /// <summary>2026-10-26 (М2): «в фоне: 3 из 12» — пока пачка идёт в очереди; пусто, когда ничего не идёт.</summary>
    public string BackgroundText { get => _backgroundText; private set { _backgroundText = value; Raise(); Raise(nameof(HasBackground)); } }
    public bool HasBackground => _myBatches.Count > 0;

    /// <summary>2026-10-26 (М2): «Наценка для всех» — последнее применённое число (хранится в черновике, В-5 «запоминать последнее»).</summary>
    public double? CrmMarkupPercent
    {
        get => _crmMarkup;
        private set { if (_crmMarkup != value) { _crmMarkup = value; Raise(); } }
    }

    /// <summary>Сколько кодов проверяется / ждёт проверки на сервере (стенд ждёт ноль).</summary>
    public int PendingChecks => _pendingChecks;

    public string StatsText
    {
        get
        {
            var qty = Rows.Sum(r => r.Line.Quantity);
            return Tr.T($"Позиций: {Rows.Count} · штук: {qty:0.###}", $"Позиция: {Rows.Count} · даана: {qty:0.###}",
                $"Items: {Rows.Count} · pieces: {qty:0.###}", $"Kalem: {Rows.Count} · adet: {qty:0.###}",
                $"Pozitsiya: {Rows.Count} · dona: {qty:0.###}");
        }
    }

    /// <summary>2026-10-26 (М2): «закупка на 4 560 сом» — вторая половина нижней строки («Позиций 12 · штук 37 · закупка на …»).</summary>
    public string PurchaseTotalText
    {
        get
        {
            var total = Rows.Sum(r => r.Line.LineTotal);
            return Tr.T($"закупка на {total:0.##} сом", $"сатып алуу {total:0.##} сом", $"purchase {total:0.##} som", $"alış {total:0.##} som", $"xarid {total:0.##} so'm");
        }
    }

    public int WarehouseCount => WarehouseRows.Count;
    public int CrmCount => CrmRows.Count;
    public int UnknownCount => UnknownRows.Count;

    public string WarehouseTitle => Tr.T($"Есть на складе ({WarehouseRows.Count})", $"Кампада бар ({WarehouseRows.Count})", $"In stock ({WarehouseRows.Count})",
        $"Depoda var ({WarehouseRows.Count})", $"Omborda bor ({WarehouseRows.Count})");

    public string CrmTitle => Tr.T($"Есть в базе CRM ({CrmRows.Count})", $"CRM базасында бар ({CrmRows.Count})", $"In the CRM catalog ({CrmRows.Count})",
        $"CRM veritabanında var ({CrmRows.Count})", $"CRM bazasida bor ({CrmRows.Count})");

    public string UnknownTitle => Tr.T($"Неизвестный штрихкод ({UnknownRows.Count})", $"Белгисиз штрихкод ({UnknownRows.Count})", $"Unknown barcode ({UnknownRows.Count})",
        $"Bilinmeyen barkod ({UnknownRows.Count})", $"Noma'lum shtrix-kod ({UnknownRows.Count})");

    /// <summary>Есть что терять при закрытии окна.</summary>
    public bool HasUnsaved => Rows.Count > 0;

    private void RaiseStats()
    {
        Raise(nameof(StatsText));
        Raise(nameof(PurchaseTotalText));
    }

    private void OnGroupsChanged()
    {
        Raise(nameof(WarehouseCount));
        Raise(nameof(CrmCount));
        Raise(nameof(UnknownCount));
        Raise(nameof(WarehouseTitle));
        Raise(nameof(CrmTitle));
        Raise(nameof(UnknownTitle));
    }

    // ------------------------------------------------------------------ скан

    /// <summary>Принять скан. Известный код (в списке или в каталоге этого ПК) обрабатывается сразу, без сети; незнакомый встаёт строкой
    /// «Неизвестный» в состоянии «проверяю…», а проверка идёт в фоне. Возвращаемая задача завершается, когда проверка этого кода закончена
    /// (для известного — сразу): ждать её необязательно.</summary>
    public Task ScanAsync(string raw)
    {
        try
        {
            return ProcessScan(raw);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: скан '{raw}' не обработан: {ex.Message}", "WARNING");
            return Task.CompletedTask;
        }
    }

    private Task ProcessScan(string raw)
    {
        var code = ProductCodeLookup.Normalize(raw);
        if (code.Length < 4)
        {
            Scanned?.Invoke(null, BulkScanOutcome.Ignored);
            return Task.CompletedTask;
        }

        // 1. Этот код уже в списке (любая форма: 12 ↔ 13 цифр; в том числе строка, которая ещё проверяется) — количество +1.
        if (FindRow(code, null) is { } same)
        {
            Bump(same);
            return Task.CompletedTask;
        }

        // 2. Каталог этого ПК — мгновенно (тот же поиск, что у приёмки; весовая этикетка не разбирается).
        var tile = ProductCodeLookup.FindLocal(code, allowWeightLabel: false)?.Product;
        if (tile is not null)
        {
            var line = PurchaseReceivingService.FromTile(tile, 1);
            // Другой штрихкод того же товара (доп. штрихкод) — тоже +1 к его строке.
            if (FindRow(line.Barcode, line.ProductId) is { } sameProduct)
            {
                Bump(sameProduct);
                return Task.CompletedTask;
            }
            var known = new BulkScanRow(line) { ScannedCode = code };
            Insert(known);
            Flash(known);
            Scanned?.Invoke(known, BulkScanOutcome.Added);
            return Task.CompletedTask;
        }

        // 3. Незнакомый: строка сразу, «проверяю…», сервер спрашиваем в фоне (скан не ждёт ответа).
        var row = new BulkScanRow(new ReceivingLineVm { Barcode = code, Source = ReceivingSource.Unknown, Quantity = 1 });
        Insert(row);
        Flash(row);
        Scanned?.Invoke(row, BulkScanOutcome.NewRow);
        return BeginCheck(row);
    }

    private void Bump(BulkScanRow row)
    {
        row.Line.Quantity = Math.Round(row.Line.Quantity + 1, 3);
        row.ClearError();
        // 2026-10-24 (аудит, круг 8, С-1): счётчик «штук» обновляется сразу при повторном скане (раньше — только при смене числа строк).
        RaiseStats();
        Raise(nameof(HasFailedRows));
        Flash(row);
        Scanned?.Invoke(row, BulkScanOutcome.Repeated);
    }

    /// <summary>Строка списка с этим кодом (в любой записи: как пришёл / 12 ↔ 13 цифр; и по коду, который отсканировали) или с этим товаром.</summary>
    private BulkScanRow? FindRow(string? barcode, string? productId)
    {
        var code = ProductCodeLookup.Normalize(barcode);
        var forms = code.Length > 0 ? ProductCodeLookup.Candidates(code) : Array.Empty<string>();
        bool Matches(string x) => x.Length > 0 && forms.Contains(x, StringComparer.OrdinalIgnoreCase);
        return Rows.FirstOrDefault(r => Matches(r.Line.Barcode) || Matches(r.ScannedCode)
            || (!string.IsNullOrWhiteSpace(productId) && string.Equals(r.Line.ProductId, productId, StringComparison.OrdinalIgnoreCase)));
    }

    private void Insert(BulkScanRow row)
    {
        row.Edited += ScheduleSave;
        row.GroupChanged += Regroup;
        // Новая строка — сверху: оператор видит её сразу, прокручивать список не нужно.
        Rows.Insert(0, row);
        PlaceInGroup(row, top: true);
    }

    private void Detach(BulkScanRow row)
    {
        row.Edited -= ScheduleSave;
        row.GroupChanged -= Regroup;
        row.IsRemoved = true;
        Rows.Remove(row);
        RemoveFromGroup(row);
    }

    private ObservableCollection<BulkScanRow> GroupCollection(BulkScanGroup g) => g switch
    {
        BulkScanGroup.Warehouse => WarehouseRows,
        BulkScanGroup.Crm => CrmRows,
        _ => UnknownRows,
    };

    private void PlaceInGroup(BulkScanRow row, bool top)
    {
        var g = row.Group;
        var list = GroupCollection(g);
        if (top)
            list.Insert(0, row);
        else
            list.Add(row);
        row.PlacedIn = g;
    }

    private void RemoveFromGroup(BulkScanRow row)
    {
        if (row.PlacedIn is { } g)
            GroupCollection(g).Remove(row);
        row.PlacedIn = null;
    }

    /// <summary>Источник строки поменялся (проверка нашла товар, «принять как существующий») — строка переезжает наверх нужной колонки.</summary>
    private void Regroup(BulkScanRow row)
    {
        if (row.IsRemoved || row.PlacedIn is null || row.PlacedIn == row.Group)
            return;
        RemoveFromGroup(row);
        PlaceInGroup(row, top: true);
    }

    private async void Flash(BulkScanRow row)
    {
        row.IsFlash = true;
        await Task.Delay(700).ConfigureAwait(true);
        row.IsFlash = false;
    }

    // ------------------------------------------------------------------ проверка незнакомого кода (фон, не больше MaxParallelChecks)

    /// <summary>«Проверить снова» у строки «не проверено (нет связи)».</summary>
    public Task RecheckAsync(BulkScanRow row)
    {
        if (row.IsRemoved || row.IsChecking || row.Group != BulkScanGroup.Unknown)
            return Task.CompletedTask;
        return BeginCheck(row);
    }

    /// <summary>Завершается, когда не осталось ни одной проверки (для стенда и для окна, которому нужен итог перед закрытием).</summary>
    public Task WhenChecksDoneAsync()
    {
        if (_pendingChecks == 0)
            return Task.CompletedTask;
        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idle.Task;
    }

    private Task BeginCheck(BulkScanRow row)
    {
        // Автономный режим: сервера нет вовсе — проверять негде, строка остаётся «неизвестной».
        if (OfflineModeHelper.UseLocalOperations)
            return Task.CompletedTask;
        // Авария сервера (ServerOutageMonitor) — запрос не шлём, строка сразу «не проверено».
        if (OfflineModeHelper.IsServerOutage || PosApp.CatalogApi is null)
        {
            row.IsChecking = false;
            row.NotVerified = true;
            return Task.CompletedTask;
        }
        row.NotVerified = false;
        row.IsChecking = true;
        return CheckAsync(row);
    }

    private async Task CheckAsync(BulkScanRow row)
    {
        _pendingChecks++;
        try
        {
            ReceivingLineVm? found = null;
            var verified = false;
            await _checkGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (row.IsRemoved)
                    return;
                var code = row.ScannedCode.Length > 0 ? row.ScannedCode : row.Line.Barcode;
                // Пока строка ждала очереди, товар мог появиться в каталоге этого ПК (синхронизация, другая строка).
                if (ProductCodeLookup.FindLocal(code, allowWeightLabel: false)?.Product is { } tile)
                {
                    found = PurchaseReceivingService.FromTile(tile, 1);
                    verified = true;
                }
                else if (!ServerOutageMonitor.IsOutage)
                {
                    using var cts = new CancellationTokenSource(CheckTimeout);
                    (found, verified) = await LookupOnServerAsync(code, cts.Token).ConfigureAwait(true);
                }
            }
            finally
            {
                _checkGate.Release();
            }
            if (!row.IsRemoved)
                ApplyChecked(row, found, verified);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: проверка кода '{row.ScannedCode}' не удалась: {ex.Message}", "WARNING");
            if (!row.IsRemoved)
            {
                row.IsChecking = false;
                row.NotVerified = true;
            }
        }
        finally
        {
            _pendingChecks--;
            if (_pendingChecks == 0)
            {
                var idle = _idle;
                _idle = null;
                idle?.TrySetResult();
            }
        }
    }

    /// <summary>Серверная часть PurchaseReceivingService.LookupAsync (склад → общая база CRM), но с признаком «проверено»: LookupAsync при сбое
    /// связи молча отдаёт «неизвестный», а здесь «нет в базе» и «нет связи» — разные состояния строки (Line == null и verified).</summary>
    private static async Task<(ReceivingLineVm? Line, bool Verified)> LookupOnServerAsync(string code, CancellationToken ct)
    {
        var api = PosApp.CatalogApi;
        if (api is null)
            return (null, false);
        try
        {
            var product = await api.FindWarehouseProductByBarcodeAsync(code, ct).ConfigureAwait(true);
            if (product is { } p)
            {
                var price = JNum(p, "price");
                var purchase = JNum(p, "purchase_price");
                var name = JStr(p, "name") ?? code;
                var unit = JStr(p, "unit") ?? "шт";
                return (new ReceivingLineVm
                {
                    ProductId = JStr(p, "id"),
                    Barcode = JStr(p, "barcode") ?? code,
                    ProductName = name,
                    Unit = unit,
                    OriginalName = name,
                    OriginalUnit = unit,
                    Source = ReceivingSource.Warehouse,
                    StockBefore = JNum(p, "quantity"),
                    Quantity = 1,
                    PurchasePrice = purchase,
                    SalePrice = price,
                    OriginalPurchasePrice = purchase,
                    OriginalSalePrice = price,
                    OriginalMarkupPercent = JNum(p, "markup_percent"),
                }, true);
            }

            var global = await api.FindGlobalProductByBarcodeAsync(code, ct).ConfigureAwait(true);
            // 2026-10-03: UPC-A из сканера (12 цифр) ↔ EAN-13 с нулём в общей базе — см. BarcodeForms.
            if (global is null && BarcodeForms.Alternate(code) is { } alternate)
                global = await api.FindGlobalProductByBarcodeAsync(alternate, ct).ConfigureAwait(true);
            if (global is { } g)
                return (new ReceivingLineVm
                {
                    Barcode = JStr(g, "barcode") ?? code,
                    ProductName = JStr(g, "name") ?? "",
                    Source = ReceivingSource.GlobalBase,
                    Quantity = 1,
                }, true);
            return (null, true);
        }
        catch (Exception ex)
        {
            // Нет связи / таймаут — не повод терять скан: строка остаётся в списке как «не проверено».
            PosLogger.Log($"Массовое добавление: поиск штрихкода {code} на сервере не удался: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return (null, false);
        }
    }

    /// <summary>Результат проверки. found == null: verified — на сервере такого кода нет (остаётся «Неизвестный»), иначе «не проверено».</summary>
    private void ApplyChecked(BulkScanRow row, ReceivingLineVm? found, bool verified)
    {
        row.IsChecking = false;
        if (found is null)
        {
            row.NotVerified = !verified;
            return;
        }
        row.NotVerified = false;

        var cur = row.Line;
        // Пока шла проверка, оператор мог досканировать (+1) или вписать название/цены — не теряем.
        found.Quantity = cur.Quantity;
        if (found.Source == ReceivingSource.GlobalBase)
        {
            if (!string.IsNullOrWhiteSpace(cur.ProductName))
                found.ProductName = cur.ProductName;
            found.PurchasePrice = cur.PurchasePrice;
            found.SalePrice = cur.SalePrice;
            found.CategoryName = cur.CategoryName;
        }
        else
        {
            // Цен у найденного товара нет (сервер не отдал) — берём те, что оператор вписал, а не пишем нули в карточку.
            if (found.PurchasePrice <= 0)
                found.PurchasePrice = cur.PurchasePrice;
            if (found.SalePrice <= 0)
                found.SalePrice = cur.SalePrice;
        }

        // Другой штрихкод того же товара (доп. штрихкод) — слить с его строкой: +количество.
        if (found.Source == ReceivingSource.Warehouse && !string.IsNullOrWhiteSpace(found.ProductId)
            && Rows.FirstOrDefault(r => !ReferenceEquals(r, row) && string.Equals(r.Line.ProductId, found.ProductId, StringComparison.OrdinalIgnoreCase)) is { } twin)
        {
            twin.Line.Quantity = Math.Round(twin.Line.Quantity + cur.Quantity, 3);
            Detach(row);
            RaiseStats();
            Flash(twin);
            Scanned?.Invoke(twin, BulkScanOutcome.Repeated);
            return;
        }

        row.ReplaceLine(found); // Source изменился → GroupChanged → строка переезжает наверх своей колонки
        Flash(row);
        Scanned?.Invoke(row, BulkScanOutcome.Resolved);
    }

    // ------------------------------------------------------------------ колонки: наценка, подсказки, «принять как существующий»

    /// <summary>«Наценка для всех» в колонке «Есть в базе CRM»: продажа = закупка × (1 + %/100), округление вверх до сома
    /// (<see cref="ReceivingPricing.SaleFromMarkup"/>) у строк, где закупка задана, а продажа пуста или не правилась руками.
    /// Число запоминается (в черновике). Возвращает, у скольких строк продажа теперь соответствует наценке.</summary>
    public int ApplyCrmMarkup(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0)
            return 0;
        CrmMarkupPercent = percent;
        var count = 0;
        foreach (var row in CrmRows.ToList())
        {
            var line = row.Line;
            if (line.PurchasePrice <= 0)
                continue;
            if (line.SalePrice > 0 && row.SaleEditedManually)
                continue;
            var sale = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, percent);
            if (Math.Abs(line.SalePrice - sale) > 0.0005)
            {
                row.SuppressManualFlag = true;
                try
                {
                    line.SalePrice = sale;
                }
                finally
                {
                    row.SuppressManualFlag = false;
                }
            }
            count++;
        }
        ScheduleSave();
        return count;
    }

    /// <summary>Подсказки названия для «неизвестного» товара: до <paramref name="max"/> похожих товаров склада (сначала начинающиеся с введённого).</summary>
    public static IReadOnlyList<CatalogProductTileVm> SuggestProducts(string? text, int max = 6)
    {
        var query = (text ?? "").Trim();
        if (query.Length < 2 || max <= 0)
            return Array.Empty<CatalogProductTileVm>();
        try
        {
            return CatalogCacheService.Products.ToArray()
                .Where(p => !string.IsNullOrEmpty(p.Title) && p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(p => p.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(p => p.Title.Length)
                .Take(max)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: подсказки названия не получены: {ex.Message}", "WARNING");
            return Array.Empty<CatalogProductTileVm>();
        }
    }

    /// <summary>Неизвестная строка оказалась существующим товаром (выбрали в подсказке): становится приходом к нему, количество сохраняется.
    /// Отсканированный штрихкод к товару НЕ привязывается (В-3 по умолчанию: на сервер он не пишется). Если такой товар уже есть в списке —
    /// строки сливаются. Возвращает строку, в которой оказалось количество.</summary>
    public BulkScanRow AcceptAsExisting(BulkScanRow row, CatalogProductTileVm tile)
    {
        var qty = row.Line.Quantity > 0 ? row.Line.Quantity : 1;
        var line = PurchaseReceivingService.FromTile(tile, qty);
        line.CategoryName = null;
        if (!string.IsNullOrWhiteSpace(line.ProductId)
            && Rows.FirstOrDefault(r => !ReferenceEquals(r, row) && string.Equals(r.Line.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } twin)
        {
            twin.Line.Quantity = Math.Round(twin.Line.Quantity + qty, 3);
            Detach(row);
            RaiseStats();
            Flash(twin);
            return twin;
        }
        // Цен у товара нет — остаются те, что вписал оператор.
        if (line.PurchasePrice <= 0)
            line.PurchasePrice = row.Line.PurchasePrice;
        if (line.SalePrice <= 0)
            line.SalePrice = row.Line.SalePrice;
        row.IsChecking = false;
        row.NotVerified = false;
        row.ClearError();
        row.ReplaceLine(line);
        Flash(row);
        return row;
    }

    /// <summary>Вернуть строки в колонки (строки «не проведены» от подающего; «Вернуть строки» из «Приходов в фоне» для пачки bulk — точка
    /// стыковки для М4). Одинаковый товар/код складывается в одну строку. <paramref name="errorOf"/> — текст ошибки на карточку.</summary>
    public int RestoreLines(IEnumerable<ReceivingLineVm> lines, Func<ReceivingLineVm, string?>? errorOf = null)
    {
        var n = 0;
        foreach (var line in lines)
        {
            if (FindRow(line.Barcode, line.ProductId) is { } same)
            {
                same.Line.Quantity = Math.Round(same.Line.Quantity + line.Quantity, 3);
                same.SetError(errorOf?.Invoke(line) ?? "");
                continue;
            }
            var row = new BulkScanRow(line);
            if (errorOf?.Invoke(line) is { Length: > 0 } error)
                row.SetError(error);
            Insert(row);
            n++;
        }
        RaiseStats();
        Raise(nameof(HasFailedRows));
        return n;
    }

    // ------------------------------------------------------------------ правка списка

    public void Remove(BulkScanRow row)
    {
        Detach(row);
    }

    public void Clear()
    {
        if (_isLoading)
            return;
        foreach (var r in Rows.ToList())
        {
            r.Edited -= ScheduleSave;
            r.GroupChanged -= Regroup;
            r.IsRemoved = true;
            r.PlacedIn = null;
        }
        Rows.Clear();
        WarehouseRows.Clear();
        CrmRows.Clear();
        UnknownRows.Clear();
        Errors.Clear();
        SummaryText = "";
        Raise(nameof(HasFailedRows));
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RaiseStats();
        Raise(nameof(CanLoad));
        Raise(nameof(HasUnsaved));
        ScheduleSave();
    }

    // ------------------------------------------------------------------ проведение (через BulkReceivingFeeder, М1)

    /// <summary>Проверка строк перед «Провести приход» (<see cref="PurchaseReceivingService.Validate"/> по каждой строке): у строк с проблемами
    /// появляется текст ошибки (красная рамка в карточке). Ничего не отправляет. Окно по результату может спросить «3 строки не заполнены.
    /// Провести остальные 9?» и только потом звать <see cref="LoadAsync"/>.</summary>
    public (int Ready, int Problems) PreCheck(bool onlyFailed = false)
    {
        var (good, bad) = Evaluate(onlyFailed);
        Raise(nameof(HasFailedRows));
        return (good.Count, bad.Count);
    }

    private (List<BulkScanRow> Good, List<BulkScanRow> Bad) Evaluate(bool onlyFailed)
    {
        var good = new List<BulkScanRow>();
        var bad = new List<BulkScanRow>();
        var newBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Where(r => !onlyFailed || r.HasError).ToList())
        {
            row.ClearError();
            var problems = PurchaseReceivingService.Validate(new[] { row.Line });
            // Два новых товара с одним штрихкодом в одной пачке создались бы дважды — второй остаётся в списке.
            if (problems.Count == 0 && row.Line.IsNew && row.Line.Barcode.Length > 0 && !newBarcodes.Add(row.Line.Barcode))
            {
                var name = string.IsNullOrWhiteSpace(row.Line.ProductName) ? row.Line.Barcode : row.Line.ProductName;
                problems.Add(Tr.T($"{name}: штрихкод {row.Line.Barcode} уже есть в этом списке — уберите дубль",
                    $"{name}: {row.Line.Barcode} штрихкоду бул тизмеде мурунтан бар — кайталоону алып салыңыз",
                    $"{name}: barcode {row.Line.Barcode} is already in this list — remove the duplicate",
                    $"{name}: barkod {row.Line.Barcode} bu listede zaten var — kopyayı kaldırın",
                    $"{name}: {row.Line.Barcode} shtrix-kodi bu ro'yxatda allaqachon bor — dublikatni olib tashlang"));
            }
            if (problems.Count > 0)
            {
                row.SetError(problems[0]);
                bad.Add(row);
            }
            else
                good.Add(row);
        }
        return (good, bad);
    }

    /// <summary>«Провести приход» (<paramref name="onlyFailed"/> = false) или «Повторить ошибочные». Годные строки уходят подающему
    /// (<see cref="BulkReceivingFeeder.Start"/>: пачка пишется на диск и идёт в фоне частями, окно свободно), строки с проблемами остаются с
    /// текстом ошибки. Итог «Создано N, принято M, ошибок K» придёт позже (<see cref="BatchCompleted"/>), не принятые строки вернутся в колонки.
    /// Возвращает номер пачки (null — ничего не отправлено).</summary>
    public Task<string?> LoadAsync(bool onlyFailed = false) => Task.FromResult(StartLoad(onlyFailed));

    /// <summary>То же, что <see cref="LoadAsync"/>, синхронно (без сети; ≤ 0,5 с).</summary>
    public string? StartLoad(bool onlyFailed = false)
    {
        // Защита от двойного нажатия: флаг ставится до первой работы.
        if (_isLoading)
            return null;
        if (Rows.Count == 0)
            return null;
        IsLoading = true;
        try
        {
            Errors.Clear();
            SummaryText = "";
            var (good, bad) = Evaluate(onlyFailed);
            foreach (var row in bad)
                Errors.Add(row.Error);
            Raise(nameof(HasFailedRows));
            if (good.Count == 0)
            {
                if (bad.Count > 0)
                    SummaryText = NotPostedText(bad.Count);
                return null;
            }

            string batchId;
            try
            {
                batchId = BulkReceivingFeeder.Instance.Start(good.Select(r => r.Line).ToList(),
                    Tr.T("Массовое добавление", "Массалык кошуу", "Bulk add", "Toplu ekleme", "Ommaviy qo'shish"));
            }
            catch (InvalidOperationException ex)
            {
                // Подающий не принял пачку (закрыт файл / не записался диск) — строки остаются в списке как были.
                PosLogger.Log($"Массовое добавление: пачка не принята: {ex.Message}", "WARNING");
                Errors.Add(ex.Message);
                SummaryText = ex.Message;
                return null;
            }

            RegisterBatch(batchId);
            foreach (var row in good)
                Detach(row);
            // Строки уже в очереди: из черновика их убираем сразу, а не через 600 мс (иначе падение между этим и записью дало бы двойной приход).
            SaveDraftNow();
            SummaryText = StartedText(good.Count) + (bad.Count > 0 ? " " + NotPostedText(bad.Count) : "");
            PosLogger.Log($"Массовое добавление: {good.Count} строк отдано подающему (пачка {batchId}), в списке осталось {Rows.Count}.", "STOCK");
            return batchId;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string StartedText(int n) => Tr.T($"В работе: {n} поз. — идёт в фоне.", $"Иштелүүдө: {n} поз. — фондо жүрүп жатат.",
        $"In progress: {n} items — running in the background.", $"Sürüyor: {n} kalem — arka planda.", $"Jarayonda: {n} pozitsiya — fonda ketmoqda.");

    private static string NotPostedText(int n) => Tr.T($"Не проведено: {n} — исправьте отмеченные строки.", $"Өткөрүлгөн жок: {n} — белгиленген саптарды оңдоңуз.",
        $"Not posted: {n} — fix the marked rows.", $"İşlenmedi: {n} — işaretli satırları düzeltin.", $"O'tkazilmadi: {n} — belgilangan qatorlarni tuzating.");

    private void RegisterBatch(string batchId)
    {
        _myBatches.Add(batchId);
        Raise(nameof(HasBackground));
        if (!_subscribed)
        {
            _subscribed = true;
            BulkReceivingFeeder.Instance.BatchChanged += OnFeederChanged;
            BulkReceivingFeeder.Instance.BatchFinished += OnFeederFinished;
        }
        // Пачка могла закончиться до регистрации (события приходят с фонового потока) — итог берём из подающего.
        if (BulkReceivingFeeder.Instance.Get(batchId) is { Finished: true } done)
            Post(() => HandleBatchFinished(done));
    }

    private void OnFeederChanged(BulkBatchInfo info) => Post(() => HandleBatchChanged(info));

    private void OnFeederFinished(BulkBatchInfo info) => Post(() => HandleBatchFinished(info));

    private void HandleBatchChanged(BulkBatchInfo info)
    {
        if (info.Finished || !_myBatches.Contains(info.Id))
            return;
        ProgressMax = info.TotalLines;
        ProgressValue = info.ProcessedLines;
        BackgroundText = Tr.T($"в фоне: {info.ProcessedLines} из {info.TotalLines}", $"фондо: {info.TotalLines} ичинен {info.ProcessedLines}",
            $"in background: {info.ProcessedLines} of {info.TotalLines}", $"arka planda: {info.ProcessedLines}/{info.TotalLines}", $"fonda: {info.TotalLines} dan {info.ProcessedLines}");
    }

    /// <summary>Пачка закончена: итог в SummaryText, не проведённые ТОЧНО строки (TakeBackLines) возвращаются в свои колонки с текстом ошибки
    /// («Повторить ошибочные»), строки «нужна проверка» остаются в «Приходах в фоне».</summary>
    private void HandleBatchFinished(BulkBatchInfo info)
    {
        if (!_myBatches.Remove(info.Id))
            return;
        if (_myBatches.Count == 0)
        {
            Unsubscribe();
            BackgroundText = "";
        }
        Raise(nameof(HasBackground));

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in info.FailedRows)
        {
            Errors.Add((f.Name.Length > 0 ? f.Name + ": " : f.Barcode.Length > 0 ? f.Barcode + ": " : "") + f.Error);
            if (f.Barcode.Length > 0 && !f.NeedsCheck && !errors.ContainsKey(f.Barcode))
                errors[f.Barcode] = f.Error;
        }
        var back = new List<ReceivingLineVm>();
        foreach (var jobId in info.FailedJobIds)
        {
            try
            {
                back.AddRange(BackgroundReceivingQueue.Instance.TakeBackLines(jobId));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: строки задачи {jobId} не возвращены: {ex.Message}", "WARNING");
            }
        }
        if (back.Count > 0)
            RestoreLines(back, l => errors.TryGetValue(l.Barcode, out var e) ? e : Tr.T("не принят", "кабыл алынган жок", "not received", "kabul edilmedi", "qabul qilinmadi"));

        var summary = info.SummaryText;
        if (info.NeedsCheck > 0)
            summary += " " + Tr.T($"Нужна проверка остатка: {info.NeedsCheck} — смотрите «Приходы в фоне».", $"Калдыкты текшерүү керек: {info.NeedsCheck} — «Фондогу кирүүлөрдү» караңыз.",
                $"Stock check needed: {info.NeedsCheck} — see “Background receivings”.", $"Stok kontrolü gerekli: {info.NeedsCheck} — «Arka plandaki mal kabulleri»ne bakın.",
                $"Qoldiqni tekshirish kerak: {info.NeedsCheck} — «Fondagi kirimlar»ga qarang.");
        SummaryText = summary;
        PosLogger.Log($"Массовое добавление: пачка {info.Id} — {info.SummaryText}; возвращено в список строк: {back.Count}.", "STOCK");
        Raise(nameof(HasFailedRows));
        ScheduleSave();
        Completed?.Invoke(info.Created, info.Received);
        BatchCompleted?.Invoke(info);
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        _subscribed = false;
        BulkReceivingFeeder.Instance.BatchChanged -= OnFeederChanged;
        BulkReceivingFeeder.Instance.BatchFinished -= OnFeederFinished;
    }

    public void Dispose() => Unsubscribe();

    /// <summary>В поток интерфейса (события подающего приходят с фонового потока). Без контекста (стенд без окна) — на месте.</summary>
    private void Post(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            action();
            return;
        }
        _ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: событие пачки не обработано: {ex}", "WARNING");
            }
        }, null);
    }

    // ------------------------------------------------------------------ JSON сервера

    private static string? JStr(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double JNum(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d) ? d : 0;

    // ------------------------------------------------------------------ черновик

    /// <summary>Сколько строк в сохранённом черновике этого магазина (0 — нет) и когда сохранён.</summary>
    public static (int Count, DateTime SavedAt) DraftInfo() => BulkScanDraftStore.Peek();

    public void RestoreDraft()
    {
        var draft = BulkScanDraftStore.LoadAll();
        for (var i = 0; i < draft.Lines.Count; i++)
        {
            var row = new BulkScanRow(draft.Lines[i]);
            row.SaleEditedManually = i < draft.SaleManual.Count && draft.SaleManual[i];
            row.Edited += ScheduleSave;
            row.GroupChanged += Regroup;
            Rows.Add(row);
            PlaceInGroup(row, top: false);
        }
        if (draft.CrmMarkup is { } markup)
            CrmMarkupPercent = markup;
    }

    public void DiscardDraft() => BulkScanDraftStore.Delete();

    public void SaveDraftNow()
    {
        if (PersistDraft)
            BulkScanDraftStore.Save(Rows.Select(r => r.Line).ToList(), Rows.Select(r => r.SaleEditedManually).ToList(), CrmMarkupPercent);
    }

    private async void ScheduleSave()
    {
        // 2026-10-24 (аудит, круг 8, С-1): любая правка строки (+/−, ввод количества) меняет «штук» — поднимаем счётчик до проверки черновика.
        RaiseStats();
        if (!PersistDraft)
            return;
        var version = ++_saveVersion;
        await Task.Delay(600).ConfigureAwait(true);
        if (version == _saveVersion)
            SaveDraftNow();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>2026-10-26 (М2): прочитанный черновик — строки, признак «продажу правили руками» по строкам и «наценка для всех».</summary>
public sealed class BulkScanDraft
{
    public List<ReceivingLineVm> Lines { get; } = new();
    public List<bool> SaleManual { get; } = new();
    public double? CrmMarkup { get; set; }
}

/// <summary>Черновик списка массового добавления — файл на этом компьютере (как журнал «Закупок»): закрыли окно, выключился свет —
/// список можно продолжить.</summary>
public static class BulkScanDraftStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для стенда проверки: компания без входа в NurCRM (по умолчанию — компания текущего входа).</summary>
    public static string? CompanyIdOverride { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases");

    private static string FilePath => Path.Combine(DataDir, "bulk-scan-draft.json");
    private static readonly object Sync = new();

    private sealed class Draft
    {
        public string CompanyId { get; set; } = "";
        public DateTime SavedAt { get; set; }
        /// <summary>2026-10-26 (М2): «Наценка для всех», % (старый черновик без поля читается как null).</summary>
        public double? CrmMarkup { get; set; }
        public List<DraftRow> Rows { get; set; } = new();
    }

    private sealed class DraftRow
    {
        public string? ProductId { get; set; }
        public string Barcode { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "шт";
        public string? Category { get; set; }
        public int Source { get; set; }
        public double Quantity { get; set; }
        public double Purchase { get; set; }
        public double Sale { get; set; }
        /// <summary>2026-10-26 (М2): продажу правили руками — «Наценка для всех» её не трогает.</summary>
        public bool SaleManual { get; set; }
        public double StockBefore { get; set; }
        public string OriginalName { get; set; } = "";
        public string OriginalUnit { get; set; } = "";
        public double OriginalPurchase { get; set; }
        public double OriginalSale { get; set; }
    }

    private static string CurrentCompany => CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";

    private static Draft? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var draft = JsonSerializer.Deserialize<Draft>(File.ReadAllText(FilePath));
            if (draft is null || draft.Rows.Count == 0)
                return null;
            // Черновик другого магазина (вход под другим аккаунтом) не показываем.
            if (draft.CompanyId.Length > 0 && CurrentCompany.Length > 0
                && !string.Equals(draft.CompanyId, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                return null;
            return draft;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: черновик не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }

    public static (int Count, DateTime SavedAt) Peek()
    {
        lock (Sync)
        {
            var draft = Read();
            return draft is null ? (0, default) : (draft.Rows.Count, draft.SavedAt);
        }
    }

    public static BulkScanDraft LoadAll()
    {
        lock (Sync)
        {
            var result = new BulkScanDraft();
            var draft = Read();
            if (draft is null)
                return result;
            result.CrmMarkup = draft.CrmMarkup;
            foreach (var r in draft.Rows)
            {
                result.Lines.Add(new ReceivingLineVm
                {
                    ProductId = r.ProductId,
                    Barcode = r.Barcode,
                    ProductName = r.Name,
                    Unit = r.Unit,
                    CategoryName = r.Category,
                    Source = (ReceivingSource)r.Source,
                    Quantity = r.Quantity,
                    PurchasePrice = r.Purchase,
                    SalePrice = r.Sale,
                    StockBefore = r.StockBefore,
                    OriginalName = r.OriginalName,
                    OriginalUnit = r.OriginalUnit,
                    OriginalPurchasePrice = r.OriginalPurchase,
                    OriginalSalePrice = r.OriginalSale,
                });
                result.SaleManual.Add(r.SaleManual);
            }
            return result;
        }
    }

    public static List<ReceivingLineVm> Load() => LoadAll().Lines;

    public static void Save(IReadOnlyList<ReceivingLineVm> lines, IReadOnlyList<bool>? saleManual = null, double? crmMarkup = null)
    {
        lock (Sync)
        {
            try
            {
                if (lines.Count == 0)
                {
                    Delete();
                    return;
                }
                var draft = new Draft
                {
                    CompanyId = CurrentCompany,
                    SavedAt = DateTime.Now,
                    CrmMarkup = crmMarkup,
                    Rows = lines.Select((l, i) => new DraftRow
                    {
                        ProductId = l.ProductId,
                        Barcode = l.Barcode,
                        Name = l.ProductName,
                        Unit = l.Unit,
                        Category = l.CategoryName,
                        Source = (int)l.Source,
                        Quantity = l.Quantity,
                        Purchase = l.PurchasePrice,
                        Sale = l.SalePrice,
                        SaleManual = saleManual is not null && i < saleManual.Count && saleManual[i],
                        StockBefore = l.StockBefore,
                        OriginalName = l.OriginalName,
                        OriginalUnit = l.OriginalUnit,
                        OriginalPurchase = l.OriginalPurchasePrice,
                        OriginalSale = l.OriginalSalePrice,
                    }).ToList(),
                };
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(draft));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: черновик не записан ({ex.Message}).", "WARNING");
            }
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: черновик не удалён ({ex.Message}).", "WARNING");
        }
    }
}
