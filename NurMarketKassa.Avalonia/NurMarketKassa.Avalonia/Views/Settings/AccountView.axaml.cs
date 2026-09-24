using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Раздел "Аккаунт" в Настройках — тариф, срок действия подписки NurCRM и
/// подключённые доп. услуги (GET /api/users/company/, см. CompanyInfoService).</summary>
public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
        RenderCompany(CompanyInfoService.LastCompany);
        _ = LoadAsync();
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>2026-09-13, по просьбе владельца — перенос текущего онлайн-каталога в
    /// автономный (офлайн) режим на этом же ПК (см. MigrateToOfflineDialog). При успехе диалог
    /// уже подготовил (активировал ключ, создал локальный аккаунт, пометил каталог как
    /// сохраняемый) — здесь только обычный выход из NurCRM-сессии, тем же путём, что и кнопка
    /// "Выйти" ниже, чтобы кассир попал на экран входа и вошёл уже автономно.</summary>
    private async void MigrateOfflineButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new MigrateToOfflineDialog();
        PosDialogHost.Show(dialog, owner);
        if (!dialog.Completed)
            return;

        var settingsWindow = owner;
        settingsWindow?.Close();

        var bridge = NurMarketKassa.AvaloniaHost.App.GetRequiredService<MainWindowHostBridge>();
        if (bridge.Window is { } mainWindow)
            await mainWindow.LogoutAsync().ConfigureAwait(true);
    }

    /// <summary>Раздел "Выйти" был отдельным пунктом главного меню — по просьбе пользователя
    /// перенесён сюда, в Аккаунт, и убран из меню (см. SideMenuView). Сама логика выхода
    /// (закрытие смены, переход на экран входа) остаётся в MainWindow.LogoutAsync — здесь
    /// только вызов через bridge, т.к. этот UserControl не владеет окном MainWindow напрямую.</summary>
    private async void LogoutButton_Click(object? sender, RoutedEventArgs e)
    {
        var settingsWindow = TopLevel.GetTopLevel(this) as Window;
        settingsWindow?.Close();

        var bridge = NurMarketKassa.AvaloniaHost.App.GetRequiredService<MainWindowHostBridge>();
        if (bridge.Window is { } mainWindow)
            await mainWindow.LogoutAsync().ConfigureAwait(true);
    }

    /// <summary>Карточка «Вход в кассу» — из того, что касса уже знает: ответ сервера на вход
    /// (логин, имя, роль) и текущая сессия (касса, смена). Сеть не нужна.</summary>
    private void RenderLogin()
    {
        var session = App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>();
        var user = App.GetRequiredService<NurMarketApiClient>().UserPayload;

        string? Read(string name) =>
            user.ValueKind == System.Text.Json.JsonValueKind.Object
            && user.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;

        var lastLogin = UserPreferences.Instance.LastLoginEmail;
        LoginText.Text = Read("email") ?? (string.IsNullOrWhiteSpace(lastLogin) ? "—" : lastLogin);
        CashierNameText.Text = string.IsNullOrWhiteSpace(session.CurrentUserDisplayName) ? "—" : session.CurrentUserDisplayName;
        RoleText.Text = Read("role_display") ?? Read("role") ?? "—";
        CashboxText.Text = string.IsNullOrWhiteSpace(session.PosCashboxDisplayName) ? "—" : session.PosCashboxDisplayName;
        ShiftText.Text = string.IsNullOrWhiteSpace(session.ActiveShiftId)
            ? Tr.T("не открыта", "ачылган эмес", "not open", "açık değil", "ochilmagan")
            : Tr.T("открыта", "ачык", "open", "açık", "ochiq") + " · №" + session.ActiveShiftId![..Math.Min(8, session.ActiveShiftId.Length)].ToUpperInvariant();
    }

    private async Task LoadAsync()
    {
        try
        {
            RenderLogin();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"AccountView login card failed: {ex.Message}", "WARNING");
        }

        RefreshButton.IsEnabled = false;
        LoadingOrErrorText.IsVisible = true;
        LoadingOrErrorText.Text = Tr.T("Загрузка...", "Жүктөлүүдө...", "Loading...", "Yükleniyor...", "Yuklanmoqda...");
        try
        {
            var status = await CompanyInfoService.RefreshAsync(App.AuthApi).ConfigureAwait(true);
            RenderCompany(CompanyInfoService.LastCompany);
            LoadingOrErrorText.IsVisible = status is null && CompanyInfoService.LastCompany is null;
            if (LoadingOrErrorText.IsVisible)
                LoadingOrErrorText.Text = Tr.T(
                    "Не удалось загрузить данные аккаунта — нет связи с сервером.",
                    "Аккаунт маалыматын жүктөө мүмкүн болгон жок — сервер менен байланыш жок.");
        }
        catch (Exception ex)
        {
            LoadingOrErrorText.IsVisible = true;
            LoadingOrErrorText.Text = Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") + ex.Message;
            PosLogger.Log($"AccountView load failed: {ex}", "WARNING");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void RenderCompany(CompanyDto? company)
    {
        if (company is null)
        {
            CompanyNameText.Text = Tr.T("—", "—", "—", "—", "—");
            CompanyInnText.Text = "";
            PlanNameText.Text = Tr.T("—", "—", "—", "—", "—");
            PlanPriceText.Text = "";
            ExpiryDatesText.Text = "";
            ExpiryStatusBadge.IsVisible = false;
            ServicesList.Items.Clear();
            return;
        }

        CompanyNameText.Text = string.IsNullOrWhiteSpace(company.Name) ? Tr.T("Без названия", "Аталышы жок", "Untitled", "Adsız", "Nomsiz") : company.Name;
        CompanyInnText.Text = string.IsNullOrWhiteSpace(company.Inn) ? "" : $"{Tr.T("ИНН", "ИНН", "TIN", "VKN", "STIR")}: {company.Inn}";

        PlanNameText.Text = string.IsNullOrWhiteSpace(company.SubscriptionPlanName)
            ? Tr.T("Не определён", "Аныкталган эмес", "Not defined", "Tanımlanmamış", "Aniqlanmagan")
            : company.SubscriptionPlanName;
        PlanPriceText.Text = string.IsNullOrWhiteSpace(company.SubscriptionPlanPrice)
            ? ""
            : $"{company.SubscriptionPlanPrice} {Tr.T("сом / мес.", "сом / ай", "som / mo.", "som / ay", "so'm / oy")}";

        RenderExpiry(company.StartDate, company.EndDate);
        RenderServices(company);
    }

    private void RenderExpiry(string? startDateRaw, string? endDateRaw)
    {
        var start = TryParseDate(startDateRaw);
        var end = TryParseDate(endDateRaw);

        ExpiryDatesText.Text = start is null && end is null
            ? Tr.T("Даты не указаны.", "Даталар көрсөтүлгөн эмес.", "Dates are not specified.", "Tarihler belirtilmedi.", "Sanalar ko'rsatilmagan.")
            : Tr.T(
                $"С {FormatDate(start)} по {FormatDate(end)}",
                $"{FormatDate(start)} чейин {FormatDate(end)}");

        if (end is null)
        {
            ExpiryStatusBadge.IsVisible = false;
            return;
        }

        var remaining = end.Value - DateTimeOffset.Now;
        string statusText;
        string bgKey, borderKey, fgKey;
        if (remaining <= TimeSpan.Zero)
        {
            statusText = Tr.T("Подписка истекла", "Жазылуу мөөнөтү бүттү", "Subscription expired", "Abonelik sona erdi", "Obuna muddati tugadi");
            (bgKey, borderKey, fgKey) = ("BrushDangerSoft", "BrushDanger", "BrushDanger");
        }
        else if (remaining.TotalDays <= 3)
        {
            var days = (int)Math.Ceiling(remaining.TotalDays);
            statusText = Tr.T($"Истекает через {days} дн.", $"{days} күндөн кийин бүтөт",
                $"Expires in {days} d.", $"{days} gün içinde sona erer", $"{days} kundan keyin tugaydi");
            (bgKey, borderKey, fgKey) = ("BrushWarningSoft", "BrushWarning", "BrushWarning");
        }
        else
        {
            statusText = Tr.T("Активна", "Активдүү", "Active", "Aktif", "Faol");
            (bgKey, borderKey, fgKey) = ("BrushSuccessSoft", "BrushSuccess", "BrushSuccess");
        }

        ExpiryStatusBadge.IsVisible = true;
        ExpiryStatusBadge.Background = ThemeBrush(bgKey, Brushes.LightGray);
        ExpiryStatusBadge.BorderBrush = ThemeBrush(borderKey, Brushes.Gray);
        ExpiryStatusText.Text = statusText;
        ExpiryStatusText.Foreground = ThemeBrush(fgKey, Brushes.Black);
    }

    private void RenderServices(CompanyDto company)
    {
        ServicesList.Items.Clear();
        var services = new (string LabelRu, string LabelKy, bool Enabled)[]
        {
            (Tr.T("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar"), "", company.CanViewDocuments),
            ("WhatsApp", "", company.CanViewWhatsapp),
            ("Instagram", "", company.CanViewInstagram),
            ("Telegram", "", company.CanViewTelegram),
            (Tr.T("Витрина (showcase)", "Витрина (showcase)", "Showcase", "Vitrin (showcase)", "Vitrina (showcase)"), "", company.CanViewShowcase),
        };

        foreach (var (label, _, enabled) in services)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = enabled ? "✅" : "⬜",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Opacity = enabled ? 1.0 : 0.55,
                VerticalAlignment = VerticalAlignment.Center,
            });
            ServicesList.Items.Add(row);
        }
    }

    private static DateTimeOffset? TryParseDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;

    private static string FormatDate(DateTimeOffset? dt) =>
        dt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—";

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Avalonia.Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;
}
