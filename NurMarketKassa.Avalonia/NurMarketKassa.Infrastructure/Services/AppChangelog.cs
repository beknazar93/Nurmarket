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
        "Голосовое управление: слово «касса» срабатывает только в начале фразы — разговоры у кассы («касается», «на кассе», «касса» посреди фразы) больше не добавляют товары в чек",
        "Настройки на узком экране: строки в несколько колонок встают столбиком — поля и подписи не обрезаются",
    ];

    public static readonly string[] LatestKy =
    [
        "Үн менен башкаруу: «касса» сөзү фразанын башында гана иштейт — кассанын жанындагы сүйлөшүүлөр («касается», «на кассе», фразанын ортосундагы «касса») чекке товар кошпойт",
        "Тар экрандагы жөндөөлөр: бир нече тилкелүү саптар мамыча болуп тизилет — талаалар жана жазуулар кесилбейт",
    ];

    public static readonly string[] LatestEn =
    [
        "Voice control: the word “касса” works only at the start of a phrase — talk near the till (“касается”, “на кассе”, “касса” mid-sentence) no longer adds goods to the receipt",
        "Settings on a narrow screen: multi-column rows stack vertically — fields and labels are not cut off",
    ];

    public static readonly string[] LatestTr =
    [
        "Sesli kontrol: «касса» kelimesi yalnızca cümlenin başında çalışır — kasa yanındaki konuşmalar («касается», «на кассе», cümle ortasında «касса») artık fişe ürün eklemiyor",
        "Dar ekranda ayarlar: çok sütunlu satırlar alt alta dizilir — alanlar ve yazılar kesilmez",
    ];

    public static readonly string[] LatestUz =
    [
        "Ovozli boshqaruv: «касса» so‘zi faqat ibora boshida ishlaydi — kassa yonidagi suhbatlar («касается», «на кассе», ibora o‘rtasidagi «касса») endi chekka tovar qo‘shmaydi",
        "Tor ekrandagi sozlamalar: ko‘p ustunli qatorlar ustma-ust joylashadi — maydonlar va yozuvlar kesilmaydi",
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
