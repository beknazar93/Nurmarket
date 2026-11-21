namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (владелец, п. 6): что должен сделать кассир, чтобы шаг обучения считался пройденным.
/// Manual — шаг-пояснение (идём «Далее»); остальное — настоящее действие в кассе.</summary>
public enum CoachCondition
{
    Manual,
    MenuOpened,
    MenuClosed,
    BasketHasLine,
    SaleDone,
}

/// <summary>Снимок состояния кассы для движка: меню открыто, строк в чеке, учебных продаж прошло.</summary>
public readonly record struct CoachSnapshot(bool MenuOpen, int BasketLines, int SalesDone);

/// <summary>Кнопка действия на шаге (мастер сфер, принтера, весов, товаров): ключ + подпись.</summary>
public sealed record CoachAction(string Key, Func<string> Label);

/// <summary>Один шаг обучения как данные: на какой элемент кассы указать (<see cref="TargetKey"/>: menu, menuPanel, input, catalog, cart,
/// discount, pay, shift; пусто — карточка по центру), тексты на 5 языках, условие завершения и нужен ли учебный режим.</summary>
public sealed record CoachStep(
    string Id,
    string TargetKey,
    Func<string> Title,
    Func<string> Text,
    CoachCondition Completion,
    bool EnterTraining = false,
    IReadOnlyList<CoachAction>? Actions = null);

/// <summary>2026-11-19: движок интерактивного обучения — чистая логика без окон (проверяется unit-тестами).
/// Шаг проходится, когда <see cref="Update"/> получает снимок, в котором условие выполнено; «Пропустить» и «Назад» работают всегда.
/// После «Назад» шаг не пролистывается сам, пока состояние кассы не изменится.</summary>
public sealed class CoachTourEngine
{
    private readonly IReadOnlyList<CoachStep> _steps;
    private readonly HashSet<int> _done = new();
    private readonly HashSet<int> _skipped = new();
    private CoachSnapshot _last;
    private int _salesBaseline;
    private CoachSnapshot? _hold;

    public CoachTourEngine(IReadOnlyList<CoachStep> steps, CoachSnapshot initial)
    {
        _steps = steps.Count > 0 ? steps : throw new ArgumentException("нужен хотя бы один шаг", nameof(steps));
        _last = initial;
        _salesBaseline = initial.SalesDone;
    }

    public IReadOnlyList<CoachStep> Steps => _steps;
    public int Count => _steps.Count;
    public int Index { get; private set; }
    public CoachStep Current => _steps[Index];
    /// <summary>Все шаги пройдены или пропущены — показывается экран «Обучение завершено!».</summary>
    public bool IsFinished { get; private set; }
    /// <summary>Кассир закрыл обучение крестиком / «Закончить позже».</summary>
    public bool IsAborted { get; private set; }
    public int CompletedCount => _done.Count;
    public int SkippedCount => _skipped.Count;
    public bool IsStepDone(int index) => _done.Contains(index);

    /// <summary>Шаг сменился, обучение закончилось или прервано.</summary>
    public event Action? Changed;

    /// <summary>Новое состояние кассы. true — шаг(и) пройдены, текущий сменился.</summary>
    public bool Update(CoachSnapshot snapshot)
    {
        _last = snapshot;
        if (IsFinished || IsAborted)
            return false;
        if (_hold is { } hold)
        {
            if (hold == snapshot)
                return false;
            _hold = null;
        }
        var moved = false;
        while (!IsFinished && Satisfied(Current, snapshot))
        {
            Advance(completed: true);
            moved = true;
            if (Current.Completion == CoachCondition.Manual)
                break;
        }
        if (moved)
            Changed?.Invoke();
        return moved;
    }

    /// <summary>«Далее» на шаге-пояснении (Manual) или «Пропустить» на шаге с действием.</summary>
    public void Next()
    {
        if (IsFinished || IsAborted)
            return;
        Advance(completed: Current.Completion == CoachCondition.Manual);
        Changed?.Invoke();
    }

    public void Back()
    {
        if (IsAborted || (!IsFinished && Index == 0))
            return;
        if (IsFinished)
            IsFinished = false;
        else
            Index--;
        _done.Remove(Index);
        _skipped.Remove(Index);
        _salesBaseline = _last.SalesDone;
        _hold = _last;
        Changed?.Invoke();
    }

    /// <summary>«Пройти ещё раз»: с первого шага, отметки стираются.</summary>
    public void Restart()
    {
        Index = 0;
        IsFinished = false;
        IsAborted = false;
        _done.Clear();
        _skipped.Clear();
        _salesBaseline = _last.SalesDone;
        _hold = _last;
        Changed?.Invoke();
    }

    public void Abort()
    {
        if (IsAborted)
            return;
        IsAborted = true;
        Changed?.Invoke();
    }

    private void Advance(bool completed)
    {
        (completed ? _done : _skipped).Add(Index);
        if (Index >= _steps.Count - 1)
        {
            IsFinished = true;
            return;
        }
        Index++;
        _salesBaseline = _last.SalesDone;
    }

    private bool Satisfied(CoachStep step, CoachSnapshot s) => step.Completion switch
    {
        CoachCondition.MenuOpened => s.MenuOpen,
        CoachCondition.MenuClosed => !s.MenuOpen,
        CoachCondition.BasketHasLine => s.BasketLines > 0,
        CoachCondition.SaleDone => s.SalesDone > _salesBaseline,
        _ => false,
    };
}

/// <summary>Сценарий обучения (данные: шаги и тексты на 5 языках), признак «обучение пройдено» и тексты экрана «Обучение завершено!».</summary>
public static class CoachTour
{
    /// <summary>Файл-признак «обучение пройдено» в %LOCALAPPDATA%\NurMarketKassa (как у first-run-wizard.done).</summary>
    public const string DoneFlagFile = "training-tour.done";

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static bool IsCompleted(string? kassaLocalFolder = null)
    {
        try
        {
            return File.Exists(Path.Combine(kassaLocalFolder ?? AppPaths.KassaLocal, DoneFlagFile));
        }
        catch
        {
            return false;
        }
    }

    public static void MarkCompleted(string? kassaLocalFolder = null)
    {
        try
        {
            var folder = kassaLocalFolder ?? AppPaths.KassaLocal;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, DoneFlagFile), DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обучение: признак «пройдено» не записан: {ex.Message}", "WARNING");
        }
    }

    public static IReadOnlyList<CoachStep> DefaultSteps() =>
    [
        new("sphere", "", () => T("Какой у вас магазин?", "Дүкөнүңүз кандай?", "What kind of store do you have?", "Mağazanız ne tür?", "Do'koningiz qanday?"),
            () => T("От вида магазина зависит, что показывает касса. Уже выбрано — нажмите «Далее».",
                "Дүкөндүн түрүнө жараша касса эмнени көрсөтөрү өзгөрөт. Тандалган болсо — «Кийинки» басыңыз.",
                "The store type decides what the till shows. Already chosen — press “Next”.",
                "Kasanın ne göstereceği mağaza türüne bağlıdır. Seçiliyse «İleri»ye basın.",
                "Kassa nimani ko'rsatishi do'kon turiga bog'liq. Tanlangan bo'lsa — «Keyingi»ni bosing."),
            CoachCondition.Manual, Actions:
            [
                new("sphere", () => T("Выбрать вид магазина", "Дүкөндүн түрүн тандоо", "Choose the store type", "Mağaza türünü seç", "Do'kon turini tanlash")),
                // 2026-11-20 (владелец: «где импорт из других программ?»): перенос товаров из 1С, МойСклад, Poster… — тот же мастер «Массовая загрузка»; кнопка только при праве на загрузку (MainWindow.RunCoachActionAsync).
                new("import", () => T("Перенести товары из другой программы", "Товарларды башка программадан которуу", "Move products from another program", "Ürünleri başka programdan aktar", "Mahsulotlarni boshqa dasturdan ko'chirish")),
            ]),
        new("menu-open", "menu", () => T("Меню кассы", "Кассанын менюсу", "The till menu", "Kasa menüsü", "Kassa menyusi"),
            () => T("Здесь меню: Настройки, Смена, Помощь. Нажмите кнопку ☰.", "Бул жерде меню: Жөндөөлөр, Смена, Жардам. ☰ баскычын басыңыз.",
                "This is the menu: Settings, Shift, Help. Press the ☰ button.", "Menü burada: Ayarlar, Vardiya, Yardım. ☰ düğmesine basın.",
                "Bu yerda menyu: Sozlamalar, Smena, Yordam. ☰ tugmasini bosing."),
            CoachCondition.MenuOpened),
        new("menu-close", "menuPanel", () => T("Настройки и помощь", "Жөндөөлөр жана жардам", "Settings and help", "Ayarlar ve yardım", "Sozlamalar va yordam"),
            () => T("Тут «Настройки», «Смена», «Помощь». Всё можно найти поиском сверху. Закройте меню — Esc или нажмите рядом с ним.",
                "Бул жерде «Жөндөөлөр», «Смена», «Жардам». Баарын жогорудагы издөө менен табасыз. Менюну жабыңыз — Esc же анын жанын басыңыз.",
                "Here are “Settings”, “Shift”, “Help”. Everything is searchable at the top. Close the menu — Esc or press next to it.",
                "Burada «Ayarlar», «Vardiya», «Yardım» var. Hepsi üstteki aramayla bulunur. Menüyü kapatın — Esc veya yanına basın.",
                "Bu yerda «Sozlamalar», «Smena», «Yordam». Hammasini yuqoridagi qidiruvdan topasiz. Menyuni yoping — Esc yoki yoniga bosing."),
            CoachCondition.MenuClosed),
        new("devices", "", () => T("Принтер и весы — если есть", "Принтер жана тараза — эгер болсо", "Printer and scales — if you have them", "Yazıcı ve terazi — varsa", "Printer va tarozi — agar bo'lsa"),
            () => T("Подключите принтер чеков и весы — касса сама найдёт их и подскажет. Нет устройств — пропустите, продавать можно и так.",
                "Чек принтерин жана таразаны туташтырыңыз — касса аларды өзү таап, айтып турат. Аппараттар жок болсо — өткөрүп жибериңиз, ансыз да сатууга болот.",
                "Connect the receipt printer and scales — the till finds them and guides you. No devices — skip, you can sell without them.",
                "Fiş yazıcısını ve teraziyi bağlayın — kasa onları bulur ve yönlendirir. Cihaz yoksa atlayın, onlarsız da satış yapılır.",
                "Chek printeri va tarozini ulang — kassa ularni o'zi topadi va yo'l ko'rsatadi. Qurilma bo'lmasa — o'tkazib yuboring, ularsiz ham sotish mumkin."),
            CoachCondition.Manual, Actions:
            [
                new("printer", () => T("Принтер чеков", "Чек принтери", "Receipt printer", "Fiş yazıcısı", "Chek printeri")),
                new("scales", () => T("Весы — «Быстрая настройка»", "Тараза — «Тез жөндөө»", "Scales — “Quick setup”", "Terazi — «Hızlı kurulum»", "Tarozi — «Tez sozlash»")),
            ]),
        new("find", "input", () => T("Найдите товар", "Товарды табыңыз", "Find a product", "Ürünü bulun", "Mahsulotni toping"),
            () => T("Отсканируйте штрихкод или впишите название сюда. Количество: «3*код». Или нажмите на плитку товара в каталоге. Включён учебный режим — деньги не учитываются.",
                "Штрихкодду сканерлеңиз же атын ушул жерге жазыңыз. Саны: «3*код». Же каталогдогу товардын плиткасын басыңыз. Окуу режими күйгүзүлдү — акча эсепке алынбайт.",
                "Scan a barcode or type a name here. Quantity: “3*code”. Or press a product tile in the catalog. Training mode is on — money is not recorded.",
                "Barkodu okutun veya adı buraya yazın. Adet: «3*kod». Ya da katalogdaki ürün kutusuna basın. Eğitim modu açık — para kaydedilmez.",
                "Shtrix-kodni skanerlang yoki nomini shu yerga yozing. Soni: «3*kod». Yoki katalogdagi mahsulot plitkasini bosing. O'quv rejimi yoqildi — pul hisobga olinmaydi."),
            CoachCondition.BasketHasLine, EnterTraining: true),
        new("cart", "cart", () => T("Корзина и скидка", "Себет жана арзандатуу", "Cart and discount", "Sepet ve indirim", "Savat va chegirma"),
            () => T("Товар в чеке. Количество меняют кнопками «+» и «−», скидку ставят на строку или на весь чек (кнопка «Скидка»). Нажмите «Далее».",
                "Товар чекте. Санын «+» жана «−» баскычтары менен өзгөртөсүз, арзандатууну саптын же бүт чектин үстүнө коёсуз («Арзандатуу» баскычы). «Кийинки» басыңыз.",
                "The item is in the receipt. Change the quantity with “+” and “−”, set a discount on a line or on the whole receipt (“Discount” button). Press “Next”.",
                "Ürün fişte. Adedi «+» ve «−» ile değiştirin, indirimi satıra veya tüm fişe verin («İndirim» düğmesi). «İleri»ye basın.",
                "Mahsulot chekda. Sonini «+» va «−» bilan o'zgartiring, chegirmani qatorga yoki butun chekka qo'ying («Chegirma» tugmasi). «Keyingi»ni bosing."),
            CoachCondition.Manual),
        new("pay", "pay", () => T("Оплатите чек", "Чекти төлөңүз", "Pay the receipt", "Fişi ödeyin", "Chekni to'lang"),
            () => T("Нажмите «Оплатить» (Enter). В окне выберите способ: наличные, перевод, долг или смешанная. Введите деньги покупателя — касса покажет сдачу. Подтвердите оплату.",
                "«Төлөө» баскычын (Enter) басыңыз. Терезеде ыкманы тандаңыз: накталай, которуу, карыз же аралаш. Сатып алуучунун акчасын киргизиңиз — касса кайтарымды көрсөтөт. Төлөмдү ырастаңыз.",
                "Press “Pay” (Enter). In the window pick a method: cash, transfer, debt or mixed. Enter the customer's money — the till shows the change. Confirm the payment.",
                "«Öde»ye (Enter) basın. Pencerede yöntemi seçin: nakit, havale, borç veya karma. Müşterinin parasını girin — kasa para üstünü gösterir. Ödemeyi onaylayın.",
                "«To'lash»ni (Enter) bosing. Oynada usulni tanlang: naqd, o'tkazma, qarz yoki aralash. Xaridorning pulini kiriting — kassa qaytimni ko'rsatadi. To'lovni tasdiqlang."),
            CoachCondition.SaleDone),
        new("shift", "shift", () => T("Смена", "Смена", "The shift", "Vardiya", "Smena"),
            () => T("Утром смену открывают, вечером закрывают: кнопка здесь или меню ☰ → «Смена». Настоящие продажи идут только в открытую смену. Нажмите «Далее».",
                "Эртең менен сменаны ачышат, кечинде жабышат: баскыч ушул жерде же ☰ меню → «Смена». Чыныгы сатуулар ачык сменада гана жүрөт. «Кийинки» басыңыз.",
                "Open the shift in the morning, close it in the evening: the button is here or menu ☰ → “Shift”. Real sales go only into an open shift. Press “Next”.",
                "Vardiya sabah açılır, akşam kapatılır: düğme burada veya ☰ menü → «Vardiya». Gerçek satışlar yalnız açık vardiyaya yazılır. «İleri»ye basın.",
                "Smena ertalab ochiladi, kechqurun yopiladi: tugma shu yerda yoki ☰ menyu → «Smena». Haqiqiy sotuvlar faqat ochiq smenaga yoziladi. «Keyingi»ni bosing."),
            CoachCondition.Manual),
        new("help", "menu", () => T("Где помощь", "Жардам кайда", "Where to get help", "Yardım nerede", "Yordam qayerda"),
            () => T("Меню ☰ → «Помощь»: база знаний, горячие клавиши, «Написать в поддержку» и это обучение — его можно пройти снова. Нажмите «Завершить».",
                "☰ меню → «Жардам»: билим базасы, ысык баскычтар, «Колдоого жазуу» жана ушул үйрөнүү — аны кайра өтсө болот. «Бүтүрүү» басыңыз.",
                "Menu ☰ → “Help”: knowledge base, hotkeys, “Write to support” and this tutorial — you can run it again. Press “Finish”.",
                "☰ menü → «Yardım»: bilgi bankası, kısayollar, «Destek'e yaz» ve bu eğitim — tekrar geçebilirsiniz. «Bitir»e basın.",
                "☰ menyu → «Yordam»: bilimlar bazasi, tezkor tugmalar, «Yordamga yozish» va shu o'rgatish — uni qayta o'tish mumkin. «Tugatish»ni bosing."),
            CoachCondition.Manual),
    ];

    // ── тексты экрана и кнопок (общие для всех шагов) ───────────────────────────────────────────────────

    public static string StepCounter(int index, int count) => T($"Шаг {index + 1} из {count}", $"{count} кадамдын {index + 1}-кадамы", $"Step {index + 1} of {count}",
        $"Adım {index + 1} / {count}", $"{count} qadamdan {index + 1}-qadam");
    public static string BackText => T("Назад", "Артка", "Back", "Geri", "Orqaga");
    public static string NextText => T("Далее", "Кийинки", "Next", "İleri", "Keyingi");
    public static string FinishText => T("Завершить", "Бүтүрүү", "Finish", "Bitir", "Tugatish");
    public static string SkipStepText => T("Пропустить шаг", "Кадамды өткөрүү", "Skip step", "Adımı atla", "Qadamni o'tkazish");
    public static string QuitText => T("Закончить позже", "Кийинчерээк бүтүрүү", "Finish later", "Sonra bitir", "Keyinroq tugatish");
    public static string DoneTitle => T("Обучение завершено!", "Үйрөнүү аяктады!", "Tutorial complete!", "Eğitim tamamlandı!", "O'rgatish tugadi!");
    public static string DoneSummary => T(
        "Теперь вы умеете: открывать меню и настройки, искать и добавлять товар, менять количество и скидку, принимать оплату и считать сдачу, работать со сменой и звать помощь.",
        "Эми сиз билесиз: меню жана жөндөөлөрдү ачууну, товар издеп кошууну, сан менен арзандатууну өзгөртүүнү, төлөм кабыл алып кайтарымды эсептөөнү, смена менен иштөөнү жана жардам чакырууну.",
        "You can now: open the menu and settings, find and add products, change quantity and discount, take payment and give change, work with the shift and get help.",
        "Artık şunları yapabilirsiniz: menü ve ayarları açmak, ürün bulup eklemek, adet ve indirimi değiştirmek, ödeme alıp para üstü vermek, vardiya ile çalışmak ve yardım almak.",
        "Endi siz bilasiz: menyu va sozlamalarni ochish, mahsulot topib qo'shish, son va chegirmani o'zgartirish, to'lov qabul qilib qaytim berish, smena bilan ishlash va yordam chaqirish.");
    public static string StartWorkText => T("Начать работу", "Иштей баштоо", "Start working", "Çalışmaya başla", "Ishni boshlash");
    public static string AgainText => T("Пройти ещё раз", "Дагы бир жолу өтүү", "Go through again", "Tekrar geç", "Yana o'tish");
    public static string OpenHelpText => T("Открыть Помощь", "Жардамды ачуу", "Open Help", "Yardımı aç", "Yordamni ochish");
}
