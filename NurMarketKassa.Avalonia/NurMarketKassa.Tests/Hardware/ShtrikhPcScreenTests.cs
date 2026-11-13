using System.Globalization;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (жалоба владельца: весы «ШТРИХ РС200 С3 15-2.5» — «при отправке товаров не показывает на экране»): весы РС-200 — компьютер с экраном, товары принимает
/// ТОЛЬКО файлом в общей папке (руководство администратора РС-200С2, п. 7.4, 8.1; драйвер SetRetail для РС-200 С3 — те же PCScale.txt/.flz и SelfScenario\Semya.ssc).
/// Проверяется: (1) касса замечает, что весы ждут другой файл импорта/флаг или показывают другой сценарий, или стоят в обычном режиме (экран = кнопки быстрого доступа);
/// (2) частичная отправка не стирает с экрана прежние товары; (3) «подменные весы» — разбор файла кассы так, как его описывает руководство (заголовок «##@@&amp;&amp;»/«#», 34 поля «&lt;D»,
/// 22 поля «&lt;R», команды в конце, флаг-файл), без настоящих весов и сети.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ShtrikhPcScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pcscr-" + Guid.NewGuid().ToString("N")[..8]);

    public ShtrikhPcScreenTests() => Directory.CreateDirectory(_root);

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

    private ShtrikhPcSettings Settings(Action<ShtrikhPcSettings>? tweak = null)
    {
        var s = new ShtrikhPcSettings { Folder = Path.Combine(_root, "Shared"), SendPictures = false, ScenarioReloadFlagEnabled = false };
        Directory.CreateDirectory(s.Folder);
        tweak?.Invoke(s);
        return s.Normalize();
    }

    private static ShtrikhPcItem Item(long code, string name = "Яблоки", decimal price = 100m) => new(code, name, price, false);

    // ---------------------------------------------------------------- проверки «дойдёт ли до экрана»

    [Fact]
    public void ScreenChecks_ImportFileNamesDiffer_ScaleNeverPicksTheFileUp()
    {
        var kassa = new ShtrikhPcSettings().Normalize(); // Import\PCScale.txt / Import\PCScale.flz
        var scale = ShtrikhPcConfig.Parse("<N  801; Import\\Import.txt\r\n<N  803; Import\\Import.flz\r\n");

        var bad = ShtrikhPcConfig.ScreenChecks(kassa, scale).Where(c => !c.Ok).Select(c => c.Text).ToList();

        bad.Should().HaveCount(2);
        bad.Should().Contain(t => t.Contains("801") && t.Contains("Import\\Import.txt"));
        bad.Should().Contain(t => t.Contains("803"));
    }

    [Fact]
    public void ScreenChecks_SameFileNames_SlashesAndCaseDoNotMatter()
    {
        var kassa = new ShtrikhPcSettings().Normalize();
        var scale = ShtrikhPcConfig.Parse("<N  801; import/pcscale.TXT\r\n<N  803; Import\\PCScale.flz\r\n");

        ShtrikhPcConfig.ScreenChecks(kassa, scale).Should().OnlyContain(c => c.Ok);
    }

    [Fact]
    public void ScreenChecks_SelfService_OtherScenarioFile_IsReported()
    {
        var kassa = new ShtrikhPcSettings().Normalize(); // SelfScenario\Semya.ssc
        var scale = ShtrikhPcConfig.Parse("<N   27; 1\r\n<N  951; SelfScenario\\\r\n<N  952; Main.ssc\r\n");

        var checks = ShtrikhPcConfig.ScreenChecks(kassa, scale);

        checks.Should().ContainSingle(c => !c.Ok).Which.Text.Should().Contain("Main.ssc").And.Contain("Semya.ssc");
    }

    [Fact]
    public void ScreenChecks_SelfService_SameScenario_Ok_AndNotCheckedWhenTheTillDoesNotUpdateIt()
    {
        var scale = ShtrikhPcConfig.Parse("<N   27; 1\r\n<N  951; SelfScenario\\\r\n<N  952; Semya.ssc\r\n");

        ShtrikhPcConfig.ScreenChecks(new ShtrikhPcSettings().Normalize(), scale).Should().ContainSingle().Which.Ok.Should().BeTrue();
        ShtrikhPcConfig.ScreenChecks(new ShtrikhPcSettings { UpdateScenario = false }.Normalize(), ShtrikhPcConfig.Parse("<N   27; 1\r\n<N  952; Main.ssc\r\n"))
            .Should().BeEmpty("касса сценарий не пишет — сверять нечего");
    }

    [Fact]
    public void ScreenChecks_NormalMode_WithoutQuickKeys_SaysGoodsAreFoundOnlyBySearch()
    {
        var scale = ShtrikhPcConfig.Parse("<N   27; 0\r\n<N  814; 0\r\n");

        var check = ShtrikhPcConfig.ScreenChecks(new ShtrikhPcSettings { QuickKeys = false }.Normalize(), scale).Single();
        check.Ok.Should().BeFalse();
        check.Text.Should().Contain("2001").And.Contain("814");

        var withKeys = ShtrikhPcConfig.ScreenChecks(new ShtrikhPcSettings { QuickKeys = true }.Normalize(), ShtrikhPcConfig.Parse("<N   27; 0\r\n<N  814; 1\r\n")).Single();
        withKeys.Ok.Should().BeTrue();
    }

    [Fact]
    public void ScreenProblems_ListsOnlyFailures_IncludingGoodsAndPictureImportOff()
    {
        var kassa = new ShtrikhPcSettings { SendPictures = true }.Normalize();
        var scale = ShtrikhPcConfig.Parse("<N  801; Import\\PCScale.txt\r\n<N  817; 0\r\n<N  819; 0\r\n");

        var problems = ShtrikhPcConfig.ScreenProblems(kassa, scale);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("817")).And.Contain(p => p.Contains("819"));
        ShtrikhPcConfig.ScreenProblems(kassa, ShtrikhPcConfig.Parse("<N  817; 1\r\n<N  819; 1\r\n")).Should().BeEmpty();
    }

    [Fact]
    public void ApplyScaleToKassa_TakesFileNamesAndScenario_ButNotUnsafePaths()
    {
        var kassa = new ShtrikhPcSettings().Normalize();
        var scale = ShtrikhPcConfig.Parse("<N  801; Import\\Import.txt\r\n<N  803; Import\\Import.flz\r\n<N  951; Scen\\\r\n<N  952; Main.ssc\r\n");

        var changed = ShtrikhPcConfig.ApplyScaleToKassa(kassa, scale);

        changed.Should().Be(4);
        kassa.ImportFile.Should().Be("Import\\Import.txt");
        kassa.FlagFile.Should().Be("Import\\Import.flz");
        kassa.ScenarioFolder.Should().Be("Scen");
        kassa.ScenarioFile.Should().Be("Main.ssc");
        ShtrikhPcConfig.ScreenChecks(kassa, scale).Should().OnlyContain(c => c.Ok);
        ShtrikhPcConfig.ApplyScaleToKassa(kassa, scale).Should().Be(0, "второй раз менять нечего");

        var safe = new ShtrikhPcSettings().Normalize();
        ShtrikhPcConfig.ApplyScaleToKassa(safe, ShtrikhPcConfig.Parse("<N  801; D:\\PCScale\\x.txt\r\n<N  803; ..\\..\\x.flz\r\n<N  952; ..\\x.ssc\r\n")).Should().Be(0);
        safe.ImportFile.Should().Be(ShtrikhPcSettings.DefaultImportFile);
        safe.FlagFile.Should().Be(ShtrikhPcSettings.DefaultFlagFile);
        safe.ScenarioFile.Should().Be("Semya.ssc");
    }

    // ---------------------------------------------------------------- прежние товары остаются на экране

    [Fact]
    public void ScreenCodes_KeepsPreviousGoods_InListOrder_DropsRejectedOfThisBatch()
    {
        // порядок списка окна: 3 (раньше), 5 (сейчас), 7 (сейчас, но не записан), 9 (раньше)
        var codes = ShtrikhPcScenarioWriter.ScreenCodes(accepted: new long[] { 5 }, batch: new long[] { 5, 7 }, screenOrder: new long[] { 3, 5, 7, 9, 3, 0 });

        codes.Should().Equal(3, 5, 9);
        ShtrikhPcScenarioWriter.ScreenCodes(new long[] { 5, 6 }, new long[] { 5, 6 }, null).Should().Equal(5, 6);
        ShtrikhPcScenarioWriter.ScreenCodes(new long[] { 5, 6 }, new long[] { 5, 6 }, new long[] { 3 }).Should().Equal(new long[] { 3, 5, 6 }, "принятые, которых нет в порядке, — в конец");
    }

    [Fact]
    public async Task WriteAsync_PartialSend_ScenarioAndQuickKeys_KeepPreviousGoods()
    {
        var settings = Settings(s => s.QuickKeys = true);

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings, screenOrder: new long[] { 3, 5, 9 });

        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        result.Scenario!.Products.Should().Be(3);
        var scenario = File.ReadAllText(Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc"));
        scenario.Should().Contain("; 1; 3; 0; 0;").And.Contain("; 1; 5; 0; 0;").And.Contain("; 1; 9; 0; 0;");
        var import = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(ShtrikhPcImportWriter.ImportPath(settings)));
        import.Should().Contain("<N 2001;3\r\n").And.Contain("<N 2002;5\r\n").And.Contain("<N 2003;9\r\n").And.Contain("<N 2004;0\r\n");
        import.Split("\r\n").Count(l => l.StartsWith("<D ", StringComparison.Ordinal)).Should().Be(1, "в базу весов пишется только эта отправка");
    }

    [Fact]
    public async Task WriteAsync_FullUpload_WithoutScreenOrder_OnlyThisBatchOnScreen()
    {
        var settings = Settings();

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5), Item(6) }, settings);

        result.Scenario!.Products.Should().Be(2);
    }

    // ---------------------------------------------------------------- подменные весы

    /// <summary>Имитация автоматического импорта весов РС-200 по руководству (п. 8.1.1–8.1.3, 6.2.1, 6.2.2): флаг-файл → заголовок «##@@&amp;&amp;» и «#» → команды в конце → строки «&lt;D»
    /// (34 поля) и «&lt;R» (22 поля) → во 2-й строке «@», флаг удаляется. Имена файлов — из «настроек весов» (801/803), как у настоящих весов.</summary>
    private sealed class FakePcScale
    {
        public readonly Dictionary<long, (string Name, string Price, int Type, long Picture)> Goods = new();
        public readonly Dictionary<long, string> Resources = new();
        public readonly List<string> Errors = new();

        public bool Poll(string shared, string importFile = ShtrikhPcSettings.DefaultImportFile, string flagFile = ShtrikhPcSettings.DefaultFlagFile)
        {
            var flag = Path.Combine(shared, flagFile);
            var import = Path.Combine(shared, importFile);
            if (!File.Exists(flag) || !File.Exists(import))
                return false;
            var lines = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(import)).Split("\r\n").ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count < 2 || lines[0] != "##@@&&" || lines[1] != "#")
            {
                Errors.Add("header");
                return false;
            }
            var seenData = false;
            var commandsStarted = false;
            for (var i = lines.Count - 1; i >= 2; i--)
            {
                // команды — только в последних строках
                if (lines[i].StartsWith("$$$", StringComparison.Ordinal))
                {
                    if (seenData)
                        Errors.Add("command-not-last");
                    if (lines[i] == "$$$CLR")
                        commandsStarted = true;
                    continue;
                }
                seenData = true;
            }
            if (commandsStarted)
                Goods.Clear();
            foreach (var line in lines.Skip(2))
            {
                if (line.StartsWith("$$$", StringComparison.Ordinal))
                    continue;
                var body = line.Length > 2 ? line[2..].TrimStart() : "";
                var f = body.Split(';');
                switch (line[..2])
                {
                    case "<D":
                        if (f.Length != 34)
                        {
                            Errors.Add($"D fields {f.Length}");
                            continue;
                        }
                        if (!long.TryParse(f[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is < 1 or > 999_999)
                        {
                            Errors.Add("D code");
                            continue;
                        }
                        if (f[2].Length > 255 || !decimal.TryParse(f[4].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                        {
                            Errors.Add("D name/price");
                            continue;
                        }
                        Goods[code] = (f[2], f[4], int.Parse(f[3].Trim(), CultureInfo.InvariantCulture), long.Parse(f[30].Trim(), CultureInfo.InvariantCulture));
                        break;
                    case "<R":
                        if (f.Length != 22)
                            Errors.Add($"R fields {f.Length}");
                        else
                            Resources[long.Parse(f[0].Trim(), CultureInfo.InvariantCulture)] = f[21];
                        break;
                    case "<N":
                        break;
                    default:
                        Errors.Add("prefix " + line[..2]);
                        break;
                }
            }
            lines[1] = "@";
            File.WriteAllBytes(import, ShtrikhPcImportWriter.Encode(string.Join("\r\n", lines) + "\r\n"));
            File.Delete(flag);
            return true;
        }
    }

    [Fact]
    public async Task FakeScale_ImportsGoods_WithCyrillicNames_Prices_AndMarksTheFileProcessed()
    {
        var settings = Settings();
        var scale = new FakePcScale();

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(9999, "Тест весовой; ТЕСТ", 1234.5m), Item(12, "Сүт өрүк") }, settings);
        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);

        scale.Poll(settings.Folder).Should().BeTrue("флаг-файл и файл импорта лежат там, где их ждут весы по умолчанию");
        scale.Errors.Should().BeEmpty();
        scale.Goods.Should().ContainKey(9999).WhoseValue.Should().Be(("Тест весовой, ТЕСТ", "1234.50", 0, 0L));
        scale.Goods[12].Name.Should().Be("Сут орук", "киргизские буквы — ближайшие из Windows-1251, иначе на экране весов «?»");
        File.Exists(ShtrikhPcImportWriter.FlagPath(settings)).Should().BeFalse("весы удалили флаг");
        ShtrikhPcImportWriter.IsPending(ShtrikhPcImportWriter.ImportPath(settings), ShtrikhPcImportWriter.FlagPath(settings)).Should().BeFalse();

        // следующая отправка проходит без вопроса «не принято»
        (await ShtrikhPcImportWriter.WriteAsync(new[] { Item(13) }, settings)).Status.Should().Be(ShtrikhPcWriteStatus.Ok);
    }

    [Fact]
    public async Task FakeScale_WaitingForAnotherImportFile_GetsNothing_AndTheTillCanSeeWhy()
    {
        var settings = Settings();
        var scale = new FakePcScale();
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings);

        scale.Poll(settings.Folder, @"Import\Import.txt", @"Import\Import.flz").Should().BeFalse("весы ждут Import.txt — файл кассы PCScale.txt они не видят");
        scale.Goods.Should().BeEmpty();

        var problems = ShtrikhPcConfig.ScreenProblems(settings, ShtrikhPcConfig.Parse("<N  801; Import\\Import.txt\r\n<N  803; Import\\Import.flz\r\n"));
        problems.Should().HaveCount(2, "касса после отправки говорит, что весы ждут другое имя");
    }

    // ---------------------------------------------------------------- 2026-11-12: ожидание приёма (ТЗ PC-200, п. 3: флаг удалён и «@», не дольше 5 минут)

    [Fact]
    public async Task WaitForAcceptance_ScalePicksTheFileUp_Accepted()
    {
        var settings = Settings();
        var scale = new FakePcScale();
        (await ShtrikhPcImportWriter.WriteAsync(new[] { Item(9999, "Тест") }, settings)).Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        var scaleTask = Task.Run(async () =>
        {
            await Task.Delay(400);
            scale.Poll(settings.Folder);
        });

        var result = await ShtrikhPcImportWriter.WaitForAcceptanceAsync(settings, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
        await scaleTask;

        result.Status.Should().Be(ShtrikhPcAcceptStatus.Accepted);
        scale.Goods.Should().ContainKey(9999);
    }

    [Fact]
    public async Task WaitForAcceptance_FlagStaysThere_Timeout()
    {
        var settings = Settings();
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings);

        var result = await ShtrikhPcImportWriter.WaitForAcceptanceAsync(settings, TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(100));

        result.Status.Should().Be(ShtrikhPcAcceptStatus.Timeout, "весы выключены / программа закрыта / ждут другое имя — флаг так и лежит");
        File.Exists(ShtrikhPcImportWriter.FlagPath(settings)).Should().BeTrue("ожидание ничего не трогает в папке весов");
    }

    [Fact]
    public async Task WaitForAcceptance_FlagDeletedButNotMarked_IsReported()
    {
        var settings = Settings();
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings);
        File.Delete(ShtrikhPcImportWriter.FlagPath(settings)); // флаг убрал кто-то другой, во 2-й строке по-прежнему «#»

        var result = await ShtrikhPcImportWriter.WaitForAcceptanceAsync(settings, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));

        result.Status.Should().Be(ShtrikhPcAcceptStatus.FlagGoneNotMarked);
    }

    [Fact]
    public async Task WaitForAcceptance_FolderUnreadable_Unavailable()
    {
        var settings = Settings(s => s.Folder = Path.Combine(_root, "нет-такой-папки"));

        var result = await ShtrikhPcImportWriter.WaitForAcceptanceAsync(settings, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));

        result.Status.Should().Be(ShtrikhPcAcceptStatus.Unavailable);
    }

    [Fact]
    public async Task FakeScale_FullUpload_ClearCommandIsLast_AndClearsOldGoods()
    {
        var settings = Settings(s => s.ClearBase = true);
        var scale = new FakePcScale();
        scale.Goods[48002] = ("старый товар", "1.00", 0, 0);

        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings);
        scale.Poll(settings.Folder).Should().BeTrue();

        scale.Errors.Should().BeEmpty();
        scale.Goods.Keys.Should().Equal(5L);
    }
}
