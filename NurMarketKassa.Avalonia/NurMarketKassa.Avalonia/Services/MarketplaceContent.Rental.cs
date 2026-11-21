namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-20 («Прокат (аренда)», решение владельца): контент страницы «подробно» для модуля «Прокат» (Маркетплейс). Только тексты на 5 языков.
internal static partial class MarketplaceContent
{
    private static void Rental(MarketplaceItem i)
    {
        i.IconKey = "calendar-clock-outline";
        i.Tagline = T("Сдавайте вещи и инструмент в прокат: залог, срок, возврат, штраф за просрочку", "Буюмдарды жана аспапты прокатка бериңиз: күрөө, мөөнөт, кайтаруу, кечиктирүү үчүн айып", "Rent out items and tools: deposit, term, return, late penalty", "Eşya ve aleti kiraya verin: depozito, süre, iade, gecikme cezası", "Buyum va asbobni prokatga bering: garov, muddat, qaytarish, kechikish jarimasi");
        i.KeyFacts =
        [
            T("Залог и срок", "Күрөө жана мөөнөт", "Deposit and term", "Depozito ve süre", "Garov va muddat"),
            T("Напоминание о возврате", "Кайтаруу эскертмеси", "Return reminder", "İade hatırlatması", "Qaytarish eslatmasi"),
            T("Чек проката", "Прокат чеги", "Rental receipt", "Kiralama fişi", "Prokat cheki"),
        ];
        i.Benefits =
        [
            B("calendar-clock-outline", T("Выдача и возврат", "Берүү жана кайтаруу", "Issue and return", "Teslim ve iade", "Berish va qaytarish"),
                T("Клиент, вещь, срок, сумма за день и залог (деньгами или документом). При возврате — состояние вещи и штраф за просрочку, вещь возвращается на склад.",
                  "Кардар, буюм, мөөнөт, күнүнө сумма жана күрөө (акча же документ). Кайтарууда — буюмдун абалы жана кечиктирүү үчүн айып, буюм кампага кайтат.",
                  "Customer, item, term, daily rate and deposit (money or a document). On return — the item's condition and the late penalty; the item goes back to stock.",
                  "Müşteri, eşya, süre, günlük ücret ve depozito (para veya belge). İadede — eşyanın durumu ve gecikme cezası; eşya depoya döner.",
                  "Mijoz, buyum, muddat, kunlik summa va garov (pul yoki hujjat). Qaytarishda — buyumning holati va kechikish jarimasi, buyum omborga qaytadi.")),
            B("bell-outline", T("Сроки под контролем", "Мөөнөттөр көзөмөлдө", "Terms under control", "Süreler kontrol altında", "Muddatlar nazoratda"),
                T("Значок «сегодня, завтра, просрочено» в меню, напоминание владельцу в Телеграм-боте; бот отвечает «кто не вернул» и «какие залоги».",
                  "Менюда «бүгүн, эртең, мөөнөтү өттү» белгиси, ээсине Телеграм-ботто эскертме; бот «ким кайтарган жок» жана «кандай күрөөлөр» деп жооп берет.",
                  "A “today, tomorrow, overdue” badge in the menu and a reminder to the owner in the Telegram bot; the bot answers “who has not returned” and “which deposits”.",
                  "Menüde «bugün, yarın, gecikmiş» rozeti ve sahibe Telegram botunda hatırlatma; bot «kim iade etmedi» ve «hangi depozitolar» sorularını yanıtlar.",
                  "Menyuda «bugun, ertaga, muddati o'tgan» belgisi va egasiga Telegram-botda eslatma; bot «kim qaytarmadi» va «qaysi garovlar» savollariga javob beradi.")),
            B("receipt-text-outline", T("«Оформить как прокат» в окне оплаты", "Төлөм терезесинде «Прокатка тариздөө»", "“Rent it out” in the payment window", "Ödeme penceresinde «Kiralama olarak düzenle»", "To'lov oynasida «Prokat sifatida rasmiylashtirish»"),
                T("Товары из чека можно оформить прокатом одной ссылкой. В чеке — сумма за день, итог, даты «от и до» и штраф за просрочку.",
                  "Чектеги товарларды бир шилтеме менен прокатка тариздөөгө болот. Чекте — күнүнө сумма, жыйынтык, «бул датадан ушул датага» жана кечиктирүү айыбы.",
                  "Items from the receipt can be turned into a rental with one link. The receipt shows the daily rate, the total, “from and to” dates and the late penalty.",
                  "Fişteki ürünler tek bağlantıyla kiralamaya çevrilebilir. Fişte günlük ücret, toplam, «başlangıç ve bitiş» tarihleri ve gecikme cezası görünür.",
                  "Chekdagi tovarlarni bitta havola bilan prokatga o'tkazish mumkin. Chekda — kunlik summa, jami, «dan va gacha» sanalar va kechikish jarimasi.")),
        ];
        i.HowTo =
        [
            T("Нажмите «Включить бесплатно» на этой карточке (в строймаркете прокат уже включён).", "Бул карточкадагы «Акысыз күйгүзүү» басыңыз (курулуш дүкөнүндө прокат күйгүзүлгөн).", "Press “Turn on for free” on this card (in a building-material shop rental is already on).", "Bu kartta «Ücretsiz aç»a basın (yapı marketinde kiralama zaten açıktır).", "Shu kartada «Bepul yoqish» ni bosing (qurilish do'konida prokat allaqachon yoqilgan)."),
            T("Откройте «Прокат» в меню кассы (☰ или «Ещё») или в программе владельца: Продажи → Прокат.", "Касса менюсунан (☰ же «Дагы») «Прокат» ачыңыз же ээсинин программасында: Сатуулар → Прокат.", "Open “Rentals” in the till menu (☰ or “More”) or in the owner's program: Sales → Rentals.", "Kasa menüsünde (☰ veya «Daha fazla») «Kiralama»yı açın veya sahip programında: Satışlar → Kiralama.", "Kassa menyusida (☰ yoki «Yana») «Prokat» ni oching yoki egasining dasturida: Sotuvlar → Prokat."),
            T("«Новый прокат» — клиент, вещь, срок, залог. При возврате нажмите «Вернуть».", "«Жаңы прокат» — кардар, буюм, мөөнөт, күрөө. Кайтарууда «Кайтаруу» басыңыз.", "“New rental” — customer, item, term, deposit. On return press “Return”.", "«Yeni kiralama» — müşteri, eşya, süre, depozito. İadede «İade et»e basın.", "«Yangi prokat» — mijoz, buyum, muddat, garov. Qaytarishda «Qaytarish» ni bosing."),
        ];
        i.Audience =
        [
            T("Строительные магазины: прокат инструмента", "Курулуш дүкөндөрү: аспапты прокатка берүү", "Building-material shops: tool rental", "Yapı marketleri: alet kiralama", "Qurilish do'konlari: asbob prokati"),
            T("Одежда и услуги: платья, костюмы, оборудование", "Кийим жана кызматтар: көйнөк, костюм, жабдуу", "Clothing and services: dresses, suits, equipment", "Giyim ve hizmetler: elbise, takım, ekipman", "Kiyim va xizmatlar: ko'ylak, kostyum, jihoz"),
        ];
        i.PriceTerms = T("Бесплатно. В сфере «Строймаг» прокат включён сразу; в других сферах — одно нажатие «Включить бесплатно». Если прокат у вас уже есть — он остаётся включённым.",
            "Акысыз. «Курулуш дүкөнү» чөйрөсүндө прокат дароо күйгүзүлгөн; башка чөйрөлөрдө — «Акысыз күйгүзүү» бир басуу. Прокатыңыз мурдатан болсо — ал күйгүзүлгөн бойдон калат.",
            "Free. In the “Building materials store” sphere rental is on right away; in other spheres — one tap on “Turn on for free”. If you already have rentals, it stays on.",
            "Ücretsiz. «Yapı market» alanında kiralama hemen açıktır; diğer alanlarda — «Ücretsiz aç»a tek dokunuş. Zaten kiralamanız varsa açık kalır.",
            "Bepul. «Qurilish do'koni» sohasida prokat darhol yoqilgan; boshqa sohalarda — «Bepul yoqish» ni bir bosish. Prokatingiz allaqachon bo'lsa — u yoqilgan holda qoladi.");
    }
}
