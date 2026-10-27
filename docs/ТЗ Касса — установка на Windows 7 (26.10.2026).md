# ТЗ Касса — установка на Windows 7 (26.10.2026)

Просьба владельца (26.10.2026): «наш десктоп: добавь возможность установки на Windows 7».
Это исследование и ТЗ. Код продукта в рамках этой задачи не менялся.
Проверено на этом ПК: разобраны все csproj, найдены все вызовы Windows API в коде кассы, разобраны таблицы импорта всех native-файлов из установленной кассы 1.17.61 (`%LOCALAPPDATA%\NurMarketKassa\current`) и установщика Velopack. Внешние факты взяты из официальной документации (ссылки в разделе 12).

---

## 0. Короткий ответ

**Можно, но только с оговорками и не так, как сейчас.** Касса 1.17.61 в нынешнем виде на Windows 7 **не установится**: установщик `Setup.exe` и программа обновления `Update.exe` (Velopack) не запускаются на Windows 7. Им нужны `combase.dll` и `GetDpiForSystem`, а это есть только в Windows 8 и Windows 10.

Сама касса (.NET 8 + Avalonia 11.2.5), по нашему разбору файлов и по отзывам сообщества, **скорее всего запустится** на Windows 7 SP1 со всеми обновлениями. Официально Microsoft это не поддерживает. Нужно:
1. ставить кассу не через Velopack, а архивом со своим скриптом;
2. один раз включить TLS 1.2 в реестре (в Windows 7 он выключен по умолчанию, без него касса не подключится к `app.nurcrm.kg`);
3. на Windows 7 выключить пять функций: встроенный браузер WebView2, проверку голоса (onnxruntime), временный адрес для поиска весов, автообновление Velopack и значки MDL2 (заменить своим шрифтом NurIcons);
4. проверить на настоящем ПК с Windows 7: на виртуальной машине или у клиента.

**Рекомендация.** Сначала ответить на вопрос «сколько клиентов на Windows 7 и что у них за ПК» (раздел 8).
- **Если таких клиентов 1–5:** дешевле и безопаснее поставить им Windows 10 (вариант D) или нашу Linux-кассу (вариант C). Писать код не нужно.
- **Если клиентов много и обновить ПК нельзя:** вариант **A′** — канал «Win7» из **того же кода и того же .NET 8**, без переписывания под .NET Framework. Это 6 пакетов для Sonnet (раздел 9).
- **Не делать:** переписывать под .NET Framework 4.8 (около 255 тыс. строк, тысячи правок, две кассы навсегда) и ставить сторонние «ядра» вроде VxKex (вариант B).

---

## 1. Что именно мешает (проверено на файлах 1.17.61)

### 1.1. Установщик и обновления: блокер, касса не установится

| Файл | Откуда | Что импортирует из «не Windows 7» | Итог на Windows 7 |
|---|---|---|---|
| `setup.exe` (vpk 1.0.1, `~/.dotnet/tools/.store/vpk/1.0.1/.../vendor/`) | установщик кассы | `combase.dll` (появилась в Windows 8), `GetDpiForSystem` (Windows 10 1607) | **не запустится**: «точка входа не найдена» или «нет DLL» |
| `update.exe` (vpk 1.0.1) | кладётся при установке | `combase.dll` (CoTaskMemAlloc/Free) | **не запустится** |
| `%LOCALAPPDATA%\NurMarketKassa\Update.exe` (Velopack 1.2.0, уже у клиентов) | автообновление | `combase.dll` | **не запустится**: обновление из кассы сломается |
| `stub.exe` | ярлык-запускалка | — | без замечаний |

У Velopack есть исправление: PR #1112 «Build Windows x86/x64 on stable with Win7 import shims» (влит 17.10.2026). Оно вошло **только в предварительную версию 1.3.39-g8d2844f** (17.10.2026). Последняя стабильная версия — 1.2.161 (29.09.2026), в ней этого нет. Сам автор пишет «Not yet verified on real Windows 7». Значит, пока стабильного Velopack для Windows 7 нет.

### 1.2. Сама касса (.NET 8): официально нельзя, фактически, скорее всего, можно

- **Позиция Microsoft:** ни одна поддерживаемая версия .NET не работает на Windows 7 и 8.1. Последней была .NET 6, её поддержка кончилась **12.11.2024**. Для .NET 7, 8, 9 и 10 Windows 7 в списке нет. Минимум для .NET 8 — Windows 10 1607 (и для Windows 10 — только редакции LTSC/Enterprise).
- **Что показал наш разбор файлов.** Ни один файл .NET 8 из кассы (`coreclr.dll`, `clrjit.dll`, `hostfxr.dll`, `hostpolicy.dll`, `System.IO.Compression.Native.dll`, `NurMarketKassa.Avalonia.exe`) **не импортирует статически** функций из Windows 8+ (`GetSystemTimePreciseAsFileTime`, `WaitOnAddress`, `CreateFile2`, `VirtualAlloc2`, `MapViewOfFile3`, `SetThreadDescription` и т. п.). Такие функции `coreclr.dll` ищет «на лету» (GetProcAddress) и обходится без них, если их нет. WinRT (`api-ms-win-core-winrt-l1-1-0.dll`) подключается отложенно, только при первом вызове, поэтому запуск не ломает. Именно эта ошибка сломала одиночные exe .NET 6 на Win7, и её починили отложенной загрузкой.
- **Отзывы сообщества.** Программы на .NET 7–10 работают на Windows 7 SP1 («they still kept the old code paths, so it still works»). Главная проблема — W^X (защита памяти, включена по умолчанию с .NET 7): «excessive memory usage and CPU spikes when launching and closing applications». Лечится переменной окружения `DOTNET_EnableWriteXorExecute=0`. В декабре 2025 там же сообщили, что примеры на .NET 10 работают «without issues». Предупреждение: Microsoft удаляет код для Win7 «more aggressively», и в .NET 11+ совместимость будет «less certain».
- **Чего нет в нашем exe.** В папке кассы нет UCRT (`ucrtbase.dll`, `api-ms-win-crt-*.dll`), а `coreclr.dll`, `hostfxr.dll` и другие его требуют. На Windows 7 обязательно нужны **KB2999226** (Universal CRT) или «Visual C++ 2015–2022 Redistributable», а также **KB3063858** или KB2533623 (`SetDefaultDllDirectories` и `AddDllDirectory`). Эти же пакеты Microsoft требовал и для .NET 6 на Win7.
- **Срок .NET 8.** Поддержка .NET 8 кончается **10.11.2026**, через две недели. Основную кассу всё равно придётся переводить на .NET 10. Канал Win7 разумно заморозить на последнем выпуске 8.0.x: это свежее, чем .NET 6, который кончился в 2024 году.

### 1.3. Перечисленные в задаче функции Windows API

| Вызов | Есть в Win7 SP1? | Касается нас? |
|---|---|---|
| `GetSystemTimePreciseAsFileTime` | нет (Windows 8+) | .NET 8 ищет его «на лету» и не падает. Статически его импортирует только `onnxruntime.dll` (см. 1.4) |
| `api-ms-win-core-*` (новые контракты: `synch-l1-2-0`, `path-l1-1-0` …) | нет | `onnxruntime.dll` импортирует `api-ms-win-core-path-l1-1-0.dll`. Остальные наши файлы — нет |
| `bcryptprimitives.dll` / `ProcessPrng` | DLL есть, `ProcessPrng` нет | в наших файлах не найдено (это ловушка Rust, а не .NET) |
| WinHTTP и KB3140245 | KB нужен **только для WinHTTP** | HttpClient в .NET 8 идёт через SocketsHttpHandler и Schannel, а не через WinHTTP. Нужен **реестр Schannel**, см. 1.5 |
| `ProcessThreadsApi` (`SetThreadDescription`, `GetThreadInformation`) | нет | `coreclr` ищет их «на лету». Не мешает |
| Значки шрифта «Segoe MDL2 Assets» (63 места в коде) | шрифта нет (он из Windows 10) | без замены вместо значков будут квадраты. Лечится нашим `IconFontFallback` (NurIcons), как на Linux и Android |

### 1.4. Native-библиотеки из пакетов (разобраны импорты)

| Файл | Чей | Win7 |
|---|---|---|
| `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` (2.88.9 / 7.3.0.3) | Avalonia: отрисовка | ✅ импортов из Win8+ нет (x64 и x86) |
| `av_libglesv2.dll` (ANGLE) | Avalonia: GPU | ✅ (заголовок рассчитан на Windows XP+). Нужен Direct3D 11, на старой видеокарте — программная отрисовка |
| `e_sqlite3.dll` (SQLitePCLRaw 2.1.6) | локальная база | ✅ (x64 и x86) |
| `QuestPdfSkia.dll`, `qpdf.dll` + mingw-библиотеки (QuestPDF 2024.12.3) | PDF | ✅ импортов из Win8+ нет (x64 и x86) |
| `libvosk.dll` + mingw (Vosk 0.3.38) | голос | ✅, но **только x64**: сборки x86 у пакета нет |
| `onnxruntime.dll` (sherpa-onnx 1.13.5) | проверка голоса владельца (`SpeakerVerificationService`) | ❌ **статически** импортирует `CreateFile2`, `GetSystemTimePreciseAsFileTime`, `GetProcessMitigationPolicy`, `CreateDXGIFactory2`, `api-ms-win-core-path-l1-1-0.dll`. Сама касса не упадёт: DLL грузится только при первом вызове. Но функция работать не будет, её нужно скрыть |
| `WebView2Loader.dll` (SDK 1.0.2957.106) | встроенный браузер | ❌ Microsoft: «WebView2 SDK versions 1.0.1519.0 and higher will no longer support Windows 7». Последняя среда WebView2 для Win7 — **109** (январь 2023). Ошибка запуска уже ловится в `Controls/WebView2Host.cs` (`InitializationFailed`) |
| `msquic.dll` | HTTP/3 в .NET | на Win7 не работает, но .NET без него переходит на HTTP/1.1 и 2. Не мешает |
| `wpfgfx_cor3.dll`, `D3DCompiler_47_cor3.dll`, `PresentationNative_cor3.dll` | части WPF/WinForms (попадают в сборку из-за `net8.0-windows` и пакета WebView2) | импортов из Win8+ нет. Касса их почти не использует |

Как проверяли: скрипт `pefile` читает статические и отложенные импорты и сверяет их со списком функций и DLL, которых нет в Win7. Скрипт — в приложении A. Это **эвристика**: он отвечает «что точно сломает загрузку», но не гарантирует, что всё заработает. Окончательная проверка — только живой запуск.

### 1.5. TLS 1.2 к `app.nurcrm.kg`: без этого касса не подключится

- В Windows 7 Schannel умеет TLS 1.2, но **по умолчанию у клиента он выключен**. Таблица Microsoft «Windows 7/Windows Server 2008 R2: TLS 1.2 Client — Disabled». TLS 1.3 в Windows 7 нет совсем.
- Касса нигде не задаёт `SslProtocols` (в коде 0 мест), то есть берёт версию по умолчанию от ОС. На Win7 это TLS 1.0. Современный сервер его, скорее всего, отвергнет. Итог: «Нет связи» при входе, продажи копятся в офлайн-очереди.
- **KB3140245 не поможет**: он настраивает только WinHTTP, а наш HttpClient идёт мимо WinHTTP.
- Лечение (одно на всю систему, нужны права администратора, документировано Microsoft):
  `HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client` → `DisabledByDefault=0`, `Enabled=1`. Затем перезагрузка.
  Другой путь — задать `Tls12` явно в коде. Но `HttpClient` в коде создаётся примерно в 30 местах (`new HttpClient`, `static readonly HttpClient … = new()`), это большая правка. Поэтому основной путь — реестр в скрипте установки. В коде — только понятное сообщение «включите TLS 1.2».
- **Не проверено (проверить в пакете W7-6):** какие наборы шифров отдаёт `app.nurcrm.kg`. В Windows 7 нет части современных наборов (по нашим данным — ECDHE-RSA-AES-GCM). Если сервер разрешает только их, рукопожатие не пройдёт даже с включённым TLS 1.2. Проверка — отчёт SSL Labs для `app.nurcrm.kg`, строка «IE 11 / Win 7» в «Handshake Simulation». Её запускает владелец или Sonnet с его разрешения. Если не проходит — просить бэкенд добавить совместимый набор шифров.
- То же касается GitHub (скачивание обновлений) и корневых сертификатов: старый Win7 без обновлений корневых сертификатов может не доверять новым центрам сертификации. Лечение — включённое автообновление корневых сертификатов и KB4474419 (поддержка SHA-2).

---

## 2. Avalonia 11.2.5 на Windows 7

- Официальная документация Avalonia 11: поддерживаются Windows 8.1, 10 и 11. О Windows 7 сказано: «While Avalonia apps will successfully run on Windows 7, this legacy platform receives limited support. We no longer provide bug fixes for Windows 7-specific issues.» Значит, запускается, но ошибки только под Win7 никто чинить не будет.
- Avalonia 11.0.8 исправила ошибку Win7 с `api-ms-win-core-winrt-string-l1-1-0.dll` (PR #14436). Версия 11.2.5 новее, исправление в ней есть.
- **Avalonia 12** требует .NET 8+ и в таблице Windows не упоминает ничего старше Windows 10. Если основная касса перейдёт на Avalonia 12, канал Win7 должен остаться на 11.x.
- Отрисовка. Композиция WinUI (Windows 10+) и DirectComposition (Windows 8+) на Win7 недоступны, Avalonia сама переходит на обычную поверхность окна. ANGLE (OpenGL ES поверх Direct3D 11) работает на Win7 SP1. Для старых драйверов нужен **KB2670838** (Platform Update, DXGI 1.2), но и с ним на слабой видеокарте бывают сбои. Надёжнее на Win7 сразу включать программную отрисовку `Win32RenderingMode.Software`. В `Program.cs` такая ветка уже есть («режим слабого ПК», `IsLowPerformanceModeEnabled`).
- DPI: манифест объявляет PerMonitorV2, Win7 его не понимает и берёт `dpiAware=true/pm`, то есть «системный DPI». Это нормально. Win7 уже указана в `supportedOS` в `app.manifest`.
- Пакеты Avalonia 11.2.5 собраны под `netstandard2.0`, `net6.0` и `net8.0`. Значит, технически они работают и на .NET Framework 4.6.2+ (4.8), и на .NET 6. Native-части SkiaSharp и HarfBuzzSharp тоже есть под `net462`.

---

## 3. Наши зависимости (реальный список из csproj)

Проекты: `NurMarketKassa.Avalonia` (`net8.0-windows`, `SupportedOSPlatformVersion 7.0`, self-contained win-x64, ReadyToRun), `.Infrastructure`, `.ViewModels`, `.Core`, `.Ui.Shared` (`net8.0`), `.Portable` (`net8.0`, Linux). Кода около **255 тыс. строк C#**: Avalonia ~150 тыс. + 156 axaml, Infrastructure ~92 тыс., ViewModels ~11,6 тыс., Core ~2,5 тыс.

| Пакет | Версия | Есть сборка для net48 / net6 | Win7 |
|---|---|---|---|
| Avalonia (+Desktop, Fluent, Inter, DataGrid, Headless, Xaml.Loader) | 11.2.5 | netstandard2.0, net6, net8 | работает, «limited support» |
| CommunityToolkit.Mvvm | 8.4.0 | netstandard2.0 | ✅ |
| MediatR | 12.4.1 | netstandard2.0, net6 | ✅ |
| Microsoft.Extensions.Hosting / DI / Logging | 8.0.x | net462, net6 | ✅ |
| Microsoft.Web.WebView2 | 1.0.2957.106 | net462 | ❌ (SDK ≥1.0.1519 без Win7; среда ≤109) |
| MySqlConnector | 2.4.0 | net48, net6 | ✅ (на кассе почти не используется) |
| DocumentFormat.OpenXml | 3.1.0 | net46, netstandard2.0 | ✅ |
| Concentus (+Oggfile) | 2.2.2 | netstandard2.0 | ✅ |
| linq2db (+PostgreSQL) | 5.4.1 | net472, net6 | ✅ |
| Microsoft.Data.Sqlite + e_sqlite3 | 8.0.11 / 2.1.6 | netstandard2.0, net6 | ✅ (native x64 и x86) |
| Microsoft.Win32.Registry | 5.0.0 | — | ✅ |
| NAudio | 2.2.1 | net472, net6 | ✅ (WASAPI есть с Vista) |
| Octokit | 14.0.0 | — | в коде не используется (0 мест) |
| org.k2fsa.sherpa.onnx (+runtime win-x64) | 1.13.5 | net45, net6 | ❌ native `onnxruntime.dll` (см. 1.4) |
| QuestPDF | 2024.12.3 | netstandard2.0, net6 | ✅ (native x64 и x86 без Win8+) |
| System.Drawing.Common | **10.0.8** | net462, net8 (**net6 нет**) | ✅ GDI+ есть в Win7. Для net6 пришлось бы 8.0.x |
| System.IO.Ports | **10.0.8** | net462, net8 (net6 нет) | ✅ |
| System.Security.Cryptography.ProtectedData | 8.0.0 | ✅ | ✅ (DPAPI) |
| System.Text.Encoding.CodePages | **10.0.8** | net462, net8 (net6 нет) | ✅ |
| Velopack (библиотека) | 1.0.1 | net472, net8 | библиотека — ✅. **Setup.exe и Update.exe — ❌** (см. 1.1) |
| Vosk | 0.3.38 | netstandard2.0 | ✅ только x64 |
| WebSocketSharp-netstandard | 1.0.1 | net45 | ✅ (агент весов и принтера) |
| ZXing.Net | 0.16.10 | net48, net6 | ✅ |

### 3.1. Вызовы Windows API в коде кассы (P/Invoke: 15 файлов)

| Где | Что | Win7 |
|---|---|---|
| `Program.cs` | SetForegroundWindow, ShowWindow, MessageBoxW | ✅ |
| `Services/AvaloniaCustomerDisplayService.cs` | QueryDisplayConfig, GetDisplayConfigBufferSizes | ✅ (есть с Win7) |
| `Services/AvaloniaKeyboardWedgeBarcodeService.cs` | GetMessageTime (сканер-клавиатура) | ✅ |
| `Services/RemoteSupport/ClientInputWatcher.cs` | SetWindowsHookEx, GetAsyncKeyState, GetLastInputInfo … | ✅ |
| `Services/RemoteSupport/RemoteSupportHost.cs` | SendInput, MapVirtualKey, GetCursorPos, CopyFromScreen (GDI), ClientWebSocket | ✅ (при работающем TLS 1.2) |
| `Services/WindowChrome.cs` | DwmSetWindowAttribute (тёмный заголовок) | ✅. Уже пропускается при сборке ниже 22000 |
| `Services/WindowsOperatingSystemKeyboardService.cs` | osk.exe, SetWindowPos, PostMessage | ✅ (osk.exe есть в Win7) |
| `ViewModels/FrmKeyboardViewModel.cs` | ToUnicode, GetKeyState | ✅ |
| `Infrastructure/.../RongtaScaleAutomationService.cs` | FindWindow, EnumWindows, PostMessage | ✅ |
| `Infrastructure/.../ScaleNetworkScanner.cs` | GetIpNetTable (iphlpapi) | ✅ |
| `Infrastructure/.../ScaleNetworkScanner.Subnets.cs` | PowerShell + `Get-NetIPInterface` + `netsh … store=active` | ❌ в Win7 PowerShell 2.0, модуля NetTCPIP нет. Порог `MinBuildForDhcpCoexistence = 19041` уже отсекает «DHCP + второй адрес», но сам скрипт тоже нужно скрыть на Win7 |
| `Infrastructure/Services/RawPrinterHelper.cs` | winspool: OpenPrinter, WritePrinter, EnumJobs … | ✅ |
| `Infrastructure/Services/PrinterPortService.cs` | CreateFile, QueryDosDevice (COM, LPT, USB) | ✅ |
| `Infrastructure/Services/WinUsbPrinterPort.cs` | winusb.dll, setupapi | ✅ (WinUSB есть с XP/Vista) |
| `System.IO.Ports` (весы, COM) | SerialPort | ✅ |

WMI, UI Automation, Windows Hello и WinRT-API в коде **не используются** (0 мест). ESC/POS, денежный ящик, COM-весы, сканер-клавиатура и osk.exe на Win7 работают, ничего менять не нужно.

---

## 4. Можно ли отдельная сборка под .NET Framework 4.8 или .NET 6

- **.NET Framework 4.8.** Ставится на Win7 SP1 (4.8.1 — уже нет). Avalonia 11 (netstandard2.0) и почти все пакеты под ним работают. Но код написан под .NET 8: `Math.Clamp` — 210 мест, `WaitAsync` — 98, `OperatingSystem.Is*` — 74, `ThrowIfNull` — 25, диапазоны `[^1]` — 54, `required` — 92, `init` — 835, `record` — 341, `Span` — 57, `GeneratedRegex` — 6, `PeriodicTimer` — 6 и т. д. Понадобятся новые зависимости-«заплатки» (PolySharp, System.Memory, Microsoft.Bcl.AsyncInterfaces, System.Text.Json) и тысячи `#if`. TLS на .NET Framework настраивается отдельно (`ServicePointManager`, `SchUseStrongCrypto`), ReadyToRun нет, старт медленнее. Оценка: **25–40 пакетов** и **вечная двойная поддержка**. **Не рекомендуется.**
- **.NET 6 (multi-target `net6.0-windows`).** Отличий API немного: `GeneratedRegex` — 6 мест, `ThrowIfNullOrEmpty` — 1, `GetItems/Shuffle` — 5, `u8` — 4, `CollectionsMarshal` — 1, плюс заглушки атрибутов для `required`. Но нужно понизить System.Drawing.Common, System.IO.Ports и CodePages до 8.0.x (изменение зависимостей) и вести вторую линию сборки. Главное: .NET 6 **тоже без поддержки с 12.11.2024**. Он только «когда-то был проверен на Win7». Выигрыш против .NET 8 на Win7 — W^X там выключен по умолчанию. Это и так решается переменной окружения. Оценка: **8–10 пакетов**. Особого смысла нет.
- **Тот же .NET 8, тот же код, проверки во время работы.** Опора: разбор файлов (раздел 1) и отзывы сообщества. Отрезается 5 мест, установщик свой. Оценка: **6 пакетов**. Это вариант **A′**, рекомендуемый, если Win7 всё-таки нужна.

Что отрезается на Win7 в любом из вариантов A/A′: встроенный браузер WebView2 (окно NurCRM, редактор сайта, WhatsApp в программе владельца, анимированная реклама на экране покупателя → «открыть в обычном браузере» или картинка), проверка голоса владельца (onnxruntime), временный адрес для поиска весов, автообновление Velopack, голос Vosk в x86. Остаются и должны работать: продажа, возврат, смена, X/Z-отчёт, долги, склад, печать ESC/POS и графическая (GDI), денежный ящик, весы, сканер, экран покупателя (без видео), удалённая помощь (своя, wss), Нур Советник и ИИ (если проходит TLS к Google), Telegram, PDF/Excel.

---

## 5. Неофициальные способы (оценка риска)

| Способ | Что это | Риск | Вывод |
|---|---|---|---|
| `DOTNET_EnableWriteXorExecute=0` | штатный переключатель .NET (выключает W^X) | малый. W^X — защита от эксплойтов, без неё программа чуть уязвимее. Действует на все .NET-программы пользователя, если ставить глобально | **использовать** на Win7 (только для процесса кассы, см. W7-2) |
| VxKex (i486/VxKex) | DLL, которая через реестр IFEO внедряется в программы и добавляет API из Windows 8 и 10 | высокий: стороннее «ядро» в кассе с деньгами. Аккаунт автора удалялся, под тем же именем появлялся **поддельный репозиторий**. Нам не нужен: статических импортов из Win8+ у .NET 8 нет | **не использовать** |
| Сборники «UpdatePack7», «win7 api-ms-win-core» из интернета | неофициальные DLL и пакеты обновлений | очень высокий (вредоносный код, лицензия) | **запрещено** |
| ESU для Windows 7 | платные обновления безопасности Microsoft | кончились 10.01.2023 (POSReady 7 — в октябре 2024) | купить уже нельзя |
| Velopack 1.3.x (предварительная) | официальные win7-шимы в Setup.exe и Update.exe | средний: предварительная версия, «not yet verified on real Windows 7», смена зависимости | ждать стабильную 1.3, проверить на ВМ. До тех пор — свой установщик архивом |

---

## 6. Варианты

| | Что получится | Стоимость (пакеты Sonnet) | Надёжность | Обновления | Безопасность | Риск для денег |
|---|---|---|---|---|---|---|
| **A. Отдельная сборка net48** | касса без 5 функций, вторая копия кода | 25–40 + вечная двойная поддержка | средняя: Avalonia на Framework проверена меньше | Velopack 1.0 с net472 возможен, но Setup.exe и Update.exe всё равно не идут на Win7 → свой установщик | Win7 без обновлений | расхождение логики денег между двумя кассами — **высокий** |
| **A′. Канал Win7 на .NET 8 (рекомендуется, если Win7 нужна)** | тот же exe и код. На Win7 отключены 5 функций, свой установщик архивом, обновление архивом | **6** | средняя-хорошая: тот же код денег, что у всех. Microsoft не поддерживает | своё обновление «архивом» (W7-4); позже Velopack 1.3, когда выйдет стабильная | Win7 без обновлений + W^X выключен | **низкий**: код денег общий. Риск — «нет связи» без TLS (лечится скриптом и проверкой) |
| **B. .NET 8 + шим (VxKex)** | то же, что A′, + чужая DLL в процессе | 2 | непредсказуемая | — | **плохо** | средний-высокий |
| **C. Linux на том же ПК** | наша Linux-касса (`NurMarketKassa.Portable`, канал `Nurmarket-Linux`) | 0–1 (инструкция и проверка принтера) | хорошая, ОС поддерживается | есть свой канал | **хорошо** | низкий. Ограничения: нет графической печати чека (`PlatformNotSupportedException`), нет WebView2, драйверы принтеров и весов под Linux, кассира надо научить |
| **D. Обновить ПК до Windows 10** | полная касса без ограничений | 0 | **лучшая** | Velopack как у всех | Windows 10 22H2 тоже без поддержки с 14.10.2025 (ESU для дома — до 13.10.2026). Лучший выбор — **Windows 10 IoT Enterprise LTSC 2021** (поддержка до 2032). Всё равно намного лучше Win7: TLS 1.2 по умолчанию | низкий |
| **E. Мобилка или Android-касса вместо ПК** | наша мобилка или Android-касса на дешёвом Android-терминале | 0 | хорошая | Google Play / наш APK | хорошо | низкий. Нужны принтер и сканер под Android |

Почему D и C дешевле: на ПК, где «тянет» Windows 7 SP1 x64 с 2+ ГБ памяти, почти всегда встаёт Windows 10 x64 или лёгкий Linux. Обновление Windows — разовая работа мастера. Канал Win7 — постоянная обязанность: проверять каждый выпуск на ВМ, держать TLS, поддерживать отдельный установщик.

---

## 7. Рекомендация

1. **Сначала вопросы владельцу** (раздел 8). Без числа клиентов и их ПК решение не принимать.
2. **По умолчанию** клиентам на Win7 — **D** (Windows 10, лучше LTSC). Если лицензии нет — **C** (Linux-касса). Написать для мастеров памятку «как перевести кассу с Windows 7» (1 пакет документации, без кода).
3. **Если владелец решит, что Win7 нужна** (много клиентов, ПК не обновить) — **A′**, пакеты W7-1…W7-6 из раздела 9, только x64. x86 — отдельным решением после ответа владельца: Vosk под x86 нет, и у 32-битного процесса потолок 2 ГБ памяти. Это риск для каталога на 15 тыс. товаров (стресс-тест 23.10).
4. **Не делать** A (net48) и B (VxKex).
5. Отметить для основной кассы: **.NET 8 кончается 10.11.2026**. Переход на .NET 10 — отдельная задача. Канал Win7 после этого остаётся на последнем 8.0.x (заморозка). Новые функции в него попадают, пока общий код собирается под .NET 8. После перехода основной кассы на .NET 10 — либо проверять .NET 10 на Win7 (по отзывам работает), либо держать Win7 на последней версии кассы под .NET 8 без новых функций.

---

## 8. Вопросы владельцу (вариант по умолчанию — **жирным**)

1. Сколько клиентов сейчас на Windows 7? (1–5 / 6–30 / больше 30). Если мало — **D/C, кода не пишем**.
2. Какие у них ПК: Windows 7 **x64** или x86 (32 бита)? Сколько памяти (1 / **2** / 4+ ГБ)? Видно в «Свойства системы». По умолчанию — **только x64, 2+ ГБ**.
3. Что подключено: чековый принтер (USB, COM, сетевой), весы (COM, сетевые Rongta), сканер (клавиатурный, COM), денежный ящик, второй экран покупателя? По умолчанию — **принтер ESC/POS USB + клавиатурный сканер**.
4. Готовы ли клиенты обновить ПК до Windows 10, если мы дадим мастера или инструкцию? (**да — вариант D**).
5. Можно ли один раз зайти на их ПК с правами администратора? Нужно для обновлений KB и включения TLS 1.2. Без этого касса на Win7 не подключится. (**да, через мастера**).
6. Нужно ли на Win7 то, что мы отрежем: окно NurCRM внутри программы, видео-реклама на экране покупателя, вход голосом? (**нет — отрезаем**).
7. Обновления на Win7: (**а) кнопка «Скачать обновление» в кассе, архив + перезапуск**; б) мастер ставит вручную; в) ждём стабильный Velopack 1.3, обновление как у всех).
8. Для проверки: есть ли у вас старый ПК или ноутбук с наклейкой Windows 7, или клиент, у которого можно проверить (с его согласия)? Есть ли образ или ключ Windows 7? (**проверяем на реальном старом ПК клиента или на вашем**).
9. Можно ли включить Hyper-V на этом ПК? Нужны права администратора и перезагрузка, а вы работаете в кассе на этом же ПК. (**нет — сначала реальный ПК**).
10. Разрешаете ли менять зависимости ради Win7 (Velopack 1.3, когда выйдет стабильная)? (**нет, пока свой установщик архивом**).

---

## 9. Пакеты для Sonnet (если выбран A′)

Общие правила для всех пакетов: CLAUDE.md (без `dotnet test`, 5 языков `Tr.T`, комментарии `// 2026-10-xx: … (Win7)`, минимальный дифф, сборка — `build-locked.ps1` или команда из CLAUDE.md). Новые файлы **не называть `*.Legacy.cs`**: такие файлы вырезаются `DefaultItemExcludes` в csproj и в Portable. Брать `*.Win7.cs`. Всё делается **проверкой во время работы** (`Win7Compat.IsWin7`), без отдельного проекта: exe один для всех. Файлы пакетов не пересекаются. W7-1 делается первым, остальные — параллельно после него.

### W7-1. Признак «Windows 7» и запуск без W^X
- Файлы: новый `NurMarketKassa.Ui.Shared/Win7Compat.cs` (или `Core`). Правка `NurMarketKassa.Avalonia/Program.cs` (только `Main` и `BuildAvaloniaApp`).
- Что сделать:
  - `Win7Compat.IsWin7 = OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(6, 2)`.
  - В `Main` на Win7, если `DOTNET_EnableWriteXorExecute` ≠ `0`: перезапустить себя с этой переменной (через `ProcessStartInfo.Environment`, те же аргументы) и выйти. Сделать это **до** захвата mutex «касса уже запущена».
  - В `BuildAvaloniaApp` на Win7 подключить `IconFontFallback.Options`, как на Linux. Отрисовка по умолчанию — `Software`, если в настройках не выбрано иное.
  - В журнал строку `Win7 mode: W^X off, icons NurIcons, render Software`.
- Приёмка: сборка зелёная. На Win10/11 поведение не изменилось (журнал без строки Win7, значки MDL2 на месте). На ВМ Win7 касса стартует один раз (не зацикливается на перезапуске), значки не квадраты, окно входа открывается меньше чем за 20 с.

### W7-2. Отключение несовместимых функций на Win7
- Файлы: `Views.axaml/Main/CrmWebViewWindow.axaml.cs`, `Views.axaml/Main/SiteEditorWindow.cs`, `Views.axaml/Customer/Parts/CustomerInfoCard.axaml.cs`, место вызова WhatsApp в `OwnerShellWindow.axaml.cs` (только строка открытия), `Infrastructure/Services/Hardware/SpeakerVerificationService.cs`, `Infrastructure/Services/Hardware/ScaleNetworkScanner.Subnets.cs`.
- Что сделать:
  - На Win7 WebView2 не создавать: окна NurCRM, сайта и WhatsApp открывают адрес в браузере по умолчанию (`Process.Start` с `UseShellExecute`). Реклама на экране покупателя — первый кадр картинкой (как в предпросмотре).
  - Проверку голоса — «недоступно на Windows 7» без вызова onnxruntime.
  - Временный адрес для поиска весов — кнопку скрыть, обычный поиск `GetIpNetTable` оставить.
  - Все новые надписи — на 5 языках.
- Приёмка: на Win10 ничего не изменилось. На Win7 ни одного `WebView2 init failed` и `DllNotFoundException onnxruntime` в журнале при обходе всех окон (раздел 10, сценарий 3).

### W7-3. TLS 1.2: проверка и понятная ошибка
- Файлы: новый `Infrastructure/Services/Win7TlsCheck.cs`. Одна строка вызова в месте первого входа (служба входа, без изменения логики входа).
- Что сделать:
  - На Win7 при старте прочитать ключ реестра Schannel TLS 1.2 Client. Если `DisabledByDefault≠0` или ошибка `AuthenticationException` при входе — показать окно «На этом ПК выключен TLS 1.2. Мастеру: запустить `win7-prepare.cmd` от администратора» (5 языков).
  - В журнал — версию протокола соединения.
- Приёмка: на Win7 без ключа — понятное окно вместо «Нет связи». С ключом — вход и продажа проходят. На Win10 проверка не выполняется.

### W7-4. Обновление «архивом» для Win7
- Файлы: новый `Infrastructure/Services/ZipUpdateService.Win7.cs` (реализует `IAppUpdateService`). Регистрация в DI (одна ветка `if (Win7Compat.IsWin7)`). Новый `NurMarketKassa.Avalonia/win7/apply-update.cmd`.
- Что сделать:
  - На Win7 вместо `VelopackUpdateService`: проверка версии по тому же релизу GitHub, файл `NurMarketKassa-win7-x64-X.Y.Z.zip`.
  - Скачать в `%LOCALAPPDATA%\NurMarketKassa-Win7\staging`, проверить SHA-256 из `win7.sha256`, запустить `apply-update.cmd`. Скрипт ждёт выхода кассы, переименовывает `app` в `app.old`, `staging` — в `app`, запускает кассу. Если касса не стартовала за 60 с — откат.
  - Учитывать тестовый канал (код тестировщика), как в `VelopackUpdateService`.
- Приёмка: на ВМ Win7 обновление 1.17.X → 1.17.X+1 проходит, журнал пишет новую версию. Битый архив (неверная сумма) не ставится. Данные `%APPDATA%\NurMarketKassa\data` не трогаются.

### W7-5. Установщик Win7 (архив + скрипт подготовки)
- Файлы: новые `NurMarketKassa.Avalonia/win7/install.cmd`, `win7-prepare.cmd`, `README-WIN7.txt` (ru/ky/uz). Новый `tools/release/pack_win7.py`.
- Что сделать:
  - `win7-prepare.cmd` (от администратора, только cmd и reg, **без PowerShell 3+**): проверяет SP1 (`ver` 6.1.7601), наличие KB2999226 или VC++ 2015–2022, KB3063858/KB2533623, KB4474419, KB2670838 (`wmic qfe`) и выводит, чего не хватает, со ссылками Microsoft. Пишет ключи Schannel TLS 1.2 Client. Сам ничего не скачивает.
  - `install.cmd`: копирует `app` в `%LOCALAPPDATA%\NurMarketKassa-Win7\app`, создаёт ярлыки «NurMarket Kassa» и «… Владелец» (`--owner`).
  - `pack_win7.py`: `dotnet publish -r win-x64 --self-contained` в отдельную папку (не `dev-out`), zip, `win7.sha256`.
- Приёмка: на чистой ВМ Win7 SP1 с нужными KB касса ставится за 5 минут без Velopack. Скрипт честно сообщает, каких KB не хватает. Основная сборка и `vpk pack` не изменились.

### W7-6. Проверка совместимости в выпуске + проверка сервера
- Файлы: новый `tools/qa/win7_imports_check.py` (скрипт из приложения A). Строка в чек-листе выпуска AI_HANDOFF 4.3 (только для канала Win7).
- Что сделать:
  - Скрипт проверяет папку сборки. Если в новых native-файлах (кроме известных `onnxruntime.dll`, `WebView2Loader.dll`, `msquic.dll`) появился импорт из Win8+ — выпуск Win7 останавливается.
  - Отдельно — отчёт SSL Labs по `app.nurcrm.kg` и `github.com` (строка «IE 11 / Win 7») с разрешения владельца. Если не проходит — пункт в ТЗ бэкенду (наборы шифров).
- Приёмка: скрипт на 1.17.61 выдаёт ровно известный список. Добавление тестового файла с `GetSystemTimePreciseAsFileTime` его ловит.

Пакет «D/C: памятка мастеру» (вне кода): `docs/Как перевести кассу с Windows 7.md`. Windows 10 LTSC / Linux, перенос базы `%APPDATA%\NurMarketKassa\data`, принтер, весы.

---

## 10. Как проверять без своего ПК с Windows 7

- **Без Windows 7 вообще:** скрипт импортов (приложение A, пакет W7-6) — объективная проверка «не сломается ли загрузка». Плюс обход на Win10 с принудительным `Win7Compat.IsWin7 = true` (скрытый флаг `--simulate-win7`, только для проверки) — проверяет отключение функций, но не саму ОС.
- **Реальный старый ПК (лучше всего):** настоящие драйверы принтера, весов и видеокарты. С согласия клиента или ПК владельца.
- **Виртуальная машина:**
  - **Hyper-V.** На этом ПК гипервизор включён (`HypervisorPresent=True`), но служба Hyper-V (`vmms`) не установлена. Включение — права администратора и перезагрузка (вопрос 9). Нужна ВМ поколения 1. Службы интеграции в Win7 SP1 уже есть.
  - **VirtualBox** — новая программа, только с разрешения владельца.
  - **Память.** Сейчас свободно ~0,7 ГБ из 15,9 ГБ (замер 26.10, в задаче — «~2 ГБ свободно»). Win7 x64 в ВМ требует минимум 2 ГБ, x86 — 1 ГБ. Касса в ВМ — ещё 0,4–0,6 ГБ. Реально: Win7 **x86** с 1–1,5 ГБ (динамическая память) при закрытых тяжёлых программах, или ВМ на другом ПК. На диске C: свободно ~127 ГБ, ВМ нужно ~20 ГБ.
  - **Образ и лицензия.** Microsoft больше не раздаёт образ Windows 7 открыто. Законные пути: свой установочный диск или образ с ключом, подписка Visual Studio (если есть), старый ПК с наклейкой OEM (такая лицензия привязана к тому ПК, в ВМ её переносить нельзя). **Не качать сборки с торрентов** — вредоносный код и нарушение лицензии. Без активации Windows 7 работает 30 дней — этого хватит для разовой проверки, решение за владельцем.
  - **Подготовка ВМ:** SP1 → KB4474419 (SHA-2) → KB2999226 / VC++ 2015–2022 → KB3063858 → KB2670838 → `win7-prepare.cmd`.
- **Сценарии на Win7 (приёмка всего A′):**
  1. Установка `install.cmd`, первый запуск, вход в тестовый аккаунт NBS.
  2. Продажа наличными и картой, возврат, долг, X-отчёт, закрытие смены.
  3. Обход всех окон: склад, ревизия, клиенты, настройки, маркетплейс. В журнале нет `DllNotFound`, `EntryPointNotFound`, `WebView2 init failed`.
  4. Печать чека (текст и графика) на USB/COM-принтер, денежный ящик, весы, сканер.
  5. Удалённая помощь (wss).
  6. Обновление архивом.
  7. 2 часа работы: память ниже 1 ГБ, нет всплесков CPU (это и проверка W^X).
  8. Офлайн: выдернуть сеть, продать, вернуть сеть — чек ушёл на сервер один раз.

---

## 11. Безопасность и деньги (честно)

- Windows 7 не получает обновлений безопасности с января 2023 года. Касса хранит токен входа (DPAPI) и данные о деньгах. Любая уязвимость ОС — риск для клиента. Это нужно **письменно** сказать клиенту: «работает, но на свой риск, рекомендуем Windows 10 или Linux».
- Без TLS 1.2 касса не сможет отправлять продажи. Офлайн-очередь сохранит их локально, но смена и долги разойдутся с сервером, пока связь не появится. Поэтому W7-3 обязателен.
- Выключенный W^X немного снижает защиту процесса. Влияние только на кассу (переменная задаётся процессу, не всей системе).
- Код денег (корзина, оплата, долги, возвраты) в A′ **общий** с основной кассой. Это главное преимущество перед A (net48), где логика разъехалась бы.

---

## 12. Источники

- Microsoft: Install .NET on Windows — поддержка Win7 и 8.1 кончилась с .NET 6 (12.11.2024), обязательные KB3063858 и VC++: https://learn.microsoft.com/en-us/dotnet/core/install/windows
- .NET 6/7 supported OS (Win7 SP1 в «Out of support»): https://github.com/dotnet/core/blob/main/release-notes/6.0/supported-os.md , https://github.com/dotnet/core/blob/main/release-notes/7.0/supported-os.md
- Объявление «.NET support for Windows 7 and 8.1 will end»: https://github.com/dotnet/core/issues/7556
- Одиночный exe .NET 6 на Win7 (отложенная загрузка winrt): https://github.com/dotnet/runtime/pull/63533
- Отзыв сообщества: .NET 7–10 на Win7, W^X и `DOTNET_EnableWriteXorExecute=0`: https://github.com/hfiref0x/WinDepends/issues/11
- Schannel: TLS 1.2 Client на Windows 7 — Disabled по умолчанию: https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-
- TLS в .NET Framework (для варианта A): https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
- Avalonia 11 Supported Platforms (Win7 — «limited support»): https://v11.docs.avaloniaui.net/docs/overview/supported-platforms ; Avalonia 12: https://docs.avaloniaui.net/docs/supported-platforms ; исправление Win7 в 11.0.8: https://github.com/AvaloniaUI/Avalonia/pull/14436
- Velopack: PR #1112 «Win7 import shims»: https://github.com/velopack/velopack/pull/1112 ; выпуски (1.3.39 — предварительная, 1.2.161 — стабильная): https://github.com/velopack/velopack/releases ; RID и минимальная ОС: https://docs.velopack.io/packaging/runtime
- WebView2 и Edge кончили поддержку Win7 на 109; SDK ≥1.0.1519 без Win7: https://blogs.windows.com/msedgedev/2022/12/09/microsoft-edge-and-webview2-ending-support-for-windows-7-and-windows-8-8-1
- KB2999226 (Universal CRT для Win7): https://support.microsoft.com/kb/2999226
- VxKex (оценка риска, предупреждение о поддельном репозитории): https://github.com/i486/VxKex
- `GetSystemTimePreciseAsFileTime` — минимум Windows 8: https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/sysinfoapi/nf-sysinfoapi-getsystemtimepreciseasfiletime.md

---

## Приложение A. Скрипт проверки импортов (для W7-6)

Запуск: `python -I win7_imports_check.py <папка сборки> [ещё файлы]`. Нужен `pip install pefile`. Это эвристика: список функций курированный и не исчерпывающий.

```python
import sys, os, glob, pefile
WIN8PLUS = set("""GetSystemTimePreciseAsFileTime WaitOnAddress WakeByAddressSingle WakeByAddressAll CreateFile2
GetCurrentThreadStackLimits SetThreadInformation GetThreadInformation PrefetchVirtualMemory
GetProcessMitigationPolicy SetProcessMitigationPolicy VirtualAlloc2 MapViewOfFile3 GetThreadDescription
SetThreadDescription QueryUnbiasedInterruptTimePrecise GetOverlappedResultEx DiscardVirtualMemory
OfferVirtualMemory CompareObjectHandles GetPackageFamilyName GetCurrentPackageId GetCurrentPackageFullName
ProcessPrng GetDpiForWindow GetDpiForSystem SetProcessDpiAwarenessContext GetSystemMetricsForDpi
AdjustWindowRectExForDpi EnableNonClientDpiScaling GetDpiForMonitor SetProcessDpiAwareness CreateDXGIFactory2
DCompositionCreateDevice RoInitialize RoGetActivationFactory WindowsCreateString CreateFileMappingFromApp""".split())
MISSING_DLLS = {"combase.dll", "shcore.dll", "dcomp.dll", "d3d12.dll", "api-ms-win-core-winrt-l1-1-0.dll",
  "api-ms-win-core-winrt-string-l1-1-0.dll", "api-ms-win-core-path-l1-1-0.dll", "api-ms-win-core-synch-l1-2-0.dll",
  "api-ms-win-core-sysinfo-l1-2-0.dll", "api-ms-win-core-file-l1-2-0.dll", "api-ms-win-core-file-l2-1-0.dll",
  "api-ms-win-core-processthreads-l1-1-2.dll", "api-ms-win-core-memory-l1-1-3.dll"}
KNOWN = {"onnxruntime.dll", "webview2loader.dll", "msquic.dll"}   # известны и отключены на Win7
bad_total = 0
for root in sys.argv[1:]:
    files = [root] if os.path.isfile(root) else glob.glob(os.path.join(root, "*.dll")) + glob.glob(os.path.join(root, "*.exe"))
    for f in sorted(files):
        try:
            pe = pefile.PE(f, fast_load=True)
            if pe.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress: continue      # управляемая сборка
            pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
        except Exception:
            continue
        bad = []
        for e in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []) or []:     # только статические импорты
            d = e.dll.decode(errors="ignore").lower()
            if d in MISSING_DLLS: bad.append("[DLL] " + d)
            bad += [i.name.decode() + "@" + d for i in e.imports if i.name and i.name.decode() in WIN8PLUS]
        if bad:
            name = os.path.basename(f).lower()
            mark = "известно" if name in KNOWN else "НОВОЕ"
            bad_total += name not in KNOWN
            print(f"{mark:9} {os.path.basename(f)}: {sorted(set(bad))}")
sys.exit(1 if bad_total else 0)
```

Результат на установленной 1.17.61 (26.10.2026): `onnxruntime.dll` (CreateFile2, GetSystemTimePreciseAsFileTime, GetProcessMitigationPolicy, CreateDXGIFactory2, api-ms-win-core-path) и `Update.exe` (combase.dll). В `vpk 1.0.1/vendor`: `setup.exe` (combase.dll, GetDpiForSystem) и `update.exe` (combase.dll). У остальных 33 native-файлов кассы, включая `coreclr.dll`, `hostfxr.dll`, Skia, HarfBuzz, ANGLE, SQLite, QuestPDF и Vosk, статических импортов из Win8+ нет.
