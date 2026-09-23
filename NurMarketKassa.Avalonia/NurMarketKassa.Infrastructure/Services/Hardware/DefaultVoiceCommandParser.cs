namespace NurMarketKassa.Services.Hardware;

/// <summary>Правило-based разбор намерения по ключевым фразам — не ML, но тестируется сотнями
/// текстовых примеров без микрофона (что и требовалось: IVoiceCommandParser не завязан на UI/аудио).
/// Порядок проверок важен: более специфичные фразы (очистка/удаление/поиск/повтор/отмена) проверяются
/// раньше общего случая "добавить товар", иначе "очисти чек" разобрался бы как товар "очисти чек".</summary>
public sealed class DefaultVoiceCommandParser : IVoiceCommandParser
{
    private static readonly string[] ClearCartPhrases =
        [
            "очисти чек", "очистить чек", "очисти корзин", "очистить корзин",
            // Кыргызча
            "чекти тазала", "себетти тазала", "баарын өчүр",
        ];

    private static readonly string[] RemoveLastPhrases =
        [
            "убери последн", "удали последн", "убрать последн", "удалить последн",
            // Кыргызча
            "акыркысын өчүр", "акыркысын алып сал", "акыркысын жок кыл",
        ];

    private static readonly string[] FindPrefixes = ["найди ", "найти ", "поищи ", "ищи "];

    // Кыргызча "табуу" (найти) естественно ставится ПОСЛЕ названия товара ("канты тап" — "сахар
    // найди"), а не перед ним, как в русском — поэтому отдельный суффиксный список, а не общий
    // FindPrefixes.
    private static readonly string[] FindSuffixes = [" тап", " изде"];

    private static readonly string[] RepeatPhrases = ["повтори", "повтор", "кайтала"];

    private static readonly string[] CancelPhrases = ["отмена", "отмени", "жокко чыгар", "токтот"];

    // "касса оплата"/"касса оплатить (чек)" — переход к оплате (2026-09-05, по запросу
    // пользователя: "голосовая команда должна оплачивать"). "төлө" перекрывает
    // "төлөө"/"төлөм"/"төлөп" — всё это формы одного корня.
    private static readonly string[] PayPhrases = ["оплат", "төлө"];

    public VoiceCommand Parse(string commandText)
    {
        var text = (commandText ?? "").Trim();
        var lower = text.ToLowerInvariant();

        if (text.Length == 0)
            return new VoiceCommand { Intent = VoiceIntent.Unknown, RawText = text };

        if (ClearCartPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
        {
            return new VoiceCommand
            {
                Intent = VoiceIntent.ClearCart,
                RawText = text,
                RequiresConfirmation = true,
            };
        }

        if (RemoveLastPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
        {
            return new VoiceCommand
            {
                Intent = VoiceIntent.RemoveLastItem,
                RawText = text,
                RequiresConfirmation = true,
            };
        }

        foreach (var prefix in FindPrefixes)
        {
            var idx = lower.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var productText = text[(idx + prefix.Length)..].Trim();
            return new VoiceCommand
            {
                Intent = VoiceIntent.FindProduct,
                ProductText = productText,
                RawText = text,
            };
        }

        foreach (var suffix in FindSuffixes)
        {
            if (!lower.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var productText = text[..^suffix.Length].Trim();
            return new VoiceCommand
            {
                Intent = VoiceIntent.FindProduct,
                ProductText = productText,
                RawText = text,
            };
        }

        // Целиком одно слово "повтори"/"повтор" — если это часть более длинной фразы, скорее
        // всего это часть названия товара, а не команда.
        if (RepeatPhrases.Contains(lower))
            return new VoiceCommand { Intent = VoiceIntent.RepeatLast, RawText = text };

        if (CancelPhrases.Contains(lower))
            return new VoiceCommand { Intent = VoiceIntent.Cancel, RawText = text };

        if (PayPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return new VoiceCommand { Intent = VoiceIntent.Pay, RawText = text };

        var (productQuery, quantity, unitKind) = VoiceCommandParser.ExtractQuantity(text);
        // Голая фраза "пачка"/"поштучно 3" без названия товара (productQuery пуст) — НЕ Unknown,
        // если при этом распознана единица (unitKind != None): это не мусор, а вероятный ответ
        // на уже открытый PackageChoiceDialog ("Поштучно или целая пачка?"), который слушает
        // именно такие команды сам (см. PackageChoiceDialog — подписка на CommandRecognized,
        // 2026-09-05). Только когда И товар не назван, И единица не распознана — действительно
        // нечего разбирать.
        if (string.IsNullOrWhiteSpace(productQuery) && unitKind == VoiceUnitKind.None)
            return new VoiceCommand { Intent = VoiceIntent.Unknown, RawText = text };

        return new VoiceCommand
        {
            Intent = VoiceIntent.AddProduct,
            ProductText = productQuery,
            Quantity = quantity,
            UnitKind = unitKind,
            RawText = text,
        };
    }
}
