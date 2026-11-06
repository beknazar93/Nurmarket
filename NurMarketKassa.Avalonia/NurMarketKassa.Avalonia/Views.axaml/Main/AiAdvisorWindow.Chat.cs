using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-06 (В13, Л4-17): разбор AiAdvisorWindow.cs на части БЕЗ изменения логики — шапка, приветствие, лента сообщений, ввод, вложения (строки 1284–2549 прежнего файла).</summary>
public sealed partial class AiAdvisorWindow
{
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
        _historySearch = UiKit.Search(this, T("Найти разговор…", "Маекти табуу…", "Find a chat…", "Sohbet bul…", "Suhbatni topish…"), 36);
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
        Item(T("Ключи Gemini (несколько аккаунтов)…", "Gemini ачкычтары (бир нече аккаунт)…", "Gemini keys (several accounts)…", "Gemini anahtarları (birden çok hesap)…", "Gemini kalitlari (bir nechta hisob)…"),
            () => _ = GeminiKeysWindow.ShowAsync(this).ContinueWith(_ => Dispatcher.UIThread.Post(RefreshKeyCard), TaskScheduler.Default));
        Item("☀ " + T("Утренний отчёт…", "Эртең мененки отчёт…", "Morning report…", "Sabah raporu…", "Ertalabki hisobot…"), () => _ = MorningReportSettingsWindow.ShowAsync(this));
        Item("✉ " + T("Напоминания должникам…", "Карыздарга эскертүүлөр…", "Debt reminders…", "Borç hatırlatmaları…", "Qarz eslatmalari…"), OpenDebtReminders);
        // 2026-11-05 (волна 2-А): просрочка при продаже, автозаказ v2, подсказки цены, оповещения «Сообщать в Telegram, если…».
        Item("⚙ " + T("Автоматизация магазина…", "Дүкөндү автоматташтыруу…", "Shop automation…", "Mağaza otomasyonu…", "Do'kon avtomatlashtirish…"), () => _ = ShopAutomationWindow.ShowAsync(this));
        Item("🛡 " + T("Подозрительные операции…", "Шектүү операциялар…", "Suspicious operations…", "Şüpheli işlemler…", "Shubhali amallar…"), () => _ = FraudWindow.ShowAsync(this)); // 2026-11-05 (волна 2-Б, функция 2)
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
        // 2026-10-30: Нур Советник выключен владельцем — вместо приветствия одна строка, где включить.
        if (!AdvisorCapabilities.MasterOn)
        {
            var off = new TextBlock
            {
                Text = AdvisorCapabilities.BlockedText(), FontSize = 15, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, MaxWidth = 560, Margin = new Thickness(0, 60, 0, 0),
            };
            Use(off, TextBlock.ForegroundProperty, "BrushTextSoft");
            _welcome = off;
            _messages.Children.Add(off);
            return;
        }
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
        // 2026-10-30: голос выключен тумблерами — про микрофон и «Разговор» в приветствии не пишем.
        if (!Cap("voice.ask") && !Cap("voice.live") && intro.Text is { } introText)
            intro.Text = introText.Split(new[] { " Можно голосом", " Үн менен да", " Voice works", " Sesle de", " Ovoz bilan" }, StringSplitOptions.None)[0];
        Use(intro, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(intro);
        if (_quick.Parent is Panel old)
            old.Children.Remove(_quick);
        _quick.HorizontalAlignment = HorizontalAlignment.Center;
        _quick.Margin = new Thickness(0, 14, 0, 0);
        _quick.MaxWidth = 800;
        panel.Children.Add(_quick);
        // 2026-10-21 (И3): утренний отчёт за сегодня — сразу на пустом экране (как «For you» у лучших помощников).
        if (Cap("pro.morning") && OwnerMorningReport.Latest is { } morning && morning.BuiltAt.Date == DateTime.Today)
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
        if (_morningShownFor == r.BuiltAt || !Cap("pro.morning"))
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
                if (body.Text != thinking || !_messages.Children.Contains(row) || _closed)
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
        var money = d.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " " + NurMarketKassa.Services.MoneyText.Currency; // 2026-11-01 (Д2-04): валюта на языке интерфейса, не зашитое «сом»
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
            var sum = new TextBlock { Text = d.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " " + NurMarketKassa.Services.MoneyText.Currency, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
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

    /// <summary>2026-10-27, жалоба «очень долго думает» (журнал: «сводка по частям (11) за 6042 мс» на КАЖДЫЙ вопрос): части сводки собираются
    /// лениво — только те, что нужны вопросу (<see cref="AdvisorIntent.PartsFor"/>; переход по разделам и болтовня — ни одной из сети),
    /// параллельно, и каждая часть живёт в кэше 60 с (повторный вопрос не ходит на сервер). Часть не ответила за 6 с — идём без неё
    /// (она доберётся в кэш сама и будет в следующем вопросе).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Task Task)> PartCache = new();

    private static readonly TimeSpan PartTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PartWait = TimeSpan.FromSeconds(6);

    private static Task<T> CachedPart<T>(string name, AiAccess access, Func<Task<T>> factory)
    {
        var key = name + "|" + access.GetHashCode();
        if (PartCache.TryGetValue(key, out var hit) && hit.Task is Task<T> cached && !cached.IsFaulted && !cached.IsCanceled && DateTime.UtcNow - hit.At < PartTtl)
            return cached;
        var task = factory();
        PartCache[key] = (DateTime.UtcNow, task);
        return task;
    }

    /// <summary>Сводка магазина для ИИ. <paramref name="parts"/> — какие сетевые части нужны (по умолчанию все: разговор голосом и прогрев при открытии).</summary>
    private Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> GetSummaryAsync(SummaryParts parts = SummaryParts.All) =>
        BuildSummaryAsync(CurrentAccess(), parts);

    private static async Task<(IReadOnlyList<AiSection> Sections, IReadOnlyDictionary<int, string> Names)> BuildSummaryAsync(AiAccess access, SummaryParts parts)
    {
        var ct = CancellationToken.None;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 2026-10-21 (И5): сравнение периодов считает программа (тот же отчёт сервера, что «Сводка»).
        var periods = parts.HasFlag(SummaryParts.Periods) ? CachedPart("periods", access, () => OwnerPeriodStats.GetAsync(ct)) : null;
        // «Я должен кому-то или мне должны?» — итоги долгов клиентов (точно, как «Сводка», О2) и поставщиков.
        var debts = parts.HasFlag(SummaryParts.Debts) ? CachedPart("debts", access, () => OwnerAiContext.DebtTotalsSectionAsync(ct)) : null;
        // «Дай список клиентов-должников»: должники под кодами [Д1]… — только с правом «Клиенты» (О4).
        var debtors = access.Clients && parts.HasFlag(SummaryParts.Debtors) ? CachedPart("debtors", access, () => OwnerAiContext.DebtorsSectionAsync(ct)) : null;
        // 2026-10-05, владелец: «дай доступ к ABC-анализу ИИ».
        var abc = parts.HasFlag(SummaryParts.Abc) ? CachedPart("abc", access, () => OwnerAiContext.AbcSectionAsync(ct)) : null;
        // 2026-10-05, владелец: «анализ продаж, склада, клиентов, заказов — чтобы предлагать акции на проблемные товары».
        var analysis = access.Products && parts.HasFlag(SummaryParts.Analysis) ? CachedPart("analysis", access, () => OwnerAiContext.AnalysisSectionsAsync(ct)) : null;
        // 2026-10-05, владелец: «добавь возможность ИИ управлять ботом» — состояние функций бота на сервере.
        var bot = access.Settings && parts.HasFlag(SummaryParts.Bot) ? CachedPart("bot", access, () => BuildBotStateAsync(ct)) : null;
        // 2026-10-05, ТЗ часть 7: итоги допродажи по всем кассам компании (сервер выложил 05.10).
        var upsell = parts.HasFlag(SummaryParts.Upsell) ? CachedPart("upsell", access, () => BuildUpsellAsync(ct)) : null;
        // 2026-10-06, лог владельца (23:10, сервер не отвечал после запуска): одна упавшая часть роняла всю сводку — ИИ
        // оставался без данных магазина. Теперь упавшая часть пропускается, остальные идут в сводку.
        static async Task<T?> Part<T>(Task<T>? task, string name) where T : class
        {
            if (task is null)
                return null;
            try
            {
                if (await Task.WhenAny(task, Task.Delay(PartWait)).ConfigureAwait(false) != task)
                {
                    PosLogger.Log($"ИИ-советник: часть сводки «{name}» не успела за {PartWait.TotalSeconds:0} с — отвечаю без неё.", "WARNING");
                    return null;
                }
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
            if (debtors is not null)
            {
                if (await Task.WhenAny(debtors, Task.Delay(PartWait)).ConfigureAwait(false) == debtors)
                {
                    var (section, n) = await debtors.ConfigureAwait(false);
                    if (section is not null)
                        list.Add(section);
                    names = n;
                }
                else
                    PosLogger.Log($"ИИ-советник: часть сводки «должники» не успела за {PartWait.TotalSeconds:0} с — отвечаю без неё.", "WARNING");
            }
            // Коды должников [Д1] в прежних ответах раскрываются именами — берём из кэша, даже если этот вопрос должников не просил.
            else if (access.Clients && PartCache.TryGetValue("debtors|" + access.GetHashCode(), out var old)
                     && old.Task is Task<(AiSection? Section, IReadOnlyDictionary<int, string> Names)> { IsCompletedSuccessfully: true } done)
                names = done.Result.Names;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: часть сводки «должники» не собрана ({ex.Message}).", "WARNING");
        }
        if (await Part(bot, "бот").ConfigureAwait(false) is { Length: > 0 } b)
            list.Add(new AiSection("bot", "Телеграм-бот", 9, b, SourceSection: "telegrambot", SourceLabel: "Телеграм-бот"));
        if (await Part(upsell, "допродажа").ConfigureAwait(false) is { Length: > 0 } u)
            list.Add(new AiSection("upsell", "Допродажа", 9, u));
        PosLogger.Log($"ИИ-советник: сводка по частям ({list.Count}; запрошено: {parts}) за {watch.ElapsedMilliseconds} мс.", "INFO");
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
                await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                if (!await AddAttachmentAsync(file.Name, stream).ConfigureAwait(true))
                    break;
            }
            RenderAttachments();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото не прикреплено ({ex.Message}).", "WARNING");
            // 2026-10-29, жалоба «в ИИ не нажимается вставка накладных»: раньше сбой выбора файла был виден только в журнале — кнопка выглядела мёртвой.
            ShowAttachFailed();
        }
    }

    /// <summary>2026-10-29: одно вложение (из окна выбора, перетаскивания или Ctrl+V) с общими проверками: число страниц, размер, тип.
    /// false — больше страниц не брать (предел достигнут); электронная накладная (Excel/CSV/XML) к ИИ не прикладывается — подсказка и кнопка «Склад → Приёмка».</summary>
    private async Task<bool> AddAttachmentAsync(string name, Stream stream)
    {
        if (_attachments.Count >= MaxAttachments)
        {
            AddBubble(T($"Не больше {MaxAttachments} страниц за раз.", $"Бир жолу {MaxAttachments} барактан ашпайт.", $"No more than {MaxAttachments} pages at a time.",
                $"Tek seferde en fazla {MaxAttachments} sayfa.", $"Bir martada {MaxAttachments} sahifadan ko'p emas."), fromOwner: false);
            return false;
        }
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls" or ".csv" or ".tsv" or ".xml" or ".txt")
        {
            var hint = AddBubble(T($"«{name}» — это электронная накладная (таблица). ИИ читает фото и PDF, а таблицу загружают в «Склад → Приёмка» кнопкой «Загрузить накладную» — там же её можно просто перетащить в окно.",
                $"«{name}» — электрондук накладной (таблица). ЖИ сүрөт жана PDF окуйт, ал эми таблицаны «Кампа → Кабыл алуу» бөлүмүндөгү «Накладнойду жүктөө» баскычы менен жүктөйт — ошол жерге жөн эле сүйрөп таштасаңыз да болот.",
                $"“{name}” is an electronic invoice (a table). The AI reads photos and PDFs; load a table in Warehouse → Receiving with the “Load invoice” button — you can also drag it into that window.",
                $"«{name}» elektronik bir fatura (tablo). Yapay zekâ fotoğraf ve PDF okur; tabloyu Depo → Mal kabul bölümünde «Fatura yükle» düğmesiyle yükleyin — pencereye sürükleyip bırakabilirsiniz.",
                $"«{name}» — elektron yuk xati (jadval). SI rasm va PDF o'qiydi; jadvalni Ombor → Qabul qilish bo'limida «Yuk xatini yuklash» tugmasi bilan yuklang — oynaga sudrab tashlasangiz ham bo'ladi."), fromOwner: false);
            if (ProductActionPlan.OpenNavTarget is not null && AdvisorNavRouter.ById("warehouse.receiving") is { } receiving)
                AddNavButtons(hint, new[] { receiving });
            return true;
        }
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".pdf"))
        {
            AddBubble(T($"«{name}» не подходит: приложите фото (JPG, PNG, WEBP) или PDF накладной.", $"«{name}» ылайыксыз: накладнойдун сүрөтүн (JPG, PNG, WEBP) же PDF тиркеңиз.",
                $"“{name}” doesn't fit: attach a photo (JPG, PNG, WEBP) or a PDF of the invoice.", $"«{name}» uygun değil: faturanın fotoğrafını (JPG, PNG, WEBP) veya PDF'ini ekleyin.",
                $"«{name}» mos emas: yuk xatining rasmini (JPG, PNG, WEBP) yoki PDF'ini biriktiring."), fromOwner: false);
            return true;
        }
        // 2026-10-30: «Читать накладные: фото и PDF» выключено — файл в ИИ не уходит.
        if (!Cap("inv.read"))
        {
            SayDisabled("inv.read");
            return false;
        }
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(true);
        if (ms.Length > 15_000_000 || _attachments.Sum(a => (long)a.Data.Length) + ms.Length > 40_000_000)
        {
            AddBubble(T($"«{name}» слишком большой (больше 15 МБ, или всего больше 40 МБ) — сожмите или сфотографируйте заново.",
                $"«{name}» өтө чоң (15 МБдан же жалпы 40 МБдан ашык) — кичирейтиңиз же кайра тартыңыз.",
                $"“{name}” is too big (over 15 MB, or over 40 MB in total) — shrink it or retake it.",
                $"«{name}» çok büyük (15 MB'tan veya toplamda 40 MB'tan fazla) — küçültün veya yeniden çekin.",
                $"«{name}» juda katta (15 MB dan yoki jami 40 MB dan ortiq) — kichraytiring yoki qayta oling."), fromOwner: false);
            return true;
        }
        _attachments.Add((ms.ToArray(), ext switch { ".png" => "image/png", ".webp" => "image/webp", ".pdf" => "application/pdf", _ => "image/jpeg" }, name));
        return true;
    }

    private void ShowAttachFailed() =>
        AddBubble(T("Не удалось приложить файл. Попробуйте ещё раз, перетащите фото в окно или вставьте его через Ctrl+V.",
            "Файлды тиркөө мүмкүн болгон жок. Кайра аракет кылыңыз, сүрөттү терезеге сүйрөп таштаңыз же Ctrl+V менен чаптаңыз.",
            "Couldn't attach the file. Try again, drag the photo into the window, or paste it with Ctrl+V.",
            "Dosya eklenemedi. Tekrar deneyin, fotoğrafı pencereye sürükleyin veya Ctrl+V ile yapıştırın.",
            "Faylni biriktirib bo'lmadi. Qayta urinib ko'ring, rasmni oynaga sudrab tashlang yoki Ctrl+V bilan qo'ying."), fromOwner: false);

    /// <summary>2026-10-29: перетаскивание файлов из проводника на окно советника — как «скрепка».</summary>
    private async Task AttachDroppedAsync(IEnumerable<Avalonia.Platform.Storage.IStorageItem>? items)
    {
        try
        {
            foreach (var file in (items ?? Array.Empty<Avalonia.Platform.Storage.IStorageItem>()).OfType<Avalonia.Platform.Storage.IStorageFile>())
            {
                await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                if (!await AddAttachmentAsync(file.Name, stream).ConfigureAwait(true))
                    break;
            }
            RenderAttachments();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: файл не прикреплён перетаскиванием ({ex.Message}).", "WARNING");
            ShowAttachFailed();
        }
    }

    /// <summary>2026-10-29: Ctrl+V — картинка из буфера (снимок экрана, фото накладной из мессенджера) или файлы, скопированные в проводнике,
    /// прикладываются как страницы. Если в буфере есть текст — это обычная вставка текста в поле, ничего не трогаем.</summary>
    private async Task TryPasteAttachmentAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _composer is not { IsVisible: true })
                return;
            var formats = await clipboard.GetFormatsAsync().ConfigureAwait(true);
            if (formats.Contains(DataFormats.Text))
                return;
            var added = false;
            if (formats.Contains(DataFormats.Files) && await clipboard.GetDataAsync(DataFormats.Files).ConfigureAwait(true) is IEnumerable<Avalonia.Platform.Storage.IStorageItem> files)
            {
                await AttachDroppedAsync(files).ConfigureAwait(true);
                return;
            }
            foreach (var format in new[] { "PNG", "image/png" })
            {
                if (!formats.Contains(format) || await clipboard.GetDataAsync(format).ConfigureAwait(true) is not byte[] png || png.Length == 0)
                    continue;
                using var ms = new MemoryStream(png);
                added = await AddAttachmentAsync($"{T("Вставлено", "Чапталган", "Pasted", "Yapıştırılan", "Qo'yilgan")} {DateTime.Now:HH-mm-ss}.png", ms).ConfigureAwait(true);
                break;
            }
            if (added)
                RenderAttachments();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: вставка из буфера не удалась ({ex.Message}).", "WARNING");
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
            var page = new TextBlock { Text = T($"стр. {n + 1}", $"{n + 1}-бет", $"p. {n + 1}", $"s. {n + 1}", $"{n + 1}-bet"), FontSize = 12 };
            Use(page, TextBlock.ForegroundProperty, "BrushTextSoft");
            var index = n;
            var remove = UiKit.Ghost(this, "✕");
            remove.Height = 22;
            remove.FontSize = 12;
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
            // 2026-11-06 (жалоба «Орук маркет»): подсказка больше не обещает «наценку не меньше 20 %» — цену продажи программа сама не придумывает.
            Text = T("Напишите, что сделать (или просто отправьте: накладная, наценка 25 % — если нужна)",
                "Эмне кылууну жазыңыз (же жөн эле жөнөтүңүз: накладная, керек болсо үстөк 25 %)",
                "Type what to do (or just send: invoice, markup 25% — if needed)",
                "Ne yapılacağını yazın (veya sadece gönderin: fatura, gerekirse kâr payı %25)",
                "Nima qilishni yozing (yoki shunchaki yuboring: yuk xati, kerak bo'lsa ustama 25 %)"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(6, 0, 0, 0),
        };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        _attachRow.Children.Add(label);
        _attachRow.IsVisible = true;
        _input.Focus();
    }

}
