using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (владелец прислал снимок «Нового приёма в ремонт»: поле «Комплектация» налезало на следующее поле, оно было обрезано краем окна, «Срок, дней» — нижним краем):
/// формы «Ремонта и сервиса» (приём в ремонт, «Стоимость работ», новый выкуп, «Оприходовать как б/у») показываются так же, как в программе — фиксированная ширина окна 800, высота по содержимому,
/// предел размера по экрану кассы (800×600, 1024×600, 1366×768; ru, ky, uz). Условия: ни одно поле, подпись и подсказка не выходят за область содержимого по ширине; «Сохранить» и «Отмена» целиком видны
/// в окне, окно не больше экрана; поля не налезают друг на друга (проба <see cref="FormFieldsOverlapUiTests.FindViolations"/>); текст подписей не обрезан.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\servicefit-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ServiceFormsFitUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "servicefit-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ServiceFormsFitUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private sealed class NoTill : IServiceDeskTill
    {
        public decimal CartTotal => 0m;
        public bool CartHasItems => false;
        public bool AddLine(string name, decimal amount, bool credit) => true;
        public Task<bool> CashAsync(bool withdrawal, decimal amount, string reason) => Task.FromResult(true);
    }

    private static RepairOrdersWindow RepairWindow(string dir) => new(new RepairOrdersDeps
    {
        Store = new RepairOrderStore(Path.Combine(dir, "repair-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 26, 10, 0, 0)),
        Till = new NoTill(),
        Print = _ => Task.FromResult<string?>(null),
        OpenUrl = _ => true,
        Confirm = (_, _) => Task.FromResult(true),
        Cashier = () => "Тест",
        Today = () => new DateTime(2026, 11, 26),
    });

    private static TradeInWindow TradeWindow(string dir) => new(new TradeInDeps
    {
        Store = new TradeInStore(Path.Combine(dir, "trade-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 26, 10, 0, 0)),
        Till = new NoTill(),
        Print = _ => Task.FromResult<string?>(null),
        Confirm = (_, _) => Task.FromResult(true),
        Cashier = () => "Тест",
        Stock = _ => Task.FromResult((true, "")),
    });

    /// <summary>Формы, собранные теми же конструкторами, что и в программе (поля и подписи — боевые).</summary>
    internal static ServiceDeskFormDialog CreateForm(string dir, string name)
    {
        switch (name)
        {
            case "repair-new":
                return RepairWindow(dir).BuildNewForm();
            case "tradein-new":
                return TradeWindow(dir).BuildNewForm();
            case "repair-price": // «Стоимость работ» (RepairOrdersWindow.AskReadyAsync): одно поле, длинная подсказка про предоплату
                return new ServiceDeskFormDialog(Tr.T("Стоимость работ", "Иштин баасы", "Cost of the work", "İş bedeli", "Ish narxi"),
                    Tr.T("Предоплата уже внесена: 1 500,00 сом. К доплате будет итог минус предоплата.", "Алдын ала төлөм салынган: 1 500,00 сом. Кошумча төлөм = жыйынтык минус алдын ала төлөм.", "Prepayment already taken: 1 500,00 som. The amount due is the total minus the prepayment.", "Ön ödeme alındı: 1 500,00 som. Ödenecek tutar = toplam − ön ödeme.", "Oldindan to'lov olingan: 1 500,00 so'm. To'lanadigan summa = jami − oldindan to'lov."),
                    new List<FormField> { new("price", Tr.T("Итог, сом", "Жыйынтык, сом", "Total, som", "Toplam, som", "Jami, so'm"), "1700", MaxLength: 12, Width: 300) },
                    _ => new List<string>(), Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
            default:
                throw new InvalidOperationException("Неизвестная форма " + name);
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var form in new[] { "repair-new", "tradein-new", "repair-price" })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        foreach (var size in new[] { new UiSize(800, 600), new UiSize(1024, 600), new UiSize(1366, 768) })
            yield return [form, lang, size.Width, size.Height];
    }

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    /// <summary>Показ как в программе: окно с собственной шириной (800) и высотой по содержимому; затем — то, что делает FitToKassaScreen на экране кассы заданного размера
    /// (в Headless экран другой, поэтому предел ставится здесь). Рамки ОС у формы нет (SystemDecorations.None), отступ окна от края экрана — 16.</summary>
    internal static void ShowLikeInApp(Window w, UiSize screen)
    {
        ModalShell.WorkingAreaForTests = new Size(screen.Width, screen.Height);
        w.Show();
        UiShot.Settle(4);
        ApplyKassaScreenLimits(w, screen);
        UiShot.Settle(6, 30);
    }

    /// <summary>То, что делает DialogScreenFit.FitToKassaScreen для окна без рамки ОС на экране кассы заданного размера (в Headless экран другой, поэтому считаем здесь):
    /// минимум опускается до рабочей области, предел размера — рабочая область минус отступ 16, заданные Width/Height сжимаются до предела.</summary>
    internal static void ApplyKassaScreenLimits(Window w, UiSize screen)
    {
        var maxWidth = Math.Max(320, screen.Width - 16);
        var maxHeight = Math.Max(240, screen.Height - 16);
        if (w.MinWidth > maxWidth)
            w.MinWidth = maxWidth;
        if (w.MinHeight > maxHeight)
            w.MinHeight = maxHeight;
        w.MaxWidth = maxWidth;
        w.MaxHeight = maxHeight;
        if (!double.IsNaN(w.Width) && w.Width > maxWidth)
            w.Width = maxWidth;
        if (!double.IsNaN(w.Height) && w.Height > maxHeight)
            w.Height = maxHeight;
    }

    private static Rect? RectIn(Visual v, Visual root) => v.TranslatePoint(default, root) is { } p ? new Rect(p, v.Bounds.Size) : null;

    private static bool Shown(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (!cur.IsVisible || cur.Opacity <= 0.01)
                return false;
        return v.Bounds.Width > 0 && v.Bounds.Height > 0;
    }

    internal static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    /// <summary>Нарушения вписывания: что-то выходит за ширину окна или области прокрутки без боковой полосы (справа не достать), кнопки низа не видны целиком, окно больше экрана.
    /// <paramref name="formButtons"/> — у формы «Сервиса» проверяются ещё «Сохранить» и «Отмена» (FormSave / FormCancel).</summary>
    internal static List<string> FitProblems(Window w, UiSize screen, bool formButtons = true)
    {
        var problems = new List<string>();
        var client = w.ClientSize;
        if (w.Bounds.Width > screen.Width - 16 + 1)
            problems.Add($"окно шире экрана: {w.Bounds.Width:0} при экране {screen.Width} (отступ 16)");
        if (w.Bounds.Height > screen.Height - 16 + 1)
            problems.Add($"окно выше экрана: {w.Bounds.Height:0} при экране {screen.Height} (отступ 16)");

        static string Name(Control c) => $"{c.GetType().Name}#{c.Name} «{(c as TextBlock)?.Text ?? (c as TextBox)?.Watermark ?? UiDrive.TextOf(c)}»";
        static bool Content(Control c) => (c is TextBox or TextBlock or Button or ComboBox) && Shown(c) && !c.GetVisualAncestors().OfType<TextBox>().Any() && c is not TextBlock { Text: null or "" };

        // Область прокрутки без боковой полосы (все модалки): всё внутри — целиком в её ширине, иначе правый край обрезан и недоступен.
        foreach (var sv in w.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && Shown(s)))
            foreach (var c in sv.GetVisualDescendants().OfType<Control>().Where(Content))
                if (RectIn(c, sv) is { } r && (r.Left < -2 || r.Right > sv.Bounds.Width + 2))
                    problems.Add($"{Name(c)} выходит за ширину области прокрутки: {r.Left:0}…{r.Right:0} при {sv.Bounds.Width:0}");

        // Всё остальное — в ширине окна (кроме областей с боковой прокруткой: списки, таблицы).
        foreach (var c in w.GetVisualDescendants().OfType<Control>().Where(Content))
        {
            if (c.GetVisualAncestors().OfType<ScrollViewer>().Any(s => s.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled))
                continue;
            if (RectIn(c, w) is { } r && (r.Left < -2 || r.Right > client.Width + 2))
                problems.Add($"{Name(c)} выходит за ширину окна: {r.Left:0}…{r.Right:0} при {client.Width:0}");
        }

        if (!formButtons)
            return problems;
        // Кнопки «Сохранить» / «Отмена» целиком в окне и не под нижним краем.
        foreach (var name in new[] { "FormSave", "FormCancel" })
        {
            var b = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == name);
            if (b is null || !Shown(b))
            {
                problems.Add($"кнопка {name} не видна");
                continue;
            }
            if (RectIn(b, w) is { } r && (r.Left < -2 || r.Right > client.Width + 2 || r.Top < -2 || r.Bottom > client.Height + 2))
                problems.Add($"кнопка {name} не помещается в окно: {r} при окне {client.Width:0}×{client.Height:0}");
        }
        return problems;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ServiceForm_FitsScreen_NoOverlap_NoClipping(string form, AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var screen = new UiSize(width, height);
        var dlg = CreateForm(_dir, form);
        try
        {
            ShowLikeInApp(dlg, screen);
            var tag = $"{form} {Code(lang)} {screen}";
            Save(dlg, $"servicefit-{form}-{Code(lang)}-{screen}.png");

            var problems = FitProblems(dlg, screen);
            problems.AddRange(FormFieldsOverlapUiTests.FindViolations(dlg, tag).Select(v => v.Detail));
            problems.AddRange(TextClipChecker.Find(dlg, form, Code(lang)).Select(v => v.Detail));
            problems.Should().BeEmpty($"форма {tag}: нет обрезания и налезания\n  " + string.Join("\n  ", problems));
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Проверка самой проверки: поле за правым краем окна на экране 800×600 обязано попасть в «выходит за ширину окна», а окно без кнопок формы — в «кнопка не видна»; иначе «всё чисто» ничего не стоит.</summary>
    [AvaloniaFact]
    public async Task Checker_FindsFieldBeyondWindowEdge()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var canvas = new Canvas();
        var box = new TextBox { Name = "Wide", Width = 200, Height = 40 };
        Canvas.SetLeft(box, 700); // правый край 900 при окне 784
        Canvas.SetTop(box, 20);
        canvas.Children.Add(box);
        var dlg = new Window { Content = canvas, Width = 800, Height = 300 };
        try
        {
            ShowLikeInApp(dlg, new UiSize(800, 600));
            FitProblems(dlg, new UiSize(800, 600), formButtons: false).Should().Contain(p => p.Contains("выходит за ширину окна"), "поле за правым краем окна должно находиться пробой");
            FitProblems(dlg, new UiSize(800, 600)).Should().Contain(p => p.Contains("FormSave"), "у окна без кнопок «Сохранить» / «Отмена» проверка форм это замечает");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }
}
