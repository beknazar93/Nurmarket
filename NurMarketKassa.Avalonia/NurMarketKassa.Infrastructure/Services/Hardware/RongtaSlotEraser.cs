using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Что и из чего выбирает владелец в окне «Стереть старые ячейки».</summary>
public enum RongtaEraseMode
{
    /// <summary>Только ячейки, из которых товар «переехал» на другой номер, и ячейки товаров, которых больше нет в каталоге.</summary>
    Stale,

    /// <summary>Номера, которые владелец вписал сам (список и диапазоны: «5, 10-20, 33»).</summary>
    Range,

    /// <summary>Все ячейки 1…4000 (жёсткое подтверждение в окне).</summary>
    All,
}

/// <summary>Почему список номеров не разобрался (текст на 5 языках — в окне).</summary>
public enum RongtaSlotListProblem
{
    None,
    Empty,
    BadToken,
    OutOfRange,
}

/// <summary>Итог планирования: что будет стёрто и что пропущено как защищённое (товары, которые сейчас отмечены в окне, и т. п.).</summary>
public sealed record RongtaErasePlan(IReadOnlyList<int> Slots, IReadOnlyList<int> SkippedProtected);

/// <summary>
/// 2026-11-21 (просьба владельца «добавь возможность старые ячейки стирать!»): правила стирания ячеек PLU на весах Rongta — без окна и сети, чтобы их можно было проверить тестами.
///
/// КАК СТИРАЕМ. Подтверждённой «родной» команды удаления PLU у весов Rongta нет: в руководстве RLS1000 «Operate: I — обновить, D — удалить» есть только у TCP-интерфейса
/// «RLS1000 ↔ сервер» (0110, режим «свой сервер», до весов не доходит), а в UDP-протоколе 5001 (разбор — docs/Отчеты/Rongta-RLS1000-разбор.md) удаление при разборе не встретилось
/// (RLS1000 «обновить plu» в ловушке удаление не показала). Поэтому ячейка ОЧИЩАЕТСЯ тем же проверенным на весах владельца способом записи — строкой «!0V» (TCP 4001), в которой
/// пустое название, код 0, цена 0, срок и тара 0: <see cref="BuildBlankCommand"/>. Ячейка на весах остаётся пустым товаром «без названия, 0,00», а не пропадает из памяти.
/// НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО (строка собирается тем же кодом, что и обычные карточки).
/// </summary>
public static class RongtaSlotEraser
{
    /// <summary>Больше ячеек у этих весов нет (<see cref="DahuaTmProtocol.MaxPluNumber"/>).</summary>
    public const int MaxSlot = DahuaTmProtocol.MaxPluNumber;

    /// <summary>Разбор «5, 10-20; 33»: номера и диапазоны через запятую, точку с запятой или пробел (тире — любое). Результат — по возрастанию, без повторов.
    /// Перевёрнутый диапазон («20-10») понимается как 10-20.</summary>
    public static RongtaSlotListProblem TryParseSlots(string? text, out List<int> slots, int max = MaxSlot)
    {
        slots = new List<int>();
        var tokens = (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return RongtaSlotListProblem.Empty;
        var set = new SortedSet<int>();
        foreach (var raw in tokens)
        {
            var token = raw.Replace('–', '-').Replace('—', '-').Replace('‑', '-');
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!TryNumber(token, out var one))
                    return RongtaSlotListProblem.BadToken;
                if (one < 1 || one > max)
                    return RongtaSlotListProblem.OutOfRange;
                set.Add(one);
                continue;
            }
            if (dash == 0 || dash == token.Length - 1 || token.IndexOf('-', dash + 1) >= 0
                || !TryNumber(token[..dash], out var from) || !TryNumber(token[(dash + 1)..], out var to))
                return RongtaSlotListProblem.BadToken;
            if (from > to)
                (from, to) = (to, from);
            if (from < 1 || to > max)
                return RongtaSlotListProblem.OutOfRange;
            for (var n = from; n <= to; n++)
                set.Add(n);
        }
        slots = set.ToList();
        return RongtaSlotListProblem.None;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Все ячейки 1…<paramref name="max"/>.</summary>
    public static List<int> AllSlots(int max = MaxSlot) => Enumerable.Range(1, Math.Max(0, max)).ToList();

    /// <summary>Что стереть: запрошенные номера в пределах 1…max без повторов, кроме защищённых (их касса не трогает никогда).</summary>
    public static RongtaErasePlan Plan(IEnumerable<int> requested, IReadOnlySet<int> protectedSlots, int max = MaxSlot)
    {
        var slots = new SortedSet<int>();
        var skipped = new SortedSet<int>();
        foreach (var slot in requested)
        {
            if (slot < 1 || slot > max)
                continue;
            if (protectedSlots.Contains(slot))
                skipped.Add(slot);
            else
                slots.Add(slot);
        }
        return new RongtaErasePlan(slots.ToList(), skipped.ToList());
    }

    /// <summary>Пустая карточка для ячейки: ни названия, ни кода, ни цены. Единица «весовой» — как у карточек по умолчанию; префикс 0, чтобы не напоминал штрихкод товара.</summary>
    public static DahuaTmPlu BlankRecord(int slot) => new()
    {
        PluNumber = slot,
        ProductCode = 0,
        Price = 0m,
        WeighMode = DahuaTmWeighMode.Weighed,
        ShelfLifeDays = 0,
        BarcodePrefix = 0,
        Name = "",
    };

    /// <summary>Строка «!0V» с пустой карточкой (имя парами Rongta, как у обычных записей кассы) — то, чем ячейка стирается.</summary>
    public static string BuildBlankCommand(int slot, int priceDecimals) =>
        DahuaTmProtocol.BuildPluCommand(BlankRecord(slot), priceDecimals, nameTerminator: true, DahuaTmNameCodec.Rongta);

    /// <summary>Запомнить старые ячейки «переехавших» товаров (номер → название товара, как он был записан): по ним окно предложит стереть. Название берётся из прошлой отправки.</summary>
    public static void RememberStale(IDictionary<int, string> stale, IEnumerable<ScalePluMove> slotsToClear, IReadOnlyDictionary<string, ScaleSentPlu> lastSent)
    {
        foreach (var move in slotsToClear)
        {
            if (move.From < 1 || move.From > MaxSlot)
                continue;
            var name = lastSent.TryGetValue(move.ProductId, out var before) ? before.Name ?? "" : "";
            stale[move.From] = name;
        }
    }

    /// <summary>В ячейку записали товар (или её стёрли) — это уже не «старая».</summary>
    public static void Forget(IDictionary<int, string> stale, IEnumerable<int> slots)
    {
        foreach (var slot in slots)
            stale.Remove(slot);
    }

    /// <summary>Кандидаты «старых» ячеек: запомненные + ячейки товаров из «что на весах», которых больше нет в каталоге (<paramref name="isLive"/> null — каталог не загружен, их не считаем).</summary>
    public static SortedDictionary<int, string> StaleCandidates(IReadOnlyDictionary<int, string> stale, IReadOnlyDictionary<string, ScaleSentPlu> lastSent, Func<string, bool>? isLive)
    {
        var result = new SortedDictionary<int, string>();
        foreach (var (slot, name) in stale)
            if (slot is >= 1 and <= MaxSlot)
                result[slot] = name;
        if (isLive is not null)
        {
            foreach (var (id, sent) in lastSent)
            {
                if (sent.Plu is < 1 or > MaxSlot || isLive(id))
                    continue;
                if (!result.ContainsKey(sent.Plu))
                    result[sent.Plu] = sent.Name ?? "";
            }
        }
        return result;
    }

    /// <summary>Номера сжато: «1–5, 7, 10–12»; не больше <paramref name="maxItems"/> групп, дальше «…».</summary>
    public static string FormatSlots(IReadOnlyList<int> slots, int maxItems = 40)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < slots.Count)
        {
            var j = i;
            while (j + 1 < slots.Count && slots[j + 1] == slots[j] + 1)
                j++;
            parts.Add(j > i + 1 ? $"{slots[i]}–{slots[j]}" : j == i + 1 ? $"{slots[i]}, {slots[j]}" : slots[i].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }
        if (parts.Count <= maxItems)
            return string.Join(", ", parts);
        return string.Join(", ", parts.Take(maxItems)) + ", …";
    }
}
