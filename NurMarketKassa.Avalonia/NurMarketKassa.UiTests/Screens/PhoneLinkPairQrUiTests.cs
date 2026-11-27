using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PhoneLink;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27, жалоба владельца «не работает подключение сканера с телефона» (фото: тёмная тема, QR на светлой плашке):
/// настоящее окно «Подключить телефон» в тёмной теме, кадр окна → декодер QR (ZXing). Проверяется, что QR читается с кадра как есть
/// и после уменьшения до ~3 пикселей на модуль с размытием (так его видит камера телефона с 25–30 см), в QR нет имени кассы
/// (короткий код), модуль — целое число пикселей, белое поле вокруг кода не меньше 4 модулей. До исправления ReceiptQrView тест падал
/// уже на чтении с кадра: в тёмной теме QR был инверсным (светлые модули на тёмном), такой код ZXing и ML Kit не читают.</summary>
[Collection("UiScenarios")]
public sealed class PhoneLinkPairQrUiTests : IDisposable
{
    private readonly object _owner = new();
    private readonly bool _wasEnabled;

    public PhoneLinkPairQrUiTests()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        _wasEnabled = PhoneLinkHost.Pairing.Enabled;
        PhoneLinkHost.Pairing.SetEnabled(true);
        PhoneLinkHost.Attach(_owner, _ => Task.FromResult(true), (_, _) => Task.FromResult(PhoneScanHandler.EnqueueResult.TillInactive));
    }

    public void Dispose()
    {
        PhoneLinkHost.Detach(_owner);
        PhoneLinkHost.Pairing.SetEnabled(_wasEnabled);
    }

    [AvaloniaFact]
    public void DarkTheme_QrInWindow_DecodesFromScreen_AndFromBlurredCameraFrame()
    {
        if (!PhoneLinkHost.IsListening || PhoneLinkHost.LocalIPv4().Count == 0)
            return; // нет сети или порта на машине теста — окно QR не покажет (это отдельные сообщения окна)

        var type = typeof(PhoneLinkHost).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.PhoneLinkPairWindow", throwOnError: true)!;
        var window = (Window)Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            using var frame = UiShot.Render(window);
            SaveForReview(frame);
            var (gray, w, h) = Gray(frame);
            var text = Decode(gray, w, h);
            text.Should().NotBeNull("QR окна должен читаться с кадра");
            text!.Should().StartWith("nurmarket-link:v1?d=").And.NotContain("&n=");
            text.Length.Should().BeLessThanOrEqualTo(120);

            var qr = type.GetField("_qr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            var modules = (int)qr.GetType().GetProperty("Modules")!.GetValue(qr)!;
            var size = ((Control)qr).Width;
            modules.Should().BeInRange(21, 41);
            (size % modules).Should().Be(0, "модуль — целое число пикселей");
            var frameBorder = (Border)type.GetField("_qrFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            frameBorder.Padding.Left.Should().BeGreaterThanOrEqualTo(4 * size / modules, "белое поле — 4 модуля");

            // Камера: модуль ~3 пикселя, размытие 3×3, низкий контраст (блики экрана).
            var scale = 3.0 / (size / modules);
            var (small, sw, sh) = Shrink(gray, w, h, scale, lo: 45, hi: 205);
            Decode(small, sw, sh).Should().Be(text, "камера телефона с 25–30 см");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Кадр окна — в папку отчётов снимков (%TEMP%\nurmarket-tests\ui-actual), чтобы при падении посмотреть глазами.</summary>
    private static void SaveForReview(SKBitmap bmp)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, "phonelink-pair-dark.png"), data.ToArray());
        }
        catch
        {
            // снимок для просмотра не обязателен
        }
    }

    private static (byte[] Gray, int W, int H) Gray(SKBitmap bmp)
    {
        var g = new byte[bmp.Width * bmp.Height];
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            g[y * bmp.Width + x] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
        }
        return (g, bmp.Width, bmp.Height);
    }

    /// <summary>Уменьшение усреднением площади (как матрица камеры), затем размытие 3×3 и сжатие контраста.</summary>
    private static (byte[] Gray, int W, int H) Shrink(byte[] src, int w, int h, double scale, byte lo, byte hi)
    {
        var nw = (int)(w * scale);
        var nh = (int)(h * scale);
        var tmp = new double[nw * nh];
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        {
            int x0 = (int)(x / scale), x1 = Math.Max(x0 + 1, (int)((x + 1) / scale));
            int y0 = (int)(y / scale), y1 = Math.Max(y0 + 1, (int)((y + 1) / scale));
            double sum = 0;
            var n = 0;
            for (var yy = y0; yy < Math.Min(y1, h); yy++)
            for (var xx = x0; xx < Math.Min(x1, w); xx++)
            {
                sum += src[yy * w + xx];
                n++;
            }
            tmp[y * nw + x] = n == 0 ? 0 : sum / n / 255.0;
        }
        var outp = new byte[nw * nh];
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        {
            double sum = 0;
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var weight = dx == 0 && dy == 0 ? 2 : 1;
                sum += tmp[Math.Clamp(y + dy, 0, nh - 1) * nw + Math.Clamp(x + dx, 0, nw - 1)] * weight;
                n += weight;
            }
            outp[y * nw + x] = (byte)(lo + sum / n * (hi - lo));
        }
        return (outp, nw, nh);
    }

    private static string? Decode(byte[] gray, int w, int h)
    {
        var source = new ZXing.RGBLuminanceSource(gray, w, h, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        var hints = new Dictionary<ZXing.DecodeHintType, object> { [ZXing.DecodeHintType.TRY_HARDER] = true };
        foreach (var binarizer in new Func<ZXing.LuminanceSource, ZXing.Binarizer>[] { s => new ZXing.Common.HybridBinarizer(s), s => new ZXing.Common.GlobalHistogramBinarizer(s) })
        {
            try
            {
                var r = new ZXing.QrCode.QRCodeReader().decode(new ZXing.BinaryBitmap(binarizer(source)), hints);
                if (r is not null)
                    return r.Text;
            }
            catch (ZXing.ReaderException)
            {
                // следующий бинаризатор
            }
        }
        return null;
    }
}
