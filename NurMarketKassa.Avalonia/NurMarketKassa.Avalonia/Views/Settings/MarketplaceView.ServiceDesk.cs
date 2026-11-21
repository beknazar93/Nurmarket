using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-13: «Ремонт и сервис» (repair) и «Trade-in / скупка» (trade_in) — ввод ключа и открытие окна. Часть класса MarketplaceView; логика ключей та же, что у остальных функций.
public partial class MarketplaceView
{
    private void UnlockServiceDesk(string slug)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = slug == "repair"
            ? Tr.T("Ремонт и сервис", "Оңдоо жана тейлөө", "Repair and service", "Tamir ve servis", "Ta'mirlash va servis")
            : Tr.T("Trade-in / скупка", "Trade-in / сатып алуу", "Trade-in / buy-back", "Trade-in / alım", "Trade-in / sotib olish");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, slug, out var isPermanent))
        {
            PosAlertDialog.Show(owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        if (slug == "repair")
            UserPreferences.Instance.RepairUnlocked = true;
        else
            UserPreferences.Instance.TradeInUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor(slug);
        TariffGate.RaisePacksChanged(); // меню кассы перечитывает пункты
        ScheduleStoreReload();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.", "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    /// <summary>2026-11-19 («Смета»): модуль бесплатный — если ещё не включён (другая сфера), «Включить бесплатно» включает его (признак EstimateEnabled, сохраняется) и открывает окно; в сфере «Хозтовары и стройматериалы» он доступен сразу.
    /// Цена модуля — EstimateFeature.PriceSom (0); при ненулевой цене включение пойдёт через ключ, как у «Ремонта».</summary>
    private void OpenEstimate()
    {
        if (!EstimateFeature.IsAvailable)
        {
            UserPreferences.Instance.EstimateEnabled = true;
            UserPreferences.Instance.SaveToDisk();
            TariffGate.RaisePacksChanged(); // меню кассы перечитывает пункты
            ScheduleStoreReload();
        }
        var owner = TopLevel.GetTopLevel(this) as Window;
        EstimatesWindow.Open(owner);
    }

    /// <summary>2026-11-20 («Прокат (аренда)», решение владельца): модуль бесплатный. В строймаркете включён сразу; в других сферах «Включить бесплатно» ставит признак RentalEnabled (сохраняется, уходит на другие кассы)
    /// и открывает окно «Прокат». Цена — RentalFeature.PriceSom (0).</summary>
    private void OpenRental()
    {
        if (!RentalFeature.IsAvailable)
        {
            UserPreferences.Instance.RentalEnabled = true;
            UserPreferences.Instance.SaveToDisk();
            TariffGate.RaisePacksChanged(); // меню кассы и программы владельца перечитывают пункты
            ScheduleStoreReload();
        }
        var owner = TopLevel.GetTopLevel(this) as Window;
        _ = new NurMarketKassa.AvaloniaHost.Views.RentalsWindow(null, null).ShowDialog(owner!);
    }

    private void OpenServiceDesk(string slug)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        // В программе владельца окно — раздел её меню; из Маркетплейса открываем как обычное окно журнала.
        if (slug == "repair")
            RepairOrdersWindow.Open(owner);
        else
            TradeInWindow.Open(owner);
    }
}
