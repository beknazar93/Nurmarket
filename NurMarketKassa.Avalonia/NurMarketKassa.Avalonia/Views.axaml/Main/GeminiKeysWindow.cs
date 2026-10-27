using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-26, владелец: «наш десктоп: добавь возможность добавить API Gemini из нескольких Google-аккаунтов».
/// Окно списка ключей Gemini (до 10): метка, ключ (скрыт/показать), «Проверить» (список моделей — лимит не тратится),
/// включить/выключить, порядок ▲▼, удалить; у каждого — состояние (рабочий / исчерпан до … / недействителен), сверху «рабочих N из M».
/// Хранение и выбор ключа — GeminiKeyPool. Честное предупреждение про правила Google и использование запросов — под списком.
/// Открывается из «Нур Советника» (⚙) и из настроек Telegram-бота; «Сохранить» пишет список, «Отмена» ничего не меняет.</summary>
internal sealed class GeminiKeysWindow : Window
{
    private const string KeyPageUrl = "https://aistudio.google.com/apikey";

    private sealed class Row
    {
        public GeminiKeyEntry Entry = new();
        public bool Reveal;
        public string? CheckText; // результат последней кнопки «Проверить» (иначе — состояние из пула)
        public bool Checking;
    }

    private readonly List<Row> _rows = new();
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _counter = new() { FontSize = 14, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private Button _add = null!;
    private readonly ModalShell _shell;
    private bool _saved;

    public GeminiKeysWindow()
    {
        Title = Tr.T("Ключи Gemini", "Gemini ачкычтары", "Gemini keys", "Gemini anahtarları", "Gemini kalitlari");
        SectionCards.AsModal(this, 672);

        foreach (var e in GeminiKeyPool.Entries())
            _rows.Add(new Row { Entry = e });
        if (_rows.Count == 0)
            _rows.Add(new Row());

        var intro = new TextBlock
        {
            Text = Tr.T(
                "Можно добавить до 10 ключей Gemini из разных Google-аккаунтов. Касса берёт их по порядку: когда у первого кончается бесплатный лимит, "
                + "она сама переходит к следующему, а после сброса лимита (сутки считаются по тихоокеанскому времени) возвращается к первому. "
                + "Ключ можно получить бесплатно в Google AI Studio (aistudio.google.com/apikey); для разных аккаунтов — разные проекты.",
                "Ар кандай Google-аккаунттардан 10го чейин Gemini ачкычын кошсоңуз болот. Касса аларды ирети менен алат: биринчисинин акысыз лимити бүтсө, "
                + "өзү кийинкисине өтөт, ал эми лимит калыбына келгенде (суткалар Тынч океан убактысы менен эсептелет) биринчисине кайтат. "
                + "Ачкычты Google AI Studio'дон акысыз алса болот (aistudio.google.com/apikey); ар башка аккаунттар үчүн — ар башка долбоорлор.",
                "You can add up to 10 Gemini keys from different Google accounts. The till uses them in order: when the first one runs out of its free limit, "
                + "it moves to the next one by itself, and goes back to the first after the limit resets (days are counted in Pacific time). "
                + "You can get a key for free in Google AI Studio (aistudio.google.com/apikey); for different accounts, use different projects.",
                "Farklı Google hesaplarından en fazla 10 Gemini anahtarı ekleyebilirsiniz. Kasa bunları sırayla kullanır: ilkinin ücretsiz limiti bitince "
                + "kendisi sonrakine geçer, limit sıfırlanınca (günler Pasifik saatine göre sayılır) ilkine döner. "
                + "Anahtarı Google AI Studio'dan ücretsiz alabilirsiniz (aistudio.google.com/apikey); farklı hesaplar için farklı projeler kullanın.",
                "Turli Google hisoblaridan 10 tagacha Gemini kaliti qo'shish mumkin. Kassa ularni ketma-ket ishlatadi: birinchisining bepul limiti tugasa, "
                + "o'zi keyingisiga o'tadi, limit tiklangach (sutkalar Tinch okeani vaqti bilan hisoblanadi) birinchisiga qaytadi. "
                + "Kalitni Google AI Studio'dan bepul olish mumkin (aistudio.google.com/apikey); turli hisoblar uchun — turli loyihalar."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        intro.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _counter.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _empty.Text = Tr.T("Ключей пока нет — нажмите «Добавить ключ».", "Азырынча ачкыч жок — «Ачкыч кошуу» басыңыз.", "No keys yet — press “Add key”.",
            "Henüz anahtar yok — «Anahtar ekle»ye basın.", "Hozircha kalit yo'q — «Kalit qo'shish» ni bosing.");
        _empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        // Честное предупреждение — не прячем: правила Google и использование запросов на бесплатном уровне.
        var warn = new TextBlock
        {
            Text = Tr.T(
                "Важно. Правила Google могут запрещать обход лимитов с помощью нескольких аккаунтов или проектов — есть риск, что Google заблокирует ключи. "
                + "Кроме того, на бесплатном уровне Google может использовать запросы для улучшения своих продуктов. Решайте сами; надёжнее — один платный ключ.",
                "Маанилүү. Google эрежелери лимитти бир нече аккаунт же долбоор менен айланып өтүүгө тыюу салышы мүмкүн — Google ачкычтарды бөгөттөп коюшу ыктымал. "
                + "Мындан тышкары, акысыз деңгээлде Google суроолорду өз продукттарын жакшыртуу үчүн колдонушу мүмкүн. Өзүңүз чечиңиз; ишенимдүүрөөк — бир акы төлөнүүчү ачкыч.",
                "Important. Google's rules may forbid working around limits with several accounts or projects — Google could block the keys. "
                + "Also, on the free tier Google may use requests to improve its products. Your call; a single paid key is more reliable.",
                "Önemli. Google'ın kuralları limitleri birden çok hesap veya projeyle aşmayı yasaklayabilir — Google anahtarları engelleyebilir. "
                + "Ayrıca ücretsiz seviyede Google istekleri ürünlerini geliştirmek için kullanabilir. Karar sizin; daha güvenlisi tek ücretli anahtardır.",
                "Muhim. Google qoidalari limitni bir nechta hisob yoki loyiha bilan aylanib o'tishni taqiqlashi mumkin — Google kalitlarni bloklashi mumkin. "
                + "Bundan tashqari, bepul darajada Google so'rovlardan o'z mahsulotlarini yaxshilash uchun foydalanishi mumkin. O'zingiz hal qiling; ishonchlirog'i — bitta pullik kalit."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        var warnBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = warn };
        warnBox.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushShiftWarnBg"));
        warn.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

        var body = new StackPanel { Spacing = 12, Children = { intro, _counter, _list, _empty, warnBox } };

        _add = SectionCards.Secondary(Tr.T("＋ Добавить ключ", "＋ Ачкыч кошуу", "＋ Add key", "＋ Anahtar ekle", "＋ Kalit qo'shish"));
        _add.Click += (_, _) =>
        {
            SyncFromControls();
            if (_rows.Count >= GeminiKeyPool.MaxKeys)
                return;
            _rows.Add(new Row());
            Rebuild();
        };
        var get = SectionCards.Ghost(Tr.T("Получить ключ", "Ачкыч алуу", "Get a key", "Anahtar al", "Kalit olish"));
        get.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(KeyPageUrl)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ключи Gemini: страница ключа не открылась ({ex.GetType().Name}).", "WARNING");
            }
        };
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        var save = SectionCards.Primary(Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) =>
        {
            SyncFromControls();
            GeminiKeyPool.Save(_rows.Select(r => r.Entry));
            PosLogger.Log($"Ключи Gemini: список сохранён ({GeminiKeyPool.Entries().Count} шт., включено {GeminiKeyPool.EnabledCount()}).", "INFO");
            _saved = true;
            Close(true);
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        footer.Children.Add(_add);
        Grid.SetColumn(get, 1);
        get.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(get);
        var right = SectionCards.Buttons(cancel, save);
        Grid.SetColumn(right, 3);
        footer.Children.Add(right);

        _shell = SectionCards.Shell(Tr.T("Ключи Google Gemini", "Google Gemini ачкычтары", "Google Gemini keys", "Google Gemini anahtarları", "Google Gemini kalitlari"),
            Tr.T("Несколько Google-аккаунтов — один общий список", "Бир нече Google-аккаунт — бир жалпы тизме", "Several Google accounts — one shared list",
                "Birden çok Google hesabı — tek ortak liste", "Bir nechta Google hisobi — bitta umumiy ro'yxat"),
            body, footer);
        Content = _shell;
        Rebuild();
    }

    /// <summary>Показать окно. true — список сохранён.</summary>
    public static async Task<bool> ShowAsync(Window? owner)
    {
        var dlg = new GeminiKeysWindow();
        await PosDialogHost.ShowModalAsync(dlg, owner).ConfigureAwait(true);
        return dlg._saved;
    }

    /// <summary>Ввод из полей — в список (перед перестройкой строк и сохранением).</summary>
    private void SyncFromControls()
    {
        for (var i = 0; i < _list.Children.Count && i < _rows.Count; i++)
        {
            if (_list.Children[i] is not Border card || card.Tag is not Row row)
                continue;
            foreach (var box in card.GetLogicalDescendants().OfType<TextBox>())
            {
                if ((box.Name ?? "") == "label")
                    row.Entry.Label = (box.Text ?? "").Trim();
                else if ((box.Name ?? "") == "key")
                    row.Entry.Key = (box.Text ?? "").Trim();
            }
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        for (var i = 0; i < _rows.Count; i++)
            _list.Children.Add(BuildCard(_rows[i], i));
        _empty.IsVisible = _rows.Count == 0;
        _add.IsEnabled = _rows.Count < GeminiKeyPool.MaxKeys;
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        var total = _rows.Count(r => r.Entry.Key.Trim().Length > 0);
        var working = _rows.Count(r => r.Entry.Key.Trim().Length > 0 && GeminiKeyPool.StateOf(r.Entry).State == GeminiKeyState.Working);
        _counter.Text = Tr.T($"Рабочих ключей: {working} из {total}", $"Иштеген ачкычтар: {working} / {total}", $"Working keys: {working} of {total}",
            $"Çalışan anahtar: {working} / {total}", $"Ishlaydigan kalitlar: {working} / {total}");
    }

    private Border BuildCard(Row row, int index)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10), Tag = row };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));

        var status = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 230 };
        void ShowStatus()
        {
            var (text, brush) = StatusOf(row);
            status.Text = text;
            status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        }
        ShowStatus();

        var label = UiKit.Input(this, Tr.T($"Метка: Аккаунт {index + 1} / псевдоним почты", $"Белги: Аккаунт {index + 1} / почта аты", $"Label: Account {index + 1} / mail alias",
            $"Etiket: Hesap {index + 1} / e-posta takma adı", $"Yorliq: Hisob {index + 1} / pochta taxallusi"), 44);
        label.Name = "label";
        label.Text = row.Entry.Label;
        label.MaxLength = 60;
        var key = UiKit.SecretInput(this, "AIza…", 44); // 2026-10-27: Д-4 — ключ не читается через UI Automation
        key.Name = "key";
        key.Text = row.Entry.Key;
        key.PasswordChar = row.Reveal ? '\0' : '•';
        Avalonia.Automation.AutomationProperties.SetName(key, Tr.T($"Ключ {index + 1}", $"Ачкыч {index + 1}", $"Key {index + 1}", $"Anahtar {index + 1}", $"Kalit {index + 1}"));
        key.TextChanged += (_, _) =>
        {
            // Ключ изменили — прежняя отметка проверки к нему не относится.
            row.Entry.Key = (key.Text ?? "").Trim();
            row.CheckText = null;
            ShowStatus();
            UpdateCounter();
        };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(label);
        status.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(status, 1);
        head.Children.Add(status);

        var show = SectionCards.Secondary(row.Reveal ? Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish") : Tr.T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"));
        show.MinHeight = 44;
        show.Click += (_, _) =>
        {
            row.Reveal = !row.Reveal;
            key.PasswordChar = row.Reveal ? '\0' : '•';
            show.Content = row.Reveal ? Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish") : Tr.T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish");
        };
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        keyRow.Children.Add(key);
        show.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(show, 1);
        keyRow.Children.Add(show);

        var enabled = new CheckBox
        {
            Content = Tr.T("Включён", "Күйгүзүлгөн", "Enabled", "Açık", "Yoqilgan"), IsChecked = row.Entry.Enabled, MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center,
        };
        enabled.IsCheckedChanged += (_, _) =>
        {
            row.Entry.Enabled = enabled.IsChecked == true;
            ShowStatus();
            UpdateCounter();
        };
        var check = SectionCards.Secondary(Tr.T("Проверить", "Текшерүү", "Check", "Kontrol et", "Tekshirish"));
        check.MinHeight = 44;
        check.Click += async (_, _) =>
        {
            var value = (key.Text ?? "").Trim();
            if (value.Length == 0 || row.Checking)
            {
                if (value.Length == 0)
                    status.Text = Tr.T("Вставьте ключ", "Ачкычты чаптаңыз", "Paste the key", "Anahtarı yapıştırın", "Kalitni joylang");
                return;
            }
            row.Checking = true;
            check.IsEnabled = false;
            status.Text = Tr.T("Проверяю…", "Текшерип жатам…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…");
            try
            {
                var (result, until, http) = await GeminiKeyPool.CheckAsync(value, CancellationToken.None).ConfigureAwait(true);
                row.CheckText = result switch
                {
                    GeminiKeyPool.CheckResult.Ok => null,
                    GeminiKeyPool.CheckResult.NoConnection => Tr.T("Нет связи с Google", "Google менен байланыш жок", "No connection to Google", "Google ile bağlantı yok", "Google bilan aloqa yo'q"),
                    GeminiKeyPool.CheckResult.Other => Tr.T($"Google ответил {http}", $"Google {http} жооп берди", $"Google replied {http}", $"Google {http} yanıtı verdi", $"Google {http} javob berdi"),
                    _ => null,
                };
            }
            finally
            {
                row.Checking = false;
                check.IsEnabled = true;
            }
            ShowStatus();
            UpdateCounter();
        };
        var up = SectionCards.Secondary("▲");
        up.MinWidth = 48;
        up.MinHeight = 44;
        up.IsEnabled = index > 0;
        up.Click += (_, _) => Move(index, -1);
        Avalonia.Automation.AutomationProperties.SetName(up, Tr.T("Выше", "Жогору", "Move up", "Yukarı", "Yuqoriga"));
        var down = SectionCards.Secondary("▼");
        down.MinWidth = 48;
        down.MinHeight = 44;
        down.IsEnabled = index < _rows.Count - 1;
        down.Click += (_, _) => Move(index, 1);
        Avalonia.Automation.AutomationProperties.SetName(down, Tr.T("Ниже", "Төмөн", "Move down", "Aşağı", "Pastga"));
        var remove = SectionCards.Secondary(Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"));
        remove.MinHeight = 44;
        remove.Click += (_, _) =>
        {
            SyncFromControls();
            _rows.Remove(row);
            Rebuild();
        };

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { enabled, check, up, down, remove })
        {
            c.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(c);
        }

        card.Child = new StackPanel { Spacing = 8, Children = { head, keyRow, actions } };
        return card;
    }

    private void Move(int index, int delta)
    {
        SyncFromControls();
        var to = index + delta;
        if (to < 0 || to >= _rows.Count)
            return;
        (_rows[index], _rows[to]) = (_rows[to], _rows[index]);
        Rebuild();
    }

    /// <summary>Строка состояния ключа и кисть темы для неё.</summary>
    private static (string Text, string Brush) StatusOf(Row row)
    {
        if (row.Entry.Key.Trim().Length == 0)
            return (Tr.T("Пустой", "Бош", "Empty", "Boş", "Bo'sh"), "BrushTextMuted");
        var (state, until) = GeminiKeyPool.StateOf(row.Entry);
        return state switch
        {
            GeminiKeyState.Disabled => (Tr.T("● Выключен", "● Өчүрүлгөн", "● Disabled", "● Kapalı", "● O'chirilgan"), "BrushTextMuted"),
            GeminiKeyState.Invalid => (Tr.T("● Недействителен", "● Жараксыз", "● Invalid", "● Geçersiz", "● Yaroqsiz"), "BrushDanger"),
            GeminiKeyState.Exhausted => (Tr.T($"● Исчерпан до {until:HH:mm}", $"● {until:HH:mm} чейин түгөнгөн", $"● Exhausted until {until:HH:mm}",
                $"● {until:HH:mm}'e kadar tükendi", $"● {until:HH:mm} gacha tugagan"), "BrushShiftWarnBorder"),
            _ when row.CheckText is not null => ("● " + row.CheckText, "BrushShiftWarnBorder"),
            _ when GeminiKeyPool.IsVerified(row.Entry.Key) => (Tr.T("● Рабочий", "● Иштейт", "● Working", "● Çalışıyor", "● Ishlaydi"), "BrushSuccess"),
            _ => (Tr.T("● Не проверен", "● Текшерилген эмес", "● Not checked", "● Kontrol edilmedi", "● Tekshirilmagan"), "BrushTextMuted"),
        };
    }
}
