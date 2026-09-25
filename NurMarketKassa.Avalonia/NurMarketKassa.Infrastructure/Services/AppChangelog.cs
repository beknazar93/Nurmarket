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
        "В «Настройки → Операции» появилась «Сфера магазина»: Продуктовый (как было), Одежда и похожие, Услуги",
        "Одежда: в окне оплаты блок «Консультант», как на сайте — выбор сотрудника, процент подставляется из его схемы зарплаты, рядом «Комиссия ≈». Консультант печатается в чеке и попадает в зарплату",
        "Услуги: каталог открывается на вкладке «Услуги», у услуг больше не показывается остаток «0 шт.»",
        "Новое окно «Зарплата» (меню → Информация и клиенты): начисления сотрудникам за период по расчёту сервера — оклад, продажи как кассир и как консультант, комиссия, бонус, к выплате. Кнопка «Схема» меняет схему начисления сотрудника",
        "«Продажи» и «Финансы» сверены с сайтом до копейки: выручка, чеки, средний чек, наличные, безнал, возвраты, прибыль и маржа за день, неделю и месяц",
        "Исправлено: если среди последних продаж был возврат, «Финансы» и «Продажи» загружали только 80 последних чеков — цифры за неделю и месяц были занижены",
        "Продажа в долг больше не считается выручкой, пока не оплачена — как на сайте. В списке продаж она видна с пометкой «Долг»",
        "Возвраты в «Продажах» и «Финансах» берутся с сервера: видны и возвраты, оформленные на сайте или на другой кассе",
        "Чек: при повторной печати и в предпросмотре появились дата, время, способ оплаты, внесено и сдача. Для смешанной оплаты печатаются наличная и безналичная части, для продажи в долг — сумма долга",
        "Озвучка кассы своим голосом: подсказки голосового управления можно записать с микрофона или загрузить файлом",
        "Склад: фильтр статусов перемещений и выбор поставщика в приёмке сделаны кнопками — выпадающие списки там не открывались. Принятое или отменённое перемещение больше не редактируется",
        "Смешанная оплата: надпись «Сумма сходится» подсвечивается правильным цветом",
    ];

    public static readonly string[] LatestKy =
    [
        "«Жөндөөлөр → Операциялар» бөлүмүндө «Дүкөндүн тармагы» пайда болду: Азык-түлүк (мурункудай), Кийим жана ушул сыяктуу, Кызматтар",
        "Кийим: төлөө терезесинде «Консультант» блогу, сайттагыдай — кызматкерди тандоо, пайыз анын эмгек акы схемасынан коюлат, жанында «Комиссия ≈». Консультант чекте басылат жана эмгек акыга кирет",
        "Кызматтар: каталог «Кызматтар» өтмөгүндө ачылат, кызматтарда «0 даана» калдык көрсөтүлбөйт",
        "Жаңы «Эмгек акы» терезеси (меню → Маалымат жана кардарлар): мезгил үчүн кызматкерлердин эмгек акысы сервердин эсеби боюнча — айлык, кассир жана консультант катары сатуу, комиссия, бонус, төлөнө турган сумма. «Схема» баскычы эсептөө схемасын өзгөртөт",
        "«Сатуулар» жана «Каржы» сайт менен тыйынга чейин текшерилди: түшкөн акча, чектер, орточо чек, накталай, накталай эмес, кайтаруулар, пайда жана маржа — күн, жума жана ай үчүн",
        "Оңдолду: акыркы сатуулардын ичинде кайтаруу болсо, «Каржы» жана «Сатуулар» акыркы 80 чекти гана жүктөчү — жума жана ай үчүн сандар аз чыгып жаткан",
        "Карызга сатуу төлөнгөнгө чейин түшкөн акчага кошулбайт — сайттагыдай. Тизмеде ал «Карыз» белгиси менен көрүнөт",
        "«Сатуулар» жана «Каржы» бөлүмүндөгү кайтаруулар серверден алынат: сайтта же башка кассада жасалган кайтаруулар да көрүнөт",
        "Чек: кайра басууда жана алдын ала көрүүдө дата, убакыт, төлөө ыкмасы, берилген акча жана кайтарым пайда болду. Аралаш төлөмдө накталай жана накталай эмес бөлүгү, карызга сатууда карыз суммасы басылат",
        "Кассаны өз үнүңүз менен сүйлөтүү: үн менен башкаруунун эскертүүлөрүн микрофондон жазса же файл менен жүктөсө болот",
        "Кампа: жылышуулардын статус чыпкасы жана кабыл алуудагы жеткирүүчүнү тандоо баскычтар менен жасалды — ачылуучу тизмелер ал жерде ачылчу эмес. Кабыл алынган же жокко чыгарылган жылышуу оңдолбойт",
        "Аралаш төлөм: «Сумма туура келет» жазуусу туура түс менен белгиленет",
    ];

    public static readonly string[] LatestEn =
    [
        "«Settings → Operations» has a new «Store type»: Grocery (as before), Clothing and similar, Services",
        "Clothing: the payment window has a «Consultant» block, as on the website — pick an employee, the percentage comes from their pay scheme, with «Commission ≈» next to it. The consultant is printed on the receipt and counted in salaries",
        "Services: the catalogue opens on the «Services» tab, and services no longer show a «0 pcs» stock",
        "New «Salary» window (menu → Information and clients): employee pay for a period as calculated by the server — salary, sales as cashier and as consultant, commission, bonus, payable. The «Scheme» button changes an employee's pay scheme",
        "«Sales» and «Finance» now match the website to the kopeck: revenue, receipts, average receipt, cash, non-cash, returns, profit and margin for the day, week and month",
        "Fixed: if there was a return among recent sales, «Finance» and «Sales» loaded only the last 80 receipts — weekly and monthly figures were too low",
        "A sale on credit is no longer counted as revenue until it is paid — as on the website. It is shown in the sales list marked «Debt»",
        "Returns in «Sales» and «Finance» come from the server, so returns made on the website or on another POS are shown too",
        "Receipt: reprints and the preview now show the date, time, payment method, amount received and change. Mixed payments print the cash and non-cash parts, credit sales print the debt amount",
        "Your own voice for the POS: voice control prompts can be recorded with a microphone or uploaded as a file",
        "Warehouse: the transfer status filter and the supplier choice in receiving are now buttons — the drop-down lists there did not open. An accepted or cancelled transfer can no longer be edited",
        "Mixed payment: the «Amount matches» label now has the correct colour",
    ];

    public static readonly string[] LatestTr =
    [
        "«Ayarlar → İşlemler» bölümünde yeni «Mağaza türü»: Market (eskisi gibi), Giyim ve benzeri, Hizmetler",
        "Giyim: ödeme penceresinde sitedeki gibi «Danışman» bölümü — personel seçilir, yüzde maaş şemasından gelir, yanında «Komisyon ≈». Danışman fişe basılır ve maaşa yansır",
        "Hizmetler: katalog «Hizmetler» sekmesinde açılır, hizmetlerde artık «0 adet» stok gösterilmez",
        "Yeni «Maaş» penceresi (menü → Bilgi ve müşteriler): dönem için personel maaşları sunucunun hesabıyla — maaş, kasiyer ve danışman olarak satışlar, komisyon, bonus, ödenecek tutar. «Şema» düğmesi personelin ödeme şemasını değiştirir",
        "«Satışlar» ve «Finans» site ile kuruşuna kadar eşleşiyor: ciro, fişler, ortalama fiş, nakit, nakit dışı, iadeler, kâr ve marj — gün, hafta ve ay için",
        "Düzeltildi: son satışlar arasında iade varsa «Finans» ve «Satışlar» yalnızca son 80 fişi yüklüyordu — haftalık ve aylık rakamlar düşük çıkıyordu",
        "Veresiye satış ödenene kadar artık ciroya sayılmaz — sitedeki gibi. Satış listesinde «Borç» işaretiyle görünür",
        "«Satışlar» ve «Finans» içindeki iadeler sunucudan alınır: sitede veya başka kasada yapılan iadeler de görünür",
        "Fiş: yeniden basımda ve önizlemede tarih, saat, ödeme yöntemi, alınan tutar ve para üstü var. Karışık ödemede nakit ve nakit dışı kısımlar, veresiye satışta borç tutarı basılır",
        "Kasa kendi sesinizle: sesli kontrol uyarıları mikrofonla kaydedilebilir veya dosya olarak yüklenebilir",
        "Depo: transfer durum filtresi ve mal kabulde tedarikçi seçimi düğmelerle yapıldı — oradaki açılır listeler açılmıyordu. Kabul edilmiş veya iptal edilmiş transfer artık düzenlenemez",
        "Karışık ödeme: «Tutar uyuşuyor» yazısı doğru renkte gösterilir",
    ];

    public static readonly string[] LatestUz =
    [
        "«Sozlamalar → Operatsiyalar» bo'limida «Do'kon turi» paydo bo'ldi: Oziq-ovqat (avvalgidek), Kiyim va shunga o'xshash, Xizmatlar",
        "Kiyim: to'lov oynasida saytdagidek «Maslahatchi» bloki — xodimni tanlash, foiz uning ish haqi sxemasidan qo'yiladi, yonida «Komissiya ≈». Maslahatchi chekda chiqadi va ish haqiga kiradi",
        "Xizmatlar: katalog «Xizmatlar» bo'limida ochiladi, xizmatlarda endi «0 dona» qoldiq ko'rsatilmaydi",
        "Yangi «Ish haqi» oynasi (menyu → Ma'lumot va mijozlar): davr uchun xodimlar ish haqi server hisobi bo'yicha — maosh, kassir va maslahatchi sifatida sotuv, komissiya, bonus, to'lanadigan summa. «Sxema» tugmasi xodimning hisoblash sxemasini o'zgartiradi",
        "«Sotuvlar» va «Moliya» sayt bilan tiyinigacha tekshirildi: tushum, cheklar, o'rtacha chek, naqd, naqd pulsiz, qaytarishlar, foyda va marja — kun, hafta va oy uchun",
        "Tuzatildi: oxirgi sotuvlar orasida qaytarish bo'lsa, «Moliya» va «Sotuvlar» faqat oxirgi 80 chekni yuklardi — hafta va oy raqamlari kam chiqardi",
        "Qarzga sotuv to'lanmaguncha tushumga qo'shilmaydi — saytdagidek. Sotuvlar ro'yxatida u «Qarz» belgisi bilan ko'rinadi",
        "«Sotuvlar» va «Moliya»dagi qaytarishlar serverdan olinadi: saytda yoki boshqa kassada qilingan qaytarishlar ham ko'rinadi",
        "Chek: qayta chop etishda va oldindan ko'rishda sana, vaqt, to'lov usuli, berilgan pul va qaytim paydo bo'ldi. Aralash to'lovda naqd va naqd pulsiz qismlari, qarzga sotuvda qarz summasi chiqadi",
        "Kassani o'z ovozingiz bilan gapirtirish: ovozli boshqaruv eslatmalarini mikrofondan yozish yoki fayl bilan yuklash mumkin",
        "Ombor: ko'chirishlar holati filtri va qabul qilishda yetkazib beruvchini tanlash tugmalar bilan qilindi — ochiladigan ro'yxatlar u yerda ochilmas edi. Qabul qilingan yoki bekor qilingan ko'chirish endi tahrirlanmaydi",
        "Aralash to'lov: «Summa mos keladi» yozuvi to'g'ri rangda ko'rsatiladi",
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
