using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System;

// 2026-11-06: В10 — часть окна возврата (механический разбор ReturnSaleDialog.axaml.cs, 3146 строк): список чеков, открытие чека, строки возврата. Логика не менялась.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private async void RefreshSales_Click(object? sender, RoutedEventArgs e) =>
        await LoadSalesAsync(true).ConfigureAwait(true);

    private async void MoreSales_Click(object? sender, RoutedEventArgs e) =>
        await LoadSalesAsync(false).ConfigureAwait(true);

    /// <summary>2026-10-04: подпись сбоя для журнала аварии (ServerOutageMonitor).</summary>
    private static string ReturnContext => Tr.T("возврат", "кайтаруу", "return", "iade", "qaytarish");

    /// <summary>2026-10-04: сервер не ответил на первую порцию данных окна — авария объявлена,
    /// в окне текст «возврат недоступен».</summary>
    private bool _serverDown;

    /// <summary>2026-10-04: идёт фоновая подгрузка следующей страницы («Ещё»).</summary>
    private bool _loadingMore;

    /// <summary>2026-10-04: «Обновить» во время фоновой подгрузки «Ещё» — её страница уже не к месту.</summary>
    private int _salesLoadGeneration;

    /// <summary>true — список загружен. 2026-10-04, отчёт «офлайн и сбои сервера»: первая страница
    /// ждёт сервер не дольше ServerAnswerWait.FirstPortionBudget (5 с с последнего ответа; раньше —
    /// до 55 с, пока касса ещё «Онлайн», а сервер уже молчит), сбой — авария и понятный текст в окне.
    /// Следующие страницы («Ещё») грузятся фоном: окно не блокируется, кнопки возврата доступны.</summary>
    /// <summary>2026-10-29: сервер недоступен — это окно закрывается, открывается «Возврат без связи» (чеки из памяти кассы). Раньше здесь был тупик с текстом ошибки.</summary>
    private void SwitchToOfflineReturn()
    {
        var owner = Owner as Window;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Close();
            _ = OfflineOperationsWindow.OpenAsync(owner, OfflineOperationsWindow.Mode.Return);
        });
    }

    private async Task<bool> LoadSalesAsync(bool reset)
    {
        if (!reset && _loadingMore)
            return false;

        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        _serverDown = false;

        if (OfflineModeHelper.IsServerOutage)
        {
            // Касса уже знает, что сервер лежит, — не ждём его ещё раз.
            _serverDown = true;
            SwitchToOfflineReturn();
            return false;
        }

        if (reset)
        {
            IsBusy = true;
            _salesLoadGeneration++;
            _salesPage = 1;
            Sales.Clear();
            _salesSeenIds.Clear();
        }
        else
        {
            _loadingMore = true;
            MoreSalesButton.IsEnabled = false;
            _salesPage++;
        }

        var page = reset ? 1 : _salesPage;
        var generation = _salesLoadGeneration;

        try
        {
            // 2026-11-10 (фильтры возврата): период — запросом к серверу (date_from / date_to, «до» не включает свой день), страница крупнее;
            // режим клиента (карточка клиента, «Долги клиентов») — продажи этого клиента одним списком.
            var clientId = InitialClientId?.Trim();
            var range = string.IsNullOrEmpty(clientId) ? ListFilter.PeriodRange(DateTime.Now) : null;
            var pageSize = range is null ? SalesPageSize : 100;
            if (!string.IsNullOrEmpty(clientId) && !reset)
                return false;
            var jsonElementList = !string.IsNullOrEmpty(clientId)
                ? await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                        ct => App.SalesApi.PosSalesByClientAsync(clientId, maxPages: 10, ct))
                    .ConfigureAwait(true)
                : reset
                ? await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                        ct => App.SalesApi.PosSalesListAsync(page, pageSize, App.PosCashboxId, ct, range?.From, range?.ToExclusive))
                    .ConfigureAwait(true)
                : await App.SalesApi.PosSalesListAsync(page, pageSize, App.PosCashboxId, default, range?.From, range?.ToExclusive)
                    .ConfigureAwait(true);

            // Пока шла фоновая страница, кассир нажал «Обновить» — список уже другой.
            if (!reset && generation != _salesLoadGeneration)
                return false;

            var hasCustomerId = jsonElementList.Any(el => el.TryGetProperty("customer_id", out _));
            var added = 0;
            var newIds = new List<string>();

            foreach (var row in jsonElementList)
            {
                if (!string.IsNullOrWhiteSpace(_currentUserId) && hasCustomerId &&
                    row.TryGetProperty("customer_id", out var cid) &&
                    !string.Equals(cid.GetString(), _currentUserId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var saleId = PosSaleRowFormatter.TrySaleId(row);
                if (string.IsNullOrEmpty(saleId) || !_salesSeenIds.Add(saleId))
                    continue;
                // 2026-11-10: «new» — открытая корзина, не чек (в режиме клиента сервер отдаёт и такие).
                if (string.Equals(SaleStatus(row), "new", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 2026-09-28 (BE-08): постоянный номер продажи с сервера — тот же, что в «Истории чеков», в «Продажах» и на печатном чеке.
                // 2026-11-10: разбор строки — ListItemFromRow (общий; плюс поля для фильтров: оплата, кассир, клиент, первый товар, return_status).
                Sales.Add(ListItemFromRow(row, saleId));
                newIds.Add(saleId);
                added++;
            }

            AddLocalLines(newIds); // 2026-11-10: состав чеков этой кассы — из локальной истории, без запросов (поиск по товару)
            _filterBar?.SetCashiers(Sales.Select(s => s.Cashier ?? ""));
            ApplySalesFilter();
            if (_productQuery is { IsEmpty: false })
                StartDeepProductSearch();

            if (reset && Sales.Count == 0)
                ShowErr(Tr.T("Список продаж пуст или API не вернул данные. Попробуйте «Обновить» или введите ID продажи вручную.",
                    "Сатуулар тизмеси бош же API маалымат кайтарган жок. «Жаңылоо» баскычын басыңыз же сатуунун ID’син өзүңүз киргизиңиз.",
                    "The sales list is empty or the API returned no data. Try “Refresh” or enter the sale ID manually.",
                    "Satış listesi boş veya API veri döndürmedi. «Yenile»yi deneyin veya satış ID'sini elle girin.",
                    "Sotuvlar ro'yxati bo'sh yoki API ma'lumot qaytarmadi. «Yangilash»ni bosib ko'ring yoki sotuv ID'sini qo'lda kiriting."));
            else if (!reset && added == 0)
            {
                // 2026-10-22 (модалки, пакет 1): сообщение — строкой в окне, а не всплывающим окном.
                ShowInputErr(Tr.T("Больше чеков нет — загружены все.", "Башка чек жок — баары жүктөлдү.", "No more receipts — all are loaded.", "Başka fiş yok — hepsi yüklendi.", "Boshqa chek yo'q — hammasi yuklandi."));
                _salesPage = Math.Max(1, _salesPage - 1);
            }

            return true;
        }
        catch (ServerNotAnsweringException ex)
        {
            // 2026-10-04: первая страница — сервер не ответил за 5 с или ответил сбоем: авария уже
            // объявлена (ServerAnswerWait), кассиру — что делать, а не «Превышено время ожидания» через 55 с.
            _serverDown = !ex.Throttled;
            if (ex.Throttled)
                ShowErr(ServerAnswerWait.ThrottledMessage);
            else
                SwitchToOfflineReturn();
        }
        catch (ApiException ex)
        {
            if (reset)
                ShowErr(ex.Message);
            else
                PosMessageBox.Show(this, ex.Message, Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (HttpRequestException ex)
        {
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message;
            if (reset)
                ShowErr(msg);
            else
                PosMessageBox.Show(this, msg, Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (TaskCanceledException)
        {
            if (reset)
                ShowErr(Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."));
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        finally
        {
            if (reset)
            {
                IsBusy = false;
            }
            else
            {
                _loadingMore = false;
                MoreSalesButton.IsEnabled = true;
            }
        }

        return false;
    }

    private void ApplySalesFilter()
    {
        FilteredSales.Clear();
        var position = 0;
        // Поиск, кроме внутреннего SaleId (серверный GUID, не показывается пользователю),
        // должен уметь находить чек и по видимому "№N" — это то, что реально видно в списке.
        var trimmedFilter = (_searchFilter ?? "").Trim().TrimStart('№', '#');
        // 2026-09-28: «№1115», «1115» и «001115» — один и тот же постоянный номер чека.
        var filterNumber = NurCrmReportsApi.ParseSaleNumber(_searchFilter);
        var now = DateTime.Now;
        foreach (var item in Sales)
        {
            position++;
            var matches = string.IsNullOrWhiteSpace(_searchFilter)
                || item.SaleId.Contains(_searchFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.ReceiptNumber) &&
                    item.ReceiptNumber.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase)) // 2026-10-29: «№1115» и «#1115» находят чек (раньше искали с символом №)
                || (filterNumber is { } wanted && NurCrmReportsApi.ParseSaleNumber(item.ReceiptNumber) == wanted)
                || (string.IsNullOrWhiteSpace(item.ReceiptNumber) &&
                    string.Equals(trimmedFilter, position.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            // 2026-11-10: фильтры (период, сумма, оплата, статус, кассир, клиент) и поиск по товару — все вместе.
            string? hit = null;
            if (matches && !PassesListFilters(item, now, out hit))
                matches = false;
            if (matches)
                FilteredSales.Add(new ReturnSaleGridRow(item, position) { HitText = hit });
        }
        UpdateFilterCount();
    }

    private static bool TryGetDateTime(JsonElement element, string propertyName, out DateTime result)
    {
        result = DateTime.MinValue;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String)
            return DateTime.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

        return false;
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal result)
    {
        result = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.Number)
        {
            result = prop.GetDecimal();
            return true;
        }

        return prop.ValueKind == JsonValueKind.String &&
               decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private async void SelectSale_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-21 (ревью денег, мелочь 4): во время возврата другой чек не открываем (см. SalesDataGrid_DoubleTapped).
        if (IsBusy || _operationInProgress || sender is not Button { Tag: string saleId } || string.IsNullOrWhiteSpace(saleId))
            return;

        // 2026-10-21 (пакет 6, п. 35): строка выбранного чека выделяется в списке (было — слева ничего не менялось).
        SelectRowInGrid(saleId.Trim());
        await OpenSaleByIdAsync(saleId.Trim()).ConfigureAwait(true);
    }

    /// <summary>2026-10-21 (пакет 6, п. 35): выделить в таблице строку чека (после «Выбрать» и после перечитывания списка).</summary>
    private void SelectRowInGrid(string? saleId)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return;
        var row = FilteredSales.FirstOrDefault(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;
        SalesDataGrid.SelectedItem = row;
        SalesDataGrid.ScrollIntoView(row, null);
    }

    /// <summary>2026-10-21 (пакет 6, п. 35): status продажи сервера — для пометки «возвращён» в списке.</summary>
    private static string? SaleStatus(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()?.Trim().ToLowerInvariant()
            : null;

    /// <summary>2026-10-21 (пакет 6, п. 35): окно возврата — на месте и по размеру окна кассы, если касса не развёрнута
    /// (было: касса 1296 px, а возврат раскрывался на весь экран 1920). Развёрнутая касса — как раньше, на весь экран.</summary>
    public void FitToOwnerWindow(Window owner)
    {
        if (OperatingSystem.IsAndroid() || owner.WindowState is WindowState.Maximized or WindowState.FullScreen
            || owner.Bounds.Width < MinWidth || owner.Bounds.Height < MinHeight)
            return;
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = owner.PointToScreen(new Avalonia.Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    private async void LoadById_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-21 (ревью денег, мелочь 4): во время возврата другой чек не открываем.
        if (_operationInProgress)
            return;
        var raw = (SaleIdBox.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            ShowErr(Tr.T("Введите ID продажи.", "Сатуунун ID'син киргизиңиз.", "Enter the sale ID.", "Satış ID'sini girin.", "Sotuv ID'sini kiriting."));
            return;
        }

        // Пользователь видит в списке только "№N" (позиционный номер, не серверный GUID),
        // поэтому если он ввёл именно его — сначала пробуем найти по позиции в загруженном
        // списке, и только если не нашли — отправляем введённый текст как есть (вдруг это
        // настоящий ID, скопированный откуда-то ещё).
        //
        // 2026-09-28 (BE-08): у продажи на сервере теперь постоянный номер. Введённый номер ищем
        // сначала среди загруженных чеков, затем на сервере (?number=) — чек мог быть пробит давно
        // и в первые страницы списка не попасть. Номер по месту в списке — только если сервер
        // номеров не присылает (старый сервер).
        if (NurCrmReportsApi.ParseSaleNumber(raw) is { } saleNumber)
        {
            var loaded = Sales.FirstOrDefault(s => NurCrmReportsApi.ParseSaleNumber(s.ReceiptNumber) == saleNumber);
            if (loaded != null)
            {
                await OpenSaleByIdAsync(loaded.SaleId).ConfigureAwait(true);
                return;
            }

            IsBusy = true;
            JsonElement? found;
            try
            {
                // 2026-10-04: не дольше 5 с без ответа сервера (раньше — до 55 с), сбой — авария и текст.
                found = await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                    ct => NurCrmReportsApi.FindSaleByNumberAsync(saleNumber, ct)).ConfigureAwait(true);
            }
            catch (ServerNotAnsweringException ex)
            {
                ShowErr(ex.Throttled ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.ReturnUnavailableInOutage);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            if (found is { } foundSale && PosSaleRowFormatter.TrySaleId(foundSale) is { Length: > 0 } foundId)
            {
                AddFoundSale(foundSale, foundId);
                await OpenSaleByIdAsync(foundId).ConfigureAwait(true);
                return;
            }

            if (Sales.Any(s => !string.IsNullOrWhiteSpace(s.ReceiptNumber)))
            {
                ShowErr(Tr.T($"Чек №{saleNumber} не найден.", $"№{saleNumber} чек табылган жок.",
                    $"Receipt No. {saleNumber} was not found.", $"{saleNumber} numaralı fiş bulunamadı.",
                    $"№{saleNumber} chek topilmadi."));
                return;
            }
        }

        var trimmed = raw.TrimStart('№', '#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && position >= 1 && position <= Sales.Count)
        {
            await OpenSaleByIdAsync(Sales[position - 1].SaleId).ConfigureAwait(true);
            return;
        }

        await OpenSaleByIdAsync(raw).ConfigureAwait(true);
    }

    /// <summary>Чек, найденный на сервере по номеру (2026-09-28, BE-08), — в список, если его там
    /// ещё нет: чтобы он был виден и выбран, как любой другой.</summary>
    private void AddFoundSale(JsonElement row, string saleId)
    {
        if (!_salesSeenIds.Add(saleId))
            return;

        _ = TryGetDateTime(row, "created_at", out var saleDate);
        _ = TryGetDecimal(row, "total", out var totalAmount);
        Sales.Insert(0, new ReturnSaleListItemVm
        {
            SaleId = saleId,
            SaleDate = saleDate,
            TotalAmount = totalAmount,
            Summary = PosSaleRowFormatter.SummaryLine(row),
            ReceiptNumber = SalesWindow.TryReceiptNumber(row),
            Status = SaleStatus(row),
        });
        ApplySalesFilter();
    }

    private CancellationTokenSource? _numberLookupCts;

    /// <summary>Строка поиска: номер, которого нет среди загруженных чеков, ищем на сервере
    /// (?number=, 2026-09-28) — с паузой, чтобы не спрашивать на каждую набранную цифру.</summary>
    private async Task LookupSaleByNumberAsync(long saleNumber, CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct).ConfigureAwait(true);
            var found = await NurCrmReportsApi.FindSaleByNumberAsync(saleNumber, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || found is not { } sale
                || PosSaleRowFormatter.TrySaleId(sale) is not { Length: > 0 } id)
                return;
            AddFoundSale(sale, id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: поиск чека №{saleNumber} на сервере не удался: {ex.Message}", "SALES");
        }
    }

    private async Task OpenSaleByIdAsync(string saleId)
    {
        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        if (OfflineModeHelper.IsServerOutage)
        {
            ShowErr(OfflineModeHelper.ReturnUnavailableInOutage);
            return;
        }

        IsBusy = true;
        try
        {
            // 2026-10-04: не дольше 5 с без ответа сервера (раньше — до 55 с), сбой — авария и текст.
            var sale = await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                ct => App.SalesApi.PosSaleGetAsync(saleId, ct)).ConfigureAwait(true);
            _currentSaleId = saleId;
            // Чека нет в загруженном списке (открыт из «Истории чеков» или по ID) — постоянный
            // номер берём из самой продажи (2026-09-28, BE-08): он же печатается на чеке возврата.
            _currentReceiptNumber = Sales.FirstOrDefault(x => x.SaleId == saleId)?.ReceiptNumber
                                    ?? SalesWindow.TryReceiptNumber(sale);
            RememberSaleLines(saleId, sale); // 2026-11-10: состав открытого чека — и для поиска по товару
            _preselectOnFill = true;         // 2026-11-10: при поиске по товару найденная позиция отмечается (количество 1)
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
            if (Lines.Count == 0)
                ShowErr(Tr.T("В ответе сервера нет позиций с идентификатором строки для возврата.",
                    "Сервердин жообунда кайтаруу үчүн сап идентификатору бар позициялар жок.",
                    "The server response has no items with a line ID that can be returned.",
                    "Sunucu yanıtında iade için satır kimliği olan kalem yok.",
                    "Server javobida qaytarish uchun qator identifikatoriga ega pozitsiyalar yo'q."));
            else if (!Lines.Any(x => x.CanReturn))
                ShowErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05): чек уже возвращён целиком — сразу при открытии, а не после ввода причины
        }
        catch (ServerNotAnsweringException ex)
        {
            ShowErr(ex.Throttled ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.ReturnUnavailableInOutage);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (ApiException ex)
        {
            ShowErr(ex.Message);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (HttpRequestException ex)
        {
            ShowErr(string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (TaskCanceledException)
        {
            ShowErr(Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."));
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>2026-10-20 (К1–К3): выбранная продажа, как её заплатили, и возвраты по ней с этой кассы (null — касса
    /// чек до возвратов не видела: суммы — по серверу, как раньше).</summary>
    private SaleLedgerEntry? _ledger;

    /// <summary>2026-10-27 (аудит кассы, В-3): у чека нет записи кассы, но часть позиций уже возвращали (на сайте/другой кассе) — сумма
    /// возврата считается по данным сервера, кассиру показываем оценку «возвращено ранее».</summary>
    private bool _noLedgerTouched;
    private decimal _noLedgerPriorReturned;

    /// <summary>2026-10-20 (К2, К3): payment_method продажи с сервера — если касса чек до возвратов не видела.</summary>
    private string? _currentSaleMethod;

    /// <summary>2026-10-21 (ревью денег, мелочь 4): payment_method каждого открытого чека — расчёт возврата берёт способ
    /// оплаты своего чека, даже если на экране уже другой.</summary>
    private readonly Dictionary<string, string?> _saleMethods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-21 (ревью денег, п. 2): текущий остаток долга чека «в долг» с сервера (remaining_debt сделки),
    /// запрошенный при открытии чека — до возврата. null в результате — остаток не получен. Берётся одним возвратом.</summary>
    private readonly Dictionary<string, Task<decimal?>> _serverDebtLeft = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-11-01 (code-review, Ж-01): имя клиента чека «в долг» (чек → имя) — по нему считаются оплаты долга, записанные на этой кассе.</summary>
    private readonly Dictionary<string, string> _saleClientNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-11-01 (Ж-08): «к выдаче», показанное кассиру в подтверждении до отправки (чек → сумма). Итоговое окно сверяет с ним фактическую выдачу.</summary>
    private readonly Dictionary<string, decimal?> _expectedPayouts = new(StringComparer.OrdinalIgnoreCase); // null — «сумму определит сервер» (подпись кнопки без суммы)

    /// <summary>2026-10-20 (К8): id строки → единиц остатка каталога в одной единице строки.</summary>
    private readonly Dictionary<string, double> _stockUnitsPerQty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-20 (К1): у продажи ещё нет возвратов — запоминаем её строки и итог (SaleReturnLedger); иначе — что
    /// касса запомнила раньше (при оплате или первом открытии).</summary>
    private SaleLedgerEntry? RememberSaleForReturns(JsonElement sale)
    {
        if (string.IsNullOrWhiteSpace(_currentSaleId) || sale.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            var items = CartDisplayHelper.EnumerateSaleLineItems(sale).ToList();
            var untouched = items.Count > 0
                && !LinesReturnedSince(sale, new Dictionary<string, double>())
                && items.All(it => !CartDisplayHelper.LineLooksFullyReturned(it)
                                   && CartDisplayHelper.RefundableQuantity(it) >= CartDisplayHelper.LineQuantity(it) - 1e-6);
            var known = SaleReturnLedger.Get(_currentSaleId);
            if (!untouched || _currentSaleTotal is not { } total || known is { Refunds.Count: > 0 })
                return known;

            var lines = new List<SaleLedgerLine>();
            foreach (var it in items)
            {
                decimal.TryParse(CartDisplayHelper.LineTotal(it), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal);
                lines.Add(new SaleLedgerLine
                {
                    LineId = ReturnLineCheck.SaleItemId(it), // 2026-11-11: sale_item_id = id строки чека (контракт Умара)
                    Name = CartDisplayHelper.ItemName(it),
                    Quantity = CartDisplayHelper.LineQuantity(it),
                    UnitPrice = (decimal)CartDisplayHelper.UnitPrice(it),
                    Total = lineTotal,
                });
            }

            var method = sale.TryGetProperty("payment_method", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetString() : null;
            decimal? received = TryGetDecimal(sale, "cash_received", out var r) ? r : null;
            decimal? change = TryGetDecimal(sale, "change", out var c) ? c : null;
            return SaleReturnLedger.RememberFromSale(_currentSaleId, _currentReceiptNumber, total, lines.Sum(l => l.Total), lines,
                method, received, change, _currentSaleDate is { } d ? new DateTimeOffset(d) : null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: чек {_currentSaleId} не запомнен для расчёта суммы возврата: {ex.Message}", "WARNING");
            return SaleReturnLedger.Get(_currentSaleId);
        }
    }

    /// <summary>2026-10-21 (ревью денег, п. 2): журнал кассы знает возвраты, но не оплаты долга — клиент мог погасить долг
    /// по этому чеку (в кассе, у владельца, на соседней кассе). Остаток долга по сделке — с сервера, запросом при открытии
    /// чека (до возврата), чтобы возврат не уменьшал долг, которого уже нет.</summary>
    private void StartServerDebtLeftLookup(JsonElement sale)
    {
        if (string.IsNullOrWhiteSpace(_currentSaleId) || sale.ValueKind != JsonValueKind.Object)
            return;
        var saleId = _currentSaleId;
        // 2026-11-01 (сверка контракта с NBS, п. 5): чек возвращён целиком — остаток долга по нему 0 (сервер оставляет старое число), «долг по чеку» не берём.
        if (CartDisplayHelper.IsSaleFullyReturned(sale))
        {
            _serverDebtLeft[saleId] = Task.FromResult<decimal?>(0m);
            return;
        }
        var method = (_ledger?.Method is { Length: > 0 } known ? known : _currentSaleMethod ?? "").Trim().ToLowerInvariant();
        if (method != "debt" && _ledger is not { Debt: > 0.005m })
        {
            _serverDebtLeft.Remove(saleId);
            return;
        }

        string? IdOf(string key) => sale.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
        var clientId = IdOf("client");
        var dealId = IdOf("deal_id");
        // Сделки нет — оплат через сделку не было, остаток самой продажи точный (как в PayDebtDialog).
        decimal? saleOwn = TryGetDecimal(sale, "debt_remaining", out var dr) ? dr
            : TryGetDecimal(sale, "remaining_debt", out var rd) ? rd
            : TryGetDecimal(sale, "debt_amount", out var da) ? da
            : null;
        _saleClientNames[saleId] = IdOf("client_name") ?? IdOf("client_display") ?? IdOf("debt_client_name") ?? ""; // 2026-11-01: имя клиента — ключ локальных оплат долга (ShiftEventsStore.DebtPaidByClientName)
        _serverDebtLeft[saleId] = FetchServerDebtLeftAsync(saleId, clientId, dealId, saleOwn);
    }

    private static async Task<decimal?> FetchServerDebtLeftAsync(string saleId, string? clientId, string? dealId, decimal? saleOwn)
    {
        if (string.IsNullOrWhiteSpace(dealId))
            return saleOwn is { } own ? Math.Max(0m, own) : null;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            PosLogger.Log($"Возврат по продаже {saleId}: остаток долга не получен (у продажи нет клиента).", "WARNING");
            return null;
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var deal = await App.SalesApi.ClientDealGetAsync(clientId, dealId, cts.Token).ConfigureAwait(false);
            if (deal.ValueKind == JsonValueKind.Object && TryGetDecimal(deal, "remaining_debt", out var remaining))
                return Math.Max(0m, remaining);
            PosLogger.Log($"Возврат по продаже {saleId}: в сделке нет remaining_debt — остаток долга неизвестен.", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: остаток долга с сервера не получен ({ex.GetType().Name}).", "WARNING");
        }

        return null;
    }

    private void FillLinesFromSale(JsonElement sale)
    {
        Lines.Clear();
        _stockUnitsPerQty.Clear();
        // 2026-11-06 (Б-2): снимок чека «до возврата»; чек прочитан заново — снимается и запрет повтора после неясного ответа.
        _saleSnapshot = sale.ValueKind == JsonValueKind.Object ? ReturnSnapshot.From(sale) : null;
        if (string.Equals(_returnUnknownSaleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            _returnUnknownSaleId = null;
        _currentSaleTotal = sale.ValueKind == JsonValueKind.Object
                            && sale.TryGetProperty("total", out var saleTotalEl)
                            && decimal.TryParse(saleTotalEl.ValueKind == JsonValueKind.String ? saleTotalEl.GetString() : saleTotalEl.GetRawText(),
                                NumberStyles.Number, CultureInfo.InvariantCulture, out var saleTotalValue)
            ? saleTotalValue
            : null;
        _currentSaleRefundable = sale.ValueKind == JsonValueKind.Object
                                 && sale.TryGetProperty("refundable_total", out var refundableEl)
                                 && refundableEl.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                 && decimal.TryParse(refundableEl.ValueKind == JsonValueKind.String ? refundableEl.GetString() : refundableEl.GetRawText(),
                                     NumberStyles.Number, CultureInfo.InvariantCulture, out var refundableValue)
            ? refundableValue
            : null;
        // 2026-10-06 (О-31): дата чека — для срока обмена и возврата.
        _currentSaleDate = sale.ValueKind == JsonValueKind.Object
                           && (TryGetDateTime(sale, "created_at", out var soldAt) || TryGetDateTime(sale, "paid_at", out soldAt))
            ? soldAt
            : Sales.FirstOrDefault(x => string.Equals(x.SaleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))?.SaleDate is { } listed && listed > DateTime.MinValue
                ? listed
                : null;
        // 2026-10-20 (живой аудит кассы, К1): сервер считает возврат строки без доли скидки на чек и после первого
        // частичного возврата убирает скидку из продажи (001837: 80 − 5 = 75, вернул 30 + 50 = 80). Чек без возвратов
        // запоминаем как есть (SaleReturnLedger): итог, сумма строк до скидки на чек, строки с id; сумма к возврату
        // строки = её доля с учётом скидки на чек, все возвраты чека вместе — не больше оплаченного.
        _currentSaleMethod = sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("payment_method", out var payMethod)
            && payMethod.ValueKind == JsonValueKind.String ? payMethod.GetString() : null;
        _ledger = RememberSaleForReturns(sale);
        if (!string.IsNullOrWhiteSpace(_currentSaleId))
            _saleMethods[_currentSaleId] = _currentSaleMethod;
        StartServerDebtLeftLookup(sale);
        var ledgerLines = _ledger?.Lines
            .Where(l => !string.IsNullOrEmpty(l.LineId))
            .GroupBy(l => l.LineId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // 2026-10-27 (аудит кассы, В-3): своей записи о чеке нет (первый частичный возврат сделан на сайте/другой кассе) — раньше доля
        // скидки на чек была 1, и возврат шёл по полной цене строки: сумма возвратов могла превысить заплаченное. Теперь доля — из итога
        // и суммы строк, как их отдаёт сервер (итог = строки − скидка на чек), а ранее возвращённое оценивается для подсказки.
        _noLedgerPriorReturned = 0m;
        _noLedgerTouched = false;
        // 2026-11-01 (ответ Умара: в чеке есть return_status): чек возвращён целиком — строки не возвращаются повторно, «Вернуть весь чек» неактивна.
        var saleFullyReturned = sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("return_status", out var saleReturnStatus)
            && saleReturnStatus.ValueKind == JsonValueKind.String
            && CartDisplayHelper.IsFullyReturnedStatus(saleReturnStatus.GetString())
            && CartDisplayHelper.EnumerateSaleLineItems(sale).All(it => CartDisplayHelper.RefundableQuantity(it) <= 1e-6); // статус — только вместе с количеством (контрольное ревью)
        var factor = _ledger?.Factor ?? 1m;
        if (_ledger is null)
        {
            var linesSum = 0m;
            foreach (var each in CartDisplayHelper.EnumerateSaleLineItems(sale))
            {
                if (decimal.TryParse(CartDisplayHelper.LineTotal(each), NumberStyles.Any, CultureInfo.InvariantCulture, out var eachTotal))
                    linesSum += eachTotal;
            }

            factor = ReturnShareEstimator.Factor(_currentSaleTotal, linesSum);
        }
        var preferRowId = PreferRowId(_currentSaleId);
        foreach (var lineItem in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            // 2026-11-10 («Есть позиции не из этого чека…»): id строки для sale_item_id — явное поле sale_item_id, иначе прежний выбор; после отказа сервера — поле id строки.
            var lineId = ReturnLineCheck.SaleItemId(lineItem, preferRowId);
            var legacyLineId = CartDisplayHelper.TryRefundLineId(lineItem);
            if (string.IsNullOrEmpty(lineId))
                continue;

            var title = CartDisplayHelper.ItemName(lineItem);
            var subLine = CartDisplayHelper.QuantityPriceLine(lineItem);
            var lineTotal = CartDisplayHelper.LineTotal(lineItem);
            var originalQty = CartDisplayHelper.LineQuantity(lineItem);
            var qty = CartDisplayHelper.RefundableQuantity(lineItem);
            // 2026-10-29 (ревью, В-5): возвраты, оформленные без связи и ещё не дошедшие до сервера, вычитаются из «можно вернуть» — сервер их пока не знает.
            qty = Math.Max(0, qty - NurMarketKassa.Services.OfflineOperationsStore.UnsentRefundQuantity(_currentSaleId, CartDisplayHelper.TryProductId(lineItem), CartDisplayHelper.ItemName(lineItem)));
            var canReturn = qty > 0 && !CartDisplayHelper.LineLooksFullyReturned(lineItem) && !saleFullyReturned;

            // Сумма к возврату — пропорционально остатку к возврату, а не полная сумма строки:
            // из 5×100 при уже возвращённых 2 к возврату идут 300, а не 500.
            var refundSum = lineTotal;
            decimal.TryParse(lineTotal, NumberStyles.Any, CultureInfo.InvariantCulture, out var refundSumValue);
            var refundPerUnit = 0m; // 2026-10-27 (Д-5): сумма возврата за единицу строки без округления — для частичного возврата по количеству
            // 2026-10-20 (К1): строка — как в продаже до возвратов (снимок), и её доля скидки на весь чек.
            if (ledgerLines is not null
                && (ledgerLines.TryGetValue(lineId, out var was) || (legacyLineId is not null && ledgerLines.TryGetValue(legacyLineId, out was)))
                && was.Quantity > 0)
            {
                originalQty = was.Quantity;
                lineTotal = CartDisplayHelper.FormatMoney((double)was.Total);
            }
            if (originalQty > 0 &&
                decimal.TryParse(lineTotal, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotalValue))
            {
                refundSumValue = Math.Round(lineTotalValue * (decimal)qty / (decimal)originalQty * factor, 2, MidpointRounding.AwayFromZero);
                refundPerUnit = lineTotalValue / (decimal)originalQty * factor;
                refundSum = CartDisplayHelper.FormatMoney((double)refundSumValue);
                if (_ledger is null && qty < originalQty - 1e-6)
                {
                    _noLedgerTouched = true;
                    _noLedgerPriorReturned += ReturnShareEstimator.PriorReturned(lineTotalValue, originalQty, qty, factor);
                }
            }

            // 2026-11-05 (аудит №3, Д3-09): после частичного возврата в строке видно, сколько уже вернули («возвращено 1 из 2»).
            if (canReturn && qty > 0 && originalQty - qty > 1e-6)
            {
                var doneText = (originalQty - qty).ToString("0.###", CultureInfo.CurrentCulture);
                var ofText = originalQty.ToString("0.###", CultureInfo.CurrentCulture);
                subLine += " · " + Tr.T($"возвращено {doneText} из {ofText}", $"кайтарылды {doneText} / {ofText}", $"returned {doneText} of {ofText}",
                    $"iade edilen {doneText} / {ofText}", $"qaytarildi {doneText} / {ofText}");
            }

            string? refundReason = null;
            if (lineItem.TryGetProperty("refund_reason", out var rr) && rr.ValueKind == JsonValueKind.String)
                refundReason = rr.GetString();
            else if (lineItem.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                refundReason = r.GetString();

            // 2026-10-20 (К8): сколько единиц остатка каталога в одной единице строки (продажа штуками из упаковки).
            var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, CartDisplayHelper.TryProductId(lineItem), StringComparison.OrdinalIgnoreCase));
            var lineQtyNow = CartDisplayHelper.LineQuantity(lineItem);
            _stockUnitsPerQty[lineId] = lineQtyNow > 0 ? CartDisplayHelper.LineQuantityInStockUnits(lineItem, tile) / lineQtyNow : 1.0;

            Lines.Add(new ReturnSaleLineVm
            {
                LineId = lineId,
                ProductId = CartDisplayHelper.TryProductId(lineItem),
                Quantity = qty,
                OriginalQuantity = originalQty,
                RefundPerUnit = refundPerUnit,
                // 2026-10-27 (Д-5): весовой/дробный товар — количество к возврату дробное; штучный — целое.
                AllowFractionalQuantity = tile is { IsWeighted: true } || tile is { MustWeigh: true }
                    || Math.Abs(qty - Math.Round(qty)) > 1e-6 || Math.Abs(originalQty - Math.Round(originalQty)) > 1e-6,
                Title = title,
                SubLine = subLine,
                // 2026-10-19, К1: сумма к возврату — «1 234,50 сом» (MoneyText), крупно справа в строке; сама сумма прежняя.
                LineSumText = decimal.TryParse(refundSum, NumberStyles.Any, CultureInfo.InvariantCulture, out var refundShown)
                    ? MoneyText.Som(refundShown)
                    : Tr.T($"{refundSum} сом", $"{refundSum} сом", $"{refundSum} som", $"{refundSum} som", $"{refundSum} so'm"),
                RefundSum = refundSumValue,
                CanReturn = canReturn,
                RefundReason = refundReason,
                // 2026-11-10 (поиск по товару): найденная позиция подсвечена.
                IsSearchHit = _productQuery is { IsEmpty: false } pq && pq.MatchesLine(title, CartDisplayHelper.TryProductId(lineItem)),
            });
        }
        ApplyProductPreselect();
        UpdateReturnDocs(sale); // 2026-11-10: прошлые возвраты чека — «Удалить»/«История» (только при признаке сервера pos_return_edit_24h)
    }
}
