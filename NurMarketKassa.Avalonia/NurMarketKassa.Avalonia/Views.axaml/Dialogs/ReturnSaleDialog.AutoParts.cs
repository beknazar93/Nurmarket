using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 1.5): проверка возврата и обмена — срок возврата и «электрика / установленные
/// детали не принимаются». Не запрет без выхода: предупреждение, продолжить можно только с PIN руководителя (владельца или администратора);
/// без связи PIN не проверить — возврат не оформляется (брак и гарантию продавец оформляет с руководителем). Правила — <see cref="AutoPartsRules"/>.</summary>
public partial class ReturnSaleDialog
{
    /// <summary>null — отказались (или PIN не принят); false — продолжать обычным возвратом.</summary>
    private async Task<bool?> CheckAutoPartsReturnAsync(IReadOnlyList<ReturnSaleLineVm> lines, bool exchange)
    {
        var verdict = AutoPartsRules.CheckReturnNow(_currentSaleDate, lines.Select(l => ((string?)l.ProductId, l.Title)));
        if (!verdict.NeedsPin)
            return false;
        var text = AutoPartsRules.ReturnWarningText(verdict);
        var go = await PosMessageBox.ShowModalAsync(this, text,
            Tr.T("Возврат не по правилам", "Эрежеге ылайык эмес кайтаруу", "Return outside the rules", "Kurallara uymayan iade", "Qoidaga mos kelmaydigan qaytarish"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            confirmText: Tr.T("Ввести PIN руководителя", "Жетекчинин PIN кодун киргизүү", "Enter manager PIN", "Yönetici PIN'ini gir", "Rahbar PIN kodini kiritish")).ConfigureAwait(true);
        if (go != MessageBoxResult.Yes)
            return null;
        if (!await ExpiredSaleGuard.AskOwnerPinAsync().ConfigureAwait(true))
            return null;
        PosLogger.Log($"{(exchange ? "Обмен" : "Возврат")} по чеку {_currentSaleId}: автозапчасти, срок/электрика — продолжено по PIN руководителя (просрочка {verdict.Expired}, позиций без возврата {verdict.NotAccepted.Count}).", "SALES");
        return false;
    }
}
