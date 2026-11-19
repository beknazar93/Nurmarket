using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-11-19 (владелец, п. 6): слой «CoachMark» поверх окна кассы. Затемняет всё, кроме «окна» вокруг нужного элемента (его находит окно кассы по ключу шага),
/// рисует рамку-подсветку, выноску со стрелкой, счётчик шагов и кнопки «Назад / Пропустить / Далее / Закончить позже».
/// Клики проходят СКВОЗЬ «окно» — затемнение собрано из четырёх прямоугольников вокруг него, поэтому подсвеченный элемент нажимается как обычно (мышь и тач).
/// Положение пересчитывается при смене размера окна, масштаба и раскладки (опрос раз в 250 мс, пока слой виден). Что считать пройденным шагом — решает <see cref="CoachTourEngine"/>.</summary>
public sealed class CoachMarkOverlay : Canvas
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private const double HoleGap = 6, ArrowLen = 10, CardGap = 14, Edge = 8;

    private readonly CoachTourEngine _engine;
    private readonly Func<string, Control?> _resolveTarget;
    private readonly Rectangle[] _dims = new Rectangle[4];
    private readonly Border _highlight;
    private readonly Border _card = new() { Name = "CoachCard", BorderThickness = new Thickness(1.5) };
    private readonly Polygon _arrow = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Rect? _lastHole;
    private Size _lastSize;

    /// <summary>Кнопка действия шага (ключ из <see cref="CoachAction"/>): мастер сфер, принтера, весов…</summary>
    public event Action<string>? ActionRequested;
    public event Action? QuitRequested;
    public event Action? StartWorkRequested;
    public event Action? OpenHelpRequested;

    /// <summary>Положение «окна» подсветки (для тестов): null — подсвечивать нечего (карточка по центру).</summary>
    public Rect? HoleRect { get; private set; }

    /// <summary>Положение карточки-выноски (для тестов).</summary>
    public Rect CardRect { get; private set; }

    public CoachMarkOverlay(CoachTourEngine engine, Func<string, Control?> resolveTarget)
    {
        _engine = engine;
        _resolveTarget = resolveTarget;
        Name = "CoachMarkOverlay";
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        ZIndex = 300;
        for (var i = 0; i < _dims.Length; i++)
        {
            _dims[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), Name = "CoachDim" + i };
            Children.Add(_dims[i]);
        }
        _highlight = new Border { Name = "CoachHighlight", BorderThickness = new Thickness(3), IsHitTestVisible = false };
        _highlight.Bind(Border.BorderBrushProperty, _highlight.GetResourceObservable("BrushAccent"));
        _highlight.Bind(Border.CornerRadiusProperty, _highlight.GetResourceObservable("NurRadiusControl"));
        _arrow.Bind(Shape.FillProperty, _arrow.GetResourceObservable("BrushAccent"));
        _card.Bind(Border.BackgroundProperty, _card.GetResourceObservable("BrushPanelElevated"));
        _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("BrushAccent"));
        _card.Bind(Border.CornerRadiusProperty, _card.GetResourceObservable("NurRadiusCard"));
        Children.Add(_highlight);
        Children.Add(_arrow);
        Children.Add(_card);
        _engine.Changed += OnEngineChanged;
        _poll.Tick += (_, _) => RelayoutIfMoved();
        AttachedToVisualTree += (_, _) => { _poll.Start(); Dispatcher.UIThread.Post(Rebuild); };
        DetachedFromVisualTree += (_, _) => _poll.Stop();
        Tr.LanguageChanged += OnEngineChanged;
        DetachedFromVisualTree += (_, _) => { _engine.Changed -= OnEngineChanged; Tr.LanguageChanged -= OnEngineChanged; };
        SizeChanged += (_, _) => Layout();
        Rebuild();
    }

    private string? _note;

    private void OnEngineChanged()
    {
        _note = null;
        Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>Итог действия шага («Принтер чеков подключён.») — зелёной строкой под кнопками; null — убрать.</summary>
    public void SetNote(string? note)
    {
        _note = string.IsNullOrWhiteSpace(note) ? null : note;
        Rebuild();
    }

    /// <summary>Перестроить карточку под текущий шаг (или под экран «Обучение завершено!») и расставить всё по местам.</summary>
    public void Rebuild()
    {
        _card.Child = _engine.IsFinished ? BuildDoneCard() : BuildStepCard(_engine.Current);
        Layout();
    }

    // ── карточка шага ───────────────────────────────────────────────────────────────────────────────────

    private Control BuildStepCard(CoachStep step)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        var counter = Text("CoachCounter", CoachTour.StepCounter(_engine.Index, _engine.Count), "NurFontCaption", soft: true, bold: true);
        var title = Text("CoachTitle", step.Title(), "NurFontTitle", bold: true);
        var text = Text("CoachText", step.Text(), "NurFontBody");
        panel.Children.Add(counter);
        panel.Children.Add(title);
        panel.Children.Add(text);
        if (step.Actions is { Count: > 0 })
        {
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var action in step.Actions)
            {
                var key = action.Key;
                actions.Children.Add(MakeButton("CoachAction_" + key, action.Label(), "nur-secondary", () => ActionRequested?.Invoke(key)));
            }
            panel.Children.Add(actions);
        }
        if (_note is not null)
        {
            var note = Text("CoachNote", "✓ " + _note, "NurFontBody", bold: true);
            note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushUiStatusOk"));
            panel.Children.Add(note);
        }
        var manual = step.Completion == CoachCondition.Manual;
        if (!manual)
        {
            var doIt = Text("CoachDoIt", T("Сделайте это — шаг засчитается сам.", "Муну жасаңыз — кадам өзү эсептелет.", "Do it — the step counts by itself.",
                "Bunu yapın — adım kendiliğinden sayılır.", "Buni bajaring — qadam o'zi hisoblanadi."), "NurFontCaption", bold: true);
            doIt.Bind(TextBlock.ForegroundProperty, doIt.GetResourceObservable("BrushAccent"));
            panel.Children.Add(doIt);
        }
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(MakeButton("CoachQuit", CoachTour.QuitText, "nur-ghost", () => QuitRequested?.Invoke()));
        if (_engine.Index > 0)
            footer.Children.Add(MakeButton("CoachBack", CoachTour.BackText, "nur-secondary", _engine.Back));
        if (manual)
            footer.Children.Add(MakeButton("CoachNext", _engine.Index == _engine.Count - 1 ? CoachTour.FinishText : CoachTour.NextText, "nur-primary", _engine.Next));
        else
            footer.Children.Add(MakeButton("CoachSkip", CoachTour.SkipStepText, "nur-secondary", _engine.Next));
        panel.Children.Add(footer);
        return panel;
    }

    private Control BuildDoneCard()
    {
        var mark = new Border { Name = "CoachDoneMark", Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Center };
        mark.Bind(Border.BackgroundProperty, mark.GetResourceObservable("BrushUiStatusOk"));
        mark.Child = new TextBlock { Text = "✓", FontSize = 38, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var title = Text("CoachDoneTitle", CoachTour.DoneTitle, "NurFontDisplay", bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        var summary = Text("CoachDoneSummary", CoachTour.DoneSummary, "NurFontBody");
        summary.TextAlignment = TextAlignment.Center;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(MakeButton("CoachStartWork", CoachTour.StartWorkText, "nur-primary", () => StartWorkRequested?.Invoke()));
        buttons.Children.Add(MakeButton("CoachAgain", CoachTour.AgainText, "nur-secondary", _engine.Restart));
        buttons.Children.Add(MakeButton("CoachOpenHelp", CoachTour.OpenHelpText, "nur-secondary", () => OpenHelpRequested?.Invoke()));
        return new StackPanel { Spacing = 14, Margin = new Thickness(24), Children = { mark, title, summary, buttons } };
    }

    private static TextBlock Text(string name, string text, string fontKey, bool soft = false, bool bold = false)
    {
        var block = new TextBlock { Name = name, Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable(fontKey));
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
        return block;
    }

    private static Button MakeButton(string name, string text, string cssClass, Action click)
    {
        // Focusable=false: касса не теряет фокус поля сканера, пока кассир жмёт кнопки обучения.
        var button = new Button { Name = name, Content = text, Focusable = false, Margin = new Thickness(0, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Bind(Layoutable.MinHeightProperty, button.GetResourceObservable("NurHeightControl"));
        button.Classes.Add(cssClass);
        button.Click += (_, _) => click();
        return button;
    }

    // ── положение «окна», выноски и стрелки ─────────────────────────────────────────────────────────────

    private void RelayoutIfMoved()
    {
        if (!IsVisible || Bounds.Width < 1)
            return;
        var hole = ComputeHole();
        if (hole != _lastHole || Bounds.Size != _lastSize)
            Layout();
    }

    private Rect? ComputeHole()
    {
        if (_engine.IsFinished || string.IsNullOrEmpty(_engine.Current.TargetKey))
            return null;
        var target = _resolveTarget(_engine.Current.TargetKey);
        if (target is null || !target.IsEffectivelyVisible || target.Bounds.Width < 1 || target.Bounds.Height < 1)
            return null;
        var topLeft = target.TranslatePoint(default, this);
        var bottomRight = target.TranslatePoint(new Point(target.Bounds.Width, target.Bounds.Height), this);
        if (topLeft is null || bottomRight is null)
            return null;
        var rect = new Rect(topLeft.Value, bottomRight.Value).Inflate(HoleGap).Intersect(new Rect(Bounds.Size));
        return rect.Width < 8 || rect.Height < 8 ? null : rect;
    }

    /// <summary>Расставить затемнение, рамку, карточку и стрелку.</summary>
    public void Layout()
    {
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
            return;
        _lastSize = size;
        var hole = ComputeHole();
        _lastHole = hole;
        HoleRect = hole;
        var step = _engine.IsFinished ? null : _engine.Current;
        // Шаг «закройте меню»: кликать по затемнению можно (клик вне меню закрывает его), на остальных шагах затемнение клики не пускает.
        // На шаге «найдите товар» тоже: товар можно нажать плиткой в каталоге, а он вне «окна».
        var dimBlocks = step?.Completion is not (CoachCondition.MenuClosed or CoachCondition.BasketHasLine);
        foreach (var dim in _dims)
            dim.IsHitTestVisible = dimBlocks;
        if (hole is { } h)
        {
            Place(_dims[0], new Rect(0, 0, size.Width, h.Top));
            Place(_dims[1], new Rect(0, h.Bottom, size.Width, size.Height - h.Bottom));
            Place(_dims[2], new Rect(0, h.Top, h.Left, h.Height));
            Place(_dims[3], new Rect(h.Right, h.Top, size.Width - h.Right, h.Height));
            Place(_highlight, h);
            _highlight.IsVisible = true;
        }
        else
        {
            Place(_dims[0], new Rect(size));
            for (var i = 1; i < _dims.Length; i++)
                Place(_dims[i], default);
            _highlight.IsVisible = false;
        }
        var cardWidth = Math.Min(_engine.IsFinished ? 540 : 400, size.Width - 2 * Edge);
        _card.Width = cardWidth;
        _card.Measure(new Size(cardWidth, Math.Max(40, size.Height - 2 * Edge)));
        var cs = _card.DesiredSize;
        var pos = new Point((size.Width - cs.Width) / 2, (size.Height - cs.Height) / 2);
        _arrow.IsVisible = false;
        if (hole is { } hr)
            pos = PlaceCardNear(hr, cs, size);
        SetLeft(_card, pos.X);
        SetTop(_card, pos.Y);
        CardRect = new Rect(pos, cs);
    }

    private Point PlaceCardNear(Rect hole, Size cs, Size size)
    {
        double ClampX(double x) => Math.Clamp(x, Edge, Math.Max(Edge, size.Width - cs.Width - Edge));
        double ClampY(double y) => Math.Clamp(y, Edge, Math.Max(Edge, size.Height - cs.Height - Edge));
        // Порядок: под «окном», над ним, справа, слева; не влезло нигде — внизу экрана поверх (стрелки нет).
        // На шаге «найдите товар» карточка не должна закрывать плитки каталога под полем ввода — ставим её сбоку (справа/слева) раньше, чем под «окном».
        if (_engine.Current.Completion == CoachCondition.BasketHasLine)
        {
            if (hole.Right + CardGap + cs.Width <= size.Width - Edge)
            {
                var p = new Point(hole.Right + CardGap, ClampY(hole.Top));
                ArrowSide(new Point(p.X, Math.Clamp(hole.Center.Y, p.Y + 24, p.Y + cs.Height - 24)), pointsLeft: true);
                return p;
            }
            if (hole.Left - CardGap - cs.Width >= Edge)
            {
                var p = new Point(hole.Left - CardGap - cs.Width, ClampY(hole.Top));
                ArrowSide(new Point(p.X + cs.Width, Math.Clamp(hole.Center.Y, p.Y + 24, p.Y + cs.Height - 24)), pointsLeft: false);
                return p;
            }
        }
        if (hole.Bottom + CardGap + cs.Height <= size.Height - Edge)
        {
            var p = new Point(ClampX(hole.Center.X - cs.Width / 2), hole.Bottom + CardGap);
            Arrow(new Point(Math.Clamp(hole.Center.X, p.X + 24, p.X + cs.Width - 24), p.Y), up: true);
            return p;
        }
        if (hole.Top - CardGap - cs.Height >= Edge)
        {
            var p = new Point(ClampX(hole.Center.X - cs.Width / 2), hole.Top - CardGap - cs.Height);
            Arrow(new Point(Math.Clamp(hole.Center.X, p.X + 24, p.X + cs.Width - 24), p.Y + cs.Height), up: false);
            return p;
        }
        if (hole.Right + CardGap + cs.Width <= size.Width - Edge)
        {
            var p = new Point(hole.Right + CardGap, ClampY(hole.Center.Y - cs.Height / 2));
            ArrowSide(new Point(p.X, Math.Clamp(hole.Center.Y, p.Y + 24, p.Y + cs.Height - 24)), pointsLeft: true);
            return p;
        }
        if (hole.Left - CardGap - cs.Width >= Edge)
        {
            var p = new Point(hole.Left - CardGap - cs.Width, ClampY(hole.Center.Y - cs.Height / 2));
            ArrowSide(new Point(p.X + cs.Width, Math.Clamp(hole.Center.Y, p.Y + 24, p.Y + cs.Height - 24)), pointsLeft: false);
            return p;
        }
        return new Point(ClampX((size.Width - cs.Width) / 2), size.Height - cs.Height - Edge);
    }

    /// <summary>Стрелка над/под карточкой: up — остриё вверх (карточка под «окном»).</summary>
    private void Arrow(Point edgePoint, bool up)
    {
        _arrow.Points = up
            ? [new Point(0, ArrowLen), new Point(ArrowLen, 0), new Point(2 * ArrowLen, ArrowLen)]
            : [new Point(0, 0), new Point(ArrowLen, ArrowLen), new Point(2 * ArrowLen, 0)];
        SetLeft(_arrow, edgePoint.X - ArrowLen);
        SetTop(_arrow, up ? edgePoint.Y - ArrowLen : edgePoint.Y);
        _arrow.IsVisible = true;
    }

    private void ArrowSide(Point edgePoint, bool pointsLeft)
    {
        _arrow.Points = pointsLeft
            ? [new Point(ArrowLen, 0), new Point(0, ArrowLen), new Point(ArrowLen, 2 * ArrowLen)]
            : [new Point(0, 0), new Point(ArrowLen, ArrowLen), new Point(0, 2 * ArrowLen)];
        SetLeft(_arrow, pointsLeft ? edgePoint.X - ArrowLen : edgePoint.X);
        SetTop(_arrow, edgePoint.Y - ArrowLen);
        _arrow.IsVisible = true;
    }

    private static void Place(Control control, Rect rect)
    {
        SetLeft(control, rect.X);
        SetTop(control, rect.Y);
        control.Width = Math.Max(0, rect.Width);
        control.Height = Math.Max(0, rect.Height);
    }
}
