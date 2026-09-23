using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-08: диалог создания сотрудника на сервере (поля повторяют форму "Новый
/// сотрудник" на app.nurcrm.kg: Email, Имя, Фамилия, Роль — Филиал сознательно не запрашиваем,
/// на сайте он необязательный). Требует непустой список ролей — без ролей создавать сотрудника
/// нельзя (сервер их требует), вызывающий код должен сначала предложить создать роль.
/// 2026-09-21, по жалобе владельца ("на сайте при создании сотрудника есть доступы а у нас
/// нет"): добавлен полный чек-лист доступов (can_view_*), как в форме "Управление доступами" на
/// сайте — раньше все эти права при создании уходили жёстко false, сотрудник получал доступ
/// только через роль. Сама разметка чек-листа вынесена в EmployeeAccessChecklist — общая с
/// EmployeeAccessDialog (редактирование доступов уже созданного сотрудника).</summary>
public sealed class AddEmployeeDialog : Window
{
    public string Email { get; private set; } = "";
    public string FirstName { get; private set; } = "";
    public string LastName { get; private set; } = "";
    public string RoleId { get; private set; } = "";
    public EmployeeAccessFlags AccessFlags { get; private set; } = new();

    private readonly TextBox _emailBox;
    private readonly TextBox _firstNameBox;
    private readonly TextBox _lastNameBox;
    private readonly ComboBox _roleBox;
    private readonly TextBlock _errorText;
    private readonly EmployeeAccessChecklist.Boxes _accessBoxes;

    public AddEmployeeDialog(IReadOnlyList<RoleInfoDto> roles)
    {
        Title = Tr.T("Новый сотрудник", "Жаңы кызматкер", "New employee", "Yeni personel", "Yangi xodim");
        Width = 560;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.Gray };

        var topFields = new StackPanel { Margin = new Thickness(16, 16, 16, 0), Spacing = 10 };

        _emailBox = new TextBox { Watermark = "user@mail.com" };
        _firstNameBox = new TextBox();
        _lastNameBox = new TextBox();
        _roleBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = roles,
            PlaceholderText = Tr.T("Выберите роль", "Ролду тандаңыз", "Select a role", "Rol seçin", "Rolni tanlang"),
        };
        _roleBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RoleInfoDto>(
            (role, _) => new TextBlock { Text = role?.Name ?? "" });

        _errorText = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        topFields.Children.Add(Label("Email *"));
        topFields.Children.Add(_emailBox);
        topFields.Children.Add(Label(Tr.T("Имя *", "Аты *", "First name *", "Ad *", "Ismi *")));
        topFields.Children.Add(_firstNameBox);
        topFields.Children.Add(Label(Tr.T("Фамилия *", "Фамилиясы *", "Last name *", "Soyad *", "Familiyasi *")));
        topFields.Children.Add(_lastNameBox);
        topFields.Children.Add(Label(Tr.T("Роль *", "Ролу *", "Role *", "Rol *", "Roli *")));
        topFields.Children.Add(_roleBox);
        topFields.Children.Add(_errorText);

        var (accessView, accessBoxes) = EmployeeAccessChecklist.Build();
        _accessBoxes = accessBoxes;
        accessView.Margin = new Thickness(16, 8, 16, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16),
        };
        var btnCancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), Width = 100, Height = 28 };
        btnCancel.Click += (_, _) => Close(false);
        var btnCreate = new Button
        {
            Content = Tr.T("Создать", "Түзүү", "Create", "Oluştur", "Yaratish"),
            IsDefault = true,
            Width = 100,
            Height = 28,
        };
        btnCreate.Click += (_, _) => TryConfirm();
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnCreate);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(topFields, 0);
        Grid.SetRow(accessView, 1);
        Grid.SetRow(buttons, 2);
        root.Children.Add(topFields);
        root.Children.Add(accessView);
        root.Children.Add(buttons);

        Content = root;
    }

    private void TryConfirm()
    {
        var email = _emailBox.Text?.Trim() ?? "";
        var first = _firstNameBox.Text?.Trim() ?? "";
        var last = _lastNameBox.Text?.Trim() ?? "";
        var role = _roleBox.SelectedItem as RoleInfoDto;

        if (email.Length == 0 || first.Length == 0 || last.Length == 0 || role?.Id is null)
        {
            _errorText.Text = Tr.T(
                "Заполните Email, Имя, Фамилию и выберите роль.",
                "Email, Аты, Фамилиясын толтуруңуз жана ролду тандаңыз.",
                "Fill in Email, First name, Last name and select a role.",
                "Email, Ad, Soyad alanlarını doldurun ve rol seçin.",
                "Email, Ism, Familiya to'ldiring va rolni tanlang.");
            _errorText.IsVisible = true;
            return;
        }

        Email = email;
        FirstName = first;
        LastName = last;
        RoleId = role.Id;
        AccessFlags = EmployeeAccessChecklist.Read(_accessBoxes);

        Close(true);
    }
}
