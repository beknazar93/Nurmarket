# ТЗ Касса — адаптация к изменениям API сервера NurCRM (26.10.2026)

**Для кого:** исполнитель Sonnet (правки кода), приёмка — Opus. **Основание:** письмо Умара `Изменения API сервера по ТЗ 23.10 — что меняется для кассы (25.10.2026).md` (ветка бэкенда `feature/tz-ne-sdelannoe-2026-10-23`, **на сервер ещё не выкачено**), наше `ТЗ для бэкенда NurCRM — НЕ СДЕЛАННОЕ (23.10.2026).md`, разбор кода кассы, программы владельца и мобилки 26.10 (только чтение).

Номера строк даны по рабочей копии на 26.10. В ней 57 изменённых и не закоммиченных файлов, поэтому строки могут сдвинуться. Сверяйте по имени метода.

## 0. Коротко

1. Касса должна одинаково работать со **старым** API (как сейчас на боевом сервере) и с **новым** (ветка Умара). Жёсткого перехода нет: признаки нового API ищем в самом ответе, а где их нет — пробуем один GET и запоминаем результат.
2. Почти все пункты касса переживёт без правок. Пользователь увидит только сырой текст ошибки вроде `code: permission_denied; required: can_view_products`.
3. **Одна поломка, которая мешает выкатке: «Расход» в чеке.** Это строка «Доп. услуга» с минусом. Сейчас касса шлёт её старым путём `custom-item`. Новый сервер ответит `400 negative_price`, и такой чек не пройдёт оплату. Нужно решение владельца и Умара (раздел 3.1).
4. Деньги без смены. Касса шлёт `shift` всегда, когда у неё есть серверная смена. Программа владельца `shift` не шлёт никогда, это решение владельца от 18.10. Как только сервер включит правило «без shift — вне смены», касса должна запрещать оплату долга наличными без серверной смены (раздел 3.2).
5. Обходы («вернуть цену вторым PATCH», «Корректировка по данным сервера», поправки `ExchangeCashCorrections`) снимаются только по признаку в ответе сервера. Если сервер откатят, касса сама вернётся к обходам.
6. Пакеты для Sonnet: П0 — подменный сервер с двумя режимами; П1 — ServerCapabilities и разбор ошибок (первым); П2–П7 — правки по темам, без общих файлов; П8 — новые возможности после выкатки; М1–М3 — мобилка.
7. Денежные пакеты П2, П3, П4, П5а, П6 и М1 после правки проходят через `/code-review`.
8. Проект ответа Умару — раздел 6. Его можно скопировать в сообщение целиком.
9. Что запросить у владельца, в том числе файл патча NurRemoteSupport, — раздел 7.
10. Версия: касса 1.17.61 (пакеты П1–П7), мобилка 1.18.5 (М1–М3). Выкатка сервера — после выпуска 1.17.61 или после решения по «Расходу».

---

## 1. Главное правило: два API одновременно — «флаги возможностей»

### 1.1. Три способа узнать, какой перед нами сервер (в порядке предпочтения)

1. **Признак в самом ответе.** Это лучший способ: кэш не нужен, касса решает по каждому ответу отдельно. Где такой признак есть, флаг из кэша **не используется**.
   - Отчёт смены: есть `cash_in_drawer_breakdown` — строки раскладки берутся из него. Нет — собираются по-старому.
   - Касса компании: есть `drawer_cash` — «Остаток по системе» берётся из него.
   - Смена: есть `auto_close_at`, `closed_by`, `close_reason` — их и показываем.
   - `PATCH products/{id}/`: есть `price_changed` — сервер нового поведения (Н-04).
   - Возврат: есть `refund{…}` — суммы берутся из него. Нет — как сейчас: `GET pos/returns/?sale=` и движения денег.
   - Оплата сделки поставщика: есть `cashflow_id` — расход записан, проверка «сервер не записал расход» не нужна.
   - Прокат: пришёл `{count,next,results}` — листаем по `next`. Пришёл массив — одна страница.
   - Ошибки: есть `code` — смотрим его. Нет — старый разбор по `detail` и подстрокам.
2. **Пробный GET раз в 6 часов и после входа.** Нужен для вопроса «есть ли новый адрес», когда без него касса делает лишнюю работу или не может включить функцию.
   - Маркер нового API: `GET api/main/idempotency/<случайный uuid>/`.
     - 404 с JSON-телом `{"code":"not_found"…}` или `{"found":false}` → **New**.
     - 404 без JSON (HTML Django) → **Old**.
     - 401/403/5xx/таймаут → **Unknown**, ничего не меняем.
   - Если Умар добавит `GET api/main/capabilities/` или заголовок версии API (вопрос в разделе 6), пробовать сначала его.
3. **Отказ «адреса нет».** Запрос на новый адрес вернул 404 без JSON, 405 или 501. Тогда эта возможность сразу помечается Old (сервер откатили или ещё не выкатили), и касса уходит на старый путь. Правило одно на всю кассу — `ServerCapabilities.IsEndpointMissing`, оно повторяет нынешнее `ClientDebtsApiService.IsEndpointMissing`. **404 с JSON-телом — это «объект не найден», а не «адреса нет».**

### 1.2. Класс `ServerCapabilities` (новый файл `NurMarketKassa.Infrastructure\Services\ServerCapabilities.cs`)

- `enum CapState { Unknown, Old, New }`
- `enum ServerFeature`:
  - `Api202610` — общий маркер;
  - `IdempotencyLookup` (Н-08);
  - `ProductPriceStable` (Н-04);
  - `ShiftReportV2` (Н-05);
  - `CashboxDrawerCash` (Н-06);
  - `ShiftAutoCloseInfo` (Н-09);
  - `ReturnRefundBreakdown` (Н-02);
  - `NegativeCustomRejected` (Н-15);
  - `ClientDocuments` (Н-22);
  - `BotOwnerServer` (Н-23);
  - `SalaryPayouts` (Н-18);
  - `SupplierPayDebt` (Н-11);
  - `AccessCodesServer` (Н-16);
  - `ChangesFeed` (Н-31);
  - `ReceiptCreateDeal` (Н-10).
- `CapState Get(ServerFeature f)`.
- `void Observe(ServerFeature f, bool isNew, string evidence)` — пассивное наблюдение из разборщиков ответов. В журнал пишется одна строка на каждую смену состояния: `Сервер: <функция> — новое API (признак: …)`.
- `Task ProbeAsync(NurMarketApiClient api, CancellationToken ct)` — пробный GET (п. 1.1.2). Не чаще раза в 6 ч, ошибки глушит.
- `static bool IsEndpointMissing(Exception ex)` — единое правило (п. 1.1.3).
- Ключ кэша: базовый адрес API + id компании. Другая компания — другой набор флагов.
- Файл кэша: `%LOCALAPPDATA%\<папка данных AppMode>\server-caps.json`. Для кассы это `NurMarketKassa`, для владельца — `NurMarketOwner`.
  - Есть статическое `DataDirOverride` для стендов, как у `BackgroundReceivingQueue.DataDirOverride`.
  - Запись атомарная: временный файл и замена.
  - Повреждённый файл означает пустой кэш, без падения.
- Срок жизни в кэше:
  - New — 24 ч, Old — 6 ч;
  - любой признак Old в ответе сразу переводит функцию в Old, признак New — в New.
- Поведение по умолчанию: **Unknown = Old**. Касса делает то же, что сейчас. Включается только то, что не может навредить старому серверу: разбор `code` в ошибках, чтение новых полей, если они есть.

### 1.3. Правило для денежных обходов

Обход снимается **только по признаку в этой же операции или в этой же смене**. Одного флага из кэша для этого мало.

- **«Вернуть цену вторым PATCH» (Н-04, `RestoreSalePricesAsync`).**
  - Флаг `ProductPriceStable == New`: после прихода касса проверяет цену **у одной строки** (выборочно). Совпала — остальные не проверяет. Не совпала — `Observe(Old)` и полная проверка, как сейчас.
  - Флаг не New: полная проверка, как сейчас.
- **«Корректировка по данным сервера» (`DrawerBreakdownBuilder`).** Не выводится, если строки раскладки пришли от сервера (`cash_in_drawer_breakdown`). Выводится только если сумма строк сервера ≠ `expected_cash` (защита от ошибки сервера).
- **Поправки `ExchangeCashCorrections`.** Записываются и прибавляются только для смен, у которых **нет** признака нового отчёта. Признак ставится, когда отчёт этой смены хоть раз пришёл с `cash_in_drawer_breakdown`; хранится по id смены в том же файле поправок. Для смены, начатой до выкатки и закрытой после неё, ждём ответа Умара (вопрос 6.5). До ответа поправки такой смены не трогаем и показываем как есть.

---

## 2. Матрица влияния

Обозначения: **К** — касса, **В** — программа владельца, **М** — мобилка (Flutter, `NurMarketMobile\lib\`). «Сейчас» — что будет, если сервер выкатить сегодня, без наших правок. Пути от `NurMarketKassa.Avalonia\`:
- **Inf** = `NurMarketKassa.Infrastructure\Services\`
- **App** = `NurMarketKassa.Avalonia\`

### 2.1. Раздел 1 письма Умара (меняет поведение)

| № | Что меняет сервер | Наш код | Сейчас (без правок) | Что сделать | Пакет |
|---|---|---|---|---|---|
| Н-01 | `cards.revenue` включает предоплату долговых чеков; новые поля `prepayment_total`, `credit_sales_total`, `debt_repaid_total` | В: `App\Views.axaml\Main\OwnerShellWindow.axaml.cs:450` (подпись «Выручка (без долга)»), `:1650 ApplyCards`; `App\Services\ServerSalesSummary.cs:46 FetchAsync`, `:143 Split()` (безнал = выручка − нал − mixed − debt); «Финансы» `FinanceWindow.axaml.cs:148, 2300, 2651`. К: Z/X `Inf\..\Models\ShiftModel.cs:56 RevenueNoDebt = min(sales_total, cash+noncash)`. М: `lib\features\admin\admin_repository.dart:112-130`, `analytics.dart:40-88` | **Выдержит.** Выручка Сводки вырастет на предоплаты и сравняется с Z/X: там предоплата уже входит в «наличные». Подпись «(без долга)» остаётся верной. Риск: если `charts.payment_methods` станет раскладывать «Смешанную» и получит элемент `debt`, `Split()` может занизить безнал (вопрос 6.4) | Проверить `Split()` на стенде в режиме new. Показ «Предоплата / В долг» в Сводке — П8 | П5а (Split), П8 |
| Н-02 | Возврат с долей скидки на чек; повтор → **409 `refund_exceeds_paid`** (было 400); в ответе `refund{}`, `replayed` | К: API `Inf\Api\SalesApiService.cs:838 PosReturnSaleAsync`, `:875 PosReturnWholeSaleAsync`, `SalesApiService.Exchange.cs:84 PosReturnWithDetailsAsync`. Ответ выбрасывает `App\Services\AccountCatalogIsolation.cs:145-191 PosRefundService`. Окно `App\Views.axaml\Dialogs\ReturnSaleDialog.axaml.cs`: `:910-921` доля скидки, `:1194 ServerReturnFiguresAsync`, `:1282 ResolveRefundAsync`, `:1368 ComputeRefundSplit`, `:1151-1173` и `:2013-2030` ошибки. Журнал `Inf\SaleReturnLedger.cs`. **Возврат по строкам идёт через `RequestDataAsync`** (`NurMarketApiClient.cs:1000 EnsureSuccessStatusCode`): тело ошибки теряется, вместо `ApiException` приходит `HttpRequestException`. М: `lib\features\ops\ops_repository.dart:168-185` (ответ не разбирается) | **Выдержит.** Суммы сервера станут равны расчёту кассы. Записи «сервер вернул больше» (`ReturnOverpaid`) перестанут появляться. 409 покажется сырым текстом, кода не видно | Возврат — через `RequestAsync` (с телом). Если в ответе есть `refund{}` — брать `cash/noncash/debt_reduced` из него. `replayed:true` — не писать журнал второй раз. 409/400 `refund_exceeds_paid` — понятный текст и перечитать чек | П3, М2 |
| Н-04 | `PATCH purchase_price` не меняет `price`; пересчёт только с `recalc_price:true`; в ответе `price_changed` | К/В: `Inf\PurchaseReceivingService.cs:738-789` (PATCH `{price, markup_percent}` после прихода), `:791-810 → RestoreSalePricesAsync :1007-1044` (обход), `:665-697` чтение цены «до»; `Inf\ReceivingPricing.cs:46, 82-124`; окно товара `App\Views.axaml\Dialogs\ProductEditDialog.axaml.cs:1709` (все три цены явно); `Inf\Api\CatalogApiService.cs:781-877 BuildProductBody`; массовый импорт `BulkImportWindow.axaml.cs:239-245`; ИИ `Inf\ProductActionPlan.cs:567` (`set_purchase` — только `purchase_price`). М: `lib\features\stock\product_edit_repository.dart:116-121, 199-213` (тот же обход) | **Выдержит.** Обход станет холостым: лишний GET на каждую строку прихода. Поведение изменится в двух местах: ИИ `set_purchase` и CSV-импорт «закупка без цены» больше не поменяют цену продажи. Именно этого владелец и хотел | Выборочная проверка цены при `ProductPriceStable == New` (п. 1.3). `recalc_price` не слать: касса всегда задаёт цену явно. Мобилка — оставить как есть | П6 |
| Н-13 | Оплата долга: **400 `shift_closed`**, **400 `shift_not_found`**, **403 `shift_not_owned`** (владельцу можно), 400 `field_conflict` | К: `App\Views.axaml\Dialogs\PayDebtDialog.Method.cs:333-334` шлёт `PosApp.ActiveShiftId`. В: `AcceptDebtPaymentWindow.cs:1108` — дословно `// Без смены: shiftId = null — поле shift не отправляется (решение владельца 18.10).` (окно открывается только из `DebtsWindow` программы владельца). Запросы: `Inf\Api\ClientDebtsApiService.cs:116 PayDebtAsync` (`method`), `:160 PaySaleDebtAsync` (`payment_method`). Если смена пустая или начинается на `offline-`, `shift` не шлётся (`:129-131`, `:173-175`). Ключ — только заголовок `Idempotency-Key` (`:302`). Ошибки — `App\Services\DebtPaymentRunner.cs:712-748`, `:859 IsShiftClosedError` (по подстроке), `:895 RejectedText` (разбирается только `no_debt`); `DebtPaymentFlow.cs:29 IsNoDebt`. М: `ops_repository.dart:156-164` (`shift`, если смена не офлайновая) | **Выдержит.** Тексты будут сырые. `field_conflict` невозможен: каждый путь шлёт одно своё поле. Риск `shift_not_owned`: кассир Б вошёл, когда открыта смена кассира А (проверить на стенде) | Разбор кодов: `shift_closed`/`shift_not_found` — «Смена закрыта — откройте смену»; `shift_not_owned` — «Смена открыта другим кассиром…»; `amount_exceeds_debt` (`data.max`); `no_debt`. 409 `idempotency_conflict` — это не «отказ», а «исход неизвестен». Касса: наличные без серверной смены запрещены (раздел 3.2) | П4, М2 |
| Н-14 | `clients/debtors/`, `clients/with-debts/` без поставщиков (вернуть: `?include_suppliers=1`) | К/В: `ClientDebtsApiService.cs:66 GetDebtorsAsync`. Вызовы: `PayDebtDialog.axaml.cs:1150`, `AcceptDebtPaymentWindow.cs:343`, `ClientPickerDialog.axaml.cs:95`, `DebtReminderPlanner.cs:272`, `TelegramBotPollingService.Server.cs:88` — везде нужны только клиенты. `with-debts` — только `DebtsWindow.cs:295`, поставщиков касса там отсеивает сама (`:294-303`). Поставщики берутся из `clients/?type=suppliers` (`CatalogApiService.cs:707-712`, `SupplierApiService.cs:118`, `PurchaseReceivingService.cs:275`, `SupplierReturnsWindow.cs:564`, `OwnerAiContext.cs:46`), долги поставщикам — из `kpis/` и `deals/` (`SuppliersWindow.cs:107, 341`). М: `ops_repository.dart:131-152` (поставщиков не отсеивает, сервер это починит) | **Выдержит, станет лучше.** `include_suppliers` **нигде не нужен** | Ничего. Фильтр в DebtsWindow оставить для старого сервера | — |
| Н-11 | `deals/{d}/pay/` поставщика без кассы в компании → **400 `cashbox_unresolvable`** | В: `Inf\Api\SupplierApiService.cs:197 PayInstallmentAsync` (ответ не разбирается). `App\Views.axaml\Main\SupplierCardView.cs:308` (оплата взноса), `:370-383` (ошибки: обрыв `HttpRequestException` показывается как «Сервер не принял оплату»), `:386-395` («сервер не записал расход — внесите вручную»), `:416 PayAllDebtAsync` («Погасить весь долг») | **Выдержит.** Текст сырой. «Погасить весь долг» остановится на первом взносе, это правильно | Текст «В компании нет кассы для расхода — создайте кассу на сайте NurCRM». Обрыв связи — «исход неизвестен», а не «отказ». Есть `cashflow_id` — проверку расхода пропустить | П6 |
| Н-15 | custom-item и checkout с отрицательной ценой → **400 `negative_price`** | К: «Расход» в чеке — `ViewModels\Main\BasketPanelViewModel.cs:3545` (`isExpense ? -price : price`). `Inf\Pos\QuickCheckoutBody.cs:196-200` уводит такой чек на старый путь. `Inf\Pos\StagingCartService.cs:121-129 → SalesApiService.cs:209 PosAddCustomItemAsync`. М: отрицательную цену не шлёт (`sale_screen.dart:380`) | **СЛОМАЕТСЯ.** Чек с «Расходом» не оплатится: ошибка на `custom-item`. Сейчас сервер принимает такую строку, но в продаже даёт 0 (письмо Умара), то есть сумма на сервере уже расходится с кассой | Решение владельца и Умара (3.1). До решения: перед оплатой понятный отказ с предложением «Оформить расход отдельно (изъятие из кассы)» — этот путь уже есть для пустого чека | П2 |
| Н-15 | `checkout/batch/` с упаковками | К: `Inf\SyncService.BatchReplay.cs:26-163`. Упаковки в quick/batch не попадают никогда (`QuickCheckoutBody.cs:217-221`) | **Выдержит** | Снять запрет на `sale_package_id` в быстром пути — П8, после проверки на стенде | П8 |
| Н-15 | Коды ошибок чекаута (`variant_required`, `insufficient_stock`, `price_below_cost`, `cash_less_than_total`, `mixed_sum_mismatch`, `too_many_items`…), `errors[{index}]`; статусы HTTP прежние | К: `Inf\ApiErrorParser.cs:8-61` (только `detail`, иначе склейка «ключ: значение»). `Inf\Pos\PosCheckoutService.Quick.cs:162-171` (4xx → молча старый путь), `:299 IsQuickTransientStatus` (**409 временный** → повтор и очередь). `App\Services\ReceiptHistoryService.cs:801-845 DescribeUploadError` ищет русскую фразу «Товар «X» имеет размеры». `SyncService.BatchReplay.cs:164 Detail()` читает только `detail`. Эвристики по подстроке: `PosCheckoutService.cs:867, 1547`, `SyncService.cs:469-494`, `CashShiftService.cs:376`, `RevisionSession.cs:1173`. М: `lib\core\api\api_client.dart:17` (`isTransient` включает 409), `sales_repository.dart:190-200` (409 → в очередь, остаток на телефоне уменьшается) | **Выдержит** (старые ключи и статусы сохранены), но тексты сырые. Риск: если Sonnet начнёт слать `expected_total`, 409 `total_mismatch` уйдёт в очередь как временный. **Не слать `expected_total` до П2** | Разбор `code` и `errors[].index` (номер строки → название товара); тексты на 5 языках; 409 делить по `code` | П1, П2, М1 |
| Н-16 | Права: **403 `{code:"permission_denied", required}`** | См. 2.2 | **Выдержит.** Кассир увидит `code: permission_denied; required: can_view_products` | Понятный текст «Нет доступа: …» | П1, П7, М1 |
| Н-21 | `GET rentals/` всегда `{count,next,previous,results}` (до 500) | К/В: `Inf\Api\RentalsApi.cs:60 ListAsync`, `:199 Rows()` (массив и `results` понимает, по `next` **не листает**), `:119 Describe` (берёт **первое строковое поле** тела — кассир увидит `validation_error`). Вызовы: `RentalsWindow.cs:203-208`, `RentalDueNotifier.cs:132-141`. М: `lib\features\rentals\rentals.dart:84-98` (понимает оба вида) | **Выдержит**, пока прокатов меньше 500 | Листать по `next`; `Describe`: сначала `detail`, потом `code` | П7 |
| Н-26/27 | Витрина: 409 `variant_out_of_stock` (+`index`, `available`), 400 `variant_required` | К: `Inf\Api\ShowcaseApiService.cs:205 CreateBotOrderAsync`; `TelegramAiChat.cs:845` ловит всё и пишет покупателю «Заказ передан продавцу для проверки…». М: `admin_repository.dart:218-253` (только статусы заказов) | **Выдержит.** Покупатель не узнает, что размера нет | Покупателю: «Размера/товара «…» нет в наличии, доступно N». Продавцу — причина | П7 |
| Н-27 | Остаток товара = сумма вариантов | К: `VariantEditorWindow.cs:194-225` (предупреждение о расхождении), `PurchaseReceivingService.cs:911-957`, `WarehouseWindow.Variants.cs:41-89`; `ProductEditDialog` шлёт `quantity` и у товара с размерами | **Выдержит.** Предупреждение просто перестанет появляться | Проверить на стенде: сервер не должен отвергать `quantity` у товара с размерами. Если отвергает — `SendQuantity=false` при `HasVariants` | П7 (проверка) |
| Н-30 | Рекомендации: статусы `out_of_stock`, `no_sales`; 400 `invalid_param` | В: `Inf\Api\SupplierApiService.cs:274-347 GetRecommendationsAsync`, сопоставление по подстроке `:318-339` (`crit\|out\|empty\|zero` → Critical); подписи `PurchasesWindow.Recommend.cs:568-576`; `Inf\Purchases\PurchasePlanner.cs:18-32`. 400 → WARNING и тихий свой расчёт (`Recommend.cs:339-342`) | **Выдержит.** `out_of_stock` покажется как «Критический остаток», а не «Нет в наличии» | Точное сопоставление `out_of_stock` → `PlannerStatus.OutOfStock`, `no_sales` → `NoSales` до поиска подстроки | П6 |
| Н-32 | Активация ключа: чужой ключ → **409 `key_already_used`**, повтор → 200 `replayed`, более 10 неверных за час → 429 | В: `App\Views\Settings\MarketplaceView.axaml.cs:1466 UnlockPackAsync`, `:1477 catch (Exception)` — **любая ошибка уходит в журнал, а ключ проверяется локально** (`TryValidateSerial :1494`) | **Дыра (и сейчас):** ключ, занятый другой компанией (400 `key_used`, 409), может открыться локальной проверкой | 409/400 `key_already_used`/`key_used` — отказ с текстом, без локальной проверки; 429 — «Слишком много попыток, подождите час». Локальная проверка — только если сервер недоступен (обрыв/5xx) | П7 |
| Н-33 | `products/barcode/{code}/`: `product`, `matched_by`; `variant` — только при совпадении штрихкода варианта | К: адрес **не используется** (поиск — `Inf\Pos\ProductCodeLookup.cs:119-170` через `products/list/?search=`). М: не используется | **Выдержит** | Можно использовать для поиска варианта — П8 | П8 |
| Н-05 | Отчёт смены: `cash_sales` как был (с предоплатой); новые `revenue_excl_debt`, `credit_sales_total`, `debt_repaid_*`, `prepayment_total`, `closing_cash`, `cash_in_drawer_breakdown[]` (предоплата — отдельной строкой `prepayments_cash`) | К/В: `Inf\Models\ServerShiftReport.cs:118-161` (разбор), `:83, 86 CashWithPrepayment`, `:166-191 ApplyTo`; `Inf\Models\ShiftModel.cs:56-60`; `App\Services\DrawerBreakdownBuilder.cs:15-58` (строка «Наличные продажи (с предоплатой по долгам)», `:45-55` **«Корректировка по данным сервера (возврат/предоплата)»**); `ShiftDetailsDialog.axaml.cs:357-405, 770-887`; `CloseShiftDialog.axaml.cs:156-179, 347-375`; `App\Services\ExchangeCashCorrections.cs` + `ShiftCashOperationsStore.cs:84-100`. М: `admin_screens.dart:437-457` | **Выдержит.** `cash_sales` прежний, новые поля просто не читаются. **Риск двойного счёта появится, если** вывести строку `prepayments_cash` из раскладки рядом с нашей строкой «Наличные продажи (с предоплатой)» | Если есть `cash_in_drawer_breakdown` — строки **только** из неё, ни с какими старыми полями не смешивать. `revenue_excl_debt`/`credit_sales_total` — вместо расчёта `ShiftModel`, если пришли. Корректировка и поправки — по п. 1.3 | П5а |

### 2.2. Раздел 2 письма (права, Н-16)

| Что проверяет сервер | Где касса упрётся в 403 | Что сделать | Пакет |
|---|---|---|---|
| `PATCH/PUT/DELETE products/{id}/`, `bulk-*` требуют `can_view_products` (создание со сканера кассиру оставлено) | Кассир без «Склада» правит цену или товар из кассы (если такое окно доступно ему по нашим правам). Склад кассиру и так закрыт (`OwnerShellWindow.axaml.cs:598-708`, `MainWindow.axaml.cs:1875 NavigateWarehouse`) | Текст «Нет доступа: Товары (Склад)…» | П1 |
| `PATCH/DELETE clients/{id}/` требуют `can_view_clients` | Правка клиента из кассы: имя или телефон в окне клиента, срок долга | Текст. Создание и поиск клиента работают | П1 |
| `cashboxes/` (запись), `cashflows/bulk/status/` требуют `can_view_cashbox`; `cashflows/` без флага — только своя смена, операция ящика или заявки | `PurchasePaymentService.cs:288 CompanyExpenseAsync` (расход из кассы компании при приёмке кассиром); `SalaryPayoutService.cs:615-637` (зарплата, если её запускает не владелец) | Текст «Нет доступа: Финансы — кассы компании»; в приёмке кассиром — предложить «из ящика смены» | П1, П6 |
| `analytics/*` требует `can_view_analytics` (`tab=sales` открыт всем) | Кассир: `ServerSalesSummary` (`tab=sales` — открыт). Прочая аналитика — у владельца | Ничего; текст на всякий случай | П1 |
| `GET users/employees/` требует `can_view_employees` | `Inf\SettingsCloudSync.cs:520` (сверка кодов доступа, ловит исключение и пишет WARNING); `NurMarketApiClient.cs:442 GetEmployeesAsync` перебирает адреса только при 404/405, на 403 бросает исключение — это правильно | Ничего. На 403 не переходить к следующему адресу | — |
| Новое поле `can_use_pos` (у всех `true`); при `false` закрыты `shifts/open/` и `pos/checkout/` | `App\Services\PermissionService.cs:88 PersonalAccessFlags`, `:102 IsCashierWorkAllowed`; поля `can_use_pos` в коде нет | Явное `can_use_pos == false` → «Работа на кассе выключена владельцем» до открытия смены. Поля нет — как сейчас. Не добавлять `can_use_pos` в правило «ничего не выставлено → можно» | П7 |
| `reset-password` | Сброса пароля в кассе нет | Кнопка «Сбросить пароль» в Настройки → Сотрудники (владелец) — П8; пароль показать один раз и **не писать в журнал** | П8 |
| Коды доступа на сервере (`access-codes`, `verify`, 429) | `Inf\EmployeeAccessGate.cs` (PBKDF2, `TryValidate :168`), ключи `EmployeeAccessCodes*` в `SettingsCloudSync.cs:54-56, 502-553` | П8 (миграция). До неё — ничего: хэши в настройках продолжают работать | П8 |
| `POST users/employees/create/` всегда отдаёт `id` | `NurMarketApiClient.cs:484 CreateEmployeeAsync` (поиск id вглубь, иначе по email) | Ничего (станет надёжнее) | — |

### 2.3. Раздел 3 (новое) — что берём и когда

| № | Возможность | Наш код сейчас | Решение |
|---|---|---|---|
| Н-03 | `receipt/` и `deals/{d}/pay/` с `shift` — один расход из ящика | `PurchasePaymentService.cs:45-53` (из ящика уже шлём `cashbox_role=pos_main`, `cashbox`, `shift`), второй расход **не пишем**; `CheckServerExpenseAsync :349-393` сравнивает `shift` строкой | П6: принимать `shift` и строкой, и объектом `{id}`, иначе выйдет ложное «расход без смены» |
| Н-05/06/09 | Отчёт, `drawer_cash`, автозакрытие | См. 2.1. Н-06: `MainWindow.axaml.cs:1729-1767 OpenShiftAsync` → `ShiftApiService.cs:82-111` → `ShiftBalanceHelper.cs:52-75` (`closing_cash` последней смены). Н-09: `MainWindow.axaml.cs:2185-2196` (таймер 90 с), `Inf\Pos\ShiftStateService.cs:117-185`, `Core\Contracts\IShiftStateService.cs:77-128` (пороги 22/23/23:45 ч жёстко), `App\Services\ServerShiftTexts.cs:51-60` («Смена идёт {h} ч…») | П5б: `drawer_cash` у кассы `App.PosCashboxId`, если поле есть; `auto_close_at`, если есть, вместо расчёта; `ShiftAutoClose {enabled,hours}` из `app-settings/nurmarket-mobile/` — только чтение (`enabled=false` → плашки нет, `hours` → пороги); `closed_by`/`close_reason` → «Смена закрыта сервером автоматически (24 ч)» |
| Н-07/Н-28 | `pos/returns/` постранично (только с `page`/`page_size`); `pos/sales/` за последней страницей → пустой список | `Inf\Api\NurCrmReportsApi.cs:98 ListReturnsAsync` (ошибка на 2-й странице обнуляет весь список, `:186-189`); `SalesApiService.cs:251 PosSalesListAsync`, `:363-364` (404 на странице >1 считается ошибкой) | П3: ошибка или 404 на странице >1 означает конец списка, полученное сохраняется |
| Н-08 | `GET main/idempotency/{key}/` | `Inf\PurchaseReceivingService.cs:233-269 FindPostedReceiptAsync` (сверка по списку приходов), `DebtPaymentRunner.cs:619 ResolveAsync` (ручное «Оплата есть на сайте») | П8: если `IdempotencyLookup == New` — сначала спросить исход по ключу, старый способ оставить запасным |
| Н-10 | Приход сам создаёт долг только с `create_deal:true` | `Inf\SupplierDebtRecorder.cs:208-342` (касса создаёт сделку сама), `BackgroundReceivingQueue.Worker.cs:447-461` | **Пока не включать.** П8: слать `create_deal:true` и, если в ответе есть `deal_id`, своей сделки не создавать; после обрыва — исход по `idempotency/{key}` (`deal_id`) или по `deal_id` в списке приходов. Без этого получится двойной долг |
| Н-11 | `pay-supplier-debt/` | `SupplierCardView.cs:416 PayAllDebtAsync` (по взносам) | П8 |
| Н-18 | `market-salary/payouts/` | `App\Services\SalaryPayoutService.cs` (метка `[#user дата–дата]` `:169`, журнал ПК `:1280`, `source_kind` отвергается `:31,40`) | П8 (деньги, `/code-review`) |
| Н-19 | `category`, `shift`, `source_kind` в `cashflows/` | `ShiftCashFlowSync.cs:78-139` (без `idempotency_key`); `SalaryPayoutService.cs:642-680` (снимает отвергнутое поле) | Ничего срочного; ключ в `ShiftCashFlowSync` — П8 |
| Н-22 | `client-documents/` | `Inf\ClientDocs\ClientDocsStore.cs:10-44` (app-settings `nurmarket-mobile`, ключи `quotes`, `waybills`, `docsProfile`); М: `lib\features\admin\quotes_store.dart:259-274` | П8, одновременно в кассе и мобилке, с миграцией (нужно решение владельца: номера документов выдаёт сервер) |
| Н-23 | Владелец бота на сервере, `bot/notify-owner/`, `owner-code/` | `Inf\TelegramBotOwner.cs:6-43` (владелец хранится **локально** `%APPDATA%\NurMarketKassa\telegram-owner.json`), `TelegramBotOwner.Group.cs` («Группа заказов»), `UserPreferences.cs:506-534` | П8: при `BotOwnerServer == New` брать владельца с сервера; локальный файл — запасной. «Группа заказов» (Г1) пока остаётся локальной (на сервере её нет) |
| Н-31 | `pos/requests/`, `main/changes/`, `ws/company/` | Нет в коде. Опросы: `SyncService.cs:12-28` (45/90 с), `AvaloniaCatalogCacheService.cs:109-121`, `DebtPaymentSettings.cs` (2 мин) | П8 (P3) |
| Н-17 | Версии по ключам, `_expected` → 409; `If-Match` → 412 | `SettingsCloudSync.cs:675-700 PatchAsync` (без `If-Match`), `ClientDocsStore.cs:771-818` (своё слияние) | Ничего: `If-Match` не шлём, 412 не получим. Условная запись — П8 |

---

## 3. Найденные риски (важно до выкатки)

### 3.1. «Расход» в чеке (Н-15, `negative_price`) — мешает выкатке
- Кассир добавляет «Расход» (например, «вычесть за тару»). Это строка с ценой −N, `BasketPanelViewModel.cs:3545`.
- Быстрый путь (`pos/checkout/`) такую строку не берёт (`QuickCheckoutBody.cs:196-200`), и чек уходит старым путём через `custom-item`.
- Сейчас сервер принимает строку, но считает её как 0. Продажа на сервере получается больше, чем в кассе.
- После выкатки придёт 400 `negative_price`, и **оплата чека не пройдёт**.
- **Нужно решение.** Вариант Opus — **(б)**: деньги из ящика уходят честно, выручка не искажается.
  - (а) Слать «Расход» как скидку на чек суммой. Выручка уменьшится, остаток ящика сойдётся.
  - (б) Провести продажу без «Расхода», а «Расход» оформить отдельным изъятием из ящика (`shift_drawer_outflow`) с названием строки. Для пустого чека так уже сделано.
  - (в) Попросить Умара принимать в чекауте строку «расход» как отдельный вид.
- Пока решения нет, П2 делает понятный отказ до отправки и кнопку «Оформить расход отдельно».

### 3.2. Оплата долга без смены (Н-13)
- Касса шлёт `shift`, когда у неё есть серверная смена. Не шлёт, если смены нет или она ещё офлайновая (`offline-…`).
- Владелец не шлёт никогда, намеренно: «в руки владельцу».
- Если Умар сделает «без shift — вне смены», то наличные кассира, принятые при офлайновой смене, пропадут из ящика по системе.
- Поэтому в П4 касса запрещает **наличную** оплату долга без серверной смены: «Смена ещё не записана на сервере — подождите несколько секунд». Безнал без смены разрешён.

### 3.3. Поправки остатка (ExchangeCashCorrections) на стыке выкатки
- Если сервер после выкатки пересчитает `expected_cash` уже идущей смены «по-новому», то поправки, записанные кассой до выкатки, учтутся дважды.
- Просим выкатывать **ночью, когда все смены закрыты**, и ответить, пересчитывается ли `expected_cash` заново (вопрос 6.5).

### 3.4. 409 в быстром чекауте (касса и мобилка)
- Касса считает 409 временным: повтор, затем офлайн-очередь. Мобилка ставит продажу в очередь и уменьшает остаток.
- Это правильно для «запрос с этим ключом ещё выполняется».
- Это неправильно для `idempotency_conflict` (тот же ключ, другой состав) и для `total_mismatch`.
- В П2/М1: 409 с `code` из списка «окончательных» — отказ с текстом, без очереди и **без старого пути** (иначе будет вторая продажа). 409 без `code` — как сейчас.

### 3.5. Возврат по строкам теряет тело ошибки
`SalesApiService.cs:866` вызывает `RequestDataAsync`, а тот делает `EnsureSuccessStatusCode`. Касса не видит `code` и даже не отличает отказ от обрыва. В П3 возвраты переводятся на `RequestAsync`. Общий `RequestDataAsync` **не трогать**: его зовёт поиск товаров.

### 3.6. Активация ключа в Маркетплейсе открывается локально при отказе сервера
Это дыра и сейчас (`MarketplaceView.axaml.cs:1477`). Закрывается в П7.

### 3.7. Тексты по подстроке
- Часть решений касса принимает по словам в `ex.Message`: «смен», «shift», «не найдена», «пуст»+«корзин», «уже закрыта».
- Если в П1 поменять текст сообщений, эти проверки могут перестать срабатывать или сработать ложно.
- Правило П1: **сначала `code`, потом прежняя подстрока**. Сырой текст сервера сохраняется в журнале.

---

## 4. Стенд и критерии приёмки (два режима сервера)

### 4.1. Подменный сервер (П0)
Файлы: `C:\Users\User\NurMarket-test\stress\mock\mock_nurcrm.py`, `mock_core.py`, `mock_selftest.py`.

- Новый ключ запуска `--api old|new` (по умолчанию `old` — ровно нынешнее поведение).
- Новое действие управления `POST /__control/api {"mode":"old"|"new"}` переключает режим на лету: проверка «сервер выкатили или откатили посреди смены».
- Действие `permissions {"deny":["can_view_products",…],"can_use_pos":true|false}` — 403 `{code:"permission_denied", required}` на нужных путях.
- В режиме `new` (контракт из письма Умара):
  - `GET main/idempotency/{key}/` → 404 JSON `{"code":"not_found","found":false}` или `{found:true,state:"done",response}` для известных ключей. В `old` → 404 HTML.
  - `shifts/{id}/report/` + `revenue_excl_debt`, `credit_sales_total`, `debt_repaid_total/_cash/_noncash`, `prepayment_total`, `closing_cash`, `cash_in_drawer_breakdown` (сумма строк = `expected_cash`; `cash_sales` строки раскладки без предоплаты, а поле `cash_sales` отчёта — с предоплатой).
  - `cashboxes/` + `drawer_cash`, `drawer_cash_at`, `last_closed_shift`.
  - Смены + `closed_by`, `close_reason`, `auto_close_at`; `close-shift` из хаоса ставит `closed_by:"system"`, `close_reason:"auto_24h"`.
  - `PATCH products/{id}/`: `purchase_price` не меняет `price`; `price_changed` в ответе. В `old` — пересчёт цены от `markup_percent`, как сейчас на боевом.
  - `pos/sales/{id}/return/`: доля скидки, `refund{cash,noncash,debt_reduced,amount}`, повтор → 409 `refund_exceeds_paid`, повтор ключом → `replayed:true`. В `old` → 400 без кода.
  - Оплата долга: 400 `shift_closed`/`shift_not_found`, 403 `shift_not_owned`, 400 `field_conflict`, `amount_exceeds_debt`.
  - `custom-item`/checkout с ценой < 0 → 400 `negative_price`. В `old` — 201 и строка 0.
  - Ошибки чекаута с `code` и `errors[{index}]`.
  - `rentals/` → `{count,next,previous,results}` (страница 2 при `limit`).
  - `deals/{d}/pay/` поставщика + `cashflow_id`; действие `no-cashbox` → 400 `cashbox_unresolvable`.
  - `recommendations` со статусами `out_of_stock`/`no_sales`.
  - `features/activate/` → 409 `key_already_used`, 200 `replayed`, 429.
  - `debtors`/`with-debts` без поставщиков.
- `mock_selftest.py` проверяет оба режима.

### 4.2. Стенд кассы `C:\Users\User\NurMarket-test\stands\api-caps\` (создаёт исполнитель пакета, образцы — `bg-queue-q1`, `receiving-b1`, `revision-r2`)
- Свой exe `ApiCapsStand.exe` (переименован). Сборка — под общим замком `Global\NurMarketBuildLock` (`build.ps1` по образцу). Библиотеки берутся из `bin\Debug` проектов репозитория (их собирает `build-locked.ps1`).
- Настоящий DI и настоящий `NurMarketApiClient` на `http://127.0.0.1:<порт>/` подменного сервера. Прокси на `127.0.0.1:9`, чтобы в интернет ничего не ушло.
- **Все хранилища — в песочнице:** `DatabaseService` (через статические поля, как в `bg-queue-q1`), `ServerCapabilities.DataDirOverride`, `SupplierDebtRecorder.DataDirOverride`, `BackgroundReceivingQueue.DataDirOverride`, журнал возвратов, поправки ящика, `AppMode.DataFolderOverride`. Подменять одну переменную `APPDATA` бесполезно: `GetFolderPath` её обходит.
- `run.ps1` снимает SHA256 файлов настоящего профиля (`NurMarketKassa`, `NurMarketOwner`, `NurMarketShared` в `APPDATA` и `LOCALAPPDATA`) до и после прогона. Если хоть один файл изменился, стенд провален.
- **Главное окно кассы не поднимать.** Окна-диалоги — только если пакет этого требует, по hwnd своего процесса.

### 4.3. Критерии приёмки (каждый — в двух режимах: `old` и `new`)
1. **Старый режим — ничего не изменилось.** Те же запросы, что до правок: сравнение `requests.jsonl` по путям и телам на одном сценарии. Исключения — пробный GET `idempotency/<uuid>` раз в 6 ч и возвраты через `RequestAsync`.
2. **Новый режим — обходы сняты.**
   - Приход 10 строк с новой закупкой: 1 проверочный GET цены вместо 10, ни одного «восстанавливающего» PATCH.
   - Отчёт смены: строки раскладки от сервера, строки «Корректировка по данным сервера» нет, сумма = `expected_cash`, предоплата не посчитана дважды.
   - «Остаток по системе» = `drawer_cash`.
3. **Переключение на лету.**
   - `old → new` посреди сессии: касса подхватывает признаки без перезапуска.
   - `new → old`: первый же ответ без признака или 404 «адреса нет» возвращает старый путь. Цена после прихода снова восстанавливается, двойного долга и двойного расхода нет.
4. **Ошибки — понятный текст на 5 языках, без `code:` и `required:`.** Проверить коды: `permission_denied` (с каждым `required`), `shift_closed`, `shift_not_found`, `shift_not_owned`, `refund_exceeds_paid`, `negative_price`, `variant_required` (с названием товара по `index`), `insufficient_stock`, `price_below_cost`, `cash_less_than_total`, `mixed_sum_mismatch`, `too_many_items`, `cashbox_unresolvable`, `key_already_used`, `amount_exceeds_debt`, `field_conflict`. Неизвестный `code` — общий текст и `detail`, если он есть. Сырой ответ — в журнале.
5. **Эвристики по подстроке дают тот же результат, что до правок**, на наборе тел старого и нового вида: таблица «тело → решение» в выводе стенда.
6. **Деньги.**
   - 409 `idempotency_conflict` в быстром чекауте не создаёт второй продажи и не уходит в очередь.
   - Повтор возврата (`replayed`) не дописывает журнал.
   - Наличная оплата долга без серверной смены в кассе запрещена.
   - Счётчики сервера (`/__control/export`) сходятся с журналом кассы.
7. Сборка `dotnet build NurMarketKassa.Avalonia/NurMarketKassa.Avalonia.csproj -c Debug -v q -m:1 -nodeReuse:false` проходит без новых предупреждений. `dotnet test` **не запускать**.

---

## 5. Пакеты для Sonnet

Общие правила для всех пакетов:
- минимальный дифф в стиле кода;
- комментарии по-русски с датой и причиной (`// 2026-10-26: …`);
- все новые надписи — `Tr.T(ru, ky, en, tr, uz)`;
- **не трогать** схему БД, зависимости, `*.csproj`, сборку, `Directory.Build.targets`;
- секреты не печатать.

Перед началом: `git status` и проверка других сессий (ListAgents). Файлы, изменённые в рабочей копии другими задачами (`PurchasePaymentService.cs`, `MainWindow.axaml.cs`, `WarehouseWindow*`…), правятся только поверх текущей версии. Коммит — только по просьбе владельца. Пакеты одного шага не имеют общих файлов. Если файл нужен двум пакетам, он закреплён за одним, это указано.

### П0. Подменный сервер: режимы old/new (тестовая обвязка, не код продукта)
- **Файлы:** `C:\Users\User\NurMarket-test\stress\mock\mock_nurcrm.py`, `mock_core.py`, `mock_selftest.py`.
- **Задача:** раздел 4.1.
- **Приёмка:** `mock_selftest.py` зелёный в обоих режимах; `--api old` даёт те же ответы, что до правки (сравнить на сценарии из `plan_replay.py`).
- **Нельзя:** слушать что-либо кроме 127.0.0.1, ходить в интернет.
- Риск низкий.

### П1. ServerCapabilities и разбор ошибок (ПЕРВЫЙ, от него зависят П2–П7)
- **Файлы:**
  - новые: `Inf\ServerCapabilities.cs`, `Inf\ServerErrorTexts.cs`;
  - правка: `Inf\ApiException.cs`, `Inf\ApiErrorParser.cs`, `Inf\PaymentErrorMessages.cs`, `Inf\Api\ClientDebtsApiService.cs` (только `IsEndpointMissing` → вызов общего), `Inf\NurMarketApiClient.cs` (только: после входа один раз `ServerCapabilities.ProbeAsync` в фоне; где именно — в `SendOnceAsync` не встраивать).
- **Как есть.**
  - `ApiException` знает только `StatusCode` и `Payload`.
  - `ApiErrorParser` берёт `detail`, иначе склеивает пары: кассир видит `code: permission_denied; required: can_view_products`.
  - Признаки возможностей разбросаны: `_summaryMissing`, `_batchMissing`, `_serverPayDebtMissing`…
- **Как должно быть.**
  1. `ServerCapabilities` по п. 1.2. Пока **никуда не встраивать**, кроме пробы после входа и `IsEndpointMissing`. Встраивание — в П2–П7.
  2. `ApiException` получает вычисляемые свойства из `Payload`, конструктор не меняется:
     - `Code` (`code`, иначе `error_code`);
     - `Required`;
     - `Errors` (список `{index, code, field, detail}` из `errors[]`);
     - `DataMax`, `Available` (из `data`).
  3. `ServerErrorTexts.ForCode(string code, ApiException ex)` возвращает понятный текст на 5 языках для кодов из 4.3.4 или `null`. Для `permission_denied` по `required` — название раздела:
     - `can_view_products` — «Товары (Склад)»;
     - `can_view_clients` — «Клиенты»;
     - `can_view_cashbox` — «Финансы: кассы компании»;
     - `can_view_analytics` — «Аналитика»;
     - `can_view_employees` — «Сотрудники»;
     - `can_use_pos` — «Работа на кассе»;
     - `can_edit_products` — «Правка товаров»;
     - неизвестный флаг — его имя.
     Текст: «Нет доступа: «{раздел}». Попросите владельца включить доступ (NurCRM → Сотрудники).» Тексты **без** слов «смена» и «shift», кроме кодов про смену (см. 3.7).
  4. `ApiErrorParser.Parse`: если есть `code` и `ForCode` дал текст — сообщение = этот текст (плюс `detail` в скобках, если он есть и отличается). Иначе — как сейчас. Полное тело — в `PosLogger` с категорией `API`.
  5. `PaymentErrorMessages.ForCashier`: перед `api.Message` проверить `Code` (порядок существующих особых случаев не менять).
- **Приёмка:** 4.3.4, 4.3.5; стенд П1 гоняет таблицу тел «старый/новый вид → текст и решение классификаторов».
- **Нельзя:** менять `RequestDataAsync`, статусы повторов, порядок веток в `PaymentErrorMessages`.
- **Риск** средний (тексты везде); денег не касается.

### П2. Чекаут: коды ошибок, 409, «Расход» (деньги → `/code-review`)
- **Зависит от:** П1, решения по 3.1.
- **Файлы:** `Inf\Pos\PosCheckoutService.Quick.cs`, `Inf\SyncService.BatchReplay.cs`, `Inf\SyncService.QuickReplay.cs`, `App\Services\ReceiptHistoryService.cs` (`DescribeUploadError`), `Inf\Pos\StagingCartService.cs`, `Inf\Pos\QuickCheckoutBody.cs` (только комментарий и причина), `App\Views.axaml\MainKassir\MainWindow.Dialogs.cs` (кнопка «Оформить расход отдельно» — **только** если владелец выберет вариант (б)).
- **Как есть.**
  - 409 всегда временный (`IsQuickTransientStatus`).
  - Прочие 4xx молча уводят на старый путь.
  - `DescribeUploadError` ищет русскую фразу.
  - `Detail()` в batch читает только `detail`.
  - «Расход» уходит в `custom-item` с минусом.
- **Как должно быть.**
  - 409 с `Code` ∈ {`idempotency_conflict`, `total_mismatch`, `refund_exceeds_paid`} — окончательный отказ: без повтора, без очереди, **без старого пути**. Кассиру: «Чек с этим ключом уже проведён сервером с другим составом — проверьте «Историю чеков»».
  - 409 без `code` или с `idempotency_in_progress` — как сейчас.
  - 4xx с `Code` ∈ {`variant_required`, `variant_not_found`, `product_not_found`, `insufficient_stock`, `price_below_cost`, `cash_less_than_total`, `mixed_sum_mismatch`, `too_many_items`, `negative_price`} — **не** уходить молча на старый путь (он ответит тем же). Показать текст, а по `errors[].index` — название строки чека.
  - `too_many_items` — старый путь можно (он по одной позиции).
  - Офлайн-досылка: строка batch/quick с таким кодом — в «Некорректные чеки» с понятным текстом.
  - «Расход»: перед отправкой, если в чеке есть строка с ценой < 0, а `NegativeCustomRejected == New` (первый 400 `negative_price` ставит флаг) — сделать то, что решит владелец. До решения: понятный отказ «Сервер больше не принимает «Расход» внутри чека. Уберите строку и оформите расход отдельно (Касса → Изъятие)».
  - `expected_total` **не** добавлять.
- **Приёмка:** 4.3.4, 4.3.6 (в режиме `new` 409 `idempotency_conflict` → одна продажа на сервере); чек с «Расходом» в `old` проходит как сейчас, в `new` — понятный отказ без зависания корзины.
- **Нельзя:** менять расчёт итога, скидок, сборку тела чека.
- **Риск:** высокий (деньги).

### П3. Возвраты и списки продаж (Н-02, Н-07, Н-28) (деньги → `/code-review`)
- **Зависит от:** П1.
- **Файлы:** `Inf\Api\SalesApiService.cs` (только методы возврата `:838`, `:875` и листание `PosSalesListAsync :251-365`), `Inf\Api\SalesApiService.Exchange.cs` (`:84`), `Inf\Api\ISalesApiService.cs` (если меняется тип ответа), `App\Services\AccountCatalogIsolation.cs` (`PosRefundService` возвращает `JsonElement?`), `App\Views.axaml\Dialogs\ReturnSaleDialog.axaml.cs`, `Inf\Api\NurCrmReportsApi.cs` (**только** `ListReturnsAsync`), `Inf\SaleReturnLedger.cs` (только если нужен признак `replayed`).
- **Как есть:** раздел 2.1 (Н-02), 3.5.
- **Как должно быть.**
  - Возвраты — через `RequestAsync` (`ApiException` с телом).
  - Ответ доходит до окна.
  - Есть `refund{}` → суммы «наличными / на карту / долг уменьшен» из него. Старый `ServerReturnFiguresAsync` вызывается только если `refund{}` нет. `ReturnSaleDialog.ResolveRefundAsync` с его правилом «сервер больше — пометка» не трогать.
  - `replayed:true` → журнал не дописывать, событие смены не дублировать.
  - 409 или 400 с `refund_exceeds_paid` → текст «По этому чеку уже вернули всё, что было оплачено» и перечитать чек. Ключ забывается, как сейчас при 4xx.
  - Листание: ошибка или 404 на странице >1 означает конец списка, полученное сохраняется (`ListReturnsAsync`, `PosSalesListAsync`).
- **Приёмка:** чек 80−5: возврат строки 30 → 28,13, затем «весь чек» → 46,87, третий → понятный отказ (режим `new`); в `old` суммы как сейчас; повтор тем же ключом не дублирует журнал; обрыв связи — как сейчас.
- **Нельзя:** менять `ComputeRefundSplit` и расчёт доли скидки кассой (это запасной путь для `old`), `ExchangeCashCorrections.cs` (закреплён за П5а).
- **Риск:** высокий.

### П4. Оплата долга клиента (Н-13) (деньги → `/code-review`)
- **Зависит от:** П1.
- **Файлы:** `App\Services\DebtPaymentRunner.cs`, `App\Services\DebtPaymentFlow.cs`, `App\Views.axaml\Dialogs\PayDebtDialog.Method.cs`, `App\Views.axaml\Dialogs\AcceptDebtPaymentWindow.cs` (только тексты ошибок; `shift` у владельца **не** добавлять).
- **Как должно быть.**
  - `RejectedText` по `Code`:
    - `shift_closed`/`shift_not_found` → «Смена закрыта на сервере — откройте смену и повторите»; после этого обновить состояние смены, как при «Смена закрыта сервером»;
    - `shift_not_owned` → «Смена открыта другим кассиром — войдите под своим именем или откройте свою смену»;
    - `amount_exceeds_debt` → «Сумма больше долга. Максимум: {data.max}»;
    - `no_debt` — как сейчас;
    - `field_conflict` → общий текст и журнал.
  - `IsShiftClosedError`: сначала `Code`, потом прежняя подстрока.
  - 409 `idempotency_conflict` на первой попытке — не «Rejected» с удалением записи, а «исход неизвестен» (ветка `Unknown`, замок и «Повторить тем же ключом»).
  - В кассе (`PayDebtDialog.Method.cs`) наличный способ без серверной смены (`ActiveShiftId` пуст или `offline-…`) — запрет с текстом (3.2). Безнал — можно.
- **Приёмка:** на стенде коды из списка дают нужные тексты; оплата наличными без смены в кассе невозможна; в программе владельца поведение прежнее (без `shift`).
- **Нельзя:** менять ключи, очередь, файловый замок, `debt-payments-unknown.json`.
- **Риск:** высокий.

### П5а. Отчёт смены и раскладка ящика (Н-05, Н-01 Split) (деньги на экране → `/code-review`)
- **Зависит от:** П1.
- **Файлы:** `Inf\Models\ServerShiftReport.cs`, `Inf\Models\ShiftModel.cs`, `App\Services\DrawerBreakdownBuilder.cs`, `App\Views.axaml\Dialogs\ShiftDetailsDialog.axaml.cs`, `App\Views.axaml\Dialogs\CloseShiftDialog.axaml.cs`, `App\Services\ExchangeCashCorrections.cs`, `App\Services\ShiftCashOperationsStore.cs` (только применение поправок), `App\Services\ServerSalesSummary.cs` (только `Split`).
- **Как должно быть.**
  - Разбор новых полей и списка `cash_in_drawer_breakdown` (`code`, `amount`).
  - Если список есть, `DrawerBreakdownBuilder` строит строки **только** из него. Подписи по кодам на 5 языках:
    - `opening` — «Начальная сумма»;
    - `cash_sales` — «Наличные продажи»;
    - `prepayments_cash` — «Предоплата по долгам (наличные)»;
    - `debt_payments_cash` — «Оплата долгов наличными»;
    - `returns_cash` — «Возвраты»;
    - `deposits` — «Внесения»;
    - `withdrawals` — «Изъятия»;
    - `expected_cash` — итог;
    - неизвестный код — сам код.
  - «Корректировка…» — только если сумма ≠ `expected_cash`.
  - Списка нет → как сейчас.
  - Плитки и печатный Z: `cash_sales` прежний (с предоплатой), подпись «в т.ч. предоплата» — из `prepayment_total`, если оно есть.
  - `ShiftModel.RevenueNoDebt` и `CreditSales` берутся из `revenue_excl_debt` и `credit_sales_total`, если они пришли.
  - Поправки — п. 1.3.
  - `Split()`: элемент `debt` в `payment_methods` не вычитать из выручки второй раз (проверить на стенде в обоих режимах).
- **Приёмка:** 4.3.2 (отчёт); чек 4 сом (предоплата 1, долг 3) + оплата долга 3 + возврат 1 → в `new` раскладка = `expected_cash` без корректировки, в `old` — как сейчас.
- **Нельзя:** менять `CashShiftService.BuildReportText` (печать при закрытии), формулы `old`.
- **Риск:** высокий.

### П5б. Открытие смены и автозакрытие (Н-06, Н-09)
- **Зависит от:** П1. Не пересекается с П5а.
- **Файлы:** `Inf\Api\ShiftApiService.cs` (`cashboxes/`: `drawer_cash`), `Inf\ShiftBalanceHelper.cs` (новый метод чтения `drawer_cash`; `TryReadBalance` не трогать), `App\Views.axaml\MainKassir\MainWindow.axaml.cs` (`OpenShiftAsync` и плашка смены — только эти места), `Inf\Pos\ShiftStateService.cs`, `Core\Contracts\IShiftStateService.cs` (`ShiftAgeWarning`, `ShiftClosedByServer` + `ClosedBy`, `CloseReason`), `App\Services\ServerShiftTexts.cs`.
- **Как должно быть.**
  - «Остаток по системе»: сначала `drawer_cash` кассы `App.PosCashboxId` (если поле есть и роль кассы `pos_*`), иначе прежний путь.
  - Плашка: `auto_close_at`, если пришёл. `ShiftAutoClose` из `app-settings/nurmarket-mobile/` (только чтение, кэш 10 мин): `enabled=false` → плашки нет, `hours` → пороги «за 2 ч, за 1 ч, за 15 мин».
  - Закрыта сервером с `close_reason:"auto_24h"` → «Сервер закрыл смену автоматически ({hours} ч) в ЧЧ:ММ».
- **Приёмка:** в `new` «Остаток по системе» = `drawer_cash`, плашка по `auto_close_at`; в `old` — как сейчас; `ShiftAutoClose.enabled=false` в настройках подменного сервера → плашки нет.
- **Нельзя:** писать в app-settings, менять таймер 90 с.
- **Риск:** средний.

### П6. Закупки, цены, поставщики (Н-04, Н-03, Н-11, Н-30) (деньги → `/code-review`)
- **Зависит от:** П1. Файлы `PurchasePaymentService.cs` и `PurchasesWindow*` изменены в рабочей копии: начинать после приёмки текущих правок Q4.
- **Файлы:** `Inf\PurchaseReceivingService.cs` (только `RestoreSalePricesAsync` и разбор ответа PATCH), `App\Services\PurchasePaymentService.cs` (только `CheckServerExpenseAsync`, сравнение `shift`; текст 403 в `CompanyExpenseAsync`), `Inf\Api\SupplierApiService.cs` (`PayInstallmentAsync` возвращает ответ; `GetRecommendationsAsync` — точное сопоставление статусов), `App\Views.axaml\Main\SupplierCardView.cs`, `App\Views.axaml\Main\PurchasesWindow.Recommend.cs` (подписи — только если нужен новый статус).
- **Как должно быть.**
  - Выборочная проверка цены (п. 1.3), `Observe(ProductPriceStable)` по `price_changed` в ответе PATCH.
  - `shift` строкой или `{id}`.
  - Оплата взноса поставщику: `cashbox_unresolvable` → текст; `HttpRequestException` → «исход неизвестен — проверьте на сайте, прежде чем платить снова»; есть `cashflow_id` → без проверки расхода.
  - `out_of_stock` → «Нет в наличии», `no_sales` → «Нет продаж».
- **Приёмка:** приход 10 строк в `new` — 1 проверочный GET, в `old` — восстановление цены как сейчас; переключение `new → old` посреди приёмки возвращает полную проверку.
- **Нельзя:** менять `SupplierDebtRecorder`, `BackgroundReceivingQueue`, состав тела прихода, `create_deal` (П8).
- **Риск:** высокий.

### П7. Прочее: прокат, витрина, Маркетплейс, `can_use_pos`
- **Зависит от:** П1.
- **Файлы:** `Inf\Api\RentalsApi.cs`, `Inf\Api\ShowcaseApiService.cs`, `Inf\TelegramAiChat.cs` (только ответ покупателю при `variant_out_of_stock`/`variant_required`), `App\Views\Settings\MarketplaceView.axaml.cs` (`UnlockPackAsync`), `App\Services\PermissionService.cs`.
- **Как должно быть.**
  - Прокат: листать по `next` (до 10 страниц); `Describe` — `detail`, затем `code`, затем первое строковое поле.
  - Витрина: текст покупателю с названием и `available`.
  - Ключ:
    - отказы сервера (`key_used`, `key_already_used`, 409, 400) — текст, **без** локальной проверки;
    - 429 — текст «подождите час»;
    - локальная проверка — только при обрыве или 5xx;
    - `replayed` = успех.
  - `can_use_pos == false` (явно) → касса не открывает смену и не продаёт: «Работа на кассе выключена владельцем». Нет поля — как сейчас.
- **Приёмка:** 4.3.4 по этим кодам; 600 прокатов в `new` — видны все.
- **Нельзя:** менять серийные ключи, `TryValidateSerial`.
- **Риск:** средний (лицензии).

### П8. Новые возможности — после выкатки сервера (отдельные ТЗ, по одному пакету)
Порядок по пользе:
1. `idempotency/{key}` в `BackgroundReceivingQueue` (`FindPostedReceiptAsync`) и в `DebtPaymentRunner.ResolveAsync` — деньги, `/code-review`.
2. `create_deal` (Н-10) с проверкой `deal_id` — деньги.
3. Сводка «Нал / Безнал / Предоплата / В долг» (Н-01).
4. `pay-supplier-debt` (Н-11) — деньги.
5. Зарплата `payouts` (Н-18) — деньги.
6. Владелец бота на сервере (Н-23).
7. Клиентские документы (Н-22, вместе с мобилкой, с миграцией).
8. Коды доступа и сброс пароля на сервере (Н-16, миграция хэшей).
9. Упаковки в быстром чекауте (Н-15).
10. `products/barcode` (Н-33).
11. Лента изменений и WebSocket (Н-31).
12. Условная запись app-settings (Н-17).

Каждый пакет включается только при `ServerCapabilities == New` для своей функции; старый путь остаётся запасным.

### Мобилка (отдельные пакеты, выпуск — свой ключ и свой репозиторий выпусков, см. память `mobile-release`)
- **М1. Ошибки и 409** (деньги → `/code-review`).
  - Файлы: `NurMarketMobile\lib\core\api\api_client.dart`, `lib\features\payment\sales_repository.dart`, `lib\core\sync\sync_service.dart`, `lib\l10n\app_{ru,ky,en,tr,uz}.arb`.
  - `ApiException` получает `code`, `required`, `errors` из `payload`.
  - `_describe`: `code` → строка из l10n (тот же список, что в П1).
  - `isTransient`: 409 с `code` ∈ {`idempotency_conflict`, `total_mismatch`, `refund_exceeds_paid`, `key_already_used`, `shift_*`, `field_conflict`} — **не** временный: продажа не ставится в очередь и остаток не уменьшается. 409 без `code` — как сейчас.
  - Приёмка: подменный сервер в режиме `new` и телефон или эмулятор; продажа при 409 `idempotency_conflict` → одна продажа, без очереди.
- **М2. Долг и возврат.**
  - Файлы: `lib\features\ops\ops_repository.dart`, `lib\features\ops\ops_screens.dart`.
  - `shift_closed`/`shift_not_found`/`shift_not_owned` → обновить смену (`shiftProvider.refresh`) и предложить открыть новую.
  - Возврат: читать `refund{}`, если он есть; `refund_exceeds_paid` → текст.
  - Наличная оплата долга без серверной смены — запрет (как П4).
- **М3. Автозакрытие смены.**
  - Файлы: `lib\features\shift\shift_repository.dart`, `lib\features\admin\admin_repository.dart`, `lib\features\admin\admin_screens.dart`.
  - Показ `closed_by`/`close_reason` («закрыта автоматически»), предупреждение по `auto_close_at` или `ShiftAutoClose`.
- Н-04 в мобилке — без правок (обход безвреден). Н-14 — без правок. Н-21 — без правок.

### Порядок и зависимости

```
П0 (стенд) ─┐
П1 ─────────┼─> П2, П3, П4, П5а, П5б, П6, П7   (параллельно, общих файлов нет)
            │
М1 ─────────┴─> М2, М3
П2..П7 приняты → выпуск 1.17.61 → ответ Умару «касса готова» → выкатка сервера ночью → П8 по одному
```

Риски по пакетам:
- высокий: П2, П3, П4, П5а, П6, М1 — все через `/code-review`;
- средний: П1, П5б, П7;
- низкий: П0.

---

## 6. Проект ответа Умару (можно скопировать)

> Умар, привет! Спасибо за большую работу: прочитали все 33 пункта и сверили с кодом кассы, программы владельца и мобилки.
>
> **1. Что касса выдержит и что поправим.** Мы делаем кассу так, чтобы она работала и со старым, и с новым API. Новые поля читаем, если они есть, и старый путь держим запасным. Поэтому выкатка ничего не сломает, кроме одного места (п. «Расход» ниже).
> - **Выдержит без правок:** Н-01, Н-04 (наш обход «вернуть цену» станет холостым, уберём его по полю `price_changed`), Н-05 (старый `cash_sales` не меняли — спасибо), Н-06, Н-09, Н-14 (`include_suppliers` нам не нужен: поставщиков берём из `clients/?type=suppliers`), Н-21 (пока прокатов меньше 500), Н-27, Н-28, Н-30, Н-33.
> - **Выдержит, но с сырым текстом ошибок** (поправим к 1.17.61): Н-02 (409 `refund_exceeds_paid`), Н-11 (`cashbox_unresolvable`), Н-13 (`shift_*`), Н-15 (коды чекаута), Н-16 (403 `permission_denied`), Н-26 (`variant_out_of_stock`), Н-32 (`key_already_used`).
> - **Сломается:** Н-15 `negative_price`. В кассе есть «Расход» внутри чека: строка «Доп. услуга» с минусом. Она идёт через `pos/carts/{id}/custom-item/`. Сейчас сервер её принимает, но в продаже считает как 0. После выкатки будет 400, и такой чек не оплатится. Вопрос: как правильно провести «Расход» в чеке? Умеет ли это сайт? Варианты: скидкой на чек; отдельным расходом из ящика смены; новым видом строки в чекауте. До ответа касса сама не даст отправить такой чек и предложит оформить расход отдельно.
>
> Версия кассы с правками — **1.17.61**, мобилки — **1.18.5**. Напишем, когда выложим. Просим выкатывать после этого, **ночью, когда все смены закрыты**.
>
> **2. Оплата долга без `shift` (Н-13).** Касса кассира передаёт `shift` всегда, когда у неё открыта серверная смена. Не передаёт только две вещи:
> - (а) программа владельца — намеренно: владелец принимает деньги «в руки», не в ящик кассира;
> - (б) касса в первые секунды после офлайн-открытия смены (id смены ещё локальный). Это мы закроем у себя: наличные без серверной смены касса принимать не будет.
>
> Поэтому **делайте «без `shift` — вне смены»**, как в ТЗ. Деньги не должны попадать ни в чью открытую смену. Нужно ещё необязательное поле `cashbox`. Без него, пожалуйста, используйте «Основную кассу компании» и напишите письменно, какая касса выбирается. `shift_not_owned` для владельца не применять — так и сделано, спасибо. Мобилка тоже передаёт `shift`, когда смена серверная.
>
> **3. 409 или 422 для конфликта ключа.** Оставьте **409 `idempotency_conflict`**. Касса и мобилка считают 409 «исход неизвестен, повторить тем же ключом». А любой другой 4xx касса воспринимает как отказ и уходит старым путём, то есть 422 привёл бы ко второй продаже. Если когда-нибудь добавите «запрос с этим ключом ещё выполняется», сделайте его тоже 409, с другим кодом (`idempotency_in_progress`) и `Retry-After`.
>
> **4. NurRemoteSupport.** Уберём у себя:
> - `Server/Relay.cs` и `Server/Peer.cs` (подтвердили: с ними сборка падает с CS1061, без них проходит);
> - `Server/IpLimiter.cs` (нужен только `Relay.cs`);
> - второй проект `Server/NurRemoteSupport.Server.csproj` (старая ветка 16.10; два проекта в одной папке делят `obj/`);
> - корневые `Dockerfile` и `docker-compose.yml` (собирают старый проект, ключ `OPERATOR_KEY`);
> - `wwwroot/operator.js` и `operator.css`, а также `SmokeTest` (они тоже от старой ветки).
>
> Оставим `deploy/` (`Server.csproj`). README перепишем: операторы — через `add-operator`, порт — `ASPNETCORE_URLS`. Патч смены пароля перенесём. **Файл `NurRemoteSupport — смена пароля оператора (25.10.2026).patch` до нас не дошёл** — пришли его, пожалуйста, ещё раз. И напиши, от какого состояния он сделан: от нашего репозитория или от копии на сервере. А также каким Dockerfile или compose собран `support.nurcrm.kg` сейчас. Сервер отвечает: `https://support.nurcrm.kg/health` → `ok`. С настоящей кассы «Получить код» проверим сами.
>
> **5. Вопросы к вам.**
> - (1) **`expected_cash`** открытой смены после выкатки пересчитывается заново по операциям или копится как раньше? У кассы есть свои поправки для известных ошибок старого счёта (предоплата долга, обмен). Если сервер пересчитает смену, начатую до выкатки, поправки учтутся дважды. Поэтому и просим выкатку при закрытых сменах.
> - (2) **`charts.payment_methods`** в `analytics/market/?tab=sales`: раскладывается ли теперь «Смешанная» по долям? Есть ли элемент `debt`? Равна ли сумма элементов без `debt` значению `cards.revenue`?
> - (3) **Признак нового API:** можно ли добавить `GET api/main/capabilities/` (список включённых возможностей) или заголовок версии API в ответах? Пока определяем по `GET main/idempotency/<случайный uuid>/`: новый сервер отвечает 404 **с JSON** `{"code":"not_found","found":false}`, старый — 404 HTML. Подтвердите, что JSON будет именно таким.
> - (4) **403 `permission_denied`**: приходит ли рядом `detail` по-русски? Тексты на 5 языков касса делает сама по `required`, нам достаточно `code` и `required`.
> - (5) **`can_use_pos`** приходит в `users/profile/` у сотрудника явным полем?
> - (6) **`create_deal:true` (Н-10):** при обрыве связи и повторе тем же ключом — тот же `deal_id`, без второй сделки? И будет ли `deal_id` в `GET suppliers/receipts/`?
> - (7) **Отрицательная цена в офлайн-досылке** (`offline:true`): тоже 400, или досылка проходит с `warnings`?

---

## 7. Что запросить у владельца

1. **Решение по «Расходу» в чеке** (3.1): (а) скидка на чек, (б) отдельное изъятие из ящика (вариант Opus), (в) ждать Умара.
2. **Файл патча** `NurRemoteSupport — смена пароля оператора (25.10.2026).patch` (в репозитории, «Загрузках» и на рабочем столе его нет). Также:
   - от какого состояния он сделан;
   - какой `Dockerfile` или `compose` стоит на `support.nurcrm.kg`;
   - разрешение удалить из репозитория перечисленные в 6.4 старые файлы (удаление — только по его слову).
3. **Проверка удалённой поддержки с настоящей кассы** — делает сам владелец, когда ему удобно:
   - Касса → «Тех. поддержка» → «Получить код». Должны появиться код и PIN.
   - Коду и PIN никому не давать, «Разрешить» не нажимать, окно закрыть.
   - В журнале `%LOCALAPPDATA%\NurMarketKassa\Logs\nurmarket-kassa.log` будут строки «Удалённая помощь: …». Ошибки соединения на этом шаге быть не должно.
   - Файла `remote-relay.txt` на этом ПК нет, значит касса идёт на боевой `wss://support.nurcrm.kg/ws/host`.
   - Стенд `NurRemoteSupport\Tools\HostHarness` для боевого сервера **не использовать**: он сам нажимает «Разрешить» и показывает настоящий экран.
   - Вход оператора в этой проверке не нужен.
4. **Согласие на версии:** касса 1.17.61, мобилка 1.18.5. Выкатку сервера ставим после их выпуска.
5. **Порядок работ:** П0 и П1 сразу, затем П2–П7 (П6 — после приёмки текущих правок закупок Q4 в рабочей копии).

## 8. NurRemoteSupport — точные шаги (после согласия владельца и получения патча)
1. Удалить:
   - `NurRemoteSupport\Server\Relay.cs`, `Peer.cs`, `IpLimiter.cs`, `NurRemoteSupport.Server.csproj`;
   - `NurRemoteSupport\Server\wwwroot\operator.js`, `operator.css`;
   - `NurRemoteSupport\Dockerfile`, `NurRemoteSupport\docker-compose.yml` (корневые; рабочие — в `deploy\`);
   - `NurRemoteSupport\SmokeTest\` (проверяет старую ветку: `OPERATOR_KEY`, `operator.js`). Для проверки остаётся `Tools\RelayTests` — он ищет `Server\Server.csproj` и `Server.dll`.
2. Почистить `Server\obj` (там общий `project.assets.json` двух проектов).
3. Сборка: `dotnet build NurRemoteSupport/Server/Server.csproj -c Release -v q`.
   - Проверено 26.10 на копии в песочнице: без `Relay.cs` и `Peer.cs` — «Build succeeded»; с ними — ошибки CS1061 (`RelayOptions` не содержит `MaxTextBytes`, `MaxHosts`, `PinLength`…).
4. Патч: `git apply --check "<путь к .patch>"`, затем `git apply`. Затронуты `Server\Program.cs`, `Server\wwwroot\index.html`, `app.js`, `app.css`. Если `--check` не проходит (патч сделан от копии сервера), запросить у Умара сами четыре файла в том виде, как они работают на `support.nurcrm.kg`, и сравнить вручную.
5. README (`NurRemoteSupport\README.md`, `deploy\README.md`):
   - операторы — `dotnet Server.dll add-operator <login> "<Имя>"` (пароль со stdin);
   - смена пароля — кнопка «Пароль» на сайте оператора (`POST /api/password`);
   - порт — `ASPNETCORE_URLS`;
   - убрать `OPERATOR_KEY`, `BIND`, `PORT`;
   - таблица файлов без `Relay.cs`.
   Паролей и токенов в README не писать.
6. Проверка: `Tools\RelayTests` против локального `Server.dll` (вход, смена пароля, неверный текущий пароль считается вместе с ошибками входа). На `support.nurcrm.kg` — только `GET /health`.
7. Коммит — только по просьбе владельца.
