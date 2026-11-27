using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-21 (ответ Умара 20.11, В.16 / В.23): «Включить» и «Выключить» модули компании на сервере (PUT users/company/modules/, DELETE users/company/tariff/modules/{code}/).
// Часть класса MarketplaceView. Карточки собирает LocalMarketplaceCatalogSource (MarketplaceModules); команды — «module-on:id» и «module-off:id».
public partial class MarketplaceView
{
    private bool _moduleBusy;

    /// <summary>Включить / выключить модуль на сервере. Успех — витрина и меню пересобираются (CompanyModules.Changed). Сервер модули не отдаёт (старый сервер, нет связи, реестра ещё не было) — по-старому: Смета и Прокат
    /// включаются местным признаком, Ремонт и Trade-in — ключом; остальные модули без сервера не включить. Уже есть ответ сервера — местный признак не ставим: иначе работа в обход цены и отключение на сайте не действовали бы.</summary>
    private async Task SetModuleAsync(string id, bool enable)
    {
        if (_moduleBusy)
            return;
        _moduleBusy = true;
        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            // 2026-11-26 («Выход мяса при приходе»): сервер этот модуль пока не знает — включается и выключается только местным признаком, без запроса (бесплатно).
            if (id == ModuleIds.CarcassYield && CompanyModules.Snapshot?.Knows(id) != true)
            {
                if (enable)
                    SetLocalModuleFlag(id);
                else
                    ClearLocalModuleFlag(id);
                return;
            }
            var knownBefore = CompanyModules.IsKnown;
            var result = enable ? await CompanyModules.EnableAsync(id).ConfigureAwait(true) : await CompanyModules.DisableAsync(id).ConfigureAwait(true);
            if (result.Status == ModuleChangeStatus.Ok)
            {
                if (!enable)
                    ClearLocalModuleFlag(id); // убрали на сервере — прежний местный признак тоже снимаем, иначе модуль остался бы включённым
            }
            else if (!knownBefore && result.Status is ModuleChangeStatus.Unsupported or ModuleChangeStatus.NoConnection)
            {
                if (!enable)
                    ClearLocalModuleFlag(id);
                else if (id is ModuleIds.Estimate or ModuleIds.Rental)
                    SetLocalModuleFlag(id);
                else if (id is ModuleIds.Repair or ModuleIds.TradeIn)
                {
                    UnlockServiceDesk(id); // по-старому: ключ
                    return;
                }
                else
                    AddonRequestAlert(owner, Tr.T("Модуль", "Модуль", "Module", "Modül", "Modul"), CompanyModules.Describe(new ModuleChangeResult(ModuleChangeStatus.Unsupported)) ?? "", PosAlertKind.Info);
            }
            else
            {
                AddonRequestAlert(owner, Tr.T("Модуль", "Модуль", "Module", "Modül", "Modul"), CompanyModules.Describe(result) ?? "", PosAlertKind.Error);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Маркетплейс: модуль «{id}» ({(enable ? "включить" : "выключить")}) не изменён: {ex.Message}", "WARNING");
        }
        finally
        {
            _moduleBusy = false;
            TariffGate.RaisePacksChanged(); // меню кассы и программы владельца перечитывают пункты
            ScheduleStoreReload();
        }
    }

    /// <summary>«Попробовать 7 дней»: POST users/company/features/trial/ {code}. Успех — модуль включён на пробу (карточка «Проба до …»); 409 trial_used / already_active — понятный текст.</summary>
    private async Task StartModuleTrialAsync(string id)
    {
        if (_moduleBusy)
            return;
        _moduleBusy = true;
        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            var result = await CompanyModules.StartTrialAsync(id).ConfigureAwait(true);
            if (result.Status != ModuleChangeStatus.Ok)
                AddonRequestAlert(owner, Tr.T("Модуль", "Модуль", "Module", "Modül", "Modul"), CompanyModules.Describe(result) ?? "",
                    result.Status is ModuleChangeStatus.TrialUsed or ModuleChangeStatus.AlreadyActive ? PosAlertKind.Info : PosAlertKind.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Маркетплейс: пробный период модуля «{id}» не включён: {ex.Message}", "WARNING");
        }
        finally
        {
            _moduleBusy = false;
            TariffGate.RaisePacksChanged();
            ScheduleStoreReload();
        }
    }

    private static void SetLocalModuleFlag(string id)
    {
        var prefs = UserPreferences.Instance;
        if (id == ModuleIds.Estimate)
            prefs.EstimateEnabled = true;
        else if (id == ModuleIds.Rental)
            prefs.RentalEnabled = true;
        else if (id == ModuleIds.CarcassYield)
            prefs.CarcassYieldEnabled = true; // 2026-11-26
        prefs.SaveToDisk();
    }

    private static void ClearLocalModuleFlag(string id)
    {
        var prefs = UserPreferences.Instance;
        switch (id)
        {
            case ModuleIds.Estimate: prefs.EstimateEnabled = false; break;
            case ModuleIds.Rental: prefs.RentalEnabled = false; break;
            case ModuleIds.CarcassYield: prefs.CarcassYieldEnabled = false; break; // 2026-11-26
            case ModuleIds.Repair: prefs.RepairUnlocked = false; break; // ключ (платный) снимается вместе с модулем; включить снова можно здесь же, без ключа
            case ModuleIds.TradeIn: prefs.TradeInUnlocked = false; break;
            default: return;
        }
        prefs.SaveToDisk();
    }
}
