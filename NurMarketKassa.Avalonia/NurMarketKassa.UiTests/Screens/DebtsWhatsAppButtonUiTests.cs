using Avalonia;
using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08, «вернуть кнопку WA»: окно «Долги клиентов» на 1024×600 (ru, ky, светлая и тёмная) с тремя должниками — с номером (кнопка «WA» активна),
/// без номера и без клиента (неактивна, причина рядом). Список подставляется отражением (сервер не нужен). Кнопка помещается в окно, текст карточек не обрезан.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\debts-wa-{тема}-{язык}-1024x600.png. Сама кнопка ничего не открывает (чат открывается только по нажатию).</summary>
[Collection("UiScenarios")]
public sealed class DebtsWhatsAppButtonUiTests
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<object[]> Cases() =>
        new[] { UiTheme.Light, UiTheme.Dark }.SelectMany(t => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { t, l }));

    private static object Sale(Type saleType, string id, string number, int daysAgo, double total) =>
        Activator.CreateInstance(saleType, id, number, DateTime.Now.AddDays(-daysAgo), "Товар", total, 0d, total, "Кассир", "Касса", false, false)!;

    private static object Debtor(Type debtorType, Type saleType, string clientId, string name, string phone, params object[] sales)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(saleType))!;
        foreach (var s in sales)
            list.Add(s);
        return Activator.CreateInstance(debtorType, clientId, name, phone, list)!;
    }

    private static bool IsWa(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "WA");

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task DebtsWindow_ShowsWaButton_EnabledOnlyWithPhone_AndFits1024x600(UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var window = new DebtsWindow();
        try
        {
            var view = (DebtsView)window.Content!;
            var viewType = typeof(DebtsView);
            var saleType = viewType.GetNestedType("DebtSale", BindingFlags.NonPublic)!;
            var debtorType = viewType.GetNestedType("Debtor", BindingFlags.NonPublic)!;
            var debtors = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(debtorType))!;
            debtors.Add(Debtor(debtorType, saleType, "c1", "Айбек Асанов", "0700 123 456", Sale(saleType, "s1", "101", 40, 12345.5), Sale(saleType, "s2", "102", 3, 500)));
            debtors.Add(Debtor(debtorType, saleType, "c2", "Мээрим Без Номера", "", Sale(saleType, "s3", "103", 10, 2000)));
            debtors.Add(Debtor(debtorType, saleType, "", "Без клиента", "", Sale(saleType, "s4", "104", 2, 300)));
            viewType.GetField("_loadStarted", NonPublic)!.SetValue(view, true); // загрузку с сервера не запускаем
            viewType.GetField("_debtors", NonPublic)!.SetValue(view, debtors);

            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            viewType.GetMethod("Render", NonPublic)!.Invoke(view, null);
            UiShot.Settle(6);

            var wa = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && IsWa(b)).ToList();
            var tag = $"{theme} {lang}";
            wa.Should().HaveCount(3, $"кнопка «WA» есть у каждого должника ({tag})");
            wa.Count(b => b.IsEnabled).Should().Be(1, "активна только у должника с номером");
            wa.Where(b => !b.IsEnabled).Should().OnlyContain(b => ToolTip.GetTip(b) is string, "у неактивной — подсказка с причиной");
            wa.First(b => b.IsEnabled).Bounds.Height.Should().BeGreaterThanOrEqualTo(40, "цель нажатия не меньше 40");
            foreach (var b in wa)
            {
                var p = b.TranslatePoint(default, window)!.Value;
                (p.X + b.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка «WA» внутри окна по ширине ({tag})");
                p.X.Should().BeGreaterThanOrEqualTo(0);
            }

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "debts-wa", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"debts-wa-{theme.ToString().ToLowerInvariant()}-{langCode}-1024x600.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
