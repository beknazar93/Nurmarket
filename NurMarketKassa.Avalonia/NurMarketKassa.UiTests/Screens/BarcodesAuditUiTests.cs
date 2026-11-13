using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (аудит Fable, пакет 4 «Штрихкоды»): настоящие проверки вместо «всегда истинных». Генерация: настоящее правило прав (без подмены), вкладка склада и раздел владельца,
/// отказ сервера на части товаров, уникальность относительно каталога, остановка при закрытии раздела (Л-4); панель: нет пустых рамок кнопок (Д-1), свёрнутая полоса настроек — одна строка (Д-2),
/// цена с запятой и отступ у «Копий» (Д-7, Д-17), подпись «Массовая печать ценников…» (Д-18), в покое раскладка не крутится (Л-15).</summary>
[Collection("UiScenarios")]
public sealed class BarcodesAuditUiTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();
    private readonly Dictionary<string, string> _server = new();
    private readonly List<string> _saves = new();
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private readonly bool? _savedSettingsOpen = BarcodePrintSettingsStore.Load().SettingsOpen;

    public BarcodesAuditUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        BulkBarcodeGenerator.ReadBarcodeOverride = (id, _) => Task.FromResult<string?>(_server.GetValueOrDefault(id) ?? "");
        BulkBarcodeGenerator.SaveOverride = (id, code, _) => { _saves.Add(id); _server[id] = code; return Task.FromResult<string?>(null); };
        BulkBarcodeGenerator.RefreshOverride = _ => Task.CompletedTask;
        BulkBarcodeGenerator.DelayOverride = (_, _) => Task.CompletedTask;
        BarcodePrintView.GenerateAllowedOverride = () => true;
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
    }

    public void Dispose()
    {
        BulkBarcodeGenerator.ReadBarcodeOverride = null;
        BulkBarcodeGenerator.SaveOverride = null;
        BulkBarcodeGenerator.RefreshOverride = null;
        BulkBarcodeGenerator.DelayOverride = null;
        BarcodePrintView.GenerateAllowedOverride = null;
        BarcodePrintWindow.ConfirmOverride = null;
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        var s = BarcodePrintSettingsStore.Load();
        s.SettingsOpen = _savedSettingsOpen;
        BarcodePrintSettingsStore.Save(s);
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    private static void Seed(int withCode, int withoutCode, int weighed = 0)
    {
        CatalogCacheService.Products.Clear();
        var list = new List<CatalogProductTileVm>();
        for (var i = 0; i < withCode; i++)
            list.Add(new CatalogProductTileVm("c" + i, "С кодом " + i, "10.00 сом", false) { Barcode = (4600000000000L + i).ToString(), Unit = "шт", Quantity = 5 });
        for (var i = 0; i < withoutCode; i++)
            list.Add(new CatalogProductTileVm("n" + i, "Без кода " + i, "10.00 сом", false) { Unit = "шт", Quantity = 5 });
        for (var i = 0; i < weighed; i++)
            list.Add(new CatalogProductTileVm("w" + i, "Весовой " + i, "100.00 сом", true) { Unit = "кг", Quantity = 5 });
        CatalogCacheService.Products.AddRange(list);
    }

    private static async Task LoginAsync(string role)
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"" + role + "\",\"can_view_cashier\":true,\"can_view_settings\":" + (role == "owner" ? "true" : "false") + "}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"" + role + "\",\"can_view_cashier\":true,\"can_view_settings\":" + (role == "owner" ? "true" : "false") + ",\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    private static Button GenButton(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().First(b => IsGenText(UiDrive.TextOf(b)));

    private static bool IsGenText(string? t) => t is not null && (t.StartsWith("Сгенерировать штрихкоды", StringComparison.Ordinal) || t.StartsWith("Создать коды", StringComparison.Ordinal) || t.StartsWith("Штрих-коддорду түзүү", StringComparison.Ordinal) || t.StartsWith("Коддорду түзүү", StringComparison.Ordinal));

    private static (Window Window, BarcodePrintView View) OpenView(int w, int h)
    {
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = w, Height = h, Content = view };
        window.Show();
        UiShot.Settle(4, 20);
        return (window, view);
    }

    // ------------------------------------------------------------------ Т-10: настоящее правило прав

    [AvaloniaFact]
    public async Task Generate_RealPermissionRule_CashierWithoutRightIsBlocked_OwnerIsAllowed()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        BarcodePrintView.GenerateAllowedOverride = null; // настоящее правило
        Seed(2, 6);
        BarcodePrintWindow.ConfirmOverride = _ => Task.FromResult(true);
        try
        {
            await LoginAsync("salesperson");
            var (w1, v1) = OpenView(1366, 768);
            try
            {
                var b = GenButton(v1);
                b.IsEnabled.Should().BeFalse("кассир без права «Склад: редактирование» не может генерировать штрихкоды");
                var hint = Avalonia.Controls.ToolTip.GetTip(b)!.ToString()!;
                hint.Should().Contain("права");
                await v1.GenerateAsync();
                _saves.Should().BeEmpty("без права в карточки ничего не пишется");
                v1.LastGenerateResult.Should().BeNull();
            }
            finally
            {
                w1.Close();
                UiShot.Pump();
            }

            await LoginAsync("owner");
            var (w2, v2) = OpenView(1366, 768);
            try
            {
                GenButton(v2).IsEnabled.Should().BeTrue("владелец на связи может");
                await v2.GenerateAsync();
                v2.LastGenerateResult!.Created.Should().HaveCount(6);
            }
            finally
            {
                w2.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            UiFake.Reset();
            await LoginAsync("owner");
        }
    }

    // ------------------------------------------------------------------ Т-18: вкладка склада и раздел владельца, отказ сервера, уникальность относительно каталога

    private static string TabHeader(TabItem t) => (t.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

    [AvaloniaFact]
    public async Task Generate_InWarehouseTab_ServerRefusesSome_ReportsAndKeepsCodesUnique()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 4, withoutCode: 8, weighed: 1);
        var existing = CatalogCacheService.Products.Where(p => !string.IsNullOrEmpty(p.Barcode)).Select(p => p.Barcode!).ToHashSet();
        BulkBarcodeGenerator.SaveOverride = (id, code, _) =>
        {
            if (id is "n2" or "n5")
                return Task.FromResult<string?>("сервер отказал: 500");
            _saves.Add(id);
            _server[id] = code;
            return Task.FromResult<string?>(null);
        };
        BarcodePrintWindow.ConfirmOverride = _ => Task.FromResult(true);
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            wh.Width = 1366;
            wh.Height = 768;
            wh.Show();
            UiShot.Settle(6, 30);
            var tab = UiDrive.Find<TabControl>(wh, "WarehouseTabs").Items.OfType<TabItem>().Single(t => TabHeader(t) == "Штрихкоды");
            UiDrive.Click(wh, tab);
            UiShot.Settle(6, 30);
            var view = wh.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            view.MissingBarcodeCount.Should().Be(9, "8 штучных и 1 весовой без штрихкода");
            UiDrive.Click(wh, GenButton(wh));
            for (var i = 0; i < 60 && view.LastGenerateResult is null; i++)
                UiShot.Settle(1, 50);
            var r = view.LastGenerateResult;
            r.Should().NotBeNull();
            r!.Created.Should().HaveCount(6);
            r.Failed.Select(f => f.Product.Id).Should().BeEquivalentTo("n2", "n5");
            r.Skipped.Should().ContainSingle().Which.Product.Id.Should().Be("w0");
            r.Created.Select(c => c.Code).Should().OnlyHaveUniqueItems();
            r.Created.Select(c => c.Code).Should().NotIntersectWith(existing, "новые коды не совпадают ни с одним кодом каталога");
            UiDrive.AllText(wh).Should().Contain("Создано 6, пропущено 1, ошибок 2.");
            CatalogCacheService.Products.Single(p => p.Id == "n2").Barcode.Should().BeNullOrEmpty("при отказе сервера код в карточке не появляется");
        }
        finally
        {
            wh.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Generate_InOwnerSection_Works_AndClosingTheSectionStopsIt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 2, withoutCode: 60);
        BarcodePrintWindow.ConfirmOverride = _ => Task.FromResult(true);
        // долгая пауза между товарами: проход «идёт», пока раздел не закрыли
        BulkBarcodeGenerator.DelayOverride = async (_, ct) => await Task.Delay(TimeSpan.FromSeconds(20), ct);
        var section = new BarcodePrintSectionWindow();
        section.Width = 1366;
        section.Height = 768;
        section.Show();
        UiShot.Settle(6, 30);
        var view = section.Host.View!;
        view.IsBusy.Should().BeFalse();
        var run = view.GenerateAsync();
        for (var i = 0; i < 40 && !view.IsBusy; i++)
            UiShot.Settle(1, 50);
        view.IsBusy.Should().BeTrue("проход идёт");
        ((IOwnerSectionKeepAlive)section).KeepAliveWhenHidden.Should().BeTrue("оболочка не закрывает спрятанный раздел, пока идёт генерация");
        section.HasUnsavedChanges.Should().BeTrue();
        var savedBefore = _saves.Count;
        section.Close();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().BeSameAs(run, "закрытие раздела остановило генерацию (раньше она шла дальше без окна)");
        view.LastGenerateResult!.Cancelled.Should().BeTrue();
        view.LastGenerateResult.Created.Count.Should().BeLessThan(60);
        _saves.Count.Should().BeLessThanOrEqualTo(savedBefore + 1, "после закрытия записи в карточки не продолжаются");
        view.IsBusy.Should().BeFalse();
    }

    // ------------------------------------------------------------------ Д-1, Д-2, Д-7, Д-17, Д-18, Л-15

    public static IEnumerable<object[]> Sizes() => new[] { new object[] { 1024, 600 }, new object[] { 1366, 768 }, new object[] { 1900, 1060 } };

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Toolbar_HasNoEmptyButtons_InTabAndSection(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 30, withoutCode: 10);
        var section = new BarcodePrintSectionWindow { Width = w, Height = h };
        section.Show();
        UiShot.Settle(8, 40);
        try
        {
            // 2026-11-13: RepeatButton — шаблонные части: «вверх/вниз» полосы прокрутки (PART_LineUpButton) и стрелки счётчика NumericUpDown (PART_IncreaseButton/PART_DecreaseButton) — у них значок вместо подписи по устройству, это не кнопки панели
            var buttons = section.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b is not Avalonia.Controls.RepeatButton).ToList();
            buttons.Should().NotBeEmpty();
            foreach (var b in buttons)
                (UiDrive.TextOf(b) ?? "").Trim().Length.Should().BeGreaterThan(0, $"у видимой кнопки {b.Name ?? b.Classes.FirstOrDefault()} на ({b.Bounds.X:0},{b.Bounds.Y:0}) есть подпись ({w}x{h})");
        }
        finally
        {
            section.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(1366, 768, false)]
    [InlineData(1024, 600, true)]
    [InlineData(1366, 768, true)]
    public async Task SettingsStrip_WhenCollapsed_IsOneLine_AndListKeepsItsHeight(int w, int h, bool savedOpen)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 40, withoutCode: 0);
        var s = BarcodePrintSettingsStore.Load();
        s.SettingsOpen = savedOpen; // запомнено «открыто» — на низком окне вкладка сама сворачивает настройки
        BarcodePrintSettingsStore.Save(s);
        var section = new BarcodePrintSectionWindow { Width = w, Height = h };
        section.Show();
        UiShot.Settle(8, 40);
        try
        {
            var view = section.Host.View!;
            var fl = BindingFlags.NonPublic | BindingFlags.Instance;
            var card = (Border)typeof(BarcodePrintView).GetField("_settingsCard", fl)!.GetValue(view)!;
            var body = (Control)typeof(BarcodePrintView).GetField("_settingsBody", fl)!.GetValue(view)!;
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            if (!body.IsVisible)
                card.Bounds.Height.Should().BeLessThan(80, "свёрнутая полоса «40×30 мм · …» — одна строка по содержимому, а не пустой блок");
            else
                card.Bounds.Height.Should().BeLessThanOrEqualTo(view.Bounds.Height * 0.5 + 1);
            scroll.Bounds.Height.Should().BeGreaterThan(200, $"список карточек получает место ({w}x{h}, запомнено «открыто»={savedOpen}, настройки видны={body.IsVisible})");
            if (h <= 600)
                body.IsVisible.Should().BeFalse("на низком окне настройки свёрнуты");
        }
        finally
        {
            section.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Cards_ShowPriceWithComma_AndLabelIndent_AndLegacyButtonName()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 6, withoutCode: 0);
        var (window, view) = OpenView(1366, 768);
        try
        {
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().Contain("10,00 сом", "цена на карточке с запятой, как на складе");
            texts.Should().NotContain("10.00 сом");
            var label = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Копий:");
            label.Margin.Left.Should().BeGreaterThanOrEqualTo(8, "у подписи «Копий:» есть отступ слева");
            view.LegacyButton.Content.Should().Be("Массовая печать ценников…", "подпись кнопки совпадает с названием окна, которое она открывает");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Layout_InIdle_DoesNotSpin()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 30, withoutCode: 5);
        var section = new BarcodePrintSectionWindow { Width = 1366, Height = 768 };
        section.Show();
        UiShot.Settle(8, 40);
        try
        {
            var free = section.GetVisualDescendants().OfType<WidthFreeHost>().Single();
            var before = free.MeasureCalls;
            UiShot.Settle(12, 100); // 1,2 с покоя
            (free.MeasureCalls - before).Should().BeLessThanOrEqualTo(2, "в покое раскладка вкладки не пересчитывается");
        }
        finally
        {
            section.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «The Control already has a parent»: повторное открытие вкладки/раздела ×5 без исключений

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    [AvaloniaFact]
    public async Task Barcodes_ReopeningTab_AndOwnerSection_Five_Times_NeverThrows()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(withCode: 20, withoutCode: 10);
        // вкладка «Склад → Штрихкоды»: Штрихкоды → Товары → Штрихкоды ×5, настройки открываются и сворачиваются
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            wh.Width = 1366;
            wh.Height = 768;
            wh.Show();
            UiShot.Settle(6, 30);
            var tabs = UiDrive.Find<TabControl>(wh, "WarehouseTabs");
            var barcodes = tabs.Items.OfType<TabItem>().Single(t => TabHeader(t) == "Штрихкоды");
            for (var i = 0; i < 5; i++)
            {
                tabs.SelectedItem = barcodes;
                UiShot.Settle(4, 20);
                var view = wh.GetVisualDescendants().OfType<BarcodePrintView>().Should().ContainSingle().Subject;
                var toggle = view.GetVisualDescendants().OfType<Button>().First(b => (UiDrive.TextOf(b) ?? "").Contains("Настройк"));
                UiDrive.Click(wh, toggle);
                UiShot.Settle(3, 20);
                UiDrive.Click(wh, toggle);
                UiShot.Settle(3, 20);
                tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ProductsTabItem");
                UiShot.Settle(3, 20);
                tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ReceivingTabItem");
                UiShot.Settle(3, 20);
            }
        }
        finally
        {
            wh.Close();
            UiShot.Pump();
        }

        // раздел «Штрихкоды и ценники» в программе владельца: открыть → «Склад» → снова «Штрихкоды и ценники» ×5
        await LoginAsync("owner");
        var shell = new OwnerShellWindow { Width = 1440, Height = 800 };
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            for (var i = 0; i < 5; i++)
            {
                NavButtons(shell)["barcodes"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                UiShot.Settle(6, 40);
                shell.OwnedWindows.OfType<BarcodePrintSectionWindow>().Should().ContainSingle();
                NavButtons(shell)["warehouse"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                UiShot.Settle(6, 40);
            }
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }
}
