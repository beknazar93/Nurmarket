using System.Text;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>Сверка кодека RongtaTcpProtocol с примером прямо из официального мануала
/// Rongta ("Label Scale Software User Manual", раздел 2.4 "Data packet format") — единственная
/// часть этого протокола, которую можно проверить без реальных весов.</summary>
public sealed class RongtaTcpProtocolTests
{
    [Fact]
    public void BuildPacket_MatchesManualStartExample()
    {
        // Мануал: "Start command: 00080201" — длина 0008 (=8 символов всего пакета), команда 0201, без данных.
        RongtaTcpProtocol.BuildPacket(RongtaTcpProtocol.CmdStart, "").Should().Be("00080201");
    }

    [Fact]
    public void BuildAck_MatchesManualResponseExampleShape()
    {
        // Мануал: "Response Command: 0022010202100000010000" — длина 0022(=22), команда 0102,
        // данные "02100000010000" = код 0210 + fresh 000001 + error 0000. Здесь код 0210 в
        // примере — просто иллюстрация формата ACK, а не буквально ответ на 0201; проверяем
        // именно общую форму (длина/код/раскладку данных), эхо-код настраиваемый.
        var ack = RongtaTcpProtocol.BuildAck("0210", "000001");
        ack.Should().Be("0022010202100000010000");
    }

    [Fact]
    public void TryParsePacket_RoundTripsBuiltPacket()
    {
        var packet = RongtaTcpProtocol.BuildPacket(RongtaTcpProtocol.CmdStart, "");
        var buffer = new StringBuilder(packet);

        var parsed = RongtaTcpProtocol.TryParsePacket(buffer);

        parsed.Should().NotBeNull();
        parsed!.Value.Command.Should().Be(RongtaTcpProtocol.CmdStart);
        parsed.Value.Data.Should().Be("");
        buffer.Length.Should().Be(0, "разобранный пакет должен быть удалён из буфера");
    }

    [Fact]
    public void TryParsePacket_ReturnsNull_WhenPacketIncomplete()
    {
        var buffer = new StringBuilder("0022010202100"); // короче заявленной длины 0022

        RongtaTcpProtocol.TryParsePacket(buffer).Should().BeNull();
    }

    [Fact]
    public void BuildPluRecord_ProducesExactly100CharDataPayload()
    {
        var packet = RongtaTcpProtocol.BuildPluRecord(42, "Яблоки", 120.5);

        // 8 (заголовок: длина+команда) + 100 (данные PLU-записи, см. сумму ширин полей в мануале).
        packet.Length.Should().Be(108);
        packet.Substring(4, 4).Should().Be(RongtaTcpProtocol.CmdPluSend);
    }

    [Fact]
    public void BuildPluRecord_EncodesPluIntoLfCodeField_AndTransliteratesName()
    {
        var packet = RongtaTcpProtocol.BuildPluRecord(42, "Яблоки", 120.5);
        var data = packet[8..];

        // Operate(1) + Rank(2) + Name(36) = 39 символов до поля LFCode/PLU.
        data.Substring(0, 1).Should().Be("I");
        var lfCode = data.Substring(39, 6);
        lfCode.Should().Be("000042");

        var name = data.Substring(3, 36).TrimEnd();
        name.Should().Be("Yabloki");
    }

    [Fact]
    public void BuildPluRecord_EncodesPriceWithoutDecimalPoint()
    {
        // Мануал: "12.34 will show as 1234 (=12.34*100)".
        var packet = RongtaTcpProtocol.BuildPluRecord(1, "Test", 12.34);
        var data = packet[8..];

        // Unit price начинается после Operate(1)+Rank(2)+Name(36)+LFCode(6)+Code(10)+BarcodeType(2) = 57.
        var unitPrice = data.Substring(57, 8);
        unitPrice.Should().Be("00001234");
    }
}
