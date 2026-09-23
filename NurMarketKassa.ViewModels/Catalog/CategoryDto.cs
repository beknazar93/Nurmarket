namespace NurMarketKassa.ViewModels.Catalog;

public sealed class CategoryDto
{
    public string Name { get; init; } = "";

    public override string ToString() => Name;

    public override bool Equals(object? obj) =>
        obj is CategoryDto other && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
