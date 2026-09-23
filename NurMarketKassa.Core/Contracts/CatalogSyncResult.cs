namespace NurMarketKassa.Core.Contracts;

public sealed class CatalogSyncResult
{
    public bool Success { get; init; }

    public int Added { get; init; }

    public int Changed { get; init; }

    public int Deleted { get; init; }

    public string? ErrorMessage { get; init; }

    public static CatalogSyncResult Failed(string message) =>
        new() { Success = false, ErrorMessage = message };

    public static CatalogSyncResult Ok(int added, int changed, int deleted) =>
        new()
        {
            Success = true,
            Added = added,
            Changed = changed,
            Deleted = deleted,
        };
}
