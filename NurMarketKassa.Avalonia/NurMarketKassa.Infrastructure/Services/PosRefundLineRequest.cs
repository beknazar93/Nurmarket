namespace NurMarketKassa.Services;

/// <summary>Позиция чека для оформления возврата через API.</summary>
public sealed class PosRefundLineRequest
{
    public required string LineId { get; init; }

    public string? ProductId { get; init; }

    public required string Title { get; init; }

    public double Quantity { get; init; }

    /// <summary>Количество в чеке до возврата (для частичного PATCH).</summary>
    public double OriginalQuantity { get; init; }

    public string? UnitPrice { get; init; }
}
