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
        // 2026-11-10 (владелец: упаковка «как в вебе»): у товара, который склад УЖЕ ведёт упаковками (упаковка в карточке), — «как в карточке» или «Изменить штук в упаковке»
        // (остаток пересчитается через подтверждение «Было → станет»). «В штуках» и «упаковками → штуки» ему нельзя: остаток в упаковках, умножение раздуло бы склад.
        if (line.CardPackSize > 0)
        {
            var size = line.CardPackSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            new ContextMenu
            {
                ItemsSource = new List<Control>
                {
                    Item(T($"Упаковка по {size} шт (как в карточке)", $"{size} даанадан таңгак (картадагыдай)", $"Pack of {size} pcs (as in the card)", $"{size} adetlik koli (karttaki gibi)", $"{size} donalik qadoq (kartadagidek)"),
                        !line.PackSale, true,
                        T("«Принимаем» и закупка — за упаковку, карточка не меняется.", "«Кабыл алабыз» жана баасы — таңгак үчүн, карта өзгөрбөйт.", "“Receiving” and the purchase price are per pack, the card does not change.",
                            "«Alınıyor» ve alış fiyatı koli başına, kart değişmez.", "«Qabul qilinadi» va xarid narxi qadoq uchun, karta o'zgarmaydi."),
                        () => line.SetPackSale(false)),
                    Item(T("Изменить штук в упаковке…", "Таңгактагы даананы өзгөртүү…", "Change pieces per pack…", "Koli başına adedi değiştir…", "Qadoqdagi donani o'zgartirish…"),
                        line.PackSale, canSale,
                        canSale
                            ? T("Новое «шт в упак.» запишется в карточку товара, остаток пересчитается в новые упаковки — перед проведением покажем «Было → станет».",
                                "Жаңы «даана/таңгак» товардын картасына жазылат, калдык жаңы таңгактарга кайра эсептелет — өткөрүүдөн мурун «Болгон → болот» көрсөтөбүз.",
                                "The new “pcs/pack” is written to the product card, the stock is recalculated into the new packs — before posting we show “Was → will be”.",
                                "Yeni «adet/koli» ürün kartına yazılır, stok yeni kolilere yeniden hesaplanır — işlemeden önce «Önce → sonra» gösterilir.",
                                "Yangi «dona/qadoq» mahsulot kartasiga yoziladi, qoldiq yangi qadoqlarga qayta hisoblanadi — o'tkazishdan oldin «Avval → bo'ladi» ko'rsatiladi.")
                            : ReceivingLineVm.PackSaleBlockedText,
                        () => line.SetPackSale(true, line.CardPackSize)),
                },
            }.Open(anchor);
            return;
        }
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
            // 2026-11-10: и для товара склада (было «только новый товар»): упаковка уходит в его карточку, остаток пересчитывается — через подтверждение «Было → станет».
            Item(T("Упаковка с поштучной продажей" + (canSale ? "" : " — недоступно"), "Даана менен сатылуучу таңгак" + (canSale ? "" : " — жеткиликсиз"),
                    "Pack with single-piece sale" + (canSale ? "" : " — not available"), "Adetli satışlı koli" + (canSale ? "" : " — kullanılamaz"), "Donalab sotiladigan qadoq" + (canSale ? "" : " — mavjud emas")),
                line.PackSale, canSale,
                canSale
                    ? line.IsNew
                        ? T("Новый товар заводится с упаковкой (штук в упаковке) и поштучной продажей, остаток — в УПАКОВКАХ. Цена закупки и цена продажи — за упаковку.",
                            "Жаңы товар таңгак (таңгактагы даана) жана даана менен сатуу менен түзүлөт, калдык — ТАҢГАКТАРДА. Сатып алуу жана сатуу баасы — таңгак үчүн.",
                            "The new product is created with a pack (pieces per pack) and single-piece sale, the stock is in PACKS. Purchase and sale price — per pack.",
                            "Yeni ürün koli (koli başına adet) ve adetli satışla oluşturulur, stok KOLİLERDE. Alış ve satış fiyatı — koli başına.",
                            "Yangi mahsulot qadoq (qadoqdagi dona) va donalab sotish bilan yaratiladi, qoldiq — QADOQLARDA. Xarid va sotuv narxi — qadoq uchun.")
                        : T("Карточка товара получит упаковку и поштучную продажу (как в окне товара), остаток пересчитается из штук в упаковки. Закупка и цена продажи — за упаковку. Перед проведением покажем «Было → станет».",
                            "Товардын картасы таңгак жана даана менен сатууну алат (товар терезесиндегидей), калдык даанадан таңгакка кайра эсептелет. Сатып алуу жана сатуу баасы — таңгак үчүн. Өткөрүүдөн мурун «Болгон → болот» көрсөтөбүз.",
                            "The product card gets a pack and single-piece sale (as in the product window), the stock is recalculated from pieces into packs. Purchase and sale price — per pack. Before posting we show “Was → will be”.",
                            "Ürün kartı koli ve adetli satış alır (ürün penceresindeki gibi), stok adetten koliye yeniden hesaplanır. Alış ve satış fiyatı — koli başına. İşlemeden önce «Önce → sonra» gösterilir.",
                            "Mahsulot kartasi qadoq va donalab sotishni oladi (mahsulot oynasidagidek), qoldiq donadan qadoqqa qayta hisoblanadi. Xarid va sotuv narxi — qadoq uchun. O'tkazishdan oldin «Avval → bo'ladi» ko'rsatiladi.")
                    : ReceivingLineVm.PackSaleBlockedText,
                () => line.SetPackSale(true, ReceivingPackSizes.Suggest(line))),
        };
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }
}
