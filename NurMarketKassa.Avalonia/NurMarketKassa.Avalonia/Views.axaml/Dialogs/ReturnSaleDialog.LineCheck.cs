using NurMarketKassa.Core.Application;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

// 2026-11-10 (жалоба владельца, фото клиента: возврат долгового чека → красная ошибка сервера «Есть позиции не из этого чека или несуществующие sale_item_id.»):
// перед отправкой возврата касса берёт СВЕЖУЮ карточку чека с сервера и сверяет id строк (ReturnLineCheck). Устаревшие строки — сопоставление по товару
// или «чек изменился, обновлено»; отказ сервера по id строк — понятный текст (что не так и что делать), чек перечитан, текст сервера — в журнале и в окне.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    /// <summary>Чеки, по которым сервер уже отказал «не из этого чека»: id строки для возврата берётся из поля <c>id</c> строки (ReturnLineCheck.SaleItemId).</summary>
    private static readonly HashSet<string> PreferRowIdSales = new(StringComparer.OrdinalIgnoreCase);

    private bool PreferRowId(string? saleId) => !string.IsNullOrEmpty(saleId) && PreferRowIdSales.Contains(saleId);

    private static string TitlesOf(IEnumerable<string> titles) => string.Join("», «", titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(5));

    /// <summary>Сверка отмеченных строк со свежим чеком сервера. true — отправлять (id строк совпали; или чек получить не удалось — тогда строки проверит сам сервер,
    /// как раньше). false — не отправлять: чек на сервере изменился (строк нет / id другие / без id), окно перечитало чек и сказало кассиру, что делать.
    /// Сопоставление по товару (ReturnLineCheck.Remap) только пишется в журнал и показывается кассиру перечитанным чеком — деньги по догадке не отправляем.</summary>
    private async Task<bool> VerifyLinesBeforeReturnAsync(string saleId, IReadOnlyList<ReturnSaleLineVm> selected)
    {
        JsonElement fresh;
        try
        {
            fresh = await ServerAnswerWait.ForegroundAsync("pos/sales/get", t => App.SalesApi.PosSaleGetAsync(saleId, t)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: свежий чек перед отправкой не получен ({ex.GetType().Name}) — строки проверит сервер.", "WARNING");
            return true;
        }
        if (!string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (fresh.ValueKind != JsonValueKind.Object)
            return true;

        var wanted = selected.Select(l => new ReturnLineCheck.Wanted(l.LineId, l.ProductId, l.Title, l.Quantity)).ToList();
        var check = ReturnLineCheck.Verify(fresh, wanted, PreferRowId(saleId));
        if (check.Ok && !check.Changed)
        {
            // Свежий чек — точный снимок «до возврата» (по нему сверяется исход после неясного ответа сервера).
            _saleSnapshot = ReturnSnapshot.From(fresh);
            return true;
        }

        PosLogger.Log($"Возврат по продаже {saleId}: строки окна не совпали со свежим чеком (без id строк: {check.FreshHasNoIds}; по товару: "
                      + string.Join("; ", check.Remap.Select(kv => kv.Key + " → " + kv.Value)) + $"). Отмечено: {ReturnLineCheck.Describe(wanted)}. "
                      + $"В чеке на сервере: {ReturnLineCheck.DescribeSale(fresh)}. Возврат не отправлен, чек перечитан.", "WARNING");
        FillLinesFromSale(fresh);
        UpdateReceiptChrome();
        var titles = TitlesOf((check.Missing.Count > 0 ? check.Missing : wanted.Where(w => check.Remap.ContainsKey(w.LineId))).Select(m => m.Title));
        var text = check.FreshHasNoIds
            ? Tr.T("Сервер прислал чек без номеров строк — вернуть отдельные позиции нельзя. Нажмите «Обновить» и откройте чек заново; если не поможет — сообщите в техподдержку. Возврат не проведён, деньги не выдавайте.",
                "Сервер чекти саптардын номерлерисиз жөнөттү — өзүнчө позицияларды кайтарууга болбойт. «Жаңыртуу» басып, чекти кайра ачыңыз; жардам бербесе — техколдоого кабарлаңыз. Кайтаруу жүргүзүлгөн жок, акча бербеңиз.",
                "The server sent the receipt without line numbers — individual items cannot be returned. Press “Refresh” and open the receipt again; if that doesn't help, contact support. The return was not processed — do not pay out.",
                "Sunucu fişi satır numaraları olmadan gönderdi — tek tek kalemler iade edilemez. «Yenile»ye basıp fişi yeniden açın; olmazsa desteğe bildirin. İade yapılmadı, para vermeyin.",
                "Server chekni qator raqamlarisiz yubordi — alohida pozitsiyalarni qaytarib bo'lmaydi. «Yangilash»ni bosib, chekni qayta oching; yordam bermasa — texnik yordamga xabar bering. Qaytarish o'tkazilmadi, pul bermang.")
            : Tr.T($"Чек изменился на сервере: позиции «{titles}» в нём изменились или их больше нет (чек правили, возвращали или обменивали). Касса обновила чек — отметьте позиции заново и повторите возврат. Возврат не проведён, деньги не выдавайте.",
                $"Чек серверде өзгөрдү: «{titles}» позициялары өзгөргөн же алар жок (чекти оңдошкон, кайтарышкан же алмаштырышкан). Касса чекти жаңыртты — позицияларды кайра белгилеп, кайтарууну кайталаңыз. Кайтаруу жүргүзүлгөн жок, акча бербеңиз.",
                $"The receipt changed on the server: the items “{titles}” changed or are no longer in it (it was edited, returned or exchanged). The till has refreshed the receipt — check the items again and repeat the return. The return was not processed — do not pay out.",
                $"Fiş sunucuda değişti: «{titles}» kalemleri değişti veya artık yok (fiş düzenlendi, iade veya değişim yapıldı). Kasa fişi yeniledi — kalemleri yeniden işaretleyip iadeyi tekrarlayın. İade yapılmadı, para vermeyin.",
                $"Chek serverda o'zgardi: «{titles}» pozitsiyalari o'zgargan yoki endi yo'q (chek tahrirlangan, qaytarilgan yoki almashtirilgan). Kassa chekni yangiladi — pozitsiyalarni qayta belgilab, qaytarishni takrorlang. Qaytarish o'tkazilmadi, pul bermang.");
        PosMessageBox.Show(this, text, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Сервер отказал «Есть позиции не из этого чека или несуществующие sale_item_id»: возврат не проведён (отказ проверки до записи).
    /// В журнал — текст сервера, что ушло (id строк, количество, названия) и какие id у строк чека сейчас; чек перечитывается; если у строк есть другой <c>id</c> —
    /// по этому чеку дальше отправляется он. Кассиру — что случилось, что делать, и исходный текст сервера.</summary>
    private async Task HandleUnknownLineRefusalAsync(string saleId, ApiException ex, IReadOnlyList<PosRefundLineRequest> sent)
    {
        var sentLines = sent.Select(r => new ReturnLineCheck.Wanted(r.LineId, r.ProductId, r.Title, r.Quantity)).ToList();
        PosLogger.Log($"Возврат по продаже {saleId}: сервер отказал ({ex.StatusCode}) «{ex.Message}». Отправлено: POST pos/sales/{saleId}/return/, items: {ReturnLineCheck.Describe(sentLines)}.", "WARNING");
        var switched = false;
        try
        {
            var fresh = await ServerAnswerWait.ForegroundAsync("pos/sales/get", t => App.SalesApi.PosSaleGetAsync(saleId, t)).ConfigureAwait(true);
            PosLogger.Log($"Возврат по продаже {saleId}: строки чека на сервере после отказа: {ReturnLineCheck.DescribeSale(fresh)}.", "WARNING");
            if (!PreferRowId(saleId) && ReturnLineCheck.HasDifferentRowIds(fresh))
            {
                PreferRowIdSales.Add(saleId);
                switched = true;
                PosLogger.Log($"Возврат по продаже {saleId}: дальше id строки берётся из поля id (как в мобилке).", "WARNING");
            }
            if (string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            {
                FillLinesFromSale(fresh);
                UpdateReceiptChrome();
            }
        }
        catch (Exception readEx)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: чек после отказа не перечитан ({readEx.GetType().Name}).", "WARNING");
        }

        var titles = TitlesOf(sent.Select(r => r.Title));
        var text = Tr.T($"Сервер не принял позиции возврата «{titles}»: он не нашёл их в этом чеке. Возврат НЕ проведён — деньги не выдавайте. Касса обновила чек с сервера: отметьте позиции и нажмите «Вернуть» ещё раз. Если ошибка повторится — сообщите в техподдержку (подробности в журнале).",
                           $"Сервер «{titles}» кайтаруу позицияларын кабыл алган жок: аларды бул чектен тапкан жок. Кайтаруу жүргүзүлгөн ЖОК — акча бербеңиз. Касса чекти серверден жаңыртты: позицияларды белгилеп, «Кайтаруу» дагы басыңыз. Ката кайталанса — техколдоого кабарлаңыз (чоо-жайы журналда).",
                           $"The server did not accept the return items “{titles}”: it did not find them in this receipt. The return was NOT processed — do not pay out. The till has refreshed the receipt from the server: check the items and press “Return” again. If the error repeats, contact support (details in the log).",
                           $"Sunucu «{titles}» iade kalemlerini kabul etmedi: bu fişte bulamadı. İade YAPILMADI — para vermeyin. Kasa fişi sunucudan yeniledi: kalemleri işaretleyip «İade»ye tekrar basın. Hata tekrarlanırsa desteğe bildirin (ayrıntılar günlükte).",
                           $"Server «{titles}» qaytarish pozitsiyalarini qabul qilmadi: ularni bu chekda topmadi. Qaytarish O'TKAZILMADI — pul bermang. Kassa chekni serverdan yangiladi: pozitsiyalarni belgilab, «Qaytarish»ni yana bosing. Xato takrorlansa — texnik yordamga xabar bering (tafsilotlar jurnalda).")
                   + "\n" + Tr.T("Ответ сервера: ", "Сервердин жообу: ", "Server response: ", "Sunucu yanıtı: ", "Server javobi: ") + ex.Message;
        if (switched)
            PosLogger.Log($"Возврат по продаже {saleId}: кассиру предложено повторить с id строк из поля id.", "SALES");
        PosMessageBox.Show(this, text, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
