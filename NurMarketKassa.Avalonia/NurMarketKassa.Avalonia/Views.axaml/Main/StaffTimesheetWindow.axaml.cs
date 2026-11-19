using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Источники данных окна табеля. По умолчанию — боевые (смены с сервера, сотрудники с сервера, файл отметок в AppPaths); тест подставляет свои, сервер не нужен.</summary>
public sealed class TimesheetDeps
{
    /// <summary>2026-11-19: полный список смен разом (тестовая заглушка). null — боевой режим: смены грузятся по месяцам через <see cref="MonthShifts"/> (вся история компании не успевала за 20 с).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ShiftHistoryEntry>>>? Shifts { get; init; }
    /// <summary>2026-11-19: «месячная» загрузка смен: (с какой даты, заново?, прогресс, отмена). По умолчанию — боевой загрузчик страниц с конца.</summary>
    public Func<DateTime, bool, Action<IReadOnlyList<ShiftHistoryEntry>>?, CancellationToken, Task<ShiftMonthResult>> MonthShifts { get; init; } =
        (since, force, progress, ct) => ShiftHistoryService.Month.LoadAsync(since, force, progress, ct);
    /// <summary>2026-11-19: готовый табель сервера (год, месяц, отмена) — запасной источник часов по дням, где смены месяца не пришли. null — не используется (так в тестах, если не задан).</summary>
    public Func<int, int, CancellationToken, Task<ServerTimesheet?>>? Timesheet { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<(string Key, string Name)>>> Employees { get; init; } = StaffTimesheetWindow.LoadServerEmployeesAsync;
    public TimesheetMarksService Marks { get; init; } = TimesheetMarksService.Default;
    /// <summary>2026-11-19: сколько ждать смены/сотрудников, прежде чем показать «не удалось, повторить» (тест ставит короче).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>2026-11-19: кэш последней загрузки между открытиями окна — только для боевых источников (тестовые заглушки кэш не трогают).</summary>
    public bool UseCache { get; init; }
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;
    /// <summary>null — решает право «Настройки» (владелец/администратор) или программа владельца.</summary>
    public bool? CanEdit { get; init; }
    public string? CurrentUserKey { get; init; }
    public string? CurrentUserName { get; init; }
}

/// <summary>Табель сотрудников. 2026-09-05 — доп. услуга «Учёт сотрудников», часть «Табель, смены»: итоги по закрытым сменам (ShiftHistoryService).
/// 2026-11-08 (запрос владельца «вернуть табель, отмечать сотрудника или авто-отметка»): вкладка «Отметки» — сетка сотрудники × дни месяца, ручные отметки
/// владельца (Был / Не был / Больничный / Отпуск / Выходной, часы, комментарий), быстрые действия, сверка со сменами, журнал изменений
/// (<see cref="TimesheetMarksService"/>), автоотметка кассой — в настройках «Операции». Окно есть и в программе владельца (раздел «Табель»).
/// Кто правит: владелец/администратор (право «Настройки») и программа владельца; кассир видит только свою строку, без правки. Деньги и зарплату окно не трогает.</summary>
public partial class StaffTimesheetWindow : Window, IOwnerSectionExcel, IOwnerSection
{
    private ObservableCollection<TimesheetRowVm> _rows = new();
    private readonly TimesheetDeps _deps;
    private bool _ownerSection;
    private int _loadVersion;

    public StaffTimesheetWindow() : this(null)
    {
    }

    public StaffTimesheetWindow(TimesheetDeps? deps)
    {
        _deps = deps ?? new TimesheetDeps { UseCache = true, Timesheet = LoadServerTimesheetAsync };
        InitializeComponent();
        TimesheetGrid.ItemsSource = _rows;
        InitMarksUi();
        // 2026-11-19: подпись месяца между ‹ › не должна быть пустой до конца загрузки.
        MonthText.Text = $"{MonthName(_month)} {_year}";
        RetryButton.Content = Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");
        ApplyTab();
        // 2026-10-20 (живой аудит кассы, К18): окно стало шире (столбцы не обрезаются) — на маленьком экране кассы ужимается до него.
        Opened += (_, _) =>
        {
            if (!_ownerSection)
                NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit.FitToKassaScreen(this);
        };
    }

    public static void Open(Window? owner)
    {
        // 2026-11-19 (список владельца, п. 7): одно окно на программу.
        NurMarketKassa.AvaloniaHost.Views.Dialogs.WindowSingleton.ShowOnce(owner, () => new StaffTimesheetWindow());
    }

    /// <summary>2026-11-08: раздел программы владельца — повтор названия и «Закрыть» не рисуем, поля как у остальных разделов.</summary>
    public void AsOwnerSection()
    {
        _ownerSection = true;
        Shell.ShowClose = false;
        Shell.Footer = null;
        // 2026-11-11 (живой показ 1.17.85, Д-4: «пустая шапка окна и широкие чёрные поля»): шапка оболочки остаётся всегда — без заголовка она была пустой полосой.
        // Теперь в ней заголовок «Табель сотрудников» (без подзаголовка); пустой подвал (кнопки «Закрыть» в разделе нет) прячем; карточка занимает всю высоту раздела
        // (раньше — 85 % экрана по центру, отсюда чёрные поля сверху и снизу).
        Shell.Subtitle = null;
        Shell.Margin = OwnerSectionLayout.Margin;
        Shell.ScreenHeightShare = 1.0;
        Shell.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        Shell.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_FooterGrid")?.Parent is Control footerBand)
                footerBand.IsVisible = false;
        };
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    // 2026-11-19: «Обновить» — смены заново с сервера; «Повторить» и открытие окна докачивают недостающее (кэш страниц живёт 5 минут).
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await LoadAsync(true).ConfigureAwait(true);

    /// <summary>Раньше был обычный DatePicker (3 отдельных "колеса" день/месяц/год с
    /// подтверждением галочкой во всплывающем окне) — кассир видел нередактируемую надпись
    /// "month" вместо числа и не понимал, как вообще выбрать дату, а сам выбор ничего не менял
    /// без явного клика по галочке (2026-09-05, подтверждено пользователем: "не сохраняет").
    /// CalendarDatePicker — обычный календарь-попап с выбором одним кликом, значение применяется
    /// сразу; вдобавок сразу же обновляет таблицу через SelectedDateChanged, не дожидаясь
    /// отдельного нажатия "Обновить".</summary>
    private void DateFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        RebuildShifts();
        // 2026-11-19: период «с» раньше начала месяца — догрузить более старые смены.
        if (_deps.Shifts == null && FromDatePicker.SelectedDate is { } f && f.Date < ShiftMonthLoader.MonthStart(_year, _month))
            _ = EnsureMonthShiftsAsync();
    }

    private void ClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = null;
        ToDatePicker.SelectedDate = null;
        RebuildShifts();
    }

    /// <summary>Боевой список сотрудников с сервера (id и имя). Нет связи/нет права — пусто: сетка соберётся из смен и отметок.</summary>
    public static async Task<IReadOnlyList<(string Key, string Name)>> LoadServerEmployeesAsync(CancellationToken ct)
    {
        try
        {
            var api = NurMarketKassa.AvaloniaHost.App.AuthApi;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var list = await api.GetEmployeesAsync(timeout.Token).ConfigureAwait(false);
            return list?.Where(e => !string.IsNullOrWhiteSpace(e.Id))
                       .Select(e => (e.Id!, !string.IsNullOrWhiteSpace(e.FullName) ? e.FullName! : e.Email ?? e.Id!)).ToList()
                   ?? new List<(string, string)>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: список сотрудников не получен: {ex.Message}", "STAFF");
            return new List<(string, string)>();
        }
    }

    /// <summary>2026-11-19: боевой источник готового табеля — GET main/staff/timesheet/?month=… (только если у сервера есть признак shifts_month_filter, с которым табель выкатили).
    /// Нет признака/связи/права, 400 period_invalid — null: окно работает по сменам, как раньше. Кассир без права «Сотрудники» получает только себя — это обычный ответ, не ошибка.</summary>
    public static async Task<ServerTimesheet?> LoadServerTimesheetAsync(int year, int month, CancellationToken ct)
    {
        try
        {
            if (NurMarketKassa.Services.ServerLoyalty.ApiProvider?.Invoke() is not { } api || string.IsNullOrEmpty(api.AccessToken)
                || !await NurMarketKassa.Services.Api.ServerCapabilities.HasAsync(api, NurMarketKassa.Services.Api.ServerCapabilities.ShiftsMonthFilter, ct).ConfigureAwait(false))
                return null;
            var raw = await NurMarketKassa.AvaloniaHost.App.ShiftApi.StaffTimesheetAsync(year, month, ct).ConfigureAwait(false);
            return raw is { } r ? ServerTimesheet.Parse(r, year, month) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Табель сотрудников: табель сервера за {year}-{month:D2} не получен: {ex.Message}", "STAFF");
            return null;
        }
    }

    /// <summary>2026-11-19: подгрузить табель сервера за показанный месяц и пересобрать «Отметки» (часы по дням без смен). Ошибка/нет данных — тихо, ничего не меняется.</summary>
    private async Task LoadServerTimesheetFactsAsync(bool force)
    {
        if (_deps.Timesheet is not { } source || _deps.Shifts != null)
            return;
        var (year, month) = (_year, _month);
        ServerTimesheet? ts = null;
        if (!force && _timesheetCache.TryGetValue((year, month), out var cached))
            ts = cached;
        else if (await WithLimitAsync(ct => source(year, month, ct), TimeSpan.FromSeconds(8), "табель сервера").ConfigureAwait(true) is { } fresh)
        {
            _timesheetCache[(year, month)] = fresh;
            ts = fresh;
        }
        if (ts == null || year != _year || month != _month)
            return;
        _serverTimesheet = ts;
        ReloadMarks();
        RebuildMarks();
    }

    // 2026-11-19 (живой обход владельца): кэш последней удачной загрузки (только боевые источники) — окно рисуется сразу из него, а не висит на «Загрузке…».
    private static List<ShiftHistoryEntry>? _cacheShifts;
    private static List<(string Key, string Name)>? _cacheEmployees;

    /// <summary>Ждёт источник не дольше лимита (он мог не уважать токен отмены — не зависаем на нём). null — таймаут или ошибка.</summary>
    private static async Task<T?> WithLimitAsync<T>(Func<CancellationToken, Task<T>> source, TimeSpan limit, string what) where T : class
    {
        using var cts = new CancellationTokenSource(limit);
        try
        {
            var task = Task.Run(() => source(cts.Token));
            var done = await Task.WhenAny(task, Task.Delay(limit, CancellationToken.None)).ConfigureAwait(true);
            if (done == task)
                return await task.ConfigureAwait(true);
            cts.Cancel();
            PosLogger.Log($"Табель сотрудников: {what} не получены за {limit.TotalSeconds:0} с", "STAFF");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: {what} не получены: {ex.Message}", "STAFF");
        }
        return null;
    }

    /// <summary>Загрузить смены, сотрудников и отметки и перестроить обе вкладки.
    /// 2026-11-19: сначала сразу рисуем то, что уже есть (кэш/пусто), смены и сотрудники догружаются с лимитом (<see cref="TimesheetDeps.LoadTimeout"/>);
    /// по таймауту/ошибке — понятная строка и «Повторить», а не вечная «Загрузка…».</summary>
    public async Task LoadAsync(bool force = false)
    {
        var version = ++_loadVersion;
        RetryButton.IsVisible = false;
        var useCache = _deps.UseCache;
        if (useCache && _shifts.Count == 0 && _cacheShifts != null)
            _shifts = _cacheShifts;
        if (useCache && _serverEmployees.Count == 0 && _cacheEmployees != null)
            _serverEmployees = _cacheEmployees;
        try
        {
            ReloadMarks();
            RebuildMarks();
            RebuildShifts();
            StatusText.Text = Tr.T("Загрузка смен…", "Смендер жүктөлүүдө…", "Loading shifts…", "Vardiyalar yükleniyor…", "Smenalar yuklanmoqda…");
            var limit = _deps.LoadTimeout;
            var shiftsVersion = ++_shiftsVersion;
            var shiftsTask = LoadShiftsAsync(limit, force, shiftsVersion);
            _ = LoadServerTimesheetFactsAsync(force); // 2026-11-19: готовый табель сервера (~0,03 с) — часы по дням, пока/если смены не пришли
            var employeesTask = WithLimitAsync(_deps.Employees, limit, "сотрудники");
            // сотрудники обычно приходят быстро — сетку показываем сразу, не дожидаясь смен
            var server = await employeesTask.ConfigureAwait(true);
            if (version != _loadVersion)
                return;
            if (server != null)
            {
                _serverEmployees = server.ToList();
                if (useCache)
                    _cacheEmployees = _serverEmployees;
                ReloadMarks();
                RebuildMarks();
            }
            var (shifts, failed) = await shiftsTask.ConfigureAwait(true);
            if (version != _loadVersion)
                return;
            ApplyShiftsOutcome(shifts, failed, shiftsVersion);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: не удалось загрузить данные: {ex}", "STAFF");
            StatusText.Text = Tr.T("Не удалось загрузить данные.", "Маалыматтарды жүктөө мүмкүн болгон жок.",
                "Could not load the data.", "Veriler yüklenemedi.", "Ma'lumotlarni yuklab bo'lmadi.");
            RetryButton.IsVisible = true;
        }
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    private int _shiftsVersion;

    /// <summary>2026-11-19: с какой даты нужны смены — начало показанного месяца (или период «с» из вкладки «Итоги», если он раньше).</summary>
    private DateTime ShiftsSince()
    {
        var start = ShiftMonthLoader.MonthStart(_year, _month);
        return FromDatePicker.SelectedDate is { } f && f.Date < start ? f.Date : start;
    }

    /// <summary>Смены: заглушка теста — целиком, как раньше (с лимитом); боевой режим — страницы с конца до начала месяца, то, что уже пришло, рисуется сразу.
    /// Failed=true — не всё пришло (Rows — что успели, null — ничего).</summary>
    private async Task<(IReadOnlyList<ShiftHistoryEntry>? Rows, bool Failed)> LoadShiftsAsync(TimeSpan limit, bool force, int version)
    {
        if (_deps.Shifts is { } all)
        {
            var rows = await WithLimitAsync(all, limit, "смены").ConfigureAwait(true);
            return (rows, rows == null);
        }

        using var cts = new CancellationTokenSource(limit);
        try
        {
            var res = await _deps.MonthShifts(ShiftsSince(), force, part => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != _shiftsVersion)
                    return;
                _shifts = part.ToList();
                ReloadMarks();
                RebuildMarks();
                RebuildShifts();
            }), cts.Token).ConfigureAwait(true);
            return (res.Entries.Count > 0 || !res.Failed ? res.Entries : null, res.Failed);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: смены за месяц не получены: {(ex is OperationCanceledException ? $"не за {limit.TotalSeconds:0} с" : ex.Message)}", "STAFF");
            return (null, true);
        }
    }

    /// <summary>Положить пришедшие смены в окно и показать итог: «Смены за … загружены» или причину с «Повторить». Старый ответ (месяц уже сменили) не трогает экран.</summary>
    private void ApplyShiftsOutcome(IReadOnlyList<ShiftHistoryEntry>? shifts, bool failed, int version)
    {
        if (version != _shiftsVersion)
            return;
        if (shifts != null)
        {
            _shifts = shifts.ToList();
            if (_deps.UseCache)
                _cacheShifts = _shifts;
        }
        ReloadMarks();
        RebuildMarks();
        RebuildShifts();
        var month = $"{MonthName(_month)} {_year}";
        if (failed)
        {
            StatusText.Text = Tr.T($"Не удалось загрузить смены за {month}. Повторите.", $"{month} үчүн смендерди жүктөө мүмкүн болгон жок. Кайра аракет кылыңыз.",
                $"Could not load shifts for {month}. Try again.", $"{month} için vardiyalar yüklenemedi. Tekrar deneyin.", $"{month} uchun smenalarni yuklab bo'lmadi. Qayta urinib ko'ring.");
            RetryButton.IsVisible = true;
        }
        else
        {
            RetryButton.IsVisible = false;
            var loaded = Tr.T($"Смены за {month} загружены.", $"{month} үчүн смендер жүктөлдү.", $"Shifts for {month} loaded.", $"{month} vardiyaları yüklendi.", $"{month} uchun smenalar yuklandi.");
            StatusText.Text = _deps.Shifts != null
                ? MarksStatusLine()
                : MarksTabRadio.IsChecked.GetValueOrDefault(true) ? loaded + " " + MarksStatusLine() : loaded + " " + ShiftsStatusLine(StaffTimesheetService.Aggregate(_shifts, FromDatePicker.SelectedDate, ToDatePicker.SelectedDate));
        }
    }

    /// <summary>2026-11-19: переключили месяц ‹ › (или период «с» раньше) — докачать недостающие смены; уже скачанное берётся из кэша страниц мгновенно.</summary>
    private async Task EnsureMonthShiftsAsync()
    {
        if (_deps.Shifts != null)
            return; // заглушка теста уже отдала все смены
        var version = ++_shiftsVersion;
        _ = LoadServerTimesheetFactsAsync(false);
        var month = $"{MonthName(_month)} {_year}";
        RetryButton.IsVisible = false;
        StatusText.Text = Tr.T($"Загрузка смен за {month}…", $"{month} үчүн смендер жүктөлүүдө…", $"Loading shifts for {month}…", $"{month} vardiyaları yükleniyor…", $"{month} uchun smenalar yuklanmoqda…");
        var (rows, failed) = await LoadShiftsAsync(_deps.LoadTimeout, false, version).ConfigureAwait(true);
        ApplyShiftsOutcome(rows, failed, version);
    }

    /// <summary>Вкладка «Итоги по сменам» — прежняя таблица по закрытым сменам.</summary>
    private void RebuildShifts()
    {
        var from = FromDatePicker.SelectedDate;
        var to = ToDatePicker.SelectedDate;
        var rows = StaffTimesheetService.Aggregate(_shifts, from, to);

        _rows.Clear();
        foreach (var row in rows)
            _rows.Add(new TimesheetRowVm(row));

        if (!ShiftsTabRadio.IsChecked.GetValueOrDefault())
            return;
        StatusText.Text = ShiftsStatusLine(rows);
    }

    private static string ShiftsStatusLine(IReadOnlyList<StaffTimesheetRow> rows)
    {
        var text = rows.Count == 0
            ? Tr.T("Нет закрытых смен за выбранный период.", "Тандалган мезгилде жабылган сменалар жок.",
                "No closed shifts for the selected period.", "Seçilen dönem için kapatılmış vardiya yok.",
                "Tanlangan davr uchun yopilgan smenalar yo'q.")
            : Tr.T($"Кассиров: {rows.Count}.", $"Кассирлер: {rows.Count}.", $"Cashiers: {rows.Count}.",
                $"Kasiyer sayısı: {rows.Count}.", $"Kassirlar: {rows.Count}.");
        // 2026-11-02 (аудит №2, В-13): смены длиннее 16 ч — вероятно, забыли закрыть; часы по ним недостоверны.
        var longShifts = rows.Sum(r => r.LongShiftCount);
        if (longShifts > 0)
            text += " " + Tr.T($"Проверьте: смен длиннее 16 ч — {longShifts} (возможно, смену забыли закрыть, часы завышены).",
                $"Текшериңиз: 16 сааттан узун смена — {longShifts} (смена жабылбай калган болушу мүмкүн, саат көбөйтүлгөн).",
                $"Check: shifts longer than 16 h — {longShifts} (a shift may have been left open, hours are overstated).",
                $"Kontrol edin: 16 saatten uzun vardiya — {longShifts} (vardiya açık unutulmuş olabilir, saatler fazla).",
                $"Tekshiring: 16 soatdan uzun smena — {longShifts} (smena yopilmay qolgan bo'lishi mumkin, soatlar oshirilgan).");
        return text;
    }

    private void Tab_Click(object? sender, RoutedEventArgs e) => ApplyTab();

    private void ApplyTab()
    {
        var marks = MarksTabRadio.IsChecked.GetValueOrDefault(true);
        MarksPanel.IsVisible = marks;
        MonthBar.IsVisible = marks;
        ShiftsPanel.IsVisible = !marks;
        PeriodBar.IsVisible = !marks;
        // 2026-11-08: на «Отметках» подзаголовок короткий (длинный расчёт «по сменам» отнимал две строки высоты на 1024×600); на «Итогах по сменам» — прежний текст.
        if (!_ownerSection)
        {
            Shell.Subtitle = marks
                ? Tr.T("Отметки сотрудников по дням: нажмите на клетку, чтобы отметить. Часы — по закрытым сменам или вручную.", "Кызматкерлердин күндөр боюнча белгилери: белгилөө үчүн уячаны басыңыз. Саат — жабылган смена боюнча же кол менен.",
                    "Staff marks by day: click a cell to mark. Hours come from closed shifts or are set by hand.", "Çalışan işaretleri günlük: işaretlemek için hücreye tıklayın. Saat kapanmış vardiyalardan veya elle.",
                    "Xodimlar belgilari kunlar bo'yicha: belgilash uchun katakni bosing. Soat yopilgan smenalardan yoki qo'lda.")
                : (this.TryFindResource("staffTimesheet.description", ActualThemeVariant, out var d) ? d as string : null);
        }
        StatusText.Text = marks ? MarksStatusLine() : ShiftsStatusLine(StaffTimesheetService.Aggregate(_shifts, FromDatePicker.SelectedDate, ToDatePicker.SelectedDate));
    }

    /// <summary>2026-11-02 (аудит №2, В-07): табель для Excel — вкладка «Отметки»: сетка месяца с итогами; «Итоги по сменам»: строки таблицы на экране.</summary>
    public SectionTable? BuildExportTable()
    {
        if (MarksTabRadio.IsChecked.GetValueOrDefault(true))
        {
            var emps = VisibleEmployees();
            return emps.Count == 0 ? null : TimesheetMarksService.BuildExportTable(_year, _month, emps, _allMarks, _shifts);
        }

        if (_rows.Count == 0)
            return null;
        var rows = _rows.Select(r => new object?[] { r.Cashier, r.ShiftCountText, r.DaysWorkedText, r.TotalWorkedText, r.AverageShiftText, r.TotalRevenueText, r.PeriodText }).ToList();
        return new SectionTable(Tr.T("Табель", "Табель", "Timesheet", "Çizelge", "Tabel"), DateTime.Today.ToString("dd.MM.yyyy"),
        [
            new XlColumn(Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")),
            new XlColumn(Tr.T("Смен", "Смена", "Shifts", "Vardiya", "Smena")),
            new XlColumn(Tr.T("Дней", "Күн", "Days", "Gün", "Kun")),
            new XlColumn(Tr.T("Отработано", "Иштеген", "Worked", "Çalışılan", "Ishlagan")),
            new XlColumn(Tr.T("Средняя смена", "Орточо смена", "Average shift", "Ortalama vardiya", "O'rtacha smena")),
            new XlColumn(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")),
            new XlColumn(Tr.T("Период", "Мезгил", "Period", "Dönem", "Davr")),
        ], rows);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Обёртка только для форматирования DataGrid-колонок текстом — сама агрегация
    /// (StaffTimesheetRow) не знает про UI-форматирование чисел/времени.</summary>
    private sealed class TimesheetRowVm(StaffTimesheetRow row)
    {
        public string Cashier { get; } = row.Cashier;
        public string ShiftCountText { get; } = row.ShiftCount.ToString(CultureInfo.InvariantCulture);
        public string DaysWorkedText { get; } = row.DaysWorked.ToString(CultureInfo.InvariantCulture);
        public string TotalWorkedText { get; } = FormatHours(row.TotalWorked);
        public string AverageShiftText { get; } = FormatHours(row.AverageShift);
        public string TotalRevenueText { get; } = $"{row.TotalRevenue:N0} {Tr.T("сом", "сом", "som", "som", "so'm")}";
        public string PeriodText { get; } = row.FirstShift is { } first && row.LastShift is { } last
            ? $"{first:dd.MM.yyyy} — {last:dd.MM.yyyy}"
            : "—";

        private static string FormatHours(TimeSpan span) =>
            $"{(int)span.TotalHours} {Tr.T("ч", "саат", "h", "sa", "soat")} {span.Minutes} {Tr.T("мин", "мүн", "min", "dk", "daq.")}";
    }
}
