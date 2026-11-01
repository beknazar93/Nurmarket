using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-01 (П-10, решение владельца «да»): право сотрудника «Печать штрих-кодов» (<see cref="PosPermissions.ViewLabels"/>, can_view_market_label).
/// Владелец и администратор — без ограничений; сервер не прислал поле (старый ответ) — НЕ блокируем; закрыто только когда право явно false
/// (правило — PermissionService.IsLabelPrintAllowed). Проверка стоит на всех входах в печать штрихкодов, ценников и этикеток.</summary>
public static class BarcodePrintAccess
{
    public static bool IsAllowed()
    {
        try
        {
            return App.AppHost?.Services.GetService<IPermissionService>()?.HasPermission(PosPermissions.ViewLabels) ?? true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print permission not read: {ex.GetType().Name}", "WARNING");
            return true; // не удалось прочитать — доступ не отнимаем
        }
    }

    public static string DeniedText => Tr.T(
        "Нет права «Печать штрих-кодов». Попросите владельца включить его на сайте app.nurcrm.kg: Сотрудники → ваш сотрудник → Доступы → «Печать штрих-кодов».",
        "«Штрих-код басып чыгаруу» укугу жок. Ээсинен аны app.nurcrm.kg сайтында күйгүзүүнү сураныңыз: Кызматкерлер → сиздин кызматкер → Укуктар → «Штрих-код басып чыгаруу».",
        "No “Barcode printing” permission. Ask the owner to turn it on at app.nurcrm.kg: Employees → your employee → Permissions → “Barcode printing”.",
        "«Barkod yazdırma» izni yok. Sahibinden app.nurcrm.kg sitesinde açmasını isteyin: Personel → personeliniz → Erişimler → «Barkod yazdırma».",
        "«Shtrix-kod chop etish» huquqi yo'q. Egasidan uni app.nurcrm.kg saytida yoqishni so'rang: Xodimlar → sizning xodimingiz → Huquqlar → «Shtrix-kod chop etish».");

    public static string DeniedHint => Tr.T("Нет права «Печать штрих-кодов»", "«Штрих-код басып чыгаруу» укугу жок", "No “Barcode printing” permission", "«Barkod yazdırma» izni yok", "«Shtrix-kod chop etish» huquqi yo'q");

    /// <summary>true — можно. Нет — показывает понятное сообщение (не исключение) и возвращает false.</summary>
    public static bool Ensure(Window? owner)
    {
        if (IsAllowed())
            return true;
        PosMessageBox.Show(owner, DeniedText, Tr.T("Печать штрих-кодов", "Штрих-код басып чыгаруу", "Barcode printing", "Barkod yazdırma", "Shtrix-kod chop etish"));
        return false;
    }
}
