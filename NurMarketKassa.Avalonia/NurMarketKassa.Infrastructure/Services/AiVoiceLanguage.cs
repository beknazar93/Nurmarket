using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05, жалоба владельца: «при звонке ИИ сам переходит на узбекский, турецкий». Gemini Live угадывает язык по звуку и
/// принимает кыргызскую/русскую речь за узбекскую/турецкую. Язык звонка теперь задан программой (язык интерфейса), а не фразой владельца:
/// жёсткое правило в начале инструкции + однократная поправка в сеанс, если в ответе ИИ замечена чужая речь.
/// Код языка в speechConfig не передаётся: native-audio модели Live сами выбирают язык, а неверное поле рвёт соединение.</summary>
public static class AiVoiceLanguage
{
    /// <summary>Язык ИИ (звонок, голосовой вопрос, чат Нур Советника): язык, заданный командой пользователя («кыргызча жооп бер»), иначе язык интерфейса.
    /// Платные en/tr/uz — только при открытом языковом пакете, иначе русский. Язык НЕ угадывается по звуку и сам не меняется.</summary>
    public static AppLanguage Current()
    {
        var prefs = UserPreferences.Instance;
        var language = prefs.AiLanguageOverride ?? prefs.Language;
        return LanguagePackGate.IsAvailable(language) ? language : AppLanguage.Russian;
    }

    /// <summary>Название языка для подписи в окне звонка.</summary>
    public static string Label(AppLanguage language) => language switch
    {
        AppLanguage.Kyrgyz => "Кыргызча",
        AppLanguage.English => "English",
        AppLanguage.Turkish => "Türkçe",
        AppLanguage.Uzbek => "O'zbekcha",
        _ => "Русский",
    };

    /// <summary>Запомнить язык ИИ по команде. Совпал с языком интерфейса — настройка снимается (ИИ следует интерфейсу).</summary>
    public static void SetAiLanguage(AppLanguage language)
    {
        var prefs = UserPreferences.Instance;
        prefs.AiLanguageOverride = language == prefs.Language ? null : language;
        prefs.SaveToDisk();
    }

    /// <summary>Подтверждение смены языка — на НОВОМ языке (одна короткая фраза).</summary>
    public static string ConfirmText(AppLanguage language) => language switch
    {
        AppLanguage.Kyrgyz => "Жарайт, эми кыргызча жооп берем.",
        AppLanguage.English => "Okay, I will answer in English from now on.",
        AppLanguage.Turkish => "Tamam, bundan sonra Türkçe yanıtlayacağım.",
        AppLanguage.Uzbek => "Xo'p, endi o'zbekcha javob beraman.",
        _ => "Хорошо, теперь отвечаю по-русски.",
    };

    /// <summary>Отказ: языка нет без платного пакета. Пишется на текущем языке ИИ.</summary>
    public static string PackMissingText(AppLanguage current) => current switch
    {
        AppLanguage.Kyrgyz => "Тил пакети туташтырылган эмес. Азырынча орусча же кыргызча гана.",
        AppLanguage.English => "The language pack is not connected. Only Russian and Kyrgyz are available.",
        AppLanguage.Turkish => "Dil paketi bağlı değil. Yalnızca Rusça ve Kırgızca kullanılabilir.",
        AppLanguage.Uzbek => "Til paketi ulanmagan. Faqat rus va qirg'iz tillari mavjud.",
        _ => "Языковой пакет не подключён. Доступны только русский и кыргызский.",
    };

    /// <summary>Служебная реплика в идущий звонок: язык меняется без обрыва (setup в сеансе менять нельзя). Модель коротко подтверждает голосом на новом языке.</summary>
    public static string SwitchText(AppLanguage language)
    {
        var name = Name(language);
        return $"[Программа] Пользователь попросил сменить язык. С этой минуты говори и отвечай ТОЛЬКО на языке: {name}. "
               + $"Никогда не переходи на другие языки, даже если речь пользователя неразборчива или на другом языке; язык меняется только по явной просьбе пользователя. "
               + $"Сейчас коротко подтверди голосом на этом языке: «{ConfirmText(language)}». From now on speak and answer ONLY in {name}.";
    }

    private static string Name(AppLanguage language) => language switch
    {
        AppLanguage.Kyrgyz => "Kyrgyz",
        AppLanguage.English => "English",
        AppLanguage.Turkish => "Turkish",
        AppLanguage.Uzbek => "Uzbek",
        _ => "Russian",
    };

    /// <summary>Жёсткое правило языка — первой строкой инструкции звонка (на языке интерфейса + короткий английский дубль для модели).</summary>
    public static string Rule()
    {
        var language = Current();
        var english = $"LANGUAGE RULE: speak and answer ONLY in {Name(language)}. Never switch to Uzbek, Turkish, Kazakh, English or any other language "
                      + $"(unless it is {Name(language)}), even if the speech is unclear, accented or sounds like one of them; if you did not understand, ask again in {Name(language)}. "
                      + "Only the program setting or an explicit request to change language (announced to you by the program as a [Программа] message) changes the language, never accent or unclear speech.";
        var native = language switch
        {
            AppLanguage.Kyrgyz => "ТИЛ ЭРЕЖЕСИ (катуу): сүйлө жана жооп бер ЖАЛГЫЗ кыргызча. Эч качан өзбекче, түркчө, казакча, англисче жана башка тилге өтпө — сүйлөө түшүнүксүз, акцент менен же ошого окшош угулса да; түшүнбөсөң, кыргызча кайра сура. "
                                  + "Өзбекчеге же түркчөгө окшош угулган сөздү кыргызча деп эсептей бер. Тилди программанын жөндөөсү же колдонуучунун ачык өтүнүчү гана өзгөртөт (программа өзү жаңы эрежени «[Программа]» билдирүүсү менен билдирет).",
            AppLanguage.English => "LANGUAGE (strict): speak and answer ONLY in English. Never switch to another language, even if the speech is unclear or accented; ask again in English.",
            AppLanguage.Turkish => "DİL KURALI (kesin): yalnızca Türkçe konuş ve yanıtla. Konuşma belirsiz, aksanlı veya başka dile benzese bile başka dile geçme; anlamazsan Türkçe tekrar sor.",
            AppLanguage.Uzbek => "TIL QOIDASI (qat'iy): faqat o'zbekcha gapiring va javob bering. Nutq noaniq yoki aksentli bo'lsa ham boshqa tilga o'tmang; tushunmasangiz o'zbekcha qayta so'rang.",
            _ => "ЖЁСТКОЕ ПРАВИЛО ЯЗЫКА: говори и отвечай ТОЛЬКО по-русски. Никогда не переходи на узбекский, турецкий, казахский, английский и другие языки, "
                 + "даже если речь неразборчива, с акцентом или похожа на них; не поняв — переспроси по-русски. Речь, похожую на узбекскую или турецкую, считай русской или кыргызской. "
                 + "Язык меняет только настройка программы или явная просьба владельца сменить язык — программа сама сообщит тебе новое правило сообщением «[Программа]».",
        };
        return native + "\n" + english;
    }

    /// <summary>Короткая поправка для сеанса, если ИИ уже ушёл на чужой язык (шлётся один раз, без ответа вслух и без записи в чат).</summary>
    public static string CorrectionText()
    {
        var language = Current();
        return $"[Программа] Отвечай только на языке интерфейса: {Name(language)}. Не переходи на другие языки. Reply only in {Name(language)}.";
    }

    // Буквы, которых нет ни в русском, ни в кыргызском: узбекские/казахские кириллические и турецко-узбекская латиница.
    private static readonly Regex ForeignLetters = new("[ўғқҳşğıİŞĞçÇöÖüÜ]", RegexOptions.Compiled);

    // Частые слова узбекского и турецкого (латиница) — ответ на русском/кыргызском латиницей не пишется.
    private static readonly Regex ForeignWords = new(
        @"\b(va|bilan|uchun|yaxshi|rahmat|salom|ham|lekin|savdo|bugun|kecha|mijoz|mahsulot|ve|için|evet|hayır|tamam|merhaba|teşekkür|değil|bugün|satış|müşteri|ürün|nasıl|çok)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>true — в расшифровке ответа ИИ явные признаки узбекского/турецкого, а язык звонка — русский или кыргызский.
    /// Для en/tr/uz интерфейса не проверяем (слова этих языков там законны).</summary>
    public static bool LooksForeign(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var language = Current();
        if (language is not (AppLanguage.Russian or AppLanguage.Kyrgyz))
            return false;
        return ForeignLetters.IsMatch(text) || ForeignWords.IsMatch(text);
    }
}
