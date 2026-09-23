using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Models
{
    /// <summary>Стандартная обёртка API для списков (results / count).</summary>
    public class ApiListResponse<T>
    {
        [JsonPropertyName("results")]
        public List<T>? Results { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }
    }

    /// <summary>Упрощённое представление товара из поиска / каталога.</summary>
    public class ProductDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("price")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public decimal? Price { get; set; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("brand")]
        public string? Brand { get; set; }

        [JsonPropertyName("image")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? Quantity { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; }

        [JsonPropertyName("must_weigh")]
        public bool MustWeigh { get; set; }

        [JsonPropertyName("is_weight")]
        public bool IsWeight { get; set; }

        [JsonPropertyName("is_weight_product")]
        public bool IsWeightProduct { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("stock_quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? StockQuantity { get; set; }

        [JsonPropertyName("stock_weight")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
        public double? StockWeight { get; set; }

        [JsonPropertyName("is_favorite")]
        public bool IsFavorite { get; set; }

        public bool ResolvesMustWeigh()
        {
            if (MustWeigh || IsWeight || IsWeightProduct)
                return true;
            var unit = (Unit ?? "").Trim().ToLowerInvariant();
            return unit is "кг" or "kg" or "kг";
        }

        public double ResolvesQuantity(bool mustWeigh)
        {
            if (mustWeigh)
            {
                if (StockWeight is > 0)
                    return StockWeight.Value;
                if (StockQuantity is not null)
                    return StockQuantity.Value;
                return Quantity ?? 0;
            }

            if (StockQuantity is not null)
                return StockQuantity.Value;
            if (Quantity is not null)
                return Quantity.Value;
            if (StockWeight is not null)
                return StockWeight.Value;
            return 0;
        }
    }

    /// <summary>GET /api/users/company/</summary>
    public sealed class CompanyDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("inn")]
        public string? Inn { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }
}