using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, проверка обмена на тестовом аккаунте: при обмене товара из чека ЗАКРЫТОЙ смены (купил вчера —
/// меняет сегодня) сервер завышает ожидаемую наличность текущей смены (expected_cash) ровно на сумму возвращённого.
/// Обмен пишет в смену пару движений «Возврат по чеку (Наличные)» и «Наличные остались в кассе: возврат отдан безналом»,
/// и у чека закрытой смены в expected_cash попадает только второе (№1585: вернули 40, сдача 5 — ящик −5, сервер +35).
/// Чек текущей или другой открытой смены сервер считает верно (+8, −5, +4 — проверено через кассу).
/// Кассир при закрытии смены увидел бы недостачу, которой нет. ТЗ бэкенда, часть 14, п. 14.1.
///
/// Пока сервер не исправлен, касса после каждого обмена сравнивает, на сколько сервер изменил expected_cash, с тем,
/// сколько наличных реально прошло через ящик, и запоминает поправку на смену — только если разница совпала
/// с суммой возвращённого (это и есть ошибка; любое другое расхождение — чужие операции в это же время, их не трогаем).
/// Когда сервер исправят, разницы не будет и поправки перестанут записываться сами.
/// Поправка прибавляется к остатку смены там же, где неотправленные внесения (ShiftCashOperationsStore.NetForShift).</summary>
public static class ExchangeCashCorrections
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "exchange_cash_fix.json");

    private sealed class Entry
    {
        public string ShiftId { get; set; } = "";
        public string ExchangeId { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Сумма поправок по смене (обычно отрицательная — сервер завысил остаток).</summary>
    public static decimal ForShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        lock (FileLock)
            return Load().Where(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Amount);
    }

    /// <summary>expected_cash смены с сервера; null — не удалось узнать (тогда поправку не считаем).</summary>
    public static async Task<decimal?> ReadExpectedCashAsync(NurMarketApiClient api, string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var row = await api.RequestAsync(HttpMethod.Get, $"api/construction/shifts/{Uri.EscapeDataString(shiftId)}/", null, null, cts.Token, TimeSpan.FromSeconds(6))
                .ConfigureAwait(false);
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("expected_cash", out var v)
                && decimal.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обмен: остаток смены с сервера не получен ({ex.Message}).", "WARNING");
        }
        return null;
    }

    /// <summary>После обмена: сверяет изменение expected_cash с реальным движением наличных и при известной ошибке
    /// сервера запоминает поправку. <paramref name="cashDelta"/> — сколько наличных реально пришло (+) или ушло (−).</summary>
    public static async Task CheckAsync(NurMarketApiClient api, string? shiftId, decimal? expectedBefore, decimal cashDelta, decimal returnedAmount, string exchangeId)
    {
        if (expectedBefore is not { } before || string.IsNullOrWhiteSpace(shiftId) || returnedAmount <= 0m)
            return;
        var after = await ReadExpectedCashAsync(api, shiftId).ConfigureAwait(false);
        if (after is not { } a)
            return;
        var overstated = (a - before) - cashDelta;
        if (Math.Abs(overstated) < 0.01m)
        {
            PosLogger.Log($"Обмен {exchangeId}: остаток смены на сервере изменился верно ({a - before:0.00}).", "SALES");
            return;
        }
        if (Math.Abs(overstated - returnedAmount) >= 0.01m)
        {
            PosLogger.Log($"Обмен {exchangeId}: остаток смены на сервере изменился на {a - before:0.00}, наличных прошло {cashDelta:0.00} — расхождение {overstated:0.00} не похоже на известную ошибку, поправку не пишем.", "WARNING");
            return;
        }
        lock (FileLock)
        {
            var list = Load();
            if (list.Any(e => string.Equals(e.ExchangeId, exchangeId, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(new Entry { ShiftId = shiftId, ExchangeId = exchangeId, Amount = -overstated, CreatedAt = DateTime.Now });
            // Старше 60 дней — смены давно закрыты.
            list.RemoveAll(e => e.CreatedAt < DateTime.Now.AddDays(-60));
            Save(list);
        }
        PosLogger.Log($"Обмен {exchangeId}: сервер завысил остаток смены на {overstated:0.00} (ошибка сервера с возвращённым товаром) — касса учитывает поправку.", "WARNING");
        PosDataEvents.RaiseSalesChanged();
    }

    private static List<Entry> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), JsonOpts) ?? new List<Entry>()
                : new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поправка обмена не сохранена: {ex.Message}", "WARNING");
        }
    }
}
