using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, владелец: «товар при сканере находит в кассе, но не находит в складе» (склад не находит товар
/// по скану). Все места склада ищут код тем же поиском, что касса (ProductCodeLookup): «Товары» (скан вне поля и
/// Enter в поиске), ревизия и списание (скан и Enter в их поиске), подсказки приёмки. Не найден ни на этом
/// компьютере, ни на сервере — «Товар со штрихкодом … не найден» с кнопкой «Создать товар».</summary>
public partial class WarehouseWindow
{
    private void HandleSearchEnter(TextBox box, KeyEventArgs e)
    {
        var text = box.Text?.Trim() ?? "";
        if (text.Length == 0)
            return;

        if (ReferenceEquals(box, ProductSearchBox))
        {
            // Название + Enter — как раньше (список уже отфильтрован), только код ищем дальше.
            if (!WarehouseViewModel.LooksLikeCode(text))
                return;
            e.Handled = true;
            _ = FindScannedInProductsAsync(text);
            return;
        }

        var selected = WarehouseTabs.SelectedItem;
        if (ReferenceEquals(selected, RevisionTabItem))
        {
            e.Handled = true;
            _viewModel.SubmitSearch(isRevisionTab: true);
        }
        else if (ReferenceEquals(selected, WriteOffTabItem))
        {
            e.Handled = true;
            _viewModel.SubmitSearch(isRevisionTab: false);
            WriteOffQuantityBox?.Focus();
        }
    }

    /// <summary>Скан на «Товарах»: код — в поиск, список сразу показывает товар (первой строкой). Нет на этом
    /// компьютере — сервер (товар могли добавить на сайте минуту назад), нет и там — предложение создать.</summary>
    private async Task FindScannedInProductsAsync(string raw)
    {
        var code = ProductCodeLookup.Normalize(raw);
        if (code.Length < 4)
            return;
        try
        {
            _viewModel.ProductSearchText = code;
            _viewModel.ReapplyProductFilter();
            if (ProductCodeLookup.FindLocal(code) is { } hit)
            {
                PosLogger.Log($"Склад: скан '{code}' (товары) — товар {hit.Product.Id} ({hit.Kind}).", "STOCK");
                return;
            }

            if (!WarehouseViewModel.LooksLikeCode(code))
                return;

            var server = await ProductCodeLookup.FindOnServerAsync(code).ConfigureAwait(true);
            if (server.Hit is { } serverHit)
            {
                PosLogger.Log($"Склад: скан '{code}' (товары) — товар {serverHit.Product.Id} найден на сервере.", "STOCK");
                _viewModel.ReapplyProductFilter();
                RefreshWarehouseTotals();
                return;
            }

            PosLogger.Log($"Склад: скан '{code}' (товары) — товар не найден{(server.Unavailable ? ", сервер не ответил" : "")}.", "STOCK");
            if (server.Unavailable)
            {
                App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(Tr.T(
                    "Не удалось проверить штрихкод на сервере. Проверьте связь и повторите сканирование; создание нового товара пока недоступно.",
                    "Штрихкодду серверден текшерүү мүмкүн болгон жок. Байланышты текшерип, кайра сканерлеңиз; азырынча жаңы товар түзүү жеткиликсиз.",
                    "The barcode could not be verified with the server. Check the connection and scan again; creating a new product is unavailable for now.",
                    "Barkod sunucuda doğrulanamadı. Bağlantıyı kontrol edip tekrar tarayın; şu anda yeni ürün oluşturma kullanılamıyor.",
                    "Shtrix-kodni serverda tekshirib bo'lmadi. Ulanishni tekshirib qayta skanerlang; hozircha yangi mahsulot yaratib bo'lmaydi."),
                    isWarning: true);
                return;
            }

            if (await OfferCreateProductAsync(code).ConfigureAwait(true) is not null)
            {
                _viewModel.ProductSearchText = code;
                _viewModel.ReapplyProductFilter();
            }
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Склад: скан '{code}' (товары) не обработан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Ревизия/списание: код не найден нигде — предложить создать товар; созданный сразу встаёт в ревизию или в
    /// списание.</summary>
    private async Task OnWarehouseBarcodeNotFoundAsync(string code, bool isRevisionTab)
    {
        if (await OfferCreateProductAsync(code).ConfigureAwait(true) is { } created)
            _viewModel.ApplyScannedProduct(created, isRevisionTab);
    }

    /// <summary>«Товар со штрихкодом … не найден» → «Создать товар»: карточка нового товара с этим штрихкодом. Следующий
    /// скан во время окна закрывает его и обрабатывается как обычно (как окно «не найден» кассы).</summary>
    private async Task<CatalogProductTileVm?> OfferCreateProductAsync(string code)
    {
        var notFound = new PosConfirmDialog(
            Tr.T("Товар не найден", "Товар табылган жок", "Product not found", "Ürün bulunamadı", "Mahsulot topilmadi"),
            Tr.T($"Товар со штрихкодом {code} не найден — ни на этом компьютере, ни на сервере. Создать новый товар с этим штрихкодом?",
                $"{code} штрихкоддуу товар табылган жок — бул компьютерде да, серверде да жок. Ушул штрихкод менен жаңы товар түзөлүбү?",
                $"No product with barcode {code} was found — neither on this computer nor on the server. Create a new product with this barcode?",
                $"{code} barkodlu ürün bulunamadı — ne bu bilgisayarda ne de sunucuda. Bu barkodla yeni ürün oluşturulsun mu?",
                $"{code} shtrix-kodli mahsulot topilmadi — na shu kompyuterda, na serverda. Shu shtrix-kod bilan yangi mahsulot yaratilsinmi?"),
            Tr.T("Создать товар", "Товар түзүү", "Create product", "Ürün oluştur", "Mahsulot yaratish"),
            Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        // Окно открывается посреди сканирования: Enter сканера не жмёт «Создать», а следующий код закрывает окно —
        // сам код склад уже получает своим обработчиком сканера (OnBarcodeScanned).
        notFound.PassScannerThrough(_barcodeInputService, _ => { });
        if (await PosDialogHost.ShowModalAsync(notFound, this).ConfigureAwait(true) != true)
            return null;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast(Tr.T("Добавление товара недоступно в этом режиме.", "Бул режимде товар кошууга болбойт.", "Adding products isn't available in this mode.", "Bu modda ürün eklenemez.", "Bu rejimda mahsulot qo'shib bo'lmaydi."), isWarning: true);
            return null;
        }

        var dialog = new ProductEditDialog(catalogApi, null) { Barcode = code };
        if (!await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
            return null;

        prompts?.ShowToast(Tr.T("Товар сохранён.", "Товар сакталды.", "Product saved.", "Ürün kaydedildi.", "Mahsulot saqlandi."));
        await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
        await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        RefreshWarehouseTotals();
        return ProductCodeLookup.FindLocal(code)?.Product;
    }

    /// <summary>Подсказки по вхождению + точный код первым (без повтора), не больше <paramref name="limit"/>.</summary>
    private static void WithExactCodeFirst(List<CatalogProductTileVm> matches, string query, int limit)
    {
        if (query.Length < 4 || ProductCodeLookup.FindLocal(query)?.Product is not { } exact)
            return;
        var inCatalog = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, exact.Id, System.StringComparison.OrdinalIgnoreCase)) ?? exact;
        matches.RemoveAll(p => string.Equals(p.Id, inCatalog.Id, System.StringComparison.OrdinalIgnoreCase));
        matches.Insert(0, inCatalog);
        if (matches.Count > limit)
            matches.RemoveRange(limit, matches.Count - limit);
    }
}
