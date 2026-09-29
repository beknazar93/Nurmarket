using Microsoft.Data.Sqlite;

namespace NurSupportBot.Data;

/// <summary>Узел базы знаний: раздел (меню) или инструкция. Дерево любой глубины.</summary>
public sealed class Node
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Title { get; set; } = "";
    public int Sort { get; set; }
    public bool Archived { get; set; }
    public bool IsArticle { get; set; }
    /// <summary>video / animation / photo / document — файл, загруженный в Telegram.</summary>
    public string? MediaKind { get; set; }
    public string? MediaFileId { get; set; }
    /// <summary>Ссылка на видео вне Telegram (YouTube и т.п.) — показывается кнопкой.</summary>
    public string? VideoUrl { get; set; }
    public string? Text { get; set; }
    public string? Keywords { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class BotUser
{
    public long TgId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? NurCrmEmail { get; set; }
    public string? NurCrmName { get; set; }
    public string? CompanyName { get; set; }
    public string? Tariff { get; set; }
    public string? Sector { get; set; }
    public string? Role { get; set; }
    public byte[]? RefreshEnc { get; set; }
    public bool Onboarded { get; set; }

    public bool LoggedIn => RefreshEnc is { Length: > 0 };
}

public sealed class Ticket
{
    public long Id { get; set; }
    public long TgId { get; set; }
    public long? NodeId { get; set; }
    public string Topic { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "open";
    public string CreatedAt { get; set; } = "";
    public long HeaderMsgId { get; set; }
}

/// <summary>Одна база SQLite. Обновления бот обрабатывает по одному, поэтому хватает одного
/// соединения; lock — на случай фоновых задач.</summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _cn;
    private readonly object _sync = new();

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _cn = new SqliteConnection($"Data Source={path}");
        _cn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS nodes(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              parent_id INTEGER NULL REFERENCES nodes(id) ON DELETE CASCADE,
              title TEXT NOT NULL,
              sort INTEGER NOT NULL DEFAULT 0,
              archived INTEGER NOT NULL DEFAULT 0,
              is_article INTEGER NOT NULL DEFAULT 0,
              media_kind TEXT NULL, media_file_id TEXT NULL, video_url TEXT NULL,
              text TEXT NULL, keywords TEXT NULL,
              updated_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE IF NOT EXISTS users(
              tg_id INTEGER PRIMARY KEY,
              username TEXT, first_name TEXT, last_name TEXT,
              created_at TEXT NOT NULL DEFAULT (datetime('now')),
              last_seen TEXT NOT NULL DEFAULT (datetime('now')),
              nurcrm_email TEXT, nurcrm_name TEXT, company_name TEXT, tariff TEXT, sector TEXT, role TEXT,
              refresh_enc BLOB NULL,
              onboarded INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS sessions(tg_id INTEGER PRIMARY KEY, state TEXT NOT NULL, data TEXT NULL);
            CREATE TABLE IF NOT EXISTS tickets(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              tg_id INTEGER NOT NULL, node_id INTEGER NULL, topic TEXT NOT NULL, text TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'open',
              created_at TEXT NOT NULL DEFAULT (datetime('now')), closed_at TEXT NULL,
              header_msg_id INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS ticket_msgs(group_msg_id INTEGER PRIMARY KEY, ticket_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL DEFAULT (datetime('now')),
              tg_id INTEGER NOT NULL, kind TEXT NOT NULL, node_id INTEGER NULL, query TEXT NULL, hits INTEGER NULL);
            CREATE INDEX IF NOT EXISTS ix_events_kind ON events(kind, ts);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NULL);
            """);
    }

    public void Dispose() => _cn.Dispose();

    // ── общие ───────────────────────────────────────────────────────────────────────

    private int Exec(string sql, params (string, object?)[] args)
    {
        lock (_sync)
        {
            using var cmd = Cmd(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (_sync)
        {
            using var cmd = Cmd(sql, args);
            return cmd.ExecuteScalar();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_sync)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read())
                list.Add(map(r));
            return list;
        }
    }

    private SqliteCommand Cmd(string sql, (string, object?)[] args)
    {
        var cmd = _cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static string? S(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
    private static long L(SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));
    private static long? LN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));

    // ── база знаний ─────────────────────────────────────────────────────────────────

    private static Node MapNode(SqliteDataReader r) => new()
    {
        Id = L(r, "id"),
        ParentId = LN(r, "parent_id"),
        Title = S(r, "title") ?? "",
        Sort = (int)L(r, "sort"),
        Archived = L(r, "archived") != 0,
        IsArticle = L(r, "is_article") != 0,
        MediaKind = S(r, "media_kind"),
        MediaFileId = S(r, "media_file_id"),
        VideoUrl = S(r, "video_url"),
        Text = S(r, "text"),
        Keywords = S(r, "keywords"),
        UpdatedAt = S(r, "updated_at") ?? "",
    };

    public Node? GetNode(long id) =>
        Query("SELECT * FROM nodes WHERE id=$id", MapNode, ("$id", id)).FirstOrDefault();

    public List<Node> Children(long? parentId, bool includeArchived = false) =>
        Query($"SELECT * FROM nodes WHERE {(parentId is null ? "parent_id IS NULL" : "parent_id=$p")}"
              + (includeArchived ? "" : " AND archived=0") + " ORDER BY sort, id",
            MapNode, ("$p", parentId));

    public List<Node> AllArticles() =>
        Query("SELECT * FROM nodes WHERE is_article=1 AND archived=0", MapNode);

    public int NodeCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM nodes"));

    public long AddNode(long? parentId, string title, bool isArticle, string? text = null, string? keywords = null)
    {
        var sort = Convert.ToInt32(Scalar(
            $"SELECT COALESCE(MAX(sort),0)+10 FROM nodes WHERE {(parentId is null ? "parent_id IS NULL" : "parent_id=$p")}",
            ("$p", parentId)));
        Exec("INSERT INTO nodes(parent_id,title,sort,is_article,text,keywords) VALUES($p,$t,$s,$a,$x,$k)",
            ("$p", parentId), ("$t", title), ("$s", sort), ("$a", isArticle ? 1 : 0), ("$x", text), ("$k", keywords));
        return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
    }

    public void UpdateNode(long id, string field, object? value)
    {
        var allowed = new[] { "title", "text", "keywords", "media_kind", "media_file_id", "video_url", "archived", "sort" };
        if (!allowed.Contains(field))
            throw new ArgumentException(field);
        Exec($"UPDATE nodes SET {field}=$v, updated_at=datetime('now') WHERE id=$id", ("$v", value), ("$id", id));
    }

    public void DeleteNode(long id) => Exec("DELETE FROM nodes WHERE id=$id", ("$id", id));

    /// <summary>Сдвиг узла на одно место среди соседей.</summary>
    public void MoveNode(long id, int direction)
    {
        var node = GetNode(id);
        if (node == null)
            return;
        var siblings = Children(node.ParentId, includeArchived: true);
        var index = siblings.FindIndex(n => n.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= siblings.Count)
            return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            Exec("UPDATE nodes SET sort=$s WHERE id=$id", ("$s", (i + 1) * 10), ("$id", siblings[i].Id));
    }

    /// <summary>Путь «Продажи → Возвраты → Как вернуть весь чек».</summary>
    public string PathOf(long id)
    {
        var parts = new List<string>();
        var node = GetNode(id);
        var guard = 0;
        while (node != null && guard++ < 10)
        {
            parts.Insert(0, node.Title);
            node = node.ParentId is { } p ? GetNode(p) : null;
        }
        return string.Join(" → ", parts);
    }

    // ── пользователи ────────────────────────────────────────────────────────────────

    public BotUser Touch(long tgId, string? username, string? first, string? last)
    {
        Exec("""
            INSERT INTO users(tg_id,username,first_name,last_name) VALUES($id,$u,$f,$l)
            ON CONFLICT(tg_id) DO UPDATE SET username=$u, first_name=$f, last_name=$l, last_seen=datetime('now')
            """, ("$id", tgId), ("$u", username), ("$f", first), ("$l", last));
        return GetUser(tgId)!;
    }

    public BotUser? GetUser(long tgId) =>
        Query("SELECT * FROM users WHERE tg_id=$id", r => new BotUser
        {
            TgId = L(r, "tg_id"),
            Username = S(r, "username"),
            FirstName = S(r, "first_name"),
            LastName = S(r, "last_name"),
            NurCrmEmail = S(r, "nurcrm_email"),
            NurCrmName = S(r, "nurcrm_name"),
            CompanyName = S(r, "company_name"),
            Tariff = S(r, "tariff"),
            Sector = S(r, "sector"),
            Role = S(r, "role"),
            RefreshEnc = r.IsDBNull(r.GetOrdinal("refresh_enc")) ? null : (byte[])r["refresh_enc"],
            Onboarded = L(r, "onboarded") != 0,
        }, ("$id", tgId)).FirstOrDefault();

    public void SetOnboarded(long tgId) => Exec("UPDATE users SET onboarded=1 WHERE tg_id=$id", ("$id", tgId));

    public void SaveLogin(long tgId, string email, string name, string company, string tariff, string sector, string role, byte[] refreshEnc) =>
        Exec("""
            UPDATE users SET nurcrm_email=$e, nurcrm_name=$n, company_name=$c, tariff=$t, sector=$s, role=$r, refresh_enc=$x, onboarded=1
            WHERE tg_id=$id
            """, ("$e", email), ("$n", name), ("$c", company), ("$t", tariff), ("$s", sector), ("$r", role), ("$x", refreshEnc), ("$id", tgId));

    public void Logout(long tgId) =>
        Exec("UPDATE users SET refresh_enc=NULL WHERE tg_id=$id", ("$id", tgId));

    // ── состояние диалога ───────────────────────────────────────────────────────────

    public (string State, string? Data) GetSession(long tgId) =>
        Query("SELECT state, data FROM sessions WHERE tg_id=$id", r => (S(r, "state") ?? "", S(r, "data")), ("$id", tgId))
            .FirstOrDefault(("", null));

    public void SetSession(long tgId, string state, string? data = null) =>
        Exec("INSERT INTO sessions(tg_id,state,data) VALUES($id,$s,$d) ON CONFLICT(tg_id) DO UPDATE SET state=$s, data=$d",
            ("$id", tgId), ("$s", state), ("$d", data));

    public void ClearSession(long tgId) => Exec("DELETE FROM sessions WHERE tg_id=$id", ("$id", tgId));

    // ── обращения ───────────────────────────────────────────────────────────────────

    private static Ticket MapTicket(SqliteDataReader r) => new()
    {
        Id = L(r, "id"),
        TgId = L(r, "tg_id"),
        NodeId = LN(r, "node_id"),
        Topic = S(r, "topic") ?? "",
        Text = S(r, "text") ?? "",
        Status = S(r, "status") ?? "open",
        CreatedAt = S(r, "created_at") ?? "",
        HeaderMsgId = L(r, "header_msg_id"),
    };

    public long CreateTicket(long tgId, long? nodeId, string topic, string text)
    {
        Exec("INSERT INTO tickets(tg_id,node_id,topic,text) VALUES($u,$n,$t,$x)",
            ("$u", tgId), ("$n", nodeId), ("$t", topic), ("$x", text));
        return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
    }

    public Ticket? GetTicket(long id) =>
        Query("SELECT * FROM tickets WHERE id=$id", MapTicket, ("$id", id)).FirstOrDefault();

    public List<Ticket> OpenTickets(int limit = 15) =>
        Query("SELECT * FROM tickets WHERE status='open' ORDER BY id DESC LIMIT $n", MapTicket, ("$n", limit));

    public void SetTicketHeader(long ticketId, long groupMsgId)
    {
        Exec("UPDATE tickets SET header_msg_id=$m WHERE id=$id", ("$m", groupMsgId), ("$id", ticketId));
        MapGroupMessage(groupMsgId, ticketId);
    }

    public void MapGroupMessage(long groupMsgId, long ticketId) =>
        Exec("INSERT OR REPLACE INTO ticket_msgs(group_msg_id,ticket_id) VALUES($m,$t)", ("$m", groupMsgId), ("$t", ticketId));

    public long? TicketByGroupMessage(long groupMsgId) =>
        Scalar("SELECT ticket_id FROM ticket_msgs WHERE group_msg_id=$m", ("$m", groupMsgId)) is long id ? id : null;

    public void CloseTicket(long id) =>
        Exec("UPDATE tickets SET status='closed', closed_at=datetime('now') WHERE id=$id", ("$id", id));

    public void ReopenTicket(long id) =>
        Exec("UPDATE tickets SET status='open', closed_at=NULL WHERE id=$id", ("$id", id));

    // ── статистика ──────────────────────────────────────────────────────────────────

    public void Log(long tgId, string kind, long? nodeId = null, string? query = null, int? hits = null) =>
        Exec("INSERT INTO events(tg_id,kind,node_id,query,hits) VALUES($u,$k,$n,$q,$h)",
            ("$u", tgId), ("$k", kind), ("$n", nodeId), ("$q", query), ("$h", hits));

    public long Count(string sql, params (string, object?)[] args) => Convert.ToInt64(Scalar(sql, args) ?? 0L);

    public List<(string Label, long Count)> Top(string sql, params (string, object?)[] args) =>
        Query(sql, r => (r.IsDBNull(0) ? "—" : r.GetString(0), r.GetInt64(1)), args);

    // ── настройки ───────────────────────────────────────────────────────────────────

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;

    public void SetSetting(string key, string? value) =>
        Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
}
