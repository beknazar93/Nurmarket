using System;

namespace NurMarketKassa.Models;

public class FilterCriteria
{
    public string? SearchQuery { get; set; }
    public string? CatalogKind { get; set; }
    public double? PriceMin { get; set; }
    public double? PriceMax { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Category { get; set; }
    public string? Brand { get; set; }
    public string? Client { get; set; }
    public string? Status { get; set; }
    public string? HotkeyGroup { get; set; }
    public bool OnlyWeight { get; set; }
    public bool OnlyPiece { get; set; }
    public bool OnlyInStock { get; set; }
    public bool OnlyFavorite { get; set; }
}
