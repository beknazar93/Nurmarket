﻿using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса UserPreferences (разбор огромного файла на partial-файлы), логика не менялась; остальное — UserPreferences.cs
public sealed partial class UserPreferences
{
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

        var securedEmployeeCodes = false;
        try
        {
            var settingsPath = FindExistingSettingsFile();
            if (settingsPath == null || !File.Exists(settingsPath))
                return;

            // 2026-11-01 (аудит 10 из 10, Н-05): битый user-settings.json читаем через AtomicFile — он сам берёт целую копию .bak
            // (пишется при каждом сохранении), восстанавливает рабочий файл и пишет WARNING. Раньше касса стартовала с настройками
            // по умолчанию, а ближайшее сохранение затирало файл. Нет ни файла, ни целой копии — null → значения по умолчанию.
            // 2026-11-01 (контрольный аудит, Н2-02): файл занят (антивирус, программа владельца) — 3 попытки с паузой 200 мс. Не вышло — настройки по умолчанию
            // ТОЛЬКО в памяти: сохранение отключено (иначе ближайшее сохранение затёрло бы рабочий файл), в журнал и тостом — предупреждение.
            string? json = null;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    json = AtomicFile.ReadAllText(settingsPath);
                    break;
                }
                catch (Exception readEx) when (readEx is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 3)
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                    _readBlockedPersistence = true;
                    PosLogger.Log($"User preferences: файл настроек занят и не прочитан за 3 попытки ({readEx.Message}) — работаем с настройками по умолчанию, запись настроек отключена до перезапуска.", "ERROR");
                    CatalogCacheService.RaiseToast(Tr.T(
                        "Файл настроек занят другой программой — настройки не загружены и не сохраняются. Закройте её и перезапустите кассу.",
                        "Жөндөөлөр файлы башка программа тарабынан ээленген — жөндөөлөр жүктөлгөн жок жана сакталбайт. Аны жаап, кассаны кайра иштетиңиз.",
                        "The settings file is locked by another program — settings are not loaded and will not be saved. Close it and restart the till.",
                        "Ayar dosyası başka bir program tarafından kullanılıyor — ayarlar yüklenmedi ve kaydedilmeyecek. Onu kapatıp kasayı yeniden başlatın.",
                        "Sozlamalar fayli boshqa dastur tomonidan band — sozlamalar yuklanmadi va saqlanmaydi. Uni yopib, kassani qayta ishga tushiring."), true);
                    return;
                }
            }
            if (json == null)
                return;
            var fromFile = JsonSerializer.Deserialize<UserPreferencesDto>(json, JsonOpt);
            if (fromFile == null)
                return;

            // Весы
            if (!string.IsNullOrWhiteSpace(fromFile.ScaleComPort))
                p.ScaleComPort = HardwarePortHelper.NormalizeComPort(fromFile.ScaleComPort, p.ScaleComPort);
            if (fromFile.ScaleBaudRate is > 0)
                p.ScaleBaudRate = fromFile.ScaleBaudRate.Value;
            p.ScaleEnabled = fromFile.ScaleEnabled ?? p.ScaleEnabled;
            p.Scale2Enabled = fromFile.Scale2Enabled ?? false;
            p.Scale2ComPort = fromFile.Scale2ComPort ?? "";
            if (fromFile.Scale2BaudRate is > 0)
                p.Scale2BaudRate = fromFile.Scale2BaudRate.Value;
            p.Scale3Enabled = fromFile.Scale3Enabled ?? false;
            p.Scale3ComPort = fromFile.Scale3ComPort ?? "";
            if (fromFile.Scale3BaudRate is > 0)
                p.Scale3BaudRate = fromFile.Scale3BaudRate.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.PoleDisplayComPort))
                p.PoleDisplayComPort = HardwarePortHelper.NormalizeComPort(fromFile.PoleDisplayComPort, p.PoleDisplayComPort);
            if (fromFile.PoleDisplayBaudRate is > 0)
                p.PoleDisplayBaudRate = fromFile.PoleDisplayBaudRate.Value;
            p.PoleDisplayEnabled = fromFile.PoleDisplayEnabled ?? p.PoleDisplayEnabled;
            if (fromFile.PoleDisplayProtocol is null)
            {
                // Настройки до 1.17.16: касса умела только текст CD5220 и писала в порт на 9600 при
                // любой скорости в поле, так что сохранённые 9600 — просто прежнее значение по
                // умолчанию. Цифровые табло (LED8N) по умолчанию работают на 2400.
                p.PoleDisplayProtocol = "led";
                if (p.PoleDisplayBaudRate == 9600)
                    p.PoleDisplayBaudRate = 2400;
            }
            else
            {
                p.PoleDisplayProtocol = fromFile.PoleDisplayProtocol;
            }
            p.ScaleRequestHex = fromFile.ScaleRequestHex ?? p.ScaleRequestHex;
            if (fromFile.ScalePollMs is >= 0)
                p.ScalePollMs = fromFile.ScalePollMs.Value;

            // Принтер – общие
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptDevicePath))
                p.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(fromFile.ReceiptDevicePath, p.ReceiptDevicePath);
            if (!string.IsNullOrWhiteSpace(fromFile.LabelPrinterDevicePath))
                p.LabelPrinterDevicePath = fromFile.LabelPrinterDevicePath;
            if (!string.IsNullOrWhiteSpace(fromFile.KnownNetworkPrinters))
                p.KnownNetworkPrinters = fromFile.KnownNetworkPrinters.Length > 2000 ? fromFile.KnownNetworkPrinters[..2000] : fromFile.KnownNetworkPrinters;
            if (fromFile.LabelPageFromTemplate is not null)
                p.LabelPageFromTemplate = fromFile.LabelPageFromTemplate.Value;
            if (fromFile.TransferAutoWaybill is not null)
                p.TransferAutoWaybill = fromFile.TransferAutoWaybill.Value; // 2026-10-31
            if (fromFile.AskQtyOnRepeatScan is not null)
                p.AskQtyOnRepeatScan = fromFile.AskQtyOnRepeatScan.Value; // 2026-11-09
            if (fromFile.TransferAutoOther is not null)
                p.TransferAutoOther = fromFile.TransferAutoOther.Value;
            if (fromFile.LabelGapMm is >= 0 and <= 20)
                p.LabelGapMm = fromFile.LabelGapMm.Value;
            if (fromFile.LabelOffsetXMm is >= -20 and <= 20)
                p.LabelOffsetXMm = fromFile.LabelOffsetXMm.Value;
            if (fromFile.LabelOffsetYMm is >= -20 and <= 20)
                p.LabelOffsetYMm = fromFile.LabelOffsetYMm.Value;
            if (fromFile.LabelRawLanguage is "escpos" or "escpos_gap" or "tspl")
                p.LabelRawLanguage = fromFile.LabelRawLanguage;
            if (fromFile.LabelTsplDensity is >= 0 and <= 15)
                p.LabelTsplDensity = fromFile.LabelTsplDensity; // 2026-11-01 (П-7)
            if (fromFile.LabelTsplSpeed is >= 2 and <= 6)
                p.LabelTsplSpeed = fromFile.LabelTsplSpeed;
            if (fromFile.LabelTsplFlip is not null)
                p.LabelTsplFlip = fromFile.LabelTsplFlip;
            if (fromFile.LabelTsplSensor is "gap" or "bline")
                p.LabelTsplSensor = fromFile.LabelTsplSensor;
            if (fromFile.ReceiptEnabled is not null)
                p.ReceiptEnabled = fromFile.ReceiptEnabled.Value;
            // 2026-09-29: нет в файле (кассир не трогал «Напечатать чек») — остаётся null, как раньше.
            if (fromFile.CheckoutPrintReceipt is not null)
                p.CheckoutPrintReceipt = fromFile.CheckoutPrintReceipt.Value;
            if (fromFile.CheckoutCardPay is not null)
                p.CheckoutCardPay = fromFile.CheckoutCardPay.Value; // 2026-11-06 (Д4-15)
            if (fromFile.CashDrawerEnabled is not null)
                p.CashDrawerEnabled = fromFile.CashDrawerEnabled.Value;
            if (fromFile.CashDrawerPin is 0 or 1)
                p.CashDrawerPin = fromFile.CashDrawerPin.Value;
            if (fromFile.ReceiptPaperWidthMm is > 0)
                p.ReceiptPaperWidthMm = ReceiptPaperProfile.NormalizePaperWidthMm(fromFile.ReceiptPaperWidthMm);
            else if (fromFile.GraphicPaperWidthPixels is >= 500)
                p.ReceiptPaperWidthMm = ReceiptPaperProfile.Paper80mm;
            p.ReceiptCharWidth58 = fromFile.ReceiptCharWidth58 ?? 0;
            p.RentalLatePenaltyPerDay = fromFile.RentalLatePenaltyPerDay is > 0 ? fromFile.RentalLatePenaltyPerDay.Value : 0;
            p.DebtLatePenaltyText = fromFile.DebtLatePenaltyText ?? "";
            p.OfflineDebtMaxPerClient = fromFile.OfflineDebtMaxPerClient is > 0 ? fromFile.OfflineDebtMaxPerClient.Value : 0;
            p.ExchangeDaysLimit = fromFile.ExchangeDaysLimit is >= 0 and <= 365 ? fromFile.ExchangeDaysLimit.Value : 14;
            p.NonExchangeableCategories = fromFile.NonExchangeableCategories;
            p.ReceiptCharWidth80 = fromFile.ReceiptCharWidth80 ?? 0;
            p.ReceiptDots58 = fromFile.ReceiptDots58 ?? 0;
            p.ReceiptDots80 = fromFile.ReceiptDots80 ?? 0;

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
            // 2026-11-11: размытие фона под модалками и режим отрисовки Windows — настройки этого ПК.
            if (fromFile.ModalBackgroundBlur is not null)
                p.ModalBackgroundBlur = fromFile.ModalBackgroundBlur.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.WindowsRenderMode))
                p.WindowsRenderMode = fromFile.WindowsRenderMode;
            if (fromFile.AskOwnerPinOnKassaPc is not null)
                p.AskOwnerPinOnKassaPc = fromFile.AskOwnerPinOnKassaPc.Value; // 2026-11-06 (Л4-19)
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
            if (fromFile.UnlockedPacks is { Count: > 0 })
                p.UnlockedPacks = fromFile.UnlockedPacks;
            if (fromFile.HiddenSections is { Count: > 0 })
                p.HiddenSections = fromFile.HiddenSections;
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
            if (!string.IsNullOrWhiteSpace(fromFile.MarketplaceWebUrl))
                p.MarketplaceWebUrl = fromFile.MarketplaceWebUrl.Trim(); // 2026-11-06: адрес Маркетплейса на сайте
            if (fromFile.AutoInstallPurchasedThemes is not null)
                p.AutoInstallPurchasedThemes = fromFile.AutoInstallPurchasedThemes.Value;
            if (fromFile.Language is not null)
                p.Language = fromFile.Language.Value;
            p.AiLanguageOverride = fromFile.AiLanguageOverride; // 2026-11-05
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
            if (fromFile.ScaleAmountPrefixes is not null)
                p.ScaleAmountPrefixes = fromFile.ScaleAmountPrefixes;
            if (fromFile.ScaleWeightPrefixes is not null)
                p.ScaleWeightPrefixes = fromFile.ScaleWeightPrefixes;
            if (fromFile.ScaleBarcodeAmountUnit is not null)
                p.ScaleBarcodeAmountUnit = fromFile.ScaleBarcodeAmountUnit;
            // 2026-11-05: локальная единица суммы этикетки и предел суммы (хотфикс TM-30F, см. WeightBarcodeParser.AmountUnit).
            if (fromFile.ScaleBarcodeAmountUnitLocal is not null)
                p.ScaleBarcodeAmountUnitLocal = fromFile.ScaleBarcodeAmountUnitLocal;
            if (fromFile.ScaleMaxLabelAmountSom is > 0)
                p.ScaleMaxLabelAmountSom = fromFile.ScaleMaxLabelAmountSom.Value;
            if (fromFile.ToolsPanelExpanded is not null)
                p.ToolsPanelExpanded = fromFile.ToolsPanelExpanded.Value;
            if (fromFile.DynamicPaymentQrEnabled is not null)
                p.DynamicPaymentQrEnabled = fromFile.DynamicPaymentQrEnabled.Value;
            if (fromFile.BlindCashCount is not null)
                p.BlindCashCount = fromFile.BlindCashCount.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.BlindCountFirstAttempt))
                p.BlindCountFirstAttempt = fromFile.BlindCountFirstAttempt;
            if (fromFile.AskCartRemovalReason is not null)
                p.AskCartRemovalReason = fromFile.AskCartRemovalReason.Value;
            if (fromFile.CartRemovalReasonThreshold is { } removalThreshold && removalThreshold >= 0)
                p.CartRemovalReasonThreshold = removalThreshold;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramBotTokenProtected))
                p.TelegramBotToken = WindowsDpapiHelper.UnprotectFromBase64(fromFile.TelegramBotTokenProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramChatId))
                p.TelegramChatId = fromFile.TelegramChatId;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramChatTitle))
                p.TelegramChatTitle = fromFile.TelegramChatTitle;
            // 2026-10-24 (Г1): группа заказов (пусто в файле = не подключена, значения по умолчанию остаются).
            p.TelegramOrdersChatId = string.IsNullOrWhiteSpace(fromFile.TelegramOrdersChatId) ? null : fromFile.TelegramOrdersChatId;
            p.TelegramOrdersChatTitle = string.IsNullOrWhiteSpace(fromFile.TelegramOrdersChatTitle) ? null : fromFile.TelegramOrdersChatTitle;
            if (fromFile.TelegramOrdersTarget is "owner" or "group" or "both")
                p.TelegramOrdersTarget = fromFile.TelegramOrdersTarget;
            if (fromFile.TelegramOrdersShowAmount is not null)
                p.TelegramOrdersShowAmount = fromFile.TelegramOrdersShowAmount.Value;
            p.TelegramOrdersBoundAtUtc = fromFile.TelegramOrdersBoundAtUtc;
            if (fromFile.TelegramOrdersBotRemoved is not null)
                p.TelegramOrdersBotRemoved = fromFile.TelegramOrdersBotRemoved.Value;
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
            if (fromFile.DebtPrepaymentChooseMethod is not null)
                p.DebtPrepaymentChooseMethod = fromFile.DebtPrepaymentChooseMethod.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.AdvisorCapabilitiesJson))
                p.AdvisorCapabilitiesJson = fromFile.AdvisorCapabilitiesJson;
            if (!string.IsNullOrWhiteSpace(fromFile.PurchaseEditWindow))
                p.PurchaseEditWindow = fromFile.PurchaseEditWindow;
            if (!string.IsNullOrWhiteSpace(fromFile.StaffAutoMarkMode))
                p.StaffAutoMarkMode = fromFile.StaffAutoMarkMode;
            if (fromFile.IdleLogoutMinutes is not null)
                p.IdleLogoutMinutes = fromFile.IdleLogoutMinutes; // 2026-11-08
            if (fromFile.DiscountModePercent is not null)
                p.DiscountModePercent = fromFile.DiscountModePercent.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramBotUsername))
                p.TelegramBotUsername = fromFile.TelegramBotUsername;
            if (!string.IsNullOrWhiteSpace(fromFile.OwnerPhone))
                p.OwnerPhone = fromFile.OwnerPhone;
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramAiKeyProtected))
                p.TelegramAiKey = WindowsDpapiHelper.UnprotectFromBase64(fromFile.TelegramAiKeyProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.TelegramAiKeysExtraProtected))
                p.TelegramAiKeysExtra = WindowsDpapiHelper.UnprotectFromBase64(fromFile.TelegramAiKeysExtraProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.GroqApiKeyProtected))
                p.GroqApiKey = WindowsDpapiHelper.UnprotectFromBase64(fromFile.GroqApiKeyProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.OpenRouterApiKeyProtected))
                p.OpenRouterApiKey = WindowsDpapiHelper.UnprotectFromBase64(fromFile.OpenRouterApiKeyProtected);
            if (fromFile.LanSyncEnabled is not null)
                p.LanSyncEnabled = fromFile.LanSyncEnabled.Value;
            if (fromFile.LanShopCode is not null)
                p.LanShopCode = fromFile.LanShopCode;
            if (!string.IsNullOrWhiteSpace(fromFile.LanDeviceId))
                p.LanDeviceId = fromFile.LanDeviceId;
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
            // 2026-11-06: В3 Б-2 — пароль весов хранится зашифрованным (ScaleLanPasswordProtected); открытое поле ScaleLanPassword читается только из файлов старше этой правки
            // (при следующем сохранении оно заменится зашифрованным).
            var scalePasswordProtected = WindowsDpapiHelper.UnprotectFromBase64(fromFile.ScaleLanPasswordProtected);
            if (!string.IsNullOrWhiteSpace(scalePasswordProtected))
                p.ScaleLanPassword = scalePasswordProtected;
            else if (!string.IsNullOrWhiteSpace(fromFile.ScaleLanPassword) && !untouchedGuess)
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
            {
                p.EmployeeAccessCodes = fromFile.EmployeeAccessCodes;
                // 2026-10-18: открытые коды и пароли прежних версий — в хэш / стереть; файл перепишется в конце загрузки.
                securedEmployeeCodes = EmployeeAccessGate.SecureStoredCodes(p.EmployeeAccessCodes);
                p.EmployeeAccessCodesVersion = fromFile.EmployeeAccessCodesVersion ?? 0;
                p.EmployeeAccessCodesCompanyId = fromFile.EmployeeAccessCodesCompanyId;
            }
            // 2026-10-18: общие настройки «Оплата долга» (последние с сервера).
            p.DebtPaymentOptions = fromFile.DebtPaymentOptions;
            p.DebtPaymentOptionsCompanyId = fromFile.DebtPaymentOptionsCompanyId;
            if (fromFile.LoyaltyEnabled is not null)
                p.LoyaltyEnabled = fromFile.LoyaltyEnabled.Value;
            if (fromFile.ShowQuickProducts is not null)
                p.ShowQuickProducts = fromFile.ShowQuickProducts.Value;
            if (fromFile.UpsellEnabled is not null)
                p.UpsellEnabled = fromFile.UpsellEnabled.Value;
            // 2026-09-28: марка весов раньше не писалась в файл — после перезапуска окно «Весы»
            // снова открывалось на «Штрих-М», какую бы модель ни выбрали.
            if (!string.IsNullOrWhiteSpace(fromFile.ScaleBrand))
                p.ScaleBrand = fromFile.ScaleBrand;
            // 2026-09-28: IP сетевых весов и настройки Rongta тоже не писались в файл — после
            // перезапуска адрес весов Штрих-М для прямой выгрузки пропадал, а Rongta
            // возвращалась к «с сайта» и порту 5001.
            if (fromFile.ScaleNetworkIp is not null)
                p.ScaleNetworkIp = fromFile.ScaleNetworkIp;
            if (!string.IsNullOrWhiteSpace(fromFile.RongtaDataSource))
                p.RongtaDataSource = fromFile.RongtaDataSource;
            if (fromFile.RongtaServerPort is > 0 and <= 65535)
                p.RongtaServerPort = fromFile.RongtaServerPort.Value;
            if (fromFile.RongtaScaleIp is not null)
                p.RongtaScaleIp = fromFile.RongtaScaleIp;
            if (fromFile.RongtaScalePort is > 0 and <= 65535)
                p.RongtaScalePort = fromFile.RongtaScalePort.Value;
            if (fromFile.TmScaleIp is not null)
                p.TmScaleIp = fromFile.TmScaleIp;
            if (!string.IsNullOrWhiteSpace(fromFile.TmScaleBarcodeFormat))
                p.TmScaleBarcodeFormat = fromFile.TmScaleBarcodeFormat;
            // 2026-09-28: прямая выгрузка на TM-30F (Dahua).
            if (fromFile.TmScalePort is > 0 and <= 65535)
                p.TmScalePort = fromFile.TmScalePort.Value;
            if (fromFile.TmScalePriceDecimals is >= 0 and <= 3)
                p.TmScalePriceDecimals = fromFile.TmScalePriceDecimals.Value;
            if (fromFile.TmScalePricePoint is >= 0 and <= 3)
                p.TmScalePricePoint = fromFile.TmScalePricePoint.Value;
            if (fromFile.TmScaleBarcodePrefix is >= 0 and <= 99)
                p.TmScaleBarcodePrefix = fromFile.TmScaleBarcodePrefix.Value;
            if (!string.IsNullOrWhiteSpace(fromFile.ShtrikhPcConfig))
                p.ShtrikhPcConfig = fromFile.ShtrikhPcConfig; // 2026-11-06: весы Штрих РС-200
            p.ShtrikhPcPassword = WindowsDpapiHelper.UnprotectFromBase64(fromFile.ShtrikhPcPasswordProtected);
            if (!string.IsNullOrWhiteSpace(fromFile.TmScaleDahuaBarcode))
                p.TmScaleDahuaBarcode = fromFile.TmScaleDahuaBarcode;
            if (!string.IsNullOrWhiteSpace(fromFile.TmScaleSendMode))
                p.TmScaleSendMode = fromFile.TmScaleSendMode;
            if (fromFile.TmScaleShelfLifeDays is >= 0 and <= 999)
                p.TmScaleShelfLifeDays = fromFile.TmScaleShelfLifeDays.Value;
            if (fromFile.RongtaLanBarcodePrefix is >= 0 and <= 99)
                p.RongtaLanBarcodePrefix = fromFile.RongtaLanBarcodePrefix.Value;
            if (fromFile.RongtaLanShelfLifeDays is >= 0 and <= 999)
                p.RongtaLanShelfLifeDays = fromFile.RongtaLanShelfLifeDays.Value;
            if (fromFile.RongtaPricePoint is >= 0 and <= 3)
                p.RongtaPricePoint = fromFile.RongtaPricePoint.Value;
            if (fromFile.RongtaBarcodeType is >= 0 and <= 99)
                p.RongtaBarcodeType = fromFile.RongtaBarcodeType.Value;
            if (fromFile.ScaleKeyCount is >= 1 and <= 240)
                p.ScaleKeyCount = fromFile.ScaleKeyCount.Value;
            if (fromFile.LoyaltyEarnPercent is not null)
                p.LoyaltyEarnPercent = Math.Clamp(fromFile.LoyaltyEarnPercent.Value, 0, 100);
            // 2026-09-28, BE-11: нет в файле (старые настройки) — остаётся значение по умолчанию.
            if (fromFile.QuickCheckoutEnabled is { } quickCheckout)
                p.QuickCheckoutEnabled = quickCheckout;
            if (fromFile.AcceptLateEnabled is { } acceptLate)
                p.AcceptLateEnabled = acceptLate; // 2026-11-01: нет в файле — выключено
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
            if (fromFile.OwnerSidebarCollapsed.HasValue) p.OwnerSidebarCollapsed = fromFile.OwnerSidebarCollapsed.Value;
            p.OwnerNavCollapsedGroups = fromFile.OwnerNavCollapsedGroups;
            p.OwnerMenuStyle = fromFile.OwnerMenuStyle;
            p.KassaMenuStyle = fromFile.KassaMenuStyle;
            p.KassaMenuCollapsedGroups = fromFile.KassaMenuCollapsedGroups;
            p.SalaryPayoutCashboxId = fromFile.SalaryPayoutCashboxId;
            p.SalaryCategoryIds = fromFile.SalaryCategoryIds;
            if (fromFile.AiMinMarkupPercent is > 0) p.AiMinMarkupPercent = fromFile.AiMinMarkupPercent.Value;
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
            if (fromFile.CompactLayoutMode is "auto" or "on" or "off")
                p.CompactLayoutMode = fromFile.CompactLayoutMode;
            if (fromFile.UpdateTesterCode is not null)
                p.UpdateTesterCode = fromFile.UpdateTesterCode;
            if (fromFile.CatalogTileScalePercent is not null)
                p.CatalogTileScalePercent = Math.Clamp(fromFile.CatalogTileScalePercent.Value, 70, 160);
            if (fromFile.WarehouseProductSort is not null)
                p.WarehouseProductSort = fromFile.WarehouseProductSort;
            if (fromFile.MarketSphere is not null)
            {
                p.MarketSphere = MarketSpheres.Normalize(fromFile.MarketSphere);
                p.MarketSphereWasInFile = true;
            }
            p.MarketSphereDetail = SphereCatalog.SanitizeDetail(fromFile.MarketSphereDetail); // 2026-10-26: ХМ1
            p.MarketSphereServerSeen = fromFile.MarketSphereServerSeen;
            p.MarketSphereChoicePending = fromFile.MarketSphereChoicePending ?? false;
            p.SupportSphereAppliedStamp = fromFile.SupportSphereAppliedStamp; // 2026-11-08: назначение вида техподдержкой (MarketSphereLock)
            if (fromFile.CustomerDisplay is not null)
            {
                fromFile.CustomerDisplay.Normalize();
                p.CustomerDisplay = fromFile.CustomerDisplay;
            }

            // 2026-10-18: коды сотрудников переведены в хэш — сразу убрать открытые коды/пароли из файла.
            if (securedEmployeeCodes)
                p.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"User preferences load failed: {ex.GetType().Name}", "WARNING");
            // 2026-10-30 (стресс-тест «Управление»): файл испорчен (обрезан, не JSON) — копия рядом (.bad): ближайшее сохранение настроек
            // по умолчанию иначе стёрло бы его безвозвратно (у файла тем такая защита уже есть).
            if (ex is JsonException)
                try { if (FindExistingSettingsFile() is { } badPath) File.Copy(badPath, badPath + ".bad", overwrite: true); } catch { }
        }
        finally
        {
            ApplyPublishedDefaults(p);
        }

        // 2026-10-18, ревью безопасности: старые файлы настроек на этом ПК (NurCrmKassa; файл кассы, который читает
        // программа владельца, и наоборот) — без открытых кодов и паролей сотрудников. Свой файл чистит SaveToDisk.
        var appData = NurMarketKassa.Services.AppPaths.Roaming;
        foreach (var folder in new[] { "NurMarketKassa", "NurMarketOwner", "NurCrmKassa" })
        {
            var path = Path.Combine(appData, folder, "user-settings.json");
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
                EmployeeAccessGate.ScrubSettingsFile(path, hashCodes: folder != "NurCrmKassa"); // 2026-10-18: старая программа хэши не понимает
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
}
