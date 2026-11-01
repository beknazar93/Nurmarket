## v1.17.71

Надёжнее деньги и смены, быстрее оплата и отчёты, два принтера и печать по сети, крупнее кнопки и лучше контраст.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.71.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.71.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.71.zip` (Intel); Android — `nurmarket-kassa-android-1.17.71.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Возвраты.** Возврат чека «в долг» после частичной оплаты выдаёт правильную сумму: перед возвратом касса показывает «К выдаче (ожидаемо)». Если сервер отказал («возврат больше положенного»), деньги не считаются выданными. Повторный возврат того же чека блокируется сразу, причина возврата доходит до сервера и видна владельцу, обмен чека «в долг» защищён от ошибок в суммах.
- **Смены без связи.** Смена, открытая без связи, не теряется после возвращения связи. Чеки старше 7 суток не проводятся «сегодняшним числом», а ждут решения владельца.
- **Закрытие смены.** Неотправленные внесения и изъятия досылаются, а если не получилось — смену можно закрыть «с пометкой». X-отчёт сходится со строкой «Касса:».
- **Оплата.** Касса ждёт ответ сервера до 5 секунд и честно пишет «Сервер отвечает медленно». «Оплатить» с 30 товарами в чеке открывается за доли секунды.
- **Скорость.** Чеки и отчёты на больших базах открываются быстрее благодаря индексу продаж. Программа владельца занимает меньше памяти, «Бонусы», «Долги» и отчёты работают быстрее.
- **Сбой сервера.** Вместо «продаж нет» касса пишет «Сервер NurCRM недоступен».
- **Товары и склад.** Правка карточки не затирает остаток и цены, есть защита от дублей товара, перемещения и приход переживают обрыв связи, повторная загрузка исправленного файла прихода не удваивает приход.
- **Деньги в ящике и ввод.** Выплата зарплаты не больше остатка в ящике; штрихкод, попавший в поле количества или «Получено», не принимается за число; настройки кассы не портятся при сбое питания.
- **Два принтера.** Чеки печатаются на одном принтере, этикетки и штрихкоды — на другом (модели могут быть разными). Штрихкоды идут сразу на выбранный принтер этикеток.
- **Печать по сети.** Принтер можно подключить по сети (LAN, порт 9100).
- **Массовая печать штрихкодов.** Листы A4 (11 готовых форматов, можно начать с нужной ячейки), «Копии = остаток», сканирование сразу в печать, предупреждения по штрихкоду, этикетка одной кнопкой; отдельное право «Печать штрих-кодов».
- **Дизайн.** Кнопки крупнее, шрифты не меньше 12, лучше контраст; боковая панель программы владельца помещается на ноутбуках; «Масштаб интерфейса» работает во всей программе владельца; на кыргызском, английском, турецком и узбекском больше нет русских вставок.

---

# Кыргызча

### Эмнелер жаңы

- **Кайтаруулар.** «Карызга» чекти жарым-жартылай төлөгөндөн кийин кайтаруу так сумманы берет: кайтаруунун алдында касса «Берүүгө (күтүлүүдө)» сумманы көрсөтөт. Сервер четке какса («кайтаруу тийиштүүдөн ашык»), акча берилди деп эсептелбейт. Бир эле чекти кайра кайтарууга дароо тыюу салынат, кайтаруунун себеби серверге жетет жана ээси көрөт, «карызга» чекти алмаштыруу сумма каталарынан корголгон.
- **Байланышсыз смена.** Байланышсыз ачылган смена байланыш калыбына келгенден кийин жоголбойт. 7 суткадан эски чектер «бүгүнкү күн менен» өткөрүлбөйт, ээсинин чечимин күтөт.
- **Сменаны жабуу.** Жөнөтүлбөгөн киргизүүлөр жана алуулар кошумча жөнөтүлөт, болбосо сменаны «белги менен» жабууга болот. X-отчёт «Касса:» сабы менен дал келет.
- **Төлөм.** Касса серверден жоопту 5 секундга чейин күтөт жана ачык «Сервер жай жооп берүүдө» деп жазат. Чекте 30 товар болгондо «Төлөө» секунданын бөлүгүндө ачылат.
- **Ылдамдык.** Чоң базалардагы чектер жана отчёттор сатуулар индекси аркылуу тезирээк ачылат. Ээсинин программасы эстутумду аз ээлейт, «Бонустар», «Карыздар» жана отчёттор тезирээк иштейт.
- **Сервер иштебей калганда.** «Сатуу жок» ордуна касса «NurCRM сервери жеткиликсиз» деп жазат.
- **Товарлар жана кампа.** Товар карточкасын оңдоо калдыкты жана бааларды өчүрбөйт, товардын кайталанышынан коргоо бар, жылдыруулар жана кирүүлөр байланыш үзүлсө да сакталат, оңдолгон кирүү файлын кайра жүктөө кирүүнү эки эсе кылбайт.
- **Суурмадагы акча жана киргизүү.** Айлык төлөө кассадагы акча калдыгынан ашпайт; саны же «Алынды» талаасына түшүп калган штрих-код сан катары кабыл алынбайт; кассанын жөндөөлөрү электр өчкөндө бузулбайт.
- **Эки принтер.** Чектер бир принтерде, энбелгилер жана штрих-коддор экинчисинде басылат (моделдери ар башка болушу мүмкүн). Штрих-коддор түз эле тандалган энбелги принтерине барат.
- **Тармак аркылуу басуу.** Принтерди тармак аркылуу туташтырууга болот (LAN, 9100 порт).
- **Штрих-коддорду жапырт басуу.** A4 барактар (11 даяр формат, керектүү уячадан баштаса болот), «Көчүрмөлөр = калдык», сканерлөө түз эле басууга, штрих-код боюнча эскертүүлөр, энбелги бир баскыч менен; өзүнчө «Штрих-код басып чыгаруу» укугу.
- **Дизайн.** Баскычтар чоңураак, шрифттер 12ден кем эмес, контраст жакшыраак; ээсинин программасынын капталдагы панели ноутбукка батат; «Интерфейс масштабы» ээсинин бүт программасында иштейт; кыргызча, англисче, түркчө жана өзбекче тилдерде орусча кыстармалар жок.

---

# English

### What's new

- **Returns.** Returning an “on credit” receipt after a partial payment gives the right amount: before the return the till shows “To pay out (expected)”. If the server refuses (“return exceeds what is due”), the money is not counted as paid out. A repeat return of the same receipt is blocked at once, the return reason reaches the server and the owner can see it, and exchanging an “on credit” receipt is protected against amount errors.
- **Shifts without a connection.** A shift opened without a connection is no longer lost when the connection returns. Receipts older than 7 days are not posted with today's date and wait for the owner's decision.
- **Closing a shift.** Unsent cash-in and cash-out entries are sent on, and if that fails the shift can be closed “with a note”. The X-report matches the “Cash:” line.
- **Payment.** The till waits up to 5 seconds for the server and honestly says “The server is answering slowly”. “Pay” with 30 items in the receipt opens in a fraction of a second.
- **Speed.** Receipts and reports on large databases open faster thanks to a sales index. The owner program uses less memory; “Bonuses”, “Debts” and reports are faster.
- **Server failure.** Instead of “no sales” the till says “The NurCRM server is unavailable”.
- **Products and warehouse.** Editing a product card no longer overwrites stock and prices, there is protection against duplicate products, transfers and receipts survive a dropped connection, and re-uploading a corrected receipt file does not double the receipt.
- **Drawer cash and input.** Salary payout cannot exceed the cash left in the drawer; a barcode that lands in the quantity or “Received” field is not taken as a number; till settings are not corrupted by a power failure.
- **Two printers.** Receipts print on one printer, labels and barcodes on another (the models may differ). Barcodes go straight to the chosen label printer.
- **Network printing.** A printer can be connected over the network (LAN, port 9100).
- **Bulk barcode printing.** A4 sheets (11 ready formats, start from any cell), “Copies = stock”, scan straight to print, barcode warnings, a label with one button; a separate “Barcode printing” permission.
- **Design.** Bigger buttons, fonts of at least 12, better contrast; the owner program's side panel fits on laptops; “Interface scale” works across the whole owner program; Kyrgyz, English, Turkish and Uzbek no longer have Russian insertions.

---

# Türkçe

### Yenilikler

- **İadeler.** Kısmi ödemeden sonra «veresiye» fişin iadesi doğru tutarı verir: iadeden önce kasa «Verilecek (beklenen)» tutarı gösterir. Sunucu reddederse («iade, hak edilenden fazla»), para verilmiş sayılmaz. Aynı fişin tekrar iadesi hemen engellenir, iade nedeni sunucuya ulaşır ve sahip tarafından görülür, «veresiye» fişin değişimi tutar hatalarına karşı korunur.
- **Bağlantısız vardiya.** Bağlantısız açılan vardiya, bağlantı geri geldikten sonra kaybolmaz. 7 günden eski fişler «bugünün tarihiyle» işlenmez, sahibin kararını bekler.
- **Vardiya kapatma.** Gönderilmemiş para girişleri ve çıkışları yeniden gönderilir, olmazsa vardiya «notla» kapatılabilir. X raporu «Kasa:» satırıyla uyuşur.
- **Ödeme.** Kasa sunucu yanıtını en çok 5 saniye bekler ve dürüstçe «Sunucu yavaş yanıt veriyor» yazar. Fişte 30 ürün varken «Öde» saniyenin kesrinde açılır.
- **Hız.** Büyük veri tabanlarında fişler ve raporlar satış dizini sayesinde daha hızlı açılır. Sahip programı daha az bellek kullanır, «Bonuslar», «Borçlar» ve raporlar daha hızlı çalışır.
- **Sunucu arızası.** «Satış yok» yerine kasa «NurCRM sunucusu kullanılamıyor» yazar.
- **Ürünler ve depo.** Ürün kartını düzenlemek stoku ve fiyatları silmez, çift ürüne karşı koruma vardır, transferler ve mal girişleri bağlantı kopsa da güvendedir, düzeltilmiş giriş dosyasını yeniden yüklemek girişi ikiye katlamaz.
- **Çekmecedeki para ve giriş.** Maaş ödemesi çekmecedeki bakiyeyi aşamaz; miktar veya «Alınan» alanına düşen barkod sayı sayılmaz; kasa ayarları elektrik kesilince bozulmaz.
- **İki yazıcı.** Fişler bir yazıcıda, etiketler ve barkodlar diğerinde basılır (modeller farklı olabilir). Barkodlar doğrudan seçilen etiket yazıcısına gider.
- **Ağ üzerinden yazdırma.** Yazıcı ağ üzerinden bağlanabilir (LAN, 9100 portu).
- **Toplu barkod yazdırma.** A4 sayfalar (11 hazır format, istenen hücreden başlanabilir), «Kopya = stok», taramadan doğrudan yazdırmaya, barkod uyarıları, tek düğmeyle etiket; ayrı «Barkod yazdırma» yetkisi.
- **Tasarım.** Düğmeler daha büyük, yazı tipi en az 12, daha iyi kontrast; sahip programının yan paneli dizüstü bilgisayarlara sığar; «Arayüz ölçeği» sahip programının tamamında çalışır; Kırgızca, İngilizce, Türkçe ve Özbekçede artık Rusça ifade yok.

---

# O'zbekcha

### Yangiliklar

- **Qaytarishlar.** Qisman to'lovdan keyin «nasiya» chekni qaytarish to'g'ri summani beradi: qaytarishdan oldin kassa «Berish (kutilmoqda)» ni ko'rsatadi. Server rad etsa («qaytarish tegishlidan ortiq»), pul berilgan deb hisoblanmaydi. Bir xil chekni qayta qaytarish darhol bloklanadi, qaytarish sababi serverga yetadi va egasi ko'radi, «nasiya» chekni almashtirish summa xatolaridan himoyalangan.
- **Aloqasiz smena.** Aloqasiz ochilgan smena aloqa qaytgandan keyin yo'qolmaydi. 7 kundan eski cheklar «bugungi sana bilan» o'tkazilmaydi, egasining qarorini kutadi.
- **Smenani yopish.** Yuborilmagan kiritishlar va chiqarishlar qayta yuboriladi, bo'lmasa smenani «izoh bilan» yopish mumkin. X-hisobot «Kassa:» qatori bilan mos keladi.
- **To'lov.** Kassa server javobini 5 soniyagacha kutadi va ochiq «Server sekin javob bermoqda» deb yozadi. Chekda 30 ta mahsulot bo'lganda «To'lash» soniyaning bir qismida ochiladi.
- **Tezlik.** Katta bazalarda cheklar va hisobotlar sotuvlar indeksi tufayli tezroq ochiladi. Egasi dasturi xotirani kamroq egallaydi, «Bonuslar», «Qarzlar» va hisobotlar tezroq ishlaydi.
- **Server nosozligi.** «Sotuv yo'q» o'rniga kassa «NurCRM serveri mavjud emas» deb yozadi.
- **Mahsulotlar va ombor.** Mahsulot kartasini tahrirlash qoldiq va narxlarni o'chirmaydi, mahsulot takrorlanishidan himoya bor, ko'chirishlar va kirimlar aloqa uzilsa ham saqlanadi, tuzatilgan kirim faylini qayta yuklash kirimni ikki barobar qilmaydi.
- **Tortmadagi pul va kiritish.** Ish haqi to'lovi tortmadagi qoldiqdan oshmaydi; miqdor yoki «Qabul qilindi» maydoniga tushgan shtrix-kod son deb qabul qilinmaydi; kassa sozlamalari elektr o'chganda buzilmaydi.
- **Ikkita printer.** Cheklar bitta printerda, yorliqlar va shtrix-kodlar boshqasida chop etiladi (modellari har xil bo'lishi mumkin). Shtrix-kodlar to'g'ridan-to'g'ri tanlangan yorliq printeriga ketadi.
- **Tarmoq orqali chop etish.** Printerni tarmoq orqali ulash mumkin (LAN, 9100 port).
- **Shtrix-kodlarni ommaviy chop etish.** A4 varaqlar (11 ta tayyor format, kerakli katakdan boshlash mumkin), «Nusxalar = qoldiq», skanerlashdan to'g'ridan-to'g'ri chop etishga, shtrix-kod bo'yicha ogohlantirishlar, bitta tugma bilan yorliq; alohida «Shtrix-kod chop etish» huquqi.
- **Dizayn.** Tugmalar kattaroq, shriftlar 12 dan kam emas, kontrast yaxshiroq; egasi dasturining yon paneli noutbuklarga sig'adi; «Interfeys masshtabi» egasi dasturining hammasida ishlaydi; qirg'izcha, inglizcha, turkcha va o'zbekchada ruscha qo'shimchalar yo'q.
