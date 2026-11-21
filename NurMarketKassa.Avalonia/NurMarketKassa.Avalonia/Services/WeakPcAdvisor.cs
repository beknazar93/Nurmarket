using Avalonia.Controls;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-21 (просьба владельца «предложи автоопределение слабого ПК»): один раз на компьютере, если у него мало оперативной памяти (до 4 ГБ) или
/// не больше двух ядер, касса спрашивает «Включить режим слабого ПК?». «Включить» — режим включается сразу (см. <see cref="LowPerformanceLive"/>), «Не нужно» —
/// больше не спрашиваем (<see cref="UserPreferences.WeakPcHintShown"/>); включить можно и позже: Настройки → Экран → Производительность.
/// Не спрашивает: на Android (там свой режим малой памяти), в программе владельца, в стендах с подменой папки данных (тесты) и если режим уже включён.</summary>
public static class WeakPcAdvisor
{
    /// <summary>Сколько ждать после показа окна, прежде чем спросить: кассир сначала видит свою кассу.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>Чистое правило: спрашивать ли (тесты подставляют цифры).</summary>
    public static bool ShouldAsk(bool alreadyShown, bool lowModeOn, long totalMemoryBytes, int logicalCores) =>
        !alreadyShown && !lowModeOn && UserPreferences.LooksLikeWeakPc(totalMemoryBytes, logicalCores);

    /// <summary>Оперативная память (всего, байт) и число логических ядер этого компьютера.</summary>
    public static (long MemoryBytes, int Cores) Probe()
    {
        long memory = 0;
        try
        {
            memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            // цифры недоступны — не считаем ПК слабым
        }
        return (memory, Environment.ProcessorCount);
    }

    public static string Question() => Tr.T(
        "Этот компьютер выглядит слабым (мало памяти или ядер). Включить режим слабого ПК? Без анимаций, размытия и узоров, реже опрос сервера — касса работает плавнее. Включается сразу, без перезапуска; выключить можно в Настройки → Экран → Производительность.",
        "Бул компьютер алсыздай (эс тутум же ядролор аз). Алсыз ПК режимин күйгүзөлүбү? Анимациясыз, бүдөмүктөтүүсүз жана оюусуз, сервер сейректей сурайт — касса жеңилирээк иштейт. Дароо, кайра ачпастан күйөт; өчүрүү: Жөндөөлөр → Экран → Өндүрүмдүүлүк.",
        "This computer looks weak (little memory or few cores). Turn on the weak-PC mode? No animations, blur or patterns, less frequent server polling — the till runs smoother. It turns on right away, no restart; you can turn it off in Settings → Screen → Performance.",
        "Bu bilgisayar zayıf görünüyor (az bellek veya çekirdek). Zayıf bilgisayar modu açılsın mı? Animasyon, bulanıklık ve desen yok, sunucu daha seyrek sorgulanır — kasa daha akıcı çalışır. Yeniden başlatmadan hemen açılır; Ayarlar → Ekran → Performans'tan kapatabilirsiniz.",
        "Bu kompyuter zaif ko'rinadi (xotira yoki yadrolar kam). Zaif kompyuter rejimi yoqilsinmi? Animatsiya, xiralashtirish va naqshlarsiz, server kamroq so'raladi — kassa tekisroq ishlaydi. Qayta ishga tushirmasdan darhol yoqiladi; o'chirish: Sozlamalar → Ekran → Unumdorlik.");

    /// <summary>Вызывается из главного окна кассы после его показа; не блокирует (ждёт спокойного момента сам).</summary>
    public static async Task MaybeAskAsync(Window owner)
    {
        try
        {
            if (OperatingSystem.IsAndroid() || AppMode.IsOwner || AppPaths.DataRootOverride is not null)
                return;
            var prefs = UserPreferences.Instance;
            var (memory, cores) = Probe();
            if (!ShouldAsk(prefs.WeakPcHintShown, prefs.LowPerformanceMode, memory, cores))
                return;

            await Task.Delay(SettleDelay).ConfigureAwait(true);
            // Спрашиваем, только когда касса на виду и поверх нет других окон (оплата, вопрос); иначе — в следующий запуск.
            if (!owner.IsVisible || owner.WindowState == WindowState.Minimized)
                return;
#if !NURANDROID
            if (Views.Dialogs.ModalWindowManager.PreferredOwner() is { } top && !ReferenceEquals(top, owner))
                return;
#endif
            prefs.WeakPcHintShown = true;
            prefs.SaveToDisk();
            PosLogger.Log($"Слабый ПК (память {memory / (1024 * 1024)} МБ, ядер {cores}) — предлагаем режим слабого устройства.", "SETTINGS");
            var yes = await NurMarketKassa.AvaloniaHost.App.GetRequiredService<IUserPrompts>().ConfirmAsync(
                Question(),
                Tr.T("Включить", "Күйгүзүү", "Turn on", "Aç", "Yoqish"),
                Tr.T("Не нужно", "Кереги жок", "No need", "Gerek yok", "Kerak emas")).ConfigureAwait(true);
            if (yes)
                UserPreferences.SetLowPerformanceMode(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Слабый ПК: предложение режима не показано ({ex.Message}).", "WARNING");
        }
    }
}
