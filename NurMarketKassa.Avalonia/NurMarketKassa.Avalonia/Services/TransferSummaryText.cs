using NurMarketKassa.Services;
using System.Globalization;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, макет MStock.html «Перемещение»): строка итога внизу карточки перемещения — «Позиций: 2 · количество: 110 · станет «В пути»».
/// Владелец видит ДО нажатия кнопки, что произойдёт с документом. Чистая логика без Avalonia (проверяется тестами). Статус документа — значение
/// <see cref="StockTransferService"/> («created» / «in_transit» / …); у принятого и отменённого документа «станет» не пишем.</summary>
public static class TransferSummaryText
{
    /// <summary>Текст итога. <paramref name="positions"/> — число строк, <paramref name="quantity"/> — сумма количеств (единицы разные, поэтому без «шт»).</summary>
    public static string Build(int positions, double quantity, string status)
    {
        var q = quantity.ToString("0.###", CultureInfo.CurrentCulture);
        var head = Tr.T($"Позиций: {positions} · количество: {q}",
            $"Позиция: {positions} · саны: {q}",
            $"Items: {positions} · quantity: {q}",
            $"Kalem: {positions} · miktar: {q}",
            $"Pozitsiya: {positions} · miqdor: {q}");
        if (positions <= 0)
            return head;

        if (status == StockTransferService.StatusCreated)
            return head + Tr.T(" · станет «В пути»", " · «Жолдо» болот", " · becomes “In transit”", " · «Yolda» olacak", " · «Yo'lda» bo'ladi");
        if (status == StockTransferService.StatusInTransit)
            return head + Tr.T(" · станет «Доставлено»", " · «Жеткирилди» болот", " · becomes “Delivered”", " · «Teslim edildi» olacak", " · «Yetkazildi» bo'ladi");
        return head;
    }
}
