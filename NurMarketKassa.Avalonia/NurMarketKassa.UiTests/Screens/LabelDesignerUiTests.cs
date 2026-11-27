using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 («Редактор ценников»): режимы «Печать / Редактор / Шаблоны» единой вкладки «Ценники и штрихкоды» в настоящем «Складе»:
/// редактор открывается, холст и свойства не налезают друг на друга и не выходят за окно (1024×600 и 1366×768, ru/ky/uz), элемент добавляется, двигается мышью,
/// меняет размер за угол, отменяется, шаблон сохраняется и попадает в «Мои шаблоны» и в печать, PDF строится, без покупки — подсказка. Снимки — в
/// %TEMP%\nurmarket-tests\ui-actual\labeldesigner-*.png (смотреть глазами).</summary>
[Collection("UiScenarios")]
public sealed class LabelDesignerUiTests : IDisposable
{
    private readonly bool _savedUnlocked = UserPreferences.Instance.LabelEditorUnlocked;
    private readonly bool _savedBulk = UserPreferences.Instance.BulkPriceTagUnlocked;
    private readonly LabelTemplate _savedTemplate = LabelTemplateStore.Load();
    private readonly string? _savedDesigns = UserPreferences.Instance.LabelDesignsJson;
    private readonly Func<Window?, string, string?> _savedPrompt = PaidFeatureGate.SerialPrompt;

    public LabelDesignerUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        LabelDesignLibrary.ResetForTests();
        UserPreferences.Instance.LabelDesignsJson = null;
        LabelDesignLibrary.Shared.Clear();
    }

    public void Dispose()
    {
        UserPreferences.Instance.LabelEditorUnlocked = _savedUnlocked;
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedBulk;
        PaidFeatureGate.SerialPrompt = _savedPrompt;
        LabelTemplateStore.Save(_savedTemplate);
        LabelDesignLibrary.Shared.Clear();
        UserPreferences.Instance.LabelDesignsJson = _savedDesigns;
        LabelDesignLibrary.ResetForTests();
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Lang(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static (WarehouseWindow Window, LabelsHubView Hub) OpenHub(int w, int h, AppLanguage lang, LabelsHubMode mode)
    {
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.LabelEditorUnlocked = true;
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        using var _ = UiShot.Render(window, new UiSize(w, h));
        UiShot.Settle(6);
        window.SelectNavTab("barcodes").Should().BeTrue();
        UiShot.Settle(6);
        var hub = window.LabelsHub!;
        hub.ShowMode(mode);
        UiShot.Settle(10);
        return (window, hub);
    }

    private static Rect Bounds(Control c, Visual root) => new(c.TranslatePoint(default, root) ?? default, c.Bounds.Size);

    // ------------------------------------------------------------------ раскладка

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Editor_FitsTheWindow_NothingOverlaps_AndShowsTheLabel(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, hub) = OpenHub(w, h, lang, LabelsHubMode.Editor);
        try
        {
            var designer = hub.Designer;
            designer.Should().NotBeNull("платный редактор открыт — вместо подсказки сам редактор");
            designer!.IsEffectivelyVisible.Should().BeTrue();
            designer.CanvasRoot.Bounds.Width.Should().BeGreaterThan(100);
            designer.CanvasRoot.Bounds.Height.Should().BeGreaterThan(60);
            designer.BoxesCanvas.Children.OfType<Control>().Count().Should().BeGreaterThanOrEqualTo(3, "название, цена и штрихкод — рамками на холсте");

            // Кнопки и поля верхней и нижней панелей — внутри окна, ненулевые, не налезают друг на друга.
            var windowRect = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
            var visible = designer.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0).ToList();
            visible.Count.Should().BeGreaterThan(6);
            foreach (var b in visible)
            {
                var r = Bounds(b, window);
                r.Right.Should().BeLessThanOrEqualTo(windowRect.Right + 1, $"кнопка «{UiDrive.TextOf(b)}» не выходит за окно ({w}x{h} {lang})");
            }
            var rows = designer.GetVisualDescendants().OfType<WrapPanel>().Where(p => p.IsEffectivelyVisible && p.Children.Count > 3).ToList();
            rows.Should().NotBeEmpty();
            foreach (var row in rows)
            {
                var rects = row.Children.OfType<Control>().Where(c => c.IsVisible && c.Bounds.Width > 0).Select(c => Bounds(c, window)).ToList();
                for (var i = 0; i < rects.Count; i++)
                    for (var j = i + 1; j < rects.Count; j++)
                    {
                        var inter = rects[i].Intersect(rects[j]);
                        (inter.Width > 1 && inter.Height > 1).Should().BeFalse($"элементы строки не налезают друг на друга ({w}x{h} {lang}): {rects[i]} и {rects[j]}");
                    }
            }
            // Холст целиком виден в своей области (масштаб подобран под окно), справа свойства.
            var canvasRect = Bounds(designer.CanvasRoot, window);
            canvasRect.Right.Should().BeLessThanOrEqualTo(windowRect.Right);
            canvasRect.Bottom.Should().BeLessThanOrEqualTo(windowRect.Bottom);
            SaveShot(window, $"labeldesigner-editor-{Lang(lang)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Modes_Print_Editor_Templates_SwitchInsideOneTab(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, hub) = OpenHub(w, h, lang, LabelsHubMode.Print);
        try
        {
            hub.Mode.Should().Be(LabelsHubMode.Print);
            var texts = hub.GetVisualDescendants().OfType<ToggleButton>().Select(b => UiDrive.TextOf(b)).ToList();
            texts.Should().Contain(new[] { LabelsHubView.ModeText(LabelsHubMode.Print), LabelsHubView.ModeText(LabelsHubMode.Editor), LabelsHubView.ModeText(LabelsHubMode.Templates) });
            // Нажатие по кнопкам режимов — настоящее.
            var editorButton = hub.GetVisualDescendants().OfType<ToggleButton>().First(b => UiDrive.TextOf(b) == LabelsHubView.ModeText(LabelsHubMode.Editor));
            UiDrive.Click(window, editorButton);
            UiShot.Settle(8);
            hub.Mode.Should().Be(LabelsHubMode.Editor);
            hub.Designer.Should().NotBeNull();
            var templatesButton = hub.GetVisualDescendants().OfType<ToggleButton>().First(b => UiDrive.TextOf(b) == LabelsHubView.ModeText(LabelsHubMode.Templates));
            UiDrive.Click(window, templatesButton);
            UiShot.Settle(8);
            hub.Mode.Should().Be(LabelsHubMode.Templates);
            hub.TemplatesView.Should().NotBeNull();
            hub.TemplatesView!.IsEffectivelyVisible.Should().BeTrue();
            hub.TemplatesView.GetVisualDescendants().OfType<Image>().Count(i => i.Source != null).Should().BeGreaterThanOrEqualTo(LabelDesignPresets.All.Count, "карточки готовых шаблонов с предпросмотром");
            SaveShot(window, $"labeldesigner-templates-{Lang(lang)}-{w}x{h}", new UiSize(w, h));
            window.GetVisualDescendants().OfType<TabItem>().Count(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды")).Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ действия

    /// <summary>2026-11-26 (отчёт «Конкуренты», п. 3): «QR магазина» — в меню «Добавить элемент»; после добавления на холсте рамка 14×14 мм, в свойствах подсказка
    /// про витрину (ru/ky/uz), блоки свойств не выходят за окно 1024×600.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Editor_ShopQr_CanBeAdded_AndItsHintIsShown(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, hub) = OpenHub(1024, 600, lang, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            var before = d.WorkingTemplate.Extras.Count;
            d.AddElement(LabelExtraKind.ShopQr);
            UiShot.Settle(6);
            d.WorkingTemplate.Extras.Should().HaveCount(before + 1);
            var shopQr = d.WorkingTemplate.Extras.Last();
            shopQr.Kind.Should().Be(LabelExtraKind.ShopQr);
            shopQr.WidthMm.Should().BeGreaterThanOrEqualTo(7);
            shopQr.HeightMm.Should().BeGreaterThanOrEqualTo(8, "в блок такой высоты влезает подпись «Заказать онлайн»");
            UiDrive.AllText(window).Should().Contain("market.nurcrm.kg", $"{Lang(lang)}: в свойствах подсказка про витрину");
            var windowRect = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
            foreach (var t in d.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Text!.Contains("market.nurcrm.kg")))
                Bounds(t, window).Right.Should().BeLessThanOrEqualTo(windowRect.Right + 1, $"{Lang(lang)}: подсказка не выходит за окно");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Editor_AddElement_DragAndResizeByMouse_Undo_Save_AndTemplate()
    {
        await TestHost.WarmAsync();
        var (window, hub) = OpenHub(1366, 768, AppLanguage.Russian, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            var before = d.WorkingTemplate.Extras.Count;
            d.AddElement(LabelExtraKind.FreeText);
            UiShot.Settle(4);
            d.WorkingTemplate.Extras.Should().HaveCount(before + 1);
            var key = d.SelectedKey;
            key.Should().StartWith("x:");
            var extra = d.WorkingTemplate.Extras.Last();
            extra.Kind.Should().Be(LabelExtraKind.FreeText);

            // Перетаскивание мышью: рамка выбранного элемента (Canvas-обёртка с Tag = ключ) → +30 px вправо, −10 px вверх.
            Rectangle Box() => d.BoxesCanvas.GetVisualDescendants().OfType<Rectangle>().First(r => (r.Tag as string) == key);
            var startX = extra.XMm;
            var startY = extra.YMm;
            var centre = Box().TranslatePoint(new Point(Box().Bounds.Width / 2, Box().Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseMove(new Point(centre.X + 15, centre.Y - 5));
            window.MouseMove(new Point(centre.X + 30, centre.Y - 10));
            window.MouseUp(new Point(centre.X + 30, centre.Y - 10), MouseButton.Left);
            UiShot.Settle(4);
            var dxMm = 30 / d.ScalePxPerMm;
            (extra.XMm - startX).Should().BeApproximately(dxMm, 0.6, "элемент сдвинулся за мышью (с привязкой к сетке 0,5 мм)");
            (startY - extra.YMm).Should().BeGreaterThan(0.3).And.BeLessThan(10 / d.ScalePxPerMm + 0.6);
            extra.XMm.Should().BeGreaterThanOrEqualTo(0);
            (extra.XMm + extra.WidthMm).Should().BeLessThanOrEqualTo(d.WorkingTemplate.WidthMm + 0.01);

            // Изменение размера за угол.
            var w0 = extra.WidthMm;
            var grip = d.BoxesCanvas.GetVisualDescendants().OfType<Border>().First(b => (b.Tag as string) == "grip");
            var gp = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(gp, MouseButton.Left);
            window.MouseMove(new Point(gp.X - 12, gp.Y));
            window.MouseUp(new Point(gp.X - 12, gp.Y), MouseButton.Left);
            UiShot.Settle(4);
            extra.WidthMm.Should().BeLessThan(w0, "потянули угол влево — блок уже");

            // Стрелка двигает на шаг сетки.
            var x1 = extra.XMm;
            d.Focus();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            UiShot.Settle(3);
            extra.XMm.Should().BeApproximately(x1 + 0.5, 0.01);

            // Отмена возвращает прежнее.
            d.Undo();
            d.WorkingTemplate.Extras.First(e => e.Id == extra.Id).XMm.Should().BeApproximately(x1, 0.01);
            d.Undo();
            d.Undo();
            d.Undo();
            d.WorkingTemplate.Extras.Should().HaveCount(before, "после всех отмен свой элемент убран");

            // Сохранение: рабочий шаблон попадает в режим «Печать».
            d.AddElement(LabelExtraKind.Brand);
            d.IsDirty.Should().BeTrue();
            d.Save();
            d.IsDirty.Should().BeFalse();
            LabelTemplateStore.Load().Extras.Should().Contain(e => e.Kind == LabelExtraKind.Brand);

            // PDF и «Мои шаблоны».
            var pdf = d.BuildPdf();
            System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
            LabelDesignLibrary.Upsert("Проверка", d.WorkingTemplate)!.Template.Extras.Should().Contain(e => e.Kind == LabelExtraKind.Brand);
            hub.ShowMode(LabelsHubMode.Templates);
            UiShot.Settle(8);
            UiDrive.AllText(window).Should().Contain("Проверка");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Editor_PresetPromo_ShowsOldPriceAndDiscountOnARealProduct()
    {
        await TestHost.WarmAsync();
        var (window, hub) = OpenHub(1366, 768, AppLanguage.Russian, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            d.LoadTemplate(LabelDesignPresets.ById("promo")!.Build());
            UiShot.Settle(6);
            d.WorkingTemplate.Extras.Select(e => e.Kind).Should().Contain(new[] { LabelExtraKind.OldPrice, LabelExtraKind.Discount });
            d.WorkingTemplate.Frame.Should().BeTrue();
            var tpl = d.PreviewTemplate();
            tpl.Item.Should().NotBeNull();
            tpl.Item!.OldPriceText.Should().NotBeNullOrEmpty("пока товар не выбран — образец данных");
            SaveShot(window, "labeldesigner-promo-ru-1366x768", new UiSize(1366, 768));
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ 2026-11-27: «Ошибка в редактировании штрих кода ценников»

    private static T PrintField<T>(LabelsHubView hub, string name) =>
        (T)typeof(BarcodePrintView).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(hub.PrintHost.View!)!;

    private static ToggleButton ModeButton(LabelsHubView hub, LabelsHubMode mode) =>
        hub.GetVisualDescendants().OfType<ToggleButton>().First(b => UiDrive.TextOf(b) == LabelsHubView.ModeText(mode));

    /// <summary>Правки редактора (тип штрихкода) без «Сохранить*» → кнопка режима «Печать»: печать получает их, а не прежний шаблон.</summary>
    [AvaloniaFact]
    public async Task Editor_UnsavedBarcodeEdit_ReachesPrint_WhenSwitchingToPrintMode()
    {
        await TestHost.WarmAsync();
        var start = LabelTemplate.CreateDefault();
        LabelTemplateStore.Save(start);
        var (window, hub) = OpenHub(1366, 768, AppLanguage.Russian, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            d.Reload();
            d.SelectElement("barcode");
            UiShot.Settle(4);
            var format = d.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<string>().Contains("Code 128"));
            format.SelectedIndex = (int)LabelBarcodeFormat.Code128;
            UiShot.Settle(3);
            d.IsDirty.Should().BeTrue();

            UiDrive.Click(window, ModeButton(hub, LabelsHubMode.Print));
            UiShot.Settle(8);
            hub.Mode.Should().Be(LabelsHubMode.Print);
            LabelTemplateStore.Load().BarcodeFormat.Should().Be(LabelBarcodeFormat.Code128, "правка редактора сохранена при уходе в «Печать»");
            PrintField<LabelTemplate>(hub, "_template").BarcodeFormat.Should().Be(LabelBarcodeFormat.Code128, "«Печать» печатает по правке, а не по старому шаблону");
            d.IsDirty.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Нажатие по «Штрих-код» над холстом выбирает штрихкод для правки и не убирает его с этикетки; выключает — повторное нажатие.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Editor_BarcodeChip_SelectsTheBarcode_WithoutHidingIt(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        LabelTemplateStore.Save(LabelTemplate.CreateDefault());
        var (window, hub) = OpenHub(1024, 600, lang, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            d.Reload();
            UiShot.Settle(4);
            d.SelectedKey.Should().NotBe("barcode");
            ToggleButton Chip() => d.GetVisualDescendants().OfType<ToggleButton>().First(b => (b.Tag as string) == "barcode");
            UiDrive.Click(window, Chip());
            UiShot.Settle(4);
            d.WorkingTemplate.Barcode.Enabled.Should().BeTrue($"{Lang(lang)}: штрихкод остался на этикетке");
            d.SelectedKey.Should().Be("barcode", "нажатие выбрало штрихкод — справа его свойства");
            Chip().IsChecked.Should().BeTrue();
            UiDrive.AllText(window).Should().Contain(Tr.T("Тип штрихкода", "Штрихкод түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"));

            UiDrive.Click(window, Chip());
            UiShot.Settle(4);
            d.WorkingTemplate.Barcode.Enabled.Should().BeFalse("повторное нажатие по выбранному — убрать с этикетки, как раньше");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Поле X показывает то, что реально на этикетке: блок не выходит за край, и в поле не остаётся введённое «30».</summary>
    [AvaloniaFact]
    public async Task Editor_PositionField_ShowsTheClampedValue()
    {
        await TestHost.WarmAsync();
        LabelTemplateStore.Save(LabelTemplate.CreateDefault());
        var (window, hub) = OpenHub(1366, 768, AppLanguage.Russian, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            d.Reload();
            d.SelectElement("barcode");
            UiShot.Settle(4);
            var xBox = d.GetVisualDescendants().OfType<NumericUpDown>().First(n => n.Value == (decimal)Math.Round(d.WorkingTemplate.Barcode.XMm, 2) && n.Maximum == 150 && n.Minimum == 0);
            xBox.Value = 30;
            UiShot.Settle(6);
            var b = d.WorkingTemplate.Barcode;
            (b.XMm + b.WidthMm).Should().BeLessThanOrEqualTo(d.WorkingTemplate.WidthMm + 0.01);
            xBox.Value.Should().Be((decimal)Math.Round(b.XMm, 2), "поле показывает настоящее X, а не введённое 30");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>«Толщина штриха» есть в свойствах штрихкода редактора; «1 (тонкий)» — подсказка, почему штрихкод узкий; у QR поле и «Цифры» неактивны.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Editor_BarcodeThickness_IsEditable_AndThinHintShown(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        LabelTemplateStore.Save(LabelTemplate.CreateDefault());
        var (window, hub) = OpenHub(1024, 600, lang, LabelsHubMode.Editor);
        try
        {
            var d = hub.Designer!;
            d.Reload();
            d.SelectElement("barcode");
            UiShot.Settle(4);
            var thin = "1 " + Tr.T("(тонкий)", "(ичке)", "(thin)", "(ince)", "(ingichka)");
            var thickness = d.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<string>().Contains(thin));
            thickness.SelectedIndex = 1;
            UiShot.Settle(6);
            d.WorkingTemplate.BarcodeModuleDots.Should().Be(1);
            UiDrive.AllText(window).Should().Contain("0,125", $"{Lang(lang)}: подсказка о тонком штрихе");
            var windowRect = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
            Bounds(thickness, window).Right.Should().BeLessThanOrEqualTo(windowRect.Right + 1, $"{Lang(lang)}: поле не выходит за окно 1024×600");

            var format = d.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.OfType<string>().Contains("Code 128"));
            format.SelectedIndex = (int)LabelBarcodeFormat.QrCode;
            UiShot.Settle(4);
            thickness.IsEnabled.Should().BeFalse("у QR толщины штриха нет");
            d.GetVisualDescendants().OfType<CheckBox>().First(c => UiDrive.TextOf(c) == Tr.T("Цифры под штрихкодом", "Штрихкоддун астындагы сандар", "Digits under the barcode", "Barkodun altında rakamlar", "Shtrix-kod ostidagi raqamlar"))
                .IsEnabled.Should().BeFalse("у QR цифр под кодом нет");
            SaveShot(window, $"labeldesigner-thickness-{Lang(lang)}-1024x600", new UiSize(1024, 600));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Шаблон из редактора (бренд, ячейка, дата) в «Печати»: галочка «Артикул» не растягивает штрихкод поверх них, смена размера растягивает и их.</summary>
    [AvaloniaFact]
    public async Task Print_DesignerTemplate_IsNotRearranged_ByCheckboxes_AndScalesWithSize()
    {
        await TestHost.WarmAsync();
        LabelTemplateStore.Save(LabelDesignPresets.ById("shelf")!.Build());
        var (window, hub) = OpenHub(1366, 768, AppLanguage.Russian, LabelsHubMode.Print);
        try
        {
            typeof(BarcodePrintView).GetMethod("ReloadTemplate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(hub.PrintHost.View!, null);
            UiShot.Settle(6);
            var t = PrintField<LabelTemplate>(hub, "_template");
            t.Extras.Should().NotBeEmpty();
            var bar = (t.Barcode.XMm, t.Barcode.YMm, t.Barcode.WidthMm, t.Barcode.HeightMm);
            var name = (t.ProductName.XMm, t.ProductName.YMm, t.ProductName.WidthMm);

            PrintField<CheckBox>(hub, "_showSku").IsChecked = true;
            UiShot.Settle(4);
            t = PrintField<LabelTemplate>(hub, "_template");
            t.Sku.Enabled.Should().BeTrue();
            (t.Barcode.XMm, t.Barcode.YMm, t.Barcode.WidthMm, t.Barcode.HeightMm).Should().Be(bar, "штрихкод не растянут поверх бренда и ячейки");
            (t.ProductName.XMm, t.ProductName.YMm, t.ProductName.WidthMm).Should().Be(name);

            var size = PrintField<ComboBox>(hub, "_sizeCombo");
            var idx = size.Items.OfType<string>().ToList().FindIndex(s => s.StartsWith("40×30", StringComparison.Ordinal));
            idx.Should().BeGreaterThanOrEqualTo(0);
            size.SelectedIndex = idx;
            UiShot.Settle(4);
            t = PrintField<LabelTemplate>(hub, "_template");
            t.WidthMm.Should().Be(40);
            foreach (var e in t.Extras)
            {
                (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(40 + 0.01, $"«{e.Kind}» не ушёл за край после 58×40 → 40×30");
                (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(30 + 0.01);
            }
            SaveShot(window, "labeldesigner-print-designer-template-ru-1366x768", new UiSize(1366, 768));
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ калибровка принтера (после ухода старого окна «Редактор этикетки»)

    [AvaloniaTheory]
    [InlineData(1024, 600)]
    [InlineData(1366, 768)]
    public async Task Print_PrinterSettings_HaveGapShiftsSpeedDensitySensorAndPageSize(int w, int h)
    {
        await TestHost.WarmAsync();
        var prefs = UserPreferences.Instance;
        var saved = (prefs.LabelTsplSensor, prefs.LabelPageFromTemplate, prefs.LabelGapMm, prefs.LabelTsplDensity, prefs.LabelTsplSpeed);
        var (window, hub) = OpenHub(w, h, AppLanguage.Russian, LabelsHubMode.Print);
        try
        {
            var view = hub.PrintHost.View!;
            T Field<T>(string name) => (T)typeof(BarcodePrintView).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
            var body = Field<StackPanel>("_ptBody");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Настройки")); // карточка «Настройки» раскрыта, как у кассира
            UiShot.Settle(6);
            body.IsVisible = true; // «Настройки принтера» раскрыты
            Field<StackPanel>("_ptTspl").IsVisible = true; // как у принтера на языке TSPL
            Field<Button>("_calibrateButton").IsVisible = true;
            UiShot.Settle(6);
            Field<NumericUpDown>("_gapBox").Should().NotBeNull();
            Field<NumericUpDown>("_offXBox").Should().NotBeNull();
            Field<NumericUpDown>("_offYBox").Should().NotBeNull();
            Field<ComboBox>("_speedCombo").Items.Count.Should().BeGreaterThan(2);
            Field<ComboBox>("_densityCombo").Items.Count.Should().BeGreaterThan(2);
            var sensor = Field<ComboBox>("_sensorCombo");
            sensor.Items.Count.Should().Be(3);
            sensor.SelectedIndex = 2;
            prefs.LabelTsplSensor.Should().Be("bline");
            sensor.SelectedIndex = 1;
            prefs.LabelTsplSensor.Should().Be("gap");
            sensor.SelectedIndex = 0;
            prefs.LabelTsplSensor.Should().BeNull();
            var page = Field<CheckBox>("_pageCheck");
            page.IsChecked = false;
            prefs.LabelPageFromTemplate.Should().BeFalse();
            page.IsChecked = true;
            prefs.LabelPageFromTemplate.Should().BeTrue();
            var rows = body.GetVisualDescendants().OfType<WrapPanel>().Where(p => p.IsEffectivelyVisible && p.Children.Count > 1).ToList();
            foreach (var row in rows)
            {
                var rects = row.Children.OfType<Control>().Where(c => c.IsVisible && c.Bounds.Width > 0).Select(c => Bounds(c, window)).ToList();
                for (var i = 0; i < rects.Count; i++)
                    for (var j = i + 1; j < rects.Count; j++)
                    {
                        var inter = rects[i].Intersect(rects[j]);
                        (inter.Width > 1 && inter.Height > 1).Should().BeFalse($"поля калибровки не налезают ({w}x{h})");
                    }
            }
            SaveShot(window, $"labeldesigner-calibration-ru-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            (prefs.LabelTsplSensor, prefs.LabelPageFromTemplate, prefs.LabelGapMm, prefs.LabelTsplDensity, prefs.LabelTsplSpeed) = saved;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WithoutPurchase_EditorShowsTheSerialHint_NotTheEditor()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.LabelEditorUnlocked = false;
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(6);
            window.SelectNavTab("barcodes").Should().BeTrue();
            var hub = window.LabelsHub!;
            hub.ShowMode(LabelsHubMode.Editor);
            UiShot.Settle(8);
            if (TariffGate.LabelEditorAvailable)
                return; // тариф уже включает редактор — подсказки нет
            hub.Designer.Should().BeNull("без покупки редактор не создаётся");
            UiDrive.AllText(window).Should().Contain("серийный номер");
        }
        finally
        {
            window.Close();
        }
    }
}
