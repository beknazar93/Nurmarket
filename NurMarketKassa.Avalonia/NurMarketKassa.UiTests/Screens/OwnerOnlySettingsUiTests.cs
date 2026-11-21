using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

/// <summary>2026-11-21 (владелец: «Убрать ИИ и Сотрудники из настроек КАССЫ! Телеграм-бот тоже, что касается владельца, удалить!»): настоящее окно «Настройки» в Avalonia.Headless.
/// Касса (AppMode.IsOwner = false): нет вкладок «Сотрудники» и «ИИ» (кнопок нет в дереве, ряд вкладок без «дырок»), нет карточки «Телеграм-бот владельца», ключей ИИ и Нур Советника;
/// «Операции» остаются — QR-оплата на месте; ссылка на скрытую вкладку не падает (SelectNavTab → false + пояснение «Это — в программе владельца»).
/// Автономный режим (OwnerSectionsInKassa): вкладка «Сотрудники» остаётся — программа владельца там не работает, а локальные коды доступа задаются только на ней.
/// Программа владельца: всё на месте. Снимки: %TEMP%\nurmarket-tests\ui-actual\ownersettings-*.png (1024×600 и 1366×768, ru/ky/uz) — смотреть глазами.</summary>
[Collection("UiScenarios")]
public sealed class OwnerOnlySettingsUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedOwnerSections = AppMode.OwnerSectionsInKassa;

    public OwnerOnlySettingsUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _savedIsOwner);
        AppMode.OwnerSectionsInKassa = _savedOwnerSections;
    }

    public static IEnumerable<object[]> SizesAndThreeLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Lang(AppLanguage language) => language switch { AppLanguage.Russian => "ru", AppLanguage.Kyrgyz => "ky", _ => "uz" };

    private static async Task LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
    }

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static Button? Nav(Window w, string name) => w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name);

    [AvaloniaFact]
    public void Gate_KassaClosed_OwnerOpen_AutonomousKeepsEmployees()
    {
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = false;
        OwnerOnlySettings.Allowed.Should().BeFalse();
        OwnerOnlySettings.EmployeesAllowed.Should().BeFalse();
        AppMode.OwnerSectionsInKassa = true;
        OwnerOnlySettings.Allowed.Should().BeFalse("ИИ и Телеграм-бот владельца в автономной кассе тоже убраны");
        OwnerOnlySettings.EmployeesAllowed.Should().BeTrue("в автономном режиме локальные коды сотрудников задаются только здесь");
        IsOwnerProp.SetValue(null, true);
        OwnerOnlySettings.Allowed.Should().BeTrue();
        OwnerOnlySettings.EmployeesAllowed.Should().BeTrue();
        OwnerOnlySettings.KassaNote.Should().NotBeNullOrWhiteSpace();
    }

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndThreeLanguages))]
    public async Task Kassa_NoEmployeesNoAiNoTelegram_OperationsStay(int w, int h, AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = false;

        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = w;
        settings.Height = h;
        try
        {
            settings.Show();
            UiShot.Settle(6);

            // вкладок «Сотрудники» и «ИИ» нет вообще, «Операции» и «Клавиши» — есть
            Nav(settings, "NavEmployees").Should().BeNull("в кассе вкладки «Сотрудники» нет в дереве");
            Nav(settings, "NavAi").Should().BeNull("в кассе вкладки «ИИ» нет в дереве");
            Nav(settings, "NavOperations").Should().NotBeNull();
            Nav(settings, "NavOperations")!.IsVisible.Should().BeTrue();
            var tabs = settings.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("SettingsTab") && b.IsVisible).ToList();
            tabs.Should().NotBeEmpty();
            tabs.Should().OnlyContain(b => b.Bounds.Width > 20 && b.Bounds.Height > 20, "панель вкладок не «дырявая»: у каждой видимой вкладки есть размер");

            // ссылки на скрытые вкладки не падают и ничего не открывают
            settings.SelectNavTab("employees").Should().BeFalse();
            settings.SelectNavTab("ai").Should().BeFalse();
            UiShot.Settle(2);
            UiDrive.AllText(settings).Should().Contain(OwnerOnlySettings.KassaNote);

            // «Операции» на месте; QR-оплата есть, Телеграм-бота владельца, ключей ИИ и Нур Советника нет
            settings.SelectNavTab("operations").Should().BeTrue();
            UiShot.Settle(6);
            var borders = settings.GetVisualDescendants().OfType<Border>().ToList();
            borders.Should().NotContain(b => b.Name == "TelegramCard", "карточки «Телеграм-бот владельца» в кассе нет");
            borders.Should().NotContain(b => b.Name == "AiKeysCard");
            borders.Should().NotContain(b => b.Name == "AiMovedHintCard");
            settings.GetVisualDescendants().OfType<AdvisorSettingsCard>().Should().BeEmpty();
            settings.GetVisualDescendants().OfType<CheckBox>().Should().Contain(c => c.Name == "DynamicQrCheck", "QR-оплата в «Операциях» остаётся");
            var banned = new[] { "Телеграм-бот владельца", "Подключить бота", "Ключи ИИ", "Нур Советник включён",
                "Ээсинин телеграм-боту", "Ботту туташтыруу", "ИИ ачкычтары", "Egasi uchun Telegram-bot", "Botni ulash", "AI kalitlari" };
            UiDrive.AllText(settings).Should().NotContainAny(banned);
            settings.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PageSubtitleText").Text.Should().NotContain("Telegram", "подзаголовок «Операций» в кассе не про Телеграм");
            SaveShot(settings, $"ownersettings-kassa-operations-{Lang(language)}-{w}x{h}", new UiSize(w, h));

            // клавиши кассира остались
            settings.SelectNavTab("keys").Should().BeTrue();
        }
        finally
        {
            settings.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Autonomous_KeepsEmployeesTab_ButNoAi()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync();
        IsOwnerProp.SetValue(null, false);
        AppMode.OwnerSectionsInKassa = true;
        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = 1366;
        settings.Height = 768;
        try
        {
            settings.Show();
            UiShot.Settle(6);
            Nav(settings, "NavEmployees").Should().NotBeNull();
            Nav(settings, "NavAi").Should().BeNull();
            settings.SelectNavTab("ai").Should().BeFalse();
        }
        finally
        {
            settings.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndThreeLanguages))]
    public async Task Owner_HasEmployeesAiAndTelegram(int w, int h, AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        IsOwnerProp.SetValue(null, true);
        AppMode.OwnerSectionsInKassa = false;

        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = w;
        settings.Height = h;
        try
        {
            settings.Show();
            UiShot.Settle(6);
            Nav(settings, "NavEmployees").Should().NotBeNull();
            Nav(settings, "NavAi").Should().NotBeNull();
            settings.SelectNavTab("employees").Should().BeTrue();
            UiShot.Settle(4);
            settings.SelectNavTab("ai").Should().BeTrue();
            UiShot.Settle(6);
            settings.GetVisualDescendants().OfType<Border>().Should().Contain(b => b.Name == "AiKeysCard");
            settings.GetVisualDescendants().OfType<AdvisorSettingsCard>().Should().ContainSingle();
            settings.SelectNavTab("operations").Should().BeTrue();
            UiShot.Settle(6);
            settings.GetVisualDescendants().OfType<Border>().Should().Contain(b => b.Name == "TelegramCard", "у владельца карточка Телеграм-бота есть (скрыта лишь тарифом)");
            SaveShot(settings, $"ownersettings-owner-operations-{Lang(language)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            settings.Close();
            UiShot.Pump();
        }
    }
}
