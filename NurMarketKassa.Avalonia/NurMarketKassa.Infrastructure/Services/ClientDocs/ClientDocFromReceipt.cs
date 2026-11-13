namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ3): строка текущего чека для «Счёта из этого чека» — товар, количество, цена за единицу
/// ДО скидки строки и сумма строки ПОСЛЕ скидки (как на экране кассы).</summary>
public sealed record ReceiptDocLine(string ProductId, string Name, string Unit, double Qty, double UnitPrice, double LineTotal);

/// <summary>
/// 2026-11-12 (КТ3): «Счёт из этого чека» для организации (школа, офис): строки открытого чека → строки документа клиента
/// (<see cref="ClientDoc"/>, вид «Счёт»). Чистая логика без окон и сети: шапку документа (номер, шаблон, срок) делает вызывающий,
/// покупателя (ИНН, ОсОО) выбирают в редакторе документа. Итог документа равен итогу чека до тыйына: цена строки после скидки берётся из
/// суммы строки, а скидка на весь чек (итог чека меньше суммы строк) — скидкой документа, как у «Накладной по чеку».
/// </summary>
public static class ClientDocFromReceipt
{
    /// <summary>Добавить строки чека в документ и выставить скидку документа так, чтобы <see cref="ClientDoc.Total"/> равнялся <paramref name="receiptTotal"/>.
    /// Строки с нулевым количеством пропускаются.</summary>
    public static void Fill(ClientDoc doc, IEnumerable<ReceiptDocLine> lines, double receiptTotal)
    {
        foreach (var src in lines.Where(l => l.Qty > 0))
        {
            var line = new ClientDocLine
            {
                ProductId = src.ProductId ?? "",
                Name = src.Name ?? "",
                Unit = string.IsNullOrWhiteSpace(src.Unit) ? "шт" : src.Unit,
                Qty = src.Qty,
            };
            var net = ClientDoc.Round2(src.LineTotal);
            var price = ClientDoc.Round2(net / src.Qty);
            // Цена за единицу после скидки с округлением до копеек может дать сумму строки на тыйын мимо (3 × 33,33 ≠ 100,00):
            // тогда цена остаётся точной (сумма строки = сумме на экране кассы).
            if (Math.Abs(ClientDoc.Round2(src.Qty * price) - net) > 0.004)
                price = net / src.Qty;
            line.Price = price;
            var gross = ClientDoc.Round2(src.UnitPrice * src.Qty);
            if (src.UnitPrice > 0 && gross - net > 0.004)
            {
                line.ListPrice = ClientDoc.Round2(src.UnitPrice);
                line.DiscountPercent = Math.Round((1 - net / gross) * 100.0, 2);
            }
            doc.Lines.Add(line);
        }
        var diff = ClientDoc.Round2(doc.Subtotal - receiptTotal);
        doc.Discount = diff > 0.004 ? diff : 0;
    }
}
