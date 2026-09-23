using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Текущая POS-корзина в памяти (аналог session["cart"] / cart_id).</summary>
public sealed class CartSession : IDisposable
{
    private JsonDocument? _doc;

    public string? CartId { get; private set; }

    public bool IsLocalOffline { get; private set; }

    /// <summary>Локальный черновик чека до синхронизации с сервером (после откладывания).</summary>
    public bool IsStaging { get; private set; }

    public JsonElement Root => _doc?.RootElement ?? default;

    /// <summary>Есть данные корзины для отображения.</summary>
    public bool HasCart => _doc != null;

    /// <summary>Можно вызвать GET /pos/carts/{id}/ (или локальная корзина в офлайне).</summary>
    public bool CanRefresh => !IsStaging && !IsLocalOffline && !string.IsNullOrEmpty(CartId);

    public void SetCart(JsonElement root)
    {
        _doc?.Dispose();
        IsStaging = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("is_staging", out var stagingFlag)
                    && stagingFlag.ValueKind == JsonValueKind.True;
        IsLocalOffline = !IsStaging
                         && root.ValueKind == JsonValueKind.Object
                         && root.TryGetProperty("is_local_offline", out var offlineFlag)
                         && offlineFlag.ValueKind == JsonValueKind.True;
        CartId = IsStaging ? null : CartDisplayHelper.TryCartId(root);
        _doc = JsonDocument.Parse(root.GetRawText());
    }

    public void SetStaging(bool staging)
    {
        lock (this) // или без lock, если класс сам не потокобезопасен
        {
            IsStaging = staging;
        }
    }

    public void SetLocalOfflineCart(string cartJson)
    {
        _doc?.Dispose();
        using var doc = JsonDocument.Parse(cartJson);
        var root = doc.RootElement;
        CartId = CartDisplayHelper.TryCartId(root) ?? "local-" + Guid.NewGuid().ToString("N");
        IsLocalOffline = true;
        IsStaging = false;
        _doc = JsonDocument.Parse(cartJson);
    }

    public void Clear()
    {
        _doc?.Dispose();
        _doc = null;
        CartId = null;
        IsLocalOffline = false;
        IsStaging = false;
        CartDisplayHelper.ClearWeighedProductDisplayHints();
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }
}
