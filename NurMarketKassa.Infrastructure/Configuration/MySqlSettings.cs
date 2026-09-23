namespace NurMarketKassa.Configuration;

/// <summary>Локальная MySQL для аудита и мониторинга работы кассы.</summary>
public sealed class MySqlSettings
{
    public bool Enabled { get; init; }

    public string ConnectionString { get; init; } =
        "Server=127.0.0.1;Port=3306;Database=nurmarket_kassa;User Id=root;Password=;Charset=utf8mb4;";

    public int CommandTimeoutSeconds { get; init; } = 15;
}
