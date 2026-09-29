## v1.17.29

Изменения с прошлой версии (1.17.28).

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Запись своей озвучки и регистрация голоса | Касса могла закрыться с ошибкой «Exception Processing Message 0xc0000005» | Не закрывается |
| «касса нан уч», «касса нан торт» | Количество 1 — «уч» и «торт» (без ү/ө) не понимались | 3 и 4 |
| «касса нан он эки», «касса нан жыйырма беш» | 2 и 5 | 12 и 25 |
| «касса хлеб двадцать пять», «касса хлеб сто» | 5 и 1 | 25 и 100 |
| Выученные слова с числом (Проверить голос → слова, «эки» → «2») | Слово убиралось из фразы, количество оставалось 1 | Количество = числу слова |
| «касса спрайт», «касса сумка» | Каждый раз вопрос «какой товар?» — слово «с» в «Кекс … с какао» совпадало со всем на «с» | Сразу нужный товар |
| «касса хлеб» при товарах «хлеб», «черный хлеб», «Хлеб от армянина» | Вопрос «какой товар?» | Сразу «хлеб» |
| «кассага алма …» | Искался товар «га алма» | Понимается как «касса алма …» |
| Голосовой замок после обновления кассы | Модель и голос кассира стирались, замок молча переставал проверять голос | Сохраняются |
| Программа владельца: «Выйти» в Настройки → Аккаунт | Не срабатывала | Выходит из учётной записи |

### Новое

- **Кыргызский счёт голосом:** бир, эки, үч/уч, төрт/торт, беш … тогуз, он, жыйырма, отуз, кырк, элүү … токсон, жүз и составные: «он эки» = 12, «эки жүз элүү» = 250, «бир жарым кило» = 1,5. Слова «он» и «торт» считаются числом только рядом с другим числом или единицей или в конце после названия — «касса торт наполеон» остаётся тортом.
- **Запись своей озвучки:** мигающий красный индикатор, «Запись 0:03 из 0:15», полоска громкости и время на кнопке «■ Стоп». Если запись дошла до 15 секунд — касса пишет об этом.
- **Программа владельца:** внизу меню — кнопка «Выйти на рабочий стол» (вход сохраняется); выход из учётной записи — Настройки → Аккаунт → «Выйти».

### Проверено

- Падение воспроизведено на прошлой версии: 40 циклов «включить — выключить» прослушивание с микрофоном — касса упала на 14-м. На новой: 60 из 60 без падения.
- Разбор голосовых команд: 69 из 69 фраз (кыргызский и русский счёт, единицы, «поштучно/пачка», команды «чекти тазала», «төлө», «оплата» и др.).
- В кассе, окно «Проверка голосового управления»: «касса спрайт уч» → Спрайт 1,0л × 3; «кассага алма бир жарым кило» → Алма × 1,5; «касса хлеб двадцать пять» → хлеб × 25; «касса кымыз алды» → кымыз × 6; «касса торт наполеон» — товар «торт наполеон».
- Индикатор записи: «Запись 0:02 из 0:15» → «0:04», полоска громкости двигается, на кнопке «■ Стоп 0:04».
- Программа владельца: «Выйти» в Аккаунте → окно входа → вход; кнопка внизу меню закрывает программу, повторный запуск входит сам.
- Сборка 1.17.29 установлена на рабочий компьютер: касса и программа владельца запускаются, голосовое управление слушает, каталог загружается; продажа №1207 (3 товара, наличные) прошла одним запросом за 1,4 с. Оплата, смена и аналитика в этой версии не менялись — полный регресс был в 1.17.28.

### Не проверено

- Кыргызские команды живым голосом разных кассиров. Кыргызская модель распознавания маленькая: на синтезированной речи она верно поняла 5 команд из 15 (русская — 8 из 10). Если касса слышит слово неверно — научите её фразе: Маркетплейс → Голосовое управление → «Проверить и обучение».

---

# Кыргызча

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Өз үнүңүз менен жазуу жана үндү каттоо | Касса «0xc0000005» катасы менен жабылып калчу | Жабылбайт |
| «касса нан уч», «касса нан торт» | Саны 1 — «уч», «торт» түшүнүлчү эмес | 3 жана 4 |
| «касса нан он эки», «касса нан жыйырма беш» | 2 жана 5 | 12 жана 25 |
| Сан менен үйрөтүлгөн сөздөр («эки» → «2») | Саны 1 бойдон калчу | Сөздүн саны коюлат |
| «касса спрайт», «касса сумка» | Ар дайым «кайсы товар?» деп сурачу | Керектүү товар дароо |
| «кассага алма …» | «га алма» деген товар изделчү | «касса алма …» катары түшүнүлөт |
| Касса жаңыргандан кийин үн кулпусу | Модель жана кассирдин үнү өчүп калчу | Сакталат |
| Ээсинин программасы: Жөндөөлөр → Аккаунт → «Чыгуу» | Иштечү эмес | Эсептик жазуудан чыгат |

### Жаңы

- **Кыргызча эсеп үн менен:** бир … тогуз, он, жыйырма, отуз, кырк, элүү … токсон, жүз, жарым жана кошмо сандар: «он эки» = 12, «эки жүз элүү» = 250, «бир жарым кило» = 1,5.
- **Өз үнүңүз менен жазуу:** жымыңдаган белги, «0:03 / 0:15» убакыт жана үндүн деңгээли.
- **Ээсинин программасы:** менюнун ылдыйында — «Иш столуна чыгуу»; эсептик жазуудан чыгуу — Жөндөөлөр → Аккаунт.

### Текшерилди

- Мурунку версияда 40 циклдын 14-үндө касса жабылды; жаңысында 60 циклдан 60 — катасыз.
- Үн буйруктары: 69 фразадан 69 туура. Кассада: «касса спрайт уч» → Спрайт 1,0л × 3; «кассага алма бир жарым кило» → Алма × 1,5.

### Текшерилген жок

- Ар кандай кассирлердин кыргызча буйруктары тирүү үн менен. Касса сөздү туура эмес укса — Маркетплейс → Үн менен башкаруу → «Текшерүү жана үйрөтүү» аркылуу үйрөтүңүз.

---

# English

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Recording your own prompts and enrolling a voice | The till could close with “Exception Processing Message 0xc0000005” | It does not |
| “касса нан уч”, “касса нан торт” | Quantity 1 | 3 and 4 |
| “касса нан он эки”, “касса нан жыйырма беш” | 2 and 5 | 12 and 25 |
| “касса хлеб двадцать пять”, “касса хлеб сто” | 5 and 1 | 25 and 100 |
| Taught words with a number (“эки” → “2”) | Quantity stayed 1 | Quantity = the word’s number |
| “касса спрайт”, “касса сумка” | Always asked “which product?” | The right product at once |
| Voice lock after a till update | The model and the cashier’s voice were erased | Kept |
| Owner app: Settings → Account → “Sign out” | Did nothing | Signs out |

### New

- **Kyrgyz counting by voice**, including tens, hundreds, “жарым” (half) and compound numbers: “он эки” = 12, “эки жүз элүү” = 250, “бир жарым кило” = 1.5.
- **Recording your own prompts:** blinking indicator, “0:03 of 0:15” and a volume bar.
- **Owner app:** “Exit to desktop” at the bottom of the menu; sign-out is in Settings → Account.

### Checked

- The crash reproduced on the previous version (on cycle 14 of 40); the new one passed 60 of 60.
- Voice commands: 69 of 69 phrases parsed correctly; in the till “касса спрайт уч” → Sprite 1.0 l × 3.

### Not checked

- Kyrgyz commands with live voices of different cashiers. If the till mishears a word, teach it the phrase: Marketplace → Voice control → “Test and train”.

---

# Türkçe

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Kendi seslendirmenizi kaydetme ve ses kaydı | Kasa «0xc0000005» hatasıyla kapanabiliyordu | Kapanmıyor |
| «касса нан уч», «касса нан торт» | Miktar 1 | 3 ve 4 |
| «касса нан он эки», «касса нан жыйырма беш» | 2 ve 5 | 12 ve 25 |
| Sayıyla öğretilen kelimeler («эки» → «2») | Miktar 1 kalıyordu | Miktar = kelimenin sayısı |
| «касса спрайт», «касса сумка» | Her seferinde «hangi ürün?» diye soruyordu | Doğru ürün hemen |
| Kasa güncellemesinden sonra ses kilidi | Model ve kasiyerin sesi siliniyordu | Korunuyor |
| Sahip programı: Ayarlar → Hesap → «Çıkış» | Çalışmıyordu | Oturumu kapatıyor |

### Yeni

- **Sesle Kırgızca sayma:** onlar, yüzler, «жарым» (yarım) ve birleşik sayılar: «он эки» = 12, «эки жүз элүү» = 250, «бир жарым кило» = 1,5.
- **Kendi seslendirmenizi kaydetme:** yanıp sönen gösterge, «0:03 / 0:15» süre ve ses seviyesi.
- **Sahip programı:** menünün altında «Masaüstüne çık»; oturumu kapatma Ayarlar → Hesap içinde.

### Kontrol edildi

- Çökme önceki sürümde yeniden üretildi (40 döngünün 14.sünde); yenisi 60/60.
- Sesli komutlar: 69 ifadenin 69’u doğru; kasada «касса спрайт уч» → Sprite 1,0 l × 3.

### Kontrol edilmedi

- Farklı kasiyerlerin canlı sesiyle Kırgızca komutlar. Kasa bir kelimeyi yanlış duyarsa: Pazar yeri → Sesli kontrol → «Test et ve eğit».

---

# O'zbekcha

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| O'z ovozingiz bilan yozish va ovozni ro'yxatdan o'tkazish | Kassa «0xc0000005» xatosi bilan yopilib qolishi mumkin edi | Yopilmaydi |
| «касса нан уч», «касса нан торт» | Miqdor 1 | 3 va 4 |
| «касса нан он эки», «касса нан жыйырма беш» | 2 va 5 | 12 va 25 |
| Son bilan o'rgatilgan so'zlar («эки» → «2») | Miqdor 1 bo'lib qolardi | Miqdor = so'zning soni |
| «касса спрайт», «касса сумка» | Har safar «qaysi mahsulot?» deb so'rardi | Kerakli mahsulot darhol |
| Kassa yangilangandan keyin ovoz qulfi | Model va kassir ovozi o'chib ketardi | Saqlanadi |
| Egasi dasturi: Sozlamalar → Hisob → «Chiqish» | Ishlamasdi | Hisobdan chiqadi |

### Yangi

- **Ovoz bilan qirg'izcha sanash:** o'nliklar, yuzliklar, «жарым» (yarim) va qo'shma sonlar: «он эки» = 12, «эки жүз элүү» = 250, «бир жарым кило» = 1,5.
- **O'z ovozingiz bilan yozish:** miltillovchi belgi, «0:03 / 0:15» vaqt va ovoz darajasi.
- **Egasi dasturi:** menyu pastida «Ish stoliga chiqish»; hisobdan chiqish — Sozlamalar → Hisob.

### Tekshirildi

- Nosozlik oldingi versiyada qayta chiqarildi (40 siklning 14-sida); yangisi 60 dan 60.
- Ovozli buyruqlar: 69 iboradan 69 tasi to'g'ri; kassada «касса спрайт уч» → Sprite 1,0 l × 3.

### Tekshirilmadi

- Turli kassirlarning jonli ovozi bilan qirg'izcha buyruqlar. Kassa so'zni noto'g'ri eshitsa: Marketpleys → Ovozli boshqaruv → «Tekshirish va o'rgatish».
