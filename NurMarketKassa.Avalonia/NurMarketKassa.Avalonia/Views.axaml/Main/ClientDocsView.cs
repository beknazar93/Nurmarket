using NurMarketKassa.Core.Application;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, владелец: «КП и товарную накладную, выставление счёта … всё это должно быть во вкладке Клиенты». Общие
/// тексты, статусы, клиенты для выбора покупателя и открытие редактора — для списка документов, карточки клиента и истории
/// покупок.</summary>
public static class ClientDocsUi
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string KindName(ClientDocKind k) => k switch
    {
        ClientDocKind.Quote => T("КП", "КС", "Quote", "Teklif", "TT"),
        ClientDocKind.Invoice => T("Счёт", "Эсеп", "Invoice", "Fatura", "Hisob"),
        _ => T("Накладная", "Накладной", "Waybill", "İrsaliye", "Yuk xati"),
    };

    public static string KindLong(ClientDocKind k) => k switch
    {
        ClientDocKind.Quote => T("Коммерческое предложение", "Коммерциялык сунуш", "Commercial offer", "Ticari teklif", "Tijorat taklifi"),
        ClientDocKind.Invoice => T("Счёт на оплату", "Төлөм үчүн эсеп", "Invoice", "Ödeme faturası", "To'lov hisobi"),
        _ => T("Накладная", "Накладной", "Waybill", "İrsaliye", "Yuk xati"),
    };

    public static string AddText(ClientDocKind k) => k switch
    {
        ClientDocKind.Quote => T("+ КП", "+ КС", "+ Quote", "+ Teklif", "+ TT"),
        ClientDocKind.Invoice => T("+ Счёт", "+ Эсеп", "+ Invoice", "+ Fatura", "+ Hisob"),
        _ => T("+ Накладная", "+ Накладной", "+ Waybill", "+ İrsaliye", "+ Yuk xati"),
    };

    /// <summary>Статусы вида — для выбора в редакторе (код, подпись).</summary>
    public static IReadOnlyList<(string Code, string Text)> Statuses(ClientDocKind k) => k switch
    {
        ClientDocKind.Quote => new[]
        {
            ("draft", T("Черновик", "Долбоор", "Draft", "Taslak", "Qoralama")),
            ("sent", T("Отправлено", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi")),
            ("accepted", T("Принято", "Кабыл алынды", "Accepted", "Kabul edildi", "Qabul qilindi")),
            ("declined", T("Отклонено", "Четке кагылды", "Declined", "Reddedildi", "Rad etildi")),
        },
        ClientDocKind.Invoice => new[]
        {
            ("draft", T("Черновик", "Долбоор", "Draft", "Taslak", "Qoralama")),
            ("issued", T("Выставлен", "Берилди", "Issued", "Düzenlendi", "Berildi")),
            ("sent", T("Отправлен", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi")),
            ("partial", T("Оплачен частично", "Жарым-жартылай төлөндү", "Partly paid", "Kısmen ödendi", "Qisman to'landi")),
            ("paid", T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'landi")),
        },
        _ => new[]
        {
            ("draft", T("Черновик", "Долбоор", "Draft", "Taslak", "Qoralama")),
            ("sent", T("Отправлена", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi")),
            ("accepted", T("Получена", "Алынды", "Received", "Teslim alındı", "Qabul qilindi")),
        },
    };

    /// <summary>Подпись статуса и его вид: neutral | accent | ok | warn | danger. Просрочка — поверх статуса.</summary>
    public static (string Text, string Tone) Status(ClientDoc d)
    {
        if (d.Overdue)
            return (T("Просрочен", "Мөөнөтү өттү", "Overdue", "Gecikmiş", "Muddati o'tgan"), "danger");
        var text = Statuses(d.Kind).FirstOrDefault(s => s.Code == d.Status).Text ?? Statuses(d.Kind)[0].Text;
        var tone = d.Status switch
        {
            "sent" or "issued" => "accent",
            "accepted" or "paid" => "ok",
            "partial" => "warn",
            _ => "neutral",
        };
        return (text, tone);
    }

    /// <summary>Плашка статуса.</summary>
    public static Border Pill(Control host, string text, string tone)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = label };
        var (bg, fg) = tone switch
        {
            "accent" => ("BrushAccentSoft", "BrushText"),
            "ok" => ("BrushSuccessSoft", "BrushSuccess"),
            "warn" => ("BrushWarningSoft", "BrushText"),
            "danger" => ("BrushDangerSoft", "BrushDangerOnSoft"),
            _ => ("BrushPanelSoft", "BrushTextSoft"),
        };
        pill.Bind(Border.BackgroundProperty, host.GetResourceObservable(bg));
        label.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(fg));
        return pill;
    }

    public static string Money(double v) => v.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));

    public static string Som => T("сом", "сом", "som", "som", "so'm");

    // ───────────── покупатели (клиенты NurCRM с ИНН и компанией) ─────────────

    public sealed record Buyer(string Id, string Name, string Phone, string Address, string Inn, string Company);

    private static List<Buyer>? _buyers;
    private static DateTime _buyersAt;

    public static async Task<List<Buyer>> BuyersAsync(bool force = false)
    {
        if (!force && _buyers != null && DateTime.UtcNow - _buyersAt < TimeSpan.FromMinutes(3))
            return _buyers;
        try
        {
            var api = App.AppHost?.Services.GetService(typeof(NurMarketKassa.Services.Api.IClientsApiService)) as NurMarketKassa.Services.Api.IClientsApiService;
            if (api == null)
                return _buyers ?? new();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var raw = await api.GetClientsAsync(null, cts.Token).ConfigureAwait(true);
            _buyers = raw.Where(e => Str(e, "type") is null or "client")
                .Select(e => new Buyer(Str(e, "id") ?? "", Str(e, "full_name") ?? "", Str(e, "phone") ?? "", Str(e, "address") ?? "", Str(e, "inn") ?? "", Str(e, "llc") ?? ""))
                .Where(b => b.Id.Length > 0)
                .ToList();
            _buyersAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: список клиентов не загружен ({ex.Message}).", "INFO");
        }
        return _buyers ?? new();
    }

    /// <summary>Уже загруженные клиенты (без запроса).</summary>
    public static IReadOnlyList<Buyer> BuyersCached() => _buyers ?? (IReadOnlyList<Buyer>)Array.Empty<Buyer>();

    public static void RememberBuyer(Buyer b)
    {
        _buyers ??= new();
        _buyers.RemoveAll(x => x.Id == b.Id);
        _buyers.Insert(0, b);
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    /// <summary>Новый клиент прямо из документа: имя, телефон, компания, ИНН, адрес — на сервер NurCRM (как «+ Клиент»; компания и
    /// ИНН — поля клиента NurCRM llc и inn, их видно и на сайте).</summary>
    public static async Task<Buyer?> CreateBuyerAsync(Window owner)
    {
        Buyer? created = null;
        Dialogs.NewClientDialog? dialog = null;
        var existing = (await BuyersAsync().ConfigureAwait(true)).Select(b => new ClientRow { Id = b.Id, FullName = b.Name, Phone = b.Phone }).ToList();
        dialog = new Dialogs.NewClientDialog(async (name, phone, email, address) =>
        {
            try
            {
                var api = SettingsCloudSync.ApiProvider?.Invoke();
                if (api == null || string.IsNullOrEmpty(api.AccessToken))
                    return T("Нет связи с NurCRM — клиента добавить нельзя.", "NurCRM менен байланыш жок — кардарды кошуу мүмкүн эмес.", "No connection to NurCRM — cannot add the client.",
                        "NurCRM bağlantısı yok — müşteri eklenemiyor.", "NurCRM bilan aloqa yo'q — mijozni qo'shib bo'lmaydi.");
                var body = new Dictionary<string, string> { ["full_name"] = name, ["phone"] = phone, ["type"] = "client" };
                if (email.Length > 0)
                    body["email"] = email;
                if (address.Length > 0)
                    body["address"] = address;
                if (dialog!.Company.Length > 0)
                    body["llc"] = dialog.Company;
                if (dialog.Inn.Length > 0)
                    body["inn"] = dialog.Inn;
                var el = await api.RequestAsync(HttpMethod.Post, "api/main/clients/", body, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                var id = Str(el, "id") ?? "";
                if (id.Length == 0)
                    return T("Не удалось добавить клиента.", "Кардарды кошуу мүмкүн болгон жок.", "Could not add the client.", "Müşteri eklenemedi.", "Mijozni qo'shib bo'lmadi.");
                created = new Buyer(id, name, phone, address, dialog.Inn, dialog.Company);
                RememberBuyer(created);
                PosLogger.Log("Документы клиента: добавлен клиент из документа.", "INFO");
                return null;
            }
            catch (ApiException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                return T("Не удалось добавить клиента: ", "Кардарды кошуу мүмкүн болгон жок: ", "Could not add the client: ", "Müşteri eklenemedi: ", "Mijozni qo'shib bo'lmadi: ") + ex.Message;
            }
        }, existing, supplier: false, companyFields: true);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return created;
    }

    // ───────────── открыть редактор ─────────────

    /// <summary>Новый документ (для клиента, если он выбран) — открывает редактор.</summary>
    public static ClientDocEditorWindow? OpenNew(Window owner, ClientDocKind kind, Buyer? buyer = null)
    {
        var doc = NewDoc(kind, buyer);
        return Open(owner, doc, isNew: true);
    }

    public static ClientDoc NewDoc(ClientDocKind kind, Buyer? buyer)
    {
        var profile = ClientDocsStore.Profile;
        var doc = new ClientDoc
        {
            Kind = kind,
            Number = ClientDocsStore.NextNumber(kind),
            Status = "draft",
            VatPercent = profile.VatPercent,
            Template = profile.Templates.TryGetValue(ClientDocProfile.KindKey(kind), out var t) ? t : null,
            Lang = UserPreferences.Instance.Language == AppLanguage.Kyrgyz ? "ky" : "ru",
            Author = PosApp.CurrentUserDisplayName,
        };
        doc.Date = DateTime.Now;
        doc.Until = DateTime.Today.AddDays(kind == ClientDocKind.Quote ? 7 : 3);
        if (profile.Terms.TryGetValue(ClientDocProfile.KindKey(kind), out var terms) && !string.IsNullOrWhiteSpace(terms))
            doc.Note = terms;
        if (buyer != null)
            ApplyBuyer(doc, buyer);
        return doc;
    }

    public static void ApplyBuyer(ClientDoc doc, Buyer b)
    {
        doc.ClientId = b.Id;
        doc.Client = b.Company.Length > 0 ? b.Company : b.Name;
        doc.Phone = b.Phone;
        doc.Inn = b.Inn;
        doc.Address = b.Address.Length > 0 ? b.Address : null;
    }

    public static ClientDocEditorWindow? Open(Window owner, ClientDoc doc, bool isNew)
    {
        try
        {
            var w = new ClientDocEditorWindow(doc, isNew);
            w.Show(owner);
            return w;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор документа не открылся: {ex}", "WARNING");
            return null;
        }
    }

    /// <summary>«Накладная по чеку»: строки проведённой продажи → новая накладная (основание — чек), редактор.</summary>
    public static ClientDoc WaybillFromSale(JsonElement sale, string? receiptNumber, Buyer? buyer)
    {
        var doc = NewDoc(ClientDocKind.Waybill, buyer);
        AppendSaleLines(doc, sale);
        doc.BasedOn = T("чек № ", "чек № ", "receipt No. ", "fiş No. ", "chek № ") + (string.IsNullOrWhiteSpace(receiptNumber) ? "—" : receiptNumber!.Trim());
        // Скидка чека (итог меньше суммы строк) — скидкой документа.
        if (sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("total", out var tot)
            && double.TryParse(tot.ValueKind == JsonValueKind.String ? tot.GetString() : tot.GetRawText(), NumberStyles.Any, CultureInfo.InvariantCulture, out var total)
            && doc.Subtotal - total > 0.004)
            doc.Discount = ClientDoc.Round2(doc.Subtotal - total);
        if (buyer == null && (Str(sale, "client_name") ?? Str(sale, "client_display")) is { Length: > 0 } name)
        {
            doc.Client = name;
            doc.ClientId = Str(sale, "client");
        }
        return doc;
    }

    public static void AppendSaleLines(ClientDoc doc, JsonElement sale)
    {
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var qty = CartDisplayHelper.LineQuantity(line);
            var price = CartDisplayHelper.UnitPrice(line);
            if (double.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var sum) && qty > 0 && Math.Abs(sum - qty * price) > 0.01)
                price = sum / qty;
            var pid = CartDisplayHelper.TryProductId(line) ?? "";
            var unit = ProductActions.Find(pid) is { } p && !string.IsNullOrWhiteSpace(p.Unit) ? p.Unit : "шт";
            var l = new ClientDocLine { ProductId = pid, Name = CartDisplayHelper.ItemName(line), Unit = unit, Qty = qty };
            l.SetPrice(price, 0);
            doc.Lines.Add(l);
        }
    }

    public static string AccentHex(Control host)
    {
        if (host.TryFindResource("BrushAccent", host.ActualThemeVariant, out var o) && o is ISolidColorBrush b)
            return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
        return "#1F3A93";
    }
}

/// <summary>2026-10-21, владелец: список документов клиента — КП, счета, накладные: сегменты «Все / КП / Счета / Накладные»,
/// статусы плашками (черновик, отправлен, принят, оплачен, просрочен), поиск, суммы; «+ КП», «+ Счёт», «+ Накладная» (жёлтая —
/// одна, по выбранному сегменту). Строка — кнопка: щелчок / Enter открывает редактор. Встраивается в «Клиенты» вкладкой
/// «Документы»; с фильтром по клиенту — из его карточки.</summary>
public sealed class ClientDocsView : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private ClientDocKind? _kind;
    private string? _clientId;
    private string? _clientName;
    private readonly TextBox _search;
    private readonly Button[] _segs = new Button[4];
    private readonly Button _addQuote;
    private readonly Button _addInvoice;
    private readonly Button _addWaybill;
    private readonly Button _clientChip;
    private readonly StackPanel _rows = new();
    private readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 540, Spacing = 6, Margin = new Thickness(16, 28), IsVisible = false };
    private readonly TextBlock _emptyTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _emptyText = new() { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _sync = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly KpiTileGrid _kpis = new() { MinTileWidth = 170, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ScrollViewer _scroll;

    /// <summary>Открыть документы клиента (из карточки) — клиент нужен для «+ КП» и фильтра.</summary>
    public Func<ClientDocsUi.Buyer?>? CurrentBuyer { get; set; }

    public ClientDocsView()
    {
        _search = UiKit.Input(this, T("Номер, клиент или товар", "Номер, кардар же товар", "Number, client or product", "Numara, müşteri veya ürün", "Raqam, mijoz yoki tovar"), 42);
        _search.TextChanged += (_, _) => Fill();
        Avalonia.Automation.AutomationProperties.SetName(_search, T("Поиск документов", "Документтерди издөө", "Search documents", "Belge ara", "Hujjatlarni qidirish"));

        string[] segText = { T("Все", "Баары", "All", "Tümü", "Barchasi"), T("КП", "КС", "Quotes", "Teklifler", "TT"), T("Счета", "Эсептер", "Invoices", "Faturalar", "Hisoblar"), T("Накладные", "Накладнойлор", "Waybills", "İrsaliyeler", "Yuk xatlari") };
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            _segs[i] = UiKit.Seg(segText[i], i == 0);
            _segs[i].Click += (_, _) => SetKind(idx == 0 ? null : (ClientDocKind)(idx - 1));
            segPanel.Children.Add(_segs[i]);
        }
        var segment = UiKit.Segment(segPanel);
        segment.VerticalAlignment = VerticalAlignment.Center;
        segment.Margin = new Thickness(10, 0, 0, 0);

        _clientChip = UiKit.Chip(this, "", true);
        _clientChip.IsVisible = false;
        _clientChip.Margin = new Thickness(10, 0, 0, 0);
        _clientChip.Click += (_, _) => SetClient(null, null);

        _addQuote = UiKit.Ghost(this, ClientDocsUi.AddText(ClientDocKind.Quote));
        _addInvoice = UiKit.Ghost(this, ClientDocsUi.AddText(ClientDocKind.Invoice));
        _addWaybill = UiKit.Ghost(this, ClientDocsUi.AddText(ClientDocKind.Waybill));
        foreach (var (b, k) in new[] { (_addQuote, ClientDocKind.Quote), (_addInvoice, ClientDocKind.Invoice), (_addWaybill, ClientDocKind.Waybill) })
        {
            b.Height = 42;
            b.Focusable = true;
            Avalonia.Automation.AutomationProperties.SetName(b, ClientDocsUi.KindLong(k));
            b.Click += (_, _) => Create(k);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var hint = new TextBlock
        {
            Text = T("КП, счета на оплату и накладные для клиентов. Нажмите на документ — откроется редактор с предпросмотром листа A4.",
                "Кардарлар үчүн КС, төлөм эсептери жана накладнойлор. Документти басыңыз — A4 барагын алдын ала көрүү менен түзөткүч ачылат.",
                "Quotes, invoices and waybills for clients. Click a document to open the editor with an A4 preview.",
                "Müşteriler için teklifler, faturalar ve irsaliyeler. Belgeye tıklayın — A4 önizlemeli düzenleyici açılır.",
                "Mijozlar uchun TT, to'lov hisoblari va yuk xatlari. Hujjatni bosing — A4 varaq ko'rinishi bilan muharrir ochiladi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        top.Children.Add(hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _addQuote, _addInvoice, _addWaybill } };
        Grid.SetColumn(actions, 1);
        top.Children.Add(actions);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        filters.Children.Add(_search);
        Grid.SetColumn(segment, 1);
        filters.Children.Add(segment);
        Grid.SetColumn(_clientChip, 2);
        filters.Children.Add(_clientChip);

        // Шапка списка и строки — одни колонки.
        var head = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
        head.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
        head.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        head.Child = RowGrid(
            Th(T("Документ", "Документ", "Document", "Belge", "Hujjat")),
            Th(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz")),
            Th(T("Дата", "Күнү", "Date", "Tarih", "Sana")),
            Th(T("Срок", "Мөөнөтү", "Due", "Vade", "Muddat")),
            Th(T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"), right: true),
            Th(T("Статус", "Статус", "Status", "Durum", "Holat")));

        _scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _rows };
        _emptyTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _emptyText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyText);
        var listGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listGrid.Children.Add(head);
        Grid.SetRow(_scroll, 1);
        listGrid.Children.Add(_scroll);
        Grid.SetRow(_empty, 1);
        listGrid.Children.Add(_empty);
        var card = new Border { BorderThickness = new Thickness(1), ClipToBounds = true, Child = listGrid };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        card.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("SettingsCardRadius"));

        _sync.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _sync.Margin = new Thickness(2, 8, 0, 0);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        root.Children.Add(top);
        Grid.SetRow(_kpis, 1);
        root.Children.Add(_kpis);
        Grid.SetRow(filters, 2);
        root.Children.Add(filters);
        Grid.SetRow(card, 3);
        root.Children.Add(card);
        Grid.SetRow(_sync, 4);
        root.Children.Add(_sync);
        Content = root;

        UpdateButtons();
        AttachedToVisualTree += (_, _) =>
        {
            ClientDocsStore.Changed += OnStoreChanged;
            Fill();
            _ = ClientDocsStore.RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) => ClientDocsStore.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(Fill);

    /// <summary>Фильтр по клиенту (из карточки) или все документы (null).</summary>
    public void SetClient(string? clientId, string? clientName)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
        _clientName = clientName;
        _clientChip.IsVisible = _clientId != null;
        _clientChip.Content = T($"Клиент: {clientName} ✕", $"Кардар: {clientName} ✕", $"Client: {clientName} ✕", $"Müşteri: {clientName} ✕", $"Mijoz: {clientName} ✕");
        Avalonia.Automation.AutomationProperties.SetName(_clientChip, T("Показать документы всех клиентов", "Бардык кардарлардын документтерин көрсөтүү", "Show documents of all clients", "Tüm müşterilerin belgelerini göster", "Barcha mijozlar hujjatlarini ko'rsatish"));
        Fill();
    }

    public void SetKind(ClientDocKind? kind)
    {
        _kind = kind;
        for (var i = 0; i < 4; i++)
            UiKit.SetActive(_segs[i], i == (kind is { } k ? (int)k + 1 : 0));
        UpdateButtons();
        Fill();
    }

    /// <summary>Жёлтая — одна: по выбранному сегменту («Все» — «+ КП»), остальные — обводкой.</summary>
    private void UpdateButtons()
    {
        var main = _kind ?? ClientDocKind.Quote;
        foreach (var (b, k) in new[] { (_addQuote, ClientDocKind.Quote), (_addInvoice, ClientDocKind.Invoice), (_addWaybill, ClientDocKind.Waybill) })
        {
            b.Classes.Set("sec-primary", k == main);
            b.Classes.Set("sec-outline", k != main);
            b.FontWeight = k == main ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    private void Create(ClientDocKind kind)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        ClientDocsUi.Buyer? buyer = null;
        if (_clientId != null)
            buyer = CurrentBuyer?.Invoke() ?? new ClientDocsUi.Buyer(_clientId, _clientName ?? "", "", "", "", "");
        ClientDocsUi.OpenNew(owner, kind, buyer);
    }

    public IReadOnlyList<ClientDoc> Shown { get; private set; } = Array.Empty<ClientDoc>();

    public void Fill()
    {
        var all = ClientDocsStore.All();
        IEnumerable<ClientDoc> list = all;
        if (_clientId != null)
            list = list.Where(d => d.ClientId == _clientId || (!string.IsNullOrWhiteSpace(_clientName) && string.Equals(d.Client.Trim(), _clientName!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        var scoped = list.ToList();
        if (_kind is { } k)
            list = list.Where(d => d.Kind == k);
        var q = (_search.Text ?? "").Trim();
        if (q.Length > 0)
            list = list.Where(d => SearchMatch.Matches(q, d.Number, d.Client, d.Phone, string.Join(" ", d.Lines.Select(l => l.Name)))); // 2026-10-29: единый поиск
        Shown = list.ToList();

        _rows.Children.Clear();
        foreach (var d in Shown)
            _rows.Children.Add(Row(d));
        UpdateKpis(scoped);

        _empty.IsVisible = Shown.Count == 0;
        if (Shown.Count == 0)
        {
            if (scoped.Count == 0)
            {
                _emptyTitle.Text = _clientId != null
                    ? T("У клиента пока нет документов", "Кардардын азырынча документтери жок", "This client has no documents yet", "Bu müşterinin henüz belgesi yok", "Mijozda hali hujjatlar yo'q")
                    : T("Документов пока нет", "Азырынча документтер жок", "No documents yet", "Henüz belge yok", "Hozircha hujjatlar yo'q");
                _emptyText.Text = T("Нажмите «+ КП», «+ Счёт» или «+ Накладная». Товары — из каталога (поиск или скан), реквизиты магазина подставятся сами; КП и счета с телефона тоже будут здесь.",
                    "«+ КС», «+ Эсеп» же «+ Накладной» басыңыз. Товарлар — каталогдон (издөө же скан), дүкөндүн реквизиттери өзү коюлат; телефондогу КС жана эсептер да ушул жерде болот.",
                    "Press “+ Quote”, “+ Invoice” or “+ Waybill”. Products come from the catalog (search or scan), shop details fill in by themselves; quotes and invoices from the phone show up here too.",
                    "«+ Teklif», «+ Fatura» veya «+ İrsaliye»ye basın. Ürünler katalogdan gelir (arama veya okutma), mağaza bilgileri kendiliğinden dolar; telefondaki teklifler ve faturalar da burada görünür.",
                    "«+ TT», «+ Hisob» yoki «+ Yuk xati»ni bosing. Tovarlar katalogdan (qidiruv yoki skan), do'kon rekvizitlari o'zi qo'yiladi; telefondagi TT va hisoblar ham shu yerda bo'ladi.");
            }
            else
            {
                _emptyTitle.Text = T("Ничего не нашли", "Эч нерсе табылган жок", "Nothing found", "Hiçbir şey bulunamadı", "Hech narsa topilmadi");
                _emptyText.Text = T("Проверьте номер или имя клиента, выберите «Все» или очистите поиск.", "Номерди же кардардын атын текшериңиз, «Баары» тандаңыз же издөөнү тазалаңыз.",
                    "Check the number or client name, choose “All” or clear the search.", "Numarayı veya müşteri adını kontrol edin, «Tümü»nü seçin ya da aramayı temizleyin.",
                    "Raqam yoki mijoz ismini tekshiring, «Barchasi»ni tanlang yoki qidiruvni tozalang.");
            }
        }
        UpdateSync();
    }

    private void UpdateSync()
    {
        _sync.Text = ClientDocsStore.State switch
        {
            ClientDocsStore.SyncState.Saved => T("Документы в общих настройках компании NurCRM — их видно на других ПК и в телефоне.",
                "Документтер NurCRM компаниясынын жалпы жөндөөлөрүндө — башка ПКларда жана телефондо көрүнөт.",
                "Documents are in the shared NurCRM company settings — visible on other PCs and on the phone.",
                "Belgeler NurCRM şirket ortak ayarlarında — diğer bilgisayarlarda ve telefonda görünür.",
                "Hujjatlar NurCRM kompaniyasining umumiy sozlamalarida — boshqa kompyuterlarda va telefonda ko'rinadi."),
            ClientDocsStore.SyncState.Saving => T("Сохраняю в NurCRM…", "NurCRMге сакталууда…", "Saving to NurCRM…", "NurCRM'e kaydediliyor…", "NurCRM'ga saqlanmoqda…"),
            ClientDocsStore.SyncState.Forbidden => T("Нет прав на общие настройки компании — документы сохранены только на этом ПК (сохраняет владелец или администратор).",
                "Компаниянын жалпы жөндөөлөрүнө укук жок — документтер ушул ПКда гана сакталды (ээси же администратор сактайт).",
                "No rights to shared company settings — documents are saved on this PC only (the owner or an administrator saves them).",
                "Şirket ortak ayarlarına yetki yok — belgeler yalnızca bu bilgisayarda kaydedildi (sahibi veya yönetici kaydeder).",
                "Kompaniya umumiy sozlamalariga huquq yo'q — hujjatlar faqat shu kompyuterda saqlandi (egasi yoki administrator saqlaydi)."),
            ClientDocsStore.SyncState.LocalOnly => T("Нет связи с NurCRM — документы сохранены на этом ПК и уйдут на сервер, когда связь появится.",
                "NurCRM менен байланыш жок — документтер ушул ПКда сакталды, байланыш болгондо серверге кетет.",
                "No connection to NurCRM — documents are saved on this PC and will be sent when the connection is back.",
                "NurCRM bağlantısı yok — belgeler bu bilgisayarda kaydedildi, bağlantı gelince sunucuya gönderilecek.",
                "NurCRM bilan aloqa yo'q — hujjatlar shu kompyuterda saqlandi, aloqa tiklanganda serverga yuboriladi."),
            _ => "",
        };
        // 2026-10-21 (ревью документов, A3/A5): сообщение слияния (изменён/удалён на другом устройстве, номер занят) и размер списка.
        if (ClientDocsStore.Notice is { Length: > 0 } notice)
            _sync.Text = (_sync.Text + Environment.NewLine + notice).Trim();
        if (ClientDocsStore.QuotesLarge)
            _sync.Text = (_sync.Text + Environment.NewLine + T($"Общий список КП и счетов большой ({ClientDocsStore.QuotesBytes / 1024} КБ) — каждое сохранение отправляет его целиком; удалите старые закрытые документы.",
                $"КС жана эсептердин жалпы тизмеси чоң ({ClientDocsStore.QuotesBytes / 1024} КБ) — ар бир сактоо аны толугу менен жөнөтөт; эски жабык документтерди өчүрүңүз.",
                $"The shared list of quotes and invoices is large ({ClientDocsStore.QuotesBytes / 1024} KB) — every save sends all of it; delete old closed documents.",
                $"Teklif ve fatura ortak listesi büyük ({ClientDocsStore.QuotesBytes / 1024} KB) — her kayıt tamamını gönderir; eski kapalı belgeleri silin.",
                $"TT va hisoblarning umumiy ro'yxati katta ({ClientDocsStore.QuotesBytes / 1024} KB) — har saqlash uni to'liq yuboradi; eski yopilgan hujjatlarni o'chiring.")).Trim();
        _sync.IsVisible = _sync.Text.Length > 0;
    }

    private void UpdateKpis(List<ClientDoc> scoped)
    {
        _kpis.Children.Clear();
        var waiting = scoped.Where(d => d.Kind == ClientDocKind.Quote && d.Status is "draft" or "sent" && !d.Overdue).ToList();
        var unpaid = scoped.Where(d => d.Kind == ClientDocKind.Invoice && d.Status != "paid").ToList();
        var overdue = scoped.Where(d => d.Overdue).ToList();
        var paidMonth = scoped.Where(d => d.Kind == ClientDocKind.Invoice && d.Status == "paid" && d.UpdatedAt >= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).ToList();
        _kpis.Children.Add(Tile(T($"КП ждут ответа · {waiting.Count}", $"КС жооп күтөт · {waiting.Count}", $"Quotes awaiting · {waiting.Count}", $"Yanıt bekleyen teklifler · {waiting.Count}", $"Javob kutayotgan TT · {waiting.Count}"),
            waiting.Sum(d => d.Total), false));
        _kpis.Children.Add(Tile(T($"Счета к оплате · {unpaid.Count}", $"Төлөнө турган эсептер · {unpaid.Count}", $"Invoices to be paid · {unpaid.Count}", $"Ödenecek faturalar · {unpaid.Count}", $"To'lanadigan hisoblar · {unpaid.Count}"),
            unpaid.Sum(d => d.Left), false));
        _kpis.Children.Add(Tile(T($"Просрочено · {overdue.Count}", $"Мөөнөтү өттү · {overdue.Count}", $"Overdue · {overdue.Count}", $"Gecikmiş · {overdue.Count}", $"Muddati o'tgan · {overdue.Count}"),
            overdue.Sum(d => d.Kind == ClientDocKind.Invoice ? d.Left : d.Total), overdue.Count > 0));
        _kpis.Children.Add(Tile(T("Оплачено по счетам за месяц", "Ушул айда эсептер боюнча төлөндү", "Paid on invoices this month", "Bu ay faturalardan ödenen", "Shu oy hisoblar bo'yicha to'langan"),
            paidMonth.Sum(d => d.Total), false));
    }

    private static Border Tile(string label, double sum, bool danger) => UiKit.Kpi(label, ClientDocsUi.Money(sum) + " " + ClientDocsUi.Som, danger);

    private Control Row(ClientDoc d)
    {
        var (statusText, tone) = ClientDocsUi.Status(d);
        var number = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        number.Children.Add(Td(d.Number, bold: true));
        number.Children.Add(Sub(ClientDocsUi.KindLong(d.Kind)));
        var client = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = Td(d.Client.Length == 0 ? "—" : d.Client, bold: true);
        name.MaxLines = 2;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        client.Children.Add(name);
        var sub = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(d.BasedOn) ? null
                : ClientDoc.NumberIndex(d.BasedOn, ClientDocKind.Quote) != null || ClientDoc.NumberIndex(d.BasedOn, ClientDocKind.Invoice) != null
                    ? T("по ", "негизи ", "from ", "kaynak ", "asosi ") + d.BasedOn
                    : T("основание: ", "негизи: ", "basis: ", "dayanak: ", "asos: ") + d.BasedOn,
            T($"строк: {d.Lines.Count}", $"саптар: {d.Lines.Count}", $"lines: {d.Lines.Count}", $"satır: {d.Lines.Count}", $"qatorlar: {d.Lines.Count}"),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        client.Children.Add(Sub(sub));
        var until = d.Kind == ClientDocKind.Waybill ? "—" : d.Until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var untilText = Td(until);
        if (d.Overdue)
        {
            untilText.Classes.Add("debt");
        }
        var sumBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var sumText = Td(ClientDocsUi.Money(d.Total), bold: true);
        sumText.TextAlignment = TextAlignment.Right;
        sumText.Margin = new Thickness(0);
        sumBox.Children.Add(sumText);
        if (d.Kind == ClientDocKind.Invoice && d.Paid > 0.004 && d.Status != "paid")
        {
            var left = Sub(T($"осталось {ClientDocsUi.Money(d.Left)}", $"калды {ClientDocsUi.Money(d.Left)}", $"left {ClientDocsUi.Money(d.Left)}", $"kalan {ClientDocsUi.Money(d.Left)}", $"qoldi {ClientDocsUi.Money(d.Left)}"));
            left.TextAlignment = TextAlignment.Right;
            left.Margin = new Thickness(0);
            sumBox.Children.Add(left);
        }
        var grid = RowGrid(number, client, Td(d.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)), untilText, sumBox, ClientDocsUi.Pill(this, statusText, tone));
        var b = new Button { Content = grid, Tag = d.Id };
        b.Classes.Add("client-row");
        Avalonia.Automation.AutomationProperties.SetName(b, $"{ClientDocsUi.KindLong(d.Kind)} {d.Number}, {d.Client}, {ClientDocsUi.Money(d.Total)} {ClientDocsUi.Som}, {statusText}");
        b.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner && ClientDocsStore.Get(d.Id) is { } fresh)
                ClientDocsUi.Open(owner, fresh, isNew: false);
        };
        return b;
    }

    private static Grid RowGrid(params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("1.25*,2.3*,0.9*,0.9*,1.15*,1.2*") };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    private static TextBlock Th(string text, bool right = false)
    {
        var t = new TextBlock { Text = text, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
        t.Classes.Add("th");
        return t;
    }

    private static TextBlock Td(string text, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        t.Classes.Add("td");
        return t;
    }

    private static TextBlock Sub(string text)
    {
        var t = new TextBlock { Text = text, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        t.Classes.Add("sub");
        t.Margin = new Thickness(0, 2, 0, 0);
        return t;
    }
}
