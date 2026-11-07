using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-07 (живая проверка 1.17.81, Б-A1): настоящая панель каталога кассы (CatalogPanelView + CatalogPanelViewModel) в окне заданной ширины,
/// с товарами «самого длинного вида» (цена 12 500,00 сом, остаток 99 999,999 кг, длинное название). Окно уже 1280 — панель сама переходит на автосписок.
/// Без сети и базы: каталог — заглушка, диспетчер — «сразу».</summary>
public static class CatalogListFixture
{
    private sealed class FakeCache : ICatalogCacheService
    {
        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CatalogSyncResult { Success = true });

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => [];
    }

    private sealed class Online : IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Func<Task> action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>Окно с панелью; <see cref="Dispose"/> закрывает окно и отписывает модель от статических событий каталога (иначе загрузка каталога в другом тесте дёргает закрытую панель).</summary>
    public sealed record Built(Window Window, CatalogPanelView View, CatalogPanelViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            View.DataContext = null;
            Vm.Dispose();
            UiShot.Pump();
        }
    }

    private static CatalogProductTileVm Tile(string id, string title, string price, bool weighted, double qty, string barcode)
    {
        var tile = new CatalogProductTileVm(id, title, price + " сом", weighted) { Barcode = barcode, Unit = weighted ? "кг" : "шт", Quantity = qty };
        ProductUnitNormalizer.ApplyToTile(tile);
        return tile;
    }

    /// <summary>Окно шириной <paramref name="width"/> с панелью каталога; вкладка — «Все товары» (0) или «Весовые» (1).</summary>
    public static Built Create(double width, double height = 700, int tab = 0)
    {
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        var items = new List<CatalogProductTileVm>
        {
            Tile("w1", "Абрикос сушёный отборный крупный", "12500.00", true, 99999.999, "4600000000011"),
            Tile("p1", "Макароны «Макфа» спагетти 450 г", "85.00", false, 120, "4600000000028"),
            Tile("w2", "Алма", "208.00", true, 8.03, "0376"),
            Tile("p2", "Айфон 15 Pro Max 256 ГБ титановый", "99999.99", false, 3, "9173310000012"),
        };
        // Как в CatalogPanelTests: каталог попадает в модель тем же закрытым методом, что и после загрузки из базы.
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, [items, false]);
        vm.SelectedTabIndex = tab;
        var view = new CatalogPanelView { DataContext = vm };
        var window = new Window { Width = width, Height = height, SizeToContent = SizeToContent.Manual, Content = view };
        return new Built(window, view, vm);
    }
}
