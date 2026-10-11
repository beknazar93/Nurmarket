## v1.17.58

Денежный ящик открывается после каждой продажи, накладная на нескольких фото с редактором, удобнее приёмка, ревизия и окно товара.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Денежный ящик и чек.** Ящик открывается после печати чека, при каждой продаже наличными. Если принтер не печатает, ящик всё равно откроется — не позже чем через 20 секунд. Чек больше не обрывается на середине.
- **Накладная на нескольких фото.** В Нур Советнике 📎 принимает до 10 фото или PDF сразу (до 15 МБ каждый) — это страницы одной накладной, советник читает их вместе. «＋ Ещё страница» добавляет лист, ✕ убирает.
- **«✎ Отредактировать» в карточке прихода.** Таблица по каждой строке накладной:
  - название с накладной;
  - название в чеке (пусто — остаётся название с накладной);
  - количество, закупка и продажа.

  «Сохранить» обновляет карточку, дальше — «Выполнить» как обычно.
- **Склад — приёмка:**
  - после выбора поставщика — его товары с последней закупкой и количеством, нажатие добавляет строку;
  - ✕ в строке убирает товар, «Очистить список» — все строки;
  - поле «Принято» можно стереть и ввести заново;
  - название товара видно целиком;
  - подсказки закрываются по Esc, ✕ или щелчку мимо.
- **Склад — ревизия:** «Очистить список» и ✕ в каждой строке.
- **Окно товара — вкладки по порядку:**
  - «Основное» — с горячей клавишей и оптовой ценой;
  - «Доп. штрих-коды» — с артикулом, штрихкод и название можно исправить кнопкой ✎ (✓ — сохранить);
  - «Упаковка» — с полем «Минимальный остаток», оно хранится на сервере NurCRM;
  - «Описание» — с категорией и брендом.

  Весовой товар можно отметить в любой сфере магазина.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Денежный ящик | Открывался один раз, потом нет | Открывается после печати чека, при каждой продаже наличными |
| Чек при открытии ящика | Иногда обрывался на середине | Печатается целиком |
| Накладная на 2–3 листах | Одно фото за раз | До 10 фото или PDF одной накладной |
| Ошибка в названии или цене из накладной | Только «Отмена» и заново | «✎ Отредактировать» — таблица с правкой |
| Приёмка: лишняя строка | Убрать нельзя | ✕ в строке или «Очистить список» |
| Поле «Принято» | Не стиралось (всегда 0) | Стирается, можно ввести заново |
| Список подсказок в приёмке | Висел поверх таблицы | Закрывается по Esc, ✕ или щелчку мимо |
| «Обновить» на складе | Без признаков работы | Значок крутится, «Загружаю товары… N с» |
| Закрытие смены с пустой суммой | Закрывалось | Нужна фактическая сумма наличных (0, если их нет) |
| Двойное нажатие «Вернуть» | Могло оформить возврат дважды | Второе нажатие не срабатывает |
| Скан нового штрихкода при обрыве связи | Предлагало создать товар (мог появиться дубль) | Просит проверить связь и отсканировать снова |
| «Создать перемещение» и «Закрыть» без товаров | В списке оставался пустой черновик | Пустой черновик удаляется |

### Проверено (10–11.10, на этом ПК)

- **Приёмка:** у поставщика «Абдилазиз» показаны 2 его товара с ценами. ✕ в строке — «Позиций: 0», «Очистить список» и сброс на «Без поставщика» работают.
- **Ревизия:** «Очистить список» работает.
- **Окно товара:** открыты все четыре вкладки, штрихкод исправлен через ✎ и ✓.
- **Нур Советник:** накладная из двух страниц ушла одним вопросом. «✎ Отредактировать» → «Лейс сметана 90г», 6 шт, 660 сом → «Сохранить» → карточка обновилась.
- **Касса:** после обновления запустилась, каталог загрузился (306 товаров).

Не проверено на этом ПК: денежный ящик (на этом ПК его нет). Если ящик не открывается: в настройках печати, блок «Денежный ящик», включите «Открывать при оплате наличными» и выберите «Контакт разъёма» 2 или 5. Журнал кассы пишет причину: «выключен в настройках» или «оплата без наличных».

---

# Кыргызча

### Жаңы

- **Акча кутусу жана чек.** Кути чек басылгандан кийин ачылат — накталай ар бир сатууда. Принтер баспаса да кути 20 секунддан кечиктирбей ачылат. Чек ортосунан үзүлбөйт.
- **Бир нече сүрөттөгү накладной.** Нур Кеңешчиде 📎 бир эле учурда 10 сүрөт же PDFке чейин кабыл алат — бир накладнойдун барактары, кеңешчи аларды бирге окуйт.
- **Кирим карточкасындагы «✎ Оңдоо»** — таблица: накладнойдогу аталыш, чектеги аталыш, саны, сатып алуу жана сатуу баасы.
- **Кампа — кабыл алуу:** жеткирүүчүнүн товарлары акыркы баасы менен; саптагы ✕ жана «Тизмени тазалоо»; «Кабыл алынды» талаасын өчүрсө болот; сунуштар Esc, ✕ же сыртка басуу менен жабылат.
- **Кампа — ревизия:** «Тизмени тазалоо» жана ар бир саптагы ✕.
- **Товардын терезеси:**
  - «Негизги» — ысык баскыч жана дүң баа менен;
  - «Кошумча штрихкоддор» — артикул менен, ✎ менен оңдоо;
  - «Таңгак» — «Минималдуу калдык» менен (NurCRM серверинде сакталат);
  - «Сүрөттөмө» — категория жана бренд менен.

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| Акча кутусу | Бир жолу ачылып, андан кийин ачылчу эмес | Чек басылгандан кийин, накталай ар бир сатууда ачылат |
| Чек | Кээде ортосунан үзүлчү | Толук басылат |
| 2–3 барактуу накладной | Бир учурда бир сүрөт | Бир накладнойдун 10 сүрөт же PDFке чейин |
| Кабыл алууда ашыкча сап | Өчүрүүгө болбойт | Саптагы ✕ же «Тизмени тазалоо» |
| Сменаны бош сумма менен жабуу | Жабылчу | Нак акчанын чыныгы суммасы керек (жок болсо 0) |
| «Кайтаруу» эки жолу басылса | Кайтаруу эки жолу болушу мүмкүн эле | Экинчи басуу иштебейт |

### Текшерилди (10–11.10, ушул компьютерде)

- Кабыл алуу: «Абдилазиз» жеткирүүчүсүнүн 2 товары; ✕ жана «Тизмени тазалоо» иштейт.
- Товардын терезеси: төрт өтмөк тең, штрихкод ✎ менен оңдолду.
- Нур Кеңешчи: эки барактуу накладной бир суроо менен кетти; «✎ Оңдоо» → сактоо → карточка жаңырды.

Текшерилген жок: акча кутусу (бул компьютерде жок). Ачылбаса: басуу жөндөөлөрүндөгү «Акча кутусу» блогунда кутуну күйгүзүп, 2 же 5 контактты тандаңыз.

---

# English

### New

- **Cash drawer and receipt.** The drawer opens after the receipt is printed, on every cash sale. If the printer does not print, the drawer still opens within 20 seconds. The receipt is no longer cut off in the middle.
- **An invoice on several photos.** In Nur Advisor, 📎 takes up to 10 photos or PDFs at once — pages of one invoice, read together.
- **“✎ Edit” on the receiving card** — a table: name on the invoice, name on the receipt, quantity, purchase and selling price.
- **Warehouse — receiving:** the chosen supplier's products with the last price; ✕ on a row and “Clear list”; the “Received” field can be erased; suggestions close with Esc, ✕ or a click outside.
- **Warehouse — stocktake:** “Clear list” and ✕ on every row.
- **Product window:**
  - “Main” — with hotkey and wholesale price;
  - “Extra barcodes” — with article, edit with ✎;
  - “Packaging” — with “Minimum stock” (kept on the NurCRM server);
  - “Description” — with category and brand.

### Before → now

| What | Before | Now |
|---|---|---|
| Cash drawer | Opened once, then no more | Opens after the receipt is printed, on every cash sale |
| Receipt | Sometimes cut off in the middle | Printed in full |
| Invoice on 2–3 pages | One photo at a time | Up to 10 photos or PDFs of one invoice |
| An extra row in receiving | Could not be removed | ✕ on the row or “Clear list” |
| Closing a shift with an empty amount | It closed | The counted cash is required (0 if none) |
| Double tap on “Return” | Could make the return twice | The second tap does nothing |

### Checked (10–11.10, on this PC)

- Receiving: 2 products of the supplier “Абдилазиз”; ✕ and “Clear list” work.
- Product window: all four tabs, a barcode corrected with ✎.
- Nur Advisor: a two-page invoice sent as one question; “✎ Edit” → save → the card updated.

Not checked: the cash drawer (there is none on this PC). If it does not open: in the print settings, “Cash drawer” block, turn on opening on cash payment and choose pin 2 or 5.

---

# Türkçe

### Yeni

- **Para çekmecesi ve fiş.** Çekmece fiş basıldıktan sonra, her nakit satışta açılır. Yazıcı basmasa da çekmece en geç 20 saniyede açılır. Fiş artık ortasında kesilmez.
- **Birkaç fotoğraflık fatura.** Nur Danışman'da 📎 aynı anda 10 fotoğraf veya PDF'e kadar alır — tek faturanın sayfaları, birlikte okunur.
- **Mal kabul kartında «✎ Düzenle»** — tablo: faturadaki ad, fişteki ad, miktar, alış ve satış fiyatı.
- **Depo — mal kabul:** seçilen tedarikçinin ürünleri son fiyatıyla; satırda ✕ ve «Listeyi temizle»; «Kabul edilen» alanı silinebilir; öneriler Esc, ✕ veya dışarı tıklayınca kapanır.
- **Depo — sayım:** «Listeyi temizle» ve her satırda ✕.
- **Ürün penceresi:**
  - «Ana» — kısayol tuşu ve toptan fiyatla;
  - «Ek barkodlar» — stok koduyla, ✎ ile düzeltme;
  - «Ambalaj» — «Minimum stok» ile (NurCRM sunucusunda saklanır);
  - «Açıklama» — kategori ve markayla.

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Para çekmecesi | Bir kez açılıyor, sonra açılmıyordu | Fiş basıldıktan sonra, her nakit satışta açılır |
| Fiş | Bazen ortasında kesiliyordu | Tam basılır |
| 2–3 sayfalık fatura | Bir seferde bir fotoğraf | Tek faturanın 10 fotoğraf veya PDF'ine kadar |
| Mal kabulde fazla satır | Silinemiyordu | Satırda ✕ veya «Listeyi temizle» |
| Boş tutarla vardiya kapatma | Kapanıyordu | Sayılan nakit gerekir (yoksa 0) |
| «İade»ye çift dokunma | İade iki kez yapılabilirdi | İkinci dokunuş çalışmaz |

### Kontrol edildi (10–11.10, bu bilgisayarda)

- Mal kabul: «Абдилазиз» tedarikçisinin 2 ürünü; ✕ ve «Listeyi temizle» çalışıyor.
- Ürün penceresi: dört sekmenin hepsi, barkod ✎ ile düzeltildi.
- Nur Danışman: iki sayfalık fatura tek soruyla gitti; «✎ Düzenle» → kaydet → kart güncellendi.

Kontrol edilmedi: para çekmecesi (bu bilgisayarda yok). Açılmazsa: yazdırma ayarlarındaki «Para çekmecesi» bölümünde nakit ödemede açmayı etkinleştirin ve 2 ya da 5 numaralı pini seçin.

---

# O'zbekcha

### Yangi

- **Pul qutisi va chek.** Quti chek bosilgandan keyin, har bir naqd sotuvda ochiladi. Printer bosmasa ham quti 20 soniyadan kechikmay ochiladi. Chek endi o'rtasida uzilmaydi.
- **Bir nechta rasmdagi nakladnoy.** Nur Maslahatchida 📎 bir vaqtda 10 tagacha rasm yoki PDF qabul qiladi — bitta nakladnoyning sahifalari, birga o'qiladi.
- **Kirim kartasida «✎ Tahrirlash»** — jadval: nakladnoydagi nom, chekdagi nom, miqdor, xarid va sotuv narxi.
- **Ombor — qabul qilish:** tanlangan yetkazib beruvchining mahsulotlari oxirgi narxi bilan; qatordagi ✕ va «Ro'yxatni tozalash»; «Qabul qilindi» maydonini o'chirish mumkin; takliflar Esc, ✕ yoki tashqariga bosish bilan yopiladi.
- **Ombor — reviziya:** «Ro'yxatni tozalash» va har bir qatorda ✕.
- **Mahsulot oynasi:**
  - «Asosiy» — tezkor tugma va ulgurji narx bilan;
  - «Qo'shimcha shtrix-kodlar» — artikul bilan, ✎ bilan tuzatish;
  - «Qadoq» — «Minimal qoldiq» bilan (NurCRM serverida saqlanadi);
  - «Tavsif» — toifa va brend bilan.

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Pul qutisi | Bir marta ochilib, keyin ochilmasdi | Chek bosilgandan keyin, har bir naqd sotuvda ochiladi |
| Chek | Ba'zan o'rtasida uzilardi | To'liq bosiladi |
| 2–3 sahifali nakladnoy | Bir vaqtda bitta rasm | Bitta nakladnoyning 10 tagacha rasmi yoki PDF |
| Qabulda ortiqcha qator | O'chirib bo'lmasdi | Qatordagi ✕ yoki «Ro'yxatni tozalash» |
| Smenani bo'sh summa bilan yopish | Yopilardi | Sanalgan naqd pul kerak (bo'lmasa 0) |
| «Qaytarish»ni ikki marta bosish | Qaytarish ikki marta bo'lishi mumkin edi | Ikkinchi bosish ishlamaydi |

### Tekshirildi (10–11.10, shu kompyuterda)

- Qabul: «Абдилазиз» yetkazib beruvchisining 2 mahsuloti; ✕ va «Ro'yxatni tozalash» ishlaydi.
- Mahsulot oynasi: to'rtta yorliqning hammasi, shtrix-kod ✎ bilan tuzatildi.
- Nur Maslahatchi: ikki sahifali nakladnoy bitta savol bilan ketdi; «✎ Tahrirlash» → saqlash → karta yangilandi.

Tekshirilmadi: pul qutisi (bu kompyuterda yo'q). Ochilmasa: chop etish sozlamalaridagi «Pul qutisi» blokida naqd to'lovda ochishni yoqing va 2 yoki 5-kontaktni tanlang.
