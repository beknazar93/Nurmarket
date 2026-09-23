#nullable enable
namespace NurMarketKassa.Models.Pos;

public sealed class ReturnSaleListItemVm
{
    public required string SaleId { get; init; }
    public required string Summary { get; init; }

    /// <summary>Читаемый номер чека с сервера (receipt_number), если он есть.</summary>
    public string? ReceiptNumber { get; init; }

    // ����� ��������
    public DateTime SaleDate { get; init; }
    public decimal TotalAmount { get; init; }
}