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

/// <summary>2026-11-20 (скриншот владельца: окно чека, блок «Возвраты»): строка возврата вылезала за карточку чека, а сумма «−255» рвалась
/// по одной цифре. Теперь возврат — двухстрочная карточка (дата, название с переносом, статус плашкой; сумма справа в одну строку).
/// Проверка на ru/ky/uz при ширине окна 360 и 520 px: ни один элемент не выходит за карточку, сумма возврата — в одну строку,
/// длинные название и статус целиком в границах, длинные подписи в других блоках не выдавливают значения. Эталоны не затрагиваются.</summary>
[Collection("UiScenarios")]
public sealed class ReceiptReturnsLayoutUiTests
{
    private const string LongName = "Макароны «Макфа» спагетти 450г очень длинное название товара для проверки переноса";

    private static SaleReceiptCardData Card()
    {
        var d = new SaleReceiptCardData
        {
            Shop = "Nur Test", Number = "1601", DateTimeText = "11.11.2026 16:30", Cashier = "Айгуль Эсенгельдиевна", Cashbox = "Основная касса компании",
            Title = SaleReceiptCardData.TitleText(false), TotalLabel = "Итого после возвратов", TotalText = SaleReceiptCardData.Som(255),
            ReturnsCaption = Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"),
        };
        d.Lines.Add(new SaleReceiptCardData.Line(LongName, "3 шт × 85", SaleReceiptCardData.Fmt(255), null, "Возвращено: 3"));
        d.Payment.Add(new SaleReceiptCardData.Row("Оплата наличными и переводом одновременно", SaleReceiptCardData.Fmt(255)));
        d.Returns.Add(new SaleReceiptCardData.Row(LongName + " × 3", "", 0, "11.11 16:34", ReturnEdit24hVoided()));
        d.Returns.Add(new SaleReceiptCardData.Row(LongName + " × 3", "−" + SaleReceiptCardData.Fmt(255), 0, "11.11 16:35"));
        d.Returns.Add(new SaleReceiptCardData.Row("   наличными 100 · на карту/перевод 155 · в счёт долга 0", ""));
        d.Returns.Add(new SaleReceiptCardData.Row("Возвращено всего", "−" + SaleReceiptCardData.Fmt(510)));
        d.Returns.Add(new SaleReceiptCardData.Row("По данным сервера", ""));
        return d;
    }

    private static string ReturnEdit24hVoided() => NurMarketKassa.Services.Api.ReturnEditErrors.VoidedMark();

    private static void Check(AppLanguage lang, int windowWidth)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var card = SaleReceiptCardView.Build(Card(), 380);
        var window = new Window { Width = windowWidth, Height = 1400, Content = new ScrollViewer { Content = new Border { Padding = new Thickness(8), Child = card } } };
        window.Show();
        UiShot.Settle(4, 20);
        try
        {
            var cardWidth = card.Bounds.Width;
            cardWidth.Should().BeLessThanOrEqualTo(windowWidth - 16 + 0.5);
            var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            texts.Should().NotBeEmpty();
            foreach (var t in texts)
            {
                var pos = t.TranslatePoint(new Point(0, 0), card)!.Value;
                var what = $"{lang}/{windowWidth}: «{t.Text}»";
                pos.X.Should().BeGreaterThanOrEqualTo(-0.5, what);
                (pos.X + t.Bounds.Width).Should().BeLessThanOrEqualTo(cardWidth + 0.5, what + " выходит за правый край карточки");
            }
            // Сумма возврата — в одну строку (высота ≤ 1,6 высоты строки), цифры не в столбик.
            foreach (var amount in texts.Where(t => t.Text!.StartsWith('−')))
                amount.Bounds.Height.Should().BeLessThanOrEqualTo(amount.LineHeight * 1.6, $"{lang}/{windowWidth}: сумма «{amount.Text}» в одну строку");
            // Название возврата и плашка статуса показаны целиком в границах (перенос по словам, не больше 3 строк).
            var name = texts.First(t => t.Text!.StartsWith("Макароны") && t.MaxLines == 3);
            name.Bounds.Height.Should().BeLessThanOrEqualTo(name.LineHeight * 3 + 1);
            texts.Should().Contain(t => t.Text == NurMarketKassa.Services.Api.ReturnEditErrors.VoidedMark(), "статус отдельной плашкой");
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
    public async Task Returns_card_stays_inside_the_receipt_and_amount_is_one_line(AppLanguage lang, int width)
    {
        TestHost.Ensure();
        await TestHost.WarmAsync();
        Check(lang, width);
    }
}
