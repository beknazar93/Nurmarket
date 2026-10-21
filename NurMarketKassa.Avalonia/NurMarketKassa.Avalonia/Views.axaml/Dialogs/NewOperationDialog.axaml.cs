using System.Globalization;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class NewOperationDialog : Window
{
    private bool _isDeposit = true;

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
            : App.CurrentUserId ?? "Кассир 1";
        CashierBox.Items.Clear();
        CashierBox.Items.Add(cashier);
        CashierBox.SelectedIndex = 0;
    }

    public NewOperationDialog(object? arg) : this(arg is true) { }

    private void OnOpTypeChanged(object? sender, RoutedEventArgs e) =>
        _isDeposit = DepositTab.IsChecked == true;

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var raw = (AmountBox.Text ?? "").Replace(',', '.').Trim();
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            PosMessageBox.Show(this,
                Tr.T("Укажите корректную сумму.", "Туура сумманы көрсөтүңүз.", "Enter a valid amount.",
                    "Geçerli bir tutar girin.", "To'g'ri summani kiriting."),
                Tr.T("Новая операция", "Жаңы операция", "New operation", "Yeni işlem", "Yangi operatsiya"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2026-10-21 (пакет 5, п. 42): причина обязательна и для внесения, и для изъятия — живой аудит 20.10: внесение
        // сохранилось с пустой «Причиной», в отчёте смены потом не понять, откуда деньги.
        if (string.IsNullOrWhiteSpace(ReasonBox.Text))
        {
            PosMessageBox.Show(this,
                Tr.T("Укажите причину операции — она попадёт в отчёт смены.",
                    "Операциянын себебин жазыңыз — ал сменанын отчётуна кирет.",
                    "Enter the reason for the operation — it goes into the shift report.",
                    "İşlemin nedenini yazın — vardiya raporuna girecek.",
                    "Operatsiya sababini kiriting — u smena hisobotiga tushadi."),
                Tr.T("Новая операция", "Жаңы операция", "New operation", "Yeni işlem", "Yangi operatsiya"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ReasonBox.Focus();
            return;
        }

        // 2026-10-21 (пакет 5, п. 42): внесение — тоже с подтверждением суммы и того, сколько станет в кассе
        // (как у изъятия): ошибка в сумме (лишний ноль) иначе сразу уходила в смену и на сервер.
        if (_isDeposit)
        {
            var confirm = CashWithdrawalGuard.DrawerCash() is { } cashNow
                ? Tr.T(
                    $"Внести {amount:0.00} сом в кассу? В кассе станет {cashNow + amount:0.00} сом.",
                    $"Кассага {amount:0.00} сом салынсынбы? Кассада {cashNow + amount:0.00} сом болот.",
                    $"Put {amount:0.00} som into the till? The till will have {cashNow + amount:0.00} som.",
                    $"Kasaya {amount:0.00} som girilsin mi? Kasada {cashNow + amount:0.00} som olacak.",
                    $"Kassaga {amount:0.00} so'm kiritilsinmi? Kassada {cashNow + amount:0.00} so'm bo'ladi.")
                : Tr.T(
                    $"Внести {amount:0.00} сом в кассу?",
                    $"Кассага {amount:0.00} сом салынсынбы?",
                    $"Put {amount:0.00} som into the till?",
                    $"Kasaya {amount:0.00} som girilsin mi?",
                    $"Kassaga {amount:0.00} so'm kiritilsinmi?");
            if (!PosDialogs.ConfirmYesNo(this, confirm,
                    Tr.T("Внесение", "Акча салуу", "Cash in", "Para girişi", "Kirim")))
                return;
        }

        // 2026-10-04, ТЗ 1.17.48 P0-2: изъятие не больше наличных в кассе и с подтверждением «станет».
        if (!_isDeposit)
        {
            if (CashWithdrawalGuard.Refusal(amount) is { } refusal)
            {
                PosMessageBox.Show(this, refusal, CashWithdrawalGuard.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!PosDialogs.ConfirmYesNo(this, CashWithdrawalGuard.ConfirmText(amount), CashWithdrawalGuard.Title))
                return;
        }

        var type = _isDeposit ? "Внесение" : "Изъятие";
        var createdAt = DateTime.Now;
        var cashier = App.CurrentUserId ?? CashierBox.SelectedItem?.ToString() ?? "—";
        var reason = (ReasonBox.Text ?? "").Trim();
        var comment = (CommentBox.Text ?? "").Trim();
        var note = string.IsNullOrWhiteSpace(comment) ? reason : comment;

        ResultOperation = new CashOperationModel
        {
            CreatedAt = createdAt,
            Type = type,
            Kind = CashOperationModel.ResolveKind(type),
            Amount = amount,
            Cashier = cashier,
            Reason = reason,
            Comment = note,
        };

        DialogResult = true;
        Close(true);
    }
}
