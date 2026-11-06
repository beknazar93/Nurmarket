using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Товар для файла импорта весов «Штрих РС-200».</summary>
/// <param name="Code">«Код товара» БД весов 1…999999 — то же число весы печатают в штрихкоде этикетки и по нему касса находит товар.</param>
/// <param name="IsPiece">false — весовой (тип 0), true — штучный (тип 1).</param>
/// <param name="Barcode">Обычный штрихкод товара (13 цифр) — поле 2 БД весов, только для поиска на весах; пусто — «0».</param>
/// <param name="PictureSource">Откуда взять картинку (ссылка на фото товара); пусто — у товара нет фото, ресурс не создаётся.</param>
/// <param name="PictureResource">Код ресурса «картинка» (поле 31 строки товара и строка «&lt;R»); 0 — нет. Заполняет шаг картинок, не вызывающий.</param>
/// <param name="PictureFile">Путь картинки в строке ресурса (<c>Pictures/&lt;код&gt;.bmp</c>); заполняет шаг картинок.</param>
public sealed record ShtrikhPcItem(long Code, string Name, decimal Price, bool IsPiece, int ShelfLifeDays = 0, string? Barcode = null,
    string? PictureSource = null, long PictureResource = 0, string? PictureFile = null);

public enum ShtrikhPcWriteStatus
{
    Ok,
    NoFolder,
    FolderUnavailable,
    PendingNotAccepted,
    NothingToWrite,
    NoAccess,
    /// <summary>Ошибки Windows 53/67 или тайм-аут подключения к сетевой папке.</summary>
    NoConnection,
    /// <summary>Ошибки Windows 86/1326.</summary>
    BadCredentials,
    /// <summary>Сетевая папка (UNC) на системе без Windows.</summary>
    Unsupported,
    Failed,
}

public enum ShtrikhPcFolderCheck
{
    Ok,
    NoFolder,
    NotFound,
    NoAccess,
    NoConnection,
    BadCredentials,
    Unsupported,
    Failed,
}

public sealed record ShtrikhPcBuildResult(string Text, int Written, IReadOnlyList<string> Warnings, int QuickKeys = 0, IReadOnlyList<ShtrikhPcItem>? Items = null);

public sealed record ShtrikhPcWriteResult(ShtrikhPcWriteStatus Status, int Written, IReadOnlyList<string> Warnings, string ImportPath, string FlagPath, string? Detail,
    ShtrikhPcPictureStats? Pictures = null, ShtrikhPcScenarioResult? Scenario = null);

/// <summary>2026-11-06: писатель файла импорта для весов «ШТРИХ РС-200С2» (руководство администратора, п. 6.2.1 «Структура БД товаров», п. 8.1 «Импорт данных»).
/// Файл — текст в Windows-1251, строки через CRLF: строка 1 «##@@&amp;&amp;», строка 2 «#» (не обработан; после обработки весы меняют её на «@»), далее строки
/// «&lt;D…» с 34 полями через «;», в конце (по желанию) команда «$$$CLR» — очистка базы товаров перед загрузкой. Весы забирают файл сами, когда в общей
/// папке появляется флаг-файл (проверка раз в «Обновление» минут), либо вручную кнопкой «Импорт данных», и удаляют флаг-файл после обработки.
/// Запись атомарная: временный файл в той же папке, затем переименование; флаг-файл создаётся только после этого.
/// На живых весах не проверено: точный вид начала строки (см. <see cref="ShtrikhPcSettings.LineStyle"/>) и десятичный разделитель цены.</summary>
public static class ShtrikhPcImportWriter
{
    public const int FieldCount = 34;
    public const int MaxNameLength = 255;
    public const long MaxCode = 999_999;

    /// <summary>Предел поля цены БД весов — 7 знаков вместе с дробной частью (руководство, п. 6.2.1, прим. 2).</summary>
    private const long MaxPriceUnits = 9_999_999;

    public const string HeaderLine1 = "##@@&&";
    public const string ClearCommand = "$$$CLR";

    /// <summary>Сколько ждать ответа общей папки весов (сетевая папка выключенных весов зависает надолго).</summary>
    public static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(3);

    private static readonly Encoding Cp1251 = ResolveCp1251();

    private static Encoding ResolveCp1251()
    {
        // Провайдер кодовых страниц (Windows-1251) в .NET не подключён сам — регистрируем (повторная регистрация безвредна; на Linux/Android тоже).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    /// <summary>Байты файла из текста: Windows-1251 (кодировка файлов обмена весов, руководство п. 6.1).</summary>
    public static byte[] Encode(string text) => Cp1251.GetBytes(text);

    public static string Decode(byte[] bytes) => Cp1251.GetString(bytes);

    // ------------------------------------------------------------------ содержимое файла

    private static readonly Dictionary<char, string> Transliteration = new()
    {
        // В Windows-1251 нет киргизских и узбекских букв — заменяем близкими, иначе на весах будет «?».
        ['ү'] = "у", ['Ү'] = "У", ['ө'] = "о", ['Ө'] = "О", ['ң'] = "н", ['Ң'] = "Н",
        ['ў'] = "у", ['Ў'] = "У", ['қ'] = "к", ['Қ'] = "К", ['ғ'] = "г", ['Ғ'] = "Г", ['ҳ'] = "х", ['Ҳ'] = "Х",
        ['«'] = "\"", ['»'] = "\"", ['“'] = "\"", ['”'] = "\"", ['‘'] = "'", ['’'] = "'", ['№'] = "N",
    };

    /// <summary>Название для поля 3: без «;» (разделитель) и переводов строки, не длиннее 255 знаков, без букв вне Windows-1251.</summary>
    public static string CleanName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").Trim())
        {
            if (c == ';')
                sb.Append(',');
            else if (c is '\r' or '\n' or '\t' || char.IsControl(c))
                sb.Append(' ');
            else if (Transliteration.TryGetValue(c, out var replacement))
                sb.Append(replacement);
            else
                sb.Append(c);
        }
        var text = sb.ToString().Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd() : text;
    }

    /// <summary>Цена с нужным числом знаков (по умолчанию 2) и разделителем из настроек; null — не помещается в 7 знаков поля цены.</summary>
    public static string? FormatPrice(decimal price, int decimals, string separator)
    {
        decimals = Math.Clamp(decimals, 0, 3);
        var rounded = Math.Round(Math.Max(0m, price), decimals, MidpointRounding.AwayFromZero);
        var units = rounded * (decimal)Math.Pow(10, decimals);
        if (units > MaxPriceUnits)
            return null;
        var text = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return separator == "," ? text.Replace('.', ',') : text;
    }

    /// <summary>Одна строка БД товаров: «&lt;D» и 34 поля через «;» (руководство, п. 6.2.1).</summary>
    public static string BuildLine(ShtrikhPcItem item, ShtrikhPcSettings settings, string priceText)
    {
        var f = new string[FieldCount];
        // Незаполненные (резервные) поля пишутся одиночным пробелом, как в примере руководства (п. 8.1.3) и в файле Умара; поле 2 без штрихкода — «0».
        Array.Fill(f, " ");
        f[0] = item.Code.ToString(CultureInfo.InvariantCulture);          // 1 код товара
        f[1] = item.Barcode is { Length: 13 } bc && bc.All(char.IsDigit) ? bc : "0"; // 2 штрих-код (только для поиска; печатается рассчитанный весами)
        f[2] = CleanName(item.Name);                                       // 3 наименование
        f[3] = item.IsPiece ? "1" : "0";                                   // 4 тип: 0 весовой, 1 штучный
        f[4] = priceText;                                                  // 5 цена
        for (var i = 5; i <= 8; i++)                                       // 6–9 группы-владельцы 1–4 уровня: 0 — нет
            f[i] = "0";
        f[11] = Math.Clamp(item.ShelfLifeDays, 0, 99_999).ToString(CultureInfo.InvariantCulture); // 12 срок годности, суток
        f[13] = "0";                                                       // 14 формат этикетки: 0 — по умолчанию
        f[14] = "0";                                                       // 15 блокировка продажи: 0 — нет
        f[15] = "0";                                                       // 16 разрешение менять цену: 0 — нет
        for (var i = 22; i <= 24; i++)                                     // 23–25 масса тары 1–3
            f[i] = "0";
        for (var i = 25; i <= 33; i++)                                     // 26–34 коды ресурсов (тара, сообщение, картинка…): 0 — нет
            f[i] = "0";
        // 31 — код ресурса «файл картинки» (тип 3, строка «<R»); без картинки — 0. У весов на главном экране видны только товары с картинкой.
        if (item.PictureResource > 0 && !string.IsNullOrEmpty(item.PictureFile))
            f[30] = item.PictureResource.ToString(CultureInfo.InvariantCulture);
        var head = settings.LineStyle switch
        {
            ShtrikhPcSettings.LineGlued => "<D",
            ShtrikhPcSettings.LineSemicolon => "<D;",
            _ => "<D ",
        };
        return head + string.Join(";", f);
    }

    /// <summary>Строка БД ресурсов (руководство, п. 6.2.2): код ресурса, тип (3 — файл картинки), 19 пустых резервных полей, путь к файлу относительно папки обмена.
    /// Как в образце весов: <c>&lt;R  54002;3;;;;;;;;;;;;;;;;;;;;Pictures/48002.bmp</c>. Начало строки — как у «&lt;D» (<see cref="ShtrikhPcSettings.LineStyle"/>).</summary>
    public static string BuildResourceLine(long resourceCode, string path, ShtrikhPcSettings settings)
    {
        var f = new string[22];
        Array.Fill(f, "");
        f[0] = resourceCode.ToString(CultureInfo.InvariantCulture);
        f[1] = "3";
        f[21] = path;
        var head = settings.LineStyle switch
        {
            ShtrikhPcSettings.LineGlued => "<R",
            ShtrikhPcSettings.LineSemicolon => "<R;",
            _ => "<R ",
        };
        return head + string.Join(";", f);
    }

    /// <summary>Строка конфигурации «&lt;N» (руководство, п. 6.3): номер параметра, «;», значение. Начало строки — как у «&lt;D». Применяется весами только при включённом импорте конфигурации.</summary>
    public static string BuildConfigLine(int parameter, long value, ShtrikhPcSettings settings)
    {
        var head = settings.LineStyle switch
        {
            ShtrikhPcSettings.LineGlued => "<N",
            ShtrikhPcSettings.LineSemicolon => "<N;",
            _ => "<N ",
        };
        return head + parameter.ToString(CultureInfo.InvariantCulture) + ";" + value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Собирает весь текст файла. Повторные коды и коды вне 1…<paramref name="maxCode"/> отбрасываются с предупреждением.</summary>
    public static ShtrikhPcBuildResult Build(IEnumerable<ShtrikhPcItem> items, ShtrikhPcSettings settings, long maxCode = MaxCode)
    {
        settings.Normalize();
        maxCode = Math.Clamp(maxCode, 1, MaxCode);
        var warnings = new List<string>();
        var lines = new List<string>();
        var resourceLines = new List<string>();
        var writtenCodes = new List<long>();
        var acceptedItems = new List<ShtrikhPcItem>();
        var seen = new HashSet<long>();
        var seenResources = new HashSet<long>();
        foreach (var item in items)
        {
            if (item.Code < 1 || item.Code > maxCode)
            {
                warnings.Add(Tr.T($"«{item.Name}»: код {item.Code} вне диапазона 1…{maxCode} — не записан.",
                    $"«{item.Name}»: {item.Code} коду 1…{maxCode} диапазонунан тышкары — жазылган жок.",
                    $"“{item.Name}”: code {item.Code} is outside 1…{maxCode} — not written.",
                    $"«{item.Name}»: {item.Code} kodu 1…{maxCode} aralığı dışında — yazılmadı.",
                    $"«{item.Name}»: {item.Code} kodi 1…{maxCode} oralig‘idan tashqarida — yozilmadi."));
                continue;
            }
            if (!seen.Add(item.Code))
            {
                warnings.Add(Tr.T($"«{item.Name}»: код {item.Code} уже у другого товара — не записан.",
                    $"«{item.Name}»: {item.Code} коду башка товарда бар — жазылган жок.",
                    $"“{item.Name}”: code {item.Code} already belongs to another product — not written.",
                    $"«{item.Name}»: {item.Code} kodu başka bir üründe var — yazılmadı.",
                    $"«{item.Name}»: {item.Code} kodi boshqa tovarda bor — yozilmadi."));
                continue;
            }
            var price = FormatPrice(item.Price, settings.PriceDecimals, settings.DecimalSeparator);
            if (price is null)
            {
                warnings.Add(Tr.T($"«{item.Name}»: цена {item.Price} не помещается в поле цены весов (7 знаков) — не записан.",
                    $"«{item.Name}»: {item.Price} баасы таразанын баа талаасына (7 белги) батпайт — жазылган жок.",
                    $"“{item.Name}”: price {item.Price} does not fit the scale's price field (7 digits) — not written.",
                    $"«{item.Name}»: {item.Price} fiyatı tartının fiyat alanına (7 hane) sığmıyor — yazılmadı.",
                    $"«{item.Name}»: {item.Price} narxi tarozining narx maydoniga (7 raqam) sig‘maydi — yozilmadi."));
                continue;
            }
            lines.Add(BuildLine(item, settings, price));
            writtenCodes.Add(item.Code);
            acceptedItems.Add(item);
            if (item.PictureResource > 0 && !string.IsNullOrEmpty(item.PictureFile) && seenResources.Add(item.PictureResource))
                resourceLines.Add(BuildResourceLine(item.PictureResource, item.PictureFile, settings));
        }

        var sb = new StringBuilder();
        sb.Append(HeaderLine1).Append("\r\n");
        sb.Append('#').Append("\r\n");
        // Ресурсы (картинки) — до товаров, которые на них ссылаются.
        foreach (var line in resourceLines)
            sb.Append(line).Append("\r\n");
        foreach (var line in lines)
            sb.Append(line).Append("\r\n");
        // Кнопки быстрого доступа (главный экран весов): кнопка N = параметр 2000 + N, значение — код товара, 0 — пустая кнопка. Лишние кнопки очищаются,
        // иначе после полной выгрузки они указывают на коды прежней системы (48002 …), которых в базе больше нет, и экран пуст.
        var quickKeys = 0;
        if (settings.QuickKeys && writtenCodes.Count > 0)
        {
            quickKeys = Math.Min(writtenCodes.Count, settings.QuickKeyCount);
            for (var key = 0; key < settings.QuickKeyCount; key++)
                sb.Append(BuildConfigLine(ShtrikhPcSettings.QuickKeyFirstParam + key, key < writtenCodes.Count ? writtenCodes[key] : 0, settings)).Append("\r\n");
        }
        // Команды — строго в последних строках; весы выполняют их ДО загрузки данных (руководство, п. 8.1.1).
        if (settings.ClearBase)
            sb.Append(ClearCommand).Append("\r\n");
        return new ShtrikhPcBuildResult(sb.ToString(), lines.Count, warnings, quickKeys, acceptedItems);
    }

    // ------------------------------------------------------------------ пути и папка

    /// <summary>Путь внутри общей папки: пути файлов в настройках весов заданы относительно неё (руководство, п. 7.4).</summary>
    public static string ResolvePath(string folder, string relative)
    {
        var sub = (relative ?? "").Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(sub) && sub.Length > 1 && sub[1] == ':')
            return sub; // указан полный путь с диском — как есть
        sub = sub.TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine((folder ?? "").Trim(), sub);
    }

    public static string ImportPath(ShtrikhPcSettings s) => ResolvePath(s.Folder, s.ImportFile);
    public static string FlagPath(ShtrikhPcSettings s) => ResolvePath(s.Folder, s.FlagFile);
    public static string ExportPath(ShtrikhPcSettings s) => ResolvePath(s.Folder, s.ExportFile);

    /// <summary>Предыдущая загрузка ещё не принята весами: флаг-файл на месте, а во 2-й строке файла импорта по-прежнему «#».</summary>
    public static bool IsPending(string importPath, string flagPath)
    {
        try
        {
            if (!File.Exists(flagPath) || !File.Exists(importPath))
                return false;
            using var stream = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Cp1251);
            if (reader.ReadLine() is null)
                return false;
            return reader.ReadLine()?.Trim() == "#";
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Выполняет <paramref name="work"/> на фоновом потоке и ждёт не дольше <paramref name="timeout"/>: сетевая папка выключенных весов не должна подвешивать кассу.</summary>
    private static async Task<(bool Completed, T? Value)> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct)
    {
        var task = Task.Run(work, CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // забываем ошибку зависшей проверки
            return (false, default);
        }
        return (true, await task.ConfigureAwait(false));
    }

    private static ShtrikhPcFolderCheck ToCheck(ShtrikhPcNetStatus net) => net switch
    {
        ShtrikhPcNetStatus.NoConnection => ShtrikhPcFolderCheck.NoConnection,
        ShtrikhPcNetStatus.BadCredentials => ShtrikhPcFolderCheck.BadCredentials,
        ShtrikhPcNetStatus.AccessDenied => ShtrikhPcFolderCheck.NoAccess,
        ShtrikhPcNetStatus.Unsupported => ShtrikhPcFolderCheck.Unsupported,
        _ => ShtrikhPcFolderCheck.Failed,
    };

    private static ShtrikhPcWriteStatus ToWrite(ShtrikhPcNetStatus net) => net switch
    {
        ShtrikhPcNetStatus.NoConnection => ShtrikhPcWriteStatus.NoConnection,
        ShtrikhPcNetStatus.BadCredentials => ShtrikhPcWriteStatus.BadCredentials,
        ShtrikhPcNetStatus.AccessDenied => ShtrikhPcWriteStatus.NoAccess,
        ShtrikhPcNetStatus.Unsupported => ShtrikhPcWriteStatus.Unsupported,
        _ => ShtrikhPcWriteStatus.Failed,
    };

    /// <summary>«Проверить связь»: общая папка отвечает (не дольше 3 с) и в неё можно писать — создаётся и удаляется тестовый файл рядом с файлом импорта.</summary>
    public static async Task<(ShtrikhPcFolderCheck Status, string Detail)> CheckFolderAsync(ShtrikhPcSettings settings, CancellationToken ct = default)
    {
        settings.Normalize();
        if (settings.Folder.Length == 0)
            return (ShtrikhPcFolderCheck.NoFolder, "");
        var folder = settings.Folder;
        var importDir = Path.GetDirectoryName(ImportPath(settings)) ?? folder;
        var (net, netCode, lease) = await ShtrikhPcShare.OpenAsync(settings, ct).ConfigureAwait(false);
        if (net != ShtrikhPcNetStatus.Ok)
            return (ToCheck(net), netCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var leaseGuard = lease;
        try
        {
            var (done, exists) = await RunWithTimeoutAsync(() => Directory.Exists(folder), AvailabilityTimeout, ct).ConfigureAwait(false);
            if (!done || !exists)
                return (ShtrikhPcFolderCheck.NotFound, done ? "" : "timeout");

            var (written, error) = await RunWithTimeoutAsync(() =>
            {
                try
                {
                    Directory.CreateDirectory(importDir);
                    var test = Path.Combine(importDir, "nurmarket-check-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
                    File.WriteAllText(test, "ok");
                    File.Delete(test);
                    return (string?)null;
                }
                catch (UnauthorizedAccessException ex)
                {
                    return "denied:" + ex.Message;
                }
                catch (Exception ex)
                {
                    return "error:" + ex.Message;
                }
            }, AvailabilityTimeout, ct).ConfigureAwait(false);
            if (!written)
                return (ShtrikhPcFolderCheck.NotFound, "timeout");
            if (error is null)
                return (ShtrikhPcFolderCheck.Ok, "");
            return error.StartsWith("denied:", StringComparison.Ordinal)
                ? (ShtrikhPcFolderCheck.NoAccess, error[7..])
                : (ShtrikhPcFolderCheck.Failed, error[6..]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (ShtrikhPcFolderCheck.Failed, ex.Message);
        }
    }

    // ------------------------------------------------------------------ запись

    /// <summary>Записывает файл импорта в общую папку весов и создаёт флаг-файл. Если прежняя загрузка не принята —
    /// <see cref="ShtrikhPcWriteStatus.PendingNotAccepted"/> и ничего не меняется, пока вызывающий не повторит с <paramref name="overwritePending"/> = true.</summary>
    public static async Task<ShtrikhPcWriteResult> WriteAsync(IEnumerable<ShtrikhPcItem> items, ShtrikhPcSettings settings, bool overwritePending = false,
        long maxCode = MaxCode, CancellationToken ct = default, ShtrikhPcPictureStep? pictures = null)
    {
        settings.Normalize();
        var itemList = items as IReadOnlyList<ShtrikhPcItem> ?? items.ToList();
        var importPath = settings.Folder.Length == 0 ? "" : ImportPath(settings);
        var flagPath = settings.Folder.Length == 0 ? "" : FlagPath(settings);
        if (settings.Folder.Length == 0)
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.NoFolder, 0, Array.Empty<string>(), importPath, flagPath, null);

        var build = Build(itemList, settings, maxCode);
        if (build.Written == 0)
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.NothingToWrite, 0, build.Warnings, importPath, flagPath, null);

        var (net, netCode, lease) = await ShtrikhPcShare.OpenAsync(settings, ct).ConfigureAwait(false);
        if (net != ShtrikhPcNetStatus.Ok)
            return new ShtrikhPcWriteResult(ToWrite(net), 0, build.Warnings, importPath, flagPath, netCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var leaseGuard = lease; // подключение к сетевой папке держится только на время записи

        var (reachable, exists) = await RunWithTimeoutAsync(() => Directory.Exists(settings.Folder), AvailabilityTimeout, ct).ConfigureAwait(false);
        if (!reachable || !exists)
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.FolderUnavailable, 0, build.Warnings, importPath, flagPath, reachable ? null : "timeout");

        if (!overwritePending)
        {
            var (_, pending) = await RunWithTimeoutAsync(() => IsPending(importPath, flagPath), AvailabilityTimeout, ct).ConfigureAwait(false);
            if (pending)
                return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.PendingNotAccepted, 0, build.Warnings, importPath, flagPath, null);
        }

        // Картинки товаров: файлы Pictures\<код>.bmp кладутся в папку весов (пока она подключена), затем в строки товаров попадают коды ресурсов.
        ShtrikhPcPictureStats? pictureStats = null;
        if (pictures is not null)
        {
            try
            {
                var outcome = await pictures(itemList, settings, ct).ConfigureAwait(false);
                pictureStats = outcome.Stats;
                build = Build(outcome.Items, settings, maxCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Картинки — удобство, а не условие загрузки: товары уйдут без них.
                PosLogger.Log($"Весы Штрих РС: картинки не записаны ({ex.GetType().Name}: {ex.Message}) — товары уйдут без картинок.", "SCALES");
            }
        }

        try
        {
            var bytes = Encode(build.Text);
            await Task.Run(() => WriteAtomic(importPath, bytes), ct).ConfigureAwait(false);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(flagPath)!);
                using var _ = new FileStream(flagPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); // пустой флаг-файл
            }, ct).ConfigureAwait(false);
            // Главный экран весов — сценарий самообслуживания: пересобираем его из выгруженных товаров (только после удачной записи файла импорта).
            ShtrikhPcScenarioResult? scenario = null;
            if (settings.UpdateScenario && build.Items is { Count: > 0 } accepted)
            {
                var onScreen = settings.ScenarioOnlyWithPicture ? accepted.Where(i => i.PictureResource > 0) : accepted;
                try
                {
                    scenario = await ShtrikhPcScenarioWriter.WriteAsync(onScreen.Select(i => i.Code).ToList(), settings, null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Весы Штрих РС: сценарий главного экрана не записан ({ex.GetType().Name}: {ex.Message}).", "SCALES");
                }
            }
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.Ok, build.Written, build.Warnings, importPath, flagPath, null, pictureStats, scenario);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.NoAccess, 0, build.Warnings, importPath, flagPath, ex.Message);
        }
        catch (Exception ex)
        {
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.Failed, 0, build.Warnings, importPath, flagPath, ex.Message);
        }
    }

    /// <summary>Только сохраняет файл импорта по указанному пути (флешка для ручного «Импорт данных» на весах) — без флаг-файла и проверок общей папки.</summary>
    public static async Task<ShtrikhPcWriteResult> SaveToFileAsync(IEnumerable<ShtrikhPcItem> items, ShtrikhPcSettings settings, string targetPath,
        long maxCode = MaxCode, CancellationToken ct = default)
    {
        settings.Normalize();
        var build = Build(items, settings, maxCode);
        if (build.Written == 0)
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.NothingToWrite, 0, build.Warnings, targetPath, "", null);
        try
        {
            var bytes = Encode(build.Text);
            await Task.Run(() => WriteAtomic(targetPath, bytes), ct).ConfigureAwait(false);
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.Ok, build.Written, build.Warnings, targetPath, "", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.Failed, 0, build.Warnings, targetPath, "", ex.Message);
        }
    }

    /// <summary>Атомарная запись: временный файл рядом, затем переименование — весы не увидят полфайла.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
