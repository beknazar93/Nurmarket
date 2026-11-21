using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (ответ Умара 20.11): внутренние штрихкоды выдаёт сервер — POST main/barcodes/next/ {count: 1..100} → {"barcodes": [...]}. Коды начинаются на 1 или 3–9 (не на 2 и не на 0),
/// без префикса 470, свободны в компании. Сервер их НЕ резервирует: код может занять кто-то другой раньше, чем его сохранят — при ответе 409 barcode_duplicate берётся следующий.
/// Сервер недоступен (нет связи, старый сервер без адреса, 403, нестандартный ответ) — <see cref="NextAsync"/> даёт null, и вызывающий берёт запасной локальный <see cref="BarcodeGenerator"/>.</summary>
public static class ServerBarcodes
{
    public const int MaxCount = 100;

    /// <summary>Только для тестов и стенда: подмена запроса (количество → коды; null — «сервер недоступен»).</summary>
    public static Func<int, CancellationToken, Task<IReadOnlyList<string>?>>? NextOverride { get; set; }

    private static DateTime _unsupportedUntilUtc = DateTime.MinValue;

    /// <summary>Только для тестов: забыть «адреса нет».</summary>
    internal static void ResetForTests() => _unsupportedUntilUtc = DateTime.MinValue;

    /// <summary>Запросить <paramref name="count"/> (1..100) кодов. null — сервер недоступен или ответил не так; список мог оказаться короче запрошенного.</summary>
    public static async Task<IReadOnlyList<string>?> NextAsync(int count, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, MaxCount);
        if (NextOverride is { } over)
            return await over(count, ct).ConfigureAwait(false);
        if ((ServerAddons.ApiProvider ?? ServerLoyalty.ApiProvider)?.Invoke() is not { } api || string.IsNullOrEmpty(api.AccessToken))
            return null;
        return await NextAsync(api, count, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<string>?> NextAsync(NurMarketApiClient api, int count, CancellationToken ct)
    {
        if (DateTime.UtcNow < _unsupportedUntilUtc)
            return null;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Post, "api/main/barcodes/next/", new Dictionary<string, object?> { ["count"] = Math.Clamp(count, 1, MaxCount) }, null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return ParseCodes(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 405 or 501)
        {
            _unsupportedUntilUtc = DateTime.UtcNow.AddMinutes(10); // сервер без адреса — не спрашиваем минут десять
            PosLogger.LogThrottled("barcodes-next-unsupported", "Штрихкоды: на сервере нет адреса main/barcodes/next/ — берём коды у кассы.", "WARNING");
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.LogThrottled("barcodes-next-" + ex.GetType().Name, $"Штрихкоды: сервер не выдал коды ({(ex is ApiException a ? "HTTP " + a.StatusCode : ex.GetType().Name)}) — берём коды у кассы.", "WARNING");
            return null;
        }
    }

    /// <summary>Разбор ответа {"barcodes": [...]} (internal — для тестов): только непустые строки; не тот вид ответа — null.</summary>
    internal static IReadOnlyList<string>? ParseCodes(JsonElement root)
    {
        var arr = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("barcodes", out var b) && b.ValueKind == JsonValueKind.Array ? b
            : default;
        if (arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var x in arr.EnumerateArray())
        {
            var s = x.ValueKind switch { JsonValueKind.String => x.GetString(), JsonValueKind.Number => x.GetRawText(), _ => null };
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>Код с сервера годится: непустой, только цифры, не весовой префикс «2…» (касса читает его как весовой) и не «0…».</summary>
    internal static bool LooksUsable(string code) =>
        code.Length is >= 8 and <= 64 && code.All(char.IsAsciiDigit) && code[0] is not ('0' or '2');
}
