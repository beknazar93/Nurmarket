using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-11, ответ Умара (NurCRM): если один штрихкод у двух товаров, GET products/warehouse-barcode/{код}/ отвечает ошибкой «неоднозначный штрихкод»
/// (а не случайным товаром). Касса такой товар НЕ подставляет и не добавляет само — кассир выбирает товар вручную или исправляет дубликат.
/// Это <see cref="ApiException"/> (код ответа сохранён), поэтому прежние проверки по ApiException работают как раньше; Message — готовый текст кассиру.
/// Какой именно код и тело вернёт сервер, точно не известно — разбираем терпимо: 300/400/409/422 и в тексте/теле «ambiguous» / «неоднозначн» / «multiple» / «несколько».
/// </summary>
public sealed class AmbiguousBarcodeException : ApiException
{
    public AmbiguousBarcodeException(int? statusCode, JsonElement? payload)
        : base(FriendlyText, statusCode, payload)
    {
    }

    public static string FriendlyText => Tr.T(
        "Этот штрихкод у нескольких товаров — выберите товар вручную или исправьте дубликат.",
        "Бул штрихкод бир нече товарда бар — товарды кол менен тандаңыз же дубликатты оңдоңуз.",
        "This barcode belongs to several products — choose the product manually or fix the duplicate.",
        "Bu barkod birden fazla üründe var — ürünü elle seçin veya kopyayı düzeltin.",
        "Bu shtrix-kod bir nechta mahsulotda bor — mahsulotni qo'lda tanlang yoki dublikatni tuzating.");

    /// <summary>Ответ сервера — «неоднозначный штрихкод».</summary>
    public static bool IsAmbiguous(ApiException ex)
    {
        if (ex is AmbiguousBarcodeException)
            return true;
        if (ex.StatusCode is not (300 or 400 or 409 or 422))
            return false;
        var text = ex.Message ?? "";
        if (ex.Payload is { } payload)
        {
            try
            {
                text += " " + payload.GetRawText();
            }
            catch (InvalidOperationException)
            {
                // тело не разобралось — судим по тексту ошибки
            }
        }

        return text.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
               || text.Contains("неоднознач", StringComparison.OrdinalIgnoreCase)
               || text.Contains("multiple", StringComparison.OrdinalIgnoreCase)
               || text.Contains("несколько", StringComparison.OrdinalIgnoreCase);
    }
}
