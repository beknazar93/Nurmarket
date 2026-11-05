using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (сервер ceba67c): машинные коды ошибок NurCRM (поле <c>code</c> в теле ответа) и понятные кассиру тексты на 5 языках — duplicate_line (две строки одного товара
/// с разной ценой/единицей в чеке), negative_stock / has_returns / debt_already_paid (удаление прихода), version_required (428), read_only (403, нет права править товары),
/// pin_invalid / pin_locked (PIN владельца). Тексты сервера на русском; здесь — на языке кассы.</summary>
public static class ServerErrorCodes
{
    public const string DuplicateLine = "duplicate_line";
    public const string NegativeStock = "negative_stock";
    public const string HasReturns = "has_returns";
    public const string DebtAlreadyPaid = "debt_already_paid";
    public const string VersionRequired = "version_required";
    public const string ReadOnly = "read_only";
    public const string PinInvalid = "pin_invalid";
    public const string PinLocked = "pin_locked";

    /// <summary>Код из тела ответа (<c>code</c>, иначе <c>error</c>), строчными; null — нет.</summary>
    public static string? Code(Exception? ex) => ex is ApiException { Payload: { } p } ? Code(p) : null;

    public static string? Code(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "code", "error" })
            if (payload.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s)
                return s.Trim().ToLowerInvariant();
        return null;
    }

    public static bool Is(Exception? ex, string code) => string.Equals(Code(ex), code, StringComparison.Ordinal);

    /// <summary>Число из тела ответа (число или строка-число); null — нет поля.</summary>
    public static decimal? Number(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            return p;
        return null;
    }

    /// <summary>Сколько секунд ждать (<c>retry_after</c> в теле); null — не сказано.</summary>
    public static int? RetryAfterSeconds(Exception? ex) =>
        ex is ApiException { Payload: { } p } && Number(p, "retry_after") is { } n && n > 0 ? (int)Math.Ceiling(n) : null;

    /// <summary>Осталось попыток PIN (<c>attempts_left</c>); null — не сказано.</summary>
    public static int? AttemptsLeft(Exception? ex) =>
        ex is ApiException { Payload: { } p } && Number(p, "attempts_left") is { } n && n >= 0 ? (int)n : null;

    /// <summary>«N мин» (минуты вверх, не меньше 1) для сообщения о блокировке PIN.</summary>
    public static int MinutesFor(int seconds) => Math.Max(1, (int)Math.Ceiling(seconds / 60.0));

    public static string DuplicateLineText() => Tr.T(
        "В чеке один и тот же товар стоит двумя строками с разной ценой или единицей измерения — сервер такой чек не принимает. Объедините строки или уберите лишнюю.",
        "Чекте бир эле товар баасы же өлчөө бирдиги башка эки сап менен турат — сервер мындай чекти кабыл албайт. Саптарды бириктириңиз же ашыкчасын алып салыңыз.",
        "The same product appears in the receipt on two lines with a different price or unit — the server does not accept such a receipt. Merge the lines or remove the extra one.",
        "Fişte aynı ürün farklı fiyat veya birimle iki satırda yer alıyor — sunucu böyle bir fişi kabul etmiyor. Satırları birleştirin veya fazlasını kaldırın.",
        "Chekda bir xil mahsulot turli narx yoki o'lchov birligi bilan ikki qatorda turibdi — server bunday chekni qabul qilmaydi. Qatorlarni birlashtiring yoki ortiqchasini olib tashlang.");

    public static string LinesMergedToast() => Tr.T(
        "Одинаковые строки в чеке объединены.",
        "Чектеги бирдей саптар бириктирилди.",
        "Identical receipt lines were merged.",
        "Fişteki aynı satırlar birleştirildi.",
        "Chekdagi bir xil qatorlar birlashtirildi.");

    public static string NegativeStockText() => Tr.T(
        "Приход удалить нельзя: часть товара из него уже продана или списана — остаток стал бы отрицательным.",
        "Кирешени өчүрүүгө болбойт: андагы товардын бир бөлүгү сатылып же эсептен чыгарылган — калдык терс болуп калмак.",
        "The receipt cannot be deleted: part of its goods has already been sold or written off — the stock would go negative.",
        "Giriş silinemez: içindeki malların bir kısmı satıldı veya silindi — stok eksiye düşerdi.",
        "Kirimni o'chirib bo'lmaydi: undagi tovarning bir qismi sotilgan yoki hisobdan chiqarilgan — qoldiq manfiy bo'lib qolardi.");

    public static string HasReturnsText() => Tr.T(
        "Приход удалить нельзя: по нему уже оформлен возврат поставщику.",
        "Кирешени өчүрүүгө болбойт: ал боюнча жеткирүүчүгө кайтаруу жасалган.",
        "The receipt cannot be deleted: a return to the supplier has already been made for it.",
        "Giriş silinemez: bunun için tedarikçiye iade zaten yapıldı.",
        "Kirimni o'chirib bo'lmaydi: u bo'yicha yetkazib beruvchiga qaytarish allaqachon rasmiylashtirilgan.");

    public static string DebtAlreadyPaidText() => Tr.T(
        "Приход удалить нельзя: по долгу за него уже платили поставщику.",
        "Кирешени өчүрүүгө болбойт: анын карызы боюнча жеткирүүчүгө буга чейин төлөнгөн.",
        "The receipt cannot be deleted: payments on its debt have already been made to the supplier.",
        "Giriş silinemez: borcu için tedarikçiye zaten ödeme yapıldı.",
        "Kirimni o'chirib bo'lmaydi: uning qarzi bo'yicha yetkazib beruvchiga allaqachon to'langan.");

    public static string ReadOnlyText() => Tr.T(
        "У вас нет права менять товары (режим «только просмотр»). Обратитесь к владельцу.",
        "Сизде товарларды өзгөртүүгө укук жок («көрүү гана» режими). Ээсине кайрылыңыз.",
        "You do not have the right to edit products (view-only mode). Ask the owner.",
        "Ürünleri değiştirme yetkiniz yok («salt okunur» modu). Sahibine başvurun.",
        "Sizda mahsulotlarni o'zgartirish huquqi yo'q («faqat ko'rish» rejimi). Egasiga murojaat qiling.");

    public static string PinInvalidText(int? attemptsLeft) => attemptsLeft is { } n
        ? Tr.T($"PIN неверный. Осталось попыток: {n}.", $"PIN туура эмес. Калган аракеттер: {n}.", $"Wrong PIN. Attempts left: {n}.", $"PIN yanlış. Kalan deneme: {n}.", $"PIN noto'g'ri. Qolgan urinishlar: {n}.")
        : Tr.T("PIN неверный.", "PIN туура эмес.", "Wrong PIN.", "PIN yanlış.", "PIN noto'g'ri.");

    public static string PinLockedText(int? retryAfterSeconds)
    {
        if (retryAfterSeconds is not { } sec || sec <= 0)
            return Tr.T("PIN заблокирован после неверных попыток. Повторите позже.", "PIN туура эмес аракеттерден кийин бөгөттөлдү. Кийинчерээк кайталаңыз.",
                "The PIN is locked after wrong attempts. Try again later.", "PIN yanlış denemelerden sonra kilitlendi. Daha sonra tekrar deneyin.", "PIN noto'g'ri urinishlardan keyin bloklandi. Keyinroq takrorlang.");
        var m = MinutesFor(sec);
        return Tr.T($"PIN заблокирован после неверных попыток. Повторите через {m} мин.", $"PIN туура эмес аракеттерден кийин бөгөттөлдү. {m} мүнөттөн кийин кайталаңыз.",
            $"The PIN is locked after wrong attempts. Try again in {m} min.", $"PIN yanlış denemelerden sonra kilitlendi. {m} dk sonra tekrar deneyin.", $"PIN noto'g'ri urinishlardan keyin bloklandi. {m} daqiqadan keyin takrorlang.");
    }

    /// <summary>Понятный текст по коду ошибки; null — код не известен (показывается слово сервера).</summary>
    public static string? Friendly(Exception? ex) => Code(ex) switch
    {
        DuplicateLine => DuplicateLineText(),
        NegativeStock when ex is ApiException { StatusCode: 409 } => NegativeStockText(),
        HasReturns => HasReturnsText(),
        DebtAlreadyPaid => DebtAlreadyPaidText(),
        ReadOnly => ReadOnlyText(),
        PinInvalid => PinInvalidText(AttemptsLeft(ex)),
        PinLocked => PinLockedText(RetryAfterSeconds(ex)),
        _ => null,
    };
}
