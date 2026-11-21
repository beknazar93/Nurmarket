using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.Ui.Shared;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (жалоба владельца «при оплате безнал окно оплаты резко меняет размер, неудобно на маленьких экранах»):
/// окно «Оплата покупки» не меняет ширину и высоту при переключении F1 Наличные → F2 Безналичные (Карта / QR с банком) → F3 В долг → F4 Смешанная,
/// а кнопка «Оплатить» и нижняя панель всегда внутри окна, и окно целиком — внутри главного окна (рабочей области).
/// 1024×600, 1024×768, 1366×768, 1920×1080; ru/ky/uz; темы Light/Dark/Touch.</summary>
[Collection("UiScenarios")]
public sealed class PaymentWindowStableSizeUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new (int W, int H, AppLanguage L, UiTheme T)[]
        {
            (1024, 600, AppLanguage.Russian, UiTheme.Dark),
            (1024, 600, AppLanguage.Kyrgyz, UiTheme.Touch),
            (1024, 600, AppLanguage.Uzbek, UiTheme.Light),
            (1024, 768, AppLanguage.Russian, UiTheme.Light),
            (1024, 1024, AppLanguage.Russian, UiTheme.Light), // 2026-11-21: квадратный экран (просьба владельца)
            (1366, 768, AppLanguage.Russian, UiTheme.Dark),
            (1366, 768, AppLanguage.Kyrgyz, UiTheme.Touch),
            (1366, 768, AppLanguage.Uzbek, UiTheme.Dark),
            (1920, 1080, AppLanguage.Russian, UiTheme.Light),
        }.Select(c => new object[] { c.W, c.H, c.L, c.T });

    private sealed class Display(bool customerSeesQr) : ICustomerDisplayService
    {
        public void Show() { }
        public void Hide() { }
        public void UpdateCart(CustomerDisplayCartSnapshot snapshot) { }
        public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) { }
        public void SetSelectedBankQrPath(string? qrPath) { }
        public bool ShowsPaymentQrToCustomer => customerSeesQr;
    }

    private static string WriteBankQr(string dir)
    {
        var matrix = new ZXing.QrCode.QRCodeWriter().encode("https://app.mbank.kg/qr/#00020101021132440012kg.elqr.test1015123456789012345530341763041234",
            ZXing.BarcodeFormat.QR_CODE, 360, 360);
        using var bmp = new SkiaSharp.SKBitmap(matrix.Width, matrix.Height);
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                bmp.SetPixel(x, y, matrix[x, y] ? SkiaSharp.SKColors.Black : SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir, "bank-qr.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task PaymentWindow_KeepsSize_AcrossMethods_AndPayButtonStaysVisible(int width, int height, AppLanguage lang, UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var savedCard = UserPreferences.Instance.CheckoutCardPay;
        var savedDynamic = UserPreferences.Instance.DynamicPaymentQrEnabled;
        var dir = Directory.CreateTempSubdirectory("nm-paysize-");
        var tag = $"{width}x{height} {lang} {theme}";
        Window? owner = null;
        CheckoutDialog? dialog = null;
        try
        {
            UserPreferences.Instance.CheckoutCardPay = false;
            UserPreferences.Instance.DynamicPaymentQrEnabled = false;
            var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 1234.56, TotalDue = 1234.56, LineCount = 3 }, "", "", null,
                new Display(customerSeesQr: false));
            vm.IsPrintReceiptEnabled = false;
            owner = new Window { Width = width, Height = height };
            owner.Show();
            UiShot.Settle(2);
            dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
            dialog.DockTo(owner);
            _ = dialog.ShowDialog<bool?>(owner);
            UiShot.Settle(3);
            var bank = new BankAccount { BankName = "MBank", QrCodeImagePath = WriteBankQr(dir.FullName) };
            vm.Banks.Insert(0, bank);
            vm.SelectedBank = bank;
            UiShot.Settle(4);

            var pay = UiDrive.Find<Button>(dialog, "PayButton");
            var steps = new (string Name, Action Apply)[]
            {
                ("F1 Наличные", () => vm.IsCash = true),
                ("F2 Безналичные Карта", () => { vm.IsTransfer = true; vm.IsCardPay = true; }),
                ("F2 Безналичные QR", () => { vm.IsTransfer = true; vm.IsCardPay = false; }),
                ("F3 В долг", () => vm.IsDebt = true),
                ("F4 Смешанная", () => vm.IsMixed = true),
                ("F2 QR снова", () => { vm.IsTransfer = true; vm.IsCardPay = false; }),
                ("F1 Наличные снова", () => vm.IsCash = true),
            };
            // Сначала собираем размеры по всем способам, потом сверяем: при сбое в сообщении видны все высоты сразу.
            var rows = new List<(string Name, Rect Size, int Y, Rect PayRect, bool PayVisible, double BottomPx)>();
            foreach (var (name, apply) in steps)
            {
                apply();
                UiShot.Settle(8);
                var scale = dialog.RenderScaling > 0 ? dialog.RenderScaling : 1.0;
                rows.Add((name, new Rect(dialog.Bounds.Size), dialog.Position.Y,
                    new Rect(pay.Bounds.Size).TransformToAABB(pay.TransformToVisual(dialog)!.Value), pay.IsEffectivelyVisible,
                    dialog.Position.Y + dialog.Bounds.Height * scale));
            }
            var ownerBottomPx = owner.Position.Y + owner.ClientSize.Height * (owner.RenderScaling > 0 ? owner.RenderScaling : 1.0);
            var log = $"{tag}: " + string.Join("; ", rows.Select(r => $"{r.Name} {r.Size.Width:0}x{r.Size.Height:0} y{r.Y}")) + $" (min {dialog.MinHeight:0}, max {dialog.MaxHeight:0}, {dialog.SizeToContent})";
            var first = rows[0];
            foreach (var r in rows)
            {
                Math.Abs(r.Size.Width - first.Size.Width).Should().BeLessThanOrEqualTo(1, $"ширина окна не меняется на «{r.Name}»: {log}");
                Math.Abs(r.Size.Height - first.Size.Height).Should().BeLessThanOrEqualTo(1, $"высота окна не меняется на «{r.Name}»: {log}");
                Math.Abs(r.Y - first.Y).Should().BeLessThanOrEqualTo(1, $"окно не прыгает по вертикали на «{r.Name}»: {log}");
                // «Оплатить» (нижняя панель закреплена) — внутри окна, а окно — внутри главного окна.
                r.PayVisible.Should().BeTrue($"«Оплатить» виден на «{r.Name}»: {log}");
                r.PayRect.Top.Should().BeGreaterThanOrEqualTo(-0.5, $"«Оплатить» не выше окна на «{r.Name}»: {log}");
                r.PayRect.Bottom.Should().BeLessThanOrEqualTo(r.Size.Height + 0.5, $"«Оплатить» не ниже окна на «{r.Name}»: {log}");
                r.BottomPx.Should().BeLessThanOrEqualTo(ownerBottomPx + 1, $"низ окна оплаты внутри главного окна на «{r.Name}»: {log}");
            }
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
