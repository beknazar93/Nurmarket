using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 1.4, 4.4): «Заказ под клиента» — детали, которых нет в наличии, привезут к сроку.
/// Хранится ТОЛЬКО локально (файл parts-orders.json в %LOCALAPPDATA%, AtomicFile, как «Смета»); сервер и схема pos_local.db не затронуты;
/// между компьютерами не синхронизируется (будущее: customer-orders/, Б-60).
/// ДЕНЬГИ: предоплата — запись «получено на руки» (<see cref="PartsOrder.Prepayment"/>), в смену и на сервер НЕ проводится: так в ящике
/// смены не бывает расхождения. При «Оформить продажу» чек пробивается на ПОЛНУЮ сумму, предоплата берётся из конверта и кладётся в ящик
/// вместе с доплатой — сумма наличных сходится с чеком.
/// </summary>
public static class PartsOrderStatus
{
    public const string New = "new";
    public const string Ordered = "ordered";
    public const string Arrived = "arrived";
    public const string Issued = "issued";
    public const string Cancelled = "cancelled";
}

public sealed class PartsOrderLine
{
    public string? ProductId { get; set; }
    public string Article { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal Qty { get; set; } = 1m;
    public decimal Price { get; set; }
    /// <summary>«Под заказ»: товара в каталоге нет или остаток нулевой — свободная строка.</summary>
    public bool OnOrder { get; set; }
}

public sealed class PartsOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Phone { get; set; } = "";
    public List<PartsOrderLine> Lines { get; set; } = new();
    /// <summary>Предоплата, полученная на руки (не проведена через смену — см. описание файла).</summary>
    public decimal Prepayment { get; set; }
    public DateTime? PrepaymentAt { get; set; }
    public bool PrepaymentRefunded { get; set; }
    public DateTime? DueDate { get; set; }
    public string Supplier { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Status { get; set; } = PartsOrderStatus.New;
    public string? CancelledNote { get; set; }
}

public sealed class PartsOrdersData
{
    public int LastNumber { get; set; }
    public List<PartsOrder> Orders { get; set; } = new();
}

/// <summary>Чистые правила заказа (суммы, статусы, текст клиенту) — без файлов и окон, для тестов.</summary>
public static class PartsOrderRules
{
    /// <summary>Мягкий лимит тарифа «Старт»: открытых заказов больше — только подсказка, ничего не блокируется.</summary>
    public const int StartOpenSoftLimit = 10;

    public static decimal Total(PartsOrder o) => Math.Round(o.Lines.Sum(l => Math.Max(0m, l.Qty) * Math.Max(0m, l.Price)), 2);

    /// <summary>Доплата после предоплаты (не меньше нуля): итог минус предоплата, если она не возвращена.</summary>
    public static decimal Remainder(PartsOrder o) =>
        Math.Max(0m, Total(o) - (o.PrepaymentRefunded ? 0m : Math.Max(0m, o.Prepayment)));

    /// <summary>Предложение мастера: 50 % от итога, целыми сомами.</summary>
    public static decimal SuggestPrepayment(decimal total) => Math.Round(Math.Max(0m, total) / 2m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Предоплата не может быть больше итога заказа.</summary>
    public static decimal ClampPrepayment(decimal value, decimal total) => Math.Clamp(Math.Round(value, 2), 0m, Math.Max(0m, total));

    public static bool IsOpen(string status) => status is PartsOrderStatus.New or PartsOrderStatus.Ordered or PartsOrderStatus.Arrived;

    /// <summary>Следующий статус цепочки Новый → Заказан → Пришёл → Выдан; null — дальше некуда.</summary>
    public static string? Next(string status) => status switch
    {
        PartsOrderStatus.New => PartsOrderStatus.Ordered,
        PartsOrderStatus.Ordered => PartsOrderStatus.Arrived,
        PartsOrderStatus.Arrived => PartsOrderStatus.Issued,
        _ => null,
    };

    /// <summary>Предоплата на руках (взята и не возвращена) у открытых заказов — для строки «Предоплаты по заказам».</summary>
    public static decimal PrepaymentsOnHand(IEnumerable<PartsOrder> orders) =>
        orders.Where(o => IsOpen(o.Status) && !o.PrepaymentRefunded).Sum(o => Math.Max(0m, o.Prepayment));

    /// <summary>Подсказка тарифа «Старт»: открытых заказов уже много. null — всё в порядке.</summary>
    public static string? SoftLimitHint(int openCount, bool startTariff) =>
        startTariff && openCount > StartOpenSoftLimit
            ? Tr.T($"Открытых заказов: {openCount}. На тарифе «Стандарт» заказы под клиента удобнее вести без ограничений — вводить можно и дальше.",
                $"Ачык заказдар: {openCount}. «Стандарт» тарифинде кардарга заказдарды чектөөсүз жүргүзүү ыңгайлуу — киргизе берсеңиз болот.",
                $"Open orders: {openCount}. The Standard plan is better for many customer orders — you can keep adding.",
                $"Açık sipariş: {openCount}. Çok sayıda müşteri siparişi için «Standart» tarife daha uygun — girmeye devam edebilirsiniz.",
                $"Ochiq buyurtmalar: {openCount}. Ko'p buyurtma uchun «Standart» tarif qulay — kiritishni davom ettirishingiz mumkin.")
            : null;

    public static string StatusName(string status) => status switch
    {
        PartsOrderStatus.New => Tr.T("Новый", "Жаңы", "New", "Yeni", "Yangi"),
        PartsOrderStatus.Ordered => Tr.T("Заказан", "Заказ берилди", "Ordered", "Sipariş verildi", "Buyurtma berildi"),
        PartsOrderStatus.Arrived => Tr.T("Пришёл", "Келди", "Arrived", "Geldi", "Keldi"),
        PartsOrderStatus.Issued => Tr.T("Выдан", "Берилди", "Issued", "Teslim edildi", "Berildi"),
        PartsOrderStatus.Cancelled => Tr.T("Отменён", "Жокко чыгарылды", "Cancelled", "İptal", "Bekor qilindi"),
        _ => status,
    };

    /// <summary>Текст клиенту в WhatsApp: «Ваш заказ №… готов» (статус «Пришёл») или «ожидается к …».</summary>
    public static string WhatsAppText(PartsOrder o, string? shop)
    {
        var shopName = string.IsNullOrWhiteSpace(shop) ? Tr.T("нашем магазине", "биздин дүкөндө", "our shop", "mağazamızda", "do'konimizda") : shop.Trim();
        var n = o.Number;
        if (o.Status == PartsOrderStatus.Arrived)
        {
            var due = Remainder(o);
            return Tr.T($"Здравствуйте! Ваш заказ №{n} готов, ждём вас в {shopName}." + (due > 0 ? $" К доплате: {due:0.##} сом." : ""),
                $"Саламатсызбы! Сиздин №{n} заказыңыз даяр, {shopName} күтөбүз." + (due > 0 ? $" Төлөнө турган калдык: {due:0.##} сом." : ""),
                $"Hello! Your order No. {n} is ready — see you at {shopName}." + (due > 0 ? $" Balance due: {due:0.##} som." : ""),
                $"Merhaba! {n} numaralı siparişiniz hazır, {shopName} sizi bekliyoruz." + (due > 0 ? $" Kalan tutar: {due:0.##} som." : ""),
                $"Assalomu alaykum! №{n} buyurtmangiz tayyor, {shopName} kutamiz." + (due > 0 ? $" To'lanadigan qoldiq: {due:0.##} som." : ""));
        }
        var day = o.DueDate is { } d ? d.ToString("dd.MM.yyyy") : "—";
        return Tr.T($"Здравствуйте! Ваш заказ №{n} ожидается к {day}. Мы сообщим, когда он придёт.",
            $"Саламатсызбы! Сиздин №{n} заказыңыз {day} күнгө чейин күтүлөт. Келгенде кабарлайбыз.",
            $"Hello! Your order No. {n} is expected by {day}. We will let you know when it arrives.",
            $"Merhaba! {n} numaralı siparişiniz {day} tarihine kadar bekleniyor. Geldiğinde haber vereceğiz.",
            $"Assalomu alaykum! №{n} buyurtmangiz {day} gacha kutilmoqda. Kelganda xabar beramiz.");
    }
}

/// <summary>Файл заказов (образец — <see cref="EstimateStore"/>): запись через AtomicFile.Update (замок + .bak); испорченный файл
/// откладывается рядом как parts-orders.json.bad, список начинается заново, исключений наружу нет.</summary>
public sealed class PartsOrderStore
{
    public const string FileName = "parts-orders.json";
    private const int MaxOrders = 2000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTime> _now;

    public PartsOrderStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    public static PartsOrderStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public DateTime Now => _now();

    private static bool Valid(string text)
    {
        try { return JsonSerializer.Deserialize<PartsOrdersData>(text, Json) is not null; }
        catch (JsonException) { return false; }
    }

    private static PartsOrdersData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new PartsOrdersData();
        try
        {
            var data = JsonSerializer.Deserialize<PartsOrdersData>(text, Json) ?? new PartsOrdersData();
            data.Orders ??= new List<PartsOrder>();
            foreach (var o in data.Orders)
            {
                o.Lines ??= new List<PartsOrderLine>();
                o.ClientId ??= "";
                o.ClientName ??= "";
                o.Phone ??= "";
                o.Supplier ??= "";
                o.Comment ??= "";
                o.Status = string.IsNullOrWhiteSpace(o.Status) ? PartsOrderStatus.New : o.Status;
            }
            return data;
        }
        catch (JsonException)
        {
            return new PartsOrdersData();
        }
    }

    public PartsOrdersData Load()
    {
        try
        {
            return Parse(AtomicFile.ReadAllText(_path, Valid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PartsOrdersData();
        }
    }

    private void Mutate(Func<PartsOrdersData, bool> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            if (!string.IsNullOrWhiteSpace(text) && !Valid(text))
            {
                try { File.WriteAllText(_path + ".bad", text); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var data = Parse(text);
            return change(data) ? JsonSerializer.Serialize(data, Json) : null;
        }, Valid);
    }

    /// <summary>Все заказы: новые сверху.</summary>
    public List<PartsOrder> All() => Load().Orders.OrderByDescending(o => o.UpdatedAt).ThenByDescending(o => o.Number).ToList();

    public PartsOrder? Find(string id) => Load().Orders.FirstOrDefault(o => o.Id == id);

    public int OpenCount() => Load().Orders.Count(o => PartsOrderRules.IsOpen(o.Status));

    /// <summary>Сохранить заказ (новый — с номером под замком, существующий — заменяется). Номер возвращается в самом объекте.</summary>
    public void Save(PartsOrder order)
    {
        Mutate(d =>
        {
            order.UpdatedAt = _now();
            var i = d.Orders.FindIndex(o => o.Id == order.Id);
            if (i >= 0)
                d.Orders[i] = order;
            else
            {
                if (order.CreatedAt == default)
                    order.CreatedAt = order.UpdatedAt;
                d.LastNumber = Math.Max(d.LastNumber, d.Orders.Count == 0 ? 0 : d.Orders.Max(o => o.Number)) + 1;
                order.Number = d.LastNumber;
                d.Orders.Add(order);
                if (d.Orders.Count > MaxOrders)
                    d.Orders.Remove(d.Orders.Where(o => o.Id != order.Id && !PartsOrderRules.IsOpen(o.Status)).OrderBy(o => o.UpdatedAt).FirstOrDefault()
                                    ?? d.Orders.Where(o => o.Id != order.Id).OrderBy(o => o.UpdatedAt).First()); // предел размера файла — самый старый закрытый
            }
            return true;
        });
    }

    /// <summary>Перевести заказ на следующий статус цепочки. false — заказа нет или дальше некуда.</summary>
    public bool Advance(string id)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || PartsOrderRules.Next(o.Status) is not { } next)
                return false;
            o.Status = next;
            o.UpdatedAt = _now();
            return done = true;
        });
        return done;
    }

    /// <summary>Отмена заказа (PIN руководителя спрашивает окно ДО вызова). Предоплата отмечается возвращённой — деньги отдают из конверта.</summary>
    public bool Cancel(string id, string? note)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !PartsOrderRules.IsOpen(o.Status))
                return false;
            o.Status = PartsOrderStatus.Cancelled;
            o.CancelledNote = note;
            if (o.Prepayment > 0m)
                o.PrepaymentRefunded = true;
            o.UpdatedAt = _now();
            return done = true;
        });
        return done;
    }

    /// <summary>Заказ выдан (чек пробит): статус «Выдан». Предоплата считается зачтённой.</summary>
    public bool MarkIssued(string id)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !PartsOrderRules.IsOpen(o.Status))
                return false;
            o.Status = PartsOrderStatus.Issued;
            o.UpdatedAt = _now();
            return done = true;
        });
        return done;
    }
}
