using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Отказ сервера по тарифу компании (403): <c>plan_client_not_allowed</c> (клиент не разрешён тарифом — «Мини» работает только с телефона) или <c>plan_limit</c> (лимит тарифа: сотрудники, кассы).</summary>
public sealed record PlanBlock(string Code, string? Plan, string? Client, string? Limit, int? Max, int? Current, string? UpgradePlan, string? Detail)
{
    public const string ClientNotAllowed = "plan_client_not_allowed";
    public const string LimitReached = "plan_limit";

    public bool IsClientBlock => Code == ClientNotAllowed;
}

/// <summary>
/// 2026-11-20 (ответ Умара 20.11, В.2, тариф «Мини»): 403 <c>plan_client_not_allowed</c> приходит на входе (auth/login/), на обновлении входа (auth/refresh/) и на любом запросе с токеном; 403 <c>plan_limit</c> — на создании
/// сотрудника или второй кассы. Это не «нет прав» и не «сессия истекла»: окно с текстом сервера (<c>detail</c>) и кнопкой «Перейти на Старт» показывает PlanBlockPrompt. Монитор разбирает тело 403 (каждый запрос кассы проходит
/// через JwtBearerRefreshHandler), помнит последний отказ и объявляет его событием не чаще раза в 5 минут на код — запросов фоном много, а окно одно. Исключений наружу нет.
/// </summary>
public static class PlanBlockMonitor
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTime> LastRaised = new(StringComparer.Ordinal);
    private static PlanBlock? _last;

    /// <summary>Не повторять событие чаще этого срока на один код отказа.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>Часы монитора (UTC); подменяются только в тестах.</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>Объявлен новый отказ по тарифу. Вызывается из потока запроса — подписчик сам переходит в UI-поток.</summary>
    public static event Action<PlanBlock>? Blocked;

    /// <summary>Последний отказ (для надписи и повторного показа); null — отказов не было.</summary>
    public static PlanBlock? Last
    {
        get { lock (Sync) return _last; }
    }

    /// <summary>Только что (до 5 минут назад) объявлен отказ «клиент не разрешён тарифом»: вход после него потерян по этой причине, и вопрос «Сессия истекла» поверх окна тарифа не нужен.</summary>
    public static bool RecentClientBlock
    {
        get
        {
            lock (Sync)
                return LastRaised.TryGetValue(PlanBlock.ClientNotAllowed, out var at) && Clock() - at < Cooldown;
        }
    }

    /// <summary>Разбор тела ответа. null — это не отказ по тарифу (другой код, не JSON, не объект).</summary>
    public static PlanBlock? TryParse(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText) || !bodyText.Contains("plan_", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            return TryParse(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PlanBlock? TryParse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
            return null;
        var code = codeEl.GetString();
        if (code is not (PlanBlock.ClientNotAllowed or PlanBlock.LimitReached))
            return null;
        static string? Str(JsonElement o, string n) =>
            o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        static int? Int(JsonElement o, string n) =>
            o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        return new PlanBlock(code, Str(root, "plan"), Str(root, "client"), Str(root, "limit"), Int(root, "max"), Int(root, "current"), Str(root, "upgrade_plan"), Str(root, "detail"));
    }

    /// <summary>Отказ по тарифу из исключения запроса (403 с кодом plan_*). null — исключение другое.</summary>
    public static PlanBlock? FromException(Exception? ex) =>
        ex is ApiException { StatusCode: 403, Payload: { } p } ? TryParse(p) : null;

    /// <summary>Тело ответа 403 — разобрать и, если это отказ по тарифу, объявить. true — объявлено событием (не чаще раза в 5 минут на код).</summary>
    public static bool Note(int status, string? bodyText)
    {
        if (status != 403)
            return false;
        return Note(TryParse(bodyText));
    }

    public static bool Note(PlanBlock? block)
    {
        if (block is null)
            return false;
        bool raise;
        lock (Sync)
        {
            _last = block;
            var now = Clock();
            raise = !LastRaised.TryGetValue(block.Code, out var at) || now - at >= Cooldown;
            if (raise)
                LastRaised[block.Code] = now;
        }
        if (!raise)
            return false;
        PosLogger.Log($"Тариф компании не позволяет действие: {block.Code}, тариф {block.Plan ?? "?"}, клиент {block.Client ?? "?"}{(block.Limit is null ? "" : ", лимит " + block.Limit)}.", "AUTH");
        try { Blocked?.Invoke(block); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Окно «Тариф»: обработчик упал: {ex.GetType().Name}", "WARNING"); }
        return true;
    }

    /// <summary>Только для тестов: чистое состояние.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            LastRaised.Clear();
            _last = null;
        }
        Clock = () => DateTime.UtcNow;
    }
}
