using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21: входы в единую вкладку «Ценники и штрихкоды» из других мест (приход, Маркетплейс, Нур Советник): позиции с конкретными
/// штрихкодами и копиями. Права и покупку проверяет та же вкладка (<see cref="Dialogs.BarcodePrintTabHost.Unlock"/>).</summary>
public sealed partial class LabelsHubView
{
    /// <summary>Позиции (товар, штрихкод, копии; копии ≤ 0 — «по умолчанию») — в «Печать»: отмечены и показаны первыми. false — нет права или не куплено.</summary>
    public bool ApplyItems(IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items, Window? owner)
    {
        if (!_printHost.Unlock(owner))
            return false;
        _printHost.View!.ApplyPreset(items);
        ShowMode(LabelsHubMode.Print);
        return true;
    }
}
