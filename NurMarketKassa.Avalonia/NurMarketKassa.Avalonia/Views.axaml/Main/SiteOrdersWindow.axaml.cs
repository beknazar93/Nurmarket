using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Заказы с сайта (2026-09-29, владелец: «заказы с сайта тоже должны падать в админку»).
///
/// Сверху — витрина магазина: работает ли, ссылка и куда уходит заказ покупателя. У NurCRM кнопка
/// заказа на витрине открывает WhatsApp на номер витрины и на сервер ничего не пишет — об этом
/// окно говорит прямо, чтобы владелец не ждал здесь заказов из WhatsApp. Ниже — заказы, которые
/// хранит сервер NurCRM (api/main/orders, на сайте — «Закупки»): фильтр по статусу, карточка с
/// товарами и смена статуса «Новый → В процессе → Завершён». Список обновляется сам каждые 30 с,
/// пока окно на экране, и не спрашивает сервер, пока тот просит паузу (429, <see cref="ApiThrottle"/>).</summary>
public partial class SiteOrdersWindow : Window, IOwnerSection
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettingsInterval = TimeSpan.FromMinutes(5);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Названия товаров по id — на весь сеанс: в строках заказа сервер отдаёт только id.</summary>
    private static readonly Dictionary<string, string> ProductNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly ShowcaseApiService _api;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading;
    private bool _busy;
    private string _filter = "all";
    private List<SiteOrder> _orders = new();
    private string? _selectedId;
    private DateTime? _lastSuccess;
    private DateTime _settingsLoadedAt = DateTime.MinValue;
    private ShowcaseSettings? _settings;
    private int? _productCount;
    private bool _offline;
    private string? _accessError;
    private HashSet<string>? _knownIds;
    private readonly HashSet<string> _arrivedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _namesLoading = new(StringComparer.OrdinalIgnoreCase);

    public SiteOrdersWindow()
    {
        InitializeComponent();
        _api = App.GetRequiredService<ShowcaseApiService>();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) =>
        {
            // Спрятанный раздел программы владельца не опрашивает сервер — обновится при показе.
            if (IsVisible)
                await ReloadAsync(manual: false).ConfigureAwait(true);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible && _lastSuccess is { } at && DateTime.Now - at > RefreshInterval)
                _ = ReloadAsync(manual: false);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cts?.Cancel();
            _lifetime.Cancel();
        };
        EscapeKey.Attach(this);
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplyTexts();
        RenderAll();
        await ReloadAsync(manual: true).ConfigureAwait(true);
        _timer.Start();
    }

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>).</summary>
    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        RefreshButton.Margin = new Thickness(0);
        SubtitleText.Margin = new Thickness(0, 0, 16, 0);
        if (SubtitleText.Parent is Control subtitlePanel)
            subtitlePanel.VerticalAlignment = VerticalAlignment.Center;
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    private void ApplyTexts()
    {
        Title = Tr.T("Заказы с сайта", "Сайттан заказдар", "Website orders", "Web sitesi siparişleri", "Saytdan buyurtmalar");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T(
            $"Витрина магазина и заказы NurCRM. Список обновляется сам каждые {RefreshInterval.TotalSeconds:0} с.",
            $"Дүкөндүн витринасы жана NurCRM заказдары. Тизме ар {RefreshInterval.TotalSeconds:0} с сайын өзү жаңырат.",
            $"The store showcase and NurCRM orders. The list refreshes itself every {RefreshInterval.TotalSeconds:0} s.",
            $"Mağaza vitrini ve NurCRM siparişleri. Liste her {RefreshInterval.TotalSeconds:0} saniyede kendini yeniler.",
            $"Do'kon vitrinasi va NurCRM buyurtmalari. Ro'yxat har {RefreshInterval.TotalSeconds:0} soniyada o'zi yangilanadi.");
        RefreshButton.Content = Tr.T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash");
        CloseButton.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        OpenShowcaseButton.Content = Tr.T("Открыть витрину", "Витринаны ачуу", "Open showcase", "Vitrini aç", "Vitrinani ochish");
        CopyLinkButton.Content = Tr.T("Скопировать ссылку", "Шилтемени көчүрүү", "Copy link", "Bağlantıyı kopyala", "Havolani nusxalash");
        SiteSettingsButton.Content = Tr.T("Настройки сайта", "Сайттын жөндөөлөрү", "Website settings", "Web sitesi ayarları", "Sayt sozlamalari");
    }

    // ------------------------------------------------------------------ загрузка

    private async Task ReloadAsync(bool manual)
    {
        if (_loading || _lifetime.IsCancellationRequested)
            return;
        // Сервер просит паузу (429) — фоновое обновление пропускаем, ручное подождёт внутри запроса.
        if (!manual && ApiThrottle.RemainingBlock > TimeSpan.Zero)
            return;

        _loading = true;
        _cts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cts = cts;
        LoadingBar.IsVisible = manual || _lastSuccess == null;
        RefreshButton.IsEnabled = false;
        try
        {
            if (manual || _settings == null || DateTime.Now - _settingsLoadedAt > SettingsInterval)
                await LoadSettingsAsync(cts.Token).ConfigureAwait(true);

            var orders = await _api.ListOrdersAsync(cts.Token).ConfigureAwait(true);
            if (_knownIds != null)
            {
                foreach (var order in orders.Where(o => !_knownIds.Contains(o.Id)))
                    _arrivedIds.Add(order.Id);
            }
            _knownIds = orders.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _orders = orders.ToList();
            _lastSuccess = DateTime.Now;
            _offline = false;
            _accessError = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            _accessError = SessionEndedText;
            PosLogger.Log($"Заказы с сайта: сессия недействительна: {ex.Message}", "SHOWCASE");
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            _accessError = Tr.T(
                "Нет доступа к заказам: у этого аккаунта на сайте NurCRM нет права «Заказы / Закупки».",
                "Заказдарга уруксат жок: бул аккаунттун NurCRM сайтында «Заказдар / Сатып алуулар» укугу жок.",
                "No access to orders: this account doesn't have the “Orders / Purchases” permission on the NurCRM website.",
                "Siparişlere erişim yok: bu hesabın NurCRM sitesinde «Siparişler / Satın almalar» yetkisi yok.",
                "Buyurtmalarga ruxsat yo'q: bu akkauntda NurCRM saytida «Buyurtmalar / Xaridlar» huquqi yo'q.");
            PosLogger.Log($"Заказы с сайта: нет доступа ({ex.StatusCode}): {ex.Message}", "SHOWCASE");
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            UpdatedText.Text = Tr.T("Сервер просит паузу — обновлю чуть позже", "Сервер тыныгуу сурап жатат — бир аздан кийин жаңыртам",
                "The server asked for a pause — will refresh a bit later", "Sunucu ara istedi — biraz sonra yenilenecek",
                "Server tanaffus so'radi — birozdan keyin yangilanadi");
            return;
        }
        catch (Exception ex)
        {
            _offline = true;
            PosLogger.Log($"Заказы с сайта: не загружены: {ex.Message}", "SHOWCASE");
        }
        finally
        {
            _loading = false;
            if (ReferenceEquals(_cts, cts))
            {
                LoadingBar.IsVisible = false;
                RefreshButton.IsEnabled = true;
            }
        }

        RenderAll();
    }

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        try
        {
            _settings = await _api.GetSettingsAsync(ct).ConfigureAwait(true);
            _settingsLoadedAt = DateTime.Now;
            _productCount = null;
            if (_settings.Slug.Length > 0)
            {
                try
                {
                    _productCount = await _api.CountShowcaseProductsAsync(_settings.Slug, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Витрина: число товаров не получено: {ex.Message}", "SHOWCASE");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Настройки — только для карточки витрины; без них заказы всё равно показываем.
            PosLogger.Log($"Витрина: настройки не получены: {ex.Message}", "SHOWCASE");
        }
    }

    // ------------------------------------------------------------------ отрисовка

    private void RenderAll()
    {
        RenderShowcaseCard();
        RenderFilters();
        RenderList();
        RenderDetail();
        RenderUpdated();
    }

    private void RenderShowcaseCard()
    {
        var connected = ShowcaseApiService.IsShowcaseConnected;
        var slug = _settings?.Slug ?? "";
        var phone = _settings?.ShowcasePhone ?? "";
        var hasLink = connected && slug.Length > 0;

        OpenShowcaseButton.IsVisible = hasLink;
        CopyLinkButton.IsVisible = hasLink;
        ShowcaseLinkText.IsVisible = hasLink;
        ShowcaseLinkText.Text = hasLink ? ShowcaseApiService.CatalogUrl(slug) : "";

        if (!connected)
        {
            SetDot("BrushWarning");
            ShowcaseStateText.Text = Tr.T("Витрина не подключена", "Витрина туташтырылган эмес", "The showcase is not connected", "Vitrin bağlı değil", "Vitrina ulanmagan");
            ShowcaseHowText.Text = Tr.T(
                "На тарифе «Старт» онлайн-витрина — платная услуга NurCRM, её подключают по заявке. После подключения здесь появится ссылка на витрину магазина, а покупатели смогут оформлять заказы.",
                "«Старт» тарифинде онлайн-витрина — NurCRMдин акылуу кызматы, ал өтүнмө боюнча туташтырылат. Туташтырылгандан кийин бул жерде дүкөндүн витринасынын шилтемеси чыгат, кардарлар заказ бере алышат.",
                "On the “Start” plan the online showcase is a paid NurCRM service, connected on request. Once connected, the link to your store showcase will appear here and customers will be able to place orders.",
                "«Start» tarifesinde çevrimiçi vitrin, talep üzerine bağlanan ücretli bir NurCRM hizmetidir. Bağlandıktan sonra mağaza vitrininin bağlantısı burada görünür ve müşteriler sipariş verebilir.",
                "«Start» tarifida onlayn vitrina — NurCRMning pullik xizmati, u ariza bo'yicha ulanadi. Ulangandan so'ng bu yerda do'kon vitrinasi havolasi chiqadi va xaridorlar buyurtma bera oladi.");
            return;
        }

        if (_settings == null)
        {
            SetDot("BrushTextSoft");
            ShowcaseStateText.Text = Tr.T("Витрина: нет данных", "Витрина: маалымат жок", "Showcase: no data", "Vitrin: veri yok", "Vitrina: ma'lumot yo'q");
            ShowcaseHowText.Text = Tr.T("Нет связи с сервером NurCRM — состояние витрины появится, когда связь вернётся.",
                "NurCRM сервери менен байланыш жок — витринанын абалы байланыш калыбына келгенде чыгат.",
                "No connection to the NurCRM server — the showcase status will appear when the connection is back.",
                "NurCRM sunucusuyla bağlantı yok — vitrin durumu bağlantı geri geldiğinde görünecek.",
                "NurCRM serveri bilan aloqa yo'q — vitrina holati aloqa tiklanganda chiqadi.");
            return;
        }

        if (slug.Length == 0)
        {
            SetDot("BrushWarning");
            ShowcaseStateText.Text = Tr.T("У витрины нет адреса", "Витринанын дареги жок", "The showcase has no address", "Vitrinin adresi yok", "Vitrinaning manzili yo'q");
            ShowcaseHowText.Text = Tr.T("Задайте адрес в «Настройки сайта» — без него ссылку на витрину собрать нельзя.",
                "«Сайттын жөндөөлөрү» бөлүмүндө дарек коюңуз — ансыз витринанын шилтемесин түзүүгө болбойт.",
                "Set the address in “Website settings” — without it the showcase link can't be built.",
                "Adresi «Web sitesi ayarları» bölümünde belirleyin — o olmadan vitrin bağlantısı oluşturulamaz.",
                "Manzilni «Sayt sozlamalari»da kiriting — usiz vitrina havolasini yasab bo'lmaydi.");
            return;
        }

        SetDot("BrushSuccess");
        ShowcaseStateText.Text = _productCount is { } count
            ? Tr.T($"Витрина работает · товаров на витрине: {count}", $"Витрина иштеп жатат · витринадагы товарлар: {count}",
                $"The showcase is live · products on the showcase: {count}", $"Vitrin yayında · vitrindeki ürünler: {count}",
                $"Vitrina ishlayapti · vitrinadagi mahsulotlar: {count}")
            : Tr.T("Витрина работает", "Витрина иштеп жатат", "The showcase is live", "Vitrin yayında", "Vitrina ishlayapti");

        var how = phone.Length > 0
            ? Tr.T($"Покупатель собирает корзину на витрине и нажимает «Оформить заказ» — сайт NurCRM открывает WhatsApp с текстом заказа на номер {phone}. На сервер NurCRM такой заказ сейчас не записывается, поэтому в списке ниже его нет: отвечайте покупателю в WhatsApp. Ниже — заказы, сохранённые в NurCRM (на сайте — раздел «Закупки»).",
                $"Кардар витринада себет түзүп, «Оформить заказ» («Заказ берүү») баскычын басат — NurCRM сайты заказдын тексти менен WhatsApp'ты {phone} номерине ачат. Азыр мындай заказ NurCRM серверине жазылбайт, ошондуктан төмөнкү тизмеде жок: кардарга WhatsApp'та жооп бериңиз. Төмөндө — NurCRMде сакталган заказдар (сайтта — «Сатып алуулар» бөлүмү).",
                $"A customer fills a cart on the showcase and taps “Оформить заказ” (Place order) — the NurCRM site opens WhatsApp with the order text to {phone}. Such orders are not saved on the NurCRM server yet, so they are not in the list below: reply to the customer in WhatsApp. Below are the orders stored in NurCRM (the “Purchases” section on the website).",
                $"Müşteri vitrinde sepetini doldurur ve «Оформить заказ» (Sipariş ver) düğmesine basar — NurCRM sitesi sipariş metniyle WhatsApp'ı {phone} numarasına açar. Bu tür siparişler henüz NurCRM sunucusuna kaydedilmiyor, bu yüzden aşağıdaki listede yok: müşteriye WhatsApp'tan yanıt verin. Aşağıda NurCRM'de kayıtlı siparişler var (sitede «Satın almalar» bölümü).",
                $"Xaridor vitrinada savatni to'ldirib, «Оформить заказ» (Buyurtma berish) tugmasini bosadi — NurCRM sayti buyurtma matni bilan WhatsApp'ni {phone} raqamiga ochadi. Hozircha bunday buyurtma NurCRM serveriga yozilmaydi, shuning uchun quyidagi ro'yxatda yo'q: xaridorga WhatsApp'da javob bering. Quyida — NurCRMda saqlangan buyurtmalar (saytda — «Xaridlar» bo'limi).")
            : Tr.T("Номер WhatsApp для заказов не задан — кнопка заказа на витрине не работает. Задайте номер в «Настройки сайта». Ниже — заказы, сохранённые в NurCRM (на сайте — раздел «Закупки»).",
                "Заказдар үчүн WhatsApp номери коюлган эмес — витринадагы заказ баскычы иштебейт. Номерди «Сайттын жөндөөлөрү» бөлүмүндө коюңуз. Төмөндө — NurCRMде сакталган заказдар (сайтта — «Сатып алуулар» бөлүмү).",
                "No WhatsApp number for orders is set — the order button on the showcase doesn't work. Set the number in “Website settings”. Below are the orders stored in NurCRM (the “Purchases” section on the website).",
                "Siparişler için WhatsApp numarası belirlenmemiş — vitrindeki sipariş düğmesi çalışmıyor. Numarayı «Web sitesi ayarları»nda belirleyin. Aşağıda NurCRM'de kayıtlı siparişler var (sitede «Satın almalar» bölümü).",
                "Buyurtmalar uchun WhatsApp raqami kiritilmagan — vitrinadagi buyurtma tugmasi ishlamaydi. Raqamni «Sayt sozlamalari»da kiriting. Quyida — NurCRMda saqlangan buyurtmalar (saytda — «Xaridlar» bo'limi).");
        var phoneCheck = ShowcaseApiService.CheckShowcasePhone(phone);
        if (phone.Length > 0 && (phoneCheck.Warning ?? phoneCheck.Error) is { } problem)
        {
            SetDot("BrushWarning");
            how = problem + " " + how;
        }
        ShowcaseHowText.Text = how;
    }

    private void SetDot(string brushKey) => UseBrush(ShowcaseStateDot, Border.BackgroundProperty, brushKey);

    private void RenderFilters()
    {
        var all = _orders.Count;
        var n = _orders.Count(o => o.Status == ShowcaseApiService.StatusNew);
        var p = _orders.Count(o => o.Status == ShowcaseApiService.StatusPending);
        var c = _orders.Count(o => o.Status == ShowcaseApiService.StatusCompleted);
        FilterAllButton.Content = Tr.T("Все", "Баары", "All", "Tümü", "Barchasi") + $" · {all}";
        FilterNewButton.Content = Tr.T("Новые", "Жаңылар", "New", "Yeni", "Yangi") + $" · {n}";
        FilterPendingButton.Content = Tr.T("В процессе", "Иштелүүдө", "In progress", "İşlemde", "Jarayonda") + $" · {p}";
        FilterCompletedButton.Content = Tr.T("Завершённые", "Аякталгандар", "Completed", "Tamamlanan", "Yakunlangan") + $" · {c}";
        foreach (var button in new[] { FilterAllButton, FilterNewButton, FilterPendingButton, FilterCompletedButton })
            button.Classes.Set("active", Equals(button.Tag, _filter));
    }

    private IEnumerable<SiteOrder> Filtered() =>
        _filter == "all" ? _orders : _orders.Where(o => o.Status == _filter);

    private void RenderList()
    {
        OrdersList.Children.Clear();
        var shown = Filtered().ToList();
        foreach (var order in shown)
            OrdersList.Children.Add(BuildRow(order));

        EmptyText.IsVisible = shown.Count == 0;
        EmptyText.Text = _accessError
            ?? (_offline && _lastSuccess == null
                ? Tr.T("Нет связи с сервером NurCRM. Заказы появятся, когда связь вернётся.",
                    "NurCRM сервери менен байланыш жок. Заказдар байланыш калыбына келгенде чыгат.",
                    "No connection to the NurCRM server. Orders will appear when the connection is back.",
                    "NurCRM sunucusuyla bağlantı yok. Siparişler bağlantı geri geldiğinde görünecek.",
                    "NurCRM serveri bilan aloqa yo'q. Buyurtmalar aloqa tiklanganda chiqadi.")
                : _lastSuccess == null
                    ? Tr.T("Загружаю заказы…", "Заказдар жүктөлүүдө…", "Loading orders…", "Siparişler yükleniyor…", "Buyurtmalar yuklanmoqda…")
                    : _orders.Count == 0
                        ? Tr.T("В NurCRM пока нет ни одного заказа.", "NurCRMде азырынча бир да заказ жок.", "There are no orders in NurCRM yet.",
                            "NurCRM'de henüz hiç sipariş yok.", "NurCRMda hozircha birorta ham buyurtma yo'q.")
                        : Tr.T("С этим статусом заказов нет.", "Бул статуста заказ жок.", "No orders with this status.", "Bu durumda sipariş yok.", "Bu holatda buyurtma yo'q."));
    }

    private Control BuildRow(SiteOrder order)
    {
        var row = new Border { Classes = { "orderRow" } };
        row.Classes.Set("isNew", order.IsNew);
        row.Classes.Set("selected", string.Equals(order.Id, _selectedId, StringComparison.OrdinalIgnoreCase));

        var stack = new StackPanel { Spacing = 3 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var number = new TextBlock
        {
            Text = NumberText(order),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        UseBrush(number, TextBlock.ForegroundProperty, "BrushText");
        top.Children.Add(number);

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (_arrivedIds.Contains(order.Id))
            pills.Children.Add(Pill(Tr.T("Только что", "Жаңы эле", "Just now", "Az önce", "Hozirgina"), "BrushAccent", "BrushAccentForeground"));
        pills.Children.Add(StatusPill(order.Status));
        Grid.SetColumn(pills, 1);
        top.Children.Add(pills);
        stack.Children.Add(top);

        var who = string.Join(" · ", new[] { order.CustomerName, order.Phone }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (who.Length > 0)
        {
            var whoText = new TextBlock { Text = who, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            UseBrush(whoText, TextBlock.ForegroundProperty, "BrushText");
            stack.Children.Add(whoText);
        }

        var meta = string.Join(" · ", new[]
        {
            OrderDateText(order),
            order.Items.Count > 0
                ? Tr.T($"{order.Items.Count} поз.", $"{order.Items.Count} позиция", order.Items.Count == 1 ? "1 item" : $"{order.Items.Count} items",
                    $"{order.Items.Count} kalem", $"{order.Items.Count} ta pozitsiya")
                : "",
            $"{Money(order.Total)} {Som}",
        }.Where(s => s.Length > 0));
        var metaText = new TextBlock { Text = meta, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        UseBrush(metaText, TextBlock.ForegroundProperty, "BrushTextSoft");
        stack.Children.Add(metaText);

        row.Child = stack;
        row.Tapped += (_, _) => Select(order.Id);
        return row;
    }

    private void Select(string id)
    {
        _selectedId = id;
        RenderList();
        RenderDetail();
    }

    private void RenderDetail()
    {
        DetailPanel.Children.Clear();
        var order = _orders.FirstOrDefault(o => string.Equals(o.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        DetailEmptyText.IsVisible = order == null;
        DetailEmptyText.Text = _orders.Count > 0
            ? Tr.T("Выберите заказ слева — здесь появятся покупатель, товары и кнопки статуса.",
                "Сол жактан заказды тандаңыз — бул жерде кардар, товарлар жана статус баскычтары чыгат.",
                "Select an order on the left to see the customer, items and status buttons here.",
                "Soldan bir sipariş seçin — müşteri, ürünler ve durum düğmeleri burada görünür.",
                "Chapdan buyurtmani tanlang — bu yerda xaridor, mahsulotlar va holat tugmalari chiqadi.")
            : Tr.T("Когда в NurCRM появится заказ, здесь будет его карточка: покупатель, товары и кнопки статуса.",
                "NurCRMде заказ пайда болгондо, бул жерде анын карточкасы чыгат: кардар, товарлар жана статус баскычтары.",
                "When an order appears in NurCRM, its card will be here: customer, items and status buttons.",
                "NurCRM'de bir sipariş göründüğünde kartı burada olacak: müşteri, ürünler ve durum düğmeleri.",
                "NurCRMda buyurtma paydo bo'lganda, bu yerda uning kartochkasi chiqadi: xaridor, mahsulotlar va holat tugmalari.");
        if (order == null)
            return;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        var title = new TextBlock
        {
            Text = Tr.T("Заказ ", "Заказ ", "Order ", "Sipariş ", "Buyurtma ") + NumberText(order),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        UseBrush(title, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(title);
        var pill = StatusPill(order.Status);
        pill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pill, 1);
        head.Children.Add(pill);
        DetailPanel.Children.Add(head);

        var stamps = new List<string>();
        if (order.CreatedAt is { } created)
            stamps.Add(Tr.T("создан ", "түзүлгөн ", "created ", "oluşturuldu ", "yaratilgan ") + created.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        if (order.UpdatedAt is { } updated && order.CreatedAt is { } c0 && updated - c0 > TimeSpan.FromSeconds(5))
            stamps.Add(Tr.T("изменён ", "өзгөртүлгөн ", "changed ", "değiştirildi ", "o'zgartirilgan ") + updated.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        if (stamps.Count > 0)
            DetailPanel.Children.Add(new TextBlock { Text = string.Join(" · ", stamps), Classes = { "soft" }, Margin = new Thickness(0, 0, 0, 12) });

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 4) };
        var rowIndex = 0;

        void Field(string label, string? value, Control? extra = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, Classes = { "label" } };
            Grid.SetRow(l, rowIndex);
            fields.Children.Add(l);
            var valuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            valuePanel.Children.Add(new TextBlock { Text = value, Classes = { "value" } });
            if (extra != null)
                valuePanel.Children.Add(extra);
            Grid.SetRow(valuePanel, rowIndex);
            Grid.SetColumn(valuePanel, 1);
            fields.Children.Add(valuePanel);
            rowIndex++;
        }

        Field(Tr.T("Покупатель", "Кардар", "Customer", "Müşteri", "Xaridor"), order.CustomerName);
        Field(Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), order.Phone, PhoneButtons(order.Phone));
        Field(Tr.T("Дата заказа", "Заказдын күнү", "Order date", "Sipariş tarihi", "Buyurtma sanasi"), OrderDateText(order));
        Field(Tr.T("Отдел", "Бөлүм", "Department", "Departman", "Bo'lim"), order.Department);
        Field(Tr.T("Получение", "Алуу", "Delivery", "Teslimat", "Olish"), order.Delivery);
        Field(Tr.T("Адрес", "Дарек", "Address", "Adres", "Manzil"), order.Address);
        Field(Tr.T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), order.Comment);
        Field(Tr.T("Источник", "Булак", "Source", "Kaynak", "Manba"), order.Source);
        DetailPanel.Children.Add(fields);

        DetailPanel.Children.Add(BuildItemsTable(order));

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (text, status, accent) in ActionsFor(order.Status))
        {
            var button = new Button
            {
                Content = text,
                Classes = { "period" },
                IsEnabled = !_busy,
                Margin = new Thickness(0, 0, 8, 8),
            };
            button.Classes.Set("accent", accent);
            button.Click += async (_, _) => await ChangeStatusAsync(order, status).ConfigureAwait(true);
            actions.Children.Add(button);
        }
        DetailPanel.Children.Add(actions);
        DetailPanel.Children.Add(new TextBlock
        {
            Text = Tr.T("В NurCRM у заказа три статуса: «Новый», «В процессе», «Завершён» — отдельной отмены нет.",
                "NurCRMде заказдын үч статусу бар: «Жаңы», «Иштелүүдө», «Аякталды» — өзүнчө жокко чыгаруу жок.",
                "Orders in NurCRM have three statuses: “New”, “In progress”, “Completed” — there is no separate cancel.",
                "NurCRM'de siparişin üç durumu vardır: «Yeni», «İşlemde», «Tamamlandı» — ayrı bir iptal yoktur.",
                "NurCRMda buyurtmaning uchta holati bor: «Yangi», «Jarayonda», «Yakunlangan» — alohida bekor qilish yo'q."),
            Classes = { "soft" },
        });

        _ = LoadProductNamesAsync(order);
    }

    private Control BuildItemsTable(SiteOrder order)
    {
        var box = new StackPanel { Spacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        var caption = new TextBlock
        {
            Text = Tr.T("Товары", "Товарлар", "Items", "Ürünler", "Mahsulotlar"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        UseBrush(caption, TextBlock.ForegroundProperty, "BrushText");
        box.Children.Add(caption);

        const string columns = "*,90,120,130";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(0, 0, 0, 6) };
        AddCell(header, 0, Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), soft: true);
        AddCell(header, 1, Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni"), soft: true, right: true);
        AddCell(header, 2, Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx"), soft: true, right: true);
        AddCell(header, 3, Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), soft: true, right: true);
        box.Children.Add(header);

        if (order.Items.Count == 0)
        {
            box.Children.Add(new TextBlock { Text = Tr.T("Строк товаров нет", "Товар саптары жок", "No item lines", "Ürün satırı yok", "Mahsulot qatorlari yo'q"), Classes = { "soft" } });
            return box;
        }

        foreach (var item in order.Items)
        {
            var line = new Border { Padding = new Thickness(0, 7), BorderThickness = new Thickness(0, 1, 0, 0) };
            UseBrush(line, Border.BorderBrushProperty, "BrushBorder");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
            AddCell(grid, 0, ItemName(item));
            AddCell(grid, 1, Qty(item.Quantity), right: true);
            AddCell(grid, 2, Money(item.Price), right: true);
            AddCell(grid, 3, Money(item.Total), right: true, bold: true);
            line.Child = grid;
            box.Children.Add(line);
        }

        var totalLine = new Border { Padding = new Thickness(0, 9, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
        UseBrush(totalLine, Border.BorderBrushProperty, "BrushBorder");
        var total = new TextBlock
        {
            Text = Tr.T($"Итого: {Qty(order.TotalQuantity)} шт. · {Money(order.Total)} сом",
                $"Жыйынтык: {Qty(order.TotalQuantity)} даана · {Money(order.Total)} сом",
                $"Total: {Qty(order.TotalQuantity)} pcs · {Money(order.Total)} som",
                $"Toplam: {Qty(order.TotalQuantity)} adet · {Money(order.Total)} som",
                $"Jami: {Qty(order.TotalQuantity)} dona · {Money(order.Total)} so'm"),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        UseBrush(total, TextBlock.ForegroundProperty, "BrushText");
        totalLine.Child = total;
        box.Children.Add(totalLine);
        return box;
    }

    private void AddCell(Grid grid, int column, string text, bool soft = false, bool right = false, bool bold = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = soft ? 12 : 13.5,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            TextWrapping = column == 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        UseBrush(cell, TextBlock.ForegroundProperty, soft ? "BrushTextSoft" : "BrushText");
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private Control? PhoneButtons(string phone)
    {
        var digits = ShowcaseApiService.WhatsAppDigits(phone);
        if (digits.Length < 9)
            return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        var wa = new Button { Content = "WhatsApp", Padding = new Thickness(10, 3), FontSize = 12, CornerRadius = new CornerRadius(8) };
        ToolTip.SetTip(wa, Tr.T("Написать покупателю в WhatsApp", "Кардарга WhatsApp'та жазуу", "Message the customer on WhatsApp", "Müşteriye WhatsApp'tan yaz", "Xaridorga WhatsApp'da yozish"));
        wa.Click += (_, _) => OpenUrl("https://wa.me/" + digits);
        panel.Children.Add(wa);
        var copy = new Button { Content = Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"), Padding = new Thickness(10, 3), FontSize = 12, CornerRadius = new CornerRadius(8) };
        copy.Click += async (_, _) => await CopyAsync(phone).ConfigureAwait(true);
        panel.Children.Add(copy);
        return panel;
    }

    private static IEnumerable<(string Text, string Status, bool Accent)> ActionsFor(string status)
    {
        var accept = Tr.T("Принять в работу", "Ишке алуу", "Accept", "İşleme al", "Ishga qabul qilish");
        var complete = Tr.T("Завершить", "Аяктоо", "Complete", "Tamamla", "Yakunlash");
        switch (status)
        {
            case ShowcaseApiService.StatusNew:
                yield return (accept, ShowcaseApiService.StatusPending, true);
                yield return (complete, ShowcaseApiService.StatusCompleted, false);
                break;
            case ShowcaseApiService.StatusPending:
                yield return (complete, ShowcaseApiService.StatusCompleted, true);
                yield return (Tr.T("Вернуть в новые", "Жаңыларга кайтаруу", "Back to new", "Yeniye geri al", "Yangilarga qaytarish"), ShowcaseApiService.StatusNew, false);
                break;
            case ShowcaseApiService.StatusCompleted:
                yield return (Tr.T("Вернуть в работу", "Ишке кайтаруу", "Back to in progress", "İşleme geri al", "Ishga qaytarish"), ShowcaseApiService.StatusPending, false);
                break;
            default:
                // Статус, которого нет в схеме сервера, — даём только «Принять» и «Завершить».
                yield return (accept, ShowcaseApiService.StatusPending, true);
                yield return (complete, ShowcaseApiService.StatusCompleted, false);
                break;
        }
    }

    private async Task ChangeStatusAsync(SiteOrder order, string status)
    {
        if (_busy)
            return;
        _busy = true;
        RenderDetail();
        ShowNotice(null, true);
        try
        {
            var updated = await _api.SetOrderStatusAsync(order.Id, status, _lifetime.Token).ConfigureAwait(true);
            var index = _orders.FindIndex(o => string.Equals(o.Id, order.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _orders[index] = updated;
            ShowcaseApiService.PublishNewOrdersCount(_orders.Count(o => o.IsNew));
            ShowNotice(Tr.T($"Заказ {NumberText(updated)}: «{StatusLabel(updated.Status)}»", $"Заказ {NumberText(updated)}: «{StatusLabel(updated.Status)}»",
                $"Order {NumberText(updated)}: “{StatusLabel(updated.Status)}”", $"Sipariş {NumberText(updated)}: «{StatusLabel(updated.Status)}»",
                $"Buyurtma {NumberText(updated)}: «{StatusLabel(updated.Status)}»"), success: true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            ShowNotice(Tr.T("Этого заказа на сервере уже нет — список обновлён.", "Бул заказ серверде жок — тизме жаңыртылды.",
                "This order is no longer on the server — the list has been refreshed.", "Bu sipariş artık sunucuda yok — liste yenilendi.",
                "Bu buyurtma serverda endi yo'q — ro'yxat yangilandi."), success: false);
            _busy = false;
            await ReloadAsync(manual: true).ConfigureAwait(true);
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Заказ {order.Id}: статус не сменён: {ex.Message}", "SHOWCASE");
            ShowNotice(Tr.T("Статус не сменён", "Статус өзгөргөн жок", "Status not changed", "Durum değiştirilmedi", "Holat o'zgarmadi")
                       + ": " + (IsNoConnection(ex)
                           ? Tr.T("нет связи с сервером", "сервер менен байланыш жок", "no connection to the server", "sunucuyla bağlantı yok", "server bilan aloqa yo'q")
                           : ex.Message), success: false);
        }
        finally
        {
            _busy = false;
        }

        RenderFilters();
        RenderList();
        RenderDetail();
    }

    private async Task LoadProductNamesAsync(SiteOrder order)
    {
        var missing = order.Items
            .Where(i => string.IsNullOrWhiteSpace(i.Name) && i.ProductId.Length > 0 && !ProductNames.ContainsKey(i.ProductId) && _namesLoading.Add(i.ProductId))
            .Select(i => i.ProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
            return;

        var changed = false;
        foreach (var id in missing)
        {
            try
            {
                // Сначала каталог программы (без запроса), потом карточка товара на сервере.
                var local = LocalProductRepository.Instance.TryGetTileById(id)?.Title;
                if (!string.IsNullOrWhiteSpace(local))
                {
                    ProductNames[id] = local;
                    changed = true;
                    continue;
                }

                var detail = await ApiThrottle.RunBulkAsync(() => App.CatalogApi.ProductsDetailAsync(id, _lifetime.Token), _lifetime.Token).ConfigureAwait(true);
                if (detail is { } d && d.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    ProductNames[id] = name.GetString()!;
                    changed = true;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Заказы с сайта: название товара {id} не получено: {ex.Message}", "SHOWCASE");
            }
            finally
            {
                _namesLoading.Remove(id);
            }
        }

        if (changed && string.Equals(_selectedId, order.Id, StringComparison.OrdinalIgnoreCase))
            RenderDetail();
    }

    private static string ItemName(SiteOrderItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name))
            return item.Name!;
        if (ProductNames.TryGetValue(item.ProductId, out var name))
            return name;
        return Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + (item.ProductId.Length >= 8 ? " " + item.ProductId[..8] : "");
    }

    private void RenderUpdated()
    {
        if (_offline)
        {
            UseBrush(LiveDot, Shape.FillProperty, "BrushWarning");
            UpdatedText.Text = _lastSuccess is { } at
                ? Tr.T($"Нет связи · данные на {at:HH:mm}", $"Байланыш жок · маалымат {at:HH:mm} боюнча", $"Offline · data as of {at:HH:mm}",
                    $"Bağlantı yok · veriler {at:HH:mm} itibarıyla", $"Aloqa yo'q · ma'lumotlar {at:HH:mm} holatiga ko'ra")
                : Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q");
            return;
        }

        UseBrush(LiveDot, Shape.FillProperty, _accessError != null ? "BrushWarning" : "BrushSuccess");
        UpdatedText.Text = _lastSuccess is { } now
            ? Tr.T($"Обновлено в {now:HH:mm}", $"{now:HH:mm} жаңыртылды", $"Updated at {now:HH:mm}", $"Güncellendi: {now:HH:mm}", $"Yangilandi: {now:HH:mm}")
            : "";
    }

    private void ShowNotice(string? message, bool success)
    {
        NoticeBox.IsVisible = !string.IsNullOrWhiteSpace(message);
        NoticeText.Text = message ?? "";
        NoticeBanner.Apply(NoticeBox, NoticeText, success);
    }

    // ------------------------------------------------------------------ кнопки

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await ReloadAsync(manual: true).ConfigureAwait(true);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Filter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter } || filter == _filter)
            return;
        _filter = filter;
        RenderFilters();
        RenderList();
    }

    private void OpenShowcase_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is { Slug.Length: > 0 } s)
            OpenUrl(ShowcaseApiService.CatalogUrl(s.Slug));
    }

    private async void CopyLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is { Slug.Length: > 0 } s)
            await CopyAsync(ShowcaseApiService.CatalogUrl(s.Slug)).ConfigureAwait(true);
    }

    private void SiteSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is OwnerShellWindow shell)
        {
            shell.OpenSiteSettings();
            return;
        }

        new SiteSettingsWindow().Show(this);
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
                ShowNotice(Tr.T($"Скопировано: {text}", $"Көчүрүлдү: {text}", $"Copied: {text}", $"Kopyalandı: {text}", $"Nusxalandi: {text}"), success: true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Заказы с сайта: не скопировано: {ex.Message}", "SHOWCASE");
        }
    }

    // ------------------------------------------------------------------ помощники

    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не открылась ссылка {url}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>401 — вход в NurCRM больше не действует (а не «нет права»).</summary>
    internal static string SessionEndedText => Tr.T(
        "Сессия NurCRM закончилась: выйдите из учётной записи (Настройки → Аккаунт → «Выйти») и войдите снова.",
        "NurCRM сессиясы бүттү: каттоо эсебинен чыгыңыз (Жөндөөлөр → Аккаунт → «Чыгуу») жана кайра кириңиз.",
        "Your NurCRM session has ended: sign out (Settings → Account → “Sign out”) and sign in again.",
        "NurCRM oturumunuz sona erdi: hesaptan çıkış yapın (Ayarlar → Hesap → «Çıkış») ve tekrar giriş yapın.",
        "NurCRM seansi tugadi: hisobdan chiqing (Sozlamalar → Akkaunt → «Chiqish») va qayta kiring.");

    /// <summary>Нет связи (а не отказ сервера): сеть, тайм-аут, 5xx.</summary>
    internal static bool IsNoConnection(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException
        || ex is ApiException { StatusCode: null or >= 500 };

    private static string NumberText(SiteOrder order) =>
        order.OrderNumber.Length == 0 ? "—" : order.OrderNumber.StartsWith('№') ? order.OrderNumber : "№" + order.OrderNumber;

    private static string OrderDateText(SiteOrder order)
    {
        if (DateTime.TryParseExact(order.DateOrdered, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return order.CreatedAt?.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? order.DateOrdered;
    }

    internal static string StatusLabel(string status) => status switch
    {
        ShowcaseApiService.StatusNew => Tr.T("Новый", "Жаңы", "New", "Yeni", "Yangi"),
        ShowcaseApiService.StatusPending => Tr.T("В процессе", "Иштелүүдө", "In progress", "İşlemde", "Jarayonda"),
        ShowcaseApiService.StatusCompleted => Tr.T("Завершён", "Аякталды", "Completed", "Tamamlandı", "Yakunlangan"),
        "" => "—",
        var other => other,
    };

    private Border StatusPill(string status) => status switch
    {
        // Текст — обычным цветом темы: акцентный на светло-акцентном фоне почти не читался (жёлтый/синий).
        ShowcaseApiService.StatusNew => Pill(StatusLabel(status), "BrushAccentSoft", "BrushText"),
        ShowcaseApiService.StatusPending => Pill(StatusLabel(status), "BrushWarningSoft", "BrushWarning"),
        ShowcaseApiService.StatusCompleted => Pill(StatusLabel(status), "BrushSuccessSoft", "BrushSuccess"),
        _ => Pill(StatusLabel(status), "BrushInputAlt", "BrushTextSoft"),
    };

    private Border Pill(string text, string backgroundKey, string foregroundKey)
    {
        var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        UseBrush(pill, Border.BackgroundProperty, backgroundKey);
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        UseBrush(label, TextBlock.ForegroundProperty, foregroundKey);
        pill.Child = label;
        return pill;
    }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    /// <summary>Сумма без «,00» у целых: 1 250 и 1 250,50.</summary>
    internal static string Money(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.005 ? value.ToString("N0", Ru) : value.ToString("N2", Ru);

    private static string Qty(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0005 ? value.ToString("N0", Ru) : value.ToString("0.###", Ru);

    /// <summary>Цвет из темы с подпиской — перекрашивается при смене светлой/тёмной темы.</summary>
    private void UseBrush(AvaloniaObject target, AvaloniaProperty property, string resourceKey) =>
        target.Bind(property, this.GetResourceObservable(resourceKey));
}
