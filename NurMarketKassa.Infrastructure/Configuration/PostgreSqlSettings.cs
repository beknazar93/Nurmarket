namespace NurMarketKassa.Configuration;

/// <summary>PostgreSQL для поиска и чтения каталога товаров (LINQ2DB).</summary>
public sealed class PostgreSqlSettings
{
    public bool Enabled { get; init; }

    public string ConnectionString { get; init; } =
        "Host=127.0.0.1;Port=5432;Database=nurmarket;Username=postgres;Password=;";

    public int CommandTimeoutSeconds { get; init; } = 30;
}
