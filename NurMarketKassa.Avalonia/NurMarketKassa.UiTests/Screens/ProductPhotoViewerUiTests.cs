using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20, просьба владельца «в маркете при нажатии на фото товара сделай, чтобы он увеличивался для просмотра»: общий просмотрщик фото товара
/// (ProductPhotoViewerWindow) — открывается, закрывается (Esc, ✕, клик вне фото), миниатюра → крупное фото, масштаб колесом и перетаскивание, несколько фото
/// (стрелки, точки), ошибка загрузки без падения, текст не обрезан и элементы не налезают на 1024×600 (ru, ky, uz); плитка каталога: клик по плитке по-прежнему
/// добавляет товар, лупа и долгое касание по фото его не добавляют. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\photo-viewer-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ProductPhotoViewerUiTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "photo-viewer");

    /// <summary>PNG с рисунком (диагональные полосы, рамка, подпись) заданного размера — «фото товара» для проверок.</summary>
    private static string MakePhoto(string name, int w, int h)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, $"{name}-{w}x{h}.png");
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0xE8, 0xEE, 0xF6));
            using var stripe = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6), IsAntialias = true };
            for (var x = -h; x < w; x += 80)
                canvas.DrawLine(x, h, x + h, 0, new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 90), StrokeWidth = 30 });
            using var frame = new SKPaint { Color = new SKColor(0xB4, 0x23, 0x18), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(4, w / 100f) };
            canvas.DrawRect(2, 2, w - 4, h - 4, frame);
            using var text = new SKPaint { Color = SKColors.Black, TextSize = h / 6f, IsAntialias = true };
            canvas.DrawText($"{name} {w}x{h}", w / 20f, h / 2f, text);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static PhotoPathLoader Loader(string? path) => (_, _) => Task.FromResult(path);

    private static ProductPhotoViewerWindow Open(string title, IReadOnlyList<ViewerPhoto> photos, PhotoPathLoader? full, PhotoPathLoader? thumb = null,
        int width = 1024, int height = 600)
    {
        var window = new ProductPhotoViewerWindow(title, photos, 0, full ?? Loader(null), thumb ?? Loader(null));
        window.WindowState = WindowState.Normal; // в Headless размер окна задаёт тест
        using var _ = UiShot.Render(window, new UiSize(width, height));
        UiShot.Settle(6);
        return window;
    }

    private static Point StagePoint(ProductPhotoViewerWindow w, double fx, double fy)
    {
        var stage = w.Stage;
        return stage.TranslatePoint(new Point(stage.Bounds.Width * fx, stage.Bounds.Height * fy), w)!.Value;
    }

    private static void Esc(Window w) => w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

    private static void Shut(Window w)
    {
        if (w.IsVisible)
            w.Close();
        UiShot.Pump();
    }

    private static Button ByName(Window w, string name) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == name);

    // ------------------------------------------------------------------ окно просмотра

    [AvaloniaFact]
    public async Task Viewer_ShowsThumbFirst_ThenFullSizePhoto()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var thumb = MakePhoto("thumb", 200, 100);
        var full = MakePhoto("full", 1200, 600);
        var gate = new TaskCompletionSource<string?>();
        var window = Open("Фильтр масляный Mann", [new ViewerPhoto("https://x.kg/a.jpg", null, thumb)], (_, _) => gate.Task);
        try
        {
            window.ShowsFullSize.Should().BeFalse("крупное фото ещё грузится");
            window.PhotoImage.Source.Should().NotBeNull("пока грузится крупное, видна миниатюра");
            window.PhotoImage.Source!.Size.Width.Should().BeLessThan(1200, "это миниатюра (конвертер кэша ужимает её до 320 px), а не крупное фото");
            window.IsLoadingFull.Should().BeTrue();
            UiDrive.AllText(window).Should().Contain("Загрузка фото");

            gate.SetResult(full);
            UiShot.Settle(10);
            window.ShowsFullSize.Should().BeTrue();
            window.PhotoImage.Source!.Size.Width.Should().Be(1200, "миниатюра заменена крупным фото");
            UiDrive.AllText(window).Should().NotContain("Загрузка фото");
            UiDrive.AllText(window).Should().Contain("Фильтр масляный Mann", "под фото — название товара");
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("esc")]
    [InlineData("cross")]
    [InlineData("outside")]
    public async Task Viewer_Closes_ByEscCrossAndClickOutsideThePhoto(string how)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var full = MakePhoto("wide", 1200, 400); // широкая: сверху и снизу — тёмные поля
        var window = Open("Товар", [new ViewerPhoto("https://x.kg/a.jpg", null, null)], Loader(full));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.ShowsFullSize.Should().BeTrue();
            // Клик ПО фото окно не закрывает.
            var center = StagePoint(window, 0.5, 0.5);
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            UiShot.Pump();
            closed.Should().BeFalse("клик по самому фото не закрывает просмотр");

            switch (how)
            {
                case "esc":
                    Esc(window);
                    break;
                case "cross":
                    UiDrive.Click(window, ByName(window, "Закрыть"));
                    break;
                default:
                    var outside = StagePoint(window, 0.5, 0.04); // тёмное поле над широким фото
                    window.MouseDown(outside, MouseButton.Left);
                    window.MouseUp(outside, MouseButton.Left);
                    break;
            }
            UiShot.Pump();
            closed.Should().BeTrue($"просмотр закрылся ({how})");
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_WheelZoom_Pan_AndReset()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var full = MakePhoto("zoom", 1200, 800);
        var window = Open("Товар", [new ViewerPhoto("https://x.kg/a.jpg", null, null)], Loader(full));
        try
        {
            window.Scale.Should().Be(1);
            var c = StagePoint(window, 0.5, 0.5);
            window.MouseWheel(c, new Vector(0, 4));
            UiShot.Pump();
            window.Scale.Should().BeGreaterThan(1.5, "колесо вперёд — увеличение");
            var m0 = ((MatrixTransform)window.PhotoImage.RenderTransform!).Matrix;

            // Перетаскивание увеличенного фото сдвигает его и не закрывает окно.
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.MouseDown(c, MouseButton.Left);
            window.MouseMove(c + new Point(60, 25));
            window.MouseUp(c + new Point(60, 25), MouseButton.Left);
            UiShot.Pump();
            var m1 = ((MatrixTransform)window.PhotoImage.RenderTransform!).Matrix;
            (m1.M31 != m0.M31 || m1.M32 != m0.M32).Should().BeTrue("фото перетащили");
            closed.Should().BeFalse();

            // Сдвиг не уводит фото за край: левый/верхний край фото не правее края окна.
            window.MouseDown(c, MouseButton.Left);
            window.MouseMove(c + new Point(4000, 4000));
            window.MouseUp(c + new Point(4000, 4000), MouseButton.Left);
            var m2 = ((MatrixTransform)window.PhotoImage.RenderTransform!).Matrix;
            m2.M31.Should().BeLessThanOrEqualTo(0.5);
            m2.M32.Should().BeLessThanOrEqualTo(0.5);

            // Колесо назад — обратно к «целиком»; 0 — тоже.
            window.MouseWheel(c, new Vector(0, -40));
            window.Scale.Should().Be(1);
            window.MouseWheel(c, new Vector(0, 4));
            window.KeyPress(Key.D0, RawInputModifiers.None, PhysicalKey.Digit0, null);
            window.Scale.Should().Be(1);
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_SeveralPhotos_ArrowsKeysAndDots()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var p = new[] { MakePhoto("one", 800, 600), MakePhoto("two", 800, 600), MakePhoto("three", 600, 800) };
        var photos = p.Select((f, i) => new ViewerPhoto("https://x.kg/" + i + ".jpg", null, f)).ToList();
        var window = Open("Товар с тремя фото", photos, (ph, _) => Task.FromResult<string?>(p[int.Parse(ph.FullUrl!.Split('/').Last().Split('.')[0])]));
        try
        {
            window.Count.Should().Be(3);
            window.Index.Should().Be(0);
            UiDrive.AllText(window).Should().Contain("1 / 3");

            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            UiShot.Settle(6);
            window.Index.Should().Be(1);
            UiDrive.AllText(window).Should().Contain("2 / 3");

            UiDrive.Click(window, ByName(window, "Следующее фото"));
            UiShot.Settle(6);
            window.Index.Should().Be(2);
            window.PhotoImage.Source!.Size.Height.Should().Be(800, "третье фото — вертикальное");

            UiDrive.Click(window, ByName(window, "Следующее фото"));
            window.Index.Should().Be(0, "после последнего — снова первое");

            UiDrive.Click(window, ByName(window, "Предыдущее фото"));
            window.Index.Should().Be(2);

            // Точки: по одной на фото, нажатие переключает.
            var dots = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Where(e => e.IsEffectivelyVisible).ToList();
            dots.Should().HaveCount(3);
            var dot = dots[1].GetVisualParent() as Control;
            UiDrive.Click(window, dot!);
            window.Index.Should().Be(1);

            // Свайп влево по неувеличенному фото — следующее.
            var a = StagePoint(window, 0.7, 0.5);
            window.MouseDown(a, MouseButton.Left);
            window.MouseMove(a + new Point(-150, 0));
            window.MouseUp(a + new Point(-150, 0), MouseButton.Left);
            UiShot.Pump();
            window.Index.Should().Be(2);
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_SinglePhoto_HasNoArrowsOrDots()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Open("Товар", [new ViewerPhoto("https://x.kg/a.jpg", null, null)], Loader(MakePhoto("solo", 800, 600)));
        try
        {
            window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible).Should().Be(1, "только ✕");
            window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Any(e => e.IsEffectivelyVisible).Should().BeFalse();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.Index.Should().Be(0);
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_GalleryFromServer_AddsPhotosWithoutLosingTheCurrentOne()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var a = MakePhoto("ga", 800, 600);
        var b = MakePhoto("gb", 800, 600);
        var gate = new TaskCompletionSource<IReadOnlyList<ViewerPhoto>?>();
        var window = new ProductPhotoViewerWindow("Товар", [new ViewerPhoto("https://x.kg/a.jpg", null, a)], 0, (ph, _) => Task.FromResult<string?>(ph.FullUrl!.EndsWith("a.jpg") ? a : b),
            Loader(null), _ => gate.Task);
        window.WindowState = WindowState.Normal;
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.Count.Should().Be(1);
            gate.SetResult([new ViewerPhoto("https://x.kg/a.jpg", "https://x.kg/a_t.jpg", null), new ViewerPhoto("https://x.kg/b.jpg", null, null)]);
            UiShot.Settle(10);
            window.Count.Should().Be(2, "сервер дал ещё одно фото");
            window.Index.Should().Be(0, "то фото, что смотрели, осталось на экране");
            window.ShowsFullSize.Should().BeTrue();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            UiShot.Settle(10);
            window.Index.Should().Be(1);
            window.ShowsFullSize.Should().BeTrue("второе фото загрузилось");
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Viewer_LoadError_ShowsUnderstandableMessage_AndDoesNotCrash(bool withThumb)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var thumb = withThumb ? MakePhoto("thumb-err", 200, 100) : null;
        // «Нет сети»: загрузчик бросает исключение (а не возвращает null) — окно не падает.
        var window = Open("Товар", [new ViewerPhoto("https://x.kg/a.jpg", null, thumb)], (_, _) => throw new HttpRequestException("нет сети"));
        try
        {
            window.IsVisible.Should().BeTrue();
            window.ShowsFullSize.Should().BeFalse();
            if (withThumb)
            {
                window.ShowsError.Should().BeFalse();
                window.PhotoImage.Source.Should().NotBeNull("осталась миниатюра");
                UiDrive.AllText(window).Should().Contain("показана уменьшенная копия");
            }
            else
            {
                window.ShowsError.Should().BeTrue();
                UiDrive.AllText(window).Should().Contain("Не удалось загрузить фото");
            }
            Esc(window);
            UiShot.Pump();
            window.IsVisible.Should().BeFalse("после ошибки окно закрывается как обычно");
        }
        finally
        {
            Shut(window);
        }
    }

    [AvaloniaFact]
    public async Task Open_WithoutPhoto_DoesNotOpenAWindow()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var created = 0;
        ProductPhotoViewer.WindowCreated = _ => created++;
        try
        {
            var noPhoto = new CatalogProductTileVm("n1", "Без фото", "10 сом", false);
            ProductPhotoViewer.HasPhoto(noPhoto).Should().BeFalse();
            ProductPhotoViewer.Open(null, noPhoto).Should().BeFalse();
            ProductPhotoViewer.Open(null, null).Should().BeFalse();
            created.Should().Be(0);
        }
        finally
        {
            ProductPhotoViewer.WindowCreated = null;
        }
    }

    // ------------------------------------------------------------------ раскладка и снимки

    public static IEnumerable<object[]> LayoutCases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768), (1024, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(LayoutCases))]
    public async Task Viewer_Layout_NoClipping_NoOverlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var p = new[] { MakePhoto("L1", 1000, 700), MakePhoto("L2", 700, 1000) };
        var photos = p.Select((f, i) => new ViewerPhoto("https://x.kg/" + i + ".jpg", null, f)).ToList();
        var title = "Фильтр масляный Mann W 811/80 для легковых автомобилей, длинное название товара для проверки переноса строки в подписи под фото";
        var window = Open(title, photos, (ph, _) => Task.FromResult<string?>(p[int.Parse(ph.FullUrl!.Split('/').Last().Split('.')[0])]), null, width, height);
        var tag = $"{width}x{height} {lang}";
        try
        {
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru";
            var clipped = TextClipChecker.Find(window, "photo-viewer", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            Rect R(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            buttons.Should().HaveCount(3, $"✕ и две стрелки ({tag})");
            foreach (var btn in buttons)
            {
                var r = R(btn);
                r.Left.Should().BeGreaterThanOrEqualTo(-0.5, $"{tag}");
                r.Top.Should().BeGreaterThanOrEqualTo(-0.5, $"{tag}");
                r.Right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"{tag}");
                r.Bottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"{tag}");
                r.Width.Should().BeGreaterThanOrEqualTo(44, "цель касания не меньше 44 px");
                r.Height.Should().BeGreaterThanOrEqualTo(44);
            }
            for (var i = 0; i < buttons.Count; i++)
                for (var j = i + 1; j < buttons.Count; j++)
                    R(buttons[i]).Intersects(R(buttons[j])).Should().BeFalse($"кнопки не налезают друг на друга ({tag})");

            // Подпись (название, «1 / 2», точки) — под сценой и не пересекается с ней; название не выходит за окно.
            var stageRect = R(window.Stage);
            var titleBlock = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title);
            var titleRect = R(titleBlock);
            titleRect.Intersects(stageRect).Should().BeFalse($"название под фото, не поверх него ({tag})");
            titleRect.Bottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5);
            titleRect.Left.Should().BeGreaterThanOrEqualTo(0);
            titleRect.Right.Should().BeLessThanOrEqualTo(window.Bounds.Width);
            stageRect.Height.Should().BeGreaterThan(window.Bounds.Height * 0.6, $"фото получает большую часть окна ({tag})");

            // Фото вписано целиком (Uniform): его размер на экране не больше сцены.
            window.PhotoImage.Bounds.Width.Should().BeLessThanOrEqualTo(stageRect.Width + 0.5);
            window.PhotoImage.Bounds.Height.Should().BeLessThanOrEqualTo(stageRect.Height + 0.5);

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"photo-viewer-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            Shut(window);
        }
    }

    // ------------------------------------------------------------------ плитка каталога

    private static NurMarketKassa.Services.CatalogViewMode PrevMode;

    private static (CatalogListFixture.Built Built, List<CatalogProductTileVm> Picked, CatalogProductTileVm WithPhoto, CatalogProductTileVm Without) Catalog(double width, bool cards = true)
    {
        var photo = MakePhoto("tile", 320, 200);
        var picked = new List<CatalogProductTileVm>();
        CatalogProductTileVm? withPhoto = null, without = null;
        var built = CatalogListFixture.Create(width, 700, 0, picked.Add, items =>
        {
            withPhoto = items.First(t => t.Id == "p1");
            withPhoto.ImageUrl = "https://x.kg/p1.jpg";
            withPhoto.ProductImagePath = photo;
            without = items.First(t => t.Id == "p2");
        });
        PrevMode = built.Vm.ViewMode;
        if (cards)
            built.Vm.SetViewMode(NurMarketKassa.Services.CatalogViewMode.Cards); // плитки; в окне уже 1280 каталог сам показывается списком (автосписок)
        return (built, picked, withPhoto!, without!);
    }

    [AvaloniaFact]
    public async Task CatalogTile_ClickStillAddsProduct_LoupeOnlyOnProductsWithPhoto()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (built, picked, withPhoto, without) = Catalog(1400);
        var opened = new List<ProductPhotoViewerWindow>();
        ProductPhotoViewer.WindowCreated = opened.Add;
        try
        {
            built.Window.Show();
            UiShot.Settle(8);

            var loupes = built.Window.GetVisualDescendants().OfType<PhotoZoomButton>().Where(b => b.IsEffectivelyVisible).ToList();
            loupes.Should().HaveCount(1, "лупа есть только у товара с фото");
            ((CatalogProductTileVm)loupes[0].DataContext!).Id.Should().Be("p1");

            // 1) Нажатие по плитке (по названию) по-прежнему кладёт товар в чек.
            var tile = built.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.DataContext == withPhoto && b.GetType() == typeof(Button) && Avalonia.Automation.AutomationProperties.GetName(b) == withPhoto.Title);
            var title = tile.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == withPhoto.Title);
            UiDrive.Click(built.Window, title);
            picked.Should().ContainSingle().Which.Should().BeSameAs(withPhoto, "клик по плитке добавляет товар");
            opened.Should().BeEmpty();

            // 2) Нажатие по ФОТО плитки (мышью) — тоже добавление, как раньше (фото не стало отдельной кнопкой).
            picked.Clear();
            var photoBorder = tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tile-photo"));
            var pp = photoBorder.TranslatePoint(new Point(photoBorder.Bounds.Width * 0.25, photoBorder.Bounds.Height * 0.5), built.Window)!.Value;
            built.Window.MouseDown(pp, MouseButton.Left);
            Thread.Sleep(ProductPhotoZoom.LongPressMs + 250); // долгое нажатие мышью — просто медленный клик
            UiShot.Pump();
            built.Window.MouseUp(pp, MouseButton.Left);
            UiShot.Pump();
            opened.Should().BeEmpty("мышью долгое нажатие просмотр не открывает");
            picked.Should().ContainSingle("клик мышью по фото плитки добавляет товар, как раньше");

            // 3) Лупа: открывает просмотр и товар в чек НЕ добавляет.
            picked.Clear();
            UiDrive.Click(built.Window, loupes[0]);
            UiShot.Settle(6);
            picked.Should().BeEmpty("лупа не добавляет товар в чек");
            opened.Should().ContainSingle();
            opened[0].IsVisible.Should().BeTrue();
            Esc(opened[0]);
            UiShot.Pump();
            opened[0].IsVisible.Should().BeFalse();
        }
        finally
        {
            ProductPhotoViewer.WindowCreated = null;
            foreach (var w in opened)
                Shut(w);
            built.Vm.SetViewMode(PrevMode);
            built.Dispose();
        }
        _ = without;
    }

    [AvaloniaFact]
    public async Task CatalogTile_LongTouchOnPhoto_OpensViewer_AndDoesNotAddProduct()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (built, picked, withPhoto, _) = Catalog(1400);
        var opened = new List<ProductPhotoViewerWindow>();
        ProductPhotoViewer.WindowCreated = opened.Add;
        try
        {
            built.Window.Show();
            UiShot.Settle(8);
            var tile = built.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.DataContext == withPhoto && b.GetType() == typeof(Button) && Avalonia.Automation.AutomationProperties.GetName(b) == withPhoto.Title);
            var photoBorder = tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tile-photo"));
            var pos = photoBorder.TranslatePoint(new Point(photoBorder.Bounds.Width * 0.25, photoBorder.Bounds.Height * 0.5), built.Window)!.Value;

            // Короткое касание: палец отпущен раньше срока — просмотр не открывается, а плитка нажимается (товар в чек).
            var shortTouch = Touch(built.Window, photoBorder, pos);
            UiShot.Settle(2, 20);
            Release(built.Window, shortTouch, pos);
            Thread.Sleep(ProductPhotoZoom.LongPressMs + 250);
            UiShot.Pump();
            opened.Should().BeEmpty("короткое касание просмотр не открывает");
            picked.Should().ContainSingle("короткое касание по плитке кладёт товар в чек");
            picked.Clear();

            // Долгое касание: палец держим дольше срока — просмотр открыт, захват у кнопки плитки снят, при отпускании товар в чек не попадает.
            var touch = Touch(built.Window, photoBorder, pos);
            Thread.Sleep(ProductPhotoZoom.LongPressMs + 250);
            UiShot.Pump();
            opened.Should().ContainSingle("долгое касание по фото открыло просмотр");
            touch.Captured.Should().BeNull("захват у кнопки плитки снят — отпускание пальца её не нажмёт");
            Release(built.Window, touch, pos);
            picked.Should().BeEmpty("товар в чек не добавлен");
            ProductPhotoZoom.LongPressJustHandled.Should().BeTrue("карточка «Подробнее» поверх просмотра не откроется");
        }
        finally
        {
            ProductPhotoViewer.WindowCreated = null;
            foreach (var w in opened)
                Shut(w);
            built.Vm.SetViewMode(PrevMode);
            built.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CatalogTable_PhotoOpensViewer_RestOfRowStillAddsProduct()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // Окно уже 1280 — каталог сам показывается списком (так выглядит касса на 1024×600): нажатие на фото-миниатюру строки открывает просмотр, остальная строка добавляет товар.
        var (built, picked, withPhoto, _) = Catalog(900, cards: false);
        var opened = new List<ProductPhotoViewerWindow>();
        ProductPhotoViewer.WindowCreated = opened.Add;
        try
        {
            built.Window.Show();
            UiShot.Settle(8);
            var row = built.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.DataContext == withPhoto && b.GetType() == typeof(Button) && Avalonia.Automation.AutomationProperties.GetName(b) == withPhoto.Title);
            var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == withPhoto.Title);
            UiDrive.Click(built.Window, title);
            picked.Should().ContainSingle("нажатие на строку (название) добавляет товар, как раньше");
            opened.Should().BeEmpty();

            picked.Clear();
            var photo = built.Window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tbl-photo") && b.IsEffectivelyVisible && b.Child is Image);
            UiDrive.Click(built.Window, photo);
            UiShot.Settle(6);
            picked.Should().BeEmpty("нажатие на фото не добавляет товар");
            opened.Should().ContainSingle("нажатие на фото открывает просмотр");
            Esc(opened[0]);
            UiShot.Pump();
            opened[0].IsVisible.Should().BeFalse();
        }
        finally
        {
            ProductPhotoViewer.WindowCreated = null;
            foreach (var w in opened)
                Shut(w);
            built.Vm.SetViewMode(PrevMode);
            built.Dispose();
        }
    }

    private static IPointer Touch(Window window, Control target, Point pos)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var e = new PointerPressedEventArgs(target, pointer, window, pos, 0, props, KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent };
        target.RaiseEvent(e);
        return pointer;
    }

    private static void Release(Window window, IPointer pointer, Point pos)
    {
        var target = (pointer.Captured as Control) ?? (Control)window.GetVisualAt(pos)!;
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var e = new PointerReleasedEventArgs(target, pointer, window, pos, 0, props, KeyModifiers.None, MouseButton.Left) { RoutedEvent = InputElement.PointerReleasedEvent };
        target.RaiseEvent(e);
        UiShot.Pump();
    }
}
