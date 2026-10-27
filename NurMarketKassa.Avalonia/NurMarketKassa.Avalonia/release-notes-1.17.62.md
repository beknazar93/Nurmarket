## v1.17.62 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Печать штрихкодов и ценников (массовая, листы A4), цвета диаграмм, темы сайта для магазина одежды, чек как в мобилке, ключи Gemini из нескольких аккаунтов, печать чеков с телефона, второй монитор, удалённая помощь с приоритетом кассира, исправления Склада, Маркетплейса и редактора сайта.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Печать штрихкодов и ценников.** Новое окно: массовая печать, листы A4, этикетка подбирается автоматически.
- **Редактор этикеток.** Шаблон магазина больше не затирается шаблоном по умолчанию; платные функции проверяются единой проверкой покупки.
- **Диаграммы.** В «Сводке» и аналитике столбцы и доли разных цветов.
- **Темы сайта для магазина одежды** (10 тем); проверка читаемости своих тем; испорченный файл тем восстанавливается.
- **Чек как в мобилке.** Экранный вид чека и PDF «бумага».
- **Ключи Gemini из нескольких аккаунтов.** Окно «Ключи Gemini», автоматическая смена ключа.
- **Печать чеков с телефона** на кассе (по умолчанию выключена).
- **Второй монитор.** «Внесено» и «сдача» при включённой галочке; сторож экрана покупателя возвращает пропавшее окно.
- **Удалённая помощь.** Пока кассир работает мышью или клавиатурой, ввод оператора уступает кассиру. Номер техподдержки: +996 221 068 796.
- **Маркетплейс.** Ряды карточек листаются мышью и пальцем.
- **Убрано голосовое управление кассой** — теперь есть ИИ-ассистент (Нур Советник).

### Исправлено: было → стало

| Было | Стало |
| --- | --- |
| «Сохранить» в редакторе этикеток затирало настоящий шаблон шаблоном по умолчанию | Открывается сохранённый шаблон; без правок ничего не записывается |
| Платные функции проверялись по-разному в разных окнах | Единая проверка покупки |
| Диаграммы в «Сводке» и аналитике были одного цвета | Столбцы и доли разных цветов |
| Испорченный файл тем ломал список тем | Предупреждение при открытии, прежний хороший файл — в .bak, файл восстанавливается |
| Поля ключей и паролей отдавали значение через UI Automation | Скрытое точками значение не отдаётся |
| Принтер этикеток мог запомниться виртуальным (PDF/XPS) | Виртуальные принтеры не запоминаются |
| Склад: лишняя подсказка сканера в углу; фильтр «Наличие» повторял плитки; приёмка не влезала в узкое окно | Подсказка убрана, дубль фильтра убран, узкая приёмка |
| Второй монитор не показывал, сколько внесено; итог оплаты держался 4 с | «Внесено» и «сдача» показываются при включённой галочке; сторож возвращает пропавшее окно |
| «Редактор сайта»: публикация могла уйти покупателям по вопросу «Как опубликовать сайт?» | Публикация только по команде и после подтверждения; нужно право «Настройки» |
| Карточки в Маркетплейсе двигались только стрелкой | Ряды листаются мышью и пальцем |

### Проверено перед выпуском

- Сборка Windows и Linux (Portable) без ошибок.
- Живая приёмка на тестовом аккаунте NBS не завершена — версия тестовая.

### Ещё не проверено вживую / известные ограничения

- Печать чеков с телефона выключена по умолчанию и требует отдельной проверки на кассе.
- Тексты на английском, турецком и узбекском переведены без носителя языка.

---

# Кыргызча

**Тесттик версия — тестировщиктер үчүн гана.**

- **Штрихкод жана баа белгилерин басуу.** Жаңы терезе: жапырт басуу, A4 барактары, этикетка автоматтык тандалат.
- **Этикетка редактору.** Дүкөндүн шаблону демейки шаблон менен жазылып калбайт; акы төлөнүүчү функциялар сатып алууну бирдиктүү текшерүү менен текшерилет.
- **Диаграммалар.** «Жыйынтыкта» жана аналитикада мамычалар менен үлүштөр ар түрдүү түстө.
- **Кийим дүкөнү үчүн сайт темалары** (10 тема); өз темаларыңыздын окулуучулугун текшерүү; бузулган темалар файлы калыбына келет.
- **Чек мобилкадагыдай.** Экрандагы чек көрүнүшү жана PDF «кагаз».
- **Бир нече аккаунттун Gemini ачкычтары.** «Gemini ачкычтары» терезеси, ачкычты автоматтык алмаштыруу.
- **Телефондон чектерди басуу** (демейки боюнча өчүк).
- **Экинчи монитор.** Белгиленген болсо «киргизилди» жана «кайтарым»; сатып алуучунун экранынын кароолчусу жоголгон терезени кайтарат.
- **Алыстан жардам.** Кассир чычкан же баскычтоп менен иштегенде оператордун киргизүүсү кассирге жол берет. Колдоонун номери: +996 221 068 796.
- **Маркетплейс.** Карточка катарлары чычкан жана манжа менен сүрүлөт.
- **Кассанын үн менен башкаруусу алынды** — эми ИИ-жардамчы бар (Нур Кеңешчи).
- **Оңдоолор.** этикетка принтери виртуалдык (PDF/XPS) принтерлерди эстебейт; Кампа: ашыкча сканер кеңеши жана «Бар болушу» чыпкасынын кайталанышы алынды, тар терезеде кабыл алуу; «Сайт редактору»: жарыялоо буйрук менен жана ырастоодон кийин гана.

**Чектөөлөр.** Тирүү кабыл алуу бүтө элек — ошондуктан тесттик версия.

---

# English

**Test version — for testers only.**

- **Printing barcodes and price tags.** A new window: bulk printing, A4 sheets, the label is fitted automatically.
- **Label editor.** The store template is no longer overwritten by the default one; paid features are checked by one common purchase check.
- **Charts.** In “Summary” and analytics, chart bars and shares are in different colors.
- **Site themes for a clothing store** (10 themes); readability check of your own themes; a damaged themes file is restored.
- **Receipt like in the mobile app.** An on-screen receipt view and a PDF “paper” receipt.
- **Gemini keys from several accounts.** The “Gemini keys” window, automatic key rotation.
- **Printing receipts from the phone** at the register (off by default).
- **Second monitor.** “Paid” and “change” when the checkbox is on; the customer-screen watchdog brings back a lost window.
- **Remote help.** While the cashier works with the mouse or keyboard, the operator’s input yields to the cashier. Support number: +996 221 068 796.
- **Marketplace.** Card rows scroll with the mouse and a finger.
- **Voice control of the till removed** — there is now an AI assistant (Nur Advisor).
- **Fixes.** The label printer does not remember virtual (PDF/XPS) printers; Stock: the extra scanner hint and the duplicated “Availability” filter are removed, receiving in a narrow window; “Site editor”: publishing only on command and after confirmation.

**Limitations.** The live acceptance is not finished — that is why it is a test build.

---

# Türkçe

**Test sürümü — yalnızca test kullanıcıları için.**

- **Barkod ve fiyat etiketi yazdırma.** Yeni pencere: toplu yazdırma, A4 sayfaları, etiket otomatik seçilir.
- **Etiket düzenleyici.** Mağaza şablonu artık varsayılan şablonla ezilmez; ücretli işlevler tek ortak satın alma denetimiyle kontrol edilir.
- **Grafikler.** «Özet» ve analizde sütunlar ve paylar farklı renklerde.
- **Giyim mağazası için site temaları** (10 tema); kendi temalarınızın okunabilirlik denetimi; bozulan tema dosyası geri yüklenir.
- **Mobil uygulamadaki gibi fiş.** Ekran fiş görünümü ve PDF «kâğıt».
- **Birden çok hesabın Gemini anahtarları.** «Gemini anahtarları» penceresi, otomatik anahtar değişimi.
- **Telefondan fiş yazdırma** kasada (varsayılan olarak kapalı).
- **İkinci monitör.** Kutu işaretliyse «ödenen» ve «para üstü»; müşteri ekranı bekçisi kaybolan pencereyi geri getirir.
- **Uzaktan yardım.** Kasiyer fare veya klavye kullanırken operatörün girişi kasiyere yol verir. Destek numarası: +996 221 068 796.
- **Pazaryeri.** Kart sıraları fare ve parmakla kaydırılır.
- **Kasanın sesli kontrolü kaldırıldı** — artık bir yapay zekâ asistanı var (Nur Danışman).
- **Düzeltmeler.** Etiket yazıcısı sanal (PDF/XPS) yazıcıları hatırlamaz; Depo: gereksiz tarayıcı ipucu ve tekrarlanan «Stok durumu» filtresi kaldırıldı, dar pencerede mal kabul; «Site düzenleyici»: yayınlama yalnızca komutla ve onaydan sonra.

**Sınırlamalar.** Canlı kabul bitmedi — bu yüzden test sürümü.

---

# O'zbekcha

**Test versiyasi — faqat testerlar uchun.**

- **Shtrix-kod va narx yorliqlarini chop etish.** Yangi oyna: ommaviy chop etish, A4 varaqlar, yorliq avtomatik tanlanadi.
- **Yorliq muharriri.** Do'kon shabloni endi standart shablon bilan o'chirilmaydi; pullik funksiyalar yagona xarid tekshiruvi bilan tekshiriladi.
- **Diagrammalar.** «Xulosa» va tahlilda ustunlar va ulushlar turli ranglarda.
- **Kiyim do'koni uchun sayt mavzulari** (10 ta); o'z mavzularingiz o'qilishini tekshirish; buzilgan mavzular fayli tiklanadi.
- **Chek mobil ilovadagidek.** Ekrandagi chek ko'rinishi va PDF «qog'oz».
- **Bir nechta akkauntning Gemini kalitlari.** «Gemini kalitlari» oynasi, kalitni avtomatik almashtirish.
- **Telefondan cheklarni chop etish** kassada (odatda o'chiq).
- **Ikkinchi monitor.** Belgi yoqilgan bo'lsa «kiritildi» va «qaytim»; xaridor ekrani qorovuli yo'qolgan oynani qaytaradi.
- **Masofaviy yordam.** Kassir sichqoncha yoki klaviatura bilan ishlaganda operator kiritishi kassirga yo'l beradi. Yordam raqami: +996 221 068 796.
- **Marketplace.** Kartalar qatorlari sichqoncha va barmoq bilan suriladi.
- **Kassaning ovozli boshqaruvi olib tashlandi** — endi SI yordamchisi bor (Nur Maslahatchi).
- **Tuzatishlar.** Yorliq printeri virtual (PDF/XPS) printerlarni eslamaydi; Ombor: ortiqcha skaner maslahati va takrorlanuvchi «Mavjudligi» filtri olib tashlandi, tor oynada qabul; «Sayt muharriri»: e'lon qilish faqat buyruq bilan va tasdiqlangandan keyin.

**Cheklovlar.** Jonli qabul tugamagan — shuning uchun test versiyasi.
