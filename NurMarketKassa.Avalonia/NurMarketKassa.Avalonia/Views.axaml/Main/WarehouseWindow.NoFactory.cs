using Avalonia.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-21 (просьба владельца): отметка «Без заводского штрихкода» (хлеб, выпечка…) — товар стоит в начале каталога кассы. На складе нет выбора нескольких строк,
// поэтому действие — пункт меню «⋯» строки: «отметить» / «снять отметку» (по текущему состоянию). Отметка — по id товара в общих настройках магазина (NoFactoryBarcodes), на сервер не уходит.
public partial class WarehouseWindow
{
    private const string IconNoFactory = "M9,16.2 L4.8,12 L3.4,13.4 L9,19 L21,7 L19.6,5.6 Z";

    private MenuItem NoFactoryRowMenuItem(CatalogProductTileVm product)
    {
        var marked = NoFactoryBarcodes.IsMarked(product.Id);
        var text = marked
            ? Tr.T("Снять отметку «Без заводского штрихкода»", "«Заводдук штрих-коду жок» белгисин алуу", "Remove the “No factory barcode” mark", "«Fabrika barkodu yok» işaretini kaldır", "«Zavod shtrixkodi yo'q» belgisini olib tashlash")
            : Tr.T("Без заводского штрихкода (хлеб, выпечка…)", "Заводдук штрих-коду жок (нан, бышырылган азыктар…)", "No factory barcode (bread, baked goods…)", "Fabrika barkodu yok (ekmek, unlu mamuller…)", "Zavod shtrixkodi yo'q (non, pishiriqlar…)");
        return RowMenuItem(text, IconNoFactory, (_, _) =>
        {
            if (!NoFactoryBarcodes.TrySet(new[] { product.Id }, !marked))
                CatalogCacheService.RaiseToast(Tr.T("Отметка не записалась: список слишком большой.", "Белги жазылган жок: тизме өтө чоң.", "The mark was not saved: the list is too large.",
                    "İşaret kaydedilmedi: liste çok büyük.", "Belgi yozilmadi: ro'yxat juda katta."), true);
        }, product);
    }
}
