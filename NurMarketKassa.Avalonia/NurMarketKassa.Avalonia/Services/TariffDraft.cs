using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Что можно добавить в тариф «Своё»: пакет (users/company/tariff, коды ai / clients / …) или функция-модуль (users/company/modules, коды scales_labels / haggle / …).</summary>
public enum TariffTargetKind { Pack, Module }

public sealed record TariffTarget(TariffTargetKind Kind, string Code);

// 2026-11-26 (требование владельца: «тариф «Своё» должен собираться из Маркетплейса»): ЕДИНОЕ состояние набора. Карточки Маркетплейса (плитки, страницы «подробно»), полоса
// «Ваш тариф» над витриной и диалог «Тариф «Своё»» читают и пишут ОДНО место — этот класс; выбрали на плитке — галочка в диалоге уже стоит, и наоборот.
// Хранятся только ОТЛИЧИЯ от текущего тарифа: что добавить (пакеты и модули) и что снять (пакеты, входящие в тариф, и включённые модули). «База» (что уже входит в тариф или подключено)
// у каждого читателя считается одинаково — PackBaseline / ModuleBaseline. Цены тут нет и быть не должно: цену набора считает ТОЛЬКО сервер (users/company/tariff/quote/ с packs[], modules[], remove[]).
// Заявка уходит одним POST users/company/tariff/custom/ (packs[], modules[], remove[]); менеджер обрабатывает её, платить в программе ничего не нужно. После успешной заявки набор очищается.
public static class TariffDraft
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> AddPacks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RemovePacks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AddModules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RemoveModules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> PublishedPackBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Набор изменился (из Маркетплейса, из диалога, очистка). Событие приходит в потоке, который менял набор (в программе — поток интерфейса).</summary>
    public static event Action? Changed;

    /// <summary>Только для проверок: подмена «это владелец компании» (по умолчанию — роль «owner» из профиля и программа владельца).</summary>
    public static Func<bool>? CanBuildOverride { get; set; }

    /// <summary>Собирать тариф «Своё» может только владелец, и только в программе владельца (как кнопка в «Аккаунт → Тариф»; админу сервер отдаёт 403).</summary>
    public static bool CanBuild
    {
        get
        {
            if (CanBuildOverride is { } over)
                return over();
            try
            {
                if (!AppMode.IsOwner)
                    return false;
                var user = App.GetRequiredService<NurMarketApiClient>().UserPayload;
                return user.ValueKind == System.Text.Json.JsonValueKind.Object
                       && user.TryGetProperty("role", out var role) && role.ValueKind == System.Text.Json.JsonValueKind.String
                       && string.Equals(role.GetString(), "owner", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    // 2026-11-26 (владелец по скриншоту плиток): кружки «+» на плитках и кнопка «Добавить в тариф «Своё»» на страницах — ТОЛЬКО в режиме сборки тарифа: его включает кнопка
    // «Собрать тариф «Своё»» в полосе над витриной, выключают «Отмена» и успешная заявка. В обычное время Маркетплейс выглядит и работает как раньше.
    private static bool _buildMode;

    /// <summary>Режим сборки тарифа «Своё» включён (само по себе не значит, что собирать можно: см. <see cref="Active"/>).</summary>
    public static bool BuildMode
    {
        get { lock (Sync) return _buildMode; }
    }

    /// <summary>Режим сборки виден в Маркетплейсе: включён и собирать может владелец. От этого зависят «+» на плитках и кнопка на странице.</summary>
    public static bool Active => BuildMode && CanBuild;

    /// <summary>Войти в режим сборки (кнопка «Собрать тариф «Своё»»).</summary>
    public static void EnterBuildMode() => SetBuildMode(true);

    /// <summary>Выйти из режима сборки («Отмена»): выбор сбрасывается, Маркетплейс возвращается к прежнему виду.</summary>
    public static void CancelBuildMode()
    {
        Clear();
        SetBuildMode(false);
    }

    /// <summary>Заявка отправлена: набор очищен, режим сборки выключен.</summary>
    public static void Finish() => CancelBuildMode();

    private static void SetBuildMode(bool on)
    {
        lock (Sync)
        {
            if (_buildMode == on)
                return;
            _buildMode = on;
        }
        Raise();
    }

    // ───────────────────────────── что выбрано ─────────────────────────────

    public static IReadOnlyList<string> AddedPacks => Snapshot(AddPacks);

    public static IReadOnlyList<string> RemovedPacks => Snapshot(RemovePacks);

    public static IReadOnlyList<string> AddedModules => Snapshot(AddModules);

    public static IReadOnlyList<string> RemovedModules => Snapshot(RemoveModules);

    public static int ChangeCount
    {
        get { lock (Sync) return AddPacks.Count + RemovePacks.Count + AddModules.Count + RemoveModules.Count; }
    }

    public static bool HasChanges => ChangeCount > 0;

    private static List<string> Snapshot(HashSet<string> set)
    {
        lock (Sync)
            return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Пакет «в тарифе» с учётом выбора: уже входит / подключён и не снят, либо добавлен.</summary>
    public static bool IsPackSelected(string code) => IsPackSelected(code, PackBaseline(code));

    public static bool IsPackSelected(string code, bool baseline)
    {
        lock (Sync)
            return (baseline && !RemovePacks.Contains(code)) || AddPacks.Contains(code);
    }

    /// <summary>Модуль «в тарифе» с учётом выбора: включён в компании и не снят, либо добавлен.</summary>
    public static bool IsModuleSelected(string id) => IsModuleSelected(id, ModuleBaseline(id));

    public static bool IsModuleSelected(string id, bool baseline)
    {
        lock (Sync)
            return (baseline && !RemoveModules.Contains(id)) || AddModules.Contains(id);
    }

    public static bool IsSelected(TariffTarget t) => t.Kind == TariffTargetKind.Pack ? IsPackSelected(t.Code) : IsModuleSelected(t.Code);

    // ───────────────────────────── изменить выбор ─────────────────────────────

    /// <summary>Хочу, чтобы пакет был (<paramref name="wanted"/> = true) или не был в тарифе; <paramref name="baseline"/> — уже ли он в тарифе без выбора. Совпало с базой — отличия нет.</summary>
    public static void SetPack(string code, bool wanted, bool baseline) => Set(AddPacks, RemovePacks, code, wanted, baseline);

    public static void SetModule(string id, bool wanted, bool baseline) => Set(AddModules, RemoveModules, id, wanted, baseline);

    /// <summary>Переключить карточку: был в тарифе — убрать, не был — добавить.</summary>
    public static void Toggle(TariffTarget t)
    {
        if (t.Kind == TariffTargetKind.Pack)
            SetPack(t.Code, !IsPackSelected(t.Code), PackBaseline(t.Code));
        else
            SetModule(t.Code, !IsModuleSelected(t.Code), ModuleBaseline(t.Code));
    }

    private static void Set(HashSet<string> add, HashSet<string> remove, string code, bool wanted, bool baseline)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;
        bool changed;
        lock (Sync)
        {
            var inAdd = add.Contains(code);
            var inRemove = remove.Contains(code);
            add.Remove(code);
            remove.Remove(code);
            if (wanted != baseline)
            {
                if (wanted)
                    add.Add(code);
                else
                    remove.Add(code);
            }
            changed = inAdd != add.Contains(code) || inRemove != remove.Contains(code);
        }
        if (changed)
            Raise();
    }

    /// <summary>Сбросить весь выбор (кнопка «Сбросить» в полосе, успешная заявка).</summary>
    public static void Clear()
    {
        bool had;
        lock (Sync)
        {
            had = AddPacks.Count + RemovePacks.Count + AddModules.Count + RemoveModules.Count > 0;
            AddPacks.Clear();
            RemovePacks.Clear();
            AddModules.Clear();
            RemoveModules.Clear();
        }
        if (had)
            Raise();
    }

    /// <summary>Только для проверок: набор, подмены и сообщение «нет выбора» в исходное состояние (без события).</summary>
    public static void ResetForTests()
    {
        lock (Sync)
        {
            AddPacks.Clear();
            RemovePacks.Clear();
            AddModules.Clear();
            RemoveModules.Clear();
            PublishedPackBaseline.Clear();
            _buildMode = false;
        }
        CanBuildOverride = null;
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Тариф «Своё»: обработчик изменения набора: {ex.Message}", "WARNING"); }
    }

    // ───────────────────────────── база: что уже в тарифе ─────────────────────────────

    /// <summary>Диалог «Своё» после расчёта у сервера знает точно, какие пакеты уже входят в тариф или подключены (included_in каталога, already_active расчёта) — и сообщает это сюда,
    /// чтобы плитки Маркетплейса показывали то же самое. Без сообщения база считается по каталогу и признакам кассы (<see cref="PackBaseline"/>).</summary>
    public static void PublishPackBaselines(IEnumerable<(string Code, bool InTariff)> baselines)
    {
        lock (Sync)
            foreach (var (code, inTariff) in baselines)
                PublishedPackBaseline[code] = inTariff;
    }

    /// <summary>Пакет уже входит в текущий тариф или подключён (без учёта выбора).</summary>
    public static bool PackBaseline(string code)
    {
        lock (Sync)
            if (PublishedPackBaseline.TryGetValue(code, out var published))
                return published;
        var catalog = TariffApi.CachedCatalog;
        var plan = TariffCustomRules.ResolvePlanCode(catalog, TariffGate.CurrentPlanName);
        if (catalog?.Packs.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)) is { } pack && TariffCustomRules.IsIncludedInPlan(pack, plan))
            return true;
        return code.ToLowerInvariant() switch
        {
            "ai" => TariffGate.HasPack(TariffGate.Packs.Ai),
            "clients" => TariffGate.HasPack(TariffGate.Packs.Clients),
            "sales_analytics" => TariffGate.HasPack(TariffGate.Packs.SalesAnalytics),
            "restock" => TariffGate.HasPack(TariffGate.Packs.Restock),
            "salary" => TariffGate.HasPack(TariffGate.Packs.Salary),
            "debts" => TariffGate.HasPack(TariffGate.Packs.Debts),
            "service" => TariffGate.HasPack(TariffGate.Packs.Service),
            "scales" => TariffGate.CanUseScales,
            "telegram_bot" => TariffGate.CanUseTelegramBot,
            "shift_analytics" => TariffGate.CanUseShiftAnalytics,
            "analytics_export" => TariffGate.CanUseAnalyticsExport,
            _ => false,
        };
    }

    /// <summary>Модуль включён в компании (ответ GET users/company/modules/; включает профиль сферы, добавленные и пробные).</summary>
    public static bool ModuleBaseline(string id) => CompanyModules.Snapshot?.IsEnabled(id) == true;

    /// <summary>Модуль входит в профиль сферы бесплатно («входит в вашу сферу»).</summary>
    public static bool ModuleInSphere(string id) => CompanyModules.IncludedInProfile(id);

    /// <summary>Набор модулей для расчёта и заявки: включённые в компании + добавленные − снятые. null — реестра модулей нет и в наборе модулей ничего нет (поле в запрос не кладём — сервер берёт текущие сам).</summary>
    public static List<string>? EffectiveModules()
    {
        var snap = CompanyModules.Snapshot;
        lock (Sync)
        {
            if (snap is null && AddModules.Count == 0 && RemoveModules.Count == 0)
                return null;
            var set = new HashSet<string>(snap?.Enabled ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            set.UnionWith(AddModules);
            set.ExceptWith(RemoveModules);
            return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
    }

    // ───────────────────────────── карточка Маркетплейса → что в тариф ─────────────────────────────

    /// <summary>Что добавляется в тариф с этой карточки Маркетплейса; null — карточка не про тариф (тема, вид кассы, «Лояльность», языки…) или сервер такой пакет / модуль не знает.</summary>
    public static TariffTarget? TargetOf(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return null;
        TariffTarget? target = null;
        if (itemId.StartsWith("module:", StringComparison.Ordinal))
            target = new TariffTarget(TariffTargetKind.Module, itemId["module:".Length..]);
        else if (Array.IndexOf(ModuleIds.WithOwnCard, itemId) >= 0)
            target = new TariffTarget(TariffTargetKind.Module, itemId);
        else if (itemId.StartsWith("pack:", StringComparison.Ordinal))
            target = new TariffTarget(TariffTargetKind.Pack, TariffGate.ServerFeatureCode(itemId["pack:".Length..]));
        else
            target = itemId switch
            {
                "scales" => new TariffTarget(TariffTargetKind.Pack, "scales"),
                "telegram" => new TariffTarget(TariffTargetKind.Pack, "telegram_bot"),
                "shiftstats" => new TariffTarget(TariffTargetKind.Pack, "shift_analytics"),
                "export" => new TariffTarget(TariffTargetKind.Pack, "analytics_export"),
                _ => null,
            };
        return target is not null && Available(target) ? target : null;
    }

    /// <summary>Сервер знает этот пакет (каталог тарифов) или модуль (реестр модулей компании) — иначе в запрос его класть нельзя (400 pack_unknown).</summary>
    public static bool Available(TariffTarget t) => t.Kind == TariffTargetKind.Pack
        ? TariffApi.CachedCatalog?.Packs.Any(p => string.Equals(p.Code, t.Code, StringComparison.OrdinalIgnoreCase)) == true
        : CompanyModules.Snapshot?.Knows(t.Code) == true;

    /// <summary>Название пакета или модуля на языке интерфейса (с сервера); нет — код.</summary>
    public static string NameOf(TariffTarget t)
    {
        if (t.Kind == TariffTargetKind.Pack)
            return TariffApi.CachedCatalog?.Packs.FirstOrDefault(p => string.Equals(p.Code, t.Code, StringComparison.OrdinalIgnoreCase))?.Name ?? t.Code;
        return PublicModules.Cached?.FirstOrDefault(m => string.Equals(m.Id, t.Code, StringComparison.OrdinalIgnoreCase))?.Name
               ?? CompanyModules.Snapshot?.Info(t.Code)?.Name ?? t.Code;
    }

    // ───────────────────────────── подписи ─────────────────────────────

    // Подпись не начинается с кавычки «: CleanLabel страницы «подробно» срезает всё, что не буква и не цифра в начале.
    public static string AddLabel => Tr.T("Добавить в тариф «Своё»", "Тарифке кошуу: «Өзүмдүкү»", "Add to the “Custom” plan", "Tarifeye ekle: «Özel»", "Tarifga qo'shish: «O'zimniki»");

    public static string RemoveLabel => Tr.T("Убрать из тарифа «Своё»", "Тарифтен алып салуу: «Өзүмдүкү»", "Remove from the “Custom” plan", "Tarifeden çıkar: «Özel»", "Tarifdan olib tashlash: «O'zimniki»");

    public static string ActionLabel(bool selected) => selected ? RemoveLabel : AddLabel;

    /// <summary>«+Ремонт, +Состояние, −ИИ-помощник»: что изменится в тарифе (до <paramref name="max"/> названий, дальше «…и ещё N»).</summary>
    public static string ChangesText(int max = 3)
    {
        var parts = new List<string>();
        foreach (var c in AddedPacks)
            parts.Add("+" + NameOf(new TariffTarget(TariffTargetKind.Pack, c)));
        foreach (var c in AddedModules)
            parts.Add("+" + NameOf(new TariffTarget(TariffTargetKind.Module, c)));
        foreach (var c in RemovedPacks)
            parts.Add("−" + NameOf(new TariffTarget(TariffTargetKind.Pack, c)));
        foreach (var c in RemovedModules)
            parts.Add("−" + NameOf(new TariffTarget(TariffTargetKind.Module, c)));
        if (parts.Count <= max)
            return string.Join(", ", parts);
        var rest = parts.Count - max;
        return string.Join(", ", parts.Take(max)) + Tr.T($" …и ещё {rest}", $" …жана дагы {rest}", $" …and {rest} more", $" …ve {rest} tane daha", $" …va yana {rest} ta");
    }
}
