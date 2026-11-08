using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-08 (ТЗ п. 32, Б-39): тариф «Своё» в программе владельца — пакеты функций поверх «Старта». Список пакетов с галочками и ценами С СЕРВЕРА
/// (GET public/tariffs/, цены временные — владелец меняет их в админке), живой пересчёт через users/company/tariff/quote/ (с задержкой 350 мс после последней галочки),
/// подсказка «Стандарт дешевле», пометка «уже подключено», кнопка «Отправить заявку» с подтверждением и Idempotency-Key (GUID на попытку; повтор с тем же ключом безопасен).
/// Сервер только записывает заявку, её обрабатывает менеджер: платёжной ссылки нет.</summary>
public sealed class TariffCustomWindow : Window
{
    private sealed class PackRow
    {
        public required TariffPack Pack { get; init; }
        public required CheckBox Box { get; init; }
        public required TextBlock Monthly { get; init; }
        public required TextBlock Once { get; init; }
        public required Border Active { get; init; }
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ITariffApi _api;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _intro = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _info = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _standardHint = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _done = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _submit;
    private readonly Button _close;
    private readonly Dictionary<string, PackRow> _packRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private TariffCatalog? _catalog;
    private TariffQuote? _quote;
    private int _version;
    private int _loadStarted;
    private bool _locked;
    private bool _submitting;
    private bool _suppress; // MarkActive ставит галочку из кода — это не выбор владельца
    private CancellationTokenSource? _quoteCts;
    private string? _attemptKey;

    /// <summary>Свой вопрос подтверждения (для проверок без окна-вопроса); по умолчанию — обычное окно «Отправить / Отмена».</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Что видно в итоге и на кнопках — для проверок.</summary>
    public string SummaryText => _summary.Text ?? "";
    public bool CanSubmit => _submit.IsEnabled;
    public string? DoneText => _done.IsVisible ? _done.Text : null;
    public string? ErrorText => _error.IsVisible ? _error.Text : null;
    public Task SubmitForTestAsync() => SubmitAsync();
    public Task QuoteForTestAsync() { _debounce.Stop(); return RequestQuoteAsync(); }
    public void SetChecked(string code, bool value) => _packRows[code].Box.IsChecked = value;

    public TariffCustomWindow(ITariffApi api)
    {
        _api = api;
        Title = T("Тариф «Своё»", "«Өзүмдүкү» тарифи", "“Custom” plan", "«Özel» tarife", "«O'zimniki» tarifi");
        SectionCards.AsModal(this, 672);

        _intro.Text = T("Соберите свой набор функций поверх «Старта». Отметьте нужные пакеты — цены считает сервер. Заявку обработает менеджер и свяжется с вами; платить сейчас ничего не нужно.",
            "«Старттын» үстүнө өз функциялар топтомун чогултуңуз. Керектүү пакеттерди белгилеңиз — баасын сервер эсептейт. Өтүнүчтү менеджер иштетип, сиз менен байланышат; азыр эч нерсе төлөбөйсүз.",
            "Build your own set of features on top of “Start”. Tick the packs you need — the server calculates the prices. A manager will process the request and contact you; nothing to pay now.",
            "«Start» üzerine kendi özellik setinizi oluşturun. İhtiyacınız olan paketleri işaretleyin — fiyatları sunucu hesaplar. Talebi bir yönetici işleyip sizinle iletişime geçer; şimdi ödeme yapmanız gerekmez.",
            "«Start» ustiga o'z funksiyalar to'plamingizni yig'ing. Kerakli paketlarni belgilang — narxni server hisoblaydi. Arizani menejer ko'rib chiqib, siz bilan bog'lanadi; hozir hech narsa to'lash shart emas.");
        Soft(_intro);
        Soft(_info);
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        _standardHint.Bind(TextBlock.ForegroundProperty, _standardHint.GetResourceObservable("BrushSuccess"));
        _done.Bind(TextBlock.ForegroundProperty, _done.GetResourceObservable("BrushSuccess"));
        _info.Text = T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");

        var summaryBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = new StackPanel { Spacing = 4, Children = { _summary, _standardHint } } };
        summaryBox.Bind(Border.BackgroundProperty, summaryBox.GetResourceObservable("BrushPanelSoft"));

        _close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.Click += (_, _) => Close();
        _submit = SectionCards.Primary(T("Отправить заявку", "Өтүнүч жөнөтүү", "Send request", "Talep gönder", "Ariza yuborish"));
        _submit.IsEnabled = false;
        _submit.Click += async (_, _) => await SubmitAsync().ConfigureAwait(true);

        Content = SectionCards.Shell(Title, null,
            // 2026-11-08: итог и результат — над списком: на 1024×600 список прокручивается, итог должен быть виден без прокрутки.
            new StackPanel { Spacing = 10, Children = { _intro, _info, SectionCards.ErrorBox(this, _error), _done, summaryBox, _rows } },
            SectionCards.Buttons(_close, _submit));

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RequestQuoteAsync();
        };
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closed += (_, _) =>
        {
            _debounce.Stop();
            _quoteCts?.Cancel();
        };
        RenderSummary();
    }

    private void Soft(TextBlock t) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));

    private static string M(decimal value)
    {
        var a = MoneyText.Amount(value);
        if (a.EndsWith(",00", StringComparison.Ordinal))
            a = a[..^3];
        return a + " " + MoneyText.Currency;
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Каталог пакетов и первый расчёт по всем пакетам: из него видно, какие уже подключены. Для тестов — вызывается и вручную; повторный вызов ничего не делает.</summary>
    public async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) != 0)
            return;
        try
        {
            _catalog = await _api.GetCatalogAsync().ConfigureAwait(true);
            if (_catalog.Packs.Count == 0)
            {
                _info.Text = T("Сервер пока не отдал список пакетов. Попробуйте позже.", "Сервер пакеттердин тизмесин азырынча берген жок. Кийинчерээк аракет кылыңыз.",
                    "The server has not returned the pack list yet. Try again later.", "Sunucu paket listesini henüz vermedi. Daha sonra deneyin.", "Server paketlar ro'yxatini hali bermadi. Keyinroq urinib ko'ring.");
                return;
            }

            BuildRows(_catalog);
            _info.Text = "";
            _info.IsVisible = false;
            try
            {
                // Один расчёт по всем пакетам — узнаём, какие уже подключены (already_active), и сразу проверяем, что тариф «Своё» этой компании доступен.
                var all = await _api.QuoteAsync(_catalog.Packs.Select(p => p.Code).ToList()).ConfigureAwait(true);
                foreach (var qp in all.Packs.Where(p => p.AlreadyActive))
                    MarkActive(qp.Code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowError(TariffApi.Describe(ex));
                if (ex is ApiException { StatusCode: 403 or 409 })
                    Lock();
            }

            RenderSummary();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Тариф «Своё»: каталог не загружен: {ex.Message}", "WARNING");
            _info.IsVisible = true;
            _info.Text = "";
            ShowError(TariffApi.Describe(ex));
            Interlocked.Exchange(ref _loadStarted, 0); // «Закрыть и открыть заново» — повтор загрузки
        }
    }

    private void Lock()
    {
        _locked = true;
        foreach (var row in _packRows.Values)
            row.Box.IsEnabled = false;
        _submit.IsEnabled = false;
    }

    private void BuildRows(TariffCatalog catalog)
    {
        _rows.Children.Clear();
        _packRows.Clear();
        foreach (var pack in catalog.Packs)
        {
            var name = new TextBlock { Text = pack.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
            var content = new StackPanel { Spacing = 1, Children = { name } };
            if (!string.IsNullOrWhiteSpace(pack.Description))
            {
                var desc = new TextBlock { Text = pack.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                Soft(desc);
                content.Children.Add(desc);
            }

            var box = new CheckBox { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, MinHeight = 44 };
            Avalonia.Automation.AutomationProperties.SetName(box, pack.Name);
            var monthly = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            monthly.Bind(TextBlock.ForegroundProperty, monthly.GetResourceObservable("BrushText"));
            var once = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            Soft(once);
            var activeText = new TextBlock { Text = T("уже подключено", "мурунтан туташкан", "already connected", "zaten bağlı", "allaqachon ulangan"), FontSize = 12, FontWeight = FontWeight.SemiBold };
            activeText.Bind(TextBlock.ForegroundProperty, activeText.GetResourceObservable("BrushSuccess"));
            var active = new Border { Child = activeText, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            var price = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { monthly, once, active } };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(box, 0);
            Grid.SetColumn(price, 1);
            grid.Children.Add(box);
            grid.Children.Add(price);
            _rows.Children.Add(grid);

            var row = new PackRow { Pack = pack, Box = box, Monthly = monthly, Once = once, Active = active };
            _packRows[pack.Code] = row;
            ShowListPrice(row);
            box.IsCheckedChanged += (_, _) => OnSelectionChanged();
        }
    }

    /// <summary>Цена из каталога: «400 сом / мес» и «+ 1 500 сом разово».</summary>
    private void ShowListPrice(PackRow row)
    {
        row.Monthly.Text = M(row.Pack.MonthlyFee) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy");
        row.Once.Text = T("+ ", "+ ", "+ ", "+ ", "+ ") + M(row.Pack.ActivationFee) + " " + T("разово", "бир жолу", "one-off", "tek sefer", "bir marta");
    }

    private void MarkActive(string code)
    {
        if (!_packRows.TryGetValue(code, out var row))
            return;
        _active.Add(row.Pack.Code);
        _suppress = true;
        row.Box.IsChecked = true;
        _suppress = false;
        row.Box.IsEnabled = false;
        row.Monthly.IsVisible = false;
        row.Once.IsVisible = false;
        row.Active.IsVisible = true;
    }

    private List<string> Selected() =>
        _packRows.Values.Where(r => r.Box.IsChecked == true && !_active.Contains(r.Pack.Code)).Select(r => r.Pack.Code).ToList();

    private void OnSelectionChanged()
    {
        if (_suppress)
            return;
        // Набор изменился: прежний расчёт и ключ попытки больше не относятся к нему.
        _version++;
        _quote = null;
        _attemptKey = null;
        _done.IsVisible = false;
        ShowError(null);
        _submit.IsEnabled = false;
        _debounce.Stop();
        _quoteCts?.Cancel();
        RenderSummary();
        if (Selected().Count > 0)
            _debounce.Start();
    }

    private async Task RequestQuoteAsync()
    {
        var codes = Selected();
        if (codes.Count == 0)
            return;
        var version = _version;
        _quoteCts?.Cancel();
        var cts = _quoteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _summary.Text = T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
        try
        {
            var quote = await _api.QuoteAsync(codes, cts.Token).ConfigureAwait(true);
            if (version != _version)
                return; // пока считали, отметки поменялись — ответ устарел
            _quote = quote;
            foreach (var qp in quote.Packs)
            {
                if (qp.AlreadyActive)
                    MarkActive(qp.Code);
                else if (_packRows.TryGetValue(qp.Code, out var row))
                {
                    row.Monthly.Text = M(qp.Monthly) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy");
                    row.Once.Text = "+ " + M(qp.Once) + " " + T("разово", "бир жолу", "one-off", "tek sefer", "bir marta");
                }
            }

            RenderSummary();
            _submit.IsEnabled = !_locked && !_submitting && Selected().Count > 0;
        }
        catch (OperationCanceledException) when (version != _version || cts.IsCancellationRequested && !IsVisible)
        {
            // устаревший расчёт — молча
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            ShowError(TariffApi.Describe(ex));
            RenderSummary();
            if (ex is ApiException { StatusCode: 403 or 409 })
                Lock();
        }
    }

    private void RenderSummary()
    {
        _standardHint.IsVisible = false;
        if (_quote is null)
        {
            _summary.Text = Selected().Count == 0
                ? T("Отметьте пакеты — здесь появится стоимость.", "Пакеттерди белгилеңиз — бул жерде баасы пайда болот.", "Tick packs — the cost will appear here.", "Paketleri işaretleyin — maliyet burada görünecek.", "Paketlarni belgilang — narxi shu yerda paydo bo'ladi.")
                : _summary.Text;
            return;
        }

        var q = _quote;
        var baseName = _catalog?.Plan("start")?.Name ?? T("Старт", "Старт", "Start", "Start", "Start");
        var lines = new List<string>
        {
            T($"Основа ({baseName}): {M(q.Base)} в месяц", $"Негиз ({baseName}): айына {M(q.Base)}", $"Base ({baseName}): {M(q.Base)} a month", $"Temel ({baseName}): ayda {M(q.Base)}", $"Asos ({baseName}): oyiga {M(q.Base)}"),
            T($"Пакеты: {M(q.Monthly)} в месяц", $"Пакеттер: айына {M(q.Monthly)}", $"Packs: {M(q.Monthly)} a month", $"Paketler: ayda {M(q.Monthly)}", $"Paketlar: oyiga {M(q.Monthly)}"),
            T($"Подключение (один раз): {M(q.Once)}", $"Туташтыруу (бир жолу): {M(q.Once)}", $"Activation (one-off): {M(q.Once)}", $"Etkinleştirme (tek sefer): {M(q.Once)}", $"Ulash (bir marta): {M(q.Once)}"),
            T($"Итого в месяц: {M(q.Base + q.Monthly)}", $"Айына бардыгы: {M(q.Base + q.Monthly)}", $"Total a month: {M(q.Base + q.Monthly)}", $"Aylık toplam: {M(q.Base + q.Monthly)}", $"Oyiga jami: {M(q.Base + q.Monthly)}"),
        };
        _summary.Text = string.Join("\n", lines);

        if (q.StandardCheaper)
        {
            var standard = _catalog?.Plan("standard")?.Name ?? T("Стандарт", "Стандарт", "Standard", "Standart", "Standart");
            var price = q.StandardPrice is { } sp ? M(sp) : "";
            _standardHint.Text = T($"«{standard}» выйдет дешевле: {price} в месяц, и все пакеты уже входят.", $"«{standard}» арзаныраак болот: айына {price}, бардык пакеттер кирет.",
                $"“{standard}” is cheaper: {price} a month, all packs included.", $"«{standard}» daha ucuz: ayda {price}, tüm paketler dahil.", $"«{standard}» arzonroq: oyiga {price}, barcha paketlar kiradi.");
            _standardHint.IsVisible = true;
        }
    }

    private async Task SubmitAsync()
    {
        var codes = Selected();
        if (_submitting || _locked || codes.Count == 0 || _quote is null)
            return;
        var q = _quote;
        var names = string.Join(", ", codes.Select(c => _packRows.TryGetValue(c, out var r) ? r.Pack.Name : c));
        var ask = T($"Отправить заявку на тариф «Своё»?\n\nПакеты: {names}.\nВ месяц: {M(q.Base + q.Monthly)} (из них пакеты — {M(q.Monthly)}). Подключение один раз: {M(q.Once)}.\n\nЗаявку обработает менеджер и свяжется с вами; тариф и пакеты включатся после этого.",
            $"«Өзүмдүкү» тарифине өтүнүч жөнөтөсүзбү?\n\nПакеттер: {names}.\nАйына: {M(q.Base + q.Monthly)} (анын ичинде пакеттер — {M(q.Monthly)}). Туташтыруу бир жолу: {M(q.Once)}.\n\nӨтүнүчтү менеджер иштетип, сиз менен байланышат; тариф жана пакеттер ошондон кийин иштейт.",
            $"Send a request for the “Custom” plan?\n\nPacks: {names}.\nPer month: {M(q.Base + q.Monthly)} (packs — {M(q.Monthly)}). Activation, once: {M(q.Once)}.\n\nA manager will process it and contact you; the plan and packs are switched on after that.",
            $"«Özel» tarife için talep gönderilsin mi?\n\nPaketler: {names}.\nAylık: {M(q.Base + q.Monthly)} (paketler — {M(q.Monthly)}). Etkinleştirme, tek sefer: {M(q.Once)}.\n\nTalebi bir yönetici işleyip sizinle iletişime geçer; tarife ve paketler bundan sonra açılır.",
            $"«O'zimniki» tarifiga ariza yuborilsinmi?\n\nPaketlar: {names}.\nOyiga: {M(q.Base + q.Monthly)} (paketlar — {M(q.Monthly)}). Ulash, bir marta: {M(q.Once)}.\n\nArizani menejer ko'rib chiqib, siz bilan bog'lanadi; tarif va paketlar shundan keyin yoqiladi.");
        var confirmed = Confirm is { } custom
            ? await custom(ask).ConfigureAwait(true)
            : await PosDialogs.ConfirmYesNoModalAsync(this, ask, Title, T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish")).ConfigureAwait(true);
        if (!confirmed)
            return;

        // Ключ — на попытку: при обрыве связи нажатие «Отправить» ещё раз уходит с ТЕМ ЖЕ ключом (сервер вернёт ту же заявку, не вторую).
        // Набор пакетов изменился — ключ новый (OnSelectionChanged обнуляет), успех или окончательный отказ сервера — тоже.
        var key = _attemptKey ??= Guid.NewGuid().ToString("D");
        _submitting = true;
        _submit.IsEnabled = false;
        ShowError(null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var order = await _api.SubmitCustomAsync(codes, key, cts.Token).ConfigureAwait(true);
            _attemptKey = null;
            _done.Text = T($"Заявка №{order.OrderId} принята — менеджер свяжется с вами. Платить сейчас ничего не нужно.",
                $"№{order.OrderId} өтүнүч кабыл алынды — менеджер сиз менен байланышат. Азыр эч нерсе төлөбөйсүз.",
                $"Request No. {order.OrderId} received — a manager will contact you. Nothing to pay now.",
                $"{order.OrderId} numaralı talep alındı — bir yönetici sizinle iletişime geçecek. Şimdi ödeme gerekmez.",
                $"№{order.OrderId} ariza qabul qilindi — menejer siz bilan bog'lanadi. Hozir to'lash shart emas.");
            _done.IsVisible = true;
            foreach (var row in _packRows.Values)
                row.Box.IsEnabled = false;
            _locked = true;
            _close.Content = T("Готово", "Даяр", "Done", "Tamam", "Tayyor");
        }
        catch (Exception ex)
        {
            if (ex is ApiException { StatusCode: >= 400 and < 500 and not 408 and not 429 })
                _attemptKey = null; // сервер ответил по существу — следующая попытка новая
            PosLogger.Log($"Тариф «Своё»: заявка не отправлена: {ex.Message}", "WARNING");
            ShowError(TariffApi.Describe(ex) + (_attemptKey is null ? "" : "\n" + T("Можно нажать «Отправить заявку» ещё раз — заявка не задвоится.", "«Өтүнүч жөнөтүү» баскычын дагы бир жолу басса болот — өтүнүч эки эсе болбойт.",
                "You can press “Send request” again — the request will not be duplicated.", "«Talep gönder»e tekrar basabilirsiniz — talep ikiye katlanmaz.", "«Ariza yuborish»ni yana bosish mumkin — ariza ikkilanmaydi.")));
            if (ex is ApiException { StatusCode: 403 or 409 })
                Lock();
        }
        finally
        {
            _submitting = false;
            _submit.IsEnabled = !_locked && Selected().Count > 0 && _quote is not null;
        }
    }
}
