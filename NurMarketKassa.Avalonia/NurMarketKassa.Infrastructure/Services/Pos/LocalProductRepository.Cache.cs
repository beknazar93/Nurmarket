using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса LocalProductRepository (разбор огромного файла на partial-файлы), логика не менялась; остальное — LocalProductRepository.cs
public sealed partial class LocalProductRepository
{
    /// <summary>Поля, которые лента изменений прислала БЕЗ карточки товара (только изменившиеся значения). null — поля нет в ответе.</summary>
    public sealed record FeedFieldPatch(string Id, double? Price, double? PurchasePrice, double? WholesalePrice, double? Stock);

    /// <param name="UnchangedIds">2026-11-11: товары, которые пришли, но запись в базе от них не изменилась (лента прислала то же, что уже есть).</param>
    public sealed record FeedApplyResult(int Added, int Changed, int Deleted, IReadOnlyList<string> MissingIds, IReadOnlyList<string>? UnchangedIds = null);

    /// <summary>2026-10-27 (Б-46, CATALOG_FEED): применить изменения из ленты сервера — upserts (уже готовые плитки) и удалённые товары.
    /// Пишутся только действительно изменившиеся записи (отпечаток, как в SyncReplaceAllWithDiff), кэш правится точечно, как после
    /// полной загрузки. Схема базы не меняется, версия каталога (catalog_version) не трогается — её двигает только полная загрузка.</summary>
    public FeedApplyResult ApplyFeedChanges(
        IReadOnlyList<CatalogProductTileVm> upserts, IReadOnlyList<FeedFieldPatch> patches, IReadOnlyCollection<string> deletedIds)
    {
        // 2026-11-06 (Б-13): «no such column» — один раз достроить столбцы и повторить (самолечение базы без новых столбцов).
        try { return ApplyFeedChangesCore(upserts, patches, deletedIds); }
        catch (SqliteException ex) when (TryHealMissingColumn(ex)) { return ApplyFeedChangesCore(upserts, patches, deletedIds); }
    }

    private FeedApplyResult ApplyFeedChangesCore(
        IReadOnlyList<CatalogProductTileVm> upserts, IReadOnlyList<FeedFieldPatch> patches, IReadOnlyCollection<string> deletedIds)
    {
        var favoriteIds = GetFavoriteIds();
        var incoming = new Dictionary<string, LocalProductRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var vm in upserts)
        {
            ProductUnitNormalizer.ApplyToTile(vm);
            var record = FromTileVm(vm);
            if (favoriteIds.Contains(record.Id))
            {
                record.IsFavorite = true;
                vm.IsFavorite = true;
            }

            incoming[record.Id] = record;
        }

        var added = 0;
        var changed = 0;
        var toUpsert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toDelete = new List<string>();
        var missing = new List<string>();
        List<string>? unchanged = null; // 2026-11-11: см. FeedApplyResult.UnchangedIds
        using var connection = OpenConnection();
        // Поля без карточки: берём запись из базы и меняем только присланное. Товара в базе нет — ленте нечем его создать (нужна полная загрузка).
        foreach (var patch in patches)
        {
            if (incoming.ContainsKey(patch.Id))
                continue;
            if (QuerySingleRecord(connection, patch.Id) is not { } baseRecord)
            {
                missing.Add(patch.Id);
                continue;
            }

            if (patch.Price is { } price)
                baseRecord.Price = price;
            if (patch.PurchasePrice is { } purchase)
                baseRecord.PurchasePrice = purchase;
            if (patch.WholesalePrice is { } wholesale)
                baseRecord.WholesalePrice = wholesale;
            if (patch.Stock is { } stock)
                baseRecord.Stock = stock;
            incoming[baseRecord.Id] = baseRecord;
        }

        List<(string Id, string Name, int? Old, int? New)>? pluChanges = null; // 2026-11-13: смена/освобождение PLU → память прежних PLU (ScalePluHistory)
        foreach (var (id, record) in incoming)
        {
            var current = QuerySingleRecord(connection, id);
            if (current is null)
            {
                added++;
                toUpsert.Add(id);
                if (record.Plu is > 0 && ScalePluHistory.Any)
                    (pluChanges ??= new()).Add((id, record.Name, null, record.Plu)); // вернулся из архива с новым PLU
            }
            else if (!string.Equals(Fingerprint(current), Fingerprint(record), StringComparison.Ordinal))
            {
                changed++;
                toUpsert.Add(id);
                if (current.Plu is > 0 && current.Plu != record.Plu)
                    (pluChanges ??= new()).Add((id, current.Name, current.Plu, record.Plu));
            }
            else
            {
                (unchanged ??= new List<string>()).Add(id);
            }
        }

        foreach (var id in deletedIds.Distinct(StringComparer.OrdinalIgnoreCase))
            if (QuerySingleRecord(connection, id) is { } existing)
            {
                toDelete.Add(existing.Id);
                if (existing.Plu is > 0)
                    (pluChanges ??= new()).Add((existing.Id, existing.Name, existing.Plu, null)); // архив/удаление освобождает PLU
            }

        if (toUpsert.Count == 0 && toDelete.Count == 0)
            return new FeedApplyResult(0, 0, 0, missing, unchanged);

        using var transaction = connection.BeginTransaction();
        if (toDelete.Count > 0)
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM Products WHERE id = @id;";
            var idParam = deleteCmd.CreateParameter();
            idParam.ParameterName = "@id";
            deleteCmd.Parameters.Add(idParam);
            foreach (var id in toDelete)
            {
                idParam.Value = id;
                deleteCmd.ExecuteNonQuery();
            }
        }

        if (toUpsert.Count > 0)
        {
            using var upsertCommand = CreateUpsertCommand(connection, transaction);
            foreach (var record in incoming.Values)
                if (toUpsert.Contains(record.Id))
                    ExecuteUpsert(upsertCommand, record);
        }

        transaction.Commit();
        NotePluChanges(pluChanges);
        if (!TryPatchCacheAfterSync(incoming, toUpsert, added, toDelete))
            ResetCache();
        return new FeedApplyResult(added, changed, toDelete.Count, missing, unchanged);
    }

    /// <summary>2026-11-06 (жалоба клиента «колонка Код / штрихкод пустая», база, заполненная сокращённой лентой): дописать в запись товара ТОЛЬКО пустые штрихкод,
    /// артикул, код, PLU и доп. штрихкоды значениями из полной карточки (<paramref name="fresh"/>). Непустое в базе не трогается, пустым не затирается.
    /// true — запись изменена (и кэш поправлен).</summary>
    public bool FillMissingCodes(CatalogProductTileVm fresh)
    {
        if (fresh is null || string.IsNullOrWhiteSpace(fresh.Id))
            return false;
        try { return FillMissingCodesCore(fresh); }
        catch (SqliteException ex) when (TryHealMissingColumn(ex)) { return FillMissingCodesCore(fresh); }
    }

    private bool FillMissingCodesCore(CatalogProductTileVm fresh)
    {
        using var connection = OpenConnection();
        if (QuerySingleRecord(connection, fresh.Id) is not { } record)
            return false;

        var changed = false;
        if (string.IsNullOrWhiteSpace(record.Barcode) && !string.IsNullOrWhiteSpace(fresh.Barcode))
        {
            record.Barcode = fresh.Barcode;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(record.Article) && !string.IsNullOrWhiteSpace(fresh.Article))
        {
            record.Article = fresh.Article;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(record.ProductCode) && !string.IsNullOrWhiteSpace(fresh.ProductCode))
        {
            record.ProductCode = fresh.ProductCode;
            changed = true;
        }

        // 2026-11-13 (сервер «ev1311»: при возврате из архива весовой товар получает НОВЫЙ PLU): fresh — только что прочитанная карточка с сервера, её PLU главнее
        // локального: непустой другой PLU тоже заменяется (раньше дописывался только пустой), прежний — в память ScalePluHistory.
        if (fresh.Plu is > 0 && record.Plu != fresh.Plu)
        {
            if (record.Plu is > 0)
                NotePluChanges([(record.Id, record.Name, record.Plu, fresh.Plu)]);
            record.Plu = fresh.Plu;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(record.AlternateBarcodesRaw) && !string.IsNullOrWhiteSpace(fresh.AlternateBarcodesRaw))
        {
            record.AlternateBarcodesRaw = fresh.AlternateBarcodesRaw;
            if (fresh.AlternateBarcodeVariants is { Count: > 0 })
                record.AlternateBarcodeVariantsJson = JsonSerializer.Serialize(fresh.AlternateBarcodeVariants);
            changed = true;
        }

        if (!changed)
            return false;

        using (var transaction = connection.BeginTransaction())
        {
            using var upsertCommand = CreateUpsertCommand(connection, transaction);
            ExecuteUpsert(upsertCommand, record);
            transaction.Commit();
        }

        var incoming = new Dictionary<string, LocalProductRecord>(StringComparer.OrdinalIgnoreCase) { [record.Id] = record };
        if (!TryPatchCacheAfterSync(incoming, new[] { record.Id }, 0, Array.Empty<string>()))
            ResetCache();
        return true;
    }

    private static LocalProductRecord? QuerySingleRecord(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group,
                   is_bundle, article, bundle_items_json, alternate_barcodes,
                   alternate_barcode_variants, product_code, kind, description, wholesale_price, IsWeight,
                   discount_percent, markup_percent, country, weight_kg, min_quantity
            FROM Products WHERE id = $value LIMIT 1;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$value";
        parameter.Value = id.Trim();
        command.Parameters.Add(parameter);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    private void RebuildCacheFromDatabase()
    {
        var records = LoadAllRecords();
        var tiles = records
            .Select(ToTileVm)
            .Where(vm => vm != null)
            .Cast<CatalogProductTileVm>()
            .ToList();
        BuildCache(tiles);
    }

    /// <summary>2026-10-04: индекс штрихкодов вынесен из BuildCache без изменений правил — им же
    /// пользуется точечная правка кэша после синхронизации (TryPatchCacheAfterSync).</summary>
    private static Dictionary<string, CatalogProductTileVm> BuildBarcodeIndex(IReadOnlyList<CatalogProductTileVm> tiles)
    {
        var barcodeCache = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in tiles)
            AddBarcodes(barcodeCache, tile);
        return barcodeCache;
    }

    private void BuildCache(IReadOnlyList<CatalogProductTileVm> tiles)
    {
        var barcodeCache = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        var skuCache = new Dictionary<string, CatalogProductTileVm>(tiles.Count, StringComparer.OrdinalIgnoreCase);
        var allProducts = new List<CatalogProductTileVm>(tiles.Count);
        var searchRows = new List<CachedProductRow>(tiles.Count);

        foreach (var tile in tiles)
        {
            allProducts.Add(tile);
            skuCache[tile.Id.Trim()] = tile;
            AddBarcodes(barcodeCache, tile);
            searchRows.Add(CachedProductRow.From(tile));
        }

        searchRows.Sort(static (a, b) =>
            string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        _cacheLock.EnterWriteLock();
        try
        {
            _barcodeCache = barcodeCache;
            _skuCache = skuCache;
            _allProductsCache = allProducts;
            _searchRows = searchRows;
            _cacheReady = true;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }

        PosLogger.Log($"CATALOG cache ready: {allProducts.Count} products", "CATALOG");
    }

    private static void AddBarcodes(Dictionary<string, CatalogProductTileVm> barcodeCache, CatalogProductTileVm tile)
    {
        {
            var barcode = tile.Barcode?.Trim();
            // 2026-10-03, живой случай (тестовый аккаунт): у двух товаров один штрихкод («Батончик Mars» 0138 с
            // остатком 98 и его дубль 0143 с остатком 0). Раньше скан брал последний — дубль, и приёмка падала
            // (сервер не даёт менять дубль, 500). При совпадении берём товар с остатком, затем — с меньшим кодом.
            // 2026-11-11 (разбор кыргызстанских штрихкодов 470…): раньше основной код проигрывал ДОП. коду другого товара, если тот шёл в списке раньше
            // (запись по доп. коду считалась «уже занятой» и заменялась только при остатке 0) — скан отдавал чужой товар, хотя у кода есть законный владелец.
            // Теперь запись, попавшая в индекс по доп. коду, всегда уступает основному коду.
            if (!string.IsNullOrEmpty(barcode)
                && (!barcodeCache.TryGetValue(barcode, out var already)
                    || !string.Equals(already.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase)
                    || PreferForBarcode(tile, already)))
                barcodeCache[barcode] = tile;

            // 2026-09-12: "Дополнительные штрихкоды" из карточки товара сохранялись (см.
            // AlternateBarcodesRaw), но никогда не участвовали в поиске — ни при сканировании на
            // кассе, ни в Складе, потому что TryGetTileByBarcode индексировал только основной
            // Barcode. Регистрируем каждый дополнительный код в том же индексе — основной
            // штрихкод в приоритете при совпадении (не даём чужому доп. коду перекрыть чей-то
            // основной).
            if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
            {
                foreach (var alt in tile.AlternateBarcodesRaw.Split(
                             ['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!barcodeCache.ContainsKey(alt))
                        barcodeCache[alt] = tile;
                }
            }
        }
    }

    private bool EnsureCacheReady()
    {
        if (_cacheReady)
            return true;

        // На большом каталоге (10-20 тыс. товаров) полная пересборка кэша — это SELECT всей
        // таблицы плюс конструирование тайлов, реально несколько секунд. Без этого лока
        // несколько потоков (фоновый WarmUpCacheAsync при старте + UI-поток на первом
        // сканировании штрих-кода) запускали ЭТУ ЖЕ пересборку параллельно и независимо —
        // на большом каталоге совпадающие по времени пересборки удваивали/утраивали нагрузку
        // на SQLite и CPU и выглядели как зависание/краш кассы. Теперь конкурентный вызов
        // просто ждёт уже идущую пересборку вместо того, чтобы начинать свою.
        lock (_warmUpGate)
        {
            if (_cacheReady)
                return true;

            if (_warmUpTask != null)
            {
                try
                {
                    _warmUpTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Catalog warm-up wait failed: {ex.GetType().Name}", "WARNING");
                }

                return _cacheReady;
            }

            RebuildCacheFromDatabase();
            return _cacheReady;
        }
    }

    private void PatchFavoriteInCache(string id, bool isFavorite)
    {
        if (!_cacheReady)
            return;

        _cacheLock.EnterWriteLock();
        try
        {
            if (_skuCache.TryGetValue(id, out var tile))
                tile.IsFavorite = isFavorite;

            var index = _searchRows.FindIndex(r => string.Equals(r.Tile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _searchRows[index] = CachedProductRow.From(_searchRows[index].Tile);
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    private void PatchStockInCache(string id, double stock, bool mustWeigh)
    {
        if (!_cacheReady)
            return;

        _cacheLock.EnterWriteLock();
        try
        {
            if (!_skuCache.TryGetValue(id, out var tile))
                return;

            StockSyncService.ApplyQuantityToTile(tile, stock, mustWeigh || tile.MustWeigh); // 2026-11-01 (Б-2)
            var index = _searchRows.FindIndex(r => string.Equals(r.Tile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _searchRows[index] = CachedProductRow.From(tile);
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    public (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) SearchCache(
        string? searchText,
        bool? mustWeigh,
        FilterCriteria? criteria,
        int offset,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        if (!EnsureCacheReady())
            return (Array.Empty<CatalogProductTileVm>(), false);

        var normalized = NormalizeSearchTerm(searchText) ?? NormalizeSearchTerm(criteria?.SearchQuery);

        _cacheLock.EnterReadLock();
        try
        {
            if (normalized != null)
            {
                if (_barcodeCache.TryGetValue(normalized, out var exactBarcode)
                    && MatchesRowFilters(CachedProductRow.From(exactBarcode), mustWeigh, criteria))
                    return PaginateSingle(exactBarcode, offset, limit);

                if (_skuCache.TryGetValue(normalized, out var exactSku)
                    && MatchesRowFilters(CachedProductRow.From(exactSku), mustWeigh, criteria))
                    return PaginateSingle(exactSku, offset, limit);
            }

            var matches = new List<CatalogProductTileVm>();

            var sq = SearchMatch.Prepare(searchText ?? criteria?.SearchQuery);
            for (var pass = 0; pass < (normalized != null ? 2 : 1) && matches.Count == 0; pass++)
            {
                foreach (var row in _searchRows)
                {
                    if (!MatchesRowFilters(row, mustWeigh, criteria))
                        continue;

                    if (normalized != null && !row.MatchesText(normalized, sq, pass == 1))
                        continue;

                    matches.Add(row.Tile);
                }
            }

            var page = matches.Skip(offset).Take(limit).ToArray();
            return (page, matches.Count > offset + limit);
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    private static (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) PaginateSingle(
        CatalogProductTileVm tile,
        int offset,
        int limit)
    {
        if (offset > 0)
            return (Array.Empty<CatalogProductTileVm>(), false);

        return ([tile], false);
    }

    private static bool MatchesRowFilters(CachedProductRow row, bool? mustWeigh, FilterCriteria? criteria)
    {
        if (mustWeigh is { } weighFilter && row.MustWeigh != weighFilter)
            return false;

        if (criteria == null)
            return true;

        if (criteria.PriceMin.HasValue && row.Price < criteria.PriceMin.Value)
            return false;
        if (criteria.PriceMax.HasValue && row.Price > criteria.PriceMax.Value)
            return false;
        if (!string.IsNullOrWhiteSpace(criteria.Category)
            && !string.Equals(row.Category, criteria.Category.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(criteria.Brand)
            && !string.Equals(row.Brand, criteria.Brand.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (criteria.OnlyFavorite && !row.IsFavorite)
            return false;
        if (criteria.OnlyWeight && !row.MustWeigh)
            return false;
        if (criteria.OnlyPiece && row.MustWeigh)
            return false;
        if (criteria.OnlyInStock && row.Quantity <= 0)
            return false;

        return true;
    }

    private static string? NormalizeSearchTerm(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : SearchMatch.Normalize(text); // 2026-10-29: ё=е, пробелы схлопнуты

    private sealed class CachedProductRow
    {
        public required CatalogProductTileVm Tile { get; init; }
        public required string SortKey { get; init; }
        public required string NormalizedName { get; init; }
        public required string NormalizedSku { get; init; }
        public required string NormalizedBarcode { get; init; }
        public required bool MustWeigh { get; init; }
        public required double Price { get; init; }
        public required double Quantity { get; init; }
        public required bool IsFavorite { get; init; }
        public string? Category { get; init; }
        public string? Brand { get; init; }

        public static CachedProductRow From(CatalogProductTileVm tile) =>
            new()
            {
                Tile = tile,
                SortKey = tile.Title.Trim(),
                NormalizedName = tile.Title.Trim().ToLowerInvariant(),
                NormalizedSku = tile.Id.Trim().ToLowerInvariant(),
                NormalizedBarcode = tile.Barcode?.Trim().ToLowerInvariant() ?? string.Empty,
                MustWeigh = tile.MustWeigh,
                Price = ParsePrice(tile.PriceLine),
                Quantity = tile.Quantity,
                IsFavorite = tile.IsFavorite,
                Category = tile.Category,
                Brand = tile.Brand,
            };

        public bool MatchesText(string normalizedQuery, SearchQuery sq, bool fuzzy)
        {
            // 2026-10-29: слова в любом порядке по ключу товара (название, код, артикул, категория, бренд) + раскладка/транслит на втором проходе.
            if (fuzzy ? sq.MatchesFuzzy(Tile.SearchKey) : sq.Matches(Tile.SearchKey))
                return true;

            if (NormalizedBarcode.Length > 0
                && (NormalizedBarcode == normalizedQuery
                    || NormalizedBarcode.StartsWith(normalizedQuery, StringComparison.Ordinal)
                    || NormalizedBarcode.Contains(normalizedQuery, StringComparison.Ordinal)))
                return true;

            if (NormalizedSku == normalizedQuery
                || NormalizedSku.StartsWith(normalizedQuery, StringComparison.Ordinal)
                || NormalizedSku.Contains(normalizedQuery, StringComparison.Ordinal))
                return true;

            return NormalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal)
                   || NormalizedName.Contains(normalizedQuery, StringComparison.Ordinal);
        }

        private static double ParsePrice(string priceLine)
        {
            if (string.IsNullOrWhiteSpace(priceLine))
                return 0;

            var digits = new string(priceLine
                .TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',')
                .ToArray())
                .Replace(',', '.');

            return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
                ? price
                : 0;
        }
    }

    public void UpdateStock(string id, double stock, bool mustWeigh)
    {
        // 2026-11-06 (Б-13): «no such column: intake_date» — один раз достроить столбцы и повторить.
        try { UpdateStockCore(id, stock, mustWeigh); }
        catch (SqliteException ex) when (TryHealMissingColumn(ex)) { UpdateStockCore(id, stock, mustWeigh); }
    }

    private void UpdateStockCore(string id, double stock, bool mustWeigh)
    {
        var kind = mustWeigh ? ProductUnitKind.Kilogram : ProductUnitKind.Piece;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Products
            SET stock = @stock,
                -- Единицу трогаем, только когда товар стал весовым или своей единицы у него не
                -- было: «м», «л», «оп» с сервера не должны превращаться в «шт» (см.
                -- ProductUnitNormalizer.StorageUnit).
                unit = CASE
                    WHEN @must_weigh = 1 THEN @unit
                    WHEN must_weigh = 1 THEN unit -- 2026-11-01 (Б-2): весовой товар не превращаем в штучный правкой остатка
                    WHEN unit IS NULL OR TRIM(unit) = '' OR LOWER(TRIM(unit)) IN ('кг', 'kg') THEN @unit
                    ELSE unit
                END,
                must_weigh = CASE WHEN @must_weigh = 1 THEN 1 ELSE must_weigh END, -- 2026-11-01 (Б-2): остаток не снимает признак «весовой» (его меняют только лента с полной карточкой и полная загрузка)
                intake_date = CASE WHEN intake_date IS NULL AND @stock > 0 THEN @today ELSE intake_date END
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@stock", stock);
        command.Parameters.AddWithValue("@unit", ProductUnitNormalizer.DisplayUnit(kind));
        command.Parameters.AddWithValue("@must_weigh", mustWeigh ? 1 : 0);
        command.Parameters.AddWithValue("@today", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        PatchStockInCache(id, stock, mustWeigh);
    }

    /// <summary>2026-10-27 (цена склад-касса): свежая цена товара с сервера — в локальный каталог сразу, не ждать полной загрузки.
    /// Только колонка price (схема не менялась); наценка и скидка товара в SQLite не хранятся и не затрагиваются.
    /// Пересобирает память репозитория, чтобы скан по штрихкоду сразу дал новую цену.</summary>
    public void UpdatePrices(IEnumerable<(string Id, double Price)> prices)
    {
        var any = false;
        using (var connection = OpenConnection())
        {
            foreach (var (id, price) in prices)
            {
                if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(price) || !(price > 0))
                    continue;
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Products SET price = @price WHERE id = @id;";
                command.Parameters.AddWithValue("@price", price);
                command.Parameters.AddWithValue("@id", id.Trim());
                command.ExecuteNonQuery();
                any = true;
            }
        }

        if (any)
            ResetCache();
    }

    /// <summary>id → дата, когда у товара впервые появился остаток > 0 (см. UpdateStock/
    /// SyncReplaceAllWithDiff) — только для товаров, где эта дата уже зафиксирована.</summary>
    public Dictionary<string, DateTime> LoadIntakeDates()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, intake_date FROM Products WHERE intake_date IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                result[reader.GetString(0)] = date;
        }
        return result;
    }

    public DateTime? GetLastSyncTime()
    {
        var value = GetMetaValue("last_sync_utc");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    public void SetLastSyncTime(DateTime utcTime) => SetMetaValue("last_sync_utc", utcTime.ToString("O", CultureInfo.InvariantCulture));

    public string? GetCatalogVersionToken() => GetMetaValue("catalog_version");

    /// <summary>2026-11-06 (Б-1, И-2): сколько товаров в базе и у скольких из них задан признак is_weight (NULL = карточка пришла сокращённой лентой). Один запрос, схема не меняется.</summary>
    public (int Total, int WithWeightFlag) CountWeightFlagCoverage()
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN IsWeight IS NOT NULL THEN 1 ELSE 0 END), 0) FROM Products;";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Convert.ToInt32(reader.GetInt64(0)), Convert.ToInt32(reader.GetInt64(1))) : (0, 0);
    }

    public void SetCatalogVersionToken(string token) => SetMetaValue("catalog_version", token);

    public string? GetMetaValue(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM catalog_meta WHERE key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetMetaValue(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_meta(key, value) VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }
}
