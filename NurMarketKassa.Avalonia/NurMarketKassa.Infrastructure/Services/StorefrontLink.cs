using System.Text.RegularExpressions;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (отчёт «Конкуренты», п. 3, «QR витрины на чеке и ценнике»): адрес витрины магазина https://market.nurcrm.kg/catalog/{slug}.
/// Slug (адрес витрины) приходит с сервера вместе с настройками компании (GET api/users/settings/company/, поле slug) — касса запоминает его
/// вместе с компанией, которой он принадлежит, чтобы после смены входа чужой адрес не печатался. Нет адреса или витрина не подключена — null,
/// и QR на чеке/ценнике не рисуется (скрыт, а не пустой).</summary>
public static class StorefrontLink
{
    /// <summary>Правило сайта NurCRM: латиница, цифры, дефис, 3–50 символов.</summary>
    private static readonly Regex SlugRule = new("^[a-zA-Z0-9-]{3,50}$", RegexOptions.Compiled);

    /// <summary>Пример адреса для предпросмотра в редакторе ценников (реального адреса у магазина может ещё не быть).</summary>
    public const string SampleUrl = "https://market.nurcrm.kg/catalog/my-shop";

    public static bool IsValidSlug(string? slug) => !string.IsNullOrWhiteSpace(slug) && SlugRule.IsMatch(slug.Trim());

    /// <summary>Запомнить slug компании. Пустая строка с сервера («витрины нет») стирает прежний адрес этой компании; мусор игнорируется.</summary>
    public static void Remember(string? slug)
    {
        var prefs = UserPreferences.Instance;
        var company = CompanyInfoService.KnownCompanyId ?? "";
        var value = (slug ?? "").Trim();
        if (value.Length > 0 && !IsValidSlug(value))
            return;
        var current = prefs.StorefrontSlug ?? "";
        if (current == value && (prefs.StorefrontSlugCompany ?? "") == company)
            return;
        // Нет ни адреса, ни прежнего адреса — писать на диск нечего.
        if (value.Length == 0 && current.Length == 0)
            return;
        prefs.StorefrontSlug = value.Length == 0 ? null : value;
        prefs.StorefrontSlugCompany = value.Length == 0 ? null : company;
        prefs.SaveToDisk();
    }

    /// <summary>Slug из ответа сервера (объект или обёртка «data»); нет поля slug — ничего не меняем.</summary>
    public static void RememberFromJson(JsonElement root)
    {
        var data = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            data = inner;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String)
            Remember(s.GetString());
    }

    /// <summary>Адрес витрины этой компании или null (slug не получен / принадлежит другой компании). Подключена ли витрина по тарифу, не смотрит — см. <see cref="PrintableUrl"/>.</summary>
    public static string? Url
    {
        get
        {
            var prefs = UserPreferences.Instance;
            if (!IsValidSlug(prefs.StorefrontSlug))
                return null;
            if ((prefs.StorefrontSlugCompany ?? "") != (CompanyInfoService.KnownCompanyId ?? ""))
                return null;
            var url = ShowcaseApiService.CatalogUrl(prefs.StorefrontSlug!);
            return ReceiptShareLink.IsPrintable(url) ? url : null;
        }
    }

    /// <summary>Адрес для печати на чеке и ценнике: есть адрес и витрина подключена (на «Старте» витрина — платная услуга). null — QR скрыт.</summary>
    public static string? PrintableUrl() => ShowcaseApiService.IsShowcaseConnected ? Url : null;

    /// <summary>Подпись под QR витрины.</summary>
    public static string Caption => Tr.T("Заказать онлайн", "Онлайн буйрутма берүү", "Order online", "Online sipariş", "Onlayn buyurtma");
}
