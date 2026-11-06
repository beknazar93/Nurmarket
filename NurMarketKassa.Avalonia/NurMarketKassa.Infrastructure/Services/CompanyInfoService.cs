namespace NurMarketKassa.Services;

using System.Globalization;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

/// <summary>Статус оплаты подписки NurCRM для текущей компании.</summary>
public sealed record SubscriptionStatus(bool IsExpired, bool IsNearExpiry, int DaysRemaining, DateTimeOffset EndDate);

/// <summary>Данные компании для чека (GET /api/users/company/).</summary>
public static class CompanyInfoService
{
    /// <summary>Последний загруженный ответ /api/users/company/ — читает раздел "Аккаунт"
    /// в Настройках (тариф, срок действия, подключённые доп. услуги), без отдельного похода
    /// на сервер каждый раз, когда кассир открывает этот экран.</summary>
    public static CompanyDto? LastCompany { get; private set; }

    /// <summary>2026-11-03 (С-03): когда <see cref="LastCompany"/> получена с сервера (UTC). Свежую (&lt; 60 с) вторую загрузку на запуске не повторяем.</summary>
    public static DateTime LastLoadedAtUtc { get; private set; }

    /// <summary>Сработало, когда загруженная компания отличается от прошлой. На это
    /// подписано разделение локальных данных (AccountDataIsolation): раньше оно вызывалось
    /// из трёх мест входа, и на пути смены кассира срабатывало не всегда — компания успевала
    /// обновиться раньше, чем код сравнивал её со старой, и подмена набора пропускалась.
    /// Здесь же событие поднимается ровно в тот момент, когда компания реально сменилась,
    /// каким бы путём вход ни произошёл.</summary>
    public static event Action<string?>? CompanyChanged;

    /// <summary>
    /// Возвращает null, если статус подписки не удалось определить (нет связи с сервером,
    /// офлайн-режим, поле не пришло от API) — в этом случае вызывающий код НЕ должен
    /// блокировать работу кассы: недоступность проверки не равна неоплате.
    /// </summary>
    /// <param name="startup">2026-11-03 (С-03, аудит «зависание при медленном сервере»): вызов с запуска кассы/программы владельца (MainWindow, OwnerShell). Тогда
    /// (1) компания, загруженная окном входа меньше минуты назад, повторно не грузится (раньше 2×55 с); (2) если в этом ПК уже есть данные известной компании
    /// (LastDataAccountKey) и сервер не ответил за 8 с — открываемся с кэшем подписки, а компания дозагружается фоном (при другой компании сработает CompanyChanged →
    /// AccountDataIsolation, как всегда). Без известной компании (первый вход на ПК) ждём сервер как раньше, до 55 с: открывать кассу с данными чужого аккаунта нельзя.</param>
    public static async Task<SubscriptionStatus?> RefreshAsync(IAuthApiService authApi, CancellationToken ct = default, bool startup = false)
    {
        // Сеет парсер весовых штрих-кодов последними сохранёнными (или дефолтными) настройками
        // сразу, даже до сетевого запроса — офлайн-старт не должен оставлять парсер незаданным.
        SyncWeightBarcodeParserSettings();

        if (string.IsNullOrEmpty(authApi.AccessToken))
            return null;

        // 2026-10-04, стенд «сбои сервера»: сервер не отвечает (ServerOutageMonitor) — срок подписки по
        // сохранённой дате, без запроса. Раньше запуск кассы в аварии ждал здесь таймаута HttpClient
        // (55 с при «чёрной дыре») — после входа кассир ещё минуту смотрел на заставку.
        if (ServerOutageMonitor.IsOutage)
            return GetCachedSubscriptionStatus();

        if (startup && LastCompany is not null && DateTime.UtcNow - LastLoadedAtUtc < TimeSpan.FromSeconds(60))
        {
            PosLogger.Log("Компания загружена меньше минуты назад (окно входа) — повторно с сервера не берём.", "API");
            return ComputeSubscriptionStatus(LastCompany);
        }

        try
        {
            CompanyDto? company;
            if (startup && !string.IsNullOrEmpty(UserPreferences.Instance.LastDataAccountKey))
            {
                try
                {
                    company = await ServerAnswerWait.ForegroundAsync("company", t => authApi.GetCompanyAsync(t), ct).ConfigureAwait(false);
                }
                catch (ServerNotAnsweringException)
                {
                    PosLogger.Log("Сервер не отдал данные компании за 8 с — открываемся с сохранённой подпиской, компания дозагрузится фоном.", "API");
                    _ = RefreshInBackgroundAsync(authApi);
                    return GetCachedSubscriptionStatus();
                }
            }
            else
                company = await authApi.GetCompanyAsync(ct).ConfigureAwait(false);

            return ApplyLoadedCompany(company, authApi);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось загрузить данные компании: {ex.Message}", "API");
            // Нет связи с сервером — считаем срок подписки от последней сохранённой даты
            // окончания (UserPreferences.SubscriptionEndDateRaw) и системных часов, а не
            // молча пропускаем проверку: касса должна доотсчитать дни до истечения и офлайн.
            return GetCachedSubscriptionStatus();
        }
    }

    /// <summary>2026-11-03 (С-03): компания не пришла за срок запуска — догружаем фоном (обычный срок HttpClient) и применяем так же, как при обычной загрузке.</summary>
    private static async Task RefreshInBackgroundAsync(IAuthApiService authApi)
    {
        try
        {
            var company = await authApi.GetCompanyAsync(CancellationToken.None).ConfigureAwait(false);
            // 2026-11-03 (ревью Н-5): фоновая догрузка пришла минуту спустя; если компания сервера не та, чьи данные лежат на ПК (LastDataAccountKey), применение вызвало бы
            // AccountDataIsolation.SwitchTo из потока пула посреди работы кассира (стирание файлов и переоткрытие базы). Данные на ходу не меняем — смена случится при следующем запуске/входе.
            var knownKey = UserPreferences.Instance.LastDataAccountKey ?? "";
            if (company is not null && knownKey.Length > 0 && !string.Equals(knownKey, AppPaths.CompanyKey(company.Id), StringComparison.OrdinalIgnoreCase)) // 2026-11-05: ключ с префиксом стенда
            {
                PosLogger.Log($"Компания с сервера ({company.Id}) отличается от данных на этом ПК ({knownKey}) — фоновая догрузка не применена, чтобы не менять данные посреди работы. Перезапустите кассу.", "WARNING");
                return;
            }
            ApplyLoadedCompany(company, authApi);
            PosLogger.Log("Компания дозагружена фоном после медленного ответа сервера.", "API");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Компания не дозагружена фоном: {ex.Message}", "API");
        }
    }

    private static SubscriptionStatus? ApplyLoadedCompany(CompanyDto? company, IAuthApiService authApi)
    {
        {
            LastLoadedAtUtc = DateTime.UtcNow;
            var previousId = LastCompany?.Id;
            var previousFeatures = string.Join(",", LastCompany?.Features ?? new List<string>());
            LastCompany = company;
            // 2026-10-05: функции компании на сервере изменились (подключили на другом устройстве) — меню пересобирается.
            if (previousFeatures != string.Join(",", company?.Features ?? new List<string>()))
                TariffGate.RaisePacksChanged();
            if (!string.Equals(previousId, company?.Id, StringComparison.Ordinal))
                RaiseCompanyChanged(company?.Id);
            ApplyCompanyToPreferences(company);
            // 2026-10-05, запрос NurCRM: бонусы на сервере — процент из настройки магазина, разовая выгрузка, очередь.
            _ = ServerLoyalty.AfterCompanyRefreshAsync(company?.Id);
            // 2026-10-31, ТЗ Умара: платные функции, включённые компании на сервере (GET users/company/addons/), — после входа и раз в 15 минут.
            ServerAddons.AfterCompanyRefresh();
            // 2026-10-05: настройки аккаунта с сервера (SettingsCloudSync) — один раз на вход в компанию.
            _ = SettingsCloudSync.PullOnceAsync(company?.Id);
            // 2026-10-29: и затем общие настройки компании подтягиваются на лету (раз в ~45 с, ETag) — без перезапуска кассы (Н-1).
            SettingsCloudSync.StartCompanyPolling();
            // 2026-10-29: и пульс чужих продаж (один лёгкий запрос раз в ~20 с) — «Сводка», «Продажи», остатки не ждут минуту-другую.
            RemoteSalesPulse.Start();
            // 2026-10-18: общие настройки «Оплата долга» (облако кассы, и у владельца тоже) — сразу и затем раз в 2 минуты.
            _ = DebtPaymentSettings.RefreshAsync();
            DebtPaymentSettings.StartAutoRefresh();
            // 2026-09-28: вид магазина, заданный на сервере, — всем кассам компании (BE-18).
            // null с сервера локальный выбор не трогает (см. MarketSphereSync).
            try
            {
                MarketSphereSync.ApplyServerValueIfNew();
            }
            catch (Exception sphereEx)
            {
                PosLogger.Log($"Вид магазина с сервера не применён: {sphereEx.Message}", "WARNING");
            }
            // 2026-11-03 (С-03): настройки весов — фоном, вход их не ждёт (раньше второй запрос до 55 с держал заставку).
            _ = RefreshScaleSettingsAsync(authApi, CancellationToken.None);
            return ComputeSubscriptionStatus(company);
        }
    }

    private static void RaiseCompanyChanged(string? companyId)
    {
        try
        {
            CompanyChanged?.Invoke(companyId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обработчик смены компании упал: {ex}", "ERROR");
        }
    }

    /// <summary>Статус подписки без похода на сервер — по последней известной дате окончания
    /// (сначала берётся из свежего <see cref="LastCompany"/>, иначе из того, что сохранено в
    /// UserPreferences с прошлого успешного подключения). Используется и фоновым монитором
    /// во время работы кассы, и как офлайн-фолбэк в <see cref="RefreshAsync"/>.</summary>
    public static SubscriptionStatus? GetCachedSubscriptionStatus()
    {
        var endDateRaw = LastCompany?.EndDate;
        if (string.IsNullOrWhiteSpace(endDateRaw))
            endDateRaw = UserPreferences.Instance.SubscriptionEndDateRaw;
        return ComputeSubscriptionStatusFromRaw(endDateRaw);
    }

    /// <summary>Настройки формата весового штрих-кода компании (scale_barcode_layout/mode) —
    /// отдельный эндпоинт, ошибка здесь не должна ронять весь RefreshAsync (касса продолжит
    /// работать с последними сохранёнными или дефолтными настройками).</summary>
    private static async Task RefreshScaleSettingsAsync(IAuthApiService authApi, CancellationToken ct)
    {
        try
        {
            var (layout, mode, amountUnit) = await authApi.GetScaleSettingsAsync(ct).ConfigureAwait(false);
            ApplyServerScaleSettings(layout, mode, amountUnit);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось загрузить настройки весов: {ex.Message}", "API");
        }
    }

    /// <summary>2026-11-05: применяет настройки весов с сервера. Серверная единица суммы пишется в ScaleBarcodeAmountUnit,
    /// но ЛОКАЛЬНАЯ (ScaleBarcodeAmountUnitLocal, выбор на кассе) её не затирается и остаётся важнее
    /// (WeightBarcodeParser.AmountUnit = локальная ?? серверная).</summary>
    internal static void ApplyServerScaleSettings(string? layout, string? mode, string? amountUnit)
    {
        var prefs = UserPreferences.Instance;
        var changed = false;
        if (!string.IsNullOrWhiteSpace(layout) && prefs.ScaleBarcodeLayout != layout)
        {
            prefs.ScaleBarcodeLayout = layout;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(mode) && prefs.ScaleBarcodeMode != mode)
        {
            prefs.ScaleBarcodeMode = mode;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(amountUnit) && prefs.ScaleBarcodeAmountUnit != amountUnit)
        {
            prefs.ScaleBarcodeAmountUnit = amountUnit;
            changed = true;
        }
        if (changed)
        {
            prefs.SaveToDisk();
            PosLogger.Log(
                $"Scale barcode settings: layout={prefs.ScaleBarcodeLayout}, mode={prefs.ScaleBarcodeMode}, amountUnit={prefs.ScaleBarcodeAmountUnit}, amountUnitLocal={prefs.ScaleBarcodeAmountUnitLocal ?? "-"}",
                "API");
        }
        SyncWeightBarcodeParserSettings();
    }

    /// <summary>Прокидывает текущие (сохранённые или дефолтные) scale_barcode_layout/mode
    /// в статические Layout/Mode парсера — Core не может сам читать UserPreferences
    /// (Infrastructure ссылается на Core, а не наоборот).</summary>
    private static void SyncWeightBarcodeParserSettings()
    {
        var prefs = UserPreferences.Instance;
        NurMarketKassa.Core.Application.WeightBarcodeParser.Layout = prefs.ScaleBarcodeLayout;
        NurMarketKassa.Core.Application.WeightBarcodeParser.Mode = prefs.ScaleBarcodeMode;
        NurMarketKassa.Core.Application.WeightBarcodeParser.AmountUnit = prefs.EffectiveScaleAmountUnit; // 2026-11-05: локальная ?? серверная
        NurMarketKassa.Core.Application.WeightBarcodeParser.MaxLabelAmountSom = prefs.ScaleMaxLabelAmountSom > 0 ? prefs.ScaleMaxLabelAmountSom : 20000;
        NurMarketKassa.Core.Application.WeightBarcodeParser.AmountPrefixes = UserPreferences.ParseAmountPrefixes(prefs.ScaleAmountPrefixes);
        // 2026-09-28: и обратное правило «в штрихкоде вес» (мастер «Настроить по этикетке»).
        NurMarketKassa.Core.Application.WeightBarcodeParser.WeightPrefixes = UserPreferences.ParseAmountPrefixes(prefs.ScaleWeightPrefixes);
        // 2026-11-06: раскладка этикетки весов Штрих РС-200 (если включена в Настройки → Весы) — для её префикса важнее режима компании.
        NurMarketKassa.Services.Hardware.ShtrikhPcSettings.ApplyToParser();
    }

    private static SubscriptionStatus? ComputeSubscriptionStatus(CompanyDto? company) =>
        ComputeSubscriptionStatusFromRaw(company?.EndDate);

    private static SubscriptionStatus? ComputeSubscriptionStatusFromRaw(string? endDateRaw)
    {
        if (string.IsNullOrWhiteSpace(endDateRaw))
            return null;
        if (!DateTimeOffset.TryParse(endDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
            return null;

        var remaining = endDate - DateTimeOffset.Now;
        var daysRemaining = (int)Math.Ceiling(remaining.TotalDays);
        return new SubscriptionStatus(
            IsExpired: remaining <= TimeSpan.Zero,
            IsNearExpiry: remaining > TimeSpan.Zero && remaining.TotalDays <= 3,
            DaysRemaining: daysRemaining,
            EndDate: endDate);
    }

    public static void RestoreFromOfflineSession()
    {
        var session = OfflineAuthSessionStore.TryLoad();
        if (session == null)
            return;

        var prefs = UserPreferences.Instance;
        if (!string.IsNullOrWhiteSpace(session.StoreInn))
            prefs.StoreInn = session.StoreInn.Trim();
        else if (!string.IsNullOrWhiteSpace(session.CompanyInn))
            prefs.StoreInn = session.CompanyInn.Trim();

        if (string.IsNullOrWhiteSpace(prefs.StoreAddress) && !string.IsNullOrWhiteSpace(session.CompanyAddress))
            prefs.StoreAddress = session.CompanyAddress.Trim();
    }

    public static void Clear()
    {
        // ИНН привязан к авторизованной компании; очищаем только при выходе из сессии API.
        UserPreferences.Instance.StoreInn = string.Empty;
    }

    private static void ApplyCompanyToPreferences(CompanyDto? company)
    {
        var prefs = UserPreferences.Instance;
        if (company == null)
        {
            prefs.StoreInn = string.Empty;
            prefs.SaveToDisk();
            OfflineAuthSessionStore.UpdateCompanyData(prefs.StoreInn, prefs.StoreAddress);
            return;
        }

        prefs.StoreInn = company.Inn ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prefs.StoreAddress))
            prefs.StoreAddress = company.Address ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(company.EndDate))
            prefs.SubscriptionEndDateRaw = company.EndDate;

        prefs.SaveToDisk();
        OfflineAuthSessionStore.UpdateCompanyData(prefs.StoreInn, company.Address);
    }
}
