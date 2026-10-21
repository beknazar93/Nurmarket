using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этап П2: карточка поставщика в разделе «Поставщики» (на месте списка, «← Все поставщики» — назад).
/// Шапка — название, телефон и реквизиты, «Изменить» (окно SupplierEditDialog). Вкладки-сегменты: «Сводка» (плитки долг / просрочено /
/// аванс / закупки и возвраты за период, ближайший платёж, предупреждения, реквизиты), «Долги» (сделки-долги NurCRM с графиком
/// взносов, остаток, срок, просрочка), «Закупки» (история закупок: оплачено / в долг, состав), «Товары» (товары с этим поставщиком),
/// «Возвраты» (возвраты поставщику), «Оплаты» (оплаты по сделкам-долгам). Только просмотр: кнопок оплаты нет (оплата — этап П4).
/// Предупреждения из исследования показываются честно и ничего не исправляют: закупка «в долг» без записи долга, похожие на двойную
/// запись долги, возврат «в счёт долга», который сервер пока не вычитает.</summary>
public sealed class SupplierCardView : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly SupplierApiService _svc;
    private readonly IReadOnlyList<SupplierInfo> _all;
    private readonly Action _back;
    private readonly Action<SupplierInfo> _changed;
    private SupplierInfo _info;
    private SupplierSummary? _summary;

    private readonly TextBlock _name = new() { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sub = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    // Справа — место под полосу прокрутки (на 1024 плитки уходили под неё).
    private readonly StackPanel _body = new() { Spacing = 10, Margin = new Thickness(0, 0, 14, 4) };
    private readonly ScrollViewer _scroll;
    private string _tab = "summary";
    private string _period = "month";
    private bool _paidExpanded;
    private string _productQuery = "";
    private readonly HashSet<string> _expanded = new();

    // Данные карточки (загружаются разом при открытии; ошибка части — подсказкой на её вкладке).
    private bool _loading = true;
    private List<SupplierDeal> _deals = new();
    private SupplierKpis? _kpis;
    private List<SupplierPurchaseLine> _purchases = new();
    private bool _purchasesAvailable = true;
    private double _purchasesTotal;
    private int _purchasesCount;
    private List<SupplierReceipt> _receipts = new();
    private List<SupplierProduct> _products = new();
    private List<SupplierReturnInfo> _returns = new();
    private readonly Dictionary<string, string> _errors = new();
    // 2026-10-20, этап П3: не записанные долги этого поставщика (очередь SupplierDebtRecorder).
    private SupplierDebtQueue _queue = new(false, null, Array.Empty<SupplierDebtEntry>());

    public string SupplierId => _info.Id;

    public SupplierCardView(SupplierApiService svc, SupplierInfo info, SupplierSummary? summary, IReadOnlyList<SupplierInfo> all, Action back, Action<SupplierInfo> changed)
    {
        _svc = svc;
        _info = info;
        _summary = summary;
        _all = all;
        _back = back;
        _changed = changed;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        // Шапка: «← Все поставщики» и «Изменить» — обводкой; ниже название и реквизиты одной строкой.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var left = new StackPanel { Spacing = 8 };
        var backButton = SmallOutline(T("← Все поставщики", "← Бардык жеткирүүчүлөр", "← All suppliers", "← Tüm tedarikçiler", "← Barcha yetkazib beruvchilar"));
        backButton.HorizontalAlignment = HorizontalAlignment.Left;
        backButton.Click += (_, _) => _back();
        left.Children.Add(backButton);
        Use(_name, TextBlock.ForegroundProperty, "BrushText");
        Use(_sub, TextBlock.ForegroundProperty, "BrushTextSoft");
        left.Children.Add(new StackPanel { Children = { _name, _sub } });
        head.Children.Add(left);
        var edit = SmallOutline(T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"));
        Avalonia.Automation.AutomationProperties.SetName(edit, T("Изменить реквизиты поставщика", "Жеткирүүчүнүн реквизиттерин өзгөртүү", "Edit supplier details", "Tedarikçi bilgilerini düzenle", "Yetkazib beruvchi rekvizitlarini o'zgartirish"));
        edit.VerticalAlignment = VerticalAlignment.Bottom;
        edit.Margin = new Thickness(12, 0, 0, 0);
        edit.Click += async (_, _) => await EditAsync().ConfigureAwait(true);
        Grid.SetColumn(edit, 1);
        head.Children.Add(edit);
        root.Children.Add(head);

        var tabs = UiKit.Segment(_tabs);
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        tabs.Margin = new Thickness(0, 0, 0, 14);
        var tabsScroll = new ScrollViewer
        {
            Content = tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(tabsScroll, 1);
        root.Children.Add(tabsScroll);

        _scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 2);
        root.Children.Add(_scroll);
        Content = root;

        ShowHeader();
        BuildTabs();
        Render();
        AttachedToVisualTree += async (_, _) =>
        {
            if (!_loading || _started)
                return;
            _started = true;
            await LoadAsync().ConfigureAwait(true);
        };
    }

    private bool _started;

    /// <summary>Список досчитал долги (kpis) — плитки «Сводки» берут их.</summary>
    public void UpdateSummary(SupplierSummary? summary)
    {
        _summary = summary;
        if (!_loading)
            Render();
    }

    private void ShowHeader()
    {
        _name.Text = _info.Name;
        _sub.Text = string.Join(" · ", new[] { _info.Phone, _info.Llc, _info.Inn.Length > 0 ? T("ИНН ", "ИНН ", "Tax ID ", "Vergi no ", "STIR ") + _info.Inn : "", _info.Address }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        _sub.IsVisible = _sub.Text.Length > 0;
    }

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("summary", T("Сводка", "Жыйынтык", "Summary", "Özet", "Umumiy")),
                     ("debts", T("Долги", "Карыздар", "Debts", "Borçlar", "Qarzlar")),
                     ("purchases", T("Закупки", "Сатып алуулар", "Purchases", "Alımlar", "Xaridlar")),
                     ("products", T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar")),
                     ("returns", T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar")),
                     ("payments", T("Оплаты", "Төлөмдөр", "Payments", "Ödemeler", "To'lovlar")),
                 })
        {
            var seg = UiKit.Seg(text, key == _tab);
            seg.Click += (_, _) =>
            {
                _tab = key;
                BuildTabs();
                Render();
                _scroll.Offset = default;
            };
            _tabs.Children.Add(seg);
        }
    }

    // ------------------------------------------------------------------ загрузка

    private async Task LoadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var id = _info.Id;
        async Task Part(string key, Func<Task> load)
        {
            try
            {
                await load().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _errors[key] = ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi")
                    : ServerTelegramBotApi.DescribeFields(ex);
                PosLogger.Log($"Поставщик {id}: «{key}» не загружено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
        await Task.WhenAll(
            Part("debts", async () => _deals = await _svc.GetDealsAsync(id, cts.Token).ConfigureAwait(true)),
            Part("kpis", async () => _kpis = await _svc.GetKpisAsync(id, cts.Token).ConfigureAwait(true)),
            Part("purchases", async () =>
            {
                var (lines, total, count, available) = await _svc.GetPurchasesAsync(id, cts.Token).ConfigureAwait(true);
                (_purchases, _purchasesTotal, _purchasesCount, _purchasesAvailable) = (lines, total, count, available);
            }),
            Part("receipts", async () => _receipts = (await _svc.GetReceiptsAsync(id, 200, cts.Token).ConfigureAwait(true)).Receipts),
            Part("products", async () => _products = await _svc.GetProductsAsync(id, cts.Token).ConfigureAwait(true)),
            Part("returns", async () => _returns = await _svc.GetReturnsAsync(id, cts.Token).ConfigureAwait(true))).ConfigureAwait(true);
        _loading = false;
        _queue = SupplierDebtRecorder.LoadQueue(id);
        PosLogger.Log($"Поставщик {id}: сделок {_deals.Count}, закупок {_purchases.Count}, приходов {_receipts.Count}, товаров {_products.Count}, возвратов {_returns.Count}"
                      + (_errors.Count > 0 ? $", не загружено: {string.Join(", ", _errors.Keys)}" : "") + ".", "INFO");
        Render();
    }

    // ------------------------------------------------------------------ общие расчёты

    private List<SupplierDeal> OpenDebts => _deals.Where(d => d.IsDebt && d.IsOpen).ToList();
    private double Debt => _kpis?.Debt ?? (_errors.ContainsKey("debts") ? _summary?.Debt ?? 0 : OpenDebts.Sum(d => d.Remaining));
    private double Overdue => _errors.ContainsKey("debts") ? _summary?.Overdue ?? 0 : OpenDebts.Sum(d => d.Overdue);
    // 2026-10-20, аудит владельца: аванс — внесено по сделкам-предоплатам, а не kpis.prepayment (31 640 = сумма 31 628 + внесено 12).
    private double Advance => SupplierApiService.Advance(_kpis, _errors.ContainsKey("debts") ? null : _deals);
    private SupplierInstallment? NextInstallment => OpenDebts.Select(d => d.NextInstallment).Where(i => i is not null).OrderBy(i => i!.DueDate).FirstOrDefault();

    /// <summary>Закупки: по «Истории закупок» (с оплатой), а если её нет — по приходам (без способа оплаты).</summary>
    private List<SupplierPurchaseGroup> Groups => _purchasesAvailable && _purchases.Count > 0
        ? SupplierApiService.GroupPurchases(_purchases)
        : _receipts.Where(r => r.CreatedAt.HasValue).Select(r => new SupplierPurchaseGroup(r.CreatedAt!.Value,
            r.Lines.Select(l => new SupplierPurchaseLine(l.Id, l.ProductName, l.Qty, l.Unit, l.Qty * l.PurchasePrice, "", r.CreatedAt)).ToList())).ToList();

    /// <summary>2026-10-20, этап П3: закупки «в долг» без сделки-долга — кроме тех, что уже в очереди «долг не записан» (своя плашка) или
    /// записаны журналом; сделка с меткой прихода (записана позже кнопкой «Записать долг») — тоже записанная.</summary>
    private List<SupplierPurchaseGroup> Unrecorded
    {
        get
        {
            if (_errors.ContainsKey("debts"))
                return new();
            var skip = new HashSet<string>(SupplierDebtRecorder.DoneKeys(), StringComparer.Ordinal);
            skip.UnionWith(_queue.Open.Select(e => e.Key));
            return SupplierApiService.DebtPurchasesWithoutDeal(Groups, _deals, MarkerOf).Where(g => !skip.Contains(MarkerOf(g))).ToList();
        }
    }

    /// <summary>Метка закупки для записи долга: id прихода (тот же, что у кассы при приёмке), без прихода — время закупки.</summary>
    private string MarkerOf(SupplierPurchaseGroup g) =>
        ReceiptFor(g) is { Id.Length: > 0 } r ? SupplierDebtRecorder.MarkerForReceipt(r.Id) : SupplierDebtRecorder.MarkerForPurchase(g.At);

    /// <summary>Журнал записи долгов изменился (другое окно, касса на этом ПК) — плашка очереди заново.</summary>
    public void RefreshDebtQueue()
    {
        _queue = SupplierDebtRecorder.LoadQueue(_info.Id);
        if (!_loading)
            Render();
    }

    /// <summary>Плашка «Долг не записан» этого поставщика (кнопка — повтор очереди, как в списке).</summary>
    private void AddQueueBox()
    {
        if (SuppliersWindow.SupplierDebtQueueBox(this, _queue, _info.Name, async () =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner)
                    return;
                var dialog = new SupplierDebtRecordDialog(_queue.Open.Select(e => e.ToRequest()).ToList(), editableTerm: false);
                await dialog.ShowDialog(owner).ConfigureAwait(true);
                _queue = SupplierDebtRecorder.LoadQueue(_info.Id);
                if (dialog.Recorded > 0)
                    await ReloadAsync().ConfigureAwait(true);
                else
                    Render();
            }) is not { } content)
            return;
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = content };
        Use(box, Border.BackgroundProperty, _queue.Closed ? "BrushDangerSoft" : "BrushWarningSoft");
        _body.Children.Add(box);
    }

    /// <summary>Закупка «в долг» без записи долга — жёлтая плашка с «Записать долг» (владелец/админ): подтверждение со сроком, та же защита.</summary>
    private Control UnrecordedBox(SupplierPurchaseGroup g, string? text = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var t = new TextBlock { Text = "⚠ " + (text ?? UnrecordedText(g)), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        grid.Children.Add(t);
        if (SupplierDebtUi.CanRecordDebts)
        {
            var button = SmallOutline(T("Записать долг", "Карызды жазуу", "Record debt", "Borcu kaydet", "Qarzni yozish"));
            Avalonia.Automation.AutomationProperties.SetName(button, T($"Записать долг за закупку {g.At:dd.MM.yyyy HH:mm}", $"{g.At:dd.MM.yyyy HH:mm} сатып алуу үчүн карызды жазуу",
                $"Record the debt for the purchase of {g.At:dd.MM.yyyy HH:mm}", $"{g.At:dd.MM.yyyy HH:mm} alımı için borcu kaydet", $"{g.At:dd.MM.yyyy HH:mm} xarid uchun qarzni yozish"));
            button.Margin = new Thickness(12, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += async (_, _) => await RecordOldAsync(g).ConfigureAwait(true);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = grid };
        Use(box, Border.BackgroundProperty, "BrushWarningSoft");
        return box;
    }

    private async Task RecordOldAsync(SupplierPurchaseGroup g)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var amount = Math.Round(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount), 2);
        var receipt = ReceiptFor(g);
        var request = new SupplierDebtRequest(_info.Id, _info.Name, MarkerOf(g), receipt?.Id ?? "", amount, g.At, DateTime.Today.AddDays(30), 30, "manual");
        var dialog = new SupplierDebtRecordDialog(new[] { request }, editableTerm: true);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Recorded > 0)
            await ReloadAsync().ConfigureAwait(true);
        else
            RefreshDebtQueue();
    }

    /// <summary>Перечитать карточку после записи долга (долги, kpis, закупки).</summary>
    private async Task ReloadAsync()
    {
        _errors.Clear();
        _loading = true;
        Render();
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Приход, совпавший с закупкой по времени (± 3 с): кто принял, сколько возвращено.</summary>
    private SupplierReceipt? ReceiptFor(SupplierPurchaseGroup g) =>
        _receipts.Where(r => r.CreatedAt is { } at && Math.Abs((at - g.At).TotalSeconds) <= 3).OrderBy(r => Math.Abs((r.CreatedAt!.Value - g.At).TotalSeconds)).FirstOrDefault();

    private DateTime? PeriodStart => _period switch
    {
        "month" => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
        "quarter" => DateTime.Today.AddMonths(-3),
        "year" => DateTime.Today.AddYears(-1),
        _ => null,
    };

    // ------------------------------------------------------------------ отрисовка

    private void Render()
    {
        _body.Children.Clear();
        if (_loading)
        {
            _body.Children.Add(Soft(T("Загружаю карточку поставщика…", "Жеткирүүчүнүн карточкасы жүктөлүүдө…", "Loading the supplier card…", "Tedarikçi kartı yükleniyor…", "Yetkazib beruvchi kartasi yuklanmoqda…")));
            return;
        }
        switch (_tab)
        {
            case "debts": RenderDebts(); break;
            case "purchases": RenderPurchases(); break;
            case "products": RenderProducts(); break;
            case "returns": RenderReturns(); break;
            case "payments": RenderPayments(); break;
            default: RenderSummary(); break;
        }
    }

    private bool PartError(string key)
    {
        if (!_errors.TryGetValue(key, out var why))
            return false;
        var e = new TextBlock { Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + why, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = true };
        _body.Children.Add(SectionCards.ErrorBox(this, e));
        return true;
    }

    private void RenderSummary()
    {
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, text) in new[]
                 {
                     ("month", T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy")), ("quarter", T("3 месяца", "3 ай", "3 months", "3 ay", "3 oy")),
                     ("year", T("Год", "Жыл", "Year", "Yıl", "Yil")), ("all", T("Всё время", "Бардык убакыт", "All time", "Tüm zamanlar", "Butun davr")),
                 })
        {
            var seg = UiKit.Seg(text, key == _period);
            seg.Click += (_, _) =>
            {
                _period = key;
                Render();
            };
            periods.Children.Add(seg);
        }
        var periodBox = UiKit.Segment(periods);
        periodBox.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(periodBox);

        var from = PeriodStart;
        var groups = Groups.Where(g => from is null || g.At >= from).ToList();
        var returns = _returns.Where(r => from is null || (r.CreatedAt ?? DateTime.MinValue) >= from).ToList();
        var kpi = new NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid { MinTileWidth = 180, Spacing = 8 };
        kpi.Children.Add(UiKit.Kpi(T("Мы должны", "Биз карызбыз", "We owe", "Borcumuz", "Qarzimiz"), MoneyText.Som(Debt), danger: Debt > 0.005));
        kpi.Children.Add(UiKit.Kpi(T("Просрочено", "Мөөнөтү өттү", "Overdue", "Vadesi geçmiş", "Muddati o'tgan"), MoneyText.Som(Overdue), danger: Overdue > 0.005));
        kpi.Children.Add(UiKit.Kpi(T("Аванс у поставщика", "Жеткирүүчүдөгү аванс", "Advance to the supplier", "Tedarikçideki avans", "Yetkazib beruvchidagi avans"), MoneyText.Som(Advance)));
        kpi.Children.Add(UiKit.Kpi(T($"Закупки за период ({groups.Count})", $"Мезгилдеги сатып алуулар ({groups.Count})", $"Purchases in the period ({groups.Count})",
            $"Dönemdeki alımlar ({groups.Count})", $"Davrdagi xaridlar ({groups.Count})"), MoneyText.Som(groups.Sum(g => g.Total))));
        kpi.Children.Add(UiKit.Kpi(T($"Возвраты за период ({returns.Count})", $"Мезгилдеги кайтаруулар ({returns.Count})", $"Returns in the period ({returns.Count})",
            $"Dönemdeki iadeler ({returns.Count})", $"Davrdagi qaytarishlar ({returns.Count})"), MoneyText.Som(returns.Sum(r => r.Total))));
        _body.Children.Add(kpi);
        // 2026-10-20, аудит владельца: у одного поставщика «Мы должны» и «Аванс» сразу — непонятно, кто кому должен. Поясняем.
        if (Advance > 0.005)
            _body.Children.Add(Soft(Debt > 0.005
                ? T("Аванс — сколько внесено вперёд по сделкам «Предоплата». Он не вычитается из долга: долг и предоплата — разные сделки NurCRM.",
                    "Аванс — «Алдын ала төлөм» келишимдери боюнча алдын ала төлөнгөнү. Ал карыздан кемитилбейт: карыз жана алдын ала төлөм — NurCRMдеги башка-башка келишимдер.",
                    "Advance is what was paid ahead on “Prepayment” deals. It is not subtracted from the debt: debt and prepayment are separate NurCRM deals.",
                    "Avans, «Ön ödeme» anlaşmalarında peşin ödenen tutardır. Borçtan düşülmez: borç ve ön ödeme NurCRM'de ayrı anlaşmalardır.",
                    "Avans — «Oldindan to'lov» bitimlari bo'yicha oldindan to'langani. U qarzdan ayirilmaydi: qarz va oldindan to'lov — NurCRMda alohida bitimlar.")
                : T("Аванс — сколько внесено вперёд по сделкам «Предоплата».", "Аванс — «Алдын ала төлөм» келишимдери боюнча алдын ала төлөнгөнү.",
                    "Advance is what was paid ahead on “Prepayment” deals.", "Avans, «Ön ödeme» anlaşmalarında peşin ödenen tutardır.",
                    "Avans — «Oldindan to'lov» bitimlari bo'yicha oldindan to'langani.")));

        // Ближайший платёж по графику.
        var next = NextInstallment;
        var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Use(line, TextBlock.ForegroundProperty, "BrushText");
        if (next?.DueDate is { } due)
        {
            line.Inlines!.Add(new Run(T("Ближайший платёж: ", "Жакынкы төлөм: ", "Next payment: ", "Sonraki ödeme: ", "Keyingi to'lov: ")));
            var r = new Run($"{due:dd.MM.yyyy} — {MoneyText.Som(next.Remaining)}") { FontWeight = FontWeight.SemiBold };
            if (due.Date < DateTime.Today)
                r.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
            line.Inlines.Add(r);
            if (due.Date < DateTime.Today)
                line.Inlines.Add(new Run(T($" · просрочен на {(DateTime.Today - due.Date).Days} дн.", $" · {(DateTime.Today - due.Date).Days} күн кечикти", $" · {(DateTime.Today - due.Date).Days} days overdue",
                    $" · {(DateTime.Today - due.Date).Days} gün gecikti", $" · {(DateTime.Today - due.Date).Days} kun kechikdi")));
        }
        else
            line.Text = Debt > 0.005
                ? T("Срока оплаты у долга нет — график на сайте не задан.", "Карыздын төлөө мөөнөтү жок — сайтта график коюлган эмес.", "The debt has no due date — no schedule was set on the website.",
                    "Borcun vadesi yok — sitede plan belirlenmemiş.", "Qarzning to'lov muddati yo'q — saytda jadval belgilanmagan.")
                : T("Платежей по графику нет — перед поставщиком долгов нет.", "График боюнча төлөм жок — жеткирүүчү алдында карыз жок.", "No scheduled payments — nothing is owed to the supplier.",
                    "Planlı ödeme yok — tedarikçiye borç yok.", "Jadval bo'yicha to'lov yo'q — yetkazib beruvchi oldida qarz yo'q.");
        _body.Children.Add(line);

        // Предупреждения (только показ): закупка в долг без записи долга, двойные записи, возврат «в счёт долга».
        var warnings = new List<string>();
        // 2026-10-20, этап П3: не записанные долги — плашками с кнопкой «Записать долг».
        AddQueueBox();
        foreach (var g in Unrecorded)
            _body.Children.Add(UnrecordedBox(g));
        foreach (var d in SupplierApiService.SuspectedDuplicates(_deals))
            warnings.Add(T($"Похоже на двойную запись: долг «{d.Title}» на {MoneyText.Som(d.Amount)} записан дважды ({d.CreatedAt:dd.MM.yyyy HH:mm}). Проверьте на сайте NurCRM.",
                $"Эки жолу жазылганга окшойт: «{d.Title}» карызы {MoneyText.Som(d.Amount)} эки жолу жазылган ({d.CreatedAt:dd.MM.yyyy HH:mm}). NurCRM сайтынан текшериңиз.",
                $"Looks like a double entry: the debt “{d.Title}” of {MoneyText.Som(d.Amount)} was recorded twice ({d.CreatedAt:dd.MM.yyyy HH:mm}). Check it on the NurCRM website.",
                $"Çift kayıt gibi görünüyor: «{d.Title}» borcu {MoneyText.Som(d.Amount)} iki kez kaydedilmiş ({d.CreatedAt:dd.MM.yyyy HH:mm}). NurCRM sitesinden kontrol edin.",
                $"Ikki marta yozilganga o'xshaydi: «{d.Title}» qarzi {MoneyText.Som(d.Amount)} ikki marta yozilgan ({d.CreatedAt:dd.MM.yyyy HH:mm}). NurCRM saytida tekshiring."));
        foreach (var r in _returns.Where(r => r.Compensation == "debt_offset"))
            warnings.Add(OffsetText(r));
        if (warnings.Count > 0)
            _body.Children.Add(WarningBox(warnings));

        // Реквизиты.
        var reqs = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
        var items = new (string Label, string Value)[]
        {
            (T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), _info.Phone),
            ("Email", _info.Email),
            (T("ОсОО / компания", "ЖЧК / компания", "Company", "Şirket", "MChJ / kompaniya"), _info.Llc),
            (T("ИНН", "ИНН", "Tax ID", "Vergi no", "STIR"), _info.Inn),
            (T("ОКПО", "ОКПО", "OKPO", "OKPO", "OKPO"), _info.Okpo),
            (T("Расчётный счёт", "Эсептешүү эсеби", "Bank account", "Banka hesabı", "Hisob raqami"), _info.Score),
            (T("БИК", "БИК", "Bank code (BIK)", "Banka kodu (BIK)", "BIK"), _info.Bik),
            (T("В базе с", "Базада", "Added", "Eklendi", "Bazada"), _info.CreatedAt is { } c ? c.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : ""),
            (T("Адрес", "Дарек", "Address", "Adres", "Manzil"), _info.Address),
        };
        for (var i = 0; i < items.Length; i++)
        {
            var row = i / 2;
            var col = i % 2 * 2;
            if (reqs.RowDefinitions.Count <= row)
                reqs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = items[i].Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(col == 0 ? 0 : 24, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
            Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
            var value = new TextBlock { Text = items[i].Value.Length > 0 ? items[i].Value : "—", FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Use(value, TextBlock.ForegroundProperty, items[i].Value.Length > 0 ? "BrushText" : "BrushTextSoft");
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, col + 1);
            if (i == items.Length - 1 && col == 0)
                Grid.SetColumnSpan(value, 3);
            reqs.Children.Add(label);
            reqs.Children.Add(value);
        }
        var reqBox = new StackPanel { Spacing = 6 };
        reqBox.Children.Add(Caption(T("Реквизиты", "Реквизиттер", "Details", "Künye", "Rekvizitlar")));
        reqBox.Children.Add(reqs);
        if (_info.Inn.Length == 0 && _info.Score.Length == 0)
            reqBox.Children.Add(Soft(T("ИНН и расчётный счёт не заполнены — их можно добавить кнопкой «Изменить».", "ИНН жана эсептешүү эсеби толтурулган эмес — «Өзгөртүү» баскычы менен кошсо болот.",
                "Tax ID and bank account are empty — add them with “Edit”.", "Vergi no ve banka hesabı boş — «Düzenle» ile ekleyebilirsiniz.",
                "STIR va hisob raqami to'ldirilmagan — ularni «O'zgartirish» tugmasi bilan qo'shish mumkin.")));
        _body.Children.Add(SectionCards.Group(this, reqBox));
        PartError("kpis");
    }

    private string UnrecordedText(SupplierPurchaseGroup g) =>
        T($"Закупка в долг без записи долга: {g.At:dd.MM.yyyy HH:mm} на {MoneyText.Som(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount))}. Долг не виден в «Долгах» и на сайте NurCRM.",
            $"Карызга сатып алуу, карыз жазылган эмес: {g.At:dd.MM.yyyy HH:mm}, {MoneyText.Som(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount))}. Карыз «Карыздарда» жана NurCRM сайтында көрүнбөйт.",
            $"Purchase on credit without a debt record: {g.At:dd.MM.yyyy HH:mm}, {MoneyText.Som(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount))}. The debt isn't shown in “Debts” or on the NurCRM website.",
            $"Borç kaydı olmayan veresiye alım: {g.At:dd.MM.yyyy HH:mm}, {MoneyText.Som(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount))}. Borç «Borçlar»da ve NurCRM sitesinde görünmüyor.",
            $"Qarz yozuvisiz qarzga xarid: {g.At:dd.MM.yyyy HH:mm}, {MoneyText.Som(g.Lines.Where(l => l.PaymentType == "debt").Sum(l => l.Amount))}. Qarz «Qarzlar»da va NurCRM saytida ko'rinmaydi.");

    private static string OffsetText(SupplierReturnInfo r) =>
        T($"Возврат «в счёт долга» {r.CreatedAt:dd.MM.yyyy} на {MoneyText.Som(r.Total)}: сервер пока не уменьшает по нему долг — сверьте остаток с поставщиком.",
            $"«Карыздын эсебине» кайтаруу {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}: сервер азырынча ал боюнча карызды азайтпайт — калдыкты жеткирүүчү менен салыштырыңыз.",
            $"Return “against the debt” on {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}: the server doesn't reduce the debt for it yet — check the balance with the supplier.",
            $"«Borca mahsuben» iade {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}: sunucu bunun için borcu henüz düşürmüyor — bakiyeyi tedarikçiyle doğrulayın.",
            $"«Qarz hisobiga» qaytarish {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}: server hozircha u bo'yicha qarzni kamaytirmaydi — qoldiqni yetkazib beruvchi bilan solishtiring.");

    // ── Долги ──

    private void RenderDebts()
    {
        if (PartError("debts"))
            return;
        var open = OpenDebts.OrderBy(d => d.NextInstallment?.DueDate ?? DateTime.MaxValue).ToList();
        var paid = _deals.Where(d => d.IsDebt && !d.IsOpen).ToList();
        var prepayments = _deals.Where(d => d.IsPrepayment).ToList();
        AddQueueBox();
        foreach (var g in Unrecorded)
            _body.Children.Add(UnrecordedBox(g));
        if (open.Count == 0 && paid.Count == 0 && prepayments.Count == 0)
        {
            _body.Children.Add(SectionCards.Empty(this, T("Долгов перед этим поставщиком нет. Закупка «в долг» на сайте NurCRM записывает долг сюда — с графиком платежей и сроком.",
                "Бул жеткирүүчү алдында карыз жок. NurCRM сайтында «карызга» сатып алуу карызды бул жерге жазат — төлөм графиги жана мөөнөтү менен.",
                "Nothing is owed to this supplier. A purchase “on credit” on the NurCRM website records the debt here — with a payment schedule and due date.",
                "Bu tedarikçiye borç yok. NurCRM sitesinde «veresiye» alım borcu buraya ödeme planı ve vadesiyle kaydeder.",
                "Bu yetkazib beruvchi oldida qarz yo'q. NurCRM saytida «qarzga» xarid qarzni shu yerga — to'lov jadvali va muddati bilan yozadi.")));
            return;
        }
        // Итог одной строкой; красное — только сумма долга и просрочка.
        var total = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(total, TextBlock.ForegroundProperty, "BrushText");
        total.Inlines!.Add(new Run(T("Мы должны ", "Биз карызбыз ", "We owe ", "Borcumuz ", "Qarzimiz ")));
        total.Inlines.Add(Red(MoneyText.Som(open.Sum(d => d.Remaining)), Debt > 0.005));
        if (Overdue > 0.005)
        {
            total.Inlines.Add(new Run(T(" · просрочено ", " · мөөнөтү өттү ", " · overdue ", " · vadesi geçmiş ", " · muddati o'tgan ")));
            total.Inlines.Add(Red(MoneyText.Som(Overdue), true));
        }
        total.Inlines.Add(new Run(T($" · долгов: {open.Count}", $" · карыздар: {open.Count}", $" · debts: {open.Count}", $" · borç: {open.Count}", $" · qarzlar: {open.Count}")));
        _body.Children.Add(total);
        if (open.Count == 0)
            _body.Children.Add(Soft(T("Все долги перед поставщиком погашены.", "Жеткирүүчү алдындагы бардык карыздар төлөндү.", "All debts to the supplier are paid off.", "Tedarikçiye tüm borçlar ödendi.", "Yetkazib beruvchi oldidagi barcha qarzlar to'langan.")));
        var duplicates = SupplierApiService.SuspectedDuplicates(_deals).Select(d => d.Id).ToHashSet();
        foreach (var d in open)
            _body.Children.Add(DebtCard(d, duplicates.Contains(d.Id)));

        if (prepayments.Count > 0)
        {
            _body.Children.Add(Caption(T("Предоплаты поставщику", "Жеткирүүчүгө алдын ала төлөмдөр", "Prepayments to the supplier", "Tedarikçiye ön ödemeler", "Yetkazib beruvchiga oldindan to'lovlar")));
            foreach (var d in prepayments)
                _body.Children.Add(SimpleDealCard(d, T($"сумма {MoneyText.Som(d.Amount)} · внесено {MoneyText.Som(d.Prepayment)}", $"суммасы {MoneyText.Som(d.Amount)} · төлөндү {MoneyText.Som(d.Prepayment)}",
                    $"amount {MoneyText.Som(d.Amount)} · paid {MoneyText.Som(d.Prepayment)}", $"tutar {MoneyText.Som(d.Amount)} · ödenen {MoneyText.Som(d.Prepayment)}", $"summa {MoneyText.Som(d.Amount)} · to'langan {MoneyText.Som(d.Prepayment)}")));
        }
        if (paid.Count > 0)
        {
            var toggle = SmallOutline(_paidExpanded
                ? T("▴ Скрыть погашенные", "▴ Төлөнгөндөрдү жашыруу", "▴ Hide paid off", "▴ Ödenenleri gizle", "▴ To'langanlarni yashirish")
                : T($"▾ Погашенные долги ({paid.Count})", $"▾ Төлөнгөн карыздар ({paid.Count})", $"▾ Paid-off debts ({paid.Count})", $"▾ Ödenmiş borçlar ({paid.Count})", $"▾ To'langan qarzlar ({paid.Count})"));
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Click += (_, _) =>
            {
                _paidExpanded = !_paidExpanded;
                Render();
            };
            _body.Children.Add(toggle);
            if (_paidExpanded)
                foreach (var d in paid)
                    _body.Children.Add(SimpleDealCard(d, T($"{MoneyText.Som(d.Amount)} · погашен", $"{MoneyText.Som(d.Amount)} · төлөндү", $"{MoneyText.Som(d.Amount)} · paid off",
                        $"{MoneyText.Som(d.Amount)} · ödendi", $"{MoneyText.Som(d.Amount)} · to'langan")));
        }
    }

    private Control DebtCard(SupplierDeal d, bool duplicate)
    {
        var body = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 3 };
        var titleRow = new WrapPanel();
        var title = new TextBlock { Text = d.Title.Length > 0 ? d.Title : T("Долг", "Карыз", "Debt", "Borç", "Qarz"), FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        titleRow.Children.Add(title);
        if (d.Overdue > 0.005)
            titleRow.Children.Add(SectionCards.Pill(this, T($"просрочено {d.OverdueDays} дн.", $"{d.OverdueDays} күн кечикти", $"{d.OverdueDays} days overdue", $"{d.OverdueDays} gün gecikti", $"{d.OverdueDays} kun kechikdi"),
                "BrushDangerSoft", "BrushDangerOnSoft"));
        if (duplicate)
            titleRow.Children.Add(SectionCards.Pill(this, T("похоже на двойную запись", "эки жолу жазылганга окшойт", "looks like a double entry", "çift kayıt olabilir", "ikki marta yozilganga o'xshaydi"),
                "BrushWarningSoft", "BrushText"));
        who.Children.Add(titleRow);
        var next = d.NextInstallment;
        who.Children.Add(Soft(string.Join(" · ", new[]
        {
            d.CreatedAt is { } c ? T($"записан {c:dd.MM.yyyy}", $"{c:dd.MM.yyyy} жазылган", $"recorded {c:dd.MM.yyyy}", $"{c:dd.MM.yyyy} kaydedildi", $"{c:dd.MM.yyyy} yozilgan") : "",
            next?.DueDate is { } due ? T($"ближайший срок {due:dd.MM.yyyy}", $"жакынкы мөөнөт {due:dd.MM.yyyy}", $"next due {due:dd.MM.yyyy}", $"en yakın vade {due:dd.MM.yyyy}", $"eng yaqin muddat {due:dd.MM.yyyy}")
                : T("срок не задан", "мөөнөт коюлган эмес", "no due date", "vade yok", "muddat belgilanmagan"),
            T($"взносов: {d.Installments.Count}", $"бөлүктөр: {d.Installments.Count}", $"instalments: {d.Installments.Count}", $"taksit: {d.Installments.Count}", $"bo'laklar: {d.Installments.Count}"),
            d.Note,
        }.Where(x => x.Length > 0))));
        head.Children.Add(who);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        var left = new TextBlock { Text = MoneyText.Som(d.Remaining), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        left.Classes.Add("num");
        Use(left, TextBlock.ForegroundProperty, "BrushDanger");
        right.Children.Add(left);
        var of = Soft(T($"из {MoneyText.Som(d.Amount)} · оплачено {MoneyText.Som(d.Paid)}", $"{MoneyText.Som(d.Amount)} ичинен · төлөндү {MoneyText.Som(d.Paid)}", $"of {MoneyText.Som(d.Amount)} · paid {MoneyText.Som(d.Paid)}",
            $"{MoneyText.Som(d.Amount)} içinden · ödenen {MoneyText.Som(d.Paid)}", $"{MoneyText.Som(d.Amount)} dan · to'langan {MoneyText.Som(d.Paid)}"));
        of.HorizontalAlignment = HorizontalAlignment.Right;
        of.TextAlignment = TextAlignment.Right;
        right.Children.Add(of);
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        body.Children.Add(head);
        if (d.Installments.Count > 0)
            body.Children.Add(InstallmentsTable(d));
        return Card(body);
    }

    /// <summary>График взносов: № | срок | сумма | оплачено | остаток | статус (просроченный — красным).</summary>
    private Control InstallmentsTable(SupplierDeal d)
    {
        // Колонки по содержимому (числа — ровным столбцом справа), остаток ширины — у статуса.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        var r = 0;
        void Cell(string text, int col, bool header, bool right, string brush = "BrushText")
        {
            var t = new TextBlock
            {
                Text = text, FontSize = header ? 12 : 13, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(col == 0 ? 0 : col == 1 ? 16 : 28, header ? 0 : 4, 0, header ? 4 : 4),
            };
            if (!header)
                t.Classes.Add("num");
            Use(t, TextBlock.ForegroundProperty, header ? "BrushTextSoft" : brush);
            Grid.SetRow(t, r);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell("№", 0, true, false);
        Cell(T("Срок", "Мөөнөт", "Due", "Vade", "Muddat"), 1, true, false);
        Cell(T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), 2, true, true);
        Cell(T("Оплачено", "Төлөндү", "Paid", "Ödenen", "To'langan"), 3, true, true);
        Cell(T("Остаток", "Калдык", "Left", "Kalan", "Qoldiq"), 4, true, true);
        Cell(T("Статус", "Абалы", "Status", "Durum", "Holati"), 5, true, false);
        foreach (var i in d.Installments.Take(60))
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(i.Number.ToString(CultureInfo.InvariantCulture), 0, false, false);
            Cell(i.DueDate is { } due ? due.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—", 1, false, false);
            Cell(MoneyText.Som(i.Amount), 2, false, true);
            Cell(MoneyText.Som(i.PaidAmount), 3, false, true, "BrushTextSoft");
            Cell(MoneyText.Som(i.Remaining), 4, false, true, i.Remaining > 0.005 ? "BrushDanger" : "BrushTextSoft");
            var (status, brush) = i.Remaining <= 0.005
                ? (T("оплачен", "төлөндү", "paid", "ödendi", "to'langan"), "BrushTextSoft")
                : i.IsOverdue || i.OverdueAmount > 0.005
                    ? (T($"просрочен {i.OverdueDays} дн.", $"{i.OverdueDays} күн кечикти", $"{i.OverdueDays} days overdue", $"{i.OverdueDays} gün gecikti", $"{i.OverdueDays} kun kechikdi"), "BrushDanger")
                    : i.PaidAmount > 0.005
                        ? (T("оплачен частично", "жарым-жартылай төлөндү", "partly paid", "kısmen ödendi", "qisman to'langan"), "BrushText")
                        : (T("ожидается", "күтүлүүдө", "upcoming", "bekleniyor", "kutilmoqda"), "BrushText");
            Cell(status, 5, false, false, brush);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = grid };
        Use(box, Border.BackgroundProperty, "BrushPanelSoft");
        return box;
    }

    private Control SimpleDealCard(SupplierDeal d, string detail)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = d.Title.Length > 0 ? d.Title : "—", FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        who.Children.Add(title);
        who.Children.Add(Soft(string.Join(" · ", new[] { d.CreatedAt is { } c ? c.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "", detail }.Where(x => x.Length > 0))));
        head.Children.Add(who);
        if (d.IsPrepayment)
        {
            var sum = new TextBlock { Text = MoneyText.Som(d.Amount), FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sum.Classes.Add("num");
            Use(sum, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(sum, 1);
            head.Children.Add(sum);
        }
        return Card(head, padding: new Thickness(16, 10));
    }

    // ── Закупки ──

    private void RenderPurchases()
    {
        if (_errors.ContainsKey("purchases") && _errors.ContainsKey("receipts"))
        {
            PartError("purchases");
            return;
        }
        var groups = Groups;
        if (groups.Count == 0)
        {
            _body.Children.Add(SectionCards.Empty(this, T("Закупок у этого поставщика пока нет. Приход оформляется в «Складе» → «Приёмка» с выбором поставщика или на сайте NurCRM («Закупки»).",
                "Бул жеткирүүчүдөн азырынча сатып алуу жок. Кириш «Кампада» → «Кабыл алуу» бөлүмүндө жеткирүүчүнү тандап же NurCRM сайтында («Сатып алуулар») таризделет.",
                "No purchases from this supplier yet. Receive goods in “Warehouse” → “Receiving” with the supplier selected, or on the NurCRM website (“Procurement”).",
                "Bu tedarikçiden henüz alım yok. Mal kabulü «Depo» → «Kabul» bölümünde tedarikçi seçilerek veya NurCRM sitesinde («Alımlar») yapılır.",
                "Bu yetkazib beruvchidan hali xarid yo'q. Kirim «Ombor» → «Qabul» bo'limida yetkazib beruvchini tanlab yoki NurCRM saytida («Xaridlar») rasmiylashtiriladi.")));
            return;
        }
        var total = _purchasesAvailable && _purchases.Count > 0 ? _purchasesTotal : groups.Sum(g => g.Total);
        var count = _purchasesAvailable && _purchases.Count > 0 ? groups.Count : groups.Count;
        var line = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Text = T($"Закупок {count} на сумму {MoneyText.Som(total)}", $"Сатып алуулар {count}, суммасы {MoneyText.Som(total)}", $"{count} purchases worth {MoneyText.Som(total)}",
                $"{count} alım, toplam {MoneyText.Som(total)}", $"{count} ta xarid, jami {MoneyText.Som(total)}") };
        Use(line, TextBlock.ForegroundProperty, "BrushText");
        _body.Children.Add(line);
        if (!_purchasesAvailable || _purchases.Count == 0)
            _body.Children.Add(Soft(T("Способ оплаты закупок сервер пока не отдаёт — показаны приходы без отметки «оплачено / в долг».",
                "Сатып алуунун төлөм ыкмасын сервер азырынча бербейт — кириштер «төлөндү / карызга» белгисиз көрсөтүлдү.",
                "The server doesn't return the payment method yet — deliveries are shown without “paid / on credit”.",
                "Sunucu ödeme yöntemini henüz vermiyor — girişler «ödendi / veresiye» işareti olmadan gösteriliyor.",
                "Server hozircha to'lov usulini bermaydi — kirimlar «to'langan / qarzga» belgisisiz ko'rsatildi.")));
        var unrecorded = Unrecorded.Select(g => g.At).ToHashSet();
        foreach (var g in groups.Take(200))
            _body.Children.Add(PurchaseCard(g, unrecorded.Contains(g.At)));
    }

    private Control PurchaseCard(SupplierPurchaseGroup g, bool unrecorded)
    {
        var key = "p" + g.At.Ticks.ToString(CultureInfo.InvariantCulture);
        var receipt = ReceiptFor(g);
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 3 };
        var titleRow = new WrapPanel();
        var title = new TextBlock { Text = g.At.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), FontSize = 15, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        titleRow.Children.Add(title);
        switch (g.PaymentType)
        {
            case "debt": titleRow.Children.Add(SectionCards.Pill(this, T("в долг", "карызга", "on credit", "veresiye", "qarzga"), "BrushDangerSoft", "BrushDangerOnSoft")); break;
            case "cash": titleRow.Children.Add(SectionCards.Pill(this, T("оплачено", "төлөндү", "paid", "ödendi", "to'langan"))); break;
            case "prepayment": titleRow.Children.Add(SectionCards.Pill(this, T("предоплата", "алдын ала төлөм", "prepayment", "ön ödeme", "oldindan to'lov"))); break;
            case "mixed": titleRow.Children.Add(SectionCards.Pill(this, T("разные способы оплаты", "ар кандай төлөм", "mixed payment", "karma ödeme", "turli to'lov"))); break;
        }
        who.Children.Add(titleRow);
        who.Children.Add(Soft(string.Join(" · ", new[]
        {
            T($"позиций: {g.Lines.Count}", $"позициялар: {g.Lines.Count}", $"items: {g.Lines.Count}", $"kalem: {g.Lines.Count}", $"pozitsiyalar: {g.Lines.Count}"),
            receipt?.CreatedBy is { Length: > 0 } by ? T($"принял {by}", $"кабыл алган {by}", $"received by {by}", $"kabul eden {by}", $"qabul qilgan {by}") : "",
            receipt is { ReturnedAmount: > 0.005 } ? T($"возвращено {MoneyText.Som(receipt.ReturnedAmount)}", $"кайтарылды {MoneyText.Som(receipt.ReturnedAmount)}", $"returned {MoneyText.Som(receipt.ReturnedAmount)}",
                $"iade {MoneyText.Som(receipt.ReturnedAmount)}", $"qaytarildi {MoneyText.Som(receipt.ReturnedAmount)}") : "",
        }.Where(x => x.Length > 0))));
        head.Children.Add(who);
        var sum = new TextBlock { Text = MoneyText.Som(g.Total), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        sum.Classes.Add("num");
        Use(sum, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(sum, 1);
        head.Children.Add(sum);
        body.Children.Add(head);
        if (unrecorded)
            body.Children.Add(UnrecordedBox(g, T("Закупка в долг без записи долга — долг не виден в «Долгах» и на сайте NurCRM. Проверьте на сайте NurCRM.",
                "Карызга сатып алуу, карыз жазылган эмес — карыз «Карыздарда» жана NurCRM сайтында көрүнбөйт. NurCRM сайтынан текшериңиз.",
                "Purchase on credit without a debt record — the debt isn't shown in “Debts” or on the NurCRM website. Check it on the NurCRM website.",
                "Borç kaydı olmayan veresiye alım — borç «Borçlar»da ve NurCRM sitesinde görünmüyor. NurCRM sitesinden kontrol edin.",
                "Qarz yozuvisiz qarzga xarid — qarz «Qarzlar»da va NurCRM saytida ko'rinmaydi. NurCRM saytida tekshiring.")));
        var expanded = _expanded.Contains(key);
        var toggle = SmallOutline(expanded ? T("▴ Скрыть состав", "▴ Курамын жашыруу", "▴ Hide items", "▴ İçeriği gizle", "▴ Tarkibini yashirish")
            : T($"▾ Состав ({g.Lines.Count})", $"▾ Курамы ({g.Lines.Count})", $"▾ Items ({g.Lines.Count})", $"▾ İçerik ({g.Lines.Count})", $"▾ Tarkibi ({g.Lines.Count})"));
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) =>
        {
            if (!_expanded.Remove(key))
                _expanded.Add(key);
            Render();
        };
        body.Children.Add(toggle);
        if (expanded)
        {
            var rows = g.Lines.Select(l => (l.ProductName.Length > 0 ? l.ProductName : "—", $"{l.Quantity.ToString("0.###", Ru)} {l.Unit}".Trim(),
                l.Quantity > 0 ? MoneyText.Som(l.Amount / l.Quantity) : "—", MoneyText.Som(l.Amount))).ToList();
            body.Children.Add(SectionCards.LinesTable(this, rows, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
                T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")));
        }
        return Card(body);
    }

    // ── Товары ──

    private void RenderProducts()
    {
        if (PartError("products"))
            return;
        if (_products.Count == 0)
        {
            _body.Children.Add(SectionCards.Empty(this, T("Товаров с этим поставщиком нет. Поставщика товару задают в карточке товара («Описание» → «Поставщик») или при приёмке.",
                "Бул жеткирүүчү менен товар жок. Товардын жеткирүүчүсү товардын карточкасында («Сүрөттөмө» → «Жеткирүүчү») же кабыл алууда коюлат.",
                "No products from this supplier. Set a product's supplier in the product card (“Description” → “Supplier”) or when receiving goods.",
                "Bu tedarikçiye ait ürün yok. Ürünün tedarikçisi ürün kartında («Açıklama» → «Tedarikçi») veya mal kabulünde belirlenir.",
                "Bu yetkazib beruvchining mahsulotlari yo'q. Mahsulot yetkazib beruvchisi mahsulot kartasida («Tavsif» → «Yetkazib beruvchi») yoki qabulda belgilanadi.")));
            return;
        }
        if (_products.Count > 8)
        {
            var search = UiKit.Input(this, T("Поиск товара: название или код…", "Товар издөө: аталышы же коду…", "Find a product: name or code…", "Ürün ara: ad veya kod…", "Mahsulot qidirish: nomi yoki kodi…"), 40);
            Avalonia.Automation.AutomationProperties.SetName(search, search.Watermark);
            search.Text = _productQuery;
            search.TextChanged += (_, _) =>
            {
                _productQuery = search.Text ?? "";
                if (_body.Children.Count > 2)
                    _body.Children[2] = ProductsTable();
            };
            _body.Children.Add(search);
        }
        var stock = _products.Sum(p => Math.Max(0, p.Quantity) * p.PurchasePrice);
        var line = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Text = T($"Товаров: {_products.Count} · на складе по закупке на {MoneyText.Som(stock)}", $"Товарлар: {_products.Count} · кампада сатып алуу баасы менен {MoneyText.Som(stock)}",
                $"Products: {_products.Count} · in stock at purchase price {MoneyText.Som(stock)}", $"Ürün: {_products.Count} · alış fiyatıyla stokta {MoneyText.Som(stock)}",
                $"Mahsulotlar: {_products.Count} · omborda xarid narxida {MoneyText.Som(stock)}") };
        Use(line, TextBlock.ForegroundProperty, "BrushText");
        _body.Children.Add(line);
        _body.Children.Add(ProductsTable());
    }

    private Control ProductsTable()
    {
        var q = _productQuery.Trim();
        var shown = _products.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Article.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(300).ToList();
        if (shown.Count == 0)
            return Soft(T("По этому запросу товаров нет.", "Бул сурам боюнча товар жок.", "No products match.", "Eşleşen ürün yok.", "Mos mahsulot yo'q."));
        var rows = shown.Select(p => ((p.Code.Length > 0 ? p.Name + " · " + p.Code : p.Name),
            p.Quantity <= 0 ? T("нет", "жок", "none", "yok", "yo'q") : p.Quantity.ToString("0.###", Ru), MoneyText.Som(p.PurchasePrice), MoneyText.Som(p.Price))).ToList();
        return SectionCards.LinesTable(this, rows, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Остаток", "Калдык", "In stock", "Stok", "Qoldiq"),
            T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"));
    }

    // ── Возвраты ──

    private void RenderReturns()
    {
        if (PartError("returns"))
            return;
        if (_returns.Count == 0)
        {
            _body.Children.Add(SectionCards.Empty(this, T("Возвратов этому поставщику не было. Брак, излишек или просрочку оформляют в разделе «Возвраты поставщикам».",
                "Бул жеткирүүчүгө кайтаруу болгон эмес. Бузук, ашыкча же мөөнөтү өткөн товар «Жеткирүүчүлөргө кайтаруулар» бөлүмүндө таризделет.",
                "No returns to this supplier. Register defects, surplus or expired goods in “Returns to suppliers”.",
                "Bu tedarikçiye iade yok. Kusurlu, fazla veya süresi geçmiş ürün «Tedarikçiye iadeler» bölümünde kaydedilir.",
                "Bu yetkazib beruvchiga qaytarish bo'lmagan. Brak, ortiqcha yoki muddati o'tgan mahsulot «Yetkazib beruvchilarga qaytarishlar» bo'limida rasmiylashtiriladi.")));
            return;
        }
        var line = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Text = T($"Возвратов {_returns.Count} на сумму {MoneyText.Som(_returns.Sum(r => r.Total))}", $"Кайтаруулар {_returns.Count}, суммасы {MoneyText.Som(_returns.Sum(r => r.Total))}",
                $"{_returns.Count} returns worth {MoneyText.Som(_returns.Sum(r => r.Total))}", $"{_returns.Count} iade, toplam {MoneyText.Som(_returns.Sum(r => r.Total))}",
                $"{_returns.Count} ta qaytarish, jami {MoneyText.Som(_returns.Sum(r => r.Total))}") };
        Use(line, TextBlock.ForegroundProperty, "BrushText");
        _body.Children.Add(line);
        foreach (var r in _returns.Take(200))
            _body.Children.Add(ReturnCard(r));
    }

    private Control ReturnCard(SupplierReturnInfo r)
    {
        var key = "r" + r.Id;
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 3 };
        var titleRow = new WrapPanel();
        var title = new TextBlock { Text = r.CreatedAt is { } at ? at.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—", FontSize = 15, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        titleRow.Children.Add(title);
        titleRow.Children.Add(SectionCards.Pill(this, SupplierReturnsWindow.ReasonName(r.Reason)));
        who.Children.Add(titleRow);
        who.Children.Add(Soft(string.Join(" · ", new[] { SupplierReturnsWindow.CompensationName(r.Compensation), r.CreatedBy }.Where(x => x.Length > 0))));
        if (r.Comment.Length > 0)
        {
            var note = Soft("«" + r.Comment + "»");
            note.FontStyle = FontStyle.Italic;
            who.Children.Add(note);
        }
        head.Children.Add(who);
        var sum = new TextBlock { Text = MoneyText.Som(r.Total), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        sum.Classes.Add("num");
        Use(sum, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(sum, 1);
        head.Children.Add(sum);
        body.Children.Add(head);
        if (r.Compensation == "debt_offset")
            body.Children.Add(WarningBox(new[] { T("Сервер пока не уменьшает долг по такому возврату — сверьте остаток долга с поставщиком.",
                "Сервер азырынча мындай кайтаруу боюнча карызды азайтпайт — карыздын калдыгын жеткирүүчү менен салыштырыңыз.",
                "The server doesn't reduce the debt for such a return yet — check the balance with the supplier.",
                "Sunucu bu tür iadede borcu henüz düşürmüyor — bakiyeyi tedarikçiyle doğrulayın.",
                "Server hozircha bunday qaytarish bo'yicha qarzni kamaytirmaydi — qoldiqni yetkazib beruvchi bilan solishtiring.") }));
        if (r.Lines.Count > 0)
        {
            var expanded = _expanded.Contains(key);
            var toggle = SmallOutline(expanded ? T("▴ Скрыть товары", "▴ Товарларды жашыруу", "▴ Hide products", "▴ Ürünleri gizle", "▴ Mahsulotlarni yashirish")
                : T($"▾ Товары ({r.Lines.Count})", $"▾ Товарлар ({r.Lines.Count})", $"▾ Products ({r.Lines.Count})", $"▾ Ürünler ({r.Lines.Count})", $"▾ Mahsulotlar ({r.Lines.Count})"));
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Click += (_, _) =>
            {
                if (!_expanded.Remove(key))
                    _expanded.Add(key);
                Render();
            };
            body.Children.Add(toggle);
            if (expanded)
                body.Children.Add(SectionCards.LinesTable(this, r.Lines.Select(l => (l.ProductName.Length > 0 ? l.ProductName : "—", $"{l.Qty.ToString("0.###", Ru)} {l.Unit}".Trim(),
                    MoneyText.Som(l.PurchasePrice), MoneyText.Som(l.Total))).ToList(), T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
                    T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")));
        }
        return Card(body);
    }

    // ── Оплаты ──

    private void RenderPayments()
    {
        if (PartError("debts"))
            return;
        var rows = _deals.Where(d => d.IsDebt).SelectMany(d => d.Payments.Where(p => p.Kind is "" or "pay").Select(p => (At: p.PaidDate ?? p.CreatedAt, Deal: d.Title,
                What: p.InstallmentNumber > 0 ? T($"взнос №{p.InstallmentNumber}", $"№{p.InstallmentNumber} бөлүк", $"instalment #{p.InstallmentNumber}", $"{p.InstallmentNumber}. taksit", $"{p.InstallmentNumber}-bo'lak") : "",
                Method: MethodName(p.Method), p.Amount)))
            .Concat(_deals.Where(d => d.IsPrepayment && d.Prepayment > 0.005).Select(d => (At: d.CreatedAt, Deal: d.Title,
                What: T("предоплата", "алдын ала төлөм", "prepayment", "ön ödeme", "oldindan to'lov"), Method: "—", Amount: d.Prepayment)))
            .OrderByDescending(x => x.At ?? DateTime.MinValue).ToList();
        if (rows.Count == 0)
        {
            _body.Children.Add(SectionCards.Empty(this, T("Оплат поставщику пока не было. Оплаты по долгам, сделанные на сайте NurCRM, появятся здесь — дата, сумма и способ.",
                "Жеткирүүчүгө азырынча төлөм болгон эмес. NurCRM сайтында жасалган карыз төлөмдөрү бул жерде көрүнөт — күнү, суммасы жана ыкмасы.",
                "No payments to the supplier yet. Debt payments made on the NurCRM website will appear here — date, amount and method.",
                "Tedarikçiye henüz ödeme yapılmadı. NurCRM sitesinde yapılan borç ödemeleri burada görünecek — tarih, tutar ve yöntem.",
                "Yetkazib beruvchiga hali to'lov bo'lmagan. NurCRM saytida qilingan qarz to'lovlari shu yerda ko'rinadi — sana, summa va usul.")));
            return;
        }
        var line = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Text = T($"Оплачено поставщику {MoneyText.Som(rows.Sum(r => r.Amount))} · оплат: {rows.Count}", $"Жеткирүүчүгө төлөндү {MoneyText.Som(rows.Sum(r => r.Amount))} · төлөмдөр: {rows.Count}",
                $"Paid to the supplier {MoneyText.Som(rows.Sum(r => r.Amount))} · payments: {rows.Count}", $"Tedarikçiye ödenen {MoneyText.Som(rows.Sum(r => r.Amount))} · ödeme: {rows.Count}",
                $"Yetkazib beruvchiga to'langan {MoneyText.Som(rows.Sum(r => r.Amount))} · to'lovlar: {rows.Count}") };
        Use(line, TextBlock.ForegroundProperty, "BrushText");
        _body.Children.Add(line);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var r = 0;
        void Cell(string text, int col, bool header, bool right, bool wrap = false, string brush = "BrushText")
        {
            var t = new TextBlock
            {
                Text = text, FontSize = header ? 12 : 13, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(col == 0 ? 0 : 16, header ? 0 : 5, 0, header ? 4 : 5),
            };
            if (!header && col != 1)
                t.Classes.Add("num");
            Use(t, TextBlock.ForegroundProperty, header ? "BrushTextSoft" : brush);
            Grid.SetRow(t, r);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(T("Дата", "Күнү", "Date", "Tarih", "Sana"), 0, true, false);
        Cell(T("Долг · взнос", "Карыз · бөлүк", "Debt · instalment", "Borç · taksit", "Qarz · bo'lak"), 1, true, false);
        Cell(T("Способ", "Ыкма", "Method", "Yöntem", "Usul"), 2, true, false);
        Cell(T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), 3, true, true);
        foreach (var p in rows.Take(500))
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(p.At is { } at ? at.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—", 0, false, false);
            Cell(string.Join(" · ", new[] { p.Deal, p.What }.Where(x => x.Length > 0)), 1, false, false, wrap: true);
            Cell(p.Method, 2, false, false, brush: "BrushTextSoft");
            Cell(MoneyText.Som(p.Amount), 3, false, true);
        }
        var box = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 10), Child = grid };
        Use(box, Border.BackgroundProperty, "BrushPanel");
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        _body.Children.Add(box);
    }

    private static string MethodName(string method) => method switch
    {
        "cash" => T("наличные", "накталай", "cash", "nakit", "naqd"),
        "card" => T("карта", "карта", "card", "kart", "karta"),
        "transfer" or "bank" or "bank_transfer" => T("перевод", "которуу", "transfer", "havale", "o'tkazma"),
        "" => "—",
        _ => method,
    };

    // ------------------------------------------------------------------ «Изменить»

    private async Task EditAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var dialog = new SupplierEditDialog(_svc, _info, _all);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Saved is not { } saved)
            return;
        _info = saved;
        ShowHeader();
        _changed(saved);
        Render();
        PosLogger.Log($"Поставщики: реквизиты поставщика {saved.Id} изменены.", "INFO");
    }

    // ------------------------------------------------------------------ кусочки оформления

    private Border Card(Control child, Thickness? padding = null)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(16, 12), Child = child };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    /// <summary>Предупреждение (только показ): мягкая жёлтая плашка, текст цветом текста.</summary>
    private Border WarningBox(IEnumerable<string> lines)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var l in lines)
        {
            var t = new TextBlock { Text = "⚠ " + l, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Use(t, TextBlock.ForegroundProperty, "BrushText");
            panel.Children.Add(t);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = panel };
        Use(box, Border.BackgroundProperty, "BrushWarningSoft");
        return box;
    }

    private Button SmallOutline(string text)
    {
        var b = new Button
        {
            Content = text, Height = 34, MinHeight = 34, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("sec-outline");
        if (Avalonia.Automation.AutomationProperties.GetName(b) is not { Length: > 0 })
            Avalonia.Automation.AutomationProperties.SetName(b, text.TrimStart('▾', '▴', '←', ' '));
        return b;
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    private Run Red(string text, bool red)
    {
        var run = new Run(text) { FontWeight = FontWeight.Bold };
        if (red)
            run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        return run;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
