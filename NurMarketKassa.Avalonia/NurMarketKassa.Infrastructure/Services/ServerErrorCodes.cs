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
    // 2026-11-13 (сервер sn1311, электроника): проверки серийных номеров в строках продажи — только с флагом компании electronics_2026_11, ошибки «в формате строки» (code, index).
    public const string SerialAlreadySold = "serial_already_sold";
    public const string SerialCountMismatch = "serial_count_mismatch";
    public const string SerialDuplicateInLine = "serial_duplicate_in_line";
    public const string SerialTooLong = "serial_too_long";

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

    /// <summary>2026-11-13 (сервер sn1311): ошибка серийного номера строки — (код, объект ошибки). Код ищется в теле ответа и, если там нет, в первом элементе массива
    /// errors / items / lines (сервер отдаёт ошибки строк с полями code, index, serial…). null — это не ошибка серийника.</summary>
    public static (string Code, JsonElement Error)? SerialError(Exception? ex)
    {
        if (ex is not ApiException { Payload: { } payload } || payload.ValueKind != JsonValueKind.Object)
            return null;
        if (SerialCodeOf(payload) is { } top)
            return (top, payload);
        foreach (var name in new[] { "errors", "items", "lines" })
            if (payload.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object && SerialCodeOf(el) is { } nested)
                        return (nested, el);
        return null;
    }

    private static string? SerialCodeOf(JsonElement obj) => Code(obj) is { } c && c is SerialAlreadySold or SerialCountMismatch or SerialDuplicateInLine or SerialTooLong ? c : null;

    public static bool IsSerialError(Exception? ex) => SerialError(ex) is not null;

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    /// <summary>«Строка 2: » — номер строки чека по полю index (с нуля); без поля — пусто.</summary>
    private static string LinePrefix(JsonElement error) =>
        Number(error, "index") is { } i && i >= 0
            ? Tr.T($"Строка {(int)i + 1}: ", $"{(int)i + 1}-сап: ", $"Line {(int)i + 1}: ", $"Satır {(int)i + 1}: ", $"{(int)i + 1}-qator: ")
            : "";

    /// <summary>Понятный кассиру текст ошибки серийного номера (5 языков); чек остаётся в кассе, оплата не проведена. null — не ошибка серийника.</summary>
    public static string? SerialFriendly(Exception? ex)
    {
        if (SerialError(ex) is not { } e)
            return null;
        var prefix = LinePrefix(e.Error);
        var serial = Text(e.Error, "serial");
        var shown = string.IsNullOrWhiteSpace(serial) ? "" : " " + serial.Trim();
        var tail = Tr.T(" Чек остаётся в кассе — исправьте номера и повторите оплату.",
            " Чек кассада калат — номерлерди оңдоп, төлөмдү кайталаңыз.",
            " The receipt stays in the till — fix the numbers and repeat the payment.",
            " Fiş kasada kalır — numaraları düzeltip ödemeyi tekrarlayın.",
            " Chek kassada qoladi — raqamlarni tuzatib, to'lovni takrorlang.");
        var body = e.Code switch
        {
            SerialAlreadySold => Tr.T($"Серийный номер{shown} уже продан в другом чеке и не возвращён. Проверьте номер на упаковке или сначала оформите возврат прежней продажи.",
                $"Сериялык номер{shown} башка чекте сатылып кеткен жана кайтарылган эмес. Номерди таңгактан текшериңиз же адегенде мурунку сатууну кайтарыңыз.",
                $"The serial number{shown} has already been sold in another receipt and not returned. Check the number on the box or first return the earlier sale.",
                $"Seri numarası{shown} başka bir fişte zaten satıldı ve iade edilmedi. Kutudaki numarayı kontrol edin veya önce önceki satışı iade edin.",
                $"Seriya raqami{shown} boshqa chekda allaqachon sotilgan va qaytarilmagan. Qutidagi raqamni tekshiring yoki avval oldingi sotuvni qaytaring."),
            SerialCountMismatch => Tr.T($"Серийных номеров больше, чем штук в строке{CountPair(e.Error)}. Уберите лишние номера или увеличьте количество.",
                $"Сериялык номерлер саптагы даанадан көп{CountPair(e.Error)}. Ашыкчаларын алып салыңыз же санын көбөйтүңүз.",
                $"There are more serial numbers than units in the line{CountPair(e.Error)}. Remove the extra numbers or increase the quantity.",
                $"Satırdaki adetten fazla seri numarası var{CountPair(e.Error)}. Fazla numaraları kaldırın veya miktarı artırın.",
                $"Qatordagi donadan ko'p seriya raqami bor{CountPair(e.Error)}. Ortiqcha raqamlarni olib tashlang yoki sonini oshiring."),
            SerialDuplicateInLine => Tr.T($"Один и тот же серийный номер{shown} указан в строке дважды. Уберите повтор.",
                $"Бир эле сериялык номер{shown} сапта эки жолу көрсөтүлгөн. Кайталоону алып салыңыз.",
                $"The same serial number{shown} appears twice in the line. Remove the duplicate.",
                $"Aynı seri numarası{shown} satırda iki kez yazılmış. Tekrarı kaldırın.",
                $"Bir xil seriya raqami{shown} qatorda ikki marta ko'rsatilgan. Takrorini olib tashlang."),
            SerialTooLong => Tr.T($"Серийный номер{shown} слишком длинный (не больше 64 знаков). Проверьте, что номер введён верно.",
                $"Сериялык номер{shown} өтө узун (64 белгиден көп эмес). Номер туура киргизилгенин текшериңиз.",
                $"The serial number{shown} is too long (up to 64 characters). Check that it was entered correctly.",
                $"Seri numarası{shown} çok uzun (en fazla 64 karakter). Doğru girildiğini kontrol edin.",
                $"Seriya raqami{shown} juda uzun (64 belgigacha). To'g'ri kiritilganini tekshiring."),
            _ => null,
        };
        return body is null ? null : prefix + body + tail;
    }

    private static string CountPair(JsonElement error) =>
        Number(error, "count") is { } c && Number(error, "quantity") is { } q
            ? $" ({c.ToString("0.###", CultureInfo.InvariantCulture)} / {q.ToString("0.###", CultureInfo.InvariantCulture)})"
            : "";

    /// <summary>Понятный текст по коду ошибки; null — код не известен (показывается слово сервера).</summary>
    public static string? Friendly(Exception? ex) => BelowCostGuard.IsServerDenial(ex) ? BelowCostGuard.ServerDeniedText(ex, null) : SerialFriendly(ex) ?? Code(ex) switch // 2026-11-13: отказ «ниже закупки» (can_sell_below_cost); ошибки серийных номеров (sn1311)
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
