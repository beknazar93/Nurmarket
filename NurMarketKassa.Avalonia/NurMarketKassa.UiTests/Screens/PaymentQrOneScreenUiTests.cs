using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.Ui.Shared;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (жалоба тестера «не генерируется QR код для кассовой стороны (сделать QR побольше для тех, кто использует 1 экран)»):
/// окно оплаты в Avalonia.Headless у правого края главного окна 1024×600 и 1366×768, ru/ky, тёмная тема. Один экран — QR крупно (не меньше 240 точек),
/// виден целиком (не срезан прокруткой и краем окна), с суммой и банком; «Показать QR крупнее» — QR на всё окно, Esc закрывает только его;
/// картинка банка не читается — текст ошибки вместо пустого места; второй экран с QR — маленькая карточка, как раньше.
/// Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\payqr-*.png.</summary>
[Collection("UiScenarios")]
public sealed class PaymentQrOneScreenUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Save(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"payqr-{name}.png"), data.ToArray());
    }

    /// <summary>Картинка статического QR банка, как её загружает владелец (PNG).</summary>
    private static string WriteBankQr(string dir)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode("https://app.mbank.kg/qr/#00020101021132440012kg.elqr.test1015123456789012345530341763041234",
            ZXing.BarcodeFormat.QR_CODE, 360, 360);
        using var bmp = new SKBitmap(matrix.Width, matrix.Height);
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                bmp.SetPixel(x, y, matrix[x, y] ? SKColors.Black : SKColors.White);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir, "bank-qr.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private sealed class Display(bool customerSeesQr) : ICustomerDisplayService
    {
        public void Show() { }
        public void Hide() { }
        public void UpdateCart(CustomerDisplayCartSnapshot snapshot) { }
        public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) { }
        public void SetSelectedBankQrPath(string? qrPath) { }
        public bool ShowsPaymentQrToCustomer => customerSeesQr;
    }

    private static (Window Owner, CheckoutDialog Dialog, CheckoutViewModel Vm) Open(int width, int height, string qrPath, bool customerSeesQr)
    {
        UserPreferences.Instance.CheckoutCardPay = false;
        UserPreferences.Instance.DynamicPaymentQrEnabled = false;
        var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 1234.56, TotalDue = 1234.56, LineCount = 3 }, "", "", null,
            new Display(customerSeesQr));
        vm.IsPrintReceiptEnabled = false;
        var owner = new Window { Width = width, Height = height };
        owner.Show();
        UiShot.Settle(2);
        var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
        dialog.DockTo(owner);
        _ = dialog.ShowDialog<bool?>(owner);
        UiShot.Settle(3);
        // Банк — в списке Banks (как из настроек): иначе список банков окна (ComboBox, SelectedItem TwoWay) сбросит выбор в null.
        var bank = new BankAccount { BankName = "MBank", QrCodeImagePath = qrPath };
        vm.Banks.Insert(0, bank);
        vm.SelectedBank = bank;
        vm.IsTransfer = true;
        UiShot.Settle(6);
        return (owner, dialog, vm);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task OneScreen_LargeQr_IsVisibleWhole_AtLeast240_AndShowLargerOpensAndEscClosesOnlyIt(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, lang);
        var savedCard = UserPreferences.Instance.CheckoutCardPay;
        var savedDynamic = UserPreferences.Instance.DynamicPaymentQrEnabled;
        var dir = Directory.CreateTempSubdirectory("nm-payqr-");
        var tag = $"{width}x{height} {lang}";
        Window? owner = null;
        CheckoutDialog? dialog = null;
        try
        {
            var qrPath = WriteBankQr(dir.FullName);
            (owner, dialog, var vm) = Open(width, height, qrPath, customerSeesQr: false);

            vm.ShowQrCardLarge.Should().BeTrue(tag);
            var image = UiDrive.Find<Image>(dialog, "QrLargeImage");
            image.Source.Should().NotBeNull($"QR загружен ({tag})");
            image.IsEffectivelyVisible.Should().BeTrue($"QR виден ({tag})");
            UiDrive.Find<TextBlock>(dialog, "QrLargeError").IsEffectivelyVisible.Should().BeFalse(tag);

            // Размер на экране (после Viewbox) и «не срезан»: целиком внутри видимой части прокрутки и окна.
            var scroll = UiDrive.Find<ScrollViewer>(dialog, "ContentScroll");
            var rect = new Rect(image.Bounds.Size).TransformToAABB(image.TransformToVisual(scroll)!.Value);
            rect.Width.Should().BeGreaterThanOrEqualTo(240, $"QR не меньше 240 точек ({tag}), сейчас {rect.Width:0}");
            Math.Abs(rect.Width - rect.Height).Should().BeLessThan(1, "QR квадратный");
            rect.Left.Should().BeGreaterThanOrEqualTo(0, tag);
            rect.Top.Should().BeGreaterThanOrEqualTo(-0.5, $"верх QR не ушёл под прокрутку ({tag})");
            rect.Right.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 0.5, $"QR не срезан справа ({tag})");
            rect.Bottom.Should().BeLessThanOrEqualTo(scroll.Viewport.Height + 0.5, $"QR не срезан снизу ({tag}): {rect.Bottom:0} > {scroll.Viewport.Height:0}");
            UiDrive.AllText(dialog).Should().Contain(vm.QrAmountDisplay, "под QR — сумма");
            UiDrive.AllText(dialog).Should().Contain("MBank", "и банк");
            Save(dialog, $"large-{width}x{height}-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}");

            // «Показать QR крупнее» — на всё окно, крупнее встроенного; Esc закрывает только его.
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "ShowQrLargeButton"));
            UiShot.Settle(6);
            var overlay = UiDrive.Find<Border>(dialog, "QrOverlay");
            overlay.IsVisible.Should().BeTrue(tag);
            var big = UiDrive.Find<Image>(dialog, "QrOverlayImage");
            Math.Min(big.Bounds.Width, big.Bounds.Height).Should().BeGreaterThan(rect.Width, $"«крупнее» действительно крупнее ({tag})");
            Save(dialog, $"overlay-{width}x{height}-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}");

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            UiShot.Settle(4);
            overlay.IsVisible.Should().BeFalse($"Esc закрыл «QR крупнее» ({tag})");
            dialog.IsVisible.Should().BeTrue($"окно оплаты осталось открытым ({tag})");
        }
        finally
        {
            dialog?.Close();
            owner?.Close();
            UiShot.Pump();
            UserPreferences.Instance.CheckoutCardPay = savedCard;
            UserPreferences.Instance.DynamicPaymentQrEnabled = savedDynamic;
            dir.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task BrokenBankImage_ShowsErrorText_NotEmptyPlace()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var savedCard = UserPreferences.Instance.CheckoutCardPay;
        var savedDynamic = UserPreferences.Instance.DynamicPaymentQrEnabled;
        var dir = Directory.CreateTempSubdirectory("nm-payqr-");
        Window? owner = null;
        CheckoutDialog? dialog = null;
        try
        {
            var broken = Path.Combine(dir.FullName, "broken.png");
            File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            (owner, dialog, var vm) = Open(1366, 768, broken, customerSeesQr: false);

            vm.ShowQrCardLarge.Should().BeTrue();
            UiDrive.Find<TextBlock>(dialog, "QrLargeError").IsEffectivelyVisible.Should().BeTrue("картинка не читается — понятный текст");
            UiDrive.Find<Button>(dialog, "ShowQrLargeButton").IsEffectivelyVisible.Should().BeFalse("крупнее показывать нечего");
            UiDrive.AllText(dialog).Should().Contain("Настройки → Операции");
            Save(dialog, "broken-1366x768-ru");
        }
        finally
        {
            dialog?.Close();
            owner?.Close();
            UiShot.Pump();
            UserPreferences.Instance.CheckoutCardPay = savedCard;
            UserPreferences.Instance.DynamicPaymentQrEnabled = savedDynamic;
            dir.Delete(true);
        }
    }

    [AvaloniaFact]
    public async Task SecondScreenWithQr_KeepsSmallCard()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var savedCard = UserPreferences.Instance.CheckoutCardPay;
        var savedDynamic = UserPreferences.Instance.DynamicPaymentQrEnabled;
        var dir = Directory.CreateTempSubdirectory("nm-payqr-");
        Window? owner = null;
        CheckoutDialog? dialog = null;
        try
        {
            var qrPath = WriteBankQr(dir.FullName);
            (owner, dialog, var vm) = Open(1366, 768, qrPath, customerSeesQr: true);

            vm.ShowQrCardCompact.Should().BeTrue();
            UiDrive.Find<Image>(dialog, "QrCompactImage").IsEffectivelyVisible.Should().BeTrue();
            UiDrive.Find<Border>(dialog, "QrLargeCard").IsEffectivelyVisible.Should().BeFalse("при втором экране с QR — как раньше");
            Save(dialog, "dual-1366x768-ru");
        }
        finally
        {
            dialog?.Close();
            owner?.Close();
            UiShot.Pump();
            UserPreferences.Instance.CheckoutCardPay = savedCard;
            UserPreferences.Instance.DynamicPaymentQrEnabled = savedDynamic;
            dir.Delete(true);
        }
    }
}
