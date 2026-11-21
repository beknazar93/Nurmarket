using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-21 (флак FirstHourUiTests.TypedQuantityOutsideField…, «7 вместо 4», «5 вместо 4» только в полном прогоне): касса отличает сканер от человека по паузам между нажатиями
/// (цифры быстрее 50 мс подряд, Enter сразу за ними — скан; иначе ручной ввод). В Headless нажатия синтетические, и пауза бралась из настоящих часов обработки: на нагруженной машине
/// 13 цифр «сканера» растягивались дольше порога, и скан превращался в ручной ввод (лишняя единица в чеке). Касса уже умеет брать время нажатия у платформы
/// (AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestampProvider, так сделан Android) — тест подставляет свои «часы»: каждое нажатие сканера — ровно +8 мс, «человеческая» пауза — ровно
/// столько, сколько задал тест, независимо от того, насколько медленно машина обработала события.</summary>
public sealed class VirtualKeyClock : IDisposable
{
    private readonly Func<ulong?>? _previous = AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestampProvider;
    private ulong _now = 1_000_000;

    public VirtualKeyClock() => AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestampProvider = () => _now;

    /// <summary>Следующее нажатие — через <paramref name="ms"/> после предыдущего (сканер — 8 мс).</summary>
    public void Tick(int ms = 8) => _now += (ulong)ms;

    /// <summary>Человек задумался на <paramref name="ms"/> мс: часы идут вперёд, интерфейс успевает обработать то, что накопилось.</summary>
    public void Pause(int ms)
    {
        _now += (ulong)ms;
        Thread.Sleep(ms); // часть кода кассы (сброс буфера цифр по таймеру) считает по настоящим часам — пауза нужна и им
        UiShot.Settle(2, 15);
    }

    public void Dispose() => AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestampProvider = _previous;
}
