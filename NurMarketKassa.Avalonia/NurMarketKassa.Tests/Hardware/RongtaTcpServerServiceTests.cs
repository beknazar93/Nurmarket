using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>Сквозная проверка RongtaTcpServerService с ПОДДЕЛЬНЫМ клиентом (эмулирует
/// RLS1000 достаточно, чтобы проверить обмен пакетами) — реальные весы этим не заменить,
/// но так хотя бы видно, что сервер отвечает по протоколу и не виснет/не падает.</summary>
public sealed class RongtaTcpServerServiceTests
{
    private static int GetFreeLocalPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task RunOnceAsync_SendsAckThenOnePluRecordPerProduct_ToFakeClient()
    {
        var port = GetFreeLocalPort();
        var products = new List<(int Plu, string Name, double PriceSom)>
        {
            (1, "Яблоки", 120.5),
            (2, "Бананы", 89.0),
        };

        var serverTask = RongtaTcpServerService.RunOnceAsync(port, products, TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();

        // Эмулируем RLS1000: шлём "0201" Start (пример из мануала — "00080201").
        var startPacket = RongtaTcpProtocol.BuildPacket(RongtaTcpProtocol.CmdStart, "");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(startPacket));

        var received = await ReadAllPacketsAsync(stream, expectedCount: 3, TimeSpan.FromSeconds(5));

        received.Should().HaveCount(3);
        received[0].Command.Should().Be(RongtaTcpProtocol.CmdAck, "первым должен прийти ACK на Start");
        received[1].Command.Should().Be(RongtaTcpProtocol.CmdPluSend);
        received[2].Command.Should().Be(RongtaTcpProtocol.CmdPluSend);

        var firstPluLfCode = received[1].Data.Substring(39, 6);
        firstPluLfCode.Should().Be("000001");
        var secondPluLfCode = received[2].Data.Substring(39, 6);
        secondPluLfCode.Should().Be("000002");

        var serverResult = await serverTask;
        serverResult.IsSuccess.Should().BeTrue();
        serverResult.RecordsSent.Should().Be(2);
    }

    [Fact]
    public async Task RunOnceAsync_TimesOut_WhenNothingConnects()
    {
        var port = GetFreeLocalPort();

        var result = await RongtaTcpServerService.RunOnceAsync(
            port, Array.Empty<(int, string, double)>(), TimeSpan.FromMilliseconds(300));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    private static async Task<List<(string Command, string Data)>> ReadAllPacketsAsync(
        NetworkStream stream, int expectedCount, TimeSpan timeout)
    {
        var buffer = new StringBuilder();
        var readBuf = new byte[4096];
        var results = new List<(string, string)>();
        var deadline = DateTime.UtcNow + timeout;

        while (results.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            while (true)
            {
                var parsed = RongtaTcpProtocol.TryParsePacket(buffer);
                if (parsed is null)
                    break;
                results.Add(parsed.Value);
            }

            if (results.Count >= expectedCount)
                break;

            using var readCts = new CancellationTokenSource(timeout);
            int read;
            try
            {
                read = await stream.ReadAsync(readBuf, readCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
                break;

            buffer.Append(Encoding.ASCII.GetString(readBuf, 0, read));
        }

        return results;
    }
}
