using System;
using System.IO;
using System.Linq;

namespace NurMarketKassa.Services;

/// <summary>
/// Разделяет локальные данные разных учётных записей.
///
/// До этого при смене аккаунта очищался только каталог товаров
/// (<see cref="AccountCatalogIsolation"/>), а всё остальное оставалось общим: история продаж
/// (из неё считаются ABC, сезонность, X/Z-отчёты и «Продажи»), смены, бонусы клиентов,
/// отложенные чеки, внесения/изъятия, непроведённые офлайн-продажи, подписчики телеграм-бота
/// и QR банка. Войдя под вторым аккаунтом, кассир видел вперемешку свои и чужие цифры, а
/// непроведённая продажа чужой компании могла уйти на сервер уже от имени новой.
///
/// Данные НЕ удаляются: набор предыдущей компании откладывается в
/// «accounts\{ключ}» целиком, и при возврате в неё поднимается обратно. Это важно ровно
/// из-за офлайн-продаж — удаление означало бы потерю денег.
///
/// Ключ — компания, а не кассир: кассиры одной компании делят кассу, историю и склад,
/// и разделять их между собой нельзя. Пока компания неизвестна (данные о ней приходят с
/// сервера уже после восстановления сессии), метод ничего не делает — разделение произойдёт
/// сразу, как только компания загрузится.
/// </summary>
public static class AccountDataIsolation
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName);

    private static readonly string ParkRoot = Path.Combine(Root, "accounts");

    /// <summary>Что принадлежит компании, а не этому компьютеру. Настройки кассы
    /// (user-settings.json), сохранённая сессия (auth.dat), модель голоса и журналы
    /// намеренно не трогаются — они про конкретный терминал и переживают смену аккаунта.</summary>
    private static readonly string[] CompanyItems =
    [
        "data",                    // pos_local.db (+ -wal/-shm) и его бэкапы
        "state.json",              // корзины/состояние экрана кассы
        "cash_history.json",       // внесения и изъятия смены
        "deferred_carts.json",     // отложенные чеки
        "offline_pos_state.json",  // очередь непроведённых продаж
        "offline_sales_pending.json",  // старый формат той же очереди: база импортирует его
        "product_thumbs",          // миниатюры товаров
        "BankQr",                  // QR банка — по нему покупатель платит именно этому магазину
        "label-template.json",     // шаблон этикетки
    ];

    /// <summary>Переключает локальные данные на указанную компанию.
    /// Возвращает true, если набор данных реально менялся.</summary>
    /// <param name="companyId">ID компании из NurCRM. Пусто — компания ещё не загружена,
    /// тогда не трогаем ничего: ошибиться ключом здесь хуже, чем подождать.</param>
    public static bool SwitchTo(string? companyId)
    {
        var key = BuildKey(companyId);
        if (string.IsNullOrEmpty(key))
            return false;

        var previous = UserPreferences.Instance.LastDataAccountKey ?? "";
        if (string.Equals(previous, key, StringComparison.OrdinalIgnoreCase))
            return false;

        // Первый запуск после обновления: ключа ещё нет, данные на месте и принадлежат
        // текущей компании. Просто запоминаем, кому они принадлежат, ничего не двигая.
        if (string.IsNullOrEmpty(previous))
        {
            Remember(key);
            return false;
        }

        try
        {
            PrepareForSwap();
            Park(previous);
            Restore(key);
            Remember(key);
            DatabaseService.Instance.ReopenAfterAccountSwitch();
            CatalogCacheService.ClearInMemory();
            PosLogger.Log($"Смена аккаунта: данные «{previous}» отложены, подняты данные «{key}».", "AUTH");
            return true;
        }
        catch (Exception ex)
        {
            // Переключиться не удалось — работаем на том, что есть, но громко пишем в журнал:
            // молча смешивать данные двух компаний нельзя.
            PosLogger.Log($"Не удалось разделить данные аккаунтов «{previous}» → «{key}»: {ex}", "ERROR");
            return false;
        }
    }

    /// <summary>Сбрасывает то, что держит файлы открытыми или может записать данные
    /// прошлой компании уже после подмены.</summary>
    private static void PrepareForSwap()
    {
        NurMarketKassa.ViewModels.Main.ApplicationStateService.CancelPendingSaves();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static void Park(string key)
    {
        var target = Path.Combine(ParkRoot, Sanitize(key));
        Directory.CreateDirectory(target);
        foreach (var item in CompanyItems)
            Move(Path.Combine(Root, item), Path.Combine(target, item));
    }

    private static void Restore(string key)
    {
        var source = Path.Combine(ParkRoot, Sanitize(key));
        if (!Directory.Exists(source))
            return;   // компания новая — начинает с чистого листа

        foreach (var item in CompanyItems)
            Move(Path.Combine(source, item), Path.Combine(Root, item));

        // Набор поднят — пустую папку не держим, иначе «accounts» со временем зарастает
        // каталогами от каждого входа.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(source).Any())
                Directory.Delete(source);
        }
        catch (IOException ex)
        {
            PosLogger.Log($"Пустая папка отложенного набора не удалена: {ex.Message}", "DEBUG");
        }
    }

    /// <summary>Перенос файла или папки. Цель может существовать (например, база успела
    /// пересоздаться пустой) — тогда её убираем, иначе Move бросит исключение.</summary>
    private static void Move(string from, string to)
    {
        var isDirectory = Directory.Exists(from);
        if (!isDirectory && !File.Exists(from))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (isDirectory)
        {
            if (Directory.Exists(to))
                Directory.Delete(to, recursive: true);
            Directory.Move(from, to);
        }
        else
        {
            if (File.Exists(to))
                File.Delete(to);
            File.Move(from, to);
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

    /// <summary>Ключ приходит с сервера, поэтому имя папки строим только из безопасных
    /// символов — иначе чужой идентификатор мог бы увести запись за пределы «accounts».</summary>
    private static string Sanitize(string key)
    {
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return safe.Length > 64 ? safe[..64] : safe;
    }
}
