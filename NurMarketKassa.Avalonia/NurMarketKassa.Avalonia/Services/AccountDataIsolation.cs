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
public sealed record UnsentSummary(int Sales, decimal SalesTotal, int CashOperations, int DeferredCarts, int ReceivingJobs, int DebtOperations)
{
    public int Count => Sales + CashOperations + DeferredCarts + ReceivingJobs + DebtOperations;
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
/// Ключ — компания, а не кассир: кассиры одной компании делят кассу, историю и склад, и ничего не стирается. Пока компания
/// неизвестна (данные о ней приходят с сервера уже после восстановления сессии), метод ничего не делает.
/// </summary>
public static class AccountDataIsolation
{
    private static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
    ];

    /// <summary>Шаблоны имён в папке данных (флаги «бонусы импортированы» по компаниям, резервные копии настроек с ключами).</summary>
    private static readonly string[] CompanyPatterns = ["loyalty-imported-*.flag", "user-settings.json.bak*"];

    /// <summary>Журналы компании в %LOCALAPPDATA%\NurMarketKassa (возвраты, списания «в минус», удаления из чека).</summary>
    private static readonly string[] LocalLogItems = ["loss-sales.json", "sale-returns.json", "sale-returns.json.bak", "cart-removals.jsonl"];

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
            PosLogger.Log($"Смена аккаунта «{previous}» → «{key}»: неотправленное ({unsent.Count}) {(decision == UnsentDecision.DiscardAndSwitch ? "удалено по решению пользователя" : "выгружено в файл")}.", "AUTH");
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
            WipeCompanyFiles();
            OfflineClientCache.ResetMemory(); // 2026-10-29: кэш клиентов прежней компании не должен остаться в памяти
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
            PosLogger.Log($"Смена аккаунта: данные «{previous}» стёрты, касса работает с «{key}».", "AUTH");
            return true;
        }
        catch (Exception ex)
        {
            // Очистить не удалось — работаем на том, что есть, но громко пишем в журнал:
            // молча смешивать данные двух компаний нельзя.
            PosLogger.Log($"Не удалось очистить данные аккаунта «{previous}» при смене на «{key}»: {ex}", "ERROR");
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

    /// <summary>Стирает все файлы прежней компании. Не удалось стереть что-то одно — пишем в журнал и продолжаем,
    /// в конце бросаем исключение (смена считается неудавшейся и громко отмечена).</summary>
    private static void WipeCompanyFiles()
    {
        var failed = new List<string>();
        foreach (var item in CompanyItems)
            TryDelete(Path.Combine(Root, item), failed);
        if (Directory.Exists(Root))
        {
            foreach (var pattern in CompanyPatterns)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(Root, pattern).ToList())
                        TryDelete(path, failed);
                }
                catch (Exception ex)
                {
                    failed.Add($"{pattern}: {ex.Message}");
                }
            }
        }
        if (RootOverride is null)
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa");
            foreach (var item in LocalLogItems)
                TryDelete(Path.Combine(local, item), failed);
        }
        if (failed.Count > 0)
            throw new IOException("не удалось стереть: " + string.Join("; ", failed));
    }

    private static void TryDelete(string path, List<string> failed)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            failed.Add($"{Path.GetFileName(path)} ({ex.Message})");
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
        var debts = CountJsonEntries(Path.Combine(Root, "pending_debt_prepayments.json"))
                    + CountJsonEntries(Path.Combine(Root, "debt-payments", "debt-payments-unknown.json"))
                    + OfflineOperationsStore.CountUnsent(Path.Combine(Root, "offline_operations.json")); // 2026-10-29: возвраты и оплаты долга без связи
        return new UnsentSummary(sales, total, cash, carts, jobs, debts);
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
                ["warning"] = "Это неотправленные данные прежнего аккаунта кассы NurCRM. Они стёрты из программы и НЕ будут отправлены автоматически. Сохраните файл и передайте поддержке.",
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
                         "offline_operations.json", Path.Combine("debt-payments", "debt-payments-unknown.json") })
            {
                var file = Path.Combine(Root, name);
                if (File.Exists(file))
                    files[name] = File.ReadAllText(file);
            }
            payload["files"] = files;

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
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
        var company = (companyId ?? "").Trim();
        return company.Length > 0 ? "c:" + company : "";
    }
}
