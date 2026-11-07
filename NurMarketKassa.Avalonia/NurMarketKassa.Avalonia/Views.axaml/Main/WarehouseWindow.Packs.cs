using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-07 (решение владельца «Да» — упаковки и в приёмку на вкладке «Склад → Приёмка»; уточнение «типа поштучной продажи»): режимы приёмки строки —
/// «в штуках», «упаковками → на склад штуки» (как в «Закупки → Приём товара») и «упаковка с поштучной продажей» (новый товар заводится с упаковкой, остаток в упаковках).
/// Здесь только вид: ссылка под «Принимаем» (меню — ReceivingPackMenu), высота строки, порядок Tab/Enter внутри ячейки, подтверждение перед проведением.
/// Расчёты, проверки и создание товара — в ReceivingLineVm, ReceivingPacks, PurchaseReceivingService (общие с «Закупками»).</summary>
public partial class WarehouseWindow
{
    // Высота строки таблицы в режиме упаковок: «Принимаем» (28) + «шт/упак.» (28) + подпись/ссылка и поля «Закупка» помещаются, строки не наезжают.
    private const double ReceivingPackRowHeight = 84;

    /// <summary>Подсказка поля «шт/упак.» (x:Static в разметке).</summary>
    public static string PackPiecesHintText => TR("шт/упак.", "даана/таңгак", "pcs/pack", "adet/koli", "dona/qadoq");

    /// <summary>В режиме упаковок строка выше: <c>MinHeight</c> побеждает фиксированную <c>RowHeight</c> таблицы (48/52), поэтому не зависит от её пересчёта при смене ширины.</summary>
    private static void ApplyReceivingRowHeight(DataGridRow row, ReceivingLineVm line)
    {
        var min = line.PackMode ? ReceivingPackRowHeight : 0;
        if (Math.Abs(row.MinHeight - min) > 0.5)
            row.MinHeight = min;
    }

    /// <summary>Поле «шт/упак.» в той же ячейке, что и «Принимаем».</summary>
    private static TextBox? FindPiecesEditor(Control inside) =>
        inside.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault()?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("rcv-pieces") && t.IsVisible);

    /// <summary>Нажатие на подпись под «Принимаем»: меню способа приёмки. У товара, у которого упаковка уже в карточке, подпись — просто подсказка «упак. = N шт».</summary>
    private void ReceivingPackLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control link || link.DataContext is not ReceivingLineVm line)
            return;
        e.Handled = true; // не начинать правку ячейки: меню открывается вместо неё
        if (!line.PackLinkClickable)
            return;
        ReceivingPackMenu.Show(link, line, () =>
        {
            RefreshReceivingSummary();
            // Курсор: нет размера упаковки — в «шт/упак.», иначе в «Принимаем».
            Dispatcher.UIThread.Post(() =>
            {
                if (!_viewModel.ReceivingLines.Contains(line))
                    return;
                BeginReceivingEdit(line, RcvColQty);
                if (line.PackMode && line.PackNeedsSize)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReceivingGrid.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("rcv-pieces") && t.IsVisible) is { } pieces)
                        {
                            pieces.Focus();
                            pieces.SelectAll();
                        }
                    }, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>Перед «Провести»: «Товар будет вестись упаковками по N шт, остаток — в упаковках. Продолжить?». false — вернуться к таблице.</summary>
    private Task<bool> ConfirmPackSaleAsync(IReadOnlyList<ReceivingLineVm> lines)
    {
        ReceivingGrid.CommitEdit();
        return PosConfirmDialog.ShowModalAsync(this,
            TR("Поштучная продажа из упаковки", "Таңгактан даана менен сатуу", "Single pieces from a pack", "Koliden tek tek satış", "Qadoqdan donalab sotish"),
            ReceivingPacks.PackSaleConfirmMessage(lines),
            TR("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish"),
            TR("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga"), PosConfirmAccent.Primary);
    }
}
