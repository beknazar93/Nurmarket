using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.GlobalProducts;

/// <summary>Одна запись справочника готовых наименований NurCRM (фото в справочнике нет).</summary>
public sealed record GlobalProductInfo(string Barcode, string Name, string Brand, string Category);

/// <summary>Строка состояния справочника для настроек: сколько товаров, на какую дату выгрузка, сколько добавлено с сервера.</summary>
public sealed record GlobalCatalogStatus(bool Available, long Count, string ExportedAt, long AddedFromServer);

/// <summary>2026-10-27, просьба владельца: «встрой справочник готовых наименований в программу и обновляй из сервера, если появилось
/// новое наименование». На сервере NurCRM есть общий справочник (128 тыс. товаров: штрихкод, название, бренд, категория). Его выгрузка от
/// 26.10.2026 лежит в программе (Assets\data\global-products.sqlite), при первом обращении копируется в
/// %APPDATA%\&lt;NurMarketKassa|NurMarketOwner&gt;\global-products.sqlite и ищется по штрихкоду мгновенно, без интернета.
///
/// Файл лежит в корне папки данных, а НЕ в data\ рядом с pos_local.db: AccountDataIsolation при смене компании откладывает папку data
/// целиком (а открытое соединение мешало бы переносу), справочник же общий для всех компаний.
///
/// Нового штрихкода нет в копии — касса один раз спрашивает сервер (GET products/global-barcode/{код}/), найденное дописывает в копию,
/// «не найден» запоминает на 24 часа. Ничего из этого не должно ломать кассу: любая ошибка работы с файлом пишется в журнал
/// (GLOBAL_PRODUCTS) и поиск просто отвечает «не нашёл». Ошибки СЕРВЕРА (нет связи, 429) наружу пробрасываются как раньше:
/// вызывающие различают «нет в базе» и «не проверено» (массовое добавление).</summary>
public sealed class GlobalProductCatalog
{
    /// <summary>Источник встроенной копии. Ставит приложение (ресурс Avalonia — Infrastructure о нём не знает); без него справочник
    /// работает как пустой и пополняется только с сервера.</summary>
    public static Func<Stream?>? EmbeddedProvider { get; set; }

    private const string Category = "GLOBAL_PRODUCTS";
    private static readonly TimeSpan MissingTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryOpenAfter = TimeSpan.FromMinutes(10);

    /// <summary>Не больше одного запроса к серверу одновременно (сервер NurCRM режет частые запросы 429).</summary>
    private static readonly SemaphoreSlim ServerGate = new(1, 1);

    private static readonly object InstanceGate = new();
    private static GlobalProductCatalog? _instance;

    /// <summary>Единственный справочник программы. Setter — только для стенда проверки (подмена пути, чтобы не трогать профиль кассы).</summary>
    public static GlobalProductCatalog Instance
    {
        get { lock (InstanceGate) return _instance ??= new GlobalProductCatalog(DefaultPath()); }
        set { lock (InstanceGate) _instance = value; }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName, "global-products.sqlite");

    private readonly string _path;
    private readonly Func<Stream?>? _embedded;
    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private SqliteCommand? _findCmd;
    private DateTime _noRetryUntilUtc = DateTime.MinValue;
    private bool _mergeScheduled;

    /// <summary>Часы (для проверки 24 часов в стенде).</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public GlobalProductCatalog(string databasePath, Func<Stream?>? embedded = null)
    {
        _path = databasePath;
        _embedded = embedded;
    }

    public string DatabasePath => _path;

    // ------------------------------------------------------------------ штрихкод

    /// <summary>Убирает пробелы и переводы строк внутри и по краям кода.</summary>
    public static string Normalize(string? barcode)
    {
        if (string.IsNullOrEmpty(barcode))
            return "";
        var sb = new System.Text.StringBuilder(barcode.Length);
        foreach (var ch in barcode)
            if (!char.IsWhiteSpace(ch) && ch != '​' && ch != '﻿')
                sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Мусорные коды старого импорта («!», «:», «$^)#@…») касса не сканирует: нужна хотя бы одна цифра и длина 6–14.</summary>
    public static bool IsPlausible(string? normalized)
    {
        if (normalized is null || normalized.Length < 6 || normalized.Length > 14)
            return false;
        foreach (var ch in normalized)
            if (ch is >= '0' and <= '9')
                return true;
        return false;
    }

    /// <summary>Тот же код в других записях: EAN-13 с ведущим нулём и без (UPC-A 12 цифр), GTIN-14 с нулём.</summary>
    public static List<string> Variants(string normalized)
    {
        var list = new List<string> { normalized };
        if (normalized.Length == 14 && normalized[0] == '0' && normalized.All(char.IsDigit))
            list.Add(normalized[1..]);
        foreach (var code in list.ToArray())
            if (BarcodeForms.Alternate(code) is { } alt && !list.Contains(alt))
                list.Add(alt);
        return list;
    }

    // ------------------------------------------------------------------ поиск в локальной копии

    /// <summary>Мгновенный поиск по штрихкоду (запрос по первичному ключу). Не нашёл, мусорный код или ошибка файла — null.</summary>
    public GlobalProductInfo? Find(string? barcode)
    {
        try
        {
            var n = Normalize(barcode);
            if (!IsPlausible(n))
                return null;
            lock (_gate)
            {
                if (EnsureReady() is null || _findCmd is null)
                    return null;
                foreach (var code in Variants(n))
                {
                    _findCmd.Parameters["$b"].Value = code;
                    using var r = _findCmd.ExecuteReader();
                    if (r.Read())
                        return new GlobalProductInfo(r.GetString(0), r.GetString(1).Trim(),
                            r.IsDBNull(2) ? "" : r.GetString(2).Trim(), r.IsDBNull(3) ? "" : r.GetString(3).Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Fail("поиск", ex);
        }
        return null;
    }

    /// <summary>Сервер уже отвечал «нет такого штрихкода» за последние 24 часа.</summary>
    public bool IsKnownMissing(string? barcode)
    {
        try
        {
            var n = Normalize(barcode);
            if (n.Length == 0)
                return false;
            lock (_gate)
            {
                if (EnsureReady() is not { } conn)
                    return false;
                var limit = ToUnix(UtcNow() - MissingTtl);
                foreach (var code in Variants(n))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT at FROM missing WHERE barcode=$b";
                    cmd.Parameters.AddWithValue("$b", code);
                    if (cmd.ExecuteScalar() is long at && at > limit)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            Fail("проверка «не найден»", ex);
        }
        return false;
    }

    /// <summary>Запомнить «сервер ответил 404» (код и его варианты) на 24 часа.</summary>
    public void MarkMissing(string? barcode)
    {
        try
        {
            var n = Normalize(barcode);
            if (n.Length == 0 || n.Length > 64)
                return;
            lock (_gate)
            {
                if (EnsureReady() is not { } conn)
                    return;
                using var tx = conn.BeginTransaction();
                var now = ToUnix(UtcNow());
                foreach (var code in Variants(n))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO missing(barcode, at) VALUES($b, $t)";
                    cmd.Parameters.AddWithValue("$b", code);
                    cmd.Parameters.AddWithValue("$t", now);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            Fail("запись «не найден»", ex);
        }
    }

    /// <summary>Дописать в копию товар, найденный на сервере (INSERT OR REPLACE). true — это новая запись, раньше её не было.</summary>
    public bool Remember(string? barcode, string? name, string? brand, string? category)
    {
        try
        {
            var n = Normalize(barcode);
            var title = name?.Trim() ?? "";
            if (!IsPlausible(n) || title.Length == 0)
                return false;
            lock (_gate)
            {
                if (EnsureReady() is not { } conn)
                    return false;
                using var tx = conn.BeginTransaction();
                bool existed;
                using (var check = conn.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT 1 FROM products WHERE barcode=$b";
                    check.Parameters.AddWithValue("$b", n);
                    existed = check.ExecuteScalar() is not null;
                }
                using (var put = conn.CreateCommand())
                {
                    put.Transaction = tx;
                    put.CommandText = "INSERT OR REPLACE INTO products(barcode, name, brand, category) VALUES($b, $n, $br, $c)";
                    put.Parameters.AddWithValue("$b", n);
                    put.Parameters.AddWithValue("$n", title);
                    put.Parameters.AddWithValue("$br", brand?.Trim() ?? "");
                    put.Parameters.AddWithValue("$c", category?.Trim() ?? "");
                    put.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM missing WHERE barcode=$b";
                    del.Parameters.AddWithValue("$b", n);
                    del.ExecuteNonQuery();
                }
                if (!existed)
                {
                    using var inc = conn.CreateCommand();
                    inc.Transaction = tx;
                    inc.CommandText = "INSERT INTO meta(key, value) VALUES('added_from_server', '1') "
                                      + "ON CONFLICT(key) DO UPDATE SET value = CAST(value AS INTEGER) + 1";
                    inc.ExecuteNonQuery();
                }
                tx.Commit();
                return !existed;
            }
        }
        catch (Exception ex)
        {
            Fail("запись товара с сервера", ex);
            return false;
        }
    }

    /// <summary>Состояние для строки в настройках. Файл недоступен — Available=false.</summary>
    public GlobalCatalogStatus GetStatus()
    {
        try
        {
            lock (_gate)
            {
                if (EnsureReady() is not { } conn)
                    return new GlobalCatalogStatus(false, 0, "", 0);
                long count;
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT COUNT(*) FROM products";
                    count = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                return new GlobalCatalogStatus(true, count, Meta(conn, "exported_at") ?? "",
                    long.TryParse(Meta(conn, "added_from_server"), out var added) ? added : 0);
            }
        }
        catch (Exception ex)
        {
            Fail("состояние", ex);
            return new GlobalCatalogStatus(false, 0, "", 0);
        }
    }

    // ------------------------------------------------------------------ локальная копия + сервер

    /// <summary>Сначала локальная копия (мгновенно, без сети); нет — сервер, но не чаще одного запроса за раз, через общий темп
    /// <see cref="ApiThrottle"/> (429 — пауза), и не чаще раза в 24 часа на один «не найденный» код.
    /// <paramref name="fetch"/>: вернуть товар (200), null (404 — такого штрихкода нет), любая другая ошибка — исключение
    /// (нет связи/429): оно уйдёт вызывающему, а «не найден» запомнится ТОЛЬКО по настоящему 404.</summary>
    public async Task<JsonElement?> FindOrFetchAsync(string barcode, Func<string, CancellationToken, Task<JsonElement?>> fetch, CancellationToken ct = default)
    {
        var n = Normalize(barcode);
        if (Find(n) is { } local)
            return ToJson(local);
        var plausible = IsPlausible(n);
        if (plausible && IsKnownMissing(n))
            return null;

        await ServerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Пока ждали очередь, соседний запрос мог уже найти этот код.
            if (Find(n) is { } later)
                return ToJson(later);
            if (plausible && IsKnownMissing(n))
                return null;

            var found = await ApiThrottle.RunBulkAsync(() => fetch(barcode.Trim(), ct), ct).ConfigureAwait(false);
            if (found is not { } product)
            {
                if (plausible)
                    MarkMissing(n);
                return null;
            }

            if (Remember(Text(product, "barcode") ?? n, Text(product, "name"), Text(product, "brand"), Text(product, "category")))
                PosLogger.Log($"Справочник: штрихкод {n} найден на сервере и добавлен в локальную копию.", Category);
            return product;
        }
        finally
        {
            ServerGate.Release();
        }
    }

    private static JsonElement ToJson(GlobalProductInfo p) => JsonSerializer.SerializeToElement(new
    {
        name = p.Name,
        barcode = p.Barcode,
        brand = p.Brand,
        category = p.Category,
        source = "local-directory",
    });

    /// <summary>Строка из ответа сервера: «category»/«brand» бывают строкой или объектом с «name».</summary>
    private static string? Text(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object when v.TryGetProperty("name", out var inner) && inner.ValueKind == JsonValueKind.String => inner.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // ------------------------------------------------------------------ файл

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string? Meta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private void Fail(string what, Exception ex) =>
        PosLogger.Log($"Справочник: {what} не удалось ({ex.GetType().Name}: {ex.Message}).", Category);

    private Stream? OpenEmbedded()
    {
        try
        {
            return (_embedded ?? EmbeddedProvider)?.Invoke();
        }
        catch (Exception ex)
        {
            Fail("чтение встроенной копии", ex);
            return null;
        }
    }

    private SqliteConnection Connect(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Открыть (при первом обращении — создать из встроенной копии) локальную базу. Вызывать под _gate.
    /// Не вышло — null и повторная попытка не раньше чем через 10 минут (касса не должна тормозить на каждом скане).</summary>
    private SqliteConnection? EnsureReady()
    {
        if (_conn is not null)
            return _conn;
        if (UtcNow() < _noRetryUntilUtc)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string? fingerprint = null;
            var fresh = !File.Exists(_path);
            if (fresh)
                fingerprint = CopyEmbedded();

            SqliteConnection conn;
            try
            {
                conn = OpenChecked();
            }
            catch (Exception ex)
            {
                // Файл повреждён (обрыв копирования, диск): откладываем его в сторону и берём свежую встроенную копию.
                Fail("файл повреждён, пересоздаю", ex);
                var bad = _path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try { File.Move(_path, bad, true); }
                catch { File.Delete(_path); }
                fingerprint = CopyEmbedded();
                fresh = true;
                conn = OpenChecked();
            }

            if (fresh && fingerprint is not null)
                SetMeta(conn, "embedded_fingerprint", fingerprint);

            var find = conn.CreateCommand();
            find.CommandText = "SELECT barcode, name, brand, category FROM products WHERE barcode=$b";
            find.Parameters.Add("$b", SqliteType.Text);
            find.Prepare();
            _findCmd = find;
            _conn = conn;

            // Старые «не найден» старше суток не нужны.
            using (var clean = conn.CreateCommand())
            {
                clean.CommandText = "DELETE FROM missing WHERE at < $limit";
                clean.Parameters.AddWithValue("$limit", ToUnix(UtcNow() - MissingTtl));
                clean.ExecuteNonQuery();
            }

            if (!_mergeScheduled)
            {
                _mergeScheduled = true;
                _ = Task.Run(MergeEmbeddedIfNewer);
            }
            return conn;
        }
        catch (Exception ex)
        {
            Fail("открытие", ex);
            _noRetryUntilUtc = UtcNow() + RetryOpenAfter;
            try { _findCmd?.Dispose(); _conn?.Dispose(); } catch { /* уже закрыто */ }
            _findCmd = null;
            _conn = null;
            return null;
        }
    }

    private SqliteConnection OpenChecked()
    {
        var conn = Connect(_path);
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS products(barcode TEXT PRIMARY KEY, name TEXT NOT NULL, brand TEXT NOT NULL DEFAULT '', category TEXT NOT NULL DEFAULT '') WITHOUT ROWID;"
                                  + "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);"
                                  + "CREATE TABLE IF NOT EXISTS missing(barcode TEXT PRIMARY KEY, at INTEGER NOT NULL) WITHOUT ROWID;";
                cmd.ExecuteNonQuery();
            }
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1)";
                if (check.ExecuteScalar() is not "ok")
                    throw new InvalidDataException("quick_check не прошёл");
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Отпечаток встроенной базы: размер + первые 100 байт заголовка SQLite (в них счётчик изменений файла — он меняется при любой
    /// правке выгрузки). Нужен, чтобы не распаковывать 8 МБ на каждом запуске ради проверки «выгрузка новее?».</summary>
    private static string? Fingerprint(Stream s)
    {
        if (!s.CanSeek)
            return null;
        var head = new byte[100];
        s.Position = 0;
        var read = 0;
        while (read < head.Length)
        {
            var n = s.Read(head, read, head.Length - read);
            if (n <= 0)
                break;
            read += n;
        }
        s.Position = 0;
        return s.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(head, 0, read);
    }

    /// <summary>Копирует встроенную базу в пользовательский файл (через временный). Возвращает её отпечаток; встроенной нет — null
    /// (создастся пустая база, справочник наполнится с сервера).</summary>
    private string? CopyEmbedded()
    {
        using var src = OpenEmbedded();
        if (src is null)
            return null;
        var fingerprint = Fingerprint(src);
        var tmp = _path + ".tmp";
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, _path, true);
        return fingerprint;
    }

    /// <summary>Программа обновилась и принесла более свежую выгрузку: новые строки вливаются (INSERT OR IGNORE) в копию на компьютере,
    /// добавленное с сервера и поправленное не теряется. Отпечаток встроенного файла хранится в meta, чтобы не распаковывать 8 МБ на
    /// каждом запуске. Идёт в фоне, на отдельном соединении.</summary>
    private void MergeEmbeddedIfNewer()
    {
        string? tmp = null;
        try
        {
            using var src = OpenEmbedded();
            if (src is null || !src.CanSeek)
                return;
            var fingerprint = Fingerprint(src);
            if (fingerprint is null)
                return;
            using var conn = Connect(_path);
            if (Meta(conn, "embedded_fingerprint") == fingerprint)
                return;

            tmp = _path + ".new";
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);

            string? newExported;
            using (var emb = Connect(tmp))
                newExported = Meta(emb, "exported_at");
            var current = Meta(conn, "exported_at");
            if (string.IsNullOrEmpty(newExported) || string.CompareOrdinal(newExported, current ?? "") <= 0)
            {
                SetMeta(conn, "embedded_fingerprint", fingerprint);
                return;
            }

            long added;
            using (var attach = conn.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $p AS emb";
                attach.Parameters.AddWithValue("$p", tmp);
                attach.ExecuteNonQuery();
            }
            try
            {
                using var tx = conn.BeginTransaction();
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO main.products(barcode, name, brand, category) SELECT barcode, name, brand, category FROM emb.products";
                    added = ins.ExecuteNonQuery();
                }
                SetMetaIn(conn, tx, "exported_at", newExported);
                SetMetaIn(conn, tx, "embedded_fingerprint", fingerprint);
                tx.Commit();
            }
            finally
            {
                using var detach = conn.CreateCommand();
                detach.CommandText = "DETACH DATABASE emb";
                detach.ExecuteNonQuery();
            }
            PosLogger.Log($"Справочник: встроенная выгрузка от {newExported} влита в локальную копию, новых товаров: {added}.", Category);
        }
        catch (Exception ex)
        {
            Fail("слияние со встроенной выгрузкой", ex);
        }
        finally
        {
            try { if (tmp is not null) File.Delete(tmp); } catch { /* временный файл */ }
        }
    }

    private static void SetMetaIn(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
