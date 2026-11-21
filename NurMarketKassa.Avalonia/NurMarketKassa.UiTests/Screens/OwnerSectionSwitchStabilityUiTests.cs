using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (жалоба владельца: «в программе владельца при смене вкладки идёт быстрое увеличение и уменьшение окна»). Разделы программы владельца — отдельные окна,
/// лежащие поверх оболочки (OwnerShellWindow.SectionHost). Живой замер: при каждом открытии раздела глобальный обработчик Opened (DialogScreenFit → FitToKassaScreen)
/// ставил его по центру экрана и сжимал до рабочей области (141,8 1636×1016 вместо 284,46 1636×1033), и через 10–600 мс SyncSectionBounds возвращал на место.
/// Тест переключает разделы подряд на 1366×768 и 1024×768 и следит за КАЖДЫМ изменением положения и размера окна раздела и оболочки: ни одно не должно отличаться
/// от итогового больше чем на 1 точку; прежний раздел обязан оставаться на экране, пока новый не показан (иначе область 100–450 мс пустая).</summary>
[Collection("UiScenarios")]
public sealed class OwnerSectionSwitchStabilityUiTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<object[]> Sizes()
    {
        yield return [1366, 768];
        yield return [1024, 768];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task OwnerSections_SwitchInSequence_WindowsNeverJumpOrResize(int w, int h)
    {
        await TestHost.WarmAsync();
        TestHost.Ensure();
        UiFake.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);

        var shell = new OwnerShellWindow { Width = w, Height = h };
        var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Flags, null, [typeof(string), typeof(Func<Window>)], null)!;
        var scaleField = typeof(OwnerShellWindow).GetField("_shellScale", Flags)!;
        // DialogScreenFit — internal: вызываем обработчик Opened через отражение
        var fitOnOpened = typeof(OwnerShellWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit")!.GetMethod("OnWindowOpened", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sections = new (string Key, Func<Window> Create)[]
        {
            ("warehouse", () => TestHost.Services.GetRequiredService<WarehouseWindow>()),
            ("barcodes", () => new BarcodePrintSectionWindow()),
            ("purchases", () => new PurchasesWindow()),
            ("clients", () => TestHost.Services.GetRequiredService<ClientsWindow>()),
            ("abc", () => TestHost.Services.GetRequiredService<AbcAnalysisWindow>()),
            ("marketplace", () => new MarketplaceWindow().AsSection()),
            ("warehouse", () => TestHost.Services.GetRequiredService<WarehouseWindow>()), // повторный показ уже созданного раздела
        };
        var failures = new List<string>();
        try
        {
            shell.Show();
            UiShot.Settle(4, 30);
            var shellSize = new Size(shell.Bounds.Width, shell.Bounds.Height);
            var shellPos = shell.Position;
            Window? previous = null;
            var windows = new Dictionary<string, Window>();
            foreach (var (key, create) in sections)
            {
                Window? window = windows.GetValueOrDefault(key);
                var log = new List<(string What, PixelPoint Pos, double W, double H)>();
                var previousVisibleAtOpen = (bool?)null;
                var prev = previous;
                void Watch(Window win)
                {
                    win.PositionChanged += (_, e) => log.Add(("pos", e.Point, win.Bounds.Width, win.Bounds.Height));
                    win.Resized += (_, e) => log.Add(("size", win.Position, e.ClientSize.Width, e.ClientSize.Height));
                    // как в работе программы: DialogScreenFit подключён ко всем окнам (App.RegisterForAllWindows), здесь — только к проверяемому разделу
                    win.Opened += (_, _) => { previousVisibleAtOpen = prev?.IsVisible; fitOnOpened.Invoke(null, [win]); };
                }
                if (window != null)
                    Watch(window);
                open.Invoke(shell, [key, (Func<Window>)(() =>
                {
                    window = create();
                    Watch(window);
                    return window;
                })]);
                UiShot.Settle(8, 30);
                window.Should().NotBeNull();
                windows[key] = window!;

                // итоговое место раздела — по области SectionHost оболочки
                var host = shell.FindControl<Control>("SectionHost")!;
                var scale = (double)scaleField.GetValue(shell)!;
                var expectedPos = host.PointToScreen(new Point(0, 0));
                var expectedW = host.Bounds.Width * scale;
                var expectedH = host.Bounds.Height * scale;

                window!.Position.Should().Be(expectedPos, $"{key} {w}x{h}: итоговое положение раздела");
                window.Width.Should().BeApproximately(expectedW, 1.0, $"{key} {w}x{h}: ширина");
                window.Height.Should().BeApproximately(expectedH, 1.0, $"{key} {w}x{h}: высота");
                double.IsInfinity(window.MaxHeight).Should().BeTrue($"{key} {w}x{h}: «подгонка под экран» не должна ограничивать встроенный раздел (MaxHeight={window.MaxHeight})");
                double.IsInfinity(window.MaxWidth).Should().BeTrue($"{key} {w}x{h}: MaxWidth={window.MaxWidth}");

                foreach (var e in log)
                {
                    if (e.What == "pos" && (Math.Abs(e.Pos.X - expectedPos.X) > 1 || Math.Abs(e.Pos.Y - expectedPos.Y) > 1))
                        failures.Add($"{key} {w}x{h}: окно раздела было на {e.Pos} (итог {expectedPos})");
                    if (e.What == "size" && (Math.Abs(e.W - expectedW) > 1 || Math.Abs(e.H - expectedH) > 1))
                        failures.Add($"{key} {w}x{h}: размер окна раздела был {e.W:0.#}×{e.H:0.#} (итог {expectedW:0.#}×{expectedH:0.#})");
                }
                if (previousVisibleAtOpen == false)
                    failures.Add($"{key} {w}x{h}: прежний раздел спрятан ДО показа нового — область раздела пуста на время создания");
                if (Math.Abs(shell.Bounds.Width - shellSize.Width) > 1 || Math.Abs(shell.Bounds.Height - shellSize.Height) > 1)
                    failures.Add($"{key} {w}x{h}: оболочка изменила размер {shellSize} -> {shell.Bounds.Size}");
                if (shell.Position != shellPos)
                    failures.Add($"{key} {w}x{h}: оболочка сдвинулась {shellPos} -> {shell.Position}");
                previous = window;
            }
        }
        finally
        {
            shell.Close();
        }

        failures.Should().BeEmpty("окна не должны прыгать и менять размер при смене раздела: " + string.Join(" | ", failures.Take(8)));
    }
}
