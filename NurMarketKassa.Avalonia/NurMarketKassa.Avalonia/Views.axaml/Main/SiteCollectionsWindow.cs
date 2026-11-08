using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-08 (ответ Умара 08.11, п.37 Б-93): «Коллекции» — подборки товаров витрины («Весна», «Подарки»). Раздел группы «Сайт» программы владельца
/// (владелец или админ с услугой «Онлайн витрина»). Список: включить/выключить, изменить, удалить (с подтверждением). Правка: название на 5 языках, описание, сезон,
/// даты показа, обложка (загрузка картинки в showcase/media/, как в редакторе сайта), товары из каталога кассы с порядком стрелками ↑ ↓.
/// Блок «Коллекции» на странице витрины включается в «Редакторе сайта» (раздел страницы collections). Окно собрано в коде.</summary>
public sealed class SiteCollectionsWindow : Window, IOwnerSection
{
    private readonly ShowcaseExtrasApiService _api;
    private readonly NurMarketApiClient _client;
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, IsVisible = false };
    private readonly Button _create;
    private readonly Button _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private List<ShowcaseCollection> _items = new();
    private bool _offline;
    private bool _busy;
    private bool _loadedOnce;

    // состояние правки
    private ShowcaseCollection? _editing;
    private bool _isNew;
    private readonly Dictionary<string, TextBox> _titleBoxes = new();
    private TextBox? _descriptionBox;
    private TextBox? _seasonBox;
    private CalendarDatePicker? _fromPicker;
    private CalendarDatePicker? _toPicker;
    private CheckBox? _activeBox;
    private TextBlock? _coverState;
    private string? _coverId;
    private bool _coverChanged;
    private bool _coverCleared;
    private List<string> _productIds = new();
    private readonly Dictionary<string, string> _productNames = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel? _productsHost;
    private WrapPanel? _pickResults;
    private TextBox? _pickSearch;
    private TextBlock? _editError;

    public SiteCollectionsWindow()
    {
        Title = Tr.T("Коллекции", "Коллекциялар", "Collections", "Koleksiyonlar", "Kolleksiyalar");
        Width = 1100;
        Height = 760;
        _api = App.GetRequiredService<ShowcaseExtrasApiService>();
        _client = App.GetRequiredService<NurMarketApiClient>();
        Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));

        _create = UiKit.Primary(this, Tr.T("Создать коллекцию", "Коллекция түзүү", "New collection", "Yeni koleksiyon", "Kolleksiya yaratish"));
        _refresh = UiKit.Ghost(this, Tr.T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash"));
        _create.Click += (_, _) => OpenEditor(null);
        _refresh.Click += async (_, _) => await ReloadAsync().ConfigureAwait(true);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _refresh, _create } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock
        {
            Text = Tr.T("Подборки товаров для витрины. Блок «Коллекции» на странице включается в «Редакторе сайта».",
                "Витрина үчүн товарлардын топтомдору. «Коллекциялар» блогу «Сайт редакторунда» күйгүзүлөт.",
                "Product selections for the showcase. The “Collections” block on the page is switched on in the “Website editor”.",
                "Vitrin için ürün seçkileri. Sayfadaki «Koleksiyonlar» bloğu «Web sitesi düzenleyici»de açılır.",
                "Vitrina uchun mahsulotlar tanlovi. Sahifadagi «Kolleksiyalar» bloki «Sayt muharriri»da yoqiladi."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        header.Children.Add(hint);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _status.Margin = new Thickness(0, 4, 0, 8);
        _loading.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable("BrushAccent"));
        var scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 12),
        };
        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(_loading, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(scroll, 3);
        root.Children.Add(header);
        root.Children.Add(_loading);
        root.Children.Add(_status);
        root.Children.Add(scroll);
        Content = root;

        Opened += (_, _) => _ = ReloadAsync();
        Closed += (_, _) => _lifetime.Cancel();
        EscapeKey.Attach(this);
    }

    public void AsOwnerSection()
    {
        // Окно без собственной шапки с названием — раздел встраивается как есть.
    }

    // ------------------------------------------------------------------ список

    private async Task ReloadAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _loading.IsVisible = true;
        string? error = null;
        try
        {
            _items = (await _api.ListCollectionsAsync(_lifetime.Token).ConfigureAwait(true)).ToList();
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
            error = SiteReviewsWindow.ErrorText(ex);
            PosLogger.Log($"Коллекции витрины: не загружены: {ex.Message}", "SHOWCASE");
        }
        finally
        {
            _loading.IsVisible = false;
        }

        _editing = null;
        RenderList(error);
    }

    private static string Lang => Tr.T("ru", "ky", "en", "tr", "uz");

    private void RenderList(string? error)
    {
        _body.Children.Clear();
        _create.IsVisible = true;
        _create.IsEnabled = !_offline && error == null || _loadedOnce && !_offline;
        if (_offline)
            ToolTip.SetTip(_create, Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q"));
        _status.Text = error ?? "";
        if (error != null && !_offline)
            return;
        if (_items.Count == 0 && error == null)
        {
            var empty = new TextBlock
            {
                Text = Tr.T("Коллекций пока нет. Нажмите «Создать коллекцию».", "Азырынча коллекциялар жок. «Коллекция түзүү» баскычын басыңыз.",
                    "No collections yet. Press “New collection”.", "Henüz koleksiyon yok. «Yeni koleksiyon»a basın.", "Hozircha kolleksiyalar yo'q. «Kolleksiya yaratish»ni bosing."),
                FontSize = 14, Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            _body.Children.Add(empty);
            return;
        }

        foreach (var item in _items)
            _body.Children.Add(BuildRow(item));
    }

    private Control BuildRow(ShowcaseCollection c)
    {
        var card = new Border { Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        var stack = new StackPanel { Spacing = 4 };
        var title = new TextBlock { Text = c.TitleFor(Lang), FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        stack.Children.Add(title);
        var meta = string.Join(" · ", new[]
        {
            c.IsActive ? Tr.T("включена", "күйгүзүлгөн", "on", "açık", "yoqilgan") : Tr.T("выключена", "өчүрүлгөн", "off", "kapalı", "o'chirilgan"),
            c.Season,
            Tr.T($"товаров: {c.ProductIds.Count}", $"товар: {c.ProductIds.Count}", $"products: {c.ProductIds.Count}", $"ürün: {c.ProductIds.Count}", $"mahsulot: {c.ProductIds.Count}"),
            DatesText(c),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var metaText = new TextBlock { Text = meta, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        metaText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(metaText);

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Add(Button b, Func<Task> run)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Height = 44;
            b.IsEnabled = !_busy && !_offline;
            if (_offline)
                ToolTip.SetTip(b, Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q"));
            b.Click += async (_, _) => await run().ConfigureAwait(true);
            actions.Children.Add(b);
        }

        Add(UiKit.Primary(this, Tr.T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish")), () => { OpenEditor(c); return Task.CompletedTask; });
        Add(UiKit.Ghost(this, c.IsActive ? Tr.T("Выключить", "Өчүрүү", "Turn off", "Kapat", "O'chirish") : Tr.T("Включить", "Күйгүзүү", "Turn on", "Aç", "Yoqish")),
            () => ToggleAsync(c));
        Add(UiKit.Danger(this, Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish")), () => DeleteAsync(c));
        stack.Children.Add(actions);
        card.Child = stack;
        return card;
    }

    private static string DatesText(ShowcaseCollection c)
    {
        if (c.StartsAt == null && c.EndsAt == null)
            return "";
        string D(DateTimeOffset? d) => d?.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "…";
        return Tr.T("показ ", "көрсөтүү ", "shown ", "gösterim ", "ko'rsatish ") + D(c.StartsAt) + " — " + D(c.EndsAt);
    }

    private async Task ToggleAsync(ShowcaseCollection c)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await _api.SetCollectionActiveAsync(c.Id, !c.IsActive, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _busy = false;
            PosDialogs.Error(this, SiteReviewsWindow.ErrorText(ex));
            return;
        }

        _busy = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task DeleteAsync(ShowcaseCollection c)
    {
        var ok = await PosDialogs.ConfirmYesNoModalAsync(this,
            Tr.T($"Удалить коллекцию «{c.TitleFor(Lang)}»? Товары останутся в каталоге.", $"«{c.TitleFor(Lang)}» коллекциясын өчүрөсүзбү? Товарлар каталогто калат.",
                $"Delete the collection “{c.TitleFor(Lang)}”? The products stay in the catalogue.", $"«{c.TitleFor(Lang)}» koleksiyonu silinsin mi? Ürünler katalogda kalır.",
                $"«{c.TitleFor(Lang)}» kolleksiyasi o'chirilsinmi? Mahsulotlar katalogda qoladi."),
            null, Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), null, danger: true, enterCancels: true).ConfigureAwait(true);
        if (!ok || _busy)
            return;
        _busy = true;
        try
        {
            await _api.DeleteCollectionAsync(c.Id, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _busy = false;
            PosDialogs.Error(this, SiteReviewsWindow.ErrorText(ex));
            return;
        }

        _busy = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ правка

    private void OpenEditor(ShowcaseCollection? c)
    {
        _editing = c;
        _isNew = c == null;
        _coverId = c?.CoverId;
        _coverChanged = false;
        _coverCleared = false;
        _productIds = c?.ProductIds.ToList() ?? new List<string>();
        _productNames.Clear();
        if (c != null)
            foreach (var pair in c.ProductNames)
                _productNames[pair.Key] = pair.Value;
        _titleBoxes.Clear();
        _create.IsVisible = false;
        _status.Text = "";
        _body.Children.Clear();

        var head = new TextBlock
        {
            Text = _isNew ? Tr.T("Новая коллекция", "Жаңы коллекция", "New collection", "Yeni koleksiyon", "Yangi kolleksiya") : Tr.T("Коллекция", "Коллекция", "Collection", "Koleksiyon", "Kolleksiya"),
            FontSize = 20, FontWeight = FontWeight.Bold,
        };
        head.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _body.Children.Add(head);

        _body.Children.Add(Label(Tr.T("Название (хотя бы на одном языке)", "Аталышы (жок дегенде бир тилде)", "Title (in at least one language)", "Ad (en az bir dilde)", "Nomi (kamida bitta tilda)")));
        var titles = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
        for (var i = 0; i < ShowcaseExtrasApiService.Languages.Length; i++)
        {
            var lang = ShowcaseExtrasApiService.Languages[i];
            var tag = new TextBlock { Text = lang.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6), MinWidth = 28 };
            tag.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            Grid.SetRow(tag, i);
            var box = new TextBox { MinHeight = 44, Margin = new Thickness(0, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center, Text = c != null && c.Titles.TryGetValue(lang, out var tv) ? tv : "" };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            _titleBoxes[lang] = box;
            titles.Children.Add(tag);
            titles.Children.Add(box);
        }
        _body.Children.Add(titles);

        _body.Children.Add(Label(Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif")));
        _descriptionBox = new TextBox { Text = c?.Description ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        _body.Children.Add(_descriptionBox);

        _body.Children.Add(Label(Tr.T("Сезон (например, «Весна 2027»)", "Мезгил (мисалы, «Жаз 2027»)", "Season (e.g. “Spring 2027”)", "Sezon (ör. «İlkbahar 2027»)", "Mavsum (masalan, «Bahor 2027»)")));
        _seasonBox = new TextBox { Text = c?.Season ?? "", MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
        _body.Children.Add(_seasonBox);

        _body.Children.Add(Label(Tr.T("Даты показа на сайте (пусто — без ограничений)", "Сайтта көрсөтүү күндөрү (бош — чектөөсүз)", "Display dates on the website (empty — no limit)", "Sitede gösterim tarihleri (boş — sınırsız)", "Saytda ko'rsatish sanalari (bo'sh — cheklovsiz)")));
        _fromPicker = new CalendarDatePicker { Width = 150, MinHeight = 44, SelectedDateFormat = CalendarDatePickerFormat.Short, SelectedDate = c?.StartsAt?.LocalDateTime.Date };
        _toPicker = new CalendarDatePicker { Width = 150, MinHeight = 44, SelectedDateFormat = CalendarDatePickerFormat.Short, SelectedDate = c?.EndsAt?.LocalDateTime.Date };
        _body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _fromPicker, new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center }, _toPicker },
        });

        _activeBox = new CheckBox { Content = Tr.T("Показывать на сайте", "Сайтта көрсөтүү", "Show on the website", "Sitede göster", "Saytda ko'rsatish"), IsChecked = c?.IsActive ?? true, Margin = new Thickness(0, 6, 0, 0) };
        _activeBox.Bind(CheckBox.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _body.Children.Add(_activeBox);

        _body.Children.Add(Label(Tr.T("Обложка", "Мукаба", "Cover", "Kapak", "Muqova")));
        _body.Children.Add(BuildCoverRow());

        _body.Children.Add(Label(Tr.T("Товары коллекции (порядок — как на сайте)", "Коллекциядагы товарлар (тартиби — сайттагыдай)", "Collection products (order as on the website)", "Koleksiyon ürünleri (sıra sitedeki gibi)", "Kolleksiya mahsulotlari (tartib saytdagidek)")));
        _productsHost = new StackPanel { Spacing = 4 };
        _body.Children.Add(_productsHost);
        RenderProducts();
        _pickSearch = new TextBox
        {
            MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = Tr.T("Добавить товар: название или код", "Товар кошуу: аталышы же коду", "Add a product: name or code", "Ürün ekle: ad veya kod", "Mahsulot qo'shish: nomi yoki kodi"),
        };
        UiKit.OnTypedDebounced(_pickSearch, RenderPick);
        _pickResults = new WrapPanel();
        _body.Children.Add(_pickSearch);
        _body.Children.Add(_pickResults);

        _editError = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _editError.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        _body.Children.Add(_editError);

        var save = UiKit.Primary(this, Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.IsEnabled = !_offline;
        if (_offline)
            ToolTip.SetTip(save, Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q"));
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        var cancel = UiKit.Ghost(this, Tr.T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => RenderList(null);
        _body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { save, cancel } });
    }

    private TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        l.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return l;
    }

    private Control BuildCoverRow()
    {
        _coverState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        _coverState.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        UpdateCoverState();
        var upload = UiKit.Ghost(this, Tr.T("Загрузить…", "Жүктөө…", "Upload…", "Yükle…", "Yuklash…"));
        var remove = UiKit.Ghost(this, Tr.T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
        remove.Click += (_, _) =>
        {
            _coverId = null;
            _coverChanged = false;
            _coverCleared = true;
            UpdateCoverState();
        };
        upload.Click += async (_, _) =>
        {
            var top = GetTopLevel(this);
            if (top is null)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(Tr.T("Картинки", "Сүрөттөр", "Pictures", "Resimler", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
            }).ConfigureAwait(true);
            var local = await ImportFileBridge.TemporaryPathAsync(files.FirstOrDefault()).ConfigureAwait(true);
            if (local is null)
                return;
            _coverState!.Text = Tr.T("загружаю…", "жүктөлүүдө…", "uploading…", "yükleniyor…", "yuklanmoqda…");
            try
            {
                var data = await _client.UploadFileAsync("api/main/showcase/media/", "file", local, new Dictionary<string, string> { ["kind"] = "cover" }).ConfigureAwait(true);
                var id = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var idv) ? idv.ToString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException(Tr.T("сервер не вернул номер картинки", "сервер сүрөттүн номерин кайтарган жок", "the server returned no picture id", "sunucu resim numarası döndürmedi", "server rasm raqamini qaytarmadi"));
                _coverId = id;
                _coverChanged = true;
                _coverCleared = false;
                UpdateCoverState();
            }
            catch (Exception ex)
            {
                _coverState!.Text = Tr.T("не загрузилась: ", "жүктөлгөн жок: ", "upload failed: ", "yüklenemedi: ", "yuklanmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
                PosLogger.Log($"Коллекции витрины: обложка не загружена ({ex.Message}).", "WARNING");
            }
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { upload, remove, _coverState } };
    }

    private void UpdateCoverState() => _coverState!.Text = string.IsNullOrWhiteSpace(_coverId)
        ? Tr.T("не загружена", "жүктөлгөн эмес", "not uploaded", "yüklenmedi", "yuklanmagan")
        : Tr.T("загружена", "жүктөлгөн", "uploaded", "yüklendi", "yuklangan");

    private string NameOf(string id) =>
        _productNames.TryGetValue(id, out var n) && n.Length > 0 ? n
        : CatalogCacheService.FindProductById(id)?.Title is { Length: > 0 } t ? t
        : id;

    private void RenderProducts()
    {
        _productsHost!.Children.Clear();
        if (_productIds.Count == 0)
        {
            var none = new TextBlock { Text = Tr.T("Товаров пока нет — добавьте ниже.", "Товарлар азырынча жок — төмөндөн кошуңуз.", "No products yet — add them below.", "Henüz ürün yok — aşağıdan ekleyin.", "Hozircha mahsulotlar yo'q — pastdan qo'shing."), FontSize = 13 };
            none.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            _productsHost.Children.Add(none);
            return;
        }

        for (var i = 0; i < _productIds.Count; i++)
        {
            var index = i;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
            var num = new TextBlock { Text = $"{i + 1}.", MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
            num.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            var name = new TextBlock { Text = NameOf(_productIds[i]), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            Grid.SetColumn(name, 1);
            row.Children.Add(num);
            row.Children.Add(name);
            Button Small(string text, string tip, int column, bool enabled, Action act)
            {
                var b = new Button { Content = text, MinWidth = 44, MinHeight = 44, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled, HorizontalContentAlignment = HorizontalAlignment.Center };
                b.Classes.Add("sec-outline");
                Avalonia.Automation.AutomationProperties.SetName(b, tip);
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => act();
                Grid.SetColumn(b, column);
                row.Children.Add(b);
                return b;
            }
            Small("↑", Tr.T("Выше", "Жогору", "Up", "Yukarı", "Yuqoriga"), 2, index > 0, () => MoveProduct(index, -1));
            Small("↓", Tr.T("Ниже", "Төмөн", "Down", "Aşağı", "Pastga"), 3, index < _productIds.Count - 1, () => MoveProduct(index, 1));
            Small("✕", Tr.T("Убрать из коллекции", "Коллекциядан алып салуу", "Remove from the collection", "Koleksiyondan çıkar", "Kolleksiyadan olib tashlash"), 4, true, () =>
            {
                _productIds.RemoveAt(index);
                RenderProducts();
            });
            _productsHost.Children.Add(row);
        }
    }

    private void MoveProduct(int index, int delta)
    {
        ShowcaseExtrasApiService.MoveId(_productIds, index, delta);
        RenderProducts();
    }

    private void RenderPick()
    {
        _pickResults!.Children.Clear();
        var text = _pickSearch!.Text?.Trim() ?? "";
        if (text.Length < 2)
            return;
        List<CatalogProductTileVm> found;
        try
        {
            found = CatalogCacheService.Products
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && !_productIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase) && SearchMatch.Matches(text, p.Title, p.Barcode, p.Article))
                .OrderBy(p => p.Title).Take(10).ToList();
        }
        catch
        {
            found = new List<CatalogProductTileVm>();
        }

        foreach (var p in found)
        {
            var chip = UiKit.Chip(this, "+ " + p.Title, false);
            chip.Margin = new Thickness(0, 0, 8, 6);
            chip.Click += (_, _) =>
            {
                _productIds.Add(p.Id);
                _productNames[p.Id] = p.Title;
                RenderProducts();
                RenderPick();
            };
            _pickResults.Children.Add(chip);
        }
    }

    private async Task SaveAsync()
    {
        if (_busy)
            return;
        var titles = _titleBoxes.ToDictionary(p => p.Key, p => p.Value.Text?.Trim() ?? "");
        if (titles.Values.All(string.IsNullOrWhiteSpace))
        {
            ShowEditError(Tr.T("Впишите название хотя бы на одном языке.", "Жок дегенде бир тилде аталышын жазыңыз.", "Enter a title in at least one language.", "En az bir dilde ad girin.", "Kamida bitta tilda nom kiriting."));
            return;
        }

        var from = _fromPicker!.SelectedDate?.Date;
        var to = _toPicker!.SelectedDate?.Date;
        if (from != null && to != null && to < from)
        {
            ShowEditError(Tr.T("Дата окончания раньше даты начала.", "Аяктоо күнү башталыш күнүнөн мурун.", "The end date is before the start date.", "Bitiş tarihi başlangıçtan önce.", "Tugash sanasi boshlanishdan oldin."));
            return;
        }

        var draft = new ShowcaseCollectionDraft
        {
            Titles = titles,
            Description = _descriptionBox!.Text ?? "",
            Season = _seasonBox!.Text ?? "",
            CoverId = _coverChanged ? _coverId : null,
            ClearCover = _coverCleared && !_isNew,
            ProductIds = _productIds.ToList(),
            IsActive = _activeBox!.IsChecked == true,
            Position = _isNew ? (_items.Count == 0 ? 0 : _items.Max(i => i.Position) + 1) : null,
            StartsAt = from is { } f ? new DateTimeOffset(f, TimeZoneInfo.Local.GetUtcOffset(f)) : null,
            EndsAt = to is { } t ? new DateTimeOffset(t.AddDays(1).AddSeconds(-1), TimeZoneInfo.Local.GetUtcOffset(t)) : null,
            ClearDates = !_isNew && from == null && to == null,
        };

        _busy = true;
        try
        {
            if (_isNew)
                await _api.CreateCollectionAsync(draft, _lifetime.Token).ConfigureAwait(true);
            else
                await _api.UpdateCollectionAsync(_editing!.Id, draft, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _busy = false;
            PosLogger.Log($"Коллекция не сохранена: {ex.Message}", "SHOWCASE");
            ShowEditError(SiteReviewsWindow.ErrorText(ex));
            return;
        }

        _busy = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    private void ShowEditError(string text)
    {
        _editError!.Text = text;
        _editError.IsVisible = true;
    }
}
