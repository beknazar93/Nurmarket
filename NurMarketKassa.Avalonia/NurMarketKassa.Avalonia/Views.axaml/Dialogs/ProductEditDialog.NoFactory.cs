using Avalonia.Controls;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-21 (просьба владельца: «хлеб, выпечка — товары без заводского штрихкода стоят в начале каталога»): галочка «Без заводского штрихкода» в карточке товара.
// Отметка — по id товара в общих настройках магазина (NoFactoryBarcodes), на сервер не уходит. Сохраняется вместе с карточкой; у нового товара id известен после создания
// (если товар ушёл в очередь без связи — отметка привязывается к его штрихкоду и ставится, как только товар появится в каталоге).
public partial class ProductEditDialog
{
    private bool _noFactoryInitial;

    private void InitNoFactoryBarcodeCheck(CatalogProductTileVm? existing)
    {
        _noFactoryInitial = existing is not null && NoFactoryBarcodes.IsMarked(existing.Id);
        NoFactoryBarcodeCheck.IsChecked = _noFactoryInitial;
        NoFactoryBarcodeCheck.Content = new TextBlock
        {
            Text = Tr.T("Без заводского штрихкода (хлеб, выпечка…)", "Заводдук штрих-коду жок (нан, бышырылган азыктар…)", "No factory barcode (bread, baked goods…)",
                "Fabrika barkodu yok (ekmek, unlu mamuller…)", "Zavod shtrixkodi yo'q (non, pishiriqlar…)"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        ToolTip.SetTip(NoFactoryBarcodeCheck, Tr.T("Такие товары стоят в начале каталога кассы. Код, созданный кнопкой «Создать», отмечается сам.",
            "Мындай товарлар кассанын каталогунун башында турат. «Түзүү» баскычы менен түзүлгөн код өзү белгиленет.",
            "Such products come first in the register catalog. A code made with “Generate” is marked automatically.",
            "Bu ürünler kasa kataloğunun başında yer alır. «Oluştur» ile üretilen kod kendiliğinden işaretlenir.",
            "Bunday mahsulotlar kassa katalogining boshida turadi. «Yaratish» bilan yaratilgan kod o'zi belgilanadi."));
    }

    /// <summary>Записать отметку по состоянию галочки (после сохранения карточки). <paramref name="productId"/> — id товара; null — id ещё неизвестен (очередь): ставим по штрихкоду.</summary>
    private void ApplyNoFactoryMark(string? productId, string? barcode = null)
    {
        var want = NoFactoryBarcodeCheck.IsChecked == true;
        if (string.IsNullOrEmpty(productId))
        {
            if (want)
                NoFactoryBarcodes.NoteGenerated(barcode);
            return;
        }
        if (want == _noFactoryInitial && want == NoFactoryBarcodes.IsMarked(productId))
            return;
        if (!NoFactoryBarcodes.TrySet(new[] { productId }, want))
            CatalogCacheService.RaiseToast(Tr.T("Отметка «Без заводского штрихкода» не записалась: список слишком большой.", "«Заводдук штрих-коду жок» белгиси жазылган жок: тизме өтө чоң.",
                "The “No factory barcode” mark was not saved: the list is too large.", "«Fabrika barkodu yok» işareti kaydedilmedi: liste çok büyük.", "«Zavod shtrixkodi yo'q» belgisi yozilmadi: ro'yxat juda katta."), true);
    }
}
