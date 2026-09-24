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
        SubtitleText.Text = $"Смена {number} · {shift.OpenedAt:dd.MM.yyyy HH:mm} — "
            + (shift.ClosedAt is { } closed ? closed.ToString("dd.MM.yyyy HH:mm") : "сейчас");
        Opened += async (_, _) => await LoadAsync();
    }

    private static string TitleFor(string kind) => kind switch
    {
        KindCash => "Наличные",
        KindCard => "Безналичные",
        KindDebt => "Продажи в долг",
        KindDiscounts => "Скидки",
        KindReturns => "Возвраты",
        KindWriteOffs => "Списания",
        KindExpense => "Расход",
        KindDebtPaid => "Оплата долгов",
        _ => "Продажи",
    };

    private static string Money(double value) => value.ToString("N2", Ru) + " сом";

    private static string MethodText(string method) => method.ToLowerInvariant() switch
    {
        "cash" => "наличные",
        "transfer" or "card" or "noncash" or "cashless" or "bank" => "безналичные",
        "debt" => "в долг",
        "mixed" => "смешанная",
        _ => string.IsNullOrWhiteSpace(method) ? "—" : method,
    };

    private async Task LoadAsync()
    {
        if (_shift is null)
            return;

        SummaryText.Text = "Загрузка…";
        LoadingBar.IsVisible = true;
        try
        {
            var rows = _kind is KindReturns or KindWriteOffs or KindExpense or KindDebtPaid
                ? LoadEvents()
                : await LoadSalesAsync();
            RowsList.ItemsSource = rows.Rows;
            SummaryText.Text = rows.Rows.Count == 0 ? "За эту смену записей нет." : rows.Summary;
            FooterText.Text = rows.Rows.Count == 0 ? "" : "Итого: " + Money(rows.Total);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены ({_kind}) не загружен: {ex}", "SHIFTS");
            SummaryText.Text = "Не удалось загрузить отчёт: " + ex.Message;
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
            var header = $"{s.CreatedAt.ToLocalTime():HH:mm} · чек {s.Id[..Math.Min(8, s.Id.Length)].ToUpperInvariant()} · {MethodText(s.PaymentMethod)}";
            if (s.Status == "debt")
                header += " · долг";
            if (s.Discount > 0.005)
                header += " · скидка " + Money(s.Discount);
            if (!string.IsNullOrWhiteSpace(s.Cashier))
                header += " · " + s.Cashier;

            var details = s.Lines.Count == 0
                ? "товары чека не загрузились"
                : string.Join("\n", s.Lines.Select(l =>
                    $"{l.Name} — {l.Quantity.ToString("0.###", Ru)} × {l.Price.ToString("N2", Ru)} = {(l.Quantity * l.Price).ToString("N2", Ru)}"));
            var amount = _kind == KindDiscounts ? s.Discount : _kind == KindDebt && s.Debt > 0.005 ? s.Debt : s.Total;
            return new Row(header, Money(amount), details);
        }).ToList();

        var total = _kind == KindDiscounts ? list.Sum(s => s.Discount)
            : _kind == KindDebt ? list.Sum(s => s.Debt > 0.005 ? s.Debt : s.Total)
            : list.Sum(s => s.Total);
        var units = list.Sum(s => s.Lines.Sum(l => l.Quantity));
        var summary = $"Чеков: {list.Count} · товаров: {units.ToString("0.###", Ru)} ед. · на сумму {Money(total)}";

        // Товары по итогам — что именно ушло за эту категорию.
        var byProduct = list.SelectMany(s => s.Lines)
            .GroupBy(l => l.Name)
            .Select(g => $"{g.Key} — {g.Sum(l => l.Quantity).ToString("0.###", Ru)} ед. на {g.Sum(l => l.Quantity * l.Price).ToString("N2", Ru)}")
            .ToList();
        if (byProduct.Count > 0)
            rows.Insert(0, new Row("Товары за смену в этом разделе", "", string.Join("\n", byProduct)));

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
        return (rows, $"Записей: {events.Count} · на сумму {Money(total)}", total);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
