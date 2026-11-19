using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-11-13 (Л-10, «Новичок: перенос данных»): демо-магазин для пробы — 30 учебных товаров (Assets/demo/demo-store.json).
// Живёт ТОЛЬКО в памяти: на сервер, в базу pos_local.db и в офлайн-очередь НЕ пишется. Для учебного режима (Н-1): окно кассы
// спрашивает DemoStoreCatalog.IsActive и подмешивает BuildTiles() к плиткам каталога; продажа в учебном режиме и так никуда не уходит (TrainingMode).

/// <summary>Один учебный товар (поля уже на языке кассы).</summary>
public sealed record DemoProduct(string Barcode, string Name, string Category, string Unit, double Purchase, double Sale, double Quantity, bool Weighed);

public static class DemoStoreCatalog
{
    /// <summary>Префикс Id плиток демо-магазина: по нему окно кассы отличает учебный товар от настоящего.</summary>
    public const string IdPrefix = "demo-";

    private static readonly object Sync = new();
    private static string? _json;
    private static volatile bool _active;

    /// <summary>Набор включён («Посмотреть на примере»). Сбрасывается при перезапуске кассы и при выходе из учебного режима.</summary>
    public static bool IsActive => _active;

    /// <summary>Учебные плитки показываются в каталоге: набор включён И идёт учебный режим (вне учебного режима продажа такого товара ушла бы на сервер).</summary>
    public static bool IsVisible => _active && TrainingMode.IsActive;

    private static bool _wasVisible;

    static DemoStoreCatalog()
    {
        TrainingMode.Changed += OnTrainingChanged;
    }

    private static void OnTrainingChanged()
    {
        if (!TrainingMode.IsActive && _active)
        {
            _active = false; // учебный режим закончился — демо-набор выключается сам
            PosLogger.Log("Демо-магазин выключен вместе с учебным режимом.", "TRAINING");
        }
        RaiseIfVisibilityChanged();
    }

    private static void RaiseIfVisibilityChanged()
    {
        var now = IsVisible;
        if (now == _wasVisible)
            return;
        _wasVisible = now;
        Changed?.Invoke();
    }

    /// <summary>Показ учебных плиток начался или закончился (см. <see cref="IsVisible"/>).</summary>
    public static event Action? Changed;

    /// <summary>Хозяин ресурса (приложение) отдаёт сюда текст demo-store.json один раз при старте окна.</summary>
    public static void SetSource(string json)
    {
        lock (Sync)
            _json = json;
    }

    public static bool HasSource
    {
        get
        {
            lock (Sync)
                return !string.IsNullOrWhiteSpace(_json);
        }
    }

    public static void Activate()
    {
        if (_active || !HasSource)
            return;
        _active = true;
        PosLogger.Log("Демо-магазин включён: учебные товары только в памяти, на сервер не уходят.", "TRAINING");
        RaiseIfVisibilityChanged();
    }

    public static void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        PosLogger.Log("Демо-магазин выключен.", "TRAINING");
        RaiseIfVisibilityChanged();
    }

    /// <summary>Название набора на языке кассы.</summary>
    public static string Title(string? json = null) => Pick(Root(json)?.GetProperty("title"), "");

    /// <summary>Товары набора на языке кассы. Пустой список — ресурс не передан или повреждён (исключений наружу не бросает).</summary>
    public static IReadOnlyList<DemoProduct> Products(string? json = null)
    {
        try
        {
            if (Root(json) is not { } root || !root.TryGetProperty("products", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<DemoProduct>();
            foreach (var p in arr.EnumerateArray())
            {
                var barcode = p.TryGetProperty("b", out var b) ? b.GetString() ?? "" : "";
                var name = Pick(p.TryGetProperty("n", out var n) ? n : null, "");
                if (barcode.Length == 0 || name.Length == 0)
                    continue;
                list.Add(new DemoProduct(
                    barcode,
                    name,
                    Pick(p.TryGetProperty("c", out var c) ? c : null, ""),
                    p.TryGetProperty("u", out var u) ? u.GetString() ?? "шт" : "шт",
                    Num(p, "buy"), Num(p, "sale"), Num(p, "qty"),
                    p.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() == 1));
            }
            return list;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Демо-магазин: набор не прочитан ({ex.Message}).", "WARNING");
            return [];
        }
    }

    /// <summary>Записи в формате локальной базы (LocalProductRecord) — для тех, кто собирает плитки сам. В базу НЕ записываются.</summary>
    public static IReadOnlyList<LocalProductRecord> ToRecords(string? json = null) =>
        Products(json).Select((p, i) => new LocalProductRecord
        {
            Id = IdPrefix + (i + 1),
            Name = p.Name,
            Barcode = p.Barcode,
            Price = p.Sale,
            PurchasePrice = p.Purchase,
            Stock = p.Quantity,
            Unit = p.Unit,
            MustWeigh = p.Weighed,
            Category = p.Category,
        }).ToList();

    /// <summary>Плитки каталога для учебного режима (Id «demo-N»). Только в памяти: в базу и на сервер не пишутся. Остаток учебный — продажа его не списывает.</summary>
    public static IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> BuildTiles(string? json = null)
    {
        var tiles = new List<NurMarketKassa.Models.Pos.CatalogProductTileVm>();
        foreach (var r in ToRecords(json))
        {
            var vm = new NurMarketKassa.Models.Pos.CatalogProductTileVm(r.Id, r.Name, $"{r.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} сом", r.MustWeigh)
            {
                Barcode = r.Barcode,
                Category = r.Category,
                Unit = r.Unit,
                PurchasePrice = r.PurchasePrice,
            };
            StockSyncService.ApplyQuantityToTile(vm, r.Stock, r.MustWeigh);
            if (ProductUnitNormalizer.TryPrepareCatalogTile(vm))
                tiles.Add(vm);
        }
        return tiles;
    }

    private static JsonElement? Root(string? json)
    {
        string? text = json;
        if (text is null)
        {
            lock (Sync)
                text = _json;
        }
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static double Num(JsonElement p, string key) =>
        p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Из массива «ru, ky, en, tr, uz» — строка на языке кассы (нет такой — русская).</summary>
    private static string Pick(JsonElement? arr, string fallback)
    {
        if (arr is not { ValueKind: JsonValueKind.Array } a || a.GetArrayLength() == 0)
            return fallback;
        var items = a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        var idx = UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => 1,
            AppLanguage.English => 2,
            AppLanguage.Turkish => 3,
            AppLanguage.Uzbek => 4,
            _ => 0,
        };
        var s = idx < items.Length ? items[idx] : "";
        return s.Length > 0 ? s : items[0];
    }
}
