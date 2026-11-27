using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (просьба владельца: «все новые функции — с описанием и с гиф, как работает»): настоящие GIF-файлы из тех же кадров, что рисует проигрыватель Маркетплейса
/// (MarketplaceMockDemoPlayer, <see cref="MarketplaceDemos"/>) — для сайта и рекламы. Кадр — подпись шага над макетом окна, язык русский, светлая тема; по кадру на шаг, 2,6 с, цикл.
/// Обычный запуск тестов пишет файлы во временную папку и проверяет: GIF читается (кодек Skia), кадров столько же, сколько шагов, размер до 1 МБ.
/// Выгрузка в репозиторий (docs/Отчеты/img-маркетплейс-гиф/&lt;код&gt;.gif) — только с переменной NURMARKET_GIF_EXPORT=1 (код карточки: «:» заменён на «-»).</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceGifExportTests
{
    private const double Width = 640, Height = 420;

    public MarketplaceGifExportTests() => TestHost.Ensure();

    private static IEnumerable<string> AllKeys() =>
        TariffBuilderFixture.LiveIds.Append(ModuleIds.CarcassYield).Append(ModuleIds.RepairStatusSite).Concat(MarketplaceFeatureCards.Ids)
            .Concat(["pack:ai", "pack:clients", "pack:salesanalytics", "pack:restock", "pack:salary", "pack:debts", "pack:service"]);

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
        return null;
    }

    [AvaloniaFact]
    public async Task Gif_encoder_writes_a_file_that_skia_reads_back_with_the_same_frames()
    {
        await TestHost.WarmAsync();
        using var a = new SKBitmap(64, 40);
        using var b = new SKBitmap(64, 40);
        using (var ca = new SKCanvas(a)) { ca.Clear(SKColors.White); using var p = new SKPaint { Color = new SKColor(200, 30, 30) }; ca.DrawRect(5, 5, 30, 20, p); }
        using (var cb = new SKCanvas(b)) { cb.Clear(new SKColor(20, 60, 160)); using var p = new SKPaint { Color = SKColors.Yellow }; cb.DrawRect(20, 10, 30, 20, p); }
        var bytes = GifWriter.Encode([a, b], 50);
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        codec.Should().NotBeNull("Skia прочитала GIF");
        codec.FrameCount.Should().Be(2);
        for (var i = 0; i < 2; i++)
        {
            using var back = new SKBitmap(codec.Info);
            codec.GetPixels(back.Info, back.GetPixels(), new SKCodecOptions(i)).Should().Be(SKCodecResult.Success);
            var src = i == 0 ? a : b;
            var pix = back.GetPixel(10, 10);
            var want = src.GetPixel(10, 10);
            Math.Abs(pix.Red - want.Red).Should().BeLessThan(10, $"кадр {i}: цвет точки совпал");
            Math.Abs(pix.Green - want.Green).Should().BeLessThan(10);
            Math.Abs(pix.Blue - want.Blue).Should().BeLessThan(10);
        }
    }

    [AvaloniaFact]
    public async Task Every_demo_is_exported_as_a_real_gif_under_one_megabyte()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var export = Environment.GetEnvironmentVariable("NURMARKET_GIF_EXPORT") == "1" && RepoRoot() is not null;
        var dir = export
            ? Path.Combine(RepoRoot()!, "docs", "Отчеты", "img-маркетплейс-гиф")
            : Path.Combine(Path.GetTempPath(), "nurmarket-tests", "marketplace-gif");
        Directory.CreateDirectory(dir);
        var sizes = new List<string>();
        foreach (var key in AllKeys())
        {
            var steps = MarketplaceDemos.StepsOf(key);
            steps.Should().NotBeEmpty(key);
            var color = MarketplaceModuleContent.Has(key) ? MarketplaceModuleContent.ColorOf(key) : "#2563EB";
            var player = new MarketplaceMockDemoPlayer(steps, color);
            var window = new Window { Width = Width, Height = Height, Content = player };
            window.Show();
            var frames = new List<SKBitmap>();
            try
            {
                UiShot.Settle(2);
                for (var i = 0; i < player.StepCount; i++)
                {
                    player.Show(i);
                    UiShot.Settle(2);
                    frames.Add(UiShot.Render(window, new UiSize((int)Width, (int)Height)));
                }
                var bytes = GifWriter.Encode(frames, 260);
                bytes.Length.Should().BeLessThan(1_000_000, $"{key}: GIF до 1 МБ");
                using (var codec = SKCodec.Create(new MemoryStream(bytes)))
                {
                    codec.Should().NotBeNull(key);
                    codec.FrameCount.Should().Be(steps.Count, $"{key}: по кадру на шаг демонстрации");
                }
                File.WriteAllBytes(Path.Combine(dir, key.Replace(':', '-') + ".gif"), bytes);
                sizes.Add($"{key}: {steps.Count} кадров, {bytes.Length / 1024} КБ");
            }
            finally
            {
                foreach (var f in frames)
                    f.Dispose();
                window.Close();
                UiShot.Pump();
            }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "nurmarket-tests", "marketplace-gif-sizes.txt"), sizes);
    }
}
