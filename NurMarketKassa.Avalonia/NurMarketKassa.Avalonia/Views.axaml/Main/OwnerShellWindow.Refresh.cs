using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15 — часть класса OwnerShellWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — OwnerShellWindow.axaml.cs
public partial class OwnerShellWindow
{
    private (DateTime From, DateTime To) CurrentRange()
    {
        var today = ServerClock.Today;
        return _period switch
        {
            // 2026-10-20: аудит владельца — «Неделя» была с понедельника (20 447 при 97 242 за 7 дней в других окнах).
            // Теперь общее правило PeriodRange: неделя = 7 дней включая сегодня, месяц — с 1-го числа.
            "week" => (PeriodRange.WeekFrom(today), today),
            "month" => (PeriodRange.MonthFrom(today), today),
            "custom" => (_customFrom.Date, _customTo.Date),
            _ => (today, today),
        };
    }

    /// <summary>С чем сравнивать: вчера; те же дни прошлой недели; те же числа прошлого месяца.</summary>
    private (DateTime From, DateTime To) PreviousRange(DateTime from, DateTime to)
    {
        var days = (to - from).Days;
        switch (_period)
        {
            case "week":
                return (from.AddDays(-7), from.AddDays(-7 + days));
            case "month":
            {
                var prevFrom = from.AddMonths(-1);
                var lastDay = DateTime.DaysInMonth(prevFrom.Year, prevFrom.Month) - 1;
                return (prevFrom, prevFrom.AddDays(Math.Min(days, lastDay)));
            }
            case "custom":
                // Свои даты — с таким же по длине периодом прямо перед ними.
                return (from.AddDays(-(days + 1)), from.AddDays(-1));
            default:
                return (from.AddDays(-1), to.AddDays(-1));
        }
    }

    /// <summary>Карточки прошлого периода для сравнения: итоги периода сервера (analytics/market/summary/, ТЗ ч.12, п. 3.7),
    /// а если сервер их не отдаёт — карточки полного отчёта, как раньше.</summary>
    private static async Task<JsonElement?> PreviousCardsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            if (await App.SalesApi.MarketSummaryCardsAsync(new[] { (from, to) }, ct).ConfigureAwait(true) is { Count: 1 } cards)
                return cards[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: итоги прошлого периода не получены ({ex.Message}) — полный отчёт.", "WARNING");
        }
        var report = await App.SalesApi.MarketSalesReportAsync(from, to, ct).ConfigureAwait(true);
        return report.ValueKind == JsonValueKind.Object && report.TryGetProperty("cards", out var pc) ? pc.Clone() : null;
    }

    /// <summary>График по дням самого периода (месяц, свои даты), иначе — последние 7 дней.</summary>
    private bool ChartIsPeriod => _period is "month" or "custom";

    private string CompareHint() => _period switch
    {
        "week" => Tr.T("к прошлой неделе", "өткөн жумага карата", "vs last week", "geçen haftaya göre", "o'tgan haftaga nisbatan"),
        "month" => Tr.T("к прошлому месяцу", "өткөн айга карата", "vs last month", "geçen aya göre", "o'tgan oyga nisbatan"),
        "custom" => Tr.T("к предыдущему периоду", "мурунку мезгилге карата", "vs previous period", "önceki döneme göre", "oldingi davrga nisbatan"),
        _ => Tr.T("к вчера", "кечээге карата", "vs yesterday", "düne göre", "kechaga nisbatan"),
    };

    /// <summary>2026-11-06 (В11, шаг 10): убрать из списков «Сводки» серые заглушки строк (Border.skel), показанные до первого ответа сервера.</summary>
    private void RemoveSkeletons()
    {
        foreach (var list in new[] { RecentList, TopList, AttentionList })
        {
            for (var i = list.Children.Count - 1; i >= 0; i--)
                if (list.Children[i].Classes.Contains("skel"))
                    list.Children.RemoveAt(i);
        }
    }

    /// <summary>Сервер недоступен: последние полученные данные (плюс продажи касс по локальной
    /// сети) с пометкой, на какое время они. 2026-09-29: вынесено из RefreshAsync; в аварии
    /// сервера (ServerOutageMonitor) — «Сервер не отвечает · данные на HH:mm» вместо «Нет связи».</summary>
    private void ShowOverviewStale()
    {
        RemoveSkeletons(); // 2026-11-06 (В11, шаг 10): данных не будет — серые заглушки строк не оставляем
        UseBrush(LiveDot, Shape.FillProperty, "BrushWarning");
        _offline = true;
        if (!ApplyLanSales(online: false))
            UpdatedText.Text = NurMarketKassa.Services.ServerOutageMonitor.IsOutage
                ? NurMarketKassa.Services.ServerOutageMonitor.StaleDataNote(_lastSuccess)
                : _lastSuccess is { } at
                    ? Tr.T($"Сервер недоступен · данные на {at:HH:mm}", $"Сервер жеткиликсиз · маалымат {at:HH:mm} боюнча", $"Server unavailable · data as of {at:HH:mm}",
                        $"Sunucu kullanılamıyor · veriler {at:HH:mm} itibarıyla", $"Server mavjud emas · ma'lumotlar {at:HH:mm} holatiga ko'ra")
                    : Tr.T("Сервер недоступен", "Сервер жеткиликсиз", "The server is unavailable", "Sunucu kullanılamıyor", "Server mavjud emas");
        // 2026-11-06 (Б-8): сервер отвечает 401 — это не «недоступен»: честная подпись «Войдите снова».
        if (SessionExpiryMonitor.IsExpired)
            UpdatedText.Text = ExpiredHeaderText();
        // 2026-10-18, ревью О1: каталог локальный — товары на исходе видны и без связи (иначе «Требует внимания» пустой).
        ApplyLowStock();
    }

    /// <summary>2026-10-04, п. 7: «Сводка» видна владельцу — окно активно, не свёрнуто и не закрыто
    /// открытым разделом (тогда сводка спрятана, см. OverviewScroll).</summary>
    private bool IsOverviewShown =>
        IsVisible && IsActive && WindowState != WindowState.Minimized && OverviewScroll.IsVisible;

    /// <summary>Обновление по таймеру и при возврате в окно — только когда «Сводку» видно (см. RefreshInterval).</summary>
    /// <param name="byTimer">2026-11-06 (В11, шаг 5): вызов от таймера — пока пульс продаж жив, интервал растягивается до страховочного (OverviewIntervalNow); сигнал продаж и
    /// возврат в окно (byTimer = false) обновляют по прежним правилам — новый чек появляется в «Сводке» сразу.</param>
    private Task RefreshWhenShownAsync(bool byTimer = false)
    {
        // 2026-11-02 (живая проверка круга 3): в простое программа владельца делала ~19 запросов в минуту (касса — ~5,6). «Сводка» (несколько
        // запросов за раз) обновлялась каждые 30 с даже при простое, а видимая-но-неактивная — тоже каждые 30 с. Теперь тихо: см. OverviewIntervalNow.
        if (IsOverviewShown)
            return DateTime.UtcNow - _lastRefreshStartedUtc >= OverviewIntervalNow(inactive: false, byTimer) - TimeSpan.FromSeconds(5) ? RefreshAsync() : Task.CompletedTask;
        // 2026-10-22 (аудит 1.17.59, П-З): окно владельца видно, но не активно (владелец работает в кассе рядом) — сводка
        // раньше стояла («Обновлено в 07:10» в 07:45, «Чеки 0» при прошедших возвратах). Теперь обновляется и так, но реже —
        // раз в 5 минут, одним запросом сводки, без пересборки окна; не во время ввода текста.
        // 2026-10-23 (аудит, круг 8, В-6): интервал — RefreshInterval (60 с; допуск 5 с на дрожание таймера, иначе
        // обновление пропускалось бы до следующего тика), а «не во время ввода» — только если печатали менее 15 с назад
        // (раньше любое поле с фокусом, даже пустое «Найти раздел», останавливало обновление насовсем).
        var visibleInactive = IsVisible && WindowState != WindowState.Minimized && OverviewScroll.IsVisible && !IsActive;
        if (visibleInactive && DateTime.UtcNow - _lastRefreshStartedUtc >= OverviewIntervalNow(inactive: true, byTimer) - TimeSpan.FromSeconds(5)
            && !(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox
                 && DateTime.UtcNow - _lastKeyInputUtc < TypingQuietPeriod))
            return RefreshAsync();
        return Task.CompletedTask;
    }

    /// <summary>2026-11-02: как часто обновлять «Сводку» по таймеру. Работаю в окне — <see cref="RefreshInterval"/> (30 с); окно не в фокусе или нет ввода
    /// дольше <see cref="SyncCenter.IdleAfter"/> — 60 с; нет ввода дольше <see cref="SyncCenter.DeepIdleAfter"/> — 5 минут. Возврат в окно (Activated) и сигнал
    /// продаж обновляют сразу, поэтому видимые цифры не устаревают.</summary>
    private static TimeSpan OverviewIntervalNow(bool inactive, bool byTimer = false)
    {
        var idle = SyncCenter.InputIdle();
        if (idle >= SyncCenter.DeepIdleAfter)
            return TimeSpan.FromMinutes(5);
        // 2026-11-06 (В11, шаг 5): пульс продаж жив — «Сводку» обновляет его сигнал (OnSalesChangedForAbc) и возврат в окно, таймер — лишь страховка раз в 2 минуты
        // (было 30–60 с: те же 3 запроса сводки по таймеру при каждой «ничего не изменилось»). Пульс не отвечает — как раньше.
        var pulseAlive = byTimer && SyncCenter.SalesPulseAlive;
        if (idle >= SyncCenter.IdleAfter || inactive)
            return SyncCenter.OverviewIntervalFor(TimeSpan.FromSeconds(60), pulseAlive);
        return SyncCenter.OverviewIntervalFor(RefreshInterval, pulseAlive);
    }

    // 2026-11-05 (аудит №3, В4-01): строка ожидания сервера в шапке «Сводки» + «Отменить ожидание» (отменяет идущую загрузку, прежние цифры остаются).
    private ServerWaitIndicator? _refreshWait;
    private Button? _refreshWaitCancel;
    private string? _refreshWaitSaved;
    private string? _refreshWaitShown;

    private ServerWaitIndicator EnsureRefreshWait()
    {
        if (_refreshWait is not null)
            return _refreshWait;
        _refreshWaitCancel = new Button
        {
            Content = ServerWaitIndicator.CancelText, FontSize = 12, Padding = new Thickness(8, 2), MinHeight = 0, IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
        };
        _refreshWaitCancel.Click += (_, _) =>
        {
            try { _refreshCts?.Cancel(); } catch (ObjectDisposedException) { }
        };
        if (UpdatedText.Parent is Panel panel)
            panel.Children.Add(_refreshWaitCancel);
        _refreshWait = new ServerWaitIndicator(
            text =>
            {
                if (text is not null)
                {
                    if (_refreshWaitShown is null)
                        _refreshWaitSaved = UpdatedText.Text;
                    _refreshWaitShown = text;
                    UpdatedText.Text = text;
                }
                else if (_refreshWaitShown is not null)
                {
                    if (UpdatedText.Text == _refreshWaitShown)
                        UpdatedText.Text = _refreshWaitSaved;
                    _refreshWaitShown = null;
                }
            },
            visible => _refreshWaitCancel.IsVisible = visible);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(UpdatedText, Avalonia.Automation.AutomationLiveSetting.Polite);
        return _refreshWait;
    }

    private async Task RefreshAsync(bool forceChart = false, bool supersede = false)
    {
        if (_loggingOut || _cts.IsCancellationRequested)
            return;
        if (_refreshing)
        {
            if (supersede)
            {
                _refreshAgain = true;
                try { _refreshCts?.Cancel(); } catch (ObjectDisposedException) { }
            }

            return;
        }

        // 2026-09-29: сервер не отвечает — «Сводку» не перезапрашиваем каждые 20 с (таймауты и
        // лишняя нагрузка на лежащий сервер), показываем последние данные с пометкой времени.
        // Проверку связи ведёт ServerOutageMonitor; после восстановления таймер обновит сам.
        if (NurMarketKassa.Services.ServerOutageMonitor.IsOutage)
        {
            ShowOverviewStale();
            return;
        }

        _refreshing = true;
        // 2026-11-03 (С-13): «Обновить» во время загрузки не серая — нажатие отменяет идущую загрузку и начинает заново.
        _lastRefreshStartedUtc = DateTime.UtcNow;
        string? startedKey = null;
        var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _refreshCts = refreshCts;
        // 2026-11-05 (аудит №3, В4-01): сервер медлит (>3 с) — в шапке «Ждём ответ сервера NurCRM… N с» и «Отменить ожидание» (ServerWaitIndicator).
        DispatcherTimer.RunOnce(() =>
        {
            if (_refreshing && ReferenceEquals(_refreshCts, refreshCts))
                EnsureRefreshWait().OnSlow(true);
        }, NurMarketKassa.Services.ServerAnswerWait.SlowNoticeAfter);
        try
        {
            var (from, to) = CurrentRange();
            startedKey = RangeKey(from, to);
            var ct = refreshCts.Token;
            var fetchStartedUtc = DateTime.UtcNow;

            // 2026-09-28, «аналитика грузится долго»: все запросы «Сводки» (период, прошлый период,
            // график, последние чеки, возвраты) уходят одновременно, а не один за другим — ожидание
            // равно самому долгому из них, а не их сумме.
            var (prevFrom, prevTo) = PreviousRange(from, to);
            var compareKey = $"{_period}:{prevFrom:yyyyMMdd}:{prevTo:yyyyMMdd}";
            // График: для «сегодня» и «недели» — последние 7 дней, для месяца — дни месяца (они уже
            // есть в отчёте за период).
            var (chartFrom, chartTo) = !ChartIsPeriod ? (ServerClock.Today.AddDays(-6), ServerClock.Today) : (from, to);

            var reportTask = App.SalesApi.MarketSalesReportAsync(from, to, ct);
            // 2026-10-06, ТЗ ч.12, п. 3.7: для сравнения нужны только карточки — итоги периода (summary), не весь отчёт.
            // 2026-10-18, ревью О1: прошлый период другой — старые карточки сравнения не показывать, пока не пришли новые.
            if (_compareKey != compareKey)
                _compareCards = null;
            var previousTask = _compareKey != compareKey ? PreviousCardsAsync(prevFrom, prevTo, ct) : null;
            // 2026-10-04, п. 7: график 7 дней — раз в 5 минут (и сразу по «Обновить» / смене дня).
            var chartKey = $"{chartFrom:yyyyMMdd}:{chartTo:yyyyMMdd}";
            var chartCached = !forceChart
                && _chartCache is not null
                && _chartCacheKey == chartKey
                && DateTime.UtcNow - _chartCacheAtUtc < ChartRefreshInterval;
            var chartTask = !ChartIsPeriod && !chartCached ? App.SalesApi.MarketSalesReportAsync(chartFrom, chartTo, ct) : null;
            var rowsTask = App.SalesApi.PosSalesListAsync(1, RecentRows, null, ct, dateFrom: from, dateToExclusive: to.AddDays(1));
            // 2026-09-28 (BE-09): возвраты периода — из списка возвратов сервера (null — не
            // ответил, тогда из «Документы → Возврат продажи» отчёта, как раньше).
            var returnsTask = NurMarketKassa.Services.Api.NurCrmReportsApi.ReturnsTotalsAsync(from, to, ct);
            // 2026-10-06: «Сегодня» сравнивается со вчера до того же часа (OwnerShellWindow.SameTime.cs).
            var yesterdayTask = EnsureYesterdaySalesAsync(ct);
            // 2026-10-06, лог владельца (17:12, сбой SSL): если отчёт падает раньше, остальные запросы никто не ждёт — их
            // ошибки уходили в «UnobservedTaskException». Ошибку помечаем просмотренной; await ниже бросает её как раньше.
            foreach (var started in new Task?[] { previousTask, chartTask, rowsTask, returnsTask, yesterdayTask })
                _ = started?.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            var report = await reportTask.ConfigureAwait(true);

            if (previousTask != null)
            {
                _compareCards = await previousTask.ConfigureAwait(true);
                _compareKey = compareKey;
            }

            var chartSource = chartTask != null
                ? await chartTask.ConfigureAwait(true)
                : !ChartIsPeriod && chartCached ? _chartCache!.Value : report;
            if (chartTask != null)
            {
                _chartCache = chartSource.Clone();
                _chartCacheKey = chartKey;
                _chartCacheAtUtc = DateTime.UtcNow;
            }

            var rows = await rowsTask.ConfigureAwait(true);

            _lastReturns = await returnsTask.ConfigureAwait(true);
            _lastReturnsKey = RangeKey(from, to);
            await yesterdayTask.ConfigureAwait(true);

            // Отчёт сервера запоминается как есть: к нему добавляются чеки касс, которые сервер
            // ещё не видит (касса без интернета), — и сейчас, и когда связь пропадёт.
            _lastReport = report.Clone();
            _lastChart = chartSource.Clone();
            _lastRows = rows.Select(r => r.Clone()).ToList();
            _lastReportKey = RangeKey(from, to);
            _lastFetchStartedUtc = fetchStartedUtc;
            _lastSuccess = DateTime.Now;
            _offline = false;
            ApplyLanSales(online: true);
            // 2026-10-05, ТЗ часть 7, п. 2.5: карточка «План продаж на месяц» (обновляется не чаще раза в 5 минут).
            _ = RefreshSalesPlanAsync();
            // 2026-10-05, владелец: «где в сводке долги??» — карточка «Долги клиентов» (не чаще раза в 2 минуты).
            _ = RefreshDebtsCardAsync();
            // 2026-10-05, запрос NurCRM: заметка «Ваш магазин в приложении NurCRM — бесплатно», пока магазин не подключён.
            _ = RefreshAppShopNoteAsync();
            if (DateTime.UtcNow - _abcRefreshedUtc > TimeSpan.FromMinutes(1))
                RefreshAbcWhenVisible();

            UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");
            ToolTip.SetTip(UpdatedText, Tr.T($"Обновляется само каждые {RefreshInterval.TotalSeconds:0} с",
                $"Ар {RefreshInterval.TotalSeconds:0} с сайын өзү жаңырат", $"Auto-refreshes every {RefreshInterval.TotalSeconds:0} s",
                $"Her {RefreshInterval.TotalSeconds:0} saniyede bir otomatik yenilenir", $"Har {RefreshInterval.TotalSeconds:0} soniyada avtomatik yangilanadi"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: overview refresh failed: {ex.Message}", "WARNING");
            ShowOverviewStale();
        }
        finally
        {
            _refreshing = false;
            _refreshWait?.OnSlow(false); // 2026-11-05 (В4-01): убрать «Ждём ответ сервера…» и «Отменить ожидание»
            if (ReferenceEquals(_refreshCts, refreshCts))
                _refreshCts = null;
            refreshCts.Dispose();
            RefreshButton.IsEnabled = true;
            // 2026-11-03 (С-13): загрузку отменили («Обновить» / смена периода) — сразу начинаем новую.
            var restarted = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                if (!_loggingOut && !_cts.IsCancellationRequested)
                {
                    restarted = true;
                    _ = RefreshAsync(forceChart: true);
                }
            }

            // 2026-10-30 (стресс-тест «Отчёты», гонка периодов): пока шла загрузка, владелец нажал другой период — его RefreshAsync вернулся
            // сразу (идёт предыдущая), а предыдущая закончилась для прежнего периода: на кнопке новый период, цифры — прежнего, до таймера
            // (до минуты). Теперь, если период сменился за время загрузки, обновляем ещё раз — уже по нему.
            if (!restarted && startedKey != null && !_loggingOut && !_cts.IsCancellationRequested)
            {
                var (nowFrom, nowTo) = CurrentRange();
                if (RangeKey(nowFrom, nowTo) != startedKey)
                    _ = RefreshAsync();
            }
        }
    }

    // ── Без интернета: продажи касс по локальной сети ────────────────────────────────

    // Последний отчёт сервера за текущий период и когда его начали грузить (по своим часам) —
    // к нему добавляются продажи касс, которых в нём ещё нет.
    private JsonElement? _lastReport;
    private JsonElement? _lastChart;
    private List<JsonElement>? _lastRows;
    private string? _lastReportKey;
    // Возвраты периода по списку возвратов сервера (2026-09-28, BE-09) и для какого периода.
    private (int Count, decimal Sum)? _lastReturns;
    private string? _lastReturnsKey;
    private DateTime _lastFetchStartedUtc;
    private bool _offline;

    private string RangeKey(DateTime from, DateTime to) => $"{_period}:{from:yyyyMMdd}:{to:yyyyMMdd}";

    private (DateTime From, DateTime To) ChartRange(DateTime from, DateTime to) =>
        !ChartIsPeriod ? (ServerClock.Today.AddDays(-6), ServerClock.Today) : (from, to);

    private void OnLanPeerData() => Dispatcher.UIThread.Post(() =>
    {
        if (!_refreshing && !_loggingOut)
            ApplyLanSales(online: !_offline);
    });

    /// <summary>
    /// Отчёт сервера плюс продажи касс из локальной сети, которых в нём нет.
    /// online — отчёт только что получен: добавляются только чеки, которые кассы ещё не
    /// отправили на сервер (касса без интернета). Иначе сервер недоступен: продажа уже в
    /// последнем отчёте, если владелец узнал, что она на сервере, раньше, чем отчёт начали
    /// грузить; если отчёта за этот период не было — только продажи касс (прибыль и возвраты
    /// тогда неизвестны). false — показать нечего.
    /// </summary>
    private bool ApplyLanSales(bool online)
    {
        var (from, to) = CurrentRange();
        var (chartFrom, chartTo) = ChartRange(from, to);
        var hasBase = _lastReportKey == RangeKey(from, to) && _lastReport is not null;
        if (online && !hasBase)
            return false;

        List<LanPeerSale> peerSales;
        try
        {
            peerSales = LanJournal.ReadPeerSales(chartFrom.AddDays(-1).ToUniversalTime());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: продажи касс по локальной сети не прочитаны: {ex.Message}", "LAN");
            peerSales = new List<LanPeerSale>();
        }

        var fetchStarted = _lastFetchStartedUtc;
        var extra = online
            ? peerSales.Where(s => !s.IsOnServer).ToList()
            : peerSales.Where(s => !(hasBase && s.KnownOnServerSinceUtc is { } known && known <= fetchStarted)).ToList();
        var inPeriod = extra.Where(s => InDays(s, from, to)).ToList();
        var inChart = extra.Where(s => InDays(s, chartFrom, chartTo)).ToList();
        if (!hasBase && inPeriod.Count == 0 && inChart.Count == 0)
            return false;

        var report = MergeReport(hasBase ? _lastReport : null, inPeriod);
        var chartBase = hasBase ? (ChartIsPeriod ? _lastReport : _lastChart) : null;
        var chart = MergeDynamics(chartBase, inChart);
        var rows = new List<JsonElement>();
        if (hasBase && _lastRows != null)
            rows.AddRange(_lastRows);
        rows.AddRange(inPeriod.Select(LanSaleRow));
        rows = rows
            .OrderByDescending(r => DateTimeOffset.TryParse(Str(r, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue)
            .Take(RecentRows)
            .ToList();

        ApplyCards(report);
        if (!hasBase)
        {
            // Себестоимость кассы не передают — без отчёта сервера прибыль неизвестна, а не ноль.
            ProfitValue.Inlines = null;
            ProfitValue.Text = "—";
            // 2026-10-18, ревью О1: и сравнение прибыли неизвестно (было «▼ 100 % …» под «—»).
            ProfitDelta.Inlines = null;
            ProfitDelta.Text = "—";
        }
        ApplyChart(chart, chartFrom, chartTo);
        ApplyPayments(report);
        ApplyTopProducts(report);
        ApplyRecent(rows);

        var count = inPeriod.Count;
        if (online)
        {
            var now = _lastSuccess ?? DateTime.Now;
            UpdatedText.Text = Tr.T($"Обновлено в {now:HH:mm}", $"{now:HH:mm} жаңыртылды", $"Updated at {now:HH:mm}",
                $"Güncellendi: {now:HH:mm}", $"Yangilandi: {now:HH:mm}");
            if (count > 0)
                UpdatedText.Text += Tr.T($" · ещё {count} чек. касс без интернета", $" · интернетсиз кассалардан дагы {count} чек",
                    $" · plus {count} receipts from tills without internet", $" · internetsiz kasalardan {count} fiş daha",
                    $" · internetsiz kassalardan yana {count} ta chek");
            return true;
        }

        UpdatedText.Text = hasBase && _lastSuccess is { } at
            ? Tr.T($"Сервер недоступен · данные на {at:HH:mm} + {count} чек. с касс по локальной сети",
                $"Сервер жеткиликсиз · маалымат {at:HH:mm} боюнча + жергиликтүү тармактагы кассалардан {count} чек",
                $"Server unavailable · data as of {at:HH:mm} + {count} receipts from tills on the local network",
                $"Sunucu kullanılamıyor · veriler {at:HH:mm} itibarıyla + yerel ağdaki kasalardan {count} fiş",
                $"Server mavjud emas · ma'lumotlar {at:HH:mm} holatiga ko'ra + mahalliy tarmoqdagi kassalardan {count} ta chek")
            : Tr.T($"Сервер недоступен · по данным касс в локальной сети ({count} чек.)",
                $"Сервер жеткиликсиз · жергиликтүү тармактагы кассалардын маалыматы боюнча ({count} чек)",
                $"Server unavailable · from tills on the local network ({count} receipts)",
                $"Sunucu kullanılamıyor · yerel ağdaki kasaların verilerine göre ({count} fiş)",
                $"Server mavjud emas · mahalliy tarmoqdagi kassalar ma'lumotlari bo'yicha ({count} ta chek)");
        if (SessionExpiryMonitor.IsExpired)
            UpdatedText.Text = ExpiredHeaderText(); // 2026-11-06 (Б-8): сервер отвечает 401 — «Войдите снова», а не «Сервер недоступен»
        return true;
    }

    private static bool InDays(LanPeerSale s, DateTime from, DateTime to)
    {
        var day = s.Sale.CreatedAtUtc.ToLocalTime().Date;
        return day >= from.Date && day <= to.Date;
    }

    private static double NodeNum(JsonNode? node)
    {
        if (node is not JsonValue v)
            return 0;
        if (v.TryGetValue<double>(out var d))
            return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static JsonObject Obj(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o)
            return o;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray Arr(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray a)
            return a;
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    /// <summary>Карточки, способы оплаты и топ товаров отчёта + продажи касс.</summary>
    private static JsonElement MergeReport(JsonElement? baseReport, List<LanPeerSale> sales)
    {
        var root = baseReport is { ValueKind: JsonValueKind.Object } b
            ? JsonNode.Parse(b.GetRawText())!.AsObject()
            : new JsonObject();

        var cards = Obj(root, "cards");
        var revenue = NodeNum(cards["revenue"]) + sales.Sum(s => s.Sale.Total);
        var checks = NodeNum(cards["transactions"]) + sales.Count;
        cards["revenue"] = revenue;
        cards["transactions"] = checks;
        cards["avg_check"] = checks > 0 ? revenue / checks : 0;

        var methods = Arr(Obj(root, "charts"), "payment_methods");
        foreach (var group in sales.GroupBy(s => string.IsNullOrWhiteSpace(s.Sale.PaymentMethod) ? "cash" : s.Sale.PaymentMethod))
        {
            var row = methods.OfType<JsonObject>().FirstOrDefault(m =>
                string.Equals(m["method"]?.ToString(), group.Key, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                row = new JsonObject { ["method"] = group.Key, ["count"] = 0.0, ["total"] = 0.0 };
                methods.Add(row);
            }
            row["count"] = NodeNum(row["count"]) + group.Count();
            row["total"] = NodeNum(row["total"]) + group.Sum(s => s.Sale.Total);
        }

        var top = Arr(Obj(root, "tables"), "top_products");
        foreach (var group in sales.SelectMany(s => s.Sale.Items).Where(i => !string.IsNullOrWhiteSpace(i.Name)).GroupBy(i => i.Name))
        {
            var row = top.OfType<JsonObject>().FirstOrDefault(p => string.Equals(p["name"]?.ToString(), group.Key, StringComparison.Ordinal));
            if (row == null)
            {
                row = new JsonObject { ["name"] = group.Key, ["sold"] = 0.0, ["revenue"] = 0.0 };
                top.Add(row);
            }
            row["sold"] = NodeNum(row["sold"]) + group.Sum(i => i.Qty);
            row["revenue"] = NodeNum(row["revenue"]) + group.Sum(i => i.LineTotal);
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Выручка по дням для графика + продажи касс.</summary>
    private static JsonElement MergeDynamics(JsonElement? baseReport, List<LanPeerSale> sales)
    {
        var root = baseReport is { ValueKind: JsonValueKind.Object } b
            ? JsonNode.Parse(b.GetRawText())!.AsObject()
            : new JsonObject();
        var points = Arr(Obj(root, "charts"), "sales_dynamics");
        foreach (var group in sales.GroupBy(s => s.Sale.CreatedAtUtc.ToLocalTime().Date))
        {
            var date = group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var point = points.OfType<JsonObject>().FirstOrDefault(p => p["date"]?.ToString() == date);
            if (point == null)
            {
                point = new JsonObject { ["date"] = date, ["value"] = 0.0 };
                points.Add(point);
            }
            point["value"] = NodeNum(point["value"]) + group.Sum(s => s.Sale.Total);
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Строка «последних продаж» в том же виде, что приходит с сервера.</summary>
    private static JsonElement LanSaleRow(LanPeerSale s)
    {
        var place = s.Sale.CashboxName is { Length: > 0 } name ? name : s.DeviceName;
        if (!s.IsOnServer)
            place += " · " + Tr.T("ещё не на сервере", "серверге али жете элек", "not on the server yet", "henüz sunucuda değil", "hali serverda emas");
        var row = new JsonObject
        {
            ["status"] = "paid",
            ["payment_method"] = s.Sale.PaymentMethod,
            ["total"] = s.Sale.Total,
            ["created_at"] = s.Sale.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            ["first_item_name"] = s.Sale.Items.FirstOrDefault()?.Name ?? "",
            // Для «+ ещё N» в «Последних продажах» (2026-09-28): состав чека кассы уже известен.
            ["items_count"] = s.Sale.Items.Count,
            ["user_display"] = s.Sale.CashierName ?? "",
            ["cashbox_name"] = place,
        };
        return JsonSerializer.SerializeToElement(row);
    }

    private void ApplyCards(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("cards", out var cards))
            return;

        var prev = _compareCards;
        var hint = CompareHint();
        double? prevRevenue = prev is { } p1 ? Num(p1, "revenue") : null;
        double? prevChecks = prev is { } p2 ? Num(p2, "transactions") : null;
        double? prevAvg = prev is { } p3 ? Num(p3, "avg_check") : null;
        double? prevProfit = prev is { } p4 ? Num(p4, "gross_profit") : null;
        // 2026-10-06: «Сегодня» — со вчера до того же часа, а не с целым вчерашним днём (OwnerShellWindow.SameTime.cs).
        var sameTime = false;
        if (prev != null && YesterdayShareNow() is { } share)
        {
            prevRevenue *= share.Revenue;
            prevChecks *= share.Checks;
            prevAvg = prevChecks is > 0 ? prevRevenue / prevChecks : null;
            prevProfit *= share.Revenue;
            hint = Tr.T("к вчера на это время", "кечээки ушул убакытка карата", "vs yesterday at this time", "dünün aynı saatine göre", "kechaning shu vaqtiga nisbatan");
            sameTime = true;
        }
        // 2026-10-18, этап О1, «—» во всех четырёх карточках: прошлый период получен, но чеков в нём (к этому часу) не было —
        // сравнивать не с чем (у NBS 18.10 в 12:02 все 22 вчерашних чека были после 16:00). Теперь так и написано, а не «—».
        var noneBefore = prev != null && prevChecks is not > 0 ? NoSalesBeforeText(sameTime) : null;
        // 2026-10-21 (пакет 2, п. 85, аудит 18.10: «вчера к этому времени продаж не было» 4 раза): сравнивать не с чем — одна
        // строка под плитками, а строки сравнения в плитках скрыты; то же, если прошлый период не получен (было «— к вчера» ×4).
        var sharedNote = noneBefore ?? (prev == null ? NoCompareText() : null);
        KpiCompareNote.Text = sharedNote;
        KpiCompareNote.IsVisible = sharedNote != null;
        RevenueDelta.IsVisible = ChecksDelta.IsVisible = AvgDelta.IsVisible = ProfitDelta.IsVisible = sharedNote == null;

        SetMoney(RevenueValue, Num(cards, "revenue"));
        SetDelta(RevenueDelta, Num(cards, "revenue"), prevRevenue, hint, noneBefore);

        ChecksValue.Inlines = null;
        ChecksValue.Text = Num(cards, "transactions").ToString("N0", UiCulture);
        SetDelta(ChecksDelta, Num(cards, "transactions"), prevChecks, hint, noneBefore);

        SetMoney(AvgValue, Num(cards, "avg_check"));
        SetDelta(AvgDelta, Num(cards, "avg_check"), prevAvg, hint, noneBefore);

        SetMoney(ProfitValue, Num(cards, "gross_profit"));
        var margin = Num(cards, "margin_percent");
        _lastMargin = margin;
        // 2026-10-18, этап О1: маржа — в названии («Валовая прибыль · 21,2 %», макет Owner), а строка под числом —
        // сравнение, как у остальных показателей (раньше при марже сравнения у прибыли не было).
        ProfitLabel.Text = Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda")
                           + (margin != 0 ? " · " + Percent(margin) : "");
        SetDelta(ProfitDelta, Num(cards, "gross_profit"), prevProfit, hint, noneBefore);

        // 2026-10-05: те же цифры — «ИИ-советнику» (OwnerOverviewSnapshot), точнее локальной истории продаж.
        OwnerOverviewSnapshot.Set("1-cards",
            $"Сводка сервера NurCRM за период «{PeriodNameForAi()}»: выручка {Num(cards, "revenue"):0.##} сом, чеков {Num(cards, "transactions"):0}, " +
            $"средний чек {Num(cards, "avg_check"):0.##} сом, валовая прибыль {Num(cards, "gross_profit"):0.##} сом" +
            (margin != 0 ? $" (маржа {margin:0.#}%)" : "") + ".");
    }

    /// <summary>Название выбранного периода «Сводки» для «ИИ-советника».</summary>
    private string PeriodNameForAi()
    {
        var (from, to) = CurrentRange();
        return from.Date == to.Date
            ? from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + (from.Date == ServerClock.Today ? " (сегодня)" : "")
            : $"{from:dd.MM}–{to:dd.MM.yyyy}";
    }

    /// <summary>Сумма крупно, «сом» мелко и приглушённо. 2026-10-18, этап О1: без копеек (точная сумма — в «Продажах»).</summary>
    private void SetMoney(TextBlock target, double value)
    {
        var currency = new Run(" " + Som()) { FontSize = 17, FontWeight = FontWeight.Medium };
        UseRunBrush(currency, "BrushTextSoft", target);
        target.Text = null;
        target.Inlines = new InlineCollection { new Run(Whole(value)), currency };
    }

    /// <summary>2026-10-18, этап О1: строка сравнения «▲ 10 % к вчера на это время (3 900)» — стрелка и процент красным/зелёным,
    /// остальное серым; прошлое значение — в скобках. Нет прошлого периода — «—»; в нём не было продаж — noneBefore.</summary>
    private void SetDelta(TextBlock line, double current, double? previous, string hintText, string? noneBefore)
    {
        line.Inlines = null;
        if (_runBrushSubs.Remove(line, out var oldSub))
            oldSub.Dispose();
        if (previous is not { } prev || prev <= 0)
        {
            line.Text = noneBefore ?? "— " + hintText;
            return;
        }

        var change = (current - prev) / prev * 100;
        var abs = Math.Abs(change);
        var flat = abs < 0.5;
        var up = change > 0;
        var number = abs >= 100 ? abs.ToString("0", CultureInfo.InvariantCulture) : abs.ToString("0.#", UiCulture);
        var head = new Run((flat ? "" : up ? "▲ " : "▼ ") + Percent(flat ? 0 : abs, flat ? "0" : number)) { FontWeight = FontWeight.SemiBold };
        UseRunBrush(head, flat ? "BrushText" : up ? "BrushSuccess" : "BrushDanger", line);
        line.Text = null;
        line.Inlines = new InlineCollection { head, new Run($" {hintText} ({Whole(prev)})") };
    }

    /// <summary>«10 %» (в турецком «%10»), с неразрывным пробелом.</summary>
    private static string Percent(double value, string? formatted = null)
    {
        var n = formatted ?? value.ToString("0.#", UiCulture);
        return Tr.T($"{n} %", $"{n} %", $"{n}%", $"%{n}", $"{n} %");
    }

    /// <summary>Сумма без копеек: 4 300.</summary>
    private static string Whole(double value)
    {
        var r = Math.Round(value);
        if (r == 0)
            r = 0; // без «-0»
        return r.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// <summary>Подпись, когда в прошлом периоде (вчера к этому часу) продаж не было.
    /// 2026-10-21 (пакет 2, п. 85): одна строка под всеми плитками — с объяснением, почему нет «▲ / ▼».</summary>
    private string NoSalesBeforeText(bool sameTime) => _period switch
    {
        "week" => Tr.T("Сравнить не с чем: на прошлой неделе продаж не было", "Салыштырууга эч нерсе жок: өткөн жумада сатуу болгон эмес", "Nothing to compare with: no sales last week", "Karşılaştırılacak bir şey yok: geçen hafta satış yoktu", "Taqqoslash uchun hech narsa yo'q: o'tgan haftada sotuv bo'lmagan"),
        "month" => Tr.T("Сравнить не с чем: в прошлом месяце продаж не было", "Салыштырууга эч нерсе жок: өткөн айда сатуу болгон эмес", "Nothing to compare with: no sales last month", "Karşılaştırılacak bir şey yok: geçen ay satış yoktu", "Taqqoslash uchun hech narsa yo'q: o'tgan oyda sotuv bo'lmagan"),
        "custom" => Tr.T("Сравнить не с чем: в прошлом периоде продаж не было", "Салыштырууга эч нерсе жок: мурунку мезгилде сатуу болгон эмес", "Nothing to compare with: no sales in the previous period", "Karşılaştırılacak bir şey yok: önceki dönemde satış yoktu", "Taqqoslash uchun hech narsa yo'q: oldingi davrda sotuv bo'lmagan"),
        _ => sameTime
            ? Tr.T("Сравнить не с чем: вчера к этому времени продаж не было", "Салыштырууга эч нерсе жок: кечээ ушул убакытка чейин сатуу болгон эмес", "Nothing to compare with: no sales by this time yesterday", "Karşılaştırılacak bir şey yok: dün bu saate kadar satış yoktu", "Taqqoslash uchun hech narsa yo'q: kecha shu vaqtgacha sotuv bo'lmagan")
            : Tr.T("Сравнить не с чем: вчера продаж не было", "Салыштырууга эч нерсе жок: кечээ сатуу болгон эмес", "Nothing to compare with: no sales yesterday", "Karşılaştırılacak bir şey yok: dün satış yoktu", "Taqqoslash uchun hech narsa yo'q: kecha sotuv bo'lmagan"),
    };

    /// <summary>2026-10-21 (пакет 2, п. 85): прошлый период не получен (сервер не ответил) — одна строка вместо «— к вчера» в каждой плитке.</summary>
    private string NoCompareText() => _period switch
    {
        "week" => Tr.T("Сравнения с прошлой неделей пока нет — её данные не получены", "Өткөн жума менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with last week yet — its data was not received", "Geçen haftayla karşılaştırma henüz yok — verileri alınamadı", "O'tgan hafta bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        "month" => Tr.T("Сравнения с прошлым месяцем пока нет — его данные не получены", "Өткөн ай менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with last month yet — its data was not received", "Geçen ayla karşılaştırma henüz yok — verileri alınamadı", "O'tgan oy bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        "custom" => Tr.T("Сравнения с прошлым периодом пока нет — его данные не получены", "Мурунку мезгил менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with the previous period yet — its data was not received", "Önceki dönemle karşılaştırma henüz yok — verileri alınamadı", "Oldingi davr bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        _ => Tr.T("Сравнения со вчера пока нет — данные за вчера не получены", "Кечээ менен салыштыруу азырынча жок — кечээки маалымат алынган жок", "No comparison with yesterday yet — yesterday's data was not received", "Dünle karşılaştırma henüz yok — dünün verileri alınamadı", "Kecha bilan taqqoslash hozircha yo'q — kechagi ma'lumotlar olinmadi"),
    };
}
