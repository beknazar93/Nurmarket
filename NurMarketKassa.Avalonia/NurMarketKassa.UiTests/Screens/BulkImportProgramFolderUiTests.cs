using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 («Автоподтяжка базы из папки программы»): панель «Указать папку программы…» на шаге «Файл» мастера «Массовая загрузка».
/// 1024×600 и 1366×768, ru/ky/uz: плитка облачной программы (МойСклад) — пояснение «данные в облаке», плитка Paloma365 — подсказка «Обычно здесь: …» и кнопка «Открыть папку»
/// только если папка есть; чтение синтетической базы SQLite из временной папки — остаёмся на шаге «Файл», карточка файла и число товаров; поля и подписи не налезают,
/// текст не обрезан. Снимки: %TEMP%\nurmarket-tests\ui-actual\bulk-folder-*.png (смотреть глазами). Сервер не нужен.</summary>
[Collection("UiScenarios")]
public sealed class BulkImportProgramFolderUiTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pf-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public BulkImportProgramFolderUiTests() => TestHost.Ensure();

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* временная папка теста */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static T Get<T>(BulkImportWizardWindow w, string name) => (T)typeof(BulkImportWizardWindow).GetField(name, Flags)!.GetValue(w)!;

    private static void Choose(BulkImportWizardWindow w, int comboIndex) => typeof(BulkImportWizardWindow).GetMethod("ChooseSource", Flags)!.Invoke(w, [comboIndex]);

    private static void Shot(Window w, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        using var bmp = UiShot.Render(w);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, file), data.ToArray());
    }

    private static void Clean(BulkImportWizardWindow window, string scope, string size, AppLanguage lang)
    {
        FormFieldsOverlapUiTests.FindViolations(window, scope).Select(v => v.Key + " — " + v.Detail).ToList()
            .Should().BeEmpty($"поля и подписи не налезают друг на друга ({scope} {size} {lang})");
        TextClipChecker.Find(window, scope, Code(lang)).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"текст не обрезан ({scope} {size} {lang})");
        window.Bounds.Height.Should().BeLessThanOrEqualTo(double.Parse(size.Split('x')[1]) + 0.5, $"окно не выше экрана ({scope} {size} {lang})");
        window.Bounds.Width.Should().BeLessThanOrEqualTo(double.Parse(size.Split('x')[0]) + 0.5, $"окно не шире экрана ({scope} {size} {lang})");
    }

    private string MakeFolder()
    {
        var dir = Path.Combine(_root, "Paloma365");
        Directory.CreateDirectory(dir);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(dir, "shop.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE goods(id INTEGER PRIMARY KEY, name TEXT, barcode TEXT, price REAL, cost REAL, qty REAL);"
                + "INSERT INTO goods VALUES (1,'Хлеб белый','4600000000011',35,25,40),(2,'Рис 1 кг','4600000000028',105,80,60),(3,'Соль','4600000000035',20,12,10);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return dir;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Folder_panel_fits_does_not_overlap_and_reads_a_synthetic_database(int width, int height, AppLanguage lang)
    {
        if (OperatingSystem.IsAndroid())
            return; // на Android панель — только пояснение
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var size = $"{width}x{height}";
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8, 30);

            // облачная программа: пояснение «данные в облаке», подсказки «Обычно здесь» нет
            Choose(window, 2); // МойСклад
            UiShot.Settle(8, 30);
            var panel = Get<Border>(window, "_folderPanel");
            panel.IsVisible.Should().BeTrue("панель «папка программы» на шаге «Файл»");
            Get<TextBlock>(window, "_folderNote").Text.Should().Contain(Tr.T("облак", "булут", "cloud", "bulut", "bulut"), "облачная программа: честное пояснение");
            Get<StackPanel>(window, "_folderUsualRow").IsVisible.Should().BeFalse("у облачной программы нет стандартных путей");
            Shot(window, $"bulk-folder-cloud-{Code(lang)}-{size}.png");
            Clean(window, "bulk-folder-cloud", size, lang);

            // локальная программа: «Обычно здесь: …», обе кнопки и поле пути видны и не налезают
            Choose(window, 5); // Paloma365
            UiShot.Settle(8, 30);
            Get<TextBlock>(window, "_folderUsual").Text.Should().Contain("Paloma365");
            var pick = Get<Button>(window, "_folderPick");
            var find = Get<Button>(window, "_folderFind");
            pick.IsVisible.Should().BeTrue();
            find.IsVisible.Should().BeTrue();
            (pick.Bounds.Width > 100 && find.Bounds.Width > 100).Should().BeTrue("кнопки не схлопнуты");
            Shot(window, $"bulk-folder-local-{Code(lang)}-{size}.png");
            Clean(window, "bulk-folder-local", size, lang);

            // чтение синтетической базы: остаёмся на шаге «Файл», карточка файла, число товаров в сообщении
            var folder = MakeFolder();
            await window.ReadFolderAsync(folder);
            UiShot.Settle(8, 30);
            Get<int>(window, "_step").Should().Be(2, "после чтения папки остаёмся на шаге «Файл»");
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            window.Plan.Rows.Count.Should().Be(3, "три товара из базы");
            window.Plan.ErrorCount.Should().Be(0, "столбцы подобраны, ошибок строк нет: " + string.Join(" | ", window.Plan.Rows.Select(r => r.Error)));
            Get<Border>(window, "_fileCard").IsVisible.Should().BeTrue("карточка выбранного файла");
            var status = Get<TextBlock>(window, "_folderStatus");
            status.IsVisible.Should().BeTrue();
            status.Text.Should().Contain("3").And.Contain("Paloma365");
            Get<Button>(window, "_nextBtn").IsEnabled.Should().BeTrue("«Далее» доступно после чтения папки");
            Shot(window, $"bulk-folder-read-{Code(lang)}-{size}.png");
            Clean(window, "bulk-folder-read", size, lang);

            // пустая папка: понятная ошибка, мастер не ломается
            var empty = Path.Combine(_root, "empty");
            Directory.CreateDirectory(empty);
            await window.ReadFolderAsync(empty);
            UiShot.Settle(8, 30);
            Get<TextBlock>(window, "_folderStatus").Text.Should().NotBeNullOrWhiteSpace();
            Shot(window, $"bulk-folder-error-{Code(lang)}-{size}.png");
            Clean(window, "bulk-folder-error", size, lang);
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            window.Close();
            UiShot.Pump();
        }
    }
}
