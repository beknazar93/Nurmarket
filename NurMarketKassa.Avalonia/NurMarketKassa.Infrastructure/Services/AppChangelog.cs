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
        "Исправлено, главное: офлайн-чек мог провестись дважды. Если касса закрывалась (или её снимали через диспетчер задач) в момент выгрузки очереди, чек возвращался в очередь и отправлялся заново — покупатель платил один раз, а в NurCRM появлялось два чека и товар списывался дважды. Защита от этого в коде была, но её условие выключало её ровно в этом случае",
        "Исправлено: чек, который сервер отверг один раз, терялся навсегда. Он уходил в «Некорректные чеки», и вернуть его оттуда было нечем — деньги в кассе, продажи в NurCRM нет. Добавлена кнопка «Отправить повторно»",
        "Исправлено: возврат ВСЕГО чека не попадал ни в один отчёт. Запись в итоги смены делал только построчный возврат. Вернули покупателю весь чек наличными — и кассир сдавал смену с «недостачей» ровно на эту сумму. Заодно перестал теряться второй возврат по тому же чеку и второй платёж по одному долгу: они молча отбрасывались из-за того, что запись велась по номеру чека, а не по операции",
        "Исправлено: оплата долга попадала в отчёт ДО того, как проходила. Сеть отваливалась — в отчёте смены значилась оплата, а денег не было",
        "Исправлено: в магазине с двумя кассами вторая касса работала в смене первой. Продажи уходили в чужую смену, а закрытие смены со второй кассы закрывало смену первой",
        "Исправлено: потолок скидки («Максимальная скидка» в настройках компании) действовал только в корзине. В окне оплаты кассир мог поставить любую скидку в обход ограничения. Теперь ограничение одно на оба места, и при неизвестных правах оно применяется, а не снимается",
        "Исправлено: изъятие денег через «Доп. услуга» не попадало в строку «Расход» отчёта смены, а такое же изъятие через «Историю смен» — попадало. Одна операция давала разные цифры в зависимости от кнопки",
        "Исправлено: строка «Списания» в отчётах всегда показывала ноль. Теперь списание со склада попадает в отчёт по закупочной цене — это и есть сумма потери",
        "Исправлено: на тарифе «Старт» при запуске без интернета открывались все платные разделы. Теперь касса помнит тариф с прошлого входа",
        "Исправлено: «Выручка», «Возвраты» и «Чеков» считались по-разному даже внутри одного экрана. «Возвраты» показывали удаления позиций из корзины ДО оплаты — это не возвраты вовсе; теперь берутся настоящие возвраты. Выручка показывается за вычетом возвратов, как и средний чек рядом. В числе чеков больше не учитываются записи журнала удалений",
        "Исправлено: скидка на отдельную позицию не сохранялась в историю продаж, из-за чего выгрузка в Excel и Word, Telegram-сводки и ABC завышали выручку ровно на сумму таких скидок",
        "Исправлено: окно оплаты нельзя было закрыть, пока оно ждёт ответа сервера — ни кнопки, ни Escape, ни рамки. При зависшем запросе кассир с очередью оставался заблокирован. Теперь через 20 секунд появляется выход и предупреждение, что оплата могла уже пройти",
        "Исправлено: в тёмной теме список товаров в массовой печати ценников был невидим — белый текст на белой подложке",
        "Исправлено: любой сбой при изменении чека стирал чек целиком и без записи в журнал. Теперь набранные позиции остаются на месте, а причина попадает в логи",
        "Устойчивость локальной базы: слияние журнала WAL теперь выполняется и при выключении компьютера через «Пуск», и при завершении процесса — раньше только при закрытии окна. Плюс устранена гонка, из-за которой могли создаться два объекта базы со своими замками, и блокировка переставала работать. Это самые вероятные оставшиеся причины повреждения базы",
        "Новое: «ABC-анализ» стал отдельным разделом — свой пункт в боковом меню и своё окно на весь экран, с пятью срезами: товары по выручке, по прибыли, по количеству, категории и бренды",
        "Новое: вкладка «Сезонность» — какие товары продаются в одни месяцы и не продаются в другие. Показывает выручку магазина по месяцам, списки сезонных товаров по сезонам и полосу из двенадцати месяцев на каждый товар. Месяцы, за которые истории нет, помечены отдельно: пока касса не проработала хотя бы два сезона, выводы о сезонности не делаются — иначе сезонным выглядел бы весь ассортимент просто потому, что раньше касса не работала",
    ];

    public static readonly string[] LatestKy =
    [
        "Оңдолду, эң негизгиси: офлайн чек эки жолу өтүп кетиши мүмкүн эле. Касса кезек жүктөлүп жатканда жабылса, чек кайра жөнөтүлүп, NurCRMде эки чек пайда болчу",
        "Оңдолду: сервер бир жолу четке каккан чек түбөлүккө жоголчу. Эми «Кайра жөнөтүү» баскычы бар",
        "Оңдолду: БҮТ чектин кайтарылышы бир да отчётко түшчү эмес. Ошондой эле бир чек боюнча экинчи кайтаруу жана бир карыз боюнча экинчи төлөм жоголбой калды",
        "Оңдолду: карызды төлөө чындап өтө электе эле отчётко жазылчу",
        "Оңдолду: эки кассасы бар дүкөндө экинчи касса биринчисинин сменасында иштеп жатчу",
        "Оңдолду: арзандатуунун чеги («Максималдуу арзандатуу») бир гана себетте иштечү, төлөө терезесинде иштечү эмес",
        "Оңдолду: «Кошумча кызмат» аркылуу акча алуу сменанын «Чыгым» сабына түшчү эмес",
        "Оңдолду: отчётторлордогу «Эсептен чыгаруу» сабы ар дайым нөл көрсөтчү",
        "Оңдолду: «Старт» тарифинде интернетсиз иштетилгенде бардык акы төлөнүүчү бөлүмдөр ачылып калчу",
        "Оңдолду: «Түшкөн акча», «Кайтаруулар» жана «Чектер» бир экрандын ичинде да ар башкача эсептелчү",
        "Оңдолду: айрым позицияга берилген арзандатуу сатуу тарыхына сакталчу эмес — Excel, Word жана Telegram отчётторунда түшкөн акча ашыкча көрсөтүлчү",
        "Оңдолду: төлөө терезесин сервердин жообун күтүп турганда жабууга мүмкүн эмес болчу. Эми 20 секунддан кийин чыгуу пайда болот",
        "Оңдолду: караңгы режимде баа энбелгилерин топтоп басып чыгаруудагы товарлар тизмеси көрүнчү эмес",
        "Жергиликтүү базанын туруктуулугу: WAL журналын бириктирүү эми компьютерди «Пуск» аркылуу өчүргөндө да аткарылат",
        "Жаңы: «ABC-анализ» өзүнчө бөлүм болду — беш кесим менен",
        "Жаңы: «Мезгилдүүлүк» кыстырмасы — кайсы товарлар жайында сатылып, кышында сатылбай турганын көрсөтөт",
    ];

    public static readonly string[] LatestEn =
    [
        "Fixed, most important: an offline receipt could be posted twice. If the till was closed (or killed from Task Manager) while the queue was uploading, the receipt went back into the queue and was sent again — the customer paid once, but NurCRM got two receipts and the stock was deducted twice. The guard against this existed, but its condition switched it off in exactly this case",
        "Fixed: a receipt the server rejected once was lost forever. It went to Irregular receipts with no way back — money in the till, no sale in NurCRM. A Resend button was added",
        "Fixed: a full receipt return did not reach any report. Only line-by-line returns were recorded. Also, a second return on the same receipt and a second payment on the same debt are no longer silently dropped",
        "Fixed: a debt payment was written to the report BEFORE it went through",
        "Fixed: in a shop with two tills, the second till worked inside the first one's shift",
        "Fixed: the discount cap applied only in the cart, not in the payment window",
        "Fixed: cash withdrawal via Extra service did not reach the Expense line of the shift report",
        "Fixed: the Write-offs line in reports always showed zero",
        "Fixed: on the Start plan, starting without internet unlocked every paid section",
        "Fixed: Revenue, Returns and Receipts were calculated differently even within one screen. Returns showed cart items removed BEFORE payment — not returns at all",
        "Fixed: a discount on an individual line was not saved to the sales history, so Excel, Word and Telegram reports overstated revenue by exactly that amount",
        "Fixed: the payment window could not be closed while waiting for the server. After 20 seconds there is now a way out",
        "Fixed: in dark theme the product list in bulk price-tag printing was invisible",
        "Local database resilience: the WAL journal is now merged when the computer is shut down from Start and when the process exits, not only on window close",
        "New: ABC analysis is now a section of its own, with five views",
        "New: a Seasonality tab — which products sell in some months and not in others",
    ];

    public static readonly string[] LatestTr =
    [
        "Düzeltildi, en önemlisi: çevrimdışı fiş iki kez işlenebiliyordu. Kuyruk yüklenirken kasa kapatılırsa fiş yeniden gönderiliyor ve NurCRM'de iki fiş oluşuyordu",
        "Düzeltildi: sunucunun bir kez reddettiği fiş sonsuza dek kayboluyordu. «Yeniden gönder» düğmesi eklendi",
        "Düzeltildi: fişin tamamının iadesi hiçbir rapora girmiyordu. Ayrıca aynı fişteki ikinci iade ve aynı borçtaki ikinci ödeme artık kaybolmuyor",
        "Düzeltildi: borç ödemesi gerçekleşmeden ÖNCE rapora yazılıyordu",
        "Düzeltildi: iki kasalı mağazada ikinci kasa birincinin vardiyasında çalışıyordu",
        "Düzeltildi: indirim üst sınırı yalnızca sepette geçerliydi, ödeme penceresinde değil",
        "Düzeltildi: «Ek hizmet» üzerinden para çekme vardiya raporunun «Gider» satırına girmiyordu",
        "Düzeltildi: raporlardaki «Zayiat» satırı her zaman sıfır gösteriyordu",
        "Düzeltildi: «Start» tarifesinde internetsiz başlatıldığında tüm ücretli bölümler açılıyordu",
        "Düzeltildi: «Ciro», «İadeler» ve «Fiş sayısı» tek ekran içinde bile farklı hesaplanıyordu",
        "Düzeltildi: tek bir kaleme verilen indirim satış geçmişine kaydedilmiyordu; Excel, Word ve Telegram raporları ciroyu bu kadar fazla gösteriyordu",
        "Düzeltildi: ödeme penceresi sunucu yanıtı beklenirken kapatılamıyordu. Artık 20 saniye sonra bir çıkış var",
        "Düzeltildi: koyu temada toplu etiket yazdırmadaki ürün listesi görünmüyordu",
        "Yerel veritabanı dayanıklılığı: WAL günlüğü artık bilgisayar «Başlat»tan kapatıldığında da birleştiriliyor",
        "Yeni: ABC analizi beş kesitle kendi bölümü oldu",
        "Yeni: «Mevsimsellik» sekmesi — hangi ürünlerin bazı aylarda satılıp bazılarında satılmadığı",
    ];

    public static readonly string[] LatestUz =
    [
        "Tuzatildi, eng muhimi: oflayn chek ikki marta o'tishi mumkin edi. Navbat yuklanayotganda kassa yopilsa, chek qayta yuborilib, NurCRMda ikkita chek paydo bo'lardi",
        "Tuzatildi: server bir marta rad etgan chek butunlay yo'qolardi. «Qayta yuborish» tugmasi qo'shildi",
        "Tuzatildi: BUTUN chekning qaytarilishi birorta hisobotga tushmasdi. Shuningdek bir chek bo'yicha ikkinchi qaytarish va bir qarz bo'yicha ikkinchi to'lov endi yo'qolmaydi",
        "Tuzatildi: qarz to'lovi amalga oshmasdan OLDIN hisobotga yozilardi",
        "Tuzatildi: ikkita kassali do'konda ikkinchi kassa birinchisining smenasida ishlardi",
        "Tuzatildi: chegirma chegarasi faqat savatda ishlardi, to'lov oynasida emas",
        "Tuzatildi: «Qo'shimcha xizmat» orqali pul olish smena hisobotining «Xarajat» qatoriga tushmasdi",
        "Tuzatildi: hisobotlardagi «Hisobdan chiqarish» qatori doim nol ko'rsatardi",
        "Tuzatildi: «Start» tarifida internetsiz ishga tushirilganda barcha pullik bo'limlar ochilardi",
        "Tuzatildi: «Tushum», «Qaytarishlar» va «Cheklar» bitta ekran ichida ham har xil hisoblanardi",
        "Tuzatildi: alohida pozitsiyaga berilgan chegirma sotuvlar tarixiga saqlanmasdi",
        "Tuzatildi: to'lov oynasini server javobini kutayotganda yopib bo'lmasdi. Endi 20 soniyadan keyin chiqish bor",
        "Tuzatildi: qorong'i mavzuda narx yorliqlarini ommaviy chop etishdagi mahsulotlar ro'yxati ko'rinmasdi",
        "Mahalliy baza barqarorligi: WAL jurnali endi kompyuter «Pusk» orqali o'chirilganda ham birlashtiriladi",
        "Yangi: ABC tahlili beshta kesim bilan alohida bo'lim bo'ldi",
        "Yangi: «Mavsumiylik» ichki sahifasi — qaysi mahsulotlar ba'zi oylarda sotilib, ba'zilarida sotilmasligi",
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