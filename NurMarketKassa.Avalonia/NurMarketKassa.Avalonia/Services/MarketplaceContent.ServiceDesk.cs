namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-13: контент страниц «подробно» для «Ремонта и сервиса» и «Trade-in / скупки» (Маркетплейс). Только тексты на 5 языках; картинок и анимации пока нет.
internal static partial class MarketplaceContent
{
    private static string SupportTerms() => T(
        "Цена и подключение — через поддержку: оставьте заявку «Подключить», перезвоним и включим функцию. Постоянный ключ тоже подходит.",
        "Баасы жана туташтыруу — колдоо кызматы аркылуу: «Туташтыруу» өтүнмөсүн калтырыңыз, чалабыз жана функцияны иштетебиз. Туруктуу ачкыч да жарайт.",
        "Price and connection go through support: leave a “Connect” request, we will call and turn the feature on. A permanent key works too.",
        "Fiyat ve bağlantı destek üzerinden: «Bağla» talebi bırakın, arayıp özelliği açarız. Kalıcı anahtar da olur.",
        "Narx va ulash — qo'llab-quvvatlash orqali: «Ulash» arizasini qoldiring, qo'ng'iroq qilib funksiyani yoqamiz. Doimiy kalit ham mos keladi.");

    private static void Repair(MarketplaceItem i)
    {
        i.IconKey = "cog-outline";
        i.Tagline = T("Каждый ремонт — под номером и с печатной квитанцией", "Ар бир оңдоо — номер менен жана басылган квитанция менен", "Every repair has a number and a printed receipt", "Her tamirin numarası ve basılı makbuzu var", "Har bir ta'mir — raqam va chop etilgan kvitansiya bilan");
        i.KeyFacts =
        [
            T("Квитанция Р-0001", "Р-0001 квитанциясы", "Receipt R-0001", "Makbuz R-0001", "Kvitansiya R-0001"),
            T("4 статуса", "4 статус", "4 statuses", "4 durum", "4 status"),
            T("Акт выдачи", "Берүү актысы", "Hand-over act", "Teslim tutanağı", "Topshirish dalolatnomasi"),
        ];
        i.Benefits =
        [
            B("file-document-outline", T("Квитанция при приёме", "Кабыл алууда квитанция", "A receipt at intake", "Kabulde makbuz", "Qabulda kvitansiya"),
                T("Клиент, телефон, устройство, серийный номер или IMEI, неисправность, комплектация, срок и предоплата — на одной квитанции с подписями.",
                  "Кардар, телефон, түзмөк, сериялык номер же IMEI, бузулуу, комплектация, мөөнөт жана алдын ала төлөм — колдор коюлган бир квитанцияда.",
                  "Customer, phone, device, serial number or IMEI, fault, included items, due date and prepayment — on one signed receipt.",
                  "Müşteri, telefon, cihaz, seri numarası veya IMEI, arıza, teslim edilenler, süre ve ön ödeme — imzalı tek makbuzda.",
                  "Mijoz, telefon, qurilma, seriya raqami yoki IMEI, nosozlik, komplektatsiya, muddat va oldindan to'lov — imzolangan bitta kvitansiyada.")),
            B("cash", T("Деньги — как в кассе", "Акча — кассадагыдай", "Money as in the till", "Para kasadaki gibi", "Pul — kassadagidek"),
                T("Предоплата вносится в кассу, оплата работ — обычной продажей услуги при выдаче, возврат — изъятием. Ничего мимо кассы.",
                  "Алдын ала төлөм кассага салынат, иштин төлөмү — берүүдө кадимки кызмат сатуусу, кайтаруу — алуу менен. Кассадан тышкары эч нерсе жок.",
                  "The prepayment goes into the till, the work is paid as an ordinary service sale at hand-over, a refund is a cash withdrawal. Nothing outside the till.",
                  "Ön ödeme kasaya girer, iş ücreti teslimde sıradan hizmet satışı olarak alınır, iade kasadan çekimdir. Kasa dışında hiçbir şey yok.",
                  "Oldindan to'lov kassaga kiritiladi, ish to'lovi — topshirishda oddiy xizmat sotuvi, qaytarish — chiqim bilan. Kassadan tashqarida hech narsa yo'q.")),
            B("message-text-outline", T("Клиенту — «готово»", "Кардарга — «даяр»", "“Ready” to the customer", "Müşteriye — «hazır»", "Mijozga — «tayyor»"),
                T("Одно нажатие открывает чат WhatsApp с готовым текстом. Сообщение уходит, только если вы сами нажмёте «Отправить».",
                  "Бир басуу даяр тексти бар WhatsApp чатын ачат. Билдирүү сиз өзүңүз «Жөнөтүү» басканда гана кетет.",
                  "One tap opens a WhatsApp chat with the text ready. The message is sent only if you press Send yourself.",
                  "Tek dokunuş hazır metinli WhatsApp sohbetini açar. Mesaj yalnızca siz Gönder'e basarsanız gider.",
                  "Bir bosish tayyor matnli WhatsApp chatini ochadi. Xabar faqat siz «Yuborish» ni o'zingiz bossangiz ketadi.")),
            B("history", T("Журнал и поиск", "Журнал жана издөө", "Journal and search", "Günlük ve arama", "Jurnal va qidiruv"),
                T("Все квитанции со статусами и сроками: поиск по номеру, клиенту, телефону и IMEI; просроченные видны сразу. Журнал есть и в программе владельца.",
                  "Бардык квитанциялар статустары жана мөөнөттөрү менен: номер, кардар, телефон жана IMEI боюнча издөө; мөөнөтү өткөндөрү дароо көрүнөт. Журнал ээсинин программасында да бар.",
                  "All receipts with statuses and due dates: search by number, customer, phone and IMEI; overdue ones stand out. The journal is in the owner's program too.",
                  "Tüm makbuzlar durum ve sürelerle: numara, müşteri, telefon ve IMEI ile arama; süresi geçenler hemen görünür. Günlük sahip programında da var.",
                  "Barcha kvitansiyalar status va muddatlari bilan: raqam, mijoz, telefon va IMEI bo'yicha qidiruv; muddati o'tganlari darhol ko'rinadi. Jurnal egasining dasturida ham bor.")),
        ];
        i.HowTo =
        [
            T("Подключите функцию: «Подключить» (заявка в поддержку) или ввод ключа.", "Функцияны туташтырыңыз: «Туташтыруу» (колдоо кызматына өтүнмө) же ачкыч киргизүү.", "Connect the feature: “Connect” (a request to support) or enter a key.", "Özelliği bağlayın: «Bağla» (destek talebi) veya anahtar girişi.", "Funksiyani ulang: «Ulash» (qo'llab-quvvatlashga ariza) yoki kalit kiritish."),
            T("В меню кассы появится «Ремонт и сервис» — нажмите «Новый приём».", "Касса менюсунда «Оңдоо жана тейлөө» пайда болот — «Жаңы кабыл алуу» басыңыз.", "“Repair and service” appears in the till menu — press “New intake”.", "Kasa menüsünde «Tamir ve servis» görünür — «Yeni kabul»a basın.", "Kassa menyusida «Ta'mirlash va servis» paydo bo'ladi — «Yangi qabul» ni bosing."),
            T("Когда готово — «Готово», сообщение клиенту, при выдаче — оплата и акт.", "Даяр болгондо — «Даяр», кардарга билдирүү, берүүдө — төлөм жана акт.", "When done — “Ready”, a message to the customer, at hand-over — payment and the act.", "Hazır olunca — «Hazır», müşteriye mesaj, teslimde — ödeme ve tutanak.", "Tayyor bo'lganda — «Tayyor», mijozga xabar, topshirishda — to'lov va dalolatnoma."),
        ];
        i.Audience =
        [
            T("Магазины электроники, телефонов и бытовой техники", "Электроника, телефон жана тиричилик техникасы дүкөндөрү", "Electronics, phone and appliance shops", "Elektronik, telefon ve beyaz eşya mağazaları", "Elektronika, telefon va maishiy texnika do'konlari"),
            T("Мастерские: часы, обувь, одежда", "Устакана: саат, бут кийим, кийим", "Workshops: watches, shoes, clothes", "Atölyeler: saat, ayakkabı, giysi", "Ustaxonalar: soat, poyabzal, kiyim"),
        ];
        i.PriceTerms = SupportTerms();
    }

    private static void TradeIn(MarketplaceItem i)
    {
        i.IconKey = "cash-multiple";
        i.Tagline = T("Купили б/у у клиента — расчёт и приход на склад без путаницы", "Кардардан б/у сатып алдык — эсептешүү жана кампага кирүү чаташпай", "Bought a used item from a customer — settlement and stock receipt without confusion", "Müşteriden ikinci el aldık — ödeme ve stok girişi karışıklık olmadan", "Mijozdan b/u sotib oldik — hisob-kitob va omborga kirim chalkashliksiz");
        i.KeyFacts =
        [
            T("Акт приёма", "Кабыл алуу актысы", "Acceptance act", "Kabul tutanağı", "Qabul dalolatnomasi"),
            T("Выплата или зачёт", "Төлөө же эсептөө", "Payout or credit", "Ödeme veya mahsup", "To'lov yoki hisobga olish"),
            T("На склад как б/у", "Кампага б/у катары", "Into stock as used", "Depoya ikinci el", "Omborga b/u"),
        ];
        i.Benefits =
        [
            B("account-voice", T("Данные продавца", "Сатуучунун маалыматы", "Seller details", "Satıcı bilgileri", "Sotuvchi ma'lumotlari"),
                T("Имя, телефон, паспорт или ID, серийный номер или IMEI с проверкой, состояние, комплектация, фото по желанию — и печатный акт.",
                  "Аты, телефону, паспорт же ID, текшерүүсү бар сериялык номер же IMEI, абалы, комплектация, каалоо боюнча сүрөт — жана басылган акт.",
                  "Name, phone, passport or ID, serial number or IMEI with a check, condition, included items, optional photos — and a printed act.",
                  "Ad, telefon, pasaport veya kimlik, kontrollü seri numarası veya IMEI, durum, teslim edilenler, isteğe bağlı fotoğraf — ve basılı tutanak.",
                  "Ism, telefon, pasport yoki ID, tekshiruvli seriya raqami yoki IMEI, holati, komplektatsiya, ixtiyoriy rasm — va chop etilgan dalolatnoma.")),
            B("cash", T("Выплата или зачёт", "Төлөө же эсептөө", "Payout or credit", "Ödeme veya mahsup", "To'lov yoki hisobga olish"),
                T("Выплата — изъятие из кассы с основанием. Зачёт — отрицательная строка в чеке покупки; итог чека не уходит в минус, а зачёт засчитывается после «чек оплачен».",
                  "Төлөө — негиздемеси бар кассадан алуу. Эсептөө — сатып алуу чегиндеги терс сап; чектин жыйынтыгы минуска кетпейт, эсептөө «чек төлөндү» болгондон кийин эсептелет.",
                  "A payout is a cash withdrawal with a reason. A credit is a negative line in the purchase receipt; the receipt total never goes below zero, and a credit counts after “receipt paid”.",
                  "Ödeme gerekçeli kasa çekimidir. Mahsup alışveriş fişinde negatif satırdır; fiş toplamı sıfırın altına inmez, mahsup «fiş ödendi»den sonra geçerli olur.",
                  "To'lov — asosli kassa chiqimi. Hisobga olish — xarid chekidagi manfiy qator; chek jami noldan pastga tushmaydi, hisobga olish «chek to'landi» dan keyin hisoblanadi.")),
            B("package-variant-closed", T("На склад как б/у", "Кампага б/у катары", "Into stock as used", "Depoya ikinci el", "Omborga b/u"),
                T("Принятое оприходуется отдельным товаром «Б/у …»: закупка равна сумме выкупа, штрихкод — серийный номер. Приход идёт обычной приёмкой, без долга поставщику.",
                  "Кабыл алынганы өзүнчө «Б/у …» товары катары кирет: сатып алуу баасы сатып алуу суммасына барабар, штрихкод — сериялык номер. Кирүү кадимки кабыл алуу менен, жеткирүүчүгө карызсыз.",
                  "The item goes into stock as a separate “Used …” product: purchase price equals the buy-back amount, barcode is the serial number. It is received by a normal receiving, with no supplier debt.",
                  "Alınan ürün ayrı «İkinci el …» ürünü olarak girer: alış fiyatı alım tutarına eşit, barkod seri numarası. Giriş sıradan mal kabulüyle, tedarikçi borcu olmadan yapılır.",
                  "Qabul qilingan narsa alohida «B/u …» mahsuloti sifatida kiradi: xarid narxi sotib olish summasiga teng, shtrix-kod — seriya raqami. Kirim oddiy qabul bilan, yetkazib beruvchiga qarzsiz.")),
            B("history", T("Журнал выкупов", "Сатып алуулар журналы", "Buy-back journal", "Alım günlüğü", "Sotib olishlar jurnali"),
                T("Все выкупы со статусами «оценён / выкуплен / на складе»; поиск по номеру, клиенту, телефону и IMEI. Журнал есть и в программе владельца.",
                  "Бардык сатып алуулар «бааланды / сатып алынды / кампада» статустары менен; номер, кардар, телефон жана IMEI боюнча издөө. Журнал ээсинин программасында да бар.",
                  "All buy-backs with statuses “appraised / bought / in stock”; search by number, customer, phone and IMEI. The journal is in the owner's program too.",
                  "Tüm alımlar «değerlendi / alındı / stokta» durumlarıyla; numara, müşteri, telefon ve IMEI ile arama. Günlük sahip programında da var.",
                  "Barcha sotib olishlar «baholandi / sotib olindi / omborda» statuslari bilan; raqam, mijoz, telefon va IMEI bo'yicha qidiruv. Jurnal egasining dasturida ham bor.")),
        ];
        i.HowTo =
        [
            T("Подключите функцию: «Подключить» (заявка в поддержку) или ввод ключа.", "Функцияны туташтырыңыз: «Туташтыруу» (колдоо кызматына өтүнмө) же ачкыч киргизүү.", "Connect the feature: “Connect” (a request to support) or enter a key.", "Özelliği bağlayın: «Bağla» (destek talebi) veya anahtar girişi.", "Funksiyani ulang: «Ulash» (qo'llab-quvvatlashga ariza) yoki kalit kiritish."),
            T("В меню кассы появится «Trade-in / скупка» — нажмите «Новый выкуп», заполните данные клиента и устройства.", "Касса менюсунда «Trade-in / сатып алуу» пайда болот — «Жаңы сатып алуу» басып, кардардын жана түзмөктүн маалыматын толтуруңуз.", "“Trade-in / buy-back” appears in the till menu — press “New buy-back” and fill in the customer and device.", "Kasa menüsünde «Trade-in / alım» görünür — «Yeni alım»a basıp müşteri ve cihaz bilgilerini doldurun.", "Kassa menyusida «Trade-in / sotib olish» paydo bo'ladi — «Yangi sotib olish» ni bosib mijoz va qurilma ma'lumotlarini to'ldiring."),
            T("Выплатите из кассы или зачтите в покупку, затем оприходуйте на склад.", "Кассадан төлөңүз же сатып алууга эсептеңиз, анан кампага киргизиңиз.", "Pay out from the till or credit to a purchase, then receive into stock.", "Kasadan ödeyin veya alışverişe mahsup edin, sonra depoya alın.", "Kassadan to'lang yoki xaridga hisobga oling, so'ng omborga kirim qiling."),
        ];
        i.Audience =
        [
            T("Магазины электроники, телефонов и техники", "Электроника, телефон жана техника дүкөндөрү", "Electronics, phone and appliance shops", "Elektronik, telefon ve teknik mağazaları", "Elektronika, telefon va texnika do'konlari"),
            T("Комиссионные и скупка б/у", "Комиссиондук жана б/у сатып алуу", "Commission shops and used-goods buy-back", "Komisyon ve ikinci el alım", "Komission do'konlar va b/u sotib olish"),
        ];
        i.PriceTerms = SupportTerms();
    }
}
