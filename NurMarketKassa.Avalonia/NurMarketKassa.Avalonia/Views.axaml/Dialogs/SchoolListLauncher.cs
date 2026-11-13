using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-12 (КТ2): кнопка «Списки к школе» в меню «Ещё» кассы. Открывает окно выбора списка; выбранные позиции идут в чек по одной тем же
/// путём, что товар из каталога (<see cref="MainWindow.AddProductFromCatalogAsync"/>: открытая смена, просрочка, остаток, окно «Нет
/// в наличии»), — обычными строками по обычной цене. Работает только в сфере «Канцтовары» (<see cref="SchoolListStore.Available"/>);
/// у других сфер кнопка скрыта (<see cref="BasketPanelViewModel.ShowSchoolLists"/>) и сюда не попадает.
/// </summary>
internal static class SchoolListLauncher
{
    public static async Task OpenAsync(Control? anchor, BasketPanelViewModel? basket)
    {
        if (!SchoolListStore.Available || anchor is null)
            return;
        try
        {
            if (basket != null)
                basket.IsMoreActionsVisible = false;
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return;
            string? selected = null;
            while (true)
            {
                var lists = SchoolListStore.Load();
                var dialog = new SchoolListWindow(lists, CatalogCacheService.FindProductById, CanEdit(), selected);
                var added = await PosDialogHost.ShowModalAsync(dialog, owner).ConfigureAwait(true) == true;
                if (added)
                {
                    await AddToReceiptAsync(owner, basket, dialog.Result).ConfigureAwait(true);
                    return;
                }
                if (!dialog.EditRequested)
                    return;
                var editor = new SchoolListsEditorWindow(lists);
                await PosDialogHost.ShowModalAsync(editor, owner).ConfigureAwait(true);
                selected = null;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Списки к школе: окно не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>Править списки может тот, у кого право «Настройки» (владелец, администратор).</summary>
    private static bool CanEdit()
    {
        try
        {
            return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewSettings);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task AddToReceiptAsync(Window owner, BasketPanelViewModel? basket, IReadOnlyList<(Models.Pos.CatalogProductTileVm Tile, int Quantity)> lines)
    {
        if (basket is null)
            return;
        foreach (var (tile, quantity) in lines)
        {
            // Количество кладётся в поле «Кол-во» — оттуда его читает обычное добавление товара (после добавления поле само возвращается к 1).
            basket.ManualQuantity = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (owner is MainWindow main)
                await main.AddProductFromCatalogAsync(tile).ConfigureAwait(true);
            else
                basket.AddProductFromCatalog(tile);
        }
        PosLogger.Log($"Списки к школе: в чек отправлено {lines.Count} позиций.", "CART");
    }
}
