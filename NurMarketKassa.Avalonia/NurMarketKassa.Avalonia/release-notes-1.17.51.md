## v1.17.51

Чековый принтер XP-80, возврат на тарифе «Старт», подключение функций «Стандарта» на «Старте», бесплатный поиск в интернете для ИИ, история разговоров ИИ-советника, QR покупателя из приложения NurCRM, заказы с сайта с сервера.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Чековый принтер XP-80 и похожие (42 символа в строке).** В «Настройки → Печать → Ширина ленты» появился вариант **«80 мм — 42 симв. (XP-80 и похожие)»**: сам ставит 42 символа и 512 точек.
- **Тариф «Старт»:** любую функцию «Стандарта» можно подключить отдельно в **Маркетплейс → Доп. функции** — 1500 сом активация + 400 сом в месяц за каждую: ИИ; клиенты, воронка и WhatsApp; продажи и аналитика; пополнение и сроки; зарплата; долги и отложенные чеки; NurCRM, база знаний и поддержка. Во вкладке **«Аккаунт»** — описание «Стандарта» и сравнение с «Стартом».
- **ИИ ищет в интернете бесплатно** — с ключом Groq (console.groq.com, карта не нужна). Запасные бесплатные модели OpenRouter отвечают, когда у Gemini кончился дневной лимит. Ключи вставляются в **Настройки → Операции** рядом с ключом Gemini (кнопки «Получить бесплатный ключ» и «Проверить») или в ИИ-советнике.
- **ИИ-советник:** кнопка **«■ Стоп»** во время ответа или поиска фото; «Новый разговор» сохраняет прежний; кнопка **🕘 «История»** — прошлые разговоры, их можно открыть и продолжить. Голосом отвечает быстрее; фото товара ищет и по названию, и в интернете.
- **Касса и программа владельца на одном компьютере** сами берут друг у друга ключи ИИ и подключение Telegram-бота — вставлять дважды не нужно.
- **QR покупателя из приложения NurCRM** (одноразовый код): касса находит клиента через сервер, нового заводит сама, показывает его бонусы.
- **«Заказы с сайта»** в программе владельца — заказы витрины и Telegram-бота с сервера NurCRM; статусы «Принят → Готов к выдаче → Выдан» или «Отменён». Под заказ товар резервируется: «Отменить» возвращает его на остаток.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Чек на XP-80 | Линии «=====» и «0 СОМ» переносились на новую строку | Вариант «80 мм — 42 симв.», чек ровный |
| Возврат на тарифе «Старт» | Пункт «Возврат» и кнопка «Вернуть…» скрыты | Работают (нужно только право сотрудника) |
| Функции «Стандарта» на «Старте» | ИИ-советник и бот были видны без оплаты, остальное недоступно | Скрыты; любую можно подключить в Маркетплейсе |
| Поиск в интернете для ИИ | Только с платным ключом Google | Бесплатно с ключом Groq |
| Кончился лимит Gemini | «Не получилось ответить» | Отвечает запасная модель (OpenRouter, Groq) |
| «Новый разговор» во время поиска фото | Чат стирался, ввод оставался выключенным | Поиск останавливается, старый разговор — в «Истории» |
| Ключи ИИ в кассе и программе владельца | Вставлять в каждой | Подхватываются автоматически |
| Одноразовый QR покупателя | «Касса пока не принимает» | Клиент выбирается в чеке |
| Заказы с сайта в программе владельца | Только в WhatsApp | Список с сервера со статусами |

### Проверено (05.10, тестовый аккаунт)

- Касса: 14 продаж подряд (наличные, безнал, два товара в чеке) — 14 из 14, оплата 0,3–0,4 с на сервере, чеки напечатаны на тестовый принтер; продажа в долг и оплата долга; полный возврат чека (товар вернулся на склад, чек возврата напечатан); внесение и изъятие 50 сом; закрытие смены с Z-отчётом и открытие новой — суммы в Z-отчёте сходятся.
- Чековый принтер: вариант «80 мм — 42 симв.» — на принтер ушли строки по 42 символа; выбор сохраняется после повторного открытия; обратное переключение возвращает 48.
- QR покупателя: поддельный одноразовый код — сервер ответил «QR устарел или недействителен», касса показала это, продажа не остановилась.
- ИИ-советник: «■ Стоп» во время поиска фото, «Новый разговор», открытие разговора из «Истории». Неверный ключ Groq — «ключ не подходит», не сохраняется.
- Ключи Groq и OpenRouter из программы владельца касса подхватила сама после запуска.
- Тарифы: «Старт» без функций, с подключённым пакетом и «Стандарт» — проверены программой-тестом; ключи пакетов проверяются.

Не проверено: работа без интернета (в этом выпуске продажа и смена не менялись); экраны тарифа «Старт» вживую — тестовый аккаунт на «Стандарте».

---

# Кыргызча

### Жаңы

- **XP-80 жана окшош чек принтерлери (сапта 42 белги).** «Жөндөөлөр → Басып чыгаруу → Тасманын туурасы» тизмесинде **«80 мм — 42 белги (XP-80 жана окшоштору)»** варианты пайда болду: 42 белги жана 512 чекит өзү коюлат.
- **«Старт» тарифи:** «Стандарттын» каалаган функциясын **Маркетплейс → Кошумча функциялар** бөлүмүндө өзүнчө туташтырса болот — ар бири үчүн 1500 сом активдештирүү + айына 400 сом. **«Аккаунт»** өтмөгүндө «Стандарттын» сүрөттөмөсү жана «Старт» менен салыштыруу.
- **ИИ интернеттен акысыз издейт** — Groq ачкычы менен. OpenRouter'дин запастагы акысыз моделдери Gemini'нин лимити бүткөндө жооп берет. Ачкычтар **Жөндөөлөр → Операциялар** бөлүмүндө Gemini ачкычынын жанына же ИИ-кеңешчиге коюлат.
- **ИИ-кеңешчи:** **«■ Токтотуу»** баскычы; «Жаңы маек» мурункусун сактайт; **🕘 «Тарых»** — мурунку маектер. Үн менен тезирээк жооп берет; товардын сүрөтүн аталышы боюнча жана интернеттен табат.
- **Бир компьютердеги касса жана ээсинин программасы** ИИ ачкычтарын жана Telegram-ботту бири-биринен өзү алат.
- **NurCRM тиркемесиндеги сатып алуучунун QR коду** (бир жолку код): касса кардарды сервер аркылуу табат, жаңысын өзү кошот.
- **«Сайттан заказдар»** — витринанын жана Telegram-боттун заказдары NurCRM серверинен; статустар «Кабыл алынды → Берүүгө даяр → Берилди» же «Жокко чыгарылды».

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| XP-80деги чек | Сызыктар жана «0 СОМ» жаңы сапка өтчү | «80 мм — 42 белги» варианты, чек түз |
| «Старт» тарифинде кайтаруу | Жашырылган | Иштейт |
| «Старттагы» «Стандарт» функциялары | ИИ жана бот акысыз көрүнчү | Жашырылган; Маркетплейсте туташтырылат |
| ИИ үчүн интернеттен издөө | Акылуу Google ачкычы менен гана | Groq ачкычы менен акысыз |
| Издөө учурундагы «Жаңы маек» | Киргизүү өчүк калчу | Издөө токтойт, мурунку маек «Тарыхта» |
| Кассадагы жана ээсинин программасындагы ИИ ачкычтары | Ар бирине коюу керек | Өзү алынат |
| Бир жолку QR | «Касса азырынча кабыл албайт» | Кардар чекте тандалат |
| Сайттан заказдар | WhatsApp'та гана | Серверден статустар менен |

### Текшерилди (05.10, сыноо аккаунту)

Касса: катары менен 14 сатуу — 14төн 14, чектер басылды; карызга сатуу жана карызды төлөө; чекти толук кайтаруу; акча салуу жана алуу; сменаны Z-отчет менен жабуу жана ачуу. XP-80 варианты — сапта 42 белги. Бир жолку QR — сервер жасалма кодду четке какты, сатуу токтогон жок. ИИ-кеңешчи: «Токтотуу», «Жаңы маек», «Тарых». Groq жана OpenRouter ачкычтарын касса өзү алды.

---

# English

### New

- **XP-80 and similar receipt printers (42 characters per line).** “Settings → Printing → Tape width” has a new option **“80 mm — 42 chars (XP-80 and similar)”** that sets 42 characters and 512 dots.
- **“Start” plan:** any “Standard” feature can be connected separately in **Marketplace → Extras** — 1500 som activation + 400 som a month each. The **“Account”** tab describes “Standard” and compares it with “Start”.
- **AI searches the web for free** with a Groq key. OpenRouter's free backup models answer when Gemini's daily limit runs out. Keys go in **Settings → Operations** next to the Gemini key or in the AI advisor.
- **AI advisor:** a **“■ Stop”** button; “New chat” keeps the previous one; **🕘 “History”** lists past chats you can reopen and continue. Faster voice answers; finds product photos by name and on the web.
- **The till and the owner program on one computer** pick up each other's AI keys and Telegram bot automatically.
- **Customer QR from the NurCRM app** (one-time code): the till finds the customer through the server and adds a new one by itself.
- **“Website orders”** in the owner program — showcase and Telegram bot orders from the NurCRM server; statuses “Accepted → Ready → Handed over” or “Canceled”.

### Before → after

| What | Before | After |
|---|---|---|
| Receipt on XP-80 | Lines and “0 СОМ” wrapped to a new line | “80 mm — 42 chars” option, neat receipt |
| Returns on “Start” | Hidden | Work |
| “Standard” features on “Start” | AI and bot visible without payment | Hidden; connectable in Marketplace |
| Web search for AI | Only with a paid Google key | Free with a Groq key |
| “New chat” during photo search | Input stayed disabled | Search stops, old chat in “History” |
| AI keys in till and owner program | Enter in each | Picked up automatically |
| One-time customer QR | “Not supported yet” | Customer selected in the receipt |
| Website orders | Only in WhatsApp | List from the server with statuses |

### Checked (05.10, test account)

Till: 14 sales in a row — 14 of 14, receipts printed; a debt sale and debt payment; a full receipt return; cash in and out; closing the shift with a Z report and opening a new one. XP-80 option — 42-character lines. One-time QR — the server rejected a fake code, the sale continued. AI advisor: “Stop”, “New chat”, “History”. The till picked up the Groq and OpenRouter keys by itself.

---

# Türkçe

### Yeni

- **XP-80 ve benzeri fiş yazıcıları (satırda 42 karakter).** «Ayarlar → Yazdırma → Rulo genişliği» listesinde **«80 mm — 42 karakter (XP-80 ve benzerleri)»** seçeneği: 42 karakter ve 512 nokta kendiliğinden ayarlanır.
- **«Start» tarifesi:** herhangi bir «Standart» özelliği **Marketplace → Ek özellikler** bölümünden ayrı bağlanabilir — her biri 1500 som etkinleştirme + ayda 400 som. **«Hesap»** sekmesinde «Standart» açıklaması ve «Start» ile karşılaştırma.
- **Yapay zekâ internette ücretsiz arar** — Groq anahtarıyla. Gemini'nin günlük limiti bitince OpenRouter'ın ücretsiz yedek modelleri yanıtlar. Anahtarlar **Ayarlar → İşlemler**'de Gemini anahtarının yanına veya yapay zekâ danışmanına girilir.
- **Yapay zekâ danışmanı:** **«■ Durdur»** düğmesi; «Yeni sohbet» öncekini saklar; **🕘 «Geçmiş»** — geçmiş sohbetler. Sesli yanıtlar daha hızlı; ürün fotoğrafını adıyla ve internette bulur.
- **Aynı bilgisayardaki kasa ve sahip programı** yapay zekâ anahtarlarını ve Telegram botunu birbirinden kendiliğinden alır.
- **NurCRM uygulamasındaki müşteri QR'ı** (tek kullanımlık kod): kasa müşteriyi sunucu üzerinden bulur, yenisini kendisi ekler.
- **«Web sitesi siparişleri»** — vitrin ve Telegram botu siparişleri NurCRM sunucusundan; durumlar «Kabul edildi → Hazır → Teslim edildi» veya «İptal edildi».

### Önce → sonra

| Ne | Önce | Sonra |
|---|---|---|
| XP-80'de fiş | Çizgiler ve «0 СОМ» alt satıra kayıyordu | «80 mm — 42 karakter» seçeneği, düzgün fiş |
| «Start»ta iade | Gizliydi | Çalışıyor |
| «Start»ta «Standart» özellikleri | Yapay zekâ ve bot ücretsiz görünüyordu | Gizli; Marketplace'te bağlanır |
| Yapay zekâ için internet araması | Yalnızca ücretli Google anahtarıyla | Groq anahtarıyla ücretsiz |
| Fotoğraf aramasında «Yeni sohbet» | Giriş kapalı kalıyordu | Arama durur, eski sohbet «Geçmiş»te |
| Kasa ve sahip programında anahtarlar | Her birine girmek gerekiyordu | Kendiliğinden alınır |
| Tek kullanımlık QR | «Henüz desteklenmiyor» | Müşteri fişte seçilir |
| Web sitesi siparişleri | Yalnızca WhatsApp'ta | Sunucudan durumlarla liste |

### Kontrol edildi (05.10, test hesabı)

Kasa: art arda 14 satış — 14'te 14, fişler yazdırıldı; borçlu satış ve borç ödemesi; fişin tam iadesi; para girişi ve çıkışı; Z raporuyla vardiya kapatma ve yeni vardiya açma. XP-80 seçeneği — satırda 42 karakter. Tek kullanımlık QR — sunucu sahte kodu reddetti, satış devam etti. Yapay zekâ danışmanı: «Durdur», «Yeni sohbet», «Geçmiş». Kasa Groq ve OpenRouter anahtarlarını kendisi aldı.

---

# O'zbekcha

### Yangi

- **XP-80 va o'xshash chek printerlari (qatorda 42 belgi).** «Sozlamalar → Chop etish → Lenta kengligi» ro'yxatida **«80 mm — 42 belgi (XP-80 va o'xshashlari)»** varianti: 42 belgi va 512 nuqta o'zi qo'yiladi.
- **«Start» tarifi:** «Standart»ning istalgan funksiyasini **Marketpleys → Qo'shimcha funksiyalar** bo'limida alohida ulash mumkin — har biri uchun 1500 so'm faollashtirish + oyiga 400 so'm. **«Akkaunt»** yorlig'ida «Standart» tavsifi va «Start» bilan taqqoslash.
- **SI internetda bepul qidiradi** — Groq kaliti bilan. Gemini kunlik limiti tugaganda OpenRouter bepul zaxira modellari javob beradi. Kalitlar **Sozlamalar → Operatsiyalar** bo'limida Gemini kaliti yoniga yoki SI maslahatchiga qo'yiladi.
- **SI maslahatchi:** **«■ To'xtatish»** tugmasi; «Yangi suhbat» oldingisini saqlaydi; **🕘 «Tarix»** — oldingi suhbatlar. Ovoz bilan tezroq javob beradi; mahsulot rasmini nomi bo'yicha va internetdan topadi.
- **Bir kompyuterdagi kassa va ega dasturi** SI kalitlari va Telegram-botni bir-biridan o'zi oladi.
- **NurCRM ilovasidagi xaridor QR kodi** (bir martalik kod): kassa mijozni server orqali topadi, yangisini o'zi qo'shadi.
- **«Saytdan buyurtmalar»** — vitrina va Telegram-bot buyurtmalari NurCRM serveridan; holatlar «Qabul qilindi → Tayyor → Berildi» yoki «Bekor qilindi».

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| XP-80dagi chek | Chiziqlar va «0 СОМ» yangi qatorga o'tardi | «80 mm — 42 belgi» varianti, chek tekis |
| «Start»da qaytarish | Yashirilgan | Ishlaydi |
| «Start»dagi «Standart» funksiyalari | SI va bot to'lovsiz ko'rinardi | Yashirilgan; Marketpleysda ulanadi |
| SI uchun internetda qidirish | Faqat pullik Google kaliti bilan | Groq kaliti bilan bepul |
| Rasm qidiruvida «Yangi suhbat» | Kiritish o'chiq qolardi | Qidiruv to'xtaydi, eski suhbat «Tarix»da |
| Kassa va ega dasturidagi kalitlar | Har biriga kiritish kerak edi | O'zi olinadi |
| Bir martalik QR | «Hozircha qabul qilinmaydi» | Mijoz chekda tanlanadi |
| Saytdan buyurtmalar | Faqat WhatsApp'da | Serverdan holatlar bilan ro'yxat |

### Tekshirildi (05.10, sinov akkaunti)

Kassa: ketma-ket 14 ta sotuv — 14 tadan 14, cheklar chop etildi; qarzga sotuv va qarzni to'lash; chekni to'liq qaytarish; pul kiritish va olish; smenani Z-hisobot bilan yopish va yangisini ochish. XP-80 varianti — qatorda 42 belgi. Bir martalik QR — server soxta kodni rad etdi, sotuv davom etdi. SI maslahatchi: «To'xtatish», «Yangi suhbat», «Tarix». Kassa Groq va OpenRouter kalitlarini o'zi oldi.
