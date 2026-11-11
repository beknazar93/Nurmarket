using System.Runtime.InteropServices;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-11, жалоба владельца: «затемнение работает криво — сначала долго грузятся анимации, потом появляется тёмная модалка,
/// потом осветляется; дольше 2 секунд». Причина: под модалкой содержимое окна кассы размывалось BlurEffect, и на каждом кадре анимации
/// (16 мс) создавался новый эффект с другим радиусом — окно 1366×768…1920×1080 целиком перерисовывалось с размытием (встроенная видеокарта,
/// программный рендер — десятки и сотни мс на кадр), а первая отрисовка самой модалки и её появление шли в той же очереди отрисовки.
/// Общие правила для Windows/Linux/Android: фон — сплошное полупрозрачное затемнение без размытия, затемнение — сразу (без нарастания),
/// модалка — короткое появление; «режим слабого устройства» или выключенные анимации Windows — без анимаций.</summary>
public static class ModalMotion
{
    /// <summary>Появление модалки (непрозрачность 0 → 1), мс. Было 160 мс поверх нарастающего размытия.</summary>
    public const int DialogFadeMs = 110;

    /// <summary>Снятие затемнения после закрытия модалки, мс. Появление затемнения — сразу.</summary>
    public const int DimFadeOutMs = 90;

    /// <summary>Размытие фона под модалкой. По умолчанию выключено (см. описание класса).
    /// 2026-11-11 (решение владельца): включается настройкой «Размытие фона под окнами (для мощных ПК)» (UserPreferences.ModalBackgroundBlur) —
    /// читается при каждом открытии модалки, поэтому действует сразу; в «режиме слабого устройства» размытия нет никогда.</summary>
    public static bool BlurEnabled
    {
        get
        {
            try
            {
                var p = NurMarketKassa.Services.UserPreferences.Instance;
                return IsBlurOn(p.ModalBackgroundBlur, p.LowPerformanceMode);
            }
            catch
            {
                return false; // настройки ещё не загружены — без размытия
            }
        }
    }

    /// <summary>2026-11-11: чистое правило — размытие только по настройке и не на слабом устройстве.</summary>
    public static bool IsBlurOn(bool setting, bool lowPerformance) => setting && !lowPerformance;

    /// <summary>2026-11-11: радиус размытия — постоянный: эффект ставится один раз при затемнении (не новый на каждом кадре анимации).</summary>
    public const double BlurRadius = 6;

    /// <summary>Без анимаций: «режим слабого устройства» (Настройки → Экран) или в Windows выключены «Эффекты анимации».</summary>
    public static bool ReduceMotion
    {
        get
        {
            try
            {
                if (NurMarketKassa.Services.UserPreferences.Instance.LowPerformanceMode)
                    return true;
            }
            catch
            {
                // настройки ещё не загружены — решаем по системе
            }
            return SystemAnimationsOff();
        }
    }

    private static long _systemCheckedAt;
    private static bool _systemOff;

    /// <summary>Windows: «Параметры → Специальные возможности → Эффекты анимации» (SPI_GETCLIENTAREAANIMATION). Проверка не чаще раза в 30 с.</summary>
    private static bool SystemAnimationsOff()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var now = Environment.TickCount64;
        if (_systemCheckedAt != 0 && now - _systemCheckedAt < 30_000)
            return _systemOff;
        _systemCheckedAt = now;
        try
        {
            var on = 1;
            _systemOff = SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0) && on == 0;
        }
        catch
        {
            _systemOff = false;
        }
        return _systemOff;
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}

/// <summary>2026-11-11: журнал медленного открытия модалок — для диагностики на кассах клиентов. Строка [UI] пишется, только если от запроса
/// показа до первого кадра модалки прошло больше <see cref="SlowMs"/>, и не чаще раза в <see cref="Throttle"/> (пропущенные медленные открытия —
/// числом в следующей строке).</summary>
public static class ModalOpenTiming
{
    public const int SlowMs = 500;

    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(30);

    private static DateTime _lastLogUtc = DateTime.MinValue;
    private static int _suppressed;

    /// <summary>Последний замер (тип окна, мс до показа окна, мс до первого кадра) — для тестов и отладки.</summary>
    public static (string Dialog, double ToOpenedMs, double ToFirstFrameMs)? Last { get; private set; }

    /// <summary>Чистая проверка: писать ли строку (медленно и прошло достаточно времени с прошлой строки).</summary>
    public static bool ShouldLog(double totalMs, DateTime lastLogUtc, DateTime nowUtc) =>
        totalMs > SlowMs && nowUtc - lastLogUtc >= Throttle;

    /// <summary>Текст строки журнала.</summary>
    public static string Format(string dialog, double toOpenedMs, double totalMs, int suppressed) =>
        $"Модалка {dialog}: первый кадр через {totalMs:0} мс (до показа окна {toOpenedMs:0} мс, отрисовка {Math.Max(0, totalMs - toOpenedMs):0} мс)"
        + (suppressed > 0 ? $"; ещё медленных открытий без записи: {suppressed}." : ".");

    /// <summary>Замер готов (вызывается из потока интерфейса).</summary>
    public static void Report(string dialog, double toOpenedMs, double totalMs)
    {
        Last = (dialog, toOpenedMs, totalMs);
        ModalBlurGuard.Observe(totalMs); // 2026-11-11: включённое размытие и 3 медленных открытия подряд — предложить выключить
        if (totalMs <= SlowMs)
            return;
        var now = DateTime.UtcNow;
        if (!ShouldLog(totalMs, _lastLogUtc, now))
        {
            _suppressed++;
            return;
        }
        _lastLogUtc = now;
        var suppressed = _suppressed;
        _suppressed = 0;
        try
        {
            NurMarketKassa.Services.PosLogger.Log(Format(dialog, toOpenedMs, totalMs, suppressed), "UI");
        }
        catch
        {
            // журнал — диагностика, модалку не роняет
        }
    }
}

/// <summary>2026-11-11 (решение владельца): защита включённого размытия. Если модалки открываются медленно (первый кадр позже
/// <see cref="ModalOpenTiming.SlowMs"/> мс — то же, что строка журнала «Модалка …: первый кадр») <see cref="SlowInARow"/> раза подряд, касса
/// спрашивает «Размытие тормозит — выключить?». Один вопрос за запуск; «Да» — настройка выключается сразу.</summary>
public static class ModalBlurGuard
{
    public const int SlowInARow = 3;

    private static int _slowInARow;
    private static bool _asked;

    /// <summary>Чистый шаг: (новый счётчик медленных подряд, предложить ли выключить).</summary>
    public static (int SlowInARow, bool Suggest) Step(int slowInARow, bool alreadyAsked, bool blurOn, double totalMs)
    {
        if (!blurOn || alreadyAsked)
            return (0, false);
        var next = totalMs > ModalOpenTiming.SlowMs ? slowInARow + 1 : 0;
        return next >= SlowInARow ? (0, true) : (next, false);
    }

    /// <summary>Вопрос пользователю (тесты подменяют); true — выключить размытие.</summary>
    public static Func<Task<bool>> Ask { get; set; } = DefaultAskAsync;

    /// <summary>Сколько раз за запуск предлагали выключить (тесты, диагностика).</summary>
    public static int Suggestions { get; private set; }

    public static void Observe(double totalMs)
    {
        var (next, suggest) = Step(_slowInARow, _asked, ModalMotion.BlurEnabled, totalMs);
        _slowInARow = next;
        if (!suggest)
            return;
        _asked = true;
        Suggestions++;
        try
        {
            NurMarketKassa.Services.PosLogger.Log($"Размытие фона: {SlowInARow} медленных открытия модалок подряд (> {ModalOpenTiming.SlowMs} мс) — предлагаем выключить.", "UI");
        }
        catch
        {
            // журнал — диагностика
        }
        // После текущего кадра модалки: вопрос сам открывается модалкой.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (await Ask().ConfigureAwait(true))
                {
                    NurMarketKassa.Services.UserPreferences.SetModalBackgroundBlur(false);
                    NurMarketKassa.Services.PosLogger.Log("Размытие фона выключено по предложению кассы (медленные модалки).", "UI");
                }
            }
            catch (Exception ex)
            {
                try { NurMarketKassa.Services.PosLogger.Log($"Размытие фона: вопрос не показан ({ex.Message}).", "WARNING"); }
                catch { /* журнал недоступен */ }
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static Task<bool> DefaultAskAsync() =>
        App.GetRequiredService<NurMarketKassa.Core.Contracts.IUserPrompts>().ConfirmAsync(
            NurMarketKassa.Services.Tr.T(
                "Размытие тормозит — выключить? Окна открываются медленно, пока под ними размывается фон. Включить обратно: Настройки → Экран → Производительность.",
                "Бүдөмүктөтүү жайлатып жатат — өчүрөлүбү? Астындагы фон бүдөмүктөгөндө терезелер жай ачылат. Кайра күйгүзүү: Жөндөөлөр → Экран → Өндүрүмдүүлүк.",
                "Blur is slowing things down — turn it off? Windows open slowly while the background under them is blurred. To turn it back on: Settings → Screen → Performance.",
                "Bulanıklaştırma yavaşlatıyor — kapatılsın mı? Altındaki arka plan bulanıklaşırken pencereler yavaş açılıyor. Geri açmak için: Ayarlar → Ekran → Performans.",
                "Xiralashtirish sekinlashtiryapti — o'chirilsinmi? Ostidagi fon xiralashganda oynalar sekin ochiladi. Qayta yoqish: Sozlamalar → Ekran → Unumdorlik."),
            NurMarketKassa.Services.Tr.T("Выключить", "Өчүрүү", "Turn off", "Kapat", "O'chirish"),
            NurMarketKassa.Services.Tr.T("Оставить", "Калтыруу", "Keep", "Kalsın", "Qoldirish"));

    /// <summary>Только для тестов.</summary>
    public static void ResetForTests()
    {
        _slowInARow = 0;
        _asked = false;
        Suggestions = 0;
        Ask = DefaultAskAsync;
    }
}
