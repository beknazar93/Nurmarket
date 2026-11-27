using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PhoneLink;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-27, жалоба владельца «не работает подключение сканера с телефона» (касса 1.17.96 + мобилка 1.18.24, Redmi 12):
/// QR кассы был слишком плотным для камеры телефона (имя кассы кириллицей в URL-кодировке + 32 символа id → 45–57 модулей,
/// а мобилка по умолчанию анализирует кадр 640×480); брандмауэр Windows молча отбрасывал телефон в «общедоступной» сети;
/// касса не показывала, что телефон до неё достучался. Тесты: короткий QR (и что он читается декодером после уменьшения и
/// размытия — имитация камеры), решение по правилам брандмауэра, событие «телефон стучался».</summary>
public sealed class PhoneLinkConnectTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"; // 32 байта 0..31

    private static string Qr(string ip = "10.0.0.59") =>
        PhoneLinkProtocol.BuildQr("0123456789abcdef0123456789abcdef", ip, 47830, Key, "4b75a1c2");

    [Fact]
    public void Qr_IsShort_NoKassaName_DeviceIdTruncated_StillParsableByOldPhones()
    {
        var qr = Qr("192.168.100.200"); // самый длинный IPv4
        qr.Should().StartWith("nurmarket-link:v1?");
        qr.Should().NotContain("&n=");
        qr.Length.Should().BeLessThanOrEqualTo(120);
        // Мобилка 1.18.x (KassaPairing.parseQr) требует d (1–64), h (IPv4), p, k (32 байта), n и c — необязательны.
        var query = qr[(qr.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        query["d"].Should().Be("0123456789ab");
        query["h"].Should().Be("192.168.100.200");
        query["p"].Should().Be("47830");
        PhoneLinkProtocol.TryBase64UrlDecode(query["k"], out var key).Should().BeTrue();
        key.Should().HaveCount(32);
        query["c"].Should().Be("4b75a1c2");
    }

    [Fact]
    public void Qr_HasFewModules_AndDecodesFromBlurredLowResCameraFrame()
    {
        var matrix = Encode(Qr("192.168.100.200"));
        matrix.Width.Should().BeLessThanOrEqualTo(41, "камера телефона с экрана ноутбука не берёт плотный код (было 45–57 модулей)");

        // Имитация камеры: ~2,6 пикселя кадра на модуль, белое поле 4 модуля, размытие, низкий контраст экрана (блики).
        foreach (var pxPerModule in new[] { 2.6, 3.0, 4.0 })
        {
            var (gray, side) = CameraFrame(matrix, pxPerModule, quietModules: 4, lo: 50, hi: 205);
            Decode(gray, side).Should().Be(Qr("192.168.100.200"), $"{pxPerModule} px на модуль");
        }
    }

    [Fact]
    public void Firewall_DefaultWindowsPrompt_AllowsPrivateBlocksPublic()
    {
        const string exe = @"C:\Users\U\AppData\Local\NurMarketKassa\current\NurMarketKassa.Avalonia.exe";
        // Так Windows сохраняет «Разрешить доступ» с галочкой только «Частные сети»: allow для профиля 2 и block для профиля 4.
        var rules = new[]
        {
            new PhoneLinkFirewall.Rule(exe.ToLowerInvariant(), true, true, true, PhoneLinkFirewall.ProfilePrivate, 6, "*"),
            new PhoneLinkFirewall.Rule(exe.ToLowerInvariant(), true, true, false, PhoneLinkFirewall.ProfilePublic, 6, "*"),
            new PhoneLinkFirewall.Rule(@"C:\other\app.exe", true, true, true, 7, 256, null),
        };
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePrivate, true, rules, exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.Allowed);
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePublic, true, rules, exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.Blocked);
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePublic, false, rules, exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.Allowed);
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePrivate, true, Array.Empty<PhoneLinkFirewall.Rule>(), exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.NoRule);
        // Выключенное правило и исходящее — не в счёт; правило «по порту» без программы — в счёт, если порт назван явно.
        var portRule = new[]
        {
            new PhoneLinkFirewall.Rule(exe, true, false, true, 7, 6, null),
            new PhoneLinkFirewall.Rule(exe, false, true, true, 7, 6, null),
            new PhoneLinkFirewall.Rule(null, true, true, true, 7, 6, "47830-47839"),
        };
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePublic, true, portRule, exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.Allowed);
        PhoneLinkFirewall.Evaluate(PhoneLinkFirewall.ProfilePublic, true, portRule.Take(2), exe, 47830).Should().Be(PhoneLinkFirewall.Verdict.NoRule);
    }

    [Fact]
    public async Task Server_RaisesKnocked_ForWrongCode_AndForSuccessfulPairing()
    {
        var dir = Path.Combine(AppPaths.Local, "test-phonelink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pairing = new PhoneLinkPairing(dir, () => "company-1");
            using var server = new PhoneLinkServer(pairing, () => new PhoneLinkHostInfo("Касса 1", "12345678", true), firstPort: 47870, lastPort: 47889);
            server.Start(out var error).Should().BeTrue(error);
            var knocks = new List<(string Address, string Op, string Code)>();
            server.Knocked += (a, o, c) => { lock (knocks) knocks.Add((a, o, c)); };

            var key = pairing.BeginPairing();
            PhoneLinkProtocol.TryBase64UrlDecode(key, out var keyBytes).Should().BeTrue();
            var wrong = new byte[32];

            var bad = await SendAsync(server.Port, Hello(wrong));
            bad.GetProperty("ok").GetBoolean().Should().BeFalse();
            var good = await SendAsync(server.Port, Hello(keyBytes));
            good.GetProperty("ok").GetBoolean().Should().BeTrue();

            lock (knocks)
            {
                knocks.Should().HaveCount(2);
                knocks[0].Should().Be(("127.0.0.1", "hello", PhoneLinkCodes.BadSig));
                knocks[1].Should().Be(("127.0.0.1", "hello", PhoneLinkCodes.Ok));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* не мешает */ }
        }
    }

    // ------------------------------------------------------------------ помощники

    private static string Hello(byte[] key)
    {
        const string phone = "phone-test-0001";
        const string body = "{\"name\":\"Тест\"}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = PhoneLinkProtocol.Base64UrlEncode(Guid.NewGuid().ToByteArray());
        var sig = PhoneLinkProtocol.Sign(key, "hello", phone, ts, nonce, body);
        return JsonSerializer.Serialize(new { v = 1, op = "hello", phone, ts, nonce, body, sig });
    }

    private static async Task<JsonElement> SendAsync(int port, string line)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return JsonDocument.Parse(reply!).RootElement.Clone();
    }

    /// <summary>Тот же кодировщик и те же настройки, что у окна «Подключить телефон» (ReceiptQrView: UTF-8, без поля, уровень L).</summary>
    private static ZXing.Common.BitMatrix Encode(string text) =>
        new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, new Dictionary<ZXing.EncodeHintType, object>
        {
            [ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8",
            [ZXing.EncodeHintType.MARGIN] = 0,
            [ZXing.EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
        });

    /// <summary>Кадр камеры: QR с белым полем в тёмной рамке (тёмная тема), дробный масштаб с усреднением, размытие 3×3, контраст lo..hi.</summary>
    private static (byte[] Gray, int Side) CameraFrame(ZXing.Common.BitMatrix m, double pxPerModule, int quietModules, byte lo, byte hi)
    {
        var modules = m.Width + 2 * quietModules;
        var border = 12;
        var qrPx = (int)Math.Ceiling(modules * pxPerModule);
        var side = qrPx + 2 * border;
        var img = new double[side * side];
        const int ss = 4; // подвыборка для дробного масштаба
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            double sum = 0;
            for (var sy = 0; sy < ss; sy++)
            for (var sx = 0; sx < ss; sx++)
            {
                var fx = (x - border + (sx + 0.5) / ss) / pxPerModule - quietModules;
                var fy = (y - border + (sy + 0.5) / ss) / pxPerModule - quietModules;
                double v;
                if (x < border || y < border || x >= border + qrPx || y >= border + qrPx)
                    v = 0.1; // тёмный фон окна вокруг белой плашки
                else if (fx < 0 || fy < 0 || fx >= m.Width || fy >= m.Height)
                    v = 1; // белое поле
                else
                    v = m[(int)fx, (int)fy] ? 0 : 1;
                sum += v;
            }
            img[y * side + x] = sum / (ss * ss);
        }
        var gray = new byte[side * side];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            double sum = 0;
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var yy = Math.Clamp(y + dy, 0, side - 1);
                var xx = Math.Clamp(x + dx, 0, side - 1);
                sum += img[yy * side + xx] * (dx == 0 && dy == 0 ? 2 : 1);
                n += dx == 0 && dy == 0 ? 2 : 1;
            }
            gray[y * side + x] = (byte)(lo + sum / n * (hi - lo));
        }
        return (gray, side);
    }

    private static string? Decode(byte[] gray, int side)
    {
        var source = new ZXing.RGBLuminanceSource(gray, side, side, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        var bitmap = new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(source));
        var hints = new Dictionary<ZXing.DecodeHintType, object> { [ZXing.DecodeHintType.CHARACTER_SET] = "UTF-8" };
        return new ZXing.QrCode.QRCodeReader().decode(bitmap, hints)?.Text;
    }
}
