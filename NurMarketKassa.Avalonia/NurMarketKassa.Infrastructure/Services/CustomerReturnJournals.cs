
namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (аудит К-1, В-1): итог изменения возврата от клиента в журналах кассы. <see cref="InLedger"/> — запись возврата найдена в журнале кассы
/// (<see cref="SaleReturnLedger"/>); иначе возврат сделан не этой кассой, суммы — по документу сервера. Дельты = после − до: отрицательные — деньги нужно забрать у клиента.</summary>
/// <param name="PreviousShift">2026-11-13 (п. 7): возврат был в прошлой смене — сторно записано в текущую с пометкой «отмена возврата прошлой смены».</param>
/// <param name="Deferred">2026-11-13 (п. 3): смены нет (программа владельца, смена закрыта) — журнал кассы и смену не трогали, поправку запишет сверка с сервером при открытой смене.</param>
public sealed record ReturnJournalChange(bool InLedger, string SaleId, decimal OldAmount, decimal NewAmount, decimal DeltaCash, decimal DeltaNonCash, decimal DeltaDebt, bool ShiftStornoWritten,
    bool PreviousShift = false, bool Deferred = false)
{
    public decimal DeltaAmount => NewAmount - OldAmount;
    public bool Voided => NewAmount <= 0.004m;
}

/// <summary>2026-11-12 (аудит К-1): правка и аннулирование возврата от клиента (сервер Умара, 24 ч) доходили только до перечитывания остатков — журнал возвратов кассы
/// (<see cref="SaleReturnLedger"/>) и события смены (<see cref="ShiftEventsStore"/>) не менялись. Дальше от них считается потолок нового возврата, пометка «возвращён», блок «Возвращено
/// всего», печать, «Касса:» и X/Z-отчёты — они продолжали считать деньги возвращёнными. Здесь одно общее место: после УСПЕХА на сервере запись возврата в журнале (по id документа
/// сервера) убирается или пересчитывается по новой сумме, а в смене пишется сторно (разница с той же разбивкой наличные/безнал/долг).</summary>
public static class CustomerReturnJournals
{
    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Возврат аннулирован: запись убрана из журнала, в смене — сторно на всю сумму. <paramref name="docAmount"/> — сумма документа сервера (для записи без id и для текста,
    /// если журнал возврата не знает). <paramref name="shiftId"/> — смена кассы (null у программы владельца — в смену ничего не пишется).</summary>
    public static ReturnJournalChange Void(string? saleId, string docId, decimal docAmount, string? shiftId)
    {
        // 2026-11-13 (проверка денег, п. 3): смены нет — запись возврата не убираем (иначе сторно нигде не запишется, а журнал уже «забыл» возврат): считаем на копии,
        // а журнал и смену поправит сверка с сервером (CustomerReturnReconciler), когда смена откроется.
        var noShift = string.IsNullOrWhiteSpace(shiftId);
        var change = SaleReturnLedger.ChangeRefundByDoc(saleId, docId, docAmount, (_, _) => true, dryRun: noShift);
        if (change is null)
            return new ReturnJournalChange(false, saleId ?? "", Math.Max(0m, docAmount), 0m, 0m, 0m, 0m, false);
        var b = change.Before;
        if (noShift)
            return new ReturnJournalChange(true, change.SaleId, b.Amount, 0m, -b.Cash, -b.NonCash, -b.DebtReduced, false, Deferred: true);
        var prev = IsPreviousShiftReturn(shiftId, b.At);
        var stornoWritten = ShiftEventsStore.RecordReturnDelta(shiftId, change.SaleId, -b.Amount, -b.Cash, -b.NonCash, -b.DebtReduced, prev);
        return new ReturnJournalChange(true, change.SaleId, b.Amount, 0m, -b.Cash, -b.NonCash, -b.DebtReduced, stornoWritten, prev);
    }

    /// <summary>Время открытия смены; в тестах подменяется. По умолчанию — запомненное при открытии смены (ShiftAgeWarning).</summary>
    public static Func<string?, DateTime?> ShiftOpenedAt { get; set; } = id => NurMarketKassa.Core.Contracts.ShiftAgeWarning.Get(id); // 2026-11-13: полное имя (в Infrastructure нет using Core.Contracts) — иначе сборка не шла

    /// <summary>2026-11-13 (проверка денег, п. 7): возврат сделан до открытия текущей смены — деньги по его изменению движутся сегодня, но возврат числится за прошлой сменой.
    /// Время открытия неизвестно (перезапуск до чтения смены) — «прошлая», если возврат не сегодня по календарю.</summary>
    public static bool IsPreviousShiftReturn(string? shiftId, DateTimeOffset refundAt) =>
        ShiftOpenedAt(shiftId) is { } opened ? refundAt.LocalDateTime < opened : refundAt.LocalDateTime.Date < DateTime.Now.Date;

    /// <summary>Возврат изменён (новый состав и сумма из ответа сервера): сумма и разбивка записи журнала пересчитаны пропорционально новой сумме документа (не больше остатка
    /// оплаченного по чеку), строки — по новым количествам; в смене — сторно (или доплата) на разницу. <paramref name="oldDocAmount"/> — сумма документа до правки,
    /// <paramref name="newDocAmount"/> — после.</summary>
    public static ReturnJournalChange Edit(string? saleId, string docId, decimal oldDocAmount, decimal newDocAmount, IReadOnlyList<CustomerReturnEditLine> newLines, string? shiftId)
    {
        var newDoc = Math.Max(0m, newDocAmount);
        var noShift = string.IsNullOrWhiteSpace(shiftId); // 2026-11-13 (п. 3): смены нет — журнал не трогаем (см. Void), считаем на копии
        var change = SaleReturnLedger.ChangeRefundByDoc(saleId, docId, oldDocAmount, (entry, r) =>
        {
            var oldDoc = r.ServerAmount is > 0.004m ? r.ServerAmount.Value : oldDocAmount > 0.004m ? oldDocAmount : r.Amount;
            var ratio = oldDoc > 0.004m ? newDoc / oldDoc : 1m;
            var cap = entry.Total > 0m ? r.Amount + entry.Remaining : decimal.MaxValue; // сумма всех возвратов чека не больше оплаченного
            var amount = Math.Min(Round2(r.Amount * ratio), cap);
            var k = r.Amount > 0.004m ? amount / r.Amount : 1m;
            r.Cash = Round2(r.Cash * k);
            r.NonCash = Round2(r.NonCash * k);
            r.DebtReduced = Round2(r.DebtReduced * k);
            r.Amount = amount;
            r.ServerAmount = newDoc;
            if (ratio < 0.999m)
                r.Whole = false;
            var byId = newLines.Where(l => l.SaleItemId.Length > 0).GroupBy(l => l.SaleItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty), StringComparer.OrdinalIgnoreCase);
            foreach (var line in r.Lines.ToList())
            {
                if (line.LineId is not { Length: > 0 } id || !byId.TryGetValue(id, out var qty))
                    continue;
                if (qty <= 0.0005)
                {
                    r.Lines.Remove(line);
                    continue;
                }
                if (line.Quantity > 0.0005)
                    line.Total = Round2(line.Total * (decimal)(qty / line.Quantity));
                line.Quantity = qty;
            }
            return false;
        }, dryRun: noShift);
        if (change is null)
            return new ReturnJournalChange(false, saleId ?? "", Math.Max(0m, oldDocAmount), newDoc, 0m, 0m, 0m, false);
        var b = change.Before;
        var a = change.After!;
        var dAmount = a.Amount - b.Amount;
        var dCash = a.Cash - b.Cash;
        var dNon = a.NonCash - b.NonCash;
        var dDebt = a.DebtReduced - b.DebtReduced;
        if (noShift)
            return new ReturnJournalChange(true, change.SaleId, b.Amount, a.Amount, dCash, dNon, dDebt, false, Deferred: true);
        var prev = IsPreviousShiftReturn(shiftId, b.At);
        var stornoWritten = ShiftEventsStore.RecordReturnDelta(shiftId, change.SaleId, dAmount, dCash, dNon, dDebt, prev);
        return new ReturnJournalChange(true, change.SaleId, b.Amount, a.Amount, dCash, dNon, dDebt, stornoWritten, prev);
    }

    private static string Som(decimal v) => MoneyText.Som(Math.Abs(v));

    /// <summary>2026-11-12 (аудит В-1): что кассиру делать с деньгами после изменения/аннулирования возврата — «Заберите у клиента N сом» / «Выдайте клиенту ещё N сом» с разбивкой по способам из журнала
    /// кассы. <paramref name="ownerProgram"/> — в программе владельца ящика и смены нет: движение денег не записывается, расчёт с клиентом — на кассе. Пустая строка — деньги не меняются.</summary>
    public static string MoneyNote(ReturnJournalChange c, bool ownerProgram)
    {
        string text;
        var give = c.DeltaAmount > 0.004m; // возврат увеличили — клиенту выдать ещё
        if (Math.Abs(c.DeltaAmount) < 0.005m)
            return Tr.T("Сумма возврата не изменилась — с клиентом ничего пересчитывать не нужно.", "Кайтаруунун суммасы өзгөргөн жок — кардар менен эч нерсе кайра эсептөөнүн кереги жок.",
                "The return amount did not change — nothing to settle with the customer.", "İade tutarı değişmedi — müşteriyle yeniden hesaplaşmaya gerek yok.", "Qaytarish summasi o'zgarmadi — mijoz bilan hech narsani qayta hisoblash shart emas.");
        var total = Som(c.DeltaAmount);
        if (c.InLedger)
        {
            var cash = Math.Abs(c.DeltaCash);
            var non = Math.Abs(c.DeltaNonCash);
            var debt = Math.Abs(c.DeltaDebt);
            var money = cash + non;
            var parts = new List<string>();
            if (cash > 0.004m)
                parts.Add(Tr.T("наличными ", "накталай ", "cash ", "nakit ", "naqd ") + Som(cash));
            if (non > 0.004m)
                parts.Add(Tr.T("на карту/перевод ", "картага/которуу ", "card/transfer ", "kart/havale ", "karta/o'tkazma ") + Som(non));
            var how = parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "";
            var moneyText = money > 0.004m
                ? give
                    ? Tr.T($"Выдайте клиенту ещё {Som(money)}{how}.", $"Кардарга дагы {Som(money)} бериңиз{how}.", $"Pay the customer {Som(money)} more{how}.", $"Müşteriye {Som(money)} daha verin{how}.", $"Mijozga yana {Som(money)} bering{how}.")
                    : Tr.T($"Заберите у клиента {Som(money)}{how}.", $"Кардардан {Som(money)} кайтарып алыңыз{how}.", $"Take {Som(money)} back from the customer{how}.", $"Müşteriden {Som(money)} geri alın{how}.", $"Mijozdan {Som(money)} qaytarib oling{how}.")
                : "";
            var debtText = debt > 0.004m
                ? give
                    ? Tr.T($" Долг клиента по чеку уменьшен ещё на {Som(debt)}.", $" Кардардын чек боюнча карызы дагы {Som(debt)} азайды.", $" The customer's debt on the receipt is reduced by {Som(debt)} more.", $" Müşterinin fişteki borcu {Som(debt)} daha azaldı.", $" Mijozning chek bo'yicha qarzi yana {Som(debt)} ga kamaydi.")
                    : Tr.T($" Долг клиента по чеку снова вырос на {Som(debt)}.", $" Кардардын чек боюнча карызы кайра {Som(debt)} өстү.", $" The customer's debt on the receipt grew back by {Som(debt)}.", $" Müşterinin fişteki borcu yeniden {Som(debt)} arttı.", $" Mijozning chek bo'yicha qarzi yana {Som(debt)} ga oshdi.")
                : "";
            // записи о способе нет (деньги = 0, долг = 0) — хотя бы сумма
            text = (moneyText + debtText).Trim();
            if (text.Length == 0)
                text = give
                    ? Tr.T($"Выдайте клиенту ещё {total}.", $"Кардарга дагы {total} бериңиз.", $"Pay the customer {total} more.", $"Müşteriye {total} daha verin.", $"Mijozga yana {total} bering.")
                    : Tr.T($"Заберите у клиента {total}.", $"Кардардан {total} кайтарып алыңыз.", $"Take {total} back from the customer.", $"Müşteriden {total} geri alın.", $"Mijozdan {total} qaytarib oling.");
        }
        else
        {
            text = give
                ? Tr.T($"Выдайте клиенту ещё {total} — способ выдачи выберите как при возврате: касса не помнит, как выдавали деньги по этому возврату.",
                    $"Кардарга дагы {total} бериңиз — берүү ыкмасын кайтарууда кандай болсо ошондой тандаңыз: касса бул кайтаруу боюнча акча кантип берилгенин эстебейт.",
                    $"Pay the customer {total} more — choose the method as for the return: the till does not remember how the money for this return was paid out.",
                    $"Müşteriye {total} daha verin — yöntemi iadedeki gibi seçin: kasa bu iade için paranın nasıl verildiğini hatırlamıyor.",
                    $"Mijozga yana {total} bering — usulni qaytarishdagidek tanlang: kassa bu qaytarish bo'yicha pul qanday berilganini eslamaydi.")
                : Tr.T($"Заберите у клиента {total} — способ возврата выберите как при выдаче: касса не помнит, как выдавали деньги по этому возврату. Проверьте «Касса:» и смену.",
                    $"Кардардан {total} кайтарып алыңыз — ыкманы берүүдөгүдөй тандаңыз: касса бул кайтаруу боюнча акча кантип берилгенин эстебейт. «Касса:» жана сменаны текшериңиз.",
                    $"Take {total} back from the customer — choose the method as when it was paid out: the till does not remember how the money for this return was paid out. Check “Till:” and the shift.",
                    $"Müşteriden {total} geri alın — yöntemi verildiği gibi seçin: kasa bu iade için paranın nasıl verildiğini hatırlamıyor. «Kasa:» ve vardiyayı kontrol edin.",
                    $"Mijozdan {total} qaytarib oling — usulni berilgandek tanlang: kassa bu qaytarish bo'yicha pul qanday berilganini eslamaydi. «Kassa:» va smenani tekshiring.");
        }

        if (ownerProgram)
            // 2026-11-13 (п. 3): журнал кассы здесь не меняется — касса сама увидит изменение при сверке возвратов с сервером и запишет поправку в свою смену
            text += (c.InLedger && c.Deferred ? " " + Tr.T("Касса сама заметит изменение при сверке с сервером и запишет поправку в свою смену.", "Касса өзгөрүүнү сервер менен салыштырууда өзү байкап, оңдоону өз сменасына жазат.",
                    "The till will notice the change when it checks with the server and record the correction in its own shift.", "Kasa değişikliği sunucuyla karşılaştırırken fark edip düzeltmeyi kendi vardiyasına yazar.",
                    "Kassa o'zgarishni server bilan solishtirishda sezib, tuzatishni o'z smenasiga yozadi.") : "")
              + " " + Tr.T("Это программа владельца: ящика и смены здесь нет, движение денег не записывается — расчёт с клиентом проведите на кассе.",
                "Бул ээсинин программасы: бул жерде кутуча жана смена жок, акчанын кыймылы жазылбайт — кардар менен эсептешүүнү кассада жүргүзүңүз.",
                "This is the owner's program: there is no drawer or shift here and no money movement is recorded — settle with the customer at the till.",
                "Bu sahibin programı: burada çekmece ve vardiya yok, para hareketi kaydedilmez — müşteriyle hesaplaşmayı kasada yapın.",
                "Bu egasining dasturi: bu yerda tortma va smena yo'q, pul harakati yozilmaydi — mijoz bilan hisob-kitobni kassada bajaring.");
        else if (c.InLedger && c.ShiftStornoWritten)
        {
            text += " " + Tr.T("В смене записано сторно — «Касса:» и отчёт смены пересчитаются.", "Сменада сторно жазылды — «Касса:» жана смена отчету кайра эсептелет.",
                "A reversal is recorded in the shift — “Till:” and the shift report will recalculate.", "Vardiyaya düzeltme kaydı yazıldı — «Kasa:» ve vardiya raporu yeniden hesaplanır.",
                "Smenada storno yozildi — «Kassa:» va smena hisoboti qayta hisoblanadi.");
            // 2026-11-13 (проверка денег, п. 7): возврат был в прошлой смене — деньги движутся сегодня, в окне закрытия смены это отдельная строка
            if (c.PreviousShift)
                text += " " + Tr.T("Возврат был в прошлой смене: деньги движутся сегодня, в окне закрытия смены это строка «Отмена возврата прошлой смены».",
                    "Кайтаруу өткөн сменада болгон: акча бүгүн кыймылдайт, смена жабуу терезесинде бул «Өткөн сменанын кайтарууну жокко чыгаруу» сабы.",
                    "The return was in a previous shift: the money moves today, in the close-shift window this is the “Reversal of a previous shift's return” row.",
                    "İade önceki vardiyada yapılmıştı: para bugün hareket ediyor, vardiya kapatma penceresinde bu «Önceki vardiya iadesinin iptali» satırıdır.",
                    "Qaytarish o'tgan smenada bo'lgan: pul bugun harakatlanadi, smenani yopish oynasida bu «O'tgan smena qaytarishini bekor qilish» qatori.");
        }
        else if (c.InLedger && c.Deferred)
            // 2026-11-13 (п. 3): смены нет — журнал кассы и смену не трогали, поправку запишет сверка с сервером при открытой смене
            text += " " + Tr.T("Смены нет — поправку в журнал и смену касса запишет сама при сверке с сервером, когда смена будет открыта.",
                "Смена жок — оңдоону журналга жана сменага касса сервер менен салыштырууда смена ачылганда өзү жазат.",
                "There is no shift — the till will record the correction in the journal and shift itself when it checks with the server once a shift is open.",
                "Vardiya yok — düzeltmeyi kasa, vardiya açılınca sunucuyla karşılaştırırken kendisi kayda geçirir.",
                "Smena yo'q — tuzatishni kassa smena ochilganda server bilan solishtirishda o'zi yozadi.");
        return text;
    }
}
