## v1.17.95

Акции v2 (раздел «Акции» у владельца, расчёт на кассе по правилам сервера); бот-продавец и торг; ремонт синхронизируется между устройствами; красивый чек по ссылке и QR; выгрузка в 1С и «Загрузим под ключ»; рассылка и номер ЭСФ; Нур-советник читает разделы; новые карточки Маркетплейса; точнее возвраты и закрытие смены; удобнее поля и кнопки. Всё из 1.17.94 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.95.tar.gz`; Android — `nurmarket-kassa-android-1.17.95.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Акции.** В программе владельца новый раздел «Акции» — мастер создания и предпросмотр. Касса сама считает скидки по правилам сервера и перед оплатой сверяет итог с сервером; без связи считает по сохранённым правилам.
- **Бот-продавец и торг.** В программе владельца — настройки бота-продавца, его диалоги, проверка ответов, статистика продаж бота, товары и правила торга, предложения покупателей. В заказах с сайта видна цена, о которой договорились в торге.
- **Ремонт.** Номер квитанции выдаёт сервер; квитанции синхронизируются между устройствами; чужие изменения не затираются — при одновременной правке открывается окно выбора.
- **Чек по ссылке и QR.** Покупатель открывает красивый чек по ссылке или по QR с чека. На чеке и этикетке можно печатать QR магазина, а для оплаты — QR на сумму чека.
- **Выгрузка в 1С и «Загрузим под ключ».** Продажи, товары, приходы, возвраты, клиенты и поставщики выгружаются файлом для 1С. Заявку на услугу «Загрузим базу под ключ» можно оставить прямо из программы.
- **Рассылка и ЭСФ.** В программе владельца — рассылка клиентам по группам. В приходе можно указать номер ЭСФ поставщика.
- **Нур-советник.** Советник сам открывает разделы и вкладки и читает их данные — только чтение, с учётом прав пользователя; секреты и телефоны клиентов скрыты.
- **Маркетплейс.** 12 новых карточек функций — бот-продавец, торг, ЭСФ, онлайн-ККМ, рассылка, 1С, «под ключ», QR, электронный чек, акции и другие. У всех функций есть описание и демонстрация «Как это работает».
- **Точность денег.** Возвраты считаются по правилам сервера: каждый шаг округляется до тыйына (половина — вверх), последний частичный возврат по чеку — ровно остаток, бесплатная позиция учитывается верно; при обмене деньги в ящике поправляются правильно.
- **Закрытие смены.** Касса ждёт ответ сервера до 10 секунд. Если сервер не успел, Z-отчёт строится по данным сервера, а не по неполному журналу кассы. Номер смены берётся с сервера.
- **Удобство и вид.** Ноль в числовом поле стирается, как только начинаете ввод. Кнопка «Оплатить» больше не показывает сумму дважды, подсказки в полях заметнее, переключатели и кнопки не меньше 40 px, числа и суммы везде в одном виде.
- **Исправления и скорость.** В сводке владельца нет двойной строки «В долг»; вместо значка «-0 сом» видна настоящая разница; подсказка смешанной оплаты не обрезается; у кнопки «Стереть» в окне взвешивания есть подпись. Быстрее работают журнал локальной сети и список штрихкодов вариантов.

# Кыргызча

### Эмнелер жаңы

- **Акциялар.** Ээсинин программасында жаңы «Акциялар» бөлүмү — түзүү чебери жана алдын ала көрүү. Касса арзандатууларды сервердин эрежелери боюнча өзү эсептейт жана төлөөдөн мурун жыйынтыкты сервер менен салыштырат; байланыш жок болсо, сакталган эрежелер боюнча эсептейт.
- **Бот-сатуучу жана соодалашуу.** Ээсинин программасында — бот-сатуучунун жөндөөлөрү, анын диалогдору, жоопторду текшерүү, боттун сатуу статистикасы, соодалашуу товарлары жана эрежелери, сатып алуучулардын сунуштары. Сайттан келген заказдарда соодалашууда макулдашылган баа көрүнөт.
- **Оңдоо.** Квитанциянын номерин сервер берет; квитанциялар түзмөктөр ортосунда шайкештелет; башкалардын өзгөртүүлөрү өчүп калбайт — бир убакта оңдогондо тандоо терезеси ачылат.
- **Шилтеме жана QR боюнча чек.** Сатып алуучу кооз чекти шилтеме же чектеги QR аркылуу ачат. Чекке жана этикеткага дүкөндүн QR кодун, ал эми төлөм үчүн — чектин суммасына QR кодун басса болот.
- **1С'ке түшүрүү жана «Ачкычка даяр жүктөйбүз».** Сатуулар, товарлар, кирешелер, кайтаруулар, кардарлар жана жеткирүүчүлөр 1С үчүн файл менен түшүрүлөт. «Базаңызды ачкычка даяр жүктөйбүз» кызматына өтүнмөнү түздөн-түз программадан калтырса болот.
- **Жөнөтмө жана ЭСФ.** Ээсинин программасында — кардарларга топтор боюнча жөнөтмө. Киреше учурунда жеткирүүчүнүн ЭСФ номерин жазса болот.
- **Нур Кеңешчи.** Кеңешчи бөлүмдөрдү жана өтмөктөрдү өзү ачып, алардын маалыматын окуйт — окуу гана, колдонуучунун укуктарына жараша; сырлар жана кардарлардын телефондору жашырылган.
- **Маркетплейс.** Функциялардын 12 жаңы карточкасы — бот-сатуучу, соодалашуу, ЭСФ, онлайн-ККМ, жөнөтмө, 1С, «ачкычка даяр», QR, электрондук чек, акциялар жана башкалар. Бардык функциялардын сүрөттөмөсү жана «Кантип иштейт» көрсөтмөсү бар.
- **Акчанын тактыгы.** Кайтаруулар сервердин эрежелери боюнча эсептелет: ар бир кадам тыйынга чейин тегеректелет (жарымы — жогору), чек боюнча акыркы жарым-жартылай кайтаруу — так калдык, акысыз позиция туура эсепке алынат; алмаштырууда кутудагы акча туура оңдолот.
- **Сменаны жабуу.** Касса сервердин жообун 10 секундга чейин күтөт. Сервер үлгүрбөсө, Z-отчёт кассанын толук эмес журналы боюнча эмес, сервердин маалыматы боюнча түзүлөт. Сменанын номери серверден алынат.
- **Ыңгайлуулук жана көрүнүш.** Сан талаасындагы нөл жаза баштаганда эле өчөт. «Төлөө» баскычы сумманы эки жолу көрсөтпөйт, талаалардагы кеңештер көрүнүктүү, которгучтар жана баскычтар 40 px'тен кем эмес, сандар жана суммалар бардык жерде бирдей түрдө.
- **Оңдоолор жана ылдамдык.** Ээсинин жыйынтыгында «Карызга» сабы эки жолу чыкпайт; «-0 сом» белгисинин ордуна чыныгы айырма көрүнөт; аралаш төлөмдүн кеңеши кесилбейт; таразалоо терезесиндеги «Өчүрүү» баскычынын аты бар. Жергиликтүү тармактын журналы жана варианттардын штрихкоддор тизмеси тезирээк иштейт.

# English

### What's new

- **Promotions.** The owner's program has a new “Promotions” section with a creation wizard and preview. The till calculates discounts by the server's rules itself and checks the total with the server before payment; offline it uses the saved rules.
- **Seller bot and bargaining.** The owner's program has seller bot settings, its dialogs, an answer check, bot sales statistics, bargaining products and rules, and customer offers. Website orders show the price agreed in bargaining.
- **Repair.** The server issues the ticket number; tickets sync between devices; other people's changes are not overwritten — a choice window opens when a ticket is edited at the same time.
- **Receipt by link and QR.** The customer opens a nice-looking receipt by link or by the QR on the receipt. The shop's QR can be printed on receipts and labels, and a QR for the receipt amount can be shown for payment.
- **Export to 1C and “We load it for you”.** Sales, products, receipts of goods, returns, customers and suppliers are exported as a file for 1C. A request for the “We load your catalog turnkey” service can be sent right from the program.
- **Mailing and ESF.** The owner's program can send mailings to customer groups. A goods receipt can store the supplier's ESF number.
- **Nur Advisor.** The advisor opens sections and tabs itself and reads their data — read-only, according to the user's rights; secrets and customers' phone numbers are hidden.
- **Marketplace.** 12 new feature cards — seller bot, bargaining, ESF, online fiscal register, mailing, 1C, “we load it for you”, QR, e-receipt, promotions and more. Every feature has a description and a “How it works” demo.
- **Money accuracy.** Refunds follow the server's rules: every step is rounded to the tyiyn (half goes up), the last partial refund on a receipt is exactly the remainder, free items are handled correctly; on exchanges the cash drawer is adjusted correctly.
- **Shift closing.** The till waits up to 10 seconds for the server. If the server is late, the Z report is built from the server's data, not from the till's incomplete log. The shift number comes from the server.
- **Ease of use and look.** A zero in a number field is cleared as soon as you start typing. The “Pay” button no longer shows the amount twice, field hints are easier to see, switches and buttons are at least 40 px, and numbers and amounts look the same everywhere.
- **Fixes and speed.** The owner's summary no longer shows “On credit” twice; the “-0 som” badge now shows the real difference; the mixed payment hint is no longer cut off; the “Erase” button in the weighing window has a name. The local network log and the variant barcode list work faster.

# Türkçe

### Yenilikler

- **Kampanyalar.** Sahibin programında oluşturma sihirbazı ve ön izlemeli yeni «Kampanyalar» bölümü var. Kasa indirimleri sunucu kurallarına göre kendisi hesaplar ve ödemeden önce toplamı sunucuyla karşılaştırır; bağlantı yoksa kayıtlı kurallarla hesaplar.
- **Satıcı bot ve pazarlık.** Sahibin programında satıcı bot ayarları, diyalogları, yanıt kontrolü, bot satış istatistikleri, pazarlık ürünleri ve kuralları, müşteri teklifleri var. Siteden gelen siparişlerde pazarlıkta anlaşılan fiyat görünür.
- **Tamir.** Fiş numarasını sunucu verir; fişler cihazlar arasında eşitlenir; başkalarının değişiklikleri silinmez — aynı anda düzenlemede seçim penceresi açılır.
- **Bağlantı ve QR ile fiş.** Müşteri şık fişi bağlantıdan veya fişteki QR'dan açar. Fişe ve etikete mağazanın QR'ı basılabilir, ödeme için fiş tutarına QR gösterilebilir.
- **1C'ye aktarım ve «Sizin için yükleyelim».** Satışlar, ürünler, girişler, iadeler, müşteriler ve tedarikçiler 1C için dosya olarak aktarılır. «Ürün tabanınızı anahtar teslim yükleriz» hizmeti için talep doğrudan programdan gönderilebilir.
- **Toplu mesaj ve ESF.** Sahibin programında müşteri gruplarına toplu mesaj gönderilebilir. Girişte tedarikçinin ESF numarası yazılabilir.
- **Nur Danışman.** Danışman bölümleri ve sekmeleri kendisi açar ve verilerini okur — yalnızca okuma, kullanıcının yetkilerine göre; gizli bilgiler ve müşteri telefonları gizlenir.
- **Pazar yeri.** 12 yeni işlev kartı — satıcı bot, pazarlık, ESF, çevrimiçi yazar kasa, toplu mesaj, 1C, «sizin için yükleyelim», QR, e-fiş, kampanyalar ve diğerleri. Her işlevin açıklaması ve «Nasıl çalışır» gösterimi var.
- **Para doğruluğu.** İadeler sunucu kurallarına göre hesaplanır: her adım tıyına yuvarlanır (yarım yukarı), fişteki son kısmi iade tam olarak kalan tutardır, ücretsiz ürün doğru hesaba katılır; değişimde kasadaki para doğru düzeltilir.
- **Vardiya kapatma.** Kasa sunucunun yanıtını 10 saniyeye kadar bekler. Sunucu yetişmezse Z raporu kasanın eksik kaydından değil, sunucu verilerinden oluşturulur. Vardiya numarası sunucudan alınır.
- **Kullanım ve görünüm.** Sayı alanındaki sıfır, yazmaya başlar başlamaz silinir. «Öde» düğmesi tutarı artık iki kez göstermez, alan ipuçları daha belirgin, anahtarlar ve düğmeler en az 40 px, sayılar ve tutarlar her yerde aynı biçimde.
- **Düzeltmeler ve hız.** Sahibin özetinde «Veresiye» satırı iki kez çıkmaz; «-0 som» rozeti yerine gerçek fark görünür; karışık ödeme ipucu kesilmez; tartım penceresindeki «Sil» düğmesinin adı var. Yerel ağ kaydı ve varyant barkod listesi daha hızlı çalışır.

# O'zbekcha

### Yangiliklar

- **Aksiyalar.** Egasining dasturida yaratish ustasi va oldindan ko'rish bilan yangi «Aksiyalar» bo'limi. Kassa chegirmalarni server qoidalari bo'yicha o'zi hisoblaydi va to'lovdan oldin jamini server bilan solishtiradi; aloqa bo'lmasa, saqlangan qoidalar bo'yicha hisoblaydi.
- **Bot-sotuvchi va savdolashuv.** Egasining dasturida — bot-sotuvchi sozlamalari, uning dialoglari, javoblarni tekshirish, bot sotuvlari statistikasi, savdolashuv tovarlari va qoidalari, xaridorlar takliflari. Saytdan kelgan buyurtmalarda savdolashuvda kelishilgan narx ko'rinadi.
- **Ta'mir.** Kvitansiya raqamini server beradi; kvitansiyalar qurilmalar o'rtasida sinxronlanadi; boshqalarning o'zgarishlari o'chib ketmaydi — bir vaqtda tahrirlashda tanlash oynasi ochiladi.
- **Havola va QR bo'yicha chek.** Xaridor chiroyli chekni havola yoki chekdagi QR orqali ochadi. Chek va yorliqqa do'kon QR kodini, to'lov uchun esa chek summasiga QR kodni chiqarish mumkin.
- **1C ga eksport va «Kalitga tayyor yuklaymiz».** Sotuvlar, tovarlar, kirimlar, qaytarishlar, mijozlar va yetkazib beruvchilar 1C uchun fayl bilan eksport qilinadi. «Bazangizni kalitga tayyor yuklaymiz» xizmatiga arizani to'g'ridan-to'g'ri dasturdan qoldirish mumkin.
- **Xabarnoma va ESF.** Egasining dasturida — mijozlarga guruhlar bo'yicha xabarnoma. Kirimda yetkazib beruvchining ESF raqamini yozish mumkin.
- **Nur Maslahatchi.** Maslahatchi bo'lim va yorliqlarni o'zi ochadi va ularning ma'lumotlarini o'qiydi — faqat o'qish, foydalanuvchi huquqlariga qarab; maxfiy ma'lumotlar va mijozlar telefonlari yashirilgan.
- **Marketplace.** Funksiyalarning 12 ta yangi kartasi — bot-sotuvchi, savdolashuv, ESF, onlayn-KKM, xabarnoma, 1C, «kalitga tayyor», QR, elektron chek, aksiyalar va boshqalar. Barcha funksiyalarning tavsifi va «Qanday ishlaydi» namoyishi bor.
- **Pul aniqligi.** Qaytarishlar server qoidalari bo'yicha hisoblanadi: har bir qadam tiyingacha yaxlitlanadi (yarmi — yuqoriga), chek bo'yicha oxirgi qisman qaytarish — aynan qoldiq, bepul pozitsiya to'g'ri hisobga olinadi; almashtirishda qutidagi pul to'g'ri tuzatiladi.
- **Smenani yopish.** Kassa server javobini 10 soniyagacha kutadi. Server ulgurmasa, Z-hisobot kassaning to'liq bo'lmagan jurnali bo'yicha emas, server ma'lumotlari bo'yicha tuziladi. Smena raqami serverdan olinadi.
- **Qulaylik va ko'rinish.** Raqam maydonidagi nol yozishni boshlashingiz bilan o'chadi. «To'lash» tugmasi summani endi ikki marta ko'rsatmaydi, maydonlardagi maslahatlar ko'zga yaqqolroq, almashtirgichlar va tugmalar kamida 40 px, sonlar va summalar hamma joyda bir xil ko'rinishda.
- **Tuzatishlar va tezlik.** Egasining hisobotida «Qarzga» qatori ikki marta chiqmaydi; «-0 som» belgisi o'rniga haqiqiy farq ko'rinadi; aralash to'lov maslahati kesilmaydi; tortish oynasidagi «O'chirish» tugmasining nomi bor. Mahalliy tarmoq jurnali va variantlar shtrix-kodlari ro'yxati tezroq ishlaydi.
