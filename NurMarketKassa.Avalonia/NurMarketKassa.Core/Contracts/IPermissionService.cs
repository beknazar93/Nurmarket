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

    // 2026-10-01, «доступы работают некорректно»: разделы — теми же флагами, что на сайте NurCRM
    // (Моя компания → Сотрудники → доступы; сайт: employeeAccessLabels и меню). Раньше Склад
    // проверялся флагом «Закупки», а Аналитика, ABC, Финансы, Клиенты и Заказы — ничем
    // (ViewSales открыт всем кассирам), и сотрудник видел то, что на сайте ему закрыто.
    public const string ViewProducts = "can_view_products";
    public const string ViewAnalytics = "can_view_analytics";
    public const string ViewClients = "can_view_clients";
    public const string ViewOrders = "can_view_orders";

    /// <summary>Разделы, которые сайт владельцу показывает всегда (при входе владельца сайт сам
    /// выставляет ему эти флаги). Если владелец ни разу не входил на сайт, флаги могут быть
    /// пустыми — касса не должна закрывать ему его же склад и аналитику.</summary>
    public static readonly string[] OwnerSections = { ViewProducts, ViewAnalytics, ViewClients, ViewOrders };
}

public sealed class PermissionDeniedException : InvalidOperationException
{
    public PermissionDeniedException(string permission)
        : base($"Permission '{permission}' is required.") => Permission = permission;

    public string Permission { get; }
}
