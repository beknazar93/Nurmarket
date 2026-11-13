using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (решение владельца, аудит «Склад/Приёмка», пакет 3: Л-11…Л-13, Л-25, Д-20, Т-8, Т-9): в программе владельца страница «Товары → Весы»
/// показывает ТОЛЬКО две вкладки — «Весы с этикетками» и «Штрих-код: вес / сумма». «Весы на кассе» (COM) и «Табло цены» остались в кассе: COM-порты держит касса,
/// а настройки, сохранённые у владельца, на кассу не влияли. Проверки: набор вкладок владельца и кассы; у владельца нет ни «Сохранить», ни подсказки про COM, ни
/// заполненных полей порта/табло (их заполнял только прежний ScaleOwnerHardware, которого больше нет); в кассе подсказка «куда переехали весы» не обрезана
/// (Д-20), «Сохранить» стоит в подвале карточки; раскладка раздела владельца на 1024×600 и 1366×768 (ru/ky): текст не обрезан, вкладки и кнопки внутри окна по ширине.
/// Снимки: %TEMP%\nurmarket-tests\ui-actual\scales-owner-full-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ScalesOwnerFullSetUiTests : IDisposable
{
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedOwnerSections = AppMode.OwnerSectionsInKassa;
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;

    public ScalesOwnerFullSetUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        AppMode.OwnerSectionsInKassa = _savedOwnerSections;
        IsOwnerProp.SetValue(null, _savedIsOwner);
    }

    private static async Task LoginOwnerAsync()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    private static void WaitFor(Func<bool> condition, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    private static bool TabVisible(ScaleSettingsView view, string name) => view.FindControl<RadioButton>(name)!.IsVisible;

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    /// <summary>Страница весов в окне раздела программы владельца (как открывает пункт «Товары → Весы»).</summary>
    private static (ScalesSectionWindow Window, ScaleSettingsView View) OpenOwnerSection()
    {
        IsOwnerProp.SetValue(null, true);
        var window = new ScalesSectionWindow();
        window.Show();
        UiShot.Settle(4, 40);
        return (window, window.GetVisualDescendants().OfType<ScaleSettingsView>().Single());
    }

    // ------------------------------------------------------------------ набор вкладок владельца

    [AvaloniaFact]
    public async Task Owner_ShowsOnlyTwoTabs_NoComScalesNoDisplay_NoSaveNoHardwareHint()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var (window, view) = OpenOwnerSection();
        try
        {
            TabVisible(view, "TabLabelButton").Should().BeTrue("«Весы с этикетками» — главное в разделе владельца");
            TabVisible(view, "TabBarcodeButton").Should().BeTrue("«Штрих-код: вес / сумма» — в программе владельца");
            TabVisible(view, "TabWeighingButton").Should().BeFalse("«Весы на кассе» (COM) — только в кассе (решение владельца 12.11)");
            TabVisible(view, "TabDisplayButton").Should().BeFalse("«Табло цены» — только в кассе");
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue("раздел открывается на «Весах с этикетками»");

            // 1 и 2 — единственные вкладки; 0 и 3 (COM-весы и табло) «через внутренний переход» не открываются — уводят на «Весы с этикетками»
            foreach (var section in new[] { 0, 3, 1, 2 })
            {
                view.ShowSection(section);
                UiShot.Settle(3, 30);
                view.FindControl<Control>("WeighingSection")!.IsVisible.Should().BeFalse($"вкладка {section}: COM-весов у владельца нет");
                view.FindControl<Control>("DisplaySection")!.IsVisible.Should().BeFalse($"вкладка {section}: табло у владельца нет");
                view.FindControl<Border>("SaveBlock")!.IsVisible.Should().BeFalse($"вкладка {section}: у владельца нечего «сохранять для кассы»");
                view.CheckScaleButton.IsEffectivelyVisible.Should().BeFalse("«Проверить весы» (открытие COM-порта) у владельца недоступно");
                view.TestPoleDisplayButton.IsEffectivelyVisible.Should().BeFalse("«Тест табло» у владельца недоступен");
                view.FindPoleDisplayButton.IsEffectivelyVisible.Should().BeFalse("«Найти табло» (перебор COM-портов) у владельца недоступен");
            }
            view.ShowSection(0);
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue("вкладка 0 у владельца ведёт на «Весы с этикетками»");
            view.ShowSection(2);
            view.FindControl<Control>("BarcodeSection")!.IsVisible.Should().BeTrue();
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Т-8: вместо «IsAvailable службы весов» (без порта всегда false) — проверка по существу: у владельца нет кода, открывающего COM-порт или табло,
    /// и страница не заполняла поля порта/табло (раньше их заполнял ScaleOwnerHardware).</summary>
    [AvaloniaFact]
    public async Task Owner_PageNeverFillsComFields_AndHasNoHardwareClass()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        typeof(ScaleSettingsView).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Settings.ScaleOwnerHardware")
            .Should().BeNull("класс, который открывал COM-порты и писал настройки весов в программу владельца, удалён");
        var (window, view) = OpenOwnerSection();
        try
        {
            foreach (var section in new[] { 1, 2, 1 })
            {
                view.ShowSection(section);
                UiShot.Settle(3, 30);
            }
            view.ScaleComCombo.ItemsSource.Should().BeNull("список COM-портов у владельца не заполняется — порты не перечисляются и не открываются");
            view.PoleDisplayComCombo.ItemsSource.Should().BeNull("список портов табло у владельца не заполняется");
            view.ScaleEnabledCheck.IsChecked.Should().NotBe(true, "включённых COM-весов у владельца нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ касса: полный набор (решение владельца 12.11), Д-20

    [AvaloniaFact]
    public async Task Kassa_KeepsAllFourTabs_SaveInCardFooter()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = false;
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            // 2026-11-12, решение владельца («оставь их»): в кассе все четыре вкладки, как в 1.17.85
            TabVisible(view, "TabLabelButton").Should().BeTrue("«Весы с этикетками» — и в кассе");
            TabVisible(view, "TabBarcodeButton").Should().BeTrue("«Штрих-код: вес / сумма» — и в кассе");
            TabVisible(view, "TabWeighingButton").Should().BeTrue("COM-весы — оборудование кассы");
            TabVisible(view, "TabDisplayButton").Should().BeTrue("табло цены — оборудование кассы");
            view.FindControl<Border>("SaveBlock")!.IsVisible.Should().BeTrue("в кассе «Сохранить» на месте");
            view.SaveButton.CornerRadius.TopLeft.Should().BeGreaterThan(0, "Д-20: у «Сохранить» скруглённые углы");
            view.SaveButton.GetVisualAncestors().Should().Contain(view.FindControl<Border>("SaveBlock")!, "Д-20: кнопка в подвале карточки");
            view.SaveButton.HorizontalAlignment.Should().Be(Avalonia.Layout.HorizontalAlignment.Right, "Д-20: не по центру");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-12: в кассе на узком окне (800 px) текст на вкладках «Весы с этикетками» и «Штрих-код» не обрезан.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Kassa_LabelAndBarcodeTabs_NoClippedText_AtNarrowWidth(AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = false;
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            using (UiShot.Render(window, new UiSize(800, 600))) { } // 2026-11-12: сначала показать окно, иначе страницы весов нет в визуальном дереве
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            foreach (var section in new[] { 1, 2 })
            {
                view.ShowSection(section);
                using var _ = UiShot.Render(window, new UiSize(800, 600));
                TextClipChecker.Find(window, $"scales-kassa-tab{section}", language.ToString()).Should().BeEmpty($"текст вкладки {section} в кассе не обрезан");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ раскладка раздела владельца (Т-9)

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task OwnerScales_TwoTabs_Layout_NoClippedText_ButtonsInsideWindow(int w, int h, AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await LoginOwnerAsync();
        IsOwnerProp.SetValue(null, true);
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            IsOwnerProp.SetValue(null, true);
            NavButtons(shell).Should().ContainKey("scales");
            NavButtons(shell)["scales"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<ScalesSectionWindow>().Any(), 3000);
            var section = shell.OwnedWindows.OfType<ScalesSectionWindow>().FirstOrDefault();
            section.Should().NotBeNull();
            UiShot.Settle(12, 60);
            var view = section!.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            var lang = language == AppLanguage.Russian ? "ru" : "ky";
            var names = new[] { "label", "barcode" };
            var sections = new[] { 1, 2 };
            for (var i = 0; i < sections.Length; i++)
            {
                view.ShowSection(sections[i]);
                UiShot.Settle(6, 40);
                var size = new UiSize(w, h);
                SaveShot(section, $"scales-owner-full-{names[i]}-{lang}-{w}x{h}", size);

                var tag = $"{names[i]} {lang} {w}x{h}";
                section.Bounds.Width.Should().BeApproximately(w, 1, $"размер окна задан снимком ({tag})");
                TextClipChecker.Find(section, $"scales-owner-{names[i]}", lang).Should().BeEmpty($"текст на вкладке не обрезан ({tag})");

                // вкладки и кнопки — внутри окна по ширине (по высоте страница прокручивается)
                var tabs = view.FindControl<Control>("SectionTabsGrid")!;
                foreach (var tab in new[] { "TabLabelButton", "TabBarcodeButton" })
                {
                    var btn = view.FindControl<RadioButton>(tab)!;
                    var left = btn.TranslatePoint(default, section)!.Value.X;
                    left.Should().BeGreaterThanOrEqualTo(-0.5, $"вкладка {tab} не уходит за левый край ({tag})");
                    (left + btn.Bounds.Width).Should().BeLessThanOrEqualTo(w + 0.5, $"вкладка {tab} не уходит за правый край ({tag})");
                }
                tabs.Bounds.Width.Should().BeLessThanOrEqualTo(w, $"ряд вкладок не шире окна ({tag})");
                foreach (var button in view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
                {
                    var x = button.TranslatePoint(default, section)!.Value.X;
                    x.Should().BeGreaterThanOrEqualTo(-0.5, $"кнопка «{UiDrive.TextOf(button)}» не уходит за левый край ({tag})");
                    (x + button.Bounds.Width).Should().BeLessThanOrEqualTo(w + 0.5, $"кнопка «{UiDrive.TextOf(button)}» не уходит за правый край ({tag})");
                }
            }
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }
}
