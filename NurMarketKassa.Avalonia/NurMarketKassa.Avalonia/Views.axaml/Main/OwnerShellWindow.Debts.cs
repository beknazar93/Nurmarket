using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «и ещё где в сводке и аналитике долги??». Карточка «Долги клиентов» в «Сводке»: сколько
/// всего должны магазину, сколько должников, сколько из этого старше 30 дней и три крупных должника; нажатие на карточку
/// или «Подробно» открывает раздел «Долги клиентов». Данные — те же, что в разделе (DebtsView.LoadDebtorsAsync, сервер
/// NurCRM); долги от периода «Сводки» не зависят — это то, что должны сейчас. Обновляется не чаще раза в 2 минуты.</summary>
public partial class OwnerShellWindow
{
    private DateTime _debtsCardAt = DateTime.MinValue;
    private bool _debtsCardLoading;

    /// <summary>2026-10-18: оплата долга (здесь, в кассе — событие LAN) — карточка обновляется сразу, а не через 2 минуты.</summary>
    private bool _debtEventsHooked;
    /// <summary>2026-10-18: просили обновить, пока шла загрузка — после неё ещё раз.</summary>
    private bool _debtsCardPending;
    /// <summary>2026-10-18, ревью после живой проверки (п.1+6): последний список карточки (быстрый или точный из кэша раздела) —
    /// по событию долга патчится только изменившийся клиент (один запрос его продаж), а не все.</summary>
    private List<DebtsView.Debtor>? _cardDebtors;
    /// <summary>Список карточки взят из точного кэша раздела «Долги клиентов» (а не из быстрого списка продаж).</summary>
    private bool _cardExact;
    /// <summary>2026-10-18, ревью после живой проверки (п.7): состояние для журнала — пишем только при его смене.</summary>
    private string? _debtsCardLogState;

    private void LogDebtsCardState(string state, string text)
    {
        if (_debtsCardLogState == state)
            return;
        _debtsCardLogState = state;
        PosLogger.Log(text, "INFO");
    }

    private void HookDebtEvents()
    {
        if (_debtEventsHooked)
            return;
        _debtEventsHooked = true;
        // 2026-10-18, восьмой круг (п.9): общая отложенная рассылка (серия оплат — одно обновление карточки через 3 с).
        void OnDebtChanged(DebtChangeBatch batch) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshDebtsCardAsync(force: true, batch.Clients));
        PosDataEvents.DebouncedDebtChanged += OnDebtChanged;
        // 2026-10-19, вторая живая проверка: раздел «Долги клиентов» обновил точный кэш — карточка и «Требует внимания» сразу из него.
        void OnExact() => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyExactDebtsFromCache);
        DebtsView.ExactChanged += OnExact;
        Closed += (_, _) =>
        {
            PosDataEvents.DebouncedDebtChanged -= OnDebtChanged;
            DebtsView.ExactChanged -= OnExact;
        };
    }

    /// <param name="changedClients">2026-10-18, ревью после живой проверки (п.1+6): клиенты события долга — патчится только их
    /// карточка (по одному запросу); null — весь список (один запрос быстрого списка).</param>
    private async Task RefreshDebtsCardAsync(bool force = false, IReadOnlyCollection<string>? changedClients = null)
    {
        HookDebtEvents();
        if (!TariffGate.CanUseDebts || SectionVisibility.IsHidden("debts"))
        {
            DebtsCard.IsVisible = false;
            LogDebtsCardState("hidden", $"Сводка: долги скрыты — тариф с долгами: {TariffGate.CanUseDebts}, раздел скрыт: {SectionVisibility.IsHidden("debts")}.");
            // 2026-10-18, этап О1: и строки в «Требует внимания» нет (источник не нужен — считается загруженным).
            _attnDebts = null;
            _attnDebtsLoaded = true;
            _attnDebtsFailed = false; // ревью после живой проверки (п.4): «не удалось загрузить» к скрытому разделу не относится
            RenderAttention();
            return;
        }
        if (_debtsCardLoading && force)
            _debtsCardPending = true;
        if (_debtsCardLoading || (!force && DateTime.UtcNow - _debtsCardAt < TimeSpan.FromMinutes(2)))
            return;
        _debtsCardLoading = true;
        // 2026-10-20, живой аудит владельца (долги 225 858 / ≈ 245 858 / ≈ 245 857): общий фоновый точный расчёт — тот же, что раздел
        // «Долги клиентов» (DebtsView.RefreshSharedExactAsync: при запуске, после изменения долга, не чаще раза в 5 мин). Готов —
        // ExactChanged → ApplyExactDebtsFromCache; пока считается — быстрый список с «≈» и «уточняю…».
        _ = DebtsView.RefreshSharedExactAsync();
        try
        {
            // 2026-10-18, ревью после живой проверки (п.1+6): «Сводка» — по БЫСТРОМУ списку (один запрос), точные суммы — из
            // точного кэша (2026-10-20: KnownExact — долги не менялись, не старше 10 мин).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (DebtsView.KnownExact() is { } exact)
            {
                _cardDebtors = exact.ToList();
                _cardExact = true;
            }
            else if (changedClients is { Count: > 0 } && _cardDebtors is not null)
            {
                foreach (var id in changedClients)
                {
                    var fresh = await DebtsView.LoadFastDebtorAsync(id, cts.Token).ConfigureAwait(true);
                    _cardDebtors = _cardDebtors.Where(d => !string.Equals(d.ClientId, id, StringComparison.OrdinalIgnoreCase))
                        .Concat(fresh is null ? Array.Empty<DebtsView.Debtor>() : new[] { fresh }).ToList();
                }
                _cardExact = false;
            }
            else
            {
                _cardDebtors = await DebtsView.LoadFastDebtorsAsync(cts.Token).ConfigureAwait(true);
                _cardExact = false;
            }
            // 2026-10-18, девятый круг (п.8): «не чаще раза в 2 мин» — считается от УСПЕШНОЙ загрузки (ошибка не откладывает повтор).
            _debtsCardAt = DateTime.UtcNow;
            _attnDebtsFailed = false;
            RenderDebtsCard(_cardDebtors);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: карточка долгов не обновлена ({ex.Message}).", "WARNING");
            _debtsCardLogState = "failed";
            // В «Требует внимания» — не «Загрузка…», а «Не удалось загрузить долги — Повторить».
            _attnDebtsFailed = true;
            _attnDebtsLoaded = true;
            RenderAttention();
        }
        finally
        {
            _debtsCardLoading = false;
        }
        // 2026-10-20: точный расчёт закончился, пока грузился быстрый список (его ExactChanged пропущен) — берём точный.
        if (!_cardExact && !_debtsCardPending)
            ApplyExactDebtsFromCache();
        if (_debtsCardPending)
        {
            _debtsCardPending = false;
            await RefreshDebtsCardAsync(force: true).ConfigureAwait(true);
        }
    }

    /// <summary>2026-10-19, вторая живая проверка: пересчитать карточку «Долги» и строку «Требует внимания» из свежего точного кэша
    /// раздела — без запросов к серверу (кэша нет или долги скрыты — ничего не делаем).</summary>
    private void ApplyExactDebtsFromCache()
    {
        if (!TariffGate.CanUseDebts || SectionVisibility.IsHidden("debts") || _debtsCardLoading || DebtsView.FreshExact() is not { } exact)
            return;
        _cardDebtors = exact.ToList();
        _cardExact = true;
        _attnDebtsFailed = false;
        _debtsCardAt = DateTime.UtcNow;
        RenderDebtsCard(_cardDebtors);
    }

    private void RenderDebtsCard(List<DebtsView.Debtor> debtors)
    {
        DebtsCard.IsVisible = true;
        DebtsTitle.Text = DebtsView.TitleText;
        DebtsLinkText.Text = T5("Подробно", "Толугу менен", "Details", "Ayrıntılar", "Batafsil");
        DebtsHost.Children.Clear();
        // 2026-10-18, ревью О1: «Всё в порядке» в «Требует внимания» — только после того, как долги действительно получены.
        _attnDebtsLoaded = true;
        if (debtors.Count == 0)
        {
            LogDebtsCardState("empty", "Сводка: долгов нет — строка долгов в «Требует внимания» убрана.");
            _attnDebts = null;
            RenderAttention();
            DebtsHost.Children.Add(Line(T5("Долгов нет — все клиенты рассчитались.", "Карыз жок — бардык кардарлар эсептешти.", "No debts — every customer has paid.",
                "Borç yok — tüm müşteriler ödedi.", "Qarz yo'q — barcha mijozlar to'lagan.")));
            return;
        }
        LogDebtsCardState("shown", "Сводка: долги показаны.");
        // 2026-10-18, ревью после живой проверки (п.1+6): сумма — ВСЕХ чеков (точная, где известна; иначе по продаже), никогда не
        // 0 из-за «уточняется». Не точная — «≈» и пояснение.
        var total = debtors.Sum(d => d.Total);
        var checking = debtors.SelectMany(d => d.Sales).Count(s => s.Checking);
        var approx = checking > 0;
        var note = !approx ? null
            : _cardExact ? NurMarketKassa.AvaloniaHost.Services.DebtPaymentFlow.CheckingSuffix(checking).Trim()
            : DebtsView.ApproxNote(); // 2026-10-20: «уточняю…», пока идёт фоновый точный расчёт
        var old = debtors.SelectMany(d => d.Sales).Where(s => DebtsView.AgeDays(s.At) > 30).Sum(s => s.Left);
        // 2026-10-18, этап О1: те же итоги — строкой «Долги клиентов» в «Требует внимания» (OwnerShellWindow.Attention.cs).
        _attnDebts = new AttentionDebts(total, debtors.Count, old, approx, note);
        RenderAttention();
        // 2026-10-18, этап О1: сумма — крупно цветом текста (красный — только просрочка «больше 30 дней»), итоги — по строкам,
        // без склейки « · » и без линии-разделителя.
        var sum = new TextBlock { Text = (approx ? "≈ " : "") + Money(total), FontSize = 30, FontWeight = FontWeight.SemiBold, Classes = { "num" } };
        sum.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        DebtsHost.Children.Add(sum);
        if (note is not null)
            DebtsHost.Children.Add(Line(note));
        DebtsHost.Children.Add(Line(T5($"Должников: {debtors.Count}", $"Карыздарлар: {debtors.Count}", $"Debtors: {debtors.Count}",
            $"Borçlu: {debtors.Count}", $"Qarzdorlar: {debtors.Count}")));
        DebtsHost.Children.Add(Line(T5($"Чеков в долг: {debtors.Sum(d => d.Sales.Count)}", $"Карыз чектер: {debtors.Sum(d => d.Sales.Count)}",
            $"Receipts on credit: {debtors.Sum(d => d.Sales.Count)}", $"Veresiye fiş: {debtors.Sum(d => d.Sales.Count)}",
            $"Qarzga cheklar: {debtors.Sum(d => d.Sales.Count)}")));
        if (old > 0.005)
            DebtsHost.Children.Add(Line(T5($"Больше 30 дней: {Money(old)}", $"30 күндөн ашык: {Money(old)}", $"Over 30 days: {Money(old)}",
                $"30 günden fazla: {Money(old)}", $"30 kundan ortiq: {Money(old)}"), bad: true));
        DebtsHost.Children.Add(new Border { Height = 6 });
        foreach (var d in debtors.OrderByDescending(d => d.Total).Take(3))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var who = new StackPanel { Spacing = 1 };
            var name = new TextBlock { Text = d.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var since = new TextBlock
            {
                Text = T5($"с {d.Oldest:dd.MM} · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} баштап · {DebtsView.Days(d.Oldest)}",
                    $"since {d.Oldest:dd.MM} · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} tarihinden · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} dan · {DebtsView.Days(d.Oldest)}"),
                FontSize = 13,
            };
            since.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(DebtsView.AgeDays(d.Oldest) > 30 ? "BrushDanger" : "BrushTextSoft"));
            who.Children.Add(name);
            who.Children.Add(since);
            row.Children.Add(who);
            var amount = new TextBlock { Text = Money(d.Total), FontSize = 14, FontWeight = FontWeight.SemiBold, Classes = { "num" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            amount.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            Grid.SetColumn(amount, 1);
            row.Children.Add(amount);
            DebtsHost.Children.Add(row);
        }
        if (DebtsCard.Cursor is null)
        {
            DebtsCard.Cursor = new Cursor(StandardCursorType.Hand);
            DebtsCard.PointerPressed += (_, e) =>
            {
                if (!e.Handled)
                    OpenDebts();
            };
        }
    }

    private void DebtsLink_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenDebts();
    }
}
