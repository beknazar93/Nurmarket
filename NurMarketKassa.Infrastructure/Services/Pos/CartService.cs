using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// Потокобезопасная реализация <see cref="ICartService"/>.
/// <para>
/// POS-система работает асинхронно (фоновая синхронизация, сканеры штрихкодов в отдельных
/// потоках, весы), поэтому всё чтение и изменение состояния корзины сериализуется одним
/// монитором <see cref="_sync"/>. Типизированные проекции (<see cref="Items"/>, итоги)
/// возвращают неизменяемые снимки и безопасны для чтения из любого потока.
/// </para>
/// </summary>
public sealed class CartService : ICartService, IDisposable
{
    private readonly object _sync = new();
    private readonly CartSession _session = new();

    public string? CartId
    {
        get { lock (_sync) return _session.CartId; }
    }

    public bool IsLocalOffline
    {
        get { lock (_sync) return _session.IsLocalOffline; }
    }

    public bool IsStaging
    {
        get { lock (_sync) return _session.IsStaging; }
    }

    public bool HasCart
    {
        get { lock (_sync) return _session.HasCart; }
    }

    public bool CanRefresh
    {
        get { lock (_sync) return _session.CanRefresh; }
    }

    public JsonElement Root
    {
        get { lock (_sync) return _session.Root; }
    }

    public string GetRawText()
    {
        lock (_sync)
            return _session.HasCart ? _session.Root.GetRawText() : "{}";
    }

    public IReadOnlyList<CartItem> Items
    {
        get
        {
            lock (_sync)
            {
                if (!_session.HasCart)
                    return Array.Empty<CartItem>();

                return CartDisplayHelper.EnumerateItems(_session.Root)
                    .Select(ProjectItem)
                    .ToArray();
            }
        }
    }

    public int LineCount
    {
        get { lock (_sync) return _session.HasCart ? CartDisplayHelper.EnumerateItems(_session.Root).Count() : 0; }
    }

    public double TotalQuantity
    {
        get
        {
            lock (_sync)
            {
                if (!_session.HasCart)
                    return 0;
                return CartDisplayHelper.EnumerateItems(_session.Root)
                    .Sum(CartDisplayHelper.LineQuantity);
            }
        }
    }

    public decimal TotalAmount
    {
        get { lock (_sync) return _session.HasCart ? (decimal)CartTotalsCalculator.Calculate(_session.Root).TotalDue : 0m; }
    }

    public decimal TotalDiscount
    {
        get
        {
            lock (_sync)
            {
                if (!_session.HasCart)
                    return 0m;
                var totals = CartTotalsCalculator.Calculate(_session.Root);
                return (decimal)(totals.LineDiscounts + totals.OrderDiscount);
            }
        }
    }

    public void SetCart(JsonElement root)
    {
        lock (_sync)
            _session.SetCart(root);
    }

    public void SetLocalOfflineCart(string cartJson)
    {
        lock (_sync)
            _session.SetLocalOfflineCart(cartJson);
    }

    public void Clear()
    {
        lock (_sync)
            _session.Clear();
    }

    public void AddItem(CatalogProductTileVm product, double quantity)
    {
        ArgumentNullException.ThrowIfNull(product);
        lock (_sync)
            ReceiptSnapshotCartEditor.AddProduct(this, product, quantity);
    }

    public void UpdateQuantity(string itemId, double quantity)
    {
        if (string.IsNullOrEmpty(itemId))
            throw new ArgumentException("Не указан идентификатор строки корзины.", nameof(itemId));
        lock (_sync)
            ReceiptSnapshotCartEditor.UpdateLineQuantity(this, itemId, quantity);
    }

    public void RemoveItem(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            throw new ArgumentException("Не указан идентификатор строки корзины.", nameof(itemId));
        lock (_sync)
            ReceiptSnapshotCartEditor.RemoveLine(this, itemId);
    }

    public void ResetForNewReceipt() => LoadEmptyStagingCart();

    public void Dispose()
    {
        lock (_sync)
            _session.Dispose();
    }

    private static CartItem ProjectItem(JsonElement line)
    {
        var quantity = CartDisplayHelper.LineQuantity(line);
        var unitPrice = CartDisplayHelper.UnitPrice(line);
        var gross = quantity * unitPrice;
        var discount = ReadLineDiscount(line, gross);
        var (discountPercent, fixedDiscountAmount) = ReadDiscountDefinition(line);

        return new CartItem(
            Id: CartDisplayHelper.TryItemId(line),
            ProductId: CartDisplayHelper.TryProductId(line),
            Barcode: CartDisplayHelper.TryBarcode(line),
            Name: CartDisplayHelper.ItemName(line),
            Quantity: quantity,
            UnitPrice: (decimal)unitPrice,
            LineDiscount: (decimal)discount,
            LineTotal: (decimal)Math.Max(0, gross - discount),
            MustWeigh: CartDisplayHelper.LineMustWeigh(line),
            DiscountPercent: discountPercent,
            FixedDiscountAmount: fixedDiscountAmount);
    }

    public void LoadEmptyStagingCart()
    {
        lock (_sync)
        {
            // Запоминаем, был ли чек отложенным
            bool wasStaging = _session.IsStaging;

            // Очищаем корзину (это сбросит IsStaging, но мы восстановим)
            _session.Clear();

            // Загружаем валидную пустую корзину
            var empty = CartJsonHelper.CreateEmptyCart();
            using var doc = JsonDocument.Parse(empty.ToJsonString());
            _session.SetCart(doc.RootElement);

            // Восстанавливаем флаг отложенного чека
            if (wasStaging)
                _session.SetStaging(true);
        }
    }

    private static double ReadLineDiscount(JsonElement line, double gross)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return 0;

        if (line.TryGetProperty("discount_total", out var dt)
            && JsonNumericReader.TryToDouble(dt, out var total) && total > 0)
            return total;

        if (line.TryGetProperty("discount_percent", out var dp)
            && JsonNumericReader.TryToDouble(dp, out var pct) && pct > 0)
            return gross * pct / 100.0;

        return 0;
    }

    private static (decimal? Percent, decimal? FixedAmount) ReadDiscountDefinition(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return (null, null);

        if (line.TryGetProperty("discount_percent", out var percent)
            && JsonNumericReader.TryToDouble(percent, out var percentValue)
            && percentValue > 0)
            return ((decimal)percentValue, null);

        if (line.TryGetProperty("discount_total", out var total)
            && JsonNumericReader.TryToDouble(total, out var totalValue)
            && totalValue > 0)
            return (null, (decimal)totalValue);

        return (null, null);
    }
}
