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
/// </summary>
public partial class KnowledgeBaseWindow : Window
{
    private const string AssetRoot = "avares://NurMarketKassa.Avalonia/Assets/kb/";
    private static readonly Regex StepPattern = new(@"^Шаг\s+(\d+)\.\s*(.*)$", RegexOptions.CultureInvariant);

    private sealed record KbImage(string File, string Caption);
    private sealed record KbArticle(string Id, string Title, string Text, List<string> Keywords, List<KbImage> Images, string Section, string Group, string Note);

    private readonly List<KbArticle> _articles = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private KbArticle? _current;

    public KnowledgeBaseWindow()
    {
        InitializeComponent();
        SubtitleText.Text = Tr.T(
            "Вопросы и пошаговое обучение со скриншотами кассы и программы владельца",
            "Суроолор жана кассанын, ээсинин программасынын скриншоттору менен кадам-кадам окутуу",
            "Questions and step-by-step training with screenshots of the register and owner program",
            "Kasa ve sahip programının ekran görüntüleriyle sorular ve adım adım eğitim",
            "Kassa va ega dasturining skrinshotlari bilan savollar va bosqichma-bosqich o'qitish");
        SearchBox.Watermark = Tr.T("Поиск: например, «возврат» или «весы»", "Издөө: мисалы, «кайтаруу» же «тараза»",
            "Search: e.g. “return” or “scale”", "Ara: örneğin «iade» veya «terazi»", "Qidiruv: masalan, «qaytarish» yoki «tarozi»");
        SearchBox.TextChanged += (_, _) => BuildNav(SearchBox.Text);

        LoadArticles();
        BuildNav(null);
        if (_articles.Count > 0)
            ShowArticle(_articles.FirstOrDefault(a => a.Title == "Как создать продажу") ?? _articles[0]);
    }

    private void LoadArticles()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetRoot + "kb.json"));
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
                        _articles.Add(new KbArticle(
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
            PosLogger.Log($"База знаний не загружена: {ex.Message}", "WARNING");
        }
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
            var hay = (a.Title + " " + a.Text + " " + string.Join(" ", a.Keywords) + " " + a.Section + " " + a.Group).ToLowerInvariant();
            return words.All(hay.Contains);
        }

        var shown = 0;
        foreach (var bySection in _articles.Where(Matches).GroupBy(a => a.Section))
        {
            NavPanel.Children.Add(new TextBlock { Text = bySection.Key.ToUpperInvariant(), Classes = { "navSection" } });
            string? lastGroup = null;
            foreach (var article in bySection)
            {
                if (words.Length == 0 && article.Group.Length > 0 && article.Group != lastGroup)
                    NavPanel.Children.Add(new TextBlock { Text = article.Group, Classes = { "navGroup" } });
                lastGroup = article.Group;

                var label = new TextBlock { Text = article.Title };
                if (article.Images.Count > 0)
                    label.Text = article.Title + "  📷";
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

    /// <summary>Снимок окна с подписью. Нажатие — во всю ширину статьи и без ограничения высоты.</summary>
    private Control? ImageBlock(KbImage image)
    {
        Bitmap bitmap;
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetRoot + image.File));
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"База знаний: нет картинки {image.File}: {ex.Message}", "WARNING");
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
}
