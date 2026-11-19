using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13: протокол весов Масса-К серий R/SL. Эталоны — десять кадров из официального «Весы Масса-К. Подключение к компьютеру» (Тв4.177.004 РД, ред. 2);
/// запись товара и PLU собрана по таблицам «Протокола R» ред. 6.44 (порядок байт внутри записей — догадка, см. комментарий MassaKProtocol).
/// Обмен проверяется на локальной «ловушке-весах» (127.0.0.1); настоящие весы не затрагиваются.</summary>
public sealed class MassaKProtocolTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static readonly string[] OfficialFrames =
    {
        "F8 55 CE 01 00 A0 A0 00",
        "F8 55 CE 07 00 10 00 00 00 00 01 01 5B 05",
        "F8 55 CE 02 00 91 04 04 91",
        "F8 55 CE 01 00 51 51 00",
        "F8 55 CE 01 00 00 00 00",
        "F8 55 CE 1B 00 01 02 00 00 15 29 FB 00 4E 22 00 01 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 13 4D",
        "F8 55 CE 01 00 90 90 00",
        "F8 55 CE 05 00 50 FB 00 4E 22 D7 DF",
        "F8 55 CE 01 00 23 23 00",
        "F8 55 CE 09 00 24 E8 03 00 00 01 01 00 00 88 E4",
    };

    [Fact]
    public void OfficialFrames_ParseWithValidCrc()
    {
        foreach (var hex in OfficialFrames)
        {
            var bytes = H(hex);
            MassaKProtocol.TryParse(bytes, out var frame, out var used).Should().Be(MassaKParseState.Ok, hex);
            used.Should().Be(bytes.Length);
            MassaKProtocol.BuildFrame(frame!.Command, frame.Data).Should().Equal(bytes, hex);
        }
    }

    [Fact]
    public void ReadOnlyCommands_MatchDocument()
    {
        MassaKProtocol.BuildGetWeight().Should().Equal(H("F8 55 CE 01 00 A0 A0 00"));
        MassaKProtocol.BuildSetWorkMode().Should().Equal(H("F8 55 CE 02 00 91 04 04 91"));
        MassaKProtocol.BuildGetDeviceId().Should().Equal(H("F8 55 CE 01 00 90 90 00"));
    }

    [Fact]
    public void Replies_AreDecoded()
    {
        MassaKProtocol.TryParse(H("F8 55 CE 07 00 10 00 00 00 00 01 01 5B 05"), out var weight, out _);
        MassaKProtocol.ParseWeight(weight!).Should().Be((0.0, true));
        MassaKProtocol.TryParse(H("F8 55 CE 05 00 50 FB 00 4E 22 D7 DF"), out var id, out _);
        MassaKProtocol.ParseDeviceId(id!).Should().Be(0x224E00FBu);
        MassaKProtocol.ParseWeight(new MassaKFrame(MassaKProtocol.CmdAckWeight, new byte[] { 0xE8, 0x03, 0, 0, 1, 0 })).Should().Be((1.0, false));
    }

    [Fact]
    public void BrokenFrames_AreClassified()
    {
        MassaKProtocol.TryParse(H("F8 55 CE 05 00 50 FB 00 4E 22 D7 DE"), out _, out var used).Should().Be(MassaKParseState.BadCrc);
        used.Should().Be(12);
        MassaKProtocol.TryParse(H("F8 55 CE 05 00 50"), out _, out _).Should().Be(MassaKParseState.NeedMore);
        MassaKProtocol.TryParse(H("00 11 F8 55 CE 01 00 51 51 00"), out _, out var skip).Should().Be(MassaKParseState.BadHeader);
        skip.Should().Be(2);
    }

    [Fact]
    public void GoodsRecord_HasConsistentLengths_AndBitMask()
    {
        var goods = new MassaKGoods { Id = 7, Code = "7", Price = 123.45m, Name = "Сыр Гауда", BarcodePrefix = 20 };
        var record = MassaKProtocol.BuildGoodsRecord(goods);
        (record[4] | (record[5] << 8)).Should().Be(record.Length - 6, "Length — всё после поля Length");
        record[6].Should().Be(4 + 15 + 4 + 1, "DigLength = BitMask + Code + Price + BarcodePrefix");
        BitConverter.ToUInt32(record, 7).Should().Be(0x0F | (1u << 5) | (1u << 15));
        // цена 123,45 сом = 12345 «копеек» = 0x3039, младший байт первым
        record.Skip(7 + 4 + 15).Take(4).Should().Equal(0x39, 0x30, 0x00, 0x00);
        // название в Windows-1251 с двухбайтной длиной
        record[^13..^2].Should().Equal(H("09 00 D1 FB F0 20 C3 E0 F3 E4 E0"));
    }

    [Fact]
    public void GoodsRecord_PieceGoods_SetsTypeBit()
    {
        var record = MassaKProtocol.BuildGoodsRecord(new MassaKGoods { Id = 1, Code = "1", Price = 10m, Name = "Яйцо", Piece = true, Unit = "шт" });
        (BitConverter.ToUInt32(record, 7) & (1u << 8)).Should().NotBe(0);
        (BitConverter.ToUInt32(record, 7) & (1u << 4)).Should().NotBe(0);
    }

    [Fact]
    public void PluRecord_Layout()
    {
        var record = MassaKProtocol.BuildPluRecord(7, 7, 7);
        record.Should().Equal(H("07 00 00 00 13 00 07 00 00 00 00 00 07 00 00 00 20 20 20 20 20 E8 03 00 00"));
    }

    [Fact]
    public void UpsertFrames_UseSingleRecordFileTypes_AndNeverReset()
    {
        var (goods, plu) = MassaKProtocol.BuildUpsertFrames(new MassaKGoods { Id = 7, Code = "7", Price = 10m, Name = "Сыр" }, 7);
        MassaKProtocol.TryParse(goods, out var g, out _);
        MassaKProtocol.TryParse(plu, out var p, out _);
        g!.Command.Should().Be(MassaKProtocol.CmdDfile);
        g.Data[0].Should().Be(101);
        p!.Data[0].Should().Be(105);
        MassaKProtocol.IsDfileAck(new MassaKFrame(MassaKProtocol.CmdAckDfile, new byte[] { 101, 1, 0, 1, 0 }), 101).Should().BeTrue();
        MassaKProtocol.IsDfileAck(new MassaKFrame(MassaKProtocol.CmdBadDfile, new byte[] { 101, 0, 0, 0, 0 }), 101).Should().BeFalse();
    }

    [Fact]
    public void Validate_FindsProblems()
    {
        var ok = new MassaKGoods { Id = 7, Code = "7", Price = 10m, Name = "Сыр" };
        MassaKProtocol.Validate(ok).Should().Be(MassaKGoodsProblem.None);
        MassaKProtocol.Validate(ok with { Id = 0 }).Should().Be(MassaKGoodsProblem.BadId);
        MassaKProtocol.Validate(ok with { Code = "" }).Should().Be(MassaKGoodsProblem.BadCode);
        MassaKProtocol.Validate(ok with { Price = 1_000_000m }).Should().Be(MassaKGoodsProblem.BadPrice);
        MassaKProtocol.Validate(ok with { Name = " " }).Should().Be(MassaKGoodsProblem.EmptyName);
    }

    [Fact]
    public void Text_RemovesSeparators_AndMapsKyrgyzLetters()
    {
        MassaKProtocol.CleanText("^Сыр|Гауда", 50).Should().Be("Сыр/Гауда");
        MassaKProtocol.EncodeText("Қымыз өрүк", latin: false).Should().Equal(MassaKProtocol.EncodeText("Кымыз орук", latin: false));
        System.Text.Encoding.ASCII.GetString(MassaKProtocol.EncodeText("Сыр", latin: true)).Should().NotBeEmpty();
    }

    // ------------------------------------------------------------------ обмен на «ловушке-весах»

    private sealed class FakeScale : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public List<byte> Commands { get; } = new();
        public Func<MassaKFrame, byte[]?> Reply { get; set; }

        public FakeScale()
        {
            Reply = DefaultReply;
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private static byte[]? DefaultReply(MassaKFrame f) => f.Command switch
        {
            0x90 => MassaKProtocol.BuildFrame(0x50, new byte[] { 0xFB, 0x00, 0x4E, 0x22 }),
            0x80 => MassaKProtocol.BuildFrame(0x40, new byte[] { 0x10, 0, 0, 0 }),
            0xA0 => MassaKProtocol.BuildFrame(0x10, new byte[] { 0xE8, 0x03, 0, 0, 1, 1 }),
            0x91 => MassaKProtocol.BuildFrame(0x51),
            0x82 => MassaKProtocol.BuildFrame(0x42, f.Data[..5]),
            _ => MassaKProtocol.BuildFrame(0xF0),
        };

        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var have = 0;
                    while (true)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(have));
                        if (n == 0)
                            break;
                        have += n;
                        while (MassaKProtocol.TryParse(buffer.AsSpan(0, have), out var frame, out var used) != MassaKParseState.NeedMore)
                        {
                            Array.Copy(buffer, used, buffer, 0, have - used);
                            have -= used;
                            if (frame is null)
                                continue;
                            lock (Commands)
                                Commands.Add(frame.Command);
                            if (Reply(frame) is { } reply)
                                await stream.WriteAsync(reply);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
            {
                // тест закончился
            }
        }

        public void Dispose() => _listener.Stop();
    }

    private static MassaKScaleService Service(FakeScale scale)
    {
        MassaKScaleService.ExchangeLogPathOverride = Path.Combine(Path.GetTempPath(), $"massak-test-{Guid.NewGuid():N}.log");
        return new MassaKScaleService(IPAddress.Loopback, scale.Port, 1000, 1);
    }

    [Fact]
    public async Task Check_ReadsSerialStatusAndWeight_WithoutWriting()
    {
        using var scale = new FakeScale();
        var result = await Service(scale).CheckAsync(CancellationToken.None);
        result.Ok.Should().BeTrue();
        result.Serial.Should().Be(0x224E00FBu);
        result.FileMask.Should().Be(0x10u);
        result.WeightKg.Should().Be(1.0);
        lock (scale.Commands)
            scale.Commands.Should().OnlyContain(c => c == 0x90 || c == 0x80 || c == 0xA0);
    }

    [Fact]
    public async Task Check_WhenScaleSaysUnknownCommand_ReportsNack()
    {
        using var scale = new FakeScale { Reply = _ => MassaKProtocol.BuildFrame(0xF0) };
        var result = await Service(scale).CheckAsync(CancellationToken.None);
        result.Error.Should().Be(MassaKError.Nack);
    }

    [Fact]
    public async Task Upload_SendsWorkModeThenGoodsAndPluRecords_AndNeverResets()
    {
        using var scale = new FakeScale();
        var items = new[]
        {
            (new MassaKGoods { Id = 7, Code = "7", Price = 10m, Name = "Сыр" }, 7L),
            (new MassaKGoods { Id = 8, Code = "8", Price = 20m, Name = "Колбаса" }, 8L),
        };
        var result = await Service(scale).UploadAsync(items, latinText: false, dryRun: false, progress: null, CancellationToken.None);
        result.Ok.Should().BeTrue();
        result.Accepted.Should().Be(2);
        lock (scale.Commands)
            scale.Commands.Should().Equal(0x91, 0x82, 0x82, 0x82, 0x82);
    }

    [Fact]
    public async Task Upload_StopsOnRefusal_AndNamesTheProduct()
    {
        using var scale = new FakeScale
        {
            Reply = f => f.Command == 0x82 && f.Data[0] == 105 ? MassaKProtocol.BuildFrame(0x43, new byte[] { 105, 0, 0, 0, 0 }) : FakeReply(f),
        };
        var result = await Service(scale).UploadAsync(new[] { (new MassaKGoods { Id = 7, Code = "7", Price = 10m, Name = "Сыр" }, 7L) }, false, false, null, CancellationToken.None);
        result.Error.Should().Be(MassaKError.Rejected);
        result.FailedId.Should().Be(7);
        result.Accepted.Should().Be(0);
    }

    private static byte[]? FakeReply(MassaKFrame f) => f.Command switch
    {
        0x91 => MassaKProtocol.BuildFrame(0x51),
        0x82 => MassaKProtocol.BuildFrame(0x42, f.Data[..5]),
        _ => MassaKProtocol.BuildFrame(0xF0),
    };

    [Fact]
    public async Task Upload_DryRun_SendsNothing_ButWritesTheLog()
    {
        using var scale = new FakeScale();
        var service = Service(scale);
        var result = await service.UploadAsync(new[] { (new MassaKGoods { Id = 7, Code = "7", Price = 10m, Name = "Сыр" }, 7L) }, false, true, null, CancellationToken.None);
        result.Ok.Should().BeTrue();
        lock (scale.Commands)
            scale.Commands.Should().BeEmpty();
        File.ReadAllText(MassaKScaleService.ExchangeLogPath).Should().Contain("DRY-TX");
    }

    [Fact]
    public async Task Check_WhenNobodyListens_FailsToConnect()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        MassaKScaleService.ExchangeLogPathOverride = Path.Combine(Path.GetTempPath(), $"massak-test-{Guid.NewGuid():N}.log");
        var result = await new MassaKScaleService(IPAddress.Loopback, port, 300, 0).CheckAsync(CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.Error.Should().Be(MassaKError.ConnectFailed);
    }
}
