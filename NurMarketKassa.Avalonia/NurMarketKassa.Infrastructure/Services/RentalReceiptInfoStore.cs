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
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all.Values.ToList()));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Прокат №{info.Number}: условия для чека не сохранены ({ex.Message}).", "WARNING");
            }
        }
    }

    public static RentalReceiptInfo? TryGet(int number)
    {
        lock (Sync)
            return LoadUnsafe().TryGetValue(number, out var info) ? info : null;
    }

    private static Dictionary<int, RentalReceiptInfo> LoadUnsafe()
    {
        if (_cache != null)
            return _cache;
        _cache = new Dictionary<int, RentalReceiptInfo>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var info in JsonSerializer.Deserialize<List<RentalReceiptInfo>>(File.ReadAllText(FilePath)) ?? new())
                    _cache[info.Number] = info;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Условия прокатов для чека не прочитаны ({ex.Message}).", "WARNING");
        }
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
