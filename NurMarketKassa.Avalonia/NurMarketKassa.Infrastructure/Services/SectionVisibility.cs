namespace NurMarketKassa.Services;

/// <summary>2026-10-05, владелец: «в настройках сделай скрытие вкладок, чтобы клиент мог скрыть ненужные функции».
/// Разделы меню программы владельца и пункты меню кассы, которые можно убрать (Настройки → Экран → «Разделы меню»).
/// «Сводку», «Настройки» и выход скрыть нельзя — иначе раздел не вернуть. Скрытие только убирает пункт из меню:
/// тариф и права сотрудника по-прежнему решают, доступен ли раздел вообще.</summary>
public static class SectionVisibility
{
    public static event Action? Changed;

    /// <summary>2026-10-05: скрытые разделы пришли с сервера (SettingsCloudSync) — меню пересобирается.</summary>
    public static void RaiseChanged() => Changed?.Invoke();

    public static bool IsHidden(string key) =>
        UserPreferences.Instance.HiddenSections.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static void Set(string key, bool hidden)
    {
        var list = UserPreferences.Instance.HiddenSections;
        var changed = hidden
            ? !list.Contains(key, StringComparer.OrdinalIgnoreCase) && Add(list, key)
            : list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!changed)
            return;
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log($"Разделы меню: «{key}» {(hidden ? "скрыт" : "показан")}.", "UI");
        Changed?.Invoke();
    }

    private static bool Add(List<string> list, string key)
    {
        list.Add(key);
        return true;
    }

    /// <summary>Разделы для текущей программы: ключ и название (на языке интерфейса).</summary>
    public static IReadOnlyList<(string Key, string Title)> Catalog => AppMode.IsOwner ? OwnerCatalog() : KassaCatalog();

    private static List<(string, string)> OwnerCatalog() => new()
    {
        ("aiadvisor", Tr.T("ИИ-советник", "ИИ-кеңешчи", "AI advisor", "Yapay zekâ danışmanı", "SI maslahatchi")),
        ("warehouse", Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor")),
        ("calculator", Tr.T("Калькуляция", "Калькуляция", "Pricing calculator", "Hesaplama", "Kalkulyatsiya")),
        ("restock", Tr.T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar")),
        ("sales", Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar")),
        ("finance", Tr.T("Финансы", "Каржы", "Finance", "Finans", "Moliya")),
        ("analytics", Tr.T("Аналитика", "Талдоо", "Analytics", "Analiz", "Analitika")),
        ("abc", Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC-tahlil")),
        ("profitcash", Tr.T("Прибыль и деньги", "Пайда жана акча", "Profit & cash", "Kâr ve nakit", "Foyda va pul")),
        ("supplierreturns", Tr.T("Возвраты поставщикам", "Жеткирүүчүлөргө кайтаруулар", "Returns to suppliers", "Tedarikçiye iadeler", "Yetkazib beruvchilarga qaytarishlar")),
        ("branches", Tr.T("Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar")),
        ("losssales", Tr.T("Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar")),
        ("sizesreport", Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colours", "Bedenler ve renkler", "O'lchamlar va ranglar")),
        ("debts", Tr.T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari")),
        ("siteorders", Tr.T("Заказы с сайта", "Сайттан заказдар", "Website orders", "Web sitesi siparişleri", "Saytdan buyurtmalar")),
        ("siteeditor", Tr.T("Редактор сайта", "Сайттын редактору", "Website editor", "Web sitesi düzenleyici", "Sayt muharriri")),
        ("appshop", Tr.T("Магазин в приложении", "Тиркемедеги дүкөн", "Shop in the app", "Uygulamadaki mağaza", "Ilovadagi do'kon")),
        ("sitesettings", Tr.T("Настройки сайта", "Сайттын жөндөөлөрү", "Website settings", "Web sitesi ayarları", "Sayt sozlamalari")),
        ("clients", Tr.T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar")),
        ("telegrambot", Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot")),
        ("rentals", Tr.T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat")),
        ("salary", Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi")),
        ("crm", "NurCRM"),
        ("whatsapp", "WhatsApp"),
        ("funnel", Tr.T("Воронка", "Воронка", "Sales funnel", "Satış hunisi", "Savdo voronkasi")),
        ("marketplace", Tr.T("Маркетплейс", "Маркетплейс", "Marketplace", "Pazar yeri", "Marketpleys")),
        ("kb", Tr.T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi")),
        ("support", Tr.T("Тех. поддержка", "Тех колдоо", "Support", "Destek", "Texnik yordam")),
        ("logs", Tr.T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali")),
    };

    private static List<(string, string)> KassaCatalog() => new()
    {
        ("kassa.return", Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish")),
        ("kassa.deferred", Tr.T("Отложенные чеки", "Кийинкиге калтырылган чектер", "Parked receipts", "Bekletilen fişler", "Kechiktirilgan cheklar")),
        ("kassa.paydebt", Tr.T("Оплата долга", "Карызды төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash")),
        ("kassa.rental", Tr.T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat")),
        ("kassa.timesheet", Tr.T("Табель сотрудников", "Кызматкерлердин табели", "Staff timesheet", "Personel puantajı", "Xodimlar tabeli")),
        ("kassa.warehouse", Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor")),
        ("kassa.restock", Tr.T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar")),
        ("kassa.sales", Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar")),
        ("kassa.finance", Tr.T("Финансы", "Каржы", "Finance", "Finans", "Moliya")),
        ("kassa.salary", Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi")),
        ("kassa.clients", Tr.T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar")),
        ("kassa.crm", "NurCRM"),
        ("kassa.marketplace", Tr.T("Маркетплейс", "Маркетплейс", "Marketplace", "Pazar yeri", "Marketpleys")),
        ("kassa.kb", Tr.T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi")),
        ("kassa.support", Tr.T("Тех. поддержка", "Тех колдоо", "Support", "Destek", "Texnik yordam")),
        ("kassa.logs", Tr.T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali")),
    };
}
