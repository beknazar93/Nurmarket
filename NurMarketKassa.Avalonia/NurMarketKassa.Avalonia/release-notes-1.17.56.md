## v1.17.56

Сенсорные моноблоки: экранная клавиатура по касанию и видимые поля цены в окне товара; ИИ-советник находит фото товаров в интернете, открывает разделы программы голосом, звонок подключается за секунды.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Сенсорные моноблоки.** Касание пальцем любого поля ввода открывает экранную клавиатуру Windows (настройка «Показывать экранную клавиатуру автоматически», включена по умолчанию). В окне **«Новый товар»** поля цены видны и на небольшом экране: поля плотнее, полоса прокрутки видна всегда, поле при касании само прокручивается в вид.
- **Фото товара из интернета.** ИИ-советник ищет варианты через Яндекс.Картинки (Ozon, Яндекс.Маркет и другие магазины), фото магазинов — первыми. Варианты пронумерованы: «поставь фото 4» или «четвёртое» (текстом или голосом) — программа ставит его сама.
- **Разговор с ИИ-советником управляет программой.** «Открой склад», «перейди в зарплату», «открой финансы», «открой товар Aos бальзам» — программа сразу открывает любой раздел меню или товар на складе. «Открой склад, проверь фото» — открывает товар, которому только что поставили фото.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Экранная клавиатура на сенсорном моноблоке | Только кнопкой, при касании поля не появлялась | Появляется при касании поля |
| Поля цены в «Новом товаре» на экране 1366×768 | Ниже края окна, не видно | Видны (проверено при высоте окна 712 и 620) |
| «Найди фото для …» | Часто «Нашёл вариантов фото: 0» (поисковик блокировал запросы) | 8 вариантов из Ozon, Маркета и других |
| «Поставь четвёртое фото» голосом | Советник обещал, фото не ставилось | Ставится |
| «Открой склад» в разговоре | Через 15–17 с | Сразу |
| Звонок с ИИ-советником при медленном сервере | «Подключаюсь…» до 40 с | Подключается за секунды, данные магазина приходят следом |
| Одна часть данных для ИИ не загрузилась | ИИ оставался без всех данных магазина | Остальные данные доходят |

### Исправлено

- Настройки иногда не сохранялись, если файл был на миг занят.
- Лишние ошибки в журнале программы владельца при сбое связи.

### Проверено (06–07.10, тестовый аккаунт)

- **Касса:** 7 продаж (наличные и перевод) — все прошли, 0,5–1,5 с; без интернета 2 продажи — в очереди, после связи досланы за 0,3 с каждая.
- **Окно «Новый товар»** при высоте 712 и 620 точек — поля цены видны целиком.
- **ИИ-советник:** «Aos Extra pover 450гр бальзам — найди фото» — 8 вариантов, фото с ozon.ru поставлено и видно на складе; «открой зарплату», «открой товар Aos бальзам», «перейди в финансы» — открыты нужные разделы.

Не проверено на этом ПК: клавиатура по касанию (нужен сенсорный экран) — проверьте на моноблоке.

---

# Кыргызча

### Жаңы

- **Сенсордук моноблоктор.** Каалаган киргизүү талаасын манжа менен басканда Windows экрандык клавиатурасы ачылат («Экрандык клавиатураны автоматтык көрсөтүү» жөндөөсү, демейки күйүк). **«Жаңы товар»** терезесинде баа талаалары кичине экранда да көрүнөт: талаалар жыйнакы, жылдыруу тилкеси дайыма көрүнөт, басылган талаа өзү көрүнүүгө жылат.
- **Интернеттен товардын сүрөтү.** ИИ-кеңешчи варианттарды Яндекс.Сүрөттөр аркылуу издейт (Ozon, Яндекс.Маркет жана башка дүкөндөр). Варианттар номерленген: «4-сүрөттү кой» (текст же үн менен) — программа өзү коёт.
- **ИИ-кеңешчи менен маек программаны башкарат.** «Кампаны ач», «эмгек акыга өт», «… товарын ач» — программа ошол замат каалаган бөлүмдү же кампадагы товарды ачат.

### Болгон → болду

| Эмне | Болгон | Болду |
|---|---|---|
| Сенсордук моноблокто экрандык клавиатура | Баскыч менен гана | Талааны басканда чыгат |
| 1366×768 экранда «Жаңы товардагы» баа талаалары | Терезенин астында, көрүнчү эмес | Көрүнөт |
| «… үчүн сүрөт тап» | Көп учурда «варианттар: 0» | Ozon, Маркет ж.б. 8 вариант |
| «4-сүрөттү кой» үн менен | Кеңешчи убада берчү, сүрөт коюлчу эмес | Коюлат |
| Маекте «кампаны ач» | 15–17 секунддан кийин | Ошол замат |
| Сервер жай болгондо ИИ-кеңешчи менен чалуу | «Туташып жатам…» 40 секундга чейин | Бир нече секундда |

### Оңдолду

- Файл бир саамга бош эмес болсо жөндөөлөр кээде сакталчу эмес.
- Байланыш үзүлгөндө ээсинин программасынын журналындагы ашыкча каталар.

### Текшерилди (06–07.10, сыноо аккаунту)

- **Касса:** 7 сатуу (накталай жана которуу) — баары өттү, 0,5–1,5 сек; интернетсиз 2 сатуу кезекке түшүп, байланыш келгенде жөнөтүлдү.
- **«Жаңы товар» терезеси** 712 жана 620 бийиктикте — баа талаалары толук көрүнөт.
- **ИИ-кеңешчи:** сүрөт издөө — 8 вариант, сүрөт коюлуп кампада көрүнөт; «эмгек акыны ач», «финансыга өт» — бөлүмдөр ачылды.

Бул компьютерде текшерилген жок: басканда клавиатура (сенсордук экран керек).

---

# English

### New

- **Touchscreen all-in-ones.** Tapping any input field with a finger opens the Windows on-screen keyboard (the “Show the on-screen keyboard automatically” setting, on by default). In the **“New product”** window the price fields are visible on small screens: fields are tighter, the scroll bar is always shown, and a tapped field scrolls into view.
- **Product photos from the web.** The AI advisor finds options via Yandex Images (Ozon, Yandex Market and other shops), shop photos first. Options are numbered: “set photo 4” or “the fourth one” (typed or spoken) — the program sets it by itself.
- **Talking to the AI advisor controls the program.** “Open the warehouse”, “go to salary”, “open finance”, “open product …” — the program opens any menu section or the product in the warehouse right away.

### Before → now

| What | Before | Now |
|---|---|---|
| On-screen keyboard on a touchscreen all-in-one | Only with the button | Appears when you tap a field |
| Price fields in “New product” on a 1366×768 screen | Below the window edge, not visible | Visible |
| “Find a photo for …” | Often “0 photo options” | 8 options from Ozon, Market and others |
| “Set the fourth photo” by voice | The advisor promised, nothing happened | It is set |
| “Open the warehouse” in a call | After 15–17 s | Right away |
| A call with the AI advisor when the server is slow | “Connecting…” up to 40 s | Connects in seconds |

### Fixed

- Settings were sometimes not saved when the file was busy for a moment.
- Extra errors in the owner program log when the connection failed.

### Checked (06–07.10, test account)

- **Till:** 7 sales (cash and transfer) — all passed, 0.5–1.5 s; 2 offline sales were queued and sent when the connection returned.
- **“New product” window** at 712 and 620 px height — price fields fully visible.
- **AI advisor:** photo search — 8 options, the photo was set and shows in the warehouse; “open salary”, “go to finance” — the sections opened.

Not checked on this PC: keyboard on tap (needs a touchscreen).

---

# Türkçe

### Yeni

- **Dokunmatik ekranlı hepsi bir arada bilgisayarlar.** Herhangi bir giriş alanına parmakla dokunmak Windows ekran klavyesini açar («Ekran klavyesini otomatik göster» ayarı, varsayılan olarak açık). **«Yeni ürün»** penceresinde fiyat alanları küçük ekranda da görünür: alanlar daha sık, kaydırma çubuğu her zaman görünür, dokunulan alan kendiliğinden görünür hale kayar.
- **İnternetten ürün fotoğrafı.** Yapay zekâ danışmanı seçenekleri Yandex Görseller üzerinden bulur (Ozon, Yandex Market ve diğer mağazalar). Seçenekler numaralı: «4. fotoğrafı koy» (yazarak veya sesle) — program kendisi koyar.
- **Yapay zekâ danışmanıyla konuşma programı yönetir.** «Depoyu aç», «maaşa geç», «… ürününü aç» — program istenen menü bölümünü veya depodaki ürünü hemen açar.

### Önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Dokunmatik ekranda ekran klavyesi | Sadece düğmeyle | Alana dokununca açılır |
| 1366×768 ekranda «Yeni ürün» fiyat alanları | Pencere kenarının altında, görünmüyordu | Görünür |
| «… için fotoğraf bul» | Sık sık «0 seçenek» | Ozon, Market ve diğerlerinden 8 seçenek |
| Sesle «4. fotoğrafı koy» | Danışman söz veriyordu, fotoğraf konmuyordu | Konuyor |
| Görüşmede «depoyu aç» | 15–17 saniye sonra | Hemen |
| Sunucu yavaşken yapay zekâ görüşmesi | «Bağlanıyor…» 40 saniyeye kadar | Saniyeler içinde |

### Düzeltildi

- Dosya bir anlığına meşgulse ayarlar bazen kaydedilmiyordu.
- Bağlantı kesilince sahip programı günlüğünde gereksiz hatalar.

### Kontrol edildi (06–07.10, test hesabı)

- **Kasa:** 7 satış (nakit ve havale) — hepsi geçti, 0,5–1,5 sn; internetsiz 2 satış sıraya alındı ve bağlantı gelince gönderildi.
- **«Yeni ürün» penceresi** 712 ve 620 piksel yükseklikte — fiyat alanları tamamen görünür.
- **Yapay zekâ danışmanı:** fotoğraf arama — 8 seçenek, fotoğraf konuldu ve depoda görünüyor; «maaşı aç», «finansa geç» — bölümler açıldı.

Bu bilgisayarda kontrol edilmedi: dokununca klavye (dokunmatik ekran gerekir).

---

# O'zbekcha

### Yangi

- **Sensorli monobloklar.** Istalgan kiritish maydoniga barmoq bilan tegilsa Windows ekran klaviaturasi ochiladi («Ekran klaviaturasini avtomatik ko'rsatish» sozlamasi, sukut bo'yicha yoqilgan). **«Yangi mahsulot»** oynasida narx maydonlari kichik ekranda ham ko'rinadi: maydonlar zichroq, aylantirish chizig'i doim ko'rinadi, tegilgan maydon o'zi ko'rinishga suriladi.
- **Internetdan mahsulot rasmi.** SI maslahatchi variantlarni Yandex Rasmlar orqali topadi (Ozon, Yandex Market va boshqa do'konlar). Variantlar raqamlangan: «4-rasmni qo'y» (matn yoki ovoz bilan) — dastur o'zi qo'yadi.
- **SI maslahatchi bilan suhbat dasturni boshqaradi.** «Omborni och», «ish haqiga o't», «… mahsulotini och» — dastur istalgan menyu bo'limini yoki ombordagi mahsulotni darhol ochadi.

### Avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Sensorli monoblokda ekran klaviaturasi | Faqat tugma bilan | Maydonga tegilganda chiqadi |
| 1366×768 ekranda «Yangi mahsulot» narx maydonlari | Oyna chetidan pastda, ko'rinmasdi | Ko'rinadi |
| «… uchun rasm top» | Ko'pincha «0 variant» | Ozon, Market va boshqalardan 8 variant |
| Ovoz bilan «4-rasmni qo'y» | Maslahatchi va'da berardi, rasm qo'yilmasdi | Qo'yiladi |
| Suhbatda «omborni och» | 15–17 soniyadan keyin | Darhol |
| Server sekin bo'lganda SI bilan qo'ng'iroq | «Ulanmoqda…» 40 soniyagacha | Bir necha soniyada |

### Tuzatildi

- Fayl bir lahzaga band bo'lsa sozlamalar ba'zan saqlanmasdi.
- Aloqa uzilganda ega dasturi jurnalidagi ortiqcha xatolar.

### Tekshirildi (06–07.10, sinov akkaunti)

- **Kassa:** 7 sotuv (naqd va o'tkazma) — hammasi o'tdi, 0,5–1,5 soniya; internetsiz 2 sotuv navbatga tushdi va aloqa qaytgach yuborildi.
- **«Yangi mahsulot» oynasi** 712 va 620 piksel balandlikda — narx maydonlari to'liq ko'rinadi.
- **SI maslahatchi:** rasm qidirish — 8 variant, rasm qo'yildi va omborda ko'rinadi; «ish haqini och», «moliyaga o't» — bo'limlar ochildi.

Bu kompyuterda tekshirilmadi: tegilganda klaviatura (sensorli ekran kerak).
