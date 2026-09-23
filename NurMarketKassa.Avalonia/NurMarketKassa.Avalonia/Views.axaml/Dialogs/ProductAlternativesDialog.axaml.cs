using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;

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

    public ProductAlternativesDialog(IReadOnlyList<CatalogProductTileVm> alternatives) : this()
    {
        AlternativesList.ItemsSource = alternatives;
        EmptyText.IsVisible = alternatives.Count == 0;
    }

    public static async System.Threading.Tasks.Task<CatalogProductTileVm?> ShowAsync(
        Window owner, IReadOnlyList<CatalogProductTileVm> alternatives)
    {
        var dialog = new ProductAlternativesDialog(alternatives);
        return await dialog.ShowDialog<CatalogProductTileVm?>(owner);
    }

    private void AlternativeRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogProductTileVm product })
            Close(product);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(null);
}
