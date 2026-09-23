using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Второй способ доставки PLU на весы Rongta (по прямой просьбе владельца,
/// 2026-09-19: "выбор — с сайта или свой сервер... отправлять на весы сразу из нашей ранее
/// скаченной локальной базы") — в отличие от способа по умолчанию (скачать .txp с сайта +
/// RongtaScaleAutomationService), здесь PLU строится ПРЯМО из уже загруженного локального
/// каталога (CatalogCacheService), без обращения к серверу NurCRM в момент отправки, через
/// протокол RongtaTcpProtocol (мануал, раздел 2.2-2.4).
///
/// Архитектура НЕ убирает RLS1000 из цепочки (см. её же doc-comment) — RLS1000 нужно один
/// раз настроить на "TCP/IP mode" как источник PLU (её собственные настройки, File →
/// Options → TCP/IP, IP-адрес = адрес этого компьютера, порт = порт отсюда), после чего то
/// же самое нажатие F9 (RongtaScaleAutomationService) заставляет RLS1000 подключиться СЮДА
/// вместо чтения файла. Мы здесь выступаем TCP-сервером: слушаем один входящий коннект,
/// отвечаем на протокол, закрываемся.
///
/// НЕ ПРОВЕРЕНО на реальном железе — см. предупреждения в RongtaTcpProtocol.</summary>
public static class RongtaTcpServerService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    public static async Task<RongtaTcpServerResult> RunOnceAsync(
        int port,
        IReadOnlyList<(int Plu, string Name, double PriceSom)> products,
        TimeSpan connectTimeout,
        CancellationToken ct = default)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptCts.CancelAfter(connectTimeout);

            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return RongtaTcpServerResult.Failed(
                    "RLS1000 не подключилась за отведённое время. Убедитесь, что в RLS1000 включён режим " +
                    "TCP/IP (File → Options → TCP/IP) с IP этого компьютера и этим портом, и нажмите " +
                    "«Скачать PLU» (F9) в самой RLS1000, если запуск не сработал автоматически.");
            }

            using (client)
            {
                PosLogger.Log($"Rongta TCP: подключение принято от {client.Client.RemoteEndPoint}.", "SCALES");
                await using var stream = client.GetStream();
                var result = await HandleSessionAsync(stream, products, ct).ConfigureAwait(false);
                PosLogger.Log(
                    result.IsSuccess
                        ? $"Rongta TCP: сессия завершена, отправлено записей PLU: {result.RecordsSent}."
                        : $"Rongta TCP: сессия завершена с ошибкой: {result.ErrorMessage}",
                    "SCALES");
                return result;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta TCP server failed: {ex}", "SCALES");
            return RongtaTcpServerResult.Failed("Ошибка сервера: " + ex.Message);
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static async Task<RongtaTcpServerResult> HandleSessionAsync(
        NetworkStream stream, IReadOnlyList<(int Plu, string Name, double PriceSom)> products, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var readBuf = new byte[4096];

        var start = await ReadPacketAsync(stream, buffer, readBuf, ct).ConfigureAwait(false);
        if (start is null || start.Value.Command != RongtaTcpProtocol.CmdStart)
        {
            // 2026-09-21: раньше при несовпадении протокол молча отказывался ("не прислала
            // стартовый пакет"), не оставляя следа, ЧТО реально пришло по сети. Это первая и
            // единственная точка, где можно увидеть живые байты от весов другого производителя
            // (VEVOR TM-30F и т.п.) — протокол подтверждён только для Rongta RLS1000, для
            // остальных весов это лучшее доступное предположение (см. RongtaTcpProtocol).
            // Печатаем сырой буфер как есть — если сервер прислал что-то, а не просто оборвал
            // соединение, это даст реальные данные для подгонки протокола под конкретную модель.
            var raw = buffer.ToString();
            PosLogger.Log(
                $"Rongta TCP: не дождались стартового пакета 0201. Получено: '{raw}' (bytes={raw.Length}).",
                "SCALES");
            return RongtaTcpServerResult.Failed(
                "Весы не прислали ожидаемый стартовый пакет по протоколу Rongta RLS1000. " +
                (raw.Length > 0
                    ? $"Получены другие данные ({raw.Length} байт) — см. лог, это может означать, что у этой модели другой протокол."
                    : "Соединение было принято, но данных не поступило — проверьте, что весы настроены слать именно сюда."));
        }

        PosLogger.Log($"Rongta TCP: получен стартовый пакет 0201, отвечаем ACK.", "SCALES");
        await WriteAsync(stream, RongtaTcpProtocol.BuildAck(RongtaTcpProtocol.CmdStart), ct).ConfigureAwait(false);

        var sent = 0;
        foreach (var product in products)
        {
            var packet = RongtaTcpProtocol.BuildPluRecord(product.Plu, product.Name, product.PriceSom);
            await WriteAsync(stream, packet, ct).ConfigureAwait(false);
            sent++;
        }

        return RongtaTcpServerResult.Sent(sent);
    }

    private static async Task<(string Command, string Data)?> ReadPacketAsync(
        NetworkStream stream, StringBuilder buffer, byte[] readBuf, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var parsed = RongtaTcpProtocol.TryParsePacket(buffer);
            if (parsed is not null)
                return parsed;

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeout);
            int read;
            try
            {
                read = await stream.ReadAsync(readBuf, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read <= 0)
                return null;

            buffer.Append(Encoding.ASCII.GetString(readBuf, 0, read));
        }

        return null;
    }

    private static Task WriteAsync(NetworkStream stream, string packet, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(packet);
        return stream.WriteAsync(bytes, ct).AsTask();
    }
}

public sealed class RongtaTcpServerResult
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }
    public int RecordsSent { get; private init; }

    public static RongtaTcpServerResult Sent(int count) => new() { IsSuccess = true, RecordsSent = count };
    public static RongtaTcpServerResult Failed(string message) => new() { IsSuccess = false, ErrorMessage = message };
}
