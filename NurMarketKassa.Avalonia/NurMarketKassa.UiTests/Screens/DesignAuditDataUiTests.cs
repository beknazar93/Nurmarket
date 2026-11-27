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
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 («дизайн и удобство 10/10»): дизайн-проверка <see cref="DesignAuditUiTests.Audit"/> там, где на экране есть ДАННЫЕ, — пустые окна каталога ничего не показывают, а известный дефект
/// (мелкий остаток поверх цены в виде «список» на 1024×600) жил именно в строках с товарами. Касса поднимается так же, как в галерее KassaLookGalleryTests (поддельный сервер, 12 товаров, в чеке 5 строк, потом всё возвращается):
/// (1) все шесть видов кассы с чеком × светлая/тёмная × ru/ky/uz × 1024×600 и 1366×768; (2) каталог в виде «список» — то же; (3) окно оплаты, прилипшее к окну кассы, и режим «В долг».
/// Правила и порог те же, что у DesignAuditUiTests; известные нарушения — в Snapshots/known-design-audit.txt.</summary>
[Collection("UiScenarios")]
public sealed class DesignAuditDataUiTests
{
    private static readonly UiSize[] Sizes = [new(1024, 600), new(1366, 768)];
    private static readonly AppLanguage[] Langs = [AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek];

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

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static void Shot(Window window, string name, UiSize? size)
    {
        if (Environment.GetEnvironmentVariable("NURMARKET_DESIGN_SHOTS") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        using var shot = UiShot.Render(window, size);
        using var image = SkiaSharp.SKImage.FromBitmap(shot);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    [AvaloniaFact]
    public async Task KassaWithGoods_AllLooks_ListAndCheckout_DesignAudit()
    {
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

        var found = new Dictionary<string, string>();
        var info = new List<string>();
        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using var guard = UiRobot.Guard();
        try
        {
            using (UiShot.Render(main, UiSize.Large)) { }
            main.Activate();
            UiShot.Settle(10, 40);
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            using (UiShot.Render(main, UiSize.Large)) { }
            Scan(main, "3*4700000000001");
            Scan(main, "4700000000003");
            Scan(main, "2*4700000000004");
            Scan(main, "4700000000011");
            Scan(main, "4700000000007");

            // (1) шесть видов кассы с чеком
            foreach (var layout in new[] { KassaLayouts.Standard, KassaLayouts.Table, KassaLayouts.Cards, KassaLayouts.Minimal, KassaLayouts.Pro, KassaLayouts.OneC })
            {
                UserPreferences.Instance.MainLayoutMode = layout;
                NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(layout);
                UiShot.Settle(8, 40);
                foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
                foreach (var lang in Langs)
                foreach (var size in Sizes)
                {
                    UiShot.Setup(theme, lang);
                    using (UiShot.Render(main, size)) { }
                    UiShot.Settle(3);
                    DesignAuditUiTests.Audit(main, "kassa-" + layout, theme, found, info, $"{size} {Code(lang)}");
                    if (theme == UiTheme.Light && lang == AppLanguage.Russian)
                        Shot(main, $"kassa-{layout}-with-goods-{size}", size);
                }
            }

            // (2) каталог в виде «список»
            UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
            NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Standard);
            var catalog = ((MainWindowViewModel)main.DataContext!).Catalog;
            var wasMode = catalog.ViewMode;
            catalog.SetViewMode(CatalogViewMode.Table);
            UiShot.Settle(6, 40);
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
            foreach (var lang in Langs)
            foreach (var size in Sizes)
            {
                UiShot.Setup(theme, lang);
                using (UiShot.Render(main, size)) { }
                UiShot.Settle(3);
                DesignAuditUiTests.Audit(main, "kassa-list", theme, found, info, $"{size} {Code(lang)}");
                if (theme == UiTheme.Light && lang == AppLanguage.Russian)
                    Shot(main, $"kassa-list-{size}", size);
            }
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
            // Окно кассы не закрываем: после закрытия MainWindow тестовый хост Headless зависает (см. SpeedBudgetUiTests) — окно уйдёт вместе с процессом.
            main.Hide();
            UiShot.Pump();
            UiFake.Reset();
            await Task.Run(() => CatalogCacheService.SyncCatalogFullAsync());
            SetShift(savedShift);
        }

        // (3) окно оплаты (прилипшее к окну кассы) и «В долг»
        foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
        foreach (var lang in Langs)
        foreach (var size in Sizes)
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
            DesignAuditUiTests.Audit(dialog, "checkout-docked", theme, found, info, $"{size} {Code(lang)}");
            if (theme == UiTheme.Light && lang == AppLanguage.Russian)
                Shot(dialog, $"checkout-docked-{size}", null);
            var cash = dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "CashReceivedBox");
            if (cash is not null)
            {
                UiDrive.Type(dialog, cash, "13000");
                UiShot.Settle(3, 30);
                DesignAuditUiTests.Audit(dialog, "checkout-docked-cash", theme, found, info, $"{size} {Code(lang)}");
            }
            vm.IsDebt = true;
            UiShot.Settle(4, 30);
            DesignAuditUiTests.Audit(dialog, "checkout-docked-debt", theme, found, info, $"{size} {Code(lang)}");
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }

        DesignAuditUiTests.Finish("data-kassa-list-checkout", "(касса с товарами, список, оплата)", found, info);
    }
}
