using System.Runtime.CompilerServices;
using DiffEngine;
using VerifyTests;

namespace NurMarketKassa.Tests.Snapshots;

/// <summary>2026-11-05: настройка Verify (снимки текста чека, этикеток TSPL). Эталоны лежат в папке Snapshots/Verified проекта тестов;
/// сравнение с внешней программой отключено (не открывать окна на ПК кассы). Ничего не пишется за пределами папки тестов.</summary>
internal static class VerifyInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        DiffRunner.Disabled = true;
        Verifier.DerivePathInfo((sourceFile, projectDirectory, type, method) =>
            new PathInfo(Path.Combine(projectDirectory, "Snapshots", "Verified"), type.Name, method.Name));
    }
}
