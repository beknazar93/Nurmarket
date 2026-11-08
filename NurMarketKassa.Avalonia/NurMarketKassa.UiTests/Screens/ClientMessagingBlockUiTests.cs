using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08 (п. 31, 35 ответа сервера): блок «Согласия на сообщения» и «Каналы» в карточке клиента (ширина карточки 580 → блок 536) на 1024×640 и 1366×768,
/// ru и ky: помещается, текст не обрезан, подсказка «согласий нет» видна у клиента без согласий, каналы показаны. Без сервера — ShowPreview.</summary>
[Collection("UiScenarios")]
public sealed class ClientMessagingBlockUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new[] { (1024, 640), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.Item1, s.Item2, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void ConsentsBlock_FitsAndIsNotClipped(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var block = new ClientMessagingBlock(null) { Width = 536 };
        block.ShowPreview(ClientConsents.Empty, new[] { new ClientChannel("telegram", "Айбек (@aibek)"), new ClientChannel("whatsapp", "+996 555 000 111") });
        var window = new Window { Content = new ScrollViewer { Content = block, Padding = new Avalonia.Thickness(22, 14) } };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var texts = block.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().Contain(t => t.Contains("/start"), "у клиента без согласий кассир видит подсказку про /start");
            texts.Should().Contain(t => t.StartsWith("Telegram"), "каналы клиента показаны");
            block.Bounds.Width.Should().BeLessThanOrEqualTo(536.5);
            var found = TextClipChecker.Find(window, "client-consents", lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            found.Should().BeEmpty("текст блока не обрезан: " + string.Join("; ", found.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void ConsentsBlock_HidesChannelsWithoutRights_AndShowsUpdatedBy()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var block = new ClientMessagingBlock(null) { Width = 536 };
        block.ShowPreview(new ClientConsents(true, false, new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), "cashier", "ru"), null);
        var window = new Window { Content = block };
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            var texts = block.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().NotContain(t => t == "Каналы клиента", "403/404 — блок каналов скрыт");
            texts.Should().Contain(t => t.StartsWith("Обновлено") && t.Contains("кассиром"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [Fact]
    public void SourceText_names_the_cashier_and_keeps_unknown_values()
    {
        ClientMessagingBlock.SourceText("cashier").Should().NotBeNullOrEmpty().And.NotBe("cashier");
        ClientMessagingBlock.SourceText("something").Should().Be("something");
        ClientMessagingBlock.SourceText(null).Should().BeEmpty();
        ClientMessagingBlock.NoConsentHint.Should().Contain("/start");
    }
}
