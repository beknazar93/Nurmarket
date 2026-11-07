using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.UiTests.Harness;

namespace NurMarketKassa.UiTests.Screens;

public sealed class ConfirmDialogTests
{
    [AvaloniaFact]
    public void Smoke_ConfirmDialog_RendersOneFrame()
    {
        UiShot.Setup(UiTheme.Light, NurMarketKassa.Services.AppLanguage.Russian);
        var dlg = new PosConfirmDialog("Закрыть смену", "Закрыть смену и потерять чек?", "Закрыть смену", null, PosConfirmAccent.Danger);
        using var bmp = UiShot.Render(dlg);
        bmp.Width.Should().BeGreaterThan(100);
        System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "nurmarket-tests"));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "nurmarket-tests", "smoke-confirm.png"), img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());
        dlg.Close();
    }
}
