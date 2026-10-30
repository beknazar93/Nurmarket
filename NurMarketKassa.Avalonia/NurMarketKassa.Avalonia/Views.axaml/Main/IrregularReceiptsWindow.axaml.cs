using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
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
    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 2026-10-30: кнопка активна только когда есть отклонённые чеки; на случай гонки — спокойное пояснение, а не красная ошибка.
            if (_failedCount == 0)
            {
                LoadRows();
                return;
            }
            // 2026-10-20 (живой аудит кассы, К11): было одно нажатие без вопроса — а в списке висел чек на 387 378,89,
            // который сервер уже отклонил. Теперь — какие чеки уйдут, на какую сумму и почему сервер отказал в прошлый раз.
            var failed = OfflinePendingSalesStore.LoadAll()
                .Where(s => string.Equals(s.Status, OfflineSaleEntry.Failed, System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.CreatedAt)
                .ToList();
            if (failed.Count > 0)
            {
                var list = string.Join("\n", failed.Take(6).Select(s =>
                {
                    var (reason, _) = ReceiptHistoryService.DescribeUploadError(s.LastError);
                    reason ??= s.LastError;
                    if (reason is { Length: > 140 })
                        reason = reason[..140] + "…";
                    return $"• {s.CreatedAt.LocalDateTime:dd.MM HH:mm} — {TryComputeTotalText(s.CartJson)}"
                           + (string.IsNullOrWhiteSpace(reason) ? "" : "\n   " + Tr.T("Сервер ответил: ", "Сервер жооп берди: ", "Server said: ", "Sunucu yanıtı: ", "Server javobi: ") + reason);
                }));
                if (failed.Count > 6)
                    list += "\n…";
                if (await PosMessageBox.ShowModalAsync(this,
                        Tr.T($"Отправить на сервер повторно чеков: {failed.Count}?\n\n{list}\n\nСервер уже отказал им один раз. Отправляйте, только если сумма верная и причина отказа устранена.",
                            $"Серверге кайра жөнөтүлө турган чектер: {failed.Count}?\n\n{list}\n\nСервер аларды бир жолу четке каккан. Сумма туура болсо жана себеби жоюлса гана жөнөтүңүз.",
                            $"Resend {failed.Count} receipt(s) to the server?\n\n{list}\n\nThe server has already rejected them once. Send only if the amount is right and the reason is fixed.",
                            $"{failed.Count} fiş sunucuya yeniden gönderilsin mi?\n\n{list}\n\nSunucu bunları bir kez reddetti. Yalnızca tutar doğruysa ve neden giderildiyse gönderin.",
                            $"{failed.Count} ta chek serverga qayta yuborilsinmi?\n\n{list}\n\nServer ularni bir marta rad etgan. Faqat summa to'g'ri va sabab bartaraf etilgan bo'lsa yuboring."),
                        Tr.T("Отправить повторно", "Кайра жөнөтүү", "Resend", "Yeniden gönder", "Qayta yuborish"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        // 2026-10-22 (модалки, пакет 4): глагол «Отправить» вместо «Продолжить».
                        confirmText: Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"),
                        cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                    return;
            }

            var count = OfflinePendingSalesStore.RequeueFailed();
            ShowMessage(count > 0
                ? Tr.T($"Возвращено в очередь: {count}. Касса отправит их при ближайшей синхронизации.",
                    $"Кезекке кайтарылды: {count}. Касса аларды жакынкы синхрондоштурууда жөнөтөт.",
                    $"Returned to the queue: {count}. The till will send them at the next sync.",
                    $"Kuyruğa geri alındı: {count}. Kasa bunları bir sonraki senkronizasyonda gönderecek.",
                    $"Navbatga qaytarildi: {count}. Kassa ularni keyingi sinxronlashda yuboradi.")
                : Tr.T("Эти чеки отклонены сервером по существу — исправьте причину или закройте их без отправки.",
                    "Бул чектерди сервер маңызы боюнча четке какты — себебин оңдоңуз же жөнөтпөй жабыңыз.",
                    "The server rejected these receipts on the merits — fix the reason or close them without sending.",
                    "Sunucu bu fişleri esastan reddetti — nedeni düzeltin veya göndermeden kapatın.",
                    "Server bu cheklarni mohiyati bo'yicha rad etdi — sababini tuzating yoki yubormasdan yoping."));
            LoadRows();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Повторная отправка чеков не удалась: {ex}", "WARNING");
            ShowMessage(Tr.T("Не удалось вернуть чеки в очередь: ", "Чектерди кезекке кайтаруу мүмкүн болгон жок: ", "Could not return the receipts to the queue: ", "Fişler kuyruğa geri alınamadı: ", "Cheklarni navbatga qaytarib bo'lmadi: ") + ex.Message);
        }
    }

    private void ShowMessage(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    // 2026-10-30 («Некорректные чеки»: 8 возвратов + 1 продажа висели без действий, «Отправить повторно» была бесполезна): строки сгруппированы
    // по причине (фильтры сверху), у каждой — свои кнопки: «Показать», «Отправить» (только отклонённые сервером чеки очереди) и «Закрыть».
    private enum Kind { Failed, Overpaid, Stock, Pending }

    private List<RowVm> _rows = new();
    private Kind? _filter;
    private bool _showClosed;
    private int _failedCount;
    private int _closedCount;

    private static string KindTitle(Kind kind) => kind switch
    {
        Kind.Failed => Tr.T("Ошибка отправки на сервер", "Серверге жиберүү катасы", "Error sending to the server", "Sunucuya gönderme hatası", "Serverga yuborishda xato"),
        Kind.Overpaid => Tr.T("Возврат больше положенного", "Кайтаруу тийиштүүдөн көп", "Return above the due amount", "İade gerekenden fazla", "Qaytarish keragidan ko'p"),
        Kind.Stock => Tr.T("Не хватило остатка", "Калдык жетпей калды", "Not enough stock", "Stok yetersiz", "Qoldiq yetarli emas"),
        _ => Tr.T("В процессе (ожидает выгрузку)", "Жүрүп жатат (жүктөлүүнү күтүүдө)", "In progress (awaiting upload)", "İşlemde (gönderilmeyi bekliyor)", "Jarayonda (yuklashni kutmoqda)"),
    };

    private void LoadRows()
    {
        var rows = new List<RowVm>();
        _closedCount = 0;

        foreach (var entry in IrregularReceiptsStore.LoadAll())
        {
            if (entry.ClosedAt is not null)
            {
                _closedCount++;
                if (!_showClosed)
                    continue;
            }
            // 2026-10-20 (живой аудит кассы, К1): сервер записал возврат больше положенного — своя строка состояния.
            var overpaid = entry.Tag == IrregularReceiptEntry.ReturnOverpaid;
            var kind = overpaid ? Kind.Overpaid : Kind.Stock;
            var hasCart = !string.IsNullOrEmpty(entry.CartJson) && entry.CartJson != "{}";
            rows.Add(new RowVm
            {
                CreatedAt = entry.CreatedAt,
                Kind = kind,
                IsInsufficientStock = !overpaid,
                IsError = overpaid,
                StatusText = KindTitle(kind),
                Note = (entry.Note ?? "") + (entry.ClosedAt is null ? "" : "  [" + Tr.T("Закрыто: ", "Жабылды: ", "Closed: ", "Kapatıldı: ", "Yopildi: ") + entry.ClosedReason + "]"),
                RawNote = entry.Note ?? "",
                TotalText = FormatMoney(entry.Total),
                EntryId = entry.Id,
                CartJson = hasCart ? entry.CartJson : null,
                IsClosed = entry.ClosedAt is not null,
                Cashier = entry.CashierName,
            });
        }

        _failedCount = 0;
        foreach (var sale in OfflinePendingSalesStore.LoadAll())
        {
            if (string.Equals(sale.Status, OfflineSaleEntry.Synced, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var isFailed = string.Equals(sale.Status, OfflineSaleEntry.Failed, System.StringComparison.OrdinalIgnoreCase);
            if (isFailed && !sale.CheckoutCompleted)
                _failedCount++;
            // 2026-10-30 (приёмка): отказ «не хватило остатка» — в группу «Не хватило остатка» (оприходовать и отправить / закрыть).
            var stockFail = isFailed && LooksLikeStockError(sale.LastError);
            var kind = stockFail ? Kind.Stock : isFailed ? Kind.Failed : Kind.Pending;
            rows.Add(new RowVm
            {
                CreatedAt = sale.CreatedAt,
                Kind = kind,
                IsPending = !isFailed,
                IsError = isFailed && !stockFail,
                IsInsufficientStock = stockFail,
                StatusText = KindTitle(kind),
                Note = sale.LastError ?? "",
                RawNote = sale.LastError ?? "",
                TotalText = TryComputeTotalText(sale.CartJson),
                Id = sale.Id,
                QueueId = sale.Id,
                CartJson = sale.CartJson,
                CanSendRow = isFailed && !stockFail && !sale.CheckoutCompleted,
                CanReplenishRow = stockFail && !sale.CheckoutCompleted,
                CanCloseRow = isFailed,
            });
        }

        _rows = rows;
        RetryButton.IsEnabled = _failedCount > 0;
        BuildFilterChips();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var order = new Dictionary<Kind, int> { [Kind.Failed] = 0, [Kind.Overpaid] = 1, [Kind.Stock] = 2, [Kind.Pending] = 3 };
        var shown = _rows
            .Where(r => _filter is null || r.Kind == _filter)
            .OrderBy(r => r.IsClosed)
            .ThenBy(r => order[r.Kind])
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
        ReceiptsGrid.ItemsSource = shown;
        EmptyText.IsVisible = shown.Count == 0;
        UpdateHint();
    }

    /// <summary>Пояснение над таблицей — что это за записи и что с ними делать (вместо красного «Нечего отправлять повторно»).</summary>
    private void UpdateHint()
    {
        var open = _rows.Count(r => !r.IsClosed);
        string? text = null;
        if (_failedCount > 0)
            text = Tr.T("Эти чеки отклонены сервером по существу: проверьте причину, отправьте заново («Отправить») или закройте без отправки.",
                "Бул чектерди сервер маңызы боюнча четке какты: себебин текшериңиз, кайра жөнөтүңүз («Жөнөтүү») же жөнөтпөй жабыңыз.",
                "The server rejected these receipts on the merits: check the reason, send again (“Send”) or close without sending.",
                "Sunucu bu fişleri esastan reddetti: nedeni kontrol edin, yeniden gönderin (“Gönder”) veya göndermeden kapatın.",
                "Server bu cheklarni mohiyati bo'yicha rad etdi: sababini tekshiring, qayta yuboring («Yuborish») yoki yubormasdan yoping.");
        else if (open > 0)
            text = Tr.T("Сервер не отклонял чеки — повторять нечего. Записи ниже — справка о возвратах и продажах, прошедших нестандартно; операции уже проведены. Когда разобрались — «Закрыть запись».",
                "Сервер чектерди четке каккан жок — кайра жөнөтүүгө эч нерсе жок. Төмөнкү жазуулар — жол-жоболор бузулуп өткөн кайтаруулар менен сатуулар жөнүндө маалымат; операциялар аткарылган. Түшүнгөндөн кийин — «Жазууну жабуу».",
                "The server has not rejected any receipts — nothing to resend. The entries below are a reference for returns and sales that went through non-standardly; the operations are already done. When you have checked them — “Close entry”.",
                "Sunucu fiş reddetmedi — yeniden gönderilecek bir şey yok. Aşağıdakiler standart dışı geçen iade ve satışların kaydıdır; işlemler zaten yapıldı. İnceledikten sonra — “Kaydı kapat”.",
                "Server cheklarni rad etmagan — qayta yuboriladigan narsa yo'q. Quyidagilar nostandart o'tgan qaytarish va sotuvlar haqida ma'lumot; operatsiyalar bajarilgan. Tekshirgach — «Yozuvni yopish».");
        HintText.Text = text ?? "";
        HintText.IsVisible = text is not null;
    }

    private void BuildFilterChips()
    {
        FilterPanel.Children.Clear();
        var open = _rows.Where(r => !r.IsClosed).ToList();
        if (open.Count == 0 && _closedCount == 0)
        {
            FilterPanel.IsVisible = false;
            return;
        }
        FilterPanel.IsVisible = true;
        Button Chip(string text, bool selected, Action onClick)
        {
            var b = new Button { Content = text, Margin = new Avalonia.Thickness(0, 0, 8, 6), Padding = new Avalonia.Thickness(10, 3), Height = 30 };
            b.Classes.Add(selected ? "PrimaryActionButton" : "SecondaryActionButton");
            b.Click += (_, _) => onClick();
            return b;
        }
        if (_filter is { } f && open.All(r => r.Kind != f))
            _filter = null; // выбранной причины больше нет (всё закрыто) — показать все
        FilterPanel.Children.Add(Chip(Tr.T($"Все: {open.Count}", $"Баары: {open.Count}", $"All: {open.Count}", $"Tümü: {open.Count}", $"Hammasi: {open.Count}"), _filter is null, () => { _filter = null; BuildFilterChips(); ApplyFilter(); }));
        foreach (var kind in new[] { Kind.Failed, Kind.Overpaid, Kind.Stock, Kind.Pending })
        {
            var count = open.Count(r => r.Kind == kind);
            if (count == 0)
                continue;
            var k = kind;
            FilterPanel.Children.Add(Chip($"{KindTitle(kind)}: {count}", _filter == kind, () => { _filter = k; BuildFilterChips(); ApplyFilter(); }));
        }
        if (_closedCount > 0)
            FilterPanel.Children.Add(Chip(_showClosed
                    ? Tr.T($"Скрыть закрытые ({_closedCount})", $"Жабылгандарды жашыруу ({_closedCount})", $"Hide closed ({_closedCount})", $"Kapalıları gizle ({_closedCount})", $"Yopilganlarni yashirish ({_closedCount})")
                    : Tr.T($"Показать закрытые ({_closedCount})", $"Жабылгандарды көрсөтүү ({_closedCount})", $"Show closed ({_closedCount})", $"Kapalıları göster ({_closedCount})", $"Yopilganlarni ko'rsatish ({_closedCount})"),
                false, () => { _showClosed = !_showClosed; LoadRows(); }));
    }

    private static bool LooksLikeStockError(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && (error.Contains("остат", StringComparison.OrdinalIgnoreCase) || error.Contains("не хватило", StringComparison.OrdinalIgnoreCase)
            || error.Contains("insufficient", StringComparison.OrdinalIgnoreCase) || error.Contains("stock", StringComparison.OrdinalIgnoreCase)
            || error.Contains("нет в наличии", StringComparison.OrdinalIgnoreCase));

    /// <summary>2026-10-30 (приёмка): «Оприходовать и отправить» — чек отклонён «не хватило остатка»: по подтверждению владельца товар оприходуется
    /// на недостающее количество (ревизия «итог = нужное количество», один документ на товар, как «Изменить остаток»), затем чек возвращается в очередь.
    /// «Принять в минус» (allow_negative_stock) появится, когда сервер это поддержит (ТЗ Умару, п. 11).</summary>
    private async void ReplenishRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RowVm row } || !row.CanReplenish || row.QueueId is null)
            return;
        try
        {
            var sale = OfflinePendingSalesStore.TryGetById(row.QueueId);
            if (sale is null)
                return;
            // что нужно оприходовать: количество по каждому товару чека (в единицах склада)
            var need = new Dictionary<string, (string Title, double Qty)>(StringComparer.OrdinalIgnoreCase);
            using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(sale.CartJson) ? "{}" : sale.CartJson))
            {
                foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(doc.RootElement))
                {
                    var pid = CartDisplayHelper.TryProductId(line);
                    if (string.IsNullOrWhiteSpace(pid))
                        continue;
                    var tile = ProductActions.Find(pid);
                    var q = CartDisplayHelper.LineQuantityInStockUnits(line, tile);
                    need[pid] = need.TryGetValue(pid, out var was) ? (was.Title, was.Qty + q) : (CartDisplayHelper.ItemName(line), q);
                }
            }
            if (need.Count == 0)
            {
                ShowMessage(Tr.T("В чеке нет товаров с известным номером — оприходовать нечего.", "Чекте номери белгилүү товарлар жок — кирешелөөгө эч нерсе жок.", "The receipt has no products with a known id — nothing to receive.", "Fişte kimliği bilinen ürün yok — girişe alınacak bir şey yok.", "Chekda id si ma'lum mahsulot yo'q — kirim qilinadigan narsa yo'q."));
                return;
            }
            var list = string.Join("\n", need.Take(8).Select(n => $"• {n.Value.Title} — {n.Value.Qty:0.###}"));
            if (need.Count > 8)
                list += "\n…";
            if (await PosMessageBox.ShowModalAsync(this,
                    Tr.T($"Оприходовать на складе товары чека от {row.DateText} {row.TimeText} ({row.TotalText}) и отправить чек на сервер?\n\n{list}\n\nОстаток на сервере будет доведён до нужного количества документом ревизии (если его уже хватает — товар не трогается). Затем чек уйдёт в очередь.",
                        $"{row.DateText} {row.TimeText} күнүндөгү чектин ({row.TotalText}) товарларын складга кирешелеп, чекти серверге жөнөтөбүзбү?\n\n{list}\n\nСервердеги калдык ревизия документи менен керектүү санга жеткирилет (жетсе — товарга тийилбейт). Андан кийин чек кезекке кетет.",
                        $"Receive the products of the receipt from {row.DateText} {row.TimeText} ({row.TotalText}) into stock and send the receipt to the server?\n\n{list}\n\nThe server stock is brought up to the needed quantity with a revision document (if it is already enough, the product is not touched). Then the receipt goes to the queue.",
                        $"{row.DateText} {row.TimeText} tarihli fişin ({row.TotalText}) ürünleri depoya girilsin ve fiş sunucuya gönderilsin mi?\n\n{list}\n\nSunucudaki stok, bir sayım belgesiyle gereken miktara getirilir (yeterliyse ürüne dokunulmaz). Sonra fiş kuyruğa gider.",
                        $"{row.DateText} {row.TimeText} dagi chekning ({row.TotalText}) mahsulotlari omborga kirim qilinib, chek serverga yuborilsinmi?\n\n{list}\n\nServerdagi qoldiq inventarizatsiya hujjati bilan kerakli miqdorga yetkaziladi (yetarli bo'lsa — mahsulotga tegilmaydi). Keyin chek navbatga ketadi."),
                    Tr.T("Оприходовать и отправить", "Кирешелеп жөнөтүү", "Receive and send", "Girişe al ve gönder", "Kirim qilib yuborish"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question,
                    confirmText: Tr.T("Оприходовать и отправить", "Кирешелеп жөнөтүү", "Receive and send", "Girişe al ve gönder", "Kirim qilib yuborish"),
                    cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return;

            var who = PosApp.CurrentUserDisplayName;
            var failedProducts = new List<string>();
            foreach (var (pid, item) in need.Select(n => (n.Key, n.Value)))
            {
                double? current = null;
                try
                {
                    var detail = await PosApp.CatalogApi.ProductsDetailAsync(pid).ConfigureAwait(true);
                    if (detail is { } el)
                        current = StockSyncService.ResolveStockQuantity(el, ProductActions.Find(pid)?.MustWeigh ?? false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Некорректные чеки: остаток «{item.Title}» не получен: {ex.Message}", "WARNING");
                }
                if (current is null)
                {
                    failedProducts.Add(item.Title);
                    continue;
                }
                if (current.Value >= item.Qty - 0.0005)
                    continue; // уже хватает
                var result = await ProductActions.ChangeStockAsync(pid, null, item.Qty,
                    "Оприходовано для отправки чека, отклонённого «не хватило остатка»", who, expectedCurrent: current).ConfigureAwait(true);
                if (!result.Ok)
                    failedProducts.Add(item.Title + ": " + result.Message);
            }
            if (failedProducts.Count > 0)
            {
                // Чек в очередь не возвращаем: часть товаров не оприходована.
                ShowMessage(Tr.T("Не всё оприходовано, чек не отправлен: ", "Баары кирешеленген жок, чек жөнөтүлгөн жок: ", "Not everything was received, the receipt was not sent: ", "Hepsi girişe alınmadı, fiş gönderilmedi: ", "Hammasi kirim qilinmadi, chek yuborilmadi: ") + string.Join("; ", failedProducts));
                return;
            }
            var ok = OfflinePendingSalesStore.RequeueFailedById(row.QueueId);
            PosLogger.Log($"Некорректные чеки: чек {row.QueueId} — товары оприходованы ({who ?? "—"}), чек возвращён в очередь: {ok}.", "OFFLINE");
            ShowMessage(ok
                ? Tr.T("Товары оприходованы, чек возвращён в очередь. Касса отправит его при ближайшей синхронизации.", "Товарлар кирешеленди, чек кезекке кайтарылды. Касса аны жакынкы синхрондоштурууда жөнөтөт.", "Products received, the receipt is back in the queue. The till will send it at the next sync.", "Ürünler girişe alındı, fiş kuyruğa geri alındı. Kasa bir sonraki senkronizasyonda gönderecek.", "Mahsulotlar kirim qilindi, chek navbatga qaytarildi. Kassa uni keyingi sinxronlashda yuboradi.")
                : Tr.T("Товары оприходованы, но чек уже нельзя вернуть в очередь.", "Товарлар кирешеленди, бирок чекти кезекке кайтаруу мүмкүн эмес.", "Products received, but the receipt can no longer be returned to the queue.", "Ürünler girişe alındı ancak fiş artık kuyruğa alınamıyor.", "Mahsulotlar kirim qilindi, lekin chekni navbatga qaytarib bo'lmaydi."));
            LoadRows();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оприходование для чека из «Некорректных чеков» не удалось: {ex}", "WARNING");
            ShowMessage(ex.Message);
        }
    }

    /// <summary>«Отправить» в строке: один отклонённый чек обратно в очередь (с вопросом — сервер уже отказал один раз).</summary>
    private async void SendRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RowVm row } || !row.CanSend)
            return;
        try
        {
            var (reason, _) = ReceiptHistoryService.DescribeUploadError(row.RawNote);
            reason ??= row.RawNote;
            if (reason is { Length: > 200 })
                reason = reason[..200] + "…";
            if (await PosMessageBox.ShowModalAsync(this,
                    Tr.T($"Отправить на сервер чек от {row.DateText} {row.TimeText} на {row.TotalText}?\n\nСервер ответил: {reason}\n\nСервер уже отказал один раз. Отправляйте, только если причина отказа устранена.",
                        $"{row.DateText} {row.TimeText} күнүндөгү {row.TotalText} чекти серверге жөнөтөбүзбү?\n\nСервер жообу: {reason}\n\nСервер бир жолу четке каккан. Четке кагуу себеби жоюлса гана жөнөтүңүз.",
                        $"Send the receipt from {row.DateText} {row.TimeText} for {row.TotalText} to the server?\n\nServer said: {reason}\n\nThe server has already rejected it once. Send only if the reason is fixed.",
                        $"{row.DateText} {row.TimeText} tarihli {row.TotalText} tutarındaki fiş sunucuya gönderilsin mi?\n\nSunucu yanıtı: {reason}\n\nSunucu bunu bir kez reddetti. Yalnızca neden giderildiyse gönderin.",
                        $"{row.DateText} {row.TimeText} dagi {row.TotalText} lik chek serverga yuborilsinmi?\n\nServer javobi: {reason}\n\nServer uni bir marta rad etgan. Faqat sabab bartaraf etilgan bo'lsa yuboring."),
                    Tr.T("Отправить чек", "Чекти жөнөтүү", "Send receipt", "Fişi gönder", "Chekni yuborish"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    confirmText: Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"),
                    cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return;
            var ok = row.QueueId is not null && OfflinePendingSalesStore.RequeueFailedById(row.QueueId);
            ShowMessage(ok
                ? Tr.T("Чек возвращён в очередь. Касса отправит его при ближайшей синхронизации.", "Чек кезекке кайтарылды. Касса аны жакынкы синхрондоштурууда жөнөтөт.", "The receipt is back in the queue. The till will send it at the next sync.", "Fiş kuyruğa geri alındı. Kasa bir sonraki senkronizasyonda gönderecek.", "Chek navbatga qaytarildi. Kassa uni keyingi sinxronlashda yuboradi.")
                : Tr.T("Этот чек уже нельзя отправить повторно (он проведён на сервере или не в ошибке).", "Бул чекти кайра жөнөтүүгө болбойт (серверде өткөрүлгөн же катада эмес).", "This receipt can no longer be resent (it is posted on the server or not in error).", "Bu fiş artık yeniden gönderilemez (sunucuda işlendi veya hatada değil).", "Bu chekni qayta yuborib bo'lmaydi (serverda o'tkazilgan yoki xatoda emas)."));
            LoadRows();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отправка чека из «Некорректных чеков» не удалась: {ex}", "WARNING");
            ShowMessage(ex.Message);
        }
    }

    /// <summary>«Закрыть» в строке: запись (справка о проведённой операции) или отклонённый чек закрываются без отправки — с подтверждением,
    /// причиной и строкой в журнале кассы.</summary>
    private async void CloseRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RowVm row } || !row.CanClose)
            return;
        try
        {
            var queue = row.QueueId is not null;
            var reason = queue
                ? Tr.T("Закрыто вручную без отправки: чек на сервер не отправляется", "Кол менен жөнөтүүсүз жабылды: чек серверге жөнөтүлбөйт", "Closed manually without sending: the receipt is not sent to the server", "Elle göndermeden kapatıldı: fiş sunucuya gönderilmez", "Qo'lda yubormasdan yopildi: chek serverga yuborilmaydi")
                : Tr.T("Разобрано: операция уже проведена, повторять нечего", "Каралды: операция аткарылган, кайталоого эч нерсе жок", "Reviewed: the operation is already done, nothing to repeat", "İncelendi: işlem zaten yapıldı, tekrarlanacak bir şey yok", "Ko'rib chiqildi: operatsiya bajarilgan, takrorlash kerak emas");
            var text = queue
                ? Tr.T($"Закрыть чек от {row.DateText} {row.TimeText} на {row.TotalText} без отправки?\n\nНа сервере этой продажи НЕ появится, хотя деньги по ней приняты в кассе: убедитесь, что это учтено (например, оформлена вручную на сайте).\n\nПричина запишется в журнал: {reason}.",
                    $"{row.DateText} {row.TimeText} күнүндөгү {row.TotalText} чекти жөнөтпөй жабабызбы?\n\nСерверде бул сатуу ЧЫГАБАЙТ, бирок акча кассада кабыл алынган: муну эске алыңыз (мисалы, сайтта кол менен жасалган).\n\nСебеп журналга жазылат: {reason}.",
                    $"Close the receipt from {row.DateText} {row.TimeText} for {row.TotalText} without sending?\n\nThis sale will NOT appear on the server although the money was taken at the till: make sure this is accounted for (for example, entered by hand on the website).\n\nThe reason is written to the log: {reason}.",
                    $"{row.DateText} {row.TimeText} tarihli {row.TotalText} tutarındaki fiş göndermeden kapatılsın mı?\n\nPara kasada alınmasına rağmen bu satış sunucuda GÖRÜNMEYECEK: bunun hesaba katıldığından emin olun (örneğin sitede elle girildi).\n\nNeden günlüğe yazılır: {reason}.",
                    $"{row.DateText} {row.TimeText} dagi {row.TotalText} lik chek yubormasdan yopilsinmi?\n\nPul kassada olingan bo'lsa ham bu sotuv serverda PAYDO BO'LMAYDI: buni hisobga olganingizga ishonch hosil qiling (masalan, saytda qo'lda kiritilgan).\n\nSabab jurnalga yoziladi: {reason}.")
                : Tr.T($"Закрыть запись от {row.DateText} {row.TimeText} ({row.StatusText})?\n\nЭто справка о проведённой операции — на сервер ничего не отправляется. Запись скроется из списка (вернуть: «Показать закрытые»).",
                    $"{row.DateText} {row.TimeText} жазуусун ({row.StatusText}) жабабызбы?\n\nБул аткарылган операция жөнүндө маалымат — серверге эч нерсе жөнөтүлбөйт. Жазуу тизмеден жашырылат («Жабылгандарды көрсөтүү» менен кайтарылат).",
                    $"Close the entry from {row.DateText} {row.TimeText} ({row.StatusText})?\n\nThis is a reference about an operation that is already done — nothing is sent to the server. The entry is hidden from the list (bring it back with “Show closed”).",
                    $"{row.DateText} {row.TimeText} kaydı ({row.StatusText}) kapatılsın mı?\n\nBu, yapılmış bir işlemin kaydıdır — sunucuya bir şey gönderilmez. Kayıt listeden gizlenir (“Kapalıları göster” ile geri gelir).",
                    $"{row.DateText} {row.TimeText} yozuvi ({row.StatusText}) yopilsinmi?\n\nBu bajarilgan operatsiya haqidagi ma'lumot — serverga hech narsa yuborilmaydi. Yozuv ro'yxatdan yashiriladi («Yopilganlarni ko'rsatish» bilan qaytariladi).");
            if (await PosMessageBox.ShowModalAsync(this,
                    text,
                    queue ? Tr.T("Закрыть без отправки", "Жөнөтпөй жабуу", "Close without sending", "Göndermeden kapat", "Yubormasdan yopish") : Tr.T("Закрыть запись", "Жазууну жабуу", "Close entry", "Kaydı kapat", "Yozuvni yopish"),
                    MessageBoxButton.YesNo, queue ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    confirmText: queue ? Tr.T("Закрыть без отправки", "Жөнөтпөй жабуу", "Close without sending", "Göndermeden kapat", "Yubormasdan yopish") : Tr.T("Закрыть запись", "Жазууну жабуу", "Close entry", "Kaydı kapat", "Yozuvni yopish"),
                    cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return;
            var who = PosApp.CurrentUserDisplayName;
            if (queue)
                OfflinePendingSalesStore.CloseWithoutSending(row.QueueId!, reason);
            else if (row.EntryId is not null)
                IrregularReceiptsStore.Close(row.EntryId, reason, who);
            ErrorText.IsVisible = false;
            LoadRows();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закрытие записи «Некорректных чеков» не удалось: {ex}", "WARNING");
            ShowMessage(ex.Message);
        }
    }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    // 2026-10-20 (живой аудит кассы, К18): «387 378,89 сом» (MoneyText) вместо «387378.89 сом» и «931 сом» без копеек.
    private static string FormatMoney(double value) => MoneyText.Som(value);

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
            return MoneyText.Som(total);
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

        // 2026-10-30: у записи без чека (возврат больше положенного, продажа при нулевом остатке без снимка) — подробности: кто, когда, что записано.
        if (!row.HasCart)
        {
            var details = new List<string>
            {
                row.StatusText,
                $"{row.DateText} {row.TimeText}" + (string.IsNullOrWhiteSpace(row.Cashier) ? "" : " · " + row.Cashier),
                new string('-', 24),
            };
            details.AddRange((row.RawNote ?? "").Split(new[] { ". ", "; " }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().TrimEnd('.') + "."));
            details.Add(Tr.T("Сумма расхождения: ", "Айырма суммасы: ", "Difference: ", "Fark: ", "Farq: ") + row.TotalText);
            PopupTitle.Text = row.StatusText;
            PopupItemsControl.ItemsSource = details;
            PopupPrintButton.IsVisible = false;
            ReceiptDetailsPopup.IsOpen = true;
            ErrorText.IsVisible = false;
            return;
        }
        PopupPrintButton.IsVisible = true;

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
            items.Add(Tr.T($"ИТОГО: {previewTotal:N2} сом", $"ЖЫЙЫНТЫК: {previewTotal:N2} сом", $"TOTAL: {previewTotal:N2} som", $"TOPLAM: {previewTotal:N2} som", $"JAMI: {previewTotal:N2} so'm"));

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
            ErrorText.Text = Tr.T("Не удалось загрузить детали чека: ", "Чектин чоо-жайын жүктөө мүмкүн болгон жок: ", "Could not load the receipt details: ", "Fiş ayrıntıları yüklenemedi: ", "Chek tafsilotlarini yuklab bo'lmadi: ") + ex.Message;
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
            lines.Add("ПОВТОРНАЯ ПЕЧАТЬ");
            lines.Add(DateTime.Now.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture));

            // В фоне: медленный принтер не подвешивает окно.
            var text = string.Join("\n", lines);
            await System.Threading.Tasks.Task.Run(() => ReceiptPrintService.PrintReceipt("{}", receiptText: text)).ConfigureAwait(true);
            ErrorText.IsVisible = false;
        }
        catch (Exception ex)
        {
            ErrorText.Text = Tr.T("Не удалось напечатать чек: ", "Чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt: ", "Fiş yazdırılamadı: ", "Chekni chop etib bo'lmadi: ") + ex.Message;
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
        public Kind Kind { get; set; }
        public bool IsPending { get; set; }
        public bool IsError { get; set; }
        public bool IsInsufficientStock { get; set; }
        public string StatusText { get; set; } = "";
        public string Note { get; set; } = "";
        public string RawNote { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string? Id { get; set; }
        /// <summary>Id очереди офлайн-продаж (только у строк-чеков очереди).</summary>
        public string? QueueId { get; set; }
        /// <summary>Id записи журнала «Некорректные чеки» (только у записей журнала).</summary>
        public string? EntryId { get; set; }
        public string? CartJson { get; set; }
        public string? Cashier { get; set; }
        public bool IsClosed { get; set; }
        public bool CanSendRow { get; set; }
        public bool CanReplenishRow { get; set; }
        public bool CanCloseRow { get; set; }
        public bool HasCart => !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(CartJson) && CartJson != "{}";
        public bool CanView => HasCart || !string.IsNullOrWhiteSpace(RawNote);
        public bool CanSend => CanSendRow;
        public bool CanReplenish => CanReplenishRow;
        public string ReplenishText => Tr.T("Оприходовать и отправить", "Кирешелеп жөнөтүү", "Receive and send", "Girişe al ve gönder", "Kirim qilib yuborish");
        public bool CanClose => !IsClosed && (CanCloseRow || EntryId is not null);
        public string ViewText => HasCart
            ? Tr.T("Показать чек", "Чекти көрсөтүү", "Show receipt", "Fişi göster", "Chekni ko'rsatish")
            : Tr.T("Подробнее", "Толугураак", "Details", "Ayrıntılar", "Batafsil");
        public string SendText => Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish");
        public string CloseText => QueueId is not null
            ? Tr.T("Закрыть без отправки", "Жөнөтпөй жабуу", "Close without sending", "Göndermeden kapat", "Yubormasdan yopish")
            : Tr.T("Проверено", "Текшерилди", "Reviewed", "İncelendi", "Tekshirildi");
    }
}
