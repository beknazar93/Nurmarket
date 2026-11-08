using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-08: вкладка «Отметки» табеля — сетка сотрудники × дни месяца (столбец сотрудника и строка дней закреплены при прокрутке), правка дня по нажатию
/// на клетку, быстрые действия, журнал. Расчёты — в <see cref="TimesheetMarksService"/>.</summary>
public partial class StaffTimesheetWindow
{
    private const double CellW = 46, RowH = 54, NameW = 210, HeadH = 52;

    private List<ShiftHistoryEntry> _shifts = new();
    private List<(string Key, string Name)> _serverEmployees = new();
    private List<TimesheetMark> _allMarks = new();
    private List<(string Key, string Name)> _employees = new();
    private readonly Dictionary<string, Dictionary<DateOnly, double>> _shiftHours = new(StringComparer.OrdinalIgnoreCase);
    private int _year = DateTime.Today.Year, _month = DateTime.Today.Month;

    // закреплённые части сетки
    private Control? _corner, _headerRow, _namesPanel;

    private static readonly (TimesheetStatus Status, Color Color)[] StatusColors =
    [
        (TimesheetStatus.Present, Color.FromRgb(0x2E, 0x7D, 0x32)),
        (TimesheetStatus.Absent, Color.FromRgb(0xC6, 0x28, 0x28)),
        (TimesheetStatus.Sick, Color.FromRgb(0xEF, 0x6C, 0x00)),
        (TimesheetStatus.Vacation, Color.FromRgb(0x15, 0x65, 0xC0)),
        (TimesheetStatus.DayOff, Color.FromRgb(0x75, 0x75, 0x75)),
    ];

    private DateOnly Today => DateOnly.FromDateTime(_deps.Today());

    /// <summary>Правит владелец/администратор (право «Настройки») и программа владельца; кассир — только смотрит свою строку.</summary>
    public bool CanEdit
    {
        get
        {
            if (_deps.CanEdit is { } forced)
                return forced;
            if (NurMarketKassa.Services.AppMode.IsOwner)
                return true;
            try { return NurMarketKassa.AvaloniaHost.App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewSettings); }
            catch (Exception) { return false; }
        }
    }

    private string EditorName =>
        PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId ?? Tr.T("Владелец", "Ээси", "Owner", "Sahip", "Egasi");

    private string CurrentKey => _deps.CurrentUserKey ?? PosApp.CurrentUserId ?? "";
    private string CurrentName => _deps.CurrentUserName ?? PosApp.CurrentUserDisplayName ?? "";

    private void InitMarksUi()
    {
        _year = Today.Year;
        _month = Today.Month;
        MarksTabRadio.Content = Tr.T("Отметки", "Белгилер", "Marks", "İşaretler", "Belgilar");
        ShiftsTabRadio.Content = Tr.T("Итоги по сменам", "Смена боюнча жыйынтык", "Shift totals", "Vardiya toplamları", "Smena yakunlari");
        ThisMonthButton.Content = Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy");
        MarkAllTodayButton.Content = Tr.T("Отметить всех присутствующими за сегодня", "Бүгүн баарын келди деп белгилөө", "Mark everyone present today", "Bugün herkesi geldi işaretle", "Bugun hammani keldi deb belgilash");
        CopyYesterdayButton.Content = Tr.T("Скопировать вчера", "Кечээкини көчүрүү", "Copy yesterday", "Dünü kopyala", "Kechagini nusxalash");
        JournalButton.Content = Tr.T("Журнал изменений", "Өзгөртүүлөр журналы", "Change log", "Değişiklik günlüğü", "O'zgarishlar jurnali");
        AutomationProperties.SetName(PrevMonthButton, Tr.T("Предыдущий месяц", "Мурунку ай", "Previous month", "Önceki ay", "Oldingi oy"));
        AutomationProperties.SetName(NextMonthButton, Tr.T("Следующий месяц", "Кийинки ай", "Next month", "Sonraki ay", "Keyingi oy"));
        TableScroll.ScrollChanged += (_, _) => UpdateSticky();
        ActualThemeVariantChanged += (_, _) => RebuildMarks();
    }

    private void ReloadMarks()
    {
        _allMarks = _deps.Marks.Load().Marks;
        _employees = TimesheetMarksService.MergeEmployees(_serverEmployees, _shifts, _allMarks);
        _shiftHours.Clear();
        foreach (var (key, name) in _employees)
            _shiftHours[key] = TimesheetMarksService.ShiftHoursByDay(_shifts, key, name);
    }

    private List<(string Key, string Name)> VisibleEmployees()
    {
        if (CanEdit)
            return _employees;
        var key = CurrentKey;
        var name = CurrentName.Trim();
        return _employees.Where(e => (!string.IsNullOrEmpty(key) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                                     || (name.Length > 0 && string.Equals(e.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private double ShiftHoursOf(string key, DateOnly date) =>
        _shiftHours.TryGetValue(key, out var map) && map.TryGetValue(date, out var h) ? h : 0;

    private TimesheetMark? MarkOf(string key, DateOnly date) =>
        _allMarks.FirstOrDefault(m => m.Date == date && string.Equals(m.EmployeeKey, key, StringComparison.OrdinalIgnoreCase));

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private static IBrush Tint(Color c, byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    private static Color ColorOf(TimesheetStatus s) => StatusColors.First(x => x.Status == s).Color;

    // ───────────────────────── месяц ─────────────────────────

    private void PrevMonth_Click(object? sender, RoutedEventArgs e) => ShiftMonth(-1);
    private void NextMonth_Click(object? sender, RoutedEventArgs e) => ShiftMonth(1);

    private void ThisMonth_Click(object? sender, RoutedEventArgs e)
    {
        _year = Today.Year;
        _month = Today.Month;
        RebuildMarks();
    }

    private void ShiftMonth(int delta)
    {
        var d = new DateTime(_year, _month, 1).AddMonths(delta);
        _year = d.Year;
        _month = d.Month;
        RebuildMarks();
        StatusText.Text = MarksStatusLine();
    }

    private static readonly string[] MonthsRu = ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthsTr = ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];
    private static readonly string[] MonthsUz = ["Yanvar", "Fevral", "Mart", "Aprel", "May", "Iyun", "Iyul", "Avgust", "Sentabr", "Oktabr", "Noyabr", "Dekabr"];

    private static string MonthName(int m) => Tr.T(MonthsRu[m - 1], MonthsRu[m - 1], MonthsEn[m - 1], MonthsTr[m - 1], MonthsUz[m - 1]);

    private static string WeekdayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => Tr.T("Пн", "Дш", "Mo", "Pt", "Du"),
        DayOfWeek.Tuesday => Tr.T("Вт", "Шш", "Tu", "Sa", "Se"),
        DayOfWeek.Wednesday => Tr.T("Ср", "Шр", "We", "Ça", "Ch"),
        DayOfWeek.Thursday => Tr.T("Чт", "Бш", "Th", "Pe", "Pa"),
        DayOfWeek.Friday => Tr.T("Пт", "Жм", "Fr", "Cu", "Ju"),
        DayOfWeek.Saturday => Tr.T("Сб", "Иш", "Sa", "Ct", "Sh"),
        _ => Tr.T("Вс", "Жк", "Su", "Pz", "Ya"),
    };

    private string MarksStatusLine()
    {
        if (!MarksTabRadio.IsChecked.GetValueOrDefault(true))
            return "";
        var n = VisibleEmployees().Count;
        if (n == 0)
            return CanEdit
                ? Tr.T("Сотрудников пока нет: список придёт с сервера, а кассиры появятся после первых смен.", "Кызматкерлер азырынча жок: тизме серверден келет, кассирлер биринчи смендерден кийин чыгат.",
                    "No staff yet: the list comes from the server, cashiers appear after their first shifts.", "Henüz çalışan yok: liste sunucudan gelir, kasiyerler ilk vardiyadan sonra görünür.",
                    "Hozircha xodimlar yo'q: ro'yxat serverdan keladi, kassirlar birinchi smenadan keyin ko'rinadi.")
                : Tr.T("Ваших отметок пока нет.", "Сиздин белгилериңиз азырынча жок.", "You have no marks yet.", "Henüz işaretiniz yok.", "Sizning belgilaringiz hali yo'q.");
        var text = Tr.T($"Сотрудников: {n}.", $"Кызматкерлер: {n}.", $"Staff: {n}.", $"Çalışan: {n}.", $"Xodimlar: {n}.");
        if (CanEdit)
            text += " " + Tr.T("Нажмите на клетку дня, чтобы отметить.", "Белгилөө үчүн күндүн уячасын басыңыз.", "Click a day cell to mark.", "İşaretlemek için gün hücresine tıklayın.", "Belgilash uchun kun katagini bosing.");
        else
            text += " " + Tr.T("Только просмотр: отметки ставит владелец.", "Көрүү гана: белгилерди ээси коёт.", "View only: the owner sets the marks.", "Yalnızca görüntüleme: işaretleri sahip koyar.", "Faqat ko'rish: belgilarni egasi qo'yadi.");
        return text;
    }

    // ───────────────────────── сетка ─────────────────────────

    /// <summary>Перестроить сетку месяца. Дёшево: сотрудников десятки, клеток до ~31 на каждого.</summary>
    private void RebuildMarks()
    {
        MonthText.Text = $"{MonthName(_month)} {_year}";
        var emps = VisibleEmployees();
        var days = DateTime.DaysInMonth(_year, _month);
        var from = new DateOnly(_year, _month, 1);
        var to = new DateOnly(_year, _month, days);
        var panel = Res("BrushPanel", Brushes.White);
        var soft = Res("BrushPanelSoft", Brushes.WhiteSmoke);
        var textSoft = Res("BrushTextSoft", Brushes.Gray);
        var border = Res("BrushBorder", Brushes.LightGray);

        TableGrid.Children.Clear();
        TableGrid.RowDefinitions.Clear();
        TableGrid.ColumnDefinitions.Clear();
        TableGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        TableGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        TableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // угол (закреплён и по горизонтали, и по вертикали)
        _corner = new Border
        {
            Width = NameW, Height = HeadH, Background = panel, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1), ZIndex = 3,
            Child = new TextBlock
            {
                Text = Tr.T("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim"), FontWeight = FontWeight.SemiBold, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0),
            },
        };
        Grid.SetRow(_corner, 0); Grid.SetColumn(_corner, 0);

        // строка дней (закреплена по вертикали)
        var head = new StackPanel { Orientation = Orientation.Horizontal, ZIndex = 2, Background = panel };
        for (var d = 1; d <= days; d++)
        {
            var date = new DateOnly(_year, _month, d);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var isToday = date == Today;
            head.Children.Add(new Border
            {
                Width = CellW, Height = HeadH,
                Background = isToday ? Tint(Color.FromRgb(0x15, 0x65, 0xC0), 0x33) : weekend ? soft : panel,
                BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = d.ToString(CultureInfo.InvariantCulture), FontWeight = isToday ? FontWeight.Bold : FontWeight.SemiBold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = WeekdayShort(date.DayOfWeek), FontSize = 11, Foreground = textSoft, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            });
        }
        _headerRow = head;
        Grid.SetRow(head, 0); Grid.SetColumn(head, 1);

        // столбец сотрудников (закреплён по горизонтали) и тело
        var names = new StackPanel { ZIndex = 2, Background = panel };
        var body = new StackPanel();
        foreach (var (key, name) in emps)
        {
            var mine = _allMarks.Where(m => string.Equals(m.EmployeeKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
            var shiftMap = _shiftHours.GetValueOrDefault(key);
            var totals = TimesheetMarksService.Summarize(mine, from, to, shiftMap);
            var other = totals.SickDays + totals.VacationDays + totals.DayOffDays + totals.AbsentDays;
            var totalsText = Tr.T($"{totals.PresentDays} дн · {HoursPlain(totals.PresentHours)} ч", $"{totals.PresentDays} күн · {HoursPlain(totals.PresentHours)} саат", $"{totals.PresentDays} d · {HoursPlain(totals.PresentHours)} h",
                $"{totals.PresentDays} gün · {HoursPlain(totals.PresentHours)} sa", $"{totals.PresentDays} kun · {HoursPlain(totals.PresentHours)} soat");
            if (other > 0)
                totalsText += " · " + Tr.T($"прочее {other}", $"башка {other}", $"other {other}", $"diğer {other}", $"boshqa {other}");
            var nameCell = new Border
            {
                Width = NameW, Height = RowH, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0),
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = totalsText, FontSize = 11, Foreground = textSoft, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(nameCell, $"{name}: {totalsText}");
            names.Children.Add(nameCell);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = RowH };
            for (var d = 1; d <= days; d++)
                row.Children.Add(MakeCell(key, name, new DateOnly(_year, _month, d), border, soft, textSoft));
            body.Children.Add(row);
        }
        _namesPanel = names;
        Grid.SetRow(names, 1); Grid.SetColumn(names, 0);
        Grid.SetRow(body, 1); Grid.SetColumn(body, 1);

        TableGrid.Children.Add(body);
        TableGrid.Children.Add(head);
        TableGrid.Children.Add(names);
        TableGrid.Children.Add(_corner);
        BuildLegend(textSoft);
        ActionsPanel.IsVisible = CanEdit;
        JournalButton.IsVisible = CanEdit;
        UpdateSticky();
    }

    private static string HoursPlain(double h) => h.ToString("0.##", CultureInfo.CurrentCulture);

    private void UpdateSticky()
    {
        var o = TableScroll.Offset;
        if (_corner is not null) _corner.RenderTransform = new TranslateTransform(o.X, o.Y);
        if (_headerRow is not null) _headerRow.RenderTransform = new TranslateTransform(0, o.Y);
        if (_namesPanel is not null) _namesPanel.RenderTransform = new TranslateTransform(o.X, 0);
    }

    private Control MakeCell(string key, string name, DateOnly date, IBrush border, IBrush soft, IBrush textSoft)
    {
        var mark = MarkOf(key, date);
        var shiftH = ShiftHoursOf(key, date);
        var check = TimesheetMarksService.Reconcile(mark, shiftH);
        string line1, line2 = "";
        IBrush background = Brushes.Transparent, edge = border;
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (mark is null)
        {
            line1 = shiftH > 0 ? "~" + HoursPlain(Math.Round(shiftH, 1)) : "";
            if (weekend) background = soft;
        }
        else
        {
            var color = ColorOf(mark.Status);
            background = Tint(color, 0x40);
            edge = new SolidColorBrush(color);
            line1 = TimesheetMarksService.StatusCode(mark.Status) + (check != TimesheetCheck.None ? "!" : "");
            if (mark.Status == TimesheetStatus.Present)
                line2 = mark.Hours is { } h ? HoursPlain(h) : shiftH > 0 ? HoursPlain(Math.Round(shiftH, 1)) : "…";
        }

        var description = Describe(name, date, mark, shiftH, check);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var codeText = new TextBlock
        {
            Text = line1, FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            FontStyle = mark is { Auto: true } ? FontStyle.Italic : FontStyle.Normal,
        };
        if (mark is null)
            codeText.Foreground = textSoft; // без отметки (только «~часы по смене») — приглушённо
        panel.Children.Add(codeText);
        if (line2.Length > 0)
            panel.Children.Add(new TextBlock { Text = line2, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, FontStyle = mark is { Auto: true } ? FontStyle.Italic : FontStyle.Normal });

        var button = new Button
        {
            Width = CellW, Height = RowH, Padding = new Thickness(0), Margin = new Thickness(0),
            Background = background, BorderBrush = edge, BorderThickness = new Thickness(mark is null ? 0.5 : 1.5), CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = panel, Tag = (key, name, date),
        };
        ToolTip.SetTip(button, description);
        AutomationProperties.SetName(button, description);
        button.Click += Cell_Click;
        return button;
    }

    private static string Describe(string name, DateOnly date, TimesheetMark? mark, double shiftH, TimesheetCheck check)
    {
        var parts = new List<string> { $"{name}, {date:dd.MM.yyyy} ({WeekdayShort(date.DayOfWeek)})" };
        if (mark is null)
            parts.Add(Tr.T("отметки нет", "белги жок", "no mark", "işaret yok", "belgi yo'q"));
        else
        {
            var s = TimesheetMarksService.StatusName(mark.Status);
            if (mark.Status == TimesheetStatus.Present)
                s += mark.Hours is { } h ? " " + TimesheetMarksService.HoursText(h) : " (" + Tr.T("часы ещё не записаны", "саат жазыла элек", "hours not recorded yet", "saat henüz yazılmadı", "soat hali yozilmagan") + ")";
            if (mark.Auto)
                s += " — " + Tr.T("поставлено кассой автоматически", "касса автоматтык койду", "set automatically by the till", "kasa otomatik koydu", "kassa avtomatik qo'ydi");
            parts.Add(s);
            if (!string.IsNullOrWhiteSpace(mark.Comment))
                parts.Add(Tr.T("комментарий: ", "комментарий: ", "comment: ", "yorum: ", "izoh: ") + mark.Comment);
        }
        parts.Add(shiftH > 0
            ? Tr.T("по смене: ", "смена боюнча: ", "by shift: ", "vardiyaya göre: ", "smena bo'yicha: ") + TimesheetMarksService.HoursText(Math.Round(shiftH, 1))
            : Tr.T("смен в этот день нет", "бул күнү смена жок", "no shifts this day", "o gün vardiya yok", "bu kuni smena yo'q"));
        var warn = CheckText(check, mark, shiftH);
        if (warn != null)
            parts.Add(warn);
        return string.Join("; ", parts);
    }

    private static string? CheckText(TimesheetCheck check, TimesheetMark? mark, double shiftH) => check switch
    {
        TimesheetCheck.AbsentButShift => Tr.T($"Внимание: отмечен как «{TimesheetMarksService.StatusName(mark!.Status).ToLowerInvariant()}», но была смена ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Көңүл буруңуз: «{TimesheetMarksService.StatusName(mark!.Status).ToLowerInvariant()}» деп белгиленген, бирок смена болгон ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Attention: marked as “{TimesheetMarksService.StatusName(mark!.Status).ToLowerInvariant()}”, but there was a shift ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Dikkat: «{TimesheetMarksService.StatusName(mark!.Status).ToLowerInvariant()}» işaretli, ama vardiya vardı ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Diqqat: «{TimesheetMarksService.StatusName(mark!.Status).ToLowerInvariant()}» deb belgilangan, lekin smena bo'lgan ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))})."),
        TimesheetCheck.HoursDiffer => Tr.T($"Внимание: часы в отметке ({TimesheetMarksService.HoursText(mark!.Hours ?? 0)}) расходятся со сменами ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Көңүл буруңуз: белгидеги саат ({TimesheetMarksService.HoursText(mark!.Hours ?? 0)}) смена менен дал келбейт ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Attention: the marked hours ({TimesheetMarksService.HoursText(mark!.Hours ?? 0)}) differ from the shifts ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}).",
            $"Dikkat: işaretteki saat ({TimesheetMarksService.HoursText(mark!.Hours ?? 0)}) vardiyalarla ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}) uyuşmuyor.",
            $"Diqqat: belgidagi soat ({TimesheetMarksService.HoursText(mark!.Hours ?? 0)}) smenalar ({TimesheetMarksService.HoursText(Math.Round(shiftH, 1))}) bilan mos kelmaydi."),
        _ => null,
    };

    private void BuildLegend(IBrush textSoft)
    {
        LegendPanel.Children.Clear();
        foreach (var (status, color) in StatusColors)
        {
            LegendPanel.Children.Add(new Border
            {
                Background = Tint(color, 0x40), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 8, 4),
                Child = new TextBlock { Text = $"{TimesheetMarksService.StatusCode(status)} — {TimesheetMarksService.StatusName(status)}", FontSize = 12 },
            });
        }
        LegendPanel.Children.Add(new TextBlock
        {
            Text = Tr.T("«~8» — по сменам 8 ч, отметки нет; «!» — расхождение со сменой; курсив — автоотметка кассы; число — часы.", "«~8» — смена боюнча 8 саат, белги жок; «!» — смена менен дал келбейт; курсив — кассанын автобелгиси; сан — саат.",
                "“~8” — 8 h by shifts, no mark; “!” — differs from the shift; italics — the till’s auto mark; a number — hours.", "«~8» — vardiyaya göre 8 sa, işaret yok; «!» — vardiyayla uyuşmuyor; italik — kasanın otomatik işareti; sayı — saat.",
                "«~8» — smena bo'yicha 8 soat, belgi yo'q; «!» — smenaga mos emas; kursiv — kassaning avtobelgisi; son — soat."),
            FontSize = 12, Foreground = textSoft, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, Margin = new Thickness(0, 0, 0, 4),
        });
    }

    // ───────────────────────── правка дня ─────────────────────────

    private void Cell_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string, DateOnly> t } button)
            return;
        ShowEditor(button, t.Item1, t.Item2, t.Item3);
    }

    private void ShowEditor(Control anchor, string key, string name, DateOnly date)
    {
        var mark = MarkOf(key, date);
        var shiftH = ShiftHoursOf(key, date);
        var flyout = new Flyout { Placement = PlacementMode.Bottom };
        var root = new StackPanel { Spacing = 8, Width = 320 };
        root.Children.Add(new TextBlock { Text = $"{name} — {date:dd.MM.yyyy} ({WeekdayShort(date.DayOfWeek)})", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock
        {
            Text = shiftH > 0
                ? Tr.T("По смене: ", "Смена боюнча: ", "By shift: ", "Vardiyaya göre: ", "Smena bo'yicha: ") + TimesheetMarksService.HoursText(Math.Round(shiftH, 1))
                : Tr.T("Смен в этот день нет.", "Бул күнү смена жок.", "No shifts this day.", "O gün vardiya yok.", "Bu kuni smena yo'q."),
            FontSize = 12, Foreground = Res("BrushTextSoft", Brushes.Gray), TextWrapping = TextWrapping.Wrap,
        });

        if (!CanEdit)
        {
            root.Children.Add(new TextBlock { Text = Describe(name, date, mark, shiftH, TimesheetMarksService.Reconcile(mark, shiftH)), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            flyout.Content = root;
            flyout.ShowAt(anchor);
            return;
        }

        var status = mark?.Status ?? TimesheetStatus.Present;
        var warn = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), IsVisible = false };
        var hoursBox = new NumericUpDown
        {
            Minimum = 0, Maximum = 24, Increment = 0.5m, FormatString = "0.##", Width = 140, HorizontalAlignment = HorizontalAlignment.Left,
            Value = (decimal)(mark?.Hours ?? TimesheetMarksService.SuggestHours(shiftH)),
        };
        var hoursLabel = new TextBlock { Text = Tr.T("Часы за день", "Күнүнө саат", "Hours for the day", "Günlük saat", "Kunlik soat"), FontSize = 12 };
        var hoursRow = new StackPanel { Spacing = 4, IsVisible = status == TimesheetStatus.Present, Children = { hoursLabel, hoursBox } };
        var comment = new TextBox { Text = mark?.Comment ?? "", Watermark = Tr.T("Комментарий (по желанию)", "Комментарий (каалоо боюнча)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (ixtiyoriy)"), MaxLength = 200, AcceptsReturn = false };

        void Refresh()
        {
            hoursRow.IsVisible = status == TimesheetStatus.Present;
            var probe = new TimesheetMark { Status = status, Hours = status == TimesheetStatus.Present ? (double?)(hoursBox.Value ?? 0) : null };
            var text = CheckText(TimesheetMarksService.Reconcile(probe, shiftH), probe, shiftH);
            warn.Text = text ?? "";
            warn.IsVisible = text != null;
        }

        var seg = new StackPanel { Spacing = 2 };
        foreach (var (s, color) in StatusColors)
        {
            var radio = new RadioButton
            {
                GroupName = "TimesheetEditStatus", Classes = { "sec-seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsChecked = s == status,
                Content = $"{TimesheetMarksService.StatusCode(s)} — {TimesheetMarksService.StatusName(s)}",
            };
            var picked = s;
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true)
                    return;
                status = picked;
                if (status == TimesheetStatus.Present && (hoursBox.Value ?? 0) == 0)
                    hoursBox.Value = (decimal)TimesheetMarksService.SuggestHours(shiftH);
                Refresh();
            };
            seg.Children.Add(radio);
        }
        root.Children.Add(new Border { Classes = { "sec-segment" }, HorizontalAlignment = HorizontalAlignment.Stretch, Child = seg });
        root.Children.Add(hoursRow);
        root.Children.Add(comment);
        root.Children.Add(warn);
        hoursBox.ValueChanged += (_, _) => Refresh();
        Refresh();

        if (mark is { Auto: true })
            root.Children.Add(new TextBlock
            {
                Text = Tr.T("Отметку поставила касса автоматически; после сохранения она станет ручной.", "Белгини касса автоматтык койгон; сактагандан кийин кол менен коюлган болот.",
                    "The till set this mark automatically; after saving it becomes manual.", "İşareti kasa otomatik koydu; kaydedince elle işaret olur.", "Belgini kassa avtomatik qo'ygan; saqlangach qo'lda qo'yilgan bo'ladi."),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });

        var buttons = new WrapPanel();
        var save = new Button { Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), Classes = { "modal-primary" }, MinHeight = 44, Margin = new Thickness(0, 0, 8, 4) };
        save.Click += (_, _) =>
        {
            var hours = status == TimesheetStatus.Present ? (double?)(hoursBox.Value ?? 0) : null;
            flyout.Hide();
            ApplyMark(key, name, date, status, hours, comment.Text);
        };
        buttons.Children.Add(save);
        if (mark != null)
        {
            var clear = new Button { Content = Tr.T("Снять отметку", "Белгини алып салуу", "Remove mark", "İşareti kaldır", "Belgini olib tashlash"), MinHeight = 44, Margin = new Thickness(0, 0, 8, 4) };
            clear.Click += (_, _) =>
            {
                flyout.Hide();
                ClearMark(key, date);
            };
            buttons.Children.Add(clear);
        }
        var cancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), MinHeight = 44, Margin = new Thickness(0, 0, 0, 4) };
        cancel.Click += (_, _) => flyout.Hide();
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        flyout.Content = root;
        flyout.ShowAt(anchor);
    }

    /// <summary>Поставить ручную отметку и обновить сетку (нажатие «Сохранить»; тесты вызывают напрямую).</summary>
    public void ApplyMark(string key, string name, DateOnly date, TimesheetStatus status, double? hours, string? comment)
    {
        if (!CanEdit)
            return;
        try
        {
            _deps.Marks.SetManual(key, name, date, status, hours, comment, EditorName);
            ReloadMarks();
            RebuildMarks();
            StatusText.Text = Tr.T($"Сохранено: {name}, {date:dd.MM} — {TimesheetMarksService.StatusName(status)}.", $"Сакталды: {name}, {date:dd.MM} — {TimesheetMarksService.StatusName(status)}.",
                $"Saved: {name}, {date:dd.MM} — {TimesheetMarksService.StatusName(status)}.", $"Kaydedildi: {name}, {date:dd.MM} — {TimesheetMarksService.StatusName(status)}.", $"Saqlandi: {name}, {date:dd.MM} — {TimesheetMarksService.StatusName(status)}.");
            var shiftH = ShiftHoursOf(key, date);
            var warn = CheckText(TimesheetMarksService.Reconcile(MarkOf(key, date), shiftH), MarkOf(key, date), shiftH);
            if (warn != null)
                StatusText.Text += " " + warn;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель: отметка не сохранена: {ex.Message}", "STAFF");
            StatusText.Text = Tr.T("Не удалось сохранить отметку.", "Белгини сактоо мүмкүн болгон жок.", "Could not save the mark.", "İşaret kaydedilemedi.", "Belgini saqlab bo'lmadi.");
        }
    }

    public void ClearMark(string key, DateOnly date)
    {
        if (!CanEdit)
            return;
        try
        {
            _deps.Marks.Clear(key, date, EditorName);
            ReloadMarks();
            RebuildMarks();
            StatusText.Text = Tr.T("Отметка снята.", "Белги алынды.", "Mark removed.", "İşaret kaldırıldı.", "Belgi olib tashlandi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель: отметка не снята: {ex.Message}", "STAFF");
            StatusText.Text = Tr.T("Не удалось снять отметку.", "Белгини алып салуу мүмкүн болгон жок.", "Could not remove the mark.", "İşaret kaldırılamadı.", "Belgini olib tashlab bo'lmadi.");
        }
    }

    // ───────────────────────── быстрые действия и журнал ─────────────────────────

    private void MarkAllToday_Click(object? sender, RoutedEventArgs e) => MarkAllToday();
    private void CopyYesterday_Click(object? sender, RoutedEventArgs e) => CopyYesterday();

    /// <summary>«Отметить всех присутствующими за сегодня»: только тем, у кого на сегодня отметки ещё нет (чужие отметки не затираются); часы — по сменам дня, иначе 8 ч.</summary>
    public int MarkAllToday()
    {
        if (!CanEdit)
            return 0;
        var today = Today;
        _year = today.Year;
        _month = today.Month;
        int made = 0, skipped = 0;
        try
        {
            foreach (var (key, name) in VisibleEmployees())
            {
                if (MarkOf(key, today) != null) { skipped++; continue; }
                _deps.Marks.SetManual(key, name, today, TimesheetStatus.Present, TimesheetMarksService.SuggestHours(ShiftHoursOf(key, today)), null, EditorName);
                made++;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель: массовая отметка не завершена: {ex.Message}", "STAFF");
        }
        ReloadMarks();
        RebuildMarks();
        StatusText.Text = Tr.T($"Отмечено присутствующими: {made}; уже были отметки: {skipped}.", $"Келди деп белгиленди: {made}; белги мурунтан бар: {skipped}.",
            $"Marked present: {made}; already marked: {skipped}.", $"Geldi işaretlendi: {made}; zaten işaretli: {skipped}.", $"Keldi deb belgilandi: {made}; avval belgilangan: {skipped}.");
        return made;
    }

    /// <summary>«Скопировать вчера»: на сегодня переносится вчерашняя отметка (статус и часы) тем, у кого сегодня отметки нет.</summary>
    public int CopyYesterday()
    {
        if (!CanEdit)
            return 0;
        var today = Today;
        var yesterday = today.AddDays(-1);
        _year = today.Year;
        _month = today.Month;
        int made = 0, none = 0;
        try
        {
            foreach (var (key, name) in VisibleEmployees())
            {
                if (MarkOf(key, today) != null) continue;
                if (MarkOf(key, yesterday) is not { } y) { none++; continue; }
                _deps.Marks.SetManual(key, name, today, y.Status, y.Hours, y.Comment, EditorName);
                made++;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель: копирование вчера не завершено: {ex.Message}", "STAFF");
        }
        ReloadMarks();
        RebuildMarks();
        StatusText.Text = Tr.T($"Скопировано со вчера: {made}; вчера отметки не было: {none}.", $"Кечээден көчүрүлдү: {made}; кечээ белги болгон эмес: {none}.",
            $"Copied from yesterday: {made}; no mark yesterday: {none}.", $"Dünden kopyalandı: {made}; dün işaret yoktu: {none}.", $"Kechadan nusxalandi: {made}; kecha belgi yo'q edi: {none}.");
        return made;
    }

    private void Journal_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanEdit || sender is not Control anchor)
            return;
        var log = _deps.Marks.Load().Log;
        var list = new StackPanel { Spacing = 6, Width = 560 };
        list.Children.Add(new TextBlock { Text = Tr.T("Журнал изменений отметок (последние 150)", "Белгилердин өзгөрүү журналы (акыркы 150)", "Mark change log (last 150)", "İşaret değişiklik günlüğü (son 150)", "Belgilar o'zgarishi jurnali (oxirgi 150)"), FontWeight = FontWeight.SemiBold });
        if (log.Count == 0)
            list.Children.Add(new TextBlock { Text = Tr.T("Пока изменений не было.", "Азырынча өзгөрүү болгон жок.", "No changes yet.", "Henüz değişiklik yok.", "Hozircha o'zgarish yo'q."), FontSize = 12 });
        foreach (var entry in Enumerable.Reverse(log).Take(150))
        {
            var by = string.IsNullOrWhiteSpace(entry.By) ? Tr.T("авто", "авто", "auto", "otomatik", "avto") : entry.By;
            var action = entry.Action switch
            {
                "clear" => Tr.T("снята отметка", "белги алынды", "mark removed", "işaret kaldırıldı", "belgi olib tashlandi"),
                "auto-arrive" => Tr.T("автоотметка прихода", "келгени автоматтык белгиленди", "auto arrival mark", "otomatik geliş işareti", "avtomatik kelish belgisi"),
                "auto-leave" => Tr.T("автоотметка ухода", "кеткени автоматтык белгиленди", "auto departure mark", "otomatik çıkış işareti", "avtomatik ketish belgisi"),
                _ => Tr.T("отметка", "белги", "mark", "işaret", "belgi"),
            };
            list.Children.Add(new TextBlock
            {
                Text = $"{entry.At.LocalDateTime:dd.MM HH:mm} · {by} · {entry.EmployeeName}, {entry.Date:dd.MM}: {action} {SnapText(entry.Before)} → {SnapText(entry.After)}",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new ScrollViewer { MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list },
        };
        flyout.ShowAt(anchor);
    }

    private static string SnapText(TimesheetSnapshot? s)
    {
        if (s is null)
            return "—";
        var t = TimesheetMarksService.StatusName(s.Status);
        if (s.Status == TimesheetStatus.Present && s.Hours is { } h)
            t += " " + TimesheetMarksService.HoursText(h);
        return s.Auto ? t + " (" + Tr.T("авто", "авто", "auto", "otomatik", "avto") + ")" : t;
    }

    // ───────────────────────── для тестов ─────────────────────────

    /// <summary>Текст всех клеток сетки (для проверок): «имя|день|текст клетки».</summary>
    public IReadOnlyList<Button> MarkCells => TableGrid.GetVisualDescendants().OfType<Button>().ToList();

    public DateOnly ShownMonthStartForTest => new(_year, _month, 1);
}
