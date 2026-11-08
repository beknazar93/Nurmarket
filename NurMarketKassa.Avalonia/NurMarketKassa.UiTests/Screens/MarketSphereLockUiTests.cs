using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08, владелец: «Вид магазина меняется только через техподдержку» — Настройки → Операции → «Сфера магазина» в Avalonia.Headless
/// на 1024×600 и 1366×768 (ru, ky): у работающей кассы кнопки серые и неактивные, выбранная (текущий вид) — серая заливка, надпись и «Написать в поддержку»
/// видны и не обрезаны; первый выбор на новой установке открыт; сеанс удалённой помощи открывает переключение на лету.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\sphere-lock-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class MarketSphereLockUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private sealed class PrefsScope : IDisposable
    {
        private readonly string _sphere = UserPreferences.Instance.MarketSphere;
        private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
        private readonly bool _pending = UserPreferences.Instance.MarketSphereChoicePending;

        public PrefsScope(bool pending, string sphere, string? serverSphere)
        {
            var p = UserPreferences.Instance;
            p.MarketSphereChoicePending = pending;
            p.MarketSphere = sphere;
            p.MarketSphereDetail = null;
            MarketSphereLock.SupportSessionActive = false;
            MarketSphereLock.ApplyFromCompanyDoc(new JsonObject()); // разрешения поддержки нет
            SetServerSphere(serverSphere);
        }

        public void Dispose()
        {
            var p = UserPreferences.Instance;
            p.MarketSphereChoicePending = _pending;
            p.MarketSphere = _sphere;
            p.MarketSphereDetail = _detail;
            MarketSphereLock.SupportSessionActive = false;
            SetServerSphere(null);
        }
    }

    private static void SetServerSphere(string? sphere)
    {
        using var doc = JsonDocument.Parse(sphere is null ? "{}" : "{\"market_sphere\":\"" + sphere + "\"}");
        MarketSphereSync.RememberFromCompanyJson("co-ui", doc.RootElement);
    }

    private static (Window Window, OperationsSettingsView View) Open()
    {
        var view = new OperationsSettingsView();
        view.LoadBankQrSettings();
        return (new Window { Content = view }, view);
    }

    private static RadioButton[] Radios(OperationsSettingsView v) =>
        new[] { "SphereGroceryRadio", "SphereClothingRadio", "SphereServicesRadio" }.Select(n => v.FindControl<RadioButton>(n)!).ToArray();

    private static Color BrushColor(Window w, string key) =>
        w.TryFindResource(key, w.ActualThemeVariant, out var r) && r is ISolidColorBrush b ? b.Color : default;

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Existing_till_shows_grey_buttons_current_type_and_support_text(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        using var scope = new PrefsScope(pending: false, MarketSpheres.Clothing, serverSphere: "clothing");
        var (window, view) = Open();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var tag = $"{width}x{height} {lang}";

            var radios = Radios(view);
            radios.Should().OnlyContain(r => !r.IsEnabled, $"под замком кнопки неактивны ({tag})");
            radios.Single(r => r.IsChecked == true).Name.Should().Be("SphereClothingRadio", "выбранной показан текущий вид");
            var checkedRadio = radios.Single(r => r.IsChecked == true);
            (checkedRadio.Background as ISolidColorBrush)?.Color.Should().Be(BrushColor(window, "BrushTextSoft"), "текущий вид — серая заливка, а не тёмная");
            (radios.First(r => r.IsChecked != true).Foreground as ISolidColorBrush)?.Color.Should().Be(BrushColor(window, "BrushTextSoft"), "невыбранные — серый текст");

            var lockText = view.FindControl<TextBlock>("SphereLockText")!;
            lockText.IsEffectivelyVisible.Should().BeTrue();
            lockText.Text.Should().Be(OperationsSettingsView.LockedText);
            view.FindControl<TextBlock>("SphereCurrentText")!.Text.Should().Contain(MarketSpheres.Profile.Title.Get());
            view.FindControl<Button>("SphereSupportButton")!.IsEffectivelyVisible.Should().BeTrue("рядом — «Написать в поддержку»");
            view.FindControl<SelectableTextBlock>("SphereSupportPhone")!.Text.Should().Contain(MarketSphereLock.SupportPhone);

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "sphere-lock", langCode).Where(v => v.Detail.Contains("#Sphere")).ToList();
            clipped.Should().BeEmpty($"текст карточки не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"sphere-lock-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task New_install_allows_first_choice_and_support_session_unlocks_live()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        using var scope = new PrefsScope(pending: true, MarketSpheres.Grocery, serverSphere: null);
        var (window, view) = Open();
        try
        {
            using (UiShot.Render(window, UiSize.Small)) { }
            Radios(view).Should().OnlyContain(r => r.IsEnabled, "вид ещё не выбран — первый выбор открыт");
            view.FindControl<TextBlock>("SphereLockText")!.Text.Should().Contain("один раз");
            view.FindControl<WrapPanel>("SphereSupportRow")!.IsVisible.Should().BeFalse();

            // Выбор сделан (как после клика) — замок.
            MarketSphereLock.NoteLocalChoice();
            UiShot.Settle(3);
            Radios(view).Should().OnlyContain(r => !r.IsEnabled, "после первого выбора — только через техподдержку");
            view.FindControl<TextBlock>("SphereLockText")!.Text.Should().Be(OperationsSettingsView.LockedText);

            // Оператор поддержки подключился — кнопки оживают без перезапуска; отключился — снова серые.
            MarketSphereLock.SupportSessionActive = true;
            UiShot.Settle(3);
            Radios(view).Should().OnlyContain(r => r.IsEnabled);
            MarketSphereLock.SupportSessionActive = false;
            UiShot.Settle(3);
            Radios(view).Should().OnlyContain(r => !r.IsEnabled);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
