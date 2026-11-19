using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Л-10, «Новичок: перенос данных»): мастер загрузки товаров с выбором программы-источника, «Посмотреть на примере» и «Перенос клиентов»
/// на 1024×600, ru и ky: окно не шире экрана, подсказка об источнике показана для файла МойСклад. Снимки: %TEMP%\nurmarket-tests\ui-actual\import-onboarding-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ImportOnboardingUiTests
{
    private static void Shot(Window window, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, file), data.ToArray());
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Wizard_WithMoySkladFile_Fits1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onboarding-moysklad.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Группы;Код;Наименование;Артикул;Внешний код;Единица измерения;Цена: Цена продажи;Закупочная цена;Штрихкод EAN13;Остаток\n"
            + "Бакалея;001;Сахар 1 кг;S-1;x1;шт;85;70;4600000000059;50\nБакалея;002;Соль 1 кг;S-2;x2;шт;25;15;4600000000066;30\n");
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            await window.LoadPathAsync(path);
            UiShot.Settle(8, 30);
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            window.Plan.Lines.Should().HaveCount(2);
            Shot(window, $"import-onboarding-wizard-{lang}-1024x600.png");
            window.Bounds.Width.Should().BeLessThanOrEqualTo(1024.5);
            window.Bounds.Height.Should().BeLessThanOrEqualTo(600.5);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task DemoStoreAndClientsWindows_Fit1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        DemoStoreCatalog.SetSource(File.ReadAllText(FindDemoJson()));
        var demo = new DemoStoreWindow();
        var clients = new ClientsImportWindow();
        try
        {
            foreach (var (w, name) in new (Window, string)[] { (demo, "demo"), (clients, "clients") })
            {
                using var _ = UiShot.Render(w, new UiSize(1024, 600));
                UiShot.Settle(5, 30);
                Shot(w, $"import-onboarding-{name}-{lang}-1024x600.png");
                w.Bounds.Width.Should().BeLessThanOrEqualTo(1024.5);
                w.Bounds.Height.Should().BeLessThanOrEqualTo(600.5);
            }
        }
        finally
        {
            demo.Close();
            clients.Close();
            UiShot.Pump();
        }
    }

    private static string FindDemoJson()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "NurMarketKassa.Avalonia", "Assets", "demo", "demo-store.json");
            if (File.Exists(p))
                return p;
        }
        throw new FileNotFoundException("demo-store.json");
    }
}
