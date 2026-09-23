using System.Text;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Снимок открытого чека в памяти (без обращения к API).</summary>
public sealed class OpenReceiptSnapshot
{
    public string CartJson { get; set; } = "{}";

    public string? ServerCartId { get; set; }

    public static OpenReceiptSnapshot CreateEmpty() =>
        new() { CartJson = "{}", ServerCartId = null };

    public static OpenReceiptSnapshot Capture(ICartService cart) =>
        new()
        {
            CartJson = cart.HasCart ? CloneCartJson(cart.Root.GetRawText()) : "{}",
            ServerCartId = cart.IsStaging || cart.IsLocalOffline ? null : cart.CartId,
        };

    /// <summary>Независимая копия JSON корзины (без общих ссылок на мутабельные узлы).</summary>
    public static string CloneCartJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return "{}";

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "{}";
            return doc.RootElement.GetRawText();
        }
        catch
        {
            return "{}";
        }
    }

    public static int CountLines(string? cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            return CartDisplayHelper.EnumerateItems(doc.RootElement).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>2026-09-13: необязательный <paramref name="tile"/> — если передан, количество
    /// строк поштучной продажи из упаковки конвертируется в единицы остатка каталога (см.
    /// CartDisplayHelper.LineQuantityInStockUnits), иначе (как раньше) суммируются сырые штуки
    /// строки — сохранено для существующих вызовов, которым остаток каталога не нужен.</summary>
    public static double SumProductQuantity(string? cartJson, string productId, CatalogProductTileVm? tile = null)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            return CartDisplayHelper.EnumerateItems(doc.RootElement)
                .Where(item => string.Equals(
                    CartDisplayHelper.TryProductId(item),
                    productId,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(item => CartDisplayHelper.LineQuantityInStockUnits(item, tile));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Хэш состава позиций для диагностики (не для криптографии).</summary>
    public static int ComputeItemsSignatureHash(string? cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            var sb = new StringBuilder();
            foreach (var it in CartDisplayHelper.EnumerateItems(doc.RootElement))
            {
                sb.Append(CartDisplayHelper.TryItemId(it));
                sb.Append('|');
                sb.Append(CartDisplayHelper.TryProductId(it));
                sb.Append('|');
                sb.Append(CartDisplayHelper.LineQuantity(it).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(';');
            }

            return sb.ToString().GetHashCode(StringComparison.Ordinal);
        }
        catch
        {
            return 0;
        }
    }

    public OpenReceiptSnapshot Clone() =>
        new()
        {
            CartJson = CloneCartJson(CartJson),
            ServerCartId = ServerCartId,
        };

    public void ApplyTo(ICartService cart)
    {
        if (string.IsNullOrWhiteSpace(CartJson) || CartJson == "{}")
        {
            cart.Clear();
            return;
        }

        var json = CloneCartJson(CartJson);
        if (!CartJsonHelper.TryParseObject(json, out var root))
        {
            cart.Clear();
            return;
        }

        if (!string.IsNullOrWhiteSpace(ServerCartId))
            root["id"] = ServerCartId;

        if (!CartJsonHelper.TryApplyObjectToCart(cart, root))
            cart.Clear();
    }

    /// <summary>Отложенный чек: локальный staging без привязки к устаревшему server cart id.</summary>
    public static void ApplyDeferredStaging(ICartService cart, string? cartJson) =>
        CartJsonHelper.ApplyStagingToCart(cart, cartJson);
}
