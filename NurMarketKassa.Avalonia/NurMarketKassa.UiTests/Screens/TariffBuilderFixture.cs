using System.Text;
using System.Text.Json;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>Модуль из ЖИВОГО ответа GET https://app.nurcrm.kg/api/public/modules/ (снято 26.11.2026, без входа, только чтение): id, русское название, группа, цена в месяц, профили, где он входит бесплатно.</summary>
public sealed record LiveModule(string Id, string Name, string Group, int Monthly, string[] DefaultIn);

/// <summary>2026-11-26: общие данные для проверок «модули Маркетплейса и тариф «Своё»» — список модулей сервера ФИКСИРОВАН (константа из живого ответа; при новом модуле сервера его добавляют сюда и в
/// MarketplaceModuleContent.Lines — иначе тест красный), реестр компании и цены модулей для статических PublicModules / CompanyModules, каталог пакетов тарифов и заглушка ITariffApi.</summary>
public static class TariffBuilderFixture
{
    private static readonly string[] Retail = ["bazaar_retail", "bazaar_wholesale"];

    /// <summary>Все 29 модулей сервера на 26.11.2026.</summary>
    public static readonly LiveModule[] Live =
    [
        new("scales_labels", "Весы с этикетками", "grocery", 200, ["grocery"]),
        new("expiry_batches", "Сроки годности и партии", "grocery", 200, ["grocery"]),
        new("quick_items", "Быстрые товары", "grocery", 100, ["grocery", "stationery"]),
        new("fractional_qty", "Дробное количество (шаг, минимум)", "hozmag", 100, ["hozmag", "stroymag"]),
        new("packages", "Упаковки", "hozmag", 100, ["hozmag", "stroymag", "stationery"]),
        new("client_price", "Цена по клиенту", "hozmag", 100, ["hozmag", "stroymag", "stationery"]),
        new("price_levels", "Уровни цен («прораб»)", "stroymag", 200, ["stroymag"]),
        new("estimate", "Смета", "stroymag", 300, ["stroymag"]),
        new("material_calc", "Калькулятор материалов", "stroymag", 200, ["stroymag"]),
        new("rental", "Прокат", "stroymag", 300, ["stroymag"]),
        new("variants", "Размеры и цвета", "clothing", 200, ["clothing"]),
        new("consultant", "Консультант", "clothing", 100, ["clothing"]),
        new("exchange", "Обмен", "clothing", 100, ["clothing"]),
        new("serials", "Серийные номера", "electronics", 200, ["electronics"]),
        new("warranty", "Гарантия", "electronics", 200, ["electronics", "autoparts"]),
        new("repair", "Ремонт", "electronics", 300, ["electronics"]),
        new("trade_in", "Trade-in", "electronics", 300, ["electronics"]),
        new("part_numbers", "Каталожный номер и OEM", "autoparts", 200, ["autoparts"]),
        new("cross_analogs", "Кроссы и аналоги", "autoparts", 200, ["autoparts"]),
        new("condition", "Состояние (новое / б/у)", "autoparts", 100, ["autoparts"]),
        new("storage_cell", "Ячейка хранения", "autoparts", 100, ["autoparts"]),
        new("sto_price", "Цена СТО", "autoparts", 100, ["autoparts"]),
        new("return_rules", "Правила возврата (электрика, установленные)", "autoparts", 100, ["autoparts"]),
        new("customer_orders", "Заказ под клиента", "autoparts", 200, ["autoparts"]),
        new("category_tiles", "Быстрые плитки категорий", "bazaar", 100, Retail),
        new("volume_price", "Цена по количеству", "bazaar", 100, Retail),
        new("haggle", "Торг", "bazaar", 100, Retail),
        new("currencies", "Курсы валют", "bazaar", 200, Retail),
        new("invoice_cargo", "Накладная и карго (опт)", "bazaar", 300, ["bazaar_wholesale"]),
    ];

    public static string[] LiveIds => Live.Select(m => m.Id).ToArray();

    /// <summary>Модули, включённые у «продуктового» магазина по умолчанию (профиль grocery).</summary>
    public static readonly string[] GroceryDefaults = ["scales_labels", "expiry_batches", "quick_items"];

    private static string Arr(IEnumerable<string> items) => "[" + string.Join(",", items.Select(i => "\"" + i + "\"")) + "]";

    /// <summary>Реестр модулей компании (GET users/company/modules/) для профиля grocery: включены модули профиля и <paramref name="extraEnabled"/> (добавил владелец).</summary>
    public static string CompanyJson(IEnumerable<string> extraEnabled)
    {
        var extra = extraEnabled.ToHashSet();
        var enabled = GroceryDefaults.Concat(extra).ToList();
        var items = string.Join(",", Live.Select(m =>
            $"{{\"id\":\"{m.Id}\",\"enabled\":{(enabled.Contains(m.Id) ? "true" : "false")},\"source\":\"{(GroceryDefaults.Contains(m.Id) ? "default" : extra.Contains(m.Id) ? "added" : "off")}\"}}"));
        var catalog = string.Join(",", Live.Select(m =>
            $"{{\"id\":\"{m.Id}\",\"name\":\"{m.Name}\",\"names\":{{\"ru\":\"{m.Name}\"}},\"group\":\"{m.Group}\",\"default_in\":{Arr(m.DefaultIn)}}}"));
        return "{\"profile\":\"grocery\",\"profile_base\":\"grocery\",\"version\":\"1\",\"modules\":" + Arr(enabled) + ",\"items\":[" + items + "],\"catalog\":[" + catalog + "],"
               + "\"profiles\":[{\"id\":\"grocery\",\"name\":\"Продуктовый\",\"names\":{\"ru\":\"Продуктовый\"},\"base\":\"grocery\",\"modules\":" + Arr(GroceryDefaults) + ",\"flags\":[]}]}";
    }

    /// <summary>GET public/modules/ как у сервера (цены строкой «200.00»).</summary>
    public static string PricesJson() =>
        "{\"currency\":\"KGS\",\"modules\":[" + string.Join(",", Live.Select(m =>
            $"{{\"id\":\"{m.Id}\",\"name\":{{\"ru\":\"{m.Name}\"}},\"group\":\"{m.Group}\",\"monthly\":\"{m.Monthly}.00\",\"once\":\"0.00\",\"trial_days\":7,\"default_in\":{Arr(m.DefaultIn)}}}")) + "]}";

    /// <summary>Подложить реестр компании и цены модулей (статические CompanyModules / PublicModules) на время проверки; Dispose возвращает как было. Профиль grocery, extraEnabled — доп. включённые модули.</summary>
    public static IDisposable Apply(string dir, params string[] extraEnabled)
    {
        Directory.CreateDirectory(dir);
        CompanyModules.DirOverride = dir;
        var prefs = UserPreferences.Instance;
        var restore = (prefs.LastDataAccountKey, prefs.MarketSphere, prefs.MarketSphereDetail, prefs.LastKnownPlanName);
        prefs.LastDataAccountKey = "c:ui-tariff-builder";
        prefs.MarketSphere = "grocery";
        prefs.MarketSphereDetail = null;
        CompanyModules.ResetForTests();
        using (var doc = JsonDocument.Parse(CompanyJson(extraEnabled)))
            CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "grocery");
        using (var prices = JsonDocument.Parse(PricesJson()))
            PublicModules.SetForTests(PublicModules.Parse(prices.RootElement, out _));
        TariffApi.SetCatalogForTests(Catalog());
        return new Restore(() =>
        {
            CompanyModules.ResetForTests();
            CompanyModules.DirOverride = null;
            PublicModules.SetForTests(null);
            TariffApi.SetCatalogForTests(null);
            prefs.LastDataAccountKey = restore.LastDataAccountKey;
            prefs.MarketSphere = restore.MarketSphere;
            prefs.MarketSphereDetail = restore.MarketSphereDetail;
            prefs.LastKnownPlanName = restore.LastKnownPlanName;
            TariffDraft.ResetForTests();
            try { Directory.Delete(dir, true); } catch { /* временная папка */ }
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static readonly string[] PackCodes = ["ai", "clients", "sales_analytics", "restock", "salary", "debts", "service", "scales", "telegram_bot", "shift_analytics", "analytics_export"];

    public static string PackName(string code) => code switch
    {
        "ai" => "ИИ-помощник",
        "clients" => "Клиенты и бонусы",
        "sales_analytics" => "Аналитика продаж",
        "restock" => "Подсказки по закупкам",
        "salary" => "Зарплата",
        "debts" => "Долги",
        "service" => "Сервис",
        "scales" => "Весы",
        "telegram_bot" => "Telegram-бот",
        "shift_analytics" => "Итоги смены",
        _ => "Выгрузка аналитики",
    };

    /// <summary>Каталог тарифов как у сервера: Старт 1300, Стандарт 3600 (все пакеты входят), Своё; 11 пакетов с описанием. <paramref name="includedInStandard"/> = false — пакеты никуда не входят.</summary>
    public static TariffCatalog Catalog(bool includedInStandard = true) =>
        new([new TariffPlan("start", "Старт", 1300m, null, 13000m), new TariffPlan("standard", "Стандарт", 3600m, null, 36000m), new TariffPlan("custom", "Своё", null, "start")],
            PackCodes.Select(c => new TariffPack(c, PackName(c), "Что делает пакет: одна строка с сервера", 1500m, 400m, includedInStandard ? ["standard"] : [])).ToList());
}
