## NurCRM Mobile 1.18.18

Файл: `nurmarket-mobile-1.18.18.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется.

### Русский

**Что нового по сравнению с 1.18.17** (аудит №4, волна 0-D)
- **Как на сервере:** округление весовых строк (вниз до шага весов), заголовок клиента, филиал и устройство в чеке, языковой пакет.
- **«Очистить чек» и «−» теперь требуют право «удаление из чека»**, как на кассе. «Очистить чек» спрашивает подтверждение и пишет каждую строку в «Удалено из чека»; «−» и ввод меньшего количества пишут разницу. То же в командах ИИ. Удаление отложенного чека — с вопросом и правом. Закрытие смены с набранным чеком предлагает отложить или очистить его.
- **Журнал «Удалено из чека» не стирается выходом из аккаунта** (привязан к аккаунту; чужой аккаунт его не видит).
- **Простой сервера не уводит продажи в «Не принято»:** при ответах 502/503/504/408/429 (и 500 — до 8 попыток) телефон повторяет отправку с паузой 20 с → 40 с → 80 с … до 10 минут, в «Чеки → Не отправленные» видна плашка «Сервер не отвечает с ЧЧ:ММ».
- **Ключи повтора возврата, внесения и оплаты долга** хранятся по подписи (до 5 штук, возврат — 24 часа): возврат по чеку B не затирает ключ неизвестного возврата по чеку A — двойной выдачи денег нет.
- **Бумажный чек сходится до копейки:** подытог = сумма напечатанных строк, разница с ИТОГО — строка «Округление» (на экране, в PDF, на печати).
- **Сбитая дата телефона:** время продажи берётся по часам сервера, на «Продаже» и главной — плашка «Время на телефоне неверное».
- **Ревизия сохраняет вес с тремя знаками** (1,235 кг); поля количества, веса и суммы не принимают лишние цифры (штрихкод в поле), количество больше 999 — вопрос «Точно?».
- **Недоплата:** если сервер насчитал больше, чем получено наличными, на «Оплачено» красная плашка «доберите ещё N сом».
- **Дизайн:** в тёмной теме видны выбранные «Розница / Опт», «Товары / Чек», «% / сом», чипы категорий; красные, зелёные и жёлтые суммы стали читаемыми. Плашка «Добавлено» после скана короче и не закрывает «Оплатить». Кнопка оплаты поднимается над клавиатурой, «Готово» проводит оплату, на кнопке «Проводим оплату…». Плитки товаров крупнее и читаются экранным диктором. Закрытая смена: прямо на «Продаже» плашка и кнопка «Открыть смену». Сетка «Продажи» обновляется, пока качается каталог. На планшете форма оплаты не шире 560 dp, плитки крупнее.
- **Весовая этикетка** после удаления строки снова добавляется без вопроса; поиск «5» не тянет товары по доп. штрихкоду; «Повторить печать» после обрыва не открывает ящик второй раз.
- **Встроенный принтер Sunmi (по желанию):** на телефонах Sunmi в «Принтер чеков» есть переключатель, чек печатается средствами Sunmi, все буквы (ң ө ү ş ğ). На остальных телефонах ничего не меняется. Пакеты: `sunmi_printer_plus` (BSD-3), `barcode` (Apache-2.0, штрихкод картинкой для этикеток — подготовка).
- Не вошло в эту сборку: планшетная раскладка «сетка + чек», сохранение вкладок, код доступа сотрудника, отправка журнала удалений на сервер — нужны телефон, решение владельца или ответ сервера.

### Кыргызча

**1.18.17 менен салыштырганда эмне жаңы** (4-аудит, 0-D толкуну)
- **Сервердей:** салмактуу саптарды тегеректөө (тараза кадамына чейин төмөн), кардардын аталышы, чекте филиал жана түзмөк, тил пакети.
- **«Чекти тазалоо» жана «−» эми «чектен өчүрүү» укугун талап кылат**, кассадагыдай. «Чекти тазалоо» ырастоону сурайт жана ар бир сапты «Чектен өчүрүлдү» журналына жазат; «−» жана азыраак сан киргизүү айырманы жазат. ИИ буйруктарында да ушундай. Кечиктирилген чекти өчүрүү — суроо жана укук менен. Чек терилип турганда сменаны жабуу аны кечиктирүүнү же тазалоону сунуштайт.
- **«Чектен өчүрүлдү» журналы аккаунттан чыгууда өчпөйт** (аккаунтка байланган).
- **Сервер токтоп калса, сатуулар «Кабыл алынган жок» болуп калбайт:** 502/503/504/408/429 (жана 500 — 8 аракетке чейин) болгондо телефон 20 с → 40 с → 80 с … 10 мүнөткө чейин тыныгуу менен кайталайт, «Чектер → Жөнөтүлгөн жок» бөлүмүндө «Сервер ЧЧ:ММ бери жооп бербейт» плашкасы бар.
- **Кайтаруунун, киргизүүнүн жана карыз төлөмүнүн кайталоо ачкычтары** кол тамга боюнча сакталат (5ке чейин, кайтарууда — 24 саат): B чеги боюнча кайтаруу A чегинин ачкычын өчүрбөйт — акча эки жолу берилбейт.
- **Кагаз чек тыйынына чейин дал келет:** жыйынтык = басылган саптардын суммасы, ЖАЛПЫ менен айырма — «Тегеректөө» сабы.
- **Телефондун датасы бузулса:** сатуу убактысы сервердин саты боюнча, «Сатуу» жана башкы экранда «Телефондогу убакыт туура эмес» плашкасы.
- **Ревизия салмакты үч белги менен сактайт** (1,235 кг); сан, салмак жана сумма талаалары ашыкча цифраларды кабыл албайт, 999дан көп сан — «Чын элеби?».
- **Жетишпеген төлөм:** сервер накталай алынгандан көп эсептесе, «Төлөндү» экранында кызыл плашка.
- **Дизайн:** караңгы темада тандалган «Чекене / Дүң», «Товарлар / Чек», «% / сом» жана категория чиптери көрүнөт; кызыл, жашыл, сары суммалар окулат. «Кошулду» плашкасы кыска жана «Төлөө» баскычын жаппайт. Төлөм баскычы баскычтоптун үстүндө. Товар плиткалары чоңураак. Смена жабык болсо «Сатуу» экранында «Сменаны ачуу» баскычы. Каталог жүктөлүп жатканда тор жаңырат. Планшетте төлөм формасы 560 dpден кең эмес.
- **Таразанын этикеткасы** сапты өчүргөндөн кийин суроосуз кошулат; «5» издөө кошумча штрихкод боюнча товарларды алып келбейт; «Кайра басуу» үзүлгөндөн кийин суурманы экинчи жолу ачпайт.
- **Sunmi орнотулган принтери (каалоо боюнча):** Sunmi телефондорунда «Чек принтери» бөлүмүндө которгуч бар, бардык тамгалар (ң ө ү ş ğ) басылат. Башка телефондордо өзгөрүү жок.

### English

**What's new since 1.18.17** (audit #4, wave 0-D)
- **Like the server:** weighed-line rounding (down to the scale step), the client heading, branch and device on the receipt, the language pack.
- **“Clear receipt” and “−” now need the “delete from receipt” right**, as on the till. “Clear receipt” asks for confirmation and writes every line to “Removed from receipt”; “−” and a smaller typed quantity log the difference. Same for AI commands. Deleting a held receipt asks and checks the right. Closing a shift with a receipt in progress offers to hold or clear it.
- **The “Removed from receipt” log survives sign-out** (tied to the account).
- **A server outage no longer sends sales to “Not accepted”:** on 502/503/504/408/429 (and 500 — up to 8 attempts) the phone retries with a pause of 20 s → 40 s → 80 s … up to 10 minutes; “Receipts → Unsent” shows a “Server not answering since HH:MM” banner.
- **Retry keys for returns, deposits and debt payments** are stored per signature (up to 5, returns — 24 hours): a return of receipt B no longer overwrites the key of an unknown return of receipt A — no double payout.
- **The paper receipt adds up to the cent:** subtotal = sum of printed lines, the difference to TOTAL is a “Rounding” line.
- **Wrong phone date:** sale time comes from the server clock, and “Sale”/Home show a “The phone's clock is wrong” banner.
- **Stock count keeps weight with three decimals** (1.235 kg); quantity, weight and sum fields reject extra digits, a quantity over 999 asks “Really?”.
- **Underpayment:** if the server counted more than the cash received, “Paid” shows a red “collect N more” banner.
- **Design:** in the dark theme the selected “Retail / Wholesale”, “Products / Receipt”, “% / som” and category chips are visible; red, green and yellow sums are readable. The “Added” banner is shorter and no longer covers “Pay”. The pay button rises above the keyboard. Product tiles are larger. With a closed shift the sale screen has an “Open shift” button. The grid refreshes while the catalog loads. On a tablet the payment form is at most 560 dp wide.
- **A scale label** can be added again without a question after its line is removed; searching “5” no longer pulls products by extra barcode; “Print again” after a dropped connection no longer opens the drawer twice.
- **Built-in Sunmi printer (optional):** on Sunmi phones “Receipt printer” has a switch; the receipt prints with all letters (ң ө ү ş ğ). Other phones are unchanged.

### Türkçe

**1.18.17'ye göre yenilikler** (4. denetim, 0-D dalgası)
- **Sunucu gibi:** tartılı satır yuvarlaması (terazi adımına kadar aşağı), müşteri başlığı, fişte şube ve cihaz, dil paketi.
- **«Fişi temizle» ve «−» artık «fişten silme» yetkisi ister**, kasadaki gibi. «Fişi temizle» onay sorar ve her satırı «Fişten silindi» günlüğüne yazar; «−» ve daha küçük miktar farkı yazar. Yapay zekâ komutlarında da aynı. Bekleyen fişi silmek soru ve yetki ister. Fiş doluyken vardiya kapatmak onu bekletmeyi veya temizlemeyi önerir.
- **«Fişten silindi» günlüğü çıkışla silinmez** (hesaba bağlıdır).
- **Sunucu kesintisi satışları «Kabul edilmedi»ye düşürmez:** 502/503/504/408/429 (ve 500 — 8 denemeye kadar) durumunda telefon 20 sn → 40 sn → 80 sn … 10 dakikaya kadar bekleyerek dener; «Fişler → Gönderilmeyenler»de «Sunucu SS:DD'den beri yanıt vermiyor» uyarısı görünür.
- **İade, para yatırma ve borç ödemesi yeniden deneme anahtarları** imzaya göre saklanır (5'e kadar, iade — 24 saat): B fişi iadesi A fişinin anahtarını ezmez — para iki kez verilmez.
- **Kağıt fiş kuruşu kuruşuna tutar:** ara toplam = basılan satırların toplamı, TOPLAM ile fark «Yuvarlama» satırıdır.
- **Yanlış telefon tarihi:** satış zamanı sunucu saatinden alınır, «Satış» ve ana ekranda «Telefonun saati yanlış» uyarısı çıkar.
- **Sayım ağırlığı üç ondalıkla saklar** (1,235 kg); miktar, ağırlık ve tutar alanları fazla haneleri kabul etmez, 999'dan büyük miktar «Emin misiniz?» diye sorar.
- **Eksik ödeme:** sunucu alınan nakitten fazla hesaplarsa «Ödendi»de kırmızı uyarı çıkar.
- **Tasarım:** koyu temada seçili «Perakende / Toptan», «Ürünler / Fiş», «% / som» ve kategori çipleri görünür; kırmızı, yeşil, sarı tutarlar okunur. «Eklendi» uyarısı kısadır ve «Öde»yi kapatmaz. Ödeme düğmesi klavyenin üstüne çıkar. Ürün karoları büyük. Vardiya kapalıysa satış ekranında «Vardiya aç» düğmesi var. Katalog yüklenirken ızgara yenilenir. Tablette ödeme formu en çok 560 dp.
- **Terazi etiketi** satır silindikten sonra sorusuz eklenir; «5» araması ek barkodla ürün getirmez; bağlantı koptuktan sonra «Tekrar yazdır» çekmeceyi ikinci kez açmaz.
- **Dahili Sunmi yazıcı (isteğe bağlı):** Sunmi telefonlarda «Fiş yazıcısı»nda anahtar var, tüm harfler (ң ө ү ş ğ) basılır. Diğer telefonlar değişmez.

### O'zbekcha

**1.18.17 dan keyingi yangiliklar** (4-audit, 0-D to'lqini)
- **Server kabi:** tarozi qatorlarini yaxlitlash (tarozi qadamigacha pastga), mijoz sarlavhasi, chekda filial va qurilma, til paketi.
- **«Chekni tozalash» va «−» endi «chekdan o'chirish» huquqini talab qiladi**, kassadagidek. «Chekni tozalash» tasdiq so'raydi va har bir qatorni «Chekdan o'chirildi» jurnaliga yozadi; «−» va kichikroq miqdor farqni yozadi. AI buyruqlarida ham shunday. Kechiktirilgan chekni o'chirish savol va huquq bilan. Chek terilib turganda smenani yopish uni kechiktirish yoki tozalashni taklif qiladi.
- **«Chekdan o'chirildi» jurnali akkauntdan chiqishda o'chmaydi** (akkauntga bog'langan).
- **Server to'xtab qolsa, sotuvlar «Qabul qilinmadi»ga tushmaydi:** 502/503/504/408/429 (va 500 — 8 martagacha) bo'lganda telefon 20 s → 40 s → 80 s … 10 daqiqagacha pauza bilan takrorlaydi; «Cheklar → Yuborilmagan»da «Server SS:DD dan beri javob bermayapti» belgisi bor.
- **Qaytarish, kiritish va qarz to'lovi takrorlash kalitlari** imzo bo'yicha saqlanadi (5 tagacha, qaytarishda — 24 soat): B cheki qaytarilishi A chekining kalitini o'chirmaydi — pul ikki marta berilmaydi.
- **Qog'oz chek tiyinigacha to'g'ri keladi:** oraliq jami = chop etilgan qatorlar yig'indisi, JAMI bilan farq — «Yaxlitlash» qatori.
- **Telefon sanasi buzilsa:** sotuv vaqti server soati bo'yicha, «Sotuv» va bosh ekranda «Telefondagi vaqt noto'g'ri» belgisi.
- **Inventarizatsiya vaznni uch xona bilan saqlaydi** (1,235 kg); miqdor, vazn va summa maydonlari ortiqcha raqamlarni qabul qilmaydi, 999 dan ko'p miqdor «Aniqmi?» deb so'raydi.
- **Kam to'lov:** server olingan naqddan ko'p hisoblasa, «To'landi»da qizil belgi chiqadi.
- **Dizayn:** qorong'i mavzuda tanlangan «Chakana / Ulgurji», «Tovarlar / Chek», «% / so'm» va kategoriya chiplari ko'rinadi; qizil, yashil, sariq summalar o'qiladi. «Qo'shildi» plashkasi qisqa va «To'lash»ni yopmaydi. To'lov tugmasi klaviatura ustida. Tovar plitkalari kattaroq. Smena yopiq bo'lsa sotuv ekranida «Smenani ochish» tugmasi bor. Katalog yuklanayotganda to'r yangilanadi. Planshetda to'lov shakli 560 dp dan keng emas.
- **Tarozi yorlig'i** qator o'chirilgandan keyin savolsiz qo'shiladi; «5» qidiruvi qo'shimcha shtrix-kod bo'yicha tovar keltirmaydi; aloqa uzilgandan keyin «Qayta chop etish» tortmani ikkinchi marta ochmaydi.
- **Sunmi o'rnatilgan printeri (ixtiyoriy):** Sunmi telefonlarida «Chek printeri»da tugma bor, barcha harflar (ң ө ү ş ğ) chop etiladi. Boshqa telefonlarda o'zgarish yo'q.
