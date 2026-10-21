## v1.17.59 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Возвраты со скидкой и долгом, срок долга в чеке, закрытие смены и X-отчёт, поставщики и долги поставщикам, прокат во всех видах магазина, КП/счёт/накладная в «Клиентах», Нур Советник быстрее и точнее, обновлённый Telegram-бот, новый вид окон и тёмная тема.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Возврат со скидкой и долгом.** Если на чек была скидка, к выдаче считается то, что покупатель реально заплатил за возвращаемые товары. Возврат продажи в долг уменьшает долг — наличные не выдаются («Выдать 0»). В истории чека — блок «Возвраты», у строк пометка «↩ возвращён».
- **Долг с датой возврата.** Срок по умолчанию — 30 дней, его можно изменить. В чеке печатается «Вернуть до дд.мм.гггг», имя клиента скрыто маской.
- **Оплата долга.** В окне — список долгов клиента, Enter принимает оплату, перед «Погасить» — подтверждение.
- **Закрытие смены.** Продажи и возвраты отдельно, по способам оплаты; поле «Фактически» пустое, рядом «Ожидается N». **X-отчёт** — итог смены без её закрытия.
- **Поставщики.** Поставщик в карточке товара; при приёмке можно записать долг поставщику и потом оплатить его.
- **Прокат** работает во всех видах магазина; в списке «Возвращены» последний — сверху, с датой возврата.
- **КП, счёт и накладная** в программе владельца, вкладка «Клиенты → Документы»: редактор с предпросмотром A4, «Сделать счёт», «Сделать накладную», PDF, Word, печать, отправка в WhatsApp. Документы общие с телефоном.
- **Нур Советник** отвечает за 1–3 секунды вместо ~25, цифры по долгам сверены с сервером, есть «откуда цифры» и таблица «В Excel». Утренний отчёт в 9:00, напоминания должникам (WhatsApp по очереди, Telegram по кнопке), проверка перед «Применить».
- **Telegram-бот.** Владелец подтверждает себя одноразовым кодом (6 цифр, 10 минут), выручка — с сервера, ответы быстрые (у каждого чата своя очередь), меню кнопками на 5 языках. **После обновления в группе владельцу нужно один раз подтвердить себя кодом.**
- **Новый вид окон** (одинаковые рамки, меню ☰ кассы), **тёмная тема** — цены цветом текста, не жёлтые.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Возврат из чека со скидкой | Выдавалась полная цена товара | Выдаётся оплаченная часть с учётом скидки |
| Возврат продажи в долг | Касса предлагала выдать наличные | Уменьшается долг, к выдаче 0 |
| Срок долга | В чеке не печатался | «Вернуть до …» в чеке |
| Закрытие смены | Одна общая сумма | Продажи и возвраты отдельно, «Ожидается N» |
| Итог смены без закрытия | Не было | X-отчёт |
| Долг поставщику | Не учитывался | Записывается при приёмке и оплачивается |
| Ответ Нур Советника | ~25 секунд | 1–3 секунды |
| Чужой чат в Telegram | Мог стать «владельцем» | Владелец — только по одноразовому коду |
| Цены в тёмной теме | Жёлтые | Цветом текста |

### Проверено перед выпуском (20–21.10, тестовый аккаунт)

- Касса: возврат со скидкой на чек (28,13 + 46,87 = 75), возврат долговой продажи, смешанная оплата, закрытие смены с возвратами, X-отчёт, долг со сроком «Вернуть до 19.11.2026» в чеке, память кассы за 3 ч 20 мин работы стабильна.
- Нур Советник: больше 20 вопросов на тестовом аккаунте — ответы сверены.

Ещё не проверено вживую: документы КП/счёт/накладная, новый Telegram-бот, часть исправлений по оплате долга. Поэтому версия выходит как тестовая.

---

# Кыргызча

**Тесттик версия — тестировщиктер үчүн гана.** Кадимки кассалар аны тестирлөө бүткөнчө албайт. Тестировщик: «Жөндөөлөр → Жаңыртуулар → Тесттик версиялар», кодду киргизип → «Жаңыртууларды текшерүү» → «Жаңыртуу».

### Жаңы

- **Арзандатуу жана карыз менен кайтаруу.** Чекте арзандатуу болсо, сатып алуучу чындап төлөгөнү гана кайтарылат. Карызга сатууну кайтаруу карызды азайтат — накталай берилбейт. Чектин тарыхында «Кайтаруулар» бөлүгү.
- **Кайтаруу мөөнөтү менен карыз.** Адатта 30 күн; чекте «… чейин кайтаруу» басылат.
- **Карызды төлөө:** кардардын карыздарынын тизмеси, Enter, «Жабуу» алдында ырастоо.
- **Сменаны жабуу:** сатуулар жана кайтаруулар өзүнчө, «Күтүлөт N». **X-отчёт** — сменаны жаппай жыйынтык.
- **Жеткирүүчүлөр:** товардын карточкасында жеткирүүчү; кабыл алууда жеткирүүчүгө карыз жана аны төлөө.
- **Прокат** дүкөндүн бардык түрлөрүндө иштейт.
- **КП, эсеп жана накладной** ээсинин программасында, «Кардарлар → Документтер»: A4 алдын ала көрүү, PDF, Word, басып чыгаруу, WhatsApp.
- **Нур Кеңешчи** 1–3 секундда жооп берет, «сандар кайдан» жана «Excelге»; саат 9:00дө эртең мененки отчёт, карыздарга эскертүүлөр.
- **Telegram-бот:** ээси бир жолку код менен ырастайт, түшкөн акча серверден, тез жооптор, баскычтар менен меню. **Жаңыртуудан кийин топто ээси өзүн бир жолу код менен ырастоосу керек.**
- **Терезелердин жаңы көрүнүшү**, **караңгы тема** — баалар сары эмес.

### Мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Арзандатуулуу чектен кайтаруу | Толук баа берилчү | Арзандатууну эске алып төлөнгөн бөлүгү |
| Карызга сатууну кайтаруу | Накталай берүүнү сунуштачу | Карыз азаят, берилүүчү 0 |
| Карыздын мөөнөтү | Чекте жок | Чекте «… чейин кайтаруу» |
| Сменаны жабуу | Бир жалпы сумма | Сатуу жана кайтаруу өзүнчө |
| Нур Кеңешчинин жообу | ~25 секунд | 1–3 секунд |

---

# English

**Test version — for testers only.** Regular tills do not get it until testing is finished. Tester: “Settings → Updates → Test versions”, enter the tester code → “Check for updates” → “Update”.

### New

- **Returns with discount and debt.** With a receipt discount, only what the customer actually paid for the returned items is given back. Returning a debt sale reduces the debt — no cash is given. The receipt history has a “Returns” block.
- **Debt with a due date** (30 days by default); the receipt prints “Return by …”.
- **Paying a debt:** the client's debts in a list, Enter, a confirmation before “Pay off”.
- **Closing a shift:** sales and returns separately, “Expected N”. **X-report** — shift totals without closing it.
- **Suppliers:** the supplier in the product card; a supplier debt at receiving and paying it.
- **Rentals** work in every store type.
- **Quotes, invoices and waybills** in the owner app, “Clients → Documents”: A4 preview, PDF, Word, print, WhatsApp.
- **Nur Advisor** answers in 1–3 seconds, “where the numbers come from” and “To Excel”; a morning report at 9:00, reminders to debtors.
- **Telegram bot:** the owner confirms with a one-time code, revenue from the server, fast replies, a button menu. **After the update the owner must confirm once with a code in the group.**
- **New look of windows**, **dark theme** — prices are no longer yellow.

### Before → now

| What | Before | Now |
|---|---|---|
| Return from a discounted receipt | Full price was given back | The paid part with the discount |
| Return of a debt sale | Offered to give cash | The debt is reduced, 0 to give |
| Debt due date | Not on the receipt | “Return by …” on the receipt |
| Closing a shift | One total | Sales and returns separately |
| Nur Advisor reply | ~25 seconds | 1–3 seconds |

---

# Türkçe

**Test sürümü — yalnızca test kullanıcıları için.** Normal kasalar test bitene kadar almaz. Test kullanıcısı: «Ayarlar → Güncellemeler → Test sürümleri», kodu girin → «Güncellemeleri kontrol et» → «Güncelle».

### Yeni

- **İndirimli ve borçlu iade.** Fişte indirim varsa yalnızca müşterinin gerçekten ödediği geri verilir. Borçlu satışın iadesi borcu azaltır — nakit verilmez. Fiş geçmişinde «İadeler» bölümü.
- **Geri ödeme tarihli borç** (varsayılan 30 gün); fişe «… tarihine kadar iade» basılır.
- **Borç ödeme:** müşterinin borç listesi, Enter, «Kapat» öncesi onay.
- **Vardiya kapanışı:** satışlar ve iadeler ayrı, «Beklenen N». **X raporu** — vardiyayı kapatmadan özet.
- **Tedarikçiler:** ürün kartında tedarikçi; mal kabulde tedarikçiye borç ve ödemesi.
- **Kiralama** tüm mağaza türlerinde çalışır.
- **Teklif, fatura ve irsaliye** sahip programında, «Müşteriler → Belgeler»: A4 önizleme, PDF, Word, yazdırma, WhatsApp.
- **Nur Danışman** 1–3 saniyede yanıt verir, «rakamlar nereden» ve «Excel'e»; 9:00'da sabah raporu, borçlulara hatırlatmalar.
- **Telegram botu:** sahip tek kullanımlık kodla doğrular, ciro sunucudan, hızlı yanıtlar, düğmeli menü. **Güncellemeden sonra grupta sahip bir kez kodla doğrulamalıdır.**
- **Pencerelerin yeni görünümü**, **koyu tema** — fiyatlar artık sarı değil.

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| İndirimli fişten iade | Tam fiyat geri verilirdi | İndirimle ödenen kısım |
| Borçlu satışın iadesi | Nakit vermeyi önerirdi | Borç azalır, verilecek 0 |
| Borç vadesi | Fişte yoktu | Fişte «… tarihine kadar iade» |
| Vardiya kapanışı | Tek toplam | Satış ve iade ayrı |
| Nur Danışman yanıtı | ~25 saniye | 1–3 saniye |

---

# O'zbekcha

**Test versiyasi — faqat testerlar uchun.** Oddiy kassalar sinov tugamaguncha uni olmaydi. Tester: «Sozlamalar → Yangilanishlar → Test versiyalari», kodni kiriting → «Yangilanishlarni tekshirish» → «Yangilash».

### Yangi

- **Chegirma va qarz bilan qaytarish.** Chekda chegirma bo'lsa, xaridor haqiqatda to'lagani qaytariladi. Qarzga sotuvni qaytarish qarzni kamaytiradi — naqd berilmaydi. Chek tarixida «Qaytarishlar» bloki.
- **Qaytarish muddatli qarz** (odatda 30 kun); chekda «… gacha qaytarish» bosiladi.
- **Qarzni to'lash:** mijozning qarzlari ro'yxati, Enter, «Yopish» oldidan tasdiq.
- **Smenani yopish:** sotuv va qaytarish alohida, «Kutilmoqda N». **X-hisobot** — smenani yopmasdan yakun.
- **Yetkazib beruvchilar:** mahsulot kartasida yetkazib beruvchi; qabulda yetkazib beruvchiga qarz va uni to'lash.
- **Prokat** do'konning barcha turlarida ishlaydi.
- **Tijorat taklifi, hisob va nakladnoy** egasining dasturida, «Mijozlar → Hujjatlar»: A4 ko'rinish, PDF, Word, chop etish, WhatsApp.
- **Nur Maslahatchi** 1–3 soniyada javob beradi, «raqamlar qayerdan» va «Excelga»; 9:00 da ertalabki hisobot, qarzdorlarga eslatmalar.
- **Telegram-bot:** egasi bir martalik kod bilan tasdiqlaydi, tushum serverdan, tezkor javoblar, tugmali menyu. **Yangilanishdan keyin guruhda egasi bir marta kod bilan tasdiqlashi kerak.**
- **Oynalarning yangi ko'rinishi**, **qorong'i mavzu** — narxlar endi sariq emas.

### Avval → hozir

| Nima | Avval | Hozir |
|---|---|---|
| Chegirmali chekdan qaytarish | To'liq narx qaytarilardi | Chegirma bilan to'langan qism |
| Qarzga sotuvni qaytarish | Naqd berishni taklif qilardi | Qarz kamayadi, berilishi 0 |
| Qarz muddati | Chekda yo'q edi | Chekda «… gacha qaytarish» |
| Smenani yopish | Bitta umumiy summa | Sotuv va qaytarish alohida |
| Nur Maslahatchi javobi | ~25 soniya | 1–3 soniya |
