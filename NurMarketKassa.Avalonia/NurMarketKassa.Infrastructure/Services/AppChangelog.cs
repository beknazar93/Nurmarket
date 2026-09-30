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
        "Установка: при первом запуске касса спрашивает, что поставить на этот компьютер — кассу и программу владельца «NurMarket Владелец» или только кассу",
        "Настройки → Обновления → «Программа владельца»: поставить её на этот компьютер или убрать — в любой момент",
        "Обновления у работающих касс ничего не меняют: ярлык программы владельца остаётся как был",
    ];

    public static readonly string[] LatestKy =
    [
        "Орнотуу: биринчи ишке кирерде касса бул компьютерге эмнени коюуну сурайт — кассаны жана «NurMarket Владелец» ээсинин программасын же кассаны гана",
        "Жөндөөлөр → Жаңыртуулар → «Ээсинин программасы»: аны бул компьютерге коюу же алып салуу — каалаган убакта",
        "Иштеп жаткан кассаларда жаңыртуу эч нерсени өзгөртпөйт: ээсинин программасынын энбелгиси мурункудай калат",
    ];

    public static readonly string[] LatestEn =
    [
        "Installation: on first launch the till asks what to install on this computer — the till and the “NurMarket Владелец” owner app, or the till only",
        "Settings → Updates → “Owner app”: add it to this computer or remove it at any time",
        "Updates on working tills change nothing: the owner app shortcut stays as it was",
    ];

    public static readonly string[] LatestTr =
    [
        "Kurulum: ilk açılışta kasa bu bilgisayara neyin kurulacağını sorar — kasa ve «NurMarket Владелец» sahip programı ya da yalnızca kasa",
        "Ayarlar → Güncellemeler → «Sahip programı»: istediğiniz zaman bu bilgisayara ekleyin veya kaldırın",
        "Çalışan kasalarda güncelleme hiçbir şeyi değiştirmez: sahip programı kısayolu olduğu gibi kalır",
    ];

    public static readonly string[] LatestUz =
    [
        "O‘rnatish: birinchi ishga tushirishda kassa bu kompyuterga nima o‘rnatishni so‘raydi — kassa va «NurMarket Владелец» egasi dasturi yoki faqat kassa",
        "Sozlamalar → Yangilanishlar → «Egasining dasturi»: uni istalgan vaqtda bu kompyuterga qo‘ying yoki olib tashlang",
        "Ishlayotgan kassalarda yangilanish hech narsani o‘zgartirmaydi: egasi dasturining yorlig‘i avvalgidek qoladi",
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
