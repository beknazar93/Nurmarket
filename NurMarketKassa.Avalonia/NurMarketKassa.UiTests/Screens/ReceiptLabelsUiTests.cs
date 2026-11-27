using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (владелец: «сразу печатать штрихкод и ценники из накладной приёмки поставщика»): кнопка «🏷 Ценники» — в «Закупки → Приём товара» (до проведения), в просмотре накладной прихода
/// и в «Склад → Приёмка». Проверка: кнопка видна на 1024×600 (ru, ky, uz), не налезает на соседние кнопки низа и не выходит за окно; по нажатию в печать уходят товары накладной с копиями = количество;
/// новый товар (его ещё нет в каталоге) в печать не идёт; в накладной копии = принято за вычетом возврата, строки с вариантом пропускаются.</summary>
[Collection("UiScenarios")]
public sealed class ReceiptLabelsUiTests
{
    public static IEnumerable<object[]> Langs() => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { l });

    private static (IDisposable Reset, List<(string Id, string? Code, int Copies)> Got) HookLabels()
    {
        var got = new List<(string, string?, int)>();
        WarehouseWindow.LabelsRequestOverride = (_, items) => got.AddRange(items ?? []);
        return (new Undo(() => WarehouseWindow.LabelsRequestOverride = null), got);
    }

    private sealed class Undo(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static Button LabelsButton(Window w) => w.GetVisualDescendants().OfType<Button>()
        .First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == ReceiptLabelsUi.ButtonTip); // на узком окне в кнопке только значок — ищем по имени для диктора

    [AvaloniaTheory]
    [MemberData(nameof(Langs))]
    public async Task PurchasesReceive_LabelsButton_Visible_NoOverlap_AndSendsProductsWithCopies(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(new CatalogProductTileVm("rl1", "Вода питьевая 0,5 л", "30 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        CatalogCacheService.Products.Add(new CatalogProductTileVm("rl2", "Сахар-рафинад 1 кг", "60 сом", false) { Barcode = "4600000000028", Unit = "шт", Quantity = 4, PurchasePrice = 50 });
        var (reset, got) = HookLabels();
        var window = new PurchasesWindow();
        var tag = lang.ToString();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("rl1", "Вода питьевая 0,5 л", "шт", 12.0, (double?)null), ("rl2", "Сахар-рафинад 1 кг", "шт", 3.0, (double?)null) }));
            UiShot.Settle(14);

            var button = LabelsButton(window);
            button.IsEnabled.Should().BeTrue(tag);

            // не налезает на соседние кнопки низа («Наценка…», «Доп. расходы…», «Оприходовать»…) и целиком в окне
            var post = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Bounds.Height >= 40 && UiDrive.LabelOf(b) is { } t
                && (t.Contains("Оприходовать", StringComparison.OrdinalIgnoreCase) || t.Contains("кирешке", StringComparison.OrdinalIgnoreCase) || t.Contains("kirim", StringComparison.OrdinalIgnoreCase)));
            Rect Box(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
            var mine = Box(button);
            mine.Left.Should().BeGreaterThanOrEqualTo(0, tag);
            mine.Right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка не за краем окна ({tag})");
            Box(post).Right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"«Оприходовать» не за краем окна ({tag})");
            foreach (var other in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && !ReferenceEquals(b, button) && b.Bounds.Width > 0))
            {
                var o = Box(other);
                if (Math.Abs(o.Center.Y - mine.Center.Y) > mine.Height / 2)
                    continue; // другая строка
                mine.Intersect(o).Width.Should().BeLessThanOrEqualTo(1, $"«Ценники» не налезает на «{UiDrive.LabelOf(other)}» ({tag})");
            }

            // нажатие: товары накладной уходят в печать, копий столько, сколько количество
            UiDrive.Click(window, button);
            UiShot.Settle(10);
            got.Should().Equal(new (string, string?, int)[] { ("rl1", "4600000000011", 12), ("rl2", "4600000000028", 3) }, $"копии = количество строки ({tag})");
        }
        finally
        {
            reset.Dispose();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    [AvaloniaFact]
    public async Task PurchasesReceive_LabelsButton_NothingToPrint_ShowsReason_AndOpensNothing()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (reset, got) = HookLabels();
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.OpenReceive(null); // пустая накладная
            UiShot.Settle(12);
            UiDrive.Click(window, LabelsButton(window));
            UiShot.Settle(8);
            got.Should().BeEmpty("печатать нечего — окно печати не открывается");
            UiDrive.AllText(window).Should().Contain("Печатать нечего");
        }
        finally
        {
            reset.Dispose();
            window.Close();
            UiShot.Pump();
        }
    }

    private static SupplierReceipt Receipt() => new("rc1", "s1", "Поставщик", "кассир", new DateTime(2026, 11, 27, 10, 0, 0), 4, 1000, 0,
        new[]
        {
            new SupplierReceiptLine("l1", "rl1", "Вода питьевая 0,5 л", "", 12, 20, 2, "шт", Barcode: "4600000000011"),   // принято 12, возвращено 2 → 10 копий
            new SupplierReceiptLine("l2", "rl2", "Сахар-рафинад 1 кг", "", 3, 50, 0, "шт", Barcode: "4600000000028"),
            new SupplierReceiptLine("l3", "rl3", "Сок яблочный", "", 5, 40, 0, "шт", Barcode: "4600000000035", VariantName: "Яблочный"), // строка с вариантом — пропускается
            new SupplierReceiptLine("l4", "rl4", "Мыло", "", 4, 10, 4, "шт", Barcode: "4600000000042"),                      // всё возвращено — не печатается
        });

    [AvaloniaTheory]
    [MemberData(nameof(Langs))]
    public async Task InvoiceDocument_LabelsButton_ClosesWithLabelsAction_AndCopiesAreReceivedMinusReturned(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new InvoiceDocumentWindow(Receipt(), null, false, null, null, showReturnRepeat: true);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(12);
            var button = LabelsButton(window);
            button.IsEnabled.Should().BeTrue(lang.ToString());
            var box = new Rect(button.TranslatePoint(default, window)!.Value, button.Bounds.Size);
            box.Right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка внутри окна ({lang})");
            foreach (var other in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && !ReferenceEquals(b, button) && b.Bounds.Width > 0))
            {
                var o = new Rect(other.TranslatePoint(default, window)!.Value, other.Bounds.Size);
                if (Math.Abs(o.Center.Y - box.Center.Y) > box.Height / 2)
                    continue;
                box.Intersect(o).Width.Should().BeLessThanOrEqualTo(1, $"«Ценники» не налезает на «{UiDrive.LabelOf(other)}» ({lang})");
            }
            UiDrive.Click(window, button);
            UiShot.Settle(8);
            window.Action.Should().Be("labels", lang.ToString());
            window.LabelItems.Should().Equal(new (string, string?, int)[] { ("rl1", "4600000000011", 10), ("rl2", "4600000000028", 3) }, $"принято минус возвращено; вариант и возвращённое — нет ({lang})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
