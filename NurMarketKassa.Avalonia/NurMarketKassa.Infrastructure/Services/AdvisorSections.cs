using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27, владелец: «Дай Нур-советнику доступы ко всем новым функциям и разделам, чтобы он мог открывать, изучать и получать данные».
/// Раздел программы владельца глазами советника: id (тот же, что у словаря переходов <see cref="AdvisorNavRouter"/>), ключ пункта меню
/// OwnerShellWindow, названия на 5 языках, что делает (для ИИ), право (PosPermissions), тумблер советника (AdvisorCapabilities), какие данные
/// можно получить (<see cref="DataArea"/>) и слово для поиска в Маркетплейсе (раздел не подключён — предложить функцию).</summary>
public sealed record AdvisorSection(string Id, string MenuKey, IReadOnlyList<string> Names, string About, string? Permission, string? Capability,
    string? DataArea, string? MarketplaceHint, bool Openable)
{
    public string Title => Tr.T(Names[0], Names[1], Names[2], Names[3], Names[4]);
    public string TitleRu => Names[0];
}

/// <summary>2026-11-27: единый реестр разделов для инструментов Нур-советника. Строится из словаря переходов (те же id, названия и вкладки,
/// что открывает советник) + таблица «что делает / право / данные» ниже + разделы только с данными (акции, тариф). Видимость «сейчас» (тариф,
/// признак сервера, «Разделы меню», права) берётся у построенного меню программы владельца (AdvisorToolHost.InMenu), а не дублируется здесь.
/// ПРАВИЛО: новый пункт меню (Add("key", …) в OwnerShellWindow.BuildNavigation) = запись в <see cref="Info"/> и в словаре AdvisorNavRouter;
/// без неё тест AdvisorSectionsTests красный.</summary>
public static class AdvisorSections
{
    private sealed record Meta(string About, string? Permission, string? Capability, string? Data, string? Market = null);

    /// <summary>Пункты меню, которые советник намеренно НЕ открывает: «Выйти на рабочий стол» закрыл бы программу.</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["exit"] = "закрывает программу — это действие, а не раздел",
    };

    private const string Products = PosPermissions.ViewProducts, Analytics = PosPermissions.ViewAnalytics, Clients = PosPermissions.ViewClients,
        SettingsRight = PosPermissions.ViewSettings, Sales = PosPermissions.ViewSales;

    // Описания — для ИИ (он сам переводит на язык владельца); названия разделов на 5 языках — в словаре AdvisorNavRouter.
    private static readonly Dictionary<string, Meta> Info = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = new("Главная страница: выручка, чеки, средний чек, прибыль, способы оплаты, последние продажи и лучшие товары за выбранный период.", null, "read.sales", "summary"),
        ["aiadvisor"] = new("Нур Советник — чат и разговор голосом с ИИ о магазине.", Analytics, null, null),
        ["warehouse"] = new("Склад: товары, остатки, цены, категории, поиск и карточка товара.", Products, "read.stock", "warehouse"),
        ["warehouse.receiving"] = new("Приёмка товара карточками: количество, цена закупки, срок годности и партия.", Products, "read.stock", "purchases"),
        ["warehouse.revision"] = new("Ревизия (инвентаризация): пересчёт остатков и расхождения.", Products, "read.stock", "warehouse"),
        ["warehouse.writeoff"] = new("Списание товара (брак, просрочка, потери).", Products, "read.stock", "warehouse"),
        ["warehouse.movements"] = new("Перемещение товара между складом и филиалами.", Products, "read.stock", "warehouse"),
        ["warehouse.barcodes"] = new("Ценники и штрихкоды: печать, редактор и шаблоны этикеток.", Products, null, null),
        ["warehouse.bulk"] = new("Массовое добавление товаров из файла Excel.", Products, null, null),
        ["warehouse.low"] = new("Склад с фильтром «Заканчивается» — товары с низким остатком.", Products, "read.stock", "warehouse"),
        ["warehouse.out"] = new("Склад с фильтром «Нет в наличии» — нулевой остаток.", Products, "read.stock", "warehouse"),
        ["warehouse.expiry"] = new("Склад с фильтром «Срок годности» — просроченные и скоро истекающие.", Products, "read.stock", "expiry"),
        ["calculator"] = new("Калькуляция: расчёт цены продажи по закупке и наценке.", Analytics, null, null),
        ["estimate"] = new("Смета для строймаркета: подбор материалов и сумма; в программе владельца видны себестоимость и маржа.", Products, null, null, "Смета"),
        ["restock"] = new("Пополнение и сроки: что заказать (по продажам и остаткам) и что скоро просрочится.", Products, "read.stock", "expiry", "Пополнение"),
        ["purchases"] = new("Закупки: история приходов (номер, поставщик, сумма, ЭСФ), заказы поставщикам, приём товара.", Products, "read.stock", "purchases"),
        ["purchases.orders"] = new("Заказы поставщикам: черновики и отправленные заказы.", Products, "read.stock", "purchases"),
        ["purchases.receive"] = new("Приём товара от поставщика с оплатой и долгом поставщику.", Products, "read.stock", "purchases"),
        ["suppliers"] = new("Поставщики: список, наш долг поставщику, сделки и карточка поставщика.", Products, "read.stock", "suppliers"),
        ["supplierreturns"] = new("Возвраты поставщикам: брак и излишки, зачёт в долг.", Products, "read.stock", "suppliers"),
        ["branches"] = new("Филиалы и перемещения товара между ними.", Products, null, null),
        ["salesfinance"] = new("Продажи и финансы: чеки всех касс, возвраты, история, смены и Z-отчёты.", Sales, "read.sales", "sales"),
        ["salesfinance.returns"] = new("Возвраты покупателей по чекам.", Sales, "read.sales", "sales"),
        ["salesfinance.history"] = new("История продаж по дням.", Sales, "read.sales", "sales"),
        ["salesfinance.shifts"] = new("Кассовые смены и Z-отчёты: кто работал, выручка смены, наличные в кассе.", Sales, "read.staff", "shifts"),
        ["salereturn"] = new("Окно «Оформить возврат» — возврат товара по чеку (сам возврат подтверждает пользователь).", PosPermissions.EmployeeReturn, null, null),
        ["partsorders"] = new("Заказы под клиента (автозапчасти): список и статусы; продажу из заказа оформляют в кассе.", Products, null, null, "Автозапчаст"),
        ["debts"] = new("Долги клиентов: кто должен, сколько, сроки и погашения.", Clients, "read.debts", "debts", "Долг"),
        ["rentals"] = new("Прокат: что выдано, когда вернуть, просрочки.", Clients, null, null, "Прокат"),
        ["repair"] = new("Ремонт и сервис: квитанции, статусы (принят, в работе, готов, выдан), сроки и предоплаты.", Clients, null, "repair", "Ремонт"),
        ["tradein"] = new("Trade-in / скупка: журнал принятых у покупателей вещей.", Clients, null, null, "Trade-in"),
        ["analytics"] = new("Аналитика: выручка и оплаты, товары, сезонность, склад, залежавшиеся товары и предложения акций.", Analytics, "read.analytics", "analytics", "Аналитик"),
        ["abc"] = new("ABC-анализ: какие товары приносят больше всего денег (группы A, B, C).", Analytics, "read.analytics", "abc"),
        ["profitcash"] = new("Прибыль и деньги: прибыль (P&L), движение денег, сверка отчётов.", Analytics, "read.analytics", "summary"),
        ["investments"] = new("Инвестиции и окупаемость: вложения в магазин, инвесторы, выплаты, срок окупаемости.", SettingsRight, null, null),
        ["losssales"] = new("Продажи в убыток: чеки, где цена со скидкой ниже закупки.", Analytics, "read.analytics", "analytics"),
        ["bonusanalytics"] = new("Бонусы: начисленные и списанные баллы программы лояльности.", Analytics, null, null, "лояльност"),
        ["staffreport"] = new("Кассиры и часы: продажи по кассирам, по часам и дням недели, сравнение периодов.", Analytics, "read.staff", "staff"),
        ["fiscalreport"] = new("Продажи без фискального чека: сверка с ККМ.", Analytics, "read.sales", null),
        ["sizesreport"] = new("Размеры и цвета: продажи по размерам и цветам (сфера «Одежда»).", Analytics, "read.analytics", null),
        ["siteorders"] = new("Заказы с сайта и приложения: новые, в работе, выполненные.", PosPermissions.ViewOrders, null, null),
        ["siteeditor"] = new("Редактор сайта: тема, баннеры и публикация витрины.", SettingsRight, null, null),
        ["sitereviews"] = new("Отзывы покупателей на витрине (модерация).", SettingsRight, null, null),
        ["sitecollections"] = new("Коллекции товаров на витрине.", SettingsRight, null, null),
        ["sitesettings"] = new("Настройки сайта: адрес, контакты, доставка, оплата.", SettingsRight, null, null),
        ["appshop"] = new("Магазин в мобильном приложении: подключение витрины в приложении (бесплатно).", null, null, null),
        ["clients"] = new("Клиенты: база покупателей, покупки, долги, бонусы.", Clients, "read.debts", "clients"),
        ["telegrambot"] = new("Телеграм-бот: обращения покупателей, заказы, настройки функций бота.", Analytics, "read.bot", "bot", "Телеграм"),
        ["telegrambot.seller"] = new("Бот-продавец: ИИ отвечает покупателям в Телеграм и оформляет заказы; настройки тона, скидок и передачи человеку.", Analytics, "read.bot", "bot_seller", "Телеграм"),
        ["telegrambot.dialogs"] = new("Диалоги бота с покупателями: открытые, переданные человеку, закрытые.", Analytics, "read.bot", "bot_dialogs", "Телеграм"),
        ["telegrambot.sales"] = new("Продажи бота: заказы и выручка через бота за период.", Analytics, "read.bot", "bot_sales", "Телеграм"),
        ["telegrambot.bargain"] = new("Торг по товарам: правила торга бота (минимальная цена, шаг скидки) по каждому товару; симулятор торга открывается из списка товара.", Analytics, "read.bot", "bot_bargain", "Телеграм"),
        ["telegrambot.offers"] = new("Отчёт торга: журнал предложений покупателей (оферт) и решений бота.", Analytics, "read.bot", "bot_offers", "Телеграм"),
        ["whatsapp"] = new("WhatsApp Web внутри программы.", Clients, null, null),
        ["funnel"] = new("Воронка продаж: этапы работы с покупателями.", Clients, null, null),
        // 2026-11-27: «Акции» (конструктор акций v2, PromotionsWindow; виден при признаке сервера promotions_v2 владельцу/админу — проверяет меню).
        ["promotions"] = new("Акции: список акций (действуют, на паузе, черновики, завершены), отчёт по акциям за период и акции товаров из карточек (скидка % от суммы строки, предел штук).",
            null, "read.stock", "promotions"),
        ["mailing"] = new("Рассылка: сегменты клиентов (должники, давно не покупали, VIP, именинники) и сообщения в WhatsApp по одному; отправляет владелец сам.", Clients, null, "mailing"),
        ["salary"] = new("Зарплата: схемы оплаты, оклады, проценты от продаж, начисления.", SettingsRight, "read.staff", "staff", "Зарплат"),
        ["timesheet"] = new("Табель: отметки сотрудников по дням и итоги по сменам.", SettingsRight, "read.staff", "staff"),
        ["crm"] = new("Сайт NurCRM внутри программы.", null, null, null),
        ["subscription"] = new("Подписка: тариф, срок действия и оплата.", null, null, "tariff"),
        ["marketplace"] = new("Маркетплейс: функции, модули, темы и виды кассы — что включено, цены, описания и как включить.", SettingsRight, null, "marketplace"),
        ["transfer"] = new("Перенос данных: импорт товаров и клиентов из других программ.", PosPermissions.EditProducts, null, null),
        ["settings"] = new("Настройки программы: экран, печать, весы, операции, клавиши, аккаунт.", SettingsRight, null, "settings"),
        ["settings.screen"] = new("Настройки экрана: язык, тема, масштаб, вид меню, разделы меню.", SettingsRight, null, "settings"),
        ["settings.scales"] = new("Весы с этикетками: подключение и отправка товаров на весы.", SettingsRight, null, "settings"),
        ["settings.print"] = new("Печать: принтер этикеток, ценников и накладных.", SettingsRight, null, "settings"),
        ["settings.updates"] = new("Обновления программы.", SettingsRight, null, null),
        ["settings.operations"] = new("Операции: правила продажи, долги, возвраты, тумблеры Нур Советника, экспорт в 1С.", SettingsRight, null, "settings"),
        ["settings.customization"] = new("Кастомизация: темы и вид программы.", SettingsRight, null, "settings"),
        ["settings.account"] = new("Аккаунт: компания, тариф, срок подписки, устройство.", SettingsRight, null, "tariff"),
        ["settings.employees"] = new("Права доступа сотрудников (сами пароли и PIN советник не видит).", SettingsRight, "read.staff", "staff"),
        ["settings.keys"] = new("Горячие клавиши кассы.", SettingsRight, null, null),
        ["settings.onec"] = new("Экспорт в 1С: выгрузка продаж, приходов и товаров в файл для бухгалтерии (параметры выгрузки; файлы советник не создаёт).", SettingsRight, null, "settings"),
        ["kb"] = new("База знаний: инструкции по программе.", null, null, null),
        ["support"] = new("Техподдержка и удалённая помощь.", null, null, null),
        ["outbox"] = new("Неотправленное: чеки, приходы и операции, которые ещё не ушли на сервер.", null, null, null),
        ["logs"] = new("Журнал ошибок программы.", null, null, null),
    };

    /// <summary>Разделы только с данными (своего пункта меню нет): открываются через раздел-хозяин <see cref="AdvisorSection.MenuKey"/>.</summary>
    private static readonly AdvisorSection[] DataOnly =
    {
        new("tariff", "overview", new[] { "Тариф и подписка", "Тариф жана жазылуу", "Plan and subscription", "Tarife ve abonelik", "Tarif va obuna" },
            "Текущий тариф, срок подписки и какие пакеты (аналитика, клиенты, зарплата, ИИ…) доступны.", null, null, "tariff", null, false),
    };

    private static readonly Lazy<IReadOnlyList<AdvisorSection>> Built = new(Build);

    /// <summary>Все разделы реестра (пункты меню, вкладки, окна бота, разделы только с данными).</summary>
    public static IReadOnlyList<AdvisorSection> All => Built.Value;

    /// <summary>Ключи пунктов меню владельца, о которых знает реестр.</summary>
    public static IReadOnlySet<string> MenuKeys => All.Select(s => s.MenuKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Записи словаря переходов без описания в <see cref="Info"/> (для теста: должно быть пусто).</summary>
    public static IReadOnlyList<string> MissingInfo() =>
        AdvisorNavRouter.Targets.Select(t => t.Id).Where(id => !Info.ContainsKey(id)).ToList();

    private static IReadOnlyList<AdvisorSection> Build()
    {
        var list = new List<AdvisorSection>();
        foreach (var t in AdvisorNavRouter.Targets)
        {
            // Без описания — раздел всё равно открывается, но тест красный (MissingInfo).
            var meta = Info.TryGetValue(t.Id, out var m) ? m : Info.TryGetValue(t.Section, out var p) ? p : new Meta("", null, null, null);
            list.Add(new AdvisorSection(t.Id, t.Section, BuildNames(t), meta.About, meta.Permission, meta.Capability, meta.Data, meta.Market, true));
        }
        list.AddRange(DataOnly);
        return list;
    }

    private static string[] BuildNames(AdvisorNavRouter.NavTarget t)
    {
        if (t.Parent is null)
            return t.Names;
        var names = new string[5];
        for (var i = 0; i < 5; i++)
            names[i] = t.Parent.Names[i] + " → " + t.Names[i];
        return names;
    }

    /// <summary>Раздел по id реестра; иначе — по словам («акции», «диалоги бота») через разбор словаря переходов.</summary>
    public static AdvisorSection? Find(string? idOrWords)
    {
        if (string.IsNullOrWhiteSpace(idOrWords))
            return null;
        var key = idOrWords.Trim();
        if (All.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        // Разделы только с данными — по названию на любом языке.
        var folded = AdvisorNavRouter.Fold(key);
        // Основа слова: «акция» / «акции» / «тарифы» (падеж и число не мешают).
        var stem = folded.Length > 5 ? folded[..^1] : folded.Length >= 4 ? folded[..4] : "";
        if (stem.Length >= 4 && DataOnly.FirstOrDefault(s => s.Names.Any(n => AdvisorNavRouter.Fold(n).Contains(stem) || folded.Contains(AdvisorNavRouter.Fold(n)))) is { } dataOnly)
            return dataOnly;
        var route = AdvisorNavRouter.Resolve(key);
        return route.Kind == AdvisorNavRouter.RouteKind.Single && route.Target is { } target
            ? All.FirstOrDefault(s => s.Id == target.Id)
            : null;
    }

    /// <summary>Области данных, для которых есть инструмент «получить данные» (enum для объявления функции).</summary>
    public static IReadOnlyList<string> DataAreas => All.Where(s => s.DataArea is not null).Select(s => s.DataArea!).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
}
