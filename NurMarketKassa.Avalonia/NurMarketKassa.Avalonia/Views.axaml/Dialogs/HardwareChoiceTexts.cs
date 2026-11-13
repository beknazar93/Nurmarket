using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-12 (ХМ14, ТЗ хозмаг 3.7): надписи выбора «Хозтовары и стройматериалы» на пяти языках — размер магазина (хозмаг у дома /
/// строительный маркет) и блок «Что изменится». Общие для окна первого запуска (<see cref="MarketSphereChoiceWindow"/>)
/// и Настроек → Операции → «Сфера магазина». Тексты ky, tr и uz проверяет владелец (В-Х9).
/// </summary>
internal static class HardwareChoiceTexts
{
    public static string SizeName(HardwareSize size) => size == HardwareSize.Market
        ? Tr.T("Строительный маркет", "Курулуш маркети", "Building supplies market", "Yapı market", "Qurilish marketi")
        : Tr.T("Хозмаг у дома", "Үй жанындагы чарба дүкөнү", "Neighbourhood hardware store", "Mahalle hırdavatçısı", "Mahalla xo'jalik do'koni");

    public static string SizeDescription(HardwareSize size) => size == HardwareSize.Market
        ? Tr.T("Склад, прорабы в долг, доставка, смета: цена прораба, долг на 14 дней, КП и счёт на виду.",
            "Кампа, прорабдорго карызга сатуу, жеткирүү, смета: прораб баасы, 14 күнгө карыз, КП жана эсеп көрүнүктүү.",
            "Warehouse, foremen on credit, delivery, estimates: foreman price, 14-day debt, quotes and invoices at hand.",
            "Depo, veresiye çalışan ustalar, teslimat, keşif: usta fiyatı, 14 gün borç, teklif ve fatura görünür.",
            "Ombor, qarzga oluvchi prorablar, yetkazish, smeta: prorab narxi, 14 kunlik qarz, KP va hisob ko'rinadi.")
        : Tr.T("20–80 м², 1–2 кассы: крепёж, сантехника, электрика, краска.",
            "20–80 м², 1–2 касса: бекиткичтер, сантехника, электр, боёк.",
            "20–80 m², 1–2 tills: fasteners, plumbing, electrical, paint.",
            "20–80 m², 1–2 kasa: bağlantı elemanları, tesisat, elektrik, boya.",
            "20–80 m², 1–2 kassa: mahkamlash buyumlari, santexnika, elektr, bo'yoq.");

    public static string SizeStepTitle => Tr.T("Размер магазина", "Дүкөндүн көлөмү", "Store size", "Mağaza boyutu", "Do'kon hajmi");

    public static string SizeStepSubtitle => Tr.T(
        "От размера зависят настройки по умолчанию. Потом его можно изменить в Настройках → Операции.",
        "Демейки жөндөөлөр көлөмгө жараша болот. Кийин аны Жөндөөлөр → Операциялар бөлүмүндө өзгөртсө болот.",
        "Defaults depend on the size. You can change it later in Settings → Operations.",
        "Varsayılan ayarlar boyuta bağlıdır. Daha sonra Ayarlar → İşlemler bölümünden değiştirilebilir.",
        "Standart sozlamalar hajmga bog'liq. Keyin uni Sozlamalar → Operatsiyalar bo'limida o'zgartirish mumkin.");

    public static string BackText => Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga");

    public static string PackScanText => Tr.T(
        "Скан коробки добавляет все штуки (коробка ×200 = 200 шт)",
        "Куту сканерленгенде бардык даана кошулат (куту ×200 = 200 даана)",
        "Scanning a box adds all the pieces (box ×200 = 200 pcs)",
        "Kutu okutulunca tüm adetler eklenir (kutu ×200 = 200 adet)",
        "Quti skanerlanganda barcha donalar qo'shiladi (quti ×200 = 200 dona)");

    public static string WhatChangesTitle => Tr.T("Что изменится в кассе", "Кассада эмне өзгөрөт", "What changes in the till", "Kasada neler değişir", "Kassada nima o'zgaradi");

    /// <summary>Строки блока «Что изменится» (ТЗ 3.7) — только то, что уже сделано в кассе.</summary>
    public static IReadOnlyList<string> WhatChanges() => new[]
    {
        Tr.T("Можно продавать 2,5 м и 1,44 м² (когда сервер включит дробное количество)",
            "2,5 м жана 1,44 м² сатууга болот (сервер бөлчөк санды күйгүзгөндө)",
            "You can sell 2.5 m and 1.44 m² (once the server turns on fractional quantities)",
            "2,5 m ve 1,44 m² satılabilir (sunucu kesirli miktarı açtığında)",
            "2,5 m va 1,44 m² sotish mumkin (server kasrli miqdorni yoqqanda)"),
        Tr.T("Скан коробки добавит все штуки (по галочке)",
            "Куту сканерлегенде бардык даана кошулат (белги боюнча)",
            "Scanning a box adds all the pieces (by the switch)",
            "Kutu okutulunca tüm adetler eklenir (anahtarla)",
            "Quti skanerlanganda barcha donalar qo'shiladi (tumbler bo'yicha)"),
        Tr.T("Окно «Количество / длина / площадь»",
            "«Саны / узундугу / аянты» терезеси",
            "A “Quantity / length / area” window",
            "«Miktar / uzunluk / alan» penceresi",
            "«Miqdor / uzunlik / maydon» oynasi"),
        Tr.T("Кнопка «Сколько нужно?» — калькулятор расхода у любого товара",
            "«Канча керек?» баскычы — каалаган товарда сарптоо калькулятору",
            "A “How much do I need?” button — a consumption calculator on any product",
            "«Ne kadar gerekir?» düğmesi — her üründe sarf hesaplayıcı",
            "«Qancha kerak?» tugmasi — har bir mahsulotda sarf kalkulyatori"),
    };

    /// <summary>Текст блока «Что изменится» одной строкой с маркерами.</summary>
    public static string WhatChangesText() => WhatChangesTitle + ":\n• " + string.Join("\n• ", WhatChanges());
}
