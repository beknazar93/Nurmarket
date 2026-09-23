using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>История покупок одного клиента — отдельным окном.
///
/// В карточке клиента покупки показывались списком «дата — сумма», без номеров чеков. По такой
/// строке невозможно найти сам чек: ни сверить состав, ни распечатать повторно, ни сослаться
/// на него при разборе долга. Владелец попросил номера и отдельное окно — здесь и то и другое,
/// плюс сам чек по двойному щелчку.
///
/// Данные берутся тем же запросом, что и в карточке (pos/sales по клиенту), состав чека —
/// отдельным запросом уже по выбранной строке: тянуть содержимое всех чеков сразу незачем.</summary>
public sealed class ClientPurchasesWindow : Window
{
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
    };

    private readonly TextBlock _summary = new()
    {
        FontSize = 12,
        Margin = new Avalonia.Thickness(0, 0, 0, 8),
        Foreground = Brushes.Gray,
    };

    private readonly TextBox _receipt = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        FontSize = 12,
        MinWidth = 320,
        TextWrapping = TextWrapping.NoWrap,
    };

    private readonly string _clientId;
    private CancellationTokenSource? _cts;

    public ClientPurchasesWindow(string clientId, string clientName)
    {
        _clientId = clientId ?? "";

        Title = Tr.T("История покупок", "Сатып алуулар тарыхы", "Purchase history",
                     "Satın alma geçmişi", "Xaridlar tarixi");
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("BrushWindow") as IBrush ?? Brushes.White;

        BuildColumns();
        _grid.SelectionChanged += (_, _) => _ = ShowReceiptAsync();

        var left = new StackPanel { Spacing = 0 };
        left.Children.Add(_summary);
        left.Children.Add(_grid);

        var right = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(12, 0, 0, 0) };
        right.Children.Add(new TextBlock
        {
            Text = Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = this.FindResource("BrushText") as IBrush ?? Brushes.Black,
        });
        right.Children.Add(new ScrollViewer { Content = _receipt, Height = 470 });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var root = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = clientName,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("BrushText") as IBrush ?? Brushes.Black,
        });
        root.Children.Add(body);

        Content = root;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void BuildColumns()
    {
        void Add(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                Binding = new Avalonia.Data.Binding(path),
            });
        }

        Add(Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), nameof(PurchaseVm.DateText), 150);
        Add(Tr.T("№ чека", "Чек №", "Receipt no.", "Fiş no", "Chek №"), nameof(PurchaseVm.ReceiptText), 130);
        Add(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), nameof(PurchaseVm.PaymentText), 120);
        Add(Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), nameof(PurchaseVm.AmountText), 130);
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _cts = cts;

        _summary.Text = Tr.T("Загружаю покупки…", "Сатып алуулар жүктөлүүдө…", "Loading purchases…",
                             "Satın almalar yükleniyor…", "Xaridlar yuklanmoqda…");

        try
        {
            var sales = await App.SalesApi
                .PosSalesByClientAsync(_clientId, maxPages: 5, cts.Token)
                .ConfigureAwait(true);

            var rows = new List<PurchaseVm>();
            decimal total = 0;

            foreach (var sale in sales)
            {
                var amount = ReadAmount(sale);
                total += amount;
                rows.Add(new PurchaseVm
                {
                    SaleId = ReadString(sale, "id") ?? "",
                    DateText = ReadDate(sale),
                    ReceiptText = ReadReceiptNumber(sale) ?? ShortId(ReadString(sale, "id")),
                    PaymentText = DescribePayment(ReadString(sale, "payment_method")),
                    AmountText = amount.ToString("N2", CultureInfo.CurrentCulture) + " "
                                 + Tr.T("сом", "сом", "KGS", "KGS", "KGS"),
                });
            }

            _grid.ItemsSource = rows;
            _summary.Text = rows.Count == 0
                ? Tr.T("Покупок пока нет.", "Азырынча сатып алуу жок.", "No purchases yet.",
                       "Henüz satın alma yok.", "Hozircha xarid yo'q.")
                : Tr.T("Покупок", "Сатып алуулар", "Purchases", "Satın almalar", "Xaridlar")
                  + $": {rows.Count} · " + Tr.T("на сумму", "суммасы", "for", "tutarı", "summasi")
                  + $" {total:N2} " + Tr.T("сом", "сом", "KGS", "KGS", "KGS");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _summary.Text = Tr.T("Не удалось загрузить покупки", "Сатып алууларды жүктөө мүмкүн болгон жок",
                "Could not load the purchases", "Satın almalar yüklenemedi",
                "Xaridlarni yuklab bo'lmadi") + ": " + ex.Message;
        }
    }

    /// <summary>Состав выбранного чека. Тем же построителем, что и предпросмотр в «Продажах», —
    /// чтобы бумага и экран совпадали.</summary>
    private async Task ShowReceiptAsync()
    {
        if (_grid.SelectedItem is not PurchaseVm row || string.IsNullOrWhiteSpace(row.SaleId))
        {
            _receipt.Text = "";
            return;
        }

        try
        {
            var sale = await App.SalesApi.PosSaleGetAsync(row.SaleId).ConfigureAwait(true);
            _receipt.Text = SaleReceiptTextBuilder.Build(
                sale,
                row.ReceiptText == "—" ? null : row.ReceiptText,
                discount: 0m,
                total: null);
        }
        catch (Exception ex)
        {
            _receipt.Text = Tr.T("Чек не загрузился", "Чек жүктөлгөн жок", "The receipt did not load",
                "Fiş yüklenmedi", "Chek yuklanmadi") + ": " + ex.Message;
        }
    }

    private static decimal ReadAmount(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("total", out var value))
            return 0m;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out var n) ? n : 0m,
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

    private static string ReadDate(JsonElement sale)
    {
        var raw = ReadString(sale, "created_at");
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : "—";
    }

    /// <summary>Читаемый номер чека. Идентификатор продажи (GUID) сюда не годится — по той же
    /// причине, по которой он не печатается на чеке.</summary>
    private static string? ReadReceiptNumber(JsonElement sale)
    {
        foreach (var key in new[] { "receipt_number", "receipt_no", "check_number", "check_no", "sale_number", "number" })
        {
            var text = ReadString(sale, key);
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    /// <summary>Короткий номер чека из идентификатора продажи.
    ///
    /// Читаемого номера сервер по продажам не отдаёт вовсе (проверено запросом к API): в
    /// ответе есть только id-GUID. Окно «Продажи» подставляет порядковые номера, но они свои у
    /// каждой страницы списка и к конкретному чеку не привязаны — по такому номеру чек не
    /// найти. Первые восемь символов идентификатора, наоборот, у чека всегда одни и те же:
    /// их можно записать в тетрадь, назвать по телефону и найти этот же чек здесь.</summary>
    private static string ShortId(string? id)
    {
        var text = (id ?? "").Replace("-", "");
        return text.Length >= 8 ? text[..8].ToUpperInvariant() : "—";
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string DescribePayment(string? method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        "transfer" or "card" or "noncash" => Tr.T("Безнал", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        "mixed" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karma", "Aralash"),
        "" => "—",
        _ => method!,
    };

    private sealed class PurchaseVm
    {
        public string SaleId { get; init; } = "";
        public string DateText { get; init; } = "";
        public string ReceiptText { get; init; } = "";
        public string PaymentText { get; init; } = "";
        public string AmountText { get; init; } = "";
    }
}
