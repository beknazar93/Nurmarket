## v1.17.22

---

# Русский

### Новое

Сверка аналитики кассы и программы владельца с сайтом nurcrm.kg. Выгрузки, ABC, способы оплаты, смены и номера чеков теперь считаются по тем же правилам, что и сайт.

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Выгрузка в Excel и Word | Считалась по истории самой кассы: за 28.09 в файле 153 052,97 и 40 чеков против 152 992,47 и 39 на экране — в выручку попадал долг, скидка по строке не вычиталась, «Скидки 0,00» | Берёт цифры сервера: 154 309,47, 58 чеков, скидки 20,50, возвраты 20 000 — как на сайте и на экране «Финансы» |
| ABC-анализ | Считался по истории кассы: за неделю 700 167 в программе владельца и 1 453 421 в кассе при 1 121 092 на сайте | Берётся с вкладки «Товары» сервера: 1 121 026,42 за неделю, как на сайте |
| Смешанная оплата | В «Финансах» целиком шла в «Безнал» (792 + 40 = 832) | «Безнал 792,00» и под ним «+ смешанная: 40,00», как на сайте |
| Список смен | Показывал 100 смен из 202 («Закрытые 99») | Все смены: «Закрытые 201» |
| Номер чека | Одна продажа: №9 в «Истории чеков» и №5 в окне «Возврат» | Одинаковый номер в обоих окнах и на сайте |
| «Последние продажи» в «Сводке» | Только первый товар чека | «Asu + ещё 1» |
| Подкачка истории продаж | С сервера добирались только последние 80 продаж, строки — по цене до скидки | Все страницы, цена строки после скидки |

### Проверено перед выпуском (тестовый аккаунт)

- За 28.09 сервер, «Сводка», «Финансы» и выгрузка Excel показывают одно и то же: 154 309,47 сом, 58 чеков, наличные 153 477,47, безнал 792, смешанная 40, скидки 20,50, возвраты 20 000.
- За неделю 22–28.09 и за месяц выручка и ABC совпали с сайтом.
- «Финансы → Смены»: 201 закрытая и 1 открытая, как на сервере.
- Номер последнего чека в «Истории чеков» и в «Возврате» одинаковый.
- 3 контрольные продажи и цикл «закрыть смену → Z-отчёт → открыть» прошли, сумма смены на сервере 534,00 совпала с продажами.

### Что осталось

- Баланс кассы после закрытия смены на сервере меньше на сумму внесений: внесение уходит на сервер как «ручная» операция и в ожидаемую сумму смены не входит. Это правило сервера NurCRM, касса тут ничего не исправит.
- Сервер не хранит, сколько из смешанной оплаты было наличными, поэтому Z-отчёт по-прежнему относит её к безналу.

---

# Кыргызча

### Жаңы

Кассанын жана ээсинин программасынын аналитикасы nurcrm.kg сайты менен салыштырылды. Жүктөөлөр, ABC, төлөм ыкмалары, сменалар жана чектердин номерлери эми сайттагыдай эле эрежелер менен эсептелет.

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| Excel жана Word'го жүктөө | Кассанын өз тарыхы боюнча эсептелчү: 28.09 үчүн файлда 153 052,97 жана 40 чек, экранда 152 992,47 жана 39 — түшүмгө карыз кирчү, саптагы арзандатуу кемитилчү эмес, «Арзандатуулар 0,00» | Сервердин сандарын алат: 154 309,47, 58 чек, арзандатуулар 20,50, кайтаруулар 20 000 — сайттагыдай жана «Каржы» экранындагыдай |
| ABC-талдоо | Кассанын тарыхы боюнча эсептелчү: жумада ээсинин программасында 700 167, кассада 1 453 421, сайтта 1 121 092 | Сервердин «Товарлар» өтмөгүнөн алынат: жумада 1 121 026,42, сайттагыдай |
| Аралаш төлөм | «Каржыда» толугу менен «Накталай эмеске» кирчү (792 + 40 = 832) | «Накталай эмес 792,00» жана анын астында «+ аралаш: 40,00», сайттагыдай |
| Сменалардын тизмеси | 202 сменадан 100ү көрүнчү («Жабылган 99») | Бардык сменалар: «Жабылган 201» |
| Чектин номери | Бир сатуу: «Чектердин тарыхында» №9, «Кайтаруу» терезесинде №5 | Эки терезеде жана сайтта бирдей номер |
| «Жыйынтыктагы» «Акыркы сатуулар» | Чектин биринчи товары гана | «Asu + дагы 1» |
| Сатуулардын тарыхын жүктөө | Серверден акыркы 80 сатуу гана алынчу, саптар арзандатууга чейинки баада | Бардык беттер, саптын баасы арзандатуудан кийин |

### Чыгаруудан мурун текшерилди (тесттик аккаунт)

- 28.09 үчүн сервер, «Жыйынтык», «Каржы» жана Excel'ге жүктөө бирдей көрсөтөт: 154 309,47 сом, 58 чек, накталай 153 477,47, накталай эмес 792, аралаш 40, арзандатуулар 20,50, кайтаруулар 20 000.
- 22–28.09 жумасы жана ай үчүн түшүм жана ABC сайт менен дал келди.
- «Каржы → Сменалар»: 201 жабылган жана 1 ачык, сервердегидей.
- Акыркы чектин номери «Чектердин тарыхында» жана «Кайтарууда» бирдей.
- 3 текшерүү сатуусу жана «сменаны жабуу → Z-отчёт → ачуу» цикли өттү, серверде сменанын суммасы 534,00 сатуулар менен дал келди.

### Эмне калды

- Сменаны жапкандан кийин кассанын калдыгы серверде салынган акчанын суммасына азыраак: салуу серверге «кол менен» операция катары кетет жана сменанын күтүлгөн суммасына кирбейт. Бул NurCRM серверинин эрежеси, касса аны оңдой албайт.
- Сервер аралаш төлөмдүн канчасы накталай болгонун сактабайт, ошондуктан Z-отчёт аны мурдагыдай накталай эмеске кошот.

---

# English

### New

Kassa and owner program analytics were reconciled with the nurcrm.kg website. Exports, ABC, payment methods, shifts and receipt numbers are now calculated by the same rules as the website.

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| Excel and Word export | Calculated from the till's own history: for 28.09 the file showed 153,052.97 and 40 receipts against 152,992.47 and 39 on screen — debt was counted as revenue, line discounts were not subtracted, “Discounts 0.00” | Uses the server's numbers: 154,309.47, 58 receipts, discounts 20.50, returns 20,000 — the same as the website and the “Finance” screen |
| ABC analysis | Calculated from the till's history: 700,167 in the owner program and 1,453,421 in the till for the week, against 1,121,092 on the website | Taken from the server's “Products” tab: 1,121,026.42 for the week, as on the website |
| Mixed payment | Went entirely into “Card” in “Finance” (792 + 40 = 832) | “Card 792.00” with “+ mixed: 40.00” below it, as on the website |
| Shift list | Showed 100 of 202 shifts (“Closed 99”) | All shifts: “Closed 201” |
| Receipt number | One sale: №9 in “Receipt history” and №5 in the “Return” window | The same number in both windows and on the website |
| “Recent sales” in the “Overview” | Only the first product of the receipt | “Asu + 1 more” |
| Sales history download | Only the last 80 sales were fetched from the server, lines at the price before discount | All pages, the line price after discount |

### Checked before release (test account)

- For 28.09 the server, “Overview”, “Finance” and the Excel export show the same: 154,309.47 som, 58 receipts, cash 153,477.47, card 792, mixed 40, discounts 20.50, returns 20,000.
- For the week of 22–28.09 and for the month, revenue and ABC matched the website.
- “Finance → Shifts”: 201 closed and 1 open, as on the server.
- The number of the latest receipt is the same in “Receipt history” and in “Return”.
- 3 control sales and a “close shift → Z-report → open” cycle went through; the shift total on the server, 534.00, matched the sales.

### What remains

- After a shift is closed, the server shows the till balance lower by the amount of cash deposits: a deposit is sent to the server as a “manual” operation and is not included in the expected shift amount. This is a NurCRM server rule; the till cannot fix it.
- The server does not store how much of a mixed payment was cash, so the Z-report still counts it as card.

---

# Türkçe

### Yenilikler

Kasa ve sahip programı analizi nurcrm.kg sitesiyle karşılaştırıldı. Dışa aktarımlar, ABC, ödeme yöntemleri, vardiyalar ve fiş numaraları artık siteyle aynı kurallarla hesaplanıyor.

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| Excel ve Word dışa aktarımı | Kasanın kendi geçmişiyle hesaplanıyordu: 28.09 için dosyada 153.052,97 ve 40 fiş, ekranda 152.992,47 ve 39 — borç ciroya giriyor, satır indirimi düşülmüyordu, «İndirimler 0,00» | Sunucu rakamlarını kullanır: 154.309,47, 58 fiş, indirimler 20,50, iadeler 20.000 — site ve «Finans» ekranıyla aynı |
| ABC analizi | Kasa geçmişiyle hesaplanıyordu: haftalık sahip programında 700.167, kasada 1.453.421, sitede 1.121.092 | Sunucunun «Ürünler» sekmesinden alınır: haftalık 1.121.026,42, sitedeki gibi |
| Karışık ödeme | «Finans»ta tamamen «Kart»a giriyordu (792 + 40 = 832) | «Kart 792,00» ve altında «+ karışık: 40,00», sitedeki gibi |
| Vardiya listesi | 202 vardiyadan 100'ünü gösteriyordu («Kapalı 99») | Tüm vardiyalar: «Kapalı 201» |
| Fiş numarası | Aynı satış: «Fiş geçmişi»nde №9, «İade» penceresinde №5 | Her iki pencerede ve sitede aynı numara |
| «Özet»te «Son satışlar» | Fişin yalnızca ilk ürünü | «Asu + 1 daha» |
| Satış geçmişini indirme | Sunucudan yalnızca son 80 satış alınıyordu, satırlar indirim öncesi fiyatla | Tüm sayfalar, satır fiyatı indirimden sonra |

### Yayından önce kontrol edildi (test hesabı)

- 28.09 için sunucu, «Özet», «Finans» ve Excel dışa aktarımı aynı şeyi gösteriyor: 154.309,47 som, 58 fiş, nakit 153.477,47, kart 792, karışık 40, indirimler 20,50, iadeler 20.000.
- 22–28.09 haftası ve ay için ciro ve ABC siteyle eşleşti.
- «Finans → Vardiyalar»: 201 kapalı ve 1 açık, sunucudaki gibi.
- Son fişin numarası «Fiş geçmişi»nde ve «İade»de aynı.
- 3 kontrol satışı ve «vardiyayı kapat → Z raporu → aç» döngüsü geçti, sunucudaki vardiya toplamı 534,00 satışlarla eşleşti.

### Kalanlar

- Vardiya kapatıldıktan sonra sunucu kasa bakiyesini para girişleri kadar düşük gösteriyor: para girişi sunucuya «manuel» işlem olarak gidiyor ve beklenen vardiya tutarına dahil edilmiyor. Bu NurCRM sunucusunun kuralı, kasa bunu düzeltemez.
- Sunucu karışık ödemenin ne kadarının nakit olduğunu saklamıyor, bu yüzden Z raporu onu hâlâ karta sayıyor.

---

# O'zbekcha

### Yangiliklar

Kassa va ega dasturi tahlili nurcrm.kg sayti bilan solishtirildi. Eksportlar, ABC, to'lov usullari, smenalar va chek raqamlari endi sayt bilan bir xil qoidalar bo'yicha hisoblanadi.

### Tuzatildi: avval → endi

| Nima | Avval | Endi |
|---|---|---|
| Excel va Word'ga eksport | Kassaning o'z tarixi bo'yicha hisoblanardi: 28.09 uchun faylda 153 052,97 va 40 chek, ekranda 152 992,47 va 39 — tushumga qarz kirardi, qatordagi chegirma ayirilmasdi, «Chegirmalar 0,00» | Server raqamlarini oladi: 154 309,47, 58 chek, chegirmalar 20,50, qaytarishlar 20 000 — sayt va «Moliya» ekranidagidek |
| ABC tahlili | Kassa tarixi bo'yicha hisoblanardi: hafta uchun ega dasturida 700 167, kassada 1 453 421, saytda 1 121 092 | Serverning «Mahsulotlar» yorlig'idan olinadi: hafta uchun 1 121 026,42, saytdagidek |
| Aralash to'lov | «Moliya»da to'liq «Naqdsiz»ga kirardi (792 + 40 = 832) | «Naqdsiz 792,00» va uning ostida «+ aralash: 40,00», saytdagidek |
| Smenalar ro'yxati | 202 ta smenadan 100 tasini ko'rsatardi («Yopilgan 99») | Barcha smenalar: «Yopilgan 201» |
| Chek raqami | Bitta sotuv: «Cheklar tarixi»da №9, «Qaytarish» oynasida №5 | Ikkala oynada va saytda bir xil raqam |
| «Umumiy»dagi «So'nggi sotuvlar» | Chekning faqat birinchi mahsuloti | «Asu + yana 1» |
| Sotuvlar tarixini yuklash | Serverdan faqat oxirgi 80 ta sotuv olinardi, qatorlar chegirmadan oldingi narxda | Barcha sahifalar, qator narxi chegirmadan keyin |

### Chiqarishdan oldin tekshirildi (test hisobi)

- 28.09 uchun server, «Umumiy», «Moliya» va Excel eksporti bir xil ko'rsatadi: 154 309,47 so'm, 58 chek, naqd 153 477,47, naqdsiz 792, aralash 40, chegirmalar 20,50, qaytarishlar 20 000.
- 22–28.09 haftasi va oy uchun tushum va ABC sayt bilan mos keldi.
- «Moliya → Smenalar»: 201 ta yopilgan va 1 ta ochiq, serverdagidek.
- Oxirgi chek raqami «Cheklar tarixi»da va «Qaytarish»da bir xil.
- 3 ta nazorat sotuvi va «smenani yopish → Z-hisobot → ochish» sikli o'tdi, serverdagi smena summasi 534,00 sotuvlar bilan mos keldi.

### Nima qoldi

- Smena yopilgandan keyin server kassa qoldig'ini kiritilgan pul miqdoricha kam ko'rsatadi: pul kiritish serverga «qo'lda» amal sifatida ketadi va smenaning kutilgan summasiga kirmaydi. Bu NurCRM serverining qoidasi, kassa buni tuzata olmaydi.
- Server aralash to'lovning qancha qismi naqd bo'lganini saqlamaydi, shuning uchun Z-hisobot uni hali ham naqdsizga qo'shadi.
