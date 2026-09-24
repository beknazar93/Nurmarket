using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Полноэкранная версия истории списаний (по запросу пользователя, 2026-09-04) —
/// узкая панель на вкладке "Списание товара" в WarehouseWindow осталась для беглого просмотра,
/// а эта отдельная модалка даёт больше места, поиск и сводку. Источник данных тот же
/// WriteOffHistoryStore (локальный журнал этой кассы — API актов списания не отдаёт историю).</summary>
public partial class WriteOffHistoryWindow : Window
{
    private List<HistoryRow> _allRows = new();

    public WriteOffHistoryWindow()
    {
        InitializeComponent();
    }

    public static void Open(Window? owner)
    {
        var window = new WriteOffHistoryWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e) => LoadHistory();

    private void LoadHistory()
    {
        _allRows = WriteOffHistoryStore.LoadReport(5000)
            .Select(row =>
            {
                // У записей до 2026-09-24 себестоимости нет — берём текущую цену закупки товара.
                var cost = row.UnitCost
                    ?? LocalProductRepository.Instance.TryGetTileById(row.ProductId)?.PurchasePrice
                    ?? 0;
                return new HistoryRow
            {
                ProductName = row.ProductName,
                Quantity = row.Quantity,
                QuantityText = row.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                UnitCost = cost,
                UnitCostText = cost > 0 ? cost.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) : "—",
                Total = cost * row.Quantity,
                TotalText = cost > 0 ? (cost * row.Quantity).ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) : "—",
                Reason = row.Reason,
                CashierName = string.IsNullOrWhiteSpace(row.CashierName) ? "—" : row.CashierName,
                DateText = row.CreatedAt == DateTime.MinValue
                    ? ""
                    : row.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            };
            })
            .ToList();

        ApplyFilter(SearchBox.Text ?? "");
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text ?? "");

    private void ApplyFilter(string query)
    {
        query = query.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r =>
                r.ProductName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Reason.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.CashierName.Contains(query, StringComparison.OrdinalIgnoreCase))
              .ToList();

        HistoryGrid.ItemsSource = filtered;
        EmptyText.IsVisible = filtered.Count == 0;

        var totalQty = filtered.Sum(r => r.Quantity);
        SummaryText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Tr.T("Записей: {0} · Списано единиц: {1}", "Жазуулар: {0} · Эсептен чыгарылган: {1}", "Entries: {0} · Units written off: {1}", "Kayıt: {0} · Silinen birim: {1}", "Yozuvlar: {0} · Hisobdan chiqarilgan: {1}"),
            filtered.Count,
            totalQty.ToString("0.###", CultureInfo.InvariantCulture));

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var totalCost = filtered.Sum(r => r.Total);
        TotalText.Text = Tr.T("Итого списано", "Бардыгы эсептен чыгарылды", "Total written off", "Toplam silinen", "Jami hisobdan chiqarildi")
            + $": {totalQty.ToString("0.###", CultureInfo.InvariantCulture)} "
            + Tr.T("ед.", "бирд.", "units", "birim", "birl.")
            + " · " + totalCost.ToString("N2", ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");
        ReasonsText.Text = string.Join("   ·   ", filtered
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason)
            .OrderByDescending(g => g.Sum(r => r.Total))
            .Select(g => $"{g.Key}: {g.Sum(r => r.Total).ToString("N2", ru)}"));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private sealed class HistoryRow
    {
        public string ProductName { get; set; } = "";
        public double Quantity { get; set; }
        public string QuantityText { get; set; } = "";
        public double UnitCost { get; set; }
        public string UnitCostText { get; set; } = "";
        public double Total { get; set; }
        public string TotalText { get; set; } = "";
        public string Reason { get; set; } = "";
        public string CashierName { get; set; } = "";
        public string DateText { get; set; } = "";
    }
}
