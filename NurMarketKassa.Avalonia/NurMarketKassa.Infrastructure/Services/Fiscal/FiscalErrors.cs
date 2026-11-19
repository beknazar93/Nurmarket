using System.Text.Json;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14 (П0-1): понятные тексты фискальных ошибок сервера (409 fiscal_locked, fiscal_already_set, …). Сервер отдаёт код в <c>code</c> корня ответа;
/// своё <c>detail</c> может быть на другом языке или слишком техническим. Тексты на 5 языках. Используется и общим клиентом API
/// (<c>NurMarketApiClient</c> подменяет сообщение 409 с таким кодом — так «Чек пробит в ККМ, изменить нельзя» видно во ВСЕХ местах правки и удаления чека), и <see cref="FiscalApi"/>.</summary>
public static class FiscalErrors
{
    public const string Locked = "fiscal_locked";
    public const string AlreadySet = "fiscal_already_set";
    public const string SaleNotCompleted = "sale_not_completed";
    public const string ReturnVoided = "return_voided";
    public const string ModeServer = "fiscal_mode_server";
    public const string ValidationError = "validation_error";

    /// <summary>Код ошибки из тела ответа (<c>code</c>, запасной ключ <c>error</c>); пусто — кода нет.</summary>
    public static string CodeOf(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p)
            return "";
        foreach (var key in new[] { "code", "error" })
            if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s.Trim().ToLowerInvariant();
        return "";
    }

    /// <summary>Тело 409 при массовом удалении: часть чеков пробита — сервер присылает <c>locked_ids</c>.</summary>
    public static bool HasLockedIds(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("locked_ids", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0;

    /// <summary>Чек пробит — менять нельзя. Оформите возврат.</summary>
    public static string LockedSaleText => Tr.T(
        "Чек пробит в ККМ, изменить нельзя — оформите возврат.",
        "Чек ККМде бектилген, өзгөртүүгө болбойт — кайтарууну тариздеңиз.",
        "The receipt is fiscalized in the cash register and cannot be changed — make a return.",
        "Fiş ÖKC'de kesildi, değiştirilemez — iade düzenleyin.",
        "Chek KKMda urilgan, o'zgartirib bo'lmaydi — qaytarish rasmiylashtiring.");

    public static string LockedReturnText => Tr.T(
        "Возврат пробит в ККМ, изменить или аннулировать его нельзя.",
        "Кайтаруу ККМде бектилген, аны өзгөртүүгө же жокко чыгарууга болбойт.",
        "The return is fiscalized in the cash register and cannot be edited or voided.",
        "İade ÖKC'de kesildi, düzenlenemez veya iptal edilemez.",
        "Qaytarish KKMda urilgan, uni o'zgartirib yoki bekor qilib bo'lmaydi.");

    public static string LockedBulkText => Tr.T(
        "Часть чеков пробита в ККМ — их удалить нельзя. Выберите только непробитые или оформите возврат.",
        "Чектердин бир бөлүгү ККМде бектилген — аларды өчүрүүгө болбойт. Бектилбегендерин гана тандаңыз же кайтарууну тариздеңиз.",
        "Some receipts are fiscalized in the cash register and cannot be deleted. Select only the unfiscalized ones or make returns.",
        "Bazı fişler ÖKC'de kesildi ve silinemez. Yalnızca kesilmemiş olanları seçin veya iade düzenleyin.",
        "Ba'zi cheklar KKMda urilgan va ularni o'chirib bo'lmaydi. Faqat urilmaganlarini tanlang yoki qaytarish rasmiylashtiring.");

    public static string AlreadySetText => Tr.T(
        "У этого чека уже записаны другие фискальные реквизиты. Повторно не пробивайте — сверьте данные с ККМ.",
        "Бул чекке башка фискалдык реквизиттер мурунтан жазылган. Чекти кайра бекитпеңиз — маалыматтарды ККМ менен салыштырыңыз.",
        "This receipt already has different fiscal details recorded. Do not fiscalize it again — check the data against the cash register.",
        "Bu fişe başka mali bilgiler zaten yazılmış. Tekrar kesmeyin — verileri ÖKC ile karşılaştırın.",
        "Bu chekda boshqa fiskal rekvizitlar allaqachon yozilgan. Qayta urmang — ma'lumotlarni KKM bilan solishtiring.");

    public static string SaleNotCompletedText => Tr.T(
        "Продажа ещё не проведена на сервере — реквизиты запишутся позже.",
        "Сатуу сервердеги өткөрүлө элек — реквизиттер кийинчерээк жазылат.",
        "The sale is not completed on the server yet — the details will be recorded later.",
        "Satış sunucuda henüz tamamlanmadı — bilgiler daha sonra yazılacak.",
        "Sotuv serverda hali o'tkazilmagan — rekvizitlar keyinroq yoziladi.");

    public static string ReturnVoidedText => Tr.T(
        "Возврат аннулирован — фискальные реквизиты записать нельзя.",
        "Кайтаруу жокко чыгарылган — фискалдык реквизиттерди жазууга болбойт.",
        "The return was voided — fiscal details cannot be recorded.",
        "İade iptal edildi — mali bilgiler yazılamaz.",
        "Qaytarish bekor qilingan — fiskal rekvizitlarni yozib bo'lmaydi.");

    public static string ModeServerText => Tr.T(
        "Фискальные чеки пробивает сервер NurCRM — касса их не пробивает. Режим меняет владелец в настройках.",
        "Фискалдык чектерди NurCRM сервери бекитет — касса бекитпейт. Режимди ээси жөндөөлөрдөн өзгөртөт.",
        "Fiscal receipts are issued by the NurCRM server — the kassa does not issue them. The owner changes the mode in settings.",
        "Mali fişleri NurCRM sunucusu keser — kasa kesmez. Modu sahip ayarlardan değiştirir.",
        "Fiskal cheklarni NurCRM serveri uradi — kassa urmaydi. Rejimni egasi sozlamalarda o'zgartiradi.");

    public static string ValidationText(string? fields) => Tr.T(
        "Сервер не принял фискальные реквизиты", "Сервер фискалдык реквизиттерди кабыл алган жок", "The server did not accept the fiscal details",
        "Sunucu mali bilgileri kabul etmedi", "Server fiskal rekvizitlarni qabul qilmadi")
        + (string.IsNullOrWhiteSpace(fields) ? "." : ": " + fields + ".");

    /// <summary>Понятный текст для ответа сервера с этим кодом (общий клиент API подменяет им сообщение). false — ответ не фискальный, сообщение остаётся прежним.
    /// <paramref name="relativePath"/> — путь запроса (по «/returns/» выбирается текст про возврат). Только 409: коды fiscal_locked / fiscal_already_set / fiscal_mode_server / sale_not_completed / return_voided и 409 с locked_ids.</summary>
    public static bool TryFriendly(int status, JsonElement? payload, out string text, string? relativePath = null)
    {
        text = "";
        if (status != 409)
            return false;
        var code = CodeOf(payload);
        if (HasLockedIds(payload))
        {
            text = LockedBulkText;
            return true;
        }
        switch (code)
        {
            case Locked:
                // возврат (pos/returns/…) — свой текст: «оформите возврат» к возврату не подходит
                text = relativePath?.Contains("/returns/", StringComparison.OrdinalIgnoreCase) == true ? LockedReturnText : LockedSaleText;
                return true;
            case AlreadySet:
                text = AlreadySetText;
                return true;
            case ModeServer:
                text = ModeServerText;
                return true;
            case SaleNotCompleted:
                text = SaleNotCompletedText;
                return true;
            case ReturnVoided:
                text = ReturnVoidedText;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Строка «поле: сообщение; …» из <c>fields</c> ответа 400 validation_error (объект «поле → текст или список»).</summary>
    public static string FieldsText(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return "";
        var parts = new List<string>();
        foreach (var f in fields.EnumerateObject())
        {
            var msg = f.Value.ValueKind switch
            {
                JsonValueKind.String => f.Value.GetString(),
                JsonValueKind.Array => string.Join(", ", f.Value.EnumerateArray().Select(x => x.ToString())),
                _ => f.Value.ToString(),
            };
            parts.Add(string.IsNullOrWhiteSpace(msg) ? f.Name : f.Name + ": " + msg);
        }
        return string.Join("; ", parts);
    }
}
