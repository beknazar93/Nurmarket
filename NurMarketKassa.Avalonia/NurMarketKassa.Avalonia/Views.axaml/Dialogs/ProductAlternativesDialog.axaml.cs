using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;

using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Список товаров той же категории с остатком > 0 — предлагается вместо
/// отсканированного товара, если его нет на складе (AI-фичи 2026-09-03, п.12 из мозгового
/// штурма). Возвращает выбранный товар или null, если кассир закрыл окно ничего не выбрав.</summary>
public partial class ProductAlternativesDialog : Window
{
    public ProductAlternativesDialog()
    {
        InitializeComponent();
    }

    /// <summary>2026-10-02: строка списка — товар, почему предложен («то же действующее вещество: фипронил»)
    /// и остаток.</summary>
    public sealed record AlternativeItem(CatalogProductTileVm Product, string Title, string PriceLine, string Info);

    public ProductAlternativesDialog(IReadOnlyList<(CatalogProductTileVm Product, string Reason)> alternatives) : this()
    {
        AlternativesList.ItemsSource = alternatives.Select(a => new AlternativeItem(
            a.Product, a.Product.Title, a.Product.PriceLine,
            (string.IsNullOrWhiteSpace(a.Reason) ? "" : a.Reason + " · ")
            + Tr.T($"в наличии {a.Product.Quantity:0.###}", $"бар {a.Product.Quantity:0.###}", $"in stock {a.Product.Quantity:0.###}",
                $"stokta {a.Product.Quantity:0.###}", $"mavjud {a.Product.Quantity:0.###}"))).ToList();
        EmptyText.IsVisible = alternatives.Count == 0;
    }

    public static async System.Threading.Tasks.Task<CatalogProductTileVm?> ShowAsync(
        Window owner, IReadOnlyList<(CatalogProductTileVm Product, string Reason)> alternatives)
    {
        var dialog = new ProductAlternativesDialog(alternatives);
        return await dialog.ShowDialog<CatalogProductTileVm?>(owner);
    }

    private void AlternativeRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AlternativeItem item })
            Close(item.Product);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);
}
