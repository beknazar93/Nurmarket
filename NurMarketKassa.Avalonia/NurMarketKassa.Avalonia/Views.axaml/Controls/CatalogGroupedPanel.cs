using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-11-27 (просьба владельца по фото кассы магазина: «Добавь линии, разделение по категориям»): панель плиток каталога
/// вместо WrapPanel. Без группировки раскладывает плитки так же, как WrapPanel (слева направо, перенос на новую строку; на Android — по центру).
/// С группировкой (CatalogPanelViewModel.IsGroupedByCategory) перед каждой новой категорией — и в начале страницы — оставляет полосу заголовка
/// на всю ширину: цветная метка категории (тот же цвет, что на шапке плитки, TileTintConverter), название, число товаров и тонкая линия до края.
/// Каждая категория начинается с новой строки. Заголовок рисуется самой панелью (без лишних элементов на каждую группу), не кликабелен.
/// В режиме <see cref="StackItems"/> (список строками) каждая строка — на всю ширину, заголовки те же.
///
/// Плитки — не больше страницы каталога (50), поэтому раскладка дешёвая; заголовки пересчитываются только при новой раскладке (смена страницы,
/// ширины, отбора), а не на каждый кадр.</summary>
public sealed class CatalogGroupedPanel : Panel
{
    public static readonly StyledProperty<bool> StackItemsProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, bool>(nameof(StackItems));

    public static readonly StyledProperty<bool> CenterRowsProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, bool>(nameof(CenterRows));

    public static readonly StyledProperty<double> HeaderHeightProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, double>(nameof(HeaderHeight), 38);

    /// <summary>Отступ слева/справа у заголовка — как поле плитки (Margin="6"), чтобы метка стояла ровно над краем плитки.</summary>
    public static readonly StyledProperty<double> HeaderInsetProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, double>(nameof(HeaderInset), 6);

    /// <summary>Сверху над заголовком второй и следующих групп — воздух, чтобы группы читались отдельно.</summary>
    public static readonly StyledProperty<double> GroupGapProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, double>(nameof(GroupGap), 8);

    /// <summary>Принудительная группировка (тесты, места без модели каталога). null — берётся из модели каталога.</summary>
    public static readonly StyledProperty<bool?> IsGroupedOverrideProperty =
        AvaloniaProperty.Register<CatalogGroupedPanel, bool?>(nameof(IsGroupedOverride));

    static CatalogGroupedPanel()
    {
        AffectsMeasure<CatalogGroupedPanel>(StackItemsProperty, CenterRowsProperty, HeaderHeightProperty, GroupGapProperty, IsGroupedOverrideProperty);
        AffectsArrange<CatalogGroupedPanel>(HeaderInsetProperty);
    }

    public bool StackItems { get => GetValue(StackItemsProperty); set => SetValue(StackItemsProperty, value); }
    public bool CenterRows { get => GetValue(CenterRowsProperty); set => SetValue(CenterRowsProperty, value); }
    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }
    public double HeaderInset { get => GetValue(HeaderInsetProperty); set => SetValue(HeaderInsetProperty, value); }
    public double GroupGap { get => GetValue(GroupGapProperty); set => SetValue(GroupGapProperty, value); }
    public bool? IsGroupedOverride { get => GetValue(IsGroupedOverrideProperty); set => SetValue(IsGroupedOverrideProperty, value); }

    /// <summary>Полоса заголовка группы после раскладки: где она, ключ категории, с какой плитки начинается группа, где текст и обрезан ли он.</summary>
    public sealed record HeaderSlot(Rect Bounds, string Key, int FirstIndex)
    {
        public Rect TitleBounds { get; internal set; }
        public Rect CountBounds { get; internal set; }
        public bool TitleTrimmed { get; internal set; }
        public string Title { get; internal set; } = "";
        public string CountText { get; internal set; } = "";
    }

    private readonly List<HeaderSlot> _headers = new();
    private readonly List<(HeaderSlot Slot, TextLayout? Title, TextLayout? Count, IBrush? Tone)> _drawn = new();
    private CatalogPanelViewModel? _catalog;
    private bool _textDirty = true;
    private IBrush? _textBrushUsed;
    private IBrush? _softBrushUsed;
    private readonly HeaderLayer _layer;

    public CatalogGroupedPanel()
    {
        // Panel.Render запечатан — заголовки рисует отдельный слой. Он лежит ТОЛЬКО среди визуальных детей (последним), не в Children:
        // Children целиком принадлежат списку (контейнеры плиток по индексам), а Panel вставляет их в VisualChildren по тем же индексам — перед слоем.
        _layer = new HeaderLayer(this);
        VisualChildren.Add(_layer);
        // 2026-11-27 (живая проверка 1.17.97, D1): кисти текста копируются в слои текста при подготовке; смена палитры (AccentThemeService кладёт BrushText прямо в ресурсы приложения)
        // ActualThemeVariantChanged не вызывает — таблица, созданная в светлой теме, в тёмной показывала тёмные названия. Теперь заголовки пересобираются при смене ресурсов и кистей.
        ResourcesChanged += (_, _) =>
        {
            _textDirty = true;
            _layer.InvalidateVisual();
        };
    }

    /// <summary>Заголовки последней раскладки (для тестов перекрытия и обрезки).</summary>
    public IReadOnlyList<HeaderSlot> Headers => _headers;

    public bool HasHeaders => _headers.Count > 0;

    private bool IsGrouped => IsGroupedOverride ?? _catalog?.IsGroupedByCategory ?? false;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var next = DataContext switch
        {
            CatalogPanelViewModel c => c,
            MainWindowViewModel m => m.Catalog,
            _ => null,
        };
        if (ReferenceEquals(next, _catalog))
            return;
        if (_catalog is not null)
            _catalog.PropertyChanged -= OnCatalogPropertyChanged;
        _catalog = next;
        if (_catalog is not null && IsAttachedToVisualTree())
            _catalog.PropertyChanged += OnCatalogPropertyChanged;
        InvalidateMeasure();
    }

    private bool IsAttachedToVisualTree() => this.GetVisualRoot() is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_catalog is not null)
        {
            _catalog.PropertyChanged -= OnCatalogPropertyChanged;
            _catalog.PropertyChanged += OnCatalogPropertyChanged;
        }
        ActualThemeVariantChanged += OnThemeChanged;
        _textDirty = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_catalog is not null)
            _catalog.PropertyChanged -= OnCatalogPropertyChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        ClearDrawn();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        PrepareText();
        _layer.InvalidateVisual();
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CatalogPanelViewModel.IsGroupedByCategory):
                InvalidateMeasure();
                break;
            case nameof(CatalogPanelViewModel.CategoryGroupsVersion):
                _textDirty = true;
                InvalidateMeasure();
                InvalidateArrange();
                break;
        }
    }

    // ── Раскладка ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static string KeyOf(Control child) => CatalogCategoryGrouping.KeyOf(child.DataContext as CatalogProductTileVm);

    protected override Size MeasureOverride(Size availableSize)
    {
        var stack = StackItems;
        var childConstraint = stack ? new Size(availableSize.Width, double.PositiveInfinity) : availableSize;
        foreach (var child in Children)
            child.Measure(childConstraint);
        _layer.Measure(availableSize);
        var width = double.IsInfinity(availableSize.Width) ? MaxChildWidth() : availableSize.Width;
        var (size, _) = Layout(width, null);
        return new Size(double.IsInfinity(availableSize.Width) ? size.Width : Math.Min(size.Width, availableSize.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var placed = new List<(Control, Rect)>(Children.Count);
        var (_, headers) = Layout(finalSize.Width, placed);
        foreach (var (child, rect) in placed)
            child.Arrange(rect);
        _headers.Clear();
        _headers.AddRange(headers);
        // Страховка: слой всегда последний визуальный ребёнок (если список когда-нибудь очистит визуальных детей целиком — вернуть его в конец).
        if (VisualChildren.Count == 0 || !ReferenceEquals(VisualChildren[VisualChildren.Count - 1], _layer))
        {
            VisualChildren.Remove(_layer);
            VisualChildren.Add(_layer);
        }
        _layer.Arrange(new Rect(finalSize));
        PrepareText(); // тексты и их места считаются здесь (не на каждый кадр): заголовков на странице единицы
        _layer.InvalidateVisual();
        return finalSize;
    }

    private double MaxChildWidth()
    {
        double w = 0;
        foreach (var child in Children)
            w = Math.Max(w, child.DesiredSize.Width);
        return w;
    }

    /// <summary>Общая раскладка для Measure и Arrange: строки плиток и полосы заголовков. <paramref name="placed"/> = null — только размер.</summary>
    private (Size Size, List<HeaderSlot> Headers) Layout(double width, List<(Control, Rect)>? placed)
    {
        var grouped = IsGrouped;
        var stack = StackItems;
        var headerHeight = Math.Max(0, HeaderHeight);
        var headers = new List<HeaderSlot>();
        double y = 0, maxLineWidth = 0;
        var line = new List<(Control Child, Size Size)>();
        double lineWidth = 0, lineHeight = 0;
        string? prevKey = null;
        var index = -1;

        void FlushLine()
        {
            if (line.Count == 0)
                return;
            var x = CenterRows && !stack ? Math.Max(0, (width - lineWidth) / 2) : 0;
            foreach (var (child, size) in line)
            {
                placed?.Add((child, new Rect(x, y, stack ? width : size.Width, size.Height)));
                x += size.Width;
            }
            maxLineWidth = Math.Max(maxLineWidth, stack ? width : lineWidth);
            y += lineHeight;
            line.Clear();
            lineWidth = 0;
            lineHeight = 0;
        }

        foreach (var child in Children)
        {
            index++;
            if (!child.IsVisible)
                continue;
            if (grouped)
            {
                var key = KeyOf(child);
                if (prevKey is null || !CatalogCategoryGrouping.SameGroup(prevKey, key))
                {
                    FlushLine();
                    if (headers.Count > 0)
                        y += Math.Max(0, GroupGap);
                    headers.Add(new HeaderSlot(new Rect(0, y, Math.Max(0, width), headerHeight), key, index));
                    y += headerHeight;
                }
                prevKey = key;
            }

            var size = child.DesiredSize;
            if (stack || (line.Count > 0 && lineWidth + size.Width > width + 0.5))
                FlushLine();
            line.Add((child, size));
            lineWidth += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            if (stack)
                FlushLine();
        }
        FlushLine();
        if (headers.Count > 0)
            maxLineWidth = Math.Max(maxLineWidth, width);
        return (new Size(maxLineWidth, y), headers);
    }

    // ── Рисование заголовков ───────────────────────────────────────────────────────────────────────────────────────

    private void ClearDrawn()
    {
        foreach (var (_, title, count, _) in _drawn)
        {
            title?.Dispose();
            count?.Dispose();
        }
        _drawn.Clear();
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    /// <summary>Тексты заголовков: название (жирное, с многоточием, если не влезает) и число товаров; ширина названия ограничена так, чтобы осталось место числу и короткой линии.</summary>
    private void PrepareText()
    {
        ClearDrawn();
        if (_headers.Count == 0)
            return;
        var family = TextElement.GetFontFamily(this);
        var fontSize = Math.Max(12, TextElement.GetFontSize(this) + 1);
        var titleFace = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        var countFace = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        var text = Brush("BrushText") ?? (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Brushes.Black : Brushes.White); // 2026-11-27: запасной цвет — по теме (чёрный на тёмном делал название невидимым)
        var soft = Brush("BrushTextSoft") ?? Brushes.Gray;
        _textBrushUsed = Brush("BrushText");
        _softBrushUsed = Brush("BrushTextSoft");
        var inset = Math.Max(0, HeaderInset);
        foreach (var slot in _headers)
        {
            var (title, count) = _catalog?.CategoryHeader(slot.Key) ?? (CatalogCategoryGrouping.TitleOf(slot.Key), 0);
            var countText = count > 0 ? count.ToString("N0", CultureInfo.CurrentCulture) : "";
            var barWidth = 4.0;
            var available = slot.Bounds.Width - 2 * inset - barWidth - 10;
            var countLayout = countText.Length > 0
                ? new TextLayout(countText, countFace, fontSize - 2, soft, TextAlignment.Left, TextWrapping.NoWrap)
                : null;
            var countWidth = countLayout is null ? 0 : countLayout.WidthIncludingTrailingWhitespace + 10;
            var titleMax = Math.Max(24, available - countWidth - 24); // 24 — минимум линии справа
            var titleLayout = new TextLayout(title, titleFace, fontSize, text, TextAlignment.Left, TextWrapping.NoWrap,
                TextTrimming.CharacterEllipsis, null, FlowDirection.LeftToRight, titleMax);
            var full = new TextLayout(title, titleFace, fontSize, text, TextAlignment.Left, TextWrapping.NoWrap);
            slot.TitleTrimmed = full.WidthIncludingTrailingWhitespace > titleMax + 0.5;
            full.Dispose();

            var cy = slot.Bounds.Y + slot.Bounds.Height / 2;
            var tx = slot.Bounds.X + inset + barWidth + 10;
            slot.Title = title;
            slot.CountText = countText;
            slot.TitleBounds = new Rect(tx, cy - titleLayout.Height / 2, titleLayout.WidthIncludingTrailingWhitespace, titleLayout.Height);
            slot.CountBounds = countLayout is null
                ? default
                : new Rect(slot.TitleBounds.Right + 10, cy - countLayout.Height / 2, countLayout.WidthIncludingTrailingWhitespace, countLayout.Height);

            // Цвет метки — тот же тон, что у шапки плитки этой категории (насыщенный вариант); «Без категории» — нейтральный.
            IBrush? tone = null;
            if (slot.Key.Length > 0 && slot.FirstIndex >= 0 && slot.FirstIndex < Children.Count)
                tone = TileTintConverter.Instance.Convert(Children[slot.FirstIndex].DataContext, typeof(IBrush), "fg", CultureInfo.CurrentCulture) as IBrush;
            _drawn.Add((slot, titleLayout, countLayout, tone ?? soft));
        }
        _textDirty = false;
    }

    /// <summary>Слой рисования заголовков поверх панели: прозрачный, нажатия не ловит (заголовок не кликабелен — нажатие проходит к плиткам рядом).</summary>
    private sealed class HeaderLayer : Control
    {
        private readonly CatalogGroupedPanel _owner;

        public HeaderLayer(CatalogGroupedPanel owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            Focusable = false;
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context) => _owner.DrawHeaders(context);
    }

    private void DrawHeaders(DrawingContext context)
    {
        if (_headers.Count == 0)
            return;
        if (_textDirty || _drawn.Count != _headers.Count
            || !ReferenceEquals(_textBrushUsed, Brush("BrushText")) || !ReferenceEquals(_softBrushUsed, Brush("BrushTextSoft")))
            PrepareText();
        var line = Brush("BrushBorderStrong") ?? Brush("BrushBorder") ?? Brushes.LightGray;
        var pen = new Pen(line, 1);
        var inset = Math.Max(0, HeaderInset);
        foreach (var (slot, title, count, tone) in _drawn)
        {
            var b = slot.Bounds;
            var cy = Math.Round(b.Y + b.Height / 2);
            // метка категории — вертикальная полоска 4×18 со скруглением
            context.DrawRectangle(tone, null, new RoundedRect(new Rect(b.X + inset, cy - 9, 4, 18), 2));
            title?.Draw(context, slot.TitleBounds.TopLeft);
            count?.Draw(context, slot.CountBounds.TopLeft);
            // тонкая линия-разделитель от текста до правого края
            var lineStart = (count is null ? slot.TitleBounds.Right : slot.CountBounds.Right) + 12;
            var lineEnd = b.Right - inset;
            if (lineEnd - lineStart >= 8)
                context.DrawLine(pen, new Point(lineStart, cy + 0.5), new Point(lineEnd, cy + 0.5));
        }
    }
}
