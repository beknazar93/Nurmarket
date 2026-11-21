using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21 (просьба владельца «исправь в нашем складе уже загруженных товаров»): кнопка «Дубли и варианты» рядом с «Архив» — открывает окно «Объединение товаров»
/// (вкусы и цвета одного товара, загруженные отдельными карточками, сводятся в один товар с доп. штрихкодами). Склад один и тот же в кассе и в программе владельца.</summary>
public partial class WarehouseWindow
{
    private void AddProductMergeButton()
    {
        var button = SectionCards.Secondary(T("Дубли и варианты", "Кайталанмалар жана варианттар", "Duplicates and variants", "Tekrarlar ve varyantlar", "Takrorlar va variantlar"));
        button.Name = "WarehouseMergeButton";
        button.Margin = new Thickness(12, 0, 0, 6);
        button.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        button.Padding = new Thickness(16, 0);
        ToolTip.SetTip(button, T("Найти товары, которые на самом деле один товар (вкусы, цвета), и объединить их в один с доп. штрихкодами",
            "Чындыгында бир товар болгон товарларды (даамдар, түстөр) таап, кошумча штрихкоддор менен бириктирүү",
            "Find products that are really one product (flavours, colours) and merge them into one with extra barcodes",
            "Aslında tek ürün olan ürünleri (tatlar, renkler) bulup ek barkodlarla tek ürün yapın",
            "Aslida bitta mahsulot bo'lgan mahsulotlarni (ta'm, rang) topib, qo'shimcha shtrix-kodlar bilan bittaga birlashtirish"));
        button.Click += async (_, _) =>
        {
            try
            {
                await ProductMergeWindow.OpenAsync(this).ConfigureAwait(true);
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Склад: окно «Объединение товаров» не открылось: {ex}", "WARNING");
            }
        };
        // 2026-11-21 (прогон 1.17.91): в конец ряда кнопка уходила на третью строку и на 1024×600 сжимала таблицу до двух строк — ставим сразу после группы «Единица продажи» (рядом свободное место).
        button.Margin = new Thickness(0, 0, 12, 6);
        WarehouseFiltersRow.Children.Insert(Math.Min(1, WarehouseFiltersRow.Children.Count), button);
    }
}
