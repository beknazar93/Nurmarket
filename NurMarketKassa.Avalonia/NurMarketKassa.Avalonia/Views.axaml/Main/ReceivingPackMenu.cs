using Avalonia.Controls;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-07 (владелец: «упаковки — типа поштучной продажи»): выбор способа приёмки строки — общий для «Склад → Приёмка» и «Закупки → Приём товара».
/// Три способа: «в штуках» (как есть), «упаковками → на склад штуки» (ReceivingPacks: упаковки × штук, цена за упаковку ÷ штук) и «упаковка с поштучной продажей»
/// (на склад идут УПАКОВКИ, в карточку НОВОГО товара при создании пишется упаковка с N штук — как «Поштучная продажа из упаковки» в окне товара). Все расчёты — в ReceivingLineVm.</summary>
internal static class ReceivingPackMenu
{
    /// <summary>Открыть меню под подписью-ссылкой строки. <paramref name="changed"/> вызывается после смены способа (перерисовать строку, итоги, поставить курсор).</summary>
    public static void Show(Control anchor, ReceivingLineVm line, Action changed)
    {
        string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
        MenuItem Item(string header, bool current, bool enabled, string? tip, Action apply)
        {
            var item = new MenuItem { Header = (current ? "✓  " : "     ") + header, IsEnabled = enabled };
            if (tip is not null)
            {
                ToolTip.SetTip(item, tip);
                ToolTip.SetShowOnDisabled(item, true);
            }
            item.Click += (_, _) =>
            {
                apply();
                changed();
            };
            return item;
        }

        var canSale = line.CanPackSale;
        var items = new List<Control>
        {
            Item(T("В штуках (как есть)", "Даана менен (болгондой)", "In pieces (as is)", "Adetle (olduğu gibi)", "Donalab (shundayligicha)"),
                !line.PackMode, true,
                T("Количество и цена закупки — за штуку.", "Саны жана сатып алуу баасы — даана үчүн.", "Quantity and purchase price — per piece.", "Miktar ve alış fiyatı — adet başına.", "Miqdor va xarid narxi — dona uchun."),
                () => line.SetPackMode(false)),
            Item(T("Упаковками → на склад штуки", "Таңгак менен → кампага даана", "By packs → pieces to stock", "Kolilerle → depoya adet", "Qadoq bilan → omborga dona"),
                line.PackMode && !line.PackSale, true,
                T("Вводите упаковки, штук в упаковке и цену за упаковку — на склад уйдут штуки и цена за штуку.",
                    "Таңгакты, таңгактагы даананы жана таңгактын баасын киргизиңиз — кампага даана жана даанасынын баасы барат.",
                    "Enter packs, pieces per pack and the price per pack — the warehouse gets pieces and the price per piece.",
                    "Koli sayısını, koli başına adedi ve koli fiyatını girin — depoya adet ve adet fiyatı gider.",
                    "Qadoq sonini, qadoqdagi donani va qadoq narxini kiriting — omborga dona va dona narxi boradi."),
                () => line.SetPackMode(true, ReceivingPackSizes.Suggest(line))),
            Item(T("Упаковка с поштучной продажей" + (canSale ? "" : " — только новый товар"), "Даана менен сатылуучу таңгак" + (canSale ? "" : " — жаңы товар гана"),
                    "Pack with single-piece sale" + (canSale ? "" : " — new product only"), "Adetli satışlı koli" + (canSale ? "" : " — yalnızca yeni ürün"), "Donalab sotiladigan qadoq" + (canSale ? "" : " — faqat yangi mahsulot")),
                line.PackSale, canSale,
                canSale
                    ? T("Новый товар заводится с упаковкой (штук в упаковке) и поштучной продажей, остаток — в УПАКОВКАХ. Цена закупки и цена продажи — за упаковку.",
                        "Жаңы товар таңгак (таңгактагы даана) жана даана менен сатуу менен түзүлөт, калдык — ТАҢГАКТАРДА. Сатып алуу жана сатуу баасы — таңгак үчүн.",
                        "The new product is created with a pack (pieces per pack) and single-piece sale, the stock is in PACKS. Purchase and sale price — per pack.",
                        "Yeni ürün koli (koli başına adet) ve adetli satışla oluşturulur, stok KOLİLERDE. Alış ve satış fiyatı — koli başına.",
                        "Yangi mahsulot qadoq (qadoqdagi dona) va donalab sotish bilan yaratiladi, qoldiq — QADOQLARDA. Xarid va sotuv narxi — qadoq uchun.")
                    : ReceivingLineVm.PackSaleBlockedText,
                () => line.SetPackSale(true, ReceivingPackSizes.Suggest(line))),
        };
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }
}
