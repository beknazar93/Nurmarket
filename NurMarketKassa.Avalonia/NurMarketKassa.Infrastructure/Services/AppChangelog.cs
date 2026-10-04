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
        "Нет интернета или сервер не отвечает — касса не ждёт: продажа сразу уходит в очередь, смена открывается и закрывается за 2–3 секунды, отправка на сервер — в фоне и без двойных продаж",
        "Возврат: на чеке и в сообщении — сумма, которую нужно выдать покупателю («Выдать покупателю: N сом»)",
        "QR клиента из приложения NurCRM «Мои баллы»: отсканировали — клиент сразу в чеке; нового клиента касса предложит добавить с его номером",
        "Сенсорный экран: два пальца прокручивают список и не нажимают две кнопки сразу",
        "Телеграм-бот: ИИ на сервере можно включить своим ключом прямо из программы",
        "Z-отчёт: предоплата долга больше не считается дважды",
        "Связь с сервером быстрее: ответы приходят сжатыми — в 8 раз меньше интернета",
    ];

    public static readonly string[] LatestKy =
    [
        "Интернет жок же сервер жооп бербесе — касса күтпөйт: сатуу дароо кезекке кетет, смена 2–3 секундада ачылат жана жабылат, серверге жөнөтүү фондо жана кош сатуусуз",
        "Кайтаруу: чекте жана билдирүүдө сатып алуучуга берилүүчү сумма («Сатып алуучуга берүү: N сом»)",
        "NurCRM «Менин баллдарым» тиркемесинен кардардын QR коду: сканерлегенде кардар дароо чекте; жаңы кардарды касса анын номери менен кошууну сунуштайт",
        "Сенсордук экран: эки манжа тизмени жылдырат жана эки баскычты бир убакта баспайт",
        "Телеграм-бот: сервердеги ЖИни өз ачкычыңыз менен программадан эле күйгүзсө болот",
        "Z-отчёт: карыздын алдын ала төлөмү эки жолу эсептелбейт",
        "Сервер менен байланыш тезирээк: жооптор кысылып келет — интернет 8 эсе аз",
    ];

    public static readonly string[] LatestEn =
    [
        "No internet or the server is not responding — the till does not wait: a sale goes to the queue at once, a shift opens and closes in 2–3 seconds, sending to the server happens in the background without double sales",
        "Returns: the receipt and the message show the amount to give the customer (“Give the customer: N som”)",
        "Customer QR from the NurCRM “My points” app: scan it and the customer is in the receipt; for a new customer the till offers to add them with their number",
        "Touch screen: two fingers scroll the list and no longer press two buttons at once",
        "Telegram bot: the AI on the server can be turned on with your own key right from the program",
        "Z report: a debt prepayment is no longer counted twice",
        "Faster connection to the server: responses come compressed — 8 times less internet",
    ];

    public static readonly string[] LatestTr =
    [
        "İnternet yoksa veya sunucu yanıt vermiyorsa kasa beklemiyor: satış hemen kuyruğa gider, vardiya 2–3 saniyede açılıp kapanır, sunucuya gönderim arka planda ve çift satış olmadan",
        "İade: fişte ve mesajda müşteriye verilecek tutar («Müşteriye ver: N som»)",
        "NurCRM «Puanlarım» uygulamasından müşteri QR'ı: okutunca müşteri hemen fişte; yeni müşteriyi kasa numarasıyla eklemeyi önerir",
        "Dokunmatik ekran: iki parmak listeyi kaydırır, iki düğmeye birden basmaz",
        "Telegram botu: sunucudaki yapay zekâ kendi anahtarınızla doğrudan programdan açılabilir",
        "Z raporu: borç ön ödemesi artık iki kez sayılmıyor",
        "Sunucuyla bağlantı daha hızlı: yanıtlar sıkıştırılmış gelir — 8 kat daha az internet",
    ];

    public static readonly string[] LatestUz =
    [
        "Internet yo'q yoki server javob bermasa — kassa kutmaydi: sotuv darhol navbatga ketadi, smena 2–3 soniyada ochiladi va yopiladi, serverga yuborish fonda va ikki marta sotuvsiz",
        "Qaytarish: chekda va xabarda xaridorga beriladigan summa («Xaridorga berish: N so'm»)",
        "NurCRM «Mening ballarim» ilovasidan mijoz QR kodi: skanerlansa mijoz darhol chekda; yangi mijozni kassa uning raqami bilan qo'shishni taklif qiladi",
        "Sensorli ekran: ikki barmoq ro'yxatni aylantiradi va ikki tugmani birdaniga bosmaydi",
        "Telegram-bot: serverdagi SIni o'z kalitingiz bilan to'g'ridan-to'g'ri dasturdan yoqish mumkin",
        "Z-hisobot: qarzning oldindan to'lovi endi ikki marta hisoblanmaydi",
        "Server bilan aloqa tezroq: javoblar siqilgan holda keladi — internet 8 barobar kam",
    ];

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
