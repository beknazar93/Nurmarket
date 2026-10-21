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

    /// <summary>2026-10-20: «Отправить на сервер» — досылка офлайн-очереди (ReceiptHistoryService.SendPendingNowAsync →
    /// SyncService). Свойства — чтобы стенд снимков подставил свою досылку без сети и базы; касса их не меняет.</summary>
    public Func<string, CancellationToken, Task<(bool Sent, string Message)>> SendPending { get; set; } = ReceiptHistoryService.SendPendingNowAsync;

    public Func<bool> IsQueueSending { get; set; } = ReceiptHistoryService.IsQueueSending;

    private bool _sending;
    private SyncService? _sync;

    public ReceiptHistoryWindow()
    {
        InitializeComponent();
        _canReturn = CanReturn();
        EmptyHintText.Text = Tr.T("Выберите другой период — «Сегодня» или «Вчера», или нажмите «Обновить».",
            "Башка мезгилди тандаңыз — «Бүгүн» же «Кечээ», же «Жаңылоо» баскычын басыңыз.",
            "Choose another period — “Today” or “Yesterday”, or press “Refresh”.",
            "Başka bir dönem seçin — «Bugün» veya «Dün» ya da «Yenile»ye basın.",
            "Boshqa davrni tanlang — «Bugun» yoki «Kecha», yoki «Yangilash»ni bosing.");
        SendButtonText.Text = SendLabel;
        CardTotalLabel.Text = Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
        // 2026-10-20 (К6): заголовок блока возвратов под чеком.
        ReturnsCaption.Text = Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar");
        // Досылка идёт сама (раз в 45 с) — кнопка «Отправить на сервер» показывает «Отправляется…», пока она идёт.
        try
        {
            _sync = App.GetRequiredService<SyncService>();
            _sync.StateChanged += OnSyncStateChanged;
        }
        catch (Exception)
        {
            _sync = null;
        }
        Opened += OnFirstOpened;
        // 2026-10-05, снимок владельца с телефона: «Вчера», поиск и «Обновить» уходили за правый край.
        NarrowLayout.Attach(this, 600, ApplyNarrowLayout);
    }

    /// <summary>2026-10-05: телефон — период отдельной строкой, ниже поиск и «Обновить»; поля окна меньше.</summary>
    private void ApplyNarrowLayout(bool narrow)
    {
        RootCard.Padding = new Avalonia.Thickness(narrow ? 12 : 20);
        RootCard.Margin = new Avalonia.Thickness(narrow ? 6 : 12);
        if (narrow)
        {
            ToolbarGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            ToolbarGrid.RowDefinitions = new RowDefinitions("Auto,8,Auto");
            Grid.SetColumnSpan(PeriodChips, 2);
            Grid.SetRow(SearchBox, 2);
            Grid.SetColumn(SearchBox, 0);
            SearchBox.Margin = new Avalonia.Thickness(0, 0, 8, 0);
            Grid.SetRow(RefreshButton, 2);
            Grid.SetColumn(RefreshButton, 1);
        }
        else
        {
            ToolbarGrid.ColumnDefinitions = new ColumnDefinitions("Auto,3*,*,Auto");
            ToolbarGrid.ColumnDefinitions[1].MaxWidth = 480;
            ToolbarGrid.RowDefinitions = new RowDefinitions();
            Grid.SetColumnSpan(PeriodChips, 1);
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 1);
            SearchBox.Margin = new Avalonia.Thickness(12, 0);
            Grid.SetRow(RefreshButton, 0);
            Grid.SetColumn(RefreshButton, 3);
        }
    }

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        ApplyShiftAvailability();
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>2026-10-20, проверка фильтров: смена закрыта — «Эта смена» выключена (подсказка «Смена закрыта»), выбрано «Сегодня».
    /// Раньше «Эта смена» оставалась отмеченной, а показывались чеки за сегодня.</summary>
    private void ApplyShiftAvailability()
    {
        if (PeriodChips.Child is not Panel panel)
            return;
        var radios = panel.Children.OfType<RadioButton>().ToList();
        var shift = radios.FirstOrDefault(r => "shift".Equals(r.Tag));
        var today = radios.FirstOrDefault(r => "today".Equals(r.Tag));
        if (shift is null || today is null)
            return;
        var hasShift = ReceiptHistoryService.CurrentShiftId() is not null;
        shift.IsEnabled = hasShift;
        ToolTip.SetShowOnDisabled(shift, true);
        ToolTip.SetTip(shift, hasShift ? null : Tr.T("Смена закрыта — откройте смену, чтобы видеть её чеки",
            "Смена жабык — анын чектерин көрүү үчүн сменаны ачыңыз", "The shift is closed — open a shift to see its receipts",
            "Vardiya kapalı — fişlerini görmek için vardiya açın", "Smena yopiq — uning cheklarini ko'rish uchun smenani oching"));
        if (!hasShift && _period == ReceiptHistoryPeriod.CurrentShift)
        {
            _period = ReceiptHistoryPeriod.Today;
            today.IsChecked = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _detailCts?.Cancel();
        if (_sync != null)
            _sync.StateChanged -= OnSyncStateChanged;
        base.OnClosed(e);
    }

    private void OnSyncStateChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSendButton);

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
        DiscountRows.ItemsSource = null;
        PeopleRows.ItemsSource = null;
        ReceiptCard.IsVisible = false;
        // 2026-10-20 (К6): блок возвратов прежнего чека — не показываем у нового.
        _returnsCts?.Cancel();
        ReturnsPanel.IsVisible = false;
        DetailErrorText.IsVisible = false;
        ActionMessage.IsVisible = false;
        PrintCopyButton.IsEnabled = false;
        DocReceiptButton.IsEnabled = DocWaybillButton.IsEnabled = false;
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
            DocReceiptButton.IsEnabled = DocWaybillButton.IsEnabled = true;
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

        // 2026-10-20, как в макете: «12.10.2026 · 14:33 · Айгерим Садыкова».
        var meta = entry.CreatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                   + " · " + entry.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(entry.Cashier))
            meta += " · " + entry.Cashier.Trim();
        DetailMeta.Text = meta;

        // 2026-10-20, владелец: под плашкой — причина последнего отказа сервера простыми словами и когда была попытка.
        var (reason, fromServer) = entry.IsLocal ? ReceiptHistoryService.DescribeUploadError(entry.Local!.LastError) : (null, false);
        DetailReasonText.Text = reason == null
            ? ""
            : (fromServer || entry.IsFailedUpload
                  ? Tr.T("Сервер не принял: ", "Сервер кабыл алган жок: ", "The server did not accept it: ", "Sunucu kabul etmedi: ", "Server qabul qilmadi: ")
                  : Tr.T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sababi: "))
              + reason;
        DetailReasonText.IsVisible = reason != null;
        var attempt = entry.Local?.LastAttemptAt;
        DetailAttemptText.Text = attempt is { } at
            ? Tr.T("Последняя попытка: ", "Акыркы аракет: ", "Last attempt: ", "Son deneme: ", "Oxirgi urinish: ")
              + at.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : "";
        DetailAttemptText.IsVisible = attempt != null && (entry.IsFailedUpload || entry.IsPendingUpload);

        if (entry.IsFailedUpload)
        {
            DetailNoticeText.Text = Tr.T(
                "Сервер отклонил этот чек. Отправить его повторно можно в разделе «Некорректные чеки».",
                "Сервер бул чекти четке какты. Аны «Туура эмес чектер» бөлүмүнөн кайра жөнөтсө болот.",
                "The server rejected this receipt. You can resend it in “Irregular receipts”.",
                "Sunucu bu fişi reddetti. «Sorunlu fişler» bölümünden yeniden gönderebilirsiniz.",
                "Server bu chekni rad etdi. Uni «Noto'g'ri cheklar» bo'limidan qayta yuborish mumkin.");
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
        UpdateSendButton();
    }

    private static string SendLabel => Tr.T("Отправить на сервер", "Серверге жөнөтүү", "Send to the server", "Sunucuya gönder", "Serverga yuborish");

    /// <summary>2026-10-20: «Отправить на сервер» — только у чека «Не отправлен на сервер»; тогда она одна жёлтая, а
    /// «Печать копии» — обводкой. Пока досылка идёт (сама или по кнопке) — выключена, «Отправляется…».</summary>
    private void UpdateSendButton()
    {
        var pending = _selected?.Entry.IsPendingUpload == true;
        SendButton.IsVisible = pending;
        PrintCopyButton.Classes.Set("sec-primary", !pending);
        PrintCopyButton.Classes.Set("sec-outline", pending);
        // Неотправленный чек: [Отправить на сервер][Печать копии] в один ряд (возврата у него нет);
        // иначе — [Печать копии][Вернуть…] или печать на всю ширину.
        Grid.SetColumn(PrintCopyButton, pending ? 2 : 0);
        Grid.SetColumnSpan(PrintCopyButton, pending || ReturnButton.IsVisible ? 1 : 3);
        if (!pending)
            return;

        bool busy;
        try
        {
            busy = _sending || IsQueueSending();
        }
        catch (Exception)
        {
            busy = _sending;
        }

        SendButton.IsEnabled = !busy;
        SendButtonText.Text = busy ? ReceiptHistoryService.SendingNowText : SendLabel;
        Avalonia.Automation.AutomationProperties.SetName(SendButton, SendButtonText.Text);
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_sending || _selected is not { Entry: { IsPendingUpload: true } entry } row)
            return;

        _sending = true;
        UpdateSendButton();
        ShowAction(ReceiptHistoryService.SendingNowText, ok: true);
        var startedAt = DateTime.Now;
        (bool Sent, string Message) result;
        try
        {
            result = await SendPending(entry.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: «Отправить на сервер» упало: {ex}", "OFFLINE");
            result = (false, Tr.T("Не удалось отправить чек: ", "Чекти жөнөтүү мүмкүн болгон жок: ", "Could not send the receipt: ",
                "Fiş gönderilemedi: ", "Chekni yuborib bo'lmadi: ") + ex.Message);
        }
        finally
        {
            _sending = false;
        }

        // Список — заново: отправленный чек приходит с сервера со статусом «Оплачен» и своим номером.
        _shownDetailId = null;
        _selectedDetail = null;
        await LoadAsync(result.Sent ? null : entry.Id).ConfigureAwait(true);
        if (result.Sent && FindSentReceipt(row, startedAt) is { } sent)
            ReceiptsList.SelectedItem = sent;
        UpdateSendButton();
        if (DetailsPanel.IsVisible)
        {
            ShowAction(result.Message, result.Sent);
        }
        else
        {
            // Чек ушёл, но в списке его не нашли (другой период, поиск) — сообщение над списком.
            NoticeText.Text = result.Message;
            NoticeBorder.IsVisible = true;
        }
    }

    /// <summary>Отправленный чек в обновлённом списке. Запись очереди удаляется после отправки, id продажи на сервере
    /// из неё уже не прочитать — ищем чек с сервера с той же суммой, проведённый после нажатия (новые — первыми).</summary>
    private RowVm? FindSentReceipt(RowVm was, DateTime startedAt) =>
        _rows.Where(r => !r.Entry.IsLocal
                         && Math.Abs(r.Entry.Total - was.Entry.Total) < 0.01m
                         && r.Entry.CreatedAt >= startedAt.AddMinutes(-2))
            .OrderByDescending(r => r.Entry.CreatedAt)
            .FirstOrDefault();

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

            // 2026-10-20, как в макете: «2 × 85,00» мелко, сумма строки жирно справа; «сом» — только у итога.
            lines.Add(new LineVm
            {
                Name = name,
                QtyPriceText = $"{qty.ToString("0.###", Ru)} × {MoneyText.Amount(price)}",
                TotalText = MoneyText.Amount(lineTotal),
                DiscountText = discount > 0
                    ? Tr.T("Скидка: −", "Арзандатуу: −", "Discount: −", "İndirim: −", "Chegirma: −") + MoneyText.Amount(discount)
                    : "",
                ReturnedText = returned > 1e-6
                    ? Tr.T("Возвращено: ", "Кайтарылды: ", "Returned: ", "İade edildi: ", "Qaytarildi: ") + returned.ToString("0.###", Ru)
                    : "",
            });
        }

        // 2026-10-20 (живой аудит кассы, К6): после частичного возврата сервер пересчитывает продажу (001836: «Получено
        // 70,59, Сдача 20,19» вместо 100,00 / 28,60, возвращённая строка пропадает). Если касса запомнила продажу до
        // возвратов (SaleReturnLedger) — чек как был, а возвраты — отдельным блоком ниже.
        var ledger = entry.IsLocal ? null : SaleReturnLedger.Get(entry.Id);
        var serverShowsReturns = !entry.IsLocal
            && (ReceiptHistoryService.IsReturnedOrCanceled(entry) || entry.Status == "partially_returned" || lines.Any(l => l.HasReturned));
        var hasReturns = ledger is { Refunds.Count: > 0 } || serverShowsReturns;
        var original = hasReturns && ledger is { Total: > 0m, Lines.Count: > 0 } ? ledger : null;
        if (original is not null)
            lines = OriginalLines(original, detail);

        DetailLines.ItemsSource = lines;
        if (lines.Count == 0)
        {
            DetailErrorText.Text = Tr.T("В чеке нет позиций.", "Чекте позиция жок.", "The receipt has no items.", "Fişte kalem yok.", "Chekda pozitsiyalar yo'q.");
            DetailErrorText.IsVisible = true;
        }

        // Поиск находит чек и по товарам, которые стали известны только сейчас.
        if (names.Count > 0)
            entry.SearchText += " " + string.Join(" ", names);

        var (discounts, total, payments) = hasReturns && ledger is { Total: > 0m }
            ? BuildOriginalSummary(ledger)
            : BuildSummary(entry, detail, hideCashDetails: hasReturns);
        DiscountRows.ItemsSource = discounts;
        CardTotal.Text = ReceiptHistoryService.Money(total);
        // 2026-10-20 (К6): у пересчитанного сервером чека (касса продажу до возврата не видела) — «Итого после возвратов».
        CardTotalLabel.Text = hasReturns && ledger is not { Total: > 0m }
            ? Tr.T("Итого после возвратов", "Кайтаруудан кийинки жыйынтык", "Total after returns", "İadelerden sonra toplam", "Qaytarishlardan keyingi jami")
            : Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
        SummaryRows.ItemsSource = payments;
        ShowReturns(entry, ledger, hasReturns);
        FillCardHeader(entry, detail);
        ReceiptCard.IsVisible = true;
    }

    /// <summary>2026-10-20: шапка карточки — те же сведения, что в печатном чеке (SaleReceiptTextBuilder /
    /// CartReceiptTextBuilder): магазин, ИНН и адрес по настройкам чека, «Товарный чек · Кассир»; внизу — клиент и
    /// консультант. Чек нефискальный (решение владельца 16.10) — «Товарный чек», без «Кассовый чек · Приход».</summary>
    private void FillCardHeader(ReceiptHistoryEntry entry, JsonElement detail)
    {
        var prefs = UserPreferences.Instance;
        var store = (prefs.StoreName ?? "").Trim();
        // «MARKET PLUS» — значение по умолчанию, пока владелец не задал название: на экране — название компании.
        if ((store.Length == 0 || store == "MARKET PLUS") && !string.IsNullOrWhiteSpace(CompanyInfoService.LastCompany?.Name))
            store = CompanyInfoService.LastCompany!.Name!.Trim();
        CardStoreName.Text = prefs.ShowStoreName ? store : "";
        CardStoreName.IsVisible = CardStoreName.Text.Length > 0;

        var info = new List<string>();
        if (prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress))
            info.Add(prefs.StoreAddress.Trim());
        if (prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn))
            info.Add(Tr.T("ИНН", "ИНН", "TIN", "VKN", "STIR") + " " + prefs.StoreInn.Trim());
        CardStoreInfo.Text = string.Join(" · ", info);
        CardStoreInfo.IsVisible = info.Count > 0;

        var cashier = entry.Cashier ?? CartDisplayHelper.TryCashierName(detail);
        CardKind.Text = Tr.T("Товарный чек", "Товардык чек", "Sales receipt", "Satış fişi", "Tovar cheki")
                        + (string.IsNullOrWhiteSpace(cashier)
                            ? ""
                            : " · " + Tr.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + cashier.Trim());

        var people = new List<SummaryVm>();
        if ((Str(detail, "client_name") ?? Str(detail, "client_display")) is { } client)
            people.Add(new SummaryVm(Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), client));
        if (Str(detail, "consultant_display") is { } consultant)
            people.Add(new SummaryVm(Tr.T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), consultant));
        PeopleRows.ItemsSource = people;
        CardPeoplePanel.IsVisible = people.Count > 0;
    }

    /// <summary>Строки карточки: скидки (до итога), итог, оплата (после итога). 2026-10-20: способ оплаты — с суммой
    /// («Наличные 209», у смешанной — наличные и безнал отдельно), ниже «Получено» и «Сдача», у долга — остаток.</summary>
    /// <param name="hideCashDetails">2026-10-20 (К6): по чеку были возвраты, а касса продажу до них не видела — «Получено/Сдача»
    /// сервер уже пересчитал (выдуманные цифры), их не показываем.</param>
    private static (List<SummaryVm> Discounts, decimal Total, List<SummaryVm> Payments) BuildSummary(ReceiptHistoryEntry entry, JsonElement detail,
        bool hideCashDetails = false)
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
                MoneyText.Amount((decimal)totals.Subtotal)));
        if (lineDiscounts > 0.005m)
            rows.Add(new SummaryVm(Tr.T("Скидка на позиции", "Позицияларга арзандатуу", "Item discounts", "Ürün indirimleri", "Pozitsiyalarga chegirma"),
                "−" + MoneyText.Amount(lineDiscounts)));
        if (orderDiscount > 0.005m)
            rows.Add(new SummaryVm(Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chekka chegirma"),
                "−" + MoneyText.Amount(orderDiscount)));

        var pay = new List<SummaryVm>();
        var method = (entry.IsLocal ? entry.PaymentMethod : Str(detail, "payment_method") ?? entry.PaymentMethod).Trim().ToLowerInvariant();
        var received = hideCashDetails ? null : entry.IsLocal ? ParseMoney(entry.Local!.CashReceived) : Money(detail, "cash_received");
        // Долг с предоплатой у офлайн-чека — «debt-cash» / «debt-noncash» (см. CartReceiptTextBuilder).
        if (method.StartsWith("debt-", StringComparison.Ordinal))
            method = "debt";
        switch (method)
        {
            case "mixed" or "split":
                if (received is > 0)
                {
                    pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel("cash"), MoneyText.Amount(received.Value)));
                    pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel("cashless"), MoneyText.Amount(Math.Max(0, total - received.Value))));
                }
                else
                {
                    pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel(method), MoneyText.Amount(total)));
                }
                break;
            case "debt":
                pay.Add(new SummaryVm(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), ReceiptHistoryService.PaymentLabel(method)));
                // 2026-10-20, владелец (долг Д5): у долга — «Оплачено» (внесено при продаже).
                if (received is > 0)
                    pay.Add(new SummaryVm(Tr.T("Оплачено", "Төлөндү", "Paid", "Ödenen", "To'landi"), MoneyText.Amount(received.Value)));
                break;
            case "":
                break;
            default:
                pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel(method), MoneyText.Amount(total)));
                if (method == "cash" && received is > 0)
                {
                    pay.Add(new SummaryVm(Tr.T("Получено", "Алынды", "Received", "Alınan", "Qabul qilindi"), MoneyText.Amount(received.Value)));
                    var change = (entry.IsLocal ? null : Money(detail, "change")) ?? Math.Max(0, received.Value - total);
                    pay.Add(new SummaryVm(Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), MoneyText.Amount(change)));
                }
                break;
        }

        if (!entry.IsLocal && method == "debt"
            && (Money(detail, "remaining_debt") ?? Money(detail, "debt_amount")) is { } debt && debt > 0)
            pay.Add(new SummaryVm(Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"), MoneyText.Amount(debt)));
        if (!entry.IsLocal && method == "debt")
            AddDebtDueRows(pay, SaleReturnLedger.Get(entry.Id));

        return (rows, total, pay);
    }

    /// <summary>2026-10-20, владелец (долг Д5): «Вернуть до» или график рассрочки и клиент — из журнала кассы (запись при оплате).</summary>
    private static void AddDebtDueRows(List<SummaryVm> pay, SaleLedgerEntry? ledger)
    {
        if (ledger is null)
            return;
        if (ledger.DebtSchedule.Count > 1)
        {
            var no = 0;
            foreach (var p in ledger.DebtSchedule)
                pay.Add(new SummaryVm(Tr.T($"{++no}-й платёж до ", $"{no}-төлөм ", $"Payment {no} by ", $"{no}. ödeme ", $"{no}-to'lov ")
                                      + p.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), MoneyText.Amount(p.Amount)));
        }
        else if (ledger.DebtDueDate is { } due)
        {
            pay.Add(new SummaryVm(Tr.T("Вернуть до", "Кайтаруу мөөнөтү", "Repay by", "Son ödeme", "Qaytarish muddati"),
                due.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
        }
        if (CartReceiptTextBuilder.MaskPhone(ledger.ClientPhone) is { Length: > 0 } phone)
            pay.Add(new SummaryVm(Tr.T("Телефон клиента", "Кардардын телефону", "Client phone", "Müşteri telefonu", "Mijoz telefoni"), phone));
    }

    /// <summary>2026-10-20 (живой аудит кассы, К6): строки продажи до возвратов (снимок журнала возвратов); у каждой — сколько
    /// уже возвращено (по возвратам этой кассы, иначе по серверу).</summary>
    private static List<LineVm> OriginalLines(SaleLedgerEntry ledger, JsonElement detail)
    {
        var returnedByLine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ledger.Refunds.SelectMany(r => r.Lines))
        {
            if (!string.IsNullOrEmpty(line.LineId))
                returnedByLine[line.LineId] = (returnedByLine.TryGetValue(line.LineId, out var q) ? q : 0) + line.Quantity;
        }
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(detail))
        {
            if (CartDisplayHelper.TryRefundLineId(line) is { } id && !returnedByLine.ContainsKey(id))
            {
                var serverReturned = CartDisplayHelper.LineQuantity(line) - CartDisplayHelper.RefundableQuantity(line);
                if (serverReturned > 1e-6)
                    returnedByLine[id] = serverReturned;
            }
        }

        var result = new List<LineVm>();
        foreach (var line in ledger.Lines)
        {
            var returned = line.LineId is { } id && returnedByLine.TryGetValue(id, out var r) ? Math.Min(r, line.Quantity) : 0;
            result.Add(new LineVm
            {
                Name = line.Name,
                QtyPriceText = $"{line.Quantity.ToString("0.###", Ru)} × {MoneyText.Amount(line.UnitPrice)}",
                TotalText = MoneyText.Amount(line.Total),
                ReturnedText = returned > 1e-6
                    ? Tr.T("Возвращено: ", "Кайтарылды: ", "Returned: ", "İade edildi: ", "Qaytarildi: ") + returned.ToString("0.###", Ru)
                    : "",
            });
        }
        return result;
    }

    /// <summary>2026-10-20 (К6): итог, оплата, «Получено/Сдача» — как при продаже (журнал возвратов), а не как их пересчитал сервер.</summary>
    private static (List<SummaryVm> Discounts, decimal Total, List<SummaryVm> Payments) BuildOriginalSummary(SaleLedgerEntry ledger)
    {
        var rows = new List<SummaryVm>();
        var orderDiscount = ledger.LinesTotal - ledger.Total;
        if (ledger.LinesTotal > 0m && orderDiscount > 0.005m)
        {
            rows.Add(new SummaryVm(Tr.T("Сумма без скидки на чек", "Чекке арзандатуусуз сумма", "Before the receipt discount", "Fiş indirimi öncesi", "Chek chegirmasisiz summa"),
                MoneyText.Amount(ledger.LinesTotal)));
            rows.Add(new SummaryVm(Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chekka chegirma"),
                "−" + MoneyText.Amount(orderDiscount)));
        }

        var pay = new List<SummaryVm>();
        switch (ledger.Method)
        {
            case "cash":
                pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel("cash"), MoneyText.Amount(ledger.Total)));
                if (ledger.Received is > 0m)
                {
                    pay.Add(new SummaryVm(Tr.T("Получено", "Алынды", "Received", "Alınan", "Qabul qilindi"), MoneyText.Amount(ledger.Received.Value)));
                    pay.Add(new SummaryVm(Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"),
                        MoneyText.Amount(ledger.Change ?? Math.Max(0m, ledger.Received.Value - ledger.Total))));
                }
                break;
            case "mixed" or "split":
                pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel("cash"), MoneyText.Amount(ledger.PaidCash)));
                pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel("cashless"), MoneyText.Amount(ledger.PaidNonCash)));
                break;
            case "debt":
                pay.Add(new SummaryVm(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), ReceiptHistoryService.PaymentLabel("debt")));
                if (ledger.PaidCash + ledger.PaidNonCash > 0.005m)
                    pay.Add(new SummaryVm(Tr.T("Оплачено", "Төлөндү", "Paid", "Ödenen", "To'landi"), MoneyText.Amount(ledger.PaidCash + ledger.PaidNonCash)));
                pay.Add(new SummaryVm(Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), MoneyText.Amount(ledger.Debt)));
                AddDebtDueRows(pay, ledger);
                break;
            case "":
                break;
            default:
                pay.Add(new SummaryVm(ReceiptHistoryService.PaymentLabel(ledger.Method), MoneyText.Amount(ledger.Total)));
                break;
        }

        return (rows, ledger.Total, pay);
    }

    private CancellationTokenSource? _returnsCts;

    /// <summary>2026-10-20 (живой аудит кассы, К6): блок «Возвраты» под чеком — когда, что, на сколько и чем вернули. Возвраты этой
    /// кассы — из журнала возвратов (суммы кассы с долей скидки на чек); иначе — список возвратов сервера по чеку.</summary>
    private void ShowReturns(ReceiptHistoryEntry entry, SaleLedgerEntry? ledger, bool hasReturns)
    {
        _returnsCts?.Cancel();
        ReturnsPanel.IsVisible = false;
        ReturnRows.ItemsSource = null;
        if (!hasReturns)
            return;

        if (ledger is { Refunds.Count: > 0 })
        {
            var rows = new List<SummaryVm>();
            foreach (var refund in ledger.Refunds.OrderBy(r => r.At))
            {
                var what = refund.Whole && refund.Lines.Count == 0
                    ? Tr.T("весь чек", "бүт чек", "whole receipt", "tüm fiş", "butun chek")
                    : string.Join(", ", refund.Lines.Select(l => l.Quantity is > 0 and not 1 ? $"{l.Name} × {l.Quantity.ToString("0.###", Ru)}" : l.Name));
                rows.Add(new SummaryVm(refund.At.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + " · " + what,
                    "−" + MoneyText.Amount(refund.Amount)));
                var how = new List<string>();
                if (refund.Cash > 0.005m)
                    how.Add(Tr.T("наличными ", "накталай ", "cash ", "nakit ", "naqd ") + MoneyText.Amount(refund.Cash));
                if (refund.NonCash > 0.005m)
                    how.Add(Tr.T("на карту/перевод ", "картага/которуу ", "card/transfer ", "kart/havale ", "karta/o'tkazma ") + MoneyText.Amount(refund.NonCash));
                if (refund.DebtReduced > 0.005m)
                    how.Add(Tr.T("в счёт долга ", "карыздын эсебинен ", "against the debt ", "borçtan ", "qarz hisobidan ") + MoneyText.Amount(refund.DebtReduced));
                if (how.Count > 0)
                    rows.Add(new SummaryVm("   " + string.Join(" · ", how), ""));
            }
            rows.Add(new SummaryVm(Tr.T("Возвращено всего", "Бардыгы кайтарылды", "Returned in total", "Toplam iade", "Jami qaytarildi"),
                "−" + MoneyText.Amount(ledger.Refunded)));
            ReturnRows.ItemsSource = rows;
            ReturnsPanel.IsVisible = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _returnsCts = cts;
        _ = LoadServerReturnsAsync(entry.Id, cts);
    }

    private async Task LoadServerReturnsAsync(string saleId, CancellationTokenSource cts)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var list = await NurMarketKassa.Services.Api.NurCrmReportsApi.ListReturnsAsync(null, null, saleId: saleId, ct: timeout.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || list is null || !string.Equals(_selected?.Entry.Id, saleId, StringComparison.OrdinalIgnoreCase))
                return;
            var mine = list.Where(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.CreatedAt).ToList();
            if (mine.Count == 0)
                return;
            var rows = mine.Select(r => new SummaryVm(
                    (r.CreatedAt?.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) ?? "—")
                    + (string.IsNullOrWhiteSpace(r.ItemsText)
                        ? (r.IsFull ? " · " + Tr.T("весь чек", "бүт чек", "whole receipt", "tüm fiş", "butun chek") : "")
                        : " · " + r.ItemsText),
                    "−" + MoneyText.Amount(r.Amount)))
                .ToList();
            rows.Add(new SummaryVm(Tr.T("По данным сервера", "Сервердин маалыматы боюнча", "According to the server", "Sunucu verilerine göre", "Server ma'lumotlariga ko'ra"), ""));
            ReturnRows.ItemsSource = rows;
            ReturnsPanel.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: возвраты по чеку {saleId} не загружены: {ex.Message}", "WARNING");
        }
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

    // 2026-10-03, владелец: «нужна кнопка для печати документов из чека». Товарный чек или накладная на A4.
    private void DocReceipt_Click(object? sender, RoutedEventArgs e) => OpenDocument(SaleDocumentHtmlBuilder.Kind.SalesReceipt);

    private void DocWaybill_Click(object? sender, RoutedEventArgs e) => OpenDocument(SaleDocumentHtmlBuilder.Kind.Waybill);

    private void OpenDocument(SaleDocumentHtmlBuilder.Kind kind)
    {
        if (_selected is not { } row || _selectedDetail is not { } detail)
            return;
        try
        {
            var total = row.Entry.IsLocal ? row.Entry.Total : SalesWindow.ReadSaleTotal(detail) ?? row.Entry.Total;
            SaleDocumentHtmlBuilder.OpenForPrint(detail, row.Entry.ReceiptNumber, total, kind);
            ShowAction(Tr.T("Документ открыт в браузере — там же окно печати.", "Документ браузерде ачылды — басып чыгаруу терезеси ошол жерде.",
                "The document opened in the browser with the print dialog.", "Belge tarayıcıda yazdırma penceresiyle açıldı.",
                "Hujjat brauzerda chop etish oynasi bilan ochildi."), ok: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: документ не открыт: {ex}", "PRINTER");
            ShowAction(Tr.T("Документ не открылся: ", "Документ ачылган жок: ", "The document did not open: ", "Belge açılmadı: ", "Hujjat ochilmadi: ") + ex.Message, ok: false);
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

        // 2026-09-29: авария сервера — возврат недоступен (см. таблицу у PosCheckoutService.CheckoutAsync).
        if (OfflineModeHelper.IsServerOutage)
        {
            ShowAction(OfflineModeHelper.ReturnUnavailableInOutage, ok: false);
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
            // 2026-10-05, владелец: на сайте возврат есть и на «Старте» — тариф не проверяем, только право.
            return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.EmployeeReturn);
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

        // 2026-10-20 (живой аудит кассы, К6): после возврата сервер уменьшает итог продажи (001840: 50,00 вместо 75,00) —
        // в списке, как в карточке, сумма продажи (журнал возвратов кассы), статус «Возврат» рядом.
        public string TotalText => ReceiptHistoryService.Money(
            !Entry.IsLocal && SaleReturnLedger.Get(Entry.Id) is { Total: > 0m, Refunds.Count: > 0 } l ? l.Total : Entry.Total);

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
