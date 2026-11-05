using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 3, пункт е): утренняя проверка «пора заказать у N поставщиков на X сом» без открытия окна «Закупки»:
// по каждому поставщику берётся рекомендация сервера (та же, что в «Рекомендуемом заказе»), к ней применяются правила автозаказа v2
// (кратность упаковки, минимальная партия, срок поставки поставщика). Кнопка «Сформировать заказы» создаёт по черновику заказа на поставщика.
// Включается настройкой MorningOrderCheck; сервер не меняется.

/// <summary>Поставщик и строки, которые пора заказать.</summary>
public sealed record AutoOrderSupplier(SupplierInfo Supplier, IReadOnlyList<PlannerRow> Rows)
{
    public double Total => Math.Round(Rows.Sum(r => r.Recommended * Math.Max(0, r.Product.PurchasePrice)), 2);
}

/// <summary>Итог проверки. Waiting — поставщики, у которых сумма ниже их минимальной суммы заказа (в «пора заказать» не входят).</summary>
public sealed record AutoOrderResult(IReadOnlyList<AutoOrderSupplier> Suppliers, int Waiting, int Failed)
{
    public int Count => Suppliers.Count;
    public double Total => Math.Round(Suppliers.Sum(s => s.Total), 2);
}

public static class AutoOrderCheck
{
    private const int MaxSuppliers = 40;

    /// <summary>Последний результат проверки (для утреннего отчёта и кнопки «Сформировать заказы» в Telegram) и когда он получен.</summary>
    // 2026-11-05 (ревью волн 0–3, С14): результат хранится ещё и в общем файле (папка NurMarketKassa в LocalAppData, auto-order-latest.json): кнопку «Сформировать заказы»
    // в Telegram может принять другая программа (касса опрашивает бота, проверку сделала программа владельца) или та же после перезапуска. Нет файла/не читается — как раньше (null).
    private static readonly object LatestGate = new();
    private static readonly JsonSerializerOptions LatestJson = new() { WriteIndented = false };
    private static AutoOrderResult? _latest;
    private static DateTime _latestAt;
    private static DateTime _latestStamp;

    private static string LatestFile => Path.Combine(AppPaths.Local, "NurMarketKassa", "auto-order-latest.json");

    private sealed class LatestBox
    {
        public DateTime At { get; set; }
        public AutoOrderResult? Result { get; set; }
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception) { return default; }
    }

    /// <summary>Подтянуть из файла, если его изменила другая программа (или файла нет, а в памяти осталось старое).</summary>
    private static void SyncLatest()
    {
        var stamp = StampOf(LatestFile);
        if (stamp == _latestStamp)
            return;
        _latestStamp = stamp;
        if (stamp == default)
        {
            _latest = null;
            _latestAt = default;
            return;
        }
        try
        {
            var box = JsonSerializer.Deserialize<LatestBox>(File.ReadAllText(LatestFile), LatestJson);
            _latest = box?.Result;
            _latestAt = box?.At ?? default;
        }
        catch (Exception)
        {
            _latest = null;
            _latestAt = default;
        }
    }

    private static void SaveLatest()
    {
        try
        {
            if (_latest is null)
            {
                if (File.Exists(LatestFile))
                    File.Delete(LatestFile);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LatestFile)!);
                AtomicFile.WriteAllText(LatestFile, JsonSerializer.Serialize(new LatestBox { At = _latestAt, Result = _latest }, LatestJson));
            }
            _latestStamp = StampOf(LatestFile);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автозаказ: результат проверки не записан в файл ({ex.GetType().Name}).", "WARNING");
        }
    }

    public static AutoOrderResult? Latest
    {
        get
        {
            lock (LatestGate)
            {
                SyncLatest();
                return _latest;
            }
        }
        set
        {
            lock (LatestGate)
            {
                _latest = value;
                if (value is null)
                    _latestAt = default;
                SaveLatest();
            }
        }
    }

    public static DateTime LatestAt
    {
        get
        {
            lock (LatestGate)
            {
                SyncLatest();
                return _latestAt;
            }
        }
        set
        {
            lock (LatestGate)
            {
                _latestAt = value;
                if (_latest is not null)
                    SaveLatest();
            }
        }
    }

    /// <summary>Чистая часть: строки сервера по поставщику → «пора заказать» с учётом правил (кратность/минимум) и минимальной суммы поставщика.
    /// null — поставщику нечего заказывать или сумма ниже минимальной (тогда waiting = true).</summary>
    public static AutoOrderSupplier? Pick(SupplierInfo supplier, IReadOnlyList<PlannerRow> serverRows, ShopAutomationData settings, out bool waiting)
    {
        waiting = false;
        var rows = serverRows.Where(r => r.Status != PlannerStatus.NoSales).ToList();
        if (settings.PlanPackRules)
            rows = PlannerV2Math.ApplyOrderRules(rows, PlannerV2Math.RulesFrom(settings));
        rows = rows.Where(r => r.Recommended > 0).ToList();
        if (rows.Count == 0)
            return null;
        var pick = new AutoOrderSupplier(supplier, rows);
        if (settings.Suppliers.TryGetValue(supplier.Id, out var rule) && rule.MinOrderSum > 0 && pick.Total < rule.MinOrderSum)
        {
            waiting = true;
            return null;
        }
        return pick;
    }

    public static async Task<AutoOrderResult> ComputeAsync(SupplierApiService svc, PlannerSettings planner, CancellationToken ct)
    {
        var settings = ShopAutomationSettings.Current;
        var suppliers = await svc.ListAsync(ct).ConfigureAwait(false);
        var picks = new List<AutoOrderSupplier>();
        int waiting = 0, failed = 0;
        foreach (var supplier in suppliers.Take(MaxSuppliers))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Срок поставки поставщика прибавляем к запасу до закупки: сервер считает «продажи в день × (запас + страховой) − остаток».
                var lead = settings.PlanLeadTime ? PlannerV2Math.LeadDaysOf(settings, supplier.Id) : 0;
                var rows = await svc.GetRecommendationsAsync(supplier.Id, planner with { CoverDays = Math.Min(365, planner.CoverDays + lead) }, ct).ConfigureAwait(false);
                if (rows is null)
                    continue; // у сервера нет рекомендаций — утренняя проверка по таким поставщикам не считается
                if (Pick(supplier, rows, settings, out var wait) is { } pick)
                    picks.Add(pick);
                else if (wait)
                    waiting++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                PosLogger.Log($"Утренняя проверка заказов: поставщик «{supplier.Name}» пропущен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
        var result = new AutoOrderResult(picks.OrderByDescending(p => p.Total).ToList(), waiting, failed);
        Latest = result;
        LatestAt = DateTime.Now;
        return result;
    }

    /// <summary>Строка для «Сводки» и Telegram: «Пора заказать у 3 поставщиков на 48 000 сом»; пустая — заказывать нечего.</summary>
    public static string SummaryText(AutoOrderResult r)
    {
        if (r.Count == 0)
            return "";
        var money = MoneyText.Som(r.Total);
        return Tr.T($"Пора заказать у {r.Count} {Plural(r.Count)} на {money}", $"{r.Count} жеткирүүчүдөн буйрутма берүүгө убакыт: {money}",
            $"Time to order from {r.Count} supplier{(r.Count == 1 ? "" : "s")}: {money}", $"{r.Count} tedarikçiden sipariş zamanı: {money}",
            $"{r.Count} ta yetkazib beruvchidan buyurtma berish vaqti: {money}");
    }

    private static string Plural(int n) => n % 100 is >= 11 and <= 14 ? "поставщиков" : n % 10 == 1 ? "поставщика" : "поставщиков";

    /// <summary>Кнопка «Сформировать заказы» в Telegram: черновики по последней проверке (не старше суток). Возвращает текст ответа владельцу.
    /// Повторное нажатие заказы второй раз не создаёт (результат сбрасывается).</summary>
    public static string MakeDraftsFromLatest(string author)
    {
        var latest = Latest;
        if (latest is null || latest.Count == 0 || DateTime.Now - LatestAt > TimeSpan.FromHours(24))
            return Tr.T("Список устарел или заказы уже сформированы. Откройте «Закупки → Рекомендуемый заказ».", "Тизме эскирди же буйрутмалар түзүлүп калды. «Сатып алуулар → Сунушталган буйрутма» бөлүмүн ачыңыз.",
                "The list is outdated or the orders were already made. Open Purchases → Recommended order.", "Liste eskidi veya siparişler zaten oluşturuldu. Satın almalar → Önerilen sipariş bölümünü açın.", "Ro'yxat eskirgan yoki buyurtmalar allaqachon yaratilgan. Xaridlar → Tavsiya etilgan buyurtma bo'limini oching.");
        Latest = null;
        var made = MakeDrafts(latest, author);
        PosLogger.Log($"Автозаказ: по кнопке в Telegram созданы черновики заказов — {made.Count}.", "INFO");
        return Tr.T($"Готово: черновиков заказов — {made.Count}. Они в программе: Закупки → Заказы поставщикам (проверьте и отправьте).",
            $"Даяр: буйрутма долбоорлору — {made.Count}. Программада: Сатып алуулар → Жеткирүүчүлөргө буйрутмалар (текшерип жөнөтүңүз).",
            $"Done: {made.Count} draft order(s). They are in the program: Purchases → Supplier orders (review and send).",
            $"Hazır: {made.Count} sipariş taslağı. Programda: Satın almalar → Tedarikçi siparişleri (kontrol edip gönderin).",
            $"Tayyor: buyurtma qoralamalari — {made.Count}. Dasturda: Xaridlar → Buyurtmalar (tekshirib yuboring).");
    }

    /// <summary>Создать по черновику заказа на поставщика (как «Сохранить заказ» в «Рекомендуемом заказе»). Возвращает номера заказов.</summary>
    public static List<PurchaseOrder> MakeDrafts(AutoOrderResult r, string author)
    {
        var made = new List<PurchaseOrder>();
        foreach (var s in r.Suppliers)
        {
            var order = new PurchaseOrder
            {
                SupplierId = s.Supplier.Id,
                SupplierName = s.Supplier.Name,
                SupplierPhone = s.Supplier.Phone,
                Author = author,
                Note = Tr.T("Создан автозаказом (утренняя проверка)", "Автобуйрутма түздү (эртең мененки текшерүү)", "Created by auto-order (morning check)",
                    "Otomatik sipariş oluşturdu (sabah kontrolü)", "Avtobuyurtma yaratdi (ertalabki tekshiruv)"),
                Lines = s.Rows.Select(row => new PurchaseOrderLine
                {
                    ProductId = row.Product.Id,
                    Name = row.Product.Name,
                    Code = row.Product.Code,
                    Unit = row.Product.Unit,
                    Qty = row.Recommended,
                    Price = row.Product.PurchasePrice,
                }).ToList(),
            };
            made.Add(PurchasesStore.SaveOrder(order));
        }
        return made;
    }

    /// <summary>Сообщение «Спросить цены у других поставщиков»: тот же список товаров всем запасным поставщикам. Без цен закупки магазина.</summary>
    public static string AskPricesText(string? shopName, IEnumerable<(string Name, double Qty, string Unit)> lines)
    {
        var sb = new StringBuilder();
        sb.Append(Tr.T("Здравствуйте! ", "Саламатсызбы! ", "Hello! ", "Merhaba! ", "Assalomu alaykum! "));
        if (!string.IsNullOrWhiteSpace(shopName))
            sb.Append(shopName.Trim()).Append(". ");
        sb.AppendLine(Tr.T("Подскажите, пожалуйста, цену и наличие:", "Бааны жана бар-жогун айтып бериңизчи:", "Please tell us the price and availability:",
            "Lütfen fiyat ve stok durumunu bildirin:", "Narx va mavjudligini aytib bering:"));
        var i = 0;
        foreach (var (name, qty, unit) in lines.Where(l => l.Qty > 0))
            sb.Append(++i).Append(". ").Append(name).Append(" — ").Append(qty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').AppendLine(string.IsNullOrWhiteSpace(unit) ? Tr.T("шт", "даана", "pcs", "adet", "dona") : unit); // 2026-11-05 (Н12)
        sb.Append(Tr.T("Спасибо!", "Рахмат!", "Thank you!", "Teşekkürler!", "Rahmat!"));
        return sb.ToString();
    }
}
