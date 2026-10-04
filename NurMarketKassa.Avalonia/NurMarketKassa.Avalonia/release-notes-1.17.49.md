## v1.17.49

ИИ-советник в программе владельца; изъятие не уводит кассу в минус; вопрос «Пополнить склад?» и чек после оплаты работают правильно.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **ИИ-советник** — новый раздел программы владельца (сразу под «Сводкой»). Спросите о своём магазине: как прошла неделя, что заказать, что не продаётся, где теряются деньги. Советник видит:
  - выручку, чеки, средний чек и прибыль — те же цифры, что в «Сводке» (с сервера NurCRM);
  - ABC-анализ за 30 дней — по выручке, прибыли, количеству, категориям, брендам и складу;
  - склад — все товары с ценой продажи, закупкой и остатком;
  - долги клиентов — сколько вам должны и с какого числа.
- **Список должников с именами и телефонами** — по просьбе «дай список должников». В интернет уходят только суммы под кодами; имена и телефоны подставляет сама программа на вашем компьютере.
- Нужен бесплатный ключ Google Gemini — тот же, что у ИИ Telegram-бота (раздел сам подскажет, где его взять).
- **«Тех. поддержка»:** кнопка «Скопировать информацию об устройстве» — сведения о компьютере и кассе в буфер обмена, чтобы отправить их в поддержку.

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Изъятие больше, чем наличных в кассе | Проходило, касса уходила в минус | Запрещено: «В кассе только N сом — изъять M сом нельзя» |
| Подтверждение изъятия | Без суммы остатка | «Изъять 1,00 сом из кассы? В кассе станет 10692,50 сом» |
| «Пополнить склад?» → «Нет» | Вопрос открывался снова и снова | Один вопрос; возвращается прежнее количество |
| Оплата, пока открыт вопрос о пополнении | Могла начаться | Не начинается: «Сначала ответьте на вопрос о пополнении склада» |
| Кассу закрыли сразу после оплаты | Оплаченные товары возвращались в чек | Чек пустой |

### Проверено (05.10, тестовый аккаунт, эта сборка установлена на кассу)

- Продажи: наличные, безнал, два товара в чеке — все прошли, оплата 0,98–1,26 с.
- Оплата → касса закрыта через 0,4 с после ответа сервера → после запуска чек пустой, остаток товара уменьшился на 1.
- Изъятие 99 999 сом при 10 693,50 в кассе — отказ без обращения к серверу; изъятие 1 сом — подтверждение с остатком (отменено, деньги не двигались).
- Количество 20 при остатке 14 → «Пополнить склад?»; Enter (оплата) в это время — оплата не началась; «Нет» — количество вернулось к 1.
- ИИ-советник: выручка за неделю совпала со «Сводкой» (886 049,17 сом); ABC-анализ — группы A и C; список должников — с именами и телефонами.
- «Скопировать информацию об устройстве» — в буфере версия 1.17.49, система, экран, память.

---

# Кыргызча

- **Жаңы:** ээсинин программасында «ИИ-кеңешчи» — дүкөн жөнүндө ИИ менен маек: киреше жана пайда («Жыйынтыктагыдай»), 30 күндүк ABC-анализ, кампа, кардарлардын карыздары. Карызкорлордун тизмеси аттары жана телефондору менен — интернетке коддор гана кетет, аттарды программа өзү коёт. «Тех колдоо»: «Түзмөк жөнүндө маалыматты көчүрүү» баскычы.
- **Болгон → болду:** кассада бардан көп акча алуу — мурда өтчү, эми тыюу салынды; алуунун алдында канча калаары жазылат. «Кампаны толуктайсызбы?» → «Жок» — мурда кайра-кайра ачылчу, эми бир суроо жана мурунку сан кайтат; суроо ачык турганда төлөм башталбайт. Төлөмдөн кийин касса дароо жабылса — товарлар чекке кайтчу, эми чек бош.
- **Текшерилди (05.10, сыноо аккаунту):** сатуулар (накталай, накталай эмес, эки товар) — баары өттү, 0,98–1,26 сек; төлөмдөн кийин кассаны жабуу — чек бош; ашыкча алуу — тыюу; толуктоо суроосу; ИИ-кеңешчинин сандары «Жыйынтык» менен дал келди.

---

# English

- **New:** “AI advisor” in the owner program — chat with AI about your shop: revenue and profit (same as the Overview), 30-day ABC analysis, the warehouse, customer debts. The debtor list shows names and phones — only codes go online, the program fills in names on your computer. “Support”: a “Copy device information” button.
- **Before → after:** taking out more cash than the till holds — used to go through, now refused; the confirmation shows what will remain. “Restock?” → “No” — used to reopen again and again, now one question and the previous quantity comes back; payment won't start while it is open. Closing the till right after payment — items used to come back into the receipt, now the receipt is empty.
- **Tested (05.10, test account):** sales (cash, transfer, two items) — all passed, 0.98–1.26 s; closing the till right after payment — empty receipt; oversized cash-out — refused; the restock question; the AI advisor's figures matched the Overview.

---

# Türkçe

- **Yeni:** işletme sahibi programında «Yapay zekâ danışmanı» — mağazanız hakkında yapay zekâ ile sohbet: ciro ve kâr (Özet ile aynı), 30 günlük ABC analizi, depo, müşteri borçları. Borçlu listesi ad ve telefonlarla gösterilir — internete yalnızca kodlar gider, adları program bilgisayarınızda yerleştirir. «Destek»: «Cihaz bilgilerini kopyala» düğmesi.
- **Önce → sonra:** kasadakinden fazla nakit çıkışı — önceden geçiyordu, şimdi reddediliyor; onayda kalacak tutar yazıyor. «Stok eklensin mi?» → «Hayır» — önceden tekrar tekrar açılıyordu, şimdi tek soru ve önceki miktar geri geliyor; soru açıkken ödeme başlamıyor. Ödemeden hemen sonra kasanın kapatılması — ürünler fişe geri geliyordu, şimdi fiş boş.
- **Test edildi (05.10, test hesabı):** satışlar (nakit, havale, iki ürün) — hepsi geçti, 0,98–1,26 sn; ödemeden hemen sonra kapatma — fiş boş; fazla nakit çıkışı — reddedildi; stok sorusu; danışmanın rakamları Özet ile tuttu.

---

# O'zbekcha

- **Yangi:** ega dasturida «SI maslahatchi» — do'kon haqida SI bilan suhbat: tushum va foyda («Umumiy ko'rinish»dagidek), 30 kunlik ABC-tahlil, ombor, mijozlar qarzlari. Qarzdorlar ro'yxati ism va telefonlar bilan — internetga faqat kodlar ketadi, ismlarni dastur kompyuteringizda o'zi qo'yadi. «Texnik yordam»: «Qurilma ma'lumotlarini nusxalash» tugmasi.
- **Avval → endi:** kassadagidan ko'p naqd chiqim — avval o'tardi, endi rad etiladi; tasdiqlashda qancha qolishi yoziladi. «Omborni to'ldirasizmi?» → «Yo'q» — avval qayta-qayta ochilardi, endi bitta savol va oldingi miqdor qaytadi; savol ochiq turganda to'lov boshlanmaydi. To'lovdan so'ng kassani darhol yopish — mahsulotlar chekka qaytardi, endi chek bo'sh.
- **Tekshirildi (05.10, sinov akkaunti):** sotuvlar (naqd, o'tkazma, ikki mahsulot) — hammasi o'tdi, 0,98–1,26 s; to'lovdan so'ng darhol yopish — chek bo'sh; ortiqcha chiqim — rad etildi; to'ldirish savoli; maslahatchi raqamlari «Umumiy ko'rinish» bilan mos keldi.
