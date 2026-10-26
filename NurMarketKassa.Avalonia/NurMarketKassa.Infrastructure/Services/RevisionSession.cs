using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-24, ТЗ «Склад — ревизия, приёмка, поставщик, Telegram-группа», пакет Р1: движок ревизии «скан → факт» БЕЗ окна.
// Весь склад сразу (кроме услуг и комплектов) с учётным остатком; пикнул штрихкод → строка найдена (индекс словарём, не перебор);
// факт вводится числом (режим по умолчанию) или «каждый скан = +1»; расхождение = факт − учёт на момент пересчёта; черновик на диске;
// «Применить» — частями по 500 строк существующим актом ревизии (IInventoryApiService.CreateSessionAsync + ApplySessionAsync,
// allow_negative: false), номера актов запоминаются в черновик СРАЗУ после создания — повтор после обрыва не создаёт второй акт.
// Остатки товаров здесь НЕ пишутся: ни денег, ни остатков своей логики нет, только числа для акта.
// Окно (пакет Р2) пользуется только открытым API: RevisionSession / RevisionDraftStore / RevisionActStore.
// Поправка на продажи (ТЗ 2.3): в акт уходит Факт + (учёт сейчас − учёт в момент ввода факта), включена флагом SalesCorrection.
// Не посчитанные позиции в акт НЕ входят (владелец выбрал «не обнулять»).

public enum RevisionFilter
{
    All,
    Counted,
    NotCounted,
    /// <summary>Посчитаны, и факт не равен учёту.</summary>
    WithDifference,
}

/// <summary>Что делает скан: EnterNumber — только находит строку (число вводят в поле «Факт»), PlusOne — каждый скан прибавляет 1 (или число в упаковке).</summary>
public enum RevisionScanMode
{
    EnterNumber,
    PlusOne,
}

public enum RevisionMatchKind
{
    Barcode,
    AlternateBarcode,
    Article,
    ProductId,
    External,
}

public enum RevisionScanStatus
{
    /// <summary>Пустой код.</summary>
    Ignored,
    NotFound,
    Found,
    /// <summary>Строку менять нельзя: товар с размерами (пересчёт по размерам в карточке), ревизия завершена или уже отправляется.</summary>
    Locked,
}

public enum RevisionWarningKind
{
    /// <summary>После поправки на продажи вышло меньше нуля — отправлено 0.</summary>
    NegativeToZero,
    /// <summary>Сервер принимает 2 знака после запятой — число округлено (0,711 → 0,71).</summary>
    RoundedTo2,
    /// <summary>Товара больше нет в каталоге — строка в акт не вошла.</summary>
    MissingProduct,
}

public sealed record RevisionMatch(RevisionRow Row, RevisionMatchKind Kind, double PackageQuantity);

/// <summary>Результат скана. Added — сколько прибавлено (режим PlusOne), иначе 0; PreviousFact — факт до скана (null — ещё не считали).</summary>
public sealed record RevisionScanResult(RevisionScanStatus Status, RevisionMatch? Match, double? PreviousFact, double Added, string Code);

public sealed record RevisionWarning(RevisionWarningKind Kind, string ProductId, string ProductName, string Text);

/// <summary>Что и сколько уйдёт в акт по одной позиции.</summary>
public sealed record RevisionPlanItem(RevisionRow Row, double Fact, double SalesCorrection, double Send);

public sealed class RevisionPreview
{
    public List<RevisionPlanItem> Items { get; } = new();
    public List<RevisionWarning> Warnings { get; } = new();
    /// <summary>Сколько позиций продавалось за время ревизии (поправка ≠ 0) — «Продано во время ревизии: N товаров».</summary>
    public int SoldDuringCount { get; set; }
    /// <summary>Посчитанные, но не входящие в акт (товара нет в каталоге).</summary>
    public int Skipped { get; set; }
}

public sealed record RevisionTotals(
    int Total, int Counted, int WithDifference, int NotCounted,
    double ShortageQty, double ShortageAtSale, double ShortageAtCost,
    double SurplusQty, double SurplusAtSale, double SurplusAtCost,
    int ReadOnly)
{
    public double NetQty => SurplusQty - ShortageQty;
    public double NetAtSale => SurplusAtSale - ShortageAtSale;
    public double NetAtCost => SurplusAtCost - ShortageAtCost;
}

/// <summary>Одна строка ревизии (один товар каталога). Меняется только через RevisionSession.</summary>
public sealed class RevisionRow : INotifyPropertyChanged
{
    private double _expected;
    private double _expectedAtCount;
    private double? _fact;
    private DateTime? _countedAt;

    internal RevisionRow(CatalogProductTileVm tile, bool readOnly, bool isAdded)
    {
        ProductId = tile.Id;
        Name = tile.Title ?? "";
        Barcode = tile.Barcode?.Trim() ?? "";
        Article = tile.Article?.Trim() ?? "";
        Category = tile.Category?.Trim() ?? "";
        Unit = tile.Unit?.Trim() ?? "";
        IsWeighted = tile.IsWeighted;
        SalePrice = tile.PriceValue;
        PurchasePrice = tile.PurchasePrice;
        IsReadOnly = readOnly;
        IsAdded = isAdded;
        InCatalog = true;
        _expected = tile.Quantity;
        _expectedAtCount = tile.Quantity;
        SearchKey = (Name + " " + Barcode + " " + Article + " " + (tile.ProductCode ?? "") + " " + (tile.AlternateBarcodesRaw ?? "")).ToLowerInvariant();
    }

    public string ProductId { get; }
    public string Name { get; }
    public string Barcode { get; }
    public string Article { get; }
    public string Category { get; }
    public string Unit { get; }
    public bool IsWeighted { get; }
    public double SalePrice { get; }
    public double PurchasePrice { get; }

    /// <summary>Товар с размерами: пересчитывается по размерам в карточке, в акт не входит.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Строка добавлена во время ревизии (товар создан по незнакомому штрихкоду).</summary>
    public bool IsAdded { get; }

    /// <summary>Товар есть в последнем обновлении каталога; false — его удалили, в акт строка не пойдёт.</summary>
    public bool InCatalog { get; internal set; }

    /// <summary>Нижний регистр: название, штрихкоды, артикул — для поиска.</summary>
    internal string SearchKey { get; }

    /// <summary>Учёт сейчас (из каталога; обновляется при RefreshExpected).</summary>
    public double Expected
    {
        get => _expected;
        internal set
        {
            if (Math.Abs(_expected - value) < 1e-9)
                return;
            _expected = value;
            if (_fact is null)
                _expectedAtCount = value;
            Raise(nameof(Expected));
            Raise(nameof(SalesSinceCount));
        }
    }

    /// <summary>Учёт в момент ввода факта (для непосчитанной строки равен Expected).</summary>
    public double ExpectedAtCount => _fact is null ? _expected : _expectedAtCount;

    public double? Fact => _fact;
    public DateTime? CountedAt => _countedAt;
    public bool IsCounted => _fact.HasValue;

    /// <summary>Факт − учёт на момент пересчёта (0, если не считали).</summary>
    public double Difference => _fact is { } f ? Math.Round(f - _expectedAtCount, 3) : 0;

    public bool HasDifference => _fact is not null && Math.Abs(Difference) > 0.0005;

    /// <summary>Сколько продано (минус — продано) с момента ввода факта: Учёт сейчас − Учёт на момент пересчёта.</summary>
    public double SalesSinceCount => _fact is null ? 0 : Math.Round(_expected - _expectedAtCount, 3);

    internal void SetCounted(double? fact, DateTime? at, double expectedAtCount)
    {
        _fact = fact;
        _countedAt = at;
        _expectedAtCount = expectedAtCount;
        Raise(nameof(Fact));
        Raise(nameof(CountedAt));
        Raise(nameof(IsCounted));
        Raise(nameof(Difference));
        Raise(nameof(HasDifference));
        Raise(nameof(ExpectedAtCount));
        Raise(nameof(SalesSinceCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}

public sealed class RevisionOptions
{
    /// <summary>Поправка на продажи во время ревизии (ТЗ 2.3). По умолчанию включена; на восстановленной ревизии — как в черновике.</summary>
    public bool? SalesCorrection { get; set; }

    /// <summary>Id товаров с размерами (RevisionSession.SizedProductIdsFromIndex() в сфере «Одежда»): они только для чтения.</summary>
    public IEnumerable<string>? SizedProductIds { get; set; }

    /// <summary>Запасной поиск для кодов, которых нет в индексе (штрихкод размера, весовая этикетка): код → id товара.</summary>
    public Func<string, string?>? ExternalResolver { get; set; }

    /// <summary>Сколько строк в одном акте. 500 — как просит ТЗ; сервер письменно лимит не подтвердил (Н-ревизия-1).</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>Писать черновик на диск (на стенде проверки можно выключить).</summary>
    public bool PersistDraft { get; set; } = true;
}

public sealed class RevisionRestoreReport
{
    public int Restored { get; set; }
    /// <summary>В черновике были, а в каталоге их больше нет.</summary>
    public int MissingProducts { get; set; }
}

public enum RevisionApplyStatus
{
    /// <summary>Всё проведено.</summary>
    Done,
    /// <summary>Остановились (ошибка/связь/отмена): повторный ApplyAsync продолжит с первой непроведённой части.</summary>
    Failed,
    /// <summary>Нечего отправлять (ничего не посчитано).</summary>
    NothingToSend,
}

public sealed class RevisionApplyResult
{
    public RevisionApplyStatus Status { get; set; }
    public int PartsTotal { get; set; }
    public int PartsApplied { get; set; }
    public int SentRows { get; set; }
    public int SoldDuringCount { get; set; }
    public string ErrorMessage { get; set; } = "";
    public List<string> SessionIds { get; set; } = new();
    public List<RevisionWarning> Warnings { get; set; } = new();
    /// <summary>Имя файла локального акта (RevisionActStore) — после Done.</summary>
    public string ActFile { get; set; } = "";
}

public sealed record RevisionApplyProgress(int Part, int PartsTotal, string Stage);

/// <summary>Сессия ревизии. Все методы изменения — с потока интерфейса (внутри есть замок, но порядок событий рассчитан на один поток).</summary>
public sealed class RevisionSession
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object _gate = new();
    private readonly List<RevisionRow> _rows = new();
    private readonly Dictionary<string, RevisionRow> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RevisionRow Row, double Pack)> _byBarcode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RevisionRow Row, double Pack)> _byAlternate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RevisionRow> _byArticle = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sized;
    private readonly Func<string, string?>? _external;
    private readonly List<PlanPart> _plan = new();
    private int _counted;
    private int _saveVersion;
    private bool _closed;
    private Task<RevisionApplyResult>? _applyTask;
    private RevisionApplyResult? _doneResult;

    private RevisionSession(RevisionOptions options)
    {
        _sized = new HashSet<string>(options.SizedProductIds ?? [], StringComparer.OrdinalIgnoreCase);
        _external = options.ExternalResolver;
        ChunkSize = Math.Max(1, options.ChunkSize);
        PersistDraft = options.PersistDraft;
        _salesCorrection = options.SalesCorrection ?? true;
        Id = Guid.NewGuid().ToString("N");
        StartedAt = DateTime.Now;
        StockAt = StartedAt;
        UserName = SafeUserName();
    }

    // ------------------------------------------------------------------ создание

    /// <summary>Новая ревизия по всему каталогу. Услуги и комплекты в список не попадают. 15 000 товаров — десятки миллисекунд.</summary>
    public static RevisionSession Start(IEnumerable<CatalogProductTileVm> catalog, RevisionOptions? options = null, DateTime? stockAt = null)
    {
        var s = new RevisionSession(options ?? new RevisionOptions());
        if (stockAt is { } at)
            s.StockAt = at;
        s.Fill(catalog);
        return s;
    }

    /// <summary>Продолжить ревизию из черновика (RevisionDraftStore.Peek() не null). null — черновика нет / он чужого магазина.</summary>
    public static RevisionSession? Restore(IEnumerable<CatalogProductTileVm> catalog, RevisionOptions? options = null, RevisionRestoreReport? report = null)
    {
        var draft = RevisionDraftStore.Load();
        if (draft is null)
            return null;
        options ??= new RevisionOptions();
        options.SalesCorrection ??= draft.SalesCorrection;
        var s = new RevisionSession(options)
        {
            Id = string.IsNullOrEmpty(draft.Id) ? Guid.NewGuid().ToString("N") : draft.Id,
            StartedAt = draft.StartedAt,
            StockAt = draft.StockAt,
            UserName = string.IsNullOrEmpty(draft.User) ? SafeUserName() : draft.User,
        };
        s.Fill(catalog);
        report ??= new RevisionRestoreReport();
        lock (s._gate)
        {
            foreach (var c in draft.Counts)
            {
                if (c.P is null || !s._byId.TryGetValue(c.P, out var row) || row.IsReadOnly)
                {
                    report.MissingProducts++;
                    continue;
                }

                if (!row.IsCounted)
                    s._counted++;
                row.SetCounted(c.F, c.T, c.E);
                report.Restored++;
            }

            if (draft.Plan is { Count: > 0 })
                s._plan.AddRange(draft.Plan.Select(PlanPart.FromDto));
        }

        return s;
    }

    private void Fill(IEnumerable<CatalogProductTileVm> catalog)
    {
        lock (_gate)
        {
            foreach (var tile in catalog)
                AddTile(tile, isAdded: false);
        }
    }

    private RevisionRow? AddTile(CatalogProductTileVm tile, bool isAdded)
    {
        if (string.IsNullOrWhiteSpace(tile.Id) || tile.IsService || tile.IsBundle
            || string.Equals(tile.Kind, "bundle", StringComparison.OrdinalIgnoreCase)
            || _byId.ContainsKey(tile.Id))
            return null;

        var row = new RevisionRow(tile, _sized.Contains(tile.Id), isAdded);
        _rows.Add(row);
        _byId[row.ProductId] = row;

        // Дубль штрихкода — как в ProductCodeLookup: товар с остатком важнее.
        if (row.Barcode.Length > 0
            && (!_byBarcode.TryGetValue(row.Barcode, out var old) || (old.Row.Expected <= 0 && row.Expected > 0)))
            _byBarcode[row.Barcode] = (row, 1);

        if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
        {
            foreach (var alt in tile.AlternateBarcodesRaw.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _byAlternate.TryAdd(alt, (row, 1));
        }

        if (tile.AlternateBarcodeVariants is { } variants)
        {
            // Штрихкод упаковки: Quantity — сколько штук в упаковке (подсказка «Упаковка ×12»; в режиме +1 прибавляется столько).
            foreach (var v in variants)
            {
                var code = v.Barcode?.Trim();
                if (!string.IsNullOrEmpty(code))
                    _byAlternate[code] = (row, v.Quantity > 0 ? v.Quantity : 1);
            }
        }

        foreach (var art in new[] { tile.Article?.Trim(), tile.ProductCode?.Trim() })
        {
            if (art is { Length: >= 3 })
                _byArticle.TryAdd(art, row);
        }

        return row;
    }

    /// <summary>Id товаров с размерами из справочника кассы (сфера «Одежда») — для RevisionOptions.SizedProductIds.</summary>
    public static List<string> SizedProductIdsFromIndex()
    {
        try
        {
            return VariantBarcodeIndex.AllVariants().Select(v => v.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: справочник размеров не прочитан ({ex.Message}).", "WARNING");
            return [];
        }
    }

    private static string SafeUserName()
    {
        try
        {
            return PosApp.CurrentUserDisplayName ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ свойства

    public string Id { get; private set; }
    public DateTime StartedAt { get; private set; }
    /// <summary>На какой момент взяты остатки каталога («Остатки на 14:05»).</summary>
    public DateTime StockAt { get; private set; }
    public string UserName { get; private set; }
    public int ChunkSize { get; }
    public bool PersistDraft { get; set; }

    private bool _salesCorrection;

    /// <summary>Поправка на продажи (ТЗ 2.3). Менять можно, пока не создан первый акт.</summary>
    public bool SalesCorrection
    {
        get => _salesCorrection;
        set
        {
            if (_salesCorrection == value || IsPostingStarted)
                return;
            _salesCorrection = value;
            ScheduleSave();
        }
    }

    /// <summary>Строки в порядке каталога. Список меняется только добавлением (AddCatalogProduct); не менять снаружи.</summary>
    public IReadOnlyList<RevisionRow> Rows => _rows;

    public int CountedCount => _counted;

    /// <summary>Хоть один акт уже создан на сервере: пересчёт заблокирован, «Применить» только доводит дело до конца.</summary>
    public bool IsPostingStarted
    {
        get
        {
            lock (_gate)
                return _plan.Any(p => p.SessionId is not null);
        }
    }

    /// <summary>Ревизия проведена целиком.</summary>
    public bool IsCompleted => _doneResult is not null;

    public bool IsApplying => _applyTask is { IsCompleted: false };

    /// <summary>Строки менять нельзя (завершена / закрыта / акты уже созданы).</summary>
    public bool IsLocked => _closed || IsCompleted || IsPostingStarted;

    /// <summary>Любое изменение счёта — окно обновляет «Посчитано N из M».</summary>
    public event Action? Changed;

    /// <summary>Часть акта проведена: окно/хозяин обновляют остаток товара в каталоге (RevisionSession.ApplyToLocalCatalog).</summary>
    public Action<string, double>? StockApplied { get; set; }

    // ------------------------------------------------------------------ поиск

    /// <summary>Найти строку по штрихкоду со сканера: основной → доп./упаковка → артикул/код → id → ExternalResolver. Словарь, не перебор: ≤ 1 мс.</summary>
    public RevisionMatch? FindByCode(string? raw)
    {
        var key = ProductCodeLookup.Normalize(raw);
        if (key.Length == 0)
            return null;
        var candidates = ProductCodeLookup.Candidates(key);
        lock (_gate)
        {
            foreach (var c in candidates)
                if (_byBarcode.TryGetValue(c, out var hit))
                    return new RevisionMatch(hit.Row, RevisionMatchKind.Barcode, hit.Pack);
            foreach (var c in candidates)
                if (_byAlternate.TryGetValue(c, out var hit))
                    return new RevisionMatch(hit.Row, RevisionMatchKind.AlternateBarcode, hit.Pack);
            foreach (var c in candidates)
                if (c.Length >= 3 && _byArticle.TryGetValue(c, out var art))
                    return new RevisionMatch(art, RevisionMatchKind.Article, 1);
            foreach (var c in candidates)
                if (_byId.TryGetValue(c, out var byId))
                    return new RevisionMatch(byId, RevisionMatchKind.ProductId, 1);
        }

        if (_external is not null)
        {
            string? id = null;
            try
            {
                id = _external(key);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: запасной поиск по коду не сработал ({ex.Message}).", "WARNING");
            }

            if (id is not null)
            {
                lock (_gate)
                    if (_byId.TryGetValue(id, out var ext))
                        return new RevisionMatch(ext, RevisionMatchKind.External, 1);
            }
        }

        return null;
    }

    public RevisionRow? FindById(string? productId)
    {
        if (string.IsNullOrEmpty(productId))
            return null;
        lock (_gate)
            return _byId.TryGetValue(productId, out var row) ? row : null;
    }

    /// <summary>Скан: находит строку; в режиме PlusOne сразу прибавляет 1 (или число в упаковке).</summary>
    public RevisionScanResult Scan(string? raw, RevisionScanMode mode = RevisionScanMode.EnterNumber)
    {
        var code = ProductCodeLookup.Normalize(raw);
        if (code.Length == 0)
            return new RevisionScanResult(RevisionScanStatus.Ignored, null, null, 0, code);
        var match = FindByCode(code);
        if (match is null)
            return new RevisionScanResult(RevisionScanStatus.NotFound, null, null, 0, code);
        var previous = match.Row.Fact;
        if (match.Row.IsReadOnly || IsLocked)
            return new RevisionScanResult(RevisionScanStatus.Locked, match, previous, 0, code);
        if (mode == RevisionScanMode.PlusOne)
        {
            var step = match.PackageQuantity > 0 ? match.PackageQuantity : 1;
            if (!AddFact(match.Row, step))
                return new RevisionScanResult(RevisionScanStatus.Locked, match, previous, 0, code);
            return new RevisionScanResult(RevisionScanStatus.Found, match, previous, step, code);
        }

        return new RevisionScanResult(RevisionScanStatus.Found, match, previous, 0, code);
    }

    /// <summary>Товар, созданный по незнакомому штрихкоду (или появившийся в каталоге): строка с его учётом (у нового — 0).</summary>
    public RevisionRow? AddCatalogProduct(CatalogProductTileVm tile)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(tile.Id, out var existing))
                return existing;
            return AddTile(tile, isAdded: true);
        }
    }

    // ------------------------------------------------------------------ ввод факта

    private static double Quantize(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    /// <summary>Записать факт (заменяет прежний). Учёт запоминается на этот момент. false — нельзя (заблокировано, число плохое).</summary>
    public bool SetFact(RevisionRow row, double value, DateTime? now = null)
    {
        if (!IsValidQty(value))
            return false;
        lock (_gate)
        {
            if (IsLockedUnsafe || row.IsReadOnly)
                return false;
            if (!row.IsCounted)
                _counted++;
            row.SetCounted(Quantize(value), now ?? DateTime.Now, row.Expected);
        }

        OnChanged();
        return true;
    }

    /// <summary>Прибавить к факту (товар на двух полках; «+3»). Нельзя уйти ниже нуля.</summary>
    public bool AddFact(RevisionRow row, double delta, DateTime? now = null)
    {
        if (!double.IsFinite(delta))
            return false;
        lock (_gate)
        {
            if (IsLockedUnsafe || row.IsReadOnly)
                return false;
            var next = Quantize((row.Fact ?? 0) + delta);
            if (!IsValidQty(next))
                return false;
            if (!row.IsCounted)
                _counted++;
            row.SetCounted(next, now ?? DateTime.Now, row.Expected);
        }

        OnChanged();
        return true;
    }

    /// <summary>Снять отметку «посчитано» (ошибся при вводе).</summary>
    public bool ClearFact(RevisionRow row)
    {
        lock (_gate)
        {
            if (IsLockedUnsafe || !row.IsCounted)
                return false;
            _counted--;
            row.SetCounted(null, null, row.Expected);
        }

        OnChanged();
        return true;
    }

    /// <summary>Разбор того, что набрали в поле «Факт»: «5» (и «5,5») — заменить, «+3» — прибавить. Пустая строка — ничего.</summary>
    public static bool TryParseEntry(string? text, out double value, out bool isAdd)
    {
        value = 0;
        isAdd = false;
        var t = (text ?? "").Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0)
            return false;
        if (t[0] == '+')
        {
            isAdd = true;
            t = t[1..];
        }
        else if (t[0] == '-' && t.Length > 1)
        {
            // «-2» — убрать лишнее (как «+(-2)»); итог ниже нуля SetFact/AddFact не пропустят.
            isAdd = true;
        }

        return double.TryParse(t, NumberStyles.Float, Inv, out value) && double.IsFinite(value) && Math.Abs(value) <= 1e9;
    }

    /// <summary>Записать то, что набрали в поле «Факт» (разбор TryParseEntry). Возвращает true, если счёт изменился.</summary>
    public bool CommitEntry(RevisionRow row, string? text, DateTime? now = null) =>
        TryParseEntry(text, out var v, out var add) && (add ? AddFact(row, v, now) : SetFact(row, v, now));

    private static bool IsValidQty(double v) => double.IsFinite(v) && v >= 0 && v <= 1e9;

    private bool IsLockedUnsafe => _closed || _doneResult is not null || _plan.Any(p => p.SessionId is not null);

    private void OnChanged()
    {
        Changed?.Invoke();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ учёт из каталога

    /// <summary>Обновить учёт из свежего каталога (после CatalogCacheService.RefreshFromApiAsync): продажи за время ревизии
    /// сдвигают Expected, а ExpectedAtCount посчитанных строк остаётся — на этом держится поправка. Новые товары каталога
    /// добавляются непосчитанными, пропавшие помечаются InCatalog = false.</summary>
    public (int Updated, int Added, int Missing) RefreshExpected(IEnumerable<CatalogProductTileVm> catalog, DateTime? stockAt = null)
    {
        int updated = 0, added = 0, missing = 0;
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tile in catalog)
            {
                if (string.IsNullOrWhiteSpace(tile.Id))
                    continue;
                if (_byId.TryGetValue(tile.Id, out var row))
                {
                    seen.Add(row.ProductId);
                    row.InCatalog = true;
                    if (Math.Abs(row.Expected - tile.Quantity) > 1e-9)
                        updated++;
                    row.Expected = tile.Quantity;
                }
                else if (AddTile(tile, isAdded: false) is { } fresh)
                {
                    seen.Add(fresh.ProductId);
                    added++;
                }
            }

            foreach (var row in _rows)
            {
                if (!seen.Contains(row.ProductId) && row.InCatalog)
                {
                    row.InCatalog = false;
                    missing++;
                }
            }

            if (stockAt is { } at)
                StockAt = at;
        }

        Changed?.Invoke();
        return (updated, added, missing);
    }

    // ------------------------------------------------------------------ списки и итоги

    /// <summary>Строки по фильтру, поиску (слова через пробел — все должны встретиться в названии/штрихкоде/артикуле) и категории.</summary>
    public List<RevisionRow> Filter(RevisionFilter filter = RevisionFilter.All, string? search = null, string? category = null)
    {
        var tokens = (search ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cat = category?.Trim();
        var result = new List<RevisionRow>(_rows.Count);
        lock (_gate)
        {
            foreach (var r in _rows)
            {
                switch (filter)
                {
                    case RevisionFilter.Counted when !r.IsCounted:
                    case RevisionFilter.NotCounted when r.IsCounted:
                    case RevisionFilter.WithDifference when !r.HasDifference:
                        continue;
                }

                if (!string.IsNullOrEmpty(cat) && !string.Equals(r.Category, cat, StringComparison.OrdinalIgnoreCase))
                    continue;
                var ok = true;
                foreach (var t in tokens)
                {
                    if (!r.SearchKey.Contains(t, StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    result.Add(r);
            }
        }

        return result;
    }

    public List<string> Categories()
    {
        lock (_gate)
            return _rows.Select(r => r.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Итоги для подвала и окна «Завершить ревизию». Один проход, миллисекунды на 15 000.</summary>
    public RevisionTotals GetTotals()
    {
        int counted = 0, withDiff = 0, readOnly = 0;
        double shQ = 0, shS = 0, shC = 0, suQ = 0, suS = 0, suC = 0;
        lock (_gate)
        {
            foreach (var r in _rows)
            {
                if (r.IsReadOnly)
                    readOnly++;
                if (!r.IsCounted)
                    continue;
                counted++;
                if (!r.HasDifference)
                    continue;
                withDiff++;
                var d = r.Difference;
                if (d < 0)
                {
                    shQ += -d;
                    shS += -d * r.SalePrice;
                    shC += -d * r.PurchasePrice;
                }
                else
                {
                    suQ += d;
                    suS += d * r.SalePrice;
                    suC += d * r.PurchasePrice;
                }
            }

            return new RevisionTotals(_rows.Count, counted, withDiff, _rows.Count - counted,
                Math.Round(shQ, 3), Math.Round(shS, 2), Math.Round(shC, 2),
                Math.Round(suQ, 3), Math.Round(suS, 2), Math.Round(suC, 2), readOnly);
        }
    }

    // ------------------------------------------------------------------ что уйдёт в акт

    /// <summary>Сколько отправить в акт по посчитанной строке: Факт + (учёт сейчас − учёт при вводе), не ниже 0, 2 знака.</summary>
    private (double Send, double Correction, RevisionWarningKind? Warn, double Raw) ComputeSend(RevisionRow r)
    {
        var correction = _salesCorrection && r.InCatalog ? Quantize(r.Expected - r.ExpectedAtCount) : 0;
        var raw = Quantize((r.Fact ?? 0) + correction);
        if (raw < 0)
            return (0, correction, RevisionWarningKind.NegativeToZero, raw);
        var send = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
        return (send, correction, Math.Abs(send - raw) > 0.0004 ? RevisionWarningKind.RoundedTo2 : null, raw);
    }

    private static RevisionWarning MakeWarning(RevisionWarningKind kind, RevisionRow r, double raw, double send)
    {
        var n = r.Name;
        var text = kind switch
        {
            RevisionWarningKind.NegativeToZero => Tr.T(
                $"«{n}»: после поправки на продажи вышло меньше нуля — отправлено 0.",
                $"«{n}»: сатууга оңдоодон кийин нөлдөн аз чыкты — 0 жөнөтүлдү.",
                $"“{n}”: below zero after the sales adjustment — 0 sent.",
                $"“{n}”: satış düzeltmesinden sonra sıfırın altına düştü — 0 gönderildi.",
                $"«{n}»: sotuvlar tuzatishidan keyin noldan kam chiqdi — 0 yuborildi."),
            RevisionWarningKind.RoundedTo2 => Tr.T(
                $"«{n}»: {raw.ToString("0.###", Inv)} округлено до {send.ToString("0.##", Inv)} (сервер принимает 2 знака).",
                $"«{n}»: {raw.ToString("0.###", Inv)} {send.ToString("0.##", Inv)} чейин тегизделди (сервер 2 белги кабыл алат).",
                $"“{n}”: {raw.ToString("0.###", Inv)} rounded to {send.ToString("0.##", Inv)} (the server accepts 2 decimals).",
                $"“{n}”: {raw.ToString("0.###", Inv)} değeri {send.ToString("0.##", Inv)} olarak yuvarlandı (sunucu 2 ondalık kabul eder).",
                $"«{n}»: {raw.ToString("0.###", Inv)} {send.ToString("0.##", Inv)} gacha yaxlitlandi (server 2 xona qabul qiladi)."),
            _ => Tr.T(
                $"«{n}»: товара больше нет в каталоге — строка в акт не вошла.",
                $"«{n}»: товар каталогто калган жок — сап актыга кирген жок.",
                $"“{n}”: the product is no longer in the catalog — the line was left out of the document.",
                $"“{n}”: ürün artık katalogda yok — satır tutanağa girmedi.",
                $"«{n}»: mahsulot endi katalogda yo'q — qator aktga kirmadi."),
        };
        return new RevisionWarning(kind, r.ProductId, n, text);
    }

    /// <summary>Заранее посмотреть, что уйдёт в акт (окно итога): позиции, поправка на продажи, предупреждения. Ничего не меняет.</summary>
    public RevisionPreview Preview()
    {
        var p = new RevisionPreview();
        lock (_gate)
        {
            foreach (var r in _rows)
            {
                if (!r.IsCounted || r.IsReadOnly)
                    continue;
                if (!r.InCatalog)
                {
                    p.Skipped++;
                    p.Warnings.Add(MakeWarning(RevisionWarningKind.MissingProduct, r, 0, 0));
                    continue;
                }

                var (send, corr, warn, raw) = ComputeSend(r);
                if (Math.Abs(corr) > 0.0004)
                    p.SoldDuringCount++;
                if (warn is { } w)
                    p.Warnings.Add(MakeWarning(w, r, raw, send));
                p.Items.Add(new RevisionPlanItem(r, r.Fact ?? 0, corr, send));
            }
        }

        return p;
    }

    // ------------------------------------------------------------------ проведение

    private sealed class PlanPart
    {
        public List<string> Ids { get; set; } = new();
        public List<double> Sent { get; set; } = new();
        public string? SessionId { get; set; }
        public bool Applied { get; set; }
        public DateTime? CreatedAt { get; set; }

        public static PlanPart FromDto(RevisionDraftPart d) => new()
        {
            Ids = d.Ids ?? new(),
            Sent = d.Sent ?? new(),
            SessionId = string.IsNullOrEmpty(d.SessionId) ? null : d.SessionId,
            Applied = d.Applied,
            CreatedAt = d.CreatedAt,
        };

        public RevisionDraftPart ToDto() => new()
        {
            Ids = new List<string>(Ids),
            Sent = new List<double>(Sent),
            SessionId = SessionId,
            Applied = Applied,
            CreatedAt = CreatedAt,
        };
    }

    /// <summary>Проверка перед «Применить»: можно ли ещё передумать (ни один акт не проведён). Брошенные черновики актов на сервере остаются непроведёнными и ничего не меняют.</summary>
    public bool CanAbandonPosting
    {
        get
        {
            lock (_gate)
                return !_plan.Any(p => p.Applied) && _doneResult is null;
        }
    }

    /// <summary>Отказаться от начатой отправки (акты на сервере не проведены): пересчёт снова разблокируется.</summary>
    public bool AbandonPosting()
    {
        lock (_gate)
        {
            if (_plan.Any(p => p.Applied) || _doneResult is not null || IsApplying)
                return false;
            _plan.Clear();
        }

        ScheduleSave();
        return true;
    }

    /// <summary>
    /// «Применить»: акты по 500 строк, каждый — создать + провести (allow_negative: false). Безопасно к повтору:
    /// созданный акт запоминается в черновик сразу; повтор не создаёт его второй раз, а проверяет состояние (GetSessionAsync)
    /// и доводит проведение. Второй вызов, пока идёт первый, возвращает тот же результат. После Done — тот же результат без отправки.
    /// refreshCatalog — обновить каталог перед отправкой (вернуть свежие товары): нужно для поправки на продажи; сбой обновления
    /// останавливает «Применить» (со старым учётом поправка была бы неверной). Вызывается при каждом запуске, пока есть что создавать.
    /// ВАЖНО: после первого await работа идёт на фоновых потоках — события Changed и обратный вызов StockApplied приходят не с потока окна
    /// (окно переводит их на свой поток); IProgress<T> сам возвращается на поток, где создан.
    /// </summary>
    public Task<RevisionApplyResult> ApplyAsync(
        IInventoryApiService api,
        Func<CancellationToken, Task<IReadOnlyList<CatalogProductTileVm>>>? refreshCatalog = null,
        IProgress<RevisionApplyProgress>? progress = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_doneResult is { } done)
                return Task.FromResult(done);
            if (_applyTask is { IsCompleted: false } running)
                return running;
            return _applyTask = ApplyCoreAsync(api, refreshCatalog, progress, ct);
        }
    }

    private async Task<RevisionApplyResult> ApplyCoreAsync(
        IInventoryApiService api,
        Func<CancellationToken, Task<IReadOnlyList<CatalogProductTileVm>>>? refreshCatalog,
        IProgress<RevisionApplyProgress>? progress,
        CancellationToken ct)
    {
        // Сразу вернуть управление вызывающему: замок _gate держится только на проверке «уже идёт», а refreshCatalog вызывается вне замка.
        await Task.Yield();
        var result = new RevisionApplyResult();
        try
        {
            // 1. Свежий учёт — если есть что ещё создавать в акты (для уже созданных актов учёт не нужен).
            if (refreshCatalog is not null && HasUncreatedWork())
            {
                progress?.Report(new RevisionApplyProgress(0, 0, "refresh"));
                IReadOnlyList<CatalogProductTileVm> fresh;
                try
                {
                    fresh = await refreshCatalog(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Status = RevisionApplyStatus.Failed;
                    result.ErrorMessage = Tr.T($"Не удалось обновить остатки с сервера: {ex.Message}", $"Серверден калдыктарды жаңыртуу мүмкүн болгон жок: {ex.Message}",
                        $"Could not refresh stock from the server: {ex.Message}", $"Stoklar sunucudan güncellenemedi: {ex.Message}", $"Qoldiqlarni serverdan yangilab bo'lmadi: {ex.Message}");
                    return result;
                }

                RefreshExpected(fresh);
            }

            // 2. План: созданные части остаются как есть, остальное пересобирается из посчитанных строк.
            lock (_gate)
                ReconcilePlan();
            int total;
            lock (_gate)
                total = _plan.Count;
            result.PartsTotal = total;
            if (total == 0)
            {
                result.Status = RevisionApplyStatus.NothingToSend;
                return result;
            }

            // 3. Части по очереди.
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                PlanPart part;
                lock (_gate)
                    part = _plan[i];
                if (part.Applied)
                    continue;

                if (part.SessionId is null)
                {
                    progress?.Report(new RevisionApplyProgress(i + 1, total, "create"));
                    List<InventorySessionItem> items;
                    lock (_gate)
                        items = BuildPartItems(part, result.Warnings);
                    if (items.Count == 0)
                    {
                        lock (_gate)
                            part.Applied = true;
                        continue;
                    }

                    var note = Tr.T($"Ревизия из кассы {StartedAt:dd.MM.yyyy HH:mm} ({i + 1}/{total})", $"Кассадан ревизия {StartedAt:dd.MM.yyyy HH:mm} ({i + 1}/{total})",
                        $"Stocktake from the register {StartedAt:dd.MM.yyyy HH:mm} ({i + 1}/{total})", $"Kasadan sayım {StartedAt:dd.MM.yyyy HH:mm} ({i + 1}/{total})",
                        $"Kassadan inventarizatsiya {StartedAt:dd.MM.yyyy HH:mm} ({i + 1}/{total})");
                    var sessionId = await api.CreateSessionAsync(note, items, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        result.Status = RevisionApplyStatus.Failed;
                        result.ErrorMessage = Tr.T("Не удалось создать акт ревизии на сервере.", "Серверде ревизия актысын түзүү мүмкүн болгон жок.", "Could not create the stocktake document on the server.", "Sunucuda sayım tutanağı oluşturulamadı.", "Serverda inventarizatsiya aktini yaratib bo'lmadi.");
                        return result;
                    }

                    lock (_gate)
                    {
                        part.SessionId = sessionId;
                        part.CreatedAt = DateTime.Now;
                    }

                    // Номер акта — на диск сразу (до проведения): обрыв после этого места не создаст второй акт.
                    await SaveDraftAsync().ConfigureAwait(false);
                }
                else
                {
                    progress?.Report(new RevisionApplyProgress(i + 1, total, "check"));
                }

                if (!await IsAppliedAsync(api, part.SessionId!, ct).ConfigureAwait(false))
                {
                    progress?.Report(new RevisionApplyProgress(i + 1, total, "apply"));
                    try
                    {
                        await api.ApplySessionAsync(part.SessionId!, allowNegative: false, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Прошлое проведение могло дойти без ответа: смотрим состояние акта (как мобилка).
                        if (!await IsAppliedAsync(api, part.SessionId!, ct).ConfigureAwait(false) && !LooksAlreadyApplied(ex))
                            throw;
                    }
                }

                List<(string Id, double Qty)> posted;
                lock (_gate)
                {
                    part.Applied = true;
                    posted = part.Ids.Select((id, k) => (id, k < part.Sent.Count ? part.Sent[k] : 0)).ToList();
                }

                result.PartsApplied++;
                result.SentRows += posted.Count;
                if (StockApplied is { } onStock)
                {
                    foreach (var (id, qty) in posted)
                    {
                        try
                        {
                            onStock(id, qty);
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Ревизия: остаток {id} в каталоге не обновлён ({ex.Message}).", "WARNING");
                        }
                    }
                }

                await SaveDraftAsync().ConfigureAwait(false);
            }

            // 4. Всё проведено: локальный акт, черновик убрать.
            lock (_gate)
            {
                result.SessionIds = _plan.Where(p => p.SessionId is not null).Select(p => p.SessionId!).ToList();
                result.PartsApplied = _plan.Count(p => p.Applied);
                result.SentRows = _plan.Sum(p => p.Ids.Count);
            }

            result.SoldDuringCount = Preview().SoldDuringCount;
            result.Status = RevisionApplyStatus.Done;
            _doneResult = result;
            RevisionActRecord act;
            lock (_gate)
                act = BuildAct();
            result.ActFile = await Task.Run(() => RevisionActStore.Save(act)).ConfigureAwait(false);
            lock (_gate)
                _closed = true;
            await Task.Run(RevisionDraftStore.Delete).ConfigureAwait(false);
            Changed?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            // Остановились: прогресс в черновике, повтор ApplyAsync продолжит.
            result.Status = RevisionApplyStatus.Failed;
            result.ErrorMessage = ex is OperationCanceledException
                ? Tr.T("Отправка остановлена.", "Жөнөтүү токтотулду.", "Sending stopped.", "Gönderim durduruldu.", "Yuborish to'xtatildi.")
                : ex.Message;
            PosLogger.Log($"Ревизия: отправка остановилась на части {result.PartsApplied + 1}: {ex.GetType().Name}: {ex.Message}", "WARNING");
            try
            {
                await SaveDraftAsync().ConfigureAwait(false);
            }
            catch
            {
                // черновик не записался — уже залогировано в RevisionDraftStore
            }

            lock (_gate)
            {
                result.SessionIds = _plan.Where(p => p.SessionId is not null).Select(p => p.SessionId!).ToList();
                result.PartsApplied = _plan.Count(p => p.Applied);
                result.PartsTotal = _plan.Count;
            }

            return result;
        }
    }

    /// <summary>Есть посчитанные строки, которых нет ни в одном созданном акте.</summary>
    private bool HasUncreatedWork()
    {
        lock (_gate)
        {
            var posted = new HashSet<string>(_plan.Where(p => p.SessionId is not null).SelectMany(p => p.Ids), StringComparer.OrdinalIgnoreCase);
            return _rows.Any(r => r.IsCounted && !r.IsReadOnly && r.InCatalog && !posted.Contains(r.ProductId));
        }
    }

    /// <summary>Под _gate. Созданные акты не трогаем; остальное (и новые посчитанные строки) раскладываем заново по ChunkSize.</summary>
    private void ReconcilePlan()
    {
        var kept = _plan.Where(p => p.SessionId is not null || p.Applied).ToList();
        var posted = new HashSet<string>(kept.SelectMany(p => p.Ids), StringComparer.OrdinalIgnoreCase);
        var pending = _rows.Where(r => r.IsCounted && !r.IsReadOnly && r.InCatalog && !posted.Contains(r.ProductId)).Select(r => r.ProductId).ToList();
        _plan.Clear();
        _plan.AddRange(kept);
        for (var i = 0; i < pending.Count; i += ChunkSize)
            _plan.Add(new PlanPart { Ids = pending.Skip(i).Take(ChunkSize).ToList() });
    }

    /// <summary>Под _gate. Количества по текущему учёту (для ещё не созданной части) — запоминаем в части.</summary>
    private List<InventorySessionItem> BuildPartItems(PlanPart part, List<RevisionWarning> warnings)
    {
        var items = new List<InventorySessionItem>(part.Ids.Count);
        var ids = new List<string>(part.Ids.Count);
        var sent = new List<double>(part.Ids.Count);
        foreach (var id in part.Ids)
        {
            if (!_byId.TryGetValue(id, out var r) || !r.IsCounted || !r.InCatalog)
                continue;
            var (send, _, warn, raw) = ComputeSend(r);
            if (warn is { } w)
                warnings.Add(MakeWarning(w, r, raw, send));
            items.Add(new InventorySessionItem(id, send));
            ids.Add(id);
            sent.Add(send);
        }

        part.Ids = ids;
        part.Sent = sent;
        return items;
    }

    private static async Task<bool> IsAppliedAsync(IInventoryApiService api, string sessionId, CancellationToken ct)
    {
        try
        {
            var info = await api.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            return info?.IsApplied == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Состояние неизвестно — считаем «не проведён» и пробуем провести (повторное проведение сервер отклонит).
            PosLogger.Log($"Ревизия: состояние акта {sessionId} не получено ({ex.Message}).", "WARNING");
            return false;
        }
    }

    private static bool LooksAlreadyApplied(Exception ex) =>
        ex is ApiException && (ex.Message.Contains("уже провед", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already applied", StringComparison.OrdinalIgnoreCase));

    private RevisionActRecord BuildAct()
    {
        var sentById = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _plan)
            for (var k = 0; k < p.Ids.Count; k++)
                sentById[p.Ids[k]] = k < p.Sent.Count ? p.Sent[k] : 0;

        var act = new RevisionActRecord
        {
            Id = Id,
            StartedAt = StartedAt,
            FinishedAt = DateTime.Now,
            StockAt = StockAt,
            UserName = UserName,
            SalesCorrection = _salesCorrection,
            SessionIds = _plan.Where(p => p.SessionId is not null).Select(p => p.SessionId!).ToList(),
        };
        foreach (var r in _rows)
        {
            act.Rows.Add(new RevisionActRow
            {
                ProductId = r.ProductId,
                Name = r.Name,
                Barcode = r.Barcode,
                Unit = r.Unit,
                Expected = r.ExpectedAtCount,
                Fact = r.Fact,
                Written = r.IsCounted && sentById.TryGetValue(r.ProductId, out var w) ? w : null,
                SalePrice = r.SalePrice,
                PurchasePrice = r.PurchasePrice,
                CountedAt = r.CountedAt,
            });
        }

        return act;
    }

    /// <summary>Обновить остаток товара в каталоге кассы и в её базе — теми же сервисами, что WarehouseViewModel.ApplyCountedStock (для StockApplied).</summary>
    public static void ApplyToLocalCatalog(string productId, double quantity)
    {
        var vm = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
            return;
        StockSyncService.ApplyQuantityToTileOnUi(vm, quantity, vm.MustWeigh);
        CatalogCacheService.PersistProductStock(productId, quantity, vm.MustWeigh);
    }

    // ------------------------------------------------------------------ черновик

    /// <summary>Снимок для записи (только посчитанные строки + план); создаётся на потоке окна за миллисекунды.</summary>
    private RevisionDraftDto Snapshot()
    {
        var dto = new RevisionDraftDto
        {
            Id = Id,
            CompanyId = RevisionDraftStore.CurrentCompany,
            User = UserName,
            StartedAt = StartedAt,
            StockAt = StockAt,
            SavedAt = DateTime.Now,
            SalesCorrection = _salesCorrection,
            Seq = RevisionDraftStore.NextSeq(),
        };
        dto.Counts.Capacity = _counted;
        foreach (var r in _rows)
            if (r.Fact is { } f)
                dto.Counts.Add(new RevisionDraftCount { P = r.ProductId, F = f, E = r.ExpectedAtCount, T = r.CountedAt });
        if (_plan.Count > 0)
            dto.Plan = _plan.Select(p => p.ToDto()).ToList();
        return dto;
    }

    private async void ScheduleSave()
    {
        if (!PersistDraft)
            return;
        var version = ++_saveVersion;
        try
        {
            await Task.Delay(600).ConfigureAwait(true);
            if (version == _saveVersion)
                await SaveDraftAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: черновик не записан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Записать черновик сейчас: снимок под замком, запись файла — на пуле потоков.</summary>
    public Task SaveDraftAsync()
    {
        if (!PersistDraft)
            return Task.CompletedTask;
        RevisionDraftDto? snap;
        lock (_gate)
        {
            if (_closed && _doneResult is null)
                return Task.CompletedTask;
            snap = _doneResult is null ? (_counted == 0 && _plan.Count == 0 ? null : Snapshot()) : null;
        }

        return snap is null ? Task.Run(RevisionDraftStore.Delete) : Task.Run(() => RevisionDraftStore.Save(snap));
    }

    /// <summary>Записать черновик синхронно (закрытие окна).</summary>
    public void SaveDraftNow()
    {
        if (!PersistDraft)
            return;
        RevisionDraftDto? snap;
        lock (_gate)
        {
            if (_closed || _doneResult is not null)
                return;
            snap = _counted == 0 && _plan.Count == 0 ? null : Snapshot();
        }

        if (snap is null)
            RevisionDraftStore.Delete();
        else
            RevisionDraftStore.Save(snap);
    }

    /// <summary>Бросить ревизию («Начать заново»): черновик удаляется. Нельзя, если акты уже созданы на сервере.</summary>
    public bool Discard()
    {
        lock (_gate)
        {
            if (_plan.Any(p => p.SessionId is not null) && _doneResult is null)
                return false;
            _closed = true;
        }

        RevisionDraftStore.Delete();
        return true;
    }
}

// ====================================================================== черновик на диске

internal sealed class RevisionDraftCount
{
    public string? P { get; set; }
    public double F { get; set; }
    public double E { get; set; }
    public DateTime? T { get; set; }
}

internal sealed class RevisionDraftPart
{
    public List<string>? Ids { get; set; }
    public List<double>? Sent { get; set; }
    public string? SessionId { get; set; }
    public bool Applied { get; set; }
    public DateTime? CreatedAt { get; set; }
}

internal sealed class RevisionDraftDto
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string User { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime StockAt { get; set; }
    public DateTime SavedAt { get; set; }
    public bool SalesCorrection { get; set; } = true;
    public List<RevisionDraftCount> Counts { get; set; } = new();
    public List<RevisionDraftPart>? Plan { get; set; }

    /// <summary>Порядковый номер записи: запоздавшая старая запись не затирает новую и не воскрешает удалённый черновик.</summary>
    [JsonIgnore]
    public long Seq { get; set; }
}

public sealed record RevisionDraftInfo(int Counted, DateTime StartedAt, DateTime SavedAt, bool PostingStarted);

/// <summary>Черновик ревизии — файл на этом компьютере (%APPDATA%\NurMarketKassa\revision\draft.json), общий для кассы и программы владельца,
/// плюс файл-замок «ревизия открыта в другой программе».</summary>
public static class RevisionDraftStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    internal static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "revision");

    private static string FilePath => Path.Combine(DataDir, "draft.json");
    private static string LockPath => Path.Combine(DataDir, "draft.lock");
    private static readonly object Sync = new();
    private static long _seq;
    private static long _floor;
    private static long _lastWritten;
    private static readonly TimeSpan LockMaxAge = TimeSpan.FromHours(2);

    internal static long NextSeq() => Interlocked.Increment(ref _seq);

    internal static string CurrentCompany
    {
        get
        {
            try
            {
                return CompanyInfoService.LastCompany?.Id ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    internal static RevisionDraftDto? Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                var draft = JsonSerializer.Deserialize<RevisionDraftDto>(File.ReadAllText(FilePath));
                if (draft is null || (draft.Counts.Count == 0 && draft.Plan is not { Count: > 0 }))
                    return null;
                // Черновик другого магазина (вход под другим аккаунтом) не показываем.
                if (draft.CompanyId.Length > 0 && CurrentCompany.Length > 0
                    && !string.Equals(draft.CompanyId, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                    return null;
                return draft;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не прочитан ({ex.Message}).", "WARNING");
                return null;
            }
        }
    }

    /// <summary>Есть ли черновик: сколько посчитано, когда начали («Продолжить ревизию от 24.10 14:05, посчитано 123»). null — нет.</summary>
    public static RevisionDraftInfo? Peek()
    {
        var d = Load();
        return d is null ? null : new RevisionDraftInfo(d.Counts.Count, d.StartedAt, d.SavedAt, d.Plan?.Any(p => !string.IsNullOrEmpty(p.SessionId)) == true);
    }

    internal static void Save(RevisionDraftDto draft)
    {
        lock (Sync)
        {
            // Запоздавшая старая запись (две записи на пуле потоков) не затирает новую и не воскрешает удалённый черновик.
            if (draft.Seq <= _floor || draft.Seq < _lastWritten)
                return;
            try
            {
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(draft));
                File.Move(tmp, FilePath, overwrite: true);
                _lastWritten = draft.Seq;
                TouchLock();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не записан ({ex.Message}).", "WARNING");
            }
        }
    }

    public static void Delete()
    {
        lock (Sync)
        {
            _floor = Interlocked.Read(ref _seq);
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не удалён ({ex.Message}).", "WARNING");
            }
        }
    }

    // ---- замок: «Ревизия открыта в другой программе этого компьютера» ----

    /// <summary>Занять ревизию за этой программой. false — её держит другая живая программа (holder — её PID); замок старше 2 часов или без процесса снимается.</summary>
    public static bool TryAcquireLock(out string holder)
    {
        holder = "";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var w = new StreamWriter(fs);
                        w.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                    catch (IOException) when (File.Exists(LockPath))
                    {
                        var text = "";
                        try { text = File.ReadAllText(LockPath).Trim(); } catch { /* занят записью — считаем живым */ }
                        _ = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
                        if (pid == Environment.ProcessId)
                            return true;
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(LockPath);
                        // Пустой файл моложе 5 с — другая программа только что создала замок и ещё пишет в него номер.
                        if (age <= LockMaxAge && ((pid > 0 && ProcessAlive(pid)) || (pid == 0 && age < TimeSpan.FromSeconds(5))))
                        {
                            holder = pid > 0 ? pid.ToString(CultureInfo.InvariantCulture) : "?";
                            return false;
                        }

                        File.Delete(LockPath); // устарел или процесса нет
                    }
                }

                holder = "?";
                return false;
            }
            catch (Exception ex)
            {
                // Папка недоступна — замок не работает, ревизии это не мешает.
                PosLogger.Log($"Ревизия: замок не занят ({ex.Message}).", "WARNING");
                return true;
            }
        }
    }

    public static void ReleaseLock()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                    File.Delete(LockPath);
            }
            catch
            {
                // замок остался — снимется по возрасту
            }
        }
    }

    /// <summary>Подтвердить, что программа жива (вызывается при каждой записи черновика).</summary>
    public static void TouchLock()
    {
        try
        {
            if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
        }
        catch
        {
            // не критично
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true; // не смогли проверить — полагаемся на возраст замка
        }
    }
}

// ====================================================================== локальные акты

public sealed class RevisionActRow
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Учёт на момент пересчёта.</summary>
    public double Expected { get; set; }
    /// <summary>Введённый факт; null — не считали.</summary>
    public double? Fact { get; set; }
    /// <summary>Что записано в акт (с поправкой на продажи); null — в акт не входила.</summary>
    public double? Written { get; set; }
    public double SalePrice { get; set; }
    public double PurchasePrice { get; set; }
    public DateTime? CountedAt { get; set; }
}

/// <summary>Проведённый акт ревизии для повторной выгрузки (пакет Р3). Хранит ВСЕ строки склада (непосчитанные — Fact = null).</summary>
public sealed class RevisionActRecord
{
    public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public DateTime StockAt { get; set; }
    public string UserName { get; set; } = "";
    public bool SalesCorrection { get; set; }
    /// <summary>Номера актов на сервере (по 500 строк).</summary>
    public List<string> SessionIds { get; set; } = new();
    public List<RevisionActRow> Rows { get; set; } = new();
}

public sealed record RevisionActSummary(string FileName, string Id, DateTime StartedAt, DateTime FinishedAt, int Counted, int Total, IReadOnlyList<string> SessionIds);

/// <summary>Последние 20 проведённых ревизий — %APPDATA%\NurMarketKassa\revision\acts\*.json.gz (сжатые: 15 000 строк ≈ 0,2 МБ).</summary>
public static class RevisionActStore
{
    private const int Keep = 20;

    private static string ActsDir => Path.Combine(RevisionDraftStore.DataDir, "acts");

    /// <summary>Записать акт; возвращает имя файла. Ошибка записи не мешает ревизии (пустое имя).</summary>
    public static string Save(RevisionActRecord act)
    {
        try
        {
            Directory.CreateDirectory(ActsDir);
            var name = $"{act.StartedAt:yyyyMMdd-HHmmss}-{(act.Id.Length >= 8 ? act.Id[..8] : act.Id)}.json.gz";
            var path = Path.Combine(ActsDir, name);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, act);
            File.Move(tmp, path, overwrite: true);
            foreach (var old in new DirectoryInfo(ActsDir).GetFiles("*.json.gz").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
            {
                try { old.Delete(); } catch { /* останется до следующего раза */ }
            }

            return name;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: акт не сохранён локально ({ex.Message}).", "WARNING");
            return "";
        }
    }

    /// <summary>Список прошлых ревизий, новые сверху.</summary>
    public static List<RevisionActSummary> List()
    {
        var result = new List<RevisionActSummary>();
        try
        {
            if (!Directory.Exists(ActsDir))
                return result;
            foreach (var f in new DirectoryInfo(ActsDir).GetFiles("*.json.gz").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var act = Load(f.Name);
                if (act is not null)
                    result.Add(new RevisionActSummary(f.Name, act.Id, act.StartedAt, act.FinishedAt, act.Rows.Count(r => r.Fact is not null), act.Rows.Count, act.SessionIds));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: список актов не прочитан ({ex.Message}).", "WARNING");
        }

        return result;
    }

    public static RevisionActRecord? Load(string fileName)
    {
        try
        {
            var path = Path.Combine(ActsDir, Path.GetFileName(fileName));
            if (!File.Exists(path))
                return null;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<RevisionActRecord>(gz);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: акт {fileName} не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }
}
