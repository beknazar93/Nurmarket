using Avalonia.Controls;
using Avalonia.Input;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь», как в мобилке — invoice_bind.dart): «Закупки → Приём товара» — штрихкод строки виден и правится
/// (цифры 4–14), у строки без кода — «Сканировать» (фокус в поле, следующий скан привяжется к товару строки; код уже у другого товара — «Выбрать его / Оставить / Отмена»),
/// «+N» — доп. штрихкоды (окно как в карточке товара: название варианта и «штук в упаковке»). Скан кода УПАКОВКИ (доп. штрихкод с «штук в упаковке» N) ставит количество строки N штук
/// с подписью «пачка = N шт.»; продажа по скану упаковки не затронута. Логика — InvoiceBarcodeRules / InvoiceBarcodeBinder (Infrastructure, с тестами); здесь только окно.
/// Отдельный файл: в ReceiveRow и ReceiveAddByCodeAsync — по несколько строк подключения.</summary>
public partial class PurchasesWindow
{
    /// <summary>Все строки приёма для проверки кода (код ещё не на сервере — каталог его не знает, поэтому смотрим и сами строки).</summary>
    private static IEnumerable<InvoiceCodeRow> ReceiveCodeRows(ReceiveState s) =>
        s.Lines.Select(l => new InvoiceCodeRow(l, l.ProductName, l.ProductId, l.Barcode, l.ExtraBarcodes.Select(v => v.Barcode).ToList()));

    /// <summary>Поле штрихкода строки правится: новый товар (кроме «База NurCRM» — его код определяет товар) или товар склада, у которого кода ещё нет.</summary>
    private static bool ReceiveBarcodeEditable(ReceivingLineVm line) =>
        line.IsNew ? line.Source != ReceivingSource.GlobalBase : !string.IsNullOrWhiteSpace(line.ProductId) && string.IsNullOrWhiteSpace(line.Barcode);

    private static int ReceiveExtraCount(ReceivingLineVm line)
    {
        if (line.IsNew || string.IsNullOrWhiteSpace(line.ProductId))
            return line.ExtraBarcodes.Count;
        return ProductActions.Find(line.ProductId)?.AlternateBarcodeVariants?.Count(v => !string.IsNullOrWhiteSpace(v.Barcode)) ?? 0;
    }

    /// <summary>Показать состояние ячейки штрихкода строки (из Sync строки).</summary>
    private static void ReceiveBarcodeApply(ReceivingLineVm line, InvoiceBarcodeCell cell)
    {
        var editable = ReceiveBarcodeEditable(line);
        cell.Apply(line.Barcode, editable, showScan: editable && line.Barcode.Length == 0, ReceiveExtraCount(line), showExtras: true);
    }

    /// <summary>Подключить поле, «Сканировать» и «+N» строки: Enter (скан сканером заканчивается им) и уход из поля — шаг «код у строки»; «+N» — окно доп. штрихкодов.</summary>
    private void ReceiveBarcodeWire(ReceiveState s, ReceivingLineVm line, InvoiceBarcodeCell cell, RowParts parts)
    {
        var committing = false;
        async Task CommitAsync()
        {
            if (committing || parts.Syncing || !ReceiveBarcodeEditable(line))
                return;
            var typed = cell.Box.Text ?? "";
            if (string.Equals(InvoiceBarcodeRules.Clean(typed), line.Barcode, StringComparison.OrdinalIgnoreCase))
                return;
            committing = true;
            try
            {
                var ctx = new BarcodeRowContext(line, line.IsNew ? null : line.ProductId, line.IsNew, line.ProductName, () => ReceiveCodeRows(s));
                var r = await InvoiceBarcodeKit.CommitAsync(PosDialogHost.ResolveOwner(this), ctx, typed).ConfigureAwait(true);
                if (_receive != s || !s.Lines.Contains(line))
                    return;
                switch (r.Kind)
                {
                    case BarcodeCommitKind.Set:
                        line.Barcode = r.Code;
                        PosLogger.Log($"Приём товара: строке «{line.ProductName}» поставлен штрихкод {PurchaseReceivingService.MaskCode(r.Code)}.", "STOCK");
                        break;
                    case BarcodeCommitKind.Cleared:
                        if (line.IsNew)
                            line.Barcode = "";
                        break;
                    case BarcodeCommitKind.UseOwner when r.Owner is { } owner:
                        ReceiveUseOwner(s, line, owner);
                        return;
                }
                if (r.Message.Length > 0)
                    ReceiveMessage(r.Message, warning: r.Warning);
                cell.Box.Text = line.Barcode;
                parts.Refresh(true);
                ReceiveRefresh();
                if (r.Kind == BarcodeCommitKind.Set)
                    ReceiveFocusScan(s);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приём товара: штрихкод строки не принят ({ex.Message}).", "WARNING");
                ReceiveMessage(ex.Message, warning: true);
            }
            finally
            {
                committing = false;
                cell.Disarm();
            }
        }
        cell.Box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true; // скан сканером заканчивается Enter: не уходит в «Оприходовать» и не идёт по цепочке полей
            _ = CommitAsync();
        };
        cell.Box.LostFocus += (_, _) => _ = CommitAsync();
        cell.Extras.Click += async (_, _) => await ReceiveEditExtrasAsync(s, line, cell, parts);
    }

    /// <summary>«+N»: окно доп. штрихкодов. Новый товар — список остаётся в строке и уйдёт в создание; товар склада — список сразу пишется на сервер (читаем свежий).</summary>
    private async Task ReceiveEditExtrasAsync(ReceiveState s, ReceivingLineVm line, InvoiceBarcodeCell cell, RowParts parts)
    {
        var outcome = await InvoiceBarcodeKit.EditExtrasFlowAsync(PosDialogHost.ResolveOwner(this), line.ProductName, line.Barcode, line.ProductId, line.IsNew, line.ExtraBarcodes, line,
            () => ReceiveCodeRows(s)).ConfigureAwait(true);
        if (outcome is null || _receive != s || !s.Lines.Contains(line))
            return;
        if (outcome.List is not null && line.IsNew)
            line.ExtraBarcodes = outcome.List;
        ReceiveMessage(outcome.Message, warning: outcome.Warning);
        parts.Refresh(true);
        ReceiveRefresh();
    }

    /// <summary>«Выбрать его» в вопросе о занятом коде: строка с этим кодом убирается, вместо неё (или к уже стоящей) — товар каталога с тем же количеством.</summary>
    private void ReceiveUseOwner(ReceiveState s, ReceivingLineVm line, CatalogProductTileVm owner)
    {
        var qty = line.Quantity > 0 ? line.Quantity : 1;
        s.Lines.Remove(line);
        ReceiveRender();
        ReceiveAddTile(owner, qty, byScan: true);
        ReceiveMessage(T($"Строка стала товаром «{owner.Title}».", $"Сап «{owner.Title}» товары болду.", $"The line became “{owner.Title}”.", $"Satır «{owner.Title}» ürünü oldu.", $"Qator «{owner.Title}» mahsulotiga aylandi."));
    }
}
