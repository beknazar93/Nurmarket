using System.Globalization;
using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;


namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец: «дай доступ ко всему для ИИ — к долгам, к контактам, клиентам, к складу, к товарам,
/// поставщикам». Склад для «ИИ-советника» программы владельца: товары с категорией, ценой продажи, закупкой и
/// остатком (локальный каталог программы) и итоги склада. Персональные данные (имена и телефоны клиентов,
/// должники, контакты поставщиков) сюда не входят — их отправка в Google ждёт решения владельца.</summary>
public static class OwnerAiContext
{
    private static readonly SemaphoreSlim DebtGate = new(1, 1);
    private static string? _debtCached;
    private static DateTime _debtCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «я должен кому-то или мне должны?» — советник не знал. Обезличенные итоги
    /// долгов: сколько должны клиенты (сумма, сколько человек и чеков, крупнейшие суммы и давность — без имён и
    /// телефонов) и поставщики (число и сумма долга, если сервер её отдаёт). Сервер — не чаще раза в 5 минут.</summary>
    public static async Task<string> BuildDebtTotalsAsync(CancellationToken ct)
    {
        await DebtGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_debtCached is not null && DateTime.UtcNow - _debtCachedAtUtc < TimeSpan.FromMinutes(5))
                return _debtCached;
            var sb = new StringBuilder();
            try
            {
                var debts = await App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>()
                    .PosDebtSalesAsync(null, ct).ConfigureAwait(false);
                var amounts = new List<double>();
                var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime? oldest = null;
                foreach (var debt in debts)
                {
                    // debt_amount — остаток долга; нет его — сумма чека (как в отчёте бота «Должники»).
                    var amount = Num(debt, "debt_amount") ?? Num(debt, "total") ?? 0;
                    if (amount <= 0.005)
                        continue;
                    amounts.Add(amount);
                    if (Str(debt, "client") is { Length: > 0 } client)
                        clients.Add(client);
                    if (DateTime.TryParse(Str(debt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
                        && (oldest is null || at < oldest))
                        oldest = at;
                }
                sb.AppendLine(amounts.Count == 0
                    ? "ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (сервер NurCRM): непогашенных долгов нет."
                    : $"ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (сервер NurCRM): вам должны {amounts.Sum().ToString("N2", Ru)} сом — {clients.Count} клиент(ов), " +
                      $"{amounts.Count} чек(ов) в долг; крупнейшие долги по чекам: {string.Join(", ", amounts.OrderByDescending(a => a).Take(5).Select(a => a.ToString("N2", Ru)))} сом" +
                      (oldest is { } o ? $"; самый старый долг с {o.ToLocalTime():dd.MM.yyyy}" : "") +
                      ". Имена и телефоны должников — в разделе «Клиенты» (советнику они не передаются).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: долги клиентов не получены ({ex.Message}).", "WARNING");
                sb.AppendLine("ДОЛГИ КЛИЕНТОВ: сервер сейчас не ответил — подскажи раздел «Клиенты».");
            }

            try
            {
                var data = await PosApp.CatalogApi.ListSuppliersAsync(ct).ConfigureAwait(false);
                var rows = data.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? data.EnumerateArray().ToList()
                    : data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? r.EnumerateArray().ToList()
                        : new List<System.Text.Json.JsonElement>();
                var owed = rows.Select(row => Num(row, "debt") ?? Num(row, "total_debt") ?? Num(row, "balance")).Where(v => v is not null).Select(v => v!.Value).ToList();
                sb.AppendLine(rows.Count == 0
                    ? "ПОСТАВЩИКИ (сервер NurCRM): поставщиков в базе нет."
                    : owed.Count == 0
                        ? $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; сумм долга перед поставщиками сервер не отдаёт — подскажи раздел «Склад» → приходы."
                        : $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; по данным сервера сумма по поставщикам {owed.Sum().ToString("N2", Ru)} сом " +
                          $"(у {owed.Count(v => Math.Abs(v) > 0.005)} есть ненулевая сумма).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: поставщики не получены ({ex.Message}).", "WARNING");
            }

            _debtCached = sb.ToString().Trim();
            _debtCachedAtUtc = DateTime.UtcNow;
            return _debtCached;
        }
        finally
        {
            DebtGate.Release();
        }
    }

    private static readonly SemaphoreSlim AbcGate = new(1, 1);
    private static string? _abcCached;
    private static DateTime _abcCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «дай доступ к ABC-анализу ИИ». Тот же расчёт, что в разделе «ABC-анализ»
    /// (AnalyticsReportData — цифры сервера NurCRM, без связи — история продаж) за последние 30 дней, все срезы:
    /// выручка, прибыль, количество, категории, бренды, склад. По каждому срезу — итоги групп A/B/C и товары
    /// группы (A — все до 40, B и C — до 25). Пересчёт не чаще раза в 10 минут.</summary>
    public static async Task<string> BuildAbcAsync(CancellationToken ct)
    {
        await AbcGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_abcCached is not null && DateTime.UtcNow - _abcCachedAtUtc < TimeSpan.FromMinutes(10))
                return _abcCached;
            var to = DateTime.Today;
            var from = to.AddDays(-29);
            var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, includeSeasonality: false, ct: ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"ABC-АНАЛИЗ за {from:dd.MM}–{to:dd.MM.yyyy} (как в разделе «ABC-анализ»; A — первые 80 % меры, B — следующие 15 %, C — остальные 5 %):");
            if (data.AbcSlices.Count == 0)
                sb.AppendLine("продаж за период нет — ABC не построен.");
            foreach (var slice in data.AbcSlices)
            {
                string Fmt(double v) => slice.IsMoney ? $"{v.ToString("N2", Ru)} сом" : $"{v.ToString("0.###", CultureInfo.InvariantCulture)} {slice.Unit}";
                sb.AppendLine($"Срез «{slice.Title}»: " + string.Join("; ", slice.Summary.Select(g =>
                    $"группа {g.Group} — {g.Count} поз., {Fmt(g.Sum)} ({g.Share:0.#} %)")));
                foreach (var group in new[] { "A", "B", "C" })
                {
                    var rows = slice.Rows.Where(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (rows.Count == 0)
                        continue;
                    var take = group == "A" ? 40 : 25;
                    sb.AppendLine($"  {group}: " + string.Join("; ", rows.Take(take).Select(r => $"{r.Name} — {Fmt(r.Sum)} ({r.Share:0.#} %)"))
                                  + (rows.Count > take ? $"; … и ещё {rows.Count - take}" : ""));
                }
            }
            _abcCached = sb.ToString().TrimEnd();
            _abcCachedAtUtc = DateTime.UtcNow;
            return _abcCached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: ABC-анализ не построен ({ex.Message}).", "WARNING");
            return "ABC-АНАЛИЗ: сейчас не построился — подскажи раздел «ABC-анализ».";
        }
        finally
        {
            AbcGate.Release();
        }
    }

    /// <summary>2026-10-05, владелец: «дай список клиентов-должников». Имена и телефоны клиентов в Google не уходят:
    /// нейросеть видит должников под кодами [Д1], [Д2]… (сумма, число чеков, с какой даты), а настоящие имя и
    /// телефон подставляет в её ответ сама программа (<see cref="RevealDebtors"/>) — на этом компьютере.</summary>
    public static async Task<(string AiText, IReadOnlyDictionary<int, string> Names)> BuildDebtorsPseudonymousAsync(CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        try
        {
            var debts = await App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>()
                .PosDebtSalesAsync(null, ct).ConfigureAwait(false);
            var byClient = new Dictionary<string, (string Name, double Amount, int Count, DateTime? Oldest)>(StringComparer.OrdinalIgnoreCase);
            foreach (var debt in debts)
            {
                var clientId = Str(debt, "client");
                var amount = Num(debt, "debt_amount") ?? Num(debt, "total") ?? 0;
                if (string.IsNullOrWhiteSpace(clientId) || amount <= 0.005)
                    continue;
                DateTime? at = DateTime.TryParse(Str(debt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
                var name = Str(debt, "client_name") ?? "Без имени";
                byClient[clientId] = byClient.TryGetValue(clientId, out var e)
                    ? (e.Name, e.Amount + amount, e.Count + 1, e.Oldest is { } o && (at is null || o < at) ? o : at ?? e.Oldest)
                    : (name, amount, 1, at);
            }
            if (byClient.Count == 0)
                return ("ДОЛЖНИКИ: непогашенных долгов нет.", names);

            // Телефоны — из карточек клиентов (только для показа владельцу на этом компьютере).
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var api = App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>();
                for (var page = 1; page <= 20 && byClient.Keys.Any(k => !phones.ContainsKey(k)); page++)
                {
                    var (items, hasNext) = await api.GetClientsPageAsync(page, null, ct).ConfigureAwait(false);
                    foreach (var c in items)
                    {
                        if (Str(c, "id") is { Length: > 0 } id && Str(c, "phone") is { Length: > 0 } phone)
                            phones[id] = phone.Trim();
                    }
                    if (!hasNext)
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: телефоны должников не получены ({ex.Message}).", "WARNING");
            }

            var sb = new StringBuilder();
            sb.AppendLine("ДОЛЖНИКИ (сервер NurCRM; имена скрыты кодами, программа сама подставит имя и телефон вместо кода):");
            var n = 0;
            foreach (var (id, info) in byClient.OrderByDescending(x => x.Value.Amount).Take(60))
            {
                n++;
                names[n] = info.Name + (phones.TryGetValue(id, out var phone) ? $" (тел. {phone})" : "");
                sb.AppendLine($"• [Д{n}] — {info.Amount.ToString("N2", Ru)} сом, чеков в долг: {info.Count}" +
                              (info.Oldest is { } o ? $", долг с {o.ToLocalTime():dd.MM.yyyy}" : ""));
            }
            return (sb.ToString().TrimEnd(), names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: должники не получены ({ex.Message}).", "WARNING");
            return ("ДОЛЖНИКИ: сервер сейчас не ответил.", names);
        }
    }

    /// <summary>Коды [Д1]… в ответе нейросети → настоящие имя и телефон (только на экране владельца).</summary>
    public static string RevealDebtors(string answer, IReadOnlyDictionary<int, string> names) =>
        names.Count == 0
            ? answer
            : System.Text.RegularExpressions.Regex.Replace(answer, @"\[?Д(\d{1,3})\]?",
                m => int.TryParse(m.Groups[1].Value, out var i) && names.TryGetValue(i, out var name) ? name : m.Value);

    private static string? Str(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => v.GetString(),
                System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double? Num(System.Text.Json.JsonElement e, string name)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        return v.ValueKind == System.Text.Json.JsonValueKind.String
               && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const int MaxProducts = 600;

    /// <summary>2026-10-05, владелец: «добавь боту и ИИ отправлять фото товара, если есть». Товары каталога,
    /// названные в ответе советника (по полному названию, без учёта регистра), у которых есть фото, — в порядке
    /// упоминания, не больше <paramref name="max"/>.</summary>
    public static IReadOnlyList<CatalogProductTileVm> FindMentionedProducts(string text, int max = 4)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<CatalogProductTileVm>();
        try
        {
            IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
            if (tiles.Count == 0)
                tiles = CatalogCacheService.Products.ToList();
            return tiles
                .Where(t => t.Title.Trim().Length >= 3
                            && (!string.IsNullOrWhiteSpace(t.ImageUrl) || !string.IsNullOrWhiteSpace(t.ProductImagePath)))
                .Select(t => (Tile: t, At: text.IndexOf(t.Title.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.At >= 0)
                .OrderBy(x => x.At)
                .ThenByDescending(x => x.Tile.Title.Length)
                .Select(x => x.Tile)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .Take(max)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: товары для фото не найдены ({ex.Message}).", "WARNING");
            return Array.Empty<CatalogProductTileVm>();
        }
    }

    public static string BuildWarehouse()
    {
        try
        {
            IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
            if (tiles.Count == 0)
                tiles = CatalogCacheService.Products.ToList();
            if (tiles.Count == 0)
                return "СКЛАД: каталог в программе ещё не загружен — подскажи раздел «Склад».";

            static double Price(CatalogProductTileVm t) => LocalCartService.ParsePrice(t.PriceLine);
            var stockSale = tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * Price(t));
            var stockCost = tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * t.PurchasePrice);
            var outOfStock = tiles.Count(t => t.Quantity <= 0);
            var sb = new StringBuilder();
            sb.AppendLine($"СКЛАД (каталог программы): товаров {tiles.Count}, с нулевым остатком {outOfStock}; " +
                          $"остаток по цене продажи {stockSale.ToString("N2", Ru)} сом, по закупке {stockCost.ToString("N2", Ru)} сом.");
            sb.AppendLine("Товары — название | категория | цена продажи | закупка | остаток:");
            foreach (var t in tiles.OrderBy(t => t.Category ?? "").ThenBy(t => t.Title).Take(MaxProducts))
            {
                var unit = string.IsNullOrWhiteSpace(t.Unit) ? (t.MustWeigh ? "кг" : "шт.") : t.Unit;
                var cost = t.PurchasePrice > 0 ? t.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture) : "—";
                sb.AppendLine($"• {t.Title} | {t.Category ?? "—"} | {Price(t):0.##} | {cost} | {t.Quantity:0.###} {unit}");
            }
            if (tiles.Count > MaxProducts)
                sb.AppendLine($"… и ещё {tiles.Count - MaxProducts} товаров (полный список — раздел «Склад»).");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: склад не собран ({ex.Message}).", "WARNING");
            return "СКЛАД: данные склада сейчас недоступны.";
        }
    }
}
