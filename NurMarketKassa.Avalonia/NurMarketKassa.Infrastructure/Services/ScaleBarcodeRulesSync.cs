using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>2026-11-11, решение владельца: «Вкладка “Штрих-код: вес / сумма” (правила префиксов): синхронизацию через облако, при том БЫСТРУЮ!»
/// Правила весовых штрихкодов жили в user-settings.json конкретной программы (касса и программа владельца — разные папки), поэтому правило,
/// заданное у владельца, на кассе не работало. Теперь все правила разбора весовой этикетки собраны в ОДИН ключ общих настроек компании
/// (<see cref="UserPreferences.ScaleBarcodeRulesShared"/> → SettingsCloudSync.CompanyKeys, документ app-settings/nurmarket-kassa): одним
/// куском, чтобы «сумма» и «вес» одного префикса не разъезжались по разным ключам.
///
/// Формат (строка JSON, версия <see cref="FormatVersion"/>):
/// <code>{"v":1,"amount":["21"],"weight":["22"],"unit":"tiyin","max_amount":20000,"shared_amount":["21"],
///  "rongta":{"type":2,"dept":20},"tm":{"prefix":20,"format":null},
///  "pc200":{"enabled":true,"prefix":27,"code_len":5,"format":"code-weight","checksum":false,"price_decimals":2,"piece_enabled":true,"piece_prefix":20,"piece_format":"code-amount"}}</code>
/// • amount/weight — префиксы 20–29 «сумма» / «вес» (ScaleAmountPrefixes / ScaleWeightPrefixes; обязательны);
/// • unit — единица суммы этикетки, выбранная вручную (ScaleBarcodeAmountUnitLocal; null — как на сайте);
/// • max_amount — предел суммы на этикетке; shared_amount — префиксы «сумма» весов с прямой отправкой (scale-amount-prefixes.json);
/// • rongta — тип штрихкода и отдел весов Rongta; tm — префикс и формат TM-30F; pc200 — раскладка этикетки «Штрих РС-200»
///   (без папки обмена, логина и пароля — это оборудование конкретного ПК, в облако не уходит).
/// Безопасность: значение проверяется целиком — битое, пустое, другой версии или с недопустимым полем НЕ применяется (касса работает по своим
/// правилам, в журнал — одна строка). Конфликт — как у остальных общих ключей: изменённое на этой программе и ещё не отправленное не
/// затирается, иначе действует последнее записанное в облако.
/// 2026-11-12 (аудит Fable, Л-2/Л-3/Л-20): unit, max_amount, rongta, tm и pc200 в значение попадают, только если заданы (не умолчания) и на кассе применяются только заданные;
/// касса СЛИВАЕТ префиксы с облачными (не заменяет), перед отправкой читает облако и сливает (<see cref="MergeForPush"/>); пустое облако касса с собственными
/// правилами заполняет один раз (программа владельца — источник правды: заменяет и заменяется).</summary>
public static class ScaleBarcodeRulesSync
{
    public const int FormatVersion = 1;

    /// <summary>Имя ключа общих настроек компании (= имя свойства UserPreferences).</summary>
    public const string CloudKey = nameof(UserPreferences.ScaleBarcodeRulesShared);

    /// <summary>Префиксы «сумма» весов с прямой отправкой, пришедшие из облака (живут в памяти, приходят при каждом входе).</summary>
    private static HashSet<string> _cloudSharedAmount = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static string? _lastRejected;

    /// <summary>Префиксы «сумма» из облака (для ScaleLabelCodeRegistry.ApplySharedAmountPrefixes).</summary>
    public static IReadOnlyCollection<string> CloudSharedAmountPrefixes
    {
        get { lock (Gate) return _cloudSharedAmount.ToArray(); }
    }

    /// <summary>Сборка значения из настроек. Детерминированно (одни и те же правила — одна и та же строка), иначе облако считало бы его изменённым.</summary>
    public static string Build(UserPreferences prefs)
    {
        var amount = UserPreferences.ParseAmountPrefixes(prefs.ScaleAmountPrefixes);
        var weight = UserPreferences.ParseAmountPrefixes(prefs.ScaleWeightPrefixes);
        weight.ExceptWith(amount); // на всякий случай: один префикс — одно правило (сумма важнее, как в парсере)
        var pc = ShtrikhPcSettings.Parse(prefs.ShtrikhPcConfig);
        var shared = ReferenceEquals(prefs, UserPreferences.Instance) ? ScaleLabelCodeRegistry.SharedAmountPrefixesForCloud() : new HashSet<string>(StringComparer.Ordinal);
        // 2026-11-12 (Л-2/Л-3, аудит Fable): единица суммы, предел и оборудование (Rongta, TM, РС-200) пишутся, только когда они ЗАДАНЫ (отличаются от умолчания программы).
        // Иначе первое же изменение префикса у владельца уносило в облако его умолчания (unit = null, РС-200 выключена…), и на кассе стиралась её единица «тыйын» и её оборудование.
        var root = new JsonObject
        {
            ["v"] = FormatVersion,
            ["amount"] = Arr(amount),
            ["weight"] = Arr(weight),
        };
        if (prefs.ScaleBarcodeAmountUnitLocal is { } u)
            root["unit"] = JsonValue.Create(u);
        if (prefs.ScaleMaxLabelAmountSom > 0 && prefs.ScaleMaxLabelAmountSom != DefaultMaxAmount)
            root["max_amount"] = prefs.ScaleMaxLabelAmountSom;
        root["shared_amount"] = Arr(shared);
        if (prefs.RongtaBarcodeType != DefaultRongtaType || prefs.RongtaLanBarcodePrefix != DefaultRongtaDept)
            root["rongta"] = new JsonObject { ["type"] = prefs.RongtaBarcodeType, ["dept"] = prefs.RongtaLanBarcodePrefix };
        if (prefs.TmScaleBarcodePrefix != DefaultTmPrefix || prefs.TmScaleDahuaBarcode is not null)
            root["tm"] = new JsonObject { ["prefix"] = prefs.TmScaleBarcodePrefix, ["format"] = prefs.TmScaleDahuaBarcode is { } f ? JsonValue.Create(f) : null };
        var pcNode = PcNode(pc);
        if (!JsonNode.DeepEquals(pcNode, PcNode(new ShtrikhPcSettings())))
            root["pc200"] = pcNode;
        return root.ToJsonString();
    }

    // 2026-11-12: умолчания программы (UserPreferences: предел 20 000 сом, Rongta тип 2 / отдел 20, TM префикс 20) — то, что в облако не пишется.
    // 2026-11-13: умолчание типа Rongta — 07 (рекомендуемый, проверен вживую).
    private const double DefaultMaxAmount = 20000;
    private const int DefaultRongtaType = 7, DefaultRongtaDept = 20, DefaultTmPrefix = 20;

    private static JsonObject PcNode(ShtrikhPcSettings pc) => new()
    {
        ["enabled"] = pc.BarcodeEnabled, ["prefix"] = pc.BarcodePrefix, ["code_len"] = pc.CodeLength, ["format"] = pc.BarcodeFormat,
        ["checksum"] = pc.CodeChecksum, ["price_decimals"] = pc.PriceDecimals, ["piece_enabled"] = pc.PieceBarcodeEnabled,
        ["piece_prefix"] = pc.PiecePrefix, ["piece_format"] = pc.PieceFormat,
    };

    /// <summary>2026-11-12 (Л-3): программа владельца — источник правды (её правки заменяют облако и облако заменяет её правила); касса — терпимая: свои префиксы,
    /// которых нет в облаке, она оставляет (ответ кассира «сумма или вес»), а при отправке сливается с облаком. Поле — для тестов.</summary>
    internal static Func<bool> IsAuthoritative = () => AppMode.IsOwner;

    /// <summary>2026-11-12 (Л-3): у программы есть собственные правила весовой этикетки (не умолчания) — только тогда пустое облако заполняется один раз с кассы.</summary>
    public static bool HasLocalRules(UserPreferences prefs)
    {
        if (JsonNode.Parse(Build(prefs)) is not JsonObject o)
            return false;
        return (o["amount"] as JsonArray)?.Count > 0 || (o["weight"] as JsonArray)?.Count > 0
            || o.ContainsKey("unit") || o.ContainsKey("max_amount") || o.ContainsKey("rongta") || o.ContainsKey("tm") || o.ContainsKey("pc200");
    }

    /// <summary>2026-11-12 (Л-3): слияние «сумма»/«вес» двух правил. Один префикс — одно правило: при споре побеждает сторона <paramref name="preferOther"/> (облако при применении,
    /// свои при отправке); префиксы, о которых другая сторона молчит, сохраняются.</summary>
    private static (HashSet<string> Amount, HashSet<string> Weight) MergeSets(
        HashSet<string> localAmount, HashSet<string> localWeight, HashSet<string> otherAmount, HashSet<string> otherWeight, bool preferOther)
    {
        var (winA, winW, loseA, loseW) = preferOther ? (otherAmount, otherWeight, localAmount, localWeight) : (localAmount, localWeight, otherAmount, otherWeight);
        var amount = new HashSet<string>(winA, StringComparer.Ordinal);
        amount.UnionWith(loseA.Where(p => !winW.Contains(p)));
        var weight = new HashSet<string>(winW, StringComparer.Ordinal);
        weight.UnionWith(loseW.Where(p => !winA.Contains(p)));
        weight.ExceptWith(amount); // «сумма» важнее, как в парсере
        return (amount, weight);
    }

    private static readonly string[] BlobOrder = ["v", "amount", "weight", "unit", "max_amount", "shared_amount", "rongta", "tm", "pc200"];

    /// <summary>2026-11-12 (Л-20, аудит Fable): значение для отправки в облако. Облако целиком принадлежало «последнему записавшему»: правка владельца и ответ кассира «сумма или вес»
    /// почти одновременно — одна пропадала. Касса перед отправкой читает облако и сливает: префиксы — объединение (свой выбор важнее при споре об одном префиксе),
    /// единица, предел и оборудование, которых у неё нет, берутся из облака. Программа владельца отправляет своё как есть. Облако пустое/битое — своё как есть.
    /// Порядок полей и сортировка — как в <see cref="Build"/> (иначе отправленное отличалось бы от своего значения текстом).</summary>
    public static string MergeForPush(string localJson, string? cloudJson)
    {
        if (IsAuthoritative())
            return localJson;
        var local = TryParse(localJson, out _);
        var cloud = TryParse(cloudJson, out _);
        if (local is null || cloud is null || JsonNode.Parse(localJson) is not JsonObject lo || JsonNode.Parse(cloudJson!) is not JsonObject co)
            return localJson;
        var (amount, weight) = MergeSets(local.Amount, local.Weight, cloud.Amount, cloud.Weight, preferOther: false);
        var result = new JsonObject();
        foreach (var key in BlobOrder)
        {
            if (key == "amount")
                result[key] = Arr(amount);
            else if (key == "weight")
                result[key] = Arr(weight);
            else if (lo[key] is { } mine)
                result[key] = mine.DeepClone();
            else if (key is not ("v" or "shared_amount") && co[key] is { } theirs)
                result[key] = theirs.DeepClone();
        }
        return result.ToJsonString();
    }

    private static JsonArray Arr(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var p in items.OrderBy(p => p, StringComparer.Ordinal))
            arr.Add(p);
        return arr;
    }

    /// <summary>Разобранное и проверенное значение из облака.</summary>
    public sealed record Rules(
        HashSet<string> Amount, HashSet<string> Weight, string? Unit, double? MaxAmount, HashSet<string>? SharedAmount,
        int? RongtaType, int? RongtaDept, int? TmPrefix, bool TmFormatSet, string? TmFormat, Pc200? Pc);

    public sealed record Pc200(bool Enabled, int Prefix, int CodeLength, string Format, bool Checksum, int PriceDecimals, bool PieceEnabled, int PiecePrefix, string PieceFormat);

    /// <summary>Разбор с проверкой. null и причина — значение не применять (пустое, битое, другой версии, недопустимое поле).</summary>
    public static Rules? TryParse(string? json, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "пусто";
            return null;
        }
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o)
            {
                reason = "не объект";
                return null;
            }
            if (!TryInt(o["v"], out var v) || v != FormatVersion)
            {
                reason = $"версия формата {o["v"]?.ToJsonString() ?? "нет"} (поддерживается {FormatVersion})";
                return null;
            }
            var amount = Prefixes(o["amount"], required: true, ref reason);
            var weight = Prefixes(o["weight"], required: true, ref reason);
            if (amount is null || weight is null)
                return null;
            if (amount.Overlaps(weight))
            {
                reason = "один префикс и «сумма», и «вес»";
                return null;
            }
            string? unit = null;
            if (o["unit"] is { } un)
            {
                unit = un is JsonValue uv && uv.TryGetValue<string>(out var us) ? us.Trim().ToLowerInvariant() : "?";
                if (unit is not ("tiyin" or "som"))
                {
                    reason = "единица суммы";
                    return null;
                }
            }
            double? max = null;
            if (o["max_amount"] is { } mn)
            {
                if (mn is not JsonValue mv || !mv.TryGetValue<double>(out var m) || double.IsNaN(m) || m < 1 || m > 100_000_000)
                {
                    reason = "предел суммы";
                    return null;
                }
                max = m;
            }
            HashSet<string>? shared = null;
            if (o.ContainsKey("shared_amount"))
            {
                shared = Prefixes(o["shared_amount"], required: false, ref reason);
                if (shared is null)
                    return null;
            }
            int? rType = null, rDept = null;
            if (o["rongta"] is JsonObject r)
            {
                if (!Range(r["type"], 0, 99, out rType) || !Range(r["dept"], 0, 99, out rDept))
                {
                    reason = "настройки Rongta";
                    return null;
                }
            }
            int? tmPrefix = null;
            var tmFormatSet = false;
            string? tmFormat = null;
            if (o["tm"] is JsonObject tm)
            {
                if (!Range(tm["prefix"], 0, 99, out tmPrefix))
                {
                    reason = "префикс TM-30F";
                    return null;
                }
                if (tm.ContainsKey("format"))
                {
                    tmFormatSet = true;
                    if (tm["format"] is { } fn)
                    {
                        tmFormat = fn is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : null;
                        if (tmFormat is null || !DahuaTmBarcodeFormat.Variants.Contains(tmFormat))
                        {
                            reason = "формат TM-30F";
                            return null;
                        }
                    }
                }
            }
            Pc200? pc = null;
            if (o["pc200"] is JsonObject p)
            {
                if (!Bool(p["enabled"], out var en) || !Range(p["prefix"], 20, 29, out var pre) || !Range(p["code_len"], 1, 6, out var cl)
                    || !Str(p["format"], ShtrikhPcSettings.Formats, out var fmt) || !Bool(p["checksum"], out var cs) || !Range(p["price_decimals"], 0, 3, out var pd)
                    || !Bool(p["piece_enabled"], out var pe) || !Range(p["piece_prefix"], 20, 29, out var pp) || !Str(p["piece_format"], ShtrikhPcSettings.PieceFormats, out var pf))
                {
                    reason = "раскладка РС-200";
                    return null;
                }
                pc = new Pc200(en, pre!.Value, cl!.Value, fmt!, cs && cl.Value >= 2, pd!.Value, pe, pp!.Value, pf!);
            }
            return new Rules(amount, weight, unit, max, shared, rType, rDept, tmPrefix, tmFormatSet, tmFormat, pc);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            reason = "битый JSON";
            return null;
        }
    }

    /// <summary>Применить значение из облака к настройкам (вызывает сеттер свойства при синхронизации). Битое — не применяется.
    /// Сразу обновляет правила разбора этикеток (без перезапуска). true — применено.</summary>
    public static bool TryApply(UserPreferences prefs, string? json)
    {
        var rules = TryParse(json, out var reason);
        if (rules is null)
        {
            // Пустое значение — не ошибка (сброс при смене компании, ключа нет); о битом — одна строка в журнал на каждое новое значение.
            if (!string.IsNullOrWhiteSpace(json) && json != _lastRejected)
            {
                _lastRejected = json;
                PosLogger.Log($"Весы: правила штрихкода из облака не применены ({reason}) — касса работает по своим правилам.", "SCALES");
            }
            return false;
        }
        var amount = rules.Amount;
        var weight = rules.Weight;
        if (!IsAuthoritative())
        {
            // 2026-11-12 (Л-3, аудит Fable): касса не заменяет свои префиксы облачными, а сливает: префиксы, которых владелец не называл (в т. ч. ответ кассира «сумма или вес»),
            // остаются; при споре об одном префиксе правило облака (владельца) главнее.
            (amount, weight) = MergeSets(UserPreferences.ParseAmountPrefixes(prefs.ScaleAmountPrefixes), UserPreferences.ParseAmountPrefixes(prefs.ScaleWeightPrefixes),
                rules.Amount, rules.Weight, preferOther: true);
        }
        prefs.ScaleAmountPrefixes = string.Join(",", amount.OrderBy(x => x, StringComparer.Ordinal));
        prefs.ScaleWeightPrefixes = string.Join(",", weight.OrderBy(x => x, StringComparer.Ordinal));
        // 2026-11-12 (Л-2, аудит Fable): единица суммы — только когда владелец задал её явно. Раньше облачное «нет единицы» стирало на кассе выбор «тыйын» — сумма с этикетки в 100 раз больше.
        if (rules.Unit is not null)
            prefs.ScaleBarcodeAmountUnitLocal = rules.Unit;
        if (rules.MaxAmount is { } max)
            prefs.ScaleMaxLabelAmountSom = max;
        if (rules.RongtaType is { } rt)
            prefs.RongtaBarcodeType = rt;
        if (rules.RongtaDept is { } rd)
            prefs.RongtaLanBarcodePrefix = rd;
        if (rules.TmPrefix is { } tp)
            prefs.TmScaleBarcodePrefix = tp;
        if (rules.TmFormatSet)
            prefs.TmScaleDahuaBarcode = rules.TmFormat;
        if (rules.Pc is { } pc)
        {
            // Папка обмена, логин, пароль и остальное — этого ПК: меняются только поля раскладки штрихкода.
            var s = ShtrikhPcSettings.Parse(prefs.ShtrikhPcConfig);
            s.BarcodeEnabled = pc.Enabled;
            s.BarcodePrefix = pc.Prefix;
            s.CodeLength = pc.CodeLength;
            s.BarcodeFormat = pc.Format;
            s.CodeChecksum = pc.Checksum;
            s.PriceDecimals = pc.PriceDecimals;
            s.PieceBarcodeEnabled = pc.PieceEnabled;
            s.PiecePrefix = pc.PiecePrefix;
            s.PieceFormat = pc.PieceFormat;
            prefs.ShtrikhPcConfig = s.ToJson();
        }
        if (ReferenceEquals(prefs, UserPreferences.Instance))
        {
            if (rules.SharedAmount is { } shared)
                lock (Gate)
                    _cloudSharedAmount = new HashSet<string>(shared, StringComparer.Ordinal);
            ApplyToParser();
            PosLogger.Log($"Весы: правила штрихкода из облака применены — сумма: {(amount.Count > 0 ? string.Join(",", amount.OrderBy(x => x)) : "—")}, " +
                          $"вес: {(weight.Count > 0 ? string.Join(",", weight.OrderBy(x => x)) : "—")}{(rules.Pc?.Enabled == true ? $", РС-200 префикс {rules.Pc.Prefix}" : "")}.", "SCALES");
        }
        return true;
    }

    /// <summary>Правила разбора этикеток — заново из настроек (режим компании, префиксы, раскладка РС-200, префиксы весов с прямой отправкой).</summary>
    public static void ApplyToParser()
    {
        try
        {
            CompanyInfoService.SyncWeightBarcodeParserSettings();
            ScaleLabelCodeRegistry.ApplySharedAmountPrefixes(force: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы: правила разбора этикеток не обновлены ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Владелец снял префикс «сумма» весов с прямой отправкой (ScaleLabelCodeRegistry.UnpublishAmountPrefix) — убрать и из пришедших из облака.</summary>
    public static void ForgetCloudSharedAmount(string prefix)
    {
        lock (Gate)
            _cloudSharedAmount.Remove(prefix);
    }

    /// <summary>Только для тестов.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            _cloudSharedAmount = new HashSet<string>(StringComparer.Ordinal);
        _lastRejected = null;
    }

    private static HashSet<string>? Prefixes(JsonNode? node, bool required, ref string reason)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (node is null)
        {
            if (required)
            {
                reason = "нет списка префиксов";
                return null;
            }
            return set;
        }
        if (node is not JsonArray arr || arr.Count > 10)
        {
            reason = "список префиксов";
            return null;
        }
        foreach (var item in arr)
        {
            var s = item is JsonValue v && v.TryGetValue<string>(out var str) ? str.Trim() : "";
            if (s.Length != 2 || s[0] != '2' || !char.IsDigit(s[1]))
            {
                reason = "префикс не 20–29";
                return null;
            }
            set.Add(s);
        }
        return set;
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        if (v.TryGetValue<int>(out value))
            return true;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        return false;
    }

    private static bool Range(JsonNode? node, int min, int max, out int? value)
    {
        value = null;
        if (!TryInt(node, out var v) || v < min || v > max)
            return false;
        value = v;
        return true;
    }

    private static bool Bool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    private static bool Str(JsonNode? node, IEnumerable<string> allowed, out string? value)
    {
        value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return value is not null && allowed.Contains(value);
    }
}
