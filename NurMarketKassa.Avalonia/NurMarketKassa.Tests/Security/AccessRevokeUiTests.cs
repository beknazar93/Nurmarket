using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.Ui.Shared;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-13 (живая проверка доступов): меню кассы пересчитывается при каждом обновлении прав («Настройки», «Удалено из чека», «Оплата долга»); действия корзины
/// (удаление, оплата) проверяются по актуальным правам: отнятое — отказ с названием права, возвращённое — снова работает без «Отложить». Права — изменяемая заглушка.</summary>
public sealed class AccessRevokeUiTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public AccessRevokeUiTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    /// <summary>Права, которые «сервер» отнимает и возвращает на ходу; ведёт себя как PermissionService (UsePos закрывает Продажу).</summary>
    private sealed class MutablePermissions : IPermissionService
    {
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasPermission(string permission) =>
            !Denied.Contains(permission) && !(Denied.Contains(PosPermissions.UsePos) && PosPermissions.CashierWork.Contains(permission));

        public void Demand(string permission)
        {
            if (!HasPermission(permission))
                throw new PermissionDeniedException(permission);
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(30);
        }
        return condition();
    }

    private static (BasketPanelViewModel Vm, CartService Cart, FakePrompts Prompts) CreateBasket(IPermissionService permissions)
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var prompts = new FakePrompts();
        var tile = CartTestHelper.CreateProduct("p1", "Саморез", 100m);
        var vm = new BasketPanelViewModel(cart, prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), new FakeWindows(), new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => id == "p1" ? tile : null, permissions: permissions);
        vm.AddProductFromCatalogWithOverride(tile, 2, 100);
        return (vm, cart, prompts);
    }

    [Fact]
    public void SideMenu_recalculates_settings_and_paydebt_on_every_refresh()
    {
        var permissions = new MutablePermissions();
        var menu = new SideMenuViewModel(Null<IAppSession>(), () => { }, permissions: permissions);
        var changed = new List<string>();
        menu.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        menu.CanViewSettings.Should().BeTrue();
        menu.CanViewMarketplace.Should().BeTrue();

        // владелец отнял «Настройки», «Клиентов» и «Долги»
        permissions.Denied.UnionWith(new[] { PosPermissions.ViewSettings, PosPermissions.ViewClients, PosPermissions.ViewDebts });
        menu.RefreshEntitlements();
        changed.Should().Contain(nameof(SideMenuViewModel.CanViewSettings), "«Настройки» и «Удалено из чека» привязаны к CanViewSettings — без уведомления они висели в меню");
        menu.CanViewSettings.Should().BeFalse();
        menu.CanViewMarketplace.Should().BeFalse();
        menu.CanViewErrorLogs.Should().BeFalse();
        menu.CanViewPayDebt.Should().BeFalse("«Оплата долга» скрыта при выключенных «Клиентах и долгах», как на сайте");

        // вернул «Настройки» — пункт возвращается без перезахода
        changed.Clear();
        permissions.Denied.Remove(PosPermissions.ViewSettings);
        menu.RefreshEntitlements();
        changed.Should().Contain(nameof(SideMenuViewModel.CanViewSettings));
        menu.CanViewSettings.Should().BeTrue();
        menu.CanViewMarketplace.Should().BeTrue();

        // вернул «Клиентов» (долги всё ещё выключены) — «Оплата долга» снова есть (достаточно одного из двух)
        permissions.Denied.Remove(PosPermissions.ViewClients);
        menu.RefreshEntitlements();
        menu.CanViewPayDebt.Should().Be(TariffGate.CanUseDebts);
    }

    /// <summary>Ошибка 6: «Продажа ниже закупки» выдаётся из программы владельца тем же PATCH employees/{id}/, что и на сайте; null у сервера = не выдано; без изменения поле не шлётся.</summary>
    [Fact]
    public void Employee_access_sends_can_sell_below_cost_only_when_shown_and_changed()
    {
        var original = new NurMarketKassa.Models.EmployeeAccessFlags { SellBelowCostShown = true, CanSellBelowCost = false };

        var unchanged = new NurMarketKassa.Models.EmployeeAccessFlags { SellBelowCostShown = true, CanSellBelowCost = false };
        unchanged.ChangedFields(original).Should().NotContainKey("can_sell_below_cost", "владелец не трогал переключатель — чужие правки не затираем");

        var granted = new NurMarketKassa.Models.EmployeeAccessFlags { SellBelowCostShown = true, CanSellBelowCost = true };
        granted.ChangedFields(original).Should().ContainKey("can_sell_below_cost").WhoseValue.Should().Be(true);

        var revoked = new NurMarketKassa.Models.EmployeeAccessFlags { SellBelowCostShown = true, CanSellBelowCost = false };
        revoked.ChangedFields(granted).Should().ContainKey("can_sell_below_cost").WhoseValue.Should().Be(false);

        // создание сотрудника: переключателя нет — поле в запрос не попадает
        new NurMarketKassa.Models.EmployeeAccessFlags().ToRequestFields().Should().NotContainKey("can_sell_below_cost");
    }

    [Fact]
    public async Task Basket_clear_denied_names_the_right_and_works_again_after_it_returns()
    {
        var permissions = new MutablePermissions();
        var (vm, cart, prompts) = CreateBasket(permissions);
        try
        {
            vm.HasItems.Should().BeTrue();
            permissions.Denied.Add(PosPermissions.DeleteCartItem); // отняли «Удаление из чека», в чеке товары

            vm.ClearCartCommand.Execute(null);
            (await WaitAsync(() => prompts.Errors.Count > 0)).Should().BeTrue();
            prompts.Errors.Last().Should().Contain("«Удаление позиций из корзины»", "отказ называет право");
            vm.HasItems.Should().BeTrue("чек не трогаем — строки остаются");

            permissions.Denied.Remove(PosPermissions.DeleteCartItem); // вернули — без «Отложить»
            vm.ClearCartCommand.Execute(null);
            (await WaitAsync(() => !vm.HasItems)).Should().BeTrue("возвращённое право работает при том же открытом чеке");
        }
        finally
        {
            vm.Dispose();
            cart.Dispose();
        }
    }

    [Fact]
    public async Task Basket_pay_with_pos_closed_says_work_at_till_not_sale()
    {
        var permissions = new MutablePermissions();
        var (vm, cart, prompts) = CreateBasket(permissions);
        try
        {
            permissions.Denied.Add(PosPermissions.UsePos); // can_use_pos=false, «Продажа» при этом включена
            vm.PayCommand.Execute(null);
            (await WaitAsync(() => prompts.Errors.Count > 0)).Should().BeTrue();
            var text = prompts.Errors.Last();
            text.Should().Contain("«Работа на кассе»").And.NotContain("«Продажа»");
            text.Should().Contain("Управление → Сотрудники");

            // только «Продажа» отнята, «Работа на кассе» открыта — называется «Продажа»
            permissions.Denied.Clear();
            permissions.Denied.Add(PosPermissions.Sale);
            prompts.Errors.Clear();
            vm.PayCommand.Execute(null);
            (await WaitAsync(() => prompts.Errors.Count > 0)).Should().BeTrue();
            prompts.Errors.Last().Should().Contain("«Продажа»").And.NotContain("«Работа на кассе»");
        }
        finally
        {
            vm.Dispose();
            cart.Dispose();
        }
    }
}
