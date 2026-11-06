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
        NurMarketKassa.Services.AppPaths.Local, "NurMarketKassa", "loss-sales.json");
    private static readonly object Gate = new();

    public static event Action? Changed;

    public static void Append(LossSaleRecord record)
    {
        lock (Gate)
        {
            // 2026-11-06 (В11, шаг 22): чтение-добавление-запись под межпроцессным замком (AtomicFile.Update): файл общий для кассы и программы владельца,
            // а замок Gate действовал только внутри одного процесса.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.Update(FilePath, text => AppendTo(text, record)); // 2026-11-01 (Н2-01/Н-07): сброс на диск + копия .bak
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Продажа в убыток {record.Loss:0.00} сом не записана в журнал (файл занят: {ex.Message}) — только в этой строке.", "WARNING");
                return;
            }
        }
        PosLogger.Log($"Продажа в убыток записана: {record.Loss:0.00} сом, позиций {record.Lines.Count}.", "CART");
        try { Changed?.Invoke(); } catch { /* журнал — не повод ломать оплату */ }
        FraudMonitor.OnLossSale(record); // 2026-11-05 (волна 2-Б, функция 2): цена ниже закупки — в «Подозрительные операции» (сама проверяет, включено ли)
    }

    /// <summary>2026-11-06 (В11, шаг 22): новый текст файла журнала — прежние записи + <paramref name="record"/>, старше 180 дней убраны. Нечитаемый JSON — как пустой журнал.</summary>
    internal static string AppendTo(string? currentText, LossSaleRecord record)
    {
        var all = new List<LossSaleRecord>();
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            try { all = JsonSerializer.Deserialize<List<LossSaleRecord>>(currentText) ?? new(); }
            catch (JsonException ex) { PosLogger.Log($"Журнал продаж в убыток не прочитан: {ex.Message}", "WARNING"); }
        }
        all.Add(record);
        var cutoff = DateTimeOffset.Now.AddDays(-180);
        all.RemoveAll(r => r.At < cutoff);
        return JsonSerializer.Serialize(all);
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
                return JsonSerializer.Deserialize<List<LossSaleRecord>>(AtomicFile.ReadWithRetry(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Журнал продаж в убыток: файл занят ({ex.Message}) — не затираем.", "WARNING");
            return new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал продаж в убыток не прочитан: {ex.Message}", "WARNING");
        }
        return new();
    }
}
