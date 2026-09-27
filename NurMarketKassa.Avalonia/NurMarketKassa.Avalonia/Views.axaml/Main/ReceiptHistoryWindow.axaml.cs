using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>«История чеков» кассы (2026-09-27, просьба владельца: «как кассир может повторный
/// чек пробить вдруг!!! это добавь и ещё история чеков»).
///
/// Слева — чеки этой кассы за смену, сегодня или вчера (с сервера и из офлайн-очереди), справа —
/// состав выбранного чека, печать копии с отметкой «(повторная печать)» и переход к возврату.
/// Данные и печать — <see cref="ReceiptHistoryService"/>.</summary>
public partial class ReceiptHistoryWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private ReceiptHistoryPeriod _period = ReceiptHistoryPeriod.CurrentShift;
    private List<RowVm> _rows = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _detailCts;
    private RowVm? _selected;
    private JsonElement? _selectedDetail;

    /// <summary>id чека, состав которого сейчас показан: поиск не перечитывает его с сервера
    /// на каждую букву.</summary>
    private string? _shownDetailId;

    private bool _printing;
    private readonly bool _canReturn;

    /// <summary>Кассир открывал возврат — главное окно после закрытия пересчитает остаток смены.</summary>
    public bool ReturnWasOpened { get; private set; }

    public ReceiptHistoryWindow()
    {
        InitializeComponent();
        _canReturn = CanReturn();
        Opened += OnFirstOpened;
    }

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        await LoadAsync().ConfigureAwait(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _detailCts?.Cancel();
        base.OnClosed(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _shownDetailId = null;
        await LoadAsync(_selected?.Entry.Id).ConfigureAwait(true);
    }

    private async void Period_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
            return;

        var period = tag switch
        {
            "today" => ReceiptHistoryPeriod.Today,
            "yesterday" => ReceiptHistoryPeriod.Yesterday,
            _ => ReceiptHistoryPeriod.CurrentShift,
        };
        if (period == _period && _rows.Count > 0)
            return;

        _period = period;
        _shownDetailId = null;
        await LoadAsync().ConfigureAwait(true);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    // ── Список ──

    private async Task LoadAsync(string? keepSelectedId = null)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        LoadingBar.IsVisible = true;
        try
        {
            var result = await ReceiptHistoryService.LoadAsync(_period, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            _rows = result.Entries.Select(entry => new RowVm(entry)).ToList();
            ShowNotice(result);
            ApplyFilter(keepSelectedId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Выбран другой период или окно закрыто — эта загрузка уже не нужна.
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков не загружена: {ex}", "WARNING");
            NoticeText.Text = Tr.T("Не удалось загрузить чеки: ", "Чектерди жүктөө мүмкүн болгон жок: ", "Could not load the receipts: ", "Fişler yüklenemedi: ", "Cheklarni yuklab bo'lmadi: ") + ex.Message;
            NoticeBorder.IsVisible = true;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                LoadingBar.IsVisible = false;
            }

            cts.Dispose();
        }
    }

    private void ShowNotice(ReceiptHistoryResult result)
    {
        var parts = new List<string>();
        if (result.NoOpenShift)
            parts.Add(Tr.T("Смена не открыта — показаны чеки этой кассы за сегодня.",
                "Смена ачылган эмес — ушул кассанын бүгүнкү чектери көрсөтүлдү.",
                "The shift is not open — showing this till's receipts for today.",
                "Vardiya açık değil — bu kasanın bugünkü fişleri gösteriliyor.",
                "Smena ochilmagan — shu kassaning bugungi cheklari ko'rsatildi."));
        if (result.ServerUnavailable)
            parts.Add(Tr.T("Нет связи с сервером — история чеков с сервера недоступна. Показаны только чеки, сохранённые на этой кассе и ещё не отправленные.",
                "Сервер менен байланыш жок — сервердеги чектердин тарыхы жеткиликсиз. Ушул кассада сакталган жана али жөнөтүлө элек чектер гана көрсөтүлдү.",
                "No connection to the server — the server receipt history is unavailable. Only receipts saved on this till and not yet sent are shown.",
                "Sunucuyla bağlantı yok — sunucudaki fiş geçmişi kullanılamıyor. Yalnızca bu kasada kayıtlı ve henüz gönderilmemiş fişler gösteriliyor.",
                "Server bilan aloqa yo'q — serverdagi cheklar tarixi mavjud emas. Faqat shu kassada saqlangan va hali yuborilmagan cheklar ko'rsatildi."));

        NoticeText.Text = string.Join("\n", parts);
        NoticeBorder.IsVisible = parts.Count > 0;
    }

    private void ApplyFilter(string? keepSelectedId = null)
    {
        var query = (SearchBox.Text ?? "").Trim().TrimStart('№', '#').Trim();
        var visible = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var selectId = keepSelectedId ?? _selected?.Entry.Id;
        ReceiptsList.ItemsSource = visible;
        EmptyText.IsVisible = _rows.Count == 0;
        NoMatchText.IsVisible = _rows.Count > 0 && visible.Count == 0;

        var again = selectId == null
            ? null
            : visible.FirstOrDefault(r => string.Equals(r.Entry.Id, selectId, StringComparison.OrdinalIgnoreCase));
        if (again != null)
            ReceiptsList.SelectedItem = again;
        else
            ClearDetails();
    }

    private async void ReceiptsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReceiptsList.SelectedItem is not RowVm row)
            return;

        // Тот же чек (поиск перестроил список) — состав уже на экране.
        if (_selectedDetail != null && string.Equals(_shownDetailId, row.Entry.Id, StringComparison.OrdinalIgnoreCase))
        {
            _selected = row;
            return;
        }

        await ShowDetailsAsync(row).ConfigureAwait(true);
    }

    // ── Выбранный чек ──

    private void ClearDetails()
    {
        _detailCts?.Cancel();
        _selected = null;
        _selectedDetail = null;
        _shownDetailId = null;
        DetailsPanel.IsVisible = false;
        SelectHintText.IsVisible = true;
    }

    private async Task ShowDetailsAsync(RowVm row)
    {
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        _selected = row;
        _selectedDetail = null;
        _shownDetailId = null;

        SelectHintText.IsVisible = false;
        DetailsPanel.IsVisible = true;
        FillHeader(row);
        DetailLines.ItemsSource = null;
        SummaryRows.ItemsSource = null;
        DetailErrorText.IsVisible = false;
        ActionMessage.IsVisible = false;
        PrintCopyButton.IsEnabled = false;
        DetailLoadingBar.IsVisible = true;

        try
        {
            var detail = await ReceiptHistoryService.LoadDetailAsync(row.Entry, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(_selected, row))
                return;

            _selectedDetail = detail;
            _shownDetailId = row.Entry.Id;
            FillDetails(row, detail);
            PrintCopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Выбран другой чек.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_selected, row))
                return;
            PosLogger.Log($"История чеков: состав чека {row.Entry.Id} не загружен: {ex.Message}", "WARNING");
            DetailErrorText.Text = Tr.T("Не удалось загрузить состав чека: ", "Чектин курамын жүктөө мүмкүн болгон жок: ", "Could not load the receipt items: ", "Fiş içeriği yüklenemedi: ", "Chek tarkibini yuklab bo'lmadi: ") + ex.Message;
            DetailErrorText.IsVisible = true;
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                _detailCts = null;
                DetailLoadingBar.IsVisible = false;
            }

            cts.Dispose();
        }
    }

    private void FillHeader(RowVm row)
    {
        var entry = row.Entry;
        DetailTitle.Text = Tr.T("Чек ", "Чек ", "Receipt ", "Fiş ", "Chek ") + entry.ReceiptNumber;
        DetailStatusText.Text = row.StatusText;
        DetailBadge.Classes.Set("warn", row.IsWarn);
        DetailBadge.Classes.Set("danger", row.IsDanger);
        DetailBadge.Classes.Set("info", row.IsInfo);

        var meta = entry.CreatedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(entry.Cashier))
            meta += "  ·  " + Tr.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + entry.Cashier;
        DetailMeta.Text = meta;

        if (entry.IsFailedUpload)
        {
            var error = string.IsNullOrWhiteSpace(entry.Local!.LastError) ? "—" : entry.Local.LastError!.Trim();
            DetailNoticeText.Text = Tr.T(
                $"Сервер отклонил этот чек: {error}. Отправить его повторно можно в разделе «Некорректные чеки».",
                $"Сервер бул чекти четке какты: {error}. Аны «Туура эмес чектер» бөлүмүнөн кайра жөнөтсө болот.",
                $"The server rejected this receipt: {error}. You can resend it in “Irregular receipts”.",
                $"Sunucu bu fişi reddetti: {error}. «Sorunlu fişler» bölümünden yeniden gönderebilirsiniz.",
                $"Server bu chekni rad etdi: {error}. Uni «Noto'g'ri cheklar» bo'limidan qayta yuborish mumkin.");
            DetailNotice.IsVisible = true;
        }
        else if (entry.IsPendingUpload)
        {
            DetailNoticeText.Text = Tr.T(
                "Чек ещё не отправлен на сервер — он сохранён на этой кассе и уйдёт, когда появится связь. Копию можно напечатать уже сейчас.",
                "Чек серверге али жөнөтүлө элек — ал ушул кассада сакталды жана байланыш пайда болгондо жөнөтүлөт. Көчүрмөсүн азыр эле басып чыгарса болот.",
                "This receipt has not been sent to the server yet — it is saved on this till and will be sent when the connection is back. You can print a copy right now.",
                "Bu fiş henüz sunucuya gönderilmedi — bu kasada kayıtlı ve bağlantı geldiğinde gönderilecek. Kopyasını şimdi yazdırabilirsiniz.",
                "Bu chek hali serverga yuborilmagan — u shu kassada saqlangan va aloqa paydo bo'lganda yuboriladi. Nusxasini hozir chop etish mumkin.");
            DetailNotice.IsVisible = true;
        }
        else
        {
            DetailNotice.IsVisible = false;
        }

        // Возврат — только по чеку, который уже есть на сервере, и тем, кому он разрешён.
        var canReturn = _canReturn && !entry.IsLocal && !ReceiptHistoryService.IsReturnedOrCanceled(entry);
        ReturnButton.IsVisible = canReturn;
        Grid.SetColumnSpan(PrintCopyButton, canReturn ? 1 : 3);
    }

    private void FillDetails(RowVm row, JsonElement detail)
    {
        var entry = row.Entry;
        var lines = new List<LineVm>();
        var names = new List<string>();
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(detail))
        {
            var name = CartDisplayHelper.ItemName(line);
            names.Add(name);

            var qty = CartDisplayHelper.LineQuantity(line);
            var price = (decimal)CartDisplayHelper.UnitPrice(line);
            var lineTotal = decimal.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (decimal)qty * price;
            var discount = LineDiscount(line);
            var returned = entry.IsLocal ? 0 : qty - CartDisplayHelper.RefundableQuantity(line);

            lines.Add(new LineVm
            {
                Name = name,
                QtyPriceText = $"{qty.ToString("0.###", Ru)} × {ReceiptHistoryService.Money(price)}",
                TotalText = ReceiptHistoryService.Money(lineTotal),
                DiscountText = discount > 0
                    ? Tr.T("Скидка: −", "Арзандатуу: −", "Discount: −", "İndirim: −", "Chegirma: −") + ReceiptHistoryService.Money(discount)
                    : "",
                ReturnedText = returned > 1e-6
                    ? Tr.T("Возвращено: ", "Кайтарылды: ", "Returned: ", "İade edildi: ", "Qaytarildi: ") + returned.ToString("0.###", Ru)
                    : "",
            });
        }

        DetailLines.ItemsSource = lines;
        if (lines.Count == 0)
        {
            DetailErrorText.Text = Tr.T("В чеке нет позиций.", "Чекте позиция жок.", "The receipt has no items.", "Fişte kalem yok.", "Chekda pozitsiyalar yo'q.");
            DetailErrorText.IsVisible = true;
        }

        // Поиск находит чек и по товарам, которые стали известны только сейчас.
        if (names.Count > 0)
            entry.SearchText += " " + string.Join(" ", names);

        SummaryRows.ItemsSource = BuildSummary(entry, detail);
    }

    private static List<SummaryVm> BuildSummary(ReceiptHistoryEntry entry, JsonElement detail)
    {
        var rows = new List<SummaryVm>();
        var totals = CartTotalsCalculator.Calculate(detail);
        var total = entry.IsLocal
            ? entry.Total
            : SalesWindow.ReadSaleTotal(detail) ?? entry.Total;
        var lineDiscounts = (decimal)totals.LineDiscounts;
        var orderDiscount = (decimal)totals.OrderDiscount;

        // «Сумма без скидок» — только когда она сходится с итогом: три числа, которые
        // противоречат друг другу, хуже одного честного (см. SaleReceiptTextBuilder).
        if (lineDiscounts + orderDiscount > 0.005m
            && Math.Abs((decimal)totals.Subtotal - lineDiscounts - orderDiscount - total) < 0.01m)
            rows.Add(new SummaryVm(Tr.T("Сумма без скидок", "Арзандатуусуз сумма", "Subtotal", "Ara toplam", "Chegirmasiz summa"),
                ReceiptHistoryService.Money((decimal)totals.Subtotal)));
        if (lineDiscounts > 0.005m)
            rows.Add(new SummaryVm(Tr.T("Скидка на позиции", "Позицияларга арзандатуу", "Item discounts", "Ürün indirimleri", "Pozitsiyalarga chegirma"),
                "−" + ReceiptHistoryService.Money(lineDiscounts)));
        if (orderDiscount > 0.005m)
            rows.Add(new SummaryVm(Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chekka chegirma"),
                "−" + ReceiptHistoryService.Money(orderDiscount)));

        rows.Add(new SummaryVm(Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami"), ReceiptHistoryService.Money(total), isTotal: true));

        var method = (entry.IsLocal ? entry.PaymentMethod : Str(detail, "payment_method") ?? entry.PaymentMethod).Trim().ToLowerInvariant();
        rows.Add(new SummaryVm(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), ReceiptHistoryService.PaymentLabel(method)));

        var received = entry.IsLocal ? ParseMoney(entry.Local!.CashReceived) : Money(detail, "cash_received");
        if (received is > 0 && method is "cash" or "mixed" or "debt")
        {
            rows.Add(new SummaryVm(Tr.T("Получено наличными", "Накталай алынды", "Cash received", "Alınan nakit", "Qabul qilingan naqd pul"),
                ReceiptHistoryService.Money(received.Value)));
            if (method == "cash")
            {
                var change = (entry.IsLocal ? null : Money(detail, "change")) ?? Math.Max(0, received.Value - total);
                rows.Add(new SummaryVm(Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), ReceiptHistoryService.Money(change)));
            }
        }

        if (!entry.IsLocal && method == "debt"
            && (Money(detail, "remaining_debt") ?? Money(detail, "debt_amount")) is { } debt && debt > 0)
            rows.Add(new SummaryVm(Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"), ReceiptHistoryService.Money(debt)));

        return rows;
    }

    // ── Действия ──

    private async void PrintCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_printing || _selected is not { } row || _selectedDetail is not { } detail)
            return;

        _printing = true;
        PrintCopyButton.IsEnabled = false;
        ShowAction(Tr.T("Печатаю копию…", "Көчүрмөсү басылып жатат…", "Printing a copy…", "Kopya yazdırılıyor…", "Nusxa chop etilmoqda…"), ok: true);
        try
        {
            var text = ReceiptHistoryService.BuildReprintText(row.Entry, detail);
            var error = await ReceiptHistoryService.PrintAsync(text).ConfigureAwait(true);
            ShowAction(error ?? Tr.T("Копия чека напечатана.", "Чектин көчүрмөсү басылып чыкты.", "The receipt copy has been printed.", "Fiş kopyası yazdırıldı.", "Chek nusxasi chop etildi."),
                ok: error == null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: печать копии не удалась: {ex}", "PRINTER");
            ShowAction(Tr.T("Не удалось напечатать чек: ", "Чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt: ", "Fiş yazdırılamadı: ", "Chekni chop etib bo'lmadi: ") + ex.Message, ok: false);
        }
        finally
        {
            _printing = false;
            PrintCopyButton.IsEnabled = _selectedDetail != null;
        }
    }

    /// <summary>«Вернуть…» — то же окно возврата, что и в меню, сразу с этим чеком.</summary>
    private async void Return_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || row.Entry.IsLocal)
            return;

        if (!CanReturn())
        {
            ShowAction(Tr.T("Недостаточно прав для возврата.", "Кайтарууга укук жетишсиз.", "You do not have permission to make returns.", "İade yapma yetkiniz yok.", "Qaytarish uchun huquq yetarli emas."), ok: false);
            return;
        }

        try
        {
            ReturnWasOpened = true;
            var dialog = App.GetRequiredService<ReturnSaleDialog>();
            dialog.InitialSaleId = row.Entry.Id;
            dialog.InitialReceiptNumber = row.Entry.ReceiptNumber;
            await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: окно возврата не открылось: {ex}", "WARNING");
            ShowAction(Tr.T("Не удалось открыть возврат: ", "Кайтарууну ачуу мүмкүн болгон жок: ", "Could not open the return: ", "İade açılamadı: ", "Qaytarishni ochib bo'lmadi: ") + ex.Message, ok: false);
            return;
        }

        // После возврата у чека другой статус и состав — перечитываем.
        _shownDetailId = null;
        _selectedDetail = null;
        await LoadAsync(row.Entry.Id).ConfigureAwait(true);
    }

    private void ShowAction(string text, bool ok)
    {
        ActionMessage.Text = text;
        // Кисти лежат в словарях темы: FindResource без темы их не видит и отдаёт UnsetValue —
        // приведение к IBrush роняло окно ещё до печати (найдено тестом 2026-09-27).
        if (Avalonia.Application.Current?.TryFindResource(ok ? "BrushUiStatusOk" : "BrushDanger", ActualThemeVariant, out var brush) == true
            && brush is Avalonia.Media.IBrush foreground)
            ActionMessage.Foreground = foreground;
        ActionMessage.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private static bool CanReturn()
    {
        try
        {
            return !TariffGate.IsStartTariff
                && App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.EmployeeReturn);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static decimal LineDiscount(JsonElement line)
    {
        foreach (var key in new[] { "discount_total", "line_discount", "discount" })
        {
            if (Money(line, key) is { } value && value > 0)
                return value;
        }

        return 0;
    }

    private static decimal? Money(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return JsonNumericReader.TryToDouble(v, out var d) ? (decimal)d : null;
    }

    private static decimal? ParseMoney(string? text) =>
        decimal.TryParse((text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    // ── Строки для разметки ──

    private sealed class RowVm
    {
        public RowVm(ReceiptHistoryEntry entry)
        {
            Entry = entry;
            StatusText = ReceiptHistoryService.StatusLabel(entry);
            IsDanger = entry.IsFailedUpload || ReceiptHistoryService.IsReturnedOrCanceled(entry);
            IsWarn = !IsDanger && (entry.IsPendingUpload || (!entry.IsLocal && entry.Status == "partially_returned"));
            IsInfo = !entry.IsLocal && entry.Status == "debt";
        }

        public ReceiptHistoryEntry Entry { get; }

        public string NumberText => Entry.ReceiptNumber;

        public string TimeText => Entry.CreatedAt.Date == DateTime.Today
            ? Entry.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : Entry.CreatedAt.ToString("dd.MM  HH:mm", CultureInfo.InvariantCulture);

        public string SummaryText => string.IsNullOrWhiteSpace(Entry.FirstItemName) ? "—" : Entry.FirstItemName;

        public string SubText => string.IsNullOrWhiteSpace(Entry.Cashier)
            ? ReceiptHistoryService.PaymentLabel(Entry.PaymentMethod)
            : Entry.Cashier + "  ·  " + ReceiptHistoryService.PaymentLabel(Entry.PaymentMethod);

        public string TotalText => ReceiptHistoryService.Money(Entry.Total);

        public string StatusText { get; }

        public bool IsWarn { get; }

        public bool IsDanger { get; }

        public bool IsInfo { get; }
    }

    private sealed class LineVm
    {
        public string Name { get; init; } = "";
        public string QtyPriceText { get; init; } = "";
        public string TotalText { get; init; } = "";
        public string DiscountText { get; init; } = "";
        public bool HasDiscount => DiscountText.Length > 0;
        public string ReturnedText { get; init; } = "";
        public bool HasReturned => ReturnedText.Length > 0;
    }

    private sealed class SummaryVm
    {
        public SummaryVm(string label, string value, bool isTotal = false)
        {
            Label = label;
            Value = value;
            IsTotal = isTotal;
        }

        public string Label { get; }
        public string Value { get; }
        public bool IsTotal { get; }
    }
}
