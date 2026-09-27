using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NurMarketKassa.Services.Lan;

/// <summary>Строка проданного товара в журнале обмена по локальной сети.</summary>
public sealed class LanSaleItem
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public double Qty { get; set; }
    public double LineTotal { get; set; }
}

/// <summary>Сколько товара ушло со склада (с составом комплектов) — по этим числам соседние
/// кассы уменьшают у себя остаток, пока продажа не видна серверу.</summary>
public sealed class LanStockDelta
{
    public string ProductId { get; set; } = "";
    public double Qty { get; set; }
}

/// <summary>Продажа одной кассы для соседей. SaleKey — стабильный ключ: у офлайн-чека это
/// номер записи очереди, у онлайн-продажи — номер продажи на сервере.</summary>
public sealed class LanSalePayload
{
    public string SaleKey { get; set; } = "";
    public string? ServerSaleId { get; set; }
    public bool Uploaded { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public double Total { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string? CashboxName { get; set; }
    public string? CashierName { get; set; }
    public List<LanSaleItem> Items { get; set; } = new();
    public List<LanStockDelta> Stock { get; set; } = new();
}

/// <summary>Офлайн-чек, выгруженный на сервер позже.</summary>
public sealed class LanUploadedPayload
{
    public string SaleKey { get; set; } = "";
    public string? ServerSaleId { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>Событие журнала — как оно хранится и передаётся по сети.</summary>
public sealed class LanEvent
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Json { get; set; } = "";
}

/// <summary>Продажа соседа вместе с отметкой о выгрузке на сервер.</summary>
public sealed class LanPeerSale
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public LanSalePayload Sale { get; set; } = new();
    public DateTime? UploadedAtUtc { get; set; }

    /// <summary>Когда продажа и отметка о выгрузке дошли до этого компьютера — по СВОИМ часам:
    /// часы касс могут расходиться на минуты, сравнивать с ними свой снимок склада нельзя.</summary>
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? UploadedReceivedAtUtc { get; set; }

    /// <summary>Продажа уже на сервере (сразу или после выгрузки офлайн-чека).</summary>
    public bool IsOnServer => Sale.Uploaded || UploadedAtUtc != null;

    /// <summary>С какого момента (по своим часам) точно известно, что сервер её учитывает:
    /// событие публикуется только после ответа сервера. null — пока не учитывает.</summary>
    public DateTime? KnownOnServerSinceUtc => Sale.Uploaded ? ReceivedAtUtc : UploadedReceivedAtUtc;
}

/// <summary>
/// Журнал обмена по локальной сети (2026-09-27, «при плохом интернете программы обмениваются
/// данными между собой по локальной сети, а на одном моноблоке — внутри компьютера»).
///
/// Каждая касса пишет сюда свои продажи и отметки «офлайн-чек выгружен», соседи забирают их по
/// порядковому номеру. Отправкой чеков на сервер журнал не занимается: чек отправляет только
/// касса, которая его пробила (сервер не узнаёт повторную отправку — была бы двойная продажа).
/// Таблицы лежат в pos_local.db компании, поэтому данные разных компаний не смешиваются.
/// </summary>
public static class LanJournal
{
    public const string KindSale = "sale";
    public const string KindUploaded = "sale_uploaded";
    private static readonly TimeSpan Keep = TimeSpan.FromDays(4);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static bool _schemaReady;

    private static void EnsureSchema(SqliteConnection c)
    {
        if (_schemaReady)
            return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS LanJournal (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LanPeerEvents (
                device TEXT NOT NULL,
                id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                kind TEXT NOT NULL,
                created_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY (device, id));
            CREATE INDEX IF NOT EXISTS IX_LanPeerEvents_created ON LanPeerEvents(created_at);
            CREATE TABLE IF NOT EXISTS LanPeers (
                device TEXT PRIMARY KEY,
                name TEXT,
                role TEXT,
                last_seq INTEGER NOT NULL DEFAULT 0,
                last_seen TEXT);
            CREATE TABLE IF NOT EXISTS LanStockAdjust (
                product_id TEXT PRIMARY KEY,
                qty REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS LanMeta (
                key TEXT PRIMARY KEY,
                value TEXT);
            """;
        cmd.ExecuteNonQuery();
        _schemaReady = true;
    }

    /// <summary>После смены компании база другая — схему проверить заново.</summary>
    public static void ResetSchemaFlag() => _schemaReady = false;

    private static T With<T>(Func<SqliteConnection, T> action)
    {
        T result = default!;
        DatabaseService.Instance.WithConnection(c =>
        {
            EnsureSchema(c);
            result = action(c);
        });
        return result;
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTime.UtcNow;

    // ------------------------------------------------------------------ запись своих событий

    /// <summary>Своя продажа — в журнал. Никогда не бросает: оплата важнее обмена.</summary>
    public static void PublishSale(LanSalePayload sale)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sale.SaleKey) || sale.Items.Count == 0)
                return;
            Append($"sale:{sale.SaleKey}", KindSale, JsonSerializer.Serialize(sale, Json));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: продажа не записана в журнал: {ex.Message}", "LAN");
        }
    }

    /// <summary>Офлайн-чек выгружен на сервер — соседям больше не нужно вычитать его из остатка.</summary>
    public static void PublishUploaded(string saleKey, string? serverSaleId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(saleKey))
                return;
            var payload = new LanUploadedPayload { SaleKey = saleKey, ServerSaleId = serverSaleId, UploadedAtUtc = DateTime.UtcNow };
            Append($"up:{saleKey}", KindUploaded, JsonSerializer.Serialize(payload, Json));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: отметка выгрузки не записана: {ex.Message}", "LAN");
        }
    }

    private static void Append(string id, string kind, string json) =>
        With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO LanJournal(id, kind, created_at, json) VALUES ($id, $kind, $at, $json)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$json", json);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Свои события после номера — для соседа, который их забирает.</summary>
    public static (List<LanEvent> Events, long Last) ReadOwnAfter(long after, int limit)
    {
        return With(c =>
        {
            var list = new List<LanEvent>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT seq, id, kind, created_at, json FROM LanJournal WHERE seq > $after ORDER BY seq LIMIT $limit";
                cmd.Parameters.AddWithValue("$after", after);
                cmd.Parameters.AddWithValue("$limit", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new LanEvent { Seq = r.GetInt64(0), Id = r.GetString(1), Kind = r.GetString(2), CreatedAt = r.GetString(3), Json = r.GetString(4) });
            }

            using var last = c.CreateCommand();
            last.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM LanJournal";
            return (list, Convert.ToInt64(last.ExecuteScalar(), CultureInfo.InvariantCulture));
        });
    }

    // ------------------------------------------------------------------ события соседей

    public static long PeerLastSeq(string device) =>
        With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT last_seq FROM LanPeers WHERE device = $d";
            cmd.Parameters.AddWithValue("$d", device);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        });

    /// <summary>Сохраняет события соседа. Возвращает, сколько из них новых.</summary>
    public static int SavePeerEvents(string device, string name, string role, IReadOnlyList<LanEvent> events, long lastSeq)
    {
        return With(c =>
        {
            using var tx = c.BeginTransaction();
            var added = 0;
            foreach (var e in events)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO LanPeerEvents(device, id, seq, kind, created_at, received_at, json) VALUES ($d, $id, $seq, $kind, $at, $rcv, $json)";
                cmd.Parameters.AddWithValue("$d", device);
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.Parameters.AddWithValue("$seq", e.Seq);
                cmd.Parameters.AddWithValue("$kind", e.Kind);
                cmd.Parameters.AddWithValue("$at", e.CreatedAt);
                cmd.Parameters.AddWithValue("$rcv", Iso(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$json", e.Json);
                added += cmd.ExecuteNonQuery();
            }

            using (var up = c.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO LanPeers(device, name, role, last_seq, last_seen) VALUES ($d, $n, $r, $s, $t)
                    ON CONFLICT(device) DO UPDATE SET name = $n, role = $r, last_seq = $s, last_seen = $t
                    """;
                up.Parameters.AddWithValue("$d", device);
                up.Parameters.AddWithValue("$n", name);
                up.Parameters.AddWithValue("$r", role);
                up.Parameters.AddWithValue("$s", lastSeq);
                up.Parameters.AddWithValue("$t", Iso(DateTime.UtcNow));
                up.ExecuteNonQuery();
            }

            tx.Commit();
            return added;
        });
    }

    /// <summary>Журнал соседа начался заново (переустановка, другая база) — забирать с начала.</summary>
    public static void ResetPeerCursor(string device) =>
        With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE LanPeers SET last_seq = 0 WHERE device = $d";
            cmd.Parameters.AddWithValue("$d", device);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Продажи соседей начиная с момента, с отметками о выгрузке.</summary>
    public static List<LanPeerSale> ReadPeerSales(DateTime sinceUtc)
    {
        return With(c =>
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var n = c.CreateCommand())
            {
                n.CommandText = "SELECT device, IFNULL(name, '') FROM LanPeers";
                using var r = n.ExecuteReader();
                while (r.Read())
                    names[r.GetString(0)] = r.GetString(1);
            }

            var sales = new Dictionary<(string, string), LanPeerSale>();
            var uploaded = new Dictionary<(string, string), (DateTime At, DateTime Received)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT device, kind, json, received_at FROM LanPeerEvents WHERE created_at >= $since";
                cmd.Parameters.AddWithValue("$since", Iso(sinceUtc));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var device = r.GetString(0);
                    var kind = r.GetString(1);
                    var received = ParseIso(r.GetString(3));
                    try
                    {
                        if (kind == KindSale)
                        {
                            var sale = JsonSerializer.Deserialize<LanSalePayload>(r.GetString(2), Json);
                            if (sale != null && !string.IsNullOrWhiteSpace(sale.SaleKey))
                                sales[(device, sale.SaleKey)] = new LanPeerSale
                                {
                                    DeviceId = device,
                                    DeviceName = names.TryGetValue(device, out var nm) ? nm : device,
                                    Sale = sale,
                                    ReceivedAtUtc = received,
                                };
                        }
                        else if (kind == KindUploaded)
                        {
                            var up = JsonSerializer.Deserialize<LanUploadedPayload>(r.GetString(2), Json);
                            if (up != null && !string.IsNullOrWhiteSpace(up.SaleKey))
                                uploaded[(device, up.SaleKey)] = (up.UploadedAtUtc, received);
                        }
                    }
                    catch (JsonException)
                    {
                        // чужой формат (другая версия программы) — пропускаем событие
                    }
                }
            }

            foreach (var (key, up) in uploaded)
                if (sales.TryGetValue(key, out var s))
                {
                    s.UploadedAtUtc = up.At;
                    s.UploadedReceivedAtUtc = up.Received;
                }
            return sales.Values.OrderBy(s => s.Sale.CreatedAtUtc).ToList();
        });
    }

    // ------------------------------------------------------------------ поправки остатка и служебное

    public static Dictionary<string, double> ReadStockAdjust() =>
        With(c =>
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT product_id, qty FROM LanStockAdjust";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[r.GetString(0)] = r.GetDouble(1);
            return map;
        });

    public static void WriteStockAdjust(IReadOnlyDictionary<string, double> map) =>
        With(c =>
        {
            using var tx = c.BeginTransaction();
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM LanStockAdjust";
                del.ExecuteNonQuery();
            }

            foreach (var (id, qty) in map)
            {
                if (Math.Abs(qty) < 1e-9)
                    continue;
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO LanStockAdjust(product_id, qty) VALUES ($p, $q)";
                ins.Parameters.AddWithValue("$p", id);
                ins.Parameters.AddWithValue("$q", qty);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
            return 0;
        });

    public static string? ReadMeta(string key) =>
        With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM LanMeta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        });

    public static void WriteMeta(string key, string value) =>
        With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO LanMeta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Старые события больше никому не нужны: сервер к этому времени их уже знает.</summary>
    public static void Prune()
    {
        try
        {
            var cutoff = Iso(DateTime.UtcNow - Keep);
            With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM LanJournal WHERE created_at < $c; DELETE FROM LanPeerEvents WHERE created_at < $c;";
                cmd.Parameters.AddWithValue("$c", cutoff);
                return cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: очистка журнала не удалась: {ex.Message}", "LAN");
        }
    }
}
