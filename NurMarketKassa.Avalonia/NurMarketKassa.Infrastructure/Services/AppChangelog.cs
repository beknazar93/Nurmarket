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
        "Касса не запускалась после обновления: на входе появлялась ошибка «Не удалось загрузить кассу: SQLite Error 1: no such column: company_id». Разделение данных между аккаунтами добавило в историю продаж колонку владельца, а индексы по ней создавались раньше, чем сама колонка появлялась в уже существующей базе. На новой базе порядок значения не имел, и при проверке ошибка не всплыла — она показывалась только там, где касса работала до обновления. Порядок исправлен: сначала колонка, потом индексы",
        "Ползунок «Масштаб» в настройках экрана ничего не менял: сколько бы ни ставили — 120%, 170%, 200% — интерфейс оставался прежним. Рядом с выбором пользователя работал автоподбор под размер экрана, и он умеет только уменьшать; в итоге из двух значений всегда бралось меньшее, то есть 100%. Теперь увеличение применяется так, как выбрано, а автоподбор остаётся для маленьких экранов терминалов, где макет не помещается",
        "Карточка товара в каталоге больше не пытается показать всё сразу. Раньше треть её высоты занимала полоса под фотографию — а фотография есть у единиц товаров, у остальных там был пустой серый прямоугольник; ниже шли штрихкод, который кассир всё равно не читает глазами, и плашка «Штучный» почти на каждой карточке. Осталось три вещи: что это, сколько стоит и сколько осталось. Фотография, если она есть, стоит сверху по центру и не наезжает на название; если её нет — название занимает середину карточки",
        "Четыре значка справа вверху подписаны: «Экран», «Тема», «Клавиши», «Горячие». Раньше значение каждого приходилось вспоминать или наводить мышь и ждать подсказку. Из центра шапки убрано название кассы — оно уже стоит слева, рядом с логотипом, и выходило два одинаковых названия подряд",
        "Пока чек пуст, правую половину экрана занимал рисунок тележки с подписью «Выберите товар слева». Теперь под ней лежат товары, отмеченные звёздочкой в каталоге: нажатие добавляет товар в чек. Если звёздочкой ничего не отмечено, блока нет вовсе",
        "Окно настроек открывалось чуть меньше экрана, и снизу из-под него просвечивал каталог кассы. Причина была в ограничении, которое нужно для маленьких экранов моноблоков: оно не давало окну занять экран целиком даже в полноэкранном режиме. Это касалось всех больших окон программы",
        "В настройках не выводились подписи переключателей — были видны только сами тумблеры. Так стояли «Применить эти обои к экрану кассира», «Присылать сводку при закрытии смены», «Отвечать на команды в Telegram»: включатель есть, а что он включает — нигде не сказано",
        "Настройки приведены к одному виду: колонка одной ширины на всех вкладках (раньше карточки скакали от края к краю при переключении), заголовки страниц и кнопки разделов прижаты к левому краю, значки вкладок и разделов — одним стилем вместо цветных эмодзи, длинные пояснения переносятся по словам, а не уезжают одной строкой во всю ширину окна",
        "В боковом меню видно, открыта ли смена и кто за кассой. Раньше там стояла неподвижная подпись «Кассовая смена» и остаток, который и так показан в шапке; имя кассира не показывалось нигде, хотя при пересменке это первое, что стоит проверить — чек уйдёт на сервер от того, кто записан в кассе",
    ];

    public static readonly string[] LatestKy =
    [
        "Жаңыртуудан кийин касса ачылбай калган: кирүүдө «Не удалось загрузить кассу: SQLite Error 1: no such column: company_id» катасы чыкчу. Аккаунттарды бөлүү тарыхка ээси боюнча мамыча кошкон, ал эми ал мамыча базада пайда болгонго чейин индекстер түзүлүп жаткан. Жаңы базада тартиби маанилүү эмес болчу, ошондуктан текшерүүдө ката көрүнгөн эмес. Эми тартип оңдолду: адегенде мамыча, анан индекстер",
        "Жөндөөлөрдөгү «Масштаб» жылдыргычы эч нерсе өзгөртчү эмес: 120%, 170%, 200% койсоңуз да интерфейс мурункудай калчу. Себеби экранга ылайыкташтыруу кичирейте гана алат жана ар дайым кичүү маани алынчу — 100%. Эми чоңойтуу тандалгандай колдонулат, ал эми ылайыкташтыруу макет батпаган кичине экрандар үчүн калды",
        "Каталогдогу товар карточкасы эми баарын бир жолу көрсөтүүгө аракет кылбайт. Мурда бийиктигинин үчтөн бири сүрөткө арналган тилке эле, ал эми сүрөт аз товарда бар — калгандарында ал жөн гана боз төрт бурчтук болчу; андан кийин штрихкод жана дээрлик ар бир карточкада «Даана» жазуусу турчу. Эми үч нерсе калды: бул эмне, канча турат жана канча калды. Сүрөт бар болсо — өйдө жагында, ортодо турат жана атына тийбейт; жок болсо — аты карточканын ортосунда",
        "Оң жактагы төрт белгиге жазуу кошулду: «Экран», «Тема», «Баскыч», «Ыкчам». Баштын ортосунан кассанын аты алынды — ал сол жакта, логотиптин жанында турат эле",
        "Чек бош турганда оң жарымында араба сүрөтү гана турчу. Эми анын астында каталогдо жылдызча менен белгиленген товарлар турат: басканда товар чекке кошулат. Эч нерсе белгиленбесе, бул блок такыр көрүнбөйт",
        "Жөндөөлөр терезеси экрандан бир аз кичине ачылып, астынан касса көрүнүп турчу. Себеби — кичине экрандар үчүн керектүү чектөө: ал терезеге толук экранды ээлөөгө жол берчү эмес. Бул программанын бардык чоң терезелерине тийиштүү болчу",
        "Жөндөөлөрдө которгучтардын жазуулары такыр чыкчу эмес — өзү гана көрүнчү. «Бул тушкагазды кассирдин экранына колдонуу», «Смена жабылганда жыйынтык жиберүү», «Telegram буйруктарына жооп берүү» ушундай турчу: которгуч бар, эмнени күйгүзөөрү жазылган эмес",
        "Жөндөөлөр бир түргө келтирилди: бардык барактарда мамычанын туурасы бирдей, баштар жана бөлүм баскычтары сол жакка тегизделди, белгилер түстүү эмодзинин ордуна бир стилде, узун түшүндүрмөлөр сөз боюнча которулат",
        "Каптал менюда смена ачыкпы жана касса артында ким турганы көрүнөт. Мурда ал жерде жылбаган «Кассалык смена» жазуусу жана баштагыдай эле калдык турчу, ал эми кассирдин аты эч жерде көрүнчү эмес",
    ];

    public static readonly string[] LatestEn =
    [
        "The POS would not start after the update: signing in produced «Could not load the POS: SQLite Error 1: no such column: company_id». Separating data between accounts added an owner column to the sales history, and the indexes over it were created before the column itself appeared in an existing database. On a fresh database the order did not matter, so testing never showed it — only tills that had been running before the update were affected. The order is fixed: column first, indexes after",
        "The Scale slider in screen settings changed nothing: 120%, 170%, 200% - the interface stayed the same. Alongside the chosen value ran an auto-fit for the screen size, and auto-fit can only shrink; the smaller of the two always won, which was 100%. Enlarging now applies as chosen, while auto-fit remains for the small terminal screens the layout does not fit",
        "The product tile in the catalogue no longer tries to show everything at once. A third of its height used to be a strip for a photo - and only a handful of products have one, so for the rest it was an empty grey rectangle; below it sat a barcode the cashier never reads by eye and a Piece badge on almost every tile. Three things remain: what it is, what it costs and what is left. The photo, when there is one, sits at the top centre and no longer overlaps the name; when there is none, the name takes the middle of the tile",
        "The four icons at the top right are now labelled: Display, Theme, Keyboard, Hotkeys. The till name has been removed from the centre of the header - it already stands on the left, next to the logo, so the same name appeared twice in a row",
        "While the receipt is empty, the right half of the screen used to hold a trolley drawing. Below it there are now the products starred in the catalogue: a tap adds one to the receipt. If nothing is starred, the block does not appear at all",
        "The settings window opened slightly smaller than the screen, with the catalogue showing through underneath. The cause was a cap meant for the small screens of all-in-one terminals: it stopped the window from filling the screen even in fullscreen mode. This affected every large window in the app",
        "Toggle captions in Settings were not rendered at all - only the switches were visible. That was the case for Apply this wallpaper to the cashier screen, Send a summary when the shift closes and Reply to Telegram commands: a switch with nothing to say what it switches",
        "Settings now look consistent: one column width on every tab (cards used to jump from edge to edge when switching), page titles and section buttons aligned to the left, tab and section icons in a single style instead of colour emoji, and long explanations wrapped instead of running the full width of the window",
        "The side menu now shows whether the shift is open and who is at the till. It used to carry a fixed Cash shift caption and the balance already shown in the header, while the cashier's name appeared nowhere",
    ];

    public static readonly string[] LatestTr =
    [
        "Güncellemeden sonra kasa açılmıyordu: girişte «Kasa yüklenemedi: SQLite Error 1: no such column: company_id» hatası çıkıyordu. Hesaplar arası veri ayrımı satış geçmişine sahip sütunu ekledi, ancak bu sütun mevcut veritabanında oluşmadan önce üzerindeki dizinler oluşturuluyordu. Yeni veritabanında sıra önemli değildi, bu yüzden testte görünmedi. Sıra düzeltildi: önce sütun, sonra dizinler",
        "Ekran ayarlarındaki «Ölçek» kaydırıcısı hiçbir şeyi değiştirmiyordu: 120%, 170%, 200% - arayüz aynı kalıyordu. Seçilen değerin yanında ekran boyutuna göre otomatik uyum çalışıyordu ve o yalnızca küçültebilir; ikisinden küçük olan, yani 100% seçiliyordu. Artık büyütme seçildiği gibi uygulanıyor",
        "Katalogdaki ürün kartı artık her şeyi birden göstermeye çalışmıyor. Yüksekliğinin üçte biri fotoğraf şeridiydi - oysa fotoğrafı olan ürün pek az, diğerlerinde orası boş gri bir dikdörtgendi; altında kasiyerin gözle okumadığı barkod ve neredeyse her kartta «Adetli» etiketi vardı. Üç şey kaldı: bu nedir, kaça ve kaç tane kaldı. Fotoğraf varsa üstte ortada durur ve adın üzerine binmez; yoksa ad kartın ortasına yerleşir",
        "Sağ üstteki dört simge artık etiketli: Ekran, Tema, Klavye, Kısayol. Başlığın ortasından kasa adı kaldırıldı - zaten solda, logonun yanında duruyordu",
        "Fiş boşken ekranın sağ yarısında bir alışveriş arabası çizimi dururdu. Artık altında katalogda yıldızlanan ürünler var: dokunmak ürünü fişe ekler. Hiçbir şey yıldızlanmamışsa bu blok hiç görünmez",
        "Ayarlar penceresi ekrandan biraz küçük açılıyor, altından kasa görünüyordu. Sebep, küçük ekranlar için konan bir üst sınırdı: tam ekran modunda bile pencerenin ekranı kaplamasını engelliyordu. Bu, programın bütün büyük pencerelerini etkiliyordu",
        "Ayarlar'da anahtarların yazıları hiç görünmüyordu - yalnızca anahtarın kendisi vardı. «Bu duvar kağıdını kasiyer ekranına uygula», «Vardiya kapanınca özet gönder» ve «Telegram komutlarına yanıt ver» böyleydi",
        "Ayarlar tek bir görünüme kavuştu: her sekmede aynı sütun genişliği, sola hizalı sayfa başlıkları ve bölüm düğmeleri, renkli emoji yerine tek stilde simgeler, uzun açıklamalar tek satır yerine sarılarak",
        "Yan menüde vardiyanın açık olup olmadığı ve kasada kimin olduğu görünüyor. Önceden orada sabit bir «Kasa vardiyası» yazısı ve zaten başlıkta olan bakiye vardı; kasiyerin adı hiçbir yerde yoktu",
    ];

    public static readonly string[] LatestUz =
    [
        "Yangilanishdan keyin kassa ochilmasdi: kirishda «Kassani yuklab bo'lmadi: SQLite Error 1: no such column: company_id» xatosi chiqardi. Hisoblar o'rtasida ma'lumotlarni ajratish sotuv tarixiga egasi ustunini qo'shgan edi, ammo indekslar bu ustun mavjud bazada paydo bo'lishidan oldin yaratilardi. Yangi bazada tartib ahamiyatsiz edi, shuning uchun tekshiruvda ko'rinmadi. Tartib to'g'rilandi: avval ustun, keyin indekslar",
        "Ekran sozlamalaridagi «Masshtab» slayderi hech narsani o'zgartirmasdi: 120%, 170%, 200% qo'ysangiz ham interfeys o'sha-o'sha qolardi. Tanlangan qiymat yonida ekran o'lchamiga moslash ishlardi, u esa faqat kichraytira oladi; ikkovidan kichigi, ya'ni 100% olinardi. Endi kattalashtirish tanlanganidek qo'llanadi",
        "Katalogdagi mahsulot kartasi endi hammasini birdan ko'rsatishga urinmaydi. Ilgari balandligining uchdan biri foto uchun bo'lgan chiziq edi - foto esa sanoqli mahsulotda bor, qolganlarida u bo'sh kulrang to'rtburchak edi; pastida kassir ko'z bilan o'qimaydigan shtrix-kod va deyarli har kartada «Donali» yozuvi turardi. Uch narsa qoldi: bu nima, qancha turadi va qancha qolgan. Foto bo'lsa - yuqorida, markazda, nomga tegmaydi; bo'lmasa - nom kartaning o'rtasida",
        "O'ng yuqoridagi to'rt belgi endi imzolangan: Ekran, Mavzu, Klaviatura, Tezkor. Sarlavha markazidan kassa nomi olib tashlandi - u allaqachon chapda, logotip yonida turardi",
        "Chek bo'sh bo'lganda ekranning o'ng yarmida arava rasmi turardi. Endi uning ostida katalogda yulduzcha qo'yilgan mahsulotlar bor: bosish mahsulotni chekka qo'shadi. Hech narsa belgilanmagan bo'lsa, bu blok umuman ko'rinmaydi",
        "Sozlamalar oynasi ekrandan biroz kichik ochilib, ostidan kassa ko'rinib turardi. Sabab - kichik ekranlar uchun qo'yilgan cheklov: u oynaga to'liq ekranni egallashga yo'l qo'ymasdi. Bu dasturning barcha katta oynalariga tegishli edi",
        "Sozlamalarda o'tkazgichlarning yozuvlari umuman chiqmasdi - faqat o'tkazgichning o'zi ko'rinardi. «Bu fonni kassir ekraniga qo'llash», «Smena yopilganda hisobot yuborish», «Telegram buyruqlariga javob berish» shunday edi",
        "Sozlamalar bir ko'rinishga keltirildi: barcha bo'limlarda ustun kengligi bir xil, sarlavhalar va bo'lim tugmalari chapga tekislangan, belgilar rangli emoji o'rniga bitta uslubda, uzun izohlar so'z bo'yicha ko'chiriladi",
        "Yon menyuda smena ochiqligi va kassada kim turgani ko'rinadi. Ilgari u yerda qimirlamas «Kassa smenasi» yozuvi va sarlavhada allaqachon bor qoldiq turardi; kassirning ismi hech qayerda yo'q edi",
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