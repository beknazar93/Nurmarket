namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13 (диагностика «Принтер не печатает?»): ответ принтера о своём состоянии — нет бумаги, открыта крышка, ошибка ножа…
/// Работает только при двусторонней связи (сеть 9100, USB напрямую через интерфейс usbprint); через очередь Windows ответа не получить.</summary>
public sealed record PrinterStatusReport(
    bool Offline = false,
    bool CoverOpen = false,
    bool PaperOut = false,
    bool PaperNearEnd = false,
    bool Error = false,
    bool CutterError = false,
    bool Paused = false,
    bool Printing = false)
{
    /// <summary>Печатать можно (бумага кончается — только предупреждение).</summary>
    public bool IsOk => !(Offline || CoverOpen || PaperOut || Error || CutterError || Paused);
}

/// <summary>Разбор ответов: ESC/POS «DLE EOT n» (10 04 n) и TSPL «ESC ! ?» (1B 21 3F). Чистые функции — проверяются тестами.</summary>
public static class PrinterStatusQuery
{
    /// <summary>DLE EOT n: 1 — состояние принтера, 2 — причина «не в сети», 3 — ошибки, 4 — датчики бумаги.</summary>
    public static byte[] DleEot(int n) => [0x10, 0x04, (byte)n];

    /// <summary>TSPL: запрос состояния одним байтом.</summary>
    public static readonly byte[] TsplStatusCommand = [0x1B, 0x21, 0x3F];

    /// <summary>Байт ответа DLE EOT по стандарту ESC/POS: бит 0 = 0, бит 1 = 1, бит 4 = 1, бит 7 = 0. Иначе это не ответ на запрос (мусор, эхо).</summary>
    public static bool IsEscPosStatusByte(byte b) => (b & 0x93) == 0x12;

    /// <summary>Разбор ответов DLE EOT 1…4 (любой может отсутствовать — принтер ответил не на всё). null — нет ни одного верного ответа.</summary>
    public static PrinterStatusReport? ParseEscPos(byte? printer, byte? offlineCause, byte? errorCause, byte? paperSensor)
    {
        static bool Ok(byte? b) => b is { } v && IsEscPosStatusByte(v);
        if (!Ok(printer) && !Ok(offlineCause) && !Ok(errorCause) && !Ok(paperSensor))
            return null;
        var p = Ok(printer) ? printer!.Value : (byte)0x12;
        var o = Ok(offlineCause) ? offlineCause!.Value : (byte)0x12;
        var e = Ok(errorCause) ? errorCause!.Value : (byte)0x12;
        var s = Ok(paperSensor) ? paperSensor!.Value : (byte)0x12;
        return new PrinterStatusReport(
            Offline: (p & 0x08) != 0,
            CoverOpen: (o & 0x04) != 0,
            PaperOut: (o & 0x20) != 0 || (s & 0x60) != 0,
            PaperNearEnd: (s & 0x0C) != 0,
            Error: (o & 0x40) != 0 || (e & 0x24) != 0 || (e & 0x40) != 0,
            CutterError: (e & 0x08) != 0);
    }

    /// <summary>Разбор ответа TSPL «ESC ! ?»: 0 — всё в порядке; бит 0 — открыта головка/крышка, 1 — замятие, 2 — нет бумаги (этикеток), 3 — нет ленты,
    /// 4 — пауза, 5 — печатает, 6 — открыта крышка, 7 — другая ошибка.</summary>
    public static PrinterStatusReport ParseTspl(byte b) => new(
        CoverOpen: (b & 0x41) != 0,
        PaperOut: (b & 0x04) != 0,
        Error: (b & 0x8A) != 0,
        Paused: (b & 0x10) != 0,
        Printing: (b & 0x20) != 0);

    /// <summary>Спросить состояние по двустороннему потоку (сеть, USB напрямую). null — принтер не ответил (не поддерживает обратную связь или занят).</summary>
    public static async Task<PrinterStatusReport?> QueryAsync(Stream duplex, PrinterLanguage language, int timeoutMs = 800, CancellationToken ct = default)
    {
        if (language == PrinterLanguage.Tspl)
        {
            var b = await AskAsync(duplex, TsplStatusCommand, timeoutMs, ct).ConfigureAwait(false);
            return b is { } v ? ParseTspl(v) : null;
        }
        var first = await AskAsync(duplex, DleEot(1), timeoutMs, ct).ConfigureAwait(false);
        if (first is not { } f || !IsEscPosStatusByte(f))
        {
            // ESC/POS не ответил — может быть, это этикеточный принтер (TSPL)
            if (language == PrinterLanguage.Unknown)
            {
                var t = await AskAsync(duplex, TsplStatusCommand, timeoutMs, ct).ConfigureAwait(false);
                return t is { } tv && !IsEscPosStatusByte(tv) ? ParseTspl(tv) : null;
            }
            return null;
        }
        var second = await AskAsync(duplex, DleEot(2), timeoutMs, ct).ConfigureAwait(false);
        var third = await AskAsync(duplex, DleEot(3), timeoutMs, ct).ConfigureAwait(false);
        var fourth = await AskAsync(duplex, DleEot(4), timeoutMs, ct).ConfigureAwait(false);
        return ParseEscPos(f, second, third, fourth);
    }

    private static async Task<byte?> AskAsync(Stream s, byte[] command, int timeoutMs, CancellationToken ct)
    {
        await s.WriteAsync(command, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var buf = new byte[1];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)));
            int read;
            try
            {
                read = await s.ReadAsync(buf.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            if (read == 1)
                return buf[0];
            // usbprint без данных отвечает «0 байт» сразу — подождать и спросить снова
            await Task.Delay(40, ct).ConfigureAwait(false);
        }
        return null;
    }
}
