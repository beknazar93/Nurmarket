using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Подробный отчёт по одной плитке «Деталей смены» (2026-09-24): «Продажи», «Наличные»,
/// «Безналичные», «Долг», «Скидки» — чеки смены с товарами; «Возвраты», «Списания», «Расход»,
/// «Оплата долгов» — записи журнала смены кассы (ShiftEventsStore), из которых и сложена цифра.</summary>
public partial class ShiftDrillDownDialog : Window
{
    public const string KindSales = "sales";
    public const string KindCash = "cash";
    public const string KindCard = "card";
    public const string KindDebt = "debt";
    public const string KindDiscounts = "discounts";
    public const string KindReturns = "returns";
    public const string KindWriteOffs = "writeoffs";
    public const string KindExpense = "expense";
    public const string KindDebtPaid = "debtpaid";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private sealed record Row(string Header, string TotalText, string Details)
    {
        public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    }

    private readonly ShiftModel? _shift;
    private readonly string _kind = KindSales;

    public ShiftDrillDownDialog() => InitializeComponent();

    public ShiftDrillDownDialog(ShiftModel shift, string kind) : this()
    {
        _shift = shift;
        _kind = kind;
        TitleText.Text = TitleFor(kind);
        var number = string.IsNullOrWhiteSpace(shift.ShiftNumber)
            ? "—"
            : shift.ShiftNumber[..Math.Min(8, shift.ShiftNumber.Length)].ToUpperInvariant();
        SubtitleText.Text = Tr.T($"Смена {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — ",
                $"Смена {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — ",
                $"Shift {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — ",
                $"Vardiya {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — ",
                $"Smena {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — ")
            + (shift.ClosedAt is { } closed ? closed.ToString("dd.MM.yyyy HH:mm") : Tr.T("сейчас", "азыр", "now", "şu an", "hozir"));
        Opened += async (_, _) => await LoadAsync();
    }

    private static string TitleFor(string kind) => kind switch
    {
        KindCash => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        KindCard => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
        KindDebt => Tr.T("Продажи в долг", "Карызга сатуулар", "Sales on credit", "Veresiye satışlar", "Qarzga sotuvlar"),
        KindDiscounts => Tr.T("Скидки", "Арзандатуулар", "Discounts", "İndirimler", "Chegirmalar"),
        KindReturns => Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"),
        KindWriteOffs => Tr.T("Списания", "Эсептен чыгаруулар", "Write-offs", "Düşümler", "Hisobdan chiqarishlar"),
        KindExpense => Tr.T("Расход", "Чыгаша", "Expense", "Gider", "Xarajat"),
        KindDebtPaid => Tr.T("Оплата долгов", "Карыздарды төлөө", "Debt payments", "Borç ödemeleri", "Qarz to'lovlari"),
        _ => Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
    };

    private static string Money(double value) => value.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private static string MethodText(string method) => method.ToLowerInvariant() switch
    {
        "cash" => Tr.T("наличные", "накталай", "cash", "nakit", "naqd"),
        "transfer" or "card" or "noncash" or "cashless" or "bank" => Tr.T("безналичные", "накталай эмес", "cashless", "nakitsiz", "naqdsiz"),
        "debt" => Tr.T("в долг", "карызга", "on credit", "veresiye", "qarzga"),
        "mixed" => Tr.T("смешанная", "аралаш", "mixed", "karışık", "aralash"),
        _ => string.IsNullOrWhiteSpace(method) ? "—" : method,
    };

    private async Task LoadAsync()
    {
        if (_shift is null)
            return;

        SummaryText.Text = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        LoadingBar.IsVisible = true;
        try
        {
            var rows = _kind is KindReturns or KindWriteOffs or KindExpense or KindDebtPaid
                ? LoadEvents()
                : await LoadSalesAsync();
            RowsList.ItemsSource = rows.Rows;
            SummaryText.Text = rows.Rows.Count == 0
                ? Tr.T("За эту смену записей нет.", "Бул сменада жазуулар жок.", "No records for this shift.", "Bu vardiyada kayıt yok.", "Bu smenada yozuvlar yo'q.")
                : rows.Summary;
            FooterText.Text = rows.Rows.Count == 0 ? "" : Tr.T("Итого: ", "Жыйынтык: ", "Total: ", "Toplam: ", "Jami: ") + Money(rows.Total);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены ({_kind}) не загружен: {ex}", "SHIFTS");
            SummaryText.Text = Tr.T("Не удалось загрузить отчёт: ", "Отчётту жүктөө мүмкүн болгон жок: ", "Could not load the report: ", "Rapor yüklenemedi: ", "Hisobotni yuklab bo'lmadi: ") + ex.Message;
        }
        finally
        {
            LoadingBar.IsVisible = false;
        }
    }

    private async Task<(List<Row> Rows, string Summary, double Total)> LoadSalesAsync()
    {
        var sales = await ShiftReportData.LoadSalesAsync(_shift!.Id, _shift.OpenedAt, _shift.ClosedAt);
        sales = sales.Where(s => !string.Equals(s.Status, "canceled", StringComparison.OrdinalIgnoreCase)).ToList();

        IEnumerable<ShiftReportData.ShiftSale> picked = _kind switch
        {
            KindCash => sales.Where(s => s.PaymentMethod is "cash" or "mixed" && s.Status != "debt"),
            KindCard => sales.Where(s => s.PaymentMethod is "transfer" or "card" or "noncash" or "cashless" or "bank" or "mixed"),
            KindDebt => sales.Where(s => s.Status == "debt" || s.PaymentMethod == "debt" || s.Debt > 0.005),
            KindDiscounts => sales.Where(s => s.Discount > 0.005),
            _ => sales,
        };
        var list = picked.ToList();

        var rows = list.Select(s =>
        {
            var receiptNo = s.Id[..Math.Min(8, s.Id.Length)].ToUpperInvariant();
            var header = Tr.T($"{s.CreatedAt.ToLocalTime():HH:mm} · чек {receiptNo} · {MethodText(s.PaymentMethod)}",
                $"{s.CreatedAt.ToLocalTime():HH:mm} · чек {receiptNo} · {MethodText(s.PaymentMethod)}",
                $"{s.CreatedAt.ToLocalTime():HH:mm} · receipt {receiptNo} · {MethodText(s.PaymentMethod)}",
                $"{s.CreatedAt.ToLocalTime():HH:mm} · fiş {receiptNo} · {MethodText(s.PaymentMethod)}",
                $"{s.CreatedAt.ToLocalTime():HH:mm} · chek {receiptNo} · {MethodText(s.PaymentMethod)}");
            if (s.Status == "debt")
                header += Tr.T(" · долг", " · карыз", " · debt", " · borç", " · qarz");
            if (s.Discount > 0.005)
                header += Tr.T(" · скидка ", " · арзандатуу ", " · discount ", " · indirim ", " · chegirma ") + Money(s.Discount);
            if (!string.IsNullOrWhiteSpace(s.Cashier))
                header += " · " + s.Cashier;

            var details = s.Lines.Count == 0
                ? Tr.T("товары чека не загрузились", "чектин товарлары жүктөлгөн жок", "receipt items did not load", "fiş ürünleri yüklenemedi", "chek mahsulotlari yuklanmadi")
                : string.Join("\n", s.Lines.Select(l =>
                    $"{l.Name} — {l.Quantity.ToString("0.###", Ru)} × {l.Price.ToString("N2", Ru)} = {(l.Quantity * l.Price).ToString("N2", Ru)}"));
            var amount = _kind == KindDiscounts ? s.Discount : _kind == KindDebt && s.Debt > 0.005 ? s.Debt : s.Total;
            return new Row(header, Money(amount), details);
        }).ToList();

        var total = _kind == KindDiscounts ? list.Sum(s => s.Discount)
            : _kind == KindDebt ? list.Sum(s => s.Debt > 0.005 ? s.Debt : s.Total)
            : list.Sum(s => s.Total);
        var units = list.Sum(s => s.Lines.Sum(l => l.Quantity));
        var unitsText = units.ToString("0.###", Ru);
        var totalText = Money(total);
        var summary = Tr.T($"Чеков: {list.Count} · товаров: {unitsText} ед. · на сумму {totalText}",
            $"Чектер: {list.Count} · товарлар: {unitsText} бирд. · суммасы {totalText}",
            $"Receipts: {list.Count} · quantity: {unitsText} · total {totalText}",
            $"Fiş: {list.Count} · ürün: {unitsText} birim · toplam {totalText}",
            $"Cheklar: {list.Count} · mahsulotlar: {unitsText} birlik · jami {totalText}");

        // Товары по итогам — что именно ушло за эту категорию.
        var byProduct = list.SelectMany(s => s.Lines)
            .GroupBy(l => l.Name)
            .Select(g =>
            {
                var qty = g.Sum(l => l.Quantity).ToString("0.###", Ru);
                var sum = g.Sum(l => l.Quantity * l.Price).ToString("N2", Ru);
                return Tr.T($"{g.Key} — {qty} ед. на {sum}",
                    $"{g.Key} — {qty} бирд., суммасы {sum}",
                    $"{g.Key} — {qty} units for {sum}",
                    $"{g.Key} — {qty} birim, toplam {sum}",
                    $"{g.Key} — {qty} birlik, jami {sum}");
            })
            .ToList();
        if (byProduct.Count > 0)
            rows.Insert(0, new Row(Tr.T("Товары за смену в этом разделе", "Бул бөлүмдөгү сменанын товарлары", "Products in this section for the shift", "Bu bölümdeki vardiya ürünleri", "Ushbu bo'limdagi smena mahsulotlari"), "", string.Join("\n", byProduct)));

        return (rows, summary, total);
    }

    private (List<Row> Rows, string Summary, double Total) LoadEvents()
    {
        var kind = _kind switch
        {
            KindReturns => ShiftEventsStore.KindReturn,
            KindWriteOffs => ShiftEventsStore.KindWriteOff,
            KindExpense => ShiftEventsStore.KindExpense,
            _ => ShiftEventsStore.KindDebtPayment,
        };

        var events = ShiftEventsStore.ListForShift(_shift!.Id, kind);
        var rows = events
            .Select(e => new Row(
                e.CreatedAt == DateTime.MinValue ? "—" : e.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                Money(e.Amount),
                e.Note ?? ""))
            .ToList();
        var total = events.Sum(e => e.Amount);
        var totalText = Money(total);
        return (rows, Tr.T($"Записей: {events.Count} · на сумму {totalText}",
            $"Жазуулар: {events.Count} · суммасы {totalText}",
            $"Records: {events.Count} · total {totalText}",
            $"Kayıt: {events.Count} · toplam {totalText}",
            $"Yozuvlar: {events.Count} · jami {totalText}"), total);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
