using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-13 (сервер apl1311+sn1311, «Электроника»): возврат с серийными номерами. В items[] возврата касса присылает serial_numbers — что именно вернули (после сверки сканом);
// без них сервер при частичном возврате отмечает «первые N по количеству». Причина «Гарантия» — обычный текст в reason; срок гарантии проверяется по warranty_active из поиска serials/.
// Всё включается только при флаге компании electronics_2026_11 (ElectronicsProfile.ServerEnabled); в других сферах и без флага возврат уходит как раньше.
public partial class ReturnSaleDialog
{
    /// <summary>Свежая карточка чека, по которой прошла сверка строк перед отправкой (VerifyLinesBeforeReturnAsync); null — сверка не состоялась.</summary>
    private JsonElement? _verifiedSale;

    /// <summary>Номера, которые вернули, по id строки чека. null — кассир отменил возврат (окно сверки закрыто «Отмена»); пустой словарь — номера не присылаем.
    /// Строка возвращается целиком (количество ≥ числа номеров строки) — номера строки берутся из чека без вопросов; частично — кассир сканирует именно те номера, что принесли,
    /// и они сверяются со списком чека (чужой номер — сообщение и повтор). «Пропустить» — номера не присылаем (сервер отметит первые N).</summary>
    private async Task<Dictionary<string, List<string>>?> CollectReturnSerialsAsync(string saleId, IReadOnlyList<ReturnSaleLineVm> selected, string reason)
    {
        var plan = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_verifiedSale is not { } sale || !ElectronicsProfile.ServerEnabled)
            return plan;

        var freshLines = CartDisplayHelper.EnumerateSaleLineItems(sale).ToList();
        foreach (var line in selected)
        {
            var fresh = freshLines.FirstOrDefault(l => string.Equals(ReturnLineCheck.SaleItemId(l, PreferRowId(saleId)), line.LineId, StringComparison.OrdinalIgnoreCase));
            var soldSerials = fresh.ValueKind == JsonValueKind.Object ? ServerSerials.LineSerials(fresh) : new List<string>();
            if (soldSerials.Count == 0)
                continue;
            var count = (int)((line.Quantity > 0 ? line.Quantity : 1) + 0.5); // число штук возврата (целое)
            if (count >= soldSerials.Count)
            {
                plan[line.LineId] = soldSerials;
                continue;
            }

            // Частичный возврат: какие именно номера принесли.
            while (true)
            {
                var dlg = new SerialNumberDialog(line.Title, count, null, ReturnScanHint(count, soldSerials.Count));
                if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true)
                    return null;
                if (dlg.Skipped || dlg.Serials.Count == 0)
                    break;
                var allowed = soldSerials.Select(ImeiValidator.Normalize).ToHashSet(StringComparer.Ordinal);
                var foreign = dlg.Serials.FirstOrDefault(s => !allowed.Contains(ImeiValidator.Normalize(s)));
                if (foreign is not null)
                {
                    var again = await PosDialogs.ConfirmYesNoModalAsync(this,
                        Tr.T($"Номера {foreign} нет в этом чеке — вернуть можно только проданное по нему. Проверьте номер на устройстве.",
                            $"{foreign} номери бул чекте жок — ал боюнча сатылганды гана кайтарууга болот. Номерди түзмөктөн текшериңиз.",
                            $"Number {foreign} is not in this receipt — only what was sold on it can be returned. Check the number on the device.",
                            $"{foreign} numarası bu fişte yok — yalnızca bu fişte satılan iade edilebilir. Numarayı cihazda kontrol edin.",
                            $"{foreign} raqami bu chekda yo'q — faqat shu chek bo'yicha sotilganini qaytarish mumkin. Raqamni qurilmada tekshiring."),
                        confirmText: Tr.T("Ввести заново", "Кайра киргизүү", "Enter again", "Yeniden gir", "Qayta kiritish"), cancelText: Tr.T("Отмена возврата", "Кайтарууну жокко чыгаруу", "Cancel return", "İadeyi iptal et", "Qaytarishni bekor qilish")).ConfigureAwait(true);
                    if (!again)
                        return null;
                    continue;
                }
                plan[line.LineId] = dlg.Serials.ToList();
                break;
            }
        }

        if (!await ConfirmWarrantyAsync(saleId, plan, reason).ConfigureAwait(true))
            return null;
        return plan;
    }

    private static string ReturnScanHint(int count, int soldCount) =>
        Tr.T($"Вернули {count} из {soldCount} шт. Отсканируйте или впишите номера именно тех устройств, которые принесли — касса сверит их с чеком.",
            $"{soldCount} даанадан {count} кайтарылды. Алып келинген түзмөктөрдүн номерлерин сканерлеңиз же жазыңыз — касса аларды чек менен салыштырат.",
            $"{count} of {soldCount} units are being returned. Scan or type the numbers of the devices that were brought in — the till checks them against the receipt.",
            $"{soldCount} adetten {count} tanesi iade ediliyor. Getirilen cihazların numaralarını okutun veya yazın — kasa bunları fişle karşılaştırır.",
            $"{soldCount} donadan {count} tasi qaytarilmoqda. Olib kelingan qurilmalarning raqamlarini skanerlang yoki yozing — kassa ularni chek bilan solishtiradi.");

    /// <summary>Причина «Гарантия» и номер вне гарантии (warranty_active = false у сервера): спросить, всё ли равно принять. Номера без сведений сервера (не нашёл, нет связи) не мешают.
    /// false — кассир отказался.</summary>
    private async Task<bool> ConfirmWarrantyAsync(string saleId, Dictionary<string, List<string>> plan, string reason)
    {
        if (!ElectronicsProfile.IsWarrantyReason(reason) || App.SalesApi is not IPosSerialsApi api)
            return true;
        var expired = new List<string>();
        foreach (var serial in plan.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).Take(10))
        {
            try
            {
                var found = await api.SerialLookupAsync(serial).ConfigureAwait(true);
                var hit = found.Results.FirstOrDefault(h => string.Equals(h.SaleId, saleId, StringComparison.OrdinalIgnoreCase) && !h.Returned) ?? found.Results.FirstOrDefault(h => !h.Returned);
                if (hit is { WarrantyUntil: { } until, WarrantyActive: false })
                    expired.Add($"{serial} ({Tr.T("до", "чейин", "until", "tarihine kadar", "gacha")} {SerialJournal.Day(until)})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Возврат по гарантии: срок номера {serial} не проверен ({ex.GetType().Name}) — возврат не задерживаем.", "WARNING");
            }
        }
        if (expired.Count == 0)
            return true;
        var list = string.Join(", ", expired);
        return await PosDialogs.ConfirmYesNoModalAsync(this,
            Tr.T($"Гарантия закончилась: {list}. Принять возврат по гарантии всё равно?",
                $"Кепилдик бүттү: {list}. Кепилдик боюнча кайтарууну баары бир кабыл аласызбы?",
                $"The warranty has ended: {list}. Accept the warranty return anyway?",
                $"Garanti sona erdi: {list}. Garanti iadesi yine de kabul edilsin mi?",
                $"Kafolat tugagan: {list}. Kafolat bo'yicha qaytarish baribir qabul qilinsinmi?"),
            confirmText: Tr.T("Принять", "Кабыл алуу", "Accept", "Kabul et", "Qabul qilish"), cancelText: Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga")).ConfigureAwait(true);
    }
}
