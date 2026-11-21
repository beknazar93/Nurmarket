using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 («Печать чека! Редактор нужен! И наименование и цена не на одном уровне!»): «Редактор чека» (Настройки → Печать → Чек) в Avalonia.Headless — 1024×600 и 1366×768,
/// ru / ky / uz: окно не выше и не шире экрана, текст не обрезан; предпросмотр на 58 и 80 мм; блок включается и переставляется — предпросмотр сразу меняется; «Сбросить на стандарт»; «Тест-печать»
/// идёт в подмену (настоящий принтер не трогаем); «Сохранить» кладёт шаблон в настройки. Названия блоков и подписи — на 5 языках. Реквизиты чека — одно место (страница «Печать → Чек»).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\receipt-editor-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceiptEditorUiTests : IDisposable
{
    private readonly string? _oldTemplate = UserPreferences.Instance.ReceiptTemplateJson;
    private readonly int _oldPaper = UserPreferences.Instance.ReceiptPaperWidthMm;

    public ReceiptEditorUiTests() => TestHost.Ensure();

    public void Dispose()
    {
        ReceiptEditorWindow.TestPrintOverride = null;
        UserPreferences.Instance.ReceiptTemplateJson = _oldTemplate;
        UserPreferences.Instance.ReceiptPaperWidthMm = _oldPaper;
        ModalShell.WorkingAreaForTests = null;
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static string PaperText(Window w) =>
        string.Join("\n", w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.FontFamily.Name.Contains("Consolas") || t.FontFamily.ToString().Contains("Consolas")).Select(t => t.Text));

    private static Border PaperHost(Window w) =>
        w.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ReceiptPaper");

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Editor_Fits_TextNotClipped_PreviewAndEdit(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.ReceiptTemplateJson = null;
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var tag = $"{width}x{height} {lang}";
        var code = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };
        var w = new ReceiptEditorWindow();
        try
        {
            using (var _ = UiShot.Render(w, new UiSize(width, height))) { }
            UiShot.Settle(4);
            w.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
            w.Bounds.Width.Should().BeLessThanOrEqualTo(width, $"окно не шире экрана ({tag})");
            TextClipChecker.Find(w, $"receipt-editor-{code}-{width}x{height}", code).Should().BeEmpty($"текст не обрезан ({tag})");
            SaveShot(w, $"receipt-editor-{code}-{width}x{height}");

            // Предпросмотр: образец чека с названием товара и итогом, строка «кол-во x цена … сумма» — одной строкой.
            var text = PaperText(w);
            text.Should().Contain("КАПСУЛА").And.Contain("ИТОГО");
            var itemLine = text.Split('\n').First(l => l.StartsWith("2 X 18.00", StringComparison.Ordinal));
            itemLine.TrimEnd().Should().EndWith("36.00", "количество × цена и сумма — на одном уровне");

            // Лента 80 мм шире ленты 58 мм.
            var w58 = PaperHost(w).Width;
            var radio80 = w.GetVisualDescendants().OfType<RadioButton>().First(r => r.GroupName == "receiptPaper" && r.IsChecked != true);
            radio80.IsChecked = true;
            UiShot.Settle(3);
            PaperHost(w).Width.Should().BeGreaterThan(w58, "80 мм шире 58 мм");

            // Выключили «Товары» — предпросмотр без позиций; «Сбросить на стандарт» вернул.
            var draft = w.DraftForTests;
            draft.Block(ReceiptTemplate.Items).On = false;
            w.SelectForTests(ReceiptTemplate.Footer); // перестроить список и предпросмотр
            PaperText(w).Should().NotContain("КАПСУЛА");
            draft.Block(ReceiptTemplate.Items).On = true;
            w.SelectForTests(ReceiptTemplate.Items);
            PaperText(w).Should().Contain("КАПСУЛА");
        }
        finally
        {
            w.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_Move_Save_TestPrint_Reset()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.ReceiptTemplateJson = null;
        var printed = new List<string>();
        ReceiptEditorWindow.TestPrintOverride = text => { printed.Add(text); return Task.FromResult<string?>(null); };
        var w = new ReceiptEditorWindow();
        try
        {
            using (var _ = UiShot.Render(w, new UiSize(1366, 768))) { }
            UiShot.Settle(3);
            var firstBefore = w.DraftForTests.Blocks.Select(b => b.Id).ToList();

            // ▼ у первого блока: порядок меняется.
            var down = w.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == Tr.T("Ниже", "Төмөн", "Move down", "Aşağı", "Pastga") && b.IsEnabled);
            UiDrive.Click(w, down);
            UiShot.Settle(3);
            w.DraftForTests.Blocks.Select(b => b.Id).Should().NotEqual(firstBefore);
            w.DraftForTests.Blocks[1].Id.Should().Be(firstBefore[0], "первый блок опустился на вторую позицию");

            // Тест-печать: образец уходит в подмену, не на принтер.
            UiDrive.Click(w, UiDrive.ButtonByText(w, "Тест-печать"));
            UiRobot.WaitFor(() => printed.Count == 1).Should().BeTrue();
            printed[0].Should().Contain("Капсула".ToUpperInvariant());

            // Сохранить: шаблон лежит в настройках, повторное открытие читает его же.
            UiDrive.Click(w, UiDrive.ButtonByText(w, "Сохранить"));
            UiShot.Settle(3);
            w.Saved.Should().BeTrue();
            UserPreferences.Instance.ReceiptTemplateJson.Should().NotBeNullOrEmpty();
            ReceiptTemplateEngine.Load().Blocks[1].Id.Should().Be(firstBefore[0]);

            // Сбросить на стандарт → сохранить → ключ пуст (стандартный чек, как раньше).
            var w2 = new ReceiptEditorWindow();
            using (var _ = UiShot.Render(w2, new UiSize(1366, 768))) { }
            UiDrive.Click(w2, UiDrive.ButtonByText(w2, "Сбросить на стандарт"));
            w2.DraftForTests.IsDefault().Should().BeTrue();
            UiDrive.Click(w2, UiDrive.ButtonByText(w2, "Сохранить"));
            UserPreferences.Instance.ReceiptTemplateJson.Should().BeNull();
        }
        finally
        {
            w.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void BlockNames_AreOnFiveLanguages()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian); // 2026-11-21: язык от прежнего теста мог остаться узбекским — эталон «ru» брался на нём
        var ru = ReceiptTemplate.DefaultOrder.ToDictionary(id => id, ReceiptEditorWindow.BlockName);
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            foreach (var id in ReceiptTemplate.DefaultOrder)
            {
                var name = ReceiptEditorWindow.BlockName(id);
                name.Should().NotBeNullOrWhiteSpace();
                name.Should().NotBe(ru[id], $"название блока «{id}» переведено на {lang}");
            }
        }
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
    }

    // ───────────── реквизиты чека — одно место ─────────────

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NurMarketKassa.Avalonia", "NurMarketKassa.Avalonia.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень решения");
    }

    [Fact]
    public void ReceiptRequisites_LiveOnlyOnPrintPage_OldPlacesHaveLink()
    {
        var views = Path.Combine(Repo(), "NurMarketKassa.Avalonia", "Views", "Settings");
        foreach (var field in new[] { "StoreNameBox", "StoreAddressBox", "StoreInnBox", "ShowInnCheck", "DebtPenaltyBox", "RentalPenaltyBox", "ShowStoreNameCheck", "ShowAddressCheck", "ShowReceiptNumberCheck", "ShowDateCheck", "ShowItemsCheck", "ShowTotalCheck", "ShowQrCodeCheck" })
        {
            var owners = Directory.GetFiles(views, "*.axaml").Where(f => File.ReadAllText(f).Contains($"x:Name=\"{field}\"", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
            owners.Should().Equal(new[] { "PrintSettingsView.axaml" }, $"«{field}» — в одном месте (Печать → Чек), без дублей");
        }
        foreach (var old in new[] { "ScreenSettingsView.axaml", "OperationsSettingsView.axaml" })
            File.ReadAllText(Path.Combine(views, old)).Should().Contain("ReceiptMovedButton", $"в {old} — ссылка «Настроить в «Чек»»");
    }
}
