## v1.17.92

Продажа без остатка, защита от товара без цены, исправление весовых этикеток и страницы «Весы», единая «Приёмка» и «Ценники и штрихкоды», сенсорный экран, касса без «Сотрудников» и «ИИ» (они в программе владельца), исправления Rongta и надёжности, тарифы по серверным ценам. Всё из 1.17.91 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.92.tar.gz`; Android — `nurmarket-kassa-android-1.17.92.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Продажа без остатка.** Новая настройка «Продавать без остатка» (по умолчанию включена): товар продаётся, даже если на складе ноль; касса сообщает об этом серверу.
- **Товар без цены.** Если у товара нет цены, касса запрашивает её и ищет на сервере по названию; при загрузке каталога видно «Загружено N из M».
- **Весовые этикетки и страница «Весы».** Исправлено чтение весовых этикеток: теперь «вес», а не «сумма». На странице «Весы» есть кнопка «Сохранить» и результат сохранения, без ложного «несохранённые правки».
- **Приёмка и ценники.** Единая вкладка «Приёмка» карточками и вкладка «Ценники и штрихкоды»; точное количество в приёмке (раньше могло получиться 6,999996).
- **Сенсорный экран и вид.** Ползунки для сенсорного экрана, редактор чека, новая шкала отступов, фильтр каталога и «Отложенные»; меньше миганий и быстрее работа.
- **Касса и программа владельца.** «Сотрудники», «ИИ» и бот владельца убраны из кассы — они в программе владельца; в ней же Табель.
- **Печать и надёжность.** Исправлена Rongta (стирание ячеек); список чеков и очистка неотправленных; защита от двойного открытия окон; три правки против исчезающих и меняющихся товаров.
- **Тарифы, модули и Базар.** Тарифы и модули показываются по серверным ценам; торг на Базаре считается точнее.

# Кыргызча

### Эмнелер жаңы

- **Калдыксыз сатуу.** Жаңы жөндөө «Калдыксыз сатуу» (демейде күйгүзүлгөн): кампада нөл болсо да товар сатылат; касса муну серверге билдирет.
- **Баасы жок товар.** Товардын баасы жок болсо, касса аны сурайт жана серверден атынан издейт; каталог жүктөлгөндө «N ичинен M жүктөлдү» көрүнөт.
- **Салмактык этикеткалар жана «Таразалар» барагы.** Салмактык этикеткаларды окуу оңдолду: эми «салмак», «сумма» эмес. «Таразалар» барагында «Сактоо» баскычы жана сактоонун жыйынтыгы бар, жалган «сакталбаган өзгөртүүлөр» жок.
- **Кабыл алуу жана баа жапсырмалары.** Карточкалар түрүндөгү бирдиктүү «Кабыл алуу» өтмөгү жана «Баа жапсырмалары жана штрих-коддор» өтмөгү; кабыл алууда так сан (мурун 6,999996 болуп калчу).
- **Сенсордук экран жана көрүнүш.** Сенсордук экран үчүн жылгычтар, чек редактору, жаңы боштук шкаласы, каталог чыпкасы жана «Кийинкиге калтырылгандар»; жымыңдоо азайды, ылдамыраак иштейт.
- **Касса жана ээсинин программасы.** «Кызматкерлер», «ИИ» жана ээсинин боту кассадан алынды — алар ээсинин программасында; Табель да ошол жерде.
- **Басып чыгаруу жана ишенимдүүлүк.** Rongta оңдолду (уячалар өчүп калчу); чектердин тизмеси жана жөнөтүлбөгөндөрдү тазалоо; терезени эки жолу ачуудан коргоо; товарлардын жоголушуна жана өзгөрүшүнө каршы үч оңдоо.
- **Тарифтер, модулдар жана Базар.** Тарифтер жана модулдар сервердеги баалар боюнча көрсөтүлөт; Базардагы соодалашуу так эсептелет.

# English

### What's new

- **Selling without stock.** New setting “Sell without stock” (on by default): a product can be sold even when the warehouse shows zero; the till tells the server about it.
- **Product without a price.** If a product has no price, the till asks for it and looks it up on the server by name; catalog loading shows “Loaded N of M”.
- **Scale labels and the Scales page.** Reading of weight labels is fixed: it is now “weight”, not “amount”. The Scales page has a Save button and a save result, with no false “unsaved changes”.
- **Receiving and price tags.** One Receiving tab with cards and a “Price tags and barcodes” tab; exact quantity when receiving (it used to come out as 6.999996).
- **Touch screen and look.** Sliders for touch screens, a receipt editor, a new spacing scale, a catalog filter and “Parked”; fewer flickers and faster work.
- **Till and owner program.** “Employees”, “AI” and the owner bot are removed from the till — they live in the owner program, together with the Timesheet.
- **Printing and reliability.** Rongta is fixed (cells being erased); a receipt list and clearing of unsent receipts; protection from opening a window twice; three fixes against disappearing and changing products.
- **Plans, modules and Bazaar.** Plans and modules are shown at the server prices; bargaining on Bazaar is calculated more accurately.

# Türkçe

### Yenilikler

- **Stoksuz satış.** Yeni «Stoksuz sat» ayarı (varsayılan olarak açık): depoda sıfır olsa da ürün satılır; kasa bunu sunucuya bildirir.
- **Fiyatsız ürün.** Ürünün fiyatı yoksa kasa fiyatı ister ve sunucuda ada göre arar; katalog yüklenirken «M içinden N yüklendi» görünür.
- **Terazi etiketleri ve «Teraziler» sayfası.** Terazi etiketlerinin okunması düzeltildi: artık «ağırlık», «tutar» değil. «Teraziler» sayfasında «Kaydet» düğmesi ve kayıt sonucu var, yanlış «kaydedilmemiş değişiklik» uyarısı yok.
- **Mal kabulü ve fiyat etiketleri.** Kartlı tek «Mal kabulü» sekmesi ve «Fiyat etiketleri ve barkodlar» sekmesi; mal kabulünde tam miktar (eskiden 6,999996 çıkabiliyordu).
- **Dokunmatik ekran ve görünüm.** Dokunmatik ekran için kaydırıcılar, fiş düzenleyici, yeni boşluk ölçeği, katalog filtresi ve «Bekletilenler»; daha az titreme ve daha hızlı çalışma.
- **Kasa ve sahip programı.** «Çalışanlar», «Yapay zekâ» ve sahip botu kasadan çıkarıldı — sahip programındalar; Puantaj da orada.
- **Yazdırma ve güvenilirlik.** Rongta düzeltildi (hücrelerin silinmesi); fiş listesi ve gönderilmemişleri temizleme; pencerenin iki kez açılmasına karşı koruma; kaybolan ve değişen ürünlere karşı üç düzeltme.
- **Tarifeler, modüller ve Pazar.** Tarifeler ve modüller sunucu fiyatlarıyla gösterilir; Pazar'daki pazarlık daha doğru hesaplanır.

# O'zbekcha

### Yangiliklar

- **Qoldiqsiz sotuv.** Yangi «Qoldiqsiz sotish» sozlamasi (standart bo'yicha yoqilgan): omborda nol bo'lsa ham tovar sotiladi; kassa buni serverga bildiradi.
- **Narxsiz tovar.** Tovarning narxi bo'lmasa, kassa uni so'raydi va serverdan nomi bo'yicha qidiradi; katalog yuklanganda «M dan N yuklandi» ko'rinadi.
- **Tarozi etiketkalari va «Tarozilar» sahifasi.** Tarozi etiketkalarini o'qish tuzatildi: endi «vazn», «summa» emas. «Tarozilar» sahifasida «Saqlash» tugmasi va saqlash natijasi bor, yolg'on «saqlanmagan o'zgarishlar» yo'q.
- **Qabul qilish va narx yorliqlari.** Kartochkali yagona «Qabul qilish» yorlig'i va «Narx yorliqlari va shtrix-kodlar» yorlig'i; qabul qilishda aniq miqdor (ilgari 6,999996 chiqishi mumkin edi).
- **Sensorli ekran va ko'rinish.** Sensorli ekran uchun sirpanchiqlar, chek muharriri, yangi masofa shkalasi, katalog filtri va «Kechiktirilganlar»; miltillash kamaydi va tezroq ishlaydi.
- **Kassa va egasi dasturi.** «Xodimlar», «AI» va egasi boti kassadan olib tashlandi — ular egasi dasturida; Tabel ham o'sha yerda.
- **Chop etish va ishonchlilik.** Rongta tuzatildi (kataklar o'chib ketardi); cheklar ro'yxati va yuborilmaganlarni tozalash; oynani ikki marta ochishdan himoya; tovarlar yo'qolishi va o'zgarishiga qarshi uchta tuzatish.
- **Tariflar, modullar va Bozor.** Tariflar va modullar server narxlarida ko'rsatiladi; Bozordagi savdolashuv aniqroq hisoblanadi.
