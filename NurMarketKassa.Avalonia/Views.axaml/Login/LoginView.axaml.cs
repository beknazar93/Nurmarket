using Avalonia.Controls;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    public LoginView(LoginViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
