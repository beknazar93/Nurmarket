using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Разбор голосовой команды кассира (VoiceControlService, 2026-09-04): вычленяет
/// количество/единицу измерения из распознанного текста и ищет товар по оставшимся словам.
/// Отдельный статический класс — вся логика чисто текстовая, без завязки на аудио/Vosk.</summary>
public static class VoiceCommandParser
{
    public const string WakeWord = "касса";

    // Vosk на маленькой модели нередко "не дослышивает" удвоенную "сс" и распознаёт "касса" как
    // "каса" (реальное слово, но здесь — просто неточная транскрипция) — подтверждено логами
    // реального использования 2026-09-04. Проверяем оба варианта, самый длинный/ранний совпавший
    // определяет, где кончается ключевое слово и начинается сама команда.
    private static readonly string[] WakeWordVariants = ["касса", "каса"];

    // Основные падежные формы (именительный/винительный/родительный/творительный) — русская речь
    // склоняется ("две пачки", "двух пачек"), точный словарь надёжнее подбора по одному корню,
    // т.к. числительных немного и их формы не всегда совпадают по началу слова (два/двух).
    // Кыргызские числительные 1-10 не склоняются в устном счёте так же сильно, поэтому даны
    // базовыми формами (бир/эки/үч...) — этого достаточно для "касса эки нан" ("касса, два хлеба").
    private static readonly Dictionary<string, double> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["один"] = 1, ["одна"] = 1, ["одно"] = 1, ["одну"] = 1, ["одной"] = 1,
        ["два"] = 2, ["две"] = 2, ["двух"] = 2, ["двумя"] = 2, ["двум"] = 2,
        ["три"] = 3, ["трёх"] = 3, ["трех"] = 3, ["тремя"] = 3, ["трём"] = 3, ["трем"] = 3,
        ["четыре"] = 4, ["четырёх"] = 4, ["четырех"] = 4, ["четырьмя"] = 4, ["четырём"] = 4, ["четырем"] = 4,
        ["пять"] = 5, ["пяти"] = 5, ["пятью"] = 5,
        ["шесть"] = 6, ["шести"] = 6, ["шестью"] = 6,
        ["семь"] = 7, ["семи"] = 7, ["семью"] = 7,
        ["восемь"] = 8, ["восьми"] = 8, ["восемью"] = 8,
        ["девять"] = 9, ["девяти"] = 9, ["девятью"] = 9,
        ["десять"] = 10, ["десяти"] = 10, ["десятью"] = 10,
        ["полтора"] = 1.5, ["полторы"] = 1.5,
        // 2026-09-29: 11–19, десятки и сотни — раньше «двадцать пять» давало 5, а «сто» не
        // понималось вовсе. Составные числа складываются в ExtractQuantity (см. CombineNumberRun).
        ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14,
        ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18,
        ["девятнадцать"] = 19,
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["сто"] = 100, ["двести"] = 200, ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500,
        // Кыргызча (кыргыз тилинде). "он" (10) — в AmbiguousNumberWords ниже: это же русское
        // местоимение ("он купил...").
        ["бир"] = 1, ["эки"] = 2, ["үч"] = 3, ["төрт"] = 4, ["беш"] = 5,
        ["алты"] = 6, ["жети"] = 7, ["сегиз"] = 8, ["тогуз"] = 9,
        // 2026-09-29, владелец: «с кыргызским проблемы — бир это 1, эки это 2». Кассир (и
        // распознавание) часто обходится без «ү/ө»: «уч», «жуз», «элуу» — владелец даже учил
        // «уч» как единицу измерения, но количество от этого не менялось.
        ["уч"] = 3, ["бээш"] = 5, ["тогус"] = 9,
        ["жыйырма"] = 20, ["жийирма"] = 20, ["жыйрма"] = 20, ["отуз"] = 30, ["кырк"] = 40, ["кырык"] = 40,
        ["элүү"] = 50, ["элуу"] = 50, ["элү"] = 50, ["алтымыш"] = 60, ["жетимиш"] = 70, ["сексен"] = 80,
        ["токсон"] = 90,
        // Русская акустическая модель Vosk (активна, пока язык кассы — русский, см.
        // VoiceControlService.ResolveVoskModelPath) плохо распознаёт кыргызские звуки, которых
        // нет в русском ("ү" и т.п.) — "эки" нередко слышится как "ики" (подтверждено в логах
        // 2026-09-05: "касса алма ики дана" не сработало именно по этой причине). Это костыль
        // под конкретную наблюдённую ошибку распознавания, а не полноценное решение — по-настоящему
        // кыргызские команды надёжно работают только при кыргызском языке кассы (кыргызская
        // модель Vosk обучена на этих звуках).
        ["ики"] = 2,
    };

    /// <summary>«жүз» (сто) по-кыргызски — множитель: «эки жүз» = 200, «жүз элүү» = 150.</summary>
    private static readonly HashSet<string> HundredMultiplierWords = new(StringComparer.OrdinalIgnoreCase) { "жүз", "жуз" };

    /// <summary>«жарым» — половина: «бир жарым» = 1,5, «жарым» = 0,5 (2026-09-29).</summary>
    private static readonly HashSet<string> HalfWords = new(StringComparer.OrdinalIgnoreCase) { "жарым", "половина", "половину" };

    /// <summary>Слова, которые бывают и числом, и обычным русским словом: «он» (10 / местоимение),
    /// «торт» (4 без «ө» / торт). Считаются числом, только если стоят рядом с другим числом или
    /// единицей («он эки», «торт даана») или последними после названия («нан торт», 2026-09-29).
    /// «касса торт наполеон» — это торт, а не 4.</summary>
    private static readonly Dictionary<string, double> AmbiguousNumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["он"] = 10, ["торт"] = 4,
        // Так кыргызская модель Vosk слышит «алты» (6) и «жети» (7) — проверено синтезированной
        // речью 2026-09-29: «касса кымыз алты» → «касса кымыз алды». Сами по себе это обычные слова
        // («алды» — взял, «жетти» — дошёл), поэтому тоже только рядом с числом или после названия.
        ["алды"] = 6, ["жетти"] = 7,
    };

    // Единицы веса/объёма — не влияют на выбор "поштучно/пачка" (весовой товар вообще не
    // проходит через PackageChoiceDialog, см. ProductUnitNormalizer.RequiresWeighing), но всё
    // равно должны вычленяться из фразы, чтобы не попасть в поисковый запрос по названию.
    private static readonly HashSet<string> WeightVolumeUnitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "кг", "килограмм", "килограмма", "килограмму", "килограммов", "килограммами",
        "кило", "килограм", "килограммдан", "килодон",
        "г", "грамм", "грамма", "грамму", "граммов", "граммами",
        "л", "литр", "литра", "литру", "литров", "литрами",
    };

    // "N шт"/"N штук" и т.п. — явный сигнал ПОШТУЧНОЙ продажи (см. VoiceUnitKind, 2026-09-04):
    // если у товара есть отдельная цена за штуку (HasPieceOption), такую команду можно сразу
    // добавить в корзину поштучно, не открывая PackageChoiceDialog для уточнения.
    private static readonly HashSet<string> PieceUnitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "шт", "штука", "штуку", "штуки", "штук", "штуками",
        "бутылка", "бутылку", "бутылки", "бутылок", "бутылками",
        // "поштучно" — кассир нередко буквально повторяет надпись на кнопке диалога
        // (PackageChoiceDialog: "Поштучно"/"Целая пачка"), а не грамматическую единицу измерения
        // (2026-09-04, подтверждено пользователем: "поштучно 3" не срабатывало, т.к. "поштучно"
        // не было ни в одном из этих множеств — попадало в поисковый запрос и ломало совпадение
        // товара). Слово-наречие, не склоняется — одной формы достаточно.
        "поштучно",
        // Кыргызча — "дана" добавлено рядом с "даана" по той же причине, что и "ики" в
        // NumberWords выше: русская модель Vosk нередко не дослышивает удвоенную "аа".
        "даана", "дана", "даанадан", "бөтөлкө", "бөтөлкөдөн",
    };

    // "N пачек"/"пачка" и т.п. — явный сигнал продажи ЦЕЛОЙ ПАЧКОЙ (симметрично PieceUnitWords).
    private static readonly HashSet<string> PackUnitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "пачка", "пачку", "пачки", "пачек", "пачками",
        "упаковка", "упаковку", "упаковки", "упаковок", "упаковками",
    };

    /// <summary>Вычленяет число+единицу из хвоста команды (текст после ключевого слова "касса").
    /// Оставшиеся слова — поисковый запрос по названию товара. UnitKind сигнализирует, была ли
    /// произнесённая единица явно "поштучной" (шт/штука/даана/…) или "пачечной" (пачка/упаковка/…) —
    /// когда товар действительно продаётся и так, и так (HasPieceOption), это позволяет добавить
    /// его в корзину сразу нужным способом, минуя PackageChoiceDialog (кассир уже сказал, что
    /// имел в виду). None — единица не названа вовсе, или названа единица веса/объёма, или
    /// кастомная (VoiceLexiconStore) единица без явной "поштучно/пачка" семантики.</summary>
    public static (string ProductQuery, double Quantity, VoiceUnitKind UnitKind) ExtractQuantity(string command)
    {
        // "целая пачка"/"целую пачку" — та же ситуация, что с "поштучно": кассир мог повторить
        // ровно то, что написано на кнопке диалога ("Целая пачка"). "целая"/"целую" САМИ ПО СЕБЕ
        // не трогаем нигде больше — это обычные слова, которые вполне могут быть частью названия
        // товара ("целая курица" и т.п.), поэтому вырезаем только когда они идут ПРЯМО перед
        // "пачка"/"пачку", а не как общее unit-слово.
        command = command
            .Replace("целая пачка", "пачка", StringComparison.OrdinalIgnoreCase)
            .Replace("целую пачку", "пачку", StringComparison.OrdinalIgnoreCase);

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        var customUnits = LoadCustomUnitWords();

        // 2026-09-29: сначала каждое слово — число, единица или часть названия; потом подряд
        // идущие числа складываются в одно («он эки» = 12, «жыйырма беш» = 25, «эки жүз элүү» =
        // 250, «бир жарым» = 1,5). Раньше каждое число просто заменяло предыдущее — «он эки» было 2.
        var kinds = new TokenKind[tokens.Count];
        var values = new double[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (NumberWords.TryGetValue(token, out var num)) { kinds[i] = TokenKind.Number; values[i] = num; }
            else if (HundredMultiplierWords.Contains(token)) kinds[i] = TokenKind.Hundred;
            else if (HalfWords.Contains(token)) kinds[i] = TokenKind.Half;
            else if (double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var digitNum) && digitNum > 0) { kinds[i] = TokenKind.Digits; values[i] = digitNum; }
            else if (AmbiguousNumberWords.TryGetValue(token, out var ambiguous)) { kinds[i] = TokenKind.Ambiguous; values[i] = ambiguous; }
            else if (PieceUnitWords.Contains(token)) kinds[i] = TokenKind.PieceUnit;
            else if (PackUnitWords.Contains(token)) kinds[i] = TokenKind.PackUnit;
            else if (WeightVolumeUnitWords.Contains(token)) kinds[i] = TokenKind.OtherUnit;
            else if (customUnits.TryGetValue(token, out var customNumber))
            {
                // Своё слово из «Проверить голос». Если в сокращении число («эки» → «2»), это
                // число: так владелец учил кассу кыргызскому счёту, но количество не менялось.
                if (customNumber is { } n) { kinds[i] = TokenKind.Number; values[i] = n; }
                else kinds[i] = TokenKind.OtherUnit;
            }
            else kinds[i] = TokenKind.Word;
        }

        // «он»/«торт» — число, только рядом с числом/единицей или последним словом после названия.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (kinds[i] != TokenKind.Ambiguous)
                continue;
            static bool NumberOrUnit(TokenKind k) => k is not (TokenKind.Word or TokenKind.Ambiguous);
            var nearNumberOrUnit = (i > 0 && NumberOrUnit(kinds[i - 1])) || (i + 1 < tokens.Count && NumberOrUnit(kinds[i + 1]));
            var lastAfterName = i == tokens.Count - 1 && kinds.Take(i).Any(k => k == TokenKind.Word);
            kinds[i] = nearNumberOrUnit || lastAfterName ? TokenKind.Number : TokenKind.Word;
        }

        double quantity = 1;
        var unitKind = VoiceUnitKind.None;
        var remaining = new List<string>();
        var run = new List<(TokenKind Kind, double Value)>();

        void FlushRun()
        {
            if (run.Count == 0)
                return;
            var value = CombineNumberRun(run);
            if (value > 0)
                quantity = value;
            run.Clear();
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            switch (kinds[i])
            {
                case TokenKind.Number:
                case TokenKind.Hundred:
                case TokenKind.Half:
                    run.Add((kinds[i], values[i]));
                    continue;
                case TokenKind.Digits:
                    FlushRun();
                    quantity = values[i];
                    continue;
            }

            FlushRun();
            switch (kinds[i])
            {
                case TokenKind.PieceUnit:
                    unitKind = VoiceUnitKind.Piece;
                    break;
                case TokenKind.PackUnit:
                    unitKind = VoiceUnitKind.Pack;
                    break;
                case TokenKind.OtherUnit:
                    break;
                default:
                    remaining.Add(tokens[i]);
                    break;
            }
        }
        FlushRun();

        return (string.Join(' ', remaining).Trim(), quantity, unitKind);
    }

    private enum TokenKind { Word, Number, Hundred, Half, Digits, Ambiguous, PieceUnit, PackUnit, OtherUnit }

    /// <summary>Подряд идущие числительные — одно число. Десятки прибавляются к сотням, единицы —
    /// к десяткам («жыйырма беш» = 25, «сто двадцать» = 120), «жүз» умножает то, что перед ним
    /// («эки жүз» = 200), «жарым» добавляет половину. Бессмысленная пара («эки эки», «беш он»)
    /// не складывается — как и раньше, берётся последнее число.</summary>
    private static double CombineNumberRun(List<(TokenKind Kind, double Value)> run)
    {
        double total = 0, current = 0;
        foreach (var (kind, value) in run)
        {
            if (kind == TokenKind.Hundred)
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
                continue;
            }
            if (kind == TokenKind.Half)
            {
                if (current % 1 != 0) { total = 0; current = 0; }
                current += 0.5;
                continue;
            }

            var fits = value switch
            {
                >= 100 => current == 0 && total == 0,
                >= 10 => current == 0 || current % 100 == 0,
                _ => current % 10 == 0 && current % 1 == 0,
            };
            if (!fits)
            {
                total = 0;
                current = 0;
            }
            current += value;
        }

        return total + current;
    }

    /// <summary>Слова-единицы, добавленные кассиром через "Проверить голос" в Настройках
    /// (VoiceLexiconStore), поверх встроенного русского/кыргызского списка выше: слово → число из
    /// сокращения, если оно число (иначе null). Читается из локальной базы на каждый вызов — список
    /// крошечный, а прочитать всегда актуальный список важнее сложности инвалидации кеша.</summary>
    private static Dictionary<string, double?> LoadCustomUnitWords()
    {
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (_, word, abbreviation) in VoiceLexiconStore.LoadUnitWords())
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;
                double? number = double.TryParse((abbreviation ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n
                    : null;
                result[word.Trim()] = number;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось прочитать словарь единиц: {ex.Message}", "VOICE");
        }

        return result;
    }

    /// <summary>Нечёткий поиск товара по общему началу слова (примитивный, но достаточный разбор
    /// склонений без полноценного морфологического анализатора) — сказанное "картошку" должно
    /// находить каталожную "Картошка", хотя дословно они не совпадают. Все слова запроса должны
    /// найти пару в названии. Возвращает ВСЕ подходящие товары, а не один — при 2+ совпадениях
    /// вызывающая сторона должна уточнить у кассира, какой именно товар имелся в виду, вместо
    /// того чтобы молча угадывать (Product Resolver из ТЗ: confidence ниже порога → уточнение).</summary>
    public static List<CatalogProductTileVm> FindProducts(string query, IReadOnlyList<CatalogProductTileVm> products)
    {
        // 2026-09-08: "обучение" голосового помощника (Настройки → Регистрация голоса → Обучение
        // товарам) — точная фраза, заранее привязанная владельцем к конкретному товару, проверяется
        // ПЕРВОЙ и в обход обычного пословного поиска. Нужно, когда офлайн-распознавание речи
        // стабильно неверно слышит название ("кола" вместо длинного "Кока-кола 0.5л ПЭТ") или
        // кассиры называют товар разговорным словом, которого нет в самом названии.
        var trimmedQuery = (query ?? "").Trim();
        if (trimmedQuery.Length > 0)
        {
            var alias = TryFindAliasByExactPhrase(trimmedQuery);
            if (alias != null)
            {
                var aliased = products.FirstOrDefault(p => p.Id == alias.Value.ProductId);
                if (aliased != null)
                    return [aliased];
            }
        }

        var matches = new List<CatalogProductTileVm>();

        var queryStems = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Select(w => Stem(w.ToLowerInvariant()))
            .ToList();
        if (queryStems.Count == 0)
            return matches;

        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Title))
                continue;

            var titleWords = product.Title.ToLowerInvariant().Split(
                [' ', ',', '.', '-', '/', '(', ')', '"'],
                StringSplitOptions.RemoveEmptyEntries);

            var allMatch = queryStems.All(qs => titleWords.Any(tw => SharesStem(qs, tw)));
            if (allMatch)
                matches.Add(product);
        }

        // 2026-09-29: сказано ровно название одного товара («касса хлеб» при «хлеб», «черный хлеб»
        // и «Хлеб от армянина») — берём его, а не переспрашиваем каждый раз.
        if (matches.Count > 1)
        {
            var exact = matches
                .Where(p => string.Equals(p.Title.Trim(), trimmedQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
                return exact;
        }

        return matches;
    }

    /// <summary>Точное совпадение (без учёта регистра) с ранее сохранённой обучающей фразой —
    /// см. DatabaseService.AddVoiceProductAlias. Читает из базы каждый раз (аliasов обычно
    /// немного, голосовые команды не настолько часты, чтобы это было заметно) — проще и надёжнее
    /// кэша, который пришлось бы инвалидировать при добавлении/удалении.</summary>
    private static (string ProductId, string ProductTitle)? TryFindAliasByExactPhrase(string phrase)
    {
        try
        {
            var aliases = VoiceLexiconStore.LoadProductAliases();
            foreach (var alias in aliases)
            {
                if (string.Equals(alias.Phrase, phrase, StringComparison.OrdinalIgnoreCase))
                    return (alias.ProductId, alias.ProductTitle);
            }
        }
        catch
        {
            // База недоступна/повреждена — обучающие фразы не критичны, тихо падаем обратно
            // на обычный пословный поиск ниже.
        }

        return null;
    }

    /// <summary>Первые 5 символов слова — за этой длиной обычно начинается падежное окончание
    /// у большинства существительных ("картошк|а/у/и", "молок|о/а/у").</summary>
    private static string Stem(string word) => word.Length <= 5 ? word : word[..5];

    private static bool SharesStem(string queryStem, string titleWord)
    {
        var titleStem = Stem(titleWord);
        // 2026-09-29, проверка голоса: короткое слово названия («с», «в», «от», «1л») — не корень.
        // Раньше «с» из «Кекс … с какао» и «с тушеной говядиной» совпадало с ЛЮБЫМ сказанным словом
        // на «с»: «касса спрайт» и «касса сумка» каждый раз переспрашивали, какой из 3–4 товаров.
        return titleWord.StartsWith(queryStem, StringComparison.OrdinalIgnoreCase)
               || (titleStem.Length >= 3 && queryStem.StartsWith(titleStem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ищет ключевое слово (в любом из принятых вариантов написания) в распознанном
    /// тексте. При успехе возвращает текст ПОСЛЕ него — то, что нужно разбирать как команду.</summary>
    public static bool TryStripWakeWord(string text, out string command)
    {
        var lower = text.ToLowerInvariant();
        var bestIndex = -1;
        var bestLength = 0;

        foreach (var variant in WakeWordVariants)
        {
            var idx = lower.IndexOf(variant, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;
            // При равном начале предпочитаем более длинный вариант ("касса" точнее "каса").
            if (bestIndex < 0 || idx < bestIndex || (idx == bestIndex && variant.Length > bestLength))
            {
                bestIndex = idx;
                bestLength = variant.Length;
            }
        }

        if (bestIndex < 0)
        {
            command = "";
            return false;
        }

        var start = bestIndex + bestLength;
        // 2026-09-29: по-кыргызски «кассага» (кассе), «кассада», «кассанын» — падежное окончание
        // приклеено к слову. Раньше оно уходило в название товара («кассага алма» → товар «га алма»,
        // не найден). Короткое окончание (до 3 букв) отбрасываем вместе со словом.
        var wordEnd = start;
        while (wordEnd < text.Length && char.IsLetter(text[wordEnd]))
            wordEnd++;
        if (wordEnd > start && wordEnd - start <= 3)
            start = wordEnd;

        command = text[start..].Trim();
        return true;
    }

    public static string? ExtractRecognizedText(string voskResultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(voskResultJson);
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
