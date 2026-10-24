using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, ТЗ ч.12, п. 3.2 (сервер выложил 05.10): «Записать долг без продажи» в карточке клиента —
/// POST api/main/clients/{id}/debts/ {amount, comment, due_date}. Для долга, который был до кассы (тетрадь, другая
/// программа), без фиктивной продажи. Раньше такой долг можно было записать только продажей «в долг».</summary>
public partial class ClientsWindow
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void InitDebtButton()
    {
        if (AddDebtButton == null)
            return;
        AddDebtButton.Content = T("＋ Записать долг без продажи", "＋ Сатуусуз карыз жазуу", "＋ Record a debt without a sale", "＋ Satışsız borç kaydet", "＋ Sotuvsiz qarz yozish");
        AddDebtButton.IsVisible = TariffGate.CanUseDebts;
        // 2026-10-19, этап О8: блок «Долг» карточки — только при тарифе с долгами; «Принять оплату» — существующее окно владельца.
        CardDebtBox.IsVisible = TariffGate.CanUseDebts;
        CardDebtLabel.Text = T("Долг", "Карыз", "Debt", "Borç", "Qarz");
        PayDebtButton.Content = T("Принять оплату", "Төлөм кабыл алуу", "Accept payment", "Ödeme al", "To'lov qabul qilish");
        foreach (var b in new[] { AddDebtButton, PayDebtButton })
            Avalonia.Automation.AutomationProperties.SetName(b, b.Content as string ?? "");
        HookDebtListEvents();

        // 2026-10-18: долг клиента оплатили (в этой кассе, в другой или у владельца — LAN «debt_paid») — покупки открытого
        // клиента перечитываются: статус «в долг» у его чеков мог смениться.
        void OnDebtChanged(string? clientId, string? operationKey) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SelectedClient is { } client && (clientId is null || string.Equals(client.Id, clientId, StringComparison.OrdinalIgnoreCase)))
                _ = LoadClientPurchasesAsync(client);
        });
        PosDataEvents.ClientDebtChanged += OnDebtChanged;
        Closed += (_, _) => PosDataEvents.ClientDebtChanged -= OnDebtChanged;
    }

    private async void AddDebtButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client || string.IsNullOrWhiteSpace(client.Id))
            return;
        try
        {
            // 2026-10-19, этап О8: долг записан — сумма в списке и в карточке перечитывается (один запрос по клиенту).
            if (await AskDebtAsync(client).ConfigureAwait(true) != null)
                await RefreshClientDebtAsync(client.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долг без продажи: окно не открылось: {ex}", "WARNING");
        }
    }

    // ── 2026-10-19, этап О8: долги в списке клиентов ──
    // Источник — тот же, что у карточки «Долги клиентов» в «Сводке» (OwnerShellWindow.Debts.cs): свежий точный список раздела
    // «Долги клиентов» (DebtsView.FreshExact, ≤ 2 мин), иначе быстрый список — ОДИН запрос продаж в долг (DebtsView.LoadFastDebtorsAsync).
    // Запроса на каждого клиента нет. Быстрый список не уточнён по сделкам — итог с «≈» и пояснением, как в «Сводке».

    private Dictionary<string, DebtsView.Debtor> _debtsByClient = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Долги получены (хотя бы раз) / последняя загрузка не удалась / суммы не уточнены по сделкам.</summary>
    private bool _debtsLoaded;
    private bool _debtsFailed;
    private bool _debtsApprox;
    /// <summary>Долг чеков без клиента (в список клиентов не попадает, но входит в «Вам должны»).</summary>
    private double _debtNoClient;
    private int _debtsGen;
    private bool _debtsLoading;

    private async Task LoadDebtsAsync()
    {
        if (!TariffGate.CanUseDebts)
            return;
        var gen = ++_debtsGen;
        _debtsLoading = true;
        UpdateKpis();
        // 2026-10-20, живой аудит владельца («Вам должны ≈ 245 857» при 225 858 в «Долгах»): в программе владельца — общий фоновый
        // точный расчёт (тот же, что раздел «Долги клиентов»); готов — OnExact ниже. Запроса на каждого клиента отсюда нет.
        if (AppMode.IsOwner)
            _ = DebtsView.RefreshSharedExactAsync();
        try
        {
            List<DebtsView.Debtor> list;
            bool approx;
            if (DebtsView.KnownExact() is { } exact)
            {
                list = exact.ToList();
                approx = list.Any(d => d.Checking);
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                list = await DebtsView.LoadFastDebtorsAsync(cts.Token).ConfigureAwait(true);
                approx = list.Count > 0;
            }
            if (gen != _debtsGen)
                return;
            // 2026-10-20: точный расчёт закончился, пока шёл быстрый список, — берём точный.
            if (approx && DebtsView.KnownExact() is { } exactNow)
            {
                list = exactNow.ToList();
                approx = list.Any(d => d.Checking);
            }
            SetDebts(list, approx);
        }
        catch (Exception ex)
        {
            if (gen != _debtsGen)
                return;
            _debtsFailed = true;
            PosLogger.Log($"Клиенты: долги не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            if (gen == _debtsGen)
                _debtsLoading = false;
        }
        RefreshAfterDebts();
    }

    private void SetDebts(List<DebtsView.Debtor> list, bool approx)
    {
        _debtsByClient = list.Where(d => d.ClientId.Length > 0)
            .GroupBy(d => d.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _debtNoClient = list.Where(d => d.ClientId.Length == 0).Sum(d => d.Total);
        _debtsApprox = approx;
        _debtsLoaded = true;
        _debtsFailed = false;
    }

    /// <summary>Перечитать долг одного клиента (после «Принять оплату» / «Записать долг») — один запрос его продаж в долг.</summary>
    private async Task RefreshClientDebtAsync(string clientId)
    {
        if (!TariffGate.CanUseDebts || string.IsNullOrWhiteSpace(clientId))
            return;
        // Общего списка долгов ещё нет (не загрузился) — грузим его целиком, иначе у остальных клиентов было бы «—» вместо «не знаем».
        if (!_debtsLoaded)
        {
            await LoadDebtsAsync().ConfigureAwait(true);
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var fresh = await DebtsView.LoadFastDebtorAsync(clientId, cts.Token).ConfigureAwait(true);
            if (fresh is null)
                _debtsByClient.Remove(clientId);
            else
            {
                _debtsByClient[clientId] = fresh;
                _debtsApprox = true;
            }
            _debtsLoaded = true;
            // 2026-10-20: долг клиента изменился — общий точный расчёт пересчитывается в фоне (OnExact заменит «≈» точной суммой).
            if (AppMode.IsOwner)
                _ = DebtsView.RefreshSharedExactAsync();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Клиенты: долг клиента {clientId} не перечитан ({ex.Message}).", "WARNING");
            return;
        }
        RefreshAfterDebts();
    }

    /// <summary>Долги получены — строки, плитки и карточка. Порядок или фильтр от долга не зависят — строки обновляются на месте
    /// (список не пересобирается и прокрутка не прыгает к началу); зависят — список пересобирается.</summary>
    private void RefreshAfterDebts()
    {
        ApplyDebtsToRows();
        if (_onlyDebtors || _sort == "debt" || FilteredClients.Count == 0)
            ApplyFilter();
        else
        {
            foreach (var row in _allClients)
                row.NotifyDebt();
            UpdateKpis();
            UpdateEmptyState();
        }
        UpdateCardDebt();
    }

    /// <summary>Сумма долга, число чеков и самый старый — в строки списка.</summary>
    private void ApplyDebtsToRows()
    {
        var known = TariffGate.CanUseDebts && _debtsLoaded;
        foreach (var row in _allClients)
        {
            row.DebtUnknown = !known;
            if (known && _debtsByClient.TryGetValue(row.Id, out var d))
            {
                row.Debt = d.Total;
                row.DebtSalesCount = d.Sales.Count(s => !s.Id.StartsWith("deal:", StringComparison.Ordinal)); // 2026-10-22 (аудит 1.17.59, П-Е): «чеков» — без долга без продажи
                row.DebtSince = d.Sales.Count > 0 ? d.Oldest : null;
            }
            else
            {
                row.Debt = 0;
                row.DebtSalesCount = 0;
                row.DebtSince = null;
            }
        }
    }

    /// <summary>Плитки «Должников» и «Вам должны» (красным — только если долг есть; не уточнено — «≈» и пояснение).</summary>
    private void UpdateDebtKpis(CultureInfo ru)
    {
        KpiDebtNote.IsVisible = false;
        KpiDebtValue.Classes.Set("danger", false);
        if (!_debtsLoaded)
        {
            KpiDebtorsValue.Text = _debtsFailed ? "—" : "…";
            KpiDebtValue.Text = _debtsFailed ? "—" : "…";
            if (_debtsFailed)
            {
                KpiDebtNote.Text = T("Долги не загрузились — нажмите «Обновить».", "Карыздар жүктөлгөн жок — «Жаңыртуу» басыңыз.", "Debts didn't load — press “Refresh”.",
                    "Borçlar yüklenemedi — «Yenile»ye basın.", "Qarzlar yuklanmadi — «Yangilash»ni bosing.");
                KpiDebtNote.IsVisible = true;
            }
            return;
        }
        // 2026-10-20, живой аудит владельца: «Должников 12», а в «Долгах» 13 — долг клиента, которого нет в списке клиентов
        // (удалён на сайте), в «Вам должны» входил, а в число должников — нет. Теперь число — по долгам (как в «Долгах клиентов»),
        // и подпись, сколько из них нет в списке.
        var withDebt = _debtsByClient.Where(kv => kv.Value.Total > 0.005).Select(kv => kv.Key).ToList();
        var missing = withDebt.Count(id => !_allClients.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)));
        var debtors = withDebt.Count + (_debtNoClient > 0.005 ? 1 : 0);
        var total = _debtsByClient.Values.Sum(d => d.Total) + _debtNoClient;
        KpiDebtorsValue.Text = debtors.ToString("N0", ru);
        KpiDebtValue.Text = (total > 0.005 && _debtsApprox ? "≈ " : "") + total.ToString("N2", ru) + " " + Som;
        KpiDebtValue.Classes.Set("danger", total > 0.005);
        var notes = new List<string>();
        if (total > 0.005 && _debtsApprox)
            notes.Add(DebtsView.ApproxNote()); // 2026-10-20: «уточняю…», пока идёт фоновый точный расчёт
        if (missing > 0)
            notes.Add(T($"нет в списке клиентов: {missing}", $"кардарлар тизмесинде жок: {missing}", $"not in the client list: {missing}",
                $"müşteri listesinde yok: {missing}", $"mijozlar ro'yxatida yo'q: {missing}"));
        if (_debtNoClient > 0.005)
            notes.Add(T($"без клиента {_debtNoClient.ToString("N2", ru)} сом", $"кардарсыз {_debtNoClient.ToString("N2", ru)} сом",
                $"no client {_debtNoClient.ToString("N2", ru)} som", $"müşterisiz {_debtNoClient.ToString("N2", ru)} som",
                $"mijozsiz {_debtNoClient.ToString("N2", ru)} so'm"));
        if (_debtsFailed)
            notes.Add(T("не обновились — показаны прежние", "жаңыланган жок — мурункулар көрсөтүлдү", "not refreshed — showing the previous ones",
                "yenilenmedi — öncekiler gösteriliyor", "yangilanmadi — avvalgilari ko'rsatilgan"));
        KpiDebtNote.Text = string.Join(" · ", notes);
        KpiDebtNote.IsVisible = notes.Count > 0;
    }

    /// <summary>Блок «Долг» карточки: сумма (красным, если есть), чеки и с какого числа; «Принять оплату» — только у владельца
    /// и только при долге (как в «Долгах клиентов»).</summary>
    private void UpdateCardDebt()
    {
        if (CardDebtBox == null || !TariffGate.CanUseDebts)
            return;
        var client = SelectedClient;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        PayDebtButton.IsVisible = false;
        if (client == null)
            return;
        if (client.DebtUnknown)
        {
            CardDebtValue.Text = _debtsFailed ? "—" : "…";
            CardDebtValue.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            CardDebtDetail.Text = _debtsFailed
                ? T("Долги не загрузились — нажмите «Обновить» в списке.", "Карыздар жүктөлгөн жок — тизмеде «Жаңыртуу» басыңыз.", "Debts didn't load — press “Refresh” in the list.",
                    "Borçlar yüklenemedi — listede «Yenile»ye basın.", "Qarzlar yuklanmadi — ro'yxatda «Yangilash»ni bosing.")
                : T("Загружаем долги…", "Карыздар жүктөлүүдө…", "Loading debts…", "Borçlar yükleniyor…", "Qarzlar yuklanmoqda…");
            return;
        }
        if (!client.HasDebt)
        {
            CardDebtValue.Text = T("нет", "жок", "none", "yok", "yo'q");
            CardDebtValue.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            CardDebtDetail.Text = T("Клиент ничего не должен.", "Клиенттин карызы жок.", "The client owes nothing.", "Müşterinin borcu yok.", "Mijozning qarzi yo'q.");
            return;
        }
        CardDebtValue.Text = client.Debt.ToString("N2", ru) + " " + Som;
        CardDebtValue.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        var n = client.DebtSalesCount;
        CardDebtDetail.Text = client.DebtSince is { } since
            ? T($"чеков в долг: {n} · с {since:dd.MM.yyyy} ({DebtsView.Days(since)})", $"карыз чектер: {n} · {since:dd.MM.yyyy} баштап ({DebtsView.Days(since)})",
                $"receipts on credit: {n} · since {since:dd.MM.yyyy} ({DebtsView.Days(since)})", $"veresiye fiş: {n} · {since:dd.MM.yyyy} tarihinden ({DebtsView.Days(since)})",
                $"qarzga cheklar: {n} · {since:dd.MM.yyyy} dan ({DebtsView.Days(since)})")
            : T($"чеков в долг: {n}", $"карыз чектер: {n}", $"receipts on credit: {n}", $"veresiye fiş: {n}", $"qarzga cheklar: {n}");
        // 2026-10-22 (аудит 1.17.59, П-Е): «Долг без продажи» — отдельной подписью (в «чеках» его нет).
        var noSale = _debtsByClient.TryGetValue(client.Id, out var debtor)
            ? debtor.Sales.Where(s => s.Id.StartsWith("deal:", StringComparison.Ordinal)).Sum(s => s.Left) : 0;
        if (noSale > 0.005)
        {
            var noSaleText = noSale.ToString("N2", ru) + " " + Som;
            var label = T($"долг без продажи: {noSaleText}", $"сатуусуз карыз: {noSaleText}", $"debt without a sale: {noSaleText}",
                $"satışsız borç: {noSaleText}", $"sotuvsiz qarz: {noSaleText}");
            CardDebtDetail.Text = n > 0 ? CardDebtDetail.Text + " · " + label : label;
        }
        PayDebtButton.IsVisible = AppMode.IsOwner;
    }

    /// <summary>«Принять оплату» — то же окно, что в «Долгах клиентов» (AcceptDebtPaymentWindow); деньги проводит оно.
    /// Оплата прошла — долг клиента и его покупки перечитываются.</summary>
    private async void PayDebtButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedClient is not { } client || string.IsNullOrWhiteSpace(client.Id))
            return;
        try
        {
            var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.AcceptDebtPaymentWindow(client.Id, client.FullName, client.Phone);
            await dialog.ShowDialog(this).ConfigureAwait(true);
            if (!dialog.Paid)
                return;
            await RefreshClientDebtAsync(client.Id).ConfigureAwait(true);
            if (ReferenceEquals(SelectedClient, client))
                _ = LoadClientPurchasesAsync(client);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Клиенты: окно оплаты долга не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>Долги менялись, пока раздел был скрыт.</summary>
    private bool _debtsStale;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _debtsStale)
        {
            _debtsStale = false;
            _ = LoadDebtsAsync();
        }
    }

    /// <summary>Долг изменился (оплата здесь, в кассе, у владельца — общая отложенная рассылка) — долги списка перечитываются
    /// одним запросом; раздел «Долги клиентов» обновил точный кэш — берём его без запросов.</summary>
    private void HookDebtListEvents()
    {
        if (!TariffGate.CanUseDebts)
            return;
        void OnBatch(DebtChangeBatch batch) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_clientsLoaded)
                return;
            // Раздел владельца скрыт — перечитаем, когда его снова покажут (без лишних запросов в фоне).
            if (!IsVisible)
            {
                _debtsStale = true;
                return;
            }
            _ = LoadDebtsAsync();
        });
        void OnExact() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_debtsLoading || DebtsView.FreshExact() is not { } exact)
                return;
            SetDebts(exact.ToList(), exact.Any(d => d.Checking));
            RefreshAfterDebts();
        });
        PosDataEvents.DebouncedDebtChanged += OnBatch;
        DebtsView.ExactChanged += OnExact;
        Closed += (_, _) =>
        {
            PosDataEvents.DebouncedDebtChanged -= OnBatch;
            DebtsView.ExactChanged -= OnExact;
        };
    }

    /// <summary>Окно «Долг без продажи». Возвращает сумму, если долг записан на сервере, иначе null.</summary>
    private async Task<string?> AskDebtAsync(ClientRow client)
    {
        var api = App.GetRequiredService<NurMarketApiClient>();
        var tcs = new TaskCompletionSource<string?>();
        // 2026-10-22 (модалки, пакет 2): общая оболочка модалки (SectionCards.AsModal 512 + ModalShell) вместо окна с рамкой Windows:
        // ✕ и Esc = «Отмена», Enter = «Записать долг», тело прокручивается (на 910×512 кнопки были за краем), фокус в сумме,
        // срок — выбором даты (было текстом «ГГГГ-ММ-ДД»), пока долг записывается — окно не закрывается.
        var dialog = new Window
        {
            Title = T("Долг без продажи", "Сатуусуз карыз", "Debt without a sale", "Satışsız borç", "Sotuvsiz qarz"),
        };
        SectionCards.AsModal(dialog, 512);
        var panel = new StackPanel { Spacing = 8 };
        var hint = new TextBlock
        {
            Text = T("Для долга, который был до кассы (тетрадь, другая программа). Продажа не создаётся, товар со склада не списывается. Оплачивают его как обычный долг.",
                "Кассага чейинки карыз үчүн (дептер, башка программа). Сатуу түзүлбөйт, товар кампадан чыгарылбайт. Кадимки карыз сыяктуу төлөнөт.",
                "For a debt from before the till (notebook, another program). No sale is created and no stock is written off. It is paid like any other debt.",
                "Kasadan önceki bir borç için (defter, başka program). Satış oluşturulmaz, stoktan düşülmez. Normal borç gibi ödenir.",
                "Kassagacha bo'lgan qarz uchun (daftar, boshqa dastur). Sotuv yaratilmaydi, ombordan chiqarilmaydi. Oddiy qarz kabi to'lanadi."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(hint);

        TextBox Field(string label, string watermark, string value)
        {
            panel.Children.Add(UiKit.Label(this, label));
            var box = SectionCards.Field(UiKit.Input(this, watermark, 44));
            box.Text = value;
            panel.Children.Add(box);
            return box;
        }

        var amount = Field(T("Сумма долга, сом *", "Карыздын суммасы, сом *", "Debt amount, som *", "Borç tutarı, som *", "Qarz summasi, so'm *"), "7000", "");
        var comment = Field(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"),
            T("Например: долг из тетради за сентябрь", "Мисалы: сентябрдагы дептерден карыз", "E.g. September notebook debt", "Örn.: eylül defterinden borç", "Masalan: sentyabr daftaridagi qarz"), "");
        comment.MaxLength = 500;
        panel.Children.Add(UiKit.Label(this, T("Вернуть до", "Кайтаруу мөөнөтү", "Due date", "Son ödeme", "Qaytarish muddati")));
        var due = SectionCards.Field(new CalendarDatePicker
        {
            SelectedDate = DateTime.Today.AddDays(30),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        panel.Children.Add(due);
        var error = SectionCards.ErrorText(this);
        panel.Children.Add(error);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => dialog.Close();
        var save = SectionCards.Primary(T("Записать долг", "Карызды жазуу", "Record the debt", "Borcu kaydet", "Qarzni yozish"), isDefault: true);
        var shell = SectionCards.Shell(dialog.Title + " — " + client.FullName, null, panel, SectionCards.Buttons(cancel, save));
        shell.InitialFocus = amount;
        amount.TextChanged += (_, _) => SectionCards.ShowError(error, null);
        save.Click += async (_, _) =>
        {
            void Fail(string text, Control? field = null) => SectionCards.ShowError(error, text, field);

            if (!decimal.TryParse((amount.Text ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var sum) || sum <= 0)
            {
                Fail(T("Введите сумму больше нуля.", "Нөлдөн чоң сумма жазыңыз.", "Enter an amount above zero.", "Sıfırdan büyük bir tutar girin.", "Noldan katta summa kiriting."), amount);
                return;
            }
            if (due.SelectedDate is not { } dueDate)
            {
                Fail(T("Выберите дату, до которой вернуть долг.", "Карызды кайтаруу датасын тандаңыз.", "Choose the date the debt is due.", "Borcun son ödeme tarihini seçin.", "Qarzni qaytarish sanasini tanlang."), due);
                return;
            }
            dueDate = dueDate.Date;
            // Повторное нажатие/Enter во время записи — ничего не делает; Esc и ✕ окно не закрывают.
            if (!shell.TryBeginBusy())
                return;
            var body = new Dictionary<string, object?>
            {
                ["amount"] = sum.ToString("0.00", CultureInfo.InvariantCulture),
                ["due_date"] = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            if ((comment.Text ?? "").Trim() is { Length: > 0 } note)
                body["comment"] = note;
            save.IsEnabled = false;
            SectionCards.ShowError(error, null);
            try
            {
                await api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Uri.EscapeDataString(client.Id)}/debts/", body, null, CancellationToken.None, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(true);
                var text = sum.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                PosLogger.Log($"Долг без продажи: клиент {client.Id}, {text} сом, до {dueDate:dd.MM.yyyy}.", "INFO");
                tcs.TrySetResult(text);
                dialog.Close();
            }
            catch (Exception ex)
            {
                Fail(T("Не записано: ", "Жазылган жок: ", "Not recorded: ", "Kaydedilmedi: ", "Yozilmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
                PosLogger.Log($"Долг без продажи: не записан ({ex.Message}).", "WARNING");
            }
            finally
            {
                shell.EndBusy();
                save.IsEnabled = true;
            }
        };
        dialog.Content = shell;
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        var result = await tcs.Task.ConfigureAwait(true);
        if (result != null)
            await InfoAsync(T($"Долг {result} сом записан клиенту «{client.FullName}» в NurCRM.",
                $"«{client.FullName}» кардарына {result} сом карыз NurCRMге жазылды.",
                $"A debt of {result} som is recorded in NurCRM for \"{client.FullName}\".",
                $"\"{client.FullName}\" için {result} som borç NurCRM'e kaydedildi.",
                $"«{client.FullName}» mijozga {result} so'm qarz NurCRMga yozildi.")).ConfigureAwait(true);
        return result;
    }

    /// <summary>2026-10-22 (модалки, пакет 2): сообщение в общей оболочке модалки (было окно с рамкой Windows и «OK» без перевода,
    /// ни Enter, ни Esc его не закрывали): «Понятно» — и Enter, и Esc, и ✕.</summary>
    private async Task InfoAsync(string text)
    {
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 512);
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var ok = SectionCards.Cancel(T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli"));
        ok.IsDefault = true;
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = SectionCards.Shell(T("Долг записан", "Карыз жазылды", "Debt recorded", "Borç kaydedildi", "Qarz yozildi"), null, label,
            SectionCards.Buttons(ok));
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }
}
