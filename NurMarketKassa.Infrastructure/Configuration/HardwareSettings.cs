namespace NurMarketKassa.Configuration;

/// <summary>Режим работы с физическим оборудованием (весы, принтер).</summary>
public sealed class HardwareSettings
{
    /// <summary>true — виртуальные весы и принтер (для разработки без COM/LPT).</summary>
    public bool DemoMode { get; init; }
}
