using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — модели ревизии: перечисления, записи, строка, параметры, результаты (разбор огромного файла: типы вынесены без изменений; остальное — RevisionSession.cs)

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
    int ReadOnly,
    // 2026-10-29: «Не хватает закупки у N товаров» — позиции с расхождением без цены закупки и их разница по цене продажи (в «по закупке» они не вошли).
    int NoCostCount = 0, double ShortageNoCostAtSale = 0, double SurplusNoCostAtSale = 0)
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
        // 2026-11-06 (В12, Б-7): штрихкод и артикул — как в списке Склада (основной → первый дополнительный; артикул → код товара → PLU), а не «сырые» поля плитки:
        // у плитки с пустым основным штрихкодом ревизия и её Excel показывали пустое место.
        Barcode = tile.BarcodeDisplay;
        // 2026-11-06 (аудит Этапа 2, В-1): в индекс сканирования идёт только основной штрихкод; «первый дополнительный» из BarcodeDisplay может быть
        // кодом упаковки ×12 и без этого считался бы как 1 шт (ложная недостача). Доп. коды индексируются со своим множителем.
        IndexBarcode = tile.Barcode?.Trim() ?? "";
        Article = tile.CodeDisplay;
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
    /// <summary>Основной штрихкод товара (пусто, если его нет) — только для индекса сканирования.</summary>
    internal string IndexBarcode { get; }
    public string Article { get; }
    public string Category { get; }
    public string Unit { get; }
    public bool IsWeighted { get; }
    public double SalePrice { get; }
    public double PurchasePrice { get; }

    /// <summary>2026-10-29: цена закупки известна (больше нуля); иначе деньги по расхождению считаем по цене продажи с пометкой.</summary>
    public bool HasPurchase => PurchasePrice > 0.0001;

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

