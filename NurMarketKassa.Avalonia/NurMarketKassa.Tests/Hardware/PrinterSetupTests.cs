using System.Net;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13 (мастер «Подключить принтер», диагностика «Принтер не печатает?», в основном Xprinter): разбор USB-принтеров и очередей из подменных
/// данных, выбор кодировки, пробный чек и этикетка, поиск в сети с подменным «сокетом», ответы DLE EOT / TSPL, скрипты очереди, итог диагностики.
/// Настоящие принтеры и очереди этого ПК НЕ трогаются.</summary>
public sealed class PrinterSetupTests
{
    private const string Iface1 = @"\\?\usb#vid_0416&pid_5011#5&2a1b3c&0&1#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";
    private const string Iface2 = @"\\?\usb#vid_0fe6&pid_811e#5&99aa&0&2#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";

    // ------------------------------------------------------------------ распознавание

    [Theory]
    [InlineData(@"USBPRINT\XprinterXP-420B83D7\7&1a2b3c&0", "Xprinter", "XP-420B")]
    [InlineData(@"USBPRINT\XPRINTERXP-58IIH9F3A", "Xprinter", "XP-58IIH")]
    [InlineData(@"USBPRINT\XprinterXP-365B6494\7&0", "Xprinter", "XP-365B")]
    [InlineData(@"USBPRINT\GprinterGP-3120TU1A2B\x", "Gprinter", "GP-3120TU")]
    [InlineData(@"USBPRINT\POS58C1D2\7&0", null, "POS58")]
    public void ParseUsbPrintId_SplitsManufacturerAndModel_DropsChecksum(string id, string? mfg, string model)
    {
        var parsed = PrinterUsbCatalog.ParseUsbPrintId(id);
        parsed.Should().NotBeNull();
        parsed!.Value.Manufacturer.Should().Be(mfg);
        parsed.Value.Model.Should().Be(model);
    }

    [Fact]
    public void ParseUsbPrintId_NotUsbPrint_IsNull()
    {
        PrinterUsbCatalog.ParseUsbPrintId(@"USB\VID_0416&PID_5011\5&0").Should().BeNull();
        PrinterUsbCatalog.ParseUsbPrintId("").Should().BeNull();
    }

    [Fact]
    public void TryParseVidPid_FromInterfacePathAndInstanceId()
    {
        PrinterUsbCatalog.TryParseVidPid(Iface1, out var vid, out var pid).Should().BeTrue();
        vid.Should().Be(0x0416);
        pid.Should().Be(0x5011);
        PrinterUsbCatalog.TryParseVidPid(@"USB\VID_0FE6&PID_811E\5&0", out vid, out pid).Should().BeTrue();
        vid.Should().Be(0x0FE6);
        pid.Should().Be(0x811E);
        PrinterUsbCatalog.TryParseVidPid("COM3", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void GuessBrand_ByName_IsCertain_ByChineseControllerVid_IsProbableXprinter()
    {
        PrinterUsbCatalog.GuessBrand(0x0416).Should().Be(new PrinterBrandGuess("Xprinter", true));
        PrinterUsbCatalog.GuessBrand(0x0FE6).Probable.Should().BeTrue();
        PrinterUsbCatalog.GuessBrand(0x0000, "XP-80C").Should().Be(new PrinterBrandGuess("Xprinter", false));
        PrinterUsbCatalog.GuessBrand(0x0416, "Xprinter").Probable.Should().BeFalse("ответ принтера важнее VID");
        PrinterUsbCatalog.GuessBrand(0x04B8).Brand.Should().Be("Epson");
        PrinterUsbCatalog.GuessBrand(0, "Gprinter GP-3120").Brand.Should().Be("Gprinter");
        PrinterUsbCatalog.GuessBrand(0, "Microsoft XPS Document Writer").Brand.Should().BeNull("XPS — не Xprinter");
        PrinterUsbCatalog.GuessBrand(0x1234).Should().Be(PrinterBrandGuess.None);
    }

    [Theory]
    [InlineData("XP-365B", PrinterRole.Label)]
    [InlineData("XP-360B", PrinterRole.Label)]
    [InlineData("XP-420B", PrinterRole.Label)]
    [InlineData("XP-460B", PrinterRole.Label)]
    [InlineData("TSC TTP-244", PrinterRole.Label)]
    [InlineData("XP-58IIH", PrinterRole.Receipt)]
    [InlineData("XP-58IIL", PrinterRole.Receipt)]
    [InlineData("XP-80C", PrinterRole.Receipt)]
    [InlineData("XP-Q80", PrinterRole.Receipt)]
    [InlineData("XP-C230", PrinterRole.Receipt)]
    [InlineData("POS-80 11.3.0.1", PrinterRole.Receipt)]
    [InlineData("", PrinterRole.Auto)]
    public void GuessRole_ByModel(string model, PrinterRole expected) =>
        PrinterUsbCatalog.GuessRole(model).Should().Be(expected);

    // ------------------------------------------------------------------ список для кассира

    [Fact]
    public void Build_MergesUsbAndQueues_HidesVirtual_UsbWithoutQueueFirst()
    {
        var usb = new[]
        {
            new UsbPrinterDevice(Iface1, 0x0416, 0x5011, "Xprinter", "XP-80C", "USB001"),
            new UsbPrinterDevice(Iface2, 0x0FE6, 0x811E, "Xprinter", "XP-365B", "USB002"),
        };
        var queues = new[]
        {
            new WindowsQueueInfo("POS-80", "USB001", "POS-80 11.3.0.1", 0, 0, 0),
            new WindowsQueueInfo("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF", 0, 0, 0),
            new WindowsQueueInfo("Fax", "SHRFAX:", "Microsoft Shared Fax Driver", 0, 0, 0),
            new WindowsQueueInfo("HP LaserJet", "USB003", "HP LaserJet M1005", 0, 0, 0),
        };
        var others = new[]
        {
            new DiscoveredPrinter("🖨 POS-80", "POS-80"),
            new DiscoveredPrinter("🔌 USB USB001 (без драйвера)", @"\\.\USB001"),
            new DiscoveredPrinter("🔌 COM3 (последовательный порт)", "COM3"),
            new DiscoveredPrinter("🌐 Сеть: 192.168.1.50:9100", "TCP:192.168.1.50:9100"),
        };

        var list = PrinterInventoryBuilder.Build(usb, queues, others);

        list.Select(c => c.DevicePath).Should().NotContain(p => p.Contains("PDF") || p == "Fax", "виртуальные и факс не показываются");
        list.Should().NotContain(c => c.DevicePath == @"\\.\USB001", "прежний «USB без драйвера» заменён USB-устройством");
        list.Count(c => c.DevicePath == "POS-80").Should().Be(1, "очередь не дублируется из прежнего списка");

        var first = list[0];
        first.Kind.Should().Be(PrinterCandidateKind.UsbWithoutQueue);
        first.NeedsQueue.Should().BeTrue();
        first.PortName.Should().Be("USB002");
        first.DevicePath.Should().Be(Iface2, "запасной путь — USB напрямую");
        first.RoleGuess.Should().Be(PrinterRole.Label);
        first.LanguageGuess.Should().Be(PrinterLanguage.Tspl);
        first.Brand.IsXprinter.Should().BeTrue();
        first.Title.Should().Contain("XP-365B");

        var pos = list.Single(c => c.DevicePath == "POS-80");
        pos.Kind.Should().Be(PrinterCandidateKind.WindowsQueue);
        pos.Usb.Should().NotBeNull("очередь связана с USB по порту USB001");
        pos.Model.Should().Be("XP-80C");
        pos.RoleGuess.Should().Be(PrinterRole.Receipt);
        list.ToList().IndexOf(pos).Should().Be(1, "очередь с подключённым USB — сразу после USB без очереди");

        list.Single(c => c.DevicePath == "COM3").Kind.Should().Be(PrinterCandidateKind.Serial);
        list.Single(c => c.DevicePath.StartsWith("TCP:")).Kind.Should().Be(PrinterCandidateKind.Network);
        list.Last().Kind.Should().Be(PrinterCandidateKind.Serial, "COM — в конце");
    }

    [Fact]
    public void Matches_And_SuggestQueueName_AvoidsTakenNames()
    {
        var label = new PrinterCandidate(PrinterCandidateKind.UsbWithoutQueue, "x", "p", "USB001", null, null, null, PrinterBrandGuess.None, PrinterRole.Label, PrinterLanguage.Tspl);
        PrinterInventoryBuilder.Matches(label, PrinterRole.Receipt).Should().BeFalse();
        PrinterInventoryBuilder.Matches(label, PrinterRole.Label).Should().BeTrue();
        PrinterInventoryBuilder.Matches(label, PrinterRole.Auto).Should().BeTrue();

        var first = PrinterInventoryBuilder.SuggestQueueName(PrinterRole.Receipt, 80, Array.Empty<string>());
        first.Should().StartWith(PrinterQueueInstaller.QueuePrefix).And.Contain("80");
        var second = PrinterInventoryBuilder.SuggestQueueName(PrinterRole.Receipt, 80, new[] { first });
        second.Should().NotBe(first).And.StartWith(first);
        PrinterQueueInstaller.IsValidQueueName(second).Should().BeTrue();
        PrinterInventoryBuilder.SuggestQueueName(PrinterRole.Label, 58, Array.Empty<string>()).Should().StartWith(PrinterQueueInstaller.QueuePrefix);
    }

    [Fact]
    public void ServicePorts_AndPortSplit()
    {
        PrinterInventoryBuilder.IsServicePort("PORTPROMPT:").Should().BeTrue();
        PrinterInventoryBuilder.IsServicePort("USB001").Should().BeFalse();
        PrinterInventoryBuilder.SplitPorts("USB001, USB002").Should().Equal("USB001", "USB002");
    }

    // ------------------------------------------------------------------ кодировка и пробная печать

    [Fact]
    public void EncodingVariants_ChoiceMapsToCashRegisterEncoding()
    {
        PrinterTestPages.VariantForEncoding("wpc1251").Number.Should().Be(2);
        PrinterTestPages.VariantForEncoding("cp1251").Number.Should().Be(2);
        PrinterTestPages.VariantForEncoding("cp866").Number.Should().Be(1);
        PrinterTestPages.VariantForEncoding(null).Number.Should().Be(1);
        PrinterTestPages.VariantByNumber(1)!.TableByte.Should().Be(17);
        PrinterTestPages.VariantByNumber(2)!.UserEncoding.Should().Be("wpc1251");
        PrinterTestPages.VariantByNumber(2)!.TableByte.Should().Be(46);
        PrinterTestPages.VariantByNumber(3).Should().BeNull();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    [Fact]
    public void ReceiptTest_HasBothCyrillicVariants_RulerByWidth_CutAndDrawer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = PrinterTestPages.BuildReceiptTest(80, narrow80: false, cut: true, openDrawer: true, drawerPin: 1);
        bytes.Take(2).Should().Equal(new byte[] { 0x1B, 0x40 }, "сначала сброс принтера");
        var t17 = IndexOf(bytes, new byte[] { 0x1B, 0x74, 17 });
        var t46 = IndexOf(bytes, new byte[] { 0x1B, 0x74, 46 });
        t17.Should().BeGreaterThan(0);
        t46.Should().BeGreaterThan(t17, "вариант 2 после варианта 1");
        var cp866 = Encoding.GetEncoding(866).GetBytes("Проверка печати");
        var cp1251 = Encoding.GetEncoding(1251).GetBytes("Проверка печати");
        IndexOf(bytes, cp866, t17).Should().BeInRange(t17, t46, "строка 1 — в CP866 после ESC t 17");
        IndexOf(bytes, cp1251, t46).Should().BeGreaterThan(t46, "строка 2 — в CP1251 после ESC t 46");
        IndexOf(bytes, Encoding.ASCII.GetBytes(PrinterTestPages.Ruler(48))).Should().BeGreaterThan(0, "80 мм — линейка 48 знаков");
        IndexOf(bytes, new byte[] { 0x1B, 0x70, 0x01 }).Should().BeGreaterThan(0, "импульс ящика на контакт 5");
        IndexOf(bytes, new byte[] { 0x1D, 0x56, 0x42, 0x00 }).Should().BeGreaterThan(0, "отрез");

        var narrow = PrinterTestPages.BuildReceiptTest(80, narrow80: true, cut: false, openDrawer: false, drawerPin: 0);
        IndexOf(narrow, Encoding.ASCII.GetBytes(PrinterTestPages.Ruler(42) + "\n")).Should().BeGreaterThan(0, "XP-80 — 42 знака");
        IndexOf(narrow, new byte[] { 0x1D, 0x56 }).Should().Be(-1, "без отреза");
        IndexOf(narrow, new byte[] { 0x1B, 0x70 }).Should().Be(-1, "без ящика");
        var r58 = PrinterTestPages.BuildReceiptTest(58, false, true, false, 0);
        IndexOf(r58, Encoding.ASCII.GetBytes(PrinterTestPages.Ruler(32) + "\n")).Should().BeGreaterThan(0, "58 мм — 32 знака");
    }

    [Fact]
    public void TsplTestLabel_SizeGapBarcodePrint()
    {
        var text = Encoding.ASCII.GetString(PrinterTestPages.BuildTsplTestLabel(58, 40, 2));
        text.Should().StartWith("SIZE 58 mm,40 mm\r\n");
        text.Should().Contain("GAP 2 mm,0 mm\r\n");
        text.Should().Contain("CLS\r\n");
        text.Should().Contain("BARCODE 16,56,\"128\",").And.Contain(",1,0,2,4,\"4600000000017\"");
        text.Should().EndWith("PRINT 1,1\r\n");
        var small = Encoding.ASCII.GetString(PrinterTestPages.BuildTsplTestLabel(30, 20, 2));
        small.Should().Contain(",1,0,1,2,\"4600000000017\"", "узкая этикетка — тонкий штрих");
        small.Should().Contain("TEXT 16,16,\"2\"");
        var esc = PrinterTestPages.BuildEscPosLabelTest();
        esc[^2..].Should().Equal(new byte[] { 0x1D, 0x0C }, "ESC/POS-этикетка — подача до следующей наклейки");
    }

    // ------------------------------------------------------------------ ответ принтера

    [Fact]
    public void ParseEscPos_DleEot()
    {
        PrinterStatusQuery.ParseEscPos(0x12, 0x12, 0x12, 0x12)!.IsOk.Should().BeTrue();
        PrinterStatusQuery.ParseEscPos(0x1A, null, null, null)!.Offline.Should().BeTrue("бит 3 — не в сети");
        PrinterStatusQuery.ParseEscPos(0x12, 0x16, null, null)!.CoverOpen.Should().BeTrue("причина 2, бит 2 — крышка");
        PrinterStatusQuery.ParseEscPos(0x12, 0x32, null, null)!.PaperOut.Should().BeTrue("причина 2, бит 5 — бумага кончилась");
        PrinterStatusQuery.ParseEscPos(0x12, null, null, 0x72)!.PaperOut.Should().BeTrue("датчик бумаги, биты 5–6");
        var near = PrinterStatusQuery.ParseEscPos(0x12, null, null, 0x1E)!;
        near.PaperNearEnd.Should().BeTrue();
        near.IsOk.Should().BeTrue("бумага кончается — только предупреждение");
        PrinterStatusQuery.ParseEscPos(0x12, null, 0x1A, null)!.CutterError.Should().BeTrue("ошибки, бит 3 — нож");
        PrinterStatusQuery.ParseEscPos(0xFF, 0x00, null, null).Should().BeNull("не ответ DLE EOT (фиксированные биты не те)");
        PrinterStatusQuery.IsEscPosStatusByte(0x12).Should().BeTrue();
        PrinterStatusQuery.IsEscPosStatusByte(0x00).Should().BeFalse();
    }

    [Fact]
    public void ParseTspl_StatusByte()
    {
        PrinterStatusQuery.ParseTspl(0x00).IsOk.Should().BeTrue();
        PrinterStatusQuery.ParseTspl(0x01).CoverOpen.Should().BeTrue();
        PrinterStatusQuery.ParseTspl(0x04).PaperOut.Should().BeTrue();
        PrinterStatusQuery.ParseTspl(0x10).Paused.Should().BeTrue();
        PrinterStatusQuery.ParseTspl(0x20).IsOk.Should().BeTrue("печатает — не ошибка");
    }

    /// <summary>Подменный принтер: на каждую команду отвечает заданным байтом (или молчит).</summary>
    private sealed class FakePrinterStream(Func<byte[], byte?> answer) : Stream
    {
        private readonly Queue<byte> _out = new();
        public List<byte[]> Commands { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_out.Count == 0)
                return 0;
            buffer[offset] = _out.Dequeue();
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            var cmd = buffer.AsSpan(offset, count).ToArray();
            Commands.Add(cmd);
            if (answer(cmd) is { } b)
                _out.Enqueue(b);
        }
    }

    [Fact]
    public async Task QueryAsync_EscPos_AsksFourStatuses_ReportsPaperOut()
    {
        var s = new FakePrinterStream(cmd => cmd[0] == 0x10 && cmd[1] == 0x04 ? cmd[2] switch { 1 => (byte)0x12, 2 => (byte)0x32, 3 => (byte)0x12, _ => (byte)0x72 } : null);
        var r = await PrinterStatusQuery.QueryAsync(s, PrinterLanguage.EscPos, 300);
        r.Should().NotBeNull();
        r!.PaperOut.Should().BeTrue();
        s.Commands.Should().HaveCount(4);
        s.Commands.Select(c => c[2]).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task QueryAsync_Silent_ReturnsNull_AndUnknownFallsBackToTspl()
    {
        var silent = new FakePrinterStream(_ => null);
        (await PrinterStatusQuery.QueryAsync(silent, PrinterLanguage.EscPos, 120)).Should().BeNull("принтер без обратной связи");

        var tspl = new FakePrinterStream(cmd => cmd.SequenceEqual(PrinterStatusQuery.TsplStatusCommand) ? (byte)0x04 : null);
        var r = await PrinterStatusQuery.QueryAsync(tspl, PrinterLanguage.Unknown, 120);
        r.Should().NotBeNull("ESC/POS промолчал — спросили по TSPL");
        r!.PaperOut.Should().BeTrue();
    }

    // ------------------------------------------------------------------ поиск в сети (подменный сокет)

    [Fact]
    public async Task NetworkScan_FakeSocket_FindsOpenPorts_LimitsParallelism_ReportsProgress()
    {
        var baseIp = ScaleNetworkScanner.ToNumber(IPAddress.Parse("192.168.1.1"));
        var hosts = Enumerable.Range(0, 100).Select(i => baseIp + (uint)i).ToList();
        var open = new HashSet<string> { "192.168.1.50", "192.168.1.7" };
        var active = 0;
        var maxActive = 0;
        async Task<bool> Probe(string ip, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            lock (open) maxActive = Math.Max(maxActive, now);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref active);
            return open.Contains(ip);
        }
        var reports = new List<NetworkPrinterScanner.Progress>();
        var progress = new SyncProgress(p => { lock (reports) reports.Add(p); });

        var found = await NetworkPrinterScanner.ScanAsync(hosts, Probe, progress, CancellationToken.None);

        found.Should().Equal("TCP:192.168.1.7:9100", "TCP:192.168.1.50:9100");
        maxActive.Should().BeLessThanOrEqualTo(32, "не больше 32 проверок одновременно — без лишней нагрузки на сеть");
        reports.Should().Contain(p => p.Done == 100 && p.Total == 100 && p.Found == 2);
    }

    [Fact]
    public async Task NetworkScan_Cancelled_ReturnsWhatWasFound()
    {
        var baseIp = ScaleNetworkScanner.ToNumber(IPAddress.Parse("10.0.0.1"));
        var hosts = Enumerable.Range(0, 64).Select(i => baseIp + (uint)i).ToList();
        using var cts = new CancellationTokenSource();
        async Task<bool> Probe(string ip, CancellationToken ct)
        {
            if (ip == "10.0.0.1")
            {
                cts.Cancel();
                return true;
            }
            await Task.Delay(1000, ct).ContinueWith(_ => { });
            return false;
        }
        var found = await NetworkPrinterScanner.ScanAsync(hosts, Probe, null, cts.Token);
        found.Should().Contain("TCP:10.0.0.1:9100");
    }

    private sealed class SyncProgress(Action<NetworkPrinterScanner.Progress> report) : IProgress<NetworkPrinterScanner.Progress>
    {
        public void Report(NetworkPrinterScanner.Progress value) => report(value);
    }

    // ------------------------------------------------------------------ очередь Windows (только скрипты и подмена запуска)

    [Fact]
    public void CreateQueueScript_UsesBuiltInDriver_QuotesNames_RejectsBadInput()
    {
        var script = PrinterQueueInstaller.BuildCreateQueueScript("NurCRM Чек 80мм", "USB001", @"C:\Temp\log.txt");
        script.Should().Contain("'Generic / Text Only'");
        script.Should().Contain("Add-PrinterDriver -Name $drv");
        script.Should().Contain("Add-Printer -Name 'NurCRM Чек 80мм' -DriverName $drv -PortName 'USB001'");
        script.Should().Contain("exit 2", "ошибка — код выхода 2 и текст в журнал");
        PrinterQueueInstaller.PsQuote("a'b").Should().Be("'a''b'");
        FluentActions.Invoking(() => PrinterQueueInstaller.BuildCreateQueueScript("bad,name", "USB001", "x")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PrinterQueueInstaller.BuildCreateQueueScript("ok", "C:\\evil", "x")).Should().Throw<ArgumentException>();
        PrinterQueueInstaller.IsValidPortName("USB012").Should().BeTrue();
        PrinterQueueInstaller.IsValidPortName("LPT1:").Should().BeTrue();
        PrinterQueueInstaller.IsValidPortName("USB001; rm").Should().BeFalse();
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(PrinterQueueInstaller.EncodeCommand(script)));
        decoded.Should().Be(script, "кириллица в имени не ломается при передаче через -EncodedCommand");
        PrinterQueueInstaller.BuildMovePortScript("NurCRM Этикетки", "USB002", "x").Should().Contain("Set-Printer -Name 'NurCRM Этикетки' -PortName 'USB002'");
        PrinterQueueInstaller.BuildClearJobsScript("POS-80", "x").Should().Contain("Get-PrintJob -PrinterName 'POS-80' | Remove-PrintJob");
        PrinterQueueInstaller.BuildResumeScript("POS-80", "x").Should().Contain("WorkOffline = $false").And.Contain("Resume");
    }

    [Fact]
    public async Task CreateQueueAsync_GoesThroughElevatedRunner_CancelIsReported()
    {
        string? seen = null;
        PrinterQueueInstaller.RunOverride = (script, _) =>
        {
            seen = script;
            return new ElevatedOutcome(ElevatedResult.Cancelled);
        };
        try
        {
            var outcome = await PrinterQueueInstaller.CreateQueueAsync("NurCRM Этикетки", "USB002");
            outcome.Result.Should().Be(ElevatedResult.Cancelled);
            seen.Should().Contain("-PortName 'USB002'");
            (await PrinterQueueInstaller.CreateQueueAsync("bad\\name", "USB002")).Result.Should().Be(ElevatedResult.Failed, "неверное имя — UAC даже не спрашивается");
        }
        finally
        {
            PrinterQueueInstaller.RunOverride = null;
        }
    }

    [Fact]
    public void RawTextOnlyQueue_OnlyForWizardQueues()
    {
        PrinterQueueInstaller.IsRawTextOnlyQueue("POS-80").Should().BeFalse("прежние очереди клиентов — как раньше");
        PrinterQueueInstaller.IsRawTextOnlyQueue(null).Should().BeFalse();
        BarcodeLabelService.IsRawDevicePath(Iface1).Should().BeTrue("USB напрямую — этикетка командами");
    }

    // ------------------------------------------------------------------ диагностика (подменная система)

    private sealed class FakeSystem : IPrinterSystem
    {
        public List<WindowsQueueInfo> Queues { get; } = new();
        public List<UsbPrinterDevice> Usb { get; } = new();
        public Func<string, PrinterPortService.PortProbeResult> ProbeFn { get; set; } = _ => new(true, "● ok", "x");
        public PrinterStatusReport? Status { get; set; }
        public List<string> StatusAsked { get; } = new();
        public IReadOnlyList<WindowsQueueInfo> ListQueues() => Queues;
        public IReadOnlyList<UsbPrinterDevice> ListUsbPrinters() => Usb;
        public IReadOnlyList<DiscoveredPrinter> ListOtherPorts() => Array.Empty<DiscoveredPrinter>();
        public PrinterPortService.PortProbeResult Probe(string devicePath) => ProbeFn(devicePath);
        public Task<PrinterStatusReport?> QueryStatusAsync(string devicePath, PrinterLanguage language, CancellationToken ct)
        {
            StatusAsked.Add(devicePath);
            return Task.FromResult(Status);
        }
    }

    [Fact]
    public async Task Diagnostics_NoPrinter_SuggestsWizard()
    {
        var r = await PrinterDiagnostics.RunAsync(new FakeSystem(), "", PrinterRole.Receipt, PrinterLanguage.EscPos);
        r.Should().Contain(f => f.Level == FindingLevel.Error && f.Fix == PrinterFix.RunWizard);
    }

    [Fact]
    public async Task Diagnostics_QueueMissing_OffersWizard_AndMentionsFreeUsb()
    {
        var sys = new FakeSystem();
        sys.Usb.Add(new UsbPrinterDevice(Iface1, 0x0416, 0x5011, "Xprinter", "XP-58IIH", "USB001"));
        var r = await PrinterDiagnostics.RunAsync(sys, "Старый принтер", PrinterRole.Receipt, PrinterLanguage.EscPos);
        r[0].Level.Should().Be(FindingLevel.Error);
        r[0].Fix.Should().Be(PrinterFix.RunWizard);
        r.Should().Contain(f => f.Level == FindingLevel.Info && f.Text.Contains("XP-58IIH") && f.Fix == PrinterFix.RunWizard);
    }

    [Fact]
    public async Task Diagnostics_PausedOfflineStuckJobs_MovedUsb_OffersFixes()
    {
        var sys = new FakeSystem();
        sys.Queues.Add(new WindowsQueueInfo("POS-80", "USB001", "POS-80 11.3.0.1", Status: 0x1 | 0x80, Attributes: 0x400, Jobs: 3));
        sys.Usb.Add(new UsbPrinterDevice(Iface1, 0x0416, 0x5011, "Xprinter", "XP-80C", "USB002")); // воткнули в другой разъём
        var r = await PrinterDiagnostics.RunAsync(sys, "POS-80", PrinterRole.Receipt, PrinterLanguage.EscPos);
        r.Should().Contain(f => f.Fix == PrinterFix.ResumeQueue && f.FixArg == "POS-80");
        r.Should().Contain(f => f.Fix == PrinterFix.ClearJobs && f.Text.Contains('3'));
        r.Should().Contain(f => f.Fix == PrinterFix.MovePort && f.FixArg == "USB002");
        sys.StatusAsked.Should().BeEmpty("устройство очереди не подключено — состояние не спрашиваем");
    }

    [Fact]
    public async Task Diagnostics_QueueOk_UsbPresent_PrinterSaysNoPaper()
    {
        var sys = new FakeSystem { Status = new PrinterStatusReport(PaperOut: true) };
        sys.Queues.Add(new WindowsQueueInfo("NurCRM Чек 80мм", "USB001", PrinterQueueInstaller.GenericTextOnlyDriver, 0, 0, 0));
        sys.Usb.Add(new UsbPrinterDevice(Iface1, 0x0416, 0x5011, "Xprinter", "XP-80C", "USB001"));
        var r = await PrinterDiagnostics.RunAsync(sys, "NurCRM Чек 80мм", PrinterRole.Receipt, PrinterLanguage.EscPos);
        sys.StatusAsked.Should().Equal(Iface1);
        r.Count(f => f.Level == FindingLevel.Ok).Should().BeGreaterThanOrEqualTo(2, "очередь есть и USB подключён");
        r.Should().Contain(f => f.Level == FindingLevel.Error && f.Fix == PrinterFix.None);
        r.Should().NotContain(f => f.Fix == PrinterFix.OpenDriverPage, "есть ошибка — общий совет не нужен");
    }

    [Fact]
    public async Task Diagnostics_Network_NoAnswer_And_AllGood()
    {
        var sys = new FakeSystem { ProbeFn = _ => new(false, "○ нет", "tcp") };
        var bad = await PrinterDiagnostics.RunAsync(sys, "TCP:192.168.1.50:9100", PrinterRole.Label, PrinterLanguage.Tspl);
        bad.Should().Contain(f => f.Level == FindingLevel.Error && f.Fix == PrinterFix.RunWizard);
        sys.StatusAsked.Should().BeEmpty();

        var good = new FakeSystem { Status = new PrinterStatusReport() };
        var ok = await PrinterDiagnostics.RunAsync(good, "TCP:192.168.1.50:9100", PrinterRole.Label, PrinterLanguage.Tspl);
        ok.Should().NotContain(f => f.Level == FindingLevel.Error || f.Level == FindingLevel.Warning);
        ok.Last().Fix.Should().Be(PrinterFix.OpenDriverPage);
        PrinterDiagnostics.DescribeStatus(null).Single().Level.Should().Be(FindingLevel.Info, "нет ответа — не ошибка");
    }

    // ------------------------------------------------------------------ запись настроек

    [Fact]
    public void ReceiptLooksUnconfigured_DefaultLptWithoutPort()
    {
        PrinterSetupApply.ReceiptLooksUnconfigured("", _ => false).Should().BeTrue();
        PrinterSetupApply.ReceiptLooksUnconfigured("LPT1", _ => false).Should().BeTrue();
        PrinterSetupApply.ReceiptLooksUnconfigured("LPT1", _ => true).Should().BeFalse();
        PrinterSetupApply.ReceiptLooksUnconfigured("POS-80", _ => false).Should().BeFalse();
    }

    [Fact]
    public void ApplyReceiptAndLabel_WriteExistingSettings()
    {
        var keep = (UserPreferences.Instance.ReceiptCharWidth58, UserPreferences.Instance.ReceiptCharWidth80, UserPreferences.Instance.ReceiptDots58, UserPreferences.Instance.ReceiptDots80);
        try
        {
            var p = new UserPreferences { ReceiptEscPosTable = 73 };
            PrinterSetupApply.ApplyReceipt(p, new ReceiptSetupChoice("NurCRM Чек 80мм", 80, Narrow80: true, "cp866", CashDrawer: true, DrawerPin: 1));
            p.ReceiptDevicePath.Should().Be("NurCRM Чек 80мм");
            p.ReceiptEnabled.Should().BeTrue();
            p.ReceiptPaperWidthMm.Should().Be(80);
            p.ReceiptCharWidth80.Should().Be(42);
            p.ReceiptEncoding.Should().Be("cp866");
            p.ReceiptEscPosTable.Should().BeNull("«Авто» — таблица по кодировке");
            p.CashDrawerEnabled.Should().BeTrue();
            p.CashDrawerPin.Should().Be(1);
            PrinterSetupApply.ApplyReceipt(p, new ReceiptSetupChoice("tcp:192.168.1.50", 80, false, "wpc1251", false, 0));
            p.ReceiptCharWidth80.Should().Be(0, "обратно на стандарт 48 знаков");
            p.ReceiptDevicePath.Should().Be("TCP:192.168.1.50:9100");

            var template = LabelTemplate.CreateDefault();
            var changed = PrinterSetupApply.ApplyLabel(p, new LabelSetupChoice(Iface2, PrinterLanguage.Tspl, 58, 40, 3), template);
            changed.Should().BeTrue();
            template.WidthMm.Should().Be(58);
            template.HeightMm.Should().Be(40);
            p.LabelPrinterDevicePath.Should().Be(Iface2);
            p.LabelRawLanguage.Should().Be("tspl");
            p.LabelGapMm.Should().Be(3);
            PrinterSetupApply.ApplyLabel(p, new LabelSetupChoice(Iface2, PrinterLanguage.EscPos, 58, 40, 2), template).Should().BeFalse("размер не менялся");
            p.LabelRawLanguage.Should().Be("escpos");
        }
        finally
        {
            (UserPreferences.Instance.ReceiptCharWidth58, UserPreferences.Instance.ReceiptCharWidth80, UserPreferences.Instance.ReceiptDots58, UserPreferences.Instance.ReceiptDots80) = keep;
        }
    }

    /// <summary>ТОЛЬКО ЧТЕНИЕ сведений этого ПК: очереди уровня 2 совпадают со списком спулера кассы (уровень 4), USB-принтеры читаются без ошибок
    /// и у каждого разобран VID. Ничего не создаётся и не печатается.</summary>
    [Fact]
    public void LocalPc_ReadOnly_QueuesMatchSpooler_UsbParsed()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var queues = PrinterSystem.Instance.ListQueues();
        queues.Select(q => q.Name).Should().BeEquivalentTo(RawPrinterHelper.GetInstalledPrinterNames(), "те же очереди, что видит касса");
        queues.Should().OnlyContain(q => q.DriverName.Length > 0, "у каждой очереди прочитан драйвер");
        var usb = PrinterSystem.Instance.ListUsbPrinters();
        usb.Should().NotContain(d => !d.InterfacePath.StartsWith(@"\\?\") || d.Vid == 0, "путь интерфейса и VID разобраны (USB-принтеров может и не быть)");
        var built = PrinterInventoryBuilder.Build(usb, queues, Array.Empty<DiscoveredPrinter>());
        built.Should().NotContain(c => c.Title.Contains("PDF") || c.Title.Contains("XPS") || c.Title.Contains("OneNote"));
    }

    [Fact]
    public void DriverLink_IsOfficialXprinterSite()
    {
        PrinterDriverLinks.XprinterDownloadUrl.Should().StartWith("https://www.xprintertech.com/");
        PrinterDriverLinks.UrlFor(PrinterRole.Label).Should().EndWith("#Label");
        PrinterDriverLinks.UrlFor(PrinterRole.Receipt).Should().EndWith("#POS");
        string? opened = null;
        PrinterDriverLinks.OpenOverride = u => opened = u;
        try
        {
            PrinterDriverLinks.Open(PrinterDriverLinks.XprinterLabelDriversUrl).Should().BeTrue();
            opened.Should().Be(PrinterDriverLinks.XprinterLabelDriversUrl);
        }
        finally
        {
            PrinterDriverLinks.OpenOverride = null;
        }
    }
}
