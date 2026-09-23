namespace NurMarketKassa.Services;

/// <summary>Локально сохранённая корзина (отложенный чек).</summary>
public sealed class DeferredCartEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Label { get; set; } = "";

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Снимок ответа API корзины (JSON).</summary>
    public string CartJson { get; set; } = "{}";

    /// <summary>ID серверной корзины на момент откладывания (для изоляции чеков).</summary>
    public string? ServerCartId { get; set; }
}
