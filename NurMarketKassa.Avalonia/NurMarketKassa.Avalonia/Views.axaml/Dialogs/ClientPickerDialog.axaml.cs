using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
#if !NURANDROID
using Avalonia.VisualTree;
#endif
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Отдельное модальное окно выбора клиента (2026-09-05, по просьбе пользователя: список
/// клиентов в диалоге оплаты "не помещается") — поиск/список/форма нового клиента раньше были
/// встроены прямо в CheckoutDialog и обрезались на невысоких экранах. Делит один и тот же
/// CheckoutViewModel с диалогом оплаты — вся бизнес-логика (поиск, добавление клиента) не
/// дублируется, здесь просто другой UI поверх тех же свойств/команд.
/// </summary>
public partial class ClientPickerDialog : Window
{
    private readonly CheckoutViewModel _viewModel;

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public ClientPickerDialog() : this(new CheckoutViewModel(new CartTotalsCalculator.CartTotals(), "", ""))
    {
    }

    public ClientPickerDialog(CheckoutViewModel viewModel)
    {
        _viewModel = viewModel;
        // 2026-10-23: долг клиента в строке списка — из общего кэша «Долги клиентов» (если уже посчитан), без новых запросов.
        ClientOption.DebtLookup = DebtOf;
        DataContext = _viewModel;
        InitializeComponent();
        // 2026-10-24 (приёмка, ПР-8): короткая подсказка поиска — прежняя («Поиск клиента по имени или телефону…») обрезалась справа рядом с «Новый клиент».
        var searchHint = Tr.T("Имя или телефон…", "Аты же телефону…", "Name or phone…", "İsim veya telefon…", "Ism yoki telefon…");
        SearchBox.Watermark = searchHint;
        Avalonia.Automation.AutomationProperties.SetName(SearchBox, searchHint);
        UpdateDuplicateText();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        // 2026-10-21 (пакет 6, п. 26): пустой результат поиска — «не найдено» с подсказкой, а не пустая рамка.
        _viewModel.ClientSearchResults.CollectionChanged += OnClientResultsChanged;
        UpdateNotFound();
        _ = LoadDebtsAsync();
        // 2026-10-23 (ТЗ компактной оплаты, П-2): карточка без ModalShell — Esc, фокус при открытии и подгонку места делаем сами.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        Opened += (_, _) => Dispatcher.UIThread.Post(FocusInitial, DispatcherPriority.Loaded);
        UpdateListHeight();
#if !NURANDROID
        SizeChanged += (_, _) => Reclamp();
#endif
    }

    // 2026-10-23: долги клиентов для метки «Долг: N» — ОДИН лёгкий запрос (/clients/debtors/, как в «Оплате долга»), кэш 60 с;
    // сбрасывается, когда долги менялись (оплата долга, продажа в долг — PosDataEvents.DebtVersion). Сделки по клиентам не считаем.
    private static Dictionary<string, double> _debtMap = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _debtMapAtUtc = DateTime.MinValue;
    private static long _debtMapVersion = -1;
    private static Task? _debtLoading;

    private static bool DebtMapFresh() =>
        _debtMapVersion == PosDataEvents.DebtVersion && DateTime.UtcNow - _debtMapAtUtc <= TimeSpan.FromSeconds(60);

    private static double DebtOf(string clientId) =>
        _debtMapVersion == PosDataEvents.DebtVersion && _debtMap.TryGetValue(clientId, out var v) ? v : 0;

    private async Task LoadDebtsAsync()
    {
        try
        {
            if (!DebtMapFresh())
            {
                if (_debtLoading is not { IsCompleted: false })
                    _debtLoading = FetchDebtsAsync();
                await _debtLoading.ConfigureAwait(true);
            }
            foreach (var option in _viewModel.ClientSearchResults.ToList())
                option.RefreshDebt();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выбор клиента: долги для метки не получены ({ex.Message}).", "WARNING");
        }
    }

    private static async Task FetchDebtsAsync()
    {
        if (App.AppHost?.Services.GetService(typeof(NurMarketKassa.Services.Api.ClientDebtsApiService)) is not NurMarketKassa.Services.Api.ClientDebtsApiService api)
            return;
        var startVersion = PosDataEvents.DebtVersion;
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        var debtors = await api.GetDebtorsAsync(0.01, cts.Token).ConfigureAwait(true);
        _debtMap = debtors.Where(d => d.DebtTotal > 0.005 && !string.IsNullOrWhiteSpace(d.ClientId))
            .GroupBy(d => d.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.DebtTotal), StringComparer.OrdinalIgnoreCase);
        _debtMapAtUtc = DateTime.UtcNow;
        _debtMapVersion = startVersion;
    }

    // 2026-10-23 (ТЗ компактной оплаты, П-2): два режима одной карточки. «Список» — поиск, «Новый клиент», ✕ и 6 строк по 48 px.
    // «Новый клиент» — «‹ Назад», заголовок и поля формы; список остаётся виден (2 строки, 1 — когда показано предупреждение
    // о дубле телефона): кассир видит, что клиент уже есть, и может выбрать его (круг 7 аудита 23.10).
    private const double RowHeight = 48;
    private int _listRows = 6;
    private bool _formMode;

    private void SetFormMode(bool form)
    {
        _formMode = form;
        ListHeader.IsVisible = !form;
        FormHeader.IsVisible = form;
        NewClientForm.IsVisible = form;
        UpdateListHeight();
        UpdateListVisibility();
#if !NURANDROID
        Dispatcher.UIThread.Post(Reclamp, DispatcherPriority.Loaded);
#endif
    }

    private void UpdateListHeight()
    {
        // 2026-10-24 (приёмка, ПР-8): в режиме формы всегда 2 строки списка (с предупреждением о дубле было 1 — кассир видел одну из двух «Аудит Клиент»).
        var rows = _formMode ? 2 : _listRows;
        ClientListBorder.MaxHeight = rows * RowHeight + 2;
        ClientListBorder.MinHeight = _formMode ? 0 : 96;
    }

    /// <summary>В режиме формы пустой список (нет клиентов / идёт загрузка / «не найдено») не показываем — место нужно форме.</summary>
    private void UpdateListVisibility() =>
        ClientListBorder.IsVisible = !(_formMode && _viewModel.ClientSearchResults.Count == 0);

    private void NewClient_Click(object? sender, RoutedEventArgs e)
    {
        SetFormMode(true);
        Dispatcher.UIThread.Post(FocusInitial, DispatcherPriority.Loaded);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        SetFormMode(false);
        Dispatcher.UIThread.Post(FocusInitial, DispatcherPriority.Loaded);
    }

    /// <summary>Фокус при открытии и при смене режима (раньше его ставил ModalShell.InitialFocus): поиск, в форме — имя; с выделением.</summary>
    private void FocusInitial()
    {
        TextBox target = _formMode ? NewClientNameBox : SearchBox;
        if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled)
            return;
        target.Focus(NavigationMethod.Tab);
        target.SelectAll();
    }

    /// <summary>Esc закрывает карточку (в форме тоже — без «назад», чтобы не путать); окно оплаты под ней остаётся.</summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    // 2026-10-24 (приёмка, ПР-8): сообщение о дубле телефона называло одного клиента из двух (CheckoutViewModel.DuplicateClient —
    // первый найденный). Здесь перечисляем всех с этим телефоном (последние 9 цифр) через запятую. Модель не трогаем: полный список
    // клиентов берём из её закрытого поля _allClients (если поле недоступно — из результатов поиска и первого дубля).
    private static readonly System.Reflection.FieldInfo? AllClientsField =
        typeof(CheckoutViewModel).GetField("_allClients", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static string PhoneTail(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length >= 9 ? digits[^9..] : "";
    }

    private void UpdateDuplicateText()
    {
        if (!_viewModel.HasDuplicateClient)
        {
            DuplicateText.Text = "";
            return;
        }
        var tail = PhoneTail(_viewModel.NewClientPhone);
        IEnumerable<ClientOption> pool = AllClientsField?.GetValue(_viewModel) as IEnumerable<ClientOption>
            ?? _viewModel.ClientSearchResults.ToList();
        var matches = pool.Where(c => tail.Length > 0 && PhoneTail(c.Phone) == tail).ToList();
        if (_viewModel.DuplicateClient is { } first && !matches.Contains(first))
            matches.Insert(0, first);
        var names = string.Join(", ", matches.Select(c => c.Name));
        if (names.Length == 0)
        {
            DuplicateText.Text = _viewModel.DuplicateClientText;
            return;
        }
        DuplicateText.Text = Tr.T($"Клиент с таким телефоном уже есть: {names}", $"Бул телефон менен кардар бар: {names}",
            $"A client with this phone already exists: {names}", $"Bu telefonla bir müşteri zaten var: {names}",
            $"Bu telefonli mijoz allaqachon bor: {names}");
    }

    private void OnClientResultsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateNotFound();

    private void UpdateNotFound()
    {
        UpdateListVisibility();
        var empty = _viewModel.ClientSearchResults.Count == 0 && !_viewModel.IsLoadingClients
            && string.IsNullOrEmpty(_viewModel.ErrorMessage);
        NotFoundText.IsVisible = empty;
        if (!empty)
            return;
        var query = (_viewModel.ClientSearchText ?? "").Trim();
        NotFoundText.Text = query.Length > 0
            ? Tr.T($"Клиент «{query}» не найден. Проверьте имя или телефон — или добавьте нового клиента ниже.",
                $"«{query}» кардары табылган жок. Атын же телефонун текшериңиз — же төмөндө жаңы кардар кошуңуз.",
                $"No client “{query}” found. Check the name or phone — or add a new client below.",
                $"«{query}» müşterisi bulunamadı. İsmi veya telefonu kontrol edin — ya da aşağıdan yeni müşteri ekleyin.",
                $"«{query}» mijozi topilmadi. Ism yoki telefonni tekshiring — yoki quyida yangi mijoz qo'shing.")
            : Tr.T("Клиентов пока нет — добавьте нового ниже.", "Азырынча кардарлар жок — төмөндө жаңысын кошуңуз.",
                "No clients yet — add a new one below.", "Henüz müşteri yok — aşağıdan yeni ekleyin.", "Hozircha mijozlar yo'q — quyida yangisini qo'shing.");
    }

    public static System.Threading.Tasks.Task Open(Window owner, CheckoutViewModel viewModel)
    {
        var dialog = new ClientPickerDialog(viewModel);
        dialog.PlaceFor(owner);
        return dialog.ShowDialog(owner);
    }

    /// <summary>2026-10-04, ТЗ разработчика NurCRM: скан QR клиента «NURCRM…», а клиента с таким телефоном
    /// в базе нет — то же окно, но сразу режим «Новый клиент» (телефон уже подставлен в модели), курсор в
    /// поле имени: ФИО вводит кассир. Можно и выбрать клиента из списка (2 строки сверху) или просто закрыть окно.</summary>
    public static System.Threading.Tasks.Task OpenForNewClient(Window owner, CheckoutViewModel viewModel)
    {
        var dialog = new ClientPickerDialog(viewModel);
        // 2026-10-23 (ТЗ компактной оплаты, П-2): сразу форма; фокус в имя — FocusInitial в Opened (раньше ModalShell.InitialFocus).
        dialog.SetFormMode(true);
        dialog.PlaceFor(owner);
        return dialog.ShowDialog(owner);
    }

    // ------------------------------------------------------------------ место карточки
    // 2026-10-23 (ТЗ компактной оплаты, П-2): из окна оплаты карточка встаёт под строкой клиента (элемент ClientRow окна-владельца,
    // +4 px), шириной строки, но 360…440; из главного окна (QR клиента) и без ClientRow — по центру владельца, ширина 440.
    // Низ карточки не ниже подвала окна оплаты (кнопка «Отмена» − 8) и рабочей области экрана: иначе поднимаем, а при нехватке
    // места убавляем строки списка (не меньше 3). Поля 10 вокруг карточки — под тень.
    private const double ShadowPad = 10;
    private const double CardWidthMin = 440, CardWidthMax = 440; // в ТЗ 360…440; длинная подсказка поиска (ky/tr/uz) при 360 не помещается рядом с «Новый клиент»
    private const double CardChrome = 2 + 20 + 44 + 8 + 2; // рамка карточки + поля карточки + шапка + промежуток + рамка списка

    private void PlaceFor(Window owner)
    {
#if NURANDROID
        // Android: размер и место окна задаёт WindowLayerHost (по центру вида); ширина — окно − 24 (но не шире 440).
        var w = owner.Bounds.Width > 0 ? Math.Clamp(owner.Bounds.Width - 24, 300, CardWidthMax) : CardWidthMax;
        Width = w + 2 * ShadowPad;
#else
        try
        {
            var scaling = owner.RenderScaling > 0 ? owner.RenderScaling : 1.0;
            var work = owner.Screens.ScreenFromWindow(owner)?.WorkingArea ?? owner.Screens.Primary?.WorkingArea;
            var row = owner.FindControl<Control>("ClientRow");
            if (row is null || !row.IsEffectivelyVisible || row.Bounds.Width < 50 || work is null
                || row.TranslatePoint(default, owner) is not { } rowPos)
            {
                // Нет строки клиента — по центру владельца; строк списка — сколько влезает в 85 % высоты экрана (обычно 6).
                Width = CardWidthMax + 2 * ShadowPad;
                if (work is { } w0)
                {
                    var workDip = w0.Height / scaling;
                    _listRows = (int)Math.Clamp(Math.Floor((workDip * 0.85 - 2 * ShadowPad - CardChrome) / RowHeight), 3, 6);
                    MaxHeight = Math.Max(240, workDip - 16);
                }
                UpdateListHeight();
                return;
            }

            var cardW = Math.Clamp(row.Bounds.Width, CardWidthMin, CardWidthMax);
            Width = cardW + 2 * ShadowPad;
            var rowTop = owner.PointToScreen(rowPos);
            var ownerTopLeft = owner.PointToScreen(new Point(0, 0));
            var ownerBottomRight = owner.PointToScreen(new Point(owner.Bounds.Width, owner.Bounds.Height));

            // Подвал окна оплаты — кнопка «Отмена» (IsCancel): карточка не должна закрывать подвал.
            double footerTopDip = owner.Bounds.Height - 8;
            var cancel = owner.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsCancel && b.IsEffectivelyVisible);
            if (cancel is not null && cancel.TranslatePoint(default, owner) is { } cancelPos)
                footerTopDip = cancelPos.Y - 8;
            var availBottom = Math.Min(ownerTopLeft.Y + (int)Math.Round(footerTopDip * scaling), work.Value.Bottom);
            var minTop = Math.Max(ownerTopLeft.Y + (int)Math.Round(4 * scaling), work.Value.Y);

            // Строки списка: 6, а если от верха окна оплаты до подвала места мало — меньше, но не меньше 3.
            var spaceDip = (availBottom - minTop) / scaling - 2 * ShadowPad;
            var rows = 6;
            while (rows > 3 && CardChrome + rows * RowHeight > spaceDip)
                rows--;
            _listRows = rows;
            UpdateListHeight();
            var cardH = _formMode ? 380 : CardChrome + rows * RowHeight;

            // По горизонтали — от левого края строки клиента; не правее правого края окна оплаты (− 8) и экрана.
            var cardWPx = (int)Math.Round(cardW * scaling);
            var left = rowTop.X;
            var rightLimit = Math.Min(ownerBottomRight.X - (int)Math.Round(8 * scaling), work.Value.Right);
            if (left + cardWPx > rightLimit)
                left = rightLimit - cardWPx;
            left = Math.Max(left, Math.Max(ownerTopLeft.X, work.Value.X));

            // По вертикали — под строкой клиента (+4); если ниже подвала — поднимаем так, чтобы низ был над ним.
            var cardHPx = (int)Math.Round(cardH * scaling);
            var top = rowTop.Y + (int)Math.Round((row.Bounds.Height + 4) * scaling);
            _anchorTopPx = top;
            if (top + cardHPx > availBottom)
                top = Math.Max(minTop, availBottom - cardHPx);

            _availBottomPx = availBottom;
            _minTopPx = minTop;
            _anchored = true;
            // Высота окна: форма (до 440) может быть выше места в окне оплаты — тогда карточка уходит вверх (Reclamp), но не за экран.
            _screenTopPx = work.Value.Y;
            MaxHeight = Math.Max(240, Math.Min(work.Value.Height / scaling - 16, Math.Max(spaceDip, 440) + 2 * ShadowPad));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(left - (int)Math.Round(ShadowPad * scaling), top - (int)Math.Round(ShadowPad * scaling));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выбор клиента: место карточки: {ex.Message}", "WARNING");
        }
#endif
    }

#if !NURANDROID
    private bool _anchored;
    private int _availBottomPx, _minTopPx, _anchorTopPx, _screenTopPx;

    /// <summary>Высота карточки изменилась (форма, предупреждение о дубле, меньше клиентов в списке) — место пересчитываем от
    /// якоря (низ строки клиента + 4): если низ ушёл ниже подвала окна оплаты — поднимаем (по возможности не выше верха окна оплаты),
    /// если места снова хватает — возвращаем под строку клиента. Идемпотентно: не зависит от промежуточных размеров.</summary>
    private void Reclamp()
    {
        if (!_anchored || !IsVisible)
            return;
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var pad = (int)Math.Round(ShadowPad * scaling);
        var cardHPx = (int)Math.Round(Bounds.Height * scaling) - 2 * pad;
        if (cardHPx <= 0)
            return;
        var top = _anchorTopPx;
        if (top + cardHPx > _availBottomPx)
        {
            top = _availBottomPx - cardHPx;
            if (top < _minTopPx) // выше верха окна оплаты — только если карточка иначе не помещается (форма на низком экране)
                top = Math.Max(_screenTopPx + pad, top);
            top = Math.Min(top, _anchorTopPx);
        }
        if (top - pad != Position.Y)
            Position = new PixelPoint(Position.X, top - pad);
    }
#endif

    /// <summary>Закрывается сразу, как только клиент выбран или только что добавлен — кассиру
    /// не нужно ещё одно подтверждающее нажатие после того, как он уже кликнул по клиенту.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CheckoutViewModel.HasSelectedClient) && _viewModel.HasSelectedClient)
            Close();
        else if (e.PropertyName is nameof(CheckoutViewModel.IsLoadingClients) or nameof(CheckoutViewModel.ErrorMessage))
            UpdateNotFound();
        else if (e.PropertyName is nameof(CheckoutViewModel.HasDuplicateClient) or nameof(CheckoutViewModel.DuplicateClientText))
        {
            UpdateDuplicateText();
            UpdateListHeight(); // 2026-10-24 (ПР-8): в форме всегда 2 строки; предупреждение о дубле место занимает, но карточка ≤ 440
#if !NURANDROID
            Dispatcher.UIThread.Post(Reclamp, DispatcherPriority.Loaded);
#endif
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.ClientSearchResults.CollectionChanged -= OnClientResultsChanged;
        base.OnClosed(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
