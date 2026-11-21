using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21, жалоба владельца («Продажи и финансы»: «снизу все чеки, они открываются сразу при одном нажатии»): список чеков программы
/// владельца. Одно нажатие по строке чек НЕ открывает (только выделяет строку) и не трогает остальные; чек открывается явно — двойным щелчком по строке
/// или кнопкой-стрелкой «Открыть чек» в строке, и открывается ровно один — тот, по строке которого нажали.</summary>
[Collection("UiScenarios")]
public sealed class ReceiptRowOpenUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private readonly bool _wasOwner = AppMode.IsOwner;

    public ReceiptRowOpenUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = null;
        IsOwnerProp.SetValue(null, true);
    }

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _wasOwner);
        PosApp.ActiveShiftId = _savedShift;
    }

    private static string Row(string id, int number, string total) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"total\":\"" + total
        + "\",\"payment_method\":\"cash\",\"created_at\":\"" + DateTimeOffset.Now.AddMinutes(-5).ToString("O") + "\",\"first_item_name\":\"Сахар\",\"user_display\":\"Айбек\"}";

    private static string Detail(string id, int number, string total) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"payment_method\":\"cash\",\"total\":\"" + total
        + "\",\"created_at\":\"" + DateTimeOffset.Now.AddMinutes(-5).ToString("O") + "\",\"items\":[{\"id\":\"si-" + number + "\",\"product_id\":\"p-" + number
        + "\",\"name\":\"Сахар\",\"quantity\":\"1\",\"unit_price\":\"" + total + "\",\"total\":\"" + total + "\",\"returnable_qty\":1}]}";

    private static void StubSales()
    {
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":3,\"next\":null,\"previous\":null,\"results\":["
            + Row("s-801", 3801, "100.00") + "," + Row("s-802", 3802, "200.00") + "," + Row("s-803", 3803, "300.00") + "]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-801/", 200, Detail("s-801", 3801, "100.00"));
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-802/", 200, Detail("s-802", 3802, "200.00"));
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-803/", 200, Detail("s-803", 3803, "300.00"));
        UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[]");
    }

    private static bool RunUntil(Func<bool> condition, int ms = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource();
            using var stop = DispatcherTimer.RunOnce(() => cts.Cancel(), TimeSpan.FromMilliseconds(80));
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return condition();
    }

    private static async Task<SalesWindow> OpenWindowAsync()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("owner@test.kg", "12345678");
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        window.AsOwnerSection();
        UiShot.Render(window, new UiSize(1366, 768));
        RunUntil(() => Rows(window).Count == 3).Should().BeTrue("три чека загружены в список");
        UiShot.Settle(3, 30);
        // 2026-11-21: список в фоне сам догружает состав каждого чека (LoadReceiptContentsAsync — по одному GET на чек, так и задумано) —
        // ждём, пока он закончится, и считаем запросы от этой точки: «открылся ли чек» = лишний GET /sales/{id}/ сверх фонового.
        RunUntil(() => Detail("s-801") >= 1 && Detail("s-802") >= 1 && Detail("s-803") >= 1, 5000);
        RunUntil(() => false, 600);
        _base = (Detail("s-801"), Detail("s-802"), Detail("s-803"));
        return window;
    }

    private static List<DataGridRow> Rows(SalesWindow w) =>
        w.FindControl<DataGrid>("SalesGrid")!.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.DataContext is SalesWindow.SaleItem && r.IsVisible).ToList(); // 2026-11-21: DataGrid держит скрытую переиспользуемую строку (Bounds 0x0, IsVisible=false) — её не считаем

    private static DataGridRow RowOf(SalesWindow w, string id) => Rows(w).Single(r => ((SalesWindow.SaleItem)r.DataContext!).Id == id);

    private static (int, int, int) _base;
    private static int Opened(string id) => Detail(id) - (id == "s-801" ? _base.Item1 : id == "s-802" ? _base.Item2 : _base.Item3);
    private static int Detail(string id) => Requests(UiFake.Server, "GET", "/api/main/pos/sales/" + id + "/").Count;

    private static void ClickFirstCell(SalesWindow w, string id)
    {
        var cell = RowOf(w, id).GetVisualDescendants().OfType<DataGridCell>().First();
        UiDrive.Click(w, cell);
    }

    [AvaloniaFact]
    public async Task SingleTap_OnRow_OnlySelectsIt_NoReceiptOpens()
    {
        var window = await OpenWindowAsync();
        try
        {
            var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
            ClickFirstCell(window, "s-802");
            RunUntil(() => false, 700); // если бы нажатие открывало чек — запрос и карточка появились бы здесь
            popup.IsOpen.Should().BeFalse("одно нажатие чек не открывает");
            Opened("s-801").Should().Be(0);
            Opened("s-802").Should().Be(0, "ни свой чек, ни чужие одним нажатием не грузятся");
            Opened("s-803").Should().Be(0);
            window.FindControl<DataGrid>("SalesGrid")!.SelectedItem.Should().Match<object>(i => ((SalesWindow.SaleItem)i).Id == "s-802", "нажатая строка выделена");

            // нажатие по другой строке — снова только выделение
            ClickFirstCell(window, "s-803");
            RunUntil(() => false, 500);
            popup.IsOpen.Should().BeFalse();
            Opened("s-803").Should().Be(0);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OpenButton_InRow_OpensExactlyThatReceipt_Only()
    {
        var window = await OpenWindowAsync();
        try
        {
            var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
            var button = RowOf(window, "s-802").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenReceiptButton");
            (button.Bounds.Width >= 40 && button.Bounds.Height >= 40).Should().BeTrue($"кнопка «Открыть чек» не меньше 40 px ({button.Bounds.Width}x{button.Bounds.Height})");
            Avalonia.Automation.AutomationProperties.GetName(button).Should().Be("Открыть чек");

            UiDrive.Click(window, button);
            RunUntil(() => popup.IsOpen).Should().BeTrue("явное действие открывает чек");
            window.FindControl<TextBlock>("PopupTitle")!.Text.Should().Contain("3802", "открыт именно чек этой строки");
            Opened("s-802").Should().Be(1);
            Opened("s-801").Should().Be(0, "остальные чеки не открываются");
            Opened("s-803").Should().Be(0);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task DoubleClick_OnRow_OpensThatReceipt_NotTheSelectedOne()
    {
        var window = await OpenWindowAsync();
        try
        {
            var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
            var grid = window.FindControl<DataGrid>("SalesGrid")!;
            grid.SelectedItem = Rows(window).Select(r => (SalesWindow.SaleItem)r.DataContext!).Single(s => s.Id == "s-801"); // выбрана другая строка
            var cell = RowOf(window, "s-803").GetVisualDescendants().OfType<DataGridCell>().First();
            var p = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left); // второй щелчок подряд — двойной
            window.MouseUp(p, MouseButton.Left);
            RunUntil(() => popup.IsOpen).Should().BeTrue("двойной щелчок открывает чек");
            window.FindControl<TextBlock>("PopupTitle")!.Text.Should().Contain("3803", "открыта строка под щелчком, а не выбранная раньше");
            Opened("s-803").Should().Be(1);
            Opened("s-801").Should().Be(0);
            Opened("s-802").Should().Be(0);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
