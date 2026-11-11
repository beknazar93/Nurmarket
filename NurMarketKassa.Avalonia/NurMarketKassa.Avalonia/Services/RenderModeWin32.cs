#if !ANDROID
// 2026-11-11: на Android сборки Avalonia.Win32 нет (проект берёт *.cs приложения глобом) — файл только для Windows/Linux-сборок.
using Avalonia;
using Avalonia.Win32;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11: режим отрисовки (RenderModeSettings) → параметры окна Windows. Чистая функция: null — параметры Avalonia по умолчанию
/// (AngleEgl + WinUIComposition/DirectComposition/RedirectionSurface), как было до настройки.</summary>
public static class RenderModeWin32
{
    public static Win32PlatformOptions? BuildOptions(string? mode, bool lowPerformance)
    {
        // «Режим слабого устройства» — программная отрисовка, как и раньше (Program.BuildAvaloniaApp).
        if (lowPerformance)
            return new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } };
        return RenderModeSettings.Normalize(mode) switch
        {
            RenderModeSettings.Software => new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } },
            // Видеокарта (ANGLE) остаётся, но окно выводится старым способом (RedirectionSurface) — без DirectComposition/WinUI-композиции,
            // на которых у части видеокарт после сворачивания оставалось пол-окна.
            RenderModeSettings.Compatible => new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
            },
            _ => null,
        };
    }
}
#endif
