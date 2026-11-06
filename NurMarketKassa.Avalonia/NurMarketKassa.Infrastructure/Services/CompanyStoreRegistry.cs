using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В3 Л4-05 — реестр хранилищ: чьи это файлы в папках данных кассы и владельца.
/// Раньше списки «что принадлежит компании» жили вручную внутри AccountDataIsolation и расходились с реальными файлами (аудит №4, Л4-05).
/// Теперь они здесь, рядом с описанием остальных видов, и проверяются тестом (Tests\Wave3) и скриптом tools/qa/check-store-registry.ps1:
/// файл данных, которого нет в реестре, — ошибка проверки. Новое хранилище = строка в одном из списков ниже.
///
/// Виды (<see cref="StoreKind"/>):
/// <list type="bullet">
/// <item><b>Company</b> — данные компании; при смене компании уносятся в карантин (<c>_previous</c>).</item>
/// <item><b>Terminal</b> — про этот компьютер (сессия, настройки, голосовая модель, WebView, общий справочник); при смене компании остаются.
/// Настройки (user-settings.json) не удаляются, а сбрасываются по списку CompanyScopedSettings.</item>
/// <item><b>SharedByCompanyField</b> — общий файл кассы и владельца, где у каждой записи есть поле «компания»; не уносится целиком (там могут лежать
/// и записи владельца, и записи, нужные при возврате в прежнюю компанию).</item>
/// </list></summary>
public static class CompanyStoreRegistry
{
    /// <summary>Чьё хранилище.</summary>
    public enum StoreKind
    {
        /// <summary>Реестр такого файла не знает — его нужно добавить в один из списков.</summary>
        Unknown,
        Company,
        Terminal,
        SharedByCompanyField,
    }

    /// <summary>Что принадлежит компании, а не этому компьютеру: уносится в карантин при смене компании (папка данных %APPDATA%\NurMarketKassa|NurMarketOwner).
    /// Сессия (auth.dat) уже принадлежит вошедшему и не трогается; модель голоса, WebView2 и общий справочник товаров (global-products.sqlite) — про терминал.
    /// Настройки (user-settings.json) не удаляются, а сбрасываются (CompanyScopedSettings).</summary>
    public static readonly string[] CompanyItems =
    [
        "data",                    // pos_local.db (+ -wal/-shm) и его бэкапы
        "state.json",              // корзины/состояние экрана кассы
        "state.json.bak",          // 2026-10-29: копия прошлой записи (AtomicFile), иначе чужие корзины «воскреснут» при порче
        "cash_history.json",       // внесения и изъятия смены
        "cash_history.json.bak",   // 2026-10-29: то же для истории касс
        "exchange_cash_fix.json",  // 2026-10-06: поправки остатка смены после обмена (ошибка сервера)
        "variant_barcodes.json",   // 2026-10-06: штрихкоды размеров одежды (скан этикетки размера)
        "sale_sizes_cache.json",   // 2026-10-06: запомненные строки чеков для отчёта «Размеры и цвета»
        "deferred_carts.json",     // отложенные чеки
        "offline_pos_state.json",  // очередь непроведённых продаж
        "offline_sales_pending.json",  // старый формат той же очереди: база импортирует его
        "product_thumbs",          // миниатюры товаров
        "BankQr",                  // QR банка — по нему покупатель платит именно этому магазину
        "label-template.json",     // шаблон этикетки
        "ai-chats.json",           // 2026-10-05: прошлые разговоры с ИИ-советником (данные магазина)
        // 2026-10-29: остальное, что лежит в папке данных и принадлежит компании (список по содержимому живой папки кассы).
        "debt-reminders.json", "debt-reminders.json.bak",     // напоминания о долге: расписание, исключённые клиенты, журнал
        "background-receipts",                                // приходы в фоне
        "pending-products.json",                              // 2026-11-06: очередь создания новых товаров, заведённых без интернета (PendingProductQueue) — товары прежней компании
        "advisor-morning.json", "cash_articles.json", "catalog_feed_cursor.json", "custom-themes.json",
        // 2026-11-06: В3 Л4-03 — "debt-payments" УБРАНО отсюда: журнал неподтверждённых оплат долга общий для кассы и программы владельца, у каждой записи есть
        // компания (см. SharedByCompanyItems). Раньше касса уносила папку целиком — вместе с записями и замком владельца (замок мог не дать сменить компанию,
        // а унесённая запись «исход неизвестен» после возврата в ту компанию не защищала от второго списания).
        "pending_debt_prepayments.json", "promotion_rules.json", "purchases", "revision", "supplier-debts",
        "rental-receipts.json", "upsell-sync.json", "client-docs", "funnel.json", "label-scales.json", "receiving-markup.json",
        "salary_payouts.json", "startup-costs", "error-reports.json", "price-tag-template.json", "bonus-branch-rule.json",
        "profit-pay", "barcode-print-settings.json", "phone-print-settings.json", "menu-badges-seen.txt",
        "backups",                                            // копии общих настроек компании (settings-migration)
        // 2026-10-29: возврат / оплата долга без связи и кэш клиентов для «в долг» — данные компании; неотправленные операции учтены в UnsentSummary.
        "offline_operations.json", "offline_clients.json",
        // 2026-10-31: кэш платных функций, включённых компании на сервере (ServerAddons), — принадлежит компании.
        "server-addons.json", "server-addons.json.bak",
        // 2026-11-03: память поиска фото пачками (какие товары компании уже проверены) — принадлежит компании.
        "photo-search-progress.json", "photo-search-progress.json.bak",
        // 2026-11-03 (аудит №3, Т3-04): кэш списка архивных товаров (10 минут) — принадлежит компании.
        "archive-products-cache.json",
        // 2026-11-05 (аудит №4, Л4-05): хранилища, которых не было в списке; три первых — «неотправленное» (см. ProbeUnsent).
        "pending_unknown_checkout.json", "loyalty-queue.json", "loyalty-queue.json.bak", "server_prices.json",
        "telegram-inquiries.json", "telegram-inquiries.json.bak", "telegram-customers.json", "telegram-customers.json.bak", "telegram-owner.json",
        // 2026-11-05 (ревью волн 0–3, п. 6): хранилища волн 2-А/2-Б (список по Grep AppPaths в новых файлах) — иначе новая компания видела бы чужие данные.
        "exchange_cash_fix.archived.json",                    // 2026-11-06 (Л5-03): архив старых поправок «Касса:» (ExchangeCashCorrections) — данные компании
        "cycle-count.json",                                   // «Пересчёт дня»: порог PIN и история «когда считали товар» (товары прежней компании)
        "bank-rules.json",                                    // правила сверки банковской выписки (поставщики, названия платежей)
        "imported",                                           // копии файлов, выбранных в «Импорте» (QR банка, фон, реклама), — настройки с путями сбрасываются
        "suppliers-cache.json", "suppliers-cache.json.bak",   // 2026-11-06 (Б-5): память списка поставщиков (PurchaseReceivingService) — поставщики прежней компании не должны показаться новой
        "telegram-reminders.json", "telegram-chat-lang.json", // напоминания и язык чатов бота прежней компании
        // старое место общих файлов (до переноса в %LOCALAPPDATA%): если остались после обновления, тоже уносим
        "shop-alerts-state.json", "shop-automation.json",
    ];

    /// <summary>Шаблоны имён в папке данных (флаги «бонусы импортированы» по компаниям, резервные копии настроек с ключами).</summary>
    public static readonly string[] CompanyPatterns = ["loyalty-imported-*.flag", "user-settings.json.bak*", "telegram-updates-*.json", "bulk-journal-*.json",
        // 2026-11-06 (аудит №5, Л5-03): запасные файлы отложенной записи (AtomicFile) — иначе при следующем запуске RecoverPending вернёт состояние прежней компании на место рабочего файла.
        "*.pending", "*.pending.old"];

    /// <summary>Журналы компании в %LOCALAPPDATA%\NurMarketKassa (возвраты, списания «в минус», удаления из чека).</summary>
    public static readonly string[] CompanyLocalItems =
    [
        "loss-sales.json", "sale-returns.json", "sale-returns.json.bak", "cart-removals.jsonl",
        "pending-money-keys.json",                           // 2026-11-06: В2 Л5-14 — ключи повтора денежных POST без ответа (PendingMoneyKeys): ключ прежней компании не должен лечь на операцию новой
        // 2026-11-05 (ревью волн 0–3, п. 6): общие файлы кассы и владельца волн 2-А/2-Б (антифрод, привязки Telegram к чекам, оповещения и автоматика магазина, уведомления об аренде).
        "antifraud-settings.json", "fraud-events.jsonl", "fraud-notified.json", "telegram-receipt-links.json",
        "shop-alerts-state.json", "shop-automation.json", "rental-alerts.json",
        // 2026-11-06 (аудит №5, Л5-11): порог PIN ревизии и история пересчётов теперь общий файл кассы и владельца (история — товары прежней компании).
        "cycle-count.json", "cycle-count.json.bak",
    ];

    /// <summary>2026-11-06: В3 Л4-05 — общие файлы кассы и владельца с полем «компания» у каждой записи: при смене компании не уносятся целиком.
    /// Папка debt-payments: журнал неподтверждённых оплат долга + межпроцессный замок (ключ записи — компания|клиент).
    /// pending_shift_closes.json: очередь закрытий смен; чужую смену SyncService не трогает (сверяет CompanyId).</summary>
    public static readonly string[] SharedByCompanyItems = ["debt-payments", "pending_shift_closes.json", "pending_shift_closes.json.bak"];

    /// <summary>2026-11-06: В3 Л4-05 — всё, что в папке данных относится к компьютеру и при смене компании остаётся.</summary>
    public static readonly string[] TerminalItems =
    [
        "auth.dat", "auth.dat.bak",                                  // вошедший пользователь (сессия)
        "user-settings.json", "user-settings.json.before-restore", // настройки терминала (компанийные поля сбрасываются отдельно; копии user-settings.json.bak* уносятся — см. CompanyPatterns)
        "VoiceModel", "WebView2", "global-products.sqlite",          // модель голоса, профиль WebView2, общий справочник товаров
        "accounts", "local_accounts",                                // прежний каталог accounts\{ключ} и автономные аккаунты этого ПК
        "Logs", "device-key.txt", "owner-shortcut.created", "server-capabilities.json", "server-capabilities.json.bak", // %LOCALAPPDATA%\NurMarketKassa
        PreviousFolderName,                                          // сам карантин
    ];

    /// <summary>Папка карантина внутри папки данных (то же имя, что у AccountDataIsolation.PreviousFolderName).</summary>
    public const string PreviousFolderName = "_previous";

    private static readonly string[] Tails = [".bak", ".pending", ".pending.old", ".tmp"];

    /// <summary>Вид хранилища по имени файла или папки (без пути). Учитывает хвосты <c>.bak</c>, <c>.pending</c>, <c>.pending.old</c>, <c>.tmp</c>.</summary>
    public static StoreKind Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return StoreKind.Unknown;
        var fileName = Path.GetFileName(name.Trim().TrimEnd('\\', '/'));
        foreach (var candidate in WithoutTails(fileName))
        {
            if (Has(CompanyItems, candidate) || CompanyPatterns.Any(p => Matches(p, candidate)))
                return StoreKind.Company;
            if (Has(CompanyLocalItems, candidate))
                return StoreKind.Company;
            if (Has(SharedByCompanyItems, candidate))
                return StoreKind.SharedByCompanyField;
            if (Has(TerminalItems, candidate))
                return StoreKind.Terminal;
        }
        return StoreKind.Unknown;
    }

    private static IEnumerable<string> WithoutTails(string fileName)
    {
        yield return fileName;
        foreach (var tail in Tails)
            if (fileName.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && fileName.Length > tail.Length)
                yield return fileName[..^tail.Length];
    }

    private static bool Has(string[] list, string name) => list.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool Matches(string pattern, string name)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
