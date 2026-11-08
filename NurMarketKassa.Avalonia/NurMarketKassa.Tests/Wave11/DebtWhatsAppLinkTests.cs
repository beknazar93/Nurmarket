using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave11;

/// <summary>2026-11-08, «вернуть кнопку WA»: ссылка wa.me для напоминания о долге — нормализация номера, текст на 5 языках, пустой номер, отказ клиента.
/// Чистая логика (ничего не открывает и не отправляет).</summary>
public sealed class DebtWhatsAppLinkTests
{
    [Theory]
    [InlineData("+996 700 123 456", "996700123456")]
    [InlineData("0700123456", "996700123456")]
    [InlineData("0700 12-34-56", "996700123456")]
    [InlineData("700123456", "996700123456")]
    [InlineData("996700123456", "996700123456")]
    [InlineData("+9960700123456", "996700123456")]
    [InlineData("00996700123456", "996700123456")]
    [InlineData("(0700) 123-456", "996700123456")]
    [InlineData("+7 700 123 45 67", "77001234567")]
    public void NormalizePhone_gives_international_digits(string phone, string expected) =>
        DebtWhatsAppLink.NormalizePhone(phone).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("89001234567")] // без «+» чужой номер не угадываем
    [InlineData("+123")]
    public void NormalizePhone_rejects_empty_and_garbage(string? phone) =>
        DebtWhatsAppLink.NormalizePhone(phone).Should().BeNull();

    [Fact]
    public void BuildUrl_has_digits_and_escaped_text()
    {
        var url = DebtWhatsAppLink.BuildUrl("0700 123 456", "Привет & «долг» 100%");
        url.Should().StartWith("https://wa.me/996700123456?text=");
        url.Should().NotContain(" ").And.NotContain("&").And.NotContain("«");
        Uri.UnescapeDataString(url![url.IndexOf("text=", StringComparison.Ordinal)..][5..]).Should().Be("Привет & «долг» 100%");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не номер")]
    public void BuildUrl_without_number_is_null(string? phone) =>
        DebtWhatsAppLink.BuildUrl(phone, "текст").Should().BeNull();

    [Fact]
    public void BuildUrl_without_text_is_plain_chat() =>
        DebtWhatsAppLink.BuildUrl("0700123456", "").Should().Be("https://wa.me/996700123456");

    [Theory]
    [InlineData("ru", "Здравствуйте, Айбек!", "12 345,50 сом", "«Семья»", "Долг с 01.10.2026.", "Срок оплаты — 20.11.2026.", "Реквизиты для оплаты: MBank 0700 123 456.", "Спасибо!")]
    [InlineData("ky", "Саламатсызбы, Айбек!", "12 345,50 сом", "«Семья»", "Карыз 01.10.2026 баштап.", "Төлөө мөөнөтү — 20.11.2026.", "Төлөө реквизиттери: MBank 0700 123 456.", "Рахмат!")]
    [InlineData("en", "Hello, Айбек!", "12 345,50 som", "“Семья”", "Owed since 01.10.2026.", "Pay by 20.11.2026.", "Payment details: MBank 0700 123 456.", "Thank you!")]
    [InlineData("tr", "Merhaba Айбек!", "12 345,50 som", "«Семья»", "Borç 01.10.2026 tarihinden beri.", "Son ödeme tarihi — 20.11.2026.", "Ödeme bilgileri: MBank 0700 123 456.", "Teşekkürler!")]
    [InlineData("uz", "Assalomu alaykum, Айбек!", "12 345,50 so'm", "«Семья»", "Qarz 01.10.2026 dan beri.", "To'lash muddati — 20.11.2026.", "To'lov rekvizitlari: MBank 0700 123 456.", "Rahmat!")]
    public void BuildText_is_ready_in_all_five_languages(string lang, string hello, string amount, string shop, string since, string due, string req, string thanks)
    {
        var text = DebtWhatsAppLink.BuildText(new DebtWhatsAppLink.Info("Айбек", 12345.5, "Семья", new DateTime(2026, 10, 1), new DateTime(2026, 11, 20), "MBank 0700 123 456"), lang);
        text.Should().StartWith(hello).And.Contain(amount).And.Contain(shop).And.Contain(since).And.Contain(due).And.Contain(req).And.EndWith(thanks);
        text.Should().NotContain(" ", "в ссылке чата нет неразрывных пробелов");
    }

    [Theory]
    [InlineData("ru", "в нашем магазине")]
    [InlineData("ky", "Дүкөнүбүздөгү")]
    [InlineData("en", "at our shop")]
    [InlineData("tr", "Mağazamızdaki")]
    [InlineData("uz", "Do'konimizdagi")]
    public void BuildText_minimal_has_only_name_amount_and_generic_shop(string lang, string generic)
    {
        var text = DebtWhatsAppLink.BuildText(new DebtWhatsAppLink.Info("Айбек", 500, "  "), lang);
        text.Should().Contain(generic).And.Contain("500").And.Contain("Айбек");
        text.Should().NotContain("01.").And.NotContain("еквизит");
    }

    [Fact]
    public void BuildText_unknown_language_falls_back_to_russian() =>
        DebtWhatsAppLink.BuildText(new DebtWhatsAppLink.Info("Айбек", 100, "Магазин"), "de").Should().StartWith("Здравствуйте, Айбек!");

    [Theory]
    [InlineData("ky", "ky")]
    [InlineData("KG", "ky")]
    [InlineData("kyrgyz", "ky")]
    [InlineData("English", "en")]
    [InlineData("tr-TR", "tr")]
    [InlineData("uz", "uz")]
    [InlineData("ru", "ru")]
    [InlineData("", "tr")] // пустой язык карточки — язык кассы (тут условно tr)
    [InlineData(null, "tr")]
    [InlineData("de", "tr")]
    public void NormalizeLanguage_maps_card_language_or_uses_fallback(string? lang, string expected) =>
        DebtWhatsAppLink.NormalizeLanguage(lang, "tr").Should().Be(expected);

    [Theory]
    [InlineData(false, false, true, true)]   // оба выключены, дата есть — клиент/кассир выключал: предупредить
    [InlineData(false, false, false, false)] // прежний клиент: «не спрашивали», не отказ
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void IsExplicitRefusal_needs_dated_switch_off(bool service, bool marketing, bool dated, bool expected) =>
        DebtWhatsAppLink.IsExplicitRefusal(service, marketing, dated).Should().Be(expected);

    [Fact]
    public void FormatAmount_whole_and_fractional()
    {
        DebtWhatsAppLink.FormatAmount(1500).Should().Be("1 500");
        DebtWhatsAppLink.FormatAmount(1500.5).Should().Be("1 500,50");
        DebtWhatsAppLink.FormatAmount(0).Should().Be("0");
    }
}
