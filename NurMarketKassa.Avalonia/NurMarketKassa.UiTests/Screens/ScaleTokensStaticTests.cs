using System.Text.RegularExpressions;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Д-8, согласие владельца 13.11): единая шкала размеров. Статическая проверка ОБЩИХ стилей программы: размер шрифта, радиус и
/// высота элемента задаются ключами шкалы (NurFont*, NurRadius*, NurHeight* из App.axaml), а не числами. Разрешённые исключения перечислены ниже
/// с причиной. Файлы окон сюда добавляются по мере перевода на шкалу.</summary>
public sealed class ScaleTokensStaticTests
{
    /// <summary>Общие стили, уже переведённые на шкалу (пути от папки проекта приложения).</summary>
    private static readonly string[] Files =
    [
        "App.axaml",
        "Themes/AppControlStyles.axaml",
        "Views.axaml/Dialogs/PosDialogStyles.axaml",
    ];

    /// <summary>Исключения: «файл|строка содержит» — причина.</summary>
    private static readonly string[] Allowed =
    [
        // «таблетка»: радиус равен половине высоты чипа, это форма, а не шаг шкалы
        "PosDialogStyles.axaml|Property=\"CornerRadius\" Value=\"22\"",
        "PosDialogStyles.axaml|CornerRadius=\"25\"",
    ];

    private static readonly Regex Number = new(
        "(FontSize=\"[0-9.]+\")|(Property=\"FontSize\" Value=\"[0-9.]+\")|(CornerRadius=\"[0-9.,]+\")|(Property=\"CornerRadius\" Value=\"[0-9.,]+\")|(Property=\"(Min)?Height\" Value=\"[0-9.]+\")",
        RegexOptions.Compiled);

    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "NurMarketKassa.Avalonia", "App.axaml");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Не найдена папка проекта приложения (App.axaml).");
    }

    [Fact]
    public void SharedStyles_UseScaleKeys_NotNumbers()
    {
        var root = AppDir();
        var problems = new List<string>();
        foreach (var rel in Files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Number.Matches(lines[i]))
                {
                    var file = Path.GetFileName(rel);
                    if (Allowed.Any(a => a.StartsWith(file + "|", StringComparison.Ordinal) && lines[i].Contains(a[(file.Length + 1)..], StringComparison.Ordinal)))
                        continue;
                    problems.Add($"{rel}:{i + 1}: {m.Value} — число вне шкалы, нужен ключ NurFont* / NurRadius* / NurHeight*");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Scale_HasFewSteps()
    {
        var app = File.ReadAllText(Path.Combine(AppDir(), "App.axaml"));
        Assert.Equal(5, Regex.Matches(app, "x:Key=\"NurFont[A-Za-z]+\"").Count);
        Assert.Equal(3, Regex.Matches(app, "x:Key=\"NurRadius[A-Za-z]+\"").Count);
        Assert.Equal(3, Regex.Matches(app, "x:Key=\"NurHeight[A-Za-z]+\"").Count);
    }
}
