using FluentAssertions;
using Xunit;
using System.Text;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 («Редактор чека», «название и цена на одном уровне»): строка товара «кол-во x цена … сумма» на ленте 32/42/48 знаков, перенос длинного названия,
/// шаблон чека (порядок, включение, жирность/размер, свой текст), один конвейер для принтера и предпросмотра, фискальная печать не затронута.</summary>
[Collection("UserPreferences")]
public sealed class ReceiptTemplateTests : IDisposable
{
    private readonly UserPreferences _p = UserPreferences.Instance;
    private readonly string? _oldTemplate;
    private readonly (string Store, string Inn, string Addr, bool Name, bool InnOn, bool AddrOn, bool No, bool Date, bool Items, bool Total, string? User) _old;

    public ReceiptTemplateTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // cp1251/cp866 для проверки байтов ленты
        _oldTemplate = _p.ReceiptTemplateJson;
        _old = (_p.StoreName, _p.StoreInn, _p.StoreAddress, _p.ShowStoreName, _p.ShowInn, _p.ShowAddress, _p.ShowReceiptNumber, _p.ShowDate, _p.ShowItems, _p.ShowTotal, PosApp.CurrentUserDisplayName);
        _p.ReceiptTemplateJson = null;
        _p.StoreName = "ТЕСТ МАРКЕТ";
        _p.StoreInn = "01234567890123";
        _p.StoreAddress = "г. Бишкек, ул. Тестовая, 1";
        _p.ShowStoreName = _p.ShowInn = _p.ShowAddress = _p.ShowReceiptNumber = _p.ShowDate = _p.ShowItems = _p.ShowTotal = true;
        PosApp.CurrentUserDisplayName = "Айгуль К.";
    }

    public void Dispose()
    {
        ReceiptLayout.WidthOverride = null;
        _p.ReceiptTemplateJson = _oldTemplate;
        (_p.StoreName, _p.StoreInn, _p.StoreAddress, _p.ShowStoreName, _p.ShowInn, _p.ShowAddress, _p.ShowReceiptNumber, _p.ShowDate, _p.ShowItems, _p.ShowTotal) =
            (_old.Store, _old.Inn, _old.Addr, _old.Name, _old.InnOn, _old.AddrOn, _old.No, _old.Date, _old.Items, _old.Total);
        PosApp.CurrentUserDisplayName = _old.User;
    }

    private const string LongNameCart = """
        { "receipt_number": "20261121-0042", "items": [
          { "product_name": "Капсула First Fresh 1шт*18 с очень длинным названием товара", "quantity": 1, "unit_price": 18, "line_total": 18 },
          { "product_name": "Хлеб", "quantity": 12.5, "unit_price": 1234.56, "line_total": 15432 } ] }
        """;

    // ───────────── строка позиции: название и цена на одном уровне ─────────────

    [Theory]
    [InlineData(32)]
    [InlineData(42)]
    [InlineData(48)]
    public void ItemBlock_QtyPriceAndTotal_OnOneLine_ExactWidth_TotalAtRightEdge(int width)
    {
        var lines = ReceiptLineLayout.FormatItemBlock("1 x 18.00", "18.00", width).ToList();
        lines.Should().ContainSingle("кол-во × цена и сумма — в одной строке");
        lines[0].Length.Should().Be(width, "строка ровно во всю ленту — принтер её не пересобирает");
        lines[0].Should().StartWith("1 x 18.00").And.EndWith("18.00 ");
    }

    [Fact]
    public void ItemBlock_TotalsOfDifferentLength_EndInSameColumn()
    {
        var a = ReceiptLineLayout.FormatItemBlock("1 x 18.00", "18.00", 32).Single();
        var b = ReceiptLineLayout.FormatItemBlock("12.5 x 1234.56", "15432.00", 32).Single();
        a.TrimEnd().Length.Should().Be(b.TrimEnd().Length, "последняя колонка суммы у правого края у всех позиций");
    }

    [Fact]
    public void ItemBlock_DoesNotFit_FallsBackToTwoLines()
    {
        var lines = ReceiptLineLayout.FormatItemBlock("1234.567 x 12345678.00", "999999999.99", 32).ToList();
        lines.Should().HaveCount(2);
        lines[1].TrimEnd().Should().EndWith("сом");
    }

    [Theory]
    [InlineData(32)]
    [InlineData(42)]
    [InlineData(48)]
    public void SaleReceipt_LongName_Wraps_AndEveryLineFits(int width)
    {
        ReceiptLayout.WidthOverride = width;
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(LongNameCart, null, "cash", "50000", new DateTime(2026, 11, 21, 10, 0, 0));
        var lines = text.Split('\n');
        lines.Should().OnlyContain(l => l.Length <= width, "ни одна строка не шире ленты");
        var idx = Array.FindIndex(lines, l => l.Contains("КАПСУЛА", StringComparison.Ordinal));
        idx.Should().BeGreaterThan(0);
        // Название — одна или несколько строк, затем сразу строка «1 X 18.00 … 18.00» (количество, цена и сумма на одном уровне).
        var itemLine = lines.Skip(idx).First(l => l.StartsWith("1 X 18.00", StringComparison.Ordinal));
        itemLine.TrimEnd().Should().EndWith("18.00");
        itemLine.Length.Should().Be(width);
        lines.Count(l => l.Contains("ДЛИННЫМ", StringComparison.Ordinal) || l.Contains("НАЗВАНИЕМ", StringComparison.Ordinal)).Should().BeGreaterThan(0);
    }

    // ───────────── шаблон по умолчанию ничего не меняет ─────────────

    [Fact]
    public void DefaultTemplate_NoServiceCharsInText_AndNotLaidOut()
    {
        ReceiptLayout.WidthOverride = 32;
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(LongNameCart, null, "cash", "50000", new DateTime(2026, 11, 21, 10, 0, 0));
        ReceiptLineStyle.IsLaidOut(text).Should().BeFalse();
        ReceiptLineStyle.Strip(text).Should().Be(text, "метки разделов убраны, служебных знаков нет");
        ReceiptTemplate.Default().IsDefault().Should().BeTrue();
    }

    // ───────────── шаблон ─────────────

    private static ReceiptTemplate Custom(Action<ReceiptTemplate> edit)
    {
        var t = ReceiptTemplate.Default();
        edit(t);
        return t;
    }

    [Fact]
    public void Template_JsonRoundTrip_AndNormalize_DropsUnknownAndClamps()
    {
        var t = Custom(x => { x.Block(ReceiptTemplate.Items).Size = 9; x.Block(ReceiptTemplate.Title).Align = "weird"; x.Block(ReceiptTemplate.HeaderText).Text = new string('я', 900); });
        var parsed = ReceiptTemplate.TryParse(t.ToJson())!;
        parsed.Block(ReceiptTemplate.Items).Size.Should().Be(3);
        parsed.Block(ReceiptTemplate.Title).Align.Should().Be("auto");
        parsed.Block(ReceiptTemplate.HeaderText).Text!.Length.Should().BeLessOrEqualTo(ReceiptTemplate.MaxTextChars);
        ReceiptTemplate.TryParse("{ нет }").Should().BeNull();
        ReceiptTemplate.TryParse(new string('x', ReceiptTemplate.MaxJsonBytes + 1)).Should().BeNull("слишком большой JSON не принимается");
        ReceiptTemplate.TryParse("""{"Blocks":[{"Id":"items"},{"Id":"nonsense"}]}""")!.Blocks.Select(b => b.Id).Should().NotContain("nonsense").And.Contain(ReceiptTemplate.DefaultOrder);
    }

    [Fact]
    public void Template_DisableBlock_ReorderBlock_AndCustomTexts_AreApplied()
    {
        var t = Custom(x =>
        {
            x.Block(ReceiptTemplate.Store).On = false;
            x.Block(ReceiptTemplate.HeaderText).Text = "Работаем с 9 до 21";
            x.Block(ReceiptTemplate.HeaderText).On = true;
            x.Block(ReceiptTemplate.FooterText).Text = "Обмен в течение 14 дней";
            x.Block(ReceiptTemplate.FooterText).On = true;
            // «Итого» — выше «Товаров».
            var total = x.Blocks.First(b => b.Id == ReceiptTemplate.Total);
            x.Blocks.Remove(total);
            x.Blocks.Insert(x.Blocks.FindIndex(b => b.Id == ReceiptTemplate.Items), total);
        });
        var rendered = ReceiptSample.Render(t, 58);
        var all = string.Join("\n", rendered.Lines.Select(l => l.Text));
        all.Should().NotContain("ТЕСТ МАРКЕТ", "блок «Магазин» выключен");
        all.Should().Contain("РАБОТАЕМ С 9 ДО 21").And.Contain("ОБМЕН В ТЕЧЕНИЕ 14 ДНЕЙ");
        all.IndexOf("ИТОГО", StringComparison.Ordinal).Should().BeLessThan(all.IndexOf("ХЛЕБ БЕЛЫЙ", StringComparison.Ordinal), "порядок блоков изменён");
        rendered.Lines.Should().OnlyContain(l => l.Text.Length <= rendered.Width);
    }

    [Fact]
    public void Template_BoldAndSize_ReachEscPosBytes_AndTextHasNoServiceChars()
    {
        var t = Custom(x => { var b = x.Block(ReceiptTemplate.Total); b.Bold = true; b.Size = 2; b.Align = "center"; });
        var text = ReceiptSample.BuildText(t, 58);
        ReceiptLineStyle.IsLaidOut(text).Should().BeTrue();

        var cfg = new ReceiptPrinterSettings { Enabled = true, DevicePath = "COM1", TextEncoding = "wpc1251" };
        var payload = EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, text, 32);
        // ESC E 1 (жирный) и GS ! (размер) есть; служебных знаков (U+2060..U+2064, в cp1251 их нет) в ленте нет.
        payload.Should().ContainInOrder(new byte[] { 0x1B, 0x45, 0x01 });
        var baseHeight = Math.Clamp((int)Math.Round(_p.GraphicFontSize / 5.0), 1, 5);
        var tall = (byte)((Math.Clamp(baseHeight * 2, 2, 8) - 1) << 4);
        payload.Should().ContainInOrder(new byte[] { 0x1D, 0x21, tall }, "двойная высота у блока «Итого» — вдвое выше обычного текста");
        Encoding.GetEncoding(1251).GetString(payload).Should().NotContain("⁠").And.NotContain("⁡").And.NotContain("⁢");
        // Для вывода без стилей (PDF/экран) служебные знаки убираются целиком.
        ReceiptLineStyle.Strip(text).Should().NotContain("⁠").And.NotContain("⁤");
    }

    [Fact]
    public void Preview_And_Printer_UseSamePipeline_SameLines()
    {
        var t = Custom(x => { x.Block(ReceiptTemplate.Meta).Align = "center"; x.Block(ReceiptTemplate.FooterText).On = true; x.Block(ReceiptTemplate.FooterText).Text = "Спасибо"; });
        foreach (var mm in new[] { 58, 80 })
        {
            var rendered = ReceiptSample.Render(t, mm);
            var text = ReceiptSample.BuildText(t, mm);
            var cfg = new ReceiptPrinterSettings { Enabled = true, DevicePath = "COM1", TextEncoding = "utf8-pos" };
            var printed = Encoding.UTF8.GetString(EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, text, rendered.Width));
            foreach (var line in rendered.Lines.Where(l => l.Text.Trim().Length > 0))
                printed.Should().Contain(line.Text.TrimEnd(), $"строка предпросмотра «{line.Text}» есть на бумаге ({mm} мм)");
        }
    }

    [Fact]
    public void Template_CentersAndRightAlignsAutoLines_WithinWidth()
    {
        var t = Custom(x => x.Block(ReceiptTemplate.Meta).Align = "right");
        var rendered = ReceiptSample.Render(t, 58);
        var cashier = rendered.Lines.First(l => l.Text.Contains("КАССИР", StringComparison.Ordinal));
        cashier.Text.Should().StartWith(" ", "выравнивание вправо сдвигает строку");
        cashier.Text.Length.Should().BeLessOrEqualTo(rendered.Width);
    }

    [Fact]
    public void Template_Size3_RelaysColumnsInHalfWidth()
    {
        var t = Custom(x => x.Block(ReceiptTemplate.Total).Size = 3);
        var rendered = ReceiptSample.Render(t, 58);
        rendered.Lines.Where(l => l.Size == 3).Should().NotBeEmpty();
        rendered.Lines.Where(l => l.Size == 3).Should().OnlyContain(l => l.Text.Length <= rendered.Width / 2 + 1, "двойная ширина — вдвое меньше знаков");
    }

    // ───────────── фискальная печать не затронута ─────────────

    [Fact]
    public void FiscalPayload_Unchanged_QrCommandStillWritten_NoTemplateEffect()
    {
        _p.ReceiptTemplateJson = Custom(x => { x.Block(ReceiptTemplate.Footer).On = false; x.Block(ReceiptTemplate.Title).Bold = true; }).ToJson();
        var cfg = new ReceiptPrinterSettings { Enabled = true, DevicePath = "COM1", TextEncoding = "wpc1251" };
        var payload = EscPosTextReceiptPrinter.BuildFiscalPayload(cfg, "ККМ ФМ 123456\nФД 0001\nРН 0000\nQR\thttps://ekassa.example/check?id=1\nКонец");
        payload.Should().ContainInOrder(new byte[] { 0x1D, 0x28, 0x6B }); // родной QR принтера
        Encoding.GetEncoding(1251).GetString(payload).Should().Contain("ККМ ФМ 123456").And.Contain("ФД 0001").And.Contain("РН 0000");
    }
}
