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
        "База знаний на языке программы: все 97 статей, снимки экранов и подписи — на русском, кыргызском, английском, турецком и узбекском",
        "В базе знаний появились обучающие анимации: как открыть программу владельца, как провести продажу и как вернуть чек",
        "Переведены около 900 надписей, которые раньше показывались только по-русски: главный экран кассы, оплата, смена, настройки, окна программы владельца",
        "Исправлены переводы на кыргызский, английский, турецкий и узбекский: грамматика, падежи, опечатки, единые названия (две проверки, около 2200 исправлений)",
        "Кнопки в окнах смены и скидки больше не обрезаются на длинных языках; программа владельца после смены языка открывает разделы заново на новом языке, а её экран входа называется «Вход в программу владельца»",
    ];

    public static readonly string[] LatestKy =
    [
        "Билим базасы программанын тилинде: бардык 97 макала, экран сүрөттөрү жана жазуулар — орус, кыргыз, англис, түрк жана өзбек тилдеринде",
        "Билим базасында окутуучу анимациялар пайда болду: ээсинин программасын кантип ачуу, сатууну кантип жүргүзүү жана чекти кантип кайтаруу",
        "Мурда орусча гана көрүнгөн 900гө жакын жазуу которулду: кассанын башкы экраны, төлөө, смена, жөндөөлөр, ээсинин программасынын терезелери",
        "Кыргыз, англис, түрк жана өзбек тилдериндеги котормолор оңдолду: грамматика, жөндөмөлөр, ката жазуулар, бирдиктүү аттар (эки текшерүү, 2200гө жакын оңдоо)",
        "Смена жана арзандатуу терезелериндеги баскычтар узун тилдерде кесилбей калды; ээсинин программасы тил алмашкандан кийин бөлүмдөрдү жаңы тилде кайра ачат, анын кирүү экраны «Ээсинин программасына кирүү» деп аталат",
    ];

    public static readonly string[] LatestEn =
    [
        "The knowledge base follows the program language: all 97 articles, screenshots and captions in Russian, Kyrgyz, English, Turkish and Uzbek",
        "The knowledge base now has tutorial animations: how to open the owner program, make a sale and return a receipt",
        "About 900 texts that used to appear only in Russian are now translated: the main till screen, payment, shifts, settings and owner program windows",
        "Kyrgyz, English, Turkish and Uzbek translations corrected: grammar, cases, typos, consistent names (two review passes, about 2,200 fixes)",
        "Buttons in the shift and discount windows are no longer cut off in longer languages; the owner program reopens sections in the new language after a language change, and its sign-in screen now says “Sign in to the owner program”",
    ];

    public static readonly string[] LatestTr =
    [
        "Bilgi bankası programın dilinde: 97 makalenin tamamı, ekran görüntüleri ve açıklamalar Rusça, Kırgızca, İngilizce, Türkçe ve Özbekçe",
        "Bilgi bankasına eğitim animasyonları eklendi: sahip programı nasıl açılır, satış nasıl yapılır ve fiş nasıl iade edilir",
        "Daha önce yalnızca Rusça görünen yaklaşık 900 metin çevrildi: kasanın ana ekranı, ödeme, vardiya, ayarlar ve sahip programının pencereleri",
        "Kırgızca, İngilizce, Türkçe ve Özbekçe çeviriler düzeltildi: dil bilgisi, hâl ekleri, yazım hataları, tutarlı adlar (iki kontrol, yaklaşık 2.200 düzeltme)",
        "Vardiya ve indirim pencerelerindeki düğmeler uzun dillerde artık kesilmiyor; sahip programı dil değiştikten sonra bölümleri yeni dilde yeniden açar, giriş ekranı artık «Sahip programına giriş» diyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Bilimlar bazasi dastur tilida: barcha 97 ta maqola, ekran suratlari va izohlar — rus, qirg'iz, ingliz, turk va o'zbek tillarida",
        "Bilimlar bazasida o'quv animatsiyalari paydo bo'ldi: ega dasturini qanday ochish, sotuvni qanday o'tkazish va chekni qanday qaytarish",
        "Ilgari faqat ruscha ko'ringan 900 ga yaqin yozuv tarjima qilindi: kassaning asosiy ekrani, to'lov, smena, sozlamalar va ega dasturi oynalari",
        "Qirg'iz, ingliz, turk va o'zbek tillaridagi tarjimalar tuzatildi: grammatika, kelishiklar, imlo xatolari, yagona nomlar (ikki tekshiruv, 2200 ga yaqin tuzatish)",
        "Smena va chegirma oynalaridagi tugmalar uzun tillarda endi kesilmaydi; ega dasturi til almashtirilgandan keyin bo'limlarni yangi tilda qayta ochadi, kirish oynasi endi «Ega dasturiga kirish» deb nomlanadi",
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
