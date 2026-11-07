﻿using NurMarketKassa.Core.Application;
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
    // 2026-10-30 (приёмка 1.17.66): спрятанный советник с открытым окном закупки, неотправленным текстом, прикреплённым файлом или карточкой
    // «Выполнить / Провести закупку» не закрывается (раньше TrimHiddenSections закрывал его, окно закупки закрывалось вместе с ним и ввод пропадал).
    public bool KeepAliveWhenHidden => _busy || _live is not null || _recorder is not null || _liveBar.IsVisible
        || _editorsOpen > 0 || _pendingProductApply is not null || _attachments.Count > 0 || !string.IsNullOrWhiteSpace(_input.Text);

    /// <summary>Сколько окон закупки сейчас открыто из советника (ShowEditorDialogAsync).</summary>
    private int _editorsOpen;

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

    // 2026-11-06 (аудит №4, Л4-26): токен жизни окна — отменяется при закрытии, и фоновые запросы (табель, сроки годности, проверка ключа, штрихкод,
    // звонок) не доживают до закрытого окна; раньше они шли с CancellationToken.None. _closed останавливает таймеры «Думаю…» и «крутилки».
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationToken LifetimeToken => _lifetime.Token;
    private volatile bool _closed;

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
            _ = TelegramAiChat.TimesheetProvider?.Invoke(DateTime.Today, DateTime.Today, LifetimeToken);
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
        // 2026-10-26: несколько ключей из разных Google-аккаунтов (бесплатный лимит считается на аккаунт) — отдельное окно списка.
        var manyKeys = UiKit.Ghost(this, T("Несколько ключей (разные аккаунты)…", "Бир нече ачкыч (ар башка аккаунттар)…", "Several keys (different accounts)…",
            "Birden çok anahtar (farklı hesaplar)…", "Bir nechta kalit (turli hisoblar)…"));
        manyKeys.HorizontalAlignment = HorizontalAlignment.Left;
        manyKeys.Click += async (_, _) =>
        {
            await GeminiKeysWindow.ShowAsync(this).ConfigureAwait(true);
            RefreshKeyCard();
        };
        keyStack.Children.Add(manyKeys);
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
        // 2026-10-29, жалоба «в ИИ не нажимается вставка накладных»: Ctrl+V с картинкой/файлом из буфера и перетаскивание файла на окно —
        // как «скрепка» (раньше вложить накладную можно было только через окно выбора файла).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0)
                _ = TryPasteAttachmentAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.Data.Contains(DataFormats.Files))
                return;
            e.Handled = true;
            _ = AttachDroppedAsync(e.Data.GetFiles());
        });
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
        _attachButton = attach; // 2026-10-30: скрывается тумблером «Читать накладные»
        attach.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(attach, T("Приложить фото: накладная поставщика — ИИ оприходует товары с наценкой; фото товара — опишет и заполнит карточку. Можно перетащить файл в окно или нажать Ctrl+V",
            "Сүрөт тиркөө: жеткирүүчүнүн накладнойу — ИИ товарларды үстөк менен кириштейт; товардын сүрөтү — сүрөттөп, карточкасын толтурат. Файлды терезеге сүйрөп таштаса же Ctrl+V басса болот",
            "Attach a photo: a supplier invoice — the AI receives the goods with a markup; a product photo — it describes it and fills in the card. You can also drag a file into the window or press Ctrl+V",
            "Fotoğraf ekle: tedarikçi faturası — yapay zekâ ürünleri kâr payıyla stoğa alır; ürün fotoğrafı — tanımlar ve kartı doldurur. Dosyayı pencereye sürükleyebilir veya Ctrl+V'ye basabilirsiniz",
            "Surat biriktirish: yetkazib beruvchi yuk xati — SI mahsulotlarni ustama bilan kirim qiladi; mahsulot surati — tavsiflab, kartani to'ldiradi. Faylni oynaga sudrab tashlash yoki Ctrl+V bosish mumkin"));
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
            FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
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
        // 2026-10-30: тумблеры возможностей Нур Советника — вид окна живёт вместе с настройками (облако, соседнее окно).
        WatchCapabilities();

        // В программе владельца своя локальная история продаж — подтягиваем свежие чеки с сервера, чтобы
        // сводка для советника (выручка, лучшие товары) была по сегодняшним данным.
        Opened += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                // 2026-10-21 (О3): сроки годности — заранее, пока владелец печатает вопрос.
                _ = ProductExpiryIndex.RefreshAsync(false, LifetimeToken);
                await SalesHistoryBackfill.RunAsync().ConfigureAwait(false);
                // 2026-10-05: сводку для ИИ — заранее, чтобы первый вопрос (особенно голосом) не ждал её сборки.
                await Dispatcher.UIThread.InvokeAsync(() => _ = GetSummaryAsync(SummaryParts.Periods | SummaryParts.Debts)); // 2026-10-27: прогреваем только частые части
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: история продаж не обновлена ({ex.Message}).", "WARNING");
            }
        });
        Closed += (_, _) =>
        {
            _closed = true; // 2026-11-06 (Л4-26): таймеры «Думаю…» и «крутилка» перестают держать закрытое окно
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { /* уже отменён */ }
            EndLive(null);
            SaveCurrentChat();
            ChatStore.Flush(TimeSpan.FromSeconds(2));
            ChatStore.Release(); // 2026-11-06 (В16, память): история разговоров (до ~12 МБ строк) не держится в памяти после закрытия окна — следующее открытие прочитает файл
            _cts?.Cancel();
            _recorder?.Dispose();
            _recorder = null;
            VoiceChatPlayer.Stop();
            ReleasePhotoMemory(); // 2026-11-05 (Р3-01): картинки ленты фото и кэш скачанных картинок
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
        _keyCard.IsVisible = !TelegramAiChat.IsConfigured && AdvisorCapabilities.MasterOn;
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
        (bool ok, string message) keyCheck;
        try
        {
            keyCheck = await TelegramAiChat.TestKeyAsync(key, LifetimeToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_closed)
        {
            return; // 2026-11-06 (Л4-26): окно закрыли, пока проверялся ключ
        }
        var (ok, message) = keyCheck;
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
        _invoiceTotals = null; // 2026-10-31: новый чат — итоги прежней накладной не переносим
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
        // 2026-10-30: Нур Советник выключен владельцем (Настройки → Операции → Нур Советник) — вежливый ответ, ничего не уходит в ИИ.
        if (!AdvisorCapabilities.MasterOn)
        {
            if (question.Length > 0)
                AddBubble(AdvisorCapabilities.BlockedText(), fromOwner: false);
            return;
        }
        // 2026-10-30: «Читать накладные: фото и PDF» выключено — вложения в ИИ не отправляются.
        if (_attachments.Count > 0 && !Cap("inv.read"))
        {
            ClearAttachment();
            SayDisabled("inv.read");
            return;
        }
        // 2026-10-29: без ключа ИИ фото с накладной раньше молча «не отправлялось» (кнопки живые, ответа нет) — говорим причину.
        if (_attachments.Count > 0 && !_busy && !TelegramAiChat.IsConfigured)
        {
            AddBubble(T("Чтобы ИИ прочитал накладную, сначала вставьте ключ ИИ (карточка вверху ленты).", "ЖИ накладнойду окушу үчүн алгач ЖИ ачкычын чаптаңыз (лентанын жогору жагындагы карточка).",
                "For the AI to read the invoice, first paste the AI key (the card at the top of the chat).", "Yapay zekânın faturayı okuması için önce yapay zekâ anahtarını yapıştırın (sohbetin üstündeki kart).",
                "SI yuk xatini o'qishi uchun avval SI kalitini qo'ying (lentaning yuqorisidagi karta)."), fromOwner: false);
            return;
        }
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
        // 2026-11-05: команда смены языка («отвечай по-кыргызски», «переведи на русский») — выполняет программа, в ИИ не уходит.
        if (image is null && TryHandleLanguageCommand(question))
        {
            _busy = false;
            RefreshKeyCard();
            SaveCurrentChat();
            ScrollToEnd();
            _input.Focus();
            return;
        }
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
        // 2026-10-27, жалоба владельца («неправильно открывает разделы/вкладки, врёт „открыла“, долго думает»): «открой / перейди / зайди …»
        // разбирает локальный маршрутизатор БЕЗ нейросети (за миллисекунды): раздел или вкладка («Склад → Приёмка»), при ничьей — кнопки
        // вариантов, а ответ строится ИЗ ФАКТА открытия. Остаток фразы с вопросом по данным («…и скажи выручку») продолжит нейросеть.
        string? navNote = null;
        if (image is null && ProductActionPlan.OpenNavTarget is not null && AdvisorNavRouter.Parse(question) is { Kind: not AdvisorNavRouter.RouteKind.NotNav } navRoute
            && RouterOwnsPhrase(navRoute, question))
        {
            try
            {
                var reply = await RunNavRouteAsync(navRoute).ConfigureAwait(true);
                if (reply.Kind == AdvisorTurn.ReplyKind.Opened && navRoute.Strong && navRoute.Residual.Count >= 2)
                    navNote = "\n(Программа уже сделала: " + reply.Text + " Не говори, что открываешь или открыл что-либо; ответь только на остальную часть вопроса.)";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: переход по фразе не выполнен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                AddBubble(T("Не получилось открыть раздел — программа сообщила об ошибке.", "Бөлүмдү ачуу болгон жок — программа ката кабарлады.", "Couldn't open the section — the program reported an error.",
                    "Bölüm açılamadı — program hata bildirdi.", "Bo'limni ochib bo'lmadi — dastur xato haqida xabar berdi."), fromOwner: false);
            }
            if (navNote is null)
            {
                if (gen == _gen)
                {
                    _busy = false;
                    RefreshKeyCard();
                    SaveCurrentChat();
                    ScrollToEnd();
                    _input.Focus();
                }
                return;
            }
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
        // 2026-10-30: «найди товары с 0 остатком», «без штрихкода», «дорогие товары» — списки по складу считает программа (тумблер «read.stocklists»).
        if (image is null && await TryStockQueryAsync(question, access).ConfigureAwait(true))
        {
            if (gen == _gen)
            {
                _busy = false;
                RefreshKeyCard();
                SaveCurrentChat();
                ScrollToEnd();
                _input.Focus();
            }
            return;
        }
        // 2026-10-30: «что за товар 4600…», «штрихкод Марса» — ответ программы по складу и справочнику NurCRM, без нейросети (тумблер «bc.directory»).
        if (image is null)
        {
            string? barcodeText = null;
            try
            {
                barcodeText = await TryBarcodeAnswerAsync(question, access, LifetimeToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_closed)
            {
                return; // 2026-11-06 (Л4-26): окно закрыли, пока искали штрихкод
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: штрихкод не определён программой ({ex.GetType().Name}: {ex.Message}) — отвечает ИИ.", "WARNING");
            }
            if (barcodeText is not null)
            {
                if (gen == _gen)
                {
                    AddBubble(barcodeText, fromOwner: false);
                    _busy = false;
                    RefreshKeyCard();
                    SaveCurrentChat();
                    ScrollToEnd();
                    _input.Focus();
                }
                return;
            }
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
            // 2026-10-27: части сводки — по смыслу вопроса (с фото накладной — все, как раньше).
            var summaryParts = image is null ? AdvisorIntent.PartsFor(question) : SummaryParts.All;
            var (sections, debtorNames) = await GetSummaryAsync(summaryParts).ConfigureAwait(true);
            var summaryMs = watch.ElapsedMilliseconds;
            AdvisorTurn.Perf("сводка", summaryMs, $"части: {summaryParts}");
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
            askText += navNote;
            var attachList = attachments.Select(a => (a.Data, a.Mime)).ToList();
            TelegramAiChat.OwnerAskResult result;
            if (attachments.Count == 0)
            {
                // 2026-10-27: общий предел ≈12 с на вопрос (сводка тоже в нём): не успели — «ИИ долго отвечает — повторить?» с кнопкой.
                var limit = TimeSpan.FromSeconds(Math.Max(6, 12 - watch.Elapsed.TotalSeconds));
                var (timed, slow) = await AdvisorTurn.WithBudget(c => TelegramAiChat.AskOwnerAppAsync(askText, shop, access, c, attachList,
                    stage => Dispatcher.UIThread.Post(() => setStage(stage)), forceFull: ForceSections(question), budget: limit), limit + TimeSpan.FromSeconds(1), token).ConfigureAwait(true);
                if (gen != _gen)
                    return;
                if (slow || timed is null)
                {
                    AdvisorTurn.Perf("предел ожидания", watch.ElapsedMilliseconds, "ответ не пришёл вовремя");
                    ShowFriendlyError(thinking, "slow", question);
                    SaveCurrentChat();
                    return;
                }
                result = timed;
            }
            else
                result = await TelegramAiChat.AskOwnerAppAsync(askText, shop, access, token, attachList,
                    stage => Dispatcher.UIThread.Post(() => setStage(stage)), forceFull: ForceSections(question)).ConfigureAwait(true);
            var answer = result.Answer;
            var error = result.Error;
            if (gen != _gen)
                return;
            PosLogger.Log($"ADVISOR_PERF этапы — сводка {summaryMs} мс (кэш {(summaryMs < 50 ? "да" : "нет")}), товары из вопроса {questionMs} мс, {result.Timings}{(speak ? " (голосом)" : "")}.", "INFO");
            Dictionary<string, bool>? botChange = null;
            Dictionary<string, object?>? scenario = null;
            // 2026-10-06, владелец: «к ИИ дай полный доступ к товарам» — строки «ТОВАР: {…}» → карточка «Выполнить».
            List<ProductActionPlan.Step>? productSteps = null;
            IReadOnlyList<string> skippedItems = Array.Empty<string>();
            List<(string Name, string? Phone, double Amount)>? debtReminders = null;
            var blockedCaps = new List<string>();
            if (answer is { Length: > 0 })
            {
                (answer, botChange) = ExtractBotChange(answer);
                (answer, scenario) = ExtractScenario(answer);
                // 2026-10-19, ревью: подсказка поставщика — от накладной: заменяется новой строкой «ПОСТАВЩИК» или новым фото
                // (другая накладная), а ответ без неё («поставь наценку 25 %») прежнюю подсказку не стирает.
                if (ProductActionPlan.SupplierHint(answer) is var hint && (hint.Name is not null || hint.Inn is not null || attachments.Count > 0))
                    _invoiceSupplierHint = hint;
                // 2026-10-31: итоги накладной (итог/НДС/скидка/позиции из документа, блок ИИ) — для контрольной сверки в окне закупки; как и поставщик, меняются новым фото.
                // 2026-10-31 (приёмка 1.17.70): ответ без «ИТОГ НАКЛАДНОЙ» («наценка 30 %» после фото) итог документа не стирает — прежние
                // Total/Vat/Discount/Positions сохраняются, обновляются только числа ИИ; полная замена — новым фото или новым итогом; сброс — «Новый чат».
                if (ProductActionPlan.InvoiceTotalsHint(answer) is var totalsHint && (totalsHint is not null || attachments.Count > 0))
                {
                    if (attachments.Count > 0 || _invoiceTotals is null || totalsHint is null || totalsHint.HasDocumentTotal)
                        _invoiceTotals = totalsHint;
                    else
                        _invoiceTotals = _invoiceTotals with
                        {
                            AiLinesTotal = totalsHint.AiLinesTotal ?? _invoiceTotals.AiLinesTotal,
                            AiPositions = totalsHint.AiPositions ?? _invoiceTotals.AiPositions,
                        };
                }
                (answer, productSteps, skippedItems) = ProductActionPlan.Extract(answer, question);
                // 2026-10-30: действия выключенных возможностей отброшены разбором — скажем об этом (ниже), а не промолчим.
                blockedCaps = ProductActionPlan.LastBlocked.ToList();
                (answer, debtReminders) = ExtractDebtReminders(answer);
                if (answer.Trim().Length == 0 && blockedCaps.Count > 0)
                    answer = AdvisorCapabilities.BlockedText(blockedCaps[0]);
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
                if (!Cap("act.bot"))
                    SayDisabled("act.bot");
                else if (access.Settings)
                    AddBotChangeCard(botChange);
                else
                    AddBubble(NoAccessText(T("настройкам Телеграм-бота", "Телеграм-боттун жөндөөлөрүнө", "the Telegram bot settings", "Telegram botu ayarlarına", "Telegram bot sozlamalariga"), "Настройки"), fromOwner: false);
            }
            if (scenario is not null && access.Settings)
            {
                if (Cap("act.bot"))
                    AddScenarioCard(scenario);
                else if (botChange is not { Count: > 0 })
                    SayDisabled("act.bot");
            }
            foreach (var blockedId in blockedCaps.Where(id => id != "act.bot"))
                SayDisabled(blockedId);
            if (productSteps is { Count: > 0 })
            {
                if (access.Products)
                    ShowProductSteps(productSteps, question);
                else
                    AddBubble(NoAccessText(T("складу и изменению товаров", "кампага жана товарларды өзгөртүүгө", "the warehouse and product changes", "depoya ve ürün değişikliklerine", "omborga va mahsulotlarni o'zgartirishga"), "Склад"), fromOwner: false);
            }
            if (debtReminders is { Count: > 0 } && access.Clients)
            {
                if (Cap("rem.debtors"))
                    AddDebtReminderCard(debtReminders);
                else
                    SayDisabled("rem.debtors");
            }
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
        NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "ai-chats.json");

    // 2026-11-06 (аудит №4/№5, Л4-13): раньше после КАЖДОГО ответа история читалась целиком и писалась на диск (сброс + копия .bak) в потоке окна.
    // Теперь — в памяти, запись в фоне одной сливающейся операцией (DeferredJsonList); при закрытии окна ждём конец записи (до 2 с).
    private static readonly DeferredJsonList<SavedChat> ChatStore = new(() => ChatsPath, 30);

    private static List<SavedChat> LoadChats() => ChatStore.Load();

    private static void StoreChats(List<SavedChat> chats) => ChatStore.Store(chats); // 2026-11-01 (аудит 10 из 10, Н-07): запись атомарная, внутри DeferredJsonList

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
        var chats = LoadChats().Where(c => query.Length == 0 || SearchMatch.Matches(query, c.Title)
                                           || c.Lines.Any(l => SearchMatch.Matches(query, l.Text))).ToList();
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
                var head = new TextBlock { Text = group, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, lastGroup is null ? 2 : 12, 0, 4) };
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
}
