using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-08 (ответ Умара 08.11, п.39 Б-95): «Отзывы» — отзывы покупателей на товары витрины с модерацией. Раздел группы «Сайт» программы владельца
/// (виден владельцу и админу с услугой «Онлайн витрина»). Фильтр по статусу (новые — со счётчиком pending_count) и по товару, карточка: оценка звёздами, текст,
/// автор, дата; «Одобрить», «Отклонить», «Ответить» (ответ магазина), «Удалить» (с подтверждением). Телефон автора сервер не отдаёт.
/// Окно собрано в коде; цвета — из темы.</summary>
public sealed class SiteReviewsWindow : Window, IOwnerSection
{
    private readonly ShowcaseExtrasApiService _api;
    private readonly StackPanel _statuses = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _productSearch = new() { MinHeight = 44, MinWidth = 220, Padding = new Thickness(12, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _productResults = new();
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false };
    private readonly Button _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private string _filter = ShowcaseExtrasApiService.ReviewPending;
    private string? _productId;
    private string? _productName;
    private int? _pendingCount;
    private List<ShowcaseReview> _reviews = new();
    private bool _offline;
    private bool _busy;
    private bool _loadedOnce;
    private string? _replyOpenId;

    public SiteReviewsWindow()
    {
        Title = Tr.T("Отзывы", "Пикирлер", "Reviews", "Yorumlar", "Sharhlar");
        Width = 1100;
        Height = 760;
        _api = App.GetRequiredService<ShowcaseExtrasApiService>();
        Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));

        _refresh = UiKit.Primary(this, Tr.T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash"));
        _refresh.Click += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        _productSearch.Watermark = Tr.T("Товар: название или код", "Товар: аталышы же коду", "Product: name or code", "Ürün: ad veya kod", "Mahsulot: nomi yoki kodi");
        UiKit.OnTypedDebounced(_productSearch, RenderProductResults);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        var left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _statuses.Margin = new Thickness(0, 0, 12, 6);
        _productSearch.Margin = new Thickness(0, 0, 8, 6);
        left.Children.Add(UiKit.Segment(_statuses));
        left.Children.Add(_productSearch);
        top.Children.Add(left);
        Grid.SetColumn(_refresh, 1);
        _refresh.VerticalAlignment = VerticalAlignment.Top;
        top.Children.Add(_refresh);

        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _status.Margin = new Thickness(0, 4, 0, 8);
        _loading.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable("BrushAccent"));

        var scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 12),
        };
        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        Grid.SetRow(_productResults, 1);
        Grid.SetRow(_loading, 2);
        Grid.SetRow(_status, 3);
        Grid.SetRow(scroll, 4);
        root.Children.Add(top);
        root.Children.Add(_productResults);
        root.Children.Add(_loading);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        Content = root;

        BuildStatuses();
        Opened += (_, _) => _ = ReloadAsync();
        Closed += (_, _) => _lifetime.Cancel();
        EscapeKey.Attach(this);
    }

    public void AsOwnerSection()
    {
        // Окно собрано без своей шапки с названием и «Закрыть» — раздел встраивается как есть.
    }

    // ------------------------------------------------------------------ фильтры

    private void BuildStatuses()
    {
        _statuses.Children.Clear();
        void Add(string key, string text)
        {
            var b = UiKit.Seg(text, key == _filter);
            b.MinHeight = 44;
            b.Click += async (_, _) =>
            {
                _filter = key;
                BuildStatuses();
                await ReloadAsync().ConfigureAwait(true);
            };
            _statuses.Children.Add(b);
        }

        var pending = _pendingCount is > 0 ? $" ({_pendingCount})" : "";
        Add(ShowcaseExtrasApiService.ReviewPending, Tr.T("Новые", "Жаңылар", "New", "Yeni", "Yangi") + pending);
        Add(ShowcaseExtrasApiService.ReviewApproved, Tr.T("Одобренные", "Бекитилген", "Approved", "Onaylanan", "Tasdiqlangan"));
        Add(ShowcaseExtrasApiService.ReviewRejected, Tr.T("Отклонённые", "Четке кагылган", "Rejected", "Reddedilen", "Rad etilgan"));
        Add("", Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"));
    }

    private void RenderProductResults()
    {
        _productResults.Children.Clear();
        if (_productId != null)
        {
            var chosen = UiKit.Chip(this, "✕ " + (_productName ?? _productId), true);
            chosen.Margin = new Thickness(0, 0, 8, 6);
            chosen.Click += async (_, _) =>
            {
                _productId = null;
                _productName = null;
                _productSearch.Text = "";
                RenderProductResults();
                await ReloadAsync().ConfigureAwait(true);
            };
            _productResults.Children.Add(chosen);
            return;
        }

        var text = _productSearch.Text?.Trim() ?? "";
        if (text.Length < 2)
            return;
        List<CatalogProductTileVm> found;
        try
        {
            found = CatalogCacheService.Products
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && SearchMatch.Matches(text, p.Title, p.Barcode, p.Article))
                .OrderBy(p => p.Title).Take(8).ToList();
        }
        catch
        {
            found = new List<CatalogProductTileVm>();
        }

        foreach (var p in found)
        {
            var chip = UiKit.Chip(this, p.Title, false);
            chip.Margin = new Thickness(0, 0, 8, 6);
            chip.Click += async (_, _) =>
            {
                _productId = p.Id;
                _productName = p.Title;
                RenderProductResults();
                await ReloadAsync().ConfigureAwait(true);
            };
            _productResults.Children.Add(chip);
        }
    }

    // ------------------------------------------------------------------ загрузка

    private async Task ReloadAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _loading.IsVisible = true;
        _refresh.IsEnabled = false;
        string? error = null;
        try
        {
            var page = await _api.ListReviewsAsync(_filter, _productId, _lifetime.Token).ConfigureAwait(true);
            _reviews = page.Items.ToList();
            // pending_count считает сервер по всем товарам; без фильтра по товару список «Новые» и есть этот счётчик.
            _pendingCount = page.PendingCount ?? (_filter == ShowcaseExtrasApiService.ReviewPending && _productId == null ? _reviews.Count : _pendingCount);
            _offline = false;
            _loadedOnce = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _offline = SiteOrdersWindow.IsNoConnection(ex);
            error = ErrorText(ex);
            PosLogger.Log($"Отзывы витрины: не загружены: {ex.Message}", "SHOWCASE");
        }
        finally
        {
            _loading.IsVisible = false;
            _refresh.IsEnabled = true;
        }

        BuildStatuses();
        Render(error);
    }

    /// <summary>Понятный текст ошибки: нет доступа (403), конец сессии (401), нет связи, иначе слово сервера.</summary>
    internal static string ErrorText(Exception ex) => ex switch
    {
        ApiException { StatusCode: 401 } => SiteOrdersWindow.SessionEndedText,
        ApiException { StatusCode: 403 } => Tr.T(
            "Нет доступа: отзывы и коллекции видят владелец и администратор с подключённой услугой «Онлайн витрина».",
            "Мүмкүнчүлүк жок: пикирлерди жана коллекцияларды ээси жана «Онлайн витрина» кызматы кошулган администратор гана көрөт.",
            "No access: reviews and collections are available to the owner and an administrator with the “Online showcase” service connected.",
            "Erişim yok: yorumları ve koleksiyonları sahip ve «Çevrimiçi vitrin» hizmeti bağlı yönetici görebilir.",
            "Ruxsat yo'q: sharhlar va kolleksiyalarni egasi va «Onlayn vitrina» xizmati ulangan administrator ko'radi."),
        ApiException { StatusCode: 404 } => Tr.T("Сервер пока не поддерживает этот раздел для вашей компании.", "Сервер бул бөлүмдү сиздин компания үчүн азырынча колдобойт.",
            "The server doesn't support this section for your company yet.", "Sunucu bu bölümü şirketiniz için henüz desteklemiyor.", "Server bu bo'limni kompaniyangiz uchun hali qo'llab-quvvatlamaydi."),
        _ when SiteOrdersWindow.IsNoConnection(ex) => Tr.T("Нет связи с сервером — кнопки временно недоступны.", "Сервер менен байланыш жок — баскычтар убактылуу жеткиликсиз.",
            "No connection to the server — buttons are temporarily unavailable.", "Sunucuyla bağlantı yok — düğmeler geçici olarak kullanılamıyor.", "Server bilan aloqa yo'q — tugmalar vaqtincha mavjud emas."),
        _ => ServerTelegramBotApi.DescribeFields(ex),
    };

    // ------------------------------------------------------------------ отрисовка

    private void Render(string? error)
    {
        _list.Children.Clear();
        _status.Text = error ?? (_loadedOnce
            ? Tr.T($"Отзывов: {_reviews.Count}. Ответ магазина виден покупателям под отзывом на сайте.", $"Пикир: {_reviews.Count}. Дүкөндүн жообу сайттагы пикирдин астында көрүнөт.",
                $"Reviews: {_reviews.Count}. The shop's reply is shown under the review on the website.", $"Yorum: {_reviews.Count}. Mağazanın yanıtı sitede yorumun altında görünür.",
                $"Sharhlar: {_reviews.Count}. Do'kon javobi saytda sharh ostida ko'rinadi.")
            : "");
        if (error != null && !_offline)
            return;
        if (_reviews.Count == 0 && error == null)
        {
            var empty = new TextBlock
            {
                Text = _filter == ShowcaseExtrasApiService.ReviewPending
                    ? Tr.T("Новых отзывов нет.", "Жаңы пикирлер жок.", "No new reviews.", "Yeni yorum yok.", "Yangi sharhlar yo'q.")
                    : Tr.T("Отзывов нет.", "Пикирлер жок.", "No reviews.", "Yorum yok.", "Sharhlar yo'q."),
                FontSize = 14, Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            _list.Children.Add(empty);
            return;
        }

        foreach (var review in _reviews)
            _list.Children.Add(BuildCard(review));
    }

    internal static string Stars(int rating) => new string('★', Math.Clamp(rating, 0, 5)) + new string('☆', 5 - Math.Clamp(rating, 0, 5));

    internal static string StatusText(string status) => status switch
    {
        ShowcaseExtrasApiService.ReviewApproved => Tr.T("Одобрен", "Бекитилген", "Approved", "Onaylandı", "Tasdiqlangan"),
        ShowcaseExtrasApiService.ReviewRejected => Tr.T("Отклонён", "Четке кагылган", "Rejected", "Reddedildi", "Rad etilgan"),
        _ => Tr.T("Ждёт проверки", "Текшерүүнү күтөт", "Awaiting review", "İnceleme bekliyor", "Tekshiruv kutmoqda"),
    };

    private Control BuildCard(ShowcaseReview review)
    {
        var offlineTip = Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q");
        var card = new Border { Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        var stack = new StackPanel { Spacing = 6 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var stars = new TextBlock { Text = Stars(review.Rating), FontSize = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        stars.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushWarning"));
        Avalonia.Automation.AutomationProperties.SetName(stars, Tr.T($"Оценка {review.Rating} из 5", $"Баа {review.Rating} / 5", $"Rating {review.Rating} out of 5", $"Puan {review.Rating} / 5", $"Baho {review.Rating} / 5"));
        head.Children.Add(stars);
        var productName = !string.IsNullOrWhiteSpace(review.ProductName) ? review.ProductName : CatalogCacheService.FindProductById(review.ProductId)?.Title ?? "";
        var who = string.Join(" · ", new[]
        {
            review.AuthorName,
            review.CreatedAt is { } c ? c.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "",
            productName,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var whoText = new TextBlock { Text = who, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        whoText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        Grid.SetColumn(whoText, 1);
        head.Children.Add(whoText);
        var pill = new TextBlock { Text = StatusText(review.Status), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        pill.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumn(pill, 2);
        head.Children.Add(pill);
        stack.Children.Add(head);

        if (review.Text.Length > 0)
        {
            var text = new TextBlock { Text = review.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            stack.Children.Add(text);
        }

        if (review.Reply.Length > 0)
        {
            var reply = new TextBlock
            {
                Text = Tr.T("Ответ магазина: ", "Дүкөндүн жообу: ", "Shop reply: ", "Mağaza yanıtı: ", "Do'kon javobi: ") + review.Reply,
                FontSize = 13, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic,
            };
            reply.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            stack.Children.Add(reply);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        Button Act(Button b, Func<Task> run, bool enabled = true)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Height = 44;
            b.IsEnabled = enabled && !_busy && !_offline;
            if (_offline)
                ToolTip.SetTip(b, offlineTip);
            b.Click += async (_, _) => await run().ConfigureAwait(true);
            actions.Children.Add(b);
            return b;
        }

        if (review.Status != ShowcaseExtrasApiService.ReviewApproved)
            Act(UiKit.Primary(this, Tr.T("Одобрить", "Бекитүү", "Approve", "Onayla", "Tasdiqlash")),
                () => ModerateAsync(review, ShowcaseExtrasApiService.ReviewApproved, review.Reply));
        if (review.Status != ShowcaseExtrasApiService.ReviewRejected)
            Act(UiKit.Ghost(this, Tr.T("Отклонить", "Четке кагуу", "Reject", "Reddet", "Rad etish")),
                () => ModerateAsync(review, ShowcaseExtrasApiService.ReviewRejected, review.Reply));
        Act(UiKit.Ghost(this, review.Reply.Length > 0
                ? Tr.T("Изменить ответ", "Жоопту өзгөртүү", "Edit reply", "Yanıtı düzenle", "Javobni o'zgartirish")
                : Tr.T("Ответить", "Жооп берүү", "Reply", "Yanıtla", "Javob berish")),
            () =>
            {
                _replyOpenId = _replyOpenId == review.Id ? null : review.Id;
                Render(null);
                return Task.CompletedTask;
            });
        Act(UiKit.Danger(this, Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish")), () => DeleteAsync(review));
        stack.Children.Add(actions);

        if (_replyOpenId == review.Id)
            stack.Children.Add(BuildReplyEditor(review));

        card.Child = stack;
        return card;
    }

    private Control BuildReplyEditor(ShowcaseReview review)
    {
        var box = new TextBox { Text = review.Reply, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 1000 };
        box.Watermark = Tr.T("Ответ покупателю (виден на сайте под отзывом)", "Сатып алуучуга жооп (сайтта пикирдин астында көрүнөт)",
            "Reply to the customer (shown under the review on the website)", "Müşteriye yanıt (sitede yorumun altında görünür)", "Xaridorga javob (saytda sharh ostida ko'rinadi)");
        var save = UiKit.Primary(this, Tr.T("Сохранить ответ", "Жоопту сактоо", "Save reply", "Yanıtı kaydet", "Javobni saqlash"));
        save.Height = 44;
        save.IsEnabled = !_busy && !_offline;
        save.Click += async (_, _) =>
        {
            _replyOpenId = null;
            await ModerateAsync(review, review.Status, box.Text?.Trim() ?? "").ConfigureAwait(true);
        };
        var cancel = UiKit.Ghost(this, Tr.T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 44;
        cancel.Click += (_, _) =>
        {
            _replyOpenId = null;
            Render(null);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, cancel } };
        return new StackPanel { Spacing = 8, Children = { box, row } };
    }

    // ------------------------------------------------------------------ действия

    private async Task ModerateAsync(ShowcaseReview review, string status, string? reply)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await _api.ModerateReviewAsync(review.Id, status, reply, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _busy = false;
            PosLogger.Log($"Отзыв {review.Id}: не сохранён: {ex.Message}", "SHOWCASE");
            PosDialogs.Error(this, ErrorText(ex));
            return;
        }

        _busy = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task DeleteAsync(ShowcaseReview review)
    {
        var ok = await PosDialogs.ConfirmYesNoModalAsync(this,
            Tr.T("Удалить отзыв насовсем? Вернуть его будет нельзя.", "Пикирди биротоло өчүрөсүзбү? Кайра калыбына келтирүүгө болбойт.",
                "Delete the review for good? It can't be restored.", "Yorum kalıcı olarak silinsin mi? Geri alınamaz.", "Sharh butunlay o'chirilsinmi? Qaytarib bo'lmaydi."),
            null, Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), null, danger: true, enterCancels: true).ConfigureAwait(true);
        if (!ok || _busy)
            return;
        _busy = true;
        try
        {
            await _api.DeleteReviewAsync(review.Id, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _busy = false;
            PosLogger.Log($"Отзыв {review.Id}: не удалён: {ex.Message}", "SHOWCASE");
            PosDialogs.Error(this, ErrorText(ex));
            return;
        }

        _busy = false;
        await ReloadAsync().ConfigureAwait(true);
    }
}
