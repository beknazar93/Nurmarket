using Avalonia.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

public enum PosHotkeyAction
{
    Checkout,
    ClearCart,
    ToggleCustomerDisplay,
    ApplyDiscount,
    FocusProductSearch,
}

public sealed record PosHotkeyDefinition(
    PosHotkeyAction Action,
    string Title,
    string Description,
    string DefaultGesture);

public sealed class PosHotkeyService
{
    // F1-F12 без модификаторов зарезервированы под группы быстрых товаров (как в веб-версии
    // NurCRM) — старые действия кассы переведены на Ctrl+... комбинации, чтобы не конфликтовать.
    //
    // 2026-09-08: раньше это было static readonly поле — Title/Description вычислялись РОВНО
    // ОДИН РАЗ при первой загрузке класса и навсегда застывали на том языке, что был активен
    // в тот момент (обычно русский, если касса вообще не переключала язык раньше) — отсюда
    // жалоба владельца на скриншот "Кассанын ыкчам баскычтары" (заголовок и подсказки уже
    // переведены через DynamicResource в XAML, а сами 5 действий — нет). Теперь свойство,
    // читает Tr.T() при каждом обращении, как и в остальных подобных фиксах в этой сессии.
    public static IReadOnlyList<PosHotkeyDefinition> Definitions =>
    [
        new(PosHotkeyAction.Checkout,
            Tr.T("Оплата / провести чек", "Төлөө / чекти өткөрүү", "Pay / process receipt", "Öde / fişi işle", "To'lash / chekni bajarish"),
            Tr.T("Открывает окно оплаты текущего чека.", "Учурдагы чектин төлөм терезесин ачат.", "Opens the payment window for the current receipt.", "Mevcut fişin ödeme penceresini açar.", "Joriy chekning to'lov oynasini ochadi."),
            "Ctrl+Enter"),
        new(PosHotkeyAction.ClearCart,
            Tr.T("Очистить корзину", "Себетти тазалоо", "Clear cart", "Sepeti temizle", "Savatni tozalash"),
            Tr.T("Удаляет текущий чек после стандартного подтверждения.", "Стандарттуу ырастоодон кийин учурдагы чекти өчүрөт.", "Deletes the current receipt after the standard confirmation.", "Standart onaydan sonra mevcut fişi siler.", "Standart tasdiqdan so'ng joriy chekni o'chiradi."),
            "Ctrl+F1"),
        new(PosHotkeyAction.ToggleCustomerDisplay,
            Tr.T("Экран покупателя", "Сатып алуучу экраны", "Customer display", "Müşteri ekranı", "Xaridor ekrani"),
            Tr.T("Открывает или скрывает экран покупателя.", "Сатып алуучу экранын ачат же жашырат.", "Opens or hides the customer display.", "Müşteri ekranını açar veya gizler.", "Xaridor ekranini ochadi yoki yashiradi."),
            "Ctrl+M"),
        new(PosHotkeyAction.ApplyDiscount,
            Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fişe indirim", "Chekka chegirma"),
            Tr.T("Открывает окно применения скидки.", "Арзандатууну колдонуу терезесин ачат.", "Opens the discount window.", "İndirim uygulama penceresini açar.", "Chegirma qo'llash oynasini ochadi."),
            "Ctrl+D"),
        new(PosHotkeyAction.FocusProductSearch,
            Tr.T("Быстрый поиск товара", "Товарды тез издөө", "Quick product search", "Hızlı ürün arama", "Tezkor mahsulot qidirish"),
            Tr.T("Переводит фокус в строку поиска каталога.", "Фокусту каталогдун издөө сабына которот.", "Moves focus to the catalog search box.", "Odağı katalog arama kutusuna taşır.", "Fokusni katalog qidiruv maydoniga o'tkazadi."),
            "Ctrl+F"),
    ];

    public string GetGesture(PosHotkeyAction action)
    {
        var key = action.ToString();
        if (UserPreferences.Instance.PosHotkeys.TryGetValue(key, out var configured) &&
            TryParse(configured, out var parsedKey, out var parsedModifiers) &&
            // F1-F12 без модификаторов зарезервированы под группы товаров (см. Definitions) —
            // старое сохранённое значение отсюда больше не действует, даже если было сохранено
            // до этого изменения.
            !(parsedModifiers == KeyModifiers.None && IsPlainFunctionKey(parsedKey)))
            return Normalize(configured);

        return Definitions.First(x => x.Action == action).DefaultGesture;
    }

    private static bool IsPlainFunctionKey(Key key) => key is >= Key.F1 and <= Key.F12;

    public bool TryMatch(KeyEventArgs e, out PosHotkeyAction action)
    {
        foreach (var definition in Definitions)
        {
            if (!TryParse(GetGesture(definition.Action), out var key, out var modifiers))
                continue;
            if (e.Key == key && NormalizeModifiers(e.KeyModifiers) == modifiers)
            {
                action = definition.Action;
                return true;
            }
        }

        action = default;
        return false;
    }

    public IReadOnlyDictionary<PosHotkeyAction, string> GetAll() =>
        Definitions.ToDictionary(x => x.Action, x => GetGesture(x.Action));

    public bool Save(IReadOnlyDictionary<PosHotkeyAction, string> gestures, out string? error)
    {
        var normalized = new Dictionary<PosHotkeyAction, string>();
        foreach (var definition in Definitions)
        {
            var raw = gestures.TryGetValue(definition.Action, out var value)
                ? value
                : definition.DefaultGesture;
            if (!TryParse(raw, out _, out _))
            {
                error = Tr.T($"Некорректная комбинация для «{definition.Title}».",
                    $"«{definition.Title}» үчүн айкалыш туура эмес.",
                    $"Invalid combination for \"{definition.Title}\".",
                    $"\"{definition.Title}\" için geçersiz kombinasyon.",
                    $"\"{definition.Title}\" uchun noto'g'ri kombinatsiya.");
                return false;
            }

            normalized[definition.Action] = Normalize(raw);
        }

        var duplicate = normalized
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            error = Tr.T($"Комбинация {duplicate.Key} назначена нескольким действиям.",
                $"{duplicate.Key} айкалышы бир нече аракетке дайындалган.",
                $"The combination {duplicate.Key} is assigned to more than one action.",
                $"{duplicate.Key} kombinasyonu birden fazla eyleme atanmış.",
                $"{duplicate.Key} kombinatsiyasi bir nechta amalga tayinlangan.");
            return false;
        }

        UserPreferences.Instance.PosHotkeys = normalized.ToDictionary(
            x => x.Key.ToString(),
            x => x.Value,
            StringComparer.OrdinalIgnoreCase);
        UserPreferences.Instance.SaveToDisk();
        error = null;
        return true;
    }

    public void ResetToDefaults()
    {
        UserPreferences.Instance.PosHotkeys = Definitions.ToDictionary(
            x => x.Action.ToString(),
            x => x.DefaultGesture,
            StringComparer.OrdinalIgnoreCase);
        UserPreferences.Instance.SaveToDisk();
    }

    public static string Format(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        modifiers = NormalizeModifiers(modifiers);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string Normalize(string gesture) =>
        TryParse(gesture, out var key, out var modifiers)
            ? Format(key, modifiers)
            : gesture.Trim();

    private static bool TryParse(string? gesture, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        foreach (var rawPart in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Meta;
            else if (!Enum.TryParse(part, true, out key) || key == Key.None || IsModifierKey(key))
                return false;
        }

        return key != Key.None;
    }

    private static KeyModifiers NormalizeModifiers(KeyModifiers modifiers) =>
        modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
}
