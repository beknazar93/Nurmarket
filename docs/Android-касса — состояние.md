# Android-касса — состояние (04.10.2026)

Цель: «точная копия кассы» + программа владельца для Android-кассовых аппаратов (тип CaravPOS: большой сенсорный экран, второй экран покупателя, встроенный термопринтер, сканер).

**Итог:** проект собирается в APK без ошибок. **На устройстве не запускался ни разу** — на этом ПК нет эмулятора (выключена виртуализация) и нет Android-аппарата. Всё ниже, кроме «собирается», — по коду, не по живой проверке.

## 1. Где что лежит

| Что | Где |
|---|---|
| Проект | `NurMarketKassa.Avalonia/NurMarketKassa.Android/NurMarketKassa.Android.csproj` (net8.0-android, Avalonia.Android 11.2.5, `kg.nurmarket.kassa`, minSdk 24 = Android 7.0, arm + arm64) |
| Код кассы | подключён ссылками из `NurMarketKassa.Avalonia/` (как в `NurMarketKassa.Portable`), константы `NURPORTABLE;NURANDROID` |
| Окна-слои | `NurMarketKassa.Android/Shell/` — `Window.cs`, `TopLevel.cs` (+ `Screens`), `WindowLayerHost.cs`, `AndroidDesktopLifetime.cs`, `AndroidNestedLoop.cs` |
| Платформа | `NurMarketKassa.Android/Platform/` — `AndroidBootstrap.cs` (запуск), `AndroidPrinterTransport.cs` (печать), `AndroidCustomerDisplay.cs` (второй экран), `AndroidSound.cs` |
| Вход | `NurMarketKassa.Android/MainActivity.cs` — две иконки: «NurMarket Касса» и «NurMarket Владелец» (отдельный процесс `:owner`, своя папка данных) |
| Переписывание разметки | `NurMarketKassa.Android/build/NurAxamlRewrite.targets` |
| Release APK (ставить его) | `NurMarketKassa.Android/bin/Release/net8.0-android/publish/kg.nurmarket.kassa-Signed.apk` (~86 МБ, без обрезки кода и AOT) |
| Debug APK | `NurMarketKassa.Android/bin/Debug/net8.0-android/kg.nurmarket.kassa-Signed.apk` (~180 МБ, с отладочными символами) |

## 2. Как собрать

На ПК нужна платформа Android **API 34** (поставлена 04.10 через `sdkmanager "platforms;android-34"`; у .NET 8 Android целевой уровень 34). Путь к SDK и JDK проект берёт сам (`%LOCALAPPDATA%\Android\Sdk`, `JAVA_HOME`).

```powershell
$env:DOTNET_GCHeapHardLimit = "0x30000000"
cd NurMarketKassa.Avalonia
dotnet build   NurMarketKassa.Android/NurMarketKassa.Android.csproj -c Debug   -f net8.0-android -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet publish NurMarketKassa.Android/NurMarketKassa.Android.csproj -c Release -f net8.0-android -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```
Debug ≈ 2 мин с нуля, Release ≈ 2,5 мин. APK подписан отладочным ключом — ставится на аппарат как есть (`adb install -r …apk` или файлом). Для выпуска клиентам нужен свой ключ (keystore) — не делалось.

## 3. Как устроено

1. **Одно окно на Android.** У Avalonia на Android один вид, а у кассы ~110 классов окон. Свой `NurMarketKassa.Window` (наследник ContentControl) повторяет API `Avalonia.Controls.Window`: Title, размеры, SizeToContent, WindowState, Owner, Show/ShowDialog/Close/Hide/Activate, Opened/Closing/Closed/Activated, Position, Screens, Clipboard, StorageProvider и т.д. Код окон не меняется: C# находит имя `Window` сначала в объемлющем пространстве имён `NurMarketKassa`, а не в `using Avalonia.Controls`. Так же подменены `TopLevel`, `Screens`, `WindowClosingEventArgs`, `IClassicDesktopStyleApplicationLifetime`.
2. **Показ окна** = слой в `WindowLayerHost` (единственный вид). `ShowDialog` — с затемнением, всё ниже не принимает касания. Главные окна (заставка, вход, касса, владелец) и развёрнутые/большие окна — на весь экран; остальные — по центру, не больше экрана. Окна, у которых в Windows была системная рамка, получают полосу заголовка с «✕». Кнопка «Назад» Android = «✕» верхнего окна (главное окно так не закрывается). «Свернуть» = увести программу в фон.
3. **Разметка.** При сборке каждый `.axaml` кассы с корнем `<Window>` копируется в `obj\nur-axaml\` с заменой на `<mw:Window xmlns:mw="using:NurMarketKassa">` (и `Window.Styles`, `AncestorType=Window`, `Selector="Window"`). Исходники Windows-кассы не меняются.
4. **Время жизни.** После настройки Avalonia (`AfterSetup`) настоящее время жизни Android оборачивается в `AndroidDesktopLifetime` — для кода кассы оно «настольное» (desktop.MainWindow, Windows, Shutdown). Для этого проект компилируется против настоящих сборок Avalonia (`AvaloniaAccessUnstablePrivateApis`, версия закреплена 11.2.5).
5. **Синхронные диалоги** (~160 мест `PosDialogHost.Show`, `PosMessageBox.Show`, `PosConfirmDialog.Show`…). В Windows они крутят вложенный цикл `Dispatcher.MainLoop`, на Android его нет. Сделано два слоя:
   - **основной сценарий продажи переведён на async** (работает без вложенного цикла): подтверждение оплаты, «принтер не подключён», сообщения оплаты (`IDialogService`, `IUserPrompts` — ветки `#if NURANDROID`), добавление товара (пачка/вес/размер-цвет, неизвестный штрихкод), скидки на чек и строку, «продажа в убыток», доп. услуга, открытие смены, внесение/изъятие, возврат (подтверждения и причина), отложенные чеки. Закрытие смены уже было async;
   - **остальные места** ждут ответа через `AndroidNestedLoop` — вложенный `Looper.Loop()` Android с выходом по особому исключению (известный приём «модального диалога» на Android). Если на каком-то аппарате он поведёт себя плохо — создать пустой файл `android-no-nested-loop` в папке данных программы: тогда такие диалоги показываются без ожидания (сообщения работают, подтверждение = «нет»).
6. **Печать** — через ту же `PrinterPortService.SendRawBytes`, что в Windows (чек ESC/POS, денежный ящик, этикетки TSPL, табло). Android подставляет `PrinterPortService.PlatformTransport`. В поле «Порт принтера» настроек:
   - `USB` — первый USB-принтер (класс 7); `USB:0FE6:811E` — по VID:PID. При первой печати Android спросит разрешение на USB-устройство;
   - `BT:00:11:22:33:44:55` — Bluetooth-принтер (сопрячь заранее в настройках Android); `BT` — первый сопряжённый принтер;
   - `TCP:192.168.1.50` — сетевой принтер (порт 9100), `TCP:адрес:порт` — другой порт.
   USB-устройства и сопряжённые Bluetooth появляются в списке «найденные принтеры» настроек.
7. **Экран покупателя** — второй дисплей аппарата через Android `Presentation`: `CustomerDisplayWindow` кладётся в отдельный AvaloniaView на втором дисплее, а второй дисплей сообщается коду кассы как второй монитор (`Screens.All`). Нет второго дисплея — окно показывается слоем (предпросмотр).
8. **Звук** — MediaPlayer (WAV во временный файл). **Обновления** — Velopack на Android не включается (нет адреса манифеста); обновлять установкой нового APK.

## 4. Что должно работать (по коду, не проверено на аппарате)

- Запуск: заставка → автовход / окно входа → касса или программа владельца; данные — во внутренней папке программы (`files/.config/NurMarketKassa`, у владельца `NurMarketOwner`), SQLite (`libe_sqlite3.so` для arm и arm64 в APK).
- Продажа: каталог, корзина, сканер-клавиатура (HID — как клавиатура; горячие клавиши окна работают, пока фокус в окне кассы), оплата нал/безнал/смешанная/долг, скидки, возврат, смена, внесение/изъятие, отложенные чеки, без интернета (очередь — та же логика).
- Печать чека ESC/POS по USB/Bluetooth/TCP, ящик через принтер.
- Программа владельца: разделы открываются слоями поверх области главного окна (через Position, как в Windows).
- Обмен по локальной сети (TCP 47810+, UDP 47809), весы по сети (Штрих-ПРИНТ UDP, Rongta/TM-30F TCP), Telegram-бот, NurCRM — обычный .NET, должны работать.

## 5. Что НЕ работает или не проверено

| Что | Почему / что делать |
|---|---|
| **Ничего не запускалось на устройстве** | Нужна живая проверка на CaravPOS: запуск, вход, продажа, печать, второй экран, сканер |
| Вложенный цикл диалогов (`AndroidNestedLoop`) | Приём рабочий, но рискованный; проверить на аппарате первым делом (любое окно «Да/Нет» вне продажи) |
| Сайт NurCRM внутри программы (WebView2) | Только Windows; показывается «Откройте в браузере» |
| Открытие ссылок, PDF/HTML-документов, AnyDesk, osk.exe (`Process.Start`, 16 мест) | На Android не работает — заменить на `TopLevel.Launcher` |
| Печать через драйвер Windows: очередь, LPT, COM, WinUSB, графический чек, ценники/этикетки через `System.Drawing` | Только Windows. На Android — ESC/POS текстом и TSPL |
| Выгрузки Excel/Word/PDF «сохранить как…» | Диалог файлов Android (SAF) отдаёт не путь, а content://; код пишет по пути — нужна доработка. QuestPDF: родная библиотека есть только для arm64 |
| Голосовое управление (Vosk) | Нет родной библиотеки под Android |
| Встроенный принтер CaravPOS | Если он виден как USB-принтер — `USB`; если только через SDK производителя (AIDL-сервис) — нужна отдельная доработка под конкретную модель |
| Весы по COM (`SerialPort`) | На Android нет; сетевые весы — да |
| Приём UDP-рассылки в локальной сети (поиск соседей) | На Wi-Fi Android может требовать `MulticastLock` — не сделано |
| Значки шрифта «Segoe MDL2 Assets» | На Android такого шрифта нет — часть старых значков будет квадратиками (новые векторные значки — в порядке) |
| Журнал | `files/.local/share/NurMarketKassa/Logs` — внутренняя память, без root не достать; стоит вывести в `Android/data/kg.nurmarket.kassa/files` |
| Подписание для клиентов, свой значок, выпуск | Не делалось (APK подписан отладочным ключом) |

## 6. Изменения в файлах Windows-кассы

Windows-касса (`NurMarketKassa.Avalonia.csproj`) и `NurMarketKassa.Portable` после правок собираются с 0 ошибок. Правки — либо под `#if NURANDROID`, либо помощники, которые в Windows делают ровно прежнее:

| Файл | Правка |
|---|---|
| `Infrastructure/Services/PrinterPortService.cs` | `PlatformTransport` (null в Windows) — проверка в `ProbePort`, `WritePayload`, `IsSpoolerPort` |
| `Infrastructure/Services/IPlatformPrinterTransport.cs` | новый интерфейс |
| `Infrastructure/Services/PrinterDiscoveryService.cs` | принтеры платформы в списке (в Windows пусто) |
| `Avalonia/Views.axaml/Dialogs/PosDialogHost.cs` | `#if NURANDROID` вместо `MainLoop`; новый `ShowModalAsync` (в Windows = `Task.FromResult(Show(...))`) |
| `PosConfirmDialog`, `PaymentConfirmationDialog`, `PrinterNotConnectedDialog` (`*.axaml.cs`), `Services/PosMessageBox.cs` | новые `ShowModalAsync`/`ConfirmYesNoModalAsync`/`ShowCheckoutModalAsync` (в Windows — прежний синхронный показ) |
| `Services/AvaloniaDialogService.cs`, `Services/AvaloniaUserPrompts.cs` | ветки `#if NURANDROID` (async), ветка Windows без изменений |
| `MainKassir/MainWindow.Dialogs.cs`, `MainKassir/MainWindow.axaml.cs`, `Dialogs/CashOperationsDialog.axaml.cs`, `Dialogs/ReturnSaleDialog.axaml.cs` | `PosDialogHost.Show` → `await ShowModalAsync` в async-методах (в Windows поведение то же); 6 методов `Task` стали `async Task` (их вызывающие и так ждут результат); возврат в `NavigateReturn` — `#if NURANDROID` |
| `Services/AccountCatalogIsolation.cs`, `Services/AvaloniaCustomerDisplayService.cs`, `Services/AvaloniaSettingsImagePicker.cs`, `Views/Settings/AccountView.axaml.cs`, `Views.axaml/Login/LoginWindow.axaml.cs` | `#if NURANDROID` там, где время жизни Avalonia указано полным именем |

Windows-кассу после этих правок **вживую не запускали** (только сборка). Перед выпуском — обычный регресс (`tools/qa/deploy_both.ps1`, продажи/долг/возврат/смена).

## 7. Что дальше (по порядку)

1. Поставить APK на CaravPOS (или любой Android-планшет 7.0+), снять журнал (`adb logcat`), пройти: запуск → вход → продажа → печать → смена.
2. Проверить вложенный цикл диалогов; при проблемах — файл `android-no-nested-loop` и перевод оставшихся мест на async.
3. Встроенный принтер CaravPOS: узнать, виден ли он как USB (VID:PID в списке настроек); если нет — SDK производителя.
4. Заменить `Process.Start` на `Launcher`, выгрузки — на SAF-потоки, журнал — во внешнюю папку программы.
5. Свой ключ подписи, значок, выпуск APK (отдельный репозиторий/канал обновлений).
