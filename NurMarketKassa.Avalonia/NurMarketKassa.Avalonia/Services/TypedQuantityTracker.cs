namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Н-2, одно поле ввода): кассир набирает «3», «*» и сканирует товар, не ставя курсор в поле (как на обычных кассах).
/// Окно кассы сообщает сюда цифры, набранные ВНЕ полей ввода; на «*» трекер отдаёт количество, если цифры набраны рукой (паузы между клавишами не меньше
/// <see cref="HumanGapMs"/> — сканер шлёт клавиши плотнее и сюда не попадает), их 1–4 и с последней прошло не больше <see cref="StaleMs"/>.
/// Цифры не «съедаются»: буфер сканера получает их как раньше. Чистая логика — проверяется тестом.</summary>
internal sealed class TypedQuantityTracker
{
    public const int HumanGapMs = 60;
    public const int StaleMs = 4000;
    private const int MaxDigits = 4;

    private string _digits = "";
    private long _lastTick;
    private bool _tooFast;

    /// <summary>Цифра, нажатая вне поля ввода.</summary>
    public void Digit(char digit, long tickMs)
    {
        var gap = tickMs - _lastTick;
        if (_digits.Length == 0 || gap > StaleMs || gap < 0)
        {
            _digits = "";
            _tooFast = false;
        }
        else if (gap < HumanGapMs)
        {
            _tooFast = true; // так быстро печатает сканер — это код, не количество
        }
        _digits += digit;
        _lastTick = tickMs;
    }

    /// <summary>Любая другая клавиша — набранные цифры количеством не считаются.</summary>
    public void Reset()
    {
        _digits = "";
        _tooFast = false;
    }

    /// <summary>Нажата «*». Возвращает количество («3»), если перед ней рукой набрано 1–4 цифры (не ноль); иначе null. Набранное в любом случае забывается.</summary>
    public string? Star(long tickMs)
    {
        var digits = _digits;
        var gap = tickMs - _lastTick;
        var ok = digits.Length is > 0 and <= MaxDigits && !_tooFast && gap >= HumanGapMs && gap <= StaleMs
                 && int.TryParse(digits, out var value) && value > 0;
        Reset();
        return ok ? int.Parse(digits).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
