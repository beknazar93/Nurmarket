using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-20 («в один товар и доп. штрихкоды»): запись вкусов/вариантов накладной в УЖЕ существующий товар склада — каждый вариант доп. штрихкодом с названием («Клубничный»),
/// мерж с имеющимися доп. кодами без потери (InvoiceBarcodeBinder читает карточку с сервера и пишет весь список). Шаг <c>add_variants</c> идёт ПОСЛЕ закупки (как set_barcode): остаток приходит
/// документом закупки на сумму вариантов, коды дописываются отдельно. Код уже у этого товара — ничего не пишется; код у ДРУГОГО товара — пропускается с пометкой, чужой товар не трогаем.
/// Новый товар получает доп. коды при создании (PurchaseReceivingService, ReceivingLineVm.ExtraBarcodes), сюда не попадает.</summary>
public static class InvoiceVariantWriter
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Шаг «добавить варианты» для приходов к существующим товарам: берутся шаги receive с Variants. Шаги, где у существующего товара вкусов нет, не добавляются.</summary>
    public static List<ProductActionPlan.Step> StepsFor(IEnumerable<ProductActionPlan.Step> steps)
    {
        var result = new List<ProductActionPlan.Step>();
        foreach (var st in steps)
        {
            if (st.Op != "receive" || st.Product.Id.Length == 0 || st.Variants is not { Count: > 0 } parts || !parts.Any(p => p.Barcode.Trim().Length > 0)
                || steps.Any(x => x.Op == "add_variants" && string.Equals(x.Product.Id, st.Product.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Два прихода к одному товару (варианты с разной ценой) — один шаг со всеми вариантами.
            var at = result.FindIndex(r => string.Equals(r.Product.Id, st.Product.Id, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
                result[at] = result[at] with { Variants = result[at].Variants!.Concat(parts).ToList() };
            else
                result.Add(new ProductActionPlan.Step("add_variants", st.Product, null, null, null, Variants: parts, Base: st.Base));
        }
        return result;
    }

    public static async Task<ProductActions.Result> AddAsync(ProductActionPlan.Step s, string who, CancellationToken ct = default)
    {
        var title = s.Product.Title;
        if (s.Product.Id.Length == 0 || s.Variants is not { Count: > 0 } parts)
            return new ProductActions.Result(false, "«" + title + "»: " + T("нет вариантов для записи.", "жазуу үчүн варианттар жок.", "no variants to write.", "yazılacak varyant yok.", "yoziladigan variant yo'q."));
        var added = new List<string>();
        var already = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        var fresh = ProductActions.Find(s.Product.Id) is { } f && string.Equals(f.Id, s.Product.Id, StringComparison.OrdinalIgnoreCase) ? f : s.Product;
        foreach (var part in parts)
        {
            var code = InvoiceBarcodeRules.Clean(part.Barcode);
            var label = part.Name.Length > 0 ? InvoiceVariantPlan.VariantTitle(part.Name) : code;
            if (code.Length == 0)
                continue;
            if (!InvoiceBarcodeRules.IsValidNew(code))
            {
                skipped.Add($"{label}: " + InvoiceBarcodeRules.FormatHint(code));
                continue;
            }
            var owner = await InvoiceBarcodeBinder.FindOwnerAsync(code, ct).ConfigureAwait(false);
            if (owner.Owner is { } other)
            {
                if (string.Equals(other.Id, s.Product.Id, StringComparison.OrdinalIgnoreCase))
                    already.Add(label);
                else
                    skipped.Add($"{label}: " + InvoiceBarcodeRules.OwnedByOtherText(code, other.Title));
                continue;
            }
            // Вариант с названием — всегда доп. штрихкод; обычный (без вкуса) у товара без основного кода становится основным (variant = null).
            var variant = part.Name.Length > 0 ? new AlternateBarcodeVariant { Barcode = code, Name = InvoiceVariantPlan.VariantTitle(part.Name) } : null;
            var res = await InvoiceBarcodeBinder.BindAsync(s.Product.Id, code, variant, ct).ConfigureAwait(false);
            if (!res.Ok)
                failed.Add($"{label}: {res.Message}");
            else if (res.Plan == InvoiceBindPlan.Already)
                already.Add(label);
            else
                added.Add(label);
        }
        var text = new List<string>();
        if (added.Count > 0)
            text.Add(T("добавлены варианты: ", "варианттар кошулду: ", "variants added: ", "varyantlar eklendi: ", "variantlar qo'shildi: ") + string.Join(", ", added));
        if (already.Count > 0)
            text.Add(T("уже были: ", "мурун эле бар болчу: ", "already there: ", "zaten vardı: ", "avval ham bor edi: ") + string.Join(", ", already));
        if (skipped.Count > 0)
            text.Add(T("пропущены: ", "өткөрүлүп жиберилди: ", "skipped: ", "atlandı: ", "o'tkazib yuborildi: ") + string.Join("; ", skipped));
        if (failed.Count > 0)
            text.Add(T("не записаны: ", "жазылган жок: ", "not saved: ", "yazılmadı: ", "yozilmadi: ") + string.Join("; ", failed));
        if (text.Count == 0)
            text.Add(T("штрихкодов у вариантов нет — нечего добавлять.", "варианттарда штрихкод жок — кошууга эч нерсе жок.", "the variants have no barcodes — nothing to add.", "varyantların barkodu yok — eklenecek bir şey yok.", "variantlarda shtrix-kod yo'q — qo'shishga hech narsa yo'q."));
        PosLogger.Log($"Накладная, варианты в товар {s.Product.Id} ({who}): добавлено {added.Count}, уже были {already.Count}, пропущено {skipped.Count}, ошибок {failed.Count}.", "STOCK");
        var ok = failed.Count == 0 && skipped.Count == 0;
        return new ProductActions.Result(ok || (added.Count + already.Count > 0 && failed.Count == 0), "«" + (fresh.Title.Length > 0 ? fresh.Title : title) + "»: " + string.Join("; ", text) + ".",
            Partial: skipped.Count > 0 || (failed.Count > 0 && added.Count > 0));
    }
}
