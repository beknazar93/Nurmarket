using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вкладка «Окупаемость»: показатели бизнеса, показатели сферы, окупаемость по участникам, диаграммы, структура вложений.</summary>
public sealed partial class InvestmentsWindow
{
    private const string OwnerKey = "owner";
    /// <summary>Цвет убытка на диаграммах: коралловый красный палитры (ChartPalette.Hex[5]).</summary>
    private static string LossHex => ChartPalette.Hex[5];
    private const int ChartMonths = 12;

    private Control BuildDashboard()
    {
        var page = new StackPanel { Spacing = 16 };
        var costs = StartupCostsStore.Items;
        var meta = StartupCostsStore.Meta;
        var investors = StartupCostsStore.Investors;

        if (costs.Count == 0 && investors.Count == 0)
        {
            page.Children.Add(EmptyState(
                T("Добавьте стартовые расходы — программа покажет, когда магазин окупится. Вложения на открытие: ремонт, оборудование, мебель, лицензии, реклама, залог за аренду. Товар на старт во «Вложено» не входит — он окупается продажами. Если у вас есть инвесторы — добавьте и их.",
                    "Баштапкы чыгымдарды кошуңуз — дүкөн качан кайтарарын программа көрсөтөт. Ачылыштагы салымдар: оңдоо, жабдуу, эмерек, лицензия, жарнама, ижара күрөөсү. Баштапкы товар «Салынды» ичине кирбейт — ал сатуу менен кайтат. Инвесторлоруңуз болсо — аларды да кошуңуз.",
                    "Add startup costs — the program will show when the store pays back. Opening investments: repairs, equipment, furniture, licences, advertising, rent deposit. The first stock is not part of “Invested” — it pays back through sales. If you have investors, add them too.",
                    "Başlangıç giderlerini ekleyin — program mağazanın ne zaman geri döneceğini gösterir. Açılış yatırımları: tadilat, ekipman, mobilya, lisans, reklam, kira depozitosu. İlk mal «Yatırılan»a dahil değil — satışla geri döner. Yatırımcılarınız varsa onları da ekleyin.",
                    "Boshlang'ich xarajatlarni qo'shing — dastur do'kon qachon o'zini oqlashini ko'rsatadi. Ochilishdagi sarmoyalar: ta'mir, uskuna, mebel, litsenziya, reklama, ijara garovi. Dastlabki tovar «Kiritilgan»ga kirmaydi — u sotuv bilan qaytadi. Investorlaringiz bo'lsa — ularni ham qo'shing."),
                T("Добавить вложения", "Салымдарды кошуу", "Add investments", "Yatırım ekle", "Sarmoya qo'shish"), () => GoTo(Tab.Costs)));
            return page;
        }
        if (_loading && _result == null)
        {
            page.Children.Add(Soft(T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…"), 14));
            return page;
        }
        if (_loadError != null)
        {
            var card = Card(new StackPanel { Spacing = 10, Children = { Danger(_loadError, 14) } });
            var retry = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Click += (_, _) =>
            {
                StartupPaybackLoader.ClearCache();
                _ = ReloadAsync();
            };
            ((StackPanel)card.Child!).Children.Add(retry);
            page.Children.Add(card);
            return page;
        }
        if (_result is not { } R)
        {
            page.Children.Add(EmptyState(T("Укажите дату открытия бизнеса на вкладке «Вложения» — без неё нечего считать.", "«Салымдар» өтмөгүндө бизнес ачылган күндү көрсөтүңүз — ансыз эсептей албайбыз.",
                "Set the business opening date on the “Investments” tab — there is nothing to calculate without it.", "«Yatırımlar» sekmesinde açılış tarihini girin — onsuz hesaplanacak bir şey yok.",
                "«Sarmoyalar» bo'limida biznes ochilgan sanani kiriting — usiz hisoblash uchun hech narsa yo'q."), T("Открыть «Вложения»", "«Салымдарды» ачуу", "Open “Investments”", "«Yatırımlar»ı aç", "«Sarmoyalar»ni ochish"), () => GoTo(Tab.Costs)));
            return page;
        }
        var B = R.Business;

        // 2026-11-05 (И-20): даты открытия нет — считаем от самой ранней даты вложения; говорим об этом прямо.
        if (meta.OpenedOn == null && InvestmentAnalysis.AnalysisStart(costs, meta, investors) is { } calcFrom)
        {
            var dateRow = new StackPanel { Spacing = 8 };
            dateRow.Children.Add(Plain(T($"Дата открытия не указана — считаем с {calcFrom:dd.MM.yyyy} (самая ранняя дата вложения).", $"Ачылган күн көрсөтүлгөн эмес — {calcFrom:dd.MM.yyyy} күнүнөн эсептейбиз (эң эрте салым күнү).",
                $"The opening date is not set — counting from {calcFrom:dd.MM.yyyy} (the earliest investment date).", $"Açılış tarihi belirtilmemiş — {calcFrom:dd.MM.yyyy} tarihinden hesaplıyoruz (en erken yatırım tarihi).",
                $"Ochilgan sana ko'rsatilmagan — {calcFrom:dd.MM.yyyy} dan hisoblaymiz (eng erta sarmoya sanasi)."), 13.5));
            var setDate = UiKit.Ghost(this, T("Указать дату", "Күндү көрсөтүү", "Set the date", "Tarihi belirt", "Sanani ko'rsatish"));
            setDate.HorizontalAlignment = HorizontalAlignment.Left;
            setDate.Click += (_, _) => GoTo(Tab.Costs);
            dateRow.Children.Add(setDate);
            page.Children.Add(Card(dateRow, 14));
        }
        var stockMoney = StartupCostsStore.StockSum(costs);
        if (stockMoney > 0m)
            page.Children.Add(Soft(T($"Деньги в товаре на старт: {Money0(stockMoney)} — во «Вложено» не входят: товар окупается продажами (его цена уже вычтена из чистой прибыли как себестоимость).",
                $"Баштапкы товардагы акча: {Money0(stockMoney)} — «Салынды» ичине кирбейт: товар сатуу менен кайтат (анын баасы таза пайдадан өздүк баа катары алынып салынган).",
                $"Money in the opening stock: {Money0(stockMoney)} — not part of “Invested”: stock pays back through sales (its cost is already deducted from net profit as cost of goods).",
                $"Açılış stokundaki para: {Money0(stockMoney)} — «Yatırılan»a dahil değil: mal satışla geri döner (maliyeti net kârdan zaten düşüldü).",
                $"Ochilishdagi tovardagi pul: {Money0(stockMoney)} — «Kiritilgan»ga kirmaydi: tovar sotuv bilan qaytadi (uning narxi sof foydadan tannarx sifatida allaqachon ayirilgan)."), 13));
        if (R.TotalInvested <= 0m)
        {
            page.Children.Add(EmptyState(T("Во «Вложено» пока ничего нет: товар на старт не учитывается. Добавьте ремонт, оборудование, лицензии, рекламу, залог за аренду или инвестора.",
                    "«Салынды» ичинде азырынча эч нерсе жок: баштапкы товар эсепке алынбайт. Оңдоо, жабдуу, лицензия, жарнама, ижара күрөөсүн же инвесторду кошуңуз.",
                    "“Invested” is empty so far: the opening stock is not counted. Add repairs, equipment, licences, advertising, a rent deposit or an investor.",
                    "«Yatırılan» henüz boş: açılış stoku sayılmaz. Tadilat, ekipman, lisans, reklam, kira depozitosu veya yatırımcı ekleyin.",
                    "«Kiritilgan» hozircha bo'sh: ochilishdagi tovar hisobga olinmaydi. Ta'mir, uskuna, litsenziya, reklama, ijara garovi yoki investor qo'shing."),
                T("Открыть «Вложения»", "«Салымдарды» ачуу", "Open “Investments”", "«Yatırımlar»ı aç", "«Sarmoyalar»ni ochish"), () => GoTo(Tab.Costs)));
            return page;
        }

        // Панель: прогноз по средней за N месяцев и план окупаемости.
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        var fcRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 20, 8) };
        fcRow.Children.Add(new TextBlock { Text = T("Прогноз по средней за", "Орточо боюнча божомол", "Forecast on the average of", "Ortalamaya göre tahmin", "O'rtacha bo'yicha prognoz"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        Use((TextBlock)fcRow.Children[0], TextBlock.ForegroundProperty, "BrushTextSoft");
        var fcSeg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var n in new[] { 3, 6, 12 })
        {
            var b = UiKit.Seg(n + " " + T("мес", "ай", "mo", "ay", "oy"), n == _forecastMonths);
            b.Click += (_, _) =>
            {
                _forecastMonths = n;
                Recompute();
                RenderTab();
            };
            fcSeg.Children.Add(b);
        }
        fcRow.Children.Add(UiKit.Segment(fcSeg));
        bar.Children.Add(fcRow);
        var plan = NumBox(meta.PlanMonths, _ => { }, "18", 90);
        plan.LostFocus += (_, _) =>
        {
            var parse = CheckoutValidation.TryParseAmount(plan.Text, out var v);
            var months = parse == CheckoutValidation.AmountParse.Ok ? (int)Math.Clamp(Math.Round(v), 0, 600) : 0;
            if (parse is CheckoutValidation.AmountParse.Ok or CheckoutValidation.AmountParse.Empty && months != meta.PlanMonths)
                _status.Text = StartupCostsStore.SetMeta(meta.OpenedOn, meta.Amortize, meta.AmortMonths, months) ?? _status.Text;
        };
        var planRow = Labeled(this, T("План окупаемости, мес", "Кайтарым планы, ай", "Payback plan, months", "Geri dönüş planı, ay", "O'zini oqlash rejasi, oy"), plan);
        planRow.Margin = new Thickness(0, 0, 0, 8);
        bar.Children.Add(planRow);
        page.Children.Add(bar);

        // Плитки.
        var tiles = new KpiTileGrid { MinTileWidth = 220 };
        tiles.Children.Add(UiKit.Kpi(T("Вложено всего", "Жалпы салым", "Total invested", "Toplam yatırım", "Jami sarmoya"), Money0(R.TotalInvested)));
        tiles.Children.Add(UiKit.Kpi(T("Вернулось (накопленная чистая прибыль)", "Кайтты (топтолгон таза пайда)", "Returned (cumulative net profit)", "Geri dönen (birikmiş net kâr)", "Qaytdi (jamlangan sof foyda)"),
            Money0(B.Returned), danger: B.Returned < 0m));
        tiles.Children.Add(UiKit.Kpi(T("Осталось окупить", "Кайтарууга калды", "Left to pay back", "Geri dönecek kalan", "Oqlash uchun qoldi"), Money0(B.Remaining)));
        tiles.Children.Add(UiKit.Kpi(T("Окупаемость вложений", "Салымдардын кайтарымы", "Payback of the investment", "Yatırımın geri dönüşü", "Sarmoyaning oqlanishi"), B.PercentBack.ToString("0.#", Ru) + " %", danger: B.PercentBack < 0m));
        tiles.Children.Add(UiKit.Kpi(T("Срок окупаемости", "Кайтарым мөөнөтү", "Payback period", "Geri dönüş süresi", "Oqlanish muddati"), PaybackTerm(R)));
        page.Children.Add(tiles);

        var notes = new StackPanel { Spacing = 4 };
        if (B.AvgMonthlyNet is { } avg)
            notes.Children.Add(Soft(T($"Среднемесячная чистая прибыль ({(B.AvgMonthsUsed > 0 ? B.AvgMonthsUsed + " " + "полн. мес." : "по дням с открытия")}): {Money0(avg)}.",
                $"Орточо айлык таза пайда ({(B.AvgMonthsUsed > 0 ? B.AvgMonthsUsed + " толук ай" : "ачылгандан бери күндөр боюнча")}): {Money0(avg)}.",
                $"Average monthly net profit ({(B.AvgMonthsUsed > 0 ? B.AvgMonthsUsed + " full months" : "per day since opening")}): {Money0(avg)}.",
                $"Aylık ortalama net kâr ({(B.AvgMonthsUsed > 0 ? B.AvgMonthsUsed + " tam ay" : "açılıştan beri günlük")}): {Money0(avg)}.",
                $"O'rtacha oylik sof foyda ({(B.AvgMonthsUsed > 0 ? B.AvgMonthsUsed + " to'liq oy" : "ochilgandan beri kunlar bo'yicha")}): {Money0(avg)}.")));
        else
            notes.Children.Add(Soft(T("Для прогноза срока окупаемости пока мало данных: нужен хотя бы один полный месяц или 14 дней работы.", "Кайтарым мөөнөтүн божомолдоо үчүн маалымат аз: жок дегенде бир толук ай же 14 күн иштөө керек.",
                "Not enough data for a payback forecast yet: at least one full month or 14 days of work is needed.", "Geri dönüş tahmini için henüz yeterli veri yok: en az bir tam ay veya 14 gün çalışma gerekir.", "O'zini oqlash prognozi uchun ma'lumot hali kam: kamida bitta to'liq oy yoki 14 kun ish kerak.")));
        if (B.Status == PaybackStatus.InProgress && B.AvgMonthlyNet is <= 0m)
            notes.Children.Add(Danger(T("При нынешней прибыли вложения не окупятся: средняя чистая прибыль не положительная.", "Азыркы пайда менен салымдар кайтпайт: орточо таза пайда оң эмес.",
                "At the current profit the investment will not pay back: the average net profit is not positive.", "Mevcut kârla yatırım geri dönmez: ortalama net kâr pozitif değil.", "Hozirgi foyda bilan sarmoya oqlanmaydi: o'rtacha sof foyda musbat emas.")));
        if (meta.PlanMonths > 0 && R.AheadOfPlan is null && R.AvgNet is null && B.Status != PaybackStatus.PaidBack)
            notes.Children.Add(Soft(T($"План: {meta.PlanMonths} мес. Для сравнения с планом пока мало данных.", $"План: {meta.PlanMonths} ай. Планга салыштыруу үчүн азырынча маалымат аз.", $"Plan: {meta.PlanMonths} months. Not enough data to compare with the plan yet.",
                $"Plan: {meta.PlanMonths} ay. Plana kıyaslamak için henüz veri az.", $"Reja: {meta.PlanMonths} oy. Rejaga solishtirish uchun ma'lumot hali kam.")));
        else if (meta.PlanMonths > 0 && R.AheadOfPlan is null && B.Status == PaybackStatus.PaidBack)
            notes.Children.Add(Soft(T($"План: {meta.PlanMonths} мес. Бизнес уже окупился.", $"План: {meta.PlanMonths} ай. Бизнес мурунтан эле кайтарды.", $"Plan: {meta.PlanMonths} months. The business has already paid back.",
                $"Plan: {meta.PlanMonths} ay. İşletme zaten geri döndü.", $"Reja: {meta.PlanMonths} oy. Biznes allaqachon oqlangan.")));
        else if (meta.PlanMonths > 0)
            notes.Children.Add(Soft(R.AheadOfPlan is { } ahead
                ? ahead > 0 ? T($"План: {meta.PlanMonths} мес. Окупаемость по прогнозу на {ahead} мес. раньше плана.", $"План: {meta.PlanMonths} ай. Божомол боюнча планга караганда {ahead} ай эрте.", $"Plan: {meta.PlanMonths} months. The forecast is {ahead} months ahead of plan.", $"Plan: {meta.PlanMonths} ay. Tahmine göre plandan {ahead} ay önce.", $"Reja: {meta.PlanMonths} oy. Prognoz bo'yicha rejadan {ahead} oy oldin.")
                : ahead < 0 ? T($"План: {meta.PlanMonths} мес. Окупаемость по прогнозу на {-ahead} мес. позже плана.", $"План: {meta.PlanMonths} ай. Божомол боюнча планга караганда {-ahead} ай кеч.", $"Plan: {meta.PlanMonths} months. The forecast is {-ahead} months behind plan.", $"Plan: {meta.PlanMonths} ay. Tahmine göre plandan {-ahead} ay sonra.", $"Reja: {meta.PlanMonths} oy. Prognoz bo'yicha rejadan {-ahead} oy keyin.")
                : T($"План: {meta.PlanMonths} мес. Окупаемость по прогнозу точно по плану.", $"План: {meta.PlanMonths} ай. Божомол боюнча так планга ылайык.", $"Plan: {meta.PlanMonths} months. The forecast is exactly on plan.", $"Plan: {meta.PlanMonths} ay. Tahmin tam planda.", $"Reja: {meta.PlanMonths} oy. Prognoz aynan reja bo'yicha.")
                : T($"План: {meta.PlanMonths} мес. При текущей прибыли окупаемость в прогноз не попадает.", $"План: {meta.PlanMonths} ай. Азыркы пайда менен божомолго кирбейт.", $"Plan: {meta.PlanMonths} months. At the current profit payback is not in the forecast.", $"Plan: {meta.PlanMonths} ay. Mevcut kârla tahminde geri dönüş yok.", $"Reja: {meta.PlanMonths} oy. Hozirgi foyda bilan prognozda o'zini oqlash yo'q.")));
        if (R.StockCredit > 0m)
            notes.Children.Add(Soft(T($"В окупаемость засчитан товарный остаток: {Money0(R.StockCredit)} (по закупке × ликвидность).", $"Кайтарымга товар калдыгы эсептелди: {Money0(R.StockCredit)} (сатып алуу баасы × ликвидүүлүк).",
                $"Stock counted towards payback: {Money0(R.StockCredit)} (at cost × liquidity).", $"Geri dönüşe mal stoku sayıldı: {Money0(R.StockCredit)} (alış × likidite).", $"Oqlanishga tovar qoldig'i hisoblandi: {Money0(R.StockCredit)} (xarid × likvidlik).")));
        if (meta.Amortize && R.LastMonth is { } lm)
        {
            // 2026-11-05 (И-06): «последний месяц» - последний полный (раньше брался неполный текущий: «за ноябрь списано 547» вместо октября 5 657); подпись показывает период.
            var amort = StartupPayback.AmortizationFor(R.TotalInvested, meta.OpenedOn ?? B.Rows.FirstOrDefault()?.Month ?? lm.From, meta.AmortMonths, lm.From, lm.To);
            var lmName = LastMonthLabel(lm);
            notes.Children.Add(Soft(T($"Амортизация ({meta.AmortMonths} мес.): за {lmName} списано {Money0(amort)}; чистая прибыль с учётом амортизации — {Money0(NetProfitCalculator.AfterAmortization(lm.Net, amort))}.",
                $"Амортизация ({meta.AmortMonths} ай): {lmName} үчүн {Money0(amort)} эсептен чыкты; амортизацияны эске алганда таза пайда — {Money0(NetProfitCalculator.AfterAmortization(lm.Net, amort))}.",
                $"Amortization ({meta.AmortMonths} months): {Money0(amort)} written off for {lmName}; net profit after amortization — {Money0(NetProfitCalculator.AfterAmortization(lm.Net, amort))}.",
                $"Amortisman ({meta.AmortMonths} ay): {lmName} için {Money0(amort)} yazıldı; amortisman sonrası net kâr — {Money0(NetProfitCalculator.AfterAmortization(lm.Net, amort))}.",
                $"Amortizatsiya ({meta.AmortMonths} oy): {lmName} uchun {Money0(amort)} hisobdan chiqdi; amortizatsiyadan keyingi sof foyda — {Money0(NetProfitCalculator.AfterAmortization(lm.Net, amort))}.")));
        }
        page.Children.Add(notes);

        // Крупный процент.
        var big = new StackPanel { Spacing = 10 };
        big.Children.Add(Soft(T("Бизнес окупился на", "Бизнес кайтарды", "The business has paid back", "İşletme geri döndü", "Biznes oqlandi"), 14));
        var pct = new TextBlock { Text = Math.Clamp(B.PercentBack, 0m, 100m).ToString("0", Ru) + "%", FontSize = 44, FontWeight = FontWeight.ExtraBold };
        Use(pct, TextBlock.ForegroundProperty, "BrushText");
        big.Children.Add(pct);
        big.Children.Add(Bar((double)B.PercentBack, ChartPalette.ColorFor("business"), 16));
        big.Children.Add(Plain(T($"{Money0(B.Returned)} из {Money0(R.TotalInvested)}", $"{Money0(R.TotalInvested)} ичинен {Money0(B.Returned)}", $"{Money0(B.Returned)} of {Money0(R.TotalInvested)}",
            $"{Money0(R.TotalInvested)} içinden {Money0(B.Returned)}", $"{Money0(R.TotalInvested)} dan {Money0(B.Returned)}"), 14, true));
        big.Children.Add(Soft(T("Старт: ", "Башталышы: ", "Start: ", "Başlangıç: ", "Boshlanishi: ") + (R.Months.Count > 0 ? MonthName(R.Months[0].From) : "—")
            + (B.FirstInvestmentDate is { } fiDate && R.Months.Count > 0 && fiDate.Date > R.Months[0].From.Date ? " · " + T("вложения с ", "салымдар ", "investments from ", "yatırımlar ", "sarmoyalar ") + fiDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + T("", " баштап", "", " itibaren", " dan") : "")
            + "   ·   " + (B.Status == PaybackStatus.PaidBack
                ? B.NeedsStockSale ? T("окупится при распродаже остатка", "калдык сатылганда кайтат", "pays back when the stock is sold", "stok satılınca geri döner", "qoldiq sotilganda oqlanadi")
                : B.PaidBackMonth is { } pm ? T("окупилось: ", "кайтты: ", "paid back: ", "geri döndü: ", "oqlandi: ") + MonthName(pm) : T("окупилось", "кайтты", "paid back", "geri döndü", "oqlandi")
                : NotPayingYet(R) ? NotPayingYetText() + T(" — чистая прибыль за последний месяц отрицательная", " — акыркы айдагы таза пайда терс", " — net profit for the last month is negative", " — son ayın net kârı negatif", " — oxirgi oydagi sof foyda manfiy")
                : R.BusinessPaybackMonth is { } bpm ? T("полная окупаемость по прогнозу: ", "божомол боюнча толук кайтарым: ", "full payback forecast: ", "tahmini tam geri dönüş: ", "to'liq oqlanish prognozi: ") + MonthName(bpm)
                : R.AvgNet == null ? NoDataText()
                : T("при текущем темпе не окупится", "азыркы темп менен кайтпайт", "will not pay back at the current pace", "mevcut hızla geri dönmez", "hozirgi sur'atda oqlanmaydi")), 13));
        if (R.Months.Count == 0)
            big.Children.Add(Soft(T("Окупаемость начнётся с даты открытия: полных дней работы пока нет.", "Кайтарым ачылган күндөн башталат: иштеген күндөр азырынча жок.", "Payback starts from the opening date: there are no days of work yet.",
                "Geri dönüş açılış tarihinden başlar: henüz çalışma günü yok.", "O'zini oqlash ochilgan sanadan boshlanadi: ish kunlari hali yo'q."), 12));
        page.Children.Add(Card(big, 22));

        // Показатели сферы.
        page.Children.Add(SphereMetricsCard(R));

        // Окупаемость по участникам.
        if (InvestmentAnalysis.ShareWarning(R) is { } shareWarn) // 2026-11-05 (И-08): сумма долей инвесторов > 100 %
            page.Children.Add(Card(Danger("⚠ " + shareWarn, 13.5), 14));
        page.Children.Add(Heading(T("Окупаемость по участникам", "Катышуучулар боюнча кайтарым", "Payback by participant", "Katılımcılara göre geri dönüş", "Ishtirokchilar bo'yicha oqlanish"), 18));
        var levels = new KpiTileGrid { MinTileWidth = 300, Spacing = 12 };
        levels.Children.Add(Participant(T("Бизнес в целом", "Бизнес жалпысынан", "The business as a whole", "İşletme bütünü", "Butun biznes"), T("все учтённые вложения", "эсептелген бардык салымдар", "all counted investments", "sayılan tüm yatırımlar", "hisoblangan barcha sarmoyalar"),
            "business", R.TotalInvested, B.Returned, B.PercentBack, T("Чистая прибыль за последний месяц", "Акыркы айдагы таза пайда", "Net profit last month", "Son ayın net kârı", "Oxirgi oydagi sof foyda") + LastMonthSuffix(R), R.LastMonth?.Net ?? 0m,
            PaybackTerm(R)));
        foreach (var inv in R.Investors)
            levels.Children.Add(Participant(inv.Investor.Name, T("инвестор · ", "инвестор · ", "investor · ", "yatırımcı · ", "investor · ") + InvestmentText.StageName(inv.Investor.Terms, inv.StageNow).ToLowerInvariant(),
                "investor:" + inv.Investor.Id, inv.Invested, inv.Got, inv.Percent, T("Выплата за последний месяц", "Акыркы айдагы төлөм", "Payout last month", "Son ay ödemesi", "Oxirgi oydagi to'lov") + LastMonthSuffix(R), inv.LastPay,
                inv.Got >= inv.Invested - 0.005m ? T("вернул вложения", "салымын кайтарды", "got the investment back", "yatırımını geri aldı", "sarmoyasini qaytarib oldi")
                    : R.AvgNet == null ? NoDataText()
                    : NotPayingYet(R) ? NotPayingYetText()
                    : inv.PaybackMonths is not null && inv.PaybackDate is { } pd ? FromNowText(pd) + " · " + pd.ToString("MM.yyyy", CultureInfo.InvariantCulture)
                    : T("не вернёт при текущей прибыли", "азыркы пайда менен кайтарбайт", "will not get it back at the current profit", "mevcut kârla geri alamaz", "hozirgi foyda bilan qaytarib olmaydi")));
        if (R.Owner.Invested > 0m)
            levels.Children.Add(Participant(T("Владелец", "Ээси", "Owner", "Sahibi", "Egasi"), T("собственные средства", "жеке каражаттар", "own funds", "öz kaynaklar", "o'z mablag'lari"), OwnerKey, R.Owner.Invested, R.Owner.Got, R.Owner.Percent,
                T("Осталось за последний месяц", "Акыркы айда калды", "Left last month", "Son ay kalan", "Oxirgi oy qolgan") + LastMonthSuffix(R), R.Owner.LastMonthly,
                R.Owner.Got >= R.Owner.Invested - 0.005m ? T("вернул вложения", "салымын кайтарды", "got the investment back", "yatırımını geri aldı", "sarmoyasini qaytarib oldi")
                    : R.AvgNet == null ? NoDataText()
                    : NotPayingYet(R) ? NotPayingYetText()
                    : R.Owner.PaybackMonths is not null && R.Owner.PaybackDate is { } od ? FromNowText(od) + " · " + od.ToString("MM.yyyy", CultureInfo.InvariantCulture)
                    : T("не вернёт при текущей прибыли", "азыркы пайда менен кайтарбайт", "will not get it back at the current profit", "mevcut kârla geri alamaz", "hozirgi foyda bilan qaytarib olmaydi")));
        page.Children.Add(levels);

        // Диаграммы.
        page.Children.Add(MonthlyChart(R));
        page.Children.Add(CumulativeChart(R));
        page.Children.Add(StructureCard(R, costs));
        return page;
    }

    /// <summary>2026-11-05 (И-02): «мало данных для прогноза» - одна и та же фраза везде, где нет единой средней прибыли.</summary>
    private static string NoDataText() => T("мало данных для прогноза", "божомол үчүн маалымат аз", "not enough data for a forecast", "tahmin için veri az", "prognoz uchun ma'lumot kam");

    /// <summary>2026-11-05 (И-02): ОДИН срок окупаемости бизнеса для плитки и карточки «Бизнес в целом» (считает StartupPayback.Compute).</summary>
    private static string PaybackTerm(InvestmentResult R)
    {
        var B = R.Business;
        if (B.Status == PaybackStatus.PaidBack)
            return B.NeedsStockSale ? T("окупится при распродаже остатка", "калдык сатылганда кайтат", "pays back when the stock is sold", "stok satılınca geri döner", "qoldiq sotilganda oqlanadi")
                : T("окупился", "кайтты", "paid back", "geri döndü", "oqlandi");
        if (R.AvgNet == null)
            return NoDataText();
        if (NotPayingYet(R))
            return NotPayingYetText();
        if (B.MonthsToPayback is { } mtp && B.ForecastDate is { } fd)
            // 2026-11-05 (Н-2): меньше месяца — «меньше месяца», а не «0 мес.» (и не «через 1 мес.» рядом на том же экране).
            return (mtp < 1m ? LessThanMonthText() : MonthsText(mtp)) + " · " + fd.ToString("MM.yyyy", CultureInfo.InvariantCulture);
        return T("не окупится", "кайтпайт", "will not pay back", "geri dönmez", "oqlanmaydi");
    }

    /// <summary>2026-11-05 (аудит №4, Д4-28): чистая прибыль последнего полного месяца отрицательная (или средняя не положительная) — «меньше месяца» и
    /// «окупилось 1 %» рядом с минусом противоречат друг другу. Вместо срока и даты — явное состояние «ещё не окупается».</summary>
    private static bool NotPayingYet(InvestmentResult R) =>
        R.Business.Status != PaybackStatus.PaidBack && (R.LastMonth is { Net: < 0m } || R.Business.AvgMonthlyNet is <= 0m);

    private static string NotPayingYetText() => T("ещё не окупается", "азырынча кайтпайт", "not paying back yet", "henüz geri dönmüyor", "hali oqlanmayapti");

    private static string LessThanMonthText() => T("меньше месяца", "бир айдан аз", "less than a month", "bir aydan az", "bir oydan kam");

    /// <summary>2026-11-05 (И-02): «через N мес.» (дальше идёт « · MM.yyyy») — N считается от СЕГОДНЯ (как срок бизнеса), а не от открытия или даты вложения.</summary>
    private static string FromNowText(DateTime month)
    {
        var n = (month.Year - DateTime.Today.Year) * 12 + month.Month - DateTime.Today.Month;
        if (n < 1)
            return LessThanMonthText(); // 2026-11-05 (Н-2): тот же месяц — одна и та же фраза на плитке и на карточке владельца
        return T($"через {n} мес.", $"{n} айдан кийин", $"in {n} months", $"{n} ay sonra", $"{n} oydan keyin");
    }

    /// <summary>Подпись месяца для карточек «за последний месяц»: «октябрь 2026»; неполный - с периодом.</summary>
    private static string LastMonthLabel(PaybackMonth m) => MonthName(m.From)
        + (m.IsFull ? "" : T(" (неполный: ", " (толук эмес: ", " (partial: ", " (eksik: ", " (to'liq emas: ") + m.From.ToString("dd.MM", CultureInfo.InvariantCulture) + "–" + m.To.ToString("dd.MM", CultureInfo.InvariantCulture) + ")");

    private static string LastMonthSuffix(InvestmentResult R) => R.LastMonth is { } m ? " (" + LastMonthLabel(m) + ")" : "";

    private Control Participant(string title, string who, string colorKey, decimal invested, decimal got, decimal pct, string monthlyLabel, decimal monthly, string forecast)
    {
        var color = ColorOf(colorKey);
        var stack = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), Background = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var names = new StackPanel { Children = { Heading(title, 15), Soft(who, 12) } };
        Grid.SetColumn(names, 1);
        var p = Heading(pct.ToString("0", Ru) + "%", 20);
        p.Margin = new Thickness(10, 0, 0, 0);
        p.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(p, 2);
        head.Children.Add(dot);
        head.Children.Add(names);
        head.Children.Add(p);
        stack.Children.Add(head);
        stack.Children.Add(Bar((double)pct, color));
        stack.Children.Add(Pair(T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan"), Money0(invested)));
        stack.Children.Add(Pair(T("Вернулось", "Кайтты", "Returned", "Geri dönen", "Qaytdi"), Money0(got), danger: got < 0m));
        stack.Children.Add(Pair(monthlyLabel, Money0(monthly), danger: monthly < 0m));
        stack.Children.Add(Pair(T("Окупаемость", "Кайтарым", "Payback", "Geri dönüş", "Oqlanish"), forecast));
        return Card(stack);
    }

    /// <summary>Показатели сферы — только из реального отчёта «Прибыль» за последний месяц (выручка, себестоимость, расходы).</summary>
    private Control SphereMetricsCard(InvestmentResult R)
    {
        var sphere = MarketSpheres.Profile;
        var pick = R.Months.LastOrDefault(m => m.IsFull && m.Detail != null) ?? R.Months.LastOrDefault(m => m.Detail != null);
        var stack = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Heading(T("Показатели сферы · ", "Тармактын көрсөткүчтөрү · ", "Sphere indicators · ", "Sektör göstergeleri · ", "Soha ko'rsatkichlari · ") + sphere.ShortTitle.Get(), 16));
        if (pick != null)
        {
            var when = Soft(T("за ", "", "for ", "", "") + MonthName(pick.From) + (pick.IsFull ? "" : T(" (неполный: ", " (толук эмес: ", " (partial: ", " (eksik: ", " (to'liq emas: ") + pick.From.ToString("dd.MM") + "–" + pick.To.ToString("dd.MM") + ")"), 12);
            // 2026-10-29 (приёмка 1.17.64, В-3): месяц считается с даты открытия/вложения, а не с 1-го числа — подпись «за октябрь (неполный)» путала с выручкой за весь октябрь в других отчётах; теперь виден реальный период.
            when.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(when, 1);
            head.Children.Add(when);
        }
        stack.Children.Add(head);
        if (pick?.Detail is not { } p || p.Revenue <= 0m)
        {
            stack.Children.Add(Soft(T("За последний месяц нет выручки — показатели сферы появятся после продаж.", "Акыркы айда түшүм жок — тармак көрсөткүчтөрү сатуулардан кийин чыгат.", "No revenue last month — sphere indicators will appear after sales.",
                "Son ayda ciro yok — sektör göstergeleri satıştan sonra görünecek.", "Oxirgi oyda tushum yo'q — soha ko'rsatkichlari sotuvdan keyin paydo bo'ladi.")));
            return Card(stack);
        }
        var services = sphere.BaseBehavior == MarketSpheres.Services;
        var cogsShare = p.Cogs / p.Revenue * 100m;
        var grossShare = p.Gross / p.Revenue * 100m;
        var be = grossShare > 0m ? p.Expenses / (grossShare / 100m) : (decimal?)null;
        var safety = be is { } b ? (p.Revenue - b) / p.Revenue * 100m : (decimal?)null;
        var tiles = new KpiTileGrid { MinTileWidth = 200, Spacing = 10 };
        tiles.Children.Add(Metric(T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Money0(p.Revenue), T("продажи за месяц по отчёту сервера", "айдагы сатуулар (сервер отчету)", "the month's sales from the server report", "sunucu raporuna göre aylık satış", "serverdagi hisobot bo'yicha oylik sotuv")));
        tiles.Children.Add(Metric(services ? T("Себестоимость услуг", "Кызматтардын өздүк баасы", "Cost of services", "Hizmet maliyeti", "Xizmatlar tannarxi") : T("Себестоимость", "Өздүк баасы", "Cost of goods", "Maliyet", "Tannarx"),
            cogsShare.ToString("0.#", Ru) + " %", T("доля закупочной цены проданного в выручке", "сатылгандын сатып алуу баасынын түшүмдөгү үлүшү", "share of the purchase cost of goods sold in revenue", "satılanların alış fiyatının cirodaki payı", "sotilganning xarid narxining tushumdagi ulushi")));
        tiles.Children.Add(Metric(services ? T("Валовая маржа", "Дүң маржа", "Gross margin", "Brüt marj", "Yalpi marja") : T("Наценка", "Кошумча баа", "Markup", "Kâr payı", "Ustama"),
            (services || p.Cogs <= 0m ? grossShare : p.Gross / p.Cogs * 100m).ToString("0.#", Ru) + " %",
            services || p.Cogs <= 0m ? T("валовая прибыль от выручки", "дүң пайда түшүмдөн", "gross profit of revenue", "cirodan brüt kâr", "tushumdan yalpi foyda") : T("валовая прибыль от закупочной цены", "дүң пайда сатып алуу баасынан", "gross profit over purchase cost", "alış fiyatı üzerinden brüt kâr", "xarid narxiga nisbatan yalpi foyda")));
        tiles.Children.Add(Metric(T("Точка безубыточности", "Зыянсыздык чекити", "Break-even point", "Başabaş noktası", "Zararsizlik nuqtasi"), be is { } bev ? Money0(bev) : "—",
            T("столько выручки в месяц нужно, чтобы выйти в ноль", "нөлгө чыгуу үчүн айына ушунча түшүм керек", "monthly revenue needed to break even", "sıfıra çıkmak için gereken aylık ciro", "nolga chiqish uchun oyiga shuncha tushum kerak")));
        tiles.Children.Add(Metric(T("Запас прочности", "Бышыктык запасы", "Safety margin", "Güvenlik payı", "Mustahkamlik zaxirasi"), safety is { } s ? s.ToString("0", Ru) + " %" : "—",
            T("насколько может упасть выручка, пока бизнес в плюсе", "бизнес оң болгондо түшүм канчалык түшө алат", "how far revenue can fall while the business stays in profit", "iş kârdayken ciro ne kadar düşebilir", "biznes foydada ekan tushum qanchalik tushishi mumkin")));
        if (_stockCost is { } stock)
            tiles.Children.Add(Metric(T("Товарный остаток", "Товар калдыгы", "Stock on hand", "Mevcut stok", "Tovar qoldig'i"), Money0(stock), T("по закупочной цене на складе и в зале", "складда жана залда сатып алуу баасы боюнча", "at purchase price in the warehouse and shop", "depoda ve salonda alış fiyatıyla", "ombor va zaldagi xarid narxida")));
        stack.Children.Add(tiles);
        return Card(stack);
    }

    private Border Metric(string label, string value, string hint)
    {
        var s = new StackPanel { Spacing = 3, Children = { Soft(label, 12), Heading(value, 19), Soft(hint, 11.5) } };
        var b = new Border { Padding = new Thickness(14, 12), Child = s };
        Use(b, Border.BackgroundProperty, "BrushSurfaceSubtle");
        Use(b, Border.CornerRadiusProperty, "SettingsButtonRadius");
        return b;
    }

    private (string Owner, string[] Investors) ChartKeys(InvestmentResult R) => ChartKeys(R.Investors.Select(i => i.Investor.Id));

    private readonly Dictionary<string, string> _colors = new();

    /// <summary>Один цвет участника на всех вкладках (владелец — «owner», инвестор — «investor:id»).</summary>
    private void EnsureColors()
    {
        var (own, inv) = ChartKeys(StartupCostsStore.Investors.Select(i => i.Id));
        _colors.Clear();
        _colors[OwnerKey] = own;
        var ids = StartupCostsStore.Investors.Select(i => i.Id).ToList();
        for (var i = 0; i < ids.Count; i++)
            _colors["investor:" + ids[i]] = inv[i];
    }

    private string ColorOf(string key) => _colors.TryGetValue(key, out var c) ? c : ChartPalette.ColorFor(key);

    private (string Owner, string[] Investors) ChartKeys(IEnumerable<string> ids)
    {
        var keys = new List<string?> { OwnerKey };
        keys.AddRange(ids.Select(i => (string?)("investor:" + i)));
        var colors = ChartPalette.Assign(keys).ToArray();
        // 2026-10-29: красный коралловый — только у убытка; участникам он не достаётся (берётся первый свободный цвет палитры).
        var used = new HashSet<string>(colors) { LossHex };
        for (var i = 0; i < colors.Length; i++)
            if (colors[i] == LossHex)
                colors[i] = ChartPalette.Hex.First(h => !used.Contains(h));
        return (colors[0], colors.Skip(1).ToArray());
    }

    private Control Legend(IEnumerable<(string Color, string Text)> items)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (color, text) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 4) };
            row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Soft(text, 12));
            w.Children.Add(row);
        }
        return w;
    }

    /// <summary>Чистая прибыль и выплаты по месяцам: цветом — часть владельца и каждого инвестора, убыток — красным.</summary>
    private Control MonthlyChart(InvestmentResult R)
    {
        var (ownColor, invColors) = ChartKeys(R);
        var from = Math.Max(0, R.Months.Count - ChartMonths);
        var count = R.Months.Count - from;
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Heading(T("Чистая прибыль и выплаты по месяцам", "Айлар боюнча таза пайда жана төлөмдөр", "Net profit and payouts by month", "Aylara göre net kâr ve ödemeler", "Oylar bo'yicha sof foyda va to'lovlar"), 16));
        var legend = new List<(string, string)>();
        for (var k = 0; k < R.Investors.Count; k++)
            legend.Add((invColors[k], R.Investors[k].Investor.Name));
        legend.Add((ownColor, T("Владельцу", "Ээсине", "Owner", "Sahibine", "Egasiga")));
        legend.Add((LossHex, T("Убыток", "Зыян", "Loss", "Zarar", "Zarar")));
        stack.Children.Add(Legend(legend));
        if (count == 0)
            return Card(stack);
        var maxAbs = Math.Max(1m, Enumerable.Range(from, count).Max(i => Math.Abs(R.Months[i].Net)));
        const double h = 170;
        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", count))), MinWidth = Math.Min(900, 60 * count) };
        for (var c = 0; c < count; c++)
        {
            var i = from + c;
            var m = R.Months[i];
            var col = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 0), Height = h + 44 };
            var val = new TextBlock { Text = Short(m.Net), FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
            Use(val, TextBlock.ForegroundProperty, m.Net < 0m ? "BrushDanger" : "BrushText");
            var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 1 };
            if (m.Net < 0m)
                bars.Children.Add(new Border { Height = Math.Max(2, (double)(-m.Net / maxAbs) * h), Background = Brush.Parse(LossHex), CornerRadius = new CornerRadius(5, 5, 0, 0) });
            else
            {
                var owner = m.Net - R.Investors.Sum(r => r.Rows.Count > i ? r.Rows[i].Pay : 0m);
                if (owner > 0m)
                    bars.Children.Add(new Border { Height = Math.Max(1, (double)(owner / maxAbs) * h), Background = Brush.Parse(ownColor), CornerRadius = new CornerRadius(5, 5, 0, 0) });
                for (var k = R.Investors.Count - 1; k >= 0; k--)
                {
                    var pay = R.Investors[k].Rows.Count > i ? R.Investors[k].Rows[i].Pay : 0m;
                    if (pay > 0m)
                        bars.Children.Insert(0, new Border { Height = Math.Max(1, (double)(pay / maxAbs) * h), Background = Brush.Parse(invColors[k]) });
                }
            }
            var lab = new TextBlock { Text = m.From.ToString("MM.yy", CultureInfo.InvariantCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            Use(lab, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetRow(bars, 1);
            Grid.SetRow(lab, 2);
            col.Children.Add(val);
            col.Children.Add(bars);
            col.Children.Add(lab);
            Grid.SetColumn(col, c);
            cols.Children.Add(col);
        }
        stack.Children.Add(new ScrollViewer { Content = cols, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return Card(stack);
    }

    /// <summary>Накопленная чистая прибыль против вложений по месяцам (линия — вложено).</summary>
    private Control CumulativeChart(InvestmentResult R)
    {
        var rows = R.Business.Rows;
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Heading(T("Накопленная прибыль и вложения", "Топтолгон пайда жана салымдар", "Cumulative profit vs investment", "Birikmiş kâr ve yatırım", "Jamlangan foyda va sarmoya"), 16));
        var cumColor = ChartPalette.ColorFor("cumulative");
        var invColor = ChartPalette.ColorFor("invested");
        stack.Children.Add(Legend(new[] { (cumColor, T("Накопленная чистая прибыль", "Топтолгон таза пайда", "Cumulative net profit", "Birikmiş net kâr", "Jamlangan sof foyda")), (invColor, T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan")) }));
        if (rows.Count == 0)
            return Card(stack);
        var shown = rows.TakeLast(24).ToList();
        var invested = R.TotalInvested;
        // 2026-11-05 (И-01): линия «вложено» - ступенька по датам вложений, а не одна высота на весь период.
        var maxV = Math.Max(shown.Max(r => r.Invested), Math.Max(1m, shown.Max(r => r.Cumulative)));
        const double h = 170;
        var area = new Grid { Height = h + 44, MinWidth = Math.Min(900, 44 * shown.Count) };
        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", shown.Count))) };
        for (var c = 0; c < shown.Count; c++)
        {
            var r = shown[c];
            var col = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 0) };
            var val = new TextBlock { Text = Short(r.Cumulative), FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            Use(val, TextBlock.ForegroundProperty, r.Cumulative < 0m ? "BrushDanger" : "BrushText");
            var barH = r.Cumulative > 0m ? Math.Max(2, (double)(r.Cumulative / maxV) * h) : 0;
            var barBorder = new Border { Height = barH, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse(cumColor), CornerRadius = new CornerRadius(5, 5, 0, 0) };
            var lab = new TextBlock { Text = r.Month.ToString("MM.yy", CultureInfo.InvariantCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            Use(lab, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetRow(barBorder, 1);
            Grid.SetRow(lab, 2);
            col.Children.Add(val);
            col.Children.Add(barBorder);
            col.Children.Add(lab);
            Grid.SetColumn(col, c);
            cols.Children.Add(col);
        }
        // Линия «вложено»: у каждого месяца на высоте вложено-к-месяцу / maxV над подписями месяцев (нижняя строка — 22 px).
        var lines = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", shown.Count))), IsHitTestVisible = false };
        for (var c = 0; c < shown.Count; c++)
        {
            if (shown[c].Invested <= 0m)
                continue;
            var seg = new Border
            {
                Height = 2, Background = Brush.Parse(invColor), VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 22 + (double)(shown[c].Invested / maxV) * h),
            };
            Grid.SetColumn(seg, c);
            lines.Children.Add(seg);
        }
        area.Children.Add(cols);
        area.Children.Add(lines);
        stack.Children.Add(new ScrollViewer { Content = area, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        stack.Children.Add(Soft(T($"Линия — вложено {Money0(invested)}. Столбик достигает линии — вложения окупились.", $"Сызык — салынды {Money0(invested)}. Мамычы сызыкка жетсе — салым кайтты.",
            $"The line is the amount invested, {Money0(invested)}. When the bar reaches the line, the investment has paid back.", $"Çizgi yatırılan tutar: {Money0(invested)}. Sütun çizgiye ulaşınca yatırım geri dönmüştür.", $"Chiziq — kiritilgan {Money0(invested)}. Ustun chiziqqa yetsa — sarmoya oqlandi."), 12));
        return Card(stack);
    }

    /// <summary>Структура вложений: кто вложил (владелец и инвесторы) и на что (по категориям стартовых расходов).</summary>
    private Control StructureCard(InvestmentResult R, IReadOnlyList<StartupCostItem> costs)
    {
        var (ownColor, invColors) = ChartKeys(R);
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Heading(T("Структура вложений", "Салымдардын түзүмү", "Structure of investments", "Yatırım yapısı", "Sarmoyalar tuzilishi"), 16));
        var parts = new List<(string Name, string Who, decimal Amount, string Color)>();
        if (R.Owner.Invested > 0m)
            parts.Add((T("Собственные средства", "Жеке каражаттар", "Own funds", "Öz kaynaklar", "O'z mablag'lari"), T("Владелец", "Ээси", "Owner", "Sahibi", "Egasi"), R.Owner.Invested, ownColor));
        for (var k = 0; k < R.Investors.Count; k++)
            parts.Add((T("Инвестиция", "Инвестиция", "Investment", "Yatırım", "Investitsiya"), R.Investors[k].Investor.Name, R.Investors[k].Invested, invColors[k]));
        var sum = Math.Max(1m, parts.Sum(p => p.Amount));
        if (parts.Count > 0)
        {
            var seg = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", parts.Select(p => ((double)(p.Amount / sum * 100m)).ToString("0.####", CultureInfo.InvariantCulture) + "*"))), Height = 14 };
            for (var i = 0; i < parts.Count; i++)
            {
                var b = new Border { Background = Brush.Parse(parts[i].Color) };
                Grid.SetColumn(b, i);
                seg.Children.Add(b);
            }
            var track = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = seg };
            stack.Children.Add(track);
            foreach (var p in parts)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 36 };
                var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), Background = Brush.Parse(p.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Plain(p.Name, 13.5, true), Soft(p.Who, 12) } };
                Grid.SetColumn(names, 1);
                var amt = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { Plain(Money0(p.Amount), 13.5, true), Soft((p.Amount / sum * 100m).ToString("0", Ru) + "% " + T("вложений", "салымдардан", "of investments", "yatırımların", "sarmoyalardan"), 12) } };
                Grid.SetColumn(amt, 2);
                row.Children.Add(dot);
                row.Children.Add(names);
                row.Children.Add(amt);
                stack.Children.Add(row);
            }
        }
        var byCat = costs.GroupBy(c => c.Category).Select(g => (Key: g.Key, Amount: g.Sum(x => x.Amount))).OrderByDescending(x => x.Amount).ToList();
        if (byCat.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6), Opacity = 0.6, Background = Brushes.Gray });
            stack.Children.Add(Heading(T("На что вложено", "Эмнеге салынды", "What it was spent on", "Neye harcandı", "Nimaga sarflandi"), 14));
            var max = byCat.Max(x => x.Amount);
            var catColors = ChartPalette.Assign(byCat.Select(x => (string?)("cat:" + x.Key)).ToList());
            for (var i = 0; i < byCat.Count; i++)
            {
                var row = new StackPanel { Spacing = 4 };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var name = Plain(StartupCostsStore.CategoryTitle(byCat[i].Key), 13, true);
                var amt = Plain(Money0(byCat[i].Amount), 13, true);
                amt.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(amt, 1);
                line.Children.Add(name);
                line.Children.Add(amt);
                row.Children.Add(line);
                row.Children.Add(Bar((double)(byCat[i].Amount / max * 100m), catColors[i], 6));
                stack.Children.Add(row);
            }
        }
        return Card(stack);
    }
}
