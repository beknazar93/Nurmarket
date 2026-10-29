using System.Windows.Input;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Координатор главного окна кассы. Делегирует зоны специализированным ViewModel.
/// Не содержит бизнес-логики корзины/каталога — только композиция и UI-состояние оболочки.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private int _disposed;
    private bool _isSideMenuOpen;
    private readonly IUpdateCheckService? _updateCheck;
    private readonly UpdateSettings? _updateSettings;

    public MainWindowViewModel(
        MainToolbarViewModel toolbar,
        CatalogPanelViewModel catalog,
        BasketPanelViewModel basket,
        SideMenuViewModel sideMenu,
        IAppSession session,
        IUpdateCheckService? updateCheck = null,
        AppSettings? appSettings = null)
    {
        Toolbar = toolbar;
        Catalog = catalog;
        Basket = basket;
        SideMenu = sideMenu;
        _session = session;
        _updateCheck = updateCheck;
        _updateSettings = appSettings?.Updates;
        // 2026-10-28: кнопка «Обновить» во всех видах кассы — недоступна, пока каталог и так грузится.
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !Catalog.IsLoading);
        Catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogPanelViewModel.IsLoading))
                (RefreshAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        };
        // 2026-10-06, стресс-тест без интернета: очередь дослана пачкой (значок в шапке — 0), а под чеком оставалось
        // «Чек сохранён, отправится автоматически. В очереди: 3». Очередь опустела — строка говорит, что чеки отправлены.
        Toolbar.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainStatusViewModel.QueuedCount) || Toolbar.Status.QueuedCount != 0)
                return;
            if (QueueMessageWords.Any(w => Basket.CartMessage.Contains(w, StringComparison.Ordinal)))
                Basket.CartMessage = Tr.T("Все чеки из очереди отправлены на сервер.", "Кезектеги бардык чектер серверге жөнөтүлдү.",
                    "All queued receipts have been sent to the server.", "Sıradaki tüm fişler sunucuya gönderildi.", "Navbatdagi barcha cheklar serverga yuborildi.");
        };
    }

    // Слова очереди из сообщений PosCheckoutService об оплате без связи (все 5 языков).
    private static readonly string[] QueueMessageWords = { "В очереди:", "Кезекте:", "Queued:", "Sırada:", "Navbatda:" };

    private readonly IAppSession _session;

    public MainToolbarViewModel Toolbar { get; }
    public CatalogPanelViewModel Catalog { get; }
    public BasketPanelViewModel Basket { get; }
    public SideMenuViewModel SideMenu { get; }

    // ── 2026-10-28: кнопка «Обновить» (⟳) в каждом виде кассы и клавиша F5 — одна общая реализация ──────────────

    /// <summary>Принудительно перечитать с сервера каталог (товары, цены, остатки) и состояние смены («Касса: N»).
    /// Корзина и цены строк открытого чека не трогаются. Не чаще раза в 3 с; без связи — без ошибок, плашка «Нет связи».</summary>
    public ICommand RefreshAllCommand { get; }

    /// <summary>Обновить смену и «Касса: N» с сервера. Задаёт главное окно (там проверка смены); null — только каталог.</summary>
    public Func<Task>? RefreshShiftAsync { get; set; }

    private bool _isRefreshing;
    private long _lastRefreshAllTick;

    /// <summary>Идёт «Обновить»: значок ⟳ крутится.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    private async Task RefreshAllAsync()
    {
        // Антидребезг: не чаще раза в 3 с (повторные нажатия и F5 молча игнорируются).
        var now = Environment.TickCount64;
        if (_lastRefreshAllTick != 0 && now - _lastRefreshAllTick < 3000)
            return;
        _lastRefreshAllTick = now;
        IsRefreshing = true;
        try
        {
            var shift = RefreshShiftAsync;
            var shiftTask = Task.CompletedTask;
            if (shift is not null)
            {
                try { shiftTask = shift(); }
                catch (Exception ex) { PosLogger.Log($"REFRESH shift failed: {ex.Message}", "WARNING"); }
            }

            // 2026-10-29: общая принудительная синхронизация (настройки компании, права, очередь чеков) — SyncCenter; каталог и смену
            // кнопка перечитывает сама (ниже), поэтому includeCatalog/includeShift = false. Не бросает.
            var syncAll = SyncCenter.ForceSyncAllAsync(default, includeCatalog: false, includeShift: false);
            var result = await Catalog.ForceRefreshAsync();
            try { await syncAll; }
            catch (Exception ex) { PosLogger.Log($"REFRESH sync-all failed: {ex.Message}", "WARNING"); }
            try { await shiftTask; }
            catch (Exception ex) { PosLogger.Log($"REFRESH shift failed: {ex.Message}", "WARNING"); }

            var time = DateTime.Now.ToString("HH:mm");
            string text;
            if (result.Online)
            {
                text = Tr.T($"Обновлено: товаров {result.Total}, изменено {result.Changed} · {time}",
                    $"Жаңыртылды: товарлар {result.Total}, өзгөргөнү {result.Changed} · {time}",
                    $"Updated: {result.Total} products, {result.Changed} changed · {time}",
                    $"Güncellendi: {result.Total} ürün, {result.Changed} değişti · {time}",
                    $"Yangilandi: mahsulotlar {result.Total}, o'zgargani {result.Changed} · {time}");
            }
            else
            {
                var last = CatalogCacheService.LastSyncTime?.ToLocalTime().ToString("HH:mm");
                text = last is null
                    ? Tr.T("Нет связи — показаны сохранённые данные",
                        "Байланыш жок — сакталган маалыматтар көрсөтүлдү",
                        "No connection — showing saved data",
                        "Bağlantı yok — kayıtlı veriler gösteriliyor",
                        "Aloqa yo'q — saqlangan ma'lumotlar ko'rsatilmoqda")
                    : Tr.T($"Нет связи — показаны данные от {last}",
                        $"Байланыш жок — {last} учурундагы маалыматтар көрсөтүлдү",
                        $"No connection — showing data from {last}",
                        $"Bağlantı yok — {last} tarihli veriler gösteriliyor",
                        $"Aloqa yo'q — {last} dagi ma'lumotlar ko'rsatilmoqda");
            }

            // Плашка под шапкой (не окно, фокус не берёт — сканер продолжает работать).
            MainStatusViewModel.TryShowInfoToast(text, warning: !result.Online);
        }
        finally
        {
            _lastRefreshAllTick = Environment.TickCount64 == 0 ? 1 : Environment.TickCount64; // 3 с отсчитываются и от конца обновления
            IsRefreshing = false;
        }
    }

    public bool IsSideMenuOpen
    {
        get => _isSideMenuOpen;
        set => SetProperty(ref _isSideMenuOpen, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Toolbar.RefreshUserTitle();
        Toolbar.Status.RefreshFromSession();
        SideMenu.ShiftBalanceText = Toolbar.Status.ShiftBalanceText;

        if (_session.IsShiftOpen)
            Toolbar.NotifyShiftStateChanged();

        if (Catalog.RefreshCatalogCommand.CanExecute(null))
            Catalog.RefreshCatalogCommand.Execute(null);

        _ = CheckForUpdateInBackgroundAsync();
        return Task.CompletedTask;
    }

    public void ToggleSideMenu()
    {
        IsSideMenuOpen = !IsSideMenuOpen;
        if (IsSideMenuOpen)
            SideMenu.RefreshEntitlements();
    }

    public void CloseSideMenu() => IsSideMenuOpen = false;

    /// <summary>
    /// Ненавязчивая проверка версии при входе в кассу. Ничего не блокирует и не
    /// падает наружу: без настроенного ManifestUrl или при сетевой ошибке просто
    /// молча ничего не показывает.
    /// </summary>
    private async Task CheckForUpdateInBackgroundAsync()
    {
        if (_updateCheck is null)
            return;

        var settings = _updateSettings ?? new UpdateSettings();
        if (!settings.CheckOnStartup)
            return;

        var lastCheck = UserPreferences.Instance.LastUpdateCheckUtc;
        if (lastCheck.HasValue &&
            DateTime.UtcNow - lastCheck.Value < TimeSpan.FromHours(Math.Max(0, settings.MinHoursBetweenChecks)))
            return;

        try
        {
            var result = await _updateCheck.CheckAsync().ConfigureAwait(true);

            // Only spend the MinHoursBetweenChecks budget on a check that actually happened —
            // a skipped one (no ManifestUrl configured yet) must not delay the real first check.
            if (result.IsConfigured)
            {
                UserPreferences.Instance.LastUpdateCheckUtc = DateTime.UtcNow;
                UserPreferences.Instance.SaveToDisk();
            }

            if (result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.LatestVersion))
            {
                Toolbar.SetUpdateAvailable(result.LatestVersion!);
                PosLogger.Log(
                    $"Update check: newer version available {result.CurrentVersion} -> {result.LatestVersion}",
                    "UPDATE");
            }
            else if (result.IsConfigured)
            {
                PosLogger.Log(
                    $"Update check: up to date (current={result.CurrentVersion}, latest={result.LatestVersion})",
                    "UPDATE");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Startup update check failed: {ex}", "WARNING");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Toolbar.Status.Dispose();
        // 2026-10-27, аудит С-24: модели каталога и чека подписаны на статические события — снимаем подписки.
        Catalog.Dispose();
        Basket.Dispose();
    }
}
