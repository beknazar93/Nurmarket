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
        "Обновления больше не меняют настройки клиента: вид магазина, выбранный в кассе, не заменяется видом с сервера при каждом запуске; сохранённые кассиры для входа без интернета не стираются при обновлении (начиная со следующего обновления)",
        "Голосовое управление: касса больше не закрывается с ошибкой «0xc0000005» при записи своей озвучки и при регистрации голоса",
        "Кыргызский счёт голосом: «уч», «торт», «он эки» (12), «жыйырма беш» (25), «эки жүз элүү» (250), «бир жарым кило» (1,5); по-русски — «двадцать пять», «сто», «кило»",
        "Выученные слова с числом («эки» → 2) теперь задают количество; «кассага …» тоже понимается",
        "Поиск товара голосом точнее: «касса спрайт» и «касса сумка» больше не переспрашивают из-за слова «с» в чужих названиях, точное название товара выбирается сразу",
        "Запись своей озвучки: мигающий индикатор, время «0:03 из 0:15» и уровень громкости — видно, слышит ли микрофон голос",
        "Голосовой замок: модель и голос кассира больше не стираются при каждом обновлении кассы (начиная со следующего обновления; после этого один раз может понадобиться скачать модель и записать голос заново)",
        "Программа владельца: внизу меню — «Выйти на рабочий стол», выход из учётной записи — в Настройки → Аккаунт (там он теперь работает)",
    ];

    public static readonly string[] LatestKy =
    [
        "Жаңыртуулар кардардын жөндөөлөрүн өзгөртпөйт: кассада тандалган дүкөндүн түрү ар бир ачылганда серверден алынган түргө алмашпайт; интернетсиз кирүү үчүн сакталган кассирлер жаңыртууда өчпөйт (кийинки жаңыртуудан баштап)",
        "Үн менен башкаруу: өз үнүңүз менен жазганда жана үндү каттаганда касса «0xc0000005» катасы менен жабылбайт",
        "Кыргызча эсеп үн менен: «уч», «торт», «он эки» (12), «жыйырма беш» (25), «эки жүз элүү» (250), «бир жарым кило» (1,5); орусча — «двадцать пять», «сто», «кило»",
        "Сан менен үйрөтүлгөн сөздөр («эки» → 2) эми санды коёт; «кассага …» да түшүнүлөт",
        "Товарды үн менен издөө тагыраак: «касса спрайт» жана «касса сумка» башка аталыштардагы «с» сөзүнөн улам кайра сурабайт, товардын так аталышы дароо тандалат",
        "Өз үнүңүз менен жазуу: жымыңдаган белги, «0:03 / 0:15» убакыт жана үндүн деңгээли — микрофон үндү угуп жатканы көрүнөт",
        "Үн кулпусу: модель жана кассирдин үнү кассаны жаңырткан сайын өчүп калбайт (кийинки жаңыртуудан баштап)",
        "Ээсинин программасы: менюнун ылдыйында — «Иш столуна чыгуу», эсептик жазуудан чыгуу — Жөндөөлөр → Аккаунт (ал жерде эми иштейт)",
    ];

    public static readonly string[] LatestEn =
    [
        "Updates no longer change the client’s settings: the store type chosen in the till is not replaced by the server one at every start; cashiers saved for offline sign-in are not erased by an update (from the next update on)",
        "Voice control: the till no longer closes with error “0xc0000005” while recording your own prompts or enrolling a voice",
        "Kyrgyz counting by voice: “уч”, “торт”, “он эки” (12), “жыйырма беш” (25), “эки жүз элүү” (250), “бир жарым кило” (1.5); in Russian — “двадцать пять”, “сто”, “кило”",
        "Taught words with a number (“эки” → 2) now set the quantity; “кассага …” is understood too",
        "Voice product search is more precise: “касса спрайт” and “касса сумка” no longer ask again because of the word “с” in other names; an exact product name is chosen at once",
        "Recording your own prompts: a blinking indicator, time “0:03 of 0:15” and the volume level — you can see whether the microphone hears the voice",
        "Voice lock: the model and the cashier’s voice are no longer erased by every till update (from the next update on)",
        "Owner app: “Exit to desktop” at the bottom of the menu; sign-out is in Settings → Account (it works there now)",
    ];

    public static readonly string[] LatestTr =
    [
        "Güncellemeler artık müşterinin ayarlarını değiştirmiyor: kasada seçilen mağaza türü her açılışta sunucudakiyle değiştirilmiyor; internetsiz giriş için kaydedilen kasiyerler güncellemede silinmiyor (bir sonraki güncellemeden itibaren)",
        "Sesli kontrol: kendi seslendirmenizi kaydederken ve ses kaydı yaparken kasa artık «0xc0000005» hatasıyla kapanmıyor",
        "Sesle Kırgızca sayma: «уч», «торт», «он эки» (12), «жыйырма беш» (25), «эки жүз элүү» (250), «бир жарым кило» (1,5); Rusça — «двадцать пять», «сто», «кило»",
        "Sayıyla öğretilen kelimeler («эки» → 2) artık miktarı belirliyor; «кассага …» da anlaşılıyor",
        "Sesle ürün arama daha isabetli: «касса спрайт» ve «касса сумка» başka adlardaki «с» kelimesi yüzünden artık tekrar sormuyor, tam ürün adı hemen seçiliyor",
        "Kendi seslendirmenizi kaydetme: yanıp sönen gösterge, «0:03 / 0:15» süre ve ses seviyesi — mikrofonun sesi duyup duymadığı görülüyor",
        "Ses kilidi: model ve kasiyerin sesi artık her kasa güncellemesinde silinmiyor (bir sonraki güncellemeden itibaren)",
        "Sahip programı: menünün altında «Masaüstüne çık»; oturumu kapatma Ayarlar → Hesap içinde (artık orada çalışıyor)",
    ];

    public static readonly string[] LatestUz =
    [
        "Yangilanishlar endi mijoz sozlamalarini o'zgartirmaydi: kassada tanlangan do'kon turi har ishga tushganda serverdagi bilan almashtirilmaydi; internetsiz kirish uchun saqlangan kassirlar yangilanishda o'chmaydi (keyingi yangilanishdan boshlab)",
        "Ovozli boshqaruv: o'z ovozingiz bilan yozishda va ovozni ro'yxatdan o'tkazishda kassa endi «0xc0000005» xatosi bilan yopilmaydi",
        "Ovoz bilan qirg'izcha sanash: «уч», «торт», «он эки» (12), «жыйырма беш» (25), «эки жүз элүү» (250), «бир жарым кило» (1,5); ruscha — «двадцать пять», «сто», «кило»",
        "Son bilan o'rgatilgan so'zlar («эки» → 2) endi miqdorni belgilaydi; «кассага …» ham tushuniladi",
        "Ovoz bilan mahsulot qidirish aniqroq: «касса спрайт» va «касса сумка» boshqa nomlardagi «с» so'zi tufayli endi qayta so'ramaydi, aniq mahsulot nomi darhol tanlanadi",
        "O'z ovozingiz bilan yozish: miltillovchi belgi, «0:03 / 0:15» vaqt va ovoz darajasi — mikrofon ovozni eshityaptimi, ko'rinadi",
        "Ovoz qulfi: model va kassir ovozi endi har bir kassa yangilanishida o'chib ketmaydi (keyingi yangilanishdan boshlab)",
        "Egasi dasturi: menyu pastida — «Ish stoliga chiqish», hisobdan chiqish — Sozlamalar → Hisob (endi u yerda ishlaydi)",
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
