namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит №4, Л4-06): поправка «часы сервера − часы ПК». Берётся из заголовка Date каждого успешного ответа NurCRM (его ставит
/// Cloudflare/сервер), усредняется по последним 5 замерам. Используется для sold_at офлайн-чеков и «серверного сегодня» (ReceiptEditPolicy.ClockSkew). Ограничения: разница меньше 2 с игнорируется (шум округления Date до секунды и задержки сети); больше 1 суток —
/// применяется только при согласных замерах (не меньше трёх, разброс до минуты; 2026-11-06, А5-07), иначе это сбой заголовка/прокси: поправка нулевая, предупреждение в журнал.
/// Расхождение от 5 минут при согласных замерах поднимает <see cref="WrongClockDetected"/> — касса показывает кассиру «Часы устройства неверны». Хранится только в памяти: после перезапуска первый же ответ сервера
/// пересчитывает поправку, а устаревшее значение на диске было бы вреднее (владелец мог уже поправить часы).</summary>
public static class ServerClock
{
    private const int SamplesMax = 5;
    private static readonly TimeSpan IgnoreBelow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IgnoreAbove = TimeSpan.FromDays(1);
    private static readonly object Sync = new();
    private static readonly Queue<double> Samples = new();
    private static TimeSpan _skew = TimeSpan.Zero;
    private static long _lastWarnTicks;
    private static long _lastWrongClockTicks;

    /// <summary>2026-11-06 (аудит №5, А5-07): расхождение от этого порога (по согласным замерам) — «часы устройства неверны»: кассиру показывается предупреждение, как в мобилке (плашка «Время на телефоне неверное»).</summary>
    public static readonly TimeSpan WrongClockThreshold = TimeSpan.FromMinutes(5);

    /// <summary>2026-11-06 (А5-07): замеры согласны: их не меньше трёх и все в пределах минуты от медианы (одиночный выброс или сбой заголовка не считаются).</summary>
    private const int ConsistentSamplesMin = 3;
    private const double ConsistentSpreadSeconds = 60;

    /// <summary>2026-11-06 (А5-07): часы устройства отличаются от серверных больше порога, и замеры согласны. Раз в 30 минут (пока так и есть) — для показа предупреждения кассиру.</summary>
    public static event Action<TimeSpan>? WrongClockDetected;

    /// <summary>2026-11-06 (А5-07): поправка применена и она больше порога — часы устройства неверны (для подсказок в интерфейсе).</summary>
    public static bool ClockLooksWrong => Skew.Duration() >= WrongClockThreshold;

    /// <summary>Поправка, которую прибавляют к часам ПК, чтобы получить время сервера. Ноль — часы сходятся (или поправка неизвестна/неприменима).</summary>
    public static TimeSpan Skew
    {
        get
        {
            lock (Sync)
                return _skew;
        }
    }

    /// <summary>Время сервера по часам ПК с поправкой.</summary>
    public static DateTimeOffset Now => DateTimeOffset.Now + Skew;

    /// <summary>Поправка применима только для отметок времени «сейчас/недавно» (офлайн-чек создан по часам ПК → прибавляем поправку).</summary>
    public static DateTimeOffset Correct(DateTimeOffset pcTime) => pcTime + Skew;

    /// <summary>Самая большая поправка, которую применяем к sold_at офлайн-чека (2026-11-05, ревью п. 4): больше — скорее сбой замера, чем реальный сдвиг часов.
    /// 2026-11-06 (А5-07): ограничение действует до суток; сдвиг больше суток применяется только после согласных замеров (см. Observe) и ограничением не режется —
    /// иначе терминал с датой «01.01.2025» слал бы чеки с sold_at 2025 года, а сервер отвечал «старше 7 суток».</summary>
    public static readonly TimeSpan SoldAtMaxCorrection = TimeSpan.FromMinutes(30);

    /// <summary>sold_at офлайн-чека: время продажи по часам ПК плюс поправка, ЗАПОМНЕННАЯ в момент продажи (null — поправки не было: время как есть). Поправка ограничена
    /// ±30 мин, результат не позже <paramref name="serverNow"/> (чек не может быть продан «в будущем»).</summary>
    public static DateTimeOffset SoldAt(DateTimeOffset createdAt, double? skewAtSaleSeconds, DateTimeOffset serverNow)
    {
        if (skewAtSaleSeconds is not { } sec || double.IsNaN(sec) || double.IsInfinity(sec))
            return createdAt;
        var skew = Math.Abs(sec) > IgnoreAbove.TotalSeconds
            ? TimeSpan.FromSeconds(sec) // больше суток: поправка запоминалась только при согласных замерах (Observe)
            : TimeSpan.FromSeconds(Math.Clamp(sec, -SoldAtMaxCorrection.TotalSeconds, SoldAtMaxCorrection.TotalSeconds));
        var corrected = createdAt + skew;
        return corrected > serverNow && serverNow >= createdAt ? serverNow : corrected;
    }

    /// <summary>Заголовок Date успешного ответа сервера. <paramref name="sentUtc"/> и <paramref name="receivedUtc"/> — часы ПК до отправки и после получения ответа: берётся середина.</summary>
    public static void Observe(DateTimeOffset? serverDate, DateTime sentUtc, DateTime receivedUtc)
    {
        try
        {
            if (serverDate is not { } sd)
                return;
            var rtt = receivedUtc - sentUtc;
            if (rtt < TimeSpan.Zero || rtt > TimeSpan.FromSeconds(10))
                return; // слишком долгий ответ — разница часов не отличить от задержки
            var pcMid = sentUtc + TimeSpan.FromTicks(rtt.Ticks / 2);
            // Date имеет точность до секунды (усечение вниз) — прибавляем полсекунды, чтобы не копить постоянный минус.
            var diff = (sd.UtcDateTime + TimeSpan.FromMilliseconds(500)) - pcMid;
            TimeSpan? wrongClock = null;
            lock (Sync)
            {
                Samples.Enqueue(diff.TotalSeconds);
                while (Samples.Count > SamplesMax)
                    Samples.Dequeue();
                var avg = TimeSpan.FromSeconds(Median(Samples)); // 2026-11-05 (ревью п. 20): медиана — один выброс (прокси, вход в Wi-Fi) поправку не сдвигает
                var abs = avg.Duration();
                var consistent = Samples.Count >= ConsistentSamplesMin && Samples.All(x => Math.Abs(x - avg.TotalSeconds) <= ConsistentSpreadSeconds);
                if (abs > IgnoreAbove)
                {
                    // 2026-11-06 (аудит №5, А5-07): сдвиг больше суток раньше НЕ применялся вовсе (как мобилка не делает): терминал с неверной датой слал офлайн-чеки с sold_at из прошлого
                    // («старше 7 суток» — чеки застревали). Теперь при согласных замерах (не меньше трёх, разброс до минуты) поправка применяется; разовый выброс по-прежнему игнорируется.
                    _skew = consistent ? avg : TimeSpan.Zero;
                    if (DateTime.UtcNow.Ticks - _lastWarnTicks > TimeSpan.FromMinutes(30).Ticks)
                    {
                        _lastWarnTicks = DateTime.UtcNow.Ticks;
                        PosLogger.Log(consistent
                            ? $"Часы устройства отличаются от сервера на {avg.TotalHours:0.#} ч (больше суток) — замеры согласны, поправка применена к времени офлайн-продаж. Исправьте дату и время."
                            : $"Часы устройства отличаются от сервера на {avg.TotalHours:0.#} ч (больше суток), но замеры не согласны — поправка не применяется, проверьте дату и время.", "WARNING");
                    }
                }
                else
                {
                    _skew = abs < IgnoreBelow ? TimeSpan.Zero : avg;
                }

                if (consistent && abs >= WrongClockThreshold && DateTime.UtcNow.Ticks - _lastWrongClockTicks > TimeSpan.FromMinutes(30).Ticks)
                {
                    _lastWrongClockTicks = DateTime.UtcNow.Ticks;
                    wrongClock = avg;
                }

                ReceiptEditPolicy.ClockSkew = _skew;
            }

            // Вне замка: подписчик показывает окно/плашку.
            if (wrongClock is { } wrong)
            {
                try { WrongClockDetected?.Invoke(wrong); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Предупреждение о часах не показано: {ex.Message}", "WARNING"); }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Поправка времени — удобство: ошибка расчёта никогда не ломает запрос.
        }
    }

    /// <summary>Только для тестов: забыть замеры и поправку.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            Samples.Clear();
            _skew = TimeSpan.Zero;
            _lastWarnTicks = 0;
            _lastWrongClockTicks = 0;
            ReceiptEditPolicy.ClockSkew = TimeSpan.Zero;
        }
    }

    /// <summary>Медиана замеров (чётное число — среднее двух средних).</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
