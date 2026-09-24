namespace NurMarketKassa.Services;

/// <summary>Краткое описание изменений ТЕКУЩЕЙ версии — показывается кассиру один раз
/// в окне "Что нового" после того, как касса обновилась (см. UserPreferences.LastSeenAppVersion),
/// и используется как текст релиза при публикации на GitHub.
/// ВАЖНО: перед каждым релизом содержимое <see cref="Latest"/> (и переводов) полностью ЗАМЕНЯЕТСЯ
/// на изменения именно этой версии — не дописывается поверх старых пунктов. История прошлых версий
/// не нужна здесь: она уже есть в самих релизах на GitHub, а этот список — только "что нового прямо
/// сейчас". 2026-09-07: список показывается на языке интерфейса (ru/ky/en/tr/uz, см.
/// <see cref="LatestForCurrentLanguage"/>) — раньше при кыргызском интерфейсе заголовок был
/// кыргызским, а сами пункты русскими.</summary>
public static class AppChangelog
{
    public static readonly string[] Latest =
    [
        "Темы собраны заново. Прежние восемь меняли акцентный цвет и форму углов, а поверхности оставались теми же — переключение было почти незаметно. Теперь тема несёт весь облик: фон окон и панелей, цвет текста, плитки каталога и цену, границы, скруглённость, размер и начертание шрифта, а заодно размер самой плитки товара и высоту кнопок. Тем девять: Классическая (родной вид), Графит, Закат, Лесная, Контрастная (чёрно-белая, для яркого света и слабого зрения), Терминал (моноширинный, зелёный по тёмному), Сенсорная (всё крупное, для работы пальцем), Плотная (мелкая сетка, вдвое больше товаров на экране) и Витрина (цветная шапка, крупное фото товара). У каждой свой светлый и тёмный вариант, переключатель в шапке остаётся на месте. В Маркетплейсе у каждой темы живой предпросмотр её собственными цветами",
        "Склад: таблица товаров переделана. Код и штрихкод съехались в одну колонку в две строки, появилась колонка «Закупка» — себестоимость за единицу и вся сумма, которая лежит на полке этим товаром. Остаток показывается плашкой вместе с единицей измерения («93 шт»), зелёной или красной: заканчивающийся товар теперь виден при беглом взгляде, а не отличается оттенком цифры. Цена прижата вправо — разряды выстраиваются друг под другом. Название и кнопки действий больше не липнут к краям",
        "Под таблицей склада появился итог: сколько позиций, сколько единиц лежит, на какую сумму по закупке и по продаже, сколько товаров заканчивается. Считается по всему складу, а не по показанной странице",
        "На складе добавлен переключатель «Список / Плитки». Таблица отвечает на вопрос «сколько и почём», плитки — «что это»: товар узнают по фотографии",
        "На складе появилась вкладка «Перемещение» — журнал того, что уходило со склада: продажи и списания в одном списке, за неделю, месяц или квартал, с поиском по названию. Раньше движение можно было смотреть только по одному товару за раз, открыв его карточку",
        "В ABC-анализе не было видно, за какой срок показаны цифры: четыре кнопки периода выглядели одинаково. Отсюда же и «не работает Месяц» — он ставит те же 30 дней, что уже стояли, и без подсветки нажатие выглядело как будто ничего не произошло. Теперь выбранный период отмечен, в том числе когда даты выставлены руками в календарях",
    ];

    public static readonly string[] LatestKy =
    [
        "Темалар кайрадан түзүлдү. Мурунку сегизи акцент түсүн жана бурчтарды гана өзгөртчү, беттер ошол эле бойдон калчу. Эми тема бүт көрүнүштү алып жүрөт: терезелердин жана панелдердин фону, тексттин түсү, каталогдун плиткалары жана баасы, чектер, тегеректик, шрифттин өлчөмү жана түрү, ошондой эле плитканын өлчөмү менен баскычтардын бийиктиги. Темалар тогуз: Классикалык, Графит, Кеч батым, Токой, Контраст, Терминал, Сенсордук, Тыгыз жана Витрина. Ар биринин жарык жана караңгы варианты бар",
        "Кампа: товарлардын таблицасы кайра жасалды. Коду менен штрихкод бир мамычага, эки сапка кошулду; «Сатып алуу» мамычасы пайда болду — бирдиктин өздүк наркы жана текчедеги жалпы сумма. Калдык бирдиги менен жазылган калкан түрүндө көрсөтүлөт («93 шт»), жашыл же кызыл. Баа оңго тегизделди. Аты жана баскычтар четке жабышпайт",
        "Кампа таблицасынын астында жыйынтык чыкты: канча позиция, канча бирдик жатат, сатып алуу жана сатуу боюнча канча сумма, канча товар аяктап жатат. Бүт кампа боюнча эсептелет",
        "Кампада «Тизме / Плиткалар» которгучу кошулду. Таблица «канча жана канчадан» деген суроого жооп берет, плиткалар — «бул эмне»: товар сүрөтү боюнча таанылат",
        "Кампада «Жылышуу» өтмөгү пайда болду — кампадан эмне чыкканынын журналы: сатуулар жана эсептен чыгаруулар бир тизмеде, жума, ай же чейрек боюнча, аты менен издөө менен",
        "ABC-анализде цифралар кайсы мөөнөт үчүн экени көрүнчү эмес: төрт баскыч бирдей көрүнчү. Эми тандалган мезгил белгиленет",
    ];

    public static readonly string[] LatestEn =
    [
        "The themes have been rebuilt. The previous eight changed the accent colour and the corner shape while the surfaces stayed the same, so switching was barely visible. A theme now carries the whole look: window and panel backgrounds, text colour, catalogue tiles and prices, borders, rounding, font size and typeface, plus the size of the product tile itself and the height of controls. There are nine: Classic, Graphite, Sunset, Forest, Contrast (black and white, for bright light and poor eyesight), Terminal (monospace, green on dark), Touch (everything larger, for finger work), Compact (a fine grid, twice as many products on screen) and Showcase (a coloured header, large product photo). Each has its own light and dark variant, and the switch in the header stays where it was. The Marketplace shows a live preview of every theme in its own colours",
        "Warehouse: the product table has been rebuilt. Code and barcode now share one column on two lines, and a Cost column shows the purchase price per unit and the whole sum sitting on the shelf in that product. Stock is shown as a badge together with its unit (93 pcs), green or red: a product running out is now visible at a glance instead of differing by the shade of a digit. Prices are right-aligned so the digits line up. Names and action buttons no longer touch the edges",
        "A summary line appeared under the warehouse table: how many items, how many units are in stock, their value at cost and at sale price, and how many products are running low. It counts the whole warehouse, not the page on screen",
        "The warehouse got a List / Tiles switch. The table answers how many and at what price, the tiles answer what it is: a product is recognised by its photo",
        "The warehouse got a Movements tab - a log of what left the stock: sales and write-offs in one list, for a week, a month or a quarter, with a search by name. Previously movement could only be viewed one product at a time, from its card",
        "In the ABC analysis there was no way to see which period the numbers covered: all four period buttons looked the same. Hence also 'Month does not work' - it sets the same 30 days that were already set, and with no highlight the press looked like nothing happened. The selected period is now marked, including when the dates are set by hand",
    ];

    public static readonly string[] LatestTr =
    [
        "Temalar yeniden kuruldu. Önceki sekiz tema yalnızca vurgu rengini ve köşe biçimini değiştiriyordu, yüzeyler aynı kalıyordu. Artık tema görünümün tamamını taşıyor: pencere ve panel arka planları, metin rengi, katalog kutucukları ve fiyat, kenarlıklar, yuvarlaklık, yazı tipi boyutu ve türü, ayrıca ürün kutucuğunun boyutu ve düğmelerin yüksekliği. Dokuz tema var: Klasik, Grafit, Gün Batımı, Orman, Kontrast, Terminal, Dokunmatik, Yoğun ve Vitrin. Her birinin açık ve koyu varyantı var",
        "Depo: ürün tablosu yeniden yapıldı. Kod ve barkod tek sütunda iki satıra geçti, «Maliyet» sütunu eklendi. Stok, birimiyle birlikte yeşil ya da kırmızı bir etikette gösteriliyor. Fiyat sağa hizalandı. Ad ve düğmeler artık kenarlara yapışmıyor",
        "Depo tablosunun altında toplam satırı çıktı: kaç kalem, kaç adet, maliyet ve satış değeri, kaç ürün azalıyor. Tüm depo üzerinden sayılıyor",
        "Depoya «Liste / Kutucuk» anahtarı eklendi. Tablo kaç tane ve kaça sorusunu, kutucuklar bu nedir sorusunu yanıtlar",
        "Depoya «Hareketler» sekmesi eklendi - depodan çıkanların günlüğü: satışlar ve zayiat tek listede, hafta, ay veya çeyrek için, adla arama ile",
        "ABC analizinde sayıların hangi döneme ait olduğu görünmüyordu. Artık seçili dönem işaretleniyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Mavzular qaytadan tuzildi. Avvalgi sakkiztasi faqat urg'u rangi va burchak shaklini o'zgartirardi, yuzalar o'sha-o'sha qolardi. Endi mavzu butun ko'rinishni olib yuradi: oyna va panel foni, matn rangi, katalog plitkalari va narx, chegaralar, yumaloqlik, shrift o'lchami va turi, shuningdek plitkaning o'lchami va tugmalar balandligi. Mavzular to'qqizta: Klassik, Grafit, Quyosh botishi, O'rmon, Kontrast, Terminal, Sensorli, Zich va Vitrina. Har birining yorug' va qorong'i varianti bor",
        "Ombor: mahsulotlar jadvali qayta qilindi. Kod va shtrix-kod bitta ustunga, ikki qatorga birlashdi; «Tannarx» ustuni paydo bo'ldi. Qoldiq birligi bilan yashil yoki qizil yorliqda ko'rsatiladi. Narx o'ngga tekislandi. Nom va tugmalar endi chetlarga yopishmaydi",
        "Ombor jadvali ostida yakun paydo bo'ldi: nechta pozitsiya, nechta birlik, tannarx va sotuv bo'yicha qancha summa, nechta mahsulot tugayapti. Butun ombor bo'yicha hisoblanadi",
        "Omborda «Ro'yxat / Plitkalar» almashtirgichi qo'shildi. Jadval qancha va qanchadan degan savolga, plitkalar bu nima degan savolga javob beradi",
        "Omborda «Harakatlar» bo'limi paydo bo'ldi - ombordan nima chiqqanining jurnali: sotuvlar va hisobdan chiqarishlar bitta ro'yxatda, hafta, oy yoki chorak uchun, nom bo'yicha qidiruv bilan",
        "ABC tahlilida raqamlar qaysi davrga tegishli ekani ko'rinmasdi. Endi tanlangan davr belgilanadi",
    ];

    /// <summary>Список на языке интерфейса (2026-09-07).</summary>
    public static string[] LatestForCurrentLanguage() =>
        UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => LatestKy,
            AppLanguage.English => LatestEn,
            AppLanguage.Turkish => LatestTr,
            AppLanguage.Uzbek => LatestUz,
            _ => Latest,
        };

    public static string LatestAsBulletedText() =>
        string.Join("\n", System.Linq.Enumerable.Select(LatestForCurrentLanguage(), line => "• " + line));
}