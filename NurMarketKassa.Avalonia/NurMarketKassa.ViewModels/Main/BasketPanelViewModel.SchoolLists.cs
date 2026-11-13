using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

public sealed partial class BasketPanelViewModel
{
    /// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): пункт «Списки к школе» в меню «Ещё» — только в сфере «Канцтовары»
    /// (у остальных сфер скрыт). Читается при каждом показе меню, отдельного события не нужно.</summary>
    public bool ShowSchoolLists => SchoolListStore.Available;

    /// <summary>2026-11-12 (КТ3): пункт «Счёт из этого чека» — только в сфере «Канцтовары».</summary>
    public bool ShowInvoiceFromReceipt => MarketSpheres.IsStationery && StationeryProfile.Features.InvoiceFromReceipt;
}
