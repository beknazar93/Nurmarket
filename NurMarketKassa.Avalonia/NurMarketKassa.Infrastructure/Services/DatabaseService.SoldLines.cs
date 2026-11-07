using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса DatabaseService (разбор огромного файла на partial-файлы), логика не менялась; остальное — DatabaseService.cs
public sealed partial class DatabaseService
{
    #region SoldLineItems

    /// <summary>ID компании, которой принадлежит текущая сессия. Пока она не загружена,
    /// возвращает null — новые строки тогда пишутся без владельца, как и раньше.</summary>
    private static string? CurrentCompanyId()
    {
        var id = CompanyInfoService.LastCompany?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>2026-11-06: В5 К-1 — сколько номеров чеков в одном запросе IN (…) (лимит SQLite на параметры — 32 766).</summary>
    private const int SaleIdBatchSize = 500;

    /// <summary>Условие «только строки текущей компании» для запросов истории продаж.
    ///
    /// Строки без владельца (company_id IS NULL) — это записи, сделанные до разделения данных
    /// аккаунтов. Они могли остаться от другой компании: у пользователя в ABC показывались
    /// товары чужого каталога. Отличить среди них свои невозможно, поэтому в аналитику они
    /// не идут — но и не удаляются, данные остаются на месте.
    ///
    /// Пока компания неизвестна (нет связи с сервером на старте), ничего не фильтруем: иначе
    /// касса без интернета показывала бы пустые отчёты вместо своих.
    ///
    /// 2026-11-06: В5 Б-5 — компания передаётся ПАРАМЕТРОМ <c>$ownCompany</c> команды, а не вставляется в текст запроса литералом
    /// (раньше идентификатор компании с сервера подставлялся в SQL строкой; экранирование кавычки было единственной защитой).
    /// <paramref name="skipIndex"/> — знак «+» перед колонкой запрещает планировщику взять индекс (company_id, product_name):
    /// в запросах по номеру чека он перебирал бы всю историю компании (300 тыс. строк: 2505 мс вместо 2 мс), см. аудит скорости 05.11.2026.</summary>
    private static string OwnRowsClause(SqliteCommand command, bool skipIndex = false)
    {
        var company = CurrentCompanyId();
        if (company is null)
            return "";
        if (!command.Parameters.Contains("$ownCompany"))
            command.Parameters.AddWithValue("$ownCompany", company);
        return skipIndex ? " AND +company_id = $ownCompany" : " AND company_id = $ownCompany";
    }

    /// <summary>Номер продажи в строках истории. По нему история двух касс одного аккаунта
    /// сливается без дублей: раньше строку было не с чем сопоставить, и подтянуть продажи с
    /// сервера можно было только в пустую базу.</summary>
    private static void AddSoldLineItemsSaleIdColumn(SqliteConnection connection)
    {
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE SoldLineItems ADD COLUMN sale_id TEXT;";
            alter.ExecuteNonQuery();
            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_sold_line_items_sale ON SoldLineItems(sale_id);";
            index.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже есть.
        }
    }

    /// <summary>Себестоимость единицы на момент списания — для отчёта «на сколько списали»
    /// (2026-09-24). У старых записей её нет: отчёт берёт текущую цену закупки товара.</summary>
    private static void AddWriteOffsUnitCostColumn(SqliteConnection connection)
    {
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WriteOffHistory ADD COLUMN unit_cost REAL;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже есть.
        }
    }

    /// <summary>То же, что у истории продаж: списания тоже относятся к конкретной компании,
    /// и записи, сделанные до разделения аккаунтов, владельца не имеют.</summary>
    private static void AddWriteOffsCompanyColumn(SqliteConnection connection)
    {
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WriteOffHistory ADD COLUMN company_id TEXT;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже есть.
        }
    }

    /// <summary>Индексы по компании — строго ПОСЛЕ добавления самой колонки.
    ///
    /// 2026-09-24, касса владельца перестала запускаться с «SQLite Error 1: no such column:
    /// company_id». Эти два индекса стояли в общем скрипте схемы, который выполняется одной
    /// командой и ДО ALTER TABLE. На базе, созданной прежними версиями, таблица SoldLineItems
    /// уже существует (CREATE TABLE IF NOT EXISTS её не трогает) и колонки company_id в ней
    /// нет — CREATE INDEX по несуществующей колонке валил весь скрипт, а вместе с ним и запуск
    /// кассы. На свежей базе ошибки не было, поэтому при проверке здесь она и не всплыла.</summary>
    private static void AddCompanyHistoryIndexes(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_company_sold
                    ON SoldLineItems(company_id, sold_at);
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_company_name
                    ON SoldLineItems(company_id, product_name COLLATE NOCASE);
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // Без индексов аналитика работает, просто медленнее — запуск кассы важнее.
            PosLogger.Log($"Индексы истории по компании не созданы: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Колонка владельца в уже существующих базах: CREATE TABLE IF NOT EXISTS её
    /// не добавит.</summary>
    private static void AddSoldLineItemsCompanyColumn(SqliteConnection connection)
    {
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE SoldLineItems ADD COLUMN company_id TEXT;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже есть — обычное дело для баз этой версии схемы.
        }
    }

    /// <summary>Записывает проданные позиции чека — по одной строке на товар. Источник данных для
    /// прогноза пополнения склада (AI-фичи 2026-09-03, п.1). <paramref name="source"/> — "local"
    /// (записано сразу после продажи, см. BasketPanelViewModel) или "backfill" (подтянуто с сервера
    /// один раз, см. RestockSuggestionsWindow) — нужно, чтобы бэкфилл не задваивал уже локально
    /// записанные строки.</summary>
    /// <summary>Дата самой поздней строки истории БЕЗ номера продажи.
    ///
    /// Такие строки писали версии до появления номера: сопоставить их с чеком на сервере
    /// нечем. Если бэкфилл не остановить, он подтянет те же продажи заново, и вся аналитика
    /// задвоится. Поэтому всё, что старше этой даты, считаем уже учтённым, а сливаем истории
    /// касс начиная с неё. Null — старых строк нет, ограничивать нечего.</summary>
    public DateTime? GetLegacyHistoryWatermark()
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(sold_at) FROM SoldLineItems WHERE (sale_id IS NULL OR sale_id = '')"
                + OwnRowsClause(command) + ";";
            var raw = command.ExecuteScalar() as string;
            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
                ? value.ToUniversalTime()
                : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отсечка старой истории не прочитана: {ex.Message}", "WARNING");
            return null;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>История одного товара — запросом, а не фильтром в памяти.
    ///
    /// Разбор товара раньше читал ВСЮ историю продаж и отбирал нужные строки уже в коде: на
    /// 300 тыс. строк это 1179 мс и триста тысяч лишних объектов, причём в UI-потоке. С
    /// индексом (company_id, product_name) тот же ответ приходит мгновенно.</summary>
    public List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>
        LoadSoldLineItemsForProduct(string productName)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadSoldLineItemsForProduct)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        var result = new List<(string, string, double, double, DateTime)>();
        if (string.IsNullOrWhiteSpace(productName))
            return result;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // Без ORDER BY намеренно: с сортировкой по дате планировщик выбирает индекс по
            // (company_id, sold_at) и перебирает всю историю компании — замер на 300 тыс.
            // строк давал 147 мс против 0 мс. Строк одного товара немного, порядок задаёт
            // вызывающий код.
            command.CommandText =
                "SELECT product_id, product_name, quantity, unit_price, sold_at FROM SoldLineItems "
                + "WHERE product_name = $name COLLATE NOCASE" + OwnRowsClause(command) + ";";
            var pName = command.CreateParameter();
            pName.ParameterName = "$name";
            pName.Value = productName;
            command.Parameters.Add(pName);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTime.TryParse(reader.GetString(4), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var soldAt))
                    continue;

                result.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    soldAt.ToUniversalTime()));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История товара не прочитана: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>Есть ли уже строки этой продажи. Нужно бэкфиллу: продажу, сделанную на этой
    /// же кассе, второй раз писать нельзя.</summary>
    /// <summary>Номера продаж локальной истории, проданных в промежутке [sinceUtc, untilUtc).</summary>
    public HashSet<string> LoadKnownSaleIdsBetween(DateTime sinceUtc, DateTime untilUtc)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadKnownSaleIdsBetween)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT sale_id FROM SoldLineItems WHERE sale_id IS NOT NULL AND sale_id <> '' "
                + "AND sold_at >= $since AND sold_at < $until" + OwnRowsClause(command) + ";";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            command.Parameters.AddWithValue("$until", untilUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список продаж за период не прочитан: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>2026-11-03 (Д-3, живая проверка 1.17.73): строки продаж, записанные ЭТОЙ кассой (source = local) с номером продажи, за промежуток —
    /// чтобы «История чеков» показала свежие чеки сразу, не дожидаясь сервера. Старые первыми не гарантированы; порядок строк — как записаны.</summary>
    public List<(string SaleId, DateTime SoldAtUtc, string ProductName, double Quantity, double UnitPrice)> LoadLocalSoldLinesBetween(DateTime sinceUtc, DateTime untilUtc)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadLocalSoldLinesBetween)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        var result = new List<(string, DateTime, string, double, double)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sale_id, sold_at, product_name, quantity, unit_price FROM SoldLineItems WHERE source = 'local' "
                + "AND sale_id IS NOT NULL AND sale_id <> '' AND sold_at >= $since AND sold_at < $until" + OwnRowsClause(command) + " ORDER BY rowid;";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            command.Parameters.AddWithValue("$until", untilUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var soldAt = DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                result.Add((reader.GetString(0), soldAt, reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4)));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Свои продажи кассы за период не прочитаны: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>Убрать из истории строки перечисленных продаж. Возвращает число удалённых строк.</summary>
    public int DeleteSoldLineItemsBySaleIds(IReadOnlyCollection<string> saleIds)
    {
        if (saleIds.Count == 0)
            return 0;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var removed = 0;
            foreach (var saleId in saleIds)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM SoldLineItems WHERE sale_id = $id" + OwnRowsClause(command) + ";";
                command.Parameters.AddWithValue("$id", saleId);
                removed += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed;
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public HashSet<string> LoadKnownSaleIds()
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadKnownSaleIds)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // 2026-11-06: В5 К-2 (шаг 3) — «+company_id»: без него планировщик брал индекс (company_id, product_name) и строил DISTINCT по всей истории
            // (300 тыс. строк: ~3 с под замком чтения каждые 30 минут). Результат тот же, замер на копии — в «Волна 1 — В5 замеры».
            // LoadKnownSaleIdsBetween вместо полного списка НЕ берём: офлайн-чек пишется с временем продажи (до трёх суток назад), а сервер ставит created_at при
            // выгрузке — окно по дате пропустило бы такой чек и задвоило бы его строки в истории.
            command.CommandText = "SELECT DISTINCT sale_id FROM SoldLineItems WHERE sale_id IS NOT NULL AND sale_id <> ''"
                + OwnRowsClause(command, skipIndex: true) + ";";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список известных продаж не прочитан: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    public void AppendSoldLineItems(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines, string source, string? saleId = null)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(AppendSoldLineItems)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SoldLineItems (product_id, product_name, quantity, unit_price, sold_at, source, company_id, sale_id)
                VALUES ($productId, $productName, $quantity, $unitPrice, $soldAt, $source, $companyId, $saleId);
                """;
            var pProductId = command.CreateParameter(); pProductId.ParameterName = "$productId"; command.Parameters.Add(pProductId);
            var pProductName = command.CreateParameter(); pProductName.ParameterName = "$productName"; command.Parameters.Add(pProductName);
            var pQuantity = command.CreateParameter(); pQuantity.ParameterName = "$quantity"; command.Parameters.Add(pQuantity);
            var pUnitPrice = command.CreateParameter(); pUnitPrice.ParameterName = "$unitPrice"; command.Parameters.Add(pUnitPrice);
            var pSoldAt = command.CreateParameter(); pSoldAt.ParameterName = "$soldAt"; command.Parameters.Add(pSoldAt);
            var pSource = command.CreateParameter(); pSource.ParameterName = "$source"; command.Parameters.Add(pSource);
            pSource.Value = source;
            var pCompany = command.CreateParameter(); pCompany.ParameterName = "$companyId"; command.Parameters.Add(pCompany);
            pCompany.Value = (object?)CurrentCompanyId() ?? DBNull.Value;
            var pSale = command.CreateParameter(); pSale.ParameterName = "$saleId"; command.Parameters.Add(pSale);
            pSale.Value = string.IsNullOrWhiteSpace(saleId) ? DBNull.Value : saleId;

            foreach (var line in lines)
            {
                pProductId.Value = line.ProductId;
                pProductName.Value = line.ProductName;
                pQuantity.Value = line.Quantity;
                pUnitPrice.Value = line.UnitPrice;
                pSoldAt.Value = line.SoldAt.ToString("O");
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Все проданные позиции с указанной даты (включительно) — используется для расчёта
    /// скорости продаж товара при построении подсказок пополнения склада.</summary>
    /// <summary>То же, что <see cref="LoadSoldLineItemsSince"/>, но с ЦЕНОЙ, по которой товар
    /// реально продали. Для выручки цена из каталога не годится: поштучная продажа из пачки
    /// уходит по своей цене (390 шт по 12 сом = 4 680), а в каталоге у того же товара стоит
    /// цена пачки — выручка по ней завышалась в разы.</summary>
    public List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>
        LoadSoldLineItemsWithPriceSince(DateTime sinceUtc, DateTime? untilUtc = null)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadSoldLineItemsWithPriceSince)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, string, double, double, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = untilUtc is null
                ? "SELECT product_id, product_name, quantity, unit_price, sold_at FROM SoldLineItems WHERE sold_at >= $since" + OwnRowsClause(command) + ";"
                : "SELECT product_id, product_name, quantity, unit_price, sold_at FROM SoldLineItems WHERE sold_at >= $since AND sold_at < $until" + OwnRowsClause(command) + ";";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            if (untilUtc is { } until)
                command.Parameters.AddWithValue("$until", until.ToString("O"));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var soldAt = DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), soldAt));
            }

            return list;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SoldLineItems with price read failed: {ex.Message}", "WARNING");
            return new List<(string, string, double, double, DateTime)>();
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>2026-10-01, «Умная допродажа»: состав чеков (номер продажи + товар) с даты — по
    /// нему считаются пары «что берут вместе». Строки без номера продажи (старая история) не годятся:
    /// их нельзя собрать в чеки.</summary>
    public List<(string SaleId, string ProductId)> LoadSaleBaskets(DateTime sinceUtc)
    {
        var list = new List<(string, string)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sale_id, product_id FROM SoldLineItems WHERE sold_at >= $since AND sale_id IS NOT NULL AND sale_id <> '' AND quantity > 0"
                + OwnRowsClause(command) + ";";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1)));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SoldLineItems baskets read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return list;
    }

    /// <summary>Событие подсказки допродажи: shown / accepted / skipped.</summary>
    public void AppendUpsellEvent(string evt, string productId, string? productName, double price, string? triggerProductId, double score, string? cartKey)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO UpsellEvents (event, product_id, product_name, price, trigger_product_id, score, cart_key, company_id, created_at)
                VALUES ($event, $productId, $name, $price, $trigger, $score, $cart, $companyId, $at);
                """;
            command.Parameters.AddWithValue("$event", evt);
            command.Parameters.AddWithValue("$productId", productId);
            command.Parameters.AddWithValue("$name", (object?)productName ?? DBNull.Value);
            command.Parameters.AddWithValue("$price", price);
            command.Parameters.AddWithValue("$trigger", (object?)triggerProductId ?? DBNull.Value);
            command.Parameters.AddWithValue("$score", score);
            command.Parameters.AddWithValue("$cart", (object?)cartKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$companyId", (object?)CurrentCompanyId() ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UpsellEvents write failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>После оплаты: всем событиям чека проставить номер продажи.</summary>
    public void LinkUpsellEventsToSale(string cartKey, string saleId)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE UpsellEvents SET sale_id = $sale WHERE cart_key = $cart AND (sale_id IS NULL OR sale_id = '');";
            command.Parameters.AddWithValue("$sale", saleId);
            command.Parameters.AddWithValue("$cart", cartKey);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UpsellEvents link failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>2026-10-05, ТЗ часть 7, п. 2.2: события допродажи этой компании с номером строки больше afterId —
    /// для отправки на сервер пачками (UpsellServerSync).</summary>
    public List<(long Id, string Event, string ProductId, double Price, string? TriggerProductId, double Score, string? CartKey, string? SaleId, string CreatedAt)>
        LoadUpsellEventsAfter(long afterId, int limit)
    {
        var list = new List<(long, string, string, double, string?, double, string?, string?, string)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, event, product_id, COALESCE(price, 0), trigger_product_id, COALESCE(score, 0), cart_key, sale_id, created_at "
                + "FROM UpsellEvents WHERE id > $after" + OwnRowsClause(command) + " ORDER BY id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8)));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UpsellEvents read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
        return list;
    }

    /// <summary>Итоги допродажи за период: показано, добавлено, пропущено и выручка добавленных
    /// товаров, которые остались в оплаченном чеке (по строкам продажи, не по цене подсказки).</summary>
    public (int Shown, int Accepted, int Skipped, double Revenue) GetUpsellStats(DateTime sinceUtc, DateTime untilUtc)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var own = OwnRowsClause(command); // 2026-11-06: В5 Б-5 — компания параметром, не литералом
            command.CommandText = "SELECT SUM(CASE WHEN event = 'shown' THEN 1 ELSE 0 END), SUM(CASE WHEN event = 'accepted' THEN 1 ELSE 0 END), "
                + "SUM(CASE WHEN event = 'skipped' THEN 1 ELSE 0 END) FROM UpsellEvents WHERE created_at >= $from AND created_at < $to" + own + ";";
            command.Parameters.AddWithValue("$from", sinceUtc.ToString("O"));
            command.Parameters.AddWithValue("$to", untilUtc.ToString("O"));
            int shown = 0, accepted = 0, skipped = 0;
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    shown = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    accepted = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    skipped = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                }
            }

            using var revenue = connection.CreateCommand();
            revenue.CommandText = "SELECT COALESCE(SUM(s.quantity * s.unit_price), 0) FROM (SELECT DISTINCT sale_id, product_id FROM UpsellEvents "
                + "WHERE event = 'accepted' AND sale_id IS NOT NULL AND sale_id <> '' AND created_at >= $from AND created_at < $to" + OwnRowsClause(revenue) + ") e "
                + "JOIN SoldLineItems s ON s.sale_id = e.sale_id AND s.product_id = e.product_id;";
            revenue.Parameters.AddWithValue("$from", sinceUtc.ToString("O"));
            revenue.Parameters.AddWithValue("$to", untilUtc.ToString("O"));
            var sum = Convert.ToDouble(revenue.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture);
            return (shown, accepted, skipped, sum);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UpsellEvents stats failed: {ex.Message}", "WARNING");
            return (0, 0, 0, 0);
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public List<(string ProductId, string ProductName, double Quantity, DateTime SoldAt)> LoadSoldLineItemsSince(DateTime sinceUtc)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadSoldLineItemsSince)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, string, double, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_id, product_name, quantity, sold_at FROM SoldLineItems WHERE sold_at >= $since" + OwnRowsClause(command) + ";";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var soldAt = DateTime.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), soldAt));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Самая ранняя дата, за которую уже есть записи продаж (любого источника) —
    /// используется, чтобы бэкфилл с сервера не дублировал то, что уже записано локально.</summary>
    public DateTime? GetEarliestSoldLineItemDate()
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(sold_at) FROM SoldLineItems WHERE 1=1" + OwnRowsClause(command) + ";";
            var raw = command.ExecuteScalar() as string;
            return !string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region Migrations

    /// <summary>Одноразовый перенос БД из старого расположения (рядом с exe) в новое
    /// (%AppData%, см. комментарий у DataDirectory) — иначе после первого автообновления на
    /// версию с этим фиксом непроведённые офлайн-продажи и локальный кэш оказались бы в
    /// старой (уже замененной Velopack-ом) папке версии, невидимой новой сборке. Ничего не
    /// делает, если в новом месте уже есть БД (перенос уже случился раньше или это чистая
    /// установка) или если рядом с exe ничего нет.</summary>
    private static void MigrateFromOldInstallDir()
    {
        try
        {
            if (File.Exists(DbPath) || !File.Exists(OldInstallDirDbPath))
                return;

            File.Copy(OldInstallDirDbPath, DbPath);
            PosLogger.Log($"Перенесена локальная БД из {OldInstallDirDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перенос локальной БД из старого расположения не удался: {ex}", "DB");
        }
    }

    private static void MigrateLegacyCatalogDb(SqliteConnection targetConnection)
    {
        if (!File.Exists(LegacyCatalogDbPath))
            return;

        try
        {
            using var countCmd = targetConnection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Products;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            using var legacy = new SqliteConnection($"Data Source={LegacyCatalogDbPath}");
            legacy.Open();

            using (var copyProducts = targetConnection.CreateCommand())
            {
                copyProducts.CommandText = """
                    ATTACH DATABASE $legacy AS legacy_db;
                    INSERT OR IGNORE INTO Products
                        (id, barcode, name, price, stock, unit, is_favorite, must_weigh, image_url, category, brand, purchase_price)
                    SELECT id, barcode, name, price, stock, unit, is_favorite, must_weigh, image_url, category, brand, purchase_price
                    FROM legacy_db.local_products;
                    DETACH DATABASE legacy_db;
                    """;
                copyProducts.Parameters.AddWithValue("$legacy", LegacyCatalogDbPath);
                copyProducts.ExecuteNonQuery();
            }

            using (var copyMeta = targetConnection.CreateCommand())
            {
                copyMeta.CommandText = """
                    ATTACH DATABASE $legacy AS legacy_db;
                    INSERT OR IGNORE INTO catalog_meta (key, value)
                    SELECT key, value FROM legacy_db.catalog_meta;
                    DETACH DATABASE legacy_db;
                    """;
                copyMeta.Parameters.AddWithValue("$legacy", LegacyCatalogDbPath);
                copyMeta.ExecuteNonQuery();
            }

            PosLogger.Log($"Мигрирован каталог из {LegacyCatalogDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция catalog.db: {ex.Message}", "DB");
        }
    }

    private static void MigrateLegacyOfflineDb(SqliteConnection targetConnection)
    {
        if (!File.Exists(LegacyOfflineDbPath))
            return;

        try
        {
            using var countCmd = targetConnection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM OfflineSales;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            using var copy = targetConnection.CreateCommand();
            copy.CommandText = """
                ATTACH DATABASE $legacy AS legacy_db;
                INSERT OR IGNORE INTO OfflineSales (id, created_at, receipt_number, json_data, sync_status, is_synced)
                SELECT id, created_at, receipt_number, json_data, sync_status,
                       CASE WHEN sync_status = 'synced' THEN 1 ELSE 0 END
                FROM legacy_db.pending_sales;
                DETACH DATABASE legacy_db;
                """;
            copy.Parameters.AddWithValue("$legacy", LegacyOfflineDbPath);
            copy.ExecuteNonQuery();

            PosLogger.Log($"Мигрирована офлайн-очередь из {LegacyOfflineDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция offline.db: {ex.Message}", "DB");
        }
    }

    private static void MigrateLegacyJsonSales(SqliteConnection connection)
    {
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM OfflineSales;";
        if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
            return;

        var legacyPath = Path.Combine(
            NurMarketKassa.Services.AppPaths.Roaming,
            NurMarketKassa.Services.AppMode.DataFolderName,
            "offline_sales_pending.json");
        if (!File.Exists(legacyPath))
            return;

        try
        {
            var legacy = JsonSerializer.Deserialize<List<OfflineSaleEntry>>(File.ReadAllText(legacyPath), JsonOpts)
                         ?? new List<OfflineSaleEntry>();
            foreach (var entry in legacy)
                UpsertOfflineSale(connection, null!, entry);

            var backup = legacyPath + ".migrated";
            File.Move(legacyPath, backup, overwrite: true);
            PosLogger.Log($"Мигрировано {legacy.Count} офлайн-чеков из JSON в SQLite", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция offline_sales_pending.json: {ex.Message}", "DB");
        }
    }

    private static void MigrateUserPreferencesCredentials(SqliteConnection connection)
    {
        try
        {
            var prefs = UserPreferences.Instance;
            if (string.IsNullOrWhiteSpace(prefs.LastLoginEmail)
                || string.IsNullOrWhiteSpace(prefs.LastLoginPassword))
                return;

            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Users;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            var encrypted = WindowsDpapiHelper.ProtectToBase64(prefs.LastLoginPassword);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Users (email, password_encrypted, remember_me, last_login_at)
                VALUES ($email, $password, 1, $lastLogin);
                """;
            insert.Parameters.AddWithValue("$email", prefs.LastLoginEmail.Trim());
            insert.Parameters.AddWithValue("$password", encrypted);
            insert.Parameters.AddWithValue("$lastLogin", DateTimeOffset.Now.ToString("O"));
            insert.ExecuteNonQuery();

            prefs.LastLoginPassword = "";
            prefs.SaveToDisk();
            PosLogger.Log("Мигрированы учётные данные из user-settings.json в SQLite Users", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция учётных данных: {ex.Message}", "DB");
        }
    }

    #endregion
}
