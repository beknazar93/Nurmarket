using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05 (функция 9 «Циклическая инвентаризация»): «Пересчёт дня» внутри вкладки «Ревизия». Кнопка рядом с фильтрами оставляет в ревизии 20–30 позиций
/// (CycleCountPlanner: отрицательный остаток, частые удаления из чека, давно не считали, класс ABC); при «Завершить ревизию…» — причина каждого расхождения
/// (в примечание акта на сервере), PIN владельца при большой недостаче и запись в историю «когда считали товар» (CycleCountStore). Движок ревизии (RevisionSession)
/// не знает про окна; здесь только сбор данных и диалоги. Не посчитанные позиции в акт не входят — «Пересчёт дня» это частичная ревизия.</summary>
public partial class WarehouseWindow
{
    private HashSet<string>? _cycleIds;
    private Button? _cycleButton;
    private bool _cycleBusy;
    private string _cyclePinSignature = "";

    /// <summary>Кнопка «Пересчёт дня…» в строке фильтров (создаётся при сборке рабочего экрана ревизии).</summary>
    private Button BuildCycleCountButton()
    {
        _cycleButton = BulkButton("", primary: false);
        _cycleButton.MinHeight = 34;
        _cycleButton.Click += async (_, _) => await CycleCountClickAsync().ConfigureAwait(true);
        CycleCountRefreshButton();
        return _cycleButton;
    }

    private void CycleCountRefreshButton()
    {
        RevisionScheduleTotals(); // 2026-11-06 (ЖП Н-3): счётчики фильтров зависят от выбранных позиций
        if (_cycleButton is null)
            return;
        _cycleButton.Content = _cycleIds is null
            ? RevT("Пересчёт дня…", "Күндүн саноосу…", "Daily count…", "Günün sayımı…", "Kunlik sanash…")
            : RevT($"Пересчёт дня: {_cycleIds.Count} — показать все", $"Күндүн саноосу: {_cycleIds.Count} — баарын көрсөтүү", $"Daily count: {_cycleIds.Count} — show all", $"Günün sayımı: {_cycleIds.Count} — tümünü göster", $"Kunlik sanash: {_cycleIds.Count} — hammasini ko'rsatish");
        ToolTip.SetTip(_cycleButton, RevT("Предложить 20–30 позиций для пересчёта сегодня: подозрительные и давно не считанные. Остальные в этой ревизии не меняются.",
            "Бүгүн кайра саноо үчүн 20–30 позицияны сунуштайт: шектүү жана көптөн бери саналбагандар. Калгандары бул ревизияда өзгөрбөйт.",
            "Suggests 20–30 items to count today: suspicious and long-uncounted ones. The others are not changed in this stocktake.",
            "Bugün sayılacak 20–30 kalemi önerir: şüpheli ve uzun süredir sayılmayanlar. Diğerleri bu sayımda değişmez.",
            "Bugun sanash uchun 20–30 pozitsiyani taklif qiladi: shubhali va anchadan beri sanalmaganlar. Qolganlari bu inventarizatsiyada o'zgarmaydi."));
    }

    /// <summary>Сброс при открытии/закрытии рабочего экрана ревизии.</summary>
    private void CycleCountReset()
    {
        _cycleIds = null;
        _cyclePinSignature = "";
        CycleCountRefreshButton();
    }

    private async Task CycleCountClickAsync()
    {
        var s = _rev;
        if (s is null || _cycleBusy)
            return;
        if (_cycleIds is not null)
        {
            // Второй щелчок — вернуть полный список.
            _cycleIds = null;
            CycleCountRefreshButton();
            RevisionRebuildView(true);
            return;
        }
        _cycleBusy = true;
        try
        {
            RevisionCommitPending();
            RevisionSay(RevT("Подбираю позиции…", "Позицияларды тандап жатам…", "Choosing items…", "Kalemler seçiliyor…", "Pozitsiyalar tanlanmoqda…"));
            var candidates = await CycleBuildCandidatesAsync(s).ConfigureAwait(true);
            RevisionSay(null);
            if (_rev != s)
                return;
            var dialog = new CycleCountDialog(candidates, ReceivingMarkupDialog.CanEditSettings());
            await dialog.ShowDialog(this).ConfigureAwait(true);
            if (dialog.Result is not { Count: > 0 } picks || _rev != s)
                return;
            _cycleIds = new HashSet<string>(picks.Select(p => p.Item.Id), StringComparer.OrdinalIgnoreCase);
            CycleCountRefreshButton();
            RevisionEnsureAllFilter();
            RevisionRebuildView(true);
            PosLogger.Log($"Склад: «Пересчёт дня» — выбрано {picks.Count} позиций.", "STOCK");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: «Пересчёт дня» не подобран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            RevisionWarn(RevT("Не удалось подобрать позиции.", "Позицияларды тандоо мүмкүн болгон жок.", "Could not choose items.", "Kalemler seçilemedi.", "Pozitsiyalarni tanlab bo'lmadi."));
        }
        finally
        {
            _cycleBusy = false;
        }
    }

    /// <summary>Список «Пересчёта дня» показываем с фильтром «Все» (посчитанные тоже остаются видны).</summary>
    private void RevisionEnsureAllFilter()
    {
        if (_revFilter == RevisionFilter.All)
            return;
        _revUiSetup = true;
        _revFilter = RevisionFilter.All;
        _revPills[0].IsChecked = true;
        _revUiSetup = false;
    }

    /// <summary>Собрать кандидатов: остатки из ревизии, класс ABC из истории продаж (12 недель), удаления из чека за 30 дней, прошлые пересчёты. Тяжёлое — вне потока окна.</summary>
    private static async Task<List<CycleCandidate>> CycleBuildCandidatesAsync(RevisionSession s)
    {
        var rows = s.Rows.Where(r => !r.IsReadOnly && r.InCatalog && !r.IsCounted).Select(r => (r.ProductId, r.Name, r.Expected)).ToList();
        return await Task.Run(() =>
        {
            var today = DateTime.Today;
            var abc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var lines = SoldLineItemsStore.LoadWithPriceSince(today.AddDays(-83).ToUniversalTime());
                foreach (var it in AbcXyz.Compute(lines.Select(l => (l.ProductId, l.ProductName, l.Quantity, l.Quantity * l.UnitPrice, l.SoldAt.ToLocalTime())), today).Items)
                    abc[it.Key] = it.Abc;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Склад: «Пересчёт дня» — ABC не посчитан ({ex.GetType().Name}); выбор без классов.", "WARNING");
            }
            var deletions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var company = CompanyInfoService.LastCompany?.Id;
                var from = DateTimeOffset.Now.AddDays(-30);
                foreach (var r in CartRemovalLogStore.ReadAll())
                {
                    if (r.At < from || string.IsNullOrEmpty(r.ProductId))
                        continue;
                    if (r.Kind is not (CartRemovalLogStore.KindLine or CartRemovalLogStore.KindOne))
                        continue;
                    if (!string.IsNullOrEmpty(company) && !string.IsNullOrEmpty(r.CompanyId) && !string.Equals(r.CompanyId, company, StringComparison.OrdinalIgnoreCase))
                        continue;
                    deletions[r.ProductId] = deletions.GetValueOrDefault(r.ProductId) + 1;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Склад: «Пересчёт дня» — журнал удалений не прочитан ({ex.GetType().Name}).", "WARNING");
            }
            return rows.Select(r => new CycleCandidate(r.ProductId, r.Name, abc.GetValueOrDefault(r.ProductId) ?? "", r.Expected,
                CycleCountStore.LastCounted(r.ProductId), deletions.GetValueOrDefault(r.ProductId))).ToList();
        }).ConfigureAwait(false);
    }

    /// <summary>Перед окном итога: причины расхождений («Пересчёт дня») → примечание акта; крупная недостача при включённом пороге → PIN владельца. false — завершение отменено.
    /// 2026-11-05 (ревью волн 0–3, п. 1): окно причин — только для «Пересчёта дня»; PIN — по порогу (по умолчанию выключен), старшему (владелец, администратор,
    /// право «Настройки») не нужен; если PIN проверить нельзя (нет связи, нет прав сессии) — ревизия НЕ блокируется, а кассир видит честное предупреждение.</summary>
    private async Task<bool> CycleCountBeforeFinishAsync(RevisionSession s)
    {
        try
        {
            var diffRows = s.Rows.Where(r => r.IsCounted && r.HasDifference && !r.IsReadOnly)
                .OrderBy(r => r.Difference * (r.PurchasePrice > 0 ? r.PurchasePrice : r.SalePrice)) // сначала самые дорогие недостачи
                .ToList();
            if (diffRows.Count == 0)
                return true;

            if (_cycleIds is not null)
            {
                var reasonsDialog = new CycleCountReasonsDialog(diffRows, s.DifferenceReasons);
                await reasonsDialog.ShowDialog(this).ConfigureAwait(true);
                if (reasonsDialog.Result is not { } chosen)
                    return false;
                s.DifferenceReasons.Clear();
                foreach (var (id, code) in chosen)
                    s.DifferenceReasons[id] = code;
            }

            var threshold = CycleCountStore.Current.PinThreshold;
            var money = diffRows.Select(r => (r.Difference, r.PurchasePrice > 0 ? r.PurchasePrice : r.SalePrice)).ToList();
            if (!CycleCountRules.MustAskPin(money, threshold, ActionGuard.IsSenior()))
                return true;
            // PIN спрашиваем один раз на один и тот же набор расхождений (вернулись «Назад» и снова «Завершить» — не мучаем).
            var signature = string.Join("|", diffRows.Select(r => r.ProductId + ":" + r.Difference.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            if (signature == _cyclePinSignature)
                return true;
            var info = RevT($"Недостача больше {threshold:0.##} сом — нужен PIN владельца. Введите его.", $"Жетишпестик {threshold:0.##} сомдон ашты — ээсинин PIN коду керек. Аны киргизиңиз.",
                $"The shortage is over {threshold:0.##} som — the owner PIN is needed. Enter it.", $"Eksik {threshold:0.##} som'u aştı — sahip PIN'i gerekli. Girin.",
                $"Kamomad {threshold:0.##} so'mdan oshdi — egasining PIN kodi kerak. Uni kiriting.");
            var status = await CycleAskOwnerPinAsync(info).ConfigureAwait(true);
            PosLogger.Log($"Склад: ревизия — расхождение выше порога {threshold:0.##}, PIN владельца: {status}.", status == LatePinStatus.Ok ? "STOCK" : "WARNING");
            if (!CycleCountRules.PinOutcomeAllowsFinish(status))
            {
                RevisionWarn(RevT("PIN владельца не принят — ревизия не завершена.", "Ээсинин PIN коду кабыл алынган жок — ревизия аякталган жок.", "The owner PIN was not accepted — the stocktake was not finished.",
                    "Sahip PIN'i kabul edilmedi — sayım bitirilmedi.", "Egasining PIN kodi qabul qilinmadi — inventarizatsiya yakunlanmadi."));
                return false;
            }
            if (status == LatePinStatus.Unavailable)
            {
                // 2026-11-06 (В12, Л5-06 хвост): PIN проверить нельзя из-за отсутствия связи. Раньше ревизия молча проходила; теперь кассир выбирает: провести (событие
                // антифрода + предупреждение владельцу) или оставить ревизию открытой («ждёт PIN») и провести, когда связь появится — черновик ревизии при этом не теряется.
                var shortage = diffRows.Where(r => r.Difference < 0).Sum(r => -r.Difference * (r.PurchasePrice > 0 ? r.PurchasePrice : r.SalePrice));
                var goOn = await PosDialogs.ConfirmYesNoModalAsync(this,
                    RevT($"Нет связи с сервером, PIN владельца проверить нельзя (недостача {shortage:0.##} сом). Провести ревизию сейчас? Запись о проведении без PIN попадёт в «Подозрительные операции» и владельцу в Telegram. Либо оставьте ревизию открытой и проведите, когда появится связь.",
                        $"Сервер менен байланыш жок, ээсинин PIN кодун текшерүүгө болбойт (жетишпестик {shortage:0.##} сом). Ревизияны азыр өткөрөсүзбү? PIN сыз өткөргөнү тууралуу жазуу «Шектүү операцияларга» жана ээсине Telegram аркылуу түшөт. Же ревизияны ачык калтырып, байланыш пайда болгондо өткөрүңүз.",
                        $"There is no connection, so the owner PIN cannot be checked (shortage {shortage:0.##} som). Post the stocktake now? A record of posting without the PIN goes to “Suspicious operations” and to the owner in Telegram. Or leave the stocktake open and post it when the connection is back.",
                        $"Bağlantı yok, sahip PIN'i doğrulanamıyor (eksik {shortage:0.##} som). Sayım şimdi işlensin mi? PIN'siz işlendiğine dair kayıt «Şüpheli işlemler»e ve sahibe Telegram ile gider. Ya da sayımı açık bırakıp bağlantı gelince işleyin.",
                        $"Server bilan aloqa yo'q, egasining PIN kodini tekshirib bo'lmaydi (kamomad {shortage:0.##} so'm). Inventarizatsiya hozir o'tkazilsinmi? PIN siz o'tkazilgani haqidagi yozuv «Shubhali amallar»ga va egasiga Telegram orqali tushadi. Yoki inventarizatsiyani ochiq qoldirib, aloqa paydo bo'lganda o'tkazing."),
                    RevT("PIN не проверен", "PIN текшерилген жок", "PIN not checked", "PIN doğrulanmadı", "PIN tekshirilmadi"),
                    confirmText: RevT("Провести без PIN", "PIN сыз өткөрүү", "Post without PIN", "PIN'siz işle", "PIN siz o'tkazish"),
                    cancelText: RevT("Оставить открытой", "Ачык калтыруу", "Leave open", "Açık bırak", "Ochiq qoldirish"), danger: true, enterCancels: true).ConfigureAwait(true);
                if (!goOn)
                {
                    PosLogger.Log("Склад: ревизия — PIN владельца не проверен (нет связи), кассир оставил ревизию открытой («ждёт PIN»).", "SECURITY");
                    RevisionWarn(RevT("Ревизия не проведена: ждёт проверки PIN владельца. Проведите её, когда появится связь.", "Ревизия өткөрүлгөн жок: ээсинин PIN кодун текшерүүнү күтүүдө. Байланыш пайда болгондо өткөрүңүз.",
                        "The stocktake was not posted: it waits for the owner PIN check. Post it when the connection is back.", "Sayım işlenmedi: sahip PIN doğrulamasını bekliyor. Bağlantı gelince işleyin.", "Inventarizatsiya o'tkazilmadi: egasining PIN tekshiruvini kutmoqda. Aloqa paydo bo'lganda o'tkazing."));
                    return false;
                }
                FraudMonitor.OnRevisionPinSkipped(shortage);
                PosLogger.Log("Склад: ревизия — PIN владельца проверить не удалось (нет связи), ревизия продолжена с предупреждением.", "SECURITY");
                RevisionWarn(RevT("PIN владельца проверить не удалось (нет связи). Ревизия продолжается — владельцу стоит проверить акт.",
                    "Ээсинин PIN кодун текшерүү мүмкүн болгон жок (байланыш жок). Ревизия улантылат — ээси актты текшергени оң.",
                    "The owner PIN could not be checked (no connection). The stocktake continues — the owner should review the document.",
                    "Sahip PIN'i doğrulanamadı (bağlantı yok). Sayım devam ediyor — sahibin tutanağı kontrol etmesi iyi olur.",
                    "Egasining PIN kodini tekshirib bo'lmadi (aloqa yo'q). Inventarizatsiya davom etadi — egasi aktni tekshirgani ma'qul."));
            }
            _cyclePinSignature = signature;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Сбой самих диалогов не должен ронять ревизию — но и обходить PIN нельзя: при сбое проверки завершение не продолжаем только если нужен PIN.
            PosLogger.Log($"Склад: ревизия — шаг причин/PIN не выполнен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return !CycleCountRules.MustAskPin(
                s.Rows.Where(r => r.IsCounted && r.HasDifference && !r.IsReadOnly).Select(r => (r.Difference, r.PurchasePrice > 0 ? r.PurchasePrice : r.SalePrice)),
                CycleCountStore.Current.PinThreshold, ActionGuard.IsSenior());
        }
    }

    /// <summary>Ревизия проведена: запомнить, когда считали каждый товар (и причину), для «Пересчёта дня» и карточки.</summary>
    private void CycleCountRecordDone(RevisionSession s)
    {
        try
        {
            var now = DateTime.Now;
            CycleCountStore.Record(s.Rows.Where(r => r.IsCounted).Select(r =>
                (r.ProductId, r.Difference, s.DifferenceReasons.TryGetValue(r.ProductId, out var code) ? code : (string?)null)), now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: история пересчётов не записана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>PIN владельца: та же проверка на сервере, что для «старого чека» и просрочки (SyncService.VerifyOwnerPinForLateAsync); PIN нигде не хранится.
    /// Возвращает итог: Ok — принят; Wrong/Locked — не принят; Unavailable — нет связи (единственный обход); Error — сервер отказал / нет прав (отказ).</summary>
    private static async Task<LatePinStatus> CycleAskOwnerPinAsync(string firstInfo)
    {
        var prompts = App.GetRequiredService<IUserPrompts>();
        var sync = App.GetRequiredService<SyncService>();
        var info = firstInfo;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? entered = null;
            var granted = await prompts.ConfirmWithCodeAsync(
                RevT("PIN владельца", "Ээсинин PIN коду", "Owner PIN", "Sahip PIN'i", "Egasining PIN kodi"), info,
                code => { entered = code?.Trim(); return !string.IsNullOrEmpty(entered); }).ConfigureAwait(true);
            if (!granted || string.IsNullOrEmpty(entered))
                return LatePinStatus.Wrong;
            var verified = await sync.VerifyOwnerPinForLateAsync(entered).ConfigureAwait(true);
            entered = null;
            if (verified.Status is LatePinStatus.Ok or LatePinStatus.Unavailable)
                return verified.Status; // Unavailable (нет связи) — предупреждение покажет вызывающий; Error (отказ сервера) — сообщение ниже, ревизия не завершается (2026-11-06, Л5-06)
            info = verified.Message;
            if (verified.Status != LatePinStatus.Wrong)
            {
                prompts.ShowError(verified.Message);
                return verified.Status;
            }
        }
        return LatePinStatus.Wrong;
    }
}
