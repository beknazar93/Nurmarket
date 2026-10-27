using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, по просьбе владельца («как в Play Market… в будущем туда будут выходить функции, темы и т.д.»): единый каталог
// Маркетплейса. Все элементы — функции, темы, виды кассы — описаны одной моделью MarketplaceItem; витрина и страница
// «подробно» (MarketplaceStoreView) рисуют только её. Откуда берутся элементы, решает IMarketplaceCatalogSource: сейчас —
// LocalMarketplaceCatalogSource (всё зашито в код, сети нет), позже можно подключить источник с сервера, не трогая
// витрину. Как добавить элемент: см. LocalMarketplaceCatalogSource.GetItems — один новый вызов в нужном разделе списка.

/// <summary>Вид элемента каталога. Список расширяемый: новый вид = новое значение + чип в витрине.</summary>
public enum MarketplaceKind { Feature, Theme, Layout, Service }

/// <summary>Состояние для плитки и страницы: Available — можно взять бесплатно/уже разблокировано, но не включено;
/// Installed — включено/установлено/выбрано; Paid — нужна активация за деньги; ComingSoon — ещё нет.</summary>
public enum MarketplaceStatus { Available, Installed, Paid, ComingSoon }

public enum MarketplacePricePeriod { OneTime, PerMonth }

/// <summary>Цена. Amount == null — платно, но сумма в коде не задана («Платно»). MonthlyFee — абонплата сверх разовой суммы.</summary>
public sealed record MarketplacePrice(decimal? Amount, MarketplacePricePeriod Period = MarketplacePricePeriod.OneTime,
    decimal? MonthlyFee = null, string Currency = "сом")
{
    private string CurrencyWord => Currency == "сом" ? Tr.T("сом", "сом", "som", "som", "so'm") : Currency;

    public string Label
    {
        get
        {
            if (Amount is null)
                return Tr.T("Платно", "Акылуу", "Paid", "Ücretli", "Pullik");
            var perMonth = Period == MarketplacePricePeriod.PerMonth
                ? Tr.T("/мес", "/ай", "/mo", "/ay", "/oy") : "";
            var text = $"{Amount:0.##} {CurrencyWord}{perMonth}";
            if (MonthlyFee is { } fee)
                text += $" + {fee:0.##} {CurrencyWord}" + Tr.T("/мес", "/ай", "/mo", "/ay", "/oy");
            return text;
        }
    }
}

/// <summary>Действие на странице: подпись и делегат (внутри — существующая логика активации/открытия/применения).</summary>
public sealed record MarketplaceAction(string Label, Action Run, bool IsEnabled = true);

/// <summary>Один элемент каталога. Все тексты уже на языке программы (Tr.T) — каталог собирается заново при смене языка/состояния.</summary>
public sealed class MarketplaceItem
{
    public required string Id { get; init; }
    public MarketplaceKind Kind { get; init; } = MarketplaceKind.Feature;
    public required string Title { get; init; }
    public string ShortDescription { get; init; } = "";
    public string LongDescription { get; init; } = "";
    /// <summary>Эмодзи на значке и цвет подложки (hex). Для тем и видов кассы вместо глифа — IconVisual.</summary>
    public string IconGlyph { get; init; } = "";
    public string IconColor { get; init; } = "#FFB208";
    /// <summary>null = бесплатно.</summary>
    public MarketplacePrice? Price { get; init; }
    public MarketplaceStatus Status { get; init; } = MarketplaceStatus.Available;
    /// <summary>Подпись состояния на плитке и странице («Включено», «Выбрана», «Входит в тариф»…); пусто — показать цену.</summary>
    public string StatusText { get; init; } = "";
    public string Version { get; init; } = "";
    public string Vendor { get; init; } = "NUR CRM";
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Пути ресурсов-скриншотов (avares://…); пусто — блока нет.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];
    /// <summary>Список «Что внутри / возможности».</summary>
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public string WhatsNew { get; init; } = "";
    /// <summary>Пояснение на странице (отдельным блоком под описанием).</summary>
    public string Note { get; init; } = "";
    public bool IsFeatured { get; init; }
    public bool IsNew { get; init; }
    /// <summary>Главное действие (Установить/Активировать/Открыть/Применить). null — кнопки нет.</summary>
    public MarketplaceAction? Primary { get; init; }
    public IReadOnlyList<MarketplaceAction> Secondary { get; init; } = [];
    public IReadOnlyList<string> RelatedIds { get; init; } = [];
    /// <summary>Рисует значок вместо глифа (размер стороны квадрата).</summary>
    public Func<double, Control>? IconVisual { get; init; }
    /// <summary>Крупный предпросмотр на странице (темы, виды кассы).</summary>
    public Func<Control>? HeroVisual { get; init; }
    /// <summary>Id готового блока управления внутри страницы (его даёт MarketplaceStoreView.InlineControlProvider).</summary>
    public string? InlineControlId { get; init; }

    // 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-0): поля для новой витрины и страницы «подробно». Заполняет MarketplaceContent.Enrich
    // (пакет П-1); set, а не init — чтобы отдельный файл контента дописывал их после сборки элемента.
    public MarketplaceGroup Group { get; set; }
    /// <summary>Ключ векторного значка (MarketplaceIcons); пусто — прежний IconGlyph.</summary>
    public string IconKey { get; set; } = "";
    /// <summary>Одна строка пользы для героя и обложки.</summary>
    public string Tagline { get; set; } = "";
    /// <summary>До 3 коротких чипов в шапке.</summary>
    public IReadOnlyList<string> KeyFacts { get; set; } = [];
    public IReadOnlyList<MarketplaceSlide> Slides { get; set; } = [];
    public MarketplaceDemo? Demo { get; set; }
    public IReadOnlyList<MarketplaceBenefit> Benefits { get; set; } = [];
    /// <summary>Шаги «Как включить».</summary>
    public IReadOnlyList<string> HowTo { get; set; } = [];
    /// <summary>«Для кого».</summary>
    public IReadOnlyList<string> Audience { get; set; } = [];
    /// <summary>Условия цены: «Разовая активация ключом», «Входит в тариф „Стандарт“» и т.п.</summary>
    public string PriceTerms { get; set; } = "";
    /// <summary>Папка медиа в Assets/marketplace (theme:nurmarket → theme_nurmarket).</summary>
    public string MediaFolder => Id.Replace(':', '_');

    public bool IsComingSoon => Status == MarketplaceStatus.ComingSoon;

    public string KindLabel => Kind switch
    {
        // 2026-10-27: темы сайта (sitetheme:*) — свой вид «Тема сайта», чтобы не путать с темой кассы.
        MarketplaceKind.Theme => Id.StartsWith("sitetheme:", StringComparison.Ordinal)
            ? Tr.T("Тема сайта", "Сайт темасы", "Website theme", "Site teması", "Sayt mavzusi")
            : Tr.T("Тема", "Тема", "Theme", "Tema", "Mavzu"),
        MarketplaceKind.Layout => Tr.T("Вид кассы", "Касса көрүнүшү", "Till layout", "Kasa görünümü", "Kassa ko'rinishi"),
        MarketplaceKind.Service => Tr.T("Услуга", "Кызмат", "Service", "Hizmet", "Xizmat"),
        _ => Tr.T("Функция", "Функция", "Feature", "Özellik", "Funksiya"),
    };

    /// <summary>Строка под плитку: состояние, а если его нет — цена/«Бесплатно»/«Скоро».</summary>
    public string TileStatusLine
    {
        get
        {
            if (IsComingSoon)
                return Tr.T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada");
            if (!string.IsNullOrEmpty(StatusText))
                return Status == MarketplaceStatus.Installed ? "✓ " + StatusText : StatusText;
            return Price is null ? Tr.T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul") : Price.Label;
        }
    }

    /// <summary>Поиск без регистра по названию, описаниям, меткам и поставщику.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var q = query.Trim();
        bool Has(string? s) => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        return Has(Title) || Has(ShortDescription) || Has(LongDescription) || Has(Vendor) || Has(KindLabel)
               || Tags.Any(Has) || Features.Any(Has);
    }
}

/// <summary>Откуда витрина берёт элементы. Сейчас — локальный список; позже — загрузка с сервера (реализация этого интерфейса).</summary>
public interface IMarketplaceCatalogSource
{
    IReadOnlyList<MarketplaceItem> GetItems();
}

/// <summary>Локальный каталог: функции, темы и виды кассы, которые уже есть в программе. Сетевых запросов нет.
/// Команды действий уходят в <c>run</c> (реализует MarketplaceView): "unlock:ID" — ввод ключа и существующая активация,
/// "open:ID" — открыть экран функции, "theme:ID", "layout:ID", "themeeditor", "customerdisplay".</summary>
public sealed class LocalMarketplaceCatalogSource : IMarketplaceCatalogSource
{
    private readonly Action<string> _run;

    public LocalMarketplaceCatalogSource(Action<string> run) => _run = run;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly string AppVersion = typeof(MarketplaceItem).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    // Метки (используются в поиске и на странице)
    private static string TagWarehouse => T("Склад", "Кампа", "Warehouse", "Depo", "Ombor");
    private static string TagPrint => T("Печать", "Басып чыгаруу", "Printing", "Yazdırma", "Chop etish");
    private static string TagAnalytics => T("Аналитика", "Аналитика", "Analytics", "Analiz", "Analitika");
    private static string TagStaff => T("Сотрудники", "Кызматкерлер", "Staff", "Personel", "Xodimlar");
    private static string TagSales => T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar");
    private static string TagOwner => T("Для владельца", "Ээси үчүн", "For the owner", "Sahibi için", "Egasi uchun");
    private static string TagLook => T("Оформление", "Жасалгалоо", "Appearance", "Görünüm", "Ko'rinish");

    // ------------------------------------------------------------------ функции
    public IReadOnlyList<MarketplaceItem> GetItems()
    {
        var items = new List<MarketplaceItem>();
        var prefs = UserPreferences.Instance;
        var start = TariffGate.IsStartTariff;
        var packPrice = new MarketplacePrice(TariffGate.PackActivationFee, MarketplacePricePeriod.OneTime, TariffGate.PackMonthlyFee);

        // 2026-10-27: карточка «Голосовое управление» убрана — есть ИИ-ассистент.

        // --- Программа лояльности (1500 сом; после активации работает сама)
        items.Add(Gated("loyalty", "🎁", "#DB2777",
            T("Программа лояльности", "Лоялдуулук программасы", "Loyalty program", "Sadakat programı", "Sodiqlik dasturi"),
            T("Бонусные баллы за покупки", "Сатып алуулар үчүн бонус упайлар", "Bonus points for purchases", "Alışveriş için bonus puanlar", "Xaridlar uchun bonus ballar"),
            T("Бонусные баллы клиентам за покупки — начисление и списание при следующей оплате.",
              "Клиенттерге сатып алуулары үчүн бонус упайлар: топтолот жана кийинки төлөмдө чегерилет.",
              "Bonus points for customer purchases — earned at payment and redeemed on the next one.",
              "Alışveriş yapan müşterilere bonus puan: puanlar ödemede kazanılır ve sonraki alışverişte harcanır.",
              "Xaridlar uchun mijozlarga bonus ballar: ball yig'ish va keyingi to'lovda ularni ishlatish."),
            [T("Баллы начисляются автоматически при оплате", "Упайлар төлөмдө автоматтык түрдө эсептелет", "Points are earned automatically at payment", "Puanlar ödemede otomatik kazanılır", "Ballar to'lovda avtomatik hisoblanadi"),
             T($"Процент начисления по умолчанию — {prefs.LoyaltyEarnPercent:0.#}% от суммы покупки", $"Демейки эсептөө пайызы — сатып алуу суммасынын {prefs.LoyaltyEarnPercent:0.#}%", $"Default earn rate — {prefs.LoyaltyEarnPercent:0.#}% of the purchase amount", $"Varsayılan kazanım oranı — alışveriş tutarının %{prefs.LoyaltyEarnPercent:0.#}'i", $"Standart hisoblash foizi — xarid summasining {prefs.LoyaltyEarnPercent:0.#}%"),
             T("Списание баллов при следующей оплате", "Упайлар кийинки төлөмдө чегерилет", "Points are redeemed on the next payment", "Puanlar sonraki ödemede harcanır", "Ballar keyingi to'lovda ishlatiladi"),
             T("После активации включается сразу — отдельного тумблера нет", "Активдештирүүдөн кийин дароо күйөт — өзүнчө которгуч жок", "Turns on right after activation — there is no separate switch", "Etkinleştirmeden sonra hemen açılır — ayrı bir anahtar yoktur", "Faollashtirilgach darhol yoqiladi — alohida almashtirgich yo'q")],
            [T("Нужны клиенты в базе кассы", "Кассанын базасында кардарлар болушу керек", "Customers in the till database", "Kasa veritabanında müşteriler gerekir", "Kassa bazasida mijozlar bo'lishi kerak")],
            prefs.LoyaltyEnabled, new MarketplacePrice(1500), openLabel: null,
            hint: T("Работает автоматически на кассе — отдельно ничего открывать не нужно.", "Кассада автоматтык түрдө иштейт — өзүнчө эч нерсе ачуунун кереги жок.", "Works automatically at the till — nothing to open separately.", "Kasada otomatik çalışır — ayrıca bir şey açmanıza gerek yok.", "Kassada avtomatik ishlaydi — alohida hech narsani ochish shart emas."),
            tags: [TagSales, T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar")],
            statusText: T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan"), installedStatus: true,
            whatsNew: T("В Маркетплейсе с 05.09.2026", "Маркетплейсте 05.09.2026 тартып", "In the Marketplace since 05.09.2026", "Marketplace'te 05.09.2026'dan beri", "Marketpleysda 05.09.2026 dan beri"),
            featured: true, related: ["timesheet"]));

        // --- Учёт сотрудников (1500 сом; открывает табель)
        items.Add(Gated("timesheet", "🧑‍💼", "#0D9488",
            T("Учёт сотрудников", "Кызматкерлерди эсепке алуу", "Staff timesheet", "Personel takibi", "Xodimlar hisobi"),
            T("Табель по кассирам: смены, часы, выручка", "Кассирлер боюнча табель: смена, саат, түшүм", "Cashier timesheet: shifts, hours, revenue", "Kasiyer çizelgesi: vardiya, saat, ciro", "Kassirlar tabeli: smena, soat, tushum"),
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): точный состав таблицы (7 колонок) и источник — закрытые смены из NurCRM.
            T("Табель по кассирам за выбранный период: смены, дни, часы, средняя смена, выручка и даты первой и последней смены — по закрытым сменам из NurCRM.",
              "Тандалган мезгил үчүн кассирлер боюнча табель: сменалар, күндөр, сааттар, орточо смена, түшүм жана биринчи, акыркы сменанын күнү — NurCRM'деги жабылган сменалар боюнча.",
              "A cashier timesheet for any period: shifts, days, hours, average shift, revenue and first/last shift dates — from closed shifts in NurCRM.",
              "Seçilen dönem için kasiyer çizelgesi: vardiyalar, günler, saatler, ortalama vardiya, ciro, ilk ve son vardiya tarihi — NurCRM'deki kapanmış vardiyalardan.",
              "Tanlangan davr uchun kassirlar tabeli: smenalar, kunlar, soatlar, o'rtacha smena, tushum va birinchi/oxirgi smena sanasi — NurCRM'dagi yopilgan smenalar bo'yicha."),
            [T("Количество смен и отработанных часов по каждому кассиру", "Ар бир кассир боюнча смена жана иштелген саат саны", "Number of shifts and hours worked per cashier", "Her kasiyer için vardiya ve çalışılan saat sayısı", "Har bir kassir bo'yicha smena va ishlangan soatlar soni"),
             T("Выручка за выбранный период", "Тандалган мезгил үчүн түшүм", "Revenue for the selected period", "Seçilen dönem için ciro", "Tanlangan davr uchun tushum"),
             T("Данные берутся из смен — вводить ничего не нужно", "Маалымат сменалардан алынат — эч нерсе киргизүүнүн кереги жок", "Data comes from shifts — no manual entry", "Veriler vardiyalardan alınır — elle giriş gerekmez", "Ma'lumot smenalardan olinadi — hech narsa kiritish shart emas")],
            // 2026-10-23: данные идут с сервера NurCRM, без связи таблица пуста (StaffTimesheetWindow) — «нужен интернет».
            [T("Интернет: смены берутся из NurCRM", "Интернет: сменалар NurCRM'ден алынат", "Internet: shifts are taken from NurCRM", "İnternet: vardiyalar NurCRM'den alınır", "Internet: smenalar NurCRM'dan olinadi")],
            prefs.StaffTimesheetUnlocked, new MarketplacePrice(1500),
            openLabel: T("Открыть табель", "Табелди ачуу", "Open timesheet", "Çizelgeyi aç", "Tabelni ochish"),
            tags: [TagStaff, TagAnalytics], related: ["analytics", "shiftstats"]));

        // --- Весы (только «Старт»)
        if (start)
        {
            items.Add(Gated("scales", "⚖", "#2563EB",
                T("Весы", "Тараза", "Scales", "Tartı", "Tarozi"),
                T("Отправка товаров с PLU на сетевые весы", "PLU менен товарларды тармактык таразага жөнөтүү", "Send PLU products to network scales", "PLU'lu ürünleri ağ tartılarına gönder", "PLU li mahsulotlarni tarmoq tarozisiga yuborish"),
                // 2026-10-23, ТЗ «Маркетплейс» (П-1): марок больше, чем «Штрих-М, Rongta» — Штрих, Rongta, TM-30F (Dahua), AI-весы (только файл) (ScaleUi).
                T("Отправляет весовые товары с PLU-кодами на весы с печатью этикеток: Штрих, Rongta, TM-30F (Dahua); для AI-весов готовит файл. Настройки → Весы. Взвешивание на кассе через COM остаётся бесплатным.",
                  "PLU коддору бар салмактуу товарларды этикетка басуучу таразаларга жөнөтөт: Штрих, Rongta, TM-30F (Dahua); AI-таразалар үчүн файл даярдайт. Жөндөөлөр → Тараза. Кассада COM аркылуу тартуу акысыз бойдон калат.",
                  "Sends weighed products with PLU codes to label-printing scales: Shtrikh, Rongta, TM-30F (Dahua); prepares a file for AI scales. Settings → Scales. Weighing at the till over COM stays free.",
                  "PLU kodlu tartılı ürünleri etiket basan terazilere gönderir: Shtrikh, Rongta, TM-30F (Dahua); AI teraziler için dosya hazırlar. Ayarlar → Tartılar. Kasada COM ile tartım ücretsiz kalır.",
                  "PLU kodli vaznli mahsulotlarni yorliq bosuvchi tarozilarga yuboradi: Shtrix, Rongta, TM-30F (Dahua); AI tarozilar uchun fayl tayyorlaydi. Sozlamalar → Tarozilar. Kassada COM orqali tortish bepul qoladi."),
                [T("Отправка PLU на весы Штрих, Rongta и TM-30F (Dahua); для AI-весов — файл", "PLU'ну Штрих, Rongta жана TM-30F (Dahua) таразаларына жөнөтүү; AI-таразалар үчүн — файл", "Send PLU to Shtrikh, Rongta and TM-30F (Dahua) scales; a file for AI scales", "PLU'yu Shtrikh, Rongta ve TM-30F (Dahua) terazilerine gönderme; AI teraziler için dosya", "PLU ni Shtrix, Rongta va TM-30F (Dahua) tarozilariga yuborish; AI tarozilar uchun — fayl"),
                 T("Раздел — Настройки → Весы", "Бөлүм — Жөндөөлөр → Тараза", "Section — Settings → Scales", "Bölüm — Ayarlar → Tartılar", "Bo'lim — Sozlamalar → Tarozilar"),
                 T("Взвешивание на кассе по COM остаётся бесплатным", "Кассадагы COM аркылуу тартуу акысыз бойдон калат", "Weighing at the till over COM stays free", "Kasada COM ile tartım ücretsiz kalır", "Kassada COM orqali tortish bepul qoladi")],
                [T("Весы Штрих, Rongta или TM-30F (Dahua); для AI-весов — файл", "Штрих, Rongta же TM-30F (Dahua) таразасы; AI-таразалар үчүн — файл", "Shtrikh, Rongta or TM-30F (Dahua) scales; a file for AI scales", "Shtrikh, Rongta veya TM-30F (Dahua) tartısı; AI teraziler için dosya", "Shtrix, Rongta yoki TM-30F (Dahua) tarozisi; AI tarozilar uchun — fayl"),
                 T("Тариф «Старт» (на «Стандарте» входит в тариф)", "«Старт» тарифи («Стандарт» тарифине кирет)", "“Start” plan (included in “Standard”)", "«Start» tarifesi («Standart»'a dahildir)", "«Start» tarifi («Standart» tarifiga kiradi)")],
                prefs.ScalesUnlocked, packPrice, openLabel: null,
                hint: T("Открыто на вкладке Настройки → Весы.", "Жөндөөлөр → Тараза бетинде ачык.", "Available on the Settings → Scales tab.", "Ayarlar → Tartılar sekmesinde açık.", "Sozlamalar → Tarozilar bo'limida ochiq."),
                tags: [TagWarehouse, TagSales],
                statusText: T("Разблокировано", "Ачылды", "Unlocked", "Kilidi açıldı", "Ochildi"), installedStatus: true,
                whatsNew: T("В Маркетплейсе с 21.09.2026", "Маркетплейсте 21.09.2026 тартып", "In the Marketplace since 21.09.2026", "Marketplace'te 21.09.2026'dan beri", "Marketpleysda 21.09.2026 dan beri"),
                related: ["pricetag", "bulktag"]));
        }

        // --- Аналитика склада (1500 сом)
        items.Add(Gated("analytics", "📊", "#2563EB",
            T("Аналитика склада", "Складдын аналитикасы", "Warehouse analytics", "Depo analitiği", "Ombor analitikasi"),
            T("Остатки, топ товаров, категории", "Калдыктар, топ товарлар, категориялар", "Stock, top products, categories", "Stok, en iyi ürünler, kategoriler", "Qoldiqlar, top mahsulotlar, kategoriyalar"),
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): во вкладке только остатки, продаж там нет (WarehouseStockAnalyticsView) — убрано «и продажам».
            T("Вкладка «Аналитика» на складе: сколько товаров в каталоге, сколько закончилось и заканчивается, сумма остатка по закупке и по продаже, топ-10 товаров по стоимости остатка и разбивка по категориям.",
              "Кампадагы «Аналитика» өтмөгү: каталогдо канча товар бар, канчасы түгөндү жана түгөнүп баратат, калдыктын сатып алуу жана сатуу баасы боюнча суммасы, калдыктын наркы боюнча топ-10 товар жана категориялар боюнча бөлүштүрүү.",
              "An “Analytics” tab in the warehouse: products in the catalog, out of stock and low stock, stock value at purchase and sale price, top 10 products by stock value and a category breakdown.",
              "Depoda «Analiz» sekmesi: katalogdaki ürün sayısı, tükenen ve azalanlar, alış ve satış fiyatına göre stok değeri, stok değerine göre ilk 10 ürün ve kategori dağılımı.",
              "Omborda «Analitika» bo'limi: katalogdagi mahsulotlar soni, tugagan va kamayayotganlar, xarid va sotuv narxi bo'yicha qoldiq qiymati, qoldiq qiymati bo'yicha top-10 mahsulot va kategoriyalar kesimi."),
            [T("Показатели по остаткам: сколько товаров, что закончилось, сумма по закупке и по продаже", "Калдыктар боюнча көрсөткүчтөр: канча товар, эмне түгөндү, сатып алуу жана сатуу баасы боюнча сумма", "Stock metrics: product count, what ran out, totals at purchase and sale price", "Stok göstergeleri: ürün sayısı, tükenenler, alış ve satış fiyatına göre tutar", "Qoldiq ko'rsatkichlari: mahsulotlar soni, nima tugadi, xarid va sotuv narxi bo'yicha summa"),
             T("Топ товаров по стоимости", "Наркы боюнча топ товарлар", "Top products by value", "Değere göre en iyi ürünler", "Qiymati bo'yicha top mahsulotlar"),
             T("Разбивка по категориям", "Категориялар боюнча бөлүштүрүү", "Category breakdown", "Kategori dağılımı", "Kategoriyalar kesimi")],
            [T("Товары в базе кассы", "Кассанын базасындагы товарлар", "Products in the till database", "Kasa veritabanındaki ürünler", "Kassa bazasidagi mahsulotlar")],
            prefs.WarehouseAnalyticsUnlocked, new MarketplacePrice(1500),
            openLabel: T("Открыть аналитику склада", "Склад аналитикасын ачуу", "Open warehouse analytics", "Depo analitiğini aç", "Ombor analitikasini ochish"),
            tags: [TagWarehouse, TagAnalytics], featured: true, related: ["export", "shiftstats"]));

        // --- Редактор ценников / массовая печать / редактор этикетки (по 1500 сом, флаги независимые)
        items.Add(Gated("pricetag", "💲", "#F97316",
            T("Редактор ценников", "Баа белгиси редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorliqlari muharriri"),
            T("6 готовых шаблонов ценника", "Баа белгисинин 6 даяр шаблону", "6 ready-made price tag templates", "6 hazır fiyat etiketi şablonu", "Narx yorlig'ining 6 ta tayyor shabloni"),
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): путь — «⋯» в строке товара (WarehouseWindow.RowActions); акционных шаблонов два (PriceTagService).
            T("Ценник для товара: 6 готовых шаблонов — простой, со штрих-кодом, акционный, акционный с цветным фоном, подробный и с QR-кодом — с живым предпросмотром и печатью на термопринтер или A4. Склад → Товары → «⋯» в строке товара → «Ценник».",
              "Товар үчүн баа белгиси: 6 даяр шаблон — жөнөкөй, штрих-код менен, акциялык, түстүү фону бар акциялык, толук жана QR-код менен — түз алдын ала көрүү жана термопринтерге же A4 барагына басып чыгаруу менен. Кампа → Товарлар → товардын сабындагы «⋯» → «Баа белгиси».",
              "A price tag for a product: 6 ready templates — simple, with barcode, promo, promo with colored background, detailed and with QR code — with a live preview and printing on a thermal printer or A4. Warehouse → Products → “⋯” in the product row → “Price tag”.",
              "Ürün için fiyat etiketi: 6 hazır şablon — basit, barkodlu, promosyonlu, renkli zeminli promosyon, detaylı ve QR kodlu — canlı önizleme ve termal yazıcı ya da A4'e baskı. Depo → Ürünler → ürün satırındaki «⋯» → «Fiyat etiketi».",
              "Mahsulot uchun narx yorlig'i: 6 ta tayyor shablon — oddiy, shtrix-kodli, aksiya, rangli fonli aksiya, batafsil va QR-kodli — jonli ko'rinish va termoprinter yoki A4 ga chop etish. Ombor → Mahsulotlar → mahsulot qatoridagi «⋯» → «Narx yorlig'i»."),
            [T("Простой, со штрих-кодом, два акционных, подробный и с QR-кодом", "Жөнөкөй, штрих-код менен, эки акциялык, толук жана QR-код менен", "Simple, with barcode, two promo ones, detailed and with QR code", "Basit, barkodlu, iki promosyonlu, detaylı ve QR kodlu", "Oddiy, shtrix-kodli, ikkita aksiya, batafsil va QR-kodli"),
             T("Печать для одного товара из карточки товара", "Товар карточкасынан бир товар үчүн басып чыгаруу", "Print for one product from the product card", "Ürün kartından tek ürün için yazdırma", "Mahsulot kartasidan bitta mahsulot uchun chop etish")],
            [T("Принтер этикеток или обычный принтер", "Этикетка принтери же кадимки принтер", "A label printer or a regular printer", "Etiket yazıcısı veya normal yazıcı", "Yorliq printeri yoki oddiy printer")],
            prefs.PriceTagEditorUnlocked, new MarketplacePrice(1500),
            openLabel: T("Открыть склад", "Складды ачуу", "Open warehouse", "Depoyu aç", "Omborni ochish"),
            tags: [TagWarehouse, TagPrint], related: ["bulktag", "labeleditor"]));

        items.Add(Gated("bulktag", "🏷", "#F97316",
            T("Массовая печать ценников", "Баа белгилерин топтоп басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi baskısı", "Narx yorliqlarini ommaviy chop etish"),
            T("Ценники для многих товаров за один проход", "Көп товардын баа белгилери бир жолу", "Price tags for many products at once", "Birçok ürünün fiyat etiketi tek seferde", "Ko'p mahsulot narx yorliqlari bir yo'la"),
            T("Печать ценников сразу для нескольких товаров за один проход — на термопринтер или обычный A4, вместо печати по одному — вкладка «Склад» → «Товары».",
              "Бир нече товардын баа белгилерин бир жолу басып чыгаруу — термопринтерге же кадимки A4 барагына, бирден басып отурбай — «Кампа» → «Товарлар» өтмөгү.",
              "Print price tags for several products in one go — on a thermal printer or plain A4 — instead of one at a time. “Warehouse” → “Products”.",
              "Birden fazla ürünün fiyat etiketini tek seferde yazdırma — termal yazıcıya veya normal A4'e, tek tek yazdırmak yerine — «Depo» → «Ürünler».",
              "Bir nechta mahsulotning narx yorliqlarini bir yo'la chop etish — termoprinterda yoki oddiy A4 printerda, birma-bir emas — «Ombor» → «Mahsulotlar»."),
            [T("Выбор нескольких товаров и печать за один проход", "Бир нече товарды тандап, бир жолу басып чыгаруу", "Select several products and print in one pass", "Birden fazla ürünü seçip tek seferde yazdırma", "Bir nechta mahsulotni tanlab, bir yo'la chop etish"),
             T("Термопринтер или обычный лист A4", "Термопринтер же кадимки A4 барагы", "Thermal printer or plain A4 sheet", "Termal yazıcı veya normal A4 sayfa", "Termoprinter yoki oddiy A4 varaq")],
            [T("Термопринтер или принтер A4", "Термопринтер же A4 принтери", "A thermal or A4 printer", "Termal veya A4 yazıcı", "Termoprinter yoki A4 printer")],
            prefs.BulkPriceTagUnlocked, new MarketplacePrice(1500),
            openLabel: T("Открыть массовую печать", "Топтоп басууну ачуу", "Open bulk printing", "Toplu baskıyı aç", "Ommaviy chop etishni ochish"),
            tags: [TagWarehouse, TagPrint], whatsNew: T("Отдельная активация с 04.09.2026", "Өзүнчө активдештирүү 04.09.2026 тартып", "Separate activation since 04.09.2026", "Ayrı etkinleştirme 04.09.2026'dan beri", "Alohida faollashtirish 04.09.2026 dan beri"),
            related: ["pricetag", "labeleditor"]));

        items.Add(Gated("labeleditor", "🎨", "#DB2777",
            T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"),
            T("Свой макет этикетки: штрих-код, цена, название", "Өз этикетка макети: штрих-код, баа, аталыш", "Your own label layout: barcode, price, name", "Kendi etiket yerleşiminiz: barkod, fiyat, ad", "O'z yorliq maketi: shtrix-kod, narx, nom"),
            T("Расположение штрих-кода, названия, цены, артикула, единицы измерения и магазина на этикетке — с перетаскиванием и настройкой каждого элемента.",
              "Штрих-коддун, аталыштын, баанын, артикулдун, өлчөм бирдигинин жана дүкөндүн этикеткадагы жайгашуусу — ар бир элементти сүйрөп жылдырууга жана жөндөөгө болот.",
              "Place the barcode, name, price, SKU, unit and store name on the label — drag and configure each element.",
              "Etiketteki barkod, ad, fiyat, stok kodu, ölçü birimi ve mağaza adının yerleşimi — her öğe sürüklenip ayrı ayrı ayarlanabilir.",
              "Yorliqdagi shtrix-kod, nom, narx, artikul, o'lchov birligi va do'kon nomining joylashuvi — har bir elementni sudrab ko'chirish va sozlash imkoni bilan."),
            [T("Штрих-код, название, цена, артикул, единица, магазин", "Штрих-код, аталыш, баа, артикул, бирдик, дүкөн", "Barcode, name, price, SKU, unit, store", "Barkod, ad, fiyat, stok kodu, birim, mağaza", "Shtrix-kod, nom, narx, artikul, birlik, do'kon"),
             T("Перетаскивание и настройка каждого элемента", "Ар бир элементти сүйрөө жана жөндөө", "Drag and configure every element", "Her öğeyi sürükleme ve ayarlama", "Har bir elementni sudrash va sozlash")],
            [T("Принтер этикеток", "Этикетка принтери", "A label printer", "Etiket yazıcısı", "Yorliq printeri")],
            prefs.LabelEditorUnlocked, new MarketplacePrice(1500),
            openLabel: T("Открыть редактор", "Редакторду ачуу", "Open editor", "Düzenleyiciyi aç", "Muharrirni ochish"),
            tags: [TagWarehouse, TagPrint], whatsNew: T("Платная с 07.09.2026", "Акылуу 07.09.2026 тартып", "Paid since 07.09.2026", "07.09.2026'dan beri ücretli", "07.09.2026 dan beri pullik"),
            related: ["pricetag", "bulktag"]));

        // --- Выгрузка аналитики / бот / итоги смены: на «Старте» платные, на «Стандарте» входят в тариф
        var inTariff = T("Входит в тариф", "Тарифке кирет", "Included in the plan", "Tarifeye dahil", "Tarifga kiradi");
        var connected = T("Подключено", "Туташтырылды", "Connected", "Bağlandı", "Ulandi");

        items.Add(Gated("export", "📤", "#16A34A",
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): с 19.10 «Excel» в «Отчётах» выгружает список чеков (листы «Чеки» и «Товары»), графики — только в Word.
            T("Отчёты в Word и Excel", "Word жана Excel отчёттору", "Reports to Word and Excel", "Word ve Excel raporları", "Word va Excel hisobotlari"),
            T("Word-отчёт с графиками и Excel со всеми чеками", "Графиктери бар Word отчёту жана бардык чектери бар Excel", "A Word report with charts and an Excel file with all receipts", "Grafikli Word raporu ve tüm fişleri içeren Excel", "Grafikli Word hisoboti va barcha cheklar bilan Excel"),
            T("В «Отчётах» за любой период: Word — аналитический отчёт с графиками (итоги, выручка по дням, топ товаров, ABC-анализ, что пора заказать); Excel — все чеки и проданные товары. Файлы собирает сама касса — Office не нужен.",
              "«Отчёттордо» каалаган мезгил үчүн: Word — графиктери бар аналитикалык отчёт (жыйынтыктар, күндөр боюнча түшүм, топ товарлар, ABC-анализ, эмнеге буйрутма берүү керек); Excel — бардык чектер жана сатылган товарлар. Файлдарды касса өзү түзөт — Office керек эмес.",
              "In “Reports”, for any period: Word — an analytics report with charts (totals, revenue by day, top products, ABC analysis, what to reorder); Excel — all receipts and sold items. The till builds the files itself — no Office needed.",
              "«Raporlar»da her dönem için: Word — grafikli analiz raporu (toplamlar, günlük ciro, en çok satanlar, ABC analizi, sipariş edilecekler); Excel — tüm fişler ve satılan ürünler. Dosyaları kasa kendisi oluşturur — Office gerekmez.",
              "«Hisobotlar»da istalgan davr uchun: Word — grafikli tahliliy hisobot (yakunlar, kunlik tushum, top mahsulotlar, ABC-tahlil, nimani buyurtma qilish kerak); Excel — barcha cheklar va sotilgan mahsulotlar. Fayllarni kassaning o'zi tayyorlaydi — Office shart emas."),
            [T("Word: аналитический отчёт с графиками — итоги, выручка по дням, топ товаров, ABC-анализ, что пора заказать", "Word: графиктери бар аналитикалык отчёт — жыйынтыктар, күндөр боюнча түшүм, топ товарлар, ABC-анализ, эмнеге буйрутма берүү керек", "Word: an analytics report with charts — totals, revenue by day, top products, ABC analysis, what to reorder", "Word: grafikli analiz raporu — toplamlar, günlük ciro, en çok satanlar, ABC analizi, sipariş edilecekler", "Word: grafikli tahliliy hisobot — yakunlar, kunlik tushum, top mahsulotlar, ABC-tahlil, nimani buyurtma qilish kerak"),
             T("Excel: все чеки и проданные товары за период (листы «Чеки» и «Товары»)", "Excel: мезгилдеги бардык чектер жана сатылган товарлар («Чектер» жана «Товарлар» барактары)", "Excel: all receipts and sold items for the period (“Receipts” and “Products” sheets)", "Excel: dönemdeki tüm fişler ve satılan ürünler («Fişler» ve «Ürünler» sayfaları)", "Excel: davrdagi barcha cheklar va sotilgan mahsulotlar («Cheklar» va «Mahsulotlar» varaqlari)"),
             T("Excel и Word на компьютере не нужны", "Компьютерде Excel жана Word керек эмес", "No Excel or Word needed on the computer", "Bilgisayarda Excel ve Word gerekmez", "Kompyuterda Excel va Word shart emas")],
            [T("Кнопки «Excel» и «Word» — в разделе «Отчёты»", "«Excel» жана «Word» баскычтары — «Отчёттор» бөлүмүндө", "The “Excel” and “Word” buttons are in the “Reports” section", "«Excel» ve «Word» düğmeleri «Raporlar» bölümünde", "«Excel» va «Word» tugmalari — «Hisobotlar» bo'limida")],
            TariffGate.CanUseAnalyticsExport, start ? packPrice : null,
            openLabel: T("Где выгрузить", "Кайдан чыгарса болот", "Where to export", "Nereden aktarılır", "Qayerda eksport qilinadi"),
            tags: [TagAnalytics, TagOwner],
            statusText: start ? connected : inTariff, installedStatus: true,
            whatsNew: T("В Маркетплейсе с 22.09.2026", "Маркетплейсте 22.09.2026 тартып", "In the Marketplace since 22.09.2026", "Marketplace'te 22.09.2026'dan beri", "Marketpleysda 22.09.2026 dan beri"),
            isNew: true, related: ["analytics", "shiftstats"],
            note: T("Excel со сводкой и графиками (ABC, по дням, склад) — в окне ABC-анализа раздела «Аналитика» (на «Старте» — пакет «Продажи и аналитика»).",
                "Жыйынтыгы жана графиктери бар Excel (ABC, күндөр боюнча, кампа) — «Аналитика» бөлүмүнүн ABC-анализ терезесинде («Стартта» — «Сатуулар жана аналитика» пакети).",
                "Excel with a summary and charts (ABC, by day, warehouse) is in the ABC analysis window of the “Analytics” section (on “Start” — the “Sales and analytics” pack).",
                "Özet ve grafikli Excel (ABC, günlük, depo) «Analiz» bölümünün ABC analizi penceresindedir («Start»'ta — «Satışlar ve analiz» paketi).",
                "Yakun va grafikli Excel (ABC, kunlar bo'yicha, ombor) «Analitika» bo'limining ABC-tahlil oynasida («Start»da — «Sotuvlar va analitika» paketi).")));

        items.Add(Gated("telegram", "✈", "#0EA5E9",
            T("Телеграм-бот владельца", "Ээсинин телеграм-боту", "Owner's Telegram bot", "Sahibin Telegram botu", "Egasi uchun Telegram-bot"),
            T("Сводка по смене и ответы на вопросы в Telegram", "Смена жыйынтыгы жана суроолорго жооптор Telegram'да", "Shift summary and answers in Telegram", "Vardiya özeti ve yanıtlar Telegram'da", "Smena hisoboti va savollarga javoblar Telegram'da"),
            T("Сводка по закрытой смене приходит вам в Telegram. Бот отвечает на команды: выручка за день и неделю, топ товаров, что пора заказать, что заканчивается, список должников со ссылками для напоминания.",
              "Жабылган смена боюнча жыйынтык сизге Telegram'га келет. Бот буйруктарга жооп берет: күндүк жана жумалык түшүм, топ товарлар, эмнеге буйрутма берүү керек, эмне түгөнүп баратат, эскертүү шилтемелери менен карызкорлордун тизмеси.",
              "You get a summary of each closed shift in Telegram. The bot answers commands: revenue for the day and week, top products, what to reorder, what's running low, and a list of debtors with reminder links.",
              "Kapanan vardiyanın özeti size Telegram'dan gelir. Bot şu komutlara yanıt verir: günlük ve haftalık ciro, en çok satanlar, sipariş edilmesi gerekenler, tükenmek üzere olanlar, hatırlatma bağlantılı borçlu listesi.",
              "Yopilgan smena bo'yicha hisobot sizga Telegram'da keladi. Bot buyruqlarga javob beradi: kunlik va haftalik tushum, top mahsulotlar, nimani buyurtma qilish kerak, nima tugayapti, eslatma havolalari bilan qarzdorlar ro'yxati."),
            [T("Сводка при закрытии смены", "Смена жабылганда жыйынтык", "Summary when a shift closes", "Vardiya kapanınca özet", "Smena yopilganda hisobot"),
             T("Выручка за день и неделю, топ товаров", "Күндүк жана жумалык түшүм, топ товарлар", "Revenue for the day and week, top products", "Günlük ve haftalık ciro, en çok satanlar", "Kunlik va haftalik tushum, top mahsulotlar"),
             T("Что пора заказать и что заканчивается", "Эмнеге буйрутма берүү керек жана эмне түгөнүп баратат", "What to reorder and what's running low", "Neyin sipariş edileceği ve neyin tükendiği", "Nimani buyurtma qilish va nima tugayotgani"),
             T("Список должников со ссылками для напоминания", "Эскертүү шилтемелери менен карызкорлордун тизмеси", "A list of debtors with reminder links", "Hatırlatma bağlantılı borçlu listesi", "Eslatma havolalari bilan qarzdorlar ro'yxati")],
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): без переноса на сервер бот отвечает, только пока включена касса (TelegramBotPollingService).
            [T("Telegram и интернет; без переноса на сервер бот работает, пока включена касса", "Telegram жана интернет; серверге которулбаса, бот касса күйүп турганда иштейт", "Telegram and internet; without moving to the server the bot works while the till is on", "Telegram ve internet; sunucuya taşınmazsa bot kasa açıkken çalışır", "Telegram va internet; serverga ko'chirilmasa, bot kassa yoniq turganda ishlaydi")],
            TariffGate.CanUseTelegramBot, start ? packPrice : null,
            openLabel: T("Настроить бота", "Ботту жөндөө", "Set up the bot", "Botu ayarla", "Botni sozlash"),
            tags: [TagOwner, TagAnalytics],
            statusText: start ? connected : inTariff, installedStatus: true,
            whatsNew: T("В Маркетплейсе с 22.09.2026", "Маркетплейсте 22.09.2026 тартып", "In the Marketplace since 22.09.2026", "Marketplace'te 22.09.2026'dan beri", "Marketpleysda 22.09.2026 dan beri"),
            isNew: true, featured: true, related: ["shiftstats", "export"]));

        items.Add(Gated("shiftstats", "🧾", "#16A34A",
            T("Расширенные итоги смены", "Кеңейтилген смена жыйынтыктары", "Extended shift totals", "Genişletilmiş vardiya toplamları", "Kengaytirilgan smena yakunlari"),
            T("Возвраты, списания, расход и скидки по смене", "Смена боюнча кайтаруулар, чыгымдар жана арзандатуулар", "Returns, write-offs, expenses and discounts per shift", "Vardiya için iadeler, giderler ve indirimler", "Smena bo'yicha qaytarishlar, xarajatlar va chegirmalar"),
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): часть цифр (возвраты, расход, скидки) берётся с сервера, когда он доступен (ShiftDetailsDialog) — «работает без интернета» убрано.
            T("В окне смены появляется ряд из 5 плиток: возвраты, списания, расход, оплата долгов и скидки (в том числе бонусами); нажатие открывает подробности. Возвраты, списания, расход и оплата долгов попадают и в X/Z-отчёт.",
              "Смена терезесинде 5 плиткадан турган сап пайда болот: кайтаруулар, эсептен чыгаруулар, чыгым, карыз төлөмдөрү жана арзандатуулар (бонус менен да); басканда толук маалымат ачылат. Кайтаруулар, эсептен чыгаруулар, чыгым жана карыз төлөмдөрү X/Z-отчётко да кирет.",
              "The shift window gets a row of 5 tiles: returns, write-offs, expenses, debt payments and discounts (including points); a click opens details. Returns, write-offs, expenses and debt payments also go into the X/Z report.",
              "Vardiya penceresine 5 kutucuk eklenir: iadeler, stok düşümleri, giderler, borç ödemeleri ve indirimler (puanlar dahil); tıklayınca ayrıntılar açılır. İadeler, düşümler, giderler ve borç ödemeleri X/Z raporuna da girer.",
              "Smena oynasida 5 ta plitka paydo bo'ladi: qaytarishlar, hisobdan chiqarishlar, xarajat, qarz to'lovlari va chegirmalar (bonuslar bilan ham); bosilganda tafsilotlar ochiladi. Qaytarishlar, hisobdan chiqarishlar, xarajat va qarz to'lovlari X/Z-hisobotga ham tushadi."),
            [T("Возвраты, списания, расход, оплата долгов, скидки", "Кайтаруулар, эсептен чыгаруулар, чыгымдар, карыз төлөмдөрү, арзандатуулар", "Returns, write-offs, expenses, debt payments, discounts", "İadeler, stok düşümleri, giderler, borç ödemeleri, indirimler", "Qaytarishlar, hisobdan chiqarishlar, xarajatlar, qarz to'lovlari, chegirmalar"),
             T("В окне смены, X/Z-отчёте и сводке закрытия", "Смена терезесинде, X/Z-отчётто жана жабуу жыйынтыгында", "In the shift window, the X/Z report and the closing summary", "Vardiya penceresinde, X/Z raporunda ve kapanış özetinde", "Smena oynasida, X/Z-hisobotda va yopilish hisobotida"),
             T("Учёт ведётся с версии 1.16.86; без связи показывается то, что записала эта касса", "Эсеп 1.16.86 версиясынан бери жүрөт; байланыш жок болсо, ушул касса жазганы көрсөтүлөт", "Tracked since version 1.16.86; without a connection, what this till recorded is shown", "Kayıt 1.16.86 sürümünden beri tutulur; bağlantı yokken bu kasanın kaydettikleri gösterilir", "Hisob 1.16.86 versiyasidan beri yuritiladi; aloqa bo'lmasa, shu kassa yozganlari ko'rsatiladi")],
            [T("Итоги открываются из меню кассы: «Смены» → выбрать смену", "Жыйынтыктар касса менюсунан ачылат: «Смена» → сменаны тандаңыз", "Totals open from the till menu: “Shifts” → select a shift", "Özet kasa menüsünden açılır: «Vardiyalar» → vardiyayı seçin", "Yakunlar kassa menyusidan ochiladi: «Smenalar» → smenani tanlang")],
            TariffGate.CanUseShiftAnalytics, start ? packPrice : null,
            openLabel: T("Открыть смены", "Сменаларды көрүү", "View shifts", "Vardiyaları aç", "Smenalar bo'limini ochish"),
            tags: [TagSales, TagAnalytics],
            statusText: start ? connected : inTariff, installedStatus: true,
            whatsNew: T("В Маркетплейсе с 22.09.2026", "Маркетплейсте 22.09.2026 тартып", "In the Marketplace since 22.09.2026", "Marketplace'te 22.09.2026'dan beri", "Marketpleysda 22.09.2026 dan beri"),
            isNew: true, related: ["telegram", "timesheet"],
            note: T("Учёт ведётся с версии 1.16.86 — у более старых смен в плитках «—». Без связи показывается то, что записала эта касса.",
                "Эсеп 1.16.86 версиясынан бери жүрөт — эски сменаларда плиткаларда «—». Байланыш жок болсо, ушул касса жазганы көрсөтүлөт.",
                "Tracked since version 1.16.86 — older shifts show “—” in the tiles. Without a connection, what this till recorded is shown.",
                "Kayıtlar 1.16.86 sürümünden beri tutuluyor — daha eski vardiyalarda kutucuklarda «—» görünür. Bağlantı yokken bu kasanın kaydettikleri gösterilir.",
                "Hisob 1.16.86 versiyasidan beri yuritiladi — eski smenalarda plitkalarda «—» bo'ladi. Aloqa bo'lmasa, shu kassa yozganlari ko'rsatiladi.")));

        // --- Языковой пакет (платный, сумма в коде не задана)
        {
            var unlocked = LanguagePackGate.IsUnlocked;
            items.Add(Gated("language", "🌐", "#2563EB",
                T("Языковой пакет", "Тил пакети", "Language pack", "Dil paketi", "Til paketi"),
                T("English, Türkçe, O'zbekcha", "English, Türkçe, O'zbekcha", "English, Türkçe, O'zbekcha", "English, Türkçe, O'zbekcha", "English, Türkçe, O'zbekcha"),
                T("English, Türkçe, O'zbekcha — дополнительные языки интерфейса кассы. По умолчанию доступны русский и кыргызский. После активации языки появятся в Настройки → Экран.",
                  "English, Türkçe, O'zbekcha — кассанын кошумча интерфейс тилдери. Демейки боюнча орусча жана кыргызча жеткиликтүү. Активациядан кийин тилдер Жөндөөлөр → Экран бөлүмүндө пайда болот.",
                  "English, Türkçe, O'zbekcha — additional interface languages for the till. Russian and Kyrgyz are available by default. After activation, the languages appear in Settings → Screen.",
                  "English, Türkçe, O'zbekcha — kasa arayüzü için ek diller. Varsayılan olarak Rusça ve Kırgızca kullanılabilir. Etkinleştirmeden sonra diller Ayarlar → Ekran bölümünde görünür.",
                  "English, Türkçe, O'zbekcha — kassa interfeysining qo'shimcha tillari. Standart holatda rus va qirg'iz tillari mavjud. Faollashtirilgach, tillar Sozlamalar → Ekran bo'limida paydo bo'ladi."),
                [T("Три дополнительных языка интерфейса: английский, турецкий, узбекский", "Интерфейстин үч кошумча тили: англисче, түркчө, өзбекче", "Three extra interface languages: English, Turkish, Uzbek", "Üç ek arayüz dili: İngilizce, Türkçe, Özbekçe", "Interfeysning uchta qo'shimcha tili: inglizcha, turkcha, o'zbekcha"),
                 T("Словари встроены — скачивать ничего не нужно", "Сөздүктөр орнотулган — эч нерсе жүктөөнүн кереги жок", "Dictionaries are built in — nothing to download", "Sözlükler dahili — indirmeye gerek yok", "Lug'atlar o'rnatilgan — hech narsa yuklab olish shart emas"),
                 T("Языки включаются в Настройки → Экран", "Тилдер Жөндөөлөр → Экран бөлүмүндө күйгүзүлөт", "Languages are selected in Settings → Screen", "Diller Ayarlar → Ekran'da seçilir", "Tillar Sozlamalar → Ekran bo'limida tanlanadi")],
                [T("Русский и кыргызский доступны всегда", "Орус жана кыргыз тили дайыма жеткиликтүү", "Russian and Kyrgyz are always available", "Rusça ve Kırgızca her zaman kullanılabilir", "Rus va qirg'iz tillari doim mavjud")],
                unlocked, new MarketplacePrice(null), openLabel: null,
                hint: T("Языки появились в Настройки → Экран.", "Тилдер Жөндөөлөр → Экран бөлүмүндө пайда болду.", "The languages are now in Settings → Screen.", "Diller Ayarlar → Ekran bölümünde göründü.", "Tillar Sozlamalar → Ekran bo'limida paydo bo'ldi."),
                tags: [TagLook, T("Языки", "Тилдер", "Languages", "Diller", "Tillar")],
                statusText: T("Активировано", "Активдештирилди", "Activated", "Etkinleştirildi", "Faollashtirildi"), installedStatus: true,
                whatsNew: T("В Маркетплейсе с 07.09.2026", "Маркетплейсте 07.09.2026 тартып", "In the Marketplace since 07.09.2026", "Marketplace'te 07.09.2026'dan beri", "Marketpleysda 07.09.2026 dan beri"),
                related: ["nurmarket"]));
        }

        // --- SMS (скоро)
        items.Add(new MarketplaceItem
        {
            Id = "sms",
            Kind = MarketplaceKind.Feature,
            Title = T("SMS-уведомления клиентам", "Кардарларга SMS-билдирмелер", "SMS notifications for customers", "Müşterilere SMS bildirimleri", "Mijozlarga SMS-xabarnomalar"),
            ShortDescription = T("Статус заказа и акции — по SMS", "Заказдын статусу жана акциялар — SMS аркылуу", "Order status and promotions by SMS", "Sipariş durumu ve kampanyalar SMS ile", "Buyurtma holati va aksiyalar — SMS orqali"),
            LongDescription = T("Автоматические уведомления о статусе заказа и акциях.", "Заказдын статусу жана акциялар жөнүндө автоматтык билдирмелер.", "Automatic notifications about order status and promotions.", "Sipariş durumu ve kampanyalar hakkında otomatik bildirimler.", "Buyurtma holati va aksiyalar haqida avtomatik xabarnomalar."),
            IconGlyph = "💬",
            IconColor = "#0EA5E9",
            Status = MarketplaceStatus.ComingSoon,
            Version = "",
            Tags = [T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar")],
            Features =
            [
                T("Уведомление клиента о статусе заказа", "Кардарга заказдын статусу жөнүндө билдирме", "Notify the customer about the order status", "Müşteriye sipariş durumu bildirimi", "Mijozga buyurtma holati haqida xabar"),
                T("Рассылка об акциях", "Акциялар жөнүндө жиберүү", "Promotion mailings", "Kampanya gönderimleri", "Aksiyalar haqida xabar yuborish"),
            ],
            Requirements = [T("Функция в разработке — дата выхода и цена пока не объявлены", "Функция иштелип жатат — чыгуу күнү жана баасы азырынча жарыяланган жок", "In development — release date and price are not announced yet", "Geliştirme aşamasında — çıkış tarihi ve fiyat henüz açıklanmadı", "Ishlab chiqilmoqda — chiqish sanasi va narxi hali e'lon qilinmagan")],
            RelatedIds = ["loyalty", "telegram"],
        });

        // --- Пакеты «Стандарта» на «Старте»
        if (start)
        {
            foreach (var (slug, title, description) in MarketplaceView.StandardPacks())
            {
                var has = TariffGate.HasPack(slug);
                items.Add(Gated("pack:" + slug, "🧩", "#7C3AED", title, description, description,
                    // 2026-10-23, ТЗ «Маркетплейс» (П-1, 5.14): «Что откроется» — по разделам пакета; общая плашка — что пакетами не открывается.
                    MarketplaceContent.PackFeatures(slug),
                    [T("Тариф «Старт» (на «Стандарте» всё это уже входит в тариф)", "«Старт» тарифи («Стандарт» тарифинде мунун баары кирет)", "“Start” plan (all of this is already in “Standard”)", "«Start» tarifesi («Standart»'ta bunların hepsi dahildir)", "«Start» tarifi («Standart» tarifida bularning hammasi kiradi)")],
                    has, packPrice, openLabel: null,
                    hint: T("Подключено — разделы в меню.", "Туташтырылды — бөлүмдөр менюда.", "Connected — the sections are in the menu.", "Bağlandı — bölümler menüde.", "Ulandi — bo'limlar menyuda."),
                    tags: [T("Пакет «Стандарта»", "«Стандарт» пакети", "“Standard” pack", "«Standart» paketi", "«Standart» paketi")],
                    statusText: connected, installedStatus: true,
                    whatsNew: T("В Маркетплейсе с 05.10.2026", "Маркетплейсте 05.10.2026 тартып", "In the Marketplace since 05.10.2026", "Marketplace'te 05.10.2026'dan beri", "Marketpleysda 05.10.2026 dan beri"),
                    related: [], kind: MarketplaceKind.Service, note: MarketplaceContent.PackNote()));
            }
        }

        // ------------------------------------------------------------------ виды кассы
        var currentLayout = KassaLayouts.Normalize(prefs.MainLayoutMode);
        var layoutColors = new Dictionary<string, string>
        {
            [KassaLayouts.Standard] = "#FFB208", [KassaLayouts.Table] = "#2563EB", [KassaLayouts.Cards] = "#16A34A",
            [KassaLayouts.Minimal] = "#0D9488", [KassaLayouts.Pro] = "#7C3AED", [KassaLayouts.OneC] = "#F97316",
        };
        foreach (var option in KassaLayouts.All)
        {
            var id = option.Id;
            var isCurrent = id == currentLayout;
            var label = option.Label();
            var oneCExtra = id == KassaLayouts.OneC
                ? T("Товар добавляется только сканером или из избранного.", "Товар сканер же тандалмалар аркылуу гана кошулат.", "Products are added by scanner or from favorites only.", "Ürün yalnızca barkod okuyucu veya favorilerle eklenir.", "Mahsulot faqat skaner yoki sevimlilar orqali qo'shiladi.")
                : "";
            items.Add(new MarketplaceItem
            {
                Id = "layout:" + id,
                Kind = MarketplaceKind.Layout,
                Title = id == KassaLayouts.OneC ? T("Интерфейс кассира 1С", "1С кассир интерфейси", "1C cashier interface", "1C kasiyer arayüzü", "1C kassir interfeysi") : label,
                ShortDescription = ShortLayout(id),
                LongDescription = option.Description(),
                IconGlyph = "🖥",
                IconColor = layoutColors.GetValueOrDefault(id, "#FFB208"),
                Status = isCurrent ? MarketplaceStatus.Installed : MarketplaceStatus.Available,
                StatusText = isCurrent ? T("Выбран", "Тандалган", "Selected", "Seçili", "Tanlangan") : "",
                Version = AppVersion,
                Tags = [TagLook, T("Вид кассы", "Касса көрүнүшү", "Till layout", "Kasa görünümü", "Kassa ko'rinishi")],
                Features =
                [
                    T("Применяется мгновенно, без перезапуска", "Дароо колдонулат, кайра ачуунун кереги жок", "Applies instantly, no restart", "Anında uygulanır, yeniden başlatma gerekmez", "Darhol qo'llanadi, qayta ishga tushirish shart emas"),
                    T("Второй экран покупателя меняется вместе с видом кассы", "Сатып алуучунун экинчи экраны кассанын көрүнүшү менен бирге өзгөрөт", "The customer's second screen changes together with the layout", "Müşterinin ikinci ekranı görünümle birlikte değişir", "Xaridorning ikkinchi ekrani ko'rinish bilan birga o'zgaradi"),
                    T("Вернуться к другому виду можно в любой момент: здесь или в Настройки → Экран", "Башка көрүнүшкө каалаган убакта кайтууга болот: бул жерде же Жөндөөлөр → Экран", "Switch back any time: here or in Settings → Screen", "İstediğiniz zaman geri dönebilirsiniz: burada veya Ayarlar → Ekran", "Istalgan vaqtda qaytish mumkin: shu yerda yoki Sozlamalar → Ekran"),
                ],
                Requirements = oneCExtra == "" ? [] : [oneCExtra],
                WhatsNew = id is KassaLayouts.Standard or KassaLayouts.OneC ? "" : T("В Маркетплейсе с 28.09.2026", "Маркетплейсте 28.09.2026 тартып", "In the Marketplace since 28.09.2026", "Marketplace'te 28.09.2026'dan beri", "Marketpleysda 28.09.2026 dan beri"),
                IsNew = id is not (KassaLayouts.Standard or KassaLayouts.OneC),
                IsFeatured = id == KassaLayouts.Cards,
                Primary = isCurrent
                    ? new MarketplaceAction(T("Выбран", "Тандалган", "Selected", "Seçili", "Tanlangan"), () => { }, false)
                    : new MarketplaceAction(T("Применить вид", "Көрүнүштү колдонуу", "Apply layout", "Görünümü uygula", "Ko'rinishni qo'llash"), () => _run("layout:" + id)),
                IconVisual = size => new Viewbox
                {
                    Width = size - 10,
                    Height = size - 10,
                    Stretch = Stretch.Uniform,
                    Child = KassaLayouts.BuildPreview(id, 220, 132),
                },
                HeroVisual = () => MarketplaceLookPreview.LayoutHero(id),
                RelatedIds = KassaLayouts.All.Where(o => o.Id != id).Take(3).Select(o => "layout:" + o.Id).Append("customerdisplay").ToList(),
            });
        }

        // --- Экран покупателя (редактор)
        items.Add(new MarketplaceItem
        {
            Id = "customerdisplay",
            Kind = MarketplaceKind.Layout,
            Title = T("Экран покупателя", "Сатып алуучунун экраны", "Customer display", "Müşteri ekranı", "Xaridor ekrani"),
            ShortDescription = T("Второй экран: надписи, цвета, что показывать", "Экинчи экран: жазуулар, түстөр, эмнени көрсөтүү", "Second screen: captions, colors, what to show", "İkinci ekran: yazılar, renkler, ne gösterilecek", "Ikkinchi ekran: yozuvlar, ranglar, nimani ko'rsatish"),
            LongDescription = CustomerDisplayText(),
            IconGlyph = "🖥",
            IconColor = "#0D9488",
            Status = MarketplaceStatus.Available,
            Version = AppVersion,
            Tags = [TagLook, TagSales],
            Features =
            [
                T("Живой предпросмотр экрана в нынешнем виде", "Экрандын азыркы көрүнүшүнүн түз алдын ала көрүнүшү", "A live preview of the screen as it looks now", "Ekranın şu anki halinin canlı önizlemesi", "Ekranning hozirgi ko'rinishining jonli ko'rinishi"),
                T("Надписи, цвета и состав показываемых данных — в редакторе", "Жазуулар, түстөр жана көрсөтүлүүчү маалыматтар — редактордо", "Captions, colors and what data to show — in the editor", "Yazılar, renkler ve gösterilecek veriler — düzenleyicide", "Yozuvlar, ranglar va ko'rsatiladigan ma'lumotlar — muharrirda"),
            ],
            Requirements = [T("Второй монитор для экрана покупателя (предпросмотр есть и без него)", "Сатып алуучунун экраны үчүн экинчи монитор (алдын ала көрүү андан тышкары да бар)", "A second monitor for the customer display (the preview works without it)", "Müşteri ekranı için ikinci monitör (önizleme onsuz da çalışır)", "Xaridor ekrani uchun ikkinchi monitor (ko'rinish usiz ham ishlaydi)")],
            Primary = new MarketplaceAction(T("Открыть редактор экрана покупателя", "Сатып алуучунун экраны редакторун ачуу", "Open the customer display editor", "Müşteri ekranı düzenleyicisini aç", "Xaridor ekrani muharririni ochish"), () => _run("customerdisplay")),
            HeroVisual = MarketplaceLookPreview.CustomerDisplayHero,
            RelatedIds = KassaLayouts.All.Take(3).Select(o => "layout:" + o.Id).ToList(),
        });

        // ------------------------------------------------------------------ темы
        var dark = prefs.DarkTheme;
        var currentTheme = AppliedThemeId(AccentThemeService.Normalize(prefs.AccentTheme),
            AccentThemeService.AvailableThemes.Select(t => t.Id));
        // 2026-10-24 (приёмка, ПР-2): пояснение про «программу владельца» — только в программе владельца; в кассе оно лишнее.
        var themeNote = !AppMode.IsOwner ? "" : T("Тема программы владельца и тема кассы выбираются отдельно: отметка «Выбрана» относится к той программе, в которой вы сейчас. Чтобы сменить тему кассы, откройте этот раздел в самой кассе.",
            "Ээсинин программасынын темасы жана кассанын темасы өзүнчө тандалат: «Тандалган» белгиси азыр өзүңүз турган программага тиешелүү. Кассанын темасын өзгөртүү үчүн бул бөлүмдү кассанын өзүндө ачыңыз.",
            "The owner app theme and the till theme are chosen separately: the “Selected” mark refers to the program you are in now. To change the till theme, open this section in the till itself.",
            "Sahip programının teması ile kasanın teması ayrı seçilir: «Seçili» işareti şu an içinde olduğunuz programa aittir. Kasa temasını değiştirmek için bu bölümü kasanın kendisinde açın.",
            "Egasi dasturi mavzusi va kassa mavzusi alohida tanlanadi: «Tanlangan» belgisi hozir turgan dasturingizga tegishli. Kassa mavzusini o'zgartirish uchun bu bo'limni kassaning o'zida oching.");
        // 2026-10-27 (аудит редакторов, В-5): своя тема выбрана, а на этой кассе её нет (создана на другом компьютере) — раньше молча включалась «Классическая».
        if (AccentThemeService.MissingCustomThemeId is not null)
            themeNote = (themeNote.Length > 0 ? themeNote + " " : "") + T("⚠ Выбранной в настройках своей темы нет на этой кассе (она создана на другом компьютере), поэтому показана «Классическая». Перенесите тему файлом: «Редактор тем» → «Экспорт» на той кассе и «Импорт» здесь.",
                "⚠ Жөндөөдө тандалган өз тема бул кассада жок (башка компьютерде түзүлгөн), ошондуктан «Классикалык» көрсөтүлдү. Темаңызды файл менен которуңуз: «Темалар редактору» → ал кассада «Экспорт», бул жерде «Импорт».",
                "⚠ The custom theme chosen in the settings is not on this till (it was created on another computer), so “Classic” is shown. Move the theme with a file: “Theme editor” → “Export” on that till and “Import” here.",
                "⚠ Ayarlarda seçilen özel tema bu kasada yok (başka bir bilgisayarda oluşturulmuş), bu yüzden «Klasik» gösteriliyor. Temayı dosyayla taşıyın: «Tema düzenleyici» → o kasada «Dışa aktar», burada «İçe aktar».",
                "⚠ Sozlamalarda tanlangan o'z mavzusi bu kassada yo'q (boshqa kompyuterda yaratilgan), shuning uchun «Klassik» ko'rsatilmoqda. Mavzuni fayl bilan ko'chiring: «Mavzu muharriri» → u kassada «Eksport», bu yerda «Import».");
        var selectedText = T("Выбрана", "Тандалган", "Selected", "Seçili", "Tanlangan");
        var themeIds = AccentThemeService.AvailableThemes.Select(t => t.Id).ToList();

        foreach (var theme in AccentThemeService.AvailableThemes)
            items.Add(ThemeItem(theme.Id, theme.Label, theme.Description, false, theme.Id == currentTheme, themeNote, selectedText, dark,
                related: themeIds.Where(i => i != theme.Id).Take(4).Select(i => "theme:" + i).Append("themeeditor").ToList()));

        foreach (var custom in CustomThemeStore.All)
            items.Add(ThemeItem(custom.Id, custom.Name,
                T("Своя тема. Изменить — кнопка «Редактор тем».", "Өз темаңыз. Өзгөртүү — «Темалар редактору» баскычы.", "Your own theme. To change it, use the “Theme editor” button.", "Kendi temanız. Değiştirmek için «Tema düzenleyici» düğmesi.", "O'z mavzuingiz. O'zgartirish — «Mavzu muharriri» tugmasi."),
                true, string.Equals(custom.Id, currentTheme, StringComparison.OrdinalIgnoreCase), themeNote, selectedText, dark,
                related: ["themeeditor"]));

        items.Add(new MarketplaceItem
        {
            Id = "themeeditor",
            Kind = MarketplaceKind.Theme,
            Title = T("Редактор тем", "Темалар редактору", "Theme editor", "Tema düzenleyici", "Mavzu muharriri"),
            ShortDescription = T("Создайте свою тему на основе любой", "Каалаган тема негизинде өз темаңызды түзүңүз", "Create your own theme from any other", "Herhangi birinden kendi temanızı oluşturun", "Istalganidan o'z mavzuingizni yarating"),
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): в редакторе меняется размер шрифта (12–18 px), а не сам шрифт (CustomThemeStore).
            LongDescription = T("Своя тема на основе любой встроенной: 9 цветов отдельно для светлого и тёмного варианта, скругление углов 0–24 px и размер шрифта 12–18 px. Темы можно копировать, удалять и переносить файлом.",
                "Каалаган орнотулган тема негизинде өз темаңыз: ачык жана караңгы вариант үчүн өзүнчө 9 түс, бурчтардын тегеректиги 0–24 px жана шрифттин өлчөмү 12–18 px. Темаларды көчүрүүгө, өчүрүүгө жана файл менен которууга болот.",
                "Your own theme based on any built-in one: 9 colors for light and dark separately, corner rounding 0–24 px and font size 12–18 px. Themes can be copied, deleted and moved as a file.",
                "Herhangi bir yerleşik temaya dayalı kendi temanız: açık ve koyu için ayrı 9 renk, köşe yuvarlaklığı 0–24 px ve yazı boyutu 12–18 px. Temalar kopyalanabilir, silinebilir ve dosyayla taşınabilir.",
                "Istalgan o'rnatilgan mavzu asosida o'z mavzuingiz: yorug' va qorong'i uchun alohida 9 rang, burchak yumaloqligi 0–24 px va shrift o'lchami 12–18 px. Mavzularni nusxalash, o'chirish va fayl bilan ko'chirish mumkin."),
            IconGlyph = "🎨",
            IconColor = "#DB2777",
            Status = MarketplaceStatus.Available,
            Version = AppVersion,
            Tags = [TagLook],
            Features =
            [
                T("Цвета, скругление и размер шрифта на основе любой темы", "Каалаган темага негизделген түстөр, тегеректик жана шрифттин өлчөмү", "Colors, rounding and font size based on any theme", "Herhangi bir temaya dayalı renkler, yuvarlaklık ve yazı boyutu", "Istalgan mavzu asosidagi ranglar, yumaloqlik va shrift o'lchami"),
                T("Свои темы появляются среди тем с пометкой «Своя»", "Өз темаларыңыз темалардын арасында «Өзүңүздүкү» белгиси менен чыгат", "Your themes appear among the themes marked “Custom”", "Kendi temalarınız «Özel» etiketiyle temaların arasında görünür", "O'z mavzularingiz «O'zingizniki» belgisi bilan mavzular orasida paydo bo'ladi"),
            ],
            WhatsNew = T("В Маркетплейсе с 28.09.2026", "Маркетплейсте 28.09.2026 тартып", "In the Marketplace since 28.09.2026", "Marketplace'te 28.09.2026'dan beri", "Marketpleysda 28.09.2026 dan beri"),
            IsNew = true,
            Primary = new MarketplaceAction(T("Открыть редактор тем", "Темалар редакторун ачуу", "Open the theme editor", "Tema düzenleyiciyi aç", "Mavzu muharririni ochish"), () => _run("themeeditor")),
            RelatedIds = themeIds.Take(4).Select(i => "theme:" + i).ToList(),
        });

        MarketplaceSiteThemes.Append(items, _run); // 2026-10-27: темы сайта (в т.ч. 10 тем одежды) — ТЗ «Сайт», С-3
        MarketplaceContent.Enrich(items); // 2026-10-23: группы и (позже) контент страницы «подробно» — отдельным файлом
        return items;
    }

    // ------------------------------------------------------------------ сборка элементов

    /// <summary>Функция, которую открывает серийный ключ. Разблокирована — главное действие «Открыть…» (если есть);
    /// нет — «Активировать — N сом». installedStatus: считать «Установлено» когда разблокирована.</summary>
    private MarketplaceItem Gated(string id, string glyph, string color, string title, string shortD, string longD,
        string[] features, string[] requirements, bool unlocked, MarketplacePrice? price, string? openLabel,
        string hint = "", string[]? tags = null, string statusText = "", bool installedStatus = false, string whatsNew = "",
        bool featured = false, bool isNew = false, string? inline = null, string[]? related = null,
        MarketplaceKind kind = MarketplaceKind.Feature, string note = "")
    {
        var slug = id;
        MarketplaceAction? primary;
        if (unlocked)
            primary = openLabel is null ? null : new MarketplaceAction(openLabel, () => _run("open:" + slug));
        else
            // 2026-10-23, ТЗ «Маркетплейс» (П-1): подпись кнопки — просто «Активировать» (без замка-эмодзи и без цены: цену показывает
            // страница рядом с кнопкой, значок замка рисует сама страница при Status == Paid).
            primary = new MarketplaceAction(
                T("Активировать", "Активдештирүү", "Activate", "Etkinleştir", "Faollashtirish"),
                () => _run("unlock:" + slug));

        MarketplaceStatus status;
        if (!unlocked)
            status = MarketplaceStatus.Paid;
        else
            // 2026-10-24 (приёмка, ПР-2): купленная платная функция без своей подписи (аналитика, ценники, этикетка, табель) — «Куплено»,
            // а не «Доступно» с ценой: иначе плитка рисовала «1500 сом», а страница не писала «Куплено» (риск второй оплаты).
            // Голос/лояльность/языковой пакет/тарифные (свой statusText) не меняются.
            status = installedStatus || (price != null && statusText.Length == 0) ? MarketplaceStatus.Installed : MarketplaceStatus.Available;

        return new MarketplaceItem
        {
            Id = id,
            Kind = kind,
            Title = title,
            ShortDescription = shortD,
            LongDescription = longD,
            // 2026-10-23: note — пояснение, которое видно всегда (hint — только после активации).
            Note = unlocked ? (hint + (hint.Length > 0 && note.Length > 0 ? " " : "") + note) : note,
            IconGlyph = glyph,
            IconColor = color,
            Price = price,
            Status = status,
            StatusText = unlocked
                ? (statusText.Length == 0 && price != null
                    ? T("Куплено", "Сатып алынган", "Purchased", "Satın alındı", "Sotib olingan") // 2026-10-24 (приёмка, ПР-2)
                    : statusText)
                : "",
            Version = AppVersion,
            Tags = tags ?? [],
            Features = features,
            Requirements = requirements,
            WhatsNew = whatsNew,
            IsFeatured = featured,
            IsNew = isNew,
            Primary = primary,
            RelatedIds = related ?? [],
            InlineControlId = inline,
        };
    }

    private MarketplaceItem ThemeItem(string themeId, string label, string description, bool custom, bool isCurrent,
        string note, string selectedText, bool dark, IReadOnlyList<string> related)
    {
        var hex = AccentThemeService.GetAccentHex(themeId) ?? "#FFB208";
        // 2026-10-27 (аудит редакторов, В-7): команда несёт id темы — редактор открывает именно её, а не применённую или первую.
        var secondary = custom
            ? new[] { new MarketplaceAction(T("Изменить в редакторе", "Редактордо өзгөртүү", "Edit in the editor", "Düzenleyicide değiştir", "Muharrirda o'zgartirish"), () => _run("themeeditor:" + themeId)) }
            : Array.Empty<MarketplaceAction>();
        // 2026-10-27 (аудит редакторов, В-8): у своей темы с нечитаемыми цветами — пометка на плитке; применить её можно только через «Всё равно применить».
        var unreadable = custom && CustomThemeStore.Find(themeId) is { } customDef && AccentThemeService.CheckContrastBoth(customDef).Count > 0;
        return new MarketplaceItem
        {
            Id = "theme:" + themeId,
            Kind = MarketplaceKind.Theme,
            Title = label,
            ShortDescription = custom
                ? T("Своя тема", "Өз тема", "Custom theme", "Özel tema", "O'z mavzu") + (unreadable ? " · " + T("⚠ плохо читается", "⚠ начар окулат", "⚠ hard to read", "⚠ zor okunuyor", "⚠ yomon o'qiladi") : "")
                : ShortTheme(description),
            LongDescription = description,
            IconGlyph = "🎨",
            IconColor = hex,
            Status = isCurrent ? MarketplaceStatus.Installed : MarketplaceStatus.Available,
            StatusText = isCurrent ? selectedText : "",
            Version = AppVersion,
            Tags = [TagLook, custom ? T("Своя", "Өзүңүздүкү", "Custom", "Özel", "O'zingizniki") : T("Тема", "Тема", "Theme", "Tema", "Mavzu")],
            Features =
            [
                T("Меняет цвета, форму кнопок и карточек, шрифт и фон окон сразу во всей программе", "Түстөрдү, баскычтардын жана карточкалардын формасын, шрифтти жана терезелердин фонун бүт программада бирдей өзгөртөт", "Changes colors, button and card shapes, font and window backgrounds across the whole program at once", "Renkleri, düğme ve kart şekillerini, yazı tipini ve pencere arka planını tüm programda bir anda değiştirir", "Ranglarni, tugma va kartochkalar shaklini, shriftni va oyna fonini butun dasturda birdan o'zgartiradi"),
                T("Есть светлый и тёмный вариант — переключатель «Тема» в шапке", "Ачык жана караңгы варианты бар — үстүндөгү «Тема» которгучу", "Has a light and a dark variant — the “Theme” switch in the header", "Açık ve koyu varyantı vardır — üstteki «Tema» anahtarı", "Yorug' va qorong'i varianti bor — tepadagi «Mavzu» almashtirgichi"),
                T("Применяется одним нажатием, без перезапуска", "Бир басуу менен колдонулат, кайра ачуунун кереги жок", "Applies in one click, no restart", "Tek tıkla uygulanır, yeniden başlatma gerekmez", "Bir bosish bilan qo'llanadi, qayta ishga tushirish shart emas"),
            ],
            Note = note,
            WhatsNew = themeId == "nurmarket" ? T("Фирменная тема NUR CRM — с 22.10.2026", "NUR CRM фирмалык темасы — 22.10.2026 тартып", "The NUR CRM brand theme — since 22.10.2026", "NUR CRM kurumsal teması — 22.10.2026'dan beri", "NUR CRM firma mavzusi — 22.10.2026 dan beri") : "",
            IsNew = themeId == "nurmarket",
            IsFeatured = themeId is "nurmarket" or "classic",
            Primary = isCurrent
                ? new MarketplaceAction(selectedText, () => { }, false)
                : new MarketplaceAction(T("Применить тему", "Теманы колдонуу", "Apply theme", "Temayı uygula", "Mavzuni qo'llash"), () => _run("theme:" + themeId)),
            Secondary = secondary,
            IconVisual = size => MarketplaceLookPreview.ThemePreview(themeId, dark, size - 8, size - 8, false),
            HeroVisual = () => custom ? MarketplaceLookPreview.CustomThemeHero(themeId, dark) : MarketplaceLookPreview.ThemeHero(themeId, dark),
            RelatedIds = related,
        };
    }

    private static string ShortTheme(string description)
    {
        // Первая фраза описания темы — короткая строка для плитки.
        var cut = description.IndexOfAny(['.', '—', ':']);
        var text = cut > 8 ? description[..cut] : description;
        return text.Length > 60 ? text[..57].TrimEnd() + "…" : text.Trim();
    }

    private static string ShortLayout(string id) => id switch
    {
        KassaLayouts.Standard => T("Каталог слева, чек справа", "Сол жакта каталог, оң жакта чек", "Catalog on the left, receipt on the right", "Solda katalog, sağda fiş", "Chapda katalog, o'ngda chek"),
        KassaLayouts.Table => T("Плотная таблица чека, для клавиатуры", "Чектин тыгыз таблицасы, баскычтоп үчүн", "A dense receipt table, for the keyboard", "Sık fiş tablosu, klavye için", "Chekning zich jadvali, klaviatura uchun"),
        KassaLayouts.Cards => T("Светлые карточки и зелёная «Оплатить»", "Жарык карточкалар жана жашыл «Төлөө»", "Light cards and a green “Pay”", "Aydınlık kartlar ve yeşil «Öde»", "Yorug' kartochkalar va yashil «To'lash»"),
        KassaLayouts.Minimal => T("Много воздуха, крупные плитки", "Көп бош орун, чоң плиткалар", "Lots of space, large tiles", "Bol boşluk, büyük karolar", "Ko'p bo'sh joy, katta plitkalar"),
        KassaLayouts.Pro => T("Боковая панель и крупный поиск", "Каптал панель жана чоң издөө", "A side rail and a large search", "Yan menü ve büyük arama", "Yon panel va katta qidiruv"),
        KassaLayouts.OneC => T("Как «Рабочее место кассира» 1С", "1С «Кассирдин жумуш орду» сыяктуу", "Like the 1C “Cashier workplace”", "1C «Kasiyer çalışma yeri» gibi", "1C «Kassir ish joyi» kabi"),
        _ => "",
    };

    /// <summary>Та же проверка, что была в галерее тем: «Выбрана» — на теме, цвета которой реально включены сейчас.</summary>
    private static string AppliedThemeId(string saved, IEnumerable<string> builtInIds) => MarketplaceView.AppliedThemeId(saved, builtInIds);

    private static string CustomerDisplayText()
    {
        try
        {
            var settings = UserPreferences.Instance.CustomerDisplay;
            var effective = NurMarketKassa.AvaloniaHost.ViewModels.CustomerDisplayViewModel.ResolveStyle(settings.DisplayStyle);
            var name = KassaLayouts.All.FirstOrDefault(o => o.Id == effective)?.Label() ?? effective;
            var follows = string.IsNullOrWhiteSpace(settings.DisplayStyle)
                          || string.Equals(settings.DisplayStyle, CustomerDisplaySettings.StyleAuto, StringComparison.OrdinalIgnoreCase);
            return follows
                ? T($"Второй экран покупателя меняется вместе с видом кассы. Сейчас: «{name}». В редакторе — надписи, цвета и что показывать.",
                    $"Сатып алуучунун экинчи экраны кассанын көрүнүшү менен бирге өзгөрөт. Азыр: «{name}». Редактордо — жазуулар, түстөр жана эмнени көрсөтүү.",
                    $"The customer's second screen changes together with the till layout. Now: “{name}”. The editor sets captions, colors and what to show.",
                    $"Müşterinin ikinci ekranı kasa görünümüyle birlikte değişir. Şu an: «{name}». Düzenleyicide yazılar, renkler ve neyin gösterileceği ayarlanır.",
                    $"Xaridorning ikkinchi ekrani kassa ko'rinishi bilan birga o'zgaradi. Hozir: «{name}». Muharrirda — yozuvlar, ranglar va nimani ko'rsatish.")
                : T($"Второй экран покупателя всегда в виде «{name}» — так выбрано в редакторе.",
                    $"Сатып алуучунун экинчи экраны дайыма «{name}» көрүнүшүндө — редактордо ушундай тандалган.",
                    $"The customer's second screen always uses the “{name}” layout — as chosen in the editor.",
                    $"Müşterinin ikinci ekranı her zaman «{name}» görünümünde — düzenleyicide böyle seçildi.",
                    $"Xaridorning ikkinchi ekrani doim «{name}» ko'rinishida — muharrirda shunday tanlangan.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: текст экрана покупателя: {ex.Message}", "WARNING");
            return "";
        }
    }
}
