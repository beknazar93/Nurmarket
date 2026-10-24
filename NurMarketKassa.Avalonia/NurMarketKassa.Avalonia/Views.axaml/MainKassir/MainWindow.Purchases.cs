using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-10-22 (закупки как в вебе): «Закупки» в кассе — окно раздела (PurchasesWindow) по праву «Склад», как «Склад»
/// (NavigateWarehouse): раздел владельца, на «Старте» скрыт (как «Поставщики» и закупки на сайте). Пункт меню ☰ вызывает этот метод.</summary>
public partial class MainWindow
{
    internal void NavigatePurchases()
    {
        if (TariffGate.IsStartTariff)
            return;
        if (OwnerSectionAvailable() && Authorize(PosPermissions.ViewProducts))
        {
            _viewModel.CloseSideMenu();
            PurchasesWindow.ShowFor(this);
        }
    }
}
