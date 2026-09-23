using Application = Avalonia.Application;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using System;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    public partial class LoginWindow : Window
    {
        private readonly LoginViewModel _viewModel;
        private bool _passwordVisible;
        private bool _suppressPasswordSync;
        private bool _enteringMain;

        /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
        public LoginWindow() : this(ResolveService<LoginViewModel>())
        {
        }

        private static T ResolveService<T>() where T : notnull
        {
            var sp = App.AppHost?.Services
                ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
            return sp.GetRequiredService<T>();
        }

        public LoginWindow(LoginViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;
            _viewModel.LoginSuccess += OnLoginSuccess;
            _viewModel.CredentialsClearRequested += ClearCredentialControls;

            // auth.dat is consumed by the background service. UI inputs always
            // start empty and are not involved in automatic authentication.
            ClearCredentialControls();

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (UserPreferences.Instance.Fullscreen)
            {
                SystemDecorations = SystemDecorations.None;
                CanResize = false;
                WindowState = WindowState.Maximized;
            }

            UpdateEmailValidIcon();
            UpdateThemeButtons(UserPreferences.Instance.DarkTheme);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _viewModel.ClearCredentialInputs();
            _viewModel.LoginSuccess -= OnLoginSuccess;
            _viewModel.CredentialsClearRequested -= ClearCredentialControls;
        }

        private async Task OnLoginSuccess()
        {
            if (_enteringMain)
                return;

            _enteringMain = true;
            try
            {
                var userId = App.CurrentUserId;

                _viewModel.SetLoadingStatus("Загрузка компании…");
                await CompanyInfoService.RefreshAsync(App.AuthApi, CancellationToken.None).ConfigureAwait(true);
                App.AuditDb.LogEvent("auth", "login", new { userId }, userId);

                AccountCatalogIsolation.PrepareForAuthenticatedUser("", userId);
                App.GetRequiredService<SyncService>().Start();

                App.IsOfflineBootstrap = _viewModel.IsOfflineMode;
                App.OfflineBootstrapMessage = _viewModel.IsOfflineMode
                    ? "Нет связи с сервером. Используются локальные данные."
                    : null;

                _viewModel.SetLoadingStatus("Загрузка кассы…");
                var mainWindow = App.GetRequiredService<MainWindow>();
                var progress = new Progress<string>(status =>
                {
                    if (!string.IsNullOrWhiteSpace(status))
                        _viewModel.SetLoadingStatus(status);
                });

                await mainWindow.InitializeApplicationAsync(progress, CancellationToken.None)
                    .ConfigureAwait(true);

                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime __desk) __desk.MainWindow = mainWindow;
                mainWindow.PlaceOnPrimaryScreen();
                mainWindow.Show();
                Close();
            }
            catch (Exception ex)
            {
                _enteringMain = false;
                _viewModel.ReportError("Не удалось загрузить кассу: " + ex.Message);
            }
        }

        private void UpdateEmailValidIcon()
        {
            var username = _viewModel.Username;
            EmailValidIcon.IsVisible = !string.IsNullOrWhiteSpace(username) && username.Contains('@') && username.Contains('.')
                    ;
        }

        private void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.Username = EmailBox.Text;
            UpdateEmailValidIcon();
        }

        private void PasswordBox_PasswordChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPasswordSync || _passwordVisible)
                return;

            _viewModel.Password = PasswordBox.Text;
        }

        private void VisiblePasswordBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPasswordSync || !_passwordVisible)
                return;

            _viewModel.Password = VisiblePasswordBox.Text;
        }

        private void RememberMeCheckBox_Changed(object sender, RoutedEventArgs e) =>
            _viewModel.RememberMe = RememberMeCheckBox.IsChecked == true;

        private void ClearCredentialControls()
        {
            _suppressPasswordSync = true;
            try
            {
                EmailBox.Text = "";
                PasswordBox.Text = "";
                VisiblePasswordBox.Text = "";
            }
            finally
            {
                _suppressPasswordSync = false;
            }
        }

        private void FluentField_GotFocus(object sender, GotFocusEventArgs e)
        {
            AnimateFieldBorder(GetFieldBorder(sender), true);
        }

        private void FluentField_LostFocus(object sender, RoutedEventArgs e)
        {
            AnimateFieldBorder(GetFieldBorder(sender), false);
        }

        private Border GetFieldBorder(object sender)
        {
            if (sender == EmailBox || sender == VisiblePasswordBox)
                return sender == EmailBox ? EmailFieldBorder : PasswordFieldBorder;

            if (sender == PasswordBox)
                return PasswordFieldBorder;

            return null;
        }

        private void AnimateFieldBorder(Border border, bool focused)
        {
            if (border == null) return;
            var key = focused ? "BrushFocus" : "BrushBorder";
            border.BorderBrush =
                Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
                && value is IBrush brush
                    ? brush
                    : focused ? Brushes.Gold : Brushes.Gray;
        }

        private void LightThemeButton_Click(object sender, RoutedEventArgs e) =>
            SetLoginTheme(dark: false);

        private void DarkThemeButton_Click(object sender, RoutedEventArgs e) =>
            SetLoginTheme(dark: true);

        private void SetLoginTheme(bool dark)
        {
            App.ApplyTheme(dark);

            var preferences = UserPreferences.Instance;
            preferences.DarkTheme = dark;
            preferences.SaveToDisk();

            UpdateThemeButtons(dark);
            EmailFieldBorder.BorderBrush = ThemeBrush("BrushBorder", Brushes.Gray);
            PasswordFieldBorder.BorderBrush = ThemeBrush("BrushBorder", Brushes.Gray);
        }

        private void UpdateThemeButtons(bool dark)
        {
            LightThemeButton.Classes.Set("selected", !dark);
            DarkThemeButton.Classes.Set("selected", dark);
            LightHeroImage.IsVisible = !dark;
            DarkHeroImage.IsVisible = dark;
        }

        private IBrush ThemeBrush(string key, IBrush fallback) =>
            Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
            && value is IBrush brush
                ? brush
                : fallback;

        private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            SetPasswordVisible(!_passwordVisible);
        }

        private void SetPasswordVisible(bool show)
        {
            _passwordVisible = show;
            TogglePasswordIcon.Text = show ? "\uED1A" : "\uE7B3";

            _suppressPasswordSync = true;
            try
            {
                if (show)
                {
                    VisiblePasswordBox.Text = PasswordBox.Text;
                    PasswordBox.IsVisible = false;
                    VisiblePasswordBox.IsVisible = true;
                    VisiblePasswordBox.Focus();
                    VisiblePasswordBox.SelectAll();
                }
                else
                {
                    PasswordBox.Text = VisiblePasswordBox.Text;
                    VisiblePasswordBox.IsVisible = false;
                    PasswordBox.IsVisible = true;
                    PasswordBox.Focus();
                    PasswordBox.SelectAll();
                }

                _viewModel.Password = show ? VisiblePasswordBox.Text : PasswordBox.Text;
            }
            finally
            {
                _suppressPasswordSync = false;
            }
        }

        private async void AdminSupport_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            OverlayGrid.Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
            OverlayGrid.IsHitTestVisible = true;

            try
            {
                var support = new AdminSupportWindow();
                // Используем await вместо GetAwaiter().GetResult()
                await support.ShowDialog(this);
            }
            finally
            {
                // Блок finally гарантирует, что затемнение снимется, 
                // даже если внутри окна произойдет ошибка
                OverlayGrid.Background = Brushes.Transparent;
                OverlayGrid.IsHitTestVisible = false;
            }
        }

        private void EmailBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return) return;

            if (_passwordVisible)
                VisiblePasswordBox.Focus();
            else
                PasswordBox.Focus();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && _viewModel.LoginCommand.CanExecute(null))
                _viewModel.LoginCommand.Execute(null);
        }

        private void VisiblePasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && _viewModel.LoginCommand.CanExecute(null))
                _viewModel.LoginCommand.Execute(null);
        }

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = await ExitConfirmationDialog.ConfirmExitAsync(this);
            if (!confirmed)
                return;

            ShutdownApplication();
        }

        private static void ShutdownApplication()
        {
            App.ExitWithoutLoginRedirect = true;
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
    }
}
