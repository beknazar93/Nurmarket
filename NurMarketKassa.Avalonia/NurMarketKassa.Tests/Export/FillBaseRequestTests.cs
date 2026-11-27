using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Export;

/// <summary>2026-11-26 (услуга «Заполним вашу базу», FillBaseRequest): проверка формы, готовое сообщение и ссылка wa.me на тот же номер поддержки, что везде.</summary>
public sealed class FillBaseRequestTests
{
    private static FillBaseRequest.Form Form(string shop = "Магазин «Нур»", string phone = "+996 700 123 456", FillBaseRequest.Sources sources = FillBaseRequest.Sources.Excel,
        string link = "", string comment = "") => new(shop, phone, sources, link, comment);

    [Fact]
    public void Validate_AcceptsFilledForm_AndRejectsEachMissingPiece()
    {
        FillBaseRequest.Validate(Form()).Should().BeNull();
        FillBaseRequest.Validate(Form(link: "https://drive.google.com/drive/folders/abc")).Should().BeNull();
        FillBaseRequest.Validate(Form(shop: "  ")).Should().NotBeNullOrEmpty("нет названия магазина");
        FillBaseRequest.Validate(Form(phone: "123")).Should().NotBeNullOrEmpty("телефон короче 9 цифр");
        FillBaseRequest.Validate(Form(sources: FillBaseRequest.Sources.None)).Should().NotBeNullOrEmpty("не отмечено, что есть");
        FillBaseRequest.Validate(Form(link: "мои файлы на флешке")).Should().NotBeNullOrEmpty("ссылка не http(s)");
        FillBaseRequest.Validate(Form(link: "ftp://host/file")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BuildText_HasShopPhoneSourcesLinkAndComment()
    {
        var form = Form(sources: FillBaseRequest.Sources.Excel | FillBaseRequest.Sources.OneC, link: "https://disk.yandex.ru/d/xyz", comment: "около 3000 товаров");
        var text = FillBaseRequest.BuildText(form);
        text.Should().Contain("Магазин «Нур»").And.Contain("+996 700 123 456").And.Contain("https://disk.yandex.ru/d/xyz").And.Contain("около 3000 товаров");
        text.Should().Contain(FillBaseRequest.SourceTitle(FillBaseRequest.Sources.Excel)).And.Contain(FillBaseRequest.SourceTitle(FillBaseRequest.Sources.OneC))
            .And.NotContain(FillBaseRequest.SourceTitle(FillBaseRequest.Sources.InvoicePhotos));
        FillBaseRequest.BuildText(Form()).Should().NotContain("http", "без ссылки в сообщении — «файлы пришлю в чате»");
    }

    [Fact]
    public void BuildUrl_IsWaMeToSupportNumber_WithEncodedReadyText_NoAutoSend()
    {
        var form = Form(comment: "Нужно к понедельнику & срочно?");
        var url = FillBaseRequest.BuildUrl(form);
        var supportDigits = new string(MarketSphereLock.SupportPhone.Where(char.IsDigit).ToArray());
        url.Should().StartWith("https://wa.me/" + supportDigits + "?text=");
        supportDigits.Should().Be("996221068796");
        url.Should().NotContain(" ").And.NotContain("\n", "текст закодирован целиком");
        Uri.UnescapeDataString(url[(url.IndexOf("?text=", StringComparison.Ordinal) + 6)..]).Should().Be(FillBaseRequest.BuildText(form));
    }

    [Fact]
    public void Validate_RejectsMessageTooLongForWhatsAppLink()
    {
        // 2026-11-26 (стресс-тест): поля окна допускают 120 + 400 + 400 знаков; кириллица в ссылке — по 6 знаков на букву
        var longForm = Form(shop: new string('Н', 120), link: "https://drive.google.com/" + new string('a', 370), comment: new string('ж', 400));
        FillBaseRequest.BuildUrl(longForm).Length.Should().BeGreaterThan(FillBaseRequest.MaxUrlLength);
        FillBaseRequest.Validate(longForm).Should().NotBeNullOrEmpty("слишком длинную ссылку WhatsApp не откроет — просим сократить комментарий");
        FillBaseRequest.Validate(Form(comment: "около 3000 товаров, 2 склада, есть весовые 🍎")).Should().BeNull("обычный комментарий с эмодзи проходит");
    }
}
