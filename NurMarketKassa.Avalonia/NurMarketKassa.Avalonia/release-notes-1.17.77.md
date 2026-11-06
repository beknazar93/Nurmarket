## v1.17.77

Приёмка без связи и окно «Неотправленное», защита входа, возврат долгового чека, новое окно оплаты, быстрее запуск каталога; исправлена загрузка каталога при чистой установке и смене аккаунта; исправления по независимому аудиту: признаки сервера при сбое связи, шаг округления весовых (весы TM-30F), PIN старшего, оповещения, смена компании, Android. Всё из 1.17.76 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.77.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.77.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.77.zip` (Intel); Android — `nurmarket-kassa-android-1.17.77.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Загрузка каталога.** Исправлена загрузка каталога при чистой установке и смене аккаунта: весовые товары, штрихкоды и PLU больше не теряются (касса сама перезагрузит неполный каталог один раз).
- **Приёмка без связи.** Приходы и новые товары сохраняются на кассе и уходят сами, когда связь вернётся; время приёма берётся с момента приёмки; номера приходов и поставщики видны и без связи.
- **Окно «Неотправленное».** Всё, что ждёт отправки на сервер (приходы, новые товары, продажи, деньги), видно в одном окне, и его можно отправить вручную.
- **Защита входа.** Вход не пропадает при сбое связи, а за несколько дней до конца срока входа касса предупреждает и предлагает войти заново, не теряя несохранённое.
- **Возврат долгового чека.** Возврат чека, проданного в долг: деньги и долг клиента считаются по правилам сервера; если сервер отказал, кассир видит понятную причину.
- **Окно оплаты.** Срок долга, рассрочка, оплата картой и по QR, цифровой блок для ввода суммы.
- **Быстрее запуск.** Каталог при запуске догружается, а не грузится заново; полная проверка каталога — не чаще раза в сутки.
- **Картинки, ИИ и разбор файлов.** Картинки товаров, ИИ-помощник и разбор файлов накладных работают быстрее и надёжнее.
- **Наценка в приёмке.** Цена продажи молча не ставится «закупка + %»: она не меняется, пока вы сами не включили автонаценку или не ввели её в строке; в окне приёмки это объяснено.
- **Программа владельца.** На главной появился раздел «Требует внимания»; PIN-защита программы по желанию.
- **Исправлено по независимому аудиту.** Признаки сервера не пропадают при сбое связи (оплата в долг не дублируется); шаг округления весовых берётся только по серверной единице (весы TM-30F); PIN старшего не обходится ошибкой сервера; оповещения не теряются; смена компании очищает отложенные записи.
- **Android.** Excel в программе владельца, накладная из файла, плашки не перехватывают фокус.

---

# Кыргызча

### Эмнелер жаңы

- **Каталогду жүктөө.** Таза орнотууда жана аккаунтту алмаштырууда каталогду жүктөө оңдолду: салмактуу товарлар, штрихкоддор жана PLU жоголбойт (касса толук эмес каталогду бир жолу өзү кайра жүктөйт).
- **Байланышсыз кабыл алуу.** Киреше жана жаңы товарлар кассада сакталат жана байланыш кайтканда өзү кетет; кабыл алуу убактысы кабыл алган учурдан алынат; кирешелердин номерлери жана жеткирүүчүлөр байланышсыз да көрүнөт.
- **«Жөнөтүлбөгөн» терезеси.** Серверге жөнөтүүнү күтүп жаткандардын баары (киреше, жаңы товарлар, сатуулар, акча) бир терезеде көрүнөт жана аны кол менен жөнөтсө болот.
- **Кирүүнү коргоо.** Байланыш бузулганда кирүү жоголбойт, ал эми кирүү мөөнөтү бүтөөрүнө бир нече күн калганда касса эскертип, сакталбаганды жоготпой кайра кирүүнү сунуштайт.
- **Карыз чегин кайтаруу.** Карызга сатылган чекти кайтаруу: акча жана кардардын карызы сервердин эрежеси боюнча эсептелет; сервер четке кагса, кассир түшүнүктүү себепти көрөт.
- **Төлөм терезеси.** Карыздын мөөнөтү, бөлүп төлөө, карта жана QR менен төлөө, суммаларды киргизүү үчүн цифралык блок.
- **Ылдамыраак иштетүү.** Иштетүүдө каталог кайра жүктөлбөстөн толукталат; каталогду толук текшерүү — суткасына бир жолудан көп эмес.
- **Сүрөттөр, ЖИ жана файлдарды талдоо.** Товар сүрөттөрү, ЖИ жардамчысы жана накладной файлдарын талдоо тезирээк жана ишенимдүүрөөк иштейт.
- **Кабыл алуудагы кошумча баа.** Сатуу баасы «сатып алуу + %» болуп унчукпай коюлбайт: автонаценканы өзүңүз иштетмейинче же сапка киргизбейинче өзгөрбөйт; кабыл алуу терезесинде бул түшүндүрүлөт.
- **Ээсинин программасы.** Башкы бетте «Көңүл бурууну талап кылат» бөлүмү пайда болду; программаны PIN менен коргоо — каалоо боюнча.
- **Көз карандысыз аудит боюнча оңдоолор.** Байланыш үзүлгөндө сервердин белгилери жоголбойт (карызга төлөм кайталанбайт); салмактуу саптарды тегеректөө кадамы сервердин бирдиги боюнча гана алынат (TM-30F тараза); улуунун PIN-коду сервер катасы менен айланып өтпөйт; эскертмелер жоголбойт; компанияны алмаштырууда кечиктирилген жазуулар тазаланат.
- **Android.** Ээсинин программасында Excel, файлдан накладной, плашкалар фокусту тартып албайт.

---

# English

### What's new

- **Catalog loading.** Fixed catalog loading on a clean install and when switching accounts: weighed products, barcodes and PLU are no longer lost (the till reloads an incomplete catalog once by itself).
- **Receiving without a connection.** Goods receipts and new products are saved on the till and go out by themselves when the connection returns; the receiving time is the moment of receiving; receipt numbers and suppliers are available offline.
- **The “Unsent” window.** Everything waiting to be sent to the server (receipts, new products, sales, money) is visible in one window and can be sent by hand.
- **Sign-in protection.** The sign-in is not lost when the connection fails, and a few days before it expires the till warns you and offers to sign in again without losing unsaved work.
- **Returning a debt receipt.** Returning a receipt sold on credit: the money and the client's debt are calculated by the server's rules; if the server refuses, the cashier sees a clear reason.
- **The payment window.** Debt term, instalments, card and QR payment, a number pad for entering the amount.
- **Faster start.** At start the catalog is topped up instead of reloaded; the full catalog check runs at most once a day.
- **Images, AI and file parsing.** Product images, the AI assistant and invoice file parsing work faster and more reliably.
- **Markup in receiving.** The selling price is no longer silently set to “purchase + %”: it does not change until you turn on the auto-markup or type one in the line; the receiving window explains this.
- **Owner program.** A “Needs attention” section appeared on the home page; PIN protection of the program is optional.
- **Fixes from the independent audit.** Server flags are not lost when the connection drops (a debt payment is not duplicated); the weighed-line rounding step comes only from the server unit (TM-30F scales); the senior PIN cannot be bypassed by a server error; notifications are not lost; switching company clears pending records.
- **Android.** Owner-program Excel, invoice from a file; banners no longer steal focus.

---

# Türkçe

### Yenilikler

- **Katalog yükleme.** Temiz kurulumda ve hesap değiştirirken katalog yükleme düzeltildi: tartılı ürünler, barkodlar ve PLU artık kaybolmaz (kasa eksik kataloğu bir kez kendisi yeniden yükler).
- **Bağlantısız mal kabulü.** Mal kabulleri ve yeni ürünler kasada saklanır ve bağlantı gelince kendiliğinden gider; kabul zamanı kabul anından alınır; kabul numaraları ve tedarikçiler bağlantısız da görünür.
- **«Gönderilmeyenler» penceresi.** Sunucuya gönderilmeyi bekleyen her şey (mal kabulleri, yeni ürünler, satışlar, para) tek pencerede görünür ve elle gönderilebilir.
- **Giriş koruması.** Bağlantı sorununda giriş kaybolmaz; giriş süresi dolmadan birkaç gün önce kasa uyarır ve kaydedilmemiş verileri kaybetmeden yeniden girmeyi önerir.
- **Borç fişinin iadesi.** Borca satılan fişin iadesi: para ve müşteri borcu sunucu kurallarına göre hesaplanır; sunucu reddederse kasiyer anlaşılır bir neden görür.
- **Ödeme penceresi.** Borç vadesi, taksit, kart ve QR ile ödeme, tutarı girmek için rakam bloğu.
- **Daha hızlı açılış.** Açılışta katalog baştan yüklenmez, tamamlanır; tam katalog kontrolü günde en fazla bir kez yapılır.
- **Resimler, yapay zekâ ve dosya ayrıştırma.** Ürün resimleri, yapay zekâ yardımcısı ve fatura dosyası ayrıştırma daha hızlı ve güvenilir çalışır.
- **Mal kabulünde kâr oranı.** Satış fiyatı sessizce «alış + %» olarak ayarlanmaz: otomatik kâr oranını açmadıkça veya satıra girmedikçe değişmez; kabul penceresi bunu açıklar.
- **Sahip programı.** Ana sayfada «Dikkat gerektirenler» bölümü eklendi; programın PIN koruması isteğe bağlıdır.
- **Bağımsız denetimden düzeltmeler.** Bağlantı koptuğunda sunucu bayrakları kaybolmaz (borçlu ödeme tekrarlanmaz); tartılı satır yuvarlama adımı yalnızca sunucu birimine göre alınır (TM-30F terazi); yönetici PIN'i sunucu hatasıyla atlanamaz; bildirimler kaybolmaz; şirket değiştirmek bekleyen kayıtları temizler.
- **Android.** Sahip programında Excel, dosyadan irsaliye; uyarı şeritleri odağı çalmaz.

---

# O'zbekcha

### Yangiliklar

- **Katalogni yuklash.** Toza o'rnatishda va akkauntni almashtirishda katalogni yuklash tuzatildi: tarozi tovarlar, shtrix-kodlar va PLU endi yo'qolmaydi (kassa to'liq bo'lmagan katalogni bir marta o'zi qayta yuklaydi).
- **Ulanishsiz qabul.** Kirimlar va yangi tovarlar kassada saqlanadi va aloqa qaytganda o'zi ketadi; qabul vaqti qabul qilingan paytdan olinadi; kirim raqamlari va yetkazib beruvchilar aloqasiz ham ko'rinadi.
- **«Yuborilmaganlar» oynasi.** Serverga yuborilishini kutayotgan hamma narsa (kirimlar, yangi tovarlar, sotuvlar, pul) bitta oynada ko'rinadi va uni qo'lda yuborish mumkin.
- **Kirishni himoya qilish.** Aloqa uzilganda kirish yo'qolmaydi, kirish muddati tugashiga bir necha kun qolganda kassa ogohlantiradi va saqlanmaganni yo'qotmasdan qayta kirishni taklif qiladi.
- **Qarz chekini qaytarish.** Qarzga sotilgan chekni qaytarish: pul va mijoz qarzi server qoidalari bo'yicha hisoblanadi; server rad etsa, kassir tushunarli sababni ko'radi.
- **To'lov oynasi.** Qarz muddati, bo'lib to'lash, karta va QR bilan to'lov, summani kiritish uchun raqamlar bloki.
- **Tezroq ishga tushish.** Ishga tushishda katalog qaytadan yuklanmaydi, to'ldiriladi; katalogni to'liq tekshirish sutkasiga ko'pi bilan bir marta.
- **Rasmlar, SI va fayllarni tahlil qilish.** Tovar rasmlari, SI yordamchisi va yuk xati fayllarini tahlil qilish tezroq va ishonchliroq ishlaydi.
- **Qabuldagi ustama.** Sotuv narxi «xarid + %» deb jimgina qo'yilmaydi: avtoustamani o'zingiz yoqmaguningizcha yoki qatorga kiritmaguningizcha o'zgarmaydi; qabul oynasida bu tushuntirilgan.
- **Egasi dasturi.** Bosh sahifada «E'tibor talab qiladi» bo'limi paydo bo'ldi; dasturni PIN bilan himoya qilish ixtiyoriy.
- **Mustaqil auditdan tuzatishlar.** Aloqa uzilganda server belgilari yo'qolmaydi (qarzga to'lov takrorlanmaydi); tarozi qatorlarini yaxlitlash qadami faqat server birligi bo'yicha olinadi (TM-30F tarozi); katta PIN server xatosi bilan aylanib o'tilmaydi; ogohlantirishlar yo'qolmaydi; kompaniyani almashtirish kechiktirilgan yozuvlarni tozalaydi.
- **Android.** Egasi dasturida Excel, fayldan yuk xati; plashkalar fokusni tortib olmaydi.
