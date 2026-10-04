## v1.17.46

Касса не ждёт интернет и сервер; QR клиента из приложения NurCRM; точная сумма возврата; сенсорный экран; ИИ бота своим ключом.

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).

Обновление не меняет ваши настройки.

---

# Русский

### Новое

- **Нет интернета или сервер NurCRM не отвечает — касса не ждёт.** Продажа сразу уходит в очередь, смена открывается и закрывается за 2–3 секунды. Когда связь вернётся, касса сама отправит чеки на сервер, каждый ровно один раз.
- **QR клиента из приложения NurCRM «Мои баллы».** Отсканировали код с телефона покупателя — клиент сразу выбран в чеке, видны его бонусы. Если клиента нет — касса откроет «Новый клиент» с уже подставленным номером.
- **Возврат:** в сообщении «Возврат оформлен» и на чеке — сумма, которую нужно выдать покупателю («Выдать покупателю: N сом»).
- **Сенсорный экран:** два пальца прокручивают список.
- **Телеграм-бот:** ИИ на сервере можно включить своим бесплатным ключом Google Gemini прямо из программы: Настройки → Операции → бот → «ИИ на сервере».

### Было → стало

| Что | Было | Стало |
|---|---|---|
| Открытие или закрытие смены, когда сервер молчит | До 55 секунд ожидания | 2–3 секунды, на сервер — фоном |
| Оплата во время сбоя сервера | Каждая оплата ждала сервер | Первая — до 2 с, следующие — мгновенно |
| Сервер ответил ошибкой, хотя продажу провёл | Бывала вторая продажа на тот же чек | Одна продажа |
| Вход без интернета, когда истёк вход | Сохранённый вход стирался — приходилось входить заново | Касса входит автономно, вход сохраняется |
| Чек возврата при скидке на чек или продаже в долг | Сумма больше выданной (56 вместо 50,40) | Сумма, которую выдал сервер |
| Касание двумя пальцами | Две кнопки нажимались сразу, бывала ошибка | Прокрутка, кнопки не нажимаются |
| Z-отчёт | Предоплата долга считалась дважды | Один раз |
| Трафик | Каталог качался без сжатия | Ответы сервера сжаты — в 8 раз меньше |

### Проверено (04.10, тестовый аккаунт)

- QR клиента «NURCRM…»: клиент найден и выбран в чеке, в окне оплаты — его бонусы; номер в журнале скрыт.
- Продажа наличными и продажа в долг с предоплатой 10 сом — предоплата проведена один раз, долг 20.
- Полный возврат: «Выдать покупателю: 100,00 сом».
- Без интернета (заблокирован брандмауэром): две продажи ушли в очередь за 0,02 с, в шапке «Автономно» и «В очереди: 2». После возврата связи касса сама стала «Онлайн» через 18 с и отправила обе продажи — на сервере ровно две (№1359, №1360), без дублей.
- Закрытие смены (с блоком «Прокат за смену») и открытие новой, внесение 50 сом.
- Скидка в убыток: окно «Я знаю что делаю», «Отмена» возвращает прежнюю скидку.
- Стенд с поддельным сервером (обрыв, «чёрная дыра», ошибки 5xx, 429, медленный ответ): 45 чеков = 45 продаж, 109 проверок из 109.
- Касание двумя пальцами — проверено только по коду: на компьютере проверки нет сенсорного экрана.

---

# Кыргызча

- **Жаңы:** интернет жок же сервер жооп бербесе, касса күтпөйт — сатуу дароо кезекке кетет, смена 2–3 секундада ачылат/жабылат; байланыш калыбына келгенде чектер өзү жана бир гана жолу жөнөтүлөт.
- **Жаңы:** NurCRM «Менин баллдарым» тиркемесиндеги кардардын QR коду — кардар дароо чекте; жаңы кардар номери менен кошулат.
- **Оңдолду:** кайтарууда сатып алуучуга берилүүчү так сумма; эки манжа менен тийгенде тизме жылат; Z-отчётто алдын ала төлөм эки жолу эсептелбейт; ботто ЖИни өз ачкычыңыз менен күйгүзсө болот.

---

# English

- **New:** with no internet or no server response the till does not wait — sales go to the queue at once, shifts open/close in 2–3 seconds; when the connection returns, receipts are sent automatically, each exactly once.
- **New:** customer QR from the NurCRM “My points” app — the customer is selected in the receipt at once; a new customer is added with their number.
- **Fixed:** exact amount to give back on returns; two fingers scroll the list; a debt prepayment is no longer counted twice in the Z report; the bot's AI can be turned on with your own key.

---

# Türkçe

- **Yeni:** internet yoksa veya sunucu yanıt vermiyorsa kasa beklemiyor — satışlar hemen kuyruğa gider, vardiya 2–3 saniyede açılıp kapanır; bağlantı dönünce fişler otomatik ve yalnızca bir kez gönderilir.
- **Yeni:** NurCRM «Puanlarım» uygulamasından müşteri QR'ı — müşteri hemen fişte; yeni müşteri numarasıyla eklenir.
- **Düzeltildi:** iadede verilecek tam tutar; iki parmak listeyi kaydırır; Z raporunda borç ön ödemesi iki kez sayılmaz; botun yapay zekâsı kendi anahtarınızla açılabilir.

---

# O'zbekcha

- **Yangi:** internet yo'q yoki server javob bermasa kassa kutmaydi — sotuvlar darhol navbatga ketadi, smena 2–3 soniyada ochiladi/yopiladi; aloqa tiklanganda cheklar avtomatik va faqat bir marta yuboriladi.
- **Yangi:** NurCRM «Mening ballarim» ilovasidan mijoz QR kodi — mijoz darhol chekda; yangi mijoz raqami bilan qo'shiladi.
- **Tuzatildi:** qaytarishda beriladigan aniq summa; ikki barmoq ro'yxatni aylantiradi; Z-hisobotda qarzning oldindan to'lovi ikki marta hisoblanmaydi; botning SIsini o'z kalitingiz bilan yoqish mumkin.
