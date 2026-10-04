using Android;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-05, владелец: «изучи параметры Android POS-касс … чековый принтер — сделай так, чтобы наша
/// программа 100% работала на этих устройствах». Встроенный принтер Android POS-терминала:
/// • Sunmi (V1s/V2/V2s/V2 Pro/V3/P2 — ручные 58 мм; T1/T2/T2s/T2 mini/D3/K2 — настольные 80 мм) — виртуальный
///   Bluetooth «InnerPrinter», адрес 00:11:22:33:44:55, команды ESC/POS; кириллица — CP866 или UTF-8
///   (FS &amp; + FS C 0xFF, документация «SUNMI Inbuilt Printer Developer Documentation»);
/// • iMin (Swift/Falcon/Swan/D4…) — Bluetooth «InnerPrinter» (SDK 2.0) или «BluetoothPrinter» (SDK 1.0), ESC/POS;
/// • китайские аналоги часто повторяют Sunmi: тот же «InnerPrinter».
/// Касса на таком аппарате при первом запуске сама выбирает встроенный принтер, если владелец ещё ничего не
/// настраивал (в настройках стоит порт Windows — LPT/COM — или пусто). Выбор владельца не трогается.</summary>
internal static class AndroidBuiltInPrinter
{
    internal const string SunmiInnerPrinterAddress = "00:11:22:33:44:55";

    private static readonly string[] BuiltInNames = { "InnerPrinter", "BluetoothPrinter", "IposPrinter", "Printer001" };

    public static void ApplyDefaults(Context context)
    {
        try
        {
            var maker = (Build.Manufacturer ?? "").Trim();
            var model = (Build.Model ?? "").Trim();
            var sunmi = maker.Contains("sunmi", StringComparison.OrdinalIgnoreCase);
            var imin = maker.Contains("imin", StringComparison.OrdinalIgnoreCase);
            var bonded = FindBondedBuiltIn(context);
            if (!sunmi && !imin && bonded is null)
                return;

            PosLogger.Log($"Android: POS-терминал {maker} {model} — встроенный принтер " +
                          (bonded is null ? "(Bluetooth пока не виден)" : $"«{bonded.Value.Name}» {bonded.Value.Address}") + ".", "PRINTER");

            var prefs = UserPreferences.Instance;
            var path = (prefs.ReceiptDevicePath ?? "").Trim().ToUpperInvariant();
            var notConfigured = path.Length == 0 || path.StartsWith("LPT", StringComparison.Ordinal)
                                || path.StartsWith("COM", StringComparison.Ordinal) || path.StartsWith("USB0", StringComparison.Ordinal);
            if (!notConfigured)
                return;

            var isSunmiPrinter = sunmi || string.Equals(bonded?.Address, SunmiInnerPrinterAddress, StringComparison.OrdinalIgnoreCase);
            prefs.ReceiptDevicePath = isSunmiPrinter ? "BT:" + SunmiInnerPrinterAddress
                : bonded is { } b ? "BT:" + b.Address
                : "BT";
            prefs.ReceiptEncoding = isSunmiPrinter ? EscPosTextReceiptPrinter.Utf8PosEncoding : "cp866";
            prefs.ReceiptEscPosTable = isSunmiPrinter ? null : 17; // ESC t 17 — CP866
            prefs.SelectedPrintMode = PrintMode.Text;
            // Ручной терминал — лента 58 мм (384 точки), настольный — 80 мм (576 точек).
            prefs.ReceiptPaperWidthMm = NurMarketKassa.AvaloniaHost.Services.DeviceForm.IsHandheld
                ? ReceiptPaperProfile.Paper58mm
                : ReceiptPaperProfile.Paper80mm;
            prefs.SaveToDisk();
            PosLogger.Log($"Android: встроенный принтер выбран сам — порт {prefs.ReceiptDevicePath}, кодировка {prefs.ReceiptEncoding}, " +
                          $"лента {prefs.ReceiptPaperWidthMm} мм.", "PRINTER");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: встроенный принтер не настроен сам: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сопряжённый встроенный принтер — без запроса разрешений (нет разрешения Bluetooth — null).</summary>
    private static (string Name, string Address)? FindBondedBuiltIn(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31)
            && context.CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted)
            return null;
        if ((context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter is not { } adapter)
            return null;
        foreach (var device in adapter.BondedDevices ?? Enumerable.Empty<BluetoothDevice>())
        {
            var name = device.Name ?? "";
            if (string.Equals(device.Address, SunmiInnerPrinterAddress, StringComparison.OrdinalIgnoreCase)
                || BuiltInNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return (name, device.Address ?? "");
        }
        return null;
    }

    /// <summary>Встроенный принтер среди сопряжённых — первым (для «BT» без адреса и для списка в настройках).</summary>
    internal static bool IsBuiltIn(BluetoothDevice device) =>
        string.Equals(device.Address, SunmiInnerPrinterAddress, StringComparison.OrdinalIgnoreCase)
        || BuiltInNames.Any(n => string.Equals(n, device.Name ?? "", StringComparison.OrdinalIgnoreCase));
}
