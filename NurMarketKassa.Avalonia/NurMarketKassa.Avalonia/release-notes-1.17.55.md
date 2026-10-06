## v1.17.55

ИИ-советник меняет товары и оприходует накладную по фото, новый склад и меню программы владельца, режим «Одежда и обувь», перемещение в филиалы, долг со сроком, бонусы и настройки на сервере NurCRM.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **ИИ-советник работает с товарами.** Приход, списание, точный остаток, цены, закупка, срок годности, описание, страна, бренд, категория, штрихкод, фото. Сначала показывает карточку «Изменить товары?», меняет только после **«Выполнить»**. Сам находит сведения о товаре в интернете (открытые базы по штрихкоду и сайты). По просьбе открывает нужный раздел, а после изменений — **«Склад» с плашкой «✨ Изменено ИИ»**. То же в Telegram-боте (кнопки «Да / Нет») и в разговоре голосом («открой Марс на складе», «спиши…», «да, выполни»).
- **Накладная по фото.** Нажмите 📎 в строке ИИ-советника и выберите фото накладной, чека или прайса. Советник:
  - читает товары, количество и закупку;
  - ставит цены с наценкой **не ниже 20 %** и спрашивает, оставить ли её («поставь 25 %» — пересчитает);
  - знакомые товары оприходует приходом, новые создаёт.
  
  У каждого нового товара — поле штрихкода: просто сканируйте по очереди, код сам встаёт в следующую строку. Если такой штрихкод уже есть в каталоге — будет приход к этому товару, без дубля.
- **ИИ о сотрудниках:** список сотрудников, ставки (оклад, процент, за товар), зарплата и продажи каждого за период (по схемам зарплаты с сервера) и табель — смены, дни, отработанные часы, кто сейчас на смене. Работает и в чате, и в разговоре голосом.
- **Фото товара на выбор:** «найди фото для …» — ИИ-советник показывает несколько найденных в интернете фото (базы по штрихкоду и сайты магазинов) с кнопкой «Поставить это фото».
- **Напоминание должникам в WhatsApp:** «напомни должникам» — ИИ-советник показывает должников с суммами и кнопкой «Написать в WhatsApp»: открывается встроенный WhatsApp Web с готовым текстом (отправляете вы), «Следующий ▶» — по очереди.
- **ИИ знает, что закончилось и что заканчивается:** хорошо продававшиеся товары с нулевым остатком и популярные, которых осталось меньше чем на 7 дней продаж, — с количеством к заказу на 2 недели. Архив смен (Z-отчёты) — в ИИ-советнике и в Telegram-боте.
- **Данные магазина — таблицей:** сотрудники, табель, зарплаты, товары, остатки, продажи по дням, должники — ИИ-советник показывает аккуратной таблицей с итогом.
- **Склад, вкладка «Товары»:**
  - плитки сверху: товаров, склад по закупке и по продаже, заканчивается, нет в наличии, срок годности (нажатие включает фильтр);
  - фильтр «Наличие»; категория, бренд и срок годности в строке товара;
  - фото товара в начале строки (если у товара есть фото);
  - кнопка **«±»**: приход, списание с причиной или точное количество;
  - двойной щелчок открывает товар.
- **Меню программы владельца:** поиск раздела (**Ctrl+K**), группы «Товары / Продажи / Отчёты / Сайт / Клиенты / Управление» сворачиваются; в «Сводке» — сравнение «к вчера на это время».
- **Режим «Одежда и обувь»** (Настройки → Операции → «Сфера магазина»). В «Продуктах» всё работает как раньше.
  - Обмен по чеку в одном окне: доплата или сдача наличными или переводом, чек обмена.
  - Срок обмена (по умолчанию 14 дней) и категории «обмену не подлежит» — касса предупреждает. Возврат брака: товар не возвращается на склад, причина сохраняется на сервере.
  - Штрихкод размера: скан этикетки добавляет нужный размер и цвет; кнопка «Размер» в строке чека.
  - Приёмка сеткой «цвет × размер», штрихкоды размеров одной кнопкой, этикетки с размером и цветом.
  - У владельца: «Размеры и цвета» (продажи по размерам и цветам, остаток, доля проданного), «Заканчиваются размеры» на главной, «Обычно берёт размер» у клиента.
- **Перемещение в филиал со склада** — на сервере NurCRM, остатки меняются сразу, «Отменить» возвращает товар. У владельца раздел **«Филиалы»**.
- **Долг со сроком:** к дате или рассрочкой, срок и штраф за просрочку — в чеке; «Записать долг без продажи» в карточке клиента. **Прокат:** в чеке «ПРОКАТ», цена за сутки, даты и штраф.
- **На сервере NurCRM:**
  - настройки аккаунта (язык, тема, скрытые разделы, вид чека, коды доступа, купленные функции) подтягиваются на другой кассе;
  - бонусы покупателей общие для всех касс, покупатель видит баланс в приложении NurCRM (баллы, накопленные на кассе раньше, переносятся сами);
  - купленные функции «Стандарта» включаются на всех кассах компании.
- **Программа владельца:** «Магазин в приложении» NurCRM, «Возвраты поставщикам», «Долги клиентов», зарплата за проданный товар (схемы «за товар», «оклад + за товар», «процент + за товар»), «Редактор сайта» с 6 темами.
- **Ещё:** скрытие ненужных разделов меню (Настройки → Экран); предупреждение, если закупка дороже продажи; поиск и исправление дублей штрихкодов на складе; продажи без интернета досылаются пачкой до 50; на новой установке касса один раз спрашивает вид магазина.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| «Финансы», «Продажи» у владельца | «Нет связи с сервером» (с 06.10 сервер отвечает ошибкой на лишнюю страницу списка) | Данные загружаются |
| Накладная поставщика | Вбивать вручную | Фото → ИИ → «Выполнить» |
| Цена при приходе по накладной | Как написал ИИ | Наценка не ниже 20 % |
| Изменения от ИИ | Только текстом | Склад показывает изменённые товары |
| Поиск раздела у владельца | Листать меню | Ctrl+K |
| Поправить остаток одного товара | Через ревизию или приёмку | Кнопка «±» |
| «Размеры и цвета» за 30 дней | 2–3 минуты при каждом открытии | Повторно — за секунды |
| Магазин одежды: сканер «нет товара», а товар на складе | Копии товаров филиалов мешали поиску | Ищется верно |
| Перемещение | Только запись на этой кассе | Между складом и филиалами на сервере |
| Бонусы покупателей | На одной кассе | Общие для всех касс и приложения NurCRM |
| Настройки на другой кассе | Настраивать заново | Подтягиваются при входе |
| Отложенный чек при закрытии смены | Пропадал без предупреждения | Остаётся вкладкой «Отложен» |
| Строка «В очереди: N» под чеком после досылки офлайн-продаж | Оставалась, хотя всё уже на сервере | «Все чеки из очереди отправлены на сервер» |
| ИИ в разговоре голосом про сотрудников | «В сводке нет данных по сотрудникам» | Знает сотрудников, ставки, зарплату и табель |
| Табель для ИИ | 7–11 с | ~4 с (смены подгружаются заранее) |
| Фото на складе | Значок-коробка вместо фото | Фото товара в строке |

### Проверено (06.10, тестовый аккаунт)

- **Касса:** 33 продажи подряд (наличные, перевод, два товара) — все прошли, 1,5–2,5 с от «Оплатить» до чека; продажа в долг со сроком (№1646, срок на сервере); полный возврат чека (выдано 39 сом, чек возврата напечатан); внесение и изъятие — на сервере; без интернета 6 продаж в очереди — все досланы (пачкой и по одной); закрытие смены с Z-отчётом и открытие новой.
- **Режимы магазина:** «Продуктовый» → «Услуги» → «Одежда»: в «Продуктах» окно возврата без обмена и без срока, в «Услугах» каталог открывается на услугах, вид сохраняется на сервере.
- **Программа владельца:** открыты все 29 разделов (Сводка, ИИ-советник, Склад, Калькуляция, Пополнение, Возвраты поставщикам, Филиалы, Продажи, Долги, Прокат, Финансы, Аналитика, ABC, Прибыль, Продажи в убыток, Размеры и цвета, Заказы с сайта, Редактор и Настройки сайта, Магазин в приложении, Клиенты, Телеграм-бот, WhatsApp, Воронка, Зарплата, NurCRM, Маркетплейс, Настройки) — ошибок в журнале нет.
- **ИИ-советник:** 10 вопросов подряд (неделя, топ товаров, остатки, должники, зарплата, архив смен, сроки годности, акции, наценка, вопрос по-кыргызски) — все ответы за 1,6–3 с; таблицы должников, сотрудников и смен сходятся с итогами; накладная по фото, штрихкоды сканером, напоминания должникам.
- **10 000 товаров на складе** (замер на тех же моделях программы): поиск 1–2 мс, фильтры 0,2 мс, итоги склада 6 мс, сводка для ИИ 3 мс, скан 0,25 мс. Первая загрузка такого каталога с сервера — около минуты (сервер отдаёт по 100 товаров), идёт в фоне.

**Найдено стресс-тестом и исправлено в этой сборке:** отложенный чек пропадал при закрытии смены; строка «В очереди: N» не обновлялась после досылки; у прежней карточки ИИ оставалась активная «Выполнить» (товары могли создаться дважды); карточка с полями штрихкода забирала курсор у строки вопроса.

Не проверено в этом выпуске: действия с товарами в Telegram-боте и голосовые команды вслух (нужны телефон с Telegram и микрофон).
---

# Кыргызча

### Жаңы

- **ИИ-кеңешчи товарлар менен иштейт.** Кириш, эсептен чыгаруу, так калдык, баалар, сатып алуу баасы, жарамдуулук мөөнөтү, сүрөттөмө, өлкө, бренд, категория, штрихкод, сүрөт. Адегенде «Товарларды өзгөртөлүбү?» карточкасын көрсөтөт, **«Аткаруу»** баскычынан кийин гана өзгөртөт. Товар тууралуу маалыматты интернеттен өзү табат. Керектүү бөлүмдү ачат, өзгөртүүлөрдөн кийин — **«✨ ИИ өзгөрттү»** белгиси менен «Кампаны». Ошол эле — Telegram-ботто («Ооба / Жок» баскычтары) жана үн менен сүйлөшүүдө.
- **Накладнойду сүрөт менен.** ИИ-кеңешчидеги 📎 баскычы → накладнойдун, чектин же прайстын сүрөтү. Кеңешчи товарларды, санын жана сатып алуу баасын окуйт, баасын **20 %дан кем эмес** үстөк менен коюп, аны калтыруу керекпи деп сурайт («25 % кой» — кайра эсептейт); тааныш товарларды кириштейт, жаңыларын түзөт. Ар бир жаңы товарда штрихкод талаасы бар: кезек менен сканерлеңиз — код өзү кийинки сапка түшөт. Мындай штрихкод каталогдо бар болсо — ошол товарга кириш болот, кайталанбайт.
- **ИИ кызматкерлер жөнүндө:** кызматкерлердин тизмеси, ставкалары, ар биринин эмгек акысы жана сатуулары, табель — сменалар, күндөр, иштеген сааттар, азыр сменада ким. Чатта да, үн менен сүйлөшүүдө да.
- **Товардын сүрөтү тандоо менен:** «… үчүн сүрөт тап» — ИИ-кеңешчи интернеттен табылган бир нече сүрөттү «Ушул сүрөттү коюу» баскычы менен көрсөтөт.
- **Карызкорлорго WhatsApp аркылуу эскертүү:** ИИ-кеңешчи карызкорлорду «WhatsApp’ка жазуу» баскычы менен көрсөтөт — даяр текст менен чат ачылат, жөнөтөсүз өзүңүз.
- **ИИ эмне түгөнгөнүн жана эмне түгөнүп баратканын билет**, буйрутмага саны менен; сменалардын архиви — ИИде жана Telegram-ботто.
- **Дүкөндүн маалыматы — таблица менен:** кызматкерлер, табель, айлыктар, товарлар, калдыктар, күндөр боюнча сатуулар, карызкорлор.
- **Кампа, «Товарлар» өтмөгү:** жогору жактагы плиткалар (басканда чыпка күйөт), «Бар болушу» чыпкасы, сапта категория, бренд, жарамдуулук мөөнөтү жана товардын сүрөтү, **«±»** баскычы (кириш, себеби менен эсептен чыгаруу, так саны), эки жолу басканда товар ачылат.
- **Ээсинин программасынын менюсу:** бөлүмдү издөө (**Ctrl+K**), топтор жыйналат; «Жыйынтыкта» — «кечээ ушул убакка салыштырмалуу».
- **«Кийим жана бут кийим» режими** (Жөндөөлөр → Операциялар → «Дүкөндүн тармагы»). «Азык-түлүктө» баары мурункудай: чек боюнча алмаштыруу, алмаштыруу мөөнөтү жана «алмаштырылбайт» категориялары, бракты кайтаруу, өлчөмдүн штрихкоду, «түс × өлчөм» торчосу менен кабыл алуу, ээсинде «Өлчөмдөр жана түстөр» отчету.
- **Кампадан филиалга жылдыруу** — NurCRM серверинде, «Жокко чыгаруу» менен; ээсинде **«Филиалдар»** бөлүмү.
- **Мөөнөтү бар карыз:** датага чейин же бөлүп төлөө, мөөнөтү жана айыбы — чекте. **Прокат:** чекте «ПРОКАТ», суткалык баа, даталар жана айып.
- **NurCRM серверинде:** аккаунттун жөндөөлөрү башка кассада да жүктөлөт; сатып алуучулардын бонустары бардык кассаларга жалпы; сатып алынган функциялар бардык кассаларда күйөт.
- **Ээсинин программасы:** NurCRM тиркемесиндеги «Дүкөн», «Жеткирүүчүлөргө кайтаруу», «Кардарлардын карыздары», сатылган товар үчүн эмгек акы, 6 темасы бар «Сайт редактору».
- **Дагы:** меню бөлүмдөрүн жашыруу; сатып алуу баасы сатуудан кымбат болсо эскертүү; кампадагы штрихкоддордун кайталанышын табуу; интернетсиз сатуулар 50гө чейин топтоп жөнөтүлөт.

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| Ээсиндеги «Финансы», «Сатуулар» | «Сервер менен байланыш жок» | Маалыматтар жүктөлөт |
| Жеткирүүчүнүн накладнойу | Колго киргизүү | Сүрөт → ИИ → «Аткаруу» |
| Накладной боюнча баа | ИИ жазгандай | Үстөк 20 %дан кем эмес |
| ИИнин өзгөртүүлөрү | Текст гана | Кампа өзгөргөн товарларды көрсөтөт |
| Бир товардын калдыгын оңдоо | Ревизия же кабыл алуу аркылуу | «±» баскычы |
| 30 күндүк «Өлчөмдөр жана түстөр» | Ар ачканда 2–3 мүнөт | Кайра — бир нече секунд |
| Жылдыруу | Ушул кассада жазуу гана | Кампа менен филиалдар ортосунда серверде |
| Бонустар | Бир кассада | Бардык кассаларга жана NurCRM тиркемесине жалпы |
| Сменаны жапканда калтырылган чек | Эскертүүсүз жоголчу | «Калтырылган» өтмөгү болуп калат |
| Офлайн сатууларды жөнөткөндөн кийин «Кезекте: N» сабы | Калчу | «Кезектеги бардык чектер серверге жөнөтүлдү» |
| Үн менен сүйлөшүүдө кызматкерлер | «Маалымат жок» | Кызматкерлерди, ставкаларды, айлыкты жана табелди билет |
| Кампадагы сүрөттөр | Сүрөттүн ордуна куту белгиси | Сапта товардын сүрөтү |

### Текшерилди (06.10, сыноо аккаунту)

- **Кассанын стресс-тести:** катары менен 15 сатуу (накталай, которуу, чекте эки товар) — 15тен 15, «Төлөө» баскычынан даяр чекке чейин 1,5–2,2 сек.
- **Интернетсиз:** 3 сатуу 0,1–0,2 секундада кезекке түштү; байланыш кайтканда бир топ менен 2,2 секундада жөнөтүлдү (3төн 3).
- **Смена:** Z-отчет менен жабуу (2,4 сек) жана жаңысын ачуу; калтырылган чек сакталып калды.
- **Накладной сүрөт менен** (4 сап): бардык саптар жана 6 893 сом окулду; «25 % кой» — баалар кайра эсептелди; 4 штрихкод ар бири өз сабына түштү; 4 товар түзүлдү.
- **ИИ кызматкерлер жөнүндө:** 5 кызматкердин тизмеси, жумалык табель жана эсептелген айлык — сервердин маалыматы боюнча.

**Стресс-тест тапкан жана ушул жыйнакта оңдолгон каталар:** сменаны жапканда калтырылган чек жоголчу; «Кезекте: N» сабы жаңыланчу эмес; мурунку ИИ карточкасында «Аткаруу» активдүү калчу; штрихкод талаалары бар карточка курсорду суроо сабынан алчу.

Касса, карыз, кайтаруу, акча салуу жана алуу, интернетсиз сатуу жана смена текшерилди. Текшерилген жок: Telegram-боттогу товар аракеттери жана үн буйруктары.
---

# English

### New

- **The AI advisor works with products.** Receipts, write-offs, exact stock, prices, purchase price, expiry date, description, country, brand, category, barcode, photo. It first shows a “Change the products?” card and changes nothing until you press **“Do it”**. It finds product details on the web by itself, opens the section you ask for and, after changes, the **warehouse with a “✨ Changed by AI” badge**. The same works in the Telegram bot (“Yes / No” buttons) and in a voice conversation.
- **Invoice by photo.** Press 📎 in the AI advisor and pick a photo of an invoice, receipt or price list. The advisor reads the items, quantities and purchase prices, sets prices with a markup of **at least 20%** and asks whether to keep it (“make it 25%” recalculates), receives known products and creates new ones. Every new product has a barcode field: just scan one after another — each code goes to the next line by itself. If the barcode already exists in the catalog, stock is received to that product, no duplicate.
- **AI about staff:** the employee list, pay rates, salary and sales of each employee for a period, and the timesheet — shifts, days, hours worked, who is on shift now. Works in chat and in voice calls.
- **Choose a product photo:** “find a photo for …” — the AI advisor shows several photos found online with a “Use this photo” button.
- **Debt reminders on WhatsApp:** the AI advisor lists debtors with a “Write on WhatsApp” button — a chat opens with the message ready, you send it.
- **The AI knows what sold out and what is running low**, with quantities to order; the shift archive (Z-reports) is in the AI advisor and the Telegram bot.
- **Shop data as tables:** staff, timesheet, salaries, products, stock, daily sales, debtors — the AI advisor shows a neat table with a total.
- **Warehouse, “Products” tab:** summary tiles on top (click to filter), a “Stock” filter, category, brand, expiry date and product photo in the row, a **“±”** button (receive, write off with a reason, exact quantity), double-click opens the product.
- **Owner program menu:** section search (**Ctrl+K**), collapsible groups; “vs yesterday at this time” in “Overview”.
- **“Clothing and shoes” mode** (Settings → Operations → “Store type”). In “Groceries” everything stays as before: exchange by receipt, exchange period and non-exchangeable categories, defect returns, size barcodes, receiving by a “color × size” grid, the “Sizes and colors” report for the owner.
- **Transfer to a branch from the warehouse** — on the NurCRM server, with “Cancel”; a **“Branches”** section for the owner.
- **Debt with a due date:** by a date or in installments, due date and late fee on the receipt. **Rentals:** “RENTAL”, price per day, dates and late fee on the receipt.
- **On the NurCRM server:** account settings follow you to another till; customer bonuses are shared by all tills; purchased features turn on at all tills of the company.
- **Owner program:** “Shop in the app” in NurCRM, “Supplier returns”, “Customer debts”, salary per product sold, “Website editor” with 6 themes.
- **Also:** hide menu sections you don't need; a warning when the purchase price is above the selling price; find and fix duplicate barcodes; offline sales are sent in batches of up to 50.

### Before → now

| What | Before | Now |
|---|---|---|
| “Finance”, “Sales” in the owner program | “No connection to the server” | Data loads |
| Supplier invoice | Typed in by hand | Photo → AI → “Do it” |
| Price when receiving an invoice | Whatever the AI wrote | Markup at least 20% |
| AI changes | Text only | The warehouse shows the changed products |
| Fixing one product's stock | Via stocktaking or receiving | The “±” button |
| “Sizes and colors” for 30 days | 2–3 minutes every time | Seconds on reopening |
| Transfers | Only a record on this till | Between warehouse and branches on the server |
| Customer bonuses | On one till | Shared by all tills and the NurCRM app |
| A held receipt when closing the shift | Disappeared without warning | Stays as a “Held” tab |
| “Queued: N” under the receipt after offline sales were sent | Stayed although everything was sent | “All queued receipts have been sent to the server” |
| Staff questions in a voice call | “No staff data” | Knows staff, pay rates, salaries and timesheet |
| Photos in the warehouse | A box icon instead of the photo | Product photo in the row |

### Checked (06.10, test account)

- **Till stress test:** 15 sales in a row (cash, transfer, two items in a receipt) — 15 of 15, 1.5–2.2 s from “Pay” to the finished receipt.
- **Offline:** 3 sales were queued in 0.1–0.2 s; when the connection came back they were sent in one batch in 2.2 s (3 of 3).
- **Shift:** closing with a Z-report (2.4 s) and opening a new one; a held receipt survived the shift close.
- **Invoice by photo** (4 lines): all lines and the 6,893 som total were read; “make it 25%” recalculated the prices; 4 scanned barcodes each went to its own line; 4 products were created.
- **AI about staff:** a list of 5 employees, the weekly timesheet and accruals — from the server data.

**Found by the stress test and fixed in this build:** a held receipt was lost when closing the shift; the “Queued: N” line did not update after sending; the previous AI card kept an active “Do it” button; the card with barcode fields took the cursor from the question line.

Sales, debt, returns, cash in/out, offline sales and the shift were checked. Not checked: product actions in the Telegram bot and spoken voice commands.
---

# Türkçe

### Yeni

- **Yapay zekâ danışmanı ürünlerle çalışır.** Giriş, düşüm, tam stok, fiyatlar, alış fiyatı, son kullanma tarihi, açıklama, ülke, marka, kategori, barkod, fotoğraf. Önce «Ürünler değiştirilsin mi?» kartını gösterir, **«Uygula»**ya basmadan hiçbir şeyi değiştirmez. Ürün bilgisini internette kendisi bulur, istediğiniz bölümü açar, değişikliklerden sonra **«✨ Yapay zekâ değiştirdi»** etiketiyle depoyu gösterir. Aynısı Telegram botunda («Evet / Hayır» düğmeleri) ve sesli konuşmada.
- **Fotoğraftan fatura.** Yapay zekâ danışmanında 📎 düğmesine basın ve fatura, fiş veya fiyat listesi fotoğrafını seçin. Danışman ürünleri, miktarları ve alış fiyatlarını okur, fiyatları **en az %20** kâr payıyla koyar ve kalsın mı diye sorar («%25 yap» — yeniden hesaplar), bilinen ürünleri stoğa alır, yenilerini oluşturur. Her yeni üründe barkod alanı vardır: sırayla okutun — her kod kendiliğinden sonraki satıra geçer. Barkod katalogda zaten varsa o ürüne giriş yapılır, kopya oluşmaz.
- **Çalışanlar hakkında yapay zekâ:** çalışan listesi, ücret oranları, her çalışanın dönem maaşı ve satışları, puantaj — vardiyalar, günler, çalışılan saatler, şu an vardiyada kim. Sohbette ve sesli görüşmede.
- **Ürün fotoğrafı seçme:** «… için fotoğraf bul» — yapay zekâ danışmanı internette bulunan birkaç fotoğrafı «Bu fotoğrafı kullan» düğmesiyle gösterir.
- **WhatsApp ile borç hatırlatma:** yapay zekâ danışmanı borçluları «WhatsApp’tan yaz» düğmesiyle gösterir — hazır metinle sohbet açılır, siz gönderirsiniz.
- **Yapay zekâ nelerin bittiğini ve azaldığını bilir**, sipariş miktarıyla; vardiya arşivi (Z raporları) yapay zekâda ve Telegram botunda.
- **Mağaza verileri tablo olarak:** çalışanlar, puantaj, maaşlar, ürünler, stok, günlük satışlar, borçlular.
- **Depo, «Ürünler» sekmesi:** üstte özet kutucukları (tıklayınca filtre), «Stok» filtresi, satırda kategori, marka, son kullanma tarihi ve ürün fotoğrafı, **«±»** düğmesi (giriş, nedenli düşüm, tam miktar), çift tıklama ürünü açar.
- **Sahip programı menüsü:** bölüm arama (**Ctrl+K**), daraltılabilen gruplar; «Özet»te «dün bu saate göre».
- **«Giyim ve ayakkabı» modu** (Ayarlar → İşlemler → «Mağaza türü»). «Gıda» modunda her şey eskisi gibi: fişle değişim, değişim süresi ve değişime tabi olmayan kategoriler, kusurlu ürün iadesi, beden barkodu, «renk × beden» tablosuyla mal kabul, sahip için «Bedenler ve renkler» raporu.
- **Depodan şubeye transfer** — NurCRM sunucusunda, «İptal» ile; sahip için **«Şubeler»** bölümü.
- **Vadeli borç:** bir tarihe kadar veya taksitle, vade ve gecikme cezası fişte. **Kiralama:** fişte «KİRALAMA», günlük fiyat, tarihler ve ceza.
- **NurCRM sunucusunda:** hesap ayarları başka kasada da gelir; müşteri bonusları tüm kasalarda ortak; satın alınan özellikler şirketin tüm kasalarında açılır.
- **Sahip programı:** NurCRM uygulamasında «Mağaza», «Tedarikçiye iadeler», «Müşteri borçları», satılan ürün başına maaş, 6 temalı «Site düzenleyici».
- **Ayrıca:** gereksiz menü bölümlerini gizleme; alış fiyatı satıştan yüksekse uyarı; kopya barkodları bulma ve düzeltme; internetsiz satışlar 50'ye kadar toplu gönderilir.

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Sahip programında «Finans», «Satışlar» | «Sunucuyla bağlantı yok» | Veriler yükleniyor |
| Tedarikçi faturası | Elle girilirdi | Fotoğraf → yapay zekâ → «Uygula» |
| Faturayla girişte fiyat | Yapay zekâ ne yazdıysa | Kâr payı en az %20 |
| Yapay zekâ değişiklikleri | Sadece metin | Depo değişen ürünleri gösterir |
| Tek ürünün stokunu düzeltmek | Sayım veya mal kabul ile | «±» düğmesi |
| 30 günlük «Bedenler ve renkler» | Her açılışta 2–3 dakika | Yeniden açılışta saniyeler |
| Transfer | Sadece bu kasada kayıt | Depo ve şubeler arasında sunucuda |
| Müşteri bonusları | Tek kasada | Tüm kasalarda ve NurCRM uygulamasında ortak |
| Vardiya kapatılırken bekleyen fiş | Uyarısız kayboluyordu | «Bekliyor» sekmesi olarak kalır |
| Çevrimdışı satışlar gönderildikten sonra fişin altındaki «Sırada: N» | Her şey gönderilse de kalıyordu | «Sıradaki tüm fişler sunucuya gönderildi» |
| Sesli görüşmede çalışanlar | «Veri yok» | Çalışanları, ücretleri, maaşları ve puantajı bilir |
| Depoda fotoğraflar | Fotoğraf yerine kutu simgesi | Satırda ürün fotoğrafı |

### Kontrol edildi (06.10, test hesabı)

- **Kasa stres testi:** art arda 15 satış (nakit, havale, fişte iki ürün) — 15/15, «Öde»den hazır fişe 1,5–2,2 saniye.
- **İnternetsiz:** 3 satış 0,1–0,2 saniyede sıraya alındı; bağlantı gelince tek pakette 2,2 saniyede gönderildi (3/3).
- **Vardiya:** Z raporuyla kapatma (2,4 sn) ve yenisini açma; bekleyen fiş vardiya kapanışından sonra korundu.
- **Fotoğraftan fatura** (4 satır): tüm satırlar ve 6.893 som toplamı okundu; «%25 yap» fiyatları yeniden hesapladı; okutulan 4 barkodun her biri kendi satırına geçti; 4 ürün oluşturuldu.
- **Çalışanlar hakkında yapay zekâ:** 5 çalışanın listesi, haftalık puantaj ve hak edişler — sunucu verilerinden.

**Stres testinde bulunan ve bu sürümde düzeltilenler:** vardiya kapatılırken bekleyen fiş kayboluyordu; «Sırada: N» satırı gönderimden sonra güncellenmiyordu; önceki yapay zekâ kartında «Uygula» etkin kalıyordu; barkod alanlı kart soru satırındaki imleci alıyordu.

Satış, borç, iade, para girişi/çıkışı, internetsiz satış ve vardiya kontrol edildi. Kontrol edilmedi: Telegram botundaki ürün işlemleri ve sesli komutlar.
---

# O'zbekcha

### Yangi

- **SI maslahatchi mahsulotlar bilan ishlaydi.** Kirim, hisobdan chiqarish, aniq qoldiq, narxlar, xarid narxi, yaroqlilik muddati, tavsif, mamlakat, brend, kategoriya, shtrix-kod, rasm. Avval «Mahsulotlarni o'zgartiraymi?» kartochkasini ko'rsatadi, **«Bajarish»**ni bosmaguningizcha hech narsani o'zgartirmaydi. Mahsulot haqidagi ma'lumotni internetdan o'zi topadi, kerakli bo'limni ochadi, o'zgarishlardan keyin **«✨ SI o'zgartirdi»** belgisi bilan omborni ko'rsatadi. Xuddi shunday — Telegram-botda («Ha / Yo'q» tugmalari) va ovozli suhbatda.
- **Yuk xati rasm orqali.** SI maslahatchidagi 📎 tugmasini bosing va yuk xati, chek yoki narxlar ro'yxati rasmini tanlang. Maslahatchi mahsulotlar, miqdor va xarid narxini o'qiydi, narxni **kamida 20 %** ustama bilan qo'yadi va qoldiraymi deb so'raydi («25 % qo'y» — qayta hisoblaydi), tanish mahsulotlarni kirim qiladi, yangilarini yaratadi. Har bir yangi mahsulotda shtrix-kod maydoni bor: ketma-ket skanerlang — har bir kod o'zi keyingi qatorga tushadi. Bunday shtrix-kod katalogda bo'lsa — o'sha mahsulotga kirim bo'ladi, takror yaratilmaydi.
- **Xodimlar haqida SI:** xodimlar ro'yxati, stavkalari, har birining davr bo'yicha ish haqi va sotuvlari, tabel — smenalar, kunlar, ishlangan soatlar, hozir smenada kim. Chatda ham, ovozli suhbatda ham.
- **Mahsulot rasmini tanlash:** «… uchun rasm top» — SI maslahatchi internetdan topilgan bir nechta rasmni «Shu rasmni qo'yish» tugmasi bilan ko'rsatadi.
- **Qarzdorlarga WhatsApp orqali eslatma:** SI maslahatchi qarzdorlarni «WhatsApp'da yozish» tugmasi bilan ko'rsatadi — tayyor matn bilan chat ochiladi, o'zingiz yuborasiz.
- **SI nima tugaganini va nima kamayayotganini biladi**, buyurtma miqdori bilan; smenalar arxivi (Z-hisobotlar) — SIda va Telegram-botda.
- **Do'kon ma'lumotlari — jadval:** xodimlar, tabel, ish haqi, mahsulotlar, qoldiqlar, kunlik sotuvlar, qarzdorlar.
- **Ombor, «Mahsulotlar» yorlig'i:** yuqorida xulosa plitkalari (bosilsa filtr yoqiladi), «Mavjudlik» filtri, qatorda kategoriya, brend, yaroqlilik muddati va mahsulot rasmi, **«±»** tugmasi (kirim, sabab bilan hisobdan chiqarish, aniq miqdor), ikki marta bosish mahsulotni ochadi.
- **Ega dasturi menyusi:** bo'limni qidirish (**Ctrl+K**), guruhlar yig'iladi; «Umumiy ko'rinish»da «kechagi shu vaqtga nisbatan».
- **«Kiyim va poyabzal» rejimi** (Sozlamalar → Operatsiyalar → «Do'kon turi»). «Oziq-ovqat»da hammasi avvalgidek: chek bo'yicha almashtirish, almashtirish muddati va almashtirilmaydigan kategoriyalar, nuqsonli tovarni qaytarish, o'lcham shtrix-kodi, «rang × o'lcham» jadvali bilan qabul, egasi uchun «O'lchamlar va ranglar» hisoboti.
- **Ombordan filialga ko'chirish** — NurCRM serverida, «Bekor qilish» bilan; egasi uchun **«Filiallar»** bo'limi.
- **Muddatli qarz:** sanagacha yoki bo'lib to'lash, muddat va jarima chekda. **Ijara:** chekda «IJARA», sutkalik narx, sanalar va jarima.
- **NurCRM serverida:** akkaunt sozlamalari boshqa kassada ham yuklanadi; xaridorlar bonuslari barcha kassalar uchun umumiy; sotib olingan funksiyalar kompaniyaning barcha kassalarida yoqiladi.
- **Ega dasturi:** NurCRM ilovasida «Do'kon», «Yetkazib beruvchiga qaytarish», «Mijozlar qarzlari», sotilgan mahsulot uchun ish haqi, 6 mavzuli «Sayt muharriri».
- **Yana:** keraksiz menyu bo'limlarini yashirish; xarid narxi sotuvdan qimmat bo'lsa ogohlantirish; takroriy shtrix-kodlarni topish va tuzatish; internetsiz sotuvlar 50 tagacha to'plab yuboriladi.

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Ega dasturida «Moliya», «Sotuvlar» | «Server bilan aloqa yo'q» | Ma'lumotlar yuklanadi |
| Yetkazib beruvchi yuk xati | Qo'lda kiritilardi | Rasm → SI → «Bajarish» |
| Yuk xati bo'yicha narx | SI qanday yozsa | Ustama kamida 20 % |
| SI o'zgarishlari | Faqat matn | Ombor o'zgargan mahsulotlarni ko'rsatadi |
| Bitta mahsulot qoldig'ini tuzatish | Inventarizatsiya yoki qabul orqali | «±» tugmasi |
| 30 kunlik «O'lchamlar va ranglar» | Har ochilganda 2–3 daqiqa | Qayta — bir necha soniya |
| Ko'chirish | Faqat shu kassada yozuv | Ombor va filiallar orasida serverda |
| Xaridorlar bonuslari | Bitta kassada | Barcha kassalar va NurCRM ilovasi uchun umumiy |
| Smena yopilganda kutishdagi chek | Ogohlantirishsiz yo'qolardi | «Kutishda» yorlig'i bo'lib qoladi |
| Oflayn sotuvlar yuborilgach chek ostidagi «Navbatda: N» | Hammasi yuborilsa ham qolardi | «Navbatdagi barcha cheklar serverga yuborildi» |
| Ovozli suhbatda xodimlar | «Ma'lumot yo'q» | Xodimlar, stavkalar, ish haqi va tabelni biladi |
| Ombordagi rasmlar | Rasm o'rniga quti belgisi | Qatorda mahsulot rasmi |

### Tekshirildi (06.10, sinov akkaunti)

- **Kassa stress-testi:** ketma-ket 15 sotuv (naqd, o'tkazma, chekda ikki mahsulot) — 15 tadan 15, «To'lash»dan tayyor chekkacha 1,5–2,2 soniya.
- **Internetsiz:** 3 sotuv 0,1–0,2 soniyada navbatga tushdi; aloqa qaytgach bitta to'plamda 2,2 soniyada yuborildi (3 tadan 3).
- **Smena:** Z-hisobot bilan yopish (2,4 soniya) va yangisini ochish; kutishdagi chek smena yopilgandan keyin saqlanib qoldi.
- **Yuk xati rasm orqali** (4 qator): barcha qatorlar va 6 893 som summasi o'qildi; «25 % qo'y» narxlarni qayta hisobladi; skanerlangan 4 shtrix-kodning har biri o'z qatoriga tushdi; 4 mahsulot yaratildi.
- **Xodimlar haqida SI:** 5 xodim ro'yxati, haftalik tabel va hisoblangan ish haqi — server ma'lumotlari bo'yicha.

**Stress-test topgan va shu yig'ilmada tuzatilgan xatolar:** smena yopilganda kutishdagi chek yo'qolardi; «Navbatda: N» qatori yuborilgandan keyin yangilanmasdi; oldingi SI kartochkasida «Bajarish» faol qolardi; shtrix-kod maydonli kartochka kursorni savol qatoridan olardi.

Sotuv, qarz, qaytarish, pul kiritish/olish, internetsiz sotuv va smena tekshirildi. Tekshirilmadi: Telegram-botdagi mahsulot amallari va ovozli buyruqlar.
