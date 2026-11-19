using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (список владельца, пп. 1, 2): рамка обрезки QR двигается и тянется на самой картинке (углы, стороны, середина) и синхронизируется с ползунками;
/// карточки ИИ уезжают с «Операций» на вкладку «ИИ». Снимок окна обрезки: %TEMP%\nurmarket-tests\ui-actual\qr-crop-{тема}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class OwnerComfortBatchUiTests
{
    private static string MakeQrPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-ui-qr-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        using var bmp = new SKBitmap(240, 240);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var i = 0; i < 12; i++)
                for (var j = 0; j < 12; j++)
                    if ((i * 7 + j * 3) % 3 == 0)
                        canvas.DrawRect(30 + i * 15, 30 + j * 15, 15, 15, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaTheory]
    [InlineData(UiTheme.Light, 1366, 768)]
    [InlineData(UiTheme.Dark, 1024, 600)]
    public async Task QrCrop_FrameDragsOnPicture_AndSyncsWithSliders(UiTheme theme, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        var path = MakeQrPng();
        var dialog = new QrCropDialog(path, "Тест");
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(w, h));
            var surface = UiDrive.Find<QrCropSurface>(dialog, "CropSurface");
            var left = UiDrive.Find<Slider>(dialog, "LeftSlider");
            var top = UiDrive.Find<Slider>(dialog, "TopSlider");
            var right = UiDrive.Find<Slider>(dialog, "RightSlider");

            surface.DragForTest("TopLeft", 12, 8); // угол: две стороны сразу
            left.Value.Should().BeApproximately(12, 0.2);
            top.Value.Should().BeApproximately(8, 0.2);

            surface.DragForTest("Right", -10, 0); // сторона
            right.Value.Should().BeApproximately(10, 0.2);

            surface.DragForTest("Move", 5, 5); // середина: сдвиг без изменения размера
            left.Value.Should().BeApproximately(17, 0.2);
            right.Value.Should().BeApproximately(5, 0.2);

            surface.DragForTest("Left", 200, 0); // не больше предела ползунка (45 %)
            left.Value.Should().BeLessThanOrEqualTo(45);

            left.Value = 3; // ползунок — запасной путь: рамка следует за ним
            UiShot.Settle(3);
            surface.LeftPercent.Should().BeApproximately(3, 0.2);

            using var shot = UiShot.Render(dialog);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var outDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"qr-crop-{theme.ToString().ToLowerInvariant()}-{w}x{h}.png"), data.ToArray());
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task AiCards_MoveFromOperationsToAiTab()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var ops = new OperationsSettingsView();
        var ai = new AiSettingsView(ops);
        var window = new Window { Content = ai, Width = 1100, Height = 700 };
        try
        {
            window.Show();
            UiShot.Settle(4);
            var texts = UiDrive.AllText(window);
            texts.Should().Contain("Искусственный интеллект");
            ai.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("SettingsCard")).Should().BeGreaterThanOrEqualTo(2, "ключи ИИ и Нур Советник");
            ops.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().NotContain("Ключи ИИ", "карточка ИИ снята с «Операций»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ScalePanel_HasExplicitSetupAndMasterButtons_AndThreeStepHint()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var panel = new LabelScaleSetupPanel();
        var window = new Window { Content = new ScrollViewer { Content = panel }, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            UiShot.Settle(4);
            UiDrive.Find<Button>(window, "DeviceSettingsButton").Content.Should().Be("Настроить весы…");
            UiDrive.Find<Button>(window, "MasterSettingsButton").Content.Should().Be("Для мастера…");
            UiDrive.AllText(window).Should().Contain("Быстрая настройка в 3 шага");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
