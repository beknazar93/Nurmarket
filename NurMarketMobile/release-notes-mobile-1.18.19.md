## NurCRM Mobile 1.18.19

Файл: `nurmarket-mobile-1.18.19.apk` (Android 8.0 и новее). Ставится поверх 1.18.x — подпись та же, вход сохраняется.

### Русский

**Что исправлено по сравнению с 1.18.18** (аудит №5)
- **Бумажный чек сходится с ИТОГО при новом правиле сервера:** весовые строки печатаются вниз до шага весов (0,343 кг × 66 = 22,63, а не 22,64), два взвешивания одного товара — одной строкой, как на сервере; строка «Округление» только когда нужна. Так же на встроенном принтере Sunmi. Повторная печать из «Чеков» больше не показывает ложную «скидку −0,01».
- **«Выйти из аккаунта» не стирает чек молча:** с набранным чеком или отложенными нужно право «удаление из чека», вопрос «позиций / отложено», и каждая строка попадает в «Удалено из чека».
- **Повтор возврата через 10+ минут** не выдаёт деньги второй раз: если на сервере уже есть возврат после прошлой попытки, новый не отправляется, показывается «Возврат мог уже пройти». Сверка идёт по часам сервера.
- **Один товар по двум ценам** (розница и опт) в чеке: онлайн оплата не проходит с понятным сообщением; чек из очереди сводится в одну строку (итог тот же) и уходит, а не попадает в «Не принято».
- **Очередь после долгого офлайна:** обрыв связи и простой сервера не тратят попытки, первая же ошибка 409 не уводит оплаченный чек в «Не принято».
- **Шаг весов компании** («сом» / «копейка»): если запрос не ответил, шаг не закрепляется на 14 дней, а запрашивается снова.
- **Мелкое:** окно перезаписи PLU показывает название и номера на своих местах; вес больше 50 кг (случайный скан штрихкода) переспрашивается; в поле филиала не уходит название, только номер; красные кнопки читаются в тёмной теме; на встроенном принтере Sunmi сбой первой строки не считается «частью чека напечатана», а настройка Sunmi на обычном телефоне не делает принтер «готовым».

### Кыргызча

**1.18.18 менен салыштырганда эмне оңдолду** (5-аудит)
- **Кагаз чек сервердин жаңы эрежеси менен ЖАЛПЫга дал келет:** салмактуу саптар тараза кадамына чейин төмөн басылат (0,343 кг × 66 = 22,63, 22,64 эмес), бир товардын эки таразасы сервердегидей бир сап; «Тегеректөө» сабы керек болгондо гана. Орнотулган Sunmi принтеринде да ушундай. «Чектерден» кайра басканда жасалма «−0,01 арзандатуу» көрүнбөйт.
- **«Аккаунттан чыгуу» чекти унчукпай өчүрбөйт:** терилген чек же кечиктирилген чектер болсо «чектен өчүрүү» укугу керек, «позиция / кечиктирилген» суроосу берилет, ар бир сап «Чектен өчүрүлдү» журналына жазылат.
- **10+ мүнөттөн кийин кайтарууну кайталоо** акчаны экинчи жолу бербейт: өткөн аракеттен кийинки кайтаруу серверде бар болсо, жаңысы жөнөтүлбөйт, «Кайтаруу өтүп кетиши мүмкүн» көрүнөт. Салыштыруу сервердин саты боюнча.
- **Чекте бир товар эки баада** (чекене жана дүң): онлайн төлөм түшүнүктүү билдирүү менен токтойт; кезектеги чек бир сапка бириктирилип (жыйынтык бирдей) жөнөтүлөт, «Кабыл алынган жок» болбойт.
- **Узак оффлайндан кийинки кезек:** байланыштын үзүлүшү жана сервердин токтошу аракеттерди жебейт, биринчи 409 каталыгы төлөнгөн чекти «Кабыл алынган жок» кылбайт.
- **Компаниянын тараза кадамы** («сом» / «тыйын»): суроо жооп бербесе, кадам 14 күнгө бекитилбейт, кайра суралат.
- **Майда:** PLU кайра жазуу терезеси атын жана номерлерди өз ордунда көрсөтөт; 50 кг дан ашык салмак (кокус штрихкод) кайра суралат; филиал талаасына аты эмес, номери гана кетет; кызыл баскычтар караңгы темада окулат; Sunmi принтеринде биринчи саптын катасы «чектин бир бөлүгү басылды» деп эсептелбейт, кадимки телефондо Sunmi орнотуусу принтерди «даяр» кылбайт.

### English

**What is fixed since 1.18.18** (audit #5)
- **The paper receipt adds up to the TOTAL under the new server rule:** weighed lines print rounded down to the scale step (0.343 kg × 66 = 22.63, not 22.64), two weighings of one product are one line like on the server; the “Rounding” line appears only when needed. The same on the built-in Sunmi printer. Printing again from Receipts no longer shows a false “discount −0.01”.
- **“Sign out” does not erase the receipt silently:** with a receipt in progress or held receipts it needs the “delete from receipt” right, asks “items / held”, and every line goes to “Removed from receipt”.
- **Repeating a return after 10+ minutes** does not pay out twice: if the server already has a return after the earlier attempt, a new one is not sent and “The return may already be done” is shown. The check uses the server clock.
- **One product at two prices** (retail and wholesale) in a receipt: online payment stops with a clear message; a queued receipt is merged into one line (same total) and sent instead of landing in “Not accepted”.
- **Queue after a long offline spell:** lost connection and server downtime do not use up attempts, the first 409 does not send a paid receipt to “Not accepted”.
- **Company scale step** (“som” / “tiyin”): if the request fails, the step is not pinned for 14 days but asked again.
- **Small things:** the PLU overwrite window shows the name and numbers in the right places; a weight over 50 kg (an accidental barcode scan) is asked again; only a branch id, never a name, is sent; red buttons are readable in the dark theme; on the built-in Sunmi printer a failure on the first line is not counted as “part of the receipt printed”, and the Sunmi setting on an ordinary phone no longer makes the printer “ready”.

### Türkçe

**1.18.18'e göre düzeltilenler** (5. denetim)
- **Kağıt fiş yeni sunucu kuralıyla TOPLAM ile tutar:** tartılı satırlar terazi adımına kadar aşağı basılır (0,343 kg × 66 = 22,63, 22,64 değil), aynı ürünün iki tartımı sunucudaki gibi tek satır; «Yuvarlama» satırı yalnız gerektiğinde. Dahili Sunmi yazıcıda da aynı. «Fişler»den tekrar yazdırınca sahte «−0,01 indirim» görünmez.
- **«Çıkış» fişi sessizce silmez:** doluyken veya bekleyen fişler varken «fişten silme» yetkisi, «kalem / bekleyen» sorusu gerekir; her satır «Fişten silindi» günlüğüne yazılır.
- **10+ dakika sonra iadeyi tekrarlamak** parayı ikinci kez vermez: önceki denemeden sonra sunucuda iade varsa yenisi gönderilmez, «İade zaten yapılmış olabilir» gösterilir. Karşılaştırma sunucu saatiyle yapılır.
- **Fişte aynı ürünün iki fiyatı** (perakende ve toptan): çevrimiçi ödeme anlaşılır bir mesajla durur; kuyruktaki fiş tek satırda birleştirilip (toplam aynı) gönderilir, «Kabul edilmedi»ye düşmez.
- **Uzun çevrimdışı sonrası kuyruk:** bağlantı kopması ve sunucu kesintisi denemeleri harcamaz, ilk 409 ödenmiş fişi «Kabul edilmedi»ye götürmez.
- **Şirketin terazi adımı** («som» / «kuruş»): istek yanıtsız kalırsa adım 14 güne sabitlenmez, yeniden sorulur.
- **Küçük şeyler:** PLU üzerine yazma penceresi adı ve numaraları yerinde gösterir; 50 kg üzeri ağırlık (yanlışlıkla barkod) tekrar sorulur; şube için ad değil yalnız numara gönderilir; kırmızı düğmeler koyu temada okunur; dahili Sunmi yazıcıda ilk satır hatası «fişin bir kısmı basıldı» sayılmaz, sıradan telefonda Sunmi ayarı yazıcıyı «hazır» yapmaz.

### O'zbekcha

**1.18.18 dan keyin tuzatilganlar** (5-audit)
- **Qog'oz chek serverning yangi qoidasi bilan JAMI ga to'g'ri keladi:** tarozi qatorlari tarozi qadamigacha pastga chop etiladi (0,343 kg × 66 = 22,63, 22,64 emas), bir tovarning ikki tortilishi serverdagidek bitta qator; «Yaxlitlash» qatori faqat kerak bo'lganda. O'rnatilgan Sunmi printerida ham shunday. «Cheklar»dan qayta chop etganda yolg'on «−0,01 chegirma» ko'rinmaydi.
- **«Akkauntdan chiqish» chekni jimgina o'chirmaydi:** terilgan chek yoki kechiktirilgan cheklar bo'lsa «chekdan o'chirish» huquqi, «pozitsiya / kechiktirilgan» savoli kerak, har bir qator «Chekdan o'chirildi» jurnaliga yoziladi.
- **10+ daqiqadan keyin qaytarishni takrorlash** pulni ikkinchi marta bermaydi: oldingi urinishdan keyin serverda qaytarish bo'lsa, yangisi yuborilmaydi, «Qaytarish allaqachon o'tgan bo'lishi mumkin» ko'rsatiladi. Solishtirish server soati bo'yicha.
- **Chekda bir tovar ikki narxda** (chakana va ulgurji): onlayn to'lov tushunarli xabar bilan to'xtaydi; navbatdagi chek bitta qatorga birlashtirilib (jami bir xil) yuboriladi, «Qabul qilinmadi»ga tushmaydi.
- **Uzoq oflayndan keyingi navbat:** aloqa uzilishi va server to'xtashi urinishlarni sarflamaydi, birinchi 409 to'langan chekni «Qabul qilinmadi»ga olib bormaydi.
- **Kompaniyaning tarozi qadami** («so'm» / «tiyin»): so'rov javob bermasa, qadam 14 kunga qotirilmaydi, qayta so'raladi.
- **Mayda ishlar:** PLU qayta yozish oynasi nom va raqamlarni o'z joyida ko'rsatadi; 50 kg dan ortiq vazn (tasodifiy shtrix-kod) qayta so'raladi; filial uchun nom emas, faqat raqam yuboriladi; qizil tugmalar qorong'i mavzuda o'qiladi; o'rnatilgan Sunmi printerida birinchi qator xatosi «chekning bir qismi chop etildi» deb hisoblanmaydi, oddiy telefonda Sunmi sozlamasi printerni «tayyor» qilmaydi.
