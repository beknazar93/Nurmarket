using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Настройки подключения MySQL для Avalonia-хоста (обёртка над AppSettings).
/// </summary>
public sealed class AvaloniaMySqlConnectionSettings : IMySqlConnectionSettings
{
    private readonly MySqlSettings _settings;

    public AvaloniaMySqlConnectionSettings(MySqlSettings settings) => _settings = settings;

    public bool IsEnabled =>
        _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.ConnectionString);

    public string ConnectionString => _settings.ConnectionString;

    public int CommandTimeoutSeconds => _settings.CommandTimeoutSeconds;
}
