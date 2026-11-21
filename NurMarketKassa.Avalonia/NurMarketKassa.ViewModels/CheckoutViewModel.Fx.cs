using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels
{
    /// <summary>Кнопка-валюта в окне оплаты («$ USD», «₽ RUB», «¥ CNY»): выбранная подсвечена.</summary>
    public sealed class FxChipVm : INotifyPropertyChanged
    {
        private bool _isSelected;

        public FxChipVm(string code, string symbol, decimal rate)
        {
            Code = code;
            Symbol = symbol;
            Rate = rate;
        }

        public string Code { get; }
        public string Symbol { get; }
        public decimal Rate { get; }
        public string Label => $"{Symbol} {Code}";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // 2026-11-21 (сфера «Базар», модуль currencies; ТЗ Контейнер-базар 2.6, ответ сервера 20.11 В.19): «Принято в валюте» в окне оплаты наличными.
    // Кассир выбирает валюту (USD / RUB / CNY — только те, у которых есть курс компании), вводит сумму в валюте — поле «Получено» (в сомах) заполняется суммой по курсу,
    // дальше работает прежняя сдача в сомах. Деньги окна не затронуты: CashReceived по-прежнему сомы; валюта уходит в снимок чека (fx_payment) и в payment.payments[] тела продажи.
    public partial class CheckoutViewModel
    {
        private ObservableCollection<FxChipVm>? _fxChips;
        private ICommand? _selectFxCommand;
        private string? _fxCurrency;
        private string _fxAmount = "";
        private string _fxSomExtra = "";

        /// <summary>Валюты, которые можно принять: модуль «Валюты» включён и у валюты есть курс. Пусто — блока в окне нет.</summary>
        public ObservableCollection<FxChipVm> FxChips => _fxChips ??= BuildFxChips();

        public bool HasFxChips => FxChips.Count > 0;

        private static ObservableCollection<FxChipVm> BuildFxChips()
        {
            var list = new ObservableCollection<FxChipVm>();
            if (!BazaarModules.CurrenciesOn)
                return list;
            var rates = BazaarCurrencies.Current;
            foreach (var code in BazaarCurrencies.Accepted(rates))
                list.Add(new FxChipVm(code, Symbol(code), rates.Rates[code]));
            return list;
        }

        private static string Symbol(string code) => code switch { "USD" => "$", "EUR" => "€", "RUB" => "₽", "CNY" => "¥", "KZT" => "₸", "TRY" => "₺", _ => code };

        public ICommand SelectFxCommand => _selectFxCommand ??= new RelayCommand<string>(SelectFx);

        public string FxTitle => Tr.T("Принято в валюте", "Валютада алынды", "Received in currency", "Dövizle alınan", "Valyutada qabul qilindi");
        public string FxAmountLabel => Tr.T("Сумма в валюте", "Валютадагы сумма", "Amount in currency", "Döviz tutarı", "Valyutadagi summa");
        public string FxSomLabel => Tr.T("Добавить сомами", "Сом кошуу", "Add in som", "Somla ekle", "So'm qo'shish");

        /// <summary>Выбранная валюта; null — оплата в сомах.</summary>
        public string? FxCurrency => _fxCurrency;

        public bool ShowFxInput => _fxCurrency is not null;

        public string FxAmount
        {
            get => _fxAmount;
            set
            {
                if (_fxAmount == value)
                    return;
                _fxAmount = value;
                OnPropertyChanged();
                ApplyFx();
            }
        }

        public string FxSomExtra
        {
            get => _fxSomExtra;
            set
            {
                if (_fxSomExtra == value)
                    return;
                _fxSomExtra = value;
                OnPropertyChanged();
                ApplyFx();
            }
        }

        private void SelectFx(string? code)
        {
            code = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
            if (code is not null && _fxCurrency == code)
                code = null; // повторное нажатие — снова сомы
            _fxCurrency = code;
            foreach (var chip in FxChips)
                chip.IsSelected = chip.Code == code;
            _fxAmount = "";
            _fxSomExtra = "";
            OnPropertyChanged(nameof(FxCurrency));
            OnPropertyChanged(nameof(ShowFxInput));
            OnPropertyChanged(nameof(FxAmount));
            OnPropertyChanged(nameof(FxSomExtra));
            OnPropertyChanged(nameof(FxEquivalentText));
            if (code is null)
                CashReceived = ShowDec(_effectiveTotalDue); // назад к сомам: «Получено» = итог, как при открытии окна
        }

        /// <summary>Строка под полем: «= 1 750,00 сом по курсу 87,5» или подсказка, что вводить.</summary>
        public string FxEquivalentText
        {
            get
            {
                if (_fxCurrency is null)
                    return "";
                var result = BazaarCurrencies.TryBuild(BazaarCurrencies.Current, _fxCurrency, _fxAmount, _fxSomExtra, out var fx);
                return result switch
                {
                    FxInputResult.Ok => $"= {MoneyText.Amount(fx!.TotalSom)} {MoneyUnitText} · " + Tr.T("курс", "курс", "rate", "kur", "kurs") + $" {fx.Rate.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))}",
                    FxInputResult.NoRate => Tr.T("Курса нет — укажите курс в окне «Курсы валют»", "Курс жок — «Валюта курстары» терезесинде көрсөтүңүз",
                        "No rate — set it in the “Exchange rates” window", "Kur yok — «Döviz kurları» penceresinde girin", "Kurs yo'q — «Valyuta kurslari» oynasida kiriting"),
                    _ => Tr.T("Введите сумму в валюте", "Валютадагы сумманы жазыңыз", "Enter the amount in currency", "Döviz tutarını girin", "Valyutadagi summani kiriting"),
                };
            }
        }

        private void ApplyFx()
        {
            OnPropertyChanged(nameof(FxEquivalentText));
            if (_fxCurrency is null)
                return;
            if (BazaarCurrencies.TryBuild(BazaarCurrencies.Current, _fxCurrency, _fxAmount, _fxSomExtra, out var fx) == FxInputResult.Ok && fx is not null)
                CashReceived = ShowDec((double)fx.TotalSom); // «Получено» в сомах — дальше прежняя проверка и сдача
            else
                CashReceived = "";
        }

        /// <summary>Принятая валюта для оплаты. null — оплата в сомах (валюта не выбрана / не наличные / «Получено» правили вручную и сумма уже не сходится).</summary>
        public FxPayment? FxPaymentForApi
        {
            get
            {
                if (_fxCurrency is null || !IsCashMode)
                    return null;
                if (BazaarCurrencies.TryBuild(BazaarCurrencies.Current, _fxCurrency, _fxAmount, _fxSomExtra, out var fx) != FxInputResult.Ok || fx is null)
                    return null;
                return CheckoutValidation.TryParseAmount(CashReceived, out var cash) == CheckoutValidation.AmountParse.Ok && Math.Abs((decimal)cash - fx.TotalSom) <= 0.01m ? fx : null;
            }
        }
    }
}
