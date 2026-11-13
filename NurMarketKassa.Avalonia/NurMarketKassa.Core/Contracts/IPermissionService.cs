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

    /// <summary>2026-11-03 (аудит №3, Т3-03): «Склад: редактирование» (can_edit_products) — удаление, архив, списание, правка карточки, цен и остатка. Владелец/админ — всегда;
    /// остальным — только при явном true (2026-11-06, В3 Л4-20; PermissionService.IsProductEditAllowed).</summary>
    public const string EditProducts = "can_edit_products";
    public const string DeleteCartItem = "can_view_market_delete_cart_item";

    /// <summary>2026-11-13 (сервер bc1112): «Продажа ниже закупки» (can_sell_below_cost) — скидка, после которой цена ниже закупочной, проводится только при этом праве.
    /// Владелец/админ — всегда по роли (сервер флаг им не шлёт); остальным нет поля в профиле — права нет (PermissionService.IsSellBelowCostAllowed).</summary>
    public const string SellBelowCost = "can_sell_below_cost";
    public const string ViewProcurement = "can_view_market_procurement";
    public const string ViewSupplier = "can_view_market_supplier";
    public const string EmployeeReturn = "can_view_market_employee_return";
    public const string ViewSales = "can_view_sales";
    public const string ViewShifts = "can_view_shifts";
    public const string ViewCashier = "can_view_cashier";

    /// <summary>2026-10-30: «Редактор документов закупок» — правка проведённых накладных прихода. Пока ТОЛЬКО у владельца (роль owner в профиле);
    /// сотрудникам не выдаётся (в доступах — «пока только владелец»). Проверка — PermissionService.CanEditPurchaseDocuments.</summary>
    public const string EditPurchaseDocuments = "can_edit_purchase_documents";

    // 2026-10-18, аудит доступов: «Продажа» на сайте — флаг can_view_sale (не can_view_sales — тот
    // открыт всем кассирам для просмотра продаж по решению владельца).
    public const string Sale = "can_view_sale";

    /// <summary>2026-10-18: доступы работы на кассе — вход в интерфейс кассы, продажа/оплата,
    /// открытие/закрытие смены. Раньше не проверялись вовсе. Владельца/админа и компании, где
    /// доступы сотрудникам не выставлены, не запирают (см. PermissionService).</summary>
    public static readonly string[] CashierWork = { ViewCashier, Sale, ViewShifts };

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
    // 2026-11-10 (владелец: «возвраты добавь в программу владельца»): возврат владелец видит всегда (на сайте возврат есть у владельца на любом тарифе);
    // сотруднику — только по флагу can_view_market_employee_return с сервера.
    public static readonly string[] OwnerSections = { ViewProducts, ViewAnalytics, ViewClients, ViewOrders, EmployeeReturn };
}

public sealed class PermissionDeniedException : InvalidOperationException
{
    public PermissionDeniedException(string permission)
        : base($"Permission '{permission}' is required.") => Permission = permission;

    public string Permission { get; }
}
