using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-23, просьба владельца: «на складе массовая загрузка товаров через сканер тоже сделай». Режим «Массовое добавление сканером»:
// оператор подряд сканирует штрихкоды, программа набирает таблицу — известный товар +1 к количеству, неизвестный — новая строка
// (название, закупка, продажа, количество, категория, единица). «Загрузить всё» создаёт новые товары и принимает количество ТЕМ ЖЕ
// сервисом, что обычная приёмка склада (PurchaseReceivingService.PostAsync без поставщика): ни денег, ни остатков здесь своей логики нет.

/// <summary>Что случилось со сканом (для звука/подсветки и фокуса в окне).</summary>
public enum BulkScanOutcome
{
    /// <summary>Код слишком короткий — пропущен.</summary>
    Ignored,
    /// <summary>Известный товар добавлен новой строкой (количество 1).</summary>
    Added,
    /// <summary>Повторный скан: количество строки +1.</summary>
    Repeated,
    /// <summary>Неизвестный штрихкод: новая строка «Новый товар», курсор — в «Название».</summary>
    NewRow,
}

/// <summary>Строка таблицы массового добавления: строка приёмки + то, что нужно только виду (ошибка, подсветка).</summary>
public sealed class BulkScanRow : INotifyPropertyChanged
{
    private ReceivingLineVm _line;
    private string _error = "";
    private bool _flash;

    /// <summary>Известный товар пришёл без цены закупки или продажи — поля цен остаются на виду, пока строку не поправят (не пропадают при вводе).</summary>
    private bool _priceFix;

    public BulkScanRow(ReceivingLineVm line)
    {
        _line = line;
        _line.PropertyChanged += OnLineChanged;
        _priceFix = !line.IsNew && (line.PurchasePrice <= 0 || line.SalePrice <= 0);
    }

    /// <summary>Строка приёмки. Один и тот же объект живёт до успеха: на нём держится защита приёмки от двойного прихода.</summary>
    public ReceivingLineVm Line
    {
        get => _line;
        private set
        {
            _line.PropertyChanged -= OnLineChanged;
            _line = value;
            _line.PropertyChanged += OnLineChanged;
            _priceFix = !value.IsNew && (value.PurchasePrice <= 0 || value.SalePrice <= 0);
            Raise(nameof(Line));
            OnLineChanged(null, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Товар на сервере нашёлся только при загрузке (не было на этом ПК) — строка становится приходом существующего.</summary>
    internal void ReplaceLine(ReceivingLineVm line) => Line = line;

    /// <summary>Правка строки (вид → черновик).</summary>
    public event Action? Edited;

    /// <summary>Товара у магазина ещё нет (новый или из общей базы CRM) — доступны название, цены, категория, единица.</summary>
    public bool IsNewProduct => _line.IsNew;

    public bool IsKnown => !_line.IsNew;

    /// <summary>Показывать поля «Закупка» и «Продажа»: у нового товара всегда, у известного — только если цен у него нет.</summary>
    public bool ShowPrices => _line.IsNew || _priceFix;

    public string KindText => _line.Source switch
    {
        ReceivingSource.Warehouse => Tr.T("На складе", "Кампада", "In stock", "Depoda", "Omborda"),
        ReceivingSource.GlobalBase => Tr.T("База CRM", "CRM базасы", "CRM catalog", "CRM veritabanı", "CRM bazasi"),
        _ => Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot"),
    };

    /// <summary>Известный товар: «остаток 5 → 6».</summary>
    public string StockInfo => _line.Source == ReceivingSource.Warehouse
        ? Tr.T($"остаток {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"калдык {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"stock {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"stok {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}",
            $"qoldiq {_line.StockBefore:0.###} → {_line.StockBefore + _line.Quantity:0.###}")
        : "";

    public string Category
    {
        get => _line.CategoryName ?? "";
        set
        {
            _line.CategoryName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Raise(nameof(Category));
            Edited?.Invoke();
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (_error == value)
                return;
            _error = value;
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    public bool IsFlash
    {
        get => _flash;
        set
        {
            if (_flash == value)
                return;
            _flash = value;
            Raise(nameof(IsFlash));
        }
    }

    internal void SetError(string message) => Error = message;
    internal void ClearError() => Error = "";

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 2026-10-23: id созданного товара проставляет PurchaseReceivingService.PostAsync с фонового потока — вид его не показывает,
        // а событие с чужого потока роняло привязки («Call from invalid thread») и обрывало строку после создания товара.
        if (e.PropertyName == nameof(ReceivingLineVm.ProductId))
            return;
        Raise(nameof(IsNewProduct));
        Raise(nameof(IsKnown));
        Raise(nameof(ShowPrices));
        Raise(nameof(KindText));
        Raise(nameof(StockInfo));
        Edited?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Сессия массового добавления: очередь сканов, таблица, загрузка, черновик. Все методы — с потока интерфейса.</summary>
public sealed class BulkScanSession : INotifyPropertyChanged
{
    /// <summary>Не дольше столько ждём сервер при скане незнакомого кода: строка встанет «новой», название можно вписать руками.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Только для стенда проверки: не обновлять каталог кассы с сервера после создания товаров.</summary>
    public static bool CatalogRefreshEnabled { get; set; } = true;

    public ObservableCollection<BulkScanRow> Rows { get; } = new();

    /// <summary>Ошибки последней загрузки (по строкам).</summary>
    public ObservableCollection<string> Errors { get; } = new();

    private readonly Queue<string> _queue = new();
    private Task _drain = Task.CompletedTask;
    private bool _draining;
    /// <summary>Штрихкод → id товара, созданного в этой сессии (один товар не создаётся дважды).</summary>
    private readonly Dictionary<string, string> _createdInSession = new(StringComparer.OrdinalIgnoreCase);
    private int _saveVersion;
    private bool _isLoading;
    private string _progressText = "";
    private double _progressValue, _progressMax = 1;
    private string _summaryText = "";

    /// <summary>Сохранять черновик (на стенде проверки можно выключить).</summary>
    public bool PersistDraft { get; set; } = true;

    public BulkScanSession()
    {
        Rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>Скан обработан: строка (null — пропущен) и что произошло. Окно играет звук/подсветку и ставит курсор.</summary>
    public event Action<BulkScanRow?, BulkScanOutcome>? Scanned;

    /// <summary>Загрузка закончена (окно обновляет каталог и остатки).</summary>
    public event Action<int, int>? Completed;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; Raise(); Raise(nameof(CanLoad)); } }
    }

    public bool CanLoad => !_isLoading && Rows.Count > 0;

    public string ProgressText { get => _progressText; private set { _progressText = value; Raise(); } }
    public double ProgressValue { get => _progressValue; private set { _progressValue = value; Raise(); } }
    public double ProgressMax { get => _progressMax; private set { _progressMax = Math.Max(1, value); Raise(); } }

    public string SummaryText { get => _summaryText; private set { _summaryText = value; Raise(); Raise(nameof(HasSummary)); } }
    public bool HasSummary => _summaryText.Length > 0;
    public bool HasFailedRows => Rows.Any(r => r.HasError);

    public string StatsText
    {
        get
        {
            var qty = Rows.Sum(r => r.Line.Quantity);
            return Tr.T($"Позиций: {Rows.Count} · штук: {qty:0.###}", $"Позиция: {Rows.Count} · даана: {qty:0.###}",
                $"Items: {Rows.Count} · pieces: {qty:0.###}", $"Kalem: {Rows.Count} · adet: {qty:0.###}",
                $"Pozitsiya: {Rows.Count} · dona: {qty:0.###}");
        }
    }

    /// <summary>Есть что терять при закрытии окна.</summary>
    public bool HasUnsaved => Rows.Count > 0;

    // ------------------------------------------------------------------ скан

    /// <summary>Поставить скан в очередь (сканы идут подряд, по одному, в порядке прихода). Завершается, когда очередь пуста.</summary>
    public Task ScanAsync(string raw)
    {
        _queue.Enqueue(raw);
        if (!_draining)
        {
            _draining = true;
            _drain = DrainAsync();
        }
        return _drain;
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_queue.TryDequeue(out var code))
            {
                try
                {
                    await ProcessScanAsync(code).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Массовое добавление: скан '{code}' не обработан: {ex.Message}", "WARNING");
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private async Task ProcessScanAsync(string raw)
    {
        var code = ProductCodeLookup.Normalize(raw);
        if (code.Length < 4)
        {
            Scanned?.Invoke(null, BulkScanOutcome.Ignored);
            return;
        }

        // 1. Этот код уже в таблице (любая форма: 12 ↔ 13 цифр) — количество +1.
        var forms = ProductCodeLookup.Candidates(code);
        var same = Rows.FirstOrDefault(r => r.Line.Barcode.Length > 0
            && (string.Equals(r.Line.Barcode, code, StringComparison.OrdinalIgnoreCase)
                || forms.Contains(r.Line.Barcode, StringComparer.OrdinalIgnoreCase)));
        if (same is not null)
        {
            Bump(same);
            return;
        }

        // 2. Свой склад (мгновенно), сервер, общая база CRM — тем же поиском, что приёмка. Нет связи — строка «новая».
        ReceivingLineVm line;
        try
        {
            using var cts = new CancellationTokenSource(LookupTimeout);
            line = await PurchaseReceivingService.Instance.LookupAsync(code, 1, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            line = new ReceivingLineVm { Barcode = code, Source = ReceivingSource.Unknown, Quantity = 1 };
        }

        // Другой штрихкод того же товара (доп. штрихкод) — тоже +1 к его строке.
        if (!string.IsNullOrWhiteSpace(line.ProductId)
            && Rows.FirstOrDefault(r => string.Equals(r.Line.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } sameProduct)
        {
            Bump(sameProduct);
            return;
        }

        var row = new BulkScanRow(line);
        Insert(row);
        var outcome = line.Source == ReceivingSource.Warehouse ? BulkScanOutcome.Added : BulkScanOutcome.NewRow;
        Flash(row);
        Scanned?.Invoke(row, outcome);
    }

    private void Bump(BulkScanRow row)
    {
        row.Line.Quantity = Math.Round(row.Line.Quantity + 1, 3);
        row.ClearError();
        // 2026-10-24 (аудит, круг 8, С-1): счётчик «штук» обновляется сразу при повторном скане (раньше — только при смене числа строк).
        Raise(nameof(StatsText));
        Flash(row);
        Scanned?.Invoke(row, BulkScanOutcome.Repeated);
    }

    private void Insert(BulkScanRow row)
    {
        row.Edited += ScheduleSave;
        // Новая строка — сверху: оператор видит её сразу, прокручивать список не нужно.
        Rows.Insert(0, row);
    }

    private async void Flash(BulkScanRow row)
    {
        row.IsFlash = true;
        await Task.Delay(700).ConfigureAwait(true);
        row.IsFlash = false;
    }

    // ------------------------------------------------------------------ правка списка

    public void Remove(BulkScanRow row)
    {
        row.Edited -= ScheduleSave;
        Rows.Remove(row);
    }

    public void Clear()
    {
        if (_isLoading)
            return;
        foreach (var r in Rows)
            r.Edited -= ScheduleSave;
        Rows.Clear();
        Errors.Clear();
        SummaryText = "";
        Raise(nameof(HasFailedRows));
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(StatsText));
        Raise(nameof(CanLoad));
        Raise(nameof(HasUnsaved));
        ScheduleSave();
    }

    // ------------------------------------------------------------------ загрузка

    /// <summary>«Загрузить всё» (<paramref name="onlyFailed"/> = false) или «Повторить ошибочные». По строке: проверка штрихкода на сервере
    /// (уже есть — приход существующему, а не дубль), создание нового товара и приход количества — PurchaseReceivingService.PostAsync
    /// без поставщика, ровно как «Провести» в приёмке. Ошибка строки не останавливает остальные.</summary>
    public async Task LoadAsync(bool onlyFailed = false)
    {
        // Защита от двойного нажатия: флаг ставится до первого await.
        if (_isLoading)
            return;
        var rows = Rows.Where(r => !onlyFailed || r.HasError).ToList();
        if (rows.Count == 0)
            return;
        IsLoading = true;
        Errors.Clear();
        SummaryText = "";
        var created = 0;
        var received = 0;
        var failed = 0;
        var warnings = new List<string>();
        var employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId;
        ProgressMax = rows.Count;
        ProgressValue = 0;
        ProgressText = ProgressLabel(0, rows.Count);
        var receivedKnown = new List<ReceivingLineVm>();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                row.ClearError();
                var name = string.IsNullOrWhiteSpace(row.Line.ProductName) ? row.Line.Barcode : row.Line.ProductName;
                try
                {
                    var problems = PurchaseReceivingService.Validate(new[] { row.Line });
                    if (problems.Count > 0)
                    {
                        Fail(row, name, problems[0], ref failed);
                        continue;
                    }

                    if (!await PrepareNewRowAsync(row).ConfigureAwait(true))
                    {
                        failed++;
                        Errors.Add(name + ": " + row.Error);
                        continue;
                    }

                    var wasNew = row.Line.IsNew && string.IsNullOrWhiteSpace(row.Line.ProductId);
                    var result = await PurchaseReceivingService.Instance
                        .PostAsync(new[] { row.Line }, null, false, employee).ConfigureAwait(true);
                    created += result.Created;
                    if (wasNew && !string.IsNullOrWhiteSpace(row.Line.ProductId) && row.Line.Barcode.Length > 0)
                        _createdInSession[row.Line.Barcode] = row.Line.ProductId!;

                    if (result.PostedLines.Count > 0)
                    {
                        received++;
                        if (!row.Line.IsNew)
                            receivedKnown.Add(row.Line);
                        warnings.AddRange(result.Errors);
                        row.Edited -= ScheduleSave;
                        Rows.Remove(row);
                    }
                    else
                    {
                        var message = result.Errors.FirstOrDefault() ?? Tr.T("не принят", "кабыл алынган жок", "not received", "kabul edilmedi", "qabul qilinmadi");
                        row.SetError(message);
                        failed++;
                        Errors.Add(message);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Массовое добавление: строка '{name}' не загружена: {ex}", "WARNING");
                    Fail(row, name, ex.Message, ref failed);
                }
                finally
                {
                    ProgressValue = i + 1;
                    ProgressText = ProgressLabel(i + 1, rows.Count);
                }
            }

            // Остаток известных товаров в каталоге этого ПК — сразу, как после обычной приёмки (синхронизация придёт через пару минут).
            foreach (var line in receivedKnown)
                ApplyReceivedStock(line);

            foreach (var w in warnings)
                Errors.Add(w);
            SummaryText = Tr.T($"Создано {created}, принято {received}, ошибок {failed}",
                $"Түзүлдү {created}, кабыл алынды {received}, ката {failed}",
                $"Created {created}, received {received}, errors {failed}",
                $"Oluşturulan {created}, kabul edilen {received}, hata {failed}",
                $"Yaratildi {created}, qabul qilindi {received}, xato {failed}");
            PosLogger.Log($"Массовое добавление: {SummaryText}.", "STOCK");
            Raise(nameof(HasFailedRows));
        }
        finally
        {
            IsLoading = false;
            ScheduleSave();
        }
        Completed?.Invoke(created, received);
    }

    private void Fail(BulkScanRow row, string name, string message, ref int failed)
    {
        row.SetError(message);
        failed++;
        Errors.Add(message.StartsWith(name, StringComparison.CurrentCultureIgnoreCase) ? message : name + ": " + message);
    }

    /// <summary>Перед созданием нового товара — штрихкод на сервере: уже есть (завели на сайте или другой кассой, пока набирали
    /// список) — строка становится приходом к существующему товару, а не дублем. Сервер не ответил — товар НЕ создаётся.</summary>
    private async Task<bool> PrepareNewRowAsync(BulkScanRow row)
    {
        var line = row.Line;
        if (!line.IsNew || !string.IsNullOrWhiteSpace(line.ProductId) || line.Barcode.Length == 0)
            return true;

        if (_createdInSession.TryGetValue(line.Barcode, out var knownId))
        {
            line.ProductId = knownId;
            return true;
        }

        var found = await ProductCodeLookup.FindOnServerAsync(line.Barcode).ConfigureAwait(true);
        if (found.Hit is { } hit)
        {
            var existing = PurchaseReceivingService.FromTile(hit.Product, line.Quantity);
            existing.CategoryName = null;
            // Цен у найденного товара нет (сервер не отдал) — берём те, что оператор вписал в строке, а не пишем нули в карточку.
            if (existing.PurchasePrice <= 0)
                existing.PurchasePrice = line.PurchasePrice;
            if (existing.SalePrice <= 0)
                existing.SalePrice = line.SalePrice;
            row.ReplaceLine(existing);
            PosLogger.Log($"Массовое добавление: штрихкод {line.Barcode} уже есть на сервере (товар {hit.Product.Id}) — приход к нему, дубль не создаётся.", "STOCK");
            return true;
        }
        if (found.Unavailable)
        {
            row.SetError(Tr.T("не удалось проверить штрихкод на сервере — проверьте связь и повторите",
                "штрихкодду серверден текшерүү мүмкүн болгон жок — байланышты текшерип, кайталаңыз",
                "could not verify the barcode on the server — check the connection and retry",
                "barkod sunucuda doğrulanamadı — bağlantıyı kontrol edip tekrar deneyin",
                "shtrix-kodni serverda tekshirib bo'lmadi — ulanishni tekshirib, qayta urining"));
            return false;
        }
        return true;
    }

    private static void ApplyReceivedStock(ReceivingLineVm line)
    {
        try
        {
            var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            if (tile is null)
                return;
            var quantity = Math.Round(tile.Quantity + line.Quantity, 3);
            StockSyncService.ApplyQuantityToTileOnUi(tile, quantity, tile.MustWeigh);
            CatalogCacheService.PersistProductStock(line.ProductId!, quantity, tile.MustWeigh);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: остаток товара {line.ProductId} в каталоге не обновлён: {ex.Message}", "WARNING");
        }
    }

    private static string ProgressLabel(int done, int total) =>
        Tr.T($"{done} из {total}", $"{total} ичинен {done}", $"{done} of {total}", $"{done} / {total}", $"{total} dan {done}");

    // ------------------------------------------------------------------ черновик

    /// <summary>Сколько строк в сохранённом черновике этого магазина (0 — нет) и когда сохранён.</summary>
    public static (int Count, DateTime SavedAt) DraftInfo() => BulkScanDraftStore.Peek();

    public void RestoreDraft()
    {
        foreach (var line in BulkScanDraftStore.Load())
        {
            var row = new BulkScanRow(line);
            row.Edited += ScheduleSave;
            Rows.Add(row);
        }
    }

    public void DiscardDraft() => BulkScanDraftStore.Delete();

    public void SaveDraftNow()
    {
        if (PersistDraft)
            BulkScanDraftStore.Save(Rows.Select(r => r.Line).ToList());
    }

    private async void ScheduleSave()
    {
        // 2026-10-24 (аудит, круг 8, С-1): любая правка строки (+/−, ввод количества) меняет «штук» — поднимаем счётчик до проверки черновика.
        Raise(nameof(StatsText));
        if (!PersistDraft)
            return;
        var version = ++_saveVersion;
        await Task.Delay(600).ConfigureAwait(true);
        if (version == _saveVersion)
            SaveDraftNow();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Черновик списка массового добавления — файл на этом компьютере (как журнал «Закупок»): закрыли окно, выключился свет —
/// список можно продолжить.</summary>
public static class BulkScanDraftStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases");

    private static string FilePath => Path.Combine(DataDir, "bulk-scan-draft.json");
    private static readonly object Sync = new();

    private sealed class Draft
    {
        public string CompanyId { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<DraftRow> Rows { get; set; } = new();
    }

    private sealed class DraftRow
    {
        public string? ProductId { get; set; }
        public string Barcode { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "шт";
        public string? Category { get; set; }
        public int Source { get; set; }
        public double Quantity { get; set; }
        public double Purchase { get; set; }
        public double Sale { get; set; }
        public double StockBefore { get; set; }
        public string OriginalName { get; set; } = "";
        public string OriginalUnit { get; set; } = "";
        public double OriginalPurchase { get; set; }
        public double OriginalSale { get; set; }
    }

    private static string CurrentCompany => CompanyInfoService.LastCompany?.Id ?? "";

    private static Draft? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var draft = JsonSerializer.Deserialize<Draft>(File.ReadAllText(FilePath));
            if (draft is null || draft.Rows.Count == 0)
                return null;
            // Черновик другого магазина (вход под другим аккаунтом) не показываем.
            if (draft.CompanyId.Length > 0 && CurrentCompany.Length > 0
                && !string.Equals(draft.CompanyId, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                return null;
            return draft;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: черновик не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }

    public static (int Count, DateTime SavedAt) Peek()
    {
        lock (Sync)
        {
            var draft = Read();
            return draft is null ? (0, default) : (draft.Rows.Count, draft.SavedAt);
        }
    }

    public static List<ReceivingLineVm> Load()
    {
        lock (Sync)
        {
            var draft = Read();
            if (draft is null)
                return new();
            return draft.Rows.Select(r => new ReceivingLineVm
            {
                ProductId = r.ProductId,
                Barcode = r.Barcode,
                ProductName = r.Name,
                Unit = r.Unit,
                CategoryName = r.Category,
                Source = (ReceivingSource)r.Source,
                Quantity = r.Quantity,
                PurchasePrice = r.Purchase,
                SalePrice = r.Sale,
                StockBefore = r.StockBefore,
                OriginalName = r.OriginalName,
                OriginalUnit = r.OriginalUnit,
                OriginalPurchasePrice = r.OriginalPurchase,
                OriginalSalePrice = r.OriginalSale,
            }).ToList();
        }
    }

    public static void Save(IReadOnlyList<ReceivingLineVm> lines)
    {
        lock (Sync)
        {
            try
            {
                if (lines.Count == 0)
                {
                    Delete();
                    return;
                }
                var draft = new Draft
                {
                    CompanyId = CurrentCompany,
                    SavedAt = DateTime.Now,
                    Rows = lines.Select(l => new DraftRow
                    {
                        ProductId = l.ProductId,
                        Barcode = l.Barcode,
                        Name = l.ProductName,
                        Unit = l.Unit,
                        Category = l.CategoryName,
                        Source = (int)l.Source,
                        Quantity = l.Quantity,
                        Purchase = l.PurchasePrice,
                        Sale = l.SalePrice,
                        StockBefore = l.StockBefore,
                        OriginalName = l.OriginalName,
                        OriginalUnit = l.OriginalUnit,
                        OriginalPurchase = l.OriginalPurchasePrice,
                        OriginalSale = l.OriginalSalePrice,
                    }).ToList(),
                };
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(draft));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: черновик не записан ({ex.Message}).", "WARNING");
            }
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: черновик не удалён ({ex.Message}).", "WARNING");
        }
    }
}
