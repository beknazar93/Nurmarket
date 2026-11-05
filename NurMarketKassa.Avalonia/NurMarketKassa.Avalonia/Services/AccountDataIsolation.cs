using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NurMarketKassa.Services;

/// <summary>Что осталось неотправленным от прежней компании (см. <see cref="AccountDataIsolation.SwitchTo"/>).</summary>
public sealed record UnsentSummary(int Sales, decimal SalesTotal, int CashOperations, int DeferredCarts, int ReceivingJobs, int DebtOperations,
    int UnknownCheckouts = 0, int BonusQueue = 0, int TelegramItems = 0)
{
    // 2026-11-05 (Л4-03/Л4-05): оплата с неизвестным исходом, очередь бонусов и данные Telegram тоже считаются неотправленными.
    public int Count => Sales + CashOperations + DeferredCarts + ReceivingJobs + DebtOperations + UnknownCheckouts + BonusQueue + TelegramItems;
}

/// <summary>Решение пользователя, когда от прежней компании остались неотправленные данные.</summary>
public enum UnsentDecision
{
    /// <summary>Не менять компанию: вернуться в прежний аккаунт (данные уйдут на сервер при его входе).</summary>
    Cancel,
    /// <summary>Выгрузить неотправленное в файл и сменить компанию (данные в программе стираются).</summary>
    ExportAndSwitch,
    /// <summary>Сменить БЕЗ отправки: неотправленное будет потеряно (подтверждено вводом слова).</summary>
    DiscardAndSwitch,
}

/// <summary>
/// Разделяет локальные данные разных учётных записей.
///
/// 2026-10-29, владелец: «ничего не должно остаться от старого аккаунта!!». Раньше (2026-10-05) набор прежней компании откладывался
/// в «accounts\{ключ}» и поднимался при возврате; теперь при смене КОМПАНИИ (не кассира внутри одной компании) всё прежнее стирается:
/// база кассы, корзины, очереди, чеки, ИИ-разговоры, QR банка, напоминания о долге, локальные журналы и настройки (ключи ИИ,
/// платные флаги, темы, бот, принтеры — см. <see cref="CompanyScopedSettings"/>). При возврате в ту компанию её данные заново
/// приходят с сервера.
///
/// Единственное, что нельзя просто стереть, — деньги, ещё не доехавшие до сервера (непроведённые офлайн-продажи, внесения/изъятия,
/// приходы в фоне, отложенные чеки, неподтверждённые оплаты долга). Отправить их уже нельзя: вход новой компании выполнен, а токен
/// прежней уже заменён (отправка ушла бы в новую компанию). Поэтому перед стиранием пользователь выбирает: вернуться в прежний
/// аккаунт (данные уйдут при его входе), выгрузить в файл или потерять осознанно (с вводом слова «УДАЛИТЬ»).
///
/// 2026-11-05 (аудит №4, Л4-01…Л4-05): «стирание» теперь — перенос в <c>_previous\{время}</c> (14 дней), при сбое всё возвращается; вопрос
/// задаётся при любой смене компании; ключ данных включает адрес сервера (стенд ≠ боевой); программа владельца общие журналы кассы не трогает.
///
/// Ключ — компания, а не кассир: кассиры одной компании делят кассу, историю и склад, и ничего не стирается. Пока компания
/// неизвестна (данные о ней приходят с сервера уже после восстановления сессии), метод ничего не делает.
/// </summary>
public static class AccountDataIsolation
{
    // 2026-11-05 (Л4-01): база папок — AppPaths.Roaming (NURMARKET_DATA_ROOT / отдельная папка стенда / %APPDATA%).
    private static string Root => RootOverride ?? Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        NurMarketKassa.Services.AppMode.DataFolderName);

    /// <summary>Только для стенда проверки: другая папка данных вместо %APPDATA% (при ней программные объекты — база, LAN,
    /// каталог — не трогаются).</summary>
    public static string? RootOverride { get; set; }

    /// <summary>Только для стенда проверки: другая папка для файла «неотправленное» вместо «Документы\NurMarket».</summary>
    public static string? ExportDirOverride { get; set; }

    /// <summary>Только для стенда проверки: свой подсчёт неотправленного (вместо базы и хранилищ программы).</summary>
    public static Func<UnsentSummary>? UnsentProbeOverride { get; set; }

    /// <summary>Вопрос пользователю «есть неотправленное — что делать». Задаётся приложением (App.axaml.cs). Не задан — смена
    /// не выполняется (безопаснее потерять вход, чем деньги).</summary>
    public static Func<UnsentSummary, UnsentDecision>? UnsentDecisionHandler { get; set; }

    /// <summary>2026-11-05 (Л4-01): вопрос «Сменить компанию?» даже когда неотправленного нет. Аргументы: прежний и новый ключ. false — отказ
    /// (приложение само выходит из только что выполненного входа). Не задан — смена выполняется (данные уходят в карантин, а не стираются).</summary>
    public static Func<string, string, bool>? CompanyChangeConfirmHandler { get; set; }

    /// <summary>2026-11-05 (Л4-04): данные прежней компании убрать не удалось (файл занят) — всё возвращено на место, вход нужно прервать.</summary>
    public static Action<string>? SwitchFailedHandler { get; set; }

    /// <summary>Папка карантина внутри папки данных: <c>_previous\{время}\</c>. Программа её не читает, через 14 дней она удаляется.</summary>
    public const string PreviousFolderName = "_previous";

    /// <summary>Сколько дней хранится карантинная копия данных прежней компании.</summary>
    public const int PreviousKeepDays = 14;

    /// <summary>Файл «неотправленное» записан (путь) — приложение может показать его пользователю.</summary>
    public static event Action<string>? UnsentExported;

    /// <summary>Что принадлежит компании, а не этому компьютеру: удаляется при смене компании. Сессия (auth.dat) уже принадлежит
    /// вошедшему и не трогается; модель голоса, WebView2 и общий справочник товаров (global-products.sqlite) — про терминал.
    /// Настройки (user-settings.json) не удаляются, а сбрасываются (<see cref="CompanyScopedSettings"/>).</summary>
    private static readonly string[] CompanyItems =
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
        "advisor-morning.json", "cash_articles.json", "catalog_feed_cursor.json", "custom-themes.json",
        "debt-payments", "pending_debt_prepayments.json", "promotion_rules.json", "purchases", "revision", "supplier-debts",
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
        "cycle-count.json",                                   // «Пересчёт дня»: порог PIN и история «когда считали товар» (товары прежней компании)
        "bank-rules.json",                                    // правила сверки банковской выписки (поставщики, названия платежей)
        "imported",                                           // копии файлов, выбранных в «Импорте» (QR банка, фон, реклама), — настройки с путями сбрасываются
        "telegram-reminders.json", "telegram-chat-lang.json", // напоминания и язык чатов бота прежней компании
        // старое место общих файлов (до переноса в %LOCALAPPDATA%): если остались после обновления, тоже уносим
        "shop-alerts-state.json", "shop-automation.json",
    ];

    /// <summary>Шаблоны имён в папке данных (флаги «бонусы импортированы» по компаниям, резервные копии настроек с ключами).</summary>
    private static readonly string[] CompanyPatterns = ["loyalty-imported-*.flag", "user-settings.json.bak*", "telegram-updates-*.json", "bulk-journal-*.json"];

    /// <summary>Журналы компании в %LOCALAPPDATA%\NurMarketKassa (возвраты, списания «в минус», удаления из чека).</summary>
    private static readonly string[] LocalLogItems =
    [
        "loss-sales.json", "sale-returns.json", "sale-returns.json.bak", "cart-removals.jsonl",
        // 2026-11-05 (ревью волн 0–3, п. 6): общие файлы кассы и владельца волн 2-А/2-Б (антифрод, привязки Telegram к чекам, оповещения и автоматика магазина, уведомления об аренде).
        "antifraud-settings.json", "fraud-events.jsonl", "fraud-notified.json", "telegram-receipt-links.json",
        "shop-alerts-state.json", "shop-automation.json", "rental-alerts.json",
    ];

    /// <summary>Пользователь отказался менять компанию: повторный вызов для того же ключа в течение двух минут молча отказывает
    /// (вход ведёт несколько путей подряд — второй вопрос подряд не нужен).</summary>
    private static string _declinedKey = "";
    private static DateTime _declinedAtUtc;

    /// <summary>Компания сменилась, настройки нового аккаунта ещё не подтянуты с сервера (см. <see cref="EnsureAccountDataLoadedAsync"/>).</summary>
    private static volatile bool _awaitingCloud;

    /// <summary>Подсчёт неотправленного от текущего (прежнего) набора данных.</summary>
    public static UnsentSummary GetUnsentSummary() => UnsentProbeOverride?.Invoke() ?? ProbeUnsent();

    /// <summary>Переключает локальные данные на указанную компанию.
    /// Возвращает true, если данные прежней компании стёрты.</summary>
    /// <param name="companyId">ID компании из NurCRM. Пусто — компания ещё не загружена,
    /// тогда не трогаем ничего: ошибиться ключом здесь хуже, чем подождать.</param>
    public static bool SwitchTo(string? companyId)
    {
        var key = BuildKey(companyId);
        if (string.IsNullOrEmpty(key))
            return false;

        var previous = UserPreferences.Instance.LastDataAccountKey ?? "";
        if (string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
        {
            _declinedKey = "";   // вернулись в прежний аккаунт — отказ от смены закончился
            PurgeOldQuarantine();
            return false;
        }

        // Первый запуск после обновления: ключа ещё нет, данные на месте и принадлежат
        // текущей компании. Просто запоминаем, кому они принадлежат, ничего не двигая.
        if (string.IsNullOrEmpty(previous))
        {
            Remember(key);
            return false;
        }

        if (string.Equals(_declinedKey, key, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _declinedAtUtc < TimeSpan.FromMinutes(2))
            return false;

        // 2026-10-29: неотправленное — сначала решение пользователя. Без него (или без обработчика) ничего не стираем.
        UnsentSummary unsent;
        try
        {
            unsent = GetUnsentSummary();
        }
        catch (Exception ex)
        {
            // Не смогли посчитать — считаем, что неотправленное может быть, и спрашиваем без цифр.
            PosLogger.Log($"Смена аккаунта: неотправленное не посчитано ({ex.Message}).", "ERROR");
            unsent = new UnsentSummary(0, 0, 0, 0, 0, 1);
        }

        if (unsent.Count > 0)
        {
            var decision = UnsentDecisionHandler?.Invoke(unsent) ?? UnsentDecision.Cancel;
            if (decision == UnsentDecision.ExportAndSwitch)
            {
                var path = ExportUnsent(previous);
                if (path is null)
                    decision = UnsentDecision.Cancel;   // файл не записался — деньги не отдаём
                else
                    TryRaiseExported(path);
            }
            if (decision == UnsentDecision.Cancel)
            {
                _declinedKey = key;
                _declinedAtUtc = DateTime.UtcNow;
                PosLogger.Log($"Смена аккаунта «{previous}» → «{key}» отклонена: есть неотправленное ({unsent.Count}).", "AUTH");
                return false;
            }
            PosLogger.Log($"Смена аккаунта «{previous}» → «{key}»: неотправленное ({unsent.Count}) {(decision == UnsentDecision.DiscardAndSwitch ? "убрано по решению пользователя" : "выгружено в файл")}.", "AUTH");
        }
        else if (CompanyChangeConfirmHandler is { } confirm && !confirm(previous, key))
        {
            // 2026-11-05 (Л4-01): раньше при нуле неотправленного данные прежней компании стирались молча — теперь всегда спрашиваем.
            _declinedKey = key;
            _declinedAtUtc = DateTime.UtcNow;
            PosLogger.Log($"Смена аккаунта «{previous}» → «{key}» отклонена пользователем.", "AUTH");
            return false;
        }

        // Обмен по локальной сети держит базу и знает компанию — на время очистки выключаем,
        // потом запускаем уже с новой компанией.
        var standMode = RootOverride is not null;
        var lan = standMode ? null : NurMarketKassa.Services.Lan.LanSyncService.Instance;
        var lanWasRunning = lan?.IsRunning ?? false;
        try
        {
            lan?.Stop();
            if (!standMode)
                PrepareForSwap();
            QuarantineCompanyFiles();   // 2026-11-05 (Л4-01/Л4-04): не стираем, а переносим в _previous\{время}; сбой — всё возвращается назад
            OfflineClientCache.ResetMemory(); // 2026-10-29: кэш клиентов прежней компании не должен остаться в памяти
            NurMarketKassa.Services.Api.ServerCapabilities.Reset(); // 2026-11-01: признаки сервера прежнего аккаунта (receipt_edit и др.) не должны жить в новом
            NurMarketKassa.AvaloniaHost.Services.PhotoSearchMemory.ResetMemory(); // 2026-11-03: память поиска фото прежней компании (файл уже стёрт)
            ServerAddons.ResetMemory(); // 2026-10-31: функции, включённые сервером прежней компании, не должны открывать функции новой
            SettingsCloudSync.RunAccountReset(() =>
            {
                CompanyScopedSettings.ResetForAccountSwitch(UserPreferences.Instance);
                UserPreferences.Instance.LastDataAccountKey = key;
                UserPreferences.Instance.SaveToDisk();
            });
            _awaitingCloud = true;
            if (!standMode)
            {
                NurMarketKassa.Services.BackgroundReceivingQueue.Instance.DiscardAllForAccountSwitch();
                DatabaseService.Instance.ReopenAfterAccountSwitch();
                NurMarketKassa.Services.Lan.LanJournal.ResetSchemaFlag();
                AccountCatalogIsolation.ResetCatalogCachesAndReload();
            }
            PosLogger.Log($"Смена аккаунта: данные «{previous}» убраны в {PreviousFolderName} (хранятся {PreviousKeepDays} дн.), касса работает с «{key}».", "AUTH");
            PurgeOldQuarantine();
            return true;
        }
        catch (Exception ex)
        {
            // Очистить не удалось — работаем на том, что есть, но громко пишем в журнал:
            // молча смешивать данные двух компаний нельзя.
            PosLogger.Log($"Не удалось убрать данные аккаунта «{previous}» при смене на «{key}» (всё возвращено на место): {ex}", "ERROR");
            try { SwitchFailedHandler?.Invoke(ex.Message); }
            catch (Exception ex2) { PosLogger.Log($"Смена аккаунта: сообщение о сбое не показано ({ex2.Message}).", "WARNING"); }
            return false;
        }
        finally
        {
            if (lanWasRunning)
                lan!.Start();
        }
    }

    /// <summary>2026-10-29: после смены компании — дождаться, пока настройки нового аккаунта (ИИ, тариф, платные функции, коды)
    /// придут с сервера, прежде чем открывать главное окно. Нет связи — false, платные функции остаются закрытыми,
    /// настройки подтянутся сами, как только связь появится.</summary>
    public static async Task<bool> EnsureAccountDataLoadedAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!_awaitingCloud)
            return true;
        progress?.Report(Tr.T("Загружаем данные аккаунта…", "Аккаунттын маалыматтары жүктөлүүдө…", "Loading account data…", "Hesap verileri yükleniyor…", "Akkaunt ma'lumotlari yuklanmoqda…"));
        bool ok;
        try
        {
            ok = await SettingsCloudSync.WaitAccountSettingsAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (ok)
        {
            _awaitingCloud = false;
            return true;
        }
        progress?.Report(Tr.T("Данные аккаунта ещё не загружены — платные функции закрыты", "Аккаунттын маалыматтары али жүктөлө элек — акы төлөнүүчү функциялар жабык",
            "Account data is not loaded yet — paid features are locked", "Hesap verileri henüz yüklenmedi — ücretli özellikler kapalı", "Akkaunt ma'lumotlari hali yuklanmagan — pullik funksiyalar yopiq"));
        PosLogger.Log("Смена аккаунта: настройки нового аккаунта с сервера не получены — платные функции закрыты до получения.", "WARNING");
        return false;
    }

    private static void TryRaiseExported(string path)
    {
        try { UnsentExported?.Invoke(path); }
        catch (Exception ex) { PosLogger.Log($"Смена аккаунта: сообщение о файле не показано ({ex.Message}).", "WARNING"); }
    }

    /// <summary>Сбрасывает то, что держит файлы открытыми или может записать данные
    /// прошлой компании уже после очистки.</summary>
    private static void PrepareForSwap()
    {
        NurMarketKassa.ViewModels.Main.ApplicationStateService.CancelPendingSaves();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>2026-11-05 (аудит №4, Л4-01/Л4-04): вместо стирания каждая вещь переносится в <c>_previous\{время}\</c> (на одном диске это
    /// мгновенное переименование). Если хоть одна не переносится (файл занят антивирусом, замок программы владельца, WebView) — всё уже
    /// перенесённое возвращается назад и бросается исключение: смесь двух компаний не остаётся, сброс настроек не выполняется.
    /// Общие с кассой журналы в %LOCALAPPDATA%\NurMarketKassa убирает только касса — программа владельца их не трогает (Л4-02).</summary>
    private static void QuarantineCompanyFiles()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var moved = new List<(string From, string To)>();
        var failed = new List<string>();

        var items = CompanyItems.Select(item => Path.Combine(Root, item)).ToList();
        if (Directory.Exists(Root))
        {
            foreach (var pattern in CompanyPatterns)
            {
                try
                {
                    items.AddRange(Directory.EnumerateFileSystemEntries(Root, pattern));
                }
                catch (Exception ex)
                {
                    failed.Add($"{pattern}: {ex.Message}");
                }
            }
        }
        MoveAside(items, Path.Combine(Root, PreviousFolderName, stamp), moved, failed);

        if (RootOverride is null && !AppMode.IsOwner)
        {
            // Журналы возвратов/списаний/удалений из чека лежат в папке КАССЫ (общей по замыслу); путь тот же, что у SaleReturnLedger и др.
            var local = Path.Combine(NurMarketKassa.Services.AppPaths.Local, "NurMarketKassa");
            MoveAside(LocalLogItems.Select(item => Path.Combine(local, item)), Path.Combine(local, PreviousFolderName, stamp), moved, failed);
        }

        if (failed.Count == 0)
            return;
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(moved[i].To))
                    Directory.Move(moved[i].To, moved[i].From);
                else if (File.Exists(moved[i].To))
                    File.Move(moved[i].To, moved[i].From);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена аккаунта: не удалось вернуть «{moved[i].From}» из {PreviousFolderName} ({ex.Message}) — файл ищите в {moved[i].To}.", "ERROR");
            }
        }
        throw new IOException("не удалось убрать: " + string.Join("; ", failed));
    }

    private static void MoveAside(IEnumerable<string> paths, string destinationDir, List<(string From, string To)> moved, List<string> failed)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var isDirectory = Directory.Exists(path);
                if (!isDirectory && !File.Exists(path))
                    continue;
                Directory.CreateDirectory(destinationDir);
                var target = Path.Combine(destinationDir, Path.GetFileName(path));
                // 2026-11-05 (ревью волн 0–3, п. 8): файл на мгновение может держать фоновая запись (синхронизация, очередь) — 3 попытки с паузой 300 мс,
                // между ними закрываем свободные соединения базы; иначе вход в новую компанию отменялся из-за случайной записи.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (isDirectory)
                            Directory.Move(path, target);
                        else
                            File.Move(path, target);
                        break;
                    }
                    catch (Exception retryEx) when (attempt < 3 && retryEx is IOException or UnauthorizedAccessException)
                    {
                        PosLogger.Log($"Смена аккаунта: «{Path.GetFileName(path)}» занят ({retryEx.Message}), попытка {attempt} из 3.", "WARNING");
                        Thread.Sleep(300);
                        if (RootOverride is null)
                        {
                            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch (Exception) { /* пул — удобство, не причина отказа */ }
                        }
                    }
                }
                moved.Add((path, target));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }
    }

    /// <summary>Удаляет наборы в <c>_previous</c> старше <see cref="PreviousKeepDays"/> дней. Время берётся из имени папки, иначе из даты создания.</summary>
    public static void PurgeOldQuarantine()
    {
        var bases = new List<string> { Root };
        if (RootOverride is null && !AppMode.IsOwner)
            bases.Add(Path.Combine(NurMarketKassa.Services.AppPaths.Local, "NurMarketKassa"));
        foreach (var baseDir in bases)
        {
            try
            {
                var previous = Path.Combine(baseDir, PreviousFolderName);
                if (!Directory.Exists(previous))
                    continue;
                foreach (var dir in Directory.EnumerateDirectories(previous))
                {
                    var name = Path.GetFileName(dir);
                    var created = DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : Directory.GetCreationTime(dir);
                    if (DateTime.Now - created > TimeSpan.FromDays(PreviousKeepDays))
                        Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Карантин {PreviousFolderName}: очистка старых наборов не выполнена ({ex.Message}).", "WARNING");
            }
        }
    }

    // ---------------------------------------------------------------- неотправленное

    private static UnsentSummary ProbeUnsent()
    {
        var sales = 0;
        decimal total = 0;
        foreach (var sale in OfflinePendingSalesStore.LoadAll().Where(IsUnsentSale))
        {
            sales++;
            total += SaleTotal(sale.CartJson);
        }

        var cash = 0;
        foreach (var shiftId in ShiftCashOperationsStore.PendingShiftIds())
            cash += ShiftCashOperationsStore.PendingForShift(shiftId).Count;

        var carts = DeferredCartsStore.Count();
        var jobs = BackgroundReceivingQueue.Instance.Snapshot(allCompanies: true)
            .Count(j => j.IsActive || j.Status is ReceivingJobStatus.Failed or ReceivingJobStatus.NeedsCheck or ReceivingJobStatus.Partial);
        // 2026-11-05 (Л4-03): журнал неподтверждённых оплат долга ОБЩИЙ для кассы и владельца (%APPDATA%\NurMarketKassa\debt-payments),
        // у владельца Root другой — читаем общий путь, иначе его «исход неизвестен» не попадал в вопрос.
        var debts = CountJsonEntries(Path.Combine(Root, "pending_debt_prepayments.json"))
                    + CountJsonEntries(Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, "NurMarketKassa", "debt-payments", "debt-payments-unknown.json"))
                    + OfflineOperationsStore.CountUnsent(Path.Combine(Root, "offline_operations.json")); // 2026-10-29: возвраты и оплаты долга без связи
        // 2026-11-05 (Л4-05): оплата с неизвестным исходом (возможно, проведённая продажа), очередь бонусов и данные Telegram — не стираем молча.
        var unknownCheckout = File.Exists(Path.Combine(Root, "pending_unknown_checkout.json")) ? 1 : 0;
        var bonus = CountJsonEntries(Path.Combine(Root, "loyalty-queue.json"));
        var telegram = CountJsonEntries(Path.Combine(Root, "telegram-inquiries.json")) + CountDictionaryEntries(Path.Combine(Root, "telegram-customers.json"));
        return new UnsentSummary(sales, total, cash, carts, jobs, debts, unknownCheckout, bonus, telegram);
    }

    private static bool IsUnsentSale(OfflineSaleEntry s) =>
        !s.IsAutonomous && !string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase);

    private static decimal SaleTotal(string? cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            decimal sum = 0;
            foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(doc.RootElement))
                if (decimal.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                    sum += value;
            return sum;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountJsonEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Array => root.GetArrayLength(),
                JsonValueKind.Object => root.EnumerateObject().Sum(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.GetArrayLength() : 0),
                _ => 0,
            };
        }
        catch
        {
            return new FileInfo(path).Length > 4 ? 1 : 0;   // не разобрали — но файл не пустой: считаем неотправленным
        }
    }

    private static int CountDictionaryEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Object => doc.RootElement.EnumerateObject().Count(),
                JsonValueKind.Array => doc.RootElement.GetArrayLength(),
                _ => 0,
            };
        }
        catch
        {
            return new FileInfo(path).Length > 4 ? 1 : 0;
        }
    }

    /// <summary>Записывает неотправленное в «Документы\NurMarket\unsent-{компания}-{дата}.json». null — не удалось
    /// (тогда смена не выполняется).</summary>
    private static string? ExportUnsent(string companyKey)
    {
        try
        {
            var dir = ExportDirOverride;
            if (string.IsNullOrEmpty(dir))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                dir = Path.Combine(string.IsNullOrEmpty(docs) ? Root : docs, "NurMarket");
            }
            Directory.CreateDirectory(dir);
            var safe = new string(companyKey.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var path = Path.Combine(dir, $"unsent-{(safe.Length > 40 ? safe[..40] : safe)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var payload = new Dictionary<string, object?>
            {
                ["warning"] = "Это неотправленные данные прежнего аккаунта кассы NurCRM. Они убраны из программы (копия лежит в папке _previous 14 дней) и НЕ будут отправлены автоматически. Сохраните файл и передайте поддержке.",
                ["company"] = companyKey,
                ["exportedAt"] = DateTimeOffset.Now,
            };
            if (RootOverride is null)
            {
                payload["offlineSales"] = OfflinePendingSalesStore.LoadAll().Where(IsUnsentSale).ToList();
                payload["receivingJobs"] = BackgroundReceivingQueue.Instance.Snapshot(allCompanies: true).ToList();
            }
            var files = new Dictionary<string, string>();
            foreach (var name in new[] { "deferred_carts.json", "cash_history.json", "pending_debt_prepayments.json", "offline_sales_pending.json",
                         "offline_operations.json", "pending_unknown_checkout.json", "loyalty-queue.json", "telegram-inquiries.json", "telegram-customers.json",
                         Path.Combine("debt-payments", "debt-payments-unknown.json") })
            {
                var file = Path.Combine(Root, name);
                if (File.Exists(file))
                    files[name] = File.ReadAllText(file);
            }
            payload["files"] = files;

            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); // 2026-11-01 (Н2-01/Н-07): сброс на диск + копия .bak
            PosLogger.Log($"Смена аккаунта: неотправленное записано в файл {path}.", "AUTH");
            return path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смена аккаунта: файл «неотправленное» не записан ({ex.Message}).", "ERROR");
            return null;
        }
    }

    private static void Remember(string key)
    {
        UserPreferences.Instance.LastDataAccountKey = key;
        UserPreferences.Instance.SaveToDisk();
    }

    private static string BuildKey(string? companyId)
    {
        // 2026-11-05 (Л4-01): ключ = адрес сервера + компания. Боевой сервер — префикса нет («c:{id}», как раньше); стенд/заглушка — «хост|c:{id}».
        return NurMarketKassa.Services.AppPaths.CompanyKey(companyId);
    }
}
