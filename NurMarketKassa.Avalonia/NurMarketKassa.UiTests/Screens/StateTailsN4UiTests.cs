using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (пакеты Н-4 + Д-5, «исправь наши минусы»): хвосты состояний и чисел.
/// «Продажи», «Финансы», «Возврат по чеку», «История чеков» при недоступном сервере показывают ОДИН общий блок состояния на месте списка
/// (а не красную полосу внизу окна и не сырой ответ сервера); «Сводка» владельца без связи не оставляет пустых белых карточек;
/// служебный ответ шлюза («Status: No matching mapping found») на экран не попадает; количество и вес пишутся с запятой одной функцией.
/// Снимки «после» для глаз: %TEMP%\nurmarket-tests\ux-n4\after.</summary>
[Collection("UiScenarios")]
public sealed class StateTailsN4UiTests
{
    private static readonly string[] Forbidden = ["Status:", "Exception", "mapping", "StatusCode", "An error occurred"];

    private static List<string> VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text!).ToList();

    private static List<Border> Blocks(Control root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("state-block") && b.IsEffectivelyVisible).ToList();

    private static void Save(Window window, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ux-n4", "after");
        Directory.CreateDirectory(dir);
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    public static IEnumerable<object[]> Screens()
    {
        foreach (var name in new[] { "sales", "finance", "return-sale" })
        {
            yield return [name, UiTheme.Light, AppLanguage.Russian, 1366, 768];
            yield return [name, UiTheme.Dark, AppLanguage.Kyrgyz, 1024, 600];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public async Task ListWindow_WhenServerFails_ShowsOneBlockInPlaceOfTheList(string screenName, UiTheme theme, AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiFake.Reset(); // заглушки продаж от соседних тестов сняты: сервер на списки отвечает сбоем
        UiShot.Setup(theme, lang);
        var screen = ScreenCatalog.Get(screenName);
        var window = screen.Create();
        try
        {
            using var _ = UiShot.Render(window, screen.Kind == ScreenKind.Window ? new UiSize(width, height) : null);
            UiShot.Settle(40, 50);
            Save(window, $"{screenName}-{UiShot.Slug(theme, lang)}-{width}x{height}");

            var blocks = Blocks(window);
            blocks.Should().ContainSingle($"{screenName}: при сбое сервера — один блок состояния на месте списка");
            blocks[0].GetVisualDescendants().OfType<Button>().Should().Contain(b => Equals(b.Content, StateBlock.RetryText), "в блоке есть «Повторить»");
            var texts = VisibleTexts(window);
            foreach (var text in texts)
                foreach (var bad in Forbidden)
                    text.Should().NotContain(bad, $"{screenName}: сырой ответ сервера на экране: «{text}»");
            // Причина написана один раз: красной полосы внизу окна с тем же смыслом больше нет.
            var notice = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "NoticeBox" && b.IsEffectivelyVisible).ToList();
            notice.Should().BeEmpty($"{screenName}: полоса сообщения внизу не дублирует блок состояния");
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(UiTheme.Light, AppLanguage.Russian, 1366, 768)]
    [InlineData(UiTheme.Dark, AppLanguage.Kyrgyz, 1024, 600)]
    public async Task OwnerOverview_WithoutServer_HasNoBlankCards(UiTheme theme, AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiFake.Reset(); // заглушки продаж от соседних тестов сняты: сервер на списки отвечает сбоем
        UiShot.Setup(theme, lang);
        var window = ScreenCatalog.Get("owner-shell").Create();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(40, 50);
            Save(window, $"owner-shell-{UiShot.Slug(theme, lang)}-{width}x{height}");

            var chart = (Control)UiDrive.Find<Grid>(window, "ChartBars").Parent!;
            Blocks(chart).Should().ContainSingle("в карточке графика — блок «Сервер недоступен» с «Повторить»");
            foreach (var name in new[] { "PaymentsLegend", "RecentList", "TopList" })
            {
                var list = UiDrive.Find<StackPanel>(window, name);
                list.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text))
                    .Should().BeTrue($"карточка {name} без связи не пустая — в ней подпись «нет данных»");
            }
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }

    [Fact]
    public void GatewayAnswer_IsNotShownAsFieldError()
    {
        var payload = JsonDocument.Parse("{\"Status\":\"No matching mapping found\"}").RootElement;
        var text = ServerTelegramBotApi.DescribeFields(new ApiException("Status: No matching mapping found", 404, payload));
        text.Should().NotContain("Status").And.NotContain("mapping");

        // Настоящая ошибка поля формы (DRF) по-прежнему видна вместе с именем поля.
        var form = JsonDocument.Parse("{\"command\":[\"Такая команда уже есть\"]}").RootElement;
        ServerTelegramBotApi.DescribeFields(new ApiException("command: Такая команда уже есть", 400, form)).Should().Be("command: Такая команда уже есть");
    }

    [Fact]
    public void UserText_KeepsOwnMessages_AndHidesRawOnes()
    {
        ServerOutageMonitor.UserText(new InvalidOperationException("Не выбран поставщик")).Should().Be("Не выбран поставщик");
        ServerOutageMonitor.UserText(new InvalidOperationException("Sequence contains no elements")).Should().NotContain("Sequence");
        ServerOutageMonitor.UserText(new System.Net.Http.HttpRequestException("An error occurred while sending the request.")).Should().NotContain("An error occurred");
    }

    [Theory]
    [InlineData(3d, "3")]
    [InlineData(1.25d, "1,25")]
    [InlineData(0.5d, "0,5")]
    [InlineData(2.0000001d, "2")]
    [InlineData(1234.5d, "1234,5")]
    public void Quantity_IsShownWithCommaByOneFunction(double value, string expected) =>
        MoneyText.Qty(value).Should().Be(expected);
}

/// <summary>2026-11-13 (Д-5, решение координатора): «храповик» числовых форматов в окнах. Глобальную культуру программа не задаёт, поэтому
/// формат числа без общего форматтера (MoneyText.Amount / Som / Price / Weight / Qty) пишет точку (InvariantCulture) или зависит от настроек
/// Windows. Тест считает такие форматы в Views и Views.axaml (*.cs и *.axaml) и сверяет с Snapshots/known-number-formats.txt (файл;число):
/// новый файл с форматом или рост числа в файле — провал. Перевёл формат на MoneyText — число падает, список обновляется
/// (test-safe.ps1 -Phase ui -Filter "FullyQualifiedName~NumberFormatRatchet" -UpdateSnapshots).</summary>
public sealed class NumberFormatRatchetTests
{
    private static readonly System.Text.RegularExpressions.Regex InCode = new(
        @"\.ToString\(\s*""(?:[0#,.]+|[FN]\d?)""\s*(?:,\s*CultureInfo\.InvariantCulture\s*)?\)|\{[^{}""]*:(?:0[0#,.]*|[FN]\d)\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex InAxaml = new(
        @"StringFormat=[^""]*\{0:(?:[FN]\d|0[0#,.]*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string ListPath = Path.Combine(Snapshots.PngSnapshot.BaselineDir, "..", "known-number-formats.txt");

    private static string AppDir => Path.GetFullPath(Path.Combine(Snapshots.PngSnapshot.BaselineDir, "..", "..", "..", "NurMarketKassa.Avalonia"));

    private static Dictionary<string, int> Scan()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { "Views", "Views.axaml" })
        {
            var dir = Path.Combine(AppDir, folder);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var isCode = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                if (!isCode && !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                    continue;
                var count = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.TrimStart();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("<!--", StringComparison.Ordinal))
                        continue;
                    // журнал — не экран
                    if (isCode && (line.Contains("PosLogger.Log(", StringComparison.Ordinal) || line.Contains("LogFailure(", StringComparison.Ordinal)))
                        continue;
                    count += (isCode ? InCode : InAxaml).Matches(line).Count;
                }
                if (count > 0)
                    result[Path.GetRelativePath(AppDir, file).Replace(Path.DirectorySeparatorChar, '/')] = count;
            }
        }
        return result;
    }

    [Fact]
    public void NoNewNumberFormatsWithoutSharedFormatter()
    {
        Directory.Exists(AppDir).Should().BeTrue("папка приложения рядом с тестами");
        var now = Scan();
        if (Snapshots.PngSnapshot.UpdateMode || !File.Exists(ListPath))
        {
            var lines = new List<string>
            {
                "# 2026-11-13 (Д-5): числовые форматы в окнах мимо общего форматтера MoneyText (файл;число). Только уменьшать.",
            };
            lines.AddRange(now.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key};{x.Value}"));
            File.WriteAllLines(ListPath, lines);
            return;
        }
        var known = File.ReadAllLines(ListPath).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(';'))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        var worse = now.Where(x => !known.TryGetValue(x.Key, out var was) || x.Value > was)
            .Select(x => $"  {x.Key}: было {(known.TryGetValue(x.Key, out var was) ? was : 0)}, стало {x.Value}").ToList();
        Assert.True(worse.Count == 0, "Новые числовые форматы в окнах мимо общего форматтера (нужно MoneyText.Amount / Som / Price / Weight / Qty):" + Environment.NewLine + string.Join(Environment.NewLine, worse));
    }
}
