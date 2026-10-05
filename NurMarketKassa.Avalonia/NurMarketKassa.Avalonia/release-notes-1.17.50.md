## v1.17.50

Воронка продаж и WhatsApp в программе владельца, ИИ-советник голосом и с фото товаров, план продаж на месяц, ИИ в голосовом управлении кассой.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **«Воронка»** — новый раздел программы владельца. Сделки по этапам «Новые → В работе → Договорились → Оплачено / Отказ»:
  - карточку можно перетащить мышью в нужный этап (или кнопками ◀ ▶);
  - у сделки есть источник клиента — Telegram, WhatsApp, Instagram, звонок, пришёл в магазин, по рекомендации; по источнику есть фильтр и итоги;
  - из карточки — переписка с клиентом в WhatsApp и Instagram; «Подтянуть из бота» — покупатели Telegram-бота за 30 дней.
- **«WhatsApp»** — WhatsApp Web прямо в программе владельца (вход по QR-коду один раз).
- **ИИ-советник:**
  - голосовой чат — кнопка микрофона: нажали, сказали, нажали ещё раз; ответ читается вслух;
  - «Найди фото для товаров без фото» — фото по штрихкоду из открытых баз товаров, «Поставить» / «Поставить все»; для ненайденных — кнопка, чтобы выбрать снимок;
  - «Какие акции запустить?» — анализ продаж, склада, клиентов и заказов и конкретные акции на проблемные товары;
  - включает и выключает функции Telegram-бота — только после вашей кнопки «Применить».
- **«План продаж на месяц»** — карточка в «Сводке»: цель, сколько продано, сколько нужно продавать в день и успеваете ли.
- **Голосовое управление кассой с ИИ:** фразу, которую касса не поняла или по которой не нашла товар, разбирает ИИ (например, «касса кока кола две» → Coca-Cola × 2). Можно спросить: «касса, сколько стоит пепси» — касса покажет цену и остаток. Без ключа ИИ и без интернета всё работает как раньше.
- **Подсказки «С этим часто берут»** со всех касс собираются на сервере NurCRM; **ошибки программы** автоматически уходят в поддержку (без паролей и токенов) — команда видит сбои раньше, чем вы о них напишете.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Сделки с клиентами | Нигде | «Воронка» с этапами, источником и перетаскиванием |
| WhatsApp магазина | Отдельно в браузере или телефоне | Раздел в программе владельца |
| Фото товаров на складе | Каждое вручную | ИИ-советник находит по штрихкоду и ставит после вашего «Поставить» |
| Вопрос ИИ-советнику | Только текстом | Ещё и голосом, ответ вслух |
| Цель на месяц | Нигде | «План продаж на месяц» в «Сводке» |
| Голос: касса не поняла фразу | «Товар не найден» | Фразу разбирает ИИ |
| Ошибки программы | Только по кнопке «Отправить в поддержку» | Ещё и автоматически в поддержку |

### Проверено (05.10, тестовый аккаунт)

- Касса: 30 продаж подряд (наличные, безнал, два товара) — 30 из 30, оплата 0,98–1,66 с; продажа в долг, оплата долга, внесение и изъятие, возврат чека.
- Программа владельца: все разделы меню открываются без ошибок; «Воронка» — новая сделка, источник, перетаскивание мышью, удаление; ИИ-советник — найдено и поставлено фото товара, выключение и включение функции бота; «План продаж» — сохранён и показан.
- Подсказки допродажи: касса отправила журнал на сервер, сервер принял.

Не проверено: голосовые функции живой речью.

---

# Кыргызча

### Жаңы

- **«Воронка»** — ээсинин программасындагы жаңы бөлүм. Келишимдер этаптар боюнча: «Жаңы → Иште → Макулдашылды → Төлөндү / Баш тартуу»; карточканы чычкан менен сүйрөөгө болот; кардардын булагы (Telegram, WhatsApp, Instagram, чалуу…), булак боюнча фильтр жана жыйынтык; карточкадан WhatsApp жана Instagram аркылуу кат алышуу.
- **«WhatsApp»** — WhatsApp Web ээсинин программасынын ичинде (QR-код менен бир жолу кирүү).
- **ИИ-кеңешчи:** үн менен маек (микрофон баскычы, жооп үн менен угулат); товарлардын сүрөтүн штрихкод боюнча табат жана «Коюу» дегенде кампага коёт; көйгөйлүү товарларга акция сунуштайт; боттун функцияларын «Колдонуу» дегенден кийин гана күйгүзүп-өчүрөт.
- **«Айлык сатуу планы»** — «Жыйынтыкта» карточка: максат, канча сатылды, күнүнө канча керек жана үлгүрүп жатасызбы.
- **ИИ менен кассаны үн аркылуу башкаруу:** касса түшүнбөгөн сөздү ИИ талдайт; «касса, пепси канча турат» деп сураса болот.
- Бардык кассалардын **«Муну менен көп алышат»** кеңештери серверге чогулат; **программанын каталары** колдоого өзү кетет (сырсөз жана токенсиз).

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| Кардарлар менен келишимдер | Эч жерде | «Воронка» |
| Дүкөндүн WhatsApp'ы | Өзүнчө браузерде же телефондо | Ээсинин программасында |
| Товарлардын сүрөтү | Ар бирин кол менен | ИИ-кеңешчи штрихкод боюнча табат |
| ИИ-кеңешчиге суроо | Жазуу менен гана | Үн менен да |
| Айлык максат | Эч жерде | «Айлык сатуу планы» |
| Касса үн буйругун түшүнбөсө | «Товар табылган жок» | ИИ талдайт |
| Программанын каталары | «Колдоого жиберүү» баскычы менен гана | Колдоого өзү да кетет |

### Текшерилди (05.10, сыноо аккаунту)

Касса: катары менен 30 сатуу — 30дан 30; карызга сатуу, карызды төлөө, акча салуу жана алуу, кайтаруу. Ээсинин программасы: бардык бөлүмдөр; «Воронка», ИИ-кеңешчи (сүрөт, бот), «Айлык сатуу планы». Үн функциялары тирүү сөз менен текшерилген жок.

---

# English

### New

- **“Funnel”** — a new owner program section. Deals by stage “New → In progress → Agreed → Paid / Lost”: drag a card with the mouse; a client source on each deal (Telegram, WhatsApp, Instagram, phone call…) with a filter and totals; chat on WhatsApp and Instagram right from the card; “Import from bot” — Telegram bot customers for 30 days.
- **“WhatsApp”** — WhatsApp Web inside the owner program (sign in with a QR code once).
- **AI advisor:** voice chat (microphone button, the answer is read aloud); finds product photos by barcode and puts them on the cards after you press “Set”; suggests promotions for problem products; turns Telegram bot features on and off only after you press “Apply”.
- **“Monthly sales plan”** — a card in the Overview: the target, sold so far, needed per day and whether you are on track.
- **Voice control of the till with AI:** a phrase the till did not understand is parsed by AI; ask “till, how much is Pepsi” to see the price and stock.
- **“Often bought with”** suggestions from all tills are collected on the NurCRM server; **program errors** are sent to support automatically (without passwords or tokens).

### Before → after

| What | Before | After |
|---|---|---|
| Deals with clients | Nowhere | “Funnel” |
| The shop's WhatsApp | Separately in a browser or phone | A section in the owner program |
| Product photos | Each one by hand | The AI advisor finds them by barcode |
| Asking the AI advisor | Text only | Also by voice |
| Monthly target | Nowhere | “Monthly sales plan” |
| The till did not understand a voice command | “Product not found” | AI parses it |
| Program errors | Only with the “Send to support” button | Also sent to support automatically |

### Checked (05.10, test account)

Till: 30 sales in a row — 30 of 30; a debt sale, debt payment, cash in and out, a return. Owner program: all sections; Funnel, AI advisor (photo, bot), Monthly sales plan. Voice features were not checked with live speech.

---

# Türkçe

### Yeni

- **«Huni»** — sahip programında yeni bölüm. Aşamalara göre anlaşmalar «Yeni → Görüşülüyor → Anlaşıldı → Ödendi / Kaybedildi»: kart fareyle sürüklenir; her anlaşmada müşteri kaynağı (Telegram, WhatsApp, Instagram, telefon…), kaynağa göre filtre ve toplamlar; karttan WhatsApp ve Instagram yazışması.
- **«WhatsApp»** — WhatsApp Web sahip programının içinde (QR koduyla bir kez giriş).
- **Yapay zekâ danışmanı:** sesli sohbet (mikrofon düğmesi, yanıt sesli okunur); ürün fotoğraflarını barkoddan bulur ve «Ayarla» dediğinizde karta koyar; sorunlu ürünler için kampanya önerir; bot özelliklerini yalnızca «Uygula»dan sonra açıp kapatır.
- **«Aylık satış planı»** — Özet'te kart: hedef, satılan, günde gereken ve yetişip yetişmediğiniz.
- **Yapay zekâ ile kasayı sesle yönetme:** kasanın anlamadığı cümleyi yapay zekâ çözer; «kasa, Pepsi ne kadar» diye sorulabilir.
- Tüm kasaların **«Bununla sık alınır»** önerileri NurCRM sunucusunda toplanır; **program hataları** desteğe kendiliğinden gider (şifre ve token olmadan).

### Önce → sonra

| Ne | Önce | Sonra |
|---|---|---|
| Müşteri anlaşmaları | Hiçbir yerde | «Huni» |
| Mağazanın WhatsApp'ı | Ayrı tarayıcıda veya telefonda | Sahip programında |
| Ürün fotoğrafları | Tek tek elle | Yapay zekâ barkoddan bulur |
| Danışmana soru | Yalnızca yazıyla | Sesle de |
| Aylık hedef | Hiçbir yerde | «Aylık satış planı» |
| Kasa sesli komutu anlamadı | «Ürün bulunamadı» | Yapay zekâ çözer |
| Program hataları | Yalnızca «Desteğe gönder» düğmesiyle | Desteğe kendiliğinden de gider |

### Kontrol edildi (05.10, test hesabı)

Kasa: art arda 30 satış — 30'da 30; veresiye satış, borç ödeme, para girişi ve çıkışı, iade. Sahip programı: tüm bölümler; Huni, yapay zekâ danışmanı (fotoğraf, bot), Aylık satış planı. Sesli özellikler canlı konuşmayla kontrol edilmedi.

---

# O'zbekcha

### Yangi

- **«Voronka»** — ega dasturidagi yangi bo'lim. Bosqichlar bo'yicha bitimlar «Yangi → Jarayonda → Kelishildi → To'landi / Rad etildi»: kartani sichqoncha bilan sudrash mumkin; har bir bitimda mijoz manbasi (Telegram, WhatsApp, Instagram, qo'ng'iroq…), manba bo'yicha filtr va jami; kartadan WhatsApp va Instagram yozishmasi.
- **«WhatsApp»** — WhatsApp Web ega dasturining ichida (QR-kod bilan bir marta kirish).
- **SI maslahatchi:** ovozli suhbat (mikrofon tugmasi, javob ovoz bilan o'qiladi); mahsulot rasmlarini shtrix-kod bo'yicha topadi va «O'rnatish»ni bosganingizda kartaga qo'yadi; muammoli mahsulotlarga aksiya taklif qiladi; bot funksiyalarini faqat «Qo'llash»dan keyin yoqib-o'chiradi.
- **«Oylik savdo rejasi»** — «Umumiy ko'rinish»da karta: maqsad, sotilgani, kuniga kerakligi va ulgurayapsizmi.
- **SI bilan kassani ovoz orqali boshqarish:** kassa tushunmagan gapni SI tahlil qiladi; «kassa, pepsi qancha turadi» deb so'rash mumkin.
- Barcha kassalarning **«Bu bilan ko'p olishadi»** tavsiyalari NurCRM serverida yig'iladi; **dastur xatolari** yordamga o'zi ketadi (parol va tokensiz).

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Mijozlar bilan bitimlar | Hech qayerda | «Voronka» |
| Do'konning WhatsApp'i | Alohida brauzerda yoki telefonda | Ega dasturida |
| Mahsulot rasmlari | Har birini qo'lda | SI shtrix-kod bo'yicha topadi |
| Maslahatchiga savol | Faqat yozib | Ovoz bilan ham |
| Oylik maqsad | Hech qayerda | «Oylik savdo rejasi» |
| Kassa ovozli buyruqni tushunmadi | «Mahsulot topilmadi» | SI tahlil qiladi |
| Dastur xatolari | Faqat «Yordamga yuborish» tugmasi bilan | Yordamga o'zi ham ketadi |

### Tekshirildi (05.10, sinov akkaunti)

Kassa: ketma-ket 30 ta sotuv — 30 dan 30; qarzga sotuv, qarzni to'lash, pul kiritish va olish, qaytarish. Ega dasturi: barcha bo'limlar; «Voronka», SI maslahatchi (rasm, bot), «Oylik savdo rejasi». Ovozli funksiyalar jonli nutq bilan tekshirilmadi.
