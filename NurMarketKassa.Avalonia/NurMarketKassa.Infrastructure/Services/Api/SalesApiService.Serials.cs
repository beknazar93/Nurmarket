using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-13 (сервер apl1311+sn1311): серийные номера. Отдельный интерфейс, как <see cref="IPosExchangeApi"/>: заглушки общего <see cref="ISalesApiService"/> в тестах этих методов не знают.</summary>
public interface IPosSerialsApi
{
    /// <summary>POST api/main/pos/sales/{cart}/items/{item}/serials/ с телом <c>{"serial_numbers": [...]}</c> — ЗАМЕНЯЕТ список номеров строки целиком. Ответ: <c>{"id", "serial_numbers"}</c>.</summary>
    Task<JsonElement> PosCartItemSerialsAsync(string cartId, string itemId, IReadOnlyList<string> serials, CancellationToken ct = default);

    /// <summary>GET api/main/serials/?serial=… — где продан номер: продан / возвращён / гарантия / повтор (офлайн-чек). Ищет по связи с продажей, а не по номеру чека.</summary>
    Task<ServerSerialLookup> SerialLookupAsync(string serial, CancellationToken ct = default);
}

public sealed partial class SalesApiService : IPosSerialsApi
{
    /// <summary>Строка <c>items[]</c> возврата: sale_item_id + quantity и (при флаге electronics_2026_11 и непустом списке) serial_numbers — что именно вернули.</summary>
    private static Dictionary<string, object> RefundItem(PosRefundLineRequest l)
    {
        var item = new Dictionary<string, object>
        {
            ["sale_item_id"] = l.LineId.Trim(),
            ["quantity"] = FormatRefundQty(l.Quantity),
        };
        if (l.SerialNumbers is { Count: > 0 } && ServerSerials.Enabled)
            item["serial_numbers"] = ServerSerials.Distinct(l.SerialNumbers).Take(ServerSerials.MaxPerLine).ToList();
        return item;
    }

    public Task<JsonElement> PosCartItemSerialsAsync(string cartId, string itemId, IReadOnlyList<string> serials, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        var body = new Dictionary<string, object> { ["serial_numbers"] = ServerSerials.Distinct(serials).Take(ServerSerials.MaxPerLine).ToList() };
        return _client.RequestAsync(HttpMethod.Post, $"api/main/pos/sales/{c}/items/{i}/serials/", body, null, ct, TimeSpan.FromSeconds(20));
    }

    public async Task<ServerSerialLookup> SerialLookupAsync(string serial, CancellationToken ct = default)
    {
        var text = (serial ?? "").Trim();
        if (text.Length == 0)
            return new ServerSerialLookup { Serial = "" };
        var d = await _client.RequestAsync(HttpMethod.Get, "api/main/serials/", null,
            new Dictionary<string, string> { ["serial"] = text }, ct, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        return ServerSerials.ParseLookup(d, text);
    }
}
