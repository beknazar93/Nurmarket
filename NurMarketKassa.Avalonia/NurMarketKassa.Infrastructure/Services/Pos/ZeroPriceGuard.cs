using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (жалоба магазина по видео: «Кирешки Лайт 33г» ложатся в чек с ценой 0,00, «Оплатить 0,00», кассир идёт в номенклатуру ставить цену):
/// товар без цены продажи (сервер не прислал price или прислал 0 — например, приход «оставить без цены») нельзя молча класть в чек.
/// Здесь — единое правило «цены нет» и тексты предупреждений; окно с выбором («Указать цену» / «Отмена») показывает окно кассы.</summary>
public static class ZeroPriceGuard
{
    /// <summary>Цены продажи у товара нет: плитка показывает «—» или 0. Не считается «без цены»: услуга (цену называют при продаже), товар с ценой штуки
    /// (поштучная продажа из упаковки — цена пачки может быть пустой), комплект с составом.</summary>
    public static bool IsPriceMissing(CatalogProductTileVm? product)
    {
        if (product is null || product.IsService)
            return false;
        if (product.PieceOption is { PieceUnitPrice: > 0 })
            return false;
        return LocalCartService.ParsePrice(product.PriceLine) <= 0;
    }

    /// <summary>Текст для кассира без права править карточки: продать по 0,00 нельзя, цену ставит владелец/администратор.</summary>
    public static string CashierText(string title) => Tr.T(
        $"У товара «{title}» не указана цена, продать его по 0,00 нельзя. Попросите владельца или администратора указать цену в карточке товара.",
        $"«{title}» товарынын баасы көрсөтүлгөн эмес, аны 0,00 баада сатууга болбойт. Ээсинен же администратордон товардын карточкасында баа көрсөтүүнү сураңыз.",
        $"The product “{title}” has no price, it cannot be sold at 0.00. Ask the owner or an administrator to set the price in the product card.",
        $"«{title}» ürününün fiyatı belirtilmemiş, 0,00 fiyatla satılamaz. Sahibinden veya yöneticiden ürün kartında fiyat belirlemesini isteyin.",
        $"«{title}» mahsulotining narxi ko'rsatilmagan, uni 0,00 narxda sotib bo'lmaydi. Egasi yoki administratordan mahsulot kartasida narx ko'rsatishni so'rang.");

    /// <summary>Вопрос владельцу/администратору (или сотруднику с правом «Склад: редактирование»): открыть карточку и указать цену.</summary>
    public static string OwnerText(string title) => Tr.T(
        $"У товара «{title}» не указана цена — по 0,00 он в чек не попадёт. Указать цену сейчас?",
        $"«{title}» товарынын баасы көрсөтүлгөн эмес — 0,00 баада чекке түшпөйт. Баасын азыр көрсөтөсүзбү?",
        $"The product “{title}” has no price — it will not go into the receipt at 0.00. Set the price now?",
        $"«{title}» ürününün fiyatı belirtilmemiş — 0,00 ile fişe eklenmez. Fiyat şimdi belirlensin mi?",
        $"«{title}» mahsulotining narxi ko'rsatilmagan — 0,00 narxda chekka tushmaydi. Narxni hozir ko'rsatasizmi?");

    public static string SetPriceButton => Tr.T("Указать цену", "Баа көрсөтүү", "Set price", "Fiyat belirle", "Narx ko'rsatish");

    /// <summary>Карточку закрыли, а цена так и не появилась.</summary>
    public static string StillNoPriceText(string title) => Tr.T(
        $"У товара «{title}» цена по-прежнему не указана — в чек он не добавлен.",
        $"«{title}» товарынын баасы дагы эле көрсөтүлгөн эмес — чекке кошулган жок.",
        $"The product “{title}” still has no price — it was not added to the receipt.",
        $"«{title}» ürününün fiyatı hâlâ belirtilmemiş — fişe eklenmedi.",
        $"«{title}» mahsulotining narxi hali ham ko'rsatilmagan — chekka qo'shilmadi.");
}
