using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (отчёт «Конкуренты», п. 3, «QR витрины на чеке»): на карточке чека второй QR «Заказать онлайн» стоит под QR электронного чека,
/// ничего не налезает и не выходит за карточку на ru/ky/uz при ширине окна 360 и 520 px; без адреса витрины и на возврате блока нет.</summary>
[Collection("UiScenarios")]
public sealed class ShopQrReceiptUiTests
{
    private const string Shop = "https://market.nurcrm.kg/catalog/my-shop";

    private static SaleReceiptCardData Card(string? shop, bool isReturn = false)
    {
        var d = new SaleReceiptCardData
        {
            Shop = "Nur Test", Number = "1601", DateTimeText = "26.11.2026 16:30", Cashier = "Айгуль Эсенгельдиевна",
            Title = SaleReceiptCardData.TitleText(isReturn), TotalLabel = "Итого", TotalText = SaleReceiptCardData.Som(255),
            QrUrl = "https://app.nurcrm.kg/r/BtrrI29oBmasghsi1u7cNG4G7d9XTcsM", ShopQrUrl = shop,
            Status = isReturn ? SaleReceiptCardData.StatusKind.Return : SaleReceiptCardData.StatusKind.Paid,
        };
        d.Lines.Add(new SaleReceiptCardData.Line("Молоко 1 л", "3 шт × 85", SaleReceiptCardData.Fmt(255), null));
        d.Payment.Add(new SaleReceiptCardData.Row("Оплата наличными", SaleReceiptCardData.Fmt(255)));
        return d;
    }

    private static void Check(AppLanguage lang, int windowWidth)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var card = SaleReceiptCardView.Build(Card(Shop), 380);
        var window = new Window { Width = windowWidth, Height = 1400, Content = new ScrollViewer { Content = new Border { Padding = new Thickness(8), Child = card } } };
        window.Show();
        UiShot.Settle(4, 20);
        try
        {
            var qrs = card.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "ReceiptQrView" && c.IsEffectivelyVisible).ToList();
            qrs.Should().HaveCount(2, "QR электронного чека и QR витрины");
            var shopQr = qrs.Single(c => c.Name == "ShopQr");
            var receiptQr = qrs.Single(c => c.Name != "ShopQr");
            var shopTop = shopQr.TranslatePoint(new Point(0, 0), card)!.Value;
            var receiptTop = receiptQr.TranslatePoint(new Point(0, 0), card)!.Value;
            shopTop.Y.Should().BeGreaterThan(receiptTop.Y + receiptQr.Bounds.Height, $"{lang}/{windowWidth}: QR витрины под QR чека, без наложения");
            (shopTop.X + shopQr.Bounds.Width).Should().BeLessThanOrEqualTo(card.Bounds.Width + 0.5);

            var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            var caption = texts.Single(t => t.Text == StorefrontLink.Caption);
            var captionTop = caption.TranslatePoint(new Point(0, 0), card)!.Value;
            captionTop.Y.Should().BeGreaterThanOrEqualTo(shopTop.Y + shopQr.Bounds.Height - 0.5, $"{lang}/{windowWidth}: подпись «Заказать онлайн» под QR");
            foreach (var t in texts)
            {
                var pos = t.TranslatePoint(new Point(0, 0), card)!.Value;
                (pos.X + t.Bounds.Width).Should().BeLessThanOrEqualTo(card.Bounds.Width + 0.5, $"{lang}/{windowWidth}: «{t.Text}» выходит за карточку");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian, 360)]
    [InlineData(AppLanguage.Russian, 520)]
    [InlineData(AppLanguage.Kyrgyz, 360)]
    [InlineData(AppLanguage.Kyrgyz, 520)]
    [InlineData(AppLanguage.Uzbek, 360)]
    [InlineData(AppLanguage.Uzbek, 520)]
    public async Task ReceiptCard_ShopQr_StandsUnderTheReceiptQr_WithoutOverlap(AppLanguage lang, int windowWidth)
    {
        await TestHost.WarmAsync();
        Check(lang, windowWidth);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptCard_WithoutAddressOrOnReturn_HasNoShopQr(bool isReturn)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var card = SaleReceiptCardView.Build(Card(isReturn ? Shop : null, isReturn), 380);
        var window = new Window { Width = 520, Height = 1400, Content = new ScrollViewer { Content = new Border { Padding = new Thickness(8), Child = card } } };
        window.Show();
        UiShot.Settle(4, 20);
        try
        {
            card.GetVisualDescendants().OfType<Control>().Should().NotContain(c => c.Name == "ShopQr");
            card.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(t => t.Text == StorefrontLink.Caption);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
