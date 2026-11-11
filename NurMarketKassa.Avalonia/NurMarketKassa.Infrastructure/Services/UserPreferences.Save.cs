﻿using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса UserPreferences (разбор огромного файла на partial-файлы), логика не менялась; остальное — UserPreferences.cs
public sealed partial class UserPreferences
{
    /// <summary>2026-10-19: стенд снимков (без экрана) выставляет true отражением ДО создания окон — SaveToDisk ничего не пишет
    /// (19.10 окно «Настроек» в стенде записало настройки по умолчанию в настоящий файл кассы). Как _persistenceDisabled
    /// у LabelScaleStore. В программе всегда false.</summary>
#pragma warning disable CS0649 // выставляется стендом отражением
    private static bool _persistenceDisabled;
#pragma warning restore CS0649

    /// <summary>2026-11-01 (Н2-02): true — при старте файл настроек не прочитан (занят другой программой после трёх попыток): сохранение отключено, чтобы настройки по умолчанию не затёрли рабочий файл.</summary>
    private static bool _readBlockedPersistence;

    public void SaveToDisk()
    {
        if (_persistenceDisabled)
            return;
        // 2026-11-01 (контрольный аудит, Н2-02): файл настроек не удалось прочитать при старте (занят/нет доступа) — работаем с настройками в памяти и НЕ пишем их поверх файла.
        if (_readBlockedPersistence)
            return;
        // 2026-10-31: в файл — только ключи активации, без функций, включённых сервером (см. IsKeyUnlocked).
        using var rawView = ServerAddons.RawView();
        // 2026-10-31 (приёмка 1.17.68, В-2): снимок настроек и запись файла — под одной блокировкой между потоками (таймер ключей и UI-поток):
        // иначе старый снимок мог затереть новый.
        var saveLockHeld = false;
        System.Threading.Monitor.Enter(SaveFileSync);
        saveLockHeld = true;
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
                Scale2Enabled = Scale2Enabled,
                Scale2ComPort = Scale2ComPort,
                Scale2BaudRate = Scale2BaudRate,
                Scale3Enabled = Scale3Enabled,
                Scale3ComPort = Scale3ComPort,
                Scale3BaudRate = Scale3BaudRate,
                PoleDisplayComPort = PoleDisplayComPort,
                PoleDisplayBaudRate = PoleDisplayBaudRate,
                PoleDisplayEnabled = PoleDisplayEnabled,
                PoleDisplayProtocol = PoleDisplayProtocol,
                ScaleRequestHex = ScaleRequestHex,
                ScalePollMs = ScalePollMs,
                ReceiptDevicePath = ReceiptDevicePath,
                LabelPrinterDevicePath = LabelPrinterDevicePath,
                KnownNetworkPrinters = KnownNetworkPrinters,
                LabelPageFromTemplate = LabelPageFromTemplate,
                TransferAutoWaybill = TransferAutoWaybill,
                TransferAutoOther = TransferAutoOther,
                AskQtyOnRepeatScan = AskQtyOnRepeatScan,
                LabelGapMm = LabelGapMm,
                LabelOffsetXMm = LabelOffsetXMm,
                LabelOffsetYMm = LabelOffsetYMm,
                LabelRawLanguage = LabelRawLanguage,
                LabelTsplDensity = LabelTsplDensity,
                LabelTsplSpeed = LabelTsplSpeed,
                LabelTsplFlip = LabelTsplFlip,
                LabelTsplSensor = LabelTsplSensor,
                ReceiptEncoding = ReceiptEncoding,
                ReceiptEscPosTable = ReceiptEscPosTable,
                ReceiptEscR = ReceiptEscR,
                ReceiptEnabled = ReceiptEnabled,
                CheckoutPrintReceipt = CheckoutPrintReceipt,
                CheckoutCardPay = CheckoutCardPay,
                CashDrawerEnabled = CashDrawerEnabled,
                CashDrawerPin = CashDrawerPin,
                ReceiptPaperWidthMm = ReceiptPaperWidthMm,
                ReceiptRetryCount = ReceiptRetryCount,
                Fullscreen = Fullscreen,
                TrueFullscreen = TrueFullscreen,
                LowPerformanceMode = LowPerformanceMode,
                ModalBackgroundBlur = ModalBackgroundBlur, // 2026-11-11
                WindowsRenderMode = WindowsRenderMode, // 2026-11-11
                AskOwnerPinOnKassaPc = AskOwnerPinOnKassaPc, // 2026-11-06 (Л4-19)
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
                UnlockedPacks = UnlockedPacks,
                HiddenSections = HiddenSections,
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
                MarketplaceWebUrl = MarketplaceWebUrl, // 2026-11-06
                AutoInstallPurchasedThemes = AutoInstallPurchasedThemes,
                Language = Language,
                AiLanguageOverride = AiLanguageOverride, // 2026-11-05
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
                ScaleAmountPrefixes = ScaleAmountPrefixes,
                ScaleWeightPrefixes = ScaleWeightPrefixes,
                ScaleBarcodeAmountUnit = ScaleBarcodeAmountUnit,
                ScaleBarcodeAmountUnitLocal = ScaleBarcodeAmountUnitLocal,
                ScaleMaxLabelAmountSom = ScaleMaxLabelAmountSom,
                ToolsPanelExpanded = ToolsPanelExpanded,
                DynamicPaymentQrEnabled = DynamicPaymentQrEnabled,
                BlindCashCount = BlindCashCount,
                BlindCountFirstAttempt = BlindCountFirstAttempt,
                AskCartRemovalReason = AskCartRemovalReason,
                CartRemovalReasonThreshold = CartRemovalReasonThreshold,
                // Токен шифруется DPAPI под текущего пользователя Windows: файл, скопированный
                // на другой компьютер, расшифровать нельзя.
                TelegramBotTokenProtected = WindowsDpapiHelper.ProtectToBase64(TelegramBotToken),
                TelegramChatId = TelegramChatId,
                TelegramChatTitle = TelegramChatTitle,
                TelegramOrdersChatId = TelegramOrdersChatId,
                TelegramOrdersChatTitle = TelegramOrdersChatTitle,
                TelegramOrdersTarget = TelegramOrdersTarget,
                TelegramOrdersShowAmount = TelegramOrdersShowAmount,
                TelegramOrdersBoundAtUtc = TelegramOrdersBoundAtUtc,
                TelegramOrdersBotRemoved = TelegramOrdersBotRemoved,
                TelegramShiftSummaryEnabled = TelegramShiftSummaryEnabled,
                AnalyticsExportUnlocked = AnalyticsExportUnlocked,
                TelegramBotUnlocked = TelegramBotUnlocked,
                ShiftAnalyticsUnlocked = ShiftAnalyticsUnlocked,
                TelegramCommandsEnabled = TelegramCommandsEnabled,
                DebtPrepaymentChooseMethod = DebtPrepaymentChooseMethod,
                AdvisorCapabilitiesJson = AdvisorCapabilitiesJson,
                PurchaseEditWindow = PurchaseEditWindow,
                StaffAutoMarkMode = StaffAutoMarkMode,
                IdleLogoutMinutes = IdleLogoutMinutes, // 2026-11-08
                DiscountModePercent = DiscountModePercent,
                TelegramBotUsername = TelegramBotUsername,
                OwnerPhone = OwnerPhone,
                TelegramAiKeyProtected = string.IsNullOrWhiteSpace(TelegramAiKey) ? null : WindowsDpapiHelper.ProtectToBase64(TelegramAiKey),
                TelegramAiKeysExtraProtected = string.IsNullOrWhiteSpace(TelegramAiKeysExtra) ? null : WindowsDpapiHelper.ProtectToBase64(TelegramAiKeysExtra),
                GroqApiKeyProtected = string.IsNullOrWhiteSpace(GroqApiKey) ? null : WindowsDpapiHelper.ProtectToBase64(GroqApiKey),
                OpenRouterApiKeyProtected = string.IsNullOrWhiteSpace(OpenRouterApiKey) ? null : WindowsDpapiHelper.ProtectToBase64(OpenRouterApiKey),
                LanSyncEnabled = LanSyncEnabled,
                LanShopCode = LanShopCode,
                LanDeviceId = LanDeviceId,
                ShtrikhDirectLan = ShtrikhDirectLan,
                ScaleLanPort = ScaleLanPort,
                ScaleLanPasswordProtected = string.IsNullOrEmpty(ScaleLanPassword) ? null : WindowsDpapiHelper.ProtectToBase64(ScaleLanPassword), // 2026-11-06: В3 Б-2 — не открытым текстом
                BankQrPaths = BankQrPaths,
                BankLogoPaths = BankLogoPaths,
                CustomBankNames = CustomBankNames,
                VisibleBankNames = VisibleBankNames,
                // 2026-10-18, ревью безопасности: на диск — очищенная копия (коды только хэшем, без паролей);
                // живые объекты не меняем — их правит карточка сотрудника на UI-потоке.
                EmployeeAccessCodes = EmployeeAccessGate.ToDiskCopy(EmployeeAccessCodes),
                EmployeeAccessCodesVersion = EmployeeAccessCodesVersion,
                EmployeeAccessCodesCompanyId = EmployeeAccessCodesCompanyId,
                DebtPaymentOptions = DebtPaymentOptions,
                DebtPaymentOptionsCompanyId = DebtPaymentOptionsCompanyId,
                LoyaltyEnabled = LoyaltyEnabled,
                ShowQuickProducts = ShowQuickProducts,
                UpsellEnabled = UpsellEnabled,
                ScaleBrand = ScaleBrand,
                ScaleNetworkIp = ScaleNetworkIp,
                RongtaDataSource = RongtaDataSource,
                RongtaServerPort = RongtaServerPort,
                RongtaScaleIp = RongtaScaleIp,
                RongtaScalePort = RongtaScalePort,
                TmScaleIp = TmScaleIp,
                TmScaleBarcodeFormat = TmScaleBarcodeFormat,
                TmScalePort = TmScalePort,
                TmScalePriceDecimals = TmScalePriceDecimals,
                TmScalePricePoint = TmScalePricePoint,
                TmScaleBarcodePrefix = TmScaleBarcodePrefix,
                TmScaleDahuaBarcode = TmScaleDahuaBarcode,
                ShtrikhPcConfig = ShtrikhPcConfig,
                ShtrikhPcPasswordProtected = string.IsNullOrEmpty(ShtrikhPcPassword) ? null : WindowsDpapiHelper.ProtectToBase64(ShtrikhPcPassword),
                TmScaleSendMode = TmScaleSendMode,
                TmScaleShelfLifeDays = TmScaleShelfLifeDays,
                RongtaLanBarcodePrefix = RongtaLanBarcodePrefix,
                RongtaLanShelfLifeDays = RongtaLanShelfLifeDays,
                RongtaPricePoint = RongtaPricePoint,
                RongtaBarcodeType = RongtaBarcodeType,
                ScaleKeyCount = ScaleKeyCount,
                LoyaltyEarnPercent = LoyaltyEarnPercent,
                QuickCheckoutEnabled = QuickCheckoutEnabled,
                AcceptLateEnabled = AcceptLateEnabled,
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
                OwnerSidebarCollapsed = OwnerSidebarCollapsed,
                OwnerNavCollapsedGroups = OwnerNavCollapsedGroups,
                OwnerMenuStyle = OwnerMenuStyle,
                KassaMenuStyle = KassaMenuStyle,
                KassaMenuCollapsedGroups = KassaMenuCollapsedGroups,
                SalaryPayoutCashboxId = SalaryPayoutCashboxId,
                SalaryCategoryIds = SalaryCategoryIds,
                AiMinMarkupPercent = AiMinMarkupPercent,
                LastFilterSearchQuery = LastFilterSearchQuery,
                LastFilterCatalogKind = LastFilterCatalogKind,
                LastFilterPriceMin = LastFilterPriceMin,
                LastFilterPriceMax = LastFilterPriceMax,
                GraphicReceiptEnabled = GraphicReceiptEnabled,
                QrCodePath = QrCodePath,
                GraphicPaperWidthPixels = GraphicPaperWidthPixels,
                ReceiptCharWidth58 = ReceiptCharWidth58 > 0 ? ReceiptCharWidth58 : null,
                RentalLatePenaltyPerDay = RentalLatePenaltyPerDay > 0 ? RentalLatePenaltyPerDay : null,
                DebtLatePenaltyText = string.IsNullOrWhiteSpace(DebtLatePenaltyText) ? null : DebtLatePenaltyText.Trim(),
                OfflineDebtMaxPerClient = OfflineDebtMaxPerClient > 0 ? OfflineDebtMaxPerClient : null,
                ExchangeDaysLimit = ExchangeDaysLimit,
                NonExchangeableCategories = NonExchangeableCategories,
                ReceiptCharWidth80 = ReceiptCharWidth80 > 0 ? ReceiptCharWidth80 : null,
                ReceiptDots58 = ReceiptDots58 > 0 ? ReceiptDots58 : null,
                ReceiptDots80 = ReceiptDots80 > 0 ? ReceiptDots80 : null,
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
                CompactLayoutMode = CompactLayoutMode,
                CatalogTileScalePercent = CatalogTileScalePercent,
                WarehouseProductSort = WarehouseProductSort,
                MarketSphere = MarketSphere,
                MarketSphereDetail = MarketSphereDetail,
                MarketSphereServerSeen = MarketSphereServerSeen,
                MarketSphereChoicePending = MarketSphereChoicePending ? true : null,
                SupportSphereAppliedStamp = SupportSphereAppliedStamp, // 2026-11-08: MarketSphereLock
                UpdateTesterCode = UpdateTesterCode,
                CustomerDisplay = CustomerDisplay,
            };
            // 2026-10-06, лог (11:32, IOException): файл на миг занят другой записью (касса и программа владельца, антивирус) —
            // настройка терялась. Повтор с короткой паузой.
            // 2026-10-31 (приёмка 1.17.68, В-2): запись — через AtomicFile (Flush на диск, File.Replace, копия .bak). Программа владельца раз в
            // минуту читает этот же файл (File.ReadAllText): пока он открыт, подмена файла даёт IOException или UnauthorizedAccessException
            // (раньше ловился только IOException — настройка молча терялась). Теперь до 5 попыток по 50–100 мс, потом WARNING в журнал.
            var json = JsonSerializer.Serialize(dto, JsonOpt);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    AtomicFile.WriteAllText(FilePath, json);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 5)
                    {
                        PosLogger.Log($"User preferences save failed after {attempt} attempts: {ex.GetType().Name} ({Path.GetFileName(FilePath)} занят другой программой).", "WARNING");
                        throw;
                    }
                    Thread.Sleep(50 * attempt);
                }
            }
            if (saveLockHeld)
            {
                saveLockHeld = false;
                System.Threading.Monitor.Exit(SaveFileSync);
            }
            // 2026-10-05, владелец: «при открытии на другом устройстве все настройки аккаунта подтягивались» — на сервер.
            SettingsCloudSync.OnSaved();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"User preferences save failed: {ex.GetType().Name}", "WARNING");
        }
        finally
        {
            if (saveLockHeld)
                System.Threading.Monitor.Exit(SaveFileSync);
        }
    }

    /// <summary>2026-10-31 (приёмка 1.17.68, В-2): общая блокировка снимка и записи user-settings.json.</summary>
    private static readonly object SaveFileSync = new();

    /// <summary>Настройки дополнительных весов (2 или 3): свой порт и скорость, остальное — как у
    /// основных весов.</summary>
    public ScaleSettings ToExtraScaleSettings(int number) =>
        new()
        {
            Enabled = number == 2 ? Scale2Enabled : Scale3Enabled,
            ComPort = HardwarePortHelper.NormalizeComPort(number == 2 ? Scale2ComPort : Scale3ComPort, ""),
            BaudRate = number == 2 ? Scale2BaudRate : Scale3BaudRate,
            RequestHex = ScaleRequestHex,
            PollMs = ScalePollMs,
        };

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
        public bool? Scale2Enabled { get; set; }
        public string? Scale2ComPort { get; set; }
        public int? Scale2BaudRate { get; set; }
        public bool? Scale3Enabled { get; set; }
        public string? Scale3ComPort { get; set; }
        public int? Scale3BaudRate { get; set; }
        public string? PoleDisplayComPort { get; set; }
        public int? PoleDisplayBaudRate { get; set; }
        public bool? PoleDisplayEnabled { get; set; }
        public string? PoleDisplayProtocol { get; set; }
        public string? ScaleRequestHex { get; set; }
        public int? ScalePollMs { get; set; }
        public string? ReceiptDevicePath { get; set; }
        public string? LabelPrinterDevicePath { get; set; }
        public string? KnownNetworkPrinters { get; set; }
        public bool? LabelPageFromTemplate { get; set; }
        public bool? TransferAutoWaybill { get; set; }
        public bool? TransferAutoOther { get; set; }
        public bool? AskQtyOnRepeatScan { get; set; }
        public double? LabelGapMm { get; set; }
        public double? LabelOffsetXMm { get; set; }
        public double? LabelOffsetYMm { get; set; }
        public string? LabelRawLanguage { get; set; }
        public int? LabelTsplDensity { get; set; }
        public int? LabelTsplSpeed { get; set; }
        public bool? LabelTsplFlip { get; set; }
        public string? LabelTsplSensor { get; set; }
        public string? ReceiptEncoding { get; set; }
        public int? ReceiptEscPosTable { get; set; }
        public int? ReceiptEscR { get; set; }
        public bool? ReceiptEnabled { get; set; }
        public bool? CheckoutPrintReceipt { get; set; }
        public bool? CheckoutCardPay { get; set; }
        public bool? CashDrawerEnabled { get; set; }
        public int? CashDrawerPin { get; set; }
        public int? ReceiptPaperWidthMm { get; set; }
        public int? ReceiptRetryCount { get; set; }
        public bool? Fullscreen { get; set; }
        public bool? TrueFullscreen { get; set; }
        public bool? LowPerformanceMode { get; set; }
        public bool? ModalBackgroundBlur { get; set; }
        public string? WindowsRenderMode { get; set; }
        public bool? AskOwnerPinOnKassaPc { get; set; }
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
        public List<string>? UnlockedPacks { get; set; }
        public List<string>? HiddenSections { get; set; }
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
        public string? MarketplaceWebUrl { get; set; } // 2026-11-06
        public bool? AutoInstallPurchasedThemes { get; set; }
        public AppLanguage? Language { get; set; }
        public AppLanguage? AiLanguageOverride { get; set; } // 2026-11-05: язык ИИ по команде пользователя (null — как интерфейс)
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
        public string? ScaleAmountPrefixes { get; set; }
        public string? ScaleWeightPrefixes { get; set; }
        public string? ScaleBarcodeAmountUnit { get; set; }
        public string? ScaleBarcodeAmountUnitLocal { get; set; }
        public double? ScaleMaxLabelAmountSom { get; set; }
        public bool? ToolsPanelExpanded { get; set; }
        public bool? DynamicPaymentQrEnabled { get; set; }
        public bool? BlindCashCount { get; set; }
        public string? BlindCountFirstAttempt { get; set; }
        public bool? AskCartRemovalReason { get; set; }
        public double? CartRemovalReasonThreshold { get; set; }
        public string? TelegramBotTokenProtected { get; set; }
        public string? TelegramChatId { get; set; }
        public string? TelegramChatTitle { get; set; }
        // 2026-10-24 (Г1): группа заказов Telegram-бота.
        public string? TelegramOrdersChatId { get; set; }
        public string? TelegramOrdersChatTitle { get; set; }
        public string? TelegramOrdersTarget { get; set; }
        public bool? TelegramOrdersShowAmount { get; set; }
        public DateTime? TelegramOrdersBoundAtUtc { get; set; }
        public bool? TelegramOrdersBotRemoved { get; set; }
        public bool? AnalyticsExportUnlocked { get; set; }
        public bool? TelegramBotUnlocked { get; set; }
        public bool? ShiftAnalyticsUnlocked { get; set; }
        public bool? TelegramCommandsEnabled { get; set; }
        public bool? DebtPrepaymentChooseMethod { get; set; }
        public string? AdvisorCapabilitiesJson { get; set; }
        public string? PurchaseEditWindow { get; set; }
        public string? StaffAutoMarkMode { get; set; }
        public int? IdleLogoutMinutes { get; set; }
        public bool? DiscountModePercent { get; set; }
        public string? TelegramBotUsername { get; set; }
        public bool? TelegramShiftSummaryEnabled { get; set; }
        public string? OwnerPhone { get; set; }
        public string? TelegramAiKeyProtected { get; set; }
        public string? TelegramAiKeysExtraProtected { get; set; }
        public string? GroqApiKeyProtected { get; set; }
        public string? OpenRouterApiKeyProtected { get; set; }
        public bool? LanSyncEnabled { get; set; }
        public string? LanShopCode { get; set; }
        public string? LanDeviceId { get; set; }
        public bool? ShtrikhDirectLan { get; set; }
        public int? ScaleLanPort { get; set; }
        public string? ScaleLanPassword { get; set; }
        /// <summary>2026-11-06: В3 Б-2 — пароль весов, зашифрованный DPAPI / PortableSecret (ScaleLanPassword выше — только для чтения старых файлов).</summary>
        public string? ScaleLanPasswordProtected { get; set; }
        public Dictionary<string, string>? BankQrPaths { get; set; }
        public List<string>? VisibleBankNames { get; set; }
        public Dictionary<string, string>? BankLogoPaths { get; set; }
        public List<string>? CustomBankNames { get; set; }
        public List<EmployeeAccessCode>? EmployeeAccessCodes { get; set; }
        public long? EmployeeAccessCodesVersion { get; set; }
        public string? EmployeeAccessCodesCompanyId { get; set; }
        public DebtPaymentOptions? DebtPaymentOptions { get; set; }
        public string? DebtPaymentOptionsCompanyId { get; set; }
        public bool? LoyaltyEnabled { get; set; }
        public bool? ShowQuickProducts { get; set; }
        public bool? UpsellEnabled { get; set; }
        public string? ScaleBrand { get; set; }
        // 2026-09-28: сетевые весы (см. комментарий в LoadFromDiskAndMergeDefaults).
        public string? ScaleNetworkIp { get; set; }
        public string? RongtaDataSource { get; set; }
        public int? RongtaServerPort { get; set; }
        public string? RongtaScaleIp { get; set; }
        public int? RongtaScalePort { get; set; }
        public string? TmScaleIp { get; set; }
        public string? TmScaleBarcodeFormat { get; set; }
        public int? TmScalePort { get; set; }
        public int? TmScalePriceDecimals { get; set; }
        public int? TmScalePricePoint { get; set; }
        public int? TmScaleBarcodePrefix { get; set; }
        public string? TmScaleDahuaBarcode { get; set; }
        public string? ShtrikhPcConfig { get; set; }
        /// <summary>2026-11-06: пароль Windows весов РС-200 — только зашифрованный (DPAPI / PortableSecret).</summary>
        public string? ShtrikhPcPasswordProtected { get; set; }
        public string? TmScaleSendMode { get; set; }
        public int? TmScaleShelfLifeDays { get; set; }
        public int? RongtaLanBarcodePrefix { get; set; }
        public int? RongtaLanShelfLifeDays { get; set; }
        public int? RongtaPricePoint { get; set; }
        public int? RongtaBarcodeType { get; set; }
        public int? ScaleKeyCount { get; set; }
        public double? LoyaltyEarnPercent { get; set; }
        public bool? QuickCheckoutEnabled { get; set; }
        public bool? AcceptLateEnabled { get; set; }
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
        public bool? OwnerSidebarCollapsed { get; set; }
        public string? OwnerNavCollapsedGroups { get; set; }
        public string? OwnerMenuStyle { get; set; }
        public string? KassaMenuStyle { get; set; }
        public string? KassaMenuCollapsedGroups { get; set; }
        public string? SalaryPayoutCashboxId { get; set; }
        public string? SalaryCategoryIds { get; set; }
        public double? AiMinMarkupPercent { get; set; }
        public string? LastFilterSearchQuery { get; set; }
        public string? LastFilterCatalogKind { get; set; }
        public double? LastFilterPriceMin { get; set; }
        public double? LastFilterPriceMax { get; set; }
        public bool? GraphicReceiptEnabled { get; set; }
        public string? QrCodePath { get; set; }
        public int? GraphicPaperWidthPixels { get; set; }
        public int? ReceiptCharWidth58 { get; set; }
        public double? RentalLatePenaltyPerDay { get; set; }
        public string? DebtLatePenaltyText { get; set; }
        public double? OfflineDebtMaxPerClient { get; set; }
        public int? ExchangeDaysLimit { get; set; }
        public string? NonExchangeableCategories { get; set; }
        public int? ReceiptCharWidth80 { get; set; }
        public int? ReceiptDots58 { get; set; }
        public int? ReceiptDots80 { get; set; }
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
        public string? CompactLayoutMode { get; set; }
        public double? CatalogTileScalePercent { get; set; }
        public string? WarehouseProductSort { get; set; }
        public string? MarketSphere { get; set; }
        public string? MarketSphereDetail { get; set; }
        public string? MarketSphereServerSeen { get; set; }
        public bool? MarketSphereChoicePending { get; set; }
        public string? SupportSphereAppliedStamp { get; set; }
        public string? UpdateTesterCode { get; set; }
        public double? BackgroundOpacity { get; set; }
        public CustomerDisplaySettings? CustomerDisplay { get; set; }
    }

    private sealed class PublishDefaults
    {
        public int Version { get; set; }
        public bool Fullscreen { get; set; }
    }
}
