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
        JsonDocument? doc = null;
        try
        {
            EnsureItemsArray(root);
            doc = JsonDocument.Parse(root.ToJsonString());

            // CartService.SetCart(JsonElement) would re-parse root.GetRawText() a
            // second time just to own an independent document. Large receipts go
            // through this path on every single line change, so handing over the
            // document we already parsed (instead of parsing it twice) matters
            // once a basket grows into the hundreds of items.
            if (cart is CartService concreteCart)
            {
                concreteCart.SetCartFromOwnedDocument(doc);
                doc = null; // ownership transferred; do not dispose in finally
            }
            else
            {
                cart.SetCart(doc.RootElement);
            }

            return true;
        }
        catch (Exception ex)
        {
            // 2026-09-23. Здесь был `catch { cart.Clear(); return false; }` — без типа, без
            // журнала и, главное, с ПОЛНОЙ очисткой чека. Любой сбой разбора (спецсимвол в
            // названии товара, нехватка памяти на длинном чеке) оставлял кассира с пустым
            // экраном и полной тележкой у покупателя, а в логах — ни строчки о причине.
            //
            // Правильное поведение при неудачной записи — оставить предыдущее состояние чека
            // нетронутым и честно вернуть false: вызывающий покажет ошибку, а набранные позиции
            // никуда не денутся.
            PosLogger.Log($"Не удалось применить изменение к чеку: {ex}", "CRITICAL");
            return false;
        }
        finally
        {
            doc?.Dispose();
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