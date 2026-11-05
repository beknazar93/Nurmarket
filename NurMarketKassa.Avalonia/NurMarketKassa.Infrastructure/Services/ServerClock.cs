namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит №4, Л4-06): поправка «часы сервера − часы ПК». Берётся из заголовка Date каждого успешного ответа NurCRM (его ставит
/// Cloudflare/сервер), усредняется по последним 5 замерам. Используется для sold_at офлайн-чеков и «серверного сегодня» (ReceiptEditPolicy.ClockSkew). Ограничения: разница меньше 2 с игнорируется (шум округления Date до секунды и задержки сети); больше 1 суток — тоже не
/// применяется (скорее всего сбой заголовка/прокси), в журнал идёт предупреждение. Хранится только в памяти: после перезапуска первый же ответ сервера
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

    /// <summary>Самая большая поправка, которую применяем к sold_at офлайн-чека (2026-11-05, ревью п. 4): больше — скорее сбой замера, чем реальный сдвиг часов.</summary>
    public static readonly TimeSpan SoldAtMaxCorrection = TimeSpan.FromMinutes(30);

    /// <summary>sold_at офлайн-чека: время продажи по часам ПК плюс поправка, ЗАПОМНЕННАЯ в момент продажи (null — поправки не было: время как есть). Поправка ограничена
    /// ±30 мин, результат не позже <paramref name="serverNow"/> (чек не может быть продан «в будущем»).</summary>
    public static DateTimeOffset SoldAt(DateTimeOffset createdAt, double? skewAtSaleSeconds, DateTimeOffset serverNow)
    {
        if (skewAtSaleSeconds is not { } sec || double.IsNaN(sec) || double.IsInfinity(sec))
            return createdAt;
        var skew = TimeSpan.FromSeconds(Math.Clamp(sec, -SoldAtMaxCorrection.TotalSeconds, SoldAtMaxCorrection.TotalSeconds));
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
            lock (Sync)
            {
                Samples.Enqueue(diff.TotalSeconds);
                while (Samples.Count > SamplesMax)
                    Samples.Dequeue();
                var avg = TimeSpan.FromSeconds(Median(Samples)); // 2026-11-05 (ревью п. 20): медиана — один выброс (прокси, вход в Wi-Fi) поправку не сдвигает
                var abs = avg.Duration();
                if (abs > IgnoreAbove)
                {
                    _skew = TimeSpan.Zero;
                    if (DateTime.UtcNow.Ticks - _lastWarnTicks > TimeSpan.FromMinutes(30).Ticks)
                    {
                        _lastWarnTicks = DateTime.UtcNow.Ticks;
                        PosLogger.Log($"Часы ПК отличаются от сервера на {avg.TotalHours:0.#} ч (больше суток) — поправка не применяется, проверьте дату и время компьютера.", "WARNING");
                    }
                }
                else
                {
                    _skew = abs < IgnoreBelow ? TimeSpan.Zero : avg;
                }

                ReceiptEditPolicy.ClockSkew = _skew;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Поправка времени — удобство: ошибка расчёта никогда не ломает запрос.
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
