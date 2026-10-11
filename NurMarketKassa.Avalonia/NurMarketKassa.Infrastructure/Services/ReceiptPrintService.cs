using System.Globalization;
using System.IO;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public static class ReceiptPrintService
{
    public static void PrintText(ReceiptPrinterSettings cfg, string text, int? charWidth = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        EscPosTextReceiptPrinter.ValidateSettings(cfg);
        EscPosTextReceiptPrinter.Print(cfg, text, charWidth);
    }

    public static void PrintGraphic(GraphicReceiptSettings settings, string text)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Graphic receipt printing is supported only on Windows.");
        ValidateGraphicSettings(settings);
        GraphicReceiptPrinter.Print(text, settings);
    }

    public static void PrintGraphicTest(GraphicReceiptSettings settings, string storeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Graphic receipt printing is supported only on Windows.");
        ValidateGraphicSettings(settings);
        var bytes = GraphicReceiptGenerator.GenerateTestReceiptImage(settings, storeName);
        PrinterPortService.SendRawBytes(settings.DevicePath, bytes, settings.RetryCount);
    }

    public static void SendRawBytes(string port, byte[] bytes, int retries = 3) =>
        PrinterPortService.SendRawBytes(port, bytes, retries);

    /// <summary>Открывает денежный ящик. Ящик подключён к чековому принтеру, а не к компьютеру,
    /// поэтому импульс уходит в тот же порт, что и чек (<see cref="UserPreferences.ReceiptDevicePath"/>).
    /// <paramref name="pinOverride"/> и <paramref name="portOverride"/> задают контакт и порт в обход
    /// сохранённых настроек — нужны кнопке проверки, чтобы перебрать оба контакта и только что
    /// введённый порт, ничего предварительно не сохраняя.
    /// Бросает исключение — вызывается из настроек, где кассиру надо показать причину.</summary>
    public static void OpenCashDrawer(int? pinOverride = null, string? portOverride = null) =>
        OpenCashDrawer(pinOverride, portOverride, afterSale: false);

    /// <param name="afterSale">2026-10-11, владелец: «ящик открывает один раз и больше не открывает, проблема с принтером тоже».
    /// После продажи принтер может ещё печатать чек: ESC @ (сброс) очищает буфер печати — хвост чека пропадал, а импульс ящика
    /// терялся. После продажи — без сброса: ESC p и следом DLE DC4 (мгновенный импульс, выполняется даже во время печати).
    /// Кнопка проверки в настройках (принтер без дела) — со сбросом, как раньше.</param>
    public static void OpenCashDrawer(int? pinOverride, string? portOverride, bool afterSale)
    {
        var prefs = UserPreferences.Instance;
        var port = HardwarePortHelper.NormalizeLptPort(portOverride ?? prefs.ReceiptDevicePath);
        if (string.IsNullOrWhiteSpace(port))
            throw new InvalidOperationException("Не указан порт принтера, к которому подключён денежный ящик.");

        // 2026-10-01, владелец: «денежный ящик один раз заработал, потом перестал». Отдельное
        // задание из одной команды ESC p принтер выполнял, только если был в исходном состоянии;
        // после чека (режим страницы/графики, незакрытая строка) часть принтеров такую команду
        // молча пропускает. Перед импульсом — ESC @ (сброс принтера в исходное состояние).
        // Каждая попытка пишется в журнал с портом и контактом — по журналу клиента видно,
        // дошла ли команда до принтера.
        var pin = pinOverride ?? prefs.CashDrawerPin;
        using var ms = new MemoryStream();
        if (!afterSale)
        {
            ms.WriteByte(0x1B); // ESC @
            ms.WriteByte(0x40);
        }
        EscPosCommands.WriteOpenCashDrawer(ms, pin);
        if (afterSale)
            EscPosCommands.WriteRealtimeDrawerPulse(ms, pin);
        PosLogger.Log($"Денежный ящик: импульс на порт {port}, контакт {(pin == 1 ? 5 : 2)}{(afterSale ? ", после продажи (без сброса, + DLE DC4)" : "")}.", "PRINTER");
        PrinterPortService.SendRawBytes(port, ms.ToArray(), prefs.ReceiptRetryCount);
    }

    /// <summary>Открывает ящик после продажи, если он включён в настройках и оплата была наличной.
    /// При смешанной оплате ящик нужен только когда реально брали наличные (<paramref name="cashReceived"/> &gt; 0).
    /// Ошибку принтера сюда пускать нельзя: продажа уже проведена, и падение на открытии ящика
    /// выглядело бы для кассира как несостоявшаяся оплата — поэтому она только пишется в журнал.</summary>
    public static void TryOpenCashDrawerAfterSale(string? paymentMethodKey, string? cashReceived) =>
        TryOpenCashDrawerAfterSale(paymentMethodKey, cashReceived, null);

    /// <summary>2026-10-11: ящик — после печати чека (<paramref name="afterPrint"/>): чек и импульс шли на один принтер
    /// одновременно, и после первого раза принтер команду ящика пропускал. Печать не удалась — ящик всё равно открывается.</summary>
    public static void TryOpenCashDrawerAfterSale(string? paymentMethodKey, string? cashReceived, Task? afterPrint)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.CashDrawerEnabled)
        {
            PosLogger.Log("Денежный ящик: выключен в настройках кассы — не открываю.", "PRINTER");
            return;
        }

        var pm = (paymentMethodKey ?? "").Trim().ToLowerInvariant();
        var takesCash = pm switch
        {
            "cash" => true,
            "mixed" => double.TryParse(cashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var part) && part > 0,
            _ => false,
        };
        if (!takesCash)
        {
            PosLogger.Log($"Денежный ящик: оплата «{pm}» без наличных — не открываю.", "PRINTER");
            return;
        }

        // В фоне, а не в потоке оплаты: порт может отвечать медленно (LPT пишется через отдельный
        // процесс copy, USB-принтер может просыпаться из энергосбережения), а оплата уже прошла —
        // кассир не должен ждать ящик. Ровно по этой причине из потока оплаты вынесена и печать,
        // см. LptReceiptPrinterService (живой баг 2026-09-07: "Проводим оплату…" висело навсегда).
        _ = Task.Run(async () =>
        {
            try
            {
                if (afterPrint is not null)
                {
                    // Ждём чек, но не дольше 20 с: зависший принтер не должен держать ящик закрытым навсегда.
                    await Task.WhenAny(afterPrint, Task.Delay(20_000)).ConfigureAwait(false);
                    // Принтер дорезает и выдаёт чек — короткая пауза, чтобы импульс не попал в хвост печати.
                    await Task.Delay(400).ConfigureAwait(false);
                }
                OpenCashDrawer(null, null, afterSale: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Не удалось открыть денежный ящик: {ex.Message}", "PRINTER");
            }
        });
    }

    public static void PrintReceipt(
        string cartJson,
        string? offlineNote = null,
        string? paymentMethodKey = null,
        string? cashReceived = null,
        string? receiptText = null)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.ReceiptEnabled)
        {
            PosLogger.Log("ReceiptPrintService: печать пропущена — выключена в настройках кассы.", "PRINTER");
            return;
        }

        var text = !string.IsNullOrWhiteSpace(receiptText)
            ? receiptText
            : CartReceiptTextBuilder.BuildSimpleReceipt(cartJson, offlineNote, paymentMethodKey, cashReceived);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Текст чека пуст.");

        // 2026-10-05: графический чек рисуется через System.Drawing — его нет на Android и Linux. Там — текстом
        // ESC/POS, а не ошибка печати после каждой продажи.
        if (prefs.SelectedPrintMode == PrintMode.Graphic && !OperatingSystem.IsWindows())
        {
            PosLogger.Log("ReceiptPrintService: графический чек есть только в Windows — печать текстом.", "PRINTER");
            PrintText(prefs.ToReceiptPrinterSettings(), text);
        }
        else if (prefs.SelectedPrintMode == PrintMode.Graphic)
        {
            if (!prefs.GraphicReceiptEnabled)
                throw new InvalidOperationException("Графический чек выключен в настройках кассы.");

            PrintGraphic(prefs.ToGraphicReceiptSettings(), text);
        }
        else
        {
            PrintText(prefs.ToReceiptPrinterSettings(), text);
        }
    }

    private static void ValidateGraphicSettings(GraphicReceiptSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DevicePath))
            throw new InvalidOperationException("Не указан порт принтера (LPT/COM).");
    }
}
