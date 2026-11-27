using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1»): «Продажи бота» — отчёт за период (GET telegram-bot/sales-stats/): диалоги, заказы и суммы, конверсия, воронка, передача продавцу
/// (сколько звали, сколько ответили, за сколько секунд) и вопросы, на которые бот не знал ответа — подсказка пополнить «Правила магазина». Открывается при признаке сервера bot_sales_stats.
/// Суммы — MoneyText, проценты и числа — MoneyText.Weight. Собрано в коде, помещается на 1024×600.</summary>
public sealed class BotSalesStatsWindow : Window
{
    private readonly IBotSellerApi? _api;
    private readonly ComboBox _period = new() { MinWidth = 110 };
    private readonly Button _apply = Btn(T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"), primary: true);
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private bool _loading;

    public BotSalesStatsWindow() : this(Api())
    {
    }

    public BotSalesStatsWindow(IBotSellerApi? api)
    {
        _api = api;
        Title = T("Продажи бота", "Боттун сатуулары", "Bot sales", "Bot satışları", "Bot savdolari");
        Width = 900;
        Height = 560;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        EscapeKey.Attach(this); // 2026-11-26 (стресс-тест): Esc закрывает окно, как остальные окна программы
        foreach (var days in new[] { 7, 30, 90 })
            _period.Items.Add(new ComboBoxItem { Content = PeriodLabel(days), Tag = days });
        _period.SelectedIndex = 1;
        Avalonia.Automation.AutomationProperties.SetName(_period, T("Период", "Мөөнөт", "Period", "Dönem", "Davr"));
        Theme(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _apply.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("Сколько покупателей писали боту в Telegram, сколько заказов он принял и как часто звал продавца.", "Telegram'да ботко канча сатып алуучу жазды, бот канча заказ кабыл алды жана сатуучуну канчалык көп чакырды.",
            "How many customers wrote to the bot in Telegram, how many orders it took and how often it called the seller.", "Telegram'da bota kaç müşteri yazdı, kaç sipariş aldı ve satıcıyı ne sıklıkla çağırdı.", "Telegram'da botga nechta xaridor yozdi, u nechta buyurtma qabul qildi va sotuvchini qanchalik tez-tez chaqirdi.")));
        root.Children.Add(head);

        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        foreach (var c in new Control[] { _period, _apply })
        {
            c.Margin = new Thickness(0, 0, 8, 4);
            filters.Children.Add(c);
        }
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_tiles);
        body.Children.Add(_details);
        var scroll = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(0, 0, 10, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        _status.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;

        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_api == null || _loading)
            return;
        _loading = true;
        _apply.IsEnabled = false;
        _status.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            var days = (_period.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 30;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Render(await _api.GetSalesStatsAsync(DateTime.Today.AddDays(-days), DateTime.Today, cts.Token).ConfigureAwait(true));
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _status.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + Failure(ex);
        }
        finally
        {
            _loading = false;
            _apply.IsEnabled = true;
        }
    }

    private static string Num(int n) => n.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));

    private static Border Tile(string caption, string value, string? sub = null)
    {
        var box = new StackPanel { Spacing = 3 };
        var c = new TextBlock { Text = caption, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Theme(c, TextBlock.ForegroundProperty, "BrushTextSoft");
        var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Theme(v, TextBlock.ForegroundProperty, "BrushText");
        box.Children.Add(c);
        box.Children.Add(v);
        if (!string.IsNullOrWhiteSpace(sub))
            box.Children.Add(Soft(sub!));
        var tile = Card(box, 14);
        tile.Width = 200;
        tile.Margin = new Thickness(0, 0, 10, 10);
        return tile;
    }

    /// <summary>2026-11-26 (торг, Ф2): блок «Торг» отчёта — сколько предложений сделал бот, сколько приняли покупатели, заказов по торгу, сколько уступили от цены в списке, средняя скидка и доля «лучшей цены».</summary>
    private StackPanel BargainBlock(BotBargainStats bg)
    {
        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(Heading(T("Торг", "Соодалашуу", "Bargaining", "Pazarlık", "Savdolashish")));
        box.Children.Add(Soft(T($"Диалогов с торгом: {Num(bg.Dialogs)} · предложений бота: {Num(bg.Proposals)} · приняли покупатели: {Num(bg.AcceptedByCustomer)} · заказов по торгу: {Num(bg.Orders)}",
            $"Соодалашуу диалогдору: {Num(bg.Dialogs)} · боттун сунуштары: {Num(bg.Proposals)} · сатып алуучулар кабыл алды: {Num(bg.AcceptedByCustomer)} · соодалашуу боюнча заказдар: {Num(bg.Orders)}",
            $"Dialogs with bargaining: {Num(bg.Dialogs)} · bot offers: {Num(bg.Proposals)} · accepted by customers: {Num(bg.AcceptedByCustomer)} · bargained orders: {Num(bg.Orders)}",
            $"Pazarlıklı diyaloglar: {Num(bg.Dialogs)} · bot teklifleri: {Num(bg.Proposals)} · müşteri kabul etti: {Num(bg.AcceptedByCustomer)} · pazarlıklı siparişler: {Num(bg.Orders)}",
            $"Savdolashuvli dialoglar: {Num(bg.Dialogs)} · bot takliflari: {Num(bg.Proposals)} · xaridorlar qabul qildi: {Num(bg.AcceptedByCustomer)} · savdolashuvli buyurtmalar: {Num(bg.Orders)}"), 13));
        box.Children.Add(Soft(T($"Уступили от цены в списке: {MoneyText.Som(bg.ConcessionsTotal)} · средняя скидка: {MoneyText.Weight(bg.AvgDiscountPercent, "0.#")} % · «лучшая цена» в {MoneyText.Weight(bg.DeclineSharePercent, "0.#")} % предложений",
            $"Тизмедеги баадан түшүрүлдү: {MoneyText.Som(bg.ConcessionsTotal)} · орточо арзандатуу: {MoneyText.Weight(bg.AvgDiscountPercent, "0.#")} % · «эң мыкты баа» сунуштардын {MoneyText.Weight(bg.DeclineSharePercent, "0.#")} %",
            $"Conceded from the list price: {MoneyText.Som(bg.ConcessionsTotal)} · average discount: {MoneyText.Weight(bg.AvgDiscountPercent, "0.#")} % · “best price” in {MoneyText.Weight(bg.DeclineSharePercent, "0.#")} % of offers",
            $"Liste fiyatından verilen taviz: {MoneyText.Som(bg.ConcessionsTotal)} · ortalama indirim: {MoneyText.Weight(bg.AvgDiscountPercent, "0.#")} % · tekliflerin {MoneyText.Weight(bg.DeclineSharePercent, "0.#")} %'inde «en iyi fiyat»",
            $"Ro'yxat narxidan yon berildi: {MoneyText.Som(bg.ConcessionsTotal)} · o'rtacha chegirma: {MoneyText.Weight(bg.AvgDiscountPercent, "0.#")} % · takliflarning {MoneyText.Weight(bg.DeclineSharePercent, "0.#")} % «eng yaxshi narx»"), 13));
        if (bg.TopProducts.Count > 0)
        {
            box.Children.Add(Soft(T("Товары, по которым торгуются чаще всего:", "Эң көп соодалашуучу товарлар:", "Products bargained most often:", "En çok pazarlık yapılan ürünler:", "Eng ko'p savdolashiladigan mahsulotlar:"), 13));
            // 2026-11-27 (ответ Умара, п. 4): до 10 товаров по убыванию «предложили торг» (сервер так и отдаёт; сортировка устойчивая — порядок сервера при равенстве сохраняется).
            foreach (var t in bg.TopProducts.OrderByDescending(x => x.Proposals ?? -1).Take(10))
            {
                var parts = new List<string>();
                if (t.Proposals is { } p)
                    parts.Add(T($"предложили торг {Num(p)} раз", $"соодалашуу сунушталды {Num(p)} жолу", $"bargaining offered {Num(p)} times", $"pazarlık {Num(p)} kez önerildi", $"savdolashuv {Num(p)} marta taklif qilindi"));
                if (t.Orders is { } o)
                    parts.Add(T($"заказов {Num(o)}", $"заказдар {Num(o)}", $"orders {Num(o)}", $"sipariş {Num(o)}", $"buyurtmalar {Num(o)}"));
                var line = Soft("• " + t.Name + (parts.Count > 0 ? " — " + string.Join(" · ", parts) : ""), 13);                Theme(line, TextBlock.ForegroundProperty, "BrushText");
                box.Children.Add(line);
            }
        }
        var journal = Btn(T("Журнал оферт", "Оферталар журналы", "Offer log", "Teklif günlüğü", "Takliflar jurnali"));
        journal.HorizontalAlignment = HorizontalAlignment.Left;
        journal.Click += (_, _) =>
        {
            try { new BotBargainOffersWindow(_api as IBotBargainApi).Show(this); }
            catch (Exception ex) { PosLogger.Log($"«Журнал оферт» не открылся: {ex.Message}", "WARNING"); }
        };
        box.Children.Add(journal);
        return box;
    }

    private void Render(BotSalesStats s)
    {
        _tiles.Children.Clear();
        _tiles.Children.Add(Tile(T("Диалогов", "Диалогдор", "Dialogs", "Diyaloglar", "Dialoglar"), Num(s.Dialogs), T($"сообщений {Num(s.Messages)} · покупателей {Num(s.People)}", $"билдирүү {Num(s.Messages)} · сатып алуучу {Num(s.People)}",
            $"messages {Num(s.Messages)} · customers {Num(s.People)}", $"mesaj {Num(s.Messages)} · müşteri {Num(s.People)}", $"xabar {Num(s.Messages)} · xaridor {Num(s.People)}")));
        _tiles.Children.Add(Tile(T("Заказов", "Заказдар", "Orders", "Siparişler", "Buyurtmalar"), Num(s.OrdersCount), MoneyText.Som(s.OrdersTotal)));
        _tiles.Children.Add(Tile(T("Выполнено", "Аткарылды", "Completed", "Tamamlanan", "Bajarildi"), Num(s.OrdersDone), MoneyText.Som(s.OrdersDoneTotal)
            + (s.OrdersCanceled > 0 ? T($" · отменено {Num(s.OrdersCanceled)}", $" · жокко чыгарылды {Num(s.OrdersCanceled)}", $" · canceled {Num(s.OrdersCanceled)}", $" · iptal {Num(s.OrdersCanceled)}", $" · bekor qilindi {Num(s.OrdersCanceled)}") : "")));
        _tiles.Children.Add(Tile(T("Средний чек", "Орточо чек", "Average check", "Ortalama fiş", "O'rtacha chek"), MoneyText.Som(s.AvgCheck)));
        _tiles.Children.Add(Tile(T("Конверсия", "Конверсия", "Conversion", "Dönüşüm", "Konversiya"), MoneyText.Weight(s.ConversionPercent, "0.#") + " %",
            T("из диалога в заказ", "диалогдон заказга", "from dialog to order", "diyalogdan siparişe", "dialogdan buyurtmaga")));

        _details.Children.Clear();
        var funnel = new StackPanel { Spacing = 6 };
        funnel.Children.Add(Heading(T("Воронка", "Сатуу воронкасы", "Funnel", "Satış hunisi", "Savdo voronkasi")));
        funnel.Children.Add(Soft(T($"{Num(s.Dialogs)} диалогов → {Num(s.FunnelHandoffs)} звали продавца → {Num(s.OrdersCount)} заказов → {Num(s.OrdersDone)} выполнено",
            $"{Num(s.Dialogs)} диалог → {Num(s.FunnelHandoffs)} сатуучуну чакырды → {Num(s.OrdersCount)} заказ → {Num(s.OrdersDone)} аткарылды",
            $"{Num(s.Dialogs)} dialogs → {Num(s.FunnelHandoffs)} called the seller → {Num(s.OrdersCount)} orders → {Num(s.OrdersDone)} completed",
            $"{Num(s.Dialogs)} diyalog → {Num(s.FunnelHandoffs)} satıcıyı çağırdı → {Num(s.OrdersCount)} sipariş → {Num(s.OrdersDone)} tamamlandı",
            $"{Num(s.Dialogs)} dialog → {Num(s.FunnelHandoffs)} sotuvchini chaqirdi → {Num(s.OrdersCount)} buyurtma → {Num(s.OrdersDone)} bajarildi"), 13));
        _details.Children.Add(Card(funnel));

        var hand = new StackPanel { Spacing = 6 };
        hand.Children.Add(Heading(T("Передача продавцу", "Сатуучуга өткөрүү", "Handoff to the seller", "Satıcıya devir", "Sotuvchiga o'tkazish")));
        hand.Children.Add(Soft(T($"Звали продавца: {Num(s.HandoffsTotal)} · ответили: {Num(s.Answered)} · без ответа: {Num(s.Unanswered)} · ждут сейчас: {Num(s.WaitingNow)} · перезвонить: {Num(s.Callbacks)}",
            $"Сатуучу чакырылды: {Num(s.HandoffsTotal)} · жооп берилди: {Num(s.Answered)} · жоопсуз: {Num(s.Unanswered)} · азыр күтүп жатат: {Num(s.WaitingNow)} · чалуу керек: {Num(s.Callbacks)}",
            $"Seller called: {Num(s.HandoffsTotal)} · answered: {Num(s.Answered)} · unanswered: {Num(s.Unanswered)} · waiting now: {Num(s.WaitingNow)} · call back: {Num(s.Callbacks)}",
            $"Satıcı çağrıldı: {Num(s.HandoffsTotal)} · yanıtlandı: {Num(s.Answered)} · yanıtsız: {Num(s.Unanswered)} · şu an bekleyen: {Num(s.WaitingNow)} · geri aranacak: {Num(s.Callbacks)}",
            $"Sotuvchi chaqirildi: {Num(s.HandoffsTotal)} · javob berildi: {Num(s.Answered)} · javobsiz: {Num(s.Unanswered)} · hozir kutmoqda: {Num(s.WaitingNow)} · qayta qo'ng'iroq: {Num(s.Callbacks)}"), 13));
        hand.Children.Add(Soft(T($"Время ответа: в среднем {Seconds(s.AvgAnswerSeconds)}, медиана {Seconds(s.MedianAnswerSeconds)}", $"Жооп убактысы: орточо {Seconds(s.AvgAnswerSeconds)}, медиана {Seconds(s.MedianAnswerSeconds)}",
            $"Answer time: average {Seconds(s.AvgAnswerSeconds)}, median {Seconds(s.MedianAnswerSeconds)}", $"Yanıt süresi: ortalama {Seconds(s.AvgAnswerSeconds)}, medyan {Seconds(s.MedianAnswerSeconds)}", $"Javob vaqti: o'rtacha {Seconds(s.AvgAnswerSeconds)}, mediana {Seconds(s.MedianAnswerSeconds)}"), 13));
        if (s.HandoffsByReason.Count > 0)
            hand.Children.Add(Soft(T("Причины: ", "Себептери: ", "Reasons: ", "Nedenler: ", "Sabablari: ") + string.Join(", ", s.HandoffsByReason.Select(kv => $"{ReasonText(kv.Key)} — {Num(kv.Value)}")), 13));
        _details.Children.Add(Card(hand));

        if (s.Bargain is { } bg && ServerCapabilities.Has(ServerCapabilities.BotBargain))
            _details.Children.Add(Card(BargainBlock(bg)));

        var unknown = new StackPanel { Spacing = 6 };
        unknown.Children.Add(Heading(T("Вопросы, на которые бот не знал ответа", "Бот жоопту билбеген суроолор", "Questions the bot could not answer", "Botun cevaplayamadığı sorular", "Bot javob bera olmagan savollar")));
        if (s.UnknownQuestions.Count == 0)
            unknown.Children.Add(Soft(T("За этот период таких вопросов не было.", "Бул мезгилде мындай суроолор болгон жок.", "There were no such questions in this period.", "Bu dönemde böyle soru olmadı.", "Bu davrda bunday savollar bo'lmadi.")));
        else
        {
            unknown.Children.Add(Soft(T("Допишите ответы в «Правила магазина» раздела «Бот-продавец» — бот начнёт отвечать сам.", "«Бот-сатуучу» бөлүмүндөгү «Дүкөндүн эрежелери» бөлүмүнө жоопторду кошуңуз — бот өзү жооп бере баштайт.",
                "Add the answers to “Shop rules” in the “Seller bot” section — the bot will start answering by itself.", "«Satıcı bot» bölümündeki «Mağaza kuralları»na yanıtları ekleyin — bot kendisi yanıtlamaya başlar.", "«Bot-sotuvchi» bo'limidagi «Do'kon qoidalari»ga javoblarni qo'shing — bot o'zi javob bera boshlaydi.")));
            foreach (var q in s.UnknownQuestions.Take(40))
            {
                var line = Soft(When(q.At) + (string.IsNullOrWhiteSpace(q.Name) ? "" : " · " + q.Name) + " · " + Clip(q.Text), 13); // 2026-11-26 (стресс-тест): длинный вопрос обрезан
                Theme(line, TextBlock.ForegroundProperty, "BrushText");
                unknown.Children.Add(line);
            }
        }
        if (ServerCapabilities.Has(ServerCapabilities.BotSeller))
        {
            var open = Btn(T("Открыть «Бот-продавец»", "«Бот-сатуучуну» ачуу", "Open “Seller bot”", "«Satıcı bot»u aç", "«Bot-sotuvchi»ni ochish"));
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += (_, _) =>
            {
                try { new BotSellerSettingsWindow(_api).Show(this); }
                catch (Exception ex) { PosLogger.Log($"«Бот-продавец» не открылся: {ex.Message}", "WARNING"); }
            };
            unknown.Children.Add(open);
        }
        _details.Children.Add(Card(unknown));
    }
}
