using System;
using System.Collections.Generic;
using System.Linq;

namespace NurMarketKassa.Services;

/// <summary>2026-09-28: описание релиза на GitHub — разметка Markdown сразу на пяти языках
/// («## v1.17.19», «---», «# Русский», таблица «было → стало»). Баннер «Доступно обновление»
/// в Маркетплейсе и окно отката версии показывали её как есть — владелец видел «## v1.17.19» и
/// «---» вместо списка изменений. Здесь из описания берётся раздел на языке программы и
/// превращается в обычный текст: заголовки — строками, пункты — «•», строки таблицы —
/// «• что: было → стало», картинки и разделители убираются.</summary>
public static class ReleaseNotesText
{
    /// <param name="markdown">Описание релиза как есть.</param>
    /// <param name="maxItems">0 — всё; иначе только первые N пунктов первого списка без
    /// заголовков (для короткого баннера).</param>
    public static string Plain(string? markdown, int maxItems = 0)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // Описание на нескольких языках — берём раздел своего языка (до следующего «---»
        // или заголовка другого языка). Одноязычное описание — целиком.
        var header = "# " + Tr.T("Русский", "Кыргызча", "English", "Türkçe", "O'zbekcha");
        var start = Array.FindIndex(lines, l => string.Equals(l.Trim(), header, StringComparison.OrdinalIgnoreCase));
        var section = start >= 0
            ? lines.Skip(start + 1).TakeWhile(l => l.Trim() != "---" && !l.StartsWith("# ", StringComparison.Ordinal)).ToArray()
            : lines;

        var result = new List<string>();
        var items = 0;
        for (var i = 0; i < section.Length; i++)
        {
            var line = section[i].Trim();
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("![", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('#'))
            {
                // Строка версии («## v1.17.19») не нужна: версия и так написана рядом.
                var title = line.TrimStart('#').Trim();
                if (maxItems > 0 || title.StartsWith("v", StringComparison.OrdinalIgnoreCase) && char.IsDigit(title.ElementAtOrDefault(1)))
                    continue;
                if (result.Count > 0)
                    result.Add("");
                result.Add(title);
                continue;
            }

            if (line.StartsWith('|'))
            {
                // Строка-разделитель «|---|» и строка заголовков таблицы (перед разделителем) не нужны.
                var next = i + 1 < section.Length ? section[i + 1].Trim() : "";
                if (line.StartsWith("|-", StringComparison.Ordinal) || next.StartsWith("|-", StringComparison.Ordinal) || maxItems > 0)
                    continue;
                var cells = line.Trim('|').Split('|').Select(c => c.Trim().Replace("**", "")).ToArray();
                if (cells.Length >= 3)
                    result.Add($"• {cells[0]}: {cells[1]} → {cells[2]}");
                else
                    result.Add("• " + string.Join(" — ", cells));
                continue;
            }

            line = line.Replace("**", "");
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items++;
                if (maxItems > 0 && items > maxItems)
                    break;
                line = "• " + line[2..];
            }
            else if (maxItems > 0)
            {
                continue;
            }

            result.Add(line);
        }

        return string.Join("\n", result).Trim();
    }
}
