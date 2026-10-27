namespace NurMarketKassa.Services;

/// <summary>Краткое описание изменений ТЕКУЩЕЙ версии — показывается кассиру один раз
/// в окне "Что нового" после того, как касса обновилась (см. UserPreferences.LastSeenAppVersion),
/// и используется как текст релиза при публикации на GitHub.
/// ВАЖНО: перед каждым релизом содержимое <see cref="Latest"/> (и переводов) полностью ЗАМЕНЯЕТСЯ
/// на изменения именно этой версии — не дописывается поверх старых пунктов. История прошлых версий
/// не нужна здесь: она уже есть в самих релизах на GitHub, а этот список — только "что нового прямо
/// сейчас". 2026-09-07: список показывается на языке интерфейса (ru/ky/en/tr/uz, см.
/// <see cref="LatestForCurrentLanguage"/>) — раньше при кыргызском интерфейсе заголовок был
/// кыргызским, а сами пункты русскими.</summary>
public static class AppChangelog
{
    public static readonly string[] Latest =
    [
        "Оплата: Enter не проводит продажу, пока кнопка «Оплатить» неактивна; сканы клавиатурным сканером во время оплаты встают в очередь: товар добавится после оплаты в новый чек (при отмене оплаты — в тот же)",
        "Скидки и возвраты: скидка «0,5», «0,50», «2,5 %» применяется верно (раньше как 50 сом / 25 %), предпросмотр совпадает с применяемым; частичный возврат по количеству («клей × 2» — вернуть 1); «Цена изменилась…» стоит в окне оплаты; «Полный экран» возвращается после «Свернуть»; имена для диктора",
        "Экран покупателя и QR показывают сумму после скидки и бонусов",
        "Защита от двойной продажи «в долг» и «смешанной» оплатой после обрыва связи; офлайн-чек не теряет клиента",
        "Предоплата долга: безналичная на старом пути не уходит как наличные, довнесение переживает перезапуск",
        "Суммы «1.000», «1e3», «(100)» дают ошибку; быстрые купюры 10/20; кнопка «+» у дробного количества",
        "Цены: сверка цен чека с сервером перед оплатой («Цена изменилась: 205 → 195»); цена на плитке обновляется за 8–10 с, если сервер отдаёт ленту изменений каталога; строгая проверка цены сервером; склад после прихода не откатывает цену",
        "Смены и возвраты: возврат со скидкой на чек не больше оплаченного; досылка внесений и изъятий закрытой смены; закрытие ждёт очередь чеков; закрытая «в очередь» смена не оживает; Z без ответа сервера считается с внесениями и возвратами",
        "X-ОТЧЁТ и Z-ОТЧЁТ на 5 языках; копия чека после возврата и смешанной оплаты по журналу кассы; уникальные имена PDF чеков; история чеков «Эта смена» — по периоду смены",
        "Сканер: старый буфер не съедает Enter, подтверждения защищены от скана, знаки / + * _ % $ и GS в Code128/DataMatrix",
        "Окна: «Полный экран» применяется к кассе, программе владельца, окну входа и окнам разделов; масштаб 1024×600 не ниже 80 %; вид «1С»: Enter, Num+, Num−",
        "Права на «Горячие клавиши» и «Логи и ошибки»; предупреждение при закрытии настроек с несохранёнными правками",
        "Тёмная тема: читаемые надписи кнопок (контраст не ниже 4,5:1), значки в кнопках",
        "Программа владельца: общие настройки идут через общий документ кассы (название магазина, коды сотрудников, причина удаления доходят до касс), история списаний и ревизий всех касс, плашка вместо окна «Товар сохранён», имена кнопок для диктора, единый порог «мало»",
        "Склад → Приёмка: загрузка электронных накладных (Excel, CSV, XML, ТОРГ-12, CommerceML)",
        "Встроенный справочник товаров NurCRM (128 276 товаров): подстановка названия, а где есть в справочнике — бренда и категории; обновляется с сервера по мере появления новых",
        "«Телефон как сканер»: скан камерой мобилки попадает в чек кассы по Wi-Fi, по QR-сопряжению; подпись ответов, защита от повторов, отключение телефонов в настройках (выключено по умолчанию: Настройки → Печать)",
        "Нур Советник: разделы открываются без нейросети, ответ строится из фактов — быстрее",
        "Окно оплаты по центру экрана (настройка «прижимать к низу» возвращает прежний вид)",
        "Оплата: защита от ложного сообщения «оплата уже прошла» (жетон чека)",
        "Настройки владельца и кассы сливаются по ключам, с резервной копией",
        "Смены: перед закрытием досылаются чеки; старую смену можно продолжить — с подсказкой об изъятии/внесении",
        "Накладные: многостраничные, защита от повторной загрузки, подтверждение похожих товаров",
        "Темы сайта: фильтр «Для вашего магазина», предупреждение «тема не для вашей сферы», «Применить без картинок»; Склад → Загрузить фото: окно предпросмотра (главное / в галерею)",
        "Весовая этикетка «по сумме» не пересчитывается; штука из пачки не получает цену пачки",
        "Убрано голосовое управление кассой — теперь есть ИИ-ассистент",
    ];

    public static readonly string[] LatestKy =
    [
        "Төлөм: «Төлөө» баскычы жигердүү болбосо, Enter сатууну өткөрбөйт; төлөм учурундагы сканерлөөлөр кезекке турат: товар төлөмдөн кийин жаңы чекке кошулат (төлөм жокко чыкса — ошол чекке)",
        "Арзандатуулар жана кайтаруулар: «0,5», «0,50», «2,5 %» арзандатуусу туура колдонулат (мурун 50 сом / 25 % болуп калчу), алдын ала көрүү колдонулганга дал келет; саны боюнча жарым-жартылай кайтаруу («клей × 2» — 1ин кайтаруу); «Баа өзгөрдү…» төлөм терезесинде турат; «Толук экран» «Жыйыштыруудан» кийин кайтат; диктор үчүн аттар",
        "Сатып алуучунун экраны жана QR арзандатуудан жана бонустардан кийинки сумманы көрсөтөт",
        "Байланыш үзүлгөндөн кийин «карызга» жана «аралаш» төлөм менен кош сатуудан коргоо; офлайн чек кардарды жоготпойт",
        "Карыздын алдын ала төлөмү: эски жолдо накталайсыз төлөм накталай болуп кетпейт, кошумча киргизүү кайра иштетүүдөн кийин да сакталат",
        "«1.000», «1e3», «(100)» суммалары ката берет; тез купюралар 10/20; бөлчөк санда «+» баскычы",
        "Баалар: төлөөдөн мурун чектин баасы сервер менен салыштырылат («Баа өзгөрдү: 205 → 195»); сервер каталогдун өзгөрүү лентасын берсе, плиткадагы баа 8–10 с ичинде жаңырат; бааны сервердин катуу текшерүүсү; кампа кирешеден кийин бааны кайтарбайт",
        "Смена жана кайтаруу: арзандатуу менен чекти кайтаруу төлөнгөндөн ашпайт; жабылган сменанын киргизүүлөрүн жана алуу операцияларын кайра жөнөтүү; жабуу чектер кезегин күтөт; «кезекке» жабылган смена кайра жанбайт; сервер жооп бербесе, Z киргизүүлөр жана кайтаруулар менен эсептелет",
        "X-ОТЧЕТ жана Z-ОТЧЕТ 5 тилде; кайтаруудан жана аралаш төлөмдөн кийин чектин көчүрмөсү касса журналы боюнча; чектердин PDF аттары уникалдуу; «Бул смена» чек тарыхы смена мезгили боюнча",
        "Сканер: эски буфер Enter баскычын жебейт, ырастоолор сканерден корголгон, Code128/DataMatrix кодунда / + * _ % $ белгилери жана GS",
        "Терезелер: «Толук экран» кассага, ээсинин программасына, кирүү терезесине жана бөлүм терезелерине колдонулат; 1024×600 масштабы 80 %дан төмөн эмес; «1С» көрүнүшү: Enter, Num+, Num−",
        "«Ысык баскычтар» жана «Журналдар жана каталар» укуктары; сакталбаган өзгөртүүлөрү бар жөндөөлөрдү жабууда эскертүү",
        "Караңгы тема: баскыч жазуулары окулуучу (контраст 4,5:1ден кем эмес), баскычтардагы белгилер",
        "Ээсинин программасы: жалпы жөндөөлөр кассанын жалпы документи аркылуу өтөт (дүкөндүн аты, кызматкер коддору, өчүрүү себеби кассаларга жетет), бардык кассалардын эсептен чыгаруу жана ревизия тарыхы, «Товар сакталды» терезесинин ордуна тилкече, диктор үчүн баскыч аттары, «аз» үчүн бирдиктүү чек",
        "Кампа → Кабыл алуу: электрондук накладныйларды жүктөө (Excel, CSV, XML, ТОРГ-12, CommerceML)",
        "NurCRM товарларынын орнотулган маалымдамасы (128 276 товар): аталышы, ал эми маалымдамада бар болсо — бренди жана категориясы коюлат; жаңылары чыккан сайын серверден жаңырат",
        "«Телефон сканер катары»: мобилка камерасы менен сканерлөө Wi-Fi аркылуу, QR жупташтыруу менен кассанын чегине түшөт; жооптордун кол тамгасы, кайталоодон коргоо, телефондорду жөндөөлөрдөн өчүрүү (демейки боюнча өчүк: Жөндөөлөр → Басуу)",
        "Нур Кеңешчи: бөлүмдөр нейротармаксыз ачылат, жооп фактылардан түзүлөт — тезирээк",
        "Төлөм терезеси экрандын ортосунда («ылдыйга кысуу» жөндөөсү мурунку көрүнүштү кайтарат)",
        "Төлөм: «төлөм өтүп кетти» деген жалган билдирүүдөн коргоо (чектин жетону)",
        "Ээсинин жана кассанын жөндөөлөрү ачкычтар боюнча бириктирилет, камдык көчүрмө менен",
        "Смена: жабуудан мурун чектер жөнөтүлөт; эски сменаны улантууга болот — алуу/киргизүү боюнча кеңеш менен",
        "Накладныйлар: көп барактуу, кайталап жүктөөдөн коргоо, окшош товарларды ырастоо",
        "Сайт темалары: «Сиздин дүкөн үчүн» чыпкасы, «тема сиздин тармакка ылайык эмес» эскертүүсү, «Сүрөтсүз колдонуу»; Кампа → Сүрөт жүктөө: алдын ала көрүү терезеси (башкысы / галереяга)",
        "«Сумма боюнча» салмактуу этикетка кайра эсептелбейт; пачкадан бир даана пачканын баасын албайт",
        "Кассанын үн менен башкаруусу алынды — эми ИИ-жардамчы бар",
    ];

    public static readonly string[] LatestEn =
    [
        "Payment: Enter does not complete the sale while the “Pay” button is inactive; scans during payment go into a queue: the item is added after payment to the new receipt (if payment is canceled — to the same one)",
        "Discounts and returns: a “0,5”, “0,50”, “2,5 %” discount is applied correctly (it used to become 50 som / 25 %), the preview matches what is applied; partial return by quantity (“glue × 2” — return 1); “Price changed…” stays in the payment window; “Full screen” comes back after “Minimize”; names for the screen reader",
        "The customer screen and QR show the amount after discount and bonuses",
        "Protection against a double sale “on credit” and with “mixed” payment after a connection drop; an offline receipt does not lose the customer",
        "Debt prepayment: a cashless one on the old path no longer goes out as cash, the top-up survives a restart",
        "Amounts “1.000”, “1e3”, “(100)” give an error; quick banknotes 10/20; the “+” button for a fractional quantity",
        "Prices: receipt prices are checked against the server before payment (“Price changed: 205 → 195”); a tile price updates within 8–10 s if the server provides the catalog change feed; strict server-side price check; stock after receiving no longer rolls the price back",
        "Shifts and returns: a return with a receipt discount is not more than what was paid; cash-in and cash-out of a closed shift are re-sent; closing waits for the receipt queue; a shift closed “to the queue” does not come back to life; Z without a server reply is counted with cash-in and returns",
        "X-REPORT and Z-REPORT in 5 languages; a receipt copy after a return and mixed payment from the till journal; unique receipt PDF names; the “This shift” receipt history follows the shift period",
        "Scanner: an old buffer does not swallow Enter, confirmations are protected from scans, the characters / + * _ % $ and GS in Code128/DataMatrix",
        "Windows: “Full screen” applies to the till, the owner’s program, the login window and section windows; 1024×600 scale not below 80%; the “1C” view: Enter, Num+, Num−",
        "Permissions for “Hotkeys” and “Logs and errors”; a warning when closing settings with unsaved changes",
        "Dark theme: readable button labels (contrast at least 4.5:1), icons in buttons",
        "Owner’s program: common settings go through the shared till document (store name, employee codes, deletion reason reach the tills), write-off and stocktake history of all tills, a banner instead of the “Product saved” window, button names for the screen reader, one common “low” threshold",
        "Stock → Receiving: loading electronic invoices (Excel, CSV, XML, TORG-12, CommerceML)",
        "Built-in NurCRM product directory (128,276 products): the name is filled in, and the brand and category where the directory has them; updated from the server as new ones appear",
        "“Phone as a scanner”: a scan with the mobile app camera goes into the till receipt over Wi-Fi, by QR pairing; signed replies, protection against repeats, phones can be disabled in settings (off by default: Settings → Printing)",
        "Nur Advisor: sections open without the neural network, the answer is built from facts — faster",
        "The payment window is centered on the screen (the “dock to the bottom” setting brings back the old look)",
        "Payment: protection against a false “payment already went through” message (receipt token)",
        "Owner’s program and till settings are merged by keys, with a backup copy",
        "Shifts: receipts are re-sent before closing; an old shift can be continued — with a cash-out/cash-in hint",
        "Invoices: multi-page, protection against loading twice, confirmation of similar products",
        "Site themes: the “For your store” filter, the “theme is not for your business” warning, “Apply without pictures”; Stock → Upload photo: a preview window (main / to gallery)",
        "A weighed label “by amount” is not recalculated; a single piece from a pack does not get the pack price",
        "Voice control of the till removed — there is now an AI assistant",
    ];

    public static readonly string[] LatestTr =
    [
        "Ödeme: «Öde» düğmesi etkin değilken Enter satışı tamamlamaz; ödeme sırasındaki taramalar sıraya girer: ürün ödemeden sonra yeni fişe eklenir (ödeme iptal edilirse — aynı fişe)",
        "İndirim ve iade: «0,5», «0,50», «%2,5» indirimi doğru uygulanır (eskiden 50 som / %25 oluyordu), önizleme uygulananla aynıdır; adede göre kısmi iade («yapıştırıcı × 2» — 1 iade); «Fiyat değişti…» ödeme penceresinde durur; «Tam ekran» «Küçült»ten sonra geri gelir; ekran okuyucu için adlar",
        "Müşteri ekranı ve QR, indirim ve bonuslardan sonraki tutarı gösterir",
        "Bağlantı kopmasından sonra «veresiye» ve «karma» ödemeyle çifte satışa karşı koruma; çevrimdışı fiş müşteriyi kaybetmez",
        "Borç ön ödemesi: eski yolda nakitsiz ödeme nakit gibi gitmez, ek ödeme yeniden başlatmadan sonra da korunur",
        "«1.000», «1e3», «(100)» tutarları hata verir; hızlı banknotlar 10/20; kesirli miktarda «+» düğmesi",
        "Fiyatlar: fiş fiyatları ödemeden önce sunucuyla karşılaştırılır («Fiyat değişti: 205 → 195»); sunucu katalog değişiklik akışını verirse kutucuktaki fiyat 8–10 sn içinde güncellenir; sunucuda sıkı fiyat denetimi; depo, mal kabulden sonra fiyatı geri almaz",
        "Vardiya ve iade: fiş indirimli iade ödenenden fazla olmaz; kapalı vardiyanın para girişi ve çıkışı yeniden gönderilir; kapatma fiş kuyruğunu bekler; «kuyruğa» kapatılan vardiya yeniden canlanmaz; sunucu yanıt vermezse Z, giriş ve iadelerle hesaplanır",
        "X-RAPORU ve Z-RAPORU 5 dilde; iade ve karma ödemeden sonra fiş kopyası kasa günlüğüne göre; fiş PDF adları benzersiz; «Bu vardiya» fiş geçmişi vardiya dönemine göre",
        "Tarayıcı: eski arabellek Enter'ı yemez, onaylar taramadan korunur, Code128/DataMatrix'te / + * _ % $ işaretleri ve GS",
        "Pencereler: «Tam ekran» kasaya, sahip programına, giriş penceresine ve bölüm pencerelerine uygulanır; 1024×600 ölçeği %80'in altına inmez; «1C» görünümü: Enter, Num+, Num−",
        "«Kısayol tuşları» ve «Günlükler ve hatalar» için yetkiler; kaydedilmemiş değişikliklerle ayarları kapatırken uyarı",
        "Koyu tema: okunaklı düğme yazıları (kontrast en az 4,5:1), düğmelerdeki simgeler",
        "Sahip programı: ortak ayarlar kasanın ortak belgesinden geçer (mağaza adı, çalışan kodları, silme nedeni kasalara ulaşır), tüm kasaların silme ve sayım geçmişi, «Ürün kaydedildi» penceresi yerine şerit, ekran okuyucu için düğme adları, ortak «az» eşiği",
        "Depo → Mal kabul: elektronik irsaliyelerin yüklenmesi (Excel, CSV, XML, TORG-12, CommerceML)",
        "Yerleşik NurCRM ürün rehberi (128 276 ürün): ad, rehberde varsa marka ve kategori otomatik doldurulur; yenileri çıktıkça sunucudan güncellenir",
        "«Telefon tarayıcı olarak»: mobil uygulama kamerasıyla tarama, Wi-Fi üzerinden QR eşleştirmesiyle kasa fişine düşer; imzalı yanıtlar, tekrara karşı koruma, telefonları ayarlardan kapatma (varsayılan olarak kapalı: Ayarlar → Yazdırma)",
        "Nur Danışman: bölümler yapay zekâ olmadan açılır, yanıt gerçeklerden kurulur — daha hızlı",
        "Ödeme penceresi ekranın ortasında («alta yasla» ayarı eski görünümü getirir)",
        "Ödeme: yanlış «ödeme zaten geçti» iletisine karşı koruma (fiş jetonu)",
        "Sahip programı ve kasa ayarları anahtarlara göre birleştirilir, yedek kopyayla",
        "Vardiya: kapatmadan önce fişler yeniden gönderilir; eski vardiya devam ettirilebilir — para çıkışı/girişi ipucuyla",
        "İrsaliyeler: çok sayfalı, tekrar yüklemeye karşı koruma, benzer ürünlerin onayı",
        "Site temaları: «Mağazanız için» filtresi, «tema işletmenize uygun değil» uyarısı, «Resimsiz uygula»; Depo → Fotoğraf yükle: önizleme penceresi (ana / galeriye)",
        "«Tutara göre» tartılı etiket yeniden hesaplanmaz; paketten tek parça paket fiyatını almaz",
        "Kasanın sesli kontrolü kaldırıldı — artık bir yapay zekâ asistanı var",
    ];

    public static readonly string[] LatestUz =
    [
        "To'lov: «To'lash» tugmasi faol bo'lmasa, Enter sotuvni o'tkazmaydi; to'lov paytidagi skanlar navbatga turadi: mahsulot to'lovdan keyin yangi chekka qo'shiladi (to'lov bekor qilinsa — shu chekka)",
        "Chegirma va qaytarish: «0,5», «0,50», «2,5 %» chegirma to'g'ri qo'llanadi (avval 50 so'm / 25 % bo'lib ketardi), oldindan ko'rish qo'llanilganga mos; miqdor bo'yicha qisman qaytarish («yelim × 2» — 1 tasini qaytarish); «Narx o'zgardi…» to'lov oynasida turadi; «To'liq ekran» «Yig'ish»dan keyin qaytadi; diktor uchun nomlar",
        "Xaridor ekrani va QR chegirma va bonuslardan keyingi summani ko'rsatadi",
        "Aloqa uzilgandan keyin «qarzga» va «aralash» to'lov bilan ikki marta sotishdan himoya; oflayn chek mijozni yo'qotmaydi",
        "Qarzning oldindan to'lovi: eski yo'lda naqdsiz to'lov naqd bo'lib ketmaydi, qo'shimcha kiritish qayta ishga tushirishdan keyin ham saqlanadi",
        "«1.000», «1e3», «(100)» summalari xato beradi; tezkor pul belgilari 10/20; kasr miqdorda «+» tugmasi",
        "Narxlar: chek narxlari to'lovdan oldin server bilan solishtiriladi («Narx o'zgardi: 205 → 195»); server katalog o'zgarishlari lentasini bersa, plitkadagi narx 8–10 s ichida yangilanadi; narxni serverda qat'iy tekshirish; ombor kirimdan keyin narxni qaytarmaydi",
        "Smena va qaytarish: chegirmali chekni qaytarish to'langandan oshmaydi; yopilgan smenaning kiritish va olish amallari qayta yuboriladi; yopish cheklar navbatini kutadi; «navbatga» yopilgan smena qayta jonlanmaydi; server javob bermasa, Z kiritish va qaytarishlar bilan hisoblanadi",
        "X-HISOBOT va Z-HISOBOT 5 tilda; qaytarish va aralash to'lovdan keyin chek nusxasi kassa jurnali bo'yicha; chek PDF nomlari noyob; «Shu smena» cheklar tarixi smena davri bo'yicha",
        "Skaner: eski bufer Enter'ni yemaydi, tasdiqlar skandan himoyalangan, Code128/DataMatrix'da / + * _ % $ belgilari va GS",
        "Oynalar: «To'liq ekran» kassaga, egasi dasturiga, kirish oynasiga va bo'lim oynalariga qo'llanadi; 1024×600 masshtabi 80 %dan past emas; «1C» ko'rinishi: Enter, Num+, Num−",
        "«Tezkor tugmalar» va «Jurnallar va xatolar» huquqlari; saqlanmagan o'zgarishlar bilan sozlamalarni yopishda ogohlantirish",
        "Qorong'i mavzu: tugma yozuvlari o'qiladigan (kontrast kamida 4,5:1), tugmalardagi belgilar",
        "Egasi dasturi: umumiy sozlamalar kassaning umumiy hujjati orqali o'tadi (do'kon nomi, xodim kodlari, o'chirish sababi kassalarga yetadi), barcha kassalarning hisobdan chiqarish va taftish tarixi, «Tovar saqlandi» oynasi o'rniga lavha, diktor uchun tugma nomlari, yagona «kam» chegarasi",
        "Ombor → Qabul: elektron nakladnoylarni yuklash (Excel, CSV, XML, TORG-12, CommerceML)",
        "NurCRM tovarlarining o'rnatilgan ma'lumotnomasi (128 276 tovar): nomi, ma'lumotnomada bo'lsa — brendi va toifasi qo'yiladi; yangilari paydo bo'lishi bilan serverdan yangilanadi",
        "«Telefon skaner sifatida»: mobil ilova kamerasi bilan skan Wi-Fi orqali, QR juftlash bilan kassa chekiga tushadi; javoblarga imzo, takrorlardan himoya, telefonlarni sozlamalarda o'chirish (odatda o'chiq: Sozlamalar → Chop etish)",
        "Nur Maslahatchi: bo'limlar neyrotarmoqsiz ochiladi, javob faktlardan tuziladi — tezroq",
        "To'lov oynasi ekran o'rtasida («pastga siqish» sozlamasi avvalgi ko'rinishni qaytaradi)",
        "To'lov: «to'lov allaqachon o'tdi» degan yolg'on xabardan himoya (chek jetoni)",
        "Egasi dasturi va kassa sozlamalari kalitlar bo'yicha birlashtiriladi, zaxira nusxa bilan",
        "Smena: yopishdan oldin cheklar qayta yuboriladi; eski smenani davom ettirish mumkin — olish/kiritish bo'yicha maslahat bilan",
        "Nakladnoylar: ko'p sahifali, qayta yuklashdan himoya, o'xshash tovarlarni tasdiqlash",
        "Sayt mavzulari: «Sizning do'koningiz uchun» filtri, «mavzu sohangizga mos emas» ogohlantirishi, «Rasmsiz qo'llash»; Ombor → Rasm yuklash: oldindan ko'rish oynasi (asosiy / galereyaga)",
        "«Summa bo'yicha» tarozi yorlig'i qayta hisoblanmaydi; qadoqdan bitta dona qadoq narxini olmaydi",
        "Kassaning ovozli boshqaruvi olib tashlandi — endi SI yordamchisi bor",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-27: интеграция итоговой 1.17.63 - добавлены пункты потоков (Советник, окно оплаты, накладные, темы сайта, смены, пачки); в Android - общие пункты оплаты, смен и этикеток.
    // 2026-10-27: Android 1.17.63 (тестовая) — общее с Windows в 1.17.63 (оплата, цены, смены, возвраты, отчёты, сканер, тёмная тема), без пунктов только для компьютера.
    // 2026-10-27: Android 1.17.62 (тестовая) — то же, что на Windows в 1.17.62, без пунктов только для компьютера (печать, второй монитор, удалённая помощь, чеки с телефона, Склад).
    // 2026-10-24: Android 1.17.60 (тестовая) — то же, что на Windows в 1.17.60, без пунктов только для компьютера (воронка, окна и панель задач, горячие клавиши, импорт) и без программы владельца.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Оплата: Enter не проводит продажу, пока кнопка «Оплатить» неактивна; сканы клавиатурным сканером во время оплаты встают в очередь: товар добавится после оплаты в новый чек (при отмене оплаты — в тот же)",
        "Экран покупателя и QR показывают сумму после скидки и бонусов",
        "Защита от двойной продажи «в долг» и «смешанной» оплатой после обрыва связи; офлайн-чек не теряет клиента",
        "Предоплата долга: безналичная на старом пути не уходит как наличные, довнесение переживает перезапуск",
        "Суммы «1.000», «1e3», «(100)» дают ошибку; быстрые купюры 10/20; кнопка «+» у дробного количества",
        "Цены: сверка цен чека с сервером перед оплатой («Цена изменилась: 205 → 195»); цена на плитке обновляется за 8–10 с, если сервер отдаёт ленту изменений каталога; строгая проверка цены сервером; склад после прихода не откатывает цену",
        "Смены и возвраты: возврат со скидкой на чек не больше оплаченного; досылка внесений и изъятий закрытой смены; закрытие ждёт очередь чеков; закрытая «в очередь» смена не оживает; Z без ответа сервера считается с внесениями и возвратами",
        "X-ОТЧЁТ и Z-ОТЧЁТ на 5 языках; копия чека после возврата и смешанной оплаты по журналу кассы; уникальные имена PDF чеков; история чеков «Эта смена» — по периоду смены",
        "Сканер: старый буфер не съедает Enter, подтверждения защищены от скана, знаки / + * _ % $ и GS в Code128/DataMatrix",
        "Тёмная тема: читаемые надписи кнопок (контраст не ниже 4,5:1), значки в кнопках",
        "Оплата: защита от ложного сообщения «оплата уже прошла» (жетон чека)",
        "Смены: перед закрытием досылаются чеки; старую смену можно продолжить — с подсказкой об изъятии/внесении",
        "Весовая этикетка «по сумме» не пересчитывается; штука из пачки не получает цену пачки",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Төлөм: «Төлөө» баскычы жигердүү болбосо, Enter сатууну өткөрбөйт; төлөм учурундагы сканерлөөлөр кезекке турат: товар төлөмдөн кийин жаңы чекке кошулат (төлөм жокко чыкса — ошол чекке)",
        "Сатып алуучунун экраны жана QR арзандатуудан жана бонустардан кийинки сумманы көрсөтөт",
        "Байланыш үзүлгөндөн кийин «карызга» жана «аралаш» төлөм менен кош сатуудан коргоо; офлайн чек кардарды жоготпойт",
        "Карыздын алдын ала төлөмү: эски жолдо накталайсыз төлөм накталай болуп кетпейт, кошумча киргизүү кайра иштетүүдөн кийин да сакталат",
        "«1.000», «1e3», «(100)» суммалары ката берет; тез купюралар 10/20; бөлчөк санда «+» баскычы",
        "Баалар: төлөөдөн мурун чектин баасы сервер менен салыштырылат («Баа өзгөрдү: 205 → 195»); сервер каталогдун өзгөрүү лентасын берсе, плиткадагы баа 8–10 с ичинде жаңырат; бааны сервердин катуу текшерүүсү; кампа кирешеден кийин бааны кайтарбайт",
        "Смена жана кайтаруу: арзандатуу менен чекти кайтаруу төлөнгөндөн ашпайт; жабылган сменанын киргизүүлөрүн жана алуу операцияларын кайра жөнөтүү; жабуу чектер кезегин күтөт; «кезекке» жабылган смена кайра жанбайт; сервер жооп бербесе, Z киргизүүлөр жана кайтаруулар менен эсептелет",
        "X-ОТЧЕТ жана Z-ОТЧЕТ 5 тилде; кайтаруудан жана аралаш төлөмдөн кийин чектин көчүрмөсү касса журналы боюнча; чектердин PDF аттары уникалдуу; «Бул смена» чек тарыхы смена мезгили боюнча",
        "Сканер: эски буфер Enter баскычын жебейт, ырастоолор сканерден корголгон, Code128/DataMatrix кодунда / + * _ % $ белгилери жана GS",
        "Караңгы тема: баскыч жазуулары окулуучу (контраст 4,5:1ден кем эмес), баскычтардагы белгилер",
        "Төлөм: «төлөм өтүп кетти» деген жалган билдирүүдөн коргоо (чектин жетону)",
        "Смена: жабуудан мурун чектер жөнөтүлөт; эски сменаны улантууга болот — алуу/киргизүү боюнча кеңеш менен",
        "«Сумма боюнча» салмактуу этикетка кайра эсептелбейт; пачкадан бир даана пачканын баасын албайт",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Payment: Enter does not complete the sale while the “Pay” button is inactive; scans during payment go into a queue: the item is added after payment to the new receipt (if payment is canceled — to the same one)",
        "The customer screen and QR show the amount after discount and bonuses",
        "Protection against a double sale “on credit” and with “mixed” payment after a connection drop; an offline receipt does not lose the customer",
        "Debt prepayment: a cashless one on the old path no longer goes out as cash, the top-up survives a restart",
        "Amounts “1.000”, “1e3”, “(100)” give an error; quick banknotes 10/20; the “+” button for a fractional quantity",
        "Prices: receipt prices are checked against the server before payment (“Price changed: 205 → 195”); a tile price updates within 8–10 s if the server provides the catalog change feed; strict server-side price check; stock after receiving no longer rolls the price back",
        "Shifts and returns: a return with a receipt discount is not more than what was paid; cash-in and cash-out of a closed shift are re-sent; closing waits for the receipt queue; a shift closed “to the queue” does not come back to life; Z without a server reply is counted with cash-in and returns",
        "X-REPORT and Z-REPORT in 5 languages; a receipt copy after a return and mixed payment from the till journal; unique receipt PDF names; the “This shift” receipt history follows the shift period",
        "Scanner: an old buffer does not swallow Enter, confirmations are protected from scans, the characters / + * _ % $ and GS in Code128/DataMatrix",
        "Dark theme: readable button labels (contrast at least 4.5:1), icons in buttons",
        "Payment: protection against a false “payment already went through” message (receipt token)",
        "Shifts: receipts are re-sent before closing; an old shift can be continued — with a cash-out/cash-in hint",
        "A weighed label “by amount” is not recalculated; a single piece from a pack does not get the pack price",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Ödeme: «Öde» düğmesi etkin değilken Enter satışı tamamlamaz; ödeme sırasındaki taramalar sıraya girer: ürün ödemeden sonra yeni fişe eklenir (ödeme iptal edilirse — aynı fişe)",
        "Müşteri ekranı ve QR, indirim ve bonuslardan sonraki tutarı gösterir",
        "Bağlantı kopmasından sonra «veresiye» ve «karma» ödemeyle çifte satışa karşı koruma; çevrimdışı fiş müşteriyi kaybetmez",
        "Borç ön ödemesi: eski yolda nakitsiz ödeme nakit gibi gitmez, ek ödeme yeniden başlatmadan sonra da korunur",
        "«1.000», «1e3», «(100)» tutarları hata verir; hızlı banknotlar 10/20; kesirli miktarda «+» düğmesi",
        "Fiyatlar: fiş fiyatları ödemeden önce sunucuyla karşılaştırılır («Fiyat değişti: 205 → 195»); sunucu katalog değişiklik akışını verirse kutucuktaki fiyat 8–10 sn içinde güncellenir; sunucuda sıkı fiyat denetimi; depo, mal kabulden sonra fiyatı geri almaz",
        "Vardiya ve iade: fiş indirimli iade ödenenden fazla olmaz; kapalı vardiyanın para girişi ve çıkışı yeniden gönderilir; kapatma fiş kuyruğunu bekler; «kuyruğa» kapatılan vardiya yeniden canlanmaz; sunucu yanıt vermezse Z, giriş ve iadelerle hesaplanır",
        "X-RAPORU ve Z-RAPORU 5 dilde; iade ve karma ödemeden sonra fiş kopyası kasa günlüğüne göre; fiş PDF adları benzersiz; «Bu vardiya» fiş geçmişi vardiya dönemine göre",
        "Tarayıcı: eski arabellek Enter'ı yemez, onaylar taramadan korunur, Code128/DataMatrix'te / + * _ % $ işaretleri ve GS",
        "Koyu tema: okunaklı düğme yazıları (kontrast en az 4,5:1), düğmelerdeki simgeler",
        "Ödeme: yanlış «ödeme zaten geçti» iletisine karşı koruma (fiş jetonu)",
        "Vardiya: kapatmadan önce fişler yeniden gönderilir; eski vardiya devam ettirilebilir — para çıkışı/girişi ipucuyla",
        "«Tutara göre» tartılı etiket yeniden hesaplanmaz; paketten tek parça paket fiyatını almaz",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "To'lov: «To'lash» tugmasi faol bo'lmasa, Enter sotuvni o'tkazmaydi; to'lov paytidagi skanlar navbatga turadi: mahsulot to'lovdan keyin yangi chekka qo'shiladi (to'lov bekor qilinsa — shu chekka)",
        "Xaridor ekrani va QR chegirma va bonuslardan keyingi summani ko'rsatadi",
        "Aloqa uzilgandan keyin «qarzga» va «aralash» to'lov bilan ikki marta sotishdan himoya; oflayn chek mijozni yo'qotmaydi",
        "Qarzning oldindan to'lovi: eski yo'lda naqdsiz to'lov naqd bo'lib ketmaydi, qo'shimcha kiritish qayta ishga tushirishdan keyin ham saqlanadi",
        "«1.000», «1e3», «(100)» summalari xato beradi; tezkor pul belgilari 10/20; kasr miqdorda «+» tugmasi",
        "Narxlar: chek narxlari to'lovdan oldin server bilan solishtiriladi («Narx o'zgardi: 205 → 195»); server katalog o'zgarishlari lentasini bersa, plitkadagi narx 8–10 s ichida yangilanadi; narxni serverda qat'iy tekshirish; ombor kirimdan keyin narxni qaytarmaydi",
        "Smena va qaytarish: chegirmali chekni qaytarish to'langandan oshmaydi; yopilgan smenaning kiritish va olish amallari qayta yuboriladi; yopish cheklar navbatini kutadi; «navbatga» yopilgan smena qayta jonlanmaydi; server javob bermasa, Z kiritish va qaytarishlar bilan hisoblanadi",
        "X-HISOBOT va Z-HISOBOT 5 tilda; qaytarish va aralash to'lovdan keyin chek nusxasi kassa jurnali bo'yicha; chek PDF nomlari noyob; «Shu smena» cheklar tarixi smena davri bo'yicha",
        "Skaner: eski bufer Enter'ni yemaydi, tasdiqlar skandan himoyalangan, Code128/DataMatrix'da / + * _ % $ belgilari va GS",
        "Qorong'i mavzu: tugma yozuvlari o'qiladigan (kontrast kamida 4,5:1), tugmalardagi belgilar",
        "To'lov: «to'lov allaqachon o'tdi» degan yolg'on xabardan himoya (chek jetoni)",
        "Smena: yopishdan oldin cheklar qayta yuboriladi; eski smenani davom ettirish mumkin — olish/kiritish bo'yicha maslahat bilan",
        "«Summa bo'yicha» tarozi yorlig'i qayta hisoblanmaydi; qadoqdan bitta dona qadoq narxini olmaydi",
    ];

    public static string[] LatestForCurrentLanguage() =>
        OperatingSystem.IsAndroid()
            ? UserPreferences.Instance.Language switch
            {
                AppLanguage.Kyrgyz => AndroidLatestKy,
                AppLanguage.English => AndroidLatestEn,
                AppLanguage.Turkish => AndroidLatestTr,
                AppLanguage.Uzbek => AndroidLatestUz,
                _ => AndroidLatest,
            }
            : UserPreferences.Instance.Language switch
            {
                AppLanguage.Kyrgyz => LatestKy,
                AppLanguage.English => LatestEn,
                AppLanguage.Turkish => LatestTr,
                AppLanguage.Uzbek => LatestUz,
                _ => Latest,
            };

    public static string LatestAsBulletedText() =>
        string.Join("\n", System.Linq.Enumerable.Select(LatestForCurrentLanguage(), line => "• " + line));
}
