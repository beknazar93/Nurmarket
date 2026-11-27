namespace NurMarketKassa.Services;

/// <summary>Строка накладной для печати ценников: товар, штрихкод, принятое количество и единица. <paramref name="IsNew"/> — товара ещё нет в каталоге (появится после проведения и синхронизации);
/// <paramref name="HasVariants"/> — строка с размерами/вкусами (её этикетки печатаются отдельно, по вариантам).</summary>
public readonly record struct ReceiptLabelSource(string? ProductId, string? Barcode, double Quantity, string? Unit, bool IsNew = false, bool HasVariants = false);

/// <summary>Результат: позиции для окна «Штрихкоды» (товар, штрихкод, копии), сколько строк нового товара ещё нет в каталоге и сколько пропущено (с размерами).</summary>
public sealed record ReceiptLabelPlan(IReadOnlyList<(string ProductId, string? Barcode, int Copies)> Items, int NotInCatalog, int Skipped)
{
    public int CopiesTotal => Items.Sum(i => Math.Min(i.Copies, 99));
    public static ReceiptLabelPlan Empty { get; } = new(Array.Empty<(string, string?, int)>(), 0, 0);
}

/// <summary>2026-11-27 (владелец: «печатать штрихкод и ценники из накладной приёмки»): какие позиции накладной и сколько копий отдать окну «Штрихкоды».
/// Копии = принятое количество (целое 1..99; весовые, дробные и в кг/л/м — 1; больше 99 — 100, окно печати покажет «по 99»). Один товар в двух строках — одна позиция
/// с суммой количества. Строки без товара в каталоге (новые) и с размерами в позиции не входят — только считаются. Деньги не трогает.</summary>
public static class ReceiptLabelPlanner
{
    public static ReceiptLabelPlan Build(IEnumerable<ReceiptLabelSource> lines, Func<string, bool>? mustWeigh = null)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, (string? Barcode, double Qty, string? Unit)>(StringComparer.OrdinalIgnoreCase);
        int notInCatalog = 0, skipped = 0;
        foreach (var l in lines)
        {
            if (double.IsNaN(l.Quantity) || l.Quantity <= 0)
                continue;
            if (l.HasVariants)
            {
                skipped++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(l.ProductId))
            {
                if (l.IsNew)
                    notInCatalog++;
                else
                    skipped++;
                continue;
            }
            var id = l.ProductId!.Trim();
            var code = string.IsNullOrWhiteSpace(l.Barcode) ? null : l.Barcode.Trim();
            if (byId.TryGetValue(id, out var have))
                byId[id] = (have.Barcode ?? code, have.Qty + l.Quantity, have.Unit ?? l.Unit);
            else
            {
                order.Add(id);
                byId[id] = (code, l.Quantity, l.Unit);
            }
        }
        var items = new List<(string ProductId, string? Barcode, int Copies)>();
        foreach (var id in order)
        {
            var v = byId[id];
            var copies = BarcodePrintCatalog.CopiesForQuantity(v.Qty, v.Unit, mustWeigh?.Invoke(id) ?? false, out var capped);
            items.Add((id, v.Barcode, capped ? 100 : copies));
        }
        return new ReceiptLabelPlan(items, notInCatalog, skipped);
    }
}
