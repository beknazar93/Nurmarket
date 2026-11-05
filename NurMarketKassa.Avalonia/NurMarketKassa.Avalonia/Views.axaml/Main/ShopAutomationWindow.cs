using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05 (волна 2-А, функции 1, 3, 5, 7): окно «Автоматизация магазина» — настройки новых умных функций в одном месте:
/// просрочка при продаже, автозаказ v2, подсказки цены, оповещения «Сообщать в Telegram, если…», запасные поставщики.
/// Данные — ShopAutomationSettings (shop-automation.json). Всё, кроме предупреждения о просрочке, по умолчанию выключено.</summary>
public sealed class ShopAutomationWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ComboBox _expiry = new() { MinHeight = 40, MinWidth = 320 };
    private readonly CheckBox _lost = new(), _pack = new(), _weekday = new(), _tsb = new(), _lead = new(), _xyz = new(), _morning = new();
    private readonly ComboBox _niceStep = new() { MinHeight = 40, MinWidth = 200 };
    private readonly TextBox _categoryMarkups = new() { AcceptsReturn = true, MinHeight = 64, MaxHeight = 120, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _reserve = new() { AcceptsReturn = true, MinHeight = 64, MaxHeight = 120, TextWrapping = TextWrapping.NoWrap };
    private readonly CheckBox _alerts = new(), _shortage = new(), _return = new(), _behind = new(), _classA = new(), _debt = new(), _expired = new(), _evening = new(), _weekly = new();
    private readonly TextBox _shortageSum, _returnSum, _behindPercent, _behindHour, _behindShare, _debtSum, _eveningHour, _quietFrom, _quietTo;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>Открыть окно над окном, где лежит anchor; без окна — обычным окном.</summary>
    public static async Task ShowAsync(Control? anchor)
    {
        if (!NurMarketKassa.AvaloniaHost.Services.ActionGuard.EnsureCanChangeProtection()) // 2026-11-05 (ревью волн 0–3, В3): кассир защиту не меняет
            return;
        try
        {
            var w = new ShopAutomationWindow();
            if (anchor is not null && TopLevel.GetTopLevel(anchor) is Window owner)
                await w.ShowDialog(owner).ConfigureAwait(true);
            else
                w.Show();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Автоматизация магазина: окно настроек не открыто ({ex.GetType().Name}: {ex.Message}).", "WARNING");
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

    private Control Section(string title, params Control[] rows)
    {
        var head = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(head);
        foreach (var r in rows)
            panel.Children.Add(r);
        return panel;
    }

    private Control Hint(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
        return tb;
    }

    private Control Line(CheckBox? check, string? before = null, TextBox? box = null, string? after = null)
    {
        // 2026-11-05 (ревью волн 0–3, Н11): перенос строки вместо обрезки на ky/uz и масштабе 125 %.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (check is not null)
            row.Children.Add(check);
        if (box is not null)
        {
            if (before is not null)
                row.Children.Add(new TextBlock { Text = before, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            row.Children.Add(box);
        }
        if (after is not null)
            row.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        foreach (var child in row.Children)
            child.Margin = new Thickness(0, 0, 8, 4); // в WrapPanel нет Spacing (Avalonia 11.2) — отступы у самих элементов
        return row;
    }

    private Control Labeled(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        row.Children.Add(control);
        return row;
    }

    public ShopAutomationWindow()
    {
        Title = T("Автоматизация магазина", "Дүкөндү автоматташтыруу", "Shop automation", "Mağaza otomasyonu", "Do'kon avtomatlashtirish");
        SectionCards.AsModal(this, 720);
        var d = ShopAutomationSettings.Current;
        var a = d.Alerts;

        // 1. Просрочка
        _expiry.ItemsSource = new[]
        {
            T("Не проверять", "Текшербөө", "Don't check", "Kontrol etme", "Tekshirmaslik"),
            T("Предупреждать: «Продать всё равно?»", "Эскертүү: «Баары бир сатасызбы?»", "Warn: “Sell anyway?”", "Uyar: «Yine de satılsın mı?»", "Ogohlantirish: «Baribir sotilsinmi?»"),
            T("Запретить без PIN владельца", "Ээсинин PIN кодусуз тыюу", "Forbid without the owner PIN", "Sahip PIN'i olmadan yasakla", "Egasining PIN kodisiz taqiqlash"),
        };
        _expiry.SelectedIndex = d.ExpiryMode switch { ExpiryModes.Off => 0, ExpiryModes.Block => 2, _ => 1 };

        // 4. Подсказки цены
        _niceStep.ItemsSource = new[]
        {
            T("Не округлять", "Тегеректебөө", "Don't round", "Yuvarlama", "Yaxlitlamaslik"),
            T("До целого сома", "Бүтүн сомго чейин", "To a whole som", "Tam soma", "Butun so'mgacha"),
            T("До 5 сом (…0 / …5)", "5 сомго чейин (…0 / …5)", "To 5 som (…0 / …5)", "5 soma (…0 / …5)", "5 so'mgacha (…0 / …5)"),
            T("До 10 сом (…0)", "10 сомго чейин (…0)", "To 10 som (…0)", "10 soma (…0)", "10 so'mgacha (…0)"),
        };
        _niceStep.SelectedIndex = d.PriceNiceStep switch { 0 => 0, 1 => 1, 10 => 3, _ => 2 };
        _categoryMarkups.Text = ShopAutomationText.FormatMarkups(d.CategoryTargetMarkup);
        _reserve.Text = ShopAutomationText.FormatReserve(d.ReserveSuppliers);

        // 5. Оповещения
        _shortageSum = Number(a.ShiftShortageAmount);
        _returnSum = Number(a.BigReturnAmount);
        _behindPercent = Number(a.RevenueBehindPercent, 72);
        _behindHour = Number(a.RevenueCheckHour, 72);
        _behindShare = Number(a.RevenueShareByCheck, 72);
        _debtSum = Number(a.NewDebtAmount);
        _eveningHour = Number(a.EveningHour, 72);
        _quietFrom = Number(a.QuietFromHour, 72);
        _quietTo = Number(a.QuietToHour, 72);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Section(T("Просрочка при продаже", "Сатууда мөөнөтү өткөндү эскертүү", "Expired products at sale", "Satışta süresi geçmiş ürün", "Sotuvda muddati o'tgan mahsulot"),
            _expiry,
            Hint(T("Сроки берутся с сервера NurCRM. Кнопка «Списать просроченное» — на плитке «Срок годности» в Складе.",
                "Мөөнөттөр NurCRM серверинен алынат. «Мөөнөтү өткөндү эсептен чыгаруу» баскычы — Кампадагы «Жарактуулук мөөнөтү» плиткасында.",
                "Dates come from the NurCRM server. The “Write off expired” button is on the “Expiry” tile in Warehouse.",
                "Tarihler NurCRM sunucusundan gelir. «Süresi geçmişi düş» düğmesi Depo'daki «Son kullanma» kutusunda.",
                "Muddatlar NurCRM serveridan olinadi. «Muddati o'tganni hisobdan chiqarish» tugmasi Ombordagi «Yaroqlilik muddati» plitkasida."))));

        panel.Children.Add(Section(T("Автозаказ v2 («Закупки → Рекомендуемый заказ»)", "Автобуйрутма v2 («Сатып алуулар → Сунушталган буйрутма»)", "Auto-order v2 (Purchases → Recommended order)", "Otomatik sipariş v2 (Satın almalar → Önerilen sipariş)", "Avtobuyurtma v2 (Xaridlar → Tavsiya etilgan buyurtma)"),
            Check(_lost, T("Не считать дни, когда товара не было на полке", "Товар текчеде жок болгон күндөрдү эсептебөө", "Ignore days when the product was out of stock", "Ürünün rafta olmadığı günleri sayma", "Mahsulot javonda bo'lmagan kunlarni hisoblamaslik"), d.PlanLostDemand),
            Check(_pack, T("Кратность упаковки и минимальная партия", "Таңгак еселиги жана минималдуу партия", "Pack multiple and minimum batch", "Paket katı ve asgari parti", "Qadoq karraligi va minimal partiya"), d.PlanPackRules),
            Check(_lead, T("Срок поставки поставщика (дней)", "Жеткирүүчүнүн жеткирүү мөөнөтү (күн)", "Supplier lead time (days)", "Tedarikçi teslim süresi (gün)", "Yetkazib beruvchi muddati (kun)"), d.PlanLeadTime),
            Check(_tsb, T("Редкие товары — прогноз TSB", "Сейрек товарлар — TSB божомолу", "Rare products — TSB forecast", "Nadir ürünler — TSB tahmini", "Kam sotiladigan mahsulotlar — TSB prognozi"), d.PlanTsb),
            Check(_weekday, T("Учитывать день недели", "Жума күнүн эске алуу", "Take the weekday into account", "Haftanın gününü hesaba kat", "Hafta kunini hisobga olish"), d.PlanWeekday),
            Check(_xyz, T("Страховой запас по классу ABC×XYZ (AX — больше, CZ — без запаса)", "ABC×XYZ классы боюнча запас (AX — көбүрөөк, CZ — запассыз)", "Safety stock by ABC×XYZ class (AX higher, CZ none)", "ABC×XYZ sınıfına göre güvenlik stoğu (AX fazla, CZ yok)", "ABC×XYZ sinfi bo'yicha zaxira (AX ko'proq, CZ yo'q)"), d.PlanXyzSafety),
            Check(_morning, T("Утром проверять «пора заказать у N поставщиков» (отчёт и Telegram)", "Эртең менен «N жеткирүүчүдөн буйрутма берүү убактысы» деп текшерүү (отчёт жана Telegram)", "Check every morning “time to order from N suppliers” (report and Telegram)", "Her sabah «N tedarikçiden sipariş zamanı» kontrolü (rapor ve Telegram)", "Har ertalab «N ta yetkazib beruvchidan buyurtma vaqti» tekshiruvi (hisobot va Telegram)"), d.MorningOrderCheck),
            Hint(T("«Дни без товара», TSB, день недели и страховой запас ABC×XYZ считает касса по продажам этого компьютера: тогда вместо рекомендации сервера используется расчёт кассы (с пометкой над таблицей). Упаковка, минимальная партия и срок поставки работают и с сервером.",
                "«Товарсыз күндөр», TSB, жума күнү жана ABC×XYZ боюнча камдык запасты касса ушул компьютердин сатуулары боюнча эсептейт: ошондо сервердин сунушунун ордуна кассанын эсеби колдонулат (таблицанын үстүндө белгиси менен). Таңгак, минималдуу партия жана жеткирүү мөөнөтү сервер менен да иштейт.",
                "“Days without stock”, TSB, weekday and ABC×XYZ safety stock are calculated by the till from this computer's sales: the till's calculation is then used instead of the server's recommendation (marked above the table). Pack multiple, minimum batch and lead time also work with the server.",
                "«Stoksuz günler», TSB, haftanın günü ve ABC×XYZ emniyet stoku kasa tarafından bu bilgisayarın satışlarından hesaplanır: bu durumda sunucu önerisi yerine kasa hesabı kullanılır (tablonun üstünde not var). Paket katı, asgari parti ve teslim süresi sunucuyla da çalışır.",
                "«Tovarsiz kunlar», TSB, hafta kuni va ABC×XYZ zaxirasini kassa shu kompyuter sotuvlari bo'yicha hisoblaydi: shunda server tavsiyasi o'rniga kassa hisobi ishlatiladi (jadval ustida belgi bor). Qadoq karraligi, minimal partiya va yetkazish muddati server bilan ham ishlaydi.")),
            Hint(T("Срок поставки и минимальная сумма заказа — по кнопке «Автозаказ v2…» в рекомендуемом заказе. Пока всё выключено, расчёт как раньше.",
                "Жеткирүү мөөнөтү жана буйрутманын минималдуу суммасы — сунушталган буйрутмадагы «Автобуйрутма v2…» баскычы аркылуу. Баары өчүк болсо, эсептөө мурункудай.",
                "Lead time and minimum order sum are set with the “Auto-order v2…” button in the recommended order. While everything is off, the calculation is as before.",
                "Teslim süresi ve asgari sipariş tutarı önerilen siparişteki «Otomatik sipariş v2…» düğmesiyle ayarlanır. Hepsi kapalıyken hesap eskisi gibidir.",
                "Yetkazish muddati va minimal buyurtma summasi tavsiya etilgan buyurtmadagi «Avtobuyurtma v2…» tugmasi orqali. Hammasi o'chiq bo'lsa, hisob avvalgidek."))));

        panel.Children.Add(Section(T("Подсказки цены после прихода", "Кирүүдөн кийин баа кеңештери", "Price hints after a delivery", "Mal kabulünden sonra fiyat ipuçları", "Kirimdan keyin narx maslahatlari"),
            Labeled(T("«Красивая» цена:", "«Кооз» баа:", "“Nice” price:", "«Güzel» fiyat:", "«Chiroyli» narx:"), _niceStep),
            Hint(T("Целевая наценка по категориям (по строке: Категория = %):", "Категория боюнча максаттуу үстөк (ар бир сап: Категория = %):", "Target markup by category (one per line: Category = %):", "Kategoriye göre hedef kâr (satır başına: Kategori = %):", "Kategoriya bo'yicha maqsadli ustama (har qatorda: Kategoriya = %):")),
            _categoryMarkups));

        panel.Children.Add(Section(T("Запасные поставщики («Спросить цены у других»)", "Запастагы жеткирүүчүлөр («Башкалардан баа сурау»)", "Reserve suppliers (“Ask other suppliers for prices”)", "Yedek tedarikçiler («Diğerlerinden fiyat sor»)", "Zaxira yetkazib beruvchilar («Boshqalardan narx so'rash»)"),
            Hint(T("По строке: Название; телефон", "Ар бир сапка: Аты; телефон", "One per line: Name; phone", "Satır başına: Ad; telefon", "Har qatorda: Nomi; telefon")),
            _reserve));

        panel.Children.Add(Section(T("Сообщать в Telegram, если…", "Telegram'га билдирүү, эгер…", "Tell me in Telegram if…", "Şu durumda Telegram'dan bildir…", "Telegram'ga xabar berish, agar…"),
            Check(_alerts, T("Оповещения включены", "Эскертүүлөр күйгүзүлгөн", "Alerts are on", "Uyarılar açık", "Ogohlantirishlar yoqilgan"), a.Enabled),
            Line(Check(_shortage, T("недостача при закрытии смены больше", "смена жабылганда жетишпестик ашса", "shortage at shift close over", "vardiya kapanışında eksik şundan fazla", "smena yopilganda kamomad shundan ortiq"), a.ShiftShortage), null, _shortageSum, T("сом", "сом", "som", "som", "so'm")),
            Line(Check(_return, T("возврат или отмена чека больше", "чекти кайтаруу же жокко чыгаруу ашса", "return or cancellation over", "iade veya iptal şundan fazla", "qaytarish yoki bekor qilish shundan ortiq"), a.BigReturn), null, _returnSum, T("сом", "сом", "som", "som", "so'm")),
            Line(Check(_debt, T("новая продажа в долг больше", "карызга жаңы сатуу ашса", "new sale on credit over", "yeni veresiye satış şundan fazla", "yangi qarzga sotuv shundan ortiq"), a.NewDebt), null, _debtSum, T("сом", "сом", "som", "som", "so'm")),
            Line(Check(_behind, T("выручка ниже обычного дня недели на", "түшүм адаттагы жума күнүнөн төмөн", "revenue below the usual weekday by", "ciro olağan hafta gününden şu kadar düşük", "tushum odatdagi hafta kunidan shuncha past"), a.RevenueBehindPlan), null, _behindPercent, "%"),
            Line(null, T("проверять в", "саат", "check at", "saat", "soat"), _behindHour, T(":00, к этому часу обычно набирается", ":00, ушул саатка чейин адатта", ":00, by this hour usually", ":00, bu saate kadar genelde", ":00, shu soatgacha odatda")),
            Line(null, null, _behindShare, T("% дневной выручки", "% күндүк түшүмдүн", "% of the day's revenue", "% günlük ciro", "% kunlik tushum")),
            Check(_classA, T("закончился товар A-группы (главные по выручке)", "A тобундагы товар түгөндү (түшүм боюнча негизгилер)", "an A-group product ran out (top by revenue)", "A grubu ürün tükendi (cirodaki ana ürünler)", "A guruhidagi mahsulot tugadi (tushum bo'yicha asosiylar)"), a.ClassAOut),
            Check(_expired, T("есть просроченные товары (раз в день)", "мөөнөтү өткөн товарлар бар (күнүнө бир жолу)", "there are expired products (once a day)", "süresi geçmiş ürünler var (günde bir)", "muddati o'tgan mahsulotlar bor (kuniga bir marta)"), a.Expired),
            Line(Check(_evening, T("вечерний итог дня в", "күндүн кечки жыйынтыгы саат", "evening summary of the day at", "günün akşam özeti saat", "kunning kechki yakuni soat"), a.EveningSummary), null, _eveningHour, ":00"),
            Check(_weekly, T("недельный отчёт по понедельникам в 9:00", "дүйшөмбүлөрдө 9:00 да жумалык отчёт", "weekly report on Mondays at 9:00", "pazartesi 9:00'da haftalık rapor", "dushanba kunlari 9:00 da haftalik hisobot"), a.WeeklyReport),
            Line(null, T("Тихие часы: с", "Тынч саат: баштап", "Quiet hours: from", "Sessiz saatler: şundan", "Tinch soatlar: dan"), _quietFrom, T("до", "чейин", "to", "şuna", "gacha")),
            Line(null, null, _quietTo, T("ч. (в это время сообщения копятся до утра)", "с. (бул убакта билдирүүлөр эртеңге чейин топтолот)", "h (messages wait until morning)", "sa. (mesajlar sabaha kadar birikir)", "s. (xabarlar ertalabgacha to'planadi)")),
            Hint(BotText())));

        // 2026-11-05 (волна 2-Б, функции 2, 12, 8): антифрод кассира, PIN старшего и чеки в Telegram — в своём окне.
        var fraudButton = UiKit.Ghost(this, T("Подозрительные операции, PIN старшего, чеки в Telegram…", "Шектүү операциялар, улуунун PIN коду, Telegram'да чектер…", "Suspicious operations, senior's PIN, receipts in Telegram…",
            "Şüpheli işlemler, yetkili PIN'i, Telegram'da fişler…", "Shubhali amallar, katta PIN i, Telegram'da cheklar…"));
        fraudButton.HorizontalAlignment = HorizontalAlignment.Left;
        fraudButton.Click += async (_, _) => await FraudWindow.ShowAsync(this).ConfigureAwait(true);
        panel.Children.Add(Section(T("Контроль кассиров", "Кассирлерди көзөмөлдөө", "Cashier control", "Kasiyer kontrolü", "Kassirlar nazorati"), fraudButton));

        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) =>
        {
            if (Save())
                Close();
        };
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 460, Padding = new Thickness(0, 0, 14, 0), /* 2026-11-05 (Н-14): текст не уходит под полосу прокрутки */ HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = SectionCards.Shell(Title,
            T("Умные функции магазина: проверка сроков при продаже, автозаказ, подсказки цены, оповещения в Telegram. Что выключено — работает как раньше.",
                "Дүкөндүн акылдуу функциялары: сатууда мөөнөттү текшерүү, автобуйрутма, баа кеңештери, Telegram эскертүүлөрү. Өчүрүлгөн нерсе мурункудай иштейт.",
                "Smart shop features: expiry check at sale, auto-order, price hints, Telegram alerts. What is off works as before.",
                "Akıllı mağaza özellikleri: satışta tarih kontrolü, otomatik sipariş, fiyat ipuçları, Telegram uyarıları. Kapalı olanlar eskisi gibi çalışır.",
                "Do'konning aqlli funksiyalari: sotuvda muddat tekshiruvi, avtobuyurtma, narx maslahatlari, Telegram ogohlantirishlari. O'chiq narsa avvalgidek ishlaydi."),
            scroll, SectionCards.Buttons(close, save));
    }

    private static string BotText() => TelegramBotService.IsConfigured
        ? T("Telegram-бот подключён — сообщения придут владельцу.", "Telegram-бот туташкан — билдирүүлөр ээсине келет.", "The Telegram bot is connected — the owner will get the messages.", "Telegram botu bağlı — mesajlar sahibine gelir.", "Telegram-bot ulangan — xabarlar egasiga keladi.")
        : T("Telegram-бот не подключён (Настройки → Telegram-бот) — без него сообщения не уходят.", "Telegram-бот туташкан эмес (Жөндөөлөр → Telegram-бот) — аны менен гана билдирүүлөр кетет.", "The Telegram bot isn't connected (Settings → Telegram bot) — nothing is sent without it.", "Telegram botu bağlı değil (Ayarlar → Telegram botu) — bağlanmadan mesaj gitmez.", "Telegram-bot ulanmagan (Sozlamalar → Telegram-bot) — usiz xabar ketmaydi.");

    private bool Save()
    {
        _error.IsVisible = false;
        double N(TextBox box, double fallback) => ShopAutomationText.ParseNumber(box.Text) ?? fallback;
        var bad = new[] { _shortageSum, _returnSum, _debtSum, _behindPercent, _behindHour, _behindShare, _eveningHour, _quietFrom, _quietTo }
            .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text) && ShopAutomationText.ParseNumber(b.Text) is null);
        if (bad is not null)
        {
            _error.Text = T("Вместо числа в одном из полей что-то другое — поправьте.", "Бир талаада сандын ордуна башка нерсе жазылган — оңдоңуз.", "One of the fields has something other than a number — please fix it.", "Alanlardan birinde sayı yerine başka bir şey var — düzeltin.", "Maydonlardan birida son o'rniga boshqa narsa — tuzating.");
            _error.IsVisible = true;
            bad.Focus();
            return false;
        }
        ShopAutomationSettings.Update(d =>
        {
            d.ExpiryMode = _expiry.SelectedIndex switch { 0 => ExpiryModes.Off, 2 => ExpiryModes.Block, _ => ExpiryModes.Warn };
            d.PlanLostDemand = _lost.IsChecked == true;
            d.PlanPackRules = _pack.IsChecked == true;
            d.PlanLeadTime = _lead.IsChecked == true;
            d.PlanTsb = _tsb.IsChecked == true;
            d.PlanWeekday = _weekday.IsChecked == true;
            d.PlanXyzSafety = _xyz.IsChecked == true;
            d.MorningOrderCheck = _morning.IsChecked == true;
            d.PriceNiceStep = _niceStep.SelectedIndex switch { 0 => 0, 1 => 1, 3 => 10, _ => 5 };
            d.CategoryTargetMarkup = ShopAutomationText.ParseMarkups(_categoryMarkups.Text);
            d.ReserveSuppliers = ShopAutomationText.ParseReserve(_reserve.Text);
            var a = d.Alerts;
            a.Enabled = _alerts.IsChecked == true;
            a.ShiftShortage = _shortage.IsChecked == true;
            a.ShiftShortageAmount = N(_shortageSum, 500);
            a.BigReturn = _return.IsChecked == true;
            a.BigReturnAmount = N(_returnSum, 3000);
            a.NewDebt = _debt.IsChecked == true;
            a.NewDebtAmount = N(_debtSum, 5000);
            a.RevenueBehindPlan = _behind.IsChecked == true;
            a.RevenueBehindPercent = (int)N(_behindPercent, 30);
            a.RevenueCheckHour = (int)N(_behindHour, 18);
            a.RevenueShareByCheck = (int)N(_behindShare, 70);
            a.ClassAOut = _classA.IsChecked == true;
            a.Expired = _expired.IsChecked == true;
            a.EveningSummary = _evening.IsChecked == true;
            a.EveningHour = (int)N(_eveningHour, 20);
            a.WeeklyReport = _weekly.IsChecked == true;
            a.QuietFromHour = (int)N(_quietFrom, 22);
            a.QuietToHour = (int)N(_quietTo, 8);
        });
        PosLogger.Log("Автоматизация магазина: настройки сохранены.", "INFO");
        return true;
    }
}
