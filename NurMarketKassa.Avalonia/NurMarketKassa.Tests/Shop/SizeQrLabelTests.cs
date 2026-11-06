using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06 (просьба клиента магазина одежды: «для размеров нужна кнопка генерации QR»): коды «Создать QR» уникальны и не весовые,
/// формат QR попадает в сцену этикетки, слишком мелкий QR замечается, скан QR с пробелами и переводом строки находит именно этот размер.</summary>
public sealed class SizeQrLabelTests
{
    private const string Code = "4870145004807";

    [Fact]
    public void QrCodes_ForSizesWithoutCode_AreUniqueNotWeightAndNotTaken()
    {
        // существующие коды товаров и размеров: новые не должны с ними совпасть (как GenerateBarcodes(qr: true) в окне «Размеры и цвета»)
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Code, "2000000000008", "2912345678903" };
        var sizeIndexTaken = new HashSet<string>(StringComparer.Ordinal) { "5901234123457" };
        var made = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var code = BarcodeGenerator.Generate(taken, c => sizeIndexTaken.Contains(c));
            taken.Add(code);
            made.Add(code);
        }

        made.Should().OnlyHaveUniqueItems();
        made.Should().NotContain(new[] { Code, "2000000000008", "2912345678903", "5901234123457" });
        foreach (var code in made)
        {
            code.Should().HaveLength(13);
            BarcodeGenerator.HasValidCheckDigit(code).Should().BeTrue();
            code[0].Should().NotBe('0').And.NotBe('2'); // 20–29 — весовые этикетки
            WeightBarcodeParser.TryParse(code, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void QrMode_PutsQrIntoLabelScene_AndDoesNotChangeTemplate()
    {
        var template = LabelTemplate.CreateDefault(); // 40×30, формат Auto — линейный код
        var qrTemplate = LabelCodeModes.Apply(template, LabelCodeMode.Qr);
        template.BarcodeFormat.Should().Be(LabelBarcodeFormat.Auto, "шаблон на диске не меняется");
        qrTemplate.BarcodeFormat.Should().Be(LabelBarcodeFormat.QrCode);

        var linear = BarcodeLabelService.BuildLabelScene("Платье", Code, "1500,00 сом", template, variantText: "44, Красный")
            .Items.OfType<LabelSceneBars>().Single();
        linear.Cells.GetLength(1).Should().Be(1, "штрихкод — одна строка модулей");

        var scene = BarcodeLabelService.BuildLabelScene("Платье", Code, "1500,00 сом", qrTemplate, variantText: "44, Красный");
        var qr = scene.Items.OfType<LabelSceneBars>().Single();
        qr.Cells.GetLength(0).Should().Be(qr.Cells.GetLength(1)).And.BeGreaterThanOrEqualTo(21, "QR — квадратная матрица");
        scene.Items.OfType<LabelSceneText>().Select(t => t.Text).Should().Contain(t => t.Contains("Платье") && t.Contains("44, Красный"));
        scene.Items.OfType<LabelSceneText>().Select(t => t.Text).Should().Contain(t => t.Contains("1500"));
    }

    [Fact]
    public void BarcodeMode_TurnsTemplateQrBackToLinear_AsTemplateKeepsIt()
    {
        var template = LabelTemplate.CreateDefault();
        template.BarcodeFormat = LabelBarcodeFormat.QrCode;
        LabelCodeModes.Apply(template, LabelCodeMode.Barcode).BarcodeFormat.Should().Be(LabelBarcodeFormat.Auto);
        LabelCodeModes.Apply(template, LabelCodeMode.AsTemplate).BarcodeFormat.Should().Be(LabelBarcodeFormat.QrCode);
    }

    [Fact]
    public void Qr_OnNormalLabel_IsReadable_OnTinyBox_Warns()
    {
        var normal = LabelTemplate.CreateDefault(); // блок кода 36×16 мм
        LabelCodeModes.QrModuleDots(normal, Code).Should().BeGreaterThanOrEqualTo(LabelCodeModes.MinQrModuleDots);
        LabelCodeModes.QrSizeProblem(normal, Code).Should().BeNull();

        var tiny = LabelTemplate.CreateDefault();
        tiny.WidthMm = 30;
        tiny.HeightMm = 20;
        tiny.Barcode.WidthMm = 8;
        tiny.Barcode.HeightMm = 5; // 5 мм на 25 модулей — меньше 3 точек на модуль
        LabelCodeModes.QrSizeProblem(tiny, Code).Should().NotBeNullOrWhiteSpace().And.Contain("QR");
        // блок кода выключен — предупреждать не о чем
        tiny.Barcode.Enabled = false;
        LabelCodeModes.QrSizeProblem(tiny, Code).Should().BeNull();
    }

    [Theory]
    [InlineData("4870145004807")]
    [InlineData("  4870145004807\r\n")]
    [InlineData("\r\n4870145004807\t ")]
    [InlineData("4870 1450 04807")]
    public void ScannedQrText_IsTrimmed_ToTheSizeCode(string scanned)
    {
        ProductCodeLookup.Normalize(scanned).Should().Be(Code);
    }

    [Fact]
    public void ScannedQrText_WithEdgeSpaces_FindsExactlyThatSize()
    {
        const string productId = "test-size-qr-product";
        try
        {
            VariantBarcodeIndex.Update(productId, new List<ProductVariantDto>
            {
                new() { Id = "v-44", Size = "44", Color = "Красный", Barcode = "7312345678908", Quantity = 3, IsActive = true },
                new() { Id = "v-46", Size = "46", Color = "Красный", Barcode = "7312345678915", Quantity = 2, IsActive = true },
            });

            var hit = VariantBarcodeIndex.Find(ProductCodeLookup.Normalize("\r\n 7312345678915 \r\n"));
            hit.Should().NotBeNull();
            hit!.ProductId.Should().Be(productId);
            hit.Variant.Id.Should().Be("v-46");
            hit.Variant.Size.Should().Be("46");
            // сам индекс тоже обрезает края (прямой вызов без Normalize)
            VariantBarcodeIndex.Find("  7312345678908\r\n")!.Variant.Id.Should().Be("v-44");
        }
        finally
        {
            VariantBarcodeIndex.Update(productId, new List<ProductVariantDto>());
        }
    }
}
