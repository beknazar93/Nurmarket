namespace NurMarketKassa.Services;

/// <summary>
/// Этот файл изолирует локальный каталог товаров при смене учётной записи кассира:
/// очищает SQLite-кэш и помечает необходимость принудительной синхронизации с API сайта.
/// </summary>
public static class AccountCatalogIsolation
{
    public static bool RequireForcedCatalogSync { get; private set; }

    public static void PrepareForAuthenticatedUser(string email, string? userId)
    {
        var key = BuildUserKey(email, userId);
        var previous = UserPreferences.Instance.LastCatalogUserKey;

        if (!string.IsNullOrEmpty(previous)
            && string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
        {
            RequireForcedCatalogSync = false;
            return;
        }

        PosLogger.Log(
            $"Смена пользователя: «{previous ?? "—"}» → «{key}». Очистка локального каталога.",
            "AUTH");

        ClearLocalCatalogData();
        UserPreferences.Instance.LastCatalogUserKey = key;
        UserPreferences.Instance.SaveToDisk();
        RequireForcedCatalogSync = true;
        ResetCatalogCachesAndReload();
    }

    /// <summary>Списки товаров в памяти — пустые, загрузка прежнего аккаунта отброшена, каталог
    /// нового аккаунта качается сразу (см. AvaloniaCatalogCacheService.ResetForAccountChange).</summary>
    public static void ResetCatalogCachesAndReload()
    {
        try
        {
            CatalogCacheService.ClearInMemory();
            if (NurMarketKassa.AvaloniaHost.App.GetRequiredService<NurMarketKassa.Core.Contracts.ICatalogCacheService>()
                is NurMarketKassa.AvaloniaHost.Services.AvaloniaCatalogCacheService cache)
                cache.ResetForAccountChange();
            NurMarketKassa.AvaloniaHost.App.GetRequiredService<SyncService>().RequestCatalogSyncNow();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG reset after account change failed: {ex.Message}", "CATALOG");
        }
    }

    public static void ClearForcedCatalogSyncFlag() => RequireForcedCatalogSync = false;

    public static void ClearLocalCatalogData()
    {
        try { LocalProductRepository.Instance.ClearAll(); } catch (Exception ex) { PosLogger.Log($"CATALOG clear failed: {ex.Message}", "CATALOG"); }
    }

    private static string BuildUserKey(string email, string? userId)
    {
        var mail = (email ?? "").Trim().ToLowerInvariant();
        var id = (userId ?? "").Trim();
        return string.IsNullOrEmpty(id) ? mail : $"{mail}|{id}";
    }
}

/// <summary>
/// Этот файл отправляет запросы на возврат продажи через REST API сайта:
/// полный возврат чека или возврат отдельных позиций.
/// </summary>
public static class PosRefundService
{
    /// <param name="isDefect">2026-10-06 (О-33): возврат брака — сервер не возвращает товар на склад. Причина теперь тоже
    /// уходит на сервер (проверено 06.10: хранится в документе возврата, раньше сервер её не принимал).</param>
    public static async Task<System.Text.Json.JsonElement> RefundWholeSaleAsync(
        Api.ISalesApiService api,
        string saleId,
        string reason,
        string? cashboxId,
        bool isDefect = false,
        CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        // 2026-10-15, ревью: idempotencyKey — тот же при повторе того же возврата (ReturnSaleDialog), null — новый.
        // 2026-10-06, владелец: «главное не трогай продуктовый» — в «Продуктах» прежний запрос возврата.
        // 2026-11-01 (аудит «10 из 10», Ж-03): причина есть — тоже запрос с деталями: в «Продуктах» прежний запрос причину терял, и у владельца в «Возвратах» стояло «—».
        if ((MarketSpheres.IsClothing || isDefect || !string.IsNullOrWhiteSpace(reason)) && api is Api.IPosExchangeApi details)
        {
            // 2026-11-01 (ответ Умара, п. 3): ответ сервера (refund.cash, debt_adjustment) возвращается наружу — окно возврата берёт из него выдачу.
            return await details.PosReturnWithDetailsAsync(saleId, null, reason, isDefect, ct, idempotencyKey).ConfigureAwait(false);
        }
        return await api.PosReturnWholeSaleAsync(saleId, reason, ct, idempotencyKey).ConfigureAwait(false);
    }

    public static async Task<System.Text.Json.JsonElement> RefundLinesAsync(
        Api.ISalesApiService api,
        string saleId,
        IReadOnlyList<PosRefundLineRequest> lines,
        string reason,
        string? cashboxId,
        bool isDefect = false,
        CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        // Один запрос на весь возврат — тот же, что делает сайт: POST pos/sales/{id}/return/
        // с массивом items. Раньше здесь был цикл по позициям, и каждая уходила в
        // cart-item-deletions/get/, которого на сервере нет (404) — возврат не работал никогда,
        // а кассир получал «Не удалось вернуть позицию: <название товара>».
        ct.ThrowIfCancellationRequested();
        // 2026-10-29 (ревью офлайн-операций, Б-1): возврат по строкам без строк не отправляем — запрос без items сервер понимает как «вернуть весь чек».
        if (lines.Count == 0)
            throw new ArgumentException("Нет строк для возврата.", nameof(lines));
        // 2026-10-06, владелец: «главное не трогай продуктовый» — в «Продуктах» прежний запрос возврата.
        // 2026-11-01 (Ж-03): см. выше — причина должна дойти до документа возврата и в «Продуктах».
        if ((MarketSpheres.IsClothing || isDefect || !string.IsNullOrWhiteSpace(reason)) && api is Api.IPosExchangeApi details)
        {
            return await details.PosReturnWithDetailsAsync(saleId, lines, reason, isDefect, ct, idempotencyKey).ConfigureAwait(false);
        }
        // 2026-10-15, ревью: ключ — от ReturnSaleDialog, тот же при повторе того же возврата.
        return await api.PosReturnSaleAsync(saleId, lines, ct, idempotencyKey).ConfigureAwait(false);
    }
}
