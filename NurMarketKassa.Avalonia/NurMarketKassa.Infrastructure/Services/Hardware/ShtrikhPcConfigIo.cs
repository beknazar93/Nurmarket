using System.Globalization;
using System.Net.Sockets;

namespace NurMarketKassa.Services.Hardware;

public enum ShtrikhPcConfigIoStatus
{
    Ok,
    NoFolder,
    FolderUnavailable,
    /// <summary>В папке нет ни Config.txt, ни Export\Export.txt.</summary>
    NotFound,
    NoAccess,
    NoConnection,
    BadCredentials,
    Unsupported,
    /// <summary>На весах выключен импорт конфигурации (параметр 814 ≠ 1): файл с «&lt;N» весы не применят.</summary>
    ImportDisabled,
    /// <summary>Прежний файл импорта ещё не принят весами.</summary>
    Pending,
    NothingToChange,
    Failed,
}

public sealed record ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus Status, ShtrikhPcConfigData? Data = null, string? Text = null, string? Path = null, DateTime? FileTime = null, string? Detail = null);

/// <summary>2026-11-06: чтение настроек весов РС-200 (Config.txt в папке программы весов или Export\Export.txt) и запись изменённых параметров файлом импорта. Запись — только по
/// <see cref="ShtrikhPcConfig.Diff"/> (не отмеченное владельцем не отправляется) и только при включённом импорте конфигурации (параметр 814 = 1). Резервная копия прочитанного
/// Config.txt — в папке данных кассы (<see cref="ShtrikhPcConfigBackup"/>).</summary>
public static class ShtrikhPcConfigIo
{
    private static ShtrikhPcConfigIoStatus Map(ShtrikhPcNetStatus net) => net switch
    {
        ShtrikhPcNetStatus.NoConnection => ShtrikhPcConfigIoStatus.NoConnection,
        ShtrikhPcNetStatus.BadCredentials => ShtrikhPcConfigIoStatus.BadCredentials,
        ShtrikhPcNetStatus.AccessDenied => ShtrikhPcConfigIoStatus.NoAccess,
        ShtrikhPcNetStatus.Unsupported => ShtrikhPcConfigIoStatus.Unsupported,
        _ => ShtrikhPcConfigIoStatus.Failed,
    };

    /// <summary>Где искать настройки в папке обмена: Config.txt рядом с программой весов (папка обмена — внутри неё, обычно <c>…\Shared</c>), сама папка обмена и файл экспорта.</summary>
    public static IReadOnlyList<string> Candidates(ShtrikhPcSettings settings)
    {
        var folder = (settings.Folder ?? "").Trim().TrimEnd('\\', '/');
        var list = new List<string>();
        if (folder.Length == 0)
            return list;
        try
        {
            var parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent))
                list.Add(Path.Combine(parent, "Config.txt"));
        }
        catch (ArgumentException)
        {
            // странный путь — пропускаем «родителя»
        }
        list.Add(Path.Combine(folder, "Config.txt"));
        list.Add(ShtrikhPcImportWriter.ExportPath(settings));
        return list;
    }

    /// <summary>Читает конфигурацию с весов: берёт самый свежий из найденных файлов; сетевая папка — не дольше 3 с.</summary>
    public static async Task<ShtrikhPcConfigIoResult> ReadFromFolderAsync(ShtrikhPcSettings settings, CancellationToken ct = default)
    {
        settings.Normalize();
        if (settings.Folder.Length == 0)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NoFolder);
        var (net, netCode, lease) = await ShtrikhPcShare.OpenAsync(settings, ct).ConfigureAwait(false);
        if (net != ShtrikhPcNetStatus.Ok)
            return new ShtrikhPcConfigIoResult(Map(net), Detail: netCode.ToString(CultureInfo.InvariantCulture));
        using var leaseGuard = lease;
        var (reachable, exists) = await ShtrikhPcImportWriter.RunWithTimeoutAsync(() => Directory.Exists(settings.Folder), ShtrikhPcImportWriter.AvailabilityTimeout, ct).ConfigureAwait(false);
        if (!reachable || !exists)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.FolderUnavailable);
        var (done, best) = await ShtrikhPcImportWriter.RunWithTimeoutAsync(() =>
        {
            string? bestPath = null;
            var bestTime = DateTime.MinValue;
            foreach (var path in Candidates(settings))
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    var time = File.GetLastWriteTime(path);
                    if (bestPath is null || time > bestTime)
                    {
                        bestPath = path;
                        bestTime = time;
                    }
                }
                catch (IOException)
                {
                    // недоступный кандидат пропускаем
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            if (bestPath is null)
                return ((string?)null, DateTime.MinValue, (byte[]?)null, (string?)null);
            try
            {
                using var stream = new FileStream(bestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return (bestPath, bestTime, ms.ToArray(), (string?)null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (bestPath, bestTime, (byte[]?)null, ex.Message);
            }
        }, ShtrikhPcImportWriter.AvailabilityTimeout, ct).ConfigureAwait(false);
        if (!done)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.FolderUnavailable, Detail: "timeout");
        if (best.Item1 is null)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NotFound);
        if (best.Item3 is null)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NoAccess, Path: best.Item1, Detail: best.Item4);
        return FromBytes(best.Item3, best.Item1, best.Item2);
    }

    /// <summary>Читает Config.txt/Export.txt по выбранному вручную пути (файл с флешки или копия).</summary>
    public static ShtrikhPcConfigIoResult ReadFromFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return FromBytes(bytes, path, File.GetLastWriteTime(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Failed, Path: path, Detail: ex.Message);
        }
    }

    private static ShtrikhPcConfigIoResult FromBytes(byte[] bytes, string path, DateTime time)
    {
        var text = ShtrikhPcImportWriter.Decode(bytes);
        var data = ShtrikhPcConfig.Parse(text);
        return data.Count == 0
            ? new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NotFound, Text: text, Path: path, FileTime: time, Detail: "no <N lines")
            : new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Ok, data, text, path, time);
    }

    /// <summary>Записывает изменённые параметры файлом импорта в папку весов (атомарно) и кладёт флаг-файл. Без включённого импорта конфигурации (814 = 1 в <paramref name="baseline"/>)
    /// ничего не пишет. Прежний неотработанный файл не затирается, пока <paramref name="overwritePending"/> = false.</summary>
    public static async Task<ShtrikhPcConfigIoResult> WriteAsync(IReadOnlyList<ShtrikhPcChange> changes, ShtrikhPcConfigData baseline, ShtrikhPcSettings settings,
        bool overwritePending = false, CancellationToken ct = default)
    {
        settings.Normalize();
        if (changes.Count == 0)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NothingToChange);
        if (baseline.GetInt(814, 0) != 1)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.ImportDisabled);
        if (settings.Folder.Length == 0)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NoFolder);
        var importPath = ShtrikhPcImportWriter.ImportPath(settings);
        var flagPath = ShtrikhPcImportWriter.FlagPath(settings);
        var (net, netCode, lease) = await ShtrikhPcShare.OpenAsync(settings, ct).ConfigureAwait(false);
        if (net != ShtrikhPcNetStatus.Ok)
            return new ShtrikhPcConfigIoResult(Map(net), Detail: netCode.ToString(CultureInfo.InvariantCulture));
        using var leaseGuard = lease;
        var (reachable, exists) = await ShtrikhPcImportWriter.RunWithTimeoutAsync(() => Directory.Exists(settings.Folder), ShtrikhPcImportWriter.AvailabilityTimeout, ct).ConfigureAwait(false);
        if (!reachable || !exists)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.FolderUnavailable);
        if (!overwritePending)
        {
            var (checkedInTime, pending) = await ShtrikhPcImportWriter.RunWithTimeoutAsync(() => ShtrikhPcImportWriter.IsPending(importPath, flagPath), ShtrikhPcImportWriter.AvailabilityTimeout, ct).ConfigureAwait(false);
            if (!checkedInTime)
                return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.FolderUnavailable, Detail: "timeout"); // 2026-11-06: неизвестность не считаем «принято»
            if (pending)
                return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Pending, Path: importPath);
        }
        try
        {
            var bytes = ShtrikhPcImportWriter.Encode(ShtrikhPcConfig.BuildImportText(changes, settings));
            await Task.Run(() => ShtrikhPcImportWriter.WriteAtomic(importPath, bytes), ct).ConfigureAwait(false);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flagPath)!);
                using var _ = new FileStream(flagPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                ShtrikhPcImportWriter.TraceFile(flagPath);
            }, ct).ConfigureAwait(false);
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Ok, Path: importPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NoAccess, Path: importPath, Detail: ex.Message);
        }
        catch (Exception ex)
        {
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Failed, Path: importPath, Detail: ex.Message);
        }
    }

    /// <summary>«Сохранить файл настроек»: тот же файл импорта, но в выбранное место (флешка для ручного «Импорт данных»); флаг-файла и проверок общей папки нет.</summary>
    public static ShtrikhPcConfigIoResult SaveToFile(IReadOnlyList<ShtrikhPcChange> changes, ShtrikhPcSettings settings, string targetPath)
    {
        settings.Normalize();
        if (changes.Count == 0)
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NothingToChange);
        try
        {
            ShtrikhPcImportWriter.WriteAtomic(targetPath, ShtrikhPcImportWriter.Encode(ShtrikhPcConfig.BuildImportText(changes, settings)));
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Ok, Path: targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.Failed, Path: targetPath, Detail: ex.Message);
        }
    }
}

/// <summary>2026-11-06: резервные копии прочитанного Config.txt перед записью — в папке данных кассы (<c>scale-config-backups</c>), хранится 20 последних.</summary>
public static class ShtrikhPcConfigBackup
{
    public const string FolderName = "scale-config-backups";
    private const int Keep = 20;

    public static string DefaultDir => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, FolderName);

    /// <summary>Сохраняет текст конфигурации как <c>Config-ГГГГММДД-ЧЧММСС.txt</c> (Windows-1251). Возвращает путь.</summary>
    public static string Save(string text, DateTime now, string? dir = null)
    {
        dir ??= DefaultDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Config-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, "Config-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + (n++).ToString(CultureInfo.InvariantCulture) + ".txt");
        File.WriteAllBytes(path, ShtrikhPcImportWriter.Encode(text));
        try
        {
            foreach (var old in Directory.GetFiles(dir, "Config-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
                File.Delete(old);
        }
        catch (IOException)
        {
            // чистка — удобство, не условие
        }
        return path;
    }

    /// <summary>Самая свежая копия или null.</summary>
    public static string? Latest(string? dir = null)
    {
        dir ??= DefaultDir;
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "Config-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>Итог проверки сетевого протокола весов (только чтение).</summary>
public sealed record ShtrikhPcProbeResult(bool Answered, string Transport, ShtrikhDeviceInfo? Info, string Message);

/// <summary>2026-11-06 (Паспорт версий 2.1.3.3/2.1.3.4: «загрузка весов через драйвер», команды «установка даты/времени» и «запрос типа устройства»; PCScale.exe слушает порт 22222): ОСТОРОЖНАЯ
/// проверка гипотезы «РС-200 отвечает на протокол Штрих-ПРИНТ». Отправляется ТОЛЬКО команда FCh «Получить тип устройства» (пароль не нужен; чтения времени в протоколе нет) — сначала по TCP,
/// затем по UDP, каждая попытка не дольше 1,2 с, итого не больше ~3 с. Никаких записывающих команд, пароль не подбирается.</summary>
public static class ShtrikhPcProtocolProbe
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>Адрес весов: заданный в настройках либо IP из папки обмена вида <c>\\192.168.0.200\Shared</c>; иначе null.</summary>
    public static string? ResolveHost(ShtrikhPcSettings settings)
    {
        var host = (settings.ProtocolHost ?? "").Trim();
        if (host.Length == 0 && ShtrikhPcShare.IsUnc(settings.Folder))
            host = ShtrikhPcShare.Host(settings.Folder);
        return System.Net.IPAddress.TryParse(host, out _) ? host : null;
    }

    public static async Task<ShtrikhPcProbeResult> ProbeAsync(string host, int port, CancellationToken ct = default)
    {
        if (!System.Net.IPAddress.TryParse(host, out var address))
            return new ShtrikhPcProbeResult(false, "", null, Tr.T("Не задан IP весов (проверка протокола нужна адрес вида 192.168.0.200).", "Таразанын IP'си коюлган эмес (протоколду текшерүүгө 192.168.0.200 түрүндөгү дарек керек).", "The scale IP is not set (the protocol check needs an address like 192.168.0.200).", "Tartı IP'si girilmemiş (protokol kontrolü için 192.168.0.200 gibi bir adres gerekir).", "Tarozi IP'si kiritilmagan (protokolni tekshirish uchun 192.168.0.200 kabi manzil kerak)."));
        var tcp = await TryTcpAsync(address, port, ct).ConfigureAwait(false);
        if (tcp is not null)
            return Answered(tcp, "TCP", port);
        var udp = await TryUdpAsync(host, port, ct).ConfigureAwait(false);
        if (udp is not null)
            return Answered(udp, "UDP", port);
        return new ShtrikhPcProbeResult(false, "", null, Tr.T($"Нет ответа по порту {port} — работаем через файлы.", $"{port} портунда жооп жок — файлдар аркылуу иштейбиз.", $"No answer on port {port} — we work through files.", $"{port} portunda yanıt yok — dosyalar üzerinden çalışıyoruz.", $"{port} portida javob yo‘q — fayllar orqali ishlaymiz."));
    }

    private static ShtrikhPcProbeResult Answered(ShtrikhDeviceInfo info, string transport, int port)
    {
        var name = string.IsNullOrWhiteSpace(info.Name) ? "—" : info.Name.Trim();
        var text = Tr.T($"Весы ответили по протоколу ({transport}, порт {port}): {name}; тип устройства {info.DeviceType}/{info.DeviceSubType}, модель {info.Model}, версия протокола {info.ProtocolVersion}.{info.ProtocolSubVersion}.",
            $"Тараза протокол боюнча жооп берди ({transport}, {port} порту): {name}; түзмөк түрү {info.DeviceType}/{info.DeviceSubType}, модель {info.Model}, протокол версиясы {info.ProtocolVersion}.{info.ProtocolSubVersion}.",
            $"The scale answered by protocol ({transport}, port {port}): {name}; device type {info.DeviceType}/{info.DeviceSubType}, model {info.Model}, protocol version {info.ProtocolVersion}.{info.ProtocolSubVersion}.",
            $"Tartı protokolle yanıt verdi ({transport}, port {port}): {name}; cihaz türü {info.DeviceType}/{info.DeviceSubType}, model {info.Model}, protokol sürümü {info.ProtocolVersion}.{info.ProtocolSubVersion}.",
            $"Tarozi protokol bo‘yicha javob berdi ({transport}, {port} port): {name}; qurilma turi {info.DeviceType}/{info.DeviceSubType}, model {info.Model}, protokol versiyasi {info.ProtocolVersion}.{info.ProtocolSubVersion}.");
        return new ShtrikhPcProbeResult(true, transport, info, text);
    }

    private static async Task<ShtrikhDeviceInfo?> TryTcpAsync(System.Net.IPAddress address, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AttemptTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var frame = ShtrikhPrintProtocol.BuildFrame(ShtrikhPrintProtocol.CmdGetDeviceType, Array.Empty<byte>(), sync: false, dynamicLength: false);
            await stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cts.Token).ConfigureAwait(false);
                if (read <= 0)
                    break;
                length += read;
                var info = ParseInfo(buffer.AsSpan(0, length));
                if (info is not null)
                    return info;
            }
            return ParseInfo(buffer.AsSpan(0, length));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // таймаут попытки
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<ShtrikhDeviceInfo?> TryUdpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var service = new ShtrikhPrintLanScaleService(host, port, null, (int)AttemptTimeout.TotalMilliseconds, 1);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AttemptTimeout + TimeSpan.FromMilliseconds(300));
            return await service.GetDeviceInfoAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is ShtrikhScaleException or SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Разбирает ответ FCh так же, как <see cref="ShtrikhPrintLanScaleService.GetDeviceInfoAsync"/>; одиночный ACK в начале отбрасывается.</summary>
    internal static ShtrikhDeviceInfo? ParseInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length > 1 && data[0] == ShtrikhPrintProtocol.Ack)
            data = data[1..];
        if (!ShtrikhPrintProtocol.TryParseFrame(data, out var response) || response.Command != ShtrikhPrintProtocol.CmdGetDeviceType)
            return null;
        var payload = response.Payload;
        if (payload.Length < 6)
            return null;
        var name = payload.Length > 6 ? ShtrikhPrintProtocol.DecodeText(payload.AsSpan(6)) : "";
        return new ShtrikhDeviceInfo(payload[0], payload[1], payload[2], payload[3], payload[4], payload[5], name);
    }
}
