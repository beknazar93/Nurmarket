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
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (владелец: «перенос данных из другой программы убери у программы кассы, он только у владельца должен быть!»): настоящие окна в Avalonia.Headless.
/// Касса (AppMode.IsOwner = false): нигде нет «Переноса данных», «Массовой загрузки», «Загрузить из файла», «Загрузить накладную» — ни в меню ☰ и его поиске (Ctrl+K),
/// ни в «Настройках → Операции», ни в «Складе» (кнопка в строке товаров, пустое состояние, «Массовое добавление», приёмка), ни в обучении и мастере первого запуска;
/// окна импорта не открываются никакой ссылкой (Open/ShowAsync молча возвращают управление); вместо кнопки — пояснение «Импорт — в программе владельца».
/// Программа владельца (AppMode.IsOwner = true): всё на месте — карточка в Настройках → Операции, кнопки в «Складе», пункт «Перенос данных» в меню, мастер открывается.
/// Снимки: %TEMP%\nurmarket-tests\ui-actual\import-*.png (1024×600 и 1366×768, ru/ky/uz) — смотреть глазами.</summary>
[Collection("UiScenarios")]
public sealed class ImportOwnerOnlyUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedOwnerSections = AppMode.OwnerSectionsInKassa;

    /// <summary>Подписи входов импорта на всех пяти языках: в кассе ни одной из них быть не должно.</summary>
    private static readonly string[] Banned =
    {
        "Массовая загрузка", "Перенос данных", "Загрузить накладную", "Загрузить из файла", "Загрузить товары и клиентов",
        "Массалык жүктөө", "Маалыматты которуу", "Накладнойду жүктөө", "Башка программадан",
        "Bulk import", "Data transfer", "Move data from another program", "Load an invoice", "Import from a file",
        "Toplu yükleme", "Veri aktarımı", "Fatura yükle", "Dosyadan yükle",
        "Ommaviy yuklash", "Ma'lumot ko'chirish", "Yuk xatini yuklash", "Fayldan yuklash",
    };

    public ImportOwnerOnlyUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _savedIsOwner);
        AppMode.OwnerSectionsInKassa = _savedOwnerSections;
        BulkImportWizardWindow.CanOpenTransferOverride = null;
    }

    public static IEnumerable<object[]> SizesAndThreeLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Lang(AppLanguage language) => language switch { AppLanguage.Russian => "ru", AppLanguage.Kyrgyz => "ky", _ => "uz" };

    private static async Task LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
    }

    private static async Task LoginOwnerAsync()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
    }

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    /// <summary>Все тексты и подписи кнопок окна (в том числе скрытые по IsVisible — нам важно, что их вообще нет в дереве).</summary>
    private static List<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
            .Concat(root.GetVisualDescendants().OfType<ContentControl>().Select(c => c.Content as string ?? ""))
            .Concat(root.GetVisualDescendants().OfType<Control>().Select(c => Avalonia.Automation.AutomationProperties.GetName(c) ?? ""))
            .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

    private static void NoBannedText(Visual root, string where) =>
        Texts(root).Where(t => Banned.Any(b => t.Contains(b, StringComparison.OrdinalIgnoreCase))).Should().BeEmpty($"в кассе нет входов импорта: {where}");

    private static Control CallBuildProductsEmpty(WarehouseWindow window, bool filtered) =>
        (Control)typeof(WarehouseWindow).GetMethod("BuildProductsEmpty", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [filtered, false])!;

    // ------------------------------------------------------------------ КАССА

    /// <summary>Кассир: окна импорта не открываются «изнутри» (ссылкой, горячей клавишей, старым кодом) — Open/ShowAsync ничего не делают.</summary>
    [AvaloniaFact]
    public async Task Kassa_ImportWindowsDoNotOpen_AndNoRightMatters()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        IsOwnerProp.SetValue(null, false);
        BulkImportWizardWindow.CanOpenTransferOverride = () => true; // даже если «право» подменили — в кассе импорта нет
        ImportAccess.Allowed.Should().BeFalse();
        BulkImportWizardWindow.CanOpenTransfer().Should().BeFalse();
        InvoiceImportFlow.CanImport().Should().BeFalse("загрузка накладной из файла — только в программе владельца");

        var openWizard = typeof(BulkImportWizardWindow).GetField("_openWizard", BindingFlags.NonPublic | BindingFlags.Static)!;
        BulkImportWizardWindow.Open(null);
        BulkImportWizardWindow.OpenTransfer(null);
        openWizard.GetValue(null).Should().BeNull("мастер «Массовая загрузка» в кассе не создаётся");
        (await BulkImportWizardWindow.ShowForOnboardingAsync(new Window())).Should().Be(new BulkImportWizardWindow.OnboardingResult(false, false));
        (await ClientsImportWindow.ShowAsync(null)).Should().Be(0);
        var before = WindowSingleton.Count;
        BulkImportWindow.Open(null);
        WindowSingleton.Count.Should().Be(before, "старая загрузка CSV в кассе не открывается");
        UiShot.Pump();
    }

    /// <summary>«Настройки → Операции» и «Склад» кассы: ни карточки «Перенос данных», ни «Массовой загрузки», ни «Загрузить накладную»; пустой склад — пояснение вместо кнопок.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(SizesAndThreeLanguages))]
    public async Task Kassa_SettingsAndWarehouse_HaveNoImportEntryPoints(int w, int h, AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = true; // склад в кассе виден (автономный режим / «Старт»): именно там раньше была кнопка

        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = w;
        settings.Height = h;
        try
        {
            settings.Show();
            UiShot.Settle(6);
            settings.SelectNavTab("operations").Should().BeTrue();
            UiShot.Settle(6);
            settings.GetVisualDescendants().OfType<Border>().Should().NotContain(b => b.Name == "TransferDataCard", "карточки «Перенос данных» в кассе нет совсем");
            settings.GetVisualDescendants().OfType<Button>().Should().NotContain(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "TransferDataButton");
            NoBannedText(settings, "Настройки → Операции");
            SaveShot(settings, $"import-kassa-settings-operations-{Lang(language)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            settings.Close();
            UiShot.Pump();
        }

        var warehouse = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using (UiShot.Render(warehouse, new UiSize(w, h))) { }
            UiShot.Settle(6);
            // вкладка «Приёмка» не показана — её кнопки в визуальном дереве нет в любом случае, поэтому проверяем родителя именованного элемента
            warehouse.FindControl<Button>("BulkImportButton")!.Parent.Should().BeNull("кнопки «Массовая загрузка» в строке товаров нет");
            warehouse.FindControl<Button>("ReceivingInvoiceImportButton")!.Parent.Should().BeNull("кнопки «Загрузить накладную» в приёмке нет");
            NoBannedText(warehouse, "Склад");

            // пустой список товаров: вместо «Массовая загрузка» — пояснение
            var empty = CallBuildProductsEmpty(warehouse, filtered: false);
            var host = new Window { Content = empty, Width = w, Height = h };
            try
            {
                host.Show();
                UiShot.Settle(4);
                NoBannedText(host, "пустой список товаров");
                Texts(host).Should().Contain(t => t.Contains(ImportAccess.KassaNote), "вместо кнопки импорта — «Импорт — в программе владельца»");
                host.GetVisualDescendants().OfType<Button>().Select(b => UiDrive.TextOf(b)).Should().ContainSingle("остаётся только «Создать товар»");
                SaveShot(host, $"import-kassa-warehouse-empty-{Lang(language)}-{w}x{h}", new UiSize(w, h));
            }
            finally
            {
                host.Close();
                UiShot.Pump();
            }

            // «Массовое добавление»: пустой список — без «Загрузить из файла (Excel)», с пояснением
            var bulkEmpty = (Control)typeof(WarehouseWindow).GetField("_bulkEmpty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(warehouse)!;
            var bulkHost = new Window { Width = w, Height = h };
            ((Panel?)bulkEmpty.Parent)?.Children.Remove(bulkEmpty);
            bulkHost.Content = bulkEmpty;
            try
            {
                bulkHost.Show();
                UiShot.Settle(4);
                NoBannedText(bulkHost, "«Массовое добавление» (пустой список)");
                Texts(bulkHost).Should().Contain(t => t.Contains(ImportAccess.KassaNote));
                SaveShot(bulkHost, $"import-kassa-bulkscan-empty-{Lang(language)}-{w}x{h}", new UiSize(w, h));
            }
            finally
            {
                bulkHost.Content = null;
                bulkHost.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            warehouse.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Меню ☰ кассы: нет пункта и плитки «Перенос данных»; поиск по меню (Ctrl+K) по словам «импорт», «перенос данных», «из файла» его не находит.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Kassa_SideMenuAndSearch_HaveNoTransferData(AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = false;
        var savedLayout = UserPreferences.Instance.MainLayoutMode;
        UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using var guard = UiRobot.Guard();
        try
        {
            using (UiShot.Render(main, UiSize.Large)) { }
            main.Activate();
            UiShot.Settle(10, 40);
            var vm = (MainWindowViewModel)main.DataContext!;
            vm.Toolbar.ToggleSideMenuCommand.Execute(null);
            UiShot.Settle(10, 40);
            main.GetVisualDescendants().OfType<Button>().Should().NotContain(b => b.Name == "MiTransferData", "плитки «Перенос данных» в меню ☰ нет");
            NoBannedText(main, "меню ☰ (пункты и плитки)");

            var search = main.GetVisualDescendants().OfType<SideMenuView>().Single().FindControl<TextBox>("MenuSearchBox")!;
            foreach (var query in new[] { "импорт", "перенос данных", "из файла", "import", "excel", "мойсклад" })
            {
                search.Text = query;
                UiShot.Settle(4, 30);
                main.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && (b.Name ?? "").StartsWith("Mi", StringComparison.Ordinal))
                    .Select(b => UiDrive.TextOf(b) ?? "").Should().NotContain(t => Banned.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase)), $"поиск «{query}» не находит перенос данных");
            }
            search.Text = "";
            vm.CloseSideMenu();
            UiShot.Settle(4, 30);
        }
        finally
        {
            UserPreferences.Instance.MainLayoutMode = savedLayout;
            UiShot.Pump();
        }
    }

    /// <summary>Обучение кассира и мастер первого запуска: нет шага/кнопки «Перенести товары из другой программы» и «Загрузить из файла».</summary>
    [AvaloniaFact]
    public async Task Kassa_TrainingAndFirstRun_HaveNoImportButtons()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        IsOwnerProp.SetValue(null, false);
        CoachTour.DefaultSteps().SelectMany(s => s.Actions ?? []).Select(a => a.Key).Should().NotContain("import", "в обучении нет кнопки переноса товаров");

        var wizard = new FirstRunWizardWindow(new FirstRunWizardWindow.Actions { AddProducts = () => Task.FromResult<string?>(null) });
        try
        {
            wizard.Show();
            UiShot.Settle(4);
            // шаг 4 «Первые товары» — тот, где раньше была кнопка «Загрузить из файла»
            typeof(FirstRunWizardWindow).GetMethod("Go", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, [4]);
            UiShot.Settle(4);
            wizard.Step.Should().Be(4);
            wizard.GetVisualDescendants().OfType<Button>().Should().NotContain(b => b.Name == "WizardImportProducts");
            NoBannedText(wizard, "мастер первого запуска, шаг «Первые товары»");
            Texts(wizard).Should().Contain(t => t.Contains("в программе владельца"));
        }
        finally
        {
            wizard.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ ПРОГРАММА ВЛАДЕЛЬЦА

    /// <summary>Программа владельца: карточка «Перенос данных» в «Настройках → Операции», кнопки «Массовая загрузка» и «Загрузить накладную» в «Складе», мастер открывается.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(SizesAndThreeLanguages))]
    public async Task Owner_HasAllImportEntryPoints_AndWizardOpens(int w, int h, AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        IsOwnerProp.SetValue(null, true);
        BulkImportWizardWindow.CanOpenTransferOverride = null;
        ImportAccess.Allowed.Should().BeTrue();
        BulkImportWizardWindow.CanOpenTransfer().Should().BeTrue("владелец (все права в тестовой среде) может открыть перенос данных");

        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = w;
        settings.Height = h;
        try
        {
            settings.Show();
            UiShot.Settle(6);
            settings.SelectNavTab("operations").Should().BeTrue();
            UiShot.Settle(6);
            var card = settings.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TransferDataCard");
            card.IsVisible.Should().BeTrue("у владельца карточка «Перенос данных из другой программы» есть");
            card.GetVisualDescendants().OfType<Button>().Should().ContainSingle(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "TransferDataButton");
            SaveShot(settings, $"import-owner-settings-operations-{Lang(language)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            settings.Close();
            UiShot.Pump();
        }

        var warehouse = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using (UiShot.Render(warehouse, new UiSize(w, h))) { }
            UiShot.Settle(6);
            warehouse.GetVisualDescendants().OfType<Button>().Should().Contain(b => b.Name == "BulkImportButton", "у владельца в строке товаров есть «Массовая загрузка»");
            warehouse.FindControl<Button>("ReceivingInvoiceImportButton")!.Parent.Should().NotBeNull("у владельца в приёмке есть «Загрузить накладную»");
            var empty = CallBuildProductsEmpty(warehouse, filtered: false);
            var host = new Window { Content = empty, Width = w, Height = h };
            try
            {
                host.Show();
                UiShot.Settle(4);
                host.GetVisualDescendants().OfType<Button>().Should().HaveCount(2, "в пустом складе владельца — «Создать товар» и «Массовая загрузка»");
                Texts(host).Should().NotContain(t => t.Contains(ImportAccess.KassaNote));
            }
            finally
            {
                host.Close();
                UiShot.Pump();
            }
            SaveShot(warehouse, $"import-owner-warehouse-{Lang(language)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            warehouse.Close();
            UiShot.Pump();
        }

        // мастер открывается одним нажатием и остаётся единственным
        BulkImportWizardWindow.OpenTransfer(null);
        UiShot.Settle(6);
        var openWizard = (BulkImportWizardWindow?)typeof(BulkImportWizardWindow).GetField("_openWizard", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        openWizard.Should().NotBeNull("у владельца мастер «Перенос данных» открылся");
        try
        {
            BulkImportWizardWindow.OpenTransfer(null);
            ReferenceEquals(typeof(BulkImportWizardWindow).GetField("_openWizard", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null), openWizard).Should().BeTrue("повторный вход выводит то же окно");
        }
        finally
        {
            openWizard!.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Меню программы владельца: пункт «Перенос данных» («Управление») есть.</summary>
    [AvaloniaFact]
    public async Task Owner_Menu_HasTransferData()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        IsOwnerProp.SetValue(null, true);
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            var buttons = (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;
            buttons.Should().ContainKey("transfer", "в программе владельца «Перенос данных» — пункт меню «Управление»");
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }
}
