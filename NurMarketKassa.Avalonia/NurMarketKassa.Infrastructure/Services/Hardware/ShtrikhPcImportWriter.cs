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

/// <summary>2026-11-12: итог ожидания приёма файла весами.</summary>
public enum ShtrikhPcAcceptStatus
{
    Accepted,
    Timeout,
    FlagGoneNotMarked,
    Unavailable,
}

public sealed record ShtrikhPcAcceptResult(ShtrikhPcAcceptStatus Status, TimeSpan Waited, string? Detail = null);

public sealed record ShtrikhPcBuildResult(string Text, int Written, IReadOnlyList<string> Warnings, int QuickKeys = 0, IReadOnlyList<ShtrikhPcItem>? Items = null);

public sealed record ShtrikhPcWriteResult(ShtrikhPcWriteStatus Status, int Written, IReadOnlyList<string> Warnings, string ImportPath, string FlagPath, string? Detail,
    ShtrikhPcPictureStats? Pictures = null, ShtrikhPcScenarioResult? Scenario = null, IReadOnlyList<string>? ReloadFlags = null);

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
        ['ә'] = "а", ['Ә'] = "А", ['ұ'] = "у", ['Ұ'] = "У", ['һ'] = "х", ['Һ'] = "Х", // 2026-11-12: казахские (ТЗ PC-200, прокол 7)
        ['«'] = "\"", ['»'] = "\"", ['“'] = "\"", ['”'] = "\"", ['‘'] = "'", ['’'] = "'", ['№'] = "N",
    };

    /// <summary>2026-11-12: знак есть в Windows-1251 (иначе кодировщик заменил бы его на «?»).</summary>
    private static bool FitsCp1251(char c)
    {
        if (c < 128 || c == '?')
            return true;
        Span<char> one = stackalloc char[1];
        one[0] = c;
        Span<byte> bytes = stackalloc byte[4];
        var n = Cp1251.GetBytes(one, bytes);
        return !(n == 1 && bytes[0] == (byte)'?');
    }

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
            else if (char.IsSurrogate(c) || !FitsCp1251(c))
                continue; // 2026-11-12 (ТЗ PC-200, прокол 7): эмодзи и прочие знаки вне Windows-1251 — убираем, а не «?» на экране весов
            else
                sb.Append(c);
        }
        var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
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
        // 2026-11-12 (ТЗ «Весы Штрих PC-200 — как всё устроено», п. 3.1, строка, принятая живыми весами 12.11): незаполненные поля — ПУСТЫЕ, поле 2 без штрихкода — пусто.
        // Было (06.11): пробел и «0» — тоже принималось, но эталон теперь — строка из ТЗ, байт в байт.
        Array.Fill(f, "");
        f[0] = item.Code.ToString(CultureInfo.InvariantCulture);          // 1 код товара
        f[1] = item.Barcode is { Length: 13 } bc && bc.All(char.IsDigit) ? bc : ""; // 2 штрих-код (только для поиска; печатается рассчитанный весами)
        f[2] = CleanName(item.Name);                                       // 3 наименование
        f[3] = item.IsPiece ? "1" : "0";                                   // 4 тип: 0 весовой, 1 штучный
        f[4] = priceText;                                                  // 5 цена
        for (var i = 5; i <= 8; i++)                                       // 6–9 группы-владельцы 1–4 уровня: 0 — нет
            f[i] = "0";
        f[11] = Math.Clamp(item.ShelfLifeDays, 0, 99_999).ToString(CultureInfo.InvariantCulture); // 12 срок годности, суток
        f[13] = Math.Clamp(settings.LabelFormat, 0, ShtrikhPcSettings.MaxLabelFormat).ToString(CultureInfo.InvariantCulture); // 14 формат этикетки: 0 — выбранный на весах (2026-11-12: 13 = 58x40, 14 = 58x30)
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

    /// <summary>Собирает весь текст файла. Повторные коды и коды вне 1…<paramref name="maxCode"/> отбрасываются с предупреждением.
    /// 2026-11-12: <paramref name="screenOrder"/> — коды главного экрана (эта отправка + отправленные раньше), см. <see cref="ShtrikhPcScenarioWriter.ScreenCodes"/>: по ним назначаются кнопки
    /// быстрого доступа, чтобы частичная отправка не очищала кнопки прежних товаров.</summary>
    public static ShtrikhPcBuildResult Build(IEnumerable<ShtrikhPcItem> items, ShtrikhPcSettings settings, long maxCode = MaxCode, IReadOnlyList<long>? screenOrder = null)
    {
        var itemList = items as IReadOnlyList<ShtrikhPcItem> ?? items.ToList();
        items = itemList;
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
                // 2026-11-06: сообщение с пределом при выбранных знаках после запятой и подсказкой (параметр весов 12).
                var pcDecimals = Math.Clamp(settings.PriceDecimals, 0, 3);
                warnings.Add($"«{item.Name}»: " + ScalePriceLimits.DoesNotFitText(item.Price, MaxPriceUnits, pcDecimals, pcDecimals == 0 ? "" : ScalePriceLimits.FewerDecimalsHint(
                    "Число десятичных знаков цены и стоимости (параметр 12)", "Баанын жана наркынын ондук белгилеринин саны (12-параметр)", "Number of decimal places of price and cost (parameter 12)",
                    "Fiyat ve tutarın ondalık hane sayısı (parametre 12)", "Narx va qiymatning o‘nlik xonalari soni (12-parametr)")));
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
            var keyCodes = ShtrikhPcScenarioWriter.ScreenCodes(writtenCodes, itemList.Select(i => i.Code).ToList(), screenOrder); // 2026-11-12: с прежними товарами
            quickKeys = Math.Min(keyCodes.Count, settings.QuickKeyCount);
            for (var key = 0; key < settings.QuickKeyCount; key++)
                sb.Append(BuildConfigLine(ShtrikhPcSettings.QuickKeyFirstParam + key, key < keyCodes.Count ? keyCodes[key] : 0, settings)).Append("\r\n");
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
    internal static async Task<(bool Completed, T? Value)> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct)
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
    // 2026-11-12: screenOrder — коды главного экрана весов (эта отправка + отправленные раньше, в порядке списка); null — только эта отправка (полная выгрузка).
    public static async Task<ShtrikhPcWriteResult> WriteAsync(IEnumerable<ShtrikhPcItem> items, ShtrikhPcSettings settings, bool overwritePending = false,
        long maxCode = MaxCode, CancellationToken ct = default, ShtrikhPcPictureStep? pictures = null, IReadOnlyList<long>? screenOrder = null)
    {
        settings.Normalize();
        var itemList = items as IReadOnlyList<ShtrikhPcItem> ?? items.ToList();
        var importPath = settings.Folder.Length == 0 ? "" : ImportPath(settings);
        var flagPath = settings.Folder.Length == 0 ? "" : FlagPath(settings);
        if (settings.Folder.Length == 0)
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.NoFolder, 0, Array.Empty<string>(), importPath, flagPath, null);

        var build = Build(itemList, settings, maxCode, screenOrder);
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
            var (checkedInTime, pending) = await RunWithTimeoutAsync(() => IsPending(importPath, flagPath), AvailabilityTimeout, ct).ConfigureAwait(false);
            // 2026-11-06: не уложились в 3 с (папка весов зависла или машина перегружена) — раньше это молча считалось «не ждёт приёма» и непринятая выгрузка затиралась без вопроса.
            // Теперь неизвестность = «папка недоступна»: ничего не пишем, владелец повторит.
            if (!checkedInTime)
                return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.FolderUnavailable, 0, build.Warnings, importPath, flagPath, "timeout");
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
                build = Build(outcome.Items, settings, maxCode, screenOrder);
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
            // Главный экран весов — сценарий самообслуживания: пересобираем его из выгруженных товаров (только после удачной записи файла импорта).
            ShtrikhPcScenarioResult? scenario = null;
            if (settings.UpdateScenario && build.Items is { Count: > 0 } accepted)
            {
                var onScreen = settings.ScenarioOnlyWithPicture ? accepted.Where(i => i.PictureResource > 0) : accepted;
                // 2026-11-12: прежние товары остаются на экране (раньше частичная отправка оставляла на главном экране только себя).
                var screenCodes = ShtrikhPcScenarioWriter.ScreenCodes(onScreen.Select(i => i.Code).ToList(), itemList.Select(i => i.Code).ToList(), screenOrder);
                try
                {
                    scenario = await ShtrikhPcScenarioWriter.WriteAsync(screenCodes, settings, null, ct).ConfigureAwait(false);
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
            // 2026-11-06 (Паспорт версий 2.1.3.7, параметры 953/954): флаг перезагрузки сценария — после файла импорта, картинок и сценария, но ПЕРЕД основным флагом импорта:
            // весы, приняв импорт, перечитывают главный экран сами. Порядок файлов: PCScale.txt → картинки → сценарий → flag → flag в SelfScenario → PCScale.flz.
            IReadOnlyList<string> reloadFlags = Array.Empty<string>();
            try
            {
                // Флаг нужен, только когда касса обновляет сценарий (галочка «Обновлять главный экран»): иначе перечитывать нечего и папку сценариев не трогаем.
                if (settings.UpdateScenario)
                    reloadFlags = await ShtrikhPcScenarioWriter.WriteReloadFlagsAsync(settings, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы Штрих РС: флаг перезагрузки сценария не создан ({ex.GetType().Name}: {ex.Message}).", "SCALES");
            }
            // Основной флаг импорта — последним: до него весы файл импорта не трогают.
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(flagPath)!);
                using var _ = new FileStream(flagPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); // пустой флаг-файл
                TraceFile(flagPath);
            }, ct).ConfigureAwait(false);
            return new ShtrikhPcWriteResult(ShtrikhPcWriteStatus.Ok, build.Written, build.Warnings, importPath, flagPath, null, pictureStats, scenario, reloadFlags);
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

    // ------------------------------------------------------------------ 2026-11-12: ожидание приёма весами

    /// <summary>ТЗ PC-200, п. 3: «Ждать, пока весы удалят .flz и поменяют строку 2 на @. Если за 5 минут флаг не исчез — ошибка».</summary>
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Ждёт, пока весы примут файл импорта: флаг-файл удалён И во 2-й строке файла «@» (п. 8.1.1 руководства, п. 3 ТЗ PC-200). Сетевая папка весов отваливается
    /// (ТЗ, прокол 3) — ошибки чтения не прерывают ожидание: подключение открывается заново на следующем опросе. Только чтение: в папку весов ничего не пишется.
    /// <see cref="ShtrikhPcAcceptStatus.Timeout"/> — флаг так и не исчез (весы выключены, программа весов закрыта, весы ждут другое имя файла);
    /// <see cref="ShtrikhPcAcceptStatus.FlagGoneNotMarked"/> — флаг исчез, а «@» нет (флаг удалил кто-то другой или файл подменили);
    /// <see cref="ShtrikhPcAcceptStatus.Unavailable"/> — весь срок папка не читалась.</summary>
    public static async Task<ShtrikhPcAcceptResult> WaitForAcceptanceAsync(ShtrikhPcSettings settings, TimeSpan? timeout = null, TimeSpan? poll = null, CancellationToken ct = default)
    {
        settings.Normalize();
        var limit = timeout ?? AcceptTimeout;
        var step = poll ?? TimeSpan.FromSeconds(3);
        var importPath = ImportPath(settings);
        var flagPath = FlagPath(settings);
        var started = DateTime.UtcNow;
        string? lastError = null;
        var everRead = false;
        var flagGoneSince = (DateTime?)null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (net, netCode, lease) = await ShtrikhPcShare.OpenAsync(settings, ct).ConfigureAwait(false);
            if (net != ShtrikhPcNetStatus.Ok)
                lastError = net + " " + netCode.ToString(CultureInfo.InvariantCulture);
            else
            {
                using (lease)
                {
                    var (done, state) = await RunWithTimeoutAsync(() => ReadAcceptState(importPath, flagPath), AvailabilityTimeout, ct).ConfigureAwait(false);
                    if (!done)
                        lastError = "timeout";
                    else if (state.Error is not null)
                        lastError = state.Error;
                    else
                    {
                        everRead = true;
                        lastError = null;
                        if (!state.FlagExists && state.SecondLine == "@")
                            return new ShtrikhPcAcceptResult(ShtrikhPcAcceptStatus.Accepted, DateTime.UtcNow - started);
                        // Флаг исчез, а «@» ещё нет: весы могли удалить флаг чуть раньше, чем дописали файл, — даём ещё два опроса.
                        if (!state.FlagExists)
                        {
                            flagGoneSince ??= DateTime.UtcNow;
                            if (DateTime.UtcNow - flagGoneSince.Value >= step + step)
                                return new ShtrikhPcAcceptResult(ShtrikhPcAcceptStatus.FlagGoneNotMarked, DateTime.UtcNow - started, state.SecondLine);
                        }
                        else
                            flagGoneSince = null;
                    }
                }
            }
            if (DateTime.UtcNow - started >= limit)
                return new ShtrikhPcAcceptResult(everRead && lastError is null ? ShtrikhPcAcceptStatus.Timeout : ShtrikhPcAcceptStatus.Unavailable, DateTime.UtcNow - started, lastError);
            await Task.Delay(step, ct).ConfigureAwait(false);
        }
    }

    private static (bool FlagExists, string? SecondLine, string? Error) ReadAcceptState(string importPath, string flagPath)
    {
        try
        {
            var flag = File.Exists(flagPath);
            if (!File.Exists(importPath))
                return (flag, null, "no import file");
            using var stream = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Cp1251);
            reader.ReadLine();
            return (flag, reader.ReadLine()?.Trim(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null, ex.Message);
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
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        TraceFile(path);
    }

    /// <summary>2026-11-06: тест порядка создания файлов (PCScale.txt → сценарий → flag → PCScale.flz) подписывается сюда; в работе не используется.</summary>
    internal static readonly AsyncLocal<Action<string>?> FileTrace = new();

    internal static void TraceFile(string path) => FileTrace.Value?.Invoke(path);
}
