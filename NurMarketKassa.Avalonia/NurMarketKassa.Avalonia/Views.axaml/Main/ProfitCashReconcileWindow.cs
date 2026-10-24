using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-01, ТЗ-BE-2026-04 раздел 6 «Что сделает касса» (сервер сделал AN-11 и AN-12): раздел
/// программы владельца «Прибыль и деньги» — три отчёта сервера NurCRM за период:
/// • «Прибыль» (P&amp;L, GET api/main/analytics/pnl/): выручка − себестоимость = валовая прибыль −
///   операционные расходы = операционная прибыль. Закупка товара прибыль не уменьшает, пока товар не продан;
/// • «Движение денег» (Cash Flow, GET api/main/analytics/cashflow/): приход и расход денег по статьям;
/// • «Сверка» (GET api/main/analytics/reconcile/): 7 проверок сервера — сходятся ли отчёты между собой,
///   с разницей и примерами чеков.
/// Окно собрано в коде, как «Телеграм-бот»; цвета — из темы.
/// 2026-10-18, редизайн владельца (этап О3, макет ProfitCash.dc.html): без своего крупного заголовка (название раздела уже в
/// полосе программы владельца), период — сегменты справа (UiKit.Seg), строки отчёта — линиями без заливки, итог «Операционная
/// прибыль» / «Чистое движение денег» — плашкой, красной только при минусе (убыток). Расчёты и запросы — прежние.
/// </summary>
public sealed class ProfitCashReconcileWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _pnl = new() { Spacing = 6 };
    private readonly StackPanel _cash = new() { Spacing = 6 };
    private readonly StackPanel _checks = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private string _period = "month";
    /// <summary>2026-10-22 (зарплата: источник, статьи): расходы периода по названию статьи (salaries/rent/taxes/utilities); null — не получены.</summary>
    private Dictionary<string, double>? _named;

    /// <summary>Строка расхода «по названию статьи»: сумма из расходов (cashflow), если она больше серверной (сервер не опознал статью —
    /// у выплат и изъятий без системного slug там 0,00). Разница вычитается из «Прочих расходов» (итог сервера не меняется).</summary>
    private double Named(JsonElement block, string key, ref double moved, List<string> notes, string title)
    {
        var server = Num(block, key) ?? 0;
        if (_named is null || !_named.TryGetValue(key, out var byName) || byName <= server + 0.005)
            return server;
        moved += byName - server;
        notes.Add($"{title}: " + Tr.T($"по статьям расходов {byName.ToString("N2", Ru)}, сервер даёт {server.ToString("N2", Ru)}",
            $"чыгым беренелери боюнча {byName.ToString("N2", Ru)}, сервер {server.ToString("N2", Ru)} берет",
            $"by expense categories {byName.ToString("N2", Ru)}, the server gives {server.ToString("N2", Ru)}",
            $"gider kalemlerine göre {byName.ToString("N2", Ru)}, sunucu {server.ToString("N2", Ru)} veriyor",
            $"xarajat moddalari bo'yicha {byName.ToString("N2", Ru)}, server {server.ToString("N2", Ru)} beradi"));
        return byName;
    }

    private TextBlock NamedNote(List<string> notes) => Soft(
        Tr.T("Строки взяты по названию статьи расхода (зарплата, аренда, налоги — как в разделе «Зарплата» и при изъятии из кассы). ",
            "Саптар чыгым беренесинин аталышы боюнча алынды (эмгек акы, ижара, салык — «Эмгек акы» бөлүмүндөгүдөй жана кассадан алганда). ",
            "Lines are taken by expense category name (salary, rent, taxes — as in “Salary” and cash withdrawals). ",
            "Satırlar gider kalemi adına göre alındı (maaş, kira, vergi — «Maaş» bölümü ve kasadan çıkışlardaki gibi). ",
            "Qatorlar xarajat moddasi nomi bo'yicha olindi (ish haqi, ijara, soliq — «Ish haqi» bo'limi va kassadan olishdagidek). ")
        + string.Join("; ", notes) + ". "
        + Tr.T("Разница вычтена из «Прочих расходов», итог сервера не менялся.", "Айырма «Башка чыгымдардан» алынды, сервердин жыйынтыгы өзгөргөн жок.",
            "The difference is taken out of “Other expenses”; the server total is unchanged.", "Fark «Diğer giderler»den düşüldü, sunucu toplamı değişmedi.",
            "Farq «Boshqa xarajatlar»dan ayirildi, server jami o'zgarmadi."));
    private CancellationTokenSource? _cts;

    public ProfitCashReconcileWindow()
    {
        Title = Tr.T("Прибыль и деньги", "Пайда жана акча", "Profit & cash", "Kâr ve nakit", "Foyda va pul");
        Width = 1200;
        Height = 820;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        // 2026-10-18, этап О3: шапка — одна строка: слева пояснение «Период: … Цифры — с сервера NurCRM» (переносится), справа
        // период сегментами. Крупный заголовок «Прибыль, движение денег и сверка» убран — он повторял название раздела.
        var root = new Grid { Margin = OwnerSectionLayout.Margin, RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 16, 0);
        var periods = UiKit.WithPeriodLabel(UiKit.Segment(_periods), _periodLabel);
        Grid.SetColumn(periods, 1);
        header.Children.Add(_status);
        header.Children.Add(periods);
        root.Children.Add(header);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*,16,*") };
        // 2026-10-19, этап О10 (аудит: «Прибыль (P&L)» — слово бухгалтера): просто «Прибыль».
        columns.Children.Add(Card(Tr.T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda"), _pnl, 0));
        columns.Children.Add(Card(Tr.T("Движение денег", "Акчанын кыймылы", "Cash flow", "Nakit akışı", "Pul harakati"), _cash, 2));
        columns.Children.Add(Card(Tr.T("Сверка отчётов", "Отчёттарды салыштыруу", "Report reconciliation", "Rapor mutabakatı", "Hisobotlarni solishtirish"), _checks, 4));
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);
        Content = root;

        BuildPeriods();
        Opened += (_, _) => _ = LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    // 2026-10-20, проверка фильтров: общие правила периода (PeriodRange) и подпись дат рядом с сегментами.
    private readonly TextBlock _periodLabel = new();

    private (DateTime From, DateTime To) Range() => _period switch
    {
        "today" => (DateTime.Today, DateTime.Today),
        "week" => (PeriodRange.WeekFrom(DateTime.Today), DateTime.Today),
        "quarter" => (DateTime.Today.AddMonths(-3).AddDays(1), DateTime.Today),
        _ => (PeriodRange.MonthFrom(DateTime.Today), DateTime.Today),
    };

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        void Add(string key, string text)
        {
            // 2026-10-18, этап О3: сегмент; выбранный — тёмно-синий (было: серые кнопки и светло-жёлтая выбранная).
            var b = UiKit.Seg(text, key == _period);
            b.Click += (_, _) =>
            {
                _period = key;
                BuildPeriods();
                _ = LoadAsync();
            };
            _periods.Children.Add(b);
        }

        Add("today", Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"));
        Add("week", Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"));
        Add("month", Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy"));
        Add("quarter", Tr.T("3 месяца", "3 ай", "3 months", "3 ay", "3 oy"));
        var (from, to) = Range();
        _periodLabel.Text = PeriodRange.Label(from, to);
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var (from, to) = Range();
        _status.Text = Tr.T("Загрузка с сервера NurCRM…", "NurCRM серверинен жүктөлүүдө…", "Loading from the NurCRM server…", "NurCRM sunucusundan yükleniyor…", "NurCRM serveridan yuklanmoqda…");
        var query = new Dictionary<string, string>
        {
            ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var api = App.GetRequiredService<NurMarketApiClient>();

        async Task<JsonElement?> Get(string path)
        {
            try
            {
                return await api.RequestAsync(HttpMethod.Get, path, null, query, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Прибыль и деньги: {path} не получен: {ex.Message}", "WARNING");
                return null;
            }
        }

        try
        {
            var pnlTask = Get("api/main/analytics/pnl/");
            var cashTask = Get("api/main/analytics/cashflow/");
            var checkTask = Get("api/main/analytics/reconcile/");
            // 2026-10-06, ТЗ ч.12, п. 3.1 (сервер выложил 05.10): доход без задвоения — одна продажа = один доход,
            // способ оплаты — разбивка; оплата долга — приход денег, а не выручка. Проверено 06.10: доход = выручке сводки.
            query["operations"] = "0";
            var financeTask = Get("api/main/analytics/finance/");
            // 2026-10-22 (статьи изъятия): изъятия из кассы смен по статьям — отчёт pnl делит расходы только на зарплату,
            // аренду, налоги, коммунальные и «прочие»; остальные статьи (транспорт, хознужды, инкассация…) группируем сами.
            var articlesTask = CashArticles.WithdrawalsByArticleAsync(api, from, to, ct);
            // 2026-10-22 (зарплата: источник, статьи): веб считает «Зарплата / Аренда / Налоги» по системной статье (slug) — выплаты и
            // изъятия со статьёй по названию там дают 0,00. Берём расходы периода и группируем по названию статьи сами.
            var namedTask = CashArticles.ExpensesByNamedCategoryAsync(api, from, to, ct);
            var pnl = await pnlTask.ConfigureAwait(true);
            var cash = await cashTask.ConfigureAwait(true);
            var check = await checkTask.ConfigureAwait(true);
            var finance = await financeTask.ConfigureAwait(true);
            _named = await namedTask.ConfigureAwait(true);
            RenderPnl(pnl);
            RenderArticles(await articlesTask.ConfigureAwait(true));
            RenderCash(cash);
            RenderFinance(finance);
            RenderChecks(check);
            _status.Text = Tr.T($"Период: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}. Цифры — с сервера NurCRM, те же, что на сайте.",
                $"Мезгил: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}. Сандар — NurCRM серверинен, сайттагыдай.",
                $"Period: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}. Figures come from the NurCRM server, the same as on the website.",
                $"Dönem: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}. Rakamlar NurCRM sunucusundan, sitedekiyle aynı.",
                $"Davr: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}. Raqamlar NurCRM serveridan, saytdagidek.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RenderPnl(JsonElement? data)
    {
        _pnl.Children.Clear();
        if (data is not { ValueKind: JsonValueKind.Object } d)
        {
            _pnl.Children.Add(Soft(NotAvailable()));
            return;
        }

        _pnl.Children.Add(Row(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Money(d, "revenue"), false));
        // 2026-10-19, этап О10: простые слова и те же названия прибыли, что на Сводке и в Аналитике (ProfitTerms).
        _pnl.Children.Add(Row(Tr.T("Закупочная цена проданного", "Сатылгандын сатып алуу баасы", "Purchase cost of goods sold", "Satılanların alış fiyatı", "Sotilganlarning xarid narxi"), "− " + Money(d, "cogs"), false));
        _pnl.Children.Add(Row(ProfitTerms.Gross,
            Money(d, "gross_profit") + (Num(d, "margin_percent") is { } m ? $"  ·  {m.ToString("0.#", Ru)} %" : ""), true));
        if (d.TryGetProperty("opex", out var opex) && opex.ValueKind == JsonValueKind.Object)
        {
            double moved = 0;
            var notes = new List<string>();
            foreach (var (key, title) in new[]
                     {
                         ("salaries", Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi")),
                         ("rent", Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara")),
                         ("taxes", Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar")),
                         ("utilities", Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal")),
                         ("other", Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar")),
                     })
            {
                // 2026-10-22 (зарплата: источник, статьи): «Прочие» считаются последними — из них вычтено перенесённое в именованные строки.
                var value = key == "other" ? Math.Max(0, (Num(opex, key) ?? 0) - moved) : Named(opex, key, ref moved, notes, title);
                if (value > 0.004)
                    _pnl.Children.Add(Row("   " + title, "− " + value.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm"), false));
            }
            if (notes.Count > 0)
                _pnl.Children.Add(NamedNote(notes));
            // 2026-10-19, этап О10 (аудит: «Прочие расходы» и «Операционные расходы» с одной суммой путают): итог — «Расходы всего».
            _pnl.Children.Add(Row(Tr.T("Расходы всего", "Бардык чыгымдар", "Total expenses", "Toplam giderler", "Jami xarajatlar"), "− " + Money(opex, "total"), true));
        }
        // 2026-10-19, этап О10: «Операционная прибыль» сервера (валовая − расходы) — «Чистая прибыль», как в Аналитике; под итогом —
        // как считается.
        _pnl.Children.Add(Row(ProfitTerms.Net, Money(d, "operating_profit"), true,
            result: Num(d, "operating_profit") ?? 0));
        _pnl.Children.Add(Soft(ProfitTerms.GrossHow + ".\n" + ProfitTerms.NetHow + "."));
        _pnl.Children.Add(Soft(Tr.T("Закупка товара прибыль не уменьшает, пока товар не продан: в прибыль идёт себестоимость проданных строк.",
            "Товар сатылмайынча сатып алуу пайданы азайтпайт: пайдага сатылган саптардын өздүк наркы кирет.",
            "Buying stock does not reduce profit until the goods are sold: profit uses the cost of the lines sold.",
            "Mal satılana kadar alım kârı azaltmaz: kâra satılan satırların maliyeti yansır.",
            "Tovar sotilmaguncha xarid foydani kamaytirmaydi: foydaga sotilgan qatorlar tannarxi kiradi.")));
    }

    /// <summary>2026-10-22 (статьи изъятия): под «Прибылью» — изъятия из кассы смен по статьям за период; инкассация —
    /// отдельной строкой с пометкой «не расход» и пояснением. Нет изъятий или сервер не ответил — блока нет.</summary>
    private void RenderArticles(IReadOnlyList<CashArticles.ArticleTotal>? rows)
    {
        if (rows is not { Count: > 0 })
            return;
        _pnl.Children.Add(Head(Tr.T("Изъятия из кассы по статьям", "Кассадан алынган акча беренелер боюнча", "Cash taken from the till by category",
            "Kalemlere göre kasadan çıkan para", "Kassadan olingan pul moddalar bo'yicha")));
        foreach (var r in rows)
        {
            var title = r.IsCollection
                ? r.Title + Tr.T(" — не расход", " — чыгым эмес", " — not an expense", " — gider değil", " — xarajat emas")
                : r.Title;
            _pnl.Children.Add(Row($"   {title} ({r.Count})", "− " + r.Amount.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm"), false));
        }
        var expense = rows.Where(r => !r.IsCollection).Sum(r => r.Amount);
        _pnl.Children.Add(Row(Tr.T("Изъято на расходы", "Чыгымдарга алынды", "Taken for expenses", "Giderler için alınan", "Xarajatlarga olindi"),
            "− " + expense.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm"), true));
        if (rows.Any(r => r.IsCollection))
            _pnl.Children.Add(Soft(CashArticles.CollectionNote));
    }

    private void RenderCash(JsonElement? data)
    {
        _cash.Children.Clear();
        if (data is not { ValueKind: JsonValueKind.Object } d)
        {
            _cash.Children.Add(Soft(NotAvailable()));
            return;
        }

        if (d.TryGetProperty("inflow", out var inflow) && inflow.ValueKind == JsonValueKind.Object)
        {
            _cash.Children.Add(Head(Tr.T("Приход", "Кириш", "Inflow", "Giriş", "Kirim")));
            foreach (var (key, title) in new[]
                     {
                         ("sales_cash", Tr.T("Продажи наличными", "Накталай сатуу", "Cash sales", "Nakit satışlar", "Naqd sotuvlar")),
                         ("sales_card", Tr.T("Продажи безналом", "Накталай эмес сатуу", "Card / transfer sales", "Kart / havale satışları", "Naqdsiz sotuvlar")),
                         ("debt_repayments", Tr.T("Погашение долгов", "Карыздарды төлөө", "Debt repayments", "Borç ödemeleri", "Qarzlarni to'lash")),
                         ("other_income", Tr.T("Прочие приходы", "Башка кириштер", "Other income", "Diğer girişler", "Boshqa kirimlar")),
                     })
                _cash.Children.Add(Row("   " + title, "+ " + Money(inflow, key), false));
            _cash.Children.Add(Row(Tr.T("Итого приход", "Жалпы кириш", "Total inflow", "Toplam giriş", "Jami kirim"), "+ " + Money(inflow, "total"), false));
        }

        if (d.TryGetProperty("outflow", out var outflow) && outflow.ValueKind == JsonValueKind.Object)
        {
            _cash.Children.Add(Head(Tr.T("Расход", "Чыгым", "Outflow", "Çıkış", "Chiqim")));
            double moved = 0;
            var notes = new List<string>();
            foreach (var (key, title) in new[]
                     {
                         ("suppliers", Tr.T("Поставщикам", "Жеткирүүчүлөргө", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilarga")),
                         ("salaries", Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi")),
                         ("rent", Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara")),
                         ("taxes", Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar")),
                         ("returns", Tr.T("Возвраты покупателям", "Сатып алуучуларга кайтаруу", "Customer refunds", "Müşteri iadeleri", "Xaridorlarga qaytarish")),
                         ("other_expenses", Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar")),
                     })
            {
                // 2026-10-22 (зарплата: источник, статьи): зарплата / аренда / налоги — по названию статьи расхода, «Прочие» — остаток.
                var named = key is "salaries" or "rent" or "taxes";
                var value = key == "other_expenses" ? Math.Max(0, (Num(outflow, key) ?? 0) - moved)
                    : named ? Named(outflow, key, ref moved, notes, title) : Num(outflow, key) ?? 0;
                _cash.Children.Add(Row("   " + title, "− " + value.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm"), false));
            }
            if (notes.Count > 0)
                _cash.Children.Add(NamedNote(notes));
            _cash.Children.Add(Row(Tr.T("Итого расход", "Жалпы чыгым", "Total outflow", "Toplam çıkış", "Jami chiqim"), "− " + Money(outflow, "total"), false));
        }

        _cash.Children.Add(Row(Tr.T("Чистое движение денег", "Акчанын таза кыймылы", "Net cash flow", "Net nakit akışı", "Sof pul harakati"), Money(d, "net"), true,
            result: Num(d, "net") ?? 0));
        // 2026-10-20, аудит программы владельца: «Продажи наличными / безналом» здесь ≠ «Доход по способам» ниже — без пояснения
        // путало. Это два разных отчёта сервера (cashflow — деньги по оплатам, finance — выручка по способу оплаты чека); цифры не меняем.
        _cash.Children.Add(Soft(Tr.T(
            "Наличные и безнал здесь и в «Доходе по способам» считаются по-разному: «Движение денег» — деньги, которые пришли по оплатам (возвраты покупателям — отдельной строкой в расходе, погашения долгов — в приходе); «Доход по способам» — выручка продаж по способу оплаты чека. Поэтому суммы могут не совпадать.",
            "Накталай жана накталай эмес бул жерде жана «Төлөм түрлөрү боюнча кирешеде» ар башкача эсептелет: «Акчанын кыймылы» — төлөмдөр боюнча келген акча (сатып алуучуларга кайтаруу — чыгымда өзүнчө сап, карыз төлөө — кириште); «Төлөм түрлөрү боюнча киреше» — чектин төлөм түрү боюнча сатуу түшүмү. Ошондуктан суммалар дал келбеши мүмкүн.",
            "Cash and non-cash here and in “Income by payment method” are counted differently: “Cash flow” is money received from payments (customer refunds are a separate outflow line, debt repayments are in inflow); “Income by method” is sales revenue by the receipt's payment method. So the amounts may differ.",
            "Nakit ve nakitsiz burada ve «Ödeme yöntemine göre gelir»de farklı hesaplanır: «Nakit akışı» — ödemelerle gelen para (müşteri iadeleri giderde ayrı satır, borç ödemeleri girişte); «Yönteme göre gelir» — fişin ödeme yöntemine göre satış cirosu. Bu yüzden tutarlar tutmayabilir.",
            "Naqd va naqdsiz bu yerda va «To'lov usullari bo'yicha daromad»da turlicha hisoblanadi: «Pul harakati» — to'lovlar bo'yicha kelgan pul (xaridorlarga qaytarish — chiqimda alohida qator, qarz to'lovlari — kirimda); «Usullar bo'yicha daromad» — chekning to'lov usuli bo'yicha sotuv tushumi. Shuning uchun summalar mos kelmasligi mumkin.")));
    }

    /// <summary>Доход от продаж по способам оплаты и оплаты долгов (analytics/finance/) — под «Движением денег».</summary>
    private void RenderFinance(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } d || Num(d, "income_total") is null)
            return;
        static string Method(string key) => key == "offset"
            ? Tr.T("Взаимозачёт", "Өз ара эсептешүү", "Offset", "Mahsup", "O'zaro hisob")
            : ReceiptHistoryService.PaymentLabel(key);

        _cash.Children.Add(Head(Tr.T("Доход от продаж по способам оплаты", "Сатуудан киреше төлөм түрлөрү боюнча", "Sales income by payment method",
            "Ödeme yöntemine göre satış geliri", "To'lov usullari bo'yicha sotuvdan daromad")));
        if (d.TryGetProperty("income_by_method", out var byMethod) && byMethod.ValueKind == JsonValueKind.Object)
            foreach (var m in byMethod.EnumerateObject().OrderByDescending(x => Num(byMethod, x.Name) ?? 0))
                _cash.Children.Add(Row("   " + Method(m.Name), Money(byMethod, m.Name), false));
        var count = (long)Math.Round(Num(d, "sales_count") ?? 0);
        _cash.Children.Add(Row(Tr.T($"Доход от продаж ({count} продаж)", $"Сатуудан киреше ({count} сатуу)", $"Sales income ({count} sales)", $"Satış geliri ({count} satış)", $"Sotuvdan daromad ({count} ta sotuv)"),
            Money(d, "income_total"), true));
        if (d.TryGetProperty("debt_repayments", out var debt) && debt.ValueKind == JsonValueKind.Object && Num(debt, "total") is > 0.004)
        {
            _cash.Children.Add(Row(Tr.T("Оплаты долгов (приход денег, не выручка)", "Карыз төлөмдөрү (акча кириши, түшүм эмес)", "Debt repayments (cash in, not revenue)",
                "Borç ödemeleri (nakit girişi, ciro değil)", "Qarz to'lovlari (pul kirimi, tushum emas)"), "+ " + Money(debt, "total"), false));
            if (debt.TryGetProperty("by_method", out var debtBy) && debtBy.ValueKind == JsonValueKind.Object)
                foreach (var m in debtBy.EnumerateObject())
                    _cash.Children.Add(Row("   " + Method(m.Name), Money(debtBy, m.Name), false));
        }
        _cash.Children.Add(Soft(Tr.T("Одна продажа — один доход: смешанная оплата делится по способам, а не считается дважды.",
            "Бир сатуу — бир киреше: аралаш төлөм түрлөргө бөлүнөт, эки жолу эсептелбейт.",
            "One sale is one income: a mixed payment is split by method, not counted twice.",
            "Bir satış bir gelirdir: karışık ödeme yöntemlere bölünür, iki kez sayılmaz.",
            "Bitta sotuv — bitta daromad: aralash to'lov usullarga bo'linadi, ikki marta hisoblanmaydi.")));
    }

    private void RenderChecks(JsonElement? data)
    {
        _checks.Children.Clear();
        if (data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
        {
            _checks.Children.Add(Soft(NotAvailable()));
            return;
        }

        // 2026-10-20, аудит программы владельца: сервер отдавал ok=false при разнице 0,00 — «⚠ разница 0,00» и «Есть
        // расхождения» пугали зря. Разница меньше копейки и без чеков-примеров — совпадение (цифры сервера не меняем).
        static bool ZeroDiff(JsonElement c) =>
            Math.Abs(Num(c, "diff") ?? 0) < 0.01
            && !(c.TryGetProperty("examples", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0);
        static bool CheckOk(JsonElement c) =>
            (c.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True) || ZeroDiff(c);
        var allOk = (d.TryGetProperty("all_ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    || checks.EnumerateArray().All(CheckOk);
        _checks.Children.Add(Row(allOk
                ? Tr.T("✅ Все отчёты сходятся", "✅ Бардык отчёттор дал келет", "✅ All reports match", "✅ Tüm raporlar tutuyor", "✅ Barcha hisobotlar mos")
                : Tr.T("⚠ Есть расхождения", "⚠ Айырмачылыктар бар", "⚠ There are mismatches", "⚠ Uyuşmazlıklar var", "⚠ Farqlar bor"),
            "", !allOk, check: allOk ? CheckState.Ok : CheckState.Warn));
        foreach (var c in checks.EnumerateArray())
        {
            var good = CheckOk(c);
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var diff = Num(c, "diff") ?? 0;
            var right = good ? "✅" : "⚠ " + Tr.T("разница ", "айырма ", "diff ", "fark ", "farq ") + diff.ToString("N2", Ru);
            if (!good && c.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array && ex.GetArrayLength() > 0)
                right += Tr.T(" · чеки: ", " · чектер: ", " · receipts: ", " · fişler: ", " · cheklar: ")
                         + string.Join(", ", ex.EnumerateArray().Take(8).Select(x => "№" + x.ToString()));
            _checks.Children.Add(Row(name, right, false, stacked: !good, check: good ? CheckState.Ok : CheckState.Warn));
        }
        _checks.Children.Add(Soft(Tr.T("Сверку считает сервер NurCRM. Расхождение — повод написать в поддержку NurCRM с номерами чеков.",
            "Салыштырууну NurCRM сервери эсептейт. Айырмачылык болсо — чектердин номерлери менен NurCRM колдоосуна жазыңыз.",
            "The NurCRM server runs the reconciliation. A mismatch is a reason to contact NurCRM support with the receipt numbers.",
            "Mutabakatı NurCRM sunucusu hesaplar. Uyuşmazlık varsa fiş numaralarıyla NurCRM desteğine yazın.",
            "Solishtirishni NurCRM serveri hisoblaydi. Farq bo'lsa — chek raqamlari bilan NurCRM yordamiga yozing.")));
    }

    private static string NotAvailable() => Tr.T("Сервер не ответил или ещё не поддерживает этот отчёт.", "Сервер жооп берген жок же бул отчётту азырынча колдобойт.",
        "The server did not respond or does not support this report yet.", "Sunucu yanıt vermedi veya bu raporu henüz desteklemiyor.",
        "Server javob bermadi yoki bu hisobotni hali qo'llab-quvvatlamaydi.");

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            }
            : null;

    private static string Money(JsonElement e, string name) =>
        (Num(e, name) ?? 0).ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private Border Card(string header, Control body, int column)
    {
        var head = new TextBlock { Text = header, FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        DockPanel.SetDock(head, Dock.Top);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // 2026-10-19, этап О10: суммы не упираются в полосу прокрутки (было 10).
            Padding = new Thickness(0, 0, 16, 0),
        };
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16),
            Child = new DockPanel { LastChildFill = true, Children = { head, scroll } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Use(card, Border.CornerRadiusProperty, "SettingsCardRadius");
        Grid.SetColumn(card, column);
        return card;
    }

    private TextBlock Head(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 2) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private enum CheckState { None, Ok, Warn }

    /// <summary>Строка отчёта. 2026-10-18, этап О3 (макет ProfitCash): обычная строка — линия снизу без заливки, итог
    /// (<paramref name="highlight"/>) — жирным; <paramref name="result"/> — итог раздела плашкой (минус — красная: убыток);
    /// <paramref name="check"/> — строка сверки на мягкой подложке, расхождение — цветом предупреждения.</summary>
    private Border Row(string left, string right, bool highlight, bool stacked = false, double? result = null, CheckState check = CheckState.None)
    {
        var loss = result is < -0.004;
        // Итог — подпись над суммой: в узкой колонке (экран 1024) крупная сумма рядом рвала «Операционная» по буквам.
        stacked |= result is not null;
        // Вложенная статья («   Зарплата») — отступом, а не пробелами: пробелы при переносе уводили слово на вторую строку.
        var indent = left.StartsWith("   ", StringComparison.Ordinal);
        if (indent)
            left = left.TrimStart();
        var a = new TextBlock { Margin = new Thickness(indent ? 12 : 0, 0, 0, 0), Text = left, FontSize = result is null ? 13.5 : 14.5, FontWeight = highlight ? FontWeight.Bold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        Use(a, TextBlock.ForegroundProperty, "BrushText");
        var b = new TextBlock
        {
            Text = right, FontSize = result is null ? 13.5 : 18, FontWeight = result is null ? FontWeight.SemiBold : FontWeight.Bold,
            TextWrapping = stacked ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };
        b.FontFeatures = TabularNumbers;
        Use(b, TextBlock.ForegroundProperty, loss ? "BrushDanger" : check == CheckState.Warn ? "BrushWarning" : "BrushText");
        Control content;
        if (stacked)
        {
            content = new StackPanel { Spacing = 2, Children = { a, b } };
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(b, 1);
            b.Margin = new Thickness(12, 0, 0, 0);
            grid.Children.Add(a);
            grid.Children.Add(b);
            content = grid;
        }

        var row = new Border { Child = content };
        if (result is not null)
        {
            // Итог раздела — плашкой: минус — убыток (мягкий красный), иначе — нейтральная.
            row.Padding = new Thickness(14, 10);
            row.Margin = new Thickness(0, 6, 0, 2);
            Use(row, Border.CornerRadiusProperty, "SettingsCardRadius");
            Use(row, Border.BackgroundProperty, loss ? "BrushDangerSoft" : "BrushSurfaceSubtle");
            row.BorderThickness = new Thickness(1);
            Use(row, Border.BorderBrushProperty, loss ? "BrushDangerSoft" : "BrushBorder");
        }
        else if (check != CheckState.None)
        {
            row.Padding = new Thickness(12, 8);
            Use(row, Border.CornerRadiusProperty, "SettingsButtonRadius");
            Use(row, Border.BackgroundProperty, check == CheckState.Warn ? "BrushWarningSoft" : "BrushSurfaceSubtle");
        }
        else
        {
            // Обычная строка — линия снизу, как таблица.
            row.Padding = new Thickness(0, 7);
            row.BorderThickness = new Thickness(0, 0, 0, 1);
            Use(row, Border.BorderBrushProperty, "BrushBorder");
        }
        return row;
    }

    private static readonly FontFeatureCollection TabularNumbers = new() { new FontFeature { Tag = "tnum", Value = 1 } };

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
