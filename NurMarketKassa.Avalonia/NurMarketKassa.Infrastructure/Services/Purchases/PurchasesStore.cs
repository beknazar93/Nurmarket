using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-22 (закупки как в вебе, docs/«Закупки в вебе NurCRM — исследование 2026-10-22.md»): у сервера NurCRM нет «заказа поставщику» и
// способа оплаты в приходе (suppliers/receipts/ отдаёт только товары и сумму). Чтобы в «Закупках» были понятные статусы — заказ отправлен /
// принят частично / принят, оплачено / в долг / частично — касса и программа владельца на этом ПК ведут свой небольшой журнал. Сам приход,
// склад и долг — на сервере, как раньше; журнал только подписывает их. Потеря журнала денег не трогает (статусы станут «—»).

/// <summary>Строка заказа поставщику: товар, сколько заказали и почём, сколько уже принято приходами.</summary>
public sealed class PurchaseOrderLine
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public double Price { get; set; }
    public double ReceivedQty { get; set; }

    public double Left => Math.Max(0, Math.Round(Qty - ReceivedQty, 3));
}

/// <summary>Заказ поставщику (из «Рекомендуемого заказа» или вручную). Status — draft / sent / cancelled; «принят частично» и «принят»
/// считаются по строкам (ReceivedQty), см. <see cref="State"/>.</summary>
public sealed class PurchaseOrder
{
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string Number { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string SupplierPhone { get; set; } = "";
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string Author { get; set; } = "";
    public string Note { get; set; } = "";
    public List<PurchaseOrderLine> Lines { get; set; } = new();
    public List<string> ReceiptIds { get; set; } = new();

    // 2026-10-28 (дробные цены): сумма заказа = сумма строк, каждая округлена до копеек по итогу (AwayFromZero)
    public double Total => Math.Round(Lines.Sum(l => NurMarketKassa.Services.ReceivingPricing.LineTotal(l.Qty, l.Price)), 2, MidpointRounding.AwayFromZero);

    /// <summary>draft, sent, partial, received, cancelled.</summary>
    public string State
    {
        get
        {
            if (Status == "cancelled")
                return "cancelled";
            var withQty = Lines.Where(l => l.Qty > 0).ToList();
            if (withQty.Count > 0 && withQty.All(l => l.ReceivedQty + 0.0005 >= l.Qty))
                return "received";
            if (Lines.Any(l => l.ReceivedQty > 0.0005))
                return "partial";
            return Status == "sent" ? "sent" : "draft";
        }
    }

    public bool IsOpen => State is "draft" or "sent" or "partial";
}

/// <summary>Подпись прихода, проведённого «Закупками» на этом ПК: как оплачен (cash — из кассы расходов, drawer — из ящика смены,
/// partial — часть сразу, остальное в долг, debt — в долг), сколько оплачено, сколько в долг и до какого числа, по какому заказу.</summary>
public sealed class PurchaseReceiptNote
{
    public string ReceiptId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string PayKind { get; set; } = "";
    public double Total { get; set; }
    public double Paid { get; set; }
    public double Debt { get; set; }
    public DateTime? DueDate { get; set; }
    public string? OrderId { get; set; }
    /// <summary>Куда ушли деньги по ответу сервера (касса, смена) или что пошло не так — для подсказки в строке.</summary>
    public string MoneyNote { get; set; } = "";
    public bool MoneyProblem { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PurchasesData
{
    public List<PurchaseOrder> Orders { get; set; } = new();
    public List<PurchaseReceiptNote> Receipts { get; set; } = new();
    /// <summary>Настройки «Рекомендуемого заказа» (период анализа, запас до закупки, страховой запас) — общие для ПК.</summary>
    // 2026-10-24 (аудит, круг 8, С-10): у нового пользователя период 30 дней (как «Пополнение»); сохранённые настройки не меняются.
    public int PeriodDays { get; set; } = 30;
    public int CoverDays { get; set; } = 14;
    public int SafetyDays { get; set; } = 3;
}

/// <summary>2026-10-22 (закупки как в вебе): журнал «Закупок» на этом ПК — %APPDATA%\NurMarketKassa\purchases\purchases.json, один для кассы и
/// программы владельца (как журнал долгов поставщикам). Строки — с компанией. Запись атомарная (временный файл + замена); файл не читается —
/// переименовывается в purchases.bad-&lt;время&gt;.json и журнал начинается заново (денег в нём нет).</summary>
public static class PurchasesStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%) и запрет записи.</summary>
    public static string? DataDirOverride { get; set; }
    public static bool ReadOnly { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases");

    /// <summary>2026-10-30: папка журнала закупок — рядом лежат индекс нумерации документов и журнал наценок приходов.</summary>
    public static string StorageDir => DataDir;

    private static string FilePath => Path.Combine(DataDir, "purchases.json");
    private static readonly object Sync = new();
    private const int KeepReceipts = 2000;
    private const int KeepClosedOrders = 300;

    public static event Action? Changed;

    public static string CurrentCompany => CompanyInfoService.LastCompany?.Id ?? "";

    private static bool Mine(string company) => company.Length == 0 || CurrentCompany.Length == 0
        || string.Equals(company, CurrentCompany, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ чтение

    public static PurchasesData Load()
    {
        lock (Sync)
            return Read();
    }

    public static List<PurchaseOrder> Orders() => Load().Orders.Where(o => Mine(o.CompanyId)).OrderByDescending(o => o.UpdatedAt).ToList();

    public static PurchaseOrder? Order(string id) => Load().Orders.FirstOrDefault(o => o.Id == id && Mine(o.CompanyId));

    public static Dictionary<string, PurchaseReceiptNote> ReceiptNotes() =>
        Load().Receipts.Where(r => Mine(r.CompanyId) && r.ReceiptId.Length > 0)
            .GroupBy(r => r.ReceiptId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ запись

    /// <summary>Сохранить заказ (новый — с номером З-ГГММДД-N и id).</summary>
    public static PurchaseOrder SaveOrder(PurchaseOrder order)
    {
        Change(data =>
        {
            var now = DateTime.Now;
            if (order.Id.Length == 0)
            {
                order.Id = Guid.NewGuid().ToString("N");
                order.CompanyId = CurrentCompany;
                order.CreatedAt = now;
                // 2026-10-30: «ЗП-2026-000123» — единая нумерация закупок (тип-год-порядок, DocumentNumbering); прежние «ЗП-2210-1» у старых заказов остаются.
                order.Number = DocumentNumbering.Store.NextOrderNumber();
            }
            order.UpdatedAt = now;
            data.Orders.RemoveAll(o => o.Id == order.Id);
            data.Orders.Add(order);
        });
        return order;
    }

    public static void SetOrderStatus(string orderId, string status)
    {
        Change(data =>
        {
            if (data.Orders.FirstOrDefault(o => o.Id == orderId) is not { } o)
                return;
            o.Status = status;
            if (status == "sent")
                o.SentAt ??= DateTime.Now;
            o.UpdatedAt = DateTime.Now;
        });
    }

    public static void DeleteOrder(string orderId) => Change(data => data.Orders.RemoveAll(o => o.Id == orderId));

    /// <summary>Приход проведён: подпись прихода и, если он по заказу, — принятое количество в строки заказа.</summary>
    public static void AddReceipt(PurchaseReceiptNote note, IReadOnlyDictionary<string, double>? receivedByProduct)
    {
        Change(data =>
        {
            note.CompanyId = CurrentCompany;
            if (note.CreatedAt == default)
                note.CreatedAt = DateTime.Now;
            data.Receipts.RemoveAll(r => r.ReceiptId.Length > 0 && string.Equals(r.ReceiptId, note.ReceiptId, StringComparison.OrdinalIgnoreCase));
            data.Receipts.Add(note);
            if (note.OrderId is { Length: > 0 } orderId && data.Orders.FirstOrDefault(o => o.Id == orderId) is { } order && receivedByProduct is not null)
            {
                foreach (var line in order.Lines)
                    if (receivedByProduct.TryGetValue(line.ProductId, out var qty))
                        line.ReceivedQty = Math.Round(line.ReceivedQty + qty, 3);
                if (note.ReceiptId.Length > 0 && !order.ReceiptIds.Contains(note.ReceiptId))
                    order.ReceiptIds.Add(note.ReceiptId);
                if (order.Status == "draft")
                    order.Status = "sent";
                order.UpdatedAt = DateTime.Now;
            }
        });
    }

    public static void SaveSettings(int periodDays, int coverDays, int safetyDays) => Change(data =>
    {
        data.PeriodDays = periodDays;
        data.CoverDays = coverDays;
        data.SafetyDays = safetyDays;
    });

    private static void Change(Action<PurchasesData> change)
    {
        lock (Sync)
        {
            var data = Read();
            change(data);
            Write(data);
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: обработчик изменения журнала упал ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ файл

    private static PurchasesData? _memory;

    private static PurchasesData Read()
    {
        if (ReadOnly)
            return _memory ??= new PurchasesData();
        try
        {
            if (!File.Exists(FilePath))
                return new PurchasesData();
            string text;
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                text = reader.ReadToEnd();
            return JsonSerializer.Deserialize<PurchasesData>(text) ?? new PurchasesData();
        }
        catch (JsonException ex)
        {
            var bad = Path.Combine(DataDir, $"purchases.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Move(FilePath, bad);
            }
            catch
            {
                // не переименовать — просто начнём заново поверх
            }
            PosLogger.Log($"Закупки: журнал повреждён ({ex.Message}) — сохранён как {bad}, начат новый (денег в нём нет).", "WARNING");
            return new PurchasesData();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: журнал не прочитан ({ex.Message}).", "WARNING");
            return new PurchasesData();
        }
    }

    private static void Write(PurchasesData data)
    {
        // Подписи приходов — последние KeepReceipts; закрытые заказы — последние KeepClosedOrders; открытые — все.
        data.Receipts = data.Receipts.OrderByDescending(r => r.CreatedAt).Take(KeepReceipts).OrderBy(r => r.CreatedAt).ToList();
        data.Orders = data.Orders.Where(o => o.IsOpen)
            .Concat(data.Orders.Where(o => !o.IsOpen).OrderByDescending(o => o.UpdatedAt).Take(KeepClosedOrders))
            .OrderBy(o => o.CreatedAt).ToList();
        if (ReadOnly)
        {
            _memory = data;
            return;
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: журнал не записан ({ex.Message}).", "WARNING");
        }
    }
}
