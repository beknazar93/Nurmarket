using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (склад «10 из 10», К-8): окно «Резервы» — список резервов (GET reservations/), «Зарезервировать» (товар, количество, комментарий, срок; POST с Idempotency-Key),
/// «Снять» (DELETE). Свободного не хватило — сервер отвечает 409 insufficient_available: окно говорит об этом понятно и предлагает «Всё равно зарезервировать» (allow_over).
/// Открывается из Склад → «⋯» → «Резервы» ТОЛЬКО при признаке reservations. Сеть — через переданные функции (в проверках подставляются заглушки).</summary>
public sealed class ReservationsWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public sealed record Ops(
        Func<CancellationToken, Task<List<StockReservation>>> List,
        Func<string, double, string, DateTime?, bool, string, Task> Reserve,
        Func<string, Task> Release,
        Func<string, IReadOnlyList<CatalogProductTileVm>> Search)
    {
        /// <summary>Настоящие запросы к серверу и поиск по каталогу памяти.</summary>
        public static Ops Live() => new(
            ct => StockServerApi.ReservationsAsync(null, ct),
            (product, qty, comment, until, allowOver, key) => StockServerApi.ReserveAsync(product, qty, comment, until, allowOver, key),
            id => StockServerApi.ReleaseAsync(id),
            text => CatalogCacheService.Products
                .Where(p => p.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || (p.Article?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (text.Length >= 4 && (p.Barcode?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(8).ToList());
    }

    private readonly Ops _ops;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _listState = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _productBox = new() { Name = "ReserveProduct" };
    private readonly StackPanel _hits = new() { Spacing = 2 };
    private readonly TextBox _qtyBox = new() { Name = "ReserveQty", Text = "1" };
    private readonly TextBox _commentBox = new() { Name = "ReserveComment" };
    private readonly TextBox _untilBox = new() { Name = "ReserveUntil" };
    private readonly Button _reserve;
    private readonly Button _force;
    private CatalogProductTileVm? _product;
    private string _lastSig = "", _lastKey = "";
    private bool _busy;

    /// <summary>Строки списка (для проверки).</summary>
    public StackPanel RowsPanel => _rows;
    public Button ForceButton => _force;
    public TextBlock Message => _message;

    public ReservationsWindow(Ops ops, CatalogProductTileVm? product = null)
    {
        _ops = ops;
        var title = T("Резервы", "Резервдер", "Reservations", "Rezervler", "Zaxiralar");
        Title = title;
        Width = 912;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        _productBox.Watermark = T("Товар: название или штрихкод", "Товар: аты же штрихкоду", "Product: name or barcode", "Ürün: ad veya barkod", "Mahsulot: nomi yoki shtrix-kod");
        Avalonia.Automation.AutomationProperties.SetName(_productBox, _productBox.Watermark);
        _productBox.TextChanged += (_, _) => ShowHits();
        _qtyBox.Watermark = T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
        Avalonia.Automation.AutomationProperties.SetName(_qtyBox, _qtyBox.Watermark);
        _commentBox.Watermark = T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh");
        Avalonia.Automation.AutomationProperties.SetName(_commentBox, _commentBox.Watermark);
        _untilBox.Watermark = T("Держать до (дд.мм.гггг)", "Чейин сактоо (кк.аа.жжжж)", "Hold until (dd.mm.yyyy)", "Şu tarihe kadar (gg.aa.yyyy)", "Qadar saqlash (kk.oo.yyyy)");
        Avalonia.Automation.AutomationProperties.SetName(_untilBox, _untilBox.Watermark);
        foreach (var b in new[] { _productBox, _qtyBox, _commentBox, _untilBox })
            b.MinHeight = 40;

        _reserve = new Button { Name = "ReserveSubmit", Content = T("Зарезервировать", "Резервге коюу", "Reserve", "Rezerve et", "Zaxiralash") };
        _reserve.Classes.Add("modal-primary");
        _reserve.Click += async (_, _) => await SubmitAsync(false).ConfigureAwait(true);
        _force = new Button { Name = "ReserveForce", Content = T("Всё равно зарезервировать", "Баары бир резервге коюу", "Reserve anyway", "Yine de rezerve et", "Baribir zaxiralash"), IsVisible = false };
        _force.Classes.Add("modal-cancel");
        _force.Click += async (_, _) => await SubmitAsync(true).ConfigureAwait(true);

        var close = new Button { Name = "ReservationsClose", Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,2*,1.6*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        Place(form, _productBox, 0, 0);
        Place(form, _qtyBox, 1, 0);
        Place(form, _commentBox, 2, 0);
        Place(form, _untilBox, 3, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _reserve, _force } };

        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("BrushWarning"));
        _listState.Bind(TextBlock.ForegroundProperty, _listState.GetResourceObservable("BrushTextSoft"));
        _listState.Text = T("Загружаем резервы…", "Резервдер жүктөлүүдө…", "Loading reservations…", "Rezervler yükleniyor…", "Zaxiralar yuklanmoqda…");

        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = title,
            Subtitle = T("Товар отложен для клиента и не продаётся как свободный", "Товар кардар үчүн сакталган жана бош катары сатылбайт", "Goods set aside for a customer are not sold as free stock", "Müşteri için ayrılan ürün serbest stok olarak satılmaz", "Mijoz uchun ajratilgan tovar bo'sh qoldiq sifatida sotilmaydi"),
            Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            Content = new StackPanel { Spacing = 8, Children = { form, _hits, _message, buttons, new Border { Height = 8 }, _listState, _rows } },
        };
        if (product is not null)
            Pick(product);
        Opened += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    private static void Place(Grid g, Control c, int col, int row)
    {
        c.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(c, col);
        Grid.SetRow(c, row);
        g.Children.Add(c);
    }

    private void Pick(CatalogProductTileVm p)
    {
        _product = p;
        _productBox.Text = p.Title;
        _hits.Children.Clear();
    }

    private void ShowHits()
    {
        _hits.Children.Clear();
        var text = (_productBox.Text ?? "").Trim();
        if (text.Length < 2 || (_product is not null && string.Equals(_product.Title, text, StringComparison.Ordinal)))
            return;
        _product = null;
        foreach (var p in _ops.Search(text))
        {
            var b = new Button { Content = p.Title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Tag = "reserve-hit", MinHeight = 36 };
            b.Classes.Add("sec-outline");
            var item = p;
            b.Click += (_, _) => Pick(item);
            _hits.Children.Add(b);
        }
    }

    private void Say(string text, bool warn = true)
    {
        _message.Text = text;
        _message.IsVisible = text.Length > 0;
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable(warn ? "BrushWarning" : "BrushSuccess"));
    }

    private async Task SubmitAsync(bool allowOver)
    {
        if (_busy)
            return;
        if (_product is null)
        {
            Say(T("Выберите товар из списка.", "Тизмеден товарды тандаңыз.", "Choose a product from the list.", "Listeden bir ürün seçin.", "Ro'yxatdan mahsulotni tanlang."));
            return;
        }
        if (!double.TryParse((_qtyBox.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            Say(T("Укажите количество больше нуля.", "Нөлдөн чоң санды көрсөтүңүз.", "Enter a quantity above zero.", "Sıfırdan büyük miktar girin.", "Noldan katta miqdorni kiriting."));
            return;
        }
        DateTime? until = null;
        var untilText = (_untilBox.Text ?? "").Trim();
        if (untilText.Length > 0)
        {
            if (!DateTime.TryParseExact(untilText, new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                Say(T("Дата — в виде дд.мм.гггг.", "Күнү — кк.аа.жжжж түрүндө.", "Enter the date as dd.mm.yyyy.", "Tarihi gg.aa.yyyy olarak girin.", "Sanani kk.oo.yyyy ko'rinishida kiriting."));
                return;
            }
            until = d;
        }
        // Тот же запрос после обрыва связи — тот же ключ повтора (сервер не создаст второй резерв); «Всё равно» — отдельный запрос, новый ключ.
        var sig = $"{_product.Id}|{qty.ToString("R", CultureInfo.InvariantCulture)}|{_commentBox.Text}|{until:yyyy-MM-dd}|{allowOver}";
        if (sig != _lastSig)
        {
            _lastSig = sig;
            // 2026-11-19: ключ повтора без дефисов (не числовой формат для экрана)
            _lastKey = Guid.NewGuid().ToString().Replace("-", "");
        }
        _busy = true;
        try
        {
            await _ops.Reserve(_product.Id, qty, (_commentBox.Text ?? "").Trim(), until, allowOver, _lastKey).ConfigureAwait(true);
            _lastSig = "";
            _force.IsVisible = false;
            Say(T("Резерв создан.", "Резерв түзүлдү.", "Reservation created.", "Rezerv oluşturuldu.", "Zaxira yaratildi."), warn: false);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (ApiException ex) when (ServerStock.TryParseInsufficient(ex, out var free))
        {
            _lastSig = "";
            _force.IsVisible = true;
            Say(free is { } f
                ? T($"Свободного остатка не хватает: свободно {ServerStock.Q(f)}. Можно зарезервировать всё равно — тогда продать этот товар будет нельзя.", $"Бош калдык жетишсиз: бош {ServerStock.Q(f)}. Баары бир резервге койсо болот — анда бул товарды сатууга болбойт.",
                    $"Not enough free stock: {ServerStock.Q(f)} free. You can reserve anyway — then this stock cannot be sold.", $"Serbest stok yetersiz: serbest {ServerStock.Q(f)}. Yine de rezerve edebilirsiniz — o zaman bu stok satılamaz.", $"Bo'sh qoldiq yetarli emas: bo'sh {ServerStock.Q(f)}. Baribir zaxiralash mumkin — u holda bu tovarni sotib bo'lmaydi.")
                : T("Свободного остатка не хватает. Можно зарезервировать всё равно — тогда продать этот товар будет нельзя.", "Бош калдык жетишсиз. Баары бир резервге койсо болот — анда бул товарды сатууга болбойт.",
                    "Not enough free stock. You can reserve anyway — then this stock cannot be sold.", "Serbest stok yetersiz. Yine de rezerve edebilirsiniz — o zaman bu stok satılamaz.", "Bo'sh qoldiq yetarli emas. Baribir zaxiralash mumkin — u holda bu tovarni sotib bo'lmaydi."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Резерв: не создан ({ex.Message}).", "WARNING");
            Say(T("Не удалось создать резерв: ", "Резерв түзүү мүмкүн болгон жок: ", "Could not create the reservation: ", "Rezerv oluşturulamadı: ", "Zaxira yaratib bo'lmadi: ") + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            Fill(await _ops.List(CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Резервы: список не получен ({ex.Message}).", "WARNING");
            _listState.IsVisible = true;
            _listState.Text = T("Не удалось загрузить резервы. Проверьте связь и откройте окно снова.", "Резервдерди жүктөө мүмкүн болгон жок. Байланышты текшерип, терезени кайра ачыңыз.",
                "Could not load the reservations. Check the connection and open the window again.", "Rezervler yüklenemedi. Bağlantıyı kontrol edip pencereyi yeniden açın.", "Zaxiralarni yuklab bo'lmadi. Aloqani tekshirib, oynani qayta oching.");
        }
    }

    /// <summary>Заполнить список (публично для проверки без сети).</summary>
    public void Fill(IReadOnlyList<StockReservation> list)
    {
        _rows.Children.Clear();
        _listState.IsVisible = list.Count == 0;
        _listState.Text = T("Резервов нет.", "Резервдер жок.", "No reservations.", "Rezerv yok.", "Zaxiralar yo'q.");
        foreach (var r in list)
            _rows.Children.Add(Row(r));
    }

    private Control Row(StockReservation r)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("2.4*,0.9*,2*,1.2*,Auto"), Margin = new Thickness(0, 6), Tag = "reservation-row" };
        var cells = new[]
        {
            r.ProductName.Length > 0 ? r.ProductName : r.ProductId,
            ServerStock.Q(r.Qty),
            r.Comment.Length > 0 ? r.Comment : "—",
            r.ExpiresAt is { } e ? T("до ", "чейин ", "until ", "şu tarihe kadar ", "gacha ") + e.ToString("dd.MM.yyyy") : "—",
        };
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        var release = new Button { Content = T("Снять", "Алып салуу", "Release", "Kaldır", "Olib tashlash"), MinHeight = 36, Tag = "reservation-release" };
        release.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(release, release.Content + " " + cells[0]);
        var id = r.Id;
        release.Click += async (_, _) =>
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                await _ops.Release(id).ConfigureAwait(true);
                Say(T("Резерв снят.", "Резерв алынды.", "Reservation released.", "Rezerv kaldırıldı.", "Zaxira olib tashlandi."), warn: false);
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Резерв: не снят ({ex.Message}).", "WARNING");
                Say(T("Не удалось снять резерв: ", "Резервди алуу мүмкүн болгон жок: ", "Could not release the reservation: ", "Rezerv kaldırılamadı: ", "Zaxirani olib tashlab bo'lmadi: ") + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        };
        Grid.SetColumn(release, 4);
        g.Children.Add(release);
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
        line.Bind(Border.BorderBrushProperty, line.GetResourceObservable("BrushBorder"));
        return line;
    }
}
