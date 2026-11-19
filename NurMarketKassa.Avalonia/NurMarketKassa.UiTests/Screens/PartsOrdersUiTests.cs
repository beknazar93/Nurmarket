using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (сфера «Автозапчасти», часть Б): окна «Заказы под клиента» (список, карточка, окно заказа) и срок возврата в Avalonia.Headless на временном файле.
/// Текст не обрезан на 1024×600 и 1366×768 в ru, ky, uz (и tr, en). Снимки: %TEMP%\nurmarket-tests\ui-actual\partsorders-*.png. Окна подтверждения (PIN, «Выдан») модальные —
/// их проверяют правила в unit-тестах (AutoPartsPartBTests), здесь — действия без модалок.</summary>
[Collection("UiScenarios")]
public sealed class PartsOrdersUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "partsorders-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public PartsOrdersUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        AutoPartsRules.ActiveOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string Code(AppLanguage l) => l switch
    {
        AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", AppLanguage.English => "en", _ => "ru",
    };

    public static IEnumerable<object[]> Cases() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish, AppLanguage.English }
            .SelectMany(l => new[] { (1024, 600), (1366, 768) }.Select(s => new object[] { l, s.Item1, s.Item2 }));

    private PartsOrderDeps Deps(out List<string> opened, bool sell = true, bool start = true)
    {
        var urls = new List<string>();
        opened = urls;
        return new PartsOrderDeps
        {
            Store = new PartsOrderStore(Path.Combine(_dir, "orders-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 20, 10, 0, 0)),
            SellAsync = sell ? _ => Task.FromResult(2) : null,
            AskPin = () => Task.FromResult(false),
            OpenUrl = url => { urls.Add(url); return true; },
            ShopName = () => "Автодом",
            StartTariff = () => start,
        };
    }

    private static PartsOrder Seed(PartsOrderStore store, string client, string phone, decimal prepay, string status = PartsOrderStatus.New)
    {
        var o = new PartsOrder
        {
            ClientName = client,
            Phone = phone,
            Prepayment = prepay,
            Status = status,
            DueDate = new DateTime(2026, 11, 25),
            Supplier = "Автозапчасти Азия",
            Comment = "Позвонить, когда придёт",
            Lines = new List<PartsOrderLine>
            {
                new() { Title = "Фильтр масляный Toyota 90915-YZZJ2", Qty = 2m, Price = 450m },
                new() { Title = "Датчик кислорода (под заказ)", Qty = 1m, Price = 3200m, OnOrder = true },
            },
        };
        store.Save(o);
        return o;
    }

    private static T Named<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    [AvaloniaFact]
    public async Task List_Select_Advance_WhatsApp_AndOwnerHasNoSellButton()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var deps = Deps(out var urls);
        var order = Seed(deps.Store, "СТО Асан", "0700 123 456", 1000m);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("PartsOrderRow_", StringComparison.Ordinal) == true).Should().Be(1);
            window.Select(order.Id);
            UiShot.Settle(4);
            Named<Button>(window, "PartsOrderSell").IsVisible.Should().BeTrue("в кассе есть «Оформить продажу»");
            window.StatusMessage.Should().Contain("Предоплаты по заказам на руках", "предоплата на руках видна в подсказке под списком");

            ClickNamed(window, "PartsOrderAdvance");
            UiShot.Settle(4);
            deps.Store.Find(order.Id)!.Status.Should().Be(PartsOrderStatus.Ordered);

            ClickNamed(window, "PartsOrderWhatsApp");
            urls.Should().ContainSingle().Which.Should().StartWith("https://wa.me/996700123456?text=");
        }
        finally
        {
            window.Close();
        }

        var ownerDeps = Deps(out _, sell: false);
        Seed(ownerDeps.Store, "Омурбек", "0555 000 111", 0m, PartsOrderStatus.Arrived);
        var owner = new PartsOrdersWindow(ownerDeps);
        ((IOwnerSection)owner).AsOwnerSection();
        try
        {
            using var _ = UiShot.Render(owner, new UiSize(1024, 600));
            UiShot.Settle(6);
            owner.Select(ownerDeps.Store.All().Single().Id);
            UiShot.Settle(4);
            owner.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "PartsOrderSell").Should().BeFalse("в программе владельца чека нет");
        }
        finally
        {
            owner.Close();
        }
    }

    private static void ClickNamed(Window w, string name) =>
        Named<Button>(w, name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task SoftLimit_OnStart_ShowsHint_ButDoesNotBlockNewOrder()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var deps = Deps(out _);
        for (var i = 0; i < 11; i++)
            Seed(deps.Store, "Клиент " + i, "0700 000 0" + i.ToString("00"), 0m);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.StatusMessage.Should().Contain("11").And.Contain("Стандарт");
            Named<Button>(window, "PartsOrderNew").IsEnabled.Should().BeTrue("«Старт» — только подсказка, ввод не блокируется");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task OrdersWindow_TextIsNotClipped(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var deps = Deps(out _);
        var a = Seed(deps.Store, "СТО «Авто-Мастер» Бишкек", "0700 123 456", 1000m, PartsOrderStatus.Arrived);
        Seed(deps.Store, "Омурбек Асанов", "0555 000 111", 0m);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            window.Select(a.Id);
            UiShot.Settle(4);
            TextClipChecker.Find(window, $"partsorders-list-{width}", Code(lang)).Should().BeEmpty($"список заказов не обрезан ({Code(lang)} {width}x{height})");
            Save(window, $"partsorders-list-{Code(lang)}-{width}x{height}.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task EditDialog_TextIsNotClipped_AndFitsScreen(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var deps = Deps(out _);
        var order = Seed(deps.Store, "СТО «Авто-Мастер» Бишкек", "0700 123 456", 1000m);
        var dialog = new PartsOrderEditDialog(order);
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(width, height));
            UiShot.Settle(6);
            TextClipChecker.Find(dialog, $"partsorders-edit-{width}", Code(lang)).Should().BeEmpty($"окно заказа не обрезано ({Code(lang)} {width}x{height})");
            Save(dialog, $"partsorders-edit-{Code(lang)}-{width}x{height}.png");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task EditDialog_Prepayment50Percent_And_Validation()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dialog = new PartsOrderEditDialog(new PartsOrder
        {
            Lines = new List<PartsOrderLine> { new() { Title = "Фильтр", Qty = 3m, Price = 100m } },
        });
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 600));
            UiShot.Settle(6);
            ClickNamed(dialog, "OrderPrepay50");
            UiShot.Settle(3);
            Named<TextBox>(dialog, "OrderPrepayment").Text.Should().Be("150");
            Named<TextBlock>(dialog, "OrderSum").Text.Should().Contain("300").And.Contain("150");

            // клиент не указан — не сохраняем, сообщение в окне
            ClickNamed(dialog, "OrderSave");
            UiShot.Settle(3);
            Named<TextBlock>(dialog, "OrderError").IsVisible.Should().BeTrue();

            Named<TextBox>(dialog, "OrderClientName").Text = "СТО Асан";
            UiShot.Settle(2);
            Named<TextBox>(dialog, "OrderPrepayment").Text = "999";
            UiShot.Settle(2);
            ClickNamed(dialog, "OrderSave");
            UiShot.Settle(3);
            Named<TextBlock>(dialog, "OrderError").IsVisible.Should().BeTrue("предоплата больше итога заказа не принимается");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ReturnTerm_AutoParts_UsesOwnerSettingDays_OtherSpheresUnchanged()
    {
        try
        {
            AutoPartsRules.ActiveOverride = true;
            NonExchangeableRules.DaysLimit.Should().Be(AutoPartsRules.GetReturnDays());
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-AutoPartsRules.GetReturnDays() - 6)).Should().BeTrue("через 20 дней при сроке 14 — предупреждение");
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-10)).Should().BeFalse("10 дней — в срок");
            AutoPartsRules.ActiveOverride = false;
            NonExchangeableRules.IsOutOfTerm(DateTime.Today.AddDays(-400)).Should().Be(NonExchangeableRules.DaysLimit > 0, "у других сфер правило прежнее");
        }
        finally
        {
            AutoPartsRules.ActiveOverride = null;
        }
    }
}
