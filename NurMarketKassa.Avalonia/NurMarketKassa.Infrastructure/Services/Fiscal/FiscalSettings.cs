using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): режим фискализации ЭТОЙ компании — из ответа GET api/main/capabilities/ (блок <c>fiscal</c> + признак <c>fiscal_kassa</c>).
/// Разбор вызывает <see cref="ServerCapabilities"/> после каждого успешного запроса признаков (один запрос на все признаки, свой не нужен).
/// Состояние привязано к аккаунту и компании (как признаки сервера): после смены аккаунта чужой режим не показывается. Последний известный блок лежит на диске
/// (fiscal-settings.json в %LOCALAPPDATA%\NurMarketKassa, данные компании — CompanyStoreRegistry): без связи при запуске режим не «выключается».
/// Правила: режим server — касса НЕ пробивает; off — как сегодня (ничего не показывается); kassa и fiscal_enabled — касса должна пробивать (когда будет провайдер).
/// Эта служба ничего в сеть не шлёт и в оплату не вмешивается.</summary>
public static class FiscalSettings
{
    public const string FileName = "fiscal-settings.json";

    private static readonly object Gate = new();
    private static (string Account, FiscalCapabilities Value)? _memory;
    private static string? _diskTriedFor;

    /// <summary>Только для тестов: папка файла (по умолчанию AppPaths.KassaLocal).</summary>
    internal static string? DirOverride { get; set; }

    /// <summary>Только для тестов: ключ аккаунта вместо настоящего.</summary>
    internal static string? AccountOverride { get; set; }

    private static string CurrentAccount() => AccountOverride ?? ServerCapabilities.AccountKey();

    private static string FilePath => Path.Combine(DirOverride ?? AppPaths.KassaLocal, FileName);

    /// <summary>Режим этого аккаунта (память, иначе последний известный с диска, иначе «выключено»).</summary>
    public static FiscalCapabilities Current
    {
        get
        {
            var account = CurrentAccount();
            lock (Gate)
            {
                if (_memory is { } m && m.Account == account)
                    return m.Value;
                if (_diskTriedFor != account)
                {
                    _diskTriedFor = account;
                    if (TryLoadFromDisk(account) is { } fromDisk)
                    {
                        _memory = (account, fromDisk);
                        return fromDisk;
                    }
                }
                return FiscalCapabilities.Off;
            }
        }
    }

    /// <summary>Показывать фискальные поля и надписи (режим не «выключено», сервер знает fiscal_kassa).</summary>
    public static bool ShowsFiscalUi => Current.ShowsFiscalUi;

    /// <summary>Касса должна сама пробивать чек: mode=kassa, fiscal_enabled, признак fiscal_kassa. При mode=server и mode=off — false (касса не пробивает).</summary>
    public static bool KassaMustFiscalize => Current.KassaMustFiscalize;

    /// <summary>Пробивает сервер (mode=server): касса реквизиты не пишет — сервер ответит 409 fiscal_mode_server.</summary>
    public static bool ServerFiscalizes => Current.ServerFiscalizes;

    /// <summary>Ставки НДС компании для списка в карточке товара (пусто — не заданы или режим off).</summary>
    public static IReadOnlyList<decimal> VatRates => Current.ShowsFiscalUi ? Current.Rates : Array.Empty<decimal>();

    /// <summary>Вызывает ServerCapabilities после успешного разбора ответа capabilities. Никогда не бросает.</summary>
    internal static void Apply(JsonElement root, IReadOnlySet<string> features, string account)
    {
        try
        {
            Set(account, FiscalCapabilities.Parse(root, features));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Фискализация: блок fiscal в capabilities не разобран ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>Подставить режим для текущего аккаунта (после сохранения настроек владельцем).</summary>
    internal static void SetCurrent(FiscalCapabilities value) => Set(CurrentAccount(), value);

    /// <summary>Подставить режим для аккаунта (ответ GET settings/fiscal/ после сохранения владельцем и тесты). Пишет на диск.</summary>
    internal static void Set(string account, FiscalCapabilities value)
    {
        bool changed;
        lock (Gate)
        {
            changed = _memory is not { } m || m.Account != account || !Same(m.Value, value);
            _memory = (account, value);
            _diskTriedFor = account;
            if (changed)
                SaveToDisk(account, value);
        }
        if (!changed)
            return;
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Фискализация: обработчик смены режима не отработал ({ex.GetType().Name}).", "WARNING"); }
    }

    /// <summary>Режим фискализации изменился (ответ capabilities, сохранение настроек): меню программы владельца показывает или прячет раздел «Продажи без фискального чека».</summary>
    public static event Action? Changed;

    /// <summary>Только для тестов: забыть память (файл остаётся) — как перезапуск кассы.</summary>
    internal static void ResetMemoryForTests()
    {
        lock (Gate)
        {
            _memory = null;
            _diskTriedFor = null;
        }
    }

    private static bool Same(FiscalCapabilities a, FiscalCapabilities b) =>
        a.FeatureAvailable == b.FeatureAvailable && a.Mode == b.Mode && a.FiscalEnabled == b.FiscalEnabled && a.TaxRegime == b.TaxRegime
        && a.VatPayer == b.VatPayer && a.NspRate == b.NspRate && a.Rates.SequenceEqual(b.Rates) && a.Kkm == b.Kkm;

    private static FiscalCapabilities? TryLoadFromDisk(string account)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return null;
            var text = AtomicFile.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("account", out var acc) || acc.GetString() != account
                || !root.TryGetProperty("fiscal", out var block))
                return null;
            var available = root.TryGetProperty("feature", out var f) && f.ValueKind == JsonValueKind.True;
            return FiscalCapabilities.ParseBlock(block, available);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            PosLogger.Log($"Фискализация: файл режима не прочитан ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    private static void SaveToDisk(string account, FiscalCapabilities value)
    {
        if (account.Trim('|').Length == 0)
            return; // аккаунт неизвестен — не пишем (как ServerCapabilities)
        try
        {
            var block = new Dictionary<string, object?>
            {
                ["mode"] = FiscalCapabilities.ModeToApi(value.Mode),
                ["fiscal_enabled"] = value.FiscalEnabled,
                ["tax_regime"] = value.TaxRegime,
                ["vat_payer"] = value.VatPayer,
                ["vat_rates"] = value.Rates.Select(r => r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                ["nsp_rate"] = value.NspRate?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (value.Kkm is { HasData: true } kkm) // 2026-11-20: сведения о ККМ с сервера — последний известный блок (имена те же, что разбирает FiscalKkmInfo.Parse)
                block["kkm"] = kkm.ToBlock();
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["account"] = account,
                ["saved_at"] = DateTime.UtcNow.ToString("O"),
                ["feature"] = value.FeatureAvailable,
                ["fiscal"] = block,
            });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            PosLogger.Log($"Фискализация: режим не записан на диск ({ex.GetType().Name}).", "WARNING");
        }
    }
}
