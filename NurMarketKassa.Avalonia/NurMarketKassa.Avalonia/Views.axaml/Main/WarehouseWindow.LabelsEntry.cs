using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21 (владелец: «все редакторы — в одну вкладку»): единый вход в вкладку «Ценники и штрихкоды» из других окон — Маркетплейс
/// («Открыть»), приход товара («Печать этикеток») и т.п. Программа владельца открывает раздел «Склад» (как пункт меню, с проверкой прав), касса —
/// окно склада (как <see cref="RequestWriteOff"/>); затем выбирается вкладка, при необходимости — с позициями (товар, штрихкод, копии).</summary>
public partial class WarehouseWindow
{
    /// <summary>Просьба «открыть вкладку Ценники и штрихкоды» открытым складам (в программе владельца это раздел «Склад»).</summary>
    private static event Action<IReadOnlyList<(string ProductId, string? Barcode, int Copies)>?>? LabelsRequested;

    /// <summary>Только для проверочного стенда (в программе всегда null): вместо открытия вкладки получает окно-источник и позиции.</summary>
    public static Action<Window?, IReadOnlyList<(string ProductId, string? Barcode, int Copies)>?>? LabelsRequestOverride;

    /// <summary>Открыть вкладку «Ценники и штрихкоды» (<paramref name="items"/> — позиции для печати; null — просто открыть).</summary>
    public static void RequestLabels(Window? from, IReadOnlyList<(string ProductId, string? Barcode, int Copies)>? items = null)
    {
        if (LabelsRequestOverride is { } hook) // 2026-11-27: проверочный стенд — «Ценники» в приёмке и накладной: какие позиции переданы, без открытия окна склада
        {
            hook(from, items);
            return;
        }
        if (AppMode.IsOwner && ProductActionPlan.OpenSection is { } openSection)
        {
            openSection("warehouse");
            // Раздел создаётся в той же очереди — просьба уходит после него.
            Dispatcher.UIThread.Post(() => LabelsRequested?.Invoke(items));
            return;
        }

        if (!App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewProducts))
        {
            PosLogger.Log($"Permission denied: {PosPermissions.ViewProducts}", "WARNING");
            PosDialogs.Error(from, T("Недостаточно прав для выполнения этой операции.", "Бул операцияны аткарууга укук жетишсиз.",
                    "Insufficient permissions to perform this operation.", "Bu işlemi gerçekleştirmek için yetkiniz yok.", "Bu amalni bajarish uchun huquq yetarli emas."),
                T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"));
            return;
        }
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var warehouse = desktop?.Windows.OfType<WarehouseWindow>().FirstOrDefault(w => w.IsVisible);
        if (warehouse is null)
        {
            warehouse = App.GetRequiredService<WarehouseWindow>();
            warehouse.Show(from?.Owner as Window ?? from);
        }
        else
            warehouse.Activate();
        warehouse.HandleLabelsRequest(items);
    }

    private void OnLabelsRequested(IReadOnlyList<(string ProductId, string? Barcode, int Copies)>? items) => HandleLabelsRequest(items);

    private void HandleLabelsRequest(IReadOnlyList<(string ProductId, string? Barcode, int Copies)>? items)
    {
        if (items is { Count: > 0 })
            OpenLabelsTabWith(items);
        else
            OpenBarcodesTab(null);
    }

    private void InitLabelsRequests()
    {
        LabelsRequested += OnLabelsRequested;
        Closed += (_, _) => LabelsRequested -= OnLabelsRequested;
    }
}
