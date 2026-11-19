using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13: окно «Весы» после добавления автоотправки, «Проверки перед отправкой» и «Как подключить / памятка» — на 1024×600 и 1366×768 (ru, ky): все три элемента
/// видны, надписи не обрезаны, окно проверки показывает замечания и «Исправить»; тихая отправка (скрытое окно) без связи с весами возвращает «повтор позже», не открывает
/// диалогов и возвращает прежний выбор весов. На весы НИЧЕГО не отправляется: адрес весов — 127.0.0.1:9 (порт закрыт), настоящие весы владельца тесты не трогают.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\scales-auto-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ScalesAutoSendUiTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _savedProducts;
    private readonly string _brand = UserPreferences.Instance.ScaleBrand;
    private readonly string? _tmIp = UserPreferences.Instance.TmScaleIp;
    private readonly int _tmPort = UserPreferences.Instance.TmScalePort;
    private readonly LabelScaleProfile _profile;
    private readonly (bool Auto, DateTime? At, int Count, string? Error) _savedAuto;

    public ScalesAutoSendUiTests()
    {
        TestHost.Ensure();
        var prefs = UserPreferences.Instance;
        prefs.ScaleBrand = "tm";
        prefs.TmScaleIp = "127.0.0.1";
        prefs.TmScalePort = 9; // закрыт: «нет подключения» сразу, настоящие весы не затрагиваются
        _savedProducts = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("a", "Яблоки красные", "120", 1));
        CatalogCacheService.Products.Add(Tile("b", "Груши Нан ү ө", "0", 2));   // цена 0 и кыргызские буквы — замечания проверки
        CatalogCacheService.Products.Add(Tile("c", "Сыр российский", "650", 3));
        _profile = LabelScaleStore.Active;
        _savedAuto = (_profile.AutoSend, _profile.AutoSendAt, _profile.AutoSendCount, _profile.AutoSendError);
        _profile.Brand = "tm";
        _profile.AutoSend = true;
        _profile.AutoSendAt = new DateTime(2026, 11, 13, 14, 32, 0);
        _profile.AutoSendCount = 3;
        _profile.AutoSendError = null;
        _profile.PluNumbers["a"] = 1; // номера закреплены (была ручная отправка) — подпись показывает «последняя автоотправка», а не «включится после первой отправки»
    }

    public void Dispose()
    {
        var prefs = UserPreferences.Instance;
        prefs.ScaleBrand = _brand;
        prefs.TmScaleIp = _tmIp;
        prefs.TmScalePort = _tmPort;
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_savedProducts);
        (_profile.AutoSend, _profile.AutoSendAt, _profile.AutoSendCount, _profile.AutoSendError) = _savedAuto;
        _profile.PluNumbers.Remove("a");
    }

    private static CatalogProductTileVm Tile(string id, string title, string price, int plu)
    {
        var tile = new CatalogProductTileVm(id, title, price + " сом", true) { Unit = "кг", Plu = plu, MustWeigh = true };
        ProductUnitNormalizer.ApplyToTile(tile);
        return tile;
    }

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "scales-auto-" + name + ".png"), data.ToArray());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task ScalesWindow_ShowsAutoSend_Preflight_AndGuideButtons(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var window = new ScalesPluWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(10);
            Save(window, "window-" + slug);

            var text = UiDrive.AllText(window);
            var ru = lang == AppLanguage.Russian;
            text.Should().Contain(ru ? "Отправлять изменения на весы автоматически" : "Өзгөрүүлөрдү таразага автоматтык жөнөтүү");
            text.Should().Contain(ru ? "Проверка перед отправкой" : "Жөнөтүүдөн мурун текшерүү");
            text.Should().Contain(ru ? "Как подключить / памятка" : "Кантип туташтыруу / эскертме");
            text.Should().Contain(ru ? "последняя автоотправка: 13.11 14:32, товаров: 3" : "акыркы автожөнөтүү: 13.11 14:32, товарлар: 3");

            var check = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "AutoSendCheck");
            check.IsEnabled.Should().BeTrue("TM-30F — прямая отправка");
            check.IsChecked.Should().BeTrue();
            window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PreflightButton").IsEffectivelyVisible.Should().BeTrue();
            window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SendButton").IsEffectivelyVisible.Should().BeTrue("главная кнопка не вытеснена");

            // клик по галочке выключает автоотправку у этих весов и запоминает
            check.IsChecked = false;
            check.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            LabelScaleStore.Active.AutoSend.Should().BeFalse();
            check.IsChecked = true;
            check.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            LabelScaleStore.Active.AutoSend.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task PreflightWindow_ListsIssues_WithFixButtons(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var issues = new List<ScalePreflightIssue>
        {
            new(ScalePreflightKind.PriceZero, ScalePreflightSeverity.Error, "b", "Груши", ""),
            new(ScalePreflightKind.NoPlu, ScalePreflightSeverity.Error, "d", "Сыр российский", ""),
            new(ScalePreflightKind.AmountOverCeiling, ScalePreflightSeverity.Warning, "e", "Буква лакым", "999,99"),
            new(ScalePreflightKind.NameChars, ScalePreflightSeverity.Warning, "f", "Нан ү ө", ""),
        };
        var fixedKinds = new List<ScalePreflightKind>();
        var window = new ScalesPreflightWindow(() => issues.ToList(), i => { fixedKinds.Add(i.Kind); issues.Remove(i); });
        try
        {
            ModalShell.WorkingAreaForTests = new Avalonia.Size(width, height);
            using var _ = UiShot.Render(window);
            Save(window, "preflight-" + slug);
            var ru = lang == AppLanguage.Russian;
            var text = UiDrive.AllText(window);
            text.Should().Contain(ru ? "ошибок 2, предупреждений 2" : "каталар 2, эскертүүлөр 2");
            text.Should().Contain(ru ? "Цена 0" : "Баасы 0");

            var fixButtons = window.GetVisualDescendants().OfType<Button>().Where(b => (UiDrive.TextOf(b) ?? "") == (ru ? "Исправить" : "Оңдоо")).ToList();
            fixButtons.Should().HaveCount(2, "«Исправить» только у «Нет PLU» и «Сумма выше потолка»: у цены 0 и символов кнопки нет");
            fixButtons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(3);
            fixedKinds.Should().Equal(ScalePreflightKind.NoPlu);
            UiDrive.AllText(window).Should().Contain(ru ? "ошибок 1, предупреждений 2" : "каталар 1, эскертүүлөр 2", "список пересобран после исправления");
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task SilentAutoSend_WithoutConnection_AsksForRetry_OpensNoDialog_AndKeepsSelectedScale()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // закреплённый номер уже есть — вопросов «закрепить номера» нет; у весов есть адрес-заглушка 127.0.0.1:9
        _profile.PluNumbers["a"] = 1;
        var idBefore = LabelScaleStore.Active.Id;

        var result = await ScalesPluWindow.RunAutoSendAsync(_profile, new[] { "a" });

        result.Ok.Should().BeFalse("весов по 127.0.0.1:9 нет");
        result.Retry.Should().BeTrue("без связи — повтор позже, без окон-ошибок кассиру");
        result.Sent.Should().Be(0);
        result.Error.Should().NotBeNullOrWhiteSpace();
        LabelScaleStore.Active.Id.Should().Be(idBefore, "выбор весов возвращён");
    }

    [AvaloniaFact]
    public async Task SilentAutoSend_AllProductsWithErrors_IsNotRetried()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // товар «b»: цена 0 — ошибка проверки, на весы не уходит; повторять нечего (ждём правки каталога)
        var result = await ScalesPluWindow.RunAutoSendAsync(_profile, new[] { "b" });
        result.Ok.Should().BeTrue();
        result.Sent.Should().Be(0);
        result.Retry.Should().BeFalse();
    }
}
