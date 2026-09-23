using NurMarketKassa.Configuration;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.Hardware;

public static class HardwareModeHelper
{
    /// <summary>Физические весы: включены в настройках кассы и указан реальный COM-порт.</summary>
    public static bool UsePhysicalScale()
    {
        var prefs = UserPreferences.Instance;
        return prefs.ScaleEnabled && !IsNonePort(prefs.ScaleComPort);
    }

    /// <summary>Виртуальные весы — только в демо-режиме, когда физический COM-порт не настроен.</summary>
    public static bool UseVirtualScale(AppSettings settings) =>
        UseDemoHardware(settings) && !UsePhysicalScale();

    /// <summary>Указан порт принтера (не NONE/OFF).</summary>
    public static bool IsPrinterPortConfigured() =>
        !IsNonePort(UserPreferences.Instance.ReceiptDevicePath);

    /// <summary>Физический принтер: печать включена и указан реальный LPT/COM-порт.</summary>
    public static bool UsePhysicalPrinter()
    {
        var prefs = UserPreferences.Instance;
        return prefs.ReceiptEnabled && !IsNonePort(prefs.ReceiptDevicePath);
    }

    /// <summary>Виртуальный принтер — только в демо-режиме, когда физический порт не настроен.</summary>
    public static bool UseVirtualPrinter(AppSettings settings) =>
        UseDemoHardware(settings) && !UsePhysicalPrinter();

    public static bool UseDemoHardware(AppSettings settings)
    {
        if (settings.Hardware.DemoMode)
            return true;

        var demoEnv = Environment.GetEnvironmentVariable("DESKTOP_MARKET_DEMO_MODE");
        if (string.Equals(demoEnv, "1", StringComparison.Ordinal) ||
            string.Equals(demoEnv, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        var scaleNone = IsNonePort(settings.Scale.ComPort);
        var printerNone = IsNonePort(settings.ReceiptPrinter.DevicePath);
        if (scaleNone && printerNone)
            return true;

        return false;
    }

    public static bool IsNonePort(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ||
               text.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("OFF", StringComparison.OrdinalIgnoreCase);
    }
}
