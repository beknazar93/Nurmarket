using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (редизайн вида кассы 2.0): галерея снимков «для глаз» — главный экран кассы во всех шести видах («Классика», «Табличная», «Карточки»,
/// «Минимал», «Профи», «1С»), светлая/тёмная/сенсорная тема, ru/ky, 1366×768 и 1024×600, пустой чек и чек с товарами, окно оплаты и «Продажа прошла».
/// Ничего не сравнивает — только сохраняет полные кадры в %TEMP%\nurmarket-tests\ui-look\{этап}\*.png (этап — переменная NURMARKET_LOOK_STAGE: before/after).
/// Без переменной тест ничего не делает (в общий прогон не входит: он подменяет товары поддельного сервера на 12 штук и потом возвращает прежние).</summary>
[Collection("UiScenarios")]
public sealed class KassaLookGalleryTests
{
    private static string? Stage => Environment.GetEnvironmentVariable("NURMARKET_LOOK_STAGE") is { Length: > 0 } s ? s : null;

    private static string Dir => Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-look", Stage ?? "after");

    private static void Save(Window window, string name, UiSize? size = null)
    {
        Directory.CreateDirectory(Dir);
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(Dir, name + ".png"), data.ToArray());
    }

    private static readonly (string Name, string Price, string Qty, bool Weight, string Category)[] Goods =
    [
        ("Хлеб «Бородинский» нарезной", "45.00", "10", false, "Хлеб"),
        ("Яблоки Голден", "120.00", "5.5", true, "Фрукты"),
        ("Молоко 3,2 % 1 л", "89.00", "24", false, "Молочные"),
        ("Кока-Кола 1 л", "95.00", "3", false, "Напитки"),
        ("Сыр Российский", "640.00", "12.4", true, "Молочные"),
        ("Чай чёрный листовой 100 пакетиков", "310.00", "0", false, "Чай, кофе"),
        ("Шоколад молочный", "135.50", "48", false, "Сладости"),
        ("Масло подсолнечное 1 л", "210.00", "17", false, "Бакалея"),
        ("Сахар 1 кг", "98.00", "150", false, "Бакалея"),
        ("Курица охлаждённая", "385.00", "8.2", true, "Мясо"),
        ("Стиральный порошок 3 кг", "12500.00", "6", false, "Бытовая химия"),
        ("Вода питьевая 5 л", "70.00", "32", false, "Напитки"),
    ];

    private static string GoodsJson()
    {
        var items = Goods.Select((g, i) => new Dictionary<string, object?>
        {
            ["id"] = "look-" + (i + 1),
            ["name"] = g.Name,
            ["barcode"] = (4700000000000 + i + 1).ToString(),
            ["price"] = g.Price,
            ["quantity"] = g.Qty,
            ["is_weight"] = g.Weight,
            ["category"] = g.Category,
        });
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["count"] = Goods.Length, ["next"] = null, ["previous"] = null, ["results"] = items });
    }

    private static void SetShift(string? id)
    {
        PosApp.ActiveShiftId = id;
        TestHost.Services.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().ActiveShiftId = id;
    }

    private static void Scan(Window main, string code)
    {
        var search = main.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Name == "ProductSearchBox");
        if (search is null)
            return;
        UiDrive.Type(main, search, code);
        Thread.Sleep(400);
        main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiShot.Settle(8, 40);
    }

    [AvaloniaFact]
    public async Task Gallery_AllKassaLooks()
    {
        if (Stage is null)
            return;
        await TestHost.WarmAsync();
        var savedShift = PosApp.ActiveShiftId;
        var savedLayout = UserPreferences.Instance.MainLayoutMode;
        UiFake.Reset();
        UiFake.Server.Server.Reset();
        UiFake.Server.StubLogin();
        UiFake.Server.StubProfile();
        UiFake.Server.Json("GET", "/api/main/products/list/", 200, GoodsJson());
        UiFake.Server.StubCheckout();
        UiFake.Server.StubShiftOpen();
        await Task.Run(() => CatalogCacheService.SyncCatalogFullAsync());
        SetShift(Guid.NewGuid().ToString());
        TrainingMode.Exit();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;

        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using var guard = UiRobot.Guard();
        try
        {
            using (UiShot.Render(main, UiSize.Large)) { }
            main.Activate();
            UiShot.Settle(10, 40);

            // «Классика»: пустой чек.
            foreach (var (theme, lang, size) in new[]
                     {
                         (UiTheme.Light, AppLanguage.Russian, UiSize.Large),
                         (UiTheme.Dark, AppLanguage.Russian, UiSize.Large),
                         (UiTheme.Light, AppLanguage.Kyrgyz, new UiSize(1024, 600)),
                     })
            {
                UiShot.Setup(theme, lang);
                Save(main, $"standard-empty-{UiShot.Slug(theme, lang, size)}", size);
            }

            // Чек с товарами (скан в единственное поле «Классики»).
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            using (UiShot.Render(main, UiSize.Large)) { }
            Scan(main, "3*4700000000001");
            Scan(main, "4700000000003");
            Scan(main, "2*4700000000004");
            Scan(main, "4700000000011");
            Scan(main, "4700000000007");

            foreach (var layout in new[] { KassaLayouts.Standard, KassaLayouts.Table, KassaLayouts.Cards, KassaLayouts.Minimal, KassaLayouts.Pro, KassaLayouts.OneC })
            {
                UserPreferences.Instance.MainLayoutMode = layout;
                NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(layout);
                UiShot.Settle(8, 40);
                foreach (var (theme, lang, size) in new[]
                         {
                             (UiTheme.Light, AppLanguage.Russian, UiSize.Large),
                             (UiTheme.Dark, AppLanguage.Russian, UiSize.Large),
                             (UiTheme.Light, AppLanguage.Kyrgyz, new UiSize(1024, 600)),
                             (UiTheme.Dark, AppLanguage.Kyrgyz, new UiSize(1024, 600)),
                             (UiTheme.Touch, AppLanguage.Russian, UiSize.Large),
                         })
                {
                    if (layout != KassaLayouts.Standard && (theme != UiTheme.Light || lang != AppLanguage.Russian))
                        continue;
                    UiShot.Setup(theme, lang);
                    Save(main, $"{layout}-lines-{UiShot.Slug(theme, lang, size)}", size);
                }
            }

            // «Классика»: каталог списком.
            UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
            NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Standard);
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            var catalog = ((MainWindowViewModel)main.DataContext!).Catalog;
            var wasMode = catalog.ViewMode;
            catalog.SetViewMode(CatalogViewMode.Table);
            UiShot.Settle(6, 40);
            Save(main, "standard-list-light-ru-1366x768", UiSize.Large);
            UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
            Save(main, "standard-list-dark-ru-1366x768", UiSize.Large);
            catalog.SetViewMode(wasMode);
            UiShot.Settle(4, 30);
        }
        finally
        {
            try { ((MainWindowViewModel)main.DataContext!).Basket.ClearTrainingReceipt(); } catch { /* чек теста не оставляем */ }
            UserPreferences.Instance.MainLayoutMode = savedLayout;
            NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Normalize(savedLayout));
            UiRobot.CloseAll();
            UiRobot.Main = null;
            // Окно кассы не закрываем: после закрытия MainWindow тестовый хост Headless зависает (см. SpeedBudgetUiTests) — прогон галереи отдельный, окно уйдёт вместе с процессом.
            main.Hide();
            UiShot.Pump();
            UiFake.Reset();
            await Task.Run(() => CatalogCacheService.SyncCatalogFullAsync());
            SetShift(savedShift);
        }
    }

    /// <summary>Окно оплаты так, как его видит кассир: прилипшим к окну кассы 1366×768 и 1024×600 (две колонки), наличные с суммой «получено», «В долг»; «Оплата прошла».
    /// Главное окно кассы здесь не создаётся (владелец окна — пустое окно того же размера).</summary>
    [AvaloniaFact]
    public async Task Gallery_Checkout()
    {
        if (Stage is null)
            return;
        await TestHost.WarmAsync();
        foreach (var (theme, lang, size) in new[]
                 {
                     (UiTheme.Light, AppLanguage.Russian, UiSize.Large),
                     (UiTheme.Dark, AppLanguage.Russian, UiSize.Large),
                     (UiTheme.Light, AppLanguage.Russian, new UiSize(1024, 600)),
                     (UiTheme.Dark, AppLanguage.Kyrgyz, new UiSize(1024, 600)),
                     (UiTheme.Touch, AppLanguage.Russian, UiSize.Large),
                 })
        {
            UiShot.Setup(theme, lang);
            var owner = new Window { Width = size.Width, Height = size.Height };
            owner.Show();
            UiShot.Pump();
            var cartJson = CartJson([("Хлеб «Бородинский» нарезной", 3, 45, 0), ("Стиральный порошок 3 кг", 1, 12500, 0)]);
            var totals = CartTotalsCalculator.Calculate(JsonDocument.Parse(cartJson).RootElement);
            var vm = new CheckoutViewModel(totals, "", "") { IsPrintReceiptEnabled = false };
            var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
            dialog.DockTo(owner);
            dialog.Show(owner);
            UiShot.Settle(6, 30);
            var slug = UiShot.Slug(theme, lang, size);
            Save(dialog, $"checkout-{slug}");
            var cash = dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CashReceivedBox");
            if (cash is not null)
            {
                UiDrive.Type(dialog, cash, "13000");
                UiShot.Settle(3, 30);
                Save(dialog, $"checkout-cash-{slug}");
            }
            vm.IsDebt = true;
            UiShot.Settle(4, 30);
            Save(dialog, $"checkout-debt-{slug}");
            dialog.Close();
            owner.Close();
            UiShot.Pump();

            var success = new SaleSuccessDialog(12635, false);
            Save(success, $"sale-success-{UiShot.Slug(theme, lang)}");
            success.Close();
            UiShot.Pump();
        }
    }
}
