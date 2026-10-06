using System;
using System.Linq;

namespace NurMarketKassa.Services;

/// <summary>Тарифные ограничения кассы (2026-09-07).
///
/// Проверено на двух тестовых аккаунтах NurCRM ("Старт" и "Стандарт"): сервер отдаёт им ОДИНАКОВЫЕ
/// can_view_market_* флаги (все true) — через права тариф не различить, ограничения сайт делает сам
/// на фронте по имени плана (subscription_plan.name) и сектору. Правила из бандла сайта для сектора
/// "Магазин" на тарифе "Старт": скрыты «Закупки», «Поставщики», «Клиенты», «Обзор», «Отделы»,
/// «Филиалы», «Бронирование», лимит 3 сотрудника (включая владельца). Из этого в кассе есть только
/// раздел «Клиенты» (ClientsWindow) — его и ограничиваем; закупок/поставщиков в кассе нет,
/// лимит сотрудников контролирует сервер при их создании.
///
/// Пока компания не загружена (LastCompany == null, первые секунды после входа) ограничений нет —
/// меню всё равно строится позже, при первом открытии.</summary>
public static class TariffGate
{
    public const string StartPlanName = "Старт";

    public static string? CurrentPlanName => CompanyInfoService.LastCompany?.SubscriptionPlanName?.Trim();

    /// <summary>Имя тарифа, известное по прошлому успешному входу. Нужно на случай, когда
    /// компания ещё не загружена (старт без сети, протухший токен, 5xx): без него касса всю
    /// сессию работала бы как «Стандарт».</summary>
    private static string? CachedPlanName =>
        UserPreferences.Instance.LastKnownPlanName is { Length: > 0 } name ? name.Trim() : null;

    public static bool IsStartTariff
    {
        get
        {
            // 2026-09-23. Раньше здесь было простое сравнение с CurrentPlanName, и при
            // LastCompany == null тариф считался НЕ «Старт» — то есть вся платная часть
            // открывалась бесплатно на всю сессию при любом сбое связи. Проверка окончания
            // подписки рядом уже была написана правильно, с опорой на кешированное значение;
            // приводим тариф к тому же поведению.
            if (CurrentPlanName is { Length: > 0 } live)
            {
                UserPreferences.Instance.LastKnownPlanName = live;
                return string.Equals(live, StartPlanName, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(CachedPlanName, StartPlanName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>2026-10-05, владелец: «всё, что есть на тарифе «Стандарт», можно подключить и на «Старт», но абонплата за
    /// каждую услугу добавляется по 400 сом + подключение (активация) 1500 сом». Пакеты функций «Стандарта» — открываются на
    /// «Старте» серийным ключом в Маркетплейс → Доп. функции (LicenseKeys, slug пакета), список — UserPreferences.UnlockedPacks.</summary>
    public const int PackActivationFee = 1500, PackMonthlyFee = 400;

    public static class Packs
    {
        public const string Ai = "ai", Clients = "clients", SalesAnalytics = "salesanalytics", Restock = "restock",
            Salary = "salary", Debts = "debts", Service = "service";

        public static readonly string[] All = { Ai, Clients, SalesAnalytics, Restock, Salary, Debts, Service };
    }

    // 2026-10-05, ТЗ ч.13, п. 2 (сервер добавил 05.10): функция компании на сервере (features) открывает пакет на всех
    // устройствах — ключ на одном компьютере больше не «теряется» на другом.
    public static bool HasPack(string slug) =>
        !IsStartTariff || UserPreferences.Instance.UnlockedPacks.Contains(slug, StringComparer.OrdinalIgnoreCase)
        || (CompanyInfoService.LastCompany?.Features?.Contains(ServerFeatureCode(slug), StringComparer.OrdinalIgnoreCase) ?? false);

    /// <summary>Код функции на сервере для пакета: salesanalytics → sales_analytics, остальные — как есть.</summary>
    public static string ServerFeatureCode(string slug) => slug == Packs.SalesAnalytics ? "sales_analytics" : slug;

    /// <summary>Пакет подключён или отключён (ключ, истёк тестовый доступ) — меню пересобирается.</summary>
    public static event Action? PacksChanged;

    public static void RaisePacksChanged() => PacksChanged?.Invoke();

    public static string PackPriceLabel =>
        Tr.T($"🔒 Подключить — {PackActivationFee} сом + {PackMonthlyFee} сом/мес", $"🔒 Туташтыруу — {PackActivationFee} сом + {PackMonthlyFee} сом/ай",
             $"🔒 Connect — {PackActivationFee} som + {PackMonthlyFee} som/mo", $"🔒 Bağla — {PackActivationFee} som + {PackMonthlyFee} som/ay",
             $"🔒 Ulash — {PackActivationFee} so'm + {PackMonthlyFee} so'm/oy");

    /// <summary>Раздел «Клиенты» — на сайте скрыт для «Старт» в секторе «Магазин». 2026-10-05: подключается пакетом.</summary>
    public static bool CanViewClients => HasPack(Packs.Clients);

    /// <summary>Продажи, финансы, аналитика, ABC, прибыль и деньги, продажи в убыток, план продаж.</summary>
    public static bool CanUseSalesAnalytics => HasPack(Packs.SalesAnalytics);

    public static bool CanUseRestock => HasPack(Packs.Restock);

    public static bool CanUseSalary => HasPack(Packs.Salary);

    /// <summary>Оплата долгов и отложенные чеки на кассе.</summary>
    public static bool CanUseDebts => HasPack(Packs.Debts);

    /// <summary>NurCRM в программе, база знаний, тех. поддержка, журнал ошибок.</summary>
    public static bool CanUseService => HasPack(Packs.Service);

    public static string ClientsLockedMessage =>
        Tr.T("Раздел «Клиенты» входит в тариф «Стандарт». На «Старт» его можно подключить в Маркетплейс → Доп. функции.",
             "«Кардарлар» бөлүмү «Стандарт» тарифине кирет. «Старт» тарифинде аны Маркетплейс → Кошумча функциялар бөлүмүндө туташтырууга болот.",
             "The “Customers” section is included in the “Standard” plan. On “Start”, it can be connected in Marketplace → Extras.",
             "«Müşteriler» bölümü «Standart» tarifesine dahildir. «Start» tarifesinde Marketplace → Ek özellikler bölümünden bağlanabilir.",
             "«Mijozlar» bo'limi «Standart» tarifiga kiradi. «Start» tarifida uni Marketpleys → Qo'shimcha funksiyalar bo'limida ulash mumkin.");

    /// <summary>Отправка PLU на сетевые весы (Штрих-М/Rongta, см. ScaleSettingsView "Выгрузка
    /// весовых товаров...") — на тарифе «Старт» платная доп. услуга, как остальные карточки в
    /// Маркетплейс → Доп. функции (2026-09-21, по просьбе владельца). На «Стандарт» и выше —
    /// бесплатно всегда. Базового COM-довеса (живое взвешивание на кассе, ScaleEnabledCheck выше
    /// на той же вкладке) и дисплея цены покупателя это НЕ касается — они как были, так и остались
    /// бесплатными на любом тарифе.</summary>
    public static bool CanUseScales => !IsStartTariff || UserPreferences.Instance.ScalesUnlocked;

    public static string ScalesLockedMessage =>
        Tr.T("Отправка на весы по сети — платная доп. услуга на тарифе «Старт». Активируйте её в Маркетплейс → Доп. функции.",
             "Таразага тармак аркылуу жөнөтүү — «Старт» тарифинде акылуу кошумча кызмат. Аны Маркетплейс → Кошумча функциялар бөлүмүндө иштетиңиз.", "Sending to scales over the network is a paid add-on on the “Start” plan. Activate it in Marketplace → Extras.", "Tartılara ağ üzerinden gönderim, «Start» tarifesinde ücretli bir ek hizmettir. Marketplace → Ek özellikler bölümünden etkinleştirin.", "Tarmoq orqali taroziga yuborish — «Start» tarifida pullik qo'shimcha xizmat. Uni Marketpleys → Qo'shimcha funksiyalar bo'limida faollashtiring.");

    /// <summary>Телеграм-бот владельца (2026-09-22). Правило, заданное владельцем: НОВЫЕ функции
    /// входят в «Стандарт», а на «Старт» покупаются в Маркетплейсе. Базовая касса — продажи,
    /// чеки, склад, печать — остаётся бесплатной на любом тарифе.</summary>
    public static bool CanUseTelegramBot => !IsStartTariff || UserPreferences.Instance.TelegramBotUnlocked;

    public static string TelegramBotLockedMessage =>
        Tr.T("Телеграм-бот владельца входит в тариф «Стандарт». На «Старт» его можно активировать в Маркетплейс → Доп. функции.",
             "Ээсинин телеграм-боту «Стандарт» тарифине кирет. «Старт» тарифинде аны Маркетплейс → Кошумча функциялар бөлүмүндө иштетүүгө болот.", "The owner's Telegram bot is included in the “Standard” plan. On “Start”, it can be activated in Marketplace → Extras.", "İşletme sahibinin Telegram botu «Standart» tarifesine dahildir. «Start» tarifesinde Marketplace → Ek özellikler bölümünden etkinleştirilebilir.", "Egasining Telegram-boti «Standart» tarifiga kiradi. «Start» tarifida uni Marketpleys → Qo'shimcha funksiyalar bo'limida faollashtirish mumkin.");

    /// <summary>2026-10-05, владелец: «почему на тарифе Старт отображаются ИИ-чаты и телеграм-боты — строго соблюдай
    /// разделение тарифов». ИИ (советник в программе владельца, ИИ в Telegram-боте, голосовой ИИ, поиск в интернете
    /// и фото товаров через ИИ) — «Стандарт» и выше; на «Старте» — пакет «ИИ» из Маркетплейса.</summary>
    public static bool CanUseAi => HasPack(Packs.Ai);

    public static string AiLockedMessage =>
        Tr.T("ИИ-советник и ИИ в боте входят в тариф «Стандарт». На «Старт» их можно подключить в Маркетплейс → Доп. функции.",
             "ИИ-кеңешчи жана боттогу ИИ «Стандарт» тарифине кирет. «Старт» тарифинде аларды Маркетплейс → Кошумча функциялар бөлүмүндө туташтырууга болот.",
             "The AI advisor and the bot's AI are included in the “Standard” plan. On “Start”, they can be connected in Marketplace → Extras.",
             "Yapay zekâ danışmanı ve bottaki yapay zekâ «Standart» tarifesine dahildir. «Start» tarifesinde Marketplace → Ek özellikler bölümünden bağlanabilir.",
             "SI maslahatchi va botdagi SI «Standart» tarifiga kiradi. «Start» tarifida ularni Marketpleys → Qo'shimcha funksiyalar bo'limida ulash mumkin.");

    /// <summary>Расширенные итоги смены: возвраты, списания, расход, оплата долгов и скидки.</summary>
    public static bool CanUseShiftAnalytics => !IsStartTariff || UserPreferences.Instance.ShiftAnalyticsUnlocked;

    public static string ShiftAnalyticsLockedMessage =>
        Tr.T("Расширенные итоги смены входят в тариф «Стандарт». На «Старт» их можно активировать в Маркетплейс → Доп. функции.",
             "Сменанын кеңейтилген жыйынтыктары «Стандарт» тарифине кирет. «Старт» тарифинде аларды Маркетплейс → Кошумча функциялар бөлүмүндө иштетүүгө болот.", "Extended shift totals are included in the “Standard” plan. On “Start”, they can be activated in Marketplace → Extras.", "Genişletilmiş vardiya sonuçları «Standart» tarifesine dahildir. «Start» tarifesinde Marketplace → Ek özellikler bölümünden etkinleştirilebilir.", "Smenaning kengaytirilgan yakunlari «Standart» tarifiga kiradi. «Start» tarifida ularni Marketpleys → Qo'shimcha funksiyalar bo'limida faollashtirish mumkin.");

    /// <summary>Выгрузка аналитики продаж и склада в Excel и Word с графиками.</summary>
    public static bool CanUseAnalyticsExport => !IsStartTariff || UserPreferences.Instance.AnalyticsExportUnlocked;

    public static string AnalyticsExportLockedMessage =>
        Tr.T("Выгрузка аналитики в Excel и Word входит в тариф «Стандарт». На «Старт» её можно активировать в Маркетплейс → Доп. функции.",
             "Аналитиканы Excel жана Word'ко чыгаруу «Стандарт» тарифине кирет. «Старт» тарифинде аны Маркетплейс → Кошумча функциялар бөлүмүндө иштетүүгө болот.", "Exporting analytics to Excel and Word is included in the “Standard” plan. On “Start”, it can be activated in Marketplace → Extras.", "Analizlerin Excel ve Word'e aktarılması «Standart» tarifesine dahildir. «Start» tarifesinde Marketplace → Ek özellikler bölümünden etkinleştirilebilir.", "Analitikani Excel va Word'ga eksport qilish «Standart» tarifiga kiradi. «Start» tarifida uni Marketpleys → Qo'shimcha funksiyalar bo'limida faollashtirish mumkin.");
}
