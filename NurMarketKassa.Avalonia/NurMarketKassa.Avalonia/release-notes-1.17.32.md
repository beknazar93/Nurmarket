## v1.17.32 (тестовая)

Тестовая версия поверх 1.17.31 — клиентам автоматически не приходит.

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Голосовое управление слышит разговор у кассы | Слово «касса» искалось в любом месте фразы и внутри слов: «то что касается…», «вай фай касас ай», «…как касса за вы сами» разбирались как «добавь товар» | Командой считается только отдельное слово «касса»/«каса» (или «кассага», «кассада») первым или вторым словом фразы |
| Настройки на узком экране (1024×768 при 125–150 %, 800×600) | Строки в 2–3 колонки сжимались, поля и подписи обрезались | На странице уже 760 точек строки встают столбиком; на широком экране вид прежний |

### Проверено

- Разбор ключевого слова на 13 фразах из журнала кассы: 8 настоящих команд («касса алма два килограмма», «кассага алма бир жарым кило», «ну касса хлеб» …) — команды; «касается…», «касас…», «касание…», «на кассе стоит человек», «касса» в середине фразы — не команды.
- Сборка установлена на рабочий компьютер: касса и программа владельца запускаются.

### Не проверено

- Страницы настроек на настоящем маленьком экране (на мониторе 1920 перестройка не включается).
- Живым голосом в шумном магазине.

---

# Кыргызча

| Эмне | Мурун | Азыр |
|---|---|---|
| Үн менен башкаруу кассанын жанындагы сүйлөшүүнү угат | «касса» сөзү фразанын каалаган жеринен жана сөздөрдүн ичинен изделчү — «касается…», «касас…» «товар кош» деп түшүнүлчү | Фразанын биринчи же экинчи сөзү болгон өзүнчө «касса»/«каса» («кассага», «кассада») гана буйрук |
| Тар экрандагы жөндөөлөр | 2–3 тилкелүү саптар кысылып, талаалар кесилчү | Барак 760 чекиттен тар болсо, саптар мамыча болуп тизилет |

---

# English

| What | Before | Now |
|---|---|---|
| Voice control hears talk near the till | “касса” was searched anywhere in the phrase and inside words — “касается…”, “касас…” were taken as “add a product” | Only a separate word “касса”/“каса” (or “кассага”, “кассада”) as the first or second word is a command |
| Settings on a narrow screen | 2–3-column rows got squeezed, fields and labels were cut | Below 760 px the rows stack vertically |

---

# Türkçe

| Ne | Önce | Şimdi |
|---|---|---|
| Sesli kontrol kasa yanındaki konuşmayı duyuyor | «касса» cümlenin her yerinde ve kelime içinde aranıyordu — «касается…», «касас…» «ürün ekle» sanılıyordu | Yalnızca cümlenin ilk veya ikinci kelimesi olan ayrı «касса»/«каса» («кассага», «кассада») komuttur |
| Dar ekranda ayarlar | 2–3 sütunlu satırlar sıkışıyor, alanlar kesiliyordu | 760 pikselden darda satırlar alt alta dizilir |

---

# O‘zbekcha

| Nima | Oldin | Hozir |
|---|---|---|
| Ovozli boshqaruv kassa yonidagi suhbatni eshitadi | «касса» iboraning istalgan joyida va so‘z ichida qidirilardi — «касается…», «касас…» «tovar qo‘sh» deb tushunilardi | Faqat iboraning birinchi yoki ikkinchi so‘zi bo‘lgan alohida «касса»/«каса» («кассага», «кассада») buyruq |
| Tor ekrandagi sozlamalar | 2–3 ustunli qatorlar siqilib, maydonlar kesilardi | 760 pikseldan tor bo‘lsa qatorlar ustma-ust joylashadi |
