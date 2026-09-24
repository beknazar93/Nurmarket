using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Некорректные чеки» (этап 2 бэклога «Доработки и добавление функционала») — объединяет
/// постоянный журнал <see cref="IrregularReceiptsStore"/> (продажи при нулевом остатке) с
/// живыми записями офлайн-очереди <see cref="OfflinePendingSalesStore"/>, которые ещё не
/// синхронизированы (в процессе / ошибка) — вторые не дублируются в отдельное хранилище,
/// так как OfflineSales уже сама по себе постоянная таблица для незавершённых записей
/// (удаляется только при успешной синхронизации).
/// </summary>
public partial class IrregularReceiptsWindow : Window
{
    public IrregularReceiptsWindow()
    {
        InitializeComponent();
        // Esc: сначала всплывающий чек, потом само окно.
        EscapeKey.Attach(this, () =>
        {
            if (!ReceiptDetailsPopup.IsOpen)
                return false;
            ReceiptDetailsPopup.IsOpen = false;
            return true;
        });
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e) => LoadRows();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => LoadRows();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Возвращает отклонённые сервером чеки в очередь на выгрузку.
    ///
    /// До 2026-09-23 отсюда не было вообще никакого выхода: чек, который сервер отверг один раз
    /// (например, товар успели удалить на сайте), навсегда оставался в статусе failed — деньги
    /// в кассе, продажи в NurCRM нет. Повтор сделан кнопкой, а не автоматикой: сервер уже сказал
    /// «нет», и молча повторять это в цикле — прямой путь к дублям.</summary>
    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var count = OfflinePendingSalesStore.RequeueFailed();
            ShowMessage(count > 0
                ? $"Возвращено в очередь: {count}. Касса отправит их при ближайшей синхронизации."
                : "Нечего отправлять повторно: отклонённых чеков нет.");
            LoadRows();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Повторная отправка чеков не удалась: {ex}", "WARNING");
            ShowMessage("Не удалось вернуть чеки в очередь: " + ex.Message);
        }
    }

    private void ShowMessage(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    private void LoadRows()
    {
        var rows = new List<RowVm>();

        foreach (var entry in IrregularReceiptsStore.LoadAll())
        {
            rows.Add(new RowVm
            {
                CreatedAt = entry.CreatedAt,
                IsInsufficientStock = true,
                StatusText = Tr.T("Не хватило остатка", "Калдык жетишсиз болду", "Not enough stock", "Stok yetersiz", "Qoldiq yetarli emas"),
                Note = entry.Note ?? "",
                TotalText = FormatMoney(entry.Total),
            });
        }

        foreach (var sale in OfflinePendingSalesStore.LoadAll())
        {
            if (string.Equals(sale.Status, OfflineSaleEntry.Synced, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var isFailed = string.Equals(sale.Status, OfflineSaleEntry.Failed, System.StringComparison.OrdinalIgnoreCase);
            rows.Add(new RowVm
            {
                CreatedAt = sale.CreatedAt,
                IsPending = !isFailed,
                IsError = isFailed,
                StatusText = isFailed
                    ? Tr.T("Ошибка отправки на сервер", "Серверге жиберүү катасы", "Error sending to the server", "Sunucuya gönderme hatası", "Serverga yuborishda xato")
                    : Tr.T("В процессе (ожидает выгрузку)", "Жүрүп жатат (жүктөлүүнү күтүүдө)", "In progress (awaiting upload)", "İşleniyor (yüklenmeyi bekliyor)", "Jarayonda (yuklashni kutmoqda)"),
                Note = sale.LastError ?? "",
                TotalText = TryComputeTotalText(sale.CartJson),
                Id = sale.Id,
                CartJson = sale.CartJson,
            });
        }

        ReceiptsGrid.ItemsSource = rows
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        EmptyText.IsVisible = rows.Count == 0;
    }

    private static string FormatMoney(double value) =>
        value.ToString("0.## сом", CultureInfo.InvariantCulture);

    /// <summary>Сумма чека для строки грида — считаем из уже сохранённого локального CartJson
    /// (тот же разбор, что и в SalesWindow.ShowReceiptDetailsByIdAsync), сеть не нужна.</summary>
    private static string TryComputeTotalText(string cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            decimal total = 0;
            foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(doc.RootElement))
            {
                var totalStr = CartDisplayHelper.LineTotal(line);
                if (decimal.TryParse(totalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal))
                    total += lineTotal;
            }
            return total.ToString("0.## сом", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "" : (id.Length >= 8 ? id[..8].ToUpperInvariant() : id.ToUpperInvariant());

    /// <summary>«Посмотреть» для локального (ещё не синхронизированного) чека — по определению
    /// экрана "Некорректные чеки" здесь ВСЕГДА локальные записи (см. класс doc-comment), поэтому
    /// в отличие от SalesWindow.ShowReceiptDetailsByIdAsync тут нет ветки похода на сервер.</summary>
    private void View_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RowVm row } || !row.CanView)
            return;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.CartJson) ? "{}" : row.CartJson);
            var json = doc.RootElement.Clone();

            var items = new List<string>();
            decimal previewTotal = 0;
            foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(json))
            {
                var name = CartDisplayHelper.ItemName(line);
                var qty = CartDisplayHelper.LineQuantity(line);
                var unitPrice = CartDisplayHelper.UnitPrice(line);
                var lineTotalStr = CartDisplayHelper.LineTotal(line);
                if (decimal.TryParse(lineTotalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal))
                    previewTotal += lineTotal;
                items.Add($"• {name} — {qty:0.###} × {unitPrice:N2} = {lineTotalStr}");
            }
            items.Add(new string('-', 24));
            items.Add($"ИТОГО: {previewTotal:N2} сом");

            _currentReceiptJson = json;
            _currentReceiptTitle = Tr.T("Чек (офлайн)", "Чек (офлайн)", "Receipt (offline)", "Fiş (çevrimdışı)", "Chek (oflayn)")
                + " " + ShortId(row.Id);
            PopupTitle.Text = _currentReceiptTitle;
            PopupItemsControl.ItemsSource = items;
            ReceiptDetailsPopup.IsOpen = true;
            ErrorText.IsVisible = false;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось загрузить детали чека: " + ex.Message;
            ErrorText.IsVisible = true;
        }
    }

    private async void PrintReceiptAgain_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var lines = new List<string> { _currentReceiptTitle, new string('-', 24) };

            decimal total = 0;
            foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(_currentReceiptJson))
            {
                var name = CartDisplayHelper.ItemName(line);
                var qty = (decimal)CartDisplayHelper.LineQuantity(line);
                var price = (decimal)CartDisplayHelper.UnitPrice(line);
                var lineTotalStr = CartDisplayHelper.LineTotal(line);
                if (!decimal.TryParse(lineTotalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal))
                    lineTotal = qty * price;
                total += lineTotal;
                lines.Add(name);
                lines.Add($"  {qty} x {price:N2} = {lineTotal:N2}");
            }

            lines.Add(new string('-', 24));
            lines.Add($"ИТОГО: {total:N2} сом");
            lines.Add("(повторная печать)");

            // В фоне: медленный принтер не подвешивает окно.
            var text = string.Join("\n", lines);
            await System.Threading.Tasks.Task.Run(() => ReceiptPrintService.PrintReceipt("{}", receiptText: text)).ConfigureAwait(true);
            ErrorText.IsVisible = false;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось напечатать чек: " + ex.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void CloseReceiptDetails_Click(object? sender, RoutedEventArgs e) => ReceiptDetailsPopup.IsOpen = false;

    private JsonElement _currentReceiptJson;
    private string _currentReceiptTitle = "";

    private sealed class RowVm
    {
        public System.DateTimeOffset CreatedAt { get; set; }
        public string DateText => CreatedAt.LocalDateTime.ToString("dd.MM", CultureInfo.InvariantCulture);
        public string TimeText => CreatedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        public bool IsPending { get; set; }
        public bool IsError { get; set; }
        public bool IsInsufficientStock { get; set; }
        public string StatusText { get; set; } = "";
        public string Note { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string? Id { get; set; }
        public string? CartJson { get; set; }
        public bool CanView => !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(CartJson) && CartJson != "{}";
    }
}
