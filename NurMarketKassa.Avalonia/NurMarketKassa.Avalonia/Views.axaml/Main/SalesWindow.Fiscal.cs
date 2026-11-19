using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-11-14 (П0-1): в карточке чека «Продаж» (программа владельца и «Продажи и финансы») — блок «Фискальный чек (ККМ)»: статус, ФД, ФМ, РН, QR, замок пробитого чека.
    /// Данные — поля fiscal_* карточки чека (<c>pos/sales/{id}/</c>), которая уже загружена для самого чека. Без включённой фискализации блок скрыт.</summary>
    public partial class SalesWindow
    {
        private void ShowPopupFiscal(JsonElement json)
        {
            if (PopupActionBarHost.Parent is Panel panel)
            {
                FiscalRequisitesCard.Place(panel, panel.Children.IndexOf(PopupActionBarHost) + 1, FiscalRequisites.Parse(json));
                // 2026-11-19 (склад К-4): блок «Партии» из allocations строк чека — только если сервер их отдал.
                NurMarketKassa.AvaloniaHost.Views.Dialogs.SaleBatchesCard.Place(panel, panel.Children.IndexOf(PopupActionBarHost) + 1, json, WarehouseWindow.RevisionShowPurchase);
            }
        }
    }
}
