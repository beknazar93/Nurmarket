## v1.17.36 (тестовая)

Тестовая версия: телеграм-бот заработал из программы владельца, понимает обычные слова и получил бесплатный ИИ для общения — с владельцем и с покупателями.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца);
- `NurMarketOwner-owner-Setup.exe` — только программа владельца «NurMarket Владелец».

---

# Русский

### Новое

- **Бот понимает обычные слова.** Можно писать без команд: «сколько заработали сегодня», «выручка за неделю», «кто должен», «список должников», «что заканчивается», «цена кола», «бүгүн канча түшүм», «ким карыз». Работает без интернета и бесплатно.
- **ИИ для общения (бесплатно, Google Gemini).** Настройки → Операции → «ИИ-помощник для общения»: нажмите «Получить бесплатный ключ», вставьте ключ, нажмите «Проверить ИИ».
  - **Владельцу** ИИ отвечает на любые вопросы («почему упала выручка», «как поднять продажи») по сводке магазина и не выдумывает цифры.
  - **Покупателям** бот отвечает как продавец-консультант: товары, цены, есть ли в наличии. Выручку, долги и данные других покупателей он не знает и не выдаёт. Не больше 20 вопросов в час от одного человека.
  - Если Google перегружен, бот переключается на другую модель, а покупателю отвечает сам — по каталогу.
- **«Сводка» владельца:** новая карточка «Заканчивается на складе» — товары с малым остатком и ссылка на «Пополнение».

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Бот, подключённый в программе владельца | Пробное сообщение приходило, а на команды (/segodnya, /soveti) бот молчал | Касса сама берёт настройки бота из программы владельца; если касса выключена — отвечает программа владельца |
| Окно «Подключение телеграм-бота» | Открытие окна выключало «Отвечать на команды» | Галочки не меняются, пока вы их сами не нажмёте |
| Ошибка отправки ответа | Ответ пропадал молча | Бот повторяет ответ простым текстом, причина пишется в журнал |
| «Пополнение и сроки» → «Подтянуть историю» | Окно «Ошибка» | Сообщение с результатом показывается нормально |

### Проверено

- Бот подхватывает настройки из программы владельца и отвечает; слушает команды только одна программа (нет конфликта Telegram).
- Помощник понимает 22 фразы на русском и кыргызском; вопросы покупателя о долге идут на его долг.
- Ключ Google: неверный ключ даёт понятную ошибку; список доступных моделей и скорость ответа проверены на настоящем ключе.
- Сборка установлена на этом компьютере, программа владельца работает.

### Не проверено

- Полный регресс продаж, долгов и смены — поэтому версия **тестовая**. Код продаж не менялся.
- Ответы ИИ в Telegram после последних исправлений оформления списков.

---

# Кыргызча

- **Бот кадимки сөздөрдү түшүнөт:** «бүгүн канча түшүм», «ким карыз», «кола баасы» — интернетсиз жана акысыз.
- **Баарлашуу үчүн ЖИ (акысыз, Google Gemini):** Жөндөөлөр → Операциялар → «Баарлашуу үчүн ЖИ-жардамчы» — ачкыч алып, коюп, «ЖИни текшерүү» басыңыз. Ээсине ар кандай суроолорго дүкөндүн жыйынтыгы боюнча жооп берет; сатып алуучуларга — товарлар, баалар жана бар-жогу гана.
- **Оңдолду:** ээсинин программасында туташтырылган бот эми буйруктарга жооп берет; «Толуктоо жана мөөнөттөр» терезеси ката бербейт.
- **«Жыйынтык»:** «Кампада түгөнүп баратат» карточкасы.
- Бул — **сыноо** версиясы.

---

# English

- **The bot understands plain words:** “how much did we make today”, “who owes”, “price of cola” — offline and free.
- **AI for chatting (free, Google Gemini):** Settings → Operations → “AI assistant for chatting” — get a key, paste it, press “Test AI”. It chats with the owner using the shop summary; customers get a shop assistant that knows only products, prices and availability.
- **Fixed:** a bot connected in the owner app now answers commands; “Restock & expiry” no longer shows an error.
- **Overview:** new “Running low in stock” card.
- This is a **test** version.

---

# Türkçe

- **Bot sade cümleleri anlar:** «bugün ne kadar kazandık», «kim borçlu», «kola fiyatı» — internetsiz ve ücretsiz.
- **Sohbet için YZ (ücretsiz, Google Gemini):** Ayarlar → İşlemler → «Sohbet için YZ asistanı» — anahtarı alın, yapıştırın, «YZ'yi test et»e basın. Sahiple mağaza özetine göre sohbet eder; müşterilere yalnızca ürünler, fiyatlar ve stok hakkında yanıt verir.
- **Düzeltildi:** sahip programında bağlanan bot artık komutlara yanıt veriyor; «Stok yenileme ve SKT» artık hata vermiyor.
- **Özet:** yeni «Stokta azalanlar» kartı.
- Bu bir **test** sürümüdür.

---

# O‘zbekcha

- **Bot oddiy so‘zlarni tushunadi:** «bugun qancha ishladik», «kim qarzdor», «kola narxi» — internetsiz va bepul.
- **Suhbat uchun SI (bepul, Google Gemini):** Sozlamalar → Operatsiyalar → «Suhbat uchun SI yordamchi» — kalitni oling, qo‘ying, «SIni tekshirish»ni bosing. Egasi bilan do‘kon xulosasi bo‘yicha suhbatlashadi; xaridorlarga faqat mahsulotlar, narxlar va mavjudlik haqida javob beradi.
- **Tuzatildi:** egasi dasturida ulangan bot endi buyruqlarga javob beradi; «To'ldirish va muddatlar» endi xato bermaydi.
- **Umumiy ko‘rinish:** yangi «Omborda tugayapti» kartasi.
- Bu **sinov** versiyasi.
