using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

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

    // 2026-10-05, владелец: «добавь в ИИ голосовой чат тоже». 🎤 — запись вопроса, ответ на голосовой вопрос
    // зачитывается вслух (Gemini: распознавание и озвучка — тот же ключ ИИ). Только Windows (VoiceChatRecorder).
    private Button? _mic;
    private VoiceChatRecorder? _recorder;
    private bool _voiceAnswer;
    private bool _webSearchNoteShown;
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
                     // 2026-10-05, владелец: «чтобы он смог предлагать назначить акции на проблемные товары».
                     T("Какие акции запустить?", "Кайсы акцияларды баштоо керек?", "Which promotions should I run?", "Hangi kampanyaları başlatmalıyım?", "Qanday aksiyalar boshlash kerak?"),
                     // 2026-10-05, владелец: «загрузи фото к товарам, которых нет фото».
                     T("Найди фото для товаров без фото", "Сүрөтсүз товарларга сүрөт тап", "Find photos for products without one", "Fotoğrafsız ürünlere fotoğraf bul", "Rasmsiz mahsulotlarga rasm top"),
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
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        _input = UiKit.Input(this, T("Напишите вопрос и нажмите Enter…", "Суроону жазып, Enter басыңыз…", "Type a question and press Enter…", "Sorunuzu yazıp Enter'a basın…", "Savolni yozib, Enter bosing…"), 48);
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await SendAsync(_input.Text).ConfigureAwait(true);
        };
        inputRow.Children.Add(_input);
        if (VoiceChatRecorder.IsSupported)
        {
            _mic = UiKit.Ghost(this, "");
            _mic.Content = MicGlyph(recording: false);
            _mic.Height = 48;
            _mic.Width = 56;
            _mic.Padding = new Thickness(0);
            _mic.FontSize = 20;
            _mic.Margin = new Thickness(8, 0, 0, 0);
            ToolTip.SetTip(_mic, T("Спросить голосом: нажмите, говорите, нажмите ещё раз — ответ прозвучит вслух",
                "Үн менен суроо: басыңыз, сүйлөңүз, кайра басыңыз — жооп үн менен угулат",
                "Ask by voice: press, speak, press again — the answer will be read aloud",
                "Sesle sor: basın, konuşun, tekrar basın — yanıt sesli okunur",
                "Ovoz bilan so'rash: bosing, gapiring, yana bosing — javob ovoz bilan o'qiladi"));
            _mic.Click += async (_, _) => await ToggleVoiceAsync().ConfigureAwait(true);
            Grid.SetColumn(_mic, 1);
            inputRow.Children.Add(_mic);
        }
        _send = UiKit.Primary(this, T("Спросить", "Суроо", "Ask", "Sor", "So'rash"));
        _send.Height = 48;
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.Click += async (_, _) => await SendAsync(_input.Text).ConfigureAwait(true);
        Grid.SetColumn(_send, 2);
        inputRow.Children.Add(_send);
        var reset = UiKit.Ghost(this, T("Новый разговор", "Жаңы маек", "New chat", "Yeni sohbet", "Yangi suhbat"));
        reset.Height = 48;
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) => ResetChat();
        Grid.SetColumn(reset, 3);
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
            // Ключ: поле на всю ширину, «Получить ключ» и «Сохранить» — строкой ниже (на телефоне поле было в 60 точек).
            keyRow.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,*" : "*,Auto,Auto");
            keyRow.RowDefinitions = narrow ? new RowDefinitions("Auto,8,Auto") : new RowDefinitions();
            Grid.SetColumnSpan(_keyBox, narrow ? 2 : 1);
            Grid.SetRow(getKey, narrow ? 2 : 0);
            Grid.SetColumn(getKey, narrow ? 0 : 1);
            Grid.SetRow(saveKey, narrow ? 2 : 0);
            Grid.SetColumn(saveKey, narrow ? 1 : 2);
            getKey.Margin = new Thickness(narrow ? 0 : 8, 0, narrow ? 4 : 0, 0);
            saveKey.Margin = new Thickness(narrow ? 4 : 8, 0, 0, 0);
            getKey.HorizontalAlignment = saveKey.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        });
        keyRow.Classes.Add("no-reflow");

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
                // 2026-10-05: сводку для ИИ — заранее, чтобы первый вопрос (особенно голосом) не ждал её сборки.
                await Dispatcher.UIThread.InvokeAsync(() => _ = GetSummaryAsync());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: история продаж не обновлена ({ex.Message}).", "WARNING");
            }
        });
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _recorder?.Dispose();
            _recorder = null;
            VoiceChatPlayer.Stop();
        };
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
        // 2026-10-05, проверка на телефоне: без ключа ИИ не работала и кнопка «Найди фото…», хотя поиску фото
        // нейросеть не нужна (открытые базы товаров по штрихкоду) — она доступна всегда.
        foreach (var chip in _quick.Children.OfType<Button>())
            chip.IsEnabled = !_busy && (TelegramAiChat.IsConfigured || chip.Content is string text && IsPhotoRequest(text));
        if (_mic is not null)
            _mic.IsEnabled = TelegramAiChat.IsConfigured && (!_busy || _recorder is not null);
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
        VoiceChatPlayer.Stop();
        TelegramAiChat.ResetOwnerAppHistory();
        _messages.Children.Clear();
        AddBubble(T("Начнём сначала. О чём посоветоваться?", "Башынан баштайлы. Эмне жөнүндө кеңешебиз?", "Let's start over. What would you like advice on?",
            "Baştan başlayalım. Ne hakkında danışmak istersiniz?", "Boshidan boshlaymiz. Nima haqida maslahatlashamiz?"), fromOwner: false);
    }

    private async Task SendAsync(string? text)
    {
        var question = (text ?? "").Trim();
        if (question.Length == 0 || _busy || (!TelegramAiChat.IsConfigured && !IsPhotoRequest(question)))
            return;
        // Вслух отвечаем только на вопрос голосом; напечатанный вопрос — молча, как раньше.
        var speak = _voiceAnswer;
        _voiceAnswer = false;
        VoiceChatPlayer.Stop();
        _busy = true;
        _input.Text = "";
        RefreshKeyCard();
        AddBubble(question, fromOwner: true);
        // 2026-10-05, владелец: «добавь техническую возможность к ИИ для загрузки фото на склад» — просьба про фото
        // товаров выполняется программой сама (поиск по штрихкоду, загрузка после подтверждения), без нейросети.
        if (IsPhotoRequest(question))
        {
            try
            {
                await RunPhotoAssistantAsync(question).ConfigureAwait(true);
            }
            finally
            {
                _busy = false;
                RefreshKeyCard();
            }
            return;
        }
        var thinking = AddBubble(T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…"), fromOwner: false);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (summary, debtorNames) = await GetSummaryAsync().ConfigureAwait(true);
            var summaryMs = watch.ElapsedMilliseconds;
            // 2026-10-05, владелец: «голос очень сильно тормозит» — на вопрос голосом ответ короткий: быстрее и пишется,
            // и озвучивается (длинный ответ на 8–10 предложений звучал полминуты и готовился долго).
            var askText = speak
                ? question + "\n(Вопрос задан голосом, ответ будет озвучен: ответь коротко — 2–3 предложения, без списков и таблиц.)"
                : question;
            var (answer, error) = await TelegramAiChat.AskOwnerAppAsync(askText, summary, _cts.Token).ConfigureAwait(true);
            PosLogger.Log($"ИИ-советник: сводка {summaryMs} мс, ответ ИИ {watch.ElapsedMilliseconds - summaryMs} мс{(speak ? " (голосом)" : "")}.", "INFO");
            Dictionary<string, bool>? botChange = null;
            Dictionary<string, object?>? scenario = null;
            if (answer is { Length: > 0 })
            {
                (answer, botChange) = ExtractBotChange(answer);
                (answer, scenario) = ExtractScenario(answer);
                AddProductPhotos(answer);
            }
            thinking.Text = answer is { Length: > 0 }
                ? OwnerAiContext.RevealDebtors(TelegramAiChat.ToPlainText(answer), debtorNames)
                : T("Не получилось ответить: ", "Жооп берүү мүмкүн болгон жок: ", "Couldn't answer: ", "Yanıt verilemedi: ", "Javob berib bo'lmadi: ") + (error ?? "нет ответа");
            // 2026-10-05, владелец: «включи поиск по интернету для ИИ» — если ИИ искал в интернете, источники ссылками.
            if (answer is { Length: > 0 } && TelegramAiChat.LastWebSources.Count > 0)
                AddWebSources(TelegramAiChat.LastWebSources);
            else if (TelegramAiChat.WebSearchUnavailable && !_webSearchNoteShown)
            {
                _webSearchNoteShown = true;
                AddBubble(T("Поиск в интернете для этого ключа ИИ недоступен: у бесплатного ключа Google Gemini нет квоты на поиск Google. "
                            + "Включите оплату (Billing) для ключа в aistudio.google.com — поиск заработает сам. Пока отвечаю по данным магазина.",
                        "Бул ИИ ачкычы үчүн интернеттен издөө жеткиликсиз: акысыз Google Gemini ачкычында Google издөөгө квота жок. "
                            + "aistudio.google.com сайтында ачкычка төлөмдү (Billing) күйгүзүңүз — издөө өзү иштейт. Азырынча дүкөндүн маалыматы боюнча жооп берем.",
                        "Web search is not available for this AI key: a free Google Gemini key has no Google Search quota. "
                            + "Enable Billing for the key at aistudio.google.com and search will start working. For now I answer from the shop data.",
                        "Bu yapay zekâ anahtarı için internet araması kullanılamıyor: ücretsiz Google Gemini anahtarında Google Arama kotası yok. "
                            + "aistudio.google.com adresinde anahtar için faturalandırmayı açın — arama kendiliğinden çalışır. Şimdilik mağaza verileriyle yanıtlıyorum.",
                        "Bu SI kaliti uchun internetda qidirish mavjud emas: bepul Google Gemini kalitida Google qidiruv kvotasi yo'q. "
                            + "aistudio.google.com saytida kalit uchun to'lovni (Billing) yoqing — qidiruv o'zi ishlaydi. Hozircha do'kon ma'lumotlari bo'yicha javob beraman."), fromOwner: false);
            }
            if (botChange is { Count: > 0 })
                AddBotChangeCard(botChange);
            if (scenario is not null)
                AddScenarioCard(scenario);
            if (speak && answer is { Length: > 0 })
                _ = SpeakAsync(thinking.Text ?? "");
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

    // ── 2026-10-05, владелец: «добавь возможность ИИ управлять ботом» (ответ «1 да»). ИИ предлагает изменение
    // строкой «БОТ: {...}», программа показывает его карточкой; на сервер (PATCH telegram-bot/settings/) — только по «Применить».

    private static readonly (string Key, string Title)[] BotSwitches =
    {
        ("shift_summary_enabled", T("Сводка смены владельцу", "Ээсине смена жыйынтыгы", "Shift summary to the owner", "Sahibine vardiya özeti", "Egasiga smena xulosasi")),
        ("commands_enabled", T("Команды владельца в боте", "Боттогу ээсинин буйруктары", "Owner commands in the bot", "Bottaki sahip komutları", "Botdagi egasi buyruqlari")),
        ("ai_enabled", T("ИИ в боте", "Боттогу ИИ", "AI in the bot", "Botta YZ", "Botda SI")),
        ("consultant_enabled", T("ИИ-консультант для покупателей", "Кардарлар үчүн ИИ-кеңешчи", "AI consultant for customers", "Müşteriler için YZ danışmanı", "Xaridorlar uchun SI-maslahatchi")),
        ("voice_replies_enabled", T("Ответы голосом", "Үн менен жооп", "Voice replies", "Sesli yanıtlar", "Ovozli javoblar")),
    };

    private static ServerBotSettings? _botCache;
    private static DateTime _botCacheAt;

    private Task<(string Summary, IReadOnlyDictionary<int, string> Names)>? _summaryTask;
    private DateTime _summaryAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «голос очень сильно тормозит». Сводка магазина для ИИ — одна задача на минуту:
    /// запускается заранее (при открытии раздела и как только нажали микрофон — пока владелец говорит), части
    /// собираются одновременно, а не по очереди.</summary>
    private Task<(string Summary, IReadOnlyDictionary<int, string> Names)> GetSummaryAsync()
    {
        if (_summaryTask is { IsFaulted: false, IsCanceled: false } && DateTime.UtcNow - _summaryAtUtc < TimeSpan.FromMinutes(1))
            return _summaryTask;
        _summaryAtUtc = DateTime.UtcNow;
        return _summaryTask = BuildSummaryAsync();
    }

    private static async Task<(string Summary, IReadOnlyDictionary<int, string> Names)> BuildSummaryAsync()
    {
        var ct = CancellationToken.None;
        // 2026-10-05, владелец: «дай доступ ко всему для ИИ … к складу, к товарам» — цифры «Сводки» и склад.
        var warehouse = Task.Run(OwnerAiContext.BuildWarehouse);
        // «Я должен кому-то или мне должны?» — обезличенные итоги долгов клиентов и поставщиков.
        var debts = OwnerAiContext.BuildDebtTotalsAsync(ct);
        // «Дай список клиентов-должников»: должники под кодами [Д1]…, имена и телефоны подставляются здесь.
        var debtors = OwnerAiContext.BuildDebtorsPseudonymousAsync(ct);
        // 2026-10-05, владелец: «дай доступ к ABC-анализу ИИ».
        var abc = OwnerAiContext.BuildAbcAsync(ct);
        // 2026-10-05, владелец: «анализ продаж, склада, клиентов, заказов — чтобы предлагать акции на проблемные товары».
        var analysis = OwnerAiContext.BuildAnalysisAsync(ct);
        // 2026-10-05, владелец: «добавь возможность ИИ управлять ботом» — состояние функций бота на сервере.
        var bot = BuildBotStateAsync(ct);
        // 2026-10-05, ТЗ часть 7: итоги допродажи по всем кассам компании (сервер выложил 05.10).
        var upsell = BuildUpsellAsync(ct);
        await Task.WhenAll(warehouse, debts, debtors, abc, analysis, bot, upsell).ConfigureAwait(false);
        var (debtorsText, names) = debtors.Result;
        var summary = string.Join("\n", new[] { OwnerOverviewSnapshot.Text, bot.Result, upsell.Result, debts.Result, debtorsText, analysis.Result, abc.Result, warehouse.Result }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return (summary, names);
    }

    private static async Task<string> BuildUpsellAsync(CancellationToken ct)
    {
        try
        {
            var api = App.AppHost?.Services.GetService<RecommendationsApi>();
            if (api is null)
                return "";
            var stats = await api.GetStatsAsync(DateTime.Today.AddDays(-29), DateTime.Today, ct).ConfigureAwait(false);
            if (stats is null || stats.Shown == 0)
                return "";
            return $"ДОПРОДАЖА «С этим часто берут» (все кассы, 30 дней): показано {stats.Shown}, добавлено {stats.Accepted} "
                + $"({stats.AcceptanceRate:0.#} %), пропущено {stats.Skipped}, выручка {stats.Revenue:N0} сом, прибыль {stats.Profit:N0} сом.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: итоги допродажи не получены ({ex.Message}).", "DEBUG");
            return "";
        }
    }

    private static async Task<string> BuildBotStateAsync(CancellationToken ct)
    {
        try
        {
            if (_botCache is null || DateTime.UtcNow - _botCacheAt > TimeSpan.FromMinutes(2))
            {
                var api = App.AppHost?.Services.GetService<ServerTelegramBotApi>();
                if (api is null)
                    return "";
                _botCache = await api.GetSettingsAsync(ct).ConfigureAwait(false);
                _botCacheAt = DateTime.UtcNow;
            }
            var s = _botCache;
            if (s is null || !s.IsServerMode)
                return "БОТ: на сервере не подключён — функции бота меняются в разделе «Телеграм-бот» (ИИ менять их не может).";
            string On(bool v) => v ? "вкл" : "выкл";
            var state = $"БОТ (@{s.BotUsername}, на сервере): сводка смены {On(s.ShiftSummaryEnabled)}; команды владельца {On(s.CommandsEnabled)}; "
                + $"ИИ {On(s.AiEnabled)}{(s.AiKeySet ? "" : " (ключ ИИ на сервере не задан)")}; ИИ-консультант покупателям {On(s.ConsultantEnabled)}; "
                + $"ответы голосом {On(s.VoiceRepliesEnabled)}; напоминания о прокате {On(s.RentalReminders)}.";
            // 2026-10-05, ТЗ часть 11: свои сценарии и команды бота — если сервер их уже умеет.
            var api2 = App.AppHost?.Services.GetService<ServerTelegramBotApi>();
            var scenarios = api2 is null ? null : await api2.GetScenariosAsync(ct).ConfigureAwait(false);
            if (scenarios is null)
                return state + "\nСЦЕНАРИИ БОТА: сервер пока не поддерживает свои сценарии и команды.";
            var list = scenarios.Take(30).Select(x =>
                (x.Kind == "command" ? "/" + x.Command : "по словам: " + string.Join(", ", x.Keywords.Take(5)))
                + $" «{x.Title}»{(x.IsActive ? "" : " (выключен)")}, сработал {x.Hits} раз");
            return state + "\nСЦЕНАРИИ БОТА (свои ответы магазина): " + (scenarios.Count == 0 ? "пока нет." : string.Join("; ", list) + ".");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: состояние бота не получено ({ex.Message}).", "WARNING");
            return "";
        }
    }

    /// <summary>Строка «БОТ: {...}» из ответа ИИ → только разрешённые переключатели. Сама строка из ответа убирается.</summary>
    private static (string Answer, Dictionary<string, bool>? Change) ExtractBotChange(string answer)
    {
        var m = System.Text.RegularExpressions.Regex.Match(answer, @"(?im)^[ \t*`]*БОТ:\s*(\{[^\n]*\})[ \t*`]*$");
        if (!m.Success)
            return (answer, null);
        var cleaned = answer.Remove(m.Index, m.Length).TrimEnd();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
            var change = new Dictionary<string, bool>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (BotSwitches.Any(s => s.Key == p.Name) && p.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    change[p.Name] = p.Value.GetBoolean();
            }
            return (cleaned, change.Count > 0 ? change : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (cleaned, null);
        }
    }

    /// <summary>2026-10-05, ТЗ часть 11: строка «СЦЕНАРИЙ: {...}» из ответа ИИ → поля сценария (проверены здесь же:
    /// команда [a-z0-9_] до 32, слова 1–30, ответ до 3500). Сама строка из ответа убирается.</summary>
    private static (string Answer, Dictionary<string, object?>? Scenario) ExtractScenario(string answer)
    {
        var m = System.Text.RegularExpressions.Regex.Match(answer, @"(?im)^[ \t*`]*СЦЕНАРИЙ:\s*(\{.*\})[ \t*`]*$");
        if (!m.Success)
            return (answer, null);
        var cleaned = answer.Remove(m.Index, m.Length).TrimEnd();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()?.Trim() : null;
            var kind = Str("kind");
            var title = Str("title");
            var reply = Str("reply_text");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(reply) || reply.Length > 3500 || kind is not ("command" or "keywords"))
                return (cleaned, null);
            var body = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["title"] = title.Length > 80 ? title[..80] : title,
                ["reply_text"] = reply,
                ["audience"] = Str("audience") is "owner" or "all" ? Str("audience") : "customers",
                ["is_active"] = true,
                ["source"] = "ai_advisor",
            };
            if (kind == "command")
            {
                var command = (Str("command") ?? "").TrimStart('/').ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(command, "^[a-z0-9_]{1,32}$"))
                    return (cleaned, null);
                body["command"] = command;
                body["show_in_menu"] = !r.TryGetProperty("show_in_menu", out var sm) || sm.ValueKind != System.Text.Json.JsonValueKind.False;
            }
            else
            {
                var words = r.TryGetProperty("keywords", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? k.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length is >= 2 and <= 60).Distinct().Take(30).ToList()
                    : new List<string>();
                if (words.Count == 0)
                    return (cleaned, null);
                body["keywords"] = words;
            }
            return (cleaned, body);
        }
        catch (System.Text.Json.JsonException)
        {
            return (cleaned, null);
        }
    }

    private void AddScenarioCard(Dictionary<string, object?> scenario)
    {
        var trigger = scenario.TryGetValue("command", out var c) && c is string cmd
            ? T("Команда /", "Буйрук /", "Command /", "Komut /", "Buyruq /") + cmd
            : T("Слова: ", "Сөздөр: ", "Words: ", "Kelimeler: ", "So'zlar: ") + string.Join(", ", (List<string>)scenario["keywords"]!);
        var title = new TextBlock { Text = T("Добавить ответ бота?", "Боттун жообун кошолубу?", "Add a bot reply?", "Bot yanıtı eklensin mi?", "Bot javobini qo'shaymi?"), FontWeight = FontWeight.Bold, FontSize = 14.5 };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var body = new SelectableTextBlock
        {
            Text = $"«{scenario["title"]}»\n{trigger}\n\n{TelegramAiChat.ToPlainText(System.Text.RegularExpressions.Regex.Replace((string)scenario["reply_text"]!, "<[^>]+>", ""))}",
            FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
        };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        var apply = UiKit.Primary(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
        apply.Height = 38;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(result, TextBlock.ForegroundProperty, "BrushTextSoft");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        cancel.Click += (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Не добавляли.", "Кошулган жок.", "Not added.", "Eklenmedi.", "Qo'shilmadi.");
        };
        apply.Click += async (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Добавляю…", "Кошуп жатам…", "Adding…", "Ekleniyor…", "Qo'shilmoqda…");
            try
            {
                var api = App.AppHost?.Services.GetService<ServerTelegramBotApi>() ?? throw new InvalidOperationException("API бота недоступно");
                var created = await api.CreateScenarioAsync(scenario).ConfigureAwait(true);
                // Проверка «что ответит бот» — без отправки в Telegram (ТЗ часть 11, scenarios/test/).
                var sample = scenario.TryGetValue("command", out var sc) && sc is string sCmd ? "/" + sCmd : ((List<string>)scenario["keywords"]!)[0];
                string? reply = null;
                try
                {
                    reply = await api.TestScenarioAsync(sample, (string)scenario["audience"]!).ConfigureAwait(true);
                }
                catch (Exception testEx)
                {
                    PosLogger.Log($"ИИ-советник: проверка сценария не прошла ({testEx.Message}).", "WARNING");
                }
                result.Text = T("✓ Добавлено. ", "✓ Кошулду. ", "✓ Added. ", "✓ Eklendi. ", "✓ Qo'shildi. ")
                    + (reply is null ? "" : T($"На «{sample}» бот ответит: ", $"«{sample}» деп жазса, бот: ", $"For “{sample}” the bot replies: ", $"«{sample}» için bot yanıtı: ", $"«{sample}» ga bot javobi: ")
                        + System.Text.RegularExpressions.Regex.Replace(reply, "<[^>]+>", ""));
                _botCacheAt = DateTime.MinValue;
                PosLogger.Log($"ИИ-советник: сценарий бота «{created?.Title ?? scenario["title"]}» добавлен по подтверждению владельца.", "INFO");
            }
            catch (Exception ex)
            {
                buttons.IsVisible = true;
                result.Text = (ex is ApiException { StatusCode: 404 }
                    ? T("Сервер пока не умеет свои ответы бота — заработает после обновления сервера (ТЗ часть 11).",
                        "Сервер азырынча боттун өз жоопторун билбейт — сервер жаңырганда иштейт.",
                        "The server does not support custom bot replies yet — it will work after the server update.",
                        "Sunucu henüz özel bot yanıtlarını desteklemiyor — sunucu güncellemesinden sonra çalışacak.",
                        "Server hali botning o'z javoblarini qo'llamaydi — server yangilangach ishlaydi.")
                    : T("Не получилось: ", "Болбой калды: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
                PosLogger.Log($"ИИ-советник: сценарий бота не добавлен ({ex.Message}).", "WARNING");
            }
        };
        _messages.Children.Add(card);
        ScrollToEnd();
    }

    private void AddBotChangeCard(Dictionary<string, bool> change)
    {
        var lines = string.Join("\n", change.Select(c =>
            (c.Value ? T("Включить: ", "Күйгүзүү: ", "Turn on: ", "Aç: ", "Yoqish: ") : T("Выключить: ", "Өчүрүү: ", "Turn off: ", "Kapat: ", "O'chirish: "))
            + BotSwitches.First(s => s.Key == c.Key).Title));
        var title = new TextBlock { Text = T("Изменить настройки бота?", "Боттун жөндөөлөрүн өзгөртөлүбү?", "Change the bot settings?", "Bot ayarları değiştirilsin mi?", "Bot sozlamalarini o'zgartiraymi?"), FontWeight = FontWeight.Bold, FontSize = 14.5 };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var body = new TextBlock { Text = lines, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        var apply = UiKit.Primary(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
        apply.Height = 38;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(result, TextBlock.ForegroundProperty, "BrushTextSoft");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        cancel.Click += (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Не меняли.", "Өзгөртүлгөн жок.", "Not changed.", "Değiştirilmedi.", "O'zgartirilmadi.");
        };
        apply.Click += async (_, _) =>
        {
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Применяю…", "Колдонуп жатам…", "Applying…", "Uygulanıyor…", "Qo'llanmoqda…");
            try
            {
                var api = App.AppHost?.Services.GetService<ServerTelegramBotApi>() ?? throw new InvalidOperationException("API бота недоступно");
                var settings = await api.PatchSettingsAsync(change.ToDictionary(c => c.Key, c => (object?)c.Value)).ConfigureAwait(true);
                _botCache = settings;
                _botCacheAt = DateTime.UtcNow;
                result.Text = T("✓ Готово — бот уже работает с новыми настройками.", "✓ Даяр — бот жаңы жөндөөлөр менен иштеп жатат.", "✓ Done — the bot already uses the new settings.",
                    "✓ Tamam — bot yeni ayarlarla çalışıyor.", "✓ Tayyor — bot yangi sozlamalar bilan ishlayapti.");
                PosLogger.Log("ИИ-советник: настройки бота изменены по подтверждению владельца: "
                    + string.Join(", ", change.Select(c => $"{c.Key}={c.Value}")) + ".", "INFO");
            }
            catch (Exception ex)
            {
                buttons.IsVisible = true;
                result.Text = T("Не получилось: ", "Болбой калды: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + ServerTelegramBotApi.Describe(ex);
                PosLogger.Log($"ИИ-советник: настройки бота не изменены ({ex.Message}).", "WARNING");
            }
        };
        _messages.Children.Add(card);
        ScrollToEnd();
    }

    /// <summary>Значок кнопки: микрофон или «стоп» (квадрат). Рисованный — эмодзи 🎤 в Windows выглядел как ручка.</summary>
    private Control MicGlyph(bool recording)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(recording
                ? "M7,7 H17 V17 H7 Z"
                : "M12,3 C13.7,3 15,4.3 15,6 V11 C15,12.7 13.7,14 12,14 C10.3,14 9,12.7 9,11 V6 C9,4.3 10.3,3 12,3 Z "
                  + "M5.5,10.5 C5.5,14.1 8.4,17 12,17 C15.6,17 18.5,14.1 18.5,10.5 M12,17 V21 M8.5,21 H15.5"),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
        };
        Use(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, "BrushText");
        if (recording)
            Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushText");
        return path;
    }

    /// <summary>🎤: первое нажатие — запись, второе — стоп, распознавание и вопрос советнику.</summary>
    private async Task ToggleVoiceAsync()
    {
        if (_mic is null)
            return;
        if (_recorder is null)
        {
            if (_busy)
                return;
            VoiceChatPlayer.Stop();
            var recorder = new VoiceChatRecorder();
            recorder.MaxLengthReached += () => Dispatcher.UIThread.Post(() => _ = ToggleVoiceAsync());
            try
            {
                recorder.Start();
            }
            catch (Exception ex)
            {
                recorder.Dispose();
                PosLogger.Log($"ИИ-советник: микрофон не включился ({ex.Message}).", "WARNING");
                AddBubble(T("Микрофон не включился: ", "Микрофон күйгөн жок: ", "The microphone did not start: ", "Mikrofon açılmadı: ", "Mikrofon yoqilmadi: ") + ex.Message, fromOwner: false);
                return;
            }
            _recorder = recorder;
            // Пока владелец говорит — собираем сводку магазина, к концу распознавания она уже готова.
            _ = GetSummaryAsync();
            _mic.Content = MicGlyph(recording: true);
            Use(_mic, Button.BackgroundProperty, "BrushDanger");
            _input.Watermark = T("Говорите… нажмите ■, когда закончите", "Сүйлөңүз… бүткөндө ■ басыңыз", "Speak… press ■ when you finish",
                "Konuşun… bitince ■ basın", "Gapiring… tugatgach ■ ni bosing");
            return;
        }

        var rec = _recorder;
        _recorder = null;
        var wav = rec.Stop();
        rec.Dispose();
        _mic.Content = MicGlyph(recording: false);
        Use(_mic, Button.BackgroundProperty, "BrushPanel");
        var watermark = T("Напишите вопрос и нажмите Enter…", "Суроону жазып, Enter басыңыз…", "Type a question and press Enter…", "Sorunuzu yazıp Enter'a basın…", "Savolni yozib, Enter bosing…");
        if (wav is null)
        {
            _input.Watermark = watermark;
            return;
        }
        _mic.IsEnabled = false;
        _input.Watermark = T("Распознаю речь…", "Сөздү таанып жатам…", "Recognising speech…", "Konuşma tanınıyor…", "Nutq tanilmoqda…");
        string? text = null;
        var sttWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            text = await TelegramVoice.TranscribeAsync(wav, "audio/wav", cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: речь не распознана ({ex.Message}).", "WARNING");
        }
        _input.Watermark = watermark;
        RefreshKeyCard();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddBubble(T("Не расслышал. Скажите ещё раз, ближе к микрофону.", "Уккан жокмун. Микрофонго жакыныраак кайра айтыңыз.", "I didn't catch that. Please say it again, closer to the microphone.",
                "Duyamadım. Mikrofona daha yakın tekrar söyleyin.", "Eshitmadim. Mikrofonga yaqinroq qayta ayting."), fromOwner: false);
            return;
        }
        PosLogger.Log($"ИИ-советник: вопрос голосом ({text.Length} симв., запись {wav.Length / 32000.0:0.#} с, распознано за {sttWatch.ElapsedMilliseconds} мс).", "INFO");
        _voiceAnswer = true;
        await SendAsync(text).ConfigureAwait(true);
    }

    /// <summary>Озвучка ответа: первая фраза — отдельным коротким запросом (звучит через 1–2 с), остальное готовится
    /// одновременно и играет следом. Не больше двух запросов на ответ — у бесплатного ключа малый лимит озвучки.</summary>
    private async Task SpeakAsync(string text)
    {
        VoiceChatPlayer.Stop();
        var generation = VoiceChatPlayer.Generation;
        var (first, rest) = SplitForSpeech(TelegramAiChat.ToPlainText(text));
        var more = T("Подробности — на экране.", "Толугураак — экранда.", "More details are on the screen.", "Ayrıntılar ekranda.", "Batafsil — ekranda.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var firstTask = TelegramVoice.SynthesizePcmAsync(first, more, cts.Token);
            var restTask = rest.Length > 0 ? TelegramVoice.SynthesizePcmAsync(rest, more, cts.Token) : null;
            var (pcm, rate) = await firstTask.ConfigureAwait(true);
            PosLogger.Log($"ИИ-советник: первая фраза озвучена за {watch.ElapsedMilliseconds} мс ({first.Length} симв.{(rest.Length > 0 ? $", дальше ещё {rest.Length}" : "")}).", "INFO");
            if (pcm is not null && IsVisible)
                VoiceChatPlayer.Enqueue(pcm, rate, generation);
            if (restTask is not null)
            {
                var (pcm2, rate2) = await restTask.ConfigureAwait(true);
                if (pcm2 is not null && IsVisible)
                    VoiceChatPlayer.Enqueue(pcm2, rate2, generation);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: ответ не озвучен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Первая фраза (до ~110 знаков) и остальное. Замер 05.10: короткая фраза озвучивается за 2,6–4,3 с,
    /// 176 знаков — за 6,5–10 с, поэтому первая фраза идёт отдельным коротким запросом, остальное — параллельно.</summary>
    private static (string Head, string Tail) SplitForSpeech(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (t.Length <= 110)
            return (t, "");
        var cut = -1;
        for (var i = 20; i < Math.Min(t.Length, 120); i++)
        {
            if (t[i] is '.' or '!' or '?' && (i + 1 == t.Length || t[i + 1] == ' '))
            {
                cut = i + 1;
                break;
            }
        }
        if (cut < 0)
            cut = t.LastIndexOf(' ', Math.Min(t.Length - 1, 100)) is var sp and > 20 ? sp : Math.Min(t.Length, 100);
        return (t[..cut].Trim(), t[cut..].Trim());
    }

    /// <summary>Источники из интернета под ответом: «🌐 Источники:» и кнопки-ссылки (открываются в браузере).</summary>
    private void AddWebSources(IReadOnlyList<TelegramAiChat.WebSource> sources)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var label = new TextBlock { Text = T("🌐 Найдено в интернете:", "🌐 Интернеттен табылды:", "🌐 Found on the web:", "🌐 İnternette bulundu:", "🌐 Internetdan topildi:"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(label);
        foreach (var source in sources.Take(5))
        {
            var title = source.Title.Length > 40 ? source.Title[..40] + "…" : source.Title;
            var link = UiKit.Ghost(this, title);
            link.Height = 28;
            link.FontSize = 12;
            link.Padding = new Thickness(10, 0);
            link.Margin = new Thickness(0, 0, 6, 6);
            ToolTip.SetTip(link, source.Uri);
            link.Click += async (_, _) =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                        await launcher.LaunchUriAsync(new Uri(source.Uri)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"ИИ-советник: ссылка не открылась ({ex.Message}).", "WARNING");
                }
            };
            panel.Children.Add(link);
        }
        _messages.Children.Add(panel);
    }

    private static bool IsPhotoRequest(string text)
    {
        var t = text.ToLowerInvariant();
        var photo = new[] { "фото", "фотк", "сүрөт", "photo", "picture", "image", "fotoğraf", "resim", "rasm", "surat" }.Any(t.Contains);
        // Начало слова, а не подстрока: иначе «кой» находится в «какой», «add» — в «address».
        var words = t.Split(new[] { ' ', ',', '.', '!', '?', '\n', '«', '»', '"' }, StringSplitOptions.RemoveEmptyEntries);
        var stems = new[] { "загруз", "найд", "найт", "постав", "добав", "ищи", "поищ", "жүктө", "тап", "таб", "кой", "кою", "upload", "find", "add", "set", "yükle", "bul", "ekle", "yukla", "top", "qo'sh" };
        var act = words.Any(w => stems.Any(w.StartsWith));
        return photo && act;
    }

    /// <summary>2026-10-05: «загрузи фото к товарам, у которых нет фото». Товары без фото → поиск по штрихкоду в
    /// открытых базах (ProductPhotoFinder) → найденные показываются с «Поставить» / «Поставить все»; остальные —
    /// с «📷 Добавить фото» (файл или камера, как на складе). На сервер фото уходит только по нажатию владельца.</summary>
    private async Task RunPhotoAssistantAsync(string question)
    {
        // 2026-10-05, владелец: «чтобы ИИ смог по команде найти фото по названию или штрихкоду товара в интернете, если в
        // базе нет». Названы товары («найди фото для кока колы», штрихкод) — ищем для них; иначе — для всех без фото.
        var named = ProductPhotoFinder.MatchRequest(question);
        var withoutPhoto = named.Count > 0 ? named : ProductPhotoFinder.WithoutPhoto();
        if (withoutPhoto.Count == 0)
        {
            AddBubble(T("У всех товаров склада уже есть фото.", "Кампадагы бардык товарлардын сүрөтү бар.", "Every product in the warehouse already has a photo.",
                "Depodaki tüm ürünlerin fotoğrafı var.", "Ombordagi barcha mahsulotlarning rasmi bor."), fromOwner: false);
            return;
        }
        // Штрихкод и название — для всех (до 80); в интернете (поиск Google, по 5–10 с на товар, лимит ключа) — для
        // названных товаров или первых 10 без фото.
        var searchable = withoutPhoto.Take(named.Count > 0 ? 10 : 80).ToList();
        var webLimit = TelegramAiChat.IsConfigured ? (named.Count > 0 ? searchable.Count : 10) : 0;
        var status = AddBubble(named.Count > 0
            ? T($"Ищу фото: {string.Join(", ", named.Take(5).Select(p => p.Title))} — по штрихкоду, названию и в интернете…",
                $"Сүрөт издеп жатам: {string.Join(", ", named.Take(5).Select(p => p.Title))} — штрихкод, аталыш жана интернет боюнча…",
                $"Searching photos: {string.Join(", ", named.Take(5).Select(p => p.Title))} — by barcode, name and on the web…",
                $"Fotoğraf arıyorum: {string.Join(", ", named.Take(5).Select(p => p.Title))} — barkod, ad ve internette…",
                $"Rasm qidiryapman: {string.Join(", ", named.Take(5).Select(p => p.Title))} — shtrix-kod, nom va internet bo'yicha…")
            : T($"Без фото: {withoutPhoto.Count} товаров. Ищу по штрихкоду и названию в открытых базах, в интернете — для первых {webLimit}…",
                $"Сүрөтсүз: {withoutPhoto.Count} товар. Ачык базалардан штрихкод жана аталыш боюнча, интернеттен — алгачкы {webLimit} үчүн издеп жатам…",
                $"Without a photo: {withoutPhoto.Count} products. Searching open databases by barcode and name, the web for the first {webLimit}…",
                $"Fotoğrafsız: {withoutPhoto.Count} ürün. Açık veritabanlarında barkod ve adla, internette ilk {webLimit} için arıyorum…",
                $"Rasmsiz: {withoutPhoto.Count} ta mahsulot. Ochiq bazalardan shtrix-kod va nom bo'yicha, internetdan — birinchi {webLimit} tasi uchun qidiryapman…"), fromOwner: false);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        List<ProductPhotoFinder.Candidate> found;
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> notFound;
        try
        {
            (found, notFound) = await Task.Run(() => ProductPhotoFinder.SearchAsync(searchable,
                (done, total) => Dispatcher.UIThread.Post(() => status.Text = T($"Ищу фото… {done} из {total}", $"Сүрөт издеп жатам… {done} / {total}",
                    $"Searching… {done} of {total}", $"Aranıyor… {done} / {total}", $"Qidiryapman… {done} / {total}")), ct, webLimit), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // 2026-10-05, проверка: поиск молча обрывался — теперь причина видна владельцу и в журнале.
            PosLogger.Log($"ИИ-советник: поиск фото прерван ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            status.Text = T("Не получилось искать фото: ", "Сүрөт издөө болбой калды: ", "Photo search failed: ", "Fotoğraf aranamadı: ", "Rasm qidirib bo'lmadi: ") + ex.Message;
            return;
        }
        notFound.AddRange(withoutPhoto.Where(p => !searchable.Contains(p)));
        status.Text = T($"Нашёл фото для {found.Count} товаров. Проверьте и нажмите «Поставить» — фото уйдёт в карточку товара на сервере.",
            $"{found.Count} товардын сүрөтү табылды. Текшерип, «Коюу» басыңыз — сүрөт сервердеги товар карточкасына кетет.",
            $"Found photos for {found.Count} products. Check them and press “Set” — the photo goes to the product card on the server.",
            $"{found.Count} ürün için fotoğraf bulundu. Kontrol edip «Ayarla»ya basın — fotoğraf sunucudaki ürün kartına gider.",
            $"{found.Count} ta mahsulot uchun rasm topildi. Tekshirib «O'rnatish»ni bosing — rasm serverdagi mahsulot kartasiga ketadi.");

        if (found.Count > 0)
        {
            var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            var all = UiKit.Primary(this, T($"Поставить все ({found.Count})", $"Баарын коюу ({found.Count})", $"Set all ({found.Count})", $"Tümünü ayarla ({found.Count})", $"Hammasini o'rnatish ({found.Count})"));
            all.Margin = new Thickness(4, 0, 0, 8);
            var setButtons = new List<(Button Button, ProductPhotoFinder.Candidate Candidate)>();
            foreach (var candidate in found)
            {
                var image = new Image { Width = 112, Height = 112, Stretch = Stretch.UniformToFill };
                _ = LoadPreviewAsync(image, candidate.ImageUrl);
                var caption = new TextBlock { Text = candidate.Product.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 112, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(caption, TextBlock.ForegroundProperty, "BrushText");
                var source = new TextBlock { Text = candidate.Source, FontSize = 10.5, MaxWidth = 112 };
                Use(source, TextBlock.ForegroundProperty, "BrushTextSoft");
                var set = UiKit.Ghost(this, T("Поставить", "Коюу", "Set", "Ayarla", "O'rnatish"));
                set.Height = 32;
                set.FontSize = 12.5;
                set.Padding = new Thickness(8, 0);
                set.Click += async (_, _) => await ApplyPhotoAsync(set, candidate).ConfigureAwait(true);
                setButtons.Add((set, candidate));
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
                    Child = new StackPanel { Spacing = 4, Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, caption, source, set } },
                };
                Use(card, Border.BackgroundProperty, "BrushPanel");
                Use(card, Border.BorderBrushProperty, "BrushBorder");
                strip.Children.Add(card);
            }
            all.Click += async (_, _) =>
            {
                all.IsEnabled = false;
                foreach (var (button, candidate) in setButtons.Where(b => b.Button.IsEnabled).ToList())
                    await ApplyPhotoAsync(button, candidate).ConfigureAwait(true);
            };
            _messages.Children.Add(all);
            _messages.Children.Add(strip);
        }

        if (notFound.Count > 0)
        {
            AddBubble(T($"Для {notFound.Count} товаров фото в базах нет (или нет штрихкода). Сфотографируйте их — нажмите на товар ниже и выберите снимок:",
                $"{notFound.Count} товардын сүрөтү базаларда жок (же штрихкод жок). Аларды сүрөткө тартыңыз — төмөндөгү товарды басып, сүрөттү тандаңыз:",
                $"No photo in the databases for {notFound.Count} products (or no barcode). Take photos — tap a product below and pick the picture:",
                $"{notFound.Count} ürün için veritabanlarında fotoğraf yok (veya barkod yok). Fotoğraflarını çekin — aşağıdaki ürüne dokunup resmi seçin:",
                $"{notFound.Count} ta mahsulot uchun bazalarda rasm yo'q (yoki shtrix-kod yo'q). Ularni suratga oling — pastdagi mahsulotni bosing va rasmni tanlang:"), fromOwner: false);
            var list = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var product in notFound.Take(40))
            {
                var b = UiKit.Ghost(this, "📷 " + product.Title);
                b.Height = 34;
                b.FontSize = 12.5;
                b.Padding = new Thickness(10, 0);
                b.Margin = new Thickness(0, 0, 6, 6);
                b.Click += async (_, _) => await PickAndUploadAsync(b, product).ConfigureAwait(true);
                list.Children.Add(b);
            }
            _messages.Children.Add(list);
        }
        ScrollToEnd();
    }

    private static async Task LoadPreviewAsync(Image image, string url)
    {
        var bytes = await ProductPhotoFinder.DownloadAsync(url, CancellationToken.None).ConfigureAwait(true);
        if (bytes is not { Length: > 0 })
            return;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            image.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 224);
        }
        catch
        {
            // картинка не читается — остаётся пустая рамка
        }
    }

    private async Task ApplyPhotoAsync(Button button, ProductPhotoFinder.Candidate candidate)
    {
        button.IsEnabled = false;
        button.Content = T("Загружаю…", "Жүктөп жатам…", "Uploading…", "Yükleniyor…", "Yuklanmoqda…");
        bool ok;
        try
        {
            ok = await ProductPhotoFinder.ApplyAsync(candidate, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото «{candidate.Product.Title}» не загружено ({ex.Message}).", "WARNING");
            ok = false;
        }
        button.Content = ok
            ? T("✓ Поставлено", "✓ Коюлду", "✓ Set", "✓ Ayarlandı", "✓ O'rnatildi")
            : T("Не получилось", "Болбой калды", "Failed", "Olmadı", "Bo'lmadi");
        button.IsEnabled = !ok;
    }

    private async Task PickAndUploadAsync(Button button, NurMarketKassa.Models.Pos.CatalogProductTileVm product)
    {
        var picker = App.AppHost?.Services.GetService<NurMarketKassa.Ui.Shared.ISettingsImagePicker>();
        if (picker is null)
            return;
        var file = await picker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(file))
            return;
        button.IsEnabled = false;
        bool ok;
        try
        {
            ok = await ProductPhotoFinder.UploadAsync(product, file, "снято владельцем", CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото «{product.Title}» не загружено ({ex.Message}).", "WARNING");
            ok = false;
        }
        button.Content = (ok ? "✓ " : "✗ ") + product.Title;
        button.IsEnabled = !ok;
    }

    /// <summary>2026-10-05, владелец: «добавь ИИ отправлять фото товара, если есть». Под ответом — фото товаров,
    /// которые советник назвал (до 4), из того же кэша фото, что у плиток каталога.</summary>
    private void AddProductPhotos(string answer)
    {
        var products = OwnerAiContext.FindMentionedProducts(answer);
        if (products.Count == 0)
            return;
        var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var services = App.AppHost?.Services;
        foreach (var product in products)
        {
            var image = new Image { Width = 112, Height = 112, Stretch = Stretch.UniformToFill };
            image.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(product.ProductImagePath))
            {
                Source = product,
                Converter = NurMarketKassa.AvaloniaHost.Converters.AssetPathToBitmapConverter.Instance,
                ConverterParameter = "thumb",
            });
            var caption = new TextBlock
            {
                Text = product.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 112,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Margin = new Thickness(0, 4, 0, 0),
            };
            Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
            var card = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new StackPanel { Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, caption } },
            };
            Use(card, Border.BackgroundProperty, "BrushPanel");
            Use(card, Border.BorderBrushProperty, "BrushBorder");
            strip.Children.Add(card);
            // Фото ещё не скачано — качаем в кэш (как плитка каталога); путь подставится в картинку сам.
            if (string.IsNullOrEmpty(product.ProductImagePath) && !string.IsNullOrWhiteSpace(product.ImageUrl) && services is not null)
            {
                _ = services.GetRequiredService<ProductThumbService>().SetThumbAsync(
                    Dispatcher.UIThread, services.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>(),
                    services.GetRequiredService<NurMarketKassa.Configuration.AppSettings>().ApiBaseUrl,
                    product.ImageUrl!, product, CancellationToken.None);
            }
        }
        _messages.Children.Add(strip);
        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
