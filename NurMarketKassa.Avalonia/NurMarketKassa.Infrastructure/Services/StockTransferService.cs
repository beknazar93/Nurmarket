using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NurMarketKassa.Services;

/// <summary>Перемещения товаров: документы, их состав, маршрут, история статусов и вложения.
///
/// Всё хранится в базе кассы. Сервер NurCRM документов перемещения не принимает — в его ответе
/// по остаткам склады есть только как справочная разбивка, поэтому журнал не уедет на другое
/// устройство, пока на сервере не появится своя поддержка. Об этом должен знать и владелец:
/// перемещения, заведённые на одной кассе, на второй не появятся.
/// </summary>
public sealed class StockTransferService
{
    public static StockTransferService Instance { get; } = new();

    private StockTransferService() { }

    /// <summary>Статусы документа. Порядок здесь — это и порядок жизни перемещения: создано,
    /// собрано и отправлено, принято на месте. Отмена возможна на любом шаге до приёмки.</summary>
    public const string StatusCreated = "created";
    public const string StatusInTransit = "in_transit";
    public const string StatusDelivered = "delivered";
    public const string StatusCancelled = "cancelled";

    public sealed record Place(string Id, string Name, string Kind, string? ParentId);

    public sealed record TransferItem(
        long Id, string TransferId, string? ProductId, string ProductName,
        string? Article, string? Barcode, double Quantity, string? Unit, double Weight);

    public sealed record Transfer(
        string Id, string Number, DateTime CreatedAt, string Status,
        string? FromPlaceId, string? ToPlaceId, string? Responsible,
        string? Carrier, string? TrackingNumber, double TotalWeight, string? Note)
    {
        public string FromPlaceName { get; init; } = "";
        public string ToPlaceName { get; init; } = "";
        public int ItemCount { get; init; }
        public double TotalQuantity { get; init; }
    }

    public sealed record LogEntry(DateTime At, string Status, string? Employee, string? Note);

    public sealed record Attachment(long Id, string TransferId, string FilePath, string FileName, string? Kind, DateTime AddedAt);

    // ------------------------------------------------------------------ места хранения

    /// <summary>Склады, зоны и ячейки одним списком: ячейка ссылается на зону, зона — на склад.
    /// Плоская таблица вместо трёх — уровней всего три, и вложенность у них одинаковая.</summary>
    public List<Place> LoadPlaces()
    {
        var list = new List<Place>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, kind, parent_id FROM StockPlaces WHERE 1=1"
                + DatabaseService.Instance.OwnRowsClauseFor() + " ORDER BY kind, name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new Place(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        });
        return list;
    }

    public string AddPlace(string name, string kind, string? parentId)
    {
        var id = Guid.NewGuid().ToString("N");
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO StockPlaces (id, name, kind, parent_id, company_id) "
                + "VALUES ($id, $name, $kind, $parent, $company);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$company", (object?)DatabaseService.Instance.CurrentCompany() ?? DBNull.Value);
            command.ExecuteNonQuery();
        });
        return id;
    }

    // ------------------------------------------------------------------ документы

    /// <summary>Журнал перемещений с фильтрами. Поиск идёт и по самому документу (номер,
    /// ответственный, перевозчик, трек-номер), и по его составу — по названию, артикулу и
    /// штрихкоду товара: кладовщик ищет «где та коробка», а не номер бумаги.</summary>
    public List<Transfer> LoadTransfers(string? search = null, string? status = null, int limit = 300)
    {
        var list = new List<Transfer>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            var where = "WHERE 1=1" + DatabaseService.Instance.OwnRowsClauseFor("t");

            if (!string.IsNullOrWhiteSpace(status))
                where += " AND t.status = $status";

            if (!string.IsNullOrWhiteSpace(search))
                where += """
                     AND (t.number LIKE $like OR IFNULL(t.responsible,'') LIKE $like
                          OR IFNULL(t.carrier,'') LIKE $like OR IFNULL(t.tracking_number,'') LIKE $like
                          OR EXISTS (SELECT 1 FROM StockTransferItems i WHERE i.transfer_id = t.id
                                     AND (i.product_name LIKE $like OR IFNULL(i.article,'') LIKE $like
                                          OR IFNULL(i.barcode,'') LIKE $like)))
                     """;

            command.CommandText = $"""
                SELECT t.id, t.number, t.created_at, t.status, t.from_place_id, t.to_place_id,
                       t.responsible, t.carrier, t.tracking_number, t.total_weight, t.note,
                       IFNULL(pf.name, ''), IFNULL(pt.name, ''),
                       (SELECT COUNT(*) FROM StockTransferItems i WHERE i.transfer_id = t.id),
                       (SELECT IFNULL(SUM(i.quantity), 0) FROM StockTransferItems i WHERE i.transfer_id = t.id)
                FROM StockTransfers t
                LEFT JOIN StockPlaces pf ON pf.id = t.from_place_id
                LEFT JOIN StockPlaces pt ON pt.id = t.to_place_id
                {where}
                ORDER BY t.created_at DESC
                LIMIT $limit;
                """;

            if (!string.IsNullOrWhiteSpace(status))
                command.Parameters.AddWithValue("$status", status);
            if (!string.IsNullOrWhiteSpace(search))
                command.Parameters.AddWithValue("$like", "%" + search.Trim() + "%");
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new Transfer(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseDate(reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetDouble(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10))
                {
                    FromPlaceName = reader.GetString(11),
                    ToPlaceName = reader.GetString(12),
                    ItemCount = reader.GetInt32(13),
                    TotalQuantity = reader.GetDouble(14),
                });
        });
        return list;
    }

    /// <summary>Создаёт документ в статусе «создано» и сразу пишет первую строку хронологии.</summary>
    public string CreateTransfer(
        string? fromPlaceId, string? toPlaceId, string? responsible,
        string? carrier, string? trackingNumber, string? note, string? employee)
    {
        var id = Guid.NewGuid().ToString("N");
        var number = NextNumber();
        var now = DateTime.Now;

        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO StockTransfers
                    (id, number, created_at, status, from_place_id, to_place_id, responsible,
                     carrier, tracking_number, total_weight, note, company_id)
                VALUES ($id, $number, $created, $status, $from, $to, $responsible,
                        $carrier, $tracking, 0, $note, $company);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$number", number);
            command.Parameters.AddWithValue("$created", now.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$status", StatusCreated);
            command.Parameters.AddWithValue("$from", (object?)fromPlaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)toPlaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$responsible", (object?)responsible ?? DBNull.Value);
            command.Parameters.AddWithValue("$carrier", (object?)carrier ?? DBNull.Value);
            command.Parameters.AddWithValue("$tracking", (object?)trackingNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            command.Parameters.AddWithValue("$company", (object?)DatabaseService.Instance.CurrentCompany() ?? DBNull.Value);
            command.ExecuteNonQuery();
        });

        AppendLog(id, StatusCreated, employee, null);
        return id;
    }

    /// <summary>Номер документа: год и порядковый номер внутри года — «2026-014». Сквозная
    /// нумерация без года через несколько лет перестаёт читаться.</summary>
    private string NextNumber()
    {
        var year = DateTime.Now.Year;
        var count = 0;
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM StockTransfers WHERE number LIKE $prefix;";
            command.Parameters.AddWithValue("$prefix", year + "-%");
            count = Convert.ToInt32(command.ExecuteScalar() ?? 0);
        });
        return $"{year}-{count + 1:000}";
    }

    public void AddItem(string transferId, string? productId, string productName,
        string? article, string? barcode, double quantity, string? unit, double weight)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO StockTransferItems
                    (transfer_id, product_id, product_name, article, barcode, quantity, unit, weight)
                VALUES ($transfer, $product, $name, $article, $barcode, $quantity, $unit, $weight);
                """;
            command.Parameters.AddWithValue("$transfer", transferId);
            command.Parameters.AddWithValue("$product", (object?)productId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", productName);
            command.Parameters.AddWithValue("$article", (object?)article ?? DBNull.Value);
            command.Parameters.AddWithValue("$barcode", (object?)barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("$quantity", quantity);
            command.Parameters.AddWithValue("$unit", (object?)unit ?? DBNull.Value);
            command.Parameters.AddWithValue("$weight", weight);
            command.ExecuteNonQuery();
        });
        RecalculateWeight(transferId);
    }

    public void RemoveItem(long itemId, string transferId)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM StockTransferItems WHERE id = $id;";
            command.Parameters.AddWithValue("$id", itemId);
            command.ExecuteNonQuery();
        });
        RecalculateWeight(transferId);
    }

    public List<TransferItem> LoadItems(string transferId)
    {
        var list = new List<TransferItem>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, transfer_id, product_id, product_name, article, barcode, "
                + "quantity, unit, weight FROM StockTransferItems WHERE transfer_id = $id ORDER BY id;";
            command.Parameters.AddWithValue("$id", transferId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new TransferItem(
                    reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDouble(8)));
        });
        return list;
    }

    private void RecalculateWeight(string transferId)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE StockTransfers SET total_weight = "
                + "(SELECT IFNULL(SUM(weight * quantity), 0) FROM StockTransferItems WHERE transfer_id = $id) "
                + "WHERE id = $id;";
            command.Parameters.AddWithValue("$id", transferId);
            command.ExecuteNonQuery();
        });
    }

    // ------------------------------------------------------------------ статусы и хронология

    public void ChangeStatus(string transferId, string status, string? employee, string? note)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE StockTransfers SET status = $status WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$id", transferId);
            command.ExecuteNonQuery();
        });
        AppendLog(transferId, status, employee, note);
    }

    private void AppendLog(string transferId, string status, string? employee, string? note)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO StockTransferLog (transfer_id, at, status, employee, note) "
                + "VALUES ($transfer, $at, $status, $employee, $note);";
            command.Parameters.AddWithValue("$transfer", transferId);
            command.Parameters.AddWithValue("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$employee", (object?)employee ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            command.ExecuteNonQuery();
        });
    }

    public List<LogEntry> LoadLog(string transferId)
    {
        var list = new List<LogEntry>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at, status, employee, note FROM StockTransferLog "
                + "WHERE transfer_id = $id ORDER BY at;";
            command.Parameters.AddWithValue("$id", transferId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new LogEntry(
                    ParseDate(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        });
        return list;
    }

    // ------------------------------------------------------------------ вложения

    /// <summary>Файл копируется в папку кассы: исходник кладовщик может унести на флешке или
    /// удалить, а накладная должна остаться привязанной к документу.</summary>
    public void AttachFile(string transferId, string sourcePath, string kind)
    {
        var folder = Path.Combine(DatabaseService.Instance.DataFolder, "transfers", transferId);
        Directory.CreateDirectory(folder);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(folder, fileName);
        var counter = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(folder,
                Path.GetFileNameWithoutExtension(fileName) + $" ({counter++})" + Path.GetExtension(fileName));
        }
        File.Copy(sourcePath, target);

        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO StockTransferFiles (transfer_id, file_path, file_name, kind, added_at) "
                + "VALUES ($transfer, $path, $name, $kind, $at);";
            command.Parameters.AddWithValue("$transfer", transferId);
            command.Parameters.AddWithValue("$path", target);
            command.Parameters.AddWithValue("$name", Path.GetFileName(target));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        });
    }

    public List<Attachment> LoadAttachments(string transferId)
    {
        var list = new List<Attachment>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, transfer_id, file_path, file_name, kind, added_at "
                + "FROM StockTransferFiles WHERE transfer_id = $id ORDER BY added_at;";
            command.Parameters.AddWithValue("$id", transferId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new Attachment(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ParseDate(reader.GetString(5))));
        });
        return list;
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;
}
