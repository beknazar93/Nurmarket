using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (ответ Умара 19.11, п. 1): DELETE прихода возвращает закупочную цену товара (проверено сервером: 1 шт по 1 сом → после удаления 35 и в карточке, и в products/list).
/// В ответе — <c>deltas.purchase_price</c> {before, after, applied}: объект (одна цена) или массив/словарь таких объектов по товарам. Здесь — разбор и короткое сообщение владельцу
/// «Закупочная цена возвращена: было X → стало Y». Разбор терпим к любой форме: нет поля — пусто.</summary>
public sealed record PurchasePriceChange(string? Name, decimal? Before, decimal? After, bool Applied);

public static class PurchasePriceDelta
{
    public static IReadOnlyList<PurchasePriceChange> Parse(JsonElement response)
    {
        var list = new List<PurchasePriceChange>();
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("deltas", out var deltas) || deltas.ValueKind != JsonValueKind.Object
            || !deltas.TryGetProperty("purchase_price", out var pp))
            return list;
        switch (pp.ValueKind)
        {
            case JsonValueKind.Object when IsChange(pp):
                list.Add(Read(pp, null));
                break;
            case JsonValueKind.Object: // словарь «товар → {before, after, applied}»
                foreach (var p in pp.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && IsChange(p.Value))
                        list.Add(Read(p.Value, p.Name));
                break;
            case JsonValueKind.Array:
                foreach (var x in pp.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Object && IsChange(x))
                        list.Add(Read(x, null));
                break;
        }
        return list;
    }

    private static bool IsChange(JsonElement o) => o.TryGetProperty("before", out _) || o.TryGetProperty("after", out _);

    private static PurchasePriceChange Read(JsonElement o, string? key)
    {
        string? name = null;
        foreach (var n in new[] { "name", "product_name", "title" })
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                name = v.GetString();
                break;
            }
        // 2026-11-19 (ответ Умара 19.11, п. 7): массив [{product_id, name, before, after, proposed, applied, latest}]; нет name — название по product_id из каталога
        if (name is null && o.TryGetProperty("product_id", out var pid) && (pid.ValueKind == JsonValueKind.String ? pid.GetString() : pid.ValueKind == JsonValueKind.Number ? pid.GetRawText() : null) is { Length: > 0 } pidText)
        {
            try { name = CatalogCacheService.FindProductById(pidText)?.Title; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* каталог не готов — без названия */ }
        }
        name ??= key;
        var before = ServerErrorCodes.Number(o, "before");
        var after = ServerErrorCodes.Number(o, "after");
        // applied: нет поля — считаем применённым, если цена действительно изменилась
        var applied = o.TryGetProperty("applied", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? a.ValueKind == JsonValueKind.True
            : before != after;
        return new PurchasePriceChange(name, before, after, applied);
    }

    /// <summary>Сообщение владельцу; null — цена не менялась (или сервер ничего не сказал).</summary>
    public static string? Message(IReadOnlyList<PurchasePriceChange> changes)
    {
        var shown = changes.Where(c => c.Applied && c.Before is not null && c.After is not null && c.Before != c.After).ToList();
        if (shown.Count == 0)
            return null;
        string One(PurchasePriceChange c)
        {
            var was = MoneyText.Som(c.Before!.Value);
            var now = MoneyText.Som(c.After!.Value);
            return Tr.T($"было {was} → стало {now}", $"болчу {was} → болду {now}", $"was {was} → now {now}", $"önceki {was} → şimdi {now}", $"edi {was} → bo'ldi {now}");
        }
        var head = Tr.T("Закупочная цена возвращена", "Сатып алуу баасы кайтарылды", "The purchase price was restored", "Alış fiyatı geri alındı", "Xarid narxi qaytarildi");
        if (shown.Count == 1 && string.IsNullOrWhiteSpace(shown[0].Name))
            return head + ": " + One(shown[0]) + ".";
        // 2026-11-19: по строке на товар, до 3 строк и «+N»
        var rows = string.Join("\n", shown.Take(3).Select(c => (string.IsNullOrWhiteSpace(c.Name) ? "" : "«" + c.Name + "»: ") + One(c)));
        return head + ":\n" + rows + (shown.Count > 3 ? "\n+" + (shown.Count - 3) : "");
    }

    /// <summary>Вызывается после успешного DELETE прихода: сообщение в журнал и владельцу (через <see cref="Notifier"/>), каталог — на перечитывание (цена в карточке и кэше).
    /// Не бросает: на удаление это не влияет.</summary>
    public static void Handle(string receiptId, JsonElement response)
    {
        try
        {
            var message = Message(Parse(response));
            if (message is null)
                return;
            PosLogger.Log($"Закупки: приход {receiptId} удалён — {message}", "INFO");
            ProductActions.RequestCatalogRefresh?.Invoke(); // карточка и кэш цены: каталог перечитывается с сервера (цена закупки вернулась)
            Notifier?.Invoke(message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Закупки: сообщение о возврате закупочной цены не показано ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Показ сообщения владельцу (плашка). Задаётся при старте программы (App.axaml.cs); в тестах не задана.</summary>
    public static Action<string>? Notifier { get; set; }
}
