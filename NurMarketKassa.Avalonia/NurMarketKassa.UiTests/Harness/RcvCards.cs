using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-21: строки «Склад → Приёмка» — карточки (объединение с «Массовым добавлением»). Поиск карточки строки и её полей по тегу (name / qty / pieces / purchase / markup / sale / barcode / category) для тестов.</summary>
public static class RcvCards
{
    /// <summary>Внешняя рамка карточки: рамка с <see cref="BulkScanRow"/> в DataContext, чей родитель уже не карточка (вложенные рамки наследуют DataContext).</summary>
    public static bool IsCard(Border b) => b.DataContext is BulkScanRow && b.GetVisualParent()?.DataContext is not BulkScanRow;

    public static List<Border> Cards(Visual root) => root.GetVisualDescendants().OfType<Border>().Where(b => IsCard(b) && b.IsEffectivelyVisible && b.Bounds.Height > 0).ToList();

    /// <summary>Карточка строки. Список виртуализирован: нет карточки в видимой части — список прокручивается, пока она не построится.</summary>
    public static Border Card(Visual root, ReceivingLineVm line)
    {
        Border? Find() => root.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => IsCard(b) && b.DataContext is BulkScanRow r && ReferenceEquals(r.Line, line));
        var found = Find();
        if (found is not null)
            return found;
        var scroll = root.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "ReceivingCardsScroll");
        if (scroll is not null)
            for (var y = 0.0; y <= scroll.Extent.Height + 1; y += 60)
            {
                scroll.Offset = new Vector(0, y);
                UiShot.Settle(2);
                found = Find();
                if (found is not null)
                    return found;
            }
        throw new InvalidOperationException("Карточка строки не построена (список виртуализирован, прокрутка не помогла).");
    }

    public static TextBox Field(Visual root, ReceivingLineVm line, string tag) =>
        Card(root, line).GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, tag) && t.IsEffectivelyVisible);

    public static TextBox? FieldOrNull(Visual root, ReceivingLineVm line, string tag) =>
        Card(root, line).GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, tag) && t.IsEffectivelyVisible);
}
