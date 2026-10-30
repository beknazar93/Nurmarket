using System.Globalization;

namespace NurMarketKassa.Services;

public enum OfflineDebtVerdictKind
{
    Allow,
    /// <summary>Разрешено только после явного подтверждения кассира (лимит неизвестен, данные старые).</summary>
    NeedConfirm,
    Block,
}

/// <summary>Итог проверки операции «в долг» / оплаты долга без связи. Message — для кассира; DataNote — «данные клиента от ЧЧ:ММ».</summary>
public sealed record OfflineDebtVerdict(OfflineDebtVerdictKind Kind, string Message, string DataNote);

/// <summary>2026-10-29: правила «в долг» и оплаты долга без связи — чистая логика без файлов и сети (её же гонит мини-прогон).
/// Сервер — истина: касса проверяет по кэшу клиентов (OfflineClientCache) и очередям; при досылке сервер может отказать (лимит, клиент удалён) —
/// тогда чек уходит в «Требует проверки», а не пропадает.</summary>
public static class OfflineDebtRules
{
    /// <summary>Данные клиента старше этого срока — «в долг» только с подтверждением кассира.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>«данные клиента от 14:35» (с датой, если не сегодня). at = null — «данных нет».</summary>
    public static string DataStamp(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t)
            return Tr.T("данных о долге клиента у кассы нет", "кассада кардардын карызы жөнүндө маалымат жок", "the till has no data on the client's debt",
                "kasada müşterinin borcu hakkında veri yok", "kassada mijoz qarzi haqida ma'lumot yo'q");
        var local = t.ToLocalTime();
        var text = local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        return Tr.T($"данные клиента от {text}", $"кардардын маалыматы {text} абалында", $"client data as of {text}",
            $"müşteri verisi {text} itibarıyla", $"mijoz ma'lumoti {text} holatida");
    }

    /// <summary>Проверка продажи «в долг» без связи.</summary>
    /// <param name="newDebt">Сколько из чека уходит в долг (итог минус предоплата).</param>
    /// <param name="knownDebt">Долг клиента: сводка сервера + неподтверждённое в очереди; null — сводки долгов касса не получала.</param>
    /// <param name="maxOfflineSetting">Настройка «Макс. долг без связи» на клиента, сом; 0 — по лимиту клиента.</param>
    /// <param name="confirmedByCashier">Кассир уже подтвердил продажу при предупреждении.</param>
    public static OfflineDebtVerdict CheckSale(OfflineClientRecord? client, decimal newDebt, decimal? knownDebt, decimal maxOfflineSetting,
        bool confirmedByCashier, DateTimeOffset now)
    {
        if (client is null)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T("Клиент не найден в списке клиентов, сохранённом кассой. «В долг» без связи — только клиенту, которого касса видела на сервере: выберите клиента из списка или дождитесь связи.",
                    "Клиент кассада сакталган клиенттер тизмесинде жок. Байланышсыз «карызга» — кассанын серверде көргөн клиентине гана: тизмеден тандаңыз же байланышты күтүңүз.",
                    "The client is not in the client list saved by the till. “On credit” without a connection is only for a client the till has seen on the server: pick one from the list or wait for the connection.",
                    "Müşteri kasanın kaydettiği müşteri listesinde yok. Bağlantısız «veresiye» yalnızca kasanın sunucuda gördüğü müşteriye yapılır: listeden seçin veya bağlantıyı bekleyin.",
                    "Mijoz kassa saqlagan mijozlar ro'yxatida yo'q. Aloqasiz «qarzga» faqat kassa serverda ko'rgan mijozga: ro'yxatdan tanlang yoki aloqani kuting."), "");

        var stamp = DataStamp(client.DebtAt ?? client.UpdatedAt, now);
        if (client.Blocked)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T($"Клиент «{client.Name}» заблокирован ({stamp}). «В долг» ему не оформляется.",
                    $"«{client.Name}» кардары бөгөттөлгөн ({stamp}). Ага «карызга» жазылбайт.",
                    $"The client “{client.Name}” is blocked ({stamp}). No credit sale can be made to them.",
                    $"«{client.Name}» müşterisi engelli ({stamp}). Ona veresiye satış yapılmaz.",
                    $"«{client.Name}» mijozi bloklangan ({stamp}). Unga «qarzga» rasmiylashtirilmaydi."), stamp);

        var caps = new List<decimal>();
        if (maxOfflineSetting > 0m)
            caps.Add(maxOfflineSetting);
        if (client.DebtLimit is > 0m)
            caps.Add(client.DebtLimit.Value);
        decimal? cap = caps.Count > 0 ? caps.Min() : null;

        if (cap is { } limit && knownDebt is { } debt && debt + newDebt > limit + 0.005m)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T($"Лимит долга без связи превышен ({stamp}): долг клиента {MoneyText.Som(debt)} + этот чек {MoneyText.Som(newDebt)} больше лимита {MoneyText.Som(limit)}. Оформите чек частично наличными/картой или дождитесь связи.",
                    $"Байланышсыз карыз лимити ашты ({stamp}): кардардын карызы {MoneyText.Som(debt)} + бул чек {MoneyText.Som(newDebt)} лимиттен {MoneyText.Som(limit)} көп. Чекти жарым накталай/карта менен жасаңыз же байланышты күтүңүз.",
                    $"The offline debt limit is exceeded ({stamp}): client debt {MoneyText.Som(debt)} + this receipt {MoneyText.Som(newDebt)} is above the limit {MoneyText.Som(limit)}. Take part in cash/card or wait for the connection.",
                    $"Bağlantısız borç limiti aşıldı ({stamp}): müşteri borcu {MoneyText.Som(debt)} + bu fiş {MoneyText.Som(newDebt)}, limit {MoneyText.Som(limit)} üzerinde. Kısmen nakit/kartla alın veya bağlantıyı bekleyin.",
                    $"Aloqasiz qarz limiti oshdi ({stamp}): mijoz qarzi {MoneyText.Som(debt)} + shu chek {MoneyText.Som(newDebt)} limitdan {MoneyText.Som(limit)} ko'p. Qisman naqd/karta bilan oling yoki aloqani kuting."), stamp);

        var warnings = new List<string>();
        if (cap is null)
            warnings.Add(Tr.T("лимит долга клиента не задан и «Макс. долг без связи» не указан", "кардардын карыз лимити коюлган эмес жана «Байланышсыз макс. карыз» көрсөтүлгөн эмес",
                "the client's debt limit is not set and “Max debt without connection” is not specified", "müşterinin borç limiti belirlenmemiş ve «Bağlantısız maks. borç» girilmemiş",
                "mijozning qarz limiti belgilanmagan va «Aloqasiz maks. qarz» ko'rsatilmagan"));
        if (knownDebt is null)
            warnings.Add(Tr.T("текущий долг клиента кассе неизвестен", "кардардын азыркы карызы кассага белгисиз", "the client's current debt is unknown to the till",
                "müşterinin mevcut borcu kasaya bilinmiyor", "mijozning joriy qarzi kassaga noma'lum"));
        if ((client.DebtAt ?? client.UpdatedAt) is var at && now - at > StaleAfter)
            warnings.Add(Tr.T("данные клиента старше суток", "кардардын маалыматы бир суткадан эски", "the client's data is over a day old",
                "müşteri verisi bir günden eski", "mijoz ma'lumoti bir kundan eski"));

        if (warnings.Count == 0 || confirmedByCashier)
            return new(OfflineDebtVerdictKind.Allow, "", stamp);

        return new(OfflineDebtVerdictKind.NeedConfirm,
            Tr.T($"Оформить «в долг» без связи на {MoneyText.Som(newDebt)} клиенту «{client.Name}»? Проверка неполная ({stamp}): {string.Join("; ", warnings)}. Сервер проверит чек при отправке и может отказать — тогда чек будет ждать проверки владельцем.",
                $"«{client.Name}» кардарына байланышсыз {MoneyText.Som(newDebt)} «карызга» жазасызбы? Текшерүү толук эмес ({stamp}): {string.Join("; ", warnings)}. Сервер жөнөтүүдө чекти текшерет жана баш тартышы мүмкүн — анда чек ээсинин текшерүүсүн күтөт.",
                $"Make a credit sale of {MoneyText.Som(newDebt)} to “{client.Name}” without a connection? The check is incomplete ({stamp}): {string.Join("; ", warnings)}. The server will check the receipt when sending and may refuse — then the receipt waits for the owner's review.",
                $"«{client.Name}» müşterisine bağlantısız {MoneyText.Som(newDebt)} veresiye satış yapılsın mı? Kontrol eksik ({stamp}): {string.Join("; ", warnings)}. Sunucu gönderimde fişi kontrol eder ve reddedebilir — o zaman fiş sahibin kontrolünü bekler.",
                $"«{client.Name}» mijoziga aloqasiz {MoneyText.Som(newDebt)} «qarzga» rasmiylashtirilsinmi? Tekshiruv to'liq emas ({stamp}): {string.Join("; ", warnings)}. Server yuborishda chekni tekshiradi va rad etishi mumkin — u holda chek egasining tekshiruvini kutadi."), stamp);
    }

    /// <summary>Проверка оплаты долга без связи. knownDebt — долг клиента по кэшу МИНУС оплаты в очереди (нижняя оценка, без отклонённых чеков).</summary>
    public static OfflineDebtVerdict CheckPayment(OfflineClientRecord? client, decimal amount, decimal? knownDebt, DateTimeOffset now)
    {
        if (client is null)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T("Клиент не найден в списке клиентов, сохранённом кассой. Оплату долга без связи можно принять у клиента, которого касса видела на сервере.",
                    "Клиент кассада сакталган клиенттер тизмесинде жок. Байланышсыз карыз төлөмүн кассанын серверде көргөн клиентинен гана алууга болот.",
                    "The client is not in the client list saved by the till. A debt payment without a connection can be taken from a client the till has seen on the server.",
                    "Müşteri kasanın kaydettiği müşteri listesinde yok. Bağlantısız borç ödemesi yalnızca kasanın sunucuda gördüğü müşteriden alınabilir.",
                    "Mijoz kassa saqlagan mijozlar ro'yxatida yo'q. Aloqasiz qarz to'lovini faqat kassa serverda ko'rgan mijozdan qabul qilish mumkin."), "");

        var stamp = DataStamp(client.DebtAt, now);
        if (amount <= 0.005m)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T("Сумма оплаты — число больше нуля.", "Төлөм суммасы — нөлдөн чоң сан.", "The payment amount must be a number above zero.", "Ödeme tutarı sıfırdan büyük bir sayı olmalı.", "To'lov summasi — noldan katta son."), stamp);
        if (knownDebt is not { } debt)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T($"Долг клиента «{client.Name}» кассе неизвестен ({stamp}) — сумму оплаты без связи проверить нельзя. Примите оплату, когда появится связь.",
                    $"«{client.Name}» кардарынын карызы кассага белгисиз ({stamp}) — байланышсыз төлөм суммасын текшерүүгө болбойт. Байланыш пайда болгондо төлөмдү алыңыз.",
                    $"The debt of “{client.Name}” is unknown to the till ({stamp}) — the payment amount cannot be checked without a connection. Take the payment once the connection is back.",
                    $"«{client.Name}» müşterisinin borcu kasaya bilinmiyor ({stamp}) — tutar bağlantısız doğrulanamaz. Ödemeyi bağlantı gelince alın.",
                    $"«{client.Name}» mijozining qarzi kassaga noma'lum ({stamp}) — to'lov summasini aloqasiz tekshirib bo'lmaydi. To'lovni aloqa tiklanganda qabul qiling."), stamp);
        if (amount > debt + 0.005m)
            return new(OfflineDebtVerdictKind.Block,
                Tr.T($"Сумма больше известного долга клиента: {MoneyText.Som(amount)} при долге {MoneyText.Som(Math.Max(0m, debt))} ({stamp}, с учётом оплат в очереди). Больше долга без связи принять нельзя.",
                    $"Сумма кардардын белгилүү карызынан көп: {MoneyText.Som(amount)}, карыз {MoneyText.Som(Math.Max(0m, debt))} ({stamp}, кезектеги төлөмдөрдү эске алуу менен). Байланышсыз карыздан ашык алууга болбойт.",
                    $"The amount is above the client's known debt: {MoneyText.Som(amount)} against a debt of {MoneyText.Som(Math.Max(0m, debt))} ({stamp}, queued payments included). More than the debt cannot be taken without a connection.",
                    $"Tutar müşterinin bilinen borcundan fazla: {MoneyText.Som(amount)}, borç {MoneyText.Som(Math.Max(0m, debt))} ({stamp}, sıradaki ödemeler dahil). Bağlantısız borçtan fazlası alınamaz.",
                    $"Summa mijozning ma'lum qarzidan ko'p: {MoneyText.Som(amount)}, qarz {MoneyText.Som(Math.Max(0m, debt))} ({stamp}, navbatdagi to'lovlar hisobga olingan). Aloqasiz qarzdan ortiq qabul qilib bo'lmaydi."), stamp);

        return new(OfflineDebtVerdictKind.Allow, "", stamp);
    }

    /// <summary>Слово для кассира: операция сделана без связи и ждёт отправки.</summary>
    public static string QueuedNote => Tr.T("оформлено без связи, ждёт отправки", "байланышсыз жасалды, жөнөтүүнү күтөт", "made without a connection, waiting to be sent",
        "bağlantısız yapıldı, gönderilmeyi bekliyor", "aloqasiz rasmiylashtirildi, yuborilishini kutmoqda");
}
