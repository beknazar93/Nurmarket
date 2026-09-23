namespace NurMarketKassa.Services;

using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

/// <summary>Данные компании для чека (GET /api/users/company/).</summary>
public static class CompanyInfoService
{
    public static async Task RefreshAsync(IAuthApiService authApi, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(authApi.AccessToken))
            return;

        try
        {
            var company = await authApi.GetCompanyAsync(ct).ConfigureAwait(false);
            ApplyCompanyToPreferences(company);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось загрузить данные компании: {ex.Message}", "API");
        }
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

        prefs.SaveToDisk();
        OfflineAuthSessionStore.UpdateCompanyData(prefs.StoreInn, company.Address);
    }
}
