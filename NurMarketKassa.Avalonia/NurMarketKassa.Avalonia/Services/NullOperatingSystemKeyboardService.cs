using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: В4 АЛ-5 — на Linux и Android экранной клавиатуры osk.exe нет, а служба для Windows регистрировалась без проверки ОС и при каждом
/// касании поля пыталась запустить несуществующую программу (исключение в журнал). Здесь — пустая замена: Android показывает свою клавиатуру сам,
/// на Linux её даёт окружение рабочего стола (своей экранной клавиатуры касса не рисует).</summary>
public sealed class NullOperatingSystemKeyboardService : IOperatingSystemKeyboardService
{
    private static bool _logged;

    public void ShowSystemKeyboard()
    {
        if (_logged)
            return;
        _logged = true;
        PosLogger.Log("Экранная клавиатура: на этой системе её запускает сама система (osk.exe — только Windows).", "UI");
    }
}
