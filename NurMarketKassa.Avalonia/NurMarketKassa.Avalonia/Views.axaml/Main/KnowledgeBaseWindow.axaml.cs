using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «База знаний» — вопросы и пошаговое обучение со скриншотами (2026-09-26, просьба владельца:
/// «все вопросы и обучение, наглядные фото как что делать»). Статьи — из базы знаний бота
/// поддержки NurCRM (NurSupportBot/seed/kb.json), собранные в Assets/kb/kb.json вместе с
/// обновлениями этой версии; картинки — снимки настоящих окон кассы и программы владельца на
/// тестовой компании (Assets/kb/*.png, личные данные на них размыты). Слева поиск и разделы,
/// справа статья: шаги с номерами, «Важно» отдельно, снимки с подписями (нажатие — крупнее).
///
/// 2026-09-26, «баг с языком»: статьи на языке интерфейса — kb.ky.json / kb.en.json / kb.tr.json /
/// kb.uz.json (та же структура и те же id, что у kb.json); статья, которой нет в переводе, берётся
/// по-русски. При смене языка окно перечитывает статьи и остаётся на той же статье.
/// </summary>
public partial class KnowledgeBaseWindow : Window, IOwnerSection
{
    private const string AssetRoot = "avares://NurMarketKassa.Avalonia/Assets/kb/";
    private static readonly Regex StepPattern = new(@"^Шаг\s+(\d+)\.\s*(.*)$", RegexOptions.CultureInvariant);

    private sealed record KbImage(string File, string Caption);
    private sealed record KbArticle(string Id, string Title, string Text, List<string> Keywords, List<KbImage> Images, string Section, string Group, string Note)
    {
        /// <summary>Русские название и ключевые слова — чтобы поиск по-русски находил статью и
        /// при другом языке интерфейса.</summary>
        public string RussianSearch { get; init; } = "";
    }

    private const string StartArticleId = "продажи-как-создать-продажу";

    private readonly List<KbArticle> _articles = new();
    private readonly Dictionary<string, Button> _navButtons = new();

    /// <summary>2026-09-29, владелец: «сделай сворачиваемыми — только при выборе категории раскрывай
    /// список». Открыт один раздел (раздел текущей статьи или выбранный); при поиске раскрыты все
    /// найденные разделы.</summary>
    private string? _openSection;
    private KbArticle? _current;

    public KnowledgeBaseWindow()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => BuildNav(SearchBox.Text);
        ApplyLanguage(StartArticleId);
        Tr.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLanguage(_current?.Id));

    private void ApplyLanguage(string? articleId)
    {
        SubtitleText.Text = Tr.T(
            "Вопросы и пошаговое обучение со скриншотами кассы и программы владельца",
            "Кассанын жана ээсинин программасынын скриншоттору менен суроолор жана кадам-кадам окутуу",
            "Questions and step-by-step guides with screenshots of the till and the owner program",
            "Sorular ve kasa ile sahip programının ekran görüntüleriyle adım adım eğitim",
            "Kassa va ega dasturi skrinshotlari bilan savollar va bosqichma-bosqich qo'llanma");
        SearchBox.Watermark = Tr.T("Поиск: например, «возврат» или «весы»", "Издөө: мисалы, «кайтаруу» же «тараза»",
            "Search: e.g. “return” or “scale”", "Ara: örneğin «iade» veya «tartı»", "Qidiruv: masalan, «qaytarish» yoki «tarozi»");

        _current = null;
        _articles.Clear();
        var russian = ReadArticles("kb.json");
        var language = Tr.T("ru", "ky", "en", "tr", "uz");
        var translated = language == "ru"
            ? new Dictionary<string, KbArticle>()
            : ReadArticles($"kb.{language}.json").GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var article in russian)
        {
            var shown = translated.TryGetValue(article.Id, out var t) ? t : article;
            _articles.Add(shown with
            {
                // Снимки одни на все языки: файлы — из русской базы, подписи — из перевода.
                Images = article.Images.Select((image, i) => image with
                {
                    Caption = i < shown.Images.Count && shown.Images[i].Caption.Length > 0 ? shown.Images[i].Caption : image.Caption,
                }).ToList(),
                RussianSearch = article.Title + " " + string.Join(" ", article.Keywords),
            });
        }

        BuildNav(SearchBox.Text);
        if (_articles.Count > 0)
            ShowArticle(_articles.FirstOrDefault(a => a.Id == articleId) ?? _articles[0]);
    }

    private static List<KbArticle> ReadArticles(string fileName)
    {
        var articles = new List<KbArticle>();
        try
        {
            var uri = new Uri(AssetRoot + fileName);
            if (!AssetLoader.Exists(uri))
                return articles;
            using var stream = AssetLoader.Open(uri);
            using var doc = JsonDocument.Parse(stream);
            foreach (var section in doc.RootElement.GetProperty("sections").EnumerateArray())
            {
                var sectionTitle = section.GetProperty("title").GetString() ?? "";
                var note = section.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
                foreach (var group in section.GetProperty("groups").EnumerateArray())
                {
                    var groupTitle = group.TryGetProperty("title", out var g) ? g.GetString() ?? "" : "";
                    foreach (var a in group.GetProperty("articles").EnumerateArray())
                    {
                        articles.Add(new KbArticle(
                            a.GetProperty("id").GetString() ?? "",
                            a.GetProperty("title").GetString() ?? "",
                            a.GetProperty("text").GetString() ?? "",
                            a.TryGetProperty("keywords", out var k) ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new(),
                            a.TryGetProperty("images", out var im)
                                ? im.EnumerateArray().Select(x => new KbImage(x.GetProperty("file").GetString() ?? "", x.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "")).ToList()
                                : new(),
                            sectionTitle, groupTitle, note));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"База знаний {fileName} не загружена: {ex.Message}", "WARNING");
        }

        return articles;
    }

    private void BuildNav(string? query)
    {
        NavPanel.Children.Clear();
        _navButtons.Clear();
        var words = (query ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(KbArticle a)
        {
            if (words.Length == 0)
                return true;
            var hay = (a.Title + " " + a.Text + " " + string.Join(" ", a.Keywords) + " " + a.Section + " " + a.Group + " " + a.RussianSearch).ToLowerInvariant();
            return words.All(hay.Contains);
        }

        var shown = 0;
        foreach (var bySection in _articles.Where(Matches).GroupBy(a => a.Section))
        {
            var sectionName = bySection.Key;
            var isOpen = words.Length > 0 || sectionName == _openSection;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = sectionName,
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrushText"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var countText = new TextBlock
            {
                Text = bySection.Count().ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 11.5,
                Margin = new Thickness(8, 0, 6, 0),
                Foreground = Brush("BrushTextSoft"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(countText, 1);
            header.Children.Add(countText);
            var arrow = new TextBlock
            {
                Text = isOpen ? "▾" : "▸",
                FontSize = 13,
                Foreground = Brush("BrushTextSoft"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(arrow, 2);
            header.Children.Add(arrow);
            var sectionButton = new Button { Content = header, Classes = { "kbSection" } };
            if (isOpen)
                sectionButton.Classes.Add("open");
            sectionButton.Click += (_, _) =>
            {
                // Один открытый раздел: нажали на открытый — свернуть, на другой — открыть его.
                _openSection = _openSection == sectionName ? null : sectionName;
                BuildNav(SearchBox.Text);
            };
            NavPanel.Children.Add(sectionButton);
            if (!isOpen)
            {
                shown += bySection.Count();
                continue;
            }

            string? lastGroup = null;
            foreach (var article in bySection)
            {
                if (words.Length == 0 && article.Group.Length > 0 && article.Group != lastGroup)
                    NavPanel.Children.Add(new TextBlock { Text = article.Group, Classes = { "navGroup" } });
                lastGroup = article.Group;

                var label = new TextBlock { Text = article.Title };
                if (article.Images.Count > 0)
                    label.Text = article.Title + (article.Images.Any(i => i.File.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) ? "  🎬" : "  📷");
                var button = new Button { Content = label, Classes = { "kbItem" } };
                if (ReferenceEquals(article, _current))
                    button.Classes.Add("active");
                var target = article;
                button.Click += (_, _) => ShowArticle(target);
                _navButtons[article.Id] = button;
                NavPanel.Children.Add(button);
                shown++;
            }
        }

        if (shown == 0)
        {
            NavPanel.Children.Add(new TextBlock
            {
                Text = Tr.T("Ничего не найдено. Попробуйте другое слово.", "Эч нерсе табылган жок. Башка сөздү колдонуп көрүңүз.",
                    "Nothing found. Try another word.", "Hiçbir şey bulunamadı. Başka bir kelime deneyin.", "Hech narsa topilmadi. Boshqa so'zni sinab ko'ring."),
                Margin = new Thickness(10, 16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrushTextSoft"),
            });
        }
    }

    private void ShowArticle(KbArticle article)
    {
        _current = article;
        if (_openSection != article.Section)
        {
            // Статья открыта из другого раздела (старт, ссылка) — раскрываем её раздел.
            _openSection = article.Section;
            BuildNav(SearchBox.Text);
        }
        foreach (var (id, button) in _navButtons)
            button.Classes.Set("active", id == article.Id);

        var panel = ArticlePanel;
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = article.Group.Length > 0 ? $"{article.Section} · {article.Group}" : article.Section,
            FontSize = 12.5,
            Foreground = Brush("BrushTextSoft"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = article.Title,
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushText"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (article.Note.Length > 0)
            panel.Children.Add(Callout("ℹ", article.Note, "BrushAccentSoft"));

        foreach (var raw in article.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var step = StepPattern.Match(line);
            if (step.Success)
            {
                panel.Children.Add(StepRow(step.Groups[1].Value, step.Groups[2].Value));
            }
            else if (line.StartsWith("Важно:", StringComparison.Ordinal))
            {
                panel.Children.Add(Callout("!", line["Важно:".Length..].Trim(), "BrushWarningSoft"));
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = line, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush("BrushText") });
            }
        }

        foreach (var image in article.Images)
        {
            var picture = ImageBlock(image);
            if (picture != null)
                panel.Children.Add(picture);
        }

        ArticleScroll.Offset = new Vector(0, 0);
    }

    private Control StepRow(string number, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2, 0, 2) };
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brush("BrushAccent"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number,
                FontWeight = FontWeight.Bold,
                FontSize = 13,
                Foreground = Brush("BrushAccentForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(badge);
        var body = new TextBlock
        {
            Text = text,
            FontSize = 14.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushText"),
            Margin = new Thickness(12, 4, 0, 0),
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private Control Callout(string mark, string text, string backgroundKey) =>
        new Border
        {
            Background = Brush(backgroundKey),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = (mark == "!" ? Tr.T("Важно: ", "Маанилүү: ", "Important: ", "Önemli: ", "Muhim: ") : "") + text,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("BrushText"),
            },
        };

    /// <summary>Снимок на языке программы: «kassa-main.ky.png» рядом с «kassa-main.png»; своего нет —
    /// русский (2026-09-27, «скрины у тебя на русском, их язык тоже поменяй»).</summary>
    private static string LocalizedAsset(string file)
    {
        var language = Tr.T("ru", "ky", "en", "tr", "uz");
        if (language == "ru")
            return file;
        var candidate = Path.GetFileNameWithoutExtension(file) + "." + language + Path.GetExtension(file);
        try
        {
            return AssetLoader.Exists(new Uri(AssetRoot + candidate)) ? candidate : file;
        }
        catch
        {
            return file;
        }
    }

    /// <summary>Кадры анимации GIF (обучающие «гифки»): Avalonia сама GIF не проигрывает, поэтому
    /// кадры раскладываются через SkiaSharp и сменяются таймером. Кадры в файлах — полные (без
    /// наложения на предыдущий), так их сохраняет наш сборщик анимаций.</summary>
    private static List<(Bitmap Frame, TimeSpan Delay)> ReadGifFrames(Stream stream)
    {
        var frames = new List<(Bitmap, TimeSpan)>();
        using var codec = SkiaSharp.SKCodec.Create(stream);
        if (codec == null)
            return frames;
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using var buffer = new SkiaSharp.SKBitmap(info);
        var count = Math.Max(1, codec.FrameCount);
        for (var i = 0; i < count; i++)
        {
            var required = codec.FrameCount > 0 ? codec.FrameInfo[i].RequiredFrame : -1;
            codec.GetPixels(info, buffer.GetPixels(), new SkiaSharp.SKCodecOptions(i, required));
            using var image = SkiaSharp.SKImage.FromBitmap(buffer);
            using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var pngStream = png.AsStream();
            var delay = codec.FrameCount > 0 ? codec.FrameInfo[i].Duration : 0;
            frames.Add((new Bitmap(pngStream), TimeSpan.FromMilliseconds(Math.Max(delay, 300))));
        }

        return frames;
    }

    /// <summary>Снимок окна с подписью. Нажатие — во всю ширину статьи и без ограничения высоты.
    /// GIF проигрывается по кругу, пока статья открыта.</summary>
    private Control? ImageBlock(KbImage image)
    {
        var file = LocalizedAsset(image.File);
        Bitmap bitmap;
        List<(Bitmap Frame, TimeSpan Delay)>? frames = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetRoot + file));
            if (file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                frames = ReadGifFrames(stream);
                if (frames.Count == 0)
                    return null;
                bitmap = frames[0].Frame;
            }
            else
            {
                bitmap = new Bitmap(stream);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"База знаний: нет картинки {file}: {ex.Message}", "WARNING");
            return null;
        }

        const double compactHeight = 520;
        var picture = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxHeight = compactHeight,
            MaxWidth = bitmap.Size.Width,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(picture, Tr.T("Нажмите, чтобы увеличить", "Чоңойтуу үчүн басыңыз", "Click to enlarge", "Büyütmek için tıklayın", "Kattalashtirish uchun bosing"));
        picture.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(picture).Properties.IsLeftButtonPressed)
                return;
            picture.MaxHeight = double.IsPositiveInfinity(picture.MaxHeight) ? compactHeight : double.PositiveInfinity;
        };

        if (frames is { Count: > 1 })
        {
            var index = 0;
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = frames[0].Delay };
            timer.Tick += (_, _) =>
            {
                index = (index + 1) % frames.Count;
                picture.Source = frames[index].Frame;
                timer.Interval = frames[index].Delay;
            };
            picture.AttachedToVisualTree += (_, _) => timer.Start();
            picture.DetachedFromVisualTree += (_, _) => timer.Stop();
        }

        var frame = new Border
        {
            BorderBrush = Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = picture,
        };

        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 6) };
        stack.Children.Add(frame);
        if (image.Caption.Length > 0)
            stack.Children.Add(new TextBlock { Text = image.Caption, FontSize = 12.5, Foreground = Brush("BrushTextSoft"), TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): значок, название и
    /// «Закрыть» уже не нужны — название стоит над разделом. Пояснение, что здесь вопросы и обучение
    /// со снимками, остаётся узкой строкой над поиском и статьёй.</summary>
    public void AsOwnerSection()
    {
        TitleIcon.IsVisible = false;
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        if (SubtitleText.Parent is Control subtitlePanel)
            subtitlePanel.Margin = new Avalonia.Thickness(0);
        WindowHeaderBorder.Padding = new Avalonia.Thickness(24, 8);
    }
}
