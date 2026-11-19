using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-13 (Н-1, «Первый час»): мастер первого запуска нового магазина — пять шагов с одной главной кнопкой на каждом:
/// вид магазина → принтер чеков → весы (по желанию) → первые товары (вручную или из файла) → учебная продажа.
/// Сам ничего не настраивает: каждый шаг вызывает уже существующее окно кассы (действия передаёт окно кассы, <see cref="Actions"/>), любой шаг можно пропустить.
/// Показывается один раз новому магазину (признак — файл <see cref="DoneFlagFile"/> в папке данных кассы) и повторно — из меню «Помощь».
/// Результат окна: true — кассир выбрал «Начать учебную продажу».</summary>
public sealed class FirstRunWizardWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public const int Steps = 5;

    /// <summary>Файл-признак «мастер уже показан» в %LOCALAPPDATA%\NurMarketKassa (настройки и база не меняются).</summary>
    public const string DoneFlagFile = "first-run-wizard.done";

    /// <summary>Что делают кнопки шагов. Каждое действие возвращает строку итога для экрана («Принтер подключён: …») или null — ничего не изменилось.</summary>
    public sealed class Actions
    {
        public Func<string> SphereText { get; init; } = () => "";
        public Func<Task<string?>>? ChooseSphere { get; init; }
        public Func<Task<string?>>? SetupPrinter { get; init; }
        public Func<Task<string?>>? SetupScale { get; init; }
        public Func<Task<string?>>? AddProducts { get; init; }
        public Func<Task<string?>>? ImportProducts { get; init; }
        public Func<int> ProductCount { get; init; } = () => 0;
    }

    public static string WizardTitle => T("Первый запуск", "Биринчи ишке киргизүү", "First start", "İlk başlangıç", "Birinchi ishga tushirish");

    private readonly Actions _actions;
    private readonly ContentControl _body = new();
    private readonly TextBlock _stepText = new() { FontWeight = FontWeight.SemiBold };
    private readonly Button _back, _next, _later;
    private readonly string?[] _status = new string?[Steps + 1];
    private int _step = 1;
    private bool _busy;

    /// <summary>Текущий шаг (для тестов).</summary>
    public int Step => _step;

    public FirstRunWizardWindow() : this(new Actions())
    {
    }

    public FirstRunWizardWindow(Actions actions)
    {
        _actions = actions;
        Title = WizardTitle;
        Width = 672;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        _stepText.Bind(TextBlock.ForegroundProperty, _stepText.GetResourceObservable("BrushTextSoft"));
        _stepText.Bind(TextBlock.FontSizeProperty, _stepText.GetResourceObservable("NurFontCaption"));

        _later = new Button { Name = "WizardLater", Content = T("Закончить позже", "Кийинчерээк бүтүрүү", "Finish later", "Sonra bitir", "Keyinroq tugatish"), IsCancel = true };
        _later.Classes.Add("nur-ghost");
        _later.Click += (_, _) => Close(false);
        _back = new Button { Name = "WizardBack", Content = T("Назад", "Артка", "Back", "Geri", "Orqaga") };
        _back.Classes.Add("nur-secondary");
        _back.Click += (_, _) => Go(_step - 1);
        _next = new Button { Name = "WizardNext", IsDefault = true };
        _next.Classes.Add("nur-primary");
        _next.Click += (_, _) =>
        {
            if (_busy)
                return;
            if (_step == Steps)
                Close(true);
            else
                Go(_step + 1);
        };

        Content = new ModalShell
        {
            Margin = new Thickness(16),
            Title = WizardTitle,
            Subtitle = T("Пять коротких шагов — и можно продавать. Любой шаг можно пропустить и вернуться к нему позже: меню ☰ → «Помощь».",
                "Беш кыска кадам — анан сата берсеңиз болот. Каалаган кадамды өткөрүп, кийин кайтууга болот: ☰ меню → «Жардам».",
                "Five short steps and you can sell. Any step can be skipped and done later: menu ☰ → “Help”.",
                "Beş kısa adım — sonra satışa başlayabilirsiniz. Her adım atlanabilir, sonra dönebilirsiniz: ☰ menü → «Yardım».",
                "Besh qisqa qadam — va sotishingiz mumkin. Istalgan qadamni o'tkazib, keyin qaytish mumkin: ☰ menyu → «Yordam»."),
            Hint = _stepText,
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _later, _back, _next } },
            Content = _body,
        };
        PosLogger.Log("[UI] Мастер первого запуска: открыт.", "SETTINGS");
        Go(1);
    }

    private void Go(int step)
    {
        if (step < 1 || step > Steps)
            return;
        _step = step;
        _stepText.Text = T($"Шаг {step} из {Steps}", $"{Steps} кадамдын {step}-кадамы", $"Step {step} of {Steps}", $"Adım {step} / {Steps}", $"{Steps} qadamdan {step}-qadam");
        _body.Content = BuildStep(step);
        _back.IsVisible = step > 1;
        _next.Content = step == Steps
            ? T("Начать учебную продажу", "Окуу сатуусун баштоо", "Start a training sale", "Eğitim satışını başlat", "O'quv sotuvini boshlash")
            : _status[step] is null ? T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish") : T("Далее", "Кийинки", "Next", "İleri", "Keyingi");
        _later.Content = step == Steps
            ? T("Закончить без учебной продажи", "Окуу сатуусуз бүтүрүү", "Finish without training", "Eğitim satışı olmadan bitir", "O'quv sotuvisiz tugatish")
            : T("Закончить позже", "Кийинчерээк бүтүрүү", "Finish later", "Sonra bitir", "Keyinroq tugatish");
    }

    private Control BuildStep(int step) => step switch
    {
        1 => StepPanel(
            T("Какой у вас магазин?", "Дүкөнүңүз кандай?", "What kind of store do you have?", "Mağazanız ne tür?", "Do'koningiz qanday?"),
            T("От вида магазина зависит, что касса показывает: продукты и весы, размеры одежды, услуги.",
                "Дүкөндүн түрүнө жараша касса эмнени көрсөтөрү өзгөрөт: азык-түлүк жана тараза, кийим өлчөмдөрү, кызматтар.",
                "The store type decides what the till shows: groceries and scales, clothing sizes, services.",
                "Kasanın ne göstereceği mağaza türüne bağlıdır: gıda ve terazi, giyim bedenleri, hizmetler.",
                "Kassa nimani ko'rsatishi do'kon turiga bog'liq: oziq-ovqat va tarozi, kiyim o'lchamlari, xizmatlar."),
            CurrentLine(T("Сейчас: ", "Азыр: ", "Now: ", "Şimdi: ", "Hozir: ") + _actions.SphereText()),
            ActionButton("WizardSphere", T("Выбрать вид магазина", "Дүкөндүн түрүн тандоо", "Choose the store type", "Mağaza türünü seç", "Do'kon turini tanlash"), _actions.ChooseSphere, primary: _status[1] is null)),
        2 => StepPanel(
            T("Принтер чеков", "Чек принтери", "Receipt printer", "Fiş yazıcısı", "Chek printeri"),
            T("Подключите принтер к компьютеру и включите его. Касса сама найдёт его и напечатает пробный чек. Принтера нет — пропустите, продавать можно и без чека.",
                "Принтерди компьютерге туташтырып, күйгүзүңүз. Касса аны өзү таап, сыноо чегин басып чыгарат. Принтер жок болсо — өткөрүп жибериңиз, чексиз да сатууга болот.",
                "Connect the printer to the computer and turn it on. The till finds it and prints a test receipt. No printer — skip, you can sell without receipts.",
                "Yazıcıyı bilgisayara bağlayıp açın. Kasa onu kendisi bulur ve deneme fişi basar. Yazıcı yoksa atlayın, fişsiz de satış yapılabilir.",
                "Printerni kompyuterga ulab, yoqing. Kassa uni o'zi topadi va sinov chekini chiqaradi. Printer bo'lmasa — o'tkazib yuboring, cheksiz ham sotish mumkin."),
            ActionButton("WizardPrinter", T("Подключить принтер", "Принтерди туташтыруу", "Connect the printer", "Yazıcıyı bağla", "Printerni ulash"), _actions.SetupPrinter, primary: _status[2] is null)),
        3 => StepPanel(
            T("Весы — если есть", "Тараза — эгер болсо", "Scales — if you have them", "Terazi — varsa", "Tarozi — agar bo'lsa"),
            T("Продаёте на вес и есть весы с этикетками или весы у кассы — настройте их здесь. Нет весов — пропустите этот шаг.",
                "Салмак менен сатсаңыз жана этикеткалуу же кассанын жанындагы тараза болсо — аны ушул жерден жөндөңүз. Тараза жок болсо — бул кадамды өткөрүп жибериңиз.",
                "If you sell by weight and have label scales or scales at the till, set them up here. No scales — skip this step.",
                "Tartıyla satıyorsanız ve etiket terazisi ya da kasa terazisi varsa buradan ayarlayın. Terazi yoksa bu adımı atlayın.",
                "Tortib sotsangiz va yorliqli yoki kassa yonidagi tarozi bo'lsa — shu yerda sozlang. Tarozi bo'lmasa — bu qadamni o'tkazib yuboring."),
            ActionButton("WizardScale", T("Настроить весы", "Таразаны жөндөө", "Set up scales", "Teraziyi ayarla", "Tarozini sozlash"), _actions.SetupScale, primary: false)),
        4 => StepPanel(
            T("Первые товары", "Алгачкы товарлар", "First products", "İlk ürünler", "Birinchi mahsulotlar"),
            T("Добавьте хотя бы три товара: отсканируйте штрихкод, впишите название и цену. Товаров много — загрузите список из файла Excel (CSV).",
                "Жок дегенде үч товар кошуңуз: штрихкодду сканерлеп, атын жана баасын жазыңыз. Товар көп болсо — тизмени Excel (CSV) файлынан жүктөңүз.",
                "Add at least three products: scan the barcode, type the name and the price. Many products — load the list from an Excel (CSV) file.",
                "En az üç ürün ekleyin: barkodu okutun, adını ve fiyatını yazın. Ürün çoksa listeyi Excel (CSV) dosyasından yükleyin.",
                "Kamida uchta mahsulot qo'shing: shtrix-kodni skanerlang, nomi va narxini yozing. Mahsulot ko'p bo'lsa — ro'yxatni Excel (CSV) faylidan yuklang."),
            CurrentLine(ProductCountText(_actions.ProductCount())),
            new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    ActionButton("WizardAddProducts", T("Добавить товары", "Товар кошуу", "Add products", "Ürün ekle", "Mahsulot qo'shish"), _actions.AddProducts, primary: _actions.ProductCount() < 3),
                    ActionButton("WizardImportProducts", T("Загрузить из файла", "Файлдан жүктөө", "Load from a file", "Dosyadan yükle", "Fayldan yuklash"), _actions.ImportProducts, primary: false),
                },
            }),
        _ => StepPanel(
            T("Учебная продажа", "Окуу сатуусу", "Training sale", "Eğitim satışı", "O'quv sotuvi"),
            T("Попробуйте продать без риска. В учебном режиме касса подсказывает каждый шаг: товар в чек → «Оплатить» → деньги покупателя → готово. Продажа никуда не записывается: ни в смену, ни в отчёты, ни в остатки. Выйти из режима — кнопкой на жёлтой полосе сверху.",
                "Тобокелсиз сатып көрүңүз. Окуу режиминде касса ар бир кадамды айтып турат: товар чекке → «Төлөө» → сатып алуучунун акчасы → даяр. Сатуу эч жерге жазылбайт: сменага да, отчётторго да, калдыктарга да. Режимден чыгуу — жогорудагы сары тилкедеги баскыч менен.",
                "Try selling with no risk. In training mode the till prompts every step: item to the receipt → “Pay” → the customer's money → done. The sale is not recorded anywhere: not in the shift, reports or stock. Leave the mode with the button on the yellow bar at the top.",
                "Risksiz satış deneyin. Eğitim modunda kasa her adımı söyler: ürün fişe → «Öde» → müşterinin parası → tamam. Satış hiçbir yere kaydedilmez: vardiyaya, raporlara, stoka. Moddan çıkmak için üstteki sarı şeritteki düğmeyi kullanın.",
                "Xavfsiz sotib ko'ring. O'quv rejimida kassa har bir qadamni aytib turadi: mahsulot chekka → «To'lash» → xaridorning puli → tayyor. Sotuv hech qayerga yozilmaydi: smenaga ham, hisobotlarga ham, qoldiqlarga ham. Rejimdan chiqish — yuqoridagi sariq chiziqdagi tugma bilan.")),
    };

    public static string ProductCountText(int count) => count <= 0
        ? T("В каталоге пока нет товаров.", "Каталогдо азырынча товар жок.", "There are no products in the catalog yet.", "Katalogda henüz ürün yok.", "Katalogda hozircha mahsulot yo'q.")
        : T($"Товаров в каталоге: {count}.", $"Каталогдогу товарлар: {count}.", $"Products in the catalog: {count}.", $"Katalogdaki ürün sayısı: {count}.", $"Katalogdagi mahsulotlar: {count}.");

    private Control StepPanel(string title, string text, params Control[] extra)
    {
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        titleBlock.Bind(TextBlock.FontSizeProperty, titleBlock.GetResourceObservable("NurFontTitle"));
        var textBlock = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        textBlock.Bind(TextBlock.FontSizeProperty, textBlock.GetResourceObservable("NurFontBody"));
        var panel = new StackPanel { Spacing = 12, MinHeight = 210, Children = { titleBlock, textBlock } };
        foreach (var control in extra)
            panel.Children.Add(control);
        if (_status[_step] is { Length: > 0 } status)
        {
            var done = new TextBlock { Name = "WizardStatus", Text = "✓ " + status, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
            done.Bind(TextBlock.FontSizeProperty, done.GetResourceObservable("NurFontBody"));
            done.Bind(TextBlock.ForegroundProperty, done.GetResourceObservable("BrushUiStatusOk"));
            panel.Children.Add(done);
        }
        return panel;
    }

    private static Control CurrentLine(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable("NurFontBody"));
        return block;
    }

    private Button ActionButton(string name, string text, Func<Task<string?>>? action, bool primary)
    {
        var button = new Button { Name = name, Content = text, Margin = new Thickness(0, 0, 10, 6), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = action is not null };
        button.Classes.Add(primary ? "nur-primary" : "nur-secondary");
        button.Click += async (_, _) =>
        {
            if (_busy || action is null)
                return;
            _busy = true;
            var step = _step;
            try
            {
                var result = await action().ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(result))
                    _status[step] = result;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Мастер первого запуска: шаг {step} не выполнен: {ex.GetType().Name}: {ex.Message}", "WARNING");
            }
            finally
            {
                _busy = false;
            }
            if (_step == step && IsVisible)
                Go(step);
        };
        return button;
    }

    // ── признак «мастер уже показан» ────────────────────────────────────────────────────────────────────

    /// <param name="kassaLocalFolder">Папка данных кассы в «Local» (в тесте — временная); null — настоящая.</param>
    public static bool IsDone(string? kassaLocalFolder = null)
    {
        try
        {
            return File.Exists(Path.Combine(kassaLocalFolder ?? AppPaths.KassaLocal, DoneFlagFile));
        }
        catch
        {
            return true; // папку не прочитать — мастер сам не навязываем
        }
    }

    public static void MarkDone(string? kassaLocalFolder = null)
    {
        try
        {
            var folder = kassaLocalFolder ?? AppPaths.KassaLocal;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, DoneFlagFile), DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер первого запуска: признак «показан» не записан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Показывать ли мастер сам при запуске: ещё не показывали, в каталоге нет товаров (новый магазин) и кассиру можно менять настройки.</summary>
    public static bool ShouldOfferAtStartup(bool alreadyDone, int productCount, bool canChangeSettings) =>
        !alreadyDone && productCount == 0 && canChangeSettings;
}
