using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 («в один товар и доп. штрихкоды»: «Асу» и «Асу со вкусом клубники» — один товар): окно ИИ-советника при загрузке накладной — панель «Команда ИИ» (переключатель, своя команда, «Сбросить»)
/// и блок вариантов в карточке накладной (группа с вложенными вариантами, итог «товаров: N, вариантов/доп. кодов: M», «Разъединить», «Объединить с …», «Объединить? цены разные», правка названия).
/// Размеры 1024×600 и 1366×768, языки ru, ky, uz: ничего не обрезано и не налезает, кнопки в окне. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\ai-variants-*.png.</summary>
[Collection("UiScenarios")]
public sealed class InvoiceVariantsUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ai-variants-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public InvoiceVariantsUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        InvoiceVariantPrefs.DataDirOverride = _dir; // настройка не пишется в настоящие данные
        InvoiceVariantPrefs.ResetCache();
    }

    public void Dispose()
    {
        InvoiceVariantPrefs.DataDirOverride = null;
        InvoiceVariantPrefs.ResetCache();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static ProductActionPlan.Step Create(string invoiceName, string baseName, string variant, string brand, string barcode, double qty, double purchase) =>
        new("create", new CatalogProductTileVm("", invoiceName, "0.00", false), qty, null, null, purchase, 110, barcode, "шт", null,
            Base: baseName, Variant: variant, Brand: brand, InvoiceName: invoiceName);

    /// <summary>Накладная: «Асу» + «клубничный» + «лимонный» (одна цена) — группа; «мятный» по другой цене — спор «цены разные»; «Чай» — обычная строка.</summary>
    private static InvoiceVariantPlan SamplePlan(bool existing = false)
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу", "Асу", "", "Асу", "4870000000011", 5, 90),
            Create("Асу со вкусом клубники", "Асу", "клубничный", "Асу", "4870000000028", 10, 90),
            Create("Асу лимон длинное название строки накладной для проверки переноса", "Асу", "лимонный", "Асу", "4870000000035", 7, 90),
            Create("Асу мятный", "Асу", "мятный", "Асу", "4870000000042", 3, 95),
            Create("Чай чёрный", "", "", "", "4870000000059", 2, 40),
        };
        var catalog = existing ? new[] { new CatalogProductTileVm("p1", "Асу", "120.00", false) { Brand = "Асу", Barcode = "4870000000011" } } : [];
        return InvoiceVariantPlan.Build(steps, new InvoiceVariantPlan.Options
        {
            Merge = true,
            FindExisting = (b, br) => InvoiceVariantPlan.FindInCatalog(catalog, b, br),
            OwnerOfCode = _ => null,
        });
    }

    /// <summary>Для общего теста «поля не налезают» (FormFieldsOverlapUiTests): блок вариантов в окне нужной ширины.</summary>
    public static Window CreateCardForLayout()
    {
        var panel = new InvoiceVariantsPanel(SamplePlan(), () => { });
        return new Window { Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { panel } } } };
    }

    /// <summary>Для общего теста: окно ИИ-советника с приложенной накладной, панель «Команда ИИ» раскрыта.</summary>
    public static AiAdvisorWindow CreateAdvisorForLayout()
    {
        InvoiceVariantPrefs.DataDirOverride ??= Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ai-variants-layout");
        InvoiceVariantPrefs.ResetCache();
        InvoiceVariantPrefs.SetCommand("бренд — Асу; цену не менять"); // непустая команда раскрывает поле сама
        var window = new AiAdvisorWindow();
        Attach(window);
        return window;
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static void Attach(AiAdvisorWindow window) =>
        typeof(AiAdvisorWindow).GetMethod("SetAttachment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, [TinyPng, "image/png", "invoice.png"]);

    private static void ShowCard(AiAdvisorWindow window, List<ProductActionPlan.Step> steps) =>
        typeof(AiAdvisorWindow).GetMethod("AddProductActionsCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [steps, "накладная", null, null, null]);

    /// <summary>Кнопка по ТОЧНОМУ названию (UiDrive.ButtonByText ищет по вхождению: «Объединить» нашло бы «Объединить с …»).</summary>
    private static Button Exact(Window window, params string[] names) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is { } t && names.Contains(t))
        ?? throw new InvalidOperationException("Кнопка не найдена: " + string.Join("/", names));

    private static string Code(AppLanguage lang) => lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru";

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void AssertInside(Window window, string tag)
    {
        foreach (var btn in window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.Bounds.Height > 0))
        {
            var p = btn.TranslatePoint(default, window)!.Value;
            (p.X + btn.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» не выходит по ширине ({tag})");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Variants_block_shows_group_summary_and_fits(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = CreateCardForLayout();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";
            var text = UiDrive.AllText(window);
            text.Should().Contain("Асу");
            text.Should().Contain(lang switch { AppLanguage.Kyrgyz => "товарлар: 3", AppLanguage.Uzbek => "mahsulotlar: 3", _ => "товаров: 3" }, $"Асу-группа + «мятный» отдельно + «Чай» — итог в блоке ({tag})");
            AssertInside(window, tag);
            var clipped = TextClipChecker.Find(window, "ai-variants", Code(lang));
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            Save(window, $"ai-variants-card-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Split_all_calls_back_and_changes_the_plan_and_rename_is_kept()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var plan = SamplePlan();
        var changed = 0;
        var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { new InvoiceVariantsPanel(plan, () => changed++) } } } };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(6);
            // правка названия варианта сохраняется при потере фокуса и не пересобирает карточку
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "клубничный");
            box.Text = "Клубника";
            box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement.LostFocusEvent));
            plan.Entries.First(e => e.IsGroup).Members.Select(m => m.Variant).Should().Contain("Клубника");
            changed.Should().Be(0);

            Exact(window, "Разъединить").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            changed.Should().Be(1);
            plan.Entries.Should().NotContain(e => e.IsGroup);
            plan.ToSteps().Should().HaveCount(5);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Merge_button_for_price_conflict_joins_the_line_into_the_group()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var plan = SamplePlan();
        var changed = 0;
        var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { new InvoiceVariantsPanel(plan, () => changed++) } } } };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(6);
            UiDrive.AllText(window).Should().Contain("цены разные");
            Exact(window, "Объединить").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            changed.Should().Be(1);
            plan.Entries.Single(e => e.IsGroup).Members.Should().HaveCount(4);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Advisor_shows_command_bar_with_attachment_and_it_fits(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new AiAdvisorWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var tag = $"{width}x{height} {lang}";
            window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Should().BeEmpty($"без накладной панель «Команда ИИ» скрыта ({tag})");

            Attach(window);
            UiShot.Settle(6);
            var visible = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.Classes.Contains("ToggleSwitch")).ToList();
            visible.Should().ContainSingle($"переключатель «Объединять вкусы/варианты» ({tag})");
            visible[0].IsChecked.Should().BeTrue("по умолчанию ВКЛ");
            AssertInside(window, tag);

            // выключить и вернуть кнопкой «Сбросить»
            visible[0].IsChecked = false;
            InvoiceVariantPrefs.Merge.Should().BeFalse();
            UiDrive.ButtonByText(window, "Сбросить", "Баштапкыга", "Tozalash").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            InvoiceVariantPrefs.Merge.Should().BeTrue();
            visible[0].IsChecked.Should().BeTrue();

            // своя команда раскрывается и запоминается
            UiDrive.ButtonByText(window, "Команда ИИ", "ЖИ буйругу", "SI buyrug'i").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4);
            var field = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.MaxLength == InvoiceVariantPrefs.MaxCommandLength);
            field.Text = "бренд — Асу";
            field.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement.LostFocusEvent));
            InvoiceVariantPrefs.Command.Should().Be("бренд — Асу");
            var clipped = TextClipChecker.Find(window, "ai-variants-bar", Code(lang));
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            Save(window, $"ai-variants-bar-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Invoice_card_in_advisor_groups_variants_and_rebuilds_in_place(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new AiAdvisorWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var tag = $"{width}x{height} {lang}";
            var steps = new List<ProductActionPlan.Step>
            {
                Create("Асу", "Асу", "", "Асу", "4870000000011", 5, 90),
                Create("Асу со вкусом клубники", "Асу", "клубничный", "Асу", "4870000000028", 10, 90),
                Create("Асу лимон", "Асу", "лимонный", "Асу", "4870000000035", 7, 90),
                Create("Чай чёрный", "", "", "", "4870000000059", 2, 40),
            };
            ShowCard(window, steps);
            UiShot.Settle(8);
            var text = UiDrive.AllText(window);
            text.Should().Contain(lang switch { AppLanguage.Kyrgyz => "товарлар: 2, варианттар/кошумча коддор: 2", AppLanguage.Uzbek => "mahsulotlar: 2, variantlar/qo'shimcha kodlar: 2", _ => "товаров: 2, вариантов/доп. кодов: 2" }, $"итог ({tag})");
            AssertInside(window, tag);
            Save(window, $"ai-variants-invoice-{Code(lang)}-{width}x{height}");

            // «Разъединить» пересобирает карточку на том же месте (одна карточка, без блока вариантов)
            var panelBefore = window.GetVisualDescendants().OfType<InvoiceVariantsPanel>().Count();
            panelBefore.Should().Be(1);
            // введённый владельцем код у «Чай» переживает пересборку карточки и не «переезжает» к другой позиции
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Text == "4870000000059").Text = "4870000000777";
            var split = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is "Разъединить" or "Ажыратуу" or "Ajratish");
            split.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8);
            string[] splitNames = ["Разъединить", "Ажыратуу", "Ajratish"];
            string[] againNames = ["Объединить снова", "Кайра бириктирүү", "Yana birlashtirish"];
            string[] postNames = ["Провести закупку", "Сатып алууну өткөрүү", "Xaridni o'tkazish"];
            int Count(string[] names) => window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is { } t && names.Contains(t));
            Count(splitNames).Should().Be(0, $"после «Разъединить» групп нет ({tag})");
            window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Text == "4870000000777").Should().HaveCount(1, $"код «Чай» сохранён ({tag})");
            window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Text == "4870000000059").Should().BeEmpty($"старый код «Чай» никому не достался ({tag})");
            Count(againNames).Should().Be(1, $"остаётся «Объединить снова» ({tag})");
            Count(postNames).Should().Be(1, $"карточка одна ({tag})");
            Save(window, $"ai-variants-invoice-split-{Code(lang)}-{width}x{height}");
            Exact(window, againNames).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8);
            Count(splitNames).Should().Be(1, $"группа вернулась ({tag})");
            Count(postNames).Should().Be(1, $"карточка по-прежнему одна ({tag})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
