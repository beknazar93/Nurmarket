## v1.17.73

Смена и остаток в ящике, остаток товара после продажи, округление чека как на сервере, возврат, накладная, печать этикеток, сообщения об ошибках и доступность. Всё из 1.17.72 сохраняется.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.73.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.73.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.73.zip` (Intel); Android — `nurmarket-kassa-android-1.17.73.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Что нового

- **Файлы и антивирус.** Файлы с деньгами и настройками не затираются, если их на миг заблокировал антивирус.
- **Обмен и возврат.** Чек обмена показывает ту же сумму, что выдана клиенту; возврат сверяется с сервером по ключу повтора, повтор после обрыва связи не создаёт второй возврат.
- **Бонусные баллы.** Если сервер отказал, это видно в «Некорректных чеках», а не теряется молча.
- **Округление весовых.** В чеке есть строка «Округление весовых» — видно, на сколько округлена сумма весовых товаров.
- **Экранный диктор.** Видит окна «Возврат», «Скидка» и «Взвесить».
- **Excel в разделах владельца.** Долги, Клиенты, Зарплата, Прибыль и деньги, Убыток, Прокат, Табель, Заказы с сайта.
- **Выход из программы.** При несохранённых данных задаётся вопрос.
- **Нагрузка.** Меньше нагрузки на сервер и памяти: каталог перечитывается раз в 8 часов, журнал стал читаемым.
- **Архив товара.** Окно «Архив» с возвратом товара из архива; закупочная цена в карточке товара принимается до 4 знаков.
- **Перемещения.** Без дублей; после обрыва связи есть «Я проверил — отправить заново».
- **Открытие смены.** «Остаток по системе» берётся из последней закрытой смены; если введён ноль, касса просит подтвердить; пока остаток загружается, виден индикатор.
- **Закрытие смены и «Касса:».** Шапка «Касса:» обновляется сразу после закрытия смены и после возврата. Закрытая смена из очереди закрытия заново не подхватывается, смену можно закрыть «с пометкой».
- **Остаток товара.** После продажи остаток не затирается: весовой товар остаётся в «кг», правка карточки, размеров и импорт не стирают остаток, повтор «± Остаток» считает от текущего.
- **Округление чека ровно как на сервере.** Весовые товары, скидка чека и признак «весовой» из карточки.
- **Возврат.** Выдача считается по ответу сервера, «К выдаче (ожидаемо)» показывается до проведения; бонусные баллы возвращаются с ключом повтора и не задваиваются.
- **Накладная и склад.** Правка всех полей идёт через сервер, закупочная цена принимается до 4 знаков; перемещения без дублей и с датой; товар можно убрать в архив.
- **Печать.** Копии этикеток печатаются частями с подсказкой «Напечатано N из M»; принтер этикеток можно сбросить; права «Печать штрих-кодов» и «Изменение цены» действуют отдельно.
- **Программа владельца.** Меньше памяти, обновления на месте; «Заказы с сайта» с фильтром «Откуда».
- **Сообщения.** Ошибки и важные сообщения больше не исчезают сами — они остаются в окне; успех показывается короткой плашкой.
- **Доступность.** Контрастнее рамки, кнопки крупнее, масштаб 0,9 на низких экранах.
- **Редактор сайта.** Telegram-кнопка и «Помощник магазина».

---

# Кыргызча

### Эмнелер жаңы

- **Файлдар жана антивирус.** Акча жана жөндөөлөр файлдары антивирус аларды көз ирмемге бөгөттөсө да өчүрүлбөйт.
- **Алмаштыруу жана кайтаруу.** Алмаштыруу чеги кардарга берилген сумманы так көрсөтөт; кайтаруу сервер менен кайталоо ачкычы боюнча салыштырылат, байланыш үзүлгөндөн кийинки кайталоо экинчи кайтарууну түзбөйт.
- **Бонус упайлары.** Сервер четке какса, бул «Туура эмес чектерде» көрүнөт, унчукпай жоголбойт.
- **Салмактуулардын тегизделиши.** Чекте «Салмактуу товарлардын тегизделиши» сабы бар — салмактуу товарлардын суммасы канчага тегизделгени көрүнөт.
- **Экран окугуч.** «Кайтаруу», «Арзандатуу» жана «Таразалоо» терезелерин көрөт.
- **Ээсинин бөлүмдөрүндө Excel.** Карыздар, Кардарлар, Айлык, Пайда жана акча, Зыян, Прокат, Табель, Сайттан заказдар.
- **Программадан чыгуу.** Сакталбаган маалымат болсо, суроо берилет.
- **Жүк.** Серверге жана эстутумга жүк азайды: каталог 8 саатта бир жолу кайра окулат, журнал түшүнүктүү окулат.
- **Товар архиви.** «Архив» терезеси жана товарды архивден кайтаруу; товар карточкасында сатып алуу баасы 4 цифрага чейин кабыл алынат.
- **Жылдыруулар.** Кайталанбайт; байланыш үзүлгөндөн кийин «Текшердим — кайра жөнөтүү» бар.
- **Сменаны ачуу.** «Система боюнча калдык» акыркы жабылган сменадан алынат; нөл киргизилсе, касса ырастоону сурайт; калдык жүктөлүп жатканда жүктөө көрсөткүчү көрүнөт.
- **Сменаны жабуу жана «Касса:».** «Касса:» баш жагы сменаны жапкандан кийин жана кайтаруудан кийин дароо жаңырат. Жабуу кезегиндеги жабык смена кайра алынбайт, сменаны «белги менен» жабууга болот.
- **Товардын калдыгы.** Сатуудан кийин калдык өчүрүлбөйт: салмактуу товар «кг» бойдон калат, карточканы жана өлчөмдөрдү оңдоо менен импорт калдыкты өчүрбөйт, «± Калдык» кайталоо учурдагыдан эсептейт.
- **Чектин тегиздөөсү так сервердегидей.** Салмактуу товарлар, чектин арзандатуусу жана карточкадагы «салмактуу» белгиси.
- **Кайтаруу.** Берүү сервердин жообу боюнча эсептелет, «Берүүгө (күтүлүүдө)» өткөрүүгө чейин көрсөтүлөт; бонус упайлары кайталоо ачкычы менен кайтарылат жана эки эселенбейт.
- **Накладной жана кампа.** Бардык талааларды оңдоо сервер аркылуу өтөт, сатып алуу баасы 4 цифрага чейин кабыл алынат; жылдыруулар кайталанбайт жана датасы менен; товарды архивге салууга болот.
- **Басып чыгаруу.** Энбелгилердин көчүрмөлөрү бөлүк-бөлүк басылып, «M ичинен N басылды» деген кеңеш көрүнөт; энбелги принтерин кайра баштапкы абалга келтирүүгө болот; «Штрих-код басып чыгаруу» жана «Бааны өзгөртүү» укуктары өзүнчө иштейт.
- **Ээсинин программасы.** Эстутум аз жана жаңыртуулар ордунда; «Сайттан заказдар» «Кайдан» чыпкасы менен.
- **Билдирүүлөр.** Каталар жана маанилүү билдирүүлөр мындан ары өзүнөн өзү жоголбойт — алар терезеде калат; ийгилик кыска плашка менен көрсөтүлөт.
- **Жеткиликтүүлүк.** Алкактар контрасттуураак, баскычтар чоңураак, бийиктиги аз экрандарда масштаб 0,9.
- **Сайт редактору.** Telegram баскычы жана «Дүкөн жардамчысы».

---

# English

### What's new

- **Files and antivirus.** Files with money and settings are not overwritten when an antivirus blocks them for a moment.
- **Exchange and return.** The exchange receipt shows the same amount that was paid out to the customer; a return is checked against the server by a retry key, and a repeat after a dropped connection does not create a second return.
- **Bonus points.** If the server refuses, you can see it in “Irregular receipts” instead of losing it silently.
- **Weighed items rounding.** The receipt has a “Weighed items rounding” line — you can see by how much the weighed goods amount was rounded.
- **Screen reader.** It sees the “Return”, “Discount” and “Weigh” windows.
- **Excel in the owner sections.** Debts, Clients, Salary, Profit and money, Loss, Rental, Timesheet, Website orders.
- **Leaving the program.** With unsaved data it now asks a question.
- **Load.** Less load on the server and memory: the catalog is re-read every 8 hours, and the log became readable.
- **Product archive.** An “Archive” window with restoring a product from the archive; the purchase price in the product card is accepted with up to 4 decimals.
- **Transfers.** Without duplicates; after a dropped connection there is “I checked — send again”.
- **Opening a shift.** The “System balance” is taken from the last closed shift; if you enter zero, the till asks to confirm; a loading indicator shows while the balance loads.
- **Closing a shift and “Cash:”.** The “Cash:” header updates at once after closing a shift and after a return. A closed shift from the closing queue is not picked up again, and a shift can be closed “with a note”.
- **Product stock.** After a sale the stock is no longer overwritten: a weighed product stays in “kg”, editing the card or sizes and an import do not erase stock, and a repeated “± Stock” counts from the current amount.
- **Receipt rounding exactly as on the server.** Weighed goods, the receipt discount and the “weighed” flag from the card.
- **Returns.** The payout is calculated from the server's answer, “To pay out (expected)” is shown before posting; bonus points are returned with a retry key and are not doubled.
- **Invoice and warehouse.** Editing all fields goes through the server and the purchase price is accepted with up to 4 decimals; transfers without duplicates and with a date; a product can be moved to the archive.
- **Printing.** Label copies are printed in parts with a hint “Printed N of M”; the label printer can be reset; the “Barcode printing” and “Price change” permissions work separately.
- **Owner program.** Less memory and in-place updates; “Website orders” with a “From” filter.
- **Messages.** Errors and important messages no longer disappear on their own — they stay in a window; success is shown as a short banner.
- **Accessibility.** Higher-contrast borders, bigger buttons, 0.9 scale on short screens.
- **Site editor.** A Telegram button and the “Store assistant”.

---

# Türkçe

### Yenilikler

- **Dosyalar ve antivirüs.** Para ve ayar dosyaları, antivirüs bir an engellediğinde bile ezilmez.
- **Değişim ve iade.** Değişim fişi, müşteriye ödenen tutarın aynısını gösterir; iade sunucuyla tekrar anahtarıyla eşleştirilir, bağlantı koptuktan sonraki tekrar ikinci bir iade oluşturmaz.
- **Bonus puanlar.** Sunucu reddederse bu «Sorunlu fişler»de görünür, sessizce kaybolmaz.
- **Tartılı ürün yuvarlaması.** Fişte «Tartılı ürün yuvarlaması» satırı var — tartılı ürünler tutarının ne kadar yuvarlandığı görünür.
- **Ekran okuyucu.** «İade», «İndirim» ve «Tart» pencerelerini görür.
- **Sahip bölümlerinde Excel.** Borçlar, Müşteriler, Maaş, Kâr ve para, Zarar, Kiralama, Puantaj, Web sitesi siparişleri.
- **Programdan çıkış.** Kaydedilmemiş veri varsa soru sorulur.
- **Yük.** Sunucuya ve belleğe daha az yük: katalog 8 saatte bir yeniden okunur, günlük okunaklı hale geldi.
- **Ürün arşivi.** Ürünü arşivden geri alma özellikli «Arşiv» penceresi; ürün kartında alış fiyatı 4 basamağa kadar kabul edilir.
- **Transferler.** Yinelenmeden; bağlantı koptuktan sonra «Kontrol ettim — yeniden gönder» var.
- **Vardiya açma.** «Sistem bakiyesi» son kapatılan vardiyadan alınır; sıfır girilirse kasa onay ister; bakiye yüklenirken yükleme göstergesi görünür.
- **Vardiya kapatma ve «Kasa:».** «Kasa:» başlığı vardiya kapatıldıktan ve iadeden hemen sonra güncellenir. Kapatma kuyruğundaki kapalı vardiya yeniden alınmaz, vardiya «notla» kapatılabilir.
- **Ürün stoku.** Satıştan sonra stok ezilmez: tartılı ürün «kg» olarak kalır, kartı veya bedenleri düzenlemek ve içe aktarma stoku silmez, «± Stok» tekrarı mevcut miktardan hesaplar.
- **Fiş yuvarlaması tam sunucudaki gibi.** Tartılı ürünler, fiş indirimi ve karttaki «tartılı» işareti.
- **İade.** Ödenecek tutar sunucu yanıtına göre hesaplanır, «Verilecek (beklenen)» işlemeden önce gösterilir; bonus puanlar tekrar anahtarıyla iade edilir ve çiftlenmez.
- **İrsaliye ve depo.** Tüm alanların düzenlenmesi sunucu üzerinden geçer, alış fiyatı 4 basamağa kadar kabul edilir; transferler yinelenmeden ve tarihle; ürün arşive alınabilir.
- **Yazdırma.** Etiket kopyaları parça parça basılır ve «M içinden N basıldı» ipucu görünür; etiket yazıcısı sıfırlanabilir; «Barkod yazdırma» ve «Fiyat değiştirme» yetkileri ayrı çalışır.
- **Sahip programı.** Daha az bellek ve yerinde güncelleme; «Web sitesi siparişleri» «Nereden» filtresiyle.
- **Mesajlar.** Hatalar ve önemli mesajlar artık kendiliğinden kaybolmaz — pencerede kalır; başarı kısa bir şeritle gösterilir.
- **Erişilebilirlik.** Daha kontrastlı çerçeveler, daha büyük düğmeler, kısa ekranlarda 0,9 ölçek.
- **Site düzenleyici.** Telegram düğmesi ve «Mağaza yardımcısı».

---

# O'zbekcha

### Yangiliklar

- **Fayllar va antivirus.** Pul va sozlamalar fayllari antivirus bir lahza bloklaganda ham o'chib ketmaydi.
- **Almashtirish va qaytarish.** Almashtirish cheki mijozga berilgan summani aynan ko'rsatadi; qaytarish server bilan takrorlash kaliti bo'yicha solishtiriladi, aloqa uzilgandan keyingi takror ikkinchi qaytarishni yaratmaydi.
- **Bonus ballar.** Server rad etsa, bu «Noto'g'ri cheklar»da ko'rinadi, jimgina yo'qolmaydi.
- **Vaznlilarni yaxlitlash.** Chekda «Tortiladigan tovarlar yaxlitlanishi» qatori bor — vaznli tovarlar summasi qancha yaxlitlangani ko'rinadi.
- **Ekran o'quvchi.** «Qaytarish», «Chegirma» va «Tortish» oynalarini ko'radi.
- **Egasi bo'limlarida Excel.** Qarzlar, Mijozlar, Ish haqi, Foyda va pul, Zarar, Ijara, Tabel, Saytdan buyurtmalar.
- **Dasturdan chiqish.** Saqlanmagan ma'lumot bo'lsa, savol beriladi.
- **Yuk.** Server va xotiraga yuk kamaydi: katalog 8 soatda bir marta qayta o'qiladi, jurnal tushunarli bo'ldi.
- **Mahsulot arxivi.** Mahsulotni arxivdan qaytarish imkoniyati bilan «Arxiv» oynasi; mahsulot kartasida xarid narxi 4 xonagacha qabul qilinadi.
- **Ko'chirishlar.** Takrorlanmaydi; aloqa uzilgandan keyin «Tekshirdim — qayta yuborish» bor.
- **Smenani ochish.** «Tizim bo'yicha qoldiq» oxirgi yopilgan smenadan olinadi; nol kiritilsa, kassa tasdiqlashni so'raydi; qoldiq yuklanayotganda yuklash ko'rsatkichi ko'rinadi.
- **Smenani yopish va «Kassa:».** «Kassa:» sarlavhasi smena yopilgandan va qaytarishdan keyin darhol yangilanadi. Yopish navbatidagi yopiq smena qayta olinmaydi, smenani «izoh bilan» yopish mumkin.
- **Mahsulot qoldig'i.** Sotuvdan keyin qoldiq o'chmaydi: vaznli mahsulot «kg» bo'lib qoladi, kartani yoki o'lchamlarni tahrirlash va import qoldiqni o'chirmaydi, «± Qoldiq» takrori joriy miqdordan hisoblaydi.
- **Chekni yaxlitlash aynan serverdagidek.** Vaznli mahsulotlar, chek chegirmasi va kartadagi «vaznli» belgisi.
- **Qaytarish.** Beriladigan summa server javobi bo'yicha hisoblanadi, «Berish (kutilmoqda)» o'tkazishdan oldin ko'rsatiladi; bonus ballar takrorlash kaliti bilan qaytariladi va ikki barobar bo'lmaydi.
- **Yuk xati va ombor.** Barcha maydonlarni tahrirlash server orqali o'tadi, xarid narxi 4 xonagacha qabul qilinadi; ko'chirishlar takrorlanmaydi va sana bilan; mahsulotni arxivga o'tkazish mumkin.
- **Chop etish.** Yorliq nusxalari qismlarga bo'lib chop etiladi va «M dan N tasi chop etildi» maslahati ko'rinadi; yorliq printerini tashlash mumkin; «Shtrix-kod chop etish» va «Narxni o'zgartirish» huquqlari alohida ishlaydi.
- **Egasi dasturi.** Xotira kam va joyida yangilanishlar; «Saytdan buyurtmalar» «Qayerdan» filtri bilan.
- **Xabarlar.** Xatolar va muhim xabarlar endi o'zi yo'qolmaydi — ular oynada qoladi; muvaffaqiyat qisqa plashka bilan ko'rsatiladi.
- **Qulaylik.** Ramkalar kontrastliroq, tugmalar kattaroq, past ekranlarda masshtab 0,9.
- **Sayt muharriri.** Telegram tugmasi va «Do'kon yordamchisi».
