using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Зависимости окна «Заказы под клиента» (подменяются в проверках).</summary>
public sealed class PartsOrderDeps
{
    public PartsOrderStore Store { get; init; } = PartsOrderStore.Default;

    /// <summary>«Оформить продажу»: кладёт позиции заказа в чек кассы. null — кнопки нет (программа владельца, у неё нет чека).
    /// Результат — сколько позиций добавлено (0 — не вышло, заказ остаётся как был).</summary>
    public Func<PartsOrder, Task<int>>? SellAsync { get; init; }

    /// <summary>PIN руководителя для отмены заказа с предоплатой: true — принят.</summary>
    public Func<Task<bool>> AskPin { get; init; } = () => ExpiredSaleGuard.AskOwnerPinAsync();

    /// <summary>Открыть ссылку (WhatsApp); false — не открылась.</summary>
    public Func<string, bool> OpenUrl { get; init; } = url => PlatformLauncher.OpenUrl(url);

    public Func<string?> ShopName { get; init; } = () => UserPreferences.Instance.StoreName;

    public Func<bool> StartTariff { get; init; } = () => TariffGate.IsStartTariff;
}

/// <summary>
/// 2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 4.4): список «Заказы под клиента». Слева — поиск и фильтр, справа — карточка заказа и действия:
/// «Отметить: Заказан / Пришёл», «Сообщение в WhatsApp», «Оформить продажу» (позиции в чек; предоплата зачитывается — доплата считается здесь, а в чеке полная
/// сумма), «Изменить», «Отменить заказ» (с предоплатой — только по PIN руководителя). Хранение — <see cref="PartsOrderStore"/> (локально). На тарифе «Старт»
/// при 10+ открытых заказах — только подсказка, ничего не блокируется. Касса: меню ☰; программа владельца: Продажи → Заказы под клиента.
/// </summary>
public sealed class PartsOrdersWindow : Window, IOwnerSection
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly PartsOrderDeps _deps;
    private readonly ModalShell _shell = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new();
    private string? _selectedId;
    private bool _ownerSection;

    public PartsOrdersWindow() : this(null) { }

    public PartsOrdersWindow(PartsOrderDeps? deps)
    {
        _deps = deps ?? new PartsOrderDeps();
        Title = T("Заказы под клиента", "Кардарга заказдар", "Customer orders", "Müşteri siparişleri", "Mijozga buyurtmalar");
        Width = 1040;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => { if (!_ownerSection) this.FitToKassaScreen(); };
        BuildUi();
        Reload();
    }

    public static void Open(Window? owner, PartsOrderDeps? deps = null) => WindowSingleton.ShowOnce(owner, () => new PartsOrdersWindow(deps));

    public void AsOwnerSection()
    {
        _ownerSection = true;
        _shell.ShowClose = false;
        _shell.Footer = null;
        _shell.Subtitle = null;
        _shell.Margin = OwnerSectionLayout.Margin;
        _shell.ScreenHeightShare = 1.0;
        _shell.VerticalAlignment = VerticalAlignment.Stretch;
        _shell.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_FooterGrid")?.Parent is Control footerBand)
                footerBand.IsVisible = false;
        };
    }

    public string StatusMessage => _status.Text ?? "";

    public string? SelectedId => _selectedId;

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;

        var newButton = ServiceDeskUi.Action("PartsOrderNew", T("＋ Новый заказ", "＋ Жаңы заказ", "＋ New order", "＋ Yeni sipariş", "＋ Yangi buyurtma"), () => _ = EditAsync(null), primary: true);
        _search.Name = "PartsOrderSearch";
        _search.Watermark = T("Номер, клиент, телефон, деталь", "Номер, кардар, телефон, тетик", "Number, customer, phone, part", "Numara, müşteri, telefon, parça", "Raqam, mijoz, telefon, detal");
        _search.Height = 40;
        _search.TextChanged += (_, _) => Reload();
        ServiceDeskUi.Use(_search, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(_search, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(_search, BorderBrushProperty, "BrushInputBorder");
        _filter.Name = "PartsOrderFilter";
        _filter.Height = 40;
        _filter.ItemsSource = new[]
        {
            T("Открытые", "Ачык", "Open", "Açık", "Ochiq"), T("Все", "Баары", "All", "Tümü", "Hammasi"),
            PartsOrderRules.StatusName(PartsOrderStatus.New), PartsOrderRules.StatusName(PartsOrderStatus.Ordered), PartsOrderRules.StatusName(PartsOrderStatus.Arrived),
            PartsOrderRules.StatusName(PartsOrderStatus.Issued), PartsOrderRules.StatusName(PartsOrderStatus.Cancelled),
        };
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Reload();

        var tools = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(newButton);
        tools.Children.Add(_search);
        tools.Children.Add(_filter);
        var left = new DockPanel { Width = 330, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        _status.Name = "PartsOrderStatusText";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        _status.Margin = new Thickness(0, 0, 0, 8); // 2026-11-20: подсказка сверху — внизу окна её не видно (прокрутка карточки)
        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var right = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var grid = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(right);
        _shell.Content = grid;

        var close = new Button { Name = "PartsOrdersClose", Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 120, IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        _shell.Footer = close;
        Content = _shell;
    }

    private bool Matches(PartsOrder o)
    {
        var f = _filter.SelectedIndex;
        var byStatus = f switch
        {
            0 => PartsOrderRules.IsOpen(o.Status),
            1 => true,
            2 => o.Status == PartsOrderStatus.New,
            3 => o.Status == PartsOrderStatus.Ordered,
            4 => o.Status == PartsOrderStatus.Arrived,
            5 => o.Status == PartsOrderStatus.Issued,
            _ => o.Status == PartsOrderStatus.Cancelled,
        };
        if (!byStatus)
            return false;
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0 || SearchMatch.Matches(q, "№" + o.Number, o.Number.ToString(), o.ClientName, o.Phone, string.Join(" ", o.Lines.Select(l => l.Title + " " + l.Article)));
    }

    public void Reload()
    {
        var all = _deps.Store.All();
        var rows = all.Where(Matches).ToList();
        _list.Children.Clear();
        if (rows.Count == 0)
            _list.Children.Add(ServiceDeskUi.Text(all.Count == 0
                ? T("Заказов пока нет. Нажмите «＋ Новый заказ».", "Азырынча заказ жок. «＋ Жаңы заказ» басыңыз.", "No orders yet. Press “＋ New order”.", "Henüz sipariş yok. «＋ Yeni sipariş» düğmesine basın.", "Hozircha buyurtma yo'q. «＋ Yangi buyurtma» tugmasini bosing.")
                : T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 13, brush: "BrushTextSoft"));
        foreach (var o in rows)
            _list.Children.Add(BuildRow(o));
        var hint = PartsOrderRules.SoftLimitHint(all.Count(o => PartsOrderRules.IsOpen(o.Status)), _deps.StartTariff());
        var onHand = PartsOrderRules.PrepaymentsOnHand(all);
        _status.Text = (onHand > 0m
                ? T($"Предоплаты по заказам на руках: {MoneyText.Som(onHand)} (в смену не проведены).", $"Заказдар боюнча алдын ала төлөмдөр колдо: {MoneyText.Som(onHand)} (сменага өткөрүлгөн эмес).",
                    $"Prepayments in hand: {MoneyText.Som(onHand)} (not posted to the shift).", $"Elde bulunan sipariş ön ödemeleri: {MoneyText.Som(onHand)} (vardiyaya işlenmedi).",
                    $"Qo'ldagi buyurtma oldindan to'lovlari: {MoneyText.Som(onHand)} (smenaga o'tkazilmagan).") + " "
                : "") + (hint ?? "");
        ShowDetail(all.FirstOrDefault(o => o.Id == _selectedId));
    }

    private Control BuildRow(PartsOrder o)
    {
        var line1 = ServiceDeskUi.Text($"№{o.Number} · {(string.IsNullOrWhiteSpace(o.ClientName) ? o.Phone : o.ClientName)}", 14, true, wrap: false);
        line1.TextTrimming = TextTrimming.CharacterEllipsis;
        var due = o.DueDate is { } d ? " · " + d.ToString("dd.MM.yyyy") : "";
        var line2 = ServiceDeskUi.Text($"{MoneyText.Som(PartsOrderRules.Total(o))}{due}", 12, brush: "BrushTextSoft", wrap: false);
        line2.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        panel.Children.Add(ServiceDeskUi.Chip(PartsOrderRules.StatusName(o.Status), o.Status == PartsOrderStatus.Arrived));
        var b = new Button { Name = "PartsOrderRow_" + o.Number, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6) };
        ServiceDeskUi.Use(b, BackgroundProperty, o.Id == _selectedId ? "BrushAccentSoft" : "BrushPanelSoft");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(b, $"№{o.Number} {o.ClientName} {PartsOrderRules.StatusName(o.Status)}");
        var id = o.Id;
        b.Click += (_, _) => Select(id);
        return b;
    }

    public void Select(string? id)
    {
        _selectedId = id;
        Reload();
    }

    private void Say(string text) => _status.Text = text;

    private void ShowDetail(PartsOrder? o)
    {
        _detail.Children.Clear();
        if (o is null)
        {
            _detail.Children.Add(ServiceDeskUi.Text(T("Выберите заказ слева или оформите новый.", "Сол жактан заказды тандаңыз же жаңысын түзүңүз.", "Pick an order on the left or create a new one.", "Soldan bir sipariş seçin veya yenisini oluşturun.", "Chapdan buyurtmani tanlang yoki yangisini yarating."), 13, brush: "BrushTextSoft"));
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(ServiceDeskUi.Text(T($"Заказ №{o.Number}", $"Заказ №{o.Number}", $"Order No. {o.Number}", $"Sipariş No. {o.Number}", $"Buyurtma №{o.Number}"), 18, true, wrap: false));
        head.Children.Add(ServiceDeskUi.Chip(PartsOrderRules.StatusName(o.Status), o.Status == PartsOrderStatus.Arrived));
        _detail.Children.Add(head);

        var id = o.Id;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (PartsOrderRules.IsOpen(o.Status))
        {
            if (PartsOrderRules.Next(o.Status) is { } next && next != PartsOrderStatus.Issued)
                actions.Children.Add(ServiceDeskUi.Action("PartsOrderAdvance", T("Отметить: ", "Белгилөө: ", "Mark: ", "İşaretle: ", "Belgilash: ") + PartsOrderRules.StatusName(next), () => Advance(id), primary: true));
            if (_deps.SellAsync is not null)
                actions.Children.Add(ServiceDeskUi.Action("PartsOrderSell", T("Оформить продажу", "Сатууну тариздөө", "Process sale", "Satışı yap", "Sotuvni rasmiylashtirish"), () => _ = SellAsync(id), primary: o.Status == PartsOrderStatus.Arrived));
        }
        if (!string.IsNullOrWhiteSpace(o.Phone) && o.Status is PartsOrderStatus.New or PartsOrderStatus.Ordered or PartsOrderStatus.Arrived)
            actions.Children.Add(ServiceDeskUi.Action("PartsOrderWhatsApp", T("Сообщение в WhatsApp", "WhatsApp билдирүүсү", "WhatsApp message", "WhatsApp mesajı", "WhatsApp xabari"), () => WhatsApp(id)));
        if (PartsOrderRules.IsOpen(o.Status))
        {
            actions.Children.Add(ServiceDeskUi.Action("PartsOrderEdit", T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"), () => _ = EditAsync(id)));
            actions.Children.Add(ServiceDeskUi.Action("PartsOrderCancel", T("Отменить заказ", "Заказды жокко чыгаруу", "Cancel order", "Siparişi iptal et", "Buyurtmani bekor qilish"), () => _ = CancelAsync(id), danger: true));
        }
        _detail.Children.Add(actions);

        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fact(string label, string? value)
        {
            var host = new StackPanel { Width = 282, Margin = new Thickness(0, 0, 10, 0) };
            ServiceDeskUi.AddFact(host, label, value);
            if (host.Children.Count > 0)
                facts.Children.Add(host);
        }
        Fact(T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), string.Join(", ", new[] { o.ClientName, o.Phone }.Where(x => !string.IsNullOrWhiteSpace(x))));
        Fact(T("Срок поставки / готовности", "Жеткирүү / даярдоо мөөнөтү", "Delivery / ready date", "Teslim / hazır olma tarihi", "Yetkazish / tayyor bo'lish muddati"), o.DueDate?.ToString("dd.MM.yyyy"));
        Fact(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), o.Supplier);
        Fact(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), o.Comment);
        foreach (var l in o.Lines)
            Fact((l.OnOrder ? T("Под заказ", "Заказ боюнча", "On order", "Siparişle", "Buyurtma bilan") : T("Позиция", "Позиция", "Item", "Kalem", "Pozitsiya")),
                $"{l.Title} — {MoneyText.Qty(l.Qty)} × {MoneyText.Som(l.Price)}");
        var total = PartsOrderRules.Total(o);
        Fact(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), MoneyText.Som(total));
        if (o.Prepayment > 0m)
            Fact(T("Предоплата (получено на руки)", "Алдын ала төлөм (колго алынды)", "Prepayment (received in hand)", "Ön ödeme (elden alındı)", "Oldindan to'lov (qo'lga olindi)"),
                MoneyText.Som(o.Prepayment) + (o.PrepaymentRefunded ? " — " + T("возвращена клиенту", "кардарга кайтарылды", "refunded to the customer", "müşteriye iade edildi", "mijozga qaytarildi") : ""));
        Fact(T("К доплате", "Төлөнө турган", "Balance due", "Kalan tutar", "To'lanadigan qoldiq"), MoneyText.Som(PartsOrderRules.Remainder(o)));
        _detail.Children.Add(facts);
    }

    // ───────────────────────────── действия ─────────────────────────────

    private void Advance(string id)
    {
        if (_deps.Store.Advance(id))
            Reload();
    }

    public async Task EditAsync(string? id)
    {
        var order = id is null ? new PartsOrder() : _deps.Store.Find(id);
        if (order is null)
            return;
        var dialog = new PartsOrderEditDialog(order);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true) != true)
            return;
        _deps.Store.Save(dialog.Order);
        _selectedId = dialog.Order.Id;
        PosLogger.Log($"Заказ под клиента №{dialog.Order.Number}: сохранён (позиций {dialog.Order.Lines.Count}, предоплата {dialog.Order.Prepayment:0.##}).", "SALES");
        Reload();
    }

    private void WhatsApp(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null)
            return;
        var url = DebtWhatsAppLink.BuildUrl(o.Phone, PartsOrderRules.WhatsAppText(o, _deps.ShopName()));
        if (url is null)
        {
            Say(T("Телефон клиента не похож на номер — исправьте его в заказе.", "Кардардын телефону номерге окшошпойт — заказда оңдоңуз.", "The phone does not look like a number — fix it in the order.", "Telefon numaraya benzemiyor — siparişte düzeltin.", "Telefon raqamga o'xshamaydi — buyurtmada tuzating."));
            return;
        }
        Say(_deps.OpenUrl(url)
            ? T("WhatsApp открыт — отправьте сообщение клиенту.", "WhatsApp ачылды — кардарга билдирүүнү жөнөтүңүз.", "WhatsApp is open — send the message to the customer.", "WhatsApp açıldı — mesajı müşteriye gönderin.", "WhatsApp ochildi — xabarni mijozga yuboring.")
            : T("Не удалось открыть WhatsApp.", "WhatsApp ачылган жок.", "Could not open WhatsApp.", "WhatsApp açılamadı.", "WhatsApp ochilmadi."));
    }

    /// <summary>«Оформить продажу»: позиции в чек (полная сумма), предоплата зачитывается — кассиру сообщается, сколько взять из конверта и сколько принять.</summary>
    private async Task SellAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null || _deps.SellAsync is null || !PartsOrderRules.IsOpen(o.Status))
            return;
        var added = await _deps.SellAsync(o).ConfigureAwait(true);
        if (added <= 0)
        {
            Say(T("Позиции не добавлены в чек — проверьте смену и остатки.", "Позициялар чекке кошулган жок — смена менен калдыктарды текшериңиз.", "Items were not added to the receipt — check the shift and stock.", "Kalemler fişe eklenmedi — vardiyayı ve stoğu kontrol edin.", "Pozitsiyalar chekka qo'shilmadi — smena va qoldiqni tekshiring."));
            return;
        }
        var prepay = o.PrepaymentRefunded ? 0m : o.Prepayment;
        var rest = PartsOrderRules.Remainder(o);
        var text = T($"В чек добавлено позиций: {added}. Чек на полную сумму {MoneyText.Som(PartsOrderRules.Total(o))}.", $"Чекке кошулган позициялар: {added}. Чек толук сумма {MoneyText.Som(PartsOrderRules.Total(o))}.",
            $"Items added to the receipt: {added}. The receipt is for the full amount {MoneyText.Som(PartsOrderRules.Total(o))}.", $"Fişe eklenen kalem: {added}. Fiş tam tutar {MoneyText.Som(PartsOrderRules.Total(o))} üzerinden.",
            $"Chekka qo'shilgan pozitsiyalar: {added}. Chek to'liq summa {MoneyText.Som(PartsOrderRules.Total(o))} ga.");
        if (prepay > 0m)
            text += "\n\n" + T($"Предоплата {MoneyText.Som(prepay)} уже получена: возьмите её из конверта и положите в ящик вместе с доплатой {MoneyText.Som(rest)}.",
                $"Алдын ала төлөм {MoneyText.Som(prepay)} алынган: аны конверттен алып, {MoneyText.Som(rest)} калдык менен чогуу кассага салыңыз.",
                $"The prepayment {MoneyText.Som(prepay)} is already received: take it from the envelope and put it in the drawer together with the balance {MoneyText.Som(rest)}.",
                $"Ön ödeme {MoneyText.Som(prepay)} alındı: zarftan alıp kalan {MoneyText.Som(rest)} ile birlikte çekmeceye koyun.",
                $"Oldindan to'lov {MoneyText.Som(prepay)} olingan: uni konvertdan olib, {MoneyText.Som(rest)} qoldiq bilan birga g'aznaga soling.");
        text += "\n\n" + T("Отметить заказ выданным?", "Заказды берилди деп белгилейсизби?", "Mark the order as issued?", "Sipariş teslim edildi olarak işaretlensin mi?", "Buyurtma berildi deb belgilansinmi?");
        var done = await PosDialogs.ConfirmYesNoModalAsync(this, text, T("Оформить продажу", "Сатууну тариздөө", "Process sale", "Satışı yap", "Sotuvni rasmiylashtirish"),
            confirmText: T("Выдан", "Берилди", "Issued", "Teslim edildi", "Berildi"), cancelText: T("Пока нет", "Азырынча жок", "Not yet", "Henüz değil", "Hozircha yo'q")).ConfigureAwait(true);
        if (done)
        {
            _deps.Store.MarkIssued(id);
            PosLogger.Log($"Заказ под клиента №{o.Number}: выдан (чек на {PartsOrderRules.Total(o):0.##}, предоплата {prepay:0.##} зачтена).", "SALES");
        }
        Reload();
    }

    /// <summary>Отмена заказа. С предоплатой — только по PIN руководителя; деньги клиенту отдают из конверта.</summary>
    private async Task CancelAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null || !PartsOrderRules.IsOpen(o.Status))
            return;
        var hasPrepay = o.Prepayment > 0m && !o.PrepaymentRefunded;
        var text = T($"Отменить заказ №{o.Number}?", $"№{o.Number} заказды жокко чыгарасызбы?", $"Cancel order No. {o.Number}?", $"{o.Number} numaralı sipariş iptal edilsin mi?", $"№{o.Number} buyurtma bekor qilinsinmi?");
        if (hasPrepay)
            text += "\n\n" + T($"Клиенту нужно вернуть предоплату {MoneyText.Som(o.Prepayment)}. Отмена — только по PIN руководителя.",
                $"Кардарга алдын ала төлөм {MoneyText.Som(o.Prepayment)} кайтарылат. Жокко чыгаруу — жетекчинин PIN коду менен гана.",
                $"The prepayment {MoneyText.Som(o.Prepayment)} must be returned to the customer. Cancelling needs the manager PIN.",
                $"Ön ödeme {MoneyText.Som(o.Prepayment)} müşteriye iade edilmeli. İptal yalnızca yönetici PIN'i ile.",
                $"Oldindan to'lov {MoneyText.Som(o.Prepayment)} mijozga qaytarilishi kerak. Bekor qilish — faqat rahbar PIN kodi bilan.");
        if (!await PosDialogs.ConfirmYesNoModalAsync(this, text, T("Отмена заказа", "Заказды жокко чыгаруу", "Cancel order", "Sipariş iptali", "Buyurtmani bekor qilish"),
                confirmText: T("Отменить заказ", "Заказды жокко чыгаруу", "Cancel order", "Siparişi iptal et", "Buyurtmani bekor qilish"),
                cancelText: T("Оставить", "Калтыруу", "Keep", "Kalsın", "Qoldirish"), danger: true, enterCancels: true).ConfigureAwait(true))
            return;
        if (hasPrepay && !await _deps.AskPin().ConfigureAwait(true))
        {
            Say(T("PIN не принят — заказ не отменён.", "PIN кабыл алынган жок — заказ жокко чыгарылган жок.", "PIN not accepted — the order was not cancelled.", "PIN kabul edilmedi — sipariş iptal edilmedi.", "PIN qabul qilinmadi — buyurtma bekor qilinmadi."));
            return;
        }
        if (_deps.Store.Cancel(id, null))
        {
            PosLogger.Log($"Заказ под клиента №{o.Number}: отменён{(hasPrepay ? $", предоплата {o.Prepayment:0.##} возвращается клиенту (по PIN руководителя)" : "")}.", "SALES");
            Reload();
            Say(hasPrepay
                ? T($"Заказ отменён. Верните клиенту {MoneyText.Som(o.Prepayment)} из конверта.", $"Заказ жокко чыгарылды. Кардарга {MoneyText.Som(o.Prepayment)} конверттен кайтарыңыз.", $"Order cancelled. Return {MoneyText.Som(o.Prepayment)} to the customer from the envelope.", $"Sipariş iptal edildi. {MoneyText.Som(o.Prepayment)} tutarını zarftan müşteriye iade edin.", $"Buyurtma bekor qilindi. Mijozga {MoneyText.Som(o.Prepayment)} ni konvertdan qaytaring.")
                : T("Заказ отменён.", "Заказ жокко чыгарылды.", "Order cancelled.", "Sipariş iptal edildi.", "Buyurtma bekor qilindi."));
        }
    }
}
