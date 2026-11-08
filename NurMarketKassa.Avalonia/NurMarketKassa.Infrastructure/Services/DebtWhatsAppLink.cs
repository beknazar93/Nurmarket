using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-08, владелец: «Нужно вернуть кнопку WA (отправка долга на WhatsApp)». Чистая логика кнопки «WA» у должника:
/// номер → международные цифры для wa.me, готовый текст напоминания на языке клиента (ru / ky / en / tr / uz), ссылка
/// <c>https://wa.me/&lt;цифры&gt;?text=…</c>. Ничего не отправляет: ссылка только открывает чат, «Отправить» нажимает человек.</summary>
public static class DebtWhatsAppLink
{
    public static readonly string[] Languages = { "ru", "ky", "en", "tr", "uz" };

    /// <summary>Данные для текста: Shop — название магазина (пусто — «наш магазин»), Since — с какого числа долг, DueDate — срок возврата (если известен),
    /// Requisites — реквизиты оплаты (если заданы; сейчас в настройках кассы такого поля нет — null).</summary>
    public sealed record Info(string Name, double Amount, string? Shop, DateTime? Since = null, DateTime? DueDate = null, string? Requisites = null);

    /// <summary>Международные цифры номера для wa.me: «+996 700 123 456», «0700123456», «700123456», «+9960700…», «00996700…» → «996700123456».
    /// Номер другой страны (с «+» или «00», 10–15 цифр) остаётся как есть. Нет номера / мусор — null.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;
        var trimmed = phone.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        var international = trimmed.StartsWith('+') || digits.StartsWith("00", StringComparison.Ordinal);
        if (digits.StartsWith("00", StringComparison.Ordinal))
            digits = digits[2..];
        if (ClientQrCode.NationalDigits(digits) is { } national)
            return "996" + national;
        // Чужой номер: только если он явно записан международным («+7…», «0090…») и длина разумная.
        return international && digits.Length is >= 10 and <= 15 && digits[0] != '0' ? digits : null;
    }

    /// <summary>Код языка клиента из карточки (ru/ky/en/tr/uz, допускаются «kg», «kyrgyz»…); пусто или неизвестный — <paramref name="fallback"/>.</summary>
    public static string NormalizeLanguage(string? lang, string fallback = "ru")
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        if (l.Length >= 2)
            l = l[..2] == "kg" ? "ky" : l[..2];
        return Languages.Contains(l) ? l : fallback;
    }

    /// <summary>Язык интерфейса кассы кодом (для клиента без языка в карточке).</summary>
    public static string AppLanguageCode() => UserPreferences.Instance.Language switch
    {
        AppLanguage.Kyrgyz => "ky",
        AppLanguage.English => "en",
        AppLanguage.Turkish => "tr",
        AppLanguage.Uzbek => "uz",
        _ => "ru",
    };

    /// <summary>У клиента явный отказ: сервер хранит согласия с датой и источником изменения; оба выключены при заполненной дате — клиент (или кассир) их
    /// выключал. У прежних клиентов оба false без даты — это «не спрашивали», а не отказ.</summary>
    public static bool IsExplicitRefusal(bool service, bool marketing, bool hasUpdatedAt) => !service && !marketing && hasUpdatedAt;

    /// <summary>Готовый текст напоминания на языке <paramref name="lang"/>: сумма, магазин, «с какого числа», срок и реквизиты — если есть.</summary>
    public static string BuildText(Info info, string lang)
    {
        lang = NormalizeLanguage(lang);
        var name = (info.Name ?? "").Trim();
        var shop = (info.Shop ?? "").Trim();
        var amount = FormatAmount(info.Amount);
        var since = info.Since?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var due = info.DueDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var req = (info.Requisites ?? "").Trim();

        string hello, body, sinceText, dueText, reqText, thanks;
        switch (lang)
        {
            case "ky":
                hello = name.Length > 0 ? $"Саламатсызбы, {name}!" : "Саламатсызбы!";
                body = shop.Length > 0 ? $"«{shop}» дүкөнүндөгү карызыңызды эскертебиз: {amount} сом." : $"Дүкөнүбүздөгү карызыңызды эскертебиз: {amount} сом.";
                sinceText = since is null ? "" : $" Карыз {since} баштап.";
                dueText = due is null ? "" : $" Төлөө мөөнөтү — {due}.";
                reqText = req.Length > 0 ? $" Төлөө реквизиттери: {req}." : "";
                thanks = " Рахмат!";
                break;
            case "en":
                hello = name.Length > 0 ? $"Hello, {name}!" : "Hello!";
                body = shop.Length > 0 ? $"A reminder about your debt at “{shop}”: {amount} som." : $"A reminder about your debt at our shop: {amount} som.";
                sinceText = since is null ? "" : $" Owed since {since}.";
                dueText = due is null ? "" : $" Pay by {due}.";
                reqText = req.Length > 0 ? $" Payment details: {req}." : "";
                thanks = " Thank you!";
                break;
            case "tr":
                hello = name.Length > 0 ? $"Merhaba {name}!" : "Merhaba!";
                body = shop.Length > 0 ? $"«{shop}» mağazasındaki borcunuzu hatırlatırız: {amount} som." : $"Mağazamızdaki borcunuzu hatırlatırız: {amount} som.";
                sinceText = since is null ? "" : $" Borç {since} tarihinden beri.";
                dueText = due is null ? "" : $" Son ödeme tarihi — {due}.";
                reqText = req.Length > 0 ? $" Ödeme bilgileri: {req}." : "";
                thanks = " Teşekkürler!";
                break;
            case "uz":
                hello = name.Length > 0 ? $"Assalomu alaykum, {name}!" : "Assalomu alaykum!";
                body = shop.Length > 0 ? $"«{shop}» do'konidagi qarzingizni eslatamiz: {amount} so'm." : $"Do'konimizdagi qarzingizni eslatamiz: {amount} so'm.";
                sinceText = since is null ? "" : $" Qarz {since} dan beri.";
                dueText = due is null ? "" : $" To'lash muddati — {due}.";
                reqText = req.Length > 0 ? $" To'lov rekvizitlari: {req}." : "";
                thanks = " Rahmat!";
                break;
            default:
                hello = name.Length > 0 ? $"Здравствуйте, {name}!" : "Здравствуйте!";
                body = shop.Length > 0 ? $"Напоминаем о долге в магазине «{shop}»: {amount} сом." : $"Напоминаем о долге в нашем магазине: {amount} сом.";
                sinceText = since is null ? "" : $" Долг с {since}.";
                dueText = due is null ? "" : $" Срок оплаты — {due}.";
                reqText = req.Length > 0 ? $" Реквизиты для оплаты: {req}." : "";
                thanks = " Спасибо!";
                break;
        }
        return hello + " " + body + sinceText + dueText + reqText + thanks;
    }

    /// <summary>«12 345» или «12 345,50» (пробел-разделитель, без неразрывного пробела — чтобы в чате не было «кракозябр»).</summary>
    public static string FormatAmount(double amount)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var text = Math.Abs(amount - Math.Round(amount)) < 0.005 ? Math.Round(amount).ToString("N0", ru) : amount.ToString("N2", ru);
        return text.Replace(' ', ' ').Replace(' ', ' ');
    }

    /// <summary>Ссылка чата WhatsApp с готовым текстом. Нет номера (или он не номер) — null.</summary>
    public static string? BuildUrl(string? phone, string? text)
    {
        if (NormalizePhone(phone) is not { } digits)
            return null;
        return string.IsNullOrWhiteSpace(text) ? "https://wa.me/" + digits : "https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(text);
    }
}
