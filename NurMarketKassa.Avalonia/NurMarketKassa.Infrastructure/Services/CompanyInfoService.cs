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
    public static async Task<SubscriptionStatus?> RefreshAsync(IAuthApiService authApi, CancellationToken ct = default)
    {
        // Сеет парсер весовых штрих-кодов последними сохранёнными (или дефолтными) настройками
        // сразу, даже до сетевого запроса — офлайн-старт не должен оставлять парсер незаданным.
        SyncWeightBarcodeParserSettings();

        if (string.IsNullOrEmpty(authApi.AccessToken))
            return null;

        try
        {
            var company = await authApi.GetCompanyAsync(ct).ConfigureAwait(false);
            var previousId = LastCompany?.Id;
            LastCompany = company;
            if (!string.Equals(previousId, company?.Id, StringComparison.Ordinal))
                RaiseCompanyChanged(company?.Id);
            ApplyCompanyToPreferences(company);
            await RefreshScaleSettingsAsync(authApi, ct).ConfigureAwait(false);
            return ComputeSubscriptionStatus(company);
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
                    $"Scale barcode settings: layout={prefs.ScaleBarcodeLayout}, mode={prefs.ScaleBarcodeMode}, amountUnit={prefs.ScaleBarcodeAmountUnit}",
                    "API");
            }
            SyncWeightBarcodeParserSettings();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось загрузить настройки весов: {ex.Message}", "API");
        }
    }

    /// <summary>Прокидывает текущие (сохранённые или дефолтные) scale_barcode_layout/mode
    /// в статические Layout/Mode парсера — Core не может сам читать UserPreferences
    /// (Infrastructure ссылается на Core, а не наоборот).</summary>
    private static void SyncWeightBarcodeParserSettings()
    {
        var prefs = UserPreferences.Instance;
        NurMarketKassa.Core.Application.WeightBarcodeParser.Layout = prefs.ScaleBarcodeLayout;
        NurMarketKassa.Core.Application.WeightBarcodeParser.Mode = prefs.ScaleBarcodeMode;
        NurMarketKassa.Core.Application.WeightBarcodeParser.AmountUnit = prefs.ScaleBarcodeAmountUnit;
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
