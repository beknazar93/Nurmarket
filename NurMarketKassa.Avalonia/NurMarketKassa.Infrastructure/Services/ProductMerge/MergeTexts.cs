namespace NurMarketKassa.Services.ProductMerge;

/// <summary>2026-11-21: тексты окна «Объединение товаров» на 5 языках (ru, ky, en, tr, uz) — в одном месте, чтобы окно и тесты брали одни и те же строки.</summary>
public static class MergeTexts
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string Blocker(MergeBlocker b) => b switch
    {
        MergeBlocker.Weighed => T("весовой товар (PLU) — не объединяется", "салмактуу товар (PLU) — бириктирилбейт", "weighed product (PLU) — not merged", "tartılan ürün (PLU) — birleştirilmez", "tortiladigan mahsulot (PLU) — birlashtirilmaydi"),
        MergeBlocker.Bundle => T("комплект", "топтом", "bundle", "set", "to'plam"),
        MergeBlocker.Service => T("услуга", "кызмат", "service", "hizmet", "xizmat"),
        MergeBlocker.OtherUnit => T("другая единица измерения", "башка өлчөм бирдиги", "different unit", "farklı birim", "boshqa o'lchov birligi"),
        _ => T("нет штрихкода", "штрихкод жок", "no barcode", "barkod yok", "shtrix-kod yo'q"),
    };

    public static string Warning(MergeWarning w) => w switch
    {
        MergeWarning.PricesDiffer => T("цены разные: варианты будут продаваться по цене главного товара", "баалары ар башка: варианттар башкы товардын баасы менен сатылат", "prices differ: variants will be sold at the main product's price", "fiyatlar farklı: varyantlar ana ürünün fiyatıyla satılır", "narxlar har xil: variantlar asosiy mahsulot narxida sotiladi"),
        MergeWarning.ExpiryBatches => T("есть срок годности/партии: остатки сложатся в один", "жарактуулук мөөнөтү/партиялар бар: калдыктар бирге кошулат", "expiry dates/batches: stocks will be added into one", "son kullanma/parti var: stoklar tek stokta toplanır", "yaroqlilik muddati/partiya bor: qoldiqlar bittaga qo'shiladi"),
        MergeWarning.PurchaseDiffers => T("закупочные цены разные: закупка станет средневзвешенной", "сатып алуу баалары ар башка: баасы орточо болот", "purchase prices differ: the cost becomes a weighted average", "alış fiyatları farklı: maliyet ağırlıklı ortalama olur", "xarid narxlari har xil: narx o'rtacha tortilgan bo'ladi"),
        MergeWarning.BrandMissing => T("бренд указан не у всех товаров", "бренд бардык товарларда көрсөтүлгөн эмес", "the brand is not set on every product", "marka her üründe girilmemiş", "brend hamma mahsulotda ko'rsatilmagan"),
        _ => T("категории не совпадают или указаны не у всех", "категориялар дал келбейт же баарында көрсөтүлгөн эмес", "categories differ or are missing", "kategoriler farklı veya eksik", "kategoriyalar mos emas yoki ko'rsatilmagan"),
    };

    public static string Confidence(MergeConfidence c) => c switch
    {
        MergeConfidence.High => T("уверенно", "ишенимдүү", "confident", "güvenilir", "ishonchli"),
        MergeConfidence.Medium => T("вероятно", "ыктымал", "likely", "olası", "ehtimol"),
        _ => T("сомнительно", "шектүү", "doubtful", "şüpheli", "shubhali"),
    };

    public static string Problem(MergeProblem p) => p switch
    {
        MergeProblem.NoMain => T("выберите главный товар", "башкы товарды тандаңыз", "choose the main product", "ana ürünü seçin", "asosiy mahsulotni tanlang"),
        MergeProblem.MainBlocked => T("главный товар нельзя объединять (весовой, комплект, услуга)", "башкы товарды бириктирүүгө болбойт (салмактуу, топтом, кызмат)", "the main product can't be merged (weighed, bundle, service)", "ana ürün birleştirilemez (tartılan, set, hizmet)", "asosiy mahsulotni birlashtirib bo'lmaydi (tortiladigan, to'plam, xizmat)"),
        MergeProblem.NothingToMerge => T("нечего объединять: в группе остался один товар", "бириктирүүгө эч нерсе жок: топто бир товар калды", "nothing to merge: one product left in the group", "birleştirilecek bir şey yok: grupta tek ürün kaldı", "birlashtirishga hech narsa yo'q: guruhda bitta mahsulot qoldi"),
        _ => "",
    };

    /// <summary>«Общая основа «Асу»: товаров 3; вкус/цвет: Клубника, Лимон» — причина группы.</summary>
    public static string Reason(MergeGroup g)
    {
        var names = g.Members.Select(m => m.VariantName).Where(n => n.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(4).ToList();
        var list = string.Join(", ", names);
        if (g.ByDictionary && list.Length > 0)
            return T($"Общая основа «{g.BaseTitle}»: товаров {g.Members.Count}; вкус/цвет: {list}", $"«{g.BaseTitle}» жалпы негизи: товар {g.Members.Count}; даам/түс: {list}",
                $"Common base “{g.BaseTitle}”: {g.Members.Count} products; flavour/colour: {list}", $"Ortak ad «{g.BaseTitle}»: {g.Members.Count} ürün; tat/renk: {list}", $"Umumiy asos «{g.BaseTitle}»: {g.Members.Count} ta mahsulot; ta'm/rang: {list}");
        if (g.ByDictionary || g.Members.All(m => m.VariantName.Length == 0))
            return T($"Одинаковое название «{g.BaseTitle}» при разных штрихкодах: товаров {g.Members.Count}", $"«{g.BaseTitle}» аталышы бирдей, штрихкоддор ар башка: товар {g.Members.Count}",
                $"Same name “{g.BaseTitle}” with different barcodes: {g.Members.Count} products", $"Aynı ad «{g.BaseTitle}», farklı barkodlar: {g.Members.Count} ürün", $"Nomi bir xil «{g.BaseTitle}», shtrix-kodlar har xil: {g.Members.Count} ta mahsulot");
        var tails = string.Join(", ", g.Members.Select(m => m.VariantName).Where(n => n.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(4));
        return T($"Общее начало названия «{g.BaseTitle}»: товаров {g.Members.Count}; отличия: {tails}", $"Аталыштын жалпы башталышы «{g.BaseTitle}»: товар {g.Members.Count}; айырмасы: {tails}",
            $"Common name start “{g.BaseTitle}”: {g.Members.Count} products; differences: {tails}", $"Ortak ad başlangıcı «{g.BaseTitle}»: {g.Members.Count} ürün; farklar: {tails}", $"Nomning umumiy boshi «{g.BaseTitle}»: {g.Members.Count} ta mahsulot; farqlar: {tails}");
    }

    public static string Qty(double v) => v.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

    public static string Money(double v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Строка предпросмотра по остаткам и закупке.</summary>
    public static string StockLine(MergePlan p) =>
        T($"Остаток главного: {Qty(p.MainStockBefore)} → {Qty(p.TotalStock)} (остатки вариантов переносятся)", $"Башкынын калдыгы: {Qty(p.MainStockBefore)} → {Qty(p.TotalStock)} (варианттардын калдыктары которулат)",
            $"Main stock: {Qty(p.MainStockBefore)} → {Qty(p.TotalStock)} (variant stocks are moved)", $"Ana stok: {Qty(p.MainStockBefore)} → {Qty(p.TotalStock)} (varyant stokları taşınır)", $"Asosiy qoldiq: {Qty(p.MainStockBefore)} → {Qty(p.TotalStock)} (variant qoldiqlari ko'chiriladi)");

    public static string PurchaseLine(MergePlan p)
    {
        if (p.NewPurchase is { } np)
            return T($"Закупка (средневзвешенная): {(p.OldPurchase is { } o ? Money(o) : "—")} → {Money(np)}", $"Сатып алуу (орточо): {(p.OldPurchase is { } o2 ? Money(o2) : "—")} → {Money(np)}",
                $"Purchase price (weighted average): {(p.OldPurchase is { } o3 ? Money(o3) : "—")} → {Money(np)}", $"Alış (ağırlıklı ortalama): {(p.OldPurchase is { } o4 ? Money(o4) : "—")} → {Money(np)}", $"Xarid (o'rtacha tortilgan): {(p.OldPurchase is { } o5 ? Money(o5) : "—")} → {Money(np)}");
        return p.PurchaseUnknown
            ? T("Закупка не меняется: у товара с остатком она не указана", "Сатып алуу баасы өзгөрбөйт: калдыгы бар товарда көрсөтүлгөн эмес", "Purchase price stays: it is not set on a product with stock", "Alış fiyatı değişmez: stoklu üründe girilmemiş", "Xarid narxi o'zgarmaydi: qoldig'i bor mahsulotda ko'rsatilmagan")
            : T("Закупка главного не меняется", "Башкынын сатып алуу баасы өзгөрбөйт", "The main purchase price stays", "Ana alış fiyatı değişmez", "Asosiy xarid narxi o'zgarmaydi");
    }

    public static string SourcesLine() =>
        T("Товары-источники отправляются в архив (история продаж и приходов остаётся на них)", "Булак товарлар архивге жөнөтүлөт (сатуулар менен кирешелердин тарыхы алардын ичинде калат)",
            "Source products go to the archive (their sales and receipt history stays on them)", "Kaynak ürünler arşive gönderilir (satış ve giriş geçmişi onlarda kalır)", "Manba mahsulotlar arxivga yuboriladi (sotuv va kirim tarixi ularda qoladi)");

    public static string PrepareOnlyReason() =>
        T("Сервер пока не поддерживает безопасное слияние (нет архива товаров): окно работает в режиме «только подготовка» — можно просмотреть группы и выгрузить план в CSV.",
            "Сервер азырынча коопсуз бириктирүүнү колдобойт (товарлардын архиви жок): терезе «даярдоо гана» режиминде — топторду көрүп, планды CSV менен чыгарса болот.",
            "The server does not support a safe merge yet (no product archive): the window works in “prepare only” mode — you can review groups and export the plan to CSV.",
            "Sunucu henüz güvenli birleştirmeyi desteklemiyor (ürün arşivi yok): pencere «yalnızca hazırlık» modunda — grupları görüp planı CSV olarak dışa aktarabilirsiniz.",
            "Server hozircha xavfsiz birlashtirishni qo'llamaydi (mahsulotlar arxivi yo'q): oyna «faqat tayyorgarlik» rejimida — guruhlarni ko'rib, rejani CSV ga chiqarish mumkin.");

    // ───────────── серверное слияние (2026-11-21) ─────────────

    /// <summary>Причина отказа сервера (409 merge_not_supported / undo_blocked) человеческим языком. Известные коды узнаются по слову, остальное показывается как прислал сервер.</summary>
    public static string ServerReason(string raw)
    {
        var r = (raw ?? "").Trim();
        var l = r.ToLowerInvariant();
        if (l.Contains("variant") || l.Contains("size") || l.Contains("color") || l.Contains("colour") || l.Contains("вариант") || l.Contains("размер") || l.Contains("цвет"))
            return T("есть варианты (размер/цвет)", "варианттар бар (өлчөм/түс)", "has variants (size/colour)", "varyantları var (beden/renk)", "variantlari bor (o'lchami/rangi)");
        if (l.Contains("batch") || l.Contains("shade") || l.Contains("lot") || l.Contains("партии") || l.Contains("оттен"))
            return T("есть партии-оттенки", "түс-партиялары бар", "has shade batches", "ton partileri var", "ton-partiyalari bor");
        if (l.Contains("sale_unit") || l.Contains("package") || l.Contains("pack") || l.Contains("упаков"))
            return T("есть упаковки (sale_units)", "таңгактары бар (sale_units)", "has packagings (sale_units)", "ambalajları var (sale_units)", "qadoqlari bor (sale_units)");
        if (l.Contains("unit") || l.Contains("weight") || l.Contains("единиц") || l.Contains("весов"))
            return T("разные единицы (весовой/штучный)", "ар кандай бирдик (салмактуу/даана)", "different units (weighed/piece)", "farklı birimler (tartılan/adet)", "turli birliklar (tortiladigan/dona)");
        if (l.Contains("barcode") || l.Contains("code") || l.Contains("штрихкод"))
            return T("штрихкод уже отдан другому товару", "штрихкод башка товарга берилген", "the barcode now belongs to another product", "barkod başka bir ürüne verilmiş", "shtrix-kod boshqa mahsulotga berilgan");
        if (l.Contains("stock") || l.Contains("qty") || l.Contains("quantity") || l.Contains("остат"))
            return T("у главного товара не хватает остатка", "башкы товарда калдык жетишпейт", "the main product does not have enough stock", "ana üründe yeterli stok yok", "asosiy mahsulotda qoldiq yetarli emas");
        return r;
    }

    private static string Reasons(IReadOnlyList<string> reasons) => string.Join("; ", reasons.Select(ServerReason).Where(x => x.Length > 0).Distinct());

    /// <summary>«Сервер не объединит «Асу»: есть варианты (размер/цвет); …» — группа блокируется, ручной путь тоже не запускается (он сломал бы то же самое).</summary>
    public static string ServerBlocked(string title, IReadOnlyList<string> reasons)
    {
        var list = reasons.Count > 0 ? Reasons(reasons) : T("причины сервер не назвал", "себептерин сервер айткан жок", "the server did not give reasons", "sunucu nedenleri belirtmedi", "server sabablarni aytmadi");
        return T($"Сервер не объединит «{title}»: {list}. Ничего не изменено.", $"Сервер «{title}» бириктирбейт: {list}. Эч нерсе өзгөргөн жок.", $"The server will not merge “{title}”: {list}. Nothing changed.", $"Sunucu «{title}» ürününü birleştirmez: {list}. Hiçbir şey değişmedi.", $"Server «{title}» ni birlashtirmaydi: {list}. Hech narsa o'zgarmadi.");
    }

    /// <summary>Почему откат не выполнен: undo_blocked (причины), слияние не найдено/старше 30 суток, сервер без отката.</summary>
    public static string UndoRefused(ServerMergeResponse r) => r.Kind switch
    {
        ServerMergeKind.UndoBlocked => T("Сервер не даёт откатить слияние: ", "Сервер бириктирүүнү кайтарууга жол бербейт: ", "The server does not allow the rollback: ", "Sunucu geri almaya izin vermiyor: ", "Server qaytarishga ruxsat bermaydi: ")
            + (r.Reasons.Count > 0 ? Reasons(r.Reasons) : T("причина не названа", "себеби айтылган эмес", "no reason given", "neden belirtilmedi", "sabab aytilmagan")) + ". "
            + T("Слияние осталось как было. Верните коды/остаток вручную или обратитесь в поддержку.", "Бириктирүү мурункудай калды. Коддорду/калдыкты кол менен кайтарыңыз же колдоого кайрылыңыз.", "The merge stays as it was. Return the codes/stock by hand or contact support.", "Birleştirme olduğu gibi kaldı. Kodları/stoku elle döndürün veya destekle iletişime geçin.", "Birlashtirish avvalgidek qoldi. Kodlar/qoldiqni qo'lda qaytaring yoki yordamga murojaat qiling."),
        ServerMergeKind.NotFound => T("Сервер не нашёл это слияние: откат возможен только 30 суток после слияния.", "Сервер бул бириктирүүнү таппады: кайтаруу бириктирүүдөн кийин 30 күн гана мүмкүн.", "The server did not find this merge: a rollback is possible only for 30 days after the merge.", "Sunucu bu birleştirmeyi bulamadı: geri alma yalnızca birleştirmeden sonraki 30 gün içinde mümkün.", "Server bu birlashtirishni topmadi: qaytarish faqat birlashtirishdan keyin 30 kun ichida mumkin."),
        _ => T("Сервер не поддерживает откат слияния.", "Сервер бириктирүүнү кайтарууну колдобойт.", "The server does not support merge rollback.", "Sunucu birleştirme geri almayı desteklemiyor.", "Server birlashtirishni qaytarishni qo'llamaydi."),
    };

    /// <summary>Баннер ручного режима: сервер без серверного слияния (404/405) — объединяем через карточки.</summary>
    public static string ManualModeBanner() =>
        T("Ручной режим: этот сервер не умеет объединять товары сам — слияние идёт через карточки (медленнее, у каждого источника несколько запросов).", "Кол режими: бул сервер товарларды өзү бириктире албайт — бириктирүү карточкалар аркылуу жүрөт (жайыраак, ар бир булакка бир нече суроо).",
            "Manual mode: this server cannot merge products itself — the merge goes through the cards (slower, several requests per source).", "Elle mod: bu sunucu ürünleri kendisi birleştiremiyor — birleştirme kartlar üzerinden yapılır (daha yavaş, her kaynak için birkaç istek).", "Qo'lda rejim: bu server mahsulotlarni o'zi birlashtira olmaydi — birlashtirish kartalar orqali boradi (sekinroq, har bir manba uchun bir necha so'rov).");

    public static string ServerCheckPending() =>
        T("Проверяю на сервере…", "Серверде текшерип жатам…", "Checking on the server…", "Sunucuda kontrol ediliyor…", "Serverda tekshirilmoqda…");

    public static string ServerCheckOk() =>
        T("Сервер объединит эту группу сам (одной операцией, с откатом 30 суток).", "Сервер бул топту өзү бириктирет (бир операция менен, 30 күн кайтаруу мүмкүнчүлүгү менен).", "The server will merge this group itself (one operation, rollback for 30 days).", "Sunucu bu grubu kendisi birleştirir (tek işlemle, 30 gün geri alma ile).", "Server bu guruhni o'zi birlashtiradi (bitta amal bilan, 30 kun qaytarish bilan).");

    public static string ServerCheckBlocked(IReadOnlyList<string> reasons) =>
        T("Сервер не объединит эту группу: ", "Сервер бул топту бириктирбейт: ", "The server will not merge this group: ", "Sunucu bu grubu birleştirmez: ", "Server bu guruhni birlashtirmaydi: ")
        + (reasons.Count > 0 ? Reasons(reasons) : T("причины не названы", "себептери айтылган эмес", "no reasons given", "nedenler belirtilmedi", "sabablar aytilmagan")) + ".";

    public static string ServerCheckBlockedChip() =>
        T("сервер не объединит", "сервер бириктирбейт", "server won't merge", "sunucu birleştirmez", "server birlashtirmaydi");

    public static string ModeName(string mode) => mode switch
    {
        "server" => T("сервер", "сервер", "server", "sunucu", "server"),
        _ => T("ручной режим", "кол режими", "manual mode", "elle mod", "qo'lda rejim"),
    };

    public static string OfflineReason() =>
        T("Нет связи с сервером: объединение выполняется только при связи. Группы можно смотреть.", "Сервер менен байланыш жок: бириктирүү байланыш болгондо гана аткарылат. Топторду көрүүгө болот.",
            "No connection: merging works only online. You can still look at the groups.", "Bağlantı yok: birleştirme yalnızca çevrimiçi yapılır. Grupları görebilirsiniz.", "Aloqa yo'q: birlashtirish faqat aloqa bo'lganda bajariladi. Guruhlarni ko'rish mumkin.");

    public static string NoRightsReason() =>
        T("Объединять товары может только владелец или администратор (право «Склад: редактирование»).", "Товарларды ээси же администратор гана бириктире алат («Склад: оңдоо» укугу).",
            "Only the owner or an administrator can merge products (“Warehouse: edit” permission).", "Ürünleri yalnızca sahip veya yönetici birleştirebilir («Depo: düzenleme» yetkisi).", "Mahsulotlarni faqat egasi yoki administrator birlashtira oladi («Ombor: tahrirlash» huquqi).");
}
