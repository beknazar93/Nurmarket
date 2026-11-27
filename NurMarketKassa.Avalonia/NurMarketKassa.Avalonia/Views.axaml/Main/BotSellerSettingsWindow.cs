using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1»): раздел «Бот-продавец» — тон, приветствие, подпись, язык, часы живого продавца, знания магазина, порог подтверждения
/// (telegram-bot/settings/ → seller_config, PATCH шлёт только изменённые поля). Открывается из раздела «Телеграм-бот» при признаке сервера bot_seller.
/// «Торг» (Ф2) сервер пока только хранит — здесь его нет. Собрано в коде, подписи над полями, прокрутка по высоте; помещается на 1024×600.</summary>
public sealed class BotSellerSettingsWindow : Window
{
    private readonly IBotSellerApi? _api;
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _orders = new();
    private readonly ComboBox _tone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _language = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _greeting = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 68, MaxHeight = 120 };
    private readonly TextBox _signature = new() { MaxLength = 100 };
    private readonly TextBox _forbidden = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 40, MaxHeight = 90 };
    private readonly CheckBox _handoff = new();
    private readonly CheckBox _allDay = new();
    private readonly TextBox _from = new() { Width = 78, MaxLength = 5, TextAlignment = TextAlignment.Center };
    private readonly TextBox _to = new() { Width = 78, MaxLength = 5, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _hoursRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _hoursBox = new() { Spacing = 6 };
    private readonly TextBlock _perDayNote;
    private readonly TextBox _offReply = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 56, MaxHeight = 110 };
    private readonly TextBox _returnMinutes = new() { Width = 110, MaxLength = 4 };
    private readonly TextBox _threshold = new() { Width = 160, MaxLength = 9 };
    private readonly TextBox _rules = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 2000, MinHeight = 90, MaxHeight = 200 };
    private readonly TextBox _delivery = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 56, MaxHeight = 110 };
    private readonly TextBox _payment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 56, MaxHeight = 110 };
    private readonly TextBox _workHours = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 56, MaxHeight = 110 };
    private readonly Button _save = Btn(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), primary: true);
    private readonly Button _check = Btn(T("Проверить бота", "Ботту текшерүү", "Check the bot", "Botu kontrol et", "Botni tekshirish"));
    private readonly Button _close = Btn(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _form = new() { Spacing = 12 };
    private BotSellerConfig? _loaded;
    private bool _busy;
    // 2026-11-26 (торг, Ф2): блок «Торг» — виден при признаке bot_bargain и блоке seller_config.bargain в ответе сервера.
    private Border? _bargainCard;
    private readonly CheckBox _bgEnabled = new();
    private readonly TextBox _bgMax = new() { MaxLength = 6 };
    private readonly TextBox _bgMargin = new() { MaxLength = 6 };
    private readonly ComboBox _bgSteps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _bgOnlyMarked = new();
    private readonly CheckBox _bgDecline = new();
    private readonly StackPanel _bgCategories = new() { Spacing = 2 };
    private IReadOnlyList<BotCategory> _categories = [];

    public BotSellerSettingsWindow() : this(Api())
    {
    }

    public BotSellerSettingsWindow(IBotSellerApi? api)
    {
        _api = api;
        Title = T("Бот-продавец", "Бот-сатуучу", "Seller bot", "Satıcı bot", "Bot-sotuvchi");
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");

        _enabled.Content = T("Бот-продавец включён", "Бот-сатуучу күйүк", "Seller bot is on", "Satıcı bot açık", "Bot-sotuvchi yoqilgan");
        _orders.Content = T("Бот оформляет заказы", "Бот заказдарды тариздейт", "The bot places orders", "Bot sipariş oluşturur", "Bot buyurtmalarni rasmiylashtiradi");
        _handoff.Content = T("Кнопка «👤 Продавец»: бот зовёт живого продавца (владельца в Telegram)", "«👤 Сатуучу» баскычы: бот жандуу сатуучуну (Telegram'да ээсин) чакырат",
            "The “👤 Seller” button: the bot calls a live seller (the owner in Telegram)", "«👤 Satıcı» düğmesi: bot canlı satıcıyı (Telegram'da sahibi) çağırır", "«👤 Sotuvchi» tugmasi: bot jonli sotuvchini (Telegram'da egasini) chaqiradi");
        _allDay.Content = T("Круглосуточно", "Күнү-түнү", "Around the clock", "Günün her saati", "Kecha-kunduz");
        foreach (var c in new CheckBox[] { _enabled, _orders, _handoff, _allDay })
            Avalonia.Automation.AutomationProperties.SetName(c, c.Content as string ?? "");
        foreach (var (tone, label) in new[]
                 {
                     ("official", T("Официально", "Расмий", "Formal", "Resmî", "Rasmiy")),
                     ("friendly", T("Дружелюбно", "Достук", "Friendly", "Samimi", "Do'stona")),
                     ("short", T("Коротко", "Кыска", "Short", "Kısa", "Qisqa")),
                 })
            _tone.Items.Add(new ComboBoxItem { Content = label, Tag = tone });
        foreach (var (lang, label) in new[]
                 {
                     ("auto", T("Как у покупателя", "Сатып алуучуга жараша", "Same as the customer", "Müşteriyle aynı", "Xaridorga qarab")),
                     ("ru", T("Русский", "Орусча", "Russian", "Rusça", "Ruscha")),
                     ("ky", T("Кыргызча", "Кыргызча", "Kyrgyz", "Kırgızca", "Qirg'izcha")),
                 })
            _language.Items.Add(new ComboBoxItem { Content = label, Tag = lang });

        _perDayNote = Soft(T("Расписание по дням задано на сервере — здесь оно не меняется.", "Күндөр боюнча график серверде коюлган — бул жерде өзгөрбөйт.",
            "The weekly schedule is set on the server — it cannot be changed here.", "Haftalık program sunucuda ayarlı — burada değiştirilemez.", "Kunlar bo'yicha jadval serverda belgilangan — bu yerda o'zgarmaydi."));
        _perDayNote.IsVisible = false;
        _allDay.IsCheckedChanged += (_, _) => _hoursRow.IsVisible = _allDay.IsChecked != true;
        _hoursRow.Children.Add(Soft(T("с", "баштап", "from", "başlangıç", "dan")));
        _hoursRow.Children.Add(_from);
        _hoursRow.Children.Add(Soft(T("до", "чейин", "to", "bitiş", "gacha")));
        _hoursRow.Children.Add(_to);
        foreach (var r in _hoursRow.Children.OfType<TextBlock>())
            r.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(_from, T("Часы продавца: с", "Сатуучунун сааттары: баштап", "Seller hours: from", "Satıcı saatleri: başlangıç", "Sotuvchi soatlari: dan"));
        Avalonia.Automation.AutomationProperties.SetName(_to, T("Часы продавца: до", "Сатуучунун сааттары: чейин", "Seller hours: to", "Satıcı saatleri: bitiş", "Sotuvchi soatlari: gacha"));
        _hoursBox.Children.Add(_allDay);
        _hoursBox.Children.Add(_hoursRow);
        _hoursBox.Children.Add(_perDayNote);

        BuildForm();
        Theme(_status, TextBlock.ForegroundProperty, "BrushTextSoft");

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("Как бот отвечает покупателям в Telegram и когда зовёт живого продавца. Изменения действуют после «Сохранить».",
            "Бот Telegram'да сатып алуучуларга кантип жооп берерин жана жандуу сатуучуну качан чакырарын көрсөтөт. Өзгөртүүлөр «Сактоо» дегенден кийин иштейт.",
            "How the bot answers customers in Telegram and when it calls a live seller. Changes apply after “Save”.",
            "Botun Telegram'da müşterilere nasıl yanıt verdiği ve canlı satıcıyı ne zaman çağırdığı. Değişiklikler «Kaydet»ten sonra geçerli olur.",
            "Botning Telegram'da xaridorlarga qanday javob berishi va jonli sotuvchini qachon chaqirishi. O'zgarishlar «Saqlash»dan keyin kuchga kiradi.")));
        root.Children.Add(head);

        var scroll = new ScrollViewer
        {
            Content = _form,
            Margin = new Thickness(0, 10, 0, 8),
            Padding = new Thickness(0, 0, 12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        footer.Children.Add(_status);
        _status.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_check, 1);
        Grid.SetColumn(_save, 2);
        Grid.SetColumn(_close, 3);
        _check.Margin = new Thickness(0, 0, 8, 0);
        _save.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(_check);
        footer.Children.Add(_save);
        footer.Children.Add(_close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _close.Click += (_, _) => Close();
        _check.Click += (_, _) =>
        {
            try { new BotCheckWindow(_api).Show(this); }
            catch (Exception ex) { PosLogger.Log($"«Проверить бота» не открылось: {ex.Message}", "WARNING"); }
        };
        _save.IsEnabled = false;
        Opened += (_, _) => _ = LoadAsync();
    }

    private void BuildForm()
    {
        var main = new StackPanel { Spacing = 8 };
        main.Children.Add(_enabled);
        main.Children.Add(Soft(T("Выключен — бот работает как раньше: отвечает по каталогу.", "Өчүк — бот мурдагыдай эле каталог боюнча жооп берет.",
            "Off — the bot works as before: it answers from the catalog.", "Kapalı — bot eskisi gibi katalogdan yanıt verir.", "O'chiq — bot avvalgidek katalog bo'yicha javob beradi.")));
        main.Children.Add(_orders);
        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        var toneBox = Labeled(T("Тон ответов", "Жооптордун тону", "Tone of replies", "Yanıt tonu", "Javoblar ohangi"), _tone);
        var langBox = Labeled(T("Язык ответов", "Жооптордун тили", "Reply language", "Yanıt dili", "Javoblar tili"), _language);
        Grid.SetColumn(langBox, 2);
        pair.Children.Add(toneBox);
        pair.Children.Add(langBox);
        main.Children.Add(pair);
        _form.Children.Add(Card(main));

        var texts = new StackPanel { Spacing = 10 };
        texts.Children.Add(Labeled(T("Приветствие на /start", "/start боюнча саламдашуу", "Greeting on /start", "/start karşılaması", "/start salomlashuvi"), _greeting,
            T("Пусто — стандартное приветствие. До 500 знаков.", "Бош — стандарттуу саламдашуу. 500 белгиге чейин.", "Empty — the standard greeting. Up to 500 characters.", "Boş — standart karşılama. En fazla 500 karakter.", "Bo'sh — standart salomlashuv. 500 belgigacha.")));
        texts.Children.Add(Labeled(T("Подпись в первом ответе", "Биринчи жоопто кол тамга", "Signature in the first reply", "İlk yanıtta imza", "Birinchi javobda imzo"), _signature,
            T("Например: «Магазин NBS». До 100 знаков.", "Мисалы: «NBS дүкөнү». 100 белгиге чейин.", "For example: “NBS shop”. Up to 100 characters.", "Örneğin: «NBS mağazası». En fazla 100 karakter.", "Masalan: «NBS do'koni». 100 belgigacha.")));
        texts.Children.Add(Labeled(T("Запрещённые слова", "Тыюу салынган сөздөр", "Forbidden words", "Yasaklı kelimeler", "Taqiqlangan so'zlar"), _forbidden,
            T("Через запятую, не больше 30. Бот вырезает их из ответов.", "Үтүр менен, 30дан ашык эмес. Бот аларды жоопторунан алып салат.", "Comma-separated, at most 30. The bot cuts them out of replies.",
                "Virgülle ayırın, en fazla 30. Bot bunları yanıtlardan çıkarır.", "Vergul bilan, 30 tadan ko'p emas. Bot ularni javoblardan olib tashlaydi.")));
        _form.Children.Add(Card(texts));

        var human = new StackPanel { Spacing = 10 };
        human.Children.Add(Heading(T("Живой продавец", "Жандуу сатуучу", "Live seller", "Canlı satıcı", "Jonli sotuvchi")));
        human.Children.Add(_handoff);
        human.Children.Add(Labeled(T("Часы, когда продавец отвечает", "Сатуучу жооп берген сааттар", "Hours when the seller answers", "Satıcının yanıt verdiği saatler", "Sotuvchi javob beradigan soatlar"), _hoursBox));
        human.Children.Add(Labeled(T("Ответ бота вне этих часов", "Бул сааттардан тышкары боттун жообу", "The bot's reply outside these hours", "Bu saatlerin dışında botun yanıtı", "Bu soatlardan tashqarida botning javobi"), _offReply,
            T("Пусто — стандартный текст. «{when}» заменится на «завтра с 09:00».", "Бош — стандарттуу текст. «{when}» «эртең 09:00дөн» болуп алмашат.", "Empty — the standard text. “{when}” becomes “tomorrow from 09:00”.",
                "Boş — standart metin. «{when}» «yarın 09:00'dan» olur.", "Bo'sh — standart matn. «{when}» «ertaga 09:00 dan» ga almashadi.")));
        human.Children.Add(Labeled(T("Вернуть диалог боту после тишины, минут", "Тынчтыктан кийин диалогду ботко кайтаруу, мүнөт", "Give the dialog back to the bot after silence, minutes",
            "Sessizlikten sonra diyaloğu bota geri ver, dakika", "Sukutdan keyin dialogni botga qaytarish, daqiqa"), _returnMinutes,
            T("От 10 до 1440.", "10дон 1440ка чейин.", "From 10 to 1440.", "10 ile 1440 arası.", "10 dan 1440 gacha.")));
        _form.Children.Add(Card(human));

        var big = new StackPanel { Spacing = 10 };
        big.Children.Add(Labeled(T("Крупный заказ — подтверждаете вы, от суммы (сом)", "Чоң заказ — сиз ырастайсыз, суммадан (сом)", "Large order — you confirm it, from the amount (som)",
            "Büyük sipariş — siz onaylarsınız, tutardan (som)", "Katta buyurtma — siz tasdiqlaysiz, summadan (so'm)"), _threshold,
            T("0 — не спрашивать. Сервер пришлёт карточку «Крупный заказ — подтвердите».", "0 — сурабоо. Сервер «Чоң заказ — ырастаңыз» карточкасын жөнөтөт.", "0 — do not ask. The server sends a “Large order — confirm” card.",
                "0 — sorma. Sunucu «Büyük sipariş — onaylayın» kartı gönderir.", "0 — so'ramaslik. Server «Katta buyurtma — tasdiqlang» kartasini yuboradi.")));
        _form.Children.Add(Card(big));
        _form.Children.Add(_bargainCard = Card(BuildBargain()));
        _bargainCard.IsVisible = false;

        var knowledge = new StackPanel { Spacing = 10 };
        knowledge.Children.Add(Heading(T("Знания магазина", "Дүкөндүн билимдери", "Shop knowledge", "Mağaza bilgisi", "Do'kon bilimlari")));
        knowledge.Children.Add(Soft(T("Бот отвечает покупателям по этим текстам. Чего здесь нет, он не выдумывает, а зовёт продавца.", "Бот сатып алуучуларга ушул тексттер боюнча жооп берет. Бул жерде жок нерсени ойлоп чыгарбайт, сатуучуну чакырат.",
            "The bot answers customers from these texts. What is not here it does not invent — it calls the seller.", "Bot müşterilere bu metinlerden yanıt verir. Burada olmayanı uydurmaz, satıcıyı çağırır.", "Bot xaridorlarga shu matnlar bo'yicha javob beradi. Bu yerda yo'q narsani to'qimaydi, sotuvchini chaqiradi.")));
        knowledge.Children.Add(Labeled(T("Правила магазина", "Дүкөндүн эрежелери", "Shop rules", "Mağaza kuralları", "Do'kon qoidalari"), _rules,
            T("Возврат, гарантия, самовывоз и т. п. До 2000 знаков.", "Кайтаруу, кепилдик, өзү алып кетүү ж. б. 2000 белгиге чейин.", "Returns, warranty, pickup, etc. Up to 2000 characters.", "İade, garanti, mağazadan teslim vb. En fazla 2000 karakter.", "Qaytarish, kafolat, o'zi olib ketish va h. k. 2000 belgigacha.")));
        knowledge.Children.Add(Labeled(T("Доставка", "Жеткирүү", "Delivery", "Teslimat", "Yetkazib berish"), _delivery));
        knowledge.Children.Add(Labeled(T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), _payment));
        knowledge.Children.Add(Labeled(T("Часы работы магазина", "Дүкөндүн иштөө сааттары", "Shop opening hours", "Mağaza çalışma saatleri", "Do'kon ish soatlari"), _workHours,
            T("До 500 знаков в каждом поле.", "Ар бир талаада 500 белгиге чейин.", "Up to 500 characters in each field.", "Her alanda en fazla 500 karakter.", "Har bir maydonda 500 belgigacha.")));
        _form.Children.Add(Card(knowledge));
    }

    private StackPanel BuildBargain()
    {
        _bgEnabled.Content = T("Торг включён", "Соодалашуу күйүк", "Bargaining is on", "Pazarlık açık", "Savdolashish yoqilgan");
        _bgOnlyMarked.Content = T("Торг только по отмеченным товарам и категориям", "Соодалашуу белгиленген товарлар жана категориялар боюнча гана", "Bargaining only for marked products and categories",
            "Pazarlık yalnızca işaretli ürün ve kategorilerde", "Savdolashish faqat belgilangan mahsulot va kategoriyalar bo'yicha");
        _bgDecline.Content = T("При «лучшей цене» звать продавца", "«Эң мыкты баа» болгондо сатуучуну чакыруу", "Call the seller on “best price”", "«En iyi fiyat»ta satıcıyı çağır", "«Eng yaxshi narx»da sotuvchini chaqirish");
        foreach (var cb in new CheckBox[] { _bgEnabled, _bgOnlyMarked, _bgDecline })
            Avalonia.Automation.AutomationProperties.SetName(cb, cb.Content as string ?? "");
        for (var i = BotBargainConfig.MinSteps; i <= BotBargainConfig.MaxSteps; i++)
            _bgSteps.Items.Add(new ComboBoxItem { Content = i.ToString(CultureInfo.InvariantCulture), Tag = i });

        var box = new StackPanel { Spacing = 10 };
        box.Children.Add(Heading(T("Торг", "Соодалашуу", "Bargaining", "Pazarlık", "Savdolashish")));
        box.Children.Add(_bgEnabled);
        box.Children.Add(Soft(T("Торг работает, только когда включён «Бот-продавец» (самый верх окна). Цену решает сервер, ИИ её не называет; ниже «пола» цена не опускается.",
            "Соодалашуу «Бот-сатуучу» (терезенин эң жогору жагы) күйүк болгондо гана иштейт. Баанын чечимин сервер кабыл алат, ЖИ аны атабайт; «полдон» төмөн түшпөйт.",
            "Bargaining works only when the “Seller bot” (the very top of this window) is on. The server decides the price, the AI does not name it; it never goes below the “floor”.",
            "Pazarlık yalnızca «Satıcı bot» (pencerenin en üstü) açıkken çalışır. Fiyata sunucu karar verir, yapay zekâ söylemez; «taban»ın altına inmez.",
            "Savdolashish faqat «Bot-sotuvchi» (oynaning eng yuqorisi) yoqilganda ishlaydi. Narxni server hal qiladi, SI uni aytmaydi; «pol»dan pastga tushmaydi.")));
        var trio = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        var a = Labeled(T("Лимит скидки, % (0–50)", "Арзандатуу лимити, % (0–50)", "Discount limit, % (0–50)", "İndirim sınırı, % (0–50)", "Chegirma limiti, % (0–50)"), _bgMax);
        var b = Labeled(T("Не ниже закупки + N %, (0–500)", "Сатып алуудан төмөн эмес + N %, (0–500)", "Not below purchase + N %, (0–500)", "Alıştan aşağı değil + N %, (0–500)", "Xariddan past emas + N %, (0–500)"), _bgMargin);
        var c = Labeled(T("Ступеней уступки (1–5)", "Көндүрүү баскычтары (1–5)", "Concession steps (1–5)", "Taviz adımları (1–5)", "Yon berish bosqichlari (1–5)"), _bgSteps);
        Grid.SetColumn(b, 2);
        Grid.SetColumn(c, 4);
        trio.Children.Add(a);
        trio.Children.Add(b);
        trio.Children.Add(c);
        box.Children.Add(trio);
        box.Children.Add(_bgOnlyMarked);
        box.Children.Add(_bgDecline);
        box.Children.Add(Labeled(T("Категории, где торг разрешён", "Соодалашууга уруксат берилген категориялар", "Categories where bargaining is allowed", "Pazarlığa izin verilen kategoriler", "Savdolashishga ruxsat berilgan kategoriyalar"),
            new ScrollViewer { Content = _bgCategories, MaxHeight = 150, Padding = new Thickness(0, 0, 10, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            T("Отдельные товары отмечаются в «Правилах торга по товарам».", "Айрым товарлар «Товарлар боюнча соодалашуу эрежелеринде» белгиленет.", "Individual products are marked in “Bargaining rules by product”.",
                "Tek tek ürünler «Ürün bazında pazarlık kuralları»nda işaretlenir.", "Alohida mahsulotlar «Mahsulotlar bo'yicha savdolashish qoidalari»da belgilanadi.")));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var rules = Btn(T("Правила торга по товарам…", "Товарлар боюнча соодалашуу эрежелери…", "Bargaining rules by product…", "Ürün bazında pazarlık kuralları…", "Mahsulotlar bo'yicha savdolashish qoidalari…"));
        rules.Click += (_, _) =>
        {
            try { new BotBargainProductsWindow(_api as IBotBargainApi).Show(this); }
            catch (Exception ex) { PosLogger.Log($"«Правила торга» не открылись: {ex.Message}", "WARNING"); }
        };
        var journal = Btn(T("Журнал оферт…", "Оферталар журналы…", "Offer log…", "Teklif günlüğü…", "Takliflar jurnali…"));
        journal.Click += (_, _) =>
        {
            try { new BotBargainOffersWindow(_api as IBotBargainApi).Show(this); }
            catch (Exception ex) { PosLogger.Log($"«Журнал оферт» не открылся: {ex.Message}", "WARNING"); }
        };
        foreach (var btn in new[] { rules, journal })
        {
            btn.Margin = new Thickness(0, 0, 8, 4);
            buttons.Children.Add(btn);
        }
        box.Children.Add(buttons);
        return box;
    }

    private void FillBargain(BotBargainConfig? bg)
    {
        _bargainCard!.IsVisible = bg is not null && ServerCapabilities.Has(ServerCapabilities.BotBargain);
        if (bg is null)
            return;
        _bgEnabled.IsChecked = bg.Enabled;
        _bgMax.Text = bg.MaxDiscountPercent.ToString("0.##", CultureInfo.InvariantCulture);
        _bgMargin.Text = bg.MinMarginPercent.ToString("0.##", CultureInfo.InvariantCulture);
        _bgSteps.SelectedItem = _bgSteps.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == bg.Steps) ?? _bgSteps.Items[2];
        _bgOnlyMarked.IsChecked = bg.OnlyMarked;
        _bgDecline.IsChecked = bg.HandoffOnDecline;
        RebuildCategories(bg.AllowedCategories);
    }

    private void RebuildCategories(IReadOnlyCollection<string> selected)
    {
        _bgCategories.Children.Clear();
        foreach (var c in _categories)
            _bgCategories.Children.Add(new CheckBox { Content = c.Name, Tag = c.Id, IsChecked = selected.Contains(c.Id) });
        if (_categories.Count == 0)
            _bgCategories.Children.Add(Soft(T("Категории не загружены.", "Категориялар жүктөлгөн жок.", "Categories are not loaded.", "Kategoriler yüklenmedi.", "Kategoriyalar yuklanmadi.")));
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void Select(ComboBox box, string value, string fallback)
    {
        var items = box.Items.OfType<ComboBoxItem>().ToList();
        box.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == value) ?? items.First(i => (string?)i.Tag == fallback);
    }

    private void Fill(BotSellerConfig c)
    {
        _loaded = c;
        _enabled.IsChecked = c.SellerEnabled;
        _orders.IsChecked = c.OrdersEnabled;
        Select(_tone, c.Tone, "friendly");
        Select(_language, c.Language, "auto");
        _greeting.Text = c.Greeting;
        _signature.Text = c.Signature;
        _forbidden.Text = string.Join(", ", c.ForbiddenWords);
        _handoff.IsChecked = c.HandoffEnabled;
        _allDay.IsChecked = c.Hours.AllDay;
        _from.Text = c.Hours.PerDay ? "" : c.Hours.From;
        _to.Text = c.Hours.PerDay ? "" : c.Hours.To;
        _hoursRow.IsVisible = !c.Hours.AllDay && !c.Hours.PerDay;
        _allDay.IsVisible = !c.Hours.PerDay;
        _perDayNote.IsVisible = c.Hours.PerDay;
        _offReply.Text = c.OffHoursReply;
        _returnMinutes.Text = c.HandoffReturnMinutes.ToString(CultureInfo.InvariantCulture);
        _threshold.Text = c.ConfirmThreshold.ToString(CultureInfo.InvariantCulture);
        _rules.Text = c.Rules;
        _delivery.Text = c.Delivery;
        _payment.Text = c.Payment;
        _workHours.Text = c.WorkHours;
        FillBargain(c.Bargain);
    }

    private async Task LoadAsync()
    {
        if (_api == null || _busy)
            return;
        _busy = true;
        _status.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var c = await _api.GetSellerConfigAsync(cts.Token).ConfigureAwait(true);
            if (c is null)
            {
                _status.Text = T("Сервер пока не отдаёт настройки бота-продавца.", "Сервер бот-сатуучунун жөндөөлөрүн азырынча бербейт.", "The server does not provide the seller bot settings yet.",
                    "Sunucu henüz satıcı bot ayarlarını vermiyor.", "Server hozircha bot-sotuvchi sozlamalarini bermaydi.");
                return;
            }
            Fill(c);
            _save.IsEnabled = true;
            _status.Text = "";
            if (c.Bargain is not null && _api is IBotBargainApi bargainApi && ServerCapabilities.Has(ServerCapabilities.BotBargain))
            {
                try
                {
                    _categories = await bargainApi.GetCategoriesAsync(cts.Token).ConfigureAwait(true);
                    RebuildCategories(_loaded?.Bargain?.AllowedCategories ?? c.Bargain.AllowedCategories);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Торг: категории не получены ({ex.GetType().Name}).", "WARNING"); // отметки категорий остаются как на сервере
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бот-продавец: настройки не получены ({ex.GetType().Name}).", "WARNING");
            _status.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + Failure(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Настройки из полей окна; null и сообщение в строке состояния — если часы, минуты или порог введены неверно.</summary>
    private BotSellerConfig? Collect()
    {
        var before = _loaded!;
        var hours = before.Hours;
        if (!before.Hours.PerDay)
        {
            if (_allDay.IsChecked == true)
                hours = new BotSellerHours(true, false, before.Hours.From, before.Hours.To);
            else
            {
                var from = QuietHours.NormalizeTime(_from.Text);
                var to = QuietHours.NormalizeTime(_to.Text);
                if (from is null || to is null)
                {
                    _status.Text = T("Часы вводятся как ЧЧ:ММ, например 09:00.", "Сааттар СС:ММ түрүндө жазылат, мисалы 09:00.", "Enter the hours as HH:MM, e.g. 09:00.", "Saatleri SS:DD biçiminde girin, örneğin 09:00.", "Soatlarni SS:DD ko'rinishida kiriting, masalan 09:00.");
                    return null;
                }
                hours = new BotSellerHours(false, false, from, to);
            }
        }
        if (!int.TryParse((_returnMinutes.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 10 or > 1440)
        {
            _status.Text = T("Минуты тишины — число от 10 до 1440.", "Тынчтык мүнөттөрү — 10дон 1440ка чейинки сан.", "Silence minutes — a number from 10 to 1440.", "Sessizlik dakikası — 10 ile 1440 arası bir sayı.", "Sukut daqiqalari — 10 dan 1440 gacha son.");
            return null;
        }
        if (!long.TryParse((_threshold.Text ?? "").Replace(" ", "").Replace(" ", "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var threshold))
        {
            _status.Text = T("Порог подтверждения — целое число сом (0 — не спрашивать).", "Ырастоо чеги — бүтүн сан сом (0 — сурабоо).", "Confirmation threshold — a whole number of som (0 — do not ask).", "Onay eşiği — tam som sayısı (0 — sorma).", "Tasdiqlash chegarasi — butun so'm soni (0 — so'ramaslik).");
            return null;
        }
        var words = BotSellerConfig.SplitWords(_forbidden.Text);
        if (words.Count > BotSellerConfig.MaxForbiddenWords)
        {
            _status.Text = T($"Запрещённых слов не больше {BotSellerConfig.MaxForbiddenWords}.", $"Тыюу салынган сөздөр {BotSellerConfig.MaxForbiddenWords}дан ашпасын.", $"At most {BotSellerConfig.MaxForbiddenWords} forbidden words.",
                $"En fazla {BotSellerConfig.MaxForbiddenWords} yasaklı kelime.", $"Taqiqlangan so'zlar {BotSellerConfig.MaxForbiddenWords} tadan oshmasin.");
            return null;
        }
        var bargain = before.Bargain;
        if (before.Bargain is { } oldBargain && _bargainCard!.IsVisible)
        {
            var max = ParseAmount(_bgMax.Text);
            var margin = ParseAmount(_bgMargin.Text);
            if (max is null or < 0 or > BotBargainConfig.MaxDiscountLimit)
            {
                _status.Text = T("Лимит скидки — число от 0 до 50.", "Арзандатуу лимити — 0дон 50гө чейинки сан.", "The discount limit is a number from 0 to 50.", "İndirim sınırı 0 ile 50 arası bir sayıdır.", "Chegirma limiti — 0 dan 50 gacha son.");
                return null;
            }
            if (margin is null or < 0 or > BotBargainConfig.MaxMarginLimit)
            {
                _status.Text = T("Маржа — число от 0 до 500.", "Маржа — 0дон 500гө чейинки сан.", "The margin is a number from 0 to 500.", "Marj 0 ile 500 arası bir sayıdır.", "Marja — 0 dan 500 gacha son.");
                return null;
            }
            // Отметки категорий + те, которых нет в загруженном списке (список не загрузился или категорию удалили): они остаются как на сервере.
            var known = _categories.Select(c => c.Id).ToHashSet();
            var cats = _bgCategories.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).Concat(oldBargain.AllowedCategories.Where(id => !known.Contains(id))).Distinct().ToList();
            bargain = new BotBargainConfig(_bgEnabled.IsChecked == true, max.Value, margin.Value, _bgOnlyMarked.IsChecked == true,
                (_bgSteps.SelectedItem as ComboBoxItem)?.Tag is int steps ? steps : oldBargain.Steps, cats, _bgDecline.IsChecked == true);
        }
        return new BotSellerConfig(_enabled.IsChecked == true, _orders.IsChecked == true, TagOf(_tone, before.Tone), (_greeting.Text ?? "").Trim(), (_signature.Text ?? "").Trim(), words,
            TagOf(_language, before.Language), _handoff.IsChecked == true, hours, (_offReply.Text ?? "").Trim(), minutes, threshold,
            (_rules.Text ?? "").Trim(), (_delivery.Text ?? "").Trim(), (_payment.Text ?? "").Trim(), (_workHours.Text ?? "").Trim(), bargain);
    }

    private async Task SaveAsync()
    {
        if (_api == null || _busy || _loaded is null || Collect() is not { } edited)
            return;
        var body = edited.DiffBody(_loaded);
        if (body.Count == 0)
        {
            _status.Text = T("Изменений нет.", "Өзгөртүү жок.", "No changes.", "Değişiklik yok.", "O'zgarish yo'q.");
            return;
        }
        _busy = true;
        _save.IsEnabled = false;
        _status.Text = T("Сохраняем…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var saved = await _api.PatchSellerConfigAsync(body, cts.Token).ConfigureAwait(true);
            if (saved is not null)
                Fill(saved);
            else
                _loaded = edited; // 2026-11-26 (стресс-тест): сервер принял, но не вернул seller_config — следующее «Сохранить» не шлёт те же поля повторно
            _status.Text = T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi.");
        }
        catch (Exception ex)
        {
            _status.Text = T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + Failure(ex);
        }
        finally
        {
            _busy = false;
            _save.IsEnabled = true;
        }
    }
}
