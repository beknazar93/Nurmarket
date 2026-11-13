using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13: «родной» протокол весов Rongta (UDP 5001) — байт в байт с пакетами RLS1000, пойманными локальной ловушкой (RongtaUdpFixtures).
/// Главное — тип штрихкода карточки: байт 42 записи, двоичный (17 → 0x11).</summary>
public sealed class RongtaUdpProtocolTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private static readonly byte[] Aaaa = Encoding.ASCII.GetBytes("AAAA");

    /// <summary>Базовая карточка тестовых файлов RLS1000: «AAAA», LF = код = n, тип 02, цена 12345, кг, отдел 20, срок 15.</summary>
    private static RongtaUdpPlu Base(int n) => new()
    {
        NameBytes = Aaaa,
        LfCode = n,
        Code = n,
        BarcodeType = 2,
        PriceUnits = 12345,
        WeightUnit = RongtaUdpProtocol.UnitKg,
        Department = 20,
        ShelfLifeDays = 15,
    };

    [Fact]
    public void AllFixtures_HaveValidChecksum()
    {
        foreach (var hex in new[]
                 {
                     RongtaUdpFixtures.Connect_20261113_003558, RongtaUdpFixtures.Disconnect, RongtaUdpFixtures.DownloadPage0200,
                     RongtaUdpFixtures.UpdateTypes07_17_02, RongtaUdpFixtures.UpdateVar1_0, RongtaUdpFixtures.UpdateVar2_4,
                     RongtaUdpFixtures.SystemParamsBt07All17Pcs27, RongtaUdpFixtures.UpdatePrice,
                 })
            RongtaUdpProtocol.ChecksumOk(H(hex)).Should().BeTrue();
    }

    [Fact]
    public void ScaleId_IsIpNumberLast8DigitsBcdReversed()
    {
        // 127.0.0.1 = 2130706433 → «30706433» → 33 64 70 30 (так в пакете RLS1000).
        RongtaUdpProtocol.ScaleId(IPAddress.Parse("127.0.0.1")).Should().Equal(0x33, 0x64, 0x70, 0x30);
        // Весы владельца 192.168.1.87 = 3232235863 → «32235863» → 63 58 23 32.
        RongtaUdpProtocol.ScaleId(IPAddress.Parse("192.168.1.87")).Should().Equal(0x63, 0x58, 0x23, 0x32);
    }

    [Fact]
    public void Connect_And_Disconnect_MatchRls1000()
    {
        var ip = IPAddress.Parse("127.0.0.1");
        RongtaUdpProtocol.BuildConnect(ip, new DateTime(2026, 11, 13, 0, 35, 58)).Should().Equal(H(RongtaUdpFixtures.Connect_20261113_003558));
        RongtaUdpProtocol.BuildDisconnect(ip).Should().Equal(H(RongtaUdpFixtures.Disconnect));
    }

    [Fact]
    public void UpdatePlu_BarcodeTypes07_17_02_MatchRls1000()
    {
        var packets = RongtaUdpProtocol.BuildUpdatePackets(new[]
        {
            Base(1) with { BarcodeType = 7 },
            Base(2) with { BarcodeType = 17 },
            Base(3) with { BarcodeType = 2, PriceUnits = 99999 },
        });
        packets.Should().ContainSingle();
        packets[0].Should().Equal(H(RongtaUdpFixtures.UpdateTypes07_17_02));
        // Тип штрихкода — байт 42 карточки (3 байта заголовка пакета + 64 × слот + 42), двоичный.
        packets[0][3 + 42].Should().Be(7);
        packets[0][3 + 64 + 42].Should().Be(0x11);
        packets[0][3 + 128 + 42].Should().Be(2);
    }

    [Fact]
    public void DownloadPage_UsesTheSameRecordLayout()
    {
        var data = new List<byte>();
        foreach (var plu in new[]
                 {
                     Base(1), Base(2) with { BarcodeType = 7 }, Base(3) with { BarcodeType = 17 },
                     Base(4) with { NameBytes = Encoding.ASCII.GetBytes("BBBBCC"), Code = 4444, Department = 21, PriceUnits = 54321 },
                 })
            data.AddRange(RongtaUdpProtocol.BuildRecord(plu));
        RongtaUdpProtocol.BuildPacket(0x01, 0x0200, data.ToArray()).Should().Equal(H(RongtaUdpFixtures.DownloadPage0200));
    }

    [Fact]
    public void FieldPositions_FirstSeries_MatchRls1000()
    {
        var expected = new[]
        {
            Base(1), Base(2), Base(3) with { PackType = 1 }, Base(4), Base(5) with { PackErrorPercent = 3 },
            Base(6) with { Message1 = 5 }, Base(7) with { Message2 = 6 }, Base(8), Base(9) with { Rebate = 2 }, Base(10) with { Account = 3 },
            Base(11) with { UnitHighNibble = 4 }, Base(12), Base(13), Base(14) with { WeightUnit = 1 }, Base(15),
            Base(16) with { Code = 1234567890 }, Base(17) with { LfCode = 999999 }, Base(18) with { ShelfLifeDays = 365 },
            Base(19) with { PriceUnits = 99999999 }, Base(20) with { Department = 99 },
        };
        var packets = RongtaUdpProtocol.BuildUpdatePackets(expected);
        packets.Should().HaveCount(5);
        packets[0].Should().Equal(H(RongtaUdpFixtures.UpdateVar1_0));
        packets[1].Should().Equal(H(RongtaUdpFixtures.UpdateVar1_1));
        packets[2].Should().Equal(H(RongtaUdpFixtures.UpdateVar1_2));
        packets[3].Should().Equal(H(RongtaUdpFixtures.UpdateVar1_3));
        packets[4].Should().Equal(H(RongtaUdpFixtures.UpdateVar1_4));
    }

    [Fact]
    public void FieldPositions_SecondSeries_MatchRls1000()
    {
        var expected = new[]
        {
            Base(1), Base(2), Base(3) with { TareGrams = 123000 }, Base(4), Base(5),
            Base(6) with { PackWeightGrams = 250000 }, Base(7) with { PackType = 2 }, Base(8) with { PackType = 1 }, Base(9), Base(10) with { UnitHighNibble = 15 },
            Base(11), Base(12), Base(13), Base(14) with { NameBytes = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890") }, Base(15) with { BarcodeType = 99 },
            Base(16) with { WeightUnit = 2 }, Base(17) with { WeightUnit = 3 }, Base(18) with { WeightUnit = 5 }, Base(19) with { WeightUnit = 6 }, Base(20) with { WeightUnit = 7 },
        };
        var packets = RongtaUdpProtocol.BuildUpdatePackets(expected);
        var fixtures = new[] { RongtaUdpFixtures.UpdateVar2_0, RongtaUdpFixtures.UpdateVar2_1, RongtaUdpFixtures.UpdateVar2_2, RongtaUdpFixtures.UpdateVar2_3, RongtaUdpFixtures.UpdateVar2_4 };
        for (var p = 0; p < 5; p++)
        {
            var want = H(fixtures[p]);
            for (var slot = 0; slot < 4; slot++)
            {
                var start = 3 + slot * 64;
                // Карточка 13 — кириллица из TXP, RLS1000 испортила её своей кодовой страницей: сверяем только поля после имени.
                var from = p * 4 + slot == 12 ? start + 36 : start;
                packets[p][from..(start + 64)].Should().Equal(want[from..(start + 64)], $"пакет {p}, слот {slot}");
            }
        }
    }

    [Fact]
    public void FromDahua_MapsPluCellToLf_CodeToCode_PieceToUnit9()
    {
        var weighed = RongtaUdpProtocol.FromDahua(new DahuaTmPlu
        {
            PluNumber = 4000, ProductCode = 9999, Price = 1000m, Name = "ТЕСТ ШК 07", BarcodePrefix = 20, ShelfLifeDays = 3,
        }, priceDecimals: 2, barcodeType: 7);
        weighed.LfCode.Should().Be(4000);
        weighed.Code.Should().Be(9999);
        weighed.PriceUnits.Should().Be(100000);
        weighed.WeightUnit.Should().Be(RongtaUdpProtocol.UnitKg);
        weighed.NameBytes.Should().Equal(RongtaNameCodec.EncodeCardBytes("ТЕСТ ШК 07"));
        var record = RongtaUdpProtocol.BuildRecord(weighed);
        record[36..39].Should().Equal(0x00, 0x40, 0x00); // LF 004000
        record[41].Should().Be(0x20);
        record[42].Should().Be(7);
        record[43..47].Should().Equal(0x00, 0x10, 0x00, 0x00); // 1000,00 сом при 2 знаках
        record[59..64].Should().Equal(0x00, 0x00, 0x00, 0x99, 0x99);

        var piece = RongtaUdpProtocol.FromDahua(new DahuaTmPlu { PluNumber = 5, ProductCode = 5, Price = 10m, WeighMode = DahuaTmWeighMode.Piece, Name = "x" }, 2, 7);
        piece.WeightUnit.Should().Be(RongtaUdpProtocol.UnitPiece);
    }

    [Fact]
    public void LongCyrillicName_IsCutTo36Bytes()
    {
        var plu = RongtaUdpProtocol.FromDahua(new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 1m, Name = new string('Ш', 40) }, 2, 7);
        var record = RongtaUdpProtocol.BuildRecord(plu);
        record[..36].Should().Equal(plu.NameBytes[..36]);
        record[36].Should().Be(0x00);
    }

    [Fact]
    public async Task Service_SendsConnectUpdateDisconnect_AndStopsOnNak()
    {
        var log = Path.Combine(Path.GetTempPath(), "rongta-udp-test-" + Guid.NewGuid().ToString("N") + ".log");
        RongtaUdpScaleService.ExchangeLogPathOverride = log;
        try
        {
            using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)fake.Client.LocalEndPoint!).Port;
            var received = new List<byte[]>();
            var replies = new Queue<byte>(new byte[] { 0x02, 0x02, 0x02, 0x02, 0x02, 0x55, 0x02 });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var server = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await fake.ReceiveAsync(stop.Token); }
                    catch (OperationCanceledException) { return; }
                    lock (received) received.Add(r.Buffer);
                    var b = replies.Count > 0 ? replies.Dequeue() : (byte)0x02;
                    await fake.SendAsync(new[] { b }, 1, r.RemoteEndPoint);
                }
            });

            var service = new RongtaUdpScaleService(IPAddress.Loopback, port, replyTimeoutMs: 500, retries: 1);
            var plus = Enumerable.Range(1, 5).Select(n => Base(n) with { BarcodeType = 7 }).ToList();
            var ok = await service.UpdatePlusAsync(plus, new DateTime(2026, 11, 13, 1, 2, 3), CancellationToken.None);
            ok.Ok.Should().BeTrue();
            ok.Packets.Should().Be(2);
            lock (received)
            {
                received.Should().HaveCount(4);
                received[0][..3].Should().Equal(0x0E, 0x01, 0x00);
                received[1][0].Should().Be(0x0B);
                received[2][0].Should().Be(0x0B);
                received[3][..3].Should().Equal(0x0E, 0x02, 0x00);
                received.Should().OnlyContain(p => RongtaUdpProtocol.ChecksumOk(p));
                received.Clear();
            }

            // Отказ (0x55) на первый пакет карточек — остановка, но «отключиться» всё равно уходит.
            var rejected = await service.UpdatePlusAsync(plus, DateTime.Now, CancellationToken.None);
            rejected.Error.Should().Be(RongtaUdpError.Rejected);
            lock (received)
            {
                received.Should().HaveCount(3);
                received[2][..3].Should().Equal(0x0E, 0x02, 0x00);
            }
            stop.Cancel();
            await server;
        }
        finally
        {
            RongtaUdpScaleService.ExchangeLogPathOverride = null;
            try { File.Delete(log); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Service_NoScale_ReportsNoReply()
    {
        var log = Path.Combine(Path.GetTempPath(), "rongta-udp-test-" + Guid.NewGuid().ToString("N") + ".log");
        RongtaUdpScaleService.ExchangeLogPathOverride = log;
        try
        {
            // Порт, который никто не слушает (занимаем и сразу закрываем).
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            var service = new RongtaUdpScaleService(IPAddress.Loopback, port, replyTimeoutMs: 200, retries: 1);
            var result = await service.ConnectOnlyAsync(DateTime.Now, CancellationToken.None);
            result.Error.Should().Be(RongtaUdpError.NoReply);
        }
        finally
        {
            RongtaUdpScaleService.ExchangeLogPathOverride = null;
            try { File.Delete(log); } catch (IOException) { }
        }
    }

    /// <summary>2026-11-13: этикетка владельца «МясоЎЎБаранаЎЎJЁмякотьJ®» — в карточке UDP имя теперь обычный Windows-1251:
    /// пробел 0x20, скобки 0x28/0x29, без пар A1 A1 / A3 xx.</summary>
    [Fact]
    public void Card_name_is_plain_cp1251_with_ascii_spaces_and_brackets()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        var bytes = RongtaNameCodec.EncodeCardBytes("Мясо Барана (мякоть)");
        bytes.Should().Equal(cp1251.GetBytes("Мясо Барана (мякоть)"));
        bytes.Should().NotContain((byte)0xA1);
        bytes.Should().NotContain((byte)0xA3);
        RongtaNameCodec.EncodeCardBytes("Курага сушёная я").Last().Should().Be(cp1251.GetBytes("Я")[0]);
        RongtaNameCodec.EncodeCardBytes(new string('А', 50)).Length.Should().Be(36);
        var card = RongtaUdpProtocol.FromDahua(new DahuaTmPlu { PluNumber = 5, ProductCode = 5, Price = 800m, Name = "Мясо Барана (мякоть)", BarcodePrefix = 20 }, priceDecimals: 2, barcodeType: 7);
        var record = RongtaUdpProtocol.BuildRecord(card);
        record[..20].Should().Equal(cp1251.GetBytes("Мясо Барана (мякоть)"));
        record[20].Should().Be(0x20); // остаток поля имени — пробелы
    }
}
