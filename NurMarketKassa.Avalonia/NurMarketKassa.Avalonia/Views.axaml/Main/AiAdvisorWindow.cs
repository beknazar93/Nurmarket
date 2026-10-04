using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-05, владелец: «в десктопе открой чат с ИИ для владельца, чтобы владелец советовался с ним — специальную
/// вкладку». Раздел «ИИ-советник» программы владельца: разговор с нейросетью (Google Gemini по ключу владельца —
/// тот же, что у ИИ Telegram-бота, Настройки → Операции → Telegram-бот), которая видит сводку магазина (выручка
/// сегодня и за неделю, лучшие товары, что заканчивается, товары из вопроса) и цифр не выдумывает
/// (<see cref="TelegramAiChat.AskOwnerAppAsync"/>). Без ключа ничего никуда не уходит — раздел просит ключ.
/// </summary>
public sealed class AiAdvisorWindow : Window, IOwnerSection
{
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(24, 18, 24, 24) };
    private readonly StackPanel _messages = new() { Spacing = 10, Margin = new Thickness(0, 4, 8, 4) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Border _keyCard = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _keyBox;
    private readonly TextBlock _keyStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _quick = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
    private CancellationTokenSource? _cts;
    private bool _busy;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public AiAdvisorWindow()
    {
        Title = T("ИИ-советник", "ИИ-кеңешчи", "AI advisor", "Yapay zekâ danışmanı", "SI maslahatchi");
        Width = 900;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var intro = new TextBlock
        {
            Text = T("Спросите о своём магазине: как идут продажи, что заказать, что не продаётся, как поднять выручку. "
                     + "Советник видит выручку за сегодня и неделю, лучшие товары и то, что заканчивается, и не выдумывает цифр.",
                "Дүкөнүңүз жөнүндө сураңыз: сатуу кандай, эмнени заказ кылуу керек, эмне сатылбай жатат, кирешени кантип көбөйтүү. "
                + "Кеңешчи бүгүнкү жана жумалык кирешени, мыкты товарларды жана түгөнүп бараткандарды көрөт, сандарды ойлоп чыгарбайт.",
                "Ask about your shop: how sales are going, what to order, what isn't selling, how to grow revenue. "
                + "The advisor sees today's and this week's revenue, top products and what is running out, and never makes numbers up.",
                "Mağazanız hakkında sorun: satışlar nasıl, ne sipariş edilmeli, ne satılmıyor, ciro nasıl artırılır. "
                + "Danışman bugünün ve haftanın cirosunu, en iyi ürünleri ve bitmek üzere olanları görür, rakam uydurmaz.",
                "Do'koningiz haqida so'rang: savdo qanday, nima buyurtma qilish kerak, nima sotilmayapti, tushumni qanday oshirish. "
                + "Maslahatchi bugungi va haftalik tushumni, eng yaxshi mahsulotlarni va tugayotganlarini ko'radi, raqamlarni o'ylab topmaydi."),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Use(intro, TextBlock.ForegroundProperty, "BrushTextSoft");
        _root.Children.Add(intro);

        // Ключ нейросети (если ещё не задан).
        Use(_keyCard, Border.BackgroundProperty, "BrushPanel");
        Use(_keyCard, Border.BorderBrushProperty, "BrushBorder");
        var keyStack = new StackPanel { Spacing = 8 };
        var keyTitle = new TextBlock
        {
            Text = T("Нужен ключ нейросети — бесплатный ключ Google Gemini",
                "Нейротармактын ачкычы керек — Google Gemini акысыз ачкычы",
                "An AI key is needed — a free Google Gemini key",
                "Yapay zekâ anahtarı gerekli — ücretsiz Google Gemini anahtarı",
                "Neyrotarmoq kaliti kerak — bepul Google Gemini kaliti"),
            FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        Use(keyTitle, TextBlock.ForegroundProperty, "BrushText");
        keyStack.Children.Add(keyTitle);
        var keyHint = new TextBlock
        {
            Text = T("Откройте aistudio.google.com → «Get API key», скопируйте ключ и вставьте ниже (карта не нужна). Тот же ключ использует ИИ Telegram-бота. "
                     + "Сводка магазина уходит в Google только по этому ключу; на бесплатном уровне Google может использовать запросы для улучшения своих продуктов.",
                "aistudio.google.com → «Get API key» ачып, ачкычты көчүрүп, төмөнгө чаптаңыз (карта керек эмес). Ушул эле ачкычты Telegram-боттун ИИси колдонот. "
                + "Дүкөндүн жыйынтыгы Google'га ушул ачкыч менен гана кетет; акысыз деңгээлде Google суроолорду өз продукттарын жакшыртуу үчүн колдонушу мүмкүн.",
                "Open aistudio.google.com → “Get API key”, copy the key and paste it below (no card needed). The Telegram bot's AI uses the same key. "
                + "The shop summary goes to Google only with this key; on the free tier Google may use requests to improve its products.",
                "aistudio.google.com → «Get API key» açın, anahtarı kopyalayıp aşağıya yapıştırın (kart gerekmez). Telegram botunun yapay zekâsı aynı anahtarı kullanır. "
                + "Mağaza özeti Google'a yalnızca bu anahtarla gider; ücretsiz seviyede Google istekleri ürünlerini geliştirmek için kullanabilir.",
                "aistudio.google.com → «Get API key» ni oching, kalitni nusxalab pastga joylang (karta kerak emas). Telegram botining SI xuddi shu kalitdan foydalanadi. "
                + "Do'kon xulosasi Google'ga faqat shu kalit bilan ketadi; bepul darajada Google so'rovlardan o'z mahsulotlarini yaxshilash uchun foydalanishi mumkin."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        Use(keyHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        keyStack.Children.Add(keyHint);
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _keyBox = UiKit.Input(this, T("Ключ Google Gemini (AIza…)", "Google Gemini ачкычы (AIza…)", "Google Gemini key (AIza…)", "Google Gemini anahtarı (AIza…)", "Google Gemini kaliti (AIza…)"));
        keyRow.Children.Add(_keyBox);
        var getKey = UiKit.Ghost(this, T("Получить ключ", "Ачкыч алуу", "Get a key", "Anahtar al", "Kalit olish"));
        getKey.Margin = new Thickness(8, 0, 0, 0);
        getKey.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri("https://aistudio.google.com/app/apikey")).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _keyStatus.Text = "https://aistudio.google.com/app/apikey — " + ex.Message;
            }
        };
        Grid.SetColumn(getKey, 1);
        keyRow.Children.Add(getKey);
        var saveKey = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        saveKey.Margin = new Thickness(8, 0, 0, 0);
        saveKey.Click += async (_, _) => await SaveKeyAsync().ConfigureAwait(true);
        Grid.SetColumn(saveKey, 2);
        keyRow.Children.Add(saveKey);
        keyStack.Children.Add(keyRow);
        Use(_keyStatus, TextBlock.ForegroundProperty, "BrushTextSoft");
        keyStack.Children.Add(_keyStatus);
        _keyCard.Child = keyStack;
        Grid.SetRow(_keyCard, 1);
        _root.Children.Add(_keyCard);

        // Разговор.
        var chatCard = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12) };
        Use(chatCard, Border.BackgroundProperty, "BrushPanelSoft");
        Use(chatCard, Border.BorderBrushProperty, "BrushBorder");
        _scroll.Content = _messages;
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        chatCard.Child = _scroll;
        Grid.SetRow(chatCard, 2);
        _root.Children.Add(chatCard);

        // Готовые вопросы.
        foreach (var q in new[]
                 {
                     T("Как прошла неделя?", "Жума кандай өттү?", "How did the week go?", "Hafta nasıl geçti?", "Hafta qanday o'tdi?"),
                     T("Что срочно заказать?", "Эмнени шашылыш заказ кылуу керек?", "What should I order urgently?", "Acilen ne sipariş etmeliyim?", "Nimani zudlik bilan buyurtma qilish kerak?"),
                     T("Как поднять продажи?", "Сатууну кантип көбөйтүү керек?", "How can I grow sales?", "Satışları nasıl artırırım?", "Savdoni qanday oshirish mumkin?"),
                     T("Какие товары продаются лучше всего?", "Кайсы товарлар эң жакшы сатылат?", "Which products sell best?", "En çok hangi ürünler satılıyor?", "Qaysi mahsulotlar eng yaxshi sotiladi?"),
                     T("Где я теряю деньги?", "Акчаны кайда жоготуп жатам?", "Where am I losing money?", "Nerede para kaybediyorum?", "Qayerda pul yo'qotyapman?"),
                 })
        {
            var chip = UiKit.Chip(this, q, false);
            chip.MinHeight = 36;
            chip.FontSize = 13;
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.Click += async (_, _) => await SendAsync(q).ConfigureAwait(true);
            _quick.Children.Add(chip);
        }
        Grid.SetRow(_quick, 3);
        _root.Children.Add(_quick);

        // Строка ввода.
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _input = UiKit.Input(this, T("Напишите вопрос и нажмите Enter…", "Суроону жазып, Enter басыңыз…", "Type a question and press Enter…", "Sorunuzu yazıp Enter'a basın…", "Savolni yozib, Enter bosing…"), 48);
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await SendAsync(_input.Text).ConfigureAwait(true);
        };
        inputRow.Children.Add(_input);
        _send = UiKit.Primary(this, T("Спросить", "Суроо", "Ask", "Sor", "So'rash"));
        _send.Height = 48;
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.Click += async (_, _) => await SendAsync(_input.Text).ConfigureAwait(true);
        Grid.SetColumn(_send, 1);
        inputRow.Children.Add(_send);
        var reset = UiKit.Ghost(this, T("Новый разговор", "Жаңы маек", "New chat", "Yeni sohbet", "Yangi suhbat"));
        reset.Height = 48;
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) => ResetChat();
        Grid.SetColumn(reset, 2);
        inputRow.Children.Add(reset);
        Grid.SetRow(inputRow, 4);
        _root.Children.Add(inputRow);

        Content = _root;

        // Телефон: кнопки строки ввода — под полем, ключ — с кнопками в отдельной строке.
        NarrowLayout.Attach(this, 640, narrow =>
        {
            _root.Margin = narrow ? new Thickness(10, 8, 10, 10) : new Thickness(24, 18, 24, 24);
            reset.IsVisible = !narrow;
            _send.Padding = new Thickness(narrow ? 12 : 20, 0);
        });

        RefreshKeyCard();
        AddBubble(T("Здравствуйте! Я ИИ-советник вашего магазина. Спросите про продажи, остатки, закупки или как поднять выручку — "
                    + "отвечу по данным вашего магазина. Можно нажать готовый вопрос ниже.",
                "Саламатсызбы! Мен дүкөнүңүздүн ИИ-кеңешчисимин. Сатуу, калдыктар, сатып алуулар же кирешени кантип көбөйтүү жөнүндө сураңыз — "
                + "дүкөнүңүздүн маалыматы боюнча жооп берем. Төмөндөгү даяр суроону басса да болот.",
                "Hello! I'm your shop's AI advisor. Ask about sales, stock, purchasing or how to grow revenue — "
                + "I'll answer from your shop's data. You can also tap a ready question below.",
                "Merhaba! Mağazanızın yapay zekâ danışmanıyım. Satışlar, stok, alımlar veya ciroyu nasıl artıracağınız hakkında sorun — "
                + "mağazanızın verilerine göre yanıtlarım. Aşağıdaki hazır sorulardan birine de dokunabilirsiniz.",
                "Assalomu alaykum! Men do'koningizning SI maslahatchisiman. Savdo, qoldiqlar, xaridlar yoki tushumni qanday oshirish haqida so'rang — "
                + "do'koningiz ma'lumotlari bo'yicha javob beraman. Pastdagi tayyor savolni bosish ham mumkin."), fromOwner: false);

        // В программе владельца своя локальная история продаж — подтягиваем свежие чеки с сервера, чтобы
        // сводка для советника (выручка, лучшие товары) была по сегодняшним данным.
        Opened += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                await SalesHistoryBackfill.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: история продаж не обновлена ({ex.Message}).", "WARNING");
            }
        });
        Closed += (_, _) => _cts?.Cancel();
    }

    public void AsOwnerSection()
    {
        _root.Margin = OwnerSectionLayout.Margin;
    }

    private void RefreshKeyCard()
    {
        _keyCard.IsVisible = !TelegramAiChat.IsConfigured;
        _input.IsEnabled = TelegramAiChat.IsConfigured && !_busy;
        _send.IsEnabled = TelegramAiChat.IsConfigured && !_busy;
        foreach (var chip in _quick.Children.OfType<Button>())
            chip.IsEnabled = TelegramAiChat.IsConfigured && !_busy;
    }

    private async Task SaveKeyAsync()
    {
        var key = (_keyBox.Text ?? "").Trim();
        if (key.Length == 0)
            return;
        _keyStatus.Text = T("Проверяю ключ…", "Ачкычты текшерип жатам…", "Checking the key…", "Anahtar kontrol ediliyor…", "Kalit tekshirilmoqda…");
        var (ok, message) = await TelegramAiChat.TestKeyAsync(key, CancellationToken.None).ConfigureAwait(true);
        if (!ok)
        {
            _keyStatus.Text = T("Ключ не подошёл: ", "Ачкыч туура келген жок: ", "The key didn't work: ", "Anahtar çalışmadı: ", "Kalit ishlamadi: ") + message;
            return;
        }
        UserPreferences.Instance.TelegramAiKey = key;
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log("ИИ-советник: ключ нейросети сохранён и проверен.", "INFO");
        RefreshKeyCard();
        _input.Focus();
    }

    private void ResetChat()
    {
        TelegramAiChat.ResetOwnerAppHistory();
        _messages.Children.Clear();
        AddBubble(T("Начнём сначала. О чём посоветоваться?", "Башынан баштайлы. Эмне жөнүндө кеңешебиз?", "Let's start over. What would you like advice on?",
            "Baştan başlayalım. Ne hakkında danışmak istersiniz?", "Boshidan boshlaymiz. Nima haqida maslahatlashamiz?"), fromOwner: false);
    }

    private async Task SendAsync(string? text)
    {
        var question = (text ?? "").Trim();
        if (question.Length == 0 || _busy || !TelegramAiChat.IsConfigured)
            return;
        _busy = true;
        _input.Text = "";
        RefreshKeyCard();
        AddBubble(question, fromOwner: true);
        var thinking = AddBubble(T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…"), fromOwner: false);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            // 2026-10-05, владелец: «дай доступ ко всему для ИИ … к складу, к товарам» — цифры «Сводки» и склад.
            var warehouse = await Task.Run(OwnerAiContext.BuildWarehouse, _cts.Token).ConfigureAwait(true);
            // «Я должен кому-то или мне должны?» — обезличенные итоги долгов клиентов и поставщиков.
            var debts = await OwnerAiContext.BuildDebtTotalsAsync(_cts.Token).ConfigureAwait(true);
            // «Дай список клиентов-должников»: должники под кодами [Д1]…, имена и телефоны подставляются здесь.
            var (debtors, debtorNames) = await OwnerAiContext.BuildDebtorsPseudonymousAsync(_cts.Token).ConfigureAwait(true);
            // 2026-10-05, владелец: «дай доступ к ABC-анализу ИИ».
            var abc = await OwnerAiContext.BuildAbcAsync(_cts.Token).ConfigureAwait(true);
            var summary = string.Join("\n", new[] { OwnerOverviewSnapshot.Text, debts, debtors, abc, warehouse }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var (answer, error) = await TelegramAiChat.AskOwnerAppAsync(question, summary, _cts.Token).ConfigureAwait(true);
            thinking.Text = answer is { Length: > 0 }
                ? OwnerAiContext.RevealDebtors(TelegramAiChat.ToPlainText(answer), debtorNames)
                : T("Не получилось ответить: ", "Жооп берүү мүмкүн болгон жок: ", "Couldn't answer: ", "Yanıt verilemedi: ", "Javob berib bo'lmadi: ") + (error ?? "нет ответа");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            thinking.Text = T("Не получилось ответить: ", "Жооп берүү мүмкүн болгон жок: ", "Couldn't answer: ", "Yanıt verilemedi: ", "Javob berib bo'lmadi: ") + ex.Message;
            PosLogger.Log($"ИИ-советник: ошибка ответа ({ex.Message}).", "WARNING");
        }
        finally
        {
            _busy = false;
            RefreshKeyCard();
        }
        ScrollToEnd();
        _input.Focus();
    }

    /// <summary>Реплика: вопрос владельца — справа, ответ советника — слева. Текст можно выделить и скопировать.</summary>
    private SelectableTextBlock AddBubble(string text, bool fromOwner)
    {
        var body = new SelectableTextBlock { Text = text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        var bubble = new Border
        {
            CornerRadius = fromOwner ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
            Padding = new Thickness(14, 10),
            MaxWidth = 680,
            HorizontalAlignment = fromOwner ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Child = body,
        };
        Use(bubble, Border.BackgroundProperty, fromOwner ? "BrushAccentSoft" : "BrushPanel");
        Use(bubble, Border.BorderBrushProperty, fromOwner ? "BrushAccentStrong" : "BrushBorder");
        _messages.Children.Add(bubble);
        ScrollToEnd();
        return body;
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
