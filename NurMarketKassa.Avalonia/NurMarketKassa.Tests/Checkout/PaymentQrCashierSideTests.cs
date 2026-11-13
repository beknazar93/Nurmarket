using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;
using ZXing;
using ZXing.Common;

namespace NurMarketKassa.Tests.Checkout;

/// <summary>2026-11-12 (жалоба тестера «не генерируется QR код для кассовой стороны (сделать QR побольше для тех, кто использует 1 экран)»):
/// QR с суммой раньше строился только для экрана покупателя, окно оплаты кассы показывало исходную картинку банка 84×84.
/// Здесь: (1) сам построитель QR — строки разной длины и символов читаются обратно тем же ZXing, тихая зона ≥ 4 модулей, коррекция M;
/// (2) окно оплаты (модель) берёт QR с суммой, при одном экране — крупная карточка, при втором экране с QR — маленькая, при осечке — понятный флаг ошибки.
/// Общая статика (UserPreferences.Instance) — коллекция «ServerFeatures».</summary>
[Collection("ServerFeatures")]
public sealed class PaymentQrCashierSideTests
{
    private static readonly string[] Payloads =
    {
        "QR",
        "https://app.mbank.kg/qr/#00020101021132440012kg.elqr.test10151234567890123455303417",
        "Оплата магазину «Нур» — 1 234,56 сом", // не-ASCII: UTF-8
        new string('7', 120) + "|" + string.Concat(Enumerable.Range(0, 60).Select(i => (char)('A' + i % 26))) + "%20&=?#/" + new string('x', 400),
    };

    private static Result? Decode(BitMatrix m)
    {
        var bytes = new byte[m.Width * m.Height];
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < m.Width; x++)
                bytes[y * m.Width + x] = m[x, y] ? (byte)0 : (byte)255;
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } },
        };
        return reader.Decode(new RGBLuminanceSource(bytes, m.Width, m.Height, RGBLuminanceSource.BitmapFormat.Gray8));
    }

    [Fact]
    public void QrWriter_RoundTrips_AnyPayload_WithQuietZone4_AndErrorCorrectionM()
    {
        foreach (var payload in Payloads)
        {
            var m = QrPngWriter.Encode(payload, 640);
            m.Width.Should().BeGreaterThanOrEqualTo(640, payload);

            var result = Decode(m);
            result.Should().NotBeNull($"QR «{payload[..Math.Min(20, payload.Length)]}…» читается обратно");
            result!.Text.Should().Be(payload);
            result.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL].ToString().Should().Be("M", "коррекция ошибок не ниже M");

            // Тихая зона: первая чёрная точка — угол поискового узора, чёрная полоса в этой строке — 7 модулей.
            int top = -1, left = -1, right = -1;
            for (var y = 0; y < m.Height && top < 0; y++)
                for (var x = 0; x < m.Width; x++)
                    if (m[x, y]) { top = y; left = x; break; }
            for (var x = m.Width - 1; x >= 0; x--)
                if (m[x, top]) { right = x; break; }
            var run = 0;
            while (m[left + run, top]) run++;
            var module = run / 7.0;
            module.Should().BeGreaterThanOrEqualTo(2, "модуль крупный, не размазан");
            left.Should().BeGreaterThanOrEqualTo((int)Math.Floor(4 * module), "слева ≥ 4 модулей белого");
            top.Should().BeGreaterThanOrEqualTo((int)Math.Floor(4 * module), "сверху ≥ 4 модулей белого");
            (m.Width - 1 - right).Should().BeGreaterThanOrEqualTo((int)Math.Floor(4 * module), "справа ≥ 4 модулей белого");
        }
    }

    [Fact]
    public void QrWriter_Png_DecodesBack_FromFile()
    {
        if (!OperatingSystem.IsWindows())
            return; // чтение файла картинки в тестах — через System.Drawing (Windows)
        var dir = Directory.CreateTempSubdirectory("nm-qr-");
        try
        {
            foreach (var payload in Payloads)
            {
                var png = Path.Combine(dir.FullName, Guid.NewGuid().ToString("N") + ".png");
                QrPngWriter.Write(payload, 420, png);
                DynamicPaymentQrService.TryDecodeStaticQr(png).Should().Be(payload);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>Статический QR ELQR, как у MBank: реквизиты без суммы (тег 63 — настоящая контрольная сумма).</summary>
    private static string StaticElqrLink() =>
        "https://app.mbank.kg/qr/#" + ElqrQrBuilder.FinalizePayload(new ElqrQrBuilder.TlvField[]
        {
            new("00", "01"), new("01", "11"), new("32", "0012kg.elqr.test1015123456789012345"),
            new("52", "5999"), new("53", "417"), new("58", "KG"), new("59", "TestShop"), new("63", "0000"),
        });

    [Fact]
    public void DynamicQr_ForCashier_HasAmountWithKopecks_AndDoesNotDeleteCustomerDisplayFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Directory.CreateTempSubdirectory("nm-qr-");
        try
        {
            var staticPng = Path.Combine(dir.FullName, "bank.png");
            QrPngWriter.Write(StaticElqrLink(), 420, staticPng);

            var forCustomer = DynamicPaymentQrService.TryBuildDynamicQrImage(staticPng, 123.45m); // экран покупателя (420)
            var forCashier = DynamicPaymentQrService.TryBuildDynamicQrImage(staticPng, 123.45m, 640); // окно оплаты кассы

            forCustomer.Should().NotBeNull();
            forCashier.Should().NotBeNull();
            File.Exists(forCustomer!).Should().BeTrue("второй вызов (касса) не удаляет файл экрана покупателя");
            DynamicPaymentQrService.TryDecodeStaticQr(forCashier!).Should().Contain("540512345", "сумма 123,45 → 12345 тыйын в теге 54");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private sealed class Display(bool customerSeesQr) : ICustomerDisplayService
    {
        public void Show() { }
        public void Hide() { }
        public void UpdateCart(CustomerDisplayCartSnapshot snapshot) { }
        public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) { }
        public void SetSelectedBankQrPath(string? qrPath) => LastBankQr = qrPath;
        public bool ShowsPaymentQrToCustomer => customerSeesQr;
        public string? LastBankQr { get; private set; } = "не задано";
    }

    private static CheckoutViewModel Create(double total, ICustomerDisplayService? display) =>
        new(new CartTotalsCalculator.CartTotals { Subtotal = total, TotalDue = total, LineCount = 1 }, "", "", null, display);

    private static (bool CardPay, bool Dynamic) SavePrefs() =>
        (UserPreferences.Instance.CheckoutCardPay, UserPreferences.Instance.DynamicPaymentQrEnabled);

    private static void RestorePrefs((bool CardPay, bool Dynamic) saved)
    {
        UserPreferences.Instance.CheckoutCardPay = saved.CardPay;
        UserPreferences.Instance.DynamicPaymentQrEnabled = saved.Dynamic;
    }

    [Fact]
    public void OneScreen_ShowsLargeQrCard_SecondScreenWithQr_KeepsSmallCard()
    {
        var saved = SavePrefs();
        var qrPath = Path.GetTempFileName();
        try
        {
            UserPreferences.Instance.CheckoutCardPay = false;
            UserPreferences.Instance.DynamicPaymentQrEnabled = false;

            var single = Create(500, new Display(customerSeesQr: false));
            single.SelectedBank = new BankAccount { BankName = "MBank", QrCodeImagePath = qrPath };
            single.IsTransfer = true;
            single.IsLargeQr.Should().BeTrue("второго экрана с QR нет");
            single.ShowQrCardLarge.Should().BeTrue();
            single.ShowQrCardCompact.Should().BeFalse();
            single.CashierQrImagePath.Should().Be(qrPath, "«QR с суммой» выключен — картинка банка, как раньше");
            single.QrPayMethodText.Should().Contain("MBank");
            single.HasDynamicQrError.Should().BeFalse();

            Create(500, null).IsLargeQr.Should().BeTrue("без экрана покупателя вовсе — тоже крупно");

            var dual = Create(500, new Display(customerSeesQr: true));
            dual.SelectedBank = new BankAccount { BankName = "MBank", QrCodeImagePath = qrPath };
            dual.IsTransfer = true;
            dual.ShowQrCardCompact.Should().BeTrue("QR видит покупатель на своём экране — у кассира маленькая карточка, как раньше");
            dual.ShowQrCardLarge.Should().BeFalse();

            single.IsCash = true;
            single.ShowQrCardLarge.Should().BeFalse("наличными QR не показывается");
        }
        finally
        {
            RestorePrefs(saved);
            File.Delete(qrPath);
        }
    }

    [Fact]
    public void DynamicQrEnabled_CashierGetsQrWithAmount_MixedUsesNonCashPart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var saved = SavePrefs();
        var dir = Directory.CreateTempSubdirectory("nm-qr-");
        try
        {
            UserPreferences.Instance.CheckoutCardPay = false;
            UserPreferences.Instance.DynamicPaymentQrEnabled = true;
            var staticPng = Path.Combine(dir.FullName, "bank.png");
            QrPngWriter.Write(StaticElqrLink(), 420, staticPng);

            var vm = Create(250.5, new Display(false));
            vm.SelectedBank = new BankAccount { BankName = "MBank", QrCodeImagePath = staticPng };
            vm.IsTransfer = true;

            vm.CashierQrImagePath.Should().NotBe(staticPng, "в окне оплаты — QR с суммой, а не картинка банка");
            DynamicPaymentQrService.TryDecodeStaticQr(vm.CashierQrImagePath!).Should().Contain("540525050", "250,50 сом");
            vm.HasDynamicQrError.Should().BeFalse();

            vm.IsMixed = true;
            vm.MixedCashAmount = "100";
            vm.MixedNonCashAmount = "150,5";
            DynamicPaymentQrService.TryDecodeStaticQr(vm.CashierQrImagePath!).Should().Contain("540515050", "в смешанной — безналичная часть 150,50");
        }
        finally
        {
            RestorePrefs(saved);
            dir.Delete(true);
        }
    }

    [Fact]
    public void DynamicQrEnabled_ButBankImageIsNotElqr_FallsBackToBankImage_WithErrorFlag()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var saved = SavePrefs();
        var dir = Directory.CreateTempSubdirectory("nm-qr-");
        try
        {
            UserPreferences.Instance.CheckoutCardPay = false;
            UserPreferences.Instance.DynamicPaymentQrEnabled = true;
            var staticPng = Path.Combine(dir.FullName, "not-elqr.png");
            QrPngWriter.Write("https://example.org/pay", 420, staticPng);

            var vm = Create(300, new Display(false));
            vm.SelectedBank = new BankAccount { BankName = "Bank", QrCodeImagePath = staticPng };
            vm.IsTransfer = true;

            vm.CashierQrImagePath.Should().Be(staticPng, "осечка — обычный QR банка, а не пустое место");
            vm.HasDynamicQrError.Should().BeTrue();
            vm.DynamicQrErrorText.Should().Contain("QR");
        }
        finally
        {
            RestorePrefs(saved);
            dir.Delete(true);
        }
    }

    [Fact]
    public void ResetCustomerDisplayQr_ClearsBankAndAmountOnCustomerDisplay()
    {
        var display = new Display(false);
        var vm = Create(100, display);

        vm.ResetCustomerDisplayQr();

        display.LastBankQr.Should().BeNull("окно оплаты закрыто — экран покупателя забывает банк и сумму QR");
    }
}
