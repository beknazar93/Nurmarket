namespace NurMarketKassa.Services;

/// <summary>Сообщения об ошибках POS для отображения кассиру на русском.</summary>
public static class PosErrorMessages
{
    /// <summary>Ошибка добавления товара в чек или сканирования штрихкода.</summary>
    public static string UserMessageForCatalogOrScan(ApiException ex)
    {
        var sc = ex.StatusCode;
        var msg = (ex.Message ?? "").Trim();
        var low = msg.ToLowerInvariant();

        if (sc == 404)
            return "Товар не найден.";

        if (low.Contains("not found", StringComparison.Ordinal) ||
            low.Contains("does not exist", StringComparison.Ordinal))
            return "Товар не найден.";

        if (low.Contains("unknown") && low.Contains("product"))
            return "Товар не найден.";

        return msg;
    }
}
