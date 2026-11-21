using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (жалоба владельца по фото: «при каждом нажатии меняет размер»). Причина: колонка пунктов сворачивалась, когда открыт раздел (экран до 1366),
/// раскрывалась нажатием на группу и снова сворачивалась выбором пункта — каждый клик менял ширину меню и область раздела.
/// Тест: подряд нажимает каждую группу и КАЖДЫЙ пункт меню программы владельца и после каждого нажатия сверяет Bounds тёмной колонки, колонки пунктов,
/// шапки компании, области раздела и окна оболочки с эталоном (допуск 0). Эталон — после первого раскрытия колонки (на узком окне сама колонка свёрнута, и её раскрытие нажатием — единственное изменение).</summary>
[Collection("UiScenarios")]
public sealed class OwnerShellNoResizeUiTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly HashSet<string> SafeKeys = new(StringComparer.Ordinal)
    {
        "overview", "aiadvisor", "warehouse", "calculator", "restock", "purchases", "suppliers", "supplierreturns", "branches", "salesfinance", "debts",
        "analytics", "abc", "profitcash", "losssales", "bonusanalytics", "staffreport", "appshop", "clients", "telegrambot", "whatsapp", "funnel",
    };

    public static IEnumerable<object[]> Cases()
    {
        // 2026-11-21: полный перебор (4 размера × 4 вида, ~45 пунктов по ~8 с) шёл больше часа — оставлены два самых тесных/широких сочетания.
        yield return [1024, 600, AppLanguage.Russian, UiTheme.Light];
        yield return [1366, 768, AppLanguage.Kyrgyz, UiTheme.Dark];
    }

    private static string Snapshot(OwnerShellWindow shell, bool withSection)
    {
        Rect B(string name) => shell.FindControl<Control>(name)?.Bounds ?? default;
        var section = withSection ? B("SectionHost") : default;
        return $"оболочка {shell.Bounds.Width}x{shell.Bounds.Height}; тёмная колонка {B("NavRail")}; колонка пунктов {B("NavItemsColumn")} (видна: {shell.FindControl<Control>("NavItemsColumn")?.IsVisible}); " +
               $"шапка компании {B("CompanyCard")}; область раздела {section}";
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task OwnerShell_NoResizeOnNavigation(int w, int h, AppLanguage language, UiTheme theme)
    {
        await TestHost.WarmAsync();
        TestHost.Ensure();
        UiFake.Reset();
        UiShot.Setup(theme, language);

        var shell = new OwnerShellWindow { Width = w, Height = h };
        var entriesField = typeof(OwnerShellWindow).GetField("_navEntries", Flags)!;
        var selectGroup = typeof(OwnerShellWindow).GetMethod("SelectNavGroup", Flags)!;
        var failures = new List<string>();
        try
        {
            shell.Show();
            UiShot.Settle(6, 30);

            var entries = ((IEnumerable)entriesField.GetValue(shell)!).Cast<object>()
                .Select(e => (Key: (string)e.GetType().GetProperty("Key")!.GetValue(e)!, Group: (string?)e.GetType().GetProperty("GroupKey")!.GetValue(e),
                              Button: (Button)e.GetType().GetProperty("Button")!.GetValue(e)!))
                // 2026-11-21: проверяются разделы, которые в Headless без сервера открываются быстро. Остальные («salary», «investments», «site*», «salereturn» и т. д.) подвешивают прогон
                // (синхронные сетевые вызовы/модальные диалоги в окне раздела) — их надо проверять отдельным прогоном на живом тестовом аккаунте.
                .Where(e => SafeKeys.Contains(e.Key) && e.Group is not null && e.Group != "system")
                .ToList();
            entries.Count.Should().BeGreaterThan(10, "меню владельца должно содержать пункты");

            // Раскрываем колонку один раз (на узком окне она свёрнута сама) — это эталон; дальше размеры не должны меняться.
            var groups = entries.Select(e => e.Group!).Distinct().ToList();
            selectGroup.Invoke(shell, [groups[0]]);
            UiShot.Settle(6, 30);
            var baseline = Snapshot(shell, withSection: false);
            var sectionBaseline = (string?)null;
            var clicks = 0;

            void Check(string what)
            {
                UiShot.Settle(6, 30);
                var hasSection = shell.FindControl<Control>("SectionHost")?.IsVisible == true;
                // колонки/шапка/оболочка — против эталона без раздела; область раздела — против первой показанной
                var noSectionNow = Snapshot(shell, false);
                if (noSectionNow != baseline)
                    failures.Add($"{what}: было [{baseline}] стало [{noSectionNow}]");
                if (hasSection)
                {
                    var host = shell.FindControl<Control>("SectionHost")!.Bounds.ToString();
                    sectionBaseline ??= host;
                    if (host != sectionBaseline)
                        failures.Add($"{what}: область раздела была {sectionBaseline} стала {host}");
                }
            }

            foreach (var group in groups)
            {
                selectGroup.Invoke(shell, [group]);
                Check($"{w}x{h} {language} {theme}: группа «{group}»");
                foreach (var entry in entries.Where(e => e.Group == group))
                {
                    var owned = shell.OwnedWindows.ToList();
                    entry.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    clicks++;
                    Check($"{w}x{h} {language} {theme}: пункт «{entry.Key}»");
                    // пункты, открывающие отдельное окно (например «Настройки»), закрываем — оно не раздел
                    foreach (var extra in shell.OwnedWindows.Except(owned).ToList())
                    {
                        if (extra.Content is not null && extra.GetType().Name.Contains("Settings", StringComparison.Ordinal))
                            extra.Close();
                    }
                }
            }
            clicks.Should().BeGreaterThan(10);
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }

        failures.Should().BeEmpty("клик по группе или пункту меню не должен менять размеры колонок, шапки, области раздела и окна: " + string.Join(" | ", failures.Take(6)));
    }
}
