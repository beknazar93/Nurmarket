using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// 2026-11-08, решение владельца («рабочее время сотрудника»): блок «Рабочее время» в карточке сотрудника (Настройки → Сотрудники → клик по сотруднику).
/// Переключатель «Ограничить рабочее время» (по умолчанию выключен у всех), поля «с / до» (ЧЧ:ММ, местные часы ПК; «до» раньше «с» — ночная смена),
/// дни недели. Правки применяются к карточке «на лету», как коды рядом; сохраняет страница «Сотрудники» (кнопка «Сохранить» и MarkCodesChanged).
/// Неверные поля в карточку не попадают — под полем ошибка, прежние значения остаются. Само правило и его применение — WorkSchedule/WorkTimeMonitor.
/// </summary>
internal static class WorkHoursEditor
{
    internal const string DefaultFrom = "09:00";
    internal const string DefaultTo = "18:00";

    public static Control Build(EmployeeAccessCode employee)
    {
        var panel = new StackPanel { Spacing = 8 };

        TextBlock Note(string text)
        {
            var note = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));
            return note;
        }

        var title = new TextBlock
        {
            Text = Tr.T("Рабочее время", "Иш убактысы", "Working hours", "Çalışma saatleri", "Ish vaqti"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(title);

        // Владельцу и администратору рабочее время не задаётся: ограничение к ним не применяется (WorkSchedule.CurrentRule).
        var role = EmployeeDirectory.TryGet(employee.ServerId)?.RoleCode;
        if (string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(Note(Tr.T("Владельцу и администратору рабочее время не ограничивается.",
                "Ээсине жана администраторго иш убактысы чектелбейт.",
                "Working hours are not limited for the owner and administrators.",
                "Sahip ve yöneticiler için çalışma saati sınırlanmaz.",
                "Egasi va administratorga ish vaqti cheklanmaydi.")));
            return panel;
        }

        var toggle = new CheckBox
        {
            Content = Tr.T("Ограничить рабочее время", "Иш убактысын чектөө", "Limit working hours", "Çalışma saatlerini sınırla", "Ish vaqtini cheklash"),
            IsChecked = employee.WorkLimitEnabled == true,
            MinHeight = 40,
        };
        toggle.Classes.Add("ToggleSwitch");
        Avalonia.Automation.AutomationProperties.SetName(toggle, (string)toggle.Content);
        panel.Children.Add(toggle);

        var hint = Note(Tr.T("Выключено — ограничения нет: смены открывают и закрывают вручную. Включено — кассир входит и открывает смену только в это время; за 10 минут до конца касса предупредит, а при бездействии (настройка «Выход при бездействии» на странице «Сотрудники») выйдет из аккаунта. Чек и смена не пропадают.",
            "Өчүк — чектөө жок: сменаны кол менен ачып-жабышат. Күйгүзүлгөн — кассир ушул убакытта гана кирет жана сменаны ачат; аягына 10 мүнөт калганда касса эскертет, ал эми иштебей калганда («Кызматкерлер» барагындагы «Иштебей калганда чыгуу» жөндөөсү) аккаунттан чыгат. Чек жана смена жоголбойт.",
            "Off — no limit: shifts are opened and closed manually. On — the cashier can sign in and open a shift only at this time; the till warns 10 minutes before the end and signs out on inactivity (the “Sign out when idle” setting on the “Employees” page). The receipt and the shift are never lost.",
            "Kapalı — sınır yok: vardiyalar elle açılıp kapatılır. Açık — kasiyer yalnızca bu saatlerde giriş yapar ve vardiya açar; bitişten 10 dakika önce kasa uyarır, işlem yapılmazsa («Personel» sayfasındaki «Boşta çıkış» ayarı) hesaptan çıkar. Fiş ve vardiya kaybolmaz.",
            "O'chiq — cheklov yo'q: smenalar qo'lda ochiladi va yopiladi. Yoqilgan — kassir faqat shu vaqtda kiradi va smena ochadi; tugashiga 10 daqiqa qolganda kassa ogohlantiradi, harakatsizlikda («Xodimlar» sahifasidagi «Harakatsizlikda chiqish» sozlamasi) akkauntdan chiqadi. Chek va smena yo'qolmaydi."));
        panel.Children.Add(hint);

        var details = new StackPanel { Spacing = 8, IsVisible = toggle.IsChecked == true };
        panel.Children.Add(details);

        TextBox TimeBox(string text, string watermark)
        {
            var box = SectionCards.Field(new TextBox { Text = text, Watermark = watermark, MaxLength = 5, Width = 110, HorizontalAlignment = HorizontalAlignment.Left });
            return box;
        }

        var fromBox = TimeBox(string.IsNullOrWhiteSpace(employee.WorkFrom) ? DefaultFrom : employee.WorkFrom!, DefaultFrom);
        var toBox = TimeBox(string.IsNullOrWhiteSpace(employee.WorkTo) ? DefaultTo : employee.WorkTo!, DefaultTo);
        var fromLabel = Note(Tr.T("с", "баштап", "from", "başlangıç", "dan"));
        var toLabel = Note(Tr.T("до", "чейин", "to", "bitiş", "gacha"));
        Avalonia.Automation.AutomationProperties.SetName(fromBox, Tr.T("Рабочее время с", "Иш убактысы баштап", "Working hours from", "Çalışma saati başlangıç", "Ish vaqti dan"));
        Avalonia.Automation.AutomationProperties.SetName(toBox, Tr.T("Рабочее время до", "Иш убактысы чейин", "Working hours to", "Çalışma saati bitiş", "Ish vaqti gacha"));
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        fromLabel.VerticalAlignment = VerticalAlignment.Center;
        toLabel.VerticalAlignment = VerticalAlignment.Center;
        timeRow.Children.Add(fromLabel);
        timeRow.Children.Add(fromBox);
        timeRow.Children.Add(toLabel);
        timeRow.Children.Add(toBox);
        details.Children.Add(timeRow);

        // Дни недели: Пн … Вс (индексы System.DayOfWeek: Пн = 1 … Сб = 6, Вс = 0).
        var dayOrder = new[] { 1, 2, 3, 4, 5, 6, 0 };
        var names = WorkSchedule.DayNames();
        var daysRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var dayBoxes = new Dictionary<int, CheckBox>();
        var selected = employee.WorkDays is { Count: > 0 } d ? d.Where(x => x is >= 0 and <= 6).ToHashSet() : new HashSet<int>(dayOrder);
        foreach (var day in dayOrder)
        {
            var box = new CheckBox { Content = names[day], IsChecked = selected.Contains(day), Margin = new Thickness(0, 0, 14, 4), MinHeight = 36 };
            dayBoxes[day] = box;
            daysRow.Children.Add(box);
        }
        details.Children.Add(Note(Tr.T("Рабочие дни (ночная смена относится ко дню начала):", "Иш күндөрү (түнкү смена башталган күнгө таандык):",
            "Working days (a night shift belongs to the day it starts):", "Çalışma günleri (gece vardiyası başladığı güne aittir):",
            "Ish kunlari (tungi smena boshlangan kunga tegishli):")));
        details.Children.Add(daysRow);

        var summary = Note("");
        details.Children.Add(summary);
        var error = SectionCards.ErrorText(panel);
        details.Children.Add(error);

        void ShowSummary(WorkRule? rule) => summary.Text = rule is null ? "" : WorkSchedule.HoursLine(rule);

        bool loading = true;

        void Apply()
        {
            if (loading)
                return;
            if (toggle.IsChecked != true)
            {
                // Выключено: время и дни остаются в карточке (включил снова — те же), но правило не действует.
                employee.WorkLimitEnabled = employee.WorkFrom is null && employee.WorkTo is null ? null : false;
                details.IsVisible = false;
                return;
            }
            details.IsVisible = true;
            string? problem = null;
            var okFrom = WorkSchedule.TryParseTime(fromBox.Text, out var from);
            var okTo = WorkSchedule.TryParseTime(toBox.Text, out var to);
            if (!okFrom || !okTo)
                problem = Tr.T("Время — в виде ЧЧ:ММ, например 09:00.", "Убакыт — СС:ММ түрүндө, мисалы 09:00.", "Enter the time as HH:MM, for example 09:00.",
                    "Saati SS:DD biçiminde girin, örneğin 09:00.", "Vaqtni SS:DD ko'rinishida kiriting, masalan 09:00.");
            else if (from == to)
                problem = Tr.T("«С» и «до» не должны совпадать.", "«Баштап» жана «чейин» дал келбеши керек.", "“From” and “to” must differ.",
                    "Başlangıç ve bitiş aynı olamaz.", "«Dan» va «gacha» bir xil bo'lmasligi kerak.");
            var days = dayOrder.Where(x => dayBoxes[x].IsChecked == true).ToList();
            if (problem is null && days.Count == 0)
                problem = Tr.T("Отметьте хотя бы один рабочий день.", "Жок дегенде бир иш күнүн белгилеңиз.", "Select at least one working day.",
                    "En az bir çalışma günü seçin.", "Kamida bitta ish kunini belgilang.");
            SectionCards.ShowError(error, problem);
            if (problem is not null)
            {
                summary.Text = "";
                return; // неверное в карточку не попадает; прежнее правило остаётся как было
            }
            employee.WorkLimitEnabled = true;
            employee.WorkFrom = WorkSchedule.FormatTime(from);
            employee.WorkTo = WorkSchedule.FormatTime(to);
            employee.WorkDays = days.Count == 7 ? null : days;
            ShowSummary(WorkSchedule.RuleOf(employee));
        }

        toggle.IsCheckedChanged += (_, _) => Apply();
        fromBox.TextChanged += (_, _) => Apply();
        toBox.TextChanged += (_, _) => Apply();
        foreach (var box in dayBoxes.Values)
            box.IsCheckedChanged += (_, _) => Apply();

        loading = false;
        // Открытие карточки ничего в ней не меняет: только показываем, как правило звучит сейчас.
        if (toggle.IsChecked == true)
            ShowSummary(WorkSchedule.RuleOf(employee));
        return panel;
    }
}
