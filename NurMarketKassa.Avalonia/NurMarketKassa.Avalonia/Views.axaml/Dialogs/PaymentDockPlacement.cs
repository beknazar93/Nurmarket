using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, жалоба владельца «почему окно оплаты вниз ушло??»: положение окна оплаты по вертикали.
/// По умолчанию окно стоит по центру клиентской области главного окна (слева от чека), при желании — прижато к низу, как с 2026-10-17.
/// Настройка — свой файл <c>payment_dialog_position.json</c> в папке данных кассы: <c>{"mode":"center"}</c> или <c>{"mode":"bottom"}</c>.
/// Расчёт места — чистая функция без Avalonia (проверяется стендом).</summary>
internal static class PaymentDockPlacement
{
    /// <summary>Ниже этой высоты клиентской области (1024×600, 1366×768) места мало — окно прижимается к низу, как раньше.</summary>
    public const double CenterMinClientHeight = 700;

    public const string FileName = "payment_dialog_position.json";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "data", FileName);

    /// <summary>true — режим «прижимать к низу». Нет файла или он испорчен — false (по центру).</summary>
    public static bool ReadBottomMode(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("mode", out var mode)
                   && mode.ValueKind == JsonValueKind.String
                   && string.Equals(mode.GetString(), "bottom", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно оплаты: {FileName} не прочитан ({ex.Message}) — по центру.", "WARNING");
            return false;
        }
    }

    /// <summary>Смена режима стирает и смещение, оставленное перетаскиванием (новый режим — новое «обычное» место).</summary>
    public static void WriteBottomMode(bool bottom, string? path = null) => Write(bottom, 0, 0, path);

    private static void Write(bool bottom, int dx, int dy, string? path)
    {
        try
        {
            path ??= FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = bottom ? "{\"mode\":\"bottom\"" : "{\"mode\":\"center\"";
            if (dx != 0 || dy != 0)
                json += $",\"dx\":{dx},\"dy\":{dy}";
            File.WriteAllText(path, json + "}");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно оплаты: {FileName} не записан ({ex.Message})", "WARNING");
        }
    }

    /// <summary>2026-10-30, «модалка не двигается!!!»: смещение окна оплаты (в пикселях экрана) от обычного места, которое задаёт режим,
    /// — после перетаскивания за шапку. Нет файла, поля или оно испорчено — (0, 0).</summary>
    public static (int Dx, int Dy) ReadOffset(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path))
                return (0, 0);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("dx", out var dx) && dx.ValueKind == JsonValueKind.Number && dx.TryGetInt32(out var x)
                && root.TryGetProperty("dy", out var dy) && dy.ValueKind == JsonValueKind.Number && dy.TryGetInt32(out var y))
                return (Math.Clamp(x, -20000, 20000), Math.Clamp(y, -20000, 20000));
        }
        catch
        {
            // испорченный файл читает ReadBottomMode (с записью в журнал) — здесь просто «без смещения»
        }
        return (0, 0);
    }

    /// <summary>Запомнить смещение окна оплаты (режим «по центру/к низу» сохраняется).</summary>
    public static void WriteOffset(int dx, int dy, string? path = null) => Write(ReadBottomMode(path), dx, dy, path);

    /// <summary>«Сбросить положение окна оплаты» в настройках: смещение убирается, режим остаётся.</summary>
    public static void ResetOffset(string? path = null) => Write(ReadBottomMode(path), 0, 0, path);

    /// <summary>Верх окна оплаты в пикселях экрана. area — видимая часть клиентской области главного окна в рабочей области экрана;
    /// heightDip — высота окна оплаты; clientHeightDip — высота клиентской области главного окна.
    /// Прижатие: низ окна у низа области минус margin (как раньше). Центр: окно посередине области по вертикали,
    /// но при низкой области (&lt; 700) — как прижатие; в любом случае окно не выходит за область.</summary>
    public static int ComputeY(int areaY, int areaBottom, double heightDip, double scaling, double clientHeightDip, double margin, bool bottomMode)
    {
        var heightPx = (int)Math.Round(heightDip * scaling);
        if (bottomMode || clientHeightDip < CenterMinClientHeight)
            return Math.Max(areaY, areaBottom - (int)Math.Round((heightDip + margin) * scaling));
        var y = areaY + (int)Math.Round((areaBottom - areaY - heightPx) / 2.0);
        return Math.Clamp(y, areaY, Math.Max(areaY, areaBottom - heightPx));
    }
}
