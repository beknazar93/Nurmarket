#nullable enable
namespace NurMarketKassa.Configuration;

public sealed class ScaleSettings
{
    public bool Enabled { get; init; }

    public string ComPort { get; init; } = "COM3";

    public int BaudRate { get; init; } = 9600;

    public string? RequestHex { get; init; }

    public int PollMs { get; init; }
}
