using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Строка чека для проверки «ниже закупки»: <c>Cost</c> — закупочная цена за единицу строки (0 — закупочной цены нет, строку не проверяем).</summary>
public sealed record BelowCostLine(string Title, decimal Cost, decimal Quantity, decimal LineTotal, bool HasDiscount);

/// <summary>Строка, проданная ниже закупки: <c>Loss</c> — на сколько сом.</summary>
public sealed record BelowCostHit(string Title, decimal Loss);

/// <summary>2026-11-13 (сервер bc1112): продажа со скидкой ниже закупочной цены проводится только при праве <c>can_sell_below_cost</c>
/// (<see cref="Core.Contracts.PosPermissions.SellBelowCost"/>). Здесь — чистая проверка чека до отправки (без сети и базы), распознавание отказа сервера
/// и тексты на 5 языках. Сервер сам тоже откажет; касса проверяет заранее, чтобы кассир не взял деньги за продажу, которую очередь потом не проведёт.
/// Принцип расчёта — как у предупреждения «Продажа в убыток»: строка со скидкой (или любая строка при скидке на чек) дешевле закупки × количество.</summary>
public static class BelowCostGuard
{
    /// <summary>Допуск на копейки: убыток меньше этой суммы не считаем (округление цен и скидок).</summary>
    private const decimal Epsilon = 0.005m;

    /// <summary>Строки, которые после скидки (на строку и/или на весь чек) ушли ниже закупки. Закупочной цены нет (Cost ≤ 0) — строка не проверяется.
    /// <paramref name="orderDiscount"/> — скидка на весь чек в сомах (с бонусами); раскладывается по строкам пропорционально их сумме.</summary>
    public static IReadOnlyList<BelowCostHit> Find(IEnumerable<BelowCostLine> lines, decimal orderDiscount)
    {
        var list = lines as IReadOnlyList<BelowCostLine> ?? lines.ToList();
        var gross = list.Sum(l => Math.Max(0m, l.LineTotal));
        var share = gross > 0m ? Math.Clamp(orderDiscount / gross, 0m, 1m) : 0m;
        var hits = new List<BelowCostHit>();
        foreach (var line in list)
        {
            if (line.Cost <= 0m || !(line.HasDiscount || share > 0m))
                continue;
            var net = Math.Max(0m, line.LineTotal) * (1m - share);
            var loss = Math.Round(Math.Max(0m, line.Cost * line.Quantity - net), 2, MidpointRounding.AwayFromZero);
            if (loss > Epsilon)
                hits.Add(new BelowCostHit(line.Title, loss));
        }
        return hits;
    }

    /// <summary>2026-11-13: перечитать профиль прав с сервера (право «Продажа ниже закупки» могли выдать на сайте при открытом чеке; тот же механизм, что при «Недостаточно прав»). true — профиль получен.</summary>
    public static Task<bool> RecheckProfileAsync() => PermissionRecheck.RefreshFromProvidersAsync("продажа ниже закупки", TimeSpan.FromSeconds(6), quiet: true);

    /// <summary>Нужно ли остановить продажу: есть строки ниже закупки и права нет.</summary>
    public static bool Blocks(IReadOnlyList<BelowCostHit> hits, bool canSellBelowCost) => !canSellBelowCost && hits.Count > 0;

    private static string RightName() => Tr.T("Продажа ниже закупки", "Сатып алуудан төмөн сатуу", "Sell below cost", "Alışın altında satış", "Xarid narxidan past sotish");

    private static string List(IReadOnlyList<BelowCostHit> hits) =>
        hits.Count == 0 ? "" : "\n" + string.Join("\n", hits.Take(5).Select(h => $"• {h.Title}"));

    /// <summary>Сообщение кассиру до отправки: что случилось, какое право нужно и что делать. <paramref name="hits"/> может быть пустым (окно скидки: список даёт текст убытка).</summary>
    public static string BlockedText(IReadOnlyList<BelowCostHit> hits) => Tr.T(
        $"Скидка опускает цену ниже закупочной — для такой продажи нужно право «{RightName()}». Уменьшите скидку или попросите старшего (владельца или администратора) оформить продажу.{List(hits)}",
        $"Арзандатуу баанын сатып алуу баасынан төмөн түшүрөт — мындай сатуу үчүн «{RightName()}» укугу керек. Арзандатууну азайтыңыз же улуу кызматкерден (ээси же администратор) сатууну жасоону сураныңыз.{List(hits)}",
        $"The discount drops the price below the purchase price — such a sale needs the “{RightName()}” permission. Reduce the discount or ask a senior (owner or administrator) to make the sale.{List(hits)}",
        $"İndirim fiyatı alış fiyatının altına düşürüyor — bu satış için «{RightName()}» yetkisi gerekir. İndirimi azaltın veya yetkiliden (sahip veya yönetici) satışı yapmasını isteyin.{List(hits)}",
        $"Chegirma narxni xarid narxidan pastga tushiryapti — bunday sotuv uchun «{RightName()}» huquqi kerak. Chegirmani kamaytiring yoki katta xodimdan (egasi yoki administrator) sotuvni rasmiylashtirishni so'rang.{List(hits)}");

    /// <summary>2026-11-13 (ответ Умара 13.11, п. 1): точный код отказа сервера — 400 <c>{"code":"price_below_cost","min_price":"…","required_permission":"can_sell_below_cost","unit_price":"…"}</c>;
    /// в checkout и bulk ошибка лежит в строке, с <c>index</c>.</summary>
    public const string ServerCode = "price_below_cost";

    /// <summary>Что сервер сказал про строку: номер позиции в чеке (с нуля), минимальная цена (<c>min_price</c>). Поля могут отсутствовать.</summary>
    public sealed record BelowCostRefusal(int? Index, decimal? MinPrice);

    /// <summary>Отказ «ниже закупки» (точный код price_below_cost; запасные признаки — can_sell_below_cost / below_cost / «ниже закупочной» в 400/403/422). Продажи на сервере нет — отказ до записи.</summary>
    public static bool IsServerDenial(Exception? ex) => ParseRefusal(ex) is not null;

    /// <summary>Разбор отказа: null — это не отказ «ниже закупки». Строку с кодом price_below_cost ищем в теле и во вложенных списках (errors / lines / items / results), берём её index и min_price.</summary>
    public static BelowCostRefusal? ParseRefusal(Exception? ex)
    {
        if (ex is not ApiException { StatusCode: 400 or 403 or 422 } api)
            return null;
        if (api.Payload is { } payload)
        {
            try
            {
                if (FindRefusalRow(payload, 0) is { } row)
                    return new BelowCostRefusal(IntOf(row, "index"), ServerErrorCodes.Number(row, "min_price"));
            }
            catch (InvalidOperationException)
            {
                // тело не разобрать — идём по запасным признакам ниже
            }
        }
        string raw;
        try
        {
            raw = ((api.Payload is { } p ? p.GetRawText() : "") + " " + api.Message).ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            raw = api.Message.ToLowerInvariant();
        }
        var heuristic = raw.Contains("can_sell_below_cost") || raw.Contains("sell_below_cost") || raw.Contains("below_cost")
                        || raw.Contains("below cost") || raw.Contains("ниже закупочн") || raw.Contains("ниже закупк");
        return heuristic ? new BelowCostRefusal(null, null) : null;
    }

    private static JsonElement? FindRefusalRow(JsonElement el, int depth)
    {
        if (depth > 3)
            return null;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                && string.Equals(c.GetString(), ServerCode, StringComparison.OrdinalIgnoreCase))
                return el;
            foreach (var prop in el.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && FindRefusalRow(prop.Value, depth + 1) is { } found)
                    return found;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (FindRefusalRow(item, depth + 1) is { } found)
                    return found;
        }
        return null;
    }

    private static int? IntOf(JsonElement o, string name) => ServerErrorCodes.Number(o, name) is { } n && n >= 0 && n < 100000 ? (int)n : null;

    /// <summary>Название позиции по номеру строки (index с нуля) в корзине-JSON; null — нет такой строки.</summary>
    public static string? TitleAtIndex(string? cartJson, int? index)
    {
        if (index is not { } i || i < 0 || string.IsNullOrWhiteSpace(cartJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(cartJson);
            var items = CartDisplayHelper.EnumerateItems(doc.RootElement).ToList();
            return i < items.Count ? CartDisplayHelper.ItemName(items[i]) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Строка-уточнение «Позиция «X»: минимальная цена N сом.»; пусто, если сервер не сказал ни позицию, ни цену.</summary>
    public static string LineDetail(BelowCostRefusal? refusal, string? title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var min = refusal?.MinPrice is { } m and > 0m ? MoneyText.Som(m) : null;
        if (name is null && min is null)
            return "";
        return " " + (name, min) switch
        {
            ({ } n, { } mp) => Tr.T($"Позиция «{n}»: минимальная цена {mp}.", $"«{n}» позициясы: эң төмөн баа {mp}.", $"Item “{n}”: minimum price {mp}.", $"«{n}» kalemi: asgari fiyat {mp}.", $"«{n}» pozitsiyasi: eng past narx {mp}."),
            ({ } n, null) => Tr.T($"Позиция «{n}».", $"«{n}» позициясы.", $"Item “{n}”.", $"«{n}» kalemi.", $"«{n}» pozitsiyasi."),
            (null, { } mp) => Tr.T($"Минимальная цена: {mp}.", $"Эң төмөн баа: {mp}.", $"Minimum price: {mp}.", $"Asgari fiyat: {mp}.", $"Eng past narx: {mp}."),
            _ => "",
        };
    }

    /// <summary>Понятный текст при отказе сервера в онлайн-оплате: продажа не проведена, чек остался в кассе. <paramref name="detail"/> — <see cref="LineDetail"/> (позиция и минимальная цена).</summary>
    public static string ServerDeniedText(string detail = "") => Tr.T(
        $"Сервер не принял продажу: цена со скидкой ниже закупочной, а у вас нет права «{RightName()}». Продажа не проведена, чек сохранён.{detail} Уменьшите скидку или попросите владельца (администратора) оформить продажу.",
        $"Сервер сатууну кабыл алган жок: арзандатуудан кийинки баа сатып алуу баасынан төмөн, ал эми сизде «{RightName()}» укугу жок. Сатуу өткөрүлгөн жок, чек сакталды.{detail} Арзандатууну азайтыңыз же ээсинен (администратордон) сатууну жасоону сураныңыз.",
        $"The server did not accept the sale: the discounted price is below the purchase price and you do not have the “{RightName()}” permission. The sale was not recorded, the receipt is kept.{detail} Reduce the discount or ask the owner (administrator) to make the sale.",
        $"Sunucu satışı kabul etmedi: indirimli fiyat alış fiyatının altında ve «{RightName()}» yetkiniz yok. Satış kaydedilmedi, fiş saklandı.{detail} İndirimi azaltın veya sahibinden (yöneticiden) satışı yapmasını isteyin.",
        $"Server sotuvni qabul qilmadi: chegirmadan keyingi narx xarid narxidan past, sizda esa «{RightName()}» huquqi yo'q. Sotuv o'tkazilmadi, chek saqlandi.{detail} Chegirmani kamaytiring yoki egasidan (administratordan) sotuvni rasmiylashtirishni so'rang.");

    /// <summary>Тот же текст по самому отказу и корзине-JSON (позиция по <c>index</c>, минимальная цена).</summary>
    public static string ServerDeniedText(Exception? ex, string? cartJson) =>
        ServerDeniedText(LineDetail(ParseRefusal(ex), TitleAtIndex(cartJson, ParseRefusal(ex)?.Index)));

    /// <summary>Причина для чека из очереди «Неотправленное»: сервер отклонил при досылке — чек и деньги сохранены, решает человек.</summary>
    public static string ReplayRejectedText(string detail = "") => Tr.T(
        $"Требует проверки: сервер не принял чек из очереди — цена со скидкой ниже закупочной, а у кассира нет права «{RightName()}». Чек и деньги сохранены.{detail} Попросите владельца выдать право (сайт app.nurcrm.kg: Сотрудники → Доступы) и нажмите «Повторить отправку» — чек уйдёт тем же ключом, дубля не будет. Или закройте чек кнопкой «Закрыть без отправки». Заново пробивать эту продажу на сайте нельзя без сверки: возможен дубль.",
        $"Текшерүү керек: сервер кезектеги чекти кабыл алган жок — арзандатуудан кийинки баа сатып алуу баасынан төмөн, ал эми кассирде «{RightName()}» укугу жок. Чек жана акча сакталды.{detail} Ээсинен укук берүүнү сураныңыз (app.nurcrm.kg сайты: Кызматкерлер → Укуктар) жана «Жөнөтүүнү кайталоо» басыңыз — чек ошол эле ачкыч менен кетет, кайталанбайт. Же чекти «Жөнөтпөй жабуу» баскычы менен жабыңыз. Бул сатууну сайтта кайра өткөрүүгө салыштырбай болбойт: кайталанып калышы мүмкүн.",
        $"Needs review: the server did not accept the queued receipt — the discounted price is below the purchase price and the cashier does not have the “{RightName()}” permission. The receipt and the money are kept.{detail} Ask the owner to grant the permission (app.nurcrm.kg: Employees → Permissions) and press “Retry sending” — the receipt goes with the same key, no duplicate. Or close the receipt with “Close without sending”. Do not ring this sale up again on the website without checking: it could be duplicated.",
        $"Kontrol gerekli: sunucu kuyruktaki fişi kabul etmedi — indirimli fiyat alış fiyatının altında ve kasiyerde «{RightName()}» yetkisi yok. Fiş ve para saklandı.{detail} Sahibinden yetkiyi vermesini isteyin (app.nurcrm.kg: Personel → Erişimler) ve «Göndermeyi tekrarla»'ya basın — fiş aynı anahtarla gider, çift kayıt olmaz. Veya fişi «Göndermeden kapat» ile kapatın. Bu satışı kontrol etmeden sitede yeniden işlemeyin: çift kayıt olabilir.",
        $"Tekshirish kerak: server navbatdagi chekni qabul qilmadi — chegirmadan keyingi narx xarid narxidan past, kassirda esa «{RightName()}» huquqi yo'q. Chek va pul saqlandi.{detail} Egasidan huquqni berishni so'rang (app.nurcrm.kg: Xodimlar → Huquqlar) va «Yuborishni takrorlash»ni bosing — chek o'sha kalit bilan ketadi, takror bo'lmaydi. Yoki chekni «Yubormasdan yopish» tugmasi bilan yoping. Bu sotuvni tekshirmasdan saytda qayta o'tkazmang: takror bo'lishi mumkin.");

    /// <summary>Причина для чека очереди по самому отказу: позиция берётся по index из корзины чека (<paramref name="cartJson"/>).</summary>
    public static string ReplayRejectedText(Exception? ex, string? cartJson) =>
        ReplayRejectedText(LineDetail(ParseRefusal(ex), TitleAtIndex(cartJson, ParseRefusal(ex)?.Index)));
}
