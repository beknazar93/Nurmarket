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

    /// <summary>2026-11-13 (сервер sn1311, «Электроника»): серийные номера, которые реально вернули (после сверки сканом) — уходят в <c>items[].serial_numbers</c>. null/пусто — не присылаем
    /// (частичный возврат без номеров сервер отмечает «первые N по количеству», лучше присылать). Только при флаге компании electronics_2026_11.</summary>
    public IReadOnlyList<string>? SerialNumbers { get; init; }
}
