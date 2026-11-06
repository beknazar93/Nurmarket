using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05 (волна 2-Б, функции 2, 12 и 8): окно «Подозрительные операции» — правила и пороги антифрода, «Права кассира: PIN старшего», «Чеки в Telegram»,
/// балл кассиров за неделю и список событий с переходом в чек. Настройки — AntiFraudSettingsStore (общий файл кассы и владельца), события — FraudEventStore +
/// журналы удалений, убытков и «Некорректных чеков». Всё выключено по умолчанию: касса ведёт себя как раньше.</summary>
public sealed class FraudWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // правила антифрода
    private readonly CheckBox _fEnabled = new(), _fTelegram = new(), _fRemoval = new(), _fMany = new(), _fReturn = new(), _fNoReceipt = new(), _fDiscount = new(), _fBelow = new(), _fShortage = new();
    private readonly TextBox _fRemovalSum, _fManyCount, _fReturnSum, _fDiscountPercent, _fShortageSum, _fQuietFrom, _fQuietTo;
    // PIN старшего
    private readonly CheckBox _gEnabled = new(), _gDiscount = new(), _gReturn = new(), _gRemoval = new(), _gShortage = new();
    private readonly TextBox _gDiscountPercent, _gReturnSum, _gRemovalSum, _gShortageSum;
    // чеки в Telegram
    private readonly CheckBox _rEnabled = new(), _rBonus = new(), _rQr = new(), _rPaper = new();

    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _scores = new() { Spacing = 4 };
    private readonly StackPanel _events = new() { Spacing = 4 };

    /// <summary>Открыть окно над окном, где лежит anchor; без окна — обычным окном.</summary>
    public static async Task ShowAsync(Control? anchor)
    {
        if (!NurMarketKassa.AvaloniaHost.Services.ActionGuard.EnsureCanChangeProtection()) // 2026-11-05 (ревью волн 0–3, В3): кассир защиту не меняет
            return;
        try
        {
            var w = new FraudWindow();
            if (anchor is not null && TopLevel.GetTopLevel(anchor) is Window owner)
                await w.ShowDialog(owner).ConfigureAwait(true);
            else
                w.Show();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Подозрительные операции: окно не открыто ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private TextBox Number(double value, double width = 96)
    {
        var box = UiKit.Input(this, "0", 40);
        box.Width = width;
        box.Text = ShopAutomationText.FormatNumber(value);
        return box;
    }

    private static CheckBox Check(CheckBox box, string text, bool on)
    {
        box.Content = text;
        box.IsChecked = on;
        AutomationProperties.SetName(box, text);
        return box;
    }

    private static Control Section(string title, params Control[] rows)
    {
        var head = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2) };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(head);
        foreach (var r in rows)
            panel.Children.Add(r);
        return panel;
    }

    private static Control Hint(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
        return tb;
    }

    private static Control Line(CheckBox? check, string? before, TextBox? box, string? after)
    {
        // 2026-11-06: В3 ДС-3 — у числового поля нет своей подписи (текст «до» и «после» лежат рядом отдельными надписями): диктор читал просто «0». Имя поля = эти надписи (или текст флажка).
        if (box is not null)
        {
            var spoken = string.Join(" ", new[] { before, after, before is null && after is null ? check?.Content as string : null }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim(' ', ':');
            if (spoken.Length > 0 && string.IsNullOrEmpty(AutomationProperties.GetName(box)))
                AutomationProperties.SetName(box, spoken);
        }
        // 2026-11-05 (ревью волн 0–3, Н11): перенос строки вместо обрезки на ky/uz и масштабе 125 %.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (check is not null)
            row.Children.Add(check);
        if (before is not null)
            row.Children.Add(new TextBlock { Text = before, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        if (box is not null)
            row.Children.Add(box);
        if (after is not null)
            row.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        foreach (var child in row.Children)
            child.Margin = new Thickness(0, 0, 8, 4); // в WrapPanel нет Spacing (Avalonia 11.2) — отступы у самих элементов
        return row;
    }

    private static string Som => T("сом", "сом", "som", "som", "so'm");

    public FraudWindow()
    {
        Title = T("Подозрительные операции", "Шектүү операциялар", "Suspicious operations", "Şüpheli işlemler", "Shubhali amallar");
        SectionCards.AsModal(this, 760);
        var d = AntiFraudSettingsStore.Current;
        var f = d.Fraud;
        var g = d.Guard;
        var r = d.Receipts;

        _fRemovalSum = Number(f.RemovalAmount);
        _fManyCount = Number(f.RemovalPerShift, 72);
        _fReturnSum = Number(f.ReturnAmount);
        _fDiscountPercent = Number(f.DiscountPercent, 72);
        _fShortageSum = Number(f.ShortageAmount);
        _fQuietFrom = Number(f.QuietFromHour, 72);
        _fQuietTo = Number(f.QuietToHour, 72);
        _gDiscountPercent = Number(g.DiscountPercent, 72);
        _gReturnSum = Number(g.ReturnAmount);
        _gRemovalSum = Number(g.RemovalAmount);
        _gShortageSum = Number(g.ShortageAmount);

        var panel = new StackPanel { Spacing = 4 };

        // ───── 1. Балл кассиров и события
        panel.Children.Add(Section(T("Балл кассиров за эту неделю", "Бул жумадагы кассирлердин упайы", "Cashier score this week", "Bu haftanın kasiyer puanı", "Shu haftadagi kassirlar bali"),
            Hint(T("Чем больше баллов, тем больше подозрительных действий. Подтверждённое PIN старшего баллов не даёт.", "Упай канчалык көп болсо, шектүү аракеттер ошончолук көп. Улуунун PIN коду менен ырасталганга упай берилбейт.",
                "More points mean more suspicious actions. Actions confirmed by a senior's PIN give no points.", "Ne kadar çok puan, o kadar çok şüpheli işlem. Yetkili PIN'iyle onaylananlar puan vermez.", "Ball qancha ko'p bo'lsa, shubhali amallar shuncha ko'p. Katta PIN i bilan tasdiqlangan amal ball bermaydi.")),
            _scores));
        panel.Children.Add(Section(T("События (последние 40)", "Окуялар (акыркы 40)", "Events (last 40)", "Olaylar (son 40)", "Voqealar (oxirgi 40)"),
            Hint(T("Журнал локальный — события этого компьютера. Для нескольких касс нужен приём событий на сервере.", "Журнал локалдык — бул компьютердин окуялары. Бир нече касса үчүн серверде окуяларды кабыл алуу керек.",
                "The log is local — events of this computer. Several tills need event intake on the server.", "Günlük yereldir — bu bilgisayarın olayları. Birden fazla kasa için sunucuda olay kabulü gerekir.", "Jurnal lokal — shu kompyuterning voqealari. Bir nechta kassa uchun serverda voqealarni qabul qilish kerak.")),
            _events));

        // ───── 2. Правила
        panel.Children.Add(Section(T("Правила: что считать подозрительным", "Эрежелер: эмнени шектүү деп эсептөө", "Rules: what counts as suspicious", "Kurallar: neyin şüpheli sayılacağı", "Qoidalar: nima shubhali hisoblanadi"),
            Check(_fEnabled, T("Вести журнал подозрительных операций", "Шектүү операциялардын журналын жүргүзүү", "Keep the suspicious operations log", "Şüpheli işlem günlüğü tut", "Shubhali amallar jurnalini yuritish"), f.Enabled),
            Check(_fTelegram, T("Сообщать владельцу в Telegram (тихие часы — копятся до утра)", "Ээсине Telegram аркылуу кабарлоо (тынч саат — эртеңкиге чейин топтолот)", "Notify the owner in Telegram (quiet hours wait until morning)",
                "Sahibe Telegram'dan bildir (sessiz saatlerde sabaha kadar birikir)", "Egasiga Telegram orqali xabar berish (tinch soatlarda ertalabgacha to'planadi)"), f.Telegram),
            Line(Check(_fRemoval, T("строка убрана из чека на сумму от", "чектен сап төмөнкү суммадан өчүрүлдү", "a line removed from the receipt worth at least", "fişten şu tutardan satır silindi", "chekdan shu summadan qator o'chirildi"), f.Removal), null, _fRemovalSum, Som),
            Line(Check(_fMany, T("за смену убрано строк больше", "сменада өчүрүлгөн сап ашса", "lines removed per shift over", "vardiyada silinen satır şundan fazla", "smenada o'chirilgan qator shundan ortiq"), f.RemovalMany), null, _fManyCount, null),
            Line(Check(_fReturn, T("возврат на сумму от", "кайтаруу суммасы төмөнкүдөн", "a return worth at least", "iade tutarı en az", "qaytarish summasi kamida"), f.Return), null, _fReturnSum, Som),
            Check(_fNoReceipt, T("возврат без чека (пока не используется: в кассе возврат всегда идёт по чеку)", "чексиз кайтаруу (азырынча колдонулбайт: кассада кайтаруу дайыма чек боюнча)", "a return without a receipt (not used yet: returns in the till always go by receipt)", "fişsiz iade (şimdilik kullanılmıyor: kasada iade her zaman fişle yapılır)", "cheksiz qaytarish (hozircha ishlatilmaydi: kassada qaytarish doim chek bo'yicha)"), f.ReturnNoReceipt),
            Line(Check(_fDiscount, T("скидка не меньше", "арзандатуу кеминде", "a discount of at least", "indirim en az", "chegirma kamida"), f.Discount), null, _fDiscountPercent, "%"),
            Check(_fBelow, T("цена ниже закупки (продажа в убыток)", "баа сатып алуудан төмөн (зыянга сатуу)", "price below cost (sale at a loss)", "fiyat maliyetin altında (zararına satış)", "narx xarid narxidan past (zararga sotuv)"), f.BelowCost),
            Line(Check(_fShortage, T("недостача при закрытии смены от", "смена жабылганда жетишпестик төмөнкүдөн", "a shortage at shift close of at least", "vardiya kapanışında eksik en az", "smena yopilganda kamomad kamida"), f.Shortage), null, _fShortageSum, Som),
            Line(null, T("Тихие часы Telegram: с", "Telegram тынч саат: баштап", "Telegram quiet hours: from", "Telegram sessiz saat: şundan", "Telegram tinch soat: dan"), _fQuietFrom, T("до", "чейин", "to", "şuna", "gacha")),
            Line(null, null, _fQuietTo, T("ч.", "с.", "h", "sa.", "s."))));

        // ───── 3. PIN старшего
        panel.Children.Add(Section(T("Права кассира: спросить PIN старшего", "Кассирдин укуктары: улуунун PIN кодун суроо", "Cashier rights: ask a senior's PIN", "Kasiyer hakları: yetkili PIN'i sor", "Kassir huquqlari: katta PIN ini so'rash"),
            Check(_gEnabled, T("Включить (владелец и администратор PIN не вводят)", "Күйгүзүү (ээси жана администратор PIN киргизбейт)", "Turn on (the owner and admin don't enter a PIN)", "Aç (sahip ve yönetici PIN girmez)", "Yoqish (ega va administrator PIN kiritmaydi)"), g.Enabled),
            Line(Check(_gDiscount, T("скидка выше", "арзандатуу андан жогору", "a discount over", "indirim şundan fazla", "chegirma shundan yuqori"), g.Discount), null, _gDiscountPercent, "%"),
            Line(Check(_gReturn, T("возврат от", "кайтаруу төмөнкүдөн", "a return from", "iade şundan itibaren", "qaytarish dan"), g.Return), null, _gReturnSum, Som + T(" (пусто — любой)", " (бош — каалаган)", " (empty — any)", " (boş — herhangi)", " (bo'sh — istalgan)")),
            Line(Check(_gRemoval, T("удаление строки после пробития от", "сапты урандан кийин өчүрүү төмөнкүдөн", "removing a line after ringing it up from", "okutulan satırı silme şundan itibaren", "urilgan qatorni o'chirish dan"), g.RemovalAfterRing), null, _gRemovalSum, Som),
            Line(Check(_gShortage, T("закрытие смены с недостачей от", "жетишпестик менен сменаны жабуу төмөнкүдөн", "closing the shift with a shortage from", "eksikle vardiya kapatma şundan itibaren", "kamomad bilan smenani yopish dan"), g.ShiftShortage), null, _gShortageSum, Som),
            Hint(T("PIN — тот же PIN владельца, что для старых чеков и просрочки; проверяется на сервере. Каждое подтверждение и отказ пишутся в журнал. Ручной цены и открытия ящика без продажи в кассе нет — их правил здесь тоже нет.",
                "PIN — эски чектер жана мөөнөтү өткөн товар үчүн ээсинин ошол эле PIN коду; серверде текшерилет. Ар бир ырастоо жана баш тартуу журналга жазылат. Кассада кол менен баа жана сатуусуз кутуну ачуу жок — алардын эрежеси да жок.",
                "The PIN is the same owner PIN as for old receipts and expired goods; it is checked on the server. Every confirmation and refusal is logged. The till has no manual price or opening the drawer without a sale, so there are no rules for them.",
                "PIN, eski fişler ve süresi geçmiş ürünlerdeki sahip PIN'iyle aynıdır; sunucuda doğrulanır. Her onay ve ret günlüğe yazılır. Kasada elle fiyat ve satışsız çekmece açma yok, bu yüzden kuralları da yok.",
                "PIN eski cheklar va muddati o'tgan mahsulotdagi ega PIN i bilan bir xil; serverda tekshiriladi. Har bir tasdiq va rad jurnalga yoziladi. Kassada qo'lda narx va sotuvsiz tortmani ochish yo'q — ularning qoidalari ham yo'q."))));

        // ───── 4. Чеки в Telegram
        var link = TelegramReceiptLinks.ShopLink();
        var qrButton = UiKit.Ghost(this, T("Показать QR для покупателя", "Сатып алуучуга QR көрсөтүү", "Show the QR to the customer", "QR'ı müşteriye göster", "QR ni xaridorga ko'rsatish"));
        qrButton.Click += (_, _) => TelegramReceiptUi.ShowQr(this);
        panel.Children.Add(Section(T("Чеки и бонусы в Telegram", "Telegram'да чектер жана бонустар", "Receipts and bonuses in Telegram", "Telegram'da fişler ve bonuslar", "Telegram'da cheklar va bonuslar"),
            Check(_rEnabled, T("Присылать чек покупателю в Telegram (если он привязал телефон к боту)", "Чекти сатып алуучуга Telegram аркылуу жөнөтүү (эгер ал телефонун ботко байласа)", "Send the receipt to the customer in Telegram (if they linked a phone to the bot)",
                "Fişi müşteriye Telegram'dan gönder (telefonunu bota bağladıysa)", "Chekni xaridorga Telegram orqali yuborish (agar u telefonini botga bog'lagan bo'lsa)"), r.Enabled),
            Check(_rBonus, T("и баланс бонусов", "жана бонус балансын", "and the bonus balance", "ve bonus bakiyesi", "va bonus balansi"), r.Bonus),
            Check(_rQr, T("QR «Чеки и бонусы в Telegram» на экране покупателя", "Сатып алуучунун экранында «Telegram'да чектер жана бонустар» QR", "“Receipts and bonuses in Telegram” QR on the customer display", "Müşteri ekranında «Telegram'da fiş ve bonus» QR'ı", "Xaridor ekranida «Telegram'da chek va bonuslar» QR i"), r.QrOnCustomerDisplay),
            Check(_rPaper, T("ссылка на бота на печатном чеке (две строки)", "басылган чекте ботко шилтеме (эки сап)", "bot link on the printed receipt (two lines)", "basılı fişte bot bağlantısı (iki satır)", "bosma chekda bot havolasi (ikki qator)"), r.LinkOnPaper),
            Hint(link is null
                ? T("Имя бота не сохранено: «Настройки → Telegram-бот».", "Боттун аты сакталган эмес: «Жөндөөлөр → Telegram-бот».", "The bot name isn't saved: “Settings → Telegram bot”.", "Bot adı kayıtlı değil: «Ayarlar → Telegram botu».", "Bot nomi saqlanmagan: «Sozlamalar → Telegram-bot».")
                : T($"Ссылка для покупателя: {link}. Привязано телефонов: {TelegramReceiptLinks.Count}. Покупатель сам нажимает «Старт» и делится номером; отписка — /stop.",
                    $"Сатып алуучу үчүн шилтеме: {link}. Байланган телефон: {TelegramReceiptLinks.Count}. Сатып алуучу өзү «Старт» басып, номерин бөлүшөт; баш тартуу — /stop.",
                    $"Customer link: {link}. Linked phones: {TelegramReceiptLinks.Count}. The customer presses Start and shares their number; opt out with /stop.",
                    $"Müşteri bağlantısı: {link}. Bağlı telefon: {TelegramReceiptLinks.Count}. Müşteri Başlat'a basıp numarasını paylaşır; vazgeçmek için /stop.",
                    $"Xaridor uchun havola: {link}. Bog'langan telefonlar: {TelegramReceiptLinks.Count}. Xaridor o'zi Start bosib raqamini ulashadi; voz kechish — /stop.")),
            qrButton));

        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) =>
        {
            if (Save())
                Close();
        };
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 470, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = SectionCards.Shell(Title,
            T("Антифрод кассира: журнал, баллы, оповещения, PIN старшего и чеки в Telegram. Всё выключено, пока вы не включите.",
                "Кассирдин антифроду: журнал, упайлар, эскертүүлөр, улуунун PIN коду жана Telegram'да чектер. Сиз күйгүзмөйүнчө баары өчүк.",
                "Cashier anti-fraud: log, scores, alerts, a senior's PIN and receipts in Telegram. Everything is off until you turn it on.",
                "Kasiyer sahtekârlık kontrolü: günlük, puanlar, uyarılar, yetkili PIN'i ve Telegram'da fişler. Siz açana kadar her şey kapalı.",
                "Kassir antifrodi: jurnal, ballar, ogohlantirishlar, katta PIN i va Telegram'da cheklar. Siz yoqmaguningizcha hammasi o'chiq."),
            scroll, SectionCards.Buttons(close, save));

        _scores.Children.Add(Hint(T("Загрузка…", "Жүктөлүүда…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        Opened += (_, _) => _ = LoadAsync();
    }

    // ───────── данные ─────────

    private async Task LoadAsync()
    {
        try
        {
            var rules = AntiFraudSettingsStore.Current.Fraud;
            var now = DateTimeOffset.Now;
            var (all, week) = await Task.Run(() =>
            {
                var own = FraudEventStore.ReadAll();
                var company = CompanyInfoService.LastCompany?.Id;
                var removals = CartRemovalLogStore.ReadAll().Where(x => company is null || x.CompanyId is null || x.CompanyId == company);
                var derived = FraudAnalyzer.FromRemovals(removals, rules)
                    .Concat(FraudAnalyzer.FromLoss(LossSalesStore.ReadAll(), rules)).ToList();
                try { derived.AddRange(FraudAnalyzer.FromIrregular(IrregularReceiptsStore.LoadAll())); }
                catch (Exception ex) { PosLogger.Log($"Подозрительные операции: «Некорректные чеки» не прочитаны ({ex.GetType().Name}).", "WARNING"); }
                // Производные события показываем только за 90 дней и только если правило сейчас включено (FromRemovals/FromLoss уже учли пороги).
                var merged = FraudAnalyzer.Merge(own, derived);
                return (merged, FraudAnalyzer.Score(merged, FraudAnalyzer.WeekStart(now), now.AddMinutes(1)));
            }).ConfigureAwait(true);

            _scores.Children.Clear();
            if (week.Count == 0 || week.All(s => s.Points == 0))
                _scores.Children.Add(Hint(T("На этой неделе подозрительных действий нет.", "Бул жумада шектүү аракеттер жок.", "No suspicious actions this week.", "Bu hafta şüpheli işlem yok.", "Bu hafta shubhali amallar yo'q.")));
            else
                foreach (var s in week.Where(s => s.Points > 0))
                    _scores.Children.Add(new TextBlock
                    {
                        FontSize = 13.5, TextWrapping = TextWrapping.Wrap,
                        Text = T($"{s.Cashier} — {s.Points} б. ({s.Events} действ., на {MoneyText.Som(s.Amount)})", $"{s.Cashier} — {s.Points} упай ({s.Events} аракет, {MoneyText.Som(s.Amount)})",
                            $"{s.Cashier} — {s.Points} pts ({s.Events} actions, {MoneyText.Som(s.Amount)})", $"{s.Cashier} — {s.Points} puan ({s.Events} işlem, {MoneyText.Som(s.Amount)})", $"{s.Cashier} — {s.Points} ball ({s.Events} amal, {MoneyText.Som(s.Amount)})"),
                    });

            _events.Children.Clear();
            if (all.Count == 0)
                // 2026-11-05 (приёмка волн 0–3, Н-7): журнал по умолчанию выключен — «событий нет» без этой подсказки выглядело как «всё чисто».
                _events.Children.Add(Hint(T("Событий пока нет.", "Окуялар азырынча жок.", "No events yet.", "Henüz olay yok.", "Hozircha voqealar yo'q.")
                    + (rules.Enabled ? "" : " " + T("Журнал выключен — включите «Вести журнал подозрительных операций» в правилах ниже, иначе события не записываются.",
                        "Журнал өчүк — төмөнкү эрежелерде «Шектүү операциялардын журналын жүргүзүү» күйгүзүңүз, болбосо окуялар жазылбайт.",
                        "The log is off — turn on “Keep the suspicious operations log” in the rules below, otherwise events are not recorded.",
                        "Günlük kapalı — aşağıdaki kurallarda «Şüpheli işlem günlüğü tut» seçeneğini açın, yoksa olaylar kaydedilmez.",
                        "Jurnal o'chiq — quyidagi qoidalarda «Shubhali amallar jurnalini yuritish»ni yoqing, aks holda voqealar yozilmaydi."))));
            foreach (var e in all.Take(40))
                _events.Children.Add(EventRow(e));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Подозрительные операции: данные не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            _scores.Children.Clear();
            _scores.Children.Add(Hint(T("Не удалось загрузить данные.", "Маалыматты жүктөө мүмкүн болгон жок.", "Could not load the data.", "Veriler yüklenemedi.", "Ma'lumotlarni yuklab bo'lmadi.")));
        }
    }

    private Control EventRow(FraudEvent e)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new TextBlock
        {
            Text = $"{e.At.LocalDateTime:dd.MM HH:mm} · {FraudText.Describe(e)}", FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        if (FraudKinds.Weight(e.Kind) == 0)
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        row.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(e.SaleId))
        {
            var open = UiKit.Ghost(this, T("Чек", "Чек", "Receipt", "Fiş", "Chek"));
            open.Height = 36;
            open.Click += async (_, _) => await OpenReceiptAsync(e.SaleId!, open).ConfigureAwait(true);
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
        }
        return row;
    }

    /// <summary>Переход в чек: чек сервера тем же окном, что «Чек» с плашки «Продажа прошла».</summary>
    private async Task OpenReceiptAsync(string saleId, Button button)
    {
        button.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var json = await App.SalesApi.PosSaleGetAsync(saleId, cts.Token).ConfigureAwait(true);
            var card = SaleReceiptCardData.Build(json, SalesWindow.TryReceiptNumber(json), SalesWindow.ReadSaleDiscount(json), SalesWindow.ReadSaleTotal(json));
            await PosDialogHost.ShowAsync(new ReceiptViewDialog(card, null), this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Подозрительные операции: чек {saleId} не открыт ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            _error.Text = T("Чек не открылся: нет связи с сервером или чек не найден.", "Чек ачылган жок: сервер менен байланыш жок же чек табылган жок.", "The receipt didn't open: no connection to the server or the receipt wasn't found.",
                "Fiş açılmadı: sunucuyla bağlantı yok veya fiş bulunamadı.", "Chek ochilmadi: server bilan aloqa yo'q yoki chek topilmadi.");
            _error.IsVisible = true;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ───────── сохранение ─────────

    private bool Save()
    {
        _error.IsVisible = false;
        double N(TextBox box, double fallback) => ShopAutomationText.ParseNumber(box.Text) ?? fallback;
        var all = new[] { _fRemovalSum, _fManyCount, _fReturnSum, _fDiscountPercent, _fShortageSum, _fQuietFrom, _fQuietTo, _gDiscountPercent, _gReturnSum, _gRemovalSum, _gShortageSum };
        var bad = all.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text) && ShopAutomationText.ParseNumber(b.Text) is null);
        if (bad is not null)
        {
            _error.Text = T("Вместо числа в одном из полей что-то другое — поправьте.", "Бир талаада сандын ордуна башка нерсе жазылган — оңдоңуз.", "One of the fields has something other than a number — please fix it.",
                "Alanlardan birinde sayı yerine başka bir şey var — düzeltin.", "Maydonlardan birida son o'rniga boshqa narsa — tuzating.");
            _error.IsVisible = true;
            bad.Focus();
            return false;
        }
        AntiFraudSettingsStore.Update(d =>
        {
            var f = d.Fraud;
            f.Enabled = _fEnabled.IsChecked == true;
            f.Telegram = _fTelegram.IsChecked == true;
            f.Removal = _fRemoval.IsChecked == true;
            f.RemovalAmount = N(_fRemovalSum, 500);
            f.RemovalMany = _fMany.IsChecked == true;
            f.RemovalPerShift = (int)N(_fManyCount, 10);
            f.Return = _fReturn.IsChecked == true;
            f.ReturnAmount = N(_fReturnSum, 2000);
            f.ReturnNoReceipt = _fNoReceipt.IsChecked == true;
            f.Discount = _fDiscount.IsChecked == true;
            f.DiscountPercent = N(_fDiscountPercent, 20);
            f.BelowCost = _fBelow.IsChecked == true;
            f.Shortage = _fShortage.IsChecked == true;
            f.ShortageAmount = N(_fShortageSum, 300);
            f.QuietFromHour = (int)N(_fQuietFrom, 22);
            f.QuietToHour = (int)N(_fQuietTo, 8);

            var g = d.Guard;
            g.Enabled = _gEnabled.IsChecked == true;
            g.Discount = _gDiscount.IsChecked == true;
            g.DiscountPercent = N(_gDiscountPercent, 10);
            g.Return = _gReturn.IsChecked == true;
            g.ReturnAmount = N(_gReturnSum, 0);
            g.RemovalAfterRing = _gRemoval.IsChecked == true;
            g.RemovalAmount = N(_gRemovalSum, 500);
            g.ShiftShortage = _gShortage.IsChecked == true;
            g.ShortageAmount = N(_gShortageSum, 300);

            var r = d.Receipts;
            r.Enabled = _rEnabled.IsChecked == true;
            r.Bonus = _rBonus.IsChecked == true;
            r.QrOnCustomerDisplay = _rQr.IsChecked == true;
            r.LinkOnPaper = _rPaper.IsChecked == true;
        });
        PosLogger.Log("Подозрительные операции: настройки сохранены.", "INFO");
        return true;
    }
}
