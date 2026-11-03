using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Табель сотрудников (2026-09-05, доп. услуга "Учёт сотрудников" — часть "Табель,
/// смены"; "мотивация персонала" из описания карточки в Маркетплейсе сюда не входит, это
/// отдельная, пока не согласованная с пользователем задача — расчёт премий требует конкретных
/// правил начисления, которых пока никто не задавал). Чистый отчёт поверх уже существующих
/// данных о сменах (ShiftHistoryService — тот же источник, что и "История смен") — ничего
/// нового не хранится.</summary>
public partial class StaffTimesheetWindow : Window, IOwnerSectionExcel
{
    private ObservableCollection<TimesheetRowVm> _rows = new();

    public StaffTimesheetWindow()
    {
        InitializeComponent();
        TimesheetGrid.ItemsSource = _rows;
        // 2026-10-20 (живой аудит кассы, К18): окно стало шире (столбцы не обрезаются) — на маленьком экране кассы ужимается до него.
        Opened += (_, _) => NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit.FitToKassaScreen(this);
    }

    public static void Open(Window? owner)
    {
        var window = new StaffTimesheetWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
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
    private async void DateFilter_Changed(object? sender, SelectionChangedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    private async void ClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = null;
        ToDatePicker.SelectedDate = null;
        await LoadAsync().ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        StatusText.Text = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            var shifts = await ShiftHistoryService.LoadAsync(fresh: true).ConfigureAwait(true);
            var from = FromDatePicker.SelectedDate;
            var to = ToDatePicker.SelectedDate;
            var rows = StaffTimesheetService.Aggregate(shifts, from, to);

            _rows.Clear();
            foreach (var row in rows)
                _rows.Add(new TimesheetRowVm(row));

            StatusText.Text = rows.Count == 0
                ? Tr.T("Нет закрытых смен за выбранный период.", "Тандалган мезгилде жабылган сменалар жок.",
                    "No closed shifts for the selected period.", "Seçilen dönem için kapatılmış vardiya yok.",
                    "Tanlangan davr uchun yopilgan smenalar yo'q.")
                : Tr.T($"Кассиров: {rows.Count}.", $"Кассирлер: {rows.Count}.", $"Cashiers: {rows.Count}.",
                    $"Kasiyer sayısı: {rows.Count}.", $"Kassirlar: {rows.Count}.");
            // 2026-11-02 (аудит №2, В-13): смены длиннее 16 ч — вероятно, забыли закрыть; часы по ним недостоверны.
            var longShifts = rows.Sum(r => r.LongShiftCount);
            if (longShifts > 0)
                StatusText.Text += " " + Tr.T($"Проверьте: смен длиннее 16 ч — {longShifts} (возможно, смену забыли закрыть, часы завышены).",
                    $"Текшериңиз: 16 сааттан узун смена — {longShifts} (смена жабылбай калган болушу мүмкүн, саат көбөйтүлгөн).",
                    $"Check: shifts longer than 16 h — {longShifts} (a shift may have been left open, hours are overstated).",
                    $"Kontrol edin: 16 saatten uzun vardiya — {longShifts} (vardiya açık unutulmuş olabilir, saatler fazla).",
                    $"Tekshiring: 16 soatdan uzun smena — {longShifts} (smena yopilmay qolgan bo'lishi mumkin, soatlar oshirilgan).");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель сотрудников: не удалось загрузить данные: {ex}", "STAFF");
            StatusText.Text = Tr.T("Не удалось загрузить данные.", "Маалыматтарды жүктөө мүмкүн болгон жок.",
                "Could not load the data.", "Veriler yüklenemedi.", "Ma'lumotlarni yuklab bo'lmadi.");
        }
    }

    /// <summary>2026-11-02 (аудит №2, В-07): табель для Excel — строки таблицы на экране.</summary>
    public SectionTable? BuildExportTable()
    {
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
