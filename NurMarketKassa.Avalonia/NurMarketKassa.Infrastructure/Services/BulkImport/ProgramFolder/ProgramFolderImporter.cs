using System.Text;

namespace NurMarketKassa.Services;

// 2026-11-20 (просьба владельца «автоматическое подтягивание базы, если указать установленную папку программы»): пользователь указывает папку программы
// (или папку с выгрузками), касса сама находит в ней данные и отдаёт мастеру обычную таблицу товаров — как будто выбран файл.
// Правила: ТОЛЬКО ЧТЕНИЕ; базы SQLite/DBF копируются во временную папку (программа может держать их открытыми) и копия удаляется; рекурсия ограничена глубиной
// и числом файлов; на сервер ничего не уходит (это делает мастер после «Проверьте»); в журнал — только виды файлов и числа, без путей и содержимого.
// Читаем: SQLite, DBF, выгрузки Excel/CSV/XML 1С (CommerceML). Закрытые форматы (1С .1CD, Firebird, Access, SQL Server) — понятное сообщение и «Как выгрузить».

public enum FolderFileKind
{
    Table,
    CommerceMl,
    Sqlite,
    Dbf,
    /// <summary>Файл с расширением SQLite, но без заголовка SQLite: зашифрован или повреждён.</summary>
    Encrypted,
    Firebird,
    Access,
    SqlServer,
    OneCFile,
    OneCDump,
}

public sealed record FolderFile(string Path, string Relative, FolderFileKind Kind, long Size, DateTime Modified)
{
    public bool Readable => Kind is FolderFileKind.Table or FolderFileKind.CommerceMl or FolderFileKind.Sqlite or FolderFileKind.Dbf;
}

public enum FolderConfidence
{
    Low,
    Medium,
    High,
}

public enum FolderProblem
{
    None,
    NotFound,
    Empty,
    NothingReadable,
    UnsupportedFormat,
    Busy,
    AccessDenied,
    Encrypted,
    TooBig,
    TooManyRows,
    Error,
}

/// <summary>Результат просмотра папки без чтения содержимого: что лежит и что за программа.</summary>
public sealed class FolderScan
{
    public string Root { get; init; } = "";
    public List<FolderFile> Files { get; } = [];
    public bool Truncated { get; set; }
    public int AccessDenied { get; set; }
    public ProgramFolderHint? Program { get; set; }
    public FolderConfidence Confidence { get; set; }
    public IEnumerable<FolderFile> Readable => Files.Where(f => f.Readable);
    public IEnumerable<FolderFile> Unsupported => Files.Where(f => !f.Readable);
}

/// <summary>Итог подтяжки: таблица (или причина), узнанная программа и уверенность, клиенты, закрытые форматы.</summary>
public sealed class ProgramFolderResult
{
    public ProductFileReadResult Read { get; init; } = ProductFileReadResult.Fail("");
    public FolderProblem Problem { get; init; }
    public ProgramFolderHint? Program { get; init; }
    public FolderConfidence Confidence { get; init; }
    /// <summary>Что прочитано: «база SQLite», «таблица DBF», «Excel/CSV», «выгрузка 1С (XML)» и имя файла — для строки под кнопкой.</summary>
    public string Source { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public ProductFileTable? Clients { get; init; }
    public IReadOnlyList<FolderFile> Unsupported { get; init; } = [];
    public bool Ok => Read.Ok;
    public string? Error => Read.Error;
}

public static class ProgramFolderImporter
{
    public const int MaxDepth = 4;
    public const int MaxFilesVisited = 4000;
    public const long MaxDbBytes = 1L << 30;
    private const int MaxSqlite = 6, MaxDbf = 12, MaxTables = 15;

    /// <summary>Только для тестов: куда класть временные копии (по умолчанию — системная временная папка).</summary>
    internal static string? TempRootForTests { get; set; }

    internal const string TempPrefix = "nurmarket-folder-import-";

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "$recycle.bin", "system volume information", "node_modules", ".git", "winsxs", "microsoft.net",
    };

    // ------------------------------------------------------------------ просмотр папки

    /// <summary>Просмотреть папку (или один файл): список известных файлов, глубина до <see cref="MaxDepth"/>, не больше <see cref="MaxFilesVisited"/> файлов. Ничего не открывает, кроме первых байт.</summary>
    public static FolderScan Scan(string path, string? expectedProgramId = null)
    {
        var scan = new FolderScan { Root = path };
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (Classify(fi) is { } kind)
                    scan.Files.Add(new FolderFile(fi.FullName, fi.Name, kind, fi.Length, fi.LastWriteTime));
                Recognize(scan, expectedProgramId, fi.DirectoryName ?? "");
                return scan;
            }
            var root = new DirectoryInfo(path);
            if (!root.Exists)
                return scan;
            var visited = 0;
            Walk(root, root.FullName, 0, scan, ref visited);
            Recognize(scan, expectedProgramId, root.FullName);
        }
        catch (Exception ex)
        {
            PosLogger.Log("Подтяжка базы из папки: просмотр не удался: " + ex.GetType().Name, "WARNING");
        }
        return scan;
    }

    private static void Walk(DirectoryInfo dir, string root, int depth, FolderScan scan, ref int visited)
    {
        IEnumerable<FileInfo> files;
        try { files = dir.EnumerateFiles().ToList(); }
        catch (UnauthorizedAccessException) { scan.AccessDenied++; return; }
        catch (Exception) { return; }
        foreach (var f in files)
        {
            if (++visited > MaxFilesVisited)
            {
                scan.Truncated = true;
                return;
            }
            if (f.Name.StartsWith("~$", StringComparison.Ordinal) || (f.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if (Classify(f) is { } kind)
                scan.Files.Add(new FolderFile(f.FullName, Path.GetRelativePath(root, f.FullName), kind, f.Length, f.LastWriteTime));
        }
        if (depth >= MaxDepth)
            return;
        IEnumerable<DirectoryInfo> subs;
        try { subs = dir.EnumerateDirectories().ToList(); }
        catch (UnauthorizedAccessException) { scan.AccessDenied++; return; }
        catch (Exception) { return; }
        foreach (var d in subs)
        {
            if (scan.Truncated)
                return;
            if (SkipDirs.Contains(d.Name) || (d.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            Walk(d, root, depth + 1, scan, ref visited);
        }
    }

    private static FolderFileKind? Classify(FileInfo f)
    {
        var ext = f.Extension.ToLowerInvariant();
        switch (ext)
        {
            case ".xlsx":
            case ".xls":
            case ".csv":
                return f.Length is > 0 and <= ProductFileReader.MaxFileBytes ? FolderFileKind.Table : null;
            case ".txt":
                return f.Length is > 64 and <= ProductFileReader.MaxFileBytes ? FolderFileKind.Table : null;
            case ".xml":
                return f.Length is > 0 and <= ProductFileReader.MaxFileBytes && LooksLikeCommerceMl(f.FullName) ? FolderFileKind.CommerceMl : null;
            case ".db":
            case ".sqlite":
            case ".sqlite3":
            case ".db3":
            case ".s3db":
            case ".sdb":
            case ".dat":
            case ".data":
                if (f.Length == 0)
                    return null;
                if (SqliteProductReader.HasSqliteMagic(f.FullName))
                    return FolderFileKind.Sqlite;
                return ext is ".sqlite" or ".sqlite3" or ".db3" or ".s3db" ? FolderFileKind.Encrypted : null;
            case ".dbf":
                return f.Length > 32 && DbfTableReader.LooksLikeDbf(f.FullName) ? FolderFileKind.Dbf : null;
            case ".fdb":
            case ".gdb":
                return FolderFileKind.Firebird;
            case ".mdb":
            case ".accdb":
                return FolderFileKind.Access;
            case ".mdf":
                return FolderFileKind.SqlServer;
            case ".1cd":
                return FolderFileKind.OneCFile;
            case ".dt":
                return FolderFileKind.OneCDump;
            default:
                return null;
        }
    }

    private static bool LooksLikeCommerceMl(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            return CommerceMlReader.LooksLike(buf.AsSpan(0, n).ToArray());
        }
        catch
        {
            return false;
        }
    }

    private static void Recognize(FolderScan scan, string? expectedProgramId, string root)
    {
        // 1) программа, выбранная на плитке, и её слова в пути/именах; 2) любая программа по словам; 3) 1С по её файлам
        var text = root + "\\" + string.Join("\\", scan.Files.Select(f => f.Relative));
        var byPath = ProgramFolderHints.GuessByPath(text);
        if (scan.Files.Any(f => f.Kind is FolderFileKind.OneCFile or FolderFileKind.OneCDump || (f.Kind == FolderFileKind.CommerceMl && f.Relative.Contains("import", StringComparison.OrdinalIgnoreCase))))
        {
            scan.Program = ProgramFolderHints.For("1c");
            scan.Confidence = FolderConfidence.High;
        }
        else if (byPath is not null)
        {
            scan.Program = byPath;
            scan.Confidence = FolderConfidence.High;
        }
        else if (ProgramFolderHints.For(expectedProgramId) is { } expected)
        {
            scan.Program = expected;
            scan.Confidence = FolderConfidence.Low; // пользователь сказал, что это эта программа, но папка этого не подтверждает
        }
    }

    // ------------------------------------------------------------------ чтение

    public static ProgramFolderResult Read(string path, string? expectedProgramId = null, CancellationToken ct = default)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0 || !(Directory.Exists(path) || File.Exists(path)))
            return Fail(FolderProblem.NotFound, null, []);
        var scan = Scan(path, expectedProgramId);
        var unsupported = scan.Unsupported.ToList();
        var readable = scan.Readable.ToList();
        if (readable.Count == 0)
        {
            if (unsupported.Count > 0)
                return Fail(unsupported.All(u => u.Kind == FolderFileKind.Encrypted) ? FolderProblem.Encrypted : FolderProblem.UnsupportedFormat, scan, unsupported);
            return Fail(scan.AccessDenied > 0 ? FolderProblem.AccessDenied : FolderProblem.Empty, scan, unsupported);
        }

        var tmp = Path.Combine(TempRootForTests ?? Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        var best = default(Candidate);
        FolderProblem? firstProblem = null;
        ProductFileTable? clients = null;
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var f in readable.Where(f => f.Kind == FolderFileKind.Sqlite).OrderByDescending(f => f.Size).Take(MaxSqlite))
            {
                ct.ThrowIfCancellationRequested();
                var one = ReadSqlite(f, tmp, ct, out var problem);
                firstProblem ??= problem;
                Consider(ref best, ref clients, one, f);
            }
            foreach (var f in readable.Where(f => f.Kind == FolderFileKind.Dbf).OrderByDescending(f => f.Size).Take(MaxDbf))
            {
                ct.ThrowIfCancellationRequested();
                var one = ReadDbf(f, tmp, out var problem);
                firstProblem ??= problem;
                Consider(ref best, ref clients, one, f);
            }
            foreach (var group in readable.Where(f => f.Kind == FolderFileKind.CommerceMl).GroupBy(f => Path.GetDirectoryName(f.Path) ?? ""))
            {
                ct.ThrowIfCancellationRequested();
                var one = ReadCommerceMl(group.ToList());
                Consider(ref best, ref clients, one, group.First());
            }
            foreach (var f in readable.Where(f => f.Kind == FolderFileKind.Table).OrderByDescending(f => f.Modified).Take(MaxTables))
            {
                ct.ThrowIfCancellationRequested();
                var one = ReadTable(f);
                if (one.Error == "too-many")
                    firstProblem ??= FolderProblem.TooManyRows;
                Consider(ref best, ref clients, one, f);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log("Подтяжка базы из папки: сбой чтения: " + ex.GetType().Name, "WARNING");
            return Fail(FolderProblem.Error, scan, unsupported);
        }
        finally
        {
            Cleanup(tmp);
        }

        if (best.Table is null)
        {
            if (firstProblem is { } p and not FolderProblem.None)
                return Fail(p, scan, unsupported, clients);
            return Fail(unsupported.Count > 0 ? FolderProblem.UnsupportedFormat : FolderProblem.NothingReadable, scan, unsupported, clients);
        }

        var table = best.Table;
        var confidence = scan.Confidence;
        var program = scan.Program;
        // программа по заголовкам файла: «Номенклатура / Конечный остаток» — это 1С, и т. п.
        if (best.File.Kind is FolderFileKind.Table or FolderFileKind.CommerceMl && ImportSourceProfiles.Detect(table.Headers) is { } det
            && ProgramFolderHints.For(det.Id) is { } byHeaders && (program is null || confidence < FolderConfidence.High))
        {
            program = byHeaders;
            confidence = FolderConfidence.High;
        }
        else if (program is not null && confidence == FolderConfidence.High && best.File.Kind is FolderFileKind.Sqlite or FolderFileKind.Dbf)
            confidence = FolderConfidence.Medium; // имя папки узнано, но схема таблицы базы — наша догадка
        PosLogger.Log($"Подтяжка базы из папки: прочитано {KindName(best.File.Kind)}, строк {table.DataRowCount}, уверенность {confidence}", "INFO");
        return new ProgramFolderResult
        {
            Read = new ProductFileReadResult { Table = table },
            Program = program,
            Confidence = confidence,
            Source = KindText(best.File.Kind),
            SourceFileName = Path.GetFileName(best.File.Path),
            Clients = clients,
            Unsupported = unsupported,
        };
    }

    private struct Candidate
    {
        public ProductFileTable? Table;
        public FolderFile File;
        public long Quality;
    }

    private static void Consider(ref Candidate best, ref ProductFileTable? clients, ExtractedTables one, FolderFile file)
    {
        if (one.Clients is not null && (clients is null || one.Clients.DataRowCount > clients.DataRowCount))
            clients = one.Clients;
        if (one.Products is not { } table)
            return;
        var q = Quality(table);
        if (q > best.Quality)
            best = new Candidate { Table = table, File = file, Quality = q };
    }

    /// <summary>Годность таблицы как списка товаров: нужны название и штрихкод/цена/артикул; больше узнанных столбцов и строк — лучше. 0 — не товары.</summary>
    internal static long Quality(ProductFileTable table)
    {
        if (table.DataRowCount == 0)
            return 0;
        var m = ProductImportMapping.AutoDetect(table.Headers, table);
        if (!m.Columns.ContainsKey(ProductImportField.Name) || !(m.Columns.ContainsKey(ProductImportField.Barcode) || m.Columns.ContainsKey(ProductImportField.Sale) || m.Columns.ContainsKey(ProductImportField.Article)))
            return 0;
        return m.Columns.Count * 100_000L + Math.Min(table.DataRowCount, 99_999);
    }

    private static ExtractedTables ReadSqlite(FolderFile f, string tmp, CancellationToken ct, out FolderProblem? problem)
    {
        problem = null;
        if (f.Size > MaxDbBytes)
        {
            problem = FolderProblem.TooBig;
            return new ExtractedTables { Error = "too-big" };
        }
        var copy = CopyWithSiblings(f.Path, tmp, out var copyProblem);
        if (copy is null)
        {
            problem = copyProblem;
            return new ExtractedTables { Error = "copy" };
        }
        var one = SqliteProductReader.Extract(copy, DisplayName(f), ct);
        problem = one.Error switch
        {
            "encrypted" => FolderProblem.Encrypted,
            "busy" => FolderProblem.Busy,
            "too-many" => FolderProblem.TooManyRows,
            "error" => FolderProblem.Error,
            _ => null,
        };
        return one;
    }

    private static ExtractedTables ReadDbf(FolderFile f, string tmp, out FolderProblem? problem)
    {
        problem = null;
        var copy = CopyWithSiblings(f.Path, tmp, out var copyProblem, siblings: false);
        if (copy is null)
        {
            problem = copyProblem;
            return new ExtractedTables { Error = "copy" };
        }
        var dbf = DbfTableReader.Read(copy, ProductFileReader.MaxRows, out var err);
        if (dbf is null)
        {
            problem = err == "busy" ? FolderProblem.Busy : null;
            return new ExtractedTables { Error = err };
        }
        var headers = dbf.Fields;
        var rows = dbf.Rows.Where(r => r.Any(c => c.Length > 0)).ToList();
        if (rows.Count > ProductFileReader.MaxRows)
        {
            problem = FolderProblem.TooManyRows;
            return new ExtractedTables { Error = "too-many" };
        }
        var table = FolderTableBuilder.BuildRaw(DisplayName(f), headers, rows);
        return ClientsImportPlan.LooksLikeClients(headers) ? new ExtractedTables { Clients = table } : new ExtractedTables { Products = table };
    }

    private static ExtractedTables ReadTable(FolderFile f)
    {
        var r = ProductFileReader.ReadFile(f.Path);
        if (r.Table is not { } t)
            return new ExtractedTables { Error = r.Error?.Contains("50 000") == true ? "too-many" : "error" };
        return ClientsImportPlan.LooksLikeClients(t.Headers) ? new ExtractedTables { Clients = t } : new ExtractedTables { Products = t };
    }

    /// <summary>Выгрузки 1С (CommerceML): import*.xml — каталог, offers*.xml — цены и остатки; строки сливаются по штрихкоду, артикулу или названию.</summary>
    private static ExtractedTables ReadCommerceMl(List<FolderFile> group)
    {
        var imports = new List<ProductFileTable>();
        var offers = new List<ProductFileTable>();
        foreach (var f in group.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = File.ReadAllBytes(f.Path);
                var r = CommerceMlReader.Read(bytes, Path.GetFileName(f.Path));
                if (r.Table is null)
                    continue;
                (Path.GetFileName(f.Path).StartsWith("offers", StringComparison.OrdinalIgnoreCase) ? offers : imports).Add(r.Table);
            }
            catch
            {
                // занят или нет прав — файл пропускается
            }
        }
        if (imports.Count == 0 && offers.Count == 0)
            return new ExtractedTables { Error = "error" };
        string[] headers = [.. (imports.Count > 0 ? imports[0] : offers[0]).Headers];
        var items = new List<string[]>();
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(ProductFileTable t, bool merge)
        {
            foreach (var row in t.DataRows)
            {
                var cells = Enumerable.Range(0, headers.Length).Select(i => row[i]).ToArray();
                var keys = new[] { cells[2], cells[1], cells[0] }.Where(k => k.Length > 0).ToList();
                var hit = merge ? keys.Select(k => byKey.TryGetValue(k, out var ix) ? ix : -1).FirstOrDefault(ix => ix >= 0, -1) : -1;
                if (hit >= 0)
                {
                    for (var c = 0; c < cells.Length; c++)
                        if (cells[c].Length > 0 && (c is 5 or 6 || items[hit][c].Length == 0))
                            items[hit][c] = cells[c];
                    continue;
                }
                items.Add(cells);
                foreach (var k in keys)
                    byKey.TryAdd(k, items.Count - 1);
            }
        }
        foreach (var t in imports)
            Add(t, merge: true);
        foreach (var t in offers)
            Add(t, merge: true);
        if (items.Count > ProductFileReader.MaxRows)
            return new ExtractedTables { Error = "too-many" };
        var table = FolderTableBuilder.BuildRaw(Path.GetFileName(group[0].Path), headers, items);
        return new ExtractedTables { Products = table };
    }

    // ------------------------------------------------------------------ копирование и уборка

    private static string DisplayName(FolderFile f) => Path.GetFileName(f.Path);

    /// <summary>Копия файла (и -wal/-shm/-journal рядом) во временную папку. Читает с FileShare.ReadWrite — запущенная программа не мешает. null — не получилось (причина в <paramref name="problem"/>).</summary>
    private static string? CopyWithSiblings(string source, string tmp, out FolderProblem? problem, bool siblings = true)
    {
        problem = null;
        var dir = Path.Combine(tmp, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(source));
            CopyShared(source, target);
            if (siblings)
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (File.Exists(source + suffix))
                        CopyShared(source + suffix, target + suffix);
            return target;
        }
        catch (UnauthorizedAccessException)
        {
            problem = FolderProblem.AccessDenied;
        }
        catch (IOException)
        {
            problem = FolderProblem.Busy;
        }
        catch
        {
            problem = FolderProblem.Error;
        }
        return null;
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output, 1 << 20);
    }

    private static void Cleanup(string tmp)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, recursive: true);
                return;
            }
            catch
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        PosLogger.Log("Подтяжка базы из папки: временная копия не удалена", "WARNING");
    }

    // ------------------------------------------------------------------ тексты

    private static ProgramFolderResult Fail(FolderProblem problem, FolderScan? scan, List<FolderFile> unsupported, ProductFileTable? clients = null) => new()
    {
        Problem = problem,
        Read = ProductFileReadResult.Fail(ProblemText(problem, unsupported)),
        Program = scan?.Program,
        Confidence = scan?.Confidence ?? FolderConfidence.Low,
        Unsupported = unsupported,
        Clients = clients,
    };

    private static string KindName(FolderFileKind k) => k.ToString();

    internal static string KindText(FolderFileKind k) => k switch
    {
        FolderFileKind.Sqlite => Tr.T("база SQLite", "SQLite базасы", "SQLite database", "SQLite veritabanı", "SQLite bazasi"),
        FolderFileKind.Dbf => Tr.T("таблица DBF", "DBF таблицасы", "DBF table", "DBF tablosu", "DBF jadvali"),
        FolderFileKind.CommerceMl => Tr.T("выгрузка 1С (XML)", "1С чыгаруусу (XML)", "1C export (XML)", "1C dışa aktarımı (XML)", "1C eksporti (XML)"),
        FolderFileKind.Table => Tr.T("файл Excel/CSV", "Excel/CSV файлы", "Excel/CSV file", "Excel/CSV dosyası", "Excel/CSV fayli"),
        FolderFileKind.Firebird => "Firebird (.fdb/.gdb)",
        FolderFileKind.Access => "Microsoft Access (.mdb/.accdb)",
        FolderFileKind.SqlServer => "SQL Server (.mdf)",
        FolderFileKind.OneCFile => Tr.T("база 1С (1Cv8.1CD)", "1С базасы (1Cv8.1CD)", "1C database (1Cv8.1CD)", "1C veritabanı (1Cv8.1CD)", "1C bazasi (1Cv8.1CD)"),
        FolderFileKind.OneCDump => Tr.T("выгрузка базы 1С (.dt)", "1С базасынын чыгаруусу (.dt)", "1C database dump (.dt)", "1C veritabanı yedeği (.dt)", "1C baza nusxasi (.dt)"),
        FolderFileKind.Encrypted => Tr.T("зашифрованная база", "шифрленген база", "encrypted database", "şifreli veritabanı", "shifrlangan baza"),
        _ => "",
    };

    internal static string ProblemText(FolderProblem p, IReadOnlyList<FolderFile> unsupported)
    {
        var closed = string.Join(", ", unsupported.Select(f => f.Kind).Distinct().Select(KindText).Where(s => s.Length > 0));
        return p switch
        {
            FolderProblem.NotFound => Tr.T("Папка не найдена. Проверьте путь.", "Папка табылган жок. Жолду текшериңиз.", "The folder was not found. Check the path.", "Klasör bulunamadı. Yolu kontrol edin.", "Papka topilmadi. Yo'lni tekshiring."),
            FolderProblem.Empty => Tr.T("В папке нет подходящих файлов (база SQLite, таблица DBF, Excel, CSV, XML 1С). Проверьте, что указана папка программы или её выгрузок.",
                "Папкада ылайыктуу файлдар жок (SQLite базасы, DBF таблицасы, Excel, CSV, 1С XML). Программанын же анын чыгарылган файлдарынын папкасы көрсөтүлгөнүн текшериңиз.",
                "The folder has no suitable files (SQLite database, DBF table, Excel, CSV, 1C XML). Check that the program folder or its exports folder is chosen.",
                "Klasörde uygun dosya yok (SQLite veritabanı, DBF tablosu, Excel, CSV, 1C XML). Program klasörünün veya dışa aktarımlarının seçildiğinden emin olun.",
                "Papkada mos fayllar yo'q (SQLite baza, DBF jadval, Excel, CSV, 1C XML). Dastur yoki uning eksportlari papkasi tanlanganini tekshiring."),
            FolderProblem.NothingReadable => Tr.T("В папке есть файлы, но списка товаров в них не нашлось. Выгрузите товары из программы в Excel или CSV.",
                "Папкада файлдар бар, бирок алардан товарлар тизмеси табылган жок. Товарларды программадан Excel же CSV'ге чыгарыңыз.",
                "The folder has files, but no product list was found in them. Export the products from the program to Excel or CSV.",
                "Klasörde dosyalar var ama içlerinde ürün listesi bulunamadı. Ürünleri programdan Excel veya CSV'ye aktarın.",
                "Papkada fayllar bor, lekin ulardan mahsulotlar ro'yxati topilmadi. Mahsulotlarni dasturdan Excel yoki CSV ga chiqaring."),
            FolderProblem.UnsupportedFormat => Tr.T($"Эту базу прочитать напрямую нельзя: {closed}. Выгрузите товары из программы в Excel или CSV и выберите файл.",
                $"Бул базаны түз окуу мүмкүн эмес: {closed}. Товарларды программадан Excel же CSV'ге чыгарып, файлды тандаңыз.",
                $"This database cannot be read directly: {closed}. Export the products from the program to Excel or CSV and choose the file.",
                $"Bu veritabanı doğrudan okunamaz: {closed}. Ürünleri programdan Excel veya CSV'ye aktarın ve dosyayı seçin.",
                $"Bu bazani to'g'ridan-to'g'ri o'qib bo'lmaydi: {closed}. Mahsulotlarni dasturdan Excel yoki CSV ga chiqaring va faylni tanlang."),
            FolderProblem.Busy => Tr.T("Файл занят программой, и скопировать его не удалось. Закройте программу и попробуйте ещё раз.",
                "Файлды программа колдонуп жатат, көчүрүү мүмкүн болгон жок. Программаны жаап, кайра аракет кылыңыз.",
                "The file is in use by the program and could not be copied. Close the program and try again.",
                "Dosya program tarafından kullanılıyor ve kopyalanamadı. Programı kapatıp tekrar deneyin.",
                "Fayl dastur tomonidan band, nusxalab bo'lmadi. Dasturni yopib, qayta urinib ko'ring."),
            FolderProblem.AccessDenied => Tr.T("Нет прав на чтение этой папки. Запустите кассу от имени администратора или скопируйте папку в «Документы» и укажите копию.",
                "Бул папканы окууга укук жок. Кассаны администратор атынан ишке киргизиңиз же папканы «Документтерге» көчүрүп, көчүрмөнү көрсөтүңүз.",
                "No permission to read this folder. Run the till as administrator or copy the folder to “Documents” and point to the copy.",
                "Bu klasörü okuma izni yok. Kasayı yönetici olarak çalıştırın veya klasörü «Belgeler»e kopyalayıp kopyayı gösterin.",
                "Bu papkani o'qishga ruxsat yo'q. Kassani administrator sifatida ishga tushiring yoki papkani «Hujjatlar» ga nusxalab, nusxani ko'rsating."),
            FolderProblem.Encrypted => Tr.T("База зашифрована или повреждена — прочитать её нельзя. Выгрузите товары из программы в Excel или CSV.",
                "База шифрленген же бузулган — аны окуу мүмкүн эмес. Товарларды программадан Excel же CSV'ге чыгарыңыз.",
                "The database is encrypted or damaged and cannot be read. Export the products from the program to Excel or CSV.",
                "Veritabanı şifreli veya bozuk — okunamaz. Ürünleri programdan Excel veya CSV'ye aktarın.",
                "Baza shifrlangan yoki shikastlangan — o'qib bo'lmaydi. Mahsulotlarni dasturdan Excel yoki CSV ga chiqaring."),
            FolderProblem.TooBig => Tr.T("База больше 1 ГБ — копировать её для чтения не будем. Выгрузите товары из программы в Excel или CSV.",
                "База 1 ГБдан чоң — окуу үчүн көчүрбөйбүз. Товарларды программадан Excel же CSV'ге чыгарыңыз.",
                "The database is larger than 1 GB — we will not copy it for reading. Export the products from the program to Excel or CSV.",
                "Veritabanı 1 GB'tan büyük — okumak için kopyalamayacağız. Ürünleri programdan Excel veya CSV'ye aktarın.",
                "Baza 1 GB dan katta — o'qish uchun nusxalamaymiz. Mahsulotlarni dasturdan Excel yoki CSV ga chiqaring."),
            FolderProblem.TooManyRows => Tr.T("В базе больше 50 000 товаров. Выгрузите товары частями (по группам) в Excel или CSV.",
                "Базада 50 000 ашык товар бар. Товарларды бөлүктөр менен (топтор боюнча) Excel же CSV'ге чыгарыңыз.",
                "The database has more than 50,000 products. Export the products in parts (by group) to Excel or CSV.",
                "Veritabanında 50.000'den fazla ürün var. Ürünleri parçalar halinde (gruplara göre) Excel veya CSV'ye aktarın.",
                "Bazada 50 000 dan ortiq mahsulot bor. Mahsulotlarni qismlarga (guruhlar bo'yicha) Excel yoki CSV ga chiqaring."),
            _ => Tr.T("Не удалось прочитать папку. Выгрузите товары из программы в Excel или CSV.", "Папканы окуу мүмкүн болгон жок. Товарларды программадан Excel же CSV'ге чыгарыңыз.",
                "The folder could not be read. Export the products from the program to Excel or CSV.", "Klasör okunamadı. Ürünleri programdan Excel veya CSV'ye aktarın.", "Papkani o'qib bo'lmadi. Mahsulotlarni dasturdan Excel yoki CSV ga chiqaring."),
        };
    }
}
