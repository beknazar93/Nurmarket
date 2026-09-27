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
    // 2026-09-27, «Назначение клавиш»: действия на одиночные клавиши без Ctrl. Раньше Enter был
    // зашит в MainWindow.OnEnterKeyPayTunnel и не настраивался, Num+/Num− не делали ничего.
    Pay,
    AddCatalogItem,
    DecreaseQuantity,
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
        new(PosHotkeyAction.Pay,
            Tr.T("Оплата", "Төлөө", "Pay", "Ödeme", "To'lov"),
            Tr.T("Открывает окно оплаты чека. Не срабатывает, пока курсор в поле ввода и пока сканер дописывает штрихкод.",
                "Чекти төлөө терезесин ачат. Курсор киргизүү талаасында турганда жана сканер штрихкодду жазып бүтө электе иштебейт.",
                "Opens the payment window. Does not fire while the cursor is in an input field or while the scanner is still typing a barcode.",
                "Ödeme penceresini açar. İmleç bir giriş alanındayken ve tarayıcı barkodu yazmayı bitirmeden çalışmaz.",
                "To'lov oynasini ochadi. Kursor kiritish maydonida turganda va skaner shtrix-kodni yozib tugatmaguncha ishlamaydi."),
            "Enter"),
        new(PosHotkeyAction.AddCatalogItem,
            Tr.T("Добавить товар в чек", "Товарды чекке кошуу", "Add product to receipt", "Ürünü fişe ekle", "Mahsulotni chekka qo'shish"),
            Tr.T("Товар под рамкой в каталоге (рамку двигают стрелками) — в чек. Без рамки прибавляет 1 к последней строке чека.",
                "Каталогдогу алкактагы товарды (алкакты жебелер менен жылдырышат) чекке кошот. Алкак жок болсо, чектин акыркы сабына 1 кошот.",
                "Adds the product under the frame in the catalog (move the frame with the arrow keys). Without a frame, adds 1 to the last receipt line.",
                "Katalogda çerçevedeki ürünü fişe ekler (çerçeve ok tuşlarıyla taşınır). Çerçeve yoksa fişin son satırına 1 ekler.",
                "Katalogdagi ramkadagi mahsulotni chekka qo'shadi (ramka strelkalar bilan suriladi). Ramka bo'lmasa, chekning oxirgi qatoriga 1 qo'shadi."),
            "Add"),
        new(PosHotkeyAction.DecreaseQuantity,
            Tr.T("Убавить / убрать товар", "Товарды азайтуу / алып салуу", "Decrease / remove product", "Ürünü azalt / çıkar", "Mahsulotni kamaytirish / olib tashlash"),
            Tr.T("Уменьшает на 1 товар под рамкой каталога, а без рамки — последнюю строку чека. Если осталась одна штука, убирает строку (с обычной проверкой прав).",
                "Каталогдогу алкактагы товарды, алкак жок болсо — чектин акыркы сабын 1ге азайтат. Бир даана калса, сапты алып салат (укукту кадимкидей текшерип).",
                "Decreases the product under the catalog frame by 1, or the last receipt line if there is no frame. If only one is left, removes the line (with the usual permission check).",
                "Katalog çerçevesindeki ürünü, çerçeve yoksa fişin son satırını 1 azaltır. Tek adet kaldıysa satırı kaldırır (olağan yetki kontrolüyle).",
                "Katalog ramkasidagi mahsulotni, ramka bo'lmasa chekning oxirgi qatorini 1 taga kamaytiradi. Bitta qolgan bo'lsa, qatorni olib tashlaydi (odatiy huquq tekshiruvi bilan)."),
            "Subtract"),
        new(PosHotkeyAction.Checkout,
            Tr.T("Оплата из любого поля", "Каалаган талаадан төлөө", "Pay from any field", "Herhangi bir alandan ödeme", "Istalgan maydondan to'lov"),
            Tr.T("Открывает окно оплаты, даже когда курсор стоит в поле поиска или ввода кода.",
                "Курсор издөө же код киргизүү талаасында турса да, төлөм терезесин ачат.",
                "Opens the payment window even when the cursor is in the search or code field.",
                "İmleç arama veya kod alanında olsa bile ödeme penceresini açar.",
                "Kursor qidiruv yoki kod kiritish maydonida tursa ham, to'lov oynasini ochadi."),
            "Ctrl+Enter"),
        new(PosHotkeyAction.ClearCart,
            Tr.T("Очистить корзину", "Себетти тазалоо", "Clear cart", "Sepeti temizle", "Savatni tozalash"),
            Tr.T("Удаляет текущий чек после стандартного подтверждения.", "Кадимки ырастоодон кийин учурдагы чекти өчүрөт.", "Deletes the current receipt after the standard confirmation.", "Standart onaydan sonra mevcut fişi siler.", "Odatiy tasdiqlashdan so'ng joriy chekni o'chiradi."),
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
            Tr.T("Быстрый поиск товара", "Товарды тез издөө", "Quick product search", "Hızlı ürün arama", "Mahsulotni tezkor qidirish"),
            Tr.T("Переводит фокус в строку поиска каталога.", "Фокусту каталогдун издөө сабына которот.", "Moves focus to the catalog search box.", "Odağı katalog arama kutusuna taşır.", "Fokusni katalog qidiruv maydoniga o'tkazadi."),
            "Ctrl+F"),
    ];

    /// <summary>Одиночные клавиши (без Ctrl/Alt/Win), которые можно назначить действию. Буквы,
    /// цифры, пробел, точка и минус с основной клавиатуры сюда не входят: их печатает сканер
    /// штрихкода и кассир в поиске — такое назначение ломало бы и то, и другое.</summary>
    private static readonly HashSet<Key> PlainKeysAllowed =
    [
        Key.Enter, Key.Add, Key.Subtract, Key.Multiply, Key.Divide,
        Key.Insert, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Pause,
    ];

    /// <summary>Сохранённая комбинация действия. Пустая строка — действие отключено (кассир
    /// снял назначение), отсутствие записи — значение по умолчанию.</summary>
    public string GetGesture(PosHotkeyAction action)
    {
        var key = action.ToString();
        if (UserPreferences.Instance.PosHotkeys.TryGetValue(key, out var configured))
        {
            if (string.IsNullOrWhiteSpace(configured))
                return "";
            // F1-F12 без модификаторов зарезервированы под группы товаров (см. Definitions) —
            // старое сохранённое значение отсюда больше не действует, даже если было сохранено
            // до этого изменения.
            if (TryParse(configured, out var parsedKey, out var parsedModifiers) &&
                ValidationError(parsedKey, parsedModifiers) is null)
                return Normalize(configured);
        }

        return Definitions.First(x => x.Action == action).DefaultGesture;
    }

    private static bool IsPlainFunctionKey(Key key) => key is >= Key.F1 and <= Key.F12;

    /// <summary>Комбинация без Ctrl/Alt/Win («одиночная» клавиша, Shift не в счёт).</summary>
    public static bool IsPlain(KeyModifiers modifiers) =>
        (NormalizeModifiers(modifiers) & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;

    public bool TryMatch(KeyEventArgs e, out PosHotkeyAction action) => TryMatch(e, null, out action);

    /// <param name="plain">true — только действия на одиночных клавишах, false — только с
    /// Ctrl/Alt/Win, null — любые.</param>
    public bool TryMatch(KeyEventArgs e, bool? plain, out PosHotkeyAction action)
    {
        foreach (var definition in Definitions)
        {
            if (!TryParse(GetGesture(definition.Action), out var key, out var modifiers))
                continue;
            if (plain is { } wantPlain && IsPlain(modifiers) != wantPlain)
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
                ? value ?? ""
                : definition.DefaultGesture;
            if (string.IsNullOrWhiteSpace(raw))
            {
                normalized[definition.Action] = "";
                continue;
            }

            if (!TryParse(raw, out var key, out var modifiers))
            {
                error = Tr.T($"Некорректная комбинация для «{definition.Title}».",
                    $"«{definition.Title}» үчүн айкалыш туура эмес.",
                    $"Invalid combination for \"{definition.Title}\".",
                    $"«{definition.Title}» için geçersiz tuş kombinasyonu.",
                    $"«{definition.Title}» uchun noto'g'ri kombinatsiya.");
                return false;
            }

            if (ValidationError(key, modifiers) is { } invalid)
            {
                error = $"«{definition.Title}»: {invalid}";
                return false;
            }

            normalized[definition.Action] = Normalize(raw);
        }

        var duplicate = normalized
            .Where(x => x.Value.Length > 0)
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            var shown = Display(duplicate.Key);
            error = Tr.T($"Комбинация {shown} назначена нескольким действиям.",
                $"{shown} айкалышы бир нече аракетке дайындалган.",
                $"The combination {shown} is assigned to more than one action.",
                $"{shown} kombinasyonu birden fazla eyleme atanmış.",
                $"{shown} kombinatsiyasi bir nechta amalga tayinlangan.");
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

    /// <summary>Нажатие в поле назначения клавиши (окно «Горячие» и страница настроек «Клавиши»).
    /// true — поле должно забрать нажатие: <paramref name="gesture"/> — новая комбинация ("" —
    /// назначение снято Delete/Backspace), <paramref name="error"/> — почему такую нельзя.
    /// false — нажатие не наше (одиночный модификатор, Tab — перейти к следующему полю, Esc —
    /// закрыть окно).</summary>
    public static bool TryCapture(KeyEventArgs e, out string? gesture, out string? error)
    {
        gesture = null;
        error = null;
        var modifiers = NormalizeModifiers(e.KeyModifiers);
        if (IsModifierKey(e.Key) || (modifiers == KeyModifiers.None && e.Key is Key.Tab or Key.Escape))
            return false;

        if (modifiers == KeyModifiers.None && e.Key is Key.Back or Key.Delete)
        {
            gesture = "";
            return true;
        }

        error = ValidationError(e.Key, modifiers);
        if (error is null)
            gesture = Format(e.Key, modifiers);
        return true;
    }

    /// <summary>Почему комбинацию нельзя назначить; null — можно.</summary>
    private static string? ValidationError(Key key, KeyModifiers modifiers)
    {
        if (!IsPlain(modifiers))
            return null;
        if (modifiers == KeyModifiers.None && IsPlainFunctionKey(key))
            return Tr.T("F1–F12 без Ctrl заняты группами быстрых товаров.",
                "Ctrl'сиз F1–F12 тез товарлардын топторуна берилген.",
                "F1–F12 without Ctrl are reserved for quick product groups.",
                "Ctrl olmadan F1–F12 hızlı ürün gruplarına ayrılmıştır.",
                "Ctrl'siz F1–F12 tezkor mahsulot guruhlariga band qilingan.");
        if (modifiers == KeyModifiers.None && PlainKeysAllowed.Contains(key))
            return null;
        return Tr.T("Без Ctrl или Alt эту клавишу печатают сканер и кассир в поиске. Добавьте Ctrl/Alt или выберите Enter, Num +, Num −, Num *, Num /, Insert, Home, End, PageUp, PageDown.",
            "Ctrl же Alt'сыз бул баскычты сканер жана издөөдө кассир басат. Ctrl/Alt кошуңуз же Enter, Num +, Num −, Num *, Num /, Insert, Home, End, PageUp, PageDown тандаңыз.",
            "Without Ctrl or Alt this key is typed by the scanner and by the cashier in search. Add Ctrl/Alt or choose Enter, Num +, Num −, Num *, Num /, Insert, Home, End, PageUp, PageDown.",
            "Ctrl veya Alt olmadan bu tuşu tarayıcı ve aramada kasiyer yazar. Ctrl/Alt ekleyin ya da Enter, Num +, Num −, Num *, Num /, Insert, Home, End, PageUp, PageDown seçin.",
            "Ctrl yoki Alt'siz bu tugmani skaner va qidiruvda kassir bosadi. Ctrl/Alt qo'shing yoki Enter, Num +, Num −, Num *, Num /, Insert, Home, End, PageUp, PageDown ni tanlang.");
    }

    public static string Format(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        modifiers = NormalizeModifiers(modifiers);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key == Key.Enter ? "Enter" : key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Комбинация так, как её читает кассир: «Num +» вместо Add, «Enter» вместо Return.
    /// Хранится по-прежнему в формате <see cref="Format"/>.</summary>
    public static string Display(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return Tr.T("не назначено", "дайындалган эмес", "not assigned", "atanmadı", "tayinlanmagan");
        if (!TryParse(gesture, out var key, out var modifiers))
            return gesture;

        var text = Format(key, modifiers);
        var keyName = key switch
        {
            Key.Enter => "Enter",
            Key.Add => "Num +",
            Key.Subtract => "Num −",
            Key.Multiply => "Num *",
            Key.Divide => "Num /",
            Key.Decimal => "Num ,",
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
            _ => null,
        };
        if (keyName is null)
            return text.Replace("+", " + ");
        var prefix = text.Contains('+') ? text[..(text.LastIndexOf('+') + 1)].Replace("+", " + ") : "";
        return prefix + keyName;
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
