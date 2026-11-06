using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06: весы «Штрих РС-200С2» — файл импорта (руководство администратора, п. 6.2.1 «БД товаров», п. 8.1.3 «Структура файла импорта»):
/// заголовок «##@@&amp;&amp;» и «#», строки «&lt;D…» по 34 полям через «;», Windows-1251, CRLF, «$$$CLR» строго в конце, атомарная запись и флаг-файл.</summary>
// 2026-11-06: проверка доступности папки ждёт не дольше 3 с — на загруженной машине параллельные тесты давали ложное «папка недоступна»;
// идём в коллекции без параллельности (как очереди приходов).
[Collection("BackgroundReceivingQueue")]
public sealed class ShtrikhPcImportWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pc-" + Guid.NewGuid().ToString("N")[..8]);

    public ShtrikhPcImportWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Настройки с общей папкой весов; папка «Shared» уже есть (на весах она есть всегда), подпапку «Import» создаёт касса.</summary>
    private ShtrikhPcSettings Settings(Action<ShtrikhPcSettings>? tweak = null, bool createFolder = true)
    {
        var s = new ShtrikhPcSettings { Folder = Path.Combine(_root, "Shared") };
        if (createFolder)
            Directory.CreateDirectory(s.Folder);
        tweak?.Invoke(s);
        return s.Normalize();
    }

    private static ShtrikhPcItem Item(long code, string name = "Лук репчатый", decimal price = 55.5m, bool piece = false, int shelf = 0) =>
        new(code, name, price, piece, shelf);

    private static string[] DataLines(string text) =>
        text.Split("\r\n", StringSplitOptions.None).Skip(2).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void Build_Header_TwoLines_ThenDataLines()
    {
        var build = ShtrikhPcImportWriter.Build(new[] { Item(1) }, Settings());

        build.Text.Should().StartWith("##@@&&\r\n#\r\n");
        build.Written.Should().Be(1);
        DataLines(build.Text).Should().HaveCount(1);
    }

    [Fact]
    public void Build_Line_Has34Fields_WithValuesInRightPlaces()
    {
        var line = DataLines(ShtrikhPcImportWriter.Build(new[] { Item(69312, "Лук репчатый", 55.5m, piece: false, shelf: 14) }, Settings()).Text).Single();

        line.Should().StartWith("<D 69312;"); // как в примере руководства: «<L 1;…»
        var fields = line["<D ".Length..].Split(';');
        fields.Should().HaveCount(ShtrikhPcImportWriter.FieldCount);
        fields[0].Should().Be("69312");       // 1 код
        fields[1].Should().Be("");            // 2 штрих-код — только для поиска
        fields[2].Should().Be("Лук репчатый"); // 3 наименование
        fields[3].Should().Be("0");           // 4 тип: весовой
        fields[4].Should().Be("55,50");       // 5 цена, 2 знака, запятая
        fields[5..9].Should().OnlyContain(f => f == "0");     // 6–9 группы-владельцы
        fields[11].Should().Be("14");         // 12 срок годности
        fields[12].Should().Be("");           // 13 дата реализации
        fields[13].Should().Be("0");          // 14 формат этикетки по умолчанию
        fields[14].Should().Be("0");          // 15 блокировка
        fields[15].Should().Be("0");          // 16 разрешение менять цену
        fields[21].Should().Be("");           // 22 сертификат
        fields[22..25].Should().OnlyContain(f => f == "0");   // 23–25 масса тары
        fields[25..34].Should().OnlyContain(f => f == "0");   // 26–34 коды ресурсов
    }

    [Fact]
    public void Build_PieceItem_TypeOne()
    {
        var fields = DataLines(ShtrikhPcImportWriter.Build(new[] { Item(2, piece: true) }, Settings()).Text).Single()["<D ".Length..].Split(';');

        fields[3].Should().Be("1");
    }

    [Theory]
    [InlineData(ShtrikhPcSettings.LineSpace, "<D 7;")]
    [InlineData(ShtrikhPcSettings.LineGlued, "<D7;")]
    [InlineData(ShtrikhPcSettings.LineSemicolon, "<D;7;")]
    public void Build_LineStart_Configurable(string style, string expectedStart)
    {
        var line = DataLines(ShtrikhPcImportWriter.Build(new[] { Item(7) }, Settings(s => s.LineStyle = style)).Text).Single();

        line.Should().StartWith(expectedStart);
    }

    [Fact]
    public void Build_UsesCrLfOnly_AndNoBareLf()
    {
        var text = ShtrikhPcImportWriter.Build(new[] { Item(1), Item(2) }, Settings()).Text;

        text.Replace("\r\n", "").Should().NotContain("\n").And.NotContain("\r");
        text.Should().EndWith("\r\n");
    }

    [Fact]
    public void Encode_Cyrillic_IsWindows1251()
    {
        // «Лук»: Л = CB, у = F3, к = EA в Windows-1251.
        ShtrikhPcImportWriter.Encode("Лук").Should().Equal(0xCB, 0xF3, 0xEA);
        ShtrikhPcImportWriter.Decode(new byte[] { 0xCB, 0xF3, 0xEA }).Should().Be("Лук");
    }

    [Fact]
    public void Build_ClearBase_CommandIsLastLine_OnlyWhenEnabled()
    {
        var off = ShtrikhPcImportWriter.Build(new[] { Item(1), Item(2) }, Settings()).Text;
        var on = ShtrikhPcImportWriter.Build(new[] { Item(1), Item(2) }, Settings(s => s.ClearBase = true)).Text;

        off.Should().NotContain("$$$CLR");
        var lines = on.Split("\r\n").Where(l => l.Length > 0).ToArray();
        lines.Last().Should().Be("$$$CLR");
        lines.Count(l => l == "$$$CLR").Should().Be(1);
        lines.Take(lines.Length - 1).Should().OnlyContain(l => !l.StartsWith("$$$", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2, ",", 55.5, "55,50")]
    [InlineData(2, ".", 55.5, "55.50")]
    [InlineData(0, ",", 55.5, "56")]
    [InlineData(3, ",", 55.5, "55,500")]
    [InlineData(1, ".", 0.04, "0.0")]
    public void FormatPrice_DecimalsAndSeparator(int decimals, string separator, double price, string expected)
    {
        ShtrikhPcImportWriter.FormatPrice((decimal)price, decimals, separator).Should().Be(expected);
    }

    [Fact]
    public void FormatPrice_TooLargeForSevenDigits_IsNull()
    {
        ShtrikhPcImportWriter.FormatPrice(100_000m, 2, ",").Should().BeNull();   // 10 000 000 единиц
        ShtrikhPcImportWriter.FormatPrice(99_999.99m, 2, ",").Should().Be("99999,99");
    }

    [Fact]
    public void Build_PriceTooLarge_DroppedWithWarning()
    {
        var build = ShtrikhPcImportWriter.Build(new[] { Item(1, price: 500_000m), Item(2) }, Settings());

        build.Written.Should().Be(1);
        build.Warnings.Should().ContainSingle().Which.Should().Contain("Лук репчатый");
    }

    [Fact]
    public void CleanName_TruncatesTo255_AndRemovesSeparators()
    {
        ShtrikhPcImportWriter.CleanName(new string('Я', 400)).Should().HaveLength(255);
        ShtrikhPcImportWriter.CleanName("Сыр; «Голландский»\r\nнарезка").Should().Be("Сыр, \"Голландский\"  нарезка");
    }

    [Fact]
    public void CleanName_KyrgyzLettersOutsideCp1251_AreReplacedWithSimilar()
    {
        ShtrikhPcImportWriter.CleanName("Өрүк кургатылган").Should().Be("Орук кургатылган");
        // и в самом файле нет «?» на месте букв
        var text = ShtrikhPcImportWriter.Build(new[] { Item(1, "Өрүк") }, Settings()).Text;
        ShtrikhPcImportWriter.Decode(ShtrikhPcImportWriter.Encode(text)).Should().Contain("Орук").And.NotContain("?");
    }

    [Fact]
    public void Build_DuplicateCodes_AndCodesOutOfRange_AreDroppedWithWarnings()
    {
        var items = new[] { Item(5, "Первый"), Item(5, "Второй"), Item(1_000_000, "Большой"), Item(0, "Нулевой"), Item(6, "Шестой") };

        var build = ShtrikhPcImportWriter.Build(items, Settings());

        build.Written.Should().Be(2);
        DataLines(build.Text).Select(l => l.Split(';')[0]).Should().Equal("<D 5", "<D 6");
        build.Warnings.Should().HaveCount(3);
        build.Warnings.Should().Contain(w => w.Contains("Второй")).And.Contain(w => w.Contains("Большой")).And.Contain(w => w.Contains("Нулевой"));
    }

    [Fact]
    public void Build_MaxCodeParameter_LimitsCodesToLabelLayout()
    {
        // число знаков кода на этикетке 4 → код не больше 9999
        var build = ShtrikhPcImportWriter.Build(new[] { Item(9999), Item(10000) }, Settings(), maxCode: 9999);

        build.Written.Should().Be(1);
        build.Warnings.Should().ContainSingle().Which.Should().Contain("10000");
    }

    [Fact]
    public async Task WriteAsync_WritesFileAtomically_ThenCreatesFlag()
    {
        var settings = Settings();

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(1), Item(2, piece: true) }, settings);

        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        result.Written.Should().Be(2);
        var import = Path.Combine(settings.Folder, "Import", "PCScale.txt");
        var flag = Path.Combine(settings.Folder, "Import", "PCScale.flz");
        result.ImportPath.Should().Be(import);
        File.Exists(import).Should().BeTrue();
        File.Exists(flag).Should().BeTrue();
        File.Exists(import + ".tmp").Should().BeFalse("временный файл должен быть переименован");
        var text = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(import));
        text.Should().StartWith("##@@&&\r\n#\r\n").And.Contain("<D 1;").And.Contain("<D 2;");
    }

    [Fact]
    public async Task WriteAsync_PreviousFileNotAccepted_IsNotOverwrittenWithoutConfirmation()
    {
        var settings = Settings();
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(1, "Первый") }, settings);
        var import = ShtrikhPcImportWriter.ImportPath(settings);
        var before = File.ReadAllBytes(import);

        var second = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(2, "Второй") }, settings);

        second.Status.Should().Be(ShtrikhPcWriteStatus.PendingNotAccepted);
        File.ReadAllBytes(import).Should().Equal(before);

        var forced = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(2, "Второй") }, settings, overwritePending: true);
        forced.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        ShtrikhPcImportWriter.Decode(File.ReadAllBytes(import)).Should().Contain("Второй").And.NotContain("Первый");
    }

    [Fact]
    public async Task WriteAsync_AfterScaleAcceptedFile_WritesNewOneWithoutQuestion()
    {
        var settings = Settings();
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(1) }, settings);
        var import = ShtrikhPcImportWriter.ImportPath(settings);
        // весы обработали файл: во 2-й строке «#» → «@», флаг-файл удалён
        File.WriteAllText(import, File.ReadAllText(import).Replace("##@@&&\r\n#\r\n", "##@@&&\r\n@\r\n"));
        File.Delete(ShtrikhPcImportWriter.FlagPath(settings));

        ShtrikhPcImportWriter.IsPending(import, ShtrikhPcImportWriter.FlagPath(settings)).Should().BeFalse();
        var again = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(3) }, settings);

        again.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        File.Exists(ShtrikhPcImportWriter.FlagPath(settings)).Should().BeTrue();
    }

    [Fact]
    public void IsPending_NeedsBothFlagAndUnprocessedMarker()
    {
        var import = Path.Combine(_root, "i.txt");
        var flag = Path.Combine(_root, "i.flz");
        File.WriteAllBytes(import, ShtrikhPcImportWriter.Encode("##@@&&\r\n#\r\n<D 1;x\r\n"));

        ShtrikhPcImportWriter.IsPending(import, flag).Should().BeFalse("флаг-файла нет — весы сами файл не заберут");
        File.WriteAllText(flag, "");
        ShtrikhPcImportWriter.IsPending(import, flag).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_EmptyFolder_IsNoFolder()
    {
        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(1) }, new ShtrikhPcSettings { Folder = "" });

        result.Status.Should().Be(ShtrikhPcWriteStatus.NoFolder);
    }

    [Fact]
    public async Task WriteAsync_FolderDoesNotExist_IsFolderUnavailable_Quickly()
    {
        var settings = new ShtrikhPcSettings { Folder = Path.Combine(_root, "нет-такой-папки") }.Normalize();
        var started = DateTime.UtcNow;

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(1) }, settings);

        result.Status.Should().Be(ShtrikhPcWriteStatus.FolderUnavailable);
        (DateTime.UtcNow - started).Should().BeLessThan(ShtrikhPcImportWriter.AvailabilityTimeout + TimeSpan.FromSeconds(2));
        Directory.Exists(settings.Folder).Should().BeFalse("несуществующую общую папку касса не создаёт — это значило бы писать мимо весов");
    }

    [Fact]
    public async Task WriteAsync_NothingValid_IsNothingToWrite_AndWritesNoFile()
    {
        var settings = Settings();

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(0), Item(2_000_000) }, settings);

        result.Status.Should().Be(ShtrikhPcWriteStatus.NothingToWrite);
        result.Warnings.Should().HaveCount(2);
        File.Exists(ShtrikhPcImportWriter.ImportPath(settings)).Should().BeFalse();
    }

    [Fact]
    public async Task CheckFolderAsync_Statuses()
    {
        (await ShtrikhPcImportWriter.CheckFolderAsync(new ShtrikhPcSettings { Folder = "" })).Status.Should().Be(ShtrikhPcFolderCheck.NoFolder);
        (await ShtrikhPcImportWriter.CheckFolderAsync(Settings(createFolder: false))).Status.Should().Be(ShtrikhPcFolderCheck.NotFound);

        var settings = Settings();
        (await ShtrikhPcImportWriter.CheckFolderAsync(settings)).Status.Should().Be(ShtrikhPcFolderCheck.Ok);
        Directory.GetFiles(Path.Combine(settings.Folder, "Import")).Should().BeEmpty("тестовый файл должен быть удалён");
    }

    [Fact]
    public async Task SaveToFileAsync_WritesFileWithoutFlag()
    {
        var target = Path.Combine(_root, "usb", "PCScale.txt");

        var result = await ShtrikhPcImportWriter.SaveToFileAsync(new[] { Item(1) }, Settings(), target);

        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        File.Exists(target).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_root, "usb")).Should().ContainSingle();
    }

    [Fact]
    public void ResolvePath_RelativeToFolder_BothSeparators()
    {
        ShtrikhPcImportWriter.ResolvePath(@"C:\Shared", @"Import\PCScale.txt").Should().Be(Path.Combine(@"C:\Shared", "Import", "PCScale.txt"));
        ShtrikhPcImportWriter.ResolvePath(@"C:\Shared", @"\Import\PCScale.flz").Should().Be(Path.Combine(@"C:\Shared", "Import", "PCScale.flz"));
    }

    [Fact]
    public void Settings_RoundTrip_AndNormalize()
    {
        var s = new ShtrikhPcSettings { Folder = @"\\192.168.0.10\Shared", PriceDecimals = 9, CodeLength = 9, BarcodePrefix = 5, BarcodeFormat = "ерунда", LineStyle = "?", ClearBase = true };
        var back = ShtrikhPcSettings.Parse(s.ToJson());

        back.Folder.Should().Be(@"\\192.168.0.10\Shared");
        back.PriceDecimals.Should().Be(3);
        back.CodeLength.Should().Be(6);
        back.BarcodePrefix.Should().Be(20);
        back.BarcodeFormat.Should().Be(ShtrikhPcSettings.FormatCodeWeight);
        back.LineStyle.Should().Be(ShtrikhPcSettings.LineSpace);
        back.ClearBase.Should().BeTrue();
        back.ImportFile.Should().Be(@"Import\PCScale.txt");
        back.FlagFile.Should().Be(@"Import\PCScale.flz");
        back.ExportFile.Should().Be(@"Export\Export.txt");
        ShtrikhPcSettings.Parse(null).PriceDecimals.Should().Be(2);
        ShtrikhPcSettings.Parse("не json").Folder.Should().BeEmpty();
    }
}
