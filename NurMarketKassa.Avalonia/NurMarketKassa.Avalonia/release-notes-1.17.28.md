## v1.17.28

Изменения с прошлой версии (1.17.27).

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Товар с акцией NurCRM (Адыгене 1л, акция −7,50) | Окно оплаты: 50,00 — кассир брал 50,00, а сервер проводил продажу на 42,50 | Окно оплаты: 42,50, сервер проводит 42,50, скидка 7,50 видна в чеке и в Z-отчёте |
| Сервер провёл продажу на другую сумму | Касса молчала | Касса сразу предупреждает кассира |
| «Финансы», «Продажи», «Аналитика» | Цифры могли не совпадать с сайтом; месяц большого магазина обрезался на 4 800 чеках | Выручка, чеки, оплаты, возвраты, скидки, прибыль и топ товаров — как на сайте NurCRM |
| Магазин с 12 000 чеков в месяц: открыть «Финансы» за месяц | Окно замирало на 23–42 с, за год — до 14 минут | Плитки через 0,7 с, окно не замирает дольше 1 с, список догружается в фоне |
| Телеграм-бот: отчёт длиннее 4 096 символов (/dolgi, /ostatki) | Telegram отклонял его целиком — владелец не получал ничего | Приходит частями |
| Телеграм-бот под нагрузкой (много сообщений сразу, рассылка должникам) | Часть ответов и напоминаний терялась (ответ Telegram «слишком часто») | Бот ждёт и повторяет — доходят все |
| Телеграм-бот: первая команда в течение 25 с после включения кассы | Молча пропускалась | Выполняется |
| Группа владельца в Telegram стала супергруппой | Сводки уходили в старый чат, команды из новой группы не принимались | Бот сам переходит на новую группу |

### Новое

- **Настройки → Весы переделаны:** вкладки «Весы на кассе», «Весы с этикетками», «Штрих-код: вес / сумма», «Табло цены». Мастер **«Настроить по этикетке»**: отсканируйте этикетку — касса покажет, где вес, где сумма, и запомнит правило. Несколько весов в магазине, у каждых — свои марка, адрес и категории товаров.
- **Отправка товаров на весы:** клавиши быстрого доступа Штрих-ПРИНТ (1–120), фильтр по категории, колонка «Результат» по каждому товару.
- **Настройки — вкладками сверху**, помещаются и на маленьких экранах. Вид кассы выбирается в Маркетплейсе → «Виды кассы».
- **База знаний:** более 30 новых статей про весы (подключение, штрихкод, частые проблемы); разделы сворачиваются.
- «Обновить» в окнах аналитики всегда берёт свежие цифры сервера.

### Проверено

- Установлено на рабочий компьютер, тестовый аккаунт NurCRM: 11 продаж подряд (наличные и перевод) — все прошли, сервер показал ровно те же суммы (день: 1 192,50, 24 чека; наличные 792,50, безнал 400,00).
- Товар с акцией: окно оплаты 42,50 → на сервере 42,50, скидка 7,50.
- Закрытие смены: Z-отчёт 10 продаж, 492,50 (наличные 342,50, безнал 150,00, скидки 7,50) — совпадает с сервером; новая смена открылась, продажи в ней прошли.
- «Финансы» и «Продажи» за сентябрь: 4 353 050,61, 791 чек, наличные 4 068 995,08, скидки 11 493,82 — до копейки как на сервере; окно загрузилось за 4 с.
- Большой магазин на испытательном стенде (11 909 чеков за месяц, 42 092 за квартал): окно не замирало дольше 1,1 с.
- Телеграм-бот на стенде с поддельным Telegram: 1 000 чатов, всплеск 100 сообщений в секунду, 5 000 должников, обрыв сети, сбои 502 — ответы не теряются; 30 минут работы — память не растёт.

### Не проверено

- Телеграм-бот с настоящим Telegram — на рабочем компьютере бот не подключён.
- Весы с настоящим железом после переделки настроек.

---

# Кыргызча

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| NurCRM акциясы бар товар (Адыгене 1л, акция −7,50) | Төлөм терезеси 50,00 — кассир 50,00 алчу, сервер 42,50 өткөрчү | Терезе 42,50, сервер 42,50, арзандатуу 7,50 чекте жана Z-отчётто |
| Сервер сатууну башка суммага өткөрдү | Касса унчукчу эмес | Касса дароо эскертет |
| «Каржы», «Сатуулар», «Аналитика» | Сандар сайттан айырмаланчу; чоң дүкөндүн айы 4 800 чекте кесилчү | Түшүм, чектер, төлөмдөр, кайтаруулар, арзандатуулар, пайда жана топ — NurCRM сайтындагыдай |
| Айына 12 000 чек: «Каржыны» ай үчүн ачуу | Терезе 23–42 с катып калчу, жылга — 14 мүнөткө чейин | Плиткалар 0,7 с, терезе 1 секунддан ашык катпайт |
| Телеграм-бот: 4 096 белгиден узун отчёт | Telegram толугу менен четке кагчу | Бөлүктөп келет |
| Телеграм-бот жүктөмдө | Жооптордун жана эскертмелердин бир бөлүгү жоголчу | Баары жетет |
| Касса күйгөндөн кийинки 25 с ичиндеги биринчи буйрук | Үнсүз өткөрүлүп жиберилчү | Аткарылат |

### Жаңы

- **Жөндөөлөр → Таразалар жаңыланды:** «Кассадагы тараза», «Этикеткалуу тараза», «Штрих-код: салмак / сумма» өтмөктөрү, «Этикетка боюнча жөндөө» устасы, категориялары менен бир нече тараза.
- Таразага жөнөтүү: Штрих-ПРИНТ ыкчам баскычтары (1–120), категория чыпкасы, ар бир товардын жыйынтыгы.
- Жөндөөлөр — өйдөдө өтмөктөр менен; кассанын түрү Маркетплейсте → «Кассанын түрлөрү». Билим базасында таразалар жөнүндө 30дан ашык жаңы макала.

### Текшерилди

- Тесттик аккаунтта 11 сатуу — сервер ошол эле суммаларды көрсөттү (күн: 1 192,50, 24 чек). Акциядагы товар: терезе 42,50 → серверде 42,50.
- Z-отчёт: 10 сатуу, 492,50 — сервер менен дал келет. Сентябрь: 4 353 050,61, 791 чек — сервердегидей.
- Телеграм-бот сыноо стендинде: 1 000 чат, секундасына 100 билдирүү, 5 000 карыздар — жооптор жоголбойт.

---

# English

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| NurCRM promo product (Adygene 1 l, promo −7.50) | Payment window 50.00 — the cashier took 50.00, the server recorded 42.50 | Window 42.50, server 42.50, the 7.50 discount is on the receipt and the Z report |
| Server recorded the sale with another amount | The till said nothing | The till warns the cashier at once |
| “Finance”, “Sales”, “Analytics” | Figures could differ from the website; a big store's month was cut at 4,800 receipts | Revenue, receipts, payments, returns, discounts, profit and top products as on the NurCRM website |
| 12,000 receipts a month: open “Finance” for the month | The window froze for 23–42 s, for a year — up to 14 min | Tiles in 0.7 s, no freeze longer than 1 s, the list loads in the background |
| Telegram bot: report longer than 4,096 characters | Rejected by Telegram entirely | Arrives in parts |
| Telegram bot under load | Some replies and debt reminders were lost | All are delivered |
| First command within 25 s after the till starts | Silently skipped | Executed |

### New

- **Settings → Scales redesigned:** tabs “Till scales”, “Label scales”, “Barcode: weight / total”, the “Set up from a label” wizard, several scales with categories.
- Sending to scales: Shtrikh-PRINT hot keys (1–120), category filter, result for each product.
- Settings as tabs at the top; the till layout is chosen in Marketplace → “Till layouts”. 30+ new knowledge base articles on scales.

### Checked

- 11 sales on the test account — the server shows the same amounts (day: 1,192.50, 24 receipts). Promo product: window 42.50 → server 42.50.
- Z report: 10 sales, 492.50 — matches the server. September: 4,353,050.61, 791 receipts — as on the server.
- Telegram bot on a test stand: 1,000 chats, 100 messages per second, 5,000 debtors — no replies lost.

---

# Türkçe

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| NurCRM kampanyalı ürün (Adygene 1 l, kampanya −7,50) | Ödeme penceresi 50,00 — kasiyer 50,00 alıyordu, sunucu 42,50 kaydediyordu | Pencere 42,50, sunucu 42,50, 7,50 indirim fişte ve Z raporunda |
| Sunucu satışı farklı tutarla kaydetti | Kasa bir şey demiyordu | Kasa kasiyeri hemen uyarır |
| «Finans», «Satışlar», «Analitik» | Rakamlar siteden farklı olabiliyordu; büyük mağazanın ayı 4.800 fişte kesiliyordu | Ciro, fişler, ödemeler, iadeler, indirimler, kâr ve en çok satanlar NurCRM sitesindeki gibi |
| Ayda 12.000 fiş: «Finans»ı ay için açmak | Pencere 23–42 sn donuyordu, yıl için 14 dakikaya kadar | Kutucuklar 0,7 sn’de, 1 sn’den uzun donma yok |
| Telegram botu: 4.096 karakterden uzun rapor | Telegram tamamen reddediyordu | Parça parça gelir |
| Telegram botu yoğunlukta | Bazı yanıtlar ve hatırlatmalar kayboluyordu | Hepsi ulaşır |
| Kasa açıldıktan sonraki 25 sn içindeki ilk komut | Sessizce atlanıyordu | Çalıştırılır |

### Yeni

- **Ayarlar → Teraziler yenilendi:** «Kasa terazisi», «Etiketli terazi», «Barkod: ağırlık / tutar» sekmeleri, «Etikete göre ayarla» sihirbazı, kategorili birden çok terazi.
- Teraziye gönderme: Shtrikh-PRINT kısayol tuşları (1–120), kategori filtresi, her ürün için sonuç.
- Ayarlar üstte sekmeler halinde; kasa görünümü Market → «Kasa görünümleri» içinden seçilir. Bilgi bankasında teraziler hakkında 30’dan fazla yeni makale.

### Kontrol edildi

- Test hesabında 11 satış — sunucu aynı tutarları gösterdi (gün: 1.192,50, 24 fiş). Kampanyalı ürün: pencere 42,50 → sunucuda 42,50.
- Z raporu: 10 satış, 492,50 — sunucuyla aynı. Eylül: 4.353.050,61, 791 fiş — sunucudaki gibi.
- Telegram botu test ortamında: 1.000 sohbet, saniyede 100 mesaj, 5.000 borçlu — yanıt kaybı yok.

---

# O'zbekcha

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| NurCRM aksiyali mahsulot (Adygene 1 l, aksiya −7,50) | To'lov oynasi 50,00 — kassir 50,00 olardi, server 42,50 o'tkazardi | Oyna 42,50, server 42,50, 7,50 chegirma chekda va Z hisobotda |
| Server savdoni boshqa summa bilan o'tkazdi | Kassa jim edi | Kassa kassirni darhol ogohlantiradi |
| «Moliya», «Savdolar», «Tahlil» | Raqamlar saytdan farq qilishi mumkin edi; katta do'konning oyi 4 800 chekda kesilardi | Tushum, cheklar, to'lovlar, qaytarishlar, chegirmalar, foyda va top — NurCRM saytidagidek |
| Oyiga 12 000 chek: «Moliya»ni oy uchun ochish | Oyna 23–42 s qotib qolardi, yil uchun — 14 daqiqagacha | Plitkalar 0,7 s da, 1 soniyadan uzoq qotish yo'q |
| Telegram-bot: 4 096 belgidan uzun hisobot | Telegram butunlay rad etardi | Qismlarga bo'lib keladi |
| Telegram-bot yuklamada | Javob va eslatmalarning bir qismi yo'qolardi | Hammasi yetib boradi |
| Kassa yoqilgandan keyingi 25 s ichidagi birinchi buyruq | Jimgina o'tkazib yuborilardi | Bajariladi |

### Yangi

- **Sozlamalar → Tarozilar yangilandi:** «Kassadagi tarozi», «Yorliqli tarozi», «Shtrix-kod: vazn / summa» varaqlari, «Yorliq bo'yicha sozlash» ustasi, toifalari bilan bir nechta tarozi.
- Taroziga yuborish: Shtrix-PRINT tezkor tugmalari (1–120), toifa filtri, har bir mahsulot natijasi.
- Sozlamalar tepada varaqlar bilan; kassa ko'rinishi Marketpleysda → «Kassa ko'rinishlari». Bilim bazasida tarozilar haqida 30 dan ortiq yangi maqola.

### Tekshirildi

- Test akkauntida 11 ta savdo — server xuddi shu summalarni ko'rsatdi (kun: 1 192,50, 24 chek). Aksiyali mahsulot: oyna 42,50 → serverda 42,50.
- Z hisobot: 10 ta savdo, 492,50 — server bilan mos. Sentabr: 4 353 050,61, 791 chek — serverdagidek.
- Telegram-bot sinov stendida: 1 000 chat, soniyasiga 100 xabar, 5 000 qarzdor — javoblar yo'qolmaydi.
