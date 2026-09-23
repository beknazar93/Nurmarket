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

    // Принтер – общие настройки
    public string ReceiptDevicePath { get; set; } = "LPT1";
    public bool ReceiptEnabled { get; set; }
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
    public int AppliedPublishDefaultsVersion { get; set; }
    public bool DarkTheme { get; set; } = true;
    public Dictionary<string, string> BankQrPaths { get; set; } = new();
    public Dictionary<string, string> BankLogoPaths { get; set; } = new();
    public Dictionary<string, string> PosHotkeys { get; set; } = new();
    public bool Autostart { get; set; }
    public bool AutoShowTouchKeyboard { get; set; } = true;
    public CatalogViewMode CatalogViewMode { get; set; } = CatalogViewMode.Cards;
    public bool SingleClickToCart { get; set; }
    public bool ResetManualAddQtyAfterAdd { get; set; } = true;
    public bool ToolsPanelExpanded { get; set; }
    /// <summary>Абсолютный путь к пользовательским обоям правой панели настроек.</summary>
    public string BackgroundImagePath { get; set; } = "";
    /// <summary>Прозрачность подложки настроек (0.05–0.8).</summary>
    public double BackgroundOpacity { get; set; } = 0.15;
    public string LastLoginEmail { get; set; } = "";
    public string LastLoginPassword { get; set; } = "";
    /// <summary>Ключ владельца локального каталога (email|userId) для изоляции при смене аккаунта.</summary>
    public string LastCatalogUserKey { get; set; } = "";
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

    public CustomerDisplaySettings CustomerDisplay { get; set; } = new();

    public string StoreAddress { get; set; } = "";

    public string StoreInn { get; set; } = "";

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
            p.ScaleRequestHex = fromFile.ScaleRequestHex ?? p.ScaleRequestHex;
            if (fromFile.ScalePollMs is >= 0)
                p.ScalePollMs = fromFile.ScalePollMs.Value;

            // Принтер – общие
            if (!string.IsNullOrWhiteSpace(fromFile.ReceiptDevicePath))
                p.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(fromFile.ReceiptDevicePath, p.ReceiptDevicePath);
            if (fromFile.ReceiptEnabled is not null)
                p.ReceiptEnabled = fromFile.ReceiptEnabled.Value;
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
            if (fromFile.AppliedPublishDefaultsVersion is > 0)
                p.AppliedPublishDefaultsVersion = fromFile.AppliedPublishDefaultsVersion.Value;
            if (fromFile.DarkTheme is not null)
                p.DarkTheme = fromFile.DarkTheme.Value;
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
            if (!string.IsNullOrWhiteSpace(fromFile.PostgreSqlConnectionStringEncrypted))
                p.PostgreSqlConnectionStringEncrypted = fromFile.PostgreSqlConnectionStringEncrypted;
            if (!string.IsNullOrEmpty(fromFile.LastFilterCategory))
                p.LastFilterCategory = fromFile.LastFilterCategory;
            if (!string.IsNullOrEmpty(fromFile.LastFilterBrand))
                p.LastFilterBrand = fromFile.LastFilterBrand;
            if (fromFile.CatalogViewMode is not null)
                p.CatalogViewMode = fromFile.CatalogViewMode.Value;
            if (fromFile.SingleClickToCart is not null)
                p.SingleClickToCart = fromFile.SingleClickToCart.Value;
            if (fromFile.ResetManualAddQtyAfterAdd is not null)
                p.ResetManualAddQtyAfterAdd = fromFile.ResetManualAddQtyAfterAdd.Value;
            if (fromFile.ToolsPanelExpanded is not null)
                p.ToolsPanelExpanded = fromFile.ToolsPanelExpanded.Value;
            if (fromFile.BankQrPaths != null)
                p.BankQrPaths = fromFile.BankQrPaths;
            if (fromFile.BankLogoPaths != null)
                p.BankLogoPaths = fromFile.BankLogoPaths;
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
                ScaleRequestHex = ScaleRequestHex,
                ScalePollMs = ScalePollMs,
                ReceiptDevicePath = ReceiptDevicePath,
                ReceiptEncoding = ReceiptEncoding,
                ReceiptEscPosTable = ReceiptEscPosTable,
                ReceiptEscR = ReceiptEscR,
                ReceiptEnabled = ReceiptEnabled,
                ReceiptPaperWidthMm = ReceiptPaperWidthMm,
                ReceiptRetryCount = ReceiptRetryCount,
                Fullscreen = Fullscreen,
                AppliedPublishDefaultsVersion = AppliedPublishDefaultsVersion,
                DarkTheme = DarkTheme,
                Autostart = Autostart,
                AutoShowTouchKeyboard = AutoShowTouchKeyboard,
                LastLoginEmail = null,
                LastCatalogUserKey = LastCatalogUserKey,
                LastLoginPassword = null,
                LastLoginPasswordEncrypted = null,
                PostgreSqlConnectionStringEncrypted = PostgreSqlConnectionStringEncrypted,
                LastFilterCategory = LastFilterCategory,
                LastFilterBrand = LastFilterBrand,
                CatalogViewMode = CatalogViewMode,
                SingleClickToCart = SingleClickToCart,
                ResetManualAddQtyAfterAdd = ResetManualAddQtyAfterAdd,
                ToolsPanelExpanded = ToolsPanelExpanded,
                BankQrPaths = BankQrPaths,
                BankLogoPaths = BankLogoPaths,
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
        public string? ScaleRequestHex { get; set; }
        public int? ScalePollMs { get; set; }
        public string? ReceiptDevicePath { get; set; }
        public string? ReceiptEncoding { get; set; }
        public int? ReceiptEscPosTable { get; set; }
        public int? ReceiptEscR { get; set; }
        public bool? ReceiptEnabled { get; set; }
        public int? ReceiptPaperWidthMm { get; set; }
        public int? ReceiptRetryCount { get; set; }
        public bool? Fullscreen { get; set; }
        public int? AppliedPublishDefaultsVersion { get; set; }
        public bool? DarkTheme { get; set; }
        public bool? Autostart { get; set; }
        public bool? AutoShowTouchKeyboard { get; set; }
        public string? LastLoginEmail { get; set; }
        public string? LastCatalogUserKey { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastLoginPassword { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastLoginPasswordEncrypted { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PostgreSqlConnectionStringEncrypted { get; set; }
        public string? LastFilterCategory { get; set; }
        public string? LastFilterBrand { get; set; }
        public CatalogViewMode? CatalogViewMode { get; set; }
        public bool? SingleClickToCart { get; set; }
        public bool? ResetManualAddQtyAfterAdd { get; set; }
        public bool? ToolsPanelExpanded { get; set; }
        public Dictionary<string, string>? BankQrPaths { get; set; }
        public Dictionary<string, string>? BankLogoPaths { get; set; }
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
        public double? BackgroundOpacity { get; set; }
        public CustomerDisplaySettings? CustomerDisplay { get; set; }
    }

    private sealed class PublishDefaults
    {
        public int Version { get; set; }
        public bool Fullscreen { get; set; }
    }
}
