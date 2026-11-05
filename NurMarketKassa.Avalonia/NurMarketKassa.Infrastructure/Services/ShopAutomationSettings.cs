using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, docs/Отчеты/Поиск решений на GitHub и GitLab — функции магазина): настройки новых «умных» функций магазина в одном файле
// shop-automation.json — ОБЩИЙ файл кассы и программы владельца (%LOCALAPPDATA%\NurMarketKassa, с 05.11.2026; старые файлы в %APPDATA%\NurMarketKassa и \NurMarketOwner сливаются при первом чтении). Все новые функции по умолчанию ведут себя как раньше
// (кроме предупреждения о просрочке при продаже — оно включено в режиме «предупреждать»). Файл не читается — берутся умолчания, испорченный
// откладывается рядом (shop-automation.bad-…json). Денег файл не содержит.

/// <summary>Режим предупреждения о просрочке при продаже.</summary>
public static class ExpiryModes
{
    public const string Off = "off";
    public const string Warn = "warn";
    /// <summary>Просроченный товар продаётся только после PIN владельца.</summary>
    public const string Block = "block";

    public static string Normalize(string? mode) => mode is Off or Warn or Block ? mode : Warn;
}

/// <summary>Свойства поставщика, которых нет на сервере: срок поставки (дней), минимальная сумма заказа.</summary>
public sealed class SupplierPlanRule
{
    public int LeadDays { get; set; }
    public double MinOrderSum { get; set; }
}

/// <summary>Свойства товара для автозаказа: кратность упаковки и минимальная партия (штук).</summary>
public sealed class ProductPlanRule
{
    public double Pack { get; set; }
    public double MinQty { get; set; }
}

/// <summary>Запасной поставщик: кому спрашиваем цены (WhatsApp).</summary>
public sealed class ReserveSupplier
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
}

/// <summary>Правила «Сообщать в Telegram, если…».</summary>
public sealed class ShopAlertRules
{
    public bool Enabled { get; set; }
    public bool ShiftShortage { get; set; } = true;
    public double ShiftShortageAmount { get; set; } = 500;
    public bool BigReturn { get; set; } = true;
    public double BigReturnAmount { get; set; } = 3000;
    public bool RevenueBehindPlan { get; set; }
    /// <summary>На сколько процентов ниже обычного дня недели считать «отстаёт» (по умолчанию 30).</summary>
    public int RevenueBehindPercent { get; set; } = 30;
    /// <summary>Час проверки («к 18:00»).</summary>
    public int RevenueCheckHour { get; set; } = 18;
    /// <summary>Какая часть дневной выручки обычно набирается к часу проверки (по умолчанию 70 %).</summary>
    public int RevenueShareByCheck { get; set; } = 70;
    public bool ClassAOut { get; set; } = true;
    public bool NewDebt { get; set; }
    public double NewDebtAmount { get; set; } = 5000;
    public bool Expired { get; set; } = true;
    /// <summary>Вечерний итог дня в Telegram.</summary>
    public bool EveningSummary { get; set; }
    public int EveningHour { get; set; } = 20;
    /// <summary>Недельный отчёт по понедельникам в 9:00.</summary>
    public bool WeeklyReport { get; set; }
    /// <summary>Тихие часы: с QuietFromHour до QuietToHour сообщения не шлём (кроме вечернего итога в своё время).</summary>
    public int QuietFromHour { get; set; } = 22;
    public int QuietToHour { get; set; } = 8;
}

public sealed class ShopAutomationData
{
    // ---- 1. Просрочка
    public string ExpiryMode { get; set; } = ExpiryModes.Warn;
    /// <summary>2026-11-05 (ревью волн 0–3, С6): однократная подсказка владельцу «касса предупреждает о просроченном при продаже» уже показана.</summary>
    public bool ExpiryHintShown { get; set; }

    // ---- 2. Автозаказ v2 (всё выключено — расчёт как раньше)
    public bool PlanLostDemand { get; set; }
    public bool PlanPackRules { get; set; }
    public bool PlanWeekday { get; set; }
    public bool PlanTsb { get; set; }
    public bool PlanLeadTime { get; set; }
    public bool PlanXyzSafety { get; set; }
    /// <summary>Утром проверять «пора заказать у N поставщиков на X сом» (Сводка и Telegram).</summary>
    public bool MorningOrderCheck { get; set; }
    public Dictionary<string, SupplierPlanRule> Suppliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProductPlanRule> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ReserveSupplier> ReserveSuppliers { get; set; } = new();

    // ---- 4. Подсказки цены
    /// <summary>«Красивая» цена: шаг округления вверх (0 — не округлять, 1, 5, 10 сом).</summary>
    public int PriceNiceStep { get; set; } = 5;
    /// <summary>Целевая наценка по категориям, % (кроме общей в «Наценке» приёмки): категория → %. 0 — взять общую.</summary>
    public Dictionary<string, double> CategoryTargetMarkup { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- 5. Оповещения
    public ShopAlertRules Alerts { get; set; } = new();

    public void Normalize()
    {
        ExpiryMode = ExpiryModes.Normalize(ExpiryMode);
        Suppliers ??= new(StringComparer.OrdinalIgnoreCase);
        Products ??= new(StringComparer.OrdinalIgnoreCase);
        ReserveSuppliers ??= new();
        CategoryTargetMarkup ??= new(StringComparer.OrdinalIgnoreCase);
        Alerts ??= new();
        Suppliers = new Dictionary<string, SupplierPlanRule>(Suppliers, StringComparer.OrdinalIgnoreCase);
        Products = new Dictionary<string, ProductPlanRule>(Products, StringComparer.OrdinalIgnoreCase);
        CategoryTargetMarkup = new Dictionary<string, double>(CategoryTargetMarkup, StringComparer.OrdinalIgnoreCase);
        if (PriceNiceStep is not (0 or 1 or 5 or 10))
            PriceNiceStep = 5;
        var a = Alerts;
        a.QuietFromHour = Math.Clamp(a.QuietFromHour, 0, 23);
        a.QuietToHour = Math.Clamp(a.QuietToHour, 0, 23);
        a.RevenueCheckHour = Math.Clamp(a.RevenueCheckHour, 0, 23);
        a.EveningHour = Math.Clamp(a.EveningHour, 0, 23);
        a.RevenueBehindPercent = Math.Clamp(a.RevenueBehindPercent, 1, 99);
        a.RevenueShareByCheck = Math.Clamp(a.RevenueShareByCheck, 10, 100);
        a.ShiftShortageAmount = Math.Max(0, double.IsFinite(a.ShiftShortageAmount) ? a.ShiftShortageAmount : 500);
        a.BigReturnAmount = Math.Max(0, double.IsFinite(a.BigReturnAmount) ? a.BigReturnAmount : 3000);
        a.NewDebtAmount = Math.Max(0, double.IsFinite(a.NewDebtAmount) ? a.NewDebtAmount : 5000);
        foreach (var s in Suppliers.Values)
        {
            s.LeadDays = Math.Clamp(s.LeadDays, 0, 90);
            s.MinOrderSum = Math.Max(0, double.IsFinite(s.MinOrderSum) ? s.MinOrderSum : 0);
        }
        foreach (var p in Products.Values)
        {
            p.Pack = Math.Max(0, double.IsFinite(p.Pack) ? p.Pack : 0);
            p.MinQty = Math.Max(0, double.IsFinite(p.MinQty) ? p.MinQty : 0);
        }
    }
}

/// <summary>Хранилище настроек. 2026-11-05 (ревью волн 0–3, В2): ОДИН общий файл для кассы и программы владельца — %LOCALAPPDATA%\NurMarketKassa\shop-automation.json
/// (как antifraud-settings.json). Раньше у каждой программы был свой файл в %APPDATA%\&lt;NurMarketKassa|NurMarketOwner&gt;\, и запрет просрочки / оповещения,
/// включённые владельцем, на кассе не действовали. Старые файлы при первом чтении сливаются в общий (ничего не удаляется). Файл перечитывается,
/// если его изменила другая программа (проверка отметки времени не чаще раза в 2 с).</summary>
public static class ShopAutomationSettings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static ShopAutomationData? _current;
    private static DateTime _readStamp;
    private static long _checkedTick;
    // 2026-11-06 (аудит №5, Л5-10): последнее чтение файла не удалось (занят, нет доступа) — прежние настройки остаются, файл не переименовывается и не перезаписывается.
    private static bool _readFailed;

    /// <summary>Только для тестов и стенда: другая папка вместо %LOCALAPPDATA%\NurMarketKassa.</summary>
    public static string? DataDirOverride { get; set; }

    public static event Action? Changed;

    private static string Folder => DataDirOverride ?? Path.Combine(AppPaths.Local, "NurMarketKassa");

    private static string FilePath => Path.Combine(Folder, "shop-automation.json");

    /// <summary>Текущие настройки (читаются из общего файла; менять — только через <see cref="Update"/>).</summary>
    public static ShopAutomationData Current
    {
        get
        {
            lock (Gate)
                return CurrentLocked();
        }
    }

    private static ShopAutomationData CurrentLocked()
    {
        var tick = Environment.TickCount64;
        if (_current is null || tick - _checkedTick >= 2000 || tick < _checkedTick)
        {
            _checkedTick = tick;
            var stamp = StampOf(FilePath);
            if (_current is null || stamp != _readStamp || _readFailed)
            {
                var fresh = Read();
                if (!_readFailed || _current is null)
                    _current = fresh; // сбой чтения и есть прежние настройки — остаются они (Л5-10)
                if (!_readFailed)
                    _readStamp = StampOf(FilePath);
            }
        }
        return _current;
    }

    public static void Update(Action<ShopAutomationData> change)
    {
        lock (Gate)
        {
            _checkedTick = 0; // перед правкой берём самое свежее содержимое файла
            // 2026-11-05 (Н13): правим копию и подменяем ссылку — читатели в фоне не видят полуготовый объект.
            var cur = CurrentLocked();
            if (_readFailed)
            {
                // 2026-11-06 (Л5-10): файл не прочитан — записать «изменённые умолчания» поверх настоящих настроек нельзя.
                PosLogger.Log("Автоматизация магазина: файл настроек сейчас не читается — изменение не записано, повторите позже.", "WARNING");
                return;
            }
            var data = Clone(cur);
            change(data);
            data.Normalize();
            _current = data;
            try
            {
                Directory.CreateDirectory(Folder);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(data, Json));
                _readStamp = StampOf(FilePath);
                _checkedTick = Environment.TickCount64;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Автоматизация магазина: настройки не записаны ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Сбросить кеш (тесты, смена папки).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _current = null;
            _readStamp = default;
            _checkedTick = 0;
        }
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception) { return default; }
    }

    private static ShopAutomationData Clone(ShopAutomationData src)
    {
        var copy = JsonSerializer.Deserialize<ShopAutomationData>(JsonSerializer.Serialize(src, Json), Json) ?? new ShopAutomationData();
        copy.Normalize();
        return copy;
    }

    private static ShopAutomationData Read()
    {
        _readFailed = false;
        try
        {
            if (File.Exists(FilePath))
            {
                // 2026-11-06 (аудит №5, Л5-10): чтение через AtomicFile (повторы, откат на .bak). Файл занят — НЕ «битый»: не переименовываем в .bad и не сбрасываем
                // режим просрочки/оповещения на умолчания; берём прежние настройки, а если их нет — копию .bak или безопасный режим.
                var r = AtomicFile.TryRead(FilePath, attempts: 2, pauseMs: 50);
                if (r.Status == AtomicFile.ReadStatus.Unreadable)
                {
                    _readFailed = true;
                    if (_current is not null)
                        return _current;
                    PosLogger.Log("Автоматизация магазина: файл настроек занят и не прочитан — взяты прежние настройки (копия .bak) или безопасный режим.", "WARNING");
                    if (r.Text is { } bakText && JsonSerializer.Deserialize<ShopAutomationData>(bakText, Json) is { } fromBak)
                    {
                        fromBak.Normalize();
                        return fromBak;
                    }
                    var safe = new ShopAutomationData { ExpiryMode = ExpiryModes.Block };
                    safe.Normalize();
                    return safe;
                }
                var data = JsonSerializer.Deserialize<ShopAutomationData>(r.Text ?? "{}", Json) ?? new ShopAutomationData();
                data.Normalize();
                return data;
            }
            if (DataDirOverride is null && MigrateLegacy() is { } migrated)
                return migrated;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автоматизация магазина: файл настроек не прочитан ({ex.GetType().Name}) — взяты умолчания.", "WARNING");
            try
            {
                File.Move(FilePath, Path.Combine(Path.GetDirectoryName(FilePath)!, $"shop-automation.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
            }
            catch (Exception)
            {
                // файл останется на месте; при следующей записи он будет заменён
            }
        }
        var fresh = new ShopAutomationData();
        fresh.Normalize();
        return fresh;
    }

    /// <summary>2026-11-05 (В2): старые файлы касса/владелец (%APPDATA%\NurMarketKassa и \NurMarketOwner) → один общий. За основу берётся более новый,
    /// из второго добавляются недостающие правила поставщиков/товаров/категорий и запасные поставщики; защита (режим просрочки, оповещения) не ослабляется.
    /// Старые файлы не удаляются. null — старых файлов нет.</summary>
    private static ShopAutomationData? MigrateLegacy()
    {
        var found = new List<(DateTime At, ShopAutomationData Data)>();
        foreach (var folder in new[] { "NurMarketKassa", "NurMarketOwner" })
        {
            try
            {
                var old = Path.Combine(AppPaths.Roaming, folder, "shop-automation.json");
                if (!File.Exists(old))
                    continue;
                var d = JsonSerializer.Deserialize<ShopAutomationData>(File.ReadAllText(old), Json);
                if (d is null)
                    continue;
                d.Normalize();
                found.Add((File.GetLastWriteTimeUtc(old), d));
            }
            catch (Exception)
            {
                // испорченный старый файл пропускаем — он остаётся на месте
            }
        }
        if (found.Count == 0)
            return null;
        found.Sort((a, b) => b.At.CompareTo(a.At));
        var merged = found[0].Data;
        for (var i = 1; i < found.Count; i++)
        {
            var other = found[i].Data;
            foreach (var kv in other.Suppliers)
                merged.Suppliers.TryAdd(kv.Key, kv.Value);
            foreach (var kv in other.Products)
                merged.Products.TryAdd(kv.Key, kv.Value);
            foreach (var kv in other.CategoryTargetMarkup)
                merged.CategoryTargetMarkup.TryAdd(kv.Key, kv.Value);
            foreach (var r in other.ReserveSuppliers.Where(r => !merged.ReserveSuppliers.Any(m => string.Equals(m.Name, r.Name, StringComparison.OrdinalIgnoreCase) && m.Phone == r.Phone)).ToList())
                merged.ReserveSuppliers.Add(r);
            if (other.ExpiryMode == ExpiryModes.Block || (other.ExpiryMode == ExpiryModes.Warn && merged.ExpiryMode == ExpiryModes.Off))
                merged.ExpiryMode = other.ExpiryMode;
            if (!merged.Alerts.Enabled && other.Alerts.Enabled)
                merged.Alerts = other.Alerts;
        }
        merged.Normalize();
        try
        {
            Directory.CreateDirectory(Folder);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(merged, Json));
            PosLogger.Log($"Автоматизация магазина: настройки собраны в общий файл из {found.Count} старых.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автоматизация магазина: общий файл не записан ({ex.GetType().Name}).", "WARNING");
        }
        return merged;
    }
}
