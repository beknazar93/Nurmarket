using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NurMarketKassa.ViewModels
{
    public sealed class ConsultantOption
    {
        public ConsultantOption(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public override string ToString() => Name;
    }

    /// <summary>Блок «Консультант» в окне оплаты (2026-09-25, сфера «Одежда»): кто помог
    /// покупателю и какой процент ему начислить с этой продажи. Повторяет сайт (CashierPage,
    /// ConsultantCommissionBlock): сотрудники — api/users/employees/, процент по умолчанию — из
    /// профиля выплат сотрудника, при оплате те же проверки и те же поля checkout.</summary>
    public partial class CheckoutViewModel
    {
        private Func<CancellationToken, Task<IReadOnlyList<(string Id, string Name)>>>? _consultantsLoader;
        private Func<string, CancellationToken, Task<double?>>? _consultantPercentLoader;
        private List<ConsultantOption> _allConsultants = new();
        private bool _consultantsLoaded;
        private bool _isLoadingConsultants;
        private string _consultantsError = "";
        private bool _isConsultantEnabled;
        private ConsultantOption? _selectedConsultant;
        private string _consultantSearch = "";
        private bool _isConsultantCommissionEnabled;
        private string _consultantPercentInput = "";
        private bool _isLoadingConsultantPercent;
        private int _percentRequestVersion;
        private ICommand? _changeConsultantCommand;

        /// <summary>Подключает загрузку сотрудников и их процента — без этого блок не показывается.</summary>
        public void ConfigureConsultants(
            Func<CancellationToken, Task<IReadOnlyList<(string Id, string Name)>>> consultantsLoader,
            Func<string, CancellationToken, Task<double?>> percentLoader)
        {
            _consultantsLoader = consultantsLoader;
            _consultantPercentLoader = percentLoader;
            OnPropertyChanged(nameof(ShowConsultantSection));
        }

        public bool ShowConsultantSection => _consultantsLoader != null && MarketSpheres.IsClothing;

        public ObservableCollection<ConsultantOption> ConsultantMatches { get; } = new();

        public ICommand ChangeConsultantCommand =>
            _changeConsultantCommand ??= new RelayCommand(() => SelectedConsultant = null);

        public bool IsConsultantEnabled
        {
            get => _isConsultantEnabled;
            set
            {
                if (_isConsultantEnabled == value)
                    return;
                _isConsultantEnabled = value;
                if (value)
                {
                    _ = EnsureConsultantsLoadedAsync();
                }
                else
                {
                    // Как на сайте: снятая галочка сбрасывает и выбор, и процент.
                    SelectedConsultant = null;
                    IsConsultantCommissionEnabled = false;
                    ConsultantPercentInput = "";
                    ConsultantSearch = "";
                }
                ErrorMessage = "";
                OnPropertyChanged();
            }
        }

        public ConsultantOption? SelectedConsultant
        {
            get => _selectedConsultant;
            set
            {
                if (ReferenceEquals(_selectedConsultant, value))
                    return;
                _selectedConsultant = value;
                _percentRequestVersion++;
                IsLoadingConsultantPercent = false;
                if (value != null)
                {
                    // Сайт при выборе сотрудника очищает процент, включает начисление и
                    // подставляет процент из его профиля выплат, если он там есть.
                    ConsultantPercentInput = "";
                    IsConsultantCommissionEnabled = true;
                    _ = LoadConsultantPercentAsync(value.Id);
                }
                else
                {
                    IsConsultantCommissionEnabled = false;
                    ConsultantPercentInput = "";
                }
                ErrorMessage = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedConsultant));
                OnPropertyChanged(nameof(ShowConsultantPicker));
            }
        }

        public bool HasSelectedConsultant => _selectedConsultant != null;
        public bool ShowConsultantPicker => _selectedConsultant == null;

        public string ConsultantSearch
        {
            get => _consultantSearch;
            set
            {
                value ??= "";
                if (_consultantSearch == value)
                    return;
                _consultantSearch = value;
                OnPropertyChanged();
                RefreshConsultantMatches();
            }
        }

        public bool IsLoadingConsultants
        {
            get => _isLoadingConsultants;
            private set
            {
                if (_isLoadingConsultants == value)
                    return;
                _isLoadingConsultants = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConsultantListStatus));
                OnPropertyChanged(nameof(HasConsultantListStatus));
            }
        }

        /// <summary>Строка под поиском: загрузка, ошибка загрузки или «никого не найдено».</summary>
        public string ConsultantListStatus =>
            _isLoadingConsultants
                ? Tr.T("Загрузка сотрудников…", "Кызматкерлер жүктөлүүдө…", "Loading employees…", "Çalışanlar yükleniyor…", "Xodimlar yuklanmoqda…")
                : !string.IsNullOrEmpty(_consultantsError)
                    ? _consultantsError
                    : _consultantsLoaded && ConsultantMatches.Count == 0
                        ? Tr.T("Никого не найдено", "Эч ким табылган жок", "Nobody found", "Kimse bulunamadı", "Hech kim topilmadi")
                        : "";

        public bool HasConsultantListStatus => ConsultantListStatus.Length > 0;

        public bool IsConsultantCommissionEnabled
        {
            get => _isConsultantCommissionEnabled;
            set
            {
                if (_isConsultantCommissionEnabled == value)
                    return;
                _isConsultantCommissionEnabled = value;
                ErrorMessage = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowConsultantNoCommissionNote));
                OnPropertyChanged(nameof(ConsultantCommissionPreview));
            }
        }

        public bool ShowConsultantNoCommissionNote => !_isConsultantCommissionEnabled;

        public string ConsultantPercentInput
        {
            get => _consultantPercentInput;
            set
            {
                value ??= "";
                if (_consultantPercentInput == value)
                    return;
                _consultantPercentInput = value;
                ErrorMessage = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConsultantCommissionPreview));
            }
        }

        public bool IsLoadingConsultantPercent
        {
            get => _isLoadingConsultantPercent;
            private set
            {
                if (_isLoadingConsultantPercent == value)
                    return;
                _isLoadingConsultantPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConsultantPercentWatermark));
            }
        }

        public string ConsultantPercentWatermark => _isLoadingConsultantPercent ? "…" : "0";

        /// <summary>«Комиссия ≈ N сом» — как на сайте: итог чека × процент / 100, до копеек.</summary>
        public string ConsultantCommissionPreview
        {
            get
            {
                var pct = ParseConsultantPercent(_consultantPercentInput);
                var amount = _isConsultantCommissionEnabled && pct is > 0 && _effectiveTotalDue > 0
                    ? Math.Round(_effectiveTotalDue * pct.Value / 100.0, 2, MidpointRounding.AwayFromZero)
                    : 0;
                var text = amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                return Tr.T($"Комиссия ≈ {text} сом", $"Комиссия ≈ {text} сом", $"Commission ≈ {text} som",
                    $"Komisyon ≈ {text} som", $"Komissiya ≈ {text} so'm");
            }
        }

        public string ConsultantTitleText => Tr.T("КОНСУЛЬТАНТ", "КОНСУЛЬТАНТ", "CONSULTANT", "DANIŞMAN", "MASLAHATCHI");
        public string ConsultantToggleText => Tr.T("Указать консультанта", "Консультантты көрсөтүү", "Specify a consultant",
            "Danışman belirt", "Maslahatchini ko'rsatish");
        public string ConsultantHintText => Tr.T(
            "Кассир чека — вы. Процент с этой продажи получит консультант.",
            "Чектин кассири — сиз. Бул сатуудан пайызды консультант алат.",
            "You are the cashier of this receipt. The consultant gets the percentage of this sale.",
            "Fişin kasiyeri sizsiniz. Bu satıştan yüzdeyi danışman alır.",
            "Chek kassiri — siz. Bu sotuvdan foizni maslahatchi oladi.");
        public string ConsultantSearchWatermark => Tr.T("Поиск по ФИО…", "Аты-жөнү боюнча издөө…", "Search by name…",
            "Ada göre ara…", "F.I.Sh. bo'yicha qidirish…");
        public string ConsultantChangeText => Tr.T("Изменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish");
        public string ConsultantCommissionToggleText => Tr.T("Начислять процент от продажи", "Сатуудан пайыз эсептөө",
            "Pay a percentage of the sale", "Satıştan yüzde ver", "Sotuvdan foiz hisoblash");
        public string ConsultantPercentLabel => Tr.T("Процент (%)", "Пайыз (%)", "Percent (%)", "Yüzde (%)", "Foiz (%)");
        public string ConsultantNoCommissionText => Tr.T(
            "Консультант будет привязан к чеку без начисления процента.",
            "Консультант чекке пайыз эсептелбей байланат.",
            "The consultant will be linked to the receipt without a percentage.",
            "Danışman fişe yüzdesiz bağlanacak.",
            "Maslahatchi chekka foizsiz biriktiriladi.");

        /// <summary>id консультанта для checkout — null, когда блок выключен или скрыт.</summary>
        public string? ConsultantIdForApi =>
            ShowConsultantSection && _isConsultantEnabled ? _selectedConsultant?.Id : null;

        public string? ConsultantNameForReceipt => ConsultantIdForApi != null ? _selectedConsultant?.Name : null;

        public bool ConsultantCommissionEnabledForApi => ConsultantIdForApi != null && _isConsultantCommissionEnabled;

        public string? ConsultantCommissionPercentForApi =>
            ConsultantCommissionEnabledForApi && ParseConsultantPercent(_consultantPercentInput) is { } pct
                ? pct.ToString("0.00", CultureInfo.InvariantCulture)
                : null;

        /// <summary>Проверки сайта перед оплатой; null — всё в порядке.</summary>
        private string? ValidateConsultant()
        {
            if (!ShowConsultantSection || !_isConsultantEnabled)
                return null;
            if (_selectedConsultant == null)
                return Tr.T(
                    "Выберите консультанта или снимите галочку «Указать консультанта».",
                    "Консультантты тандаңыз же «Консультантты көрсөтүү» белгисин алып салыңыз.",
                    "Choose a consultant or untick \"Specify a consultant\".",
                    "Bir danışman seçin veya «Danışman belirt» işaretini kaldırın.",
                    "Maslahatchini tanlang yoki «Maslahatchini ko'rsatish» belgisini olib tashlang.");
            if (_isConsultantCommissionEnabled && ParseConsultantPercent(_consultantPercentInput) is not { } pct)
                return Tr.T(
                    "Укажите процент консультанта от 0 до 100.",
                    "Консультанттын пайызын 0дөн 100гө чейин көрсөтүңүз.",
                    "Enter the consultant's percentage from 0 to 100.",
                    "Danışman yüzdesini 0 ile 100 arasında girin.",
                    "Maslahatchi foizini 0 dan 100 gacha kiriting.");
            return null;
        }

        /// <summary>Процент 0–100 или null, если введено не число или вне диапазона.</summary>
        private static double? ParseConsultantPercent(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var normalized = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                   && double.IsFinite(value) && value >= 0 && value <= 100
                ? value
                : null;
        }

        private async Task EnsureConsultantsLoadedAsync()
        {
            if (_consultantsLoaded || _isLoadingConsultants || _consultantsLoader == null)
                return;
            IsLoadingConsultants = true;
            _consultantsError = "";
            try
            {
                var rows = await _consultantsLoader(CancellationToken.None);
                _allConsultants = rows.Select(r => new ConsultantOption(r.Id, r.Name)).ToList();
                _consultantsLoaded = true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Consultants load failed: {ex.Message}", "PAYMENT");
                _consultantsError = Tr.T("Не удалось загрузить сотрудников", "Кызматкерлерди жүктөө мүмкүн болгон жок",
                    "Could not load employees", "Çalışanlar yüklenemedi", "Xodimlarni yuklab bo'lmadi");
            }
            finally
            {
                IsLoadingConsultants = false;
            }
            RefreshConsultantMatches();
        }

        private void RefreshConsultantMatches()
        {
            var query = _consultantSearch.Trim();
            ConsultantMatches.Clear();
            foreach (var c in _allConsultants)
            {
                if (query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    ConsultantMatches.Add(c);
            }
            OnPropertyChanged(nameof(ConsultantListStatus));
            OnPropertyChanged(nameof(HasConsultantListStatus));
        }

        private async Task LoadConsultantPercentAsync(string userId)
        {
            if (_consultantPercentLoader == null)
                return;
            var version = _percentRequestVersion;
            IsLoadingConsultantPercent = true;
            double? percent = null;
            try
            {
                percent = await _consultantPercentLoader(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Как на сайте: без профиля выплат процент просто не подставляется.
                PosLogger.Log($"Consultant percent load failed: {ex.Message}", "PAYMENT");
            }

            if (version != _percentRequestVersion)
                return;
            IsLoadingConsultantPercent = false;
            if (percent is { } p && ParseConsultantPercent(_consultantPercentInput) == null)
            {
                ConsultantPercentInput = p.ToString("0.##", CultureInfo.InvariantCulture);
                IsConsultantCommissionEnabled = true;
            }
        }
    }
}
