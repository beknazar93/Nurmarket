using FluentAssertions;
using NurMarketKassa.Services.InvoiceImport;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Invoice;

/// <summary>2026-11-05: сопоставление названий накладной с товарами склада. Расстояние Левенштейна — из Raffinert.FuzzySharp, запасной транслит —
/// AnyAscii; правила «числа и объём должны совпасть», сокращения и порог «предложить» — свои (InvoiceNameParser / InvoiceCatalogIndex).
/// Тесты чистые: не трогают базу, сеть и настройки кассы.</summary>
public sealed class InvoiceNameMatchingTests
{
    private static bool Close(string a, string b)
    {
        var x = InvoiceNameParser.Parse(a).Words;
        var y = InvoiceNameParser.Parse(b).Words;
        return x.Length == 1 && y.Length == 1 && InvoiceNameParser.WordsClose(x[0], y[0]);
    }

    [Theory]
    [InlineData("Мол. Простоквашино 1л", "Молоко Простоквашино 1 литр")]
    [InlineData("Coca-Cola 0.5л", "Кока-кола 0,5л")]
    [InlineData("ПЕЧЕНЬЕ ЮБИЛЕЙНОЕ 200Г", "Печенье юбилейное 200 г")]
    [InlineData("Шок. Аленка 100г", "Шоколад Аленка 0,1 кг")]
    public void NameScore_SameProductDifferentWriting_IsHigh(string invoice, string product)
    {
        InvoiceCatalogIndex.NameScore(invoice, product).Should().BeGreaterThanOrEqualTo(InvoiceCatalogIndex.FuzzyOffer);
    }

    [Theory]
    [InlineData("Молоко Простоквашино 1л", "Молоко Простоквашино 2л")]
    [InlineData("Samsung Galaxy A15 128GB", "Samsung Galaxy A16 128GB")]
    [InlineData("Масло 180г", "Масло 200г")]
    public void NameScore_DifferentNumberOrVolume_IsZero(string invoice, string product)
    {
        InvoiceCatalogIndex.NameScore(invoice, product).Should().Be(0);
    }

    [Theory]
    [InlineData("шоколад", "шоколат")]       // одна замена
    [InlineData("простоквашино", "простоквашина")]
    [InlineData("сметана", "сметанаа")]      // одна вставка
    public void WordsClose_OneTypoInLongWord_IsClose(string a, string b) => Close(a, b).Should().BeTrue();

    [Theory]
    [InlineData("картошка", "картофель")]   // три правки
    [InlineData("молоко", "масло")]         // разные слова
    [InlineData("мука", "мока")]            // одна замена, но слово короче 5 букв
    public void WordsClose_DifferentWords_IsNotClose(string a, string b) => Close(a, b).Should().BeFalse();

    [Theory]
    [InlineData("кең", "ken")]               // ң → n (таблица кассы)
    [InlineData("өзгөчө", "ozgocho")]        // ө → o, «ч» → ch
    [InlineData("үй", "ui")]                 // ү → u, й → i
    public void Phonetic_KyrgyzLetters_MatchLatinSpelling(string cyrillic, string latin)
    {
        InvoiceNameData.Phonetic(cyrillic).Should().Be(InvoiceNameData.Phonetic(latin));
    }

    [Theory]
    [InlineData("ўзбек", "uzbek")]           // ў — узбекская, нет в таблице кассы: запасной транслит AnyAscii
    [InlineData("ҳалол", "halol")]           // ҳ
    [InlineData("ışık", "isik")]             // турецкие ı, ş
    [InlineData("çay", "cay")]               // ç → c
    public void Phonetic_LettersOutsideTable_UseAnyAsciiFallback(string foreign, string ascii)
    {
        InvoiceNameData.Phonetic(foreign).Should().Be(InvoiceNameData.Phonetic(ascii));
    }

    [Fact]
    public void Phonetic_PlainRussianAndLatin_Unchanged()
    {
        InvoiceNameData.Phonetic("Кока").Should().Be("koka");
        InvoiceNameData.Phonetic("Coca").Should().Be("koka");
        InvoiceNameData.Phonetic("Сникерс").Should().Be(InvoiceNameData.Phonetic("Snickers"));
    }

    [Fact]
    public void Match_AbbreviatedInvoiceName_FindsRightProductAndAsksConfirm()
    {
        var milk1 = CartTestHelper.CreateProduct("p1", "Молоко Простоквашино 1 литр", 95);
        var milk2 = CartTestHelper.CreateProduct("p2", "Молоко Простоквашино 2 литра", 180);
        var cola = CartTestHelper.CreateProduct("p3", "Кока-кола 0,5 л", 70);
        var index = new InvoiceCatalogIndex([milk1, milk2, cola]);

        var match = index.Match(new InvoiceItem { Name = "Мол. Простоквашино 1л" });

        match.Kind.Should().Be(InvoiceMatchKind.Fuzzy);
        match.Tile.Should().BeSameAs(milk1);
        match.NeedsConfirm.Should().BeTrue();
    }

    [Fact]
    public void Match_Typo_StillFindsProduct()
    {
        var choc = CartTestHelper.CreateProduct("p1", "Шоколад Аленка 100 г", 120);
        var other = CartTestHelper.CreateProduct("p2", "Шоколад Милка 100 г", 130);
        var index = new InvoiceCatalogIndex([choc, other]);

        var match = index.Match(new InvoiceItem { Name = "Шоколат Аленка 100г" });

        match.Tile.Should().BeSameAs(choc);
    }

    [Fact]
    public void Match_OtherVolume_NotOffered()
    {
        var milk = CartTestHelper.CreateProduct("p1", "Молоко Простоквашино 1 литр", 95);
        var index = new InvoiceCatalogIndex([milk]);

        var match = index.Match(new InvoiceItem { Name = "Молоко Простоквашино 3л" });

        match.Kind.Should().NotBe(InvoiceMatchKind.Fuzzy);
        match.Tile.Should().BeNull();
    }

    [Fact]
    public void Match_ForeignLettersInCatalogName_MatchesAsciiInvoice()
    {
        // В каталоге узбекское написание с «ў», в накладной — латиницей: звуковые ключи слов совпадают через запасной транслит.
        var tea = CartTestHelper.CreateProduct("p1", "Ўзбек чой 100 г", 40);
        var index = new InvoiceCatalogIndex([tea]);

        var match = index.Match(new InvoiceItem { Name = "Uzbek choy 100g" });

        match.Tile.Should().BeSameAs(tea);
    }

    [Fact]
    public void Similarity_WrapperKeepsOldContract()
    {
        InvoiceCatalogIndex.Similarity([], ["молоко"]).Should().Be(0);
        InvoiceCatalogIndex.Similarity(["молоко", "1", "л"], ["молоко", "1", "литр"]).Should().BeGreaterThan(0.7);
        InvoiceCatalogIndex.Similarity(["молоко", "1", "л"], ["молоко", "2", "л"]).Should().Be(0);
    }
}
