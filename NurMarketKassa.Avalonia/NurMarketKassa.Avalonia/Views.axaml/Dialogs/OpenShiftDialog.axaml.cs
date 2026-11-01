using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class OpenShiftDialog : Window
{
    // Поле заполняется значением в инвариантной культуре (точка), поэтому и разбор
    // должен быть инвариантным — иначе на ru-RU/de-DE сумма теряется или растёт в 100 раз.
    // 2026-10-15, ревью: NumberStyles.Number считал запятую разделителем тысяч — «1500,50» с русской
    // цифровой клавиатуры становилось 150050 (как в CloseShiftDialog). Общий разбор — CashAmountInput.
    public decimal OpeningCash => CashAmountInput.Parse(OpeningCashBox.Text) ?? 0;

    public decimal? SuggestedBalance { get; set; }

    public OpenShiftDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
        OpeningCashBox.TextChanged += (_, _) =>
        {
            SectionCards.ShowError(OpeningCashError, null);
            if (!_settingProgrammatically)
                _openingCashEdited = true;
        };
    }

    // 2026-10-23 (аудит, круг 5): «Остаток по системе —» не подгрузился за 5 с. Остаток смены тянется в фоне (как в закрытии смены),
    // пока идёт — «загрузка…», не пришёл — «—» и кнопка «Повторить». Кнопка «Открыть смену» не блокируется ни на секунду.
    private bool _openingCashEdited;
    private bool _settingProgrammatically;

    /// <summary>Повтор загрузки остатка (ставит MainWindow): вернуть остаток или null, если не получилось.</summary>
    public Func<Task<decimal?>>? LoadBalanceAsync { get; set; }

    private bool _loadingBalance;

    // 2026-11-01 (приёмка 1.17.72, С-3): «Открыть смену» ждёт загрузки остатка не дольше BalanceWaitMs; дальше кнопка работает — остаток «не загружен»,
    // сумма подтверждается явно (без остатка по системе нельзя молча подставить 0). Ход загрузки — «Загружаем остаток… N с».
    private const long BalanceWaitMs = 8000;
    private long _loadStartedMs;
    private bool _balanceFailed;
    private DispatcherTimer? _loadTimer;

    private string LoadingText() => Tr.T($"Загружаем остаток… {(Environment.TickCount64 - _loadStartedMs) / 1000} с",
        $"Калдык жүктөлүүдө… {(Environment.TickCount64 - _loadStartedMs) / 1000} с", $"Loading the balance… {(Environment.TickCount64 - _loadStartedMs) / 1000} s",
        $"Bakiye yükleniyor… {(Environment.TickCount64 - _loadStartedMs) / 1000} sn", $"Qoldiq yuklanmoqda… {(Environment.TickCount64 - _loadStartedMs) / 1000} s");

    private void StopLoadTimer()
    {
        if (_loadTimer is null)
            return;
        _loadTimer.Stop();
        _loadTimer = null;
    }

    /// <summary>Остаток известен заранее не был — показать «загрузка…» и подтянуть его в фоне.</summary>
    public async Task LoadBalanceInBackgroundAsync()
    {
        if (_loadingBalance || LoadBalanceAsync is null)
            return;
        _loadingBalance = true;
        _balanceFailed = false;
        _loadStartedMs = Environment.TickCount64;
        RetryBalanceButton.IsVisible = false;
        SystemBalanceText.Text = LoadingText();
        StopLoadTimer();
        _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _loadTimer.Tick += (_, _) => { if (_loadingBalance) SystemBalanceText.Text = LoadingText(); };
        _loadTimer.Start();
        // 2026-11-01 (жалоба клиента «сумма не меняется»): пока остаток грузится, в поле НЕ стоит «0.00» (Enter открывал смену с нулём, хотя в ящике деньги) — поле пустое,
        // «Открыть смену» ждёт загрузки (Ok_Click). Кассир ничего не вводил — после загрузки в поле встанет остаток по системе.
        if (!_openingCashEdited)
        {
            _settingProgrammatically = true;
            try { OpeningCashBox.Text = ""; }
            finally { _settingProgrammatically = false; }
        }
        try
        {
            decimal? balance = null;
            for (var attempt = 0; attempt < 2 && balance is null; attempt++)
                balance = await LoadBalanceAsync().ConfigureAwait(true);
            if (balance is { } value)
                UpdateSystemBalance(value);
            else
            {
                _balanceFailed = true;
                SystemBalanceText.Text = Tr.T("не загружен", "жүктөлгөн жок", "not loaded", "yüklenemedi", "yuklanmadi");
                RetryBalanceButton.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Открытие смены: остаток по системе не загружен ({ex.Message}).", "WARNING");
            _balanceFailed = true;
            SystemBalanceText.Text = Tr.T("не загружен", "жүктөлгөн жок", "not loaded", "yüklenemedi", "yuklanmadi");
            RetryBalanceButton.IsVisible = true;
        }
        finally
        {
            _loadingBalance = false;
            StopLoadTimer();
        }
    }

    /// <summary>Остаток пришёл позже открытия окна: показать его и подставить в поле, если кассир ещё ничего не вводил.</summary>
    public void UpdateSystemBalance(decimal balance)
    {
        SuggestedBalance = balance;
        _balanceFailed = false;
        // 2026-11-01 (приёмка 1.17.72): расчётное значение (expected_cash закрытой смены без пересчёта кассира) помечается.
        SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(balance)
            + (ShiftBalanceHelper.LastClosedCashEstimated ? Tr.T(" (расчётный)", " (эсептелген)", " (calculated)", " (hesaplanan)", " (hisoblangan)") : "");
        if (_openingCashEdited)
            return;
        _settingProgrammatically = true;
        try
        {
            OpeningCashBox.Text = balance.ToString("0.00", CultureInfo.InvariantCulture);
        }
        finally
        {
            _settingProgrammatically = false;
        }
    }

    private void RetryBalance_Click(object? sender, RoutedEventArgs e) => _ = LoadBalanceInBackgroundAsync();

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            _settingProgrammatically = true;
            OpeningCashBox.Text = SuggestedBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
            _settingProgrammatically = false;
        }
        else
            _ = LoadBalanceInBackgroundAsync();

        OpeningCashBox.Focus();
        OpeningCashBox.SelectAll();
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-11-01 (жалоба клиента + приёмка 1.17.72, С-3): остаток грузится — ждём не дольше BalanceWaitMs (с ходом «N с»), потом кнопка работает, а сумма подтверждается явно.
        var stillLoading = _loadingBalance;
        if (stillLoading && Environment.TickCount64 - _loadStartedMs < BalanceWaitMs)
        {
            var left = Math.Max(1, (BalanceWaitMs - (Environment.TickCount64 - _loadStartedMs) + 999) / 1000);
            SectionCards.ShowError(OpeningCashError, Tr.T($"Загружаем остаток по системе… Ещё до {left} с, дальше можно открыть смену и без него (с подтверждением суммы).",
                $"Системадагы калдык жүктөлүүдө… Дагы {left} с чейин, андан кийин аны күтпөй эле ачса болот (суммаңызды ырастап).",
                $"Loading the system balance… Up to {left} s more, then you can open the shift without it (with the amount confirmed).",
                $"Sistem bakiyesi yükleniyor… En fazla {left} sn daha, sonra onsuz da açabilirsiniz (tutar onayıyla).",
                $"Tizim qoldig'i yuklanmoqda… Yana {left} soniyagacha, keyin usiz ham ochish mumkin (summani tasdiqlab)."), OpeningCashBox);
            return;
        }

        var balanceUnknown = stillLoading || _balanceFailed;
        var emptyField = string.IsNullOrWhiteSpace(OpeningCashBox.Text);
        decimal amount;
        if (balanceUnknown && emptyField)
            amount = 0m;
        else if (!CashAmountInput.TryParse(OpeningCashBox.Text, out amount) || amount < 0)
        {
            // 2026-10-22 (модалки, пакет 2): ошибка под полем с фокусом в нём (было всплывающее окно).
            SectionCards.ShowError(OpeningCashError, Tr.T("Введите сумму не меньше нуля.", "Нөлдөн кем эмес сумманы киргизиңиз.", "Enter an amount of zero or more.", "Sıfır veya daha büyük bir tutar girin.", "Noldan kam bo'lmagan summani kiriting."),
                OpeningCashBox);
            return;
        }

        // Остаток по системе не загрузился: молча подставлять 0 нельзя — явное подтверждение суммы (по таймауту или при сбое загрузки).
        if (balanceUnknown)
        {
            var sumText = ShiftBalanceHelper.FormatBalance(amount);
            var text = emptyField
                ? Tr.T("Остаток по системе не загрузился, сумма не введена. Открыть смену с нулевой начальной суммой? Проверьте деньги в ящике вручную или нажмите «Повторить».",
                    "Системадагы калдык жүктөлгөн жок, сумма киргизилген жок. Сменаны нөлдүк баштапкы сумма менен ачабызбы? Акчаны кассадан кол менен текшериңиз же «Кайталоо» баскычын басыңыз.",
                    "The system balance did not load and no amount is entered. Open the shift with a zero opening amount? Check the drawer by hand or press “Retry”.",
                    "Sistem bakiyesi yüklenmedi ve tutar girilmedi. Vardiya sıfır başlangıç tutarıyla açılsın mı? Çekmeceyi elle kontrol edin veya «Tekrar dene»'ye basın.",
                    "Tizim qoldig'i yuklanmadi va summa kiritilmadi. Smena nol boshlang'ich summa bilan ochilsinmi? Qutini qo'lda tekshiring yoki «Qayta urinish»ni bosing.")
                : Tr.T($"Остаток по системе не загрузился. Открыть смену с начальной суммой {sumText}? Проверьте, что в ящике именно столько.",
                    $"Системадагы калдык жүктөлгөн жок. Сменаны {sumText} баштапкы сумма менен ачабызбы? Кассада так ушунча акча бар экенин текшериңиз.",
                    $"The system balance did not load. Open the shift with an opening amount of {sumText}? Make sure the drawer holds exactly that.",
                    $"Sistem bakiyesi yüklenmedi. Vardiya {sumText} başlangıç tutarıyla açılsın mı? Çekmecede tam bu kadar olduğundan emin olun.",
                    $"Tizim qoldig'i yuklanmadi. Smena {sumText} boshlang'ich summa bilan ochilsinmi? Qutida aynan shuncha borligiga ishonch hosil qiling.");
            var yes = emptyField
                ? Tr.T("Открыть с нулём", "Нөл менен ачуу", "Open with zero", "Sıfırla aç", "Nol bilan ochish")
                : Tr.T("Открыть с этой суммой", "Ушул сумма менен ачуу", "Open with this amount", "Bu tutarla aç", "Shu summa bilan ochish");
            if (await PosMessageBox.ShowModalAsync(this, text, yes, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    confirmText: yes, cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return;
            if (emptyField)
            {
                _settingProgrammatically = true;
                try { OpeningCashBox.Text = "0.00"; }
                finally { _settingProgrammatically = false; }
            }
        }
        // 2026-11-01 (жалоба клиента): вводят 0 (или не меняют «0.00»), а по системе в ящике деньги — спросить, а не открывать молча.
        else if (amount == 0m && SuggestedBalance is { } system && system > 0.005m)
        {
            var text = Tr.T($"Остаток по системе {ShiftBalanceHelper.FormatBalance(system)}, а вы вводите 0. Открыть смену с нулевой начальной суммой?",
                $"Системадагы калдык {ShiftBalanceHelper.FormatBalance(system)}, ал эми сиз 0 киргизип жатасыз. Сменаны нөлдүк баштапкы сумма менен ачабызбы?",
                $"The system balance is {ShiftBalanceHelper.FormatBalance(system)}, but you enter 0. Open the shift with a zero opening amount?",
                $"Sistem bakiyesi {ShiftBalanceHelper.FormatBalance(system)}, siz ise 0 giriyorsunuz. Vardiya sıfır başlangıç tutarıyla açılsın mı?",
                $"Tizim qoldig'i {ShiftBalanceHelper.FormatBalance(system)}, siz esa 0 kiritayapsiz. Smena nol boshlang'ich summa bilan ochilsinmi?");
            var yes = Tr.T("Открыть с нулём", "Нөл менен ачуу", "Open with zero", "Sıfırla aç", "Nol bilan ochish");
            if (await PosMessageBox.ShowModalAsync(this, text, yes, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    confirmText: yes, cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
