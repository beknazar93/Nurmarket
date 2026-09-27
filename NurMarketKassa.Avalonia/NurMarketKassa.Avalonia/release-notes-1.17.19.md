## v1.17.19

---

# Русский

### Новое

- **История чеков для кассира** (☰ → Работа → «История чеков»): чеки смены, за сегодня и вчера. Чек показан так же, как на бумаге. «Печать копии» печатает его с пометкой «ПОВТОРНАЯ ПЕЧАТЬ» и датой копии.
- **«⋮ Ещё» в кассе:** «Печать последнего чека» и «Списание» товара (испорчен, просрочен, взят себе).
- **«Отложить чек»:** чек остаётся вкладкой «Отложен ЧЧ:ММ», и сразу открывается новый чек. Вернуться к отложенному — нажать на его вкладку.
- **Клавиатура:** Enter — оплата, Num + — добавить выделенный товар, Num − — убавить или убрать строку, стрелки — по каталогу и по способам оплаты. В «Настройки → Клавиши» любую клавишу можно переназначить.
- **Работа без интернета по локальной сети:** кассы и программа владельца обмениваются продажами между собой, поэтому остатки и выручка видны сразу.
- **Программа владельца:** новый раздел «Аналитика». ABC-анализ теперь есть в «Складе», «Продажах» и «Финансах», полный ABC — в «Сводке».
- **Выгрузки в Excel и Word оформлены как отчёты:** шапка с магазином и периодом, оглавление, итоги формулами, диаграммы, шапка таблицы на каждой странице, печать на A4.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Масштаб на квадратных экранах | После обновления касса сама увеличивалась примерно до 160% и не помещалась на экран 1024×768 | Масштаб не больше, чем помещается на экран. Пересчитывается при смене монитора и масштаба Windows |
| Штрих-код на ценниках и этикетках | При печати через драйвер принтера ценник 40 мм выходил шириной около 81 мм, и половина штрих-кода обрезалась. На A4 один ценник занимал весь лист. Неверный EAN обрывал печать всей пачки, а к 12-значному коду дописывалась лишняя цифра | Размер точный, штрихи чёткие. Код печатается ровно таким, каким его прочитает сканер. Если код напечатать нельзя, программа предупреждает |
| Закрытие кассы крестиком | Крестик выходил из учётной записи: после закрытия кассы и программы владельца приходилось входить заново | Крестик = «Выйти на рабочий стол», вход сохраняется |
| «Отложить чек» | Чек пропадал, а отложенный не удалялся после оплаты | Чек остаётся вкладкой, после оплаты вкладка закрывается |
| Копия чека со скидкой | Скидка на строку считалась дважды: сумма 225,50, скидка −41,00 | Как в оригинале: 205,00 и −20,50 |
| Z-отчёт, плитка «Скидки» | Прочерк, если скидка была не бонусами | Сумма всех скидок по чекам смены |
| Отчёт смены в «Финансах» владельца | Открывался около 8 секунд, у старых смен «Долг: —» | Открывается за 0,8 секунды, долг ищется по всем чекам смены |
| Z-отчёт при закрытии смены | Окно ждало расчёт долга несколько секунд | Открывается сразу (около 1,5 с), долг подставляется, когда посчитается |
| Стрелки на клавиатуре | В каталоге выделялся товар, но рамка не двигалась; ↑/↓ в окне оплаты не работали | Рамка ходит по каталогу, ↑/↓ переключают способ оплаты |
| Заголовки разделов | «Маркетплейс» и разделы владельца показывали заголовок 2–3 раза, в «Финансах» было два бургер-меню | Один заголовок и одно меню |
| Выгрузки | Таблицы без оформления. В CSV перемещения количество «1.5» Excel превращал в дату. Подписи наполовину по-русски | Оформленные отчёты, дробь через запятую, подписи на языке программы |
| «Отчёт сохранён» | Показывался красной плашкой, как ошибка | Показывается зелёной плашкой |

### Проверено перед выпуском (тестовый аккаунт)

- 33 продажи подряд (наличные, перевод, два товара в чеке) прошли без ошибок, в среднем 3,3 секунды от «Оплатить» до нового чека.
- Наличные, перевод, смешанная оплата, скидка 10%, продажа в долг, оплата долга, возврат всего чека, внесение и изъятие: суммы совпадают с сервером.
- 2 продажи без интернета сохранились и сами ушли на сервер через 16 секунд после появления связи.
- 3 цикла «закрыть смену → Z-отчёт → открыть смену» подряд: на сервере ровно одна открытая смена.
- «Сводка» и «Финансы» владельца за день совпадают с продажами кассы до копейки.

---

# Кыргызча

### Жаңы

- **Кассир үчүн чектердин тарыхы** (☰ → Жумуш → «Чектердин тарыхы»): сменанын, бүгүнкү жана кечээки чектер. Чек кагаздагыдай көрсөтүлөт. «Көчүрмөсүн басып чыгаруу» аны «ПОВТОРНАЯ ПЕЧАТЬ» белгиси жана көчүрмөнүн күнү менен басат.
- **Кассадагы «⋮ Дагы»:** «Акыркы чекти басып чыгаруу» жана товарды «Эсептен чыгаруу» (бузулган, мөөнөтү өткөн, өзүнө алынган).
- **«Чекти калтыруу»:** чек «Калтырылган СС:ММ» өтмөгү болуп калат жана дароо жаңы чек ачылат. Калтырылган чекке кайтуу үчүн анын өтмөгүн басыңыз.
- **Баскычтоп:** Enter — төлөм, Num + — белгиленген товарды кошуу, Num − — санын азайтуу же сапты алып салуу, жебелер — каталог жана төлөм ыкмалары боюнча. «Жөндөөлөр → Баскычтар» бөлүмүндө каалаган баскычты кайра дайындаса болот.
- **Жергиликтүү тармак аркылуу интернетсиз иштөө:** кассалар жана ээсинин программасы сатууларды өз ара алмашат, ошондуктан калдыктар жана түшкөн акча дароо көрүнөт.
- **Ээсинин программасы:** жаңы «Талдоо» бөлүмү. ABC-талдоо эми «Кампа», «Сатуулар» жана «Каржы» бөлүмдөрүндө бар, толук ABC — «Жыйынтыкта».
- **Excel жана Word'го жүктөө отчёт катары жасалды:** дүкөн жана мезгил жазылган баш сап, мазмуну, формула менен жыйынтыктар, диаграммалар, ар бир бетте таблицанын баш сабы, A4 басып чыгаруу.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Чарчы экрандардагы масштаб | Жаңыртуудан кийин касса өзүнөн-өзү болжол менен 160%га чоңоюп, 1024×768 экранга батчу эмес | Масштаб экранга баткандан чоң болбойт. Монитор же Windows масштабы өзгөргөндө кайра эсептелет |
| Баа белгилериндеги жана этикеткалардагы штрих-код | Принтердин драйвери аркылуу басканда 40 мм баа белгиси болжол менен 81 мм чыгып, штрих-коддун жарымы кесилчү. A4'тө бир баа белгиси бүт баракты ээлечү. Туура эмес EAN бүт топтомду токтотчу, 12 орундуу кодго ашыкча сан кошулчу | Өлчөмү так, сызыктары ачык. Код сканер окуй тургандай так басылат. Кодду басууга мүмкүн болбосо, программа эскертет |
| Кассаны айкаш белги менен жабуу | Айкаш белги каттоо эсебинен чыгарчу: кассаны жана ээсинин программасын жапкандан кийин кайра кирүү керек болчу | Айкаш белги = «Иш столуна чыгуу», кирүү сакталат |
| «Чекти калтыруу» | Чек жоголуп кетчү, калтырылган чек төлөгөндөн кийин өччү эмес | Чек өтмөк болуп калат, төлөгөндөн кийин өтмөк жабылат |
| Арзандатуусу бар чектин көчүрмөсү | Саптагы арзандатуу эки жолу эсептелчү: сумма 225,50, арзандатуу −41,00 | Түпнускадагыдай: 205,00 жана −20,50 |
| Z-отчёт, «Арзандатуулар» плиткасы | Арзандатуу бонус менен болбосо, сызыкча турчу | Сменанын чектериндеги бардык арзандатуулардын суммасы |
| Ээсинин «Каржы» бөлүмүндөгү сменанын отчёту | Болжол менен 8 секундда ачылчу, эски сменаларда «Карыз: —» | 0,8 секундда ачылат, карыз сменанын бардык чектеринен изделет |
| Сменаны жапкандагы Z-отчёт | Терезе бир нече секунд карыздын эсебин күтчү | Дароо ачылат (болжол менен 1,5 с), карыз эсептелгенде коюлат |
| Баскычтоптогу жебелер | Каталогдо товар белгиленчү, бирок алкак жылчу эмес; төлөм терезесинде ↑/↓ иштечү эмес | Алкак каталог боюнча жылат, ↑/↓ төлөм ыкмасын алмаштырат |
| Бөлүмдөрдүн аталыштары | «Маркетплейс» жана ээсинин бөлүмдөрү аталышты 2–3 жолу көрсөтчү, «Каржыда» эки бургер-меню бар эле | Бир аталыш жана бир меню |
| Жүктөөлөр | Таблицалар жасалгасыз. Которуунун CSV файлында «1.5» санын Excel датага айлантчу. Жазуулар жарым-жартылай орусча | Жасалгаланган отчёттор, бөлчөк үтүр менен, жазуулар программанын тилинде |
| «Отчёт сакталды» | Ката сыяктуу кызыл тилкеде чыкчу | Жашыл тилкеде чыгат |

### Чыгаруудан мурун текшерилди (тесттик аккаунт)

- Катары менен 33 сатуу (накталай, которуу, чекте эки товар) катасыз өттү, «Төлөө» баскычынан жаңы чекке чейин орточо 3,3 секунд.
- Накталай, которуу, аралаш төлөм, 10% арзандатуу, карызга сатуу, карызды төлөө, бүт чекти кайтаруу, салуу жана алуу: суммалар сервер менен дал келет.
- Интернетсиз 2 сатуу сакталып, байланыш пайда болгондон 16 секунддан кийин серверге өзү кетти.
- Катары менен 3 жолу «сменаны жабуу → Z-отчёт → сменаны ачуу»: серверде бир гана ачык смена.
- Ээсинин «Жыйынтыгы» жана «Каржысы» күн боюнча кассанын сатуулары менен тыйынына чейин дал келет.

---

# English

### New

- **Receipt history for cashiers** (☰ → Work → “Receipt history”): receipts of the shift, today and yesterday. The receipt is shown exactly as on paper. “Print copy” prints it marked “REPRINT” with the date of the copy.
- **“⋮ More” in the till:** “Print last receipt” and product “Write-off” (damaged, expired, taken for own use).
- **“Hold receipt”:** the receipt stays as a “Held HH:MM” tab and a new receipt opens right away. Click the tab to return to the held receipt.
- **Keyboard:** Enter — pay, Num + — add the highlighted product, Num − — decrease or remove the line, arrow keys — move through the catalog and the payment methods. Any key can be reassigned in “Settings → Keys”.
- **Working without internet over the local network:** tills and the owner program exchange sales with each other, so stock and revenue are visible right away.
- **Owner program:** a new “Analytics” section. ABC analysis is now in “Warehouse”, “Sales” and “Finance”, and the full ABC is in the “Overview”.
- **Excel and Word exports are real reports:** a header with the shop and period, a table of contents, totals as formulas, charts, the table header repeated on every page, A4 printing.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Scale on square screens | After an update the till enlarged itself to about 160% and did not fit a 1024×768 screen | The scale is never larger than what fits the screen. It is recalculated when the monitor or Windows scaling changes |
| Barcode on price tags and labels | When printing through a printer driver, a 40 mm price tag came out about 81 mm wide and half of the barcode was cut off. On A4 one price tag filled the whole sheet. An invalid EAN stopped the whole batch, and an extra digit was added to 12-digit codes | The size is exact and the bars are sharp. The code is printed exactly as the scanner will read it. If a code cannot be printed, the program warns you |
| Closing the till with the close button | The close button signed you out: after closing the till and the owner program you had to sign in again | The close button = “Exit to desktop”, you stay signed in |
| “Hold receipt” | The receipt disappeared, and a held receipt was not removed after payment | The receipt stays as a tab, and the tab closes after payment |
| Copy of a receipt with a discount | The line discount was counted twice: total 225.50, discount −41.00 | Same as the original: 205.00 and −20.50 |
| Z-report, “Discounts” tile | A dash if the discount was not paid with points | The sum of all discounts on the shift's receipts |
| Shift report in the owner's “Finance” | Took about 8 seconds to open; old shifts showed “Debt: —” | Opens in 0.8 seconds; the debt is searched across all receipts of the shift |
| Z-report when closing a shift | The window waited several seconds for the debt calculation | Opens right away (about 1.5 s); the debt is filled in when ready |
| Arrow keys | In the catalog a product was highlighted but the frame did not move; ↑/↓ did not work in the payment window | The frame moves through the catalog; ↑/↓ switch the payment method |
| Section titles | “Marketplace” and owner sections showed the title 2–3 times; “Finance” had two burger menus | One title and one menu |
| Exports | Plain tables. In the transfer CSV, Excel turned the quantity “1.5” into a date. Labels were half in Russian | Formatted reports, decimals with a comma, labels in the program's language |
| “Report saved” | Shown in a red bar like an error | Shown in a green bar |

### Checked before release (test account)

- 33 sales in a row (cash, transfer, two products per receipt) went through without errors, 3.3 seconds on average from “Pay” to a new receipt.
- Cash, transfer, mixed payment, 10% discount, sale on credit, debt payment, full receipt return, cash in and cash out: the amounts match the server.
- 2 sales made without internet were saved and sent to the server by themselves 16 seconds after the connection came back.
- 3 cycles of “close shift → Z-report → open shift” in a row: exactly one open shift on the server.
- The owner's “Overview” and “Finance” for the day match the till's sales to the cent.

---

# Türkçe

### Yenilikler

- **Kasiyer için fiş geçmişi** (☰ → İş → «Fiş geçmişi»): vardiyanın, bugünün ve dünün fişleri. Fiş kâğıttaki gibi gösterilir. «Kopya yazdır» fişi «ПОВТОРНАЯ ПЕЧАТЬ» işareti ve kopyanın tarihiyle yazdırır.
- **Kasada «⋮ Daha fazla»:** «Son fişi yazdır» ve ürün «Düşümü» (bozuk, süresi geçmiş, kendi kullanımı için alınmış).
- **«Fişi beklet»:** fiş «Bekleyen SS:DD» sekmesi olarak kalır ve hemen yeni fiş açılır. Bekleyen fişe dönmek için sekmesine tıklayın.
- **Klavye:** Enter — ödeme, Num + — seçili ürünü ekle, Num − — miktarı azalt veya satırı kaldır, ok tuşları — katalogda ve ödeme yöntemlerinde gezinme. «Ayarlar → Tuşlar» bölümünde her tuş yeniden atanabilir.
- **Yerel ağ üzerinden internetsiz çalışma:** kasalar ve sahip programı satışları birbiriyle paylaşır, bu yüzden stok ve ciro hemen görünür.
- **Sahip programı:** yeni «Analiz» bölümü. ABC analizi artık «Depo», «Satışlar» ve «Finans» bölümlerinde, tam ABC ise «Özet»te.
- **Excel ve Word dışa aktarımları gerçek raporlar:** mağaza ve dönemin yazdığı başlık, içindekiler, formüllü toplamlar, grafikler, her sayfada tablo başlığı, A4 yazdırma.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Kare ekranlarda ölçek | Güncellemeden sonra kasa kendiliğinden yaklaşık %160'a büyüyor ve 1024×768 ekrana sığmıyordu | Ölçek ekrana sığandan büyük olmaz. Monitör veya Windows ölçeği değişince yeniden hesaplanır |
| Etiketlerdeki barkod | Yazıcı sürücüsüyle yazdırırken 40 mm etiket yaklaşık 81 mm genişliğinde çıkıyor, barkodun yarısı kesiliyordu. A4'te bir etiket tüm sayfayı kaplıyordu. Hatalı EAN tüm partiyi durduruyor, 12 haneli koda fazladan rakam ekleniyordu | Boyut tam, çizgiler net. Kod, tarayıcının okuyacağı şekilde birebir yazdırılır. Kod yazdırılamıyorsa program uyarır |
| Kasayı kapatma düğmesiyle kapatma | Kapatma düğmesi oturumu kapatıyordu: kasa ve sahip programı kapatıldıktan sonra yeniden giriş gerekiyordu | Kapatma düğmesi = «Masaüstüne çık», oturum açık kalır |
| «Fişi beklet» | Fiş kayboluyordu, bekleyen fiş ödemeden sonra silinmiyordu | Fiş sekme olarak kalır, ödemeden sonra sekme kapanır |
| İndirimli fişin kopyası | Satır indirimi iki kez sayılıyordu: toplam 225,50, indirim −41,00 | Orijinaldeki gibi: 205,00 ve −20,50 |
| Z raporu, «İndirimler» kutusu | İndirim puanla yapılmadıysa çizgi görünüyordu | Vardiya fişlerindeki tüm indirimlerin toplamı |
| Sahibin «Finans» bölümünde vardiya raporu | Yaklaşık 8 saniyede açılıyordu, eski vardiyalarda «Borç: —» | 0,8 saniyede açılır, borç vardiyanın tüm fişlerinde aranır |
| Vardiya kapatılırken Z raporu | Pencere borç hesabını birkaç saniye bekliyordu | Hemen açılır (yaklaşık 1,5 sn), borç hazır olunca eklenir |
| Ok tuşları | Katalogda ürün seçiliyordu ama çerçeve hareket etmiyordu; ödeme penceresinde ↑/↓ çalışmıyordu | Çerçeve katalogda gezinir, ↑/↓ ödeme yöntemini değiştirir |
| Bölüm başlıkları | «Pazaryeri» ve sahip bölümleri başlığı 2–3 kez gösteriyordu, «Finans»ta iki menü vardı | Tek başlık ve tek menü |
| Dışa aktarımlar | Biçimsiz tablolar. Transfer CSV'sinde Excel «1.5» miktarını tarihe çeviriyordu. Etiketlerin yarısı Rusçaydı | Biçimli raporlar, ondalık virgülle, etiketler program dilinde |
| «Rapor kaydedildi» | Hata gibi kırmızı şeritte gösteriliyordu | Yeşil şeritte gösterilir |

### Yayından önce kontrol edildi (test hesabı)

- Arka arkaya 33 satış (nakit, havale, fişte iki ürün) hatasız geçti, «Öde»den yeni fişe ortalama 3,3 saniye.
- Nakit, havale, karışık ödeme, %10 indirim, veresiye satış, borç ödemesi, tüm fişin iadesi, para girişi ve çıkışı: tutarlar sunucuyla aynı.
- İnternetsiz yapılan 2 satış kaydedildi ve bağlantı gelince 16 saniye içinde sunucuya kendiliğinden gönderildi.
- Arka arkaya 3 kez «vardiyayı kapat → Z raporu → vardiyayı aç»: sunucuda tam olarak bir açık vardiya.
- Sahibin günlük «Özet» ve «Finans» verileri kasanın satışlarıyla kuruşu kuruşuna aynı.

---

# O'zbekcha

### Yangiliklar

- **Kassir uchun cheklar tarixi** (☰ → Ish → «Cheklar tarixi»): smenadagi, bugungi va kechagi cheklar. Chek qog'ozdagidek ko'rsatiladi. «Nusxani chop etish» uni «ПОВТОРНАЯ ПЕЧАТЬ» belgisi va nusxa sanasi bilan chop etadi.
- **Kassadagi «⋮ Yana»:** «Oxirgi chekni chop etish» va mahsulotni «Hisobdan chiqarish» (buzilgan, muddati o'tgan, o'zi uchun olingan).
- **«Chekni kutishga qo'yish»:** chek «Kutishda SS:DD» yorlig'i bo'lib qoladi va darhol yangi chek ochiladi. Kutishdagi chekka qaytish uchun uning yorlig'ini bosing.
- **Klaviatura:** Enter — to'lov, Num + — belgilangan mahsulotni qo'shish, Num − — miqdorni kamaytirish yoki qatorni olib tashlash, strelkalar — katalog va to'lov usullari bo'ylab. «Sozlamalar → Tugmalar» bo'limida istalgan tugmani qayta tayinlash mumkin.
- **Mahalliy tarmoq orqali internetsiz ishlash:** kassalar va ega dasturi sotuvlarni o'zaro almashadi, shuning uchun qoldiq va tushum darhol ko'rinadi.
- **Ega dasturi:** yangi «Tahlil» bo'limi. ABC tahlili endi «Ombor», «Sotuvlar» va «Moliya» bo'limlarida, to'liq ABC esa «Umumiy»da.
- **Excel va Word'ga eksport haqiqiy hisobotlar:** do'kon va davr yozilgan sarlavha, mundarija, formulali jamlar, diagrammalar, har sahifada jadval sarlavhasi, A4 chop etish.

### Tuzatildi: avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Kvadrat ekranlarda masshtab | Yangilanishdan keyin kassa o'z-o'zidan taxminan 160% gacha kattalashib, 1024×768 ekranga sig'mas edi | Masshtab ekranga sig'adigandan katta bo'lmaydi. Monitor yoki Windows masshtabi o'zgarganda qayta hisoblanadi |
| Narx yorliqlari va etiketkalardagi shtrix-kod | Printer drayveri orqali chop etilganda 40 mm yorliq taxminan 81 mm eni bilan chiqib, shtrix-kodning yarmi kesilar edi. A4 da bitta yorliq butun varaqni egallardi. Noto'g'ri EAN butun to'plamni to'xtatardi, 12 xonali kodga ortiqcha raqam qo'shilardi | O'lcham aniq, chiziqlar tiniq. Kod skaner o'qiydigandek aynan chop etiladi. Kodni chop etib bo'lmasa, dastur ogohlantiradi |
| Kassani yopish tugmasi bilan yopish | Yopish tugmasi hisobdan chiqarardi: kassa va ega dasturi yopilgandan keyin qayta kirish kerak edi | Yopish tugmasi = «Ish stoliga chiqish», kirish saqlanadi |
| «Chekni kutishga qo'yish» | Chek yo'qolib qolardi, kutishdagi chek to'lovdan keyin o'chmas edi | Chek yorliq bo'lib qoladi, to'lovdan keyin yorliq yopiladi |
| Chegirmali chek nusxasi | Qatordagi chegirma ikki marta hisoblanardi: summa 225,50, chegirma −41,00 | Asl nusxadagidek: 205,00 va −20,50 |
| Z-hisobot, «Chegirmalar» plitkasi | Chegirma bonus bilan bo'lmasa, chiziqcha turardi | Smena cheklaridagi barcha chegirmalar yig'indisi |
| Egadagi «Moliya» bo'limida smena hisoboti | Taxminan 8 soniyada ochilardi, eski smenalarda «Qarz: —» | 0,8 soniyada ochiladi, qarz smenaning barcha cheklaridan qidiriladi |
| Smena yopilayotgandagi Z-hisobot | Oyna qarz hisobini bir necha soniya kutardi | Darhol ochiladi (taxminan 1,5 s), qarz tayyor bo'lganda qo'yiladi |
| Klaviatura strelkalari | Katalogda mahsulot belgilanardi, lekin ramka siljimasdi; to'lov oynasida ↑/↓ ishlamasdi | Ramka katalog bo'ylab yuradi, ↑/↓ to'lov usulini almashtiradi |
| Bo'lim sarlavhalari | «Marketpleys» va ega bo'limlari sarlavhani 2–3 marta ko'rsatardi, «Moliya»da ikkita menyu bor edi | Bitta sarlavha va bitta menyu |
| Eksportlar | Bezaksiz jadvallar. Ko'chirish CSV faylida Excel «1.5» miqdorini sanaga aylantirardi. Yozuvlar yarim ruscha edi | Bezatilgan hisobotlar, kasr vergul bilan, yozuvlar dastur tilida |
| «Hisobot saqlandi» | Xato kabi qizil tasmada chiqardi | Yashil tasmada chiqadi |

### Chiqarishdan oldin tekshirildi (test hisobi)

- Ketma-ket 33 ta sotuv (naqd, o'tkazma, chekda ikkita mahsulot) xatosiz o'tdi, «To'lash»dan yangi chekkacha o'rtacha 3,3 soniya.
- Naqd, o'tkazma, aralash to'lov, 10% chegirma, qarzga sotuv, qarzni to'lash, butun chekni qaytarish, pul kiritish va olish: summalar server bilan mos.
- Internetsiz qilingan 2 ta sotuv saqlandi va aloqa tiklangandan 16 soniya o'tib serverga o'zi yuborildi.
- Ketma-ket 3 marta «smenani yopish → Z-hisobot → smenani ochish»: serverda aynan bitta ochiq smena.
- Egadagi kunlik «Umumiy» va «Moliya» kassa sotuvlari bilan tiyinigacha mos keladi.
