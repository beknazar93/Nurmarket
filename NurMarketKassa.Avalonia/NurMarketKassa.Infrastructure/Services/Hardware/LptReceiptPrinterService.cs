namespace NurMarketKassa.Services.Hardware;

/// <summary>Печать чека на физический ESC/POS принтер (LPT).</summary>
public sealed class LptReceiptPrinterService : IReceiptPrinterService
{
    public Task<bool> PrintReceiptAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LastFailureReason = null;
        // 2026-10-27 (аудит кассы, С-14): копия чека/отчёт по кнопке («{}» в корзине) не запускается, пока прошлая печать ещё идёт: после
        // тайм-аута 8 с принтер мог доделать чек, а кассир, видя «не напечатан», жал копию и получал два чека. Чек продажи эта защита
        // не трогает — он стоит в очереди за портом, как раньше.
        var explicitCopy = string.IsNullOrWhiteSpace(cart.CartJson) || cart.CartJson.Trim() == "{}";
        if (explicitCopy && _inFlight is { IsCompleted: false })
        {
            LastFailureReason = Tr.T("Предыдущая печать ещё не закончилась — подождите минуту и проверьте ленту, чтобы не получить два чека.",
                "Мурунку басып чыгаруу али бүтө элек — эки чек чыкпашы үчүн бир мүнөт күтүп, лентаны текшериңиз.",
                "The previous print is still running — wait a minute and check the paper so you do not get two receipts.",
                "Önceki yazdırma henüz bitmedi — iki fiş çıkmaması için bir dakika bekleyip kâğıdı kontrol edin.",
                "Oldingi chop etish hali tugamadi — ikkita chek chiqmasligi uchun bir daqiqa kutib, lentani tekshiring.");
            PosLogger.Log("Печать пропущена: предыдущая печать ещё идёт (защита от двойного чека).", "PRINTER");
            return Task.FromResult(false);
        }

        var prefs = UserPreferences.Instance;
        if (!prefs.ReceiptEnabled)
        {
            PosLogger.Log("Печать пропущена: выключена в настройках кассы (ReceiptEnabled=false).", "PRINTER");
            return Task.FromResult(false);
        }

        if (HardwareModeHelper.IsNonePort(prefs.ReceiptDevicePath))
        {
            PosLogger.Log("Печать пропущена: порт принтера не указан.", "PRINTER");
            return Task.FromResult(false);
        }

        return PrintWithTimeoutAsync(cart);
    }

    /// <summary>2026-10-27 (С-14): печать, которая ещё идёт (в том числе после тайм-аута ожидания).</summary>
    private static Task? _inFlight;

    /// <summary>2026-10-27 (С-14): понятная причина последней неудачи для окна (null — неудачи не было). Читает
    /// ReceiptHistoryService.PrintAsync вместо общего «Принтер не ответил».</summary>
    public static string? LastFailureReason { get; private set; }

    // 2026-09-07: реальный баг — ReceiptPrintService.PrintReceipt блокирующий (сырые вызовы
    // WinUSB/COM/LPT без единого таймаута), и раньше вызывался прямо тут. Если принтер завис на
    // уровне драйвера (например, оборвался кабель во время записи), продажа на сервере уже
    // проходит, но касса зависала на "Проводим оплату..." навсегда — единственным выходом было
    // убить процесс. Печать теперь выполняется на отдельном потоке с жёстким лимитом времени:
    // после тайм-аута продажа считается завершённой (просто без чека), а не зависшей.
    private static async Task<bool> PrintWithTimeoutAsync(CartSnapshot cart)
    {
        var printTask = Task.Run(() =>
        {
            ReceiptPrintService.PrintReceipt(
                cart.CartJson,
                cart.OfflineNote,
                cart.PaymentMethodKey,
                cart.CashReceived,
                cart.ReceiptText);
            return true;
        });

        _inFlight = printTask;
        var finished = await Task.WhenAny(printTask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
        if (finished != printTask)
        {
            // 2026-10-27 (С-14): само ожидание порта (до 15 с) и запись (8+ с) длиннее этих 8 с — «не напечатан» было неточным: чек
            // мог выйти позже. Время ожидания не растягиваем (оплата не должна висеть, см. 2026-09-07), но говорим правду.
            LastFailureReason = Tr.T("Принтер не ответил за 8 секунд, но чек мог напечататься позже. Проверьте ленту, прежде чем печатать копию.",
                "Принтер 8 секундада жооп берген жок, бирок чек кийинчерээк чыгышы мүмкүн. Көчүрмөнү басуудан мурун лентаны текшериңиз.",
                "The printer did not answer in 8 seconds, but the receipt may still print later. Check the paper before printing a copy.",
                "Yazıcı 8 saniyede yanıt vermedi ama fiş sonradan yazdırılabilir. Kopya yazdırmadan önce kâğıdı kontrol edin.",
                "Printer 8 soniyada javob bermadi, lekin chek keyinroq chiqishi mumkin. Nusxa chop etishdan oldin lentani tekshiring.");
            PosLogger.Log("Печать LPT: тайм-аут — принтер не отвечает 8 секунд, продажа уже проведена, чек не распечатан (может выйти позже).", "PRINTER");
            // Задача печати дорабатывает сама и может упасть позже (принтер так и не ответил) —
            // забираем её ошибку, иначе она всплывает в журнале как «Unobserved task exception».
            _ = printTask.ContinueWith(
                t => PosLogger.Log($"Печать LPT: чек после тайм-аута так и не напечатан: {t.Exception?.GetBaseException().Message}", "PRINTER"),
                TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }

        try
        {
            return await printTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать LPT: {ex.Message}\n{ex.StackTrace}", "PRINTER");
            // 2026-10-27 (С-14): причина — кассиру словами принтера (нет бумаги, крышка, порт занят), а не общее «не ответил».
            LastFailureReason = Tr.T("Принтер вернул ошибку: ", "Принтер ката кайтарды: ", "The printer returned an error: ", "Yazıcı hata verdi: ", "Printer xato qaytardi: ") + ex.Message;
            return false;
        }
    }
}
