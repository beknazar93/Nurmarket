## v1.17.57

Нур Советник сам собирает данные и показывает таблицей, фото товаров — прямо из интернета, редактор сайта голосом, штрихкод нового товара по названию.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Нур Советник.** Так теперь зовут ИИ-советника (по-кыргызски — Нур Кеңешчи). На вопросы с цифрами он сам загружает данные с сервера и показывает таблицей: «чеки за вчера», «продажи за 6.10», «чеки за неделю» — чеки, кассиры, оплата, итоги. В разговоре голосом таблица появляется в чате, итог советник говорит вслух.
- **Фото товара — прямо из интернета.** «Найди фото Mars» — варианты из магазинов (Ozon, Маркет и другие) с номерами, в чате крутится индикатор поиска. Подпись каждого фото сверяется с названием товара: случайные картинки отбрасываются, магазины — первыми. «Поставь фото 4» — ставится в карточку.
- **Разделы открываются только по команде.** «Открой склад», «перейди в зарплату», «кампаны ач» — сразу открывается раздел или товар. Без слова «открой» программа вкладки не переключает, отвечает в чате.
- **Редактор сайта голосом и текстом.** «Поставь тему Ала-Тоо», «тёмный стиль сайта», «опубликуй сайт». Кнопка «Смотреть сайт» и ссылка под строкой состояния открывают market.nurcrm.kg/catalog/… — там видно новое оформление.
- **Штрихкод нового товара по названию.** В окне «Новый товар» и в карточке прихода по накладной — до трёх штрихкодов из открытой базы barcode-list.ru с названием товара из базы. Сверяете название и вес и нажимаете нужный; сам штрихкод не ставится.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| «Покажи чеки за вчера» | «В сводке только общая выручка, зайдите в NurMarket» | Таблица: кассиры, чеки, суммы, наличные и безнал, возвраты |
| Таблицы в разговоре голосом | Только голосом | Таблица в чате, итог голосом |
| Фото товара на просьбу «найди фото» | Иногда фото со склада или чужого товара («Нан» на «банан») | Только поиск в интернете, с проверкой по названию |
| Поиск фото | Без признаков работы | Индикатор и секунды в чате |
| «Найди Mars» в разговоре | Открывался склад | Сведения в чате, склад — только по «открой» |
| После «Выполнить» | Программа сама уходила на склад | Остаётся в чате |
| «Опубликовать» в редакторе | Писало «покупатели уже видят», а nurcrm.kg/catalog показывал прежний вид | Пишет, где виден новый вид (market.nurcrm.kg) |
| Под ответом ИИ | Иногда ссылки «Найдено в интернете» от прошлых вопросов | Только когда просили поиск |

### Проверено (07.10, тестовый аккаунт)

- **Касса:** 3 продажи (наличные и перевод) — все прошли, 0,5–1,8 с.
- **Нур Советник:** «Покажи чеки за вчера» — с сервера загружено 120 чеков за 0,9 с, ответ таблицей за 3 с; «кампаны ач» — открылся склад; «как тебя зовут» — «Нур Советник», «сенин атың ким» — «Нур Кеңешчи».
- **Сайт:** опубликованная тема видна на market.nurcrm.kg/catalog/nurmarket.
- **Поиск фото:** для «Батончик Mars 50г» из 30 фото Яндекса отобраны 29 с названием товара в подписи; для «Банан» отброшены картинки фотобанков.

Не проверено на этом ПК: таблица во время живого разговора голосом (нужен микрофон).

---

# Кыргызча

### Жаңы

- **Нур Кеңешчи.** ИИ-кеңешчинин аты ушундай (орусча — Нур Советник). Сандар тууралуу суроолорго маалыматты серверден өзү жүктөп, таблица менен көрсөтөт: «кечээки чектер», «6.10 сатуулар», «жумалык чектер». Үн менен сүйлөшүүдө таблица чатта чыгат, жыйынтыгын кеңешчи үн менен айтат.
- **Товардын сүрөтү — түз интернеттен.** «Mars сүрөтүн тап» — дүкөндөрдөн номерленген варианттар, чатта издөө белгиси. Ар бир сүрөттүн жазуусу товардын аты менен салыштырылат. «4-сүрөттү кой» — карточкага коюлат.
- **Бөлүмдөр буйрук менен гана ачылат.** «Кампаны ач», «эмгек акыга өт» — бөлүм же товар ачылат. «Ач» деген сөз жок болсо программа өтмөктөрдү алмаштырбайт.
- **Сайттын редактору үн жана текст менен.** «Ала-Тоо темасын кой», «сайтты жарыяла». «Сайтты көрүү» market.nurcrm.kg ачат — жаңы жасалгалоо ошол жерде.
- **Жаңы товардын штрихкоду аталышы боюнча** barcode-list.ru ачык базасынан — салыштырып, керектүүсүн басасыз.

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| «Кечээки чектерди көрсөт» | «Сводкада жалпы киреше гана» | Таблица: кассирлер, чектер, суммалар |
| Үн менен сүйлөшүүдө таблица | Үн менен гана | Таблица чатта, жыйынтыгы үн менен |
| «Сүрөт тап» | Кээде кампадагы же башка товардын сүрөтү | Интернеттен гана, аты менен текшерилет |
| «Mars тап» маекте | Кампа ачылчу | Маалымат чатта, кампа — «ач» деген буйрук менен |

### Текшерилди (07.10, сыноо аккаунту)

- **Касса:** 3 сатуу — баары өттү, 0,5–1,8 сек.
- **Нур Кеңешчи:** кечээки 120 чек 0,9 секундда жүктөлүп, таблица менен көрсөтүлдү; «кампаны ач» — кампа ачылды.
- **Сайт:** жарыяланган тема market.nurcrm.kg/catalog/nurmarket дарегинде көрүнөт.

---

# English

### New

- **Nur Advisor.** The AI advisor's new name (Nur Keңeshchi in Kyrgyz). For questions with numbers it loads data from the server itself and shows tables: “receipts for yesterday”, “sales on 6.10”, “receipts for the week”. In a voice call the table appears in the chat and the advisor says the total.
- **Product photos straight from the web.** “Find a photo of Mars” — numbered options from shops with a search indicator in the chat; each caption is checked against the product name. “Set photo 4” — saved to the product card.
- **Sections open only on command.** “Open the warehouse”, “go to salary” — the section or product opens right away. Without “open” the program doesn't switch tabs.
- **Website editor by voice and text.** “Apply the Ala-Too theme”, “publish the website”. “View website” opens market.nurcrm.kg, where the new design is visible.
- **Barcode of a new product by name** from the open barcode-list.ru database — check and press the right one.

### Before → now

| What | Before | Now |
|---|---|---|
| “Show yesterday's receipts” | “Only total revenue in the summary” | A table: cashiers, receipts, totals |
| Tables in a voice call | Voice only | Table in the chat, total spoken |
| “Find a photo” | Sometimes a warehouse photo or another product | Web search only, checked by name |
| “Find Mars” in a call | The warehouse opened | Details in the chat; warehouse only on “open” |

### Checked (07.10, test account)

- **Till:** 3 sales — all passed, 0.5–1.8 s.
- **Nur Advisor:** 120 receipts for yesterday loaded in 0.9 s and shown as a table; “кампаны ач” opened the warehouse.
- **Website:** the published theme is visible on market.nurcrm.kg/catalog/nurmarket.

---

# Türkçe

### Yeni

- **Nur Danışman.** Yapay zekâ danışmanının yeni adı. Sayılarla ilgili sorularda verileri sunucudan kendisi yükler ve tablo gösterir: «dünün fişleri», «6.10 satışları», «haftanın fişleri». Sesli görüşmede tablo sohbette çıkar, özeti sesle söylenir.
- **Ürün fotoğrafı doğrudan internetten.** Mağazalardan numaralı seçenekler, sohbette arama göstergesi; her açıklama ürün adıyla karşılaştırılır. «4. fotoğrafı koy» — ürün kartına konur.
- **Bölümler yalnızca komutla açılır.** «Depoyu aç», «maaşa geç» — hemen açılır. «Aç» denmezse program sekme değiştirmez.
- **Site düzenleyici sesle ve yazıyla.** «Ala-Too temasını koy», «siteyi yayınla». «Siteyi gör» market.nurcrm.kg'yi açar.
- **Yeni ürünün barkodu ada göre** açık barcode-list.ru veritabanından — karşılaştırıp doğru olana basarsınız.

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| «Dünün fişlerini göster» | «Özette sadece toplam ciro» | Tablo: kasiyerler, fişler, tutarlar |
| Sesli görüşmede tablo | Sadece ses | Tablo sohbette, özet sesle |
| «Fotoğraf bul» | Bazen depo fotoğrafı veya başka ürün | Sadece internet, ada göre kontrol |

### Kontrol edildi (07.10, test hesabı)

- **Kasa:** 3 satış — hepsi geçti, 0,5–1,8 sn.
- **Nur Danışman:** dünün 120 fişi 0,9 sn'de yüklendi ve tablo olarak gösterildi.
- **Site:** yayınlanan tema market.nurcrm.kg/catalog/nurmarket adresinde görünüyor.

---

# O'zbekcha

### Yangi

- **Nur Maslahatchi.** SI maslahatchining yangi nomi. Raqamli savollarda ma'lumotlarni serverdan o'zi yuklaydi va jadval ko'rsatadi: «kechagi cheklar», «6.10 sotuvlari», «haftalik cheklar». Ovozli suhbatda jadval chatda chiqadi, yakuni ovozda aytiladi.
- **Mahsulot rasmi to'g'ridan-to'g'ri internetdan.** Do'konlardan raqamlangan variantlar, chatda qidiruv belgisi; har bir izoh mahsulot nomi bilan solishtiriladi. «4-rasmni qo'y» — kartochkaga qo'yiladi.
- **Bo'limlar faqat buyruq bilan ochiladi.** «Omborni och», «ish haqiga o't» — darhol ochiladi. «Och» deyilmasa dastur yorliqni almashtirmaydi.
- **Sayt muharriri ovoz va matn bilan.** «Ala-Too mavzusini qo'y», «saytni e'lon qil». «Saytni ko'rish» market.nurcrm.kg ni ochadi.
- **Yangi mahsulot shtrix-kodi nomi bo'yicha** ochiq barcode-list.ru bazasidan — solishtirib keraklisini bosasiz.

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| «Kechagi cheklarni ko'rsat» | «Xulosada faqat umumiy tushum» | Jadval: kassirlar, cheklar, summalar |
| Ovozli suhbatda jadval | Faqat ovoz | Jadval chatda, yakuni ovozda |
| «Rasm top» | Ba'zan ombor rasmi yoki boshqa mahsulot | Faqat internet, nomi bo'yicha tekshiriladi |

### Tekshirildi (07.10, sinov akkaunti)

- **Kassa:** 3 sotuv — hammasi o'tdi, 0,5–1,8 soniya.
- **Nur Maslahatchi:** kechagi 120 chek 0,9 soniyada yuklandi va jadval qilib ko'rsatildi.
- **Sayt:** e'lon qilingan mavzu market.nurcrm.kg/catalog/nurmarket da ko'rinadi.
