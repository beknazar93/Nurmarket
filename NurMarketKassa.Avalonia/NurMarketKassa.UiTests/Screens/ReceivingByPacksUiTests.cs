using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-07 («Принять упаковками» в «Закупки → Приём товара»): настоящее окно в Avalonia.Headless — ссылка «упаковками» включает режим, поля «упак.» и «шт в упак.» помещаются
/// при 1024 и 1366, название товара не ужимается (баг «Наименование до пикселей» не повторён), цена за упаковку считается в сумму «упаковки × цена упаковки».</summary>
[Collection("UiScenarios")]
public sealed class ReceivingByPacksUiTests
{
    /// <summary>Нажать подпись-ссылку способа приёмки и выбрать пункт меню по началу заголовка (меню — ReceivingPackMenu; пункты лежат в слое всплывающих окон окна).</summary>
    internal static void PickPackMenu(Window window, Control link, string itemStartsWith)
    {
        UiDrive.Click(window, link);
        UiShot.Settle(6);
        var item = window.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.IsEffectivelyVisible && m.Header is string h && h.TrimStart().TrimStart('✓').TrimStart().StartsWith(itemStartsWith, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Нет пункта меню «" + itemStartsWith + "»: " + string.Join(" | ", window.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header)));
        item.IsEnabled.Should().BeTrue("пункт «" + itemStartsWith + "» доступен");
        UiDrive.Click(window, item);
        UiShot.Settle();
    }

    [AvaloniaTheory]
    [InlineData(1024, 640)]
    [InlineData(1366, 768)]
    public async Task PurchasesReceive_PackMode_FitsAndKeepsTheNameReadable(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        const string title = "Вода питьевая негазированная 0,5 л";
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", title, "30 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 10, PurchasePrice = 0 });
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", title, "шт", 1.0, (double?)null) }));
            UiShot.Settle(12);

            // 2026-11-09: название товара склада в «Приёме товара» теперь поле ввода (правка названия), а не подпись.
            double NameWidth() => window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Text == title).Bounds.Width;
            var before = NameWidth();
            before.Should().BeGreaterThanOrEqualTo(120, "название читается и до включения режима");

            var toggle = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "упаковками ▾");
            // 2026-11-07: вместо переключателя — меню способа приёмки; у существующего товара «поштучная продажа» недоступна (только новый товар)
            UiDrive.Click(window, toggle);
            UiShot.Settle(6);
            // 2026-11-10 (владелец: «как в вебе — упаковка, продажа поштучно»): и у товара склада доступна (остаток пересчитается через подтверждение «Было → станет»)
            window.GetVisualDescendants().OfType<MenuItem>().Single(m => m.Header is string h && h.Contains("Упаковка с поштучной продажей")).IsEnabled.Should().BeTrue("товар со склада: упаковка уходит в карточку тем же PATCH, что окно товара");
            UiDrive.Click(window, window.GetVisualDescendants().OfType<MenuItem>().Single(m => m.Header is string h && h.Contains("Упаковками → на склад штуки")));
            UiShot.Settle();
            UiShot.Settle();

            var pieces = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark == "шт в упак.");
            var packs = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark == "упак.");
            var price = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(t).StartsWith("Цена закупки", StringComparison.Ordinal) && t.Tag as string == "rcv-cell");
            UiDrive.Type(window, pieces, "24");
            UiDrive.Type(window, packs, "5");
            UiDrive.Type(window, price, "600");
            UiShot.Settle();

            var text = UiDrive.AllText(window);
            text.Should().Contain("5 × 24 = 120 шт.", "под числом упаковок — сколько штук уйдёт на склад");
            text.Should().Contain("за упак. · 25,00 за шт", "под ценой упаковки — цена за штуку");
            Regex.IsMatch(text, @"3[\s  ]?000").Should().BeTrue("сумма закупки строки и накладной = 5 × 600 = 3 000: " + text);
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark == "упак.").Text.Should().Be("5");

            foreach (var box in new[] { pieces, packs, price })
            {
                var right = box.TranslatePoint(new Point(box.Bounds.Width, 0), window)!.Value.X;
                right.Should().BeLessThanOrEqualTo(window.Bounds.Width, "поле целиком в окне");
                box.Bounds.Width.Should().BeGreaterThanOrEqualTo(60, "поле читаемо");
            }
            NameWidth().Should().BeGreaterThanOrEqualTo(120, "название не ужимается режимом упаковок");
            NameWidth().Should().BeGreaterThanOrEqualTo(before - 1, "ширина названия не меняется от режима");

            // Снимок для глаз (не эталон): %TEMP%\nurmarket-tests\ui-actual\packs-{ширина}.png
            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"packs-{width}x{height}.png"), data.ToArray());

            // Обратно в штуки: упаковочные поля скрываются, штуки и цена за штуку остаются.
            PickPackMenu(window, window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "упак. → штуки ▾"), "В штуках (как есть)");
            UiShot.Settle();
            window.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.Watermark == "шт в упак.").Should().BeFalse();
            UiDrive.AllText(window).Should().Contain("120", "штуки остались в «Принимаем»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
