using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26 (просьба владельца: «Телеграм-бот, бот-продавец, ЭСФ, ККМ онлайн в Маркетплейс — с описанием; все новые функции — с описанием и как работает»):
// карточки функций, которых сервер NurCRM как модулей компании пока не отдаёт (в GET public/modules/ и users/company/modules/ их нет — сверено 26.11.2026).
// Коды ниже — ПРЕДЛАГАЕМЫЕ коды модулей (см. docs/ТЗ/ТЗ Умару — модули Маркетплейса: бот, бот-продавец, ЭСФ, онлайн-ККМ (26.11).md); серверных кодов и цен мы не выдумываем:
//  • Capability — функция приходит признаком сервера (bot_seller, bot_bargain в GET api/main/capabilities/ → features): есть признак — «Доступно», нет — «Скоро»;
//  • Fiscal — онлайн-ККМ: работает, когда NurCRM включил режим фискализации (блок fiscal), иначе — «Цена уточняется» и «Как подключить»;
//  • Builtin — настройка или окно кассы, входит в программу без доплаты (ЭСФ, рассылки, 1С, QR, электронный чек, сканер на складе в мобилке);
//  • Service — услуга поддержки (цену называет поддержка).
// Цена на карточке — только серверная (PublicModules.PriceOf), нет её — «Цена уточняется»; рекомендованные цены лежат ТОЛЬКО в документе ТЗ Умару, не здесь.
// Когда сервер заведёт модуль с таким кодом, карточку соберёт общий MarketplaceModules.Append (цена, «Включить», «+» в «Своё»), а эта — отступит.
public static class MarketplaceFeatureCards
{
    public const string BotSeller = "bot_seller", BotBargain = "bot_bargain", Esf = "esf", Ekassa = "ekassa", ClientMailing = "client_mailing",
        OneCExport = "onec_export", FillBase = "fill_base", ShopQr = "shop_qr", PaymentQr = "payment_qr", ReceiptLink = "receipt_link", StockScan = "stock_scan",
        Promotions = "promotions"; // 2026-11-27: «Конструктор акций» — код модуля из ТЗ (раздел 6); в public/modules его на 27.11 нет, работает по признаку promotions_v2

    /// <summary>Все коды таких карточек (для тестов и проверки полноты: значок, строка, страница, демо).</summary>
    public static readonly string[] Ids = [BotSeller, BotBargain, Esf, Ekassa, ClientMailing, OneCExport, FillBase, ShopQr, PaymentQr, ReceiptLink, StockScan, Promotions];

    private enum Mode { Capability, Fiscal, Builtin, Service }

    private sealed record Def(string Id, Mode Mode, string? Flag, MarketplaceGroup Group, Func<string> Title, Func<string> Requirement, Func<string> OpenLabel, string[] Related);

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Where() => T("Где найти", "Кайдан табууга болот", "Where to find", "Nerede bulunur", "Qayerdan topish mumkin");

    private static readonly Def[] Defs =
    [
        new(BotSeller, Mode.Capability, ServerCapabilities.BotSeller, MarketplaceGroup.Owner,
            () => T("Бот-продавец в витрине", "Витринадагы бот-сатуучу", "Seller bot on the storefront", "Vitrinde satıcı bot", "Vitrinadagi bot-sotuvchi"),
            () => T("Нужны Telegram-бот и онлайн-витрина магазина; функцию открывает сервер NurCRM", "Дүкөндүн Telegram-боту жана онлайн-витринасы керек; функцияны NurCRM сервери ачат", "Needs the shop's Telegram bot and online storefront; the NurCRM server opens the feature", "Mağazanın Telegram botu ve çevrimiçi vitrini gerekir; özelliği NurCRM sunucusu açar", "Do'konning Telegram-boti va onlayn vitrinasi kerak; funksiyani NurCRM serveri ochadi"),
            Where, ["telegram", BotBargain]),
        new(BotBargain, Mode.Capability, ServerCapabilities.BotBargain, MarketplaceGroup.Owner,
            () => T("Торг бота", "Боттун соодалашуусу", "Bot bargaining", "Botun pazarlığı", "Botning savdolashuvi"),
            () => T("Нужен «Бот-продавец»; торг открывает сервер NurCRM", "«Бот-сатуучу» керек; соодалашууну NurCRM сервери ачат", "Needs the “Seller bot”; the NurCRM server opens bargaining", "«Satıcı bot» gerekir; pazarlığı NurCRM sunucusu açar", "«Bot-sotuvchi» kerak; savdolashuvni NurCRM serveri ochadi"),
            Where, [BotSeller, "telegram"]),
        new(Esf, Mode.Builtin, null, MarketplaceGroup.Warehouse,
            () => T("ЭСФ (электронные счёт-фактуры)", "ЭСФ (электрондук эсеп-фактуралар)", "ESF (electronic invoices)", "ESF (elektronik faturalar)", "ESF (elektron hisob-fakturalar)"),
            () => T("Номер хранится в приходе; в налоговую (ГНС) ничего не отправляется", "Номер кирешеде сакталат; салык кызматына (ГНС) эч нерсе жөнөтүлбөйт", "The number is kept in the receipt; nothing is sent to the tax service (STS)", "Numara girişte saklanır; vergi idaresine (GNS) bir şey gönderilmez", "Raqam kirimda saqlanadi; soliq xizmatiga (GNS) hech narsa yuborilmaydi"),
            Where, []),
        new(Ekassa, Mode.Fiscal, null, MarketplaceGroup.Sales,
            () => T("Онлайн-ККМ (eKassa)", "Онлайн-ККМ (eKassa)", "Online cash register (eKassa)", "Çevrimiçi ÖKC (eKassa)", "Onlayn KKM (eKassa)"),
            () => T("Режим включает NurCRM по заявке; нужна ККМ, зарегистрированная в eKassa, либо режим сервера", "Режимди NurCRM өтүнмө боюнча күйгүзөт; eKassa'да катталган ККМ же сервер режими керек", "NurCRM turns the mode on on request; an eKassa-registered register or server mode is needed", "Modu NurCRM talep üzerine açar; eKassa'ya kayıtlı ÖKC veya sunucu modu gerekir", "Rejimni NurCRM ariza bo'yicha yoqadi; eKassa'da ro'yxatdan o'tgan KKM yoki server rejimi kerak"),
            Where, []),
        new(ClientMailing, Mode.Builtin, null, MarketplaceGroup.Owner,
            () => T("Рассылки клиентам в WhatsApp", "Кардарларга WhatsApp жөнөтмөлөрү", "WhatsApp mailings to customers", "Müşterilere WhatsApp toplu mesajı", "Mijozlarga WhatsApp xabarnomalari"),
            () => T("Только в программе владельца; нужны клиенты с телефонами и WhatsApp на этом компьютере", "Ээсинин программасында гана; телефондору бар кардарлар жана ушул компьютерде WhatsApp керек", "Only in the owner's program; needs customers with phones and WhatsApp on this computer", "Yalnız sahibin programında; telefonlu müşteriler ve bu bilgisayarda WhatsApp gerekir", "Faqat egasining dasturida; telefonli mijozlar va shu kompyuterda WhatsApp kerak"),
            Where, []),
        new(OneCExport, Mode.Builtin, null, MarketplaceGroup.Owner,
            () => T("Экспорт в 1С", "1Ске экспорт", "Export to 1C", "1C'ye aktarım", "1C ga eksport"),
            () => T("Программа владельца или кассир с правом «Аналитика»; тариф проверяет само окно", "Ээсинин программасы же «Аналитика» укугу бар кассир; тарифти терезенин өзү текшерет", "The owner's program or a cashier with the “Analytics” right; the window itself checks the plan", "Sahibin programı veya «Analiz» yetkili kasiyer; tarifeyi pencerenin kendisi kontrol eder", "Egasining dasturi yoki «Analitika» huquqli kassir; tarifni oynaning o'zi tekshiradi"),
            () => T("Открыть выгрузку", "Чыгарууну ачуу", "Open the export", "Aktarımı aç", "Eksportni ochish"), []),
        new(FillBase, Mode.Service, null, MarketplaceGroup.Warehouse,
            () => T("Загрузим вашу базу под ключ", "Базаңызды ачкыч менен жүктөйбүз", "We load your catalog turnkey", "Ürün tabanınızı anahtar teslim yükleriz", "Bazangizni kalit topshiriq bilan yuklaymiz"),
            () => T("Нужен WhatsApp на этом компьютере; Excel, фото накладных или выгрузка из 1С", "Ушул компьютерде WhatsApp керек; Excel, накладный сүрөттөрү же 1Сттен чыгарылган файл", "Needs WhatsApp on this computer; Excel, invoice photos or a 1C export", "Bu bilgisayarda WhatsApp gerekir; Excel, fatura fotoğrafları veya 1C çıktısı", "Shu kompyuterda WhatsApp kerak; Excel, hisob-faktura suratlari yoki 1C dan chiqarilgan fayl"),
            () => T("Оставить заявку", "Өтүнмө калтыруу", "Leave a request", "Talep bırak", "Ariza qoldirish"), []),
        new(ShopQr, Mode.Builtin, null, MarketplaceGroup.Sales,
            () => T("QR магазина на чеке и ценнике", "Чекте жана баа белгисинде дүкөндүн QRу", "Shop QR on the receipt and price tag", "Fişte ve etikette mağaza QR'ı", "Chekda va narx belgisida do'kon QRi"),
            () => T("Нужна онлайн-витрина магазина (адрес market.nurcrm.kg/catalog/…); нет адреса — QR не печатается", "Дүкөндүн онлайн-витринасы керек (дарек market.nurcrm.kg/catalog/…); дарек жок болсо — QR басылбайт", "Needs the shop's online storefront (address market.nurcrm.kg/catalog/…); no address — no QR is printed", "Mağazanın çevrimiçi vitrini gerekir (adres market.nurcrm.kg/catalog/…); adres yoksa QR basılmaz", "Do'konning onlayn vitrinasi kerak (manzil market.nurcrm.kg/catalog/…); manzil bo'lmasa — QR chop etilmaydi"),
            Where, [ReceiptLink]),
        new(PaymentQr, Mode.Builtin, null, MarketplaceGroup.Sales,
            () => T("QR на сумму чека", "Чектин суммасына QR", "QR with the receipt amount", "Fiş tutarlı QR", "Chek summasi bilan QR"),
            () => T("Нужна картинка QR вашего банка; подходят не все банки — касса проверяет сама", "Банкыңыздын QR сүрөтү керек; бардык банктар ылайык эмес — кассанын өзү текшерет", "Needs your bank's QR picture; not every bank fits — the till checks by itself", "Bankanızın QR resmi gerekir; her banka uymaz — kasa kendisi kontrol eder", "Bankingiz QR rasmi kerak; barcha banklar mos emas — kassaning o'zi tekshiradi"),
            Where, []),
        new(ReceiptLink, Mode.Builtin, null, MarketplaceGroup.Sales,
            () => T("Красивый электронный чек по QR", "QR аркылуу кооз электрондук чек", "Nice e-receipt by QR", "QR ile güzel e-fiş", "QR orqali chiroyli elektron chek"),
            () => T("Ссылку на чек даёт сервер NurCRM; нет ссылки — QR на чеке не печатается", "Чекке шилтемени NurCRM сервери берет; шилтеме жок болсо — чекте QR басылбайт", "The receipt link comes from the NurCRM server; no link — no QR is printed", "Fiş bağlantısını NurCRM sunucusu verir; bağlantı yoksa QR basılmaz", "Chek havolasini NurCRM serveri beradi; havola bo'lmasa — chekda QR chop etilmaydi"),
            Where, [ShopQr]),
        new(StockScan, Mode.Builtin, null, MarketplaceGroup.Warehouse,
            () => T("Сканер на складе (мобильное приложение)", "Кампада сканер (мобилдик тиркеме)", "Stock scanner (mobile app)", "Depoda tarayıcı (mobil uygulama)", "Omborda skaner (mobil ilova)"),
            () => T("Мобильное приложение NurCRM; разрешите доступ к камере", "NurCRM мобилдик тиркемеси; камерага уруксат бериңиз", "The NurCRM mobile app; allow camera access", "NurCRM mobil uygulaması; kamera erişimine izin verin", "NurCRM mobil ilovasi; kameraga ruxsat bering"),
            Where, []),
        // 2026-11-27: «Конструктор акций» (ТЗ Умару — конструктор акций, раздел 6): сервер открывает признаком promotions_v2; цены в public/modules пока нет — «Цена уточняется»
        new(Promotions, Mode.Capability, ServerCapabilities.PromotionsV2, MarketplaceGroup.Sales,
            () => T("Конструктор акций", "Акциялар конструктору", "Promotion builder", "Kampanya oluşturucu", "Aksiya konstruktori"),
            () => T("Акции создают владелец или администратор в программе владельца; функцию открывает сервер NurCRM", "Акцияларды ээси же администратор ээсинин программасында түзөт; функцияны NurCRM сервери ачат", "The owner or an administrator creates promotions in the owner's program; the NurCRM server opens the feature", "Kampanyaları sahip veya yönetici sahibin programında oluşturur; özelliği NurCRM sunucusu açar", "Aksiyalarni egasi yoki administrator egasining dasturida yaratadi; funksiyani NurCRM serveri ochadi"),
            Where, []),
    ];

    private static readonly HashSet<string> IdSet = new(Ids, StringComparer.OrdinalIgnoreCase);

    /// <summary>Код — одна из таких карточек.</summary>
    public static bool IsFeature(string id) => IdSet.Contains(id);

    /// <summary>Группа витрины карточки (для MarketplaceContent.GroupOf); null — не наша карточка.</summary>
    public static MarketplaceGroup? GroupOf(string itemId)
    {
        var id = itemId.StartsWith("module:", StringComparison.Ordinal) ? itemId["module:".Length..] : itemId;
        return Defs.FirstOrDefault(d => d.Id == id)?.Group;
    }

    /// <summary>Заголовок и текст окошка «Где найти» (шаги из описания страницы); у онлайн-ККМ без включённого режима — как подключить.</summary>
    public static (string Title, string Body) OpenInfo(string id)
    {
        if (id == Ekassa && !FiscalSettings.ShowsFiscalUi)
            return (T("Как подключить", "Кантип туташтыруу", "How to connect", "Nasıl bağlanır", "Qanday ulash"),
                T("Фискализацию включает NurCRM по заявке: напишите в поддержку — режим (касса или сервер) включится для вашей компании. Потом проверьте «Настройки → Операции → Налоги и ККМ».",
                  "Фискалдаштырууну NurCRM өтүнмө боюнча күйгүзөт: колдоого жазыңыз — режим (касса же сервер) компанияңыз үчүн күйөт. Анан «Жөндөөлөр → Операциялар → Салыктар жана ККМ» бөлүмүн текшериңиз.",
                  "NurCRM turns fiscalization on on request: write to support — the mode (till or server) is turned on for your company. Then check “Settings → Operations → Taxes and cash register”.",
                  "Mali modu NurCRM talep üzerine açar: desteğe yazın — mod (kasa veya sunucu) şirketiniz için açılır. Sonra «Ayarlar → İşlemler → Vergiler ve ÖKC»yi kontrol edin.",
                  "Fiskallashtirishni NurCRM ariza bo'yicha yoqadi: qo'llab-quvvatlashga yozing — rejim (kassa yoki server) kompaniyangiz uchun yoqiladi. Keyin «Sozlamalar → Operatsiyalar → Soliqlar va KKM»ni tekshiring."));
        var steps = MarketplaceModuleContent.TextOf(id)?.Steps ?? [];
        return (Where(), string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}")));
    }

    private static string Included() => T("Входит в программу", "Программага кирет", "Included in the program", "Programa dahil", "Dasturga kiradi");

    private static string PriceTbd() => T("Цена уточняется", "Баасы тактылууда", "Price to be confirmed", "Fiyat netleşiyor", "Narx aniqlanmoqda");

    private static string SupportPrice() => T("Цену называет поддержка", "Баасын колдоо айтат", "Support names the price", "Fiyatı destek söyler", "Narxni qo'llab-quvvatlash aytadi");

    /// <summary>Карточка — не модуль сервера (тест «без ответа сервера карточек-модулей нет» их пропускает).</summary>
    public static bool IsLocalCardId(string itemId) => itemId.StartsWith("module:", StringComparison.Ordinal) && IsFeature(itemId["module:".Length..]);

    /// <summary>Добавить карточки, которых сервер как модули не знает. Сервер уже знает такой код — карточку соберёт MarketplaceModules.Append.</summary>
    internal static void Append(IList<MarketplaceItem> items, Action<string> run, string version)
    {
        var snap = CompanyModules.Snapshot;
        foreach (var def in Defs)
        {
            if (snap?.Knows(def.Id) == true)
                continue;
            var id = def.Id;
            var active = def.Mode switch
            {
                Mode.Capability => ServerCapabilities.Has(def.Flag!),
                Mode.Fiscal => FiscalSettings.ShowsFiscalUi,
                _ => true,
            };
            var soon = def.Mode == Mode.Capability && !active;
            var monthly = PublicModules.PriceOf(id);
            var price = monthly is { } m && def.Mode is Mode.Capability or Mode.Fiscal ? new MarketplacePrice(m, MarketplacePricePeriod.PerMonth) : null;
            var text = MarketplaceModuleContent.TextOf(id);
            var statusText = soon ? T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada")
                : def.Mode switch
                {
                    Mode.Builtin => Included(),
                    Mode.Service => SupportPrice(),
                    Mode.Fiscal => active ? T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan") : price?.Label ?? PriceTbd(),
                    _ => T("Доступно", "Жеткиликтүү", "Available", "Kullanılabilir", "Mavjud"),
                };
            var installed = !soon && (def.Mode == Mode.Builtin || (def.Mode is Mode.Capability or Mode.Fiscal && active)); // услуга поддержки — не «включена», а «Доступна»
            var priceTerms = def.Mode switch
            {
                Mode.Builtin => T("Входит в программу без доплаты.", "Программага кошумча төлөмсүз кирет.", "Included in the program at no extra charge.", "Programa ek ücretsiz dahildir.", "Dasturga qo'shimcha to'lovsiz kiradi."),
                Mode.Service => T("Цену и срок называет поддержка до начала работы.", "Баасын жана мөөнөтүн колдоо иш башталганга чейин айтат.", "Support names the price and the time before the work starts.", "Fiyatı ve süreyi destek iş başlamadan söyler.", "Narx va muddatni qo'llab-quvvatlash ish boshlanishidan oldin aytadi."),
                _ => price is not null
                    ? T($"{price.Label}, цена с сервера.", $"{price.Label}, баа серверден.", $"{price.Label}, the price comes from the server.", $"{price.Label}, fiyat sunucudan gelir.", $"{price.Label}, narx serverdan.")
                    : T("Цена уточняется: она появится здесь, как только сервер NurCRM пришлёт цену модуля.", "Баасы тактылууда: NurCRM сервери модулдун баасын жөнөткөндө бул жерде пайда болот.", "Price to be confirmed: it will appear here as soon as the NurCRM server sends the module price.", "Fiyat netleşiyor: NurCRM sunucusu modül fiyatını gönderince burada görünecek.", "Narx aniqlanmoqda: NurCRM serveri modul narxini yuborishi bilan shu yerda paydo bo'ladi."),
            };
            var note = soon
                ? T("Скоро: ждём сервер NurCRM — функция заработает, когда сервер её откроет.", "Жакында: NurCRM серверин күтөбүз — функция сервер аны ачканда иштейт.", "Coming soon: waiting for the NurCRM server — the feature works once the server opens it.", "Yakında: NurCRM sunucusunu bekliyoruz — özellik sunucu açınca çalışır.", "Tez orada: NurCRM serverini kutmoqdamiz — funksiya server ochganda ishlaydi.")
                : priceTerms;
            items.Add(new MarketplaceItem
            {
                Id = "module:" + id,
                IsModule = true,
                Kind = MarketplaceKind.Feature,
                Title = def.Title(),
                IconKey = MarketplaceModuleContent.IconKeyOf(id),
                Tagline = text?.Short ?? "",
                Line = MarketplaceModuleContent.LineOf(id) ?? "",
                HowTo = text?.Steps ?? [],
                Audience = text?.Audience ?? [],
                ShortDescription = text?.Short ?? MarketplaceModuleContent.LineOf(id) ?? def.Title(),
                LongDescription = text?.What ?? MarketplaceModuleContent.LineOf(id) ?? def.Title(),
                IconGlyph = "➕",
                IconColor = MarketplaceModuleContent.ColorOf(id),
                Price = soon ? null : price,
                Status = soon ? MarketplaceStatus.ComingSoon : installed ? MarketplaceStatus.Installed : MarketplaceStatus.Available,
                StatusText = statusText,
                Version = version,
                Tags = [T("Возможность", "Мүмкүнчүлүк", "Feature", "Özellik", "Imkon")],
                Features = [],
                Requirements = [def.Requirement()],
                Note = note,
                PriceTerms = soon ? "" : priceTerms,
                Primary = soon ? null : new MarketplaceAction(def.Mode == Mode.Fiscal && !active ? T("Как подключить", "Кантип туташтыруу", "How to connect", "Nasıl bağlanır", "Qanday ulash") : def.OpenLabel(), () => run("open:" + id)),
                Secondary = [],
                RelatedIds = def.Related.Select(r => r == "telegram" ? r : "module:" + r).ToList(),
            });
        }
    }
}
