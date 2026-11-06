using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-06, просьба владельца («история неотправленных документов — чеков, накладных, приходов — и в ней возможность ручной отправки»): окно «Неотправленное».
/// Одно место для всего, что ещё не ушло на сервер: чеки продаж, возвраты и оплаты долга без связи, приходы (накладные) в фоне, новые товары, закрытия смен, внесения/изъятия.
/// Список — по времени СОЗДАНИЯ (старые первыми, переключается), у каждой записи статус (Ждёт связи / Отправляется / Ошибка), причина, число попыток, время последней попытки
/// и раскрывающиеся подробности (позиции, оплата). Кнопки: «Отправить сейчас», «Повторить», «Вернуть в приёмку» (приходы), «Убрать из очереди» (владелец/админ, не для чеков продаж),
/// «Скопировать данные», «Отправить в поддержку»; вверху «Отправить всё» (по порядку создания).
/// Устроено как «Приходы в фоне»: не модальное, одно окно на программу, строки создаются один раз и обновляются на месте (кнопки и раскрытые подробности не прыгают);
/// обновление — по событию очередей (не чаще раза в 250 мс) и таймером раз в 2,5 с. Вся логика отправки — в OutboxService (проект Infrastructure).</summary>
public sealed class OutboxWindow : Window
{
    private static OutboxWindow? _instance;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Сколько строк рисуем (остальные — в счётчике): очередь на тысячи чеков не должна вешать окно.</summary>
    private const int MaxRows = 300;

    /// <summary>Открыть окно или вывести уже открытое на передний план.</summary>
    public static Window ShowOrActivate(Window? owner)
    {
        OutboxSetup.EnsureRegistered();
        if (_instance is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }

        var window = new OutboxWindow();
        _instance = window;
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
        return window;
    }

    private sealed class Row
    {
        public string Key = "";
        public OutboxItem Item = null!;
        public Border Card = new();
        public TextBlock Head = new();
        public TextBlock Info = new();
        public TextBlock Status = new();
        public TextBlock Reason = new();
        public TextBlock Hint = new();
        public StackPanel Detail = new() { Spacing = 2, IsVisible = false };
        public Button Send = null!;
        public Button Retry = null!;
        public Button TakeBack = null!;
        public Button Details = null!;
        public Button Copy = null!;
        public Button Support = null!;
        public Button Remove = null!;
        public string StatusBrush = "";
        public string ReasonBrush = "";
        public bool DetailsOpen;
        public bool Busy;
        public string DetailSignature = "";
    }

    private readonly Dictionary<string, Row> _rows = new();
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _counts = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24) };
    private readonly TextBlock _more = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _notice = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), IsVisible = false };
    private readonly TextBlock _noticeText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _noticeAction;
    private readonly ComboBox _kindFilter = new() { MinWidth = 210, Height = 40 };
    private readonly ComboBox _statusFilter = new() { MinWidth = 170, Height = 40 };
    private readonly Button _order;
    private readonly Button _sendAll;
    private readonly Button _openIrregular;
    private readonly Button _openBackground;
    private readonly Button _resetFilter; // 2026-11-06 (Б-7): «Сбросить фильтр», когда записи есть, но фильтр их скрыл
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) }; // 2026-11-06 (В-2): «Данные скопированы.» гаснет сама
    private readonly ModalShell _shell;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _refreshPending;
    private bool _newestFirst;
    private bool _batchBusy;
    private bool _buildingFilters;
    private OutboxAccess _access = new(false, null);
    private Func<Task>? _noticeActionRun;

    private static readonly OutboxKind?[] KindFilters =
    {
        null, OutboxKind.Sale, OutboxKind.Return, OutboxKind.DebtPayment, OutboxKind.Receiving, OutboxKind.NewProduct, OutboxKind.ShiftClose, OutboxKind.CashOperation,
    };

    private static readonly OutboxStatus?[] StatusFilters = { null, OutboxStatus.Waiting, OutboxStatus.Sending, OutboxStatus.Error };

    private OutboxWindow()
    {
        Title = OutboxText.Title;
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };

        _counts.Bind(TextBlock.ForegroundProperty, _counts.GetResourceObservable("BrushText"));
        _empty.Bind(TextBlock.ForegroundProperty, _empty.GetResourceObservable("BrushTextSoft"));
        _more.Bind(TextBlock.ForegroundProperty, _more.GetResourceObservable("BrushTextSoft"));
        _noticeText.Bind(TextBlock.ForegroundProperty, _noticeText.GetResourceObservable("BrushText"));
        _notice.Bind(Border.BackgroundProperty, _notice.GetResourceObservable("BrushSurfaceSubtle"));
        _notice.Bind(Border.BorderBrushProperty, _notice.GetResourceObservable("BrushBorder"));
        _notice.BorderThickness = new Thickness(1);

        _noticeAction = Small(UiKit.Primary(this, T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish")));
        _noticeAction.IsVisible = false;
        _noticeAction.Margin = new Thickness(0, 8, 0, 0);
        _noticeAction.HorizontalAlignment = HorizontalAlignment.Left;
        _noticeAction.Click += async (_, _) =>
        {
            if (_noticeActionRun is { } run)
                await run().ConfigureAwait(true);
        };
        var noticeBody = new StackPanel { Children = { _noticeText, _noticeAction } };
        _notice.Child = noticeBody;

        _order = Small(UiKit.Ghost(this, ""));
        _order.Click += (_, _) => { _newestFirst = !_newestFirst; HideNotice(); ApplyTexts(); Refresh(); };
        _noticeTimer.Tick += (_, _) => HideNotice();
        _resetFilter = Small(UiKit.Ghost(this, ""));
        _resetFilter.IsVisible = false;
        _resetFilter.HorizontalAlignment = HorizontalAlignment.Center;
        _resetFilter.Click += (_, _) =>
        {
            _buildingFilters = true;
            try { _kindFilter.SelectedIndex = 0; _statusFilter.SelectedIndex = 0; }
            finally { _buildingFilters = false; }
            Refresh();
        };
        _sendAll = Small(UiKit.Primary(this, ""));
        _sendAll.Click += async (_, _) => await OnSendAllAsync().ConfigureAwait(true);
        _openIrregular = Small(UiKit.Ghost(this, ""));
        _openIrregular.Click += (_, _) =>
        {
            object? main = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow; // object: на Android окна кассы наследуют своё Window
            if (main is NurMarketKassa.AvaloniaHost.Views.MainKassir.MainWindow kassa)
                kassa.NavigateDeferredReceipts();
        };
        _openBackground = Small(UiKit.Ghost(this, ""));
        _openBackground.Click += (_, _) => BackgroundReceiptsWindow.ShowOrActivate(this);

        _kindFilter.SelectionChanged += (_, _) => { if (!_buildingFilters) Refresh(); };
        _statusFilter.SelectionChanged += (_, _) => { if (!_buildingFilters) Refresh(); };

        var filters = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { _kindFilter, _statusFilter, _order })
        {
            c.Margin = new Thickness(0, 0, 8, 8);
            filters.Children.Add(c);
        }

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _list, MaxHeight = 460 };
        _list.Margin = new Thickness(0, 0, 8, 0);

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(_counts);
        root.Children.Add(filters);
        root.Children.Add(_notice);
        root.Children.Add(_scroll);
        root.Children.Add(_more);
        root.Children.Add(_empty);
        root.Children.Add(_resetFilter);

        var close = UiKit.Ghost(this, T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), isCancel: true);
        close.Click += (_, _) => Close();
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var c in new Control[] { _openBackground, _openIrregular, _sendAll, close })
        {
            c.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(c);
        }

        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = Title,
            Footer = footer,
            Content = root,
        };
        Content = _shell;
        ApplyTexts();

        Opened += (_, _) =>
        {
            OutboxService.Default.Changed += OnChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            _tick.Tick += (_, _) => Refresh();
            _tick.Start();
            FitHeight();
            Refresh();
        };
        Closed += (_, _) =>
        {
            OutboxService.Default.Changed -= OnChanged;
            Tr.LanguageChanged -= OnLanguageChanged;
            _tick.Stop();
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };
    }

    /// <summary>Высота списка по экрану: окно не выше рабочей области (на ноутбуке 768 px и на телефоне).</summary>
    private void FitHeight()
    {
        try
        {
            var area = Screens.ScreenFromWindow(this)?.WorkingArea;
            if (area is { } wa)
                _scroll.MaxHeight = Math.Clamp(wa.Height / Math.Max(1.0, RenderScaling) - 360, 200, 620);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: высота окна не подогнана ({ex.Message}).", "WARNING");
        }
    }

    private static Button Small(Button b)
    {
        b.Height = 40;
        b.FontSize = 13;
        b.Padding = new Thickness(14, 0);
        return b;
    }

    // ------------------------------------------------------------------ тексты

    private void ApplyTexts()
    {
        Title = OutboxText.Title;
        _shell.Title = Title;
        _shell.Subtitle = T("Всё, что ещё не ушло на сервер. Записи уходят сами — здесь их можно отправить вручную.",
            "Серверге али жөнөтүлө элек бардыгы. Жазуулар өзү жөнөтүлөт — бул жерде кол менен жөнөтсө болот.",
            "Everything that has not reached the server yet. Items go by themselves — here you can send them by hand.",
            "Sunucuya henüz ulaşmayan her şey. Kayıtlar kendiliğinden gider — burada elle gönderebilirsiniz.",
            "Serverga hali yetib bormagan hamma narsa. Yozuvlar o'zi ketadi — bu yerda qo'lda yuborish mumkin.");
        _order.Content = _newestFirst
            ? T("Сначала новые", "Адегенде жаңылары", "Newest first", "Önce yeniler", "Avval yangilari")
            : T("Сначала старые", "Адегенде эскилери", "Oldest first", "Önce eskiler", "Avval eskilari");
        _openIrregular.Content = T("Некорректные чеки", "Туура эмес чектер", "Irregular receipts", "Hatalı fişler", "Noto'g'ri cheklar");
        _openBackground.Content = T("Приходы в фоне", "Фондогу кирүүлөр", "Background receivings", "Arka plandaki mal kabulleri", "Fondagi kirimlar");
        _noticeAction.Content = T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish");
        // 2026-11-06 (Б-7): текст пустого списка зависит от того, есть ли записи вообще — см. ApplySnapshot.
        _resetFilter.Content = T("Сбросить фильтр", "Чыпканы тазалоо", "Reset filter", "Filtreyi sıfırla", "Filtrni tozalash");

        var selKind = _kindFilter.SelectedIndex;
        var selStatus = _statusFilter.SelectedIndex;
        _buildingFilters = true;
        try
        {
            _kindFilter.ItemsSource = KindFilters.Select(k => k is null ? T("Все виды", "Бардык түрлөр", "All kinds", "Tüm türler", "Barcha turlar") : OutboxText.KindLabel(k.Value)).ToList();
            _statusFilter.ItemsSource = StatusFilters.Select(s => s is null ? T("Все статусы", "Бардык статустар", "All statuses", "Tüm durumlar", "Barcha holatlar") : OutboxText.StatusLabel(s.Value)).ToList();
            _kindFilter.SelectedIndex = selKind >= 0 ? selKind : 0;
            _statusFilter.SelectedIndex = selStatus >= 0 ? selStatus : 0;
        }
        finally
        {
            _buildingFilters = false;
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTexts();
        _rows.Clear();
        _list.Children.Clear();
        Refresh();
    });

    private void OnChanged() => Dispatcher.UIThread.Post(Schedule);

    private void Schedule()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        DispatcherTimer.RunOnce(() =>
        {
            _refreshPending = false;
            Refresh();
        }, TimeSpan.FromMilliseconds(250));
    }

    // ------------------------------------------------------------------ обновление списка

    private bool _refreshRunning;
    private bool _refreshAgain;

    /// <summary>Перечитать очереди. Чтение (база, файлы, разбор чеков) идёт в фоне, на экран результат ложится уже в потоке окон — окно не «замирает» на большой очереди.
    /// Пока идёт чтение, новые просьбы сливаются в одну.</summary>
    private void Refresh()
    {
        if (!IsVisible)
            return;
        if (_refreshRunning)
        {
            _refreshAgain = true;
            return;
        }
        _refreshRunning = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            do
            {
                _refreshAgain = false;
                OutboxAccess access;
                IReadOnlyList<OutboxItem> all;
                try
                {
                    access = OutboxSetup.CurrentAccess();
                    all = await Task.Run(() => OutboxService.Default.Snapshot(access)).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Окно «Неотправленное»: не удалось прочитать очереди: {ex.Message}", "WARNING");
                    return;
                }
                if (!IsVisible)
                    return;
                _access = access;
                ApplySnapshot(all);
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshRunning = false;
        }
    }

    private void ApplySnapshot(IReadOnlyList<OutboxItem> all)
    {
        var waiting = all.Count(i => i.Status != OutboxStatus.Error);
        var errors = all.Count(i => i.Status == OutboxStatus.Error);
        _counts.Text = T($"Ждут: {waiting} · Ошибок: {errors}", $"Күтүүдө: {waiting} · Каталар: {errors}", $"Waiting: {waiting} · Errors: {errors}", $"Bekleyen: {waiting} · Hata: {errors}", $"Kutmoqda: {waiting} · Xatolar: {errors}");
        _sendAll.Content = T($"Отправить всё ({all.Count(i => i.Status == OutboxStatus.Waiting && i.CanSend)})", $"Баарын жөнөтүү ({all.Count(i => i.Status == OutboxStatus.Waiting && i.CanSend)})",
            $"Send all ({all.Count(i => i.Status == OutboxStatus.Waiting && i.CanSend)})", $"Hepsini gönder ({all.Count(i => i.Status == OutboxStatus.Waiting && i.CanSend)})", $"Hammasini yuborish ({all.Count(i => i.Status == OutboxStatus.Waiting && i.CanSend)})");
        _sendAll.IsEnabled = !_batchBusy && all.Any(i => i.Status == OutboxStatus.Waiting && i.CanSend);

        var kind = _kindFilter.SelectedIndex is >= 0 and var ki && ki < KindFilters.Length ? KindFilters[ki] : null;
        var status = _statusFilter.SelectedIndex is >= 0 and var si && si < StatusFilters.Length ? StatusFilters[si] : null;
        IEnumerable<OutboxItem> view = all;
        if (kind is not null)
            view = view.Where(i => i.Kind == kind);
        if (status is not null)
            view = view.Where(i => i.Status == status);
        var shown = (_newestFirst ? view.OrderByDescending(i => i.CreatedAt) : view.OrderBy(i => i.CreatedAt)).ToList();
        var over = Math.Max(0, shown.Count - MaxRows);
        if (over > 0)
            shown = shown.Take(MaxRows).ToList();

        var keys = new HashSet<string>(shown.Select(i => i.Key));
        foreach (var gone in _rows.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _list.Children.Remove(_rows[gone].Card);
            _rows.Remove(gone);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            var item = shown[i];
            if (!_rows.TryGetValue(item.Key, out var row))
            {
                row = BuildRow(item.Key);
                _rows[item.Key] = row;
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row.Card);
            }
            else if (!ReferenceEquals(_list.Children.ElementAtOrDefault(i), row.Card))
            {
                _list.Children.Remove(row.Card);
                _list.Children.Insert(Math.Min(i, _list.Children.Count), row.Card);
            }
            Apply(row, item);
        }

        _more.IsVisible = over > 0;
        _more.Text = over > 0 ? T($"Показаны первые {MaxRows}, ещё {over} — отправьте эти, и список продолжится.", $"Алгачкы {MaxRows} көрсөтүлдү, дагы {over} — муну жөнөтсөңүз, тизме улантылат.",
            $"The first {MaxRows} are shown, {over} more — send these and the list continues.", $"İlk {MaxRows} gösteriliyor, {over} tane daha — bunları gönderince liste devam eder.", $"Dastlabki {MaxRows} ko'rsatildi, yana {over} ta — bularni yuborsangiz, ro'yxat davom etadi.") : "";
        _empty.IsVisible = shown.Count == 0;
        // 2026-11-06 (Б-7): «всё на сервере» — только когда записей нет вообще; если их скрыл фильтр, так и говорим.
        var hiddenByFilter = shown.Count == 0 && all.Count > 0;
        _empty.Text = OutboxText.EmptyList(hiddenByFilter);
        _resetFilter.IsVisible = hiddenByFilter;
        _scroll.IsVisible = shown.Count > 0;
        _openIrregular.IsVisible = all.Any(i => i.Kind == OutboxKind.Sale);
        _openBackground.IsVisible = all.Any(i => i.Kind == OutboxKind.Receiving);
    }

    // ------------------------------------------------------------------ строка

    private Row BuildRow(string key)
    {
        var row = new Row { Key = key };
        row.Head.FontSize = 15;
        row.Head.FontWeight = FontWeight.SemiBold;
        row.Head.TextWrapping = TextWrapping.Wrap;
        row.Head.Bind(TextBlock.ForegroundProperty, row.Head.GetResourceObservable("BrushText"));
        row.Info.FontSize = 13;
        row.Info.TextWrapping = TextWrapping.Wrap;
        row.Info.Bind(TextBlock.ForegroundProperty, row.Info.GetResourceObservable("BrushTextSoft"));
        row.Status.FontSize = 13;
        row.Status.FontWeight = FontWeight.SemiBold;
        row.Status.TextWrapping = TextWrapping.Wrap;
        row.Reason.FontSize = 13;
        row.Reason.TextWrapping = TextWrapping.Wrap;
        row.Hint.FontSize = 13;
        row.Hint.TextWrapping = TextWrapping.Wrap;
        row.Hint.Bind(TextBlock.ForegroundProperty, row.Hint.GetResourceObservable("BrushWarning"));

        row.Send = Small(UiKit.Primary(this, T("Отправить сейчас", "Азыр жөнөтүү", "Send now", "Şimdi gönder", "Hozir yuborish")));
        row.Retry = Small(UiKit.Primary(this, T("Повторить", "Кайра аракет", "Retry", "Tekrar dene", "Qayta urinish")));
        row.TakeBack = Small(UiKit.Ghost(this, T("Вернуть в приёмку", "Кабыл алууга кайтаруу", "Return to receiving", "Mal kabule geri al", "Qabulga qaytarish")));
        row.Details = Small(UiKit.Ghost(this, T("Подробнее", "Кененирээк", "Details", "Ayrıntılar", "Batafsil")));
        row.Copy = Small(UiKit.Ghost(this, T("Скопировать данные", "Маалыматты көчүрүү", "Copy data", "Verileri kopyala", "Ma'lumotni nusxalash")));
        row.Support = Small(UiKit.Ghost(this, T("Отправить в поддержку", "Колдоого жөнөтүү", "Send to support", "Desteğe gönder", "Qo'llab-quvvatlashga yuborish")));
        row.Remove = Small(UiKit.Ghost(this, T("Убрать из очереди", "Кезектен алып салуу", "Remove from queue", "Kuyruktan kaldır", "Navbatdan olib tashlash")));
        row.Send.Click += async (_, _) => await OnSendAsync(row, retry: false).ConfigureAwait(true);
        row.Retry.Click += async (_, _) => await OnSendAsync(row, retry: true).ConfigureAwait(true);
        row.TakeBack.Click += (_, _) => BackgroundReceiptsWindow.ShowOrActivate(this);
        row.Details.Click += (_, _) => { row.DetailsOpen = !row.DetailsOpen; Apply(row, row.Item); };
        row.Copy.Click += async (_, _) => await OnCopyAsync(row).ConfigureAwait(true);
        row.Support.Click += async (_, _) => await OnSupportAsync(row).ConfigureAwait(true);
        row.Remove.Click += async (_, _) => await OnRemoveAsync(row).ConfigureAwait(true);

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { row.Send, row.Retry, row.TakeBack, row.Details, row.Copy, row.Support, row.Remove })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(b);
        }

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(row.Head);
        body.Children.Add(row.Info);
        body.Children.Add(row.Status);
        body.Children.Add(row.Reason);
        body.Children.Add(row.Hint);
        body.Children.Add(row.Detail);
        body.Children.Add(new Border { Height = 4 });
        body.Children.Add(buttons);

        row.Card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 4), BorderThickness = new Thickness(1), Child = body };
        row.Card.Bind(Border.BackgroundProperty, row.Card.GetResourceObservable("BrushSurfaceSubtle"));
        row.Card.Bind(Border.BorderBrushProperty, row.Card.GetResourceObservable("BrushBorder"));
        return row;
    }

    private void Apply(Row row, OutboxItem item)
    {
        row.Item = item;
        row.Head.Text = item.KindLabel + " · " + item.Title;
        var info = new List<string> { T("создан ", "түзүлгөн ", "created ", "oluşturuldu ", "yaratilgan ") + item.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
            info.Add(item.Subtitle!);
        if (item.Amount is { } amount)
            info.Add(MoneyText.Som(amount));
        if (item.LinesCount is { } n && n > 0)
            info.Add(T($"{n} поз.", $"{n} сап", $"{n} items", $"{n} kalem", $"{n} ta pozitsiya"));
        row.Info.Text = string.Join(" · ", info);

        var statusParts = new List<string> { OutboxText.StatusLabel(item.Status) };
        if (item.Attempts > 0)
            statusParts.Add(T($"попыток: {item.Attempts}", $"аракет: {item.Attempts}", $"attempts: {item.Attempts}", $"deneme: {item.Attempts}", $"urinishlar: {item.Attempts}"));
        if (item.LastAttemptAt is { } last)
            statusParts.Add(T("последняя попытка ", "акыркы аракет ", "last attempt ", "son deneme ", "oxirgi urinish ") + last.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture));
        row.Status.Text = string.Join(" · ", statusParts);
        var brush = row.Busy ? "BrushText" : item.Status switch
        {
            OutboxStatus.Error => "BrushDanger",
            OutboxStatus.Sending => "BrushText",
            _ => "BrushWarning",
        };
        if (row.Busy)
            row.Status.Text = OutboxText.StatusLabel(OutboxStatus.Sending) + "…";
        if (!string.Equals(row.StatusBrush, brush, StringComparison.Ordinal))
        {
            row.StatusBrush = brush;
            row.Status.Bind(TextBlock.ForegroundProperty, row.Status.GetResourceObservable(brush));
        }

        var reason = item.Reason;
        row.Reason.IsVisible = !string.IsNullOrWhiteSpace(reason);
        row.Reason.Text = reason ?? "";
        var reasonBrush = item.Status == OutboxStatus.Error ? "BrushDanger" : "BrushTextSoft";
        if (!string.Equals(row.ReasonBrush, reasonBrush, StringComparison.Ordinal))
        {
            row.ReasonBrush = reasonBrush;
            row.Reason.Bind(TextBlock.ForegroundProperty, row.Reason.GetResourceObservable(reasonBrush));
        }
        row.Hint.IsVisible = !string.IsNullOrWhiteSpace(item.Hint);
        row.Hint.Text = item.Hint ?? "";

        var busy = row.Busy || item.Status == OutboxStatus.Sending || _batchBusy;
        row.Send.IsVisible = item.Status == OutboxStatus.Waiting && item.CanSend;
        row.Send.IsEnabled = !busy;
        row.Retry.IsVisible = item.Status == OutboxStatus.Error && item.CanRetry;
        row.Retry.IsEnabled = !busy;
        row.TakeBack.IsVisible = item.CanTakeBack && item.Kind == OutboxKind.Receiving;
        row.Remove.IsVisible = item.CanRemove && item.Status == OutboxStatus.Error && item.Kind != OutboxKind.Sale && _access.IsAdmin;
        row.Remove.IsEnabled = !busy;
        row.Copy.IsVisible = item.CanCopy;
        row.Support.IsVisible = item.CanSupport && SupportLogService.IsConfigured;

        var hasDetails = item.Lines.Count > 0 || !string.IsNullOrWhiteSpace(item.Details);
        row.Details.IsVisible = hasDetails;
        row.Details.Content = row.DetailsOpen
            ? T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish")
            : T("Подробнее", "Кененирээк", "Details", "Ayrıntılar", "Batafsil");
        row.Detail.IsVisible = hasDetails && row.DetailsOpen;
        if (row.Detail.IsVisible)
            FillDetails(row, item);
    }

    /// <summary>Раскрытая панель пересобирается, только если изменились данные (выделение текста и прокрутка не сбрасываются каждые 2,5 с).</summary>
    private void FillDetails(Row row, OutboxItem item)
    {
        var signature = (item.Details ?? "") + "|" + string.Join(";", item.Lines.Select(l => l.Name + l.Quantity + l.Amount + l.Note));
        if (signature == row.DetailSignature && row.Detail.Children.Count > 0)
            return;
        row.DetailSignature = signature;
        row.Detail.Children.Clear();
        if (!string.IsNullOrWhiteSpace(item.Details))
            row.Detail.Children.Add(DetailLine(item.Details!, bold: true));
        foreach (var l in item.Lines.Take(200))
            row.Detail.Children.Add(DetailLine($"{l.Name} — {l.Quantity} · {LineMoney(l.Amount)}" + (string.IsNullOrWhiteSpace(l.Note) ? "" : " [" + OutboxText.RawStatus(l.Note) + "]"), bold: false)); // 2026-11-06 (В-2): «95,00 сом» и статус простыми словами
        if (item.Lines.Count > 200)
            row.Detail.Children.Add(DetailLine("… " + (item.Lines.Count - 200), bold: false));
    }

    private static TextBlock DetailLine(string text, bool bold)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
        return tb;
    }

    // ------------------------------------------------------------------ сообщения

    private void ShowNotice(OutboxSendResult result) => ShowNotice(result.Message, result.Kind);

    /// <summary>2026-11-06 (В-2): сумма строки подробностей — как везде («95,00 сом»); источники отдают её числом с точкой.</summary>
    private static string LineMoney(string amount) =>
        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? MoneyText.Som(value) : amount;

    private void HideNotice()
    {
        _noticeTimer.Stop();
        _notice.IsVisible = false;
        _noticeAction.IsVisible = false;
    }

    private void ShowNotice(string message, OutboxSendKind? kind, bool autoHide = false)
    {
        _noticeTimer.Stop();
        if (autoHide)
            _noticeTimer.Start(); // 2026-11-06 (В-2): короткое «Данные скопированы.» гаснет через 3 с
        _noticeText.Text = message;
        var signIn = kind == OutboxSendKind.SessionExpired;
        _noticeAction.IsVisible = signIn;
        _noticeActionRun = signIn ? OutboxSetup.AskSignInAsync : null;
        _notice.IsVisible = !string.IsNullOrWhiteSpace(message);
        var danger = kind is OutboxSendKind.Rejected or OutboxSendKind.NoConnection or OutboxSendKind.SessionExpired or OutboxSendKind.Forbidden;
        _noticeText.Bind(TextBlock.ForegroundProperty, _noticeText.GetResourceObservable(danger ? "BrushDanger" : "BrushText"));
        if (signIn)
            _ = OutboxSetup.AskSignInAsync(); // 2026-11-06: сессия недействительна — сразу «Войдите снова» с кнопкой «Войти», а не серия ошибок
    }

    // ------------------------------------------------------------------ действия

    private async Task OnSendAsync(Row row, bool retry)
    {
        if (row.Busy)
            return;
        row.Busy = true;
        Apply(row, row.Item);
        try
        {
            var result = retry
                ? await OutboxService.Default.RetryAsync(row.Key, _access).ConfigureAwait(true)
                : await OutboxService.Default.SendNowAsync(row.Key, _access).ConfigureAwait(true);
            ShowNotice(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: «Отправить сейчас» не удалось: {ex}", "WARNING");
            ShowNotice(ServerOutageMonitor.FriendlyError(ex), OutboxSendKind.Rejected);
        }
        finally
        {
            row.Busy = false;
            Refresh();
        }
    }

    private async Task OnSendAllAsync()
    {
        if (_batchBusy)
            return;
        _batchBusy = true;
        _sendAll.IsEnabled = false;
        foreach (var r in _rows.Values)
            Apply(r, r.Item);
        try
        {
            var result = await OutboxService.Default.SendAllAsync(_access).ConfigureAwait(true);
            ShowNotice(result.Message, result.StoppedBy ?? (result.Rejected > 0 ? OutboxSendKind.Rejected : OutboxSendKind.Sent));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: «Отправить всё» не удалось: {ex}", "WARNING");
            ShowNotice(ServerOutageMonitor.FriendlyError(ex), OutboxSendKind.Rejected);
        }
        finally
        {
            _batchBusy = false;
            Refresh();
        }
    }

    private async Task OnRemoveAsync(Row row)
    {
        var item = row.Item;
        // Для чека продажи кнопки нет вовсе; сервис всё равно откажет.
        var detail = $"«{item.KindLabel}» {item.Title}" + (item.Amount is { } a ? " — " + MoneyText.Som(a) : "") + (string.IsNullOrWhiteSpace(item.Reason) ? "" : "\n" + item.Reason);
        var ok = await PosConfirmDialog.ShowModalAsync(this,
            T("Убрать из очереди", "Кезектен алып салуу", "Remove from queue", "Kuyruktan kaldır", "Navbatdan olib tashlash"),
            OutboxText.RemoveConfirm + "\n\n" + detail,
            T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger).ConfigureAwait(true);
        if (!ok)
            return;
        var result = OutboxService.Default.Remove(row.Key, _access);
        ShowNotice(result.Message, result.Done ? OutboxSendKind.Sent : OutboxSendKind.Rejected);
        Refresh();
    }

    private async Task OnCopyAsync(Row row)
    {
        var text = OutboxService.Default.Export(row.Key, _access);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowNotice(OutboxText.NotFound, OutboxSendKind.NotFound);
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            ShowNotice(T("Данные скопированы.", "Маалыматтар көчүрүлдү.", "Data copied.", "Veriler kopyalandı.", "Ma'lumotlar nusxalandi."), OutboxSendKind.Sent, autoHide: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: данные не скопированы: {ex.Message}", "WARNING");
            ShowNotice(T("Не удалось скопировать данные.", "Маалыматты көчүрүү мүмкүн болгон жок.", "Could not copy the data.", "Veriler kopyalanamadı.", "Ma'lumotni nusxalab bo'lmadi."), OutboxSendKind.Rejected);
        }
    }

    private async Task OnSupportAsync(Row row)
    {
        var text = OutboxService.Default.Export(row.Key, _access);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowNotice(OutboxText.NotFound, OutboxSendKind.NotFound);
            return;
        }
        row.Support.IsEnabled = false;
        try
        {
            // Заголовок-пояснение + начало данных записи; полный журнал кассы уходит файлом (SupportLogService).
            var note = T("Неотправленный чек: ", "Жөнөтүлбөгөн чек: ", "Unsent receipt: ", "Gönderilmemiş fiş: ", "Yuborilmagan chek: ") + (text!.Length > 700 ? text[..700] + "…" : text);
            var error = await SupportLogService.SendLogsAsync(note).ConfigureAwait(true);
            ShowNotice(error is null
                ? T("Отправлено в поддержку.", "Колдоого жөнөтүлдү.", "Sent to support.", "Desteğe gönderildi.", "Qo'llab-quvvatlashga yuborildi.")
                : error, error is null ? OutboxSendKind.Sent : OutboxSendKind.Rejected);
        }
        finally
        {
            row.Support.IsEnabled = true;
        }
    }
}
