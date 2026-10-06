using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;

namespace NurMarketKassa.ViewModels
{
    /// <summary>Строка таблицы платежей рассрочки в окне оплаты.</summary>
    public sealed class DebtInstallmentRow
    {
        public DebtInstallmentRow(string title, string date, string amount, bool isFinal)
        {
            Title = title;
            Date = date;
            Amount = amount;
            IsFinal = isFinal;
        }

        public string Title { get; }
        public string Date { get; }
        public string Amount { get; }
        public bool IsFinal { get; }
    }

    /// <summary>2026-10-06, владелец: «долг со сроком; срок возврата — тот, что указал клиент»; «в вебе есть оплата одним днём
    /// и рассрочка на 7 и т. д., месяцы и год — сделай так же, но для рассрочки должна быть галочка». При оплате «В долг»:
    /// • по умолчанию — одним платежом: дата, до которой клиент вернёт долг (или «через 7 / 14 / 30 дней»), обязательна;
    /// • галочка «Рассрочка» — как «Отсрочка» сайта: по дням (1–90 платежей, каждый день / через день / через 3 дня / раз
    ///   в неделю / каждые N дней) или по месяцам (1–24 платежа, каждые N месяцев), дата первого платежа, таблица платежей.
    ///   Сумма делится поровну, остаток копеек — в последний платёж (так же делит сервер).
    /// График уходит на сервер после продажи (DebtDueDateSync) и печатается в чеке.</summary>
    public partial class CheckoutViewModel
    {
        private DateTime? _debtDueDate;
        private bool _isInstallment;
        private bool _installmentByMonths;
        private int _installmentCount = 2;
        private int _installmentInterval = 7;
        private DateTime? _installmentFirstDate;
        private ICommand? _setDebtDueDaysCommand;
        private ICommand? _setInstallmentUnitCommand;
        private ICommand? _setInstallmentCountCommand;
        private ICommand? _setInstallmentIntervalCommand;

        /// <summary>До какого числа клиент вернёт долг (одним платежом).</summary>
        public DateTime? DebtDueDate
        {
            get => _debtDueDate;
            set
            {
                var date = value?.Date;
                if (_debtDueDate == date)
                    return;
                _debtDueDate = date;
                OnPropertyChanged();
                RaiseDebtScheduleChanged();
            }
        }

        /// <summary>«Через N дней» — параметр строкой ("7", "14", "30").</summary>
        public ICommand SetDebtDueDaysCommand => _setDebtDueDaysCommand ??= new RelayCommand<string>(days =>
        {
            if (int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                DebtDueDate = DateTime.Today.AddDays(n);
        });

        /// <summary>Галочка «Рассрочка (несколько платежей)».</summary>
        public bool IsInstallment
        {
            get => _isInstallment;
            set
            {
                if (_isInstallment == value)
                    return;
                _isInstallment = value;
                if (value && _installmentFirstDate is null)
                    _installmentFirstDate = DefaultFirstDate();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSinglePayment));
                OnPropertyChanged(nameof(InstallmentFirstDate));
                RaiseDebtScheduleChanged();
            }
        }

        public bool IsSinglePayment => !_isInstallment;

        public bool IsInstallmentByDays
        {
            get => !_installmentByMonths;
            set { if (value) ApplyInstallmentUnit(false); }
        }

        public bool IsInstallmentByMonths
        {
            get => _installmentByMonths;
            set { if (value) ApplyInstallmentUnit(true); }
        }

        /// <summary>"day" — по дням, "month" — по месяцам.</summary>
        public ICommand SetInstallmentUnitCommand => _setInstallmentUnitCommand ??= new RelayCommand<string>(unit =>
            ApplyInstallmentUnit(string.Equals(unit, "month", StringComparison.OrdinalIgnoreCase)));

        private void ApplyInstallmentUnit(bool months)
        {
            if (months == _installmentByMonths)
                return;
            _installmentByMonths = months;
            // Как на сайте: по дням — раз в неделю, по месяцам — каждый месяц; первый платёж — через день / через месяц.
            _installmentInterval = months ? 1 : 7;
            _installmentCount = Math.Clamp(_installmentCount, 1, MaxCount);
            _installmentFirstDate = DefaultFirstDate();
            OnPropertyChanged(nameof(IsInstallmentByDays));
            OnPropertyChanged(nameof(IsInstallmentByMonths));
            OnPropertyChanged(nameof(InstallmentFirstDate));
            OnPropertyChanged(nameof(InstallmentCountText));
            OnPropertyChanged(nameof(InstallmentIntervalText));
            RaiseDebtScheduleChanged();
        }

        public ICommand SetInstallmentCountCommand => _setInstallmentCountCommand ??= new RelayCommand<string>(text =>
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                InstallmentCountText = n.ToString(CultureInfo.InvariantCulture);
        });

        public ICommand SetInstallmentIntervalCommand => _setInstallmentIntervalCommand ??= new RelayCommand<string>(text =>
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                InstallmentIntervalText = n.ToString(CultureInfo.InvariantCulture);
        });

        private int MaxCount => _installmentByMonths ? 24 : 90;
        private int MaxInterval => _installmentByMonths ? 12 : 30;

        /// <summary>Число платежей (по дням 1–90, по месяцам 1–24).</summary>
        public string InstallmentCountText
        {
            get => _installmentCount.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (!int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return;
                n = Math.Clamp(n, 1, MaxCount);
                if (n == _installmentCount)
                    return;
                _installmentCount = n;
                OnPropertyChanged();
                RaiseDebtScheduleChanged();
            }
        }

        /// <summary>Через сколько дней (1–30) или месяцев (1–12) следующий платёж.</summary>
        public string InstallmentIntervalText
        {
            get => _installmentInterval.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (!int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return;
                n = Math.Clamp(n, 1, MaxInterval);
                if (n == _installmentInterval)
                    return;
                _installmentInterval = n;
                OnPropertyChanged();
                RaiseDebtScheduleChanged();
            }
        }

        public DateTime? InstallmentFirstDate
        {
            get => _installmentFirstDate;
            set
            {
                var date = value?.Date;
                if (_installmentFirstDate == date)
                    return;
                _installmentFirstDate = date;
                OnPropertyChanged();
                RaiseDebtScheduleChanged();
            }
        }

        private DateTime DefaultFirstDate() => _installmentByMonths ? DateTime.Today.AddMonths(1) : DateTime.Today.AddDays(1);

        // ── надписи (5 языков)

        public string DebtDueLabel => Tr.T("Клиент вернёт долг до", "Кардар карызды качанга чейин кайтарат", "The client will repay by",
            "Müşteri borcu şu tarihe kadar ödeyecek", "Mijoz qarzni qaytaradigan sana");
        /// <summary>Подсказка в пустом поле даты (иначе календарь показывает «dd.MM.yyyy»).</summary>
        public string DebtDateWatermark => Tr.T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy");
        public string DebtDue7Text => Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun");
        public string DebtDue14Text => Tr.T("14 дней", "14 күн", "14 days", "14 gün", "14 kun");
        public string DebtDue30Text => Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun");
        public string InstallmentCheckText => Tr.T("Рассрочка (несколько платежей)", "Бөлүп төлөө (бир нече төлөм)", "Instalments (several payments)",
            "Taksit (birden fazla ödeme)", "Bo'lib to'lash (bir nechta to'lov)");
        public string InstallmentByDaysText => Tr.T("По дням", "Күн боюнча", "By days", "Günlük", "Kunlar bo'yicha");
        public string InstallmentByMonthsText => Tr.T("По месяцам", "Ай боюнча", "By months", "Aylık", "Oylar bo'yicha");
        public string InstallmentCountLabel => Tr.T("Платежей", "Төлөмдөр", "Payments", "Ödeme sayısı", "To'lovlar soni");
        public string InstallmentIntervalLabel => _installmentByMonths
            ? Tr.T("Каждые … месяцев", "Ар … айда", "Every … months", "Her … ayda", "Har … oyda")
            : Tr.T("Каждые … дней", "Ар … күндө", "Every … days", "Her … günde", "Har … kunda");
        public string InstallmentFirstDateLabel => Tr.T("Дата первого платежа", "Биринчи төлөмдүн датасы", "First payment date", "İlk ödeme tarihi", "Birinchi to'lov sanasi");
        public string IntervalEveryDayText => Tr.T("Каждый день", "Күн сайын", "Every day", "Her gün", "Har kuni");
        public string IntervalEveryOtherDayText => Tr.T("Через день", "Күн аралап", "Every other day", "Gün aşırı", "Kun ora");
        public string IntervalEvery3DaysText => Tr.T("Через 3 дня", "3 күндө", "Every 3 days", "3 günde bir", "3 kunda");
        public string IntervalWeeklyText => Tr.T("Раз в неделю", "Жумасына бир", "Once a week", "Haftada bir", "Haftada bir");
        public string IntervalMonthlyText => Tr.T("Каждый месяц", "Ай сайын", "Every month", "Her ay", "Har oy");
        public string IntervalEvery2MonthsText => Tr.T("Раз в 2 месяца", "2 айда бир", "Every 2 months", "2 ayda bir", "2 oyda bir");
        public string IntervalQuarterlyText => Tr.T("Раз в 3 месяца", "3 айда бир", "Every 3 months", "3 ayda bir", "3 oyda bir");
        public string Months12Text => Tr.T("1 год", "1 жыл", "1 year", "1 yıl", "1 yil");
        public string Months24Text => Tr.T("2 года", "2 жыл", "2 years", "2 yıl", "2 yil");

        public bool IsDebtDueMissing => !IsDebtScheduleValid;

        /// <summary>Подсказка под датой (одним платежом) или итог рассрочки.</summary>
        public string DebtDueHint
        {
            get
            {
                if (_isInstallment)
                    return InstallmentSummary;
                if (_debtDueDate is not { } d)
                    return Tr.T("Спросите клиента, до какого числа он вернёт долг — дата будет в чеке.",
                        "Кардардан карызды качанга чейин кайтарарын сураңыз — дата чекте болот.",
                        "Ask the client by what date they will repay — the date goes on the receipt.",
                        "Müşteriye borcu hangi tarihe kadar ödeyeceğini sorun — tarih fişe yazılır.",
                        "Mijozdan qarzni qachongacha qaytarishini so'rang — sana chekda bo'ladi.");
                if (d < DateTime.Today)
                    return PastDateText();
                var days = (d - DateTime.Today).Days;
                return Tr.T($"Срок: {days} дн., до {d:dd.MM.yyyy}", $"Мөөнөтү: {days} күн, {d:dd.MM.yyyy} чейин", $"Term: {days} days, until {d:dd.MM.yyyy}",
                    $"Süre: {days} gün, {d:dd.MM.yyyy} tarihine kadar", $"Muddat: {days} kun, {d:dd.MM.yyyy} gacha");
            }
        }

        private static string PastDateText() => Tr.T("Дата уже прошла — выберите будущую.", "Бул дата өтүп кетти — келечектеги датаны тандаңыз.",
            "That date has passed — pick a future one.", "Bu tarih geçti — ileri bir tarih seçin.", "Bu sana o'tib ketgan — keyingi sanani tanlang.");

        /// <summary>«3 платежа · раз в неделю · по 6 666,66 сом · до 27.10.2026».</summary>
        public string InstallmentSummary
        {
            get
            {
                if (_installmentFirstDate is not { } first)
                    return Tr.T("Укажите дату первого платежа.", "Биринчи төлөмдүн датасын көрсөтүңүз.", "Set the first payment date.", "İlk ödeme tarihini girin.", "Birinchi to'lov sanasini kiriting.");
                if (first < DateTime.Today)
                    return PastDateText();
                var plan = BuildPlan();
                if (plan is null || plan.Payments.Count == 0)
                    return "";
                var per = plan.Payments[0].Amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                var interval = IntervalWords(plan.IsMonths, plan.Interval);
                return Tr.T($"{plan.Count} плат. · {interval} · по {per} сом · до {plan.LastDueDate:dd.MM.yyyy}",
                    $"{plan.Count} төлөм · {interval} · {per} сомдон · {plan.LastDueDate:dd.MM.yyyy} чейин",
                    $"{plan.Count} payments · {interval} · {per} som each · until {plan.LastDueDate:dd.MM.yyyy}",
                    $"{plan.Count} ödeme · {interval} · {per} som · {plan.LastDueDate:dd.MM.yyyy} tarihine kadar",
                    $"{plan.Count} to'lov · {interval} · {per} so'mdan · {plan.LastDueDate:dd.MM.yyyy} gacha");
            }
        }

        internal static string IntervalWords(bool months, int interval) => months
            ? interval == 1
                ? Tr.T("каждый месяц", "ай сайын", "every month", "her ay", "har oy")
                : Tr.T($"каждые {interval} мес.", $"ар {interval} айда", $"every {interval} months", $"her {interval} ayda", $"har {interval} oyda")
            : interval switch
            {
                1 => Tr.T("каждый день", "күн сайын", "every day", "her gün", "har kuni"),
                2 => Tr.T("через день", "күн аралап", "every other day", "gün aşırı", "kun ora"),
                7 => Tr.T("раз в неделю", "жумасына бир", "once a week", "haftada bir", "haftada bir"),
                _ => Tr.T($"каждые {interval} дн.", $"ар {interval} күндө", $"every {interval} days", $"her {interval} günde", $"har {interval} kunda"),
            };

        /// <summary>Таблица платежей рассрочки.</summary>
        public IReadOnlyList<DebtInstallmentRow> InstallmentRows
        {
            get
            {
                var rows = new List<DebtInstallmentRow>();
                if (!_isInstallment || BuildPlan() is not { } plan)
                    return rows;
                var ru = CultureInfo.GetCultureInfo("ru-RU");
                foreach (var p in plan.Payments)
                    rows.Add(new DebtInstallmentRow(
                        Tr.T($"{p.Number}-й платёж", $"{p.Number}-төлөм", $"Payment {p.Number}", $"{p.Number}. ödeme", $"{p.Number}-to'lov")
                        + (p.Number == plan.Count ? Tr.T(" · финальный", " · акыркы", " · final", " · son", " · oxirgi") : ""),
                        p.DueDate.ToString("dd.MM.yyyy", ru), p.Amount.ToString("N2", ru), p.Number == plan.Count));
                return rows;
            }
        }

        private decimal DebtAmount
        {
            get
            {
                var paid = ParseNonNegative(_debtCashReceived) ?? 0;
                return Math.Max(0m, Math.Round((decimal)_effectiveTotalDue - (decimal)paid, 2));
            }
        }

        /// <summary>График: одним платежом (1 платёж на дату) или рассрочка; null — данных не хватает.</summary>
        private DebtSchedulePlan? BuildPlan()
        {
            var amount = DebtAmount;
            if (!_isInstallment)
            {
                if (_debtDueDate is not { } due)
                    return null;
                return new DebtSchedulePlan("day", 1, 1, due, new[] { new DebtSchedulePayment(1, due, amount) });
            }
            if (_installmentFirstDate is not { } first)
                return null;
            var count = Math.Clamp(_installmentCount, 1, MaxCount);
            var interval = Math.Clamp(_installmentInterval, 1, MaxInterval);
            // Поровну, остаток копеек — в последний платёж (так делит и сервер: 2 сом на 3 мес. = 0,66 + 0,66 + 0,68).
            var cents = (long)Math.Round(amount * 100m);
            var per = cents / count;
            var payments = new List<DebtSchedulePayment>(count);
            for (var i = 0; i < count; i++)
            {
                var date = _installmentByMonths ? first.AddMonths(i * interval) : first.AddDays(i * interval);
                var part = i == count - 1 ? cents - per * (count - 1) : per;
                payments.Add(new DebtSchedulePayment(i + 1, date, part / 100m));
            }
            return new DebtSchedulePlan(_installmentByMonths ? "month" : "day", count, interval, first, payments);
        }

        private bool IsDebtScheduleValid => _isInstallment
            ? _installmentFirstDate is { } f && f >= DateTime.Today && _installmentCount >= 1
            : _debtDueDate is { } d && d >= DateTime.Today;

        /// <summary>График долга для запроса оплаты — только при оплате «В долг».</summary>
        public DebtSchedulePlan? DebtScheduleForApi => IsDebtMode && IsDebtScheduleValid ? BuildPlan() : null;

        private void RaiseDebtScheduleChanged()
        {
            OnPropertyChanged(nameof(DebtDueHint));
            OnPropertyChanged(nameof(IsDebtDueMissing));
            OnPropertyChanged(nameof(InstallmentSummary));
            OnPropertyChanged(nameof(InstallmentRows));
            OnPropertyChanged(nameof(InstallmentIntervalLabel));
            ErrorMessage = "";
            RaiseCommandsCanExecuteChanged();
        }
    }
}
