using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Platform;


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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => HeadlessRootServices.CopyTextServices());
}

/// <summary>2026-11-19 (падения «Unable to locate 'Avalonia.Platform.IFontManagerImpl'», затем «Кадр не получен» у всех следующих тестов):
/// Avalonia.Headless 11.2 перед каждым тестом входит в новую пустую область служб (AvaloniaLocator.EnterScope) и вызывает
/// Dispatcher.ResetForUnitTests — тот доделывает задания, оставшиеся от прошлого теста (продолжения фоновых загрузок окна, отрисовка).
/// В пустой области ещё нет шрифтов и шейпера текста — разметка TextBlock падала, сессия ломалась, и дальше ни один кадр не получался
/// (252 «Кадр не получен» после первой же такой ошибки). Новая область наследует корневую — кладём туда шрифты и шейпер Skia один раз,
/// после первой настройки приложения; задания-хвосты доделываются без ошибки.</summary>
internal static class HeadlessRootServices
{
    // AvaloniaLocator.Current/CurrentMutable и Bind<T>() в Avalonia 11 — internal (видны только своим сборкам), поэтому через отражение.
    private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
    private static object? _root;
    private static bool _copied;

    private static object? CurrentLocator() => typeof(AvaloniaLocator).GetProperty("CurrentMutable", Any)?.GetValue(null);

    /// <summary>При загрузке сборки тестов ни одной области ещё нет — текущий локатор и есть корневой.</summary>
    [ModuleInitializer]
    internal static void CaptureRoot() => _root = CurrentLocator();

    internal static void CopyTextServices()
    {
        try
        {
            var current = CurrentLocator();
            if (_copied || _root is null || current is null || ReferenceEquals(_root, current))
                return;
            var getService = typeof(AvaloniaLocator).GetMethods(Any).First(m => m.Name.EndsWith("GetService", StringComparison.Ordinal) &&!m.IsGenericMethodDefinition
                                                                               && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(Type));
            var fonts = getService.Invoke(current, [typeof(IFontManagerImpl)]);
            var shaper = getService.Invoke(current, [typeof(ITextShaperImpl)]);
            if (fonts is null || shaper is null)
                return;
            BindConstant(typeof(IFontManagerImpl), fonts);
            BindConstant(typeof(ITextShaperImpl), shaper);
            // 2026-11-21 (PlatformNotSupportedException в Dispatcher.PushFrame, 1 мс, в случайных тестах: 3 раза за ~10 полных прогонов): Dispatcher.UIThread создаётся при первом обращении
            // и берёт IDispatcherImpl из ТЕКУЩЕЙ области служб; если не нашёл — делает NullDispatcherImpl, у которого PushFrame бросает PlatformNotSupportedException. Headless кладёт реализацию
            // только в область теста, и фоновая задача прежнего теста, обратившаяся к Dispatcher.UIThread в промежутке между сбросом диспетчера и настройкой платформы нового теста
            // (или вне области), получала такой «пустой» диспетчер. Кладём ту же реализацию и в корневую область — «пустого» диспетчера больше не получается.
            var dispatcherType = typeof(Avalonia.Threading.Dispatcher).Assembly.GetType("Avalonia.Threading.IDispatcherImpl");
            if (dispatcherType is not null && getService.Invoke(current, [dispatcherType]) is { } dispatcherImpl)
                BindConstant(dispatcherType, dispatcherImpl);
            _copied = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HeadlessRootServices: не удалось положить шрифты в корневую область: {ex.Message}");
        }
    }

    /// <summary>_root.Bind&lt;T&gt;().ToConstant(value).</summary>
    private static void BindConstant(Type service, object value)
    {
        var bind = typeof(AvaloniaLocator).GetMethods(Any).First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var helper = bind.MakeGenericMethod(service).Invoke(_root, null)!;
        var toConstant = helper.GetType().GetMethods(Any).First(m => m.Name == "ToConstant" && m.GetParameters().Length == 1);
        if (toConstant.IsGenericMethodDefinition)
            toConstant = toConstant.MakeGenericMethod(value.GetType());
        toConstant.Invoke(helper, [value]);
    }
}
