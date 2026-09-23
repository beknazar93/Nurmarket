namespace NurMarketKassa.Core.Contracts;

public interface IMySqlConnectionSettings
{
    bool IsEnabled { get; }

    string ConnectionString { get; }

    int CommandTimeoutSeconds { get; }
}
