using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>Регрессионные тесты для VoiceCommandParser.ExtractQuantity/DefaultVoiceCommandParser
/// (2026-09-04) — добавлены после трёх живых багов подряд (штук/пачки не различались,
/// "поштучно" и "целая пачка" ломали поиск товара, т.к. полностью текстовый парсер без единого
/// теста регрессировал незаметно). Все примеры — реальные фразы, которые кассир произносил
/// вживую и которые не сработали до соответствующего исправления.</summary>
public sealed class VoiceCommandParserTests
{
    [Theory]
    [InlineData("ессе манго 20 штук", "ессе манго", 20, VoiceUnitKind.Piece)]
    [InlineData("ессе манго 2 пачки", "ессе манго", 2, VoiceUnitKind.Pack)]
    [InlineData("ессе манго поштучно 3", "ессе манго", 3, VoiceUnitKind.Piece)]
    [InlineData("ессе манго целая пачка", "ессе манго", 1, VoiceUnitKind.Pack)]
    [InlineData("ессе манго целую пачку", "ессе манго", 1, VoiceUnitKind.Pack)]
    [InlineData("ессе манго пачка", "ессе манго", 1, VoiceUnitKind.Pack)]
    [InlineData("ессе манго 2", "ессе манго", 2, VoiceUnitKind.None)]
    [InlineData("картошка два килограмма", "картошка", 2, VoiceUnitKind.None)]
    public void ExtractQuantity_ParsesUnitKindAndLeavesCleanProductQuery(
        string command, string expectedQuery, double expectedQuantity, VoiceUnitKind expectedKind)
    {
        var (query, quantity, kind) = VoiceCommandParser.ExtractQuantity(command);

        query.Should().Be(expectedQuery);
        quantity.Should().Be(expectedQuantity);
        kind.Should().Be(expectedKind);
    }

    [Fact]
    public void Parse_PoshtuchnoDoesNotPolluteProductSearch()
    {
        var parser = new DefaultVoiceCommandParser();

        var command = parser.Parse("ессе манго поштучно 3");

        command.Intent.Should().Be(VoiceIntent.AddProduct);
        command.ProductText.Should().Be("ессе манго");
        command.Quantity.Should().Be(3);
        command.UnitKind.Should().Be(VoiceUnitKind.Piece);
    }

    [Fact]
    public void Parse_TselayaPachkaDoesNotPolluteProductSearch()
    {
        var parser = new DefaultVoiceCommandParser();

        var command = parser.Parse("ессе манго целая пачка");

        command.Intent.Should().Be(VoiceIntent.AddProduct);
        command.ProductText.Should().Be("ессе манго");
        command.UnitKind.Should().Be(VoiceUnitKind.Pack);
    }

    [Fact]
    public void Parse_TselayaAloneIsNotStrippedAsPackWord()
    {
        // "целая" сама по себе НЕ должна вырезаться как unit-слово — иначе реальные товары вроде
        // "Целая курица" не находились бы по голосу.
        var parser = new DefaultVoiceCommandParser();

        var command = parser.Parse("целая курица");

        command.ProductText.Should().Be("целая курица");
        command.UnitKind.Should().Be(VoiceUnitKind.None);
    }

    [Theory]
    [InlineData("поштучно 3", VoiceUnitKind.Piece, 3)]
    [InlineData("пачка", VoiceUnitKind.Pack, 1)]
    [InlineData("целая пачка два", VoiceUnitKind.Pack, 2)]
    public void Parse_BareUnitAnswerWithoutProductNameIsAddProductNotUnknown(
        string command, VoiceUnitKind expectedKind, double expectedQuantity)
    {
        // "касса пачка"/"касса поштучно 3" без названия товара — не мусор, а вероятный голосовой
        // ответ на уже открытый PackageChoiceDialog (2026-09-05: раньше падало в Unknown, потому
        // что ExtractQuantity возвращал пустой productQuery, а Parse считал это "нечего
        // разобрать" не глядя на unitKind). PackageChoiceDialog сам слушает именно такие ответы.
        var parser = new DefaultVoiceCommandParser();

        var result = parser.Parse(command);

        result.Intent.Should().Be(VoiceIntent.AddProduct);
        result.ProductText.Should().BeEmpty();
        result.Quantity.Should().Be(expectedQuantity);
        result.UnitKind.Should().Be(expectedKind);
    }

    [Fact]
    public void Parse_TrulyEmptyOrMeaninglessCommandStaysUnknown()
    {
        var parser = new DefaultVoiceCommandParser();

        parser.Parse("").Intent.Should().Be(VoiceIntent.Unknown);
    }

    [Theory]
    [InlineData("алма ики дана", "алма", 2, VoiceUnitKind.Piece)]
    [InlineData("алма эки даана", "алма", 2, VoiceUnitKind.Piece)]
    public void ExtractQuantity_RecognizesCommonVoskMishearingsOfKyrgyzWords(
        string command, string expectedQuery, double expectedQuantity, VoiceUnitKind expectedKind)
    {
        // 2026-09-05, воспроизведено по логу: пока язык кассы — русский, активна русская модель
        // Vosk, которая не обучена на кыргызских звуках и нередко слышит "эки" как "ики"
        // (подтверждено: "касса алма ики дана" реально не сработало у пользователя). "ики"/"дана"
        // добавлены как защитные альтернативные написания — не исправляет распознавание речи
        // саму по себе, только эти конкретные наблюдённые ошибки.
        var (query, quantity, kind) = VoiceCommandParser.ExtractQuantity(command);

        query.Should().Be(expectedQuery);
        quantity.Should().Be(expectedQuantity);
        kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("оплата")]
    [InlineData("оплатить")]
    [InlineData("оплатить чек")]
    [InlineData("төлө")]
    [InlineData("төлөө")]
    public void Parse_PayPhrasesReturnPayIntent(string command)
    {
        var parser = new DefaultVoiceCommandParser();

        parser.Parse(command).Intent.Should().Be(VoiceIntent.Pay);
    }
}
