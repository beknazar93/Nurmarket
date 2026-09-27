using System;

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

    /// <summary>Раздел «Клиенты» — на сайте скрыт для «Старт» в секторе «Магазин».</summary>
    public static bool CanViewClients => !IsStartTariff;

    public static string ClientsLockedMessage =>
        Tr.T("Раздел «Клиенты» доступен на тарифе «Стандарт» и выше.",
             "«Кардарлар» бөлүмү «Стандарт» тарифинен баштап жеткиликтүү.", "The “Customers” section is available on the “Standard” plan and above.", "«Müşteriler» bölümü «Standart» ve üzeri tarifelerde kullanılabilir.", "«Mijozlar» bo'limi «Standart» va undan yuqori tarifda mavjud.");

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
