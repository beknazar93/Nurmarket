using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Модуль из GET public/modules/: цена в месяц (Monthly) и разовая (Once; по решению 21.11 разовой платы за модули нет — 0), профили, где он входит бесплатно.</summary>
/// 2026-11-21 (ответ Умара 21.11): TrialDays — trial_days (пробный период модуля, 7 дней; 0 — пробы нет).
public sealed record PublicModule(string Id, string Name, string Group, decimal Monthly, decimal Once, IReadOnlyList<string> DefaultIn, int TrialDays = 0);

/// <summary>2026-11-21 (решение «цены только серверные»; ответ Умара В.23): цены модулей Маркетплейса — GET public/modules/ ({currency, modules:[{id, name{ru,en}, group, monthly, once, default_in}], profiles}).
/// Кэш 10 минут в памяти; цен не нашли (нет связи, старый сервер) — <see cref="PriceOf"/> даёт null, и касса НЕ придумывает цену. <see cref="Changed"/> — цены пришли или изменились (витрина перечитывается).</summary>
public static class PublicModules
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
    private static readonly object Sync = new();
    private static IReadOnlyList<PublicModule>? _modules;
    private static string _currency = "KGS";
    private static DateTime _at = DateTime.MinValue;
    private static DateTime _failedAt = DateTime.MinValue;
    private static int _running;

    public static event Action? Changed;

    /// <summary>Модули с ценами из последнего ответа; null — ещё не получены.</summary>
    public static IReadOnlyList<PublicModule>? Cached
    {
        get { lock (Sync) return _modules; }
    }

    public static string Currency
    {
        get { lock (Sync) return _currency; }
    }

    /// <summary>Цена модуля в месяц; null — сервер цену не отдал (или ещё не загружено).</summary>
    public static decimal? PriceOf(string id)
    {
        var list = Cached;
        return list?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Monthly;
    }

    /// <summary>Дней пробного периода модуля (public/modules trial_days); 0 — пробы нет или цены ещё не получены.</summary>
    public static int TrialDaysOf(string id) =>
        Cached?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.TrialDays ?? 0;

    /// <summary>Модуль входит в профиль бесплатно (default_in): профиль сравнивается по единому имени (hardware → stroymag, bazaar_retail → bazaar).</summary>
    public static bool IncludedInProfile(string id, string? profile)
    {
        var m = Cached?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (m is null)
            return false;
        var p = CompanyModules.CanonProfile(profile);
        return p.Length > 0 && m.DefaultIn.Any(d => string.Equals(CompanyModules.CanonProfile(d), p, StringComparison.Ordinal));
    }

    /// <summary>Загрузить цены в фоне (не чаще раза в 10 минут; после сбоя — раз в минуту).</summary>
    public static void RefreshInBackground(NurMarketApiClient? api = null)
    {
        api ??= (ServerAddons.ApiProvider ?? ServerLoyalty.ApiProvider)?.Invoke();
        if (api is null || string.IsNullOrEmpty(api.AccessToken))
            return;
        lock (Sync)
        {
            var ttl = _modules is null ? FailTtl : Ttl;
            var last = _at > _failedAt ? _at : _failedAt;
            if (DateTime.UtcNow - last < ttl)
                return;
        }
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try { await LoadAsync(api).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Цены модулей: {ex.Message}", "WARNING"); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    /// <summary>GET public/modules/. false — не удалось (кэш прежний).</summary>
    public static async Task<bool> LoadAsync(NurMarketApiClient api, CancellationToken ct = default)
    {
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, "api/public/modules/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var parsed = Parse(data, out var currency);
            if (parsed is null)
            {
                lock (Sync) _failedAt = DateTime.UtcNow;
                return false;
            }
            bool changed;
            lock (Sync)
            {
                changed = _modules is null || !_modules.SequenceEqual(parsed, ModuleComparer.Instance) || _currency != currency;
                _modules = parsed;
                _currency = currency;
                _at = DateTime.UtcNow;
            }
            if (changed)
                try { Changed?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Цены модулей: обработчик: {ex.Message}", "WARNING"); }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            lock (Sync) _failedAt = DateTime.UtcNow;
            PosLogger.Log($"Цены модулей не загружены: {ex.Message}", "WARNING");
            return false;
        }
    }

    private sealed class ModuleComparer : IEqualityComparer<PublicModule>
    {
        public static readonly ModuleComparer Instance = new();
        public bool Equals(PublicModule? a, PublicModule? b) => a is not null && b is not null && a.Id == b.Id && a.Monthly == b.Monthly && a.Once == b.Once && a.Name == b.Name && a.TrialDays == b.TrialDays;
        public int GetHashCode(PublicModule m) => HashCode.Combine(m.Id, m.Monthly, m.Once);
    }

    /// <summary>Разбор ответа (internal — для тестов): null — это не список модулей.</summary>
    internal static IReadOnlyList<PublicModule>? Parse(JsonElement root, out string currency)
    {
        currency = "KGS";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("modules", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        if (root.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cur.GetString()))
            currency = cur.GetString()!.Trim();
        var list = new List<PublicModule>();
        foreach (var el in arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var id = Str(el, "id");
            if (id is null)
                continue;
            var def = new List<string>();
            if (el.TryGetProperty("default_in", out var d) && d.ValueKind == JsonValueKind.Array)
                def.AddRange(d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()));
            list.Add(new PublicModule(id, TariffApi.LocalName(el, "name") ?? id, Str(el, "group") ?? "", Dec(el, "monthly") ?? 0m, Dec(el, "once") ?? 0m, def,
                Dec(el, "trial_days") is { } td && td > 0 ? (int)td : 0));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static decimal? Dec(JsonElement e, string name) =>
        Str(e, name) is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Только для тестов.</summary>
    internal static void SetForTests(IReadOnlyList<PublicModule>? modules)
    {
        lock (Sync)
        {
            _modules = modules;
            _at = modules is null ? DateTime.MinValue : DateTime.UtcNow;
            _failedAt = DateTime.MinValue;
        }
    }
}
