using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Checkout;

/// <summary>2026-11-26 (владелец: «при скане QR на чеке должен открываться красивый чек, а не текст»): на обычном (не фискальном) чеке — QR со ссылкой
/// на публичную страницу чека (share_url сервера). Проверяем: что считается печатной ссылкой, как ссылка едет к принтеру в тексте, что читается из ответа
/// сервера, что окно «QR-код чека» кодирует ссылку (а не текст чека), и что в ленте ESC/POS появляется родной QR только когда ссылка есть.</summary>
[Collection("UserPreferences")]
public sealed class ReceiptShareLinkTests
{
    private const string Url = "https://app.nurcrm.kg/r/BtrrI29oBmasghsi1u7cNG4G7d9XTcsM";

    [Theory]
    [InlineData(Url, true)]
    [InlineData("http://app.nurcrm.kg/r/abc", false)]
    [InlineData("https://app.nurcrm.kg/r/абв", false)]
    [InlineData("https://app.nurcrm.kg/r/a b", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_plain_https_link_is_printable(string? url, bool expected) =>
        ReceiptShareLink.IsPrintable(url).Should().Be(expected);

    [Fact]
    public void Print_of_the_link_qr_is_on_by_default() =>
        new UserPreferences().PrintReceiptLinkQr.Should().BeTrue();

    [Fact]
    public void Link_travels_in_front_of_the_receipt_text_and_comes_back_out()
    {
        var withLink = ReceiptShareLink.Attach("ЧЕК 1\nИТОГО 100", Url, enabled: true);
        withLink.Should().StartWith("\u0001QRURL ");
        var text = ReceiptShareLink.Split(withLink, out var url);
        text.Should().Be("ЧЕК 1\nИТОГО 100");
        url.Should().Be(Url);
    }

    [Fact]
    public void Empty_text_still_carries_the_link_so_the_printer_builds_the_receipt_from_the_cart()
    {
        var text = ReceiptShareLink.Split(ReceiptShareLink.Attach("", Url, enabled: true), out var url);
        text.Should().BeEmpty();
        url.Should().Be(Url);
    }

    [Fact]
    public void Switched_off_or_bad_link_leaves_the_text_untouched()
    {
        ReceiptShareLink.Attach("ЧЕК", Url, enabled: false).Should().Be("ЧЕК");
        ReceiptShareLink.Attach("ЧЕК", null, enabled: true).Should().Be("ЧЕК");
        ReceiptShareLink.Attach("ЧЕК", "http://x", enabled: true).Should().Be("ЧЕК");
        ReceiptShareLink.Split("ЧЕК", out var none).Should().Be("ЧЕК");
        none.Should().BeNull();
    }

    [Fact]
    public void Share_url_is_read_from_the_sale_and_from_a_wrapped_answer()
    {
        using var flat = JsonDocument.Parse($"{{\"id\":\"1\",\"share_url\":\"{Url}\"}}");
        using var wrapped = JsonDocument.Parse($"{{\"data\":{{\"share_url\":\"{Url}\"}}}}");
        using var none = JsonDocument.Parse("{\"id\":\"1\",\"share_url\":null}");
        ReceiptShareLink.FromSale(flat.RootElement).Should().Be(Url);
        ReceiptShareLink.FromSale(wrapped.RootElement).Should().Be(Url);
        ReceiptShareLink.FromSale(none.RootElement).Should().BeNull();
    }

    [Fact]
    public async Task A_known_link_is_used_without_any_request()
    {
        using var doc = JsonDocument.Parse($"{{\"share_url\":\"{Url}\"}}");
        (await ReceiptShareLink.EnsureAsync(null, "1", doc.RootElement)).Should().Be(Url);
    }

    [Fact]
    public void Receipt_qr_encodes_the_web_page_when_the_server_gave_it_and_the_text_only_when_it_did_not()
    {
        using var withLink = JsonDocument.Parse($"{{\"id\":\"7\",\"share_url\":\"{Url}\"}}");
        SaleReceiptCardData.EReceiptUrl(withLink.RootElement).Should().Be(Url, "страница чека, а не PDF и не текст");

        var card = new SaleReceiptCardData { Shop = "Магазин", Number = "7", TotalText = "100 сом", QrUrl = Url };
        card.QrPayload().Should().Be(Url);
        card.QrUrl = null;
        card.QrPayload().Should().Contain("Магазин", "без ссылки — прежнее поведение: чек текстом");
    }

    [Fact]
    public void Escpos_tape_gets_a_native_qr_with_the_link_before_the_cut_only_when_there_is_a_link()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cfg = new ReceiptPrinterSettings { Enabled = true, DevicePath = "COM1", TextEncoding = "utf8-pos" };
        var qrStore = new byte[] { 0x31, 0x50, 0x30 };

        var plain = EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, "МАГАЗИН\nИТОГО 100", 32);
        Contains(plain, qrStore).Should().BeFalse("без ссылки QR на ленте нет — вид чека не меняется");

        var linked = EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, "МАГАЗИН\nИТОГО 100", 32, Url);
        var storeAt = IndexOf(linked, qrStore);
        storeAt.Should().BeGreaterThan(0);
        Encoding.ASCII.GetString(linked, storeAt + 3, Url.Length).Should().Be(Url, "в QR — адрес страницы чека");
        var cutAt = LastIndexOf(linked, new byte[] { 0x1D, 0x56 });
        cutAt.Should().BeGreaterThan(storeAt, "QR печатается до отреза ленты");

        var fiscal = EscPosTextReceiptPrinter.BuildFiscalPayload(cfg, "ФД 1\nQR\thttps://ofd.example.kg/check?id=1");
        Contains(fiscal, qrStore).Should().BeTrue("фискальный QR eKassa печатается как раньше");
    }

    private static bool Contains(byte[] hay, byte[] needle) => IndexOf(hay, needle) >= 0;

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
}
