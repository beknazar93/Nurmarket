## v1.17.61 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Ревизия и приёмка на складе, оприходование в фоне, накладная Нур Советника по штрихкодам, генератор штрихкода, группа заказов Telegram, редактор этикеток (печать только штрихкода, названия или цены).

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Склад → «Ревизия».** Пересчёт сканером (скан → факт), акты, выгрузка в Excel, CSV и PDF.
- **Склад → «Приёмка».** Скан → строка сверху, наценка и подорожание (▲), кнопка «＋ поставщик», подтверждение изменения цен продажи.
- **Оприходование в фоне.** Окно не ждёт сервер: при плохом интернете «операция выполняется в фоне», новое окно «Приходы в фоне», повторы без дублей, предупреждение при выходе.
- **Нур Советник.** Накладная по штрихкодам; кнопка «Сгенерировать» штрихкод; ввод в полях больше не теряет символы.
- **Генератор штрихкода** в карточке товара и закупке: код не начинается с 0 и 2 (2 — весовые) и не повторяет коды каталога.
- **Telegram-бот.** «Группа заказов»: заказы приходят владельцу, в группу или в оба места.
- **Редактор этикеток.** Можно печатать только штрихкод, только название или только цену; размер текста не ограничен.
- **Массовое добавление.** Подача прихода частями в фоне, чтение Excel и CSV (в том числе Windows-1251) — основа для нового экрана с тремя колонками.
- **Исправления.** Скан в открытой карточке товара не срабатывает в окне «Склад»; поле «Штрихкод» в карточке шире; из окна «Приходы в фоне» можно вернуться в программу.

### Ещё не проверено вживую / известные ограничения

- Приёмка этой версии не закончена: на тестовом ПК мешала зажатая клавиша Enter, живая проверка ревизии, приёмки с наценкой, закупок и Telegram-группы не проведена. Поэтому версия тестовая.
- Деньги и остатки при оприходовании проверены на стендах с подменным сервером; на боевом тестовом аккаунте — частично.
- Новое окно массового добавления (три колонки, Excel/CSV) и мастер импорта ещё не включены в эту версию.
- Сервер NurCRM создаёт товары по одному: 15 000 новых товаров загружаются в фоне часами.
- Тексты на английском, турецком и узбекском переведены без носителя языка.

---

# Кыргызча

**Тесттик версия — тестировщиктер үчүн гана.**

- **Кампа → «Ревизия».** Сканер менен кайра эсептөө (скан → факт), актылар, Excel, CSV жана PDFке чыгаруу.
- **Кампа → «Кабыл алуу».** Скан → үстүндө сап, кошумча баа жана кымбаттоо (▲), «＋ жеткирүүчү» баскычы, сатуу баасы өзгөргөнүн ырастоо.
- **Фондо кирешелөө.** Терезе серверди күтпөйт: интернет начар болсо «операция фондо аткарылууда», «Фондогу кирешелер» жаңы терезеси, кайталоолор дубликатсыз.
- **Нур Кеңешчи.** Штрихкод боюнча накладная; штрихкодду «Түзүү» баскычы; талааларга киргизүү символдорду жоготпойт.
- **Штрихкод генератору**: код 0 жана 2 менен башталбайт, каталогдогу коддорду кайталабайт.
- **Telegram-бот.** «Буйрутмалар тобу».
- **Этикетка редактору.** Штрихкодду гана, атын гана же баасын гана басууга болот; текст өлчөмү чектелбейт.
- **Жапырт кошуу.** Фондо кирешени бөлүп берүү, Excel жана CSV окуу (Windows-1251 да).
- **Оңдоолор.** Ачык карточкадагы скан «Кампа» терезесинде иштебейт; «Штрихкод» талаасы кеңирээк; «Фондогу кирешелерден» программага кайтса болот.

**Чектөөлөр.** Кабыл алуу бүтө элек, тирүү текшерүү толук эмес — ошондуктан тесттик версия. Жапырт кошуунун жаңы экраны (үч тилке, Excel/CSV) бул версияга кирген жок.

---

# English

**Test version — for testers only.**

- **Stock → “Revision”.** Counting with a scanner (scan → actual), acts, export to Excel, CSV and PDF.
- **Stock → “Receiving”.** Scan → a row on top, markup and price rise (▲), the “＋ supplier” button, confirmation of sale price changes.
- **Receiving in the background.** The window does not wait for the server: with bad internet “the operation runs in the background”, a new “Receipts in the background” window, retries without duplicates.
- **Nur Advisor.** An invoice by barcodes; a “Generate” barcode button; typing in fields no longer loses characters.
- **Barcode generator**: the code never starts with 0 or 2 and never repeats catalog codes.
- **Telegram bot.** “Orders group”.
- **Label editor.** You can print only the barcode, only the name or only the price; the text size is not limited.
- **Bulk adding.** Receipts are fed in parts in the background; Excel and CSV reading (including Windows-1251).
- **Fixes.** A scan in an open product card no longer triggers in the “Stock” window; the “Barcode” field is wider; you can return to the program from “Receipts in the background”.

**Limitations.** Acceptance of this version is not finished and the live check is incomplete — that is why it is a test build. The new bulk-adding screen (three columns, Excel/CSV) is not in this version yet.

---

# Türkçe

**Test sürümü — yalnızca test kullanıcıları için.**

- **Depo → «Sayım».** Tarayıcıyla sayım (tarama → fiili), tutanaklar, Excel, CSV ve PDF dışa aktarma.
- **Depo → «Mal kabul».** Tarama → üstte satır, kâr oranı ve zam (▲), «＋ tedarikçi» düğmesi, satış fiyatı değişiminin onayı.
- **Arka planda mal girişi.** Pencere sunucuyu beklemez: kötü internette «işlem arka planda yürütülüyor», yeni «Arka plandaki girişler» penceresi, tekrarlar çift kayıtsız.
- **Nur Danışman.** Barkodlarla fatura; «Oluştur» barkod düğmesi; alanlara yazarken karakterler kaybolmaz.
- **Barkod üretici**: kod 0 ve 2 ile başlamaz, katalog kodlarını tekrarlamaz.
- **Telegram botu.** «Sipariş grubu».
- **Etiket düzenleyici.** Yalnızca barkod, yalnızca ad veya yalnızca fiyat yazdırılabilir; yazı boyutu sınırsız.
- **Toplu ekleme.** Girişler arka planda parçalar halinde verilir; Excel ve CSV okuma (Windows-1251 dahil).
- **Düzeltmeler.** Açık ürün kartındaki tarama «Depo» penceresinde çalışmaz; «Barkod» alanı daha geniş; «Arka plandaki girişler»den programa dönülebilir.

**Sınırlamalar.** Bu sürümün kabulü bitmedi, canlı kontrol eksik — bu yüzden test sürümü. Yeni toplu ekleme ekranı (üç sütun, Excel/CSV) bu sürümde henüz yok.

---

# O'zbekcha

**Test versiyasi — faqat testerlar uchun.**

- **Ombor → «Taftish».** Skaner bilan qayta hisoblash (skan → haqiqiy), dalolatnomalar, Excel, CSV va PDFga chiqarish.
- **Ombor → «Qabul».** Skan → tepada qator, ustama va qimmatlash (▲), «＋ yetkazib beruvchi» tugmasi, sotuv narxi o'zgarishini tasdiqlash.
- **Fonda kirim.** Oyna serverni kutmaydi: yomon internetda «amal fonda bajarilmoqda», yangi «Fondagi kirimlar» oynasi, takrorlar dublikatsiz.
- **Nur Maslahatchi.** Shtrix-kodlar bo'yicha hisob-faktura; «Yaratish» tugmasi; maydonlarga yozishda belgilar yo'qolmaydi.
- **Shtrix-kod generatori**: kod 0 va 2 bilan boshlanmaydi, katalogdagi kodlarni takrorlamaydi.
- **Telegram-bot.** «Buyurtmalar guruhi».
- **Yorliq muharriri.** Faqat shtrix-kodni, faqat nomni yoki faqat narxni chop etish mumkin; matn o'lchami cheklanmagan.
- **Ommaviy qo'shish.** Kirim fonda qismlarga bo'lib beriladi; Excel va CSV o'qish (Windows-1251 ham).
- **Tuzatishlar.** Ochiq kartadagi skan «Ombor» oynasida ishlamaydi; «Shtrix-kod» maydoni kengroq; «Fondagi kirimlar»dan dasturga qaytish mumkin.

**Cheklovlar.** Bu versiyani qabul qilish tugamagan, jonli tekshiruv to'liq emas — shuning uchun test versiyasi. Ommaviy qo'shishning yangi ekrani (uch ustun, Excel/CSV) bu versiyada hali yo'q.
