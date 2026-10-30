# NurCRM Kassa (раньше NurMarket Kassa) — документ передачи работы для ИИ-агентов

Состояние на **28.09.2026, утро**. Последний выпуск — **v1.17.22** (коммит `7941c67`, ветка `main`, опубликован на GitHub официальным). Этот файл — первое, что нужно прочитать новому агенту: что это за проект, где что лежит, как собирать, проверять и выпускать, какие правила у владельца, что сделано, что сломано и что в работе.

---

## 1. Что это за проект

**Название (29.10.2026).** По решению владельца программа называется NurCRM («Нур СРМ»): видимое название везде NurCRM Kassa / NurCRM Владелец. Технические идентификаторы остались NurMarket* (packId Velopack `NurMarketKassa`, проекты и пространства имён, имена файлов выпуска, applicationId Android, папки данных `NurMarketKassa`/`NurMarketOwner`, mutex, журналы, репозитории GitHub) — не менять, иначе сломаются автообновления и данные. Ярлык владельца переименован в «NurCRM Владелец» (старый «NurMarket Владелец» переносится при запуске).
- **NurMarket Kassa** — кассовая программа для Windows (Avalonia 11.2.5, .NET 8) для магазинов в Кыргызстане. Работает поверх облачной CRM **NurCRM** (сайт https://nurcrm.kg, API `https://app.nurcrm.kg/`). Своего сервера у кассы нет: товары, продажи, смены, клиенты — всё в NurCRM.
- Один exe в двух режимах:
  - **Касса** (обычный запуск) — продажи, смена, возвраты, внесение/изъятие, оплата долга, печать, весы, экран покупателя.
  - **Программа владельца** — тот же exe с ключом `--owner` (`AppMode.IsOwner`). Разделы: Сводка, Склад, Продажи, Финансы, Аналитика, ABC-анализ, Клиенты, Зарплата, Маркетплейс, Настройки, База знаний. У неё отдельная папка данных (`NurMarketOwner`), чтобы две программы на одном ПК не мешали друг другу. Ярлык «NurMarket Владелец» создаёт хук Velopack при установке.
- Интерфейс на 5 языках: русский, кыргызский, английский, турецкий, узбекский. Бесплатны ru/ky, en/tr/uz — платный языковой пакет.
- Обновления — **Velopack** из релизов GitHub `beknazar93/Nurmarket` (канал `win`).
- Владелец — один человек, продакт и хозяин магазина. **Отвечать ему только по-русски**, просто и конкретно.

## 2. Где что лежит

### 2.1. Репозиторий

```
nurmarketDesctop/
├─ NurMarketKassa.Avalonia/                 ← папка решения (сборка отсюда)
│  ├─ NurMarketKassa.Avalonia/              ← ПРИЛОЖЕНИЕ: NurMarketKassa.Avalonia.csproj (это настоящий проект)
│  │  ├─ Program.cs, App.axaml(.cs)         ← вход, DI, ApplyMainLayoutMode
│  │  ├─ Views.axaml/MainKassir/            ← MainWindow (касса), Layouts/ (виды кассы)
│  │  ├─ Views.axaml/Main/                  ← окна: OwnerShellWindow, FinanceWindow, SalesWindow, ReceiptHistoryWindow…
│  │  ├─ Views.axaml/Dialogs/               ← диалоги: оплата, возврат, долг, смена, ценники, редактор тем…
│  │  ├─ Views.axaml/Controls/              ← CatalogPanelView, BasketPanelView, SideMenuView…
│  │  ├─ Views.axaml/Customer/              ← экран покупателя (второй монитор)
│  │  ├─ Views/Settings/                    ← страницы настроек и MarketplaceView
│  │  ├─ Services/                          ← сервисы уровня UI (темы, масштаб, клавиши, история чеков…)
│  │  ├─ Resources/Strings.{Ru,Ky,En,Tr,Uz}.axaml ← словари интерфейса
│  │  ├─ Assets/kb/                         ← база знаний (kb.json + снимки/GIF)
│  │  └─ release-notes-X.Y.Z.md             ← описания релизов (5 языков)
│  ├─ NurMarketKassa.Infrastructure/        ← API NurCRM, касса/смены/оплата, печать, выгрузки, LAN, Tr, UserPreferences
│  ├─ NurMarketKassa.ViewModels/            ← BasketPanelViewModel, MainStatusViewModel…
│  ├─ NurMarketKassa.Core/                  ← контракты (ICashShiftService, ICustomerDisplayService…)
│  ├─ NurMarketKassa.Ui.Shared/
│  ├─ NurMarketKassa.Tests/                 ← ТЕСТЫ. НЕ ЗАПУСКАТЬ (см. правила)
│  ├─ dev-out/                              ← локальная сборка для проверки (в .gitignore)
│  └─ releases/                             ← пакеты Velopack (в .gitignore)
├─ NurSupportBot/                           ← Telegram-бот поддержки (отдельно; appsettings.json с токеном НЕ коммитить)
├─ tools/qa/                                ← скрипты проверки (UI через hwnd, стресс, смены, зонд API, виртуальный принтер)
├─ tools/release/                           ← выгрузка релиза на GitHub
├─ docs/AI_HANDOFF.md                       ← этот файл
├─ token.txt                                ← токен GitHub (секрет, в .gitignore; значение НИКОГДА не печатать)
└─ AnyDesk.exe                              ← нужен сборке (копируется в выход), в .gitignore
```

### 2.2. Ключевые файлы по темам

| Тема | Файлы |
|---|---|
| Режимы касса/владелец | `Infrastructure/Services/AppMode.cs`, `Avalonia/Views.axaml/Main/OwnerShellWindow.axaml.cs`, `Avalonia/Views.axaml/IOwnerSection.cs` |
| Главное окно кассы | `Avalonia/Views.axaml/MainKassir/MainWindow.axaml(.cs)` (клавиши, закрытие, смена, раскладки) |
| Корзина и оплата | `ViewModels/Main/BasketPanelViewModel.cs`, `Avalonia/Views.axaml/Dialogs/CheckoutDialog.axaml.cs`, `Infrastructure/Services/Pos/PosCheckoutService.cs`, `.../Pos/StagingCartService.cs` |
| Без интернета | `Infrastructure/Services/OfflinePendingSalesStore.cs`, `Infrastructure/Services/SyncService.cs` |
| Синхронизация касса ↔ владелец ↔ сервер | `Infrastructure/Services/SyncCenter.cs` (интервалы, `ForceSyncAllAsync`, «Синхронизация: HH:mm:ss»), `SettingsCloudSync.cs` (опрос общих настроек), `RemoteSalesPulse.cs`, `Pos/CatalogFeedSync.cs` + `Pos/FeedStockGuard.cs`; раздел 13 |
| Обмен по локальной сети | `Infrastructure/Services/Lan/` (`LanSyncService`, `LanJournal`, `LanSalePublisher`, `LanStockAdjuster`) |
| Смены и Z-отчёт | `Infrastructure/Services/Pos/CashShiftService.cs` (в т. ч. долг смены), `Infrastructure/Services/Api/ShiftApiService.cs` (список смен, все страницы), `Avalonia/Services/ShiftHistoryService.cs`, `Avalonia/Services/ShiftReportData.cs`, `Avalonia/Views.axaml/Dialogs/ShiftDetailsDialog.axaml.cs` |
| Чеки и история | `Infrastructure/Services/SaleReceiptTextBuilder.cs`, `.../CartReceiptTextBuilder.cs`, `Avalonia/Services/ReceiptHistoryService.cs`, `Avalonia/Views.axaml/Main/ReceiptHistoryWindow.axaml.cs`, `Avalonia/Views.axaml/Dialogs/ReturnSaleDialog.axaml.cs` |
| Долги | `Avalonia/Views.axaml/Dialogs/PayDebtDialog.axaml.cs` (сделки и взносы NurCRM) |
| Списание | `Avalonia/Views.axaml/Dialogs/WriteOffDialog.axaml.cs` (использует `WarehouseViewModel.RunWriteOffAsync`) |
| Печать | `Infrastructure/Services/PrinterPortService.cs` (USB/WinUSB/COM/LPT/спулер), `.../BarcodeLabelService.cs`, `.../PriceTagService.cs` |
| Выгрузки Excel/Word | `Infrastructure/Services/AnalyticsExportService.cs`, `ExcelWorkbookBuilder.cs`, `WordReportBuilder.cs`, `ReportExportCommon.cs`, `AnalyticsReportData.cs` (данные — с сервера) |
| Аналитика владельца | `Avalonia/Views.axaml/Main/FinanceWindow.axaml.cs`, `SalesWindow.axaml.cs`, `AbcAnalysisWindow.axaml.cs`, `OwnerShellWindow.axaml.cs` (Сводка), `Avalonia/Services/SalesHistoryBackfill.cs` (локальная история) |
| API NurCRM | `Infrastructure/Services/NurMarketApiClient.cs`, `Infrastructure/Services/Api/*` (`SalesApiService`, `ShiftApiService`, `ApiThrottle` — защита от 429) |
| Виды кассы | `Avalonia/Services/KassaLayouts.cs`, `Avalonia/Views.axaml/MainKassir/Layouts/*`, выбор — `Avalonia/Views/Settings/ScreenSettingsView.axaml.cs` |
| Темы | `Avalonia/Services/AccentThemeService.cs`, `CustomThemeStore.cs`, `Avalonia/Views.axaml/Dialogs/ThemeEditorWindow.axaml.cs` |
| Масштаб | `Avalonia/Services/UiScaleHelper.cs` |
| Клавиши | `Avalonia/Services/PosHotkeyService.cs`, `Avalonia/Views/Settings/KeyBindingsSettingsView.axaml.cs` |
| Экран покупателя | `Avalonia/Views.axaml/Customer/CustomerDisplayWindow.axaml.cs`, `Avalonia/ViewModels/CustomerDisplayViewModel.cs`, `Infrastructure/Services/CustomerDisplaySettings.cs` |
| Маркетплейс (платные модули, темы) | `Avalonia/Views/Settings/MarketplaceView.axaml.cs`, `Infrastructure/Services/LicenseKeys.cs`, `Infrastructure/Services/TariffGate.cs` |
| Обновления | `Infrastructure/Services/VelopackUpdateService.cs`, `ReleaseNotesText.cs` (описание релиза → обычный текст), `AppChangelog.cs` («Что нового») |
| Переводы | `Infrastructure/Services/Tr.cs` (`Tr.T(ru, ky, en, tr, uz)`), `Avalonia/Resources/Strings.*.axaml` |
| Настройки | `Infrastructure/Services/UserPreferences.cs` |
| Голос (голосовое управление кассой) | **убрано 27.10.2026** (Vosk/SpeakerVerification/VoiceControlTestWindow удалены, есть ИИ-ассистент); ИИ-голос Нур Советника — `Hardware/GeminiLiveVoice.cs`, `VoiceChatAudio.cs`, `AiAdvisorWindow` — остался |
| Изоляция аккаунтов | `Avalonia/Services/AccountDataIsolation.cs` |
| Telegram-бот владельца | `Infrastructure/Services/TelegramReportBuilder.cs` |

(Пути выше — от `NurMarketKassa.Avalonia/`, префикс проекта сокращён: `Avalonia/` = `NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/`, `Infrastructure/` = `NurMarketKassa.Avalonia/NurMarketKassa.Infrastructure/` и т. д.)

### 2.3. Данные на компьютере

| Что | Где |
|---|---|
| Установка (Velopack) | `%LOCALAPPDATA%\NurMarketKassa\` — `current\` (файлы программы), `packages\` (скачанные пакеты), `Update.exe` |
| Журнал кассы | `%LOCALAPPDATA%\NurMarketKassa\Logs\nurmarket-kassa.log` |
| Журнал владельца | `%LOCALAPPDATA%\NurMarketOwner\Logs\nurmarket-kassa.log` |
| Локальная база кассы (SQLite) | `%APPDATA%\NurMarketKassa\data\pos_local.db` (владелец — `%APPDATA%\NurMarketOwner\data\`) |
| Настройки | `%APPDATA%\NurMarketKassa\user-settings.json` (в т. ч. `MainLayoutMode`, `LastSeenAppVersion`, `UiScalePercent`) |
| Свои темы | `%APPDATA%\NurMarketKassa\custom-themes.json` |
| Реестр соседей по локальной сети | `%LOCALAPPDATA%\NurMarketLan\peers`; порты TCP 47810–47819, UDP 47809 |

## 3. Правила работы (обязательно)

Это постоянные требования владельца. Нарушение уже стоило ему денег и времени.

1. **Никогда не запускать `dotnet test`.** Тесты `NurMarketKassa.Tests` ходят в боевую базу. Проверка — сборка + живая проверка на тестовом аккаунте.
2. **Секреты не печатать и не коммитить.** `token.txt`, токен в адресе `origin`, `docs/paid-serial-keys.md`, `NurSupportBot/appsettings.json`, JWT из ответов API. Вывод команд фильтровать (`grep -v -iE "token|bearer|ghp_|github_pat"`).
3. **Отвечать по-русски.** Все новые надписи в программе — на 5 языках (`Tr.T(ru, ky, en, tr, uz)` в коде, ключи в `Strings.*.axaml`).
4. **Бережные правки.** Минимальный дифф в стиле окружающего кода; не переписывать и не удалять работающее; не менять зависимости, схему базы, сборку без спроса; комментарии по-русски с датой и причиной (так написан весь проект). Перед разрушительными действиями — спросить.
5. **Проверять локально до выпуска.** Поставить сборку на этот компьютер и прогнать полный регресс: продажи (нал/безнал/смешанная/скидка/долг), оплата долга, возврат, внесение/изъятие, закрытие и открытие смены, стресс продаж, работа без интернета. Только потом GitHub.
6. **Продажи — только на тестовом аккаунте** (компания NBS; логины «Кассир Тест» и «Nur Test» — владелец; пароли у владельца, в файлы не записывать).
7. **Не мешать владельцу.** Он часто сам работает в кассе на этом ПК. Перед перезапуском/кликами смотреть журнал на свежие действия человека.
8. **UI-автоматизация — только в окна процесса кассы по hwnd** (`tools/qa`: PostMessage в окно, снимки через PrintWindow). Никогда не кликать по координатам экрана и не снимать весь экран: ввод однажды ушёл в VS Code.
9. **Не делать свою экранную клавиатуру** — только системная `osk.exe` (владелец дважды отверг свою).
10. **Коммитить и пушить — только когда владелец просит** (просьба «выгрузи обновление» = выпуск). Сообщения коммитов — по-русски; в конце строка соавторства, если её требует ваш инструмент.
11. **Описание каждого релиза**: «Новое», «Исправлено: было → стало» (таблица), «Проверено перед выпуском» — на 5 языках (образец — `release-notes-1.17.22.md`). Окно «Что нового» в программе — массивы `Latest*` в `AppChangelog.cs` (5 языков).

## 4. Сборка, установка, выпуск

### 4.1. Сборка и локальная установка

```bash
cd NurMarketKassa.Avalonia
dotnet publish NurMarketKassa.Avalonia/NurMarketKassa.Avalonia.csproj -c Release -r win-x64 --self-contained true -o dev-out -nologo -v q -m:1 -nodeReuse:false
```
- Если XAML-компилятор падает с `OutOfMemoryException` — `dotnet build-server shutdown` и повтор с `-m:1 -nodeReuse:false` (на ПК 7,8 ГБ памяти).
- Установить на этот ПК: `powershell -File tools/qa/deploy_both.ps1` — останавливает кассу и владельца, копирует базы в `%TEMP%`, копирует `dev-out` в `%LOCALAPPDATA%\NurMarketKassa\current`, запускает кассу и `--owner`. Метаданные Velopack (`current\sq.version`) при этом не меняются.
- Диск C: почти полон (~7–8 ГБ свободно). Удаляйте за собой `bin/obj` временных проектов и старые `release-*-out`.

### 4.2. Версия

- В `NurMarketKassa.Avalonia/NurMarketKassa.Avalonia.csproj` поднимать **одну строку `<Version>`**. `AssemblyVersion` и `FileVersion` берутся из неё (`$(Version)`). До 1.17.21 они были закреплены отдельно, и 1.17.19/1.17.20 вышли с внутренней версией 1.17.18 — касса вечно предлагала обновиться.
- Касса сравнивает с GitHub **версию сборки**, а не `sq.version`.

### 4.3. Выпуск на GitHub

1. Поднять `<Version>`, обновить `AppChangelog.cs` (`Latest`, `LatestKy`, `LatestEn`, `LatestTr`, `LatestUz`), написать `NurMarketKassa.Avalonia/release-notes-X.Y.Z.md`.
2. Собрать `dev-out`, поставить локально, регресс (раздел 5).
3. Коммит в `main`.
4. Упаковать:
   ```bash
   cd NurMarketKassa.Avalonia
   vpk pack --packId NurMarketKassa --packVersion X.Y.Z --packDir dev-out --mainExe NurMarketKassa.Avalonia.exe --packTitle "NurCRM Kassa" --packAuthors NurMarketKassa --icon NurMarketKassa.Avalonia/Assets/app-icon.ico --channel win --outputDir releases --releaseNotes NurMarketKassa.Avalonia/release-notes-X.Y.Z.md -y
   ```
   `packId` строго `NurMarketKassa`. Дельта строится от предыдущего полного пакета в `releases/`.
5. Отправить код: `python tools/release/gh_release.py push main` (токен берётся из `token.txt`, в вывод не попадает).
6. Создать релиз и загрузить 7 файлов: `python tools/release/gh_release.py release X.Y.Z <ПОЛНЫЙ sha-коммита (git rev-parse HEAD; короткий GitHub отклоняет с 422)> NurMarketKassa.Avalonia/NurMarketKassa.Avalonia/release-notes-X.Y.Z.md` (добавьте `--prerelease` для тестовой версии). `releases.win.json` грузится последним — по нему кассы узнают об обновлении. Загрузка длинная (~350 МБ), запускать в фоне.
7. Проверить: `python tools/release/resume_release.py X.Y.Z --check` — все 7 файлов `uploaded`. Если загрузка оборвалась (ошибка 10054) — тот же скрипт без `--check` догружает недостающее.
8. Проверить настоящее обновление, как у клиента: распаковать `lib/app` из `%LOCALAPPDATA%\NurMarketKassa\packages\NurMarketKassa-<предыдущая>-full.nupkg` в `current`, запустить кассу, Настройки → Обновления → «Проверить» → «Обновить». После перезапуска журнал должен писать `Update check: up to date (current=X.Y.Z.0 …)`.

## 5. Как проверять (набор `tools/qa`)

- `kwins.ps1 [-Owner]` — окна процесса кассы/владельца: hwnd, размер, заголовок.
- `clickh.ps1 -Handle <hwnd> -X -Y` — щелчок сообщениями в клиентские координаты окна. `uiaclick.ps1 -ProcId -Hwnd -Type Button -NameLike …` — по UI Automation.
- `uia_edit.ps1 -Hwnd <hwnd> -List` / `-Index N -Value "…"` — ввод в поля через ValuePattern (надёжно; WM_CHAR — нет).
- `shoth.ps1 -Handle <hwnd> -Out file.png` — снимок окна через PrintWindow. Встроенные разделы владельца — отдельные окна поверх главного: снимать их hwnd.
- `closewin.ps1 -Hwnd` — закрыть окно (WM_CLOSE). `filedlg2.ps1 -ProcId` — системное окно «Сохранить» (жмёт OK; файл сохраняется в папку по умолчанию — обычно Рабочий стол, переносите его оттуда).
- `stress2.ps1 -Count 30` — поток продаж в «Классике» (плитки Aos 50 и Asu 39 на вкладке «Штучные»), итог по журналу `PAY finished` / `Checkout API OK`.
- `shiftcycle.ps1 -Count 3` — «закрыть смену → Z-отчёт → открыть смену».
- `quiet.ps1` — щелчок и снимок главного окна кассы (выбирает процесс кассы, а не владельца).
- `vprinter.py 9100` — виртуальный чековый принтер на порту 9100 (задания в `%TEMP%\nurmarket-vprinter\jobs`). На этом ПК принтер Windows «NurTest Receipt» (Generic / Text Only) смотрит в порт NURTEST_9100 — графику (штрих-коды) он не печатает; для проверки размера ценника печатайте в «Microsoft Print to PDF».
- `nurcrm_probe.py sales YYYY-MM-DD` / `shifts` — сверка с сервером только на чтение (логин из переменных `NURCRM_EMAIL`, `NURCRM_PASSWORD`).
- **Без интернета**: правило брандмауэра только для exe кассы (нужны права администратора):
  `New-NetFirewallRule -DisplayName "NurTest-Offline-Kassa" -Direction Outbound -Program "$env:LOCALAPPDATA\NurMarketKassa\current\NurMarketKassa.Avalonia.exe" -Action Block -RemoteAddress Internet`, после проверки — `Remove-NetFirewallRule -DisplayName "NurTest-Offline-Kassa"`.
- После перезапуска кассы разделы меню ☰ свёрнуты, а в «Ещё» панель может остаться раскрытой — координаты кнопок сдвигаются. Снимайте окно перед щелчком.

## 6. Как устроен NurCRM (проверено на тестовом аккаунте)

- Вход: `POST api/users/auth/login/` → `access` (JWT). При частых запросах сервер отвечает 429 — касса ограничивает поток (`ApiThrottle`).
- **Продажи**: `api/main/pos/sales/?page=N` — новые первыми, по 80 на страницу; страница за последней повторяет последнюю (останавливаться, когда нет новых id). У строки есть `status`, `payment_method`, `total`, `discount_total`, `shift`, `cashbox`, `client`, `first_item_name`, `debt_amount`.
- **Выручка по правилам сайта** = статусы `paid` + `partially_returned` (частичный возврат — по полной сумме). `debt` и `canceled` не входят. Полный возврат делает продажу `canceled`. `discount_total` уже включает скидки по строкам.
- **Смешанная оплата**: сервер не хранит разбивку нал/безнал (`cash_received=0`, в `payments` одна строка `mixed`). Сайт показывает её отдельной строкой.
- **Номера чеков**: постоянного номера нет. Сайт нумерует по месту в общем списке компании (новые первыми), поэтому номер старого чека растёт с каждой продажей. Касса нумерует так же (с 1.17.22).
- **Аналитика сайта**: `api/main/analytics/market/?tab=sales` (выручка, чеки, по дням, возвраты) и `?tab=products` (товары, категории, бренды, цены строк после скидки). С 1.17.22 выгрузки и ABC берутся отсюда.
- **Корзина/продажа**: `api/main/pos/sales/start/` (нужна открытая смена кассира на этой кассе) → `…/{id}/add-item/`, `custom-item/`, `scan/` → `…/{id}/checkout/`. Состояние корзины — `api/main/pos/carts/{id}/` (`open` / `checked_out`; 404 — корзины нет, открытые корзины удаляются при закрытии смены). Касса собирает чек локально и переносит на сервер только при оплате (`StagingCartService`, три запроса подряд, ~1 с).
- **Смены**: `api/construction/shifts/` (страницы, поле `count`), `api/construction/shifts/{id}/`. Смена своя у кассира на кассе; «открыть смену» идемпотентно по кассиру. Сервер сам закрывает смены в 01:00.
- **Ожидаемая сумма смены** (`expected_cash`) = начало + наличные продажи (с наличной частью смешанной и предоплатой долга) + наличные оплаты долгов + внесения − изъятия. С 28.09.2026 (BE-06) **внесение** уходит как `shift_drawer_inflow` и В неё входит (`ShiftCashFlowSync`, `ShiftCashOperationsStore.NetForShift` после записи на сервер его больше не прибавляет). Изъятие — `shift_drawer_outflow`. Старые внесения, записанные как `manual`, сервер в ожидаемую сумму не включает. Без ответа сервера касса считает ту же сумму сама — `ShiftDrawerMath.Expected` (минус наличные возвраты); «Касса: N» в шапке = ответ сервера + ещё не дошедшие внесения/изъятия + чеки офлайн-очереди (в X-отчёте это отдельная строка «ещё не на сервере»). (Исправлено 27.10.2026, аудит кассы Н-15: раньше тут стояло «внесение в expected не входит».)
- **Долги** — сделки клиента со взносами; погашение по взносам (`PosPayDebtAsync`). У старых тестовых сделок бывает 30+ взносов — оплата идёт по одному (~15 с).
- **Тарифы** «Старт» и «Стандарт» на сервере не различаются флагами — касса различает по названию тарифа (`TariffGate`). Хрупко.
- Товары: `api/main/products/list/`; вариантов (размеров) у товара в NurCRM нет.

## 7. История проекта (кратко)

- До 1.15: переход с WPF на Avalonia, базовая касса поверх NurCRM, релизы только через Velopack (исходники долго не коммитились в git).
- **1.15.0** (03.09): 8 этапов «Доработок» — меню по разделам, отложенные и некорректные чеки, журнал ошибок, база знаний, масштаб экрана, полноэкранный режим.
- **1.16.x** (сентябрь): лояльность (локально), голосовое управление (Vosk, платный модуль) и блокировка по голосу, табло покупателя (COM), редактор этикеток и ценников, Маркетплейс с платными модулями по серийным ключам, языковые пакеты, производительность, Telegram-бот владельца, весы Штрих-М (PLU), CSV-импорт, динамический QR ELQR (MBank), изоляция данных аккаунтов.
- **1.17.1–1.17.14** (22–25.09): сверка аналитики с сайтом, склад, темы, приёмка, защита от 429, озвучка своим голосом, сфера магазина и консультант.
- **1.17.15–1.17.18** (26–27.09): смена «только своя», печать не зависает; программа владельца в пакете кассы (`--owner`); табло LED8N; несколько весов; база знаний со снимками и GIF на 5 языках.
- **1.17.19–1.17.22** (27–28.09) — эта сессия, подробно ниже.

## 8. Сделано 27–28.09 (1.17.19–1.17.22)

| Версия | Коммит | Что вошло |
|---|---|---|
| 1.17.19 | `85983ab`, `cd31dd4`, `28945c5`, `d7115bb` | История чеков кассира и «Печать копии» (чек как на бумаге, «ПОВТОРНАЯ ПЕЧАТЬ» + дата); «Отложить чек» вкладкой; «Списание» в «⋮ Ещё»; клавиши Num+/Num−/Enter и страница «Клавиши»; обмен по локальной сети (этап 1); аналитика владельца (раздел «Аналитика», ABC по разделам); выгрузки Excel/Word в виде отчётов; ценники через драйвер; масштаб 160%; крестик без выхода из учётной записи; Z-отчёт «Скидки»; отчёт смены у владельца за 0,8 с; долг смены по всем страницам |
| 1.17.20 | `cc71346`, `87f219d`, `52c019e` | Шесть видов кассы (Классика, Табличная, Карточки, Минимал, Профи, 1С) в Настройки → Экран; редактор своих тем (Маркетплейс → Темы); полировка основного вида; баннер обновления без разметки (`ReleaseNotesText`) |
| 1.17.21 | `8b298b4` | Срочная: версия сборки = версия пакета (`$(Version)`); голос «удалить последний»; «Оплачено N сом» в оплате долга; значок очереди без интернета обновляется сразу |
| 1.17.22 | `22b2e58`, `53921fc`, `7941c67` | Сверка с сайтом: выгрузки и ABC по данным сервера; смешанная оплата отдельной строкой; все страницы смен (202); номер чека как на сайте; «+ ещё N» в «Последних продажах»; подкачка истории продаж по всем страницам с ценой после скидки |

### 8.1. Баги этой сессии и как исправлены

| Баг | Причина | Исправление (где) |
|---|---|---|
| На квадратных экранах масштаб становился ~160% | (1) с 1.17.13 ползунок >100% применялся без подгонки; (2) подгонка считалась после окна «Касса обновлена» (1024×768 при 125% влезает 62%, 100/62≈1,6); (3) окно настроек подгонялось до показа | `UiScaleHelper.ComputeScale` = min(выбор, помещается), отложенное применение до появления окна, пересчёт при смене экрана/DPI |
| Штрих-код на ценниках обрезан, размер ×2 | Драйверу передавались пиксели вместо сотых долей дюйма; плюс ещё 6 ошибок (неверный EAN рвал пачку, лишняя цифра к 12-значному коду, EAN с ведущим нулём, QR с кириллицей, размытые штрихи, память на A4) | `BarcodeLabelService`, `PriceTagService`, диалоги печати ценников/этикеток |
| Крестик выходил из учётной записи; после кассы+владельца надо входить заново | Закрытие окна шло по пути «выход» с удалением `auth.dat` | `MainWindow`: крестик → `ExitApplication()` |
| Копия чека: скидка дважды (225,50 / −41,00) | `discount_total` уже содержит скидки строк, а их прибавляли ещё раз | `SalesWindow.ReadSaleDiscount` |
| «Отложить чек»: чек пропадал / не удалялся после оплаты | Логика вкладок | `BasketPanelViewModel` (`DeferredAt`, `ReturnToPrimaryReceiptIfSecondary`) |
| Z-отчёт «Скидки: —» при скидке кассира | Плитка считала только бонусы | `ShiftDetailsDialog.ShowServerDiscountsAsync` (discount_total неотменённых чеков) |
| Отчёт смены в «Финансах» 7,9 с, «Долг: —» | Грузился весь список смен; долг смотрел одну страницу продаж | `FinanceWindow.ShiftCard_PointerReleased` → `LoadOneAsync`; `CashShiftService.TryComputeDebtTotalAsync` листает страницы |
| «Отчёт сохранён» красной плашкой | Успех выводился через поле ошибки | `Avalonia/Services/NoticeBanner.cs` |
| Длинный текст под полосой прокрутки | Нет отступа у текста | `PosAlertDialog`/`PosConfirmDialog`: `Margin="0,0,14,0"` |
| После обновления до 1.17.19/1.17.20 касса снова предлагала обновиться | В csproj обновлялась только `<Version>` | `AssemblyVersion`/`FileVersion` = `$(Version)` |
| Голос «удалить последний» убирал первую строку | Строки чека хранятся новыми сверху, брался `LastOrDefault` | `MainWindow` (`VoiceIntent.RemoveLastItem` → `FirstOrDefault`) |
| Выгрузка Excel ≠ экран (153 052,97 против 152 992,47) | Считалась по локальной истории: долг в выручке, цена строки до скидки, неполная история | `AnalyticsReportData` + `SalesApiService` (данные `tab=sales/products`), `SalesHistoryBackfill` |
| «Финансы»: смешанная целиком в «Безнал» | Правило раскладки | `FinanceWindow`, `SalesWindow` («+ смешанная») |
| Смен 100 из 202 | Читалась первая страница | `ShiftApiService` (все страницы, по 3 параллельно) |
| Номер чека №9 в истории и №5 в возврате | Разные правила нумерации | `ReceiptHistoryService`, `ReturnSaleDialog` (как на сайте) |
| Баннер обновления показывал «## v1.17.19», «---» | Описание релиза — Markdown на 5 языках | `ReleaseNotesText.Plain` |

### 8.2. Проверено вживую (тестовый аккаунт)

33 продажи подряд без ошибок (в среднем 3,3 с); 2 продажи без интернета досланы за 16 с; 5 закрытий смены подряд (одна открытая на сервере); возврат, внесение/изъятие, оплата долга; все 6 видов кассы продажами; за 28.09 сервер = «Сводка» = «Финансы» = Excel (154 309,47 сом, 58 чеков, скидки 20,50); обновление 1.17.20 → 1.17.21 кнопкой «Обновить» на клиентском пакете.

### 8.3. 06.10.2026: режим «Одежда» (не закоммичено на момент записи)

Источник — исследование `reports/Кассы для одежды и услуг.md`. **Правило владельца: «главное не трогай продуктовый»** — всё новое включается только при `MarketSpheres.IsClothing` (часть — и в «Услугах»); в «Продуктах» поведение прежнее (возврат идёт старым запросом, окно причины без кнопок, поиск без доп. штрихкодов и штрихкодов размера).

- **Обмен** (кнопка «Обменять выбранное» в возврате): `ExchangeWindow.cs`, API `SalesApiService.Exchange.cs` (`POST pos/sales/{id}/exchange/`, метод только cash/transfer, «card» сервер отклоняет), чек `ExchangeReceiptTextBuilder`. Баг сервера: обмен товара из чека **закрытой** смены завышает `expected_cash` на сумму возврата — касса пишет поправку (`ExchangeCashCorrections`, файл `exchange_cash_fix.json`, учитывается в `ShiftCashOperationsStore.NetForShift`) только если расхождение ровно равно сумме возврата.
- **Правила возврата**: срок (по умолчанию 14 дней) и категории «обмену не подлежит» — `NonExchangeableRules.cs`, карточка «Обмен и возврат» в Настройки → Операции. Причина и «Брак» (`is_defect` → товар не на склад) уходят на сервер.
- **Штрихкод размера**: сервер не находит штрихкоды вариантов (`products/barcode/{code}` → 404), поэтому `VariantBarcodeIndex` — общий для кассы и программы владельца файл `%APPDATA%\NurMarketShared\{companyId}\variant_barcodes.json`, фоновая проверка товаров (1,5 с на товар, пауза 2 мин на 429). Скан размера — в чек, поиск каталога, окно обмена, склад. Кнопка «Размер» в строке чека меняет вариант.
- **Каталог без копий филиалов** (баг «сканер говорит нет, а товар на складе»): `CatalogApiService.ExcludeBranchCopiesAsync` — только в «Одежде»/«Услугах» и без выбранного филиала.
- **Приёмка сеткой цвет×размер**: `VariantReceiveWindow.cs`, `WarehouseWindow.Variants.cs`, колонка «Размеры» в приёмке; после проведения остатки вариантов дописываются на сервер (`PurchaseReceivingService`, шаг 3а), затем предложение напечатать этикетки.
- **Этикетки**: элемент шаблона «Размер и цвет», в редакторе вариантов — «Создать штрихкоды» (29 + 10 цифр + контрольная), «Этикетки», готовые сетки размеров.
- **Владелец**: раздел «Размеры и цвета» (`SizesReportWindow.cs`, по умолчанию 7 дней), «Заканчиваются размеры» на главной, «Обычно берёт размер» в покупках клиента. Строки чеков запоминаются на диске (`SaleSizesCache.cs`, `sale_sizes_cache.json`): первый раз 30 дней = 1083 чека ≈ 2 мин, повторно — секунды; чек, по которому был возврат после запоминания, перечитывается (список `pos/returns/?date_from&date_to`, `date_to` включительно; полностью возвращённый чек сервер помечает `canceled` и убирает строки). Программа владельца в «Одежде» подгружает 30 дней в фоне.
- **Перемещение в филиал со склада** (`StockTransferDialog.Branches.cs`): «▾» у «Откуда/Куда» — главный склад и филиалы (`api/users/branches/`); «Отправить» → `POST api/main/branch-transfers/` (товар ищется на складе отправителя: `products/list/?branch=<id|main>`), номер и id накладной — в примечании документа; «Отменить» в пути → `…/{id}/cancel/`. Проверено: ПЕР-000004, 64 → 63 и обратно.
- **Первый запуск**: `MarketSphereChoiceWindow` — один раз на новой установке (`UserPreferences.MarketSphereChoicePending`; у старых установок флаг читается как false), только если у компании на сервере вид магазина не задан.
- **ТЗ**: `docs/ТЗ для бэкенда NurCRM - часть 14 (магазин одежды …).md` (баг expected_cash, поиск штрихкода варианта, размер/цвет в характеристиках, сертификаты, отложенный товар и др.) и `docs/ТЗ для фронтенда NurCRM - вид магазина … (06.10.2026).md`.
- **Сервер 06.10 стал отвечать 500 на страницу списка продаж дальше «следующей за последней»** (`pos/sales/?page=3` при 65 чеках): «Финансы», «Продажи» и аналитика качают по 3 страницы сразу и показывали «Нет связи». Исправлено в `SalesApiService.PosSalesListAsync` (`PageIsPastEndAsync`: на 500 для page > 1 — запрос count; страницы нет — пустой ответ). В Android/Linux 1.17.53 этого исправления ещё нет.
- **Редизайн программы владельца (этап 1–2)**: поиск раздела в меню (Ctrl+K, `OwnerShellWindow.NavSearch.cs`), сворачиваемые группы (`OwnerNavCollapsedGroups`), группы «Продажи / Отчёты / Клиенты / Управление» вместо «Продажи и деньги / Люди / Сервис», свои значки; «Сводка» за сегодня — «к вчера на это время» (`OwnerShellWindow.SameTime.cs`); цена на складе с разрядами (`CatalogProductTileVm.PriceLineDisplay`).
- **Редизайн склада (вкладка «Товары», `WarehouseWindow.Redesign.cs`)**: плитки сводки сверху (нажатие — фильтр), фильтр «Наличие» (все / заканчивается / нет / срок годности), «категория · бренд» и метка срока в строке, кнопка «±» (`StockAdjustDialog`: приход / списание / точное количество), поиск со значком и «✕», «Список/Плитки» справа, «Удалить» отдельно, двойной щелчок — открыть товар, сортировка цены по числу (`PriceValue`). Сроки годности — с сервера (`ProductExpiryIndex`: `analytics/market/?tab=products` → `tables.expiring_products`).
- **ИИ и бот меняют товары** (`ProductActions` — остаток документом ревизии с перечитанным остатком, поля PATCH; `ProductActionPlan` — строки ИИ «ТОВАР: {op…}» → подтверждение → выполнение). Операции: stock_in/out/set, set_expiry, set_shelf_life, set_min, set_price, set_purchase, set_description, set_country, set_name, set_brand (`brand_name`), set_category (`category_name`), set_barcode, set_article, photo. Советник — карточка «Выполнить / Отмена»; бот — кнопки (callback `pa:<token>:y|n`) или ответ «да/нет». В «звонке» (живой голос) действий нет — советник направляет в чат. Проверено в советнике: Mars списание 68 → 67, описание и страна сохранены. В боте вживую не проверялось.
- **Поиск сведений о товаре** (`ProductInfoResearch`): на «опиши / дополни / найди в интернете» — открытые базы по штрихкоду (Open Food/Beauty/Products Facts) + выдержки DuckDuckGo (html, ~1,5 с, без ключа); Google-поиск Gemini — только если ключ позволяет. Groq с поиском не используется: отвечал по 60 с и упирался в лимит. Вопрос с поиском — ~8 с.
- **Товары голосом в «звонке»** (`AiAdvisorWindow.LiveProductPassAsync`): реплика про товар → параллельно разбор чатом → «открыть» сразу (`ProductActionPlan.OpenProduct` → «Склад» с товаром), изменения — карточкой; «да, выполни» голосом (`IsVoiceYes`) выполняет; результат сообщается в звонок `GeminiLiveVoice.SendTextAsync` («[Программа] …»). Голосом вживую не проверялось (нет микрофона у агента).
- **Фото накладной в ИИ-советнике** («📎» в строке ввода): картинка уходит в Gemini частью `inline_data` (`TelegramAiChat.QuestionImage`, только текущий вопрос); ИИ пишет `receive` (приход: qty, purchase, price) или `create` (новый товар через `CreateProductAsync`), цена = закупка × (1 + наценка) (`RoundPrice`). Проверено: тестовая накладная 3 строки, наценка 30 % — Mars +24 (цена 50 → 39), салфетки +20, создан «Сок Gracio яблочный 1л» 12 шт.
- **ИИ показывает изменения**: после «Выполнить» (`ProductActionPlan.ShowChangedProducts`) — «Склад» с плашкой «✨ Изменено ИИ: N ✕» и только изменёнными товарами; перед показом полная синхронизация каталога (после PATCH цены версия каталога сервера не меняется — обычная синхронизация пропустила бы). Операция `open_section` — ИИ открывает раздел (склад, продажи, финансы, зарплата…).
- **Сотрудники в ИИ** (`TelegramAiChat.StaffContextAsync`): на вопросы о зарплате/продажах сотрудников — `analytics/market/?tab=salary` (сервер считает по схемам), период из вопроса.
- **Реклама**: `docs/Реклама/ИИ-советник с голосом (ru).mp4` (79 с, edge-tts Дмитрий/Светлана, кадры из программы; сборка — `make_ad.py` в папке агента, не в репозитории).
- **Тестовые данные на NBS**: продажи №1593–1601, вариантные штрихкоды (в т. ч. 9900440200017), поправка обмена.
- **Вечер 06.10 → выпуск 1.17.54 (Windows, тестовый, коммит 0159504):**
  - Карточка ИИ «Изменить товары?»: у нового товара без штрихкода — поле «Штрихкод — отсканируйте» (`AiAdvisorWindow.AddProductActionsCard`). Курсор карточка НЕ забирает (владелец: «завис!!!» — ответ на вопрос ИИ уходил в поле штрихкода); скан сканера ловит туннельный `ScanKeyDown` окна (быстрый набор ≤ 40 мс + Enter) и кладёт код в следующее пустое поле. Код уже есть в каталоге (точное совпадение штрихкода) → приход к этому товару, без дубля. Новая карточка гасит прежнюю (`_pendingProductSupersede`) — иначе два «Выполнить» создавали товары дважды.
  - ИИ о сотрудниках: список (`ListConsultantsAsync`), зарплата (`analytics/market/?tab=salary`), табель — `TelegramAiChat.TimesheetProvider` (в `App.axaml.cs`: `ShiftHistoryService` + `StaffTimesheetService`, как окно «Табель»).
  - Стресс-тест (скрипты в папке агента: `stress5.ps1`, `offline5.ps1`, `shiftcycle5.ps1` — каждая продажа в новом чеке «+ Новый чек», корзину владельца не трогают): 15/15 продаж 1,5–2,2 с; офлайн 3 → пачка 2,2 с; смена с Z-отчётом. Найдено и исправлено: `BasketPanelViewModel.ClearAfterShiftClose` стирал вкладки «Отложен» (теперь непустые неактивные вкладки остаются); строка «В очереди: N» под чеком после досылки (`MainWindowViewModel` → «Все чеки из очереди отправлены»).
  - Не проверено в 1.17.54: долг, внесение/изъятие, возврат, действия с товарами в боте и голосом.
  - После выпуска 1.17.54 (только на этом ПК, не закоммичено): в «звонке» советник знает сотрудников, ставки, начисления и табель
    (`BuildOwnerVoiceInstruction(..., staff)`, не успели к началу — `SendStaffWhenReadyAsync` с `respond: false`; вопрос о сотрудниках
    без данных или за другой период — `LiveStaffPassAsync`); три запроса о сотрудниках идут параллельно, список смен для ИИ кэшируется
    10 мин и подгружается при открытии советника (было 7,3 с на табель, стало ~0,4 с); данные магазина в чате советника — таблицей
    Markdown, окно рисует её сеткой (`AiAdvisorWindow.RenderTables`), в Telegram-боте таблиц нет (там свои правила `ListRules`).
- **Выпуск 1.17.55 для клиентов (коммит 8e5fb59)** — всё выше плюс: архив смен для ИИ и бота (`TelegramAiChat.ShiftArchiveProvider`, кэш списка смен 3 мин в
  `App.axaml.cs`); напоминание должникам (`НАПОМНИТЬ: [Д1]…` → `AddDebtReminderCard`, встроенный WhatsApp Web `…/send?phone=&text=`, отправляет
  владелец; должники по кодам — `OwnerAiContext.LastDebtors`); «закончились хиты / популярные с малым остатком» в `AppendProductAnalysisAsync`;
  фото на выбор (`ProductPhotoFinder.FindChoicesAsync`: базы по штрихкоду + og:image страниц из `ProductInfoResearch.SearchPageUrlsAsync`;
  заголовок Accept без AVIF); склад: миниатюры видимой страницы (`LoadPageThumbnails`), каталог из базы, если в памяти пусто (`LoadCatalogIfEmpty`);
  `ToPlainText` не рвёт строки таблиц; Groq-поиск ≤ 30 с, нет ответа — пауза 10 мин; окно товара — запас 80 px снизу (вкладки переносятся).
  Проверено: 33 продажи, долг №1646, возврат, внесение/изъятие, офлайн, смена, режимы Продукты/Услуги/Одежда (вернул «Одежда»),
  29 разделов владельца без ошибок, 10 вопросов ИИ (1,6–3 с), замер 10 000 товаров (операции ≤ 7 мс; загрузка с сервера ~1 мин, 100 стр.).
  Поиск картинок DuckDuckGo i.js (403) и Bing (пустая страница) без ключа не работают — не использовать.
  - Реклама: `docs/Реклама/Накладная по фото и голосом (ru).mp4` (88 с, разговор хозяин — советник), сборщик `make_ad_invoice.py` в папке агента. edge-tts временами отвечает «No audio was received» (ограничение запросов) — повтор через несколько минут.
  - ТЗ фронтенда (вид магазина; витрина с темами) на 06.10 вечером не сделаны: в сборке `market.nurcrm.kg/assets/*.js` нет `market_sphere`, `variants/`, `exchange/`, `rentals`, `showcase/design`.
  - Тестовые товары на NBS: «Печенье «Овсяное с изюмом» 300г», «Вафли «Лесной орех» 220г», «Кофе «Жокей» молотый 250г», «Сахар-рафинад 1кг», «Макароны «Макфа» спагетти 450г» (штрихкоды 47021540000xx и 200000015201x — тестовые), «Печенье «Юбилейное» 112г» (настоящий 7622210457745).
- **Выпуск 1.17.56 (07.10)**:
  - Сенсорные моноблоки: `Services/TouchKeyboardAuto.cs` — касание поля пальцем/пером открывает osk.exe (настройка `AutoShowTouchKeyboard` раньше ни к чему не была подключена). `ProductEditDialog`: на окне < 820 поля плотнее, < 700 — без подсказки про скан, полоса прокрутки видна всегда, поле с фокусом прокручивается в вид.
  - Фото из интернета: DuckDuckGo html отвечает 202 «anomaly» (блок) — основной источник теперь Яндекс.Картинки (`ProductPhotoFinder.YandexImagesAsync`, поле `origUrl` + `snippet.url`); WebP при загрузке → PNG; скачанные картинки кэшируются в памяти. Google Картинки без браузера ссылок не отдают; запасной — Google через Gemini (`TryFindOnWebAsync`).
  - ИИ-советник: «поставь фото N» (`PhotoChoiceNumber`), «открой …» — сразу, без ИИ (`TryQuickOpen`; разделы — `ProductActionPlan.NavigateByWords` → `OwnerShellWindow.NavigateByWords`, по основе слова; товар — по целым словам названия). Звонок ждёт сводку ≤ 6 с, потом досылает (`SendSummaryWhenReadyAsync`); упавшая часть сводки больше не роняет всю.
  - Исправлено: ненаблюдаемые исключения параллельных запросов (`OwnerShellWindow` refresh, `ServerSalesSummary`), повтор записи настроек при IOException.
  - ТЗ фронтенда на 07.10 ночью по-прежнему не сделаны (сборка `index-ULfBm94a`, `catalog-DHRtM-ZA`): витрина не читает `showcase/design` — тема из редактора у покупателей не видна, это не баг программы.
- **Выпуск 1.17.57 (07.10)**: Нур Советник (имя), чеки за период таблицей (`OwnerSalesData`, `ReceiptHistoryService.LoadRangeAsync`, `TelegramAiChat.SalesDetailProvider`), в звонке таблица в чате (`LiveTablePassAsync`), разделы — только по «открой» (`HasOpenCommand`), редактор сайта голосом (`SiteEditorWindow.VoiceCommandAsync`), штрихкод по названию (`BarcodeListLookup`, barcode-list.ru). 07.10: market.nurcrm.kg (`catalog-Ccmz2ht8.js`) оформление уже показывает; nurcrm.kg/catalog — нет; «Смотреть сайт» ведёт на market (`ShowcaseApiService.DesignCatalogUrl`).
- **Выпуск 1.17.58 (11.10) — Windows, Android и Linux одной версией** (коммиты 981036d, 535c443). Всё из списка ниже + денежный ящик: импульс после печати чека (`TryOpenCashDrawerAfterSale(..., printTask)`), после продажи без ESC @ и с DLE DC4 (`EscPosCommands.WriteRealtimeDrawerPulse`) — ESC @ стирал недопечатанный чек. Удалённая помощь: сеанс только после локального «Разрешить» (`_consentGranted`), адрес только `wss://` (ws:// — loopback).
  - Android: версия в `NurMarketKassa.Android.csproj` (`ApplicationVersion` = 1.17.58 → 11758, `ApplicationDisplayVersion`, `Version`); APK — из папки с `global.json` SDK 8.0.422 (см. ловушки). Подпись (21.10.2026: старый debug.keystore от 04.10 пропал) — **с 1.17.59 новый ключ** `C:\Users\User\Documents\NurMarket-keys\nurmarket-android.keystore` (alias `nurmarket`, копия в `backup\`, пароль — в `README.txt` рядом, в репозиторий не класть); все APK подписывать только им: `-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=<путь> -p:AndroidSigningKeyAlias=nurmarket -p:AndroidSigningStorePass=env:NURKS_PASS -p:AndroidSigningKeyPass=env:NURKS_PASS`. Выпуск: репозиторий `beknazar93/Nurmarket-Android`, тег `android-X`, файл `nurmarket-kassa-android-X.apk`, prerelease (обновление в программе читает список выпусков и берёт .apk). Пункты «Что нового» — `AppChangelog.AndroidLatest*`.
  - Linux: `dotnet publish NurMarketKassa.Portable/... -r linux-x64 --self-contained`, в архив добавить `NurMarketKassa.Portable/linux/README-LINUX.txt` и `nurmarket-owner.sh` (publish их не копирует), права 755 у `NurMarketKassa.Avalonia` и `.sh` (tar собирать Python `tarfile`, не Windows tar). Репозиторий `beknazar93/Nurmarket-Linux`, тег `linux-X`, файл `nurmarket-kassa-linux-x64-X.tar.gz`, prerelease. «Что нового» на Linux — общий `AppChangelog.Latest*` (не Android).
  - ТЗ бэкенда, часть 16 (11.10): сервер удалённой помощи на VPS (`support.nurcrm.kg`), товары поставщика одним запросом, фото накладной у прихода, фильтр ниже минимального остатка.
- **После 1.17.58 (11.10, не выпущено)** — владелец выбрал дизайны (холсты «Нур Советник — варианты дизайна чата» A/E/H и «Сводка владельца — варианты дизайна» — «все»):
  - Нур Советник (`AiAdvisorWindow.Redesign.cs`, класс стал partial): A — строка ввода в две строки, ответ без пузыря, значок «Н»; E — сцена звонка слева (`BuildCallStage`, `SetCallLayout`, `TickCallStage`), уже 980 точек — прежняя полоска; H — `[ПЛИТКИ]…[/ПЛИТКИ]` и `[ГРАФИК: …]…[/ГРАФИК]` в ответе (правило в `OwnerAppPrompt`, рисует `RenderTables` → `BuildWidget`; «Копировать»/озвучка — `WidgetsToText`). `AskQuestion(q)` — вопрос извне.
  - «Сводка» (`OwnerShellWindow.Brief.cs`): тёмная карточка «Нур Советник · коротко» над плитками с «Требует внимания» (закончились ходовые / скоро закончатся — по каталогу и отчёту 14 дней, закупка дороже цены, долги старше 30 дней; кнопки открывают раздел через `OpenNav`), ряд «Продажи по часам» (сегодня/вчера) + «План на сегодня» (кольцо из плана месяца или до вчерашней выручки, прогноз по `YesterdayShareNow`, «нужно в час», кассиры). Только для периода «Сегодня»; узко — `NarrowStack`.
  - Аудиты от 10.10 (`docs/Полный … аудит …`) сверены с кодом: часть закрыта в 1.17.58; осталось — ключи идемпотентности возврата и предоплаты, приход без ключа, закрытие окон во время запроса, «операция записана — повторить печать», dirty-флаг закрытия смены, подписи сумм долга, узкий checkout на Windows.
- **После 1.17.57 (11.10, вошло в 1.17.58)** — замечания тестировщика (файлы владельца «склад.md», «иишка.md»):
  - Склад: «Обновить» крутится и пишет «Загружаю… N с»; приёмка — пустое «Принято»/цены можно стереть (`ReceivingLineVm` raw-текст), название не уже 240 точек, крестик в строке, «Очистить список», подсказки и список поставщиков закрываются (Esc, ✕, клик мимо, повторный выбор), товары поставщика из его приходов (`PurchaseReceivingService.LoadSupplierProductsAsync`); ревизия — крестик и «Очистить список» (`WarehouseWindow.Fixes.cs`); перемещение — пустой черновик удаляется (`StockTransferService.DeleteIfEmptyDraft`), без филиалов прямо написано, что это только учёт кассы.
  - Окно товара: «Основное» — + горячая клавиша, оптовая/скидка (артикул ушёл); «Доп. штрих-коды» — артикул + правка штрихкода ✎/✓ (`VariantDraft` с INotifyPropertyChanged); «Упаковка» — минимальный остаток (`ProductEditRequest.MinimumQuantity` → `minimum_quantity`, читается `ProductsDetailAsync`); «Описание» — + категория и бренд; запас снизу 240 точек. Тумблер «Весовой» — во всех режимах.
  - Нур Советник: до 10 фото/PDF к одному вопросу (`_attachments`, `TelegramAiChat.QuestionExtraImages`), «✎ Отредактировать» в карточке накладной (`InvoiceEditorWindow`: наименование с накладной, в чеке, кол-во, закупка, продажа).
  - **Переходы Нур Советника без нейросети (27.10, жалоба «открывает не те разделы, долго думает»)**: `Infrastructure/Services/AdvisorNavRouter.cs` — словарь всех разделов и вкладок программы владельца (ru/ky/uz/en/tr, нечёткий поиск, ничья → до 4 кнопок), `AdvisorTurn.cs` — открытие и ответ из факта + предел ответа 12 с (`WithBudget`) + замеры `ADVISOR_PERF`. Открытие — `ProductActionPlan.OpenNavTarget` ← `OwnerShellWindow.OpenNavTarget` (проверяет `_activeSection`, вкладки: `SelectNavTab` у Склада/Закупок/Настроек, `TrySelectSalesFinanceTab`). Голос: инструмент `open_section` в `GeminiLiveVoice` (`ToolHandler`). Сводка — частями по смыслу вопроса (`AdvisorIntent.PartsFor`, кэш 60 с в `AiAdvisorWindow.CachedPart`). Новый раздел/вкладка = строка в словаре роутера. Стенды: `NurMarket-rel1762/stands/advisor-nav` (фразы, время) и `advisor-shot` (снимки чата).
  - Своя удалённая помощь (аналог AnyDesk): сервер `NurRemoteSupport/Server` (38/38 проверок `RelayTests`), касса — `Services/RemoteSupport/RemoteSupportHost.cs` + блок в «Тех. поддержке» (`RemoteSupportWindow.NurSupport.cs`); включается файлом `%LOCALAPPDATA%\NurMarketKassa\remote-relay.txt` или `NURREMOTE_RELAY`, на VPS ещё не развёрнут. Проверено локально: код+PIN → согласие → экран у оператора.
  - Внимание: в индексе git кто-то застейджил `linux-out/` (315 файлов), wav 52 МБ и `__pycache__` — в коммит не брать (`git add -- NurMarketKassa.Avalonia ':(exclude)NurMarketKassa.Avalonia/linux-out'`). Незакоммиченные правки другой сессии 09–10.10 (смена, возврат, корзина, бот) — тоже в рабочей копии.

## 9. Открытые задачи

| Задача | Статус |
|---|---|
| **Виды кассы в Маркетплейсе** (отдельная вкладка), **экран покупателя меняется вместе с видом кассы**, **редактор экрана покупателя** — просьба владельца 28.09 | В работе у агента в отдельной рабочей копии (`.claude/worktrees/…`), в `main` ещё не влито. После слияния — сборка, живая проверка, выпуск 1.17.23 |
| Баланс кассы после закрытия смены меньше на сумму внесений | Правило сервера NurCRM; нужна доработка NurCRM (вид операции «внесение в ящик») |
| Z-отчёт относит смешанную оплату к безналу | Сервер не хранит разбивку; нужна доработка NurCRM |
| Постоянный номер чека | Нужен от NurCRM (понадобится и для фискализации) |
| Отчёты Telegram-бота (/неделя, /топ, /abc) считаются по локальной истории | Не начато |
| В отчёте смены у владельца нет возвратов и внесений (эти счётчики живут в локальной базе кассы) | Не начато |
| Ускорить оплату (~2 с: перенос чека одним запросом заранее, короче пауза «Платёж принят») | Не начато |
| Долг из десятков взносов гасится ~15 с | Не начато |
| В списке принтеров этикеток видны камера и Bluetooth (WinUSB) | Не начато |
| Мелочи: строка склада нечитаема при наведении; в меню ☰ крестик налезает на название | Не начато |
| Проверка на железе: ценники на термопринтере через драйвер, весы Штрих-ПРИНТ по сети | Нужно оборудование |
| Обновление 1.17.21 → 1.17.22 кнопкой не проверялось (1.17.22 ставился копированием) | Проверить при следующем выпуске |
| Большие направления (из анализа конкурентов): фискализация через облачную ККМ (eKassa / Neo Fiscal), банковский терминал (O!Bank API), автоподтверждение QR и ELQR для 2–3 банков, электронный чек через Telegram, бонусы на сервере, облачный бэкап, мастер первого запуска | Не начато; Android-касса отложена владельцем |
| Папка `.playwright-mcp/` в корне (снимки сайта от агента сверки, в .gitignore) | Удалить может только владелец |

## 10. Ловушки, на которых уже спотыкались

- **Потоки в `BasketPanelViewModel`**: там везде `ConfigureAwait(false)`; всё, что трогает UI после `await`, — через `_dispatcher.InvokeAsync`. Иначе «Call from invalid thread» роняет всю кассу.
- **Ресурсы темы**: `FindResource` без варианта темы возвращает `UnsetValue` → падение при приведении. Использовать `Application.Current.TryFindResource(key, ActualThemeVariant, out var v)`.
- **Клавиши**: `TextBox` съедает Delete/Backspace и Enter — перехватывать в фазе Tunnel. `ListBox` не берёт фокус — фокусировать `ListBoxItem`.
- **Строки чека** в `BasketPanelViewModel.Lines` хранятся новыми сверху (`Insert(0, …)`): «последняя добавленная» = `FirstOrDefault()`.
- **Автоматизация**: PostMessage обходит модальные окна (в жизни кассир так не сможет); WM_CHAR ненадёжен — вводить через `uia_edit.ps1`; при двух запущенных процессах (касса и владелец) выбирать окно по заголовку, а не «первый процесс».
- **Агенты в worktree**: создаются от `origin/main` — перед запуском агента запушьте нужные коммиты, иначе он начнёт со старого состояния. Если cwd — `c:\` в нижнем регистре, создание worktree падает (перейдите в `/c/Users/...`). В worktree для сборки нужен `AnyDesk.exe` (скопировать из корня).
- **Слияние патчей** с другими окончаниями строк: `git apply --ignore-whitespace`; файлы, не менявшиеся в `main`, проще скопировать целиком.
- **Сохранение файлов программой** (выгрузки, PDF) по умолчанию идёт на Рабочий стол владельца — переносите тестовые файлы оттуда.
- **Кодировка PowerShell 5.1**: `.ps1` с кириллицей должны быть в UTF-8 **с BOM**.
- **Два .NET SDK** (с 06.10.2026 в `%LOCALAPPDATA%\Microsoft\dotnet` есть SDK 10.0.401, он стал основным): Android-нагрузка для net8.0-android стоит только у SDK 8.0.422 (21.10.2026: SDK 8.0.424 с ПК пропал, нагрузка android 34.0.154 поставлена `dotnet workload install android` для 8.0.422). APK собирать из папки с `global.json` `{"sdk":{"version":"8.0.422","rollForward":"disable"}}` (вне репозитория) и полным путём к csproj, иначе `NETSDK1147 … android wasm-tools-net8`.
- **Маркетплейс и баннер обновления**: когда показан баннер обновления, кнопки ниже сдвигаются — снимайте окно перед щелчком.

## 11. Как продолжить

1. Прочитать этот файл и `git log --oneline | head -20`.
2. Проверить незакоммиченные изменения (`git status`) и открытые рабочие копии агентов (`git worktree list`).
3. Взять задачу из раздела 9 или новую от владельца; сказать владельцу по-русски, что поняли, какие файлы тронете и какие риски.
4. Сделать, собрать, поставить локально, проверить вживую (раздел 5), только потом выпуск (раздел 4.3) — и только по просьбе владельца.

## 12. Новый десктоп и телефон (с 12.10.2026)

Отдельно от кассы 1.17.x идут два новых клиента по макетам холста «NurMarket — редизайн в стиле Coca». В выпуски кассы они не входят, ничего не закоммичено (на 13.10).

- **Новый десктоп** — `NurMarketDesktop/NurMarket.Desktop` (Avalonia 11.2, своя тема `Theme/Tokens.axaml` + `Theme/Controls.axaml`). Касса — `Views/KassaWindow`, программа владельца — тот же exe с ключом `--owner` (`Views/AdminWindow`, своя папка данных `NurMarketDesktopOwner`).
  - Окна, уже сделанные по макету: касса, оплата, возврат, история чеков, склад, клиенты, продажи/финансы/ABC, пополнение и сроки, некорректные чеки, КП и счета, воронка CRM, поздравления и бонусы, сводка.
  - Остальные разделы — **окна старой кассы целиком**: ссылка на проект старой кассы (на Windows `NurMarketKassa.Avalonia`, для Linux `NurMarketKassa.Portable`), открытие — `Host/LegacySections.cs`, встраивание в админку — `Host/SectionEmbed.cs`, их службы — `Host/LegacyRegistrations.cs`, статические свойства их `App` — `Host/LegacyHostBridge.cs`.
  - КП, счета, сделки, правила поздравлений пока хранятся на компьютере (`Host/CrmStore.cs`, `crm/<компания>/crm.json`) — API описано в ТЗ бэкенда, часть 28, раздел 17. Бонусы за поздравления уже идут на сервер (`ServerLoyalty.PostAsync`).
  - 13.10 — сверка с холстом «NurMarket — Десктоп (касса и владелец)», доделаны доски, которых не было: контроль удалений из чека с причиной (У2, `Views/DeletionControl.cs`, `DeletionsWindow.cs`), задачи / напоминания / «сообщить админу» (ЗД2, `TasksWindow.cs`), печать и экспорт документов — бланки A4/A5/80 мм, акт сверки, выгрузка за период (Д1–Д3, `Host/DocForms.cs`, `DocPrintWindow.cs`, `DocumentsWindow.cs`), заказы поставщикам с приёмкой по заказу через настоящую `PurchaseReceivingService` (З2–З4, `PurchasesWindow.cs`, `ReceiveByOrderWindow.cs`), сборка и выдача заказов с сайта (СЗ1–СЗ3, `PickingWindow.cs`), рассрочка (Р1–Р5, `InstallmentsWindow.cs`), подарочные сертификаты (Л2–Л3, `GiftCardsWindow.cs`), настройки ККМ / оплаты / бонусов без фальшивой фискализации (Ф1, Б1, Л1, `KkmPaySettingsWindow.cs`), темы светлая/тёмная/авто и виды кассы (ТМ, ВК, `Host/DesktopThemes.cs` — меняет цвет общих кистей на лету). Воронки (старая «Заявки из бота» и новая CRM): нажатие на клиента открывает переписку там, где он написал (`ClientChatWindow.cs` в старой кассе: бот — история с ботом, WhatsApp, Instagram).
  - Данные этих разделов, пока нет API, — `Host/OpsStore.cs`, файл `%AppData%\NurMarketDesktopShared\ops\<компания>\ops.json` (общий для кассы и программы владельца на одном ПК; у `--qa-instance` — `NurMarketDesktopSharedQA`). Деньги везде идут настоящими путями: рассрочка — продажа «в долг» + `pay-debt`, сертификат — строка чека и скидка суммой, выдача заказа с резервом — внесением (иначе остаток спишется дважды).
  - Исправлено там же: у кнопок строки чека (−, +, %, опт, ✕) не было `CommandParameter` — они были неактивны; общий `Ui.Cell` делал текст таблиц невидимым.
  - Вид «Старая касса» (владелец 13.10: «вернуть старый дизайн и расположение кнопок»): в `KassaWindow.axaml` — `LegacyGrid` с `CatalogPanelView` и `CustomerCartView` старой кассы на тех же моделях; тема «Как в старой кассе» берёт цвета из словарей старой кассы. Старой панели каталога нужен `ProductThumbService` её сборки — зарегистрирован в `LegacyRegistrations`.
  - Сборка для проверки: `dotnet build -c Debug -p:OutputPath='bin\DevBuild\'` (exe владельца в `bin\Debug` бывает занят). Проверка окон без касаний мыши: отладочные ключи `--qa-instance` (своя папка данных), `--qa-open=<раздел>`, в программе владельца ещё `--qa-scan=<штрихкод>`.
  - 13.10, владелец: «все функции старого маркета — в новом, без API — заглушка; адаптивные экраны; слабые компьютеры; выгрузка платная»:
    - Платные функции — `Host/PaidFeatures.cs`: те же ключи и флаги, что у старой кассы (`LicenseKeys`, `UserPreferences`). Выгрузка Excel/Word/CSV (продажи, документы, печать документа) — `TariffGate.CanUseAnalyticsExport` («Стандарт» — входит, «Старт» — ключ `export`); ценник, редактор этикетки, массовые ценники — ключами `pricetag`/`labeleditor`/`bulktag`. Печать и PDF — бесплатно.
    - Склад: «Печать и ещё ▾» (массовые ценники, редактор этикетки, склад старой кассы), на карточке и в меню строки — ценник, этикетка, «Разбор товара» (`ProductAnalyticsWindow`). В админке — раздел «Склад — все функции» (`WarehouseWindow` старой кассы; для него зарегистрирован `IBarcodeInputService`).
    - Касса: горячие клавиши настраиваются (`PosHotkeyService` старой кассы, меню ☰ → «Горячие клавиши»), F1–F12 — группы быстрых товаров (`HotkeyGroupDialog`).
    - Адаптивность — `Host/Adaptive.cs`: каждое окно при открытии ужимается до рабочей области экрана, при нехватке места — прокрутка; касса — минимум 860×600; в админке уже 1200 px меню сворачивается в ☰.
    - Слабые ПК — `Host/DesktopPerf.cs` (`desktop-perf.json` в папке данных): «Авто» (≤2 ядер или <4 ГБ) — 80 плиток, без фото, без анимаций; «Вкл» — ещё программная отрисовка; переключатель — «Тема и вид кассы». В csproj — `PublishReadyToRun`.
  - 13.10, вторая сверка с холстом (версия 1791862284): новых досок, кроме CRM/КП/счетов (сделаны раньше), нет. Доделано:
    - Касса услуг и записи (С3) и услуга с мастерами (Н5) — `Views/BookingWindow.cs` (окно, `ServiceFlow`: запись → чек, после оплаты процент мастеру и расходники «к списанию»; списание — настоящее, через `WarehouseViewModel.RunWriteOffAsync`). Данные — `OpsStore` (Services, Bookings, MasterEarnings, ConsumableUses, ServicesSettings). Касса: ☰ → «Запись и услуги»; админка: «Продажи → Услуги и запись». В карточке нового товара — тип «Товар / Одежда / Услуга» (одежда — `ProductEditDialog` старой кассы с вариантами).
    - Долги клиентов (доски 9, О1) — `Views/CustomerDebtsWindow.cs` (данные — `PosDebtSalesAsync` + телефоны клиентов, как у старого раздела).
    - Поиск товара: кнопка 🔍 с клавишей (Ctrl+F или назначенная) в кассе; в админке Ctrl+F — строка «Поиск товара или скан», название ищется в разделе «Склад».
    - Ещё в окнах старой кассы (по доскам 8, 10, 12, 14, 16–18, 20–21, 24–36, О2–О6, О11, К1–К5): переделывать по одной.
  - 14.10: разделы старой кассы в админке получают свои стили и ресурсы (`SectionEmbed.Take` переносит `Window.Styles`/`Resources` в обёртку; прячет «Закрыть» и рамку окна — значки MDL2 E921/E8BB). Стандартные кнопки Fluent — в цветах нового дизайна (`Theme/Tokens.axaml`: ButtonBackground… → BrushPanel… — ссылки на те же кисти, темы меняют их на лету). Пункты меню админки — класс `navitem` (наведение не гасит текст). Касса по умолчанию — вид «Старая касса» (`LookSettings.LayoutChosen`), ☰ — боковое меню в оформлении старой кассы (`Views/KassaSideMenu`), быстрые товары — плитки как в старой.
  - 14.10: QR для оплаты по кассирам — `Host/PaymentQrs.cs` (общий с телефоном список: `api/main/app-settings/nurmarket-mobile/`, ключ `PaymentQrs`; сумма — `ElqrQrBuilder`; QR рисует ZXing). Настройка — «Оформление, ККМ, оплата → Оплата и банки», в оплате безналом — банки кассира (до 5) и QR с суммой.
  - 14.10: «История смен» в новом виде — `Views/ShiftsWindow.cs` (первая страница смен сразу, полный список в фоне, отчёт смены `NurCrmReportsApi.GetShiftReportAsync`, «Подробный отчёт» — `ShiftDetailsDialog` старой кассы). Тайм-аут HttpClient приходит как TaskCanceledException — в обработчиках окон ловить все исключения (иначе падение, проверено 14.10).
  - Проверка окон, пока владелец работает на ПК: ключ `--qa-quiet` (отладочная сборка, `Host/QaQuiet.cs`) — окна без фокуса и за левым краем экрана, снимок по hwnd (PrintWindow) работает. 14.10 без этого ключа ввод владельца попал в проверочное окно.
- **Телефон (Flutter)** — `NurMarketMobile` (Flutter 3.47 в `D:\flutter`): кассир — продажа, сканер камерой (весовые этикетки, UPC/EAN), оплата, чек по нормам КР (лента, PDF, QR), приёмка, ревизия, смена, без интернета (очередь с `Idempotency-Key`). `flutter test` — только юнит-тесты, базу не трогают.
  - 13.10, этап 2 — администратор на телефоне (`lib/features/admin/`, вход с главной «Управление», только `Session.isAdmin`): показатели дня, товар — цена продажи и закупки (PATCH products), смены, заказы с сайта и сборка сканером камерой (showcase/orders), клиенты и ручные бонусы (`clients/{id}/bonus/`), заказ поставщику текстом в WhatsApp, журнал «Удалено из чека» (причина спрашивается при смахивании строки в продаже; журнал — на телефоне). Там же: оптовая цена товара, приёмка с ценами (`admin_receive.dart`: с поставщиком — `suppliers/{id}/receipt/`, без — PATCH товара, новый товар прямо из приёмки, наценка +30 % до 5 сом), сотрудники и права (`api/users/employees/`, PATCH can_view_*), уведомления (собираются из заказов, остатков, долгов и очереди телефона), новый товар по штрихкоду (`admin_more.dart`), накладная / товарный чек / счёт PDF из карточки чека (`waybill_pdf.dart`). Продажа «Розница / Опт»: оптовая цена товара в базе телефона (`products.wholesale_price`, схема v3), переключатель на весь чек и «опт» на строке; строка уходит на сервер с ценой опта и `is_wholesale: true`, как у кассы. Скидка на чек и на строку — процентом или суммой в сомах (`askDiscount` в `sale_screen.dart`), журнал внесений/изъятий смены (Т25), тема «как в системе» (ТМ3), общий поиск товаров/клиентов/разделов/отложенных (О8, `home/search_screen.dart`). Одежда (С5): переключатель в профиле «Размеры и цвета при продаже», выбор варианта (`products/{id}/variants/`), строка уходит с `variant`. Прокат (С8, `rentals/rentals.dart`): `api/rentals/` — список, возврат со штрафом, новый прокат с залогом; стоимость — строкой в чек. Новые строки — в `tool/gen_arb.py`, затем `python tool/gen_arb.py` и `flutter gen-l10n`.
  - 13.10: фото товаров — сервер отдаёт их списком `images` (объекты), а не строкой; разбор как у кассы — `CatalogRepository.productImageUrl`, показ — `ProductThumb` (core/util/ui.dart, с cacheWidth). Доступы сотрудника — флаги `can_view_*` профиля (`Session.perms`, `can`, `canRoute`, как `PermissionService` кассы): закрытые разделы не открываются (`/denied`), скидка/удаление строки/возврат — `checkAccess` (core/util/access.dart); профиль перечитывается при каждом запуске.
  - 13.10, сверка с холстом телефона (версия 1791826606 — новых досок, кроме Э1/Э2, нет): новая позиция «Товар / Одежда / Услуга» (`features/admin/new_item.dart`: одежда — варианты POST products/{id}/variants/, услуга — kind=service, мастера/длительность/расходники в настройке `service.<id>`; «Найти в интернете» — global-barcode + браузер); запись к мастерам С7 (`features/sale/bookings.dart`, настройка `bookings`); приёмка — окно «Цены продажи» (ПР5) и итог перед проведением (ПР6); КП и счета (`features/admin/quotes.dart`, настройка `quotes`, PDF `DocKind.quote`), ККМ и ЭСФ (очередь чеков — настоящая, ЭСФ — черновики, отправка ждёт API ГНС). Всё без API — на телефоне, на экранах есть пометка.
  - 13.10 (поздно): склад — «+ Новый товар», «Массовая загрузка» (`features/stock/bulk_import.dart`, свой разбор .xlsx через archive+xml — пакет excel конфликтует с pdf), «Умная камера» (`core/ai/vision.dart` — Gemini с ключом TelegramAiKey из личных app-settings или вставленным на телефоне, `features/scan/smart_camera.dart`; в сканере режим «По виду»). Сканер: «Назад» → продажа; не найден штрихкод → «Добавить товар» (`/admin/new-product?barcode=`). Новая позиция: оптовая цена, поставщик со скидкой (количество — приходом от поставщика), доп. штрихкоды упаковки (`alternate_barcodes` {barcode,name,quantity}; в базе телефона строка «код|кол-во|название», `AppDatabase.packQuantity`), фото — камера, галерея, интернет (`core/util/photo_finder.dart`, как ProductPhotoFinder), загрузка POST products/{id}/images/. Производительность: поле `searchKey` (схема v4) — поиск по-русски без учёта регистра и по словам; продажа и склад без лимитов 60/500; поиск с задержкой 250 мс; клиенты/заказы/движения — все страницы (`getAllPages`).
  - 14.10: общий отступ снизу на всё приложение (`app.dart`, `builder` + `SafeArea(bottom)` — Android 15 рисует под полосой жестов); QR для оплаты по кассирам (`features/payment/payment_qr.dart`, `Elqr` — перенос `ElqrQrBuilder`, список общий с десктопом); «Настройка ИИ» (`AiSettingsScreen` в `advisor.dart`: Gemini + запасные Groq/OpenRouter, ключи в secrets app-settings); «Управление» группами с поиском; выбор товара с фильтрами (`core/util/product_picker.dart`); тема «По времени» (`Prefs.auto`); `LeaveGuard` (ui.dart) — «Вы точно хотите выйти?»; стресс-тесты `test/stress_test.dart` (20 000 товаров, 500 случайных чеков, 1000 QR, мусор в массовую загрузку).
  - 14.10 (позже): умная камера — видоискатель как у сканера (`features/scan/live_shot.dart`, пакет `camera`, «Авто» через 2 с) вместо системной камеры; обучение товара — 3 снимка (справа, слева, сверху) + штрихкод (`teachCamera`, эталоны — `core/ai/product_refs.dart`, папка refs + настройка `refs.index`), при продаже «По виду» второй шаг `VisionService.identify` сравнивает кадр с эталонами (до 8 товаров; с других телефонов — фото товара из NurCRM). Журнал аудита — `core/util/audit.dart` (файл `audit.jsonl`, 5000 записей; продажи, чек, скидки, удаления, возвраты, цены/остатки, товары, приёмка, деньги, смены, настройки, вход/выход, открытые экраны) и экран `AuditLogScreen` («Управление → Сотрудники»).
  - На Redmi установка по USB требует подтверждения на самом телефоне (иначе `INSTALL_FAILED_USER_RESTRICTED`).

- **Несколько ключей Gemini (26.10.2026, владелец: «добавь возможность добавить API Gemini из нескольких Google-аккаунтов»)** — `Services/GeminiKeyPool.cs` (Infrastructure): список до 10 ключей, окно `Views.axaml/Main/GeminiKeysWindow.cs` (⚙ в «Нур Советнике» и «Несколько ключей…» в настройках бота). Хранение: основной ключ — как раньше `UserPreferences.TelegramAiKey` (его читают мобилка, бот на сервере, старые кассы); остальные и метка основного — `TelegramAiKeysExtra`, строка JSON `{"v":1,"mainLabel":"","mainEnabled":true,"extra":[{"label":"","key":"","enabled":true}]}` (на диске DPAPI, в облаке — секрет `TelegramAiKeysExtra` в `secrets` личных настроек, как `TelegramAiKey`; `SettingsCloudSync.SecretKeys`). Выбор — «первый рабочий» по порядку списка; 429/RESOURCE_EXHAUSTED — ключ «исчерпан до …» (минутный лимит — retryDelay из ответа, по умолчанию 60 с; суточный `PerDay` — до полуночи по тихоокеанскому времени), «API key not valid»/401/403 — «недействителен» (повтор через 30 мин); блокировка на пару (ключ, модель), состояние только в памяти. Применено в `TelegramAiChat.GenerateGeminiAsync`, `TelegramVoice.PostAsync`, `GeminiLiveVoice.SessionAsync` (ключ выбирается при открытии соединения). `TestKeyAsync` и «Проверить» ключ не перебирают (проверка — GET списка моделей). В журнал ключ не пишется — только номер и последние 4 символа. **ЧЕСТНОЕ ПРЕДУПРЕЖДЕНИЕ (оно же в окне ключей):** правила Google могут запрещать обход лимитов несколькими аккаунтами/проектами — есть риск блокировки ключей; на бесплатном уровне Google может использовать запросы для улучшения своих продуктов. Бот на сервере (`ai_key`) по-прежнему получает только основной ключ.


## 13. Синхронизация (29.10.2026, жалоба «очень медленная»)
Все интервалы — в одном месте, `SyncCenter` (Infrastructure/Services/SyncCenter.cs). Что опрашивает сервер: лента товаров `products/changes` (~8 с, ETag/304, `AvaloniaCatalogCacheService`); общие настройки компании `app-settings/nurmarket-kassa` (30 с, `SettingsCloudSync.StartCompanyPolling`, без перезапуска; журнал SETTINGS_SYNC; пока на экране окно настроек — применение ждёт: `PosSettingsWindow.HasUnsavedEdits`); пульс чужих продаж (`RemoteSalesPulse`, 20 с, один запрос «последние 5 чеков» → `PosDataEvents.SalesChanged` и перечитывание остатков не чаще раза в 60 с); очередь офлайн-чеков (`SyncService`, 15 с + сразу после постановки чека в очередь); сторож смены кассы (30 с, «Касса: N» раз в минуту); «Сводка» владельца (30 с + по `SalesChanged`). Остаток из ленты применяется по товарам (`FeedStockGuard`: не трогает товары с неотправленным чеком, продажей соседа по LAN и правкой остатка менее 90 с назад). `SyncCenter.ForceSyncAllAsync()` — «обновить всё» (настройки + очередь + смена + каталог), её зовут кнопки «Обновить» кассы и владельца. Потолок опроса — сервер: события реального времени (SSE/WebSocket), ETag на app-settings и остаток в ленте при продаже — Б-102 в `docs/ТЗ Умару — СРОЧНО баг сервера, цена продажи меняется сама (27.10.2026).md`.
