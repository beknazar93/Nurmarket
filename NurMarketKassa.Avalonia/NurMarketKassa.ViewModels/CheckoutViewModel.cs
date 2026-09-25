using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NurMarketKassa.ViewModels
{
    public class BankAccount
    {
        public string BankName { get; set; } = "";
        public string QrCodeImagePath { get; set; } = "";
        public string LogoPath { get; set; } = "";
    }

    public class ClientOption
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";

        /// <summary>Имя и телефон по отдельности — для строки списка клиентов (2026-09-24).
        /// Если их не задали, берутся из DisplayName вида «Имя · телефон».</summary>
        public string Name
        {
            get => string.IsNullOrWhiteSpace(_name) ? SplitDisplay().Name : _name!;
            init => _name = value;
        }

        public string Phone
        {
            get => string.IsNullOrWhiteSpace(_phone) ? SplitDisplay().Phone : _phone!;
            init => _phone = value;
        }

        public bool HasPhone => !string.IsNullOrWhiteSpace(Phone);

        /// <summary>Одна-две буквы для кружка слева: «Кайрат» → «К», «Нур Тест» → «НТ».</summary>
        public string Initials
        {
            get
            {
                var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.TrimStart('|', '-', '.', '"'))
                    .Where(p => p.Length > 0)
                    .ToList();
                if (parts.Count == 0)
                    return "?";
                var letters = parts.Count == 1
                    ? parts[0][..1]
                    : string.Concat(parts[0][0], parts[1][0]);
                return letters.ToUpperInvariant();
            }
        }

        private readonly string? _name;
        private readonly string? _phone;

        private (string Name, string Phone) SplitDisplay()
        {
            var i = DisplayName.LastIndexOf(" · ", StringComparison.Ordinal);
            return i < 0 ? (DisplayName.Trim(), "") : (DisplayName[..i].Trim(), DisplayName[(i + 3)..].Trim());
        }
    }

    public partial class CheckoutViewModel : INotifyPropertyChanged
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

        private readonly IClientsApiService? _clientsApi;
        private readonly ICustomerDisplayService? _customerDisplay;
        private List<ClientOption> _allClients = new();
        private bool _clientsLoaded;
        private bool _isLoadingClients;
        private ClientOption? _selectedClient;
        private string _clientSearchText = "";
        private string _newClientName = "";
        private string _newClientPhone = "";
        private string _newClientAddress = "";
        private bool _isAddingClient;
        private string _debtCashReceived = "0.00";
        private string _mixedCashAmount = "";
        private string _mixedNonCashAmount = "";

        private double _clientLoyaltyBalance;
        private string _pointsToRedeemInput = "0";
        private double _pointsRedeemed;

        public CheckoutViewModel(
            CartTotalsCalculator.CartTotals totals,
            string initialPercent,
            string initialSum,
            IClientsApiService? clientsApi = null,
            ICustomerDisplayService? customerDisplay = null)
        {
            _clientsApi = clientsApi;
            _customerDisplay = customerDisplay;
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
            CancelCommand = new RelayCommand(() =>
            {
                _customerDisplay?.SetSelectedBankQrPath(null);
                RequestClose?.Invoke(false);
            });
            ApplyCashSuggestion1Command = new RelayCommand(() => SetCash(CashSuggestion1), () => HasCashSuggestion1);
            ApplyCashSuggestion2Command = new RelayCommand(() => SetCash(CashSuggestion2), () => HasCashSuggestion2);
            ApplyCashSuggestion3Command = new RelayCommand(() => SetCash(CashSuggestion3), () => HasCashSuggestion3);
            ExactCashCommand = new RelayCommand(SetExactCash, () => IsCashMode);
            ClearCashCommand = new RelayCommand(ClearCash, () => IsCashMode);
            SelectClientCommand = new RelayCommand<ClientOption>(SelectClient);
            ClearClientCommand = new RelayCommand(ClearSelectedClient);
            AddClientCommand = new AsyncRelayCommand(AddClientAsync, CanAddClient);

            LoadBanks();
            RecalculateTotals();
            UpdatePaymentMode();

            // 2026-09-17, по просьбе пользователя ("есть бонусная программа но нет выбора
            // клиента при продаже") — выбор клиента доступен всегда, для любого способа оплаты
            // (наличные/безнал/смешанная/долг), а не только когда включена бонусная программа
            // или продажа "в долг". Список клиентов подгружаем сразу, чтобы поиск в пикере
            // открывался без задержки.
            _ = EnsureClientsLoadedAsync();
        }

        /// <summary>Бонусная программа включена в Настройки → Операции (см. AI-фичи 2026-09-04) —
        /// баланс хранится локально на этой кассе, см. ClientLoyaltyStore.</summary>
        public bool LoyaltyEnabled => UserPreferences.Instance.LoyaltyEnabled;

        /// <summary>Блок выбора клиента в диалоге чекаута — доступен всегда (2026-09-17), чтобы
        /// кассир мог привязать клиента к любой продаже, а не только к долгу или когда включена
        /// бонусная программа. Блок бонусов внутри него по-прежнему скрыт без LoyaltyEnabled
        /// (см. ShowLoyaltyBlock).</summary>
        public bool ShowClientSection => true;

        public bool ShowLoyaltyBlock => LoyaltyEnabled && HasSelectedClient;

        public double ClientLoyaltyBalance => _clientLoyaltyBalance;

        public string ClientLoyaltyBalanceDisplay => IsKyrgyz
            ? $"Бонус: {_clientLoyaltyBalance:0.##} сом"
            : $"Бонусов доступно: {_clientLoyaltyBalance:0.##} сом";

        public string PointsToRedeemInput
        {
            get => _pointsToRedeemInput;
            set
            {
                if (_pointsToRedeemInput == value)
                    return;
                _pointsToRedeemInput = value;
                OnPropertyChanged();
                RecalculateTotals();
            }
        }

        /// <summary>Сколько реально спишется бонусами при текущем чеке — уже обрезано и по
        /// балансу клиента, и по остатку суммы к оплате (нельзя уйти в отрицательный итог).</summary>
        public double PointsRedeemed => _pointsRedeemed;

        /// <summary>Обычная скидка на чек в сомах (без бонусов) — нужна отчётам смены.</summary>
        public double OrderDiscountAmount => _orderDiscountAmount;

        /// <summary>Сколько бонусов начислится клиенту при успешной оплате — считается от
        /// ИТОГОВОЙ суммы к оплате (после всех скидок и списания баллов), не от подытога, чтобы
        /// не начислять бонусы на уже списанные бонусы.</summary>
        public double EarnedPointsPreview => LoyaltyEnabled && HasSelectedClient
            ? Math.Round(_effectiveTotalDue * UserPreferences.Instance.LoyaltyEarnPercent / 100.0, 2)
            : 0;

        public string EarnedPointsPreviewDisplay => IsKyrgyz
            ? $"Ушул чекте берилет: {EarnedPointsPreview:0.##} сом бонус"
            : $"За этот чек начислится: {EarnedPointsPreview:0.##} сом бонусов";

        public double EffectiveTotalDue => _effectiveTotalDue;

        /// <summary>2026-09-15, по просьбе пользователя ("при оплате частично и остальное в долг
        /// должна меняться сумма... там должна быть сумма частичной оплаты", затем уточнено —
        /// это касается именно большой суммы "ИТОГ К ОПЛАТЕ" в диалоге оплаты, не только кнопки
        /// "Оплатить") — при продаже «в долг» кассир реально принимает в кассу только введённую
        /// в "Получено сейчас" сумму (остальное уходит в долг клиента, не в кассу), поэтому "итог
        /// к оплате" должен отражать именно её, а не полную сумму чека.</summary>
        private double EffectivePayableNow =>
            IsDebtMode && ParseNonNegative(_debtCashReceived) is { } debtPaidNow && debtPaidNow > 0.005
                ? debtPaidNow
                : _effectiveTotalDue;

        public string BigTotalDisplay =>
            EffectivePayableNow.ToString("0.00", CultureInfo.InvariantCulture);

        public string SubtotalDisplay =>
            $"{_subtotal.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        public string DiscountSummaryDisplay =>
            $"{_orderDiscountAmount.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        /// <summary>Раньше видимость плашки скидки была завязана на "!!DiscountSummaryDisplay" —
        /// эта строка НИКОГДА не пуста (даже "0.00 сом" без реальной скидки), так что красная
        /// плашка "0.00 сом" висела в диалоге оплаты всегда, даже без скидки — путала кассира,
        /// выглядя как нечто связанное с частичной оплатой в долг.</summary>
        public bool HasDiscount => _orderDiscountAmount > 0.005;

        public string PayableDisplay =>
            $"{EffectivePayableNow.ToString("0.00", CultureInfo.InvariantCulture)} сом";

        private static bool IsKyrgyz => UserPreferences.Instance.Language == AppLanguage.Kyrgyz;

        public string PayButtonText
        {
            get
            {
                var amount = EffectivePayableNow;
                return $"{Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash")} {amount.ToString("0.00", CultureInfo.InvariantCulture)} сом";
            }
        }

        public string ChangeAmountDisplay =>
            _changeAmount.ToString("0.00", CultureInfo.InvariantCulture);

        public string ChangeStatusText =>
            IsCashMode
                ? _isInsufficientCash
                    ? Tr.T("Недостаточно средств", "Каражат жетишсиз", "Insufficient funds", "Yetersiz tutar", "Mablag' yetarli emas")
                    : _changeAmount > 1e-9
                        ? Tr.T("Сдача будет выдана клиенту", "Клиентке кайтарым берилет", "Change will be given to the client", "Para üstü müşteriye verilecek", "Qaytim mijozga beriladi")
                        : Tr.T("Точная сумма", "Так сумма", "Exact amount", "Tam tutar", "Aniq summa")
                : "";

        public bool ShowChangeBlock => IsCashMode;
        public bool ChangeStatusIsOk => IsCashMode && !_isInsufficientCash;
        public bool ChangeStatusIsWarn => IsCashMode && _isInsufficientCash;

        /// <summary>2026-09-17, по просьбе пользователя: подсказки "клиент дал круглую сумму" —
        /// как на сайте (например при чеке на 120 сом снизу предлагаются 200/500/1000), чтобы не
        /// набирать сумму, которую дал покупатель, вручную. Ближайшие 3 круглые суммы не меньше
        /// итога чека, по возрастанию.</summary>
        private static readonly double[] CashSuggestionSteps = { 50, 100, 200, 500, 1000, 2000, 5000, 10000 };

        private double[] CashSuggestionValues =>
            _effectiveTotalDue > 0
                ? CashSuggestionSteps.Where(s => s >= _effectiveTotalDue).Distinct().OrderBy(s => s).Take(3).ToArray()
                : Array.Empty<double>();

        public double CashSuggestion1 => CashSuggestionValues.Length > 0 ? CashSuggestionValues[0] : 0;
        public double CashSuggestion2 => CashSuggestionValues.Length > 1 ? CashSuggestionValues[1] : 0;
        public double CashSuggestion3 => CashSuggestionValues.Length > 2 ? CashSuggestionValues[2] : 0;
        public string CashSuggestion1Display => CashSuggestion1.ToString("0.##", CultureInfo.InvariantCulture);
        public string CashSuggestion2Display => CashSuggestion2.ToString("0.##", CultureInfo.InvariantCulture);
        public string CashSuggestion3Display => CashSuggestion3.ToString("0.##", CultureInfo.InvariantCulture);
        public bool HasCashSuggestion1 => IsCashMode && CashSuggestion1 > 0;
        public bool HasCashSuggestion2 => IsCashMode && CashSuggestion2 > 0;
        public bool HasCashSuggestion3 => IsCashMode && CashSuggestion3 > 0;

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

        public bool IsDebt
        {
            get => _paymentMethod == "debt";
            set { if (value) PaymentMethod = "debt"; }
        }

        public bool IsMixed
        {
            get => _paymentMethod == "mixed";
            set { if (value) PaymentMethod = "mixed"; }
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
        public bool IsBankSelectionVisible => _paymentMethod is "transfer" or "mixed";
        public bool IsDebtMode => _paymentMethod == "debt";
        public bool IsMixedMode => _paymentMethod == "mixed";
        public bool CanPay => CanExecutePay(null);

        /// <summary>Сколько кассир получил сейчас при продаже «в долг» — остальное уходит в долг клиента.
        /// "0.00" по умолчанию (весь долг целиком), но можно указать частичную оплату.</summary>
        public string DebtCashReceived
        {
            get => _debtCashReceived;
            set
            {
                if (_debtCashReceived == value)
                    return;
                _debtCashReceived = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DebtRemainingText));
                OnPropertyChanged(nameof(PayButtonText));
                OnPropertyChanged(nameof(BigTotalDisplay));
                OnPropertyChanged(nameof(PayableDisplay));
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
            }
        }

        public string DebtRemainingText
        {
            get
            {
                var paid = ParseNonNegative(_debtCashReceived) ?? 0;
                var remaining = Math.Max(0, _effectiveTotalDue - paid);
                return $"{Tr.T("Останется в долг", "Карызда калат", "Remains as debt", "Borç olarak kalır", "Qarzga qoladi")}: {remaining.ToString("0.00", CultureInfo.InvariantCulture)} сом";
            }
        }

        public string MixedCashAmount
        {
            get => _mixedCashAmount;
            set
            {
                if (_mixedCashAmount == value)
                    return;
                _mixedCashAmount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MixedRemainingText));
                OnPropertyChanged(nameof(MixedAmountsMatch));
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
                SyncMixedAmount(fromCash: true);
            }
        }

        public string MixedNonCashAmount
        {
            get => _mixedNonCashAmount;
            set
            {
                if (_mixedNonCashAmount == value)
                    return;
                _mixedNonCashAmount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MixedRemainingText));
                OnPropertyChanged(nameof(MixedAmountsMatch));
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();
                SyncMixedAmount(fromCash: false);
            }
        }

        private bool _isSyncingMixedAmounts;

        /// <summary>Наличные и безналичные в смешанной оплате всегда должны давать сумму чека —
        /// правим второе поле сразу при вводе первого, а не оставляем кассиру считать разницу
        /// самому (было: оба поля независимые, кассир должен был сам подгонять остаток).</summary>
        private void SyncMixedAmount(bool fromCash)
        {
            if (_isSyncingMixedAmounts)
                return;

            var source = ParseNonNegative(fromCash ? _mixedCashAmount : _mixedNonCashAmount);
            if (source is not { } value)
                return;

            var other = Math.Max(0, _effectiveTotalDue - value);
            var otherFormatted = other.ToString("0.00", CultureInfo.InvariantCulture);

            _isSyncingMixedAmounts = true;
            try
            {
                if (fromCash)
                    MixedNonCashAmount = otherFormatted;
                else
                    MixedCashAmount = otherFormatted;
            }
            finally
            {
                _isSyncingMixedAmounts = false;
            }
        }

        public string MixedRemainingText
        {
            get
            {
                var cash = ParseNonNegative(_mixedCashAmount) ?? 0;
                var nonCash = ParseNonNegative(_mixedNonCashAmount) ?? 0;
                var remaining = _effectiveTotalDue - cash - nonCash;
                if (Math.Abs(remaining) < 0.005)
                    return Tr.T("Сумма сходится", "Сумма туура келет", "Amount matches", "Tutar uyuşuyor", "Summa mos keladi");
                return remaining > 0
                    ? $"{Tr.T("Не хватает", "Жетишсиз", "Missing", "Eksik", "Yetishmaydi")}: {remaining.ToString("0.00", CultureInfo.InvariantCulture)} сом"
                    : $"{Tr.T("Лишнее", "Ашык", "Excess", "Fazla", "Ortiqcha")}: {(-remaining).ToString("0.00", CultureInfo.InvariantCulture)} сом";
            }
        }

        public bool MixedAmountsMatch
        {
            get
            {
                var cash = ParseNonNegative(_mixedCashAmount);
                var nonCash = ParseNonNegative(_mixedNonCashAmount);
                if (cash is not { } c || nonCash is not { } n)
                    return false;
                return Math.Abs(c + n - _effectiveTotalDue) < 0.005;
            }
        }

        public bool IsLoadingClients
        {
            get => _isLoadingClients;
            private set { _isLoadingClients = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ClientOption> ClientSearchResults { get; } = new();

        public ClientOption? SelectedClient
        {
            get => _selectedClient;
            private set
            {
                _selectedClient = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedClientName));
                OnPropertyChanged(nameof(HasSelectedClient));
                OnPropertyChanged(nameof(ShowLoyaltyBlock));
                ErrorMessage = "";
                RaiseCommandsCanExecuteChanged();

                _clientLoyaltyBalance = value != null ? ClientLoyaltyStore.GetBalance(value.Id) : 0;
                _pointsToRedeemInput = "0";
                OnPropertyChanged(nameof(ClientLoyaltyBalance));
                OnPropertyChanged(nameof(ClientLoyaltyBalanceDisplay));
                OnPropertyChanged(nameof(PointsToRedeemInput));
                RecalculateTotals();
            }
        }

        public bool HasSelectedClient => _selectedClient != null;
        public string? ClientId => _selectedClient?.Id;

        public string SelectedClientName =>
            _selectedClient?.DisplayName ?? Tr.T("Клиент не выбран", "Клиент тандалган жок", "No client selected", "Müşteri seçilmedi", "Mijoz tanlanmagan");

        public string ClientSearchText
        {
            get => _clientSearchText;
            set
            {
                if (_clientSearchText == value)
                    return;
                _clientSearchText = value;
                OnPropertyChanged();
                ApplyClientFilter();
            }
        }

        public string NewClientName
        {
            get => _newClientName;
            set
            {
                _newClientName = value;
                OnPropertyChanged();
                (AddClientCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Адрес нового клиента — необязательное поле. Заполняется прежде всего при
        /// продаже в долг: телефон покупателя может не отвечать, и тогда адрес — единственное,
        /// по чему его можно найти. На пустом значении поведение прежнее.</summary>
        public string NewClientAddress
        {
            get => _newClientAddress;
            set
            {
                _newClientAddress = value ?? "";
                OnPropertyChanged();
            }
        }

        public string NewClientPhone
        {
            get => _newClientPhone;
            set
            {
                _newClientPhone = value;
                OnPropertyChanged();
                (AddClientCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsAddingClient
        {
            get => _isAddingClient;
            private set { _isAddingClient = value; OnPropertyChanged(); }
        }

        public ICommand SelectClientCommand { get; private set; } = null!;
        public ICommand ClearClientCommand { get; private set; } = null!;
        public ICommand AddClientCommand { get; private set; } = null!;

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
                // 2026-09-21: раньше экран покупателя не знал о выборе банка в этом диалоге вовсе
                // и всегда показывал первый попавшийся загруженный QR — независимо от того, какой
                // банк реально выбрал кассир (жалоба: "QR не меняется смотря на банк").
                _customerDisplay?.SetSelectedBankQrPath(QrCodePath);
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
        public ICommand ApplyCashSuggestion1Command { get; }
        public ICommand ApplyCashSuggestion2Command { get; }
        public ICommand ApplyCashSuggestion3Command { get; }
        public ICommand ExactCashCommand { get; }
        public ICommand ClearCashCommand { get; }

        public event Action<bool>? RequestClose;

        /// <summary>Сумма, которую нужно передать как cash_received при чекауте — зависит от
        /// выбранного способа оплаты (для "долга" это частичная оплата, для "смешанной" — наличная часть).</summary>
        public string CashReceivedForApi => PaymentMethod switch
        {
            "cash" => CheckoutValidation.NormalizeDecimal(CashReceived) is { Length: > 0 } n
                ? n
                : _effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture),
            "debt" => (ParseNonNegative(_debtCashReceived) ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
            "mixed" => (ParseNonNegative(_mixedCashAmount) ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
            _ => "0.00",
        };

        /// <summary>Безналичная часть смешанной оплаты — null для остальных способов оплаты.</summary>
        public string? NonCashReceivedForApi =>
            PaymentMethod == "mixed"
                ? (ParseNonNegative(_mixedNonCashAmount) ?? 0).ToString("0.00", CultureInfo.InvariantCulture)
                : null;

        public Dictionary<string, string>? PendingOrderDiscountBody
        {
            get
            {
                // Бонусы клиента — дополнительная скидка, о которой сервер сам не знает (в API
                // клиентов NurCRM нет понятия баллов) — поэтому когда баллы списаны, отправляем
                // ВЕСЬ совокупный размер скидки (обычная + бонусы) одной суммой. Без этого сервер
                // посчитал бы итог чека больше денег, которые реально забрали у покупателя.
                // 2026-09-21, живой баг ("оплата не прошла... проверьте параметры скидки"):
                // раньше здесь ДОПОЛНИТЕЛЬНО отправлялся "order_discount_percent": "0" — сервер
                // требует РОВНО ОДНО из двух полей в теле запроса, а не "оба, но одно нулевое"
                // (подтверждено живым ответом: non_field_errors "Выберите либо фиксированную
                // скидку, либо скидку в процентах"). Присутствие второго ключа — уже ошибка,
                // даже если его значение "0".
                if (_pointsRedeemed > 1e-9)
                {
                    // Бонусы — это ДЕНЬГИ, а не процент: покупателю обещано «минус 8 сом», а не
                    // «минус 5 %». Процентом их отправлять нельзя ещё и потому, что на сервере
                    // стоит потолок скидки (max_discount_percent, у этой компании 10 %): бонус
                    // в 10 сом с чека на 39 сом — это 25,64 %, и сервер отказывал совершенно
                    // законно, хотя речь шла о тех же десяти сомах.
                    return new Dictionary<string, string>
                    {
                        ["order_discount_total"] = (_orderDiscountAmount + _pointsRedeemed)
                            .ToString("0.00", CultureInfo.InvariantCulture),
                    };
                }

                if (!_discountDirty)
                    return null;

                // BuildPatchBody/BuildClearPatchBody (OrderDiscountHelper) уже гарантируют не
                // больше одного ключа за раз — раньше здесь ДОПОЛНИТЕЛЬНО дописывался
                // противоположный ключ = "0" (та же ошибка, что и у бонусов выше), сводя на нет
                // их же собственную защиту.
                var body = OrderDiscountHelper.BuildPatchBody(_isDiscountPercent, _discountInput);
                if (body.Count > 0)
                    return body;

                // Скидка снята: гасим только то поле, которое реально было выставлено раньше.
                var clear = OrderDiscountHelper.BuildClearPatchBody(_initialPercent, _initialSum);
                return clear.Count == 0 ? null : clear;
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
            var afterDiscount = Math.Max(0, _subtotal - _lineDiscounts - _orderDiscountAmount);
            _pointsRedeemed = ComputePointsRedeemed(afterDiscount);
            _effectiveTotalDue = Math.Max(0, afterDiscount - _pointsRedeemed);
            _discountDirty = IsDiscountChangedFromInitial();

            OnPropertyChanged(nameof(BigTotalDisplay));
            OnPropertyChanged(nameof(SubtotalDisplay));
            OnPropertyChanged(nameof(DiscountSummaryDisplay));
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(DiscountAmountPreview));
            OnPropertyChanged(nameof(PayableDisplay));
            OnPropertyChanged(nameof(PayButtonText));
            OnPropertyChanged(nameof(DebtRemainingText));
            OnPropertyChanged(nameof(MixedRemainingText));
            OnPropertyChanged(nameof(PointsRedeemed));
            OnPropertyChanged(nameof(EarnedPointsPreview));
            OnPropertyChanged(nameof(EarnedPointsPreviewDisplay));

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

        /// <summary>Сколько бонусов реально спишется — не больше баланса клиента и не больше
        /// суммы, оставшейся к оплате после обычной скидки (иначе итог ушёл бы в минус).</summary>
        private double ComputePointsRedeemed(double amountAfterDiscount)
        {
            if (!LoyaltyEnabled || !HasSelectedClient)
                return 0;

            var normalized = OrderDiscountHelper.NormalizeDecimal(_pointsToRedeemInput);
            if (string.IsNullOrWhiteSpace(normalized)
                || !double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var requested)
                || requested <= 0)
                return 0;

            return Math.Min(Math.Min(requested, _clientLoyaltyBalance), amountAfterDiscount);
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
            OnPropertyChanged(nameof(CashSuggestion1));
            OnPropertyChanged(nameof(CashSuggestion2));
            OnPropertyChanged(nameof(CashSuggestion3));
            OnPropertyChanged(nameof(CashSuggestion1Display));
            OnPropertyChanged(nameof(CashSuggestion2Display));
            OnPropertyChanged(nameof(CashSuggestion3Display));
            OnPropertyChanged(nameof(HasCashSuggestion1));
            OnPropertyChanged(nameof(HasCashSuggestion2));
            OnPropertyChanged(nameof(HasCashSuggestion3));
            OnPropertyChanged(nameof(ConsultantCommissionPreview));
            (PayCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCashSuggestion1Command as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCashSuggestion2Command as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCashSuggestion3Command as RelayCommand)?.RaiseCanExecuteChanged();
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

        private static double? ParseNonNegative(string? raw)
        {
            var normalized = CheckoutValidation.NormalizeDecimal(raw ?? "");
            if (string.IsNullOrEmpty(normalized))
                return null;
            return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
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

            if (PaymentMethod == "debt")
            {
                if (SelectedClient == null)
                    return false;
                var paid = ParseNonNegative(_debtCashReceived);
                return paid is { } p && p <= _effectiveTotalDue + 1e-9;
            }

            // 2026-09-16, по просьбе пользователя ("сделай возможной оплату онлайн без
            // обязательного QR") — раньше банк без загруженного QR-кода полностью блокировал
            // безналичную/смешанную оплату (кассир не мог принять оплату, даже подтвердив её
            // другим способом, просто потому что в настройках банка не приложена картинка QR).
            // QR остаётся опциональным удобством (показывается, если есть — см. QrCodePath), но
            // больше не обязателен для завершения продажи.
            if (PaymentMethod == "mixed")
                return MixedAmountsMatch && SelectedBank != null;

            return SelectedBank != null;
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
            else if (PaymentMethod == "debt")
            {
                if (SelectedClient == null)
                {
                    ErrorMessage = IsKyrgyz
                        ? "Карыз сатуу үчүн клиентти тандаңыз."
                        : "Для продажи в долг выберите клиента.";
                    return;
                }

                var paid = ParseNonNegative(_debtCashReceived);
                if (paid == null)
                {
                    ErrorMessage = IsKyrgyz
                        ? "Учурда алынган суманы көрсөтүңүз (0 болушу мүмкүн)."
                        : "Укажите сумму, полученную сейчас (может быть 0).";
                    return;
                }

                if (paid.Value > _effectiveTotalDue + 1e-9)
                {
                    ErrorMessage = IsKyrgyz
                        ? "Учурда алынган сумма жалпы сумманан ашпашы керек."
                        : "Полученная сумма не может превышать итог чека.";
                    return;
                }
            }
            else if (PaymentMethod == "mixed")
            {
                if (!MixedAmountsMatch)
                {
                    ErrorMessage = IsKyrgyz
                        ? "Накталай жана накталай эмес суммалардын жыйындысы жалпы суммага барабар болушу керек."
                        : "Сумма наличными и безналичными должна совпадать с итогом чека.";
                    return;
                }

                if (SelectedBank == null)
                {
                    ErrorMessage = IsKyrgyz ? "Банкты тандаңыз." : "Выберите банк для безналичной части.";
                    return;
                }
            }
            else
            {
                if (SelectedBank == null)
                {
                    ErrorMessage = Tr.T("Выберите банк для безналичной оплаты.",
                        "Накталай эмес төлөм үчүн банкты тандаңыз.",
                        "Select a bank for the non-cash payment.",
                        "Nakit dışı ödeme için banka seçin.",
                        "Naqd pulsiz to'lov uchun bankni tanlang.");
                    return;
                }
            }

            var consultantError = ValidateConsultant();
            if (consultantError != null)
            {
                ErrorMessage = consultantError;
                return;
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

            // 2026-09-23. Потолок скидки компании (max_discount_percent) проверялся только в
            // корзине. Окно оплаты о нём не знало, и кассир ставил здесь любую скидку в обход
            // ограничения владельца. Сумма приводится к процентам, иначе ограничение обходилось
            // бы переключением «процент → сумма».
            if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var entered)
                && MaxDiscountGate.Exceeds(entered, _isDiscountPercent, Math.Max(0, _subtotal - _lineDiscounts))
                && MaxDiscountGate.Limit is { } limit)
            {
                return Tr.T(
                    $"Скидка не может превышать {limit:0.##}% — таково ограничение для сотрудников.",
                    $"Арзандатуу {limit:0.##}%дан ашпашы керек — бул кызматкерлер үчүн чектөө.",
                    $"The discount can't exceed {limit:0.##}% — that's the limit set for employees.",
                    $"İndirim %{limit:0.##}'i geçemez — personel için belirlenen sınır budur.",
                    $"Chegirma {limit:0.##}%dan oshmasligi kerak — bu xodimlar uchun belgilangan chegara.");
            }

            return null;
        }

        private void UpdatePaymentMode()
        {
            OnPropertyChanged(nameof(IsCash));
            OnPropertyChanged(nameof(IsTransfer));
            OnPropertyChanged(nameof(IsDebt));
            OnPropertyChanged(nameof(IsMixed));
            OnPropertyChanged(nameof(IsCashMode));
            OnPropertyChanged(nameof(IsBankSelectionVisible));
            OnPropertyChanged(nameof(IsDebtMode));
            OnPropertyChanged(nameof(IsMixedMode));
            OnPropertyChanged(nameof(ShowClientSection));
            OnPropertyChanged(nameof(PayButtonText));
            OnPropertyChanged(nameof(BigTotalDisplay));
            OnPropertyChanged(nameof(PayableDisplay));
            UpdateCashState();

            if (IsDebtMode)
                _ = EnsureClientsLoadedAsync();

            if (IsMixedMode && string.IsNullOrWhiteSpace(_mixedCashAmount) && string.IsNullOrWhiteSpace(_mixedNonCashAmount))
            {
                // Разумные значения по умолчанию: всё наличными, кассир перераспределяет сам.
                MixedCashAmount = _effectiveTotalDue.ToString("0.00", CultureInfo.InvariantCulture);
                MixedNonCashAmount = "0.00";
            }
        }

        private void LoadBanks()
        {
            var prefs = UserPreferences.Instance;
            prefs.BankQrPaths ??= new Dictionary<string, string>();

            // 2026-09-23, живой баг («при выборе безнал не видны остальные банки»). Причин было
            // две, и обе здесь.
            //
            // Первая: список был жёстко задан тремя строками, хотя в Кыргызстане по реестру
            // НБКР работают 26 коммерческих банков. Кассир физически не мог выбрать свой банк.
            //
            // Вторая: банки, которые владелец добавляет сам в «Настройки → Операции →
            // Добавить банк», складываются в prefs.CustomBankNames, и окно оплаты о них не
            // знало вовсе — банк заводился, QR к нему загружался, а выбрать его было нельзя.
            //
            // Названия встроенных банков менять НЕЛЬЗЯ: prefs.BankQrPaths хранит загруженные
            // QR-коды по имени банка как по ключу, и переименование потеряет уже настроенные QR.
            // Показываем только то, что владелец отметил в «Настройки → Операции». Банков в
            // Кыргызстане больше двух десятков, и вываливать их все кассиру в очереди нельзя.
            // Пустой список означает «не выбирал» — тогда те же три банка, что и до обновления.
            var visible = prefs.VisibleBankNames is { Count: > 0 }
                ? prefs.VisibleBankNames
                : KyrgyzBanks.DefaultVisible.ToList();

            var bankNames = visible
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Банки с загруженным QR — наверх. Их у магазина обычно один-два, и заставлять
            // кассира искать свой банк среди двух с половиной десятков в очереди не стоит.
            bankNames = bankNames
                .OrderByDescending(name => prefs.BankQrPaths.ContainsKey(name))
                .ToList();

            // Логотипы есть только у встроенных: файлы лежат в Assets и взяты из официальных
            // материалов самих банков. Для остальных путь остаётся пустым — подставлять
            // несуществующий нельзя, конвертер молча вернёт null и следа в журнале не оставит.
            var logoNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Элкарт", "Elkart-logo.png" },
                { "MBank", "Mbank-logo.png" },
                { "ФинкаБанк", "Finca-logo.png" },
            };

            if (bankNames.Count == 0)
                bankNames = KyrgyzBanks.DefaultVisible.ToList();

            var list = new ObservableCollection<BankAccount>();
            foreach (var name in bankNames)
            {
                var qrPath = prefs.BankQrPaths.TryGetValue(name, out var path) ? path : "";
                var logoPath = logoNames.TryGetValue(name, out var logo)
                    ? $"avares://NurMarketKassa.Avalonia/Assets/{logo}"
                    : "";

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

        private void SelectClient(ClientOption? client)
        {
            if (client == null)
                return;
            SelectedClient = client;
            ClientSearchText = "";
        }

        /// <summary>Отменяет выбор клиента, если он выбран по ошибке (2026-09-05, по просьбе
        /// пользователя: "если по ошибке выбрали клиента добавь возможность отмены") — раньше
        /// после выбора отменить было нельзя вообще, единственный публичный путь менять
        /// SelectedClient был SelectClient(), который явно игнорирует null.</summary>
        private void ClearSelectedClient() => SelectedClient = null;

        private bool CanAddClient() =>
            !_isAddingClient
            && _clientsApi != null
            && !OfflineModeHelper.UseLocalOperations
            && !string.IsNullOrWhiteSpace(_newClientName)
            && !string.IsNullOrWhiteSpace(_newClientPhone);

        private async Task AddClientAsync()
        {
            if (_clientsApi == null)
                return;

            // 2026-09-10: клиенты — вне охвата автономного/офлайн режима (нет сервера, на
            // который заводить реального клиента). Без этой проверки CreateClientAsync падал с
            // сырым серверным "Сессия недействительна…", который кассир видел прямо в диалоге.
            if (OfflineModeHelper.UseLocalOperations)
            {
                ErrorMessage = IsKyrgyz
                    ? "Клиенттер офлайн жеткиликсиз."
                    : "Клиенты недоступны офлайн.";
                return;
            }

            IsAddingClient = true;
            try
            {
                var created = await _clientsApi
                    .CreateClientAsync(_newClientName, _newClientPhone, null, _newClientAddress)
                    .ConfigureAwait(true);

                var option = ToClientOption(created);
                if (string.IsNullOrWhiteSpace(option.Id))
                {
                    ErrorMessage = IsKyrgyz
                        ? "Клиентти кошуу мүмкүн болгон жок."
                        : "Не удалось добавить клиента.";
                    return;
                }

                _allClients.Insert(0, option);
                SelectClient(option);
                NewClientName = "";
                NewClientPhone = "";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
                PosLogger.Log($"Checkout add client failed: {ex}", "PAYMENT");
            }
            catch (Exception ex)
            {
                ErrorMessage = IsKyrgyz
                    ? "Клиентти кошуу мүмкүн болгон жок."
                    : "Не удалось добавить клиента.";
                PosLogger.Log($"Checkout add client failed: {ex}", "PAYMENT");
            }
            finally
            {
                IsAddingClient = false;
            }
        }

        private async Task EnsureClientsLoadedAsync()
        {
            if (_clientsLoaded || _isLoadingClients || _clientsApi == null)
                return;

            if (OfflineModeHelper.UseLocalOperations)
            {
                ErrorMessage = IsKyrgyz
                    ? "Клиенттер офлайн жеткиликсиз."
                    : "Клиенты недоступны офлайн.";
                return;
            }

            IsLoadingClients = true;
            try
            {
                var raw = await _clientsApi.GetClientsAsync(null).ConfigureAwait(true);
                _allClients = raw
                    // "type" различает реальных клиентов и поставщиков/подрядчиков в той же
                    // таблице NurCRM — для продажи в долг нужны только настоящие клиенты.
                    .Where(el => TryGetString(el, "type") is null or "client")
                    .Select(ToClientOption)
                    .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                    .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                _clientsLoaded = true;
                ApplyClientFilter();
            }
            catch (Exception ex)
            {
                ErrorMessage = IsKyrgyz
                    ? "Клиенттерди жүктөө мүмкүн болгон жок."
                    : "Не удалось загрузить список клиентов.";
                PosLogger.Log($"Checkout clients load failed: {ex}", "PAYMENT");
            }
            finally
            {
                IsLoadingClients = false;
            }
        }

        private void ApplyClientFilter()
        {
            ClientSearchResults.Clear();
            var query = _clientSearchText?.Trim() ?? "";
            IEnumerable<ClientOption> source = _allClients;
            if (query.Length > 0)
                source = source.Where(c => c.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));

            foreach (var client in source.Take(30))
                ClientSearchResults.Add(client);
        }

        private static ClientOption ToClientOption(JsonElement element)
        {
            var id = TryGetString(element, "id") ?? "";
            var name = TryGetString(element, "full_name") ?? "";
            var phone = TryGetString(element, "phone") ?? "";
            var display = string.IsNullOrWhiteSpace(phone) ? name : $"{name} · {phone}";
            return new ClientOption { Id = id, DisplayName = display, Name = name, Phone = phone };
        }

        private static string? TryGetString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            if (!element.TryGetProperty(property, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
