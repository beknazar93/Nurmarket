using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06: настройки весов «Штрих РС-200С2» — разбор и запись строк «&lt;N номер;значение» (руководство, п. 6.3 и 8), дифф «только изменённое», защита опасных параметров,
/// сверка весов с кассой, резервная копия, порядок файлов выгрузки (флаг перезагрузки сценария — перед основным флагом), чтение-проверка протокола (порт 22222).</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ShtrikhPcConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pccfg-" + Guid.NewGuid().ToString("N")[..8]);

    public ShtrikhPcConfigTests() => Directory.CreateDirectory(_root);

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
        var s = new ShtrikhPcSettings { Folder = Path.Combine(_root, "Shared"), QuickKeys = false };
        Directory.CreateDirectory(s.Folder);
        tweak?.Invoke(s);
        return s.Normalize();
    }

    // Фрагмент Config.txt весов владельца (06.11.2026), как есть.
    private const string OwnerConfig = "<N    1; 1\r\n<N    2; vob-Scale1\r\n<N    6; Shared\\\r\n<N   11; 2\r\n<N   12; 2\r\n<N  201; Спасибо за покупку!\r\n<N  202; Алатурка сладкий мир\r\n<N  303; 27\r\n<N  304; 20\r\n<N  305; 0\r\n<N  306; 0\r\n<N  307; 5\r\n<N  308; 0\r\n<N  814; 0\r\n<N  817; 1\r\n<N  819; 1\r\n<N  953; 1\r\n<N  954; flag\r\n<N 2001; 0\r\n<N 2002; 123\r\n";

    // ---- разбор

    [Fact]
    public void Parse_OwnerConfig_ReadsNumbersAndValues()
    {
        var data = ShtrikhPcConfig.Parse(OwnerConfig);

        data.Get(1).Should().Be("1");
        data.Get(2).Should().Be("vob-Scale1");
        data.Get(6).Should().Be("Shared\\");
        data.GetInt(12, -1).Should().Be(2);
        data.Get(201).Should().Be("Спасибо за покупку!");
        data.GetInt(303, -1).Should().Be(27);
        data.GetInt(814, -1).Should().Be(0);
        data.Get(954).Should().Be("flag");
        data.Has(5).Should().BeFalse();
        ShtrikhPcConfig.AssignedQuickKeys(data).Should().Be(1);
    }

    [Theory]
    [InlineData("<N 12; 2")]
    [InlineData("<N12;2")]
    [InlineData("<N;12;2")]
    [InlineData("<N   12 ;   2  ")]
    public void Parse_AcceptsAllLineStyles(string line) =>
        ShtrikhPcConfig.Parse("##@@&&\r\n#\r\n" + line + "\r\n<D 1;;x;0;1.00").GetInt(12, -1).Should().Be(2);

    [Fact]
    public void ParseBytes_DecodesWindows1251_Cyrillic()
    {
        var bytes = ShtrikhPcImportWriter.Encode("<N  202; Магазин «Алма»\r\n");

        var data = ShtrikhPcConfig.ParseBytes(bytes);

        data.Get(202).Should().Be("Магазин «Алма»");
    }

    [Fact]
    public void Parse_IgnoresGarbageAndOtherPrefixes()
    {
        var data = ShtrikhPcConfig.Parse("мусор\r\n<D 1;;t;0;5\r\n<N abc; 1\r\n<N 5\r\n<N 0; 1\r\n<N 7; 1\r\n");

        data.Numbers.Should().Equal(7);
    }

    // ---- проверка значения

    [Fact]
    public void Validate_RangesAndText()
    {
        var decimals = ShtrikhPcConfig.Find(12)!;
        ShtrikhPcConfig.Validate(decimals, "3").Should().BeNull();
        ShtrikhPcConfig.Validate(decimals, "4").Should().NotBeNull();
        ShtrikhPcConfig.Validate(decimals, "x").Should().NotBeNull();
        ShtrikhPcConfig.Validate(ShtrikhPcConfig.Find(303)!, "19").Should().NotBeNull();
        ShtrikhPcConfig.Validate(ShtrikhPcConfig.Find(5)!, "2").Should().NotBeNull();
        ShtrikhPcConfig.Validate(ShtrikhPcConfig.Find(201)!, "а;б").Should().NotBeNull("«;» — разделитель строки");
        ShtrikhPcConfig.Validate(ShtrikhPcConfig.Find(201)!, "Спасибо!").Should().BeNull();
    }

    [Fact]
    public void Catalog_EveryParamHasTitle_UniqueNumbers_AndValidDefaults()
    {
        ShtrikhPcConfig.Catalog.Select(p => p.Number).Should().OnlyHaveUniqueItems();
        foreach (var p in ShtrikhPcConfig.Catalog)
        {
            p.Title().Should().NotBeNullOrWhiteSpace($"параметр {p.Number}");
            if (!p.ReadOnly && p.Kind != ShtrikhPcParamKind.Text)
                ShtrikhPcConfig.Validate(p, p.Default).Should().BeNull($"значение по умолчанию параметра {p.Number}");
        }
        // номера из руководства (п. 6.3) и Паспорта версий
        foreach (var n in new[] { 1, 2, 3, 4, 5, 12, 301, 303, 304, 305, 306, 307, 308, 401, 402, 403, 601, 801, 802, 803, 814, 817, 819, 953, 954, 955, 956, 957, 700, 702, 703, 704, 450, 469 })
            ShtrikhPcConfig.Find(n).Should().NotBeNull($"параметр {n}");
    }

    // ---- дифф и файл импорта

    [Fact]
    public void Diff_OnlyChangedParams()
    {
        var baseline = ShtrikhPcConfig.Parse(OwnerConfig);

        var changes = ShtrikhPcConfig.Diff(baseline, new Dictionary<int, string> { [1] = "1", [12] = "3", [303] = "27", [201] = "Спасибо за покупку!", [202] = "Другой магазин" });

        changes.Select(c => (c.Number, c.Old, c.New)).Should().Equal((12, "2", "3"), (202, "Алатурка сладкий мир", "Другой магазин"));
    }

    [Fact]
    public void Diff_ParamAbsentOnScale_OnlyIfDifferentFromDefault()
    {
        var baseline = ShtrikhPcConfig.Parse("<N 1; 1\r\n");

        // 5 («Звук») на весах нет: по умолчанию 0 — не изменение; 1 — изменение
        ShtrikhPcConfig.Diff(baseline, new Dictionary<int, string> { [5] = "0" }).Should().BeEmpty();
        ShtrikhPcConfig.Diff(baseline, new Dictionary<int, string> { [5] = "1" }).Select(c => c.Number).Should().Equal(5);
    }

    [Fact]
    public void BuildImportText_HeaderThenChangedLinesOnly_Cp1251_Crlf()
    {
        var settings = Settings();
        var changes = new List<ShtrikhPcChange> { new(12, "2", "3"), new(202, "Алатурка", "Новый «Алма»") };

        var text = ShtrikhPcConfig.BuildImportText(changes, settings);

        text.Should().Be("##@@&&\r\n#\r\n<N 12;3\r\n<N 202;Новый «Алма»\r\n");
        var bytes = ShtrikhPcImportWriter.Encode(text);
        ShtrikhPcImportWriter.Decode(bytes).Should().Be(text, "кириллица и «» должны пережить Windows-1251");
        Encoding.ASCII.GetString(bytes[..8]).Should().Be("##@@&&\r\n");
        // повторный разбор тем же парсером даёт те же значения
        ShtrikhPcConfig.Parse(text).GetInt(12, -1).Should().Be(3);
    }

    [Theory]
    [InlineData(ShtrikhPcSettings.LineSpace, "<N 12;3")]
    [InlineData(ShtrikhPcSettings.LineGlued, "<N12;3")]
    [InlineData(ShtrikhPcSettings.LineSemicolon, "<N;12;3")]
    public void BuildLine_FollowsLineStyle(string style, string expected) =>
        ShtrikhPcConfig.BuildLine(12, "3", new ShtrikhPcSettings { LineStyle = style }).Should().Be(expected);

    [Fact]
    public void Apply_AndRestore_RoundTrip()
    {
        var baseline = ShtrikhPcConfig.Parse(OwnerConfig);
        var changes = new List<ShtrikhPcChange> { new(12, "2", "3"), new(303, "27", "22") };

        var after = ShtrikhPcConfig.Apply(baseline, changes);
        var restore = ShtrikhPcConfig.DiffToRestore(after, baseline);

        after.GetInt(12, -1).Should().Be(3);
        baseline.GetInt(12, -1).Should().Be(2, "исходная копия не меняется");
        restore.Select(c => (c.Number, c.New)).Should().Equal((12, "2"), (303, "27"));
        ShtrikhPcConfig.DiffToRestore(baseline, baseline).Should().BeEmpty();
    }

    // ---- опасные параметры

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(17)]
    [InlineData(27)]
    [InlineData(801)]
    [InlineData(803)]
    [InlineData(814)]
    [InlineData(815)]
    [InlineData(817)]
    [InlineData(819)]
    [InlineData(214)]
    public void DangerReason_ProtectedParams(int number)
    {
        var reason = ShtrikhPcConfig.DangerReason(number);

        reason.Should().NotBeNull();
        reason!().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(303)]
    [InlineData(954)]
    [InlineData(201)]
    public void DangerReason_OrdinaryParams_AreNotProtected(int number) =>
        ShtrikhPcConfig.DangerReason(number).Should().BeNull();

    // ---- сверка с кассой

    [Fact]
    public void Compare_Match_AllGreen()
    {
        var kassa = new ShtrikhPcSettings { PriceDecimals = 2, BarcodePrefix = 27, PiecePrefix = 20, BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight, PieceFormat = ShtrikhPcSettings.FormatCodeAmount, CodeLength = 5, CodeChecksum = false, SendPictures = true, ScenarioReloadFlag = "flag" };
        var scale = ShtrikhPcConfig.Parse(OwnerConfig + "<N  814; 1\r\n<N  306; 1\r\n");

        var items = ShtrikhPcConfig.Compare(kassa.Normalize(), scale);

        items.Should().NotBeEmpty();
        items.Where(i => !i.Ok).Select(i => i.Text).Should().BeEmpty();
    }

    [Fact]
    public void Compare_Mismatch_ReportsDecimalsPrefixFormatAndImport()
    {
        var kassa = new ShtrikhPcSettings { PriceDecimals = 0, BarcodePrefix = 21, BarcodeFormat = ShtrikhPcSettings.FormatCodeAmount, CodeLength = 4, ScenarioReloadFlag = "other" }.Normalize();
        var scale = ShtrikhPcConfig.Parse(OwnerConfig); // 814 = 0

        var bad = ShtrikhPcConfig.Compare(kassa, scale).Where(i => !i.Ok).Select(i => i.Text).ToList();

        bad.Should().Contain(t => t.Contains("12"));
        bad.Should().Contain(t => t.Contains("303"));
        bad.Should().Contain(t => t.Contains("305"));
        bad.Should().Contain(t => t.Contains("307"));
        bad.Should().Contain(t => t.Contains("814"));
        bad.Should().Contain(t => t.Contains("954"));
    }

    [Fact]
    public void ApplyScaleToKassa_CopiesBarcodeAndDecimals()
    {
        var kassa = new ShtrikhPcSettings { PriceDecimals = 0, BarcodePrefix = 21, BarcodeFormat = ShtrikhPcSettings.FormatCodeAmount, CodeLength = 4, CodeChecksum = true }.Normalize();

        var changed = ShtrikhPcConfig.ApplyScaleToKassa(kassa, ShtrikhPcConfig.Parse(OwnerConfig));

        changed.Should().BeGreaterThan(0);
        kassa.PriceDecimals.Should().Be(2);
        kassa.BarcodePrefix.Should().Be(27);
        kassa.PiecePrefix.Should().Be(20);
        kassa.BarcodeFormat.Should().Be(ShtrikhPcSettings.FormatCodeWeight);
        kassa.CodeLength.Should().Be(5);
        kassa.CodeChecksum.Should().BeFalse();
        kassa.ScenarioReloadFlag.Should().Be("flag");
        ShtrikhPcConfig.ApplyScaleToKassa(kassa, ShtrikhPcConfig.Parse(OwnerConfig)).Should().Be(0, "второй раз менять нечего");
    }

    // ---- запись в папку весов

    [Fact]
    public async Task WriteAsync_ImportDisabled_WritesNothing()
    {
        var settings = Settings();
        var baseline = ShtrikhPcConfig.Parse(OwnerConfig); // 814 = 0

        var result = await ShtrikhPcConfigIo.WriteAsync(new[] { new ShtrikhPcChange(12, "2", "3") }, baseline, settings);

        result.Status.Should().Be(ShtrikhPcConfigIoStatus.ImportDisabled);
        File.Exists(ShtrikhPcImportWriter.ImportPath(settings)).Should().BeFalse();
        File.Exists(ShtrikhPcImportWriter.FlagPath(settings)).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_ImportEnabled_WritesOnlyChangedLines_ThenFlag()
    {
        var settings = Settings();
        var baseline = ShtrikhPcConfig.Parse(OwnerConfig + "<N  814; 1\r\n"); // дубль 814: последний — 1
        var changes = ShtrikhPcConfig.Diff(baseline, new Dictionary<int, string> { [12] = "3", [1] = "1" });

        var result = await ShtrikhPcConfigIo.WriteAsync(changes, baseline, settings);

        result.Status.Should().Be(ShtrikhPcConfigIoStatus.Ok);
        var text = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(ShtrikhPcImportWriter.ImportPath(settings)));
        text.Should().Be("##@@&&\r\n#\r\n<N 12;3\r\n");
        File.Exists(ShtrikhPcImportWriter.FlagPath(settings)).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_PreviousFilePending_IsNotOverwritten()
    {
        var settings = Settings();
        var baseline = ShtrikhPcConfig.Parse("<N 814; 1\r\n<N 12; 2\r\n");
        var changes = new[] { new ShtrikhPcChange(12, "2", "3") };
        (await ShtrikhPcConfigIo.WriteAsync(changes, baseline, settings)).Status.Should().Be(ShtrikhPcConfigIoStatus.Ok);

        var second = await ShtrikhPcConfigIo.WriteAsync(new[] { new ShtrikhPcChange(12, "2", "1") }, baseline, settings);

        second.Status.Should().Be(ShtrikhPcConfigIoStatus.Pending);
        ShtrikhPcImportWriter.Decode(File.ReadAllBytes(ShtrikhPcImportWriter.ImportPath(settings))).Should().Contain("<N 12;3");
    }

    [Fact]
    public async Task ReadFromFolder_PrefersNewestOfConfigAndExport()
    {
        var settings = Settings();
        var parent = Path.GetDirectoryName(settings.Folder)!;
        File.WriteAllBytes(Path.Combine(parent, "Config.txt"), ShtrikhPcImportWriter.Encode("<N 12; 2\r\n"));
        Directory.CreateDirectory(Path.Combine(settings.Folder, "Export"));
        var export = Path.Combine(settings.Folder, "Export", "Export.txt");
        File.WriteAllBytes(export, ShtrikhPcImportWriter.Encode("<N 12; 3\r\n"));
        File.SetLastWriteTime(Path.Combine(parent, "Config.txt"), DateTime.Now.AddHours(-1));

        var result = await ShtrikhPcConfigIo.ReadFromFolderAsync(settings);

        result.Status.Should().Be(ShtrikhPcConfigIoStatus.Ok);
        result.Path.Should().Be(export);
        result.Data!.GetInt(12, -1).Should().Be(3);
    }

    [Fact]
    public async Task ReadFromFolder_NothingThere_IsNotFound()
    {
        var result = await ShtrikhPcConfigIo.ReadFromFolderAsync(Settings());

        result.Status.Should().Be(ShtrikhPcConfigIoStatus.NotFound);
    }

    [Fact]
    public void SaveToFile_WritesSameImportText()
    {
        var path = Path.Combine(_root, "out", "cfg.txt");

        var result = ShtrikhPcConfigIo.SaveToFile(new[] { new ShtrikhPcChange(5, "0", "1") }, Settings(), path);

        result.Status.Should().Be(ShtrikhPcConfigIoStatus.Ok);
        ShtrikhPcImportWriter.Decode(File.ReadAllBytes(path)).Should().Be("##@@&&\r\n#\r\n<N 5;1\r\n");
        ShtrikhPcConfigIo.SaveToFile(Array.Empty<ShtrikhPcChange>(), Settings(), path).Status.Should().Be(ShtrikhPcConfigIoStatus.NothingToChange);
    }

    [Fact]
    public void Backup_SaveAndLatest_KeepsText()
    {
        var dir = Path.Combine(_root, "backups");

        var first = ShtrikhPcConfigBackup.Save("<N 1; 1\r\n", new DateTime(2026, 11, 6, 10, 0, 0), dir);
        var second = ShtrikhPcConfigBackup.Save("<N 1; 2\r\n<N 202; Магазин\r\n", new DateTime(2026, 11, 6, 11, 0, 0), dir);

        Path.GetFileName(first).Should().Be("Config-20261106-100000.txt");
        ShtrikhPcConfigBackup.Latest(dir).Should().Be(second);
        ShtrikhPcConfig.ParseBytes(File.ReadAllBytes(second)).Get(202).Should().Be("Магазин");
        ShtrikhPcConfigBackup.Latest(Path.Combine(_root, "none")).Should().BeNull();
    }

    // ---- фаза 3: флаг перезагрузки сценария

    [Theory]
    [InlineData("flag", "flag")]
    [InlineData("  reload ", "reload")]
    [InlineData("", "flag")]
    [InlineData("a\\b", "flag")]
    [InlineData("..", "flag")]
    [InlineData("x:y", "flag")]
    public void SafeFlagName_OnlyPlainNames(string input, string expected) =>
        ShtrikhPcScenarioWriter.SafeFlagName(input).Should().Be(expected);

    [Fact]
    public async Task WriteAsync_FileOrder_ImportFile_Scenario_ReloadFlags_ThenMainFlag()
    {
        var settings = Settings();
        var created = new List<string>();
        ShtrikhPcImportWriter.FileTrace.Value = path => created.Add(Path.GetRelativePath(settings.Folder, path));
        try
        {
            var result = await ShtrikhPcImportWriter.WriteAsync(new[] { new ShtrikhPcItem(5, "Яблоки", 100m, false), new ShtrikhPcItem(6, "Груши", 120m, false) }, settings);

            result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
            result.ReloadFlags.Should().NotBeNull().And.HaveCount(2);
        }
        finally
        {
            ShtrikhPcImportWriter.FileTrace.Value = null;
        }

        created.Should().Equal(
            Path.Combine("Import", "PCScale.txt"),
            Path.Combine("SelfScenario", "Semya.ssc"),
            "flag",
            Path.Combine("SelfScenario", "flag"),
            Path.Combine("Import", "PCScale.flz"));
        File.Exists(Path.Combine(settings.Folder, "flag")).Should().BeTrue();
        new FileInfo(Path.Combine(settings.Folder, "flag")).Length.Should().Be(0, "флаг — пустой файл");
    }

    [Fact]
    public async Task WriteAsync_CustomFlagName_AndDisabled()
    {
        var custom = Settings(s => s.ScenarioReloadFlag = "reload.flg");
        await ShtrikhPcImportWriter.WriteAsync(new[] { new ShtrikhPcItem(5, "Яблоки", 100m, false) }, custom);
        File.Exists(Path.Combine(custom.Folder, "reload.flg")).Should().BeTrue();
        File.Exists(Path.Combine(custom.Folder, "SelfScenario", "reload.flg")).Should().BeTrue();
        File.Exists(Path.Combine(custom.Folder, "flag")).Should().BeFalse();

        var off = Settings(s =>
        {
            s.Folder = Path.Combine(_root, "Shared2");
            s.ScenarioReloadFlagEnabled = false;
        });
        Directory.CreateDirectory(off.Folder);
        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { new ShtrikhPcItem(5, "Яблоки", 100m, false) }, off);
        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        result.ReloadFlags.Should().BeNullOrEmpty();
        File.Exists(Path.Combine(off.Folder, "flag")).Should().BeFalse();
        File.Exists(ShtrikhPcImportWriter.FlagPath(off)).Should().BeTrue("основной флаг импорта создаётся всегда");
    }

    // ---- фаза 3: проверка протокола (только чтение)

    [Fact]
    public void ResolveHost_FromSettingsOrUncFolder()
    {
        ShtrikhPcProtocolProbe.ResolveHost(new ShtrikhPcSettings { Folder = @"\\192.168.0.200\Shared" }).Should().Be("192.168.0.200");
        ShtrikhPcProtocolProbe.ResolveHost(new ShtrikhPcSettings { Folder = @"\\scale-pc\Shared" }).Should().BeNull();
        ShtrikhPcProtocolProbe.ResolveHost(new ShtrikhPcSettings { Folder = @"\\scale-pc\Shared", ProtocolHost = "10.0.0.5" }).Should().Be("10.0.0.5");
        new ShtrikhPcSettings { ProtocolPort = 0 }.Normalize().ProtocolPort.Should().Be(22222);
    }

    private static byte[] DeviceTypeReply(string name)
    {
        var text = Encoding.GetEncoding(1251).GetBytes(name);
        var payload = new byte[] { 1, 1, 1, 6, 0, 0 }.Concat(text).ToArray();
        var body = new byte[] { ShtrikhPrintProtocol.CmdGetDeviceType, 0 }.Concat(payload).ToArray();
        return new byte[] { ShtrikhPrintProtocol.Stx, (byte)body.Length }.Concat(body).ToArray();
    }

    [Fact]
    public async Task Probe_TcpScaleAnswers_ReportsModel_AndSendsOnlyDeviceTypeCommand()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[]? received = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[64];
            var n = await stream.ReadAsync(buffer);
            received = buffer[..n];
            await stream.WriteAsync(DeviceTypeReply("PC-200"));
        });

        var result = await ShtrikhPcProtocolProbe.ProbeAsync("127.0.0.1", port);
        await server;
        listener.Stop();

        result.Answered.Should().BeTrue();
        result.Transport.Should().Be("TCP");
        result.Info!.Name.Should().Contain("PC-200");
        received.Should().Equal(ShtrikhPrintProtocol.BuildFrame(ShtrikhPrintProtocol.CmdGetDeviceType, Array.Empty<byte>()), "кроме запроса типа устройства (FCh) ничего не отправляется");
    }

    [Fact]
    public async Task Probe_NobodyListens_ReportsFilesFallback_Within3Seconds()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var started = DateTime.UtcNow;

        var result = await ShtrikhPcProtocolProbe.ProbeAsync("127.0.0.1", port);

        result.Answered.Should().BeFalse();
        result.Message.Should().Contain(port.ToString());
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void ParseInfo_AcceptsLeadingAck_RejectsOtherCommand()
    {
        var reply = DeviceTypeReply("PC-200");

        ShtrikhPcProtocolProbe.ParseInfo(reply)!.Name.Should().Contain("PC-200");
        ShtrikhPcProtocolProbe.ParseInfo(new byte[] { ShtrikhPrintProtocol.Ack }.Concat(reply).ToArray())!.Name.Should().Contain("PC-200");
        var other = (byte[])reply.Clone();
        other[2] = 0x11;
        ShtrikhPcProtocolProbe.ParseInfo(other).Should().BeNull();
        ShtrikhPcProtocolProbe.ParseInfo(new byte[] { 1, 2, 3 }).Should().BeNull();
    }
}
