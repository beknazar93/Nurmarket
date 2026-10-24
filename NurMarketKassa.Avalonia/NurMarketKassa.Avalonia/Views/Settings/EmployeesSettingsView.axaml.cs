using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
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
        // 2026-10-18, 6-й/7-й круг (П.3): облако этой компании пришло — список обновить (до этого чужой не показываем).
        // Подписка — только пока страница на экране.
        AttachedToVisualTree += (_, _) => SettingsCloudSync.Applied += OnCloudApplied;
        DetachedFromVisualTree += (_, _) => SettingsCloudSync.Applied -= OnCloudApplied;

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
            // 2026-10-20, аудит программы владельца: здесь 2 карточки, а в «Зарплате» 4 сотрудника — список кодов
            // не подтянут с сайта. При первом показе — только ЧТЕНИЕ списка с сайта: сколько сотрудников не добавлено
            // (без записи; добавляет по-прежнему «Загрузить с сайта» + «Сохранить»).
            if (AppMode.IsOwner)
                AttachedToVisualTree += (_, _) =>
                {
                    if (_siteCheckStarted)
                        return;
                    _siteCheckStarted = true;
                    _ = ShowMissingSiteEmployeesAsync();
                };
        }
    }

    private bool _siteCheckStarted;

    /// <summary>2026-10-20, аудит программы владельца: подсказка «на сайте ещё N сотрудников» — только чтение, список не меняет.</summary>
    private async System.Threading.Tasks.Task ShowMissingSiteEmployeesAsync()
    {
        try
        {
            var authApi = AuthApi;
            if (authApi is null || !EmployeeAccessGate.ListBelongsToCurrentCompany())
                return;
            var employees = await authApi.GetEmployeesAsync();
            if (employees is null || LoadStatusPanel.IsVisible)
                return;
            var codes = UserPreferences.Instance.EmployeeAccessCodes.ToList();
            var missing = employees
                .Where(x => x.Id == null || codes.All(c => c.ServerId != x.Id))
                .Select(x => (x.FullName ?? x.Email ?? x.Phone ?? "").Trim())
                .Where(n => n.Length > 0 && codes.All(c => !string.Equals((c.Name ?? "").Trim(), n, System.StringComparison.OrdinalIgnoreCase)))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0)
                return;
            var names = string.Join(", ", missing);
            ShowLoadStatus(Tr.T(
                    $"На сайте ещё сотрудников: {missing.Count} ({names}). Нажмите «Загрузить с сайта», чтобы добавить их сюда.",
                    $"Сайтта дагы кызматкерлер бар: {missing.Count} ({names}). Аларды бул жерге кошуу үчүн «Сайттан жүктөө» баскычын басыңыз.",
                    $"More employees on the website: {missing.Count} ({names}). Press “Load from website” to add them here.",
                    $"Sitede başka personel var: {missing.Count} ({names}). Buraya eklemek için «Siteden yükle»ye basın.",
                    $"Saytda yana xodimlar bor: {missing.Count} ({names}). Ularni bu yerga qo'shish uchun «Saytdan yuklash»ni bosing."),
                isError: false);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Сотрудники: проверка списка на сайте не удалась: {ex.Message}", "UI");
        }
    }

    private static IAuthApiService? AuthApi => App.AppHost?.Services.GetService<IAuthApiService>();

    private void OnCloudApplied() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshList);

    private void RefreshList()
    {
        EmployeeListPanel.Children.Clear();
        // 2026-10-18, 7-й круг (3b): пока список другой компании — добавлять в него нельзя (ушло бы не туда).
        var ownList = EmployeeAccessGate.ListBelongsToCurrentCompany();
        var waitTip = ownList
            ? null
            : Tr.T("Подождите загрузки сотрудников этого магазина с сервера.",
                "Бул дүкөндүн кызматкерлери серверден жүктөлгүчө күтө туруңуз.",
                "Wait until this store's employees are loaded from the server.",
                "Bu mağazanın personeli sunucudan yüklenene kadar bekleyin.",
                "Bu do'kon xodimlari serverdan yuklanguncha kuting.");
        AddEmployeeButton.IsEnabled = ownList;
        LoadFromWebButton.IsEnabled = ownList;
        ToolTip.SetTip(AddEmployeeButton, waitTip);
        ToolTip.SetTip(LoadFromWebButton, waitTip);
        // 2026-10-18, 6-й круг (П.3): список другой компании (вход в другой магазин на этом ПК) не показываем, пока не
        // пришло облако этой компании.
        if (!ownList)
        {
            EmployeeListPanel.Children.Add(new TextBlock
            {
                Text = Tr.T("Загрузка сотрудников этого магазина с сервера… Сотрудники других магазинов скрыты.",
                    "Бул дүкөндүн кызматкерлери серверден жүктөлүүдө… Башка дүкөндөрдүн кызматкерлери жашырылган.",
                    "Loading this store's employees from the server… Employees of other stores are hidden.",
                    "Bu mağazanın personeli sunucudan yükleniyor… Diğer mağazaların personeli gizlendi.",
                    "Bu do'kon xodimlari serverdan yuklanmoqda… Boshqa do'konlar xodimlari yashirilgan."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            });
            return;
        }
        foreach (var employee in UserPreferences.Instance.EmployeeAccessCodes.ToList())
            EmployeeListPanel.Children.Add(BuildEmployeeRow(employee));
        // 2026-10-19, этап О7: пустой список — подсказка, что делать (раньше было пусто между кнопками и «Сохранить»).
        if (EmployeeListPanel.Children.Count == 0)
        {
            var empty = new Border
            {
                Classes = { "SettingsCard" },
                Padding = new Avalonia.Thickness(16, 14),
                Child = new TextBlock
                {
                    Text = Tr.T("Сотрудников пока нет. Нажмите «Загрузить с сайта» — придут сотрудники из NurCRM, или «+ Добавить сотрудника».",
                        "Азырынча кызматкерлер жок. «Сайттан жүктөө» баскычын басыңыз — NurCRM кызматкерлери келет, же «+ Кызматкер кошуу».",
                        "No employees yet. Press “Load from website” to get employees from NurCRM, or “+ Add employee”.",
                        "Henüz personel yok. NurCRM personelini almak için «Siteden yükle»ye veya «+ Personel ekle»ye basın.",
                        "Hozircha xodimlar yo'q. NurCRM xodimlarini olish uchun «Saytdan yuklash»ni yoki «+ Xodim qo'shish»ni bosing."),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
            };
            ((TextBlock)empty.Child).Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            EmployeeListPanel.Children.Add(empty);
        }
    }

    /// <summary>2026-09-08: список показывает только имя + сколько кодов уже заполнено —
    /// подробности (сами коды, удаление) открываются по клику в EmployeeDetailDialog, а не
    /// разворачиваются прямо в списке (по просьбе владельца — раньше все карточки были
    /// развёрнуты сразу).</summary>
    /// <summary>2026-10-22 (аудит 1.17.59, П-Г): эта карточка — владелец/админ, который сейчас в программе (по id на сайте,
    /// по почте или по имени). Коды ему не нужны, поэтому «действия под кодом закрыты» про него не пишем.</summary>
    private static bool IsSignedInOwnerRow(EmployeeAccessCode employee)
    {
        var auth = AuthApi;
        if (!(AppMode.IsOwner || (auth is not null && PermissionService.IsOwnerOrAdmin(auth.UserPayload))))
            return false;
        var id = NurMarketKassa.PosApp.CurrentUserId;
        if (!string.IsNullOrWhiteSpace(id) && string.Equals(employee.ServerId, id, System.StringComparison.OrdinalIgnoreCase))
            return true;
        var name = NurMarketKassa.PosApp.CurrentUserDisplayName;
        return !string.IsNullOrWhiteSpace(name) && string.Equals((employee.Name ?? "").Trim(), name.Trim(), System.StringComparison.OrdinalIgnoreCase);
    }

    private Control BuildEmployeeRow(EmployeeAccessCode employee)
    {
        // 2026-10-18: коды хранятся хэшем — считаем по EmployeeAccessGate.
        var filledCount = EmployeeAccessGate.CountCodes(employee);

        var nameText = new TextBlock
        {
            Text = employee.Name.Length > 0 ? employee.Name : Tr.T("(без имени)", "(аты жок)", "(no name)", "(isimsiz)", "(ismsiz)"),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        // 2026-10-18, решение владельца: код короче 6 цифр (задан до правила) — пометка у карточки.
        var shortNote = EmployeeAccessGate.HasShortCode(employee)
            ? Tr.T(" · короткий код — замените на 6 цифр", " · кыска код — 6 цифрага алмаштырыңыз", " · short code — replace with 6 digits",
                " · kısa kod — 6 haneli ile değiştirin", " · qisqa kod — 6 raqamga almashtiring")
            : "";
        var subtitleText = new TextBlock
        {
            // 2026-10-18: кодов теперь пять (+ приём оплаты долга) — число берётся из EmployeeAccessGate.
            // 2026-10-19, этап О7: простыми словами («Кодов заполнено: 0/4» было непонятно), цвет — из темы (в тёмной читается).
            Text = (filledCount == 0
                ? (IsSignedInOwnerRow(employee)
                    // 2026-10-22 (аудит 1.17.59, П-Г): у владельца/админа, который сейчас в кассе, кодов нет и не нужно —
                    // он работает без кода; надпись «действия закрыты» его пугала.
                    ? Tr.T("Владелец — работает без личного кода", "Ээси — жеке кодсуз иштейт", "Owner — works without a personal code",
                        "Sahibi — kişisel kod olmadan çalışır", "Egasi — shaxsiy kodsiz ishlaydi")
                    : Tr.T("Личных кодов нет — действия под кодом ему закрыты", "Жеке коддор жок — код менен аракеттер жабык",
                        "No personal codes — code-protected actions are closed", "Kişisel kod yok — kodlu işlemler kapalı",
                        "Shaxsiy kodlar yo'q — kod bilan amallar yopiq"))
                : Tr.T($"Личные коды: {filledCount} из {EmployeeAccessGate.ActionCount}", $"Жеке коддор: {filledCount} / {EmployeeAccessGate.ActionCount}",
                    $"Personal codes: {filledCount} of {EmployeeAccessGate.ActionCount}", $"Kişisel kodlar: {filledCount} / {EmployeeAccessGate.ActionCount}",
                    $"Shaxsiy kodlar: {filledCount} / {EmployeeAccessGate.ActionCount}")) + shortNote,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        subtitleText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(nameText);
        textPanel.Children.Add(subtitleText);

        var chevron = new TextBlock { Text = "›", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

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
            Padding = new Avalonia.Thickness(16, 10),
        };
        AutomationProperties.SetName(cardButton, nameText.Text);

        cardButton.Click += (_, _) =>
        {
            async System.Threading.Tasks.Task DeleteOnServer()
            {
                var authApi = AuthApi ?? throw new System.InvalidOperationException("API недоступен.");
                await authApi.DeleteEmployeeAsync(employee.ServerId!);
            }

            // 2026-10-18, повторное ревью: правка карточки повышает версию списка (облако не вернёт старое).
            static string CardState(EmployeeAccessCode e) =>
                System.Text.Json.JsonSerializer.Serialize(EmployeeAccessGate.ToCloudSecure(new[] { e }));
            var before = CardState(employee);
            var dialog = new EmployeeDetailDialog(employee, employee.ServerId != null ? DeleteOnServer : null);
            var owner = TopLevel.GetTopLevel(this) as Window;
            PosDialogHost.Show(dialog, owner);
            // 2026-10-18, ревью безопасности: набранные в карточке коды — сразу в хэш (на UI-потоке, карточка закрыта).
            var typedCodes = EmployeeAccessGate.SecureStoredCodes(new List<EmployeeAccessCode> { employee });

            // 2026-10-18, 6-й круг (П.6): пока карточка была открыта, облако могло заменить объекты списка — удаляем
            // по ссылке, иначе по id на сайте / email / имени; версию повышаем, только если реально удалено.
            var removed = false;
            if (dialog.DeleteRequested)
            {
                var list = UserPreferences.Instance.EmployeeAccessCodes;
                var target = list.FirstOrDefault(x => ReferenceEquals(x, employee))
                             ?? (employee.ServerId != null ? list.FirstOrDefault(x => x.ServerId == employee.ServerId) : null)
                             ?? (!string.IsNullOrWhiteSpace(employee.Email)
                                 ? list.FirstOrDefault(x => string.Equals(x.Email, employee.Email, System.StringComparison.OrdinalIgnoreCase))
                                 : null)
                             ?? (employee.ServerId == null && string.IsNullOrWhiteSpace(employee.Email)
                                 // 7-й круг (3c): по имени — только если такое имя в списке одно
                                 ? (list.Where(x => x.ServerId == null && string.Equals((x.Name ?? "").Trim(), (employee.Name ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase)).ToList()
                                     is { Count: 1 } byName ? byName[0] : null)
                                 : null);
                removed = target != null && list.Remove(target);
            }
            if (removed || (!dialog.DeleteRequested && (typedCodes || CardState(employee) != before)))
                EmployeeAccessGate.MarkCodesChanged();

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
            // 2026-10-19, этап О7: подпись «Доступы» вместо одного значка-замка (было непонятно, что это).
            var accessText = Tr.T("Доступы", "Укуктар", "Permissions", "Erişimler", "Huquqlar");
            var accessButton = new Button
            {
                Content = accessText,
                Classes = { "SettingsCard" },
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
                Padding = new Avalonia.Thickness(16, 0),
            };
            ToolTip.SetTip(accessButton, Tr.T("Что сотруднику можно делать в кассе и на сайте", "Кызматкерге кассада жана сайтта эмне кылууга болот",
                "What the employee may do in the till and on the website", "Personelin kasada ve sitede neler yapabileceği",
                "Xodim kassada va saytda nima qila oladi"));
            AutomationProperties.SetName(accessButton, accessText + ": " + nameText.Text);
            accessButton.Click += (_, _) => _ = OpenAccessEditorAsync(employee);
            Grid.SetColumn(accessButton, 1);
            outer.Children.Add(accessButton);
        }
        else
        {
            // 2026-10-21 (пакет 6, п. 64): у сотрудника только этой кассы (нет на сайте) — та же кнопка, но неактивная и с
            // пояснением: правый край списка ровный, и видно, почему доступы не открываются.
            var accessText = Tr.T("Доступы", "Укуктар", "Permissions", "Erişimler", "Huquqlar");
            var placeholder = new Button
            {
                Content = accessText,
                Classes = { "SettingsCard" },
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
                Padding = new Avalonia.Thickness(16, 0),
                IsEnabled = false,
            };
            var why = Tr.T("Доступы — у сотрудников с сайта: нажмите «Загрузить с сайта»", "Укуктар — сайттагы кызматкерлерде: «Сайттан жүктөө» басыңыз",
                "Permissions are for employees from the website: press “Load from website”", "Erişimler siteden gelen personel içindir: «Siteden yükle»ye basın",
                "Huquqlar — saytdagi xodimlarda: «Saytdan yuklash»ni bosing");
            ToolTip.SetTip(placeholder, why);
            ToolTip.SetShowOnDisabled(placeholder, true);
            AutomationProperties.SetName(placeholder, accessText + ": " + why);
            Grid.SetColumn(placeholder, 1);
            outer.Children.Add(placeholder);
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
                    "Кызматкердин учурдагы укуктарын серверден жүктөө мүмкүн болгон жок.",
                    "Couldn't load the employee's current permissions from the server.",
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
                Tr.T($"Доступы «{employee.Name}» обновлены.", $"«{employee.Name}» укуктары жаңыртылды.",
                    $"Permissions for “{employee.Name}” updated.", $"«{employee.Name}» için erişimler güncellendi.",
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
                    "Адегенде кеминде бир роль түзүңүз (жогорудагы «+ Роль кошуу» баскычы) — сервер жаңы кызматкер үчүн роль талап кылат.",
                    "First create at least one role (the “+ Add role” button above) — the server requires a role for a new employee.",
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
            });
            EmployeeAccessGate.MarkCodesChanged(); // 2026-10-18: версия списка для облака
            // 2026-10-18, аудит: пароль сотрудника больше не сохраняется (раньше уходил и в общие настройки
            // компании в облаке, которые читает любой сотрудник) — его показывают один раз в окне ниже.
            UserPreferences.Instance.SaveToDisk();
            RefreshList();

            if (!string.IsNullOrWhiteSpace(created?.Password))
                PosDialogHost.Show(new EmployeeCredentialsDialog(created.Email ?? dialog.Email, created.Password), owner);

            ShowLoadStatus(
                Tr.T(
                    $"Сотрудник «{name}» создан на сервере. Впишите ему коды доступа и нажмите «Сохранить».",
                    $"«{name}» кызматкери серверде түзүлдү. Ага кирүү коддорун жазып, «Сактоо» баскычын басыңыз.",
                    $"Employee “{name}” created on the server. Enter their access codes and click “Save”.",
                    $"Personel «{name}» sunucuda oluşturuldu. Erişim kodlarını girip «Kaydet»e basın.",
                    $"«{name}» xodimi serverda yaratildi. Uning kirish kodlarini kiriting va «Saqlash»ni bosing."),
                isError: false);
        }
        catch (System.Exception ex)
        {
            ShowLoadStatus(
                Tr.T(
                    $"Не удалось создать сотрудника на сервере: {ex.Message}",
                    $"Кызматкерди серверде түзүү мүмкүн болгон жок: {ex.Message}",
                    $"Couldn't create the employee on the server: {ex.Message}",
                    $"Personel sunucuda oluşturulamadı: {ex.Message}",
                    $"Xodimni serverda yaratib bo'lmadi: {ex.Message}"),
                isError: true);
        }
        finally
        {
            AddEmployeeButton.IsEnabled = EmployeeAccessGate.ListBelongsToCurrentCompany(); // 2026-10-18: см. RefreshList
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
                        "Сайттан кызматкерлердин тизмесин алуу мүмкүн болгон жок — сервер күтүлгөн маалыматты кайтарган жок. Колдоо кызматына кайрылыңыз.",
                        "Couldn't load the employee list from the website — the server didn't return the expected data. Contact support.",
                        "Web sitesinden personel listesi alınamadı — sunucu beklenen verileri döndürmedi. Destek ekibine başvurun.",
                        "Saytdan xodimlar ro'yxatini olib bo'lmadi — server kutilgan ma'lumotlarni qaytarmadi. Qo'llab-quvvatlash xizmatiga murojaat qiling."),
                    isError: true);
                return;
            }

            var existingNames = UserPreferences.Instance.EmployeeAccessCodes
                .Select(x => (x.Name ?? "").Trim())
                .Where(n => n.Length > 0)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            static string SiteName(EmployeeInfoDto x) => (x.FullName ?? x.Email ?? x.Phone ?? "").Trim();
            static bool SameName(string? a, string b) => string.Equals((a ?? "").Trim(), b, System.StringComparison.OrdinalIgnoreCase);

            var added = 0;
            foreach (var employee in employees)
            {
                var name = SiteName(employee);
                if (employee.Id != null && UserPreferences.Instance.EmployeeAccessCodes.Any(x => x.ServerId == employee.Id))
                    continue;
                if (name.Length > 0 && existingNames.Contains(name))
                {
                    // 2026-10-18, аудит: личный код подходит только самому сотруднику (по id/email входа) — карточке,
                    // заведённой вручную с тем же именем, проставляем id и email с сайта, иначе её коды не сработают.
                    // Ревью безопасности: только если имя единственное и на сайте, и среди карточек кассы.
                    var manual = UserPreferences.Instance.EmployeeAccessCodes.Where(x => SameName(x.Name, name)).ToList();
                    if (manual.Count == 1 && manual[0].ServerId == null && employee.Id != null
                        && employees.Count(x => SameName(SiteName(x), name)) == 1)
                    {
                        manual[0].ServerId = employee.Id;
                        manual[0].Email ??= employee.Email;
                        added++;
                    }
                    continue;
                }
                if (name.Length == 0)
                    continue;

                UserPreferences.Instance.EmployeeAccessCodes.Add(new EmployeeAccessCode { Name = name, ServerId = employee.Id, Email = employee.Email });
                existingNames.Add(name);
                added++;
            }

            if (added > 0)
                EmployeeAccessGate.MarkCodesChanged(); // 2026-10-18: версия списка для облака
            RefreshList();
            // 2026-10-18, ревью безопасности: карточки с кодами, не связанные с сотрудником сайта, — их коды никому не
            // подходят (код проверяется по вошедшему кассиру). Показываем владельцу, какие это.
            var unlinked = UserPreferences.Instance.EmployeeAccessCodes
                .Where(x => x.ServerId == null && string.IsNullOrWhiteSpace(x.Email) && EmployeeAccessGate.CountCodes(x) > 0)
                .Select(x => string.IsNullOrWhiteSpace(x.Name) ? "?" : x.Name.Trim())
                .ToList();
            var unlinkedNote = unlinked.Count == 0
                ? ""
                : "\n" + Tr.T(
                    $"Не привязаны к сотрудникам сайта (их коды не сработают): {string.Join(", ", unlinked)}. Имя должно совпадать с сайтом и быть единственным — или удалите карточку и добавьте сотрудника заново.",
                    $"Сайттагы кызматкерлерге байланбаган (коддору иштебейт): {string.Join(", ", unlinked)}. Аты сайттагыдай жана жалгыз болушу керек — же карточканы өчүрүп, кызматкерди кайра кошуңуз.",
                    $"Not linked to website employees (their codes won't work): {string.Join(", ", unlinked)}. The name must match the website and be unique — or delete the card and add the employee again.",
                    $"Sitedeki personele bağlı değil (kodları çalışmaz): {string.Join(", ", unlinked)}. Ad sitedekiyle aynı ve tek olmalı — ya da kartı silip personeli yeniden ekleyin.",
                    $"Saytdagi xodimlarga bog'lanmagan (kodlari ishlamaydi): {string.Join(", ", unlinked)}. Ism saytdagidek va yagona bo'lishi kerak — yoki kartani o'chirib, xodimni qayta qo'shing.");
            ShowLoadStatus(
                (added > 0
                    ? Tr.T(
                        $"Добавлено сотрудников: {added}. Не забудьте нажать «Сохранить».",
                        $"Кошулган кызматкерлер: {added}. «Сактоо» баскычын басууну унутпаңыз.",
                        $"Employees added: {added}. Don't forget to click “Save”.",
                        $"Eklenen personel: {added}. «Kaydet» düğmesine basmayı unutmayın.",
                        $"Qo'shilgan xodimlar: {added}. «Saqlash» tugmasini bosishni unutmang.")
                    : Tr.T(
                        "Новых сотрудников не найдено — все уже добавлены.",
                        "Жаңы кызматкерлер табылган жок — баары мурунтан кошулган.",
                        "No new employees found — all have already been added.",
                        "Yeni personel bulunamadı — herkes zaten eklendi.",
                        "Yangi xodimlar topilmadi — hammasi allaqachon qo'shilgan.")) + unlinkedNote,
                isError: unlinked.Count > 0);
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
            LoadFromWebButton.IsEnabled = EmployeeAccessGate.ListBelongsToCurrentCompany(); // 2026-10-18: см. RefreshList
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
        // 2026-10-19, этап О7: роли свёрнуты — в шапке видно, сколько их.
        var title = this.TryFindResource("employees.rolesTitle", out var t) && t is string s ? s : "Роли";
        RolesHeaderText.Text = _roles.Count > 0 ? $"{title} · {_roles.Count}" : title;
    }

    private Control BuildRoleRow(RoleInfoDto role)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 44 };
        var nameText = new TextBlock { Text = role.Name ?? "?", VerticalAlignment = VerticalAlignment.Center };
        // 2026-10-19, этап О7: «Удалить» у роли — маленькая красная обводка (sec-outline danger), строки 44 с разделителем.
        var deleteButton = new Button
        {
            Content = Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            Classes = { "sec-outline", "danger" },
            MinHeight = 32,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            // 2026-10-18, аудит: роль «Владелец» удалять нельзя — кнопки нет.
            IsVisible = !IsOwnerRole(role),
        };
        AutomationProperties.SetName(deleteButton, Tr.T("Удалить роль", "Ролду өчүрүү", "Delete role", "Rolü sil", "Rolni o'chirish") + " " + (role.Name ?? ""));
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(deleteButton, 1);
        row.Children.Add(nameText);
        row.Children.Add(deleteButton);

        deleteButton.Click += async (_, _) =>
        {
            if (role.Id is null || IsOwnerRole(role))
                return;

            // 2026-10-18, аудит: подтверждение — раньше роль удалялась на сайте с одного нажатия.
            var owner = TopLevel.GetTopLevel(this) as Window;
            var roleName = role.Name ?? "?";
            var confirmed = await PosConfirmDialog.ShowModalAsync(owner,
                Tr.T("Удалить роль?", "Ролду өчүрөсүзбү?", "Delete the role?", "Rol silinsin mi?", "Rol o'chirilsinmi?"),
                Tr.T($"Точно удалить роль «{roleName}»? Она будет удалена и на сайте app.nurcrm.kg. Это нельзя отменить.",
                    $"«{roleName}» ролу чын эле өчүрүлсүнбү? Ал app.nurcrm.kg сайтынан да өчүрүлөт. Муну артка кайтарууга болбойт.",
                    $"Really delete the role “{roleName}”? It will also be deleted on app.nurcrm.kg. This cannot be undone.",
                    $"«{roleName}» rolü gerçekten silinsin mi? app.nurcrm.kg sitesinden de silinecek. Bu geri alınamaz.",
                    $"«{roleName}» roli rostdan o'chirilsinmi? U app.nurcrm.kg saytidan ham o'chiriladi. Buni bekor qilib bo'lmaydi."),
                Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                PosConfirmAccent.Danger).ConfigureAwait(true);
            if (!confirmed)
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

        var line = new Border { Child = row, BorderThickness = new Avalonia.Thickness(0, 0, 0, 1), Padding = new Avalonia.Thickness(0, 2) };
        line.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        return line;
    }

    /// <summary>2026-10-18: системная роль «Владелец» (на сайте так и называется) — удалять нельзя.</summary>
    private static bool IsOwnerRole(RoleInfoDto role)
    {
        var name = (role.Name ?? "").Trim();
        return string.Equals(role.Id, "owner", System.StringComparison.OrdinalIgnoreCase)
               || name.Equals("Владелец", System.StringComparison.OrdinalIgnoreCase)
               || name.Equals("Owner", System.StringComparison.OrdinalIgnoreCase)
               || name.Equals("Ээси", System.StringComparison.OrdinalIgnoreCase);
    }

    private async void AddRole_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SimpleInputDialog(
            Tr.T("Новая роль", "Жаңы роль", "New role", "Yeni rol", "Yangi rol"),
            Tr.T("Название роли", "Ролдун аты", "Role name", "Rol adı", "Rol nomi"),
            // 2026-10-22 (модалки, пакет 4): глагол вместо «OK».
            confirmText: Tr.T("Создать роль", "Роль түзүү", "Create role", "Rol oluştur", "Rol yaratish"));
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
        // 2026-10-18, ревью безопасности: набранные коды — в хэш до записи (на UI-потоке).
        EmployeeAccessGate.SecureStoredCodes(UserPreferences.Instance.EmployeeAccessCodes);
        UserPreferences.Instance.SaveToDisk();
        SavedNote.IsVisible = true;
    }
}
