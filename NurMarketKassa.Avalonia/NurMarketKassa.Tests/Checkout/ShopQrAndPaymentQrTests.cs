using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Checkout;

/// <summary>2026-11-26 (отчёт «Конкуренты», пп. 1 и 3): (1) «QR на сумму чека» — самопроверка статического QR банка (контрольная сумма сходится с формулой ELQR),
/// QR с суммой собирается только для таких, на новой установке функция включена; (2) «QR витрины на чеке и ценнике» — адрес витрины (slug компании),
/// второй QR «Заказать онлайн» на ленте ESC/POS, на карточке/PDF чека и своим элементом на ценнике. Существующий QR электронного чека не затрагивается.</summary>
[Collection("UserPreferences")]
public sealed class ShopQrAndPaymentQrTests : IDisposable
{
    private const string Shop = "https://market.nurcrm.kg/catalog/my-shop";
    private const string ReceiptUrl = "https://app.nurcrm.kg/r/BtrrI29oBmasghsi1u7cNG4G7d9XTcsM";

    private readonly string? _slug = UserPreferences.Instance.StorefrontSlug;
    private readonly string? _slugCompany = UserPreferences.Instance.StorefrontSlugCompany;

    public ShopQrAndPaymentQrTests()
    {
        UserPreferences.Instance.StorefrontSlug = null;
        UserPreferences.Instance.StorefrontSlugCompany = null;
    }

    public void Dispose()
    {
        UserPreferences.Instance.StorefrontSlug = _slug;
        UserPreferences.Instance.StorefrontSlugCompany = _slugCompany;
    }

    // ------------------------------------------------------------------ адрес витрины

    [Theory]
    [InlineData("my-shop", true)]
    [InlineData("Shop123", true)]
    [InlineData("ab", false)]
    [InlineData("мой-магазин", false)]
    [InlineData("a b c", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Slug_follows_the_site_rule(string? slug, bool expected) =>
        StorefrontLink.IsValidSlug(slug).Should().Be(expected);

    [Fact]
    public void Storefront_url_is_built_from_the_remembered_slug_and_hidden_without_it()
    {
        StorefrontLink.Url.Should().BeNull("slug не получен — QR не рисуется");
        StorefrontLink.Remember("my-shop");
        StorefrontLink.Url.Should().Be(Shop);
        ReceiptShareLink.IsPrintable(StorefrontLink.Url).Should().BeTrue("годится для QR принтера: https, ASCII");
    }

    [Fact]
    public void Slug_of_another_company_is_not_printed()
    {
        UserPreferences.Instance.StorefrontSlug = "other-shop";
        UserPreferences.Instance.StorefrontSlugCompany = "c-other-company-id";
        StorefrontLink.Url.Should().BeNull("после смены входа чужой адрес не печатается");
    }

    [Fact]
    public void Slug_is_read_from_the_server_answer_plain_or_wrapped_and_empty_slug_clears_it()
    {
        using var plain = JsonDocument.Parse("{\"name\":\"Магазин\",\"slug\":\"my-shop\"}");
        StorefrontLink.RememberFromJson(plain.RootElement);
        StorefrontLink.Url.Should().Be(Shop);

        using var wrapped = JsonDocument.Parse("{\"data\":{\"slug\":\"second-shop\"}}");
        StorefrontLink.RememberFromJson(wrapped.RootElement);
        StorefrontLink.Url.Should().EndWith("/catalog/second-shop");

        using var noField = JsonDocument.Parse("{\"name\":\"Магазин\"}");
        StorefrontLink.RememberFromJson(noField.RootElement);
        StorefrontLink.Url.Should().EndWith("/catalog/second-shop", "в ответе нет slug — прежний адрес не трогаем");

        using var garbage = JsonDocument.Parse("{\"slug\":\"a b\"}");
        StorefrontLink.RememberFromJson(garbage.RootElement);
        StorefrontLink.Url.Should().EndWith("/catalog/second-shop", "мусор вместо адреса игнорируется");

        using var empty = JsonDocument.Parse("{\"slug\":\"\"}");
        StorefrontLink.RememberFromJson(empty.RootElement);
        StorefrontLink.Url.Should().BeNull("сервер сказал «витрины нет»");
    }

    [Fact]
    public void Shop_qr_on_the_receipt_is_off_by_default_and_the_payment_qr_with_amount_is_on_for_a_new_install()
    {
        var fresh = new UserPreferences();
        fresh.PrintShopQr.Should().BeFalse("у действующих клиентов чек после обновления не меняется");
        fresh.DynamicPaymentQrEnabled.Should().BeTrue("QR с суммой на новой установке включён: касса сама проверяет QR банка");
        fresh.PrintReceiptLinkQr.Should().BeTrue("QR электронного чека по-прежнему включён");
    }

    // ------------------------------------------------------------------ лента ESC/POS

    private static readonly byte[] QrStore = { 0x31, 0x50, 0x30 };

    private static ReceiptPrinterSettings Cfg() => new() { Enabled = true, DevicePath = "COM1", TextEncoding = "utf8-pos" };

    [Fact]
    public void Escpos_tape_gets_the_shop_qr_with_caption_before_the_cut_and_keeps_the_receipt_qr()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var plain = EscPosTextReceiptPrinter.BuildEscPosPayload(Cfg(), "МАГАЗИН\nИТОГО 100", 32);
        Count(plain, QrStore).Should().Be(0, "ничего не включено — вид чека не меняется");

        var shopOnly = EscPosTextReceiptPrinter.BuildEscPosPayload(Cfg(), "МАГАЗИН\nИТОГО 100", 32, null, Shop);
        Count(shopOnly, QrStore).Should().Be(1);
        var at = IndexOf(shopOnly, QrStore);
        Encoding.ASCII.GetString(shopOnly, at + 3, Shop.Length).Should().Be(Shop, "в QR — адрес витрины");
        IndexOf(shopOnly, Encoding.UTF8.GetBytes(StorefrontLink.Caption.ToUpperInvariant())).Should().BeGreaterThan(0, "подпись «Заказать онлайн» печатается");
        LastIndexOf(shopOnly, new byte[] { 0x1D, 0x56 }).Should().BeGreaterThan(at, "QR печатается до отреза ленты");

        var both = EscPosTextReceiptPrinter.BuildEscPosPayload(Cfg(), "МАГАЗИН\nИТОГО 100", 32, ReceiptUrl, Shop);
        Count(both, QrStore).Should().Be(2, "QR электронного чека и QR витрины");
        var first = IndexOf(both, QrStore);
        Encoding.ASCII.GetString(both, first + 3, ReceiptUrl.Length).Should().Be(ReceiptUrl, "QR чека идёт первым, как раньше");

        var badAddress = EscPosTextReceiptPrinter.BuildEscPosPayload(Cfg(), "МАГАЗИН", 32, null, "http://не-ссылка");
        Count(badAddress, QrStore).Should().Be(0, "негодный адрес — QR не печатается");
    }

    [Fact]
    public void Escpos_tape_in_cp866_also_gets_the_shop_qr()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cfg = new ReceiptPrinterSettings { Enabled = true, DevicePath = "COM1", TextEncoding = "cp866" };
        Count(EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, "МАГАЗИН\nИТОГО 100", 32, null, Shop), QrStore).Should().Be(1);
    }

    // ------------------------------------------------------------------ карточка и PDF чека

    [Fact]
    public void Receipt_card_shows_the_shop_qr_only_on_a_normal_sale()
    {
        var card = new SaleReceiptCardData { Shop = "Магазин", Number = "7", TotalText = "100 сом", QrUrl = ReceiptUrl };
        card.ShowShopQr.Should().BeFalse("адреса витрины нет");
        card.ToText().Should().NotContain(StorefrontLink.Caption);

        card.ShopQrUrl = Shop;
        card.ShowShopQr.Should().BeTrue();
        card.ToText().Should().Contain(StorefrontLink.Caption + ": " + Shop);
        card.QrPayload().Should().Be(ReceiptUrl, "QR электронного чека не затронут");

        card.Status = SaleReceiptCardData.StatusKind.Return;
        card.ShowShopQr.Should().BeFalse("на возвратном документе «Заказать онлайн» не нужно");
    }

    [Fact]
    public void Receipt_pdf_carries_the_shop_caption_and_does_not_break_without_it()
    {
        var card = new SaleReceiptCardData { Shop = "Магазин", Number = "7", TotalText = "100 сом", Title = "ТОВАРНЫЙ ЧЕК", QrUrl = ReceiptUrl };
        var without = ReceiptSkiaPdf.Render(card);
        card.ShopQrUrl = Shop;
        var with = ReceiptSkiaPdf.Render(card);
        without.Length.Should().BeGreaterThan(500);
        with.Length.Should().BeGreaterThan(without.Length, "в PDF добавились подпись и адрес витрины");
    }

    // ------------------------------------------------------------------ ценник

    private static LabelTemplate BareLabel() => new()
    {
        WidthMm = 40,
        HeightMm = 30,
        Barcode = { Enabled = false },
        ProductName = { Enabled = false },
        Price = { Enabled = false },
    };

    private static int Ink(LabelTemplate t)
    {
        using var bmp = LabelSceneSkia.Render(BarcodeLabelService.BuildLabelScene("", "", null, t));
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (SkiaRaster.Luminance(bmp.GetPixel(x, y)) < 128)
                    n++;
        return n;
    }

    [Fact]
    public void Label_shop_qr_element_is_drawn_when_there_is_an_address_and_hidden_without_it()
    {
        var t = BareLabel();
        var e = LabelExtras.Create(LabelExtraKind.ShopQr, t);
        e.Enabled = true;
        t.Extras.Add(e);

        t.Item = new LabelItemData();
        Ink(t).Should().Be(0, "витрины нет — элемент скрыт, а не пустой");

        t.Item = LabelExtras.Sample;
        Ink(t).Should().BeGreaterThan(300, "в редакторе виден образец QR");

        // Адрес магазина (а не образец) берётся, когда витрина подключена по тарифу (на «Старте» — платная услуга).
        t.Item = new LabelItemData(ShopUrl: Shop);
        Ink(t).Should().BeGreaterThan(300, "адрес витрины задан — QR печатается");
    }

    [Fact]
    public void Printable_address_needs_both_the_slug_and_a_connected_showcase()
    {
        StorefrontLink.PrintableUrl().Should().BeNull("slug не получен");
        StorefrontLink.Remember("my-shop");
        StorefrontLink.PrintableUrl().Should().Be(NurMarketKassa.Services.Api.ShowcaseApiService.IsShowcaseConnected ? Shop : null, "на «Старте» без услуги «Онлайн витрина» QR скрыт");
    }

    [Fact]
    public void Label_shop_qr_has_a_caption_row_only_when_the_block_is_big_enough()
    {
        var big = new LabelCustomElement { XMm = 2, YMm = 2, WidthMm = 14, HeightMm = 14 };
        var (qr, caption) = LabelExtras.ShopQrLayout(big);
        caption.Should().NotBeNull();
        (qr.HeightMm + caption!.HeightMm).Should().BeApproximately(14, 1e-9);
        caption.YMm.Should().BeApproximately(qr.YMm + qr.HeightMm, 1e-9, "подпись сразу под QR, блоки не налезают");

        var small = new LabelCustomElement { XMm = 2, YMm = 2, WidthMm = 10, HeightMm = 6 };
        LabelExtras.ShopQrLayout(small).Caption.Should().BeNull("в мелкий блок подпись не влезает — только QR");
    }

    [Fact]
    public void Label_shop_qr_kind_is_appended_to_the_enum_and_listed_in_the_editor()
    {
        ((int)LabelExtraKind.ShopQr).Should().Be(8, "числа прежних видов в сохранённых шаблонах не меняются");
        LabelExtras.AllKinds.Should().Contain(LabelExtraKind.ShopQr);
        var t = new LabelTemplate();
        var back = JsonSerializer.Deserialize<LabelTemplate>(JsonSerializer.Serialize(new LabelTemplate { Extras = { LabelExtras.Create(LabelExtraKind.ShopQr, t) } }))!;
        back.Extras.Single().Kind.Should().Be(LabelExtraKind.ShopQr);
    }

    // ------------------------------------------------------------------ QR оплаты с суммой: самопроверка QR банка

    private static string StaticElqrLink() =>
        "https://app.mbank.kg/qr/#" + ElqrQrBuilder.FinalizePayload(new ElqrQrBuilder.TlvField[]
        {
            new("00", "01"), new("01", "11"), new("32", "0012kg.elqr.test1015123456789012345"),
            new("52", "5999"), new("53", "417"), new("58", "KG"), new("59", "TestShop"), new("63", "0000"),
        });

    [Fact]
    public void Bank_qr_self_check_tells_apart_ok_foreign_checksum_and_not_elqr()
    {
        var ok = StaticElqrLink();
        ElqrQrBuilder.Inspect(ok).Should().Be(ElqrQrBuilder.StaticQrCheck.Ok);

        var tampered = ok[..^4] + (ok.EndsWith("abcd", StringComparison.Ordinal) ? "1234" : "abcd");
        ElqrQrBuilder.Inspect(tampered).Should().Be(ElqrQrBuilder.StaticQrCheck.ChecksumMismatch, "контрольная сумма иного вида — наша формула этому банку не подходит");

        ElqrQrBuilder.Inspect("https://example.org/pay").Should().Be(ElqrQrBuilder.StaticQrCheck.NotElqr);
        ElqrQrBuilder.Inspect("").Should().Be(ElqrQrBuilder.StaticQrCheck.NotElqr);
        ElqrQrBuilder.Inspect(null).Should().Be(ElqrQrBuilder.StaticQrCheck.NotElqr);
    }

    [Fact]
    public void Bank_qr_image_check_and_the_amount_qr_only_for_a_bank_that_passed_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-shopqr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var okPng = Path.Combine(dir, "ok.png");
            QrPngWriter.Write(StaticElqrLink(), 420, okPng);
            var bad = StaticElqrLink();
            var badPng = Path.Combine(dir, "bad.png");
            QrPngWriter.Write(bad[..^4] + (bad.EndsWith("abcd", StringComparison.Ordinal) ? "1234" : "abcd"), 420, badPng);
            var plainPng = Path.Combine(dir, "plain.png");
            QrPngWriter.Write("https://example.org/pay", 420, plainPng);
            var junk = Path.Combine(dir, "junk.png");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });

            DynamicPaymentQrService.Inspect(okPng).Should().Be(DynamicPaymentQrService.QrSupport.Ok);
            DynamicPaymentQrService.Inspect(badPng).Should().Be(DynamicPaymentQrService.QrSupport.ChecksumMismatch);
            DynamicPaymentQrService.Inspect(plainPng).Should().Be(DynamicPaymentQrService.QrSupport.NotElqr);
            DynamicPaymentQrService.Inspect(junk).Should().Be(DynamicPaymentQrService.QrSupport.Unreadable);
            DynamicPaymentQrService.Inspect(Path.Combine(dir, "none.png")).Should().Be(DynamicPaymentQrService.QrSupport.NoFile);

            DynamicPaymentQrService.TryBuildDynamicQrImage(okPng, 250.5m).Should().NotBeNull("проверенный QR — собираем с суммой");
            DynamicPaymentQrService.TryBuildDynamicQrImage(badPng, 250.5m).Should().BeNull("иная контрольная сумма — банк отверг бы QR, показываем обычный");
            DynamicPaymentQrService.TryBuildDynamicQrImage(plainPng, 250.5m).Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    private static int LastIndexOf(byte[] hay, byte[] needle)
    {
        for (var i = hay.Length - needle.Length; i >= 0; i--)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    private static int Count(byte[] hay, byte[] needle)
    {
        var n = 0;
        for (var i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                n++;
        return n;
    }
}
