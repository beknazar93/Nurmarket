using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using System.Threading.Tasks;

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

    /// <summary>Остаток известен заранее не был — показать «загрузка…» и подтянуть его в фоне.</summary>
    public async Task LoadBalanceInBackgroundAsync()
    {
        if (_loadingBalance || LoadBalanceAsync is null)
            return;
        _loadingBalance = true;
        RetryBalanceButton.IsVisible = false;
        SystemBalanceText.Text = Tr.T("загрузка…", "жүктөлүүдө…", "loading…", "yükleniyor…", "yuklanmoqda…");
        try
        {
            decimal? balance = null;
            for (var attempt = 0; attempt < 2 && balance is null; attempt++)
                balance = await LoadBalanceAsync().ConfigureAwait(true);
            if (balance is { } value)
                UpdateSystemBalance(value);
            else
            {
                SystemBalanceText.Text = "—";
                RetryBalanceButton.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Открытие смены: остаток по системе не загружен ({ex.Message}).", "WARNING");
            SystemBalanceText.Text = "—";
            RetryBalanceButton.IsVisible = true;
        }
        finally
        {
            _loadingBalance = false;
        }
    }

    /// <summary>Остаток пришёл позже открытия окна: показать его и подставить в поле, если кассир ещё ничего не вводил.</summary>
    public void UpdateSystemBalance(decimal balance)
    {
        SuggestedBalance = balance;
        SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(balance);
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

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!CashAmountInput.TryParse(OpeningCashBox.Text, out var amount) || amount < 0)
        {
            // 2026-10-22 (модалки, пакет 2): ошибка под полем с фокусом в нём (было всплывающее окно).
            SectionCards.ShowError(OpeningCashError, Tr.T("Введите сумму не меньше нуля.", "Нөлдөн кем эмес сумманы киргизиңиз.", "Enter an amount of zero or more.", "Sıfır veya daha büyük bir tutar girin.", "Noldan kam bo'lmagan summani kiriting."),
                OpeningCashBox);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
