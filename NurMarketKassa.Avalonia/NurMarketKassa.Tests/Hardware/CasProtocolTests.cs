using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13: весы CAS. Эталоны — hex-примеры документа CAS «CL5000J Communication Protocol» (05.10.2007, Rev 1.0, стр. 2–8): кадры записи PLU, стирания,
/// чтения штрихкода, ответы весов. Сеть — только локальная «ловушка» 127.0.0.1 (на настоящие весы тесты ничего не шлют).</summary>
public sealed class CasProtocolTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static string X(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void Packet_DownloadPluExample_MatchesDocumentBytes()
    {
        // Документ, стр. 4: «Download PLU 1 of dept 1 (147 bytes no message)». Название 2/3 и два числа 0x2711 — как в примере; PLU 1, отдел 1, тип 1, «PLU0001».
        var data = CasProtocol.BuildPluStruct(new CasPlu { Department = 1, PluNumber = 1, Name = "PLU0001", Weighed = true });
        data.Length.Should().Be(147);
        foreach (var (offset, text) in new[] { (47, "name2"), (87, "name3") })
            Encoding.ASCII.GetBytes(text).CopyTo(data, offset);
        data[113] = 0x11; data[114] = 0x27; data[121] = 0x11; data[122] = 0x27;
        var frame = CasProtocol.BuildPacket("WL", 0, data);
        X(frame[..10]).Should().Be("574C000000002C93003A");
        X(frame[10..24]).Should().Be("01000100000001504C5530303031"); // отдел 1, PLU 1, тип 1, «PLU0001» (2026-11-14: в эталоне была опечатка в hex)
        X(frame[^3..]).Should().Be("3AFF0D"); // контрольная сумма из документа
    }

    [Theory]
    [InlineData("WL", 0u, "010001000000", "574C000000002C06003A010001000000" + "3AA80D")]            // стирание PLU 1 отдела 1
    [InlineData("WL", 0u, "000000000000", "574C000000002C06003A000000000000" + "3AA60D")]            // стирание всех PLU
    [InlineData("RB", 0u, "01000000", "52420000" + "00002C04003A01000000" + "3AA50D")]               // чтение штрихкода 1
    [InlineData("GM", 1u, "1D000000", "474D0100" + "00002C04003A1D000000" + "3AC20D")]               // ответ «сообщение 29 записано»
    [InlineData("GL", 1u, "41420F00", "474C0100" + "00002C04003A41420F00" + "3A370D")]               // ответ «PLU 1 отдела 1 записан»
    public void Packet_OtherDocumentFrames_MatchBytes(string opcode, uint address, string dataHex, string expectedHex)
    {
        X(CasProtocol.BuildPacket(opcode, address, H(dataHex))).Should().Be(expectedHex);
    }

    [Fact]
    public void ReadStatus_IsEmptyReadCommand_WithValidFrame()
    {
        var frame = CasProtocol.BuildReadStatus();
        X(frame[..10]).Should().Be("524E000000002C0000" + "3A");
        frame[^1].Should().Be(0x0D);
        frame[^3].Should().Be(0x3A);
        CasProtocol.ParseReply(frame).Should().NotBeNull(); // тот же кадр читается как «запрос без данных»
    }

    [Fact]
    public void ParseReply_GoodAndNoGood()
    {
        var good = CasProtocol.ParseReply(H("474C0100" + "00002C04003A41420F003A370D"))!;
        good.Kind.Should().Be(CasProtocol.ReplyKind.Good);
        good.ChecksumOk.Should().BeTrue();
        good.Opcode.Should().Be("GL");
        BitConverter.ToUInt32(good.Data).Should().Be(1_000_001u); // отдел 1 × 1 000 000 + PLU 1 (документ)

        var no = CasProtocol.ParseReply(CasProtocol.BuildPacket("NL", 1, H("41420F0059")))!; // ошибка 89 — память заполнена
        no.Kind.Should().Be(CasProtocol.ReplyKind.NoGood);
        no.ErrorNumber.Should().Be(89);

        CasProtocol.ParseReply(H("0102030405")).Should().BeNull();
        var broken = H("474C0100" + "00002C04003A41420F003A380D"); // сумма не та
        CasProtocol.ParseReply(broken)!.ChecksumOk.Should().BeFalse();
    }

    [Fact]
    public void PluStruct_PutsFieldsAtDocumentOffsets()
    {
        var d = CasProtocol.BuildPluStruct(new CasPlu
        {
            Department = 21, PluNumber = 1234, ItemCode = 98765, Name = "Яблоки", Weighed = true, UnitPriceUnits = 12550, BarcodeNumber = 3, LabelNumber = 7,
        });
        BitConverter.ToUInt16(d, 0).Should().Be(21);
        BitConverter.ToUInt32(d, 2).Should().Be(1234u);
        d[6].Should().Be(1);
        d[7].Should().Be(0xDF); // «Я» в cp1251
        d[12].Should().Be(0xE8); // «и»
        d[13].Should().Be(0); // конец строки
        BitConverter.ToUInt16(d, 94).Should().Be(7);
        BitConverter.ToUInt32(d, 105).Should().Be(98765u);
        BitConverter.ToUInt32(d, 113).Should().Be(12550u);
        BitConverter.ToUInt16(d, 126).Should().Be(3);
        BitConverter.ToUInt32(d, 117).Should().Be(0u); // особая цена не пишется
        CasProtocol.BuildPluStruct(new CasPlu { PluNumber = 1, Weighed = false })[6].Should().Be(3);
    }

    [Fact]
    public void Name_SplitsByWordsAndReplacesKyrgyzLetters()
    {
        var (a, b) = CasProtocol.SplitName("Колбаса варёная докторская высший сорт очень вкусная");
        a.Should().Be("Колбаса варёная докторская высший сорт");
        b.Should().Be("очень вкусная");
        a.Length.Should().BeLessThan(40);
        CasProtocol.SplitName("Өрүк үзүм ң").Name1.Should().Be("Орук узум н");
        CasProtocol.SplitName(new string('Я', 100)).Name1.Length.Should().Be(39);
        CasProtocol.Cp1251('№').Should().Be(0xB9);
        CasProtocol.Cp1251('€').Should().Be((byte)'?');
    }

    [Fact]
    public void Validate_ChecksLimits()
    {
        CasProtocol.Validate(new CasPlu { PluNumber = 1 }).Should().Be(CasPluProblem.None);
        CasProtocol.Validate(new CasPlu { PluNumber = 0 }).Should().Be(CasPluProblem.BadPluNumber);
        CasProtocol.Validate(new CasPlu { PluNumber = 1_000_000 }).Should().Be(CasPluProblem.BadPluNumber);
        CasProtocol.Validate(new CasPlu { PluNumber = 1, ItemCode = 1_000_000 }).Should().Be(CasPluProblem.BadItemCode);
        CasProtocol.Validate(new CasPlu { PluNumber = 1, Department = 0 }).Should().Be(CasPluProblem.BadDepartment);
        CasProtocol.Validate(new CasPlu { PluNumber = 1, UnitPriceUnits = 1_000_000 }).Should().Be(CasPluProblem.PriceDoesNotFit);
        CasProtocol.Validate(new CasPlu { PluNumber = 1, UnitPriceUnits = 999_999 }).Should().Be(CasPluProblem.None);
    }

    [Fact]
    public void Status_ParsesDecimalPoints()
    {
        var d = new byte[30];
        d[1] = 1; d[5] = 3; d[6] = 2;
        BitConverter.GetBytes(12550u).CopyTo(d, 16);
        BitConverter.GetBytes(77u).CopyTo(d, 24);
        BitConverter.GetBytes((ushort)21).CopyTo(d, 28);
        var s = CasProtocol.ParseStatus(d)!;
        s.Stable.Should().BeTrue();
        s.WeightDecimalPoint.Should().Be(3);
        s.PriceDecimalPoint.Should().Be(2);
        s.UnitPrice.Should().Be(12550);
        s.PluNumber.Should().Be(77);
        s.Department.Should().Be(21);
        CasProtocol.ParseStatus(new byte[5]).Should().BeNull();
    }

    // ------------------------------------------------------------------ штрихкод

    [Theory]
    [InlineData("plu", true, "DDIIIIIWWWWWC")]
    [InlineData("code", true, "DDIIIIIIWWWWC")]
    [InlineData("plu", false, "DDIIIIIPPPPPC")]
    [InlineData("code", false, "DDIIIIIIPPPPC")]
    public void BarcodeFormat_Recommended_Has13Chars(string layout, bool byWeight, string expected)
    {
        var f = CasBarcodeFormat.Recommended(layout, byWeight);
        f.Should().Be(expected);
        f.Length.Should().Be(13);
    }

    [Fact]
    public void BarcodeSample_HasEan13CheckAndFits()
    {
        // 20 + 00012 + 00392 → 12 цифр, контрольная по EAN-13
        var sample = CasBarcodeFormat.BuildSample("DDIIIIIWWWWWC", 20, 12, 392)!;
        sample.Should().HaveLength(13);
        sample[..12].Should().Be("200001200392");
        sample[12].Should().Be(CasBarcodeFormat.Ean13Check("200001200392"));
        CasBarcodeFormat.BuildSample("DDIIIIIWWWWWC", 20, 100_000, 1).Should().BeNull(); // код не влезает в 5 цифр
        CasBarcodeFormat.BuildSample("DDIIIIIIWWWWC", 20, 12, 10_000).Should().BeNull(); // вес не влезает в 4 цифры
        CasBarcodeFormat.Ean13Check("200000000000").Should().Be('8'); // 2·1 = 2 → (10 − 2) mod 10
    }

    [Fact]
    public void BarcodeSample_IsReadByKassaParser()
    {
        var sample = CasBarcodeFormat.BuildSample("DDIIIIIWWWWWC", 20, 12, 392)!;
        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();
        parsed.ProductCode.TrimStart('0').Should().Be("12");
        parsed.Value.Should().BeApproximately(0.392, 0.0005);
    }

    // ------------------------------------------------------------------ настройки и файл

    [Fact]
    public void Settings_RoundTripAndNormalize()
    {
        var s = new CasScaleSettings { Ip = " 192.168.1.80 ", Port = 0, Model = "хлам", BarcodePrefix = 5, PricePoint = 9, CsvDecimalSeparator = ";" };
        var back = CasScaleSettings.Parse(s.ToJson());
        back.Ip.Should().Be("192.168.1.80");
        back.Port.Should().Be(CasProtocol.DefaultPort);
        back.Model.Should().Be(CasScaleSettings.ModelCl5000J);
        back.BarcodePrefix.Should().Be(20);
        back.PricePoint.Should().Be(3);
        back.CsvDecimalSeparator.Should().Be(".");
        back.IsNetwork.Should().BeTrue();
        CasScaleSettings.Parse("не json").Port.Should().Be(CasProtocol.DefaultPort);
        CasScaleSettings.IsNetworkModel(CasScaleSettings.ModelCl3000).Should().BeTrue();
        foreach (var file in new[] { CasScaleSettings.ModelCl5500, CasScaleSettings.ModelCl5200, CasScaleSettings.ModelCl7200, CasScaleSettings.ModelLp16 })
            CasScaleSettings.IsNetworkModel(file).Should().BeFalse();
    }

    [Fact]
    public void Csv_Has50Columns_PriceAndEncoding()
    {
        CasCsvWriter.Columns.Length.Should().Be(50);
        CasCsvWriter.Columns.Distinct().Count().Should().Be(50);
        var plu = new CasPlu { Department = 21, PluNumber = 5, ItemCode = 123, Name = "Сыр, \"Российский\"", UnitPriceUnits = 12550, Weighed = true };
        var line = CasCsvWriter.BuildLine(plu, 2);
        line.Should().StartWith("21,5,1,123,\"Сыр, \"\"Российский\"\"\",,0,");
        line.Should().Contain(",125.50,");
        CasCsvWriter.BuildLine(plu, 2, ',').Should().Contain(",\"125,50\","); // 2026-11-14: запятая в цене — в кавычках
        CasCsvWriter.FormatPrice(5, 3, '.').Should().Be("0.005");
        CasCsvWriter.FormatPrice(12550, 0, '.').Should().Be("12550");

        var cp = CasCsvWriter.Build(new[] { new CasPlu { PluNumber = 1, Name = "Яблоки" } }, 2);
        cp.Should().Contain((byte)0xDF);
        cp[^2].Should().Be(13);
        cp[^1].Should().Be(10);
        Encoding.UTF8.GetString(CasCsvWriter.Build(new[] { new CasPlu { PluNumber = 1, Name = "Яблоки" } }, 2, '.', true)).Should().Contain("Яблоки");
    }

    // ------------------------------------------------------------------ сеть: локальная ловушка

    /// <summary>Имитация весов CAS на 127.0.0.1: на «RN» — состояние, на «WL» — «GL» (или «NL» с ошибкой 89 для PLU 2), всё остальное игнорирует.</summary>
    private sealed class FakeCasScale : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public List<byte[]> Received { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool Silent { get; set; }

        public FakeCasScale()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        var stream = client.GetStream();
                        var acc = new List<byte>();
                        var one = new byte[1];
                        while (await stream.ReadAsync(one, _cts.Token) > 0)
                        {
                            acc.Add(one[0]);
                            if (one[0] != 0x0D || CasProtocol.ParseReply(acc.ToArray()) is not { } request)
                                continue;
                            lock (Received)
                                Received.Add(acc.ToArray());
                            acc.Clear();
                            if (Silent)
                                continue;
                            byte[] answer;
                            if (request.Opcode == "RN")
                            {
                                var status = new byte[30];
                                status[1] = 1; status[5] = 3; status[6] = 2;
                                answer = CasProtocol.BuildPacket("WN", 1, status);
                            }
                            else if (BitConverter.ToUInt32(request.Data, 2) == 2)
                                answer = CasProtocol.BuildPacket("NL", 1, new byte[] { 0, 0, 0, 0, 89 });
                            else
                                answer = CasProtocol.BuildPacket("GL", 1, request.Data[..4]);
                            await stream.WriteAsync(answer, _cts.Token);
                        }
                    }
                }
                catch (Exception)
                {
                    // ловушка закрыта
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static string TempLog() => Path.Combine(Path.GetTempPath(), "nurmarket-tests", "cas-test-" + Guid.NewGuid().ToString("N")[..8] + ".log");

    [Fact]
    public async Task Service_ReadStatus_ReadsDecimalPoints_AndWritesNothing()
    {
        CasScaleService.ExchangeLogPathOverride = TempLog();
        using var scale = new FakeCasScale();
        var result = await new CasScaleService("127.0.0.1", scale.Port, 1500, 1500).ReadStatusAsync(CancellationToken.None);
        result.Ok.Should().BeTrue();
        result.Status!.PriceDecimalPoint.Should().Be(2);
        result.Status.WeightDecimalPoint.Should().Be(3);
        scale.Received.Should().HaveCount(1);
        Encoding.ASCII.GetString(scale.Received[0], 0, 2).Should().Be("RN"); // только чтение состояния
        File.ReadAllText(CasScaleService.ExchangeLogPath).Should().Contain("TX 52 4E");
    }

    [Fact]
    public async Task Service_SendPlus_StopsAtRejection_AndReportsScaleError()
    {
        CasScaleService.ExchangeLogPathOverride = TempLog();
        using var scale = new FakeCasScale();
        var plus = new[]
        {
            new CasPlu { PluNumber = 1, Department = 21, ItemCode = 123, Name = "Яблоки", UnitPriceUnits = 12550 },
            new CasPlu { PluNumber = 2, Department = 21, ItemCode = 124, Name = "Груши" },
            new CasPlu { PluNumber = 3, Department = 21, ItemCode = 125, Name = "Сливы" },
        };
        var result = await new CasScaleService("127.0.0.1", scale.Port, 1500, 1500).SendPlusAsync(plus, CancellationToken.None);
        result.Error.Should().Be(CasError.Rejected);
        result.Sent.Should().Be(1);
        result.ScaleErrorNumber.Should().Be(89);
        scale.Received.Should().HaveCount(2); // третья карточка не ушла
        scale.Received.Should().OnlyContain(f => f[0] == (byte)'W' && f[1] == (byte)'L'); // только «записать PLU»
    }

    [Fact]
    public async Task Service_SilentScale_GivesNoReply_AndClosedPortFailsToConnect()
    {
        CasScaleService.ExchangeLogPathOverride = TempLog();
        using var scale = new FakeCasScale { Silent = true };
        var silent = await new CasScaleService("127.0.0.1", scale.Port, 400, 1500).ReadStatusAsync(CancellationToken.None);
        silent.Error.Should().Be(CasError.NoReply);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var closed = await new CasScaleService("127.0.0.1", closedPort, 400, 800).ReadStatusAsync(CancellationToken.None);
        closed.Error.Should().Be(CasError.ConnectFailed);
        CasScaleService.TryCreate("  ").Should().BeNull();
    }
}
