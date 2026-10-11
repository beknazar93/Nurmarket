using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «проведи полный редизайн склада… и ещё к ИИ и боту дай полный доступ к товарам — количество
/// добавить, уменьшить, следить за сроками годности, количеством и, если надо, как с фото полностью наполнить информацией
/// товары, описание». Общие действия с товаром — для кнопки «±» на складе, ИИ-советника программы владельца и бота:
/// • остаток: приход, списание, точное количество — так же, как «Списание» склада: остаток перечитывается с сервера
///   (не из кеша — иначе откатились бы продажи других касс), и сервер получает документ ревизии с итоговым количеством
///   (CreateSessionAsync + ApplySessionAsync). Списание пишется в журнал списаний и в смену (по закупочной цене);
/// • поля карточки (описание, срок годности, срок хранения, минимальный остаток, цены, страна) — PATCH товара.
/// Сами ИИ и бот ничего не меняют без подтверждения владельца — подтверждение показывают они (см. ProductActionPlan).</summary>
public static class ProductActions
{
    public sealed record Result(bool Ok, string Message, bool Partial = false);

    /// <summary>Учёт остатков (документ ревизии). Задаётся приложением при запуске.</summary>
    public static IInventoryApiService? InventoryApi { get; set; }

    /// <summary>Перечитать каталог после правки карточки (задаётся приложением: SyncService.RequestCatalogSyncNow).</summary>
    public static Action? RequestCatalogRefresh { get; set; }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Товар по id, штрихкоду или названию (точно, затем единственное совпадение по части названия).</summary>
    public static CatalogProductTileVm? Find(string? idOrName)
    {
        var q = (idOrName ?? "").Trim();
        if (q.Length == 0)
            return null;
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return all.FirstOrDefault(p => string.Equals(p.Id, q, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Barcode) && string.Equals(p.Barcode, q, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => string.Equals(p.Title.Trim(), q, StringComparison.CurrentCultureIgnoreCase))
               ?? (all.Where(p => p.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(2).ToList() is { Count: 1 } one ? one[0] : null);
    }

    public static string Qty(double v) => v.ToString("0.###", Ru);

    /// <summary>Изменить остаток: <paramref name="delta"/> (+ приход, − списание) или <paramref name="setTo"/> (точное количество).</summary>
    public static async Task<Result> ChangeStockAsync(string productId, double? delta, double? setTo, string reason, string? actor,
        CancellationToken ct = default, double? expectedCurrent = null)
    {
        var product = Find(productId);
        if (product is null)
            return new Result(false, T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.", "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."));
        if (InventoryApi is not { } api)
            return new Result(false, T("Учёт остатков недоступен в этом режиме.", "Бул режимде калдыкты эсепке алуу жеткиликсиз.", "Stock accounting is not available in this mode.", "Stok takibi bu modda kullanılamıyor.", "Bu rejimda qoldiq hisobi mavjud emas."));

        double current;
        try
        {
            var detail = await PosApp.CatalogApi.ProductsDetailAsync(product.Id, ct).ConfigureAwait(false);
            if (detail is not { } el)
                return new Result(false, T("Сервер не вернул остаток товара — ничего не изменено.", "Сервер товардын калдыгын кайтарган жок — эч нерсе өзгөргөн жок.", "The server didn't return the stock — nothing changed.", "Sunucu stoğu döndürmedi — hiçbir şey değişmedi.", "Server qoldiqni qaytarmadi — hech narsa o'zgarmadi."));
            current = StockSyncService.ResolveStockQuantity(el, product.MustWeigh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: остаток «{product.Title}» не получен ({ex.Message}).", "WARNING");
            return new Result(false, T("Нет связи с сервером — остаток не получен, ничего не изменено.", "Сервер менен байланыш жок — калдык алынган жок, эч нерсе өзгөргөн жок.", "No connection to the server — nothing changed.", "Sunucuyla bağlantı yok — hiçbir şey değişmedi.", "Server bilan aloqa yo'q — hech narsa o'zgarmadi."));
        }

        if (expectedCurrent is { } expected && Math.Abs(current - expected) >= 0.0005)
            return new Result(false, T($"«{product.Title}»: остаток изменился после подготовки действия ({Qty(expected)} → {Qty(current)}). Ничего не изменено — обновите данные и подтвердите заново.",
                $"«{product.Title}»: аракет даярдалгандан кийин калдык өзгөрдү ({Qty(expected)} → {Qty(current)}). Өзгөртүү болгон жок — маалыматты жаңыртып, кайра ырастаңыз.",
                $"“{product.Title}”: stock changed after this action was prepared ({Qty(expected)} → {Qty(current)}). Nothing changed — refresh and confirm again.",
                $"«{product.Title}»: işlem hazırlandıktan sonra stok değişti ({Qty(expected)} → {Qty(current)}). Değişiklik yapılmadı — yenileyip tekrar onaylayın.",
                $"«{product.Title}»: amal tayyorlangandan keyin qoldiq o'zgardi ({Qty(expected)} → {Qty(current)}). O'zgarish qilinmadi — yangilab, qayta tasdiqlang."));

        var target = Math.Round(setTo ?? current + (delta ?? 0), 3);
        if (target < 0)
            return new Result(false, T($"«{product.Title}»: списать больше остатка нельзя (есть {Qty(current)}).", $"«{product.Title}»: калдыктан көп чыгарууга болбойт (бар {Qty(current)}).",
                $"“{product.Title}”: can't write off more than the stock ({Qty(current)}).", $"«{product.Title}»: stoktan fazla düşülemez (mevcut {Qty(current)}).", $"«{product.Title}»: qoldiqdan ko'p chiqarib bo'lmaydi (bor {Qty(current)})."));
        if (Math.Abs(target - current) < 0.0005)
            return new Result(true, T($"«{product.Title}»: остаток уже {Qty(current)}.", $"«{product.Title}»: калдык мурунтан {Qty(current)}.", $"“{product.Title}”: stock is already {Qty(current)}.", $"«{product.Title}»: stok zaten {Qty(current)}.", $"«{product.Title}»: qoldiq allaqachon {Qty(current)}."));

        try
        {
            var note = string.IsNullOrWhiteSpace(reason) ? "Изменение остатка" : reason.Trim();
            var sessionId = await api.CreateSessionAsync(note, new[] { new InventorySessionItem(product.Id, target) }, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sessionId))
                return new Result(false, T("Сервер не создал документ — ничего не изменено.", "Сервер документ түзгөн жок — эч нерсе өзгөргөн жок.", "The server didn't create the document — nothing changed.", "Sunucu belgeyi oluşturmadı — hiçbir şey değişmedi.", "Server hujjat yaratmadi — hech narsa o'zgarmadi."));
            await api.ApplySessionAsync(sessionId, allowNegative: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: остаток «{product.Title}» не изменён ({ex.Message}).", "WARNING");
            return new Result(false, T("Не удалось изменить остаток: ", "Калдыкты өзгөртүү мүмкүн болгон жок: ", "Couldn't change the stock: ", "Stok değiştirilemedi: ", "Qoldiqni o'zgartirib bo'lmadi: ") + ex.Message);
        }

        StockSyncService.ApplyQuantityToTileOnUi(product, target, product.MustWeigh);
        CatalogCacheService.PersistProductStock(product.Id, target, product.MustWeigh);
        var diff = target - current;
        if (diff < 0)
        {
            // Как «Списание» склада: журнал списаний и строка «Списания» смены — по закупочной цене.
            try
            {
                var unitCost = product.PurchasePrice > 0 ? product.PurchasePrice : LocalCartService.ParsePrice(product.PriceLine);
                WriteOffHistoryStore.Append(product.Id, product.Title, -diff, reason, actor ?? PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId, unitCost > 0 ? unitCost : null);
                if (unitCost > 0)
                    ShiftEventsStore.Record(ShiftEventsStore.KindWriteOff, PosApp.ActiveShiftId, ShiftEventsStore.OperationKey(product.Id + ":" + DateTime.UtcNow.Ticks), -diff * unitCost, product.Title);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Действие с товаром: журнал списаний не записан ({ex.Message}).", "WARNING");
            }
        }
        PosLogger.Log($"Действие с товаром ({actor ?? "—"}): «{product.Title}» {Qty(current)} → {Qty(target)} ({reason}).", "STOCK");
        return new Result(true, T($"«{product.Title}»: было {Qty(current)}, стало {Qty(target)}.", $"«{product.Title}»: {Qty(current)} болчу, {Qty(target)} болду.",
            $"“{product.Title}”: was {Qty(current)}, now {Qty(target)}.", $"«{product.Title}»: {Qty(current)} idi, şimdi {Qty(target)}.", $"«{product.Title}»: {Qty(current)} edi, endi {Qty(target)}."));
    }

    /// <summary>Поля карточки товара (имена — как у сервера: description, expiration_date, shelf_life_days, minimum_quantity,
    /// price, purchase_price, country). <paramref name="what"/> — что меняем, словами для ответа.</summary>
    public static async Task<Result> UpdateFieldsAsync(string productId, IReadOnlyDictionary<string, object?> fields, string what, string? actor, CancellationToken ct = default)
    {
        var product = Find(productId);
        if (product is null)
            return new Result(false, T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.", "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."));
        if (fields.Count == 0)
            return new Result(false, T("Нечего менять.", "Өзгөртө турган эч нерсе жок.", "Nothing to change.", "Değiştirilecek bir şey yok.", "O'zgartiradigan narsa yo'q."));
        try
        {
            await PosApp.CatalogApi.PatchProductFieldsAsync(product.Id, fields, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: «{product.Title}» — {what} не сохранено ({ex.Message}).", "WARNING");
            return new Result(false, T("Сервер не сохранил: ", "Сервер сактаган жок: ", "The server didn't save it: ", "Sunucu kaydetmedi: ", "Server saqlamadi: ") + ex.Message);
        }
        RequestCatalogRefresh?.Invoke();
        PosLogger.Log($"Действие с товаром ({actor ?? "—"}): «{product.Title}» — {what} ({string.Join(", ", fields.Keys)}).", "STOCK");
        return new Result(true, T($"«{product.Title}»: {what} — сохранено.", $"«{product.Title}»: {what} — сакталды.", $"“{product.Title}”: {what} — saved.",
            $"«{product.Title}»: {what} — kaydedildi.", $"«{product.Title}»: {what} — saqlandi."));
    }

    /// <summary>Карточка товара для ИИ: всё, что известно (id нужен ИИ, чтобы точно указать товар в действии).</summary>
    public static async Task<string> DescribeForAiAsync(CatalogProductTileVm p, CancellationToken ct = default)
    {
        var line = $"• id={p.Id} | {p.Title} | цена {p.PriceLine} | закупка {p.PurchasePrice:0.##} | остаток {Qty(p.Quantity)} {(p.MustWeigh ? "кг" : "шт")}"
                   + (string.IsNullOrWhiteSpace(p.Barcode) ? "" : $" | штрихкод {p.Barcode}")
                   + (string.IsNullOrWhiteSpace(p.Category) ? "" : $" | категория {p.Category}")
                   + (string.IsNullOrWhiteSpace(p.Brand) ? "" : $" | бренд {p.Brand}");
        try
        {
            if (await PosApp.CatalogApi.ProductsDetailAsync(p.Id, ct).ConfigureAwait(false) is { } el && el.ValueKind == JsonValueKind.Object)
            {
                string? S(string name) => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
                if (S("expiration_date") is { Length: > 0 } exp)
                    line += $" | срок годности до {exp}";
                if (S("shelf_life_days") is { Length: > 0 } shelf && shelf != "0")
                    line += $" | срок хранения {shelf} дн.";
                if (S("minimum_quantity") is { Length: > 0 } min && double.TryParse(min, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) && m > 0)
                    line += $" | минимальный остаток {Qty(m)}";
                if (S("country") is { Length: > 0 } country)
                    line += $" | страна {country}";
                var description = S("description") ?? "";
                line += description.Length > 0 ? $" | описание: {(description.Length > 200 ? description[..200] + "…" : description)}" : " | описания нет";
                line += el.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? " | фото есть" : " | фото нет";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Без подробностей сервера — то, что есть в каталоге.
        }
        return line;
    }
}
