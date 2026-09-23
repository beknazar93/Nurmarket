using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-09-08: раздел "Сотрудники" (Настройки, только для владельца/админа — сама
/// страница Настроек доступна лишь тем, у кого есть ViewSettings) — здесь владелец заводит
/// личные коды доступа сотрудников для 4 защищённых действий (см. EmployeeAccessGate), а также
/// (2026-09-08) управляет реальными сотрудниками/ролями на сервере (app.nurcrm.kg): создание и
/// удаление через CreateEmployeeAsync/DeleteEmployeeAsync/CreateRoleAsync/DeleteRoleAsync.
/// Список сотрудников строится вручную в код-behind (как HotkeyOptionsList/CategoryOptionsList в
/// ProductEditDialog) — правки полей PIN-кодов пишутся напрямую в объекты EmployeeAccessCode
/// "на лету", кнопка "Сохранить" персистит их на диск (это отдельно от серверных операций,
/// которые применяются сразу же, без кнопки "Сохранить").</summary>
public partial class EmployeesSettingsView : UserControl
{
    private List<RoleInfoDto> _roles = new();

    public EmployeesSettingsView()
    {
        InitializeComponent();
        RefreshList();

        // 2026-09-09: офлайн (обычный или автономный режим) — реальных Сотрудников/Ролей на
        // сервере не существует без NurCRM, эти кнопки/карточка только пытались бы дёрнуть
        // IAuthApiService.CreateEmployeeAsync/GetRolesAsync и т.п. (реальный HTTP). Остаются
        // только локальные PIN-коды (EmployeeAccessCode) — они и так полностью офлайновые.
        if (OfflineModeHelper.UseLocalOperations)
        {
            RolesCard.IsVisible = false;
            AddEmployeeButton.IsVisible = false;
            LoadFromWebButton.IsVisible = false;
        }
        else
        {
            _ = LoadRolesAsync();
        }
    }

    private static IAuthApiService? AuthApi => App.AppHost?.Services.GetService<IAuthApiService>();

    private void RefreshList()
    {
        EmployeeListPanel.Children.Clear();
        foreach (var employee in UserPreferences.Instance.EmployeeAccessCodes)
            EmployeeListPanel.Children.Add(BuildEmployeeRow(employee));
    }

    /// <summary>2026-09-08: список показывает только имя + сколько кодов уже заполнено —
    /// подробности (сами коды, удаление) открываются по клику в EmployeeDetailDialog, а не
    /// разворачиваются прямо в списке (по просьбе владельца — раньше все карточки были
    /// развёрнуты сразу).</summary>
    private Control BuildEmployeeRow(EmployeeAccessCode employee)
    {
        var filledCount = new[] { employee.CartDeleteCode, employee.WarehouseDeleteCode, employee.ProductEditCode, employee.ProductAddCode }
            .Count(c => !string.IsNullOrWhiteSpace(c));

        var nameText = new TextBlock
        {
            Text = employee.Name.Length > 0 ? employee.Name : Tr.T("(без имени)", "(аты жок)", "(no name)", "(isimsiz)", "(ismisiz)"),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        var subtitleText = new TextBlock
        {
            Text = Tr.T($"Кодов заполнено: {filledCount}/4", $"Толтурулган коддор: {filledCount}/4",
                $"Codes set: {filledCount}/4", $"Doldurulan kodlar: {filledCount}/4", $"To'ldirilgan kodlar: {filledCount}/4"),
            FontSize = 12,
            Foreground = Brushes.Gray,
        };

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(nameText);
        textPanel.Children.Add(subtitleText);

        var chevron = new TextBlock { Text = "›", FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };

        var cardContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(chevron, 1);
        cardContent.Children.Add(textPanel);
        cardContent.Children.Add(chevron);

        var cardButton = new Button
        {
            Content = cardContent,
            Classes = { "SettingsCard" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Avalonia.Thickness(16, 14),
        };

        cardButton.Click += (_, _) =>
        {
            async System.Threading.Tasks.Task DeleteOnServer()
            {
                var authApi = AuthApi ?? throw new System.InvalidOperationException("API недоступен.");
                await authApi.DeleteEmployeeAsync(employee.ServerId!);
            }

            var dialog = new EmployeeDetailDialog(employee, employee.ServerId != null ? DeleteOnServer : null);
            var owner = TopLevel.GetTopLevel(this) as Window;
            PosDialogHost.Show(dialog, owner);

            if (dialog.DeleteRequested)
                UserPreferences.Instance.EmployeeAccessCodes.Remove(employee);

            RefreshList();
        };

        // 2026-09-21, по просьбе владельца ("клик по сотруднику открывал редактор доступов, как
        // на сайте"): отдельная кнопка-замочек РЯДОМ с карточкой (не ВНУТРИ неё — Button внутри
        // Button в Avalonia не изолирует клик надёжно: нажатие на вложенную кнопку срабатывало
        // ещё и как клик по всей карточке, открывая старый диалог с PIN-кодами вместо нового
        // редактора доступов, живой баг с фото от владельца). Здесь оба — соседние элементы
        // одного и того же внешнего Grid, а не один внутри другого. Доступна только для
        // сотрудников, реально созданных на сервере (ServerId задан).
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(cardButton, 0);
        outer.Children.Add(cardButton);

        if (employee.ServerId != null)
        {
            var accessButton = new Button
            {
                Content = "🔐",
                Classes = { "SettingsCard" },
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(6, 0, 0, 0),
                Padding = new Avalonia.Thickness(14, 0),
            };
            ToolTip.SetTip(accessButton, Tr.T("Доступы", "Доступтар", "Access", "Erişimler", "Huquqlar"));
            accessButton.Click += (_, _) => _ = OpenAccessEditorAsync(employee);
            Grid.SetColumn(accessButton, 1);
            outer.Children.Add(accessButton);
        }

        return outer;
    }

    /// <summary>Подгружает актуальные доступы сотрудника перед показом редактора — локальная
    /// запись EmployeeAccessCode их не хранит (только ServerId/имя/PIN-коды), поэтому свежий
    /// список запрашивается заново и сотрудник находится по ServerId (тот же приём, что уже
    /// используется в DeleteEmployeeAsync для проверки "правда ли удалилось").</summary>
    private async System.Threading.Tasks.Task OpenAccessEditorAsync(EmployeeAccessCode employee)
    {
        var authApi = AuthApi;
        if (authApi is null || employee.ServerId is null)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        List<EmployeeInfoDto>? employees;
        try
        {
            employees = await authApi.GetEmployeesAsync();
        }
        catch (ApiException ex)
        {
            ShowLoadStatus(ex.Message, isError: true);
            return;
        }

        var match = employees?.FirstOrDefault(e => e.Id == employee.ServerId);
        if (match is null)
        {
            ShowLoadStatus(
                Tr.T("Не удалось загрузить текущие доступы сотрудника с сервера.",
                    "Кызматкердин учурдагы доступторун сервертен жүктөө мүмкүн болгон жок.",
                    "Could not load the employee's current access from the server.",
                    "Personelin mevcut erişimleri sunucudan yüklenemedi.",
                    "Xodimning joriy huquqlarini serverdan yuklab bo'lmadi."),
                isError: true);
            return;
        }

        var dialog = new EmployeeAccessDialog(employee.Name, match.Access);
        var confirmed = PosDialogHost.Show(dialog, owner);
        if (confirmed != true)
            return;

        try
        {
            await authApi.UpdateEmployeeAccessAsync(employee.ServerId, dialog.AccessFlags);
            ShowLoadStatus(
                Tr.T($"Доступы «{employee.Name}» обновлены.", $"«{employee.Name}» доступтору жаңырды.",
                    $"Access for «{employee.Name}» updated.", $"«{employee.Name}» erişimleri güncellendi.",
                    $"«{employee.Name}» huquqlari yangilandi."),
                isError: false);
        }
        catch (ApiException ex)
        {
            ShowLoadStatus(ex.Message, isError: true);
        }
    }

    /// <summary>2026-09-08: создание сотрудника теперь идёт через сервер (форма "Новый
    /// сотрудник" на сайте требует Email/Имя/Фамилию/Роль) — локальная запись без ServerId
    /// добавляется только после успешного ответа сервера, чтобы не плодить "призрачных"
    /// сотрудников, которых нет на сайте.</summary>
    private async void AddEmployee_Click(object? sender, RoutedEventArgs e)
    {
        if (_roles.Count == 0)
        {
            ShowLoadStatus(
                Tr.T(
                    "Сначала создайте хотя бы одну роль (кнопка «+ Добавить роль» выше) — сервер требует роль для нового сотрудника.",
                    "Адегенде жок дегенде бир ролду түзүңүз (жогорудагы «+ Ролду кошуу» баскычы) — сервер жаңы кызматкер үчүн ролду талап кылат.",
                    "First create at least one role (the «+ Add role» button above) — the server requires a role for a new employee.",
                    "Önce en az bir rol oluşturun (yukarıdaki «+ Rol ekle» düğmesi) — sunucu yeni personel için rol gerektirir.",
                    "Avval kamida bitta rol yarating (yuqoridagi «+ Rol qo'shish» tugmasi) — server yangi xodim uchun rol talab qiladi."),
                isError: true);
            return;
        }

        var dialog = new AddEmployeeDialog(_roles);
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = PosDialogHost.Show(dialog, owner);
        if (confirmed != true)
            return;

        AddEmployeeButton.IsEnabled = false;
        try
        {
            var authApi = AuthApi;
            if (authApi is null)
                return;

            var created = await authApi.CreateEmployeeAsync(dialog.Email, dialog.FirstName, dialog.LastName, dialog.RoleId, dialog.AccessFlags);
            var name = created?.FullName ?? $"{dialog.FirstName} {dialog.LastName}".Trim();
            UserPreferences.Instance.EmployeeAccessCodes.Add(new EmployeeAccessCode
            {
                Name = name,
                ServerId = created?.Id,
                Email = created?.Email ?? dialog.Email,
                LoginPassword = created?.Password,
            });
            // 2026-09-08: пароль сервер показывает только один раз (в этом самом ответе) — сохраняем
            // на диск сразу, не дожидаясь кнопки "Сохранить", чтобы он не потерялся при закрытии окна.
            UserPreferences.Instance.SaveToDisk();
            RefreshList();

            if (!string.IsNullOrWhiteSpace(created?.Password))
                PosDialogHost.Show(new EmployeeCredentialsDialog(created.Email ?? dialog.Email, created.Password), owner);

            ShowLoadStatus(
                Tr.T(
                    $"Сотрудник «{name}» создан на сервере. Впишите ему коды доступа и нажмите «Сохранить».",
                    $"«{name}» сервердо түзүлдү. Ага коддорду киргизип, «Сактоо» баскычын басыңыз.",
                    $"Employee «{name}» created on the server. Enter their access codes and click «Save».",
                    $"«{name}» sunucuda oluşturuldu. Erişim kodlarını girin ve «Kaydet»e basın.",
                    $"«{name}» serverda yaratildi. Unga kirish kodlarini kiriting va «Saqlash»ni bosing."),
                isError: false);
        }
        catch (System.Exception ex)
        {
            ShowLoadStatus(
                Tr.T(
                    $"Не удалось создать сотрудника на сервере: {ex.Message}",
                    $"Кызматкерди сервердо түзүү мүмкүн болгон жок: {ex.Message}",
                    $"Couldn't create the employee on the server: {ex.Message}",
                    $"Personel sunucuda oluşturulamadı: {ex.Message}",
                    $"Xodimni serverda yaratib bo'lmadi: {ex.Message}"),
                isError: true);
        }
        finally
        {
            AddEmployeeButton.IsEnabled = true;
        }
    }

    /// <summary>2026-09-08: подтягивает реальных сотрудников компании с app.nurcrm.kg (раздел
    /// "Сотрудники" на сайте), чтобы владелец не вводил имена вручную. Точный API-эндпоинт не
    /// подтверждён (см. NurMarketApiClient.GetEmployeesAsync) — если сервер ответит не так, как
    /// ожидалось, покажем понятную ошибку вместо тихого "ничего не произошло".</summary>
    private async void LoadFromWeb_Click(object? sender, RoutedEventArgs e)
    {
        LoadFromWebButton.IsEnabled = false;
        LoadStatusPanel.IsVisible = false;
        try
        {
            var authApi = AuthApi;
            if (authApi is null)
                return;

            var employees = await authApi.GetEmployeesAsync();
            if (employees is null)
            {
                ShowLoadStatus(
                    Tr.T(
                        "Не удалось получить список сотрудников с сайта — сервер не ответил ожидаемыми данными. Обратитесь в поддержку.",
                        "Сайттан кызматкерлердин тизмесин алуу мүмкүн болгон жок — сервер күтүлгөн маалыматты кайтарган жок.",
                        "Couldn't load the employee list from the website — the server didn't return the expected data.",
                        "Web sitesinden personel listesi alınamadı — sunucu beklenen verileri döndürmedi.",
                        "Veb-saytdan xodimlar ro'yxatini olib bo'lmadi — server kutilgan ma'lumotlarni qaytarmadi."),
                    isError: true);
                return;
            }

            var existingNames = UserPreferences.Instance.EmployeeAccessCodes
                .Select(x => (x.Name ?? "").Trim())
                .Where(n => n.Length > 0)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var employee in employees)
            {
                var name = (employee.FullName ?? employee.Email ?? employee.Phone ?? "").Trim();
                if (name.Length == 0 || existingNames.Contains(name))
                    continue;

                UserPreferences.Instance.EmployeeAccessCodes.Add(new EmployeeAccessCode { Name = name, ServerId = employee.Id, Email = employee.Email });
                existingNames.Add(name);
                added++;
            }

            RefreshList();
            ShowLoadStatus(
                added > 0
                    ? Tr.T(
                        $"Добавлено сотрудников: {added}. Не забудьте нажать «Сохранить».",
                        $"Кошулган кызматкерлер: {added}. «Сактоо» баскычын басууну унутпаңыз.",
                        $"Added employees: {added}. Don't forget to click «Save».",
                        $"Eklenen personel: {added}. «Kaydet» düğmesine basmayı unutmayın.",
                        $"Qo'shilgan xodimlar: {added}. «Saqlash» tugmasini bosishni unutmang.")
                    : Tr.T(
                        "Новых сотрудников не найдено — все уже добавлены.",
                        "Жаңы кызматкерлер табылган жок — баары мурунтан кошулган.",
                        "No new employees found — everyone is already added.",
                        "Yeni personel bulunamadı — herkes zaten eklendi.",
                        "Yangi xodimlar topilmadi — hammasi allaqachon qo'shilgan."),
                isError: false);
        }
        catch (System.Exception ex)
        {
            ShowLoadStatus(
                Tr.T(
                    $"Ошибка при загрузке сотрудников: {ex.Message}",
                    $"Кызматкерлерди жүктөөдө ката: {ex.Message}",
                    $"Error loading employees: {ex.Message}",
                    $"Personel yüklenirken hata: {ex.Message}",
                    $"Xodimlarni yuklashda xatolik: {ex.Message}"),
                isError: true);
        }
        finally
        {
            LoadFromWebButton.IsEnabled = true;
        }
    }

    private void ShowLoadStatus(string text, bool isError)
    {
        LoadStatusText.Text = text;
        LoadStatusText.Foreground = ResolveStatusBrush(isError);
        // Показываем/прячем подложку целиком — сам текст внутри неё видим всегда.
        LoadStatusPanel.IsVisible = true;
    }

    private void ShowRolesStatus(string text, bool isError)
    {
        RolesStatusText.Text = text;
        RolesStatusText.Foreground = ResolveStatusBrush(isError);
        RolesStatusText.IsVisible = true;
    }

    private IBrush ResolveStatusBrush(bool isError)
    {
        var key = isError ? "BrushDanger" : "BrushSuccess";
        return Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : (isError ? Brushes.Red : Brushes.Green);
    }

    /// <summary>2026-09-08: список ролей компании — грузится сразу при открытии страницы, нужен
    /// и для отображения, и для выбора роли в диалоге создания сотрудника.</summary>
    private async System.Threading.Tasks.Task LoadRolesAsync()
    {
        try
        {
            var authApi = AuthApi;
            if (authApi is null)
                return;

            var roles = await authApi.GetRolesAsync();
            _roles = roles ?? new List<RoleInfoDto>();
            RefreshRolesList();
            if (roles is null)
            {
                ShowRolesStatus(
                    Tr.T(
                        "Не удалось получить роли с сайта — сервер не ответил ожидаемыми данными.",
                        "Сайттан ролдорду алуу мүмкүн болгон жок — сервер күтүлгөн маалыматты кайтарган жок.",
                        "Couldn't load roles from the website — the server didn't return the expected data.",
                        "Web sitesinden roller alınamadı — sunucu beklenen verileri döndürmedi.",
                        "Veb-saytdan rollarni olib bo'lmadi — server kutilgan ma'lumotlarni qaytarmadi."),
                    isError: true);
            }
        }
        catch (System.Exception ex)
        {
            ShowRolesStatus(
                Tr.T(
                    $"Ошибка при загрузке ролей: {ex.Message}",
                    $"Ролдорду жүктөөдө ката: {ex.Message}",
                    $"Error loading roles: {ex.Message}",
                    $"Roller yüklenirken hata: {ex.Message}",
                    $"Rollarni yuklashda xatolik: {ex.Message}"),
                isError: true);
        }
    }

    private void RefreshRolesList()
    {
        RolesListPanel.Children.Clear();
        foreach (var role in _roles)
            RolesListPanel.Children.Add(BuildRoleRow(role));
    }

    private Grid BuildRoleRow(RoleInfoDto role)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var nameText = new TextBlock { Text = role.Name ?? "?", VerticalAlignment = VerticalAlignment.Center };
        var deleteButton = new Button
        {
            Content = Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            Classes = { "btn-secondary" },
        };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(deleteButton, 1);
        row.Children.Add(nameText);
        row.Children.Add(deleteButton);

        deleteButton.Click += async (_, _) =>
        {
            if (role.Id is null)
                return;

            deleteButton.IsEnabled = false;
            try
            {
                var authApi = AuthApi;
                if (authApi is null)
                    return;

                await authApi.DeleteRoleAsync(role.Id);
                _roles.Remove(role);
                RefreshRolesList();
            }
            catch (System.Exception ex)
            {
                ShowRolesStatus(
                    Tr.T(
                        $"Не удалось удалить роль: {ex.Message}",
                        $"Ролду өчүрүү мүмкүн болгон жок: {ex.Message}",
                        $"Couldn't delete the role: {ex.Message}",
                        $"Rol silinemedi: {ex.Message}",
                        $"Rolni o'chirib bo'lmadi: {ex.Message}"),
                    isError: true);
                deleteButton.IsEnabled = true;
            }
        };

        return row;
    }

    private async void AddRole_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SimpleInputDialog(
            Tr.T("Новая роль", "Жаңы ролу", "New role", "Yeni rol", "Yangi rol"),
            Tr.T("Название роли", "Ролдун аты", "Role name", "Rol adı", "Rol nomi"));
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = PosDialogHost.Show(dialog, owner);
        if (confirmed != true)
            return;

        var name = dialog.Result.Trim();
        if (name.Length == 0)
            return;

        AddRoleButton.IsEnabled = false;
        try
        {
            var authApi = AuthApi;
            if (authApi is null)
                return;

            var created = await authApi.CreateRoleAsync(name);
            _roles.Add(created ?? new RoleInfoDto { Name = name });
            RefreshRolesList();
        }
        catch (System.Exception ex)
        {
            ShowRolesStatus(
                Tr.T(
                    $"Не удалось создать роль: {ex.Message}",
                    $"Ролду түзүү мүмкүн болгон жок: {ex.Message}",
                    $"Couldn't create the role: {ex.Message}",
                    $"Rol oluşturulamadı: {ex.Message}",
                    $"Rolni yaratib bo'lmadi: {ex.Message}"),
                isError: true);
        }
        finally
        {
            AddRoleButton.IsEnabled = true;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        UserPreferences.Instance.SaveToDisk();
        SavedNote.IsVisible = true;
    }
}
