using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05 (волна 2-А, функция 3): «Автозаказ v2…» в рекомендуемом заказе — то, чего нет на сервере и что касса помнит у себя:
/// срок поставки поставщика (дней), его минимальная сумма заказа, кратность упаковки и минимальная партия по товарам.
/// Применяются, только если включены в «Автоматизация магазина». Пустое поле = не задано.</summary>
public sealed class PlannerRulesDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly TextBox _lead;
    private readonly TextBox _minSum;
    private readonly List<(string Id, TextBox Pack, TextBox Min)> _products = new();
    private readonly SupplierInfo _supplier;

    /// <summary>true — настройки сохранены (рекомендуемый заказ нужно пересчитать).</summary>
    public bool Saved { get; private set; }

    public PlannerRulesDialog(SupplierInfo supplier, IReadOnlyList<PlannerRow> rows)
    {
        _supplier = supplier;
        Title = T("Автозаказ v2: " + supplier.Name, "Автобуйрутма v2: " + supplier.Name, "Auto-order v2: " + supplier.Name, "Otomatik sipariş v2: " + supplier.Name, "Avtobuyurtma v2: " + supplier.Name);
        SectionCards.AsModal(this, 700);
        var d = ShopAutomationSettings.Current;
        d.Suppliers.TryGetValue(supplier.Id, out var sr);

        _lead = UiKit.Input(this, "0", 40);
        _lead.Width = 90;
        _lead.Text = ShopAutomationText.FormatNumber(sr?.LeadDays ?? 0);
        _minSum = UiKit.Input(this, "0", 40);
        _minSum.Width = 120;
        _minSum.Text = ShopAutomationText.FormatNumber(sr?.MinOrderSum ?? 0);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Row(T("Срок поставки, дней:", "Жеткирүү мөөнөтү, күн:", "Lead time, days:", "Teslim süresi, gün:", "Yetkazish muddati, kun:"), _lead));
        panel.Children.Add(Row(T("Минимальная сумма заказа, сом:", "Буйрутманын минималдуу суммасы, сом:", "Minimum order sum, som:", "Asgari sipariş tutarı, som:", "Minimal buyurtma summasi, so'm:"), _minSum));
        panel.Children.Add(Soft(T("Кратность упаковки и минимальная партия по товарам поставщика (штук). Рекомендация округляется вверх до упаковки и не меньше минимальной партии.",
            "Жеткирүүчүнүн товарлары боюнча таңгак еселиги жана минималдуу партия (даана). Сунуш таңгакка чейин жогору тегеректелет жана минималдуу партиядан кем болбойт.",
            "Pack multiple and minimum batch per supplier product (pieces). The recommendation is rounded up to the pack and is never below the minimum batch.",
            "Tedarikçi ürünleri için paket katı ve asgari parti (adet). Öneri pakete yukarı yuvarlanır ve asgari partiden az olmaz.",
            "Yetkazib beruvchi mahsulotlari bo'yicha qadoq karraligi va minimal partiya (dona). Tavsiya qadoqqa yuqoriga yaxlitlanadi va minimal partiyadan kam bo'lmaydi.")));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,110") };
        head.Children.Add(new TextBlock { Text = T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), FontWeight = FontWeight.SemiBold, FontSize = 13 });
        var h1 = new TextBlock { Text = T("Упаковка", "Таңгак", "Pack", "Paket", "Qadoq"), FontWeight = FontWeight.SemiBold, FontSize = 13 };
        Grid.SetColumn(h1, 1);
        var h2 = new TextBlock { Text = T("Мин. партия", "Мин. партия", "Min batch", "Asgari parti", "Min. partiya"), FontWeight = FontWeight.SemiBold, FontSize = 13 };
        Grid.SetColumn(h2, 2);
        head.Children.Add(h1);
        head.Children.Add(h2);
        panel.Children.Add(head);

        var list = new StackPanel { Spacing = 4 };
        foreach (var r in rows.Where(r => r.Status != PlannerStatus.NoSales || r.NeedsPurchase).Take(150))
        {
            d.Products.TryGetValue(r.Product.Id, out var pr);
            var pack = UiKit.Input(this, "—", 36);
            pack.Text = ShopAutomationText.FormatNumber(pr?.Pack ?? 0);
            var min = UiKit.Input(this, "—", 36);
            min.Text = ShopAutomationText.FormatNumber(pr?.MinQty ?? 0);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,110") };
            line.Children.Add(new TextBlock { Text = r.Product.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(pack, 1);
            Grid.SetColumn(min, 2);
            pack.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(pack);
            line.Children.Add(min);
            list.Children.Add(line);
            _products.Add((r.Product.Id, pack, min));
        }
        if (_products.Count == 0)
        {
            // 2026-11-05 (приёмка волн 0–3, С-2): поставщик выбран — причину пустого списка называем честно, а не «выберите поставщика».
            list.Children.Add(Soft(rows.Count == 0
                ? T("У выбранного поставщика нет товаров в справочнике — добавьте товары поставщику.", "Тандалган жеткирүүчүдө справочникте товарлар жок — товарларды кошуңуз.",
                    "The selected supplier has no products in the catalog — add products to the supplier.", "Seçilen tedarikçinin katalogda ürünü yok — ürün ekleyin.", "Tanlangan yetkazib beruvchining katalogda mahsuloti yo'q — mahsulot qo'shing.")
                : T($"Нет товаров к заказу: у выбранного поставщика ({rows.Count} товаров) нет продаж за период и достаточный остаток. Срок поставки и минимальную сумму заказа можно задать выше.",
                    $"Буйрутма үчүн товар жок: тандалган жеткирүүчүдө ({rows.Count} товар) мезгил ичинде сатуу жок жана калдык жетиштүү. Жеткирүү мөөнөтүн жана буйрутманын минималдуу суммасын жогоруда коюуга болот.",
                    $"Nothing to order: the selected supplier ({rows.Count} products) has no sales for the period and enough stock. Lead time and minimum order sum can be set above.",
                    $"Sipariş edilecek ürün yok: seçilen tedarikçinin ({rows.Count} ürün) dönemde satışı yok ve stok yeterli. Teslim süresi ve asgari sipariş tutarı yukarıda ayarlanabilir.",
                    $"Buyurtma uchun mahsulot yo'q: tanlangan yetkazib beruvchida ({rows.Count} mahsulot) davrda sotuv yo'q va qoldiq yetarli. Yetkazish muddati va minimal buyurtma summasini yuqorida belgilash mumkin.")));
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var error = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(SectionCards.ErrorBox(this, error));

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var open = SectionCards.Secondary(T("Автоматизация магазина…", "Дүкөндү автоматташтыруу…", "Shop automation…", "Mağaza otomasyonu…", "Do'kon avtomatlashtirish…"));
        open.Click += async (_, _) => await ShopAutomationWindow.ShowAsync(this).ConfigureAwait(true);
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) =>
        {
            if (!TrySave(error))
                return;
            Saved = true;
            Close();
        };
        var features = ShopAutomationSettings.Current;
        var off = !(features.PlanPackRules || features.PlanLeadTime);
        Content = SectionCards.Shell(Title, off
                ? T("Пока в «Автоматизации магазина» не включены «Кратность упаковки» и «Срок поставки», эти значения только запоминаются.",
                    "«Дүкөндү автоматташтырууда» «Таңгак еселиги» жана «Жеткирүү мөөнөтү» күйгүзүлмөйүнчө бул маанилер сакталат гана.",
                    "Until “Pack multiple” and “Lead time” are on in “Shop automation”, these values are only remembered.",
                    "«Mağaza otomasyonu»nda «Paket katı» ve «Teslim süresi» açılana kadar bu değerler yalnızca saklanır.",
                    "«Do'kon avtomatlashtirish»da «Qadoq karraligi» va «Yetkazish muddati» yoqilmaguncha bu qiymatlar faqat eslab qolinadi.")
                : null,
            panel, SectionCards.Buttons(close, open, save));
    }

    private static Control Row(string label, Control box)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 });
        row.Children.Add(box);
        return row;
    }

    private TextBlock Soft(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
        return tb;
    }

    private bool TrySave(TextBlock error)
    {
        error.IsVisible = false;
        var all = new List<TextBox> { _lead, _minSum };
        all.AddRange(_products.SelectMany(p => new[] { p.Pack, p.Min }));
        if (all.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text) && ShopAutomationText.ParseNumber(b.Text) is not >= 0) is { } bad)
        {
            error.Text = T("Нужно неотрицательное число (или пусто).", "Терс эмес сан керек (же бош).", "A non-negative number (or empty) is required.", "Negatif olmayan bir sayı (veya boş) gerekli.", "Manfiy bo'lmagan son (yoki bo'sh) kerak.");
            error.IsVisible = true;
            bad.Focus();
            return false;
        }
        double N(TextBox b) => ShopAutomationText.ParseNumber(b.Text) ?? 0;
        ShopAutomationSettings.Update(d =>
        {
            var lead = (int)Math.Round(N(_lead));
            var min = N(_minSum);
            if (lead > 0 || min > 0)
                d.Suppliers[_supplier.Id] = new SupplierPlanRule { LeadDays = lead, MinOrderSum = min };
            else
                d.Suppliers.Remove(_supplier.Id);
            foreach (var (id, pack, minQty) in _products)
            {
                if (N(pack) > 0 || N(minQty) > 0)
                    d.Products[id] = new ProductPlanRule { Pack = N(pack), MinQty = N(minQty) };
                else
                    d.Products.Remove(id);
            }
        });
        PosLogger.Log($"Автозаказ v2: правила поставщика «{_supplier.Name}» сохранены (товаров с правилами: {_products.Count(p => N(p.Pack) > 0 || N(p.Min) > 0)}).", "INFO");
        return true;
    }
}

/// <summary>2026-11-05 (волна 2-А, функция 3, этап 0 «Биржи поставщиков»): «Спросить цены у других поставщиков» — один и тот же список товаров уходит
/// запасным поставщикам по очереди в WhatsApp (как отправка рекомендуемого заказа: открывается чат с готовым текстом, отправляет сам человек).
/// Список запасных поставщиков хранится в «Автоматизации магазина» и правится прямо здесь.</summary>
public sealed class AskPricesDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly string _text;
    private readonly TextBox _editor = new() { AcceptsReturn = true, MinHeight = 64, MaxHeight = 110, TextWrapping = TextWrapping.NoWrap };
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly HashSet<string> _sent = new(StringComparer.CurrentCultureIgnoreCase);

    public AskPricesDialog(IEnumerable<(string Name, double Qty, string Unit)> lines)
    {
        Title = T("Спросить цены у других поставщиков", "Башка жеткирүүчүлөрдөн баа сурау", "Ask other suppliers for prices", "Diğer tedarikçilerden fiyat sor", "Boshqa yetkazib beruvchilardan narx so'rash");
        SectionCards.AsModal(this, 640);
        var shop = ClientDocsStore.Profile.Name is { Length: > 0 } own ? own : CompanyInfoService.LastCompany?.Name;
        _text = AutoOrderCheck.AskPricesText(shop, lines);

        var preview = new TextBox { Text = _text, IsReadOnly = true, AcceptsReturn = true, MinHeight = 90, MaxHeight = 150, TextWrapping = TextWrapping.Wrap };
        _editor.Text = ShopAutomationText.FormatReserve(ShopAutomationSettings.Current.ReserveSuppliers);
        var saveList = SectionCards.Secondary(T("Сохранить список", "Тизмени сактоо", "Save the list", "Listeyi kaydet", "Ro'yxatni saqlash"));
        saveList.Click += (_, _) =>
        {
            ShopAutomationSettings.Update(d => d.ReserveSuppliers = ShopAutomationText.ParseReserve(_editor.Text));
            BuildRows();
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Soft(T("Сообщение (цен закупки в нём нет):", "Билдирүү (ичинде сатып алуу баасы жок):", "Message (it has no purchase prices):", "Mesaj (alış fiyatı içermez):", "Xabar (ichida xarid narxi yo'q):")));
        panel.Children.Add(preview);
        panel.Children.Add(Soft(T("Запасные поставщики — по строке: Название; телефон", "Запастагы жеткирүүчүлөр — ар бир сапка: Аты; телефон", "Reserve suppliers — one per line: Name; phone", "Yedek tedarikçiler — satır başına: Ad; telefon", "Zaxira yetkazib beruvchilar — har qatorda: Nomi; telefon")));
        panel.Children.Add(_editor);
        panel.Children.Add(saveList);
        panel.Children.Add(_rows);
        BuildRows();

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title,
            T("Нажмите «WhatsApp» у каждого поставщика: откроется чат с готовым текстом, останется нажать «Отправить». Ответы с ценами сравните сами.",
                "Ар бир жеткирүүчүдө «WhatsApp» басыңыз: даяр тексти бар чат ачылат, «Жөнөтүү» гана калат. Бааларды өзүңүз салыштырыңыз.",
                "Press “WhatsApp” for each supplier: a chat opens with the text ready — just press Send. Compare the price replies yourself.",
                "Her tedarikçide «WhatsApp»a basın: hazır metinle sohbet açılır, yalnızca Gönder'e basın. Fiyat yanıtlarını kendiniz karşılaştırın.",
                "Har bir yetkazib beruvchida «WhatsApp»ni bosing: tayyor matnli chat ochiladi, faqat Yuborish qoladi. Narx javoblarini o'zingiz solishtiring."),
            new ScrollViewer { Content = panel, MaxHeight = 470, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, SectionCards.Buttons(close));
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        var list = ShopAutomationSettings.Current.ReserveSuppliers;
        if (list.Count == 0)
        {
            _rows.Children.Add(Soft(T("Список пуст — впишите поставщиков выше и сохраните.", "Тизме бош — жогоруга жеткирүүчүлөрдү жазып, сактаңыз.", "The list is empty — type suppliers above and save.", "Liste boş — yukarıya tedarikçileri yazıp kaydedin.", "Ro'yxat bo'sh — yuqoriga yetkazib beruvchilarni yozib, saqlang.")));
            return;
        }
        foreach (var r in list)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var done = _sent.Contains(r.Name);
            row.Children.Add(new TextBlock { Text = (done ? "✓ " : "") + r.Name + (r.Phone.Length > 0 ? " · " + r.Phone : T(" · нет телефона", " · телефон жок", " · no phone", " · telefon yok", " · telefon yo'q")), VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            var wa = SectionCards.Secondary("WhatsApp");
            wa.IsEnabled = r.Phone.Length > 0;
            wa.Click += (_, _) =>
            {
                PurchaseDocs.OpenWhatsApp(r.Phone, _text);
                _sent.Add(r.Name);
                PosLogger.Log($"Закупки: запрос цен открыт в WhatsApp — «{r.Name}».", "INFO");
                BuildRows();
            };
            Grid.SetColumn(wa, 1);
            row.Children.Add(wa);
            _rows.Children.Add(row);
        }
    }

    private TextBlock Soft(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushTextSoft"));
        return tb;
    }
}
