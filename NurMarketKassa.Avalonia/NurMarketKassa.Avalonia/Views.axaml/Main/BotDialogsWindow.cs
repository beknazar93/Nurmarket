using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1»): «Диалоги бота» — покупатели, которые пишут боту в Telegram (GET telegram-bot/dialogs/): кто ждёт продавца, у кого диалог у продавца,
/// кому перезвонить. «Взять» — бот замолкает и отвечает владелец, «Вернуть боту» — бот снова отвечает (POST dialogs/&lt;chat_id&gt;/ {"action":"take"|"return"}). «Переписка» раскрывает последние сообщения и заказы диалога.
/// Отвечать покупателю владелец может из Telegram: «Ответить» на карточку диалога. Открывается при признаке сервера bot_handoff. Собрано в коде, помещается на 1024×600.</summary>
public sealed class BotDialogsWindow : Window
{
    private readonly IBotSellerApi? _api;
    private readonly ComboBox _status = new() { MinWidth = 190 };
    private readonly ComboBox _period = new() { MinWidth = 110 };
    private readonly Button _apply = Btn(T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"), primary: true);
    private readonly Button _prev = Btn("‹ " + T("Назад", "Артка", "Back", "Geri", "Orqaga"));
    private readonly Button _next = Btn(T("Дальше", "Кийинки", "Next", "İleri", "Keyingi") + " ›");
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _info = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private int _page = 1;
    private bool _loading;

    public BotDialogsWindow() : this(Api())
    {
    }

    public BotDialogsWindow(IBotSellerApi? api)
    {
        _api = api;
        Title = T("Диалоги бота", "Боттун диалогдору", "Bot dialogs", "Bot diyalogları", "Bot dialoglari");
        Width = 1000;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        // 2026-11-26 (стресс-тест): Esc закрывает окно; в поле с набранным текстом — не закрывает (набранный ответ/цена не теряются)
        EscapeKey.Attach(this, () => FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });

        foreach (var (value, label) in new[]
                 {
                     ("", T("Все диалоги", "Бардык диалогдор", "All dialogs", "Tüm diyaloglar", "Barcha dialoglar")),
                     ("needs_human", StatusText("needs_human")),
                     ("human", StatusText("human")),
                     ("callback", StatusText("callback")),
                     ("closed", T("Закрытые (вернулись боту)", "Жабылгандар (ботко кайтты)", "Closed (returned to the bot)", "Kapananlar (bota döndü)", "Yopilganlar (botga qaytdi)")),
                     ("bot", StatusText("bot")),
                 })
            _status.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _status.SelectedIndex = 0;
        foreach (var days in new[] { 7, 30, 90 })
            _period.Items.Add(new ComboBoxItem { Content = PeriodLabel(days), Tag = days });
        _period.SelectedIndex = 1;
        Avalonia.Automation.AutomationProperties.SetName(_status, T("Статус диалога", "Диалогдун статусу", "Dialog status", "Diyalog durumu", "Dialog holati"));
        Avalonia.Automation.AutomationProperties.SetName(_period, T("Период", "Мөөнөт", "Period", "Dönem", "Davr"));
        Theme(_info, TextBlock.ForegroundProperty, "BrushTextSoft");
        Theme(_pageText, TextBlock.ForegroundProperty, "BrushTextSoft");

        _apply.Click += async (_, _) => { _page = 1; await LoadAsync().ConfigureAwait(true); };
        _prev.Click += async (_, _) => { if (_page > 1) { _page--; await LoadAsync().ConfigureAwait(true); } };
        _next.Click += async (_, _) => { _page++; await LoadAsync().ConfigureAwait(true); };

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("«Ответить» — вы пишете покупателю прямо отсюда (или «Ответить» на карточку в вашем Telegram): диалог переходит к вам и бот молчит. «Вернуть боту» — бот снова отвечает сам.",
            "«Жооп берүү» — сатып алуучуга түз ушул жерден жазасыз (же Telegram'да карточкага «Жооп берүү»): диалог сизге өтөт, бот унчукпайт. «Ботко кайтаруу» — бот кайра өзү жооп берет.",
            "“Reply” — you write to the customer right here (or press “Reply” on the card in your Telegram): the dialog becomes yours and the bot goes quiet. “Return to bot” — the bot answers again.",
            "«Yanıtla» — müşteriye doğrudan buradan yazarsınız (veya Telegram'da karta «Yanıtla»): diyalog size geçer ve bot susar. «Bota geri ver» — bot yine kendisi yanıtlar.",
            "«Javob berish» — xaridorga to'g'ridan-to'g'ri shu yerdan yozasiz (yoki Telegram'da kartaga «Javob berish»): dialog sizga o'tadi va bot jim bo'ladi. «Botga qaytarish» — bot yana o'zi javob beradi.")));
        root.Children.Add(head);

        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        foreach (var c in new Control[] { _status, _period, _apply })
        {
            c.Margin = new Thickness(0, 0, 8, 4);
            filters.Children.Add(c);
        }
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            Padding = new Thickness(0, 0, 10, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_info);
        Grid.SetColumn(_prev, 1);
        Grid.SetColumn(_pageText, 2);
        Grid.SetColumn(_next, 3);
        _pageText.Margin = new Thickness(10, 0);
        footer.Children.Add(_prev);
        footer.Children.Add(_pageText);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        _prev.IsEnabled = _next.IsEnabled = false;
        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_api == null || _loading)
            return;
        _loading = true;
        _apply.IsEnabled = false;
        _info.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        var pageGone = false;
        try
        {
            var status = (_status.SelectedItem as ComboBoxItem)?.Tag as string;
            var days = (_period.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 30;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var page = await _api.GetDialogsAsync(status, DateTime.Today.AddDays(-days), DateTime.Today, _page, cts.Token).ConfigureAwait(true);
            Render(page);
        }
        catch (Exception ex)
        {
            _rows.Children.Clear();
            _prev.IsEnabled = _page > 1;
            _next.IsEnabled = false;
            _info.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + Failure(ex);
            // 2026-11-26 (живая проверка NBS): страницы за концом списка сервер отвечает 404 — список стал короче (взяли диалог, сбросили правила), а не «сервер не поддерживает»: идём на первую страницу.
            pageGone = ex is ApiException { StatusCode: 404 } && _page > 1;
        }
        finally
        {
            _loading = false;
            _apply.IsEnabled = true;
        }
        if (pageGone)
        {
            _page = 1;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private void Render(BotDialogsPage page)
    {
        _rows.Children.Clear();
        _prev.IsEnabled = _page > 1;
        _next.IsEnabled = page.HasNext;
        _pageText.Text = T("Страница ", "Барак ", "Page ", "Sayfa ", "Sahifa ") + _page.ToString(CultureInfo.InvariantCulture);
        if (page.Rows.Count == 0)
        {
            _info.Text = T("Диалогов по этим условиям нет.", "Бул шарттар боюнча диалогдор жок.", "No dialogs match.", "Bu koşullarla diyalog yok.", "Bu shartlar bo'yicha dialoglar yo'q.");
            return;
        }
        _info.Text = page.Count is { } n
            ? T($"Всего: {n}", $"Бардыгы: {n}", $"Total: {n}", $"Toplam: {n}", $"Jami: {n}")
            : T($"Показано: {page.Rows.Count}", $"Көрсөтүлдү: {page.Rows.Count}", $"Shown: {page.Rows.Count}", $"Gösterilen: {page.Rows.Count}", $"Ko'rsatildi: {page.Rows.Count}");
        foreach (var d in page.Rows)
            _rows.Children.Add(Row(d));
    }

    private Border Row(BotDialog d)
    {
        var box = new StackPanel { Spacing = 6 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new TextBlock { Text = Person(d), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(who, who.Text); // 2026-11-26 (стресс-тест): полное имя во всплывающей подсказке
        Theme(who, TextBlock.ForegroundProperty, "BrushText");
        var state = new TextBlock { Text = StatusText(d.Status) + (d.LastAt is null ? "" : " · " + When(d.LastAt)), FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0), FontWeight = d.Status is "needs_human" or "callback" ? FontWeight.SemiBold : FontWeight.Normal };
        Theme(state, TextBlock.ForegroundProperty, d.Status is "needs_human" or "callback" ? "BrushText" : "BrushTextSoft");
        Grid.SetColumn(state, 1);
        top.Children.Add(who);
        top.Children.Add(state);
        box.Children.Add(top);
        if (!string.IsNullOrWhiteSpace(d.LastText))
            box.Children.Add(Soft(Clip(d.LastText))); // 2026-11-26 (стресс-тест): длинный текст — обрезан, полный в «Переписке»
        if (d.Handoff is { Status: "returned" or "expired" } closed) // 2026-11-26: закрытая заявка — подпись, диалог у бота
            box.Children.Add(Soft("↩ " + (closed.Status == "expired"
                ? T("Заявка истекла — диалог снова у бота", "Өтүнүч мөөнөтү бүттү — диалог кайра ботто", "The request expired — the dialog is back with the bot", "Talep süresi doldu — diyalog yine botta", "So'rov muddati tugadi — dialog yana botda")
                : T("Диалог возвращён боту", "Диалог ботко кайтарылды", "The dialog was returned to the bot", "Diyalog bota geri verildi", "Dialog botga qaytarildi"))
                + (closed.CreatedAt is null ? "" : " · " + When(closed.CreatedAt))));
        if (d.Handoff is { Status: "waiting" or "taken" or "callback" } h)
        {
            var line = ReasonText(h.Reason);
            if (!string.IsNullOrWhiteSpace(h.Question))
                line += ": " + Clip(h.Question, 200);
            if (!string.IsNullOrWhiteSpace(h.Phone))
                line += " · 📞 " + h.Phone;
            box.Children.Add(Soft("👤 " + line));
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var transcript = new StackPanel { Spacing = 6, IsVisible = false };
        var read = Btn(T("Переписка", "Кат алышуу", "Conversation", "Yazışma", "Yozishma"));
        var loadedTranscript = false;
        read.Click += async (_, _) =>
        {
            transcript.IsVisible = !transcript.IsVisible;
            if (transcript.IsVisible && !loadedTranscript)
            {
                loadedTranscript = true;
                await FillTranscriptAsync(d, transcript).ConfigureAwait(true);
            }
        };
        var reply = ReplyPanel(d);
        var replyButton = Btn(T("Ответить", "Жооп берүү", "Reply", "Yanıtla", "Javob berish"), primary: d.Status == "human");
        replyButton.Click += (_, _) => reply.IsVisible = !reply.IsVisible;
        actions.Children.Add(replyButton);
        if (d.Status != "human")
            actions.Children.Add(ActionButton(T("Взять", "Алуу", "Take", "Al", "Olish"), d, "take", primary: true));
        if (d.Status != "bot")
            actions.Children.Add(ActionButton(T("Вернуть боту", "Ботко кайтаруу", "Return to bot", "Bota geri ver", "Botga qaytarish"), d, "return"));
        actions.Children.Add(read);
        foreach (var b in actions.Children)
            b.Margin = new Thickness(0, 0, 8, 4);
        box.Children.Add(actions);
        box.Children.Add(reply);
        box.Children.Add(transcript);

        var row = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = box };
        Theme(row, Border.BackgroundProperty, d.Status is "needs_human" or "callback" ? "BrushAccentSoft" : "BrushPanelSoft");
        Theme(row, Border.BorderBrushProperty, d.Status is "needs_human" or "callback" ? "BrushAccent" : "BrushBorder");
        return row;
    }

    /// <summary>2026-11-26 (ответ Умара, вечер): ответ покупателю из программы — POST dialogs/&lt;chat_id&gt;/ {"action":"reply","text"}: сервер шлёт текст от бота, сам берёт диалог (бот молчит) и пишет в «Чаты».
    /// До 4000 знаков, счётчик под полем; не дошло до Telegram (502 telegram_error) — ошибка под полем, текст остаётся.</summary>
    private StackPanel ReplyPanel(BotDialog d)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = BotDialogLimits.ReplyMaxLength, MinHeight = 64, MaxHeight = 140 };
        Avalonia.Automation.AutomationProperties.SetName(box, T("Ответ покупателю", "Сатып алуучуга жооп", "Reply to the customer", "Müşteriye yanıt", "Xaridorga javob"));
        var counter = Soft($"0 / {BotDialogLimits.ReplyMaxLength}", 12);
        var status = Soft("", 12.5);
        var send = Btn(T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), primary: true);
        // 2026-11-26 (стресс-тест): пустой ответ (одни пробелы) отправить нельзя — кнопка неактивна, а не молча ничего не делает.
        send.IsEnabled = false;
        // 2026-11-26 (стресс-тест): исход неизвестен (таймаут, обрыв, 5xx кроме telegram_error) — сообщение могло уйти. Тот же текст повторно — только вторым нажатием, иначе покупатель получит его дважды.
        string? unsureText = null;
        var confirmRepeat = false;
        box.TextChanged += (_, _) =>
        {
            counter.Text = $"{(box.Text ?? "").Length} / {BotDialogLimits.ReplyMaxLength}";
            send.IsEnabled = (box.Text ?? "").Trim().Length > 0;
            confirmRepeat = false;
        };
        send.Click += async (_, _) =>
        {
            var text = (box.Text ?? "").Trim();
            if (_api == null || text.Length == 0)
                return;
            if (_loading)
            {
                status.Text = T("Список обновляется — нажмите ещё раз через секунду.", "Тизме жаңыланууда — бир секунддан кийин кайра басыңыз.", "The list is refreshing — press again in a second.", "Liste yenileniyor — bir saniye sonra tekrar basın.", "Ro'yxat yangilanmoqda — bir soniyadan keyin yana bosing.");
                return;
            }
            if (unsureText == text && !confirmRepeat)
            {
                confirmRepeat = true;
                status.Text = T("Этот текст мог уже дойти до покупателя. Проверьте «Переписку»; чтобы отправить ещё раз — нажмите «Отправить» снова.",
                    "Бул текст сатып алуучуга жетип калышы мүмкүн. «Кат алышууну» текшериңиз; кайра жөнөтүү үчүн «Жөнөтүүнү» дагы басыңыз.",
                    "This text may have already reached the customer. Check “Conversation”; to send it again, press “Send” once more.",
                    "Bu metin müşteriye zaten ulaşmış olabilir. «Yazışma»yı kontrol edin; tekrar göndermek için «Gönder»e yeniden basın.",
                    "Bu matn xaridorga allaqachon yetgan bo'lishi mumkin. «Yozishma»ni tekshiring; qayta yuborish uchun «Yuborish»ni yana bosing.");
                return;
            }
            confirmRepeat = false;
            send.IsEnabled = false;
            status.Text = T("Отправляем…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var r = await _api.ReplyToDialogAsync(d.ChatId, text, cts.Token).ConfigureAwait(true);
                PosLogger.Log($"Диалоги бота: ответ покупателю отправлен из программы (диалог {d.ChatId}).", "TELEGRAM");
                await LoadAsync().ConfigureAwait(true); // строки перерисованы: диалог теперь «У продавца»
                _info.Text = (r.Sent
                    ? T("Отправлено покупателю. Диалог у вас — бот молчит.", "Сатып алуучуга жөнөтүлдү. Диалог сизде — бот унчукпайт.", "Sent to the customer. The dialog is yours — the bot is quiet.", "Müşteriye gönderildi. Diyalog sizde — bot susuyor.", "Xaridorga yuborildi. Dialog sizda — bot jim.")
                    : T("Сервер не подтвердил отправку.", "Сервер жөнөтүүнү ырастаган жок.", "The server did not confirm sending.", "Sunucu gönderimi onaylamadı.", "Server yuborishni tasdiqlamadi.")) + " · " + _info.Text;
            }
            catch (Exception ex)
            {
                var telegram = BotDialogLimits.ReplyFailedForSure(ex);
                unsureText = telegram ? null : text;
                status.Text = ex is ApiException { StatusCode: 502 } && telegram
                    ? T("Ошибка Telegram: ", "Telegram катасы: ", "Telegram error: ", "Telegram hatası: ", "Telegram xatosi: ") + ServerTelegramBotApi.DescribeFields(ex)
                    : telegram
                        ? T("Не отправлено: ", "Жөнөтүлгөн жок: ", "Not sent: ", "Gönderilemedi: ", "Yuborilmadi: ") + Failure(ex)
                        : T("Неизвестно, дошло ли сообщение (нет ответа сервера). Проверьте «Переписку». ", "Билдирүү жеттиби — белгисиз (сервер жооп берген жок). «Кат алышууну» текшериңиз. ",
                            "Unknown whether the message arrived (no server response). Check “Conversation”. ", "Mesajın ulaşıp ulaşmadığı bilinmiyor (sunucu yanıt vermedi). «Yazışma»yı kontrol edin. ",
                            "Xabar yetib bordimi — noma'lum (server javob bermadi). «Yozishma»ni tekshiring. ") + Failure(ex);
                send.IsEnabled = true;
            }
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(counter);
        Grid.SetColumn(send, 1);
        footer.Children.Add(send);
        var panel = new StackPanel { Spacing = 6, IsVisible = false };
        panel.Children.Add(box);
        panel.Children.Add(footer);
        panel.Children.Add(status);
        return panel;
    }

    private Button ActionButton(string text, BotDialog d, string action, bool primary = false)
    {
        var b = Btn(text, primary);
        b.Click += async (_, _) =>
        {
            if (_api == null || _loading)
                return;
            b.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _api.DialogActionAsync(d.ChatId, action, cts.Token).ConfigureAwait(true);
                PosLogger.Log($"Диалоги бота: {(action == "take" ? "взят" : "возвращён боту")} диалог {d.ChatId}.", "TELEGRAM");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _info.Text = T("Не получилось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + Failure(ex);
                b.IsEnabled = true;
            }
        };
        return b;
    }

    private async Task FillTranscriptAsync(BotDialog d, StackPanel host)
    {
        host.Children.Clear();
        host.Children.Add(Soft(T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var detail = _api is null ? null : await _api.GetDialogAsync(d.ChatId, cts.Token).ConfigureAwait(true);
            host.Children.Clear();
            if (detail is null || (detail.Messages.Count == 0 && detail.Orders.Count == 0))
            {
                host.Children.Add(Soft(T("Сообщений пока нет.", "Билдирүүлөр азырынча жок.", "No messages yet.", "Henüz mesaj yok.", "Hozircha xabarlar yo'q.")));
                return;
            }
            var lines = new StackPanel { Spacing = 6 };
            foreach (var m in detail.Messages.TakeLast(30))
            {
                var buyer = new TextBlock { Text = When(m.At) + " · " + (m.IsVoice ? "🎤 " : "") + m.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                Theme(buyer, TextBlock.ForegroundProperty, "BrushText");
                lines.Children.Add(buyer);
                if (!string.IsNullOrWhiteSpace(m.Reply))
                {
                    var bot = Soft("🤖 " + BotSellerText.Plain(m.Reply));
                    bot.Margin = new Thickness(16, 0, 0, 0);
                    lines.Children.Add(bot);
                }
            }
            host.Children.Add(new ScrollViewer { Content = lines, MaxHeight = 240, Padding = new Thickness(0, 0, 10, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            foreach (var o in detail.Orders)
                host.Children.Add(Heading(T($"Заказ №{o.Number} · {MoneyText.Som(o.Total)}", $"Заказ №{o.Number} · {MoneyText.Som(o.Total)}", $"Order #{o.Number} · {MoneyText.Som(o.Total)}", $"Sipariş #{o.Number} · {MoneyText.Som(o.Total)}", $"Buyurtma №{o.Number} · {MoneyText.Som(o.Total)}"), 12.5));
        }
        catch (Exception ex)
        {
            host.Children.Clear();
            host.Children.Add(Soft(T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + Failure(ex)));
        }
    }
}
