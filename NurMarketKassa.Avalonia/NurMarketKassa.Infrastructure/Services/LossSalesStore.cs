using System.Text.Json;

namespace NurMarketKassa.Services;

public sealed class LossSaleLine
{
    public string Title { get; set; } = "";
    public double Quantity { get; set; }
    public string Unit { get; set; } = "";
    public double UnitPrice { get; set; }
    public double UnitCost { get; set; }
    /// <summary>Сколько взяли за строку с учётом всех скидок.</summary>
    public double Net { get; set; }
    public double Loss { get; set; }
}

public sealed class LossSaleRecord
{
    public DateTimeOffset At { get; set; }
    public string? SaleId { get; set; }
    public string Cashier { get; set; } = "";
    public List<LossSaleLine> Lines { get; set; } = new();
    public double Loss => Lines.Sum(l => l.Loss);
}

/// <summary>2026-10-03, владелец: «если в убыток продаёт — фиксировать это в админке». Касса пишет сюда каждый
/// оплаченный чек, где со скидкой товар ушёл дешевле закупки; программа владельца показывает журнал в разделе
/// «Продажи в убыток». Файл общий для кассы и программы владельца на этом компьютере
/// (%LOCALAPPDATA%\NurMarketKassa\loss-sales.json), хранится 180 дней. На сервере NurCRM такого поля нет
/// (ТЗ для бэкенда, часть 8) — с другого компьютера журнал этой кассы пока не виден.</summary>
public static class LossSalesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "loss-sales.json");
    private static readonly object Gate = new();

    public static event Action? Changed;

    public static void Append(LossSaleRecord record)
    {
        lock (Gate)
        {
            var all = ReadUnlocked();
            all.Add(record);
            var cutoff = DateTimeOffset.Now.AddDays(-180);
            all.RemoveAll(r => r.At < cutoff);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, FilePath, true);
        }
        PosLogger.Log($"Продажа в убыток записана: {record.Loss:0.00} сом, позиций {record.Lines.Count}.", "CART");
        try { Changed?.Invoke(); } catch { /* журнал — не повод ломать оплату */ }
    }

    public static IReadOnlyList<LossSaleRecord> ReadAll()
    {
        lock (Gate)
            return ReadUnlocked().OrderByDescending(r => r.At).ToList();
    }

    private static List<LossSaleRecord> ReadUnlocked()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<LossSaleRecord>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал продаж в убыток не прочитан: {ex.Message}", "WARNING");
        }
        return new();
    }
}
