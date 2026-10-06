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
    // 2026-10-05, владелец: «начни редизайн окна чата с ИИ». Как в современных чатах: слева — история разговоров
    // («＋ Новый разговор», по дням), справа — заголовок с подключёнными моделями, лента по центру (вопрос — пузырь справа,
    // ответ ИИ — со значком ✦ и кнопками «Копировать» / «Озвучить»), пустой чат — приветствие и готовые вопросы
    // карточками, внизу — скруглённое поле ввода с микрофоном и круглой кнопкой ➤ / ■. На телефоне история — по ☰.
    private readonly Grid _shell = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
    private readonly Border _sidebar = new() { Width = 264, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(12, 14) };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(24, 14, 24, 14) };
    private readonly StackPanel _messages = new() { Spacing = 16, Margin = new Thickness(4, 8, 12, 8), MaxWidth = 860 };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private Button? _menuButton;
    // 2026-10-05, владелец (снимок): «некрасиво, два меню — неудобно». История — не постоянной панелью рядом с меню
    // разделов, а выдвижной панелью справа по кнопке «История»; в программе владельца название раздела не повторяется.
    private Control? _headerTitle;
    private bool _narrow;
    private Control? _welcome;

    /// <summary>Реплика на экране: кто сказал и текст (для сохранения разговора).</summary>
    private sealed record MessageTag(bool Owner, SelectableTextBlock Body);
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

    // 2026-10-05, владелец: «найди бесплатную модель, которая ищет в интернете», «настрой несколько моделей» — ключи
    // запасных моделей и поиска в интернете (AiProviders): ⚙ в строке ввода.
    private readonly Border _modelsCard = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly Border _routerSection = new();

    // 2026-10-05, владелец: «баг: при нажатии «Новый разговор» — где старый чат и почему новый чат завис?» Шёл поиск фото
    // (долгий), «Новый разговор» стирал экран, но поиск не останавливал — ввод оставался выключенным. Теперь: _gen — номер
    // текущей работы (старая после остановки UI не трогает), «Спросить» во время работы — «■ Стоп», прошлые разговоры
    // сохраняются (ai-chats.json в папке данных компании) и открываются кнопкой «История».
    private int _gen;
    private string _chatId = Guid.NewGuid().ToString("N");
    private readonly Border _historyCard = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly StackPanel _historyList = new() { Spacing = 6 };
    private bool _busy;

    // 2026-10-05, владелец: «постоянное голосовое общение как ChatGPT можем?» → «Делать? да». Кнопка «Разговор»:
    // живой разговор голосом (GeminiLiveVoice) — микрофон слушает всё время, советник отвечает голосом сразу,
    // текст обеих сторон пишется в чат и в историю. Вместо поля ввода — панель разговора (круг, состояние, «Перебить», «Завершить»).
    private GeminiLiveVoice? _live;
    private int _liveGen;
    private Button? _talk;
    private Border? _composer;
    private readonly Border _liveBar = new() { CornerRadius = new CornerRadius(30), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 12, 8), MaxWidth = 860, IsVisible = false };
    private readonly Border _orb = new() { Width = 52, Height = 52, CornerRadius = new CornerRadius(26) };
    private readonly ScaleTransform _orbScale = new(1, 1);
    private readonly TextBlock _liveState = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _liveHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private Button? _liveInterrupt;
    private DispatcherTimer? _liveTimer;
    private SelectableTextBlock? _liveUser;
    private SelectableTextBlock? _liveAi;
    private readonly System.Text.StringBuilder _liveUserText = new();
    private readonly System.Text.StringBuilder _liveAiText = new();
    private IReadOnlyDictionary<int, string> _liveDebtors = new Dictionary<int, string>();

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public AiAdvisorWindow()
    {
        Title = T("ИИ-советник", "ИИ-кеңешчи", "AI advisor", "Yapay zekâ danışmanı", "SI maslahatchi");
        Width = 900;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        BuildHeader();

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
        // Ключ Gemini и «Модели ИИ» — друг под другом в одной строке разметки.
        BuildModelsCard();
        BuildHistoryCard();
        var topCards = new StackPanel { Children = { _keyCard, _modelsCard } };
        Grid.SetRow(topCards, 1);
        _root.Children.Add(topCards);

        // Разговор: лента по центру, без рамки.
        _scroll.Content = _messages;
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Grid.SetRow(_scroll, 2);
        _root.Children.Add(_scroll);

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
            chip.Content = new TextBlock { Text = q, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
            chip.Tag = q;
            chip.Width = 252;
            chip.MinHeight = 58;
            chip.CornerRadius = new CornerRadius(14);
            chip.Padding = new Thickness(14, 10);
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            chip.Margin = new Thickness(0, 0, 10, 10);
            chip.Click += async (_, _) => await SendAsync(q).ConfigureAwait(true);
            _quick.Children.Add(chip);
        }

        // Поле ввода.
        var composer = new Border { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1), Padding = new Thickness(14, 4, 6, 4), MaxWidth = 860 };
        Use(composer, Border.BackgroundProperty, "BrushPanel");
        Use(composer, Border.BorderBrushProperty, "BrushBorder");
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        _input = UiKit.Input(this, T("Спросите о своём магазине…", "Дүкөнүңүз жөнүндө сураңыз…", "Ask about your shop…", "Mağazanız hakkında sorun…", "Do'koningiz haqida so'rang…"), 44);
        _input.BorderThickness = new Thickness(0);
        _input.Background = Brushes.Transparent;
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await SendAsync(_input.Text).ConfigureAwait(true);
        };
        inputRow.Children.Add(_input);
        AddHandler(KeyDownEvent, ScanKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (VoiceChatRecorder.IsSupported)
        {
            _mic = RoundButton(MicGlyph(recording: false), accent: false);
            ToolTip.SetTip(_mic, T("Спросить голосом: нажмите, говорите, нажмите ещё раз — ответ прозвучит вслух",
                "Үн менен суроо: басыңыз, сүйлөңүз, кайра басыңыз — жооп үн менен угулат",
                "Ask by voice: press, speak, press again — the answer will be read aloud",
                "Sesle sor: basın, konuşun, tekrar basın — yanıt sesli okunur",
                "Ovoz bilan so'rash: bosing, gapiring, yana bosing — javob ovoz bilan o'qiladi"));
            _mic.Click += async (_, _) => await ToggleVoiceAsync().ConfigureAwait(true);
            Grid.SetColumn(_mic, 2);
            inputRow.Children.Add(_mic);
            _talk = RoundButton(WaveGlyph("BrushText"), accent: false);
            _talk.Margin = new Thickness(6, 0, 0, 0);
            ToolTip.SetTip(_talk, T("Разговор голосом, как по телефону: говорите — советник сразу отвечает голосом и снова слушает",
                "Үн менен маек, телефондогудай: сүйлөңүз — кеңешчи дароо үн менен жооп берип, кайра угат",
                "Voice conversation, like a phone call: speak — the advisor answers by voice right away and listens again",
                "Telefondaki gibi sesli sohbet: konuşun — danışman hemen sesle yanıtlar ve yeniden dinler",
                "Telefondagidek ovozli suhbat: gapiring — maslahatchi darhol ovoz bilan javob beradi va yana tinglaydi"));
            _talk.Click += async (_, _) => await StartLiveAsync().ConfigureAwait(true);
            Grid.SetColumn(_talk, 3);
            inputRow.Children.Add(_talk);
        }
        _send = RoundButton(SendGlyph(stop: false), accent: true);
        _send.Margin = new Thickness(6, 0, 0, 0);
        _send.Click += async (_, _) =>
        {
            if (_busy)
                StopCurrent(showNote: true);
            else
                await SendAsync(_input.Text).ConfigureAwait(true);
        };
        Grid.SetColumn(_send, 4);
        inputRow.Children.Add(_send);
        // 2026-10-06, владелец: «добавь загрузку фото накладной в наш ИИ, чтобы он мог загрузить с маржей на склад».
        var attach = RoundButton(new TextBlock { Text = "📎", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, accent: false);
        attach.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(attach, T("Приложить фото: накладная поставщика — ИИ оприходует товары с наценкой; фото товара — опишет и заполнит карточку",
            "Сүрөт тиркөө: жеткирүүчүнүн накладнойу — ИИ товарларды үстөк менен кириштейт; товардын сүрөтү — сүрөттөп, карточкасын толтурат",
            "Attach a photo: a supplier invoice — the AI receives the goods with a markup; a product photo — it describes it and fills in the card",
            "Fotoğraf ekle: tedarikçi faturası — yapay zekâ ürünleri kâr payıyla stoğa alır; ürün fotoğrafı — tanımlar ve kartı doldurur",
            "Surat biriktirish: yetkazib beruvchi yuk xati — SI mahsulotlarni ustama bilan kirim qiladi; mahsulot surati — tavsiflab, kartani to'ldiradi"));
        attach.Click += async (_, _) => await PickPhotoAsync().ConfigureAwait(true);
        Grid.SetColumn(attach, 1);
        inputRow.Children.Add(attach);
        _attachRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 6, 0, 2), IsVisible = false };
        composer.Child = new StackPanel { Children = { _attachRow, inputRow } };
        Grid.SetRow(composer, 3);
        _root.Children.Add(composer);
        _composer = composer;
        BuildLiveBar();
        var footnote = new TextBlock
        {
            Text = T("ИИ может ошибаться — важное проверяйте. Цифры берутся из данных вашего магазина.",
                "ИИ жаңылышы мүмкүн — маанилүүнү текшериңиз. Сандар дүкөнүңүздүн маалыматынан алынат.",
                "AI can make mistakes — check what matters. Numbers come from your shop's data.",
                "Yapay zekâ hata yapabilir — önemli olanı kontrol edin. Rakamlar mağazanızın verilerinden gelir.",
                "SI xato qilishi mumkin — muhimini tekshiring. Raqamlar do'koningiz ma'lumotlaridan olinadi."),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        };
        Use(footnote, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetRow(footnote, 4);
        _root.Children.Add(footnote);

        BuildSidebar();
        Grid.SetColumn(_root, 1);
        Grid.SetColumn(_sidebar, 1);
        _sidebar.ZIndex = 10;
        _sidebar.IsVisible = false;
        _sidebar.Width = 320;
        _sidebar.HorizontalAlignment = HorizontalAlignment.Right;
        _sidebar.Margin = new Thickness(0, 64, 16, 16);
        _sidebar.CornerRadius = new CornerRadius(14);
        _sidebar.BorderThickness = new Thickness(1);
        _sidebar.BoxShadow = BoxShadows.Parse("0 8 28 0 #50000000");
        _scroll.PointerPressed += (_, _) => _sidebar.IsVisible = false;
        _shell.Children.Add(_root);
        _shell.Children.Add(_sidebar);
        Content = _shell;

        // Телефон: история — поверх ленты по ☰; ключ — поле на всю ширину, кнопки строкой ниже.
        NarrowLayout.Attach(this, 760, narrow =>
        {
            _narrow = narrow;
            _root.Margin = narrow ? new Thickness(10, 8, 10, 10) : new Thickness(24, 14, 24, 14);
            _sidebar.Width = narrow ? double.NaN : 320;
            _sidebar.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
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
        ShowWelcome();
        RefreshHistoryList();

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
            EndLive(null);
            SaveCurrentChat();
            _cts?.Cancel();
            _recorder?.Dispose();
            _recorder = null;
            VoiceChatPlayer.Stop();
        };
    }

    public void AsOwnerSection()
    {
        // Панель истории — вплотную к краю раздела, отступ раздела — у ленты.
        var m = OwnerSectionLayout.Margin;
        _shell.Margin = new Thickness(0, m.Top, 0, 0);
        _root.Margin = new Thickness(m.Left, 0, m.Right, m.Bottom);
        // Название раздела уже есть в полосе программы владельца — не повторяем.
        if (_headerTitle is not null)
            _headerTitle.IsVisible = false;
    }

    private void RefreshKeyCard()
    {
        _keyCard.IsVisible = !TelegramAiChat.IsConfigured;
        _input.IsEnabled = TelegramAiChat.IsConfigured && !_busy;
        // Во время ответа или поиска фото — «■ Стоп» (раньше кнопка была просто серой, казалось — зависло).
        _send.IsEnabled = TelegramAiChat.IsConfigured || _busy;
        _send.Content = SendGlyph(stop: _busy);
        ToolTip.SetTip(_send, _busy
            ? T("Остановить", "Токтотуу", "Stop", "Durdur", "To'xtatish")
            : T("Спросить", "Суроо", "Ask", "Sor", "So'rash"));
        RefreshStatus();
        // 2026-10-05, проверка на телефоне: без ключа ИИ не работала и кнопка «Найди фото…», хотя поиску фото
        // нейросеть не нужна (открытые базы товаров по штрихкоду) — она доступна всегда.
        foreach (var chip in _quick.Children.OfType<Button>())
            chip.IsEnabled = !_busy && (TelegramAiChat.IsConfigured || chip.Tag is string text && IsPhotoRequest(text));
        if (_mic is not null)
            _mic.IsEnabled = TelegramAiChat.IsConfigured && (!_busy || _recorder is not null);
        if (_talk is not null)
            _talk.IsEnabled = TelegramAiChat.IsConfigured && !_busy && _recorder is null;
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
        StopCurrent(showNote: false);
        SaveCurrentChat();
        _chatId = Guid.NewGuid().ToString("N");
        TelegramAiChat.ResetOwnerAppHistory();
        _messages.Children.Clear();
        ShowWelcome();
        RefreshHistoryList();
        _sidebar.IsVisible = false;
        _input.Focus();
    }

    private async Task SendAsync(string? text)
    {
        var question = (text ?? "").Trim();
        // 2026-10-06: приложено фото без вопроса — по умолчанию это накладная.
        if (question.Length == 0 && _attachedImage is not null)
            question = T("Это фото накладной — оприходуй товары на склад.", "Бул накладнойдун сүрөтү — товарларды кампага кириште.",
                "This is an invoice photo — receive the goods into stock.", "Bu bir fatura fotoğrafı — ürünleri stoğa al.",
                "Bu yuk xati surati — mahsulotlarni omborga kirim qil.");
        if (question.Length == 0 || _busy || (!TelegramAiChat.IsConfigured && !IsPhotoRequest(question)))
            return;
        var image = _attachedImage;
        var imageMime = _attachedMime;
        var imageName = _attachedName;
        ClearAttachment();
        // Вслух отвечаем только на вопрос голосом; напечатанный вопрос — молча, как раньше.
        var speak = _voiceAnswer;
        _voiceAnswer = false;
        VoiceChatPlayer.Stop();
        var gen = ++_gen;
        _busy = true;
        _input.Text = "";
        RefreshKeyCard();
        AddBubble(image is null ? question : "📎 " + imageName + "\n" + question, fromOwner: true);
        // 2026-10-05, владелец: «добавь техническую возможность к ИИ для загрузки фото на склад» — просьба про фото
        // товаров выполняется программой сама (поиск по штрихкоду, загрузка после подтверждения), без нейросети.
        if (image is null && IsPhotoRequest(question))
        {
            try
            {
                await RunPhotoAssistantAsync(question).ConfigureAwait(true);
            }
            finally
            {
                if (gen == _gen)
                {
                    _busy = false;
                    RefreshKeyCard();
                    SaveCurrentChat();
                }
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
            var (answer, error) = await TelegramAiChat.AskOwnerAppAsync(askText, summary, _cts.Token, image, imageMime).ConfigureAwait(true);
            if (gen != _gen)
                return;
            PosLogger.Log($"ИИ-советник: сводка {summaryMs} мс, ответ ИИ {watch.ElapsedMilliseconds - summaryMs} мс{(speak ? " (голосом)" : "")}.", "INFO");
            Dictionary<string, bool>? botChange = null;
            Dictionary<string, object?>? scenario = null;
            // 2026-10-06, владелец: «к ИИ дай полный доступ к товарам» — строки «ТОВАР: {…}» → карточка «Выполнить».
            List<ProductActionPlan.Step>? productSteps = null;
            if (answer is { Length: > 0 })
            {
                (answer, botChange) = ExtractBotChange(answer);
                (answer, scenario) = ExtractScenario(answer);
                (answer, productSteps) = ProductActionPlan.Extract(answer);
                if (answer.Length == 0 && productSteps.Count > 0)
                    answer = T("Предлагаю изменения — подтвердите:", "Өзгөртүүлөрдү сунуштайм — ырастаңыз:", "I suggest these changes — please confirm:",
                        "Şu değişiklikleri öneriyorum — onaylayın:", "Quyidagi o'zgarishlarni taklif qilaman — tasdiqlang:");
                AddProductPhotos(answer);
            }
            thinking.Text = answer is { Length: > 0 }
                ? OwnerAiContext.RevealDebtors(TelegramAiChat.ToPlainText(answer), debtorNames)
                : T("Не получилось ответить: ", "Жооп берүү мүмкүн болгон жок: ", "Couldn't answer: ", "Yanıt verilemedi: ", "Javob berib bo'lmadi: ") + (error ?? "нет ответа");
            // 2026-10-05, владелец: «включи поиск по интернету для ИИ» — если ИИ искал в интернете, источники ссылками.
            if (answer is { Length: > 0 } && TelegramAiChat.LastWebSources.Count > 0)
                AddWebSources(TelegramAiChat.LastWebSources);
            else if (TelegramAiChat.WebSearchUnavailable && !AiProviders.HasGroq && !_webSearchNoteShown)
            {
                _webSearchNoteShown = true;
                _modelsCard.IsVisible = true;
                AddBubble(T("Искать в интернете бесплатный ключ Google Gemini не умеет. Чтобы ИИ искал в интернете бесплатно, добавьте ключ Groq: "
                            + "блок «Поиск в интернете» сверху → «Получить ключ» (бесплатно, карта не нужна). Пока отвечаю по данным магазина.",
                        "Акысыз Google Gemini ачкычы интернеттен издей албайт. ИИ интернеттен акысыз издеши үчүн Groq ачкычын кошуңуз: "
                            + "жогорудагы «Интернеттен издөө» блогу → «Ачкыч алуу» (акысыз, карта керек эмес). Азырынча дүкөндүн маалыматы боюнча жооп берем.",
                        "A free Google Gemini key cannot search the web. To let the AI search the web for free, add a Groq key: "
                            + "the “Web search” block at the top → “Get a key” (free, no card). For now I answer from the shop data.",
                        "Ücretsiz Google Gemini anahtarı internette arama yapamaz. Yapay zekânın ücretsiz arama yapması için bir Groq anahtarı ekleyin: "
                            + "üstteki «İnternet araması» bloğu → «Anahtar al» (ücretsiz, kart gerekmez). Şimdilik mağaza verileriyle yanıtlıyorum.",
                        "Bepul Google Gemini kaliti internetda qidira olmaydi. SI internetda bepul qidirishi uchun Groq kalitini qo'shing: "
                            + "yuqoridagi «Internetda qidirish» bloki → «Kalit olish» (bepul, karta kerak emas). Hozircha do'kon ma'lumotlari bo'yicha javob beraman."), fromOwner: false);
            }
            if (botChange is { Count: > 0 })
                AddBotChangeCard(botChange);
            if (scenario is not null)
                AddScenarioCard(scenario);
            if (productSteps is { Count: > 0 })
                ShowProductSteps(productSteps);
            // 2026-10-06: строки, которые не стали действиями, — не молча, а списком (раньше накладная на 20 позиций давала «ничего»).
            if (ProductActionPlan.LastSkipped.Count > 0)
                AddBubble(T("Не нашёл в каталоге и не смог разобрать: ", "Каталогдон таппадым жана ажырата алган жокмун: ", "Not found in the catalog and couldn't parse: ",
                    "Katalogda bulunamadı ve ayrıştırılamadı: ", "Katalogda topilmadi va ajratib bo'lmadi: ") + string.Join(", ", ProductActionPlan.LastSkipped)
                    + T(". Напишите, что с ними сделать (например, «создай как новые товары»).", ". Алар менен эмне кылууну жазыңыз (мисалы, «жаңы товар катары түз»).",
                        ". Tell me what to do with them (e.g. “create as new products”).", ". Bunlarla ne yapılacağını yazın (ör. «yeni ürün olarak oluştur»).",
                        ". Ular bilan nima qilishni yozing (masalan, «yangi mahsulot sifatida yarat»)."), fromOwner: false);
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
            if (gen == _gen)
            {
                _busy = false;
                RefreshKeyCard();
            }
        }
        if (gen != _gen)
            return;
        SaveCurrentChat();
        ScrollToEnd();
        _input.Focus();
    }

    /// <summary>Остановить текущий ответ или поиск фото: ввод сразу доступен, старая работа UI больше не трогает.</summary>
    private void StopCurrent(bool showNote)
    {
        EndLive(null);
        var wasBusy = _busy;
        _gen++;
        _cts?.Cancel();
        VoiceChatPlayer.Stop();
        _busy = false;
        RefreshKeyCard();
        if (wasBusy)
        {
            PosLogger.Log("ИИ-советник: работа остановлена владельцем.", "INFO");
            if (showNote)
                AddBubble(T("Остановил.", "Токтоттум.", "Stopped.", "Durdurdum.", "To'xtatdim."), fromOwner: false);
        }
    }

    // ── История разговоров (2026-10-05). Сохраняются реплики (вопросы и ответы текстом); карточки фото и кнопки — нет.
    private sealed record SavedLine(bool Owner, string Text);

    private sealed record SavedChat(string Id, DateTime At, string Title, List<SavedLine> Lines);

    private static string ChatsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "ai-chats.json");

    private static List<SavedChat> LoadChats()
    {
        try
        {
            if (System.IO.File.Exists(ChatsPath))
                return System.Text.Json.JsonSerializer.Deserialize<List<SavedChat>>(System.IO.File.ReadAllText(ChatsPath)) ?? new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: история разговоров не прочитана ({ex.Message}).", "WARNING");
        }
        return new();
    }

    private static void StoreChats(List<SavedChat> chats)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ChatsPath)!);
            System.IO.File.WriteAllText(ChatsPath, System.Text.Json.JsonSerializer.Serialize(chats.Take(30).ToList()));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: история разговоров не сохранена ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Реплики текущего разговора с экрана (пузыри: справа — владелец, слева — советник).</summary>
    private List<SavedLine> CurrentLines()
    {
        var thinking = T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…");
        var lines = new List<SavedLine>();
        foreach (var child in _messages.Children)
        {
            if (child.Tag is MessageTag tag && !string.IsNullOrWhiteSpace(tag.Body.Text) && tag.Body.Text != thinking)
                lines.Add(new SavedLine(tag.Owner, tag.Body.Text!));
        }
        return lines;
    }

    private void SaveCurrentChat()
    {
        var lines = CurrentLines();
        var first = lines.FirstOrDefault(l => l.Owner);
        if (first is null)
            return;
        var title = first.Text.Length > 70 ? first.Text[..70] + "…" : first.Text;
        var chats = LoadChats();
        var at = chats.FirstOrDefault(c => c.Id == _chatId)?.At ?? DateTime.Now;
        chats.RemoveAll(c => c.Id == _chatId);
        chats.Insert(0, new SavedChat(_chatId, at, title, lines));
        StoreChats(chats);
        RefreshHistoryList();
    }

    private void BuildHistoryCard()
    {
        Use(_historyCard, Border.BackgroundProperty, "BrushPanel");
        Use(_historyCard, Border.BorderBrushProperty, "BrushBorder");
        var title = new TextBlock
        {
            Text = T("Прошлые разговоры", "Мурунку маектер", "Past chats", "Geçmiş sohbetler", "Oldingi suhbatlar"),
            FontSize = 15, FontWeight = FontWeight.Bold,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        _historyCard.Child = new StackPanel
        {
            Spacing = 8,
            Children = { title, new ScrollViewer { MaxHeight = 260, Content = _historyList } },
        };
    }

    private void RefreshHistoryList()
    {
        _historyList.Children.Clear();
        var chats = LoadChats();
        if (chats.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Здесь появятся ваши разговоры — каждый сохраняется сам после ответа.",
                    "Бул жерде маектериңиз чыгат — ар бири жооптон кийин өзү сакталат.",
                    "Your chats will appear here — each one is saved automatically after an answer.",
                    "Sohbetleriniz burada görünecek — her biri yanıttan sonra otomatik kaydedilir.",
                    "Suhbatlaringiz shu yerda chiqadi — har biri javobdan keyin o'zi saqlanadi."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 0),
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _historyList.Children.Add(empty);
            return;
        }
        string? lastGroup = null;
        foreach (var chat in chats)
        {
            var days = (DateTime.Today - chat.At.Date).TotalDays;
            var group = days < 1 ? T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")
                : days < 2 ? T("Вчера", "Кечээ", "Yesterday", "Dün", "Kecha")
                : days < 7 ? T("Последние 7 дней", "Акыркы 7 күн", "Previous 7 days", "Son 7 gün", "Oxirgi 7 kun")
                : T("Ранее", "Мурда", "Earlier", "Daha önce", "Avvalroq");
            if (group != lastGroup)
            {
                var head = new TextBlock { Text = group, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, lastGroup is null ? 2 : 12, 0, 4) };
                Use(head, TextBlock.ForegroundProperty, "BrushTextSoft");
                _historyList.Children.Add(head);
                lastGroup = group;
            }

            var current = chat.Id == _chatId;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = new TextBlock { Text = chat.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal };
            Use(title, TextBlock.ForegroundProperty, "BrushText");
            var open = new Button
            {
                Content = title, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false,
            };
            Use(open, Button.BackgroundProperty, current ? "BrushAccentSoft" : "BrushPanel");
            ToolTip.SetTip(open, $"{chat.At:dd.MM.yyyy HH:mm} · {chat.Title}");
            open.Click += (_, _) => OpenChat(chat);
            row.Children.Add(open);
            var delete = new Button
            {
                Content = "✕", Width = 30, Height = 30, Padding = new Thickness(0), FontSize = 12, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
                Opacity = 0.55, Focusable = false,
            };
            Use(delete, Button.BackgroundProperty, "BrushPanel");
            Use(delete, Button.ForegroundProperty, "BrushTextSoft");
            ToolTip.SetTip(delete, T("Удалить разговор", "Маекти өчүрүү", "Delete chat", "Sohbeti sil", "Suhbatni o'chirish"));
            delete.Click += (_, _) =>
            {
                var all = LoadChats();
                all.RemoveAll(c => c.Id == chat.Id);
                StoreChats(all);
                RefreshHistoryList();
            };
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
            row.Classes.Add("no-reflow");
            _historyList.Children.Add(row);
        }
    }

    /// <summary>Боковая панель: «＋ Новый разговор» и история.</summary>
    private void BuildSidebar()
    {
        Use(_sidebar, Border.BackgroundProperty, "BrushPanel");
        Use(_sidebar, Border.BorderBrushProperty, "BrushBorder");
        _sidebar.Padding = new Thickness(12, 12);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        var label = new TextBlock { Text = T("История разговоров", "Маектердин тарыхы", "Chat history", "Sohbet geçmişi", "Suhbatlar tarixi"), FontSize = 14, FontWeight = FontWeight.Bold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Use(label, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(label);
        var close = RoundButton(new TextBlock { Text = "✕", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, accent: false);
        close.Width = 32;
        close.Height = 32;
        close.Click += (_, _) => _sidebar.IsVisible = false;
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        var list = new ScrollViewer { Content = _historyList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _historyList.Spacing = 2;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(list, 1);
        grid.Children.Add(head);
        grid.Children.Add(list);
        _sidebar.Child = grid;
    }

    /// <summary>Шапка: название и модели слева; справа «История», «＋ Новый разговор», ⚙ «Модели ИИ».</summary>
    private void BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var avatar = AiAvatar(34);
        titleRow.Children.Add(avatar);
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        titles.Children.Add(title);
        titles.Children.Add(_status);
        titleRow.Children.Add(titles);
        _headerTitle = title;
        header.Children.Add(titleRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _menuButton = UiKit.Ghost(this, T("🕘  История", "🕘  Тарых", "🕘  History", "🕘  Geçmiş", "🕘  Tarix"));
        _menuButton.Height = 40;
        _menuButton.CornerRadius = new CornerRadius(20);
        ToolTip.SetTip(_menuButton, T("Прошлые разговоры", "Мурунку маектер", "Past chats", "Geçmiş sohbetler", "Oldingi suhbatlar"));
        _menuButton.Click += (_, _) =>
        {
            _sidebar.IsVisible = !_sidebar.IsVisible;
            if (_sidebar.IsVisible)
                RefreshHistoryList();
        };
        buttons.Children.Add(_menuButton);
        var newChat = UiKit.Primary(this, T("＋  Новый разговор", "＋  Жаңы маек", "＋  New chat", "＋  Yeni sohbet", "＋  Yangi suhbat"));
        newChat.Height = 40;
        newChat.CornerRadius = new CornerRadius(20);
        newChat.Click += (_, _) => ResetChat();
        buttons.Children.Add(newChat);
        var models = RoundButton(new TextBlock { Text = "⚙", FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, accent: false);
        ToolTip.SetTip(models, T("Модели ИИ и поиск в интернете", "ИИ моделдери жана интернеттен издөө", "AI models and web search", "Yapay zekâ modelleri ve internet araması", "SI modellari va internetda qidirish"));
        models.Click += (_, _) =>
        {
            var show = !(_modelsCard.IsVisible && _routerSection.IsVisible);
            _modelsCard.IsVisible = show;
            _routerSection.IsVisible = show;
        };
        buttons.Children.Add(models);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        // Узкий экран: текст на кнопках не помещается — только значки.
        var historyButton = _menuButton;
        NarrowLayout.Attach(this, 640, narrow =>
        {
            historyButton.Content = narrow ? "🕘" : T("🕘  История", "🕘  Тарых", "🕘  History", "🕘  Geçmiş", "🕘  Tarix");
            newChat.Content = narrow ? "＋" : T("＋  Новый разговор", "＋  Жаңы маек", "＋  New chat", "＋  Yeni sohbet", "＋  Yangi suhbat");
        });
    }

    /// <summary>Строка под названием: какие модели работают.</summary>
    private void RefreshStatus()
    {
        if (!TelegramAiChat.IsConfigured)
        {
            _status.Text = T("Нужен ключ ИИ — блок ниже", "ИИ ачкычы керек — төмөнкү блок", "An AI key is needed — see below", "Yapay zekâ anahtarı gerekli — aşağıya bakın", "SI kaliti kerak — pastdagi blok");
            return;
        }
        var parts = new List<string> { "Google Gemini" };
        if (AiProviders.HasGroq)
            parts.Add(T("поиск в интернете: Groq", "интернеттен издөө: Groq", "web search: Groq", "internet araması: Groq", "internetda qidirish: Groq"));
        if (AiProviders.HasOpenRouter)
            parts.Add(T("запасная модель: OpenRouter", "запастагы модель: OpenRouter", "backup model: OpenRouter", "yedek model: OpenRouter", "zaxira model: OpenRouter"));
        _status.Text = "● " + string.Join(" · ", parts);
    }

    /// <summary>Пустой чат: значок, «Чем помочь?», что умеет советник, готовые вопросы карточками.</summary>
    private void ShowWelcome()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 28, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 800 };
        var avatar = AiAvatar(56);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(avatar);
        var hello = new TextBlock
        {
            Text = T("Чем помочь вашему магазину?", "Дүкөнүңүзгө кантип жардам берейин?", "How can I help your shop?", "Mağazanıza nasıl yardımcı olabilirim?", "Do'koningizga qanday yordam beray?"),
            FontSize = 24, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Use(hello, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(hello);
        var intro = new TextBlock
        {
            Text = T("Спросите про продажи, остатки, закупки или как поднять выручку — отвечу по данным вашего магазина и не выдумаю цифр. "
                     + "Можно голосом — кнопка микрофона, а поговорить вживую, как по телефону, — кнопка «Разговор» рядом.",
                "Сатуу, калдыктар, сатып алуулар же кирешени кантип көбөйтүү жөнүндө сураңыз — дүкөнүңүздүн маалыматы боюнча жооп берем, сандарды ойлоп чыгарбайм. "
                + "Үн менен да болот — микрофон баскычы, ал эми телефондогудай жандуу сүйлөшүү — жанындагы «Маек» баскычы.",
                "Ask about sales, stock, purchasing or how to grow revenue — I answer from your shop's data and never make numbers up. "
                + "You can ask by voice — the microphone button, or talk live like on the phone — the “Conversation” button next to it.",
                "Satışlar, stok, alımlar veya ciroyu nasıl artıracağınız hakkında sorun — mağazanızın verileriyle yanıtlarım, rakam uydurmam. "
                + "Sesle sorabilirsiniz — mikrofon düğmesi; telefondaki gibi canlı konuşmak için yanındaki «Sohbet» düğmesi.",
                "Savdo, qoldiqlar, xaridlar yoki tushumni qanday oshirish haqida so'rang — do'koningiz ma'lumotlari bo'yicha javob beraman, raqam o'ylab topmayman. "
                + "Ovoz bilan so'rash — mikrofon tugmasi, telefondagidek jonli gaplashish — yonidagi «Suhbat» tugmasi."),
            FontSize = 14, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 620,
        };
        Use(intro, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(intro);
        if (_quick.Parent is Panel old)
            old.Children.Remove(_quick);
        _quick.HorizontalAlignment = HorizontalAlignment.Center;
        _quick.Margin = new Thickness(0, 14, 0, 0);
        _quick.MaxWidth = 800;
        panel.Children.Add(_quick);
        _welcome = panel;
        _messages.Children.Add(panel);
        RefreshKeyCard();
    }

    /// <summary>Значок ИИ: круг с ✦.</summary>
    private Border AiAvatar(double size)
    {
        var star = new TextBlock { Text = "✦", FontSize = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Use(star, TextBlock.ForegroundProperty, "BrushAccentForeground");
        var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = star, VerticalAlignment = VerticalAlignment.Top };
        Use(circle, Border.BackgroundProperty, "BrushAccent");
        return circle;
    }

    /// <summary>Круглая кнопка со значком (микрофон, отправка, ☰, ⚙).</summary>
    private Button RoundButton(Control glyph, bool accent)
    {
        var b = new Button
        {
            Content = glyph, Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(b, Button.BackgroundProperty, accent ? "BrushAccent" : "BrushPanelSoft");
        Use(b, Button.ForegroundProperty, accent ? "BrushAccentForeground" : "BrushText");
        return b;
    }

    /// <summary>Значок отправки (стрелка) или «стоп» (квадрат).</summary>
    private Control SendGlyph(bool stop)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(stop ? "M7,7 H17 V17 H7 Z" : "M12,19 V5 M5.5,11.5 L12,5 L18.5,11.5"),
            Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 2.4, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        Use(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, "BrushAccentForeground");
        if (stop)
            Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushAccentForeground");
        return path;
    }

    /// <summary>Открыть прошлый разговор и продолжить его: реплики — на экран, вопросы и ответы — в память разговора ИИ.</summary>
    private void OpenChat(SavedChat chat)
    {
        StopCurrent(showNote: false);
        SaveCurrentChat();
        _messages.Children.Clear();
        foreach (var line in chat.Lines)
            AddBubble(line.Text, line.Owner);
        _chatId = chat.Id;
        var turns = new List<(string Role, string Text)>();
        for (var i = 0; i + 1 < chat.Lines.Count; i++)
        {
            if (chat.Lines[i].Owner && !chat.Lines[i + 1].Owner)
            {
                turns.Add(("user", chat.Lines[i].Text));
                turns.Add(("model", chat.Lines[i + 1].Text));
            }
        }
        TelegramAiChat.RestoreOwnerAppHistory(turns);
        RefreshHistoryList();
        _sidebar.IsVisible = false;
        PosLogger.Log($"ИИ-советник: открыт прошлый разговор ({chat.Lines.Count} реплик).", "INFO");
        ScrollToEnd();
    }

    /// <summary>Реплика: вопрос владельца — пузырь справа; ответ советника — слева со значком ✦ и кнопками
    /// «Копировать» / «Озвучить». Текст можно выделить. Первый вопрос убирает приветствие пустого чата.</summary>
    private SelectableTextBlock AddBubble(string text, bool fromOwner)
    {
        if (fromOwner && _welcome is not null)
        {
            _messages.Children.Remove(_welcome);
            _welcome = null;
        }
        var body = new SelectableTextBlock { Text = text, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        if (fromOwner)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(18, 18, 4, 18),
                Padding = new Thickness(16, 10),
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = body,
                Tag = new MessageTag(true, body),
            };
            Use(bubble, Border.BackgroundProperty, "BrushAccentSoft");
            _messages.Children.Add(bubble);
            ScrollToEnd();
            return body;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Tag = new MessageTag(false, body) };
        var avatar = AiAvatar(30);
        avatar.Margin = new Thickness(0, 2, 12, 0);
        row.Children.Add(avatar);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Opacity = 0.75 };
        actions.Children.Add(ActionLink(T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"), async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && !string.IsNullOrWhiteSpace(body.Text))
                await clipboard.SetTextAsync(body.Text).ConfigureAwait(true);
        }));
        if (VoiceChatRecorder.IsSupported)
            actions.Children.Add(ActionLink(T("Озвучить", "Үн менен окуу", "Read aloud", "Sesli oku", "Ovoz bilan o'qish"), () => SpeakAsync(body.Text ?? "")));
        content.Children.Add(actions);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        _messages.Children.Add(row);

        // «Думаю…» мигает, пока ответа нет; кнопки — когда ответ пришёл.
        var thinking = T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…");
        if (text == thinking)
        {
            actions.IsVisible = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            timer.Tick += (_, _) =>
            {
                if (body.Text != thinking || !_messages.Children.Contains(row))
                {
                    body.Opacity = 1;
                    actions.IsVisible = body.Text != thinking;
                    timer.Stop();
                    return;
                }
                body.Opacity = body.Opacity > 0.7 ? 0.4 : 1;
            };
            timer.Start();
        }
        ScrollToEnd();
        return body;
    }

    private Button ActionLink(string text, Func<Task> action)
    {
        var b = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Focusable = false,
        };
        Use(b, Button.ForegroundProperty, "BrushTextSoft");
        b.Click += async (_, _) =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: действие «{text}» не выполнено ({ex.Message}).", "WARNING");
            }
        };
        return b;
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

    // ── 2026-10-06: фото к вопросу (накладная, товар) ──
    private StackPanel? _attachRow;
    private byte[]? _attachedImage;
    private string _attachedMime = "image/jpeg";
    private string _attachedName = "";

    private async Task PickPhotoAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = T("Фото накладной или товара", "Накладнойдун же товардын сүрөтү", "Invoice or product photo", "Fatura veya ürün fotoğrafı", "Yuk xati yoki mahsulot surati"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType(T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar")) { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" } } },
            }).ConfigureAwait(true);
            if (files.Count == 0)
                return;
            await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(true);
            if (ms.Length > 15_000_000)
            {
                AddBubble(T("Фото больше 15 МБ — сожмите или сфотографируйте заново.", "Сүрөт 15 МБдан чоң — кичирейтиңиз же кайра тартыңыз.", "The photo is over 15 MB — shrink it or retake it.",
                    "Fotoğraf 15 MB'tan büyük — küçültün veya yeniden çekin.", "Surat 15 MB dan katta — kichraytiring yoki qayta oling."), fromOwner: false);
                return;
            }
            var name = files[0].Name;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            SetAttachment(ms.ToArray(), ext switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" }, name);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото не прикреплено ({ex.Message}).", "WARNING");
        }
    }

    private void SetAttachment(byte[] data, string mime, string name)
    {
        _attachedImage = data;
        _attachedMime = mime;
        _attachedName = name;
        if (_attachRow is null)
            return;
        _attachRow.Children.Clear();
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 96);
            _attachRow.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Width = 48, Height = 48, Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill } });
        }
        catch
        {
            // не картинка для предпросмотра — покажем имя
        }
        var label = new TextBlock
        {
            Text = name + " — " + T($"напишите, что сделать (или просто отправьте: накладная, наценка не меньше {ProductActionPlan.MinMarkupPercent:0} %)",
                $"эмне кылууну жазыңыз (же жөн эле жөнөтүңүз: накладная, үстөк {ProductActionPlan.MinMarkupPercent:0} %дан кем эмес)",
                $"type what to do (or just send: invoice, markup at least {ProductActionPlan.MinMarkupPercent:0}%)",
                $"ne yapılacağını yazın (veya sadece gönderin: fatura, kâr payı en az %{ProductActionPlan.MinMarkupPercent:0})",
                $"nima qilishni yozing (yoki shunchaki yuboring: yuk xati, ustama kamida {ProductActionPlan.MinMarkupPercent:0} %)"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 600,
        };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        _attachRow.Children.Add(label);
        var remove = UiKit.Ghost(this, "✕");
        remove.Height = 32;
        remove.Click += (_, _) => ClearAttachment();
        _attachRow.Children.Add(remove);
        _attachRow.IsVisible = true;
        _input.Focus();
    }

    private void ClearAttachment()
    {
        _attachedImage = null;
        _attachedName = "";
        if (_attachRow is null)
            return;
        _attachRow.Children.Clear();
        _attachRow.IsVisible = false;
    }

    /// <summary>Подготовленные изменения, которые ждут «Выполнить» (или голосового «да, выполни» в звонке).</summary>
    private Func<Task<string>>? _pendingProductApply;
    private Action? _pendingProductCancel;
    // 2026-10-06: «поставь наценку 25 %» даёт новую карточку — прежняя гаснет, иначе два «Выполнить» создали бы товары дважды.
    private Action? _pendingProductSupersede;

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». «Открыть» — сразу, остальное — карточкой с подтверждением.</summary>
    private void ShowProductSteps(List<ProductActionPlan.Step> steps)
    {
        foreach (var open in steps.Where(st => st.Op is "open" or "open_section").Take(1))
            _ = ProductActionPlan.ExecuteAsync(open, T("ИИ-советник", "ИИ-кеңешчи", "AI advisor", "Yapay zekâ danışmanı", "SI maslahatchi"));
        var changes = steps.Where(st => st.Op is not ("open" or "open_section")).ToList();
        if (changes.Count > 0)
            AddProductActionsCard(changes);
    }

    // 2026-10-06, владелец «завис!!!» (снимок карточки с полями штрихкода): карточка сама ставила курсор в поле штрихкода —
    // ответ на вопрос ИИ «наценка 20 % — оставить?» печатался туда, Enter ничего не отправлял. Теперь курсор не трогаем,
    // а скан сканера (очень быстрый набор + Enter) где бы ни стоял курсор попадает в следующее пустое поле штрихкода карточки.
    private List<TextBox>? _scanTargets;
    private string _scanBuf = "";
    private long _scanLastTick;
    private int _scanFastRun;
    private TextBox? _scanFocusBox;
    private string? _scanFocusText;
    private const int ScanInterkeyMs = 40;

    private void ScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (_scanTargets is not { Count: > 0 } targets || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox fb && targets.Contains(fb))
            return; // курсор в поле штрихкода — скан печатается прямо туда, Enter — к следующему полю
        var now = Environment.TickCount64;
        var fast = _scanBuf.Length > 0 && now - _scanLastTick is >= 0 and <= ScanInterkeyMs;
        if (e.Key == Key.Enter)
        {
            var code = _scanBuf;
            var isScan = fast && code.Length >= 4 && _scanFastRun >= code.Length - 1;
            _scanBuf = "";
            _scanFastRun = 0;
            if (!isScan)
                return;
            e.Handled = true;
            if (_scanFocusBox is { } leaked)
                leaked.Text = _scanFocusText; // первые 1–2 символа скана успели попасть в поле вопроса — вернуть как было
            if (targets.FirstOrDefault(t => t.IsEnabled && string.IsNullOrWhiteSpace(t.Text)) is { } target)
            {
                target.Text = code;
                target.BringIntoView();
            }
            return;
        }
        char? ch = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => (char)('0' + (e.Key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
            >= Key.A and <= Key.Z => (char)('A' + (e.Key - Key.A)),
            Key.OemMinus or Key.Subtract => '-',
            _ => null,
        };
        if (ch is null)
        {
            _scanBuf = "";
            _scanFastRun = 0;
            return;
        }
        if (fast)
            _scanFastRun++;
        else
        {
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = focused as TextBox;
            _scanFocusText = _scanFocusBox?.Text;
        }
        _scanBuf += ch;
        _scanLastTick = now;
        if (_scanFastRun >= 2)
            e.Handled = true;
    }

    /// <summary>2026-10-06: предложенные ИИ действия с товарами — карточкой; выполняются только по «Выполнить» (ProductActionPlan).</summary>
    private void AddProductActionsCard(List<ProductActionPlan.Step> steps)
    {
        var title = new TextBlock { Text = T("Изменить товары?", "Товарларды өзгөртөлүбү?", "Change the products?", "Ürünler değiştirilsin mi?", "Mahsulotlarni o'zgartiraymi?"), FontWeight = FontWeight.Bold, FontSize = 14.5 };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-06, владелец: «при загрузке товара спрашивать штрихкод через сканер». У нового товара без штрихкода — поле
        // «отсканируйте»: сканер пишет в поле и жмёт Enter — курсор переходит к следующему. Штрихкод уже есть у товара в
        // каталоге — это не новый товар, а приход к нему (дубль не создаётся).
        var body = new StackPanel { Spacing = 4 };
        var barcodeBoxes = new Dictionary<int, (TextBox Box, TextBlock Note)>();
        // Только точное совпадение штрихкода (Find ищет и по названию — недописанный код не должен «найти» чужой товар).
        static NurMarketKassa.Models.Pos.CatalogProductTileVm? ByBarcode(string code) =>
            code.Length >= 4 && ProductActions.Find(code) is { } p && string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase) ? p : null;
        for (var i = 0; i < steps.Count; i++)
        {
            var line = new TextBlock { Text = "• " + ProductActionPlan.Describe(steps[i]), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
            Use(line, TextBlock.ForegroundProperty, "BrushText");
            body.Children.Add(line);
            if (steps[i].Op != "create" || !string.IsNullOrWhiteSpace(steps[i].Barcode))
                continue;
            var box = UiKit.Input(this, T("Штрихкод — отсканируйте сканером или введите", "Штрихкод — сканер менен окутуңуз же жазыңыз", "Barcode — scan it or type it",
                "Barkod — okutun veya yazın", "Shtrix-kod — skanerlang yoki kiriting"), 36);
            box.Margin = new Thickness(14, 0, 0, 0);
            box.Width = 440;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            var note = new TextBlock { FontSize = 12.5, Margin = new Thickness(14, 0, 0, 2), TextWrapping = TextWrapping.Wrap, IsVisible = false };
            Use(note, TextBlock.ForegroundProperty, "BrushWarning");
            var index = i;
            box.TextChanged += (_, _) =>
            {
                var code = (box.Text ?? "").Trim();
                var existing = ByBarcode(code);
                // Сканер «пикнул» дважды или один код у двух строк — второй товар с тем же штрихкодом сервер не примет.
                var twice = code.Length >= 4 && barcodeBoxes.Any(kv => kv.Key != index && string.Equals((kv.Value.Box.Text ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));
                note.IsVisible = existing is not null || twice;
                if (twice && existing is null)
                    note.Text = T("Этот штрихкод уже введён у другой строки — проверьте.", "Бул штрихкод башка сапта киргизилген — текшериңиз.",
                        "This barcode is already entered on another line — please check.", "Bu barkod başka bir satıra zaten girildi — kontrol edin.",
                        "Bu shtrix-kod boshqa qatorda kiritilgan — tekshiring.");
                if (existing is not null)
                    note.Text = T($"Этот штрихкод уже у товара «{existing.Title}» — будет приход к нему, новый товар не создаётся.",
                        $"Бул штрихкод «{existing.Title}» товарында бар — ага кириш болот, жаңы товар түзүлбөйт.",
                        $"This barcode already belongs to “{existing.Title}” — it will be received there, no new product.",
                        $"Bu barkod zaten «{existing.Title}» ürününde — ona giriş yapılır, yeni ürün oluşturulmaz.",
                        $"Bu shtrix-kod «{existing.Title}» mahsulotida bor — unga kirim bo'ladi, yangi mahsulot yaratilmaydi.");
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                // Следующее пустое поле штрихкода; все заполнены — к «Выполнить».
                var next = barcodeBoxes.Where(kv => kv.Key > index && string.IsNullOrWhiteSpace(kv.Value.Box.Text)).Select(kv => kv.Value.Box).FirstOrDefault();
                if (next is not null)
                    next.Focus();
            };
            barcodeBoxes[i] = (box, note);
            body.Children.Add(box);
            body.Children.Add(note);
        }

        // Шаги с отсканированными штрихкодами (или приход к уже существующему товару с этим штрихкодом).
        List<ProductActionPlan.Step> ResolveSteps() => steps.Select((st, i) =>
        {
            if (!barcodeBoxes.TryGetValue(i, out var b) || (b.Box.Text ?? "").Trim() is not { Length: >= 4 } code)
                return st;
            return ByBarcode(code) is { Id.Length: > 0 } existing
                ? new ProductActionPlan.Step("receive", existing, st.Qty, null, null, st.Purchase, st.Price)
                : st with { Barcode = code };
        }).ToList();
        var scanTargets = barcodeBoxes.OrderBy(kv => kv.Key).Select(kv => kv.Value.Box).ToList();
        _scanTargets = scanTargets.Count > 0 ? scanTargets : null;

        var apply = UiKit.Primary(this, T("Выполнить", "Аткаруу", "Do it", "Uygula", "Bajarish"));
        apply.Height = 38;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 20 };
        Use(result, TextBlock.ForegroundProperty, "BrushText");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        var finished = false;
        var superseded = false;
        void Cancel()
        {
            if (finished)
                return;
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            if (ReferenceEquals(_scanTargets, scanTargets))
                _scanTargets = null;
            buttons.IsVisible = false;
            result.IsVisible = true;
            foreach (var (box, _) in barcodeBoxes.Values)
                box.IsEnabled = false;
            result.Text = superseded
                ? T("Заменено новым предложением ниже.", "Төмөнкү жаңы сунуш менен алмаштырылды.", "Replaced by the new proposal below.",
                    "Aşağıdaki yeni öneriyle değiştirildi.", "Quyidagi yangi taklif bilan almashtirildi.")
                : T("Не меняли.", "Өзгөртүлгөн жок.", "Not changed.", "Değiştirilmedi.", "O'zgartirilmadi.");
        }
        async Task<string> Apply()
        {
            if (finished)
                return "";
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            if (ReferenceEquals(_scanTargets, scanTargets))
                _scanTargets = null;
            buttons.IsVisible = false;
            result.IsVisible = true;
            result.Text = T("Выполняю…", "Аткарып жатам…", "Working…", "Uygulanıyor…", "Bajarilmoqda…");
            var done = await ProductActionPlan.ExecuteAllAsync(ResolveSteps(), T("ИИ-советник", "ИИ-кеңешчи", "AI advisor", "Yapay zekâ danışmanı", "SI maslahatchi"), CancellationToken.None).ConfigureAwait(true);
            foreach (var (box, _) in barcodeBoxes.Values)
                box.IsEnabled = false;
            result.Text = done;
            PosLogger.Log($"ИИ-советник: действия с товарами выполнены по подтверждению владельца ({steps.Count}).", "INFO");
            SaveCurrentChat();
            ScrollToEnd();
            return done;
        }
        cancel.Click += (_, _) => Cancel();
        apply.Click += async (_, _) => await Apply().ConfigureAwait(true);
        _pendingProductSupersede?.Invoke();
        _pendingProductSupersede = () =>
        {
            superseded = true;
            Cancel();
        };
        _pendingProductApply = Apply;
        _pendingProductCancel = Cancel;
        _messages.Children.Add(card);
        ScrollToEnd();
    }

    /// <summary>2026-10-06, владелец (снимок звонка: «Голосом прямо в звонке поменять информацию не могу»): «дай возможность голосом
    /// менять информацию, открывать товар на складе, добавлять и удалять информацию». Реплика владельца в звонке про товар —
    /// параллельно со звонком тот же разбор, что в чате (сведения из интернета, строки «ТОВАР:»): «открыть» — сразу, изменения —
    /// карточкой; «да, выполни» голосом или кнопка — выполнить. Советнику в звонке программа сообщает, что сделано.</summary>
    private async Task LiveProductPassAsync(string utterance, GeminiLiveVoice live)
    {
        try
        {
            var (summary, _) = await GetSummaryAsync().ConfigureAwait(true);
            var (answer, error) = await TelegramAiChat.AskOwnerAppAsync(
                utterance + "\n(Сказано голосом во время звонка. Ответь одной-двумя фразами и строками «ТОВАР:», если нужны изменения или открыть товар.)",
                summary, CancellationToken.None).ConfigureAwait(true);
            if (_live != live || answer is not { Length: > 0 })
            {
                if (error != null)
                    PosLogger.Log($"ИИ-советник: в звонке товар не разобран ({error}).", "WARNING");
                return;
            }
            var (text, steps) = ProductActionPlan.Extract(answer);
            var changes = steps.Where(st => st.Op is not ("open" or "open_section")).ToList();
            // Просили сведения (без изменений) — ответ текстом на экране, со ссылками.
            if (changes.Count == 0 && text.Length > 0 && ProductInfoResearch.LooksLikeInfoRequest(utterance))
            {
                AddBubble(TelegramAiChat.ToPlainText(text), fromOwner: false);
                if (TelegramAiChat.LastWebSources.Count > 0)
                    AddWebSources(TelegramAiChat.LastWebSources);
            }
            ShowProductSteps(steps);
            var note = changes.Count > 0
                ? "[Программа] На экране список изменений: " + string.Join("; ", changes.Select(ProductActionPlan.Describe))
                  + ". Коротко скажи владельцу, что подготовлено, и спроси: выполнить? (ответ «да, выполни» или кнопка)."
                : steps.Any(st => st.Op == "open")
                    ? "[Программа] Товар открыт на складе: " + string.Join(", ", steps.Where(st => st.Op == "open").Select(st => st.Product.Title)) + ". Скажи об этом одной фразой."
                    : text.Length > 0
                        ? "[Программа] Найдено о товаре: " + (text.Length > 600 ? text[..600] : text) + ". Коротко перескажи главное."
                        : "";
            if (note.Length > 0)
                await live.SendTextAsync(note).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: разбор товара в звонке не удался ({ex.Message}).", "WARNING");
        }
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

    /// <summary>Значок «Разговор»: звуковая волна (четыре полоски).</summary>
    private Control WaveGlyph(string brushKey)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,10 V14 M9.5,6.5 V17.5 M14,3.5 V20.5 M18.5,8 V16"),
            Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 2.4, StrokeLineCap = PenLineCap.Round,
        };
        Use(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, brushKey);
        return path;
    }

    /// <summary>Панель живого разговора на месте поля ввода: круг (дышит под голос), что сейчас происходит, «Перебить», «Завершить».</summary>
    private void BuildLiveBar()
    {
        Use(_liveBar, Border.BackgroundProperty, "BrushPanel");
        Use(_liveBar, Border.BorderBrushProperty, "BrushAccent");
        Use(_orb, Border.BackgroundProperty, "BrushAccent");
        _orb.Child = WaveGlyph("BrushAccentForeground");
        _orb.RenderTransform = _orbScale;
        _orb.RenderTransformOrigin = RelativePoint.Center;
        _orb.VerticalAlignment = VerticalAlignment.Center;
        Use(_liveState, TextBlock.ForegroundProperty, "BrushText");
        Use(_liveHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        row.Children.Add(_orb);
        var texts = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_liveState);
        texts.Children.Add(_liveHint);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        _liveInterrupt = UiKit.Ghost(this, T("Перебить", "Бөлүү", "Interrupt", "Böl", "To'xtatish"));
        _liveInterrupt.Height = 40;
        _liveInterrupt.CornerRadius = new CornerRadius(20);
        _liveInterrupt.Margin = new Thickness(0, 0, 8, 0);
        _liveInterrupt.IsVisible = false;
        ToolTip.SetTip(_liveInterrupt, T("Советник замолчит и будет слушать вас", "Кеңешчи унчукпай калып, сизди угат", "The advisor stops talking and listens to you",
            "Danışman susar ve sizi dinler", "Maslahatchi jim bo'lib, sizni tinglaydi"));
        _liveInterrupt.Click += (_, _) =>
        {
            _live?.Interrupt();
            FinishLiveTurn(interrupted: true);
        };
        Grid.SetColumn(_liveInterrupt, 2);
        row.Children.Add(_liveInterrupt);
        var end = new Button
        {
            Content = T("Завершить", "Бүтүрүү", "End", "Bitir", "Tugatish"), Height = 40, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
        };
        Use(end, Button.BackgroundProperty, "BrushDanger");
        end.Foreground = Brushes.White;
        end.Click += (_, _) => EndLive(null);
        Grid.SetColumn(end, 3);
        row.Children.Add(end);
        _liveBar.Child = row;
        Grid.SetRow(_liveBar, 3);
        _root.Children.Add(_liveBar);
    }

    /// <summary>«Разговор»: сводка магазина → живой сеанс Gemini; дальше владелец просто говорит.</summary>
    private async Task StartLiveAsync()
    {
        if (_live is not null || _busy || _recorder is not null || !TelegramAiChat.IsConfigured)
            return;
        var gen = ++_liveGen;
        VoiceChatPlayer.Stop();
        _sidebar.IsVisible = false;
        if (_composer is not null)
            _composer.IsVisible = false;
        _liveBar.IsVisible = true;
        _liveState.Text = T("Подключаюсь…", "Туташып жатам…", "Connecting…", "Bağlanıyor…", "Ulanmoqda…");
        _liveHint.Text = T("Готовлю данные магазина для разговора", "Маек үчүн дүкөндүн маалыматын даярдап жатам", "Preparing your shop's data for the conversation",
            "Sohbet için mağaza verileri hazırlanıyor", "Suhbat uchun do'kon ma'lumotlari tayyorlanmoqda");
        _liveTimer?.Stop();
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _liveTimer.Tick += (_, _) => TickLive();
        _liveTimer.Start();
        IReadOnlyDictionary<int, string> names;
        string instruction;
        try
        {
            var (summary, debtorNames) = await GetSummaryAsync().ConfigureAwait(true);
            names = debtorNames;
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(summary, CurrentLines().Select(l => (l.Owner, l.Text)));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сводка для разговора не собрана ({ex.Message}).", "WARNING");
            names = new Dictionary<int, string>();
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(null, CurrentLines().Select(l => (l.Owner, l.Text)));
        }
        if (gen != _liveGen)
            return;
        _liveDebtors = names;
        var live = new GeminiLiveVoice(instruction);
        live.UserText += text => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            if (_liveUser is null)
            {
                _liveUser = AddBubble("", fromOwner: true);
                // Расшифровка вопроса пришла позже начала ответа — пузырь вопроса ставим перед ответом.
                if (_liveAi is not null && _liveUser.Parent is Control bubble
                    && _messages.Children.FirstOrDefault(c => c.Tag is MessageTag t && t.Body == _liveAi) is { } aiRow)
                {
                    _messages.Children.Remove(bubble);
                    _messages.Children.Insert(_messages.Children.IndexOf(aiRow), bubble);
                }
            }
            _liveUserText.Append(text);
            _liveUser.Text = _liveUserText.ToString().Trim();
            ScrollToEnd();
        });
        live.AiText += text => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            _liveAi ??= AddBubble("", fromOwner: false);
            _liveAiText.Append(text);
            _liveAi.Text = _liveAiText.ToString().Trim();
            ScrollToEnd();
        });
        live.TurnDone += () => Dispatcher.UIThread.Post(() =>
        {
            if (_live == live)
                FinishLiveTurn(interrupted: false);
        });
        live.Ended += reason => Dispatcher.UIThread.Post(() =>
        {
            if (_live != live)
                return;
            EndLive(reason is null
                ? null
                : T("Разговор прервался: ", "Маек үзүлдү: ", "The conversation was interrupted: ", "Sohbet kesildi: ", "Suhbat uzildi: ") + reason
                  + T(". Можно спросить голосом кнопкой микрофона или написать.", ". Микрофон баскычы менен үн менен сурасаңыз же жазсаңыз болот.",
                      ". You can ask by voice with the microphone button or type.", ". Mikrofon düğmesiyle sesli sorabilir veya yazabilirsiniz.",
                      ". Mikrofon tugmasi bilan ovozli so'rashingiz yoki yozishingiz mumkin."));
        });
        _live = live;
        try
        {
            live.Start();
            PosLogger.Log($"ИИ-советник: живой разговор начат (инструкция {instruction.Length} симв.).", "INFO");
        }
        catch (Exception ex)
        {
            EndLive(T("Разговор не начался: ", "Маек башталган жок: ", "The conversation didn't start: ", "Sohbet başlamadı: ", "Suhbat boshlanmadi: ") + ex.Message);
        }
    }

    /// <summary>Каждые 60 мс: подпись состояния, «дыхание» круга, «Перебить» — пока советник говорит; 3 минуты тишины — конец.</summary>
    private void TickLive()
    {
        var live = _live;
        var state = live?.State ?? GeminiLiveVoice.LiveState.Connecting;
        var t = Environment.TickCount64 / 1000.0;
        double scale;
        switch (state)
        {
            case GeminiLiveVoice.LiveState.Listening:
                var hearing = live!.MicLevel > 0.06;
                _liveState.Text = hearing
                    ? T("Слышу вас…", "Угуп жатам…", "I'm hearing you…", "Sizi duyuyorum…", "Sizni eshityapman…")
                    : T("Слушаю — говорите", "Угуп жатам — сүйлөңүз", "Listening — go ahead", "Dinliyorum — konuşun", "Tinglayapman — gapiring");
                _liveHint.Text = T("Спросите что угодно о магазине. Закончили — «Завершить».", "Дүкөн жөнүндө каалаганды сураңыз. Бүттүңүзбү — «Бүтүрүү».",
                    "Ask anything about your shop. Done — “End”.", "Mağazanız hakkında her şeyi sorun. Bitince — «Bitir».", "Do'kon haqida istalgan narsani so'rang. Tugatsangiz — «Tugatish».");
                scale = 1 + Math.Min(0.35, live.MicLevel * 0.45);
                _orbScale.ScaleX += (scale - _orbScale.ScaleX) * 0.5;
                _orbScale.ScaleY = _orbScale.ScaleX;
                _orb.Opacity = 1;
                break;
            case GeminiLiveVoice.LiveState.Speaking:
                _liveState.Text = T("Советник говорит…", "Кеңешчи сүйлөп жатат…", "The advisor is speaking…", "Danışman konuşuyor…", "Maslahatchi gapiryapti…");
                _liveHint.Text = T("Пока он говорит, микрофон молчит — чтобы не слышать сам себя. Перебить — кнопкой.",
                    "Ал сүйлөп жатканда микрофон өчүк — өзүн укпашы үчүн. Бөлүү — баскыч менен.",
                    "While it speaks the microphone is muted so it doesn't hear itself. Interrupt with the button.",
                    "Konuşurken mikrofon kapalı — kendini duymaması için. Bölmek için düğmeye basın.",
                    "U gapirayotganda mikrofon o'chiq — o'zini eshitmasligi uchun. To'xtatish — tugma bilan.");
                scale = 1.06 + 0.06 * Math.Sin(t * 7);
                _orbScale.ScaleX = _orbScale.ScaleY = scale;
                _orb.Opacity = 1;
                break;
            default:
                _orbScale.ScaleX = _orbScale.ScaleY = 1;
                _orb.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(t * 3));
                break;
        }
        if (_liveInterrupt is not null)
            _liveInterrupt.IsVisible = state == GeminiLiveVoice.LiveState.Speaking;
        // Забыли завершить — через 3 минуты тишины разговор заканчивается сам (бесплатный лимит Google не тратится впустую).
        if (live is not null && state == GeminiLiveVoice.LiveState.Listening && DateTime.UtcNow - live.LastActivityUtc > TimeSpan.FromMinutes(3))
            EndLive(T("Разговор завершён: три минуты тишины.", "Маек бүттү: үч мүнөт тынчтык.", "Conversation ended: three minutes of silence.",
                "Sohbet bitti: üç dakika sessizlik.", "Suhbat tugadi: uch daqiqa jimlik."));
    }

    /// <summary>Ответ договорён (или перебит): текст ответа — начисто, должники — по именам; реплики — в историю и в память чата.</summary>
    private void FinishLiveTurn(bool interrupted)
    {
        // 2026-10-06: голосом в звонке — «да, выполни» для подготовленных изменений и просьбы про товары (LiveProductPassAsync).
        var spoken = _liveUserText.ToString().Trim();
        if (!interrupted && spoken.Length > 0 && _live is { } liveNow)
        {
            if (_pendingProductApply is { } apply && ProductActionPlan.IsVoiceYes(spoken))
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var done = await apply().ConfigureAwait(true);
                    if (done.Length > 0)
                        await liveNow.SendTextAsync("[Программа] Выполнено: " + done + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            else if (_pendingProductCancel is { } cancelPending && ProductActionPlan.IsVoiceNo(spoken))
                cancelPending();
            else if (ProductActionPlan.LooksLikeAction(spoken) || ProductInfoResearch.LooksLikeInfoRequest(spoken))
                _ = LiveProductPassAsync(spoken, liveNow);
        }
        if (_liveAi is not null)
        {
            var text = _liveAiText.ToString().Trim();
            _liveAi.Text = text.Length == 0
                ? (interrupted ? "…" : "")
                : OwnerAiContext.RevealDebtors(TelegramAiChat.ToPlainText(text), _liveDebtors) + (interrupted ? " …" : "");
        }
        var had = _liveUser is not null || _liveAi is not null;
        _liveUser = null;
        _liveAi = null;
        _liveUserText.Clear();
        _liveAiText.Clear();
        if (!had)
            return;
        // Текстовый чат после разговора помнит, о чём говорили голосом.
        var lines = CurrentLines();
        var turns = new List<(string Role, string Text)>();
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].Owner && !lines[i + 1].Owner)
            {
                turns.Add(("user", lines[i].Text));
                turns.Add(("model", lines[i + 1].Text));
            }
        }
        TelegramAiChat.RestoreOwnerAppHistory(turns);
        SaveCurrentChat();
    }

    /// <summary>Закончить живой разговор (кнопкой, «Новый разговор», закрытие окна или обрыв) и вернуть поле ввода.</summary>
    private void EndLive(string? note)
    {
        if (!_liveBar.IsVisible && _live is null)
            return;
        _liveGen++;
        var live = _live;
        _live = null;
        if (live is not null)
            _ = live.StopAsync();
        FinishLiveTurn(interrupted: true);
        _liveTimer?.Stop();
        _liveTimer = null;
        _liveBar.IsVisible = false;
        if (_composer is not null)
            _composer.IsVisible = true;
        RefreshKeyCard();
        if (note is not null)
            AddBubble(note, fromOwner: false);
        PosLogger.Log("ИИ-советник: живой разговор завершён.", "INFO");
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

    /// <summary>Блок «Модели ИИ»: ключи Groq (поиск в интернете, запасные ответы) и OpenRouter (запасные бесплатные модели).
    /// 2026-10-05, владелец: «сделай как у Gemini кнопку на получение ключа» — каждый ключ своим блоком, как ключ Gemini:
    /// заголовок, шаги, поле + «Получить ключ» + «Сохранить». Ключ проверяется до сохранения; пустое поле — прежний ключ остаётся.
    /// Пока ключа Groq нет, блок Groq показывается сам; OpenRouter (необязательный) — по кнопке ⚙.</summary>
    private void BuildModelsCard()
    {
        Use(_modelsCard, Border.BackgroundProperty, "BrushPanel");
        Use(_modelsCard, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(ProviderKeySection("Groq",
            T("Поиск в интернете — бесплатный ключ Groq", "Интернеттен издөө — Groq акысыз ачкычы", "Web search — a free Groq key",
                "İnternet araması — ücretsiz Groq anahtarı", "Internetda qidirish — bepul Groq kaliti"),
            T("Нажмите «Получить ключ» → войдите через Google → «Create API Key», скопируйте ключ (gsk_…) и вставьте ниже (карта не нужна, 1000 запросов в день). "
              + "Для поиска в Groq уходит только вопрос. Если у Gemini кончился дневной лимит, Groq ответит сам — тогда туда уходит и сводка магазина.",
                "«Ачкыч алуу» басыңыз → Google аркылуу кириңиз → «Create API Key», ачкычты (gsk_…) көчүрүп, төмөнгө чаптаңыз (карта керек эмес, күнүнө 1000 суроо). "
                + "Издөө үчүн Groq'ко суроо гана кетет. Gemini'нин күндүк лимити бүтсө, Groq өзү жооп берет — анда дүкөндүн жыйынтыгы да кетет.",
                "Press “Get a key” → sign in with Google → “Create API Key”, copy the key (gsk_…) and paste it below (no card, 1000 requests a day). "
                + "For search only the question goes to Groq. If Gemini's daily limit runs out, Groq answers itself — then the shop summary goes there too.",
                "«Anahtar al»a basın → Google ile giriş yapın → «Create API Key», anahtarı (gsk_…) kopyalayıp aşağıya yapıştırın (kart gerekmez, günde 1000 istek). "
                + "Arama için Groq'a yalnızca soru gider. Gemini'nin günlük limiti biterse Groq kendisi yanıtlar — o zaman mağaza özeti de gider.",
                "«Kalit olish» ni bosing → Google orqali kiring → «Create API Key», kalitni (gsk_…) nusxalab pastga joylang (karta kerak emas, kuniga 1000 so'rov). "
                + "Qidiruv uchun Groq'ga faqat savol ketadi. Gemini kunlik limiti tugasa, Groq o'zi javob beradi — unda do'kon xulosasi ham ketadi."),
            "gsk_…", AiProviders.HasGroq, "https://console.groq.com/keys", AiProviders.TestGroqAsync,
            key => UserPreferences.Instance.GroqApiKey = key,
            onSaved: () =>
            {
                AddBubble(T("✓ Поиск в интернете включён (Groq). Спросите, например: «Какой сейчас курс доллара?»",
                    "✓ Интернеттен издөө күйгүзүлдү (Groq). Мисалы, сураңыз: «Доллардын курсу азыр канча?»",
                    "✓ Web search is on (Groq). Ask, for example: “What is the dollar rate now?”",
                    "✓ İnternet araması açık (Groq). Örneğin sorun: «Doların kuru şu an ne?»",
                    "✓ Internetda qidirish yoqildi (Groq). Masalan, so'rang: «Dollar kursi hozir qancha?»"), fromOwner: false);
                // Открылся сам (без ⚙) — после сохранения прячем, как блок ключа Gemini.
                if (!_routerSection.IsVisible)
                    _modelsCard.IsVisible = false;
            }));
        _routerSection.Child = ProviderKeySection("OpenRouter",
            T("Запасные модели — бесплатный ключ OpenRouter (необязательно)", "Запастагы моделдер — OpenRouter акысыз ачкычы (милдеттүү эмес)",
                "Backup models — a free OpenRouter key (optional)", "Yedek modeller — ücretsiz OpenRouter anahtarı (isteğe bağlı)",
                "Zaxira modellar — bepul OpenRouter kaliti (ixtiyoriy)"),
            T("Если у Gemini кончился дневной лимит, ответит бесплатная модель OpenRouter (50 запросов в день). Нажмите «Получить ключ» → войдите через Google → "
              + "«Create API Key», скопируйте ключ (sk-or-…) и вставьте ниже. Вопрос и сводка магазина при этом уходят в OpenRouter.",
                "Gemini'нин күндүк лимити бүтсө, OpenRouter'дин акысыз модели жооп берет (күнүнө 50 суроо). «Ачкыч алуу» басыңыз → Google аркылуу кириңиз → "
                + "«Create API Key», ачкычты (sk-or-…) көчүрүп, төмөнгө чаптаңыз. Анда суроо жана дүкөндүн жыйынтыгы OpenRouter'ге кетет.",
                "If Gemini's daily limit runs out, a free OpenRouter model answers (50 requests a day). Press “Get a key” → sign in with Google → "
                + "“Create API Key”, copy the key (sk-or-…) and paste it below. The question and the shop summary then go to OpenRouter.",
                "Gemini'nin günlük limiti biterse ücretsiz bir OpenRouter modeli yanıtlar (günde 50 istek). «Anahtar al»a basın → Google ile giriş yapın → "
                + "«Create API Key», anahtarı (sk-or-…) kopyalayıp aşağıya yapıştırın. Soru ve mağaza özeti o zaman OpenRouter'a gider.",
                "Gemini kunlik limiti tugasa, OpenRouter bepul modeli javob beradi (kuniga 50 so'rov). «Kalit olish» ni bosing → Google orqali kiring → "
                + "«Create API Key», kalitni (sk-or-…) nusxalab pastga joylang. Savol va do'kon xulosasi shunda OpenRouter'ga ketadi."),
            "sk-or-…", AiProviders.HasOpenRouter, "https://openrouter.ai/keys", AiProviders.TestOpenRouterAsync,
            key => UserPreferences.Instance.OpenRouterApiKey = key, onSaved: null);
        stack.Children.Add(_routerSection);
        _modelsCard.Child = stack;
        // Ключа Groq нет — блок Groq виден сразу (как блок ключа Gemini), OpenRouter — по ⚙.
        _routerSection.IsVisible = false;
        _modelsCard.IsVisible = !AiProviders.HasGroq;
    }

    /// <summary>Блок одного ключа в стиле ключа Gemini: заголовок, шаги, [поле][Получить ключ][Сохранить], строка состояния.</summary>
    private StackPanel ProviderKeySection(string name, string titleText, string hintText, string sample, bool saved, string getUrl,
        Func<string, CancellationToken, Task<(bool Ok, string Message)>> test, Action<string> store, Action? onSaved)
    {
        var savedWatermark = T("ключ сохранён — вставьте новый, чтобы заменить", "ачкыч сакталды — алмаштыруу үчүн жаңысын чаптаңыз",
            "key saved — paste a new one to replace", "anahtar kaydedildi — değiştirmek için yenisini yapıştırın", "kalit saqlandi — almashtirish uchun yangisini joylang");
        var section = new StackPanel { Spacing = 8 };
        var title = new TextBlock { Text = titleText, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        section.Children.Add(title);
        var hint = new TextBlock { Text = hintText, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        section.Children.Add(hint);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var box = UiKit.Input(this, saved ? savedWatermark
            : T($"Ключ {name} ({sample})", $"{name} ачкычы ({sample})", $"{name} key ({sample})", $"{name} anahtarı ({sample})", $"{name} kaliti ({sample})"));
        box.PasswordChar = '•';
        row.Children.Add(box);
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var get = UiKit.Ghost(this, T("Получить ключ", "Ачкыч алуу", "Get a key", "Anahtar al", "Kalit olish"));
        get.Margin = new Thickness(8, 0, 0, 0);
        get.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(getUrl)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                status.Text = getUrl + " — " + ex.Message;
            }
        };
        Grid.SetColumn(get, 1);
        row.Children.Add(get);
        var save = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += async (_, _) =>
        {
            var key = (box.Text ?? "").Trim();
            if (key.Length == 0)
            {
                status.Text = T("Вставьте ключ в поле слева.", "Ачкычты сол жактагы талаага чаптаңыз.", "Paste the key into the field on the left.",
                    "Anahtarı soldaki alana yapıştırın.", "Kalitni chapdagi maydonga joylang.");
                return;
            }
            save.IsEnabled = false;
            status.Text = T("Проверяю ключ…", "Ачкычты текшерип жатам…", "Checking the key…", "Anahtar kontrol ediliyor…", "Kalit tekshirilmoqda…");
            var (ok, message) = await test(key, CancellationToken.None).ConfigureAwait(true);
            save.IsEnabled = true;
            PosLogger.Log($"ИИ-советник: ключ {name} {(ok ? "проверен и сохранён" : "не подошёл")}.", "INFO");
            if (!ok)
            {
                status.Text = T("Ключ не подошёл: ", "Ачкыч туура келген жок: ", "The key didn't work: ", "Anahtar çalışmadı: ", "Kalit ishlamadi: ") + message;
                return;
            }
            store(key);
            UserPreferences.Instance.SaveToDisk();
            box.Text = "";
            box.Watermark = savedWatermark;
            status.Text = "✓ " + message;
            onSaved?.Invoke();
        };
        Grid.SetColumn(save, 2);
        row.Children.Add(save);
        section.Children.Add(row);
        section.Children.Add(status);
        return section;
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
