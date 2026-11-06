## v1.17.79

Срочное исправление сумм в программе владельца («Продажи», «Финансы», «Топ», «Смены»), оплата долга после частичного возврата, быстрее запуск, Склад, весы «Штрих РС-200». Всё из 1.17.78 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.79.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.79.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.79.zip` (Intel); Android — `nurmarket-kassa-android-1.17.79.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Суммы в программе владельца.** Срочно: в таблицах «Продажи» и «Финансы» колонка «Сумма» (и «Выручка» в «Топе» и «Смены») показывала 0,00 — исправлено (ошибка появилась в 1.17.73).
- **Оплата долга.** После частичного возврата долговой чек больше не пропадает из «Непогашенных»; касса, запущенная без интернета, сама подгружает данные компании после возврата связи и принимает оплату долга.
- **Ревизия.** Штрихкод упаковки больше не считается за 1 штуку.
- **Прокат.** Отмена проката привязана к своему чеку; при отмене кассир видит, что вернуть клиенту.
- **Смена.** Закрытая смена не «открывается» заново после быстрого закрытия на медленном сервере.
- **Быстрее запуск и поиск.** Каталог грузится параллельно со сменой, ожидание данных компании — 2 с, на медленном сервере окно открывается по сохранённой смене; поиск товара по номеру быстрее.
- **Склад.** «Списать просроченное» со списком и галочками; «Пересчёт дня» без нелепых счётчиков; «Прибыль» накладной с учётом расходов; плашка скидки и «Заканчивается» по минимальному остатку; колонка «Код / штрихкод» заполнена; возврат поставщику округляется до копейки.
- **Весы «Штрих РС-200».** Картинки товаров на весы (BMP), «Полная выгрузка с очисткой», вход в папку весов с логином web-scale1\kassa, разбор этикеток (префикс 27, Код-Вес, 5 знаков), сообщения «Штрихкод прочитан неверно» и «Весовой товар с PLU … не найден».
- **Программа владельца.** «Сегодня» считается по часам сервера, есть плашка «Показать неделю» и кнопка «?», «Клиенты» открываются быстрее; закрытые разделы освобождают память.
- **Просроченный товар.** Enter в окне «Товар просрочен» убирает товар.

---

# Кыргызча

### Эмнелер жаңы

- **Ээсинин программасындагы суммалар.** Шашылыш: «Сатуулар» жана «Финансы» таблицаларында «Сумма» мамычасы («Топ» жана «Смендер» бөлүмдөрүндөгү «Түшүм» да) 0,00 көрсөтүп жатты — оңдолду (ката 1.17.73 версиясында пайда болгон).
- **Карызды төлөө.** Жарым-жартылай кайтаруудан кийин карыз чеги «Төлөнө элек» тизмесинен жоголбойт; интернетсиз ишке киргизилген касса байланыш кайтканда компаниянын маалыматтарын өзү жүктөйт жана карыз төлөмүн кабыл алат.
- **Ревизия.** Таңгактын штрихкоду эми 1 даана деп саналбайт.
- **Прокат.** Прокатты жокко чыгаруу өз чегине байланган; жокко чыгарууда кассир кардарга эмнени кайтарууну көрөт.
- **Смена.** Жай серверде тез жабылгандан кийин жабылган смена кайра «ачылып» калбайт.
- **Ылдамыраак иштетүү жана издөө.** Каталог смена менен параллель жүктөлөт, компаниянын маалыматтарын күтүү — 2 с, жай серверде терезе сакталган смена боюнча ачылат; товарды номери боюнча издөө тезирээк.
- **Кампа.** «Мөөнөтү өткөндү эсептен чыгаруу» тизме жана белгилер менен; «Күндүн кайра эсеби» акылга сыйбаган эсептегичсиз; накладнойдун «Пайдасы» чыгымдарды эске алуу менен; арзандатуу плашкасы жана минималдуу калдык боюнча «Бүтүп баратат»; «Код / штрихкод» мамычасы толтурулган; жеткирүүчүгө кайтаруу тыйынга чейин тегеректелет.
- **«Штрих РС-200» таразасы.** Товар сүрөттөрү таразага (BMP), «Тазалоо менен толук чыгаруу», тараза папкасына web-scale1\kassa логини менен кирүү, этикеткаларды талдоо (27 префикси, Код-Салмак, 5 белги), «Штрихкод туура эмес окулду» жана «PLU … салмактуу товар табылган жок» билдирүүлөрү.
- **Ээсинин программасы.** «Бүгүн» сервердин сааты боюнча эсептелет, «Жумалыкты көрсөтүү» плашкасы жана «?» баскычы бар, «Кардарлар» тезирээк ачылат; жабылган бөлүмдөр эс тутумду бошотот.
- **Мөөнөтү өткөн товар.** «Товардун мөөнөтү бүткөн» терезесинде Enter товарды алып салат.

---

# English

### What's new

- **Totals in the owner program.** Urgent: in the “Sales” and “Finance” tables the “Amount” column (and “Revenue” in “Top” and “Shifts”) showed 0.00 — fixed (the bug appeared in 1.17.73).
- **Paying a debt.** After a partial return a debt receipt no longer disappears from “Unpaid”; a till started without internet loads the company data by itself once the connection returns and accepts a debt payment.
- **Stock count.** A pack barcode is no longer counted as 1 piece.
- **Rental.** Cancelling a rental is tied to its own receipt; when cancelling, the cashier sees what to give back to the customer.
- **Shift.** A closed shift no longer “reopens” after a quick close on a slow server.
- **Faster start and search.** The catalog loads in parallel with the shift, waiting for company data is 2 s, and on a slow server the window opens on the saved shift; searching a product by number is faster.
- **Warehouse.** “Write off expired” with a list and checkboxes; “Day recount” without absurd counters; an invoice's “Profit” that includes costs; a discount badge and “Running out” by the minimum stock; the “Code / barcode” column is filled in; a supplier return is rounded to the kopeck.
- **“Shtrikh RS-200” scales.** Product pictures on the scales (BMP), “Full upload with clearing”, signing in to the scales folder as web-scale1\kassa, label parsing (prefix 27, Code-Weight, 5 digits), the messages “Barcode read incorrectly” and “Weighed product with PLU … not found”.
- **Owner program.** “Today” is calculated by the server clock, there is a “Show the week” badge and a “?” button, “Clients” opens faster; closed sections free their memory.
- **Expired product.** Enter in the “Product is expired” window removes the product.

---

# Türkçe

### Yenilikler

- **Sahip programında tutarlar.** Acil: «Satışlar» ve «Finans» tablolarında «Tutar» sütunu («Top» ve «Vardiyalar»daki «Ciro» da) 0,00 gösteriyordu — düzeltildi (hata 1.17.73'te ortaya çıkmıştı).
- **Borç ödemesi.** Kısmi iadeden sonra borç fişi artık «Ödenmemiş» listesinden kaybolmaz; internetsiz başlatılan kasa bağlantı gelince şirket verilerini kendisi yükler ve borç ödemesini kabul eder.
- **Sayım.** Paket barkodu artık 1 adet sayılmaz.
- **Kiralama.** Kiralama iptali kendi fişine bağlıdır; iptalde kasiyer müşteriye neyin iade edileceğini görür.
- **Vardiya.** Yavaş sunucuda hızlı kapatılan vardiya artık yeniden «açılmaz».
- **Daha hızlı açılış ve arama.** Katalog vardiyayla paralel yüklenir, şirket verisi beklemesi 2 sn'dir, yavaş sunucuda pencere kayıtlı vardiyayla açılır; ürünü numarayla arama daha hızlıdır.
- **Depo.** Liste ve onay kutulu «Süresi geçenleri sil»; saçma sayaçsız «Günün sayımı»; masrafları içeren irsaliye «Kârı»; indirim şeridi ve asgari stoka göre «Bitiyor»; «Kod / barkod» sütunu dolu; tedarikçiye iade kuruşa yuvarlanır.
- **«Shtrih RS-200» terazisi.** Ürün resimleri teraziye (BMP), «Temizlemeli tam yükleme», terazi klasörüne web-scale1\kassa kullanıcısıyla giriş, etiket ayrıştırma (27 öneki, Kod-Ağırlık, 5 hane), «Barkod yanlış okundu» ve «PLU … tartılı ürün bulunamadı» mesajları.
- **Sahip programı.** «Bugün» sunucu saatine göre hesaplanır, «Haftayı göster» şeridi ve «?» düğmesi var, «Müşteriler» daha hızlı açılır; kapatılan bölümler belleği boşaltır.
- **Süresi geçmiş ürün.** «Ürünün tarihi geçmiş» penceresinde Enter ürünü kaldırır.

---

# O'zbekcha

### Yangiliklar

- **Egasi dasturidagi summalar.** Shoshilinch: «Sotuvlar» va «Moliya» jadvallarida «Summa» ustuni («Top» va «Smenalar»dagi «Tushum» ham) 0,00 ko'rsatayotgan edi — tuzatildi (xato 1.17.73 da paydo bo'lgan).
- **Qarzni to'lash.** Qisman qaytarishdan keyin qarz cheki «To'lanmagan» ro'yxatidan yo'qolmaydi; internetsiz ishga tushirilgan kassa aloqa qaytgach kompaniya ma'lumotlarini o'zi yuklaydi va qarz to'lovini qabul qiladi.
- **Reviziya.** O'ramning shtrix-kodi endi 1 dona deb hisoblanmaydi.
- **Ijara.** Ijarani bekor qilish o'z chekiga bog'langan; bekor qilishda kassir mijozga nima qaytarilishini ko'radi.
- **Smena.** Sekin serverda tez yopilgan smena qayta «ochilib» qolmaydi.
- **Tezroq ishga tushish va qidiruv.** Katalog smena bilan parallel yuklanadi, kompaniya ma'lumotini kutish — 2 s, sekin serverda oyna saqlangan smena bo'yicha ochiladi; tovarni raqami bo'yicha qidirish tezroq.
- **Ombor.** Ro'yxat va belgilar bilan «Muddati o'tganni hisobdan chiqarish»; bema'ni hisoblagichlarsiz «Kun qayta hisobi»; xarajatlarni hisobga olgan yuk xatining «Foydasi»; chegirma belgisi va minimal qoldiq bo'yicha «Tugayapti»; «Kod / shtrix-kod» ustuni to'ldirilgan; yetkazib beruvchiga qaytarish tiyingacha yaxlitlanadi.
- **«Shtrix RS-200» tarozisi.** Tovar rasmlari tarozida (BMP), «Tozalash bilan to'liq yuklash», taroziga papkaga web-scale1\kassa logini bilan kirish, yorliqlarni tahlil qilish (27 prefiks, Kod-Vazn, 5 raqam), «Shtrix-kod noto'g'ri o'qildi» va «PLU … tarozi tovari topilmadi» xabarlari.
- **Egasi dasturi.** «Bugun» server soati bo'yicha hisoblanadi, «Haftani ko'rsatish» belgisi va «?» tugmasi bor, «Mijozlar» tezroq ochiladi; yopilgan bo'limlar xotirani bo'shatadi.
- **Muddati o'tgan tovar.** «Tovarning muddati o'tgan» oynasida Enter tovarni olib tashlaydi.
