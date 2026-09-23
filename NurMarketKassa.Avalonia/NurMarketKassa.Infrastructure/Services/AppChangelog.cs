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
        "ВАЖНОЕ ПРО ДЕНЬГИ. Офлайн-чек мог провестись дважды: если касса закрывалась в момент выгрузки очереди, чек возвращался в очередь и отправлялся заново — покупатель платил один раз, а в NurCRM появлялось два чека и товар списывался дважды. Защита в коде была, но её условие выключало её ровно в этом случае",
        "Чек, который сервер отверг один раз, терялся навсегда: уходил в «Некорректные чеки» без возможности вернуть — деньги в кассе, продажи в NurCRM нет. Добавлена кнопка «Отправить повторно»",
        "Возврат ВСЕГО чека не попадал ни в один отчёт — запись в итоги смены делал только построчный возврат. Кассир сдавал смену с «недостачей» ровно на сумму возврата. Перестал теряться и второй возврат по тому же чеку, и второй платёж по одному долгу",
        "Оплата долга попадала в отчёт смены ДО того, как проходила: сеть отваливалась — в отчёте оплата есть, денег нет",
        "В магазине с двумя кассами вторая работала в смене первой: продажи уходили в чужую смену, а закрытие смены со второй кассы закрывало смену первой",
        "Потолок скидки («Максимальная скидка» в настройках компании) действовал только в корзине — в окне оплаты кассир мог поставить любую скидку в обход ограничения. Теперь ограничение одно на оба места, и при неизвестных правах оно применяется, а не снимается",
        "Изъятие денег через «Доп. услуга» не попадало в строку «Расход» отчёта смены, а такое же изъятие через «Историю смен» — попадало. Одна операция давала разные цифры в зависимости от кнопки",
        "Строка «Списания» в отчётах всегда показывала ноль. Теперь списание со склада попадает в отчёт по закупочной цене — это и есть сумма потери",
        "На тарифе «Старт» при запуске без интернета открывались все платные разделы. Теперь касса помнит тариф с прошлого входа",
        "«Продажи» показывали не тот период: запрос уходил без дат, а размер страницы сервер молча урезал до 80 записей, поэтому любой период фильтровался внутри одних и тех же 80 последних чеков. Из-за этого выручка расходилась с веб-аналитикой NurCRM",
        "«Выручка», «Возвраты» и «Чеков» считались по-разному даже внутри одного экрана. «Возвраты» показывали удаления позиций из корзины ДО оплаты — это не возвраты вовсе; теперь берутся настоящие возвраты, выручка показывается за их вычетом, а записи журнала удалений больше не считаются чеками",
        "Скидка на отдельную позицию не сохранялась в историю продаж, из-за чего выгрузка в Excel и Word, Telegram-сводки и ABC завышали выручку ровно на сумму таких скидок",
        "Безналичная оплата: банков было жёстко зашито три, хотя в настройках предлагалось семь — владелец настраивал QR для банка, которого при оплате просто не существовало. Теперь в настройках все 26 банков Кыргызстана по реестру НБКР, у каждого галочка «Показывать при оплате», а кассир видит только отмеченные. Банки, добавленные вручную, тоже наконец появились в списке. Плюс логотипы MBank и ФинкаБанка — их файлов в программе не было вообще, поэтому логотипы не показывались никогда",
        "Окно оплаты нельзя было закрыть, пока оно ждёт ответа сервера — ни кнопки, ни Escape, ни рамки. При зависшем запросе кассир с очередью оставался заблокирован. Теперь через 20 секунд появляется выход и предупреждение, что оплата могла уже пройти",
        "Устойчивость локальной базы: слияние журнала WAL теперь выполняется и при выключении компьютера через «Пуск», и при завершении процесса — раньше только при закрытии окна. Устранена гонка, из-за которой могли создаться два объекта базы со своими замками. Любой сбой при изменении чека больше не стирает набранные позиции",
        "НОВОЕ: «ABC-анализ» — отдельный раздел со своим пунктом в меню и окном на весь экран. Пять срезов: товары по выручке, по прибыли, по количеству, категории и бренды. В каждом — диаграмма «доли позиций против долей выручки», диаграмма Парето с границами 80 % и 95 % и полная таблица",
        "НОВОЕ: «Сезонность» — какие товары продаются в одни месяцы и не продаются в другие. Выручка магазина по месяцам, списки сезонных товаров по сезонам и полоса из двенадцати месяцев на каждый товар. Месяцы без истории помечены отдельно: пока касса не проработала два сезона, выводы не делаются — иначе сезонным выглядел бы весь ассортимент просто потому, что раньше касса не работала",
        "НОВОЕ в телеграм-боте: /abc — ABC по всем срезам сразу, /sezon — сезонность, /soveti — рекомендации: что срочно заказать из группы A, где выручка есть, а прибыли нет, и что лежит на складе без единой продажи с суммой замороженных денег",
        "НОВОЕ: выгрузка аналитики в Excel и Word на экранах «Продажи» и «Финансы». В Excel — оформленные таблицы с закреплённой шапкой и НАСТОЯЩИЕ диаграммы Excel, которые пересчитываются вместе с данными, а не картинки. По листу на каждый срез ABC",
        "«Продажи» и «Финансы» теперь работают без интернета: раньше экран оставался пустым с красной строкой «Этот хост неизвестен», хотя все данные лежат в самой кассе",
    ];

    public static readonly string[] LatestKy =
    [
        "АКЧА ЖӨНҮНДӨ МААНИЛҮҮ. Офлайн чек эки жолу өтүп кетиши мүмкүн эле: кезек жүктөлүп жатканда касса жабылса, чек кайра жөнөтүлүп, NurCRMде эки чек пайда болчу",
        "Сервер бир жолу четке каккан чек түбөлүккө жоголчу. Эми «Кайра жөнөтүү» баскычы бар",
        "БҮТ чектин кайтарылышы бир да отчётко түшчү эмес. Бир чек боюнча экинчи кайтаруу жана бир карыз боюнча экинчи төлөм да жоголбой калды",
        "Карызды төлөө чындап өтө электе эле сменанын отчётуна жазылчу",
        "Эки кассасы бар дүкөндө экинчи касса биринчисинин сменасында иштеп жатчу",
        "Арзандатуунун чеги бир гана себетте иштечү, төлөө терезесинде иштечү эмес",
        "«Кошумча кызмат» аркылуу акча алуу сменанын «Чыгым» сабына түшчү эмес",
        "Отчётторлордогу «Эсептен чыгаруу» сабы ар дайым нөл көрсөтчү",
        "«Старт» тарифинде интернетсиз иштетилгенде бардык акы төлөнүүчү бөлүмдөр ачылып калчу",
        "«Сатуулар» туура эмес мезгилди көрсөтүп жаткан: сурам күндөрсүз кетип, сервер барактын көлөмүн 80 жазууга кыскартчу",
        "«Түшкөн акча», «Кайтаруулар» жана «Чектер» бир экрандын ичинде да ар башкача эсептелчү",
        "Айрым позицияга берилген арзандатуу сатуу тарыхына сакталчу эмес — отчётторлордо түшкөн акча ашыкча көрсөтүлчү",
        "Накталай эмес төлөө: мурда үч банк гана бар эле. Эми жөндөөлөрдө Кыргызстандын бардык 26 банкы бар, ар биринде «Төлөөдө көрсөтүү» белгиси, кассир белгиленгендерин гана көрөт. MBank жана ФинкаБанктын логотиптери да пайда болду",
        "Төлөө терезесин сервердин жообун күтүп турганда жабууга мүмкүн эмес болчу. Эми 20 секунддан кийин чыгуу пайда болот",
        "Жергиликтүү базанын туруктуулугу: WAL журналын бириктирүү эми компьютерди «Пуск» аркылуу өчүргөндө да аткарылат",
        "ЖАҢЫ: «ABC-анализ» — өзүнчө бөлүм, беш кесим менен: товарлар түшкөн акча, пайда, саны боюнча, категориялар жана бренддер",
        "ЖАҢЫ: «Мезгилдүүлүк» — кайсы товарлар жайында сатылып, кышында сатылбай турганын көрсөтөт",
        "ЖАҢЫ телеграм-ботто: /abc, /sezon жана /soveti — сунуштар",
        "ЖАҢЫ: аналитиканы Excel жана Word'ко жүктөө, чыныгы Excel диаграммалары менен",
        "«Сатуулар» жана «Каржы» эми интернетсиз да иштейт",
    ];

    public static readonly string[] LatestEn =
    [
        "IMPORTANT, ABOUT MONEY. An offline receipt could be posted twice: if the till was closed while the queue was uploading, the receipt went back into the queue and was sent again - the customer paid once, but NurCRM got two receipts and the stock was deducted twice. The guard existed, but its condition switched it off in exactly this case",
        "A receipt the server rejected once was lost forever - money in the till, no sale in NurCRM. A Resend button was added",
        "A full receipt return did not reach any report; only line-by-line returns were recorded. A second return on the same receipt and a second payment on the same debt are no longer silently dropped",
        "A debt payment was written to the shift report BEFORE it went through",
        "In a shop with two tills, the second one worked inside the first one's shift",
        "The discount cap applied only in the cart, not in the payment window",
        "Cash withdrawal via Extra service did not reach the Expense line of the shift report",
        "The Write-offs line in reports always showed zero",
        "On the Start plan, starting without internet unlocked every paid section",
        "The Sales screen showed the wrong period: the request went out with no dates and the server trimmed the page size to 80 records",
        "Revenue, Returns and Receipts were calculated differently even within one screen. Returns showed cart items removed BEFORE payment - not returns at all",
        "A discount on an individual line was not saved to the sales history, so exports and Telegram reports overstated revenue by exactly that amount",
        "Card payment: only three banks were hardcoded while Settings offered seven, so a QR could be set up for a bank that did not exist at checkout. Settings now list all 26 Kyrgyz banks with a Show at checkout tick each, and the cashier sees only the ticked ones. MBank and FINCA logos were added too - their files did not exist in the app at all",
        "The payment window could not be closed while waiting for the server. After 20 seconds there is now a way out",
        "Local database resilience: the WAL journal is now merged when the computer is shut down from Start and when the process exits",
        "NEW: ABC analysis is a section of its own, with five views: products by revenue, by profit, by quantity, categories and brands",
        "NEW: Seasonality - which products sell in some months and not in others",
        "NEW in the Telegram bot: /abc, /sezon and /soveti with recommendations",
        "NEW: analytics export to Excel and Word, with real Excel charts rather than pictures",
        "Sales and Finance now work without internet",
    ];

    public static readonly string[] LatestTr =
    [
        "PARA HAKKINDA ÖNEMLİ. Çevrimdışı fiş iki kez işlenebiliyordu: kuyruk yüklenirken kasa kapatılırsa fiş yeniden gönderiliyor ve NurCRM'de iki fiş oluşuyordu",
        "Sunucunun bir kez reddettiği fiş sonsuza dek kayboluyordu. «Yeniden gönder» düğmesi eklendi",
        "Fişin tamamının iadesi hiçbir rapora girmiyordu. Aynı fişteki ikinci iade ve aynı borçtaki ikinci ödeme artık kaybolmuyor",
        "Borç ödemesi gerçekleşmeden ÖNCE vardiya raporuna yazılıyordu",
        "İki kasalı mağazada ikinci kasa birincinin vardiyasında çalışıyordu",
        "İndirim üst sınırı yalnızca sepette geçerliydi, ödeme penceresinde değil",
        "«Ek hizmet» üzerinden para çekme vardiya raporunun «Gider» satırına girmiyordu",
        "Raporlardaki «Zayiat» satırı her zaman sıfır gösteriyordu",
        "«Start» tarifesinde internetsiz başlatıldığında tüm ücretli bölümler açılıyordu",
        "Satışlar ekranı yanlış dönemi gösteriyordu: istek tarihsiz gidiyor, sunucu sayfa boyutunu 80 kayda kırpıyordu",
        "«Ciro», «İadeler» ve «Fiş sayısı» tek ekran içinde bile farklı hesaplanıyordu",
        "Tek bir kaleme verilen indirim satış geçmişine kaydedilmiyordu",
        "Kartla ödeme: yalnızca üç banka sabit kodluydu. Artık Ayarlar'da Kırgızistan'ın 26 bankasının tamamı ve her birinde «Ödemede göster» kutusu var, kasiyer yalnızca işaretlenenleri görür. MBank ve FINCA logoları da eklendi",
        "Ödeme penceresi sunucu yanıtı beklenirken kapatılamıyordu. Artık 20 saniye sonra bir çıkış var",
        "Yerel veritabanı dayanıklılığı: WAL günlüğü artık bilgisayar «Başlat»tan kapatıldığında da birleştiriliyor",
        "YENİ: ABC analizi beş kesitle kendi bölümü oldu",
        "YENİ: «Mevsimsellik» - hangi ürünlerin bazı aylarda satılıp bazılarında satılmadığı",
        "YENİ Telegram botunda: /abc, /sezon ve /soveti önerileri",
        "YENİ: Excel ve Word'e analiz aktarımı, resim değil gerçek Excel grafikleriyle",
        "Satışlar ve Finans artık internetsiz de çalışıyor",
    ];

    public static readonly string[] LatestUz =
    [
        "PUL HAQIDA MUHIM. Oflayn chek ikki marta o'tishi mumkin edi: navbat yuklanayotganda kassa yopilsa, chek qayta yuborilib, NurCRMda ikkita chek paydo bo'lardi",
        "Server bir marta rad etgan chek butunlay yo'qolardi. «Qayta yuborish» tugmasi qo'shildi",
        "BUTUN chekning qaytarilishi birorta hisobotga tushmasdi. Bir chek bo'yicha ikkinchi qaytarish va bir qarz bo'yicha ikkinchi to'lov endi yo'qolmaydi",
        "Qarz to'lovi amalga oshmasdan OLDIN smena hisobotiga yozilardi",
        "Ikkita kassali do'konda ikkinchi kassa birinchisining smenasida ishlardi",
        "Chegirma chegarasi faqat savatda ishlardi, to'lov oynasida emas",
        "«Qo'shimcha xizmat» orqali pul olish smena hisobotining «Xarajat» qatoriga tushmasdi",
        "Hisobotlardagi «Hisobdan chiqarish» qatori doim nol ko'rsatardi",
        "«Start» tarifida internetsiz ishga tushirilganda barcha pullik bo'limlar ochilardi",
        "«Sotuvlar» noto'g'ri davrni ko'rsatardi: so'rov sanasiz ketar, server sahifa hajmini 80 yozuvga qisqartirardi",
        "«Tushum», «Qaytarishlar» va «Cheklar» bitta ekran ichida ham har xil hisoblanardi",
        "Alohida pozitsiyaga berilgan chegirma sotuvlar tarixiga saqlanmasdi",
        "Naqd pulsiz to'lov: avval faqat uchta bank bor edi. Endi sozlamalarda Qirg'izistonning barcha 26 banki va har birida «To'lovda ko'rsatish» belgisi bor, kassir faqat belgilanganlarini ko'radi. MBank va FINCA logotiplari ham qo'shildi",
        "To'lov oynasini server javobini kutayotganda yopib bo'lmasdi. Endi 20 soniyadan keyin chiqish bor",
        "Mahalliy baza barqarorligi: WAL jurnali endi kompyuter «Pusk» orqali o'chirilganda ham birlashtiriladi",
        "YANGI: ABC tahlili beshta kesim bilan alohida bo'lim bo'ldi",
        "YANGI: «Mavsumiylik» - qaysi mahsulotlar ba'zi oylarda sotilib, ba'zilarida sotilmasligi",
        "YANGI Telegram botida: /abc, /sezon va /soveti tavsiyalari",
        "YANGI: tahlilni Excel va Word ga yuklash, rasm emas haqiqiy Excel diagrammalari bilan",
        "«Sotuvlar» va «Moliya» endi internetsiz ham ishlaydi",
    ];

    /// <summary>Список на языке интерфейса (2026-09-07).</summary>
    public static string[] LatestForCurrentLanguage() =>
        UserPreferences.Instance.Language switch
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