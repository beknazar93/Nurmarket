## v1.17.38

Телеграм-бот и ИИ на сервере NurCRM, исправления печати чеков и этикеток, денежного ящика, весов и прав сотрудников, раздел «Прибыль и деньги» в программе владельца и подсказка допродажи «С этим часто берут».

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки: название и адрес магазина, принтеры, весы и всё, что вы задали, остаются как были. Новая подсказка допродажи по умолчанию выключена.

---

# Русский

### Новое

- **Телеграм-бот и ИИ на сервере NurCRM.** Бот отвечает круглые сутки, даже когда касса и компьютер выключены: команды, отчёты, ИИ, консультант для покупателей, заказы и сводка по закрытию смены. После обновления программа один раз спросит, перенести ли бота на сервер (без вашего согласия токен и ключ ИИ никуда не уходят). Перенести позже или вернуть обратно — **Настройки → Операции → «Подключить бота»**.
- **Подсказка «С этим часто берут».** Над итогом чека касса предлагает один товар, который покупатели чаще всего берут вместе с товарами из чека. Касса считает это по своим чекам за 90 дней и предлагает только то, что есть на складе. «Добавить» кладёт товар в чек (с выбором размера, пачки или веса, как из каталога), «Пропустить» — больше не предлагать в этом чеке. Включается в **Настройки → Экран → «Подсказки допродажи»**.
- **Раздел «Прибыль и деньги» в программе владельца** (Финансы): прибыль (выручка, себестоимость, расходы), движение денег (пришло / ушло по способам оплаты) и сверка кассы за сегодня, 7 дней, месяц или 3 месяца.
- **Чековый принтер:** в Настройки → Печать можно задать «Символов в строке» и ширину графики (в точках) отдельно для ленты 58 и 80 мм. Пробная печать сразу показывает результат.
- **Принтер этикеток:** размер наклейки берётся из шаблона, в редакторе этикетки настраиваются зазор между наклейками, сдвиг по горизонтали и вертикали и язык принтера (ESC/POS или TSPL).
- **Калькуляция:** кнопка «Обновить · время» — перечитывает товары с сервера.
- **«Подробнее»** у товара видно в любой сфере магазина, если продавец заполнил описание.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Текст на чеке 58/80 мм | Мог уходить за край ленты | «Символов в строке» и ширина графики настраиваются |
| Принтер этикеток | Пропускал наклейки, печатал не на месте | Размер из шаблона, зазор и сдвиг настраиваются |
| Денежный ящик | Открылся один раз и перестал | Открывается при каждой оплате наличными (принтер сбрасывается перед сигналом) |
| Горячие клавиши весов | Назначенные товары сами слетали | Назначения сохраняются, даже если товар временно не виден в списке |
| Права сотрудника | Разделы открывались не по правам сайта | «Склад», «Аналитика», «Финансы», «Клиенты», «Заказы» — строго по галочкам на сайте NurCRM |
| Калькуляция | Показывала старые цены и остатки | Обновляется сама и по кнопке «Обновить» |
| «Подробнее» на плитке с фото | Кнопку закрывало фото | Кнопка видна поверх фото |
| Продажа в долг с предоплатой | Предоплата наличными попадала в отчёт смены неточно | Наличные сразу учитываются в кассе и в отчёте смены |
| Сводка смены в Telegram | — | Если бот перенесён на сервер NurCRM, сводку шлёт сервер (без дублей) |

### Проверено (тестовый аккаунт, 01–02.10)

- Продажа наличными (получено 50, сдача 10), безналичная продажа, продажа в долг с предоплатой 30 из 80 — на сервере: оплата 30 наличными + 50 в долг на клиента, повторного платежа нет.
- Закрытие смены: ожидаемые наличные в кассе 130 = продажи наличными 100 + предоплата 30, расхождение 0.
- Импульс денежного ящика пишется в журнал при каждой оплате наличными.
- Подсказка допродажи: показ, «Добавить» (товар в чеке, следующая подсказка), «Пропустить» (товар больше не предлагается в этом чеке), журнал показов.
- Кнопка «Подробнее» на плитках с фото, Калькуляция с кнопкой «Обновить», «Символов в строке» в настройках печати, раздел «Прибыль и деньги».

### Не проверено

- Настоящие принтеры чеков и этикеток, денежный ящик и весы — на этом компьютере их нет. Если что-то печатается не так — пришлите фото чека или наклейки и снимок настроек.
- Права сотрудников — проверены по правилам сайта, но не под логином каждого вида сотрудника.
- Бот на сервере NurCRM отвечает медленнее, чем с компьютера; ускорение — на стороне сервера NurCRM. Если бот долго молчит — «Вернуть на этот компьютер» в том же окне.

---

# Кыргызча

### Жаңы

- **Телеграм-бот жана ЖИ NurCRM серверинде.** Касса жана компьютер өчүк болсо да бот күнү-түнү жооп берет. Жаңыргандан кийин программа бир жолу сурайт; кийин — **Жөндөөлөр → Операциялар → «Ботту туташтыруу»**.
- **«Муну менен көп алышат» кеңеши.** Чектин жыйынтыгынын үстүндө касса чектеги товарлар менен көбүнчө бирге алынган бир товарды сунуштайт (кассанын 90 күндүк чектери боюнча, кампада бары гана). «Кошуу» — товарды чекке кошот, «Өткөрүү» — бул чекте мындан ары сунушталбайт. **Жөндөөлөр → Экран → «Кошумча сатуу кеңештери»** бөлүмүндө күйгүзүлөт (демейки боюнча өчүк).
- **Ээсинин программасында «Пайда жана акча» бөлүмү:** пайда, акчанын кыймылы жана кассаны салыштыруу — бүгүн, 7 күн, ай же 3 ай.
- **Чек принтери:** 58 жана 80 мм үчүн «Саптагы белгилер» жана графиканын туурасы өзүнчө жөндөлөт.
- **Этикетка принтери:** чаптаманын өлчөмү шаблондон, аралык, жылыш жана принтер тили (ESC/POS же TSPL) жөндөлөт.
- **Калькуляция:** «Жаңыртуу» баскычы.

### Оңдолду

- Акча кутусу ар бир накталай төлөмдө ачылат.
- Таразалардын ыкчам баскычтарындагы товарлар түшүп калбайт.
- Кызматкерлердин укуктары NurCRM сайтындагы белгилер боюнча гана.
- Сүрөттүү плиткада «Толугураак» баскычы көрүнөт.
- Алдын ала төлөм менен карызга сатууда накталай кассага жана смена отчётуна туура түшөт.

Тесттик аккаунтта текшерилди: накталай, накталай эмес, алдын ала төлөм менен карызга сатуу, сменаны жабуу (айырма 0), кошумча сатуу кеңеши. Чыныгы принтерлер, акча кутусу жана таразалар бул компьютерде текшерилген жок.

---

# English

### New

- **Telegram bot and AI on the NurCRM server.** The bot answers around the clock, even when the till and computer are off. After the update the app asks once; later — **Settings → Operations → “Connect bot”**.
- **“Often bought with this” suggestion.** Above the receipt total, the till suggests one product that customers most often buy together with the items in the receipt (from this till's receipts for 90 days, in-stock items only). “Add” puts it in the receipt, “Skip” stops suggesting it in this receipt. Turn on in **Settings → Screen → “Upsell suggestions”** (off by default).
- **“Profit and money” section in the owner app:** profit, cash flow and till reconciliation for today, 7 days, a month or 3 months.
- **Receipt printer:** “Characters per line” and graphics width are set separately for 58 and 80 mm paper.
- **Label printer:** label size from the template; gap, offset and printer language (ESC/POS or TSPL) are adjustable.
- **Costing:** “Refresh” button.

### Fixed

- The cash drawer opens on every cash payment.
- Products assigned to scale hotkeys no longer drop off.
- Employee permissions follow the NurCRM website checkboxes only.
- The “Details” button is visible on tiles with photos.
- On a credit sale with a down payment, the cash goes correctly into the till and the shift report.

Verified on a test account: cash, card/transfer, credit sale with down payment, shift close (difference 0), upsell suggestion. Real printers, cash drawer and scales were not tested on this computer.

---

# Türkçe

### Yeni

- **Telegram botu ve yapay zekâ NurCRM sunucusunda.** Kasa ve bilgisayar kapalıyken bile bot günün her saati yanıt verir. Güncellemeden sonra program bir kez sorar; sonra — **Ayarlar → İşlemler → «Botu bağla»**.
- **«Bununla sık alınanlar» önerisi.** Fiş toplamının üstünde kasa, fişteki ürünlerle en sık birlikte alınan bir ürünü önerir (bu kasanın 90 günlük fişlerine göre, yalnızca stokta olanlar). «Ekle» ürünü fişe ekler, «Geç» bu fişte artık önermez. **Ayarlar → Ekran → «Ek satış önerileri»** bölümünden açılır (varsayılan olarak kapalı).
- **Sahip programında «Kâr ve para» bölümü:** bugün, 7 gün, bir ay veya 3 ay için kâr, nakit akışı ve kasa mutabakatı.
- **Fiş yazıcısı:** 58 ve 80 mm için «Satırdaki karakter sayısı» ve grafik genişliği ayrı ayarlanır.
- **Etiket yazıcısı:** etiket boyutu şablondan; boşluk, kaydırma ve yazıcı dili (ESC/POS veya TSPL) ayarlanır.
- **Maliyet:** «Yenile» düğmesi.

### Düzeltildi

- Para çekmecesi her nakit ödemede açılır.
- Terazi kısayol tuşlarına atanan ürünler artık düşmüyor.
- Çalışan yetkileri yalnızca NurCRM sitesindeki işaretlere göre.
- Fotoğraflı kartlarda «Ayrıntılar» düğmesi görünür.
- Ön ödemeli veresiye satışta nakit kasaya ve vardiya raporuna doğru geçer.

Test hesabında doğrulandı: nakit, nakitsiz, ön ödemeli veresiye satış, vardiya kapanışı (fark 0), ek satış önerisi. Gerçek yazıcılar, para çekmecesi ve teraziler bu bilgisayarda test edilmedi.

---

# O‘zbekcha

### Yangi

- **Telegram bot va SI NurCRM serverida.** Kassa va kompyuter o'chiq bo'lsa ham bot kecha-kunduz javob beradi. Yangilanishdan keyin dastur bir marta so'raydi; keyin — **Sozlamalar → Operatsiyalar → «Botni ulash»**.
- **«Bu bilan ko'p olishadi» maslahati.** Chek jamining ustida kassa chekdagi mahsulotlar bilan eng ko'p birga olinadigan bitta mahsulotni taklif qiladi (shu kassaning 90 kunlik cheklari bo'yicha, faqat omborda borlari). «Qo'shish» uni chekka qo'shadi, «O'tkazib yuborish» — bu chekda boshqa taklif qilinmaydi. **Sozlamalar → Ekran → «Qo'shimcha sotuv maslahatlari»** bo'limida yoqiladi (sukut bo'yicha o'chiq).
- **Egasi dasturida «Foyda va pul» bo'limi:** bugun, 7 kun, oy yoki 3 oy uchun foyda, pul harakati va kassani solishtirish.
- **Chek printeri:** 58 va 80 mm uchun «Qatordagi belgilar» va grafika kengligi alohida sozlanadi.
- **Yorliq printeri:** yorliq o'lchami shablondan; oraliq, siljish va printer tili (ESC/POS yoki TSPL) sozlanadi.
- **Kalkulyatsiya:** «Yangilash» tugmasi.

### Tuzatildi

- Pul qutisi har bir naqd to'lovda ochiladi.
- Tarozi tezkor tugmalariga biriktirilgan mahsulotlar endi tushib qolmaydi.
- Xodimlar huquqlari faqat NurCRM saytidagi belgilarga ko'ra.
- Rasmli kartalarda «Batafsil» tugmasi ko'rinadi.
- Oldindan to'lov bilan nasiyaga sotishda naqd pul kassaga va smena hisobotiga to'g'ri tushadi.

Test akkauntida tekshirildi: naqd, naqdsiz, oldindan to'lov bilan nasiyaga sotish, smenani yopish (farq 0), qo'shimcha sotuv maslahati. Haqiqiy printerlar, pul qutisi va tarozilar bu kompyuterda tekshirilmadi.
