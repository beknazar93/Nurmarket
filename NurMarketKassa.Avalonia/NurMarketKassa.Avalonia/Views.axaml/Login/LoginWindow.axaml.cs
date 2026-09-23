using Application = Avalonia.Application;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
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

            // "Запомнить меня" по умолчанию включена — касса стоит на одном
            // рабочем месте, и кассиру не нужно логиниться заново после каждого
            // перезапуска приложения. Кассир может явно снять галочку сам.
            _viewModel.RememberMe = true;
            RememberMeCheckBox.IsChecked = true;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>2026-09-17: не своя экранная клавиатура — пользователь уже один раз
        /// (2026-09-15, см. MainWindow.ToggleKeyboard) явно попросил вернуть системную
        /// клавиатуру Windows вместо самодельной, та же логика применяется и здесь.</summary>
        private void ToggleKeyboardButton_Click(object sender, RoutedEventArgs e) =>
            App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (UserPreferences.Instance.Fullscreen)
                CanResize = false;
            FullscreenHelper.Apply(this);

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

                // 2026-09-09: автономный (офлайн) вход — своего "аккаунта" на сервере нет вообще,
                // поэтому здесь нельзя дёргать ни загрузку компании (сетевой вызов, которого
                // никогда не будет), ни AccountCatalogIsolation.PrepareForAuthenticatedUser (эта
                // защита считает локальный email "новым" пользователем и СТИРАЕТ локальный
                // каталог товаров — реальный баг, пойманный на живом тесте: обычный локальный
                // каталог обнулился после первого автономного входа), ни фоновую синхронизацию с
                // сервером (SyncService.Start — тоже вся про NurCRM).
                var isAutonomous = App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous;
                if (!isAutonomous)
                {
                    _viewModel.SetLoadingStatus(Tr.T("Загрузка компании…", "Компания жүктөлүүдө…", "Loading company…", "Şirket yükleniyor…", "Kompaniya yuklanmoqda…"));
                    await CompanyInfoService.RefreshAsync(App.AuthApi, CancellationToken.None).ConfigureAwait(true);
                    App.AuditDb.LogEvent("auth", "login", new { userId }, userId);

                    AccountCatalogIsolation.PrepareForAuthenticatedUser("", userId);
                    App.GetRequiredService<SyncService>().Start();
                }

                // 2026-09-09: PosApp.IsOfflineBootstrap управляет OfflineModeHelper.UseLocalOperations,
                // который уже используется по всему приложению (PosCheckoutService, CashShiftService,
                // ShiftStateService, CatalogCacheService) — включив его, автономный режим бесплатно
                // получает всю готовую офлайн-логику продаж/смен/каталога, без отдельных Local*ApiService.
                // 2026-09-10: ВАЖНО — здесь нужен именно NurMarketKassa.App (мост к PosApp), а не
                // голое "App": внутри пространства имён NurMarketKassa.AvaloniaHost.Views неквалифи-
                // цированное имя App резолвится в объемлющее NurMarketKassa.AvaloniaHost.App (свой
                // собственный, НЕСВЯЗАННЫЙ IsOfflineBootstrap в App.axaml.cs) — запись в него молча
                // никуда не долетала, из-за чего OfflineModeHelper.UseLocalOperations оставался false
                // всю автономную сессию (тот самый баг "у тебя ничего не поменялось").
                NurMarketKassa.App.IsOfflineBootstrap = isAutonomous || _viewModel.IsOfflineMode;
                NurMarketKassa.App.OfflineBootstrapMessage = isAutonomous
                    ? Tr.T("Автономный режим — работа без интернета.", "Автономдук режим — интернетсиз иштөө.", "Offline mode — working without internet.", "Çevrimdışı mod — internetsiz çalışma.", "Oflayn rejim — internetsiz ishlash.")
                    : _viewModel.IsOfflineMode
                        ? Tr.T("Нет связи с сервером. Используются локальные данные.", "Сервер менен байланыш жок. Жергиликтүү маалыматтар колдонулууда.", "No connection to the server. Using local data.", "Sunucuyla bağlantı yok. Yerel veriler kullanılıyor.", "Server bilan aloqa yo'q. Mahalliy ma'lumotlar ishlatilmoqda.")
                        : null;

                _viewModel.SetLoadingStatus(Tr.T("Загрузка кассы…", "Касса жүктөлүүдө…", "Loading the POS…", "Kasa yükleniyor…", "Kassa yuklanmoqda…"));
                var mainWindow = App.GetRequiredService<MainWindow>();
                var progress = new Progress<string>(status =>
                {
                    if (!string.IsNullOrWhiteSpace(status))
                        _viewModel.SetLoadingStatus(status);
                });

                var canOpen = await mainWindow.InitializeApplicationAsync(progress, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!canOpen)
                {
                    if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime __shutdownDesk)
                        __shutdownDesk.Shutdown();
                    else
                        Close();
                    return;
                }

                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime __desk) __desk.MainWindow = mainWindow;
                mainWindow.PlaceOnPrimaryScreen();
                mainWindow.Show();
                Close();
            }
            catch (Exception ex)
            {
                _enteringMain = false;
                _viewModel.ReportError(Tr.T("Не удалось загрузить кассу: ", "Кассаны жүктөө мүмкүн болгон жок: ", "Could not load the POS: ", "Kasa yüklenemedi: ", "Kassani yuklab bo'lmadi: ") + ex.Message);
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

        private void RememberMeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
                return;
            _viewModel.RememberMe = RememberMeCheckBox.IsChecked == true;
        }

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

        private async void ExitButton_Click(object sender, RoutedEventArgs e) =>
            await ConfirmAndExitAsync();

        // Fullscreen login (SystemDecorations.None, see OnLoaded) has no native
        // window chrome, so minimize/close only exist if the login screen itself
        // offers them.
        private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private async void CloseWindowButton_Click(object sender, RoutedEventArgs e) =>
            await ConfirmAndExitAsync();

        private async Task ConfirmAndExitAsync()
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
