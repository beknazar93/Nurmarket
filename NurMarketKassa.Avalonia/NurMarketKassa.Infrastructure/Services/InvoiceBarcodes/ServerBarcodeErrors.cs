using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (ответ Умара 21.11): кто владеет занятым кодом — из ответа сервера 409 <c>barcode_duplicate</c> (data: product_id, name, code, source = product|alternate, barcode).
/// Касса показывает «код уже у товара «…»» без повторного поиска по каталогу.</summary>
public sealed record ServerBarcodeOwner(string? ProductId, string Name, string? Code, string? Source, string Barcode);

/// <summary>2026-11-21: разбор ошибок штрихкодов сервера в одном месте — barcode_duplicate (409; раньше дубль доп. кода давал 400 с текстом) и version_conflict (409, If-Match).</summary>
public static class ServerBarcodeErrors
{
    public const string Duplicate = "barcode_duplicate";
    public const string VersionConflict = "version_conflict";

    /// <summary>Тело 409 barcode_duplicate (JSON ответа) → владелец кода; null — это другая ошибка. Поля берутся из <c>data</c> (или из корня, если data нет).</summary>
    public static ServerBarcodeOwner? TryDuplicate(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } root || ServerErrorCodes.Code(root) != Duplicate)
            return null;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
        return new ServerBarcodeOwner(Text(data, "product_id"), Text(data, "name") ?? "", Text(data, "code"), Text(data, "source"), Text(data, "barcode") ?? "");
    }

    public static ServerBarcodeOwner? TryDuplicate(Exception? ex) => ex is ApiException { StatusCode: 409 or 400, Payload: { } p } ? TryDuplicate(p) : null;

    /// <summary>409 version_conflict: карточку товара успели изменить, пока касса держала её версию.</summary>
    public static bool IsVersionConflict(Exception? ex) => ex is ApiException { StatusCode: 409, Payload: { } p } && p.ValueKind == JsonValueKind.Object && ServerErrorCodes.Code(p) == VersionConflict;

    /// <summary>Текст для окон: «Штрихкод X уже у товара «Y»». Имени нет — без него.</summary>
    public static string DuplicateText(ServerBarcodeOwner owner)
    {
        var code = owner.Barcode;
        return string.IsNullOrWhiteSpace(owner.Name)
            ? Tr.T($"Штрихкод {code} уже занят другим товаром.", $"{code} штрихкоду башка товарда мурунтан бар.", $"Barcode {code} already belongs to another product.",
                $"{code} barkodu başka bir üründe zaten var.", $"{code} shtrix-kodi boshqa mahsulotda allaqachon bor.")
            : InvoiceBarcodeRules.OwnedByOtherText(code, owner.Name.Trim());
    }

    public static string VersionConflictText => Tr.T("Данные только что изменили на другом устройстве — откройте их заново и повторите.",
        "Маалыматтар башка түзмөктө жаңы эле өзгөртүлдү — аларды кайра ачып, кайталаңыз.",
        "The data was just changed on another device — open it again and retry.",
        "Veriler az önce başka bir cihazda değiştirildi — yeniden açıp tekrar deneyin.",
        "Ma'lumotlar hozirgina boshqa qurilmada o'zgartirildi — ularni qayta ochib, takrorlang.");

    /// <summary>Номер версии карточки (поле <c>version</c> из GET products/&lt;id&gt;/) для If-Match; null — сервер версию не прислал (старый сервер).</summary>
    public static string? VersionOf(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("version", out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
}
