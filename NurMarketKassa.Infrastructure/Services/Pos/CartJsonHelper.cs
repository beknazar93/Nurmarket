using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.Services;

/// <summary>Безопасный разбор JSON корзины (защита от [], строк и битых ответов API).</summary>
public static class CartJsonHelper
{
    public static JsonObject CreateEmptyCart() =>
        new()
        {
            ["items"] = new JsonArray(),
        };

    public static JsonObject CreateStagingCart(string? cartJson)
    {
        var root = ParseObjectOrEmpty(cartJson);
        root.Remove("id");
        root["is_staging"] = true;
        EnsureItemsArray(root);
        return root;
    }

    /// <summary>Пытается распарсить JSON-строку в JsonObject. Если это массив, null или битая строка – возвращает false.</summary>
    public static bool TryParseObject(string? json, out JsonObject root)
    {
        root = CreateEmptyCart();
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return false;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                root = obj;
                EnsureItemsArray(root);
                return true;
            }

            // Если пришёл массив ([]) – не падаем, возвращаем false
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static JsonObject ParseObjectOrEmpty(string? json)
    {
        if (TryParseObject(json, out var root))
            return root;

        return CreateEmptyCart();
    }

    public static JsonObject ParseCartRoot(ICartService cart)
    {
        if (!cart.HasCart)
            return CreateEmptyCart();

        // Дополнительная страховка: если Root не объект – вернуть пустую корзину
        if (cart.Root.ValueKind != JsonValueKind.Object)
            return CreateEmptyCart();

        return ParseObjectOrEmpty(cart.Root.GetRawText());
    }

    public static bool TryApplyObjectToCart(ICartService cart, JsonObject root)
    {
        try
        {
            EnsureItemsArray(root);
            using var doc = JsonDocument.Parse(root.ToJsonString());
            cart.SetCart(doc.RootElement);
            return true;
        }
        catch
        {
            cart.Clear();
            return false;
        }
    }

    public static void ApplyStagingToCart(ICartService cart, string? cartJson)
    {
        var root = CreateStagingCart(cartJson);
        if (!TryApplyObjectToCart(cart, root))
            StagingCartService.StartEmpty(cart);
    }

    private static void EnsureItemsArray(JsonObject root)
    {
        if (root["items"] is not JsonArray)
            root["items"] = new JsonArray();
    }
}