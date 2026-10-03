using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-01, владелец: «в админке где аналитика по боту — его обращения, клиенты, заказы?»
/// Раздел программы владельца «Телеграм-бот»: сколько покупателей писали боту (сегодня, 7 и 30 дней),
/// сколько заказов оформлено через бота, список покупателей и лента обращений. Данные —
/// TelegramInquiryStore (общий файл кассы и программы владельца), обновление раз в 30 секунд.
/// Окно собрано в коде: разметки немного, а цвета берутся из темы (GetResourceObservable).
/// </summary>
public sealed class TelegramBotAnalyticsWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly StackPanel _customers = new() { Spacing = 6 };
    private readonly StackPanel _feed = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    // 2026-10-03, владелец: «индикатор ИИ на сервере добавь у клиентов» — бот на сервере, ИИ, связь, получатель.
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public TelegramBotAnalyticsWindow()
    {
        Title = Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot");
        Width = 1200;
        Height = 820;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        // 2026-10-01, владелец (скриншот): «сделай фиксированную высоту и добавь скролл» — шапка и плитки
        // на месте, а колонки «Покупатели» и «Последние обращения» занимают остаток высоты окна и
        // прокручиваются каждая сама, не утаскивая за собой всю страницу.
        var root = new Grid { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        var title = new TextBlock
        {
            Text = Tr.T("Обращения покупателей к боту", "Сатып алуучулардын ботко кайрылуулары", "Customer inquiries to the bot",
                "Bota gelen müşteri başvuruları", "Xaridorlarning botga murojaatlari"),
            FontSize = 22,
            FontWeight = FontWeight.Bold,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.Margin = new Thickness(0, 6, 0, 16);
        _tiles.Margin = new Thickness(0, 0, 0, 16);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_chips, 2);
        Grid.SetRow(_tiles, 3);
        root.Children.Add(title);
        root.Children.Add(_status);
        root.Children.Add(_chips);
        root.Children.Add(_tiles);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,16,3*") };
        columns.Children.Add(Card(Tr.T("Покупатели (30 дней)", "Сатып алуучулар (30 күн)", "Customers (30 days)", "Müşteriler (30 gün)", "Xaridorlar (30 kun)"), _customers, 0));
        columns.Children.Add(Card(Tr.T("Последние обращения", "Акыркы кайрылуулар", "Latest inquiries", "Son başvurular", "Oxirgi murojaatlar"), _feed, 2));
        Grid.SetRow(columns, 4);
        root.Children.Add(columns);

        Content = root;

        Opened += (_, _) =>
        {
            _ = RenderAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => _ = RenderAsync();
    }

    /// <summary>2026-10-01, ТЗ часть 5: бот на сервере NurCRM — обращения пишет сервер
    /// (GET /api/main/telegram-bot/inquiries/), а журнал этого компьютера больше не пополняется.
    /// Показываем обращения сервера вместе с записанными здесь до переноса.</summary>
    private List<TelegramInquiryStore.Entry>? _server;
    private bool _serverMode;
    private bool _loading;

    private async Task RenderAsync()
    {
        if (_loading)
            return;
        _loading = true;
        try
        {
            var api = App.AppHost?.Services.GetService(typeof(NurMarketKassa.Services.Api.ServerTelegramBotApi)) as NurMarketKassa.Services.Api.ServerTelegramBotApi;
            if (api is not null)
            {
                var settings = await api.GetSettingsAsync().ConfigureAwait(true);
                TelegramBotStatusChips.Fill(_chips, settings);
                _serverMode = settings?.IsServerMode == true;
                if (_serverMode)
                {
                    var rows = await api.GetInquiriesAsync(DateTime.Today.AddDays(-30), DateTime.Today).ConfigureAwait(true);
                    _server = rows.Select(r => new TelegramInquiryStore.Entry(
                        (r.At ?? DateTimeOffset.Now).LocalDateTime, r.ChatId,
                        string.IsNullOrWhiteSpace(r.Name) ? r.Username : r.Name,
                        r.IsVoice ? "🎤 " + r.Text : r.Text, r.OrderNumber)).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: обращения с сервера не получены ({ex.GetType().Name}).", "TELEGRAM");
        }
        finally
        {
            _loading = false;
        }
        Render();
    }

    private Border Card(string header, Control body, int column)
    {
        var head = new TextBlock { Text = header, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        DockPanel.SetDock(head, Dock.Top);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 10, 0),
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new DockPanel { LastChildFill = true, Children = { head, scroll } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetColumn(card, column);
        return card;
    }

    private void Render()
    {
        var all = TelegramInquiryStore.Load();
        if (_serverMode && _server is not null)
        {
            // До переноса на сервер обращения писались здесь — показываем и их, без повторов.
            var serverFrom = _server.Count > 0 ? _server.Min(e => e.At) : DateTime.MaxValue;
            all = all.Where(e => e.At < serverFrom).Concat(_server).ToList();
        }
        var today = all.Where(e => e.At.Date == DateTime.Today).ToList();
        var week = all.Where(e => e.At.Date > DateTime.Today.AddDays(-7)).ToList();
        var month = all.Where(e => e.At.Date > DateTime.Today.AddDays(-30)).ToList();

        var prefs = UserPreferences.Instance;
        _status.Text = (TelegramBotService.IsConfigured
                ? Tr.T($"Бот подключён: @{prefs.TelegramBotUsername}", $"Бот туташкан: @{prefs.TelegramBotUsername}", $"Bot connected: @{prefs.TelegramBotUsername}", $"Bot bağlı: @{prefs.TelegramBotUsername}", $"Bot ulangan: @{prefs.TelegramBotUsername}")
                : Tr.T("Бот не подключён — Настройки → Операции → «Телеграм-бот владельца».", "Бот туташкан эмес — Жөндөөлөр → Операциялар.", "Bot is not connected — Settings → Operations.", "Bot bağlı değil — Ayarlar → İşlemler.", "Bot ulanmagan — Sozlamalar → Operatsiyalar."))
            + " · " + (TelegramAiChat.IsConfigured
                ? Tr.T("ИИ-консультант включён", "ЖИ-кеңешчи күйүк", "AI assistant on", "YZ asistanı açık", "SI maslahatchi yoqilgan")
                : Tr.T("ИИ выключен (нет ключа)", "ЖИ өчүк (ачкыч жок)", "AI off (no key)", "YZ kapalı (anahtar yok)", "SI o'chirilgan (kalit yo'q)"))
            + " · " + (_serverMode
                ? Tr.T("бот работает на сервере NurCRM круглые сутки", "бот NurCRM серверинде күнү-түнү иштейт",
                    "the bot runs on the NurCRM server around the clock", "bot NurCRM sunucusunda günün her saati çalışır",
                    "bot NurCRM serverida kecha-kunduz ishlaydi")
                : Tr.T("бот отвечает, пока на компьютере магазина включена касса или программа владельца",
                    "бот дүкөндүн компьютеринде касса же ээсинин программасы күйүп турганда жооп берет",
                    "the bot replies while the till or owner app is running on the shop computer",
                    "bot, mağaza bilgisayarında kasa veya sahip programı açıkken yanıt verir",
                    "bot do'kon kompyuterida kassa yoki egasi dasturi yoqilganda javob beradi"));

        _tiles.Children.Clear();
        _tiles.Children.Add(Tile(Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"), today));
        _tiles.Children.Add(Tile(Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"), week));
        _tiles.Children.Add(Tile(Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun"), month));

        _customers.Children.Clear();
        var byCustomer = month.GroupBy(e => e.ChatId)
            .Select(g => (Name: g.Select(e => e.Name).LastOrDefault(n => !string.IsNullOrWhiteSpace(n)), Count: g.Count(),
                Orders: g.Count(e => e.OrderNumber != null), Last: g.Max(e => e.At)))
            .OrderByDescending(x => x.Last)
            .Take(40)
            .ToList();
        if (byCustomer.Count == 0)
            _customers.Children.Add(Soft(Tr.T("Покупатели пока не писали боту. Дайте им ссылку на бота — на чеке, в WhatsApp или QR-кодом у кассы.",
                "Сатып алуучулар ботко азырынча жазышкан жок. Ботко шилтемени чекте, WhatsApp'та же кассанын жанында QR-код менен бериңиз.",
                "No customers have written to the bot yet. Share the bot link — on receipts, in WhatsApp or as a QR code at the till.",
                "Henüz bota yazan müşteri yok. Bot bağlantısını fişte, WhatsApp'ta veya kasada QR kodla paylaşın.",
                "Xaridorlar hali botga yozmagan. Bot havolasini chekda, WhatsApp'da yoki kassa yonida QR-kod bilan bering.")));
        foreach (var c in byCustomer)
        {
            var name = string.IsNullOrWhiteSpace(c.Name) ? Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor") : c.Name!;
            var right = Tr.T($"{c.Count} сообщ.", $"{c.Count} билдирүү", $"{c.Count} msgs", $"{c.Count} mesaj", $"{c.Count} xabar")
                        + (c.Orders > 0 ? Tr.T($" · заказов {c.Orders}", $" · заказ {c.Orders}", $" · orders {c.Orders}", $" · sipariş {c.Orders}", $" · buyurtma {c.Orders}") : "")
                        + " · " + c.Last.ToString("dd.MM HH:mm", Ru);
            _customers.Children.Add(Row(name, right, c.Orders > 0));
        }

        _feed.Children.Clear();
        var recent = all.OrderByDescending(e => e.At).Take(60).ToList();
        if (recent.Count == 0)
            _feed.Children.Add(Soft(Tr.T("Обращений пока нет.", "Азырынча кайрылуулар жок.", "No inquiries yet.", "Henüz başvuru yok.", "Hozircha murojaatlar yo'q.")));
        foreach (var e in recent)
        {
            var who = string.IsNullOrWhiteSpace(e.Name) ? Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor") : e.Name!;
            var head = $"{e.At.ToString("dd.MM HH:mm", Ru)} · {who}" + (e.OrderNumber != null ? Tr.T($" · заказ №{e.OrderNumber}", $" · заказ №{e.OrderNumber}", $" · order #{e.OrderNumber}", $" · sipariş #{e.OrderNumber}", $" · buyurtma №{e.OrderNumber}") : "");
            _feed.Children.Add(Row(head, e.Text, e.OrderNumber != null, stacked: true));
        }
    }

    private Border Tile(string period, List<TelegramInquiryStore.Entry> list)
    {
        var people = list.Select(e => e.ChatId).Distinct().Count();
        var orders = list.Count(e => e.OrderNumber != null);
        var caption = new TextBlock { Text = period, FontSize = 12.5 };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var value = new TextBlock { Text = list.Count.ToString(Ru), FontSize = 26, FontWeight = FontWeight.Bold };
        Use(value, TextBlock.ForegroundProperty, "BrushText");
        var sub = new TextBlock
        {
            Text = Tr.T($"обращений · {people} чел. · заказов {orders}", $"кайрылуу · {people} адам · заказ {orders}",
                $"inquiries · {people} people · orders {orders}", $"başvuru · {people} kişi · sipariş {orders}", $"murojaat · {people} kishi · buyurtma {orders}"),
            FontSize = 12.5,
        };
        Use(sub, TextBlock.ForegroundProperty, "BrushTextSoft");
        var tile = new Border
        {
            Width = 260,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12),
            Child = new StackPanel { Spacing = 4, Children = { caption, value, sub } },
        };
        Use(tile, Border.BackgroundProperty, "BrushPanel");
        Use(tile, Border.BorderBrushProperty, "BrushBorder");
        return tile;
    }

    private Border Row(string left, string right, bool highlight, bool stacked = false)
    {
        var a = new TextBlock { Text = left, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(a, TextBlock.ForegroundProperty, "BrushText");
        var b = new TextBlock { Text = right, FontSize = 12.5, TextWrapping = stacked ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Use(b, TextBlock.ForegroundProperty, stacked ? "BrushText" : "BrushTextSoft");
        Control content;
        if (stacked)
        {
            Use(a, TextBlock.ForegroundProperty, "BrushTextSoft");
            content = new StackPanel { Spacing = 2, Children = { a, b } };
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(b, 1);
            b.Margin = new Thickness(12, 0, 0, 0);
            grid.Children.Add(a);
            grid.Children.Add(b);
            content = grid;
        }

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), BorderThickness = new Thickness(1), Child = content };
        Use(row, Border.BackgroundProperty, highlight ? "BrushAccentSoft" : "BrushPanelSoft");
        Use(row, Border.BorderBrushProperty, highlight ? "BrushAccent" : "BrushBorder");
        return row;
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
