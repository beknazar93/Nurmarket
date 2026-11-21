#nullable enable
namespace NurMarketKassa.Services;

/// <summary>Строка готового чека: текст без служебных знаков и её стиль.</summary>
public readonly record struct RenderedReceiptLine(string Text, bool Bold, int Size);

/// <summary>Готовый чек для предпросмотра: ширина в знаках и строки «как напечатает принтер».</summary>
public sealed record RenderedReceipt(int Width, IReadOnlyList<RenderedReceiptLine> Lines, string PrinterText);

/// <summary>2026-11-21 (редактор чека): образец чека для предпросмотра и тест-печати. Собирается НАСТОЯЩИМ построителем чека (CartReceiptTextBuilder)
/// по шаблону-черновику и проходит тот же конвейер подготовки к принтеру (<see cref="ReceiptPrintPipeline"/>), что и печать, — предпросмотр и бумага совпадают.
/// Название магазина, ИНН, адрес, кассир и включённые пункты берутся из настоящих настроек; товары — образец.</summary>
public static class ReceiptSample
{
    /// <summary>Образец корзины: длинное название (переносится), весовой товар, скидка на позицию.</summary>
    public const string SampleCartJson = """
        {
          "receipt_number": "20261121-0042",
          "items": [
            { "product_name": "Капсула First Fresh 1шт*18 с длинным названием для проверки переноса", "quantity": 2, "unit_price": 18, "line_total": 36 },
            { "product_name": "Хлеб белый", "quantity": 1, "unit_price": 30, "line_total": 30 },
            { "product_name": "Колбаса Докторская", "product_id": "p3", "is_weight": true, "quantity": 0.356, "unit_price": 520, "line_total": 185.12, "discount_total": 5.12 }
          ]
        }
        """;

    /// <summary>Текст образца чека для принтера (с метками шаблона), по шаблону <paramref name="template"/> и ширине ленты <paramref name="paperWidthMm"/>.</summary>
    public static string BuildText(ReceiptTemplate template, int paperWidthMm)
    {
        using var draft = ReceiptTemplateEngine.UseDraft(template);
        var previous = ReceiptLayout.WidthOverride;
        ReceiptLayout.WidthOverride = ReceiptPaperProfile.GetCharWidth(paperWidthMm);
        try
        {
            return CartReceiptTextBuilder.BuildSimpleReceipt(SampleCartJson, null, "cash", "500", new DateTime(2026, 11, 21, 14, 30, 0));
        }
        finally
        {
            ReceiptLayout.WidthOverride = previous;
        }
    }

    /// <summary>Предпросмотр: то, что уйдёт на принтер (ESC/POS-конвейер), по строкам.</summary>
    public static RenderedReceipt Render(ReceiptTemplate template, int paperWidthMm)
    {
        var width = ReceiptPaperProfile.GetCharWidth(paperWidthMm);
        var text = BuildText(template, paperWidthMm);
        var prepared = ReceiptPrintPipeline.Prepare(text, width);
        var lines = new List<RenderedReceiptLine>();
        foreach (var raw in prepared.Split('\n'))
        {
            var clean = ReceiptLineStyle.Parse(raw, out var bold, out var size);
            lines.Add(new RenderedReceiptLine(clean, bold, size));
        }
        return new RenderedReceipt(width, lines, prepared);
    }
}
