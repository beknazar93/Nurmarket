using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-11 (ТЗ «TM-30F — цена на этикетке в 10 раз больше»): касса слала цену с 2 знаками, а в весах Price point = 1. Настройка «Знаков после
/// запятой в цене на весах (Price point)» управляет числом в поле mprice команды «!0V» (сом × 10^знаков) и пределом цены. Отправка — на заглушку сокета
/// (локальный TCP), настоящие весы не трогаем.</summary>
public sealed class DahuaTmPricePointTests
{
    private static DahuaTmPlu Plu(decimal price) => new() { PluNumber = 1, ProductCode = 12, Price = price, Name = "Яблоки" };

    /// <summary>Поле mprice — 6 цифр сразу после «A» и 7 цифр кода товара.</summary>
    private static string PriceField(string command) => command.Substring("!0V0001A".Length + 7, 6);

    [Theory]
    [InlineData(0, "000250")]
    [InlineData(1, "002500")]
    [InlineData(2, "025000")]
    [InlineData(3, "250000")]
    public void Price_field_follows_price_point(int pricePoint, string expected)
    {
        DahuaTmProtocol.ScalePrice(250m, pricePoint).Should().Be(long.Parse(expected));
        PriceField(DahuaTmProtocol.BuildPluCommand(Plu(250m), pricePoint)).Should().Be(expected);
    }

    [Fact]
    public void Fractional_price_is_rounded_away_from_zero_at_the_chosen_point()
    {
        DahuaTmProtocol.ScalePrice(123.45m, 1).Should().Be(1235, "123,45 при 1 знаке — 123,5");
        DahuaTmProtocol.ScalePrice(123.45m, 0).Should().Be(123);
        DahuaTmProtocol.ScalePrice(123.45m, 2).Should().Be(12345);
    }

    [Theory]
    [InlineData(9_999.99, 2, true)]
    [InlineData(10_000, 2, false)]
    [InlineData(99_999.9, 1, true)]
    [InlineData(100_000, 1, false)]
    [InlineData(999_999, 0, true)]
    [InlineData(999.999, 3, true)]
    [InlineData(1_000, 3, false)]
    public void Limit_is_counted_by_the_same_price_point(double price, int pricePoint, bool fits)
    {
        var p = (decimal)price;
        ScalePriceLimits.Fits(p, ScalePriceLimits.DahuaMaxUnits, pricePoint).Should().Be(fits);
        (DahuaTmProtocol.Validate(Plu(p), pricePoint) == DahuaTmPluProblem.None).Should().Be(fits, "проверка записи и предел — одно правило");
    }

    [Fact]
    public void Mismatch_kassa_2_scale_1_prints_ten_times_more_and_matching_points_print_the_price()
    {
        DahuaTmProtocol.LabelPrice(250m, kassaPricePoint: 2, scalePricePoint: 1).Should().Be(2500m, "жалоба клиента: на этикетке в 10 раз больше");
        DahuaTmProtocol.LabelPrice(250m, kassaPricePoint: 1, scalePricePoint: 1).Should().Be(250m, "настройку уменьшили на 1 — цена верная");
        DahuaTmProtocol.LabelPrice(250m, kassaPricePoint: 0, scalePricePoint: 2).Should().Be(2.5m, "в 100 раз меньше");
        for (var pp = 0; pp <= 3; pp++)
            DahuaTmProtocol.LabelPrice(123.4m, pp, pp).Should().Be(Math.Round(123.4m, pp, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Send_to_socket_stub_carries_the_price_scaled_by_the_setting()
    {
        // заглушка весов: принимает одну строку и отвечает «принято» (как TM-30F: «0v0001a\r\n\x03»)
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n"))
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0)
                    break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            var reply = Encoding.ASCII.GetBytes("0v0001a\r\n\u0003");
            await stream.WriteAsync(reply);
            return sb.ToString();
        });

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var line = DahuaTmProtocol.BuildPluCommand(Plu(250m), priceDecimals: 1);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(line));
            var buf = new byte[64];
            var n = await tcp.GetStream().ReadAsync(buf);
            DahuaTmProtocol.ClassifyReply(Encoding.ASCII.GetString(buf, 0, n)).Should().Be(DahuaTmProtocol.ReplyKind.Ack);
        }
        var received = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        PriceField(received).Should().Be("002500", "Price point 1: 250 сом → 2500, весы с Price point 1 напечатают 250,0");
        listener.Stop();
    }
}
