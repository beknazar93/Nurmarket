using System.IO;
using System.Runtime.CompilerServices;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-05 (аудит №4, Л4-01/Л4-04): ВСЕ тесты работают только во временной папке.
/// Инициализатор модуля выполняется при загрузке сборки тестов — раньше любого сервиса кассы (DatabaseService, UserPreferences, хранилища).
/// Он ставит NURMARKET_DATA_ROOT на уникальную папку; AppPaths.Roaming / AppPaths.Local везде ведут внутрь неё, настоящий %APPDATA%
/// не читается и не пишется. Скрипт tools/qa/test-safe.ps1 может задать свою родительскую папку той же переменной.</summary>
internal static class TestDataRoot
{
    /// <summary>Уникальная папка данных этого прогона тестов.</summary>
    public static string Path { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        var parent = Environment.GetEnvironmentVariable(AppPaths.DataRootVariable)?.Trim();
        if (string.IsNullOrEmpty(parent))
            parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nurmarket-tests");
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable(AppPaths.DataRootVariable, Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* файл может быть занят (SQLite) — временная папка, не страшно */ }
        };
    }
}
