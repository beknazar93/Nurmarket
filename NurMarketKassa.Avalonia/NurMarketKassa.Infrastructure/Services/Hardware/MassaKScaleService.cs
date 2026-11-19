using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

public enum MassaKError
{
    None,
    BadAddress,
    ConnectFailed,
    NoReply,
    Nack,
    Rejected,
    SendFailed,
    Cancelled,
}

/// <summary>Итог проверки связи (только чтение): серийный номер, маска файлов, масса на платформе.</summary>
public sealed record MassaKCheckResult(MassaKError Error, uint? Serial, uint? FileMask, double? WeightKg, bool? WeightStable, string Detail)
{
    public bool Ok => Error == MassaKError.None;
}

/// <summary>Итог загрузки: сколько товаров принято весами целиком (обе записи — товар и PLU).</summary>
public sealed record MassaKUploadResult(MassaKError Error, int Total, int Accepted, int FailedId, string Detail)
{
    public bool Ok => Error == MassaKError.None;
}

public sealed record MassaKUploadProgress(int Done, int Total);

/// <summary>
/// 2026-11-13: транспорт обмена с весами Масса-К серий R/SL (<see cref="MassaKProtocol"/>): TCP, порт 5001 по умолчанию, запрос → ответ, при тишине
/// и при CMD_NACK — повтор того же кадра (протокол: «повторить; 5 раз подряд не удалось — остановить»). Шлёт только: GET_DEVICE_ID, GET_STATUS, GET_WEIGHT
/// (проверка связи — чтение), SET_WORK_MODE и одиночные записи DFILE+100 (загрузка). Стирание файлов (RESET_FILES) не отправляется никогда.
/// Весь обмен — в журнал <see cref="ExchangeLogPath"/> (байты в hex). Режим <c>dryRun</c> ничего не отправляет, а только пишет кадры в журнал.
/// НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО — проверено на локальной «ловушке-весах» в тестах.
/// </summary>
public sealed class MassaKScaleService
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly int _replyTimeoutMs;
    private readonly int _retries;

    public MassaKScaleService(IPAddress address, int port = MassaKProtocol.DefaultPort, int replyTimeoutMs = 3000, int retries = 4)
    {
        _address = address;
        _port = port is > 0 and <= 65535 ? port : MassaKProtocol.DefaultPort;
        _replyTimeoutMs = Math.Max(200, replyTimeoutMs);
        _retries = Math.Clamp(retries, 0, 10);
    }

    /// <summary>null — адрес не IPv4.</summary>
    public static MassaKScaleService? TryCreate(string? host, int port = MassaKProtocol.DefaultPort)
    {
        if (!IPAddress.TryParse((host ?? "").Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        return new MassaKScaleService(address, port);
    }

    public string Host => _address.ToString();
    public int Port => _port;

    /// <summary>%LOCALAPPDATA%\NurMarketKassa\Logs\massak-exchange.log (у программы владельца — своя папка).</summary>
    public static string ExchangeLogPath => ExchangeLogPathOverride ?? Path.Combine(
        NurMarketKassa.Services.AppPaths.Local, AppMode.DataFolderName, "Logs", "massak-exchange.log");

    /// <summary>Для тестов: журнал не в папку установленной кассы.</summary>
    public static string? ExchangeLogPathOverride { get; set; }

    private static readonly object LogLock = new();

    private void LogText(string direction, string text)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Host}:{Port} {direction} {text}{Environment.NewLine}";
            lock (LogLock)
            {
                var path = ExchangeLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2_000_000)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // журнал вспомогательный — из-за него обмен падать не должен
        }
    }

    private void Log(string direction, ReadOnlySpan<byte> bytes) => LogText(direction, MassaKProtocol.Hex(bytes));

    /// <summary>Проверка связи ТОЛЬКО ЧТЕНИЕМ: серийный номер (0x90), состояние файлов (0x80), масса (0xA0). Ничего не записывает.</summary>
    public async Task<MassaKCheckResult> CheckAsync(CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var connected = await ConnectAsync(client, ct).ConfigureAwait(false);
        if (connected != MassaKError.None)
            return new MassaKCheckResult(connected, null, null, null, null, "connect");
        var stream = client.GetStream();

        uint? serial = null, mask = null;
        double? kg = null;
        bool? stable = null;
        var answered = false;
        var nacks = 0;

        var (e1, f1) = await ExchangeAsync(stream, MassaKProtocol.BuildGetDeviceId(), ct, allowNack: true, retries: 1).ConfigureAwait(false);
        if (e1 is MassaKError.Cancelled or MassaKError.SendFailed or MassaKError.NoReply)
            return new MassaKCheckResult(e1, null, null, null, null, "device id");
        if (f1 is not null)
        {
            answered = true;
            serial = MassaKProtocol.ParseDeviceId(f1);
            if (f1.Command == MassaKProtocol.CmdNack)
                nacks++;
        }
        var (e2, f2) = await ExchangeAsync(stream, MassaKProtocol.BuildGetStatus(), ct, allowNack: true, retries: 1).ConfigureAwait(false);
        if (f2 is not null)
        {
            answered = true;
            mask = MassaKProtocol.ParseFileStatus(f2);
            if (f2.Command == MassaKProtocol.CmdNack)
                nacks++;
        }
        else if (e2 is MassaKError.Cancelled or MassaKError.SendFailed)
            return new MassaKCheckResult(e2, serial, null, null, null, "status");
        var (_, f3) = await ExchangeAsync(stream, MassaKProtocol.BuildGetWeight(), ct, allowNack: true, retries: 1).ConfigureAwait(false);
        if (f3 is not null)
        {
            answered = true;
            if (MassaKProtocol.ParseWeight(f3) is { } w)
            {
                kg = w.Kg;
                stable = w.Stable;
            }
        }
        if (!answered)
            return new MassaKCheckResult(MassaKError.NoReply, null, null, null, null, "no reply");
        // Ответили, но все три команды «неизвестная команда» — это не весы Масса-К серии R/SL (или другая серия): честно говорим.
        if (serial is null && mask is null && kg is null && nacks > 0)
            return new MassaKCheckResult(MassaKError.Nack, null, null, null, null, "nack");
        return new MassaKCheckResult(MassaKError.None, serial, mask, kg, stable, "");
    }

    /// <summary>Загрузка товаров: SET_WORK_MODE → для каждого товара запись в файл товаров (101) и файл PLU (105) → отключение. При <paramref name="dryRun"/> сети нет.</summary>
    public async Task<MassaKUploadResult> UploadAsync(IReadOnlyList<(MassaKGoods Goods, long Plu)> items, bool latinText, bool dryRun,
        IProgress<MassaKUploadProgress>? progress, CancellationToken ct)
    {
        if (items.Count == 0)
            return new MassaKUploadResult(MassaKError.None, 0, 0, 0, "");

        if (dryRun)
        {
            LogText("DRY", $"пробный прогон: {items.Count} товаров, ничего не отправляется");
            Log("DRY-TX", MassaKProtocol.BuildSetWorkMode());
            foreach (var (goods, plu) in items)
            {
                var (g, p) = MassaKProtocol.BuildUpsertFrames(goods, plu, latinText);
                Log("DRY-TX", g);
                Log("DRY-TX", p);
            }
            return new MassaKUploadResult(MassaKError.None, items.Count, 0, 0, "dry");
        }

        using var client = new TcpClient(AddressFamily.InterNetwork);
        var connected = await ConnectAsync(client, ct).ConfigureAwait(false);
        if (connected != MassaKError.None)
            return new MassaKUploadResult(connected, items.Count, 0, 0, "connect");
        var stream = client.GetStream();

        // Режим обмена. Весы без этой команды (SL) ответят CMD_NACK «неизвестная команда» — продолжаем; отказ именно режима (0x54) — стоп.
        var (modeError, modeFrame) = await ExchangeAsync(stream, MassaKProtocol.BuildSetWorkMode(), ct, allowNack: true).ConfigureAwait(false);
        if (modeError != MassaKError.None)
            return new MassaKUploadResult(modeError, items.Count, 0, 0, "work mode");
        if (modeFrame is { Command: MassaKProtocol.CmdNackWorkMode })
            return new MassaKUploadResult(MassaKError.Rejected, items.Count, 0, 0, "work mode refused");

        var accepted = 0;
        foreach (var (goods, plu) in items)
        {
            var (goodsFrame, pluFrame) = MassaKProtocol.BuildUpsertFrames(goods, plu, latinText);
            var (e1, r1) = await ExchangeAsync(stream, goodsFrame, ct, allowNack: false).ConfigureAwait(false);
            if (e1 != MassaKError.None)
                return new MassaKUploadResult(e1, items.Count, accepted, goods.Id, "goods");
            if (!MassaKProtocol.IsDfileAck(r1!, MassaKProtocol.FileGoods + MassaKProtocol.SingleRecordOffset))
                return new MassaKUploadResult(MassaKError.Rejected, items.Count, accepted, goods.Id, $"goods reply {r1!.Command:X2}");
            var (e2, r2) = await ExchangeAsync(stream, pluFrame, ct, allowNack: false).ConfigureAwait(false);
            if (e2 != MassaKError.None)
                return new MassaKUploadResult(e2, items.Count, accepted, goods.Id, "plu");
            if (!MassaKProtocol.IsDfileAck(r2!, MassaKProtocol.FilePlu + MassaKProtocol.SingleRecordOffset))
                return new MassaKUploadResult(MassaKError.Rejected, items.Count, accepted, goods.Id, $"plu reply {r2!.Command:X2}");
            accepted++;
            progress?.Report(new MassaKUploadProgress(accepted, items.Count));
        }
        return new MassaKUploadResult(MassaKError.None, items.Count, accepted, 0, "");
    }

    private async Task<MassaKError> ConnectAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_replyTimeoutMs);
            LogText("CONNECT", "");
            await client.ConnectAsync(_address, _port, timeout.Token).ConfigureAwait(false);
            return MassaKError.None;
        }
        catch (OperationCanceledException)
        {
            LogText("ERR", ct.IsCancellationRequested ? "отмена" : "таймаут подключения");
            return ct.IsCancellationRequested ? MassaKError.Cancelled : MassaKError.ConnectFailed;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            LogText("ERR", ex.Message);
            return MassaKError.ConnectFailed;
        }
    }

    /// <summary>Кадр → ответ. Тишина и (если не allowNack) CMD_NACK — повтор того же кадра; кадр с неверной CRC игнорируется.
    /// allowNack: CMD_NACK возвращается как ответ (проверка связи и режим обмена) без повторов.</summary>
    private async Task<(MassaKError Error, MassaKFrame? Frame)> ExchangeAsync(NetworkStream stream, byte[] frame, CancellationToken ct, bool allowNack, int? retries = null)
    {
        var maxRetries = retries ?? _retries;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return (MassaKError.Cancelled, null);
            try
            {
                Log(attempt == 0 ? "TX" : $"TX(повтор {attempt})", frame);
                await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (MassaKError.Cancelled, null);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                LogText("ERR", ex.Message);
                return (MassaKError.SendFailed, null);
            }

            var (state, reply) = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            switch (state)
            {
                case ReadState.Cancelled:
                    return (MassaKError.Cancelled, null);
                case ReadState.Frame when reply!.Command == MassaKProtocol.CmdNack && !allowNack:
                    continue; // «ошибка CRC принятого кадра или неизвестная команда» — повтор
                case ReadState.Frame:
                    return (MassaKError.None, reply);
                case ReadState.Closed:
                    LogText("RX", "— соединение закрыто весами");
                    return (MassaKError.NoReply, null);
                default:
                    continue; // тишина / кадр с плохой CRC — повтор
            }
        }
        return (MassaKError.NoReply, null);
    }

    private enum ReadState { Frame, Silence, Closed, Cancelled }

    private async Task<(ReadState State, MassaKFrame? Frame)> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var have = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_replyTimeoutMs);
        while (true)
        {
            try
            {
                var n = await stream.ReadAsync(buffer.AsMemory(have), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                    return (ReadState.Closed, null);
                have += n;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    return (ReadState.Cancelled, null);
                LogText("RX", "— тишина");
                return (ReadState.Silence, null);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                LogText("RX", "— " + ex.Message);
                return (ReadState.Closed, null);
            }
            while (true)
            {
                var state = MassaKProtocol.TryParse(buffer.AsSpan(0, have), out var parsed, out var consumed);
                if (state == MassaKParseState.NeedMore)
                    break;
                Log("RX", buffer.AsSpan(0, Math.Min(consumed, have)));
                Array.Copy(buffer, consumed, buffer, 0, have - consumed);
                have -= consumed;
                if (state == MassaKParseState.Ok)
                    return (ReadState.Frame, parsed);
                // BadHeader / BadCrc — отбрасываем и ждём дальше; BadCrc при тишине дальше превратится в повтор
                if (have == 0)
                    break;
            }
            if (have == buffer.Length)
                have = 0; // переполнение мусором
        }
    }
}
