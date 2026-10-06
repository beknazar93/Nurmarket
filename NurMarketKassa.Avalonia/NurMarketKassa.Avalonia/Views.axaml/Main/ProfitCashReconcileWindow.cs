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
/// </summary>
public sealed class ProfitCashReconcileWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _pnl = new() { Spacing = 6 };
    private readonly StackPanel _cash = new() { Spacing = 6 };
    private readonly StackPanel _checks = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private string _period = "month";
    private CancellationTokenSource? _cts;

    public ProfitCashReconcileWindow()
    {
        Title = Tr.T("Прибыль и деньги", "Пайда жана акча", "Profit & cash", "Kâr ve nakit", "Foyda va pul");
        Width = 1200;
        Height = 820;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new Grid { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        var title = new TextBlock
        {
            Text = Tr.T("Прибыль, движение денег и сверка", "Пайда, акчанын кыймылы жана салыштыруу", "Profit, cash flow and reconciliation",
                "Kâr, nakit akışı ve mutabakat", "Foyda, pul harakati va solishtirish"),
            FontSize = 22,
            FontWeight = FontWeight.Bold,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.Margin = new Thickness(0, 6, 0, 12);
        _periods.Margin = new Thickness(0, 0, 0, 16);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_periods, 2);
        root.Children.Add(title);
        root.Children.Add(_status);
        root.Children.Add(_periods);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*,16,*") };
        columns.Children.Add(Card(Tr.T("Прибыль (P&L)", "Пайда (P&L)", "Profit (P&L)", "Kâr (P&L)", "Foyda (P&L)"), _pnl, 0));
        columns.Children.Add(Card(Tr.T("Движение денег", "Акчанын кыймылы", "Cash flow", "Nakit akışı", "Pul harakati"), _cash, 2));
        columns.Children.Add(Card(Tr.T("Сверка отчётов", "Отчёттарды салыштыруу", "Report reconciliation", "Rapor mutabakatı", "Hisobotlarni solishtirish"), _checks, 4));
        Grid.SetRow(columns, 3);
        root.Children.Add(columns);
        Content = root;

        BuildPeriods();
        Opened += (_, _) => _ = LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private (DateTime From, DateTime To) Range() => _period switch
    {
        "today" => (DateTime.Today, DateTime.Today),
        "week" => (DateTime.Today.AddDays(-6), DateTime.Today),
        "quarter" => (DateTime.Today.AddMonths(-3).AddDays(1), DateTime.Today),
        _ => (new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today),
    };

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        void Add(string key, string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6) };
            if (key == _period)
                Use(b, Button.BackgroundProperty, "BrushAccentSoft");
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
            var pnl = await pnlTask.ConfigureAwait(true);
            var cash = await cashTask.ConfigureAwait(true);
            var check = await checkTask.ConfigureAwait(true);
            var finance = await financeTask.ConfigureAwait(true);
            RenderPnl(pnl);
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
        _pnl.Children.Add(Row(Tr.T("Себестоимость проданного", "Сатылгандын өздүк наркы", "Cost of goods sold", "Satılan malın maliyeti", "Sotilgan tovar tannarxi"), "− " + Money(d, "cogs"), false));
        _pnl.Children.Add(Row(Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"),
            Money(d, "gross_profit") + (Num(d, "margin_percent") is { } m ? $"  ·  {m.ToString("0.#", Ru)} %" : ""), true));
        if (d.TryGetProperty("opex", out var opex) && opex.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, title) in new[]
                     {
                         ("salaries", Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi")),
                         ("rent", Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara")),
                         ("taxes", Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar")),
                         ("utilities", Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal")),
                         ("other", Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar")),
                     })
            {
                if (Num(opex, key) is > 0.004)
                    _pnl.Children.Add(Row("   " + title, "− " + Money(opex, key), false));
            }
            _pnl.Children.Add(Row(Tr.T("Операционные расходы", "Операциялык чыгымдар", "Operating expenses", "Faaliyet giderleri", "Operatsion xarajatlar"), "− " + Money(opex, "total"), false));
        }
        _pnl.Children.Add(Row(Tr.T("Операционная прибыль", "Операциялык пайда", "Operating profit", "Faaliyet kârı", "Operatsion foyda"), Money(d, "operating_profit"), true));
        _pnl.Children.Add(Soft(Tr.T("Закупка товара прибыль не уменьшает, пока товар не продан: в прибыль идёт себестоимость проданных строк.",
            "Товар сатылмайынча сатып алуу пайданы азайтпайт: пайдага сатылган саптардын өздүк наркы кирет.",
            "Buying stock does not reduce profit until the goods are sold: profit uses the cost of the lines sold.",
            "Mal satılana kadar alım kârı azaltmaz: kâra satılan satırların maliyeti yansır.",
            "Tovar sotilmaguncha xarid foydani kamaytirmaydi: foydaga sotilgan qatorlar tannarxi kiradi.")));
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
            foreach (var (key, title) in new[]
                     {
                         ("suppliers", Tr.T("Поставщикам", "Жеткирүүчүлөргө", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilarga")),
                         ("salaries", Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi")),
                         ("rent", Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara")),
                         ("taxes", Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar")),
                         ("returns", Tr.T("Возвраты покупателям", "Сатып алуучуларга кайтаруу", "Customer refunds", "Müşteri iadeleri", "Xaridorlarga qaytarish")),
                         ("other_expenses", Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar")),
                     })
                _cash.Children.Add(Row("   " + title, "− " + Money(outflow, key), false));
            _cash.Children.Add(Row(Tr.T("Итого расход", "Жалпы чыгым", "Total outflow", "Toplam çıkış", "Jami chiqim"), "− " + Money(outflow, "total"), false));
        }

        _cash.Children.Add(Row(Tr.T("Чистое движение денег", "Акчанын таза кыймылы", "Net cash flow", "Net nakit akışı", "Sof pul harakati"), Money(d, "net"), true));
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

        var allOk = d.TryGetProperty("all_ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        _checks.Children.Add(Row(allOk
                ? Tr.T("✅ Все отчёты сходятся", "✅ Бардык отчёттор дал келет", "✅ All reports match", "✅ Tüm raporlar tutuyor", "✅ Barcha hisobotlar mos")
                : Tr.T("⚠ Есть расхождения", "⚠ Айырмачылыктар бар", "⚠ There are mismatches", "⚠ Uyuşmazlıklar var", "⚠ Farqlar bor"),
            "", !allOk));
        foreach (var c in checks.EnumerateArray())
        {
            var good = c.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var diff = Num(c, "diff") ?? 0;
            var right = good ? "✅" : "⚠ " + Tr.T("разница ", "айырма ", "diff ", "fark ", "farq ") + diff.ToString("N2", Ru);
            if (!good && c.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array && ex.GetArrayLength() > 0)
                right += Tr.T(" · чеки: ", " · чектер: ", " · receipts: ", " · fişler: ", " · cheklar: ")
                         + string.Join(", ", ex.EnumerateArray().Take(8).Select(x => "№" + x.ToString()));
            _checks.Children.Add(Row(name, right, !good, stacked: !good));
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
        var head = new TextBlock { Text = header, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        DockPanel.SetDock(head, Dock.Top);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 10, 0),
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            Child = new DockPanel { LastChildFill = true, Children = { head, scroll } },
        };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetColumn(card, column);
        return card;
    }

    private TextBlock Head(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private Border Row(string left, string right, bool highlight, bool stacked = false)
    {
        var a = new TextBlock { Text = left, FontSize = 13, FontWeight = highlight ? FontWeight.Bold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        Use(a, TextBlock.ForegroundProperty, "BrushText");
        var b = new TextBlock { Text = right, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = stacked ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Use(b, TextBlock.ForegroundProperty, "BrushText");
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

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7), BorderThickness = new Thickness(1), Child = content };
        Use(row, Border.BackgroundProperty, highlight ? "BrushAccentSoft" : "BrushPanelSoft");
        Use(row, Border.BorderBrushProperty, highlight ? "BrushAccent" : "BrushBorder");
        return row;
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
