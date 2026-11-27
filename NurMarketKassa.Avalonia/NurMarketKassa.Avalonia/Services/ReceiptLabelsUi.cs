using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-27 (владелец: «сразу печатать штрихкод и ценники из накладной приёмки»): общие надписи и сборка позиций для кнопки «Ценники» в «Закупки → Приём товара»,
/// «Склад → Приёмка» и в просмотре накладной прихода. Позиции и копии считает <see cref="ReceiptLabelPlanner"/>; печатает единая вкладка «Ценники и штрихкоды»
/// (WarehouseWindow.RequestLabels) — тот же шаблон, A4 и термоэтикетка. Здесь только надписи и чтение каталога.</summary>
public static class ReceiptLabelsUi
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Печать этикеток работает на этой платформе (в Android-кассе кнопки нет).</summary>
    public static bool Supported => LabelPrinterSelector.PrintingSupported;

    public static string ButtonText => T("🏷 Ценники", "🏷 Баа белгилери", "🏷 Price tags", "🏷 Fiyat etiketleri", "🏷 Narx yorliqlari");

    public static string ButtonTip => T("Печать ценников и штрихкодов на товары накладной: копий столько, сколько принято (можно изменить в окне печати).",
        "Накладнойдогу товарларга баа белгилерин жана штрих-коддорду басуу: кабыл алынган санча нуска (басуу терезесинде өзгөртсө болот).",
        "Print price tags and barcodes for the products of this delivery: copies = received quantity (can be changed in the print window).",
        "Bu irsaliyedeki ürünler için fiyat etiketi ve barkod yazdır: kopya sayısı kabul edilen miktar kadar (yazdırma penceresinde değiştirilebilir).",
        "Shu yuk xatidagi mahsulotlarga narx yorliqlari va shtrix-kodlarni chop etish: nusxalar qabul qilingan miqdorcha (chop etish oynasida o'zgartirish mumkin).");

    private static bool MustWeighFromCatalog(string id)
    {
        try
        {
            return CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.MustWeigh ?? false;
        }
        catch (InvalidOperationException)
        {
            return false; // каталог обновлялся — весовой узнаем по единице (кг/л/м дают 1 копию)
        }
    }

    /// <summary>Позиции для печати из любых строк (например, проведённых задач очереди приходов).</summary>
    public static ReceiptLabelPlan Plan(IEnumerable<ReceiptLabelSource> lines) => ReceiptLabelPlanner.Build(lines, MustWeighFromCatalog);

    /// <summary>Строки приёмки (до проведения): товары склада с количеством; новые товары не входят (их ещё нет в каталоге).</summary>
    public static ReceiptLabelPlan FromLines(IEnumerable<ReceivingLineVm> lines) =>
        ReceiptLabelPlanner.Build(lines.Select(l => new ReceiptLabelSource(l.ProductId, l.Barcode, l.Quantity, l.Unit, l.IsNew, l.VariantQuantities is { Count: > 0 })), MustWeighFromCatalog);

    /// <summary>Проведённая накладная: принято за вычетом возвращённого поставщику; строки с вариантом (вкусом, размером) печатаются в «Штрихкоды» по вариантам, здесь пропускаются.</summary>
    public static ReceiptLabelPlan FromReceipt(SupplierReceipt receipt) =>
        ReceiptLabelPlanner.Build(receipt.Lines.Select(l => new ReceiptLabelSource(l.ProductId, l.Barcode, l.Qty - Math.Max(l.ReturnedQty, 0), l.Unit, false, !string.IsNullOrWhiteSpace(l.VariantName))), MustWeighFromCatalog);

    /// <summary>Сколько позиций плана имеют в накладной цену продажи, не совпадающую с ценой каталога: ценник напечатается по цене каталога (новая появится после проведения).</summary>
    public static int PriceDiffers(IEnumerable<ReceivingLineVm> lines, ReceiptLabelPlan plan)
    {
        var ids = new HashSet<string>(plan.Items.Select(i => i.ProductId), StringComparer.OrdinalIgnoreCase);
        var count = 0;
        try
        {
            foreach (var l in lines)
            {
                if (l.Quantity <= 0 || l.SalePrice <= 0 || string.IsNullOrWhiteSpace(l.ProductId) || !ids.Contains(l.ProductId!))
                    continue;
                var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, l.ProductId, StringComparison.OrdinalIgnoreCase));
                if (tile is { PriceValue: > 0 } && Math.Abs(tile.PriceValue - l.SalePrice) > 0.005)
                    count++;
            }
        }
        catch (InvalidOperationException) { /* каталог обновлялся — вопрос про цену пропускаем */ }
        return count;
    }

    public static string PriceDiffersText(int count) => T(
        $"У {count} тов. цена продажи в накладной новая, а в каталоге ещё прежняя — ценник напечатается по цене каталога. Новые цены встанут после «Оприходовать»; тогда и печатайте. Печатать сейчас?",
        $"{count} товарда накладнойдогу сатуу баасы жаңы, каталогдо эски — баа белгиси каталогдогу баа менен басылат. Жаңы баалар «Кирешке алуудан» кийин коюлат; ошондо басыңыз. Азыр басабызбы?",
        $"For {count} products the sale price in the delivery is new but the catalog still has the old one — the tag will print the catalog price. New prices apply after “Post the goods”; print then. Print now?",
        $"{count} üründe irsaliyedeki satış fiyatı yeni, katalogda eski — etiket katalog fiyatıyla yazdırılır. Yeni fiyatlar «Kaydet»ten sonra geçer; o zaman yazdırın. Şimdi yazdırılsın mı?",
        $"{count} ta mahsulotda yuk xatidagi sotuv narxi yangi, katalogda esa eski — yorliq katalog narxida chop etiladi. Yangi narxlar «Kirim qilish»dan keyin o'rnatiladi; o'shanda chop eting. Hozir chop etilsinmi?");

    public static string PrintAnywayText => T("Печатать сейчас", "Азыр басуу", "Print now", "Şimdi yazdır", "Hozir chop etish");
    public static string LaterText => T("Не сейчас", "Азыр эмес", "Not now", "Şimdi değil", "Hozir emas");

    /// <summary>Нечего печатать (нет принятых позиций с карточкой в каталоге) — с причиной.</summary>
    public static string NothingText(ReceiptLabelPlan plan) => plan.NotInCatalog > 0
        ? T($"Печатать пока нечего: {plan.NotInCatalog} новых товаров появятся в каталоге после «Оприходовать» — ценники напечатаете тогда.",
            $"Азырынча басуу жок: {plan.NotInCatalog} жаңы товар «Кирешке алуудан» кийин каталогдо пайда болот — ошондо басасыз.",
            $"Nothing to print yet: {plan.NotInCatalog} new products will appear in the catalog after “Post the goods” — print the tags then.",
            $"Henüz yazdırılacak bir şey yok: {plan.NotInCatalog} yeni ürün «Kaydet»ten sonra katalogda görünecek — etiketleri o zaman yazdırın.",
            $"Hozircha chop etadigan narsa yo'q: {plan.NotInCatalog} ta yangi mahsulot «Kirim qilish»dan keyin katalogda paydo bo'ladi — yorliqlarni o'shanda chop eting.")
        : T("Печатать нечего: добавьте товары склада и впишите количество.", "Басуучу эч нерсе жок: кампанын товарларын кошуп, санын жазыңыз.",
            "Nothing to print: add warehouse products and enter quantities.", "Yazdırılacak bir şey yok: depo ürünleri ekleyip miktar girin.", "Chop etadigan narsa yo'q: ombor mahsulotlarini qo'shib, miqdorni kiriting.");

    /// <summary>В проведённой накладной нечего печатать: нет товаров с карточкой, всё возвращено или остались только строки с размерами/вкусами.</summary>
    public static string NothingInReceiptText => T("В накладной нечего печатать: товаров с карточкой нет, всё возвращено или строки с размерами и вкусами (их печатайте во вкладке «Штрихкоды» по вариантам).",
        "Накладнойдо басуучу эч нерсе жок: карточкалуу товар жок, баары кайтарылган же өлчөм/даам саптары (аларды «Штрих-коддор» өтмөгүндө варианттар боюнча басыңыз).",
        "Nothing to print in this delivery: no products with a card, everything was returned, or only lines with sizes and flavours (print those in the “Barcodes” tab by variant).",
        "Bu irsaliyede yazdırılacak bir şey yok: kartlı ürün yok, hepsi iade edilmiş veya yalnızca beden/lezzet satırları var (bunları «Barkodlar» sekmesinde varyanta göre yazdırın).",
        "Bu yuk xatida chop etadigan narsa yo'q: kartali mahsulot yo'q, hammasi qaytarilgan yoki faqat o'lcham/ta'm qatorlari (ularni «Shtrix-kodlar» yorlig'ida variantlar bo'yicha chop eting).");

    /// <summary>Пояснение к уже открытому списку: новые товары в него не вошли.</summary>
    public static string NotInCatalogNote(int count) => T($"Ещё {count} новых товаров появятся в каталоге после «Оприходовать» — их ценники напечатаете потом.",
        $"Дагы {count} жаңы товар «Кирешке алуудан» кийин каталогдо пайда болот — алардын баа белгилерин кийин басасыз.",
        $"{count} more new products will appear in the catalog after “Post the goods” — print their tags later.",
        $"{count} yeni ürün daha «Kaydet»ten sonra katalogda görünecek — etiketlerini sonra yazdırın.",
        $"Yana {count} ta yangi mahsulot «Kirim qilish»dan keyin katalogda paydo bo'ladi — ularning yorliqlarini keyin chop eting.");
}
