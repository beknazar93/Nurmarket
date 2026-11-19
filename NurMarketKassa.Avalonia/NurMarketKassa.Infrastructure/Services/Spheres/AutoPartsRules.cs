using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 1.3, 1.5, 4.3): правила чека, цены СТО и возврата. Работают только в сфере
/// <c>autoparts</c> (<see cref="Active"/>): у остальных сфер ничего не меняется, чеки остаются байт в байт прежними.
/// Настройки владельца — раздел <c>autoparts</c> общего ключа <see cref="SphereSettings"/> (срок возврата, «не принимать установленные
/// детали и электрику»); у товара — местные признаки (<see cref="AutoPartsProductMeta"/>): гарантия в днях, «Электрика», «Установленные не принимаются».
/// </summary>
public static class AutoPartsRules
{
    public const string ProfileId = "autoparts";
    public const int DefaultReturnDays = 14;
    private const string Section = "autoparts";

    /// <summary>Только для проверок: сфера включена/выключена без настоящего профиля.</summary>
    public static bool? ActiveOverride { get; set; }

    public static bool Active => ActiveOverride ?? string.Equals(MarketSpheres.Profile.Id, ProfileId, StringComparison.Ordinal);

    /// <summary>Подпись уровня цены «прораб» в этой сфере (механика та же, что у хозмага).</summary>
    public static string PriceLevelName => Tr.T("СТО / мастер", "СТО / уста", "Garage / mechanic", "Servis / usta", "STO / usta");

    /// <summary>Цена детали для клиента: отмеченному «СТО / мастер» — цена СТО (оптовая цена карточки), если она задана и не выше розницы; иначе розница.
    /// <c>UsedSto</c> — применена цена СТО; <c>Hint</c> — у детали нет цены СТО, остаётся розница (кассиру подсказка, продажу не блокируем).</summary>
    public static (decimal Price, bool UsedSto, bool Hint) StoPrice(decimal retail, decimal stoPrice, bool clientIsSto)
    {
        if (!clientIsSto)
            return (retail, false, false);
        return stoPrice > 0m && stoPrice <= retail ? (stoPrice, true, false) : (retail, false, true);
    }

    // ───────────── настройки владельца ─────────────

    /// <summary>Срок возврата, дней (не считая дня покупки). Умолчание 14; 0 — не проверять.</summary>
    public static int GetReturnDays(string? json = null) =>
        SphereSettings.Section(json ?? UserPreferences.Instance.SphereSettingsJson, Section)?["returnDays"] is JsonValue v
        && v.TryGetValue<int>(out var d) ? Math.Clamp(d, 0, 365) : DefaultReturnDays;

    /// <summary>«Не принимать установленные детали и электрику» — по умолчанию включено (решение владельца 19.11).</summary>
    public static bool GetNoInstalled(string? json = null) =>
        SphereSettings.Section(json ?? UserPreferences.Instance.SphereSettingsJson, Section)?["noInstalled"] is not JsonValue v
        || !v.TryGetValue<bool>(out var b) || b;

    public static bool SetReturnTerms(int days, bool noInstalled)
    {
        var section = SphereSettings.Section(UserPreferences.Instance.SphereSettingsJson, Section) ?? new JsonObject();
        section["returnDays"] = Math.Clamp(days, 0, 365);
        section["noInstalled"] = noInstalled;
        return SphereSettings.TrySetSection(Section, section);
    }

    // ───────────── возврат ─────────────

    /// <summary>Слова (начала слов), по которым касса сама считает деталь электрикой, пока у товара нет явной отметки.</summary>
    private static readonly string[] ElectricWords =
        { "датчик", "лямбд", "эбу", "реле", "генератор", "стартер", "катушк", "форсунк", "электр", "блок упр", "sensor", "датчи" };

    public static bool LooksElectric(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;
        var t = title.ToLowerInvariant().Replace('ё', 'е');
        var words = t.Split(new[] { ' ', ',', '.', '-', '/', '(', ')', '«', '»', '"' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            foreach (var k in ElectricWords)
            {
                if (words[i].StartsWith(k, StringComparison.Ordinal))
                    return true;
                // «блок управления»: два слова подряд
                if (k.Contains(' ') && i + 1 < words.Length && (words[i] + " " + words[i + 1]).StartsWith(k, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Итог проверки возврата: срок прошёл и/или есть позиции, которые не принимают.</summary>
    public sealed class ReturnVerdict
    {
        public bool Expired { get; init; }
        public int DaysPassed { get; init; }
        public int Limit { get; init; }
        /// <summary>Названия позиций «электрика / установленные детали не принимаются».</summary>
        public IReadOnlyList<string> NotAccepted { get; init; } = Array.Empty<string>();
        /// <summary>Нужен PIN руководителя, чтобы продолжить.</summary>
        public bool NeedsPin => Expired || NotAccepted.Count > 0;
    }

    /// <summary>Чистая проверка (без настроек и файлов — для тестов). День покупки не считается; limitDays = 0 — срок не проверяется.
    /// <paramref name="lines"/>: название, «электрика/не принимается» по признаку товара.</summary>
    public static ReturnVerdict CheckReturn(DateTime? saleDate, DateTime today, int limitDays, bool noInstalled,
        IEnumerable<(string Title, bool Flagged)> lines)
    {
        var passed = saleDate is { } d && d > DateTime.MinValue ? Math.Max(0, (today.Date - d.Date).Days) : 0;
        var expired = saleDate is not null && limitDays > 0 && passed > limitDays;
        var bad = noInstalled
            ? lines.Where(l => l.Flagged).Select(l => l.Title).ToList()
            : new List<string>();
        return new ReturnVerdict { Expired = expired, DaysPassed = passed, Limit = limitDays, NotAccepted = bad };
    }

    /// <summary>Проверка по настройкам и карточкам товаров (для окон возврата). Не сфера — пустой «всё можно».</summary>
    public static ReturnVerdict CheckReturnNow(DateTime? saleDate, IEnumerable<(string? ProductId, string Title)> lines) =>
        !Active
            ? new ReturnVerdict()
            : CheckReturn(saleDate, DateTime.Today, GetReturnDays(), GetNoInstalled(),
                lines.Select(l => (l.Title, AutoPartsProductMeta.IsNotReturnable(l.ProductId, l.Title))));

    /// <summary>Текст предупреждения при возврате: «Срок возврата истёк (N дн.)» и/или «Электрика не подлежит возврату» + как продолжить (PIN руководителя).</summary>
    public static string ReturnWarningText(ReturnVerdict verdict)
    {
        var parts = new List<string>();
        if (verdict.Expired)
            parts.Add(Tr.T($"Срок возврата истёк ({verdict.DaysPassed} дн., разрешено {verdict.Limit}).",
                $"Кайтаруу мөөнөтү өттү ({verdict.DaysPassed} күн, уруксат {verdict.Limit}).",
                $"The return period has expired ({verdict.DaysPassed} days, {verdict.Limit} allowed).",
                $"İade süresi doldu ({verdict.DaysPassed} gün, izin verilen {verdict.Limit}).",
                $"Qaytarish muddati o'tdi ({verdict.DaysPassed} kun, ruxsat {verdict.Limit})."));
        if (verdict.NotAccepted.Count > 0)
            parts.Add(Tr.T("Электрика и установленные детали не подлежат возврату:\n", "Электрика жана орнотулган тетиктер кайтарылбайт:\n",
                "Electrical and installed parts are not returnable:\n", "Elektrik ve takılmış parçalar iade edilmez:\n", "Elektrika va o'rnatilgan detallar qaytarilmaydi:\n")
                + string.Join("\n", verdict.NotAccepted.Select(n => "• " + n)));
        var text = string.Join("\n\n", parts) + "\n\n"
            + Tr.T("Продолжить можно только с PIN руководителя. Если это брак или гарантия — выберите причину «Брак» / «Гарантия».",
                "Улантуу үчүн жетекчинин PIN коду керек. Бузук же кепилдик болсо — «Бузук» / «Кепилдик» себебин тандаңыз.",
                "Continuing needs the manager PIN. If it is a defect or warranty case, choose the reason “Defective” / “Warranty”.",
                "Devam etmek için yönetici PIN'i gerekir. Kusur veya garanti ise «Kusurlu» / «Garanti» nedenini seçin.",
                "Davom etish uchun rahbar PIN kodi kerak. Nuqson yoki kafolat bo'lsa — «Nuqsonli» / «Kafolat» sababini tanlang.");
        return text;
    }

    // ───────────── гарантия и чек ─────────────

    /// <summary>Гарантия товара, дней: местная отметка карточки; иначе warranty_months сервера × 30; иначе 0.</summary>
    public static int WarrantyDaysOf(string? productId)
    {
        if (AutoPartsProductMeta.Get(productId)?.WarrantyDays is { } days && days > 0)
            return days;
        return ProductSerialRules.WarrantyMonths(productId) is { } m && m > 0 ? m * 30 : 0;
    }

    /// <summary>Строка под позицией чека «Гарантия N дн.»; пусто — не сфера или у товара нет гарантии.</summary>
    public static string? WarrantyLine(string? productId)
    {
        if (!Active)
            return null;
        var days = WarrantyDaysOf(productId);
        return days > 0 ? Tr.T($"Гарантия {days} дн.", $"Кепилдик {days} күн", $"Warranty {days} days", $"Garanti {days} gün", $"Kafolat {days} kun") : null;
    }

    /// <summary>Блок «Гарантия и возврат» внизу чека (пусто — не сфера). Срок и условия — из настроек владельца.</summary>
    public static List<string> ReceiptReturnBlock(int width)
    {
        var lines = new List<string>();
        if (!Active)
            return lines;
        var days = GetReturnDays();
        lines.Add(ReceiptLineLayout.Center(Tr.T("Гарантия и возврат", "Кепилдик жана кайтаруу", "Warranty and returns", "Garanti ve iade", "Kafolat va qaytarish"), width));
        if (days > 0)
            lines.AddRange(ReceiptLineLayout.WrapLeft(Tr.T($"Возврат и обмен — {days} дн. со дня покупки при сохранении упаковки и чека.",
                $"Кайтаруу жана алмаштыруу — сатып алган күндөн {days} күн, таңгагы жана чеги сакталса.",
                $"Returns and exchanges within {days} days of purchase with the packaging and receipt.",
                $"İade ve değişim — satın alma gününden itibaren {days} gün, ambalaj ve fiş saklanmışsa.",
                $"Qaytarish va almashtirish — xarid kunidan {days} kun, qadoq va chek saqlansa."), width));
        if (GetNoInstalled())
            lines.AddRange(ReceiptLineLayout.WrapLeft(Tr.T("Установленные детали и электрика (датчики, блоки, реле) возврату не подлежат.",
                "Орнотулган тетиктер жана электрика (сенсорлор, блоктор, реле) кайтарылбайт.",
                "Installed parts and electrical parts (sensors, units, relays) are not returnable.",
                "Takılmış parçalar ve elektrik aksamı (sensör, ünite, röle) iade edilmez.",
                "O'rnatilgan detallar va elektrika (datchiklar, bloklar, rele) qaytarilmaydi."), width));
        lines.Add("");
        return lines;
    }
}

/// <summary>2026-11-20: местные признаки товара «Автозапчастей» (гарантия в днях, «Электрика», «Установленные не принимаются»). Файл
/// autoparts-products.json в %LOCALAPPDATA% (AtomicFile; данные компании — CompanyStoreRegistry); схема pos_local.db и сервер не затронуты.
/// Карточка товара пишет сюда через <see cref="Set"/>; без записи касса решает по названию (<see cref="AutoPartsRules.LooksElectric"/>).</summary>
public static class AutoPartsProductMeta
{
    public const string FileName = "autoparts-products.json";

    public sealed class Meta
    {
        public int? WarrantyDays { get; set; }
        public bool Electric { get; set; }
        public bool NoReturn { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly object Gate = new();

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.KassaLocal, FileName);

    private static Dictionary<string, Meta> Load()
    {
        try
        {
            var text = AtomicFile.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(text)
                ? new Dictionary<string, Meta>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Meta>(JsonSerializer.Deserialize<Dictionary<string, Meta>>(text, Json) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, Meta>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static Meta? Get(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        lock (Gate)
            return Load().TryGetValue(productId.Trim(), out var m) ? m : null;
    }

    public static void Set(string? productId, int? warrantyDays, bool electric, bool noReturn)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        lock (Gate)
        {
            var all = Load();
            all[productId.Trim()] = new Meta { WarrantyDays = warrantyDays is > 0 ? Math.Min(warrantyDays.Value, 3650) : null, Electric = electric, NoReturn = noReturn };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        }
    }

    /// <summary>2026-11-20: гарантия из поля карточки: целое число дней, пусто / не число / 0 — «нет» (null). Больше 3650 — 3650 (как в <see cref="Set"/>).</summary>
    public static int? ParseDays(string? text) =>
        int.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? Math.Min(d, 3650) : null;

    /// <summary>Позиция «не принимается» (электрика / установленные): явная отметка карточки; без неё — по названию.</summary>
    public static bool IsNotReturnable(string? productId, string? title)
    {
        var m = Get(productId);
        return m is not null ? m.Electric || m.NoReturn : AutoPartsRules.LooksElectric(title);
    }
}
