using System.Text.Json.Serialization;
using LinqToDB.Mapping;

namespace NurMarketKassa.Models;

/// <summary>Товар в PostgreSQL (таблица products).</summary>
[Table(Name = "products")]
public sealed class Product
{
    [PrimaryKey]
    [Column(Name = "id")]
    public string Id { get; set; } = string.Empty;

    [Column(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [Column(Name = "barcode")]
    public string? Barcode { get; set; }

    [Column(Name = "price")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal Price { get; set; }

    [Column(Name = "quantity")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double? Quantity { get; set; }

    /// <summary>true — весовые (кг), false — штучные.</summary>
    [Column(Name = "is_weight")]
    public bool IsWeight { get; set; }

    [Column(Name = "image")]
    public string? ImageUrl { get; set; }

    [Column(Name = "category")]
    public string? Category { get; set; }

    [Column(Name = "brand")]
    public string? Brand { get; set; }

    [Column(Name = "unit")]
    public string? Unit { get; set; }

    [Column(Name = "is_favorite")]
    public bool IsFavorite { get; set; }
}
