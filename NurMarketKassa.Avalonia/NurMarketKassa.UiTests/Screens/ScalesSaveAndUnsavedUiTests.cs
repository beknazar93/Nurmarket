using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (фото владельца, раздел «Весы» программы владельца): при выходе вопрос «В разделах есть несохранённые правки: Весы» был ложным — «признак» ставило
/// пробное поле «Настроить по этикетке» (13 цифр этикетки), а всё остальное на странице применяется сразу. Проверяем: ввод в этом поле — не правка; настоящее поле с «Сохранить»
/// по-прежнему правка, а «Сохранить» её снимает и показывает результат рядом с кнопкой (раньше «Сохранено» было за прокруткой или скрыто — кнопка казалась неработающей);
/// фраза «Внимание: на сайте стоит…» на странице не повторяется, раскладка — в свёрнутом «Подробнее»; текст не обрезан на 1024×600 и 1366×768 (ru/ky/uz).</summary>
[Collection("UiScenarios")]
public sealed class ScalesSaveAndUnsavedUiTests : IDisposable
{
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedOwnerSections = AppMode.OwnerSectionsInKassa;
    private readonly string? _savedLocalUnit = UserPreferences.Instance.ScaleBarcodeAmountUnitLocal;
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;

    public ScalesSaveAndUnsavedUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.SetLocalScaleAmountUnit(_savedLocalUnit);
        AppMode.OwnerSectionsInKassa = _savedOwnerSections;
        IsOwnerProp.SetValue(null, _savedIsOwner);
    }

    private static async Task LoginAsync(string role)
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"" + role + "\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"" + role + "\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    private static PosSettingsWindow OpenScales(bool owner)
    {
        IsOwnerProp.SetValue(null, owner);
        AppMode.OwnerSectionsInKassa = !owner;
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        window.Show();
        UiShot.Settle(4, 40);
        window.SelectNavTab("scales").Should().BeTrue();
        UiShot.Settle(4, 40);
        return window;
    }

    private static void Close(PosSettingsWindow window)
    {
        typeof(PosSettingsWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close();
        UiShot.Pump();
    }

    /// <summary>Как оболочка владельца: «несохранённое» разделов считает UnsavedInputTracker по окну раздела.</summary>
    [AvaloniaFact]
    public async Task Owner_TrialLabelInput_IsNotUnsaved_ButRealFieldIs_AndSaveClearsAndReports()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync("owner");
        var window = OpenScales(owner: true);
        try
        {
            UnsavedInputTracker.Attach(window);
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            view.ShowSection(2);
            UiShot.Settle(4, 40);

            var label = view.FindControl<ScaleBarcodeSetupPanel>("BarcodePanel")!.FindControl<TextBox>("LabelCodeBox")!;
            UiDrive.Type(window, label, "2000001018408");
            label.Text.Should().Be("2000001018408");
            UnsavedInputTracker.IsDirty(window).Should().BeFalse("пробный ввод этикетки в «Настроить по этикетке» — не несохранённая правка (настройки применяются сразу)");

            // у владельца видна «Сохранить», и она подтверждает сохранение рядом с кнопкой
            var bar = window.FindControl<Border>("PinnedSaveBar")!;
            var save = window.FindControl<Button>("PinnedSaveButton")!;
            bar.IsVisible.Should().BeTrue("в разделе «Весы» программы владельца есть строка «Сохранить»");
            save.IsEffectivelyVisible.Should().BeTrue();
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(2, 30);
            var note = window.FindControl<TextBlock>("AutoApplyNote")!;
            note.IsEffectivelyVisible.Should().BeTrue("после «Сохранить» рядом с кнопкой написано, что сохранено");
            note.Text.Should().Contain("Сохранено");
            UnsavedInputTracker.IsDirty(window).Should().BeFalse();

            // обычное поле формы вне панели правил (как адрес весов, который сохраняет «Сохранить») — несохранённая правка; после «Сохранить» — снова чисто
            var field = new TextBox();
            ((Panel)view.FindControl<Control>("BarcodeSection")!).Children.Add(field);
            UiShot.Settle(3, 30);
            UiDrive.Type(window, field, "7");
            UnsavedInputTracker.IsDirty(window).Should().BeTrue("обычное поле формы по-прежнему считается несохранённым");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(2, 30);
            UnsavedInputTracker.IsDirty(window).Should().BeFalse("«Сохранить» снимает признак");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>Касса: «Сохранить» в Настройки → Весы не молчит — результат виден в закреплённой строке, а не только в карточке внизу страницы.</summary>
    [AvaloniaFact]
    public async Task Kassa_PinnedSave_ShowsResultNextToButton()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync("cashier");
        var window = OpenScales(owner: false);
        try
        {
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            view.ShowSection(2);
            UiShot.Settle(3, 30);
            var save = window.FindControl<Button>("PinnedSaveButton")!;
            save.IsEffectivelyVisible.Should().BeTrue();
            save.IsEnabled.Should().BeTrue();
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(3, 30);
            view.FindControl<TextBlock>("SaveStatusText")!.Text.Should().NotBeNullOrEmpty("обработчик «Сохранить» страницы вызван");
            var note = window.FindControl<TextBlock>("AutoApplyNote")!;
            note.IsEffectivelyVisible.Should().BeTrue("результат виден рядом с кнопкой");
            note.Text.Should().Be(view.FindControl<TextBlock>("SaveStatusText")!.Text);
        }
        finally
        {
            Close(window);
        }
    }

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    /// <summary>Единица на кассе отличается от сайта — предупреждение «Внимание…» одно (не дважды), раскладка свёрнута в «Подробнее», текст не обрезан.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task OwnerBarcodeTab_WarningOnce_DetailsCollapsed_NoClippedText(int w, int h, AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync("owner");
        var server = WeightBarcodeParser.ResolveAmountUnit(null, UserPreferences.Instance.ScaleBarcodeAmountUnit);
        UserPreferences.Instance.SetLocalScaleAmountUnit(string.Equals(server, WeightBarcodeParser.UnitSom, StringComparison.OrdinalIgnoreCase) ? WeightBarcodeParser.UnitTiyin : WeightBarcodeParser.UnitSom);
        var window = OpenScales(owner: true);
        try
        {
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            view.ShowSection(2);
            using (UiShot.Render(window, new UiSize(w, h))) { }
            UiShot.Settle(4, 40);
            var panel = view.FindControl<ScaleBarcodeSetupPanel>("BarcodePanel")!;
            var details = panel.FindControl<Expander>("CompanyDetails")!;
            details.IsExpanded.Should().BeFalse("раскладка компании свёрнута в «Подробнее»");

            var warnWord = new[] { "Внимание", "Көңүл буруңуз", "Diqqat" };
            var texts = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            texts.Count(t => warnWord.Any(t.Contains)).Should().BeLessThanOrEqualTo(1, "предупреждение про единицу на сайте — один раз, не дважды подряд");

            var lang = language switch { AppLanguage.Russian => "ru", AppLanguage.Kyrgyz => "ky", _ => "uz" };
            TextClipChecker.Find(window, "scales-barcode-collapsed", lang).Should().BeEmpty($"текст не обрезан ({lang} {w}x{h})");
            details.IsExpanded = true;
            using (UiShot.Render(window, new UiSize(w, h))) { }
            UiShot.Settle(3, 30);
            TextClipChecker.Find(window, "scales-barcode-expanded", lang).Should().BeEmpty($"раскрытое «Подробнее» не обрезано ({lang} {w}x{h})");
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                var x = button.TranslatePoint(default, window)!.Value.X;
                x.Should().BeGreaterThanOrEqualTo(-0.5, $"кнопка «{UiDrive.TextOf(button)}» не уходит за левый край");
                (x + button.Bounds.Width).Should().BeLessThanOrEqualTo(w + 0.5, $"кнопка «{UiDrive.TextOf(button)}» не уходит за правый край");
            }
        }
        finally
        {
            Close(window);
        }
    }
}
