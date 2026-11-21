using System.Globalization;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>
/// 2026-11-21 (сфера «Базар», модуль «Торг», ТЗ Контейнер-базар 2.3): торг по строке и по чеку из корзины. Окно — <see cref="BazaarHaggleDialog"/>; сама скидка — обычная скидка кассы
/// (на строку суммой, на чек суммой; строки чека не меняются), поэтому работают те же защиты: «Максимальная скидка» компании, «Продажа в убыток» / право can_sell_below_cost (BelowCostGuard),
/// журнал подозрительных операций. Сверх них — PIN руководителя при скидке торга выше порога (<see cref="BazaarHaggleGuard"/>). Причина «торг» пишется в чек (discount_reason) и в журнал.
/// </summary>
public partial class MainWindow
{
    private void WireBazaarHooks()
    {
        BazaarHooks.HaggleLine = HaggleLineAsync;
        BazaarHooks.HaggleCheck = HaggleCheckAsync;
    }

    /// <summary>Торг по всему чеку: «Договорились за …» — итог чека; скидка = сумма строк (после скидок строк) − итог.</summary>
    internal async Task HaggleCheckAsync()
    {
        if (!BazaarModules.HaggleOn || !Authorize(PosPermissions.ApplyDiscount))
            return;
        var cart = ResolveCartService();
        if (!cart.HasCart || cart.LineCount == 0)
            return;
        var total = ReceiptSnapshotCartEditor.HaggleBase(cart.Root);
        var subject = Tr.T("на чек", "чекке", "on the receipt", "fişe", "chekka");
        var dlg = new BazaarHaggleDialog(Tr.T("Торг по всему чеку", "Бүт чек боюнча соодалашуу", "Haggling on the whole receipt", "Tüm fişte pazarlık", "Butun chek bo'yicha savdolashuv"), total);
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true)
            return;

        var result = BazaarHaggle.Agreed(total, dlg.Agreed);
        if (!result.IsOk)
            return;
        if (!await BazaarHaggleGuard.ConfirmAsync(result.Percent, subject).ConfigureAwait(true))
            return;

        var basket = _viewModel.Basket;
        var (previousPercent, previousTotal) = basket.ReadOrderDiscount();
        var value = result.Discount.ToString("0.00", CultureInfo.InvariantCulture);
        // Скидка записывается в чек без обновления экрана: покупатель её не видит, пока кассир не подтвердит «Продажа в убыток» (как у обычной скидки на чек).
        if (!basket.ApplyOrderDiscount("sum", value, refresh: false))
            return;
        if (!await ConfirmSellingAtLossAsync(basket.LossWarningTextFromCart()).ConfigureAwait(true))
        {
            basket.RestoreOrderDiscount(previousPercent, previousTotal, refresh: false);
            basket.CartMessage = LossDiscountCancelledText();
            return;
        }
        ReceiptSnapshotCartEditor.MarkDiscountReason(cart, null, BazaarHaggle.ReasonValue);
        basket.RefreshFromCart();
        FraudMonitor.OnDiscount(result.Percent, (double)result.Discount, subject + " (" + BazaarHaggle.ReasonText + ")");
        PosLogger.Log($"Торг по чеку: сумма {result.Total:0.00} → {result.Agreed:0.00}, скидка {result.Discount:0.00} ({result.Percent:0.#} %).", "CART");
        basket.CartMessage = Tr.T($"Торг: договорились за {MoneyText.Som(result.Agreed)} (скидка {MoneyText.Som(result.Discount)}).",
            $"Соодалашуу: {MoneyText.Som(result.Agreed)} макулдашылды (арзандатуу {MoneyText.Som(result.Discount)}).",
            $"Haggle: agreed for {MoneyText.Som(result.Agreed)} (discount {MoneyText.Som(result.Discount)}).",
            $"Pazarlık: {MoneyText.Som(result.Agreed)} üzerinden anlaşıldı (indirim {MoneyText.Som(result.Discount)}).",
            $"Savdolashuv: {MoneyText.Som(result.Agreed)} ga kelishildi (chegirma {MoneyText.Som(result.Discount)}).");
    }

    /// <summary>Торг по строке: скидка на позицию суммой = цена × количество − договорённая сумма за позицию.</summary>
    internal async Task HaggleLineAsync(CartLineItemVm line)
    {
        if (!BazaarModules.HaggleOn || !Authorize(PosPermissions.ApplyDiscount) || string.IsNullOrWhiteSpace(line.ItemId))
            return;
        var cart = ResolveCartService();
        var gross = BazaarHaggle.LineGross(line.UnitPrice, line.Quantity);
        var dlg = new BazaarHaggleDialog(line.Title, gross);
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true)
            return;

        var result = BazaarHaggle.Agreed(gross, dlg.Agreed);
        if (!result.IsOk)
            return;
        // Потолок «Максимальная скидка» компании (как у обычной скидки на позицию): владелец и администратор не ограничены.
        if (MaxDiscountGate.Exceeds(result.Percent, isPercent: true, baseSum: 0))
        {
            var limit = MaxDiscountGate.Limit ?? 0m;
            _prompts.ShowWarning(Tr.T($"Скидка не может превышать {limit:0.##}% — таково ограничение для сотрудников.",
                $"Арзандатуу {limit:0.##}%дан ашпашы керек — бул кызматкерлер үчүн чектөө.",
                $"The discount can't exceed {limit:0.##}% — that's the limit set for employees.",
                $"İndirim en fazla %{limit:0.##} olabilir — personel için belirlenen sınır budur.",
                $"Chegirma {limit:0.##}%dan oshmasligi kerak — bu xodimlar uchun belgilangan chegara."));
            return;
        }
        if (!await BazaarHaggleGuard.ConfirmAsync(result.Percent, line.Title).ConfigureAwait(true))
            return;

        var (mode, previous) = ReadLineDiscount(cart, line.ItemId);
        var value = result.Discount.ToString("0.00", CultureInfo.InvariantCulture);
        ReceiptSnapshotCartEditor.PatchLineDiscount(cart, line.ItemId, "sum", value);
        // Скидка увела товар ниже закупки — без «Я знаю что делаю» (и без права can_sell_below_cost) возвращаем прежнюю скидку.
        if (!await ConfirmSellingAtLossAsync(_viewModel.Basket.LossWarningTextFromCart()).ConfigureAwait(true))
        {
            ReceiptSnapshotCartEditor.PatchLineDiscount(cart, line.ItemId, mode, previous?.ToString(CultureInfo.InvariantCulture));
            _viewModel.Basket.CartMessage = LossDiscountCancelledText();
            return;
        }
        ReceiptSnapshotCartEditor.MarkDiscountReason(cart, line.ItemId, BazaarHaggle.ReasonValue);
        _viewModel.Basket.RefreshFromCart();
        FraudMonitor.OnDiscount(result.Percent, (double)result.Discount, line.Title + " (" + BazaarHaggle.ReasonText + ")");
        PosLogger.Log($"Торг по строке «{line.Title}»: сумма {result.Total:0.00} → {result.Agreed:0.00}, скидка {result.Discount:0.00} ({result.Percent:0.#} %).", "CART");
        _viewModel.Basket.CartMessage = Tr.T($"Торг по «{line.Title}»: договорились за {MoneyText.Som(result.Agreed)}.",
            $"«{line.Title}» боюнча соодалашуу: {MoneyText.Som(result.Agreed)} макулдашылды.",
            $"Haggle on “{line.Title}”: agreed for {MoneyText.Som(result.Agreed)}.",
            $"«{line.Title}» için pazarlık: {MoneyText.Som(result.Agreed)} üzerinden anlaşıldı.",
            $"«{line.Title}» bo'yicha savdolashuv: {MoneyText.Som(result.Agreed)} ga kelishildi.");
    }
}

/// <summary>PIN руководителя для скидки торга (ТЗ Контейнер-базар 2.3): до порога — свободно; выше порога (5 %) — PIN старшего; выше жёсткого предела (10 %) — только владелец / администратор.
/// Если в «Защите» включено своё правило на скидку и оно строже — действует оно (PIN спрашивается один раз).</summary>
internal static class BazaarHaggleGuard
{
    public static async Task<bool> ConfirmAsync(double percent, string? subject)
    {
        var needsPin = false;
        try
        {
            var level = BazaarHaggle.GuardLevel(percent);
            needsPin = level != HaggleGuardLevel.Free || ActionGuardPolicy.NeedsPin(GuardAction.Discount, percent, AntiFraudSettingsStore.Current.Guard);
            if (!needsPin || ActionGuard.IsSenior())
                return true;
            var prompts = App.GetRequiredService<IUserPrompts>();
            if (level == HaggleGuardLevel.SeniorOnly)
            {
                PosLogger.Log($"Торг: скидка {percent:0.#} % выше предела {BazaarHaggle.HardPercent:0.#} % — только владелец / администратор.", "SECURITY");
                prompts.ShowToast(Tr.T($"Торг выше {BazaarHaggle.HardPercent:0.#} % — только владелец или администратор.",
                    $"{BazaarHaggle.HardPercent:0.#} %дан жогору соодалашуу — ээси же администратор гана.",
                    $"Haggling above {BazaarHaggle.HardPercent:0.#} % — owner or administrator only.",
                    $"%{BazaarHaggle.HardPercent:0.#} üstü pazarlık — yalnızca sahip veya yönetici.",
                    $"{BazaarHaggle.HardPercent:0.#} % dan yuqori savdolashuv — faqat egasi yoki administrator."), isWarning: true);
                return false;
            }
            PosLogger.Log($"PIN старшего: нужен для торга (скидка {percent:0.#} %).", "SECURITY");
            var granted = await ExpiredSaleGuard.AskOwnerPinAsync().ConfigureAwait(true);
            FraudMonitor.OnPin(GuardAction.Discount, percent, granted, subject);
            if (!granted)
                prompts.ShowToast(Tr.T("Нужен PIN старшего — торг отменён.", "Улуунун PIN коду керек — соодалашуу токтотулду.", "A senior's PIN is needed — the haggle was cancelled.",
                    "Yetkili PIN'i gerekli — pazarlık iptal edildi.", "Katta xodimning PIN kodi kerak — savdolashuv bekor qilindi."), isWarning: true);
            return granted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // сбой проверки ≠ разрешение: если PIN был нужен — отказ
            PosLogger.Log($"Торг: проверка PIN не выполнена ({ex.GetType().Name}: {ex.Message}) — {(needsPin ? "скидка отклонена" : "PIN не требовался")}.", "SECURITY");
            return !needsPin;
        }
    }
}
