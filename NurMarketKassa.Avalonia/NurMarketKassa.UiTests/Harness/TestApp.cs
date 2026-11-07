using Avalonia;
using Avalonia.Headless;


namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): приложение для Headless — настоящий <see cref="App"/> (те же App.axaml: темы, стили, строки, шрифты), но без запуска
/// входа, оболочки и хоста служб (OnFrameworkInitializationCompleted кассы поднимает DI, сеть и окно входа). Окна под тестом получают то,
/// что нужно, прямо из теста.</summary>
public sealed class TestApp : NurMarketKassa.AvaloniaHost.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // 2026-11-06: намеренно пусто — хост служб и окно входа в тестах окон не нужны.
    }
}

/// <summary>Сборка Avalonia для Headless-тестов: Skia + реальная отрисовка (иначе CaptureRenderedFrame вернёт пустой кадр).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
