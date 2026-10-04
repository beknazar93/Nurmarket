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
        "Программа владельца: новый раздел «ИИ-советник» — чат с ИИ о вашем магазине. Видит выручку и прибыль из «Сводки», ABC-анализ, склад (цены, закупка, остатки) и долги клиентов, отвечает по вашим данным и не выдумывает цифр",
        "ИИ-советник показывает список должников с именами и телефонами, но в интернет уходят только суммы под кодами — имена и телефоны подставляет сама программа на вашем компьютере",
        "Изъятие из кассы больше наличных, чем в ней есть, запрещено; перед изъятием касса пишет, сколько останется",
        "«Пополнить склад?»: после «Нет» вопрос больше не открывается снова и возвращается прежнее количество; оплата не начинается, пока вопрос открыт",
        "Если кассу закрыли сразу после оплаты, оплаченные товары больше не возвращаются в чек",
        "«Тех. поддержка»: кнопка «Скопировать информацию об устройстве» — сведения о компьютере и кассе в буфер обмена, чтобы отправить их в поддержку",
    ];

    public static readonly string[] LatestKy =
    [
        "Ээсинин программасы: жаңы «ИИ-кеңешчи» бөлүмү — дүкөнүңүз жөнүндө ИИ менен маек. «Жыйынтыктагы» кирешени жана пайданы, ABC-анализди, кампаны (баалар, сатып алуу, калдыктар) жана кардарлардын карыздарын көрөт, маалыматыңыз боюнча жооп берет жана сандарды ойлоп чыгарбайт",
        "ИИ-кеңешчи карызкорлордун тизмесин аттары жана телефондору менен көрсөтөт, бирок интернетке коддор астындагы суммалар гана кетет — аттарды жана телефондорду программа өзү компьютериңизде коёт",
        "Кассада бар накталай акчадан көп алуу тыюу салынды; алуунун алдында касса канча калаарын жазат",
        "«Кампаны толуктайсызбы?»: «Жок» дегенден кийин суроо кайра ачылбайт жана мурунку сан кайтат; суроо ачык турганда төлөм башталбайт",
        "Кассаны төлөмдөн кийин дароо жапса, төлөнгөн товарлар чекке кайра кайтпайт",
        "«Тех колдоо»: «Түзмөк жөнүндө маалыматты көчүрүү» баскычы — компьютер жана касса жөнүндө маалымат алмашуу буферине, колдоого жиберүү үчүн",
    ];

    public static readonly string[] LatestEn =
    [
        "Owner program: new “AI advisor” section — chat with AI about your shop. It sees revenue and profit from the Overview, the ABC analysis, the warehouse (prices, cost, stock) and customer debts, answers from your data and never makes numbers up",
        "The AI advisor lists debtors with names and phones, but only amounts under codes go online — the program fills in names and phones on your computer",
        "Taking more cash out of the till than it holds is no longer allowed; before a cash-out the till shows what will remain",
        "“Restock?”: after “No” the question no longer reopens and the previous quantity comes back; payment won't start while the question is open",
        "If the till is closed right after payment, the paid items no longer come back into the receipt",
        "“Support”: a “Copy device information” button — computer and till details to the clipboard to send to support",
    ];

    public static readonly string[] LatestTr =
    [
        "İşletme sahibi programı: yeni «Yapay zekâ danışmanı» bölümü — mağazanız hakkında yapay zekâ ile sohbet. Özet'teki ciro ve kârı, ABC analizini, depoyu (fiyatlar, maliyet, stok) ve müşteri borçlarını görür, verilerinize göre yanıtlar ve rakam uydurmaz",
        "Yapay zekâ danışmanı borçluları ad ve telefonlarıyla listeler, ancak internete yalnızca kodlu tutarlar gider — ad ve telefonları program bilgisayarınızda yerleştirir",
        "Kasadaki nakitten fazla para çıkışı artık yasak; çıkıştan önce kasa ne kadar kalacağını gösterir",
        "«Stok eklensin mi?»: «Hayır»dan sonra soru tekrar açılmaz ve önceki miktar geri gelir; soru açıkken ödeme başlamaz",
        "Kasa ödemeden hemen sonra kapatılırsa ödenen ürünler artık fişe geri gelmez",
        "«Destek»: «Cihaz bilgilerini kopyala» düğmesi — bilgisayar ve kasa bilgileri panoya, desteğe göndermek için",
    ];

    public static readonly string[] LatestUz =
    [
        "Ega dasturi: yangi «SI maslahatchi» bo'limi — do'koningiz haqida SI bilan suhbat. «Umumiy ko'rinish»dagi tushum va foydani, ABC-tahlilni, omborni (narxlar, tannarx, qoldiqlar) va mijozlar qarzlarini ko'radi, ma'lumotlaringiz bo'yicha javob beradi va raqamlarni o'ylab topmaydi",
        "SI maslahatchi qarzdorlar ro'yxatini ism va telefonlari bilan ko'rsatadi, lekin internetga faqat kodlangan summalar ketadi — ism va telefonlarni dastur kompyuteringizda o'zi qo'yadi",
        "Kassadagi naqd puldan ko'p chiqim endi taqiqlangan; chiqimdan oldin kassa qancha qolishini yozadi",
        "«Omborni to'ldirasizmi?»: «Yo'q»dan keyin savol qayta ochilmaydi va oldingi miqdor qaytadi; savol ochiq turganda to'lov boshlanmaydi",
        "Kassa to'lovdan so'ng darhol yopilsa, to'langan mahsulotlar endi chekka qaytmaydi",
        "«Texnik yordam»: «Qurilma ma'lumotlarini nusxalash» tugmasi — kompyuter va kassa haqidagi ma'lumotlar buferga, yordamga yuborish uchun",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    public static readonly string[] AndroidLatest =
    [
        "Кассовые терминалы со встроенным сканером и принтером (Sunmi, iMin, Urovo, Newland и похожие): коды со сканера принимаются сами, встроенный принтер чеков находится сам",
        "При сворачивании касса больше не закрывается — работает в фоне, в шторке «NurMarket работает»",
        "Кнопка 📷 в поиске товара — сканер штрихкодов камерой",
        "Окна весового товара, возврата, оплаты долга, проката, истории чеков и настроек подстроены под телефон; каталог — по 12 товаров на странице, листается быстрее",
        "Программа владельца: «Подробнее» в продажах больше не зависает; новые разделы «ИИ-советник», «Воронка» (с источником клиента) и «WhatsApp»",
        "Изъятие больше наличных в кассе запрещено; если кассу закрыли сразу после оплаты, товары больше не возвращаются в чек",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Ичинде сканер жана принтер бар касса терминалдары (Sunmi, iMin, Urovo, Newland ж.б.): сканердин коддору өзү кабыл алынат, чек принтери өзү табылат",
        "Кичирейткенде касса мындан ары жабылбайт — фондо иштейт, билдирмелерде «NurMarket иштеп жатат»",
        "Товар издөөдө 📷 баскычы — камера менен штрихкод сканери",
        "Салмак товары, кайтаруу, карыз төлөө, прокат, чектердин тарыхы жана жөндөөлөр терезелери телефонго ылайыкталды; каталогдо баракта 12 товар, тезирээк жылат",
        "Ээсинин программасы: сатуудагы «Толугураак» мындан ары катып калбайт; жаңы бөлүмдөр «ИИ-кеңешчи», «Воронка» (кардардын булагы менен) жана «WhatsApp»",
        "Кассадагы накталайдан көп алуу тыюу салынды; төлөмдөн кийин касса дароо жабылса, товарлар чекке кайтпайт",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Till terminals with a built-in scanner and printer (Sunmi, iMin, Urovo, Newland and similar): scanner codes are accepted and the built-in receipt printer is found automatically",
        "Minimising no longer closes the till — it keeps running in the background with “NurMarket is running” in the notification shade",
        "The 📷 button in product search — barcode scanning with the camera",
        "Weighed-product, return, debt payment, rentals, receipt history and settings windows fit a phone; the catalog shows 12 products per page and pages faster",
        "Owner app: “Details” in sales no longer freezes; new sections “AI advisor”, “Funnel” (with client source) and “WhatsApp”",
        "A cash-out larger than the cash in the till is not allowed; if the till is closed right after payment, the items no longer come back into the receipt",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Dahili tarayıcılı ve yazıcılı kasa terminalleri (Sunmi, iMin, Urovo, Newland ve benzerleri): tarayıcı kodları kendiliğinden alınır, dahili fiş yazıcısı kendiliğinden bulunur",
        "Küçültünce kasa artık kapanmıyor — arka planda çalışır, bildirimlerde «NurMarket çalışıyor»",
        "Ürün aramada 📷 düğmesi — kamerayla barkod tarama",
        "Tartılı ürün, iade, borç ödeme, kiralama, fiş geçmişi ve ayarlar pencereleri telefona uyarlandı; katalogda sayfada 12 ürün, daha hızlı",
        "Sahip programı: satışlarda «Ayrıntılar» artık donmuyor; yeni bölümler «YZ danışmanı», «Huni» (müşteri kaynağıyla) ve «WhatsApp»",
        "Kasadaki nakitten fazla çıkış yasak; kasa ödemeden hemen sonra kapanırsa ürünler fişe geri gelmez",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Ichki skaner va printerli kassa terminallari (Sunmi, iMin, Urovo, Newland va shunga o'xshashlar): skaner kodlari o'zi qabul qilinadi, ichki chek printeri o'zi topiladi",
        "Yig'ilganda kassa endi yopilmaydi — fonda ishlaydi, bildirishnomalarda «NurMarket ishlayapti»",
        "Mahsulot qidiruvida 📷 tugmasi — kamera bilan shtrix-kod skaneri",
        "Tortiladigan mahsulot, qaytarish, qarz to'lash, prokat, cheklar tarixi va sozlamalar oynalari telefonga moslashtirildi; katalogda sahifada 12 ta mahsulot, tezroq",
        "Egasi dasturi: savdodagi «Batafsil» endi qotib qolmaydi; yangi bo'limlar «SI-maslahatchi», «Voronka» (mijoz manbasi bilan) va «WhatsApp»",
        "Kassadagi naqddan ko'p chiqim taqiqlangan; to'lovdan so'ng kassa darhol yopilsa, mahsulotlar chekka qaytmaydi",
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
