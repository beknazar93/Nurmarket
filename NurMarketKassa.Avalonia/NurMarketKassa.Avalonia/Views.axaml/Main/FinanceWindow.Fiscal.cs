using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-11-14 (П0-1): «Финансы» владельца — вкладка «Возвраты»: колонка «ККМ» (Пробит / Не пробит / Ошибка) и в карточке чека (из «Возвратов» и «Истории») — блок «Фискальный чек (ККМ)» самого чека
    /// и блок «Фискальный возврат» по каждому возврату этого чека. Колонка и блоки видны только при включённой фискализации компании (FiscalSettings), иначе окно как раньше.</summary>
    public partial class FinanceWindow
    {
        /// <summary>Колонка «ККМ» в таблице возвратов: показывается только при включённой фискализации; заголовок — на языке интерфейса.</summary>
        private void ApplyRefundsFiscalColumn()
        {
            // колонка найдена по привязке FiscalText (x:Name у колонок таблицы поле не создаёт)
            var column = RefundsGrid.Columns.OfType<DataGridTextColumn>().FirstOrDefault(c => (c.Binding as Avalonia.Data.Binding)?.Path == "FiscalText");
            if (column is null)
                return;
            column.Header = Tr.T("ККМ", "ККМ", "KKM", "ÖKC", "KKM");
            column.IsVisible = FiscalSettings.ShowsFiscalUi;
        }

        private void ShowPopupFiscal(string saleId, JsonElement json)
        {
            var returns = _refunds
                .Where(r => r.IsExact && !r.IsVoided && string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Fiscal ?? FiscalRequisites.None)
                .ToList();
            // 2026-11-19 (сверка с eKassa «режим server»): фискальные возвраты сервер отдаёт в карточке чека — ekassa_fiscal.returns {id: {status, fd_number, receipt_txt}}; если поля fiscal_* возвратов пусты, берём их
            if (returns.All(r => !r.HasData))
            {
                var ekassa = FiscalServerReceipt.ParseReturns(json);
                if (ekassa.Count > 0)
                    returns = ekassa.Select(r => new FiscalRequisites(FiscalRequisites.FromEkassaStatus(r.Status), r.Fd > 0 ? r.Fd.ToString() : null)).ToList();
            }
            FiscalRequisitesCard.Place(PopupFooterPanel, 0, FiscalRequisites.Parse(json), returns);
            // 2026-11-19 (склад К-4): блок «Партии» из allocations строк чека — только если сервер их отдал.
            NurMarketKassa.AvaloniaHost.Views.Dialogs.SaleBatchesCard.Place(PopupFooterPanel, 0, json, WarehouseWindow.RevisionShowPurchase);
        }
    }
}
