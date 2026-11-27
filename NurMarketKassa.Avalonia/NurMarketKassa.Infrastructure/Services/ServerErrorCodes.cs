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
    // 2026-11-19 (ответ Умара 19.11): у владельца/админа PIN не задан; поле payment.received длиннее 12 цифр → 400 validation_error.
    public const string PinNotSet = "pin_not_set";
    public const string ValidationError = "validation_error";
    // 2026-11-13 (сервер sn1311, электроника): проверки серийных номеров в строках продажи — только с флагом компании electronics_2026_11, ошибки «в формате строки» (code, index).
    public const string SerialAlreadySold = "serial_already_sold";
    public const string SerialCountMismatch = "serial_count_mismatch";
    public const string SerialDuplicateInLine = "serial_duplicate_in_line";
    public const string SerialTooLong = "serial_too_long";
    // 2026-11-14 (ответ Умара 13.11 вечер, строймаркет): упаковки (sale_units), шаг/минимум количества, объект прораба, лимит долга клиента.
    public const string SaleUnitConflict = "sale_unit_conflict";
    public const string SaleUnitNotFound = "sale_unit_not_found";
    public const string QuantityStepMismatch = "quantity_step_mismatch";
    public const string QuantityBelowMin = "quantity_below_min";
    public const string ObjectNotFound = "object_not_found";
    public const string CreditLimitExceeded = "credit_limit_exceeded";
    // 2026-11-26 (Д1, отчёт «Проверка сфер»): отказы 403 по тарифу компании (ответ Умара 20.11) — раньше любой 403 показывался как «Нет права на это действие».
    public const string PlanLimit = "plan_limit";
    public const string PlanClientNotAllowed = "plan_client_not_allowed";
    // 2026-11-26 (Д1, autoparts): сервер на возврат чека отвечает 403 pin_required — действие нужно подтвердить PIN владельца/админа (X-Confirm-Token), а не «нет права».
    public const string PinRequired = "pin_required";

    /// <summary>2026-11-26 (Д1): сервер просит подтвердить действие PIN-кодом (403 <c>pin_required</c>) — касса спрашивает PIN и повторяет запрос с X-Confirm-Token.</summary>
    public static bool IsPinRequired(Exception? ex) => ex is ApiException { StatusCode: 403 } && Is(ex, PinRequired);

    /// <summary>2026-11-26 (Д1): текст, если PIN не введён (отмена) или повтор с токеном тоже отклонён.</summary>
    public static string PinRequiredText() => Tr.T(
        "Это действие нужно подтвердить PIN-кодом владельца или администратора. Повторите и введите PIN.",
        "Бул аракетти ээсинин же администратордун PIN коду менен ырастоо керек. Кайталап, PIN киргизиңиз.",
        "This action must be confirmed with the owner's or administrator's PIN. Try again and enter the PIN.",
        "Bu işlem sahibin veya yöneticinin PIN'i ile onaylanmalı. Tekrar deneyin ve PIN'i girin.",
        "Bu amalni egasi yoki administrator PIN kodi bilan tasdiqlash kerak. Qayta urinib, PIN kiriting.");

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

    /// <summary>2026-11-26 (Д1): лимит тарифа компании исчерпан (сотрудник, вторая касса и т. п.).</summary>
    public static string PlanLimitText() => Tr.T(
        "Лимит вашего тарифа исчерпан. Обратитесь к владельцу: нужно сменить тариф.",
        "Тарифиңиздин чеги түгөндү. Ээсине кайрылыңыз: тарифти алмаштыруу керек.",
        "Your plan limit has been reached. Ask the owner to change the plan.",
        "Tarifinizin sınırı doldu. Sahibine başvurun: tarifin değiştirilmesi gerekiyor.",
        "Tarifingiz limiti tugadi. Egasiga murojaat qiling: tarifni almashtirish kerak.");

    /// <summary>2026-11-26 (Д1): тариф компании не пускает этот вид программы (например, «Мини» — только с телефона).</summary>
    public static string PlanClientNotAllowedText() => Tr.T(
        "Ваш тариф не позволяет работать в этой программе. Обратитесь к владельцу: нужно сменить тариф.",
        "Тарифиңиз бул программада иштөөгө уруксат бербейт. Ээсине кайрылыңыз: тарифти алмаштыруу керек.",
        "Your plan does not allow using this application. Ask the owner to change the plan.",
        "Tarifiniz bu programda çalışmaya izin vermiyor. Sahibine başvurun: tarifin değiştirilmesi gerekiyor.",
        "Tarifingiz bu dasturda ishlashga ruxsat bermaydi. Egasiga murojaat qiling: tarifni almashtirish kerak.");

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

    /// <summary>2026-11-19: PIN владельца/админа не задан — кассир не может подтвердить отправку, пока его не зададут.</summary>
    public static string PinNotSetText() => Tr.T(
        "PIN не задан — задайте в Настройки → Сотрудники.",
        "PIN коюлган эмес — Жөндөөлөр → Кызматкерлер бөлүмүндө коюңуз.",
        "The PIN is not set — set it in Settings → Employees.",
        "PIN belirlenmemiş — Ayarlar → Çalışanlar bölümünde belirleyin.",
        "PIN o'rnatilmagan — Sozlamalar → Xodimlar bo'limida o'rnating.");

    /// <summary>2026-11-19 (ответ Умара 19.11, п. 5): сумма оплаты (payment.received) длиннее 12 цифр — сервер отвечает 400 validation_error и ничего не пишет.
    /// Поле ищется в fields / errors / вложенном payment и, как запасной путь, в тексте тела.</summary>
    public static bool IsPaymentReceivedError(Exception? ex)
    {
        if (ex is not ApiException { StatusCode: 400 } api)
            return false;
        try
        {
            if (api.Payload is not { ValueKind: JsonValueKind.Object } p)
                return api.Message.Contains("payment.received", StringComparison.OrdinalIgnoreCase);
            var code = Code(p);
            if (code is not null && code != ValidationError)
                return false;
            if (p.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object
                && (f.TryGetProperty("payment.received", out _) || (f.TryGetProperty("payment", out var pf) && pf.ValueKind == JsonValueKind.Object && pf.TryGetProperty("received", out _))))
                return true;
            return p.GetRawText().Contains("payment.received", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static string PaymentReceivedText() => Tr.T(
        "Проверьте сумму оплаты: в ней больше 12 цифр — похоже на ошибку ввода. Сервер чек не принял, продажа не записана.",
        "Төлөм суммасын текшериңиз: анда 12 санан ашык — киргизүү катасына окшош. Сервер чекти кабыл алган жок, сатуу жазылган жок.",
        "Check the payment amount: it has more than 12 digits — it looks like a typing mistake. The server did not accept the receipt, the sale is not recorded.",
        "Ödeme tutarını kontrol edin: 12 haneden uzun — yazım hatasına benziyor. Sunucu fişi kabul etmedi, satış kaydedilmedi.",
        "To'lov summasini tekshiring: u 12 raqamdan uzun — kiritish xatosiga o'xshaydi. Server chekni qabul qilmadi, sotuv yozilmadi.");

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

    // ── 2026-11-14 (ответ Умара 13.11 вечер, строймаркет): ошибки упаковок, шага/минимума, объекта и лимита долга ──

    /// <summary>Ошибка «строймаркета» из ответа: (код, объект ошибки). Код ищется в теле ответа и в первом подходящем элементе массива errors / items / lines (ошибки строк с index). null — не она.</summary>
    public static (string Code, JsonElement Error)? HardwareError(Exception? ex)
    {
        if (ex is not ApiException { Payload: { } payload } || payload.ValueKind != JsonValueKind.Object)
            return null;
        if (HardwareCodeOf(payload) is { } top)
            return (top, payload);
        foreach (var name in new[] { "errors", "items", "lines" })
            if (payload.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object && HardwareCodeOf(el) is { } nested)
                        return (nested, el);
        return null;
    }

    private static string? HardwareCodeOf(JsonElement obj) =>
        Code(obj) is { } c && c is SaleUnitConflict or SaleUnitNotFound or QuantityStepMismatch or QuantityBelowMin or ObjectNotFound or CreditLimitExceeded ? c : null;

    public static bool IsHardwareError(Exception? ex) => HardwareError(ex) is not null;

    /// <summary>409 credit_limit_exceeded — клиент превысил лимит долга (проверка у сервера только при флаге «Хозтовары»).</summary>
    public static bool IsCreditLimitExceeded(Exception? ex) => HardwareError(ex) is { Code: CreditLimitExceeded };

    /// <summary>Число из ошибки: в самом объекте или в его <c>data</c> (сервер кладёт step/min/limit/debt/requested в <c>data</c>).</summary>
    private static decimal? DataNumber(JsonElement error, string name) =>
        Number(error, name) ?? (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var data) ? Number(data, name) : null);

    private static string Qty(decimal? v) => v is { } d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "?";

    private static string MoneyText(decimal? v) => v is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "?";

    /// <summary>Понятный кассиру текст ошибки упаковки / шага количества / объекта / лимита долга (5 языков). <paramref name="ownerCanOverride"/> — вошёл владелец/админ: к тексту про лимит
    /// добавляется предложение провести продажу с превышением (override_credit_limit). null — это не такая ошибка.</summary>
    public static string? HardwareFriendly(Exception? ex, bool ownerCanOverride = false)
    {
        if (HardwareError(ex) is not { } e)
            return null;
        var prefix = LinePrefix(e.Error);
        var step = Qty(DataNumber(e.Error, "step"));
        var min = Qty(DataNumber(e.Error, "min"));
        var body = e.Code switch
        {
            SaleUnitConflict => Tr.T("В чеке один товар стоит в разных единицах продажи (штуки и упаковка или разные упаковки). Оставьте одну единицу: удалите лишнюю строку.",
                "Чекте бир товар ар башка сатуу бирдиктеринде турат (даана жана таңгак же ар түрдүү таңгак). Бир бирдик калтырыңыз: ашыкча сапты өчүрүңүз.",
                "One product is in the receipt in different sale units (pieces and a pack, or different packs). Keep a single unit: delete the extra line.",
                "Fişte aynı ürün farklı satış birimlerinde (adet ve paket veya farklı paketler). Tek bir birim bırakın: fazla satırı silin.",
                "Chekda bir mahsulot turli sotuv birliklarida turibdi (dona va qadoq yoki turli qadoqlar). Bitta birlik qoldiring: ortiqcha qatorni o'chiring."),
            SaleUnitNotFound => Tr.T("Упаковка не найдена на сервере — её удалили или изменили в NurCRM. Обновите каталог и добавьте товар заново.",
                "Таңгак серверден табылган жок — аны NurCRMде өчүрүшкөн же өзгөртүшкөн. Каталогду жаңыртып, товарды кайра кошуңуз.",
                "The pack was not found on the server — it was deleted or changed in NurCRM. Refresh the catalog and add the product again.",
                "Paket sunucuda bulunamadı — NurCRM'de silinmiş veya değiştirilmiş. Kataloğu yenileyin ve ürünü yeniden ekleyin.",
                "Qadoq serverda topilmadi — uni NurCRMda o'chirishgan yoki o'zgartirishgan. Katalogni yangilab, mahsulotni qayta qo'shing."),
            QuantityStepMismatch => Tr.T($"Количество не подходит под шаг продажи этого товара (шаг {step}). Измените количество.",
                $"Саны бул товарды сатуу кадамына туура келбейт (кадам {step}). Санын өзгөртүңүз.",
                $"The quantity does not match this product's sale step (step {step}). Change the quantity.",
                $"Miktar bu ürünün satış adımına uymuyor (adım {step}). Miktarı değiştirin.",
                $"Miqdor bu mahsulotning sotuv qadamiga mos kelmaydi (qadam {step}). Miqdorni o'zgartiring."),
            QuantityBelowMin => Tr.T($"Количество меньше минимального для продажи этого товара (минимум {min}). Увеличьте количество.",
                $"Саны бул товарды сатуунун эң азынан аз (эң аз {min}). Санын көбөйтүңүз.",
                $"The quantity is below this product's minimum sale quantity (minimum {min}). Increase the quantity.",
                $"Miktar bu ürünün asgari satış miktarından az (asgari {min}). Miktarı artırın.",
                $"Miqdor bu mahsulotning eng kam sotuv miqdoridan kam (eng kami {min}). Miqdorni oshiring."),
            ObjectNotFound => Tr.T("Объект клиента не найден: его удалили или он принадлежит другому клиенту. Выберите объект заново или продайте без объекта.",
                "Кардардын объектиси табылган жок: ал өчүрүлгөн же башка кардарга таандык. Объектини кайра тандаңыз же объектисиз сатыңыз.",
                "The client's site was not found: it was deleted or belongs to another client. Choose the site again or sell without a site.",
                "Müşterinin şantiyesi bulunamadı: silinmiş veya başka bir müşteriye ait. Şantiyeyi yeniden seçin veya şantiyesiz satın.",
                "Mijozning obyekti topilmadi: u o'chirilgan yoki boshqa mijozga tegishli. Obyektni qayta tanlang yoki obyektsiz soting."),
            CreditLimitExceeded => CreditLimitText(e.Error, ownerCanOverride),
            _ => null,
        };
        return body is null ? null : prefix + body;
    }

    private static string CreditLimitText(JsonElement error, bool ownerCanOverride)
    {
        var limit = MoneyText(DataNumber(error, "limit"));
        var debt = MoneyText(DataNumber(error, "debt"));
        var requested = MoneyText(DataNumber(error, "requested"));
        var main = Tr.T($"Продажа в долг не проведена: превышен лимит долга клиента (лимит {limit}, долг {debt}, этот чек в долг {requested} сом).",
            $"Карызга сатуу өткөрүлгөн жок: кардардын карыз лимити ашты (лимит {limit}, карыз {debt}, бул чек карызга {requested} сом).",
            $"The credit sale was not recorded: the client's debt limit is exceeded (limit {limit}, debt {debt}, this receipt on credit {requested} som).",
            $"Veresiye satış kaydedilmedi: müşterinin borç limiti aşıldı (limit {limit}, borç {debt}, bu fiş veresiye {requested} som).",
            $"Qarzga sotuv o'tkazilmadi: mijozning qarz limiti oshdi (limit {limit}, qarz {debt}, bu chek qarzga {requested} so'm).");
        var tail = ownerCanOverride
            ? Tr.T(" Вы можете провести продажу с превышением лимита.",
                " Сиз сатууну лимитти ашып өткөрсөңүз болот.",
                " You can record the sale over the limit.",
                " Satışı limiti aşarak kaydedebilirsiniz.",
                " Savdoni limitdan oshirib o'tkazishingiz mumkin.")
            : Tr.T(" Оплатите часть наличными или позовите владельца.",
                " Бир бөлүгүн накталай төлөңүз же ээсин чакырыңыз.",
                " Pay part in cash or call the owner.",
                " Bir kısmını nakit ödeyin veya sahibini çağırın.",
                " Bir qismini naqd to'lang yoki egasini chaqiring.");
        return main + tail;
    }

    /// <summary>Понятный текст по коду ошибки; null — код не известен (показывается слово сервера).</summary>
    public static string? Friendly(Exception? ex) => BelowCostGuard.IsServerDenial(ex) ? BelowCostGuard.ServerDeniedText(ex, null) : SerialFriendly(ex) ?? HardwareFriendly(ex) ?? Code(ex) switch // 2026-11-13: отказ «ниже закупки» (can_sell_below_cost); ошибки серийных номеров (sn1311); 2026-11-14: строймаркет
    {
        DuplicateLine => DuplicateLineText(),
        NegativeStock when ex is ApiException { StatusCode: 409 } => NegativeStockText(),
        HasReturns => HasReturnsText(),
        DebtAlreadyPaid => DebtAlreadyPaidText(),
        ReadOnly => ReadOnlyText(),
        PlanLimit when ex is ApiException { StatusCode: 403 } => PlanLimitText(), // 2026-11-26 (Д1)
        PlanClientNotAllowed when ex is ApiException { StatusCode: 403 } => PlanClientNotAllowedText(), // 2026-11-26 (Д1)
        PinRequired when ex is ApiException { StatusCode: 403 } => PinRequiredText(), // 2026-11-26 (Д1)
        PinInvalid => PinInvalidText(AttemptsLeft(ex)),
        PinLocked => PinLockedText(RetryAfterSeconds(ex)),
        PinNotSet => PinNotSetText(),
        ValidationError when IsPaymentReceivedError(ex) => PaymentReceivedText(), // 2026-11-19: «Ошибка сервера» вместо понятного текста
        _ => null,
    };
}
