using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Зависимости окна «Trade-in / скупка»: по умолчанию боевые; тест подставляет свои (кассу, принтер, приход на склад).</summary>
public sealed class TradeInDeps
{
    public TradeInStore Store { get; init; } = TradeInStore.Default;
    /// <summary>null — программа владельца (кассы нет): деньги не двигаются, окно — журнал.</summary>
    public IServiceDeskTill? Till { get; init; } = ServiceDeskTill.Resolve();
    public Func<string, Task<string?>> Print { get; init; } = ServiceDeskTill.PrintReceiptText;
    public Func<string, string, Task<bool>> Confirm { get; init; } = null!;
    public Func<string> Cashier { get; init; } = () => NurMarketKassa.PosApp.CurrentUserDisplayName ?? "";
    /// <summary>Приход б/у на склад обычной приёмкой без поставщика (остаток и цены; денег и долга не создаёт). Возвращает (успех, текст ошибки).</summary>
    public Func<ReceivingLineVm, Task<(bool Ok, string Error)>> Stock { get; init; } = StockViaReceiving;

    private static async Task<(bool Ok, string Error)> StockViaReceiving(ReceivingLineVm line)
    {
        try
        {
            var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, supplier: null, paidNow: false, employee: NurMarketKassa.PosApp.CurrentUserDisplayName).ConfigureAwait(true);
            return result.Errors.Count == 0 && result.PostedLines.Count > 0 ? (true, "") : (false, string.Join("; ", result.Errors));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>2026-11-13 (решение владельца: «Ремонт и trade-in — доп. услуги в маркетплейсе»): «Trade-in / скупка» — журнал выкупов б/у у клиентов: оценка, данные клиента (паспорт),
/// серийный номер / IMEI, состояние, фото по желанию, сумма. Расчёт с клиентом — только путями кассы: выплата = изъятие из кассы смены с основанием, зачёт в покупку = отрицательная
/// строка «Доп. услуга» в чеке (итог чека не уходит в минус; зачёт считается состоявшимся после подтверждения «чек оплачен»). Выкупленное оприходуется на склад как б/у обычной приёмкой
/// без поставщика (закупка = сумма выкупа). Данные — локальный файл (<see cref="TradeInStore"/>); сервер trade-in пока не умеет.</summary>
public sealed class TradeInWindow : Window, IOwnerSection
{
    private readonly TradeInDeps _deps;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly ModalShell _shell = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new();
    private string? _selectedId;
    private bool _ownerSection;

    public TradeInWindow() : this(null) { }

    public TradeInWindow(TradeInDeps? deps)
    {
        _deps = deps ?? new TradeInDeps();
        _confirm = _deps.Confirm ?? ((msg, title) => PosDialogs.ConfirmYesNoModalAsync(this, msg, title));
        Title = Tr.T("Trade-in / скупка", "Trade-in / сатып алуу", "Trade-in / buy-back", "Trade-in / alım", "Trade-in / sotib olish");
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

    public static void Open(Window? owner)
    {
        var w = new TradeInWindow();
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

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;

        var newButton = ServiceDeskUi.Action("TradeNew", Tr.T("＋ Новый выкуп", "＋ Жаңы сатып алуу", "＋ New buy-back", "＋ Yeni alım", "＋ Yangi sotib olish"), () => _ = NewRecordAsync(), primary: true);
        _search.Name = "TradeSearch";
        _search.Watermark = Tr.T("Номер, клиент, телефон, IMEI", "Номер, кардар, телефон, IMEI", "Number, customer, phone, IMEI", "Numara, müşteri, telefon, IMEI", "Raqam, mijoz, telefon, IMEI");
        _search.Height = 40;
        _search.TextChanged += (_, _) => Reload();
        ServiceDeskUi.Use(_search, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(_search, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(_search, BorderBrushProperty, "BrushInputBorder");
        _filter.Name = "TradeFilter";
        _filter.Height = 40;
        _filter.ItemsSource = new[]
        {
            Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"),
            TradeInRules.StatusName(TradeInStatus.Appraised), TradeInRules.StatusName(TradeInStatus.Bought),
            TradeInRules.StatusName(TradeInStatus.Stocked), TradeInRules.StatusName(TradeInStatus.Cancelled),
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

        _status.Name = "TradeStatusText";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        _status.Margin = new Thickness(0, 6, 0, 0);
        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var rightDock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        rightDock.Children.Add(_status);
        rightDock.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var grid = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(rightDock);
        _shell.Content = grid;

        var close = new Button { Name = "TradeClose", Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 120, IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        _shell.Footer = close;
        Content = _shell;
    }

    private TradeInStatus? FilterStatus() => _filter.SelectedIndex switch
    {
        1 => TradeInStatus.Appraised, 2 => TradeInStatus.Bought, 3 => TradeInStatus.Stocked, 4 => TradeInStatus.Cancelled, _ => null,
    };

    public void Reload()
    {
        var all = _deps.Store.All();
        var rows = TradeInStore.Filter(all, _search.Text, FilterStatus()).ToList();
        _list.Children.Clear();
        if (rows.Count == 0)
            _list.Children.Add(ServiceDeskUi.Text(all.Count == 0
                ? Tr.T("Выкупов пока нет. Нажмите «＋ Новый выкуп».", "Азырынча сатып алуу жок. «＋ Жаңы сатып алуу» басыңыз.", "No buy-backs yet. Press “＋ New buy-back”.", "Henüz alım yok. «＋ Yeni alım»a basın.", "Hozircha sotib olish yo'q. «＋ Yangi sotib olish» ni bosing.")
                : Tr.T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 13, brush: "BrushTextSoft"));
        foreach (var r in rows)
            _list.Children.Add(BuildRow(r));
        ShowDetail(all.FirstOrDefault(r => r.Id == _selectedId));
    }

    private Control BuildRow(TradeInRecord r)
    {
        var line1 = ServiceDeskUi.Text($"{TradeInRules.Code(r)} · {r.Device}", 14, true, wrap: false);
        line1.TextTrimming = TextTrimming.CharacterEllipsis;
        var line2 = ServiceDeskUi.Text($"{r.ClientName} · {ServiceDeskUi.Som(r.Amount)}", 12, brush: "BrushTextSoft", wrap: false);
        line2.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ServiceDeskUi.Chip(TradeInRules.StatusName(r.Status), r.Status == TradeInStatus.Bought));
        if (r.CreditAdded > 0)
            chips.Children.Add(ServiceDeskUi.Chip(Tr.T("Зачёт ждёт чека", "Эсептөө чекти күтүүдө", "Credit waits for the receipt", "Mahsup fişi bekliyor", "Hisobga olish chekni kutmoqda"), true));
        panel.Children.Add(chips);
        var b = new Button { Name = "Row_" + r.Number, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10) };
        ServiceDeskUi.Use(b, BackgroundProperty, r.Id == _selectedId ? "BrushAccentSoft" : "BrushPanelSoft");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(b, $"{TradeInRules.Code(r)} {r.Device} {TradeInRules.StatusName(r.Status)}");
        var id = r.Id;
        b.Click += (_, _) => Select(id);
        return b;
    }

    public void Select(string? id)
    {
        _selectedId = id;
        Reload();
    }

    private void Say(string text) => _status.Text = text;

    private string By => _deps.Cashier();

    private string NoTill => Tr.T("Деньги проводятся только в кассе. Откройте этот раздел в кассе (меню → «Trade-in / скупка»).",
        "Акча кассада гана өткөрүлөт. Бул бөлүмдү кассада ачыңыз (меню → «Trade-in / сатып алуу»).",
        "Money is handled only at the till. Open this section in the till (menu → “Trade-in / buy-back”).",
        "Para yalnızca kasada işlenir. Bu bölümü kasada açın (menü → «Trade-in / alım»).",
        "Pul faqat kassada o'tkaziladi. Bu bo'limni kassada oching (menyu → «Trade-in / sotib olish»).");

    private void ShowDetail(TradeInRecord? r)
    {
        _detail.Children.Clear();
        if (r is null)
        {
            _detail.Children.Add(ServiceDeskUi.Text(Tr.T("Выберите выкуп слева или оформите новый.", "Сол жактан сатып алууну тандаңыз же жаңысын түзүңүз.", "Pick a buy-back on the left or create a new one.", "Soldan bir alım seçin veya yenisini oluşturun.", "Chapdan sotib olishni tanlang yoki yangisini rasmiylashtiring."), 14, brush: "BrushTextSoft"));
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(ServiceDeskUi.Text($"{Tr.T("Выкуп", "Сатып алуу", "Buy-back", "Alım", "Sotib olish")} {TradeInRules.Code(r)}", 18, true, wrap: false));
        head.Children.Add(ServiceDeskUi.Chip(TradeInRules.StatusName(r.Status), r.Status == TradeInStatus.Bought));
        _detail.Children.Add(head);

        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fact(string label, string? value)
        {
            var host = new StackPanel { Width = 282, Margin = new Thickness(0, 0, 10, 0) };
            ServiceDeskUi.AddFact(host, label, value);
            if (host.Children.Count > 0)
                facts.Children.Add(host);
        }
        Fact(Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), $"{r.ClientName}, {r.ClientPhone}");
        Fact(Tr.T("Паспорт / ID", "Паспорт / ID", "Passport / ID", "Pasaport / Kimlik", "Pasport / ID"), r.ClientDocument);
        Fact(Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), r.Device);
        Fact(ImeiValidator.LabelFor(r.Serial), r.Serial);
        Fact(Tr.T("Состояние", "Абалы", "Condition", "Durum", "Holati"), r.Condition);
        Fact(Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), r.Equipment);
        Fact(Tr.T("Оценка", "Баа", "Appraisal", "Değerleme", "Baho"), r.Appraisal > 0 ? ServiceDeskUi.Som(r.Appraisal) : null);
        Fact(Tr.T("Сумма выкупа", "Сатып алуу суммасы", "Buy-back amount", "Alım tutarı", "Sotib olish summasi"), ServiceDeskUi.Som(r.Amount));
        Fact(Tr.T("Выплачено из кассы", "Кассадан төлөндү", "Paid out from the till", "Kasadan ödendi", "Kassadan to'landi"), r.PaidOut > 0 ? ServiceDeskUi.Som(r.PaidOut) : null);
        Fact(Tr.T("Зачтено в покупку", "Сатып алууга эсептелди", "Credited to a purchase", "Alışverişe mahsup edildi", "Xaridga hisobga olindi"), r.CreditConfirmed > 0 ? ServiceDeskUi.Som(r.CreditConfirmed) : null);
        Fact(Tr.T("Зачёт в чеке (ждёт оплаты)", "Чектеги эсептөө (төлөмдү күтүүдө)", "Credit in the receipt (waiting for payment)", "Fişteki mahsup (ödeme bekliyor)", "Chekdagi hisobga olish (to'lovni kutmoqda)"), r.CreditAdded > 0 ? ServiceDeskUi.Som(r.CreditAdded) : null);
        Fact(Tr.T("Осталось рассчитаться", "Эсептешүү калды", "Left to settle", "Kalan ödeme", "Hisob-kitobga qoldi"), r.Status == TradeInStatus.Appraised ? ServiceDeskUi.Som(TradeInRules.Outstanding(r)) : null);
        Fact(Tr.T("Цена продажи", "Сатуу баасы", "Resale price", "Satış fiyatı", "Sotuv narxi"), r.ResalePrice > 0 ? ServiceDeskUi.Som(r.ResalePrice) : null);
        Fact(Tr.T("Фото", "Сүрөт", "Photos", "Fotoğraf", "Rasm"), r.Photos.Count > 0 ? r.Photos.Count.ToString(CultureInfo.InvariantCulture) : null);

        var id = r.Id;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var payout = TradeInRules.PayoutLimit(r);
        if (payout > 0)
            actions.Children.Add(ServiceDeskUi.Action("TradePayout", Tr.T($"Выплатить из кассы {ServiceDeskUi.Som(payout)}", $"Кассадан төлөө {ServiceDeskUi.Som(payout)}", $"Pay out from the till {ServiceDeskUi.Som(payout)}", $"Kasadan öde {ServiceDeskUi.Som(payout)}", $"Kassadan to'lash {ServiceDeskUi.Som(payout)}"), () => _ = PayoutAsync(id), primary: true));
        if (r.Status == TradeInStatus.Appraised && r.CreditAdded == 0 && TradeInRules.Outstanding(r) > 0)
            actions.Children.Add(ServiceDeskUi.Action("TradeCredit", Tr.T("Зачесть в покупку (в чек)", "Сатып алууга эсептөө (чекке)", "Credit to a purchase (in the receipt)", "Alışverişe mahsup et (fişe)", "Xaridga hisobga olish (chekka)"), () => _ = AddCreditAsync(id)));
        if (r.CreditAdded > 0)
        {
            actions.Children.Add(ServiceDeskUi.Action("TradeCreditConfirm", Tr.T("Чек оплачен — зачёт состоялся", "Чек төлөндү — эсептөө болду", "Receipt paid — credit done", "Fiş ödendi — mahsup tamam", "Chek to'landi — hisobga olish bo'ldi"), () => _ = ConfirmCreditAsync(id), primary: true));
            actions.Children.Add(ServiceDeskUi.Action("TradeCreditCancel", Tr.T("Убрал зачёт из чека", "Эсептөөнү чектен алып салдым", "Removed the credit from the receipt", "Mahsubu fişten çıkardım", "Hisobga olishni chekdan olib tashladim"), () => CancelCredit(id)));
        }
        if (TradeInRules.CanStock(r))
            actions.Children.Add(ServiceDeskUi.Action("TradeStock", Tr.T("Оприходовать на склад как б/у…", "Кампага б/у катары кирүү…", "Receive into stock as used…", "İkinci el olarak depoya al…", "Omborga b/u sifatida kirim qilish…"), () => _ = AskStockAsync(id), primary: true));
        actions.Children.Add(ServiceDeskUi.Action("TradePrintAct", Tr.T("Печать акта", "Актыны басуу", "Print the act", "Tutanağı yazdır", "Dalolatnomani chop etish"), () => _ = PrintAsync(id)));
        if (r.Photos.Count > 0)
            actions.Children.Add(ServiceDeskUi.Action("TradeOpenPhoto", Tr.T("Открыть фото", "Сүрөттү ачуу", "Open photo", "Fotoğrafı aç", "Rasmni ochish"), () => PlatformLauncher.OpenFile(r.Photos[0])));
        if (r.Status is TradeInStatus.Appraised or TradeInStatus.Bought)
            actions.Children.Add(ServiceDeskUi.Action("TradeAddPhoto", Tr.T("Добавить фото…", "Сүрөт кошуу…", "Add photo…", "Fotoğraf ekle…", "Rasm qo'shish…"), () => _ = AddPhotoAsync(id)));
        if (TradeInRules.CanCancel(r))
            actions.Children.Add(ServiceDeskUi.Action("TradeCancel", Tr.T("Отменить выкуп", "Сатып алууну жокко чыгаруу", "Cancel the buy-back", "Alımı iptal et", "Sotib olishni bekor qilish"), () => _ = CancelAsync(id), danger: true));
        // 2026-11-13: кнопки действий — над данными, чтобы на экране 1024×600 их не приходилось искать прокруткой.
        _detail.Children.Add(actions);
        _detail.Children.Add(facts);
    }

    // ───────────────────────── форма нового выкупа ─────────────────────────

    public async Task NewRecordAsync()
    {
        var dlg = BuildNewForm();
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
        if (dlg.Values.Count == 0)
            return;
        await CreateAsync(dlg.Values).ConfigureAwait(true);
    }

    public ServiceDeskFormDialog BuildNewForm()
    {
        var fields = new List<FormField>
        {
            new("name", Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz")),
            new("phone", Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), MaxLength: 30),
            new("document", Tr.T("Паспорт / ID: серия, номер, кем и когда выдан", "Паспорт / ID: серия, номер, ким жана качан берген", "Passport / ID: series, number, issuer and date", "Pasaport / Kimlik: seri, numara, veren makam ve tarih", "Pasport / ID: seriya, raqam, kim va qachon bergan"), Width: 734, MaxLength: 200),
            new("device", Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma")),
            new("serial", Tr.T("Серийный номер / IMEI", "Сериялык номер / IMEI", "Serial number / IMEI", "Seri numarası / IMEI", "Seriya raqami / IMEI"), MaxLength: 48),
            new("condition", Tr.T("Состояние (царапины, работает ли, аккаунт снят)", "Абалы (чийилген жерлери, иштейби, аккаунт чыгарылганбы)", "Condition (scratches, works or not, account removed)", "Durum (çizikler, çalışıyor mu, hesap kaldırıldı mı)", "Holati (chiziqlar, ishlaydimi, akkaunt o'chirilganmi)"), Multiline: true, Width: 734, MaxLength: 400),
            new("equipment", Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), Width: 734, MaxLength: 300),
            new("appraisal", Tr.T("Оценка, сом", "Баа, сом", "Appraisal, som", "Değerleme, som", "Baho, so'm"), MaxLength: 12, Width: 230),
            new("amount", Tr.T("Сумма выкупа, сом", "Сатып алуу суммасы, сом", "Buy-back amount, som", "Alım tutarı, som", "Sotib olish summasi, so'm"), MaxLength: 12, Width: 230),
        };
        return new ServiceDeskFormDialog(Tr.T("Новый выкуп (trade-in)", "Жаңы сатып алуу (trade-in)", "New buy-back (trade-in)", "Yeni alım (trade-in)", "Yangi sotib olish (trade-in)"),
            Tr.T("Принимаем б/у у физического лица. После сохранения: выплатить из кассы или зачесть в покупку.", "Жеке адамдан б/у кабыл алабыз. Сактагандан кийин: кассадан төлөө же сатып алууга эсептөө.", "We accept a used item from an individual. After saving: pay from the till or credit to a purchase.", "Bireyden ikinci el alıyoruz. Kaydettikten sonra: kasadan öde veya alışverişe mahsup et.", "Jismoniy shaxsdan b/u qabul qilamiz. Saqlagach: kassadan to'lash yoki xaridga hisobga olish."),
            fields, ValidateForm, Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
    }

    private static TradeInRecord FromValues(IReadOnlyDictionary<string, string> v)
    {
        string G(string k) => v.TryGetValue(k, out var s) ? s.Trim() : "";
        return new TradeInRecord
        {
            ClientName = G("name"), ClientPhone = G("phone"), ClientDocument = G("document"), Device = G("device"), Serial = G("serial"),
            Condition = G("condition"), Equipment = G("equipment"),
            Appraisal = RepairRules.TryParseMoney(G("appraisal")) ?? 0m, Amount = RepairRules.TryParseMoney(G("amount")) ?? 0m,
        };
    }

    private List<string> ValidateForm(IReadOnlyDictionary<string, string> v)
    {
        var problems = TradeInRules.ValidateNew(FromValues(v));
        foreach (var key in new[] { "appraisal", "amount" })
            if (v.TryGetValue(key, out var s) && RepairRules.TryParseMoney(s) is null)
                problems.Add(Tr.T("Сумма написана неверно — только цифры.", "Сумма туура эмес жазылган — сандар гана.", "The amount is written incorrectly — digits only.", "Tutar yanlış yazılmış — yalnızca rakam.", "Summa noto'g'ri yozilgan — faqat raqamlar."));
        return problems.Distinct().ToList();
    }

    public async Task<TradeInRecord?> CreateAsync(IReadOnlyDictionary<string, string> values)
    {
        if (ValidateForm(values).Count > 0)
            return null;
        var rec = _deps.Store.Create(FromValues(values), By);
        _selectedId = rec.Id;
        var message = Tr.T($"Выкуп {TradeInRules.Code(rec)} записан. Деньги ещё не двигались: выплатите из кассы или зачтите в покупку.", $"{TradeInRules.Code(rec)} сатып алуу жазылды. Акча али кыймылдаган жок: кассадан төлөңүз же сатып алууга эсептеңиз.", $"Buy-back {TradeInRules.Code(rec)} saved. No money has moved yet: pay from the till or credit to a purchase.", $"{TradeInRules.Code(rec)} alımı kaydedildi. Para henüz hareket etmedi: kasadan ödeyin veya alışverişe mahsup edin.", $"{TradeInRules.Code(rec)} sotib olish yozildi. Pul hali harakatlanmadi: kassadan to'lang yoki xaridga hisobga oling.");
        Reload();
        var printError = await _deps.Print(TradeInRules.BuildAct(rec)).ConfigureAwait(true);
        Say(message + (printError is null ? "" : " " + printError));
        return rec;
    }

    // ───────────────────────── деньги ─────────────────────────

    private static string Reason(string kind, TradeInRecord r) => kind switch
    {
        "payout" => Tr.T($"Выкуп trade-in {TradeInRules.Code(r)}: {r.Device}", $"Trade-in {TradeInRules.Code(r)} сатып алуу: {r.Device}", $"Trade-in buy-back {TradeInRules.Code(r)}: {r.Device}", $"Trade-in alımı {TradeInRules.Code(r)}: {r.Device}", $"Trade-in sotib olish {TradeInRules.Code(r)}: {r.Device}"),
        _ => Tr.T($"Зачёт trade-in {TradeInRules.Code(r)}: {r.Device}", $"Trade-in {TradeInRules.Code(r)} эсептөө: {r.Device}", $"Trade-in credit {TradeInRules.Code(r)}: {r.Device}", $"Trade-in mahsubu {TradeInRules.Code(r)}: {r.Device}", $"Trade-in hisobga olish {TradeInRules.Code(r)}: {r.Device}"),
    };

    /// <summary>Выплатить остаток из кассы: изъятие смены с основанием «Выкуп trade-in …» (касса сама проверяет, что в ящике хватает денег, и печатает расходный чек).</summary>
    public async Task<bool> PayoutAsync(string id)
    {
        var r = _deps.Store.Find(id);
        if (r is null)
            return false;
        var amount = TradeInRules.PayoutLimit(r);
        if (amount <= 0)
            return false;
        if (_deps.Till is not { } till)
        {
            Say(NoTill);
            return false;
        }
        if (!await till.CashAsync(true, amount, Reason("payout", r)).ConfigureAwait(true))
        {
            Say(Tr.T("Выплата не проведена — выкуп без изменений.", "Төлөм өткөрүлгөн жок — сатып алуу өзгөргөн жок.", "The payout was not made — the buy-back is unchanged.", "Ödeme yapılmadı — alım değişmedi.", "To'lov o'tkazilmadi — sotib olish o'zgarmadi."));
            return false;
        }
        _deps.Store.RecordPayout(id, amount, By);
        Say(Tr.T($"Выплачено из кассы {ServiceDeskUi.Som(amount)}.", $"Кассадан төлөндү {ServiceDeskUi.Som(amount)}.", $"Paid out from the till: {ServiceDeskUi.Som(amount)}.", $"Kasadan ödendi: {ServiceDeskUi.Som(amount)}.", $"Kassadan to'landi: {ServiceDeskUi.Som(amount)}."));
        Reload();
        return true;
    }

    /// <summary>Зачесть в покупку: отрицательная строка в текущем чеке на сумму не больше остатка и не больше итога чека. Состоявшимся зачёт считается после «Чек оплачен».</summary>
    public async Task<bool> AddCreditAsync(string id)
    {
        var r = _deps.Store.Find(id);
        if (r is null)
            return false;
        if (_deps.Till is not { } till)
        {
            Say(NoTill);
            return false;
        }
        var total = till.CartTotal;
        var credit = TradeInRules.CreditLimit(r, total);
        if (credit <= 0)
        {
            Say(Tr.T("В чеке нет суммы для зачёта: сначала добавьте в чек покупку клиента.", "Чекте эсептөө үчүн сумма жок: алгач кардардын сатып алуусун чекке кошуңуз.", "There is no amount in the receipt to credit against: add the customer's purchase to the receipt first.", "Fişte mahsup edilecek tutar yok: önce müşterinin alışverişini fişe ekleyin.", "Chekda hisobga olish uchun summa yo'q: avval mijozning xaridini chekka qo'shing."));
            return false;
        }
        var rest = TradeInRules.Outstanding(r) - credit;
        if (!await _confirm(Tr.T($"Зачесть {ServiceDeskUi.Som(credit)} в оплату чека (клиент доплатит {ServiceDeskUi.Som(RepairRules.Money(total - credit))}).{(rest > 0 ? " Остаток выкупа " + ServiceDeskUi.Som(rest) + " можно выплатить из кассы." : "")}", $"Чектин төлөмүнө {ServiceDeskUi.Som(credit)} эсептелсинби (кардар {ServiceDeskUi.Som(RepairRules.Money(total - credit))} кошуп төлөйт).{(rest > 0 ? " Сатып алуунун калган " + ServiceDeskUi.Som(rest) + " кассадан төлөнөт." : "")}", $"Credit {ServiceDeskUi.Som(credit)} to the receipt (the customer pays {ServiceDeskUi.Som(RepairRules.Money(total - credit))} more).{(rest > 0 ? " The remaining " + ServiceDeskUi.Som(rest) + " can be paid out from the till." : "")}", $"{ServiceDeskUi.Som(credit)} fiş ödemesine mahsup edilsin mi (müşteri {ServiceDeskUi.Som(RepairRules.Money(total - credit))} öder).{(rest > 0 ? " Kalan " + ServiceDeskUi.Som(rest) + " kasadan ödenebilir." : "")}", $"{ServiceDeskUi.Som(credit)} chek to'lovi hisobiga olinsinmi (mijoz yana {ServiceDeskUi.Som(RepairRules.Money(total - credit))} to'laydi).{(rest > 0 ? " Qolgan " + ServiceDeskUi.Som(rest) + " kassadan to'lanishi mumkin." : "")}"),
                Tr.T("Зачёт в покупку", "Сатып алууга эсептөө", "Credit to a purchase", "Alışverişe mahsup", "Xaridga hisobga olish")).ConfigureAwait(true))
            return false;
        if (!till.AddLine(Reason("credit", r), credit, credit: true))
        {
            Say(Tr.T("Строка не добавилась в чек.", "Сап чекке кошулган жок.", "The line was not added to the receipt.", "Satır fişe eklenmedi.", "Qator chekka qo'shilmadi."));
            return false;
        }
        _deps.Store.RecordCreditAdded(id, credit, By);
        Say(Tr.T("Зачёт добавлен в чек. Проведите оплату чека, затем вернитесь сюда и нажмите «Чек оплачен — зачёт состоялся».", "Эсептөө чекке кошулду. Чекти төлөтүп, бул жерге кайтып «Чек төлөндү — эсептөө болду» басыңыз.", "The credit was added to the receipt. Take the payment, then come back and press “Receipt paid — credit done”.", "Mahsup fişe eklendi. Ödemeyi alın, sonra buraya dönüp «Fiş ödendi — mahsup tamam»a basın.", "Hisobga olish chekka qo'shildi. Chekni to'lating, so'ng bu yerga qaytib «Chek to'landi — hisobga olish bo'ldi» ni bosing."));
        Reload();
        return true;
    }

    public async Task<bool> ConfirmCreditAsync(string id)
    {
        if (!await _confirm(Tr.T("Чек с зачётом оплачен? Подтверждайте, только когда оплата проведена.", "Эсептөөсү бар чек төлөндүбү? Төлөм өткөрүлгөндө гана ырастаңыз.", "Is the receipt with the credit paid? Confirm only when the payment is done.", "Mahsuplu fiş ödendi mi? Yalnızca ödeme yapıldığında onaylayın.", "Hisobga olingan chek to'landimi? Faqat to'lov o'tkazilganda tasdiqlang."), Tr.T("Зачёт trade-in", "Trade-in эсептөө", "Trade-in credit", "Trade-in mahsubu", "Trade-in hisobga olish")).ConfigureAwait(true))
            return false;
        var ok = _deps.Store.ConfirmCredit(id, By);
        Say(ok ? Tr.T("Зачёт подтверждён.", "Эсептөө ырасталды.", "Credit confirmed.", "Mahsup onaylandı.", "Hisobga olish tasdiqlandi.") : "");
        Reload();
        return ok;
    }

    public void CancelCredit(string id)
    {
        _deps.Store.CancelCredit(id, By);
        Say(Tr.T("Зачёт снят — сумма снова в остатке. Если строка ещё лежит в чеке, удалите её из чека.", "Эсептөө алынды — сумма кайра калдыкта. Эгер сап чекте турса — чектен өчүрүңүз.", "The credit was released — the amount is outstanding again. If the line is still in the receipt, delete it there.", "Mahsup kaldırıldı — tutar yine kalanda. Satır hâlâ fişteyse oradan silin.", "Hisobga olish olib tashlandi — summa yana qoldiqda. Qator hali chekda bo'lsa — uni chekdan o'chiring."));
        Reload();
    }

    public async Task<bool> CancelAsync(string id)
    {
        var r = _deps.Store.Find(id);
        if (r is null || !TradeInRules.CanCancel(r))
            return false;
        if (!await _confirm(Tr.T($"Отменить выкуп {TradeInRules.Code(r)}? Деньги не двигались.", $"{TradeInRules.Code(r)} сатып алууну жокко чыгарасызбы? Акча кыймылдаган жок.", $"Cancel buy-back {TradeInRules.Code(r)}? No money has moved.", $"{TradeInRules.Code(r)} alımı iptal edilsin mi? Para hareket etmedi.", $"{TradeInRules.Code(r)} sotib olish bekor qilinsinmi? Pul harakatlanmagan."), Tr.T("Отмена выкупа", "Сатып алууну жокко чыгаруу", "Cancel buy-back", "Alım iptali", "Sotib olishni bekor qilish")).ConfigureAwait(true))
            return false;
        var ok = _deps.Store.Cancel(id, By);
        Say(ok ? Tr.T("Выкуп отменён.", "Сатып алуу жокко чыгарылды.", "Buy-back cancelled.", "Alım iptal edildi.", "Sotib olish bekor qilindi.") : "");
        Reload();
        return ok;
    }

    // ───────────────────────── склад, печать, фото ─────────────────────────

    private async Task AskStockAsync(string id)
    {
        var r = _deps.Store.Find(id);
        if (r is null)
            return;
        var dlg = new ServiceDeskFormDialog(Tr.T("Оприходовать как б/у", "Б/у катары кирүү", "Receive as used", "İkinci el olarak al", "B/u sifatida kirim"),
            Tr.T($"Закупка = сумма выкупа {ServiceDeskUi.Som(r.Amount)}. Укажите цену продажи.", $"Сатып алуу = сатып алуу суммасы {ServiceDeskUi.Som(r.Amount)}. Сатуу баасын көрсөтүңүз.", $"Purchase price = buy-back amount {ServiceDeskUi.Som(r.Amount)}. Enter the resale price.", $"Alış = alım tutarı {ServiceDeskUi.Som(r.Amount)}. Satış fiyatını girin.", $"Xarid = sotib olish summasi {ServiceDeskUi.Som(r.Amount)}. Sotuv narxini kiriting."),
            new List<FormField> { new("price", Tr.T("Цена продажи, сом", "Сатуу баасы, сом", "Resale price, som", "Satış fiyatı, som", "Sotuv narxi, so'm"), MaxLength: 12, Width: 300) },
            v => RepairRules.TryParseMoney(v["price"]) is not { } p || p <= 0
                ? new List<string> { Tr.T("Впишите цену продажи (больше нуля).", "Сатуу баасын жазыңыз (нөлдөн чоң).", "Enter the resale price (more than zero).", "Satış fiyatını girin (sıfırdan büyük).", "Sotuv narxini kiriting (noldan katta).") }
                : new List<string>(),
            Tr.T("Оприходовать", "Кирүү", "Receive", "Stoğa al", "Kirim qilish"));
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
        if (dlg.Values.Count == 0)
            return;
        await StockAsync(id, RepairRules.TryParseMoney(dlg.Values["price"]) ?? 0m).ConfigureAwait(true);
    }

    /// <summary>Приход на склад: обычная приёмка без поставщика, один новый товар «Б/у …», закупка = сумма выкупа, цена продажи — как ввели. Нужен интернет; при ошибке выкуп остаётся «Выкуплен».</summary>
    public async Task<bool> StockAsync(string id, decimal resalePrice)
    {
        var r = _deps.Store.Find(id);
        if (r is null || !TradeInRules.CanStock(r) || resalePrice <= 0)
            return false;
        var (ok, error) = await _deps.Stock(TradeInRules.StockLine(r, resalePrice)).ConfigureAwait(true);
        if (!ok)
        {
            Say(Tr.T("Не оприходовано: ", "Кирүү жасалган жок: ", "Not received into stock: ", "Stoğa alınmadı: ", "Kirim qilinmadi: ") + error + " " + Tr.T("Проверьте интернет и повторите.", "Интернетти текшерип, кайталаңыз.", "Check the internet and try again.", "İnterneti kontrol edip tekrar deneyin.", "Internetni tekshirib, qayta urinib ko'ring."));
            return false;
        }
        _deps.Store.MarkStocked(id, resalePrice, By);
        Say(Tr.T("Оприходовано на склад как б/у.", "Кампага б/у катары кирди.", "Received into stock as used.", "İkinci el olarak depoya alındı.", "Omborga b/u sifatida kirim qilindi."));
        Reload();
        return true;
    }

    public async Task PrintAsync(string id)
    {
        var r = _deps.Store.Find(id);
        if (r is null)
            return;
        var error = await _deps.Print(TradeInRules.BuildAct(r)).ConfigureAwait(true);
        Say(error ?? Tr.T("Напечатано.", "Басылып чыкты.", "Printed.", "Yazdırıldı.", "Chop etildi."));
    }

    private async Task AddPhotoAsync(string id)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = true,
                FileTypeFilter = new[] { Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll },
            }).ConfigureAwait(true);
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            if (paths.Count == 0)
                return;
            AddPhotos(id, paths);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Trade-in: фото не добавлено: {ex.Message}", "WARNING");
            Say(Tr.T("Не удалось выбрать фото.", "Сүрөттү тандоо мүмкүн болгон жок.", "Could not pick a photo.", "Fotoğraf seçilemedi.", "Rasmni tanlab bo'lmadi."));
        }
    }

    public void AddPhotos(string id, IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _deps.Store.Update(id, r =>
        {
            foreach (var p in list)
                if (r.Photos.Count < 8 && !r.Photos.Contains(p, StringComparer.OrdinalIgnoreCase))
                    r.Photos.Add(p);
            return true;
        }, By, "photo");
        Reload();
    }
}
