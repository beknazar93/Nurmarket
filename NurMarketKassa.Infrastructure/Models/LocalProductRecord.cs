#nullable enable

namespace NurMarketKassa.Models;

public sealed class LocalProductRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Price { get; set; }
    public string? Barcode { get; set; }
    public double Stock { get; set; }
    public string Unit { get; set; } = "шт";
    public bool IsFavorite { get; set; }
    public bool MustWeigh { get; set; }
    public string? ImageUrl { get; set; }
    public string? Category { get; set; }
    public string? Brand { get; set; }
    public double PurchasePrice { get; set; }
}
