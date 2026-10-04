using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-02: «Новый прокат» — клиент, вещи (у одежды — размер и цвет в окне выбора варианта),
/// срок, цена за сутки и залог (деньги или паспорт / другой документ — «в залоге и паспорт добавь»).
/// Результат окна — созданный на сервере прокат; <see cref="RentTotal"/> — сколько взять за прокат в чеке.</summary>
public sealed class NewRentalWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TextBox _clientSearch;
    // 2026-10-02, владелец: «список клиентов нормальным сделай — как выпадающий список, с фиксированной
    // высотой и прокруткой, если их много». Раньше — плашки в несколько рядов.
    private readonly StackPanel _clientResults = new() { Spacing = 0 };
    private readonly Border _clientDropdown = new()
    {
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), IsVisible = false, ClipToBounds = true,
        Margin = new Thickness(0, -6, 0, 0),
    };
    private readonly TextBlock _clientChosen = new() { FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _itemSearch;
    private readonly WrapPanel _itemResults = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _items = new() { Spacing = 6 };
    private readonly CalendarDatePicker _from = new() { SelectedDate = DateTime.Today, Height = 44, MinWidth = 170 };
    private readonly CalendarDatePicker _to = new() { SelectedDate = DateTime.Today.AddDays(1), Height = 44, MinWidth = 170 };
    private readonly TextBox _pricePerDay;
    private readonly TextBlock _total = new() { FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _depMoney = new() { GroupName = "dep", IsChecked = true, FontSize = 15, Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _depDoc = new() { GroupName = "dep", FontSize = 15 };
    private readonly TextBox _depAmount;
    private readonly TextBox _depDocText;
    private readonly TextBox _note;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _create;
    private readonly List<RentalItem> _chosenItems = new();
    private (string Id, string Name)? _client;
    // 2026-10-02, владелец: «список клиентов не открывается; если нет клиента — добавить клиента тоже сделай».
    private readonly StackPanel _newClientPanel = new() { Spacing = 8, IsVisible = false };
    private TextBox _newClientName = null!;
    private TextBox _newClientPhone = null!;
    private DispatcherTimer? _clientTimer;

    public double RentTotal { get; private set; }
    private Border _moneyTile = null!;
    private Border _docTile = null!;

    /// <summary>2026-10-02: из окна оплаты — вещи и клиент из чека уже подставлены.</summary>
    public NewRentalWindow(IReadOnlyList<RentalItem> items, string? clientId, string? clientName, double pricePerDay = 0) : this()
    {
        _chosenItems.AddRange(items);
        if (pricePerDay > 0)
            _pricePerDay.Text = pricePerDay.ToString("0.##", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            _client = (clientId, clientName ?? "");
            _clientChosen.Text = "✓ " + (clientName ?? "");
        }
        RenderItems();
    }

    public NewRentalWindow()
    {
        Title = T("Новый прокат", "Жаңы прокат", "New rental", "Yeni kiralama", "Yangi prokat");
        Width = 760;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        _clientSearch = UiKit.Input(this, T("Имя или телефон клиента", "Кардардын аты же телефону", "Client name or phone", "Müşteri adı veya telefonu", "Mijoz ismi yoki telefoni"));
        _itemSearch = UiKit.Input(this, T("Что выдаём: название товара", "Эмне беребиз: товардын аталышы", "What to rent: product name", "Ne kiralanıyor: ürün adı", "Nima beriladi: mahsulot nomi"));
        _pricePerDay = UiKit.Input(this, "0");
        _pricePerDay.Width = 140;
        _depAmount = UiKit.Input(this, "0");
        _depAmount.Width = 160;
        _depAmount.HorizontalAlignment = HorizontalAlignment.Left;
        _depDocText = UiKit.Input(this, T("Паспорт: серия и номер, ФИО", "Паспорт: сериясы жана номери, аты-жөнү", "Passport: number, full name", "Pasaport: numara, ad soyad", "Pasport: raqami, F.I.Sh."));
        _depDocText.MaxLength = 250;
        _note = UiKit.Input(this, T("Комментарий (необязательно)", "Түшүндүрмө (милдеттүү эмес)", "Comment (optional)", "Not (isteğe bağlı)", "Izoh (ixtiyoriy)"));

        // 2026-10-02, владелец: «сделай редизайн всего, что добавили нового». Шаги — пронумерованными
        // карточками, залог — двумя большими плитками, кнопки — внизу окна вне прокрутки.
        var root = new StackPanel { Margin = new Thickness(26, 20, 26, 12), Spacing = 14 };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        // 1. Клиент
        Use(_clientChosen, TextBlock.ForegroundProperty, "BrushSuccess");
        var addClient = UiKit.Ghost(this, "+  " + T("Новый клиент", "Жаңы кардар", "New client", "Yeni müşteri", "Yangi mijoz"));
        addClient.Height = 40;
        addClient.HorizontalAlignment = HorizontalAlignment.Left;
        addClient.Click += (_, _) =>
        {
            _newClientPanel.IsVisible = !_newClientPanel.IsVisible;
            if (_newClientPanel.IsVisible)
            {
                // Набранное в поиске — подсказка: цифры в телефон, буквы в имя.
                var typed = _clientSearch.Text?.Trim() ?? "";
                if (typed.Length > 0 && typed.Count(char.IsDigit) >= typed.Length / 2)
                    _newClientPhone.Text = typed;
                else if (typed.Length > 0)
                    _newClientName.Text = typed;
                _newClientName.Focus();
            }
        };
        _newClientName = UiKit.Input(this, T("Имя и фамилия", "Аты-жөнү", "Full name", "Ad soyad", "Ism familiya"));
        _newClientPhone = UiKit.Input(this, T("Телефон, например +996 700 123 456", "Телефон, мисалы +996 700 123 456", "Phone, e.g. +996 700 123 456", "Telefon, örn. +996 700 123 456", "Telefon, masalan +996 700 123 456"));
        var saveClient = UiKit.Primary(this, T("Сохранить клиента", "Кардарды сактоо", "Save client", "Müşteriyi kaydet", "Mijozni saqlash"));
        saveClient.HorizontalAlignment = HorizontalAlignment.Left;
        saveClient.Click += async (_, _) => await CreateClientAsync().ConfigureAwait(true);
        _newClientPanel.Children.Add(_newClientName);
        _newClientPanel.Children.Add(_newClientPhone);
        _newClientPanel.Children.Add(saveClient);
        Use(_clientDropdown, Border.BackgroundProperty, "BrushPanel");
        Use(_clientDropdown, Border.BorderBrushProperty, "BrushBorder");
        _clientDropdown.Child = new ScrollViewer
        {
            Content = _clientResults, MaxHeight = 264,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        root.Children.Add(Section(1, T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), _clientSearch, _clientDropdown, _clientChosen, addClient, _newClientPanel));
        _clientSearch.TextChanged += (_, _) => ScheduleClientSearch();
        // Список клиентов — сразу при нажатии на поле (раньше — только с двух букв); выбранного клиента
        // можно сменить, снова нажав на поле.
        _clientSearch.GotFocus += (_, _) => ScheduleClientSearch();

        // 2. Вещи
        root.Children.Add(Section(2, T("Вещи", "Буюмдар", "Items", "Ürünler", "Buyumlar"), _itemSearch, _itemResults, _items));
        _itemSearch.TextChanged += (_, _) => RenderItemResults();

        // 3. Срок и цена
        var dates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        dates.Children.Add(Small(T("с", "—дан", "from", "başlangıç", "dan")));
        dates.Children.Add(_from);
        dates.Children.Add(Small(T("по", "—га чейин", "to", "bitiş", "gacha")));
        dates.Children.Add(_to);
        _from.SelectedDateChanged += (_, _) => UpdateTotal();
        _to.SelectedDateChanged += (_, _) => UpdateTotal();
        var price = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        price.Children.Add(_pricePerDay);
        Use(_total, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        _total.FontSize = 17;
        price.Children.Add(_total);
        _pricePerDay.TextChanged += (_, _) => UpdateTotal();
        root.Children.Add(Section(3, T("Срок и цена", "Мөөнөт жана баа", "Period and price", "Süre ve fiyat", "Muddat va narx"),
            dates,
            UiKit.Label(this, T("Цена за сутки (сом) — сумма добавится в чек", "Суткалык баа (сом) — сумма чекке кошулат", "Price per day (som) — the total goes into the receipt",
                "Günlük fiyat (som) — toplam fişe eklenir", "Sutkalik narx (so'm) — summa chekka qo'shiladi")),
            price));

        // 4. Залог — две плитки
        _depMoney.Content = T("Деньги (наличными)", "Акча (накталай)", "Money (cash)", "Para (nakit)", "Pul (naqd)");
        _depDoc.Content = T("Паспорт / документ", "Паспорт / документ", "Passport / document", "Pasaport / belge", "Pasport / hujjat");
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        _moneyTile = DepositTile(RentalsWindow.Icons.Cash,
            T("Деньги", "Акча", "Money", "Para", "Pul"),
            T("наличными, сразу в кассу", "накталай, дароо кассага", "cash, straight into the till", "nakit, doğrudan kasaya", "naqd, darhol kassaga"),
            () => { _depMoney.IsChecked = true; UpdateDeposit(); });
        _docTile = DepositTile(RentalsWindow.Icons.IdCard,
            T("Паспорт / документ", "Паспорт / документ", "Passport / document", "Pasaport / belge", "Pasport / hujjat"),
            T("клиент оставляет документ", "кардар документ калтырат", "the client leaves a document", "müşteri belge bırakır", "mijoz hujjat qoldiradi"),
            () => { _depDoc.IsChecked = true; UpdateDeposit(); });
        tiles.Children.Add(_moneyTile);
        Grid.SetColumn(_docTile, 2);
        tiles.Children.Add(_docTile);
        root.Children.Add(Section(4, T("Залог", "Күрөө", "Deposit", "Depozito", "Garov"), tiles, _depAmount, _depDocText));
        _depMoney.IsCheckedChanged += (_, _) => UpdateDeposit();
        UpdateDeposit();

        root.Children.Add(Section(5, T("Комментарий", "Түшүндүрмө", "Comment", "Not", "Izoh"), _note));

        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        _create = UiKit.Primary(this, T("Оформить прокат", "Прокатты түзүү", "Create rental", "Kiralamayı oluştur", "Prokatni rasmiylashtirish"));
        _create.MinWidth = 220;
        _create.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_create);

        var footer = new Border { Padding = new Thickness(26, 12, 26, 16), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        Use(footer, Border.BorderBrushProperty, "BrushBorder");
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = root };
        layout.Children.Add(scroll);
        // 2026-10-02, проверка: колесо мыши над полем даты меняло дату (при прокрутке формы «по» уехало
        // с 03.10 на 18.10, сумма проката выросла в 16 раз). Над датами колесо прокручивает форму.
        foreach (var picker in new[] { _from, _to })
        {
            picker.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                e.Handled = true;
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - e.Delta.Y * 60));
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        Content = layout;
        UpdateTotal();
        RenderItems();
        Opened += (_, _) => _clientSearch.Focus();
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private int Days => _from.SelectedDate is { } f && _to.SelectedDate is { } t ? Math.Max(1, (t.Date - f.Date).Days) : 1;

    private void UpdateTotal()
    {
        var perDay = ParseMoney(_pricePerDay.Text);
        RentTotal = Math.Round(perDay * Days, 2);
        _total.Text = T($"× {Days} сут. = {RentalsWindow.Money(RentTotal)}", $"× {Days} сутка = {RentalsWindow.Money(RentTotal)}", $"× {Days} d = {RentalsWindow.Money(RentTotal)}",
            $"× {Days} gün = {RentalsWindow.Money(RentTotal)}", $"× {Days} kun = {RentalsWindow.Money(RentTotal)}");
    }

    private void UpdateDeposit()
    {
        var money = _depMoney.IsChecked == true;
        _depAmount.IsVisible = money;
        _depDocText.IsVisible = !money;
        if (_moneyTile is null || _docTile is null)
            return;
        StyleTile(_moneyTile, money);
        StyleTile(_docTile, !money);
    }

    private void StyleTile(Border tile, bool selected)
    {
        tile.BorderThickness = new Thickness(selected ? 2 : 1);
        Use(tile, Border.BorderBrushProperty, selected ? "BrushAccentStrong" : "BrushBorder");
        Use(tile, Border.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
    }

    /// <summary>Пронумерованная карточка шага.</summary>
    private Border Section(int number, string title, params Control[] content)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
        Use(badge, Border.BackgroundProperty, "BrushAccent");
        var num = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Use(num, TextBlock.ForegroundProperty, "BrushAccentForeground");
        badge.Child = num;
        head.Children.Add(badge);
        var t = new TextBlock { Text = title, FontSize = 15.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(t);
        stack.Children.Add(head);
        foreach (var c in content)
            stack.Children.Add(c);
        card.Child = stack;
        return card;
    }

    /// <summary>Плитка вида залога: пиктограмма, название, пояснение.</summary>
    private Border DepositTile(string icon, string title, string hint, Action onClick)
    {
        var tile = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(icon), Width = 26, Height = 26, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushAccentStrong");
        g.Children.Add(path);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var t1 = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Bold };
        Use(t1, TextBlock.ForegroundProperty, "BrushText");
        var t2 = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Use(t2, TextBlock.ForegroundProperty, "BrushTextSoft");
        texts.Children.Add(t1);
        texts.Children.Add(t2);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        tile.Child = g;
        tile.PointerPressed += (_, _) => onClick();
        return tile;
    }

    private void ScheduleClientSearch()
    {
        _clientTimer?.Stop();
        _clientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _clientTimer.Tick += async (_, _) =>
        {
            _clientTimer?.Stop();
            await SearchClientsAsync().ConfigureAwait(true);
        };
        _clientTimer.Start();
    }

    private async Task SearchClientsAsync()
    {
        var text = _clientSearch.Text?.Trim() ?? "";
        _clientResults.Children.Clear();
        _clientDropdown.IsVisible = true;
        try
        {
            var rows = await App.GetRequiredService<IClientsApiService>().GetClientsAsync(text.Length == 0 ? null : text).ConfigureAwait(true);
            _clientResults.Children.Clear();
            foreach (var r in rows.Take(100))
            {
                var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var name = Field(r, "full_name") ?? Field(r, "name") ?? "—";
                var phone = Field(r, "phone") ?? "";
                var b = ClientRow(name, phone, _client?.Id == id, _clientResults.Children.Count > 0);
                b.Click += (_, _) =>
                {
                    _client = (id!, name);
                    _clientChosen.Text = "✓ " + name + (string.IsNullOrWhiteSpace(phone) ? "" : " · " + phone);
                    _clientResults.Children.Clear();
                    _clientDropdown.IsVisible = false;
                };
                _clientResults.Children.Add(b);
            }
            if (_clientResults.Children.Count == 0)
                _clientResults.Children.Add(PaddedSmall(T("Клиент не найден — добавьте его в «Клиенты».", "Кардар табылган жок — аны «Кардарлар» бөлүмүнө кошуңуз.", "Client not found — add them in “Clients”.",
                    "Müşteri bulunamadı — «Müşteriler»e ekleyin.", "Mijoz topilmadi — uni «Mijozlar»ga qo'shing.")));
        }
        catch (Exception ex)
        {
            _clientResults.Children.Add(PaddedSmall(T("Поиск клиентов не удался: ", "Кардарларды издөө болбой калды: ", "Client search failed: ", "Müşteri araması başarısız: ", "Mijozlarni qidirib bo'lmadi: ") + ex.Message));
        }
    }

    /// <summary>Строка выпадающего списка: имя слева, телефон справа, разделитель сверху.</summary>
    private Button ClientRow(string name, string phone, bool selected, bool separator)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var n = new TextBlock { Text = name, FontSize = 14.5, FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(n, TextBlock.ForegroundProperty, "BrushText");
        var ph = new TextBlock { Text = phone, FontSize = 13.5, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Use(ph, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetColumn(ph, 1);
        grid.Children.Add(n);
        grid.Children.Add(ph);
        var b = new Button
        {
            Content = grid, MinHeight = 44, Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0, separator ? 1 : 0, 0, 0),
        };
        Use(b, Button.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
        Use(b, Button.BorderBrushProperty, "BrushBorder");
        return b;
    }

    private Control PaddedSmall(string text)
    {
        var t = Small(text);
        t.Margin = new Thickness(14, 10);
        return t;
    }

    private async Task CreateClientAsync()
    {
        var name = _newClientName.Text?.Trim() ?? "";
        var phone = _newClientPhone.Text?.Trim() ?? "";
        if (name.Length < 2 || phone.Count(char.IsDigit) < 6)
        {
            _error.Text = T("Впишите имя и телефон клиента.", "Кардардын атын жана телефонун жазыңыз.", "Enter the client's name and phone.", "Müşterinin adını ve telefonunu yazın.", "Mijozning ismi va telefonini yozing.");
            return;
        }
        try
        {
            var created = await App.GetRequiredService<IClientsApiService>().CreateClientAsync(name, phone, null).ConfigureAwait(true);
            var id = created.ValueKind == JsonValueKind.Object && created.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(T("сервер не вернул номер клиента", "сервер кардардын номерин кайтарган жок", "the server returned no client id", "sunucu müşteri kimliği döndürmedi", "server mijoz raqamini qaytarmadi"));
            _client = (id, name);
            _clientChosen.Text = "✓ " + name + " · " + phone;
            _clientResults.Children.Clear();
            _clientDropdown.IsVisible = false;
            _newClientPanel.IsVisible = false;
            _error.Text = "";
            PosLogger.Log("Прокат: новый клиент создан из окна проката.", "RENTAL");
        }
        catch (Exception ex)
        {
            _error.Text = T("Клиента создать не удалось: ", "Кардарды түзүү болбой калды: ", "Could not create the client: ", "Müşteri oluşturulamadı: ", "Mijozni yaratib bo'lmadi: ") + RentalsApi.Describe(ex);
        }
    }

    private void RenderItemResults()
    {
        _itemResults.Children.Clear();
        var text = _itemSearch.Text?.Trim() ?? "";
        if (text.Length < 2)
            return;
        List<CatalogProductTileVm> found;
        try
        {
            found = CatalogCacheService.Products
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && p.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(p => p.Title).Take(8).ToList();
        }
        catch
        {
            found = new List<CatalogProductTileVm>();
        }
        foreach (var p in found)
        {
            var b = UiKit.Chip(this, p.Title, false);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += async (_, _) => await PickProductAsync(p).ConfigureAwait(true);
            _itemResults.Children.Add(b);
        }
    }

    private async Task PickProductAsync(CatalogProductTileVm p)
    {
        // 2026-10-04: общий кеш размеров (ProductVariantCache) — окно открывается сразу, без ожидания сервера.
        var variants = await ProductVariantCache.GetAsync(p.Id, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (variants is null)
            PosLogger.Log($"Прокат: варианты товара {p.Id} не получены.", "RENTAL");

        if (variants is { Count: > 0 } && variants.Any(v => v.IsActive))
        {
            var picker = new VariantPickerWindow(p, variants, Tr.T("Выбрать", "Тандоо", "Choose", "Seç", "Tanlash"));
            await picker.ShowDialog(this).ConfigureAwait(true);
            if (picker.Result is not { Id: { } variantId } chosen)
                return;
            _chosenItems.Add(new RentalItem(p.Id, variantId, p.Title, chosen.Size, chosen.Color, picker.Quantity));
        }
        else
        {
            _chosenItems.Add(new RentalItem(p.Id, null, p.Title, "", "", 1));
        }
        _itemSearch.Text = "";
        _itemResults.Children.Clear();
        RenderItems();
    }

    private void RenderItems()
    {
        _items.Children.Clear();
        if (_chosenItems.Count == 0)
        {
            _items.Children.Add(Small(T("Найдите товар по названию и нажмите на него.", "Товарды аты боюнча таап, аны басыңыз.", "Find a product by name and click it.",
                "Ürünü adıyla bulun ve tıklayın.", "Mahsulotni nomi bo'yicha toping va bosing.")));
            return;
        }
        foreach (var item in _chosenItems.ToList())
        {
            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1) };
            Use(row, Border.BackgroundProperty, "BrushPanelSoft");
            Use(row, Border.BorderBrushProperty, "BrushBorder");
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = item.Label + (item.Qty > 1 ? $" × {item.Qty:0.###}" : ""), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            g.Children.Add(name);
            var remove = new Button { Content = "✕", Width = 32, Height = 32, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { _chosenItems.Remove(item); RenderItems(); };
            Grid.SetColumn(remove, 1);
            g.Children.Add(remove);
            row.Child = g;
            _items.Children.Add(row);
        }
    }

    private async Task CreateAsync()
    {
        _error.Text = "";
        if (_client is null)
        {
            _error.Text = T("Выберите клиента.", "Кардарды тандаңыз.", "Choose a client.", "Müşteri seçin.", "Mijozni tanlang.");
            return;
        }
        if (_chosenItems.Count == 0)
        {
            _error.Text = T("Добавьте хотя бы одну вещь.", "Жок дегенде бир буюм кошуңуз.", "Add at least one item.", "En az bir ürün ekleyin.", "Kamida bitta buyum qo'shing.");
            return;
        }
        if (_from.SelectedDate is not { } from || _to.SelectedDate is not { } to || to.Date < from.Date)
        {
            _error.Text = T("Проверьте даты: «по» не раньше «с».", "Даталарды текшериңиз.", "Check the dates: “to” cannot be before “from”.", "Tarihleri kontrol edin.", "Sanalarni tekshiring.");
            return;
        }
        var money = _depMoney.IsChecked == true;
        var depAmount = money ? ParseMoney(_depAmount.Text) : 0;
        var doc = money ? null : _depDocText.Text?.Trim();
        if (!money && string.IsNullOrWhiteSpace(doc))
        {
            _error.Text = T("Впишите документ залога (например, «Паспорт ID1234567, Асанов А.»).", "Күрөө документин жазыңыз.", "Enter the deposit document (e.g. “Passport ID1234567, A. Asanov”).",
                "Depozito belgesini yazın.", "Garov hujjatini yozing.");
            return;
        }

        _create.IsEnabled = false;
        try
        {
            var created = await App.GetRequiredService<RentalsApi>().CreateAsync(_client.Value.Id, _chosenItems, from, to, "day",
                money ? "money" : "document", depAmount, money ? "cash" : null, doc, _note.Text?.Trim()).ConfigureAwait(true);
            // 2026-10-04: вещь ушла со склада — остаток размера в окне выбора уменьшается сразу.
            foreach (var item in _chosenItems)
                if (item.ProductId is { } pid && item.VariantId is { } vid)
                    ProductVariantCache.Adjust(pid, vid, -item.Qty);
            Close(created);
        }
        catch (Exception ex)
        {
            _error.Text = T("Не удалось оформить прокат: ", "Прокатты түзүү болбой калды: ", "Could not create the rental: ", "Kiralama oluşturulamadı: ", "Prokatni rasmiylashtirib bo'lmadi: ") + RentalsApi.Describe(ex);
            PosLogger.Log($"Прокат: создание не удалось ({RentalsApi.Describe(ex)}).", "WARNING");
            _create.IsEnabled = true;
        }
    }

    private static string? Field(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double ParseMoney(string? s) =>
        double.TryParse((s ?? "").Replace(" ", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;

    private TextBlock Small(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>Приём возврата: состояние вещи и штраф (удерживается из залога сервером).</summary>
public sealed class ReturnRentalWindow : Window
{
    private readonly RentalDto _rental;
    private readonly RadioButton _ok = new() { GroupName = "cond", IsChecked = true, FontSize = 15, Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _damaged = new() { GroupName = "cond", FontSize = 15 };
    private readonly TextBox _penalty;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new();
    private readonly Button _confirm;
    private Border _okTile = null!;
    private Border _damagedTile = null!;

    public ReturnRentalWindow(RentalDto rental)
    {
        _rental = rental;
        Title = Tr.T($"Возврат проката №{rental.Number}", $"Прокат №{rental.Number} кайтаруу", $"Return of rental #{rental.Number}", $"Kiralama №{rental.Number} iadesi", $"Prokat №{rental.Number} qaytarilishi");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        // 2026-10-02, редизайн: карточка проката, состояние — двумя плитками, итог «вернуть клиенту» сразу.
        var root = new StackPanel { Margin = new Thickness(26, 22), Spacing = 14 };
        var title = new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        var info = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12) };
        Use(info, Border.BackgroundProperty, "BrushPanelSoft");
        var infoStack = new StackPanel { Spacing = 4 };
        var who = new TextBlock { Text = rental.ClientName, FontSize = 16, FontWeight = FontWeight.Bold };
        Use(who, TextBlock.ForegroundProperty, "BrushText");
        infoStack.Children.Add(who);
        infoStack.Children.Add(Info(string.Join("; ", rental.Items.Select(i => i.Label))));
        var period = Info($"{RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}"
                          + (rental.IsOverdue ? Tr.T(" · просрочен", " · мөөнөтү өттү", " · overdue", " · gecikmiş", " · muddati o'tgan") : ""));
        if (rental.IsOverdue)
            Use(period, TextBlock.ForegroundProperty, "BrushDanger");
        infoStack.Children.Add(period);
        infoStack.Children.Add(Info(Tr.T("Залог: ", "Күрөө: ", "Deposit: ", "Depozito: ", "Garov: ") + (rental.IsDocumentDeposit ? rental.DepositDocument : RentalsWindow.Money(rental.DepositAmount))));
        info.Child = infoStack;
        root.Children.Add(info);

        root.Children.Add(UiKit.Label(this, Tr.T("Состояние вещи", "Буюмдун абалы", "Item condition", "Ürün durumu", "Buyum holati")));
        _ok.Content = Tr.T("В порядке", "Жакшы", "OK", "Sorunsuz", "Yaxshi");
        _damaged.Content = Tr.T("Повреждено", "Бузулган", "Damaged", "Hasarlı", "Shikastlangan");
        var cond = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        _okTile = CondTile("M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z", Tr.T("В порядке", "Жакшы", "OK", "Sorunsuz", "Yaxshi"), "BrushSuccess",
            () => { _ok.IsChecked = true; Refresh(); });
        _damagedTile = CondTile("M13,14H11V10H13M13,18H11V16H13M1,21H23L12,2L1,21Z", Tr.T("Повреждено", "Бузулган", "Damaged", "Hasarlı", "Shikastlangan"), "BrushWarning",
            () => { _damaged.IsChecked = true; Refresh(); });
        cond.Children.Add(_okTile);
        Grid.SetColumn(_damagedTile, 2);
        cond.Children.Add(_damagedTile);
        root.Children.Add(cond);

        root.Children.Add(UiKit.Label(this, rental.IsDocumentDeposit
            ? Tr.T("Штраф (сом) — за просрочку или повреждение", "Айып (сом) — кечиктирүү же бузулуу үчүн", "Penalty (som) — for delay or damage", "Ceza (som) — gecikme veya hasar için", "Jarima (so'm) — kechikish yoki shikast uchun")
            : Tr.T("Штраф (сом) — удерживается из залога", "Айып (сом) — күрөөдөн кармалат", "Penalty (som) — withheld from the deposit", "Ceza (som) — depozitodan kesilir", "Jarima (so'm) — garovdan ushlab qolinadi")));
        _penalty = UiKit.Input(this, "0");
        _penalty.Width = 180;
        _penalty.HorizontalAlignment = HorizontalAlignment.Left;
        _penalty.TextChanged += (_, _) => Refresh();
        root.Children.Add(_penalty);

        _summary.FontSize = 16;
        _summary.FontWeight = FontWeight.Bold;
        _summary.TextWrapping = TextWrapping.Wrap;
        Use(_summary, TextBlock.ForegroundProperty, "BrushText");
        var sumCard = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = _summary };
        Use(sumCard, Border.BackgroundProperty, "BrushAccentSoft");
        root.Children.Add(sumCard);

        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var cancel = UiKit.Ghost(this, Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        _confirm = UiKit.Primary(this, Tr.T("Принять возврат", "Кайтарууну кабыл алуу", "Take back", "İadeyi al", "Qaytarishni qabul qilish"));
        _confirm.Click += async (_, _) => await ConfirmAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_confirm);
        root.Children.Add(buttons);
        Content = root;
        Refresh();
    }

    private double Penalty =>
        double.TryParse((_penalty.Text ?? "").Replace(" ", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 0;

    /// <summary>Плитки состояния и итог: что вернуть клиенту.</summary>
    private void Refresh()
    {
        if (_okTile is null || _damagedTile is null)
            return;
        Tile(_okTile, _ok.IsChecked == true);
        Tile(_damagedTile, _damaged.IsChecked == true);
        if (_rental.IsDocumentDeposit)
            _summary.Text = Tr.T($"Вернуть клиенту документ: {_rental.DepositDocument}", $"Кардарга документти кайтаруу: {_rental.DepositDocument}", $"Give back the document: {_rental.DepositDocument}",
                $"Belgeyi iade edin: {_rental.DepositDocument}", $"Hujjatni qaytaring: {_rental.DepositDocument}")
                + (Penalty > 0 ? Tr.T($" · штраф {RentalsWindow.Money(Penalty)}", $" · айып {RentalsWindow.Money(Penalty)}", $" · penalty {RentalsWindow.Money(Penalty)}", $" · ceza {RentalsWindow.Money(Penalty)}", $" · jarima {RentalsWindow.Money(Penalty)}") : "");
        else
        {
            var back = Math.Max(0, _rental.DepositAmount - Penalty);
            _summary.Text = Tr.T($"Вернуть клиенту залог: {RentalsWindow.Money(back)}", $"Кардарга күрөөнү кайтаруу: {RentalsWindow.Money(back)}", $"Give back the deposit: {RentalsWindow.Money(back)}",
                $"Depozitoyu iade edin: {RentalsWindow.Money(back)}", $"Garovni qaytaring: {RentalsWindow.Money(back)}")
                + (Penalty > 0 ? Tr.T($" (удержано {RentalsWindow.Money(Math.Min(Penalty, _rental.DepositAmount))})", $" (кармалды {RentalsWindow.Money(Math.Min(Penalty, _rental.DepositAmount))})",
                    $" (withheld {RentalsWindow.Money(Math.Min(Penalty, _rental.DepositAmount))})", $" (tutulan {RentalsWindow.Money(Math.Min(Penalty, _rental.DepositAmount))})",
                    $" (ushlab qolindi {RentalsWindow.Money(Math.Min(Penalty, _rental.DepositAmount))})") : "");
        }
    }

    private void Tile(Border tile, bool selected)
    {
        tile.BorderThickness = new Thickness(selected ? 2 : 1);
        Use(tile, Border.BorderBrushProperty, selected ? "BrushAccentStrong" : "BrushBorder");
        Use(tile, Border.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
    }

    private Border CondTile(string icon, string text, string iconBrush, Action onClick)
    {
        var tile = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(icon), Width = 22, Height = 22, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, iconBrush);
        row.Children.Add(path);
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        row.Children.Add(t);
        tile.Child = row;
        tile.PointerPressed += (_, _) => onClick();
        return tile;
    }

    private async Task ConfirmAsync()
    {
        _error.Text = "";
        var penalty = Penalty;
        _confirm.IsEnabled = false;
        try
        {
            var done = await App.GetRequiredService<RentalsApi>().ReturnAsync(_rental.Id, _damaged.IsChecked == true ? "damaged" : "ok", penalty).ConfigureAwait(true);
            // 2026-10-04: вещь вернулась на склад — остаток размера в окне выбора растёт сразу.
            foreach (var item in _rental.Items)
                if (item.ProductId is { } pid && item.VariantId is { } vid)
                    ProductVariantCache.Adjust(pid, vid, item.Qty);
            Close(done);
        }
        catch (Exception ex)
        {
            _error.Text = Tr.T("Не удалось принять возврат: ", "Кайтарууну кабыл алуу болбой калды: ", "Could not take back: ", "İade alınamadı: ", "Qaytarishni qabul qilib bo'lmadi: ") + RentalsApi.Describe(ex);
            PosLogger.Log($"Прокат №{_rental.Number}: возврат не удался ({RentalsApi.Describe(ex)}).", "WARNING");
            _confirm.IsEnabled = true;
        }
    }

    private TextBlock Info(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
