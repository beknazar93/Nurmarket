using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, «дизайн 10»: сцены для headless-снимков дизайна (стенд в scratchpad, без сети и без ИИ). В работе программы не
/// вызывается — только стендом через reflection. Цифры — образец (как у NBS 20.10), ничего не сохраняется и не отправляется.</summary>
public sealed partial class AiAdvisorWindow
{
    internal void PreviewScene(string scene)
    {
        _messages.Children.Clear();
        _welcome = null;
        var today = DateTime.Today;
        var y = today.AddDays(-1);
        OwnerPeriodStats.Period P(string key, string label, DateTime from, DateTime to, double rev, double checks, double profit) =>
            new(key, label, from, to, rev, checks, checks > 0 ? rev / checks : 0, profit, rev > 0 ? profit / rev * 100 : 0);
        var yesterday = P("yesterday", "Вчера", y, y, 19677, 22, 3542);
        var dayBefore = P("day_before", "Позавчера", y.AddDays(-1), y.AddDays(-1), 1805, 5, 330);
        var lastWeekDay = P("yesterday_last_week", "Тот же день", y.AddDays(-7), y.AddDays(-7), 12420, 15, 2210);
        var last7 = P("last7", "7 дней", y.AddDays(-6), y, 96472, 61, 17267);
        var prev7 = P("prev7", "Прошлые 7", y.AddDays(-13), y.AddDays(-7), 87589, 58, 15420);
        var month = P("month", "Месяц", new DateTime(today.Year, today.Month, 1), today, 241330, 170, 43200);
        var prevMonth = P("prev_month", "Прошлый месяц", new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddMonths(-1).AddDays(today.Day - 1), 198700, 151, 36100);
        OwnerPeriodStats.PreviewSet(new OwnerPeriodStats.Snapshot(DateTime.Now, new[] { yesterday, dayBefore, lastWeekDay, last7, prev7, month, prevMonth },
            new[]
            {
                new OwnerPeriodStats.Comparison("yesterday_vs_day_before", "Вчера к позавчера", yesterday, dayBefore),
                new OwnerPeriodStats.Comparison("yesterday_vs_last_week", "Вчера к тому же дню", yesterday, lastWeekDay),
                new OwnerPeriodStats.Comparison("week_vs_prev", "7 к 7", last7, prev7),
                new OwnerPeriodStats.Comparison("month_vs_prev", "Месяц к месяцу", month, prevMonth),
            }, new Dictionary<DateTime, double>(), Array.Empty<string>()));
        var sources = new[]
        {
            new AiSection("periods", "Сравнение периодов", 0, "-", SourceSection: "overview", SourceLabel: T($"Сводка (сервер NurCRM), периоды на {DateTime.Now:dd.MM HH:mm}",
                $"Жыйынтык (NurCRM сервери), {DateTime.Now:dd.MM HH:mm}", $"Overview (NurCRM server), {DateTime.Now:dd.MM HH:mm}", $"Özet (NurCRM sunucusu), {DateTime.Now:dd.MM HH:mm}", $"Umumiy (NurCRM serveri), {DateTime.Now:dd.MM HH:mm}")),
        };
        var day = T("День", "Күн", "Day", "Gün", "Kun");
        var rev = T("Выручка, сом", "Киреше, сом", "Revenue, som", "Ciro, som", "Tushum, so'm");
        var checks = T("Чеков", "Чек", "Receipts", "Fiş", "Chek");
        var total = T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
        var table = $"| {day} | {rev} | {checks} |\n|---|---:|---:|\n"
                    + string.Join("\n", new[] { 3100, 5040, 51480, 15400, 1805, 19677, 770 }.Select((v, i) => $"| {y.AddDays(i - 6):dd.MM} | {v:N0} | {new[] { 4, 6, 9, 11, 5, 22, 6 }[i]} |"))
                    + $"\n| {total} | 97 272 | 63 |";
        switch (scene)
        {
            case "answer":
            {
                AddBubble(T("Сравни эту неделю с прошлой", "Бул жуманы өткөн жума менен салыштыр", "Compare this week with last week", "Bu haftayı geçen haftayla karşılaştır", "Bu haftani o'tgan hafta bilan solishtir"), fromOwner: true);
                var text = T("За 7 полных дней выручка 96 472 сом — на 10,1 % больше прошлой недели (87 589 сом).", "Акыркы 7 толук күндө киреше 96 472 сом — өткөн жумадан 10,1 % көп (87 589 сом).",
                               "Over the last 7 full days revenue was 96,472 som — 10.1% more than last week (87,589 som).", "Son 7 tam günde ciro 96 472 som — geçen haftadan %10,1 fazla.",
                               "Oxirgi 7 to'liq kunda tushum 96 472 so'm — o'tgan haftadan 10,1 % ko'p.")
                           + "\n\n" + table + "\n\n"
                           + T("Лучший день — 16.10 (51 480 сом). Вчера 22 чека — втрое больше обычного: проверьте, что продавалось, и повторите это в выходные.",
                               "Эң жакшы күн — 16.10 (51 480 сом). Кечээ 22 чек — кадимкиден үч эсе көп.", "Best day — 16.10 (51,480 som). Yesterday had 22 receipts — triple the usual.",
                               "En iyi gün — 16.10 (51 480 som). Dün 22 fiş — normalin üç katı.", "Eng yaxshi kun — 16.10 (51 480 so'm). Kecha 22 chek — odatdagidan uch barobar ko'p.");
                var body = AddBubble(text, fromOwner: false);
                AddAnswerExtras(body, T("Сравни эту неделю с прошлой", "Бул жуманы өткөн жума менен салыштыр", "Compare this week with last week", "Bu haftayı geçen haftayla karşılaştır", "Bu haftani o'tgan hafta bilan solishtir"), text, sources, localOnly: false);
                break;
            }
            case "stages":
            {
                AddBubble(T("Что срочно заказать?", "Эмнени шашылыш заказ кылуу керек?", "What should I order urgently?", "Acilen ne sipariş etmeliyim?", "Nimani zudlik bilan buyurtma qilish kerak?"), fromOwner: true);
                var (_, setStage) = AddStageBubble();
                setStage(2);
                _busy = true;
                RefreshKeyCard();
                break;
            }
            case "actions":
            case "confirm":
            {
                var bayel = new CatalogProductTileVm("p1", "Байэл вода 1 л", "120 сом", false) { PurchasePrice = 160, Quantity = 24 };
                var mars = new CatalogProductTileVm("p2", "Mars 50 г", "75 сом", false) { PurchasePrice = 52, Quantity = 6 };
                var adyg = new CatalogProductTileVm("p3", "Адыгене 1 л", "95 сом", false) { PurchasePrice = 64, Quantity = 40 };
                AddBubble(T("Поставь цену на Байэл 1 сом, спиши 10 Mars и подними Адыгене до 120", "Байэлге 1 сом баа кой, 10 Mars чыгар, Адыгенени 120 кыл",
                    "Set Bayel to 1 som, write off 10 Mars and raise Adygene to 120", "Bayel fiyatını 1 som yap, 10 Mars düş, Adygene'yi 120 yap",
                    "Bayel narxini 1 so'm qil, 10 Mars hisobdan chiqar, Adygeneni 120 qil"), fromOwner: true);
                AddBubble(T("Предлагаю изменения — подтвердите:", "Өзгөртүүлөрдү сунуштайм — ырастаңыз:", "I suggest these changes — please confirm:", "Şu değişiklikleri öneriyorum — onaylayın:",
                    "Quyidagi o'zgarishlarni taklif qilaman — tasdiqlang:"), fromOwner: false);
                var steps = new List<ProductActionPlan.Step>
                {
                    new("set_price", bayel, 1, null, null),
                    new("stock_out", mars, 10, null, "Брак", ExpectedStock: 6),
                    new("set_price", adyg, 120, null, null),
                };
                AddProductActionsCard(steps, "preview");
                if (scene == "confirm" && _messages.Children.OfType<Border>().LastOrDefault() is { } card)
                    ShowSecondConfirm(card, ProductActionWarnings(steps), () => { }, (card.Child as StackPanel)?.Children.OfType<StackPanel>().FirstOrDefault(p => p.Orientation == Avalonia.Layout.Orientation.Horizontal));
                break;
            }
            case "morning":
            {
                var report = new OwnerMorningReport.Report(y, DateTime.Now,
                    new[]
                    {
                        new OwnerMorningReport.Tile("rev", T($"Выручка вчера, {y:dd.MM}", $"Кечээки киреше, {y:dd.MM}", $"Revenue yesterday, {y:dd.MM}", $"Dünkü ciro, {y:dd.MM}", $"Kechagi tushum, {y:dd.MM}"), "19 677 сом",
                            new[] { new OwnerMorningReport.Delta(990, T("к позавчера", "мурдагы күнгө", "vs day before", "önceki güne göre", "oldingi kunga")),
                                new OwnerMorningReport.Delta(58.4, T("к тому же дню прошлой недели", "өткөн жуманын ошол күнүнө", "vs same day last week", "geçen haftaya göre", "o'tgan haftaga")) },
                            T("22 чека, прибыль 3 542 сом", "22 чек, пайда 3 542 сом", "22 receipts, profit 3,542 som", "22 fiş, kâr 3 542 som", "22 chek, foyda 3 542 so'm"), false, "overview"),
                        new OwnerMorningReport.Tile("week", T("7 дней к прошлым 7", "7 күн мурунку 7ге", "7 days vs previous 7", "7 gün önceki 7'ye", "7 kun oldingi 7 ga"), "96 472 сом",
                            new[] { new OwnerMorningReport.Delta(10.1, T("к прошлой неделе", "өткөн жумага", "vs last week", "geçen haftaya", "o'tgan haftaga")) }, null, false, "overview"),
                        new OwnerMorningReport.Tile("debts", T("Вам должны", "Сизге карыз", "You are owed", "Size borçlu", "Sizga qarz"), "225 856 сом", Array.Empty<OwnerMorningReport.Delta>(),
                            T("13 клиентов, просрочка > 30 дн. — 117 811", "13 кардар, 30 күндөн ашык — 117 811", "13 customers, overdue > 30 d — 117,811", "13 müşteri, 30+ gün — 117 811", "13 mijoz, 30 kundan ortiq — 117 811"), true, "debts"),
                    },
                    new[] { new OwnerMorningReport.Item("Эссе манго — 0 шт., заказать ≈ 14", true, "restock"), new OwnerMorningReport.Item("Mars 50 г — хватит на 2 дн., заказать ≈ 20", false, "restock") }, 6,
                    new[] { new OwnerMorningReport.Item("Нан — просрочен 3 дн. (остаток 4)", true, "warehouse") }, 2,
                    new[] { new OwnerMorningReport.Item("Проверочный — 117 811 сом, 34 дн.", true, "debts") },
                    new[] { new OwnerMorningReport.Item(T("Удалено из чека: 3 позиции на 640 сом (кассир Айбек)", "Чектен өчүрүлдү: 3 позиция, 640 сом", "Removed from receipts: 3 items, 640 som", "Fişten silinen: 3 kalem, 640 som", "Chekdan o'chirildi: 3 ta, 640 so'm"), false, "sales") }, true,
                    T("Вчера — лучший день недели. Проверьте просрочку «Проверочного» и закажите Эссе манго до выходных.", "Кечээ — жуманын эң жакшы күнү. «Проверочныйдын» карызын текшериңиз.",
                        "Yesterday was the best day of the week. Check the overdue debt and reorder Esse mango before the weekend.", "Dün haftanın en iyi günüydü.", "Kecha — haftaning eng yaxshi kuni."),
                    new[] { T("Сводка сервера NurCRM", "NurCRM серверинин жыйынтыгы", "NurCRM overview", "NurCRM özeti", "NurCRM xulosasi"), T("Долги клиентов (точно)", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzi") },
                    Array.Empty<string>(), "", "");
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var avatar = AiAvatar(30);
                avatar.Margin = new Avalonia.Thickness(0, 2, 12, 0);
                row.Children.Add(avatar);
                var card = MorningReportCard.Build(report, _ => { }, () => { });
                Grid.SetColumn(card, 1);
                row.Children.Add(card);
                _messages.Children.Add(row);
                break;
            }
            case "history":
                ShowWelcome();
                RefreshHistoryList();
                _sidebar.IsVisible = true;
                break;
            default:
                ShowWelcome();
                break;
        }
    }
}
