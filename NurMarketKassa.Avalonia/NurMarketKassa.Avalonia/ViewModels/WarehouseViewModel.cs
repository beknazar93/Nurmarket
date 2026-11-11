using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed class WarehouseViewModel : INotifyPropertyChanged
{
    /// <summary>2026-09-08: раньше жёстко зашитые русские строки без перевода (владелец
    /// пожаловался: "нету кириллицы" — экран списания на кыргызском интерфейсе показывал их
    /// по-русски). Теперь свойство, а не статическое поле — читает текущий язык интерфейса
    /// при каждом обращении (Tr.T), так что список причин уже приходит переведённым в
    /// WriteOffReasonOptions/InitializeWriteOffReasonPicker без изменений там.</summary>
    public static string[] WriteOffReasons => new[]
    {
        Tr.T("Брак", "Брак", "Defective", "Kusurlu", "Nuqsonli"),
        Tr.T("Просрочка", "Мөөнөтү өткөн", "Expired", "Son kullanma tarihi geçmiş", "Muddati o'tgan"),
        Tr.T("Порча упаковки", "Таңгагы бузулган", "Damaged packaging", "Ambalaj hasarlı", "Qadoq shikastlangan"),
        Tr.T("Списание для себя", "Өз керегине алуу", "Personal use", "Kişisel kullanım", "O'z ehtiyoji uchun hisobdan chiqarish"),
        Tr.T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    /// <summary>2026-11-05 (аудит №3, Т3-08): причина списания для примечания акта НА СЕРВЕРЕ — всегда по-русски (ключ), а не слово языка интерфейса:
    /// раньше в отчётах и на сайте была смесь «Другое», «Other», «Башка». Порядок — как в <see cref="WriteOffReasons"/>.</summary>
    private static readonly string[] WriteOffReasonsRu = { "Брак", "Просрочка", "Порча упаковки", "Списание для себя", "Другое" };

    public static string WriteOffReasonForServer(string? localized)
    {
        var idx = Array.IndexOf(WriteOffReasons, localized);
        return idx >= 0 ? WriteOffReasonsRu[idx] : (localized ?? "");
    }

    private readonly IInventoryApiService? _inventoryApi;
    private readonly IUserPrompts? _prompts;

    private bool _isBusy;
    private string _writeOffProductId = "";
    private string _writeOffProductName = "";
    private string _writeOffBarcode = "";
    private double _writeOffQuantity = 1;
    private string _writeOffReason = WriteOffReasons[0];
    private RevisionLineVm? _focusedRevisionLine;
    private string _productSearchText = "";

    /// <summary>Фильтр "Единица продажи" в Складе (2026-09-07, по образцу stage.nurcrm.kg):
    /// "all"/"weight"/"piece"/"piecepackage". "piece" — обычный штучный товар без варианта
    /// продажи из упаковки, "piecepackage" — товар, у которого настроена поштучная продажа из
    /// упаковки (CatalogProductTileVm.HasPieceOption), в веб-версии это отдельный от "Штучные"
    /// пункт "Поштучно".</summary>
    private string _saleUnitFilter = "all";

    /// <summary>2026-09-12, по просьбе пользователя: "как на каталоге в интерфейсе кассира" —
    /// DataGrid и так виртуализирует строки (в отличие от карточек каталога), но на каталоге в
    /// тысячи товаров бесконечный скролл всё равно неудобен кассиру/владельцу — та же постраничная
    /// навигация, что уже есть в CatalogPanelViewModel (PageSize/CurrentPage/TotalPages).</summary>
    private const int ProductPageSize = 50;
    private int _currentPage = 1;

    public WarehouseViewModel(IInventoryApiService? inventoryApi = null, IUserPrompts? prompts = null)
    {
        _inventoryApi = inventoryApi;
        _prompts = prompts;
        CommitRevisionCommand = new AsyncRelayCommand(CommitRevisionAsync, () => !IsBusy && RevisionLines.Count > 0);
        CommitReceivingCommand = new AsyncRelayCommand(CommitReceivingAsync, () => !IsBusy && !_receivingCommitInProgress && ReceivingLines.Count > 0);
        WriteOffCommand = new AsyncRelayCommand(
            WriteOffAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(_writeOffProductId) && WriteOffQuantity > 0);
        WriteOffReasonOptions = new ObservableCollection<string>(WriteOffReasons);
        RevisionLines.CollectionChanged += (_, _) => (CommitRevisionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        ReceivingLines.CollectionChanged += (_, _) => (CommitReceivingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        // 2026-11-07 (ТЗ «Порядковая нумерация товаров в накладной»): «№» строки = порядок добавления; удаление уплотняет номера, перестановка «наверх» их не трогает.
        ReceivingLines.CollectionChanged += (_, e) =>
        {
            if (!IsReorderingReceiving)
                ReceivingListOrder.OnListChanged(ReceivingLines, e);
        };
        RevisionPickCommand = new RelayCommand<CatalogProductTileVm>(PickRevisionProduct);
        WriteOffPickCommand = new RelayCommand<CatalogProductTileVm>(PickWriteOffProduct);
        PreviousProductPageCommand = new RelayCommand(() => GoToProductPage(_currentPage - 1), () => CanGoToPreviousProductPage);
        NextProductPageCommand = new RelayCommand(() => GoToProductPage(_currentPage + 1), () => CanGoToNextProductPage);
        LoadWriteOffHistory();
        RebuildPagedProducts();
    }

    public ObservableCollection<RevisionLineVm> RevisionLines { get; } = new();
    public ObservableCollection<string> WriteOffReasonOptions { get; }
    public ObservableCollection<CatalogProductTileVm> FilteredProducts { get; private set; } = new();

    /// <summary>Текущая страница FilteredProducts (ProductPageSize штук) — то, что реально
    /// показывает DataGrid в WarehouseWindow.axaml.</summary>
    public ObservableCollection<CatalogProductTileVm> PagedProducts { get; private set; } = new();

    public int CurrentProductPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value)
                return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProductPageStatusText));
            OnPropertyChanged(nameof(CanGoToPreviousProductPage));
            OnPropertyChanged(nameof(CanGoToNextProductPage));
            (PreviousProductPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NextProductPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public int TotalProductPages => Math.Max(1, (int)Math.Ceiling(FilteredProducts.Count / (double)ProductPageSize));
    public bool CanGoToPreviousProductPage => _currentPage > 1;
    public bool CanGoToNextProductPage => _currentPage < TotalProductPages;
    public bool ShowProductPager => FilteredProducts.Count > ProductPageSize;
    public string ProductPageStatusText => Tr.T(
        $"Страница {_currentPage} из {TotalProductPages}",
        $"{_currentPage}/{TotalProductPages}-бет",
        $"Page {_currentPage} of {TotalProductPages}",
        $"Sayfa {_currentPage}/{TotalProductPages}",
        $"Sahifa: {_currentPage} / {TotalProductPages}");

    public ICommand PreviousProductPageCommand { get; }
    public ICommand NextProductPageCommand { get; }

    private void GoToProductPage(int page)
    {
        page = Math.Clamp(page, 1, TotalProductPages);
        CurrentProductPage = page;
        RebuildPagedProducts();
    }

    private void RebuildPagedProducts()
    {
        PagedProducts = new ObservableCollection<CatalogProductTileVm>(
            FilteredProducts.Skip((_currentPage - 1) * ProductPageSize).Take(ProductPageSize));
        OnPropertyChanged(nameof(PagedProducts));
        OnPropertyChanged(nameof(TotalProductPages));
        OnPropertyChanged(nameof(ShowProductPager));
        OnPropertyChanged(nameof(ProductPageStatusText));
        OnPropertyChanged(nameof(CanGoToPreviousProductPage));
        OnPropertyChanged(nameof(CanGoToNextProductPage));
        (PreviousProductPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextProductPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Журнал списаний этой кассы (AI-фичи 2026-09-04) — см. WriteOffHistoryStore,
    /// локальный, т.к. IInventoryApiService не даёт прочитать историю актов обратно с сервера.</summary>
    public ObservableCollection<WriteOffHistoryRowVm> WriteOffHistory { get; } = new();

    /// <summary>2026-11-05 (Д-6): перечитать журнал списаний на вкладке «Списание» (окно подписывается на <see cref="WriteOffHistoryStore.Appended"/>).</summary>
    public void ReloadWriteOffHistory() => LoadWriteOffHistory();

    private void LoadWriteOffHistory()
    {
        WriteOffHistory.Clear();
        foreach (var row in WriteOffHistoryStore.LoadRecent())
        {
            WriteOffHistory.Add(new WriteOffHistoryRowVm
            {
                ProductName = row.ProductName,
                QuantityText = row.Quantity.ToString("0.###"),
                Reason = row.Reason,
                CashierName = row.CashierName ?? "",
                DateText = row.CreatedAt == DateTime.MinValue
                    ? ""
                    : row.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                SortUtc = row.CreatedAt.ToUniversalTime(),
                Quantity = row.Quantity,
            });
        }

        // 2026-10-27, аудит владельца В-2: у владельца к местному журналу добавляются списания всех касс компании (акты сервера).
        if (AppMode.IsOwner && _inventoryApi is not null)
            _ = MergeServerWriteOffsAsync();
    }

    private int _writeOffMergeGen;

    /// <summary>2026-10-27, В-2: «История списаний» владельца показывала только этот ПК, хотя сервер отдаёт акты всех касс
    /// (GET inventory/sessions/). Акты с пометкой «Списание…» и отрицательной разницей по строке становятся строками журнала;
    /// местная запись, у которой есть такая же строка на сервере (тот же товар и количество, разница во времени до 15 мин), не дублируется.
    /// Сервер не ответил — остаётся местный журнал.</summary>
    private async Task MergeServerWriteOffsAsync()
    {
        var gen = ++_writeOffMergeGen;
        IReadOnlyList<InventoryAct>? acts;
        try
        {
            acts = await _inventoryApi!.ListActsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История списаний: акты сервера не получены ({ex.Message}).", "WARNING");
            return;
        }

        if (acts is null || gen != _writeOffMergeGen)
            return;
        var fromServer = new List<WriteOffHistoryRowVm>();
        foreach (var act in acts)
        {
            if (!act.IsApplied || !act.Note.TrimStart().StartsWith("Списание", StringComparison.OrdinalIgnoreCase))
                continue;
            var reason = act.Note;
            var colon = reason.IndexOf(':');
            if (colon >= 0)
                reason = reason[(colon + 1)..].Trim();
            reason = System.Text.RegularExpressions.Regex.Replace(reason, @"\s*\([\d.,\s]+\)\s*$", "");
            foreach (var line in act.Lines)
            {
                if (line.Delta >= 0)
                    continue;
                fromServer.Add(new WriteOffHistoryRowVm
                {
                    ProductName = line.Name,
                    Quantity = -line.Delta,
                    QuantityText = (-line.Delta).ToString("0.###"),
                    Reason = reason,
                    CashierName = act.CreatedByName,
                    DateText = act.CreatedUtc == DateTime.MinValue ? "" : act.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                    SortUtc = act.CreatedUtc,
                });
            }
        }

        var local = WriteOffHistory.Where(r => !fromServer.Any(s =>
            string.Equals(s.ProductName, r.ProductName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(s.Quantity - r.Quantity) < 0.001 && Math.Abs((s.SortUtc - r.SortUtc).TotalMinutes) <= 15)).ToList();
        var all = fromServer.Concat(local).OrderByDescending(r => r.SortUtc).Take(500).ToList();
        WriteOffHistory.Clear();
        foreach (var r in all)
            WriteOffHistory.Add(r);
    }

    private string _revisionSearchText = "";
    public string RevisionSearchText
    {
        get => _revisionSearchText;
        set
        {
            if (_revisionSearchText == value)
                return;
            _revisionSearchText = value;
            OnPropertyChanged();
            ApplyNameSearch(value, RevisionSearchResults);
        }
    }

    public ObservableCollection<CatalogProductTileVm> RevisionSearchResults { get; } = new();
    public ICommand RevisionPickCommand { get; private set; } = null!;

    private string _writeOffSearchText = "";
    public string WriteOffSearchText
    {
        get => _writeOffSearchText;
        set
        {
            if (_writeOffSearchText == value)
                return;
            _writeOffSearchText = value;
            OnPropertyChanged();
            ApplyNameSearch(value, WriteOffSearchResults);
        }
    }

    public ObservableCollection<CatalogProductTileVm> WriteOffSearchResults { get; } = new();
    public ICommand WriteOffPickCommand { get; private set; } = null!;

    /// <summary>Same click-to-pick-from-a-filtered-list pattern the cashier catalog search
    /// already uses reliably — AutoCompleteBox's SelectedItem binding turned out to not fire
    /// consistently on a suggestion click in this Avalonia version.</summary>
    private static void ApplyNameSearch(string query, ObservableCollection<CatalogProductTileVm> results)
    {
        results.Clear();
        var trimmed = query.Trim();
        if (trimmed.Length < 2)
            return;

        // 2026-10-21: поиск ревизии и списания не знал штрихкода вовсе — скан в это поле ничего не находил (склад не
        // находит товар по скану). Теперь точный код (как у кассы, ProductCodeLookup) — первым, и вхождение в основной
        // штрихкод — с 4 знаков, как у «Товаров».
        var exact = trimmed.Length >= 4 ? InCatalog(ProductCodeLookup.FindLocal(trimmed)?.Product) : null;
        if (exact is not null)
            results.Add(exact);

        foreach (var product in CatalogCacheService.Products
                     .Where(p => (p.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                                  || (p.Article?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                                  || (trimmed.Length >= 4 && (p.Barcode?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)))
                                 && !string.Equals(p.Id, exact?.Id, StringComparison.OrdinalIgnoreCase))
                     .Take(exact is null ? 8 : 7))
            results.Add(product);
    }

    /// <summary>2026-10-21: товар поиска по коду — тем же объектом, что в каталоге памяти (фото, метки срока); если в
    /// памяти его нет (каталог ещё грузится) — плитка из базы кассы.</summary>
    private static CatalogProductTileVm? InCatalog(CatalogProductTileVm? product) =>
        product is null
            ? null
            : CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, product.Id, StringComparison.OrdinalIgnoreCase)) ?? product;

    /// <summary>2026-10-21: похоже на код со сканера, а не на название: без пробелов, от 4 знаков, есть цифра.</summary>
    public static bool LooksLikeCode(string? text)
    {
        var code = ProductCodeLookup.Normalize(text);
        return code.Length >= 4 && !code.Contains(' ') && code.Any(char.IsDigit);
    }

    /// <summary>2026-10-21: Enter в поле поиска ревизии/списания (сканер пишет код в поле и жмёт Enter — раньше
    /// ничего не происходило). Точный код — этот товар; иначе первый из подсказок (не для штрихкода из цифр: там
    /// подсказка могла бы быть чужим товаром); иначе код — на сервер и «не найден».</summary>
    public void SubmitSearch(bool isRevisionTab)
    {
        var text = (isRevisionTab ? RevisionSearchText : WriteOffSearchText).Trim();
        if (text.Length == 0)
            return;
        var results = isRevisionTab ? RevisionSearchResults : WriteOffSearchResults;
        var code = ProductCodeLookup.Normalize(text);
        var digitsOnly = code.Length >= 6 && code.All(char.IsDigit);
        var exact = LooksLikeCode(text) ? InCatalog(ProductCodeLookup.FindLocal(code)?.Product) : null;
        var pick = exact ?? (digitsOnly ? null : results.FirstOrDefault());
        if (pick is not null)
        {
            if (isRevisionTab)
                PickRevisionProduct(pick);
            else
                PickWriteOffProduct(pick);
            return;
        }

        if (!LooksLikeCode(text))
            return;
        if (isRevisionTab)
            RevisionSearchText = "";
        else
            WriteOffSearchText = "";
        HandleBarcodeScan(code, isRevisionTab);
    }

    private void PickRevisionProduct(CatalogProductTileVm? product)
    {
        if (product is null)
            return;
        AddRevisionLineForProduct(product);
        RevisionSearchText = "";
        RevisionSearchResults.Clear();
    }

    private void PickWriteOffProduct(CatalogProductTileVm? product)
    {
        if (product is null)
            return;
        SelectProductForWriteOff(product);
        WriteOffSearchText = "";
        WriteOffSearchResults.Clear();
    }

    public string ProductSearchText
    {
        get => _productSearchText;
        set
        {
            if (_productSearchText == value)
                return;
            _productSearchText = value;
            OnPropertyChanged();
            ScheduleProductFilter();
        }
    }

    public string ProductCountText => Tr.T(
        $"Всего: {CatalogCacheService.Products.Count} · Найдено: {FilteredProducts.Count}",
        $"Жалпы: {CatalogCacheService.Products.Count} · Табылды: {FilteredProducts.Count}",
        $"Total: {CatalogCacheService.Products.Count} · Found: {FilteredProducts.Count}",
        $"Toplam: {CatalogCacheService.Products.Count} · Bulunan: {FilteredProducts.Count}",
        $"Jami: {CatalogCacheService.Products.Count} · Topildi: {FilteredProducts.Count}");

    /// <summary>Вызывается из радиокнопок "Единица продажи" в WarehouseWindow.axaml.cs
    /// (см. SaleUnitFilter_Changed) — "all"/"weight"/"piece"/"piecepackage".</summary>
    public void SetSaleUnitFilter(string filter)
    {
        if (_saleUnitFilter == filter)
            return;
        _saleUnitFilter = filter;
        ApplyProductFilter();
    }

    // 2026-10-06, владелец: «проведи полный редизайн склада». Фильтр наличия — кнопки и плитки
    // «Заканчивается / Нет в наличии / Срок годности» (all / low / out / expiry).
    private string _stockFilter = "all";

    public string StockFilter => _stockFilter;

    public void SetStockFilter(string filter)
    {
        if (_stockFilter == filter)
            return;
        _stockFilter = filter;
        OnPropertyChanged(nameof(StockFilter));
        ApplyProductFilter();
    }

    // 2026-10-06, владелец: «чтобы ИИ показывал наглядно изменения» — после «Выполнить» склад показывает только изменённые товары
    // (id, а у только что созданных — «name:Название», пока каталог не перечитался).
    private HashSet<string>? _changedKeys;

    public int ChangedFilterCount => _changedKeys?.Count ?? 0;

    public void SetChangedFilter(IReadOnlyList<string>? keys)
    {
        _changedKeys = keys is { Count: > 0 } ? new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase) : null;
        ApplyProductFilter();
    }

    /// <summary>Пересобрать список (после обновления сроков годности или остатков).</summary>
    public void ReapplyProductFilter() => ApplyProductFilter();

    // ---------------------------------------------------------------- 2026-10-30, владелец: «на складе сортировка: при нажатии на заголовок
    // столбца «Остаток» как в Windows — сначала показывать с 0 остатком». Клик по заголовку: по возрастанию ▲ → по убыванию ▼ → без сортировки.
    // Ключи: name (название), code (код/штрихкод), price, cost (закупка), stock (остаток), category. Запоминается в настройках этого ПК.

    private string _sortKey = "";
    private bool _sortDescending;
    private bool _sortLoaded;

    public string ProductSortKey { get { EnsureSortLoaded(); return _sortKey; } }
    public bool ProductSortDescending { get { EnsureSortLoaded(); return _sortDescending; } }

    /// <summary>Сменилась сортировка — окно обновляет стрелки в заголовках и выпадающий список плиток.</summary>
    public event Action? ProductSortChanged;

    private void EnsureSortLoaded()
    {
        if (_sortLoaded)
            return;
        _sortLoaded = true;
        var saved = UserPreferences.Instance.WarehouseProductSort ?? "";
        var parts = saved.Split(':');
        if (parts.Length == 2 && parts[0] is "name" or "code" or "price" or "cost" or "stock" or "category")
        {
            _sortKey = parts[0];
            _sortDescending = parts[1] == "desc";
        }
    }

    public void SetProductSort(string key, bool descending)
    {
        EnsureSortLoaded();
        key ??= "";
        if (_sortKey == key && _sortDescending == descending)
            return;
        _sortKey = key;
        _sortDescending = key.Length > 0 && descending;
        try
        {
            UserPreferences.Instance.WarehouseProductSort = _sortKey.Length == 0 ? "" : $"{_sortKey}:{(_sortDescending ? "desc" : "asc")}";
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: сортировка не сохранена: {ex.Message}", "WARNING");
        }

        // 2026-10-30: журнал для диагностики («сортировка не работает») — столбец, направление, сколько строк и сколько мс заняло.
        var sortWatch = System.Diagnostics.Stopwatch.StartNew();
        ApplyProductFilter();
        ProductSortChanged?.Invoke();
        PosLogger.Log($"SORT: столбец «{(_sortKey.Length == 0 ? "без сортировки" : _sortKey)}», {(_sortKey.Length == 0 ? "-" : _sortDescending ? "по убыванию" : "по возрастанию")}, строк {FilteredProducts.Count}, {sortWatch.ElapsedMilliseconds} мс", "INFO");
    }

    /// <summary>Клик по заголовку столбца: другой столбец — по возрастанию; тот же — по убыванию; третий клик — без сортировки.</summary>
    public void CycleProductSort(string key)
    {
        EnsureSortLoaded();
        if (_sortKey != key)
            SetProductSort(key, false);
        else if (!_sortDescending)
            SetProductSort(key, true);
        else
            SetProductSort("", false);
    }

    /// <summary>Ключ для сравнения строк: без регистра, ё = е (алфавит кириллицы по кодам совпадает с русским порядком).</summary>
    internal static string SortText(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Коды из одних цифр — как числа (сначала короче), остальные — по алфавиту.</summary>
    private static int CompareCode(string a, string b)
    {
        static bool Digits(string s)
        {
            foreach (var ch in s)
                if (ch is < '0' or > '9')
                    return false;
            return true;
        }

        if (Digits(a) && Digits(b))
        {
            var ta = a.TrimStart('0');
            var tb = b.TrimStart('0');
            if (ta.Length != tb.Length)
                return ta.Length.CompareTo(tb.Length);
            var c = string.CompareOrdinal(ta, tb);
            return c != 0 ? c : a.Length.CompareTo(b.Length);
        }

        return string.CompareOrdinal(a, b);
    }

    /// <summary>2026-10-30: порядок остатков «сначала 0»: нули, потом минусовые (от меньшего), потом положительные (по возрастанию).</summary>
    internal static int CompareStockZeroFirst(double a, double b)
    {
        static int Rank(double v) => Math.Abs(v) < 0.0005 ? 0 : v < 0 ? 1 : 2;
        var c = Rank(a).CompareTo(Rank(b));
        return c != 0 ? c : Rank(a) == 0 ? 0 : a.CompareTo(b);
    }

    /// <summary>Сортировка с предвычисленными ключами. Пустые значения (нет названия/кода/категории, цена или закупка 0) — в конце при любом
    /// направлении; остаток — число (отрицательные, затем 0, 1, 2 …). При равенстве — по названию, затем исходный порядок (стабильно).</summary>
    public static List<CatalogProductTileVm> SortProducts(IReadOnlyList<CatalogProductTileVm> items, string key, bool descending)
    {
        var n = items.Count;
        var names = new string[n];
        var texts = key is "code" or "category" ? new string[n] : null;
        var nums = key is "price" or "cost" or "stock" ? new double[n] : null;
        for (var i = 0; i < n; i++)
        {
            var p = items[i];
            names[i] = SortText(p.Title);
            if (texts is not null)
                texts[i] = key == "code"
                    ? (string.IsNullOrWhiteSpace(p.Article) ? SortText(p.Barcode) : SortText(p.Article))
                    : SortText(p.Category);
            else if (nums is not null)
            {
                var v = key switch { "price" => p.PriceValue, "cost" => p.PurchasePrice, _ => p.Quantity };
                // цена/закупка 0 — «не заполнено»; остаток 0 — настоящее значение
                nums[i] = !double.IsFinite(v) || (key != "stock" && v <= 0) ? double.NaN : v;
            }
        }

        var dir = descending ? -1 : 1;
        var order = new int[n];
        for (var i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c;
            if (nums is not null)
            {
                var na = double.IsNaN(nums[a]);
                var nb = double.IsNaN(nums[b]);
                // 2026-10-30 (приёмка 1.17.67): «Остаток ▲» = «сначала 0» — нули, затем минусовые, затем положительные (раньше минусовые шли раньше нулей).
                c = na || nb ? na.CompareTo(nb) : key == "stock" && !descending ? CompareStockZeroFirst(nums[a], nums[b]) : dir * nums[a].CompareTo(nums[b]);
            }
            else
            {
                var sa = texts is null ? names[a] : texts[a];
                var sb = texts is null ? names[b] : texts[b];
                var ea = sa.Length == 0;
                var eb = sb.Length == 0;
                c = ea || eb ? ea.CompareTo(eb) : dir * (key == "code" ? CompareCode(sa, sb) : string.CompareOrdinal(sa, sb));
            }

            if (c != 0)
                return c;
            if (texts is not null || nums is not null)
            {
                c = string.CompareOrdinal(names[a], names[b]);
                if (c != 0)
                    return c;
            }

            return a.CompareTo(b);
        });
        var result = new List<CatalogProductTileVm>(n);
        foreach (var i in order)
            result.Add(items[i]);
        return result;
    }

    /// <summary>Метки срока годности у товаров каталога — из справочника сервера (ProductExpiryIndex).</summary>
    public static void ApplyExpiryBadges()
    {
        var marks = ProductExpiryIndex.Marks;
        foreach (var p in CatalogCacheService.Products.ToList())
        {
            if (marks.TryGetValue(p.Id, out var m))
            {
                p.ExpiryBadgeText = ProductExpiryIndex.BadgeText(m);
                p.ExpiryExpired = m.Expired;
            }
            else
            {
                p.ExpiryBadgeText = null;
                p.ExpiryExpired = false;
            }
        }
    }

    /// <summary>2026-11-02 (аудит №2, поиск Склада, скорость на 15 000 товаров): всё, что нужно фильтру, собирается на потоке окна (запрос, точный код, снимок каталога);
    /// сам перебор и сортировка (RunProductFilter) идут в фоне по снимку, а в интерфейс уходит готовый список. Раньше перебор каталога шёл на потоке окна на каждый запрос.</summary>
    private sealed record ProductFilterContext(
        string Query, bool ExtraSearch, string? VariantProductId, CatalogProductTileVm? ExactHit, CatalogProductTileVm[] Products,
        string SaleUnitFilter, HashSet<string>? Changed, string StockFilter, IReadOnlyDictionary<string, ProductExpiryIndex.Mark> Expiry,
        bool HasSort, string SortKey, bool SortDescending, string? CodeQuery = null);

    private int _filterVersion;

    private ProductFilterContext PrepareProductFilter()
    {
        var query = _productSearchText.Trim();
        // 2026-10-06, владелец: «касса не видит товары на складе — при сканере, даже если товар есть на складе, говорит нет».
        // Поиск склада знал только основной штрихкод товара: скан этикетки размера одежды или доп. штрихкода давал пустой
        // список. Теперь — и штрихкод размера (VariantBarcodeIndex), и доп. штрихкоды.
        // В «Продуктах» склад ищет как раньше (владелец: «главное не трогай продуктовый»).
        var extraSearch = MarketSpheres.IsClothing || MarketSpheres.IsServices;
        // 2026-11-01 (контрольный аудит, С2-25): точный поиск по коду (FindLocal разбирает базу и доп. штрихкоды всего каталога) — только для запроса, похожего на код
        // (без пробелов и с цифрой); «молоко» и «сок яблочный» ищутся обычным SearchMatch, без тяжёлого прохода на каждое нажатие.
        var codeLike = query.Length >= 4 && !query.Any(char.IsWhiteSpace) && query.Any(char.IsDigit);
        var variantProductId = codeLike && MarketSpheres.IsClothing ? VariantBarcodeIndex.ProductIdFor(query) : null;
        // 2026-10-21: точный код — тем же поиском, что касса (ProductCodeLookup): доп. штрихкод, 12 ↔ 13 цифр, весовая
        // этикетка, артикул (склад не находит товар по скану). Найденный товар — первой строкой и мимо фильтров
        // наличия/единицы: скан — прямой запрос этого товара. Остальной поиск «Продуктов» — как был.
        // 2026-11-05 (аудит №3, Т3-15): сам FindLocal (читает базу кассы и разбирает весь каталог) уходит в фон — в RunProductFilter; на потоке окна остаётся только снимок.
        return new ProductFilterContext(query, extraSearch, variantProductId, null, CatalogCacheService.Products.ToArray(),
            _saleUnitFilter, _changedKeys, _stockFilter, ProductExpiryIndex.Marks, ProductSortKey.Length > 0, _sortKey, _sortDescending, codeLike ? query : null);
    }

    private static List<CatalogProductTileVm> RunProductFilter(ProductFilterContext c)
    {
        var query = c.Query;
        // 2026-11-05 (Т3-15): точный товар по коду — здесь (в фоне), тот же объект из снимка каталога, что и раньше давал InCatalog.
        var exactFound = c.ExactHit;
        if (exactFound is null && c.CodeQuery is { Length: > 0 } codeQuery && ProductCodeLookup.FindLocal(codeQuery)?.Product is { } foundByCode)
            exactFound = c.Products.FirstOrDefault(p => string.Equals(p.Id, foundByCode.Id, StringComparison.OrdinalIgnoreCase)) ?? foundByCode;
        var variantProductId = c.VariantProductId;
        IEnumerable<CatalogProductTileVm> source = string.IsNullOrEmpty(query)
            ? c.Products
            // 2026-10-29: единый поиск SearchMatch — слова в любом порядке, ё=е, код/артикул/бренд, раскладка и транслит («Asu» → «Асу»).
            : NurMarketKassa.Core.Application.SearchMatch.Products(c.Products, query, p =>
                (c.ExtraSearch && query.Length >= 4 && (p.AlternateBarcodesRaw?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) ||
                (variantProductId != null && string.Equals(p.Id, variantProductId, StringComparison.OrdinalIgnoreCase)));
        source = c.SaleUnitFilter switch
        {
            "weight" => source.Where(p => p.IsWeighted),
            "piece" => source.Where(p => !p.IsWeighted && !p.HasPieceOption),
            "piecepackage" => source.Where(p => !p.IsWeighted && p.HasPieceOption),
            _ => source,
        };
        if (c.Changed is { } changed)
            source = source.Where(p => changed.Contains(p.Id) || changed.Contains("name:" + p.Title));
        // 2026-10-06, редизайн склада: фильтр наличия; «Срок годности» — сначала просроченные, потом ближайшие.
        var expiry = c.Expiry;
        source = c.StockFilter switch
        {
            "low" => source.Where(p => p.IsLowStock && p.Quantity > 0),
            "out" => source.Where(p => p.Quantity <= 0),
            "expiry" => source.Where(p => expiry.ContainsKey(p.Id)).OrderBy(p => expiry[p.Id].DaysLeft),
            _ => source,
        };
        // 2026-10-30, владелец: сортировка по столбцу — по ВСЕМУ отфильтрованному списку (встроенная сортировка таблицы упорядочивала
        // только видимую страницу из 50 строк). Ключи считаются один раз на строку (15 000 — десятки мс).
        if (c.HasSort)
            source = SortProducts(source as List<CatalogProductTileVm> ?? source.ToList(), c.SortKey, c.SortDescending);
        if (exactFound is { } exactHit)
            source = new[] { exactHit }.Concat(source.Where(p => !string.Equals(p.Id, exactHit.Id, StringComparison.OrdinalIgnoreCase)));
        return source.ToList();
    }

    private void ApplyFilteredProducts(List<CatalogProductTileVm> list, ObservableCollection<CatalogProductTileVm>? prebuilt = null)
    {
        // Одна замена коллекции вместо Clear()+Add() по одному (2026-09-07, оптимизация для
        // слабых ПК): каждый Add — событие CollectionChanged и перерасчёт DataGrid, на каталоге
        // в несколько тысяч товаров это заметно подвешивало окно на каждый символ поиска.
        // 2026-11-05 (аудит №3, Т3-15): коллекция на 15 тысяч строк собирается в фоне (prebuilt), на потоке окна — только подмена.
        FilteredProducts = prebuilt ?? new ObservableCollection<CatalogProductTileVm>(list);
        OnPropertyChanged(nameof(FilteredProducts));
        OnPropertyChanged(nameof(ProductCountText));

        // Новый поиск/фильтр — новый список результатов, старый номер страницы (например,
        // страница 5) почти наверняка за пределами нового TotalProductPages.
        _currentPage = 1;
        RebuildPagedProducts();
    }

    private void ApplyProductFilter()
    {
        Interlocked.Increment(ref _filterVersion); // отменяет фоновый расчёт, запущенный раньше
        ApplyFilteredProducts(RunProductFilter(PrepareProductFilter()));
    }

    /// <summary>Поиск применяется через 180 мс после последнего нажатия (debounce), а не на каждый
    /// символ — иначе быстрый набор запроса на большом каталоге фильтрует список 5–10 раз подряд.
    /// Escape/сброс и фильтр «Единица продажи» по-прежнему применяются сразу.
    /// 2026-11-02 (аудит №2): сам перебор каталога — в фоне, окно в это время не замирает.</summary>
    private CancellationTokenSource? _searchDebounce;

    private void ScheduleProductFilter()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        var token = cts.Token;
        var version = Interlocked.Increment(ref _filterVersion);
        _ = Task.Delay(180, token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _filterVersion))
                    return;
                var context = PrepareProductFilter();
                _ = Task.Run(() => { var found = RunProductFilter(context); return (List: found, Collection: new ObservableCollection<CatalogProductTileVm>(found)); }).ContinueWith(done =>
                {
                    if (done.IsFaulted)
                    {
                        PosLogger.Log($"Склад: фильтр товаров в фоне не выполнен ({done.Exception?.GetBaseException().Message}) — считаем на потоке окна.", "WARNING");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (version == Volatile.Read(ref _filterVersion))
                                ApplyProductFilter();
                        });
                        return;
                    }
                    if (!done.IsCompletedSuccessfully)
                        return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (version == Volatile.Read(ref _filterVersion) && !token.IsCancellationRequested)
                            ApplyFilteredProducts(done.Result.List, done.Result.Collection);
                    });
                }, TaskScheduler.Default);
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>Строки приёмки: сколько привезли и почём (см. PurchaseReceivingService).</summary>
    public ObservableCollection<ReceivingLineVm> ReceivingLines { get; } = new();

    /// <summary>Поставщики для приёмки. Первая строка — «без поставщика»: тогда приход пишется
    /// прямо в товар, как у сайта без выбранного поставщика.</summary>
    public ObservableCollection<PurchaseReceivingService.Supplier> ReceivingSuppliers { get; } = new();

    private PurchaseReceivingService.Supplier? _receivingSupplier;
    public PurchaseReceivingService.Supplier? ReceivingSupplier
    {
        get => _receivingSupplier;
        set
        {
            if (_receivingSupplier == value)
                return;
            _receivingSupplier = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReceivingSupplier));
        }
    }

    public bool HasReceivingSupplier => _receivingSupplier is not null && !string.IsNullOrEmpty(_receivingSupplier.Id);

    private bool _receivingPaidNow; // 2026-11-01 (С2-32): по умолчанию «В долг»
    /// <summary>С поставщиком: true — оплачено сразу, false — в долг поставщику.</summary>
    public bool ReceivingPaidNow
    {
        get => _receivingPaidNow;
        set
        {
            if (_receivingPaidNow == value)
                return;
            _receivingPaidNow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>2026-10-20, этап П3: «В долг» — срок оплаты поставщику в днях (по умолчанию 30): дата оплаты долга = день прихода + срок.</summary>
    public int ReceivingDebtDays { get; set; } = 30;

    /// <summary>2026-10-30 (нумерация закупок): номер и дата накладной поставщика (из импорта накладной) — уходят с приходом и показываются рядом с номером документа.</summary>
    public string? ReceivingSupplierDocNumber { get; set; }
    public DateTime? ReceivingSupplierDocDate { get; set; }

    public async Task LoadReceivingSuppliersAsync()
    {
        if (ReceivingSuppliers.Count > 0)
            return;

        ReceivingSuppliers.Add(new PurchaseReceivingService.Supplier("",
            Tr.T("Без поставщика", "Жеткирүүчүсүз", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz")));
        ReceivingSupplier = ReceivingSuppliers[0];
        try
        {
            foreach (var supplier in await PurchaseReceivingService.Instance.LoadSuppliersAsync().ConfigureAwait(true))
                ReceivingSuppliers.Add(supplier);
        }
        catch (Exception ex)
        {
            SessionExpiryMonitor.LogFailure("receiving-suppliers", ex, $"Приёмка: список поставщиков не загружен: {ex.Message}"); // 2026-11-06: потеря сессии — раз в 10 минут
        }
    }

    public ICommand CommitReceivingCommand { get; }

    public ICommand CommitRevisionCommand { get; }
    public ICommand WriteOffCommand { get; }

    public event Action<RevisionLineVm>? RevisionLineAdded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public RevisionLineVm? FocusedRevisionLine
    {
        get => _focusedRevisionLine;
        set { _focusedRevisionLine = value; OnPropertyChanged(); }
    }

    public string WriteOffProductName
    {
        get => _writeOffProductName;
        set { _writeOffProductName = value; OnPropertyChanged(); }
    }

    public string WriteOffBarcode
    {
        get => _writeOffBarcode;
        set { _writeOffBarcode = value; OnPropertyChanged(); }
    }

    public double WriteOffQuantity
    {
        get => _writeOffQuantity;
        set
        {
            _writeOffQuantity = value;
            OnPropertyChanged();
            (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string WriteOffReason
    {
        get => _writeOffReason;
        set { _writeOffReason = value; OnPropertyChanged(); }
    }

    public Task EnsureCatalogLoadedAsync()
    {
        ApplyProductFilter();
        return Task.CompletedTask;
    }

    /// <summary>Скан в приёмке. Повторный скан того же товара не плодит строки, а прибавляет
    /// единицу: коробку с накладной обычно пробивают подряд, а не пересчитывают в уме. Новый
    /// штрихкод ищется по складу, по общей базе CRM и только потом считается неизвестным.</summary>
    public async Task HandleReceivingScanAsync(string barcode, double quantity = 1)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return;

        var trimmed = barcode.Trim();

        // 2026-11-09 (владелец: «при повторном скане того же товара остановить сканер и спросить количество»): тот же код сразу после добавления — окно «Сколько штук?»
        // (окно задаёт приёмка через AskRepeatQuantity; без него и при выключенной настройке — как прежде, +количество). Весовая этикетка и код без строки в списке не спрашивают.
        if (AskRepeatQuantity is { } ask
            && RepeatScanGuard.Check(trimmed) == ReceivingScanVerdict.Repeat
            && ReceivingRepeatScan.FindLine(ReceivingLines, trimmed) is { } repeatLine)
        {
            var add = await ask(repeatLine, quantity > 0 ? quantity : 1).ConfigureAwait(true);
            RepeatScanGuard.Counted(trimmed); // после окна (любой исход) этот код по-прежнему «последний»: ещё один скан подряд — снова вопрос
            if (add is { } n && n > 0 && ReceivingLines.Contains(repeatLine))
            {
                ReceivingRepeatScan.AddTo(repeatLine, n);
                MoveReceivingLineToTop(repeatLine);
                ReceivingLineAdded?.Invoke(repeatLine);
            }
            return;
        }

        var existing = ReceivingLines.FirstOrDefault(l =>
            string.Equals(l.Barcode, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // 2026-11-11 (Б-4, видео «Ак Кант»): в режиме упаковок повторный скан прибавлял 1 ШТУКУ, а колонка показывает упаковки (40 → 40,04 — «не меняется»).
            // Теперь — в тех же единицах, что в колонке «Принимаем» (ReceivingRepeatScan.AddTo: упаковка в режиме упаковок, иначе штука).
            ReceivingRepeatScan.AddTo(existing, quantity);
            PurchaseReceivingService.LogScan(trimmed, "повторный скан", existing.PackMode ? $"+{quantity:0.###} упак." : $"+{quantity:0.###}");
            RepeatScanGuard.Counted(trimmed);
            MoveReceivingLineToTop(existing); // 2026-10-24, ТЗ «Склад» Б1: найденная строка поднимается наверх
            ReceivingLineAdded?.Invoke(existing);
            return;
        }

        // 2026-11-11 (владелец: «на приёмке многие товары, которые не находит, показывает как ВЕСОВОЙ; в массовом сканировании всё чётко»): порядок как в массовом —
        // точный штрихкод в базе кассы (и доп. коды/размеры) → склад на сервере (только товар ИМЕННО с этим штрихкодом) → общая база NurCRM → и только если товар
        // нигде не найден, код может быть этикеткой весов — и то лишь при настроенном у магазина префиксе весов (ProductCodeLookup.FindWeightLabel). Раньше этикетка
        // разбиралась ДО поиска, и обычный код «20…» становился чужим весовым товаром.
        ReceivingLineVm line;
        try
        {
            line = await PurchaseReceivingService.Instance.LookupAsync(trimmed, quantity).ConfigureAwait(true);
        }
        catch (AmbiguousBarcodeException ambiguous)
        {
            // 2026-11-11 (ответ Умара): один штрихкод у нескольких товаров — строку не добавляем и товар не подставляем, красное сообщение (сервер сам не выбирает случайный).
            SetReceivingNotice(ambiguous.Message, isError: true, hideAfterSeconds: 10);
            return;
        }

        // 2026-10-31 (владелец: «в приёмке весовые добавляются???»): этикетка весов — товар по коду внутри, количество = вес с этикетки
        // (раньше становилась «новым товаром» с кодом этикетки). Только если точного штрихкода с таким кодом нет.
        if (line.Source == ReceivingSource.Unknown
            && ProductCodeLookup.FindWeightLabel(trimmed) is { Product: { } weighedTile } weighedLabel)
        {
            PurchaseReceivingService.LogScan(trimmed, "весовая этикетка (префикс весов настроен)", $"товар {weighedTile.Id}, {weighedLabel.Quantity:0.###}");
            RepeatScanGuard.Counted(trimmed); // 2026-11-09: код этикетки весов запоминается, но окно не вызывает (строки с таким штрихкодом нет)
            var sameProduct = ReceivingLines.FirstOrDefault(l => string.Equals(l.ProductId, weighedTile.Id, StringComparison.OrdinalIgnoreCase));
            if (sameProduct is not null)
            {
                sameProduct.Quantity += weighedLabel.Quantity;
                MoveReceivingLineToTop(sameProduct);
                ReceivingLineAdded?.Invoke(sameProduct);
                return;
            }

            var weighedLine = PurchaseReceivingService.FromTile(weighedTile, weighedLabel.Quantity);
            weighedLine.EnableReceivingPricing(); // 2026-11-06: цена существующего товара из карточки общей наценкой не меняется
            ReceivingLines.Insert(0, weighedLine);
            ReceivingLineAdded?.Invoke(weighedLine);
            return;
        }

        // Пока шёл запрос, тот же товар могли отсканировать ещё раз.
        var raced = ReceivingLines.FirstOrDefault(l =>
            string.Equals(l.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(line.ProductId)
                && string.Equals(l.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)));
        if (raced is not null)
        {
            ReceivingRepeatScan.AddTo(raced, quantity); // 2026-11-11 (Б-4): в единицах колонки (упаковка в режиме упаковок)
            RepeatScanGuard.Counted(trimmed);
            MoveReceivingLineToTop(raced);
            ReceivingLineAdded?.Invoke(raced);
            return;
        }

        // 2026-10-24, ТЗ «Склад» Б1: новая строка встаёт сверху — у кладовщика перед глазами последний скан, а не конец длинной накладной.
        line.EnableReceivingPricing(); // 2026-10-28: ручной скан; 2026-11-06: у товара с ценой в карточке общая наценка её не меняет (только «Как у всех» / «Применить ко всем»)
        ReceivingLines.Insert(0, line);
        RepeatScanGuard.Counted(trimmed);
        ReceivingLineAdded?.Invoke(line);
    }

    /// <summary>2026-11-09: «тот же код подряд» для окна «Сколько штук?» (одна общая настройка UserPreferences.AskQtyOnRepeatScan).</summary>
    public ReceivingRepeatScanGuard RepeatScanGuard { get; } = new();

    /// <summary>2026-11-09: «Сколько штук?» — окно приёмки (строка, число по умолчанию → сколько добавить; null — «Отмена»). Без обработчика повторный скан добавляет, как раньше.</summary>
    public Func<ReceivingLineVm, double, Task<double?>>? AskRepeatQuantity { get; set; }

    /// <summary>2026-10-24, ТЗ «Склад» Б1: строка приёмки наверх списка (повторный скан / выбор уже добавленного товара).</summary>
    public void MoveReceivingLineToTop(ReceivingLineVm line)
    {
        var index = ReceivingLines.IndexOf(line);
        if (index <= 0)
            return;
        // Move таблица не отрабатывает (порядок строк на экране не менялся) — убираем и вставляем заново; IsReorderingReceiving не даёт окну
        // принять это за новую строку (сетка размеров одежды).
        IsReorderingReceiving = true;
        try
        {
            ReceivingLines.RemoveAt(index);
            ReceivingLines.Insert(0, line);
        }
        finally
        {
            IsReorderingReceiving = false;
        }
    }

    /// <summary>true, пока строка приёмки переставляется наверх (Remove + Insert) — это не добавление товара.</summary>
    public bool IsReorderingReceiving { get; private set; }

    /// <summary>2026-10-24, ТЗ «Склад» Б1 (пакет В): поставщик создан кнопкой «＋» — встаёт в список после «Без поставщика» по алфавиту и выбирается.
    /// Уже есть в списке (тот же id) — просто выбирается.</summary>
    public void SelectOrAddReceivingSupplier(PurchaseReceivingService.Supplier supplier)
    {
        var known = ReceivingSuppliers.FirstOrDefault(s => string.Equals(s.Id, supplier.Id, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            var index = 1;
            while (index < ReceivingSuppliers.Count
                   && string.Compare(ReceivingSuppliers[index].Name, supplier.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
                index++;
            ReceivingSuppliers.Insert(Math.Min(index, ReceivingSuppliers.Count), supplier);
            known = supplier;
        }
        ReceivingSupplier = known;
    }

    public event Action<ReceivingLineVm>? ReceivingLineAdded;

    // 2026-10-27 (ревью Ск-2): накладные, строки которых сейчас лежат в приёмке. Перед «Оприходовать» их снова сверяют с журналом (не оприходована ли
    // уже эта накладная, нет ли её в списке дважды), после постановки прихода в очередь — помечают проведёнными. Опустел список — забываем.
    private readonly List<NurMarketKassa.Services.InvoiceImport.InvoiceImportEntry> _invoicesInReceiving = new();
    private bool _invoiceWatchHooked;
    private bool _invoicesPosting; // 2026-11-05: идёт проведение приёмки — опустевший список не значит «очищен»

    /// <summary>Накладная перенесена в приёмку (см. InvoiceImportFlow).</summary>
    public void NoteInvoiceInReceiving(NurMarketKassa.Services.InvoiceImport.InvoiceImportEntry entry)
    {
        _invoicesInReceiving.Add(entry);
        if (_invoiceWatchHooked)
            return;
        _invoiceWatchHooked = true;
        ReceivingLines.CollectionChanged += (_, _) =>
        {
            if (ReceivingLines.Count == 0)
            {
                // 2026-11-05 (Н-7): список очищен без проведения — накладные не «лежат в приёмке», ложного предупреждения о дубле быть не должно.
                if (!_invoicesPosting)
                    NurMarketKassa.Services.InvoiceImport.InvoiceImportRegistry.ForgetUnposted(_invoicesInReceiving.Select(e => e.Id).ToList());
                _invoicesInReceiving.Clear();
            }
        };
    }

    /// <summary>Повтор накладной перед «Оприходовать»: окно спрашивает «Оприходовать всё равно?». false — приёмка не проводится.</summary>
    public Func<IReadOnlyList<NurMarketKassa.Services.InvoiceImport.InvoiceImportEntry>, Task<bool>>? ConfirmInvoiceRepeats { get; set; }

    /// <summary>2026-10-24, ТЗ «Склад» (Б1): подтверждение перед «Оприходовать», если у товаров со склада меняются цены продажи («Цена продажи изменится у K товаров»).
    /// Без обработчика (другие окна) — как раньше, без вопроса. false — приёмка не проводится.</summary>
    public Func<IReadOnlyList<ReceivingLineVm>, Task<bool>>? ConfirmSalePriceChanges { get; set; }

    /// <summary>2026-10-29 (спека Fable): подтверждение перед «Провести», если у строк цена продажи ниже закупки. Без обработчика — без вопроса. false — приёмка не проводится.</summary>
    public Func<IReadOnlyList<ReceivingLineVm>, Task<bool>>? ConfirmBelowCost { get; set; }

    /// <summary>2026-11-07 («поштучная продажа» в приёмке): перед «Провести», если есть строки «упаковка с поштучной продажей», — «Товар будет вестись упаковками по N шт, остаток — в упаковках.
    /// Продолжить?» со списком. Без обработчика такие строки НЕ проводятся (меняется единица остатка товара — молча нельзя). false — приёмка не проводится.</summary>
    public Func<IReadOnlyList<ReceivingLineVm>, Task<bool>>? ConfirmPackSale { get; set; }

    /// <summary>2026-11-06 (жалоба «Орук маркет»): у строк нет цены продажи — окно «У N позиций нет цены продажи» (цена/наценка, «Оставить без цены», «Отмена»). Задаёт окно приёмки.
    /// false — приём не проводится. Без обработчика (другие окна) цена продажи, как раньше, обязательна: программа сама ничего не придумывает.</summary>
    public Func<IReadOnlyList<ReceivingLineVm>, Task<bool>>? ConfirmMissingSalePrices { get; set; }

    /// <summary>2026-10-06 (О-70): строки приёмки проведены (для этикеток на принятые размеры).</summary>
    public event Action<IReadOnlyList<ReceivingLineVm>>? ReceivingPosted;

    /// <summary>Добавить товар в приёмку из списка, а не сканом.</summary>
    public ReceivingLineVm AddReceivingProduct(CatalogProductTileVm product, double quantity)
    {
        RepeatScanGuard.Reset(); // 2026-11-09: товар выбран из списка, а не отсканирован — отсчёт «тот же код подряд» начинается заново
        var existing = ReceivingLines.FirstOrDefault(l =>
            string.Equals(l.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Quantity += quantity;
            MoveReceivingLineToTop(existing); // 2026-10-24, ТЗ «Склад» Б1: как при скане — строка наверх
            return existing;
        }

        // 2026-10-24, ТЗ «Склад» Б1: сверху (возвращаем строку — окно ставит в неё курсор).
        var line = PurchaseReceivingService.FromTile(product, quantity);
        line.EnableReceivingPricing(); // 2026-10-28: ручной выбор из списка; 2026-11-06: цена из карточки остаётся
        ReceivingLines.Insert(0, line);
        return line;
    }

    /// <summary>2026-10-21: товар не найден ни здесь, ни на сервере — окно предлагает «Создать товар» (WarehouseWindow).
    /// Без обработчика — прежнее поведение (строка ревизии без товара / предупреждение списания).</summary>
    public Func<string, bool, Task>? BarcodeNotFound { get; set; }

    public void HandleBarcodeScan(string barcode, bool isRevisionTab) =>
        _ = HandleBarcodeScanAsync(barcode, isRevisionTab).ContinueWith(
            t => PosLogger.Log($"Склад: скан '{barcode}' не обработан: {t.Exception?.GetBaseException().Message}", "WARNING"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>2026-10-21: скан в ревизии и списании. Раньше — только точный ОСНОВНОЙ штрихкод в каталоге памяти: доп.
    /// штрихкод, UPC-A 12 цифр при EAN-13 в карточке, штрихкод размера, весовая этикетка и товар, только что
    /// добавленный на сайте, давали «не найден», хотя касса тот же скан находила (склад не находит товар по скану).
    /// Теперь — общий поиск кассы (ProductCodeLookup): этот компьютер, затем сервер. Найден на компьютере — всё
    /// происходит сразу, как раньше (до первого await).</summary>
    public async Task HandleBarcodeScanAsync(string barcode, bool isRevisionTab)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;
        var trimmed = ProductCodeLookup.Normalize(barcode);
        if (trimmed.Length == 0) return;
        var local = ProductCodeLookup.FindLocal(trimmed);
        var product = InCatalog(local?.Product);
        var serverUnavailable = false;
        if (product is null)
        {
            ProductCodeLookup.Result server;
            try
            {
                server = await ProductCodeLookup.FindOnServerAsync(trimmed).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Склад: скан '{trimmed}' — поиск на сервере упал: {ex.Message}", "STOCK");
                server = new ProductCodeLookup.Result(null, true);
            }

            product = InCatalog(server.Hit?.Product);
            serverUnavailable = server.Unavailable;
        }

        PosLogger.Log(product is null
            ? $"Склад: скан '{trimmed}' ({(isRevisionTab ? "ревизия" : "списание")}) — товар не найден{(serverUnavailable ? ", сервер не ответил" : "")}."
            : $"Склад: скан '{trimmed}' ({(isRevisionTab ? "ревизия" : "списание")}) — товар {product.Id} ({local?.Kind.ToString() ?? "Server"}).", "STOCK");

        if (product is null && serverUnavailable)
        {
            _prompts?.ShowWarning(Tr.T(
                "Не удалось проверить штрихкод на сервере. Проверьте связь и повторите сканирование; создание нового товара пока недоступно.",
                "Штрихкодду серверден текшерүү мүмкүн болгон жок. Байланышты текшерип, кайра сканерлеңиз; азырынча жаңы товар түзүү жеткиликсиз.",
                "The barcode could not be verified with the server. Check the connection and scan again; creating a new product is unavailable for now.",
                "Barkod sunucuda doğrulanamadı. Bağlantıyı kontrol edip tekrar tarayın; şu anda yeni ürün oluşturma kullanılamıyor.",
                "Shtrix-kodni serverda tekshirib bo'lmadi. Ulanishni tekshirib qayta skanerlang; hozircha yangi mahsulot yaratib bo'lmaydi."));
            return;
        }

        if (product is null && BarcodeNotFound is { } notFound)
        {
            await notFound(trimmed, isRevisionTab).ConfigureAwait(true);
            return;
        }

        if (isRevisionTab)
        {
            var line = product is not null
                ? BuildRevisionLine(product)
                : new RevisionLineVm { Barcode = trimmed, ProductName = trimmed, ExpectedQty = 0, ActualQty = 1 };
            RevisionLines.Add(line);
            FocusedRevisionLine = line;
            RevisionLineAdded?.Invoke(line);
        }
        else if (product is not null)
        {
            SelectProductForWriteOff(product);
        }
        else
        {
            _writeOffProductId = "";
            WriteOffBarcode = trimmed;
            WriteOffProductName = trimmed;
            _prompts?.ShowWarning(Tr.T("Товар с этим штрих-кодом не найден в каталоге — списание недоступно.", "Бул штрихкоддуу товар каталогдон табылган жок — эсептен чыгаруу мүмкүн эмес.", "No product with this barcode in the catalog — write-off isn't available.", "Bu barkoda sahip ürün katalogda bulunamadı — düşüm yapılamaz.", "Bu shtrix-kodli mahsulot katalogda topilmadi — hisobdan chiqarib bo'lmaydi."));
        }
    }

    /// <summary>2026-10-21: товар, созданный из окна «не найден», — сразу в ревизию или в списание.</summary>
    public void ApplyScannedProduct(CatalogProductTileVm product, bool isRevisionTab)
    {
        product = InCatalog(product)!;
        if (isRevisionTab)
            AddRevisionLineForProduct(product);
        else
            SelectProductForWriteOff(product);
    }

    private void AddRevisionLineForProduct(CatalogProductTileVm product)
    {
        var line = BuildRevisionLine(product);
        RevisionLines.Add(line);
        FocusedRevisionLine = line;
        RevisionLineAdded?.Invoke(line);
    }

    private static RevisionLineVm BuildRevisionLine(CatalogProductTileVm product) => new()
    {
        ProductId = product.Id,
        Barcode = product.Barcode ?? "",
        ProductName = product.Title,
        ExpectedQty = product.Quantity,
        ActualQty = product.Quantity,
    };

    private void SelectProductForWriteOff(CatalogProductTileVm product)
    {
        _writeOffProductId = product.Id;
        WriteOffBarcode = product.Barcode ?? "";
        WriteOffProductName = product.Title;
        (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ приёмка в фоне (2026-10-25)

    // 2026-10-25, ТЗ «оприходование в фоне», пакет Q2: «Оприходовать» больше не держит модальный слой IsBusy на всё окно
    // (на стенде при ответе сервера 20 с он висел минуты). Снимок строк уходит в BackgroundReceivingQueue (файл на диске), список
    // сразу очищается; индикатор на кнопке — не дольше 0,5 с, потом плашка «Операция выполняется в фоне». Проведение, остатки
    // плиток, каталог и долг поставщику делает очередь (PurchaseReceivingService.PostAsync вызывается оттуда, формулы не менялись).

    /// <summary>Сколько ждём итог прихода на кнопке, прежде чем сказать «выполняется в фоне».</summary>
    private static readonly TimeSpan ReceivingWaitLimit = TimeSpan.FromSeconds(0.46); // 0,5 с минус запас на шаг таймера Windows (~15 мс) и возврат в поток UI

    private bool _isReceivingPosting;
    private bool _receivingCommitInProgress;
    private string _receivingBackgroundNotice = "";
    private bool _receivingNoticeIsError;
    private string? _receivingNoticeJobId;
    private int _receivingNoticeVersion;
    private readonly HashSet<string> _myReceivingJobs = new();

    /// <summary>Идёт короткое ожидание ответа (≤ 0,5 с): на кнопке «Оприходую…».</summary>
    public bool IsReceivingPosting
    {
        get => _isReceivingPosting;
        private set { _isReceivingPosting = value; OnPropertyChanged(); }
    }

    /// <summary>Плашка под кнопкой: «Операция выполняется в фоне…», итог или ошибка (не модальная). Пусто — скрыта.</summary>
    public string ReceivingBackgroundNotice
    {
        get => _receivingBackgroundNotice;
        private set { _receivingBackgroundNotice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasReceivingBackgroundNotice)); }
    }

    public bool HasReceivingBackgroundNotice => _receivingBackgroundNotice.Length > 0;

    /// <summary>Плашка — об ошибке (красноватая), иначе нейтрально-жёлтая.</summary>
    public bool ReceivingNoticeIsError
    {
        get => _receivingNoticeIsError;
        private set { _receivingNoticeIsError = value; OnPropertyChanged(); OnPropertyChanged(nameof(ReceivingNoticeIsInfo)); }
    }

    public bool ReceivingNoticeIsInfo => !_receivingNoticeIsError;

    private void SetReceivingNotice(string text, bool isError, string? jobId = null, int hideAfterSeconds = 0)
    {
        var version = ++_receivingNoticeVersion;
        _receivingNoticeJobId = jobId;
        ReceivingNoticeIsError = isError;
        ReceivingBackgroundNotice = text;
        if (hideAfterSeconds > 0)
            DispatcherTimer.RunOnce(() =>
            {
                if (version == _receivingNoticeVersion)
                    ReceivingBackgroundNotice = "";
            }, TimeSpan.FromSeconds(hideAfterSeconds));
    }

    /// <summary>2026-10-27 (приёмка 26.10, Д-9): короткое сообщение без окна (в программе владельца ShowToast открывался модальным «Сообщением»
    /// с кнопкой «Понятно») — плашка под кнопкой «Оприходовать», гаснет через 6 с.</summary>
    public void ShowReceivingInfo(string text) => SetReceivingNotice(text, isError: false, hideAfterSeconds: 6);

    // 2026-11-06: офлайн-приход — без связи плашка говорит «сохранён и будет отправлен» + счётчик неотправленных.
    private static string BackgroundNoticeText => BackgroundReceivingQueue.NoConnectionNow
        ? BackgroundReceivingQueue.SavedOfflineText + ". " + BackgroundReceivingQueue.UnsentText(BackgroundReceivingQueue.Instance.ActiveCount)
        : Tr.T(
            "Операция выполняется в фоне — можно продолжать работу",
            "Операция фондо аткарылууда — иштей берсеңиз болот",
            "The operation is running in the background — you can keep working",
            "İşlem arka planda yürütülüyor — çalışmaya devam edebilirsiniz",
            "Amal fonda bajarilmoqda — ishni davom ettirishingiz mumkin");

    /// <summary>Провести приёмку: снимок строк уходит в очередь фоновых приходов (BackgroundReceivingQueue) — новые товары создаются,
    /// остаток прибавляется, цены пишутся, каждая строка уходит в историю закупок товара (см. PurchaseReceivingService.PostAsync).</summary>
    private async Task CommitReceivingAsync()
    {
        // 2026-10-25: двойное нажатие (в том числе пока открыто подтверждение цен) — одна задача.
        if (_receivingCommitInProgress)
            return;
        _receivingCommitInProgress = true;
        (CommitReceivingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            await CommitReceivingCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsReceivingPosting = false;
            _receivingCommitInProgress = false;
            (CommitReceivingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task CommitReceivingCoreAsync()
    {
        var lines = ReceivingListOrder.InAddedOrder(ReceivingLines); // 2026-11-07: в приход — в порядке добавления («№» 1, 2, 3…), а не «новая сверху»
        var problems = PurchaseReceivingService.Validate(lines, requireSalePrice: ConfirmMissingSalePrices is null); // 2026-11-06: цену продажи спросит окно ниже
        problems.AddRange(PurchaseReceivingService.FractionalProblems(lines)); // 2026-10-31: «2,5 шт» у нового товара — выберите «кг» или целое
        if (problems.Count > 0)
        {
            var shown = problems.Take(8).ToList();
            if (problems.Count > shown.Count)
                shown.Add(Tr.T($"…и ещё {problems.Count - shown.Count}", $"…жана дагы {problems.Count - shown.Count}",
                    $"…and {problems.Count - shown.Count} more", $"…ve {problems.Count - shown.Count} tane daha",
                    $"…va yana {problems.Count - shown.Count}"));
            _prompts?.ShowWarning(Tr.T("Приёмку нельзя провести:", "Кабыл алууну өткөрүүгө болбойт:",
                "Receiving can't be posted:", "Mal kabul yapılamıyor:", "Qabulni rasmiylashtirib bo'lmaydi:")
                + "\n\n" + string.Join("\n", shown));
            return;
        }

        // 2026-11-07: «упаковка с поштучной продажей» — товар заводится с упаковкой и остатком в упаковках: явное подтверждение, без него не проводим.
        if (lines.Where(l => l.PackSale).ToList() is { Count: > 0 } packSaleLines)
        {
            if (ConfirmPackSale is not { } askPackSale)
            {
                _prompts?.ShowWarning(Tr.T("Упаковку с поштучной продажей нельзя провести без подтверждения — откройте приёмку в окне «Склад».", "Даана менен сатылуучу таңгакты ырастоосуз өткөрүүгө болбойт — кабыл алууну «Кампа» терезесинде ачыңыз.",
                    "A pack with single-piece sale can't be posted without confirmation — open receiving in the Warehouse window.", "Adetli satışlı koli onaysız kaydedilemez — mal kabulü «Depo» penceresinde açın.", "Donalab sotiladigan qadoqni tasdiqsiz o'tkazib bo'lmaydi — qabulni «Ombor» oynasida oching."));
                return;
            }
            if (!await askPackSale(packSaleLines).ConfigureAwait(true))
                return;
        }

        // 2026-11-06: нет цены продажи — спрашиваем ДО постановки в очередь: цена/наценка или «Оставить без цены» (цена товара не трогается). «Отмена» — вернуться.
        if (ConfirmMissingSalePrices is { } askMissing && ReceivingPricing.MissingSalePrice(lines).Count > 0
            && !await askMissing(lines).ConfigureAwait(true))
            return;

        // 2026-10-29, спека Fable: цена продажи НИЖЕ закупки — «Провести» спрашивает подтверждение (маржа < 10 % — проводить можно без вопроса).
        if (ConfirmBelowCost is { } askBelow
            && lines.Where(l => l.IsBelowCost).ToList() is { Count: > 0 } below
            && !await askBelow(below).ConfigureAwait(true))
            return;

        // 2026-10-24, ТЗ «Склад» (Б1): меняются цены продажи у товаров со склада — короткое подтверждение со списком (окно задаёт обработчик).
        // 2026-10-25: подтверждение остаётся до постановки в очередь (это действие кассира, а не ожидание сервера).
        if (ConfirmSalePriceChanges is { } confirm
            && ReceivingPricing.SalePriceChanges(lines) is { Count: > 0 } changed
            && !await confirm(changed).ConfigureAwait(true))
            return;

        // 2026-10-27 (ревью Ск-2): накладная из приёмки уже оприходована раньше (или лежит в списке дважды) — приход и долг поставщику удвоились бы.
        if (_invoicesInReceiving.Count > 0
            && NurMarketKassa.Services.InvoiceImport.InvoiceImportRegistry.FindRepeats(_invoicesInReceiving) is { Count: > 0 } repeats
            && (ConfirmInvoiceRepeats is not { } askRepeat || !await askRepeat(repeats).ConfigureAwait(true)))
            return;

        // 2026-10-25: 0,5 с считаем от постановки в очередь (запись на диск входит в них), а не после неё.
        var sinceQueued = System.Diagnostics.Stopwatch.StartNew();
        string jobId;
        var duplicate = false;
        try
        {
            var supplier = HasReceivingSupplier ? ReceivingSupplier : null;
            var employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId;
            jobId = BackgroundReceivingQueue.Instance.Enqueue(
                new ReceivingJobRequest(ReceivingJobSource.Warehouse, lines, supplier, ReceivingPaidNow, employee, ReceivingDebtDays,
                    SupplierDocNumber: ReceivingSupplierDocNumber, SupplierDocDate: ReceivingSupplierDocDate),
                out duplicate);
            ReceivingSupplierDocNumber = null;
            ReceivingSupplierDocDate = null;
        }
        catch (InvalidOperationException ex)
        {
            // Очередь закрыта (повреждён файл) или диск не записался: строки остаются в окне, показываем причину (не модально).
            PosLogger.Log($"Приёмка: не поставлена в очередь: {ex.Message}", "WARNING");
            SetReceivingNotice(ex.Message, isError: true);
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка не поставлена в очередь: {ex}", "WARNING");
            SetReceivingNotice(Tr.T("Не удалось провести приёмку: ", "Кабыл алуу өткөрүлгөн жок: ",
                "Receiving failed: ", "Mal kabul yapılamadı: ", "Qabulni rasmiylashtirib bo'lmadi: ") + ex.Message, isError: true);
            return;
        }

        // Задача принята и записана на диск — список готов к следующему приходу сразу (убираем ровно отправленные строки).
        foreach (var packed in lines)
            ReceivingPackSizes.Remember(packed); // 2026-11-07: «штук в упаковке» запоминается локально (как в «Закупки → Приём товара»)
        if (!duplicate)
            _myReceivingJobs.Add(jobId);
        // 2026-10-27 (ревью Ск-2): накладные этой приёмки считаются проведёнными (повторная загрузка того же файла предупредит). Ids берём до опустошения списка.
        var postedInvoiceIds = _invoicesInReceiving.Select(e => e.Id).ToList();
        _invoicesPosting = true;
        try
        {
            foreach (var line in lines)
                ReceivingLines.Remove(line);
        }
        finally
        {
            _invoicesPosting = false;
        }
        NurMarketKassa.Services.InvoiceImport.InvoiceImportRegistry.MarkPosted(postedInvoiceIds);
        _invoicesInReceiving.Clear();
        if (duplicate)
            return;

        // Короткий индикатор на кнопке (≤ 0,5 с): успел — обычный итог, нет — плашка «в фоне».
        SetReceivingNotice("", isError: false);
        IsReceivingPosting = true;
        ReceivingJob? done;
        try
        {
            done = await BackgroundReceivingQueue.Instance.WaitAsync(jobId, TimeSpan.FromMilliseconds(Math.Max(50, (ReceivingWaitLimit - sinceQueued.Elapsed).TotalMilliseconds))).ConfigureAwait(true);
        }
        finally
        {
            IsReceivingPosting = false;
        }

        if (done is null)
        {
            // JobFinished мог прийти, пока мы возвращались в поток UI, — тогда плашка уже не нужна.
            if (_myReceivingJobs.Contains(jobId))
                SetReceivingNotice(BackgroundNoticeText, isError: false, jobId);
            return;
        }
        ShowReceivingJobResult(done);
    }

    /// <summary>Итог задачи, завершившейся за 0,5 с: успех — лёгкое уведомление (в программе владельца — плашка в окне), ошибка —
    /// плашка; строки, которые точно не проведены, возвращаются в список (как было: непринятые остаются, их можно поправить).</summary>
    private void ShowReceivingJobResult(ReceivingJob job)
    {
        var summary = job.Summary ?? "";
        if (job.Status == ReceivingJobStatus.Done && job.Errors.Count == 0)
        {
            if (!MainStatusViewModel.TryShowInfoToast(summary))
                SetReceivingNotice(summary, isError: false, hideAfterSeconds: 10);
            return;
        }

        var restored = job.Status is ReceivingJobStatus.Failed or ReceivingJobStatus.Partial or ReceivingJobStatus.NeedsCheck
            ? RestoreReceivingLines(BackgroundReceivingQueue.Instance.TakeBackLines(job.Id))
            : 0;
        var details = job.Errors.Count > 0 ? string.Join("\n", job.Errors.Take(10)) : job.LastError ?? "";
        var text = (summary.Length > 0 ? summary + "\n" : "") + details;
        if (restored > 0)
            text += "\n" + Tr.T($"Не принятые строки ({restored}) возвращены в список.", $"Кабыл алынбаган саптар ({restored}) тизмеге кайтарылды.",
                $"Lines not received ({restored}) were returned to the list.", $"Alınmayan satırlar ({restored}) listeye geri döndü.",
                $"Qabul qilinmagan qatorlar ({restored}) ro'yxatga qaytarildi.");
        SetReceivingNotice(text.Trim(), isError: true);
    }

    /// <summary>Задача из этого окна завершилась (JobFinished, уже в потоке UI): убрать плашку «в фоне» и предложить этикетки на принятые
    /// размеры. Уведомление об итоге делает общий индикатор (пакет Q5), здесь только состояние окна.</summary>
    public void OnReceivingJobFinished(ReceivingJob job)
    {
        if (!_myReceivingJobs.Remove(job.Id))
            return;
        if (_receivingNoticeJobId == job.Id)
        {
            _receivingNoticeJobId = null;
            ReceivingBackgroundNotice = "";
        }
        var posted = job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).Select(l => l.ToVm()).ToList();
        // 2026-10-06 (О-70): окно склада предложит этикетки на принятые размеры.
        if (posted.Count > 0)
            ReceivingPosted?.Invoke(posted);
    }

    /// <summary>Точка входа для «Вернуть строки в приёмку» (окно «Приходы в фоне», пакет Q5): вставить в список приёмки строки,
    /// которые вернул <see cref="BackgroundReceivingQueue.TakeBackLines"/>. Одинаковый товар с той же ценой закупки складывается в одну строку.
    /// Вызывать в потоке UI (из других окон — через <c>WarehouseWindow.TryRestoreReceivingLines</c>). Возвращает число принятых строк.</summary>
    public int RestoreReceivingLines(IReadOnlyList<ReceivingLineVm> restored)
    {
        foreach (var line in restored)
        {
            var same = line.VariantQuantities is { Count: > 0 } ? null : ReceivingLines.FirstOrDefault(l =>
                l.VariantQuantities is not { Count: > 0 }
                && l.Source == line.Source
                && Math.Abs(l.PurchasePrice - line.PurchasePrice) < 0.0001
                && ((!string.IsNullOrWhiteSpace(line.ProductId) && string.Equals(l.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(line.ProductId) && !string.IsNullOrWhiteSpace(line.Barcode)
                        && string.Equals(l.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase))));
            if (same is not null)
                same.Quantity += line.Quantity;
            else
                ReceivingLines.Insert(0, line);
        }
        return restored.Count;
    }

    private async Task CommitRevisionAsync()
    {
        if (_inventoryApi is null)
        {
            _prompts?.ShowError(Tr.T("Сервис ревизии недоступен в этом режиме.", "Бул режимде ревизия кызматы жеткиликсиз.", "The stocktake service isn't available in this mode.", "Sayım hizmeti bu modda kullanılamıyor.", "Bu rejimda inventarizatsiya xizmati mavjud emas."));
            return;
        }

        var countable = RevisionLines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        var skipped = RevisionLines.Count - countable.Count;
        if (countable.Count == 0)
        {
            _prompts?.ShowWarning(Tr.T("В акте нет позиций, привязанных к товару из каталога.", "Актта каталогдогу товарга байланган позициялар жок.", "The document has no lines linked to a catalog product.", "Tutanakta katalogdaki bir ürüne bağlı kalem yok.", "Aktda katalogdagi mahsulotga bog'langan pozitsiyalar yo'q."));
            return;
        }

        IsBusy = true;
        (CommitRevisionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            var items = countable
                .Select(l => new InventorySessionItem(l.ProductId, l.ActualQty))
                .ToList();

            var sessionId = await _inventoryApi
                .CreateSessionAsync("Ревизия из кассы", items, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _prompts?.ShowError(Tr.T("Не удалось создать акт ревизии на сервере.", "Серверде ревизия актысын түзүү мүмкүн болгон жок.", "Could not create the stocktake document on the server.", "Sunucuda sayım tutanağı oluşturulamadı.", "Serverda inventarizatsiya aktini yaratib bo'lmadi."));
                return;
            }

            await _inventoryApi.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var line in countable)
                ApplyCountedStock(line.ProductId, line.ActualQty);

            RevisionLines.Clear();
            FocusedRevisionLine = null;
            // 2026-11-02 (приёмка 1.17.73, п. 1): есть пропущенные позиции (число) — окном; без пропусков — обычная плашка «проведена».
            if (skipped > 0)
                _prompts?.ShowWarning(Tr.T($"Ревизия проведена. Пропущено позиций без привязки к товару: {skipped}.",
                    $"Ревизия өткөрүлдү. Товарга байланбаган позициялар өткөрүлүп жиберилди: {skipped}.",
                    $"Stocktake posted. Lines skipped (not linked to a product): {skipped}.",
                    $"Sayım tamamlandı. Ürüne bağlı olmadığı için atlanan kalem: {skipped}.",
                    $"Inventarizatsiya o'tkazildi. Mahsulotga bog'lanmagan, o'tkazib yuborilgan pozitsiyalar: {skipped}."));
            else
                _prompts?.ShowToast(Tr.T("Ревизия проведена.", "Ревизия өткөрүлдү.", "Stocktake posted.", "Sayım tamamlandı.", "Inventarizatsiya o'tkazildi."));
        }
        catch (ApiException ex)
        {
            var revFail = ServerOutageMonitor.FriendlyError(ex); // 2026-11-05 (С2-38): без сырого ex.Message
            _prompts?.ShowError(Tr.T($"Не удалось провести ревизию: {revFail}", $"Ревизияны өткөрүү мүмкүн болгон жок: {revFail}", $"Could not post the stocktake: {revFail}", $"Sayım yapılamadı: {revFail}", $"Inventarizatsiyani o'tkazib bo'lmadi: {revFail}"));
        }
        catch (HttpRequestException)
        {
            _prompts?.ShowError(Tr.T("Не удалось провести ревизию — нет сети.", "Ревизияны өткөрүү мүмкүн болгон жок — тармак жок.", "Could not post the stocktake — no network.", "Sayım yapılamadı — ağ bağlantısı yok.", "Inventarizatsiyani o'tkazib bo'lmadi — tarmoq yo'q."));
        }
        finally
        {
            IsBusy = false;
            (CommitRevisionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Task WriteOffAsync() => RunWriteOffAsync();

    /// <summary>Списание выбранного товара. true — акт проведён. Публичный для окна «Списание»
    /// в меню «Ещё» кассы (2026-09-27): оно ждёт результат, чтобы закрыться только при успехе.</summary>
    public async Task<bool> RunWriteOffAsync()
    {
        if (_inventoryApi is null)
        {
            _prompts?.ShowError(Tr.T("Сервис списания недоступен в этом режиме.", "Бул режимде эсептен чыгаруу кызматы жеткиликсиз.", "The write-off service isn't available in this mode.", "Düşüm hizmeti bu modda kullanılamıyor.", "Bu rejimda hisobdan chiqarish xizmati mavjud emas."));
            return false;
        }

        // 2026-11-03 (аудит №3, Т3-03): списание — по праву «Склад: редактирование» (владелец и администратор — всегда; сотрудник — пока сервер явно не запретил).
        if (App.AppHost?.Services.GetService(typeof(IPermissionService)) is IPermissionService perm && !perm.HasPermission(PosPermissions.EditProducts))
        {
            _prompts?.ShowError(NurMarketKassa.AvaloniaHost.Views.WarehouseWindow.NoEditRightText);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_writeOffProductId))
        {
            _prompts?.ShowWarning(Tr.T("Выберите товар для списания.", "Эсептен чыгаруу үчүн товар тандаңыз.", "Select a product to write off.", "Düşülecek ürünü seçin.", "Hisobdan chiqarish uchun mahsulotni tanlang."));
            return false;
        }

        if (WriteOffQuantity <= 0)
        {
            _prompts?.ShowWarning(Tr.T("Укажите количество для списания.", "Эсептен чыгарылуучу санды көрсөтүңүз.", "Enter the quantity to write off.", "Düşülecek miktarı girin.", "Hisobdan chiqariladigan miqdorni kiriting."));
            return false;
        }

        var product = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Id, _writeOffProductId, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            _prompts?.ShowError(Tr.T("Товар не найден в каталоге — обновите каталог и попробуйте снова.", "Товар каталогдон табылган жок — каталогду жаңыртып, кайра аракет кылыңыз.", "Product not found in the catalog — refresh the catalog and try again.", "Ürün katalogda bulunamadı — kataloğu güncelleyip tekrar deneyin.", "Mahsulot katalogda topilmadi — katalogni yangilab, qayta urinib ko'ring."));
            return false;
        }

        // 2026-11-03 (аудит №3, Т3-02): списание идёт через ProductActions.ChangeStockAsync — тем же путём, что кнопка «± Остаток»: свежий остаток с сервера (не из кэша —
        // иначе откатились бы чужие продажи), один акт на действие и запись «акт в пути» (PendingChanges): после обрыва связи на «провести» повтор сначала спросит
        // сервер о прошлом акте и не спишет второй раз. Передний план — не дольше 8 с (ForegroundWriteAsync): запись не отменяется, окно освобождается,
        // итог придёт фоном. Раньше: три запроса без срока (кнопки серые до 55 с), при обрыве повтор списывал дважды.
        var productId = _writeOffProductId;
        var productName = WriteOffProductName;
        var qty = WriteOffQuantity;
        var reasonText = WriteOffReason;
        WriteOffPending = false;
        IsBusy = true;
        (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            var note = $"Списание: {WriteOffReasonForServer(reasonText)} ({qty:0.###})"; // 2026-11-05 (Т3-08): ключ по-русски, не слово языка
            var fg = await ServerAnswerWait.ForegroundWriteAsync("stock/write-off",
                ct => ProductActions.ChangeStockAsync(productId, -qty, null, note, null, ct, historyReason: reasonText)).ConfigureAwait(true);

            if (fg.Pending)
            {
                WriteOffPending = true;
                _ = fg.Completion.ContinueWith(t => Dispatcher.UIThread.Post(() => FinishBackgroundWriteOff(t, productName)), TaskScheduler.Default);
                ResetWriteOffForm();
                var pendingText = Tr.T($"Акт списания «{productName}» отправлен, но сервер ещё не ответил. Не нажимайте «Списать» ещё раз: проверьте журнал списаний чуть позже — итог появится сам.",
                    $"«{productName}» эсептен чыгаруу актысы жөнөтүлдү, бирок сервер али жооп берген жок. «Эсептен чыгаруу» баскычын кайра баспаңыз: бир аздан кийин эсептен чыгаруулар журналын текшериңиз — жыйынтык өзү чыгат.",
                    $"The write-off document for “{productName}” was sent, but the server has not answered yet. Do not press “Write off” again: check the write-off log a little later — the result will appear on its own.",
                    $"«{productName}» düşüm belgesi gönderildi ancak sunucu henüz yanıt vermedi. «Düş»e tekrar basmayın: biraz sonra düşüm günlüğünü kontrol edin — sonuç kendiliğinden görünür.",
                    $"«{productName}» hisobdan chiqarish hujjati yuborildi, lekin server hali javob bermadi. «Hisobdan chiqarish»ni qayta bosmang: birozdan keyin hisobdan chiqarishlar jurnalini tekshiring — natija o'zi paydo bo'ladi.");
                Dispatcher.UIThread.Post(() => _ = NurMarketKassa.AvaloniaHost.Views.Dialogs.PosAlertDialog.ShowAsync(null,
                    Tr.T("Списание отправлено", "Эсептен чыгаруу жөнөтүлдү", "Write-off sent", "Düşüm gönderildi", "Hisobdan chiqarish yuborildi"), pendingText,
                    NurMarketKassa.AvaloniaHost.Views.Dialogs.PosAlertKind.Info), DispatcherPriority.Background);
                return true;
            }

            var result = fg.Value!;
            if (!result.Ok)
            {
                _prompts?.ShowError(result.Message);
                return false;
            }

            InventoryApiService.InvalidateActsCache(); // 2026-10-27, В-2: свой акт сразу виден в списке сервера
            LoadWriteOffHistory();
            ResetWriteOffForm();
            // 2026-10-27, аудит владельца С-11: успех — плашкой, без окна с кнопкой «Понятно».
            NurMarketKassa.AvaloniaHost.Views.SuccessToast.Show(Tr.T("Списание проведено.", "Эсептен чыгаруу аткарылды.", "Write-off posted.", "Düşüm tamamlandı.", "Hisobdan chiqarish o'tkazildi."));
            return true;
        }
        catch (OperationCanceledException)
        {
            // Тайм-аут «провести» — акт мог пройти. Запись «акт в пути» осталась в ProductActions: повтор сначала проверит прошлый акт.
            _prompts?.ShowError(Tr.T("Сервер не ответил вовремя — неизвестно, прошло ли списание. Нажмите «Списать» ещё раз: касса сначала проверит прошлый акт и не спишет остаток дважды.",
                "Сервер өз убагында жооп берген жок — эсептен чыгаруу өттүбү, белгисиз. «Эсептен чыгаруу» баскычын дагы бир жолу басыңыз: касса адегенде мурунку актыны текшерет жана калдыкты эки жолу чыгарбайт.",
                "The server did not answer in time — it is unknown whether the write-off went through. Press “Write off” again: the till will first check the previous document and will not write the stock off twice.",
                "Sunucu zamanında yanıt vermedi — düşümün yapılıp yapılmadığı bilinmiyor. «Düş»e tekrar basın: kasa önce önceki belgeyi kontrol eder ve stoğu iki kez düşmez.",
                "Server o'z vaqtida javob bermadi — hisobdan chiqarish o'tgani noma'lum. «Hisobdan chiqarish»ni yana bosing: kassa avval oldingi hujjatni tekshiradi va qoldiqni ikki marta chiqarmaydi."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Списание: сбой {ex.GetType().Name}: {ex.Message}", "WARNING");
            _prompts?.ShowError(ServerOutageMonitor.FriendlyError(ex));
        }
        finally
        {
            IsBusy = false;
            (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        return false;
    }

    /// <summary>2026-11-03 (аудит №3, Т3-02): true — последний вызов <see cref="RunWriteOffAsync"/> вернул управление, не дождавшись ответа сервера (акт отправлен, итог придёт фоном).</summary>
    public bool WriteOffPending { get; private set; }

    private void ResetWriteOffForm()
    {
        _writeOffProductId = "";
        WriteOffBarcode = "";
        WriteOffProductName = "";
        WriteOffQuantity = 1;
    }

    /// <summary>2026-11-03 (аудит №3, Т3-02): итог списания, ответ на которое пришёл уже после срока 8 с — плашка или окно с причиной, журнал обновляется.</summary>
    private void FinishBackgroundWriteOff(Task<ProductActions.Result> task, string productName)
    {
        if (task.IsCompletedSuccessfully && task.Result.Ok)
        {
            InventoryApiService.InvalidateActsCache();
            LoadWriteOffHistory();
            NurMarketKassa.AvaloniaHost.Views.SuccessToast.Show(Tr.T($"Списание «{productName}» проведено.", $"«{productName}» эсептен чыгаруу аткарылды.", $"Write-off of “{productName}” posted.", $"«{productName}» düşümü tamamlandı.", $"«{productName}» hisobdan chiqarish o'tkazildi."));
            return;
        }

        var reason = task.IsCompletedSuccessfully
            ? task.Result.Message
            : Tr.T($"Ответа сервера по списанию «{productName}» нет. Проверьте журнал списаний на NurCRM; повторное списание сначала проверит прошлый акт.",
                $"«{productName}» эсептен чыгаруу боюнча сервер жообу жок. NurCRM'деги эсептен чыгаруулар журналын текшериңиз; кайра эсептен чыгаруу адегенде мурунку актыны текшерет.",
                $"No server answer for the write-off of “{productName}”. Check the write-off log on NurCRM; a repeated write-off will first check the previous document.",
                $"«{productName}» düşümü için sunucu yanıtı yok. NurCRM'deki düşüm günlüğünü kontrol edin; tekrarlanan düşüm önce önceki belgeyi kontrol eder.",
                $"«{productName}» hisobdan chiqarish bo'yicha server javobi yo'q. NurCRM'dagi hisobdan chiqarishlar jurnalini tekshiring; qayta hisobdan chiqarish avval oldingi hujjatni tekshiradi.");
        if (!task.IsCompletedSuccessfully)
            PosLogger.Log($"Списание «{productName}»: фоновый итог — {task.Exception?.GetBaseException().GetType().Name ?? "отмена"}.", "WARNING");
        _ = NurMarketKassa.AvaloniaHost.Views.Dialogs.PosAlertDialog.ShowAsync(null,
            Tr.T("Списание не проведено", "Эсептен чыгаруу өткөрүлгөн жок", "Write-off not posted", "Düşüm yapılmadı", "Hisobdan chiqarish o'tkazilmadi"), reason,
            NurMarketKassa.AvaloniaHost.Views.Dialogs.PosAlertKind.Warning, null, info: true);
    }

    private static void ApplyCountedStock(string productId, double newQuantity)
    {
        var vm = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
            return;

        StockSyncService.ApplyQuantityToTileOnUi(vm, newQuantity, vm.MustWeigh);
        CatalogCacheService.PersistProductStock(productId, newQuantity, vm.MustWeigh);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Строка журнала списаний (см. WarehouseViewModel.WriteOffHistory).</summary>
public sealed class WriteOffHistoryRowVm
{
    public string ProductName { get; set; } = "";
    public string QuantityText { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CashierName { get; set; } = "";
    public string DateText { get; set; } = "";
    /// <summary>2026-10-27, В-2: время (UTC) и число — для слияния с актами сервера и сортировки; на экран не выводятся.</summary>
    public DateTime SortUtc { get; set; }
    public double Quantity { get; set; }
}
