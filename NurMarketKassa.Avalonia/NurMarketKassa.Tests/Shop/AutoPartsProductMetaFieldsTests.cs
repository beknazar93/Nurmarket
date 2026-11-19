using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (склейка карточки товара «Автозапчасти» с чеком/возвратом): поля «Гарантия, дней», «Электрика», «Установленные не принимаются» —
/// разбор числа из поля и запись/чтение через AutoPartsProductMeta. Файл — во временной папке.</summary>
[Collection("UserPreferences")]
public sealed class AutoPartsProductMetaFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "autoparts-meta-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsProductMetaFieldsTests()
    {
        Directory.CreateDirectory(_dir);
        AutoPartsProductMeta.FileOverride = Path.Combine(_dir, "autoparts-products.json");
    }

    public void Dispose()
    {
        AutoPartsProductMeta.FileOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 90 ", 90)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("-5", null)]
    [InlineData("9999", 3650)]
    public void ParseDays_reads_the_card_field(string? text, int? expected) =>
        AutoPartsProductMeta.ParseDays(text).Should().Be(expected);

    [Fact]
    public void Card_fields_roundtrip_through_Set_and_Get()
    {
        AutoPartsProductMeta.Get("p1").Should().BeNull("без записи — решает название");
        AutoPartsProductMeta.Set("p1", AutoPartsProductMeta.ParseDays("45"), electric: true, noReturn: true);
        var m = AutoPartsProductMeta.Get("p1")!;
        m.WarrantyDays.Should().Be(45);
        m.Electric.Should().BeTrue();
        m.NoReturn.Should().BeTrue();

        // Сняли всё и очистили гарантию: запись остаётся (явное «можно»), гарантии нет.
        AutoPartsProductMeta.Set("p1", AutoPartsProductMeta.ParseDays(""), electric: false, noReturn: false);
        var cleared = AutoPartsProductMeta.Get("p1")!;
        cleared.WarrantyDays.Should().BeNull();
        cleared.Electric.Should().BeFalse();
        cleared.NoReturn.Should().BeFalse();
        AutoPartsProductMeta.Get("p2").Should().BeNull("другой товар не затронут");
    }
}
