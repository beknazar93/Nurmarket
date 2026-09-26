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
        "База знаний: все вопросы и пошаговое обучение — 97 статей с поиском, у 40 из них наглядные скриншоты окон кассы и программы владельца («☰ → Системные → База знаний»)",
        "Несколько весов одновременно: в настройках весов появились «Весы 2» и «Весы 3» — касса сама берёт вес с тех весов, на которых лежит товар",
        "Подсказка у табло покупателя теперь про цифровое табло «0.00» и кнопку «Найти табло»",
    ];

    public static readonly string[] LatestKy =
    [
        "Билим базасы: бардык суроолор жана кадам-кадам окутуу — издөө менен 97 макала, 40ында кассанын жана ээсинин программасынын терезелеринин скриншоттору бар («☰ → Системалык → Билим базасы»)",
        "Бир эле учурда бир нече тараза: тараза жөндөөлөрүндө «Тараза 2» жана «Тараза 3» пайда болду — касса товар турган таразадан салмакты өзү алат",
        "Сатып алуучунун таблосундагы кеңеш эми «0.00» сандык таблосу жана «Таблону табуу» баскычы жөнүндө",
    ];

    public static readonly string[] LatestEn =
    [
        "Knowledge base: all questions and step-by-step training — 97 articles with search, 40 of them with screenshots of the register and owner program windows (☰ → System → Knowledge base)",
        "Several scales at once: scale settings now have “Scale 2” and “Scale 3” — the register takes the weight from the scale the goods are on",
        "The customer display hint now explains the numeric “0.00” display and the “Find display” button",
    ];

    public static readonly string[] LatestTr =
    [
        "Bilgi bankası: tüm sorular ve adım adım eğitim — aramalı 97 makale, 40'ında kasa ve sahip programı pencerelerinin ekran görüntüleri var (☰ → Sistem → Bilgi bankası)",
        "Aynı anda birkaç terazi: terazi ayarlarında “Terazi 2” ve “Terazi 3” var — kasa, ürünün bulunduğu teraziden ağırlığı kendisi alır",
        "Müşteri ekranı ipucu artık “0.00” sayısal ekranı ve “Ekranı bul” düğmesini anlatıyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Bilimlar bazasi: barcha savollar va bosqichma-bosqich o'qitish — qidiruvli 97 ta maqola, 40 tasida kassa va ega dasturi oynalarining skrinshotlari bor («☰ → Tizim → Bilimlar bazasi»)",
        "Bir vaqtda bir nechta tarozi: tarozi sozlamalarida «Tarozi 2» va «Tarozi 3» paydo bo'ldi — kassa og'irlikni mahsulot turgan tarozidan o'zi oladi",
        "Xaridor tablosidagi maslahat endi «0.00» raqamli tablo va «Tabloni topish» tugmasi haqida",
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
