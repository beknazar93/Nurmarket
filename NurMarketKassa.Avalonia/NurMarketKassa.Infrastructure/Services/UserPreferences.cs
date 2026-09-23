using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public enum PrintMode
{
    Text,   // Текстовый ESC/POS
    Graphic // Графический чек
}

/// <summary>2026-09-08: личные коды доступа сотрудника для 4 защищённых действий кассы
/// (по просьбе владельца — "проверяй акаунт если это сотрудник, то по каждому доступу должен
/// быть код"). Пустой/null-код у конкретного сотрудника означает "этот код ещё не выдан" —
/// такой сотрудник не сможет выполнить именно это действие, пока владелец не впишет код в
/// разделе Настройки → Сотрудники. См. EmployeeAccessGate.</summary>
public sealed class EmployeeAccessCode
{
    public string Name { get; set; } = "";
    public string? CartDeleteCode { get; set; }
    public string? WarehouseDeleteCode { get; set; }
    public string? ProductEditCode { get; set; }
    public string? ProductAddCode { get; set; }

    /// <summary>2026-09-08: id сотрудника на app.nurcrm.kg, если карточка появилась из
    /// "Загрузить с сайта" или "Добавить сотрудника" (создан через API) — нужен, чтобы кнопка
    /// "Удалить сотрудника" могла также удалить его на сервере. Null у карточек, введённых
    /// вручную (только локальный код доступа, без реального аккаунта на сайте).</summary>
    public string? ServerId { get; set; }

    /// <summary>2026-09-08: email сотрудника (логин на app.nurcrm.kg) — заполняется при создании
    /// через кассу или при "Загрузить с сайта".</summary>
    public string? Email { get; set; }

    /// <summary>2026-09-08: пароль, который сервер сам сгенерировал при создании сотрудника
    /// через кассу (владелец попросил его сохранять — сервер показывает его только один раз,
    /// в самом ответе на создание, и больше никогда не отдаёт). Null для сотрудников, загруженных
    /// через "Загрузить с сайта" — сервер не отдаёт существующие пароли, только свежесозданные.</summary>
    public string? LoginPassword { get; set; }
}

/// <summary>Локальные настройки POS (%AppData%\NurMarketKassa\user-settings.json).</summary>
public sealed class UserPreferences
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string ScaleComPort { get; set; } = "COM2";
    public int ScaleBaudRate { get; set; } = 9600;
    public bool ScaleEnabled { get; set; }
    public string? ScaleRequestHex { get; set; }
    public int ScalePollMs { get; set; }

    /// <summary>Бренд весов для выгрузки PLU (экран «Весы» в Настройках) — "shtrikh" (сервер
    /// сам говорит с весами по LAN) или "rongta" (через встроенный запуск RLS1000, см.
    /// RongtaScaleAutomationService).</summary>
    public string ScaleBrand { get; set; } = "shtrikh";

    /// <summary>Источник данных для Rongta — "site" (скачать .txp с сервера, по умолчанию) или
    /// "server" (свой TCP-сервер прямо из локального каталога, RongtaTcpServerService — по
    /// просьбе владельца 2026-09-19, работает без обращения к сайту в момент отправки).</summary>
    public string RongtaDataSource { get; set; } = "site";

    /// <summary>IP-адрес сетевых PLU-весов (Штрих-М или Rongta) — чисто для проверки связи
    /// («Проверить подключение» на экране Весы), кассой напрямую не используется: у Штрих-М
    /// связь целиком серверная, у Rongta — через RLS1000 (см. её doc-comment).</summary>
    public string? ScaleNetworkIp { get; set; }

    /// <summary>Порт своего TCP-сервера для Rongta (см. RongtaDataSource="server") — тот же
    /// порт нужно один раз вручную указать в самой RLS1000 (File → Options → TCP/IP).</summary>
    public int RongtaServerPort { get; set; } = 5001;

    // Дисплей цены покупателя (отдельная COM-коробочка, не второй монитор — см.
    // PoleDisplayService, 2026-09-04)
    public string PoleDisplayComPort { get; set; } = "COM3";
    public int PoleDisplayBaudRate { get; set; } = 9600;
    public bool PoleDisplayEnabled { get; set; }

    // Принтер – общие настройки
    public string ReceiptDevicePath { get; set; } = "LPT1";
    public bool ReceiptEnabled { get; set; }

    /// <summary>Денежный ящик подключается не к компьютеру, а к чековому принтеру (разъём "DK"),
    /// поэтому отдельного порта у него нет — импульс уходит в <see cref="ReceiptDevicePath"/>.
    /// Выключено по умолчанию: у кассы без ящика лишняя команда принтеру не нужна.</summary>
    public bool CashDrawerEnabled { get; set; }

    /// <summary>Контакт разъёма для импульса: 0 — контакт 2 (обычный случай), 1 — контакт 5.
    /// Зависит от распайки кабеля ящика; при неверном значении ящик просто не открывается,
    /// ошибки не будет — поэтому значение вынесено в настройки, а рядом есть кнопка проверки.</summary>
    public int CashDrawerPin { get; set; }

    /// <summary>Путь устройства/имя принтера для печати ценников (spooler-имя, \\.\USBxxx,
    /// LPT/COM или WinUSB-адрес VID:PID) — см. PrinterDiscoveryService.Discover().</summary>
    public string? LabelPrinterDevicePath { get; set; }
    /// <summary>Ширина ленты: 58 (узкая) или 80 (широкая) мм.</summary>
    public int ReceiptPaperWidthMm { get; set; } = ReceiptPaperProfile.Paper58mm;

    // ===== ТЕКСТОВЫЙ РЕЖИМ =====
    public string ReceiptEncoding { get; set; } = "wpc1251";
    public int? ReceiptEscPosTable { get; set; }
    public int? ReceiptEscR { get; set; }
    public int ReceiptRetryCount { get; set; } = 3;

    // ===== ГРАФИЧЕСКИЙ РЕЖИМ =====
    public bool GraphicReceiptEnabled { get; set; } = false;
    public string QrCodePath { get; set; } = "";
    public int GraphicPaperWidthPixels { get; set; } = 384;
    public string GraphicFontFamily { get; set; } = "Consolas";
    public float GraphicFontSize { get; set; } = 16f; // 16 — более крупный, как на фото
    public PrintMode SelectedPrintMode { get; set; } = PrintMode.Text;

    // Элементы графического чека
    public bool ShowStoreName { get; set; } = true;
    public bool ShowAddress { get; set; } = true;
    public bool ShowInn { get; set; } = true;
    public bool ShowReceiptNumber { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowItems { get; set; } = true;
    public bool ShowTotal { get; set; } = true;
    public bool ShowQrCode { get; set; } = false;

    // Остальные настройки
    public bool Fullscreen { get; set; } = true;

    /// <summary>Этап 8 бэклога "Доработки": "настоящий" полноэкранный режим (Avalonia
    /// WindowState.FullScreen) вместо текущего "полный экран в окне" (SystemDecorations=None +
    /// Maximized) — на части моноблоков/неоптимизированных Windows последний всё равно даёт
    /// проглядывать панели задач/меню "Пуск". Действует только когда Fullscreen тоже включён
    /// (см. FullscreenHelper.Apply) — самостоятельного смысла без него не имеет.</summary>
    public bool TrueFullscreen { get; set; } = true;
    /// <summary>
    /// Режим слабого устройства: программный рендер вместо GPU-ускорения — стабильнее
    /// на старых моноблоках/ноутбуках со слабой/проблемной видеокартой, ценой части
    /// плавности анимаций. Применяется при следующем запуске приложения.
    /// </summary>
    public bool LowPerformanceMode { get; set; }

    /// <summary>Голосовое управление кассой (Vosk, офлайн, 2026-09-04) — кассир говорит
    /// ключевое слово "касса" и название товара с количеством, касса сама ищет и добавляет
    /// его в чек. Выключено по умолчанию — постоянное прослушивание нагружает CPU, включать
    /// только на достаточно мощном железе.</summary>
    public bool VoiceControlEnabled { get; set; }

    public int AppliedPublishDefaultsVersion { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public bool DarkTheme { get; set; } = true;
    public string AccentTheme { get; set; } = "gold";

    /// <summary>Раскладка главного экрана кассы (2026-09-06): "standard" — текущая (каталог +
    /// корзина рядом, resizable), "onec" — альтернативная, в стиле 1С "Рабочее место кассира"
    /// (крупные плитки, чек фиксированной колонкой справа, numpad для количества). Переключается
    /// в Настройках → Экран, применяется мгновенно без перезапуска (см. App.ApplyMainLayoutMode).</summary>
    public string MainLayoutMode { get; set; } = "standard";

    /// <summary>Прозрачность темы "Жидкое стекло" (0–100, где 100 — почти полностью прозрачно,
    /// 0 — почти непрозрачно) — настраивается ползунком в Маркетплейс → Темы, влияет только
    /// на тему glass (см. AccentThemeService.GlassCardBrush).</summary>
    public double GlassOpacityPercent { get; set; } = 80;

    /// <summary>Вкл/выкл эффекта "Жидкое стекло" (п.1.3 бэклога) — включено (по умолчанию):
    /// как сейчас, глянцевая полупрозрачная текстура карточек + мягкий блюр на кассе.
    /// Выключено: матовые, слабо прозрачные карточки, без блюра — тема "glass" сохраняет
    /// только форму/скруглённость, не стеклянный вид.</summary>
    public bool LiquidGlassEnabled { get; set; } = true;

    /// <summary>Масштаб интерфейса кассы (Настройки → Экран → «Масштаб»), 50–200%, 100 —
    /// обычный размер. Применяется через LayoutTransformControl в MainWindow (см.
    /// MainWindow.RefreshUiScale) — в отличие от простого RenderTransform, он также
    /// корректно пересчитывает координаты кликов/попаданий курсором под новым масштабом.</summary>
    public double UiScalePercent { get; set; } = 100;

    /// <summary>Ручной оверрайд скруглённости кнопок (px) поверх значения активной темы —
    /// null означает "как задано в теме" (см. AccentThemeService.Apply).</summary>
    public double? CustomButtonRadius { get; set; }

    /// <summary>Ручной оверрайд скруглённости карточек/окон (px) поверх значения активной
    /// темы — null означает "как задано в теме".</summary>
    public double? CustomCardRadius { get; set; }

    /// <summary>Свой акцентный цвет по HEX (например "#FF6B00") — используется только когда
    /// AccentTheme == "custom" (см. AccentThemeService.BuildCustomTheme).</summary>
    public string? CustomAccentHex { get; set; }

    /// <summary>Оверрайд шрифта приложения (семейство) поверх системного по умолчанию — null
    /// означает "как всегда было" (Segoe UI). Настраивается в модалке "⚙ Настройка темы".</summary>
    public string? CustomFontFamily { get; set; }

    /// <summary>Оверрайд базового размера шрифта (px) — null означает "как всегда было".</summary>
    public double? CustomFontSize { get; set; }

    /// <summary>Оверрайд цвета текста по HEX поверх BrushText (светлого/тёмного режима) — null
    /// означает "как всегда было" (нативный цвет темы). Настраивается в модалке "⚙ Настройка
    /// темы" вместе со шрифтом.</summary>
    public string? CustomTextColor { get; set; }

    /// <summary>ID платных тем, разблокированных вводом серийного номера (см.
    /// MarketplaceView.TryValidateSerial) — "gold"/"blue" сюда не попадают, они и так
    /// бесплатны. Пока нет реальной оплаты/сервера лицензий — это офлайн-список,
    /// подтверждённый только локальной проверкой серийника.</summary>
    public List<string> UnlockedThemeIds { get; set; } = new();

    /// <summary>2026-09-07: мастер-ключ (LicenseKeys.MasterTestSerial) теперь даёт временный
    /// доступ на 15 минут, а не навсегда — постоянные ключи для реальной покупки отдельные
    /// (см. LicenseKeys.IsPermanentSerial и docs/paid-serial-keys.md). Эти два поля — учёт того,
    /// что именно было открыто мастер-ключом и когда истекает: по истечении LicenseKeys.ExpireIfDue()
    /// сбрасывает обратно ровно эти флаги/темы, не трогая то, что куплено постоянным ключом.</summary>
    public DateTime? MasterAccessExpiresAtUtc { get; set; }
    public List<string> MasterUnlockedFeatureFlags { get; set; } = new();
    public List<string> MasterUnlockedThemeIds { get; set; } = new();

    /// <summary>Голосовое управление — платная доп. услуга (см. MarketplaceView, "Доп. функции"):
    /// пакет распознавания речи (~113 МБ) больше не входит в базовую установку, его нужно
    /// разблокировать серийным номером и скачать отдельно.</summary>
    public bool VoiceControlUnlocked { get; set; }

    /// <summary>Платный «Языковой пакет» (2026-09-07): английский, турецкий и узбекский интерфейс.
    /// По умолчанию доступны только русский и кыргызский; активируется серийным номером в
    /// Маркетплейс → Доп. функции (см. LanguagePackGate).</summary>
    public bool LanguagePackUnlocked { get; set; }

    /// <summary>Аналитика склада и редактор ценников — тоже платные доп. услуги (2026-09-04),
    /// каждая со своим независимым флагом разблокировки (см. LicenseKeys).</summary>
    public bool WarehouseAnalyticsUnlocked { get; set; }
    public bool PriceTagEditorUnlocked { get; set; }

    /// <summary>Редактор этикеток (2026-09-07) — раньше был бесплатным (см. историю в
    /// MarketplaceView.AddLabelEditorCard), по явной просьбе пользователя стал платным.</summary>
    public bool LabelEditorUnlocked { get; set; }

    /// <summary>Массовая печать ценников (2026-09-04) — независимая от PriceTagEditorUnlocked
    /// доп. услуга (пользователь явно попросил разделить: активация одной не должна включать
    /// другую), хотя UI-код обеих живёт в одном месте (WarehouseWindow/BulkPriceTagPrintDialog).</summary>
    public bool BulkPriceTagUnlocked { get; set; }

    /// <summary>Голосовой замок (2026-09-05) — если голос, произнёсший команду, не совпадает с
    /// зарегистрированным (см. SpeakerVerificationService), команда не выполняется, показывается
    /// предупреждение. По умолчанию выключено (даже после регистрации голоса) — включать явно, а
    /// не молча запирать существующим пользователям голосовое управление.</summary>
    public bool VoiceLockEnabled { get; set; }

    /// <summary>Доп. услуга «Выгрузка аналитики» (2026-09-22): отчёт по продажам и складу
    /// в Excel и Word с графиками. На «Стандарт» и выше входит в тариф, на «Старт» — платная.</summary>
    public bool AnalyticsExportUnlocked { get; set; }

    /// <summary>Доп. услуга «Телеграм-бот владельца» (2026-09-22): сводка по смене, ответы на
    /// команды (выручка, топ товаров, что заказать, должники) и напоминания о долге. На тарифе
    /// «Стандарт» и выше входит в тариф, на «Старт» — покупается в Маркетплейс → Доп. функции.</summary>
    public bool TelegramBotUnlocked { get; set; }

    /// <summary>Доп. услуга «Расширенные итоги смены» (2026-09-22): возвраты, списания, расход,
    /// оплата долгов и скидки в окне смены, X/Z-отчёте и сводке в Telegram. На «Стандарт» и выше
    /// входит в тариф, на «Старт» — платная.</summary>
    public bool ShiftAnalyticsUnlocked { get; set; }

    /// <summary>Доп. услуга "Учёт сотрудников" (2026-09-05) — табель по кассирам (часы, смены,
    /// выручка), см. StaffTimesheetService/StaffTimesheetWindow.</summary>
    public bool StaffTimesheetUnlocked { get; set; }

    /// <summary>Доп. услуга "Весы" (2026-09-21, по просьбе владельца — "весы надо добавить в
    /// доп услуги в тарифе старт") — на тарифе «Старт» отправка PLU на весы (Штрих-М/Rongta,
    /// экран Настройки → Весы) требует серийного номера, как остальные карточки в Маркетплейс →
    /// Доп. функции (см. TariffGate.CanUseScales). На тарифе «Стандарт» и выше весы бесплатны
    /// всегда — сам этот флаг там не проверяется. Для уже настроивших весы ДО этого обновления
    /// флаг проставляется в true при первой загрузке файла без него (см.
    /// LoadFromDiskAndMergeDefaults) — апдейт не должен молча отключить уже работающие весы
    /// прямо на смене.</summary>
    public bool ScalesUnlocked { get; set; }

    /// <summary>Последние настройки массовой печати ценников (2026-09-05, по просьбе
    /// пользователя: "зона где настраивается высота и ширина этикеток и прочее должно
    /// запоминаться") — BulkPriceTagPrintDialog заполняет свои поля этими значениями при
    /// открытии вместо жёстко заданных по умолчанию, и сохраняет их сюда при печати. Null-поля
    /// означают "ещё ни разу не печатали" — тогда используется прежнее поведение по умолчанию.</summary>
    public int? BulkPriceTagKind { get; set; }
    public double? BulkPriceTagWidthMm { get; set; }
    public double? BulkPriceTagHeightMm { get; set; }
    public bool BulkPriceTagTargetIsA4 { get; set; }
    public int? BulkPriceTagCopies { get; set; }

    /// <summary>Показывать ли на экране покупателя QR с УЖЕ ВПИСАННОЙ суммой чека вместо
    /// статического (см. DynamicPaymentQrService). По умолчанию ВЫКЛЮЧЕНО: формула контрольной
    /// суммы ELQR сверена с настоящими QR MBank, но примут ли такой QR приложения ДРУГИХ банков
    /// — не проверялось. Владелец включает сам, отсканировав тестовый QR парой приложений.</summary>
    public bool DynamicPaymentQrEnabled { get; set; }

    /// <summary>Телеграм-бот владельца: вечерняя сводка по смене приходит ему в телефон.
    /// Токен в памяти лежит открытым, а НА ДИСК пишется через DPAPI (см. SaveToDisk) — файл
    /// настроек читает любой, у кого доступ к этому компьютеру, а токен позволяет писать
    /// сообщения от имени бота магазина.</summary>
    public string? TelegramBotToken { get; set; }

    /// <summary>Кому слать. Определяется один раз: владелец пишет боту «/start», касса читает
    /// chat_id через getUpdates.</summary>
    public string? TelegramChatId { get; set; }

    /// <summary>Имя получателя — только чтобы владелец видел в настройках, что подключился
    /// именно его чат, а не чужой.</summary>
    public string? TelegramChatTitle { get; set; }

    /// <summary>Отправлять ли сводку при закрытии смены. Выключено по умолчанию: выручка
    /// магазина уходит во внешний сервис, это должно быть осознанным решением владельца.</summary>
    public bool TelegramShiftSummaryEnabled { get; set; }

    /// <summary>Отвечает ли бот на команды владельца (/segodnya, /dolgi, /zakaz и т.п.).
    /// Включено по умолчанию: бот без ответов — это только вечерняя сводка, ради которой
    /// отдельную настройку заводить незачем. Опрос идёт, лишь пока касса включена.</summary>
    public bool TelegramCommandsEnabled { get; set; } = true;

    /// <summary>Имя бота без «@» — нужно, чтобы построить персональную ссылку для покупателя
    /// (t.me/ИмяБота?start=idКлиента). Заполняется автоматически при определении получателя.</summary>
    public string? TelegramBotUsername { get; set; }

    /// <summary>Телефон владельца — подставляется в рассылки клиентам как обратный контакт
    /// («по вопросам: +996 ...»). Сам по себе он НЕ даёт кассе отправлять SMS, см.
    /// комментарий в TelegramBotService и разговор с владельцем 2026-09-22.</summary>
    public string? OwnerPhone { get; set; }

    /// <summary>Как касса общается с весами Штрих-М: false — через сервер NurCRM (как было
    /// всегда), true — напрямую по витой паре из самой кассы (ShtrikhPrintLanScaleService).
    /// По умолчанию false: серверный путь рабочий и проверенный, прямой — написан по
    /// спецификации, но на живых весах не испытан.</summary>
    public bool ShtrikhDirectLan { get; set; }

    /// <summary>UDP-порт весов. В протоколе не указан — берётся из системного меню весов.
    /// 1111 подтверждён на живых весах (2026-09-22).</summary>
    public int ScaleLanPort { get; set; } = 1111;

    /// <summary>Пароль администратора весов — 4 цифры. 0030 подтверждён на живых весах
    /// (2026-09-22); «0000» весы не принимают.</summary>
    public string ScaleLanPassword { get; set; } = "0030";

    public Dictionary<string, string> BankQrPaths { get; set; } = new();
    public Dictionary<string, string> BankLogoPaths { get; set; } = new();

    /// <summary>Banks the cashier added themselves via Настройки → Операции → "Добавить банк",
    /// beyond the app's built-in default list (Элкарт/MBank/ФинкаБанк) — see OperationsSettingsView.</summary>
    public List<string> CustomBankNames { get; set; } = new();

    /// <summary>Какие банки показывать кассиру при безналичной оплате. Отмечаются в
    /// «Настройки → Операции». Пустой список означает «владелец ещё не выбирал» — тогда
    /// берётся KyrgyzBanks.DefaultVisible, то есть те же три банка, что были до обновления.</summary>
    public List<string> VisibleBankNames { get; set; } = new();

    public List<EmployeeAccessCode> EmployeeAccessCodes { get; set; } = new();

    /// <summary>Бонусная программа (AI-фичи 2026-09-04) — баланс хранится ЛОКАЛЬНО на этой кассе
    /// (см. ClientLoyaltyStore), т.к. в API клиентов NurCRM пока нет поля для баллов. По умолчанию
    /// выключено — новая функция, меняющая сумму к оплате, не должна включаться сама собой.</summary>
    public bool LoyaltyEnabled { get; set; }

    /// <summary>Процент от финальной суммы чека, начисляемый клиенту бонусами при включённой
    /// программе лояльности.</summary>
    public double LoyaltyEarnPercent { get; set; } = 5;

    public Dictionary<string, string> PosHotkeys { get; set; } = new();
    public bool Autostart { get; set; }
    public bool AutoShowTouchKeyboard { get; set; } = true;
    public CatalogViewMode CatalogViewMode { get; set; } = CatalogViewMode.Cards;
    /// <summary>Показывать ли загруженные фото товаров в плитках каталога кассира.</summary>
    public bool ShowCatalogPhotos { get; set; } = true;
    /// <summary>Язык интерфейса (пока только экран кассира).</summary>
    public AppLanguage Language { get; set; } = AppLanguage.Russian;
    /// <summary>Касса, выбранная вручную в настройках — перекрывает автовыбор первой
    /// активной кассы, если автовыбор берёт не ту (напр. с другого филиала).</summary>
    public string? PreferredCashboxId { get; set; }
    public string? PreferredCashboxName { get; set; }
    public bool SingleClickToCart { get; set; }
    public bool ResetManualAddQtyAfterAdd { get; set; } = true;
    /// <summary>Версия, для которой кассир уже видел окно "Что нового" после обновления.</summary>
    public string LastSeenAppVersion { get; set; } = "";
    /// <summary>2026-09-09: время последнего успешного онлайн-обращения к API (UTC, ISO 8601) —
    /// обновляется в NurMarketApiClient.SendOnceAsync при КАЖДОМ успешном ответе сервера, не
    /// только при логине. Основа для 60-часового потолка офлайн-работы обычного (не автономного)
    /// режима — см. OfflineGraceGuardService/OnlineOfflineAuthenticationService.</summary>
    public string? LastOnlineContactAtUtc { get; set; }
    /// <summary>2026-09-09: автономный (офлайн, без NurCRM) режим активирован на этом ПК —
    /// ключ активации был один раз успешно проверен онлайн (см. GithubLicenseRegistryClient).
    /// Дальше касса в этом режиме не обращается в интернет вообще.</summary>
    public bool AutonomousModeActivated { get; set; }
    /// <summary>Ключ, которым была выполнена активация — хранится только для показа владельцу
    /// (например, в диагностике), повторно на сервер не отправляется.</summary>
    public string? AutonomousModeActivationKey { get; set; }
    /// <summary>2026-09-10: последний успешный вход на этом ПК был локальным (LoginLocal) — при
    /// следующем запуске кассы автономная сессия продолжается автоматически, без повторного
    /// прохождения экрана логина (см. IAutonomousAuthService.TryAutoResume). Снимается при выходе
    /// из кассы и при успешном входе через обычный NurCRM-логин.</summary>
    public bool AutonomousAutoResume { get; set; }
    /// <summary>2026-09-13, по просьбе владельца ("с онлайна на офлайн переходил с базой") —
    /// одноразовый флаг: следующий LoginLocal НЕ должен чистить локальный каталог, потому что
    /// он был явно подготовлен (полная синхронизация с сервера) специально для переноса из
    /// текущего онлайн-аккаунта. Взводится MigrateToOfflineDialog перед выходом из NurCRM-сессии
    /// и сбрасывается самим LoginLocal сразу после использования — не переживает обычный вход.</summary>
    public bool PreserveCatalogOnNextAutonomousLogin { get; set; }
    /// <summary>Раскладка весового штрих-кода компании: "plu" или "code" (см. WeightBarcodeParser).</summary>
    public string ScaleBarcodeLayout { get; set; } = "plu";
    /// <summary>Режим значения весового штрих-кода: "auto"/"weight"/"amount".</summary>
    public string ScaleBarcodeMode { get; set; } = "auto";
    /// <summary>2026-09-14: единица суммы в весовом штрих-коде для mode="amount" (или "auto" с
    /// суммовым префиксом) — "tiyin" (значение ÷100) или "som" (значение как есть). Раньше не
    /// запрашивалась и не хранилась вовсе — касса всегда считала как "tiyin", хотя NurCRM
    /// поддерживает и "som" (см. WeightBarcodeParser).</summary>
    public string ScaleBarcodeAmountUnit { get; set; } = "tiyin";
    public bool ToolsPanelExpanded { get; set; }
    /// <summary>Абсолютный путь к пользовательским обоям.</summary>
    public string BackgroundImagePath { get; set; } = "";
    /// <summary>"Плотность стекла" — альфа-канал белой/тёмной подложки поверх обоев (0.05–0.8),
    /// НЕ прозрачность самих обоев (см. UI-заголовок в SettingsView.axaml).</summary>
    public double BackgroundOpacity { get; set; } = 0.15;
    /// <summary>Размытие фоновых обоев в процентах (0–100) — 0 отключает blur полностью.</summary>
    public double BackgroundBlurPercent { get; set; } = 40;
    /// <summary>Показывать ли обои (с размытием/плотностью стекла выше) на основном экране
    /// кассира (MainWindow), а не только в самих Настройках — см.
    /// MainWindow.RefreshBackgroundWallpaper.</summary>
    public bool ApplyBackgroundToCashierScreen { get; set; }
    public string LastLoginEmail { get; set; } = "";
    public string LastLoginPassword { get; set; } = "";
    /// <summary>Ключ владельца локального каталога (email|userId) для изоляции при смене аккаунта.</summary>
    public string LastCatalogUserKey { get; set; } = "";
    /// <summary>Чьи локальные данные сейчас лежат в рабочих папках (см. AccountDataIsolation):
    /// «c:{id компании}» либо «u:{id пользователя}» для автономного входа.</summary>
    public string LastDataAccountKey { get; set; } = "";
    public string? PostgreSqlConnectionStringEncrypted { get; set; }
    public string? LastFilterCategory { get; set; }
    public string? LastFilterBrand { get; set; }
    public DateTime? LastFilterDateFrom { get; set; }
    public DateTime? LastFilterDateTo { get; set; }
    public string? LastFilterClient { get; set; }
    public string? LastFilterStatus { get; set; }
    public string? LastFilterHotkeyGroup { get; set; }
    public bool LastFilterOnlyWeight { get; set; }
    public bool LastFilterOnlyPiece { get; set; }
    public bool LastFilterOnlyInStock { get; set; }
    public bool LastFilterOnlyFavorite { get; set; }
    public string? LastFilterSearchQuery { get; set; }
    public string LastFilterCatalogKind { get; set; } = "Все";
    public double? LastFilterPriceMin { get; set; }
    public double? LastFilterPriceMax { get; set; }
    public string StoreName { get; set; } = "MARKET PLUS";

    /// <summary>Имя тарифного плана с прошлого успешного входа. Нужно, чтобы при недоступной
    /// компании (старт без сети, протухший токен, 5xx) касса не открывала платные разделы
    /// бесплатно — см. TariffGate.IsStartTariff.</summary>
    public string? LastKnownPlanName { get; set; }

    public CustomerDisplaySettings CustomerDisplay { get; set; } = new();

    public string StoreAddress { get; set; } = "";

    public string StoreInn { get; set; } = "";

    /// <summary>Последняя известная дата окончания подписки NurCRM (Company.end_date, ISO),
    /// сохраняется при каждом успешном обновлении данных компании — используется для отсчёта
    /// дней до истечения офлайн, когда нет связи с сервером (см. CompanyInfoService.GetCachedSubscriptionStatus).</summary>
    public string? SubscriptionEndDateRaw { get; set; }

    public static UserPreferences Instance { get; } = new();

    private const string SettingsAppFolder = "NurMarketKassa";

    private static readonly string[] SettingsSearchFolders = { "NurMarketKassa", "NurCrmKassa" };

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SettingsAppFolder,
            "user-settings.json");

    private static string? FindExistingSettingsFile()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var folder in SettingsSearchFolders)
        {
            var path = Path.Combine(appData, folder, "user-settings.json");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static void LoadFromDiskAndMergeDefaults(AppSettings appDefaults)
    {
        var p = Instance;
        p.ScaleEnabled = appDefaults.Scale.Enabled;
        p.ScaleComPort = HardwarePortHelper.NormalizeComPort(appDefaults.Scale.ComPort, "COM2");
        p.ScaleBaudRate = appDefaults.Scale.BaudRate;
        p.ScaleRequestHex = appDefaults.Scale.RequestHex;
        p.ScalePollMs = appDefaults.Scale.PollMs;

        var rp = appDefaults.ReceiptPrinter;
        p.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(rp.DevicePath, "LPT1");
        p.ReceiptEncoding = string.IsNullOrWhiteSpace(rp.TextEncoding) ? "wpc1251" : rp.TextEncoding.Trim();
        p.ReceiptEscPosTable = rp.EscPosTableByte;
        p.ReceiptEscR = rp.EscRByte;
        p.ReceiptEnabled = rp.Enabled;
        p.ReceiptRetryCount = rp.RetryCount;

        try
        {
            var settingsPath = FindExistingSettingsFile();
            if (settingsPath == null || !File.Exists(settingsPath))
                return;

            var json = File.ReadAllText(settingsPath);
            var fromFile = JsonSerializer.Deserialize<UserPreferencesDto>(json, JsonOpt);
            if (fromFile == null)
                return;

            // Весы
            if (!string.IsNullOrWhiteSpace(fromFile.ScaleComPort))
                p.ScaleComPort = HardwarePortHelper.NormalizeComPort(fromFile.ScaleComPort, p.ScaleComPort);
            if (fromFile.ScaleBaudRate is > 0)
                p.ScaleBaudRate = fromFile.ScaleBaudRate.Value;
            p.ScaleEnabled = fromFile.ScaleEnabled ?? p.ScaleEnabled;
            if (!string.IsNullOrWhiteSpace(fromFile.PoleDisplayComPort))
                p.PoleDisplayComPort = HardwarePortHelper.NormalizeComPort(fromFile.PoleDisplayComPort, p.PoleDisplayComPort);
            if (fromFile.PoleDisplayBaudRate is > 0)
                p.PoleDisplayBaudRate = fromFile.PoleDisplayBaudRate.Value;
            p.PoleDisplayEnabled = fromFile.PoleDisplayEnabled ?? p.PoleDisplayEnabled;
            p.ScaleRequestHex = fromFile.ScaleRequestHex ?? p.ScaleRequestHex;
            if (fromFile.ScalePollMs is >= 0)
                p.ScalePollMs = fromFile.ScalePollMs.Value;

            // Принтер – общие
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptDevicePath))
                p.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(fromFile.ReceiptDevicePath, p.ReceiptDevicePath);
            if (!string.IsNullOrWhiteSpace(fromFile.LabelPrinterDevicePath))
                p.LabelPrinterDevicePath = fromFile.LabelPrinterDevicePath;
            if (fromFile.ReceiptEnabled is not null)
                p.ReceiptEnabled = fromFile.ReceiptEnabled.Value;
            if (fromFile.CashDrawerEnabled is not null)
                p.CashDrawerEnabled = fromFile.CashDrawerEnabled.Value;
            if (fromFile.CashDrawerPin is 0 or 1)
                p.CashDrawerPin = fromFile.CashDrawerPin.Value;
            if (fromFile.ReceiptPaperWidthMm is > 0)
                p.ReceiptPaperWidthMm = ReceiptPaperProfile.NormalizePaperWidthMm(fromFile.ReceiptPaperWidthMm);
            else if (fromFile.GraphicPaperWidthPixels is >= 500)
                p.ReceiptPaperWidthMm = ReceiptPaperProfile.Paper80mm;

            // Текстовый режим
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptEncoding))
                p.ReceiptEncoding = fromFile.ReceiptEncoding.Trim();
            p.ReceiptEscPosTable = fromFile.ReceiptEscPosTable ?? p.ReceiptEscPosTable;
            p.ReceiptEscR = fromFile.ReceiptEscR ?? p.ReceiptEscR;
            if (fromFile.ReceiptRetryCount is > 0)
                p.ReceiptRetryCount = fromFile.ReceiptRetryCount.Value;

            // Графический режим
            if (fromFile.GraphicReceiptEnabled.HasValue)
                p.GraphicReceiptEnabled = fromFile.GraphicReceiptEnabled.Value;
            if (!string.IsNullOrEmpty(fromFile.QrCodePath))
                p.QrCodePath = fromFile.QrCodePath;
            if (fromFile.GraphicPaperWidthPixels.HasValue)
                p.GraphicPaperWidthPixels = fromFile.GraphicPaperWidthPixels.Value;
            if (!string.IsNullOrEmpty(fromFile.GraphicFontFamily))
                p.GraphicFontFamily = fromFile.GraphicFontFamily;
            if (fromFile.SelectedPrintMode.HasValue)
                p.SelectedPrintMode = fromFile.SelectedPrintMode.Value;
            if (!string.IsNullOrEmpty(fromFile.StoreName))
                p.StoreName = fromFile.StoreName;
            if (fromFile.StoreAddress is not null)
                p.StoreAddress = fromFile.StoreAddress;
            if (fromFile.StoreInn is not null)
                p.StoreInn = fromFile.StoreInn;
            if (!string.IsNullOrWhiteSpace(fromFile.SubscriptionEndDateRaw))
                p.SubscriptionEndDateRaw = fromFile.SubscriptionEndDateRaw;
            if (fromFile.ShowStoreName.HasValue)
                p.ShowStoreName = fromFile.ShowStoreName.Value;
            if (fromFile.ShowAddress.HasValue)
                p.ShowAddress = fromFile.ShowAddress.Value;
            if (fromFile.ShowInn.HasValue)
                p.ShowInn = fromFile.ShowInn.Value;
            if (fromFile.ShowReceiptNumber.HasValue)
                p.ShowReceiptNumber = fromFile.ShowReceiptNumber.Value;
            if (fromFile.ShowDate.HasValue)
                p.ShowDate = fromFile.ShowDate.Value;
            if (fromFile.ShowItems.HasValue)
                p.ShowItems = fromFile.ShowItems.Value;
            if (fromFile.ShowTotal.HasValue)
                p.ShowTotal = fromFile.ShowTotal.Value;
            if (fromFile.ShowQrCode.HasValue)
                p.ShowQrCode = fromFile.ShowQrCode.Value;

            // Остальные настройки
            if (fromFile.Fullscreen is not null)
                p.Fullscreen = fromFile.Fullscreen.Value;
            if (fromFile.TrueFullscreen is not null)
                p.TrueFullscreen = fromFile.TrueFullscreen.Value;
            if (fromFile.LowPerformanceMode is not null)
                p.LowPerformanceMode = fromFile.LowPerformanceMode.Value;
            if (fromFile.VoiceControlEnabled is not null)
                p.VoiceControlEnabled = fromFile.VoiceControlEnabled.Value;
            if (fromFile.LanguagePackUnlocked is not null)
                p.LanguagePackUnlocked = fromFile.LanguagePackUnlocked.Value;
            if (fromFile.VoiceControlUnlocked is not null)
                p.VoiceControlUnlocked = fromFile.VoiceControlUnlocked.Value;
            if (fromFile.WarehouseAnalyticsUnlocked is not null)
                p.WarehouseAnalyticsUnlocked = fromFile.WarehouseAnalyticsUnlocked.Value;
            if (fromFile.PriceTagEditorUnlocked is not null)
                p.PriceTagEditorUnlocked = fromFile.PriceTagEditorUnlocked.Value;
            if (fromFile.LabelEditorUnlocked is not null)
                p.LabelEditorUnlocked = fromFile.LabelEditorUnlocked.Value;
            if (fromFile.BulkPriceTagUnlocked is not null)
                p.BulkPriceTagUnlocked = fromFile.BulkPriceTagUnlocked.Value;
            if (fromFile.VoiceLockEnabled is not null)
                p.VoiceLockEnabled = fromFile.VoiceLockEnabled.Value;
            if (fromFile.StaffTimesheetUnlocked is not null)
                p.StaffTimesheetUnlocked = fromFile.StaffTimesheetUnlocked.Value;
            if (fromFile.ScalesUnlocked is not null)
                p.ScalesUnlocked = fromFile.ScalesUnlocked.Value;
            else if (fromFile.ScaleEnabled == true)
                // Файл настроек сохранён ДО появления этого флага, но весы уже были настроены и
                // включены — не отключаем то, что уже реально работает на смене (см. doc-comment
                // у ScalesUnlocked).
                p.ScalesUnlocked = true;
            if (fromFile.BulkPriceTagKind is not null)
                p.BulkPriceTagKind = fromFile.BulkPriceTagKind.Value;
            if (fromFile.BulkPriceTagWidthMm is not null)
                p.BulkPriceTagWidthMm = fromFile.BulkPriceTagWidthMm.Value;
            if (fromFile.BulkPriceTagHeightMm is not null)
                p.BulkPriceTagHeightMm = fromFile.BulkPriceTagHeightMm.Value;
            if (fromFile.BulkPriceTagTargetIsA4 is not null)
                p.BulkPriceTagTargetIsA4 = fromFile.BulkPriceTagTargetIsA4.Value;
            if (fromFile.BulkPriceTagCopies is not null)
                p.BulkPriceTagCopies = fromFile.BulkPriceTagCopies.Value;
            if (fromFile.AppliedPublishDefaultsVersion is > 0)
                p.AppliedPublishDefaultsVersion = fromFile.AppliedPublishDefaultsVersion.Value;
            if (fromFile.DarkTheme is not null)
                p.DarkTheme = fromFile.DarkTheme.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.AccentTheme))
                p.AccentTheme = fromFile.AccentTheme;
            if (!string.IsNullOrWhiteSpace(fromFile.MainLayoutMode))
                p.MainLayoutMode = fromFile.MainLayoutMode;
            if (fromFile.GlassOpacityPercent is not null)
                p.GlassOpacityPercent = fromFile.GlassOpacityPercent.Value;
            if (fromFile.LiquidGlassEnabled is not null)
                p.LiquidGlassEnabled = fromFile.LiquidGlassEnabled.Value;
            if (fromFile.CustomButtonRadius is not null)
                p.CustomButtonRadius = fromFile.CustomButtonRadius;
            if (fromFile.CustomCardRadius is not null)
                p.CustomCardRadius = fromFile.CustomCardRadius;
            if (!string.IsNullOrWhiteSpace(fromFile.CustomAccentHex))
                p.CustomAccentHex = fromFile.CustomAccentHex;
            if (!string.IsNullOrWhiteSpace(fromFile.CustomFontFamily))
                p.CustomFontFamily = fromFile.CustomFontFamily;
            if (fromFile.CustomFontSize is not null)
                p.CustomFontSize = fromFile.CustomFontSize;
            if (!string.IsNullOrWhiteSpace(fromFile.CustomTextColor))
                p.CustomTextColor = fromFile.CustomTextColor;
            if (fromFile.UnlockedThemeIds is { Count: > 0 })
                p.UnlockedThemeIds = fromFile.UnlockedThemeIds;
            if (fromFile.MasterAccessExpiresAtUtc is not null)
                p.MasterAccessExpiresAtUtc = fromFile.MasterAccessExpiresAtUtc;
            if (fromFile.MasterUnlockedFeatureFlags is { Count: > 0 })
                p.MasterUnlockedFeatureFlags = fromFile.MasterUnlockedFeatureFlags;
            if (fromFile.MasterUnlockedThemeIds is { Count: > 0 })
                p.MasterUnlockedThemeIds = fromFile.MasterUnlockedThemeIds;
            if (fromFile.Autostart is not null)
                p.Autostart = fromFile.Autostart.Value;
            if (fromFile.AutoShowTouchKeyboard is not null)
                p.AutoShowTouchKeyboard = fromFile.AutoShowTouchKeyboard.Value;
            // Authentication input values are intentionally not restored from
            // Settings. Auto-login reads the encrypted session file directly.
            p.LastLoginEmail = "";
            p.LastLoginPassword = "";
            if (!string.IsNullOrEmpty(fromFile.LastCatalogUserKey))
                p.LastCatalogUserKey = fromFile.LastCatalogUserKey!;
            if (!string.IsNullOrEmpty(fromFile.LastDataAccountKey))
                p.LastDataAccountKey = fromFile.LastDataAccountKey!;
            if (!string.IsNullOrWhiteSpace(fromFile.PostgreSqlConnectionStringEncrypted))
                p.PostgreSqlConnectionStringEncrypted = fromFile.PostgreSqlConnectionStringEncrypted;
            if (!string.IsNullOrEmpty(fromFile.LastFilterCategory))
                p.LastFilterCategory = fromFile.LastFilterCategory;
            if (!string.IsNullOrEmpty(fromFile.LastFilterBrand))
                p.LastFilterBrand = fromFile.LastFilterBrand;
            if (fromFile.CatalogViewMode is not null)
                p.CatalogViewMode = fromFile.CatalogViewMode.Value;
            if (fromFile.ShowCatalogPhotos is not null)
                p.ShowCatalogPhotos = fromFile.ShowCatalogPhotos.Value;
            if (fromFile.Language is not null)
                p.Language = fromFile.Language.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.PreferredCashboxId))
                p.PreferredCashboxId = fromFile.PreferredCashboxId;
            if (!string.IsNullOrWhiteSpace(fromFile.PreferredCashboxName))
                p.PreferredCashboxName = fromFile.PreferredCashboxName;
            if (fromFile.SingleClickToCart is not null)
                p.SingleClickToCart = fromFile.SingleClickToCart.Value;
            if (fromFile.ResetManualAddQtyAfterAdd is not null)
                p.ResetManualAddQtyAfterAdd = fromFile.ResetManualAddQtyAfterAdd.Value;
            if (fromFile.LastSeenAppVersion is not null)
                p.LastSeenAppVersion = fromFile.LastSeenAppVersion;
            if (fromFile.LastOnlineContactAtUtc is not null)
                p.LastOnlineContactAtUtc = fromFile.LastOnlineContactAtUtc;
            if (fromFile.AutonomousModeActivated is not null)
                p.AutonomousModeActivated = fromFile.AutonomousModeActivated.Value;
            if (fromFile.AutonomousModeActivationKey is not null)
                p.AutonomousModeActivationKey = fromFile.AutonomousModeActivationKey;
            if (fromFile.AutonomousAutoResume is not null)
                p.AutonomousAutoResume = fromFile.AutonomousAutoResume.Value;
            if (fromFile.PreserveCatalogOnNextAutonomousLogin is not null)
                p.PreserveCatalogOnNextAutonomousLogin = fromFile.PreserveCatalogOnNextAutonomousLogin.Value;
            if (fromFile.ScaleBarcodeLayout is not null)
                p.ScaleBarcodeLayout = fromFile.ScaleBarcodeLayout;
            if (fromFile.ScaleBarcodeMode is not null)
                p.ScaleBarcodeMode = fromFile.ScaleBarcodeMode;
            if (fromFile.ScaleBarcodeAmountUnit is not null)
                p.ScaleBarcodeAmountUnit = fromFile.ScaleBarcodeAmountUnit;
            if (fromFile.ToolsPanelExpanded is not null)
                p.ToolsPanelExpanded = fromFile.ToolsPanelExpanded.Value;
            if (fromFile.DynamicPaymentQrEnabled is not null)
                p.DynamicPaymentQrEnabled = fromFile.DynamicPaymentQrEnabled.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramBotTokenProtected))
                p.TelegramBotToken = WindowsDpapiHelper.UnprotectFromBase64(fromFile.TelegramBotTokenProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramChatId))
                p.TelegramChatId = fromFile.TelegramChatId;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramChatTitle))
                p.TelegramChatTitle = fromFile.TelegramChatTitle;
            if (fromFile.TelegramShiftSummaryEnabled is not null)
                p.TelegramShiftSummaryEnabled = fromFile.TelegramShiftSummaryEnabled.Value;
            if (fromFile.AnalyticsExportUnlocked is not null)
                p.AnalyticsExportUnlocked = fromFile.AnalyticsExportUnlocked.Value;
            if (fromFile.TelegramBotUnlocked is not null)
                p.TelegramBotUnlocked = fromFile.TelegramBotUnlocked.Value;
            if (fromFile.ShiftAnalyticsUnlocked is not null)
                p.ShiftAnalyticsUnlocked = fromFile.ShiftAnalyticsUnlocked.Value;
            if (fromFile.TelegramCommandsEnabled is not null)
                p.TelegramCommandsEnabled = fromFile.TelegramCommandsEnabled.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramBotUsername))
                p.TelegramBotUsername = fromFile.TelegramBotUsername;
            if (!string.IsNullOrWhiteSpace(fromFile.OwnerPhone))
                p.OwnerPhone = fromFile.OwnerPhone;
            if (fromFile.ShtrikhDirectLan is not null)
                p.ShtrikhDirectLan = fromFile.ShtrikhDirectLan.Value;
            // Порт 4001 и пароль 0000 были УГАДАННЫМИ значениями по умолчанию: протокол порт
            // не задаёт вовсе, а 0000 весы не принимают (живая проверка 2026-09-22 — весы
            // заблокировались по числу неудачных попыток). Если в файле лежит ровно эта пара,
            // её никто не настраивал осознанно — подставляем подтверждённые 1111/0030.
            // Любое отличающееся значение оставляем как есть: его владелец выбрал сам.
            var untouchedGuess = fromFile.ScaleLanPort == 4001
                && string.Equals(fromFile.ScaleLanPassword, "0000", StringComparison.Ordinal);

            if (fromFile.ScaleLanPort is > 0 && !untouchedGuess)
                p.ScaleLanPort = fromFile.ScaleLanPort.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.ScaleLanPassword) && !untouchedGuess)
                p.ScaleLanPassword = fromFile.ScaleLanPassword;
            if (fromFile.BankQrPaths != null)
                p.BankQrPaths = fromFile.BankQrPaths;
            if (fromFile.BankLogoPaths != null)
                p.BankLogoPaths = fromFile.BankLogoPaths;
            if (fromFile.CustomBankNames != null)
                p.CustomBankNames = fromFile.CustomBankNames;

            if (fromFile.VisibleBankNames != null)
                p.VisibleBankNames = fromFile.VisibleBankNames;
            if (fromFile.EmployeeAccessCodes != null)
                p.EmployeeAccessCodes = fromFile.EmployeeAccessCodes;
            if (fromFile.LoyaltyEnabled is not null)
                p.LoyaltyEnabled = fromFile.LoyaltyEnabled.Value;
            if (fromFile.LoyaltyEarnPercent is not null)
                p.LoyaltyEarnPercent = Math.Clamp(fromFile.LoyaltyEarnPercent.Value, 0, 100);
            if (fromFile.PosHotkeys != null)
                p.PosHotkeys = fromFile.PosHotkeys;
            if (fromFile.LastFilterDateFrom.HasValue) p.LastFilterDateFrom = fromFile.LastFilterDateFrom;
            if (fromFile.LastFilterDateTo.HasValue) p.LastFilterDateTo = fromFile.LastFilterDateTo;
            if (fromFile.LastFilterClient is not null) p.LastFilterClient = fromFile.LastFilterClient;
            if (fromFile.LastFilterStatus is not null) p.LastFilterStatus = fromFile.LastFilterStatus;
            if (fromFile.LastFilterHotkeyGroup is not null) p.LastFilterHotkeyGroup = fromFile.LastFilterHotkeyGroup;
            if (fromFile.LastFilterOnlyWeight.HasValue) p.LastFilterOnlyWeight = fromFile.LastFilterOnlyWeight.Value;
            if (fromFile.LastFilterOnlyPiece.HasValue) p.LastFilterOnlyPiece = fromFile.LastFilterOnlyPiece.Value;
            if (fromFile.LastFilterOnlyInStock.HasValue) p.LastFilterOnlyInStock = fromFile.LastFilterOnlyInStock.Value;
            if (fromFile.LastFilterOnlyFavorite.HasValue) p.LastFilterOnlyFavorite = fromFile.LastFilterOnlyFavorite.Value;
            if (fromFile.LastFilterSearchQuery is not null) p.LastFilterSearchQuery = fromFile.LastFilterSearchQuery;
            if (!string.IsNullOrWhiteSpace(fromFile.LastFilterCatalogKind)) p.LastFilterCatalogKind = fromFile.LastFilterCatalogKind;
            if (fromFile.LastFilterPriceMin.HasValue) p.LastFilterPriceMin = fromFile.LastFilterPriceMin;
            if (fromFile.LastFilterPriceMax.HasValue) p.LastFilterPriceMax = fromFile.LastFilterPriceMax;
            if (fromFile.GraphicFontSize.HasValue)
                p.GraphicFontSize = fromFile.GraphicFontSize.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.BackgroundImagePath))
                p.BackgroundImagePath = fromFile.BackgroundImagePath!;
            if (fromFile.BackgroundOpacity is > 0)
                p.BackgroundOpacity = Math.Clamp(fromFile.BackgroundOpacity.Value, 0.05, 0.8);
            if (fromFile.BackgroundBlurPercent is not null)
                p.BackgroundBlurPercent = Math.Clamp(fromFile.BackgroundBlurPercent.Value, 0, 100);
            if (fromFile.ApplyBackgroundToCashierScreen is not null)
                p.ApplyBackgroundToCashierScreen = fromFile.ApplyBackgroundToCashierScreen.Value;
            if (fromFile.UiScalePercent is not null)
                p.UiScalePercent = Math.Clamp(fromFile.UiScalePercent.Value, 50, 200);
            if (fromFile.CustomerDisplay is not null)
            {
                fromFile.CustomerDisplay.Normalize();
                p.CustomerDisplay = fromFile.CustomerDisplay;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"User preferences load failed: {ex.GetType().Name}", "WARNING");
        }
        finally
        {
            ApplyPublishedDefaults(p);
        }
    }

    private static void ApplyPublishedDefaults(UserPreferences preferences)
    {
        var defaultsPath = Path.Combine(AppContext.BaseDirectory, "publish-defaults.json");
        if (!File.Exists(defaultsPath))
            return;

        try
        {
            var defaults = JsonSerializer.Deserialize<PublishDefaults>(File.ReadAllText(defaultsPath), JsonOpt);
            if (defaults is null || defaults.Version <= preferences.AppliedPublishDefaultsVersion)
                return;

            if (defaults.Fullscreen)
                preferences.Fullscreen = true;

            preferences.AppliedPublishDefaultsVersion = defaults.Version;
            preferences.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Published defaults load failed: {ex.GetType().Name}", "WARNING");
        }
    }

    public void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var dto = new UserPreferencesDto
            {
                ScaleComPort = ScaleComPort,
                ScaleBaudRate = ScaleBaudRate,
                ScaleEnabled = ScaleEnabled,
                PoleDisplayComPort = PoleDisplayComPort,
                PoleDisplayBaudRate = PoleDisplayBaudRate,
                PoleDisplayEnabled = PoleDisplayEnabled,
                ScaleRequestHex = ScaleRequestHex,
                ScalePollMs = ScalePollMs,
                ReceiptDevicePath = ReceiptDevicePath,
                LabelPrinterDevicePath = LabelPrinterDevicePath,
                ReceiptEncoding = ReceiptEncoding,
                ReceiptEscPosTable = ReceiptEscPosTable,
                ReceiptEscR = ReceiptEscR,
                ReceiptEnabled = ReceiptEnabled,
                CashDrawerEnabled = CashDrawerEnabled,
                CashDrawerPin = CashDrawerPin,
                ReceiptPaperWidthMm = ReceiptPaperWidthMm,
                ReceiptRetryCount = ReceiptRetryCount,
                Fullscreen = Fullscreen,
                TrueFullscreen = TrueFullscreen,
                LowPerformanceMode = LowPerformanceMode,
                VoiceControlEnabled = VoiceControlEnabled,
                VoiceControlUnlocked = VoiceControlUnlocked,
                LanguagePackUnlocked = LanguagePackUnlocked,
                WarehouseAnalyticsUnlocked = WarehouseAnalyticsUnlocked,
                PriceTagEditorUnlocked = PriceTagEditorUnlocked,
                LabelEditorUnlocked = LabelEditorUnlocked,
                BulkPriceTagUnlocked = BulkPriceTagUnlocked,
                VoiceLockEnabled = VoiceLockEnabled,
                StaffTimesheetUnlocked = StaffTimesheetUnlocked,
                ScalesUnlocked = ScalesUnlocked,
                BulkPriceTagKind = BulkPriceTagKind,
                BulkPriceTagWidthMm = BulkPriceTagWidthMm,
                BulkPriceTagHeightMm = BulkPriceTagHeightMm,
                BulkPriceTagTargetIsA4 = BulkPriceTagTargetIsA4,
                BulkPriceTagCopies = BulkPriceTagCopies,
                AppliedPublishDefaultsVersion = AppliedPublishDefaultsVersion,
                DarkTheme = DarkTheme,
                AccentTheme = AccentTheme,
                MainLayoutMode = MainLayoutMode,
                GlassOpacityPercent = GlassOpacityPercent,
                LiquidGlassEnabled = LiquidGlassEnabled,
                CustomButtonRadius = CustomButtonRadius,
                CustomCardRadius = CustomCardRadius,
                CustomAccentHex = CustomAccentHex,
                CustomFontFamily = CustomFontFamily,
                CustomFontSize = CustomFontSize,
                CustomTextColor = CustomTextColor,
                UnlockedThemeIds = UnlockedThemeIds,
                MasterAccessExpiresAtUtc = MasterAccessExpiresAtUtc,
                MasterUnlockedFeatureFlags = MasterUnlockedFeatureFlags,
                MasterUnlockedThemeIds = MasterUnlockedThemeIds,
                Autostart = Autostart,
                AutoShowTouchKeyboard = AutoShowTouchKeyboard,
                LastLoginEmail = null,
                LastCatalogUserKey = LastCatalogUserKey,
                LastDataAccountKey = LastDataAccountKey,
                LastLoginPassword = null,
                LastLoginPasswordEncrypted = null,
                PostgreSqlConnectionStringEncrypted = PostgreSqlConnectionStringEncrypted,
                LastFilterCategory = LastFilterCategory,
                LastFilterBrand = LastFilterBrand,
                CatalogViewMode = CatalogViewMode,
                ShowCatalogPhotos = ShowCatalogPhotos,
                Language = Language,
                PreferredCashboxId = PreferredCashboxId,
                PreferredCashboxName = PreferredCashboxName,
                SingleClickToCart = SingleClickToCart,
                ResetManualAddQtyAfterAdd = ResetManualAddQtyAfterAdd,
                LastSeenAppVersion = LastSeenAppVersion,
                LastOnlineContactAtUtc = LastOnlineContactAtUtc,
                AutonomousModeActivated = AutonomousModeActivated,
                AutonomousModeActivationKey = AutonomousModeActivationKey,
                AutonomousAutoResume = AutonomousAutoResume,
                PreserveCatalogOnNextAutonomousLogin = PreserveCatalogOnNextAutonomousLogin,
                ScaleBarcodeLayout = ScaleBarcodeLayout,
                ScaleBarcodeMode = ScaleBarcodeMode,
                ScaleBarcodeAmountUnit = ScaleBarcodeAmountUnit,
                ToolsPanelExpanded = ToolsPanelExpanded,
                DynamicPaymentQrEnabled = DynamicPaymentQrEnabled,
                // Токен шифруется DPAPI под текущего пользователя Windows: файл, скопированный
                // на другой компьютер, расшифровать нельзя.
                TelegramBotTokenProtected = WindowsDpapiHelper.ProtectToBase64(TelegramBotToken),
                TelegramChatId = TelegramChatId,
                TelegramChatTitle = TelegramChatTitle,
                TelegramShiftSummaryEnabled = TelegramShiftSummaryEnabled,
                AnalyticsExportUnlocked = AnalyticsExportUnlocked,
                TelegramBotUnlocked = TelegramBotUnlocked,
                ShiftAnalyticsUnlocked = ShiftAnalyticsUnlocked,
                TelegramCommandsEnabled = TelegramCommandsEnabled,
                TelegramBotUsername = TelegramBotUsername,
                OwnerPhone = OwnerPhone,
                ShtrikhDirectLan = ShtrikhDirectLan,
                ScaleLanPort = ScaleLanPort,
                ScaleLanPassword = ScaleLanPassword,
                BankQrPaths = BankQrPaths,
                BankLogoPaths = BankLogoPaths,
                CustomBankNames = CustomBankNames,
                VisibleBankNames = VisibleBankNames,
                EmployeeAccessCodes = EmployeeAccessCodes,
                LoyaltyEnabled = LoyaltyEnabled,
                LoyaltyEarnPercent = LoyaltyEarnPercent,
                PosHotkeys = PosHotkeys,
                LastFilterDateFrom = LastFilterDateFrom,
                LastFilterDateTo = LastFilterDateTo,
                LastFilterClient = LastFilterClient,
                LastFilterStatus = LastFilterStatus,
                LastFilterHotkeyGroup = LastFilterHotkeyGroup,
                LastFilterOnlyWeight = LastFilterOnlyWeight,
                LastFilterOnlyPiece = LastFilterOnlyPiece,
                LastFilterOnlyInStock = LastFilterOnlyInStock,
                LastFilterOnlyFavorite = LastFilterOnlyFavorite,
                LastFilterSearchQuery = LastFilterSearchQuery,
                LastFilterCatalogKind = LastFilterCatalogKind,
                LastFilterPriceMin = LastFilterPriceMin,
                LastFilterPriceMax = LastFilterPriceMax,
                GraphicReceiptEnabled = GraphicReceiptEnabled,
                QrCodePath = QrCodePath,
                GraphicPaperWidthPixels = GraphicPaperWidthPixels,
                GraphicFontFamily = GraphicFontFamily,
                SelectedPrintMode = SelectedPrintMode,
                StoreName = StoreName,
                StoreAddress = StoreAddress,
                StoreInn = StoreInn,
                SubscriptionEndDateRaw = SubscriptionEndDateRaw,
                ShowStoreName = ShowStoreName,
                ShowAddress = ShowAddress,
                ShowInn = ShowInn,
                ShowReceiptNumber = ShowReceiptNumber,
                ShowDate = ShowDate,
                ShowItems = ShowItems,
                ShowTotal = ShowTotal,
                ShowQrCode = ShowQrCode,
                GraphicFontSize = GraphicFontSize,
                BackgroundImagePath = BackgroundImagePath,
                BackgroundOpacity = BackgroundOpacity,
                BackgroundBlurPercent = BackgroundBlurPercent,
                ApplyBackgroundToCashierScreen = ApplyBackgroundToCashierScreen,
                UiScalePercent = UiScalePercent,
                CustomerDisplay = CustomerDisplay,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, JsonOpt));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"User preferences save failed: {ex.GetType().Name}", "WARNING");
        }
    }

    public ScaleSettings ToScaleSettings() =>
        new()
        {
            Enabled = ScaleEnabled,
            ComPort = HardwarePortHelper.NormalizeComPort(ScaleComPort),
            BaudRate = ScaleBaudRate,
            RequestHex = ScaleRequestHex,
            PollMs = ScalePollMs,
        };

    public ReceiptPrinterSettings ToReceiptPrinterSettings() =>
        new()
        {
            Enabled = ReceiptEnabled,
            DevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptDevicePath),
            TextEncoding = ReceiptEncoding,
            EscPosTableByte = ReceiptEscPosTable,
            EscRByte = ReceiptEscR,
            RetryCount = ReceiptRetryCount,
        };

    public GraphicReceiptSettings ToGraphicReceiptSettings() =>
        new()
        {
            PaperWidthPixels = ReceiptPaperProfile.GetRasterWidthPixels(ReceiptPaperWidthMm),
            FontFamily = string.IsNullOrWhiteSpace(GraphicFontFamily) ? TestReceiptLineBuilder.FontFamily : GraphicFontFamily,
            FontSize = TestReceiptLineBuilder.ResolveFontSize(GraphicFontSize),
            DevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptDevicePath),
            RetryCount = ReceiptRetryCount,
            ShowStoreName = ShowStoreName,
            ShowAddress = ShowAddress,
            ShowInn = ShowInn,
            ShowReceiptNumber = ShowReceiptNumber,
            ShowDate = ShowDate,
            ShowItems = ShowItems,
            ShowTotal = ShowTotal,
            ShowQrCode = ShowQrCode,
            QrCodePath = QrCodePath,
            StoreAddress = StoreAddress,
            StoreInn = StoreInn,
            GraphicPrintMode = SelectedPrintMode == PrintMode.Graphic,
        };

    private sealed class UserPreferencesDto
    {
        public string? ScaleComPort { get; set; }
        public int? ScaleBaudRate { get; set; }
        public bool? ScaleEnabled { get; set; }
        public string? PoleDisplayComPort { get; set; }
        public int? PoleDisplayBaudRate { get; set; }
        public bool? PoleDisplayEnabled { get; set; }
        public string? ScaleRequestHex { get; set; }
        public int? ScalePollMs { get; set; }
        public string? ReceiptDevicePath { get; set; }
        public string? LabelPrinterDevicePath { get; set; }
        public string? ReceiptEncoding { get; set; }
        public int? ReceiptEscPosTable { get; set; }
        public int? ReceiptEscR { get; set; }
        public bool? ReceiptEnabled { get; set; }
        public bool? CashDrawerEnabled { get; set; }
        public int? CashDrawerPin { get; set; }
        public int? ReceiptPaperWidthMm { get; set; }
        public int? ReceiptRetryCount { get; set; }
        public bool? Fullscreen { get; set; }
        public bool? TrueFullscreen { get; set; }
        public bool? LowPerformanceMode { get; set; }
        public bool? VoiceControlEnabled { get; set; }
        public bool? VoiceControlUnlocked { get; set; }
        public bool? LanguagePackUnlocked { get; set; }
        public bool? WarehouseAnalyticsUnlocked { get; set; }
        public bool? PriceTagEditorUnlocked { get; set; }
        public bool? LabelEditorUnlocked { get; set; }
        public bool? BulkPriceTagUnlocked { get; set; }
        public bool? VoiceLockEnabled { get; set; }
        public bool? StaffTimesheetUnlocked { get; set; }
        public bool? ScalesUnlocked { get; set; }
        public int? BulkPriceTagKind { get; set; }
        public double? BulkPriceTagWidthMm { get; set; }
        public double? BulkPriceTagHeightMm { get; set; }
        public bool? BulkPriceTagTargetIsA4 { get; set; }
        public int? BulkPriceTagCopies { get; set; }
        public int? AppliedPublishDefaultsVersion { get; set; }
        public bool? DarkTheme { get; set; }
        public string? AccentTheme { get; set; }
        public string? MainLayoutMode { get; set; }
        public double? GlassOpacityPercent { get; set; }
        public bool? LiquidGlassEnabled { get; set; }
        public double? CustomButtonRadius { get; set; }
        public double? CustomCardRadius { get; set; }
        public string? CustomAccentHex { get; set; }
        public string? CustomFontFamily { get; set; }
        public double? CustomFontSize { get; set; }
        public string? CustomTextColor { get; set; }
        public List<string>? UnlockedThemeIds { get; set; }
        public DateTime? MasterAccessExpiresAtUtc { get; set; }
        public List<string>? MasterUnlockedFeatureFlags { get; set; }
        public List<string>? MasterUnlockedThemeIds { get; set; }
        public bool? Autostart { get; set; }
        public bool? AutoShowTouchKeyboard { get; set; }
        public string? LastLoginEmail { get; set; }
        public string? LastCatalogUserKey { get; set; }
        public string? LastDataAccountKey { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastLoginPassword { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastLoginPasswordEncrypted { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PostgreSqlConnectionStringEncrypted { get; set; }
        public string? LastFilterCategory { get; set; }
        public string? LastFilterBrand { get; set; }
        public CatalogViewMode? CatalogViewMode { get; set; }
        public bool? ShowCatalogPhotos { get; set; }
        public AppLanguage? Language { get; set; }
        public string? PreferredCashboxId { get; set; }
        public string? PreferredCashboxName { get; set; }
        public bool? SingleClickToCart { get; set; }
        public bool? ResetManualAddQtyAfterAdd { get; set; }
        public string? LastSeenAppVersion { get; set; }
        public string? LastOnlineContactAtUtc { get; set; }
        public bool? AutonomousModeActivated { get; set; }
        public string? AutonomousModeActivationKey { get; set; }
        public bool? AutonomousAutoResume { get; set; }
        public bool? PreserveCatalogOnNextAutonomousLogin { get; set; }
        public string? ScaleBarcodeLayout { get; set; }
        public string? ScaleBarcodeMode { get; set; }
        public string? ScaleBarcodeAmountUnit { get; set; }
        public bool? ToolsPanelExpanded { get; set; }
        public bool? DynamicPaymentQrEnabled { get; set; }
        public string? TelegramBotTokenProtected { get; set; }
        public string? TelegramChatId { get; set; }
        public string? TelegramChatTitle { get; set; }
        public bool? AnalyticsExportUnlocked { get; set; }
        public bool? TelegramBotUnlocked { get; set; }
        public bool? ShiftAnalyticsUnlocked { get; set; }
        public bool? TelegramCommandsEnabled { get; set; }
        public string? TelegramBotUsername { get; set; }
        public bool? TelegramShiftSummaryEnabled { get; set; }
        public string? OwnerPhone { get; set; }
        public bool? ShtrikhDirectLan { get; set; }
        public int? ScaleLanPort { get; set; }
        public string? ScaleLanPassword { get; set; }
        public Dictionary<string, string>? BankQrPaths { get; set; }
        public List<string>? VisibleBankNames { get; set; }
        public Dictionary<string, string>? BankLogoPaths { get; set; }
        public List<string>? CustomBankNames { get; set; }
        public List<EmployeeAccessCode>? EmployeeAccessCodes { get; set; }
        public bool? LoyaltyEnabled { get; set; }
        public double? LoyaltyEarnPercent { get; set; }
        public Dictionary<string, string>? PosHotkeys { get; set; }
        public DateTime? LastFilterDateFrom { get; set; }
        public DateTime? LastFilterDateTo { get; set; }
        public string? LastFilterClient { get; set; }
        public string? LastFilterStatus { get; set; }
        public string? LastFilterHotkeyGroup { get; set; }
        public bool? LastFilterOnlyWeight { get; set; }
        public bool? LastFilterOnlyPiece { get; set; }
        public bool? LastFilterOnlyInStock { get; set; }
        public bool? LastFilterOnlyFavorite { get; set; }
        public string? LastFilterSearchQuery { get; set; }
        public string? LastFilterCatalogKind { get; set; }
        public double? LastFilterPriceMin { get; set; }
        public double? LastFilterPriceMax { get; set; }
        public bool? GraphicReceiptEnabled { get; set; }
        public string? QrCodePath { get; set; }
        public int? GraphicPaperWidthPixels { get; set; }
        public string? GraphicFontFamily { get; set; }
        public PrintMode? SelectedPrintMode { get; set; }
        public string? StoreName { get; set; }
        public string? StoreAddress { get; set; }
        public string? StoreInn { get; set; }
        public string? SubscriptionEndDateRaw { get; set; }
        public bool? ShowStoreName { get; set; }
        public bool? ShowAddress { get; set; }
        public bool? ShowInn { get; set; }
        public bool? ShowReceiptNumber { get; set; }
        public bool? ShowDate { get; set; }
        public bool? ShowItems { get; set; }
        public bool? ShowTotal { get; set; }
        public bool? ShowQrCode { get; set; }
        public float? GraphicFontSize { get; set; }
        public string? BackgroundImagePath { get; set; }
        public double? BackgroundBlurPercent { get; set; }
        public bool? ApplyBackgroundToCashierScreen { get; set; }
        public double? UiScalePercent { get; set; }
        public double? BackgroundOpacity { get; set; }
        public CustomerDisplaySettings? CustomerDisplay { get; set; }
    }

    private sealed class PublishDefaults
    {
        public int Version { get; set; }
        public bool Fullscreen { get; set; }
    }
}
