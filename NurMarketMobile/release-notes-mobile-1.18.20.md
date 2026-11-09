## NurCRM Mobile 1.18.20

Файл: `nurmarket-mobile-1.18.20.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется.

### Русский

**Что нового по сравнению с 1.18.19**
- **Повторный скан того же кода останавливает камеру и спрашивает количество.** Если товар долго смотрит в камеру, он больше не добавляется строка за строкой: при повторном считывании камера останавливается и появляется окно «Сколько штук?». Работает в «Продаже», «Приёмке», коммерческом предложении, сборке заказа и в режиме «Сканер для кассы». Убрали товар из кадра — следующий скан обычный.
- **Приёмка: литры и килограммы остаются литрами и килограммами.** Остаток считается в единице карточки: было 3 л, пришло 10 л — станет 13 л (раньше литры вели как штуки). Дробное количество из накладной («2,5 кг», «10,5 л») больше не округляется до целого; умная камера читает единицу измерения; у нового товара единицу можно выбрать (по умолчанию «шт»).
- **Кнопка WA в «Долгах».** У каждого должника снова есть кнопка WA: открывается чат WhatsApp с готовым напоминанием (имя, сумма долга, магазин) на языке приложения. Сообщение отправляет сам человек.
- **Нижняя панель сразу откликается на касание.** Волна нажатия теперь видна над фоном панели (раньше пряталась под ним), а вместо «искр» Material 3 — обычная рябь без рывка на первом касании. Жалоба «сенсор откликается плохо».

### Кыргызча

**1.18.19 менен салыштырганда эмне жаңы**
- **Бир эле кодду кайра сканерлегенде камера токтоп, санын сурайт.** Товар камерага көпкө турса, ал эми сап сайын кошулбайт: кайра окулганда камера токтоп, «Канча даана?» терезеси чыгат. «Сатуу», «Кабыл алуу», коммерциялык сунуш, заказды чогултуу жана «Кассага сканер» режиминде иштейт. Товарды кадрдан алсаңыз — кийинки скан адаттагыдай.
- **Кабыл алуу: литр жана килограмм литр жана килограмм бойдон калат.** Калдык карточканын бирдигинде эсептелет: 3 л болчу, 10 л келди — 13 л болот (мурун литр даана катары жүрчү). Накладнойдогу бөлчөк сан («2,5 кг», «10,5 л») бүтүнгө тегеректелбейт; акылдуу камера өлчөө бирдигин окуйт; жаңы товарга бирдикти тандаса болот (демейде «даана»).
- **«Карыздар» бөлүмүндө WA баскычы.** Ар бир карызкорго кайра WA баскычы бар: WhatsApp чаты даяр эскертме менен (аты, карыз суммасы, дүкөн) колдонмонун тилинде ачылат. Билдирүүнү адам өзү жөнөтөт.
- **Ылдыйкы панель тийгенде дароо жооп берет.** Басуу толкуну эми панелдин фонунун үстүндө көрүнөт (мурун анын астында калчу), Material 3 «учкундарынын» ордуна биринчи тийгенде кармалбаган кадимки толкун. «Сенсор начар иштейт» даттануусу.

### English

**What's new since 1.18.19**
- **Scanning the same code again stops the camera and asks for the quantity.** If a product stays in front of the camera, it is no longer added line after line: on the repeated read the camera stops and a “How many?” window appears. Works in Sale, Receiving, Quote, order picking and in the “Scanner for the till” mode. Take the product out of the frame — the next scan is a normal one.
- **Receiving: liters and kilograms stay liters and kilograms.** The stock is counted in the card's unit: 3 l before, 10 l arrived — becomes 13 l (liters used to be treated as pieces). A fractional quantity from the invoice (“2.5 kg”, “10.5 l”) is no longer rounded to a whole number; the smart camera reads the unit; a new product's unit can be chosen (“pcs” by default).
- **WA button in “Debts”.** Each debtor has the WA button again: it opens a WhatsApp chat with a ready reminder (name, debt amount, shop) in the app language. The person sends the message themselves.
- **The bottom bar responds to a touch at once.** The press ripple is now drawn above the bar background (it used to hide under it), and instead of Material 3 “sparkles” there is a plain ripple with no jank on the first touch. Fixes the complaint “the touch responds badly”.

### Türkçe

**1.18.19'a göre yenilikler**
- **Aynı kodu tekrar okutmak kamerayı durdurur ve miktarı sorar.** Ürün kameranın önünde uzun kalırsa artık satır satır eklenmez: tekrar okumada kamera durur ve «Kaç adet?» penceresi çıkar. Satış, Mal kabul, Teklif, sipariş toplama ve «Kasa için tarayıcı» modunda çalışır. Ürünü kadrajdan çıkarırsanız sonraki okutma normaldir.
- **Mal kabul: litre ve kilogram litre ve kilogram olarak kalır.** Stok kartın biriminde sayılır: 3 l vardı, 10 l geldi — 13 l olur (eskiden litre adet sayılırdı). Faturadaki kesirli miktar («2,5 kg», «10,5 l») artık tam sayıya yuvarlanmaz; akıllı kamera birimi okur; yeni ürünün birimi seçilebilir (varsayılan «adet»).
- **«Borçlar»da WA düğmesi.** Her borçlunun yine WA düğmesi var: uygulama dilinde hazır hatırlatmayla (ad, borç tutarı, mağaza) WhatsApp sohbeti açılır. Mesajı kişi kendisi gönderir.
- **Alt çubuk dokunuşa hemen yanıt verir.** Basma dalgası artık çubuğun arka planının üstünde görünür (eskiden altında kalırdı); Material 3 «kıvılcımları» yerine ilk dokunuşta takılma yapmayan düz dalga. «Dokunmatik kötü yanıt veriyor» şikâyeti.

### O'zbekcha

**1.18.19 dan keyingi yangiliklar**
- **Bir xil kodni qayta skanerlash kamerani to'xtatib, miqdorni so'raydi.** Tovar kamera oldida uzoq tursa, endi qatorma-qator qo'shilmaydi: qayta o'qishda kamera to'xtab, «Nechta?» oynasi chiqadi. Sotuv, Qabul, Taklif, buyurtma yig'ish va «Kassa uchun skaner» rejimida ishlaydi. Tovarni kadrdan olib qo'ysangiz — keyingi skan odatdagidek.
- **Qabul: litr va kilogramm litr va kilogramm bo'lib qoladi.** Qoldiq kartochka birligida hisoblanadi: 3 l edi, 10 l keldi — 13 l bo'ladi (avval litr dona sifatida yurardi). Nakladnoydagi kasrli miqdor («2,5 kg», «10,5 l») butunga yaxlitlanmaydi; aqlli kamera birlikni o'qiydi; yangi tovarga birlikni tanlash mumkin (odatda «dona»).
- **«Qarzlar»da WA tugmasi.** Har bir qarzdorda yana WA tugmasi bor: ilova tilida tayyor eslatma (ism, qarz summasi, do'kon) bilan WhatsApp chati ochiladi. Xabarni odamning o'zi yuboradi.
- **Pastki panel teginishga darrov javob beradi.** Bosish to'lqini endi panel fonining ustida ko'rinadi (avval uning ostida qolardi), Material 3 «uchqunlari» o'rniga birinchi teginishda qotmaydigan oddiy to'lqin. «Sensor yomon javob beradi» shikoyati.
