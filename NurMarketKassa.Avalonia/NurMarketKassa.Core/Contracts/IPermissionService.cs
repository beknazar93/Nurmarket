namespace NurMarketKassa.Core.Contracts;

public interface IPermissionService
{
    bool HasPermission(string permission);
    void Demand(string permission);
}

public static class PosPermissions
{
    public const string ViewSettings = "can_view_settings";
    public const string ViewScales = "can_view_market_scales";
    public const string ViewLabels = "can_view_market_label";
    public const string ApplyDiscount = "can_view_market_discount";
    public const string EditPrice = "can_view_market_edit_price";
    public const string DeleteCartItem = "can_view_market_delete_cart_item";
    public const string ViewProcurement = "can_view_market_procurement";
    public const string ViewSupplier = "can_view_market_supplier";
    public const string EmployeeReturn = "can_view_market_employee_return";
    public const string ViewSales = "can_view_sales";
    public const string ViewShifts = "can_view_shifts";
    public const string ViewCashier = "can_view_cashier";
}

public sealed class PermissionDeniedException : InvalidOperationException
{
    public PermissionDeniedException(string permission)
        : base($"Permission '{permission}' is required.") => Permission = permission;

    public string Permission { get; }
}
