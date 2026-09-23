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
        Tr.T("Брак", "Брак", "Defect", "Kusurlu", "Nuqsonli"),
        Tr.T("Просрочка", "Мөөнөтү өткөн", "Expired", "Son kullanma tarihi geçmiş", "Muddati o'tgan"),
        Tr.T("Порча упаковки", "Кабы бузулган", "Damaged packaging", "Ambalaj hasarlı", "Qadoq shikastlangan"),
        Tr.T("Списание для себя", "Өзүм үчүн алып коюу", "Written off for personal use", "Kendim için düşüldü", "O'zim uchun hisobdan chiqarish"),
        Tr.T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

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
        WriteOffCommand = new AsyncRelayCommand(
            WriteOffAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(_writeOffProductId) && WriteOffQuantity > 0);
        WriteOffReasonOptions = new ObservableCollection<string>(WriteOffReasons);
        RevisionLines.CollectionChanged += (_, _) => (CommitRevisionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
        $"{_currentPage}/{TotalProductPages}-sahifa");

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
            });
        }
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

        foreach (var product in CatalogCacheService.Products
                     .Where(p => p.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                                 || (p.Article?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false))
                     .Take(8))
            results.Add(product);
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
        $"Баары: {CatalogCacheService.Products.Count} · Табылды: {FilteredProducts.Count}",
        $"Total: {CatalogCacheService.Products.Count} · Found: {FilteredProducts.Count}",
        $"Toplam: {CatalogCacheService.Products.Count} · Bulundu: {FilteredProducts.Count}",
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

    private void ApplyProductFilter()
    {
        var query = _productSearchText.Trim();
        IEnumerable<CatalogProductTileVm> source = string.IsNullOrEmpty(query)
            ? CatalogCacheService.Products
            : CatalogCacheService.Products.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (p.Barcode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.Article?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        source = _saleUnitFilter switch
        {
            "weight" => source.Where(p => p.IsWeighted),
            "piece" => source.Where(p => !p.IsWeighted && !p.HasPieceOption),
            "piecepackage" => source.Where(p => !p.IsWeighted && p.HasPieceOption),
            _ => source,
        };

        // Одна замена коллекции вместо Clear()+Add() по одному (2026-09-07, оптимизация для
        // слабых ПК): каждый Add — событие CollectionChanged и перерасчёт DataGrid, на каталоге
        // в несколько тысяч товаров это заметно подвешивало окно на каждый символ поиска.
        FilteredProducts = new ObservableCollection<CatalogProductTileVm>(source);
        OnPropertyChanged(nameof(FilteredProducts));
        OnPropertyChanged(nameof(ProductCountText));

        // Новый поиск/фильтр — новый список результатов, старый номер страницы (например,
        // страница 5) почти наверняка за пределами нового TotalProductPages.
        _currentPage = 1;
        RebuildPagedProducts();
    }

    /// <summary>Поиск применяется через 180 мс после последнего нажатия (debounce), а не на каждый
    /// символ — иначе быстрый набор запроса на большом каталоге фильтрует список 5–10 раз подряд.
    /// Escape/сброс и фильтр «Единица продажи» по-прежнему применяются сразу.</summary>
    private CancellationTokenSource? _searchDebounce;

    private void ScheduleProductFilter()
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        var token = cts.Token;
        _ = Task.Delay(180, token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested)
                    ApplyProductFilter();
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

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

    public void HandleBarcodeScan(string barcode, bool isRevisionTab)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;
        var trimmed = barcode.Trim();
        var product = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Barcode, trimmed, StringComparison.OrdinalIgnoreCase));

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
            _prompts?.ShowWarning("Товар с этим штрих-кодом не найден в каталоге — списание недоступно.");
        }
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

    private async Task CommitRevisionAsync()
    {
        if (_inventoryApi is null)
        {
            _prompts?.ShowError("Сервис ревизии недоступен в этом режиме.");
            return;
        }

        var countable = RevisionLines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        var skipped = RevisionLines.Count - countable.Count;
        if (countable.Count == 0)
        {
            _prompts?.ShowWarning("В акте нет позиций, привязанных к товару из каталога.");
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
                _prompts?.ShowError("Не удалось создать акт ревизии на сервере.");
                return;
            }

            await _inventoryApi.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var line in countable)
                ApplyCountedStock(line.ProductId, line.ActualQty);

            RevisionLines.Clear();
            FocusedRevisionLine = null;
            _prompts?.ShowToast(skipped > 0
                ? $"Ревизия проведена. Пропущено позиций без привязки к товару: {skipped}."
                : "Ревизия проведена.");
        }
        catch (ApiException ex)
        {
            _prompts?.ShowError($"Не удалось провести ревизию: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            _prompts?.ShowError("Не удалось провести ревизию — нет сети.");
        }
        finally
        {
            IsBusy = false;
            (CommitRevisionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task WriteOffAsync()
    {
        if (_inventoryApi is null)
        {
            _prompts?.ShowError("Сервис списания недоступен в этом режиме.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_writeOffProductId))
        {
            _prompts?.ShowWarning("Выберите товар для списания.");
            return;
        }

        if (WriteOffQuantity <= 0)
        {
            _prompts?.ShowWarning("Укажите количество для списания.");
            return;
        }

        var product = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Id, _writeOffProductId, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            _prompts?.ShowError("Товар не найден в каталоге — обновите каталог и попробуйте снова.");
            return;
        }

        // Остаток перечитываем с сервера прямо сейчас, а не берём из локального кэша: кэш
        // обновляется раз в ~2 минуты, а списание отправляется АБСОЛЮТНЫМ остатком
        // (quantity_fact). С устаревшим кэшем это откатывало чужие продажи: касса Б продала 30
        // (на сервере стало 70), касса А списывала 5 испорченных из кэша «100» и отправляла 95 —
        // сервер ВЫСТАВЛЯЛ 95, возвращая 30 уже проданных единиц обратно в остаток.
        double currentQuantity;
        try
        {
            var detail = await PosApp.CatalogApi
                .ProductsDetailAsync(_writeOffProductId, CancellationToken.None)
                .ConfigureAwait(true);
            if (detail is not { } el)
            {
                _prompts?.ShowError("Сервер не вернул остаток товара. Списание отменено.");
                return;
            }

            currentQuantity = StockSyncService.ResolveStockQuantity(el, product.MustWeigh);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Списание: не удалось получить актуальный остаток: {ex}", "WARNING");
            // Списание всё равно требует сервера (создание и проведение акта), поэтому без
            // достоверного остатка безопаснее отменить операцию, чем отправить абсолютное
            // значение, посчитанное из устаревших данных.
            _prompts?.ShowError("Нет связи с сервером — актуальный остаток не получен. Списание отменено.");
            return;
        }

        if (WriteOffQuantity > currentQuantity)
        {
            _prompts?.ShowWarning(
                $"Списываемое количество ({WriteOffQuantity:0.###}) больше остатка ({currentQuantity:0.###}).");
            return;
        }

        var newQuantity = Math.Max(0, currentQuantity - WriteOffQuantity);

        IsBusy = true;
        (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            var items = new[] { new InventorySessionItem(_writeOffProductId, newQuantity) };
            var note = $"Списание: {WriteOffReason} ({WriteOffQuantity:0.###})";

            var sessionId = await _inventoryApi
                .CreateSessionAsync(note, items, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _prompts?.ShowError("Не удалось создать акт списания на сервере.");
                return;
            }

            await _inventoryApi.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None)
                .ConfigureAwait(true);

            ApplyCountedStock(_writeOffProductId, newQuantity);

            try
            {
                WriteOffHistoryStore.Append(_writeOffProductId, WriteOffProductName, WriteOffQuantity, WriteOffReason, PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId);
                LoadWriteOffHistory();

                // 2026-09-23. Строка «Списания» в Z-отчёте, Telegram-сводке и выгрузке всегда
                // показывала ноль: ShiftEventsStore.KindWriteOff читался в четырёх местах и не
                // писался нигде. Журнал списаний хранит только количество, поэтому сумму берём
                // из каталога — по ЗАКУПОЧНОЙ цене: списание это потеря того, что товар стоил
                // магазину, а не недополученная выручка.
                var card = CatalogCacheService.Products
                    .FirstOrDefault(x => string.Equals(x.Id, _writeOffProductId, StringComparison.OrdinalIgnoreCase));
                var unitCost = card is { PurchasePrice: > 0 }
                    ? card.PurchasePrice
                    : LocalCartService.ParsePrice(card?.PriceLine);
                if (unitCost > 0)
                {
                    ShiftEventsStore.Record(
                        ShiftEventsStore.KindWriteOff,
                        PosApp.ActiveShiftId,
                        ShiftEventsStore.OperationKey(_writeOffProductId),
                        WriteOffQuantity * unitCost,
                        WriteOffProductName);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Write-off history write failed: {ex.Message}", "WARNING");
            }

            _writeOffProductId = "";
            WriteOffBarcode = "";
            WriteOffProductName = "";
            WriteOffQuantity = 1;
            _prompts?.ShowToast("Списание проведено.");
        }
        catch (ApiException ex)
        {
            _prompts?.ShowError($"Не удалось провести списание: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            _prompts?.ShowError("Не удалось провести списание — нет сети.");
        }
        finally
        {
            IsBusy = false;
            (WriteOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
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
}
