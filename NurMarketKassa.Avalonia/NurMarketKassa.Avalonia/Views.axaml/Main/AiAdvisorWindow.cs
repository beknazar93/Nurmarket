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
// 2026-10-21, «доведи Нур Советника до 10»: partial — ответ (этапы, плитки цифр, «откуда цифра», кнопки действий, проверка И10)
// в AiAdvisorWindow.Answer.cs, сцены для снимков дизайна — в AiAdvisorWindow.Preview.cs.
public sealed partial class AiAdvisorWindow : Window, IOwnerSection, IOwnerSectionKeepAlive
{
    /// <summary>2026-10-20, память программы владельца: пока советник отвечает, идёт звонок или запись голоса, оболочка
    /// не закрывает раздел при уходе из него (иначе оборвался бы звонок). Разговор при закрытии сохраняется в историю.</summary>
    public bool KeepAliveWhenHidden => _busy || _live is not null || _recorder is not null || _liveBar.IsVisible;

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

    private static bool IsMissingPhotoListRequest(string question)
    {
        var q = question.ToLowerInvariant();
        var asksForMissing = q.Contains("без фото") || q.Contains("без фотограф")
                             || q.Contains("нет фото") || q.Contains("нет фотограф");
        var explicitlyOnline = q.Contains("интернет") || q.Contains("сети") || q.Contains("онлайн");
        return asksForMissing && !explicitlyOnline;
    }

    public AiAdvisorWindow()
    {
        Title = T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi");
        // 2026-10-06, владелец «почему долго??»: табель (список смен с сервера, 3–7 с) подгружается заранее — к вопросу или звонку готов.
        try
        {
            _ = TelegramAiChat.TimesheetProvider?.Invoke(DateTime.Today, DateTime.Today, CancellationToken.None);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: табель заранее не загружен ({ex.Message}).", "WARNING");
        }
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
            // 2026-10-21 (О5): честно — что именно уходит в Google и что остаётся на этом компьютере.
            Text = T("Откройте aistudio.google.com → «Get API key», скопируйте ключ и вставьте ниже (карта не нужна). Тот же ключ использует ИИ Telegram-бота. "
                     + "Что уходит в Google: вопрос и цифры магазина — продажи, прибыль, склад с закупочными ценами, ABC, итоги долгов. Должники и сотрудники — только кодами "
                     + "[Д1], [С1]: имена, телефоны и зарплаты по именам остаются на этом компьютере. На бесплатном уровне Google может использовать запросы для улучшения своих продуктов.",
                "aistudio.google.com → «Get API key» ачып, ачкычты көчүрүп, төмөнгө чаптаңыз (карта керек эмес). Ушул эле ачкычты Telegram-боттун ИИси колдонот. "
                + "Google'га эмне кетет: суроо жана дүкөндүн сандары — сатуу, пайда, сатып алуу баалары менен кампа, ABC, карыздардын жыйынтыгы. Карыздарлар жана кызматкерлер — коддор менен гана "
                + "[Д1], [С1]: аттары, телефондору жана айлыктары ушул компьютерде калат. Акысыз деңгээлде Google суроолорду өз продукттарын жакшыртуу үчүн колдонушу мүмкүн.",
                "Open aistudio.google.com → “Get API key”, copy the key and paste it below (no card needed). The Telegram bot's AI uses the same key. "
                + "What goes to Google: the question and shop figures — sales, profit, stock with purchase prices, ABC, debt totals. Debtors and staff only as codes "
                + "[Д1], [С1]: names, phones and salaries by name stay on this computer. On the free tier Google may use requests to improve its products.",
                "aistudio.google.com → «Get API key» açın, anahtarı kopyalayıp aşağıya yapıştırın (kart gerekmez). Telegram botunun yapay zekâsı aynı anahtarı kullanır. "
                + "Google'a ne gider: soru ve mağaza rakamları — satışlar, kâr, alış fiyatlı stok, ABC, borç toplamları. Borçlular ve çalışanlar yalnızca kodla "
                + "[Д1], [С1]: adlar, telefonlar ve isimli maaşlar bu bilgisayarda kalır. Ücretsiz seviyede Google istekleri ürünlerini geliştirmek için kullanabilir.",
                "aistudio.google.com → «Get API key» ni oching, kalitni nusxalab pastga joylang (karta kerak emas). Telegram botining SI xuddi shu kalitdan foydalanadi. "
                + "Google'ga nima ketadi: savol va do'kon raqamlari — savdo, foyda, xarid narxlari bilan ombor, ABC, qarzlar jami. Qarzdorlar va xodimlar faqat kodlar bilan "
                + "[Д1], [С1]: ismlar, telefonlar va ism bo'yicha maoshlar shu kompyuterda qoladi. Bepul darajada Google so'rovlardan o'z mahsulotlarini yaxshilash uchun foydalanishi mumkin."),
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
        // 2026-10-21 (О10): карточки ключей — внутри ленты, над приветствием (раньше отдельной строкой над лентой: без ключа они
        // занимали пол-экрана и приветствие уезжало под строку ввода на 1024 и 1366).
        var topCards = new StackPanel { MaxWidth = 860, Margin = new Thickness(4, 8, 12, 0), Children = { _keyCard, _modelsCard } };

        // Разговор: лента по центру, без рамки.
        // 2026-10-22 (аудит 1.17.59, П-З): отступ снизу — последний ответ («Утренний отчёт») не прячется под строкой ввода.
        _scroll.Content = new StackPanel { Margin = new Thickness(0, 0, 0, 24), Children = { topCards, _messages } };
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Grid.SetRow(_scroll, 2);
        _root.Children.Add(_scroll);

        // Готовые вопросы.
        // 2026-10-21, «дизайн 10» (исследование, раздел 2: пустой чат — 4–6 готовых вопросов по делу): ровно 6 карточек (3 × 2 или 2 × 3,
        // без одинокой седьмой), у каждой — вопрос и подпись, что посчитает советник. Поиск фото — по просьбе в строке ввода.
        foreach (var (q, hint) in new[]
                 {
                     (T("Как прошёл вчерашний день?", "Кечээки күн кандай өттү?", "How did yesterday go?", "Dün nasıl geçti?", "Kechagi kun qanday o'tdi?"),
                      T("Выручка и прибыль к позавчера", "Киреше жана пайда мурдагы күнгө карата", "Revenue and profit vs the day before", "Önceki güne göre ciro ve kâr", "Tushum va foyda oldingi kunga nisbatan")),
                     (T("Сравни эту неделю с прошлой", "Бул жуманы өткөн жума менен салыштыр", "Compare this week with last week", "Bu haftayı geçen haftayla karşılaştır", "Bu haftani o'tgan hafta bilan solishtir"),
                      T("7 дней к прошлым 7, в процентах", "7 күн мурунку 7 күнгө карата, пайыз менен", "Last 7 days vs previous 7, in %", "Son 7 gün önceki 7 güne göre, %", "7 kun oldingi 7 kunga nisbatan, foizda")),
                     (T("Что срочно заказать?", "Эмнени шашылыш заказ кылуу керек?", "What should I order urgently?", "Acilen ne sipariş etmeliyim?", "Nimani zudlik bilan buyurtma qilish kerak?"),
                      T("Закончилось и заканчивается", "Түгөнгөн жана түгөнүп жаткан", "Sold out and running low", "Tükenen ve azalan", "Tugagan va tugayotgan")),
                     (T("Кто должен больше всех?", "Ким эң көп карыз?", "Who owes the most?", "En çok kim borçlu?", "Kim eng ko'p qarzdor?"),
                      T("Долги клиентов и просрочка", "Кардарлардын карызы жана мөөнөтү өткөнү", "Customer debts and overdue", "Müşteri borçları ve gecikme", "Mijozlar qarzi va muddati o'tgani")),
                     (T("Где я теряю деньги?", "Акчаны кайда жоготуп жатам?", "Where am I losing money?", "Nerede para kaybediyorum?", "Qayerda pul yo'qotyapman?"),
                      T("Залежавшийся товар, низкая наценка", "Жатып калган товар, төмөн үстөк", "Dead stock, low markup", "Bekleyen stok, düşük kâr payı", "Turib qolgan tovar, past ustama")),
                     // 2026-10-05, владелец: «чтобы он смог предлагать назначить акции на проблемные товары».
                     (T("Какие акции запустить?", "Кайсы акцияларды баштоо керек?", "Which promotions should I run?", "Hangi kampanyaları başlatmalıyım?", "Qanday aksiyalar boshlash kerak?"),
                      T("Скидки не ниже закупки + 5 %", "Арзандатуу сатып алуу + 5 %дан төмөн эмес", "Discounts not below cost + 5%", "İndirim alış + %5'in altında değil", "Chegirma xarid + 5 % dan past emas")),
                 })
        {
            var chip = UiKit.Chip(this, q, false);
            var qText = new TextBlock { Text = q, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold };
            Use(qText, TextBlock.ForegroundProperty, "BrushText");
            var hText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
            Use(hText, TextBlock.ForegroundProperty, "BrushTextSoft");
            chip.Content = new StackPanel { Children = { qText, hText } };
            chip.Tag = q;
            Avalonia.Automation.AutomationProperties.SetName(chip, q);
            chip.Width = 252;
            chip.MinHeight = 84;
            chip.CornerRadius = new CornerRadius(14);
            chip.Padding = new Thickness(14, 10);
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            chip.VerticalContentAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(0, 0, 10, 10);
            chip.Click += async (_, _) => await SendAsync(q).ConfigureAwait(true);
            _quick.Children.Add(chip);
        }
        // 2026-10-21: карточки-вопросы — одной высоты (по самой длинной): по-кыргызски вопрос и подпись переносятся на 2 строки
        // у одних карточек и нет у соседних — ряды были «ступеньками» 82/90/98 (пакет 7, хвост Советника).
        _quick.LayoutUpdated += (_, _) => EqualizeQuickChips();

        // Поле ввода.
        // 2026-10-06, владелец (снимок строки ввода): «сделай расположение нормальным, профессиональным, дизайнерским». Поле без своей
        // заливки и синей рамки (было «поле в поле»), при фокусе подсвечивается вся строка; кнопки — одного вида, равные отступы.
        var composer = new Border { CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(1.5), Padding = new Thickness(18, 6, 6, 6), MaxWidth = 860 };
        Use(composer, Border.BackgroundProperty, "BrushPanel");
        Use(composer, Border.BorderBrushProperty, "BrushBorder");
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        _input = UiKit.Input(this, T("Спросите о своём магазине…", "Дүкөнүңүз жөнүндө сураңыз…", "Ask about your shop…", "Mağazanız hakkında sorun…", "Do'koningiz haqida so'rang…"), 44);
        _input.BorderThickness = new Thickness(0);
        _input.Background = Brushes.Transparent;
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.Padding = new Thickness(0, 8, 8, 8);
        // 2026-10-21 («дизайн 10»): и в выключенном виде (пока советник отвечает) — без серой заливки «поле в поле».
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver",
                     "TextControlBackgroundDisabled", "TextControlBorderBrushDisabled" })
            _input.Resources[key] = Brushes.Transparent;
        _input.GotFocus += (_, _) => Use(composer, Border.BorderBrushProperty, "BrushAccentStrong");
        _input.LostFocus += (_, _) => Use(composer, Border.BorderBrushProperty, "BrushBorder");
        // 2026-10-21 (удобство): Shift+Enter — новая строка (длинный вопрос по пунктам), Enter — отправить.
        _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.MaxHeight = 140;
        Avalonia.Automation.AutomationProperties.SetName(_input, T("Вопрос советнику", "Кеңешчиге суроо", "Question to the advisor", "Danışmana soru", "Maslahatchiga savol"));
        _input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter || (e.KeyModifiers & KeyModifiers.Shift) != 0)
                return;
            e.Handled = true;
            await SendAsync(_input.Text).ConfigureAwait(true);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        inputRow.Children.Add(_input);
        AddHandler(KeyDownEvent, ScanKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (VoiceChatRecorder.IsSupported)
        {
            _mic = RoundButton(MicGlyph(recording: false), accent: false); _mic.Margin = new Thickness(6, 0, 0, 0);
            ToolTip.SetTip(_mic, T("Спросить голосом: нажмите, говорите, нажмите ещё раз — ответ прозвучит вслух",
                "Үн менен суроо: басыңыз, сүйлөңүз, кайра басыңыз — жооп үн менен угулат",
                "Ask by voice: press, speak, press again — the answer will be read aloud",
                "Sesle sor: basın, konuşun, tekrar basın — yanıt sesli okunur",
                "Ovoz bilan so'rash: bosing, gapiring, yana bosing — javob ovoz bilan o'qiladi"));
            _mic.Click += async (_, _) => await ToggleVoiceAsync().ConfigureAwait(true);
            // 2026-10-21 (О10): имя для экранного диктора (было «Avalonia.Controls.Shapes.Path»).
            Avalonia.Automation.AutomationProperties.SetName(_mic, T("Спросить голосом", "Үн менен суроо", "Ask by voice", "Sesle sor", "Ovoz bilan so'rash"));
            Grid.SetColumn(_mic, 2);
            inputRow.Children.Add(_mic);
            // 2026-10-21 («дизайн 10»): «Разговор» — со словом рядом со значком: приветствие ссылается на кнопку «Разговор», а на экране
            // была безымянная «волна».
            var talkLabel = new TextBlock { Text = T("Разговор", "Маек", "Talk", "Sohbet", "Suhbat"), FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(talkLabel, TextBlock.ForegroundProperty, "BrushText");
            _talk = RoundButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { WaveGlyph("BrushText"), talkLabel } }, accent: false);
            _talk.Width = double.NaN;
            _talk.Padding = new Thickness(12, 0, 14, 0);
            _talk.Margin = new Thickness(6, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(_talk, T("Разговор голосом", "Үн менен маек", "Voice conversation", "Sesli sohbet", "Ovozli suhbat"));
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
        Avalonia.Automation.AutomationProperties.SetName(_send, T("Спросить", "Суроо", "Ask", "Sor", "So'rash"));
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
        var clip = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M16.5,6 V17.5 A4,4 0 0 1 8.5,17.5 V5 A2.5,2.5 0 0 1 13.5,5 V15.5 A1,1 0 0 1 11.5,15.5 V6"),
            Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round,
        };
        Use(clip, Avalonia.Controls.Shapes.Shape.StrokeProperty, "BrushText");
        var attach = RoundButton(clip, accent: false);
        attach.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(attach, T("Приложить фото: накладная поставщика — ИИ оприходует товары с наценкой; фото товара — опишет и заполнит карточку",
            "Сүрөт тиркөө: жеткирүүчүнүн накладнойу — ИИ товарларды үстөк менен кириштейт; товардын сүрөтү — сүрөттөп, карточкасын толтурат",
            "Attach a photo: a supplier invoice — the AI receives the goods with a markup; a product photo — it describes it and fills in the card",
            "Fotoğraf ekle: tedarikçi faturası — yapay zekâ ürünleri kâr payıyla stoğa alır; ürün fotoğrafı — tanımlar ve kartı doldurur",
            "Surat biriktirish: yetkazib beruvchi yuk xati — SI mahsulotlarni ustama bilan kirim qiladi; mahsulot surati — tavsiflab, kartani to'ldiradi"));
        attach.Click += async (_, _) => await PickPhotoAsync().ConfigureAwait(true);
        Avalonia.Automation.AutomationProperties.SetName(attach, T("Приложить фото или PDF", "Сүрөт же PDF тиркөө", "Attach a photo or PDF", "Fotoğraf veya PDF ekle", "Surat yoki PDF biriktirish"));
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
        // 2026-10-21 («дизайн 10»): панель истории не закрывает строку ввода (на снимках «было» перекрывала микрофон и «отправить»).
        _sidebar.Margin = new Thickness(0, 64, 16, 96);
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
        // 2026-10-21 (О4): менять товары через советника — только с правом «Склад» (и в боте владельца этой программы).
        ProductActionPlan.ChangesAllowed = () => CurrentAccess().Products;
        // 2026-10-21 (И3): утренний отчёт — карточкой, как только готов.
        Action<OwnerMorningReport.Report> onMorning = r => Dispatcher.UIThread.Post(() => OnMorningReport(r));
        OwnerMorningReport.ReportReady += onMorning;
        Closed += (_, _) => OwnerMorningReport.ReportReady -= onMorning;

        // В программе владельца своя локальная история продаж — подтягиваем свежие чеки с сервера, чтобы
        // сводка для советника (выручка, лучшие товары) была по сегодняшним данным.
        Opened += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                // 2026-10-21 (О3): сроки годности — заранее, пока владелец печатает вопрос.
                _ = ProductExpiryIndex.RefreshAsync(false, CancellationToken.None);
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
        Avalonia.Automation.AutomationProperties.SetName(_send, _busy
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

    /// <summary>2026-10-22 (модалки, пакет 4): что сделать с фото без подписи. true — как накладную (приход на склад),
    /// false — описать товар, null — отмена (Esc, ✕, «Отмена»). Окно в общей рамке ModalShell, Enter — «Как накладную».</summary>
    private async Task<bool?> AskImageIntentAsync()
    {
        bool? choice = null;
        var title = T("Изображение без подписи", "Сүрөттүн максаты", "Image intent", "Görüntü amacı", "Rasm maqsadi");
        var dlg = new Window { Title = title };
        SectionCards.AsModal(dlg, 512);
        var text = new TextBlock
        {
            Text = T("«Как накладную» — распознать строки и подготовить приход на склад (ничего не проводится без вашего подтверждения).\n«Описать товар» — только описание, склад и цены не меняются.",
                "«Накладной катары» — саптарды таанып, кампага киришти даярдоо (сиз ырастамайынча эч нерсе өткөрүлбөйт).\n«Товарды сүрөттөө» — сүрөттөө гана, кампа жана баалар өзгөрбөйт.",
                "“As an invoice” — recognize the lines and prepare a stock receipt (nothing is posted without your confirmation).\n“Describe product” — only a description, stock and prices stay unchanged.",
                "«Fatura olarak» — satırları tanı ve stok girişini hazırla (onayınız olmadan hiçbir şey kaydedilmez).\n«Ürünü açıkla» — yalnızca açıklama, stok ve fiyatlar değişmez.",
                "«Yuk xati sifatida» — qatorlarni tanib, omborga kirim tayyorlash (tasdig'ingizsiz hech narsa o'tkazilmaydi).\n«Mahsulotni tasvirlash» — faqat tavsif, ombor va narxlar o'zgarmaydi."),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        text.Bind(TextBlock.ForegroundProperty, dlg.GetResourceObservable("BrushText"));
        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => dlg.Close(false);
        var describe = SectionCards.Secondary(T("Описать товар", "Товарды сүрөттөө", "Describe product", "Ürünü açıkla", "Mahsulotni tasvirlash"));
        describe.Click += (_, _) => { choice = false; dlg.Close(true); };
        var invoice = SectionCards.Primary(T("Как накладную", "Накладной катары", "As an invoice", "Fatura olarak", "Yuk xati sifatida"), isDefault: true);
        invoice.Click += (_, _) => { choice = true; dlg.Close(true); };
        var shell = SectionCards.Shell(title, T("Что сделать с фото?", "Сүрөт менен эмне кылабыз?", "What to do with the photo?", "Fotoğrafla ne yapılsın?", "Rasm bilan nima qilamiz?"),
            text, SectionCards.Buttons(cancel, describe, invoice));
        shell.InitialFocus = invoice;
        dlg.Content = shell;
        await Dialogs.PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true);
        return choice;
    }

    private async Task SendAsync(string? text)
    {
        var question = (text ?? "").Trim();
        // Фото без текста неоднозначно. Спрашиваем назначение перед передачей ИИ, чтобы
        // обычное изображение товара не запускало сценарий прихода на склад.
        if (question.Length == 0 && _attachments.Count > 0)
        {
            // 2026-10-22 (модалки, пакет 4): ошибка — у окна-вопроса (PosConfirmDialog) всего два ответа, «Отмена»
            // не возвращалась: ✕, Esc и «Нет» одинаково запускали «описать товар». Теперь три кнопки-глагола:
            // «Как накладную» / «Описать товар» / «Отмена» (Esc и ✕ — отмена, фото остаётся во вложении).
            var asInvoice = await AskImageIntentAsync().ConfigureAwait(true);
            if (asInvoice is null)
                return;
            question = asInvoice == true
                ? T("Это фото накладной — распознай все строки и подготовь приход, ничего не проводи без моего подтверждения.",
                    "Бул накладнойдун сүрөтү — бардык саптарды таанып, мен ырастамайынча өткөрбөстөн киришти даярда.",
                    "This is an invoice photo. Recognize every line and prepare the receipt; do not post it without my confirmation.",
                    "Bu bir fatura fotoğrafı. Tüm satırları tanı ve onayım olmadan kaydetmeden stok girişini hazırla.",
                    "Bu yuk xati surati. Barcha qatorlarni tanib, tasdig'im bo'lmaguncha o'tkazmasdan kirimni tayyorla.")
                : T("Опиши товар на изображении. Не меняй склад и цены.", "Сүрөттөгү товарды сүрөттөп бер. Кампаны жана бааларды өзгөртпө.",
                    "Describe the product in the image. Do not change stock or prices.", "Görüntüdeki ürünü açıkla. Stok ve fiyatları değiştirme.",
                    "Rasmdagi mahsulotni tasvirla. Ombor va narxlarni o'zgartirma.");
        }
        if (question.Length == 0 || _busy || (!TelegramAiChat.IsConfigured && !IsPhotoRequest(question)))
            return;
        // 2026-10-11 (иишка.md, 1): несколько страниц накладной — все вложения в одном вопросе.
        var attachments = _attachments.ToList();
        var image = attachments.Count > 0 ? attachments[0].Data : null;
        var imageName = string.Join(", ", attachments.Select(a => a.Name));
        // Пометка «одна накладная на N страницах» — только для ИИ, в пузыре вопроса её не видно.
        var pagesNote = "";
        if (attachments.Count > 1)
            pagesNote = T($"\n(Это ОДНА накладная на {attachments.Count} страницах/фото: объедини строки всех страниц по порядку, строку на стыке страниц не дублируй.)",
                $"\n(Бул {attachments.Count} барактагы БИР накладной: бардык барактардын саптарын ирети менен бириктир, барактардын чегиндеги сапты кайталаба.)",
                $"\n(This is ONE invoice on {attachments.Count} pages/photos: merge the lines of all pages in order, don't duplicate a line split between pages.)",
                $"\n(Bu {attachments.Count} sayfalık TEK bir fatura: tüm sayfaların satırlarını sırayla birleştir, sayfa geçişindeki satırı tekrarlama.)",
                $"\n(Bu {attachments.Count} sahifali BITTA yuk xati: barcha sahifalar qatorlarini tartib bilan birlashtir, sahifalar chegarasidagi qatorni takrorlama.)");
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
        // 2026-10-06: «поставь фото 4» / «четвёртое» — вариант из последнего поиска фото ставит программа, без нейросети.
        // Короткая команда «открой …» (до 6 слов) — сразу; длинный вопрос со словом «открой» отвечает ИИ (он тоже умеет открывать).
        if (image is null && IsNoPhotoInventoryQuery(question))
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var reportToken = _cts.Token;
            var report = AddBubble(T("Проверяю локальный каталог склада…", "Кампанын жергиликтүү каталогун текшерип жатам…",
                "Checking the local warehouse catalog…", "Yerel depo kataloğu kontrol ediliyor…", "Omborning mahalliy katalogi tekshirilmoqda…"), fromOwner: false);
            try
            {
                var rows = await Task.Run(() => BuildNoPhotoInventoryReport(reportToken), reportToken)
                    .ConfigureAwait(true);
                if (gen != _gen)
                    return;
                report.Text = rows;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                report.Text = T("Не удалось прочитать локальный каталог склада. Обновите каталог и повторите запрос.",
                    "Кампанын жергиликтүү каталогун окуу мүмкүн болгон жок. Каталогду жаңыртып, кайра сураңыз.",
                    "Couldn't read the local warehouse catalog. Refresh it and try again.",
                    "Yerel depo kataloğu okunamadı. Kataloğu yenileyip tekrar deneyin.",
                    "Omborning mahalliy katalogini o'qib bo'lmadi. Katalogni yangilab qayta urinib ko'ring.");
                PosLogger.Log($"ИИ-советник: локальный список товаров без фото не собран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
            finally
            {
                if (gen == _gen)
                {
                    _busy = false;
                    RefreshKeyCard();
                    SaveCurrentChat();
                    ScrollToEnd();
                    _input.Focus();
                }
            }
            return;
        }
        // 2026-10-06: команды редактору сайта из чата — «поставь тему Ала-Тоо», «опубликуй сайт».
        if (image is null && SiteEditorWindow.LooksLikeCommand(question) && ProductActionPlan.OpenSection is not null)
        {
            var status = AddBubble("…", fromOwner: false);
            try
            {
                status.Text = await SiteEditorCommandAsync(question).ConfigureAwait(true)
                              ?? T("Не понял, что изменить на сайте: назовите тему (Солнечный, Ночной, Свежий рынок, Бутик, Ала-Тоо, Чистый) или скажите «опубликуй сайт».",
                                  "Сайтта эмнени өзгөртүүнү түшүнгөн жокмун: теманы атаңыз же «сайтты жарыяла» деңиз.",
                                  "I didn't get what to change on the website: name a theme or say “publish the website”.",
                                  "Sitede neyin değişeceğini anlamadım: bir tema adı söyleyin veya «siteyi yayınla» deyin.",
                                  "Saytda nimani o'zgartirishni tushunmadim: mavzu nomini ayting yoki «saytni e'lon qil» deng.");
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
        var quickOpen = image is null && PhotoChoiceNumber(question) is null && !HasActionBesidesOpen(question)
                        && question.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6 ? TryQuickOpen(question) : null;
        if (image is null && (PhotoChoiceNumber(question) is not null || quickOpen is not null))
        {
            var status = AddBubble("…", fromOwner: false);
            try
            {
                status.Text = quickOpen ?? await ApplyPhotoChoiceAsync(PhotoChoiceNumber(question)!.Value).ConfigureAwait(true);
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
        // 2026-10-10: список товаров без фото формируем из полного локального каталога,
        // а не просим нейросеть угадать по сокращённой сводке склада. Веб-поиск здесь не запускается.
        if (image is null && IsMissingPhotoListRequest(question))
        {
            var status = AddBubble(T("Сверяю весь локальный каталог…", "Жергиликтүү каталогду толук текшерип жатам…",
                "Checking the full local catalog…", "Yerel kataloğun tamamı denetleniyor…", "Mahalliy katalog to‘liq tekshirilmoqda…"), fromOwner: false);
            try
            {
                status.Text = await Task.Run(OwnerAiContext.BuildMissingPhotoReport).ConfigureAwait(true);
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
        // 2026-10-21, «доведи Нур Советника до 10»: права пользователя (О4) — без права ИИ даже не спрашиваем; телефоны клиентов —
        // таблицей прямо из программы, в ИИ не уходят (О5); этапы ответа на экране и время каждого этапа в журнале (О3).
        var access = CurrentAccess();
        if (image is null && (DeniedAnswer(question, access) ?? FutureDateAnswer(question)) is { } denied)
        {
            AddBubble(denied, fromOwner: false);
            PosLogger.Log("ИИ-советник: ответ без ИИ (нет доступа к разделу или будущая дата).", "INFO");
            _busy = false;
            RefreshKeyCard();
            SaveCurrentChat();
            return;
        }
        var (thinking, setStage) = AddStageBubble();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            if (image is null && await TryLocalClientsListAsync(question, access, token).ConfigureAwait(true) is { } localList)
            {
                if (gen != _gen)
                    return;
                thinking.Text = localList;
                AddAnswerExtras(thinking, question, localList, Array.Empty<AiSection>(), localOnly: true);
                SaveCurrentChat();
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (sections, debtorNames) = await GetSummaryAsync().ConfigureAwait(true);
            var summaryMs = watch.ElapsedMilliseconds;
            var shop = sections.ToList();
            if (access.Products && image is null && await OwnerAiContext.QuestionProductsSectionAsync(question, token).ConfigureAwait(true) is { } qp)
                shop.Add(qp);
            if (image is null && await PeriodRevenueSectionAsync(question, token).ConfigureAwait(true) is { } pr)
                shop.Add(pr);
            var questionMs = watch.ElapsedMilliseconds - summaryMs;
            // 2026-10-05, владелец: «голос очень сильно тормозит» — на вопрос голосом ответ короткий: быстрее и пишется,
            // и озвучивается (длинный ответ на 8–10 предложений звучал полминуты и готовился долго).
            var askText = (speak
                ? question + "\n(Вопрос задан голосом, ответ будет озвучен: ответь коротко — 2–3 предложения, без списков и таблиц.)"
                : TelegramAiChat.LooksLikeListRequest(question) && image is null
                    // 2026-10-21 (О6): «выведи список» — таблица, а не карточка действий.
                    ? question + "\n(Владелец просит ПОКАЗАТЬ список: выведи таблицей все подходящие строки из данных; строки «ТОВАР:» не пиши, ничего не меняй.)"
                    : question) + pagesNote;
            var result = await TelegramAiChat.AskOwnerAppAsync(askText, shop, access, token, attachments.Select(a => (a.Data, a.Mime)).ToList(),
                stage => Dispatcher.UIThread.Post(() => setStage(stage)), forceFull: ForceSections(question)).ConfigureAwait(true);
            var answer = result.Answer;
            var error = result.Error;
            if (gen != _gen)
                return;
            PosLogger.Log($"ИИ-советник: этапы — сводка {summaryMs} мс (кэш {(summaryMs < 50 ? "да" : "нет")}), товары из вопроса {questionMs} мс, {result.Timings}{(speak ? " (голосом)" : "")}.", "INFO");
            Dictionary<string, bool>? botChange = null;
            Dictionary<string, object?>? scenario = null;
            // 2026-10-06, владелец: «к ИИ дай полный доступ к товарам» — строки «ТОВАР: {…}» → карточка «Выполнить».
            List<ProductActionPlan.Step>? productSteps = null;
            IReadOnlyList<string> skippedItems = Array.Empty<string>();
            List<(string Name, string? Phone, double Amount)>? debtReminders = null;
            if (answer is { Length: > 0 })
            {
                (answer, botChange) = ExtractBotChange(answer);
                (answer, scenario) = ExtractScenario(answer);
                // 2026-10-19, ревью: подсказка поставщика — от накладной: заменяется новой строкой «ПОСТАВЩИК» или новым фото
                // (другая накладная), а ответ без неё («поставь наценку 25 %») прежнюю подсказку не стирает.
                if (ProductActionPlan.SupplierHint(answer) is var hint && (hint.Name is not null || hint.Inn is not null || attachments.Count > 0))
                    _invoiceSupplierHint = hint;
                (answer, productSteps, skippedItems) = ProductActionPlan.Extract(answer, question);
                (answer, debtReminders) = ExtractDebtReminders(answer);
                // 2026-10-21 (О6): просили ПОКАЗАТЬ список, а ИИ ответил действиями без таблицы (история 08.10: «выведи список всех 21» —
                // 4 раза «подготовил… подтвердите») — показываем таблицу этих товаров, карточку действий не показываем.
                if (TelegramAiChat.LooksLikeListRequest(question) && image is null && productSteps.Count > 0 && !HasTable(answer)
                    && !ProductActionPlan.LooksLikeAction(System.Text.RegularExpressions.Regex.Replace(question.ToLowerInvariant(), @"выведи\w*|покажи\w*|список\w*|таблиц\w*", " ")))
                {
                    answer = (answer.Trim().Length > 0 ? answer.Trim() + "\n\n" : "") + ListFromSteps(productSteps);
                    productSteps = new List<ProductActionPlan.Step>();
                }
                // 2026-10-23 (аудит Б-5/Б-4): вопрос только на чтение («Какие акции запустить?», «что посоветуешь?») — карточку действий
                // на запись (списание, цены) не показываем; действия — только на явный приказ («спиши», «поставь цену»).
                if (image is null && productSteps.Count > 0 && IsReadOnlyQuestion(question))
                {
                    PosLogger.Log($"ИИ-советник: вопрос на чтение — карточка из {productSteps.Count} действий скрыта.", "INFO");
                    productSteps = new List<ProductActionPlan.Step>();
                    if (answer.Trim().Length == 0)
                        answer = T("Это совет, ничего не меняю. Чтобы я сделал, скажите прямо, например: «поставь цену …» или «спиши …».",
                            "Бул кеңеш, эч нерсе өзгөртпөйм. Кылышымды кааласаңыз, түз айтыңыз: «баасын койгун …» же «списать …».",
                            "This is advice only, nothing is changed. To make me do it, say so directly, e.g. “set the price …” or “write off …”.",
                            "Bu yalnızca öneri, hiçbir şey değişmiyor. Yapmamı istiyorsanız açıkça söyleyin: «fiyatı … yap» veya «… düş».",
                            "Bu faqat maslahat, hech narsa o'zgarmaydi. Bajarishimni istasangiz, to'g'ridan-to'g'ri ayting: «narxini … qil» yoki «… hisobdan chiqar».");
                }
                if (answer.Length == 0 && productSteps.Count > 0)
                    answer = T("Предлагаю изменения — подтвердите:", "Өзгөртүүлөрдү сунуштайм — ырастаңыз:", "I suggest these changes — please confirm:",
                        "Şu değişiklikleri öneriyorum — onaylayın:", "Quyidagi o'zgarishlarni taklif qilaman — tasdiqlang:");
                // 2026-10-07, владелец: «перестань выдавать фото из базы NurCRM» — фото склада под ответом больше не подставляются
                // (находили «Нан» внутри «бананды»); фото — только поиском в интернете по просьбе.
            }
            if (answer is { Length: > 0 })
            {
                thinking.Text = OwnerAiContext.RevealCodes(TelegramAiChat.ToPlainText(answer), debtorNames);
                AddAnswerExtras(thinking, question, thinking.Text ?? "", result.Included, localOnly: false, fallback: result.Fallback);
            }
            else
                ShowFriendlyError(thinking, error, question);
            // 2026-10-05, владелец: «включи поиск по интернету для ИИ» — если ИИ искал в интернете, источники ссылками.
            // 2026-10-07: Gemini сам ищет в Google и по вопросам о магазине (по старым репликам разговора) — ссылки показываем,
            // только если владелец просил поиск или сведения о товаре.
            var webSources = result.Sources;
            if (answer is { Length: > 0 } && webSources.Count > 0
                && (ProductInfoResearch.LooksLikeInfoRequest(question)
                    || new[] { "интернет", "найди", "поищи", "google", "гугл", "сайт", "internet", "интернеттен" }.Any(question.ToLowerInvariant().Contains)))
                AddWebSources(webSources);
            else if (ProductActionPlan.IsExplicitWebSearchRequest(question) && TelegramAiChat.WebSearchUnavailable && !AiProviders.HasGroq && !_webSearchNoteShown)
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
            // 2026-10-21 (О4): изменения — только с правом на них; без права карточки нет, а владелец видит почему.
            if (botChange is { Count: > 0 })
            {
                if (access.Settings)
                    AddBotChangeCard(botChange);
                else
                    AddBubble(NoAccessText(T("настройкам Телеграм-бота", "Телеграм-боттун жөндөөлөрүнө", "the Telegram bot settings", "Telegram botu ayarlarına", "Telegram bot sozlamalariga"), "Настройки"), fromOwner: false);
            }
            if (scenario is not null && access.Settings)
                AddScenarioCard(scenario);
            if (productSteps is { Count: > 0 })
            {
                if (access.Products)
                    ShowProductSteps(productSteps, question);
                else
                    AddBubble(NoAccessText(T("складу и изменению товаров", "кампага жана товарларды өзгөртүүгө", "the warehouse and product changes", "depoya ve ürün değişikliklerine", "omborga va mahsulotlarni o'zgartirishga"), "Склад"), fromOwner: false);
            }
            if (debtReminders is { Count: > 0 } && access.Clients)
                AddDebtReminderCard(debtReminders);
            // 2026-10-06: строки, которые не стали действиями, — не молча, а списком (раньше накладная на 20 позиций давала «ничего»).
            if (skippedItems.Count > 0 && access.Products)
                AddBubble(T("Не нашёл в каталоге и не смог разобрать: ", "Каталогдон таппадым жана ажырата алган жокмун: ", "Not found in the catalog and couldn't parse: ",
                    "Katalogda bulunamadı ve ayrıştırılamadı: ", "Katalogda topilmadi va ajratib bo'lmadi: ") + string.Join(", ", skippedItems)
                    + T(". Напишите, что с ними сделать (например, «создай как новые товары»).", ". Алар менен эмне кылууну жазыңыз (мисалы, «жаңы товар катары түз»).",
                        ". Tell me what to do with them (e.g. “create as new products”).", ". Bunlarla ne yapılacağını yazın (ör. «yeni ürün olarak oluştur»).",
                        ". Ular bilan nima qilishni yozing (masalan, «yangi mahsulot sifatida yarat»)."), fromOwner: false);
            if (speak && answer is { Length: > 0 })
                _ = SpeakAsync(thinking.Text ?? "");
        }
        catch (OperationCanceledException)
        {
            // 2026-10-21 (О8): не «The operation was canceled.», а понятный текст (если это не «■ Стоп» владельца).
            if (gen == _gen)
                ShowFriendlyError(thinking, "timeout", question);
            else
                return;
        }
        catch (Exception ex)
        {
            ShowFriendlyError(thinking, ex is System.Net.Http.HttpRequestException ? "нет связи" : "internal", question);
            PosLogger.Log($"ИИ-советник: ошибка ответа ({ex.GetType().Name}: {ex.Message}).", "WARNING");
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
        // 2026-10-21 («удобство 10»): поиск по разговорам — по названию и тексту реплик.
        var query = (_historySearch?.Text ?? "").Trim();
        var chats = LoadChats().Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                           || c.Lines.Any(l => l.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
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
            Avalonia.Automation.AutomationProperties.SetName(open, chat.Title);
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
            Avalonia.Automation.AutomationProperties.SetName(delete, T("Удалить разговор", "Маекти өчүрүү", "Delete chat", "Sohbeti sil", "Suhbatni o'chirish") + ": " + chat.Title);
            // 2026-10-21 (О10): удаление — только после подтверждения (раньше ✕ удалял сразу, без отмены).
            delete.Click += async (_, _) =>
            {
                // 2026-10-21: окно с понятными кнопками «Удалить» (красная) и «Отменить» вместо «Да/Нет» (пакет 7, п. 157).
                var sure = await Dialogs.PosConfirmDialog.ShowModalAsync(this,
                    T("Удалить разговор", "Маекти өчүрүү", "Delete chat", "Sohbeti sil", "Suhbatni o'chirish"),
                    T($"Удалить разговор «{chat.Title}»? Вернуть его будет нельзя.", $"«{chat.Title}» маегин өчүрөлүбү? Кайра калыбына келтирүүгө болбойт.",
                        $"Delete the chat “{chat.Title}”? It can't be restored.", $"«{chat.Title}» sohbeti silinsin mi? Geri alınamaz.",
                        $"«{chat.Title}» suhbati o'chirilsinmi? Uni qaytarib bo'lmaydi."),
                    confirmText: T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                    cancelText: T("Отменить", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                    accent: Dialogs.PosConfirmAccent.Danger).ConfigureAwait(true);
                if (!sure)
                    return;
                var all = LoadChats();
                all.RemoveAll(c => c.Id == chat.Id);
                StoreChats(all);
                PosLogger.Log("ИИ-советник: разговор удалён владельцем (после подтверждения).", "INFO");
                RefreshHistoryList();
            };
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
            row.Classes.Add("no-reflow");
            _historyList.Children.Add(row);
        }
    }

    private TextBox? _historySearch;

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
        // 2026-10-21 («удобство 10»): поиск по прошлым разговорам.
        _historySearch = UiKit.Input(this, T("Найти разговор…", "Маекти табуу…", "Find a chat…", "Sohbet bul…", "Suhbatni topish…"), 36);
        _historySearch.Margin = new Thickness(2, 0, 2, 8);
        Avalonia.Automation.AutomationProperties.SetName(_historySearch, T("Поиск по разговорам", "Маектерден издөө", "Search chats", "Sohbetlerde ara", "Suhbatlardan qidirish"));
        _historySearch.TextChanged += (_, _) => RefreshHistoryList();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(_historySearch, 1);
        Grid.SetRow(list, 2);
        grid.Children.Add(head);
        grid.Children.Add(_historySearch);
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
        // 2026-10-21: «Новый разговор» — обычной кнопкой, как «История»: жёлтая на экране одна — «Сохранить» ключ без ключа,
        // ➤ «Спросить» с ключом (было две-три жёлтых рядом) (пакет 7, п. 155).
        var newChat = UiKit.Ghost(this, T("＋  Новый разговор", "＋  Жаңы маек", "＋  New chat", "＋  Yeni sohbet", "＋  Yangi suhbat"));
        newChat.Height = 40;
        newChat.CornerRadius = new CornerRadius(20);
        newChat.Click += (_, _) => ResetChat();
        // 2026-10-21: имена для диктора без значков (на узком экране на кнопках только «🕘» и «＋») (пакет 7, п. 156).
        Avalonia.Automation.AutomationProperties.SetName(_menuButton, T("История разговоров", "Маектердин тарыхы", "Chat history", "Sohbet geçmişi", "Suhbatlar tarixi"));
        Avalonia.Automation.AutomationProperties.SetName(newChat, T("Новый разговор", "Жаңы маек", "New chat", "Yeni sohbet", "Yangi suhbat"));
        buttons.Children.Add(newChat);
        var models = RoundButton(new TextBlock { Text = "⚙", FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, accent: false);
        ToolTip.SetTip(models, T("Модели ИИ и поиск в интернете", "ИИ моделдери жана интернеттен издөө", "AI models and web search", "Yapay zekâ modelleri ve internet araması", "SI modellari va internetda qidirish"));
        Avalonia.Automation.AutomationProperties.SetName(models, T("Настройки советника", "Кеңешчинин жөндөөлөрү", "Advisor settings", "Danışman ayarları", "Maslahatchi sozlamalari"));
        // 2026-10-21 (И3, И9): ⚙ — меню: ключи и модели ИИ, утренний отчёт, напоминания должникам.
        var gearMenu = new MenuFlyout();
        // 2026-10-22 (модалки, пакет 4): пункты меню 44 px, как меню «Ещё» владельца (стиль row-menu).
        gearMenu.FlyoutPresenterClasses.Add("row-menu");
        void Item(string text, Action action)
        {
            // 2026-10-21: пункт с именем для диктора (без значка ☀/✉) и с «Нажать» (Invoke) в UIA — раньше экранный диктор и
            // автоматизация пункт видели, но нажать не могли (пакет 7, хвост Советника).
            var mi = new InvokableMenuItem { Header = text };
            Avalonia.Automation.AutomationProperties.SetName(mi, text.TrimStart('☀', '✉', ' '));
            mi.Click += (_, _) =>
            {
                gearMenu.Hide();
                try { action(); }
                catch (Exception ex) { PosLogger.Log($"ИИ-советник: пункт «{text}» не открылся ({ex.Message}).", "WARNING"); }
            };
            gearMenu.Items.Add(mi);
        }
        Item(T("Модели ИИ и ключи", "ИИ моделдери жана ачкычтар", "AI models and keys", "Yapay zekâ modelleri ve anahtarlar", "SI modellari va kalitlar"), () =>
        {
            var show = !(_modelsCard.IsVisible && _routerSection.IsVisible);
            _modelsCard.IsVisible = show;
            _routerSection.IsVisible = show;
            if (show)
                _scroll.Offset = new Vector(0, 0);
        });
        Item("☀ " + T("Утренний отчёт…", "Эртең мененки отчёт…", "Morning report…", "Sabah raporu…", "Ertalabki hisobot…"), () => _ = MorningReportSettingsWindow.ShowAsync(this));
        Item("✉ " + T("Напоминания должникам…", "Карыздарга эскертүүлөр…", "Debt reminders…", "Borç hatırlatmaları…", "Qarz eslatmalari…"), OpenDebtReminders);
        models.Flyout = gearMenu;
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
        // 2026-10-21 («дизайн 10»): вместо технических имён моделей — понятная строка; модели — в подсказке и по ⚙.
        var parts = new List<string> { "Google Gemini" };
        if (AiProviders.HasGroq)
            parts.Add(T("поиск в интернете: Groq", "интернеттен издөө: Groq", "web search: Groq", "internet araması: Groq", "internetda qidirish: Groq"));
        if (AiProviders.HasOpenRouter)
            parts.Add(T("запасная модель: OpenRouter", "запастагы модель: OpenRouter", "backup model: OpenRouter", "yedek model: OpenRouter", "zaxira model: OpenRouter"));
        _status.Text = "● " + T("Готов отвечать по данным вашего магазина", "Дүкөнүңүздүн маалыматы боюнча жооп берүүгө даярмын", "Ready to answer from your shop's data",
            "Mağazanızın verileriyle yanıtlamaya hazır", "Do'koningiz ma'lumotlari bo'yicha javob berishga tayyor");
        ToolTip.SetTip(_status, string.Join(" · ", parts));
    }

    /// <summary>Пустой чат: значок, «Чем помочь?», что умеет советник, готовые вопросы карточками.
    /// 2026-10-21 (И3): под вопросами — утренний отчёт за сегодня, если он уже собран.</summary>
    private void ShowWelcome()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 800 };
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
            // 2026-10-21 («дизайн 10»): вступление — одна-две строки (было 3–4).
            Text = T("Отвечаю по цифрам вашего магазина и показываю, откуда они. Можно голосом — 🎤 или «Разговор».",
                "Дүкөнүңүздүн сандары боюнча жооп берем жана алар кайдан экенин көрсөтөм. Үн менен да болот — 🎤 же «Маек».",
                "I answer from your shop's numbers and show where they come from. Voice works too — 🎤 or “Talk”.",
                "Mağazanızın rakamlarıyla yanıtlar ve kaynağını gösteririm. Sesle de olur — 🎤 veya «Sohbet».",
                "Do'koningiz raqamlari bo'yicha javob beraman va manbasini ko'rsataman. Ovoz bilan ham — 🎤 yoki «Suhbat»."),
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
        // 2026-10-21 (И3): утренний отчёт за сегодня — сразу на пустом экране (как «For you» у лучших помощников).
        if (OwnerMorningReport.Latest is { } morning && morning.BuiltAt.Date == DateTime.Today)
        {
            var card = MorningReportCard.Build(morning, OpenSectionSafe, () => _ = MorningReportSettingsWindow.ShowAsync(this));
            card.Margin = new Thickness(0, 6, 0, 0);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(card);
            _morningShownFor = morning.BuiltAt;
        }
        if (DebtReminderBanner() is { } remind)
            panel.Children.Add(remind);
        _welcome = panel;
        _messages.Children.Add(panel);
        RefreshKeyCard();
    }

    private DateTime? _morningShownFor;

    /// <summary>2026-10-21: всем карточкам-вопросам — высота самой высокой (по естественной высоте содержимого, поэтому при смене
    /// ширины или языка высота и уменьшается) (пакет 7).</summary>
    private void EqualizeQuickChips()
    {
        var chips = _quick.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
        if (chips.Count < 2)
            return;
        double max = 84;
        foreach (var chip in chips)
        {
            if (chip.Content is not Control content || !content.IsMeasureValid)
                return;
            max = Math.Max(max, content.DesiredSize.Height + chip.Padding.Top + chip.Padding.Bottom + chip.BorderThickness.Top + chip.BorderThickness.Bottom);
        }
        max = Math.Ceiling(max);
        foreach (var chip in chips)
            if (Math.Abs(chip.MinHeight - max) > 0.5)
                chip.MinHeight = max;
    }

    /// <summary>2026-10-21 (И3): готов новый утренний отчёт — на пустом экране обновить приветствие, в разговоре — карточкой в ленту.</summary>
    private void OnMorningReport(OwnerMorningReport.Report r)
    {
        if (_morningShownFor == r.BuiltAt)
            return;
        if (_welcome is not null)
        {
            _messages.Children.Remove(_welcome);
            _welcome = null;
            ShowWelcome();
            return;
        }
        _morningShownFor = r.BuiltAt;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var avatar = AiAvatar(30);
        avatar.Margin = new Thickness(0, 2, 12, 0);
        row.Children.Add(avatar);
        var card = MorningReportCard.Build(r, OpenSectionSafe, () => _ = MorningReportSettingsWindow.ShowAsync(this));
        Grid.SetColumn(card, 1);
        row.Children.Add(card);
        _messages.Children.Add(row);
        ScrollToEnd();
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
            UseOwnerBubble(bubble);
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
        // 2026-10-06, владелец: «выводи данные нормально, как таблицу, если это данные магазина» — таблицы Markdown в ответе — сеткой.
        RenderTables(body, content);
        body.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
                RenderTables(body, content);
        };
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

    private static readonly System.Text.RegularExpressions.Regex TableLine = new(@"^\s*\|.*\|\s*$");
    private static readonly System.Text.RegularExpressions.Regex TableSeparator = new(@"^\s*\|?(\s*:?-{2,}:?\s*\|)+\s*(:?-{2,}:?\s*)?$");
    private static readonly System.Text.RegularExpressions.Regex DateCell = new(@"^\d{1,2}\.\d{1,2}(\.\d{2,4})?$");
    private static readonly System.Text.RegularExpressions.Regex NumericCell = new(@"^[−\-+]?[\d\s  .,]+(%|\s*(сом|шт|ч|мин|дн\.?|кг|л))?$");

    /// <summary>2026-10-21: ячейки строки «Итого | 87 589,23 |» / «| A | B» без пустых краёв (пакет 7, п. 160).</summary>
    private static string[] StrayCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("**", "")).ToArray();

    /// <summary>Строка с «|», которую ИИ прислал вне таблицы или без «|» по краям: две и больше непустых ячеек.</summary>
    private static bool IsStrayRow(string line) =>
        line.Contains('|') && !TableSeparator.IsMatch(line) && StrayCells(line).Count(c => c.Length > 0) >= 2;

    private static bool IsTotalRow(string line) =>
        StrayCells(line) is { Length: > 0 } cells && TotalWords.Any(w => cells[0].StartsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Строка с «палками» вне таблицы — обычным текстом: «Итого: 87 589,23», три и больше ячеек — через «·».</summary>
    private static string TidyStrayRow(string line)
    {
        if (!IsStrayRow(line))
            return line;
        var cells = StrayCells(line).Where(c => c.Length > 0).ToArray();
        return cells.Length == 2 ? cells[0].TrimEnd(':') + ": " + cells[1] : string.Join(" · ", cells);
    }

    /// <summary>Ответ с таблицей Markdown: текст до/после — абзацами, таблица — сеткой (заголовок, разделители, числа справа,
    /// широкая — с прокруткой). Исходный текст остаётся в скрытом body — для «Копировать» и «Озвучить».</summary>
    private void RenderTables(SelectableTextBlock body, StackPanel content)
    {
        if (content.Children.OfType<StackPanel>().FirstOrDefault(p => Equals(p.Tag, "tables")) is { } old)
            content.Children.Remove(old);
        var lines = (body.Text ?? "").Replace("\r\n", "\n").Split('\n');
        // 2026-10-21: строка «Итого | 87 589,23 |» без таблицы (или оторванная от неё) — тоже без «палок» (пакет 7, п. 160).
        if (!lines.Any(l => TableSeparator.IsMatch(l)) && !lines.Any(IsStrayRow))
        {
            body.IsVisible = true;
            return;
        }
        var panel = new StackPanel { Spacing = 10, Tag = "tables" };
        var buf = new List<string>();
        void FlushText()
        {
            var t = string.Join("\n", buf.Select(TidyStrayRow)).Trim();
            buf.Clear();
            if (t.Length == 0)
                return;
            var tb = new SelectableTextBlock { Text = t, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
            Use(tb, TextBlock.ForegroundProperty, "BrushText");
            panel.Children.Add(tb);
        }
        static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("**", "")).ToArray();
        for (var i = 0; i < lines.Length;)
        {
            if (i + 1 < lines.Length && (TableLine.IsMatch(lines[i]) || IsStrayRow(lines[i])) && TableSeparator.IsMatch(lines[i + 1]))
            {
                FlushText();
                var header = Cells(lines[i]);
                i += 2;
                var rows = new List<string[]>();
                // 2026-10-21: строка таблицы и без «|» в начале/конце («Итого | 87 589,23 |»), итог — и после одной пустой строки
                // (раньше такая строка выпадала из таблицы и показывалась текстом с «палками») (пакет 7, п. 160).
                bool Row(int k) => k < lines.Length && (TableLine.IsMatch(lines[k]) || IsStrayRow(lines[k]));
                while (Row(i) || (i + 1 < lines.Length && lines[i].Trim().Length == 0 && Row(i + 1) && IsTotalRow(lines[i + 1])))
                {
                    if (lines[i].Trim().Length == 0)
                        i++;
                    var cells = Cells(lines[i++]);
                    // ИИ иногда ставит «| | Имя | …» — лишняя пустая ячейка сдвигает строку вправо.
                    while (cells.Length > header.Length && cells.Length > 0 && cells[0].Length == 0)
                        cells = cells[1..];
                    rows.Add(cells);
                }
                panel.Children.Add(BuildTable(header, rows));
            }
            else
                buf.Add(lines[i++]);
        }
        FlushText();
        body.IsVisible = false;
        content.Children.Insert(content.Children.IndexOf(body) + 1, panel);
    }

    private Control BuildTable(string[] header, List<string[]> rows)
    {
        var cols = Math.Max(header.Length, rows.Count > 0 ? rows.Max(r => r.Length) : 0);
        // 2026-10-21 («дизайн 10»): числовая колонка — вправо целиком, вместе с заголовком; даты (14.10) — числом не считаются.
        var numericCol = new bool[cols];
        for (var c = 0; c < cols; c++)
        {
            var cells = rows.Where(r => !(r.Length > 0 && TotalWords.Any(w => r[0].Trim().StartsWith(w, StringComparison.OrdinalIgnoreCase))))
                .Select(r => c < r.Length ? r[c].Trim() : "").Where(t => t.Length > 0 && t != "-" && t != "—").ToList();
            numericCol[c] = cells.Count > 0 && cells.All(t => NumericCell.IsMatch(t) && !DateCell.IsMatch(t));
        }
        var grid = new Grid();
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r <= rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Cell(int r, int c, string text, bool head, bool total)
        {
            var tb = new SelectableTextBlock
            {
                Text = text, FontSize = 13.5, TextWrapping = TextWrapping.NoWrap,
                FontWeight = head || total ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = c < numericCol.Length && numericCol[c] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Use(tb, TextBlock.ForegroundProperty, head ? "BrushTextSoft" : "BrushText");
            var cell = new Border { Padding = new Thickness(12, 7), BorderThickness = new Thickness(0, 0, 0, r < rows.Count ? 1 : 0), Child = tb };
            Use(cell, Border.BorderBrushProperty, "BrushBorder");
            if (head || total)
                Use(cell, Border.BackgroundProperty, "BrushPanel");
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
        for (var c = 0; c < cols; c++)
            Cell(0, c, c < header.Length ? header[c] : "", head: true, total: false);
        for (var r = 0; r < rows.Count; r++)
        {
            // 2026-10-21 («дизайн 10»): строка итога — на любом языке (было только «Итого»: ky «Жыйынтык» не выделялась).
            var total = rows[r].Length > 0 && TotalWords.Any(w => rows[r][0].Trim().StartsWith(w, StringComparison.OrdinalIgnoreCase));
            for (var c = 0; c < cols; c++)
                Cell(r + 1, c, c < rows[r].Length ? rows[r][c] : "", head: false, total);
        }
        var frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), ClipToBounds = true, Child = grid, HorizontalAlignment = HorizontalAlignment.Left };
        Use(frame, Border.BorderBrushProperty, "BrushBorder");
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = frame };
    }

    private static readonly System.Text.RegularExpressions.Regex RemindLine = new(@"(?im)^\s*НАПОМНИТЬ\s*:\s*(.+)$");

    /// <summary>2026-10-06: строка ИИ «НАПОМНИТЬ: [Д1], [Д3]» (или «все») → должники с телефонами из последней сводки.</summary>
    private static (string Answer, List<(string Name, string? Phone, double Amount)>? List) ExtractDebtReminders(string answer)
    {
        var m = RemindLine.Match(answer);
        if (!m.Success)
            return (answer, null);
        var cleaned = RemindLine.Replace(answer, "").Trim();
        var all = OwnerAiContext.LastDebtors;
        var spec = m.Groups[1].Value;
        var list = spec.Contains("все", StringComparison.OrdinalIgnoreCase) || spec.Contains("all", StringComparison.OrdinalIgnoreCase)
            ? all.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList()
            : System.Text.RegularExpressions.Regex.Matches(spec, @"Д(\d{1,3})")
                .Select(x => int.TryParse(x.Groups[1].Value, out var n) && all.TryGetValue(n, out var d) ? d : default)
                .Where(d => d.Name is not null).Distinct().ToList();
        return (cleaned, list);
    }

    /// <summary>Международный номер для WhatsApp: «0700 123 456» → 996700123456, «700123456» → 996700123456.</summary>
    private static string? WhatsAppDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits[0] == '0')
            digits = "996" + digits[1..];
        else if (digits.Length == 9)
            digits = "996" + digits;
        return digits.Length >= 11 ? digits : null;
    }

    private Window? _whatsAppWindow;

    private void OpenDebtReminder((string Name, string? Phone, double Amount) d)
    {
        if (WhatsAppDigits(d.Phone) is not { } digits)
            return;
        var money = d.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " сом";
        var text = T($"Здравствуйте, {d.Name}! Напоминаем о долге в нашем магазине: {money}. Пожалуйста, верните долг. Спасибо!",
            $"Саламатсызбы, {d.Name}! Дүкөнүбүздөгү карызыңызды эскертебиз: {money}. Карызды кайтарып бериңиз. Рахмат!",
            $"Hello, {d.Name}! A reminder about your debt at our shop: {money}. Please pay it back. Thank you!",
            $"Merhaba {d.Name}! Mağazamızdaki borcunuzu hatırlatırız: {money}. Lütfen borcunuzu ödeyin. Teşekkürler!",
            $"Assalomu alaykum, {d.Name}! Do'konimizdagi qarzingizni eslatamiz: {money}. Iltimos, qarzni qaytaring. Rahmat!");
        var query = "phone=" + digits + "&text=" + Uri.EscapeDataString(text);
        if (OperatingSystem.IsWindows())
        {
            // Встроенный WhatsApp Web (вход по QR-коду сохраняется) — одно окно, следующий должник открывается в нём же.
            // Владелец мог уже закрыть окно сам — закрываем только открытое.
            if (_whatsAppWindow is { IsVisible: true } previous)
                previous.Close();
            var window = new CrmWebViewWindow(CrmWebViewWindow.WhatsAppWebUrl + "/send?" + query, "WhatsApp Web");
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_whatsAppWindow, window))
                    _whatsAppWindow = null;
            };
            _whatsAppWindow = window;
            window.Show();
        }
        else
            SiteOrdersWindow.OpenUrl("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(text));
        PosLogger.Log($"ИИ-советник: напоминание о долге — открыт чат WhatsApp ({digits[..4]}…).", "INFO");
    }

    /// <summary>2026-10-06, владелец: «если открыт WhatsApp, дай ИИ доступ, чтобы по номеру находил должников и писал им вернуть долг».
    /// Карточка: должник, сумма, «Написать в WhatsApp» (чат с готовым текстом — отправляет владелец) и «Следующий ▶» по очереди.</summary>
    private void AddDebtReminderCard(List<(string Name, string? Phone, double Amount)> debtors)
    {
        var title = new TextBlock { Text = T("Напомнить о долге в WhatsApp", "WhatsApp'та карызды эскертүү", "Debt reminder on WhatsApp", "WhatsApp'tan borç hatırlatma", "WhatsApp'da qarzni eslatish"),
            FontWeight = FontWeight.Bold, FontSize = 14.5 };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock { Text = T("Откроется чат с готовым текстом — проверьте и нажмите «Отправить» в WhatsApp.",
                "Даяр текст менен чат ачылат — текшерип, WhatsApp'та «Жөнөтүү» басыңыз.",
                "A chat opens with the message ready — check it and press Send in WhatsApp.",
                "Hazır metinle sohbet açılır — kontrol edip WhatsApp'ta Gönder'e basın.",
                "Tayyor matn bilan chat ochiladi — tekshirib, WhatsApp'da «Yuborish»ni bosing."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        var rows = new StackPanel { Spacing = 6 };
        var withPhone = debtors.Where(d => WhatsAppDigits(d.Phone) is not null).ToList();
        var next = 0;
        Button? nextButton = null;
        void UpdateNext()
        {
            if (nextButton is null)
                return;
            nextButton.IsEnabled = next < withPhone.Count;
            nextButton.Content = next < withPhone.Count
                ? T($"Следующий ▶ {withPhone[next].Name} ({next + 1} из {withPhone.Count})", $"Кийинки ▶ {withPhone[next].Name} ({withPhone.Count} ичинен {next + 1})",
                    $"Next ▶ {withPhone[next].Name} ({next + 1} of {withPhone.Count})", $"Sonraki ▶ {withPhone[next].Name} ({next + 1}/{withPhone.Count})",
                    $"Keyingi ▶ {withPhone[next].Name} ({next + 1}/{withPhone.Count})")
                : T("Все чаты открыты", "Бардык чаттар ачылды", "All chats opened", "Tüm sohbetler açıldı", "Barcha chatlar ochildi");
        }
        foreach (var d in debtors)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var name = new TextBlock { Text = d.Name + (d.Phone is { Length: > 0 } p ? $" · {p}" : ""), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            var sum = new TextBlock { Text = d.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " сом", FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(sum, TextBlock.ForegroundProperty, "BrushDanger");
            sum.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(sum, 1);
            row.Children.Add(name);
            row.Children.Add(sum);
            if (WhatsAppDigits(d.Phone) is not null)
            {
                var wa = UiKit.Ghost(this, T("Написать в WhatsApp", "WhatsApp'ка жазуу", "Write on WhatsApp", "WhatsApp'tan yaz", "WhatsApp'da yozish"));
                wa.Height = 32;
                wa.FontSize = 12.5;
                var debtor = d;
                wa.Click += (_, _) => OpenDebtReminder(debtor);
                Grid.SetColumn(wa, 2);
                row.Children.Add(wa);
            }
            else
            {
                var none = new TextBlock { Text = T("нет телефона", "телефон жок", "no phone", "telefon yok", "telefon yo'q"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Use(none, TextBlock.ForegroundProperty, "BrushTextSoft");
                Grid.SetColumn(none, 2);
                row.Children.Add(none);
            }
            rows.Children.Add(row);
        }
        var panel = new StackPanel { Spacing = 8, Children = { title, hint, rows } };
        if (withPhone.Count > 1)
        {
            nextButton = UiKit.Primary(this, "");
            nextButton.Height = 36;
            nextButton.HorizontalAlignment = HorizontalAlignment.Left;
            nextButton.Click += (_, _) =>
            {
                if (next >= withPhone.Count)
                    return;
                OpenDebtReminder(withPhone[next++]);
                UpdateNext();
            };
            UpdateNext();
            panel.Children.Add(nextButton);
        }
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, Child = panel };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        _messages.Children.Add(card);
        ScrollToEnd();
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

    /// <summary>2026-10-23: вопрос на чтение — «?»/вопросительное слово/«посоветуй» и нет глагола-приказа (спиши, поставь, измени…).</summary>
    private static bool IsReadOnlyQuestion(string question)
    {
        var q = (question ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0 || ProductActionPlan.LooksLikeAction(q))
            return false;
        string[] starts = { "какие", "какой", "какая", "какое", "что ", "как ", "почему", "сколько", "где ", "кто ", "когда", "зачем", "посоветуй", "подскажи", "проанализ", "анализ", "стоит ли", "можно ли", "кандай", "эмне", "канча" };
        return q.Contains('?') || starts.Any(q.StartsWith);
    }

    private Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)>? _summaryTask;
    private DateTime _summaryAtUtc = DateTime.MinValue;
    private AiAccess? _summaryAccess;

    /// <summary>2026-10-05, владелец: «голос очень сильно тормозит». Сводка магазина для ИИ — одна задача на минуту:
    /// запускается заранее (при открытии раздела и как только нажали микрофон — пока владелец говорит), части
    /// собираются одновременно, а не по очереди.
    /// 2026-10-21 (О1): сводка — частями по важности (AiSection), а не одной строкой; без прав — без этих частей (О4).</summary>
    private Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> GetSummaryAsync()
    {
        var access = CurrentAccess();
        if (_summaryTask is { IsFaulted: false, IsCanceled: false } && DateTime.UtcNow - _summaryAtUtc < TimeSpan.FromMinutes(1) && Equals(_summaryAccess, access))
            return _summaryTask;
        _summaryAtUtc = DateTime.UtcNow;
        _summaryAccess = access;
        return _summaryTask = BuildSummaryAsync(access);
    }

    private static async Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> BuildSummaryAsync(AiAccess access)
    {
        var ct = CancellationToken.None;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 2026-10-21 (И5): сравнение периодов считает программа (тот же отчёт сервера, что «Сводка»).
        var periods = OwnerPeriodStats.GetAsync(ct);
        // «Я должен кому-то или мне должны?» — итоги долгов клиентов (точно, как «Сводка», О2) и поставщиков.
        var debts = OwnerAiContext.DebtTotalsSectionAsync(ct);
        // «Дай список клиентов-должников»: должники под кодами [Д1]… — только с правом «Клиенты» (О4).
        var debtors = access.Clients ? OwnerAiContext.DebtorsSectionAsync(ct) : Task.FromResult<(AiSection?, IReadOnlyDictionary<int, string>)>((null, new Dictionary<int, string>()));
        // 2026-10-05, владелец: «дай доступ к ABC-анализу ИИ».
        var abc = OwnerAiContext.AbcSectionAsync(ct);
        // 2026-10-05, владелец: «анализ продаж, склада, клиентов, заказов — чтобы предлагать акции на проблемные товары».
        var analysis = access.Products ? OwnerAiContext.AnalysisSectionsAsync(ct) : Task.FromResult(new List<AiSection>());
        // 2026-10-05, владелец: «добавь возможность ИИ управлять ботом» — состояние функций бота на сервере.
        var bot = access.Settings ? BuildBotStateAsync(ct) : Task.FromResult("");
        // 2026-10-05, ТЗ часть 7: итоги допродажи по всем кассам компании (сервер выложил 05.10).
        var upsell = BuildUpsellAsync(ct);
        // 2026-10-06, лог владельца (23:10, сервер не отвечал после запуска): одна упавшая часть роняла всю сводку — ИИ
        // оставался без данных магазина. Теперь упавшая часть пропускается, остальные идут в сводку.
        static async Task<T?> Part<T>(Task<T> task, string name) where T : class
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: часть сводки «{name}» не собрана ({ex.Message}).", "WARNING");
                return null;
            }
        }
        var list = new List<AiSection>();
        // Цифры «Сводки» (выбранный там период, выручка по дням, лучшие товары) — всегда.
        if (OwnerOverviewSnapshot.Text is { Length: > 0 } overview)
            list.Add(new AiSection("overview", "Сводка программы", 0, overview, SourceSection: "overview", SourceLabel: "Сводка (сервер NurCRM)"));
        if (await Part(periods, "сравнение периодов").ConfigureAwait(false) is { } snap)
            list.Add(new AiSection("periods", "Сравнение периодов", 0, snap.ToAiText(), SourceSection: "overview",
                SourceLabel: $"Сводка (сервер NurCRM), периоды на {snap.BuiltAtLocal:dd.MM HH:mm}"));
        if (await Part(debts, "долги").ConfigureAwait(false) is { } d)
            list.Add(d);
        if (access.Products)
        {
            list.Add(OwnerAiContext.WarehouseTotalsSection());
            list.Add(OwnerAiContext.WarehouseListSection());
        }
        if (await Part(abc, "ABC").ConfigureAwait(false) is { } a)
            list.Add(a);
        if (await Part(analysis, "анализ").ConfigureAwait(false) is { } an)
            list.AddRange(an);
        IReadOnlyDictionary<int, string> names = new Dictionary<int, string>();
        try
        {
            var (section, n) = await debtors.ConfigureAwait(false);
            if (section is not null)
                list.Add(section);
            names = n;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: часть сводки «должники» не собрана ({ex.Message}).", "WARNING");
        }
        if (await Part(bot, "бот").ConfigureAwait(false) is { Length: > 0 } b)
            list.Add(new AiSection("bot", "Телеграм-бот", 9, b, SourceSection: "telegrambot", SourceLabel: "Телеграм-бот"));
        if (await Part(upsell, "допродажа").ConfigureAwait(false) is { Length: > 0 } u)
            list.Add(new AiSection("upsell", "Допродажа", 9, u));
        PosLogger.Log($"ИИ-советник: сводка по частям ({list.Count}) за {watch.ElapsedMilliseconds} мс.", "INFO");
        return (list, names);
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
    // 2026-10-11, тестировщик (иишка.md, 1): несколько фото/файлов (страниц накладной) — по порядку, каждую можно убрать.
    private readonly List<(byte[] Data, string Mime, string Name)> _attachments = new();
    private const int MaxAttachments = 10;

    private async Task PickPhotoAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = T("Фото накладной или товара", "Накладнойдун же товардын сүрөтү", "Invoice or product photo", "Fatura veya ürün fotoğrafı", "Yuk xati yoki mahsulot surati"),
                AllowMultiple = true,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType(T("Фото и PDF", "Сүрөт жана PDF", "Photos and PDF", "Fotoğraf ve PDF", "Rasm va PDF"))
                    { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.pdf" } } },
            }).ConfigureAwait(true);
            foreach (var file in files)
            {
                if (_attachments.Count >= MaxAttachments)
                {
                    AddBubble(T($"Не больше {MaxAttachments} страниц за раз.", $"Бир жолу {MaxAttachments} барактан ашпайт.", $"No more than {MaxAttachments} pages at a time.",
                        $"Tek seferde en fazla {MaxAttachments} sayfa.", $"Bir martada {MaxAttachments} sahifadan ko'p emas."), fromOwner: false);
                    break;
                }
                await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(true);
                if (ms.Length > 15_000_000 || _attachments.Sum(a => (long)a.Data.Length) + ms.Length > 40_000_000)
                {
                    AddBubble(T($"«{file.Name}» слишком большой (больше 15 МБ, или всего больше 40 МБ) — сожмите или сфотографируйте заново.",
                        $"«{file.Name}» өтө чоң (15 МБдан же жалпы 40 МБдан ашык) — кичирейтиңиз же кайра тартыңыз.",
                        $"“{file.Name}” is too big (over 15 MB, or over 40 MB in total) — shrink it or retake it.",
                        $"«{file.Name}» çok büyük (15 MB'tan veya toplamda 40 MB'tan fazla) — küçültün veya yeniden çekin.",
                        $"«{file.Name}» juda katta (15 MB dan yoki jami 40 MB dan ortiq) — kichraytiring yoki qayta oling."), fromOwner: false);
                    continue;
                }
                var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                _attachments.Add((ms.ToArray(), ext switch { ".png" => "image/png", ".webp" => "image/webp", ".pdf" => "application/pdf", _ => "image/jpeg" }, file.Name));
            }
            RenderAttachments();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото не прикреплено ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Вложения над строкой ввода: миниатюра и «стр. N» у каждого, ✕ — убрать страницу, «＋ Ещё страница».</summary>
    private void RenderAttachments()
    {
        if (_attachRow is null)
            return;
        _attachRow.Children.Clear();
        if (_attachments.Count == 0)
        {
            _attachRow.IsVisible = false;
            return;
        }
        for (var n = 0; n < _attachments.Count; n++)
        {
            var (data, mime, name) = _attachments[n];
            Control thumb;
            if (mime.StartsWith("image/", StringComparison.Ordinal))
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    thumb = new Image { Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 96), Stretch = Stretch.UniformToFill };
                }
                catch
                {
                    thumb = new TextBlock { Text = "🖼", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
            else
                thumb = new TextBlock { Text = "📄", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var page = new TextBlock { Text = T($"стр. {n + 1}", $"{n + 1}-бет", $"p. {n + 1}", $"s. {n + 1}", $"{n + 1}-bet"), FontSize = 11 };
            Use(page, TextBlock.ForegroundProperty, "BrushTextSoft");
            var index = n;
            var remove = UiKit.Ghost(this, "✕");
            remove.Height = 22;
            remove.FontSize = 11;
            remove.Padding = new Thickness(6, 0);
            remove.Click += (_, _) =>
            {
                if (index < _attachments.Count)
                    _attachments.RemoveAt(index);
                RenderAttachments();
            };
            ToolTip.SetTip(remove, name);
            _attachRow.Children.Add(new StackPanel
            {
                Spacing = 2, Margin = new Thickness(0, 0, 6, 0),
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Width = 48, Height = 48, Child = thumb },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { page, remove } },
                },
            });
        }
        if (_attachments.Count < MaxAttachments)
        {
            var more = UiKit.Ghost(this, T("＋ Ещё страница", "＋ Дагы барак", "＋ Another page", "＋ Bir sayfa daha", "＋ Yana sahifa"));
            more.Height = 32;
            more.Click += async (_, _) => await PickPhotoAsync().ConfigureAwait(true);
            _attachRow.Children.Add(more);
        }
        var label = new TextBlock
        {
            Text = T($"Напишите, что сделать (или просто отправьте: накладная, наценка не меньше {ProductActionPlan.MinMarkupPercent:0} %)",
                $"Эмне кылууну жазыңыз (же жөн эле жөнөтүңүз: накладная, үстөк {ProductActionPlan.MinMarkupPercent:0} %дан кем эмес)",
                $"Type what to do (or just send: invoice, markup at least {ProductActionPlan.MinMarkupPercent:0}%)",
                $"Ne yapılacağını yazın (veya sadece gönderin: fatura, kâr payı en az %{ProductActionPlan.MinMarkupPercent:0})",
                $"Nima qilishni yozing (yoki shunchaki yuboring: yuk xati, ustama kamida {ProductActionPlan.MinMarkupPercent:0} %)"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(6, 0, 0, 0),
        };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        _attachRow.Children.Add(label);
        _attachRow.IsVisible = true;
        _input.Focus();
    }

    /// <summary>Одно вложение (например, снимок с камеры) — как раньше; добавляется к уже прикреплённым страницам.</summary>
    private void SetAttachment(byte[] data, string mime, string name)
    {
        if (_attachments.Count < MaxAttachments)
            _attachments.Add((data, mime, name));
        RenderAttachments();
    }

    private void ClearAttachment()
    {
        _attachments.Clear();
        RenderAttachments();
    }

    /// <summary>Подготовленные изменения, которые ждут «Выполнить» (или голосового «да, выполни» в звонке).</summary>
    private Func<Task<string>>? _pendingProductApply;
    private Action? _pendingProductCancel;
    // 2026-10-06: «поставь наценку 25 %» даёт новую карточку — прежняя гаснет, иначе два «Выполнить» создали бы товары дважды.
    private Action? _pendingProductSupersede;
    // 2026-10-19: поставщик, которого ИИ прочитал в последней накладной (ProductActionPlan.SupplierHint), — подсказка окну закупки.
    private (string? Name, string? Inn) _invoiceSupplierHint;

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». «Открыть» — сразу, остальное — карточкой с подтверждением.</summary>
    /// <summary>2026-10-07, владелец: «открывай вкладки только после команды открыть что-то из программы». Явное «открой / перейди /
    /// зайди / ач / кир» во фразе владельца.</summary>
    private static bool HasOpenCommand(string text) =>
        System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}']+")
            .Any(w => new[] { "откр", "перей", "зайди", "зайти", "ачып", "ачкыла", "аччы", "кирип", "open", "aç", "och" }.Any(w.StartsWith) || w is "ач" or "кир");

    private void ShowProductSteps(List<ProductActionPlan.Step> steps, string request)
    {
        // Открыть раздел или товар — только если владелец сам сказал «открой» (ИИ по своей воле вкладки не переключает).
        foreach (var open in steps.Where(st => st.Op is "open" or "open_section" && HasOpenCommand(request)).Take(1))
            _ = ProductActionPlan.ExecuteAsync(open, T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"));
        // 2026-10-06: «найди фото» от ИИ — не ставим первое найденное, а показываем варианты на выбор.
        var photos = steps.Where(st => st.Op == "photo").Select(st => st.Product).DistinctBy(p => p.Id).Take(3).ToList();
        if (photos.Count > 0)
            _ = ShowPhotoChoicesAsync(photos);
        var changes = steps.Where(st => st.Op is not ("open" or "open_section" or "photo")).ToList();
        if (changes.Count > 0)
            AddProductActionsCard(changes, request);
    }

    private static bool LooksLikeInvoiceRequest(string request)
    {
        var text = (request ?? "").ToLowerInvariant();
        return new[] { "накладн", "накладную", "счёт-фактур", "счет-фактур", "invoice", "fatura", "yuk xati", "накладнойдун" }
            .Any(text.Contains);
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
    private int _scanFocusCaret;
    private const int ScanInterkeyMs = 40;

    // 2026-10-15: прежде с 3-го быстрого символа нажатия глушились (e.Handled) в расчёте на Enter сканера. Без Enter символы
    // пропадали: автоповтор клавиши (~33 мс) превращал «100000» в «10», быстрый набор терял буквы; Shift сбрасывал буфер (буквы
    // сканера в верхнем регистре терялись), суффикс Tab не принимался. Теперь символы НЕ глушатся — печатаются, куда стоит курсор;
    // только когда быстрая серия закончилась Enter или Tab, это скан: поле с курсором возвращается к тексту до серии, а код уходит
    // в поле штрихкода. Серия из одной и той же клавиши (автоповтор) сканом не считается; Shift буфер не сбрасывает.
    private void ScanKeyDown(object? sender, KeyEventArgs e)
    {
        if (_scanTargets is not { Count: > 0 } targets || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox fb && targets.Contains(fb))
            return; // курсор в поле штрихкода — скан печатается прямо туда, Enter — к следующему полю
        if (e.Key is Key.LeftShift or Key.RightShift or Key.CapsLock)
            return; // 2026-10-15: Shift перед заглавной буквой сканера — часть скана, буфер не сбрасываем
        var now = Environment.TickCount64;
        var fast = _scanBuf.Length > 0 && now - _scanLastTick is >= 0 and <= ScanInterkeyMs;
        if (e.Key is Key.Enter or Key.Tab)
        {
            var code = _scanBuf;
            var isScan = fast && code.Length >= 4 && _scanFastRun >= code.Length - 1 && code.Distinct().Count() > 1;
            var leaked = _scanFocusBox;
            var before = _scanFocusText;
            var caret = _scanFocusCaret;
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = null;
            if (!isScan)
                return;
            e.Handled = true;
            if (leaked is not null && ReferenceEquals(leaked, focused))
            {
                // 2026-10-15: символы скана напечатались в поле с курсором (вопрос и т. п.) — вернуть его текст как был до скана.
                leaked.Text = before;
                leaked.CaretIndex = Math.Min(caret, (before ?? "").Length);
            }
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
            // 2026-10-15: буква — с регистром, как её прислал сканер (KeySymbol).
            // 2026-10-16: KeySymbol идёт по текущей раскладке: при русской (или турецкой) Code128 «AB12345» приходил как «фи12345»,
            // товар по штрихкоду не находился и создавался новый с мусорным штрихкодом. KeySymbol берём, только если это латинская
            // буква; иначе буква — по самой клавише (e.Key не зависит от раскладки), регистр — по Shift и CapsLock.
            >= Key.A and <= Key.Z => e.KeySymbol is { Length: 1 } sym && char.IsAsciiLetter(sym[0])
                ? sym[0]
                : (char)((((e.KeyModifiers & KeyModifiers.Shift) != 0) ^ (OperatingSystem.IsWindows() && Console.CapsLock) ? 'A' : 'a') + (e.Key - Key.A)),
            Key.OemMinus or Key.Subtract => '-',
            _ => null,
        };
        if (ch is null)
        {
            _scanBuf = "";
            _scanFastRun = 0;
            _scanFocusBox = null;
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
            _scanFocusCaret = _scanFocusBox?.CaretIndex ?? 0;
        }
        _scanBuf += ch;
        _scanLastTick = now;
    }

    /// <summary>2026-10-24: строка блока «Штрихкоды товаров» карточки накладной — одна позиция накладной со своим полем.</summary>
    private sealed class BarcodeRow
    {
        public int Index;
        public ProductActionPlan.Step Step = null!;
        public TextBox Box = null!;
        public Border Host = null!;
        public TextBlock Note = null!, Num = null!, Label = null!;
        public Grid Grid = null!;
        public bool Editable;
        public bool DupMarked;
        public string Initial = "";
        public string? LastCode;
        public NurMarketKassa.Models.Pos.CatalogProductTileVm? Existing;
        public string Code => (Box.Text ?? "").Trim();
    }

    /// <summary>Раскладка строки: широкая — «№ · название · кол-во» слева, поле справа, пометка под ними; узкая (&lt; 700 px) — подпись
    /// над полем, поле на всю ширину.</summary>
    private static void PlaceBarcodeRow(BarcodeRow r, bool narrow)
    {
        Grid.SetRow(r.Num, 0); Grid.SetColumn(r.Num, 0);
        if (narrow)
        {
            Grid.SetRow(r.Label, 0); Grid.SetColumn(r.Label, 1); Grid.SetColumnSpan(r.Label, 2);
            Grid.SetRow(r.Host, 1); Grid.SetColumn(r.Host, 0); Grid.SetColumnSpan(r.Host, 3);
            Grid.SetRow(r.Note, 2); Grid.SetColumn(r.Note, 0); Grid.SetColumnSpan(r.Note, 3);
        }
        else
        {
            Grid.SetRow(r.Label, 0); Grid.SetColumn(r.Label, 1); Grid.SetColumnSpan(r.Label, 1);
            Grid.SetRow(r.Host, 0); Grid.SetColumn(r.Host, 2); Grid.SetColumnSpan(r.Host, 1);
            Grid.SetRow(r.Note, 1); Grid.SetColumn(r.Note, 1); Grid.SetColumnSpan(r.Note, 2);
        }
    }

    /// <summary>2026-10-06: предложенные ИИ действия с товарами — карточкой; выполняются только по «Выполнить» (ProductActionPlan).</summary>
    /// <param name="proposedUtc">2026-10-16: когда предложена исходная карточка (у отредактированной — время исходной): карточка,
    /// предложенная раньше выполнения такой же (в боте или здесь), не выполняется повторно (ProductActionPlan.ExecuteAllAsync).</param>
    private void AddProductActionsCard(List<ProductActionPlan.Step> steps, string request, DateTime? proposedUtc = null)
    {
        var proposed = proposedUtc ?? DateTime.UtcNow;
        var invoice = LooksLikeInvoiceRequest(request);
        // 2026-10-19, владелец: «при скане накладной добавляет товар на склад напрямую, а не через закупку». Приход и новые товары
        // с количеством — закупка у поставщика: «Провести закупку…» открывает окно закупки (InvoiceEditorWindow), черновик живёт
        // в карточке (повтор после обрыва — те же строки и тот же ключ идемпотентности).
        var purchase = steps.Any(ProductActionPlan.IsPurchaseStep);
        var supplierHint = _invoiceSupplierHint;
        InvoiceEditorWindow.PurchaseDraft? draft = null;
        var editorOpen = false;
        var title = new TextBlock
        {
            Text = invoice
                ? T("Накладная — проверьте строки и итоги", "Накладная — саптарды жана жыйынтыкты текшериңиз", "Invoice — review items and totals", "Fatura — satırları ve toplamları kontrol edin", "Yuk xati — qatorlar va jami summani tekshiring")
                : T("Изменить товары?", "Товарларды өзгөртөлүбү?", "Change the products?", "Ürünler değiştirilsin mi?", "Mahsulotlarni o'zgartiraymi?"),
            FontWeight = FontWeight.Bold,
            FontSize = 14.5,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-06, владелец: «при загрузке товара спрашивать штрихкод через сканер». У нового товара без штрихкода — поле
        // «отсканируйте»: сканер пишет в поле и жмёт Enter — курсор переходит к следующему. Штрихкод уже есть у товара в
        // каталоге — это не новый товар, а приход к нему (дубль не создаётся).
        var body = new StackPanel { Spacing = 4 };
        var barcodeBoxes = new Dictionary<int, (TextBox Box, TextBlock Note)>();
        // 2026-10-10: список новых товаров и приходов показываем таблицей, даже если формулировка запроса не распознана как накладная.
        var showProductTable = invoice || steps.Any(step => step.Op is "create" or "receive");
        if (showProductTable)
        {
            // 2026-10-21 (О10): заголовки накладной — на языке программы (были только по-русски).
            var header = invoice
                ? new[]
                {
                    T("№", "№", "No.", "No.", "№"),
                    T("Наименование товара", "Товардын аталышы", "Product name", "Ürün adı", "Mahsulot nomi"),
                    T("Штрихкод / Артикул", "Штрихкод / Артикул", "Barcode / SKU", "Barkod / Stok kodu", "Shtrix-kod / Artikul"),
                    T("Ед. изм.", "Өлчөм бирд.", "Unit", "Birim", "O'lchov bir."),
                    T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
                    T("Цена закупа", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
                    T("Розничная цена", "Чекене баа", "Retail price", "Perakende fiyatı", "Chakana narx"),
                    T("Сумма (закуп)", "Сумма (сатып алуу)", "Total (purchase)", "Tutar (alış)", "Summa (xarid)"),
                }
                : new[]
                {
                    T("№", "№", "No.", "No.", "№"),
                    T("Наименование товара", "Товардын аталышы", "Product name", "Ürün adı", "Mahsulot nomi"),
                    T("Действие", "Аракет", "Action", "İşlem", "Amal"),
                    T("Ед. изм.", "Өлчөм бирд.", "Unit", "Birim", "O'lchov bir."),
                    T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
                    T("Цена закупа", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
                    T("Розничная цена", "Чекене баа", "Retail price", "Perakende fiyatı", "Chakana narx"),
                    T("Штрихкод / Артикул", "Штрихкод / Артикул", "Barcode / SKU", "Barkod / Stok kodu", "Shtrix-kod / Artikul"),
                    T("Наценка", "Үстөк", "Markup", "Kâr payı", "Ustama"),
                };
            var rows = steps.Select((step, index) =>
            {
                var unit = string.IsNullOrWhiteSpace(step.Unit)
                    ? (step.Product.MustWeigh ? "кг" : "шт")
                    : step.Unit!;
                var quantity = step.Qty is { } q ? q.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var purchase = step.Purchase is { } cost ? cost.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var retailValue = step.Price ?? (step.Product.PriceValue > 0 ? step.Product.PriceValue : null);
                var retail = retailValue is { } price ? price.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "-";
                var lineTotal = step.Qty is { } qty && step.Purchase is { } unitCost
                    ? (qty * unitCost).ToString("N2", System.Globalization.CultureInfo.CurrentCulture)
                    : "-";
                var barcode = !string.IsNullOrWhiteSpace(step.Barcode) ? step.Barcode : step.Product.Barcode;
                if (invoice)
                {
                    return new[]
                    {
                        (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), step.Product.Title,
                        string.IsNullOrWhiteSpace(barcode) ? "-" : barcode, unit, quantity, purchase, retail, lineTotal,
                    };
                }
                var markup = step.RaisedToMin
                    ? $"мин. {ProductActionPlan.MinMarkupPercent:0.#}%"
                    : step.Purchase is > 0 && retailValue is { } retailPrice
                        ? $"{((retailPrice / step.Purchase.Value - 1) * 100):0.#}%"
                        : "-";
                var operation = step.Op == "create"
                    ? T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot")
                    : step.Op == "receive"
                        ? T("Приход", "Киреше", "Stock receipt", "Stok girişi", "Kirim")
                        : ProductActionPlan.Describe(step);
                return new[]
                {
                    (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), step.Product.Title, operation, unit,
                    quantity, purchase, retail, string.IsNullOrWhiteSpace(barcode) ? "-" : barcode, markup,
                };
            }).ToList();
            body.Children.Add(BuildTable(header, rows));
            // 2026-10-15: в таблице у такой строки — текущая цена; почему цена накладной не поставлена — пометкой под таблицей.
            var priceNotes = steps.Select(step => ProductActionPlan.PriceNotLoweredNote(step) is { } n ? $"• «{step.Product.Title}»: {n}" : null)
                .Where(n => n is not null).ToList();
            if (priceNotes.Count > 0)
            {
                var priceNote = new TextBlock { Text = string.Join("\n", priceNotes), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 2, 2, 4) };
                Use(priceNote, TextBlock.ForegroundProperty, "BrushWarning");
                body.Children.Add(priceNote);
            }

        }
        if (invoice)
        {
            var totalNames = steps.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            var unitTotals = steps
                .Where(step => step.Qty is not null)
                .GroupBy(step => string.IsNullOrWhiteSpace(step.Unit) ? (step.Product.MustWeigh ? "кг" : "шт") : step.Unit!)
                .Select(group => $"{group.Sum(step => step.Qty!.Value).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} {group.Key}")
                .ToList();
            var totalQuantity = unitTotals.Count == 0 ? "-" : string.Join("; ", unitTotals);
            var totalPurchase = steps.All(step => step.Qty is not null && step.Purchase is not null)
                ? steps.Sum(step => step.Qty!.Value * step.Purchase!.Value).ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " " + T("сом", "сом", "som", "som", "so'm")
                : "-";
            var totals = new TextBlock
            {
                Text = T($"Всего наименований: {totalNames}\nОбщее количество единиц: {totalQuantity}\nОбщая сумма закупа: {totalPurchase}",
                    $"Бардык аталыштар: {totalNames}\nБирдиктердин жалпы саны: {totalQuantity}\nСатып алуунун жалпы суммасы: {totalPurchase}",
                    $"Items: {totalNames}\nTotal units: {totalQuantity}\nTotal purchase amount: {totalPurchase}",
                    $"Kalem sayısı: {totalNames}\nToplam birim: {totalQuantity}\nToplam alış tutarı: {totalPurchase}",
                    $"Jami nomlar: {totalNames}\nJami birliklar: {totalQuantity}\nJami xarid summasi: {totalPurchase}"),
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(2, 4, 2, 6),
            };
            Use(totals, TextBlock.ForegroundProperty, "BrushText");
            body.Children.Add(totals);
        }
        if (purchase)
        {
            var purchaseNote = new TextBlock
            {
                Text = T("Накладная проводится закупкой у поставщика (как «Приёмка» склада): нажмите «Провести закупку…», выберите поставщика и оплату.",
                    "Накладной жеткирүүчүдөн сатып алуу менен өткөрүлөт (кампанын «Кабыл алуусу» сыяктуу): «Сатып алууну өткөрүү…» баскычын басып, жеткирүүчүнү жана төлөмдү тандаңыз.",
                    "The invoice is posted as a purchase from a supplier (like warehouse “Receiving”): press “Post purchase…”, choose the supplier and payment.",
                    "Fatura tedarikçiden alım olarak işlenir (depodaki «Mal kabul» gibi): «Alımı işle…»ye basın, tedarikçiyi ve ödemeyi seçin.",
                    "Yuk xati yetkazib beruvchidan xarid sifatida o'tkaziladi (ombordagi «Qabul» kabi): «Xaridni o'tkazish…»ni bosing, yetkazib beruvchi va to'lovni tanlang."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 4),
            };
            Use(purchaseNote, TextBlock.ForegroundProperty, "BrushTextSoft");
            body.Children.Add(purchaseNote);
            // 2026-10-19, ревью: часть строк этой накладной уже проведена (прежней карточкой) — показать их; в окне закупки они
            // помечены «уже проведено» и без второго подтверждения не проводятся.
            var alreadyPosted = steps.Where(ProductActionPlan.IsPurchaseStep)
                .Select(st => (st, hit: InvoiceEditorWindow.PostedLines.Find(null, st.Op == "receive" ? st.Product.Id : null, new[] { st.Product.Title }, st.Qty ?? 0, st.Purchase ?? 0)))
                .Where(x => x.hit is not null)
                .Select(x => $"«{x.st.Product.Title}» {(x.st.Qty ?? 0).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} — {x.hit!.At:HH:mm}")
                .ToList();
            if (alreadyPosted.Count > 0)
            {
                var postedNote = new TextBlock
                {
                    Text = "⚠ " + T("Уже проведено закупкой: ", "Сатып алуу менен өткөрүлгөн: ", "Already posted as a purchase: ", "Alım olarak zaten işlendi: ", "Xarid sifatida o'tkazilgan: ")
                           + string.Join("; ", alreadyPosted),
                    FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 4),
                };
                Use(postedNote, TextBlock.ForegroundProperty, "BrushWarning");
                body.Children.Add(postedNote);
            }
        }
        // Только точное совпадение штрихкода (Find ищет и по названию — недописанный код не должен «найти» чужой товар).
        static NurMarketKassa.Models.Pos.CatalogProductTileVm? ByBarcode(string code) =>
            code.Length >= 4 && ProductActions.Find(code) is { } p && string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase) ? p : null;
        // 2026-10-15: новый товар по скану оказался товаром склада — приход к нему; цена нового товара (по наценке) ниже текущей
        // сама не ставится, как и у прихода из разбора (ProductActionPlan.KeepCurrentPriceIfLower).
        static ProductActionPlan.Step AsReceive(ProductActionPlan.Step st, NurMarketKassa.Models.Pos.CatalogProductTileVm existing) =>
            ProductActionPlan.KeepCurrentPriceIfLower(st.Price, existing.PriceValue) is var (price, notLowered)
                ? new ProductActionPlan.Step("receive", existing, st.Qty, null, null, st.Purchase, price, ExpectedStock: existing.Quantity, PriceNotLowered: notLowered)
                : st;
        // 2026-10-24, владелец: «внизу … поле для ввода штрих кода (столько поле для ввода, сколько товара)», «разделить каждому
        // товару своё поле». Прежде поля были безымянными, только у новых товаров без кода. Теперь — блок «Штрихкоды товаров»:
        // строка на КАЖДУЮ позицию накладной (приход или новый товар) «№ · название · кол-во» со своим полем; известный код
        // подставлен (у товара склада — серый, не правится); счётчик «введено N из M»; нехватка кодов проведению не мешает,
        // повтор одного кода у двух строк — мешает. Код сохраняется существующими путями: новый товар — закупкой (Barcode шага),
        // приход к товару склада без кода — шагом set_barcode (ProductActions.UpdateFieldsAsync) после закупки.
        var barcodeRows = new List<BarcodeRow>();
        var dupRows = new HashSet<int>();
        var dupShown = false;
        Action? afterRefresh = null;
        TextBlock? counterText = null, statusText = null, dupText = null;
        Border? blockHost = null;
        string ApplyName() => purchase
            ? T("Провести закупку…", "Сатып алууну өткөрүү…", "Post purchase…", "Alımı işle…", "Xaridni o'tkazish…")
            : T("Применить изменения", "Өзгөртүүлөрдү колдонуу", "Apply changes", "Değişiklikleri uygula", "O'zgarishlarni qo'llash");
        void SetNote(BarcodeRow r, string text, string brush)
        {
            r.Note.Text = text;
            r.Note.IsVisible = text.Length > 0;
            if (text.Length > 0)
                Use(r.Note, TextBlock.ForegroundProperty, brush);
        }
        void RefreshBarcodes()
        {
            foreach (var r in barcodeRows)
            {
                var c = r.Code;
                if (!string.Equals(c, r.LastCode, StringComparison.Ordinal))
                {
                    r.LastCode = c;
                    r.Existing = r.Editable ? ByBarcode(c) : null;
                }
            }
            // Повтор кода: тот же код (от 4 знаков) у двух строк, если хоть одна из них правится. Один и тот же товар склада
            // дважды в накладной (серые строки с одним кодом) — не повтор, приход к нему сольётся в одну строку.
            dupRows.Clear();
            foreach (var g in barcodeRows.Where(r => r.Code.Length >= 4).GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase))
                if (g.Count() > 1 && g.Any(r => r.Editable))
                    foreach (var r in g)
                        dupRows.Add(r.Index);
            foreach (var r in barcodeRows)
            {
                var code = r.Code;
                var dup = dupRows.Contains(r.Index);
                if (dup != r.DupMarked)
                {
                    r.DupMarked = dup;
                    if (dup)
                        Use(r.Host, Border.BorderBrushProperty, "BrushDanger");
                    else
                        r.Host.ClearValue(Border.BorderBrushProperty);
                }
                if (dup)
                {
                    var other = barcodeRows.First(o => o.Index != r.Index && string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)).Index + 1;
                    SetNote(r, T($"этот штрихкод уже у строки {other}", $"бул штрихкод {other}-сапта бар", $"this barcode is already on line {other}",
                        $"bu barkod zaten {other}. satırda", $"bu shtrix-kod {other}-qatorda allaqachon bor"), "BrushDanger");
                }
                else if (!r.Editable)
                    SetNote(r, T("уже у товара", "товарда бар", "already on the product", "üründe zaten var", "mahsulotda allaqachon bor"), "BrushTextSoft");
                else if (code.Length == 0)
                    SetNote(r, "", "BrushTextSoft");
                else if (code.Length < 4)
                    SetNote(r, T("слишком короткий — проверьте", "өтө кыска — текшериңиз", "too short — please check", "çok kısa — kontrol edin", "juda qisqa — tekshiring"), "BrushWarning");
                else if (r.Existing is { } existing && r.Step.Op == "create")
                {
                    var text = T($"Этот штрихкод уже у товара «{existing.Title}» — будет приход к нему, новый товар не создаётся.",
                        $"Бул штрихкод «{existing.Title}» товарында бар — ага кириш болот, жаңы товар түзүлбөйт.",
                        $"This barcode already belongs to “{existing.Title}” — it will be received there, no new product.",
                        $"Bu barkod zaten «{existing.Title}» ürününde — ona giriş yapılır, yeni ürün oluşturulmaz.",
                        $"Bu shtrix-kod «{existing.Title}» mahsulotida bor — unga kirim bo'ladi, yangi mahsulot yaratilmaydi.");
                    // 2026-10-15: цена склада не снижается приходом — сказать об этом здесь же.
                    if (ProductActionPlan.PriceNotLoweredNote(AsReceive(r.Step, existing)) is { } priceNote)
                        text += " " + priceNote;
                    SetNote(r, text, "BrushWarning");
                }
                else if (r.Existing is { } owner && !string.Equals(owner.Id, r.Step.Product.Id, StringComparison.OrdinalIgnoreCase))
                    SetNote(r, T($"этот штрихкод уже у товара «{owner.Title}» — для этой строки он не будет поставлен",
                        $"бул штрихкод «{owner.Title}» товарында бар — бул сап үчүн коюлбайт",
                        $"this barcode already belongs to “{owner.Title}” — it will not be set for this line",
                        $"bu barkod zaten «{owner.Title}» ürününde — bu satır için atanmaz",
                        $"bu shtrix-kod «{owner.Title}» mahsulotida bor — bu qator uchun qo'yilmaydi"), "BrushWarning");
                else if (r.Initial.Length > 0 && string.Equals(code, r.Initial, StringComparison.Ordinal))
                    SetNote(r, T("прочитано с накладной — сверьте", "накладнойдон окулду — салыштырыңыз", "read from the invoice — please check",
                        "faturadan okundu — kontrol edin", "yuk xatidan o'qildi — tekshiring"), "BrushTextSoft");
                else if (r.Step.Op == "create")
                    SetNote(r, T("✓ новый товар", "✓ жаңы товар", "✓ new product", "✓ yeni ürün", "✓ yangi mahsulot"), "BrushSuccess");
                else
                    SetNote(r, T("после проведения штрихкод будет поставлен товару", "өткөргөндөн кийин штрихкод товарга коюлат",
                        "after posting, the barcode is set on the product", "işlemden sonra barkod ürüne atanır", "o'tkazilgandan keyin shtrix-kod mahsulotga qo'yiladi"), "BrushTextSoft");
            }
            var editable = barcodeRows.Where(r => r.Editable).ToList();
            var filled = editable.Count(r => r.Code.Length > 0);
            var missing = editable.Count - filled;
            if (counterText is not null)
            {
                counterText.IsVisible = editable.Count > 0;
                counterText.Text = T($"введено {filled} из {editable.Count}", $"киргизилди {filled} / {editable.Count}", $"entered {filled} of {editable.Count}",
                    $"girilen {filled} / {editable.Count}", $"kiritildi {filled} / {editable.Count}");
            }
            if (statusText is not null)
            {
                statusText.IsVisible = editable.Count > 0 && (missing > 0 || dupRows.Count == 0); // при повторе кода вместо «все введены» — красная строка ниже
                if (missing > 0)
                {
                    statusText.Text = T($"⚠ Не хватает штрихкодов: {missing}. Можно провести и без них — товар создастся без штрихкода, поставить его можно позже в карточке товара.",
                        $"⚠ Штрихкод жетишпейт: {missing}. Алар жок да өткөрсө болот — товар штрихкодсуз түзүлөт, аны кийин товардын карточкасында коюуга болот.",
                        $"⚠ Barcodes missing: {missing}. You can still post without them — the product is created without a barcode; you can add it later in the product card.",
                        $"⚠ Eksik barkod: {missing}. Onlarsız da işleyebilirsiniz — ürün barkodsuz oluşturulur, barkodu sonra ürün kartından ekleyebilirsiniz.",
                        $"⚠ Shtrix-kod yetishmayapti: {missing}. Ularsiz ham o'tkazish mumkin — mahsulot shtrix-kodsiz yaratiladi, uni keyin mahsulot kartasida qo'yish mumkin.");
                    Use(statusText, TextBlock.ForegroundProperty, "BrushWarning");
                }
                else
                {
                    var name = ApplyName();
                    statusText.Text = T($"Все штрихкоды введены — нажмите «{name}».", $"Бардык штрихкоддор киргизилди — «{name}» баскычын басыңыз.",
                        $"All barcodes entered — press “{name}”.", $"Tüm barkodlar girildi — «{name}» düğmesine basın.", $"Barcha shtrix-kodlar kiritildi — «{name}» tugmasini bosing.");
                    Use(statusText, TextBlock.ForegroundProperty, "BrushTextSoft");
                }
            }
            if (dupText is not null)
                dupText.IsVisible = dupRows.Count > 0;
            afterRefresh?.Invoke();
        }
        BarcodeRow AddBarcodeRow(int i, StackPanel host)
        {
            var step = steps[i];
            var unit = string.IsNullOrWhiteSpace(step.Unit) ? (step.Product.MustWeigh ? "кг" : "шт") : step.Unit!;
            var qtyText = step.Qty is { } q ? " · " + q.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) + " " + unit : "";
            var known = !string.IsNullOrWhiteSpace(step.Barcode) ? step.Barcode!.Trim() : (step.Product.Barcode ?? "").Trim();
            // Приход к товару склада с известным штрихкодом — серое поле «только чтение».
            var editable = !(step.Op == "receive" && known.Length > 0);
            var num = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(num, TextBlock.ForegroundProperty, "BrushTextSoft");
            var label = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            label.Inlines!.Add(new Avalonia.Controls.Documents.Run(step.Product.Title) { FontWeight = FontWeight.SemiBold });
            if (qtyText.Length > 0)
                label.Inlines.Add(new Avalonia.Controls.Documents.Run(qtyText));
            Use(label, TextBlock.ForegroundProperty, "BrushText");
            // 2026-10-24: подсказка короткая — поле строки 300 px, длинная обрезалась.
            var box = UiKit.Input(this, T("Штрихкод: скан или ввод", "Штрихкод: скан же жазуу", "Barcode: scan or type",
                "Barkod: okutun veya yazın", "Shtrix-kod: skaner yoki kiritish"), 36);
            box.Text = known;
            if (!editable)
            {
                box.IsReadOnly = true;
                box.Focusable = false;
                box.IsTabStop = false;
                box.IsHitTestVisible = false;
                Use(box, TextBox.ForegroundProperty, "BrushTextSoft");
            }
            var fieldHost = new Border { Child = box, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(11) };
            var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
            var row = new BarcodeRow { Index = i, Step = step, Box = box, Host = fieldHost, Note = note, Num = num, Label = label, Editable = editable, Initial = editable ? known : "" };
            row.Grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(30, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(300, GridUnitType.Pixel) },
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
                Children = { num, label, fieldHost, note },
            };
            PlaceBarcodeRow(row, false);
            var rowStack = new StackPanel { Spacing = 1, Children = { row.Grid } };
            host.Children.Add(rowStack);
            barcodeRows.Add(row);
            if (!editable)
                return row;
            box.TextChanged += (_, _) => RefreshBarcodes();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                // Следующее пустое правимое поле (серые пропускаются); все заполнены — фокус остаётся, счётчик это пишет.
                barcodeRows.Where(r => r.Editable && r.Index > i && string.IsNullOrWhiteSpace(r.Box.Text)).Select(r => r.Box).FirstOrDefault()?.Focus();
            };
            barcodeBoxes[i] = (box, note);
            // 2026-10-07, владелец: «если наименование есть — поставь штрихкод из barcode-list.ru, но дай проверить и сверить с товаром».
            // Под строкой — варианты из базы с их названием; нажатие вписывает штрихкод в поле (поле можно поправить до «Выполнить»).
            if (step.Op == "create" && known.Length == 0)
            {
                var suggest = new WrapPanel { Margin = new Thickness(30, 0, 0, 4), IsVisible = false };
                rowStack.Children.Add(suggest);
                var stepTitle = step.Product.Title;
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var found = await BarcodeListLookup.FindByNameAsync(stepTitle, CancellationToken.None).ConfigureAwait(true);
                    if (found.Count == 0 || !string.IsNullOrWhiteSpace(box.Text))
                        return;
                    var hint = new TextBlock
                    {
                        Text = T("Штрихкод из barcode-list.ru — сверьте с товаром:", "barcode-list.ru'дан штрихкод — товар менен салыштырыңыз:",
                            "Barcode from barcode-list.ru — check against the product:", "barcode-list.ru'dan barkod — ürünle karşılaştırın:",
                            "barcode-list.ru'dan shtrix-kod — mahsulot bilan solishtiring:"),
                        FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4),
                    };
                    Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
                    suggest.Children.Add(hint);
                    foreach (var s in found)
                    {
                        var pick = UiKit.Ghost(this, $"{s.Barcode} · {s.Name}");
                        pick.Height = 30;
                        pick.FontSize = 12;
                        pick.Padding = new Thickness(10, 0);
                        pick.Margin = new Thickness(0, 0, 6, 4);
                        pick.Click += (_, _) => box.Text = s.Barcode;
                        suggest.Children.Add(pick);
                    }
                    suggest.IsVisible = true;
                });
            }
            return row;
        }
        for (var i = 0; i < steps.Count; i++)
        {
            if (!showProductTable)
            {
                var line = new TextBlock { Text = "• " + ProductActionPlan.Describe(steps[i]), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
                Use(line, TextBlock.ForegroundProperty, "BrushText");
                body.Children.Add(line);
            }
        }
        if (showProductTable && steps.Any(st => st.Op is "create" or "receive"))
        {
            var rowsPanel = new StackPanel { Spacing = 6 };
            var titleRow = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            var blockTitle = new TextBlock
            {
                Text = T("Штрихкоды товаров", "Товарлардын штрихкоддору", "Product barcodes", "Ürün barkodları", "Mahsulotlar shtrix-kodlari"),
                FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
            };
            Use(blockTitle, TextBlock.ForegroundProperty, "BrushText");
            counterText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(counterText, TextBlock.ForegroundProperty, "BrushAccentStrong");
            Grid.SetColumn(counterText, 2);
            titleRow.Children.Add(blockTitle);
            titleRow.Children.Add(counterText);
            rowsPanel.Children.Add(titleRow);
            for (var i = 0; i < steps.Count; i++)
                if (steps[i].Op is "create" or "receive")
                    AddBarcodeRow(i, rowsPanel);
            // 2026-10-26 (требование владельца): «Сгенерировать» — свой штрихкод новым товарам с пустым полем (BarcodeGenerator:
            // EAN-13, не с 0 и не весовой; введённые руками коды не трогает, повторов с каталогом и между строками нет).
            if (steps.Any(st => st.Op == "create"))
            {
                var makeCodes = UiKit.Ghost(this, T("Сгенерировать", "Түзүү", "Generate", "Oluştur", "Yaratish"));
                makeCodes.Height = 32;
                makeCodes.FontSize = 12.5;
                makeCodes.Padding = new Thickness(12, 0);
                makeCodes.Margin = new Thickness(0, 0, 10, 0);
                ToolTip.SetTip(makeCodes, T("Проставить свой штрихкод новым товарам с пустым полем", "Талаасы бош жаңы товарларга өз штрихкодун коюу",
                    "Set an own barcode on new products with an empty field", "Alanı boş yeni ürünlere kendi barkodunu ver", "Maydoni bo'sh yangi mahsulotlarga o'z shtrix-kodini qo'yish"));
                Grid.SetColumn(makeCodes, 1);
                makeCodes.Click += (_, _) =>
                {
                    var typed = barcodeRows.Select(r => r.Code).Where(c => c.Length > 0).ToList();
                    foreach (var r in barcodeRows)
                    {
                        if (r.Step.Op != "create" || !r.Editable || r.Code.Length > 0)
                            continue;
                        var code = BarcodeGenerator.GenerateForCatalog(typed);
                        typed.Add(code);
                        r.Box.Text = code;
                    }
                };
                titleRow.Children.Add(makeCodes);
            }
            statusText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 2, 2, 0) };
            dupText = new TextBlock
            {
                Text = T("⚠ Один и тот же штрихкод стоит у двух строк — исправьте, иначе провести нельзя.", "⚠ Бир эле штрихкод эки сапта турат — оңдоңуз, болбосо өткөрүүгө болбойт.",
                    "⚠ The same barcode is on two lines — fix it, otherwise it cannot be posted.", "⚠ Aynı barkod iki satırda — düzeltin, aksi halde işlenemez.",
                    "⚠ Bir xil shtrix-kod ikki qatorda — tuzating, aks holda o'tkazib bo'lmaydi."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(2, 0, 2, 0), IsVisible = false,
            };
            Use(dupText, TextBlock.ForegroundProperty, "BrushDanger");
            rowsPanel.Children.Add(statusText);
            rowsPanel.Children.Add(dupText);
            blockHost = new Border { Child = rowsPanel, Margin = new Thickness(0, 6, 0, 2), Padding = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            Use(blockHost, Border.BorderBrushProperty, "BrushBorder");
            // Узкое окно (< 700 px): подпись строки над полем, поле на всю ширину.
            var narrowNow = false;
            rowsPanel.SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < 640;
                if (narrow == narrowNow)
                    return;
                narrowNow = narrow;
                foreach (var r in barcodeRows)
                    PlaceBarcodeRow(r, narrow);
            };
            body.Children.Add(blockHost);
            RefreshBarcodes();
        }

        // Шаги с отсканированными штрихкодами (или приход к уже существующему товару с этим штрихкодом).
        List<ProductActionPlan.Step> ResolveSteps()
        {
            var list = new List<ProductActionPlan.Step>();
            for (var i = 0; i < steps.Count; i++)
            {
                var st = steps[i];
                if (!barcodeBoxes.TryGetValue(i, out var b) || (b.Box.Text ?? "").Trim() is not { Length: >= 4 } code)
                {
                    list.Add(st);
                    continue;
                }
                if (st.Op == "receive")
                {
                    // Приход к товару склада без штрихкода: закупка — как есть, штрихкод ставит шаг set_barcode после неё
                    // (существующий путь ProductActions.UpdateFieldsAsync); код другого товара склада не ставим — сервер не примет дубль.
                    list.Add(st);
                    if (st.Product.Id is { Length: > 0 } pid && ByBarcode(code) is null
                        && !list.Any(x => x.Op == "set_barcode" && string.Equals(x.Product.Id, pid, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new ProductActionPlan.Step("set_barcode", st.Product, null, code, null));
                    continue;
                }
                list.Add(ByBarcode(code) is { Id.Length: > 0 } existing ? AsReceive(st, existing) : st with { Barcode = code });
            }
            return list;
        }
        var scanTargets = barcodeBoxes.OrderBy(kv => kv.Key).Select(kv => kv.Value.Box).ToList();
        _scanTargets = scanTargets.Count > 0 ? scanTargets : null;

        // 2026-10-21 (И10): проверка перед «Выполнить» — цена ниже закупки, наценка < 0 или > 500 %, остаток в минус, списание больше
        // половины остатка, больше 20 товаров разом: предупреждение с цифрами в карточке и второе подтверждение.
        var warnings = ProductActionWarnings(steps);
        if (warnings.Count > 0)
            body.Children.Add(WarningsPanel(warnings));
        var apply = UiKit.Primary(this, purchase
            ? T("Провести закупку…", "Сатып алууну өткөрүү…", "Post purchase…", "Alımı işle…", "Xaridni o'tkazish…")
            : T($"Применить изменения ({steps.Count})", $"Өзгөртүүлөрдү колдонуу ({steps.Count})", $"Apply changes ({steps.Count})", $"Değişiklikleri uygula ({steps.Count})", $"O'zgarishlarni qo'llash ({steps.Count})"));
        apply.Height = 38;
        Avalonia.Automation.AutomationProperties.SetName(apply, purchase ? T("Провести закупку", "Сатып алууну өткөрүү", "Post purchase", "Alımı işle", "Xaridni o'tkazish")
            : T("Применить изменения", "Өзгөртүүлөрдү колдонуу", "Apply changes", "Değişiklikleri uygula", "O'zgarishlarni qo'llash"));
        var confirmedTwice = warnings.Count == 0;
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 38;
        var result = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 20 };
        Use(result, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-24: сообщение «повторяются штрихкоды» (из Apply) гаснет, когда повтор исправлен.
        afterRefresh = () =>
        {
            if (dupShown && dupRows.Count == 0)
            {
                dupShown = false;
                result.IsVisible = false;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, cancel } };
        // 2026-10-11, тестировщик (иишка.md, 2): «рядом с одобрением — кнопка «Отредактировать»: подробный редактор списка».
        // 2026-10-19: редактор накладной стал окном закупки — его открывает «Провести закупку…» (отдельная «✎ Отредактировать» не нужна).
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = invoice ? 900 : 680, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 8, Children = { title, body, buttons, result } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushAccentStrong");
        var finished = false;
        var superseded = false;
        void Cancel()
        {
            // 2026-10-19: пока открыто окно закупки, карточка не гаснет (новое предложение ниже её не отменяет).
            if (finished || editorOpen)
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
            // 2026-10-19, ревью: замена карточки после частичного проведения — «Проведено частично: …», карточка больше не нажимается,
            // остальные строки — в новой (там проведённые помечены по реестру InvoiceEditorWindow.PostedLines).
            result.Text = (draft is { PostedSummaries.Count: > 0 }
                    ? T("Проведено частично: ", "Жарым-жартылай өткөрүлдү: ", "Partly posted: ", "Kısmen işlendi: ", "Qisman o'tkazildi: ")
                      + "\n✓ " + string.Join("\n✓ ", draft.PostedSummaries) + "\n"
                    : "")
                + (superseded
                ? T("Заменено новым предложением ниже.", "Төмөнкү жаңы сунуш менен алмаштырылды.", "Replaced by the new proposal below.",
                    "Aşağıdaki yeni öneriyle değiştirildi.", "Quyidagi yangi taklif bilan almashtirildi.")
                : draft is { PostedSummaries.Count: > 0 }
                    ? T("Остальные строки не проведены.", "Калган саптар өткөрүлгөн жок.", "The remaining lines were not posted.", "Kalan satırlar işlenmedi.", "Qolgan qatorlar o'tkazilmadi.")
                    : T("Не меняли.", "Өзгөртүлгөн жок.", "Not changed.", "Değiştirilmedi.", "O'zgartirilmadi."));
        }
        // 2026-10-19: закупка по накладной — окно закупки; карточка завершается, когда проведены все строки.
        async Task<string> OpenPurchase()
        {
            if (finished || editorOpen)
                return "";
            editorOpen = true;
            try
            {
                if (draft is null)
                {
                    draft = InvoiceEditorWindow.PurchaseDraft.FromSteps(ResolveSteps(), supplierHint.Name, supplierHint.Inn);
                    // Штрихкоды теперь правятся в окне закупки — поля карточки больше не действуют.
                    foreach (var (box, _) in barcodeBoxes.Values)
                        box.IsEnabled = false;
                    if (ReferenceEquals(_scanTargets, scanTargets))
                        _scanTargets = null;
                }
                await new InvoiceEditorWindow(draft).ShowDialog<string?>(this).ConfigureAwait(true);
            }
            finally
            {
                editorOpen = false;
            }
            if (draft.PostedSummaries.Count > 0)
            {
                result.IsVisible = true;
                result.Text = "✓ " + string.Join("\n✓ ", draft.PostedSummaries)
                    + (draft.Rows.Count > 0
                        ? "\n⚠ " + T($"Не проведено строк: {draft.Rows.Count} — нажмите «Провести закупку…» ещё раз.", $"Өткөрүлбөгөн саптар: {draft.Rows.Count} — «Сатып алууну өткөрүү…» баскычын кайра басыңыз.",
                            $"Lines not posted: {draft.Rows.Count} — press “Post purchase…” again.", $"İşlenmeyen satır: {draft.Rows.Count} — «Alımı işle…»ye tekrar basın.",
                            $"O'tkazilmagan qatorlar: {draft.Rows.Count} — «Xaridni o'tkazish…»ni yana bosing.")
                        : "");
            }
            // 2026-10-25 (ТЗ «оприходование в фоне», Q3): часть закупки ещё проводится в фоне — карточка не ждёт сервер.
            if (draft.PendingJobs.Count > 0)
            {
                result.IsVisible = true;
                result.Text = (draft.PostedSummaries.Count > 0 ? result.Text + "\n" : "") + "⏳ "
                    + T("Операция выполняется в фоне — можно продолжать работу", "Операция фондо аткарылууда — иштей берсеңиз болот",
                        "The operation is running in the background — you can keep working", "İşlem arka planda yürütülüyor — çalışmaya devam edebilirsiniz",
                        "Amal fonda bajarilmoqda — ishni davom ettirishingiz mumkin");
            }
            if (!draft.Completed)
            {
                // 2026-10-19, ревью: пока окно было открыто, пришла новая карточка — эта закрывается и после частичного проведения.
                if (superseded)
                    Cancel();
                return draft.PostedSummaries.Count > 0 ? result.Text ?? "" : "";
            }
            finished = true;
            if (_pendingProductCancel == Cancel)
            {
                _pendingProductApply = null;
                _pendingProductCancel = null;
            }
            buttons.IsVisible = false;
            // Прочие шаги карточки (описание, категория, штрихкод set_barcode…) — как раньше, после закупки.
            // 2026-10-25 (Q3): закупка может проводиться в фоне — тогда шаги выполняются ПОСЛЕ её завершения (draft.OnSettled, UI-поток),
            // один раз (othersRan), и только если все части закупки оприходованы полностью (иначе штрихкод и пр. не ставим: приход к
            // товару мог не пройти). Прежний порядок (быстрый ответ) не меняется.
            var othersRan = false;
            async Task AfterPurchaseAsync(bool allDone, bool rewriteText)
            {
                if (othersRan)
                    return;
                othersRan = true;
                if (rewriteText)
                {
                    result.IsVisible = true;
                    result.Text = draft.PostedSummaries.Count > 0 ? "✓ " + string.Join("\n✓ ", draft.PostedSummaries) : "";
                }
                if (!allDone)
                {
                    result.IsVisible = true;
                    result.Text += (result.Text is { Length: > 0 } ? "\n" : "") + "⚠ "
                        + T("Закупка проведена не полностью — откройте «Приходы в фоне». Штрихкоды и прочие шаги карточки не выполнены.",
                            "Сатып алуу толук өткөрүлгөн жок — «Фондогу кирүүлөрдү» ачыңыз. Штрихкоддор жана карточканын башка кадамдары аткарылган жок.",
                            "The purchase was not fully posted — open “Background receipts”. Barcodes and other card steps were not applied.",
                            "Alım tam işlenmedi — «Arka plandaki girişler»i açın. Barkodlar ve kartın diğer adımları uygulanmadı.",
                            "Xarid to'liq o'tkazilmadi — «Fondagi kirimlar»ni oching. Shtrixkodlar va kartaning boshqa qadamlari bajarilmadi.");
                }
                else
                {
                    var others = ResolveSteps().Where(st => !ProductActionPlan.IsPurchaseStep(st)).ToList();
                    if (others.Count > 0)
                    {
                        var done = await ProductActionPlan.ExecuteAllAsync(others, T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), CancellationToken.None,
                            proposed).ConfigureAwait(true);
                        result.Text += "\n" + done;
                    }
                }
                PosLogger.Log($"ИИ-советник: накладная проведена закупкой ({steps.Count(ProductActionPlan.IsPurchaseStep)} строк).", "INFO");
                SaveCurrentChat();
                ScrollToEnd();
            }
            if (draft.PendingJobs.Count > 0)
            {
                draft.OnSettled = allDone => AfterPurchaseAsync(allDone, rewriteText: true);
                SaveCurrentChat();
                ScrollToEnd();
                return result.Text ?? "";
            }
            await AfterPurchaseAsync(!draft.BackgroundFailed, rewriteText: false).ConfigureAwait(true);
            return result.Text ?? "";
        }
        async Task<string> Apply()
        {
            if (finished)
                return "";
            // 2026-10-21 (И10): есть предупреждения — сначала второе подтверждение в карточке (голосовое «да» его не заменяет).
            // 2026-10-24: один и тот же штрихкод у двух строк — сервер второй товар с ним не примет; пока повтор не исправлен, не проводим.
            // Нехватка штрихкодов (пустые поля) проведению не мешает.
            if (dupRows.Count > 0)
            {
                var lines = string.Join(", ", dupRows.OrderBy(n => n).Select(n => (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                dupShown = true;
                result.IsVisible = true;
                result.Text = T($"⚠ Нельзя провести: один и тот же штрихкод у строк {lines}. Исправьте и нажмите ещё раз.",
                    $"⚠ Өткөрүүгө болбойт: бир эле штрихкод {lines}-саптарда. Оңдоп, кайра басыңыз.",
                    $"⚠ Cannot post: the same barcode is on lines {lines}. Fix it and press again.",
                    $"⚠ İşlenemez: aynı barkod {lines}. satırlarda. Düzeltip tekrar basın.",
                    $"⚠ O'tkazib bo'lmaydi: bir xil shtrix-kod {lines}-qatorlarda. Tuzatib, yana bosing.");
                return result.Text;
            }
            if (!confirmedTwice)
            {
                ShowSecondConfirm(card, warnings, () => { confirmedTwice = true; _ = Apply(); }, buttons);
                return T("Есть предупреждения — подтвердите ещё раз кнопкой на экране.", "Эскертүүлөр бар — экрандагы баскыч менен дагы бир жолу ырастаңыз.",
                    "There are warnings — confirm once more with the button on screen.", "Uyarılar var — ekrandaki düğmeyle bir kez daha onaylayın.",
                    "Ogohlantirishlar bor — ekrandagi tugma bilan yana bir bor tasdiqlang.");
            }
            if (purchase)
                return await OpenPurchase().ConfigureAwait(true);
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
            // 2026-10-16: с временем карточки — такой же план, уже выполненный после её появления (в боте или новой карточкой), повторно
            // не выполнится; кнопка и голосовое «да» (_pendingProductApply) идут сюда же.
            var done = await ProductActionPlan.ExecuteAllAsync(ResolveSteps(), T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), CancellationToken.None,
                proposed).ConfigureAwait(true);
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
            var (sections, _) = await GetSummaryAsync().ConfigureAwait(true);
            var access = CurrentAccess();
            // 2026-10-21 (О7): разбор фразы звонка — БЕЗ истории текстового чата (звонок 07.10 искал фото товаров из прошлых просьб).
            var res = await TelegramAiChat.AskOwnerAppAsync(
                utterance + "\n(Сказано голосом во время звонка. Ответь одной-двумя фразами и строками «ТОВАР:», если нужны изменения или открыть товар. "
                + "Действия — только для товаров, названных в ЭТОЙ фразе.)",
                sections, access, CancellationToken.None, useHistory: false).ConfigureAwait(true);
            var (answer, error, webSources) = (res.Answer, res.Error, res.Sources);
            if (_live != live || answer is not { Length: > 0 })
            {
                if (error != null)
                    PosLogger.Log($"ИИ-советник: в звонке товар не разобран ({error}).", "WARNING");
                return;
            }
            // 2026-10-19, ревью: в звонке фото нет — подсказку поставщика меняет только новая строка «ПОСТАВЩИК».
            if (ProductActionPlan.SupplierHint(answer) is var liveHint && (liveHint.Name is not null || liveHint.Inn is not null))
                _invoiceSupplierHint = liveHint;
            var (text, steps, _) = ProductActionPlan.Extract(answer, utterance);
            // 2026-10-21 (О7): фото — только для товаров из этой фразы; без права «Склад» изменений нет (О4).
            var named = ProductsByWords(utterance, 6).Select(p => p.Id).ToHashSet();
            steps = steps.Where(st => st.Op != "photo" || named.Contains(st.Product.Id)).ToList();
            if (!access.Products)
                steps = steps.Where(st => st.Op is "open" or "open_section").ToList();
            var changes = steps.Where(st => st.Op is not ("open" or "open_section")).ToList();
            // Просили сведения (без изменений) — ответ текстом на экране, со ссылками.
            if (changes.Count == 0 && text.Length > 0 && ProductInfoResearch.LooksLikeInfoRequest(utterance))
            {
                AddBubble(TelegramAiChat.ToPlainText(text), fromOwner: false);
                if (webSources.Count > 0)
                    AddWebSources(webSources);
            }
            ShowProductSteps(steps, utterance);
            var note = changes.Count > 0
                ? "[Программа] На экране список изменений: " + string.Join("; ", changes.Select(ProductActionPlan.Describe))
                  + ". Коротко скажи владельцу, что подготовлено, и спроси: выполнить? (ответ «да, выполни» или кнопка)."
                : steps.Any(st => st.Op == "open") && HasOpenCommand(utterance)
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

    // Сведения о сотрудниках уже есть у советника в звонке (в инструкции или присланы после начала).
    private bool _liveStaffDelivered;

    private static readonly string[] LiveStaffPeriodWords = { "сегодн", "вчера", "недел", "прошл", "бүгүн", "кечээ", "жума", "өткөн" };

    /// <summary>2026-10-06: сведения о сотрудниках для звонка — не дольше 6 с (не держать начало разговора); не вышло — пусто.</summary>
    private static async Task<string> LiveStaffContextAsync(string question)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var text = await TelegramAiChat.StaffContextForAsync(question, cts.Token).ConfigureAwait(false);
            PosLogger.Log($"ИИ-советник: сотрудники для звонка — {text.Length} симв. за {watch.ElapsedMilliseconds} мс.", "INFO");
            return text;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сотрудники для звонка не получены ({ex.Message}).", "WARNING");
            return "";
        }
    }

    private static readonly string[] DataWords =
    {
        "продаж", "продал", "выручк", "чек", "остат", "товар", "долг", "должник", "зарплат", "табел", "смен", "прибыл", "топ", "сколько",
        "отчёт", "отчет", "аналит", "наценк", "касс", "клиент", "покупател", "поставщ", "расход", "доход", "закончил", "заканчива", "срок",
        "сатуу", "саттык", "калдык", "карыз", "айлык", "эмгек", "канча", "пайда", "киреше", "кардар", "сотрудник", "кызматкер", "сумм",
        "sales", "revenue", "stock", "debt", "salary", "how much", "satış", "borç", "sotuv", "qarz",
    };

    /// <summary>Вопрос с цифрами магазина (продажи, чеки, остатки, долги, сотрудники…) или «найди Mars» — ответ таблицей в чат.</summary>
    private static bool LooksLikeDataQuestion(string spoken)
    {
        var t = spoken.ToLowerInvariant();
        if (DataWords.Any(t.Contains) || OwnerSalesData.LooksLikeQuestion(spoken))
            return true;
        var find = new[] { "найд", "найт", "поищ", "тап", "таап", "покаж", "көрсөт", "find", "show" }.Any(t.Contains);
        return find && ProductsByWords(spoken, 1).Count > 0;
    }

    /// <summary>2026-10-07, владелец: «баг — ИИ должен сам собирать информацию и выводить в чате таблицей, и на звонок добавь».
    /// В звонке голосом таблицу не покажешь: параллельно программа спрашивает ИИ тем же путём, что и чат (сводка, сотрудники,
    /// чеки за период с сервера, карточки товаров), просит ответ таблицей и показывает его в чате; советнику — итог для голоса.</summary>
    private async Task LiveTablePassAsync(string utterance, GeminiLiveVoice live, bool speak)
    {
        var bubble = AddBubble(T("◐ Собираю данные для таблицы…", "◐ Таблица үчүн маалымат чогултуп жатам…", "◐ Collecting data for a table…",
            "◐ Tablo için veriler toplanıyor…", "◐ Jadval uchun ma'lumot yig'ilmoqda…"), fromOwner: false);
        try
        {
            var (sections, names) = await GetSummaryAsync().ConfigureAwait(true);
            var res = await TelegramAiChat.AskOwnerAppAsync(
                utterance + "\n(Вопрос задан голосом во время звонка. Ответь ТАБЛИЦЕЙ Markdown по данным магазина: перед таблицей одна короткая "
                + "строка, в таблице — заголовок, строки и строка «Итого», где уместно. Без вступлений и советов. Данных нет — одной строкой честно.)",
                sections, CurrentAccess(), CancellationToken.None, useHistory: false, forceFull: ForceSections(utterance)).ConfigureAwait(true);
            var answer = res.Answer;
            if (answer is not { Length: > 0 })
            {
                // 2026-10-21 (О8): понятный текст вместо «The operation was canceled.».
                bubble.Text = FriendlyErrorText(res.Error);
                return;
            }
            var (clean, _, _) = ProductActionPlan.Extract(answer, utterance);
            var text = OwnerAiContext.RevealCodes(TelegramAiChat.ToPlainText(clean), names);
            bubble.Text = text;
            ScrollToEnd();
            PosLogger.Log($"ИИ-советник: таблица к вопросу в звонке ({text.Length} симв.).", "INFO");
            if (_live == live && live.IsOpen)
                await live.SendTextAsync("[Программа] На экране таблица по вопросу владельца. Данные таблицы: "
                                         + (text.Length > 900 ? text[..900] + "…" : text)
                                         + (speak ? " Коротко скажи голосом главный итог (одна-две фразы), таблицу не зачитывай." : " Просто запомни."),
                    respond: speak).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            bubble.Text = FriendlyErrorText(ex is OperationCanceledException ? "timeout" : "internal");
            PosLogger.Log($"ИИ-советник: таблица в звонке не собрана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Сведения о сотрудниках пришли после начала звонка — в память разговора, без ответа вслух.</summary>
    private async Task SendStaffWhenReadyAsync(Task<string> staffTask, GeminiLiveVoice live)
    {
        var staff = await staffTask.ConfigureAwait(true);
        for (var i = 0; i < 60 && _live == live && !live.IsOpen; i++)
            await Task.Delay(250).ConfigureAwait(true);
        if (_live != live || staff.Length == 0 || !live.IsOpen)
            return;
        await live.SendTextAsync("[Программа] Справка о сотрудниках для ответов на вопросы владельца (сейчас вслух ничего не говори, просто запомни): " + staff,
            respond: false).ConfigureAwait(true);
        _liveStaffDelivered = true;
        PosLogger.Log("ИИ-советник: сотрудники переданы в звонок после его начала.", "INFO");
    }

    /// <summary>2026-10-06: сводка магазина собралась после начала звонка — в память разговора, без ответа вслух.</summary>
    private async Task SendSummaryWhenReadyAsync(Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> summaryTask, GeminiLiveVoice live)
    {
        try
        {
            var (sections, names) = await summaryTask.ConfigureAwait(true);
            // 2026-10-21 (О1): тот же построитель по важности, что у чата.
            var summary = AiContextBudget.Compose(sections, 24000).Text;
            for (var i = 0; i < 60 && _live == live && !live.IsOpen; i++)
                await Task.Delay(250).ConfigureAwait(true);
            if (_live != live || summary.Length == 0 || !live.IsOpen)
                return;
            _liveDebtors = names;
            await live.SendTextAsync("[Программа] Данные магазина для ответов владельцу (сейчас вслух ничего не говори, просто запомни; "
                                     + "раньше этих данных у тебя не было — теперь отвечай по ним): " + summary, respond: false).ConfigureAwait(true);
            PosLogger.Log($"ИИ-советник: сводка ({summary.Length} симв.) передана в звонок после его начала.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сводка для звонка не собрана ({ex.Message}).", "WARNING");
        }
    }

    private async Task LiveStaffPassAsync(string utterance, GeminiLiveVoice live)
    {
        var staff = await LiveStaffContextAsync(utterance).ConfigureAwait(true);
        if (_live != live || staff.Length == 0)
            return;
        await live.SendTextAsync("[Программа] Данные о сотрудниках по вопросу владельца: " + staff + " Коротко ответь владельцу по этим данным.").ConfigureAwait(true);
        _liveStaffDelivered = true;
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
        // 2026-10-06, владелец (снимок звонка: «нет данных по сотрудникам», «не могу показать табель»): «исправь это, дай доступ».
        // Сотрудники, ставки, начисления и табель с начала месяца — параллельно со сводкой. Начало звонка не держим:
        // не успели — программа пришлёт их в звонок, как только будут готовы (SendStaffWhenReadyAsync).
        var liveAccess = CurrentAccess();
        // 2026-10-21 (О4): сотрудники в звонок — только с правом на зарплату.
        var staffTask = liveAccess.Staff ? LiveStaffContextAsync("") : Task.FromResult("");
        string? staffNow = null;
        // 2026-10-06, владелец: «долгое подключение звонка» (лог 23:10: сервер не отвечал, «Подключаюсь…» ~40 с). Сводку ждём
        // не дольше 6 с; не успела — звонок начинается без неё, сводка уходит в разговор, как только соберётся.
        var summaryTask = GetSummaryAsync();
        var summaryLate = false;
        try
        {
            if (await Task.WhenAny(summaryTask, Task.Delay(6000)).ConfigureAwait(true) != summaryTask)
            {
                summaryLate = true;
                throw new TimeoutException("сводка не готова за 6 с — дошлю в разговор");
            }
            var (sections, debtorNames) = await summaryTask.ConfigureAwait(true);
            names = debtorNames;
            await Task.WhenAny(staffTask, Task.Delay(1500)).ConfigureAwait(true);
            staffNow = staffTask.IsCompletedSuccessfully ? staffTask.Result : null;
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(sections, CurrentLines().Select(l => (l.Owner, l.Text)), staffNow, liveAccess);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: сводка для разговора не собрана ({ex.Message}).", "WARNING");
            names = new Dictionary<int, string>();
            staffNow = staffTask.IsCompletedSuccessfully ? staffTask.Result : null;
            instruction = TelegramAiChat.BuildOwnerVoiceInstruction(Array.Empty<AiSection>(), CurrentLines().Select(l => (l.Owner, l.Text)), staffNow, liveAccess);
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
            _liveStaffDelivered = staffNow is { Length: > 0 };
            if (staffNow is null)
                _ = SendStaffWhenReadyAsync(staffTask, live);
            if (summaryLate)
                _ = SendSummaryWhenReadyAsync(summaryTask, live);
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
            // 2026-10-21 (О7): сменилась тема (фраза не про фото) — прежние «товарные цели» звонка забываются.
            if (!new[] { "фото", "фотк", "сүрөт", "photo", "rasm", "surat", "картин", "fotoğraf" }.Any(spoken.ToLowerInvariant().Contains))
            {
                _liveLastPhotoTalkUtc = DateTime.MinValue;
                _liveLastProducts = new();
            }
            // 2026-10-06, владелец в звонке: «Да, поставь четвёртое фото по счёту» — теперь программа ставит его сама.
            if (PhotoChoiceNumber(spoken) is { } photoNumber)
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var result = await ApplyPhotoChoiceAsync(photoNumber).ConfigureAwait(true);
                    if (_live == liveNow)
                        await liveNow.SendTextAsync("[Программа] " + result + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            // 2026-10-06: «найди / покажи фото Mars», «поищи в интернете» — фото в чате или варианты из интернета (LivePhotoPass).
            else if (LivePhotoPass(spoken, _liveAiText.ToString(), liveNow))
            {
            }
            else if (SiteEditorWindow.LooksLikeCommand(spoken) && ProductActionPlan.OpenSection is not null)
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var result = await SiteEditorCommandAsync(spoken).ConfigureAwait(true);
                    if (result is not null && _live == liveNow)
                        await liveNow.SendTextAsync("[Программа] " + result + " Коротко скажи владельцу результат.").ConfigureAwait(true);
                });
            else if (TryQuickOpen(spoken) is { } opened)
            {
                _ = liveNow.SendTextAsync("[Программа] " + opened + " Скажи об этом одной короткой фразой; владелец видит раздел на экране.");
                if (HasActionBesidesOpen(spoken))
                    _ = LiveProductPassAsync(spoken, liveNow);
            }
            else if (_pendingProductApply is { } apply && ProductActionPlan.IsVoiceYes(spoken))
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
            // Сотрудники: за другой период (с начала месяца уже в звонке) или сведения ещё не дошли до звонка — программа
            // присылает данные, и советник сразу отвечает (владелец, 06.10: «почему долго??» — советник ждал, данные не приходили).
            else if (TelegramAiChat.LooksLikeStaffQuestion(spoken) && CurrentAccess().Staff && (!_liveStaffDelivered || LiveStaffPeriodWords.Any(spoken.ToLowerInvariant().Contains)))
            {
                _ = LiveStaffPassAsync(spoken, liveNow);
                _ = LiveTablePassAsync(spoken, liveNow, speak: false);
            }
            // 2026-10-07, владелец: «всю информацию выводи в чат красивой таблицей» — вопрос с цифрами в звонке: таблица в чате.
            else if (LooksLikeDataQuestion(spoken))
                _ = LiveTablePassAsync(spoken, liveNow, speak: true);
            // 2026-10-07: речь владельца распознана плохо (кыргызская фраза пришла корейскими буквами), а советник сам сказал
            // «Открываю склад» / «ачып жатам» — выполняем по его словам (его речь программа знает точно).
            else if (_liveAiText.ToString() is { Length: > 0 } aiSaid
                     && new[] { "открыва", "открою", "ачып жатам", "ачам", "opening", "açıyorum", "ochyapman" }.Any(aiSaid.ToLowerInvariant().Contains)
                     && TryQuickOpen(aiSaid.Length > 160 ? aiSaid[..160] : aiSaid) is { } openedByAi)
                _ = liveNow.SendTextAsync("[Программа] " + openedByAi + " Владелец видит это на экране.", respond: false);
        }
        if (_liveAi is not null)
        {
            var text = _liveAiText.ToString().Trim();
            _liveAi.Text = text.Length == 0
                ? (interrupted ? "…" : "")
                : OwnerAiContext.RevealCodes(TelegramAiChat.ToPlainText(text), _liveDebtors) + (interrupted ? " …" : "");
            // 2026-10-07, владелец: «перестань выдавать фото из базы NurCRM, ищи прямо в интернете» — фото склада к репликам
            // больше не подставляются (раньше «бананды» показывало товар «Нан»); просьба о фото — поиск в интернете (LivePhotoPass).
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
        // Таблица вслух — без разделителей и черт: «Кассир Тест, 10 смен, …».
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*\|?(\s*:?-{2,}:?\s*\|)+.*$\n?", "").Replace(" |", ",").Replace("|", "");
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
              + "Для поиска в Groq уходит только вопрос. Если Gemini не ответил, Groq ответит сам — тогда туда уходят только обезличенные цифры магазина: без сотрудников, должников и чеков (на такие вопросы отвечает только Gemini).",
                "«Ачкыч алуу» басыңыз → Google аркылуу кириңиз → «Create API Key», ачкычты (gsk_…) көчүрүп, төмөнгө чаптаңыз (карта керек эмес, күнүнө 1000 суроо). "
                + "Издөө үчүн Groq'ко суроо гана кетет. Gemini жооп бербесе, Groq өзү жооп берет — анда ага дүкөндүн жеке маалыматсыз сандары гана кетет: кызматкерлер, карыздарлар жана чектер жок.",
                "Press “Get a key” → sign in with Google → “Create API Key”, copy the key (gsk_…) and paste it below (no card, 1000 requests a day). "
                + "For search only the question goes to Groq. If Gemini doesn't answer, Groq answers itself — then only depersonalised shop figures go there: no staff, debtors or receipts.",
                "«Anahtar al»a basın → Google ile giriş yapın → «Create API Key», anahtarı (gsk_…) kopyalayıp aşağıya yapıştırın (kart gerekmez, günde 1000 istek). "
                + "Arama için Groq'a yalnızca soru gider. Gemini yanıt vermezse Groq kendisi yanıtlar — o zaman yalnızca kişisel veri içermeyen mağaza rakamları gider: çalışan, borçlu ve fiş yok.",
                "«Kalit olish» ni bosing → Google orqali kiring → «Create API Key», kalitni (gsk_…) nusxalab pastga joylang (karta kerak emas, kuniga 1000 so'rov). "
                + "Qidiruv uchun Groq'ga faqat savol ketadi. Gemini javob bermasa, Groq o'zi javob beradi — unda faqat shaxsiy ma'lumotsiz do'kon raqamlari ketadi: xodimlar, qarzdorlar va cheklarsiz."),
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
              + "«Create API Key», скопируйте ключ (sk-or-…) и вставьте ниже. При этом в OpenRouter уходят вопрос и только обезличенные цифры (без сотрудников, должников и чеков); бесплатные модели OpenRouter — разных компаний, условия хранения данных у них разные.",
                "Gemini'нин күндүк лимити бүтсө, OpenRouter'дин акысыз модели жооп берет (күнүнө 50 суроо). «Ачкыч алуу» басыңыз → Google аркылуу кириңиз → "
                + "«Create API Key», ачкычты (sk-or-…) көчүрүп, төмөнгө чаптаңыз. Анда OpenRouter'ге суроо жана жеке маалыматсыз сандар гана кетет (кызматкерлер, карыздарлар, чектер жок); акысыз моделдер ар кайсы компанияныкы.",
                "If Gemini's daily limit runs out, a free OpenRouter model answers (50 requests a day). Press “Get a key” → sign in with Google → "
                + "“Create API Key”, copy the key (sk-or-…) and paste it below. Then only the question and depersonalised figures go to OpenRouter (no staff, debtors or receipts); its free models belong to different companies with different data policies.",
                "Gemini'nin günlük limiti biterse ücretsiz bir OpenRouter modeli yanıtlar (günde 50 istek). «Anahtar al»a basın → Google ile giriş yapın → "
                + "«Create API Key», anahtarı (sk-or-…) kopyalayıp aşağıya yapıştırın. O zaman OpenRouter'a yalnızca soru ve kişisel veri içermeyen rakamlar gider (çalışan, borçlu, fiş yok); ücretsiz modeller farklı şirketlerindir.",
                "Gemini kunlik limiti tugasa, OpenRouter bepul modeli javob beradi (kuniga 50 so'rov). «Kalit olish» ni bosing → Google orqali kiring → "
                + "«Create API Key», kalitni (sk-or-…) nusxalab pastga joylang. Shunda OpenRouter'ga faqat savol va shaxsiy ma'lumotsiz raqamlar ketadi (xodimlar, qarzdorlar, cheklarsiz); bepul modellar turli kompaniyalarniki."),
            "sk-or-…", AiProviders.HasOpenRouter, "https://openrouter.ai/keys", AiProviders.TestOpenRouterAsync,
            key => UserPreferences.Instance.OpenRouterApiKey = key, onSaved: null);
        stack.Children.Add(_routerSection);
        _modelsCard.Child = stack;
        // Ключа Groq нет — блок Groq виден сразу (как блок ключа Gemini), OpenRouter — по ⚙.
        _routerSection.IsVisible = false;
        // 2026-10-21 («дизайн 10», О10): блок необязательных ключей — только по ⚙ (раньше без ключа Groq он всегда стоял над лентой).
        _modelsCard.IsVisible = false;
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

    /// <summary>2026-10-06, владелец в звонке: «Да, поставь четвёртое фото по счёту» — советник обещал, программа не делала.
    /// Варианты последнего поиска фото (видимые, по порядку на экране, с номерами №1…), выбор — голосом или текстом.</summary>
    private readonly List<(ProductPhotoFinder.Candidate Candidate, Button Button, Border Card, TextBlock Label)> _photoChoices = new();

    private static readonly string[][] OrdinalStems =
    {
        new[] { "перв", "биринчи", "first", "birinci", "birinchi" },
        new[] { "втор", "экинчи", "second", "ikinci", "ikkinchi" },
        new[] { "трет", "үчүнчү", "third", "üçüncü", "uchinchi" },
        new[] { "четв", "төртүнчү", "fourth", "dördüncü", "to'rtinchi" },
        new[] { "пят", "бешинчи", "fifth", "beşinci", "beshinchi" },
        new[] { "шест", "алтынчы", "sixth", "altıncı", "oltinchi" },
        new[] { "седьм", "жетинчи", "seventh", "yedinci", "yettinchi" },
        new[] { "восьм", "сегизинчи", "eighth", "sekizinci", "sakkizinchi" },
    };

    /// <summary>Номер фото из просьбы «поставь фото 4», «четвёртое фото», «№3»; null — это не выбор варианта.</summary>
    private int? PhotoChoiceNumber(string text)
    {
        var visible = _photoChoices.Count(c => c.Card.IsVisible);
        if (visible == 0 || text.Length > 90)
            return null;
        var t = text.ToLowerInvariant();
        var words = System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}\p{N}']+").Where(w => w.Length > 0).ToList();
        var aboutPhoto = new[] { "фото", "фотк", "вариант", "сүрөт", "photo", "picture", "option", "fotoğraf", "rasm", "surat", "№" }.Any(t.Contains);
        var act = words.Any(w => new[] { "постав", "став", "выбер", "бери", "давай", "кой", "танда", "use", "set", "pick", "take", "kullan", "seç", "qo'y", "tanla" }.Any(w.StartsWith));
        if (!aboutPhoto || !act)
            return null;
        int? n = null;
        if (System.Text.RegularExpressions.Regex.Match(t, @"(?:№|номер|number|nomer|фото|photo|сүрөт|вариант)\s*(\d{1,2})\b|\b(\d{1,2})\s*(?:-?(?:е|ое|й|ий|ю|th|st|nd|rd)\b|\s*(?:фото|photo|сүрөт|вариант))") is { Success: true } m)
            n = int.Parse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value);
        else
            for (var i = 0; i < OrdinalStems.Length && n is null; i++)
                if (words.Any(w => OrdinalStems[i].Any(w.StartsWith)))
                    n = i + 1;
        return n is >= 1 && n <= visible ? n : null;
    }

    /// <summary>Поставить вариант №n (по видимым на экране); текст результата для чата и звонка.</summary>
    private async Task<string> ApplyPhotoChoiceAsync(int n)
    {
        var choice = _photoChoices.Where(c => c.Card.IsVisible).ElementAt(n - 1);
        if (!choice.Button.IsEnabled)
            return T($"Фото №{n} уже поставлено или недоступно.", $"№{n} сүрөт коюлган же жеткиликсиз.", $"Photo #{n} is already set or unavailable.",
                $"{n} numaralı fotoğraf zaten ayarlı veya kullanılamıyor.", $"№{n} rasm allaqachon qo'yilgan yoki mavjud emas.");
        foreach (var other in _photoChoices.Where(c => c.Button != choice.Button))
            other.Button.IsEnabled = false;
        await ApplyPhotoAsync(choice.Button, choice.Candidate).ConfigureAwait(true);
        var ok = !choice.Button.IsEnabled;
        return ok
            ? T($"Фото №{n} поставлено товару «{choice.Candidate.Product.Title}».", $"№{n} сүрөт «{choice.Candidate.Product.Title}» товарына коюлду.",
                $"Photo #{n} set for “{choice.Candidate.Product.Title}”.", $"{n} numaralı fotoğraf «{choice.Candidate.Product.Title}» ürününe ayarlandı.",
                $"№{n} rasm «{choice.Candidate.Product.Title}» mahsulotiga qo'yildi.")
            : T($"Фото №{n} поставить не получилось — выберите другое.", $"№{n} сүрөттү коюу болбой калды — башкасын тандаңыз.",
                $"Couldn't set photo #{n} — choose another.", $"{n} numaralı fotoğraf ayarlanamadı — başka birini seçin.",
                $"№{n} rasmni qo'yib bo'lmadi — boshqasini tanlang.");
    }

    /// <summary>Товар, которому последним поставили фото (для «открой склад, проверь, поставилось ли фото»).</summary>
    private NurMarketKassa.Models.Pos.CatalogProductTileVm? _lastPhotoProduct;

    /// <summary>2026-10-06, владелец (снимок звонка: «открой склад и проверь, поставилась ли фото» — «Программа не открывает, где
    /// она?»; склад открылся через 17 с — шёл полный запрос к ИИ): «дай ИИ звонку полный доступ к программе — открывать вкладки
    /// и разделы и искать товары». «Открой …» программа выполняет сама и сразу: товар — на складе, иначе раздел меню.
    /// Ответ — что открыто; null — это не команда «открыть» или открыть нечего.</summary>
    private string? TryQuickOpen(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Length > 160)
            return null;
        var words = System.Text.RegularExpressions.Regex.Split(t, @"[^\p{L}\p{N}']+").Where(w => w.Length > 0).ToList();
        // 2026-10-07, владелец (звонок по-кыргызски: «почему ИИ не смог открыть склад и найти товар»): кыргызские «ач», «аччы»,
        // «кир». Только явная команда «открыть»: «покажи остатки на складе» — это вопрос, ответ таблицей в чат.
        var open = words.Any(w => new[] { "откр", "перей", "зайди", "зайти", "ачып", "ачкыла", "аччы", "кирип", "open", "aç", "och" }.Any(w.StartsWith)
                                  || w is "ач" or "кир" or "ачып");
        // 2026-10-07, владелец: «открывай вкладки только после команды открыть» — «найди / тап товар» больше не открывает склад:
        // сведения о товаре идут таблицей в чат (LiveTablePassAsync).
        if (!open)
            return null;
        // Проверить фото — товар, которому его только что поставили. Иначе товар по целым словам названия (не подстрокой:
        // «финансы» находили товар «Нан»); раздел меню важнее товара, если не сказано «товар» и совпало одно слово.
        var section = ProductActionPlan.NavigateByWords?.Invoke(text, false);
        var (named, score) = QuickOpenProduct(words);
        var product = new[] { "фото", "фотк", "сүрөт", "photo" }.Any(t.Contains) && _lastPhotoProduct is { } photoProduct
            ? photoProduct
            : named is not null && (section is null || score >= 2 || words.Any(w => w.StartsWith("товар")))
                ? named
                : null;
        if (product is not null && ProductActionPlan.OpenProduct is { } openProduct)
        {
            openProduct(product);
            PosLogger.Log($"ИИ-советник: открыт товар «{product.Title}» на складе по фразе владельца.", "INFO");
            return T($"Открыт склад, товар «{product.Title}».", $"Кампа ачылды, товар «{product.Title}».", $"Warehouse opened at “{product.Title}”.",
                $"Depo açıldı, ürün «{product.Title}».", $"Ombor ochildi, mahsulot «{product.Title}».");
        }
        return section is not null && ProductActionPlan.NavigateByWords?.Invoke(text, true) is { } opened
            ? T($"Открыт раздел «{opened}».", $"«{opened}» бөлүмү ачылды.", $"Opened “{opened}”.", $"«{opened}» açıldı.", $"«{opened}» ochildi.")
            : null;
    }

    /// <summary>Товар для «открой …»: слова фразы (кроме команд) совпадают с началом слов названия; лучший по числу совпадений.</summary>
    private static (NurMarketKassa.Models.Pos.CatalogProductTileVm? Product, int Score) QuickOpenProduct(List<string> words)
    {
        string[] skip = { "откр", "перей", "зайд", "зайт", "покаж", "товар", "склад", "раздел", "вкладк", "провер", "постав", "фото", "фотк",
            "пожалуйста", "мне", "его", "это", "там", "где", "ачып", "ачкыла", "open", "show" };
        var said = words.Where(w => w.Length >= 3 && !skip.Any(w.StartsWith)).Distinct().ToList();
        if (said.Count == 0)
            return (null, 0);
        IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> tiles;
        try
        {
            tiles = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return (null, 0);
        }
        NurMarketKassa.Models.Pos.CatalogProductTileVm? best = null;
        var bestScore = 0;
        foreach (var tile in tiles)
        {
            var titleWords = System.Text.RegularExpressions.Regex.Split(tile.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            var score = said.Count(w => titleWords.Any(tw => tw.StartsWith(w) || (w.Length >= 4 && tw.Length >= 4 && w.StartsWith(tw))));
            if (score > bestScore)
                (best, bestScore) = (tile, score);
        }
        return (best, bestScore);
    }

    /// <summary>2026-10-06, владелец: «добавь редактор к нашему ИИ звонку». «Поставь тему Ала-Тоо», «тёмный стиль сайта»,
    /// «опубликуй сайт» — программа открывает «Редактор сайта» и выполняет сама. null — это не команда редактору.</summary>
    private async Task<string?> SiteEditorCommandAsync(string text)
    {
        if (!SiteEditorWindow.LooksLikeCommand(text) || ProductActionPlan.OpenSection is not { } openSection)
            return null;
        openSection("siteeditor");
        for (var i = 0; i < 60 && SiteEditorWindow.Current is not { IsReady: true }; i++)
            await Task.Delay(250).ConfigureAwait(true);
        if (SiteEditorWindow.Current is not { IsReady: true } editor)
            return T("Редактор сайта не открылся — попробуйте ещё раз.", "Сайттын редактору ачылган жок — дагы аракет кылыңыз.", "The website editor didn't open — try again.",
                "Site düzenleyici açılmadı — tekrar deneyin.", "Sayt muharriri ochilmadi — qayta urinib ko'ring.");
        var result = await editor.VoiceCommandAsync(text).ConfigureAwait(true);
        // 2026-10-07: «открывай вкладки только после команды открыть» — сделали и вернулись к советнику, если «открой» не говорили.
        if (!HasOpenCommand(text))
            openSection("aiadvisor");
        return result;
    }

    /// <summary>Товары, о которых говорили в звонке последними (для «поищи в интернете» без названия).</summary>
    private List<NurMarketKassa.Models.Pos.CatalogProductTileVm> _liveLastProducts = new();
    private DateTime _liveLastPhotoTalkUtc = DateTime.MinValue;

    /// <summary>Товары по словам фразы: слово — начало слова названия («батончика» → «Батончик Mars 50г»), не подстрока
    /// («нас» не находит «Ананас»); только лучшие совпадения, до <paramref name="max"/>.</summary>
    private static List<NurMarketKassa.Models.Pos.CatalogProductTileVm> ProductsByWords(string phrase, int max)
    {
        string[] skip = { "найд", "найт", "поищ", "ищи", "покаж", "показ", "интернет", "котор", "есть", "мне", "нас", "наш", "склад", "фот", "сүрөт",
            "картин", "изображ", "пожалуйста", "это", "там", "где", "товар", "откр", "постав", "photo", "find", "show", "нету", "никак", "нет" };
        var words = System.Text.RegularExpressions.Regex.Split(phrase.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3 && !skip.Any(w.StartsWith)).Distinct().ToList();
        if (words.Count == 0)
            return new();
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> tiles;
        try
        {
            tiles = CatalogCacheService.Products.Where(t => !t.IsService).ToList();
        }
        catch (InvalidOperationException)
        {
            return new();
        }
        var scored = tiles.Select(tile =>
        {
            var titleWords = System.Text.RegularExpressions.Regex.Split(tile.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            return (Tile: tile, Score: words.Count(w => titleWords.Any(tw => tw.StartsWith(w) || (w.Length >= 4 && tw.Length >= 4 && w.StartsWith(tw[..Math.Min(tw.Length, 6)])))));
        }).Where(x => x.Score > 0).ToList();
        if (scored.Count == 0)
            return new();
        var best = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == best).Select(x => x.Tile).Take(max).ToList();
    }

    /// <summary>2026-10-06/07, владелец (снимки звонка): «найди мне фотки батончика Mars» — советник сказал «программа нашла», в чате
    /// ничего; «перестань выдавать фото из базы NurCRM, ищи прямо в интернете». Любая просьба о фото в звонке — сразу поиск в
    /// интернете с индикатором в чате: товар склада по словам (или тот, о котором говорили только что); такого товара на складе
    /// нет — ищем по словам фразы (без кнопки «Поставить»). Речь владельца распознана криво («non bolotat»), а советник сам сказал,
    /// что ищет фото, — товар берём из слов советника. true — просьба о фото обработана.</summary>
    private bool LivePhotoPass(string spoken, string aiSaid, GeminiLiveVoice live)
    {
        static bool OpenWords(string x) => new[] { "откр", "перейд", "зайди", "ачып" }.Any(x.Contains);
        var t = spoken.ToLowerInvariant();
        // «Открой склад, проверь фото» — это открыть товар (TryQuickOpen), не поиск фото.
        if (OpenWords(t))
            return false;
        var recentPhotoTalk = DateTime.UtcNow - _liveLastPhotoTalkUtc < TimeSpan.FromMinutes(3);
        // Поиск разрешает только прямая команда владельца, а не слова «фото» или обещание самого ИИ.
        var fromOwner = ProductActionPlan.IsExplicitPhotoWebSearchRequest(t)
                        || (recentPhotoTalk && ProductActionPlan.IsExplicitWebSearchRequest(t));
        if (!fromOwner)
            return false;
        var source = spoken;
        var products = ProductsByWords(source, 3);
        // 2026-10-21 (О7): прежний товар — только если владелец явно говорит о нём же («этого», «его», «ушул»), а не всегда, когда
        // в фразе нет названия (07.10 звонок искал фото «Патронов для степлера» вместо «Легенды клубничной»).
        if (products.Count == 0 && recentPhotoTalk && System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<!\p{L})(его|этого|этот|этому|ему|ушул|анын|it|this)(?!\p{L})"))
            products = _liveLastProducts;
        if (products.Count == 0)
        {
            // Товара на складе нет — ищем по словам фразы (посмотреть можно, поставить некуда).
            string[] skip = { "найд", "найт", "поищ", "ищи", "покаж", "интернет", "фот", "сүрөт", "картин", "мне", "пожалуйста", "тап", "изде",
                "көрсөт", "из", "в", "на", "для", "photo", "find", "show", "программ", "экран" };
            var query = string.Join(" ", System.Text.RegularExpressions.Regex.Split(source, @"[^\p{L}\p{N}]+")
                .Where(w => w.Length >= 3 && !skip.Any(w.ToLowerInvariant().StartsWith)).Take(5));
            if (query.Length < 3)
                return false;
            products = new() { new NurMarketKassa.Models.Pos.CatalogProductTileVm("", query, "", false) };
        }
        _liveLastProducts = products;
        _liveLastPhotoTalkUtc = DateTime.UtcNow;
        var names = string.Join(", ", products.Select(p => p.Title));
        _ = ShowPhotoChoicesAsync(products);
        PosLogger.Log($"ИИ-советник: в звонке поиск фото в интернете — {names}.", "INFO");
        // 2026-10-21 (О7): советник переспрашивает «ищу фото «X» — верно?» — не тот товар владелец поправит сразу.
        AddBubble(T($"Ищу фото: «{names}». Не тот товар — скажите название ещё раз.", $"Сүрөт издеп жатам: «{names}». Башка товар болсо — атын кайра айтыңыз.",
            $"Searching photos for “{names}”. Wrong product? Say the name again.", $"«{names}» için fotoğraf arıyorum. Yanlış ürünse adını tekrar söyleyin.",
            $"«{names}» uchun rasm qidiryapman. Boshqa mahsulot bo'lsa — nomini yana ayting."), fromOwner: false);
        _ = live.SendTextAsync($"[Программа] Ищу фото в интернете для: {names}. На экране крутится индикатор поиска, через несколько секунд появятся "
                               + "варианты с номерами; владелец выберет словами «поставь фото номер N». Фото из склада программы не показывай и не обещай. "
                       + $"Скажи одной фразой ровно так: «Ищу фото «{names}» — верно?». Прежние товары из разговора не ищи.");
        return true;
    }

    /// <summary>Кроме «открой» во фразе есть действие с товаром («открой склад и спиши 1 Марс») — его разбирает ИИ.</summary>
    private static bool HasActionBesidesOpen(string text) =>
        ProductActionPlan.LooksLikeAction(System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"откр\w*|ачып|ачкыла\w*|покажи товар", " "));

    /// <summary>Номера «№1 · сайт» по видимым вариантам — после скрытия тех, что не открылись.</summary>
    private void RenumberPhotoChoices()
    {
        var i = 0;
        foreach (var c in _photoChoices.Where(c => c.Card.IsVisible))
            c.Label.Text = $"№{++i} · {c.Candidate.Source.Replace("интернет: ", "")}";
    }

    /// <summary>2026-10-10: запрос «покажи/проанализируй склад без фото» отвечает по полному локальному каталогу, а не по короткой AI-сводке.</summary>
    private static bool IsNoPhotoInventoryQuery(string question)
    {
        var text = (question ?? "").ToLowerInvariant();
        if (ProductActionPlan.IsExplicitPhotoWebSearchRequest(text))
            return false;
        var withoutPhoto = new[] { "без фото", "без фотограф", "нет фото", "нет фотограф", "without photo", "no photo", "fotoğrafsız", "rasmsiz", "rasm yo'q", "сүрөтү жок" }
            .Any(text.Contains);
        var asksForInventory = new[] { "склад", "товар", "спис", "перечис", "покаж", "вывед", "введ", "анализ", "какие", "inventory", "product", "list", "show", "analy" }
            .Any(text.Contains);
        return withoutPhoto && asksForInventory;
    }

    /// <summary>Полный отчёт по отсутствию фото. Используется локальная копия всего каталога; внешние сайты и AI не вызываются.</summary>
    private static string BuildNoPhotoInventoryReport(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = LocalProductRepository.Instance.LoadAllTiles()
            .Where(product => !product.IsService)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.Count == 0)
            return T("Локальный каталог пуст или ещё не загружен. Обновите каталог и повторите запрос.",
                "Жергиликтүү каталог бош же жүктөлө элек. Каталогду жаңыртып, кайра сураңыз.",
                "The local catalog is empty or has not loaded yet. Refresh the catalog and try again.",
                "Yerel katalog boş veya henüz yüklenmedi. Kataloğu yenileyip tekrar deneyin.",
                "Mahalliy katalog bo'sh yoki hali yuklanmagan. Katalogni yangilab qayta urinib ko'ring.");

        var missing = catalog
            .Where(product => string.IsNullOrWhiteSpace(product.ImageUrl) && string.IsNullOrWhiteSpace(product.ProductImagePath))
            .OrderBy(product => product.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (missing.Count == 0)
            return T($"Проверено товаров: {catalog.Count}. У всех товаров каталога есть фото.",
                $"Текшерилген товарлар: {catalog.Count}. Каталогдогу бардык товарларда сүрөт бар.",
                $"Products checked: {catalog.Count}. Every catalog product has a photo.",
                $"Kontrol edilen ürün: {catalog.Count}. Katalogdaki tüm ürünlerin fotoğrafı var.",
                $"Tekshirilgan mahsulotlar: {catalog.Count}. Katalogdagi barcha mahsulotlarda rasm bor.");

        static string Cell(string? value) => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(T("Товары без фото в локальном каталоге:", "Жергиликтүү каталогдогу сүрөтсүз товарлар:",
            "Products without photos in the local catalog:", "Yerel katalogda fotoğrafı olmayan ürünler:",
            "Mahalliy katalogdagi rasmsiz mahsulotlar:"));
        sb.AppendLine("| № | " + T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + " | "
                      + T("Штрихкод / артикул", "Штрихкод / артикул", "Barcode / article", "Barkod / stok kodu", "Shtrix-kod / artikul") + " | "
                      + T("Категория / бренд", "Категория / бренд", "Category / brand", "Kategori / marka", "Turkum / brend") + " | "
                      + T("Цена", "Баасы", "Price", "Fiyat", "Narx") + " | "
                      + T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq") + " |");
        sb.AppendLine("|---:|---|---|---|---:|---:|");
        for (var index = 0; index < missing.Count; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var product = missing[index];
            var barcode = string.Join(" / ", new[] { product.Barcode, product.Article }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var categoryBrand = string.Join(" / ", new[] { product.Category, product.Brand }.Where(value => !string.IsNullOrWhiteSpace(value)));
            sb.Append("| ").Append(index + 1).Append(" | ").Append(Cell(product.Title)).Append(" | ")
                .Append(Cell(barcode)).Append(" | ").Append(Cell(categoryBrand)).Append(" | ")
                .Append(Cell(product.PriceLineDisplay)).Append(" | ").Append(Cell(product.StockWithUnitText)).AppendLine(" |");
        }
        sb.AppendLine();
        sb.Append(T("Всего товаров без фото", "Сүрөтсүз товарлардын жалпы саны", "Products without photos", "Fotoğrafı olmayan ürün sayısı", "Rasmsiz mahsulotlar soni"))
            .Append(": ").Append(missing.Count).Append(" / ").Append(catalog.Count);
        return sb.ToString();
    }

    private static bool IsPhotoRequest(string text)
        => ProductActionPlan.IsExplicitPhotoWebSearchRequest(text);

    /// <summary>2026-10-05: «загрузи фото к товарам, у которых нет фото». Товары без фото → поиск по штрихкоду в
    /// открытых базах (ProductPhotoFinder) → найденные показываются с «Поставить» / «Поставить все»; остальные —
    /// с «📷 Добавить фото» (файл или камера, как на складе). На сервер фото уходит только по нажатию владельца.</summary>
    private async Task RunPhotoAssistantAsync(string question)
    {
        // 2026-10-05, владелец: «чтобы ИИ смог по команде найти фото по названию или штрихкоду товара в интернете, если в
        // базе нет». Названы товары («найди фото для кока колы», штрихкод) — ищем для них; иначе — для всех без фото.
        var named = ProductPhotoFinder.MatchRequest(question);
        // 2026-10-06, владелец: «при поиске в интернете отображай найденные фото товаров на выбор в чате». Названы 1–3 товара —
        // несколько вариантов фото каждого, ставит выбранное.
        if (named.Count is > 0 and <= 3)
        {
            await ShowPhotoChoicesAsync(named).ConfigureAwait(true);
            return;
        }
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

    /// <summary>Варианты фото товаров (до 8 на товар) — плиткой в чате, «Поставить это фото» под каждым.</summary>
    private async Task ShowPhotoChoicesAsync(IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> products)
    {
        var status = AddBubble(T($"Ищу фото на выбор: {string.Join(", ", products.Select(p => p.Title))} — в открытых базах и на сайтах магазинов…",
            $"Тандоо үчүн сүрөт издеп жатам: {string.Join(", ", products.Select(p => p.Title))} — ачык базалардан жана дүкөн сайттарынан…",
            $"Looking for photos to choose from: {string.Join(", ", products.Select(p => p.Title))} — in open databases and shop websites…",
            $"Seçmek için fotoğraf arıyorum: {string.Join(", ", products.Select(p => p.Title))} — açık veritabanlarında ve mağaza sitelerinde…",
            $"Tanlash uchun rasm qidiryapman: {string.Join(", ", products.Select(p => p.Title))} — ochiq bazalar va do'kon saytlarida…"), fromOwner: false);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var total = 0;
        _photoChoices.Clear();
        // 2026-10-06, владелец: «очень долго ищет фото, добавь индикатор загрузки в чат». Пока ищем — крутится значок и идут
        // секунды; товары ищутся одновременно, а не по очереди.
        var searchText = status.Text;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frame = 0;
        string[] frames = { "◐", "◓", "◑", "◒" };
        var spinner = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        spinner.Tick += (_, _) => status.Text = $"{frames[frame++ % frames.Length]} {searchText} {watch.Elapsed.TotalSeconds:0} "
                                                + T("с", "сек", "s", "sn", "s");
        spinner.Start();
        var searches = products.Select(p => Task.Run(() => ProductPhotoFinder.FindChoicesAsync(p, 8, ct), ct)).ToList();
        foreach (var (product, search) in products.Zip(searches))
        {
            List<ProductPhotoFinder.Candidate> choices;
            try
            {
                choices = await search.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                spinner.Stop();
                return;
            }
            total += choices.Count;
            var header = new TextBlock
            {
                Text = choices.Count > 0
                    ? T($"«{product.Title}» — выберите фото ({choices.Count}):", $"«{product.Title}» — сүрөт тандаңыз ({choices.Count}):", $"“{product.Title}” — choose a photo ({choices.Count}):",
                        $"«{product.Title}» — fotoğraf seçin ({choices.Count}):", $"«{product.Title}» — rasmni tanlang ({choices.Count}):")
                    : T($"«{product.Title}» — фото в интернете не нашлось. Сфотографируйте товар: «📷 Добавить фото» на складе.",
                        $"«{product.Title}» — интернеттен сүрөт табылган жок. Товарды сүрөткө тартыңыз: кампада «📷 Сүрөт кошуу».",
                        $"“{product.Title}” — no photo found online. Take a photo: “📷 Add photo” in the warehouse.",
                        $"«{product.Title}» — internette fotoğraf bulunamadı. Fotoğrafını çekin: depoda «📷 Fotoğraf ekle».",
                        $"«{product.Title}» — internetda rasm topilmadi. Suratga oling: omborda «📷 Rasm qo'shish»."),
                FontWeight = FontWeight.SemiBold, FontSize = 13.5, Margin = new Thickness(4, 4, 0, 4), TextWrapping = TextWrapping.Wrap,
            };
            Use(header, TextBlock.ForegroundProperty, "BrushText");
            _messages.Children.Add(header);
            if (choices.Count == 0)
                continue;
            var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            var buttons = new List<Button>();
            foreach (var candidate in choices)
            {
                var image = new Image { Width = 132, Height = 132, Stretch = Stretch.UniformToFill };
                Border? choiceCard = null;
                var source = new TextBlock { Text = candidate.Source, FontSize = 10.5, MaxWidth = 132, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(source, TextBlock.ForegroundProperty, "BrushTextSoft");
                var set = UiKit.Ghost(this, T("Поставить это фото", "Ушул сүрөттү коюу", "Use this photo", "Bu fotoğrafı kullan", "Shu rasmni qo'yish"));
                set.Height = 32;
                set.FontSize = 12;
                set.Padding = new Thickness(8, 0);
                // 2026-10-07: товара нет на складе (поиск по словам фразы) — фото только посмотреть, ставить некуда.
                set.IsVisible = candidate.Product.Id.Length > 0;
                buttons.Add(set);
                set.Click += async (_, _) =>
                {
                    foreach (var other in buttons.Where(b => b != set))
                        other.IsEnabled = false;
                    await ApplyPhotoAsync(set, candidate).ConfigureAwait(true);
                };
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
                    Child = new StackPanel { Spacing = 4, Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, source, set } },
                };
                Use(card, Border.BackgroundProperty, "BrushPanel");
                Use(card, Border.BorderBrushProperty, "BrushBorder");
                strip.Children.Add(card);
                choiceCard = card;
                if (candidate.Product.Id.Length > 0)
                    _photoChoices.Add((candidate, set, card, source));
                // Картинка в формате, который окно не показывает (avif и т. п.), — вариант убираем, а не оставляем пустую рамку.
                _ = LoadPreviewAsync(image, candidate.ImageUrl).ContinueWith(t =>
                {
                    if (!t.Result && choiceCard is not null)
                    {
                        choiceCard.IsVisible = false;
                        RenumberPhotoChoices();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            _messages.Children.Add(strip);
            RenumberPhotoChoices();
            ScrollToEnd();
        }
        spinner.Stop();
        PosLogger.Log($"ИИ-советник: фото на выбор — {total} вар. за {watch.ElapsedMilliseconds} мс.", "INFO");
        // 2026-10-06: при 0 вариантов не писать «Нашёл вариантов фото: 0. Нажмите «Поставить…»» — нечего нажимать.
        if (total == 0)
        {
            status.Text = T("Фото в интернете не нашлось.", "Интернеттен сүрөт табылган жок.", "No photos found online.",
                "Internette fotoğraf bulunamadı.", "Internetda rasm topilmadi.");
            ScrollToEnd();
            return;
        }
        status.Text = T($"Нашёл вариантов фото: {total}. Нажмите «Поставить это фото» под подходящим — фото уйдёт в карточку товара на сервере.",
            $"Сүрөт варианттары: {total}. Ылайыктуусунун астындагы «Ушул сүрөттү коюу» басыңыз — сүрөт сервердеги товар карточкасына кетет.",
            $"Photo options found: {total}. Press “Use this photo” under the right one — it goes to the product card on the server.",
            $"Bulunan fotoğraf seçenekleri: {total}. Uygun olanın altındaki «Bu fotoğrafı kullan»a basın — fotoğraf sunucudaki ürün kartına gider.",
            $"Topilgan rasm variantlari: {total}. Mosining ostidagi «Shu rasmni qo'yish»ni bosing — rasm serverdagi mahsulot kartasiga ketadi.");
        ScrollToEnd();
    }

    private static async Task<bool> LoadPreviewAsync(Image image, string url)
    {
        var bytes = await ProductPhotoFinder.DownloadAsync(url, CancellationToken.None).ConfigureAwait(true);
        if (bytes is not { Length: > 0 })
            return false;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            image.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 224);
            return true;
        }
        catch
        {
            // картинка не читается — остаётся пустая рамка
            return false;
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
            if (ok)
                _lastPhotoProduct = candidate.Product;
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
    private void AddProductPhotos(string answer) => AddProductPhotoStrip(OwnerAiContext.FindMentionedProducts(answer));

    /// <summary>Фото товаров склада в чате (2026-10-06: и по списку — для «покажи фото Mars» голосом).</summary>
    private void AddProductPhotoStrip(IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> products)
    {
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

    /// <summary>2026-10-21: пункт меню ⚙, который экранный диктор и UIA-автоматизация могут «нажать» (Invoke) — у MenuItem в
    /// Avalonia 11.2 этого нет (пакет 7, хвост Советника). Вид — обычного MenuItem.</summary>
    private sealed class InvokableMenuItem : MenuItem
    {
        protected override Type StyleKeyOverride => typeof(MenuItem);

        protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer : Avalonia.Automation.Peers.MenuItemAutomationPeer, Avalonia.Automation.Provider.IInvokeProvider
        {
            private readonly MenuItem _item;

            public Peer(MenuItem owner) : base(owner) => _item = owner;

            public void Invoke()
            {
                if (_item.IsEffectivelyEnabled)
                    _item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            }
        }
    }
}
