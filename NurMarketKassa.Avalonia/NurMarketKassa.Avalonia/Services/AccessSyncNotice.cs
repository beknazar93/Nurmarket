using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11 (доступы и маркетплейс «с сайта»): ненавязчивая плашка «Доступы обновлены с сайта», когда фоновая проверка (AccessSync,
/// PermissionRecheck) увидела другие доступы или купленные функции. Один раз на изменение и не чаще раза в 5 минут; на Android плашка — это окно,
/// поэтому там только журнал (меню и разделы обновляются и без сообщения).</summary>
internal static class AccessSyncNotice
{
    private static long _lastShownTicks;

    public static void Show()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastShownTicks) < TimeSpan.FromMinutes(5).Ticks)
            return;
        Interlocked.Exchange(ref _lastShownTicks, now);
        if (OperatingSystem.IsAndroid())
            return;
        try
        {
            App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(Tr.T("Доступы обновлены с сайта", "Уруксаттар сайттан жаңыртылды",
                "Access updated from the website", "Erişimler siteden güncellendi", "Ruxsatlar saytdan yangilandi"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Права: плашка «Доступы обновлены» не показана ({ex.Message}).", "WARNING");
        }
    }
}
