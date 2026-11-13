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
