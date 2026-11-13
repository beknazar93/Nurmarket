using Avalonia.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Канцтовары», КТ3): кнопка «Счёт из этого чека» в меню «Ещё» кассы. Строки открытого чека (с ценами после скидок)
/// → новый документ «Счёт на оплату» (<see cref="ClientDocFromReceipt"/>) → редактор документов: там выбирают организацию (ИНН, ОсОО),
/// печатают счёт и накладную. Чек не проводится и не меняется. Оплата организацией — переводом по счёту (продажа в долг со сроком и
/// «Оплата долга → Перечисление» уже есть в кассе). Работает только в сфере «Канцтовары» (<see cref="BasketPanelViewModel.ShowInvoiceFromReceipt"/>).
/// </summary>
public static class InvoiceFromReceipt
{
    /// <summary>Строки чека кассы → входные данные построителя. Цена за единицу — до скидки строки, сумма — после.</summary>
    public static List<ReceiptDocLine> LinesOf(BasketPanelViewModel basket) =>
        basket.Lines.Select(l => new ReceiptDocLine(l.ProductId, l.Title, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal)).ToList();

    /// <summary>Документ-счёт по текущему чеку (без открытия окна).</summary>
    public static ClientDoc Build(BasketPanelViewModel basket)
    {
        var doc = ClientDocsUi.NewDoc(ClientDocKind.Invoice, null);
        ClientDocFromReceipt.Fill(doc, LinesOf(basket), basket.Total);
        doc.BasedOn = Tr.T("текущий чек кассы", "кассанын учурдагы чеги", "current till receipt", "kasanın mevcut fişi", "kassaning joriy cheki");
        return doc;
    }

    public static void Open(Control? anchor, BasketPanelViewModel? basket)
    {
        if (anchor is null || basket is null || !basket.ShowInvoiceFromReceipt)
            return;
        try
        {
            basket.IsMoreActionsVisible = false;
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return;
            if (!TariffGate.CanViewClients)
            {
                basket.CartMessage = Tr.T("Документы клиентов недоступны на этом тарифе.", "Кардарлардын документтери бул тарифте жеткиликсиз.", "Client documents are not available on this plan.", "Müşteri belgeleri bu tarifede kullanılamaz.", "Mijoz hujjatlari bu tarifda mavjud emas.");
                return;
            }
            if (basket.Lines.Count == 0)
            {
                basket.CartMessage = Tr.T("Добавьте товары в чек — счёт строится по его строкам.", "Чекке товар кошуңуз — эсеп анын саптары боюнча түзүлөт.", "Add products to the receipt — the invoice is built from its lines.", "Fişe ürün ekleyin — fatura satırlarından oluşturulur.", "Chekka mahsulot qo'shing — hisob uning qatorlaridan tuziladi.");
                return;
            }
            ClientDocsUi.Open(owner, Build(basket), isNew: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Счёт из чека: окно не открылось: {ex}", "WARNING");
        }
    }
}
