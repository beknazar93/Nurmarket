using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-29: что реально записано на весы при последней отправке товара — номер ячейки
/// ПЛУ, код товара в записи (он же «Код в ШК» этикетки) и название. Нужно, чтобы при смене номера
/// знать, какую старую ячейку очистить и какие клавиши весов перевести на новый номер.</summary>
public sealed class ScaleSentPlu
{
    public int Plu { get; set; }

    /// <summary>Код товара в записи ПЛУ (печатается в весовом штрих-коде). null — неизвестен
    /// (например, номер восстановлен из журнала отправки, где кода нет).</summary>
    public string? Code { get; set; }

    /// <summary>Название товара на момент отправки — для проверки «в старой ячейке лежит именно он».</summary>
    public string? Name { get; set; }
}

/// <summary>Товар переехал: в прошлый раз был записан в ячейку <see cref="From"/>, сейчас — в <see cref="To"/>.</summary>
public readonly record struct ScalePluMove(string ProductId, int From, int To);

/// <summary>У товара сменился код в записи ПЛУ (клавиши «выбор по коду» надо перевести).</summary>
public readonly record struct ScaleCodeMove(string ProductId, long From, long To);

/// <summary>Итог сравнения «что было на весах» и «что пишем сейчас».</summary>
public sealed record ScalePluMovePlan(
    IReadOnlyList<ScalePluMove> PluMoves,
    IReadOnlyList<ScaleCodeMove> CodeMoves,
    IReadOnlyList<ScalePluMove> SlotsToClear)
{
    public bool IsEmpty => PluMoves.Count == 0 && CodeMoves.Count == 0;
}

/// <summary>2026-09-29, живой баг клиента «Алтымыш ата» (ШТРИХ-ПРИНТ М по сети): «весовой товар
/// таразага отправка кылган сайын ПЛУ алмашып кетти» (при каждой отправке ПЛУ товаров менялись) и
/// просьба владельца «надо сделать так, чтобы товар фиксировался на определённый плу».
///
/// Причина: окно «Весы» (ScalesPluWindow) при галочке «Нумеровать под клавиши весов» (она стояла
/// по умолчанию) раздавало номера ПЛУ ПОДРЯД в текущем порядке отмеченных строк. Добавили товар
/// (сортировка по названию), сняли галочку с одного, отправили только изменившиеся — и все товары
/// ниже сдвигались на другие ячейки. Клавиши на весах вызывают ячейку по НОМЕРУ, поэтому под той же
/// клавишей оказывался другой товар, а старые ячейки никто не чистил.
///
/// Теперь номер закрепляется за товаром один раз (файл весов label-scales.json, у каждых весов
/// свой список) и больше не зависит от порядка, фильтра и выбора строк. Здесь — только правила
/// (без окна и файлов), чтобы их можно было проверить отдельно:
/// • новый товар получает наименьший свободный номер, начиная с «Начальный PLU»;
/// • номер правится владельцем — с проверкой диапазона и того, что он не занят другим товаром;
/// • при первом запуске номера восстанавливаются из журнала последней успешной отправки;
/// • если номер или код товара сменился — какие клавиши весов перевести и какую ячейку очистить.</summary>
public static class ScalePluPlanner
{
    /// <summary>Строка журнала после успешной прямой отправки на ШТРИХ-ПРИНТ (ScalesPluWindow):
    /// «Весы, раскладка клавиш: 1 — Яблоки; 2 — Груши».</summary>
    public const string ShtrikhLayoutMarker = "Весы, раскладка клавиш: ";

    /// <summary>То же для TM-30F: «TM-30F, раскладка PLU: 1 — Яблоки; 2 — Груши».</summary>
    public const string TmLayoutMarker = "TM-30F, раскладка PLU: ";

    /// <summary>Номера для товаров, у которых закреплённого номера ещё нет: каждому по порядку —
    /// наименьший свободный номер не меньше <paramref name="start"/>. Уже закреплённые номера не
    /// трогаются никогда.</summary>
    /// <param name="pinned">Закреплённые номера: id товара → номер.</param>
    /// <param name="orderedIds">Товары, которым нужен номер, в порядке выдачи.</param>
    /// <param name="isLive">Есть ли товар в каталоге. Номер товара, которого в каталоге больше нет
    /// (удалён, другой аккаунт), считается свободным. null — все живые.</param>
    /// <param name="reserved">Номера, занятые помимо закреплённых (PLU из карточек товаров в режиме
    /// «PLU из карточки»).</param>
    public static Dictionary<string, int> AssignMissing(
        IReadOnlyDictionary<string, int> pinned,
        IEnumerable<string> orderedIds,
        int start,
        int max,
        Func<string, bool>? isLive = null,
        IEnumerable<int>? reserved = null)
    {
        var used = new HashSet<int>(pinned.Where(p => isLive?.Invoke(p.Key) ?? true).Select(p => p.Value));
        if (reserved is not null)
            used.UnionWith(reserved);

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = Math.Max(1, start);
        foreach (var id in orderedIds)
        {
            if (pinned.ContainsKey(id) || result.ContainsKey(id))
                continue;
            while (next <= max && used.Contains(next))
                next++;
            if (next > max)
                break; // места в таблице весов нет — такие товары останутся без номера
            result[id] = next;
            used.Add(next);
            next++;
        }
        return result;
    }

    public enum EditProblem
    {
        None,
        NotANumber,
        OutOfRange,
        Taken,
    }

    /// <summary>Проверка номера, который владелец вписал в колонку PLU.</summary>
    /// <param name="takenBy">id товара, у которого этот номер уже закреплён (при Taken).</param>
    public static EditProblem CheckEdit(
        IReadOnlyDictionary<string, int> pinned,
        string productId,
        string? text,
        int max,
        Func<string, bool>? isLive,
        out int value,
        out string? takenBy)
    {
        takenBy = null;
        value = 0;
        if (!int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return EditProblem.NotANumber;
        if (value < 1 || value > max)
            return EditProblem.OutOfRange;
        foreach (var (id, plu) in pinned)
        {
            if (plu != value || string.Equals(id, productId, StringComparison.Ordinal))
                continue;
            if (isLive?.Invoke(id) ?? true)
            {
                takenBy = id;
                return EditProblem.Taken;
            }
        }
        return EditProblem.None;
    }

    /// <summary>Разбирает строку журнала «…раскладка…: 1 — Яблоки; 2 — Груши» в пары (номер,
    /// название). null — в строке нет маркера.</summary>
    public static List<(int Plu, string Name)>? ParseLayout(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var body = line[(at + marker.Length)..].TrimEnd();
        var result = new List<(int Plu, string Name)>();
        foreach (var part in body.Split("; "))
        {
            var dash = part.IndexOf(" — ", StringComparison.Ordinal);
            if (dash <= 0)
                continue;
            if (!int.TryParse(part[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var plu) || plu <= 0)
                continue;
            var name = part[(dash + 3)..].Trim();
            if (name.Length > 0)
                result.Add((plu, name));
        }
        return result;
    }

    /// <summary>Сопоставляет раскладку из журнала с товарами каталога по названию. Берутся только
    /// однозначные названия: если в каталоге два «Яблока», номер не угадывается.</summary>
    public static Dictionary<string, int> MatchLayout(
        IReadOnlyList<(int Plu, string Name)> layout,
        IEnumerable<(string Id, string Title)> products)
    {
        var byTitle = products
            .GroupBy(p => (p.Title ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedPlu = new HashSet<int>();
        foreach (var (plu, name) in layout)
        {
            if (!byTitle.TryGetValue(name.Trim(), out var id))
                continue;
            if (result.ContainsKey(id) || !usedPlu.Add(plu))
                continue;
            result[id] = plu;
        }
        return result;
    }

    /// <summary>Сравнивает то, что было записано на весы в прошлый раз, с тем, что пишется сейчас.
    /// Старая ячейка переехавшего товара чистится, только если в эту отправку в неё не пишется
    /// другой товар.</summary>
    /// <param name="lastSent">Что было на весах: id товара → запись.</param>
    /// <param name="writtenNow">Что записано сейчас (только принятые весами записи).</param>
    public static ScalePluMovePlan PlanMoves(
        IReadOnlyDictionary<string, ScaleSentPlu> lastSent,
        IReadOnlyDictionary<string, ScaleSentPlu> writtenNow)
    {
        var writtenSlots = new HashSet<int>(writtenNow.Values.Select(v => v.Plu));
        var pluMoves = new List<ScalePluMove>();
        var codeMoves = new List<ScaleCodeMove>();
        var clear = new List<ScalePluMove>();
        foreach (var (id, now) in writtenNow)
        {
            if (!lastSent.TryGetValue(id, out var before))
                continue;
            if (before.Plu > 0 && before.Plu != now.Plu)
            {
                var move = new ScalePluMove(id, before.Plu, now.Plu);
                pluMoves.Add(move);
                if (!writtenSlots.Contains(before.Plu))
                    clear.Add(move);
            }
            if (TryCode(before.Code, out var oldCode) && TryCode(now.Code, out var newCode) && oldCode != newCode)
                codeMoves.Add(new ScaleCodeMove(id, oldCode, newCode));
        }
        return new ScalePluMovePlan(pluMoves, codeMoves, clear);
    }

    /// <summary>Во что перевести клавишу весов, которая вызывала переехавший товар: «выбор по
    /// номеру ПЛУ» со старым номером → новый номер, «выбор по коду» со старым кодом → новый код.
    /// null — клавиша к переездам не относится (или неоднозначна) и остаётся как есть.</summary>
    public static (byte Function, long Value)? RemapHotkey(byte function, long value, ScalePluMovePlan plan)
    {
        if (function == ShtrikhPrintProtocol.HotkeyPluNumber)
        {
            var targets = plan.PluMoves.Where(m => m.From == value).Select(m => m.To).Distinct().ToList();
            if (targets.Count == 1 && targets[0] != value)
                return (function, targets[0]);
        }
        else if (function == ShtrikhPrintProtocol.HotkeyProductCode)
        {
            var targets = plan.CodeMoves.Where(m => m.From == value).Select(m => m.To).Distinct().ToList();
            if (targets.Count == 1 && targets[0] != value)
                return (function, targets[0]);
        }
        return null;
    }

    /// <summary>Запоминает отправку: у записанных товаров — новые номер и код; товары, чью ячейку
    /// сейчас перезаписали другим товаром или очистили, из «что на весах» убираются.</summary>
    public static void ApplySent(
        IDictionary<string, ScaleSentPlu> lastSent,
        IReadOnlyDictionary<string, ScaleSentPlu> writtenNow,
        IEnumerable<int> clearedSlots)
    {
        var slots = new HashSet<int>(writtenNow.Values.Select(v => v.Plu));
        slots.UnionWith(clearedSlots);
        var overwritten = lastSent
            .Where(kv => !writtenNow.ContainsKey(kv.Key) && slots.Contains(kv.Value.Plu))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in overwritten)
            lastSent.Remove(id);
        foreach (var (id, now) in writtenNow)
            lastSent[id] = new ScaleSentPlu { Plu = now.Plu, Code = now.Code, Name = now.Name };
    }

    private static bool TryCode(string? text, out long code) =>
        long.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0;
}
