#nullable enable
namespace NurMarketKassa.Configuration;

public sealed class UpdateSettings
{
    public string? ManifestUrl { get; init; }

    public bool CheckOnStartup { get; init; } = true;

    public int MinHoursBetweenChecks { get; init; } = 4;
}
