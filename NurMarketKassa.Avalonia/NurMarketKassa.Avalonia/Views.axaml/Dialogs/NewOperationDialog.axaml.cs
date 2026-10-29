using System.Globalization;
using System.Windows;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class NewOperationDialog : Window
{
    private bool _isDeposit = true;
    /// <summary>2026-10-22 (статьи изъятия): выбранная статья (ключ CashArticles); null — не выбрана (по умолчанию).</summary>
    private string? _articleKey;
    private readonly string? _defaultCommentWatermark;

    public CashOperationModel? ResultOperation { get; set; }
    public bool? DialogResult { get; set; }

    public NewOperationDialog() : this(isDeposit: true) { }

    public NewOperationDialog(bool isDeposit)
    {
        _isDeposit = isDeposit;
        InitializeComponent();
        DepositTab.IsChecked = isDeposit;
        WithdrawTab.IsChecked = !isDeposit;
        DepositTab.IsCheckedChanged += OnOpTypeChanged;
        WithdrawTab.IsCheckedChanged += OnOpTypeChanged;
        DateBox.SelectedDate = DateTime.Today;
        // В списке — имя кассира, а не его внутренний ID (раньше показывался GUID
        // «6e39345d-ba15-…»). В саму операцию по-прежнему пишется ID, см. Save_Click.
        var cashier = !string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserDisplayName)
            ? NurMarketKassa.PosApp.CurrentUserDisplayName!
            // 2026-10-23 (аудит, круг 8, В-1): App.CurrentUserId (сессия) мог быть пуст — сначала id из PosApp; «Кассир 1» только если пусто везде.
            : !string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserId) ? NurMarketKassa.PosApp.CurrentUserId!
            : !string.IsNullOrWhiteSpace(App.CurrentUserId) ? App.CurrentUserId! : FallbackCashierTitle;
        CashierBox.Items.Clear();
        CashierBox.Items.Add(cashier);
        CashierBox.SelectedIndex = 0;

        // 2026-10-22 (статьи изъятия): плитки статей — сразу из кэша; справочник NurCRM (статьи, заведённые владельцем на
        // сайте) подтягивается в фоне и дополняет плитки, выбор кассира при этом сохраняется.
        _defaultCommentWatermark = CommentBox.Watermark;
        // 2026-10-22 (модалки, пакет 2): Enter в многострочном комментарии переносит строку, Ctrl+Enter — «Сохранить».
        CommentBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && NurMarketKassa.AvaloniaHost.Services.PlatformKeys.HasCommand(e.KeyModifiers))
            {
                e.Handled = true;
                Save_Click(null, new RoutedEventArgs());
            }
        }, RoutingStrategies.Tunnel);
        AmountBox.TextChanged += (_, _) => SectionCards.ShowError(AmountError, null);
        CommentBox.TextChanged += (_, _) => SectionCards.ShowError(CommentError, null);
        BuildArticles();
        _ = RefreshArticlesAsync();
    }

    public NewOperationDialog(object? arg) : this(arg is true) { }

    private void OnOpTypeChanged(object? sender, RoutedEventArgs e)
    {
        var deposit = DepositTab.IsChecked == true;
        if (deposit == _isDeposit)
            return;
        _isDeposit = deposit;
        // Статьи внесения и изъятия разные — выбор сбрасывается.
        _articleKey = null;
        BuildArticles();
    }

    /// <summary>2026-10-22 (статьи изъятия): плитки статей для текущего вида операции (UniformGrid по 3 в ряд).</summary>
    private void BuildArticles()
    {
        ArticleLabel.Text = _isDeposit
            ? Tr.T("Откуда деньги", "Акча кайдан", "Source of the money", "Para nereden", "Pul qayerdan")
            : Tr.T("Статья расхода", "Чыгым беренеси", "Expense category", "Gider kalemi", "Xarajat moddasi");
        ArticleGrid.Children.Clear();
        var articles = CashArticles.ForDialog(withdrawal: !_isDeposit);
        if (articles.All(a => a.Key != _articleKey))
            _articleKey = null;
        foreach (var article in articles)
        {
            var key = article.Key;
            var tile = new RadioButton
            {
                GroupName = "CashArticle",
                Tag = key,
                IsChecked = key == _articleKey,
                Content = new TextBlock
                {
                    Text = article.Title,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            };
            tile.Classes.Add("article-tile");
            AutomationProperties.SetName(tile, article.Title);
            tile.IsCheckedChanged += (_, _) =>
            {
                if (tile.IsChecked != true)
                    return;
                _articleKey = key;
                SectionCards.ShowError(ArticleError, null);
                UpdateArticleState();
            };
            ArticleGrid.Children.Add(tile);
        }
        UpdateArticleState();
    }

    /// <summary>«Прочее» — комментарий обязателен (звёздочка и подсказка); «Инкассация» — пояснение, что это не расход магазина.</summary>
    private void UpdateArticleState()
    {
        var article = CashArticles.Find(!_isDeposit, _articleKey);
        var required = article?.CommentRequired == true;
        CommentStar.IsVisible = required;
        CommentBox.Watermark = required
            ? (_isDeposit
                ? Tr.T("Обязательно: откуда эти деньги", "Милдеттүү: бул акча кайдан", "Required: where this money comes from",
                    "Zorunlu: bu para nereden", "Majburiy: bu pul qayerdan")
                : Tr.T("Обязательно: на что ушли деньги", "Милдеттүү: акча эмнеге кетти", "Required: what the money was spent on",
                    "Zorunlu: para neye harcandı", "Majburiy: pul nimaga sarflandi"))
            : _defaultCommentWatermark;
        ArticleHint.IsVisible = article?.IsCollection == true;
        ArticleHint.Text = Tr.T(
            "Инкассация — деньги сданы владельцу: из кассы они уходят, но это не расход магазина и прибыль не уменьшает.",
            "Инкассация — акча ээсине тапшырылды: кассадан чыгат, бирок бул дүкөндүн чыгымы эмес, пайданы азайтпайт.",
            "Cash collection — the money is handed to the owner: it leaves the till, but it is not a shop expense and does not reduce profit.",
            "Para teslimi — para sahibine verildi: kasadan çıkar, ama mağaza gideri değildir ve kârı azaltmaz.",
            "Inkassatsiya — pul egasiga topshirildi: kassadan chiqadi, lekin bu do'kon xarajati emas va foydani kamaytirmaydi.");
    }

    /// <summary>Справочник статей NurCRM в фоне (кэш 30 мин): появились статьи, заведённые владельцем, — плитки перестраиваются.</summary>
    private async Task RefreshArticlesAsync()
    {
        try
        {
            var before = string.Join("|", CashArticles.ForDialog(withdrawal: true).Select(a => a.Key));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await CashArticles.LoadCategoriesAsync(force: false, cts.Token).ConfigureAwait(true) is null)
                return;
            if (!_isDeposit && string.Join("|", CashArticles.ForDialog(withdrawal: true).Select(a => a.Key)) != before)
                BuildArticles();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно внесения/изъятия: статьи NurCRM не обновлены: {ex.Message}", "WARNING");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }

    /// <summary>2026-10-22 (модалки, пакет 2): «Кассир 1» на языке программы — только в списке; в данные пишется по-прежнему «Кассир 1».</summary>
    private static string FallbackCashierTitle => Tr.T("Кассир 1", "Кассир 1", "Cashier 1", "Kasiyer 1", "Kassir 1");

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-22 (модалки, пакет 2): повторное нажатие/Enter, пока открыт вопрос «Внести/Изъять», — ничего не делает.
        if (!Shell.TryBeginBusy())
            return;
        try
        {
            SaveCore();
        }
        finally
        {
            Shell.EndBusy();
        }
    }

    private void SaveCore()
    {
        var raw = (AmountBox.Text ?? "").Replace(',', '.').Trim();
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            // 2026-10-22 (модалки, пакет 2): ошибка под полем суммы (было всплывающее окно).
            SectionCards.ShowError(AmountError,
                Tr.T("Укажите корректную сумму.", "Туура сумманы көрсөтүңүз.", "Enter a valid amount.",
                    "Geçerli bir tutar girin.", "To'g'ri summani kiriting."), AmountBox);
            return;
        }

        // 2026-10-21 (пакет 5, п. 42): причина обязательна и для внесения, и для изъятия — живой аудит 20.10: внесение
        // сохранилось с пустой «Причиной», в отчёте смены потом не понять, откуда деньги.
        // 2026-10-22 (статьи изъятия): причина — выбранная статья; для «Прочее» обязателен комментарий.
        var article = CashArticles.Find(!_isDeposit, _articleKey);
        if (article is null)
        {
            SectionCards.ShowError(ArticleError,
                _isDeposit
                    ? Tr.T("Выберите, откуда деньги (размен, возврат из инкассации или прочее), — это попадёт в отчёт смены.",
                        "Акча кайдан экенин тандаңыз (майда акча, инкассациядан кайтаруу же башка) — бул сменанын отчётуна кирет.",
                        "Choose where the money comes from (change float, return from collection or other) — it goes into the shift report.",
                        "Paranın nereden geldiğini seçin (bozuk para, teslimden iade veya diğer) — vardiya raporuna girecek.",
                        "Pul qayerdan ekanini tanlang (mayda pul, inkassatsiyadan qaytarish yoki boshqa) — bu smena hisobotiga tushadi.")
                    : Tr.T("Выберите статью расхода (аренда, налог, зарплата…) — она попадёт в отчёт смены и в расходы владельца.",
                        "Чыгым беренесин тандаңыз (ижара, салык, эмгек акы…) — ал сменанын отчётуна жана ээсинин чыгымдарына кирет.",
                        "Choose the expense category (rent, tax, salary…) — it goes into the shift report and the owner's expenses.",
                        "Gider kalemini seçin (kira, vergi, maaş…) — vardiya raporuna ve sahibin giderlerine girecek.",
                        "Xarajat moddasini tanlang (ijara, soliq, ish haqi…) — u smena hisobotiga va egasining xarajatlariga tushadi."));
            ArticleError.BringIntoView();
            return;
        }
        var articleComment = (CommentBox.Text ?? "").Trim();
        if (article.CommentRequired && articleComment.Length == 0)
        {
            SectionCards.ShowError(CommentError,
                _isDeposit
                    ? Tr.T("Для «Прочее» напишите в комментарии, откуда эти деньги.", "«Башка» үчүн комментарийге бул акча кайдан экенин жазыңыз.",
                        "For “Other”, write in the comment where this money comes from.", "«Diğer» için yorumda bu paranın nereden geldiğini yazın.",
                        "«Boshqa» uchun izohda bu pul qayerdan ekanini yozing.")
                    : Tr.T("Для «Прочее» напишите в комментарии, на что ушли деньги.", "«Башка» үчүн комментарийге акча эмнеге кеткенин жазыңыз.",
                        "For “Other”, write in the comment what the money was spent on.", "«Diğer» için yorumda paranın neye harcandığını yazın.",
                        "«Boshqa» uchun izohda pul nimaga sarflanganini yozing."), CommentBox);
            return;
        }
        var articleLine = (_isDeposit
            ? Tr.T("Откуда: ", "Кайдан: ", "Source: ", "Kaynak: ", "Qayerdan: ")
            : Tr.T("Статья: ", "Берене: ", "Category: ", "Kalem: ", "Modda: ")) + article.Title;

        // 2026-10-21 (пакет 5, п. 42): внесение — тоже с подтверждением суммы и того, сколько станет в кассе
        // (как у изъятия): ошибка в сумме (лишний ноль) иначе сразу уходила в смену и на сервер.
        if (_isDeposit)
        {
            var confirm = CashWithdrawalGuard.DrawerCash() is { } cashNow
                ? Tr.T(
                    $"Внести {MoneyText.Amount(amount)} сом в кассу? В кассе станет {MoneyText.Amount(cashNow + amount)} сом.",
                    $"Кассага {MoneyText.Amount(amount)} сом салынсынбы? Кассада {MoneyText.Amount(cashNow + amount)} сом болот.",
                    $"Put {MoneyText.Amount(amount)} som into the till? The till will have {MoneyText.Amount(cashNow + amount)} som.",
                    $"Kasaya {MoneyText.Amount(amount)} som girilsin mi? Kasada {MoneyText.Amount(cashNow + amount)} som olacak.",
                    $"Kassaga {MoneyText.Amount(amount)} so'm kiritilsinmi? Kassada {MoneyText.Amount(cashNow + amount)} so'm bo'ladi.")
                : Tr.T(
                    $"Внести {MoneyText.Amount(amount)} сом в кассу?",
                    $"Кассага {MoneyText.Amount(amount)} сом салынсынбы?",
                    $"Put {MoneyText.Amount(amount)} som into the till?",
                    $"Kasaya {MoneyText.Amount(amount)} som girilsin mi?",
                    $"Kassaga {MoneyText.Amount(amount)} so'm kiritilsinmi?");
            // 2026-10-22 (модалки, пакет 2): кнопка-глагол с суммой вместо «Да/Нет».
            if (!PosDialogs.ConfirmYesNo(this, confirm + "\n" + articleLine,
                    Tr.T("Внесение", "Акча салуу", "Cash in", "Para girişi", "Kirim"),
                    confirmText: Tr.T($"Внести {MoneyText.Amount(amount)} сом", $"{MoneyText.Amount(amount)} сом салуу", $"Put in {MoneyText.Amount(amount)} som",
                        $"{MoneyText.Amount(amount)} som gir", $"{MoneyText.Amount(amount)} so'm kiritish")))
                return;
        }

        // 2026-10-04, ТЗ 1.17.48 P0-2: изъятие не больше наличных в кассе и с подтверждением «станет».
        if (!_isDeposit)
        {
            if (CashWithdrawalGuard.Refusal(amount) is { } refusal)
            {
                // 2026-10-22 (модалки, пакет 2): «больше, чем в кассе» — под полем суммы.
                SectionCards.ShowError(AmountError, refusal, AmountBox);
                return;
            }
            if (!PosDialogs.ConfirmYesNo(this, CashWithdrawalGuard.ConfirmText(amount) + "\n" + articleLine, CashWithdrawalGuard.Title,
                    confirmText: Tr.T($"Изъять {MoneyText.Amount(amount)} сом", $"{MoneyText.Amount(amount)} сом алуу", $"Take out {MoneyText.Amount(amount)} som",
                        $"{MoneyText.Amount(amount)} som çek", $"{MoneyText.Amount(amount)} so'm olish")))
                return;
        }

        var type = _isDeposit ? "Внесение" : "Изъятие";
        var createdAt = DateTime.Now;
        var selectedCashier = CashierBox.SelectedItem?.ToString();
        // 2026-10-23 (аудит, круг 8, В-1): пустой id сессии (не null) раньше проходил мимо «??» и в операцию писалась пустота.
        var cashier = (!string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserId) ? NurMarketKassa.PosApp.CurrentUserId
                          : !string.IsNullOrWhiteSpace(App.CurrentUserId) ? App.CurrentUserId : null)
                      ?? (selectedCashier == FallbackCashierTitle ? "Кассир 1" : selectedCashier)
                      ?? "—";
        // 2026-10-22 (статьи изъятия): Reason — статья (канон по-русски), Comment — «Аренда — комментарий» для журнала смены.
        var reason = article.Ru;
        var note = articleComment.Length > 0 ? $"{article.Title} — {articleComment}" : article.Title;

        ResultOperation = new CashOperationModel
        {
            CreatedAt = createdAt,
            Type = type,
            Kind = CashOperationModel.ResolveKind(type),
            Amount = amount,
            Cashier = cashier,
            Reason = reason,
            Comment = note,
            ArticleKey = article.Key,
            ArticleRu = article.Ru,
            ArticleComment = articleComment.Length > 0 ? articleComment : null,
        };
        CashArticles.RememberLastUsed(!_isDeposit, article.Key);

        DialogResult = true;
        Close(true);
    }
}
