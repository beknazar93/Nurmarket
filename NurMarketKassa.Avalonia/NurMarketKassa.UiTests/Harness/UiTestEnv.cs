using System.Runtime.CompilerServices;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): сеть — только на заглушку (случайный вызов не дойдёт до app.nurcrm.kg). Папка данных — TestDataRoot (ссылка на файл
/// проекта обычных тестов): все пути AppPaths ведут во временную папку, настоящие pos_local.db и user-settings.json не читаются и не пишутся.</summary>
internal static class UiTestEnv
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKTOP_MARKET_API_URL")))
            Environment.SetEnvironmentVariable("DESKTOP_MARKET_API_URL", "http://127.0.0.1:9");
    }
}
