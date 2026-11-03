using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>Условия проката для чека: срок, цена за сутки, итог, залог, штраф за просрочку.</summary>
public sealed record RentalReceiptInfo(
    int Number,
    DateTime From,
    DateTime To,
    double PricePerDay,
    int Days,
    double Total,
    double DepositAmount,
    string DepositDocument,
    double LatePenaltyPerDay);

/// <summary>2026-10-06, владелец: «в чек при прокате — с надписью ПРОКАТ, сумма за день, итоговая сумма проката, дата
/// от и до и инфо о штрафе за просрочку». У проката на сервере NurCRM нет цены (стоимость касса берёт строкой
/// «Прокат №N…»), поэтому условия запоминаются на кассе при оформлении (NewRentalWindow) и печатаются в чеке
/// (CartReceiptTextBuilder). На другой кассе условий нет — тогда номер и срок берутся из самой строки «Прокат №N…».</summary>
public static class RentalReceiptInfoStore
{
    private const int Keep = 500;
    private static readonly object Sync = new();
    private static Dictionary<int, RentalReceiptInfo>? _cache;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "rental-receipts.json");

    public static void Save(RentalReceiptInfo info)
    {
        lock (Sync)
        {
            var all = LoadUnsafe();
            all[info.Number] = info;
            if (all.Count > Keep)
                foreach (var old in all.Keys.OrderBy(k => k).Take(all.Count - Keep).ToList())
                    all.Remove(old);
            var json = JsonSerializer.Serialize(all.Values.ToList());
            if (_partial)
            {
                AtomicFile.DeferWrite(FilePath, "rental-receipts.json", json, MergeInfos); // 2026-11-02 (К3-01): файл не прочитан/был занят — отложенная запись
                return;
            }
            try
            {
                // 2026-11-01 (аудит 10 из 10, Н-07): запись через AtomicFile — обрыв питания не оставляет полфайла.
                AtomicFile.WriteAllText(FilePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _partial = true;
                AtomicFile.DeferWrite(FilePath, "rental-receipts.json", json, MergeInfos);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Прокат №{info.Number}: условия для чека не сохранены ({ex.Message}).", "WARNING");
            }
        }
    }

    /// <summary>Объединение при отложенной записи: условия из памяти + те, что есть только в файле (по номеру проката).</summary>
    private static string MergeInfos(string? fileText, string memoryText)
    {
        List<RentalReceiptInfo> Parse(string? text)
        {
            try
            {
                return text is null ? new() : JsonSerializer.Deserialize<List<RentalReceiptInfo>>(text) ?? new();
            }
            catch (JsonException)
            {
                return new();
            }
        }
        var memory = Parse(memoryText);
        foreach (var e in Parse(fileText))
            if (!memory.Any(x => x.Number == e.Number))
                memory.Add(e);
        return JsonSerializer.Serialize(memory);
    }

    public static RentalReceiptInfo? TryGet(int number)
    {
        lock (Sync)
            return LoadUnsafe().TryGetValue(number, out var info) ? info : null;
    }

    // 2026-11-02 (аудит №2 кассы, К3-01): файл занят — не «условий нет»: кэш помечается неполным, запись в файл откладывается (иначе перезаписали бы файл одной новой записью).
    private static bool _partial;

    private static Dictionary<int, RentalReceiptInfo> LoadUnsafe()
    {
        if (_cache != null && !_partial)
            return _cache;
        var dict = new Dictionary<int, RentalReceiptInfo>();
        var read = AtomicFile.TryRead(FilePath, attempts: 3, pauseMs: 100); // 2026-11-01 (Н-07): битый файл — из копии .bak
        try
        {
            if (read.Text is { } text)
                foreach (var info in JsonSerializer.Deserialize<List<RentalReceiptInfo>>(text) ?? new())
                    dict[info.Number] = info;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Условия прокатов для чека не прочитаны ({ex.Message}).", "WARNING");
        }
        // 2026-11-02 (приёмка круга 3): данные из памяти (отложенная запись) или из копии .bak — писать поверх файла нельзя, только через отложенную запись с объединением.
        _partial = read.Status == AtomicFile.ReadStatus.Unreadable || read.FromMemory;
        _cache = dict;
        return _cache;
    }

    private static readonly Regex NumberPattern = new(@"Прокат №\s*(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex RangePattern = new(@"(\d{2}\.\d{2}\.\d{4})\s*[–-]\s*(\d{2}\.\d{2}\.\d{4})", RegexOptions.CultureInvariant);
    private static readonly Regex UntilPattern = new(@"до (\d{2}\.\d{2}(?:\.\d{4})?)", RegexOptions.CultureInvariant);

    /// <summary>Строки чека «Прокат №N…» → условия проката: из запомненных, иначе — номер и срок из самой строки.</summary>
    public static RentalReceiptInfo? FromLineName(string name, DateTime receiptTime)
    {
        var m = NumberPattern.Match(name ?? "");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return null;
        if (TryGet(number) is { } saved)
            return saved;

        DateTime from = receiptTime.Date, to = default;
        var range = RangePattern.Match(name!);
        if (range.Success
            && DateTime.TryParseExact(range.Groups[1].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            && DateTime.TryParseExact(range.Groups[2].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            from = f;
            to = t;
        }
        else if (UntilPattern.Match(name!) is { Success: true } until)
        {
            var text = until.Groups[1].Value;
            if (text.Length == 5 && DateTime.TryParseExact(text + "." + receiptTime.Year.ToString(CultureInfo.InvariantCulture), "dd.MM.yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var u))
                to = u < receiptTime.Date ? u.AddYears(1) : u;
            else if (DateTime.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u2))
                to = u2;
        }
        return new RentalReceiptInfo(number, from, to, 0, to == default ? 0 : Math.Max(1, (to.Date - from.Date).Days), 0, 0, "", 0);
    }
}
