using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (владелец: «корректировка возвратных накладных от клиентов и от поставщиков!»; аудит 12.11, пакет П-4): места входа к возврату и к правке
/// возвратов в программе владельца. «Продажи и финансы → Возвраты»: кнопка «Править возврат» у каждой строки с чеком, без признака сервера — пояснение «почему нельзя»;
/// карточка чека — «Возврат» и «Правка возвратов» в один ряд (Н-6), без права — пояснение (Н-9); окно возврата без смены — постоянное напоминание (С-30);
/// «Сводка» — видимая кнопка «⋯» у строки продажи (С-19); «Печать» открывается и при скрытых «Настройках» (В-11), подсвечивается сама (С-21).</summary>
[Collection("UiScenarios")]
public sealed class OwnerReturnEntryUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private readonly bool _wasOwner = AppMode.IsOwner;

    public OwnerReturnEntryUiTests()
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

    private static string At(int hoursAgo = 0) => DateTimeOffset.Now.AddHours(-hoursAgo).AddMinutes(-5).ToString("O");

    private static void StubSales()
    {
        var row = "{\"id\":\"s-801\",\"number\":3801,\"status\":\"paid\",\"return_status\":\"partial\",\"total\":\"340.00\",\"payment_method\":\"cash\",\"created_at\":\"" + At()
                  + "\",\"first_item_name\":\"Сахар\",\"user_display\":\"Айбек\"}";
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[" + row + "]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-801/", 200, "{\"id\":\"s-801\",\"number\":3801,\"status\":\"paid\",\"return_status\":\"partial\",\"payment_method\":\"cash\",\"total\":\"340.00\",\"created_at\":\""
            + At() + "\",\"items\":[{\"id\":\"si-1\",\"product_id\":\"p-1\",\"name\":\"Сахар\",\"quantity\":\"2.5\",\"unit_price\":\"100\",\"total\":\"250\",\"returnable_qty\":1.25},"
            + "{\"id\":\"si-2\",\"product_id\":\"p-2\",\"name\":\"Хлеб\",\"quantity\":\"2\",\"unit_price\":\"45\",\"total\":\"90\",\"returnable_qty\":2}]}");
        // возврат без признаков правки (сервер без pos_return_edit_24h)
        UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[{\"id\":\"pr-9\",\"sale\":\"s-801\",\"sale_number\":3801,\"amount\":\"125.00\",\"created_at\":\"" + At(1)
            + "\",\"reason\":\"Не подошёл товар\",\"items\":[{\"name\":\"Сахар\",\"qty\":\"1.25\"}]}]");
    }

    private static async Task<NurMarketApiClient> LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("owner@test.kg", "12345678");
        return api;
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

    private static IEnumerable<Window> Walk(IEnumerable<Window> roots)
    {
        foreach (var w in roots)
        {
            yield return w;
            foreach (var c in Walk(w.OwnedWindows))
                yield return c;
        }
    }

    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Отвечает на подтверждения «да», на сообщения — «Понятно»; тексты сообщений — в списке. Перед остановкой 2 с ловит поздние окна.</summary>
    private sealed class Pilot : IDisposable
    {
        private readonly Window _root;
        private readonly HashSet<Window> _seen = new();
        private readonly DispatcherTimer _timer;
        public List<string> Alerts { get; } = new();

        public Pilot(Window root)
        {
            _root = root;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            foreach (var w in Walk([_root]).ToList())
            {
                if (!w.IsVisible || !_seen.Add(w))
                    continue;
                if (w is PosConfirmDialog c)
                    Press(c.FindControl<Button>("ConfirmButton")!);
                else if (w is PosAlertDialog a)
                {
                    Alerts.Add(UiDrive.AllText(a));
                    Press(a.FindControl<Button>("OkButton")!);
                }
                else
                    _seen.Remove(w);
            }
        }

        public void Dispose()
        {
            RunUntil(() => false, 2000);
            _timer.Stop();
        }
    }

    // ───────────── «Продажи и финансы → Возвраты»: «Править возврат» у каждой строки ─────────────

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task FinanceReturnsTab_Owner_EditReturnOnEveryRow_FeatureOff_ExplainsWhy(AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        StubSales();
        await LoginAsync();
        ServerCapabilities.Reset();
        var finance = TestHost.Services.GetRequiredService<FinanceWindow>();
        typeof(FinanceWindow).GetMethod("AsSalesAndFinance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finance, null);
        finance.AsOwnerSection();
        UiShot.Render(finance, new UiSize(1366, 768));
        using var pilot = new Pilot(finance);
        try
        {
            finance.FindControl<TabControl>("FinanceTabs")!.SelectedItem = finance.FindControl<TabItem>("ReturnsTab");
            var grid = finance.FindControl<DataGrid>("RefundsGrid")!;
            var editText = lang == AppLanguage.Kyrgyz ? "Кайтарууну оңдоо" : "Править возврат";
            Button? edit = null;
            RunUntil(() => (edit = grid.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) == editText)) is not null)
                .Should().BeTrue("у возврата с чеком в программе владельца есть «Править возврат», даже без флагов сервера");
            Press(edit!);
            RunUntil(() => pilot.Alerts.Count > 0).Should().BeTrue("нажатие не молчит");
            pilot.Alerts[0].Should().Contain(ReceiptReturnActions.ReturnsEditUnavailableText, "без признака сервера — пояснение, почему правки нет");
        }
        finally
        {
            finance.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task FinanceReturnsTab_Kassa_KeepsOldRule()
    {
        IsOwnerProp.SetValue(null, false);
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        ServerCapabilities.Reset();
        var finance = TestHost.Services.GetRequiredService<FinanceWindow>();
        UiShot.Render(finance, new UiSize(1366, 768));
        try
        {
            finance.FindControl<TabControl>("FinanceTabs")!.SelectedItem = finance.FindControl<TabItem>("ReturnsTab");
            var grid = finance.FindControl<DataGrid>("RefundsGrid")!;
            RunUntil(() => grid.ItemsSource is System.Collections.IEnumerable e && e.Cast<object>().Any()).Should().BeTrue();
            UiShot.Settle(3);
            grid.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(UiDrive.TextOf)
                .Should().NotContain(new[] { "Правка", "Править возврат" }, "в кассе без признака сервера кнопки правки нет — как раньше");
        }
        finally
        {
            finance.Close();
            UiShot.Pump();
        }
    }

    // ───────────── карточка чека: один ряд, «нет права» ─────────────

    [AvaloniaFact]
    public async Task OwnerCard_ReturnAndEditReturns_InOneRow()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        var api = await LoginAsync();
        UiFake.Server.Json("GET", "/api/main/capabilities/", 200, "[\"pos_return_edit_24h\"]");
        await ServerCapabilities.LoadAsync(api, force: true);
        UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[{\"id\":\"pr-9\",\"sale\":\"s-801\",\"sale_number\":3801,\"amount\":\"125.00\",\"created_at\":\"" + At(1)
            + "\",\"status\":\"posted\",\"version\":2,\"can_edit\":true,\"can_void\":true,\"editable_until\":\"" + DateTimeOffset.UtcNow.AddHours(20).ToString("O") + "\",\"server_now\":\""
            + DateTimeOffset.UtcNow.ToString("O") + "\",\"items\":[{\"name\":\"Сахар\",\"qty\":\"1.25\"}]}]");
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        window.AsOwnerSection();
        UiShot.Render(window, new UiSize(1024, 600));
        try
        {
            var show = typeof(SalesWindow).GetMethod("ShowReceiptDetailsByIdAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)show.Invoke(window, ["s-801", "№003801"])!;
            var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
            Button Btn(string name) => popup.Child!.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);
            RunUntil(() => Btn("CustomerReturnsEditButton").IsVisible).Should().BeTrue();
            UiShot.Settle(3);
            var ret = Btn("ReceiptReturnButton");
            var edit = Btn("CustomerReturnsEditButton");
            var yRet = ret.TranslatePoint(new Point(0, 0), popup.Child!)!.Value.Y;
            var yEdit = edit.TranslatePoint(new Point(0, 0), popup.Child!)!.Value.Y;
            yEdit.Should().BeApproximately(yRet, 0.5, "«Возврат» и «Правка возвратов» — в одном ряду (аудит Н-6)");
            edit.TranslatePoint(new Point(0, 0), popup.Child!)!.Value.X.Should().BeGreaterThan(ret.TranslatePoint(new Point(0, 0), popup.Child!)!.Value.X + 10);
            popup.Child!.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ReceiptReturnNoRight").IsVisible.Should().BeFalse("у владельца право есть");
        }
        finally
        {
            window.Close();
            UiFake.Server.Json("GET", "/api/main/capabilities/", 200, "[]");
            await ServerCapabilities.LoadAsync(api, force: true);
            ServerCapabilities.Reset();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerCard_NoReturnPermission_ExplainsInsteadOfEmptyBlock()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"can_view_market_employee_sales\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"can_view_market_employee_sales\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        window.AsOwnerSection();
        UiShot.Render(window, new UiSize(1366, 768));
        try
        {
            var show = typeof(SalesWindow).GetMethod("ShowReceiptDetailsByIdAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)show.Invoke(window, ["s-801", "№003801"])!;
            UiShot.Settle(3);
            var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
            var hint = popup.Child!.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ReceiptReturnNoRight");
            hint.IsVisible.Should().BeTrue("без права «Возврат» — пояснение, а не пустой блок (аудит Н-9)");
            hint.Text.Should().Contain(ReceiptReturnActions.NoRightText);
        }
        finally
        {
            window.Close();
            UiFake.Reset();
            await LoginAsync();
            UiShot.Pump();
        }
    }

    // ───────────── окно возврата без смены: напоминание ─────────────

    [AvaloniaFact]
    public async Task OwnerReturnDialog_NoShift_ShowsPermanentReminder_KassaHasNone()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var host = new Window();
        UiShot.Render(host, new UiSize(1366, 768));
        using var pilot = new Pilot(host);
        try
        {
            var opened = ReturnSaleLauncher.OpenAsync(host, "s-801", "№003801");
            ReturnSaleDialog? dialog = null;
            RunUntil(() => (dialog = Walk([host]).OfType<ReturnSaleDialog>().FirstOrDefault(d => d.IsVisible)) is not null).Should().BeTrue();
            var note = dialog!.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "OwnerNoShiftNote");
            note.Should().NotBeNull("в программе владельца без смены — строка-напоминание в шапке окна (аудит С-30)");
            note!.Text.Should().Contain(ReturnSaleLauncher.OwnerNoShiftNoteText);
            dialog.Close();
            RunUntil(() => opened.IsCompleted, 3000);

            // касса со сменой — строки нет
            IsOwnerProp.SetValue(null, false);
            PosApp.ActiveShiftId = Guid.NewGuid().ToString();
            var opened2 = ReturnSaleLauncher.OpenAsync(host, "s-801", "№003801");
            ReturnSaleDialog? kassa = null;
            RunUntil(() => (kassa = Walk([host]).OfType<ReturnSaleDialog>().FirstOrDefault(d => d.IsVisible)) is not null).Should().BeTrue();
            kassa!.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(t => t.Name == "OwnerNoShiftNote", "касса не меняется");
            kassa.Close();
            RunUntil(() => opened2.IsCompleted, 3000);
        }
        finally
        {
            host.Close();
            UiShot.Pump();
        }
    }

    // ───────────── «Сводка»: видимая «⋯» у продажи; «Печать» при скрытых «Настройках» ─────────────

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    [AvaloniaFact]
    public async Task Overview_RecentSale_HasVisibleMenuButton_OpensReturnMenu()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            Button? more = null;
            RunUntil(() => (more = shell.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RecentSaleMenuButton" && b.IsEffectivelyVisible)) is not null, 15000)
                .Should().BeTrue("у строки «Последних продаж» — видимая кнопка «⋯» (аудит С-19: на сенсорном экране правую кнопку не найти)");
            Press(more!);
            UiShot.Settle(3);
            var row = more!.FindAncestorOfType<Border>()!;
            row.ContextMenu.Should().NotBeNull();
            row.ContextMenu!.IsOpen.Should().BeTrue("«⋯» открывает меню возврата");
            row.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).Should().Contain(h => h != null && h.StartsWith(ReturnSaleLauncher.ButtonText));
            row.ContextMenu.Close();
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Print_OpensWhenSettingsHidden_AndIsHighlightedItself()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // 2026-11-13: пункт «Печать» в оболочке владельца требует право can_view_settings (владельцу оно не дарится по роли) — в профиле заглушки входа его нет, добавляем
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200, "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        await LoginAsync();
        var wasHidden = SectionVisibility.IsHidden("settings");
        SectionVisibility.Set("settings", true);
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3);
            NavButtons(shell).Should().NotContainKey("settings", "«Настройки» скрыты в «Разделах меню»");
            NavButtons(shell).Should().ContainKey("print");
            Press(NavButtons(shell)["print"]);
            RunUntil(() => shell.OwnedWindows.OfType<PosSettingsWindow>().Any(w => w.IsVisible), 8000)
                .Should().BeTrue("«Печать» открывается и при скрытых «Настройках» (аудит В-11)");
            NavButtons(shell)["print"].Classes.Should().Contain("active", "подсвечена сама «Печать», а не «Настройки» (аудит С-21)");
        }
        finally
        {
            SectionVisibility.Set("settings", wasHidden);
            shell.Close();
            UiShot.Pump();
        }
    }
}
