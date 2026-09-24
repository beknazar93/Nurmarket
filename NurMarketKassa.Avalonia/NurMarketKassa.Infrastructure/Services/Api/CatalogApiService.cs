using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация каталожных запросов поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// </summary>
public sealed class CatalogApiService : ICatalogApiService
{
    private readonly NurMarketApiClient _client;

    public CatalogApiService(NurMarketApiClient client) => _client = client;

    public async Task<List<JsonElement>> GetAgentProductsAsync(CancellationToken ct = default)
    {
        foreach (var path in new[]
                 {
                     "api/main/agents/me/products/",
                     "api/main/products/agent-stock/",
                     "api/main/agents/products/",
                 })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                var list = NurMarketApiClient.UnwrapList(data);
                if (list.Count > 0)
                    return list;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next path */
            }
        }

        return new List<JsonElement>();
    }

    public async Task<bool> SetProductFavoriteAsync(string productId, bool isFavorite, CancellationToken ct = default)
    {
        var pid = productId.Trim();
        if (pid.Length == 0)
            return false;

        var escaped = Uri.EscapeDataString(pid);
        var bodyFavorite = new Dictionary<string, string> { ["is_favorite"] = isFavorite ? "true" : "false" };
        var bodyToggle = new Dictionary<string, string>();

        var attempts = new List<(HttpMethod Method, string Path, Dictionary<string, string>? Body)>
        {
            (HttpMethod.Patch, $"api/main/products/{escaped}/", bodyFavorite),
            (HttpMethod.Patch, $"api/main/products/list/{escaped}/", bodyFavorite),
            (HttpMethod.Post, $"api/main/products/{escaped}/favorite/", bodyToggle),
            (HttpMethod.Post, $"api/main/products/list/{escaped}/favorite/", bodyToggle),
            (HttpMethod.Post, $"api/main/products/{escaped}/toggle-favorite/", bodyToggle),
            (HttpMethod.Put, $"api/main/products/{escaped}/favorite/", bodyFavorite),
        };

        foreach (var (method, path, body) in attempts)
        {
            try
            {
                await _client.RequestAsync(method, path, body, null, ct).ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next */
            }
        }

        return false;
    }

    public async Task<List<ProductDto>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new List<ProductDto>();

        limit = Math.Clamp(limit, 1, 20000);
        var list = new List<ProductDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        var hasNext = true;

        while (hasNext && list.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var qs = new Dictionary<string, string>
            {
                ["search"] = q,
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "300",
            };

            var response = await _client.RequestDataAsync<ApiListResponse<ProductDto>>(
                HttpMethod.Get, "api/main/products/list/", null, qs, ct).ConfigureAwait(false);

            if (response?.Results == null || response.Results.Count == 0)
                break;

            foreach (var item in response.Results)
            {
                if (string.IsNullOrEmpty(item.Id) || !seen.Add(item.Id))
                    continue;
                list.Add(item);
                if (list.Count >= limit)
                    break;
            }

            hasNext = response.Results.Count >= 300;
            page++;
            if (page > 100)
                break;
        }

        return list;
    }

    public async Task<CatalogVersionInfo?> ProductsCatalogVersionAsync(CancellationToken ct = default)
    {
        foreach (var path in new[]
                 {
                     "api/main/products/catalog-meta/",
                     "api/main/products/meta/",
                     "api/main/catalog/version/",
                     "api/main/products/version/",
                 })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                var parsed = CatalogVersionParser.TryParseMeta(data);
                if (parsed != null && !parsed.IsEmpty)
                {
                    parsed = new CatalogVersionInfo
                    {
                        CatalogVersion = parsed.CatalogVersion,
                        LastModified = parsed.LastModified,
                        Token = parsed.Token,
                        Source = path,
                    };
                    return parsed;
                }
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        foreach (var path in new[] { "api/main/products/list/", "api/main/products/" })
        {
            try
            {
                var qs = new Dictionary<string, string>
                {
                    ["page"] = "1",
                    ["page_size"] = "1",
                    ["ordering"] = "-updated_at",
                };
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                var parsed = CatalogVersionParser.TryParseListProbe(data);
                if (parsed != null && !parsed.IsEmpty)
                {
                    parsed = new CatalogVersionInfo
                    {
                        CatalogVersion = parsed.CatalogVersion,
                        LastModified = parsed.LastModified,
                        Token = parsed.Token,
                        Source = $"{path} (probe)",
                    };
                    return parsed;
                }
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        return null;
    }

    public async Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default)
    {
        var outList = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new[] { "api/main/products/list/", "api/main/products/" };
        ApiException? last404 = null;
        limit = Math.Max(1, limit);
        maxPages = maxPages <= 0 ? 100 : Math.Max(1, maxPages);
        const int pageSize = 300;

        foreach (var path in paths)
        {
            outList.Clear();
            seen.Clear();

            try
            {
                var page = 1;
                var hasNext = true;

                while (hasNext && outList.Count < limit && page <= maxPages)
                {
                    ct.ThrowIfCancellationRequested();
                    var qs = new Dictionary<string, string>
                    {
                        ["page"] = page.ToString(CultureInfo.InvariantCulture),
                        ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture),
                    };

                    var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                    var batch = NurMarketApiClient.UnwrapList(data);
                    if (batch.Count == 0)
                        break;

                    // На большом каталоге (10-20 тыс. товаров) это десятки последовательных
                    // запросов — без лога прогресса синхронизация, которая просто идёт медленно,
                    // в логах выглядит неотличимо от зависшей.
                    PosLogger.Log($"CATALOG fetch: page {page}, +{batch.Count} (всего {outList.Count})", "CATALOG");

                    foreach (var p in batch)
                    {
                        var pid = TryProductIdString(p);
                        if (string.IsNullOrEmpty(pid) || !seen.Add(pid))
                            continue;
                        outList.Add(p);
                        if (outList.Count >= limit)
                            return outList;
                    }

                    hasNext = HasNextPage(data, batch.Count, pageSize);
                    page++;
                }

                // Запрос по этому пути отработал без исключения — значит это и есть рабочий
                // эндпоинт для этого аккаунта, даже если товаров 0 (пустой каталог). Раньше
                // здесь стояло "if (outList.Count > 0)", из-за чего пустой, но УСПЕШНЫЙ
                // результат ошибочно считался поводом пробовать запасной путь — а тот у
                // части аккаунтов не существует (404), и этот чужой 404 в итоге выдавался
                // наружу как "не удалось загрузить каталог" вместо честного "каталог пуст".
                return outList;
            }
            catch (ApiException e)
            {
                last404 = e;
                if (e.StatusCode != 404)
                    throw;
            }
        }

        if (last404 != null && outList.Count == 0)
            throw last404;
        return outList;
    }

    public async Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default)
    {
        var pid = Uri.EscapeDataString(productId.Trim());
        if (pid.Length == 0)
            return null;
        foreach (var path in new[] { $"api/main/products/{pid}/", $"api/main/products/list/{pid}/" })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                if (data.ValueKind != JsonValueKind.Object)
                    continue;
                if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object &&
                    inner.TryGetProperty("id", out _))
                    return inner.Clone();
                if (data.TryGetProperty("id", out _))
                    return data.Clone();
            }
            catch (ApiException e)
            {
                if (e.StatusCode is 404 or 405 or 410)
                    continue;
                return null;
            }
        }

        return null;
    }

    public async Task<bool> TrySetProductStockAsync(string productId, double quantity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        var escaped = Uri.EscapeDataString(productId.Trim());
        var qtyText = quantity.ToString("0.###", CultureInfo.InvariantCulture);
        var bodies = new[]
        {
            new Dictionary<string, string> { ["stock_quantity"] = qtyText, ["quantity"] = qtyText },
            new Dictionary<string, string> { ["quantity"] = qtyText },
        };

        var paths = new[]
        {
            $"api/main/products/{escaped}/",
            $"api/main/products/list/{escaped}/",
            $"api/main/agents/me/products/{escaped}/",
        };

        foreach (var path in paths)
        {
            foreach (var body in bodies)
            {
                try
                {
                    await _client.RequestAsync(HttpMethod.Patch, path, body, null, ct).ConfigureAwait(false);
                    return true;
                }
                catch (ApiException ex) when (ex.StatusCode is 404 or 405)
                {
                    /* next */
                }
            }
        }

        return false;
    }

    /// <summary>Удаление товара (2026-09-07): DELETE по тем же двум путям карточки, что и
    /// PATCH в TrySetProductStockAsync/UpdateProductAsync — сервер отдаёт карточку то по
    /// "products/{id}/", то по "products/list/{id}/"; 404/405 = путь не тот, пробуем следующий.
    /// Успешный DELETE в DRF отвечает 204 без тела — RequestAsync на пустое тело возвращает
    /// default(JsonElement), это не ошибка.</summary>
    public async Task<bool> DeleteProductAsync(string productId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        var escaped = Uri.EscapeDataString(productId.Trim());
        foreach (var path in new[] { $"api/main/products/{escaped}/", $"api/main/products/list/{escaped}/" })
        {
            try
            {
                await _client.RequestAsync(HttpMethod.Delete, path, null, null, ct).ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next */
            }
        }

        return false;
    }

    public Task<List<string>> GetCategoryNamesAsync(CancellationToken ct = default) =>
        FetchReferenceNamesAsync("api/main/categories/", ct);

    public Task<List<string>> GetBrandNamesAsync(CancellationToken ct = default) =>
        FetchReferenceNamesAsync("api/main/brands/", ct);

    public Task CreateCategoryAsync(string name, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Post, "api/main/categories/",
            new Dictionary<string, string> { ["name"] = name.Trim() }, null, ct);

    public Task CreateBrandAsync(string name, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Post, "api/main/brands/",
            new Dictionary<string, string> { ["name"] = name.Trim() }, null, ct);

    /// <summary>Справочники категорий/брендов (2026-09-07): DRF-пагинация
    /// {"count","next","results":[{id,name,parent}]}, как у списка товаров — идём по страницам,
    /// пока "next" не станет null. Наружу только имена: карточка товара отправляет
    /// category_name/brand_name (см. UpdateProductAsync), сервер сопоставляет по имени.</summary>
    private async Task<List<string>> FetchReferenceNamesAsync(string path, CancellationToken ct)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 50; page++)
        {
            ct.ThrowIfCancellationRequested();
            var qs = new Dictionary<string, string>
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "300",
            };

            var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
            if (!TryGetReferenceResults(data, out var results))
                break;

            var pageCount = 0;
            foreach (var item in results.EnumerateArray())
            {
                pageCount++;
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && nameEl.GetString()?.Trim() is { Length: > 0 } name
                    && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (pageCount == 0 || !hasNext)
                break;
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    private static bool TryGetReferenceResults(JsonElement data, out JsonElement results)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            results = data;
            return true;
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("results", out results)
            && results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        results = default;
        return false;
    }

    public async Task<string?> UploadProductImageAsync(string productId, string localFilePath, bool isPrimary, CancellationToken ct = default)
    {
        var pid = Uri.EscapeDataString(productId.Trim());
        var fields = new Dictionary<string, string> { ["is_primary"] = isPrimary ? "true" : "false" };
        var result = await _client
            .UploadFileAsync($"api/main/products/{pid}/images/", "image", localFilePath, fields, ct)
            .ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("image_url", out var url) &&
            url.ValueKind == JsonValueKind.String)
            return url.GetString();

        return null;
    }

    public async Task<int> GetWeightProductCountAsync(CancellationToken ct = default)
    {
        var qs = new Dictionary<string, string> { ["is_weight"] = "true", ["page"] = "1", ["page_size"] = "1" };
        var data = await _client.RequestAsync(HttpMethod.Get, "api/main/products/list/", null, qs, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("count", out var countEl)
            && countEl.ValueKind == JsonValueKind.Number
            && countEl.TryGetInt32(out var count)
            ? count
            : 0;
    }

    public async Task<bool> SendProductsToScaleAsync(int pluStart, IReadOnlyList<string> productIds, CancellationToken ct = default)
    {
        var body = new { plu_start = pluStart, product_ids = productIds };
        await _client.RequestAsync(HttpMethod.Post, "api/users/scales/send-products/", body, null, ct)
            .ConfigureAwait(false);
        return true;
    }

    public Task<byte[]?> DownloadScaleExportAsync(bool translit = true, CancellationToken ct = default)
    {
        var qs = new Dictionary<string, string> { ["format"] = "txp", ["translit"] = translit ? "1" : "0" };
        return _client.DownloadAsync("api/main/products/scale-export/", qs, ct);
    }

    /// <summary>Адрес создания товара — подтверждён через DevTools живого сайта (Network,
    /// 2026-09-04): реальный эндпоинт "api/main/products/create-manual/", а не "api/main/products/"
    /// (тот отвечал то 404, то 405 — существует, но POST не принимает). Остальные пути оставлены
    /// как запасной вариант на случай, если у части аккаунтов маршрут всё же другой.</summary>
    public async Task<JsonElement> CreateProductAsync(ProductEditRequest request, CancellationToken ct = default)
    {
        var body = BuildProductBody(request);
        var paths = new[]
        {
            "api/main/products/create-manual/",
            "api/main/products/list/",
            "api/main/products/",
            "api/main/agents/me/products/",
        };

        ApiException? last = null;
        foreach (var path in paths)
        {
            try
            {
                return await _client.RequestAsync(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                last = ex;
            }
        }

        throw last!;
    }

    public Task<JsonElement> UpdateProductAsync(string productId, ProductEditRequest request, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Patch, $"api/main/products/{Uri.EscapeDataString(productId.Trim())}/", BuildProductBody(request), null, ct);

    public async Task<JsonElement?> FindWarehouseProductByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.RequestAsync(
                    HttpMethod.Get, $"api/main/products/warehouse-barcode/{Uri.EscapeDataString(barcode.Trim())}/", null, null, ct)
                .ConfigureAwait(false);
            // Ответ — {"product": {...}}; на всякий случай принимаем и голый объект товара.
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("product", out var product)
                && product.ValueKind == JsonValueKind.Object)
                return product.Clone();
            return data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<JsonElement?> FindGlobalProductByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.RequestAsync(
                    HttpMethod.Get, $"api/main/products/global-barcode/{Uri.EscapeDataString(barcode.Trim())}/", null, null, ct)
                .ConfigureAwait(false);
            return data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Task<JsonElement> CreateProductFromGlobalBarcodeAsync(string barcode, string name, double price, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Post,
            "api/main/products/create-by-barcode/",
            new Dictionary<string, object?>
            {
                ["barcode"] = barcode.Trim(),
                ["name"] = name.Trim(),
                ["price"] = price.ToString("0.##", CultureInfo.InvariantCulture),
            },
            null,
            ct);

    public Task<JsonElement> PatchProductFieldsAsync(string productId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Patch, $"api/main/products/{Uri.EscapeDataString(productId.Trim())}/", fields, null, ct);

    public Task<JsonElement> ListSuppliersAsync(CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Get,
            "api/main/clients/",
            null,
            new Dictionary<string, string> { ["type"] = "suppliers", ["page_size"] = "500" },
            ct);

    public Task<JsonElement> CreateSupplierReceiptAsync(string supplierId, object body, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Post, $"api/main/suppliers/{Uri.EscapeDataString(supplierId.Trim())}/receipt/", body, null, ct);

    public Task<JsonElement> ListSupplierReceiptsAsync(int page, int limit, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Get,
            "api/main/suppliers/receipts/",
            null,
            new Dictionary<string, string>
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            ct);

    /// <summary>Доп. штрихкоды — сервер теперь (2026-09-21, подтверждено живым запросом к API)
    /// ожидает массив объектов {"barcode","name","quantity"}, а не голые строки, как раньше:
    /// голая строка в этом поле, скорее всего, отклоняется DRF ("Expected a dictionary, but got
    /// str") или как минимум лишает штрихкод названия/количества, заданных на сайте. Для строк,
    /// которые кассир не трогал в многострочном поле, подставляем уже известные название/
    /// количество (KnownAlternateBarcodeVariants, пришли при загрузке карточки) — иначе Save из
    /// кассы молча стирал бы «Название»/«Кол-во в упаковке» варианта, заданные на сайте, даже
    /// если сам штрихкод не менялся. Для новых, вручную вписанных в кассе штрихкодов название
    /// пустое — эта форма пока не умеет его задавать.</summary>
    private static object[] BuildAlternateBarcodes(ProductEditRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.AlternateBarcodes))
            return Array.Empty<object>();

        var known = r.KnownAlternateBarcodeVariants;
        var codes = r.AlternateBarcodes.Split(
            ['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<object>(codes.Length);
        foreach (var code in codes)
        {
            var match = known?.FirstOrDefault(v => string.Equals(v.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
            result.Add(new Dictionary<string, object?>
            {
                ["barcode"] = code,
                ["name"] = match?.Name ?? "",
                ["quantity"] = match?.Quantity ?? 0,
            });
        }
        return result.ToArray();
    }

    private static Dictionary<string, object?> BuildProductBody(ProductEditRequest r)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = r.Name.Trim(),
            ["unit"] = r.Unit,
            ["is_weight"] = r.IsWeight,
            ["quantity"] = r.Quantity,
            ["kind"] = string.IsNullOrWhiteSpace(r.Kind) ? "product" : r.Kind,
            ["article"] = r.Article?.Trim() ?? "",
            ["barcode"] = string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode.Trim(),
            ["alternate_barcodes"] = BuildAlternateBarcodes(r),
            ["category_name"] = r.CategoryName?.Trim() ?? "",
            ["brand_name"] = r.BrandName?.Trim() ?? "",
            ["description"] = r.Description?.Trim() ?? "",
            ["purchase_price"] = (r.PurchasePrice ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            ["markup_percent"] = (r.MarkupPercent ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            ["price"] = (r.Price ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            ["wholesale_price"] = (r.WholesalePrice ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            ["discount_percent"] = (r.DiscountPercent ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            ["country"] = r.Country?.Trim() ?? "",
            // Заглавными или null — так шлёт веб-форма (см. buildProductPayload в исходниках сайта).
            ["hotkey_group"] = string.IsNullOrWhiteSpace(r.HotkeyGroup) ? null : r.HotkeyGroup.Trim().ToUpperInvariant(),
            // plu — только число или null (не строка), см. buildProductPayload в исходниках сайта.
            ["plu"] = r.IsWeight ? (object?)r.Plu : null,
        };

        var characteristics = BuildCharacteristics(r);
        if (characteristics != null)
            body["characteristics"] = characteristics;

        if (r.IsNew)
        {
            // Новый товар: веб-форма всегда шлёт эти поля (пустыми, если акции не заданы) —
            // предположительно сериализатор их ждёт при создании.
            body["stock"] = false;
            body["promotion_rules_input"] = Array.Empty<object>();
        }

        if (r.EnablePieceSale && r.PackageQuantity is > 0)
        {
            // Только когда кассир сам включил галочку в этой сессии — есть что отправлять.
            body["packages_input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "упаковка",
                    ["quantity_in_package"] = r.PackageQuantity!.Value,
                    ["unit"] = r.Unit,
                    ["piece_unit_price"] = (r.PackagePiecePrice ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
                },
            };
        }
        else if (r.IsNew)
        {
            // Новый товар без поштучной продажи — пустой массив, как шлёт веб-форма.
            body["packages_input"] = Array.Empty<object>();
        }
        // При редактировании без включённой галочки packages_input не шлём вовсе — чтобы не
        // затереть уже настроенную на сайте поштучную продажу, для которой в этой форме нет
        // поля предзаполнения (сам чекбокс всегда стартует выключенным).

        // 2026-09-12: НЕ ПРОВЕРЕНО на реальном сервере (нет подтверждённого примера запроса от
        // веб-формы для создания "Комплекта", в отличие от остальных полей этого метода — те все
        // сверены с buildProductPayload сайта). Единственная имеющаяся зацепка — ProductCatalogMapper
        // при ЧТЕНИИ уже существующего комплекта находит его состав в том же поле "packages", что и
        // поштучную продажу обычного товара (см. её комментарий), только с другой формой записи —
        // поэтому здесь по аналогии используется то же "packages_input", с товар+количество вместо
        // фасовки. Если сервер ждёт другое имя поля/форму — этот блок нужно поправить по реальному
        // запросу с сайта (DevTools → Network → создание комплекта).
        if (string.Equals(r.Kind, "bundle", StringComparison.OrdinalIgnoreCase) && r.BundleItems is { Count: > 0 })
        {
            body["packages_input"] = r.BundleItems
                .Select(item => new Dictionary<string, object?>
                {
                    ["product"] = item.ProductId,
                    ["quantity"] = item.Quantity,
                })
                .ToArray();
        }

        // Убираем поля, которых не было в источнике данных (см. ProductEditRequest.SendQuantity
        // и соседние). Сервер трактует тело как «стало столько», поэтому отправить сюда ноль
        // вместо «не трогать» — значит обнулить остаток или цену живого товара. Для обычной
        // формы редактирования все признаки true, и тело остаётся прежним.
        if (!r.SendQuantity) body.Remove("quantity");
        if (!r.SendPrice) body.Remove("price");
        if (!r.SendPurchasePrice) body.Remove("purchase_price");
        if (!r.SendMarkupPercent) body.Remove("markup_percent");

        return body;
    }

    private static Dictionary<string, object?>? BuildCharacteristics(ProductEditRequest r)
    {
        if (!r.HeightCm.HasValue && !r.WidthCm.HasValue && !r.DepthCm.HasValue && !r.WeightKg.HasValue)
            return null;

        return new Dictionary<string, object?>
        {
            ["height_cm"] = r.HeightCm?.ToString("0.##", CultureInfo.InvariantCulture),
            ["width_cm"] = r.WidthCm?.ToString("0.##", CultureInfo.InvariantCulture),
            ["depth_cm"] = r.DepthCm?.ToString("0.##", CultureInfo.InvariantCulture),
            ["factual_weight_kg"] = r.WeightKg?.ToString("0.##", CultureInfo.InvariantCulture),
            ["description"] = "",
        };
    }

    private static bool HasNextPage(JsonElement data, int batchCount, int pageSize)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            {
                var url = next.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }
        }

        return batchCount >= pageSize;
    }

    private static string? TryProductIdString(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id))
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }
}
