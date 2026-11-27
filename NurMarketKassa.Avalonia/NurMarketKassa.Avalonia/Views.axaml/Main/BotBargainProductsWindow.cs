using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг»): «Правила торга по товарам» — список товаров магазина (GET telegram-bot/bargain/products/: поиск, категория, «только с правилами»)
/// с ценой, закупкой, правилами и итоговым полом цены; правка (PUT: торг разрешён / запрещён / как в общих, минимальная цена, макс. скидка) и сброс (DELETE) прямо в строке, «Проверить торг» (симулятор).
/// Закупка и пол цены — только владельцу, покупатель и ИИ их не видят. Открывается при признаке сервера bot_bargain. Суммы — MoneyText. Собрано в коде, помещается на 1024×600.</summary>
public sealed class BotBargainProductsWindow : Window
{
    private readonly IBotBargainApi? _api;
    private readonly TextBox _search = new() { Width = 220, MaxLength = 100, Watermark = T("Название или штрихкод", "Аты же штрихкод", "Name or barcode", "Ad veya barkod", "Nomi yoki shtrixkod") };
    private readonly ComboBox _category = new() { MinWidth = 200 };
    private readonly CheckBox _onlyRules = new() { Content = T("Только с правилами", "Эрежелери барлар гана", "Only with rules", "Yalnızca kurallı", "Faqat qoidali") };
    private readonly Button _apply = Btn(T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"), primary: true);
    private readonly Button _journal = Btn(T("Журнал оферт", "Оферталар журналы", "Offer log", "Teklif günlüğü", "Takliflar jurnali"));
    private readonly Button _prev = Btn("‹ " + T("Назад", "Артка", "Back", "Geri", "Orqaga"));
    private readonly Button _next = Btn(T("Дальше", "Кийинки", "Next", "İleri", "Keyingi") + " ›");
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _info = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private int _page = 1;
    private bool _loading;

    public BotBargainProductsWindow() : this(BargainApi())
    {
    }

    public BotBargainProductsWindow(IBotBargainApi? api)
    {
        _api = api;
        Title = T("Правила торга по товарам", "Товарлар боюнча соодалашуу эрежелери", "Bargaining rules by product", "Ürün bazında pazarlık kuralları", "Mahsulotlar bo'yicha savdolashish qoidalari");
        Width = 1000;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        // 2026-11-26 (стресс-тест): Esc закрывает окно; в поле с набранным текстом — не закрывает (набранный ответ/цена не теряются)
        EscapeKey.Attach(this, () => FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark ?? "");
        Avalonia.Automation.AutomationProperties.SetName(_category, T("Категория", "Категория", "Category", "Kategori", "Kategoriya"));
        Avalonia.Automation.AutomationProperties.SetName(_onlyRules, _onlyRules.Content as string ?? "");
        Theme(_info, TextBlock.ForegroundProperty, "BrushTextSoft");
        Theme(_pageText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _category.Items.Add(new ComboBoxItem { Content = T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"), Tag = "" });
        _category.SelectedIndex = 0;

        _apply.Click += async (_, _) => { _page = 1; await LoadAsync().ConfigureAwait(true); };
        _prev.Click += async (_, _) => { if (_page > 1) { _page--; await LoadAsync().ConfigureAwait(true); } };
        _next.Click += async (_, _) => { _page++; await LoadAsync().ConfigureAwait(true); };
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _page = 1; await LoadAsync().ConfigureAwait(true); }
        };
        _journal.Click += (_, _) =>
        {
            try { new BotBargainOffersWindow(_api).Show(this); }
            catch (Exception ex) { PosLogger.Log($"Журнал оферт не открылся: {ex.Message}", "WARNING"); }
        };

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("Торг решает сервер: цену ниже «пола» бот не уступит. Пол = наибольшая из минимальной цены, закупки с маржой и цены минус лимит скидки. Закупку и пол видите только вы.",
            "Соодалашууну сервер чечет: «полдон» төмөн баага бот көнбөйт. Пол — минималдуу баа, чегерим менен сатып алуу баасы жана лимит арзандатуусу кемитилген баанын эң чоңу. Сатып алуу баасын жана полду сиз гана көрөсүз.",
            "The server decides: the bot never goes below the “floor”. The floor is the largest of the minimum price, the purchase price plus margin, and the price minus the discount limit. Only you see the purchase price and the floor.",
            "Kararı sunucu verir: bot «taban»ın altına inmez. Taban; en düşük fiyat, marjlı alış fiyatı ve indirim sınırı düşülmüş fiyatın en büyüğüdür. Alış fiyatını ve tabanı yalnızca siz görürsünüz.",
            "Qarorni server qabul qiladi: bot «pol»dan pastga tushmaydi. Pol — minimal narx, marjali xarid narxi va chegirma limiti ayirilgan narxning eng kattasi. Xarid narxi va polni faqat siz ko'rasiz.")));
        root.Children.Add(head);

        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        foreach (var c in new Control[] { _search, _category, _onlyRules, _apply, _journal })
        {
            c.Margin = new Thickness(0, 0, 8, 4);
            if (c is CheckBox)
                c.VerticalAlignment = VerticalAlignment.Center;
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
        Opened += (_, _) => _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (_api != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach (var c in await _api.GetCategoriesAsync(cts.Token).ConfigureAwait(true))
                    _category.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Торг: категории не получены ({ex.GetType().Name}).", "WARNING"); // список остаётся без фильтра по категории
            }
        }
        await LoadAsync().ConfigureAwait(true);
    }

    private string CategoryName(string? id) =>
        id is null ? "" : _category.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == id)?.Content as string ?? "";

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
            var category = (_category.SelectedItem as ComboBoxItem)?.Tag as string;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Render(await _api.GetBargainProductsAsync(_search.Text, category, _onlyRules.IsChecked == true, _page, cts.Token).ConfigureAwait(true));
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

    private void Render(BotBargainProductsPage page)
    {
        _rows.Children.Clear();
        _prev.IsEnabled = _page > 1;
        _next.IsEnabled = page.HasNext;
        _pageText.Text = T("Страница ", "Барак ", "Page ", "Sayfa ", "Sahifa ") + _page.ToString(CultureInfo.InvariantCulture);
        if (page.Rows.Count == 0)
        {
            _info.Text = T("Товаров по этим условиям нет.", "Бул шарттар боюнча товарлар жок.", "No products match.", "Bu koşullarla ürün yok.", "Bu shartlar bo'yicha mahsulotlar yo'q.");
            return;
        }
        _info.Text = page.Count is { } n
            ? T($"Всего: {n}", $"Бардыгы: {n}", $"Total: {n}", $"Toplam: {n}", $"Jami: {n}")
            : T($"Показано: {page.Rows.Count}", $"Көрсөтүлдү: {page.Rows.Count}", $"Shown: {page.Rows.Count}", $"Gösterilen: {page.Rows.Count}", $"Ko'rsatildi: {page.Rows.Count}");
        foreach (var p in page.Rows)
            _rows.Children.Add(Row(p));
    }

    private static string AllowedText(BotBargainProduct p) => p.Allowed switch
    {
        true => T("Торг разрешён", "Соодалашууга уруксат", "Bargaining allowed", "Pazarlığa izin var", "Savdolashishga ruxsat"),
        false => T("Торг запрещён", "Соодалашуу тыюу салынган", "Bargaining forbidden", "Pazarlık yasak", "Savdolashish taqiqlangan"),
        _ => p.EffectiveAllowed
            ? T("Как в общих: торг идёт", "Жалпы жөндөөдөй: соодалашуу бар", "As in general: bargaining on", "Genel gibi: pazarlık var", "Umumiydek: savdolashish bor")
            : T("Как в общих: торга нет", "Жалпы жөндөөдөй: соодалашуу жок", "As in general: no bargaining", "Genel gibi: pazarlık yok", "Umumiydek: savdolashish yo'q"),
    };

    private Border Row(BotBargainProduct p)
    {
        var box = new StackPanel { Spacing = 6 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock { Text = p.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, p.Name); // 2026-11-26 (стресс-тест): длинное название обрезано «…» — полностью во всплывающей подсказке
        Theme(name, TextBlock.ForegroundProperty, "BrushText");
        var state = new TextBlock { Text = AllowedText(p), FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0), FontWeight = p.EffectiveAllowed ? FontWeight.SemiBold : FontWeight.Normal };
        Theme(state, TextBlock.ForegroundProperty, p.EffectiveAllowed ? "BrushText" : "BrushTextSoft");
        Grid.SetColumn(state, 1);
        top.Children.Add(name);
        top.Children.Add(state);
        box.Children.Add(top);

        var parts = new List<string>
        {
            T("Цена: ", "Баасы: ", "Price: ", "Fiyat: ", "Narx: ") + Som(p.Price),
            T("Закупка: ", "Сатып алуу: ", "Purchase: ", "Alış: ", "Xarid: ") + Som(p.PurchasePrice),
        };
        if (p.MinPrice is not null)
            parts.Add(T("Мин. цена: ", "Мин. баа: ", "Min. price: ", "En düşük fiyat: ", "Min. narx: ") + Som(p.MinPrice));
        if (p.MaxDiscountPercent is not null)
            parts.Add(T("Макс. скидка: ", "Макс. арзандатуу: ", "Max. discount: ", "Maks. indirim: ", "Maks. chegirma: ") + Percent(p.MaxDiscountPercent));
        parts.Add(T("Пол цены: ", "Баанын полу: ", "Price floor: ", "Fiyat tabanı: ", "Narx poli: ") + Som(p.Floor));
        if (CategoryName(p.CategoryId) is { Length: > 0 } cat)
            parts.Add(cat);
        box.Children.Add(Soft(string.Join(" · ", parts), 12.5));

        var editor = EditPanel(p);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var edit = Btn(T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"), primary: true);
        edit.Click += (_, _) => editor.IsVisible = !editor.IsVisible;
        var test = Btn(T("Проверить торг", "Соодалашууну текшерүү", "Check bargaining", "Pazarlığı dene", "Savdolashishni tekshirish"));
        test.Click += (_, _) =>
        {
            try { new BotBargainTestWindow(_api, p.ProductId, p.Name, p.Price).Show(this); }
            catch (Exception ex) { PosLogger.Log($"«Проверить торг» не открылось: {ex.Message}", "WARNING"); }
        };
        actions.Children.Add(edit);
        actions.Children.Add(test);
        if (p.HasRule)
        {
            var reset = Btn(T("Сбросить правила", "Эрежелерди тазалоо", "Reset rules", "Kuralları sıfırla", "Qoidalarni tozalash"));
            reset.Click += async (_, _) => await ResetAsync(p, reset).ConfigureAwait(true);
            actions.Children.Add(reset);
        }
        foreach (var b in actions.Children)
            b.Margin = new Thickness(0, 0, 8, 4);
        box.Children.Add(actions);
        box.Children.Add(editor);

        var row = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = box };
        Theme(row, Border.BackgroundProperty, p.EffectiveAllowed ? "BrushAccentSoft" : "BrushPanelSoft");
        Theme(row, Border.BorderBrushProperty, p.EffectiveAllowed ? "BrushAccent" : "BrushBorder");
        return row;
    }

    /// <summary>Правка правил в строке: «Торг» (как в общих / разрешён / запрещён), «Минимальная цена» (пусто — не задана), «Макс. скидка, %» (пусто — как в общих). Ошибки 400 сервера — по-русски под полями.</summary>
    private StackPanel EditPanel(BotBargainProduct p)
    {
        var allowed = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        allowed.Items.Add(new ComboBoxItem { Content = T("Как в общих настройках", "Жалпы жөндөөлөрдөгүдөй", "As in the general settings", "Genel ayarlardaki gibi", "Umumiy sozlamalardagidek"), Tag = "" });
        allowed.Items.Add(new ComboBoxItem { Content = T("Разрешён", "Уруксат", "Allowed", "İzinli", "Ruxsat"), Tag = "true" });
        allowed.Items.Add(new ComboBoxItem { Content = T("Запрещён", "Тыюу салынган", "Forbidden", "Yasak", "Taqiqlangan"), Tag = "false" });
        allowed.SelectedIndex = p.Allowed switch { true => 1, false => 2, _ => 0 };
        var minPrice = new TextBox { MaxLength = 14, Text = p.MinPrice is { } mp ? mp.ToString("0.##", CultureInfo.InvariantCulture) : "" };
        var maxDiscount = new TextBox { MaxLength = 6, Text = p.MaxDiscountPercent is { } md ? md.ToString("0.##", CultureInfo.InvariantCulture) : "" };
        var status = Soft("", 12.5);
        var save = Btn(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), primary: true);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        var a = Labeled(T("Торг", "Соодалашуу", "Bargaining", "Pazarlık", "Savdolashish"), allowed);
        var b = Labeled(T("Минимальная цена для торга, сом", "Соодалашуу үчүн минималдуу баа, сом", "Minimum price for bargaining, som", "Pazarlık için en düşük fiyat, som", "Savdolashish uchun minimal narx, so'm"), minPrice,
            T("Пусто — не задана.", "Бош — коюлган эмес.", "Empty — not set.", "Boş — ayarlı değil.", "Bo'sh — belgilanmagan."));
        var c = Labeled(T("Макс. скидка, %", "Макс. арзандатуу, %", "Max. discount, %", "Maks. indirim, %", "Maks. chegirma, %"), maxDiscount,
            T("0–50. Пусто — как в общих.", "0–50. Бош — жалпыдагыдай.", "0–50. Empty — as in general.", "0–50. Boş — genel gibi.", "0–50. Bo'sh — umumiydek."));
        Grid.SetColumn(b, 2);
        Grid.SetColumn(c, 4);
        grid.Children.Add(a);
        grid.Children.Add(b);
        grid.Children.Add(c);

        save.Click += async (_, _) =>
        {
            if (_api == null || _loading)
                return;
            decimal? price = ParseAmount(minPrice.Text), disc = ParseAmount(maxDiscount.Text);
            if ((!string.IsNullOrWhiteSpace(minPrice.Text) && price is null) || (!string.IsNullOrWhiteSpace(maxDiscount.Text) && disc is null))
            {
                status.Text = T("Цена и скидка — числа, например 28000 и 7.", "Баа жана арзандатуу — сандар, мисалы 28000 жана 7.", "Price and discount are numbers, e.g. 28000 and 7.", "Fiyat ve indirim sayıdır, örneğin 28000 ve 7.", "Narx va chegirma — sonlar, masalan 28000 va 7.");
                return;
            }
            if (BotBargainText.CheckRule(price, disc, p.Price) is { } problem) // 2026-11-26 (стресс-тест): понятная причина до PUT
            {
                status.Text = problem;
                return;
            }
            bool? allow =(allowed.SelectedItem as ComboBoxItem)?.Tag switch { "true" => true, "false" => false, _ => null };
            save.IsEnabled = false;
            status.Text = T("Сохраняем…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _api.SaveBargainProductAsync(p.ProductId, new BotBargainRule(allow, price, disc), cts.Token).ConfigureAwait(true);
                PosLogger.Log($"Торг: правила товара сохранены ({p.ProductId}).", "TELEGRAM");
                await LoadAsync().ConfigureAwait(true);
                _info.Text = T("Правила сохранены. ", "Эрежелер сакталды. ", "Rules saved. ", "Kurallar kaydedildi. ", "Qoidalar saqlandi. ") + _info.Text;
            }
            catch (Exception ex)
            {
                status.Text = T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + BargainFailure(ex);
                save.IsEnabled = true;
            }
        };
        var panel = new StackPanel { Spacing = 8, IsVisible = false };
        panel.Children.Add(grid);
        panel.Children.Add(save);
        panel.Children.Add(status);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        return panel;
    }

    private async Task ResetAsync(BotBargainProduct p, Button button)
    {
        if (_api == null || _loading)
            return;
        // 2026-11-26 (стресс-тест): сброс правил стирает мин. цену и лимит товара — только вторым нажатием («Да, сбросить»), случайный клик ничего не меняет.
        if (button.Tag as string != "confirm")
        {
            button.Tag = "confirm";
            button.Content = T("Да, сбросить", "Ооба, тазалоо", "Yes, reset", "Evet, sıfırla", "Ha, tozalash");
            Avalonia.Automation.AutomationProperties.SetName(button, button.Content as string ?? ""); // экранный диктор читает новую подпись
            _info.Text = T($"Сбросить правила «{p.Name}»? Мин. цена и лимит скидки удалятся, товар будет «как в общих». Нажмите «Да, сбросить».",
                $"«{p.Name}» эрежелерин тазалайлыбы? Мин. баа жана арзандатуу лимити өчөт, товар «жалпыдагыдай» болот. «Ооба, тазалоону» басыңыз.",
                $"Reset the rules of “{p.Name}”? The min. price and discount limit will be removed, the product goes “as in general”. Press “Yes, reset”.",
                $"«{p.Name}» kuralları sıfırlansın mı? En düşük fiyat ve indirim sınırı silinir, ürün «genel gibi» olur. «Evet, sıfırla»ya basın.",
                $"«{p.Name}» qoidalari tozalansinmi? Min. narx va chegirma limiti o'chadi, mahsulot «umumiydek» bo'ladi. «Ha, tozalash»ni bosing.");
            return;
        }
        button.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _api.ResetBargainProductAsync(p.ProductId, cts.Token).ConfigureAwait(true);
            PosLogger.Log($"Торг: правила товара сброшены ({p.ProductId}).", "TELEGRAM");
            await LoadAsync().ConfigureAwait(true);
            _info.Text = T("Правила сброшены. ", "Эрежелер тазаланды. ", "Rules reset. ", "Kurallar sıfırlandı. ", "Qoidalar tozalandi. ") + _info.Text;
        }
        catch (Exception ex)
        {
            _info.Text = T("Не получилось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + BargainFailure(ex);
            button.IsEnabled = true;
        }
    }
}
