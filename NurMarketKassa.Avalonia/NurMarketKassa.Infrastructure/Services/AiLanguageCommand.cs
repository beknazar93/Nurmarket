using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05, просьба владельца: «при команде „переведи на кыргызский“, „кыргызча жооп бер“, „кыргызча сүйлө“ ИИ переходит на кыргызский и отвечает только на нём».
/// Локальный распознаватель (без модели) команд смены языка по расшифровке речи или тексту чата. Язык ИИ НЕ угадывается по звуку: он меняется только настройкой
/// программы или этой явной командой.
///
/// Правила (мобилка может повторить их один в один):
///  1. Текст приводится к нижнему регистру; ё→е, ө→о, ү→у, ң→н, ӊ→н, і→и; латиница сохраняется (расшифровка бывает латиницей).
///  2. Слова режутся по не-буквам. Слова-«вода» (пожалуйста, мне, теперь, давай-те, язык, тил, эми, азыр, …) выбрасываются.
///  3. Нужны ВМЕСТЕ: слово языка (кырг…/кирг…/kyrg…, рус…/орус…/russ…; англ…/инглис…; турец…/турк…; узбек…) и глагол-просьба
///     (переведи/переключи/переходи/говори/отвечай/разговаривай/скажи/давай; жооп бер…/сүйлө/которул…/айт…).
///  4. Остаток (слова, не вошедшие ни в язык, ни в глагол, ни в предлоги на/по/в/с/к) — не больше 1 слова (для «переведи» — ни одного):
///     «ответь по-русски, сколько продаж сегодня» — это вопрос, а не команда; «кыргызский рынок» (нет глагола) — не команда.
///  5. Два разных языка в одной фразе («переведи с русского на кыргызский») — это перевод текста, не команда.
///  6. Слова «программа / интерфейс / приложение / касса / меню / экран» в фразе — меняется язык ИНТЕРФЕЙСА, иначе — только язык ИИ.
/// </summary>
public static class AiLanguageCommand
{
    /// <summary>Распознанная команда: язык и что менять (интерфейс программы или только язык ИИ).</summary>
    public readonly record struct Result(AppLanguage Language, bool Interface);

    private static readonly string[] Filler =
    {
        "пожалуйста", "пожалуста", "пожалуй", "мне", "нам", "теперь", "дальше", "тоже", "язык", "языке", "языка", "язычок", "тил", "тилде", "тилин", "тилди",
        "эми", "азыр", "сураныч", "ну", "и", "а", "же", "уж", "ка", "ладно", "хорошо", "давайте", "только", "всегда", "потом", "ты", "вы", "сен", "сиз",
        "чи", "ча", "чу", "ответы", "разговор", "чат", "голос", "голосом", "весь", "все", "всё", "да", "нет", "плиз", "please", "now", "ok", "okay",
        "советник", "кеңешчи", "кенешчи", "нур", "ии", "ai", "жи", "бот",
    };

    private static readonly string[] Prepositions = { "на", "по", "в", "к", "в", "bir", "ba", "po", "na" };

    // Слова интерфейса: «переведи программу на кыргызский».
    private static readonly string[] InterfaceStems =
    {
        "программ", "интерфейс", "приложен", "кассу", "касса", "кассе", "меню", "экран", "настройк", "программа", "программан", "интерфейст", "колдонмо", "program", "interface",
    };

    // Глаголы-просьбы. «переведи» — строгий (остаток 0).
    private static readonly string[] TranslateStems = { "перевед", "перевест", "которул", "которуп", "котор", "tercume", "translate", "kotor" };

    private static readonly string[] VerbStems =
    {
        "переключ", "переход", "перейд", "перейт", "говор", "отвеч", "ответь", "разговарив", "общай", "общат", "скаж", "давай", "продолж", "пиши", "пишите",
        "жооп", "суйло", "сойло", "айт", "оту", "joop", "suylo", "suilo", "sujlo", "soylo", "speak", "answer", "reply", "switch", "talk", "govori", "otvechai", "otvechay",
        "pereved", "perekl", "perehod",
    };

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw switch
            {
                'ё' => 'е', 'ө' => 'о', 'ү' => 'у', 'ң' => 'н', 'ӊ' => 'н', 'і' => 'и', 'ö' => 'o', 'ü' => 'u', 'ı' => 'i', 'ş' => 's', 'ğ' => 'g', 'ç' => 'c',
                _ => raw,
            };
            sb.Append(char.IsLetter(c) ? c : ' ');
        }
        return sb.ToString();
    }

    private static AppLanguage? LanguageOf(string token)
    {
        // «русская водка», «кыргызские товары» — прилагательные женского/среднего рода и множественного числа к предмету, а не название языка.
        if (token is "русская" or "русское" or "русские" or "русскую" or "русских" or "кыргызская" or "кыргызское" or "кыргызские" or "кыргызскую" or "кыргызских")
            return null;
        if (token.StartsWith("кырг") || token.StartsWith("кирг") || token.StartsWith("kyrg") || token.StartsWith("kirg") || token.StartsWith("kirgh") || token.StartsWith("qirg"))
            return AppLanguage.Kyrgyz;
        if (token.StartsWith("русск") || token.StartsWith("русч") || token.StartsWith("орус") || token.StartsWith("russ") || token.StartsWith("orus") || token == "рус" || token == "rus"
            || token.StartsWith("ruscha") || token.StartsWith("русс"))
            return AppLanguage.Russian;
        if (token.StartsWith("англ") || token.StartsWith("инглис") || token.StartsWith("english") || token.StartsWith("ingliz"))
            return AppLanguage.English;
        if (token.StartsWith("турецк") || token.StartsWith("турк") || token.StartsWith("turk"))
            return AppLanguage.Turkish;
        if (token.StartsWith("узбек") || token.StartsWith("ozbek") || token.StartsWith("uzbek") || token.StartsWith("o'zbek"))
            return AppLanguage.Uzbek;
        return null;
    }

    private static bool StartsWithAny(string token, string[] stems)
    {
        foreach (var stem in stems)
            if (token.StartsWith(stem, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>null — это не команда смены языка.</summary>
    public static Result? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 160)
            return null;
        var tokens = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 12)
            return null;

        AppLanguage? language = null;
        var otherLanguage = false;
        var hasVerb = false;
        var strict = false;
        var forInterface = false;
        var leftover = 0;
        var kyrgyzCha = false;
        var hasShortOt = false;
        foreach (var token in tokens)
        {
            if (LanguageOf(token) is { } l)
            {
                if (language is not null && language != l)
                    otherLanguage = true;
                language ??= l;
                if (token.EndsWith("чага") || token.EndsWith("chaga"))
                    kyrgyzCha = true;
                continue;
            }
            if (StartsWithAny(token, InterfaceStems))
            {
                forInterface = true;
                continue;
            }
            if (StartsWithAny(token, TranslateStems))
            {
                hasVerb = true;
                strict = true;
                continue;
            }
            if (StartsWithAny(token, VerbStems))
            {
                hasVerb = true;
                continue;
            }
            if (token == "от" || token == "ot")
            {
                hasShortOt = true; // «кыргызчага өт» — только вместе со словом на «-чага»
                continue;
            }
            if (token.StartsWith("бер") || token.StartsWith("ber") || Array.IndexOf(Prepositions, token) >= 0 || Array.IndexOf(Filler, token) >= 0)
                continue;
            leftover++;
        }

        if (hasShortOt && kyrgyzCha)
            hasVerb = true;
        if (language is null || otherLanguage || !hasVerb)
            return null;
        if (leftover > (strict ? 0 : 1))
            return null;
        return new Result(language.Value, forInterface);
    }
}
