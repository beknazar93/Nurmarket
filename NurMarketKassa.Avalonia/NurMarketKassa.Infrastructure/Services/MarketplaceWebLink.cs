using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (просьба владельца «Маркетплейс должен открываться с нашего сайта»): адрес страницы Маркетплейса в веб-админке (front-market).
/// Адрес по умолчанию — раздел «Маркетплейс» на market.nurcrm.kg (настройка UserPreferences.MarketplaceWebUrl). Токен, пароль и другие секреты в адрес НЕ передаются:
/// вход на сайте делает сам клиент. Допустим только https (http — лишь для localhost при проверке); адрес с логином/паролем (user:pass@host) отвергается.</summary>
public static class MarketplaceWebLink
{
    public const string DefaultUrl = "https://market.nurcrm.kg/crm/marketplace";

    private static readonly Regex SafeItem = new("^[a-zA-Z0-9:_.-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Готовый адрес для браузера или null, если настроенный адрес негоден. <paramref name="item"/> — код функции/темы (kassa-loyalty, theme-sunny): сайт откроет её карточку.</summary>
    public static string? Build(string? configured, string? item = null)
    {
        var raw = string.IsNullOrWhiteSpace(configured) ? DefaultUrl : configured.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var local = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp))
            return null;
        if (string.IsNullOrWhiteSpace(item) || !SafeItem.IsMatch(item))
            return uri.AbsoluteUri;
        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        builder.Query = (query.Length > 0 ? query + "&" : "") + "item=" + Uri.EscapeDataString(item);
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>Адрес по настройке программы.</summary>
    public static string? ForSettings(string? item = null) => Build(UserPreferences.Instance.MarketplaceWebUrl, item);

    /// <summary>Страница темы на сайте: адрес от сервера (web_url), если он https и на домене nurcrm.kg; иначе общий Маркетплейс сайта с параметром item.</summary>
    public static string? ForTheme(MarketplaceThemeEntry entry, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(entry.WebUrl) && Uri.TryCreate(entry.WebUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(u.UserInfo) && (u.Host.Equals("nurcrm.kg", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nurcrm.kg", StringComparison.OrdinalIgnoreCase)
                                                    || u.Host.EndsWith("front-market.vercel.app", StringComparison.OrdinalIgnoreCase)))
            return u.AbsoluteUri;
        return Build(configured ?? UserPreferences.Instance.MarketplaceWebUrl, entry.Code);
    }
}
