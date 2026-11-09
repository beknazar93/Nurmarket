using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-11-09 (владелец: «если товар не найден — только ПРЕДЛОЖИТЬ найти в интернете, по нажатию»): поиск названия товара по штрихкоду в открытых
/// базах интернета (Open Food Facts и две сестры). НИКОГДА не вызывается сам: ни скан, ни «Массовое добавление», ни накладная его не запускают — только
/// кнопка «Найти в интернете» у строки «Неизвестно». Нашлось — название ставится в строку, метка источника становится «Из интернета» (ReceivingSource.Internet);
/// не нашлось или нет связи — строка остаётся «Неизвестно». Название, которое кассир уже вписал, не заменяется. Поиск по общей базе NurCRM (метка «База NurCRM»)
/// — отдельно, в PurchaseReceivingService.LookupAsync: он ходит на сервер NurCRM, не в интернет.
/// Категория и фото из этого поиска не подставляются: категории открытых баз — английские теги, из них нельзя заводить категории магазина.</summary>
public static class ReceivingInternetLookup
{
    public sealed record Found(string Name, string Origin);

    public enum Outcome
    {
        /// <summary>Строка не «Неизвестно» / нет подходящего штрихкода / поиск уже идёт — ничего не делали.</summary>
        NotApplicable,
        /// <summary>Название подставлено, метка стала «Из интернета».</summary>
        Applied,
        /// <summary>Нашли, но кассир уже вписал своё название — оставлено его, метка прежняя.</summary>
        NameKept,
        /// <summary>В базах такого штрихкода нет — строка осталась «Неизвестно».</summary>
        NotFound,
        /// <summary>Поиск не удался (нет связи, ошибка) — строка осталась «Неизвестно».</summary>
        Failed,
    }

    public sealed record Result(Outcome Outcome, string? Name = null, string? Origin = null);

    /// <summary>Поставщик поиска: штрихкод → найденное (или null). Окна ходят только через <see cref="ApplyAsync"/>; тесты подставляют свой поставщик, настоящий интернет не дёргают.</summary>
    public static Func<string, CancellationToken, Task<Found?>> Provider { get; set; } = OpenFactsAsync;

    /// <summary>Штрихкод, по которому есть смысл искать: 8–14 цифр (EAN-8, UPC, EAN-13, GTIN-14).</summary>
    public static bool IsSearchable(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        var t = code.Trim();
        return t.Length is >= 8 and <= 14 && t.All(char.IsDigit);
    }

    /// <summary>Нажатие «Найти в интернете»: один вызов поставщика на нажатие.</summary>
    public static async Task<Result> ApplyAsync(ReceivingLineVm line, CancellationToken ct = default)
    {
        if (line.Source != ReceivingSource.Unknown || !IsSearchable(line.Barcode) || line.InternetSearching)
            return new Result(Outcome.NotApplicable);
        var code = line.Barcode.Trim();
        Found? found;
        line.InternetSearching = true;
        try
        {
            found = await Provider(code, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Приёмка: поиск штрихкода {code} в интернете по нажатию не удался: {ex.Message}", "WARNING");
            return new Result(Outcome.Failed);
        }
        finally
        {
            line.InternetSearching = false;
        }

        if (found is null || string.IsNullOrWhiteSpace(found.Name))
            return new Result(Outcome.NotFound);
        if (line.Source != ReceivingSource.Unknown)
            return new Result(Outcome.NotApplicable); // пока искали, строку уже превратили во что-то другое — не трогаем
        if (!string.IsNullOrWhiteSpace(line.ProductName))
            return new Result(Outcome.NameKept, found.Name.Trim(), found.Origin);

        line.ProductName = found.Name.Trim();
        line.Source = ReceivingSource.Internet;
        return new Result(Outcome.Applied, line.ProductName, found.Origin);
    }

    /// <summary>Сообщение кассиру о результате нажатия (пусто — нечего сказать).</summary>
    public static string MessageFor(Result r) => r.Outcome switch
    {
        Outcome.Applied => Tr.T($"Из интернета ({r.Origin}): «{r.Name}». Проверьте название и штрихкод перед проведением.",
            $"Интернеттен ({r.Origin}): «{r.Name}». Өткөрүүдөн мурун атын жана штрихкодун текшериңиз.",
            $"From the internet ({r.Origin}): “{r.Name}”. Check the name and barcode before posting.",
            $"İnternetten ({r.Origin}): «{r.Name}». İşlemeden önce adı ve barkodu kontrol edin.",
            $"Internetdan ({r.Origin}): «{r.Name}». O'tkazishdan oldin nomi va shtrix-kodni tekshiring."),
        Outcome.NameKept => Tr.T($"В интернете нашлось «{r.Name}», но вы уже вписали название — оставлено ваше.",
            $"Интернеттен «{r.Name}» табылды, бирок аты жазылган — сиздики калды.",
            $"The internet has “{r.Name}”, but you already typed a name — yours is kept.",
            $"İnternette «{r.Name}» bulundu, ancak zaten bir ad yazdınız — sizinki korundu.",
            $"Internetda «{r.Name}» topildi, lekin nom yozilgan — sizniki qoldirildi."),
        Outcome.NotFound => Tr.T("В интернете по этому штрихкоду ничего не нашлось — впишите название вручную.",
            "Интернетте бул штрихкод боюнча эч нерсе табылган жок — атын кол менен жазыңыз.",
            "Nothing was found on the internet for this barcode — type the name by hand.",
            "Bu barkod için internette bir şey bulunamadı — adı elle yazın.",
            "Bu shtrix-kod bo'yicha internetda hech narsa topilmadi — nomini qo'lda yozing."),
        Outcome.Failed => Tr.T("Поиск в интернете не удался (нет связи?). Строка осталась «Неизвестно» — впишите название вручную или повторите.",
            "Интернеттен издөө ишке ашкан жок (байланыш жокпу?). Сап «Белгисиз» бойдон калды — атын кол менен жазыңыз же кайталаңыз.",
            "The internet search failed (no connection?). The line stays “Unknown” — type the name by hand or try again.",
            "İnternet araması başarısız (bağlantı yok mu?). Satır «Bilinmiyor» kaldı — adı elle yazın veya tekrar deneyin.",
            "Internetdan qidirish bajarilmadi (aloqa yo'qmi?). Qator «Noma'lum» bo'lib qoldi — nomini qo'lda yozing yoki qayta urining."),
        _ => "",
    };

    // ------------------------------------------------------------------ настоящий поставщик: открытые базы Open Food Facts

    private static readonly (string Name, string Host)[] Hosts =
    {
        ("Open Food Facts", "world.openfoodfacts.org"),
        ("Open Beauty Facts", "world.openbeautyfacts.org"),
        ("Open Products Facts", "world.openproductsfacts.org"),
    };

    private static readonly Lazy<HttpClient> Http = new(() => ExternalHttp.Create(TimeSpan.FromSeconds(10), "NurMarketKassa/1.17 (+https://nurcrm.kg)"));

    private static async Task<Found?> OpenFactsAsync(string code, CancellationToken ct)
    {
        foreach (var (origin, host) in Hosts)
        {
            try
            {
                var json = await Http.Value.GetStringAsync($"https://{host}/api/v2/product/{code}.json?fields=product_name,product_name_ru,brands,quantity", ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 1
                    || !root.TryGetProperty("product", out var p))
                    continue;
                string Str(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
                var name = Str("product_name_ru");
                if (name.Length == 0)
                    name = Str("product_name");
                if (name.Length == 0)
                    continue;
                var brand = Str("brands").Split(',')[0].Trim();
                if (brand.Length > 0 && !name.Contains(brand, StringComparison.OrdinalIgnoreCase))
                    name += " " + brand;
                var quantity = Str("quantity");
                if (quantity.Length > 0 && !name.Contains(quantity, StringComparison.OrdinalIgnoreCase))
                    name += " " + quantity;
                return new Found(name, origin);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // нет в этой базе (404), нет сети или не ответила — пробуем следующую
            }
        }
        return null;
    }
}
