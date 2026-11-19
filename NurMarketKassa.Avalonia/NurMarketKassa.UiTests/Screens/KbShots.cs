using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (версия 2.0): общий код генератора снимков и анимаций встроенной базы знаний (Assets/kb). Включается переменной
/// NURMARKET_KB_SHOTS=1; без неё все тесты-генераторы сразу выходят (обычный прогон не меняется). NURMARKET_KB_ONLY=имя1,имя2 — снять только
/// указанные картинки (по имени без языка и расширения, напр. kassa-main), NURMARKET_KB_OUT — другая папка вместо Assets/kb.
/// Данные — только вымышленные (поддельный сервер UiFake, демо-товары ниже); настоящие данные магазина в кадры не попадают (папка данных — временная).
/// Запуск: powershell -ExecutionPolicy Bypass -File tools/qa/test-safe.ps1 -Phase ui -Filter "FullyQualifiedName~KbShots" (с NURMARKET_KB_SHOTS=1 в окружении).</summary>
public static class KbShots
{
    public static bool On => Environment.GetEnvironmentVariable("NURMARKET_KB_SHOTS") == "1";

    private static readonly string[] Only = (Environment.GetEnvironmentVariable("NURMARKET_KB_ONLY") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Картинки, которые снимаются только по прямому заказу в NURMARKET_KB_ONLY: данных для них у стенда нет (отчёты сервера), кадр вышел бы «сервер недоступен».</summary>
    private static readonly string[] OptIn = ["owner-overview"];

    /// <summary>Нужна ли эта картинка в текущем запуске.</summary>
    public static bool Want(string name) => On && (Only.Length == 0 ? !OptIn.Contains(name) : Only.Contains(name));

    /// <summary>Порядок языков: русский — основа без суффикса, остальные — «.ky/.en/.tr/.uz» (как их ищет окно базы знаний).</summary>
    public static readonly AppLanguage[] Langs = [AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek];

    public static string Suffix(AppLanguage lang) => lang switch
    {
        AppLanguage.Kyrgyz => ".ky",
        AppLanguage.English => ".en",
        AppLanguage.Turkish => ".tr",
        AppLanguage.Uzbek => ".uz",
        _ => "",
    };

    public static string OutDir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("NURMARKET_KB_OUT") is { Length: > 0 } custom)
                return Directory.CreateDirectory(custom).FullName;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "NurMarketKassa.Avalonia", "Assets", "kb");
                if (Directory.Exists(candidate))
                    return candidate;
            }
            throw new DirectoryNotFoundException("Папка Assets/kb не найдена выше " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Каталог снятых картинок: имя → число сохранённых файлов (для отчёта в консоль).</summary>
    public static readonly Dictionary<string, int> Done = new();

    public static void SavePng(SKBitmap bmp, string name, AppLanguage lang)
    {
        File.WriteAllBytes(Path.Combine(OutDir, name + Suffix(lang) + ".png"), KbImageEncoder.EncodePng(bmp));
        Done[name] = Done.GetValueOrDefault(name) + 1;
    }

    public static void SaveGif(IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> delaysCs, string name, AppLanguage lang)
    {
        File.WriteAllBytes(Path.Combine(OutDir, name + Suffix(lang) + ".gif"), KbImageEncoder.EncodeGif(frames, delaysCs));
        Done[name] = Done.GetValueOrDefault(name) + 1;
    }

    /// <summary>Снимок окна (с размером — фиксирует его) → PNG в Assets/kb.</summary>
    public static void Shot(Window window, string name, AppLanguage lang, UiSize? size = null)
    {
        using var bmp = UiShot.Render(window, size);
        SavePng(bmp, name, lang);
    }

    /// <summary>Уменьшение кадра (для GIF) с хорошим сглаживанием.</summary>
    public static SKBitmap Scale(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        using var image = SKImage.FromBitmap(src);
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawImage(image, new SKRect(0, 0, width, height), paint);
        return dst;
    }

    // ------------------------------------------------------------------ кадры анимаций

    /// <summary>Кадр окна как есть (для сборки анимации).</summary>
    public static SKBitmap Frame(Window window, UiSize? size = null) => UiShot.Render(window, size);

    /// <summary>Кадр окна поверх затемнённого кадра основы — как модальное окно в кассе (окно по центру, фон темнее).</summary>
    public static SKBitmap Overlay(SKBitmap baseFrame, SKBitmap dialogFrame)
    {
        var result = new SKBitmap(baseFrame.Width, baseFrame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(baseFrame, 0, 0);
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 90) })
            canvas.DrawRect(0, 0, baseFrame.Width, baseFrame.Height, dim);
        using var clean = KbImageEncoder.BlackFrameToTransparent(dialogFrame);
        canvas.DrawBitmap(clean, (baseFrame.Width - clean.Width) / 2f, (baseFrame.Height - clean.Height) / 2f);
        return result;
    }

    /// <summary>Кадр анимации: окно уменьшается до ширины GIF (пропорции сохраняются), под ним — тёмная полоса с номером шага и подписью
    /// (на окно подпись не налезает). Итоговый размер кадра — width × height.</summary>
    public static SKBitmap Step(SKBitmap source, int width, int height, int step, int total, string text)
    {
        var barHeight = 64;
        var imageHeight = Math.Min(height - barHeight, (int)Math.Round(source.Height * (double)width / source.Width));
        var frame = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(frame);
        canvas.Clear(new SKColor(17, 24, 39));
        using (var scaled = Scale(source, width, imageHeight))
            canvas.DrawBitmap(scaled, 0, 0);
        using var face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        var top = height - barHeight;
        using var badge = new SKPaint { Color = new SKColor(255, 176, 0), IsAntialias = true };
        canvas.DrawRoundRect(14, top + (barHeight - 30) / 2f, 62, 30, 15, 15, badge);
        using var dark = new SKPaint { Color = new SKColor(17, 24, 39), IsAntialias = true, TextSize = 17, Typeface = face, TextAlign = SKTextAlign.Center };
        canvas.DrawText($"{step} / {total}", 14 + 31, top + (barHeight - 30) / 2f + 21, dark);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 19, Typeface = face };
        var lines = Wrap(text, white, width - 100f);
        var lineHeight = 25f;
        var y = top + (barHeight - lines.Count * lineHeight) / 2f + 19;
        foreach (var line in lines)
        {
            canvas.DrawText(line, 92, y, white);
            y += lineHeight;
        }
        return frame;
    }

    private static List<string> Wrap(string text, SKPaint paint, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (paint.MeasureText(candidate) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
                current = candidate;
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    /// <summary>Пауза кадров: 1,8 с на кадр, последний — 3 с (в сотых долях секунды).</summary>
    public static List<int> Delays(int frames) => Enumerable.Range(0, frames).Select(i => i == frames - 1 ? 300 : 180).ToList();

    // ------------------------------------------------------------------ вымышленный магазин

    private static readonly (string Name, string Price, string Qty, bool Weight, string Category)[] Goods =
    [
        ("Хлеб «Бородинский» нарезной", "45.00", "10", false, "Хлеб"),
        ("Яблоки Голден", "120.00", "5.5", true, "Фрукты"),
        ("Молоко 3,2 % 1 л", "89.00", "24", false, "Молочные"),
        ("Кока-Кола 1 л", "95.00", "3", false, "Напитки"),
        ("Сыр Российский", "640.00", "12.4", true, "Молочные"),
        ("Чай чёрный листовой 100 пакетиков", "310.00", "0", false, "Чай, кофе"),
        ("Шоколад молочный", "135.50", "48", false, "Сладости"),
        ("Масло подсолнечное 1 л", "210.00", "17", false, "Бакалея"),
        ("Сахар 1 кг", "98.00", "150", false, "Бакалея"),
        ("Курица охлаждённая", "385.00", "8.2", true, "Мясо"),
        ("Стиральный порошок 3 кг", "12500.00", "6", false, "Бытовая химия"),
        ("Вода питьевая 5 л", "70.00", "32", false, "Напитки"),
        ("Печенье овсяное", "65.00", "40", false, "Сладости"),
        ("Йогурт клубничный", "55.00", "28", false, "Молочные"),
        ("Макароны спагетти 500 г", "72.00", "60", false, "Бакалея"),
        ("Огурцы свежие", "110.00", "14.5", true, "Овощи"),
    ];

    private static string GoodsJson()
    {
        var items = Goods.Select((g, i) => new Dictionary<string, object?>
        {
            ["id"] = "kb-" + (i + 1),
            ["name"] = g.Name,
            ["barcode"] = (4700000000000 + i + 1).ToString(),
            ["price"] = g.Price,
            ["purchase_price"] = (decimal.Parse(g.Price, System.Globalization.CultureInfo.InvariantCulture) * 0.7m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["quantity"] = g.Qty,
            ["is_weight"] = g.Weight,
            ["category"] = g.Category,
        });
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["count"] = Goods.Length, ["next"] = null, ["previous"] = null, ["results"] = items });
    }

    /// <summary>Поддельный сервер с демо-товарами, открытая смена и каталог в кэше. Возвращает действие «вернуть как было».</summary>
    public static async Task<Action> BeginDemoShopAsync()
    {
        await TestHost.WarmAsync();
        var savedShift = PosApp.ActiveShiftId;
        UiFake.Reset();
        UiFake.Server.Server.Reset();
        UiFake.Server.StubLogin();
        UiFake.Server.StubProfile();
        UiFake.Server.Json("GET", "/api/main/products/list/", 200, GoodsJson());
        UiFake.Server.StubCheckout();
        UiFake.Server.StubShiftOpen();
        await Task.Run(() => CatalogCacheService.SyncCatalogFullAsync());
        SetShift(Guid.NewGuid().ToString());
        TrainingMode.Exit();
        return () =>
        {
            SetShift(savedShift);
            UiFake.Reset();
        };
    }

    public static void SetShift(string? id)
    {
        PosApp.ActiveShiftId = id;
        TestHost.Services.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().ActiveShiftId = id;
    }

    public static MainWindowViewModel Vm(Window main) => (MainWindowViewModel)main.DataContext!;

    /// <summary>Скан кода в единственное поле главного экрана (Enter добавляет товар в чек).</summary>
    public static void Scan(Window main, string code)
    {
        var search = main.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Name == "ProductSearchBox");
        if (search is null)
            return;
        UiDrive.Type(main, search, code);
        Thread.Sleep(400);
        main.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        main.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        UiShot.Settle(8, 40);
    }

    public static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }
}
