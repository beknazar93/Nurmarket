## v1.17.27

Изменения с прошлой версии для всех (1.17.22): 1.17.23–1.17.27.

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Этикетка весов с СУММОЙ в штрихкоде (TM-30F и другие, префикс 21) | «Алма» 0,220 кг × 60 = 13,20, штрихкод 2101003013207 → в чеке 1,320 кг = 79,20 | В чеке 0,220 кг × 60 = 13,20, как на этикетке |
| Вес из суммы, если весы печатают сумму с тыйынами | Вес подбирался как для весов, отбрасывающих тыйыны, — сумма могла разойтись с этикеткой | Если сумма ровно делится на цену — берётся точный вес |
| Первый чек после «Открыть смену» | Мог уйти в офлайн-очередь при живой связи | Проводится сразу |
| Сбой сервера NurCRM (502) при оплате | «error code: 502» | Понятное сообщение; касса проверяет, не прошла ли продажа, — двойного чека нет |
| F1–F12 при открытом окне товаров | Другая клавиша не срабатывала | Окно сразу меняется |
| Марка и адрес весов | Сбрасывались после перезапуска | Сохраняются |

### Новое

- **Касса сама спрашивает, что в штрихкоде весов.** При первом скане этикетки с префиксом 21–24 или 26–29: «ДА — СУММА 13,20 (0,220 кг) / НЕТ — ВЕС 1,320 кг (79,20)». Сверьте с этикеткой и нажмите — ответ запоминается, больше касса не спрашивает. Префиксы 20 (вес) и 25 (сумма) работают как раньше.
- **Оплата одним запросом** к серверу: 0,3–0,4 с вместо 1,5–1,9 с. Смешанная оплата уходит с разбивкой на наличные и безнал.
- **Номер чека как на сайте** (например 001125) — на чеке сразу после оплаты, в «Истории чеков» и в «Возврате»; поиск чека по номеру.
- **Отчёт смены и Z-отчёт с сервера**; внесение входит в сумму смены; в «Деталях смены» — список проданных товаров.
- **«Оплата долга» одной суммой** — сервер сам раскладывает по взносам.
- **Весы:** поиск весов в сети (все подсети), окна настроек для Штрих-ПРИНТ, Rongta и TM-30F, прямая отправка товаров на TM-30F (Dahua), «Валюта» и «Макет этикетки» Штрих-ПРИНТ, поиск в окне «Весы».
- **Количество в строке чека** вводится с клавиатуры во всех видах кассы; быстрые товары можно отключить (Настройки → Экран).

### Проверено

- Касса на рабочем компьютере: скан 2101003013207 → вопрос показал «СУММА 13,20 (0,220 кг)» и «ВЕС 1,320 кг (79,20)», после «Да» в чеке «Алма 0,220 кг × 60 = 13,20». Следующая этикетка 2110007035207 без вопроса: «Айфон 0,220 кг × 160 = 35,20», итого 48,40.
- Разбор кодов: 2134567012072 → 12,07; префикс 20 (2000001003923) — по-прежнему вес 0,392 кг.
- Оплата одним запросом, отчёт смены, долг одной суммой — проверены продажами на тестовом аккаунте в 1.17.25.

### Не проверено

- Скан на компьютере с весами TM-30F после обновления — при первой этикетке с префиксом 21 нажмите «Да», если на этикетке сумма.

---

# Кыргызча

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Штрих-коддо СУММА бар тараза этикеткасы (TM-30F ж.б., префикс 21) | «Алма» 0,220 кг × 60 = 13,20, 2101003013207 → чекте 1,320 кг = 79,20 | Чекте 0,220 кг × 60 = 13,20, этикеткадагыдай |
| «Сменаны ачуудан» кийинки биринчи чек | Байланыш бар болсо да офлайн кетчү | Дароо өтөт |
| Төлөмдө сервер катасы (502) | «error code: 502» | Түшүнүктүү билдирүү, эки чек түзүлбөйт |
| Таразанын маркасы жана дареги | Кайра иштеткенде өчүп калчу | Сакталат |

### Жаңы

- **Касса тараза штрих-кодунда эмне экенин өзү сурайт:** 21–24 же 26–29 префикстүү этикетканы биринчи сканерлегенде «ООБА — СУММА / ЖОК — САЛМАК» — жооп эсте калат.
- Төлөм серверге бир суроо менен (0,3–0,4 с); сайттагыдай чек номери; сменанын отчёту серверден; карызды бир сумма менен төлөө; таразаларды тармактан издөө жана жөндөө терезелери.

### Текшерилди

- 2101003013207 → «Алма 0,220 кг × 60 = 13,20»; 2110007035207 → «Айфон 0,220 кг × 160 = 35,20»; 20 префикси мурдагыдай салмак.

---

# English

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Scale label with the TOTAL in the barcode (TM-30F etc., prefix 21) | “Alma” 0.220 kg × 60 = 13.20, 2101003013207 → 1.320 kg = 79.20 in the receipt | 0.220 kg × 60 = 13.20, as on the label |
| First receipt after “Open shift” | Could go to the offline queue while online | Goes through at once |
| Server failure (502) at payment | “error code: 502” | Clear message, no duplicate sale |
| Scale brand and address | Reset after restart | Saved |

### New

- **The till asks what the scale barcode holds:** on the first scan of a label with prefix 21–24 or 26–29 — “YES — TOTAL / NO — WEIGHT”, the answer is remembered.
- One-request payment (0.3–0.4 s); receipt number as on the website; shift report from the server; debt payment with one sum; scale network search and settings windows.

### Checked

- 2101003013207 → “Alma 0.220 kg × 60 = 13.20”; 2110007035207 → “iPhone 0.220 kg × 160 = 35.20”; prefix 20 still weight.

---

# Türkçe

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Barkodunda TUTAR olan terazi etiketi (TM-30F vb., önek 21) | «Alma» 0,220 kg × 60 = 13,20, 2101003013207 → fişte 1,320 kg = 79,20 | Fişte 0,220 kg × 60 = 13,20, etiketteki gibi |
| «Vardiya aç» sonrası ilk fiş | Bağlantı varken çevrimdışı kuyruğa gidebiliyordu | Hemen geçer |
| Ödemede sunucu hatası (502) | «error code: 502» | Anlaşılır mesaj, çift satış yok |
| Terazi markası ve adresi | Yeniden başlatınca sıfırlanıyordu | Kaydediliyor |

### Yeni

- **Kasa terazi barkodunda ne olduğunu sorar:** 21–24 veya 26–29 önekli etiketin ilk taramasında «EVET — TUTAR / HAYIR — AĞIRLIK», cevap hatırlanır.
- Tek istekle ödeme (0,3–0,4 sn); sitedeki gibi fiş numarası; sunucudan vardiya raporu; borcu tek tutarla ödeme; ağda terazi arama ve ayar pencereleri.

### Kontrol edildi

- 2101003013207 → «Alma 0,220 kg × 60 = 13,20»; 2110007035207 → «Ayfon 0,220 kg × 160 = 35,20»; önek 20 hâlâ ağırlık.

---

# O'zbekcha

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| Shtrix-kodida SUMMA bor tarozi yorlig'i (TM-30F va b., prefiks 21) | «Alma» 0,220 kg × 60 = 13,20, 2101003013207 → chekda 1,320 kg = 79,20 | Chekda 0,220 kg × 60 = 13,20, yorliqdagidek |
| «Smenani ochish»dan keyingi birinchi chek | Aloqa bor paytda oflayn navbatga ketishi mumkin edi | Darhol o'tadi |
| To'lovda server xatosi (502) | «error code: 502» | Tushunarli xabar, ikki savdo yo'q |
| Tarozi markasi va manzili | Qayta ishga tushirganda o'chib ketardi | Saqlanadi |

### Yangi

- **Kassa tarozi shtrix-kodida nima borligini so'raydi:** 21–24 yoki 26–29 prefiksli yorliqni birinchi skanerlashda «HA — SUMMA / YO'Q — VAZN», javob eslab qolinadi.
- Bitta so'rov bilan to'lov (0,3–0,4 s); saytdagidek chek raqami; smena hisoboti serverdan; qarzni bitta summa bilan to'lash; tarmoqda tarozi qidirish va sozlash oynalari.

### Tekshirildi

- 2101003013207 → «Alma 0,220 kg × 60 = 13,20»; 2110007035207 → «Ayfon 0,220 kg × 160 = 35,20»; 20 prefiksi avvalgidek vazn.
