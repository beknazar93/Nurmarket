using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-08, ответ сервера Умара п. 31 (Б-30): «Журнал сообщений» бота покупателям (GET main/telegram-bot/outbox/?client=&amp;status=&amp;kind=, владельцу и админу)
/// и «Тихие часы» (PATCH users/company/ {"quiet_hours":{from,to,tz}}; по умолчанию 21:00–09:00 Asia/Bishkek). Открывается кнопкой в разделе «Телеграм-бот» программы владельца.
/// Формат строк журнала сервер не расписал — разбор терпим к неизвестным полям (ClientMessagingApi.ParseOutbox). Собрано в коде, помещается на 1024×600.</summary>
public sealed class BotMessagesWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ClientMessagingApi? _api;
    private readonly TextBox _from = new() { Width = 78, MaxLength = 5, TextAlignment = TextAlignment.Center };
    private readonly TextBox _to = new() { Width = 78, MaxLength = 5, TextAlignment = TextAlignment.Center };
    private readonly ComboBox _tz = new() { MinWidth = 180 };
    private readonly Button _saveQuiet = new() { Classes = { "sec-outline" } };
    private readonly TextBlock _quietStatus = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _client = new() { Width = 200, Watermark = T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz") };
    private readonly TextBox _status = new() { Width = 130, Watermark = T("Статус", "Статус", "Status", "Durum", "Holat") };
    private readonly TextBox _kind = new() { Width = 130, Watermark = T("Вид", "Түрү", "Kind", "Tür", "Turi") };
    private readonly Button _apply = new() { Classes = { "sec-primary" } };
    private readonly Button _prev = new() { Classes = { "sec-outline" } };
    private readonly Button _next = new() { Classes = { "sec-outline" } };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _info = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private int _page = 1;
    private bool _loading;
    private QuietHours _quiet = QuietHours.Default;

    public BotMessagesWindow() : this(App.AppHost?.Services.GetService(typeof(ClientMessagingApi)) as ClientMessagingApi)
    {
    }

    public BotMessagesWindow(ClientMessagingApi? api)
    {
        _api = api;
        Title = T("Журнал сообщений бота", "Боттун билдирүүлөр журналы", "Bot message log", "Bot mesaj günlüğü", "Bot xabarlari jurnali");
        Width = 1000;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(this, BackgroundProperty, "BrushWindowBackdrop");

        _saveQuiet.Content = T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
        _apply.Content = T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish");
        _prev.Content = "‹ " + T("Назад", "Артка", "Back", "Geri", "Orqaga");
        _next.Content = T("Дальше", "Кийинки", "Next", "İleri", "Keyingi") + " ›";
        foreach (var b in new[] { _saveQuiet, _apply, _prev, _next })
            Avalonia.Automation.AutomationProperties.SetName(b, b.Content as string ?? "");
        Avalonia.Automation.AutomationProperties.SetName(_from, T("Тихие часы: с", "Тынч сааттар: баштап", "Quiet hours: from", "Sessiz saatler: başlangıç", "Tinch soatlar: dan"));
        Avalonia.Automation.AutomationProperties.SetName(_to, T("Тихие часы: до", "Тынч сааттар: чейин", "Quiet hours: to", "Sessiz saatler: bitiş", "Tinch soatlar: gacha"));
        foreach (var zone in QuietHours.TimeZones)
            _tz.Items.Add(zone);
        Bind(_info, TextBlock.ForegroundProperty, "BrushTextSoft");
        Bind(_quietStatus, TextBlock.ForegroundProperty, "BrushTextSoft");
        Bind(_pageText, TextBlock.ForegroundProperty, "BrushTextSoft");

        _saveQuiet.Click += async (_, _) => await SaveQuietAsync().ConfigureAwait(true);
        _apply.Click += async (_, _) => { _page = 1; await LoadAsync().ConfigureAwait(true); };
        _prev.Click += async (_, _) => { if (_page > 1) { _page--; await LoadAsync().ConfigureAwait(true); } };
        _next.Click += async (_, _) => { _page++; await LoadAsync().ConfigureAwait(true); };
        foreach (var box in new[] { _client, _status, _kind })
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; _page = 1; await LoadAsync().ConfigureAwait(true); }
            };

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var title = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold };
        Bind(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        // Тихие часы
        var quiet = new StackPanel { Spacing = 6 };
        var qHead = new TextBlock { Text = T("Тихие часы: в это время сообщения покупателям откладываются до конца",
            "Тынч сааттар: бул убакытта сатып алуучуларга билдирүүлөр аягына чейин кечиктирилет",
            "Quiet hours: messages to customers are held until they end", "Sessiz saatler: bu saatlerde müşteri mesajları bitişe kadar ertelenir",
            "Tinch soatlar: bu vaqtda xaridorlarga xabarlar oxirigacha kechiktiriladi"), FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(qHead, TextBlock.ForegroundProperty, "BrushText");
        quiet.Children.Add(qHead);
        quiet.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Soft(T("с", "баштап", "from", "başlangıç", "dan")), _from, Soft(T("до", "чейин", "to", "bitiş", "gacha")), _to, _tz, _saveQuiet },
        });
        quiet.Children.Add(_quietStatus);
        var quietCard = Card(quiet);
        quietCard.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(quietCard, 1);
        root.Children.Add(quietCard);

        // Фильтры
        var filters = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { _client, _status, _kind, _apply })
        {
            c.Margin = new Thickness(0, 0, 8, 4);
            filters.Children.Add(c);
        }
        Grid.SetRow(filters, 2);
        filters.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(filters);

        // Таблица
        var table = new DockPanel();
        var head = Header();
        DockPanel.SetDock(head, Dock.Top);
        table.Children.Add(head);
        table.Children.Add(new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });
        var tableCard = Card(table);
        Grid.SetRow(tableCard, 3);
        root.Children.Add(tableCard);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        footer.Children.Add(_info);
        Grid.SetColumn(_prev, 1);
        Grid.SetColumn(_pageText, 2);
        Grid.SetColumn(_next, 3);
        _pageText.Margin = new Thickness(10, 0);
        footer.Children.Add(_prev);
        footer.Children.Add(_pageText);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 4);
        footer.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(footer);
        Content = root;

        Opened += (_, _) => { _ = LoadQuietAsync(); _ = LoadAsync(); };
        ShowQuiet(QuietHours.Default);
        _prev.IsEnabled = _next.IsEnabled = false;
    }

    private static void Bind(Control target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key));

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Bind(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private static Border Card(Control child)
    {
        var card = new Border { Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = child };
        Bind(card, Border.BackgroundProperty, "BrushPanel");
        Bind(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    private static readonly string Cols = "120,1.1*,90,100,2.6*";

    private static Grid Header()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(8, 0, 8, 6) };
        var names = new[] { T("Время", "Убакыт", "Time", "Zaman", "Vaqt"), T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("Вид", "Түрү", "Kind", "Tür", "Turi"),
            T("Статус", "Статус", "Status", "Durum", "Holat"), T("Текст / причина", "Текст / себеби", "Text / reason", "Metin / neden", "Matn / sabab") };
        for (var i = 0; i < names.Length; i++)
        {
            var t = new TextBlock { Text = names[i], FontSize = 12, FontWeight = FontWeight.SemiBold };
            Bind(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    private void ShowQuiet(QuietHours q)
    {
        _quiet = q;
        _from.Text = q.From;
        _to.Text = q.To;
        if (!_tz.Items.Contains(q.Tz))
            _tz.Items.Add(q.Tz);
        _tz.SelectedItem = q.Tz;
        _quietStatus.Text = q.IsDefault
            ? T("Сейчас: по умолчанию (21:00–09:00, Asia/Bishkek). Измените время и нажмите «Сохранить».",
                "Азыр: демейки боюнча (21:00–09:00, Asia/Bishkek). Убакытты өзгөртүп, «Сактоо» басыңыз.",
                "Now: default (21:00–09:00, Asia/Bishkek). Change the time and press “Save”.",
                "Şu an: varsayılan (21:00–09:00, Asia/Bishkek). Saati değiştirip «Kaydet»e basın.",
                "Hozir: odatiy (21:00–09:00, Asia/Bishkek). Vaqtni o'zgartirib «Saqlash»ni bosing.")
            : "";
    }

    private async Task LoadQuietAsync()
    {
        if (_api == null)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            ShowQuiet(await _api.GetQuietHoursAsync(cts.Token).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Тихие часы не получены: {ex.GetType().Name}", "WARNING");
            _quietStatus.Text = T("Не удалось узнать текущие тихие часы: ", "Азыркы тынч сааттарды билүү мүмкүн болгон жок: ", "Could not read the current quiet hours: ",
                "Mevcut sessiz saatler okunamadı: ", "Joriy tinch soatlarni bilib bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex);
        }
    }

    private async Task SaveQuietAsync()
    {
        if (_api == null)
            return;
        var from = QuietHours.NormalizeTime(_from.Text);
        var to = QuietHours.NormalizeTime(_to.Text);
        if (from is null || to is null)
        {
            _quietStatus.Text = T("Время вводится как ЧЧ:ММ, например 21:00.", "Убакыт СС:ММ түрүндө жазылат, мисалы 21:00.", "Enter the time as HH:MM, e.g. 21:00.",
                "Saati SS:DD biçiminde girin, örneğin 21:00.", "Vaqtni SS:DD ko'rinishida kiriting, masalan 21:00.");
            return;
        }
        var value = new QuietHours(from, to, _tz.SelectedItem as string ?? _quiet.Tz, false);
        _saveQuiet.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await _api.PatchQuietHoursAsync(value, cts.Token).ConfigureAwait(true);
            ShowQuiet(value);
            _quietStatus.Text = T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi.");
        }
        catch (Exception ex)
        {
            _quietStatus.Text = (ex is ApiException { StatusCode: 403 }
                ? T("Менять тихие часы может только владелец.", "Тынч сааттарды ээси гана өзгөртө алат.", "Only the owner can change quiet hours.", "Sessiz saatleri yalnızca sahip değiştirebilir.", "Tinch soatlarni faqat egasi o'zgartira oladi.")
                : T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
        }
        finally
        {
            _saveQuiet.IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        if (_api == null || _loading)
            return;
        _loading = true;
        _apply.IsEnabled = false;
        _info.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var page = await _api.GetOutboxAsync(_client.Text, _status.Text, _kind.Text, _page, cts.Token).ConfigureAwait(true);
            Render(page);
        }
        catch (Exception ex)
        {
            _rows.Children.Clear();
            _prev.IsEnabled = _page > 1;
            _next.IsEnabled = false;
            _info.Text = ex is ApiException { StatusCode: 403 }
                ? T("Журнал сообщений доступен владельцу и администратору.", "Билдирүүлөр журналы ээсине жана администраторго жеткиликтүү.", "The message log is available to the owner and administrator.",
                    "Mesaj günlüğü sahip ve yöneticiye açıktır.", "Xabarlar jurnali egasi va administratorga ochiq.")
                : ex is ApiException { StatusCode: 404 }
                    ? T("Сервер пока не отдаёт журнал сообщений.", "Сервер азырынча билдирүүлөр журналын бербейт.", "The server does not provide the message log yet.", "Sunucu henüz mesaj günlüğünü vermiyor.", "Server hozircha xabarlar jurnalini bermaydi.")
                    : T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex);
        }
        finally
        {
            _loading = false;
            _apply.IsEnabled = true;
        }
    }

    private void Render(OutboxPage page)
    {
        _rows.Children.Clear();
        _prev.IsEnabled = _page > 1;
        _next.IsEnabled = page.HasNext;
        _pageText.Text = T("Страница ", "Барак ", "Page ", "Sayfa ", "Sahifa ") + _page.ToString(CultureInfo.InvariantCulture);
        if (page.Rows.Count == 0)
        {
            _info.Text = T("Сообщений по этим условиям нет. Если клиенты не дали согласие, бот им не пишет.",
                "Бул шарттар боюнча билдирүүлөр жок. Кардарлар макулдук бербесе, бот аларга жазбайт.",
                "No messages match. If clients have not given consent, the bot does not write to them.",
                "Bu koşullarla mesaj yok. Müşteriler onay vermediyse bot onlara yazmaz.",
                "Bu shartlar bo'yicha xabarlar yo'q. Mijozlar rozilik bermagan bo'lsa, bot ularga yozmaydi.");
            return;
        }
        _info.Text = page.Count is { } n
            ? T($"Всего: {n}", $"Бардыгы: {n}", $"Total: {n}", $"Toplam: {n}", $"Jami: {n}")
            : T($"Показано: {page.Rows.Count}", $"Көрсөтүлдү: {page.Rows.Count}", $"Shown: {page.Rows.Count}", $"Gösterilen: {page.Rows.Count}", $"Ko'rsatildi: {page.Rows.Count}");
        foreach (var m in page.Rows)
            _rows.Children.Add(Row(m));
    }

    private static Border Row(OutboxMessage m)
    {
        var detail = m.Text;
        if (!string.IsNullOrWhiteSpace(m.Reason))
            detail += (detail.Length > 0 ? "\n" : "") + m.Reason;
        if (m.SendAfter is { } after)
            detail += (detail.Length > 0 ? "\n" : "") + T("отложено до ", "кечиктирилди: ", "held until ", "ertelendi: ", "kechiktirildi: ") + after.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        var cells = new[]
        {
            m.At is { } at ? at.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) : "",
            m.ClientName, m.Kind, m.Status, detail,
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols) };
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            Bind(t, TextBlock.ForegroundProperty, i == 4 ? "BrushText" : "BrushTextSoft");
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        var row = new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = g };
        Bind(row, Border.BackgroundProperty, "BrushPanelSoft");
        Bind(row, Border.BorderBrushProperty, "BrushBorder");
        return row;
    }
}
