using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

#nullable disable

namespace NurMarketKassa.ViewModels
{
    public class ProductDetailVm : INotifyPropertyChanged
    {
        private string _id = "";
        private string _productName = "";
        private string _barcode = "";
        private string _article = "";
        private string _code = "";
        private DateTime _createdAt = DateTime.Now;
        private string _category = "";
        private string _country = "";
        private DateTime? _expiryDate;
        private string _group = "";
        private string _description = "";
        private decimal _price;
        private decimal _purchasePrice;
        private decimal _markupPercent;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string ProductName
        {
            get => _productName;
            set { _productName = value; OnPropertyChanged(); }
        }

        public string Barcode
        {
            get => _barcode;
            set { _barcode = value; OnPropertyChanged(); }
        }

        public string Article
        {
            get => _article;
            set { _article = value; OnPropertyChanged(); }
        }

        public string Code
        {
            get => _code;
            set { _code = value; OnPropertyChanged(); }
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set { _createdAt = value; OnPropertyChanged(); }
        }

        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(); }
        }

        public string Country
        {
            get => _country;
            set { _country = value; OnPropertyChanged(); }
        }

        public DateTime? ExpiryDate
        {
            get => _expiryDate;
            set { _expiryDate = value; OnPropertyChanged(); }
        }

        public string Group
        {
            get => _group;
            set { _group = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public decimal Price
        {
            get => _price;
            set { _price = value; OnPropertyChanged(); }
        }

        public decimal PurchasePrice
        {
            get => _purchasePrice;
            set { _purchasePrice = value; OnPropertyChanged(); }
        }

        public decimal MarkupPercent
        {
            get => _markupPercent;
            set { _markupPercent = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}