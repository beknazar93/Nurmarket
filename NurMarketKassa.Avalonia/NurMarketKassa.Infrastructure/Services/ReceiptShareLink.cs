using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (владелец: «при скане QR на чеке должен открываться красивый чек, а не текст»): публичная ссылка на страницу чека
/// (share_url сервера, https://app.nurcrm.kg/r/&lt;токен&gt;) и её путь до принтера. Текст чека проходит через общий конвейер печати
/// (верхний регистр, переносы), поэтому ссылка едет в начале текста служебной строкой <see cref="Marker"/> и вынимается в
/// <see cref="ReceiptPrintService.PrintReceipt"/> перед печатью. Фискальный QR eKassa (<c>QR&lt;TAB&gt;ссылка</c>) этим не затрагивается.</summary>
public static class ReceiptShareLink
{
    internal const string Marker = "\u0001QRURL ";

    private const int MaxUrlLength = 300;

    /// <summary>Сервер ответил 404/405 на share-link — до перезапуска больше не спрашиваем (старый сервер без этой функции).</summary>
    private static volatile bool _serverLacksLink;

    /// <summary>Только полноценная https-ссылка в ASCII: короткий QR читается любым принтером, чужие схемы и мусор в QR не попадают.</summary>
    public static bool IsPrintable(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= MaxUrlLength
        && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && url.All(c => c > 32 && c < 127);

    /// <summary>Приложить ссылку к тексту чека (если она годится и печать QR включена); иначе текст как был.</summary>
    public static string Attach(string text, string? url, bool? enabled = null)
    {
        if (!(enabled ?? UserPreferences.Instance.PrintReceiptLinkQr) || !IsPrintable(url) || text.StartsWith(Marker, StringComparison.Ordinal))
            return text;
        return Marker + url!.Trim() + "\n" + text;
    }

    /// <summary>Вынуть ссылку из начала текста: возвращает текст без служебной строки, ссылка — в <paramref name="url"/> (или null).</summary>
    public static string Split(string text, out string? url)
    {
        url = null;
        if (!text.StartsWith(Marker, StringComparison.Ordinal))
            return text;
        var end = text.IndexOf('\n');
        if (end < 0)
            return text[Marker.Length..];
        var candidate = text.Substring(Marker.Length, end - Marker.Length).Trim();
        url = IsPrintable(candidate) ? candidate : null;
        return text[(end + 1)..];
    }

    /// <summary>share_url из ответа сервера (checkout, pos/sales/{id}); null — ссылки нет.</summary>
    public static string? FromSale(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return null;
        if (sale.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && FromSale(data) is { } inner)
            return inner;
        return sale.TryGetProperty("share_url", out var v) && v.ValueKind == JsonValueKind.String && IsPrintable(v.GetString()) ? v.GetString()!.Trim() : null;
    }

    /// <summary>Ссылка на чек продажи: из ответа, если она там есть, иначе POST share-link (повторный вызов отдаёт ту же ссылку). Любой сбой — null:
    /// чек печатается без QR, продажа не страдает.</summary>
    public static async Task<string?> EnsureAsync(ISalesApiService? api, string? saleId, JsonElement? known, CancellationToken ct = default)
    {
        try
        {
            if (known is { } k && FromSale(k) is { } have)
                return have;
            if (_serverLacksLink || string.IsNullOrWhiteSpace(saleId) || api is not SalesApiService sales)
                return null;
            var answer = await sales.PosSaleShareLinkAsync(saleId!, ct).ConfigureAwait(false);
            if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && IsPrintable(u.GetString()))
                return u.GetString()!.Trim();
            return null;
        }
        catch (ApiException e) when (e.StatusCode is 404 or 405)
        {
            _serverLacksLink = true;
            PosLogger.Log($"Ссылка на чек: сервер ответил {e.StatusCode} — QR со ссылкой не печатаем до перезапуска.", "PRINTER");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Ссылка на чек не получена: {ex.Message} — чек без QR.", "PRINTER");
            return null;
        }
    }
}
