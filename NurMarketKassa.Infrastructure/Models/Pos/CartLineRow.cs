using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NurMarketKassa.Models.Pos
{
    public class CartLineRow : INotifyPropertyChanged
    {
        private string _itemId = "";
        private double _qty;
        private bool _weighedLine;
        private string _title = "";
        private string _subLine = "";
        private string _lineTotal = "";
        private string _pricePerKgHint = "";
        private string? _discountType;
        private decimal? _discountValue;

        public string ItemId
        {
            get => _itemId;
            set { _itemId = value; OnPropertyChanged(); }
        }

        public double Qty
        {
            get => _qty;
            set { _qty = value; OnPropertyChanged(); }
        }

        public bool WeighedLine
        {
            get => _weighedLine;
            set { _weighedLine = value; OnPropertyChanged(); }
        }

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string SubLine
        {
            get => _subLine;
            set { _subLine = value; OnPropertyChanged(); }
        }

        public string LineTotal
        {
            get => _lineTotal;
            set { _lineTotal = value; OnPropertyChanged(); }
        }

        public string PricePerKgHint
        {
            get => _pricePerKgHint;
            set { _pricePerKgHint = value; OnPropertyChanged(); }
        }

        // --- �������� ������ ---
        public string? DiscountType
        {
            get => _discountType;
            set
            {
                _discountType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiscountDisplay));
                OnPropertyChanged(nameof(HasDiscount));
            }
        }

        public decimal? DiscountValue
        {
            get => _discountValue;
            set
            {
                _discountValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiscountDisplay));
                OnPropertyChanged(nameof(HasDiscount));
            }
        }

        // ����������� �������� ��� ����������� (�������, ������)
        public string DiscountDisplay
        {
            get
            {
                if (DiscountValue == null || DiscountValue == 0)
                    return "";
                if (DiscountType == "percent")
                    return $"-{DiscountValue.Value:0.##}%";
                if (DiscountType == "sum")
                    return $"-{DiscountValue.Value:0.##} ���";
                return "";
            }
        }

        // ??????????????? ???????? ??? ???????? ?????????
        public bool HasDiscount => !string.IsNullOrEmpty(DiscountDisplay);

        public string ProductId { get; set; } = "";

        public bool InsufficientStock
        {
            get => _insufficientStock;
            set
            {
                _insufficientStock = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStockWarning));
            }
        }

        private bool _insufficientStock;

        public bool HasStockWarning => InsufficientStock;

        public string StockWarningText
        {
            get => _stockWarningText;
            set { _stockWarningText = value; OnPropertyChanged(); }
        }

        private string _stockWarningText = "";

        public string StockAvailableText
        {
            get => _stockAvailableText;
            set { _stockAvailableText = value; OnPropertyChanged(); }
        }

        private string _stockAvailableText = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}