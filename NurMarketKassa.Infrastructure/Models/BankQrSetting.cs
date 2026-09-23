using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NurMarketKassa.Models
{
    public class BankQrSetting : INotifyPropertyChanged
    {
        private string? _qrCodePath;
        private string? _logoPath;

        public string BankName { get; set; } = "";

        public string? QrCodePath
        {
            get => _qrCodePath;
            set
            {
                _qrCodePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQrCode));
            }
        }

        public string? LogoPath
        {
            get => _logoPath;
            set
            {
                _logoPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(HasNoLogo));
            }
        }

        public bool HasQrCode => !string.IsNullOrEmpty(QrCodePath);
        public bool HasLogo => !string.IsNullOrEmpty(LogoPath);
        public bool HasNoLogo => !HasLogo;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}