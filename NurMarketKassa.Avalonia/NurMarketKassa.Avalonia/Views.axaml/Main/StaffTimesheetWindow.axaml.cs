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
    public Func<CancellationToken, Task<IReadOnlyList<ShiftHistoryEntry>>> Shifts { get; init; } = async ct => await ShiftHistoryService.LoadAsync(ct, fresh: true).ConfigureAwait(false);
    public Func<CancellationToken, Task<IReadOnlyList<(string Key, string Name)>>> Employees { get; init; } = StaffTimesheetWindow.LoadServerEmployeesAsync;
    public TimesheetMarksService Marks { get; init; } = TimesheetMarksService.Default;
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
        _deps = deps ?? new TimesheetDeps();
        InitializeComponent();
        TimesheetGrid.ItemsSource = _rows;
        InitMarksUi();
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
        var window = new StaffTimesheetWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
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

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    /// <summary>Раньше был обычный DatePicker (3 отдельных "колеса" день/месяц/год с
    /// подтверждением галочкой во всплывающем окне) — кассир видел нередактируемую надпись
    /// "month" вместо числа и не понимал, как вообще выбрать дату, а сам выбор ничего не менял
    /// без явного клика по галочке (2026-09-05, подтверждено пользователем: "не сохраняет").
    /// CalendarDatePicker — обычный календарь-попап с выбором одним кликом, значение применяется
    /// сразу; вдобавок сразу же обновляет таблицу через SelectedDateChanged, не дожидаясь
    /// отдельного нажатия "Обновить".</summary>
    private void DateFilter_Changed(object? sender, SelectionChangedEventArgs e) => RebuildShifts();

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

    /// <summary>Загрузить смены, сотрудников и отметки и перестроить обе вкладки.</summary>
    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        StatusText.Text = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            var shifts = await _deps.Shifts(CancellationToken.None).ConfigureAwait(true);
            var server = await _deps.Employees(CancellationToken.None).ConfigureAwait(true);
            if (version != _loadVersion)
                return;
            _shifts = shifts.ToList();
            _serverEmployees = server.ToList();
            ReloadMarks();
            RebuildMarks();
            RebuildShifts();
            StatusText.Text = MarksStatusLine();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: не удалось загрузить данные: {ex}", "STAFF");
            StatusText.Text = Tr.T("Не удалось загрузить данные.", "Маалыматтарды жүктөө мүмкүн болгон жок.",
                "Could not load the data.", "Veriler yüklenemedi.", "Ma'lumotlarni yuklab bo'lmadi.");
        }
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
