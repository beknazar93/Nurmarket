using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Драйвер весов ШТРИХ-ПРИНТ по Ethernet (витая пара, LAN) — прямая связь кассы с
/// весами, без участия сервера NurCRM. Кадрирование и упаковка полей — в
/// <see cref="ShtrikhPrintProtocol"/>, здесь только транспорт и операции.
///
/// ТРАНСПОРТ. По спецификации сообщение передаётся внутри UDP-пакета, и «в связи с
/// использованием UDP ответственность за надёжность передачи данных... возлагается на
/// программное обеспечение хоста» — то есть повторы и таймауты обязаны быть здесь.
/// Реализовано: таймаут на ответ, до <see cref="_retries"/> повторов, отбрасывание чужих и
/// устаревших ответов по эхо кода команды (иначе ответ на повтор предыдущей команды был бы
/// принят за ответ на текущую — самая частая ошибка при работе поверх UDP).
///
/// РЕЖИМ СИНХРОНИЗАЦИИ (кадр со STE вместо STX) НЕ используется. Он нужен командам, для
/// которых повтор даёт другой результат — «например, установка тары подряд два раза». Все
/// команды этого драйвера идемпотентны: запись ПЛУ с теми же данными, чтение, запрос
/// состояния. Поэтому обычный повтор безопасен, а лишнего состояния «весы захвачены хостом»
/// (ответ BUSY другим хостам) мы не создаём.
///
/// ЧТО НЕ ПРОВЕРЕНО НА ЖИВОМ ЖЕЛЕЗЕ: всё. Код написан строго по спецификации v1.6 и сходится
/// с ней по длинам всех команд, но у автора нет весов ШТРИХ-ПРИНТ для проверки. Первым делом
/// на реальных весах надо выполнить <see cref="TestConnectionAsync"/> — она использует только
/// команды без пароля (FCh, 11h, 13h) и по звуковому сигналу сразу видно, что это те весы.
/// Отдельно см. оговорку про номер UDP-порта у <see cref="DefaultPort"/>.</summary>
public sealed class ShtrikhPrintLanScaleService : IDisposable
{
    /// <summary>Номер UDP-порта в спецификации НЕ указан ни разу — он задаётся в системном
    /// меню весов. 1111 подтверждён на живых весах владельца (2026-09-22); раньше здесь стояло
    /// угаданное 4001, и связи с этим значением не было. Если связи нет — первым делом сверьте
    /// порт с системным меню весов, а не ищите ошибку в протоколе.</summary>
    public const int DefaultPort = 1111;

    /// <summary>Пароль администратора весов. 0030 — то, что реально стоит на весах владельца
    /// (2026-09-22) и обычное заводское значение у ШТРИХ-М; «0000» не подошло и привело к
    /// блокировке по числу неудачных попыток. Если пароль меняли — он в меню весов.</summary>
    public const string DefaultPassword = "0030";

    private readonly IPEndPoint _endPoint;
    private readonly byte[] _password;
    private readonly int _timeoutMs;
    private readonly int _retries;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UdpClient? _client;
    private bool _disposed;

    /// <param name="host">IP-адрес весов в локальной сети.</param>
    /// <param name="port">UDP-порт из системного меню весов (см. <see cref="DefaultPort"/>).</param>
    /// <param name="password">Пароль администратора — 4 цифры.</param>
    /// <param name="timeoutMs">Ожидание ответа. Спецификация требует не меньше 1 с на реакцию
    /// весов на запрос ENQ, поэтому меньше 1000 мс ставить не стоит.</param>
    /// <param name="retries">Сколько раз повторить команду, если ответ не пришёл.</param>
    public ShtrikhPrintLanScaleService(string host, int port = DefaultPort, string? password = DefaultPassword,
        int timeoutMs = 1500, int retries = 3)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Не задан IP-адрес весов.", nameof(host));
        if (!IPAddress.TryParse(host.Trim(), out var address))
            throw new ArgumentException($"«{host}» не похоже на IP-адрес весов.", nameof(host));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Порт должен быть в диапазоне 1..65535.");

        _endPoint = new IPEndPoint(address, port);
        _password = ShtrikhPrintProtocol.EncodePassword(password);
        _timeoutMs = Math.Max(300, timeoutMs);
        _retries = Math.Clamp(retries, 1, 10);
    }

    public string Host => _endPoint.Address.ToString();
    public int Port => _endPoint.Port;

    // ---------------------------------------------------------------- транспорт

    private UdpClient EnsureClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
            return _client;

        // Не Connect(): так мы видим адрес отправителя и можем отбросить пакеты не от весов.
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.ReceiveTimeout = _timeoutMs;
        _client = client;
        return client;
    }

    /// <summary>Отправляет команду и дожидается ответа на НЕЁ. Ответы на предыдущие команды
    /// (эхо другого кода) и пакеты с посторонних адресов отбрасываются — при работе поверх
    /// UDP с повторами они неизбежны.</summary>
    public async Task<ShtrikhResponse> SendAsync(byte command, byte[]? parameters = null,
        bool dynamicLength = false, CancellationToken ct = default)
    {
        var frame = ShtrikhPrintProtocol.BuildFrame(command, parameters ?? Array.Empty<byte>(),
            sync: false, dynamicLength: dynamicLength);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            Exception? lastError = null;

            for (var attempt = 1; attempt <= _retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await client.SendAsync(frame, frame.Length, _endPoint).ConfigureAwait(false);

                    var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(remaining);

                        UdpReceiveResult received;
                        try
                        {
                            received = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            break; // истёк таймаут этой попытки — уходим на повтор
                        }

                        if (!received.RemoteEndPoint.Address.Equals(_endPoint.Address))
                            continue; // пакет не от наших весов

                        var data = received.Buffer;
                        if (data.Length == 0)
                            continue;

                        // Служебные однобайтовые сообщения от весов.
                        if (data.Length == 1)
                        {
                            if (data[0] == ShtrikhPrintProtocol.Nak)
                            {
                                // «периферийным устройством используется для информирования
                                // хоста о неверном формате сообщения для текущего состояния».
                                throw new ShtrikhScaleException(
                                    "Весы ответили NAK — команда не принята в текущем состоянии весов. " +
                                    "Проверьте, не открыто ли на весах системное меню.");
                            }
                            continue; // ACK на ENQ и прочее — не ответ на команду
                        }

                        if (data[0] == ShtrikhPrintProtocol.Busy)
                        {
                            throw new ShtrikhScaleException(
                                "Весы заняты другим хостом (ответ BUSY): с ними уже работает другая программа " +
                                "или другая касса. Дождитесь окончания её работы.");
                        }

                        if (!ShtrikhPrintProtocol.TryParseFrame(data, out var response))
                            continue;

                        if (response.Command != command)
                            continue; // ответ на прошлую команду, пришедший с опозданием

                        return response;
                    }

                    lastError = new ShtrikhScaleException(
                        $"Весы {Host}:{Port} не ответили на команду {command:X2}h за {_timeoutMs} мс.");
                }
                catch (ShtrikhScaleException)
                {
                    throw; // содержательный ответ весов — повторять бессмысленно
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                }
            }

            throw lastError is ShtrikhScaleException known
                ? known
                : new ShtrikhScaleException(
                    $"Нет связи с весами {Host}:{Port} после {_retries} попыток. " +
                    "Проверьте кабель, IP-адрес и номер порта в системном меню весов.", lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>То же, но сразу бросает исключение при ненулевом коде ошибки — для команд,
    /// у которых частичный успех смысла не имеет.</summary>
    private async Task<ShtrikhResponse> SendCheckedAsync(byte command, byte[]? parameters = null,
        bool dynamicLength = false, CancellationToken ct = default)
    {
        var response = await SendAsync(command, parameters, dynamicLength, ct).ConfigureAwait(false);
        if (!response.Ok)
            throw new ShtrikhScaleException($"Весы отклонили команду {command:X2}h: {response.ErrorText}.", response.ErrorCode);
        return response;
    }

    private byte[] WithPassword(params byte[] tail)
    {
        var parameters = new byte[4 + tail.Length];
        _password.CopyTo(parameters, 0);
        tail.CopyTo(parameters, 4);
        return parameters;
    }

    // ---------------------------------------------------------------- опознание и состояние

    /// <summary>Однобайтовый запрос ENQ — самая дешёвая проверка «весы вообще живы».
    /// По спецификации NAK в ответ означает, что весы ждут очередную команду (то есть всё
    /// хорошо), ACK — что готовится ответ, отсутствие ответа — нет связи.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            var probe = new[] { ShtrikhPrintProtocol.Enq };
            await client.SendAsync(probe, probe.Length, _endPoint).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeoutMs);
            try
            {
                var received = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                return received.RemoteEndPoint.Address.Equals(_endPoint.Address);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>FCh «Получить тип устройства» — пароля не требует.</summary>
    public async Task<ShtrikhDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var response = await SendCheckedAsync(ShtrikhPrintProtocol.CmdGetDeviceType, null, false, ct).ConfigureAwait(false);
        var payload = response.Payload;
        if (payload.Length < 6)
            throw new ShtrikhScaleException("Весы вернули слишком короткий ответ на запрос типа устройства.");

        var name = payload.Length > 6
            ? ShtrikhPrintProtocol.DecodeText(payload.AsSpan(6))
            : "";

        return new ShtrikhDeviceInfo(
            DeviceType: payload[0],
            DeviceSubType: payload[1],
            ProtocolVersion: payload[2],
            ProtocolSubVersion: payload[3],
            Model: payload[4],
            Language: payload[5],
            Name: name);
    }

    /// <summary>11h «Запрос состояния весов» — пароля не требует. Отдаёт размер таблицы
    /// товаров, положение десятичной точки, режим и состояние принтера.</summary>
    public async Task<ShtrikhScaleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(ShtrikhPrintProtocol.CmdGetStatus, null, false, ct).ConfigureAwait(false);
        if (!response.HasStatusPayload)
            throw new ShtrikhScaleException($"Весы не отдали состояние: {response.ErrorText}.", response.ErrorCode);

        var status = ShtrikhScaleStatus.Parse(response.Payload)
            ?? throw new ShtrikhScaleException("Весы вернули неполный пакет состояния.");

        if (response.ErrorCode == 165)
            PosLogger.Log("Весы: сбой часов (код 165) — дата и время на этикетках будут неверными.", "SCALES");
        else if (response.ErrorCode == 168)
            PosLogger.Log("Весы: ошибка структуры базы (код 168) — таблицу товаров, возможно, нужно очистить.", "SCALES");

        return status;
    }

    /// <summary>12h «Запрос текущего режима весов» — пароля не требует. Это единственная
    /// команда, которая выполняется во время очистки базы, поэтому ей же отслеживают
    /// окончание <see cref="ClearDatabaseAsync"/>.</summary>
    public async Task<(int Mode, int SubMode)> GetModeAsync(CancellationToken ct = default)
    {
        var response = await SendCheckedAsync(ShtrikhPrintProtocol.CmdGetMode, null, false, ct).ConfigureAwait(false);
        if (response.Payload.Length < 3)
            throw new ShtrikhScaleException("Весы вернули слишком короткий ответ на запрос режима.");

        var mode = (int)ShtrikhPrintProtocol.ReadLittleEndian(response.Payload, 0, 2);
        return (mode, response.Payload[2]);
    }

    /// <summary>13h «Гудок» — пароля не требует. Лучший способ убедиться, что отвечают именно
    /// те весы, которые стоят перед кассиром, а не другое устройство в сети.</summary>
    public Task BeepAsync(CancellationToken ct = default) =>
        SendCheckedAsync(ShtrikhPrintProtocol.CmdBeep, null, false, ct);

    /// <summary>1Ah «Получить заводской номер».</summary>
    public async Task<int> GetSerialNumberAsync(CancellationToken ct = default)
    {
        var response = await SendCheckedAsync(ShtrikhPrintProtocol.CmdGetSerial, WithPassword(), false, ct).ConfigureAwait(false);
        return response.Payload.Length < 2 ? 0 : (int)ShtrikhPrintProtocol.ReadLittleEndian(response.Payload, 0, 2);
    }

    /// <summary>D0h «Запрос макс. количества ПЛУ» — верхняя граница номеров ПЛУ.</summary>
    public async Task<int> GetMaxPluAsync(CancellationToken ct = default)
    {
        var response = await SendCheckedAsync(ShtrikhPrintProtocol.CmdGetMaxPlu, WithPassword(), false, ct).ConfigureAwait(false);
        return response.Payload.Length < 2 ? 0 : (int)ShtrikhPrintProtocol.ReadLittleEndian(response.Payload, 0, 2);
    }

    /// <summary>4Ah «Запрос состояния печатающего устройства» — есть ли бумага, закрыта ли
    /// головка. Стоит показать кассиру до массовой печати этикеток.</summary>
    public async Task<byte> GetPrinterStateAsync(CancellationToken ct = default)
    {
        var response = await SendCheckedAsync(ShtrikhPrintProtocol.CmdGetPrinterStatus, WithPassword(), false, ct).ConfigureAwait(false);
        return response.Payload.Length < 1 ? (byte)0 : response.Payload[0];
    }

    /// <summary>Проверка связи для кнопки «Проверить весы»: опознаёт устройство, читает
    /// состояние и подаёт звуковой сигнал. Используются только команды без пароля, поэтому
    /// она работает даже если пароль администратора изменён и забыт.</summary>
    public async Task<string> TestConnectionAsync(bool beep = true, CancellationToken ct = default)
    {
        var info = await GetDeviceInfoAsync(ct).ConfigureAwait(false);
        if (!info.IsScale)
            throw new ShtrikhScaleException(
                $"По адресу {Host}:{Port} отвечает не весы, а другое устройство (тип {info.DeviceType}).");

        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (beep)
        {
            try
            {
                await BeepAsync(ct).ConfigureAwait(false);
            }
            catch (ShtrikhScaleException)
            {
                // Гудок — приятная мелочь, а не условие успеха: на некоторых исполнениях
                // звук выключен параметром «Звук» (2Ah), и команда вернёт ошибку.
            }
        }

        return $"{info} · {status.HardwareVariantName}, ПО {status.SoftwareVersion}, " +
               $"таблица товаров {status.ProductTableSize}, {status.DescribeBusyReason()}";
    }

    // ---------------------------------------------------------------- работа с ПЛУ

    /// <summary>56h «Управление быстрой загрузкой». Включённый режим ускоряет заливку базы;
    /// спецификация предупреждает, что команда «также осуществляет блокирование или
    /// разблокирование расчета веса» — то есть во время заливки весами пользоваться нельзя,
    /// и выключить режим обязательно.</summary>
    public Task SetFastLoadAsync(bool enabled, CancellationToken ct = default) =>
        SendCheckedAsync(ShtrikhPrintProtocol.CmdFastLoad, WithPassword((byte)(enabled ? 1 : 0)), false, ct);

    /// <summary>57h «Записать ПЛУ расширенного формата» — одна запись.</summary>
    public Task WritePluAsync(ShtrikhPluRecord plu, CancellationToken ct = default)
    {
        var record = ShtrikhPrintProtocol.EncodePluRecord(plu);
        return SendCheckedAsync(ShtrikhPrintProtocol.CmdWritePluExtended, WithPassword(record), false, ct);
    }

    /// <summary>55h «Записать блок ПЛУ расширенного формата» — от 1 до 6 записей одним
    /// пакетом. В байт длины кадра идёт FFh (маркер динамической длины), как требует
    /// примечание 1 к команде.</summary>
    public async Task WritePluBlockAsync(IReadOnlyList<ShtrikhPluRecord> plus, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plus);
        if (plus.Count is 0 or > ShtrikhPrintProtocol.MaxPlusPerBlock)
            throw new ArgumentOutOfRangeException(nameof(plus), plus.Count,
                $"В одном блоке может быть от 1 до {ShtrikhPrintProtocol.MaxPlusPerBlock} записей ПЛУ.");

        var parameters = new byte[4 + 1 + (plus.Count * ShtrikhPrintProtocol.PluRecordLength)];
        _password.CopyTo(parameters, 0);
        parameters[4] = (byte)plus.Count;

        var offset = 5;
        foreach (var plu in plus)
        {
            ShtrikhPrintProtocol.EncodePluRecord(plu).CopyTo(parameters, offset);
            offset += ShtrikhPrintProtocol.PluRecordLength;
        }

        await SendCheckedAsync(ShtrikhPrintProtocol.CmdWritePluBlockExtended, parameters,
            dynamicLength: true, ct).ConfigureAwait(false);
    }

    /// <summary>58h «Получить ПЛУ расширенного формата». Возвращает null, если ячейка пуста
    /// (весы отвечают кодом 140 «Пустое ПЛУ») — это не ошибка связи.</summary>
    public async Task<ShtrikhPluRecord?> ReadPluAsync(int pluNumber, CancellationToken ct = default)
    {
        var tail = new byte[2];
        ShtrikhPrintProtocol.WriteLittleEndian(tail, pluNumber, 2);

        var response = await SendAsync(ShtrikhPrintProtocol.CmdReadPluExtended, WithPassword(tail), false, ct).ConfigureAwait(false);
        if (response.ErrorCode is 140 or 156)
            return null; // пустое ПЛУ / ПЛУ не найдено
        if (!response.Ok)
            throw new ShtrikhScaleException($"Не удалось прочитать ПЛУ {pluNumber}: {response.ErrorText}.", response.ErrorCode);

        return ShtrikhPrintProtocol.DecodePluRecord(pluNumber, response.Payload);
    }

    /// <summary>54h «Очистить ПЛУ» — освобождает одну ячейку.</summary>
    public Task ClearPluAsync(int pluNumber, CancellationToken ct = default)
    {
        var tail = new byte[2];
        ShtrikhPrintProtocol.WriteLittleEndian(tail, pluNumber, 2);
        return SendCheckedAsync(ShtrikhPrintProtocol.CmdClearPlu, WithPassword(tail), false, ct);
    }

    /// <summary>5Ah «Получить номер ПЛУ по коду товара». null — товара с таким кодом нет.</summary>
    public async Task<int?> FindPluByProductCodeAsync(int productCode, CancellationToken ct = default)
    {
        var tail = new byte[4];
        ShtrikhPrintProtocol.WriteLittleEndian(tail, productCode, 4);

        var response = await SendAsync(ShtrikhPrintProtocol.CmdGetPluByProductCode, WithPassword(tail), false, ct).ConfigureAwait(false);
        if (response.ErrorCode is 156 or 139 or 130)
            return null;
        if (!response.Ok)
            throw new ShtrikhScaleException($"Поиск ПЛУ по коду {productCode}: {response.ErrorText}.", response.ErrorCode);

        return response.Payload.Length < 2 ? null : (int)ShtrikhPrintProtocol.ReadLittleEndian(response.Payload, 0, 2);
    }

    /// <summary>5Bh «Получить номер пустого ПЛУ».</summary>
    public async Task<int?> FindEmptyPluAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(ShtrikhPrintProtocol.CmdGetEmptyPlu, WithPassword(), false, ct).ConfigureAwait(false);
        if (!response.Ok)
            return null;
        return response.Payload.Length < 2 ? null : (int)ShtrikhPrintProtocol.ReadLittleEndian(response.Payload, 0, 2);
    }

    /// <summary>2Fh «Записать параметр "Доступ к ПЛУ"»: false — товар вызывается по номеру
    /// ПЛУ, true — по коду товара.</summary>
    public Task SetPluAccessByProductCodeAsync(bool byProductCode, CancellationToken ct = default) =>
        SendCheckedAsync(ShtrikhPrintProtocol.CmdSetPluAccess, WithPassword((byte)(byProductCode ? 1 : 0)), false, ct);

    /// <summary>52h «Записать сообщение» — одна строка сообщения (состав, условия хранения).
    /// Номер строки нумеруется с 1; сколько строк помещается, отдаёт команда D2h.</summary>
    public Task WriteMessageLineAsync(int messageNumber, int lineNumber, string text, CancellationToken ct = default)
    {
        var tail = new byte[2 + 1 + ShtrikhPrintProtocol.MessageLineLength];
        ShtrikhPrintProtocol.WriteLittleEndian(tail, messageNumber, 2);
        tail[2] = (byte)lineNumber;
        ShtrikhPrintProtocol.EncodeFixedText(text, ShtrikhPrintProtocol.MessageLineLength).CopyTo(tail, 3);
        return SendCheckedAsync(ShtrikhPrintProtocol.CmdWriteMessageLine, WithPassword(tail), false, ct);
    }

    /// <summary>18h «Очистить базу товаров и сообщений». Команда только ЗАПУСКАЕТ очистку:
    /// пока она идёт, весы принимают единственную команду 12h, по подрежиму которой и видно
    /// окончание. Поэтому здесь мы дожидаемся снятия битов подрежима 0 и 1, а биты 3 и 4
    /// означают, что очистка завершилась ошибкой.</summary>
    public async Task ClearDatabaseAsync(CancellationToken ct = default)
    {
        await SendCheckedAsync(ShtrikhPrintProtocol.CmdClearDatabase, WithPassword(), false, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct).ConfigureAwait(false);

            var (_, subMode) = await GetModeAsync(ct).ConfigureAwait(false);
            if ((subMode & 0x08) != 0)
                throw new ShtrikhScaleException("Весы сообщили об ошибке очистки базы товаров.");
            if ((subMode & 0x10) != 0)
                throw new ShtrikhScaleException("Весы сообщили об ошибке очистки итогов учёта.");
            if ((subMode & 0x03) == 0)
                return; // очистка закончилась
        }

        throw new ShtrikhScaleException("Весы не завершили очистку базы за 2 минуты.");
    }

    // ---------------------------------------------------------------- массовая выгрузка

    /// <summary>Выгружает список товаров на весы. Это основная операция для кассы.
    ///
    /// Порядок: проверяем, что весы свободны (иначе команды записи посыплются ошибкой 123)
    /// и что номера ПЛУ влезают в таблицу; включаем быструю загрузку; пишем блоками по 6
    /// (55h) и обязательно выключаем быструю загрузку в finally — иначе весы останутся с
    /// заблокированным расчётом веса и кассир решит, что они сломались.
    ///
    /// Если весы не понимают блочную команду (код 120), автоматически переходим на
    /// одиночную запись 57h: 55h появилась только в версии протокола 1.2.</summary>
    public async Task<ShtrikhUploadResult> UploadPlusAsync(
        IReadOnlyList<ShtrikhPluRecord> plus,
        IProgress<ShtrikhUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plus);
        if (plus.Count == 0)
            return new ShtrikhUploadResult(0, 0, Array.Empty<string>());

        progress?.Report(new ShtrikhUploadProgress(0, plus.Count, "Проверка связи с весами"));
        var status = await GetStatusAsync(ct).ConfigureAwait(false);

        if (!status.IsIdle)
            throw new ShtrikhScaleException(
                $"Весы сейчас заняты ({status.DescribeBusyReason()}). " +
                "Выйдите на весах в обычный режим и повторите выгрузку.");

        var tableSize = status.ProductTableSize > 0 ? status.ProductTableSize : await GetMaxPluAsync(ct).ConfigureAwait(false);
        var errors = new List<string>();
        var sent = 0;

        // Записи с номером ПЛУ вне таблицы весы отвергнут поштучно (ошибка 128); отсеиваем
        // их заранее, чтобы не ронять блок целиком из-за одной плохой строки.
        var accepted = new List<ShtrikhPluRecord>(plus.Count);
        foreach (var plu in plus)
        {
            if (plu.PluNumber < 1 || (tableSize > 0 && plu.PluNumber > tableSize))
            {
                errors.Add($"«{plu.Name1}»: номер ПЛУ {plu.PluNumber} вне таблицы товаров весов (1..{tableSize}).");
                continue;
            }
            if (plu.ProductCode is < 1 or > 999999)
            {
                errors.Add($"«{plu.Name1}»: код товара {plu.ProductCode} вне диапазона 1..999999.");
                continue;
            }
            accepted.Add(plu);
        }

        if (accepted.Count == 0)
            return new ShtrikhUploadResult(0, plus.Count - accepted.Count, errors);

        var fastLoadOn = false;
        try
        {
            try
            {
                await SetFastLoadAsync(true, ct).ConfigureAwait(false);
                fastLoadOn = true;
            }
            catch (ShtrikhScaleException ex)
            {
                // Ускорение, а не обязательное условие — идём дальше на обычной скорости.
                PosLogger.Log($"Весы: быстрая загрузка недоступна ({ex.Message}), пишем обычным темпом.", "SCALES");
            }

            var useBlocks = true;
            for (var i = 0; i < accepted.Count;)
            {
                ct.ThrowIfCancellationRequested();

                if (useBlocks)
                {
                    var take = Math.Min(ShtrikhPrintProtocol.MaxPlusPerBlock, accepted.Count - i);
                    var block = new List<ShtrikhPluRecord>(take);
                    for (var k = 0; k < take; k++)
                        block.Add(accepted[i + k]);

                    try
                    {
                        await WritePluBlockAsync(block, ct).ConfigureAwait(false);
                        sent += take;
                        i += take;
                        progress?.Report(new ShtrikhUploadProgress(sent, accepted.Count, "Выгрузка товаров"));
                        continue;
                    }
                    catch (ShtrikhScaleException ex) when (ex.ErrorCode is 120 or 121)
                    {
                        // 120 «Неизвестная команда» / 121 «Неверная длина» — весы старой
                        // версии протокола, блочной записи не знают. Переходим на 57h.
                        PosLogger.Log("Весы не поддерживают блочную запись ПЛУ (55h) — переключаюсь на одиночную (57h).", "SCALES");
                        useBlocks = false;
                        continue;
                    }
                    catch (ShtrikhScaleException ex) when (ShtrikhPasswordErrors.IsPasswordError(ex.ErrorCode))
                    {
                        // Пароль не подошёл — дальше идти нельзя: разбор блока поштучно дал бы
                        // ещё шесть попыток и приблизил блокировку весов.
                        throw ShtrikhPasswordErrors.Wrap(ex);
                    }
                    catch (ShtrikhScaleException ex)
                    {
                        // Блок отвергнут целиком — перепишем его поштучно, чтобы понять,
                        // какая именно запись виновата, и не потерять остальные пять.
                        PosLogger.Log($"Весы отвергли блок ПЛУ ({ex.Message}) — повторяю записи поодиночке.", "SCALES");
                        for (var k = 0; k < take; k++)
                        {
                            var plu = accepted[i + k];
                            try
                            {
                                await WritePluAsync(plu, ct).ConfigureAwait(false);
                                sent++;
                            }
                            catch (ShtrikhScaleException single)
                                when (ShtrikhPasswordErrors.IsPasswordError(single.ErrorCode))
                            {
                                throw ShtrikhPasswordErrors.Wrap(single);
                            }
                            catch (ShtrikhScaleException single)
                            {
                                errors.Add($"ПЛУ {plu.PluNumber} «{plu.Name1}»: {single.Message}");
                            }
                            progress?.Report(new ShtrikhUploadProgress(sent, accepted.Count, "Выгрузка товаров"));
                        }
                        i += take;
                        continue;
                    }
                }

                var record = accepted[i];
                try
                {
                    await WritePluAsync(record, ct).ConfigureAwait(false);
                    sent++;
                }
                catch (ShtrikhScaleException ex) when (ShtrikhPasswordErrors.IsPasswordError(ex.ErrorCode))
                {
                    throw ShtrikhPasswordErrors.Wrap(ex);
                }
                catch (ShtrikhScaleException ex)
                {
                    errors.Add($"ПЛУ {record.PluNumber} «{record.Name1}»: {ex.Message}");
                }
                i++;
                progress?.Report(new ShtrikhUploadProgress(sent, accepted.Count, "Выгрузка товаров"));
            }
        }
        finally
        {
            if (fastLoadOn)
            {
                try
                {
                    // Без своего токена: если выгрузку отменили, режим всё равно обязан быть
                    // снят, иначе весы останутся с заблокированным расчётом веса.
                    await SetFastLoadAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Весы: НЕ удалось выключить режим быстрой загрузки: {ex.Message}. " +
                                  "Расчёт веса может остаться заблокированным — перезапустите весы.", "SCALES");
                    errors.Add("Не удалось выключить режим быстрой загрузки на весах — перезапустите их питанием.");
                }
            }
        }

        var failed = plus.Count - sent;
        progress?.Report(new ShtrikhUploadProgress(sent, accepted.Count, "Готово"));
        return new ShtrikhUploadResult(sent, failed, errors);
    }

    /// <summary>Собирает запись ПЛУ из данных каталога кассы. Цена приходит в сомах и
    /// пересчитывается по положению десятичной точки, прочитанному с самих весов.</summary>
    public static ShtrikhPluRecord CreateRecord(
        int pluNumber,
        int productCode,
        string name,
        decimal priceSom,
        int decimalPointDigits,
        bool isPiece = false,
        int shelfLifeDays = 0,
        int tareGrams = 0,
        int messageNumber = 0)
    {
        // Наименование длиннее 28 байт переносим во вторую строку: обрезать название товара
        // молча — верный способ получить на этикетке «Колбаса варёная Докто».
        var name1 = name ?? "";
        var name2 = "";
        if (name1.Length > ShtrikhPrintProtocol.NameFieldLength)
        {
            var breakAt = name1.LastIndexOf(' ', Math.Min(ShtrikhPrintProtocol.NameFieldLength, name1.Length - 1));
            if (breakAt <= 0)
                breakAt = ShtrikhPrintProtocol.NameFieldLength;
            name2 = name1[breakAt..].Trim();
            name1 = name1[..breakAt].Trim();
        }

        return new ShtrikhPluRecord
        {
            PluNumber = pluNumber,
            ProductCode = productCode,
            Name1 = name1,
            Name2 = name2,
            PriceMde = ShtrikhPrintProtocol.PriceToMde(priceSom, decimalPointDigits),
            IsPiece = isPiece,
            ShelfLifeDays = shelfLifeDays,
            TareGrams = tareGrams,
            MessageNumber = messageNumber,
        };
    }

    // ---------------------------------------------------------------- поиск весов в сети

    /// <summary>Ищет весы в локальной подсети /24, опрашивая каждый адрес командой FCh
    /// «Получить тип устройства» — она не требует пароля. Нужна, когда владелец не знает
    /// IP-адрес весов: перебирать 254 адреса руками в окне настроек невозможно.
    /// Широковещательный поиск не подходит: по таблице «Поддерживаемые команды» FCh в режиме
    /// Broadcast не поддерживается, а сам приём широковещательных команд по умолчанию в
    /// весах выключен.</summary>
    public static async Task<IReadOnlyList<(string Host, ShtrikhDeviceInfo Info)>> DiscoverAsync(
        string subnetSampleIp,
        int port = DefaultPort,
        int perHostTimeoutMs = 300,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (!IPAddress.TryParse((subnetSampleIp ?? "").Trim(), out var sample))
            throw new ArgumentException("Нужен любой IP-адрес из нужной подсети, например адрес этого компьютера.", nameof(subnetSampleIp));

        var octets = sample.GetAddressBytes();
        if (octets.Length != 4)
            throw new ArgumentException("Поиск работает только для IPv4-сетей.", nameof(subnetSampleIp));

        var found = new List<(string Host, ShtrikhDeviceInfo Info)>();
        var gate = new SemaphoreSlim(32); // не заливаем сеть 254 одновременными пакетами
        var tasks = new List<Task>(254);
        var scanned = 0;

        for (var last = 1; last <= 254; last++)
        {
            var host = $"{octets[0]}.{octets[1]}.{octets[2]}.{last}";
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var probe = new ShtrikhPrintLanScaleService(host, port, DefaultPassword, perHostTimeoutMs, retries: 1);
                    var info = await probe.GetDeviceInfoAsync(ct).ConfigureAwait(false);
                    if (info.IsScale)
                    {
                        lock (found)
                            found.Add((host, info));
                    }
                }
                catch (Exception)
                {
                    // Молчание на этом адресе — норма: там просто нет весов.
                }
                finally
                {
                    progress?.Report(Interlocked.Increment(ref scanned));
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        found.Sort((a, b) => string.CompareOrdinal(a.Host, b.Host));
        return found;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client?.Dispose();
        _client = null;
        _gate.Dispose();
    }
}

/// <summary>Ошибка при работе с весами: либо нет связи, либо весы вернули код ошибки из
/// Приложения 5 спецификации.</summary>
/// <summary>Ошибки, связанные с паролем администратора весов. Вынесены отдельно, потому что
/// на них нельзя продолжать выгрузку: каждая следующая запись — это ещё одна попытка с тем же
/// неверным паролем, а весы считают такие попытки и после нескольких блокируют доступ совсем.
/// Живой случай 2026-09-22: список из 15 товаров дал 15 отказов подряд и довёл весы до
/// «Исчерпан лимит попыток обращения с неверным паролем».</summary>
internal static class ShtrikhPasswordErrors
{
    /// <summary>122 — «Неверный пароль», 170 — «Исчерпан лимит попыток обращения с неверным
    /// паролем» (Приложение 5 протокола).</summary>
    public static bool IsPasswordError(byte errorCode) => errorCode is 122 or 170;

    public static ShtrikhScaleException Wrap(ShtrikhScaleException source) =>
        new(
            source.ErrorCode == 170
                ? "Весы заблокировали доступ: исчерпан лимит попыток с неверным паролем. "
                  + "Выключите и включите весы, чтобы снять блокировку, и укажите правильный пароль "
                  + "администратора (заводской — 0000; если его меняли, посмотрите в меню весов). "
                  + "Выгрузка остановлена, чтобы не блокировать весы снова."
                : "Весы не приняли пароль администратора. Проверьте его в меню весов "
                  + "(заводской — 0000) и повторите. Выгрузка остановлена после первой же записи: "
                  + "весы считают неудачные попытки и после нескольких блокируют доступ.",
            source.ErrorCode);
}

public sealed class ShtrikhScaleException : Exception
{
    public ShtrikhScaleException(string message, byte errorCode = 0) : base(message) => ErrorCode = errorCode;

    public ShtrikhScaleException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Код ошибки весов (0 — ошибка связи, а не ответ весов).</summary>
    public byte ErrorCode { get; }
}
