using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>2026-10-18, владелец: общие настройки «Оплата долга» — одинаковые для всех касс и программы владельца.
/// Значения по умолчанию — поведение до этой настройки: касса принимает только наличные, у владельца разрешено всё,
/// код сотрудника не нужен.</summary>
public sealed class DebtPaymentOptions
{
    /// <summary>Касса: наличные (в ящик, в смену кассира).</summary>
    public bool KassaCash { get; set; } = true;
    /// <summary>Касса: безнал (карта, перевод) — в смене кассира безналом, не в ящике.</summary>
    public bool KassaTransfer { get; set; }
    /// <summary>Программа владельца: наличные «в руки владельцу» — без смены, касса не меняется.</summary>
    public bool OwnerCash { get; set; } = true;
    /// <summary>Программа владельца: безнал — тоже без смены.</summary>
    public bool OwnerTransfer { get; set; } = true;
    /// <summary>Сотрудник (не владелец и не администратор) вводит свой код на приём оплаты (EmployeeAccessGate.DebtPayment).</summary>
    public bool RequireEmployeeCode { get; set; }

    /// <summary>В каждом месте разрешён хотя бы один способ (оба выключены — наличные, как раньше).</summary>
    public DebtPaymentOptions Normalized()
    {
        var c = (DebtPaymentOptions)MemberwiseClone();
        if (!c.KassaCash && !c.KassaTransfer)
            c.KassaCash = true;
        if (!c.OwnerCash && !c.OwnerTransfer)
            c.OwnerCash = true;
        return c;
    }

    public bool SameAs(DebtPaymentOptions other) =>
        KassaCash == other.KassaCash && KassaTransfer == other.KassaTransfer && OwnerCash == other.OwnerCash
        && OwnerTransfer == other.OwnerTransfer && RequireEmployeeCode == other.RequireEmployeeCode;
}

/// <summary>2026-10-18, владелец: «синхронизация полностью с кассой и настройки должны быть». Общие настройки «Оплата
/// долга» лежат в ОБЩИХ настройках компании программы КАССЫ — api/main/app-settings/nurmarket-kassa/, поле
/// <see cref="CloudKey"/> — и в кассе, и в программе владельца (у SettingsCloudSync у владельца своё облако
/// nurmarket-owner, поэтому этот ключ туда не входит и ходит отдельно, здесь). Читают все; меняют владелец и
/// администратор (кассир получает 403).
///
/// Правила: значения на этом ПК — последние полученные с сервера для текущей компании (нет на сервере — значения по
/// умолчанию). Касса перечитывает их раз в 2 минуты и при открытии окна оплаты долга (лёгкий GET; сбой сети — молча,
/// остаются прежние). Запись — только целиком после успешного чтения в этом запуске; локально значение меняется
/// только после ответа сервера «сохранено».</summary>
public static class DebtPaymentSettings
{
    public const string CloudKey = "DebtPaymentOptions";
    private const string CloudPath = "api/main/app-settings/nurmarket-kassa/";
    public const string MethodCash = "cash";
    /// <summary>Безнал — как способ «Безнал» в окне оплаты продажи (CheckoutViewModel: "transfer").</summary>
    public const string MethodTransfer = "transfer";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(2);
    private static DateTime _lastOkUtc = DateTime.MinValue;
    private static string? _loadedFor;
    private static int _autoStarted;
    /// <summary>2026-10-18, ревью: номер успешной записи — ответ GET, начатого до записи, не откатывает новые значения.</summary>
    private static int _writeGen;

    /// <summary>Значения изменились (пришли с сервера или сохранены) — окна обновляют выбор способа.</summary>
    public static event Action? Changed;

    /// <summary>Действующие значения для текущей компании.</summary>
    public static DebtPaymentOptions Current
    {
        get
        {
            var prefs = UserPreferences.Instance;
            var saved = prefs.DebtPaymentOptions;
            if (saved is null)
                return new DebtPaymentOptions();
            // Значения другой компании на этом ПК (вход в другой магазин) не действуют.
            var company = CompanyInfoService.LastCompany?.Id;
            if (!string.IsNullOrWhiteSpace(company) && !string.Equals(prefs.DebtPaymentOptionsCompanyId, company, StringComparison.Ordinal))
                return new DebtPaymentOptions();
            return saved.Normalized();
        }
    }

    /// <summary>2026-10-18, ревью безопасности: значения для текущей компании известны — получены в этом запуске или раньше
    /// (сохранены с отметкой этой компании). Нет — касса не считает «код не нужен» (см. DebtPaymentFlow.ConfirmEmployeeCodeAsync).</summary>
    public static bool KnownForCurrentCompany
    {
        get
        {
            var company = CompanyInfoService.LastCompany?.Id;
            if (string.IsNullOrWhiteSpace(company))
                return false;
            var prefs = UserPreferences.Instance;
            return LoadedFromServer
                   || (prefs.DebtPaymentOptions is not null && string.Equals(prefs.DebtPaymentOptionsCompanyId, company, StringComparison.Ordinal));
        }
    }

    /// <summary>2026-10-18, третий круг ревью: получены с сервера в этом запуске не позже 2 минут назад (можно не перечитывать).</summary>
    public static bool IsFresh => LoadedFromServer && DateTime.UtcNow - _lastOkUtc <= AutoInterval;

    /// <summary>Получены с сервера в этом запуске для текущей компании (запись разрешена только после этого).</summary>
    public static bool LoadedFromServer =>
        _loadedFor is { } id && string.Equals(id, CompanyInfoService.LastCompany?.Id, StringComparison.Ordinal);

    /// <summary>Способы оплаты долга здесь (касса или программа владельца): "cash" / "transfer", хотя бы один.</summary>
    public static IReadOnlyList<string> MethodsHere()
    {
        var o = Current;
        var list = new List<string>();
        if (AppMode.IsOwner ? o.OwnerCash : o.KassaCash)
            list.Add(MethodCash);
        if (AppMode.IsOwner ? o.OwnerTransfer : o.KassaTransfer)
            list.Add(MethodTransfer);
        return list;
    }

    /// <summary>Перечитать с сервера. force = false — не чаще раза в 30 с. true — значения получены (или свежие).
    /// Ошибки сети и сервера — молча (false), остаются прежние значения.</summary>
    public static async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && LoadedFromServer && DateTime.UtcNow - _lastOkUtc < MinInterval)
            return true;
        if (ServerOutageMonitor.IsOutage)
            return false;
        var api = SettingsCloudSync.ApiProvider?.Invoke();
        var company = CompanyInfoService.LastCompany?.Id;
        if (api is null || string.IsNullOrEmpty(api.AccessToken) || string.IsNullOrWhiteSpace(company))
            return false;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали очередь, другой вызов уже получил свежие значения.
            if (LoadedFromServer && DateTime.UtcNow - _lastOkUtc < TimeSpan.FromSeconds(3))
                return true;
            var writeGen = Volatile.Read(ref _writeGen);
            var data = await api.RequestAsync(HttpMethod.Get, CloudPath, null, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            // Пока шёл запрос, сменилась компания — ответ относится к прежней.
            if (!string.Equals(CompanyInfoService.LastCompany?.Id, company, StringComparison.Ordinal))
                return false;
            // Пока шёл запрос, значения сохранили — ответ старше их (запись и чтение идут по одному через Gate, это подстраховка).
            if (writeGen != Volatile.Read(ref _writeGen))
                return true;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings)
                || settings.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return false; // ответ без поля settings — сбой, а не «пусто»
            DebtPaymentOptions remote = new();
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(CloudKey, out var node) && node.ValueKind == JsonValueKind.Object)
                remote = JsonSerializer.Deserialize<DebtPaymentOptions>(node.GetRawText(), Json) ?? new DebtPaymentOptions();
            // 2026-10-18, четвёртый круг ревью: UserPreferences и SaveToDisk — на UI-потоке, как остальные их изменения.
            await NurMarketKassa.Ui.Shared.UiDispatcherHolder.InvokeAsync(() => ApplyLocal(remote.Normalized(), company!, "с сервера")).ConfigureAwait(false);
            _loadedFor = company;
            _lastOkUtc = DateTime.UtcNow;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            // Тихо: настройка не критична, остаются прежние значения; следующий опрос — через 2 минуты.
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Опрос раз в 2 минуты (касса и программа владельца). Запускается один раз — после входа в компанию.</summary>
    public static void StartAutoRefresh()
    {
        if (Interlocked.Exchange(ref _autoStarted, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(AutoInterval);
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                try
                {
                    await RefreshAsync(force: true).ConfigureAwait(false);
                }
                catch
                {
                    // опрос не должен падать
                }
            }
        });
    }

    /// <summary>2026-10-21 (ревью денег, A1): NoMethod — после изменения в кассе или у владельца не остаётся ни одного способа.</summary>
    public enum SaveOutcome { Saved, Forbidden, Offline, Failed, NoMethod }

    /// <summary>Сохранить на сервере. Локально значения меняются только после ответа «сохранено»; кассир —
    /// <see cref="SaveOutcome.Forbidden"/> (403), значения остаются прежними.
    /// 2026-10-21 (ревью денег, A1): раньше уходил весь набор переключателей с экрана — если значения сервера при открытии
    /// не пришли (на экране умолчания) или другой администратор поменял настройку меньше 2 минут назад, сохранение одного
    /// переключателя выключало «Нужен код сотрудника» на всех кассах. Теперь: всегда свежее чтение с сервера, и поверх
    /// него — только то, что пользователь реально изменил (<paramref name="change"/>).</summary>
    public static async Task<(SaveOutcome Outcome, string? Error)> SaveAsync(Action<DebtPaymentOptions> change, CancellationToken ct = default)
    {
        var api = SettingsCloudSync.ApiProvider?.Invoke();
        var company = CompanyInfoService.LastCompany?.Id;
        if (api is null || string.IsNullOrEmpty(api.AccessToken) || string.IsNullOrWhiteSpace(company) || ServerOutageMonitor.IsOutage)
            return (SaveOutcome.Offline, null);
        // Как П1 у SettingsCloudSync: без свежих значений сервера не записываем вслепую (2026-10-21, A1: читаем каждый раз).
        if (!await RefreshAsync(force: true, ct).ConfigureAwait(false) || !LoadedFromServer)
            return (SaveOutcome.Offline, null);
        // 2026-10-18, ревью: запись — под тем же Gate, что и чтение: GET, начатый раньше, закончится ДО записи и не откатит
        // новые значения; GET после записи ждёт её конца.
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 2026-10-21 (ревью денег, A1): Current — только что полученные значения сервера (новый объект), меняем в нём
            // только нажатый переключатель.
            var fresh = Current;
            var options = Current;
            change(options);
            if ((!options.KassaCash && !options.KassaTransfer) || (!options.OwnerCash && !options.OwnerTransfer))
                return (SaveOutcome.NoMethod, null);
            options = options.Normalized();
            if (options.SameAs(fresh))
                return (SaveOutcome.Saved, null); // на сервере уже так — отправлять нечего
            var body = new JsonObject { [CloudKey] = JsonSerializer.SerializeToNode(options) };
            await api.RequestAsync(new HttpMethod("PATCH"), CloudPath, body, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Interlocked.Increment(ref _writeGen);
            if (string.Equals(CompanyInfoService.LastCompany?.Id, company, StringComparison.Ordinal))
            {
                await NurMarketKassa.Ui.Shared.UiDispatcherHolder.InvokeAsync(() => ApplyLocal(options, company!, "сохранены")).ConfigureAwait(false);
                _loadedFor = company;
                _lastOkUtc = DateTime.UtcNow;
            }
            return (SaveOutcome.Saved, null);
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            PosLogger.Log("Оплата долга: общие настройки — нет прав на запись (меняют владелец и администратор).", "INFO");
            return (SaveOutcome.Forbidden, null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: общие настройки не сохранены ({ex.Message}).", "WARNING");
            return (SaveOutcome.Failed, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void ApplyLocal(DebtPaymentOptions options, string company, string why)
    {
        var prefs = UserPreferences.Instance;
        var before = prefs.DebtPaymentOptions;
        if (before is not null && before.Normalized().SameAs(options) && prefs.DebtPaymentOptionsCompanyId == company)
            return;
        prefs.DebtPaymentOptions = options;
        prefs.DebtPaymentOptionsCompanyId = company;
        prefs.SaveToDisk();
        PosLogger.Log($"Оплата долга: общие настройки {why} — касса: {Describe(options.KassaCash, options.KassaTransfer)}; " +
                      $"владелец: {Describe(options.OwnerCash, options.OwnerTransfer)}; код сотрудника: {(options.RequireEmployeeCode ? "нужен" : "не нужен")}.", "INFO");
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: окна не обновлены после смены настроек ({ex.Message}).", "WARNING");
        }
    }

    private static string Describe(bool cash, bool transfer) =>
        string.Join(", ", new[] { cash ? "наличные" : null, transfer ? "безнал" : null }.Where(x => x != null));
}
