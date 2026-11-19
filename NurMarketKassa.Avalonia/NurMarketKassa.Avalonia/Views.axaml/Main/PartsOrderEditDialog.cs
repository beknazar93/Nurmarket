using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 4.4): окно «Заказ под клиента» — клиент (из базы или телефон), позиции (из каталога или «под заказ»
/// без остатка), «Предоплата» (необязательна, кнопка «50 %»), «Срок поставки / готовности», поставщик, комментарий. Окно только собирает данные в
/// <see cref="Order"/> и ничего не пишет на диск — сохраняет окно списка. Предоплата — запись «получено на руки»: в смену и на сервер не проводится
/// (см. <see cref="PartsOrder"/>). Закрывается результатом true — «Сохранить».
/// </summary>
public sealed class PartsOrderEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ModalShell _shell = new();
    private readonly PartsOrder _order;
    private readonly ComboBox _clients = new();
    private readonly TextBox _name = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _found = new() { Spacing = 4 };
    private readonly StackPanel _lines = new() { Spacing = 6 };
    private readonly TextBox _prepay = new();
    private readonly TextBox _due = new();
    private readonly TextBox _supplier = new();
    private readonly TextBox _comment = new();
    private readonly TextBlock _sum = new();
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly List<(PartsOrderLine Line, TextBox Title, TextBox Qty, TextBox Price)> _rows = new();
    private readonly List<OfflineClientRecord> _known;
    private string _clientId = "";

    public PartsOrder Order => _order;

    public PartsOrderEditDialog() : this(new PartsOrder()) { }

    public PartsOrderEditDialog(PartsOrder order)
    {
        _order = order;
        _known = SafeClients();
        Title = T("Заказ под клиента", "Кардарга заказ", "Customer order", "Müşteri siparişi", "Mijozga buyurtma");
        Width = 860;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => this.FitToKassaScreen();
        BuildUi();
        foreach (var l in order.Lines)
            AddRow(l);
        Recalc();
    }

    private static List<OfflineClientRecord> SafeClients()
    {
        try { return OfflineClientCache.All(); }
        catch (Exception) { return new List<OfflineClientRecord>(); }
    }

    private static void StyleInput(TextBox b)
    {
        ServiceDeskUi.Use(b, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(b, BorderBrushProperty, "BrushInputBorder");
    }

    private static TextBox Box(string name, string watermark, double width = double.NaN, int max = 120)
    {
        var b = new TextBox { Name = name, Watermark = watermark, MaxLength = max, Height = 38 };
        if (!double.IsNaN(width))
            b.Width = width;
        StyleInput(b);
        return b;
    }

    private static Control Labeled(string label, Control c)
    {
        var p = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 10, 8) };
        p.Children.Add(ServiceDeskUi.Text(label, 11.5, brush: "BrushTextSoft"));
        p.Children.Add(c);
        return p;
    }

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;

        var root = new StackPanel { Spacing = 6 };

        // клиент
        _clients.Name = "OrderClientPick";
        _clients.Height = 38;
        _clients.MinWidth = 260;
        _clients.ItemsSource = new[] { T("Из базы клиентов…", "Кардарлар базасынан…", "From the customer list…", "Müşteri listesinden…", "Mijozlar bazasidan…") }
            .Concat(_known.Select(c => string.IsNullOrWhiteSpace(c.Phone) ? c.Name : c.Name + " · " + c.Phone)).ToArray();
        _clients.SelectedIndex = 0;
        _clients.SelectionChanged += (_, _) =>
        {
            var i = _clients.SelectedIndex - 1;
            if (i < 0 || i >= _known.Count)
                return;
            _clientId = _known[i].Id;
            _name.Text = _known[i].Name;
            _phone.Text = _known[i].Phone;
        };
        _name.Name = "OrderClientName";
        _name.Watermark = T("Имя клиента", "Кардардын аты", "Customer name", "Müşteri adı", "Mijoz ismi");
        _name.MaxLength = 100;
        _name.Height = 38;
        _name.MinWidth = 220;
        _name.Text = _order.ClientName;
        _clientId = _order.ClientId;
        _phone.Name = "OrderClientPhone";
        _phone.Watermark = T("Телефон", "Телефон", "Phone", "Telefon", "Telefon");
        _phone.MaxLength = 30;
        _phone.Height = 38;
        _phone.MinWidth = 180;
        _phone.Text = _order.Phone;
        StyleInput(_name);
        StyleInput(_phone);
        var clientRow = new WrapPanel { Orientation = Orientation.Horizontal };
        clientRow.Children.Add(Labeled(T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), _clients));
        clientRow.Children.Add(Labeled(T("Имя", "Аты", "Name", "Ad", "Ism"), _name));
        clientRow.Children.Add(Labeled(T("Телефон (для WhatsApp)", "Телефон (WhatsApp үчүн)", "Phone (for WhatsApp)", "Telefon (WhatsApp için)", "Telefon (WhatsApp uchun)"), _phone));
        root.Children.Add(clientRow);

        // позиции
        root.Children.Add(ServiceDeskUi.Text(T("Позиции", "Позициялар", "Items", "Kalemler", "Pozitsiyalar"), 14, true));
        _search.Name = "OrderProductSearch";
        _search.Watermark = T("Деталь из каталога: название, артикул", "Каталогдон тетик: аталышы, артикул", "Part from the catalog: name, article", "Katalogdan parça: ad, parça no", "Katalogdan detal: nomi, artikul");
        _search.Height = 38;
        StyleInput(_search);
        _search.TextChanged += (_, _) => RefreshFound();
        var free = ServiceDeskUi.Action("OrderAddFree", T("＋ Под заказ", "＋ Заказ боюнча", "＋ On order", "＋ Siparişle", "＋ Buyurtma bilan"),
            () => { AddRow(new PartsOrderLine { OnOrder = true }); Recalc(); });
        var searchRow = new DockPanel();
        DockPanel.SetDock(free, Dock.Right);
        free.Margin = new Thickness(8, 0, 0, 0);
        searchRow.Children.Add(free);
        searchRow.Children.Add(_search);
        root.Children.Add(searchRow);
        root.Children.Add(_found);
        root.Children.Add(_lines);

        // условия
        _prepay.Name = "OrderPrepayment";
        _prepay.Watermark = "0";
        _prepay.Width = 130;
        _prepay.Height = 38;
        _prepay.MaxLength = 12;
        _prepay.Text = _order.Prepayment > 0m ? MoneyText.Weight(_order.Prepayment, "0.##") : ""; // 2026-11-20: показ общим форматтером (запятая; разбор принимает и запятую, и точку)
        StyleInput(_prepay);
        _prepay.TextChanged += (_, _) => Recalc();
        var half = ServiceDeskUi.Action("OrderPrepay50", "50 %", () =>
        {
            _prepay.Text = MoneyText.Weight(PartsOrderRules.SuggestPrepayment(CurrentTotal()), "0.##");
        });
        half.Height = 38;
        half.Margin = new Thickness(8, 0, 0, 0);
        var prepayRow = new StackPanel { Orientation = Orientation.Horizontal };
        prepayRow.Children.Add(_prepay);
        prepayRow.Children.Add(half);
        _due.Name = "OrderDue";
        _due.Watermark = T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy");
        _due.Width = 150;
        _due.Height = 38;
        _due.MaxLength = 10;
        _due.Text = _order.DueDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        StyleInput(_due);
        _supplier.Name = "OrderSupplier";
        _supplier.Width = 240;
        _supplier.Height = 38;
        _supplier.MaxLength = 80;
        _supplier.Text = _order.Supplier;
        StyleInput(_supplier);
        _comment.Name = "OrderComment";
        _comment.MaxLength = 300;
        _comment.Height = 38;
        _comment.Text = _order.Comment;
        StyleInput(_comment);
        var terms = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        terms.Children.Add(Labeled(T("Предоплата, сом (необязательно)", "Алдын ала төлөм, сом (милдеттүү эмес)", "Prepayment, som (optional)", "Ön ödeme, som (isteğe bağlı)", "Oldindan to'lov, som (majburiy emas)"), prepayRow));
        terms.Children.Add(Labeled(T("Срок поставки / готовности", "Жеткирүү / даярдоо мөөнөтү", "Delivery / ready date", "Teslim / hazır olma tarihi", "Yetkazish / tayyor bo'lish muddati"), _due));
        terms.Children.Add(Labeled(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), _supplier));
        root.Children.Add(terms);
        root.Children.Add(Labeled(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), _comment));
        _sum.Name = "OrderSum";
        _sum.FontSize = 12.5;
        _sum.MaxWidth = 440;
        _sum.VerticalAlignment = VerticalAlignment.Center;
        _sum.FontWeight = FontWeight.SemiBold;
        _sum.TextWrapping = TextWrapping.Wrap;
        ServiceDeskUi.Use(_sum, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(ServiceDeskUi.Text(T(
            "Предоплата записывается в заказ как «получено на руки» и не проводится через смену: держите эти деньги отдельно (в конверте) до выдачи заказа. Они зачтутся при продаже.",
            "Алдын ала төлөм заказга «колго алынды» деп жазылат жана смена аркылуу өтпөйт: бул акчаны заказ берилгенче өзүнчө (конвертте) сактаңыз. Сатууда эсепке алынат.",
            "The prepayment is recorded in the order as “received in hand” and is not posted through the shift: keep this money apart (in an envelope) until the order is issued. It is credited at the sale.",
            "Ön ödeme siparişe «elden alındı» olarak yazılır ve vardiyadan geçmez: bu parayı sipariş teslim edilene kadar ayrı (zarfta) tutun. Satışta mahsup edilir.",
            "Oldindan to'lov buyurtmaga «qo'lga olindi» deb yoziladi va smena orqali o'tmaydi: bu pulni buyurtma berilguncha alohida (konvertda) saqlang. Sotuvda hisobga olinadi."), 12, brush: "BrushTextSoft"));
        _error.Name = "OrderError";
        ServiceDeskUi.Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(_error);

        _shell.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        var save = new Button { Name = "OrderSave", Content = T("Сохранить заказ", "Заказды сактоо", "Save order", "Siparişi kaydet", "Buyurtmani saqlash"), MinWidth = 160 };
        save.Classes.Add("modal-primary");
        save.Click += (_, _) => TrySave();
        var cancel = new Button { Name = "OrderCancel", Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), MinWidth = 120, IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        _shell.Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { save, cancel, _sum } }; // 2026-11-20: итог всегда на виду (на 1024×600 поля прокручиваются)
        Content = _shell;
    }

    private void RefreshFound()
    {
        _found.Children.Clear();
        var q = (_search.Text ?? "").Trim();
        if (q.Length < 2)
            return;
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> hits;
        try
        {
            hits = CatalogCacheService.Products
                .Where(p => SearchMatch.Matches(q, p.Title, p.Article))
                .Take(6).ToList();
        }
        catch (Exception)
        {
            hits = new();
        }
        foreach (var p in hits)
        {
            var product = p;
            var stock = product.Quantity > 0 ? "" : T(" · нет в наличии", " · жок", " · out of stock", " · stokta yok", " · mavjud emas");
            var b = new Button { Content = $"{product.Title}  ({MoneyText.Som(product.PriceValue)}{stock})", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6) };
            ServiceDeskUi.Use(b, BackgroundProperty, "BrushPanelSoft");
            ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
            b.Click += (_, _) =>
            {
                // Клиент с отметкой «СТО / мастер» — цена СТО (оптовая цена карточки); нет её — розница.
                var price = AutoPartsRules.StoPrice((decimal)product.PriceValue, (decimal)product.WholesalePrice, ClientPriceGroups.IsForeman(_clientId) || PriceLevelPricing.LevelOf(_clientId) is not null).Price;
                AddRow(new PartsOrderLine { ProductId = product.Id, Article = product.Article ?? "", Title = product.Title, Price = price, OnOrder = product.Quantity <= 0 });
                _search.Text = "";
                Recalc();
            };
            _found.Children.Add(b);
        }
    }

    private void AddRow(PartsOrderLine line)
    {
        var title = Box("OrderLineTitle", T("Название / номер", "Аталышы / номери", "Name / number", "Ad / numara", "Nomi / raqami"), max: 160);
        title.Text = line.Title;
        title.MinWidth = 300;
        var qty = Box("OrderLineQty", T("Кол-во", "Саны", "Qty", "Adet", "Soni"), 80, 8);
        qty.Text = MoneyText.Qty(line.Qty);
        var price = Box("OrderLinePrice", T("Цена", "Баасы", "Price", "Fiyat", "Narx"), 110, 12);
        price.Text = line.Price > 0m ? MoneyText.Weight(line.Price, "0.##") : "";
        qty.TextChanged += (_, _) => Recalc();
        price.TextChanged += (_, _) => Recalc();
        var remove = ServiceDeskUi.Action("OrderLineRemove", "✕", () => { });
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.Height = 38;
        var row = new DockPanel();
        var entry = (line, title, qty, price);
        remove.Click += (_, _) => { _rows.Remove(entry); _lines.Children.Remove(row); Recalc(); };
        DockPanel.SetDock(remove, Dock.Right);
        DockPanel.SetDock(price, Dock.Right);
        DockPanel.SetDock(qty, Dock.Right);
        qty.Margin = new Thickness(6, 0, 0, 0);
        price.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(remove);
        row.Children.Add(price);
        row.Children.Add(qty);
        if (line.OnOrder)
        {
            var chip = ServiceDeskUi.Chip(T("под заказ", "заказ боюнча", "on order", "siparişle", "buyurtma bilan"), true);
            chip.Margin = new Thickness(6, 8, 0, 0);
            DockPanel.SetDock(chip, Dock.Right);
            row.Children.Add(chip);
        }
        row.Children.Add(title);
        _rows.Add(entry);
        _lines.Children.Add(row);
    }

    private static decimal ParseOr0(string? text) => ServiceDeskUi.ParseMoney(text) ?? 0m;

    private decimal CurrentTotal() => _rows.Sum(r => Math.Max(0m, ParseOr0(r.Qty.Text)) * Math.Max(0m, ParseOr0(r.Price.Text)));

    private void Recalc()
    {
        var total = decimal.Round(CurrentTotal(), 2); // 2026-11-20: decimal.Round, а не Math.Round — проверка «нет денежных расчётов во view» (итог только для показа)
        var prepay = PartsOrderRules.ClampPrepayment(ParseOr0(_prepay.Text), total);
        var rest = Math.Max(0m, total - prepay);
        _sum.Text = T($"Итого: {MoneyText.Som(total)} · предоплата: {MoneyText.Som(prepay)} · к доплате: {MoneyText.Som(rest)}",
            $"Жыйынтыгы: {MoneyText.Som(total)} · алдын ала төлөм: {MoneyText.Som(prepay)} · төлөнө турган: {MoneyText.Som(rest)}",
            $"Total: {MoneyText.Som(total)} · prepayment: {MoneyText.Som(prepay)} · balance due: {MoneyText.Som(rest)}",
            $"Toplam: {MoneyText.Som(total)} · ön ödeme: {MoneyText.Som(prepay)} · kalan: {MoneyText.Som(rest)}",
            $"Jami: {MoneyText.Som(total)} · oldindan to'lov: {MoneyText.Som(prepay)} · qoldiq: {MoneyText.Som(rest)}");
    }

    private void Fail(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void TrySave()
    {
        _error.IsVisible = false;
        var lines = new List<PartsOrderLine>();
        foreach (var r in _rows)
        {
            var title = (r.Title.Text ?? "").Trim();
            if (title.Length == 0)
                continue;
            var qty = ServiceDeskUi.ParseMoney(r.Qty.Text);
            var price = ServiceDeskUi.ParseMoney(r.Price.Text);
            if (qty is null or <= 0m || price is null or < 0m)
            {
                Fail(T($"Проверьте количество и цену у «{title}».", $"«{title}» үчүн санын жана баасын текшериңиз.", $"Check the quantity and price of “{title}”.", $"«{title}» için adet ve fiyatı kontrol edin.", $"«{title}» uchun soni va narxni tekshiring."));
                return;
            }
            r.Line.Title = title;
            r.Line.Qty = qty.Value;
            r.Line.Price = price.Value;
            lines.Add(r.Line);
        }
        if (lines.Count == 0)
        {
            Fail(T("Добавьте хотя бы одну позицию.", "Жок дегенде бир позиция кошуңуз.", "Add at least one item.", "En az bir kalem ekleyin.", "Kamida bitta pozitsiya qo'shing."));
            return;
        }
        var name = (_name.Text ?? "").Trim();
        var phone = (_phone.Text ?? "").Trim();
        if (name.Length == 0 && phone.Length == 0)
        {
            Fail(T("Укажите клиента: имя или телефон.", "Кардарды көрсөтүңүз: аты же телефону.", "Enter the customer: name or phone.", "Müşteriyi girin: ad veya telefon.", "Mijozni kiriting: ism yoki telefon."));
            return;
        }
        DateTime? due = null;
        var dueText = (_due.Text ?? "").Trim();
        if (dueText.Length > 0)
        {
            if (!DateTime.TryParseExact(dueText, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                Fail(T("Срок — дата в виде дд.мм.гггг.", "Мөөнөт — кк.аа.жжжж түрүндөгү дата.", "The date must look like dd.mm.yyyy.", "Tarih gg.aa.yyyy biçiminde olmalı.", "Muddat — kk.oo.yyyy ko'rinishidagi sana."));
                return;
            }
            due = parsed;
        }
        var total = lines.Sum(l => l.Qty * l.Price);
        var prepayRaw = ServiceDeskUi.ParseMoney(_prepay.Text);
        if (prepayRaw is null or < 0m || prepayRaw > total)
        {
            Fail(T("Предоплата — сумма от 0 до итога заказа.", "Алдын ала төлөм — 0дөн заказдын жыйынтыгына чейинки сумма.", "The prepayment must be between 0 and the order total.", "Ön ödeme 0 ile sipariş toplamı arasında olmalı.", "Oldindan to'lov 0 dan buyurtma jamigacha bo'lishi kerak."));
            return;
        }
        _order.Lines = lines;
        _order.ClientId = _clientId;
        _order.ClientName = name;
        _order.Phone = phone;
        _order.DueDate = due;
        _order.Supplier = (_supplier.Text ?? "").Trim();
        _order.Comment = (_comment.Text ?? "").Trim();
        if (prepayRaw.Value != _order.Prepayment)
        {
            _order.Prepayment = prepayRaw.Value;
            _order.PrepaymentAt = prepayRaw.Value > 0m ? DateTime.Now : null;
        }
        Close(true);
    }
}
