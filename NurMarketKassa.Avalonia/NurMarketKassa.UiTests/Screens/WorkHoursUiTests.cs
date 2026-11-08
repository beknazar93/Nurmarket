using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08 (решение владельца «рабочее время сотрудника»): карточка сотрудника с блоком «Рабочее время» и страница «Сотрудники» с настройкой
/// «Выход при бездействии» в Avalonia.Headless. Проверяется: по умолчанию выключено и ничего не меняется при открытии; включение — клик по переключателю;
/// неверное время в карточку не попадает; ночная смена; выключение; владельцу блок не показывается; текст не обрезан на 1024×600 и 1366×768 (ru, ky).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\work-hours-*.png. Сервер не нужен, настройки кассы меняются в памяти и возвращаются.</summary>
[Collection("UiScenarios")]
public sealed class WorkHoursUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static EmployeeAccessCode Cashier() => new() { Name = "Айгуль Сыдыкова", ServerId = "u1", Email = "aigul@test.kg" };

    private static CheckBox Toggle(Window w) =>
        w.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Classes.Contains("ToggleSwitch"));

    private static TextBox TimeBox(Window w, bool from) =>
        w.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == (from ? "09:00" : "18:00"));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var outDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), data.ToArray());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void EmployeeCard_WorkHours_DefaultOff_ThenEnable_Invalid_Overnight_Off_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var card = Cashier();
        var window = new EmployeeDetailDialog(card, null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var tag = $"{width}x{height} {lang}";

            // По умолчанию выключено; открытие карточки её не меняет.
            Toggle(window).IsChecked.Should().NotBe(true, "по умолчанию ограничения нет");
            card.WorkLimitEnabled.Should().BeNull($"открытие карточки ничего не пишет ({tag})");
            WorkSchedule.RuleOf(card).Should().BeNull();

            // Включаем кликом по переключателю: 09:00–18:00, каждый день.
            Toggle(window).BringIntoView();
            UiShot.Settle(2);
            UiDrive.Click(window, Toggle(window));
            UiShot.Settle(4);
            card.WorkLimitEnabled.Should().BeTrue();
            card.WorkFrom.Should().Be("09:00");
            card.WorkTo.Should().Be("18:00");
            card.WorkDays.Should().BeNull("все семь дней — это «каждый день»");
            TimeBox(window, true).IsEffectivelyVisible.Should().BeTrue();

            // Неверное время в карточку не попадает, под полем — ошибка.
            UiDrive.Type(window, TimeBox(window, false), "25:99");
            UiShot.Settle(3);
            card.WorkTo.Should().Be("18:00", "неверное значение не принято");
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "СС:ММ" : "ЧЧ:ММ", "под полем — подсказка о формате времени");

            // Ночная смена 22:00–06:00.
            UiDrive.Type(window, TimeBox(window, true), "22:00");
            UiDrive.Type(window, TimeBox(window, false), "06:00");
            UiShot.Settle(3);
            card.WorkFrom.Should().Be("22:00");
            card.WorkTo.Should().Be("06:00");
            WorkSchedule.RuleOf(card)!.Overnight.Should().BeTrue();

            // Не только 24:00, но и «с = до» отвергается.
            UiDrive.Type(window, TimeBox(window, false), "22:00");
            card.WorkTo.Should().Be("06:00", "«с» и «до» не должны совпадать");
            UiDrive.Type(window, TimeBox(window, false), "18:00");
            UiDrive.Type(window, TimeBox(window, true), "09:00");

            // Дни: снимаем субботу и воскресенье.
            var dayBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => !c.Classes.Contains("ToggleSwitch")).ToList();
            var names = WorkSchedule.DayNames();
            foreach (var d in new[] { 6, 0 })
            {
                var dayBox = dayBoxes.Single(c => (string?)c.Content == names[d]);
                dayBox.BringIntoView();
                UiShot.Settle(2);
                UiDrive.Click(window, dayBox);
            }
            card.WorkDays.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
            WorkSchedule.Evaluate(WorkSchedule.RuleOf(card), new DateTime(2026, 11, 8, 12, 0, 0)).Verdict.Should().Be(WorkTimeVerdict.Outside, "воскресенье не рабочий день");

            var clipped = TextClipChecker.Find(window, "employee-work-hours", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            SaveShot(window, $"work-hours-card-{langCode}-{width}x{height}");

            // Выключаем: время остаётся в карточке, но правила нет.
            Toggle(window).BringIntoView();
            UiShot.Settle(2);
            UiDrive.Click(window, Toggle(window));
            UiShot.Settle(3);
            card.WorkLimitEnabled.Should().BeFalse();
            WorkSchedule.RuleOf(card).Should().BeNull("выключено — ограничения нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void EmployeeCard_ForOwner_ShowsNoEditor()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        EmployeeDirectory.Update([new EmployeeInfoDto { Id = "u9", RoleCode = "owner" }]);
        try
        {
            var card = new EmployeeAccessCode { Name = "Владелец", ServerId = "u9" };
            var window = new EmployeeDetailDialog(card, null);
            try
            {
                using var _ = UiShot.Render(window, new UiSize(1366, 768));
                UiShot.Settle(4);
                window.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Classes.Contains("ToggleSwitch")).Should().BeFalse("владельцу рабочее время не задаётся");
                UiDrive.AllText(window).Should().Contain("не ограничивается");
            }
            finally
            {
                window.Close();
                UiShot.Pump();
            }
        }
        finally { EmployeeDirectory.Invalidate(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void EmployeesPage_IdleLogoutSetting_SavesValue_RejectsGarbage_AndFits(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        var oldIdle = prefs.IdleLogoutMinutes;
        var oldList = prefs.EmployeeAccessCodes;
        var oldCompany = prefs.EmployeeAccessCodesCompanyId;
        prefs.IdleLogoutMinutes = null;
        prefs.EmployeeAccessCodesCompanyId = null;
        prefs.EmployeeAccessCodes = new List<EmployeeAccessCode>
        {
            new() { Name = "Айгуль Сыдыкова", ServerId = "u1", WorkLimitEnabled = true, WorkFrom = "09:00", WorkTo = "18:00", WorkDays = new List<int> { 1, 2, 3, 4, 5 } },
            new() { Name = "Бекзат Жумабеков", ServerId = "u2" },
        };
        var window = new Window { Content = new EmployeesSettingsView() };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var tag = $"{width}x{height} {lang}";
            var box = UiDrive.Find<TextBox>(window, "IdleMinutesBox");
            box.Text.Should().Be("60", "по умолчанию — 60 минут");
            UiDrive.AllText(window).Should().Contain("09:00–18:00", "рабочее время кассира видно в списке");

            // Новое значение — Enter в поле сохраняет
            UiDrive.Type(window, box, "45");
            Enter(window);
            prefs.IdleLogoutMinutes.Should().Be(45);

            // Мусор не принимается, значение возвращается
            UiDrive.Type(window, box, "abc");
            Enter(window);
            prefs.IdleLogoutMinutes.Should().Be(45);
            box.Text.Should().Be("45");
            UiDrive.Find<TextBlock>(window, "IdleError").IsVisible.Should().BeTrue();

            // 0 — «не выходить»
            UiDrive.Type(window, box, "0");
            Enter(window);
            prefs.IdleLogoutMinutes.Should().Be(0);
            WorkSchedule.IdleLogoutMinutes().Should().Be(0);
            UiDrive.Type(window, box, "45");
            Enter(window);

            var clipped = TextClipChecker.Find(window, "employees-idle", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            SaveShot(window, $"work-hours-employees-{langCode}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            prefs.IdleLogoutMinutes = oldIdle;
            prefs.EmployeeAccessCodes = oldList;
            prefs.EmployeeAccessCodesCompanyId = oldCompany;
        }
    }

    private static void Enter(Window w)
    {
        w.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        w.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        UiShot.Pump();
    }
}
