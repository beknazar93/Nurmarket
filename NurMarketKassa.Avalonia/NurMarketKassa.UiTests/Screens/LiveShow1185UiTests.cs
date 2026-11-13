using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (живой показ 1.17.85 на настоящей программе, дефекты Д-4, Д-7…Д-12, п. 1е в приёмке): окна в Avalonia.Headless без сервера.
/// Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\liveshow-*.png.</summary>
[Collection("UiScenarios")]
public sealed class LiveShow1185UiTests
{
    private static void Save(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"liveshow-{name}.png"), data.ToArray());
    }

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    // ---------------------------------------------------------------- Д-4: табель разделом владельца

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Timesheet_as_owner_section_has_a_title_no_empty_footer_and_fills_the_height(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, lang);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-ui-timesheet-" + Guid.NewGuid().ToString("N")[..8]);
        IReadOnlyList<ShiftHistoryEntry> shifts =
        [
            new() { CashierId = "u1", Cashier = "Nur Test", OpenedAt = new DateTime(2026, 11, 10, 8, 0, 0), ClosedAt = new DateTime(2026, 11, 12, 1, 36, 0) },
        ];
        var deps = new TimesheetDeps
        {
            Shifts = _ => Task.FromResult(shifts),
            Employees = _ => Task.FromResult<IReadOnlyList<(string Key, string Name)>>([("u1", "Nur Test")]),
            Marks = new TimesheetMarksService(Path.Combine(dir, "timesheet-marks.json")),
            Today = () => new DateTime(2026, 11, 11),
            CanEdit = true,
        };
        var window = new StaffTimesheetWindow(deps);
        window.AsOwnerSection();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(6);
            var shell = UiDrive.Find<ModalShell>(window, "Shell");
            shell.Title.Should().NotBeNullOrWhiteSpace($"шапка не пустая — «Табель сотрудников» ({tag})");
            var titleText = shell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == shell.Title);
            titleText.Should().NotBeNull();
            titleText!.IsEffectivelyVisible.Should().BeTrue();
            var footer = shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PART_FooterGrid");
            footer.IsEffectivelyVisible.Should().BeFalse($"пустой подвал в разделе не рисуется ({tag})");
            var top = shell.TranslatePoint(default, window)!.Value.Y;
            top.Should().BeLessThanOrEqualTo(20, $"карточка табеля сразу под заголовком раздела, без чёрного поля сверху ({tag})");
            (top + shell.Bounds.Height).Should().BeGreaterThan(window.ClientSize.Height - 40, $"карточка — на всю высоту раздела ({tag})");

            // Д-2: «~» в клетках — не больше 24 ч
            var cellTexts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.StartsWith('~')).ToList();
            cellTexts.Should().NotBeEmpty();
            foreach (var t in cellTexts)
                double.Parse(t[1..].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThanOrEqualTo(24, $"«{t}» — в сутках не больше 24 ч ({tag})");
            Save(window, $"timesheet-owner-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- Д-7: номер прошлого возврата

    [AvaloniaFact]
    public async Task Customer_return_label_never_shows_a_piece_of_the_technical_id()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var at = new DateTimeOffset(2026, 11, 11, 1, 51, 0, TimeSpan.FromHours(6));
        var id = "7328e9cf-1111-2222-3333-444455556666";
        var noNumbers = ReturnEditFlows.CustomerNumber(new NurCrmReportsApi.PosReturn(id, "s1", null, 170m, false, null, null, at));
        noNumbers.Should().NotContain("7328").And.NotContain("e9cf");
        noNumbers.Should().Contain(at.LocalDateTime.ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        ReturnEditFlows.CustomerNumber(new NurCrmReportsApi.PosReturn(id, "s1", 2094, 170m, false, null, null, at)).Should().StartWith("№002094");
        ReturnEditFlows.CustomerNumber(new NurCrmReportsApi.PosReturn(id, "s1", 2094, 170m, false, null, null, at, Number: "15")).Should().StartWith("№15");
        ReturnEditFlows.CustomerNumber(new NurCrmReportsApi.PosReturn(id, "s1", null, 170m, false, null, null, null)).Should().NotContain("7328");
    }

    // ---------------------------------------------------------------- Д-8: кнопки «Возврат» в колонке «Продаж» на одной вертикали

    [AvaloniaFact]
    public async Task Sales_return_buttons_are_right_aligned_with_or_without_a_return_mark()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var sales = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SalesWindow>(TestHost.Services);
        var build = typeof(SalesWindow).GetMethod("BuildReturnCell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var plain = (Control)build.Invoke(sales, null)!;
        var marked = (Control)build.Invoke(sales, null)!;
        plain.DataContext = new SalesWindow.SaleItem { Id = "a", ReceiptNumber = "002096", Status = "paid" };
        marked.DataContext = new SalesWindow.SaleItem { Id = "b", ReceiptNumber = "002094", Status = "paid", ReturnStatus = "partial" };
        var column = new StackPanel { Width = 240 };
        column.Children.Add(plain);
        column.Children.Add(marked);
        var host = new Window { Width = 400, Height = 200, Content = column };
        try
        {
            host.Show();
            UiShot.Settle(3);
            double RightEdge(Control cell)
            {
                var b = cell.GetVisualDescendants().OfType<Button>().Single();
                return b.TranslatePoint(new Point(b.Bounds.Width, 0), host)!.Value.X;
            }
            Math.Abs(RightEdge(plain) - RightEdge(marked)).Should().BeLessThan(0.5, "кнопки «Возврат» на одной вертикали");
        }
        finally
        {
            host.Close();
            sales.Close();
            UiShot.Pump();
        }
    }

    // ---------------------------------------------------------------- Д-9: фильтры возврата компактно

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Filter_bar_keeps_amount_from_and_to_together_and_reset_with_the_counter(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var bar = new SaleFilterBar(showPeriod: true);
        bar.SetCount(3, 35);
        var host = new Window { Width = 780, Height = 260, Content = new Border { Padding = new Thickness(16), Child = bar } };
        try
        {
            host.Show();
            UiShot.Settle(3);
            TextBox Box(string fieldName) => (TextBox)typeof(SaleFilterBar).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
            double Y(Control c) => c.TranslatePoint(default, host)!.Value.Y;
            var reset = (Button)typeof(SaleFilterBar).GetField("_reset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
            var count = (TextBlock)typeof(SaleFilterBar).GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
            Y(Box("_from")).Should().Be(Y(Box("_to")), "«Сумма от» и «до» — в одной строке");
            Math.Abs(Y(reset) - (Y(count) - (reset.Bounds.Height - count.Bounds.Height) / 2)).Should().BeLessThan(30, "«Сбросить» и счётчик — вместе");
            Save(host, $"return-filters-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}");
        }
        finally
        {
            host.Close();
            UiShot.Pump();
        }
    }

    // ---------------------------------------------------------------- Д-10: «История чеков» по центру окна кассы

    [AvaloniaFact]
    public async Task Receipt_history_is_centered_on_the_kassa_window()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var root = new Window { Width = 1366, Height = 768, Position = new PixelPoint(0, 0) };
        var history = new ReceiptHistoryWindow { Width = 1300, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(0, 0) };
        try
        {
            root.Show();
            history.Show();
            UiShot.Settle(3);
            history.CenterOnRoot(root);
            UiShot.Settle(2);
            var scale = root.RenderScaling;
            var origin = root.PointToScreen(default);
            history.Position.X.Should().BeCloseTo(origin.X + (int)((root.ClientSize.Width - history.Bounds.Width) * scale / 2), 2);
            history.Position.Y.Should().BeCloseTo(origin.Y + (int)((root.ClientSize.Height - history.Bounds.Height) * scale / 2), 2);
        }
        finally
        {
            history.Close();
            root.Close();
            UiShot.Pump();
        }
    }

    // ---------------------------------------------------------------- Д-12: имя флажка строки возврата

    [AvaloniaFact]
    public async Task Return_line_checkbox_has_an_accessible_name()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var line = new ReturnSaleLineVm { LineId = "l1", Title = "Макароны «Макфа» спагетти 450г", SubLine = "4 × 85,00 сом", LineSumText = "340,00 сом" };
        line.SelectCheckName.Should().Be("Вернуть: Макароны «Макфа» спагетти 450г");
    }

    // ---------------------------------------------------------------- п. 1е: приёмка склада — подсказка о контрольной цифре

    private sealed class Prompts : IUserPrompts
    {
        public List<string> Questions { get; } = new();
        public bool Answer { get; set; }
        public Task<bool> ConfirmAsync(string message) { Questions.Add(message); return Task.FromResult(Answer); }
        public Task<bool> ConfirmAsync(string message, string? confirmText, string? cancelText = null, bool danger = false) => ConfirmAsync(message);
        public void ShowToast(string message, bool isWarning = false) { }
        public void ShowWarning(string message) { }
        public void ShowError(string message) { }
        public Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword) => Task.FromResult(true);
        public Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator) => Task.FromResult(true);
    }

    [AvaloniaFact]
    public async Task Warehouse_receiving_scan_with_a_wrong_check_digit_asks_to_rescan_and_add_anyway_adds_the_line()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        const string misread = "4700004710079"; // верная контрольная цифра — 8
        var rescan = new Prompts { Answer = false };
        var vm = new WarehouseViewModel(null, rescan);
        await vm.HandleReceivingScanAsync(misread);
        rescan.Questions.Should().ContainSingle().Which.Should().Be(ScanMessages.CheckDigitHint);
        vm.ReceivingLines.Should().BeEmpty("«Отсканирую ещё раз» — строка не добавлена");

        var addAnyway = new Prompts { Answer = true };
        var vm2 = new WarehouseViewModel(null, addAnyway);
        await vm2.HandleReceivingScanAsync(misread);
        addAnyway.Questions.Should().ContainSingle();
        vm2.ReceivingLines.Should().ContainSingle(l => l.Barcode == misread, "«Всё равно добавить» — строка нового товара с этим кодом");
    }
}
