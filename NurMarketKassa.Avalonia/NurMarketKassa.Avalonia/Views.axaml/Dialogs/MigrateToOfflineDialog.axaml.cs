using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-09-13, по просьбе владельца ("с онлайна на офлайн переходил с базой") —
/// обратное направление MigrateToOnlineDialog (автономная сборка): владелец текущего
/// NurCRM-аккаунта заводит автономный (офлайн) доступ на ЭТОМ ЖЕ ПК, а его текущий каталог
/// (после полной досинхронизации с сервера) переносится в локальный офлайн-каталог вместо
/// обычной очистки при первом автономном входе (см. AutonomousAuthService.LoginLocal /
/// IAutonomousAuthService.MarkCatalogPreservedForNextLogin).
///
/// Сама эта касса НЕ переключается в офлайн прямо здесь "по живому" — после успешного переноса
/// диалог просто просит вызывающий код (AccountView) сделать обычный выход (MainWindow.
/// LogoutAsync, как для кнопки "Выйти"), и на экране входа кассир нажимает «Работать
/// автономно» и входит уже новым email/паролем — используется уже существующий, проверенный
/// экран, а не отдельная логика смены сессии на лету.</summary>
public partial class MigrateToOfflineDialog : Window
{
    /// <summary>true — перенос прошёл успешно, вызывающий код должен выполнить выход из
    /// текущей NurCRM-сессии, чтобы кассир мог войти автономно новыми данными.</summary>
    public bool Completed { get; private set; }

    private bool _isRunning;

    public MigrateToOfflineDialog()
    {
        InitializeComponent();
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Completed)
        {
            Close(true);
            return;
        }

        if (_isRunning)
            return;

        var key = KeyBox.Text?.Trim() ?? "";
        var email = EmailBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        if (key.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            ShowError(Tr.T("Заполните ключ активации, email и пароль.",
                "Активация ачкычын, email жана паролду толтуруңуз.",
                "Fill in the activation key, email and password.",
                "Etkinleştirme anahtarını, e-postayı ve şifreyi doldurun.",
                "Faollashtirish kalitini, emailni va parolni to'ldiring."));
            return;
        }

        SetRunning(true);

        // 2026-09-13: досинхронизируем каталог с сервера ПЕРЕД активацией — чтобы в офлайн
        // ушёл действительно текущий полный каталог аккаунта, а не то, что случайно уже было
        // в локальном кэше (частичная синхронизация/старые правки).
        ShowStatus(Tr.T("Синхронизация каталога с сервером…", "Каталог сервер менен синхрондоштурулууда…",
            "Syncing the catalog with the server…", "Katalog sunucuyla senkronize ediliyor…", "Katalog server bilan sinxronlanmoqda…"));
        var syncResult = await CatalogCacheService.SyncCatalogFullAsync(CancellationToken.None).ConfigureAwait(true);
        if (!syncResult.Success)
        {
            SetRunning(false);
            ShowError(syncResult.ErrorMessage ?? Tr.T("Не удалось синхронизировать каталог с сервером.",
                "Каталогду сервер менен синхрондоштуруу мүмкүн болгон жок.",
                "Could not sync the catalog with the server.",
                "Katalog sunucuyla senkronize edilemedi.",
                "Katalogni server bilan sinxronlab bo'lmadi."));
            return;
        }

        var autonomous = NurMarketKassa.AvaloniaHost.App.GetRequiredService<IAutonomousAuthService>();

        ShowStatus(Tr.T("Проверка ключа активации…", "Активация ачкычы текшерилүүдө…", "Checking the activation key…", "Etkinleştirme anahtarı kontrol ediliyor…", "Faollashtirish kaliti tekshirilmoqda…"));
        var (activated, activateError) = await autonomous.ActivateAsync(key, CancellationToken.None).ConfigureAwait(true);
        if (!activated)
        {
            SetRunning(false);
            ShowError(activateError ?? Tr.T("Не удалось активировать ключ.", "Ачкычты активдештирүү мүмкүн болгон жок.", "Could not activate the key.", "Anahtar etkinleştirilemedi.", "Kalitni faollashtirib bo'lmadi."));
            return;
        }

        var (accountCreated, accountError) = autonomous.CreateLocalAccount(email, password, displayName: null);
        if (!accountCreated)
        {
            SetRunning(false);
            ShowError(accountError ?? Tr.T("Не удалось создать локальный аккаунт.", "Жергиликтүү аккаунт түзүү мүмкүн болгон жок.", "Could not create the local account.", "Yerel hesap oluşturulamadı.", "Mahalliy hisob yaratib bo'lmadi."));
            return;
        }

        // Каталог, который мы только что полностью досинхронизировали выше, не должен
        // стираться при первом автономном входе новым аккаунтом (см. её собственный
        // комментарий) — помечаем ОДНОРАЗОВОЕ исключение.
        autonomous.MarkCatalogPreservedForNextLogin();

        Completed = true;
        SetRunning(false);
        FormPanel.IsEnabled = false;
        ConfirmButton.Content = Tr.T("Готово — выйти", "Бүттү — чыгуу", "Done — log out", "Tamamlandı — çıkış yap", "Tayyor — chiqish");
        CancelButton.IsVisible = false;
        ShowStatus(Tr.T(
            $"Каталог подготовлен ({syncResult.Added + syncResult.Changed} товаров). Нажмите «Готово — выйти», затем на экране входа выберите «Работать автономно» и войдите этим email и паролем.",
            $"Каталог даяр ({syncResult.Added + syncResult.Changed} товар). «Бүттү — чыгуу» баскычын басып, кирүү экранында «Автономдук иштөөнү» тандап, ушул email жана пароль менен кириңиз.",
            $"Catalog prepared ({syncResult.Added + syncResult.Changed} products). Click \"Done — log out\", then on the login screen choose \"Work autonomously\" and sign in with this email and password.",
            $"Katalog hazır ({syncResult.Added + syncResult.Changed} ürün). \"Tamamlandı — çıkış yap\"a tıklayın, ardından giriş ekranında \"Bağımsız çalış\"ı seçip bu e-posta ve şifreyle giriş yapın.",
            $"Katalog tayyor ({syncResult.Added + syncResult.Changed} mahsulot). \"Tayyor — chiqish\"ni bosing, so'ng kirish ekranida \"Avtonom ishlash\"ni tanlab shu email va parol bilan kiring."));
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        FormPanel.IsEnabled = !running;
        ConfirmButton.IsEnabled = !running;
        CancelButton.IsEnabled = !running;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
