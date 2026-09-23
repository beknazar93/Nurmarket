#nullable enable
namespace NurMarketKassa.Models.Pos;

public sealed class ReturnSaleListItemVm
{
    public required string SaleId { get; init; }
    public required string Summary { get; init; }

    // Новые свойства
    public DateTime SaleDate { get; init; }
    public decimal TotalAmount { get; init; }
}