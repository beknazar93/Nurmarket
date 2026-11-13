using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (Rongta: этикетки без штрихкода при сумме ≥ 1000, в карточке товара на весах «Barcode type 000»): экспериментальное поле
/// madv4 записи «!0V». 2026-11-13: на весах владельца madv4 = 07 на тип штрихкода карточки не повлиял — настройка в кассе убрана, остался параметр протокола
/// (всегда 0 из кассы). Тесты держат: по умолчанию запись байт в байт как раньше, параметр пишется только в свою позицию.</summary>
[Collection("UserPreferences")]
public sealed class RongtaCardBarcodeTypeTests : IDisposable
{
    // «!0V» + PLU(4) + «A» = 8; затем mcode 7, mprice 6, mweigh_mode 1, madv1..3 по 2, mshelflife 3, madv5 2 → madv4 с позиции 33.
    private const int Madv4Offset = 8 + 7 + 6 + 1 + 2 + 2 + 2 + 3 + 2;

    public RongtaCardBarcodeTypeTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.AmountUnit = "tiyin";
        WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
        WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
        WeightBarcodeParser.CustomLayouts = Array.Empty<WeightBarcodeCustomLayout>();
    }

    private static DahuaTmPlu Plu(int cardType) => new()
    {
        PluNumber = 5,
        ProductCode = 5,
        Price = 1000m,
        ShelfLifeDays = 3,
        BarcodePrefix = 20,
        Name = "Мясо Барана (мякоть)",
        CardBarcodeType = cardType,
    };

    [Fact]
    public void Default_IsZero_SameBytesAsBefore()
    {
        new DahuaTmPlu().CardBarcodeType.Should().Be(0);
        var line = DahuaTmProtocol.BuildPluCommand(Plu(0), 2, codec: DahuaTmNameCodec.Rongta);

        line.Substring(Madv4Offset, 2).Should().Be("00");
        line.Substring(Madv4Offset - 2, 2).Should().Be("20", "перед madv4 — madv5, префикс (отдел) штрихкода");
    }

    [Fact]
    public void Type07_WrittenIntoMadv4_EverythingElseUnchanged()
    {
        var before = DahuaTmProtocol.BuildPluCommand(Plu(0), 2, codec: DahuaTmNameCodec.Rongta);
        var after = DahuaTmProtocol.BuildPluCommand(Plu(7), 2, codec: DahuaTmNameCodec.Rongta);

        after.Substring(Madv4Offset, 2).Should().Be("07");
        after.Length.Should().Be(before.Length);
        (after[..Madv4Offset] + after[(Madv4Offset + 2)..]).Should().Be(before[..Madv4Offset] + before[(Madv4Offset + 2)..]);
        after.Substring(8, 7).Should().Be("0000005", "код товара");
        after.Substring(15, 6).Should().Be("100000", "цена 1000,00 при 2 знаках");
    }

    [Fact]
    public void ReservedAdv_StillWinsOverCardType_ForExeReferenceLines()
    {
        var plu = Plu(7) with { ReservedAdv = new[] { 3 } };

        DahuaTmProtocol.BuildPluCommand(plu, 2, codec: DahuaTmNameCodec.Rongta).Substring(Madv4Offset, 2).Should().Be("03");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void OutOfRange_IsRejected(int cardType) =>
        DahuaTmProtocol.Validate(Plu(cardType), 2).Should().Be(DahuaTmPluProblem.BadSpecialInfo);

    [Fact]
    public void ExeDemoLines_StillMatch()
    {
        // Эталон «PLU Demo» №1 из mscale.exe: madv4 = 00 — сборщик с CardBarcodeType по умолчанию даёт ту же строку.
        var demo = new DahuaTmPlu { PluNumber = 1, ProductCode = 228100, Price = 11m, ShelfLifeDays = 8, BarcodePrefix = 3, Name = "test1" };
        var line = DahuaTmProtocol.BuildPluCommand(demo, 0, nameTerminator: false);

        line.TrimEnd('\r', '\n').Should().Be(DahuaTmProtocol.ExeDemoLines[0]);
    }

    [Fact]
    public void Type07Label_KassaReadsWeight_AndAmountAbove1000()
    {
        // Этикетка типа 07 для товара №5: 1,900 кг — касса читает вес и сама считает 1900,00 по цене 1000,00 (сумма в штрихкоде не нужна).
        var sample = RongtaBarcodeFormat.BuildSample(RongtaBarcodeFormat.Find(7)!, 20, 5, 1900, 1900m)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.ProductCode.Should().Be("00005");
        (parsed.Value * 1000.0).Should().BeApproximately(1900.0, 0.001);
    }
}
