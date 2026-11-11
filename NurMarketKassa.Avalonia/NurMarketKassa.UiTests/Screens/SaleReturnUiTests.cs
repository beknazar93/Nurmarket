using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-10 (владелец: ошибка возврата долгового чека «не из этого чека», «и фильтр в возврате!», «поиск по товару!», «возврат добавь в клиентов, которые брали в долг»,
/// «возвраты добавь в программу владельца», «функцию возврата добавь в продажи»): настоящие окна в Avalonia.Headless против поддельного NurCRM.
/// Окно возврата с фильтрами при 1024×600 и 1366×768 (ru, ky) — без обрезки новых надписей; фильтры вместе и «Сбросить»; поиск по товару с подсветкой и предвыбором;
/// режим клиента (статус «Долговой»); «Возврат» в «Истории покупок» клиента; колонка «Возврат» в «Продажах»; пункт «Возврат» в меню программы владельца (виден с правом, скрыт без права,
/// открывает то же окно в режиме --owner). Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\return-filters-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class SaleReturnUiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public SaleReturnUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = Guid.NewGuid().ToString();
    }

    public void Dispose() => PosApp.ActiveShiftId = _savedShift;

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static string Row(string id, int number, string total, string method, string status, string returnStatus, string item, string cashier, string? client = null, int daysAgo = 0) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"" + status + "\",\"return_status\":\"" + returnStatus + "\",\"total\":\"" + total + "\",\"payment_method\":\"" + method
        + "\",\"created_at\":\"" + DateTimeOffset.Now.AddDays(-daysAgo).AddMinutes(-number).ToString("O") + "\",\"first_item_name\":\"" + item + "\",\"user_display\":\"" + cashier + "\""
        + (client is null ? "" : ",\"client\":\"c-1\",\"client_name\":\"" + client + "\"") + "}";

    /// <summary>Список с фото владельца: 3 200, 260, 3 540 (долг), 800, 200, 300, 2 150, 480.</summary>
    private static void StubPhotoList()
    {
        var rows = new[]
        {
            Row("s-3200", 2101, "3200.00", "cash", "paid", "none", "Масло моторное 5W-40", "Айбек"),
            Row("s-260", 2102, "260.00", "card", "paid", "none", "Хомут", "Айбек"),
            Row("s-3540", 2103, "3540.00", "debt", "debt", "none", "Ступица подшипник передний Ласетти WNQ", "Нурлан", "Азамат"),
            Row("s-800", 2104, "800.00", "qr", "paid", "none", "Свеча", "Айбек"),
            Row("s-200", 2105, "200.00", "cash", "partially_returned", "partial", "Лампа H4", "Айбек"),
            Row("s-300", 2106, "300.00", "mixed", "paid", "none", "Предохранитель", "Нурлан"),
            Row("s-2150", 2107, "2150.00", "cash", "canceled", "full", "Глушитель", "Айбек"),
            Row("s-480", 2108, "480.00", "transfer", "paid", "none", "Щётка", "Нурлан"),
        };
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":8,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-3540/", 200, "{\"id\":\"s-3540\",\"number\":2103,\"status\":\"debt\",\"payment_method\":\"debt\",\"total\":\"3540.00\",\"client\":\"c-1\",\"items\":["
            + "{\"id\":\"si-1\",\"product_id\":\"p-1\",\"name\":\"Подшипник\",\"quantity\":\"1\",\"unit_price\":\"500\",\"total\":\"500\",\"returnable_qty\":1},"
            + "{\"id\":\"si-2\",\"product_id\":\"p-2\",\"name\":\"Ступица подшипник передний Ласетти WNQ\",\"quantity\":\"1\",\"unit_price\":\"550\",\"total\":\"550\",\"returnable_qty\":1},"
            + "{\"id\":\"si-3\",\"product_id\":\"p-3\",\"name\":\"Глушитель\",\"quantity\":\"1\",\"unit_price\":\"250\",\"total\":\"250\",\"returnable_qty\":1},"
            + "{\"id\":\"si-4\",\"product_id\":\"p-4\",\"name\":\"S-OIL SEVEN ATF III\",\"quantity\":\"4\",\"unit_price\":\"560\",\"total\":\"2240\",\"returnable_qty\":4}]}");
    }

    private static async Task LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
    }

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    private static ObservableCollectionView Grid(ReturnSaleDialog d) => new(d.FilteredSales);

    private sealed record ObservableCollectionView(System.Collections.ObjectModel.ObservableCollection<ReturnSaleDialog.ReturnSaleGridRow> Rows)
    {
        public List<decimal> Totals => Rows.Select(r => r.Item.TotalAmount).ToList();
    }

    private static ComboBox Combo(Window w, string automationName) =>
        w.GetVisualDescendants().OfType<ComboBox>().First(c => Avalonia.Automation.AutomationProperties.GetName(c) == automationName);

    // ------------------------------------------------------------------ окно возврата: фильтры на 1024×600 / 1366×768

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ReturnDialog_FiltersOpen_FitScreen_NewTextsNotClipped(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        StubPhotoList();
        await LoginAsync();
        var dialog = new ReturnSaleDialog();
        try
        {
            UiShot.Render(dialog, new UiSize(width, height));
            WaitFor(() => dialog.Sales.Count == 8);
            dialog.Sales.Should().HaveCount(8, "список чеков пришёл с сервера");

            var toggle = UiDrive.Find<Button>(dialog, "ReturnFiltersToggle");
            UiDrive.Click(dialog, toggle);
            UiShot.Settle(4, 30);
            var bar = dialog.GetVisualDescendants().OfType<SaleFilterBar>().Single();
            bar.IsEffectivelyVisible.Should().BeTrue("«Фильтры» раскрывают панель");
            var product = UiDrive.Find<TextBox>(dialog, "ReturnProductSearchBox");
            product.IsEffectivelyVisible.Should().BeTrue();

            // Новые элементы — в пределах окна (без горизонтальной прокрутки) и с текстом без обрезки.
            foreach (var c in new Control[] { bar, product, toggle })
            {
                var topLeft = c.TranslatePoint(new Avalonia.Point(0, 0), dialog)!.Value;
                (topLeft.X + c.Bounds.Width).Should().BeLessThanOrEqualTo(width + 1, $"{c.GetType().Name} не выходит за правый край при {width}×{height}");
            }
            var mine = bar.GetVisualDescendants().OfType<TextBlock>().Concat(toggle.GetVisualDescendants().OfType<TextBlock>())
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Length > 60 ? t[..60] : t!).ToHashSet();
            var clipped = TextClipChecker.Find(dialog, "return-filters", lang.ToString()).Where(v => mine.Any(t => v.Detail.Contains("«" + t, StringComparison.Ordinal))).ToList();
            clipped.Should().BeEmpty(string.Join("\n", clipped.Select(v => v.Detail)));

            SaveShot(dialog, $"return-filters-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ фильтры вместе, «Сбросить», поиск по товару, предвыбор

    [AvaloniaFact]
    public async Task ReturnDialog_FiltersCombine_Reset_ProductSearch_PreselectsFoundLine()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubPhotoList();
        await LoginAsync();
        var dialog = new ReturnSaleDialog();
        try
        {
            UiShot.Render(dialog, new UiSize(1366, 768));
            WaitFor(() => dialog.Sales.Count == 8);
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "ReturnFiltersToggle"));
            UiShot.Settle(3, 30);

            Combo(dialog, "Статус чека").SelectedIndex = (int)SaleListFilter.StatusKind.Debt;
            UiShot.Settle(2, 20);
            Grid(dialog).Totals.Should().Equal(3540m);
            UiDrive.AllText(dialog).Should().Contain("Найдено: 1 из 8");

            Combo(dialog, "Статус чека").SelectedIndex = 0;
            Combo(dialog, "Способ оплаты").SelectedIndex = (int)SaleListFilter.PaymentKind.Cash;
            UiShot.Settle(2, 20);
            Grid(dialog).Totals.Should().BeEquivalentTo(new[] { 3200m, 200m, 2150m });
            Combo(dialog, "Кассир").SelectedItem = "Нурлан";
            UiShot.Settle(2, 20);
            Grid(dialog).Totals.Should().BeEmpty("фильтры работают вместе: у Нурлана наличных чеков нет");

            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Сбросить"));
            UiShot.Settle(3, 30);
            Grid(dialog).Totals.Should().HaveCount(8, "«Сбросить» снимает все фильтры");

            // Поиск по товару: слова названия в любом порядке → чек 3 540 с подписью найденного товара.
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "ReturnProductSearchBox"), "ласетти ступица");
            WaitFor(() => dialog.FilteredSales.Count == 1, 3000);
            var row = dialog.FilteredSales.Should().ContainSingle().Subject;
            row.SaleId.Should().Be("s-3540");
            row.HitText.Should().Contain("Ласетти");

            // Открыли чек — найденная позиция отмечена, количество 1; остальные не отмечены. Долговой чек — подпись «в счёт долга клиента».
            var open = typeof(ReturnSaleDialog).GetMethod("OpenSaleByIdAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)open.Invoke(dialog, ["s-3540"])!;
            UiShot.Settle(3, 30);
            dialog.Lines.Should().HaveCount(4, "все позиции чека видны");
            var hit = dialog.Lines.Single(l => l.IsSearchHit);
            hit.Title.Should().Contain("Ласетти");
            hit.IsSelected.Should().BeTrue();
            hit.ReturnQuantity.Should().Be(1);
            dialog.Lines.Where(l => !l.IsSearchHit).Should().OnlyContain(l => !l.IsSelected);
            dialog.Lines.Select(l => l.LineId).Should().Equal("si-1", "si-2", "si-3", "si-4");
            UiDrive.AllText(dialog).Should().Contain("в счёт долга клиента");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnDialog_ClientMode_DebtStatus_ShowsOnlyClientsDebtReceipts()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubPhotoList();
        await LoginAsync();
        var dialog = new ReturnSaleDialog { InitialClientId = "c-1", InitialClientName = "Азамат", InitialStatus = SaleListFilter.StatusKind.Debt };
        try
        {
            UiShot.Render(dialog, new UiSize(1024, 600));
            WaitFor(() => dialog.FilteredSales.Count > 0);
            dialog.FilteredSales.Select(r => r.SaleId).Should().Equal("s-3540");
            NurMarketKassa.Tests.Wave1.ScenarioSupport.Requests(UiFake.Server, "GET", "/api/main/pos/sales/")
                .Should().Contain(e => e.RequestMessage.Url.Contains("client=c-1"), "список — продажи этого клиента (pos/sales/?client=)");
            dialog.Title.Should().Contain("Азамат");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Возврат» в «Истории покупок» клиента и в «Продажах»

    [AvaloniaFact]
    public async Task ClientPurchases_ReturnButton_VisibleForReturnable_DisabledForFullyReturned()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubPhotoList();
        await LoginAsync();
        var window = new ClientPurchasesWindow("c-1", "Азамат");
        try
        {
            UiShot.Render(window, new UiSize(1024, 600));
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
            WaitFor(() => grid.ItemsSource is System.Collections.IList { Count: > 0 });
            var items = ((System.Collections.IList)grid.ItemsSource!).Cast<object>().ToList();
            var button = UiDrive.Find<Button>(window, "ClientPurchaseReturnButton");

            grid.SelectedItem = items.First(i => i.GetType().GetProperty("SaleId")!.GetValue(i) as string == "s-3540");
            UiShot.Settle(3, 30);
            button.IsVisible.Should().BeTrue("у долгового чека есть «Возврат»");
            button.IsEnabled.Should().BeTrue();

            grid.SelectedItem = items.First(i => i.GetType().GetProperty("SaleId")!.GetValue(i) as string == "s-2150");
            UiShot.Settle(3, 30);
            button.IsEnabled.Should().BeFalse("чек возвращён целиком — возвращать нечего");
            ToolTip.GetTip(button).Should().Be(ReturnSaleLauncher.FullyReturnedHint);
            grid.Columns.Select(c => c.Header as string).Should().Contain("Возврат");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void SalesRow_ReturnState_MarkAndButtonAvailability()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        new SalesWindow.SaleItem { Id = "s-1", Status = "paid", ReturnStatus = "none" }.CanReturnRow.Should().BeTrue();
        var partial = new SalesWindow.SaleItem { Id = "s-2", Status = "partially_returned", ReturnStatus = "partial" };
        partial.CanReturnRow.Should().BeTrue();
        partial.ReturnMark.Should().Be("↩ частично");
        var full = new SalesWindow.SaleItem { Id = "s-3", Status = "paid", ReturnStatus = "full" };
        full.CanReturnRow.Should().BeFalse();
        full.ReturnTip.Should().Be(ReturnSaleLauncher.FullyReturnedHint);
        new SalesWindow.SaleItem { Id = "local-1" }.CanReturnRow.Should().BeFalse("чек без связи ещё не на сервере");
    }

    [AvaloniaFact]
    public async Task SalesWindow_HasReturnColumn_ForOwner()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync(); // поддельный сервер: роль owner — право «Возврат» есть всегда
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        window.AsOwnerSection(); // как раздел «Продажи» программы владельца
        try
        {
            UiShot.Render(window, new UiSize(1366, 768));
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "SalesGrid");
            grid.Columns.Select(c => c.Header as string).Should().Contain(ReturnSaleLauncher.ButtonText);
            window.GetVisualDescendants().OfType<SaleFilterBar>().Should().ContainSingle("общая панель фильтров есть и в «Продажах»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ пункт «Возврат» в меню программы владельца

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    [AvaloniaFact]
    public async Task OwnerMenu_ReturnItem_VisibleForOwner_OpensSameReturnWindow_InOwnerMode()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubPhotoList();
        await LoginAsync();
        var isOwner = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
        var wasOwner = AppMode.IsOwner;
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("salereturn", "пункт «Возврат» в группе «Продажи» виден владельцу");
            isOwner.SetValue(null, true); // режим --owner; смена у теста открыта — подтверждение «без смены» не нужно
            SectionVisibility.Catalog.Select(c => c.Key).Should().Contain("salereturn", "пункт можно скрыть в «Разделах меню» программы владельца");
            NavButtons(shell)["salereturn"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<ReturnSaleDialog>().Any(), 3000);
            var dialog = shell.OwnedWindows.OfType<ReturnSaleDialog>().FirstOrDefault();
            dialog.Should().NotBeNull("пункт открывает то же окно возврата, что в кассе");
            WaitFor(() => dialog!.Sales.Count == 8);
            dialog!.Sales.Should().HaveCount(8);
            dialog.Close();
            UiShot.Pump();
        }
        finally
        {
            isOwner.SetValue(null, wasOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_ReturnItem_HiddenWithoutPermission()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            ReturnSaleLauncher.CanReturn().Should().BeFalse();
            NavButtons(shell).Should().NotContainKey("salereturn", "без права «Возврат» пункт не показывается");
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
            UiFake.Reset();
            await LoginAsync(); // вернуть владельца для следующих тестов
        }
    }
}
