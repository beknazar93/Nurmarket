## v1.17.20

---

# Русский

### Новое

- **Шесть видов кассы на выбор** (Настройки → Экран → «Вид кассы»). Касса перестраивается сразу, без перезапуска. Товары, чеки, оплата и сканер работают одинаково во всех видах.
  - **Классика** — каталог плитками слева, чек справа (прежний вид, обновлённый).
  - **Табличная** — чек плотной таблицей, поле штрихкода сверху, справа быстрые товары и цветные кнопки. «Наличные» и «Безналичные» сразу открывают оплату нужным способом.
  - **Карточки** — категории кнопками-чипами, крупные карточки товаров, чек с кнопками +/−.
  - **Минимал** — крупные цветные плитки, «Текущая продажа» и большая кнопка «Оплатить» с суммой.
  - **Профи** — тёмная боковая панель с разделами, крупный поиск и список товаров, блок покупателя у чека.
  - **1С** — как «Рабочее место кассира» 1С (без изменений).
- **Редактор своих тем** (Маркетплейс → Темы → «Редактор тем» или карточка «+ Своя тема»): тема на основе любой встроенной, 9 цветов отдельно для светлого и тёмного варианта, скругление, размер шрифта, живой предпросмотр на всей программе. Можно сделать несколько тем, скопировать, выгрузить в файл и загрузить на другой кассе. Закрыли без «Применить» — вернётся прежняя тема.
- **Обновлённый основной вид:** единые векторные значки, крупный итог справа, кнопки при наведении и нажатии — в цветах темы.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Выбранная вкладка чека | Выглядела серой, как недоступная | Выделена цветом темы |
| Кнопки при наведении и нажатии | Серели цветами стандартной темы Windows | Подсвечиваются цветами выбранной темы |
| Баннер «Доступно обновление» | Показывал разметку описания: «## v1.17.19», «---» | Показывает первые пункты «Нового» обычным текстом на языке программы |
| Окно отката на другую версию | Описание версии — разметкой на пяти языках подряд | Описание на языке программы, таблица «было → стало» — строками |

### Проверено перед выпуском (тестовый аккаунт)

- Продажа в виде «Табличная» кнопкой «Безналичные», в виде «Профи» мышью и только клавиатурой (→, Num +, Enter, Enter).
- 6 продаж подряд в «Классике», «Отложить чек» и оплата отложенного, печать последнего чека, цикл «закрыть смену → Z-отчёт → открыть смену»: сумма смены на сервере совпала с продажами (961,00 сом).
- 74 автоматические проверки клавиатуры и сканера во всех шести видах, 72 снимка экранов 1920×1080, 1366×768 и 1024×768 в светлой и тёмной теме — ничего не вылезает за край окна.

---

# Кыргызча

### Жаңы

- **Кассанын алты көрүнүшү** (Жөндөөлөр → Экран → «Кассанын көрүнүшү»). Касса кайра ачылбастан дароо өзгөрөт. Товарлар, чектер, төлөм жана сканер бардык көрүнүштөрдө бирдей иштейт.
  - **Классика** — солдо товарлар плиткалар менен, оңдо чек (мурунку көрүнүш, жаңыланган).
  - **Таблица** — чек тыгыз таблица менен, өйдөдө штрихкод талаасы, оңдо тез товарлар жана түстүү баскычтар. «Накталай» жана «Накталай эмес» төлөмдү дароо керектүү ыкма менен ачат.
  - **Карточкалар** — категориялар чип-баскычтар менен, чоң товар карточкалары, +/− баскычтары бар чек.
  - **Минимал** — чоң түстүү плиткалар, «Учурдагы сатуу» жана суммасы жазылган чоң «Төлөө» баскычы.
  - **Профи** — бөлүмдөрү бар караңгы каптал панель, чоң издөө жана товарлардын тизмеси, чектин жанында сатып алуучунун блогу.
  - **1С** — 1С «Кассирдин жумуш орду» сыяктуу (өзгөрүүсүз).
- **Өз темаларыңыздын редактору** (Маркетплейс → Темалар → «Темалардын редактору» же «+ Өз темаңыз» карточкасы): каалаган орнотулган теманын негизинде тема, жарык жана караңгы вариант үчүн өзүнчө 9 түс, бурчтардын тегеректиги, шрифттин өлчөмү, бүт программада түз алдын ала көрүү. Бир нече тема жасап, көчүрүп, файлга чыгарып, башка кассага жүктөсө болот. «Колдонуу» баспай жапсаңыз — мурунку тема кайтат.
- **Жаңыланган негизги көрүнүш:** бирдиктүү вектордук белгилер, оң жакта чоң жыйынтык, баскычтар курсор келгенде жана басканда теманын түсүндө.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Тандалган чек өтмөгү | Жеткиликсиздей боз көрүнчү | Теманын түсү менен белгиленет |
| Курсор келгенде жана басканда баскычтар | Windows'тун стандарттык темасынын түсүнө боз тартчу | Тандалган теманын түсү менен жарык болот |
| «Жаңыртуу бар» тилкеси | Сүрөттөмөнүн белгилерин көрсөтчү: «## v1.17.19», «---» | «Жаңынын» биринчи пункттарын программанын тилинде жөнөкөй текст менен көрсөтөт |
| Башка версияга кайтуу терезеси | Версиянын сүрөттөмөсү беш тилде катары менен, белгилер менен | Сүрөттөмө программанын тилинде, «мурун → азыр» таблицасы саптар менен |

### Чыгаруудан мурун текшерилди (тесттик аккаунт)

- «Таблица» көрүнүшүндө «Накталай эмес» баскычы менен сатуу, «Профи» көрүнүшүндө чычкан менен жана баскычтоп менен гана (→, Num +, Enter, Enter).
- «Классикада» катары менен 6 сатуу, «Чекти калтыруу» жана калтырылган чекти төлөө, акыркы чекти басып чыгаруу, «сменаны жабуу → Z-отчёт → сменаны ачуу»: серверде сменанын суммасы сатуулар менен дал келди (961,00 сом).
- Бардык алты көрүнүштө баскычтоп жана сканер боюнча 74 автоматтык текшерүү, 1920×1080, 1366×768 жана 1024×768 экрандардын жарык жана караңгы темадагы 72 сүрөтү — эч нерсе терезенин четинен чыкпайт.

---

# English

### New

- **Six till layouts to choose from** (Settings → Screen → “Till layout”). The till rearranges instantly, without a restart. Products, receipts, payment and the scanner work the same in every layout.
  - **Classic** — product tiles on the left, the receipt on the right (the previous layout, refreshed).
  - **Table** — the receipt as a dense table, a barcode field on top, quick products and colored buttons on the right. “Cash” and “Card” open payment with that method right away.
  - **Cards** — categories as chip buttons, large product cards, a receipt with +/− buttons.
  - **Minimal** — large colored tiles, “Current sale” and a big “Pay” button with the amount.
  - **Pro** — a dark side panel with sections, a large search and a product list, a customer block next to the receipt.
  - **1C** — like the 1C “Cashier workstation” (unchanged).
- **Your own theme editor** (Marketplace → Themes → “Theme editor” or the “+ Your theme” card): a theme based on any built-in one, 9 colors separately for the light and dark variant, corner radius, font size, live preview across the whole program. You can make several themes, copy them, export to a file and import on another till. Closed without “Apply” — the previous theme comes back.
- **Refreshed main layout:** consistent vector icons, a large total on the right, buttons highlighted in the theme colors on hover and press.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Selected receipt tab | Looked gray, as if disabled | Highlighted in the theme color |
| Buttons on hover and press | Turned gray in the standard Windows theme colors | Highlighted in the selected theme's colors |
| “Update available” banner | Showed the notes' markup: “## v1.17.19”, “---” | Shows the first “New” items as plain text in the program's language |
| Roll back to another version window | Version notes as markup in five languages in a row | Notes in the program's language, the “before → now” table as lines |

### Checked before release (test account)

- A sale in the “Table” layout with the “Card” button, and in the “Pro” layout with the mouse and with the keyboard only (→, Num +, Enter, Enter).
- 6 sales in a row in “Classic”, “Hold receipt” and paying the held receipt, printing the last receipt, a “close shift → Z-report → open shift” cycle: the shift total on the server matched the sales (961.00 som).
- 74 automated keyboard and scanner checks in all six layouts, 72 screenshots at 1920×1080, 1366×768 and 1024×768 in the light and dark theme — nothing goes past the window edge.

---

# Türkçe

### Yenilikler

- **Seçilebilir altı kasa görünümü** (Ayarlar → Ekran → «Kasa görünümü»). Kasa yeniden başlatmadan anında değişir. Ürünler, fişler, ödeme ve tarayıcı tüm görünümlerde aynı çalışır.
  - **Klasik** — solda ürün kutuları, sağda fiş (önceki görünüm, yenilendi).
  - **Tablo** — fiş sıkı bir tablo halinde, üstte barkod alanı, sağda hızlı ürünler ve renkli düğmeler. «Nakit» ve «Kart» ödemeyi hemen o yöntemle açar.
  - **Kartlar** — kategoriler çip düğmeleri halinde, büyük ürün kartları, +/− düğmeli fiş.
  - **Minimal** — büyük renkli kutular, «Mevcut satış» ve tutarı yazan büyük «Öde» düğmesi.
  - **Pro** — bölümleri olan koyu yan panel, büyük arama ve ürün listesi, fişin yanında müşteri bloğu.
  - **1C** — 1C «Kasiyer iş yeri» gibi (değişmedi).
- **Kendi tema düzenleyiciniz** (Pazaryeri → Temalar → «Tema düzenleyici» veya «+ Kendi temanız» kartı): herhangi bir yerleşik temaya dayalı tema, açık ve koyu varyant için ayrı 9 renk, köşe yuvarlaklığı, yazı boyutu, tüm programda canlı önizleme. Birden çok tema yapılabilir, kopyalanabilir, dosyaya aktarılıp başka kasada yüklenebilir. «Uygula»ya basmadan kapatırsanız önceki tema geri gelir.
- **Yenilenen ana görünüm:** tutarlı vektör simgeler, sağda büyük toplam, düğmeler üzerine gelince ve basınca tema renklerinde.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Seçili fiş sekmesi | Devre dışıymış gibi gri görünüyordu | Tema rengiyle vurgulanır |
| Üzerine gelince ve basınca düğmeler | Standart Windows teması renginde griye dönüyordu | Seçili temanın renkleriyle vurgulanır |
| «Güncelleme mevcut» şeridi | Açıklamanın işaretlerini gösteriyordu: «## v1.17.19», «---» | «Yenilikler»in ilk maddelerini program dilinde düz metin olarak gösterir |
| Başka sürüme dönme penceresi | Sürüm açıklaması beş dilde art arda, işaretlerle | Açıklama program dilinde, «önce → şimdi» tablosu satırlar halinde |

### Yayından önce kontrol edildi (test hesabı)

- «Tablo» görünümünde «Kart» düğmesiyle satış, «Pro» görünümünde fareyle ve yalnızca klavyeyle (→, Num +, Enter, Enter).
- «Klasik»te arka arkaya 6 satış, «Fişi beklet» ve bekleyen fişin ödenmesi, son fişin yazdırılması, «vardiyayı kapat → Z raporu → vardiyayı aç» döngüsü: sunucudaki vardiya toplamı satışlarla aynı çıktı (961,00 som).
- Altı görünümün hepsinde 74 otomatik klavye ve tarayıcı kontrolü, açık ve koyu temada 1920×1080, 1366×768 ve 1024×768 ekranların 72 görüntüsü — hiçbir şey pencere kenarından taşmıyor.

---

# O'zbekcha

### Yangiliklar

- **Kassaning oltita ko'rinishi** (Sozlamalar → Ekran → «Kassa ko'rinishi»). Kassa qayta ishga tushirmasdan darhol o'zgaradi. Mahsulotlar, cheklar, to'lov va skaner barcha ko'rinishlarda bir xil ishlaydi.
  - **Klassika** — chapda mahsulot plitkalari, o'ngda chek (avvalgi ko'rinish, yangilangan).
  - **Jadval** — chek zich jadval ko'rinishida, tepada shtrix-kod maydoni, o'ngda tezkor mahsulotlar va rangli tugmalar. «Naqd» va «Naqdsiz» to'lovni darhol kerakli usulda ochadi.
  - **Kartochkalar** — toifalar chip tugmalar ko'rinishida, katta mahsulot kartochkalari, +/− tugmali chek.
  - **Minimal** — katta rangli plitkalar, «Joriy sotuv» va summasi yozilgan katta «To'lash» tugmasi.
  - **Pro** — bo'limli qorong'i yon panel, katta qidiruv va mahsulotlar ro'yxati, chek yonida xaridor bloki.
  - **1C** — 1C «Kassir ish joyi» kabi (o'zgarishsiz).
- **O'z mavzularingiz muharriri** (Marketpleys → Mavzular → «Mavzular muharriri» yoki «+ O'z mavzungiz» kartochkasi): istalgan o'rnatilgan mavzu asosida mavzu, yorug' va qorong'i variant uchun alohida 9 rang, burchak yumaloqligi, shrift o'lchami, butun dasturda jonli oldindan ko'rish. Bir nechta mavzu yaratish, nusxalash, faylga chiqarish va boshqa kassaga yuklash mumkin. «Qo'llash»ni bosmasdan yopsangiz — avvalgi mavzu qaytadi.
- **Yangilangan asosiy ko'rinish:** yagona vektor belgilar, o'ngda katta jami, tugmalar ustiga kelganda va bosilganda mavzu ranglarida.

### Tuzatildi: avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Tanlangan chek yorlig'i | Faol emasdek kulrang ko'rinardi | Mavzu rangi bilan ajratiladi |
| Ustiga kelganda va bosilganda tugmalar | Windows standart mavzusi rangida kulrang tortardi | Tanlangan mavzu ranglari bilan yonadi |
| «Yangilanish mavjud» tasmasi | Tavsif belgilarini ko'rsatardi: «## v1.17.19», «---» | «Yangiliklar»ning birinchi bandlarini dastur tilida oddiy matn bilan ko'rsatadi |
| Boshqa versiyaga qaytish oynasi | Versiya tavsifi besh tilda ketma-ket, belgilar bilan | Tavsif dastur tilida, «avval → endi» jadvali qatorlar bilan |

### Chiqarishdan oldin tekshirildi (test hisobi)

- «Jadval» ko'rinishida «Naqdsiz» tugmasi bilan sotuv, «Pro» ko'rinishida sichqoncha bilan va faqat klaviatura bilan (→, Num +, Enter, Enter).
- «Klassika»da ketma-ket 6 ta sotuv, «Chekni kutishga qo'yish» va kutishdagi chekni to'lash, oxirgi chekni chop etish, «smenani yopish → Z-hisobot → smenani ochish» sikli: serverdagi smena summasi sotuvlar bilan mos keldi (961,00 som).
- Barcha oltita ko'rinishda klaviatura va skaner bo'yicha 74 ta avtomatik tekshiruv, yorug' va qorong'i mavzuda 1920×1080, 1366×768 va 1024×768 ekranlarning 72 ta surati — hech narsa oyna chetidan chiqmaydi.
