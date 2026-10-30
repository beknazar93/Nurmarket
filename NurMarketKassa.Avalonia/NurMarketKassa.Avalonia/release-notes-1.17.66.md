## v1.17.66 (тестовая версия)

**Тестовая версия — только для тестировщиков.** Обычные кассы её не получают, пока тестирование не закончено. Тестировщик: «Настройки → Обновления → Тестовые версии», ввести код тестировщика → «Проверить обновления» → «Обновить».

Файлы для установки:

- `NurMarketKassa-win-Setup.exe` — касса (при первом запуске спросит, ставить ли программу владельца).
- Linux — `nurmarket-kassa-linux-x64-1.17.66.tar.gz`; macOS — `nurmarket-kassa-macos-arm64-1.17.66.zip` (Apple Silicon) и `nurmarket-kassa-macos-x64-1.17.66.zip` (Intel); Android — `nurmarket-kassa-android-1.17.66.apk`.

Обновление не меняет ваши настройки.

---

# Русский

### Новое и исправлено

- Закупки получают номера вида ПР-… (нумерация настраивается); в накладной видны наценка и цена реализации
- Новая форма «Накладная прихода» и редактор: правку принятой накладной можно разрешить на 24 часа, 48 часов или всегда (настройка)
- Реестр «Покупки»: все закупки одним списком
- «Оприходовать»: нового поставщика можно создать прямо в окне
- «Провести закупку» работает и после переключения между окнами
- Нур Советник: тумблеры, что ему разрешено, и списки склада в ответах
- Каталог до 15 тысяч товаров: при ответе «слишком часто» (429) загрузка повторяется сама, одна общая загрузка на все окна, быстрый холодный старт; сортировка в Складе
- «Сервер занят» вместо «нет связи»: ответ «слишком часто» (429) больше не включает автономный режим и не сбрасывает смену и «Касса: 0,00» (берётся последнее известное состояние, сверка повторяется после паузы сервера); в шапке нейтральное «Сервер занят, повтор через N с»; оплата, открытие и закрытие смены идут первыми, фоновые запросы ждут паузу; список клиентов в окне оплаты сам повторяется до 3 раз, есть кнопка «Повторить». Z-отчёт смены, закрытой без связи, считается по данным кассы (продажи, возвраты, оплата долга) с пометкой «Данные по кассе без связи; сверка с сервером после отправки»
- Массовая загрузка товаров быстрее: карточки идут по 6–8 запросов сразу (было 4 с остановкой на каждой сотне), скорость сама подстраивается под сервер (при «слишком часто» снижается и ждёт нужную паузу); приход пачкой — по 8 строк сразу вместо одной, без лишнего чтения остатка у только что созданных товаров; в окне «Загружено 1 240 из 15 433 · 38 строк/с · осталось ~6 мин» и кнопка «Пауза»
- Надёжность: «файл занят» (антивирус) больше не стирает историю внесений и изъятий, очередь закрытия смен и бонусов; продажа не задваивается при сбое между отправкой и ответом (ключ пишется на диск до отправки, запись снимается только после сброса чека); «в долг» из офлайна не уходит в чужую смену; возврат «весь чек» после потерянного ответа повторяется тем же ключом; «Требует проверки» можно закрыть; сессия: закрытие смены ждёт свою компанию, очередь копится и досылается после входа; вкладка «Поступления» не шлёт до 100 запросов при каждом открытии; ярлык кассы не двоится после обновления
- Мастер загрузки: надёжность, права закупки — «Остановить» прерывает и приход; повтор того же файла не создаёт дублей, после обрыва есть «Продолжить загрузку»; при ошибках сервера и обрывах связи запросы повторяются; доп. штрихкоды, добавленные на сайте, не стираются; «Цена закупки», наценка и суммы закупки скрыты у ролей без права видеть закупку
- Окна «Тех поддержка», «Некорректные чеки» и все разделы поверх кассы теперь тоже размывают и затемняют фон, как «QR-код чека»; окно кассы всегда целиком в рабочей области экрана (при смене монитора и масштаба), шапка не обрезается — «Закрыть смену» и «Розничный» больше не уходят за край; «Некорректные чеки»: записи по причинам, кнопки «Показать», «Отправить», «Закрыть без отправки» / «Закрыть запись», понятные подсказки вместо красного «Нечего отправлять повторно», «Отправить повторно» активна только когда есть что повторять; плашка «Не отправлено / Отклонено» в шапке открывает этот список; возврат больше положенного — понятное сообщение с суммой, которую разрешает сервер
- Меньше нагрузки на процессор и батарею, установка легче на ~66 МБ (убраны неиспользуемые библиотеки распознавания речи); Android в фоне опрашивает сервер реже
- Массовая загрузка товаров: столбцы подбираются точнее («Цена» — цена продажи, «Закупочная» — закупка; уверенность подбора и подсветка спорных), «Количество» больше не обязательно — без него загружаются только карточки и цены, остаток не меняется; новое поле «Доп. штрихкоды» (слияние без затирания и дублей, проверка на чужие штрихкоды, штрихкод файла из доп. штрихкодов товара — это тот же товар); большие файлы: поиск, фильтры «Предупреждения» и «Объединено», группы ошибок «15 412: укажите закупку → показать», разбор с прогрессом, загрузка пакетами по 100 с остановкой и итогом «Создано, обновлено, пропущено, ошибок»; проверка заново сама после смены столбцов; подсказка «Какой файл подходит» и новый шаблон
- Склад → товар → вкладка «Поступления» (и «⋯ → Поступления и партии» в строке товара): от кого, когда и по какой накладной пришёл товар — дата и время, поставщик (клик открывает его карточку), № прихода, количество, цена закупки, сумма, кто принял, оплата, «остаток партии» (оценка по FIFO) и итоги сверху; поиск по поставщику и номеру, период «Неделя / Месяц / Год / Всё» (по умолчанию «Всё», понятные подсказки, если за период пусто), список выдерживает сотни поступлений, без связи — данные от последней загрузки. «Накладная №…» открывает «Позиции прихода» с выделенной строкой товара; файл загруженной накладной (Excel, CSV, XML) теперь остаётся на этом ПК и открывается кнопкой «Открыть файл». Закупочные цены и поставщиков видят только владелец и администратор, кассиру — дата, количество и остаток партии
- Окна-диалоги всегда спереди: больше не прячутся за главным окном или разделом и не «замораживают» программу; клик по заблокированному окну, Alt-Tab и сворачивание возвращают диалог наверх, а фон за диалогом размывается и слегка затемняется
- Сессия истекла — больше не тупик: вместо «Ошибка закрытия смены: Сессия недействительна…» одно окно «Сессия истекла. Войдите снова» с кнопкой «Войти», в шапке «🔑 Войдите снова». Продажи, смена, корзины и закрытие смены сохраняются и уходят сами после входа; в программе владельца при этом не пропадают права («Недостаточно прав» больше не выходит у владельца)
- Программа теперь называется NurCRM (Нур СРМ): новое название в окнах, меню, чеках, подсказках и справке
- Без интернета теперь можно: продажа «в долг», возврат и оплата долга. Клиент и чек берутся из памяти кассы, операция ждёт в очереди («Ждёт отправки») и уходит на сервер сама, строго после чека; если сервер откажет (лимит долга, клиент удалён) — «Требует проверки» с причиной, ничего не теряется. Настройка «Макс. долг без связи»
- Чек не теряется при пропаже питания или сбое: продажа записывается на диск до отправки на сервер
- «В долг» и смешанная оплата после обрыва связи не создают дубль чека; ключи повтора не теряются при ответах сервера 409, 423 и 425
- Очередь неотправленных чеков не встаёт на одном проблемном чеке; красная плашка «Не отправлено/Отклонено» и оповещение владельцу
- Файлы очередей пишутся атомарно и с копиями; база проверяется при запуске; понятное сообщение, если база заблокирована или диск полон
- Смена, открытая без связи, привязывается к настоящей смене на сервере, когда связь появилась
- Синхронизация: цена и остаток доходят до других касс в магазине за 1–2 с (лента изменений с кодом 304, HTTP/2, умный опрос), подсказки по локальной сети
- Связь с телефоном (PhoneLink): события и подсказки для мобильного приложения
- Штрихкод не пропадает после прихода товара
- Поиск «Asu»: точные совпадения стоят выше в списке
- Массовая загрузка CSV: без падений, определяется кодировка, подсказка при выборе Excel вместо CSV, понятные ошибки по строкам
- ИИ-Советник: накладную можно вставить через Ctrl+V или перетащить в окно
- Linux: печать чеков (/dev/usb, ttyUSB, TCP, CUPS), ярлыки и автозапуск
- macOS: касса для Mac (Apple Silicon и Intel) в архиве zip
- При смене компании на кассе стирается всё от прежней: ключи ИИ, платные функции и темы, база, чеки, бот, принтеры; неотправленные продажи не теряются молча — вернуться в прежний аккаунт, выгрузить в файл или стереть с подтверждением
- Программа владельца: пояснения в отчётах «Бонусы», «Зарплата», «Инвестиции», подпись периода
- Чеки со статусом «Ошибка отправки» уходят сами; на плашке — причина и кнопка «Повторить отправку»
- При обрывах связи касса сама переходит на HTTP/1.1
- Автоповтор «Ошибки отправки» — только для свежих чеков, старые ждут решения кассира
- Ключ оплаты записывается на диск до отправки, а после сбоя касса сверяется с сервером — двойной продажи нет
- Чек, начатый без связи: цены сверяются с сервером перед оплатой
- Файлы кассы защищены от ошибки «занят другой программой»
- Внесения и выдачи денег привязываются к нужной смене
- Понятный текст ошибки при приходе товара и плашка «ошибка: N» в шапке
- Ревизия: закупочная цена, сумма разницы, итоги по деньгам, топ-10 недостач
- Меньше нагрузки на процессор и батарею: в фоне касса опрашивает сервер в несколько раз реже; приложение легче на ~26 МБ
- Диалог всегда спереди, фон за ним размывается и затемняется; окно не пропадает под слоем кассы и не блокирует касания
- Android: настройки и массовая загрузка без ошибок, ровные отступы и кнопка «⋯», предпросмотр штрихкодов

# Кыргызча

### Жаңы жана оңдолгон

- Сатып алуулар ПР-… түрүндөгү номер алат (номерлөө жөндөлөт); накладнойдо үстөк жана сатуу баасы көрүнөт
- Жаңы «Кирүү накладнойу» формасы жана редактору: кабыл алынган накладнойду оңдоого 24 саатка, 48 саатка же ар дайым уруксат берсе болот (жөндөө)
- «Сатып алуулар» реестри: бардык сатып алуулар бир тизмеде
- «Кирешелөө»: жаңы жеткирүүчүнү түз эле терезеде түзсө болот
- «Сатып алууну өткөрүү» терезелерди которгондон кийин да иштейт
- Нур Кеңешчи: эмнеге уруксат берилгенин көрсөткөн өчүргүчтөр жана жооптордо кампа тизмелери
- 15 миң товарга чейинки каталог: «өтө тез-тез» (429) жообунда жүктөө өзү кайталанат, бардык терезелерге бир жалпы жүктөө, тез муздак старт; Кампада иргөө
- «Байланыш жок» ордуна «Сервер бош эмес»: «өтө тез-тез» (429) жообу автономдуу режимди күйгүзбөйт, сменаны жана «Касса: 0,00» калпына келтирбейт (акыркы белгилүү абал алынат, сервер тыныгуусунан кийин салыштыруу кайталанат); баш жакта бейтарап «Сервер бош эмес, N с кийин кайталанат»; төлөм, сменаны ачуу жана жабуу биринчи өтөт, фондук суроолор тыныгууну күтөт; төлөм терезесинде кардарлардын тизмеси өзү 3 жолуга чейин кайталанат, «Кайра аракет» баскычы бар. Байланышсыз жабылган сменанын Z-отчёту касса маалыматы боюнча (сатуулар, кайтаруулар, карыз төлөмү) «Байланышсыз касса маалыматы; жөнөткөндөн кийин сервер менен салыштырылат» белгиси менен эсептелет
- Товарларды жаппай жүктөө тезирээк: карточкалар бир убакта 6–8 суроо менен кетет (мурун 4 болчу, ар жүзүндө токтоп), ылдамдык серверге өзү ыңгайлашат («өтө тез-тез» болсо төмөндөйт жана керектүү тыным күтөт); киреше пачка менен — бир саптын ордуна 8 сап бир убакта, жаңы түзүлгөн товарлардын калдыгын ашыкча окуусуз; терезеде «Жүктөлдү 1 240 / 15 433 · 38 сап/сек · калды ~6 мүн» жана «Тыныгуу» баскычы
- Ишенимдүүлүк: «файл бош эмес» (антивирус) акча салуулар менен алып коюулардын тарыхын, смена жабуу жана бонус кезектерин өчүрбөйт; сатуу жөнөтүү менен жооптун ортосундагы үзгүлтүктө эки эсе өтпөйт (ачкыч жөнөтүүдөн мурун дискке жазылат, чек тазалангандан кийин гана алынат); офлайндагы «карызга» сатуу бөтөн сменага кетпейт; жоголгон жооптон кийин «бүтүн чек» кайтаруу ошол эле ачкыч менен кайталанат; «Текшерүү керек» жабылат; сессия: смена жабуу өз компаниясын күтөт, кезек топтолуп, кирген соң жөнөтүлөт; «Кирүүлөр» өтмөгү ар ачканда 100гө чейин сурам жөнөтпөйт; жаңыртуудан кийин касса ярлыгы эки эсе болбойт
- Жүктөө устасы: ишенимдүүлүк, сатып алуу укугу — «Токтотуу» кирешени да токтотот; ошол эле файлды кайра жүктөгөндө кайталанбайт, үзгүлтүктөн кийин «Жүктөөнү улантуу» бар; сервер каталарында жана байланыш үзүлгөндө суроолор кайталанат; сайтта кошулган кошумча штрих-коддор өчүрүлбөйт; «Сатып алуу баасы», үстөк жана сатып алуу суммасы сатып алууну көрүү укугу жок ролдордо жашырылган
- «Тех колдоо», «Туура эмес чектер» терезелери жана кассанын үстүндөгү бардык бөлүмдөр да фонду бүдөмүктөтүп, караңгылатат («Чектин QR-коду» сыяктуу); касса терезеси ар дайым экрандын жумуш аймагына толугу менен батат (монитор же масштаб өзгөргөндө), баш бөлүк кесилбейт — «Сменаны жабуу» жана «Чекене» экрандын четинен чыкпайт; «Туура эмес чектер»: жазуулар себеби боюнча, «Көрсөтүү», «Жөнөтүү», «Жөнөтпөй жабуу» / «Жазууну жабуу» баскычтары, кызыл «Кайра жөнөтүүгө эч нерсе жок» ордуна түшүнүктүү кеңеш, «Кайра жөнөтүү» кайталоого нерсе болгондо гана иштейт; баш бөлүктөгү «Жөнөтүлгөн жок / Четке кагылды» белгиси ушул тизмени ачат; тийиштүүдөн көп кайтаруу — сервер уруксат берген сумма менен түшүнүктүү билдирүү
- Процессорго жана батареяга жүк азайды, орнотуу ~66 МБ жеңилирээк (колдонулбаган үн таануу китепканалары алынды); Android фондо серверден сейрек сурайт
- Товарларды жаппай жүктөө: мамычалар так аныкталат («Баа» — сатуу баасы, «Закупочная» — сатып алуу баасы; ишенимдүүлүк жана талаштуулардын белгиси), «Саны» милдеттүү эмес — анысыз карточкалар жана баалар гана жүктөлөт, калдык өзгөрбөйт; жаңы талаа «Кошумча штрих-коддор» (өчүрүүсүз жана кайталоосуз кошуу, бөтөн штрих-кодду текшерүү); чоң файлдар: издөө, «Эскертүүлөр» жана «Бириктирилди» чыпкалары, каталар топтору, прогресс менен талдоо, 100дөн пакеттер менен жүктөө жана токтотуу; мамычаларды өзгөрткөндөн кийин өзү кайра текшерет; «Кандай файл ылайыктуу» кеңеши жана жаңы үлгү
- Склад → товар → «Кирүүлөр» өтмөгү (жана товардын сабындагы «⋯ → Кирүүлөр жана партиялар»): товар кимден, качан жана кайсы накладной менен келди — күнү жана убактысы, жеткирүүчү (басканда анын картасы ачылат), кирүү №, саны, сатып алуу баасы, суммасы, ким кабыл алды, төлөм, «партиянын калдыгы» (FIFO боюнча болжол) жана жогорудагы жыйынтыктар; жеткирүүчү жана номер боюнча издөө, «Апта / Ай / Жыл / Баары» мезгили, тизме жүздөгөн кирүүнү көтөрөт, байланышсыз — акыркы жүктөөнүн маалыматы. «Накладной №…» товардын сабы белгиленген «Кирүү позицияларын» ачат; жүктөлгөн накладнойдун файлы (Excel, CSV, XML) эми ушул ПКда калат жана «Файлды ачуу» баскычы менен ачылат. Сатып алуу баасын жана жеткирүүчүлөрдү ээси менен администратор гана көрөт, кассирге — күнү, саны жана партиянын калдыгы
- Диалог терезелери дайыма алдыңкы планда: башкы терезенин же бөлүмдүн артына жашынбайт, программаны «тоңдурбайт»; бөгөттөлгөн терезени басуу, Alt-Tab жана жыйыштыруу диалогду кайра өйдө алып чыгат, диалогдун арткы фону бүдөмүк жана бир аз караңгы болот
- Сессиянын мөөнөтү бүтүшү — туюк эмес: «Сменаны жабууда ката: Сессия жараксыз…» ордуна бир терезе «Сессиянын мөөнөтү бүттү. Кайра кириңиз» жана «Кирүү» баскычы, башкы тилкеде «🔑 Кайра кириңиз». Сатуулар, смена, себеттер жана сменаны жабуу сакталат да, киргенден кийин өзү кетет; ээсинин программасында укуктар жоголбойт («Укук жетишсиз» ээсине чыкпайт)
- Программа эми NurCRM (Нур СРМ) деп аталат: жаңы ат терезелерде, менюда, чектерде, кеңештерде жана жардамда
- Интернетсиз эми болот: «карызга» сатуу, кайтаруу жана карыз төлөмү. Кардар жана чек кассанын эсинен алынат, операция кезекте («Жөнөтүүнү күтөт») турат жана чектен кийин серверге өзү кетет; сервер баш тартса (карыз лимити, кардар өчүрүлгөн) — себеби менен «Текшерүү керек», эч нерсе жоголбойт. «Байланышсыз макс. карыз» жөндөөсү
- Чек электр өчүп калганда же сбойдо жоголбойт: сатуу серверге жөнөтүлгөнгө чейин дискке жазылат
- «Карызга» жана аралаш төлөм байланыш үзүлгөндөн кийин чектин кайталанышын жаратпайт; кайталоо ачкычтары сервердин 409, 423 жана 425 жоопторунда жоголбойт
- Жөнөтүлбөгөн чектер кезеги бир көйгөйлүү чекте токтоп калбайт; кызыл тилке «Жөнөтүлгөн жок/Четке кагылды» жана ээсине билдирүү
- Кезек файлдары атомдук түрдө жана көчүрмөлөрү менен жазылат; маалымат базасы ишке киргизүүдө текшерилет; база бөгөттөлгөндө же диск толгондо түшүнүктүү билдирүү
- Байланышсыз ачылган смена байланыш пайда болгондо сервердеги чыныгы сменага байланыштырылат
- Синхрондоо: баа жана калдык дүкөндөгү башка кассаларга 1–2 с ичинде жетет (304 кодулуу өзгөрүүлөр лентасы, HTTP/2, акылдуу сурамжылоо), жергиликүү тармак боюнча кеңештер
- Телефон менен байланыш (PhoneLink): мобилдик тиркеме үчүн окуялар жана кеңештер
- Штрихкод товар кирип келгенден кийин жоголбойт
- «Asu» издөө: так дал келүүлөр тизмеде жогору турат
- CSV массалык жүктөө: кулабайт, коддоо аныкталат, CSV ордуна Excel тандалса кеңеш, саптар боюнча түшүнүктүү каталар
- ЖИ-Кеңешчи: накладнойду Ctrl+V менен коюуга же терезеге сүйрөп таштоого болот
- Linux: чек басып чыгаруу (/dev/usb, ttyUSB, TCP, CUPS), ярлыктар жана автоиштетүү
- macOS: Mac үчүн касса (Apple Silicon жана Intel) zip архивинде
- Компания алмашканда мурункунун баары өчүрүлөт: ИИ ачкычтары, акы төлөнүүчү функциялар жана темалар, база, чектер, бот, принтерлер; жөнөтүлбөгөн сатуулар унчукпай жоголбойт — мурунку аккаунтка кайтуу, файлга сактоо же ырастоо менен өчүрүү
- Ээсинин программасы: «Бонустар», «Эмгек акы», «Инвестициялар» отчетторунда түшүндүрмөлөр, мезгилдин жазуусу
- «Жөнөтүүдө ката» статусундагы чектер өзү кетет; тилкеде — себеби жана «Кайра жөнөтүү» баскычы
- Байланыш үзүлгөндө касса өзү HTTP/1.1ге өтөт
- «Жөнөтүүдө ката» автотекшерүүсү — жаңы чектер үчүн гана, эскилери кассирдин чечимин күтөт
- Төлөм ачкычы жөнөтүүдөн мурун дискке жазылат, сбойдон кийин касса сервер менен салыштырат — кош сатуу болбойт
- Байланышсыз башталган чек: төлөөдөн мурун баалар сервер менен салыштырылат
- Касса файлдары «башка программа ээлеп турат» катасынан корголгон
- Акча киргизүү жана чыгаруу керектүү сменага байланыштырылат
- Товар кабыл алууда түшүнүктүү ката тексти жана башындагы «ката: N» тилкеси
- Ревизия: сатып алуу баасы, айырманын суммасы, акча боюнча жыйынтыктар, эң көп жетишпегендердин топ-10
- Процессорго жана батареяга жүк азайды: фондо касса серверден бир нече эсе сейрек сурайт; тиркеме ~26 МБ жеңилирээк
- Диалог дайыма алдыда, анын арткы фону бүдөмүк жана караңгыланат; терезе кассанын катмарынын астына түшпөйт, тийүүнү бөгөттөбөйт
- Android: жөндөөлөр жана массалык жүктөө катасыз, түз боштуктар жана «⋯» баскычы, штрихкоддорду алдын ала көрүү

# English

### New and fixed

- Purchases get PR-… numbers (numbering is configurable); the invoice shows markup and retail price
- New “Receiving invoice” form and editor: editing an accepted invoice can be allowed for 24 hours, 48 hours or always (setting)
- “Purchases” register: all purchases in one list
- “Receive goods”: a new supplier can be created right in the window
- “Post purchase” also works after switching between windows
- Nur Advisor: toggles for what it is allowed to do, and stock lists in its answers
- Catalog of up to 15,000 products: on a “too many requests” (429) reply the load retries by itself, one shared load for all windows, fast cold start; sorting in Stock
- “Server busy” instead of “no connection”: a “too many requests” reply (429) no longer switches the till to autonomous mode or resets the shift and “Till: 0.00” (the last known state is kept, the check is repeated after the server's pause); the header shows a neutral “Server busy, retrying in N s”; payment and opening/closing a shift go first, background requests wait for the pause; the client list in the payment window retries itself up to 3 times and has a “Retry” button. The Z-report of a shift closed without a connection is built from the till's own data (sales, returns, debt payments) with the note “Till data without a connection; reconciled with the server after sending”
- Bulk product import is faster: cards go 6–8 requests at a time (was 4, pausing every hundred), the speed adapts to the server by itself (on “too many requests” it slows down and waits the asked pause); a batch receipt handles 8 rows at once instead of one, without a needless stock read for just-created products; the window shows “Loaded 1,240 of 15,433 · 38 rows/s · ~6 min left” and a “Pause” button
- Reliability: a busy file (antivirus) no longer wipes the cash in/out history or the shift-close and bonus queues; a sale is not doubled by a failure between sending and the reply (the key is written to disk before sending and removed only after the receipt is cleared); an offline credit sale never lands in someone else's shift; a whole-receipt return is retried with the same key after a lost reply; “Needs review” items can be closed; session: a deferred shift close waits for its own company, queues keep filling and are sent after sign-in; the “Receipts” tab no longer sends up to 100 requests on every open; the till shortcut is not duplicated after an update
- Import wizard: reliability and purchase-cost rights — “Stop” also interrupts the receipt; loading the same file again creates no duplicates and there is “Continue loading” after a break; requests are retried on server errors and connection drops; extra barcodes added on the website are no longer erased; “Purchase price”, markup and purchase totals are hidden from roles without the right to see purchase costs
- The “Tech support” and “Irregular receipts” windows and every section opened over the till now also blur and dim the background, like “Receipt QR code”; the till window always fits inside the screen's working area (also after a monitor or scale change) and the header no longer gets cut off — “Close shift” and “Retail” no longer run past the edge; “Irregular receipts”: entries grouped by reason, buttons “Show”, “Send”, “Close without sending” / “Close entry”, clear hints instead of the red “Nothing to resend”, and “Resend” is active only when there is something to resend; the “Not sent / Rejected” badge in the header opens this list; a return above the due amount shows a clear message with the amount the server allows
- Lower CPU and battery load, the install is ~66 MB lighter (unused speech-recognition libraries removed); Android polls the server less often in the background
- Bulk product import: columns are detected more accurately (“Price” = sale price, “Purchase” = purchase price; confidence shown and doubtful ones highlighted); “Quantity” is no longer required — without it only cards and prices are loaded and the stock does not change; new “Extra barcodes” field (merged without overwriting or duplicates, checked against other products’ barcodes, a file barcode that is an extra barcode of a product means that same product); big files: search, “Warnings” and “Merged” filters, error groups (“15,412: enter the purchase price → show”), parsing with progress, loading in batches of 100 with a stop button and a summary “Created, updated, skipped, errors”; automatic re-check after changing columns; “Which file fits” hint and a new template
- Warehouse → product → the “Receipts” tab (and “⋯ → Receipts and batches” in the product row): who the product came from, when and on which invoice — date and time, supplier (click opens the supplier card), receipt no., quantity, purchase price, amount, who received it, payment, “batch left” (a FIFO estimate) and totals on top; search by supplier and number, period “Week / Month / Year / All”, a list that handles hundreds of receipts, and the data from the last load when offline. “Invoice no. …” opens “Delivery items” with the product line highlighted; the file of a loaded invoice (Excel, CSV, XML) now stays on this PC and opens with “Open file”. Purchase prices and suppliers are shown to the owner and administrator only; the cashier sees the date, quantity and batch left
- Dialog windows are always in front: they no longer hide behind the main window or a section and no longer freeze the program; clicking the blocked window, Alt-Tab and minimising bring the dialog back on top, and the background behind it is blurred and slightly dimmed
- An expired session is no longer a dead end: instead of “Error closing the shift: Session is invalid…” you get one window “Session expired. Sign in again” with a “Sign in” button, and “🔑 Sign in again” in the header. Sales, the shift, carts and the shift closing are kept and go out by themselves after sign-in; in the owner program permissions no longer disappear (“Not enough permissions” no longer appears for the owner)
- The program is now called NurCRM (Nur CRM): the new name in windows, menus, receipts, hints and help
- Without the internet you can now: sell on credit, make returns and take debt payments. The client and receipt come from the till’s memory, the operation waits in the queue (“Waiting to be sent”) and goes to the server by itself, strictly after the receipt; if the server refuses (debt limit, client deleted) — “Needs review” with the reason, nothing is lost. New setting “Max debt without a connection”
- A receipt is no longer lost on a power cut or crash: the sale is written to disk before it is sent to the server
- “On credit” and mixed payment after a connection drop no longer create a duplicate receipt; retry keys are not lost on server replies 409, 423 and 425
- The queue of unsent receipts no longer gets stuck on one problem receipt; red “Not sent/Rejected” badge and a notice to the owner
- Queue files are written atomically and with backups; the database is checked at startup; a clear message when the database is locked or the disk is full
- A shift opened offline is linked to the real server shift once the connection is back
- Sync: price and stock reach other tills in the shop within 1–2 s (change feed with 304, HTTP/2, smart polling), local network hints
- Phone link (PhoneLink): events and hints for the mobile app
- The barcode no longer disappears after receiving goods
- “Asu” search: exact matches rank higher in the list
- Bulk CSV import: no crashes, encoding detection, a hint when Excel is chosen instead of CSV, clear per-row errors
- AI Advisor: paste an invoice with Ctrl+V or drag it into the window
- Linux: receipt printing (/dev/usb, ttyUSB, TCP, CUPS), shortcuts and autostart
- macOS: the till for Mac (Apple Silicon and Intel) as a zip archive
- When the company changes, everything from the previous one is erased: AI keys, paid features and themes, database, receipts, bot, printers; unsent sales are never lost silently — go back to the previous account, save to a file or erase with confirmation
- Owner program: explanations in the Bonuses, Salary and Investments reports, period caption
- Receipts with the “Send error” status are sent automatically; the badge shows the reason and a “Retry sending” button
- On connection drops the till switches to HTTP/1.1 by itself
- Auto-retry of “Send error” receipts applies only to fresh receipts; older ones wait for the cashier’s decision
- The payment key is written to disk before sending and reconciled with the server after a failure — no double sale
- A receipt started offline: prices are checked against the server before payment
- Till files are protected from the “in use by another program” error
- Cash deposits and withdrawals are linked to the correct shift
- Clear error text when receiving goods and an “error: N” badge in the header
- Stocktaking: purchase price, difference amount, money totals, top 10 shortages
- Lower CPU and battery load: in the background the app polls the server several times less often; the app is ~26 MB lighter
- The dialog is always in front and the background behind it is blurred and dimmed; the window no longer slips under the till layer or blocks taps
- Android: settings and bulk import without errors, even padding and the “⋯” button, barcode preview

# Türkçe

### Yeni ve düzeltilen

- Alımlar ПР-… biçiminde numara alır (numaralandırma ayarlanabilir); faturada kâr oranı ve satış fiyatı görünür
- Yeni «Giriş faturası» formu ve düzenleyici: kabul edilmiş faturanın düzenlenmesine 24 saat, 48 saat veya her zaman izin verilebilir (ayar)
- «Alımlar» kaydı: tüm alımlar tek listede
- «Mal girişi»: yeni tedarikçi doğrudan pencerede oluşturulabilir
- «Alımı işle» pencereler arasında geçişten sonra da çalışır
- Nur Danışman: izinleri gösteren anahtarlar ve yanıtlarda depo listeleri
- 15 bin ürüne kadar katalog: «çok sık» (429) yanıtında yükleme kendiliğinden tekrarlanır, tüm pencereler için tek ortak yükleme, hızlı soğuk başlangıç; Depoda sıralama
- «Bağlantı yok» yerine «Sunucu meşgul»: «çok sık» yanıtı (429) artık otonom modu açmaz, vardiyayı ve «Kasa: 0,00» değerini sıfırlamaz (son bilinen durum korunur, sunucunun beklemesinden sonra kontrol tekrarlanır); üst çubukta nötr «Sunucu meşgul, N sn sonra tekrar»; ödeme, vardiya açma ve kapatma önce gider, arka plan istekleri duraklamayı bekler; ödeme penceresinde müşteri listesi kendiliğinden 3 kereye kadar tekrarlanır ve «Tekrar dene» düğmesi vardır. Bağlantısız kapatılan vardiyanın Z raporu kasanın kendi verilerinden (satışlar, iadeler, borç ödemeleri) «Bağlantısız kasa verileri; gönderimden sonra sunucuyla karşılaştırılır» notuyla hazırlanır
- Toplu ürün yükleme daha hızlı: kartlar aynı anda 6–8 istekle gider (eskiden 4, her yüzde duruyordu), hız sunucuya göre kendiliğinden ayarlanır («çok sık» denirse yavaşlar ve istenen süre bekler); toplu mal kabulü tek satır yerine 8 satırı aynı anda işler, yeni oluşturulan ürünlerde gereksiz stok okuması yoktur; pencerede «Yüklendi 1 240 / 15 433 · 38 satır/sn · ~6 dk kaldı» ve «Duraklat» düğmesi var
- Güvenilirlik: «dosya meşgul» (antivirüs) artık para giriş/çıkış geçmişini, vardiya kapatma ve bonus kuyruklarını silmez; gönderim ile yanıt arasındaki arızada satış iki kez işlenmez (anahtar göndermeden önce diske yazılır, fiş temizlendikten sonra silinir); çevrimdışı veresiye satış başkasının vardiyasına düşmez; yanıt kaybolunca «tüm fiş» iadesi aynı anahtarla tekrarlanır; «Kontrol gerekli» kayıtları kapatılabilir; oturum: ertelenmiş vardiya kapatma kendi şirketini bekler, kuyruklar birikir ve girişten sonra gönderilir; «Girişler» sekmesi her açılışta 100 isteğe kadar göndermez; güncellemeden sonra kasa kısayolu ikilenmez
- Yükleme sihirbazı: güvenilirlik ve alış hakları — «Durdur» mal kabulü de keser; aynı dosyayı yeniden yüklemek kopya oluşturmaz, kesintiden sonra «Yüklemeye devam et» vardır; sunucu hatalarında ve bağlantı kopmalarında istekler yinelenir; sitede eklenen ek barkodlar silinmez; «Alış fiyatı», kâr oranı ve alış tutarları alışı görme hakkı olmayan rollerde gizlidir
- “Teknik destek” ve “Hatalı fişler” pencereleri ile kasanın üstünde açılan tüm bölümler artık “Fiş QR kodu” gibi arka planı bulanıklaştırır ve karartır; kasa penceresi (monitör veya ölçek değiştiğinde de) her zaman ekranın çalışma alanına sığar, üst çubuk kesilmez — “Vardiyayı kapat” ve “Perakende” artık kenardan taşmaz; “Hatalı fişler”: kayıtlar nedene göre gruplanır, “Göster”, “Gönder”, “Göndermeden kapat” / “Kaydı kapat” düğmeleri, kırmızı “Yeniden gönderilecek bir şey yok” yerine anlaşılır ipuçları, “Yeniden gönder” yalnızca gönderilecek bir şey varken etkin; üst çubuktaki “Gönderilmedi / Reddedildi” rozeti bu listeyi açar; gerekenden fazla iadede sunucunun izin verdiği tutarla anlaşılır mesaj
- İşlemci ve pil yükü azaldı, kurulum ~66 MB hafifledi (kullanılmayan konuşma tanıma kütüphaneleri kaldırıldı); Android arka planda sunucuyu daha seyrek sorgular
- Toplu ürün yükleme: sütunlar daha doğru belirlenir («Fiyat» — satış fiyatı, «Alış» — alış fiyatı; güven oranı ve şüpheli olanların vurgusu), «Miktar» artık zorunlu değil — olmadan yalnızca kart ve fiyatlar yüklenir, stok değişmez; yeni «Ek barkodlar» alanı (silmeden ve tekrarsız birleştirme, başka ürünlerin barkod kontrolü); büyük dosyalar: arama, «Uyarılar» ve «Birleştirilen» filtreleri, hata grupları, ilerlemeli ayrıştırma, 100’lük paketlerle yükleme ve durdurma; sütunlar değişince otomatik yeniden kontrol; «Hangi dosya uygun» ipucu ve yeni şablon
- Depo → ürün → «Girişler» sekmesi (ve ürün satırında «⋯ → Girişler ve partiler»): ürün kimden, ne zaman ve hangi faturayla geldi — tarih ve saat, tedarikçi (tıklayınca kartı açılır), giriş no., miktar, alış fiyatı, tutar, kimin teslim aldığı, ödeme, «parti kalanı» (FIFO tahmini) ve üstte toplamlar; tedarikçi ve numaraya göre arama, «Hafta / Ay / Yıl / Tümü» dönemi, yüzlerce girişi kaldıran liste, bağlantı yokken son yüklemenin verileri. «Fatura no. …» ürün satırı vurgulanmış «Giriş kalemleri»ni açar; yüklenen faturanın dosyası (Excel, CSV, XML) artık bu bilgisayarda kalır ve «Dosyayı aç» ile açılır. Alış fiyatlarını ve tedarikçileri yalnızca sahip ve yönetici görür, kasiyer tarih, miktar ve parti kalanını görür
- Diyalog pencereleri her zaman önde: artık ana pencerenin veya bölümün arkasına saklanmıyor, programı kilitlemiyor; kilitli pencereye tıklama, Alt-Tab ve küçültme diyaloğu tekrar öne getirir, arkadaki zemin bulanıklaştırılır ve hafifçe karartılır
- Süresi dolan oturum artık çıkmaz değil: «Vardiya kapatma hatası: Oturum geçersiz…» yerine tek pencere «Oturum süresi doldu. Yeniden giriş yapın» ve «Giriş yap» düğmesi, üst çubukta «🔑 Yeniden giriş yapın». Satışlar, vardiya, sepetler ve vardiya kapatma korunur, girişten sonra kendiliğinden gider; sahip programında yetkiler kaybolmaz (sahibe «Yetkiniz yetersiz» çıkmaz)
- Program artık NurCRM (Nur CRM) olarak adlandırılıyor: yeni ad pencerelerde, menülerde, fişlerde, ipuçlarında ve yardımda
- İnternetsiz artık mümkün: veresiye satış, iade ve borç ödemesi. Müşteri ve fiş kasanın hafızasından alınır, işlem kuyrukta bekler («Gönderilmeyi bekliyor») ve fişten sonra sunucuya kendiliğinden gider; sunucu reddederse (borç limiti, müşteri silinmiş) — nedeniyle «Kontrol gerekli», hiçbir şey kaybolmaz. Yeni ayar «Bağlantısız maks. borç»
- Elektrik kesintisi veya çökme durumunda fiş kaybolmaz: satış sunucuya gönderilmeden önce diske yazılır
- Bağlantı koptuktan sonra «Veresiye» ve karma ödeme çift fiş oluşturmaz; tekrar anahtarları sunucunun 409, 423 ve 425 yanıtlarında kaybolmaz
- Gönderilmemiş fiş kuyruğu tek bir sorunlu fişte takılmaz; kırmızı «Gönderilmedi/Reddedildi» şeridi ve sahibine bildirim
- Kuyruk dosyaları atomik olarak ve yedekli yazılır; veritabanı açılışta kontrol edilir; veritabanı kilitliyse veya disk doluysa anlaşılır mesaj
- Bağlantısız açılan vardiya, bağlantı gelince sunucudaki gerçek vardiyaya bağlanır
- Eşitleme: fiyat ve stok mağazadaki diğer kasalara 1–2 sn içinde ulaşır (304'lü değişiklik akışı, HTTP/2, akıllı sorgulama), yerel ağ ipuçları
- Telefon bağlantısı (PhoneLink): mobil uygulama için olaylar ve ipuçları
- Barkod mal kabulünden sonra kaybolmaz
- «Asu» araması: tam eşleşmeler listede üstte yer alır
- Toplu CSV yükleme: çökmez, kodlama algılanır, CSV yerine Excel seçilirse ipucu, satır bazında anlaşılır hatalar
- YZ Danışman: irsaliye Ctrl+V ile yapıştırılabilir veya pencereye sürüklenebilir
- Linux: fiş yazdırma (/dev/usb, ttyUSB, TCP, CUPS), kısayollar ve otomatik başlatma
- macOS: Mac için kasa (Apple Silicon ve Intel) zip arşivinde
- Şirket değiştiğinde öncekinden kalan her şey silinir: yapay zekâ anahtarları, ücretli özellikler ve temalar, veritabanı, fişler, bot, yazıcılar; gönderilmemiş satışlar sessizce kaybolmaz — önceki hesaba dön, dosyaya kaydet veya onayla sil
- Sahip programı: «Bonus», «Maaş», «Yatırımlar» raporlarında açıklamalar, dönem etiketi
- «Gönderim hatası» durumundaki fişler kendiliğinden gider; şeritte neden ve «Tekrar gönder» düğmesi var
- Bağlantı koptuğunda kasa kendiliğinden HTTP/1.1'e geçer
- «Gönderim hatası» otomatik tekrarı yalnızca yeni fişler için; eskiler kasiyerin kararını bekler
- Ödeme anahtarı gönderilmeden önce diske yazılır, arızadan sonra sunucuyla karşılaştırılır — çift satış olmaz
- Bağlantısız başlanan fiş: ödemeden önce fiyatlar sunucuyla karşılaştırılır
- Kasa dosyaları «başka program kullanıyor» hatasına karşı korunur
- Kasa girişleri ve çıkışları doğru vardiyaya bağlanır
- Mal kabulde anlaşılır hata metni ve başlıkta «hata: N» şeridi
- Sayım: alış fiyatı, fark tutarı, para toplamları, en çok eksik 10 ürün
- İşlemci ve pil yükü azaldı: arka planda kasa sunucuyu birkaç kat daha seyrek sorgular; uygulama ~26 MB hafifledi
- Diyalog her zaman önde, arkasındaki zemin bulanıklaşır ve kararır; pencere kasa katmanının altına düşmez, dokunmayı engellemez
- Android: ayarlar ve toplu yükleme hatasız, düzgün boşluklar ve «⋯» düğmesi, barkod önizlemesi

# O'zbekcha

### Yangi va tuzatilgan

- Xaridlar ПР-… ko'rinishidagi raqam oladi (raqamlash sozlanadi); nakladnoyda ustama va sotuv narxi ko'rinadi
- Yangi «Kirim nakladnoyi» shakli va tahrirlagich: qabul qilingan nakladnoyni tahrirlashga 24 soat, 48 soat yoki doim ruxsat berish mumkin (sozlama)
- «Xaridlar» reyestri: barcha xaridlar bitta ro'yxatda
- «Kirim qilish»: yangi yetkazib beruvchini to'g'ridan-to'g'ri oynada yaratish mumkin
- «Xaridni o'tkazish» oynalar almashgandan keyin ham ishlaydi
- Nur Maslahatchi: nimaga ruxsat borligini ko'rsatuvchi tumblerlar va javoblarda ombor ro'yxatlari
- 15 ming tovargacha katalog: «juda tez-tez» (429) javobida yuklash o'zi takrorlanadi, barcha oynalar uchun bitta umumiy yuklash, tez sovuq start; Omborda saralash
- «Aloqa yo'q» o'rniga «Server band»: «juda tez-tez» (429) javobi endi avtonom rejimni yoqmaydi, smena va «Kassa: 0,00» ni tashlamaydi (oxirgi ma'lum holat saqlanadi, server pauzasidan keyin tekshiruv takrorlanadi); sarlavhada betaraf «Server band, N s dan keyin qayta uriniladi»; to'lov, smenani ochish va yopish birinchi o'tadi, fon so'rovlari pauzani kutadi; to'lov oynasida mijozlar ro'yxati o'zi 3 martagacha takrorlanadi va «Qayta urinish» tugmasi bor. Aloqasiz yopilgan smenaning Z-hisoboti kassaning o'z ma'lumotlaridan (sotuvlar, qaytarishlar, qarz to'lovlari) «Aloqasiz kassa ma'lumotlari; yuborilgandan keyin server bilan solishtiriladi» belgisi bilan tuziladi
- Mahsulotlarni ommaviy yuklash tezroq: kartochkalar bir vaqtda 6–8 so'rov bilan ketadi (avval 4 edi, har yuztada to'xtardi), tezlik serverga o'zi moslashadi («juda tez-tez» desa sekinlashadi va so'ralgan pauzani kutadi); kirimni paket bilan o'tkazish bitta qator o'rniga 8 qatorni bir vaqtda bajaradi, yangi yaratilgan mahsulotlarda ortiqcha qoldiq o'qish yo'q; oynada «Yuklandi 1 240 / 15 433 · 38 qator/s · ~6 daq qoldi» va «Pauza» tugmasi bor
- Ishonchlilik: «fayl band» (antivirus) kirim va chiqimlar tarixini, smenani yopish va bonus navbatlarini endi o'chirmaydi; yuborish va javob orasidagi nosozlikda sotuv ikki marta o'tmaydi (kalit yuborishdan oldin diskka yoziladi, chek tozalangandan keyingina olinadi); oflayndagi «qarzga» sotuv begona smenaga tushmaydi; javob yo'qolgandan keyin «butun chek» qaytarish o'sha kalit bilan takrorlanadi; «Tekshirish kerak» yozuvlarini yopish mumkin; sessiya: kechiktirilgan smena yopish o'z kompaniyasini kutadi, navbatlar to'planadi va kirgandan keyin yuboriladi; «Kirimlar» yorlig'i har ochilganda 100 tagacha so'rov yubormaydi; yangilanishdan keyin kassa yorlig'i ikkilanmaydi
- Yuklash ustasi: ishonchlilik va xarid huquqlari — «To'xtatish» kirimni ham to'xtatadi; o'sha faylni qayta yuklash takrorlarni yaratmaydi, uzilishdan keyin «Yuklashni davom ettirish» bor; server xatolari va aloqa uzilganda so'rovlar takrorlanadi; saytda qo'shilgan qo'shimcha shtrix-kodlar o'chmaydi; «Xarid narxi», ustama va xarid summalari xaridni ko'rish huquqi yo'q rollarda yashirilgan
- «Texnik yordam» va «Noto'g'ri cheklar» oynalari hamda kassa ustida ochiladigan barcha bo'limlar endi «Chek QR-kodi» kabi orqa fonni xiralashtiradi va qoraytiradi; kassa oynasi (monitor yoki masshtab o'zgarganda ham) doim ekranning ishchi maydoniga to'liq sig'adi, sarlavha kesilmaydi — «Smenani yopish» va «Chakana» chetdan chiqib ketmaydi; «Noto'g'ri cheklar»: yozuvlar sabab bo'yicha guruhlangan, «Ko'rsatish», «Yuborish», «Yubormasdan yopish» / «Yozuvni yopish» tugmalari, qizil «Qayta yuboriladigan narsa yo'q» o'rniga tushunarli maslahat, «Qayta yuborish» faqat yuboriladigan narsa bo'lganda faol; sarlavhadagi «Yuborilmadi / Rad etildi» belgisi shu ro'yxatni ochadi; keragidan ortiq qaytarishda server ruxsat bergan summa bilan tushunarli xabar
- Protsessor va batareyaga yuk kamaydi, o'rnatish ~66 MB yengilroq (ishlatilmaydigan nutqni tanish kutubxonalari olib tashlandi); Android fonda serverdan kamroq so'raydi
- Mahsulotlarni ommaviy yuklash: ustunlar aniqroq aniqlanadi («Narx» — sotuv narxi, «Xarid» — xarid narxi; ishonch darajasi va shubhalilarni ajratib ko'rsatish), «Miqdor» endi majburiy emas — usiz faqat kartochkalar va narxlar yuklanadi, qoldiq o'zgarmaydi; yangi «Qo'shimcha shtrix-kodlar» maydoni (o'chirmasdan va takrorsiz qo'shish, begona shtrix-kodlarni tekshirish); katta fayllar: qidiruv, «Ogohlantirishlar» va «Birlashtirilgan» filtrlari, xato guruhlari, jarayon bilan tahlil, 100 tadan paketlab yuklash va to'xtatish; ustunlar o'zgargach o'zi qayta tekshiradi; «Qanday fayl mos» maslahati va yangi shablon
- Ombor → tovar → «Kirimlar» varag'i (va tovar qatorida «⋯ → Kirimlar va partiyalar»): tovar kimdan, qachon va qaysi yuk xati bilan kelgan — sana va vaqt, yetkazib beruvchi (bosilsa kartasi ochiladi), kirim №, miqdor, xarid narxi, summa, kim qabul qilgan, to'lov, «partiya qoldig'i» (FIFO bo'yicha taxmin) va tepada jami; yetkazib beruvchi va raqam bo'yicha qidiruv, «Hafta / Oy / Yil / Hammasi» davri, yuzlab kirimga bardosh beradigan ro'yxat, aloqasiz — oxirgi yuklash ma'lumoti. «Yuk xati №…» tovar qatori ajratilgan «Kirim pozitsiyalari»ni ochadi; yuklangan yuk xati fayli (Excel, CSV, XML) endi shu kompyuterda qoladi va «Faylni ochish» bilan ochiladi. Xarid narxlari va yetkazib beruvchilarni faqat egasi va administrator ko'radi, kassirga — sana, miqdor va partiya qoldig'i
- Dialog oynalari doim oldinda: asosiy oyna yoki bo'lim ortiga yashirinmaydi va dasturni «qotirib» qo'ymaydi; bloklangan oynani bosish, Alt-Tab va yig'ish dialogni yana yuqoriga chiqaradi, dialog orqasidagi fon xira va biroz qoraytirilgan bo'ladi
- Sessiya muddati tugashi endi tupik emas: «Smenani yopishda xato: Sessiya yaroqsiz…» o'rniga bitta oyna «Sessiya muddati tugadi. Qaytadan kiring» va «Kirish» tugmasi, sarlavhada «🔑 Qaytadan kiring». Sotuvlar, smena, savatlar va smenani yopish saqlanadi va kirgandan keyin o'zi ketadi; egasi dasturida huquqlar yo'qolmaydi (egasiga «Huquq yetarli emas» chiqmaydi)
- Dastur endi NurCRM (Nur CRM) deb ataladi: yangi nom oynalarda, menyularda, cheklarda, maslahatlarda va yordamda
- Internetsiz endi mumkin: «qarzga» sotuv, qaytarish va qarz to'lovi. Mijoz va chek kassa xotirasidan olinadi, operatsiya navbatda kutadi («Yuborilishini kutmoqda») va chekdan keyin serverga o'zi ketadi; server rad etsa (qarz limiti, mijoz o'chirilgan) — sababi bilan «Tekshirish kerak», hech narsa yo'qolmaydi. Yangi sozlama «Aloqasiz maks. qarz»
- Elektr o'chganda yoki nosozlikda chek yo'qolmaydi: sotuv serverga yuborilishidan oldin diskka yoziladi
- Aloqa uzilgandan keyin «Qarzga» va aralash to'lov chekning takrorini yaratmaydi; takrorlash kalitlari serverning 409, 423 va 425 javoblarida yo'qolmaydi
- Yuborilmagan cheklar navbati bitta muammoli chekda qotib qolmaydi; qizil «Yuborilmadi/Rad etildi» belgisi va egasiga xabar
- Navbat fayllari atomik tarzda va nusxalari bilan yoziladi; ma'lumotlar bazasi ishga tushganda tekshiriladi; baza bloklangan yoki disk to'lgan bo'lsa tushunarli xabar
- Aloqasiz ochilgan smena aloqa paydo bo'lganda serverdagi haqiqiy smenaga bog'lanadi
- Sinxronlash: narx va qoldiq do'kondagi boshqa kassalarga 1–2 s ichida yetadi (304 kodli o'zgarishlar lentasi, HTTP/2, aqlli so'rov), mahalliy tarmoq bo'yicha maslahatlar
- Telefon bilan aloqa (PhoneLink): mobil ilova uchun hodisalar va maslahatlar
- Shtrix-kod tovar kirimidan keyin yo'qolmaydi
- «Asu» qidiruvi: aniq mos kelganlar ro'yxatda yuqorida turadi
- CSV ommaviy yuklash: qulamaydi, kodlash aniqlanadi, CSV o'rniga Excel tanlansa maslahat, qatorlar bo'yicha tushunarli xatolar
- AI Maslahatchi: nakladnoyni Ctrl+V bilan qo'yish yoki oynaga sudrab tashlash mumkin
- Linux: chek chop etish (/dev/usb, ttyUSB, TCP, CUPS), yorliqlar va avtoishga tushirish
- macOS: Mac uchun kassa (Apple Silicon va Intel) zip arxivida
- Kompaniya almashganda avvalgisidan hammasi o'chiriladi: sun'iy intellekt kalitlari, pullik funksiyalar va mavzular, baza, cheklar, bot, printerlar; yuborilmagan sotuvlar jimgina yo'qolmaydi — avvalgi akkauntga qaytish, faylga saqlash yoki tasdiq bilan o'chirish
- Egasi dasturi: «Bonuslar», «Ish haqi», «Investitsiyalar» hisobotlarida tushuntirishlar, davr yozuvi
- «Yuborishda xato» holatidagi cheklar o'zi ketadi; belgida sabab va «Qayta yuborish» tugmasi bor
- Aloqa uzilganda kassa o'zi HTTP/1.1 ga o'tadi
- «Yuborishda xato» avto-takrori faqat yangi cheklar uchun, eskilari kassir qarorini kutadi
- To'lov kaliti yuborishdan oldin diskka yoziladi, nosozlikdan keyin server bilan solishtiriladi — ikki marta sotuv bo'lmaydi
- Aloqasiz boshlangan chek: to'lovdan oldin narxlar server bilan solishtiriladi
- Kassa fayllari «boshqa dastur band qilgan» xatosidan himoyalangan
- Pul kiritish va chiqarish kerakli smenaga bog'lanadi
- Tovar kirimida tushunarli xato matni va sarlavhada «xato: N» belgisi
- Taftish: xarid narxi, farq summasi, pul bo'yicha jami, eng ko'p yetishmaydigan 10 tovar
- Protsessor va batareyaga yuk kamaydi: fonda kassa serverdan bir necha barobar kamroq so'raydi; ilova ~26 MB yengilroq
- Dialog doim oldinda, orqasidagi fon xira va qoraytirilgan; oyna kassa qatlami ostiga tushmaydi, teginishni to'sib qo'ymaydi
- Android: sozlamalar va ommaviy yuklash xatosiz, tekis bo'shliqlar va «⋯» tugmasi, shtrix-kodlarni oldindan ko'rish
