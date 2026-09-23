using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NurMarketKassa.ViewModels
{
    public class BankAccount
    {
        public string BankName { get; set; } = "";
        public string QrCodeImagePath { get; set; } = "";
        public string LogoPath { get; set; } = "";
    }

    public class CheckoutViewModel : INotifyPropertyChanged
    {
        private readonly double _subtotal;
        private readonly double _lineDiscounts;
        private readonly string _initialPercent;
        private readonly string _initialSum;

        private string _paymentMethod = "cash";
        private string _cashReceived = "";
        private string _lastCashReceived = "";
        private string _errorMessage = "";
        private bool _isPrintReceiptEnabled = true;
        private BankAccount? _selectedBank;
        private ObservableCollection<BankAccount> _banks = new();

        private bool _isDiscountPercent = true;
        private string _discountInput = "0";
        private double _orderDiscountAmount;
        private double _effectiveTotalDue;
        private double _changeAmount;
        private bool _isInsufficientCash;
        private bool _discountDirty;

        public CheckoutViewModel(
            CartTotalsCalculator.CartTotals totals,
            string initialPercent,
            string initialSum)
        {
            _subtotal = totals.Subtotal;
            _lineDiscounts = totals.LineDiscounts;
            _effectiveTotalDue = totals.TotalDue;
            _initialPercent = initialPercent ?? "";
            _initialSum = initialSum ?? "";

            if (!string.IsNullOrWhiteSpace(_initialPercent) && !OrderDiscountHelper.IsEmptyOrZeroLike(_initialPercent))
            {
                _isDiscountPercent = true;
                _discountInput = _initialPercent;
            }
            else if (!string.IsNullOrWhiteSpace(_initialSum) && !OrderDiscountHelper.IsEmptyOrZeroLike(_initialSum))
            {
                _isDiscountPercent = false;
                _discountInput = _initialSum;
            }

            CashReceived = totals.TotalDue.ToString("0.00", CultureInfo.InvariantCulture);
            _lastCashReceived = CashReceived;
            _isPrintReceiptEnabled = UserPreferences.Instance.ReceiptEnabled;

            PayCommand = new RelayCommand(ExecutePay);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
            AddCash100Command = new RelayCommand(() => SetCash(100), () => IsCashMode);
            AddCash200Command = new RelayCommand(() => SetCash(200), () => IsCashMode);
            AddCash500Command = new RelayCommand(() => SetCash(500), () => IsCashMode);
            AddCash1000Command = new RelayCommand(() => SetCash(1000), () => IsCashMode);
            ExactCashCommand = new RelayCommand(SetExactCash, () => IsCashMode);
            ClearCashCommand = new RelayCommand(ClearCash, () => IsCashMode);

            LoadBanks();
            RecalculateTotals();
            UpdatePaymentMode();
        }

        public double EffectiveTotalDue => _effectiveTotalDue;

        public string BigTotalDisplay =>
            _effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture);

        public string SubtotalDisplay =>
            $"{_subtotal.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        public string DiscountSummaryDisplay =>
            $"{_orderDiscountAmount.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        public string PayableDisplay =>
            $"{_effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        public string PayButtonText =>
            $"Оплатить {_effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        public string ChangeAmountDisplay =>
            _changeAmount.ToString("0.00", CultureInfo.InvariantCulture);

        public string ChangeStatusText =>
            IsCashMode
                ? _isInsufficientCash
                    ? "Недостаточно средств"
                    : _changeAmount > 1e-9
                        ? "Сдача будет выдана клиенту"
                        : "Точная сумма"
                : "";

        public bool ShowChangeBlock => IsCashMode;
        public bool ChangeStatusIsOk => IsCashMode && !_isInsufficientCash;
        public bool ChangeStatusIsWarn => IsCashMode && _isInsufficientCash;

        public bool IsDiscountPercent
        {
            get => _isDiscountPercent;
            set
            {
                if (_isDiscountPercent == value)
                    return;
                _isDiscountPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDiscountSum));
                OnPropertyChanged(nameof(DiscountInputSuffix));
                RecalculateTotals();
            }
        }

        public bool IsDiscountSum
        {
            get => !_isDiscountPercent;
            set { if (value) IsDiscountPercent = false; }
        }

        public string DiscountInputSuffix => _isDiscountPercent ? "%" : "сом";

        public string DiscountInput
        {
            get => _discountInput;
            set
            {
                if (_discountInput == value)
                    return;
                _discountInput = value;
                OnPropertyChanged();
                RecalculateTotals();
            }
        }

        public string DiscountAmountPreview => DiscountSummaryDisplay;

        public bool IsCash
        {
            get => _paymentMethod == "cash";
            set { if (value) PaymentMethod = "cash"; }
        }

        public bool IsTransfer
        {
            get => _paymentMethod == "transfer";
            set { if (value) PaymentMethod = "transfer"; }
        }

        public string PaymentMethod
        {
            get => _paymentMethod;
            private set
            {
                if (_paymentMethod == value)
                    return;
                if (_paymentMethod == "cash")
                    _lastCashReceived = _cashReceived;
                _paymentMethod = value;
                OnPropertyChanged();
                if (_paymentMethod == "cash")
                    RestoreCashAmount();
                UpdatePaymentMode();
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
            }
        }

        public string CashReceived
        {
            get => _cashReceived;
            set
            {
                if (_cashReceived == value)
                    return;
                _cashReceived = value;
                OnPropertyChanged();
                UpdateCashState();
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool IsPrintReceiptEnabled
        {
            get => _isPrintReceiptEnabled;
            set { _isPrintReceiptEnabled = value; OnPropertyChanged(); }
        }

        public bool IsCashMode => _paymentMethod == "cash";
        public bool IsBankSelectionVisible => _paymentMethod == "transfer";
        public bool CanPay => CanExecutePay(null);

        public ObservableCollection<BankAccount> Banks
        {
            get => _banks;
            set { _banks = value; OnPropertyChanged(); }
        }

        public BankAccount? SelectedBank
        {
            get => _selectedBank;
            set
            {
                _selectedBank = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QrCodePath));
                OnPropertyChanged(nameof(HasQrCode));
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
            }
        }

        public string? QrCodePath =>
            !string.IsNullOrWhiteSpace(SelectedBank?.QrCodeImagePath)
            && File.Exists(SelectedBank.QrCodeImagePath)
                ? SelectedBank.QrCodeImagePath
                : null;

        public bool HasQrCode => !string.IsNullOrEmpty(QrCodePath);
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public ICommand PayCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AddCash100Command { get; }
        public ICommand AddCash200Command { get; }
        public ICommand AddCash500Command { get; }
        public ICommand AddCash1000Command { get; }
        public ICommand ExactCashCommand { get; }
        public ICommand ClearCashCommand { get; }

        public event Action<bool>? RequestClose;

        public Dictionary<string, string>? PendingOrderDiscountBody
        {
            get
            {
                if (!_discountDirty)
                    return null;

                var body = OrderDiscountHelper.BuildPatchBody(_isDiscountPercent, _discountInput);
                if (body.Count > 0)
                    return body;

                var clear = OrderDiscountHelper.BuildClearPatchBody(_initialPercent, _initialSum);
                return clear.Count > 0 ? clear : null;
            }
        }

        private void SetCash(double amount) =>
            CashReceived = amount.ToString("0.00", CultureInfo.InvariantCulture);

        private void SetExactCash() =>
            CashReceived = _effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture);

        private void ClearCash() => CashReceived = "";

        private void RecalculateTotals()
        {
            var previousTotal = _effectiveTotalDue;
            _orderDiscountAmount = ComputeOrderDiscountAmount();
            _effectiveTotalDue = Math.Max(0, _subtotal - _lineDiscounts - _orderDiscountAmount);
            _discountDirty = IsDiscountChangedFromInitial();

            OnPropertyChanged(nameof(BigTotalDisplay));
            OnPropertyChanged(nameof(SubtotalDisplay));
            OnPropertyChanged(nameof(DiscountSummaryDisplay));
            OnPropertyChanged(nameof(DiscountAmountPreview));
            OnPropertyChanged(nameof(PayableDisplay));
            OnPropertyChanged(nameof(PayButtonText));

            if (IsCashMode)
            {
                var cash = ParseCash();
                if (cash == null || Math.Abs(cash.Value - previousTotal) < 1e-9)
                    CashReceived = _effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture);
            }

            UpdateCashState();
        }

        private double ComputeOrderDiscountAmount()
        {
            var normalized = OrderDiscountHelper.NormalizeDecimal(_discountInput);
            if (string.IsNullOrWhiteSpace(normalized) || OrderDiscountHelper.IsEmptyOrZeroLike(normalized))
                return 0;

            if (!double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) || val <= 0)
                return 0;

            if (_isDiscountPercent)
            {
                var baseAmount = Math.Max(0, _subtotal - _lineDiscounts);
                return Math.Min(baseAmount, baseAmount * val / 100.0);
            }

            return Math.Min(Math.Max(0, _subtotal - _lineDiscounts), val);
        }

        private bool IsDiscountChangedFromInitial()
        {
            var normalized = OrderDiscountHelper.NormalizeDecimal(_discountInput);
            if (_isDiscountPercent)
            {
                var init = OrderDiscountHelper.NormalizeDecimal(_initialPercent);
                return !string.Equals(normalized, init, StringComparison.Ordinal);
            }

            var initSum = OrderDiscountHelper.NormalizeDecimal(_initialSum);
            return !string.Equals(normalized, initSum, StringComparison.Ordinal);
        }

        private void UpdateCashState()
        {
            if (!IsCashMode)
            {
                _changeAmount = 0;
                _isInsufficientCash = false;
            }
            else
            {
                var cash = ParseCash();
                if (cash == null)
                {
                    _changeAmount = 0;
                    _isInsufficientCash = true;
                }
                else if (cash.Value + 1e-9 < _effectiveTotalDue)
                {
                    _changeAmount = 0;
                    _isInsufficientCash = true;
                }
                else
                {
                    _changeAmount = cash.Value - _effectiveTotalDue;
                    _isInsufficientCash = false;
                }
            }

            OnPropertyChanged(nameof(ChangeAmountDisplay));
            OnPropertyChanged(nameof(ChangeStatusText));
            OnPropertyChanged(nameof(ChangeStatusIsOk));
            OnPropertyChanged(nameof(ChangeStatusIsWarn));
            OnPropertyChanged(nameof(ShowChangeBlock));
            RaiseCommandsCanExecuteChanged();
        }

        private void RaiseCommandsCanExecuteChanged()
        {
            OnPropertyChanged(nameof(CanPay));
            (PayCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCash100Command as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCash200Command as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCash500Command as RelayCommand)?.RaiseCanExecuteChanged();
            (AddCash1000Command as RelayCommand)?.RaiseCanExecuteChanged();
            (ExactCashCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ClearCashCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private double? ParseCash()
        {
            var normalized = CheckoutValidation.NormalizeDecimal(CashReceived);
            if (string.IsNullOrEmpty(normalized))
                return null;
            return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var cash)
                ? cash
                : null;
        }

        private void RestoreCashAmount()
        {
            var normalized = CheckoutValidation.NormalizeDecimal(_lastCashReceived);
            var restored = double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var cash)
                && cash + 1e-9 >= _effectiveTotalDue
                    ? cash
                    : _effectiveTotalDue;
            CashReceived = restored.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private bool CanExecutePay(object? _)
        {
            if (PaymentMethod == "cash")
            {
                var cash = ParseCash();
                return cash is { } c && c + 1e-9 >= _effectiveTotalDue;
            }

            return SelectedBank != null && HasQrCode;
        }

        private void ExecutePay()
        {
            var discountError = ValidateDiscount();
            if (discountError != null)
            {
                ErrorMessage = discountError;
                return;
            }

            if (PaymentMethod == "cash")
            {
                var error = CheckoutValidation.ValidateCashReceived(CashReceived, _effectiveTotalDue);
                if (error != null)
                {
                    ErrorMessage = error;
                    return;
                }
            }
            else
            {
                if (SelectedBank == null)
                {
                    ErrorMessage = "Выберите банк для безналичной оплаты.";
                    return;
                }

                if (!HasQrCode)
                {
                    ErrorMessage =
                        $"Для банка \"{SelectedBank.BankName}\" не загружен QR‑код.\nПожалуйста, загрузите QR‑код в настройках.";
                    return;
                }
            }

            RequestClose?.Invoke(true);
        }

        private string? ValidateDiscount()
        {
            var normalized = OrderDiscountHelper.NormalizeDecimal(_discountInput);
            if (OrderDiscountHelper.IsEmptyOrZeroLike(normalized))
                return null;

            var error = _isDiscountPercent
                ? OrderDiscountHelper.ValidatePercent(normalized)
                : OrderDiscountHelper.ValidateSum(normalized);
            if (error != null)
                return error;

            if (!_isDiscountPercent
                && double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                var maximum = Math.Max(0, _subtotal - _lineDiscounts);
                if (value > maximum + 1e-9)
                    return $"Скидка не может превышать сумму товаров: {maximum:0.00} сом.";
            }

            return null;
        }

        private void UpdatePaymentMode()
        {
            OnPropertyChanged(nameof(IsCash));
            OnPropertyChanged(nameof(IsTransfer));
            OnPropertyChanged(nameof(IsCashMode));
            OnPropertyChanged(nameof(IsBankSelectionVisible));
            UpdateCashState();
        }

        private void LoadBanks()
        {
            var prefs = UserPreferences.Instance;
            prefs.BankQrPaths ??= new Dictionary<string, string>();

            var bankNames = new[] { "Элкарт", "MBank", "ФинкаБанк" };
            var logoNames = new Dictionary<string, string>
            {
                { "Элкарт", "Elkart-logo.png" },
                { "MBank", "Mbank-logo.png" },
                { "ФинкаБанк", "Finca-logo.png" },
            };

            var list = new ObservableCollection<BankAccount>();
            foreach (var name in bankNames)
            {
                var qrPath = prefs.BankQrPaths.TryGetValue(name, out var path) ? path : "";
                var logoPath = $"avares://NurMarketKassa.Avalonia/Assets/{logoNames[name]}";

                list.Add(new BankAccount
                {
                    BankName = name,
                    QrCodeImagePath = qrPath,
                    LogoPath = logoPath,
                });
            }

            Banks = list;
            if (Banks.Count > 0)
                SelectedBank = Banks.FirstOrDefault(bank =>
                    !string.IsNullOrWhiteSpace(bank.QrCodeImagePath)
                    && File.Exists(bank.QrCodeImagePath)) ?? Banks[0];
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
