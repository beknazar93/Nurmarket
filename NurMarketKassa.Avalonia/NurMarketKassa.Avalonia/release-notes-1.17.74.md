## v1.17.74 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Умное сопоставление накладных с товарами склада и поиск фото товаров пачками в Нур Советнике. Всё из 1.17.73 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.74.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.74.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.74.zip` (Intel); Android — `nurmarket-kassa-android-1.17.74.apk`.

Обновление не меняет ваши настройки.

---

# Русский

**Тестовая версия — только для тестировщиков.**

### Что нового

- **Нур Советник ищет фото пачками.** «Найди фото для товаров без фото» берёт по 20 (или 50) товаров по очереди, ищет в интернете для каждого и помнит, что уже проверено, — следующий раз продолжает с того места. Кнопки «Искать дальше», «Остановить», «Искать автоматически по очереди». Видео и соцсети (YouTube, VK и т. п.) не принимаются, неуверенные совпадения показываются отдельно с пометкой «проверьте» и не входят в «Поставить все». Вместе с фото ставится описание из интернета (если в карточке его не было).
- **Накладная: названия сверяются умнее.** Понимаются сокращения («Мол.»), «1л» = «1 литр» = «1000 мл», латиница и кириллица («Coca-Cola» = «Кока-кола»). Похожий товар предлагается списком до 5, вы решаете: тот же (остаток увеличится) или новый.
- **Касса запоминает ваш выбор у поставщика.** В следующей накладной тот же товар находится сам (кнопка «Забыть соответствие»). Кнопка «Подобрать ИИ» предлагает товар склада для новых строк (нужен ключ Gemini, всегда с вашим подтверждением).
- **Накладная по фото и рукописная.** ИИ опирается на названия вашего склада, неуверенные строки подсвечиваются «проверьте по фото», строки, где сумма не сходится, перечитываются ещё раз. В закупке по фото у нового товара есть кнопка «Есть похожие».

---

# Кыргызча

**Сыноо версиясы — сыноочулар үчүн гана.**

### Эмнелер жаңы

- **Нур Кеңешчи сүрөттү пачкалап издейт.** «Сүрөтсүз товарларга сүрөт тап» 20 (же 50) товардан кезеги менен алат, ар бирин интернеттен издейт жана эмнени текшергенин эстеп калат — кийинки жолу ошол жерден улантат. «Андан ары издөө», «Токтотуу», «Автоматтык түрдө кезеги менен издөө» баскычтары. Видео жана соц тармактар (YouTube, VK ж. б.) кабыл алынбайт, ишенимсиз дал келүүлөр өзүнчө «текшериңиз» белгиси менен чыгат жана «Баарын коюу» ичине кирбейт. Сүрөт менен бирге интернеттен сүрөттөмө да коюлат (карточкада болбосо).
- **Накладной: аталыштар акылдуураак салыштырылат.** Кыскартуулар («Мол.»), «1л» = «1 литр» = «1000 мл», латын жана кирилл («Coca-Cola» = «Кока-кола») түшүнүлөт. Окшош товар 5ке чейин тизме менен сунушталат, сиз чечесиз: ошол эле (калдык көбөйөт) же жаңы.
- **Касса жеткирүүчү боюнча тандооңузду эстейт.** Кийинки накладнойдо ошол эле товар өзү табылат («Дал келүүнү унутуу» баскычы). «ЖИ менен тандоо» баскычы жаңы саптар үчүн кампадан товар сунуштайт (Gemini ачкычы керек, ар дайым сиздин ырастооңуз менен).
- **Сүрөт жана кол менен жазылган накладной.** ЖИ сиздин кампанын аталыштарына таянат, ишенимсиз саптар «сүрөт боюнча текшериңиз» деп белгиленет, суммасы дал келбеген саптар кайра окулат. Сүрөт боюнча сатып алууда жаңы товарда «Окшоштору бар» баскычы бар.

---

# English

**Test version — for testers only.**

### What's new

- **Nur Advisor searches for photos in batches.** “Find photos for products without a photo” takes 20 (or 50) products at a time, searches the web for each one and remembers what was already checked, so the next request continues where it stopped. Buttons “Search more”, “Stop” and “Search automatically, batch after batch”. Video sites and social networks (YouTube, VK and so on) are rejected, uncertain matches are shown separately with a “check” mark and are not part of “Set all”. A description from the web is set together with the photo (if the card had none).
- **Invoice: smarter name matching.** Abbreviations (“Mol.”), “1l” = “1 litre” = “1000 ml”, Latin and Cyrillic (“Coca-Cola” = “Кока-кола”) are understood. A similar item is offered as a list of up to 5 and you decide: the same item (stock increases) or a new one.
- **The till remembers your choice per supplier.** Next time the same item is found by itself (“Forget the match” button). The “Match with AI” button suggests a stock item for new lines (needs a Gemini key, always with your confirmation).
- **Photo and handwritten invoices.** The AI leans on your stock names, uncertain lines are marked “check against the photo”, lines whose total does not add up are re-read once more. In a photo purchase a new item has a “Similar items” button.

---

# Türkçe

**Test sürümü — yalnızca test edenler için.**

### Yenilikler

- **Nur Danışman fotoğrafları gruplar halinde arıyor.** «Fotoğrafsız ürünlere fotoğraf bul» her seferinde 20 (veya 50) ürünü alır, her biri için internette arar ve nelerin kontrol edildiğini hatırlar — sonraki sefer kaldığı yerden devam eder. «Aramaya devam», «Durdur», «Otomatik olarak sırayla ara» düğmeleri. Video ve sosyal ağlar (YouTube, VK vb.) kabul edilmez, emin olunmayan eşleşmeler ayrı ve «kontrol edin» işaretiyle gösterilir, «Tümünü ayarla»ya girmez. Kartta açıklama yoksa fotoğrafla birlikte internetten açıklama da eklenir.
- **Fatura: adlar daha akıllı eşleştirilir.** Kısaltmalar («Mol.»), «1l» = «1 litre» = «1000 ml», Latin ve Kiril («Coca-Cola» = «Кока-кола») anlaşılır. Benzer ürün 5'e kadar liste olarak önerilir, siz karar verirsiniz: aynı ürün (stok artar) veya yeni.
- **Kasa tedarikçi başına seçiminizi hatırlar.** Sonraki faturada aynı ürün kendiliğinden bulunur («Eşleşmeyi unut» düğmesi). «YZ ile eşleştir» düğmesi yeni satırlar için depodan ürün önerir (Gemini anahtarı gerekir, her zaman onayınızla).
- **Fotoğraflı ve el yazısı fatura.** YZ deponuzdaki adlara dayanır, emin olunmayan satırlar «fotoğrafa göre kontrol edin» diye işaretlenir, tutarı tutmayan satırlar bir kez daha okunur. Fotoğraflı alımda yeni üründe «Benzerleri var» düğmesi bulunur.

---

# O'zbekcha

**Sinov versiyasi — faqat sinovchilar uchun.**

### Yangiliklar

- **Nur Maslahatchi rasmlarni guruhlab qidiradi.** «Rasmsiz mahsulotlarga rasm top» har safar 20 (yoki 50) ta mahsulotni oladi, har birini internetdan qidiradi va nimalar tekshirilganini eslab qoladi — keyingi safar qolgan joyidan davom etadi. «Davom ettirish», «To'xtatish», «Avtomatik ravishda navbat bilan qidirish» tugmalari. Video va ijtimoiy tarmoqlar (YouTube, VK va boshqalar) qabul qilinmaydi, ishonchsiz mos kelishlar alohida «tekshiring» belgisi bilan chiqadi va «Hammasini o'rnatish»ga kirmaydi. Kartada tavsif bo'lmasa, rasm bilan birga internetdan tavsif ham o'rnatiladi.
- **Yuk xati: nomlar aqlliroq solishtiriladi.** Qisqartmalar («Mol.»), «1l» = «1 litr» = «1000 ml», lotin va kirill («Coca-Cola» = «Кока-кола») tushuniladi. O'xshash mahsulot 5 tagacha ro'yxat bilan taklif qilinadi, siz hal qilasiz: aynan o'sha (qoldiq oshadi) yoki yangi.
- **Kassa yetkazib beruvchi bo'yicha tanlovingizni eslab qoladi.** Keyingi yuk xatida o'sha mahsulot o'zi topiladi («Moslikni unutish» tugmasi). «SI bilan tanlash» tugmasi yangi qatorlar uchun ombordan mahsulot taklif qiladi (Gemini kaliti kerak, doim sizning tasdig'ingiz bilan).
- **Rasm va qo'lda yozilgan yuk xati.** SI omboringizdagi nomlarga tayanadi, ishonchsiz qatorlar «rasm bo'yicha tekshiring» deb belgilanadi, summasi to'g'ri kelmagan qatorlar yana bir bor o'qiladi. Rasm bo'yicha xaridda yangi mahsulotda «O'xshashlari bor» tugmasi bor.
