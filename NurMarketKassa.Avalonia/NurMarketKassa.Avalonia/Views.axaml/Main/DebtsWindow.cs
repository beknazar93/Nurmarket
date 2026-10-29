using NurMarketKassa.Core.Application;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «аналитика по долгам! при нажатии на неё должен подробно показывать долги». Раздел программы
/// владельца «Долги клиентов»: сколько должны магазину (сумма, клиенты, чеки, самый старый долг), разбивка по давности,
/// по каждому клиенту — сумма, с какого числа, телефон и напоминание в WhatsApp; по нажатию — все его чеки в долг (дата,
/// номер, товар, сумма чека, внесено, остаток, кассир). Данные — продажи со статусом debt с сервера NurCRM
/// (ISalesApiService.PosDebtSalesAsync), телефоны — из списка клиентов. Окно собрано в коде, как «Продажи в убыток».</summary>
public sealed class DebtsWindow : Window
{
    public DebtsWindow()
    {
        Title = DebtsView.TitleText;
        Width = 1100;
        Height = 800;
        Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        // 2026-10-18, этап О4 (аудит «Долги»): окно открывается только разделом программы владельца — название уже в полосе
        // над разделом, второго крупного заголовка нет; поля — как у остальных разделов.
        Content = new DebtsView(showTitle: false, OwnerSectionLayout.Margin);
    }
}

/// <summary>2026-10-05, владелец: «где в сводке и аналитике долги??» — содержимое «Долгов клиентов» отдельным блоком:
/// то же самое и в разделе «Долги клиентов», и вкладкой «Долги» в «Аналитике». Данные загружаются, когда блок впервые
/// показан на экране. Карточка «Долги клиентов» в «Сводке» берёт итоги из <see cref="LoadDebtorsAsync"/>.</summary>
public sealed class DebtsView : UserControl
{
    public static string TitleText => T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari");

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TextBlock _summary = new SummaryText { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    /// <summary>2026-10-19, вторая живая проверка: TextBlock с Inlines отдаёт в UIA пустое имя (его peer берёт Text, а не
    /// AutomationProperties.Name). Этот — то же оформление (StyleKey TextBlock), имя для UIA — из AutomationProperties.Name.</summary>
    private sealed class SummaryText : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer : Avalonia.Automation.Peers.ControlAutomationPeer
        {
            public Peer(Control owner) : base(owner) { }

            protected override Avalonia.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                Avalonia.Automation.Peers.AutomationControlType.Text;

            protected override string GetClassNameCore() => "TextBlock";

            protected override string? GetNameCore() => Avalonia.Automation.AutomationProperties.GetName(Owner);
        }
    }
    private readonly StackPanel _aging = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 12) };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBox _search;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private List<Debtor> _debtors = new();
    private string _sort = "amount";
    private int _loadGen;
    private bool _loadStarted;

    /// <param name="Checking">2026-10-18, девятый круг: точный остаток сделки не получен — показана прежняя сумма с пометкой «уточняется».</param>
    /// <param name="Duplicate">2026-10-23 (аудит, круг 8, В-3): «Долг без продажи», похожий на двойную запись чека — показан с пометкой.
    /// 2026-10-24 (приёмка, ПР-1): в сумму долга ВХОДИТ (Left = остаток сделки), как на сервере; не оплачивается «по чеку».</param>
    internal sealed record DebtSale(string Id, string Number, DateTime At, string Item, double Total, double Paid, double Left, string Cashier, string Cashbox,
        bool Checking = false, bool Duplicate = false);

    internal sealed record Debtor(string ClientId, string Name, string Phone, List<DebtSale> Sales)
    {
        public double Total => Sales.Sum(s => s.Left);
        public DateTime Oldest => (Sales.Any(s => !s.Duplicate) ? Sales.Where(s => !s.Duplicate) : Sales).Min(s => s.At); // 2026-10-23 (В-3): «похоже на двойную запись» срок долга не задаёт; 2026-10-24 (ПР-1): а если других строк нет — по ней
        public bool Checking => Sales.Any(s => s.Checking);
    }

    public DebtsView(bool showTitle, Thickness? margin = null)
    {
        var root = new Grid { Margin = margin ?? (showTitle ? new Thickness(24, 16, 24, 24) : new Thickness(8, 4, 8, 8)), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        var title = new TextBlock { Text = TitleText, FontSize = 22, FontWeight = FontWeight.Bold, IsVisible = showTitle };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = T("Кто и сколько должен магазину — по данным NurCRM, со всех касс. Нажмите на клиента, чтобы увидеть его чеки в долг.",
                "Ким дүкөнгө канча карыз — NurCRM маалыматы боюнча, бардык кассалардан. Карыз чектерин көрүү үчүн кардарды басыңыз.",
                "Who owes the shop and how much — from NurCRM data, all tills. Tap a customer to see their receipts on credit.",
                "Mağazaya kim ne kadar borçlu — NurCRM verilerinden, tüm kasalar. Veresiye fişlerini görmek için müşteriye dokunun.",
                "Do'konga kim qancha qarz — NurCRM ma'lumotlari bo'yicha, barcha kassalardan. Qarzga cheklarini ko'rish uchun mijozni bosing."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, showTitle ? 6 : 0, 0, 12),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _search = UiKit.Search(this, T("Поиск по имени или телефону…", "Аты же телефону боюнча издөө…", "Search by name or phone…", "Ad veya telefona göre ara…", "Ism yoki telefon bo'yicha qidirish…"), 44); // 2026-10-27, аудит владельца С-5: было 40 (цель нажатия)
        _search.TextChanged += (_, _) => Render();
        tools.Children.Add(_search);
        var byAmount = UiKit.Chip(this, T("По сумме", "Сумма боюнча", "By amount", "Tutara göre", "Summa bo'yicha"), true);
        var byAge = UiKit.Chip(this, T("По давности", "Эскилиги боюнча", "By age", "Süreye göre", "Muddati bo'yicha"), false);
        void SetSort(string sort)
        {
            _sort = sort;
            // 2026-10-18, этап О3: выбранный чип — классом active (тёмно-синий, UiKit), а не светло-жёлтой заливкой на месте.
            UiKit.SetActive(byAmount, sort == "amount");
            UiKit.SetActive(byAge, sort == "age");
            Render();
        }
        byAmount.Margin = new Thickness(10, 0, 0, 0);
        byAge.Margin = new Thickness(8, 0, 0, 0);
        byAmount.Click += (_, _) => SetSort("amount");
        byAge.Click += (_, _) => SetSort("age");
        Grid.SetColumn(byAmount, 1);
        Grid.SetColumn(byAge, 2);
        tools.Children.Add(byAmount);
        tools.Children.Add(byAge);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Grid.SetColumn(refresh, 3);
        tools.Children.Add(refresh);
        tools.Classes.Add("no-reflow");

        // 2026-10-18, этап О4 (аудит «Долги», правило 4): строка итога — цветом текста, красная только сумма долга (SetSummary).
        Use(_summary, TextBlock.ForegroundProperty, "BrushText");
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(hint, 1);
        Grid.SetRow(tools, 2);
        Grid.SetRow(_summary, 3);
        Grid.SetRow(_aging, 4);
        Grid.SetRow(scroll, 5);
        root.Children.Add(title);
        root.Children.Add(hint);
        root.Children.Add(tools);
        root.Children.Add(_summary);
        root.Children.Add(_aging);
        root.Children.Add(scroll);

        SetSummary(T("Загружаю долги…", "Карыздар жүктөлүүдө…", "Loading debts…", "Borçlar yükleniyor…", "Qarzlar yuklanmoqda…"));
        ShowLoading();
        Content = root;
        AttachedToVisualTree += async (_, _) =>
        {
            // 2026-10-18: оплата долга здесь, в кассе или в другой программе (LAN «debt_paid») — список перечитывается;
            // подписка — только пока раздел на экране (как у других окон: статическое событие не держит закрытое окно).
            // Восьмой круг (п.9): общая отложенная рассылка PosDataEvents.DebouncedDebtChanged (серия — одна, через 3 с).
            PosDataEvents.DebouncedDebtChanged -= OnDebtsChanged;
            PosDataEvents.DebouncedDebtChanged += OnDebtsChanged;
            // Долг изменился, пока раздел был скрыт, — перечитать при показе.
            if (_loadStarted && _seenDebtVersion == PosDataEvents.DebtVersion)
                return;
            _loadStarted = true;
            await LoadAsync().ConfigureAwait(true);
        };
        DetachedFromVisualTree += (_, _) => PosDataEvents.DebouncedDebtChanged -= OnDebtsChanged;
    }

    /// <summary>2026-10-18: версия долгов (PosDataEvents.DebtVersion), на которой начата последняя полная загрузка, — скрытый
    /// раздел по ней понимает, что его список устарел.</summary>
    private long _seenDebtVersion;
    /// <summary>2026-10-18, восьмой круг (п.5): версия, с которой начата последняя ПРИМЕНЁННАЯ полная загрузка, и по клиентам —
    /// последняя применённая перезагрузка карточки. Ответ, начатый раньше уже применённого, не затирает свежую карточку.</summary>
    private long _fullAppliedVersion;
    private readonly Dictionary<string, long> _clientAppliedVersion = new(StringComparer.OrdinalIgnoreCase);

    private void OnDebtsChanged(DebtChangeBatch batch) => Dispatcher.UIThread.Post(async () =>
    {
        if (!_loadStarted)
            return;
        var clients = batch.Clients;
        try
        {
            if (clients is null || _debtors.Count == 0)
            {
                await LoadAsync().ConfigureAwait(true);
                return;
            }
            foreach (var id in clients)
            {
                // Клиента нет в непустом списке (у него долга не было) — оплата его долга список не меняет.
                if (!_debtors.Any(d => string.Equals(d.ClientId, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // 2026-10-18, девятый круг (п.9): карточка уже перечитана не раньше последнего изменения (например, сразу после
                // «Принять оплату») — второй раз не читаем.
                if (_clientAppliedVersion.TryGetValue(id, out var applied) && applied >= PosDataEvents.DebtVersionOf(id))
                    continue;
                await ReloadClientAsync(id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долги клиентов: перечитывание после оплаты не удалось ({ex.Message}).", "WARNING");
        }
    });

    /// <summary>2026-10-18, шестой круг ревью: перечитать долги одного клиента (pos/sales/?status=debt&amp;client=…) и заменить
    /// его карточку; телефон — прежний. Долга не осталось — карточка убирается. Восьмой круг (п.5): ответ применяется, только
    /// если запрос начат не раньше уже применённой полной загрузки и прошлой перезагрузки этого клиента.</summary>
    private async Task ReloadClientAsync(string clientId)
    {
        var startVersion = PosDataEvents.DebtVersion;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var salesApi = App.GetRequiredService<ISalesApiService>();
        var sales = await salesApi.PosDebtSalesAsync(clientId, cts.Token).ConfigureAwait(true);
        // 2026-10-18, девятый круг (п.3): остаток — точный, по сделкам (тот же загрузчик, что у окон оплаты).
        var exact = await PayDebtDialog.LoadDebtRowsAsync(salesApi, clientId, sales, cts.Token).ConfigureAwait(true);
        // 2026-10-22 (аудит 1.17.59, П-Е): и «Долг без продажи» клиента — тем же расчётом, что у окон оплаты.
        await AppendNoSaleAsync(clientId, exact, cts.Token).ConfigureAwait(true);
        if (startVersion < _fullAppliedVersion
            || (_clientAppliedVersion.TryGetValue(clientId, out var applied) && startVersion < applied))
            return; // позже начатый ответ уже на экране
        _clientAppliedVersion[clientId] = startVersion;
        var phones = _debtors.Where(d => d.ClientId.Length > 0).GroupBy(d => d.ClientId).ToDictionary(g => g.Key, g => g.First().Phone, StringComparer.OrdinalIgnoreCase);
        var fresh = BuildDebtors(sales, phones).FirstOrDefault(d => string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        // 2026-10-22 (аудит 1.17.59, П-Е): у клиента может быть только долг без продажи — карточка строится из него.
        if (fresh is null && exact.Any(r => r.IsNoSale))
        {
            var known = _debtors.FirstOrDefault(d => string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
            fresh = new Debtor(clientId, known?.Name ?? "", known?.Phone ?? phones.GetValueOrDefault(clientId, ""), new List<DebtSale>());
        }
        if (fresh is not null)
            fresh = ApplyExact(fresh, exact);
        _debtors = _debtors.Where(d => !string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .Concat(fresh is null ? Array.Empty<Debtor>() : new[] { fresh }).ToList();
        Render();
    }

    /// <summary>2026-10-18, девятый/десятый круг: остатки чеков клиента — по точному остатку сделок (exact); чек без точного
    /// остатка (или exact null — клиента не уточнить) — прежняя сумма с пометкой «уточняется». Долга не осталось — null.</summary>
    private static Debtor? ApplyExact(Debtor d, IReadOnlyList<DebtSaleRow>? exact)
    {
        var byId = (exact ?? Array.Empty<DebtSaleRow>()).Where(r => r.Id.Length > 0).GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var refined = new List<DebtSale>();
        foreach (var sale in d.Sales)
        {
            if (byId.TryGetValue(sale.Id, out var row) && !row.RemainingUnknown)
            {
                if (row.Amount > 0.005)
                    refined.Add(sale with { Left = row.Amount, Paid = Math.Max(0, sale.Total - row.Amount), Checking = false });
            }
            else
                refined.Add(sale with { Checking = true }); // точный не получен — прежняя сумма, «уточняется»
        }
        // 2026-10-22 (аудит 1.17.59, П-Е): «Долг без продажи» (сделка клиента без чека) — в долг клиента, как в кассе и у окон оплаты.
        // 2026-10-23 (аудит, круг 8, В-3): «похоже на двойную запись» — с пометкой в раскрытом списке.
        // 2026-10-24 (приёмка, ПР-1): такая строка в сумму входит (Amount = остаток, как у сервера и в окне оплаты) — отдельной ветки с нулями нет.
        foreach (var row in exact ?? Array.Empty<DebtSaleRow>())
            if (row.IsNoSale && row.Amount > 0.005 && !refined.Any(s => string.Equals(s.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
                refined.Add(ToNoSaleSale(row));
        return refined.Count == 0 ? null : d with { Sales = refined };
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Е): строка «Долг без продажи» (DebtSaleRow.IsNoSale, Id «deal:…») → запись долга клиента.</summary>
    private static DebtSale ToNoSaleSale(DebtSaleRow r) =>
        new(r.Id, "", r.CreatedAt == default ? DateTime.Today : r.CreatedAt.LocalDateTime, r.FirstItemName, r.Amount, 0, r.Amount, "", "",
            Duplicate: r.IsSuspectedDuplicate); // 2026-10-24 (ПР-1): пометка «похоже на двойную запись» остаётся, сумма — как у сервера

    /// <summary>2026-10-22 (аудит 1.17.59, П-Е): добавить к точным строкам клиента его «Долг без продажи» (clients/{id}/deals/ —
    /// общий загрузчик кассы PayDebtDialog.LoadNoSaleDebtRowsAsync; не больше 4 запросов одновременно). Не получилось — как раньше.</summary>
    private static async Task AppendNoSaleAsync(string clientId, List<DebtSaleRow> rows, CancellationToken ct)
    {
        if (clientId.Length == 0)
            return;
        await ExactThrottle.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            rows.AddRange(await PayDebtDialog.LoadNoSaleDebtRowsAsync(clientId, rows, ct).ConfigureAwait(true));
        }
        finally
        {
            ExactThrottle.Release();
        }
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Е): клиенты, у которых есть ТОЛЬКО долг без продажи (в списке продаж в долг их нет).
    /// Кандидаты — clients/with-debts/ без поставщиков (долг поставщику — наш, не клиента) и без уже найденных должников;
    /// у каждого — тот же загрузчик долгов без продажи, что в кассе. Любой сбой — пусто (суммы как раньше), в журнал.</summary>
    private static async Task<List<Debtor>> NoSaleOnlyDebtorsAsync(IReadOnlyCollection<Debtor> known, CancellationToken ct)
    {
        var result = new List<Debtor>();
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            // Без списка поставщиков не угадать, чей это долг, — не рискуем (иначе долг поставщику попал бы в «Вам должны»).
            // 2026-10-23 (аудит, круг 7): список поставщиков и клиенты с долгами запрашиваются одновременно (были друг за другом).
            var suppliersTask = PosApp.CatalogApi.ListSuppliersAsync(ct);
            var withDebtsTask = api.RequestAsync(System.Net.Http.HttpMethod.Get, "api/main/clients/with-debts/", null,
                new Dictionary<string, string> { ["page_size"] = "500" }, ct);
            var supplierIds = new HashSet<string>(
                NurMarketApiClient.UnwrapList(await suppliersTask.ConfigureAwait(true)).Select(s => Str(s, "id") ?? "").Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            var withDebts = NurMarketApiClient.UnwrapList(await withDebtsTask.ConfigureAwait(true));
            var have = new HashSet<string>(known.Select(d => d.ClientId), StringComparer.OrdinalIgnoreCase);
            var candidates = withDebts.Select(c => (Id: Str(c, "id") ?? "", Name: Str(c, "full_name") ?? Str(c, "name") ?? "", Phone: Str(c, "phone") ?? ""))
                .Where(c => c.Id.Length > 0 && !have.Contains(c.Id) && !supplierIds.Contains(c.Id)).Take(60).ToList();
            var found = await Task.WhenAll(candidates.Select(async c =>
            {
                var rows = new List<DebtSaleRow>();
                await NoSaleThrottle.WaitAsync(ct).ConfigureAwait(true); // 2026-10-23: не больше 4 запросов сделок одновременно
                try
                {
                    await AppendNoSaleAsync(c.Id, rows, ct).ConfigureAwait(true);
                }
                finally
                {
                    NoSaleThrottle.Release();
                }
                return (c, Rows: rows.Where(r => r.IsNoSale && r.Amount > 0.005).ToList());
            })).ConfigureAwait(true);
            foreach (var (c, rows) in found.Where(f => f.Rows.Count > 0))
                result.Add(new Debtor(c.Id, c.Name, c.Phone, rows.Select(ToNoSaleSale).ToList()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Долги клиентов: долги без продажи у остальных клиентов не получены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        return result;
    }

    /// <summary>2026-10-18, десятый круг (п.4): не больше 4 запросов уточнения одновременно (раздел «Долги клиентов»).</summary>
    private static readonly SemaphoreSlim ExactThrottle = new(4, 4);

    /// <summary>2026-10-23 (аудит, круг 7): то же для поиска клиентов с «Долгом без продажи» (запросы сделок идут рядом с уточнением).</summary>
    private static readonly SemaphoreSlim NoSaleThrottle = new(4, 4);

    /// <summary>Сколько чеков уточнять до первой отрисовки; больше — сначала список с пометкой «уточняется», точные — постепенно.</summary>
    private const int ExactBeforeRenderLimit = 60;

    /// <summary>Точные остатки одного клиента по его продажам из общего списка; не получилось — null (все чеки «уточняются»).</summary>
    private static async Task<List<DebtSaleRow>?> ExactRowsAsync(string clientId, IReadOnlyList<JsonElement> sales, CancellationToken ct)
    {
        if (clientId.Length == 0)
            return null; // «Без клиента» — сделку по клиенту не запросить
        try
        {
            var rows = await PayDebtDialog.LoadDebtRowsAsync(App.GetRequiredService<ISalesApiService>(), clientId, sales, ct, ExactThrottle).ConfigureAwait(true);
            await AppendNoSaleAsync(clientId, rows, ct).ConfigureAwait(true); // 2026-10-22 (аудит 1.17.59, П-Е): + «Долг без продажи»
            return rows;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долги клиентов: точный остаток клиента {clientId} не получен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Должники с сервера NurCRM (продажи в долг с остатком), по клиентам. withPhones — ещё и телефоны из списка клиентов.</summary>
    internal static async Task<List<Debtor>> LoadDebtorsAsync(bool withPhones, CancellationToken ct) =>
        (await LoadSalesAndDebtorsAsync(withPhones, ct).ConfigureAwait(true)).Debtors;

    private static async Task<(List<JsonElement> Sales, List<Debtor> Debtors)> LoadSalesAndDebtorsAsync(bool withPhones, CancellationToken ct)
    {
        // 2026-10-23 (аудит, круг 6: «Долги клиентов» ~5 с): список клиентов (телефоны) грузится параллельно с долговыми продажами.
        var salesTask = App.GetRequiredService<ISalesApiService>().PosDebtSalesAsync(null, ct);
        var clientsTask = withPhones ? App.GetRequiredService<IClientsApiService>().GetClientsAsync(null, ct) : null;
        var sales = await salesTask.ConfigureAwait(true);
        // Телефоны — из списка клиентов (в продаже только имя). Не получилось — без телефонов.
        var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (clientsTask != null)
        {
            try
            {
                var clients = await clientsTask.ConfigureAwait(true);
                foreach (var c in clients)
                    if (Str(c, "id") is { Length: > 0 } id)
                        phones[id] = Str(c, "phone") ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Долги клиентов: телефоны не получены ({ex.Message}).", "WARNING");
            }
        }

        return (sales, BuildDebtors(sales, phones));
    }

    /// <summary>Продажи в долг → должники по клиентам (остаток — debt_remaining / debt_amount / total).</summary>
    private static List<Debtor> BuildDebtors(IEnumerable<JsonElement> sales, IReadOnlyDictionary<string, string> phones)
    {
        var debtors = new Dictionary<string, Debtor>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sales)
        {
            var total = Num(s, "total") ?? 0;
            var left = Num(s, "debt_remaining") ?? Num(s, "debt_amount") ?? total;
            if (left <= 0.005)
                continue;
            var clientId = Str(s, "client") ?? "";
            var key = clientId.Length > 0 ? clientId : "—";
            if (!debtors.TryGetValue(key, out var debtor))
            {
                debtor = new Debtor(clientId, Str(s, "client_name") ?? T("Без клиента", "Кардарсыз", "No customer", "Müşterisiz", "Mijozsiz"),
                    phones.TryGetValue(clientId, out var ph) ? ph : "", new List<DebtSale>());
                debtors[key] = debtor;
            }
            DateTime.TryParse(Str(s, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at);
            debtor.Sales.Add(new DebtSale(Str(s, "id") ?? "", Str(s, "number") ?? "", at.ToLocalTime(), Str(s, "first_item_name") ?? "", total,
                Math.Max(0, total - left), left, Str(s, "user_display") ?? "", Str(s, "cashbox_name") ?? ""));
        }
        return debtors.Values.ToList();
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-З): пока долги грузятся — полоса прогресса и серые «скелеты» строк вместо одной
    /// надписи «Загружаю долги…» (на снимке через 3 с раздел выглядел зависшим). Уже показанный список не трогаем.</summary>
    private void ShowLoading()
    {
        if (_debtors.Count > 0)
            return;
        _list.Children.Clear();
        _aging.Children.Clear();
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 0, 0, 4) };
        Avalonia.Automation.AutomationProperties.SetName(bar, T("Загружаю долги…", "Карыздар жүктөлүүдө…", "Loading debts…", "Borçlar yükleniyor…", "Qarzlar yuklanmoqda…"));
        _list.Children.Add(bar);
        for (var i = 0; i < 4; i++)
        {
            var skeleton = new Border { CornerRadius = new CornerRadius(12), Height = 64, Opacity = 0.55, IsHitTestVisible = false };
            Use(skeleton, Border.BackgroundProperty, "BrushBorder");
            _list.Children.Add(skeleton);
        }
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-З): сбой загрузки — понятное сообщение и «Повторить» (раньше только строка итога).</summary>
    private void ShowLoadError(string reason)
    {
        if (_debtors.Count > 0)
            return;
        _list.Children.Clear();
        var box = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        var text = new TextBlock
        {
            Text = T("Не удалось загрузить долги. Проверьте интернет и нажмите «Повторить».", "Карыздарды жүктөө мүмкүн болгон жок. Интернетти текшерип, «Кайталоо» баскычын басыңыз.",
                "Couldn't load the debts. Check the internet connection and press “Retry”.", "Borçlar yüklenemedi. İnterneti kontrol edip «Yeniden dene»ye basın.",
                "Qarzlarni yuklab bo'lmadi. Internetni tekshirib, «Qayta urinish»ni bosing.") + (reason.Length > 0 ? "\n" + reason : ""),
            TextWrapping = TextWrapping.Wrap,
        };
        Use(text, TextBlock.ForegroundProperty, "BrushTextSoft");
        var retry = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Yeniden dene", "Qayta urinish"));
        retry.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        box.Children.Add(text);
        box.Children.Add(retry);
        _list.Children.Add(box);
    }

    private async Task LoadAsync()
    {
        var gen = ++_loadGen;
        var startVersion = PosDataEvents.DebtVersion;
        _seenDebtVersion = startVersion; // 2026-10-18: этот список — не старше этого изменения
        ShowLoading(); // 2026-10-22 (аудит 1.17.59, П-З): индикатор, пока список пуст
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            // 2026-10-18, десятый круг (п.4) / 2026-10-18: общий точный загрузчик (тот же — у «Сводки»).
            var (debtors, progressive, byClient) = await LoadExactDebtorsAsync(withPhones: true, cts.Token, first =>
            {
                // 2026-10-27, С-6: быстрый первый экран — только если раздел ещё пуст и загрузка не устарела.
                if (gen != _loadGen || _debtors.Count > 0)
                    return;
                _debtors = first;
                Render();
            }).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            // 2026-10-18, восьмой круг (п.5): карточки, перечитанные позже старта этой загрузки, свежее её. Список применяется
            // (он новее всех ответов, начатых до него), а клиенты, чей долг менялся ВО ВРЕМЯ загрузки, перечитываются сразу.
            var changedDuring = debtors.Select(d => d.ClientId).Concat(_clientAppliedVersion.Keys)
                .Where(id => id.Length > 0 && PosDataEvents.DebtVersionOf(id) > startVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _debtors = debtors;
            _fullAppliedVersion = startVersion;
            foreach (var k in _clientAppliedVersion.Where(kv => kv.Value <= startVersion).Select(kv => kv.Key).ToList())
                _clientAppliedVersion.Remove(k);
            PosLogger.Log($"Долги клиентов: {_debtors.Count} клиент(ов), {_debtors.Sum(d => d.Sales.Count)} чек(ов) в долг{(progressive ? ", точные остатки — постепенно" : "")}.", "INFO");
            Render();
            if (progressive)
                _ = RefineProgressivelyAsync(gen, startVersion, debtors, byClient);
            else
                RememberExact(startVersion);
            // «Неизвестно чьё» изменение во время загрузки — полная перезагрузка придёт сама (DebouncedDebtChanged).
            if (PosDataEvents.DebtVersionOf(null) <= startVersion)
                foreach (var id in changedDuring)
                {
                    try
                    {
                        await ReloadClientAsync(id).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"Долги клиентов: карточка {id} после загрузки не перечитана ({ex.Message}).", "WARNING");
                    }
                }
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            PosLogger.Log($"Долги клиентов: не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetSummary(T("Не удалось загрузить долги: ", "Карыздарды жүктөө мүмкүн болгон жок: ", "Couldn't load debts: ", "Borçlar yüklenemedi: ", "Qarzlarni yuklab bo'lmadi: ")
                       + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : ex.Message));
            ShowLoadError(ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : "");
        }
    }

    /// <summary>2026-10-18, десятый круг (п.4): точные остатки после первой отрисовки — по клиенту (от крупных долгов), не больше
    /// 4 запросов одновременно; отрисовка — каждые 5 клиентов и в конце. Новая полная загрузка — прекращаем; карточка клиента,
    /// перечитанная позже старта этой загрузки, — свежее, её не трогаем.</summary>
    private async Task RefineProgressivelyAsync(int gen, long startVersion, List<Debtor> start, Dictionary<string, IReadOnlyList<JsonElement>> byClient)
    {
        var done = 0;
        foreach (var d in start.Where(d => d.ClientId.Length > 0).OrderByDescending(d => d.Total).ToList())
        {
            if (gen != _loadGen)
                return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var exact = await ExactRowsAsync(d.ClientId, byClient.TryGetValue(d.ClientId, out var own) ? own : Array.Empty<JsonElement>(), cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            if (_clientAppliedVersion.TryGetValue(d.ClientId, out var applied) && applied > startVersion)
                continue;
            var fresh = ApplyExact(d, exact);
            _debtors = _debtors.Where(x => !string.Equals(x.ClientId, d.ClientId, StringComparison.OrdinalIgnoreCase))
                .Concat(fresh is null ? Array.Empty<Debtor>() : new[] { fresh }).ToList();
            if (++done % 5 == 0)
                Render();
        }
        if (gen == _loadGen)
        {
            Render();
            RememberExact(startVersion);
        }
    }

    /// <summary>2026-10-18, живая проверка: последний точный список раздела — «Сводка» берёт его, если он свежий (≤ 2 мин) и с тех
    /// пор долги не менялись (та же версия), иначе грузит сама тем же загрузчиком. Цифры двух мест не расходятся.</summary>
    internal static (DateTime AtUtc, long Version, List<Debtor> Debtors)? LastExact { get; private set; }

    private void RememberExact(long startVersion)
    {
        if (PosDataEvents.DebtVersion != startVersion)
            return;
        LastExact = (DateTime.UtcNow, startVersion, _debtors.ToList());
        // 2026-10-19, вторая живая проверка: «Сводка» пересчитывает карточку из этого кэша сразу, без запросов к серверу.
        try
        {
            ExactChanged?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долги клиентов: «Сводка» не обновлена из кэша ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Раздел обновил точный кэш (LastExact) — подписчики (карточка «Сводки») пересчитывают из него. С UI-потока.</summary>
    internal static event Action? ExactChanged;

    /// <summary>Свежий точный список раздела (≤ 2 мин, долги с тех пор не менялись) или null.</summary>
    internal static List<Debtor>? FreshExact() =>
        LastExact is { } c && DateTime.UtcNow - c.AtUtc <= TimeSpan.FromMinutes(2) && c.Version == PosDataEvents.DebtVersion ? c.Debtors : null;

    // ── 2026-10-20, живой аудит владельца: один источник долгов для «Сводки», «Клиентов» и «Долгов клиентов» ──
    // Было: «Долги» 225 858 (точно, по сделкам), «Сводка» ≈ 245 858 и «Клиенты» ≈ 245 857 — быстрый список по продажам не вычитал
    // оплаты через сделку (клиент «Проверочный» 137 811 вместо 117 811). Теперь программа владельца при запуске в фоне считает
    // ТОТ ЖЕ точный расчёт, что раздел «Долги клиентов» (LoadExactDebtorsAsync + уточнение по сделкам, не больше 4 запросов
    // одновременно), и кладёт его в общий кэш LastExact; «Сводка» и «Клиенты» показывают его. Пока не посчитан — «≈» и «уточняю…».
    // Повтор — после изменения долга (оплата, новая продажа в долг) и не чаще раза в 5 минут; запросов из «Клиентов» по каждому
    // клиенту нет. Сам DebtPaymentRunner и окна оплаты не тронуты.

    private static Task? _sharedExactTask;

    /// <summary>Фоновый точный расчёт идёт.</summary>
    internal static bool SharedExactLoading => _sharedExactTask is { IsCompleted: false };

    /// <summary>Последний точный список, если долги с тех пор не менялись (версия та же) и ему не больше 10 минут; иначе null.</summary>
    internal static List<Debtor>? KnownExact() =>
        LastExact is { } c && DateTime.UtcNow - c.AtUtc <= TimeSpan.FromMinutes(10) && c.Version == PosDataEvents.DebtVersion ? c.Debtors : null;

    /// <summary>Запустить фоновый точный расчёт (если уже идёт — вернуть его). force=false — только если точного списка нет,
    /// долги менялись или ему больше 5 минут. Ошибки — в журнал; по готовности — ExactChanged (с UI-потока).</summary>
    internal static Task RefreshSharedExactAsync(bool force = false)
    {
        if (_sharedExactTask is { IsCompleted: false } running)
            return running;
        if (!force && LastExact is { } c && c.Version == PosDataEvents.DebtVersion && DateTime.UtcNow - c.AtUtc <= TimeSpan.FromMinutes(5))
            return Task.CompletedTask;
        return _sharedExactTask = RunSharedExactAsync();
    }

    private static async Task RunSharedExactAsync()
    {
        // Долги менялись во время расчёта — считаем ещё раз (не больше трёх раз подряд).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var startVersion = PosDataEvents.DebtVersion;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var (debtors, progressive, byClient) = await LoadExactDebtorsAsync(withPhones: false, cts.Token).ConfigureAwait(true);
                if (progressive)
                {
                    // Больше 60 чеков — раздел уточняет постепенно; здесь — то же уточнение по клиенту, до конца (в фоне).
                    var refined = new List<Debtor>();
                    foreach (var d in debtors)
                    {
                        if (d.ClientId.Length == 0)
                        {
                            refined.Add(d);
                            continue;
                        }
                        var rows = await ExactRowsAsync(d.ClientId, byClient.TryGetValue(d.ClientId, out var own) ? own : Array.Empty<JsonElement>(), cts.Token).ConfigureAwait(true);
                        if (ApplyExact(d, rows) is { } fresh)
                            refined.Add(fresh);
                    }
                    debtors = refined;
                }
                if (PosDataEvents.DebtVersion != startVersion)
                    continue;
                LastExact = (DateTime.UtcNow, startVersion, debtors);
                PosLogger.Log($"Долги клиентов (фоновый точный расчёт): {debtors.Count} клиент(ов), {debtors.Sum(d => d.Sales.Count)} чек(ов), {debtors.Sum(d => d.Total):N2} сом.", "INFO");
                void Notify()
                {
                    try
                    {
                        ExactChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"Долги клиентов: окна не обновлены из точного расчёта ({ex.Message}).", "WARNING");
                    }
                }
                if (Dispatcher.UIThread.CheckAccess())
                    Notify();
                else
                    Dispatcher.UIThread.Post(Notify);
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги клиентов: фоновый точный расчёт не удался ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                return;
            }
        }
    }

    /// <summary>Подпись к неточной сумме: «уточняю…», пока идёт фоновый расчёт, иначе — где смотреть точно.</summary>
    internal static string ApproxNote() => SharedExactLoading
        ? T("уточняю…", "такталууда…", "checking…", "kontrol ediliyor…", "aniqlanmoqda…")
        : T("точная сумма — в разделе «Долги клиентов»", "так сумма — «Кардарлардын карыздары» бөлүмүндө", "the exact amount is in “Customer debts”",
            "kesin tutar «Müşteri borçları» bölümünde", "aniq summa — «Mijozlar qarzlari» bo'limida");

    /// <summary>2026-10-18, ревью после живой проверки (п.1): быстрый список для «Сводки» (один запрос) — суммы по продажам,
    /// все чеки помечены Checking («не точно»); точные — только из свежего кэша раздела.</summary>
    internal static async Task<List<Debtor>> LoadFastDebtorsAsync(CancellationToken ct) =>
        (await LoadDebtorsAsync(withPhones: false, ct).ConfigureAwait(true)).Select(d => ApplyExact(d, null)!).ToList();

    /// <summary>Быстрая карточка одного клиента (один запрос его продаж) — «Сводка» патчит только его по событию долга.</summary>
    internal static async Task<Debtor?> LoadFastDebtorAsync(string clientId, CancellationToken ct)
    {
        var sales = await App.GetRequiredService<ISalesApiService>().PosDebtSalesAsync(clientId, ct).ConfigureAwait(true);
        var d = BuildDebtors(sales, new Dictionary<string, string>()).FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        // 2026-10-22 (аудит 1.17.59, П-Е): после «Записать долг без продажи» долг клиента виден сразу — карточка по одному клиенту
        // считается точно (чеки + долг без продажи тем же загрузчиком, что у окон оплаты); не получилось — как раньше (≈ по продажам).
        var exact = await ExactRowsAsync(clientId, sales, ct).ConfigureAwait(true);
        if (exact is null)
            return d is null ? null : ApplyExact(d, null);
        d ??= new Debtor(clientId, "", "", new List<DebtSale>());
        return ApplyExact(d, exact);
    }

    /// <summary>2026-10-18: общий точный загрузчик для раздела «Долги клиентов». До 60 чеков — точные
    /// остатки сразу; больше — Progressive: все «уточняется» (раздел догружает точные постепенно, «Сводка» показывает «≈»).</summary>
    internal static async Task<(List<Debtor> Debtors, bool Progressive, Dictionary<string, IReadOnlyList<JsonElement>> ByClient)> LoadExactDebtorsAsync(
        bool withPhones, CancellationToken ct, Action<List<Debtor>>? onFirstPaint = null)
    {
        var (rawSales, debtors) = await LoadSalesAndDebtorsAsync(withPhones, ct).ConfigureAwait(true);
        // 2026-10-27, аудит владельца С-6 («Долги клиентов» до 9 с): список по продажам готов после ОДНОГО запроса, а точные остатки
        // (запрос на каждого клиента) и поиск «долга без продажи» (до 60 запросов) занимали остальное время при пустом экране.
        // Раздел показывает этот список сразу (чеки с пометкой «уточняется»), точные суммы подставляются, как только придут.
        if (onFirstPaint is not null && debtors.Count > 0)
        {
            try
            {
                onFirstPaint(debtors.Select(d => ApplyExact(d, null)!).ToList());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги клиентов: первый показ списка не удался ({ex.Message}).", "WARNING");
            }
        }
        var byClient = rawSales.GroupBy(s => Str(s, "client") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<JsonElement>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        // 2026-10-22 (аудит 1.17.59, П-Е): клиенты только с «Долгом без продажи» — в общий расчёт («Сводка», «Клиенты», «Долги клиентов», Советник).
        // 2026-10-23 (аудит, круг 7: «Долги клиентов» 4–5 с): поиск таких клиентов не ждёт уточнения остатков — идёт рядом с ним
        // (число запросов то же; у обоих свой лимит 4 одновременных).
        var noSaleTask = NoSaleOnlyDebtorsAsync(debtors, ct);
        if (debtors.Sum(d => d.Sales.Count) > ExactBeforeRenderLimit)
            return (debtors.Select(d => ApplyExact(d, null)!).Concat(await noSaleTask.ConfigureAwait(true)).ToList(), true, byClient);
        using var exactCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var refined = await Task.WhenAll(debtors.Select(async d =>
            ApplyExact(d, await ExactRowsAsync(d.ClientId, byClient.TryGetValue(d.ClientId, out var own) ? own : Array.Empty<JsonElement>(), exactCts.Token).ConfigureAwait(true))))
            .ConfigureAwait(true);
        return (refined.Where(d => d is not null).Select(d => d!).Concat(await noSaleTask.ConfigureAwait(true)).ToList(), false, byClient);
    }

    private void Render()
    {
        _list.Children.Clear();
        _aging.Children.Clear();
        var all = _debtors;
        // 2026-10-18, десятый круг (п.4): «Вам должны» — только по точным остаткам; «уточняется» — отдельной пометкой.
        var total = all.SelectMany(d => d.Sales).Where(s => !s.Checking).Sum(s => s.Left);
        var checkingCount = all.SelectMany(d => d.Sales).Count(s => s.Checking);
        var checks = all.Sum(d => d.Sales.Count);
        if (all.Count == 0)
        {
            SetSummary(T("Долгов нет — все клиенты рассчитались.", "Карыз жок — бардык кардарлар эсептешти.", "No debts — every customer has paid.",
                "Borç yok — tüm müşteriler ödedi.", "Qarz yo'q — barcha mijozlar to'lagan."));
            // 2026-10-18, доводка О4: пустой раздел — с пояснением, что здесь появится.
            var empty = new TextBlock
            {
                Text = T("Когда кассир продаст в долг (способ оплаты «В долг» с клиентом), клиент появится здесь — с суммой, датой и напоминанием в WhatsApp.",
                    "Кассир карызга сатканда (кардар менен «Карызга» төлөм ыкмасы), кардар ушул жерде көрүнөт — суммасы, күнү жана WhatsApp'тагы эскертүү менен.",
                    "When a cashier sells on credit (payment “On credit” with a customer), the customer appears here — with the amount, date and a WhatsApp reminder.",
                    "Kasiyer veresiye sattığında (müşteriyle «Veresiye» ödeme), müşteri burada görünür — tutar, tarih ve WhatsApp hatırlatmasıyla.",
                    "Kassir qarzga sotganda (mijoz bilan «Qarzga» to'lov usuli), mijoz shu yerda ko'rinadi — summa, sana va WhatsApp eslatmasi bilan."),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 56, 24, 0),
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(empty);
            return;
        }
        var oldest = all.Min(d => d.Oldest);
        // 2026-10-18, этап О4: та же строка, но красная в ней только сумма долга.
        SetSummary(T("Вам должны ", "Сизге карыз ", "You are owed ", "Size borçlu: ", "Sizga qarz: "),
            // 2026-10-18, ревью после живой проверки: главная цифра никогда не 0 из-за «уточняется» — тогда «≈ всего» и в скобках точная часть.
            checkingCount == 0 ? Money(total)
                : "≈ " + Money(all.Sum(d => d.Total)) + " (" + T("точно ", "так ", "exact ", "kesin ", "aniq ") + Money(total) + NurMarketKassa.AvaloniaHost.Services.DebtPaymentFlow.CheckingSuffix(checkingCount) + ")",
            T($" · клиентов: {all.Count} · чеков в долг: {checks} · самый старый долг с {oldest:dd.MM.yyyy} ({Days(oldest)})",
                $" · кардарлар: {all.Count} · карыз чектер: {checks} · эң эски карыз {oldest:dd.MM.yyyy} баштап ({Days(oldest)})",
                $" · customers: {all.Count} · receipts on credit: {checks} · oldest debt since {oldest:dd.MM.yyyy} ({Days(oldest)})",
                $" · müşteri: {all.Count} · veresiye fiş: {checks} · en eski borç {oldest:dd.MM.yyyy} ({Days(oldest)})",
                $" · mijozlar: {all.Count} · qarzga cheklar: {checks} · eng eski qarz {oldest:dd.MM.yyyy} dan ({Days(oldest)})"));

        // Давность: до 7 дней, 8–30, больше 30 — по остатку долга.
        var sales = all.SelectMany(d => d.Sales).ToList();
        AddAging(T("до 7 дней", "7 күнгө чейин", "up to 7 days", "7 güne kadar", "7 kungacha"), sales.Where(s => AgeDays(s.At) <= 7).Sum(s => s.Left), "BrushSuccess");
        AddAging(T("8–30 дней", "8–30 күн", "8–30 days", "8–30 gün", "8–30 kun"), sales.Where(s => AgeDays(s.At) is > 7 and <= 30).Sum(s => s.Left), "BrushWarning");
        AddAging(T("больше 30 дней", "30 күндөн ашык", "over 30 days", "30 günden fazla", "30 kundan ortiq"), sales.Where(s => AgeDays(s.At) > 30).Sum(s => s.Left), "BrushDanger", overdue: true);

        var query = (_search.Text ?? "").Trim();
        var digits = new string(query.Where(char.IsDigit).ToArray());
        IEnumerable<Debtor> shown = all.Where(d => query.Length == 0
            || SearchMatch.Matches(query, d.Name) // 2026-10-29: единый поиск
            || (digits.Length >= 3 && new string(d.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
        shown = _sort == "age" ? shown.OrderBy(d => d.Oldest) : shown.OrderByDescending(d => d.Total);
        foreach (var d in shown.Take(300))
            _list.Children.Add(BuildCard(d));
    }

    /// <summary>2026-10-18, этап О4 (аудит «Долги», правило 4): чип давности — с обычной рамкой и цветной точкой, текст цветом
    /// текста; красным — только сумма просрочки «больше 30 дней» (и только если она есть). Раньше рамка и текст всех трёх
    /// чипов были зелёным / оранжевым / красным.</summary>
    private void AddAging(string label, double amount, string brush, bool overdue = false)
    {
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1) };
        Use(pill, Border.BackgroundProperty, "BrushPanel");
        Use(pill, Border.BorderBrushProperty, "BrushBorder");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        Use(dot, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
        row.Children.Add(dot);
        var text = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Use(text, TextBlock.ForegroundProperty, "BrushText");
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(label + ": "));
        var sum = new Avalonia.Controls.Documents.Run(Money(amount));
        if (overdue && amount > 0.005)
            sum.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        text.Inlines.Add(sum);
        row.Children.Add(text);
        pill.Child = row;
        _aging.Children.Add(pill);
    }

    /// <summary>2026-10-18, этап О4: строка итога; вторая часть (сумма долга) — красным.</summary>
    private void SetSummary(string text, string? debt = null, string? tail = null)
    {
        // 2026-10-18, живая проверка: строка итога видна в UIA (диктор, автоматизация).
        Avalonia.Automation.AutomationProperties.SetName(_summary, text + (debt ?? "") + (tail ?? ""));
        var inlines = _summary.Inlines!;
        inlines.Clear();
        inlines.Add(new Avalonia.Controls.Documents.Run(text));
        if (debt is null)
            return;
        var sum = new Avalonia.Controls.Documents.Run(debt) { FontWeight = FontWeight.Bold };
        sum.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        inlines.Add(sum);
        if (tail is not null)
            inlines.Add(new Avalonia.Controls.Documents.Run(tail));
    }

    private Control BuildCard(Debtor d)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        // 2026-10-18, этап О4 (аудит «Долги»): рамка — обычная у всех; давность видна в строке «долг с … (N дн.)», сумма — красным.
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 2 };
        var name = new TextBlock { Text = d.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        who.Children.Add(name);
        var meta = new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                d.Phone,
                // 2026-10-24 (приёмка, ПР-1b): считаются те же строки, что «чеков в долг» окна «Принять оплату» (чеки с остатком и сделки без продажи)
                T($"чеков: {d.Sales.Count}", $"чектер: {d.Sales.Count}", $"receipts: {d.Sales.Count}", $"fiş: {d.Sales.Count}", $"cheklar: {d.Sales.Count}"),
                T($"долг с {d.Oldest:dd.MM.yyyy} ({Days(d.Oldest)})", $"карыз {d.Oldest:dd.MM.yyyy} баштап ({Days(d.Oldest)})", $"owing since {d.Oldest:dd.MM.yyyy} ({Days(d.Oldest)})",
                    $"{d.Oldest:dd.MM.yyyy} tarihinden beri ({Days(d.Oldest)})", $"{d.Oldest:dd.MM.yyyy} dan beri ({Days(d.Oldest)})"),
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
        who.Children.Add(meta);
        head.Children.Add(who);
        var expanded = _expanded.Contains(d.ClientId + d.Name);
        // 2026-10-18, доводка О4: «Чеки в долг (N)» — маленькой кнопкой-обводкой под строкой клиента (не отдельной строкой карточки);
        // нажатие на любое место карточки тоже раскрывает чеки, как сказано в подсказке сверху.
        var more = new Button
        {
            Content = expanded ? T("▴ Скрыть чеки", "▴ Чектерди жашыруу", "▴ Hide receipts", "▴ Fişleri gizle", "▴ Cheklarni yashirish")
                : T($"▾ Чеки в долг ({d.Sales.Count})", $"▾ Карыз чектер ({d.Sales.Count})", $"▾ Receipts on credit ({d.Sales.Count})",
                    $"▾ Veresiye fişler ({d.Sales.Count})", $"▾ Qarzga cheklar ({d.Sales.Count})"),
            Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            Focusable = false,
        };
        more.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(more, more.Content as string);
        more.Click += (_, e) =>
        {
            e.Handled = true;
            var key = d.ClientId + d.Name;
            if (!_expanded.Remove(key))
                _expanded.Add(key);
            Render();
        };
        who.Children.Add(more);
        var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var sum = new TextBlock
        {
            // 2026-10-18, девятый круг: по части чеков точный остаток не получен — «уточняется».
            Text = Money(d.Total) + (d.Checking ? " · " + T("уточняется", "такталууда", "being checked", "kontrol ediliyor", "aniqlanmoqda") : ""),
            FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right,
        };
        // 2026-10-21 (пакет 4, п. 93; аудит: «много красного»): сумма карточки — цветом текста; красная — только у просроченного
        // долга (старше 30 дней, как красный чип «больше 30 дней» сверху). Расчёт суммы не менялся.
        Use(sum, TextBlock.ForegroundProperty, AgeDays(d.Oldest) > 30 ? "BrushDanger" : "BrushText");
        right.Children.Add(sum);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (ClientQrCode.NationalDigits(d.Phone) is { } national)
        {
            var wa = UiKit.Ghost(this, T("Напомнить в WhatsApp", "WhatsApp'та эскертүү", "Remind on WhatsApp", "WhatsApp'tan hatırlat", "WhatsApp'da eslatish"));
            wa.Height = 32;
            wa.MinHeight = 32; // 2026-10-18, доводка О4: у sec-outline MinHeight 40 — иначе высота 32 не действует
            wa.FontSize = 12.5;
            wa.Click += (_, e) =>
            {
                e.Handled = true;
                var text = T($"Здравствуйте, {d.Name}! Напоминаем о долге в нашем магазине: {Money(d.Total)}. Спасибо!",
                    $"Саламатсызбы, {d.Name}! Дүкөнүбүздөгү карызыңызды эскертебиз: {Money(d.Total)}. Рахмат!",
                    $"Hello, {d.Name}! A reminder about your debt at our shop: {Money(d.Total)}. Thank you!",
                    $"Merhaba {d.Name}! Mağazamızdaki borcunuzu hatırlatırız: {Money(d.Total)}. Teşekkürler!",
                    $"Assalomu alaykum, {d.Name}! Do'konimizdagi qarzingizni eslatamiz: {Money(d.Total)}. Rahmat!");
                SiteOrdersWindow.OpenUrl($"https://wa.me/996{national}?text={Uri.EscapeDataString(text)}");
            };
            buttons.Children.Add(wa);
        }
        else
        {
            // 2026-10-18, этап О4 (аудит «Долги»): было непонятно, почему «Напомнить в WhatsApp» есть не у всех.
            // Доводка О4: вместо выключенной кнопки — маленькая серая причина справа (подробно — в подсказке).
            var why = new TextBlock
            {
                Text = NoPhoneShort(d), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
                MaxWidth = 280, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0),
            };
            Use(why, TextBlock.ForegroundProperty, "BrushTextSoft");
            ToolTip.SetTip(why, NoWhatsAppReason(d));
            Avalonia.Automation.AutomationProperties.SetName(why, NoWhatsAppReason(d));
            buttons.Children.Add(why);
        }
        // 2026-10-18, владелец: «Принять оплату» — главная (жёлтая) кнопка карточки, только в программе владельца и только
        // у долга с клиентом (без клиента сервер не знает, чей долг гасить). WhatsApp — обводкой рядом.
        if (AppMode.IsOwner && d.ClientId.Length > 0)
        {
            // 2026-10-21: аудит предлагал обводку («жёлтая в каждой карточке»), но владелец 18.10 прямо просил жёлтую —
            // оставлена жёлтая (решение владельца важнее правила «одна жёлтая на экран»).
            var pay = UiKit.Primary(this, T("Принять оплату", "Төлөм кабыл алуу", "Accept payment", "Ödeme al", "To'lov qabul qilish"));
            pay.Height = 32;
            pay.MinHeight = 32;
            pay.FontSize = 12.5;
            pay.FontWeight = FontWeight.Bold;
            pay.Padding = new Thickness(14, 0);
            pay.Margin = new Thickness(6, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(pay, (pay.Content as string) + " — " + d.Name);
            pay.Click += async (_, e) =>
            {
                e.Handled = true;
                var dialog = new AcceptDebtPaymentWindow(d.ClientId, d.Name, d.Phone);
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    await dialog.ShowDialog(owner).ConfigureAwait(true);
                    // 2026-10-18, восьмой круг (п.10): оплата принята — карточка клиента сразу, без паузы общей рассылки.
                    if (dialog.Paid)
                    {
                        try
                        {
                            await ReloadClientAsync(d.ClientId).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Долги клиентов: карточка {d.ClientId} после оплаты не перечитана ({ex.Message}).", "WARNING");
                        }
                    }
                }
                else
                    dialog.Show();
            };
            buttons.Children.Add(pay);
        }
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        body.Children.Add(head);

        if (expanded)
            body.Children.Add(BuildSalesTable(d));
        card.Child = body;
        card.PointerPressed += (_, e) =>
        {
            if (e.Handled)
                return;
            var key = d.ClientId + d.Name;
            if (!_expanded.Remove(key))
                _expanded.Add(key);
            Render();
        };
        return card;
    }

    private Control BuildSalesTable(Debtor d)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var row = 0;
        void Cell(string text, int col, bool head = false, string brush = "BrushText", bool right = false, bool wrap = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = head ? 11.5 : 13, FontWeight = head ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = new Thickness(col == 0 ? 0 : 14, 3, 0, 3), TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, // 2026-10-23 (В-3): пометка «похоже на двойную запись» длинная — переносится
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Use(t, TextBlock.ForegroundProperty, head ? "BrushTextSoft" : brush);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(T("Дата", "Күнү", "Date", "Tarih", "Sana"), 0, head: true);
        Cell(T("Чек", "Чек", "Receipt", "Fiş", "Chek"), 1, head: true);
        Cell(T("Товар · кассир", "Товар · кассир", "Item · cashier", "Ürün · kasiyer", "Mahsulot · kassir"), 2, head: true);
        Cell(T("Сумма чека", "Чектин суммасы", "Receipt total", "Fiş tutarı", "Chek summasi"), 3, head: true, right: true);
        Cell(T("Внесено", "Төлөндү", "Paid", "Ödenen", "To'langan"), 4, head: true, right: true);
        Cell(T("Остаток", "Калдык", "Left", "Kalan", "Qoldiq"), 5, head: true, right: true);
        foreach (var s in d.Sales.OrderBy(s => s.At))
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell($"{s.At:dd.MM.yyyy HH:mm}", 0);
            Cell(s.Number.Length > 0 ? "№" + s.Number : "—", 1);
            // 2026-10-23 (аудит, круг 8, В-3): «похоже на двойную запись» — пометка жёлтым.
            // 2026-10-24 (приёмка, ПР-1): сумма в долг входит (как на сервере) — остаток показываем, пометка остаётся жёлтой.
            if (s.Duplicate)
                Cell(s.Item, 2, brush: "BrushWarning", wrap: true);
            else
                Cell(string.Join(" · ", new[] { s.Item, s.Cashier }.Where(x => !string.IsNullOrWhiteSpace(x))), 2, brush: "BrushTextSoft");
            Cell(Money(s.Total), 3, right: true);
            Cell(Money(s.Paid), 4, brush: "BrushTextSoft", right: true);
            // 2026-10-21 (пакет 4, п. 93): остаток чека — цветом текста, красный — только у чека старше 30 дней.
            Cell(Money(s.Left) + (s.Checking ? " · " + T("уточняется", "такталууда", "being checked", "kontrol ediliyor", "aniqlanmoqda") : ""), 5,
                brush: s.Checking ? "BrushWarning" : AgeDays(s.At) > 30 ? "BrushDanger" : "BrushText", right: true);
        }
        return grid;
    }

    /// <summary>2026-10-18, этап О4: коротко в строке клиента, почему нет напоминания.</summary>
    private static string NoPhoneShort(Debtor d) => d.ClientId.Length == 0
        ? T("Чек без клиента — напомнить некому", "Кардарсыз чек — эскертүүчү жок", "Receipt without a customer", "Müşterisiz fiş", "Mijozsiz chek")
        : string.IsNullOrWhiteSpace(d.Phone)
            ? T("Нет телефона — WhatsApp недоступен", "Телефону жок — WhatsApp жеткиликсиз", "No phone — WhatsApp unavailable", "Telefon yok — WhatsApp kullanılamaz", "Telefon yo'q — WhatsApp mavjud emas")
            : T("Номер не для WhatsApp", "WhatsApp үчүн номер эмес", "Not a WhatsApp number", "WhatsApp numarası değil", "WhatsApp raqami emas");

    /// <summary>2026-10-18, этап О4: подсказка у выключенной кнопки «Напомнить в WhatsApp».</summary>
    private static string NoWhatsAppReason(Debtor d) => d.ClientId.Length == 0
        ? T("Долг записан без клиента — напомнить некому.", "Карыз кардарсыз жазылган — эскертүүчү эч ким жок.", "The debt was recorded without a customer — there is no one to remind.",
            "Borç müşterisiz kaydedilmiş — hatırlatılacak kimse yok.", "Qarz mijozsiz yozilgan — eslatadigan hech kim yo'q.")
        : string.IsNullOrWhiteSpace(d.Phone)
            ? T("У клиента нет телефона. Добавьте номер в разделе «Клиенты» — появится напоминание в WhatsApp.", "Кардардын телефону жок. «Кардарлар» бөлүмүнө номер кошуңуз — WhatsApp'та эскертүү пайда болот.",
                "The customer has no phone. Add a number in “Customers” and the WhatsApp reminder will appear.", "Müşterinin telefonu yok. «Müşteriler» bölümünde numara ekleyin — WhatsApp hatırlatması açılır.",
                "Mijozning telefoni yo'q. «Mijozlar» bo'limida raqam qo'shing — WhatsApp eslatmasi paydo bo'ladi.")
            : T("Номер клиента не похож на номер Кыргызстана (+996) — WhatsApp не откроется. Исправьте его в разделе «Клиенты».", "Кардардын номери Кыргызстандын номерине (+996) окшобойт — WhatsApp ачылбайт. Аны «Кардарлар» бөлүмүндө оңдоңуз.",
                "The customer's number doesn't look like a Kyrgyzstan number (+996) — WhatsApp won't open. Fix it in “Customers”.", "Müşterinin numarası Kırgızistan numarasına (+996) benzemiyor — WhatsApp açılmaz. «Müşteriler» bölümünde düzeltin.",
                "Mijoz raqami Qirg'iziston raqamiga (+996) o'xshamaydi — WhatsApp ochilmaydi. Uni «Mijozlar» bo'limida tuzating.");

    internal static int AgeDays(DateTime at) => Math.Max(0, (int)(DateTime.Today - at.Date).TotalDays);

    internal static string Days(DateTime at)
    {
        var n = AgeDays(at);
        return n == 0
            ? T("сегодня", "бүгүн", "today", "bugün", "bugun")
            : T($"{n} дн.", $"{n} күн", n == 1 ? "1 day" : $"{n} days", $"{n} gün", $"{n} kun");
    }

    internal static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString() : null;

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
