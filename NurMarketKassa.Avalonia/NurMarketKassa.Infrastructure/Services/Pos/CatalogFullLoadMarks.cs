using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06 (дефект Б-1, расследование 06.11.2026): лента products/changes несёт сокращённую карточку (9 полей: нет единицы, признака «весовой»,
/// PLU, штрихкода, вида, фото). Раньше пустая база заполнялась этой лентой как полным каталогом — весовые товары становились штучными, штрихкоды и PLU
/// не находились, а страховочная полная загрузка (раз в 8 ч для каталога больше 3000) обнулялась каждым перезапуском. Здесь — общие правила:
/// (1) «сокращённая ли лента» (первая загрузка тогда только по страницам); (2) метка «последняя ПОЛНАЯ загрузка» в catalog_meta (переживает перезапуск);
/// (3) самолечение уже испорченной базы — один раз полная загрузка после обновления. Вынесено из AvaloniaCatalogCacheService, чтобы проверяться тестами.
/// </summary>
public static class CatalogFullLoadMarks
{
    /// <summary>Ключ в catalog_meta: UTC-время последней полной загрузки каталога ПОЛНЫМИ карточками (постранично products/list или лента с полными полями).
    /// Стирается вместе с остальными catalog_meta при смене аккаунта (ClearAll).</summary>
    public const string FullLoadMetaKey = "full_load_utc";

    /// <summary>Сколько первых карточек страницы ленты смотрим на наличие ключей единицы/веса/штрихкода.</summary>
    private const int SampleSize = 30;

    private static readonly string[] BarcodeKeys = ["barcode", "sku", "ean", "alternate_barcodes", "plu"];

    /// <summary>Лента «сокращённая»: в первых карточках страницы нет НИ ОДНОГО ключа единицы/веса (unit, is_weight…) и штрихкода/PLU. Пустая страница сокращённой не считается.</summary>
    public static bool IsShortFeedPage(IReadOnlyList<JsonElement> items)
    {
        if (items.Count == 0)
            return false;
        var n = Math.Min(items.Count, SampleSize);
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (CatalogFeedSync.HasUnitOrWeightKey(item))
                return false;
            foreach (var key in BarcodeKeys)
                if (item.TryGetProperty(key, out _))
                    return false;
        }

        return true;
    }

    /// <summary>Время последней полной загрузки из базы; null — метки нет (база старая или загружена до этой правки).</summary>
    public static DateTime? GetLastFullLoadUtc(LocalProductRepository repo)
    {
        try
        {
            var raw = repo.GetMetaValue(FullLoadMetaKey);
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.ToUniversalTime();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: метка полной загрузки не прочитана: {ex.Message}", "CATALOG");
        }

        return null;
    }

    public static void MarkFullLoad(LocalProductRepository repo, DateTime utc)
    {
        try { repo.SetMetaValue(FullLoadMetaKey, utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: метка полной загрузки не записана: {ex.Message}", "CATALOG");
        }
    }

    /// <summary>Когда была последняя полная загрузка для отсчёта страховочного интервала: своя метка; нет её (база до обновления) — прежняя «last_sync_utc»
    /// (её ставили только полные загрузки); нет и её — null (полная загрузка нужна).</summary>
    public static DateTime? LastFullLoadOrLegacy(LocalProductRepository repo)
    {
        if (GetLastFullLoadUtc(repo) is { } mark)
            return mark;
        try { return repo.GetLastSyncTime()?.ToUniversalTime(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Самолечение (И-2): база непустая, признак is_weight есть меньше чем у 2 % товаров (полная загрузка постранично шлёт его всегда, лента — никогда)
    /// и метки «полная загрузка выполнена» нет — база заполнена сокращённой лентой. Метка ставится после любой полной загрузки, поэтому лечение идёт один раз
    /// (даже если сервер вообще не присылает is_weight).</summary>
    public static bool NeedsSelfHeal(LocalProductRepository repo, out string reason)
    {
        reason = "";
        try
        {
            if (GetLastFullLoadUtc(repo) is not null)
                return false;
            var (total, withFlag) = repo.CountWeightFlagCoverage();
            if (total <= 0 || withFlag * 50 >= total)
                return false;
            reason = $"в базе {total} товаров, признак «весовой» (is_weight) есть у {withFlag}, метки полной загрузки нет — база заполнена сокращённой лентой";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: проверка полноты каталога не удалась: {ex.Message}", "CATALOG");
            return false;
        }
    }
}
