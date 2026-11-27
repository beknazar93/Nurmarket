using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг»): «Журнал оферт» — каждое решение движка торга (GET telegram-bot/bargain/offers/ ?from&amp;to&amp;decision): товар, цена в списке, предложение
/// покупателя, решение, цена, ступень, пол цены, лимит скидки и номер заказа. Пол цены — только владельцу. Строки-карточки в две строки (на 1024×600 колонки не помещались). Суммы — MoneyText.</summary>
public sealed class BotBargainOffersWindow : Window
{
    private readonly IBotBargainApi? _api;
    private readonly ComboBox _decision = new() { MinWidth = 190 };
    private readonly ComboBox _period = new() { MinWidth = 110 };
    private readonly Button _apply = Btn(T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"), primary: true);
    private readonly Button _prev = Btn("‹ " + T("Назад", "Артка", "Back", "Geri", "Orqaga"));
    private readonly Button _next = Btn(T("Дальше", "Кийинки", "Next", "İleri", "Keyingi") + " ›");
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _info = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private int _page = 1;
    private bool _loading;

    public BotBargainOffersWindow() : this(BargainApi())
    {
    }

    public BotBargainOffersWindow(IBotBargainApi? api)
    {
        _api = api;
        Title = T("Журнал оферт", "Оферталар журналы", "Offer log", "Teklif günlüğü", "Takliflar jurnali");
        Width = 1000;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        EscapeKey.Attach(this); // 2026-11-26 (стресс-тест): Esc закрывает окно, как остальные окна программы
        foreach (var (value, label) in new[]
                 {
                     ("", T("Все решения", "Бардык чечимдер", "All decisions", "Tüm kararlar", "Barcha qarorlar")),
                     ("accept", DecisionText("accept")),
                     ("counter", DecisionText("counter")),
                     ("decline", DecisionText("decline")),
                     ("fixed", DecisionText("fixed")),
                 })
            _decision.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _decision.SelectedIndex = 0;
        foreach (var days in new[] { 7, 30, 90 })
            _period.Items.Add(new ComboBoxItem { Content = PeriodLabel(days), Tag = days });
        _period.SelectedIndex = 1;
        Avalonia.Automation.AutomationProperties.SetName(_decision, T("Решение", "Чечим", "Decision", "Karar", "Qaror"));
        Avalonia.Automation.AutomationProperties.SetName(_period, T("Период", "Мөөнөт", "Period", "Dönem", "Davr"));
        Theme(_info, TextBlock.ForegroundProperty, "BrushTextSoft");
        Theme(_pageText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _apply.Click += async (_, _) => { _page = 1; await LoadAsync().ConfigureAwait(true); };
        _prev.Click += async (_, _) => { if (_page > 1) { _page--; await LoadAsync().ConfigureAwait(true); } };
        _next.Click += async (_, _) => { _page++; await LoadAsync().ConfigureAwait(true); };

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("Все решения движка торга: что просил покупатель, что ответил сервер и ниже какой цены он не опускался (пол). Строки «тест» — из «Проверить торг», в отчёт они не попадают.",
            "Соодалашуу кыймылдаткычынын бардык чечимдери: сатып алуучу эмне сурады, сервер эмне жооп берди жана кайсы баадан төмөн түшкөн жок (пол). «Тест» саптары — «Соодалашууну текшерүүдөн», отчётко кирбейт.",
            "All decisions of the bargaining engine: what the customer asked, what the server answered and the floor it never went below. “Test” rows come from “Check bargaining” and are not in the report.",
            "Pazarlık motorunun tüm kararları: müşteri ne istedi, sunucu ne yanıt verdi ve hangi fiyatın (taban) altına inmedi. «Deneme» satırları «Pazarlığı dene»den gelir, rapora girmez.",
            "Savdolashish dvigatelining barcha qarorlari: xaridor nima so'radi, server nima javob berdi va qaysi narxdan (pol) pastga tushmadi. «Sinov» qatorlari «Savdolashishni tekshirish»dan, hisobotga kirmaydi.")));
        root.Children.Add(head);

        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        foreach (var c in new Control[] { _decision, _period, _apply })
        {
            c.Margin = new Thickness(0, 0, 8, 4);
            filters.Children.Add(c);
        }
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            Padding = new Thickness(0, 0, 10, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_info);
        Grid.SetColumn(_prev, 1);
        Grid.SetColumn(_pageText, 2);
        Grid.SetColumn(_next, 3);
        _pageText.Margin = new Thickness(10, 0);
        footer.Children.Add(_prev);
        footer.Children.Add(_pageText);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        _prev.IsEnabled = _next.IsEnabled = false;
        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_api == null || _loading)
            return;
        _loading = true;
        _apply.IsEnabled = false;
        _info.Text = T("Загружаем…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        var pageGone = false;
        try
        {
            var decision = (_decision.SelectedItem as ComboBoxItem)?.Tag as string;
            var days = (_period.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 30;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Render(await _api.GetBargainOffersAsync(DateTime.Today.AddDays(-days), DateTime.Today, decision, _page, cts.Token).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            _rows.Children.Clear();
            _prev.IsEnabled = _page > 1;
            _next.IsEnabled = false;
            _info.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + Failure(ex);
            // 2026-11-26 (живая проверка NBS): страницы за концом списка сервер отвечает 404 — список стал короче (взяли диалог, сбросили правила), а не «сервер не поддерживает»: идём на первую страницу.
            pageGone = ex is ApiException { StatusCode: 404 } && _page > 1;
        }
        finally
        {
            _loading = false;
            _apply.IsEnabled = true;
        }
        if (pageGone)
        {
            _page = 1;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private void Render(BotBargainOffersPage page)
    {
        _rows.Children.Clear();
        _prev.IsEnabled = _page > 1;
        _next.IsEnabled = page.HasNext;
        _pageText.Text = T("Страница ", "Барак ", "Page ", "Sayfa ", "Sahifa ") + _page.ToString(CultureInfo.InvariantCulture);
        if (page.Rows.Count == 0)
        {
            _info.Text = T("Оферт по этим условиям нет.", "Бул шарттар боюнча оферталар жок.", "No offers match.", "Bu koşullarla teklif yok.", "Bu shartlar bo'yicha takliflar yo'q.");
            return;
        }
        _info.Text = page.Count is { } n
            ? T($"Всего: {n}", $"Бардыгы: {n}", $"Total: {n}", $"Toplam: {n}", $"Jami: {n}")
            : T($"Показано: {page.Rows.Count}", $"Көрсөтүлдү: {page.Rows.Count}", $"Shown: {page.Rows.Count}", $"Gösterilen: {page.Rows.Count}", $"Ko'rsatildi: {page.Rows.Count}");
        foreach (var o in page.Rows)
            _rows.Children.Add(Row(o));
    }

    private static Border Row(BotBargainOffer o)
    {
        var box = new StackPanel { Spacing = 4 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock { Text = o.ProductName + (o.IsTest ? " · " + T("тест", "тест", "test", "deneme", "sinov") : ""), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, name.Text); // 2026-11-26 (стресс-тест): полное название во всплывающей подсказке
        Theme(name, TextBlock.ForegroundProperty, "BrushText");
        var decision = new TextBlock { Text = DecisionText(o.Decision) + (o.At is null ? "" : " · " + When(o.At)), FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeight.SemiBold };
        Theme(decision, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(decision, 1);
        top.Children.Add(name);
        top.Children.Add(decision);
        box.Children.Add(top);
        var parts = new List<string>
        {
            T("В списке: ", "Тизмеде: ", "List: ", "Liste: ", "Ro'yxatda: ") + Som(o.ListPrice),
            T("Предложение: ", "Сунуш: ", "Offer: ", "Teklif: ", "Taklif: ") + Som(o.ClientOffer),
            T("Цена: ", "Баасы: ", "Price: ", "Fiyat: ", "Narx: ") + Som(o.Price),
            T("Ступень: ", "Баскыч: ", "Step: ", "Adım: ", "Bosqich: ") + o.Step.ToString(CultureInfo.InvariantCulture),
            T("Пол: ", "Пол: ", "Floor: ", "Taban: ", "Pol: ") + Som(o.Floor),
            T("Лимит скидки: ", "Арзандатуу лимити: ", "Discount limit: ", "İndirim sınırı: ", "Chegirma limiti: ") + Percent(o.LimitPercent),
        };
        if (!string.IsNullOrWhiteSpace(o.Order))
            parts.Add(T("Заказ №", "Заказ №", "Order #", "Sipariş #", "Buyurtma №") + o.Order);
        box.Children.Add(Soft(string.Join(" · ", parts), 12.5));
        var row = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = box };
        Theme(row, Border.BackgroundProperty, o.Decision == "accept" ? "BrushAccentSoft" : "BrushPanelSoft");
        Theme(row, Border.BorderBrushProperty, o.Decision == "accept" ? "BrushAccent" : "BrushBorder");
        return row;
    }
}
