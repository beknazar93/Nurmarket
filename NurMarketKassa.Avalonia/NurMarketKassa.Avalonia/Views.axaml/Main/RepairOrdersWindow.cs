using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Зависимости окна «Ремонт и сервис»: по умолчанию боевые (файл квитанций, касса, принтер чеков, ссылка WhatsApp); тест подставляет свои.</summary>
public sealed class RepairOrdersDeps
{
    public RepairOrderStore Store { get; init; } = RepairOrderStore.Default;
    /// <summary>null — программа владельца (кассы нет): деньги не двигаются, окно — журнал.</summary>
    public IServiceDeskTill? Till { get; init; } = ServiceDeskTill.Resolve();
    public Func<string, Task<string?>> Print { get; init; } = ServiceDeskTill.PrintReceiptText;
    public Func<string?, bool> OpenUrl { get; init; } = PlatformLauncher.OpenUrl;
    public Func<string, string, Task<bool>> Confirm { get; init; } = null!;
    public Func<string> Cashier { get; init; } = () => NurMarketKassa.PosApp.CurrentUserDisplayName ?? "";
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;
}

/// <summary>2026-11-13 (решение владельца: «Ремонт и trade-in — доп. услуги в маркетплейсе»): «Ремонт и сервис» — журнал квитанций приёма в ремонт: приём (клиент, устройство,
/// серийный номер / IMEI, неисправность, комплектация, предоплата, срок), статусы принят → в работе → готов → выдан, печать квитанции и акта выдачи, сообщение клиенту «готово»
/// (чат WhatsApp с готовым текстом — отправляет человек). Деньги — только путями кассы: предоплата и возврат — внесение/изъятие смены, оплата работ — строка «Доп. услуга» в чеке
/// (выдача — после подтверждения «чек оплачен»). Данные — локальный файл (<see cref="RepairOrderStore"/>); сервер ремонт пока не умеет. Окно есть и в программе владельца (журнал).</summary>
public sealed class RepairOrdersWindow : Window, IOwnerSection
{
    private readonly RepairOrdersDeps _deps;
    private readonly ModalShell _shell = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new();
    private string? _selectedId;
    private bool _ownerSection;

    public RepairOrdersWindow() : this(null) { }

    public RepairOrdersWindow(RepairOrdersDeps? deps)
    {
        _deps = deps ?? new RepairOrdersDeps();
        _confirm = _deps.Confirm ?? ((msg, title) => PosDialogs.ConfirmYesNoModalAsync(this, msg, title));
        Title = Tr.T("Ремонт и сервис", "Оңдоо жана тейлөө", "Repair and service", "Tamir ve servis", "Ta'mirlash va servis");
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

    private readonly Func<string, string, Task<bool>> _confirm;

    public static void Open(Window? owner)
    {
        var w = new RepairOrdersWindow();
        if (owner != null)
            w.Show(owner);
        else
            w.Show();
    }

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

    // ───────────────────────── интерфейс ─────────────────────────

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;

        var newButton = ServiceDeskUi.Action("RepairNew", Tr.T("＋ Новый приём", "＋ Жаңы кабыл алуу", "＋ New intake", "＋ Yeni kabul", "＋ Yangi qabul"), () => _ = NewOrderAsync(), primary: true);
        _search.Name = "RepairSearch";
        _search.Watermark = Tr.T("Номер, клиент, телефон, IMEI", "Номер, кардар, телефон, IMEI", "Number, customer, phone, IMEI", "Numara, müşteri, telefon, IMEI", "Raqam, mijoz, telefon, IMEI");
        _search.Height = 40;
        _search.TextChanged += (_, _) => Reload();
        ServiceDeskUi.Use(_search, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(_search, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(_search, BorderBrushProperty, "BrushInputBorder");
        _filter.Name = "RepairFilter";
        _filter.Height = 40;
        _filter.ItemsSource = new[]
        {
            Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"),
            RepairRules.StatusName(RepairStatus.Accepted), RepairRules.StatusName(RepairStatus.InWork), RepairRules.StatusName(RepairStatus.Ready),
            RepairRules.StatusName(RepairStatus.Issued), RepairRules.StatusName(RepairStatus.Cancelled),
        };
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Reload();

        var listTools = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        listTools.Children.Add(newButton);
        listTools.Children.Add(_search);
        listTools.Children.Add(_filter);
        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var left = new DockPanel { Width = 330, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(listTools, Dock.Top);
        left.Children.Add(listTools);
        left.Children.Add(listScroll);

        var right = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _status.Name = "RepairStatusText";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var rightDock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(0, 6, 0, 0);
        rightDock.Children.Add(_status);
        rightDock.Children.Add(right);

        var grid = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(rightDock);
        _shell.Content = grid;

        var close = new Button { Name = "RepairClose", Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 120, IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        _shell.Footer = close;
        Content = _shell;
    }

    private RepairStatus? FilterStatus() => _filter.SelectedIndex switch
    {
        1 => RepairStatus.Accepted, 2 => RepairStatus.InWork, 3 => RepairStatus.Ready, 4 => RepairStatus.Issued, 5 => RepairStatus.Cancelled, _ => null,
    };

    /// <summary>Перечитать файл и перестроить список и карточку.</summary>
    public void Reload()
    {
        var all = _deps.Store.All();
        var rows = RepairOrderStore.Filter(all, _search.Text, FilterStatus()).ToList();
        _list.Children.Clear();
        if (rows.Count == 0)
            _list.Children.Add(ServiceDeskUi.Text(all.Count == 0
                ? Tr.T("Квитанций пока нет. Нажмите «＋ Новый приём».", "Азырынча квитанция жок. «＋ Жаңы кабыл алуу» басыңыз.", "No receipts yet. Press “＋ New intake”.", "Henüz makbuz yok. «＋ Yeni kabul»a basın.", "Hozircha kvitansiya yo'q. «＋ Yangi qabul» ni bosing.")
                : Tr.T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 13, brush: "BrushTextSoft"));
        foreach (var o in rows)
            _list.Children.Add(BuildRow(o));
        ShowDetail(all.FirstOrDefault(o => o.Id == _selectedId));
    }

    private Control BuildRow(RepairOrder o)
    {
        var overdue = RepairRules.IsOverdue(o, _deps.Today());
        var line1 = ServiceDeskUi.Text($"{RepairRules.Code(o)} · {o.Device}", 14, true, wrap: false);
        line1.TextTrimming = TextTrimming.CharacterEllipsis;
        var line2 = ServiceDeskUi.Text($"{o.ClientName} · {o.ClientPhone}", 12, brush: "BrushTextSoft", wrap: false);
        line2.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chipRow.Children.Add(ServiceDeskUi.Chip(RepairRules.StatusName(o.Status), o.Status is RepairStatus.Ready));
        if (overdue)
            chipRow.Children.Add(ServiceDeskUi.Chip(Tr.T("Срок прошёл", "Мөөнөт өттү", "Overdue", "Süre geçti", "Muddat o'tdi"), true));
        panel.Children.Add(chipRow);
        var b = new Button { Name = "Row_" + o.Number, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10) };
        ServiceDeskUi.Use(b, BackgroundProperty, o.Id == _selectedId ? "BrushAccentSoft" : "BrushPanelSoft");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(b, $"{RepairRules.Code(o)} {o.Device} {RepairRules.StatusName(o.Status)}");
        var id = o.Id;
        b.Click += (_, _) => Select(id);
        return b;
    }

    public void Select(string? id)
    {
        _selectedId = id;
        Reload();
    }

    private RepairOrder? Current => _selectedId is null ? null : _deps.Store.Find(_selectedId);

    private void Say(string text) => _status.Text = text;

    private void ShowDetail(RepairOrder? o)
    {
        _detail.Children.Clear();
        if (o is null)
        {
            _detail.Children.Add(ServiceDeskUi.Text(Tr.T("Выберите квитанцию слева или оформите новый приём.", "Сол жактан квитанцияны тандаңыз же жаңы кабыл алуу түзүңүз.", "Pick a receipt on the left or create a new intake.", "Soldan bir makbuz seçin veya yeni kabul oluşturun.", "Chapdan kvitansiyani tanlang yoki yangi qabul rasmiylashtiring."), 14, brush: "BrushTextSoft"));
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(ServiceDeskUi.Text($"{Tr.T("Квитанция", "Квитанция", "Receipt", "Makbuz", "Kvitansiya")} {RepairRules.Code(o)}", 18, true, wrap: false));
        head.Children.Add(ServiceDeskUi.Chip(RepairRules.StatusName(o.Status), o.Status == RepairStatus.Ready));
        _detail.Children.Add(head);

        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fact(string label, string? value)
        {
            var host = new StackPanel { Width = 282, Margin = new Thickness(0, 0, 10, 0) };
            ServiceDeskUi.AddFact(host, label, value);
            if (host.Children.Count > 0)
                facts.Children.Add(host);
        }
        Fact(Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), $"{o.ClientName}, {o.ClientPhone}");
        Fact(Tr.T("Принято", "Кабыл алынды", "Accepted", "Kabul", "Qabul"), $"{o.AcceptedAt:dd.MM.yyyy HH:mm} · {o.AcceptedBy}");
        Fact(Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), o.Device);
        Fact(string.IsNullOrEmpty(o.Serial) ? "S/N / IMEI" : ImeiValidator.LabelFor(o.Serial), o.Serial);
        Fact(Tr.T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"), o.Fault);
        Fact(Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), o.Equipment);
        Fact(Tr.T("Заметка", "Эскертүү", "Note", "Not", "Izoh"), o.Note);
        Fact(Tr.T("Срок", "Мөөнөт", "Due", "Süre", "Muddat"), o.DueDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : null);
        Fact(Tr.T("Ориентир", "Болжол", "Estimate", "Tahmin", "Taxmin"), o.Estimate > 0 ? ServiceDeskUi.Som(o.Estimate) : null);
        Fact(Tr.T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), o.Prepay > 0 ? ServiceDeskUi.Som(o.Prepay) + (o.Refunded > 0 ? " (" + Tr.T("возвращено", "кайтарылды", "refunded", "iade edildi", "qaytarildi") + " " + ServiceDeskUi.Som(o.Refunded) + ")" : "") : null);
        if (o.Status is RepairStatus.Ready or RepairStatus.Issued)
        {
            Fact(Tr.T("Стоимость работ", "Иштин баасы", "Cost of work", "İş bedeli", "Ish narxi"), ServiceDeskUi.Som(o.FinalPrice));
            Fact(Tr.T("К доплате", "Кошумча төлөм", "To pay", "Ödenecek", "To'lanadi"), o.Status == RepairStatus.Ready ? ServiceDeskUi.Som(RepairRules.AmountDue(o.FinalPrice, o.Prepay)) : null);
        }
        if (o.PaymentInCart > 0)
            Fact(Tr.T("В чеке", "Чекте", "In the receipt", "Fişte", "Chekda"), Tr.T($"оплата работ {ServiceDeskUi.Som(o.PaymentInCart)} — ждёт оплаты чека", $"иштин төлөмү {ServiceDeskUi.Som(o.PaymentInCart)} — чектин төлөнүшүн күтөт", $"payment for work {ServiceDeskUi.Som(o.PaymentInCart)} — waiting for the receipt to be paid", $"iş ödemesi {ServiceDeskUi.Som(o.PaymentInCart)} — fiş ödemesi bekleniyor", $"ish to'lovi {ServiceDeskUi.Som(o.PaymentInCart)} — chek to'lanishini kutmoqda"));
        if (o.NotifiedAt is { } n)
            Fact(Tr.T("Клиенту написали", "Кардарга жазылды", "Customer notified", "Müşteriye yazıldı", "Mijozga yozildi"), n.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var id = o.Id;
        if (o.Status == RepairStatus.Accepted)
            actions.Children.Add(ServiceDeskUi.Action("RepairToWork", Tr.T("Взять в работу", "Ишке алуу", "Start work", "İşleme al", "Ishga olish"), () => StartWork(id), primary: true));
        if (o.Status is RepairStatus.Accepted or RepairStatus.InWork)
            actions.Children.Add(ServiceDeskUi.Action("RepairReady", Tr.T("Готово…", "Даяр…", "Ready…", "Hazır…", "Tayyor…"), () => _ = AskReadyAsync(id), primary: o.Status == RepairStatus.InWork));
        if (o.Status == RepairStatus.Ready)
        {
            var due = RepairRules.AmountDue(o.FinalPrice, o.Prepay);
            if (o.PaymentInCart > 0)
            {
                actions.Children.Add(ServiceDeskUi.Action("RepairIssue", Tr.T("Чек оплачен — выдать клиенту", "Чек төлөндү — кардарга берүү", "Receipt paid — hand over", "Fiş ödendi — teslim et", "Chek to'landi — mijozga topshirish"), () => _ = IssueAsync(id), primary: true));
                actions.Children.Add(ServiceDeskUi.Action("RepairUnpay", Tr.T("Убрал строку из чека", "Сапты чектен алып салдым", "Removed the line from the receipt", "Satırı fişten çıkardım", "Qatorni chekdan olib tashladim"), () => ClearPaymentLine(id)));
            }
            else if (due > 0)
                actions.Children.Add(ServiceDeskUi.Action("RepairPay", Tr.T($"Оплатить работы в чеке ({ServiceDeskUi.Som(due)})", $"Иш үчүн чекте төлөө ({ServiceDeskUi.Som(due)})", $"Charge the work in a receipt ({ServiceDeskUi.Som(due)})", $"İşi fişte tahsil et ({ServiceDeskUi.Som(due)})", $"Ish uchun chekda to'lov ({ServiceDeskUi.Som(due)})"), () => _ = AddPaymentToCartAsync(id), primary: true));
            else
                actions.Children.Add(ServiceDeskUi.Action("RepairIssue", Tr.T("Выдать клиенту", "Кардарга берүү", "Hand over", "Teslim et", "Mijozga topshirish"), () => _ = IssueAsync(id), primary: true));
            if (o.PaymentInCart == 0)
                actions.Children.Add(ServiceDeskUi.Action("RepairFixPrice", Tr.T("Изменить стоимость…", "Баасын өзгөртүү…", "Change the price…", "Ücreti değiştir…", "Narxni o'zgartirish…"), () => _ = AskReadyAsync(id)));
            actions.Children.Add(ServiceDeskUi.Action("RepairNotify", Tr.T("Написать клиенту (WhatsApp)", "Кардарга жазуу (WhatsApp)", "Message the customer (WhatsApp)", "Müşteriye yaz (WhatsApp)", "Mijozga yozish (WhatsApp)"), () => Notify(id)));
        }
        if (o.Status == RepairStatus.InWork)
            actions.Children.Add(ServiceDeskUi.Action("RepairNotifyEarly", Tr.T("Написать клиенту (WhatsApp)", "Кардарга жазуу (WhatsApp)", "Message the customer (WhatsApp)", "Müşteriye yaz (WhatsApp)", "Mijozga yozish (WhatsApp)"), () => Notify(id)));
        if (o.Status == RepairStatus.Ready)
            actions.Children.Add(ServiceDeskUi.Action("RepairBackToWork", Tr.T("Вернуть в работу", "Ишке кайтаруу", "Back to work", "İşleme geri al", "Ishga qaytarish"), () => BackToWork(id)));
        actions.Children.Add(ServiceDeskUi.Action("RepairPrintReceipt", Tr.T("Печать квитанции", "Квитанцияны басуу", "Print receipt", "Makbuzu yazdır", "Kvitansiyani chop etish"), () => _ = PrintAsync(id, false)));
        if (o.Status == RepairStatus.Issued)
            actions.Children.Add(ServiceDeskUi.Action("RepairPrintAct", Tr.T("Печать акта выдачи", "Берүү актысын басуу", "Print hand-over act", "Teslim tutanağını yazdır", "Topshirish dalolatnomasini chop etish"), () => _ = PrintAsync(id, true)));
        if (o.Status is RepairStatus.Accepted or RepairStatus.InWork or RepairStatus.Ready)
            actions.Children.Add(ServiceDeskUi.Action("RepairCancel", Tr.T("Отменить приём", "Кабыл алууну жокко чыгаруу", "Cancel intake", "Kabulü iptal et", "Qabulni bekor qilish"), () => _ = CancelAsync(id), danger: true));
        // 2026-11-13: кнопки действий — над данными, чтобы на экране 1024×600 их не приходилось искать прокруткой.
        _detail.Children.Add(actions);
        _detail.Children.Add(facts);
    }

    // ───────────────────────── действия ─────────────────────────

    private string By => _deps.Cashier();

    private string NoTill => Tr.T("Деньги проводятся только в кассе. Откройте этот раздел в кассе (меню → «Ремонт и сервис»).",
        "Акча кассада гана өткөрүлөт. Бул бөлүмдү кассада ачыңыз (меню → «Оңдоо жана тейлөө»).",
        "Money is handled only at the till. Open this section in the till (menu → “Repair and service”).",
        "Para yalnızca kasada işlenir. Bu bölümü kasada açın (menü → «Tamir ve servis»).",
        "Pul faqat kassada o'tkaziladi. Bu bo'limni kassada oching (menyu → «Ta'mirlash va servis»).");

    public async Task NewOrderAsync()
    {
        var dlg = BuildNewForm();
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
        if (dlg.Values.Count == 0)
            return;
        await AcceptAsync(dlg.Values).ConfigureAwait(true);
    }

    /// <summary>Поля формы приёма — по ним же тест собирает значения.</summary>
    public ServiceDeskFormDialog BuildNewForm()
    {
        var canPrepay = _deps.Till is not null;
        var fields = new List<FormField>
        {
            new("name", Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz")),
            new("phone", Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), MaxLength: 30),
            new("device", Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma")),
            new("serial", Tr.T("Серийный номер / IMEI", "Сериялык номер / IMEI", "Serial number / IMEI", "Seri numarası / IMEI", "Seriya raqami / IMEI"), MaxLength: 48,
                Hint: Tr.T("IMEI проверяется по контрольной цифре", "IMEI текшерүүчү сан боюнча текшерилет", "IMEI is checked by its check digit", "IMEI kontrol hanesiyle denetlenir", "IMEI nazorat raqami bo'yicha tekshiriladi")),
            new("fault", Tr.T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"), Multiline: true, Width: 734, MaxLength: 500),
            new("equipment", Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), Width: 734, MaxLength: 300,
                Hint: Tr.T("зарядка, чехол, коробка, SIM…", "заряддагыч, чехол, куту, SIM…", "charger, case, box, SIM…", "şarj aleti, kılıf, kutu, SIM…", "zaryadka, g'ilof, quti, SIM…")),
            new("estimate", Tr.T("Ориентир стоимости, сом", "Болжолдуу баа, сом", "Estimated price, som", "Tahmini ücret, som", "Taxminiy narx, so'm"), MaxLength: 12, Width: 230),
            new("prepay", Tr.T("Предоплата, сом", "Алдын ала төлөм, сом", "Prepayment, som", "Ön ödeme, som", "Oldindan to'lov, so'm"), MaxLength: 12, Width: 230, Enabled: canPrepay,
                Hint: canPrepay ? Tr.T("Принимается в кассу", "Кассага алынат", "Goes into the till", "Kasaya alınır", "Kassaga qabul qilinadi") : Tr.T("Предоплату принимает касса", "Алдын ала төлөмдү касса алат", "The till takes the prepayment", "Ön ödemeyi kasa alır", "Oldindan to'lovni kassa qabul qiladi")),
            new("days", Tr.T("Срок, дней", "Мөөнөт, күн", "Due, days", "Süre, gün", "Muddat, kun"), MaxLength: 3, Width: 230),
        };
        return new ServiceDeskFormDialog(Tr.T("Новый приём в ремонт", "Оңдоого жаңы кабыл алуу", "New repair intake", "Yeni tamir kabulü", "Ta'mirlashga yangi qabul"), null, fields, ValidateForm,
            Tr.T("Принять", "Кабыл алуу", "Accept", "Kabul et", "Qabul qilish"));
    }

    private static RepairOrder FromValues(IReadOnlyDictionary<string, string> v)
    {
        string G(string k) => v.TryGetValue(k, out var s) ? s.Trim() : "";
        return new RepairOrder
        {
            ClientName = G("name"), ClientPhone = G("phone"), Device = G("device"), Serial = G("serial"), Fault = G("fault"), Equipment = G("equipment"),
            Estimate = RepairRules.TryParseMoney(G("estimate")) ?? 0m, Prepay = RepairRules.TryParseMoney(G("prepay")) ?? 0m,
            DueDate = int.TryParse(G("days"), out var days) && days is > 0 and < 400 ? DateTime.Today.AddDays(days) : null,
        };
    }

    private List<string> ValidateForm(IReadOnlyDictionary<string, string> v)
    {
        var problems = RepairRules.ValidateNew(FromValues(v));
        foreach (var key in new[] { "estimate", "prepay" })
            if (v.TryGetValue(key, out var s) && RepairRules.TryParseMoney(s) is null)
                problems.Add(Tr.T("Сумма написана неверно — только цифры.", "Сумма туура эмес жазылган — сандар гана.", "The amount is written incorrectly — digits only.", "Tutar yanlış yazılmış — yalnızca rakam.", "Summa noto'g'ri yozilgan — faqat raqamlar."));
        return problems.Distinct().ToList();
    }

    /// <summary>Принять: записать квитанцию, затем (если указана предоплата) внести её в кассу. Нет предоплаты в кассу — квитанция остаётся без предоплаты, кассир видит причину.</summary>
    public async Task<RepairOrder?> AcceptAsync(IReadOnlyDictionary<string, string> values)
    {
        var draft = FromValues(values);
        if (ValidateForm(values).Count > 0)
            return null;
        var prepay = draft.Prepay;
        draft.Prepay = 0m; // предоплата записывается только после того, как касса её приняла
        var order = _deps.Store.Create(draft, By);
        var message = Tr.T($"Квитанция {RepairRules.Code(order)} оформлена.", $"{RepairRules.Code(order)} квитанциясы түзүлдү.", $"Receipt {RepairRules.Code(order)} created.", $"{RepairRules.Code(order)} makbuzu oluşturuldu.", $"{RepairRules.Code(order)} kvitansiyasi rasmiylashtirildi.");
        if (prepay > 0)
        {
            if (_deps.Till is { } till
                && await till.CashAsync(false, prepay, Tr.T($"Предоплата за ремонт {RepairRules.Code(order)}", $"{RepairRules.Code(order)} оңдоо үчүн алдын ала төлөм", $"Prepayment for repair {RepairRules.Code(order)}", $"{RepairRules.Code(order)} tamiri için ön ödeme", $"{RepairRules.Code(order)} ta'mir uchun oldindan to'lov")).ConfigureAwait(true))
            {
                _deps.Store.Update(order.Id, o => { o.Prepay = prepay; return true; }, By, "prepay");
                message += " " + Tr.T($"Предоплата {ServiceDeskUi.Som(prepay)} внесена в кассу.", $"Алдын ала төлөм {ServiceDeskUi.Som(prepay)} кассага салынды.", $"Prepayment {ServiceDeskUi.Som(prepay)} put into the till.", $"Ön ödeme {ServiceDeskUi.Som(prepay)} kasaya girildi.", $"Oldindan to'lov {ServiceDeskUi.Som(prepay)} kassaga kiritildi.");
            }
            else
                message += " " + Tr.T("Предоплата НЕ внесена в кассу — квитанция записана без неё.", "Алдын ала төлөм кассага САЛЫНГАН ЭМЕС — квитанция аны жок жазылды.", "The prepayment was NOT put into the till — the receipt is saved without it.", "Ön ödeme kasaya GİRİLMEDİ — makbuz onsuz kaydedildi.", "Oldindan to'lov kassaga KIRITILMADI — kvitansiya usiz yozildi.");
        }
        _selectedId = order.Id;
        Reload();
        var printError = await _deps.Print(RepairRules.BuildReceipt(_deps.Store.Find(order.Id) ?? order)).ConfigureAwait(true);
        Say(message + (printError is null ? "" : " " + printError));
        return _deps.Store.Find(order.Id);
    }

    public void StartWork(string id)
    {
        if (_deps.Store.Move(id, RepairStatus.InWork, By))
            Say(Tr.T("Взято в работу.", "Ишке алынды.", "Work started.", "İşleme alındı.", "Ishga olindi."));
        Reload();
    }

    public void BackToWork(string id)
    {
        if (_deps.Store.Find(id) is { PaymentInCart: > 0 })
        {
            Say(Tr.T("Сначала уберите строку оплаты из чека.", "Алгач төлөм сабын чектен алып салыңыз.", "Remove the payment line from the receipt first.", "Önce ödeme satırını fişten çıkarın.", "Avval to'lov qatorini chekdan olib tashlang."));
            return;
        }
        _deps.Store.Move(id, RepairStatus.InWork, By);
        Reload();
    }

    private async Task AskReadyAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null)
            return;
        var initial = (o.FinalPrice > 0 ? o.FinalPrice : o.Estimate).ToString("0.##", CultureInfo.InvariantCulture);
        var dlg = new ServiceDeskFormDialog(Tr.T("Стоимость работ", "Иштин баасы", "Cost of the work", "İş bedeli", "Ish narxi"),
            Tr.T($"Предоплата уже внесена: {ServiceDeskUi.Som(o.Prepay)}. К доплате будет итог минус предоплата.", $"Алдын ала төлөм салынган: {ServiceDeskUi.Som(o.Prepay)}. Кошумча төлөм = жыйынтык минус алдын ала төлөм.", $"Prepayment already taken: {ServiceDeskUi.Som(o.Prepay)}. The amount due is the total minus the prepayment.", $"Ön ödeme alındı: {ServiceDeskUi.Som(o.Prepay)}. Ödenecek tutar = toplam − ön ödeme.", $"Oldindan to'lov olingan: {ServiceDeskUi.Som(o.Prepay)}. To'lanadigan summa = jami − oldindan to'lov."),
            new List<FormField> { new("price", Tr.T("Итог, сом", "Жыйынтык, сом", "Total, som", "Toplam, som", "Jami, so'm"), initial, MaxLength: 12, Width: 300) },
            v => RepairRules.TryParseMoney(v["price"]) is null
                ? new List<string> { Tr.T("Сумма написана неверно — только цифры.", "Сумма туура эмес жазылган — сандар гана.", "The amount is written incorrectly — digits only.", "Tutar yanlış yazılmış — yalnızca rakam.", "Summa noto'g'ri yozilgan — faqat raqamlar.") }
                : new List<string>(),
            Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
        if (dlg.Values.Count == 0)
            return;
        MarkReady(id, RepairRules.TryParseMoney(dlg.Values["price"]) ?? 0m);
    }

    public void MarkReady(string id, decimal finalPrice)
    {
        var o = _deps.Store.Find(id);
        if (o is null)
            return;
        var ok = o.Status == RepairStatus.Ready ? _deps.Store.SetFinalPrice(id, finalPrice, By) : _deps.Store.Move(id, RepairStatus.Ready, By, finalPrice);
        Say(ok ? Tr.T("Отмечено «Готов». Можно написать клиенту.", "«Даяр» деп белгиленди. Кардарга жазса болот.", "Marked “Ready”. You can message the customer.", "«Hazır» işaretlendi. Müşteriye yazabilirsiniz.", "«Tayyor» deb belgilandi. Mijozga yozish mumkin.")
                  : Tr.T("Не удалось изменить: строка оплаты уже в чеке.", "Өзгөртүү мүмкүн болгон жок: төлөм сабы чекте.", "Could not change: the payment line is already in the receipt.", "Değiştirilemedi: ödeme satırı fişte.", "O'zgartirib bo'lmadi: to'lov qatori chekda."));
        Reload();
    }

    /// <summary>Оплата работ — обычной продажей услуги: строка «Доп. услуга» в чек кассы на сумму «итог минус предоплата». Если предоплата больше итога — сначала возврат переплаты из кассы.</summary>
    public async Task AddPaymentToCartAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null || o.Status != RepairStatus.Ready)
            return;
        if (_deps.Till is not { } till)
        {
            Say(NoTill);
            return;
        }
        var due = RepairRules.AmountDue(o.FinalPrice, o.Prepay);
        if (due <= 0)
        {
            Say(Tr.T("Доплата не нужна — можно выдавать.", "Кошумча төлөм керек эмес — берсе болот.", "No extra payment is needed — you can hand it over.", "Ek ödeme gerekmiyor — teslim edebilirsiniz.", "Qo'shimcha to'lov kerak emas — topshirish mumkin."));
            return;
        }
        if (!till.AddLine(Tr.T($"Ремонт {RepairRules.Code(o)}: {o.Device}", $"Оңдоо {RepairRules.Code(o)}: {o.Device}", $"Repair {RepairRules.Code(o)}: {o.Device}", $"Tamir {RepairRules.Code(o)}: {o.Device}", $"Ta'mir {RepairRules.Code(o)}: {o.Device}"), due, credit: false))
        {
            Say(Tr.T("Строка не добавилась в чек.", "Сап чекке кошулган жок.", "The line was not added to the receipt.", "Satır fişe eklenmedi.", "Qator chekka qo'shilmadi."));
            return;
        }
        _deps.Store.MarkPaymentInCart(id, due, By);
        Say(Tr.T($"Оплата работ {ServiceDeskUi.Som(due)} добавлена в чек. Проведите оплату, затем вернитесь сюда и нажмите «Чек оплачен — выдать клиенту».", $"Иштин төлөмү {ServiceDeskUi.Som(due)} чекке кошулду. Төлөмдү өткөрүп, бул жерге кайтып «Чек төлөндү — кардарга берүү» басыңыз.", $"Payment for the work {ServiceDeskUi.Som(due)} was added to the receipt. Take the payment, then come back and press “Receipt paid — hand over”.", $"İş ödemesi {ServiceDeskUi.Som(due)} fişe eklendi. Ödemeyi alın, sonra buraya dönüp «Fiş ödendi — teslim et»e basın.", $"Ish to'lovi {ServiceDeskUi.Som(due)} chekka qo'shildi. To'lovni o'tkazing, so'ng bu yerga qaytib «Chek to'landi — mijozga topshirish» ni bosing."));
        Reload();
    }

    public void ClearPaymentLine(string id)
    {
        _deps.Store.CancelPaymentInCart(id, By);
        Say(Tr.T("Строка оплаты снята. Если она ещё лежит в чеке — удалите её из чека.", "Төлөм сабы алынды. Эгер ал али чекте турса — чектен өчүрүңүз.", "The payment line was released. If it is still in the receipt, delete it there.", "Ödeme satırı kaldırıldı. Hâlâ fişteyse oradan silin.", "To'lov qatori olib tashlandi. Agar u hali chekda turgan bo'lsa — uni chekdan o'chiring."));
        Reload();
    }

    /// <summary>Выдать клиенту: переплата предоплаты возвращается из кассы, доплата уже проведена продажей; затем — статус «Выдан» и акт выдачи.</summary>
    public async Task<bool> IssueAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null || o.Status != RepairStatus.Ready)
            return false;
        var over = RepairRules.Overpaid(o.FinalPrice, o.Prepay) - o.Refunded;
        if (over > 0)
        {
            if (_deps.Till is not { } till)
            {
                Say(NoTill);
                return false;
            }
            if (!await till.CashAsync(true, over, Tr.T($"Возврат переплаты предоплаты по ремонту {RepairRules.Code(o)}", $"{RepairRules.Code(o)} оңдоо боюнча алдын ала төлөмдүн ашыгын кайтаруу", $"Refund of the prepayment overpayment for repair {RepairRules.Code(o)}", $"{RepairRules.Code(o)} tamiri ön ödeme fazlasının iadesi", $"{RepairRules.Code(o)} ta'mir bo'yicha oldindan to'lov ortig'ini qaytarish")).ConfigureAwait(true))
            {
                Say(Tr.T("Возврат переплаты не проведён — выдача отменена.", "Ашыкчаны кайтаруу өткөрүлгөн жок — берүү токтотулду.", "The overpayment refund was not made — hand-over stopped.", "Fazla ödeme iadesi yapılmadı — teslim durduruldu.", "Ortiqcha to'lov qaytarilmadi — topshirish to'xtatildi."));
                return false;
            }
            _deps.Store.RecordRefund(id, over, By);
        }
        if (!await _confirm(Tr.T($"Выдать «{o.Device}» клиенту {o.ClientName}? Оплата проведена, комплектация проверена.", $"«{o.Device}» түзмөгүн {o.ClientName} кардарга бересизби? Төлөм өткөрүлдү, комплект текшерилди.", $"Hand over “{o.Device}” to {o.ClientName}? The payment is done, the items are checked.", $"«{o.Device}» cihazı {o.ClientName} adlı müşteriye teslim edilsin mi? Ödeme yapıldı, teslim edilenler kontrol edildi.", $"«{o.Device}» qurilmasini {o.ClientName} mijozga topshiramizmi? To'lov o'tkazildi, komplektatsiya tekshirildi."),
                Tr.T("Выдача", "Берүү", "Hand-over", "Teslim", "Topshirish")).ConfigureAwait(true))
            return false;
        if (!_deps.Store.Issue(id, By))
        {
            Say(Tr.T("Выдать нельзя: доплата ещё не проведена продажей.", "Берүүгө болбойт: кошумча төлөм сатуу менен өткөрүлө элек.", "Cannot hand over: the extra payment has not been made by a sale yet.", "Teslim edilemez: ek ödeme henüz satışla yapılmadı.", "Topshirib bo'lmaydi: qo'shimcha to'lov hali sotuv bilan o'tkazilmagan."));
            Reload();
            return false;
        }
        var issued = _deps.Store.Find(id)!;
        var printError = await _deps.Print(RepairRules.BuildIssueAct(issued)).ConfigureAwait(true);
        Say(Tr.T("Выдано клиенту.", "Кардарга берилди.", "Handed over.", "Müşteriye teslim edildi.", "Mijozga topshirildi.") + (printError is null ? "" : " " + printError));
        Reload();
        return true;
    }

    /// <summary>Отмена приёма: предоплата возвращается из кассы (изъятие), затем статус «Отменён».</summary>
    public async Task<bool> CancelAsync(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null || RepairRules.IsFinal(o.Status))
            return false;
        if (o.PaymentInCart > 0)
        {
            Say(Tr.T("Сначала уберите строку оплаты из чека.", "Алгач төлөм сабын чектен алып салыңыз.", "Remove the payment line from the receipt first.", "Önce ödeme satırını fişten çıkarın.", "Avval to'lov qatorini chekdan olib tashlang."));
            return false;
        }
        if (!await _confirm(Tr.T($"Отменить приём {RepairRules.Code(o)}?{(RepairRules.PrepayToRefund(o) > 0 ? " Предоплата " + ServiceDeskUi.Som(RepairRules.PrepayToRefund(o)) + " будет возвращена из кассы." : "")}", $"{RepairRules.Code(o)} кабыл алууну жокко чыгарасызбы?{(RepairRules.PrepayToRefund(o) > 0 ? " Алдын ала төлөм " + ServiceDeskUi.Som(RepairRules.PrepayToRefund(o)) + " кассадан кайтарылат." : "")}", $"Cancel intake {RepairRules.Code(o)}?{(RepairRules.PrepayToRefund(o) > 0 ? " The prepayment " + ServiceDeskUi.Som(RepairRules.PrepayToRefund(o)) + " will be refunded from the till." : "")}", $"{RepairRules.Code(o)} kabulü iptal edilsin mi?{(RepairRules.PrepayToRefund(o) > 0 ? " Ön ödeme " + ServiceDeskUi.Som(RepairRules.PrepayToRefund(o)) + " kasadan iade edilecek." : "")}", $"{RepairRules.Code(o)} qabulini bekor qilamizmi?{(RepairRules.PrepayToRefund(o) > 0 ? " Oldindan to'lov " + ServiceDeskUi.Som(RepairRules.PrepayToRefund(o)) + " kassadan qaytariladi." : "")}"),
                Tr.T("Отмена приёма", "Кабыл алууну жокко чыгаруу", "Cancel intake", "Kabulü iptal", "Qabulni bekor qilish")).ConfigureAwait(true))
            return false;
        var refund = RepairRules.PrepayToRefund(o);
        if (refund > 0)
        {
            if (_deps.Till is not { } till)
            {
                Say(NoTill);
                return false;
            }
            if (!await till.CashAsync(true, refund, Tr.T($"Возврат предоплаты по ремонту {RepairRules.Code(o)} (отмена)", $"{RepairRules.Code(o)} оңдоо боюнча алдын ала төлөмдү кайтаруу (жокко чыгаруу)", $"Refund of the prepayment for repair {RepairRules.Code(o)} (cancelled)", $"{RepairRules.Code(o)} tamiri ön ödeme iadesi (iptal)", $"{RepairRules.Code(o)} ta'mir bo'yicha oldindan to'lovni qaytarish (bekor)")).ConfigureAwait(true))
            {
                Say(Tr.T("Предоплата не возвращена — приём не отменён.", "Алдын ала төлөм кайтарылган жок — кабыл алуу жокко чыгарылган жок.", "The prepayment was not refunded — the intake was not cancelled.", "Ön ödeme iade edilmedi — kabul iptal edilmedi.", "Oldindan to'lov qaytarilmadi — qabul bekor qilinmadi."));
                return false;
            }
            _deps.Store.RecordRefund(id, refund, By);
        }
        var ok = _deps.Store.Cancel(id, By);
        Say(ok ? Tr.T("Приём отменён.", "Кабыл алуу жокко чыгарылды.", "Intake cancelled.", "Kabul iptal edildi.", "Qabul bekor qilindi.") : Tr.T("Отменить не удалось.", "Жокко чыгаруу мүмкүн болгон жок.", "Could not cancel.", "İptal edilemedi.", "Bekor qilib bo'lmadi."));
        Reload();
        return ok;
    }

    /// <summary>Открывает чат WhatsApp с готовым текстом «готово»; сообщение уходит, только если человек сам нажмёт «Отправить».</summary>
    public bool Notify(string id)
    {
        var o = _deps.Store.Find(id);
        if (o is null)
            return false;
        var url = DebtWhatsAppLink.BuildUrl(o.ClientPhone, RepairRules.ReadyMessage(o));
        if (url is null)
        {
            Say(Tr.T("Номер клиента не похож на телефонный — исправьте его.", "Кардардын номери телефонго окшобойт — оңдоңуз.", "The customer's number does not look like a phone number — fix it.", "Müşterinin numarası telefon numarasına benzemiyor — düzeltin.", "Mijoz raqami telefon raqamiga o'xshamaydi — tuzating."));
            return false;
        }
        if (!_deps.OpenUrl(url))
        {
            Say(Tr.T("Чат не открылся.", "Чат ачылган жок.", "The chat did not open.", "Sohbet açılmadı.", "Chat ochilmadi."));
            return false;
        }
        _deps.Store.MarkNotified(id, By);
        Say(Tr.T("Чат открыт — нажмите «Отправить» в WhatsApp.", "Чат ачылды — WhatsApp'та «Жөнөтүү» басыңыз.", "The chat is open — press Send in WhatsApp.", "Sohbet açıldı — WhatsApp'ta Gönder'e basın.", "Chat ochildi — WhatsApp'da «Yuborish» ni bosing."));
        Reload();
        return true;
    }

    public async Task PrintAsync(string id, bool act)
    {
        var o = _deps.Store.Find(id);
        if (o is null)
            return;
        var error = await _deps.Print(act ? RepairRules.BuildIssueAct(o) : RepairRules.BuildReceipt(o)).ConfigureAwait(true);
        Say(error ?? Tr.T("Напечатано.", "Басылып чыкты.", "Printed.", "Yazdırıldı.", "Chop etildi."));
    }
}
