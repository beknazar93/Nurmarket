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

    // ---------------------------------------------------------------- 2026-11-06 (Б-9): докачка полной загрузки и редкая сверка

    /// <summary>Ключ в catalog_meta: ход незаконченной полной загрузки «следующая страница|размер страницы|UTC обновления|принято товаров».
    /// Стирается вместе с остальными catalog_meta при смене аккаунта (ClearAll).</summary>
    public const string ProgressMetaKey = "full_load_progress";

    /// <summary>Ключ в catalog_meta: когда в последний раз запускалось самолечение (не чаще раза в <see cref="SelfHealRetry"/>).</summary>
    public const string SelfHealMetaKey = "self_heal_utc";

    /// <summary>Страховочная полная сверка каталога (остатки чужих касс, удаления) — раз в сутки. Лента products/changes ведёт дельту, полный каталог (155 страниц
    /// на 15 тыс. товаров) нужен редко; раньше было 8 ч, и после ночи каждый первый запуск качал всё (журнал 06.11: 13:28, 13:33, 13:46).</summary>
    public static readonly TimeSpan SafetyFullReloadInterval = TimeSpan.FromHours(24);

    /// <summary>Ход загрузки старше этого считается устаревшим (страницы сдвинулись) — качаем с первой.</summary>
    public static readonly TimeSpan ProgressMaxAge = TimeSpan.FromHours(12);

    /// <summary>Самолечение не чаще раза в сутки: сервер может вообще не присылать is_weight — лечить бесконечно незачем.</summary>
    public static readonly TimeSpan SelfHealRetry = TimeSpan.FromHours(24);

    /// <summary>Ход незаконченной загрузки: с какой страницы продолжать, каким размером страницы она шла, сколько товаров уже принято.</summary>
    public readonly record struct Progress(int NextPage, int PageSize, DateTime UpdatedUtc, int Accepted);

    public static void SaveProgress(LocalProductRepository repo, Progress p)
    {
        try
        {
            repo.SetMetaValue(ProgressMetaKey, string.Create(CultureInfo.InvariantCulture,
                $"{p.NextPage}|{p.PageSize}|{p.UpdatedUtc.ToUniversalTime():O}|{p.Accepted}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: ход полной загрузки не записан: {ex.Message}", "CATALOG");
        }
    }

    /// <summary>Ход прошлой оборванной загрузки; null — нет (или устарел, или не разобрался): тогда загрузка с первой страницы.</summary>
    public static Progress? GetProgress(LocalProductRepository repo, DateTime nowUtc)
    {
        try
        {
            var raw = repo.GetMetaValue(ProgressMetaKey);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var parts = raw.Split('|');
            if (parts.Length < 4
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) || next < 2
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size < 1
                || !DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var accepted))
                return null;
            updated = updated.ToUniversalTime();
            if (nowUtc - updated > ProgressMaxAge || updated - nowUtc > TimeSpan.FromMinutes(5))
                return null;
            return new Progress(next, size, updated, accepted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: ход полной загрузки не прочитан: {ex.Message}", "CATALOG");
            return null;
        }
    }

    public static void ClearProgress(LocalProductRepository repo)
    {
        try { repo.SetMetaValue(ProgressMetaKey, ""); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: ход полной загрузки не сброшен: {ex.Message}", "CATALOG");
        }
    }

    /// <summary>Страница, с которой продолжить оборванную загрузку: на одну раньше сохранённой (нахлёст — страницы могли чуть сдвинуться) и не меньше первой.</summary>
    public static int ResumePage(Progress p) => Math.Max(1, p.NextPage - 1);

    /// <summary>Пора ли страховочной полной сверке. <paramref name="lastFullUtc"/> — время последней настоящей полной загрузки (null/MinValue — не было).
    /// Идёт чек у кассира — откладываем (до 6 часов сверх срока); оплата/смена — всегда откладываем (проверяет вызывающий).</summary>
    public static bool IsSafetyReloadDue(DateTime? lastFullUtc, DateTime nowUtc, TimeSpan interval, bool openCheck, out string reason)
    {
        reason = "";
        if (lastFullUtc is null || lastFullUtc.Value == DateTime.MinValue)
        {
            reason = "полной загрузки с полными карточками ещё не было";
            return true;
        }

        var age = nowUtc - lastFullUtc.Value;
        if (age < TimeSpan.FromSeconds(-5))
            age = TimeSpan.MaxValue; // часы переведены назад — метка «в будущем» считается давней
        if (age < interval)
            return false;
        if (openCheck && age != TimeSpan.MaxValue && age < interval + TimeSpan.FromHours(6))
            return false; // кассир за чеком — не мешаем, повтор на следующем такте
        reason = age == TimeSpan.MaxValue
            ? "метка последней полной загрузки в будущем (часы переведены назад)"
            : $"последняя полная загрузка была {age.TotalHours:0.#} ч назад (страховочный интервал {interval.TotalHours:0.#} ч)";
        return true;
    }

    /// <summary>2026-11-06 (Б-9): после возврата связи каталог догоняет лента изменений, а не полная загрузка — столько минут от последнего момента без связи полную загрузку не начинаем
    /// (журнал 16:00: сразу после возврата 155 страниц, 100 с, без причины). Дальше — обычные правила (плановая сверка раз в сутки, самолечение).</summary>
    public static readonly TimeSpan RecoveryFeedOnlyWindow = TimeSpan.FromMinutes(20);

    /// <summary>true — связь вернулась недавно (<paramref name="lastTroubleUtc"/> — последний момент без связи или её возврата; null — проблем не было), значит полную загрузку откладываем.
    /// Условия «в базе есть каталог» и «лента поддерживается» проверяет вызывающий. Часы переведены назад (метка в будущем) — окно не действует.</summary>
    public static bool IsRecoveryFeedOnly(DateTime? lastTroubleUtc, DateTime nowUtc)
    {
        if (lastTroubleUtc is not { } last || last == DateTime.MinValue)
            return false;
        var age = nowUtc - last;
        return age >= TimeSpan.FromSeconds(-5) && age < RecoveryFeedOnlyWindow;
    }

    public static void MarkSelfHealAttempt(LocalProductRepository repo, DateTime utc)
    {
        try { repo.SetMetaValue(SelfHealMetaKey, utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: метка самолечения не записана: {ex.Message}", "CATALOG");
        }
    }

    /// <summary>Самолечение (И-2): база непустая, признак is_weight есть меньше чем у 2 % товаров (полная загрузка постранично шлёт его всегда, лента — никогда)
    /// и метки «полная загрузка выполнена» нет — база заполнена сокращённой лентой. Метка ставится после любой полной загрузки, поэтому лечение идёт один раз
    /// (даже если сервер вообще не присылает is_weight).
    /// 2026-11-06 (Б-9): попытка лечения запоминается — не чаще раза в сутки; оборванную докачку (есть ход загрузки) продолжаем без ожидания суток.</summary>
    public static bool NeedsSelfHeal(LocalProductRepository repo, out string reason) => NeedsSelfHeal(repo, DateTime.UtcNow, out reason);

    public static bool NeedsSelfHeal(LocalProductRepository repo, DateTime nowUtc, out string reason)
    {
        reason = "";
        try
        {
            if (GetLastFullLoadUtc(repo) is not null)
                return false;
            var (total, withFlag) = repo.CountWeightFlagCoverage();
            if (total <= 0 || withFlag * 50 >= total)
                return false;
            if (GetProgress(repo, nowUtc) is null
                && repo.GetMetaValue(SelfHealMetaKey) is { Length: > 0 } last
                && DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAttempt)
                && nowUtc - lastAttempt.ToUniversalTime() < SelfHealRetry)
                return false; // уже пробовали недавно: сервер не отдаёт is_weight или загрузка сорвалась — не гоняем каждый запуск
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
