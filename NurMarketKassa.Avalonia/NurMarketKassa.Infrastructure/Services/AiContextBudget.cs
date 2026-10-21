using System.Text;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21, «доведи Нур Советника до 10» (О1, идея 1). Часть данных магазина для ИИ: ключ, название, важность
/// (0 — всегда), полный текст и короткий вариант («показаны 10 из 330 — остальное спроси»), откуда цифра (раздел программы и
/// период — для подписи «откуда цифра» под ответом) и есть ли в части персональные данные (сотрудники, должники, кассиры чеков).</summary>
public sealed record AiSection(string Key, string Title, int Priority, string Full, string? Short = null,
    string? SourceSection = null, string? SourceLabel = null, bool Personal = false);

/// <summary>2026-10-21 (О4, идея 7): что разрешено текущему пользователю программы владельца — те же флаги, что разделы
/// (PosPermissions): склад и изменения товаров — «Склад» (can_view_products); имена и телефоны клиентов-должников — «Клиенты»
/// (can_view_clients); зарплата, табель, сотрудники и настройки бота — «Настройки» (can_view_settings: так открываются
/// разделы «Зарплата» и «Настройки»). Нет права — ИИ даже не получает эти данные.</summary>
public sealed record AiAccess(bool Products, bool Clients, bool Staff, bool Settings)
{
    public static readonly AiAccess Full = new(true, true, true, true);

    public bool IsFull => Products && Clients && Staff && Settings;

    /// <summary>Строка для инструкции ИИ: к чему нет доступа и как отвечать.</summary>
    public string DeniedPrompt()
    {
        var denied = new List<string>();
        if (!Staff)
            denied.Add("сотрудники, зарплата, табель и смены сотрудников");
        if (!Clients)
            denied.Add("клиенты и должники (имена, телефоны, список должников)");
        if (!Products)
            denied.Add("склад (остатки, закупочные цены) и любые изменения товаров");
        if (!Settings)
            denied.Add("настройки Телеграм-бота");
        return denied.Count == 0 ? ""
            : "НЕТ ДОСТУПА у этого пользователя: " + string.Join("; ", denied) + ". Этих данных в сводке нет. На вопрос или просьбу об этом "
              + "ответь одной фразой: «У вас нет доступа к …» и что доступ даёт владелец на сайте NurCRM (Сотрудники → доступы). Ничего не выдумывай.";
    }
}

/// <summary>2026-10-21 (О5, идея 8): сотрудники для ИИ — кодами [С1], [С2]… (как должники [Д1]). Имена кодами заменяет программа
/// перед отправкой, обратно — на этом компьютере (<see cref="Reveal"/>). Коды живут, пока работает программа.</summary>
public static class AiPseudonyms
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> StaffByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> StaffNames = new();

    /// <summary>Код сотрудника «[С3]» (пустое имя — «—»).</summary>
    public static string Staff(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n == "—" || n == "-")
            return "—";
        lock (Gate)
        {
            if (!StaffByName.TryGetValue(n, out var code))
            {
                code = StaffNames.Count + 1;
                StaffByName[n] = code;
                StaffNames[code] = n;
            }
            return $"[С{code}]";
        }
    }

    /// <summary>Известные имена сотрудников в тексте → коды (длинные имена первыми, только целым словом).</summary>
    public static string MaskStaff(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        List<KeyValuePair<string, int>> known;
        lock (Gate)
            known = StaffByName.OrderByDescending(k => k.Key.Length).ToList();
        foreach (var (name, code) in known)
        {
            if (name.Length < 2)
                continue;
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", $"[С{code}]", RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>Коды «[С1]» (и латинская «[C1]» — ИИ иногда путает буквы) → имена. Только со скобками: «С1» без скобок не трогаем.</summary>
    public static string RevealStaff(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        Dictionary<int, string> names;
        lock (Gate)
            names = new Dictionary<int, string>(StaffNames);
        return names.Count == 0 ? text
            : Regex.Replace(text, @"\[[СC](\d{1,3})\]", m => int.TryParse(m.Groups[1].Value, out var i) && names.TryGetValue(i, out var n) ? n : m.Value);
    }
}

/// <summary>2026-10-21 (О1): «сводка по важности» вместо обрезки всей строки до 16 000 знаков (с 1.17.58 склад, ABC, остатки и
/// сроки до ИИ не доходили). Части идут по важности, пока помещаются в бюджет; не влезла полная — короткая («показаны 10 из 330»);
/// не влезла и она — одна строка «раздел X не поместился — спроси НУЖНО: x». Молча ничего не обрезается.</summary>
public static class AiContextBudget
{
    /// <summary>Отчёт последней сборки «что ушло в ИИ» (разделы, вид, размер) — для журнала и проверки.</summary>
    public static string LastReport { get; private set; } = "";

    public static (string Text, string Report, IReadOnlyList<AiSection> Included) Compose(IEnumerable<AiSection> sections, int budget,
        bool withoutPersonal = false, ISet<string>? forceFull = null)
    {
        var sb = new StringBuilder();
        var report = new StringBuilder();
        var included = new List<AiSection>();
        var omitted = new List<AiSection>();
        var ordered = sections.Where(s => !string.IsNullOrWhiteSpace(s.Full))
            .Select((s, i) => (s, i))
            .OrderBy(x => forceFull?.Contains(x.s.Key) == true ? -1 : x.s.Priority).ThenBy(x => x.i)
            .Select(x => x.s).ToList();
        foreach (var s in ordered)
        {
            if (withoutPersonal && s.Personal)
            {
                report.Append($"{s.Key}: не отправлено (персональные данные); ");
                continue;
            }
            var full = s.Full.Trim();
            var left = budget - sb.Length;
            if (full.Length + 2 <= left || s.Priority == 0 || forceFull?.Contains(s.Key) == true)
            {
                sb.AppendLine(full).AppendLine();
                included.Add(s);
                report.Append($"{s.Key}: полностью {full.Length}; ");
                continue;
            }
            if (s.Short is { Length: > 0 } shortText && shortText.Trim().Length + 2 <= left)
            {
                sb.AppendLine(shortText.Trim()).AppendLine();
                included.Add(s);
                report.Append($"{s.Key}: коротко {shortText.Trim().Length} из {full.Length}; ");
                continue;
            }
            omitted.Add(s);
            report.Append($"{s.Key}: не поместился ({full.Length}); ");
        }
        if (omitted.Count > 0)
            sb.AppendLine("НЕ ПОМЕСТИЛИСЬ (данные есть в программе): " + string.Join("; ", omitted.Select(o => $"{o.Title} — строкой «НУЖНО: {o.Key}»"))
                          + ". Если для ответа нужен такой раздел, ответь ТОЛЬКО одной строкой НУЖНО: ключ — программа пришлёт его полностью.");
        var text = sb.ToString().TrimEnd();
        LastReport = $"данные {text.Length} симв. (бюджет {budget}): " + report.ToString().TrimEnd(' ', ';');
        return (text, LastReport, included);
    }

    private static readonly Regex NeedLine = new(@"^\s*\**\s*НУЖНО\s*:\s*([a-z0-9_]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>Ответ ИИ — просьба прислать раздел целиком («НУЖНО: warehouse»): ключ или null.</summary>
    public static string? NeedRequest(string? answer)
    {
        var a = (answer ?? "").Trim();
        if (a.Length == 0 || a.Length > 300)
            return null;
        var m = NeedLine.Match(a);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
