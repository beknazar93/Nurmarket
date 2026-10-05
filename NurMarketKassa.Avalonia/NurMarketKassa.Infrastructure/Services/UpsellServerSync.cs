using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, ТЗ часть 7, раздел 2.2 — сервер выложил журнал допродажи 05.10 около 04:30 (владелец: «если бэкенд
/// добавит — сразу применяй»). События подсказки «С этим часто берут» (UpsellEvents в локальной базе) уходят на сервер
/// пачками до 200: после оплаты чека и раз в 5 минут; без связи копятся и досылаются. client_event_id — постоянный
/// UUID из «касса + номер строки», поэтому повторная отправка дублей не создаёт. После оплаты — PATCH link-sale
/// (номер продажи всем событиям чека); не прошёл — повторяется со следующей отправкой.
/// Сервер без этих адресов (404) — до перезапуска больше не спрашиваем, подсказки на кассе работают как раньше.
/// </summary>
public static class UpsellServerSync
{
    private const int Batch = 200;

    /// <summary>Задаётся при запуске приложения (App) — API из DI.</summary>
    public static Func<RecommendationsApi?>? ApiProvider { get; set; }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>Файл состояния читают и пишут и отправка, и «привязать продажу» — только под этой блокировкой.</summary>
    private static readonly object StateLock = new();
    private static Timer? _timer;
    private static bool _unsupported;

    private sealed class State
    {
        public long LastSentId { get; set; }
        public List<string[]> PendingLinks { get; set; } = new();
    }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "upsell-sync.json");

    /// <summary>Запустить отправку раз в 5 минут (повторный вызов ничего не делает).</summary>
    public static void EnsureStarted()
    {
        if (_timer is not null || _unsupported)
            return;
        _timer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    /// <summary>После оплаты: событиям чека — номер продажи; сначала досылаем сами события.</summary>
    public static void LinkSaleLater(string? cartKey, string? saleId)
    {
        if (string.IsNullOrWhiteSpace(cartKey) || string.IsNullOrWhiteSpace(saleId) || _unsupported)
            return;
        Update(s => s.PendingLinks.Add(new[] { cartKey, saleId }));
        EnsureStarted();
        _ = Task.Run(FlushAsync);
    }

    public static async Task FlushAsync()
    {
        if (_unsupported || ApiProvider?.Invoke() is not { } api)
            return;
        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            var lastSent = Read().LastSentId;
            var sent = 0;
            int accepted = 0, duplicates = 0, rejected = 0;
            while (true)
            {
                var rows = DatabaseService.Instance.LoadUpsellEventsAfter(lastSent, Batch);
                if (rows.Count == 0)
                    break;
                var device = LanSyncService.DeviceId;
                var events = rows.Select(r => new Dictionary<string, object?>
                {
                    ["client_event_id"] = StableUuid(device + ":" + r.Id),
                    ["event"] = r.Event,
                    ["product_id"] = r.ProductId,
                    ["trigger_product_ids"] = string.IsNullOrWhiteSpace(r.TriggerProductId) ? new List<string>() : new List<string> { r.TriggerProductId! },
                    ["cart_id"] = r.CartKey,
                    ["sale_id"] = string.IsNullOrWhiteSpace(r.SaleId) ? null : r.SaleId,
                    ["price"] = r.Price.ToString("0.00", CultureInfo.InvariantCulture),
                    ["score"] = Math.Round(r.Score, 4),
                    ["device_id"] = device,
                    ["occurred_at"] = OccurredAt(r.CreatedAt),
                }).ToList();
                var answer = await api.SendEventsAsync(events).ConfigureAwait(false);
                // 2026-10-05, проверка схемы сервера: ответ {"accepted", "duplicates", "rejected"}; rejected — события
                // с товаром, которого нет в компании (сервер их не хранит) — видно в журнале.
                accepted += Count(answer, "accepted");
                duplicates += Count(answer, "duplicates");
                rejected += Count(answer, "rejected");
                lastSent = rows[^1].Id;
                sent += rows.Count;
                Update(s => s.LastSentId = Math.Max(s.LastSentId, lastSent));
                if (rows.Count < Batch)
                    break;
            }

            var linked = 0;
            foreach (var link in Read().PendingLinks)
            {
                await api.LinkSaleAsync(link[0], link[1]).ConfigureAwait(false);
                Update(s => s.PendingLinks.RemoveAll(x => x.Length == 2 && x[0] == link[0] && x[1] == link[1]));
                linked++;
            }
            if (sent > 0 || linked > 0)
                PosLogger.Log($"Допродажа: на сервер отправлено событий {sent} (принято {accepted}, повторы {duplicates}, отклонено {rejected}), "
                    + $"привязано продаж {linked}.", "SYNC");
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            _unsupported = true;
            PosLogger.Log("Допродажа: у сервера нет журнала recommendations/events/ — события остаются на кассе.", "SYNC");
        }
        catch (Exception ex)
        {
            // Нет связи или сервер занят — пошлём со следующей отправкой.
            PosLogger.Log($"Допродажа: отправка событий отложена ({ex.Message}).", "DEBUG");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static int Count(JsonElement answer, string name) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static string OccurredAt(string createdAtUtc) =>
        DateTimeOffset.TryParse(createdAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
            : DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Постоянный UUID из строки (одинаковый при каждой отправке той же строки журнала).</summary>
    private static string StableUuid(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("nurmarket-upsell:" + text));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString();
    }

    private static State Read()
    {
        lock (StateLock)
            return Load();
    }

    private static void Update(Action<State> change)
    {
        lock (StateLock)
        {
            var state = Load();
            change(state);
            Save(state);
        }
    }

    private static State Load()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Допродажа: файл отправки не прочитан ({ex.Message}).", "WARNING");
        }
        return new State();
    }

    private static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Допродажа: файл отправки не сохранён ({ex.Message}).", "WARNING");
        }
    }
}
