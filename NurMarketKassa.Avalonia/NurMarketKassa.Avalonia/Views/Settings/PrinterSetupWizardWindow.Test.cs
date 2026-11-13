using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-13: шаг 5 мастера «Подключить принтер» — пробная печать, выбор кодировки по ленте, «Напечаталось правильно?» и сохранение.
public sealed partial class PrinterSetupWizardWindow
{
    private StackPanel? _step5;
    private Button _testPrint = null!;
    private readonly TextBlock _testStatus = Text("", 13);
    private RadioButton _rbEnc1 = null!, _rbEnc2 = null!;
    private StackPanel _encPanel = null!, _problems = null!;
    private readonly TextBlock _problemHint = Text("", 13);
    private readonly TextBlock _step5Title = Text("", 15, true);
    private bool _testSent;

    /// <summary>Сколько пробных печатей отправлено (для тестов).</summary>
    public int TestPrints { get; private set; }

    private Control EnterStep5()
    {
        if (_step5 is null)
        {
            _testPrint = new Button { Name = "WizardTestPrint", MinHeight = 48, Padding = new Thickness(16, 6), HorizontalAlignment = HorizontalAlignment.Left };
            _testPrint.Classes.Add("modal-primary");
            _testPrint.Click += (_, _) => TestPrint();
            var enc = PrinterTestPages.VariantForEncoding(UserPreferences.Instance.ReceiptEncoding);
            _rbEnc1 = Radio("WizardEnc1", "wizEnc", T("Строка 1 (CP866) читается правильно", "1-сап (CP866) туура окулат", "Line 1 (CP866) reads correctly", "Satır 1 (CP866) doğru okunuyor", "1-qator (CP866) to'g'ri o'qiladi"), enc.Number == 1);
            _rbEnc2 = Radio("WizardEnc2", "wizEnc", T("Строка 2 (CP1251) читается правильно", "2-сап (CP1251) туура окулат", "Line 2 (CP1251) reads correctly", "Satır 2 (CP1251) doğru okunuyor", "2-qator (CP1251) to'g'ri o'qiladi"), enc.Number == 2);
            _encPanel = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    Text(T("На ленте две строки с русскими буквами. Какая читается без «иероглифов» и знаков вопроса?", "Лентада орусча тамгалары бар эки сап. Кайсынысы «иероглифсиз» жана суроо белгилерисиз окулат?",
                        "The tape has two lines with Russian letters. Which one reads without “hieroglyphs” and question marks?", "Ruloda Rusça harfli iki satır var. Hangisi «hiyeroglif» ve soru işareti olmadan okunuyor?",
                        "Lentada rus harflari bilan ikki qator bor. Qaysi biri «ierogliflar» va so'roq belgilarisiz o'qiladi?"), 12.5, true),
                    _rbEnc1, _rbEnc2,
                },
            };
            var yes = Action("WizardPrintedOk", T("Да, всё правильно — сохранить", "Ооба, баары туура — сактоо", "Yes, all correct — save", "Evet, her şey doğru — kaydet", "Ha, hammasi to'g'ri — saqlash"));
            yes.Click += (_, _) => Save();
            var no = Action("WizardPrintedBad", T("Нет — что не так?", "Жок — эмне туура эмес?", "No — what is wrong?", "Hayır — sorun ne?", "Yo'q — nima noto'g'ri?"));
            no.Click += (_, _) => ShowProblems();
            _problems = new StackPanel { Spacing = 6, IsVisible = false };
            _step5 = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    _step5Title, _testPrint, _testStatus, _encPanel,
                    Text(T("Напечаталось правильно?", "Туура басылдыбы?", "Did it print correctly?", "Doğru yazdırıldı mı?", "To'g'ri chop etildimi?"), 13.5, true),
                    ButtonRow(yes, no),
                    _problems, _problemHint,
                },
            };
        }
        _step5Title.Text = IsLabel
            ? T("Пробная этикетка", "Сыноо этикеткасы", "Test label", "Deneme etiketi", "Sinov yorlig'i")
            : T("Пробный чек", "Сыноо чеги", "Test receipt", "Deneme fişi", "Sinov cheki");
        _testPrint.Content = IsLabel
            ? T("Напечатать пробную этикетку", "Сыноо этикеткасын басуу", "Print a test label", "Deneme etiketi yazdır", "Sinov yorlig'ini chop etish")
            : T("Напечатать пробный чек", "Сыноо чегин басуу", "Print a test receipt", "Deneme fişi yazdır", "Sinov chekini chop etish");
        _encPanel.IsVisible = !IsLabel;
        _problems.IsVisible = false;
        _problemHint.Text = "";
        if (!_testSent)
            _testStatus.Text = T("Пробная печать расходует немного бумаги. Сохранить можно и без неё.", "Сыноо басуу бир аз кагаз сарптайт. Ансыз да сактаса болот.", "A test print uses a little paper. You can save without it.", "Deneme baskısı biraz kağıt harcar. Onsuz da kaydedebilirsiniz.", "Sinov chop etish ozgina qog'oz sarflaydi. Usiz ham saqlash mumkin.");
        return _step5;
    }

    /// <summary>Байты пробной печати по выбору на шагах 1–4.</summary>
    internal byte[] BuildTestBytes()
    {
        if (IsLabel)
        {
            var (w, h) = LabelSize;
            return LabelLanguage == PrinterLanguage.Tspl ? PrinterTestPages.BuildTsplTestLabel(w, h, Gap) : PrinterTestPages.BuildEscPosLabelTest();
        }
        var (paper, narrow) = PaperChoice;
        return PrinterTestPages.BuildReceiptTest(paper, narrow, cut: true, openDrawer: _drawerCheck.IsChecked == true, drawerPin: PinChoice);
    }

    private int PinChoice => (_pinCombo.SelectedItem as ComboBoxItem)?.Tag as string == "1" ? 1 : 0;

    private async void TestPrint()
    {
        var path = _devicePath;
        if (string.IsNullOrWhiteSpace(path) || !_shell.TryBeginBusy())
            return;
        _testStatus.Text = T("Отправляю на принтер…", "Принтерге жөнөтүп жатам…", "Sending to the printer…", "Yazıcıya gönderiliyor…", "Printerga yuborilmoqda…");
        try
        {
            var bytes = BuildTestBytes();
            if (SendOverride is { } fake)
                await fake(path, bytes);
            else
                await Task.Run(() => PrinterPortService.SendRawBytes(path, bytes, retries: 1));
            TestPrints++;
            _testSent = true;
            PosLogger.Log($"Мастер принтера: пробная печать ({(IsLabel ? "этикетка " + LabelLanguage : "чек")}) отправлена на «{path}», {bytes.Length} байт.", "PRINTER");
            _testStatus.Text = IsLabel
                ? T("Отправлено. Должна выйти этикетка с рамкой, надписью «NurCRM TEST» и штрихкодом.", "Жөнөтүлдү. Алкагы, «NurCRM TEST» жазуусу жана штрих-коду бар этикетка чыгышы керек.", "Sent. A label with a frame, the text “NurCRM TEST” and a barcode should come out.", "Gönderildi. Çerçeveli, «NurCRM TEST» yazılı ve barkodlu bir etiket çıkmalı.", "Yuborildi. Ramkali, «NurCRM TEST» yozuvli va shtrix-kodli yorliq chiqishi kerak.")
                : T("Отправлено. Должен выйти чек «NurCRM - TEST» с двумя строками русских букв и линейкой цифр.", "Жөнөтүлдү. Орусча тамгалуу эки сап жана сандар сызгычы бар «NurCRM - TEST» чеги чыгышы керек.", "Sent. A “NurCRM - TEST” receipt with two lines of Russian letters and a digit ruler should come out.", "Gönderildi. İki satır Rusça harf ve rakam cetveli olan «NurCRM - TEST» fişi çıkmalı.", "Yuborildi. Ikki qator rus harflari va raqamlar chizg'ichi bo'lgan «NurCRM - TEST» cheki chiqishi kerak.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер принтера: пробная печать не ушла на «{path}»: {ex.Message}", "PRINTER");
            _testStatus.Text = T("Не удалось отправить: ", "Жөнөтүү мүмкүн болгон жок: ", "Could not send: ", "Gönderilemedi: ", "Yuborib bo'lmadi: ") + ex.Message
                + "\n" + T("Проверьте питание и кабель; кнопка «Принтер не печатает?» в настройках печати покажет причину.", "Кубатты жана кабелди текшериңиз; басып чыгаруу жөндөөлөрүндөгү «Принтер баспай жатабы?» баскычы себебин көрсөтөт.",
                    "Check power and cable; the “Printer not printing?” button in print settings will show the reason.", "Gücü ve kabloyu kontrol edin; yazdırma ayarlarındaki «Yazıcı yazdırmıyor mu?» düğmesi nedenini gösterir.", "Quvvat va kabelni tekshiring; chop etish sozlamalaridagi «Printer chop etmayaptimi?» tugmasi sababini ko'rsatadi.");
        }
        finally
        {
            _shell.EndBusy();
        }
    }

    // ------------------------------------------------------------------ «Нет — что не так?»

    private void ShowProblems()
    {
        _problems.Children.Clear();
        var items = IsLabel
            ? new (string Name, string Symptom, string Hint)[]
            {
                ("ProblemNothing", T("Ничего не вышло", "Эч нерсе чыккан жок", "Nothing came out", "Hiçbir şey çıkmadı", "Hech narsa chiqmadi"), NothingHint),
                ("ProblemBlank", T("Вышла пустая этикетка или протянуло несколько", "Бош этикетка чыкты же бир нечеси тартылды", "A blank label came out or several were fed", "Boş etiket çıktı veya birkaç tane ilerledi", "Bo'sh yorliq chiqdi yoki bir nechtasi tortildi"),
                    T("Принтер не знает размер наклейки: выключите его, зажмите кнопку FEED и включите — отпустите после 1–2 миганий (калибровка). Проверьте размер этикетки и зазор на шаге 4.",
                        "Принтер чаптаманын өлчөмүн билбейт: аны өчүрүп, FEED баскычын кармап туруп күйгүзүңүз — 1–2 жымылдагандан кийин коё бериңиз (калибровка). 4-кадамда этикетканын өлчөмүн жана боштукту текшериңиз.",
                        "The printer does not know the sticker size: turn it off, hold FEED and turn it on — release after 1–2 blinks (calibration). Check the label size and gap in step 4.",
                        "Yazıcı etiket boyutunu bilmiyor: kapatın, FEED düğmesini basılı tutup açın — 1–2 yanıp sönmeden sonra bırakın (kalibrasyon). 4. adımda etiket boyutunu ve boşluğu kontrol edin.",
                        "Printer yorliq o'lchamini bilmaydi: uni o'chiring, FEED tugmasini bosib turib yoqing — 1–2 marta miltillagandan keyin qo'yib yuboring (kalibrlash). 4-qadamda yorliq o'lchami va tirqishni tekshiring.")),
                ("ProblemCommandsText", T("Вместо этикетки напечатались слова SIZE, GAP, BARCODE", "Этикетканын ордуна SIZE, GAP, BARCODE сөздөрү басылды", "The words SIZE, GAP, BARCODE printed instead of a label", "Etiket yerine SIZE, GAP, BARCODE kelimeleri basıldı", "Yorliq o'rniga SIZE, GAP, BARCODE so'zlari chop etildi"),
                    T("Это чековый (ESC/POS) принтер: на шаге 4 выберите «ESC/POS».", "Бул чек (ESC/POS) принтери: 4-кадамда «ESC/POS» тандаңыз.", "This is a receipt (ESC/POS) printer: choose “ESC/POS” in step 4.", "Bu bir fiş (ESC/POS) yazıcısı: 4. adımda «ESC/POS»u seçin.", "Bu chek (ESC/POS) printeri: 4-qadamda «ESC/POS» ni tanlang.")),
                ("ProblemShifted", T("Печать съехала или обрезана", "Басуу жылып кетти же кесилди", "The print is shifted or cut off", "Baskı kaydı veya kesildi", "Chop etish siljidi yoki kesildi"),
                    T("Проверьте размер этикетки на шаге 4 — он должен совпадать с наклейками в принтере. Точный сдвиг настраивается в Настройки → Печать → принтер этикеток.", "4-кадамда этикетканын өлчөмүн текшериңиз — ал принтердеги чаптамаларга дал келиши керек. Так жылдыруу Жөндөөлөр → Басып чыгаруу → этикетка принтеринде жөндөлөт.",
                        "Check the label size in step 4 — it must match the stickers in the printer. Fine offset is set in Settings → Printing → label printer.", "4. adımda etiket boyutunu kontrol edin — yazıcıdaki etiketlerle aynı olmalı. İnce kaydırma Ayarlar → Yazdırma → etiket yazıcısında ayarlanır.",
                        "4-qadamda yorliq o'lchamini tekshiring — u printerdagi yorliqlarga mos kelishi kerak. Aniq siljish Sozlamalar → Chop etish → yorliq printerida sozlanadi.")),
            }
            : new (string Name, string Symptom, string Hint)[]
            {
                ("ProblemNothing", T("Ничего не вышло", "Эч нерсе чыккан жок", "Nothing came out", "Hiçbir şey çıkmadı", "Hech narsa chiqmadi"), NothingHint),
                ("ProblemBothBad", T("Обе строки — «иероглифы» или знаки вопроса", "Эки сап тең — «иероглиф» же суроо белгилери", "Both lines are “hieroglyphs” or question marks", "Her iki satır da «hiyeroglif» veya soru işareti", "Ikkala qator ham — «ieroglif» yoki so'roq belgilari"),
                    T("Принтер не знает эти таблицы кириллицы. Сохраните мастер, затем в Настройки → Печать включите «Графический режим» — чек будет печататься картинкой, буквы любые.",
                        "Принтер бул кирилл таблицаларын билбейт. Чеберди сактап, андан кийин Жөндөөлөр → Басып чыгаруу бөлүмүндө «Графикалык режимди» күйгүзүңүз — чек сүрөт катары басылат.",
                        "The printer does not know these Cyrillic tables. Save the wizard, then turn on “Graphic mode” in Settings → Printing — the receipt will print as an image, any letters work.",
                        "Yazıcı bu Kiril tablolarını bilmiyor. Sihirbazı kaydedin, sonra Ayarlar → Yazdırma'da «Grafik mod»u açın — fiş resim olarak basılır.",
                        "Printer bu kirill jadvallarini bilmaydi. Ustani saqlang, so'ng Sozlamalar → Chop etish bo'limida «Grafik rejim»ni yoqing — chek rasm sifatida chop etiladi.")),
                ("ProblemWidth", T("Линейка цифр перенеслась на вторую строку или не доходит до края", "Сандар сызгычы экинчи сапка өттү же четине жетпейт", "The digit ruler wrapped to a second line or stops short of the edge", "Rakam cetveli ikinci satıra geçti veya kenara ulaşmıyor", "Raqamlar chizg'ichi ikkinchi qatorga o'tdi yoki chetiga yetmaydi"),
                    T("Выберите другую ширину ленты на шаге 4: перенос — меньше (80 мм → «42 знака» или 58 мм), не доходит до края — больше.", "4-кадамда башка лента туурасын тандаңыз: өтүп кетсе — кичирээк (80 мм → «42 белги» же 58 мм), четине жетпесе — чоңураак.",
                        "Choose another paper width in step 4: wrapped — smaller (80 mm → “42 characters” or 58 mm); short of the edge — larger.", "4. adımda başka bir rulo genişliği seçin: satır kaydıysa daha küçük (80 mm → «42 karakter» veya 58 mm), kenara ulaşmıyorsa daha büyük.",
                        "4-qadamda boshqa lenta kengligini tanlang: o'tib ketsa — kichikroq (80 mm → «42 belgi» yoki 58 mm), chetiga yetmasa — kattaroq.")),
                ("ProblemNoCut", T("Не отрезал", "Кескен жок", "It did not cut", "Kesmedi", "Kesmadi"),
                    T("У принтера нет ножа (у XP-58 обычно нет) — это нормально, отрывайте чек по кромке.", "Принтерде бычак жок (XP-58'де адатта болбойт) — бул кадимки нерсе, чекти четинен үзүңүз.", "The printer has no cutter (XP-58 usually has none) — that is normal, tear the receipt along the edge.", "Yazıcıda bıçak yok (XP-58'de genellikle olmaz) — bu normal, fişi kenardan yırtın.", "Printerda pichoq yo'q (XP-58 da odatda bo'lmaydi) — bu normal, chekni chetidan yirting.")),
                ("ProblemLabelText", T("Вышли слова SIZE, GAP или пустая наклейка", "SIZE, GAP сөздөрү же бош чаптама чыкты", "The words SIZE, GAP or a blank sticker came out", "SIZE, GAP kelimeleri veya boş etiket çıktı", "SIZE, GAP so'zlari yoki bo'sh yorliq chiqdi"),
                    T("Похоже, это принтер этикеток: вернитесь на шаг 1 и выберите «Принтер этикеток».", "Бул этикетка принтери окшойт: 1-кадамга кайтып, «Этикетка принтерин» тандаңыз.", "This looks like a label printer: go back to step 1 and choose “Label printer”.", "Bu bir etiket yazıcısına benziyor: 1. adıma dönüp «Etiket yazıcısı»nı seçin.", "Bu yorliq printeriga o'xshaydi: 1-qadamga qayting va «Yorliq printeri» ni tanlang.")),
            };
        foreach (var (name, symptom, hint) in items)
        {
            var b = Action("Wizard" + name, symptom);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Click += (_, _) =>
            {
                _problemHint.Text = hint;
                PosLogger.Log($"Мастер принтера: «напечаталось неправильно» — {name}.", "PRINTER");
            };
            _problems.Children.Add(b);
        }
        _problems.IsVisible = true;
    }

    private static string NothingHint => T("Проверьте: принтер включён (горит лампочка), бумага вставлена термослоем к головке (ноготь оставляет тёмный след), крышка закрыта. Затем нажмите «Напечатать» ещё раз. Не помогло — «Назад» и выберите другой принтер или кнопку «Принтер не печатает?» в настройках печати.",
        "Текшериңиз: принтер күйүк (лампочка күйөт), кагаз термо катмары башка карай салынган (тырмак кара из калтырат), капкак жабык. Андан кийин «Басуу» баскычын кайра басыңыз. Жардам бербесе — «Артка» басып, башка принтерди тандаңыз же басып чыгаруу жөндөөлөрүндөгү «Принтер баспай жатабы?» баскычын басыңыз.",
        "Check: the printer is on (the light is lit), the paper is loaded thermal side to the head (a fingernail leaves a dark mark), the cover is closed. Then press “Print” again. If that does not help, press “Back” and choose another printer, or use “Printer not printing?” in print settings.",
        "Kontrol edin: yazıcı açık (ışık yanıyor), kağıt termal yüzü kafaya bakacak şekilde takılı (tırnak koyu iz bırakır), kapak kapalı. Sonra «Yazdır»a tekrar basın. İşe yaramazsa «Geri»ye basıp başka yazıcı seçin veya yazdırma ayarlarındaki «Yazıcı yazdırmıyor mu?» düğmesini kullanın.",
        "Tekshiring: printer yoqilgan (chiroq yonib turibdi), qog'oz termo qatlami boshchaga qaratib solingan (tirnoq qora iz qoldiradi), qopqoq yopiq. So'ng «Chop etish» tugmasini qayta bosing. Yordam bermasa — «Orqaga» bosib, boshqa printerni tanlang yoki chop etish sozlamalaridagi «Printer chop etmayaptimi?» tugmasidan foydalaning.");

    // ------------------------------------------------------------------ сохранение

    private void Save()
    {
        var path = _devicePath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        var prefs = UserPreferences.Instance;
        try
        {
            if (IsLabel)
            {
                var (w, h) = LabelSize;
                var template = LabelTemplateStore.Load();
                var templateChanged = PrinterSetupApply.ApplyLabel(prefs, new LabelSetupChoice(path, LabelLanguage, w, h, Gap), template);
                if (templateChanged && PersistSettings)
                    LabelTemplateStore.Save(template);
            }
            else
            {
                var (paper, narrow) = PaperChoice;
                var variant = _rbEnc2.IsChecked == true ? PrinterTestPages.EncodingVariants[1] : PrinterTestPages.EncodingVariants[0];
                PrinterSetupApply.ApplyReceipt(prefs, new ReceiptSetupChoice(path, paper, narrow, variant.UserEncoding, _drawerCheck.IsChecked == true, PinChoice));
            }
            if (PersistSettings)
            {
                if (NetworkPrinterAddress.IsNetworkPort(path))
                    NetworkPrinterAddress.Remember(path); // сам сохраняет настройки
                prefs.SaveToDisk();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер принтера: сохранение не удалось ({ex.GetType().Name}: {ex.Message}).", "ERROR");
            _testStatus.Text = T("Не удалось сохранить настройки: ", "Жөндөөлөрдү сактоо мүмкүн болгон жок: ", "Could not save the settings: ", "Ayarlar kaydedilemedi: ", "Sozlamalarni saqlab bo'lmadi: ") + ex.Message;
            return;
        }
        PosLogger.Log($"[UI] Мастер принтера: сохранено ({RoleLog(EffectiveRole)}, «{path}»).", "PRINTER");
        Finish(new PrinterWizardResult(EffectiveRole, path));
    }
}
