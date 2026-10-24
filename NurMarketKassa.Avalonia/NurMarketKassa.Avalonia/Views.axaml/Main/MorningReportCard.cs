using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, «доведи Нур Советника до 10» (И3 «Утренний отчёт»): карточка отчёта для ленты чата Нур Советника.
/// Вид — по правилам редизайна программы владельца (docs/owner-redesign-plan.md, аудит 1–10): плитки цифр в KpiTileGrid
/// (суммы не обрезаются, плитки переносятся — от 1024 точек), красный — только долг/просрочка/убыток/недостача, ▲/▼ — как
/// в «Сводке»; кнопки — обводкой (sec-outline), у всех AutomationProperties.Name; цвета — только ресурсы темы.
/// Данные и подписи готовит <see cref="OwnerMorningReport"/> — карточка ничего не считает и не загружает.</summary>
public static class MorningReportCard
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Карточка отчёта. openSection — открыть раздел по ключу ("overview", "debts", "restock", "warehouse", "sales"),
    /// openSettings — окно настроек отчёта (обычно <see cref="MorningReportSettingsWindow.ShowAsync"/>).</summary>
    public static Control Build(OwnerMorningReport.Report r, Action<string> openSection, Action openSettings)
    {
        var root = new StackPanel { Spacing = 12 };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1), MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch, Child = root,
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        AutomationProperties.SetName(card, OwnerMorningReport.Title(r));

        // Заголовок: «☀ Утренний отчёт · 21.10», под ним «За пн 20.10 · собран в 09:00»; справа «⚙ Настроить».
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "☀ " + OwnerMorningReport.Title(r), FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var subtitle = new TextBlock { Text = OwnerMorningReport.Subtitle(r), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(subtitle, TextBlock.ForegroundProperty, "BrushTextSoft");
        titles.Children.Add(title);
        titles.Children.Add(subtitle);
        head.Children.Add(titles);
        var settings = SmallButton("⚙ " + T("Настроить", "Жөндөө", "Settings", "Ayarlar", "Sozlash"),
            T("Настроить утренний отчёт", "Эртең мененки отчётту жөндөө", "Morning report settings", "Sabah raporu ayarları", "Ertalabki hisobotni sozlash"));
        settings.VerticalAlignment = VerticalAlignment.Top;
        settings.Margin = new Thickness(10, 0, 0, 0);
        settings.Click += (_, _) => Safe(openSettings, "настройки");
        Grid.SetColumn(settings, 1);
        head.Children.Add(settings);
        root.Children.Add(head);

        // Плитки цифр.
        if (r.Tiles.Count > 0)
        {
            var grid = new KpiTileGrid { MinTileWidth = 190, Spacing = 8 };
            foreach (var t in r.Tiles)
                grid.Children.Add(BuildTile(t));
            root.Children.Add(grid);
        }

        // Короткие списки.
        AddList(root, OwnerMorningReport.RunningOutTitle, r.RunningOutTotal, r.RunningOut);
        AddList(root, OwnerMorningReport.ExpiryTitle, r.ExpiryTotal, r.Expiry);
        AddList(root, OwnerMorningReport.OverdueTitle, r.Overdue.Count, r.Overdue);
        if (r.Oddities.Count > 0)
            AddList(root, OwnerMorningReport.OdditiesTitle, r.Oddities.Count, r.Oddities);
        else if (r.OdditiesChecked)
            root.Children.Add(Soft(OwnerMorningReport.NoOdditiesText, 13));

        // Вывод ИИ — курсивом, приглушённо, со значком ✦.
        if (!string.IsNullOrWhiteSpace(r.AiNote))
        {
            var note = new SelectableTextBlock { Text = "✦ " + r.AiNote, FontSize = 13.5, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap };
            Use(note, TextBlock.ForegroundProperty, "BrushTextSoft");
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = note };
            // 2026-10-21 (дизайн, круг 2): в тёмной теме BrushAccentSoft — оливковый; вывод ИИ — на нейтральном фоне с полосой акцента.
            Use(box, Border.BackgroundProperty, "BrushPanelSoft");
            Use(box, Border.BorderBrushProperty, "BrushAccent");
            box.BorderThickness = new Thickness(3, 0, 0, 0);
            root.Children.Add(box);
        }

        if (r.Problems.Count > 0)
            root.Children.Add(Soft(OwnerMorningReport.ProblemsPrefix + string.Join("; ", r.Problems), 12));
        if (r.Sources.Count > 0)
            root.Children.Add(Soft(OwnerMorningReport.SourcesPrefix + string.Join(" · ", r.Sources), 12));

        // «Открыть …» — только к тем разделам, по которым в отчёте что-то есть.
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        void Open(string key, string text)
        {
            var b = SmallButton(text, text);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += (_, _) => Safe(() => openSection(key), key);
            buttons.Children.Add(b);
        }
        if (r.Tiles.Any(t => t.Section == "overview"))
            Open("overview", T("Открыть сводку", "Жыйынтыкты ачуу", "Open summary", "Özeti aç", "Xulosani ochish"));
        if (r.Tiles.Any(t => t.Section == "debts") || r.Overdue.Count > 0)
            Open("debts", T("Открыть долги", "Карыздарды ачуу", "Open debts", "Borçları aç", "Qarzlarni ochish"));
        if (r.RunningOut.Count > 0)
            Open("restock", T("Открыть пополнение", "Толуктоону ачуу", "Open restock", "Stok yenilemeyi aç", "To'ldirishni ochish"));
        if (r.Expiry.Count > 0)
            Open("warehouse", T("Открыть склад", "Кампаны ачуу", "Open warehouse", "Depoyu aç", "Omborni ochish"));
        if (r.Oddities.Count > 0)
            Open("sales", T("Открыть продажи", "Сатууларды ачуу", "Open sales", "Satışları aç", "Sotuvlarni ochish"));
        if (buttons.Children.Count > 0)
            root.Children.Add(buttons);

        return card;
    }

    /// <summary>Плитка: подпись, сумма (уменьшается, а не обрезается), строки «▲ +5 % к позавчера» (стрелка — зелёным/красным,
    /// как в «Сводке»), примечание. Красная сумма — только долг с просрочкой или убыток (Danger).</summary>
    internal static Control BuildTile(OwnerMorningReport.Tile t)
    {
        var caption = new TextBlock { Text = t.Title };
        caption.Classes.Add("sec-kpi-label");
        var number = new TextBlock { Text = t.Value };
        number.Classes.Add("sec-kpi-value");
        if (t.Danger)
            number.Classes.Add("danger");
        var fit = new Viewbox { Child = number };
        fit.Classes.Add("sec-kpi-fit");
        var stack = new StackPanel { Spacing = 4, Children = { caption, fit } };
        foreach (var d in t.Deltas)
        {
            var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
            if (d.Percent is { } p && Math.Abs(p) >= 0.5)
            {
                var headRun = new Run(OwnerMorningReport.DeltaHead(p)) { FontWeight = FontWeight.SemiBold };
                headRun.Bind(TextElement.ForegroundProperty, line.GetResourceObservable(p > 0 ? "BrushSuccess" : "BrushDanger"));
                line.Inlines = new InlineCollection { headRun, new Run(" " + d.Against) };
            }
            else
                line.Text = OwnerMorningReport.DeltaText(d);
            stack.Children.Add(line);
        }
        if (!string.IsNullOrWhiteSpace(t.Note))
        {
            var note = new TextBlock { Text = t.Note, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Use(note, TextBlock.ForegroundProperty, "BrushTextSoft");
            stack.Children.Add(note);
        }
        var tile = new Border { Child = stack };
        tile.Classes.Add("sec-kpi");
        AutomationProperties.SetName(tile, t.Title + ": " + t.Value);
        return tile;
    }

    private static void AddList(Panel root, string title, int total, IReadOnlyList<OwnerMorningReport.Item> items)
    {
        if (items.Count == 0)
            return;
        var panel = new StackPanel { Spacing = 3 };
        var head = new TextBlock
        {
            Text = title + (total > items.Count ? T($" · всего {total}", $" · баары {total}", $" · {total} in total", $" · toplam {total}", $" · jami {total}") : ""),
            FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(head);
        foreach (var i in items.Take(5))
        {
            var line = new SelectableTextBlock { Text = "• " + i.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Use(line, TextBlock.ForegroundProperty, i.Danger ? "BrushDanger" : "BrushText");
            panel.Children.Add(line);
        }
        root.Children.Add(panel);
    }

    private static TextBlock Soft(string text, double size)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    /// <summary>Небольшая кнопка обводкой (sec-outline), с именем для UIA.</summary>
    internal static Button SmallButton(string text, string automationName)
    {
        var b = new Button
        {
            Content = text, MinHeight = 34, Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("sec-outline");
        AutomationProperties.SetName(b, automationName);
        return b;
    }

    private static void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: кнопка «{what}» не сработала ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    internal static void Use(StyledElement target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key));
}

/// <summary>2026-10-21 (И3): маленькое окно настроек утреннего отчёта — вкл/выкл, время ЧЧ:ММ, Telegram да/нет, «Показать отчёт
/// сейчас» и последние записи журнала отправки. Настройки — в advisor-morning.json (OwnerMorningReport.SaveSettings).</summary>
public sealed class MorningReportSettingsWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly CheckBox _enabled = new();
    private readonly TextBox _time;
    private readonly CheckBox _telegram = new();
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _journal = new() { Spacing = 2 };
    private readonly Button _showNow;
    private bool _busy;

    /// <summary>Открыть окно над окном, где лежит anchor (лента советника, кнопка). Без окна — обычным окном.</summary>
    public static async Task ShowAsync(Control? anchor)
    {
        try
        {
            var w = new MorningReportSettingsWindow();
            if (anchor is not null && TopLevel.GetTopLevel(anchor) is Window owner)
                await w.ShowDialog(owner).ConfigureAwait(true);
            else
                w.Show();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: окно настроек не открыто ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    public MorningReportSettingsWindow()
    {
        Title = T("Утренний отчёт", "Эртең мененки отчёт", "Morning report", "Sabah raporu", "Ertalabki hisobot");
        // 2026-10-22 (модалки, пакет 4): M 672 вместо 480 — три кнопки (~440 px) не помещались в подвал 480 и «Сохранить»
        // уходила за край на всех экранах. «Показать отчёт сейчас» — слева в подвале (второстепенная), справа «Закрыть» и
        // «Сохранить»; правило про тихие часы — строкой в теле. Поле времени 44 px, фокус — в нём.
        SectionCards.AsModal(this, 672);
        var s = OwnerMorningReport.Settings;

        _enabled.Content = T("Готовить утренний отчёт каждый день", "Эртең мененки отчётту күн сайын даярдоо", "Prepare the morning report every day",
            "Sabah raporunu her gün hazırla", "Ertalabki hisobotni har kuni tayyorlash");
        _enabled.IsChecked = s.Enabled;
        AutomationProperties.SetName(_enabled, _enabled.Content!.ToString()!);

        var timeLabel = new TextBlock { Text = T("Время (ЧЧ:ММ)", "Убакыт (СС:ММ)", "Time (HH:MM)", "Saat (SS:DD)", "Vaqt (SS:DD)"), FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        MorningReportCard.Use(timeLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        _time = UiKit.Input(this, "09:00", 44);
        _time.Width = 96;
        _time.MaxLength = 5;
        _time.Text = s.Time;
        AutomationProperties.SetName(_time, timeLabel.Text);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { timeLabel, _time } };

        _telegram.Content = T("Присылать владельцу в Telegram", "Ээсине Telegram'га жөнөтүү", "Send to the owner in Telegram", "Sahibine Telegram'dan gönder", "Egasiga Telegram'da yuborish");
        // 2026-10-23: без токена бота галочка выключена и недоступна, подсказка ниже (аудит Б-5)
        _telegram.IsChecked = s.Telegram && TelegramBotService.IsConfigured;
        _telegram.IsEnabled = TelegramBotService.IsConfigured;
        AutomationProperties.SetName(_telegram, _telegram.Content!.ToString()!);

        var botHint = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Text = BotStateText() };
        MorningReportCard.Use(botHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        MorningReportCard.Use(_status, TextBlock.ForegroundProperty, "BrushText");

        var journalHead = new TextBlock { Text = T("Последние отправки", "Акыркы жөнөтүүлөр", "Recent deliveries", "Son gönderimler", "Oxirgi yuborishlar"), FontSize = 13, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 4, 0, 0) };
        MorningReportCard.Use(journalHead, TextBlock.ForegroundProperty, "BrushText");
        RenderJournal();

        var quietHint = new TextBlock
        {
            Text = T("В Telegram — не чаще раза в день и не в тихие часы 21:00–09:00 (тогда — только в окне Нур Советника).",
                "Telegram'га — күнүнө бир жолудан көп эмес жана тынч саатта 21:00–09:00 эмес (анда — Нур Кеңешчи терезесинде гана).",
                "Telegram: at most once a day and never in quiet hours 21:00–09:00 (then only in the Nur Advisor window).",
                "Telegram: günde en fazla bir kez, sessiz saatlerde 21:00–09:00 asla (o zaman yalnızca Nur Danışman penceresinde).",
                "Telegram: kuniga ko'pi bilan bir marta va tinch soatlarda 21:00–09:00 emas (unda — faqat Nur Maslahatchi oynasida)."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        MorningReportCard.Use(quietHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        botHint.FontSize = 13;

        var panel = new StackPanel { Spacing = 10, Children = { _enabled, timeRow, _telegram, botHint, quietHint, journalHead, _journal, _status, SectionCards.ErrorBox(this, _error) } };

        _showNow = new Button { Content = T("Показать отчёт сейчас", "Отчётту азыр көрсөтүү", "Show the report now", "Raporu şimdi göster", "Hisobotni hozir ko'rsatish") };
        _showNow.Classes.Add("modal-secondary");
        AutomationProperties.SetName(_showNow, _showNow.Content!.ToString()!);
        _showNow.Click += async (_, _) => await ShowNowAsync().ConfigureAwait(true);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => { if (!_busy) Close(); };
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            if (Save())
                Close();
        };

        Content = SectionCards.Shell(Title,
            T("Каждое утро — выручка вчера и за неделю, прибыль, долги, что заканчивается, сроки годности и странности за день. Цифры считает программа, ИИ — только короткий вывод.",
                "Ар бир эртең — кечээки жана апталык түшүү, пайда, карыздар, түгөнүп бараткандар, мөөнөттөр жана күндүн өзгөчөлүктөрү. Сандарды программа эсептейт, ЖИ — кыска корутунду гана.",
                "Every morning: yesterday's and the week's revenue, profit, debts, what is running out, expiry dates and unusual things. The app counts the numbers; AI only writes a short conclusion.",
                "Her sabah: dünün ve haftanın cirosu, kâr, borçlar, tükenenler, son kullanma tarihleri ve olağandışı durumlar. Rakamları program hesaplar, YZ yalnızca kısa bir sonuç yazar.",
                "Har ertalab: kechagi va haftalik tushum, foyda, qarzlar, tugayotganlar, muddatlar va g'alati holatlar. Raqamlarni dastur hisoblaydi, SI — faqat qisqa xulosa."),
            panel, SectionCards.Buttons(close, save), _showNow);
        if (Content is Dialogs.ModalShell shell)
            shell.InitialFocus = _time;
        Closing += (_, e) =>
        {
            if (_busy)
                e.Cancel = true;
        };
    }

    private static string BotStateText()
    {
        if (TelegramBotService.IsConfigured)
            return T("Telegram-бот подключён — отчёт придёт владельцу.", "Telegram-бот туташкан — отчёт ээсине келет.", "The Telegram bot is connected — the owner will get the report.",
                "Telegram botu bağlı — rapor sahibine gelecek.", "Telegram-bot ulangan — hisobot egasiga keladi.");
        if (NurMarketKassa.Services.Api.ServerTelegramBotApi.LastKnownServerMode == true)
            return T("Бот работает на сервере NurCRM, а на этом компьютере нет токена бота — отчёт будет только в окне Нур Советника.",
                "Бот NurCRM серверинде иштейт, бул компьютерде боттун токени жок — отчёт Нур Кеңешчи терезесинде гана болот.",
                "The bot runs on the NurCRM server and this computer has no bot token — the report will be in the Nur Advisor window only.",
                "Bot NurCRM sunucusunda çalışıyor, bu bilgisayarda bot jetonu yok — rapor yalnızca Nur Danışman penceresinde olacak.",
                "Bot NurCRM serverida ishlaydi, bu kompyuterda bot tokeni yo'q — hisobot faqat Nur Maslahatchi oynasida bo'ladi.");
        return T("Telegram-бот не подключён (Настройки → Telegram-бот) — отчёт будет только в окне Нур Советника.",
            "Telegram-бот туташкан эмес (Жөндөөлөр → Telegram-бот) — отчёт Нур Кеңешчи терезесинде гана болот.",
            "The Telegram bot isn't connected (Settings → Telegram bot) — the report will be in the Nur Advisor window only.",
            "Telegram botu bağlı değil (Ayarlar → Telegram botu) — rapor yalnızca Nur Danışman penceresinde olacak.",
            "Telegram-bot ulanmagan (Sozlamalar → Telegram-bot) — hisobot faqat Nur Maslahatchi oynasida bo'ladi.");
    }

    private void RenderJournal()
    {
        _journal.Children.Clear();
        var entries = OwnerMorningReport.Journal.Take(4).ToList();
        if (entries.Count == 0)
        {
            var empty = new TextBlock { Text = T("Пока не было.", "Азырынча болгон жок.", "None yet.", "Henüz yok.", "Hozircha yo'q."), FontSize = 12.5 };
            MorningReportCard.Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _journal.Children.Add(empty);
            return;
        }
        foreach (var j in entries)
        {
            var where = j.Channel == OwnerMorningReport.ChannelTelegram ? "Telegram" : T("окно", "терезе", "window", "pencere", "oyna");
            var line = new TextBlock { Text = $"{j.At:dd.MM HH:mm} · {where} · {(j.Ok ? "✓" : "✕")} {j.Message}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            MorningReportCard.Use(line, TextBlock.ForegroundProperty, j.Ok ? "BrushTextSoft" : "BrushText");
            _journal.Children.Add(line);
        }
    }

    private bool Save()
    {
        _error.IsVisible = false;
        if (!OwnerMorningReport.SaveSettings(_enabled.IsChecked == true, _time.Text ?? "", _telegram.IsChecked == true))
        {
            _error.Text = T("Время — в виде ЧЧ:ММ, например 09:00.", "Убакыт — СС:ММ түрүндө, мисалы 09:00.", "Time must be HH:MM, for example 09:00.",
                "Saat SS:DD biçiminde olmalı, örneğin 09:00.", "Vaqt SS:DD ko'rinishida, masalan 09:00.");
            _error.IsVisible = true;
            _time.Focus();
            return false;
        }
        return true;
    }

    private async Task ShowNowAsync()
    {
        if (_busy || !Save())
            return;
        _busy = true;
        _showNow.IsEnabled = false;
        _status.Text = T("Собираю отчёт…", "Отчёт чогултулууда…", "Building the report…", "Rapor hazırlanıyor…", "Hisobot tuzilmoqda…");
        _status.IsVisible = true;
        try
        {
            var report = await OwnerMorningReport.RunNowAsync().ConfigureAwait(true);
            _status.Text = report is { HasData: true }
                ? T("Готово — отчёт в окне Нур Советника.", "Даяр — отчёт Нур Кеңешчи терезесинде.", "Done — the report is in the Nur Advisor window.",
                    "Hazır — rapor Nur Danışman penceresinde.", "Tayyor — hisobot Nur Maslahatchi oynasida.")
                : T("Отчёт не собран: сервер NurCRM не ответил — проверьте интернет.", "Отчёт чогулган жок: NurCRM сервери жооп берген жок — интернетти текшериңиз.",
                    "The report wasn't built: the NurCRM server didn't respond — check the internet.", "Rapor hazırlanamadı: NurCRM sunucusu yanıt vermedi — interneti kontrol edin.",
                    "Hisobot tuzilmadi: NurCRM serveri javob bermadi — internetni tekshiring.");
            RenderJournal();
        }
        finally
        {
            _busy = false;
            _showNow.IsEnabled = true;
        }
    }
}
