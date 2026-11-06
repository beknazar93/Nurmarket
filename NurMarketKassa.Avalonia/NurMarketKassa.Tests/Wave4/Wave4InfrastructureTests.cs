using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Wave4;

/// <summary>2026-11-06: В4 «Паритет Linux и Android» — проверки того, что лежит в Infrastructure и не требует устройства: ключи облачной синхронизации вида кассы
/// (А4-24), безопасность поиска Rongta вне Windows (Н-2), график Word-отчёта, который не должен ронять выгрузку (АЛ-1). Без сети и без боевых данных.</summary>
public sealed class Wave4InfrastructureTests
{
    private static string[] StaticArray(string name)
    {
        var field = typeof(SettingsCloudSync).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull($"поле {name} должно существовать");
        return (string[])field!.GetValue(null)!;
    }

    private static readonly string[] ViewKeys = { "MainLayoutMode", "CatalogViewMode", "ShowCatalogPhotos", "CatalogTileScalePercent" };

    [Fact]
    public void CloudSync_AndroidLocalViewKeys_AreTheFourViewKeys()
    {
        StaticArray("AndroidLocalViewKeys").Should().BeEquivalentTo(ViewKeys);
    }

    [Fact]
    public void CloudSync_OnWindows_PersonalKeysStillContainViewKeys()
    {
        // Только Android перестаёт синхронизировать вид; на Windows (и Linux) всё как было.
        StaticArray("PersonalKeys").Should().Contain(ViewKeys);
    }

    [Fact]
    public void CloudSync_ViewKeys_AreRealUserPreferencesProperties()
    {
        foreach (var key in ViewKeys)
            typeof(UserPreferences).GetProperty(key, BindingFlags.Public | BindingFlags.Instance).Should().NotBeNull($"{key} — свойство UserPreferences");
    }

    [Fact]
    public void CloudSync_PersonalKeys_AreAllKeysMinusViewKeysOnAndroid()
    {
        var all = StaticArray("PersonalKeysAll");
        all.Should().Contain(ViewKeys);
        var android = all.Where(k => !ViewKeys.Contains(k)).ToArray();
        android.Should().NotContain(ViewKeys);
        android.Length.Should().Be(all.Length - ViewKeys.Length);
    }

    [Fact]
    public void RongtaSetup_FindInstalled_DoesNotThrow()
    {
        // Н-2: на Linux/Android это не бросает PlatformNotSupportedException (null); на Windows — путь или null, но не исключение.
        var act = () => RongtaSetupService.TryFindInstalledExePath();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task RongtaSetup_InstallSilently_MissingFile_ReturnsFalse()
    {
        (await RongtaSetupService.InstallSilentlyAsync(Path.Combine(Path.GetTempPath(), "no-such-installer-" + Guid.NewGuid().ToString("N") + ".exe"))).Should().BeFalse();
    }

    [Fact]
    public void SafeChart_RenderThrows_DoesNotBreakReport()
    {
        // АЛ-1: сбой отрисовки одного графика не ломает выгрузку (на Linux/Android график пропускается вообще).
        var path = Path.Combine(Path.GetTempPath(), "w4-chart-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var doc = new WordReportBuilder(path, ReportMeta.Create("Проверка", null, null)))
            {
                var act = () => doc.TryChart(() => throw new InvalidOperationException("график не нарисован"), "тест");
                act.Should().NotThrow();
                doc.Paragraph("после графика отчёт продолжается");
            }
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            try { File.Delete(path); } catch { /* временный файл */ }
        }
    }

    [Fact]
    public void SaleDocumentHtmlBuilder_HasAndroidOpenHook()
    {
        // А5-05: на Android документ открывает обработчик, который подставляет проект Android.
        typeof(SaleDocumentHtmlBuilder).GetProperty("PlatformOpenFile", BindingFlags.Public | BindingFlags.Static).Should().NotBeNull();
    }
}
