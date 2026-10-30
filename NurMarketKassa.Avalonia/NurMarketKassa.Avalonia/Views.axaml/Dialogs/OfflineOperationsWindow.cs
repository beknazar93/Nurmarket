using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-29: «Возврат без связи» и «Оплата долга без связи» — владелец разрешил обе операции без интернета. Окно собрано в коде и работает
/// только с памятью кассы (OfflineOperationsService): чек — из очереди или из журнала кассы, клиент — из кэша клиентов. Операция ставится в очередь
/// (OfflineOperationsStore) и уходит на сервер сама, когда появится связь, после чеков очереди. Внизу — очередь операций с состоянием
/// («Ждёт отправки», «Требует проверки») и кнопкой «Повторить».</summary>
internal sealed class OfflineOperationsWindow : Window
{
    public enum Mode { Return, DebtPayment }

    private readonly Mode _mode;
    private readonly string? _preselect;
    private readonly ModalShell _shell;
    private readonly TextBlock _error = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _summary = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _queuePanel = new() { Spacing = 4 };
    private readonly Button _ok;
    private bool _busy;

    // возврат
    private readonly ListBox _salesList = new() { MaxHeight = 170 };
    private readonly StackPanel _linesPanel = new() { Spacing = 4 };
    private readonly TextBox _reason = new() { Watermark = "", MaxLength = 200, Height = 40 };
    private List<OfflineSaleSnapshot> _sales = new();
    private OfflineSaleSnapshot? _sale;
    private readonly List<(OfflineSaleLine Line, CheckBox Check, TextBox Qty)> _lineRows = new();
    private OfflineRefundPlan? _plan;
    private bool _rebuilding;

    // оплата долга
    private readonly TextBox _search = new() { Height = 40 };
    private readonly ListBox _clientsList = new() { MaxHeight = 190 };
    private readonly TextBox _amount = new() { FontSize = 20, FontWeight = FontWeight.Bold, Height = 48, MaxLength = 14 };
    private readonly ComboBox _method = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private List<OfflineClientRecord> _clients = new();
    private OfflineClientRecord? _client;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Открыть окно. Android — без вложенного цикла: ShowAsync; продолжение (обновление остатка кассы) вызывает вызывающий.</summary>
    public static Task OpenAsync(Window? owner, Mode mode, string? preselectSaleRef = null) =>
        PosDialogHost.ShowAsync(new OfflineOperationsWindow(mode, preselectSaleRef), owner);

    private OfflineOperationsWindow(Mode mode, string? preselectSaleRef)
    {
        _mode = mode;
        _preselect = preselectSaleRef;
        var title = mode == Mode.Return
            ? T("Возврат без связи", "Байланышсыз кайтаруу", "Return without a connection", "Bağlantısız iade", "Aloqasiz qaytarish")
            : T("Оплата долга без связи", "Байланышсыз карыз төлөмү", "Debt payment without a connection", "Bağlantısız borç ödemesi", "Aloqasiz qarz to'lovi");
        Title = title;
        Width = 712;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        _summary.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

        var cancel = new Button { Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(null);
        _ok = new Button
        {
            Content = mode == Mode.Return
                ? T("Оформить возврат", "Кайтарууну жасоо", "Make the return", "İadeyi yap", "Qaytarishni rasmiylashtirish")
                : T("Принять оплату", "Төлөмдү кабыл алуу", "Take the payment", "Ödemeyi al", "To'lovni qabul qilish"),
            IsDefault = true,
            IsEnabled = false,
        };
        _ok.Classes.Add("modal-primary");
        _ok.Click += async (_, _) => await OnOkAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Soft(T("Работает без интернета: чек и клиент берутся из памяти кассы, операция сохраняется в очереди и уйдёт на сервер сама, когда появится связь.",
            "Интернетсиз иштейт: чек жана кардар кассанын эсинен алынат, операция кезекте сакталат жана байланыш пайда болгондо серверге өзү кетет.",
            "Works without the internet: the receipt and the client come from the till's memory, the operation is kept in the queue and goes to the server by itself once the connection is back.",
            "İnternetsiz çalışır: fiş ve müşteri kasanın hafızasından alınır, işlem kuyrukta saklanır ve bağlantı gelince sunucuya kendiliğinden gider.",
            "Internetsiz ishlaydi: chek va mijoz kassa xotirasidan olinadi, operatsiya navbatda saqlanadi va aloqa tiklanganda serverga o'zi ketadi.")));
        if (mode == Mode.Return)
            BuildReturn(body);
        else
            BuildDebt(body);
        body.Children.Add(_summary);
        body.Children.Add(_error);
        body.Children.Add(Sub(T("Операции без связи", "Байланышсыз операциялар", "Operations without a connection", "Bağlantısız işlemler", "Aloqasiz operatsiyalar")));
        body.Children.Add(_queuePanel);

        _shell = new ModalShell { Margin = new Thickness(16), Title = title, Footer = buttons, Content = body };
        Content = _shell;
        Opened += (_, _) => RefreshQueue();
        OfflineOperationsStore.Changed += OnStoreChanged;
        Closed += (_, _) => OfflineOperationsStore.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshQueue);

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private TextBlock Sub(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        return t;
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    // ────────────────────────────── возврат ──────────────────────────────

    private void BuildReturn(StackPanel body)
    {
        body.Children.Add(Sub(T("Чек (из памяти кассы)", "Чек (кассанын эсинен)", "Receipt (from the till's memory)", "Fiş (kasanın hafızasından)", "Chek (kassa xotirasidan)")));
        _sales = OfflineOperationsService.ListAvailableSales();
        _salesList.ItemsSource = _sales.Select(SaleTitle).ToList();
        _salesList.SelectionChanged += (_, _) => OnSaleSelected();
        body.Children.Add(_salesList);
        if (_sales.Count == 0)
            body.Children.Add(Soft(OfflineOperationsService.NotKnownLocallyText));
        if (!string.IsNullOrWhiteSpace(_preselect))
        {
            var at = _sales.FindIndex(s => string.Equals(s.SaleRef, _preselect, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
                _salesList.SelectedIndex = at;
            else
                ShowError(OfflineOperationsService.FindSale(_preselect!).Error);
        }
        body.Children.Add(_linesPanel);
        body.Children.Add(Sub(T("Причина возврата", "Кайтаруу себеби", "Return reason", "İade nedeni", "Qaytarish sababi")));
        body.Children.Add(_reason);
    }

    private string SaleTitle(OfflineSaleSnapshot s)
    {
        var at = s.CreatedAt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        var queued = s.IsLocal ? " · " + OfflineDebtRules.QueuedNote : "";
        var num = !string.IsNullOrWhiteSpace(s.ReceiptNumber) ? s.ReceiptNumber : OfflineOperationsService.ShortRef(s.SaleRef);
        var debt = s.Debt > 0.005m ? " · " + T("в долг", "карызга", "on credit", "veresiye", "qarzga") : "";
        return $"{at} · {num} · {MoneyText.Som(s.Total)}{debt}{queued}";
    }

    private void OnSaleSelected()
    {
        _sale = _salesList.SelectedIndex is >= 0 and var i && i < _sales.Count ? _sales[i] : null;
        _linesPanel.Children.Clear();
        _lineRows.Clear();
        _plan = null;
        ShowError(null);
        if (_sale is null)
        {
            RecalcReturn();
            return;
        }

        // Остаток к возврату по строкам — чтобы не предлагать больше, чем осталось.
        var priors = OfflineRefundPlanner.PriorRefunds(_sale.SaleRef, SaleReturnLedger.Get(_sale.SaleRef));
        _rebuilding = true;
        try
        {
            foreach (var line in _sale.Lines)
            {
                var done = priors.SelectMany(p => p.Lines).Where(l => l.Key == line.Key || (l.Key is null && string.Equals(l.Name, line.Name, StringComparison.OrdinalIgnoreCase))).Sum(l => l.Quantity);
                var left = Math.Max(0, line.Quantity - done);
                var check = new CheckBox { Content = $"{line.Name} — {Q(line.Quantity)} × {MoneyText.Amount(line.UnitPrice)}", IsEnabled = left > 1e-6, MinWidth = 320 };
                var qty = new TextBox { Text = Q(left), Width = 90, IsEnabled = left > 1e-6, Height = 36 };
                var hint = Soft(T($"осталось {Q(left)}", $"калды {Q(left)}", $"left {Q(left)}", $"kalan {Q(left)}", $"qoldi {Q(left)}"));
                check.IsCheckedChanged += (_, _) => RecalcReturn();
                qty.TextChanged += (_, _) => RecalcReturn();
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, qty, hint } };
                _linesPanel.Children.Add(row);
                _lineRows.Add((line, check, qty));
            }
        }
        finally
        {
            _rebuilding = false;
        }
        RecalcReturn();
    }

    private static string Q(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void RecalcReturn()
    {
        if (_rebuilding)
            return;
        _plan = null;
        _ok.IsEnabled = false;
        ShowError(null);
        if (_sale is null)
        {
            _summary.Text = "";
            return;
        }

        var requested = new List<(string Key, double Quantity)>();
        foreach (var (line, check, qty) in _lineRows)
        {
            if (check.IsChecked != true)
                continue;
            if (!double.TryParse((qty.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var q) || q <= 0)
            {
                _summary.Text = "";
                ShowError(T($"«{line.Name}»: укажите количество больше нуля.", $"«{line.Name}»: санды нөлдөн чоң киргизиңиз.", $"“{line.Name}”: enter a quantity above zero.",
                    $"«{line.Name}»: sıfırdan büyük bir miktar girin.", $"«{line.Name}»: noldan katta miqdor kiriting."));
                return;
            }
            requested.Add((line.Key, q));
        }

        if (requested.Count == 0)
        {
            _summary.Text = T("Отметьте позиции для возврата.", "Кайтаруу үчүн позицияларды белгилеңиз.", "Select the items to return.", "İade edilecek kalemleri işaretleyin.", "Qaytariladigan pozitsiyalarni belgilang.");
            return;
        }

        _plan = OfflineOperationsService.PlanRefund(_sale, requested);
        if (!_plan.Ok)
        {
            _summary.Text = "";
            ShowError(_plan.Error);
            return;
        }

        _summary.Text = RefundGiveText(_plan);
        _ok.IsEnabled = !_busy;
    }

    private static string RefundGiveText(OfflineRefundPlan plan)
    {
        var parts = new List<string>();
        if (plan.Cash > 0.005m)
            parts.Add(T("наличными ", "накталай ", "in cash ", "nakit ", "naqd ") + MoneyText.Som(plan.Cash));
        if (plan.NonCash > 0.005m)
            parts.Add(T("на карту/перевод ", "картага/которуу ", "to the card/transfer ", "karta/havale ", "kartaga/o'tkazma ") + MoneyText.Som(plan.NonCash));
        var give = !plan.SplitKnown ? MoneyText.Som(plan.Cash + plan.NonCash) : parts.Count == 0 ? MoneyText.Som(0m) : string.Join(" · ", parts);
        var text = (plan.Whole ? T("Возвращается ВЕСЬ чек. ", "Чек ТОЛУГУ МЕНЕН кайтарылат. ", "The WHOLE receipt is returned. ", "Fişin TAMAMI iade ediliyor. ", "Chekning BUTUNI qaytariladi. ") : "")
            + T($"К возврату: {MoneyText.Som(plan.Amount)}. Выдать покупателю: {give}.", $"Кайтаруу: {MoneyText.Som(plan.Amount)}. Сатып алуучуга берүү: {give}.",
            $"To return: {MoneyText.Som(plan.Amount)}. Give the customer: {give}.", $"İade: {MoneyText.Som(plan.Amount)}. Müşteriye verilecek: {give}.", $"Qaytarish: {MoneyText.Som(plan.Amount)}. Xaridorga berish: {give}.");
        if (plan.DebtReduced > 0.005m)
            text += "\n" + T($"Долг клиента уменьшится на {MoneyText.Som(plan.DebtReduced)} — эти деньги не выдаются.", $"Кардардын карызы {MoneyText.Som(plan.DebtReduced)} азаят — бул акча берилбейт.",
                $"The client's debt will go down by {MoneyText.Som(plan.DebtReduced)} — this money is not paid out.", $"Müşterinin borcu {MoneyText.Som(plan.DebtReduced)} azalacak — bu para verilmez.",
                $"Mijozning qarzi {MoneyText.Som(plan.DebtReduced)} ga kamayadi — bu pul berilmaydi.");
        if ((plan.DebtUnknown || plan.DebtNeedsCheck) && plan.DebtReduced > 0.005m)
            text += "\n" + T("Текущий долг клиента кассе неизвестен: если клиент уже погасил долг по этому чеку, ему нужно вернуть деньги — проверьте перед выдачей.",
                "Кардардын азыркы карызы кассага белгисиз: ал бул чек боюнча карызын төлөп койгон болсо, акчасын кайтаруу керек — берүүдөн мурун текшериңиз.",
                "The client's current debt is unknown to the till: if the client has already paid this receipt's debt, they must get the money back — check before paying out.",
                "Müşterinin mevcut borcu kasaya bilinmiyor: müşteri bu fişin borcunu zaten ödediyse parası iade edilmeli — vermeden önce kontrol edin.",
                "Mijozning joriy qarzi kassaga noma'lum: mijoz bu chek qarzini to'lab bo'lgan bo'lsa, pulini qaytarish kerak — berishdan oldin tekshiring.");
        if (plan.NonCash > 0.005m && plan.SplitKnown)
            text += "\n" + T("На карту/перевод — вернуть через банк, не из кассы.", "Картага/которуу — банк аркылуу кайтарыңыз, кассадан эмес.",
                "Card/transfer part — refund through the bank, not from the till.", "Kart/havale kısmı — kasadan değil, banka üzerinden iade edin.", "Karta/o'tkazma qismi — kassadan emas, bank orqali qaytaring.");
        return text;
    }

    private async Task CommitReturnAsync()
    {
        if (_sale is null || _plan is not { Ok: true } plan)
            return;
        var sale = _sale;
        var total = MoneyText.Som(plan.Amount);
        var confirm = await PosMessageBox.ShowModalAsync(this,
            T($"Сумма возврата: {total}\n\n{RefundGiveText(plan)}\n\nВозврат оформляется без связи и уйдёт на сервер позже.",
                $"Кайтаруу суммасы: {total}\n\n{RefundGiveText(plan)}\n\nКайтаруу байланышсыз жасалат жана серверге кийин кетет.",
                $"Return amount: {total}\n\n{RefundGiveText(plan)}\n\nThe return is made without a connection and goes to the server later.",
                $"İade tutarı: {total}\n\n{RefundGiveText(plan)}\n\nİade bağlantısız yapılır ve sunucuya sonra gider.",
                $"Qaytarish summasi: {total}\n\n{RefundGiveText(plan)}\n\nQaytarish aloqasiz rasmiylashtiriladi va serverga keyin ketadi."),
            T("Подтверждение возврата", "Кайтарууну ырастоо", "Confirm return", "İadeyi onayla", "Qaytarishni tasdiqlash"),
            MessageBoxButton.YesNo, MessageBoxImage.Question,
            confirmText: T($"Вернуть {total}", $"{total} кайтаруу", $"Refund {total}", $"{total} iade et", $"{total} qaytarish")).ConfigureAwait(true);
        if (confirm != MessageBoxResult.Yes)
            return;

        var reason = (_reason.Text ?? "").Trim();
        OfflineOperationEntry op;
        try
        {
            op = OfflineOperationsService.CommitRefund(sale, plan, string.IsNullOrWhiteSpace(reason) ? null : reason, NurMarketKassa.PosApp.CurrentUserDisplayName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowError(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            PosLogger.Log($"OFFLINE: возврат не записан в очередь: {ex.Message}", "PAYMENT ERROR");
            ShowError(OfflineOperationsStore.IsCorrupt ? OfflineOperationsStore.CorruptText : OfflineOperationsService.QueueWriteFailedText);
            return;
        }

        // Чек возврата с пометкой «оформлен без связи» — как и с интернетом, выдача денег подтверждается бумагой. Печать в фоне.
        var printLines = plan.Lines.Select(l => (Name: l.Name, Quantity: l.Quantity > 0 ? l.Quantity : 1, UnitPrice: l.Quantity > 0 ? l.Amount / (decimal)l.Quantity : l.Amount, Sum: l.Amount)).ToList();
        var printNumber = sale.ReceiptNumber ?? OfflineOperationsService.ShortRef(sale.SaleRef);
        var printReason = "ОФОРМЛЕН БЕЗ СВЯЗИ" + (string.IsNullOrWhiteSpace(reason) ? "" : " · " + reason);
        var printTask = Task.Run(() => OperationReceiptPrinter.PrintReturn(printNumber, printLines, plan.Amount, printReason, plan.Whole, op.CashierName));
        var printError = await printTask.ConfigureAwait(true);

        var done = T("Возврат оформлен без связи и ждёт отправки.", "Кайтаруу байланышсыз жасалды жана жөнөтүүнү күтөт.", "The return was made without a connection and is waiting to be sent.",
                "İade bağlantısız yapıldı ve gönderilmeyi bekliyor.", "Qaytarish aloqasiz rasmiylashtirildi va yuborilishini kutmoqda.")
            + "\n" + RefundGiveText(plan)
            + (printError is null ? "" : "\n\n" + T("Чек возврата не напечатан — принтер не подключён или не отвечает.", "Кайтаруу чеги басылган жок — принтер туташкан эмес же жооп бербейт.",
                "The return receipt was not printed — the printer is not connected or not responding.", "İade fişi yazdırılmadı — yazıcı bağlı değil veya yanıt vermiyor.", "Qaytarish cheki chop etilmadi — printer ulanmagan yoki javob bermayapti."));
        PosMessageBox.Show(this, done, T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);

        // Список чеков и строки — заново (остатки к возврату изменились).
        _sales = OfflineOperationsService.ListAvailableSales();
        _salesList.ItemsSource = _sales.Select(SaleTitle).ToList();
        _salesList.SelectedIndex = _sales.FindIndex(s => string.Equals(s.SaleRef, sale.SaleRef, StringComparison.OrdinalIgnoreCase));
        if (_salesList.SelectedIndex < 0)
            OnSaleSelected();
        RefreshQueue();
    }

    // ────────────────────────────── оплата долга ──────────────────────────────

    private void BuildDebt(StackPanel body)
    {
        body.Children.Add(Sub(T("Клиент (из памяти кассы)", "Кардар (кассанын эсинен)", "Client (from the till's memory)", "Müşteri (kasanın hafızasından)", "Mijoz (kassa xotirasidan)")));
        _search.Watermark = T("Имя или телефон", "Аты же телефону", "Name or phone", "Ad veya telefon", "Ism yoki telefon");
        _search.TextChanged += (_, _) => FillClients();
        body.Children.Add(_search);
        _clientsList.SelectionChanged += (_, _) => OnClientSelected();
        body.Children.Add(_clientsList);

        body.Children.Add(Sub(T("Сумма оплаты, сом", "Төлөм суммасы, сом", "Payment amount, som", "Ödeme tutarı, som", "To'lov summasi, so'm")));
        _amount.TextChanged += (_, _) => RecalcDebt();
        body.Children.Add(_amount);

        var methods = DebtPaymentSettings.MethodsHere();
        if (methods.Count == 0)
            methods = new List<string> { DebtPaymentSettings.MethodCash };
        _method.ItemsSource = methods.Select(m => m == DebtPaymentSettings.MethodTransfer
            ? T("Безналом (карта, перевод)", "Накталай эмес (карта, которуу)", "Non-cash (card, transfer)", "Nakitsiz (kart, havale)", "Naqd emas (karta, o'tkazma)")
            : T("Наличными", "Накталай", "Cash", "Nakit", "Naqd")).ToList();
        _method.SelectedIndex = 0;
        _methodCodes = methods.ToList();
        if (methods.Count > 1)
        {
            body.Children.Add(Sub(T("Способ оплаты", "Төлөм ыкмасы", "Payment method", "Ödeme yöntemi", "To'lov usuli")));
            body.Children.Add(_method);
        }

        _clients = OfflineClientCache.All();
        FillClients();
        if (_clients.Count == 0)
            body.Children.Add(Soft(T("В памяти кассы нет клиентов: они сохраняются, пока есть связь (раз в полчаса и при открытии списка клиентов). Оплату долга можно принять, когда связь появится.",
                "Кассанын эсинде кардарлар жок: байланыш болгондо сакталат (жарым саатта бир жолу жана кардарлар тизмеси ачылганда). Байланыш пайда болгондо төлөмдү алыңыз.",
                "There are no clients in the till's memory: they are saved while there is a connection (every half hour and when the client list is opened). Take the payment once the connection is back.",
                "Kasanın hafızasında müşteri yok: bağlantı varken kaydedilir (yarım saatte bir ve müşteri listesi açıldığında). Ödemeyi bağlantı gelince alın.",
                "Kassa xotirasida mijozlar yo'q: aloqa bor paytda saqlanadi (har yarim soatda va mijozlar ro'yxati ochilganda). To'lovni aloqa tiklanganda qabul qiling.")));
    }

    private List<string> _methodCodes = new() { DebtPaymentSettings.MethodCash };

    private void FillClients()
    {
        var query = (_search.Text ?? "").Trim();
        var shown = _clients
            .Where(c => query.Length == 0 ? (OfflineClientCache.KnownDebt(c, false) ?? 0m) > 0.005m : SearchMatch.Matches(query, c.Name + " " + c.Phone))
            .Take(60)
            .ToList();
        _clientsShown = shown;
        _clientsList.ItemsSource = shown.Select(ClientTitle).ToList();
    }

    private List<OfflineClientRecord> _clientsShown = new();

    private static string ClientTitle(OfflineClientRecord c)
    {
        var known = OfflineClientCache.KnownDebt(c, includeRejectedSales: false);
        var stamp = OfflineDebtRules.DataStamp(c.DebtAt, DateTimeOffset.Now);
        var debt = known is { } d
            ? T($"долг {MoneyText.Som(d)} ({stamp})", $"карыз {MoneyText.Som(d)} ({stamp})", $"debt {MoneyText.Som(d)} ({stamp})", $"borç {MoneyText.Som(d)} ({stamp})", $"qarz {MoneyText.Som(d)} ({stamp})")
            : T("долг неизвестен", "карыз белгисиз", "debt unknown", "borç bilinmiyor", "qarz noma'lum");
        return $"{c.Name}{(string.IsNullOrWhiteSpace(c.Phone) ? "" : " · " + c.Phone)} · {debt}";
    }

    private void OnClientSelected()
    {
        _client = _clientsList.SelectedIndex is >= 0 and var i && i < _clientsShown.Count ? _clientsShown[i] : null;
        if (_client != null && OfflineClientCache.KnownDebt(_client, false) is { } known && known > 0.005m && string.IsNullOrWhiteSpace(_amount.Text))
            _amount.Text = known.ToString("0.##", CultureInfo.InvariantCulture);
        RecalcDebt();
    }

    private OfflineDebtVerdict? _debtVerdict;

    private void RecalcDebt()
    {
        _ok.IsEnabled = false;
        ShowError(null);
        _summary.Text = "";
        _debtVerdict = null;
        if (_client is null)
            return;
        if (!CashAmountInput.TryParse(_amount.Text, out var amount) || amount <= 0m)
            return;
        var verdict = OfflineOperationsService.CheckDebtPayment(_client.Id, amount);
        _debtVerdict = verdict;
        if (verdict.Kind == OfflineDebtVerdictKind.Block)
        {
            ShowError(verdict.Message);
            return;
        }
        _summary.Text = T($"Принять {MoneyText.Som(amount)} от «{_client.Name}» ({verdict.DataNote}). Оплата уйдёт на сервер, когда появится связь.",
            $"«{_client.Name}» кардарынан {MoneyText.Som(amount)} кабыл алуу ({verdict.DataNote}). Төлөм байланыш пайда болгондо серверге кетет.",
            $"Take {MoneyText.Som(amount)} from “{_client.Name}” ({verdict.DataNote}). The payment goes to the server once the connection is back.",
            $"«{_client.Name}» müşterisinden {MoneyText.Som(amount)} al ({verdict.DataNote}). Ödeme bağlantı gelince sunucuya gider.",
            $"«{_client.Name}» mijozidan {MoneyText.Som(amount)} qabul qilish ({verdict.DataNote}). To'lov aloqa tiklanganda serverga ketadi.");
        _ok.IsEnabled = !_busy;
    }

    private async Task CommitDebtPaymentAsync()
    {
        if (_client is null || !CashAmountInput.TryParse(_amount.Text, out var amount) || amount <= 0m)
            return;
        var client = _client;
        if (string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.ActiveShiftId))
        {
            ShowError(OfflineOperationsService.NoShiftText);
            return;
        }

        // 2026-10-29 (ревью, В-5): онлайн-оплата долга этого клиента оборвалась без ответа — сервер мог её провести; вторая такая же без связи недопустима.
        if (DebtPaymentRunner.UnknownFor(client.Id) is not null)
        {
            ShowError(T("По этому клиенту есть оплата долга без ответа сервера (онлайн): она могла пройти. Дождитесь связи и сверьте её в «Оплате долга», потом принимайте новую.",
                "Бул кардар боюнча сервердин жообусуз карыз төлөмү бар (онлайн): ал өтүп кетиши мүмкүн. Байланышты күтүп, аны «Карыз төлөөдө» салыштырыңыз, анан жаңысын алыңыз.",
                "This client has a debt payment without a server reply (online): it may have gone through. Wait for the connection and check it in “Pay debt”, then take a new one.",
                "Bu müşteri için sunucu yanıtı olmayan (çevrimiçi) bir borç ödemesi var: gerçekleşmiş olabilir. Bağlantıyı bekleyin, «Borç ödemesi»nde kontrol edin, sonra yenisini alın.",
                "Bu mijoz bo'yicha server javobisiz (onlayn) qarz to'lovi bor: o'tib ketgan bo'lishi mumkin. Aloqani kuting, «Qarz to'lovi»da tekshiring, so'ng yangisini qabul qiling."));
            return;
        }

        // Код сотрудника на приём оплаты — как в обычной «Оплате долга».
        var (granted, codeError) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
        if (!granted)
        {
            ShowError(codeError);
            return;
        }

        var method = _method.SelectedIndex is >= 0 and var mi && mi < _methodCodes.Count ? _methodCodes[mi] : DebtPaymentSettings.MethodCash;
        OfflineOperationEntry op;
        try
        {
            op = OfflineOperationsService.CommitDebtPayment(client.Id, amount, method, NurMarketKassa.PosApp.CurrentUserDisplayName);
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            PosLogger.Log($"OFFLINE: оплата долга не записана в очередь: {ex.Message}", "PAYMENT ERROR");
            ShowError(OfflineOperationsStore.IsCorrupt ? OfflineOperationsStore.CorruptText : OfflineOperationsService.QueueWriteFailedText);
            return;
        }

        // Приходный чек с пометкой «без связи» (печать в фоне).
        var receiptReason = T($"Оплата долга: {client.Name} (оформлена без связи)", $"Карыз төлөмү: {client.Name} (байланышсыз жасалды)", $"Debt payment: {client.Name} (made without a connection)",
            $"Borç ödemesi: {client.Name} (bağlantısız yapıldı)", $"Qarz to'lovi: {client.Name} (aloqasiz rasmiylashtirildi)");
        var printError = await Task.Run(() => OperationReceiptPrinter.PrintCashOperation(false, op.Amount, receiptReason, op.CashierName)).ConfigureAwait(true);
        var done = T($"Оплата долга {MoneyText.Som(op.Amount)} принята без связи и ждёт отправки.", $"Карыз төлөмү {MoneyText.Som(op.Amount)} байланышсыз кабыл алынды жана жөнөтүүнү күтөт.",
                $"The debt payment of {MoneyText.Som(op.Amount)} was taken without a connection and is waiting to be sent.", $"{MoneyText.Som(op.Amount)} tutarındaki borç ödemesi bağlantısız alındı ve gönderilmeyi bekliyor.",
                $"{MoneyText.Som(op.Amount)} qarz to'lovi aloqasiz qabul qilindi va yuborilishini kutmoqda.")
            + (op.Method == DebtPaymentSettings.MethodTransfer ? "" : "\n" + T("Наличные — в ящик, в смену кассира.", "Накталай — кутуга, кассирдин сменасына.", "Cash goes into the drawer and the cashier's shift.", "Nakit çekmeceye ve kasiyerin vardiyasına gider.", "Naqd — qutiga, kassir smenasiga."))
            + (printError is null ? "" : "\n\n" + T("Чек не напечатан — принтер не подключён или не отвечает.", "Чек басылган жок — принтер туташкан эмес же жооп бербейт.",
                "The receipt was not printed — the printer is not connected or not responding.", "Fiş yazdırılmadı — yazıcı bağlı değil veya yanıt vermiyor.", "Chek chop etilmadi — printer ulanmagan yoki javob bermayapti."));
        PosMessageBox.Show(this, done, T("Оплата долга", "Карыз төлөмү", "Debt payment", "Borç ödemesi", "Qarz to'lovi"), MessageBoxButton.OK, MessageBoxImage.Information);

        _amount.Text = "";
        _clients = OfflineClientCache.All();
        FillClients();
        RecalcDebt();
        RefreshQueue();
    }

    // ────────────────────────────── общее ──────────────────────────────

    private async Task OnOkAsync()
    {
        if (_busy)
            return;
        _busy = true;
        _ok.IsEnabled = false;
        try
        {
            if (_mode == Mode.Return)
                await CommitReturnAsync().ConfigureAwait(true);
            else
                await CommitDebtPaymentAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: операция без связи не оформлена: {ex}", "PAYMENT ERROR");
            ShowError(PaymentErrorMessages.ForCashier(ex));
        }
        finally
        {
            _busy = false;
            if (_mode == Mode.Return)
                RecalcReturn();
            else
                RecalcDebt();
        }
    }

    private void RefreshQueue()
    {
        _queuePanel.Children.Clear();
        if (OfflineOperationsStore.IsCorrupt)
        {
            var warn = new TextBlock { Text = OfflineOperationsStore.CorruptText, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            warn.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
            var accept = new Button { Content = T("Я проверил операции, продолжить работу", "Операцияларды текшердим, улантуу", "I checked the operations, continue", "İşlemleri kontrol ettim, devam et", "Operatsiyalarni tekshirdim, davom etish"), HorizontalAlignment = HorizontalAlignment.Left };
            accept.Click += (_, _) =>
            {
                OfflineOperationsStore.ClearCorrupt();
                RefreshQueue();
            };
            _queuePanel.Children.Add(warn);
            _queuePanel.Children.Add(accept);
            return;
        }

        List<OfflineOperationEntry> ops;
        try
        {
            ops = OfflineOperationsStore.LoadAll()
                .Where(o => o.Status != OfflineOperationEntry.Synced && o.LastError != OfflineOperationsStore.CanceledMarker && (_mode == Mode.Return ? o.Kind == OfflineOperationEntry.KindRefund : o.Kind == OfflineOperationEntry.KindDebtPayment))
                .OrderByDescending(o => o.CreatedAt).Take(12).ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: очередь операций для окна не прочитана: {ex.Message}", "WARNING");
            ops = new();
        }

        if (ops.Count == 0)
        {
            _queuePanel.Children.Add(Soft(T("Очередь пуста.", "Кезек бош.", "The queue is empty.", "Kuyruk boş.", "Navbat bo'sh.")));
            return;
        }

        foreach (var op in ops)
        {
            var failed = op.Status == OfflineOperationEntry.Failed;
            var line = new TextBlock
            {
                Text = $"{op.CreatedAt.ToLocalTime():dd.MM HH:mm} · {OfflineOperationsService.Title(op)} · {OfflineOperationsService.StatusLabel(op)}"
                       + (string.IsNullOrWhiteSpace(op.LastError) ? "" : "\n" + op.LastError),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            };
            line.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(failed ? "BrushDanger" : "BrushText"));
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(line);
            if (failed)
            {
                var retry = new Button { Content = T("Повторить отправку", "Жөнөтүүнү кайталоо", "Retry sending", "Göndermeyi tekrarla", "Yuborishni takrorlash"), HorizontalAlignment = HorizontalAlignment.Left };
                var id = op.Id;
                retry.Click += (_, _) =>
                {
                    OfflineOperationsStore.Requeue(id);
                    RefreshQueue();
                };
                row.Children.Add(retry);
                // 2026-10-30 (приёмка 1.17.65, «Важно»): «Требует проверки» нельзя было закрыть — плашка «Отклонено» висела вечно, деньги считались дважды.
                var closeBtn = new Button { Content = T("Проверено, закрыть без отправки", "Текшерилди, жөнөтпөй жабуу", "Checked, close without sending", "Kontrol edildi, göndermeden kapat", "Tekshirildi, yubormasdan yopish"), HorizontalAlignment = HorizontalAlignment.Left };
                closeBtn.Click += async (_, _) =>
                {
                    var answer = await PosMessageBox.ShowModalAsync(this,
                        T("Операция НЕ будет отправлена на сервер и пропадёт из очереди и плашки «Отклонено». Закрывайте, только если вы проверили чек на сервере (или деньги уже учтены вручную). Закрыть?",
                            "Операция серверге ЖӨНӨТҮЛБӨЙТ, кезектен жана «Четке кагылды» тактасынан жоголот. Чекти серверден текшергенден кийин (же акча кол менен эсепке алынган болсо) гана жабыңыз. Жабабызбы?",
                            "The operation will NOT be sent to the server and will disappear from the queue and the “Rejected” badge. Close it only if you checked the receipt on the server (or the money is already accounted for by hand). Close it?",
                            "İşlem sunucuya GÖNDERİLMEYECEK; kuyruktan ve «Reddedildi» rozetinden kalkacak. Yalnızca fişi sunucuda kontrol ettiyseniz (veya para elle hesaba katıldıysa) kapatın. Kapatılsın mı?",
                            "Amal serverga YUBORILMAYDI va navbatdan hamda «Rad etilgan» belgisidan yo'qoladi. Faqat chekni serverda tekshirgan bo'lsangiz (yoki pul qo'lda hisobga olingan bo'lsa) yoping. Yopilsinmi?"),
                        T("Закрыть без отправки", "Жөнөтпөй жабуу", "Close without sending", "Göndermeden kapat", "Yubormasdan yopish"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning).ConfigureAwait(true);
                    if (answer != MessageBoxResult.Yes)
                        return;
                    OfflineOperationsStore.CloseWithoutSending(id, NurMarketKassa.AvaloniaHost.App.CurrentUserId);
                    PosDataEvents.RaiseSalesChanged();
                    RefreshQueue();
                };
                row.Children.Add(closeBtn);
            }
            _queuePanel.Children.Add(row);
        }
    }
}
