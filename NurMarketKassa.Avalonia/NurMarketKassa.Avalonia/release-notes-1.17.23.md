## v1.17.23 — тестовая версия

---

# Русский

### Новое

- **Настройки весов Штрих-ПРИНТ прямо в кассе.** Настройки → Весы → «Сетевые весы» → «Настройки весов (клавиатура, этикетка, штрих-код)…». Семь вкладок, как в тест-драйвере Штрих-М: Клавиатура (клавиши быстрого доступа, функциональные клавиши, блокировка), Состояние (вес, принтер), Система (номер весов, режим печати, звук, дата, часы с компьютера), Товары, Печать и этикетка (формат, поля, контраст, пробная этикетка), Штрих-код, Тексты (название магазина, реклама). Сначала «Прочитать с весов», в весы записываются только изменённые поля.
- **Штрих-код при отправке на весы.** В окне «Весы» колонка «Код в ШК» — код можно поправить у каждого товара; рядом пример этикетки (например, 2000001003923 = префикс 20, товар 1, 0,392 кг). Касса предупреждает, если формат на весах не совпадёт с настройкой компании.
- **Виды кассы в Маркетплейсе** — отдельная вкладка «Виды кассы».
- **Экран покупателя по виду кассы** — второй экран меняется вместе с видом кассы; добавлен редактор экрана покупателя.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Пароль весов Штрих-ПРИНТ | Короткий пароль «30» уходил как «3000» — весы отвечали ошибкой пароля | Уходит «0030», как у драйвера Штрих-М |
| Код товара при прямой отправке на весы | В штрих-код попадал номер ячейки: у товара с PLU 5 на этикетке был код 1, и касса находила чужой товар | В штрих-код идёт PLU товара (или код товара при раскладке «по коду») |
| Экран покупателя | Один вид, не зависел от вида кассы | Шесть видов под вид кассы, свой редактор |

### Проверено

- Команды к весам сверены с драйвером Штрих-М на эмуляторе весов: 66 из 66 совпали байт в байт, записанное читается обратно.
- Штрих-код 2000001003923 собирается и разбирается кассой как «тест весовых 1», 0,392 кг; пример 2100100001452 «по коду» — как в документации сайта.
- Сборка установлена на рабочем компьютере, касса и программа владельца запускаются.

### Не проверено (поэтому версия тестовая)

- На настоящих весах Штрих-ПРИНТ — весов в сети не было.
- Полный прогон продаж, долга, возврата и смены на этой сборке.

---

# Кыргызча

### Жаңы

- **Штрих-ПРИНТ таразасынын жөндөөлөрү кассанын өзүндө.** Жөндөөлөр → Таразалар → «Тармактык таразалар» → «Таразанын жөндөөлөрү (баскычтар, этикетка, штрих-код)…». Штрих-М тест-драйверидегидей жети өтмөк: Баскычтар, Абал, Система, Товарлар, Басып чыгаруу жана этикетка, Штрих-код, Тексттер. Адегенде «Таразадан окуу», таразага өзгөртүлгөн талаалар гана жазылат.
- **Таразага жөнөтүүдө штрих-код.** «Таразалар» терезесинде «ШКдагы код» тилкеси — ар бир товардын кодун оңдоого болот; жанында этикетканын үлгүсү (мисалы, 2000001003923 = префикс 20, товар 1, 0,392 кг). Таразадагы формат компаниянын жөндөөсү менен дал келбесе, касса эскертет.
- **Кассанын көрүнүштөрү Маркетплейсте** — өзүнчө «Кассанын көрүнүштөрү» өтмөгү.
- **Сатып алуучунун экраны кассанын көрүнүшү боюнча** — экинчи экран кассанын көрүнүшү менен кошо өзгөрөт; редактору кошулду.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Штрих-ПРИНТ таразасынын сырсөзү | Кыска «30» сырсөзү «3000» болуп кетчү | «0030» болуп кетет, Штрих-М драйверидегидей |
| Таразага түз жөнөтүүдө товардын коду | Штрих-кодго уячанын номери кирчү, касса башка товарды табчу | Штрих-кодго товардын PLU'су (же «код боюнча» болсо товардын коду) кирет |
| Сатып алуучунун экраны | Бир гана көрүнүш | Кассанын көрүнүшүнө жараша алты көрүнүш, өз редактору |

### Текшерилди

- Таразага буйруктар эмулятордо Штрих-М драйвери менен салыштырылды: 66дан 66сы байтка чейин дал келди.
- 2000001003923 штрих-коду касса тарабынан «тест весовых 1», 0,392 кг деп туура окулат.
- Жыйнак жумушчу компьютерге орнотулду, касса жана ээсинин программасы иштейт.

### Текшерилген жок (ошондуктан тесттик версия)

- Чыныгы Штрих-ПРИНТ таразасында.
- Бул жыйнакта сатуу, карыз, кайтаруу жана сменанын толук текшерүүсү.

---

# English

### New

- **Shtrikh-PRINT scale settings inside the till.** Settings → Scales → “Network scales” → “Scale settings (keyboard, label, barcode)…”. Seven tabs like the Shtrikh-M test driver: Keyboard, Status, System, Products, Printing and label, Barcode, Texts. Read from the scales first; only changed fields are written.
- **Barcode when sending to scales.** The “Scales” window has a “Code in barcode” column you can edit per product, with a sample label (e.g. 2000001003923 = prefix 20, product 1, 0.392 kg). The till warns if the scale format does not match the company setting.
- **Till layouts in the Marketplace** — a separate “Till layouts” tab.
- **Customer display follows the till layout**, plus a customer display editor.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Shtrikh-PRINT password | A short password “30” was sent as “3000” | Sent as “0030”, like the Shtrikh-M driver |
| Product code on direct upload | The barcode carried the memory cell number, so the till found the wrong product | The barcode carries the product PLU (or product code for the “by code” layout) |
| Customer display | One look regardless of till layout | Six looks matching the till layout, with its own editor |

### Checked

- Scale commands compared with the Shtrikh-M driver on a scale emulator: 66 of 66 byte-identical.
- Barcode 2000001003923 is parsed by the till as product “тест весовых 1”, 0.392 kg.
- Build installed on the work PC; the till and owner app start.

### Not checked (hence a test release)

- On real Shtrikh-PRINT scales.
- A full run of sales, debt, returns and shifts on this build.

---

# Türkçe

### Yeni

- **Shtrikh-PRINT terazi ayarları kasanın içinde.** Ayarlar → Teraziler → «Ağ terazileri» → «Terazi ayarları (tuş takımı, etiket, barkod)…». Shtrikh-M test sürücüsündeki gibi yedi sekme: Tuş takımı, Durum, Sistem, Ürünler, Yazdırma ve etiket, Barkod, Metinler. Önce teraziden okunur; yalnızca değişen alanlar yazılır.
- **Teraziye gönderirken barkod.** «Teraziler» penceresinde ürün başına düzenlenebilen «Barkoddaki kod» sütunu ve örnek etiket (ör. 2000001003923 = önek 20, ürün 1, 0,392 kg). Terazideki biçim şirket ayarıyla uyuşmazsa kasa uyarır.
- **Kasa görünümleri Pazaryeri'nde** — ayrı «Kasa görünümleri» sekmesi.
- **Müşteri ekranı kasa görünümüne göre değişir**, müşteri ekranı düzenleyicisi eklendi.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Shtrikh-PRINT şifresi | Kısa «30» şifresi «3000» olarak gidiyordu | Shtrikh-M sürücüsü gibi «0030» olarak gider |
| Doğrudan gönderimde ürün kodu | Barkoda hücre numarası giriyordu, kasa yanlış ürünü buluyordu | Barkoda ürünün PLU'su (veya «koda göre» düzende ürün kodu) girer |
| Müşteri ekranı | Tek görünüm | Kasa görünümüne uygun altı görünüm ve düzenleyici |

### Kontrol edildi

- Terazi komutları emülatörde Shtrikh-M sürücüsüyle karşılaştırıldı: 66/66 bayt bayt aynı.
- 2000001003923 barkodu kasada «тест весовых 1», 0,392 kg olarak okunuyor.
- Derleme iş bilgisayarına kuruldu; kasa ve sahip programı açılıyor.

### Kontrol edilmedi (bu yüzden test sürümü)

- Gerçek Shtrikh-PRINT terazisinde.
- Bu derlemede satış, borç, iade ve vardiya tam turu.

---

# O'zbekcha

### Yangi

- **Shtrix-PRINT tarozi sozlamalari kassaning o'zida.** Sozlamalar → Tarozilar → «Tarmoq tarozilari» → «Tarozi sozlamalari (klaviatura, yorliq, shtrix-kod)…». Shtrix-M test drayveridagidek yetti yorliq: Klaviatura, Holat, Tizim, Mahsulotlar, Chop etish va yorliq, Shtrix-kod, Matnlar. Avval tarozidan o'qiladi; faqat o'zgargan maydonlar yoziladi.
- **Taroziga yuborishda shtrix-kod.** «Tarozilar» oynasida har bir mahsulot uchun tahrirlanadigan «Shtrix-koddagi kod» ustuni va yorliq namunasi (masalan, 2000001003923 = prefiks 20, mahsulot 1, 0,392 kg). Tarozidagi format kompaniya sozlamasiga mos kelmasa, kassa ogohlantiradi.
- **Kassa ko'rinishlari Marketpleysda** — alohida «Kassa ko'rinishlari» yorlig'i.
- **Xaridor ekrani kassa ko'rinishiga qarab o'zgaradi**, xaridor ekrani muharriri qo'shildi.

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| Shtrix-PRINT paroli | Qisqa «30» paroli «3000» bo'lib ketardi | Shtrix-M drayveridagidek «0030» bo'lib ketadi |
| To'g'ridan-to'g'ri yuborishda mahsulot kodi | Shtrix-kodga katak raqami tushardi, kassa boshqa mahsulotni topardi | Shtrix-kodga mahsulot PLU'si (yoki «kod bo'yicha» bo'lsa mahsulot kodi) tushadi |
| Xaridor ekrani | Bitta ko'rinish | Kassa ko'rinishiga mos oltita ko'rinish va muharrir |

### Tekshirildi

- Tarozi buyruqlari emulyatorda Shtrix-M drayveri bilan solishtirildi: 66 dan 66 tasi baytma-bayt mos.
- 2000001003923 shtrix-kodi kassada «тест весовых 1», 0,392 kg deb o'qiladi.
- Yig'ma ish kompyuteriga o'rnatildi; kassa va egasi dasturi ishga tushadi.

### Tekshirilmadi (shuning uchun test versiyasi)

- Haqiqiy Shtrix-PRINT tarozisida.
- Ushbu yig'mada savdo, qarz, qaytarish va smenaning to'liq tekshiruvi.
