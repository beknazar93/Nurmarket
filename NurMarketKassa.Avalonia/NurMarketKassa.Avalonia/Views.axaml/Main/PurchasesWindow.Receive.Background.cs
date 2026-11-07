using NurMarketKassa.Core.Application;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса PurchasesWindow (Background): перенесено из PurchasesWindow.Receive.cs без изменений логики.
public partial class PurchasesWindow
{
    // ------------------------------------------------------------------ итог фоновой задачи, пока окно открыто

    private readonly HashSet<string> _bgWaiting = new();
    private readonly HashSet<string> _bgShown = new();
    private bool _bgSubscribed;

    /// <summary>Один раз за жизнь окна: слушаем JobFinished (итог задач «Закупок», дошедших после того, как окно перестало ждать), отписка в Closed.</summary>
    private void EnsureBackgroundSubscription()
    {
        if (_bgSubscribed)
            return;
        _bgSubscribed = true;
        void OnFinished(ReceivingJob job)
        {
            if (job.Source == ReceivingJobSource.Purchases)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ReceiveOnBackgroundFinished(job));
        }
        BackgroundReceivingQueue.Instance.JobFinished += OnFinished;
        Closed += (_, _) => BackgroundReceivingQueue.Instance.JobFinished -= OnFinished;
    }

    private async void ReceiveOnBackgroundFinished(ReceivingJob job)
    {
        // Окно ещё ждёт эту задачу или уже показало её итог (диалог) — не дублируем.
        if (_bgWaiting.Contains(job.Id) || !_bgShown.Add(job.Id))
            return;
        try
        {
            var text = BackgroundResultText(job, out var warning);
            if (_receive is not null)
                ReceiveMessage(text, warning);
            else if (warning)
                SetStatus(text, true);   // успех уже показал общий тост «Приходов в фоне» (BackgroundReceiptsHost), здесь — только замечания по деньгам
            // 2026-11-05 (приёмка волн 0–3, В-1): на боевом сервере приход идёт 4–5 с и уходит в фон, диалог «Оприходование завершено» не показывается —
            // «Подсказки цены» строим здесь, по завершении фоновой задачи, и даём кнопку «Подсказки цены (N)» наверху окна.
            if (job.Status == ReceivingJobStatus.Done && job.PostedCount > 0)
                UpdatePendingPriceHints(job);
            if (job.PostedCount > 0)
                await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: итог фонового прихода {job.Id} не показан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-11-05 (В-1): подсказки цены по проведённым строкам фонового прихода → кнопка «Подсказки цены (N)»; нет подсказок — кнопка прячется.</summary>
    private void UpdatePendingPriceHints(ReceivingJob job)
    {
        List<PriceHint> hints;
        try
        {
            hints = PriceHints.ForPosted(job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).Select(l => l.ToVm()).ToList());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: подсказки цены по приходу {job.Id} не построены ({ex.Message}).", "WARNING");
            return;
        }
        _pendingHints = hints;
        _hintsButton.IsVisible = hints.Count > 0;
        if (hints.Count == 0)
            return;
        var label = T($"Подсказки цены ({hints.Count})", $"Баа кеңештери ({hints.Count})", $"Price hints ({hints.Count})", $"Fiyat ipuçları ({hints.Count})", $"Narx maslahatlari ({hints.Count})");
        _hintsButton.Content = label;
        Avalonia.Automation.AutomationProperties.SetName(_hintsButton, label);
        PosLogger.Log($"Закупки: после фонового прихода {job.Id} есть подсказки цены ({hints.Count}).", "INFO");
    }

    private async Task ShowPendingPriceHintsAsync()
    {
        if (_pendingHints.Count == 0)
            return;
        await new PriceHintsDialog(_pendingHints).ShowDialog(this).ConfigureAwait(true);
        // Закрыли окно подсказок: кнопка остаётся, пока приход не сменится новым (подсказки можно открыть ещё раз; «Поднять» повторно не навредит — цена уже другая).
    }

    /// <summary>Итог фоновой задачи словами кассира (раздел 3 п. 5 ТЗ): принято — на какую сумму и от кого; нет — куда смотреть.</summary>
    private static string BackgroundResultText(ReceivingJob job, out bool warning)
    {
        var name = job.Supplier?.Name ?? "—";
        var posted = job.PostedCount;
        var noSupplier = job.Supplier is null;
        var sum = MoneyText.Som(job.ReceiptTotal is > 0 and var t ? t : job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).Sum(l => l.LineTotal));
        var extra = job.Summary is { Length: > 0 } sm ? string.Join("\n", sm.Split('\n').Skip(1)) : "";
        warning = job.Status != ReceivingJobStatus.Done || job.Errors.Count > 0;
        if (posted > 0)
            return (job.Status == ReceivingJobStatus.Done ? "✓ " : "⚠ ")
                   + (noSupplier
                       ? T($"Приход оприходован без поставщика: {posted} поз. на {sum} (прямо на склад, без долга)", $"Кирүү жеткирүүчүсүз кабыл алынды: {posted} поз., {sum} (түздөн-түз кампага, карызсыз)",
                           $"Delivery posted without a supplier: {posted} items, {sum} (straight to the warehouse, no debt)", $"Mal tedarikçisiz kaydedildi: {posted} kalem, {sum} (doğrudan depoya, borçsuz)",
                           $"Kirim yetkazib beruvchisiz qabul qilindi: {posted} poz., {sum} (to'g'ridan-to'g'ri omborga, qarzsiz)")
                       : T($"Приход оприходован: {posted} поз. на {sum} (поставщик «{name}»)", $"Кирүү кабыл алынды: {posted} поз., {sum} (жеткирүүчү «{name}»)",
                           $"Delivery posted: {posted} items, {sum} (supplier “{name}”)", $"Mal kabul edildi: {posted} kalem, {sum} (tedarikçi «{name}»)",
                           $"Kirim qabul qilindi: {posted} poz., {sum} (yetkazib beruvchi «{name}»)"))
                   + (job.Status == ReceivingJobStatus.Done ? "" : " — " + job.StatusText)
                   + (extra.Length > 0 ? "\n" + extra : "")
                   + (job.Errors.Count > 0 ? "\n" + string.Join("\n", job.Errors.Take(4)) : "");
        return "⚠ " + T("Приход не оприходован — откройте «Приходы в фоне»", "Кирүү кабыл алынган жок — «Фондогу кирүүлөрдү» ачыңыз", "The delivery wasn't posted — open “Background receipts”",
                   "Mal kabul işlenmedi — «Arka plandaki girişler»i açın", "Kirim o'tkazilmadi — «Fondagi kirimlar»ni oching")
               + (job.LastError is { Length: > 0 } err ? ": " + err : "");
    }

    // 2026-10-25 (Q4): финишер «purchases» очереди фоновых приходов — расход/изъятие/долг и запись в «Закупки» после прихода
    // (PurchasePaymentService.BackgroundFinisherAsync). Регистрируется при загрузке сборки (PurchaseFinisherSetup), до любого BackgroundReceivingQueue.Resume:
    // задача, оставшаяся от прошлого запуска, не потеряет оплату. Метод можно вызвать и вручную — повторная регистрация безвредна.
    public static void RegisterBackgroundFinisher() => PurchaseFinisherSetup.Register();

    private async Task<bool> ConfirmAsync(string text, string yes)
    {
        var w = new Window { Title = TitleText };
        SectionCards.AsModal(w, 480);
        var ok = false;
        var no = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        no.Click += (_, _) => w.Close();
        var go = SectionCards.Primary(yes);
        go.Click += (_, _) =>
        {
            ok = true;
            w.Close();
        };
        var body = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        w.Content = SectionCards.Shell(TitleText, null, body, SectionCards.Buttons(no, go));
        await w.ShowDialog(this).ConfigureAwait(true);
        return ok;
    }
}
