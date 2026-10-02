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
    private readonly WrapPanel _clientResults = new() { Orientation = Orientation.Horizontal };
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
    private DispatcherTimer? _clientTimer;

    public double RentTotal { get; private set; }

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

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 22), Spacing = 10 };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        // Клиент
        root.Children.Add(UiKit.Label(this, T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz")));
        root.Children.Add(_clientSearch);
        root.Children.Add(_clientResults);
        Use(_clientChosen, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(_clientChosen);
        _clientSearch.TextChanged += (_, _) => ScheduleClientSearch();

        // Вещи
        root.Children.Add(UiKit.Label(this, T("Вещи", "Буюмдар", "Items", "Ürünler", "Buyumlar")));
        root.Children.Add(_itemSearch);
        root.Children.Add(_itemResults);
        root.Children.Add(_items);
        _itemSearch.TextChanged += (_, _) => RenderItemResults();

        // Срок
        root.Children.Add(UiKit.Label(this, T("Срок", "Мөөнөт", "Period", "Süre", "Muddat")));
        var dates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        dates.Children.Add(Small(T("с", "—дан", "from", "başlangıç", "dan")));
        dates.Children.Add(_from);
        dates.Children.Add(Small(T("по", "—га чейин", "to", "bitiş", "gacha")));
        dates.Children.Add(_to);
        root.Children.Add(dates);
        _from.SelectedDateChanged += (_, _) => UpdateTotal();
        _to.SelectedDateChanged += (_, _) => UpdateTotal();

        // Цена
        root.Children.Add(UiKit.Label(this, T("Цена проката за сутки (сом) — оплата в чеке", "Прокаттын суткалык баасы (сом) — төлөм чекте", "Rental price per day (som) — paid in the receipt",
            "Günlük kiralama bedeli (som) — fişte ödenir", "Prokatning sutkalik narxi (so'm) — to'lov chekda")));
        var price = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        price.Children.Add(_pricePerDay);
        Use(_total, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        price.Children.Add(_total);
        root.Children.Add(price);
        _pricePerDay.TextChanged += (_, _) => UpdateTotal();

        // Залог
        root.Children.Add(UiKit.Label(this, T("Залог", "Күрөө", "Deposit", "Depozito", "Garov")));
        _depMoney.Content = T("Деньги (наличными)", "Акча (накталай)", "Money (cash)", "Para (nakit)", "Pul (naqd)");
        _depDoc.Content = T("Паспорт / документ", "Паспорт / документ", "Passport / document", "Pasaport / belge", "Pasport / hujjat");
        var depType = new StackPanel { Orientation = Orientation.Horizontal };
        depType.Children.Add(_depMoney);
        depType.Children.Add(_depDoc);
        root.Children.Add(depType);
        root.Children.Add(_depAmount);
        root.Children.Add(_depDocText);
        _depMoney.IsCheckedChanged += (_, _) => UpdateDeposit();
        UpdateDeposit();

        root.Children.Add(UiKit.Label(this, T("Комментарий", "Түшүндүрмө", "Comment", "Not", "Izoh")));
        root.Children.Add(_note);

        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        _create = UiKit.Primary(this, T("Оформить прокат", "Прокатты түзүү", "Create rental", "Kiralamayı oluştur", "Prokatni rasmiylashtirish"));
        _create.MinWidth = 220;
        _create.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_create);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root };
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
        if (text.Length < 2)
            return;
        try
        {
            var rows = await App.GetRequiredService<IClientsApiService>().GetClientsAsync(text).ConfigureAwait(true);
            foreach (var r in rows.Take(8))
            {
                var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var name = Field(r, "full_name") ?? Field(r, "name") ?? "—";
                var phone = Field(r, "phone") ?? "";
                var b = UiKit.Chip(this, string.IsNullOrWhiteSpace(phone) ? name : $"{name} · {phone}", _client?.Id == id);
                b.Margin = new Thickness(0, 0, 8, 8);
                b.Click += (_, _) =>
                {
                    _client = (id!, name);
                    _clientChosen.Text = "✓ " + name + (string.IsNullOrWhiteSpace(phone) ? "" : " · " + phone);
                    _clientResults.Children.Clear();
                };
                _clientResults.Children.Add(b);
            }
            if (_clientResults.Children.Count == 0)
                _clientResults.Children.Add(Small(T("Клиент не найден — добавьте его в «Клиенты».", "Кардар табылган жок — аны «Кардарлар» бөлүмүнө кошуңуз.", "Client not found — add them in “Clients”.",
                    "Müşteri bulunamadı — «Müşteriler»e ekleyin.", "Mijoz topilmadi — uni «Mijozlar»ga qo'shing.")));
        }
        catch (Exception ex)
        {
            _clientResults.Children.Add(Small(T("Поиск клиентов не удался: ", "Кардарларды издөө болбой калды: ", "Client search failed: ", "Müşteri araması başarısız: ", "Mijozlarni qidirib bo'lmadi: ") + ex.Message));
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
        List<ProductVariantDto>? variants = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            variants = await App.CatalogApi.GetProductVariantsAsync(p.Id, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: варианты товара {p.Id} не получены ({ex.Message}).", "RENTAL");
        }

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
    private readonly Button _confirm;

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

        var root = new StackPanel { Margin = new Thickness(26, 22), Spacing = 10 };
        var title = new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);
        root.Children.Add(Info($"{rental.ClientName} · " + string.Join("; ", rental.Items.Select(i => i.Label))));
        root.Children.Add(Info(Tr.T($"Срок: {RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}", $"Мөөнөт: {RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}",
            $"Period: {RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}", $"Süre: {RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}",
            $"Muddat: {RentalsWindow.D(rental.DateFrom)} — {RentalsWindow.D(rental.DateTo)}") + (rental.Overdue ? Tr.T(" (просрочен)", " (мөөнөтү өттү)", " (overdue)", " (gecikmiş)", " (muddati o'tgan)") : "")));
        root.Children.Add(Info(Tr.T("Залог: ", "Күрөө: ", "Deposit: ", "Depozito: ", "Garov: ") + (rental.IsDocumentDeposit ? rental.DepositDocument : RentalsWindow.Money(rental.DepositAmount))));

        root.Children.Add(UiKit.Label(this, Tr.T("Состояние вещи", "Буюмдун абалы", "Item condition", "Ürün durumu", "Buyum holati")));
        _ok.Content = Tr.T("В порядке", "Жакшы", "OK", "Sorunsuz", "Yaxshi");
        _damaged.Content = Tr.T("Повреждено", "Бузулган", "Damaged", "Hasarlı", "Shikastlangan");
        var cond = new StackPanel { Orientation = Orientation.Horizontal };
        cond.Children.Add(_ok);
        cond.Children.Add(_damaged);
        root.Children.Add(cond);

        root.Children.Add(UiKit.Label(this, rental.IsDocumentDeposit
            ? Tr.T("Штраф (сом) — за просрочку или повреждение", "Айып (сом) — кечиктирүү же бузулуу үчүн", "Penalty (som) — for delay or damage", "Ceza (som) — gecikme veya hasar için", "Jarima (so'm) — kechikish yoki shikast uchun")
            : Tr.T("Штраф (сом) — удерживается из залога", "Айып (сом) — күрөөдөн кармалат", "Penalty (som) — withheld from the deposit", "Ceza (som) — depozitodan kesilir", "Jarima (so'm) — garovdan ushlab qolinadi")));
        _penalty = UiKit.Input(this, "0");
        _penalty.Width = 180;
        _penalty.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_penalty);

        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = UiKit.Ghost(this, Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(null);
        _confirm = UiKit.Primary(this, Tr.T("Принять возврат", "Кайтарууну кабыл алуу", "Take back", "İadeyi al", "Qaytarishni qabul qilish"));
        _confirm.Click += async (_, _) => await ConfirmAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_confirm);
        root.Children.Add(buttons);
        Content = root;
    }

    private async Task ConfirmAsync()
    {
        _error.Text = "";
        var penalty = double.TryParse((_penalty.Text ?? "").Replace(" ", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 0;
        _confirm.IsEnabled = false;
        try
        {
            var done = await App.GetRequiredService<RentalsApi>().ReturnAsync(_rental.Id, _damaged.IsChecked == true ? "damaged" : "ok", penalty).ConfigureAwait(true);
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
