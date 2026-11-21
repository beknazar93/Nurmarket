using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-20 (С7, «Синхронизация и интеграция»): окно «Данные изменились на другом устройстве: обновить / перезаписать / отмена» при сохранении существующей карточки.
// Сравнивается карточка при открытии окна (_openedCard) со свежей ТОЛЬКО по полям, которые владелец правит сейчас (ProductConflict). Остаток сверяет прежнее окно «Остаток изменился».
public partial class ProductEditDialog
{
    /// <summary>Свежая карточка, прочитанная для проверки конфликта, — её же использует сверка остатка (один запрос вместо двух). null — не читали / не удалось.</summary>
    private JsonElement? _conflictFresh;

    /// <summary>true — сохранять можно; false — владелец отменил или выбрал «Обновить» (окно остаётся открытым, ввод не потерян). Сбой чтения свежей карточки сохранение не блокирует.</summary>
    private async Task<bool> ResolveEditConflictAsync(ProductEditRequest request)
    {
        _conflictFresh = null;
        if (_openedCard is not { } opened || _existing?.Id is not { Length: > 0 } id)
            return true;
        var edited = ProductConflict.EditedKeys(
            name: !string.Equals(ProductConflict.Norm(opened, "name"), (request.Name ?? "").Trim(), StringComparison.Ordinal),
            price: request.SendPrice, purchasePrice: request.SendPurchasePrice, wholesalePrice: request.SendWholesalePrice, description: request.SendDescription,
            location: request.SendStorageLocation, attributes: request.Attributes is not null);
        if (edited.Count == 0)
            return true;
        JsonElement fresh;
        try
        {
            if (await ServerAnswerWait.ForegroundAsync("products/detail", t => PosApp.CatalogApi.ProductsDetailAsync(id, t)).ConfigureAwait(true) is not { ValueKind: JsonValueKind.Object } card)
                return true;
            fresh = card;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"PRODUCT save: свежая карточка для проверки конфликта не прочитана ({ex.Message}) — сохраняем без проверки.", "WAREHOUSE");
            return true;
        }
        _conflictFresh = fresh;
        var diffs = ProductConflict.Changed(opened, fresh, edited);
        if (diffs.Count == 0)
            return true;

        var dialog = new ProductConflictDialog(request.Name ?? _existing.Title ?? "", diffs);
        var choice = await dialog.ShowDialog<ConflictChoice>(this).ConfigureAwait(true);
        PosLogger.Log($"PRODUCT save: карточка изменена на другом устройстве ({string.Join(", ", diffs.Select(d => d.Key))}), выбрано: {choice}.", "WAREHOUSE");
        switch (choice)
        {
            case ConflictChoice.Overwrite:
                _openedCard = fresh.Clone(); // решение принято: повторно не спрашиваем, пока не изменится ещё раз
                return true;
            case ConflictChoice.Refresh:
                ApplyFreshValues(fresh, diffs);
                _openedCard = fresh.Clone();
                return false;
            default:
                return false;
        }
    }

    /// <summary>«Обновить»: подставить в окно свежие значения изменённых полей; остальной ввод и сравнение «менялось ли» (_orig*) согласованы, чтобы не слать лишнего.</summary>
    private void ApplyFreshValues(JsonElement fresh, IReadOnlyList<ProductConflict.Diff> diffs)
    {
        string V(string key) => ProductConflict.Norm(fresh, key);
        foreach (var d in diffs)
        {
            switch (d.Key)
            {
                case "name": ProductName = V("name"); break;
                case "price": Price = V("price"); _origPrice = _price.Trim(); break;
                case "purchase_price": PurchasePrice = V("purchase_price"); _origPurchasePrice = _purchasePrice.Trim(); break;
                case "wholesale_price": WholesalePrice = V("wholesale_price"); _origWholesale = _wholesalePrice.Trim(); break;
                case "description": Description = V("description"); _origDescription = _description.Trim(); break;
            }
        }
        if (AutoPartVisible && diffs.Any(d => d.Key is "attributes" or "storage_location") && _existing is not null)
        {
            PartMetaStore.Remember(_existing.Id, fresh);
            _apBase = AutoPartInfo.Parse(fresh)?.Clone();
            FillAutoPart(_apBase);
            FillAutoPartMeta(AutoPartsProductMeta.Get(_existing.Id));
            RememberAutoPartOriginal();
        }
        StatusMessage = Tr.T("Свежие значения подставлены. Проверьте и сохраните ещё раз.", "Жаңы маанилер коюлду. Текшерип, кайра сактаңыз.",
            "Fresh values applied. Check them and save again.", "Güncel değerler yerleştirildi. Kontrol edip tekrar kaydedin.", "Yangi qiymatlar qo'yildi. Tekshirib, qayta saqlang.");
    }
}
