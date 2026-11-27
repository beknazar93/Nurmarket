using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-26 (выгрузка в 1С, <see cref="OneCExport"/>): собирает данные периода из тех же сервисов, что экраны, — ничего заново не считает.
/// Продажи — <see cref="SalesExcelExport.LoadAsync"/> (тот же список «Чеки», что и кнопка «Excel»), возвраты — NurCrmReportsApi.ListReturnsAsync,
/// приходы — SupplierApiService.GetReceiptsPageAsync (как «История закупок»), клиенты и поставщики — их API, номенклатура и остатки — каталог кассы.
/// Что не загрузилось (нет связи, нет права) — не обрывает выгрузку: остальное пишется, а причина идёт в предупреждения окна.</summary>
internal static class OneCExportLoader
{
    private const int ReceiptPageSize = 200;
    private const int ReceiptMaxPages = 100;

    public sealed record Loaded(OneCExport.Data Data, IReadOnlyList<string> Warnings);

    public static async Task<Loaded> LoadAsync(DateTime from, DateTime to, IReadOnlyCollection<OneCExport.Part> parts, IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        from = from.Date;
        to = to.Date;

        // Снимок каталога — на потоке окна, до первого await (общий список каталога не потокобезопасен).
        var products = parts.Contains(OneCExport.Part.Products) || parts.Contains(OneCExport.Part.Stock)
            ? Products(CatalogCacheService.Products.ToList())
            : [];

        IReadOnlyList<SalesExportService.Receipt> sales = [];
        decimal? serverRevenue = null;
        if (parts.Contains(OneCExport.Part.Sales))
        {
            progress?.Report(Step(L("продажи", "сатуулар", "sales", "satışlar", "sotuvlar")));
            var loaded = await SalesExcelExport.LoadAsync(from, to, progress, ct).ConfigureAwait(false);
            sales = loaded.Receipts;
            // 2026-11-26 (стресс-тест): выручка сводки сервера — окно сверяет с ней сумму чеков в файле (OneCExport.TotalsLines)
            if (loaded.ServerRevenue is { } sr && !double.IsNaN(sr) && !double.IsInfinity(sr))
                serverRevenue = OneCExport.Money(sr);
            if (loaded.LocalOnly)
                warnings.Add(L("Продажи — по данным этой кассы (нет связи с сервером): способа оплаты, кассира и скидок в них нет.",
                    "Сатуулар — ушул кассанын маалыматы боюнча (сервер менен байланыш жок): төлөм түрү, кассир жана арзандатуулар жок.",
                    "Sales are from this till's data (no connection to the server): no payment method, cashier or discounts.",
                    "Satışlar bu kasanın verisiyle (sunucu bağlantısı yok): ödeme yöntemi, kasiyer ve indirim yok.",
                    "Sotuvlar shu kassa ma'lumotlari bo'yicha (server bilan aloqa yo'q): to'lov turi, kassir va chegirmalar yo'q."));
            if (loaded.Truncated || loaded.LinesSkipped > 0 || loaded.LinesFailed > 0)
                warnings.Add(L("Продажи выгружены не полностью (слишком большой период или сервер не отдал состав части чеков) — выберите период короче.",
                    "Сатуулар толук чыгарылган жок (мезгил өтө чоң же сервер чектердин бир бөлүгүнүн курамын бербеди) — кыскараак мезгил тандаңыз.",
                    "Sales were not exported in full (the period is too large or the server did not return the contents of some receipts) — choose a shorter period.",
                    "Satışlar tam aktarılmadı (dönem çok büyük veya sunucu bazı fişlerin içeriğini vermedi) — daha kısa dönem seçin.",
                    "Sotuvlar to'liq chiqarilmadi (davr juda katta yoki server ba'zi cheklar tarkibini bermadi) — qisqaroq davr tanlang."));
        }

        var returns = new List<OneCExport.ReturnRow>();
        if (parts.Contains(OneCExport.Part.Returns))
        {
            progress?.Report(Step(L("возвраты", "кайтаруулар", "returns", "iadeler", "qaytarishlar")));
            try
            {
                var list = await NurCrmReportsApi.ListReturnsAsync(from, to, ct: ct).ConfigureAwait(false);
                if (list is null)
                    warnings.Add(NoServer(OneCExport.PartTitle(OneCExport.Part.Returns)));
                else
                    returns.AddRange(list.Select(r => new OneCExport.ReturnRow(r.Id, !string.IsNullOrWhiteSpace(r.Number) ? r.Number! : "№" + ShortId(r.Id),
                        r.CreatedAt?.LocalDateTime, r.SaleNumber?.ToString(CultureInfo.InvariantCulture) ?? "", Math.Round(r.Amount, 2, MidpointRounding.AwayFromZero),
                        r.Reason ?? "", r.ItemsText ?? "")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(Failed(OneCExport.PartTitle(OneCExport.Part.Returns), ex));
            }
        }

        var receipts = new List<OneCExport.ReceiptRow>();
        var parties = new List<OneCExport.PartyRow>();
        SupplierApiService? suppliers = null;
        if (parts.Contains(OneCExport.Part.Receipts) || parts.Contains(OneCExport.Part.Counterparties))
        {
            try
            {
                suppliers = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Выгрузка в 1С: поставщики недоступны ({ex.Message}).", "WARNING");
            }
        }

        if (parts.Contains(OneCExport.Part.Receipts))
        {
            progress?.Report(Step(L("приходы", "кирешелер", "receipts", "girişler", "kirimlar")));
            try
            {
                if (suppliers is null)
                    throw new InvalidOperationException(NoServer(OneCExport.PartTitle(OneCExport.Part.Receipts)));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var page = 1; page <= ReceiptMaxPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (list, _, _, hasNext) = await suppliers.GetReceiptsPageAsync(page, ReceiptPageSize, null, from, to, ct).ConfigureAwait(false);
                    foreach (var r in list)
                    {
                        if (!seen.Add(r.Id) || (r.CreatedAt is { } at && (at.Date < from || at.Date > to)))
                            continue;
                        receipts.Add(Receipt(r));
                    }
                    if (!hasNext || list.Count == 0)
                        break;
                    // 2026-11-26 (стресс-тест): раньше упор в предел страниц молча обрезал приходы — теперь честное предупреждение
                    if (page == ReceiptMaxPages)
                        warnings.Add(L($"Приходы выгружены не полностью (больше {ReceiptMaxPages * ReceiptPageSize}) — выберите период короче.",
                            $"Кирешелер толук чыгарылган жок ({ReceiptMaxPages * ReceiptPageSize} ашык) — кыскараак мезгил тандаңыз.",
                            $"Receipts were not exported in full (more than {ReceiptMaxPages * ReceiptPageSize}) — choose a shorter period.",
                            $"Girişler tam aktarılmadı ({ReceiptMaxPages * ReceiptPageSize} üzeri) — daha kısa dönem seçin.",
                            $"Kirimlar to'liq chiqarilmadi ({ReceiptMaxPages * ReceiptPageSize} dan ko'p) — qisqaroq davr tanlang."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(Failed(OneCExport.PartTitle(OneCExport.Part.Receipts), ex));
            }
        }

        if (parts.Contains(OneCExport.Part.Counterparties))
        {
            progress?.Report(Step(L("контрагенты", "контрагенттер", "counterparties", "cariler", "kontragentlar")));
            try
            {
                foreach (var c in await App.GetRequiredService<IClientsApiService>().GetClientsAsync(null, ct).ConfigureAwait(false))
                {
                    var name = Str(c, "full_name") ?? Str(c, "name") ?? "";
                    if (name.Length > 0)
                        parties.Add(new OneCExport.PartyRow(false, Str(c, "id") ?? "", name, Str(c, "phone") ?? "", Str(c, "email") ?? "", "", "", "", "", Str(c, "address") ?? ""));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(Failed(L("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"), ex));
            }

            try
            {
                if (suppliers is not null)
                    foreach (var s in await suppliers.ListAsync(ct).ConfigureAwait(false))
                        parties.Add(new OneCExport.PartyRow(true, s.Id, string.IsNullOrWhiteSpace(s.Llc) ? s.Name : s.Llc, s.Phone, s.Email, s.Inn, s.Okpo, s.Score, s.Bik, s.Address));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(Failed(L("Поставщики", "Жеткирүүчүлөр", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilar"), ex));
            }
        }

        return new Loaded(new OneCExport.Data(from, to, ReportShopName.Current, products, sales, returns, receipts, parties, serverRevenue), warnings);
    }

    private static IReadOnlyList<OneCExport.ProductRow> Products(List<NurMarketKassa.Models.Pos.CatalogProductTileVm> catalog) =>
        catalog.Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Select(p => new OneCExport.ProductRow(p.Id ?? "",
                (!string.IsNullOrWhiteSpace(p.Article) ? p.Article : p.ProductCode) ?? "",
                p.Barcode ?? "", p.Title.Trim(), p.Category ?? "",
                string.IsNullOrWhiteSpace(p.Unit) ? L("шт", "даана", "pcs", "adet", "dona") : p.Unit!.Trim(),
                OneCExport.Money(p.PriceValue), OneCExport.Money(p.PurchasePrice), OneCExport.Qty(p.Quantity)))
            .ToList();

    private static OneCExport.ReceiptRow Receipt(SupplierReceipt r)
    {
        // 2026-11-26 (стресс-тест): без номера раньше в «Номер прихода» уходил весь служебный комментарий («№ …; накладная …; ЭСФ …») — теперь короткий номер
        var number = new[] { r.NumberDisplay, r.ServerNumber }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        return new OneCExport.ReceiptRow(r.Id, number ?? "№" + ShortId(r.Id), r.CreatedAt, r.SupplierName, r.SupplierDocNumber, OneCExport.Money(r.Total),
            r.Lines.Select(l =>
            {
                var qty = OneCExport.Qty(l.Qty);
                var price = (decimal)l.PurchasePrice;
                return new OneCExport.ReceiptLineRow(l.ProductName, l.Code, l.Barcode ?? "", l.Unit, qty, Math.Round(price, 4, MidpointRounding.AwayFromZero),
                    Math.Round(qty * price, 2, MidpointRounding.AwayFromZero));
            }).ToList(), r.EsfNumber);
    }

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Step(string what) => L("Готовлю выгрузку… ", "Чыгаруу даярдалууда… ", "Preparing the export… ", "Aktarım hazırlanıyor… ", "Eksport tayyorlanmoqda… ") + what;

    private static string NoServer(string what) => what + ": " + L("нет связи с сервером — раздел пропущен.", "сервер менен байланыш жок — бөлүм өткөрүлдү.",
        "no connection to the server — section skipped.", "sunucu bağlantısı yok — bölüm atlandı.", "server bilan aloqa yo'q — bo'lim o'tkazib yuborildi.");

    private static string Failed(string what, Exception ex)
    {
        PosLogger.Log($"Выгрузка в 1С: «{what}» не загружено: {ex.Message}", "WARNING");
        return what + ": " + L("не удалось загрузить — раздел пропущен (", "жүктөө мүмкүн болгон жок — бөлүм өткөрүлдү (", "could not be loaded — section skipped (",
            "yüklenemedi — bölüm atlandı (", "yuklab bo'lmadi — bo'lim o'tkazib yuborildi (") + ex.Message + ")";
    }
}
