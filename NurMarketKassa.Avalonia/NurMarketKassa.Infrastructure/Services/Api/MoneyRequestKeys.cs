using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-05 (аудит №4, Л4-07/Л4-08): ключ повтора (Idempotency-Key) по умолчанию для денежных и складских POST, которые идут через
/// <see cref="NurMarketApiClient.RequestAsync"/> без своего ключа (внесения/изъятия, прокат, ручной долг, «из кармана владельца», поставщики, перемещения…).
/// Правило как в <see cref="PendingIdempotencyKey"/>: ключ строится по подписи (метод + путь + тело). Повтор ТОГО ЖЕ запроса после ответа, который не дошёл
/// (таймаут, обрыв, 5xx, 408, 429, 409/423/425), уходит с ТЕМ ЖЕ ключом; успех или отказ по существу — ключ забывается. Пока такой же запрос ещё
/// выполняется, второй такой же не отправляется (защита от повторного нажатия — и там, где сервер ключ ещё не принимает; заголовок для него безвреден).
/// Ключ живёт в памяти процесса <see cref="Lifetime"/>; запросы с собственным ключом (оплата чека, возврат, обмен, PostWithIdempotencyKeyAsync) не затрагиваются.</summary>
internal static class MoneyRequestKeys
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private sealed class Entry
    {
        public string Key = "";
        public DateTime CreatedUtc;
        public bool InFlight;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    // Подстроки пути денежных/складских операций (нижний регистр).
    private static readonly string[] MoneyMarkers =
    {
        "debt", "rental", "cashflow", "bonus", "salary", "supplier", "/pay", "transfer", "incoming", "inventory/sessions",
        "refund", "/return", "exchange", "payment", "deposit", "withdraw", "purchase", "invoice", "write-off", "writeoff", "employee-pay", "/deals/",
    };

    // Не деньги / свои ключи: оплата чека (PosCheckoutService), вход, категории статей.
    private static readonly string[] ExcludedMarkers = { "checkout", "/auth/", "cashflow-categories", "pin/verify" };

    internal static bool Applies(HttpMethod method, string relativePath)
    {
        if (method != HttpMethod.Post || string.IsNullOrWhiteSpace(relativePath))
            return false;
        var p = relativePath.Trim().ToLowerInvariant();
        foreach (var x in ExcludedMarkers)
            if (p.Contains(x, StringComparison.Ordinal))
                return false;
        foreach (var m in MoneyMarkers)
            if (p.Contains(m, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Подпись запроса. null — тело уже содержит собственный idempotency_key (возврат, обмен): ключ не добавляем.</summary>
    internal static string? Signature(HttpMethod method, string relativePath, string? json)
    {
        if (json != null && json.Contains("idempotency_key", StringComparison.OrdinalIgnoreCase))
            return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json ?? "")));
        return method.Method + "|" + relativePath.Trim().ToLowerInvariant().TrimEnd('/') + "|" + hash;
    }

    /// <summary>Начало отправки. false — такой же запрос уже выполняется (повторно не слать). Иначе key — ключ для заголовка.</summary>
    internal static bool TryBegin(string signature, out string key)
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (Entries.Count > 64)
            {
                foreach (var dead in Entries.Where(e => !e.Value.InFlight && now - e.Value.CreatedUtc > Lifetime).Select(e => e.Key).ToList())
                    Entries.Remove(dead);
            }

            if (Entries.TryGetValue(signature, out var e) && now - e.CreatedUtc > Lifetime && !e.InFlight)
                Entries.Remove(signature);
            if (!Entries.TryGetValue(signature, out e))
            {
                e = new Entry { Key = Guid.NewGuid().ToString(), CreatedUtc = now };
                Entries[signature] = e;
            }

            key = e.Key;
            if (e.InFlight)
                return false;
            e.InFlight = true;
            return true;
        }
    }

    internal static void OnSuccess(string signature)
    {
        lock (Sync)
            Entries.Remove(signature);
    }

    /// <summary>Исход неизвестен — ключ остаётся для повтора; отказ по существу — ключ забывается.</summary>
    internal static void OnError(string signature, Exception ex)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(signature, out var e))
                return;
            if (PendingIdempotencyKey.IsOutcomeUnknown(ex))
                e.InFlight = false;
            else
                Entries.Remove(signature);
        }
    }

    internal static ApiException StillRunning() => new(
        Tr.T("Эта операция уже выполняется: сервер ещё не ответил. Дождитесь ответа, повторно не нажимайте.",
            "Бул операция аткарылууда: сервер дагы жооп бере элек. Жоопту күтүңүз, кайра баспаңыз.",
            "This operation is already in progress: the server has not answered yet. Wait for the answer, do not press again.",
            "Bu işlem zaten sürüyor: sunucu henüz yanıt vermedi. Yanıtı bekleyin, tekrar basmayın.",
            "Bu amal allaqachon bajarilmoqda: server hali javob bermadi. Javobni kuting, qayta bosmang."),
        409);
}
