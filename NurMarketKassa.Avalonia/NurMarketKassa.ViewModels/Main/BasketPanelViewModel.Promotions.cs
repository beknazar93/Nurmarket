using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-11-27 (конструктор акций Ф1 + pos/quote/): подсказки акций над чеком («Добавьте ещё 1 — будет бесплатно»), понятные предупреждения
/// («Скидка по акции урезана … не ниже закупки») и сверка итога с сервером: после изменения чека (с задержкой 300 мс, прежний запрос отменяется)
/// касса спрашивает pos/quote/; расхождение с локальным итогом пишется в журнал (PROMO_V2) и на чек ложится итог СЕРВЕРА с пояснением.
/// Без признаков promotions_v2 / pos_quote ничего не происходит.</summary>
public sealed partial class BasketPanelViewModel
{
    private CancellationTokenSource? _quoteCts;
    private string _promoNoticeText = "";
    private string? _serverTotalNote;
    private string? _serverTotalNoteFp;

    /// <summary>Только для проверок: задержка перед запросом quote.</summary>
    public static TimeSpan QuoteDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Подсказки и предупреждения акций над чеком (пусто — строка скрыта).</summary>
    public string PromoNoticeText
    {
        get => _promoNoticeText;
        private set
        {
            if (SetProperty(ref _promoNoticeText, value ?? ""))
                OnPropertyChanged(nameof(HasPromoNotice));
        }
    }

    public bool HasPromoNotice => !string.IsNullOrWhiteSpace(PromoNoticeText);

    /// <summary>Подпись под строкой: «Акция «2+1»: −123,00» (id строки → текст).</summary>
    private Dictionary<string, string> PromoLineNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_cart.HasCart || _cart.Root.ValueKind != JsonValueKind.Object)
            return notes;
        foreach (var it in CartDisplayHelper.EnumerateItems(_cart.Root))
            if (CartDisplayHelper.TryItemId(it) is { } id && PromoV2Cart.AppliedLines(it) is { Count: > 0 } lines)
                notes[id] = string.Join("; ", lines);
        return notes;
    }

    /// <summary>Обновить строку подсказок и запланировать сверку с сервером (вызывается после пересчёта итога).</summary>
    private void RefreshPromotionsUi()
    {
        try
        {
            if (!_cart.HasCart || _cart.Root.ValueKind != JsonValueKind.Object)
            {
                PromoNoticeText = "";
                return;
            }
            var root = _cart.Root;
            var parts = new List<string>();
            parts.AddRange(PromoV2Cart.Hints(root));
            parts.AddRange(PromoV2Cart.Warnings(root));
            var fp = root.TryGetProperty(PromoV2Cart.FingerprintField, out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (_serverTotalNote is not null && fp is not null && fp == _serverTotalNoteFp)
                parts.Add(_serverTotalNote);
            else
                _serverTotalNote = null;
            PromoNoticeText = string.Join("\n", parts.Distinct());
            ScheduleQuote();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"PROMO_V2: подсказки акций не обновлены: {ex.Message}", "WARNING");
        }
    }

    private void ScheduleQuote()
    {
        _quoteCts?.Cancel();
        _quoteCts = null;
        if (!PosQuoteService.Enabled || !_cart.HasCart || !(_cart.IsStaging || _cart.IsLocalOffline) || _cart.Items.Count == 0)
            return;
        // Итог сервера уже лежит на этом составе чека — повторно не спрашиваем.
        if (_cart.Root.TryGetProperty(PromoV2Cart.FingerprintField, out _))
            return;
        var cts = new CancellationTokenSource();
        _quoteCts = cts;
        var cartJson = _cart.GetRawText();
        var localTotal = (decimal)CartTotalsCalculator.Calculate(_cart.Root).TotalDue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(QuoteDelay, cts.Token).ConfigureAwait(false);
                var quote = await PosQuoteService.QuoteAsync(cartJson, null, null, cts.Token).ConfigureAwait(false);
                if (quote is null || cts.IsCancellationRequested)
                    return;
                if (Math.Abs(quote.Total - localTotal) <= 0.005m)
                    return;
                PosLogger.Log($"PROMO_V2: итог сервера (quote) {quote.Total:0.00} ≠ итог кассы {localTotal:0.00} (правила {quote.RulesVersion ?? "—"}). Строки сервера: {quote.Lines.GetRawText()}", "WARNING");
                PromoV2Store.RefreshInBackground(force: true);
                _dispatcher.Post(() =>
                {
                    if (cts.IsCancellationRequested || _cart.GetRawText() != cartJson)
                        return; // чек изменился, пока шёл запрос — ответ устарел
                    if (!ReceiptSnapshotCartEditor.ApplyServerPromoLines(_cart, quote.Lines, quote.Hints, quote.Warnings))
                        return;
                    _serverTotalNote = PosQuoteService.ServerTotalNote(quote.Total, localTotal);
                    _serverTotalNoteFp = _cart.Root.TryGetProperty(PromoV2Cart.FingerprintField, out var nf) ? nf.GetString() : null;
                    SyncLinesAndTotals();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Нет связи / сервер не ответил — остаётся локальный расчёт (он тот же по правилам).
                PosLogger.Log($"PROMO_V2: pos/quote/ не ответил ({ex.Message}) — итог по расчёту кассы.", "INFO");
            }
        });
    }

    /// <summary>409 total_mismatch: наложить строки сервера на чек (если число строк совпало) и обновить набор акций. true — чек пересчитан.</summary>
    private bool ApplyTotalMismatch(JsonElement? payload)
    {
        PromoV2Store.RefreshInBackground(force: true);
        if (payload is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("lines", out var lines))
            return false;
        if (!ReceiptSnapshotCartEditor.ApplyServerPromoLines(_cart, lines))
            return false;
        _serverTotalNote = null;
        return true;
    }
}
