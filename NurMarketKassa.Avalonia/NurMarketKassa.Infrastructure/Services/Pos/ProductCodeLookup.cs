using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21: один поиск товара по отсканированному коду для кассы и всех мест склада (склад не находит
/// товар по скану). Владелец: «товар при сканере находит в кассе, но не находит в складе». Касса искала через базу
/// кассы (основной и доп. штрихкоды, UPC-A 12 ↔ EAN-13 13 цифр, id), потом штрихкод размера, весовую этикетку и
/// сервер, а склад — каждое окно по-своему: «Товары» — вхождение только в основной штрихкод (доп. штрихкоды — лишь
/// в «Одежде»/«Услугах»), ревизия и списание — только точный основной штрихкод в списке памяти, поиск в ревизии и
/// списании — вообще без штрихкода. Теперь все места спрашивают здесь.
/// Порядок как у кассы (BasketPanelViewModel.AddByBarcodeAsync): точный код в базе кассы → тот же код в каталоге
/// памяти (доп. штрихкоды, пока индекс базы не прогрет; артикул, код товара) → штрихкод размера → весовая этикетка
/// → сервер.</summary>
public static class ProductCodeLookup
{
    public enum HitKind
    {
        Barcode,
        Variant,
        WeightLabel,
        Article,
        Server,
    }

    public sealed record Hit(CatalogProductTileVm Product, HitKind Kind, ProductVariantDto? Variant = null);

    /// <summary>Hit — найден; Unavailable — сервер не ответил (связь), «не найден» говорить рано.</summary>
    public sealed record Result(Hit? Hit, bool Unavailable);

    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Код со сканера без мусора: пробелы и переводы строки по краям, управляющие символы (GS у кодов GS1),
    /// пробелы внутри цифрового кода («4870 2025 21261» — так вставляют из накладной).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var cleaned = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var noSpaces = cleaned.Replace(" ", "");
        return noSpaces.Length > 0 && noSpaces.All(char.IsDigit) ? noSpaces : cleaned;
    }

    /// <summary>Точный поиск кассы (основной скан главного экрана — тот же код, см. MainViewModelRegistration):
    /// база кассы по штрихкоду (основной, доп., 12 ↔ 13 цифр), затем по id.</summary>
    public static CatalogProductTileVm? FindExact(string? code)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return null;
        var repo = LocalProductRepository.Instance;
        return repo.TryGetTileByBarcode(key) ?? repo.TryGetTileBySku(key);
    }

    /// <summary>Только этот компьютер, без сети. <paramref name="allowWeightLabel"/> = false — весовую этикетку не
    /// разбирать (приёмка: этикетка с весов несёт вес одной покупки, а не товар поставки).</summary>
    public static Hit? FindLocal(string? raw, bool allowWeightLabel = true)
    {
        var key = Normalize(raw);
        if (key.Length == 0)
            return null;
        try
        {
            var candidates = Candidates(key);
            try
            {
                foreach (var code in candidates)
                {
                    if (FindExact(code) is { } exact)
                        return new Hit(exact, HitKind.Barcode);
                }
            }
            catch (Exception ex)
            {
                // База кассы не открылась (занята, повреждена) — каталог памяти всё равно проверяем.
                PosLogger.Log($"Поиск товара по коду {key}: база кассы недоступна ({ex.Message}), ищем в каталоге памяти.", "WARNING");
            }

            var snapshot = SnapshotCatalog();
            foreach (var code in candidates)
            {
                if (FindInCatalog(snapshot, code) is { } inMemory)
                    return inMemory;
            }

            // Штрихкод размера/цвета (этикетка «Платье — 44, Красный») — справочник ведёт касса.
            if (VariantBarcodeIndex.Find(key) is { } variant
                && (FindById(snapshot, variant.ProductId) ?? LocalProductRepository.Instance.TryGetTileById(variant.ProductId)) is { } variantProduct)
                return new Hit(variantProduct, HitKind.Variant, variant.Variant);

            // Этикетка весов (префиксы 20–29): товар по коду внутри (PLU, артикул, код товара, коды, записанные на весы).
            if (allowWeightLabel && WeightLabelAllowedHere(key))
            {
                ScaleLabelCodeRegistry.ApplySharedAmountPrefixes();
                if (WeightBarcodeParser.TryParse(key, out var weighted)
                    && LocalCartService.FindByEmbeddedCode(weighted.ProductCode) is { } weighedProduct)
                    return new Hit(weighedProduct, HitKind.WeightLabel);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поиск товара по коду {key} не удался: {ex.Message}", "WARNING");
        }

        return null;
    }

    /// <summary>Сначала этот компьютер, потом сервер — как касса при скане незнакомого кода.</summary>
    public static async Task<Result> FindAsync(string? raw, bool allowWeightLabel = true, CancellationToken ct = default)
    {
        if (FindLocal(raw, allowWeightLabel) is { } local)
            return new Result(local, false);
        return await FindOnServerAsync(raw, ct).ConfigureAwait(true);
    }

    /// <summary>Сервер по штрихкоду (как BasketPanelViewModel.TryFindProductOnServerAsync): найденный товар
    /// записывается в базу кассы и в каталог памяти — дальше он находится уже без сети.</summary>
    public static async Task<Result> FindOnServerAsync(string? raw, CancellationToken ct = default)
    {
        var code = Normalize(raw);
        if (code.Length == 0)
            return new Result(null, false);
        // Автономный режим: товары создаются на этом компьютере, сервер не спрашиваем — сразу «не найден».
        if (OfflineModeHelper.UseLocalOperations)
            return new Result(null, false);
        if (OfflineModeHelper.IsServerOutage || PosApp.CatalogApi is null)
            return new Result(null, true);

        try
        {
            var forms = Candidates(code);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ServerTimeout);
            var results = await PosApp.CatalogApi.ProductsSearchAsync(code, limit: 5, cts.Token).ConfigureAwait(true);
            var dto = results.FirstOrDefault(d => forms.Contains(d.Barcode?.Trim() ?? "", StringComparer.OrdinalIgnoreCase));
            if (dto is null && BarcodeForms.Alternate(code) is { } alternate)
            {
                results = await PosApp.CatalogApi.ProductsSearchAsync(alternate, limit: 5, cts.Token).ConfigureAwait(true);
                dto = results.FirstOrDefault(d => forms.Contains(d.Barcode?.Trim() ?? "", StringComparer.OrdinalIgnoreCase));
            }

            if (dto is null)
                return new Result(null, false);

            var tile = ProductCatalogMapper.TryTile(dto, PosApp.Settings?.ApiBaseUrl ?? "");
            if (tile is null)
                return new Result(null, true);

            LocalProductRepository.Instance.UpsertFromTiles([tile]);
            var index = CatalogCacheService.Products.FindIndex(p => p.Id == tile.Id);
            if (index >= 0)
                CatalogCacheService.Products[index] = tile;
            else
                CatalogCacheService.Products.Add(tile);
            CatalogCacheService.NotifyCatalogChanged();
            PosLogger.Log($"Поиск по коду: '{code}' нет на этом компьютере, найден на сервере — товар {tile.Id}.", "CATALOG");
            return new Result(new Hit(tile, HitKind.Server), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Поиск по коду '{code}' на сервере: нет ответа за {ServerTimeout.TotalSeconds:0} с.", "WARNING");
            return new Result(null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Поиск по коду '{code}' на сервере не удался: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return new Result(null, true);
        }
    }

    /// <summary>Записи одного кода: как пришёл; UPC-A 12 ↔ EAN-13 с нулём; GTIN-14 с нулём впереди → EAN-13;
    /// набранный в русской раскладке (сканер-клавиатура в поле при ЙЦУКЕН: «ФИ123» вместо «AB123»).</summary>
    public static IReadOnlyList<string> Candidates(string? raw)
    {
        var key = Normalize(raw);
        var list = new List<string>();
        void Add(string? code)
        {
            if (!string.IsNullOrEmpty(code) && !list.Contains(code, StringComparer.OrdinalIgnoreCase))
                list.Add(code);
        }

        if (key.Length == 0)
            return list;
        Add(key);
        Add(BarcodeForms.Alternate(key));
        if (key.Length == 14 && key[0] == '0' && key.All(char.IsDigit))
            Add(key[1..]);
        if (SwapRussianLayout(key) is { } latin)
        {
            Add(latin);
            Add(BarcodeForms.Alternate(latin));
        }

        return list;
    }

    private const string RussianKeys = "йцукенгшщзфывапролдячсмить";
    private const string LatinKeys = "qwertyuiopasdfghjklzxcvbnm";

    private static string? SwapRussianLayout(string code)
    {
        if (!code.Any(c => RussianKeys.Contains(char.ToLowerInvariant(c))))
            return null;
        var chars = code.Select(c =>
        {
            var i = RussianKeys.IndexOf(char.ToLowerInvariant(c));
            if (i < 0)
                return c;
            return char.IsUpper(c) ? char.ToUpperInvariant(LatinKeys[i]) : LatinKeys[i];
        }).ToArray();
        return new string(chars);
    }

    /// <summary>Как у кассы (BasketPanelViewModel.WeightBarcodeAllowedHere): в «Одежде» внутренние штрихкоды тоже на «2»,
    /// там весовой код — только с явно настроенным префиксом весов.</summary>
    private static bool WeightLabelAllowedHere(string code)
    {
        if (!MarketSpheres.IsClothing)
            return true;
        if (code.Length < 2)
            return false;
        var prefix = code[..2];
        return WeightBarcodeParser.AmountPrefixes.Contains(prefix) || WeightBarcodeParser.WeightPrefixes.Contains(prefix);
    }

    private static CatalogProductTileVm[] SnapshotCatalog()
    {
        try
        {
            return CatalogCacheService.Products.ToArray();
        }
        catch (Exception)
        {
            // Каталог как раз перезаписывается — без него обойдёмся базой кассы.
            return [];
        }
    }

    private static CatalogProductTileVm? FindById(CatalogProductTileVm[] catalog, string id) =>
        catalog.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Каталог памяти — полный каталог компании. Нужен, пока индекс базы кассы не прогрет (тогда база
    /// ищет только основной штрихкод), и для артикула/кода товара, которых касса при скане не проверяет.</summary>
    private static Hit? FindInCatalog(CatalogProductTileVm[] catalog, string code)
    {
        if (catalog.Length == 0)
            return null;

        CatalogProductTileVm? byMain = null, byAlternate = null, byArticle = null;
        foreach (var p in catalog)
        {
            if (string.Equals(p.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
            {
                // Дубль штрихкода — как в базе кассы: товар с остатком важнее.
                if (byMain is null || (byMain.Quantity <= 0 && p.Quantity > 0))
                    byMain = p;
                continue;
            }

            if (byAlternate is null && HasAlternateBarcode(p, code))
                byAlternate = p;
            if (byArticle is null && code.Length >= 3
                && (string.Equals(p.Article?.Trim(), code, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.ProductCode?.Trim(), code, StringComparison.OrdinalIgnoreCase)))
                byArticle = p;
        }

        if (byMain is not null)
            return new Hit(byMain, HitKind.Barcode);
        if (byAlternate is not null)
            return new Hit(byAlternate, HitKind.Barcode);
        return byArticle is not null ? new Hit(byArticle, HitKind.Article) : null;
    }

    private static bool HasAlternateBarcode(CatalogProductTileVm p, string code)
    {
        if (!string.IsNullOrWhiteSpace(p.AlternateBarcodesRaw)
            && p.AlternateBarcodesRaw.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(alt => string.Equals(alt, code, StringComparison.OrdinalIgnoreCase)))
            return true;
        return p.AlternateBarcodeVariants?.Any(v => string.Equals(v.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
