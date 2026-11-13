namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>Выбор мастера для чекового принтера.</summary>
/// <param name="Narrow80">«80 мм, 42 знака» (XP-80 и похожие печатают уже 48 знаков).</param>
/// <param name="UserEncoding">Кодировка кассы: «cp866» или «wpc1251» — по строке, которая прочиталась на пробной ленте.</param>
public sealed record ReceiptSetupChoice(string DevicePath, int PaperMm, bool Narrow80, string UserEncoding, bool CashDrawer, int DrawerPin);

/// <summary>Выбор мастера для принтера этикеток.</summary>
public sealed record LabelSetupChoice(string DevicePath, PrinterLanguage Language, double WidthMm, double HeightMm, double GapMm);

/// <summary>2026-11-13 (мастер «Подключить принтер»): запись выбора в ТЕ ЖЕ настройки печати, что и раньше (Настройки → Печать) — без новых полей.
/// Если принтер уже настроен, мастер не обязателен: он меняет настройки только по кнопке «Сохранить» на последнем шаге.</summary>
public static class PrinterSetupApply
{
    public const int Narrow80Chars = 42, Narrow80Dots = 512;

    public static void ApplyReceipt(UserPreferences p, ReceiptSetupChoice c)
    {
        var paper = ReceiptPaperProfile.NormalizePaperWidthMm(c.PaperMm);
        p.ReceiptDevicePath = NetworkPrinterAddress.IsNetworkPort(c.DevicePath) ? NetworkPrinterAddress.Canonical(c.DevicePath) : c.DevicePath.Trim();
        p.ReceiptEnabled = true;
        p.ReceiptPaperWidthMm = paper;
        if (paper >= ReceiptPaperProfile.Paper80mm)
        {
            if (c.Narrow80)
            {
                p.ReceiptCharWidth80 = Narrow80Chars;
                p.ReceiptDots80 = Narrow80Dots;
            }
            else if (p.ReceiptCharWidth80 == Narrow80Chars)
            {
                p.ReceiptCharWidth80 = 0; // обратно на стандарт 48 знаков
                p.ReceiptDots80 = 0;
            }
        }
        p.GraphicPaperWidthPixels = ReceiptPaperProfile.GetRasterWidthPixels(paper); // как при «Сохранить» на странице «Печать»
        p.ReceiptEncoding = c.UserEncoding;
        p.ReceiptEscPosTable = null; // «Авто» = таблица по кодировке (CP866 → 17, CP1251 → 46), как на пробной ленте
        p.CashDrawerEnabled = c.CashDrawer;
        p.CashDrawerPin = c.DrawerPin == 1 ? 1 : 0;
        PosLogger.Log($"Мастер принтера: чековый принтер «{p.ReceiptDevicePath}», лента {paper} мм{(c.Narrow80 ? " (42 знака)" : "")}, кодировка {c.UserEncoding}, ящик {(c.CashDrawer ? "вкл (контакт " + p.CashDrawerPin + ")" : "выкл")}.", "PRINTER");
    }

    /// <summary>Записывает принтер этикеток; размер этикетки меняет в шаблоне (с подгонкой раскладки). true — шаблон изменён (его надо сохранить).</summary>
    public static bool ApplyLabel(UserPreferences p, LabelSetupChoice c, LabelTemplate template)
    {
        p.LabelPrinterDevicePath = NetworkPrinterAddress.IsNetworkPort(c.DevicePath) ? NetworkPrinterAddress.Canonical(c.DevicePath) : c.DevicePath.Trim();
        p.LabelRawLanguage = c.Language == PrinterLanguage.Tspl ? "tspl" : "escpos";
        p.LabelGapMm = Math.Clamp(c.GapMm, 0, 10);
        var changed = Math.Abs(template.WidthMm - c.WidthMm) > 0.05 || Math.Abs(template.HeightMm - c.HeightMm) > 0.05;
        if (changed && c.WidthMm > 0 && c.HeightMm > 0)
            LabelAutoLayout.ScaleToSize(template, c.WidthMm, c.HeightMm);
        PosLogger.Log($"Мастер принтера: принтер этикеток «{p.LabelPrinterDevicePath}», язык {p.LabelRawLanguage}, этикетка {c.WidthMm:0.#}×{c.HeightMm:0.#} мм, зазор {p.LabelGapMm:0.#} мм.", "PRINTER");
        return changed;
    }

    /// <summary>Принтер чеков «не задан»: пусто или LPT1 по умолчанию, а порта LPT в системе нет (новые ПК). Подсказка «Подключить принтер».</summary>
    public static bool ReceiptLooksUnconfigured(string? devicePath, Func<string, bool> lptExists)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return true;
        return HardwarePortHelper.LooksLikeLptPort(devicePath) && !lptExists(devicePath);
    }

    /// <summary>Очистить очередь: сначала свои задания без прав администратора, остались чужие/застрявшие — с повышением прав (UAC).</summary>
    public static async Task<ElevatedOutcome> ClearJobsAsync(string queueName)
    {
        if (!OperatingSystem.IsWindows())
            return new ElevatedOutcome(ElevatedResult.NotSupported);
        var deleted = 0;
        var left = await Task.Run(() => WindowsPrinterNative.DeleteOwnJobs(queueName, out deleted)).ConfigureAwait(false);
        PosLogger.Log($"Мастер принтера: очередь «{queueName}» — удалено своих заданий {deleted}, осталось {left}.", "PRINTER");
        return left == 0 ? new ElevatedOutcome(ElevatedResult.Done) : await PrinterQueueInstaller.ClearJobsElevatedAsync(queueName).ConfigureAwait(false);
    }
}
