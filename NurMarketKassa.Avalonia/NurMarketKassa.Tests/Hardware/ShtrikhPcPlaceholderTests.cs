using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06 (жалоба владельца: после выгрузки главный экран весов пуст): плитка-заглушка для товара без фото (BMP 288×288), строки «&lt;N» кнопок быстрого доступа
/// (весы показывают на главном экране кнопки 2001…, а не все товары), чтение двух раскладок этикеток одновременно (27 «Код-Вес» и 20 «Код-Стоимость»).
/// Парсер статический — коллекция «UserPreferences».</summary>
[Collection("UserPreferences")]
public sealed class ShtrikhPcPlaceholderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pcph-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _custom = WeightBarcodeParser.CustomLayouts;
    private readonly string _layout = WeightBarcodeParser.Layout;
    private readonly string _mode = WeightBarcodeParser.Mode;

    public ShtrikhPcPlaceholderTests()
    {
        Directory.CreateDirectory(_root);
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.CustomLayouts = Array.Empty<WeightBarcodeCustomLayout>();
    }

    public void Dispose()
    {
        WeightBarcodeParser.CustomLayouts = _custom;
        WeightBarcodeParser.Layout = _layout;
        WeightBarcodeParser.Mode = _mode;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ShtrikhPcSettings Settings()
    {
        var folder = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(folder);
        return new ShtrikhPcSettings { Folder = folder, QuickKeys = false }.Normalize();
    }

    private ShtrikhPcPictureState State() => new(Path.Combine(_root, "state.json"));

    private static ShtrikhPcItem Item(long code, string name = "Яблоки Гала", decimal price = 100m, string? source = null) =>
        new(code, name, price, false, PictureSource: source);

    private static ShtrikhPcPlaceholder Tile(bool showPrice = false) =>
        new(item => ShtrikhPcPlaceholderRenderer.Render(item.Name, item.Code, showPrice ? item.Price.ToString("0.00") : null), showPrice);

    private static Task<byte[]?> Photo(ShtrikhPcItem _, CancellationToken __) => Task.FromResult<byte[]?>(new byte[ShtrikhPcBmp.HeaderSize + 4]);

    private static (byte B, byte G, byte R) PixelAt(byte[] bmp, int x, int y, int size = 288)
    {
        var stride = (size * 3 + 3) & ~3;
        var o = 54 + (size - 1 - y) * stride + x * 3;
        return (bmp[o], bmp[o + 1], bmp[o + 2]);
    }

    // ---------------------------------------------------------------- перенос слов и цвета (без Skia)

    // «ширина» строки — число знаков × 10: проверяем перенос независимо от шрифта
    private static float Width(string s) => s.Length * 10f;

    [Fact]
    public void WrapWords_BreaksOnSpaces_NotInsideWords()
    {
        var lines = ShtrikhPcTile.WrapWords("Мясо говядина охлаждённое высший сорт", Width, 150, 5);

        lines.Should().Equal("Мясо говядина", "охлаждённое", "высший сорт");
        lines.Should().OnlyContain(l => Width(l) <= 150);
    }

    [Fact]
    public void WrapWords_LongWordIsCutWithEllipsis()
    {
        var lines = ShtrikhPcTile.WrapWords("Достопримечательность", Width, 100, 5);

        lines.Should().ContainSingle();
        lines[0].Should().EndWith("…");
        Width(lines[0]).Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void WrapWords_MoreThanMaxLines_EndsWithEllipsis()
    {
        var lines = ShtrikhPcTile.WrapWords("один два три четыре пять шесть семь восемь девять десять", Width, 60, 3);

        lines.Should().HaveCount(3);
        lines[^1].Should().EndWith("…");
        lines.Should().OnlyContain(l => Width(l) <= 60);
    }

    [Fact]
    public void WrapWords_EmptyText_NoLines() =>
        ShtrikhPcTile.WrapWords("   ", Width, 100, 5).Should().BeEmpty();

    [Fact]
    public void BackgroundColor_IsSoftStable_AndDarkTextIsReadable()
    {
        var a = ShtrikhPcTile.BackgroundColor("Яблоки Гала", 5);

        a.Should().Be(ShtrikhPcTile.BackgroundColor("Яблоки Гала", 5), "цвет зависит только от названия и кода");
        ShtrikhPcTile.BackgroundColor("Сыр Гауда", 6).Should().NotBe(a);
        foreach (var (name, code) in new[] { ("Яблоки", 1L), ("Өрүк кургатылган", 2L), ("Сыр", 3L), ("Говядина", 4L), ("Лук", 5L), ("Картофель", 6L) })
        {
            var (r, g, b) = ShtrikhPcTile.BackgroundColor(name, code);
            Math.Min(r, Math.Min(g, b)).Should().BeGreaterThan(130, "пастельный светлый фон");
            ShtrikhPcTile.UseDarkText(r, g, b).Should().BeTrue("на светлом фоне читается чёрный текст");
        }
        ShtrikhPcTile.UseDarkText(0, 0, 0).Should().BeFalse("на тёмном фоне — белый");
    }

    // ---------------------------------------------------------------- Skia: сама плитка

    [Theory]
    [InlineData("Мясо говядина охлаждённое высшего сорта в вакуумной упаковке для жарки и тушения, 1 кг")]
    [InlineData("Өрүк кургатылган жана ийне жалбырактуу мөмө-жемиштердин аралашмасы, ңүү")]
    [InlineData("Я")]
    public void Render_LongRussianOrKyrgyzName_Gives288x288Bmp_WithBackgroundCornerAndText(string name)
    {
        var bmp = ShtrikhPcPlaceholderRenderer.Render(name, 12345, "650.00");

        bmp.Should().NotBeNull();
        bmp!.Length.Should().Be(54 + 288 * 3 * 288);
        bmp[0].Should().Be((byte)'B');
        bmp[1].Should().Be((byte)'M');
        BitConverter.ToInt32(bmp, 18).Should().Be(288);
        BitConverter.ToInt32(bmp, 22).Should().Be(288);
        BitConverter.ToInt16(bmp, 28).Should().Be(24);

        var (r, g, b) = ShtrikhPcTile.BackgroundColor(name.Trim(), 12345);
        PixelAt(bmp, 2, 2).Should().Be((b, g, r), "угол — фоновый цвет плитки");
        PixelAt(bmp, 285, 2).Should().Be((b, g, r));

        // не пустая и не однородная: в области названия есть тёмные пиксели текста
        var distinct = new HashSet<(byte, byte, byte)>();
        var dark = 0;
        for (var y = 18; y < 240; y += 2)
        {
            for (var x = 18; x < 270; x += 2)
            {
                var p = PixelAt(bmp, x, y);
                distinct.Add(p);
                if (p.R < 110 && p.G < 110 && p.B < 110)
                    dark++;
            }
        }
        distinct.Count.Should().BeGreaterThan(5);
        dark.Should().BeGreaterThan(40, "название нарисовано тёмным текстом на светлом фоне");
    }

    [Fact]
    public void Render_PriceInFooter_ChangesThePicture()
    {
        var without = ShtrikhPcPlaceholderRenderer.Render("Сыр Гауда", 7, null)!;
        var with = ShtrikhPcPlaceholderRenderer.Render("Сыр Гауда", 7, "980.00")!;

        with.Should().NotEqual(without);
    }

    [Fact]
    public void Render_DifferentNames_GiveDifferentPictures() =>
        ShtrikhPcPlaceholderRenderer.Render("Сыр Гауда", 7, null)!.Should().NotEqual(ShtrikhPcPlaceholderRenderer.Render("Сыр Российский", 7, null)!);

    // ---------------------------------------------------------------- шаг картинок с заглушкой

    [Fact]
    public async Task Step_ProductWithoutPhoto_GetsPlaceholderTile_ResourceAndField31()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(Photo, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Tile());

        var outcome = await step(new[] { Item(123) }, settings, CancellationToken.None);

        outcome.Stats.Placeholders.Should().Be(1);
        outcome.Stats.WithPhoto.Should().Be(0);
        outcome.Stats.NoPhoto.Should().Be(1);
        outcome.Stats.Failed.Should().Be(0);
        var file = Path.Combine(settings.Folder, "Pictures", "123.bmp");
        File.Exists(file).Should().BeTrue();
        new FileInfo(file).Length.Should().Be(54 + 288 * 3 * 288);
        outcome.Items.Single().PictureResource.Should().Be(100_123);

        var text = ShtrikhPcImportWriter.Build(outcome.Items, settings).Text;
        text.Should().Contain("<R 100123;3;").And.Contain("Pictures/123.bmp");
        text.Split("\r\n").Single(l => l.StartsWith("<D 123;", StringComparison.Ordinal))["<D ".Length..].Split(';')[30].Should().Be("100123");
    }

    [Fact]
    public async Task Step_WithPhoto_UsesPhoto_NotPlaceholder()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(Photo, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Tile());

        var outcome = await step(new[] { Item(5, source: "https://x/5.jpg") }, settings, CancellationToken.None);

        outcome.Stats.WithPhoto.Should().Be(1);
        outcome.Stats.Placeholders.Should().Be(0);
        new FileInfo(Path.Combine(settings.Folder, "Pictures", "5.bmp")).Length.Should().Be(ShtrikhPcBmp.HeaderSize + 4, "записано то, что отдал loadBmp (фото), а не плитка");
    }

    [Fact]
    public async Task Step_PhotoFails_FallsBackToPlaceholder()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep((_, _) => Task.FromResult<byte[]?>(null), State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Tile());

        var outcome = await step(new[] { Item(5, source: "https://x/dead.jpg") }, settings, CancellationToken.None);

        outcome.Stats.Placeholders.Should().Be(1);
        outcome.Stats.Failed.Should().Be(0);
        outcome.Items.Single().PictureResource.Should().Be(100_005);
    }

    [Fact]
    public async Task Step_SameNameAndPrice_IsNotRedrawn_ButRenameOrPhotoReplacesTheTile()
    {
        var settings = Settings();
        var state = State();
        var drawn = 0;
        var placeholder = new ShtrikhPcPlaceholder(item =>
        {
            Interlocked.Increment(ref drawn);
            return ShtrikhPcPlaceholderRenderer.Render(item.Name, item.Code, null);
        }, false);
        ShtrikhPcPictureStep Step(Func<ShtrikhPcItem, CancellationToken, Task<byte[]?>>? loader = null) =>
            ShtrikhPcPictures.CreateStep(loader ?? Photo, state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: placeholder);
        var file = Path.Combine(settings.Folder, "Pictures", "9.bmp");

        await Step()(new[] { Item(9, "Сыр") }, settings, CancellationToken.None);
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var again = await Step()(new[] { Item(9, "Сыр") }, settings, CancellationToken.None);

        drawn.Should().Be(1, "название и цена не менялись — плитку не перерисовываем");
        again.Stats.Unchanged.Should().Be(1);
        again.Stats.Placeholders.Should().Be(1);
        File.GetLastWriteTimeUtc(file).Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Step()(new[] { Item(9, "Сыр Гауда") }, settings, CancellationToken.None);
        drawn.Should().Be(2, "сменилось название — плитка перерисована");

        var photo = await Step()(new[] { Item(9, "Сыр Гауда", source: "https://x/9.jpg") }, settings, CancellationToken.None);
        photo.Stats.WithPhoto.Should().Be(1);
        new FileInfo(file).Length.Should().Be(ShtrikhPcBmp.HeaderSize + 4, "фото появилось — плитка заменена фото");
        drawn.Should().Be(2);
    }

    [Fact]
    public async Task Step_PriceOnTile_RedrawsWhenPriceChanges_OnlyIfPriceIsShown()
    {
        var settings = Settings();
        var drawn = 0;
        ShtrikhPcPlaceholder Make(bool showPrice) => new(item =>
        {
            Interlocked.Increment(ref drawn);
            return ShtrikhPcPlaceholderRenderer.Render(item.Name, item.Code, showPrice ? "x" : null);
        }, showPrice);

        var state = State();
        await ShtrikhPcPictures.CreateStep(Photo, state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Make(false))(new[] { Item(1, price: 10m) }, settings, CancellationToken.None);
        await ShtrikhPcPictures.CreateStep(Photo, state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Make(false))(new[] { Item(1, price: 20m) }, settings, CancellationToken.None);
        drawn.Should().Be(1, "цена не на плитке — смена цены её не трогает");

        var state2 = State();
        await ShtrikhPcPictures.CreateStep(Photo, state2, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Make(true))(new[] { Item(2, price: 10m) }, settings, CancellationToken.None);
        await ShtrikhPcPictures.CreateStep(Photo, state2, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), placeholder: Make(true))(new[] { Item(2, price: 20m) }, settings, CancellationToken.None);
        drawn.Should().Be(3, "цена на плитке сменилась — плитка перерисована");
    }

    [Fact]
    public async Task Step_WithoutPlaceholderSetting_ProductWithoutPhoto_StaysWithoutPicture_AsBefore()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(Photo, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)); // заглушка выключена

        var outcome = await step(new[] { Item(7) }, settings, CancellationToken.None);

        outcome.Stats.NoPhoto.Should().Be(1);
        outcome.Stats.Placeholders.Should().Be(0);
        outcome.Items.Single().PictureResource.Should().Be(0);
        File.Exists(Path.Combine(settings.Folder, "Pictures", "7.bmp")).Should().BeFalse();
        ShtrikhPcImportWriter.Build(outcome.Items, settings).Text.Should().NotContain("<R ");
    }

    [Fact]
    public void PlaceholderSettings_Defaults()
    {
        var s = new ShtrikhPcSettings().Normalize();

        s.PlaceholderPictures.Should().BeTrue("заглушка включена по умолчанию");
        s.PlaceholderShowPrice.Should().BeFalse();
        s.QuickKeys.Should().BeFalse("«Быстрый доступ» — не главный экран (его строит сценарий), по умолчанию не пишется");
        s.QuickKeyCount.Should().Be(54);
    }

    // ---------------------------------------------------------------- кнопки быстрого доступа (<N 2001…)

    [Fact]
    public void QuickKeys_AssignFirstProductsToButtons_AndClearTheRest()
    {
        var settings = new ShtrikhPcSettings { QuickKeys = true, QuickKeyCount = 5 }.Normalize();
        var items = new[] { Item(123), Item(124), Item(125) };

        var build = ShtrikhPcImportWriter.Build(items, settings);
        var lines = build.Text.Split("\r\n").Where(l => l.Length > 0).ToArray();

        build.QuickKeys.Should().Be(3);
        lines.Where(l => l.StartsWith("<N ", StringComparison.Ordinal)).Should().Equal(
            "<N 2001;123", "<N 2002;124", "<N 2003;125", "<N 2004;0", "<N 2005;0");
        Array.FindLastIndex(lines, l => l.StartsWith("<D ", StringComparison.Ordinal)).Should().BeLessThan(Array.FindIndex(lines, l => l.StartsWith("<N ", StringComparison.Ordinal)));
    }

    [Fact]
    public void QuickKeys_Limit_AndSwitchOff()
    {
        var many = Enumerable.Range(1, 80).Select(i => Item(i, "Товар " + i)).ToArray();
        var settings = new ShtrikhPcSettings { QuickKeys = true }.Normalize();

        var build = ShtrikhPcImportWriter.Build(many, settings);

        build.QuickKeys.Should().Be(54);
        build.Text.Split("\r\n").Count(l => l.StartsWith("<N ", StringComparison.Ordinal)).Should().Be(54);
        build.Text.Should().Contain("<N 2054;54").And.NotContain("<N 2055;");

        var off = ShtrikhPcImportWriter.Build(many, new ShtrikhPcSettings { QuickKeys = false }.Normalize());
        off.QuickKeys.Should().Be(0);
        off.Text.Should().NotContain("<N ");
    }

    [Fact]
    public void QuickKeys_ClearCommandStaysLast_AndLineStyleIsFollowed()
    {
        var settings = new ShtrikhPcSettings { QuickKeys = true, QuickKeyCount = 2, ClearBase = true, LineStyle = ShtrikhPcSettings.LineGlued }.Normalize();

        var lines = ShtrikhPcImportWriter.Build(new[] { Item(5) }, settings).Text.Split("\r\n").Where(l => l.Length > 0).ToArray();

        lines.Last().Should().Be("$$$CLR");
        lines.Should().Contain("<N2001;5").And.Contain("<N2002;0");
    }

    // ---------------------------------------------------------------- две раскладки этикеток сразу

    private static string Ean(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (10 - sum % 10) % 10;
    }

    [Fact]
    public void BothLayouts_Prefix27CodeWeight_And_Prefix20CodeCost_AreReadAtTheSameTime()
    {
        WeightBarcodeParser.CustomLayouts = new ShtrikhPcSettings().Normalize().ToLayouts().ToArray();

        // 27 + PLU 123 + 1,250 кг (в ТЗ Умара записано как …508, верная контрольная цифра — 5)
        WeightBarcodeParser.TryParse("2700123012505", out var weighed).Should().BeTrue();
        long.Parse(weighed.ProductCode).Should().Be(123);
        weighed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        weighed.Value.Should().BeApproximately(1.250, 1e-9);

        // фото этикетки «Алма» с весов владельца: 0,165 кг × 45,00 = 7,43 → 20 + код 00002 + стоимость 00743
        WeightBarcodeParser.TryParse("2000002007432", out var cost).Should().BeTrue();
        cost.ProductCode.Should().Be("00002");
        long.Parse(cost.ProductCode).Should().Be(2);
        cost.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        cost.Value.Should().BeApproximately(7.43, 1e-9);
        cost.ResolveWeightKg(45.0).Should().BeApproximately(0.165, 0.002, "вес восстанавливается по цене 45,00/кг");
    }

    [Fact]
    public void PieceLayout_CanBeSwitchedOff_AndNeverClashesWithTheMainPrefix()
    {
        new ShtrikhPcSettings { PieceBarcodeEnabled = false }.Normalize().ToLayouts().Should().ContainSingle();
        new ShtrikhPcSettings { BarcodePrefix = 20, PiecePrefix = 20 }.Normalize().ToLayouts().Should().ContainSingle("одинаковые префиксы — штучная раскладка не добавляется");
        var both = new ShtrikhPcSettings().Normalize().ToLayouts();
        both.Select(l => l.Prefix).Should().Equal("27", "20");
        both[1].Kind.Should().Be(WeightBarcodeValueKind.Amount);
        both[1].CodeFirst.Should().BeTrue();

        new ShtrikhPcSettings { PieceFormat = ShtrikhPcSettings.FormatAmountCode }.Normalize().ToPieceLayout().CodeFirst.Should().BeFalse();
        new ShtrikhPcSettings { PieceFormat = "ерунда" }.Normalize().PieceFormat.Should().Be(ShtrikhPcSettings.FormatCodeAmount);
    }

    [Fact]
    public void Label2000002007432_ProductCode2_NotFoundCase_IsDetectedAsProductCode()
    {
        // касса получает код «00002» (PLU 2) — если такого товара нет, сообщение «Товар с кодом 00002 не найден» (BasketPanelViewModel) называет этот код
        WeightBarcodeParser.CustomLayouts = new ShtrikhPcSettings().Normalize().ToLayouts().ToArray();

        WeightBarcodeParser.TryParse("2000002007432", out var parsed).Should().BeTrue();

        parsed.ProductCode.TrimStart('0').Should().Be("2");
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit("2000002007432").Should().BeFalse();
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit("2000002007433").Should().BeTrue();
    }
}
