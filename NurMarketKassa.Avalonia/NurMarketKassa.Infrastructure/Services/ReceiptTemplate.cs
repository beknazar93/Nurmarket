#nullable enable
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-21 (просьба владельца: «Печать чека! Редактор нужен!»): шаблон печатного чека — какие блоки печатать, в каком порядке,
// выравнивание/жирность/размер каждого блока и свой текст шапки и подвала. Построители чека (CartReceiptTextBuilder, SaleReceiptTextBuilder)
// помечают разделы строками-метками (ReceiptTemplateEngine.Section), а движок ниже переставляет и оформляет их. Один движок и один конвейер
// (ReceiptPrintPipeline) кормят и принтер (ESC/POS, графика, PDF), и предпросмотр в редакторе — на бумаге то же, что на экране.
// Шаблон по умолчанию ничего не меняет: чек печатается ровно как раньше (метки просто убираются).

/// <summary>Настройка одного блока чека.</summary>
public sealed class ReceiptBlockSetting
{
    public string Id { get; set; } = "";
    /// <summary>Печатать блок.</summary>
    public bool On { get; set; } = true;
    /// <summary>auto — как сверстал построитель чека; left / center / right.</summary>
    public string Align { get; set; } = "auto";
    public bool Bold { get; set; }
    /// <summary>1 — обычный, 2 — двойная высота, 3 — двойной размер (ширина и высота).</summary>
    public int Size { get; set; } = 1;
    /// <summary>Свой текст (только блоки «Текст шапки» и «Текст подвала»).</summary>
    public string? Text { get; set; }

    public ReceiptBlockSetting Clone() => (ReceiptBlockSetting)MemberwiseClone();
}

/// <summary>Шаблон печатного чека: упорядоченный список блоков.</summary>
public sealed class ReceiptTemplate
{
    public int Version { get; set; } = 1;
    public List<ReceiptBlockSetting> Blocks { get; set; } = new();

    public const string Title = "title", HeaderText = "header_text", Store = "store", Meta = "meta", Notes = "notes", Items = "items",
        Totals = "totals", Total = "total", Status = "status", Warranty = "warranty", Footer = "footer", FooterText = "footer_text";

    /// <summary>Блоки по умолчанию, в порядке построителя чека.</summary>
    public static readonly string[] DefaultOrder =
        { Title, HeaderText, Store, Meta, Notes, Items, Totals, Total, Status, Warranty, Footer, FooterText };

    public static bool IsCustomTextBlock(string id) => id is HeaderText or FooterText;

    public const int MaxTextChars = 400;
    public const int MaxTextLines = 6;
    public const int MaxJsonBytes = 16 * 1024;

    public static ReceiptTemplate Default()
    {
        var t = new ReceiptTemplate();
        foreach (var id in DefaultOrder)
            t.Blocks.Add(new ReceiptBlockSetting { Id = id, On = !IsCustomTextBlock(id) });
        return t;
    }

    public ReceiptTemplate Clone() => new() { Version = Version, Blocks = Blocks.Select(b => b.Clone()).ToList() };

    public ReceiptBlockSetting Block(string id) => Blocks.First(b => b.Id == id);

    /// <summary>Шаблон равен стандартному — чек печатается как раньше, без изменений.</summary>
    public bool IsDefault()
    {
        var d = Default();
        if (Blocks.Count != d.Blocks.Count)
            return false;
        for (var i = 0; i < d.Blocks.Count; i++)
        {
            var a = Blocks[i];
            var b = d.Blocks[i];
            if (a.Id != b.Id || a.On != b.On || a.Align != b.Align || a.Bold != b.Bold || a.Size != b.Size || !string.IsNullOrWhiteSpace(a.Text))
                return false;
        }
        return true;
    }

    /// <summary>Приводит к допустимому виду: неизвестные блоки убираются, недостающие добавляются в конец, значения — в границы.</summary>
    public ReceiptTemplate Normalize()
    {
        var known = new HashSet<string>(DefaultOrder);
        var result = new List<ReceiptBlockSetting>();
        foreach (var b in Blocks)
        {
            if (b is null || !known.Contains(b.Id) || result.Any(r => r.Id == b.Id))
                continue;
            b.Align = b.Align is "left" or "center" or "right" ? b.Align : "auto";
            b.Size = Math.Clamp(b.Size, 1, 3);
            b.Text = LimitText(b.Text);
            result.Add(b);
        }
        foreach (var id in DefaultOrder.Where(id => result.All(r => r.Id != id)))
            result.Add(new ReceiptBlockSetting { Id = id, On = !IsCustomTextBlock(id) });
        Blocks = result;
        return this;
    }

    private static string? LimitText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Take(MaxTextLines);
        var joined = string.Join("\n", lines);
        return joined.Length > MaxTextChars ? joined[..MaxTextChars] : joined;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReceiptTemplate? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ReceiptTemplate>(json, JsonOptions)?.Normalize();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Стили строки чека. Невидимые знаки в начале строки (в экранных предпросмотрах не видны, принтером читаются).</summary>
public static class ReceiptLineStyle
{
    /// <summary>Первый знак текста: «чек уже сверстан по шаблону» — построчная вёрстка принтера (переносы, «ИТОГО» столбиком) не применяется.</summary>
    public const char LaidOut = '⁠';
    public const char BoldMark = '⁡';
    public const char BigMark = '⁢';
    public const char HugeMark = '⁣';
    /// <summary>Строка-метка раздела (перед ней — id блока).</summary>
    public const char SectionMark = '⁤';

    public static bool IsLaidOut(string? text) => !string.IsNullOrEmpty(text) && text[0] == LaidOut;

    public static string Prefix(bool bold, int size) =>
        (bold ? BoldMark.ToString() : "") + (size >= 3 ? HugeMark.ToString() : size == 2 ? BigMark.ToString() : "");

    /// <summary>Разбирает строку: возвращает чистый текст и стиль.</summary>
    public static string Parse(string line, out bool bold, out int size)
    {
        bold = false;
        size = 1;
        var i = 0;
        while (i < line.Length && line[i] is LaidOut or BoldMark or BigMark or HugeMark)
        {
            if (line[i] == BoldMark) bold = true;
            else if (line[i] == BigMark) size = 2;
            else if (line[i] == HugeMark) size = 3;
            i++;
        }
        return i == 0 ? line : line[i..];
    }

    /// <summary>Убирает все служебные знаки из текста (для предпросмотров и вывода без стилей).</summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch is not (LaidOut or BoldMark or BigMark or HugeMark or SectionMark))
                sb.Append(ch);
        return sb.ToString();
    }
}

/// <summary>Движок шаблона: переставляет и оформляет блоки, помеченные построителем чека.</summary>
public static class ReceiptTemplateEngine
{
    [ThreadStatic] private static ReceiptTemplate? _draft;

    /// <summary>Строка-метка начала раздела; построитель кладёт её перед строками раздела.</summary>
    public static string Section(string id) => ReceiptLineStyle.SectionMark + id;

    /// <summary>Шаблон, по которому печатается чек сейчас: черновик редактора (если открыт) или сохранённый.</summary>
    public static ReceiptTemplate Current => _draft ?? Load();

    /// <summary>Сохранённый шаблон магазина (из общих настроек); нет или повреждён — стандартный.</summary>
    public static ReceiptTemplate Load() =>
        ReceiptTemplate.TryParse(UserPreferences.Instance.ReceiptTemplateJson) ?? ReceiptTemplate.Default();

    /// <summary>Сохраняет шаблон в настройки (общие для магазина). Стандартный — очищает ключ.</summary>
    public static void Save(ReceiptTemplate template)
    {
        template.Normalize();
        var json = template.IsDefault() ? null : template.ToJson();
        if (json is not null && System.Text.Encoding.UTF8.GetByteCount(json) > ReceiptTemplate.MaxJsonBytes)
            throw new InvalidOperationException("Шаблон чека слишком большой.");
        UserPreferences.Instance.ReceiptTemplateJson = json;
        UserPreferences.Instance.SaveToDisk();
    }

    /// <summary>На время предпросмотра в редакторе: чеки на этом потоке собираются по черновику, не по сохранённому шаблону.</summary>
    public static IDisposable UseDraft(ReceiptTemplate draft)
    {
        var previous = _draft;
        _draft = draft.Clone().Normalize();
        return new Scope(() => _draft = previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _undo;
        public Scope(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    /// <summary>Применяет шаблон к тексту с метками. Стандартный шаблон — только убирает метки (чек как раньше).</summary>
    public static string Apply(string raw)
    {
        var template = Current;
        var width = ReceiptLayout.CharWidth;
        var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = raw.EndsWith('\n');
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (template.IsDefault())
        {
            var plain = lines.Where(l => !IsMark(l)).ToList();
            return string.Join("\n", plain) + (endsWithNewline ? "\n" : "");
        }

        // Разбор на разделы.
        var sections = new Dictionary<string, List<string>>();
        var order = new List<string>();
        var current = "_pre";
        sections[current] = new List<string>();
        order.Add(current);
        foreach (var line in lines)
        {
            if (IsMark(line))
            {
                current = line[1..];
                if (!sections.ContainsKey(current))
                {
                    sections[current] = new List<string>();
                    order.Add(current);
                }
                continue;
            }
            sections[current].Add(line);
        }

        var output = new List<string>();
        void Emit(ReceiptBlockSetting? setting, IEnumerable<string> blockLines)
        {
            foreach (var line in blockLines)
                output.AddRange(StyleLine(line, setting, width));
        }

        if (sections.TryGetValue("_pre", out var pre))
            Emit(null, pre);

        foreach (var setting in template.Blocks)
        {
            if (!setting.On)
                continue;
            if (ReceiptTemplate.IsCustomTextBlock(setting.Id))
            {
                Emit(setting, CustomTextLines(setting, width));
                continue;
            }
            if (sections.TryGetValue(setting.Id, out var body))
                Emit(setting, body);
        }
        // Разделы, которых нет в шаблоне (построитель новее шаблона), — в конец, как есть.
        foreach (var id in order.Where(id => id != "_pre" && template.Blocks.All(b => b.Id != id)))
            Emit(null, sections[id]);

        return ReceiptLineStyle.LaidOut + string.Join("\n", output) + "\n";
    }

    private static bool IsMark(string line) => line.Length > 0 && line[0] == ReceiptLineStyle.SectionMark;

    /// <summary>Строки собственного текста шапки/подвала: по центру-умолчанию слева, перенос по ширине.</summary>
    private static IEnumerable<string> CustomTextLines(ReceiptBlockSetting setting, int width)
    {
        if (string.IsNullOrWhiteSpace(setting.Text))
            yield break;
        var w = setting.Size >= 3 ? Math.Max(8, width / 2) : width;
        foreach (var raw in setting.Text.Replace("\r", "").Split('\n').Take(ReceiptTemplate.MaxTextLines))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield return "";
                continue;
            }
            foreach (var part in ReceiptLineLayout.WrapLeft(raw, w))
                yield return part;
        }
    }

    /// <summary>Оформляет одну строку блока: выравнивание, жирность, размер. Пустая строка остаётся пустой.</summary>
    private static IEnumerable<string> StyleLine(string line, ReceiptBlockSetting? s, int width)
    {
        if (s is null || string.IsNullOrWhiteSpace(line))
        {
            yield return line;
            yield break;
        }

        var size = Math.Clamp(s.Size, 1, 3);
        var prefix = ReceiptLineStyle.Prefix(s.Bold, size);
        var text = line.TrimEnd();
        var hasColumns = HasColumns(text);

        if (size == 3)
        {
            // Двойная ширина: в строку влезает вдвое меньше знаков — перекладываем столбцы и переносим текст.
            var half = Math.Max(8, width / 2);
            if (hasColumns && SplitColumns(text) is { } cols)
            {
                foreach (var l in ReceiptLineLayout.FormatLabelAmount(cols.Left, cols.Right, half, 0).Split('\n'))
                    yield return prefix + l;
                yield break;
            }
            foreach (var part in ReceiptLineLayout.WrapLeft(text.Trim(), half))
                yield return prefix + Align(part, s.Align, half, centerIfAuto: IsCentered(line));
            yield break;
        }

        if (hasColumns)
        {
            yield return prefix + text; // две колонки (название … сумма) — вёрстку построителя не трогаем
            yield return "\u0000".Length == 0 ? "" : null!; // не достигается; держит форму итератора
            yield break;
        }

        yield return prefix + Align(text, s.Align, width, centerIfAuto: false);
    }

    private static bool IsCentered(string line)
    {
        var lead = line.Length - line.TrimStart().Length;
        return lead >= 2;
    }

    private static string Align(string text, string align, int width, bool centerIfAuto)
    {
        var t = text.Trim();
        if (t.Length >= width)
            return t;
        return align switch
        {
            "center" => ReceiptLineLayout.Center(t, width),
            "right" => t.PadLeft(Math.Max(0, width - 1)),
            "left" => t,
            _ => centerIfAuto ? ReceiptLineLayout.Center(t, width) : text,
        };
    }

    /// <summary>В строке есть «столбцы»: слева текст, затем 2+ пробела, затем значение справа.</summary>
    private static bool HasColumns(string text)
    {
        var trimmed = text.TrimStart();
        var i = trimmed.IndexOf("  ", StringComparison.Ordinal);
        return i > 0 && i < trimmed.Length - 2 && trimmed.Length - 1 >= 0 && (text.Length - text.TrimStart().Length) < 2;
    }

    private static (string Left, string Right)? SplitColumns(string text)
    {
        var t = text.Trim();
        var i = t.IndexOf("  ", StringComparison.Ordinal);
        if (i <= 0)
            return null;
        return (t[..i].TrimEnd(), t[i..].Trim());
    }
}

/// <summary>Единый конвейер подготовки текста чека к печати: и ESC/POS-принтер, и предпросмотр в редакторе чека проходят через него.</summary>
internal static class ReceiptPrintPipeline
{
    /// <summary>Готовый к печати текст (в верхнем регистре, с переносами по ширине). Чек по шаблону (<see cref="ReceiptLineStyle.IsLaidOut"/>) — без служебной
    /// чистки и без пересборки строк; обычный — как раньше: приветствие, замена способа оплаты, «ИТОГО» столбиком.</summary>
    internal static string Prepare(string text, int width, int rightMarginChars = 1)
    {
        var prepared = ReceiptLineStyle.IsLaidOut(text) ? text : ReceiptSanitizer.StripShiftCashboxAndEnsureWelcome(text);
        return ReceiptTextFormatter.FormatForPrinter(prepared, width, rightMarginChars).Trim().ToUpperInvariant();
    }
}
