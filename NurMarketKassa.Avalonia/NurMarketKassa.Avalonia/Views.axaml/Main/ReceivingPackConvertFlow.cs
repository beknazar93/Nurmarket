using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (Умар: атомарный convert-to-packages): общий для «Склад → Приёмка» и «Закупки → Приём товара» шаг перед проведением строк
/// «упаковка с поштучной продажей». Для товаров склада, у которых меняется единица остатка: свежая карточка с сервера (не кэш) → остаток, который покажет
/// подтверждение и уйдёт в expected_quantity; у товара уже есть упаковка — строка пересчитывается в неё (возврат к таблице); остаток не делится —
/// вопрос «Округлить?» (allow_rounding только по явному согласию); затем прежнее подтверждение «было → станет». Новый ключ повтора — на каждое подтверждение.
/// Сам перевод и приход — в фоне (PurchaseReceivingService.ConvertToPacksAsync).</summary>
internal static class ReceivingPackConvertFlow
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>true — кассир подтвердил, строки готовы к очереди приходов; false — вернуться к таблице (причина уже показана).</summary>
    public static async Task<bool> ConfirmAsync(Window owner, IReadOnlyList<ReceivingLineVm> packSaleLines)
    {
        var title = T("Поштучная продажа из упаковки", "Таңгактан даана менен сатуу", "Single pieces from a pack", "Koliden tek tek satış", "Qadoqdan donalab sotish");
        var converting = packSaleLines.Where(l => l.PackSaleConvertsExisting && !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        if (converting.Count > 0 && !OfflineModeHelper.UseLocalOperations)
        {
            var adopted = new List<string>();
            foreach (var line in converting)
            {
                ReceivingPackConvert.FreshCard? fresh;
                try
                {
                    fresh = await ReceivingPackConvert.ReadFreshAsync(PosApp.CatalogApi, line.ProductId!, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Приёмка: свежая карточка товара {line.ProductId} для перевода в упаковки не получена: {ex.Message}", "WARNING");
                    fresh = null;
                }
                if (fresh is null)
                {
                    await PosAlertDialog.ShowAsync(owner, title, ReceivingPackConvert.NoFreshCardText(line), PosAlertKind.Warning).ConfigureAwait(true);
                    return false;
                }
                if (fresh.PackSize > 0)
                {
                    // already_has_packages: у товара уже есть упаковка — та же (прошлая попытка дошла) — переводить нечего; другая — строка в её упаковки.
                    if (Math.Abs(fresh.PackSize - line.PiecesPerPack) < 1e-9)
                    {
                        line.SetCardPackSizeFromServer(fresh.PackSize);
                        continue;
                    }
                    adopted.Add(line.AdoptCardPack(fresh.PackSize)
                        ? ReceivingPackConvert.AdoptedText(line, fresh.PackSize)
                        : ReceivingPackConvert.CannotAdoptText(line, fresh.PackSize));
                    continue;
                }
                line.SetCardPackSizeFromServer(0);
                line.PackConvertExpected = fresh.Quantity;
                line.PackConvertAllowRounding = false;
                line.PackConvertKey = Guid.NewGuid().ToString(); // новая попытка — новый ключ; повтор очередью после обрыва — этот же
            }
            if (adopted.Count > 0)
            {
                await PosAlertDialog.ShowAsync(owner, title, string.Join("\n\n", adopted), PosAlertKind.Info).ConfigureAwait(true);
                return false;
            }
            foreach (var line in converting.Where(l => l.PackSaleConvertsExisting && l.PackConvertExpected is not null && l.ConvertedStock is null))
            {
                var round = await PosConfirmDialog.ShowModalAsync(owner,
                    T("Остаток не делится на упаковки", "Калдык таңгакка бөлүнбөйт", "The stock does not divide into packs", "Stok kolilere bölünmüyor", "Qoldiq qadoqlarga bo'linmaydi"),
                    ReceivingPackConvert.RoundingQuestion(line),
                    T("Округлить", "Тегеректөө", "Round", "Yuvarla", "Yaxlitlash"),
                    T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary, enterCancels: true).ConfigureAwait(true);
                if (!round)
                {
                    line.PackConvertExpected = null;
                    line.PackConvertKey = null;
                    return false;
                }
                line.PackConvertAllowRounding = true;
            }
        }
        // 2026-11-10: «Было → станет» — «Применить» / «Отмена»; без подтверждения не проводим.
        var ok = await PosConfirmDialog.ShowModalAsync(owner, title,
            ReceivingPacks.PackSaleConfirmMessage(packSaleLines),
            T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary).ConfigureAwait(true);
        if (!ok)
            foreach (var line in converting)
            {
                line.PackConvertExpected = null;
                line.PackConvertAllowRounding = false;
                line.PackConvertKey = null;
            }
        return ok;
    }
}
