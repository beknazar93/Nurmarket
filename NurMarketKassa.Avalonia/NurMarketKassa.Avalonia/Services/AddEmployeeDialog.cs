using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
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
    /// <summary>2026-10-28: телефон и номер (track_number) — необязательные поля схемы сервера.</summary>
    public string Phone { get; private set; } = "";
    public string TrackNumber { get; private set; } = "";
    public EmployeeAccessFlags AccessFlags { get; private set; } = new();

    private readonly TextBox _emailBox;
    private readonly TextBox _firstNameBox;
    private readonly TextBox _lastNameBox;
    private readonly ComboBox _roleBox;
    private readonly TextBox _phoneBox;
    private readonly TextBox _trackBox;
    private readonly TextBlock _errorText;
    private readonly EmployeeAccessChecklist.Boxes _accessBoxes;

    public AddEmployeeDialog(IReadOnlyList<RoleInfoDto> roles)
    {
        Title = Tr.T("Новый сотрудник", "Жаңы кызматкер", "New employee", "Yeni personel", "Yangi xodim");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (M 672) вместо системной рамки Windows 560×680 — кнопки
        // были 28 px, Esc не закрывал, цвета Gray/Red не менялись с темой. Поля и доступы прокручиваются, «Отмена» и
        // «Создать» закреплены внизу (видны на 910×512); Esc и ✕ — «Отмена»; фокус — в Email; ошибка — под полями.
        SectionCards.AsModal(this, 672);

        TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return label;
        }

        var topFields = new StackPanel { Spacing = 8 };

        _emailBox = SectionCards.Field(new TextBox { Watermark = "user@mail.com" });
        _firstNameBox = SectionCards.Field(new TextBox());
        _lastNameBox = SectionCards.Field(new TextBox());
        _roleBox = SectionCards.Field(new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = roles,
            PlaceholderText = Tr.T("Выберите роль", "Ролду тандаңыз", "Select a role", "Rol seçin", "Rolni tanlang"),
        });
        _roleBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RoleInfoDto>(
            (role, _) => new TextBlock { Text = role?.Name ?? "" });

        _phoneBox = SectionCards.Field(new TextBox { Watermark = "+996 700 123 456", MaxLength = 32 });
        _trackBox = SectionCards.Field(new TextBox { MaxLength = 64 });
        _errorText = SectionCards.ErrorText(this);

        topFields.Children.Add(Label("Email *"));
        topFields.Children.Add(_emailBox);
        topFields.Children.Add(Label(Tr.T("Имя *", "Аты *", "First name *", "Ad *", "Ismi *")));
        topFields.Children.Add(_firstNameBox);
        topFields.Children.Add(Label(Tr.T("Фамилия *", "Фамилиясы *", "Last name *", "Soyad *", "Familiyasi *")));
        topFields.Children.Add(_lastNameBox);
        topFields.Children.Add(Label(Tr.T("Роль *", "Ролу *", "Role *", "Rol *", "Roli *")));
        topFields.Children.Add(_roleBox);
        topFields.Children.Add(Label(Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon")));
        topFields.Children.Add(_phoneBox);
        topFields.Children.Add(Label(Tr.T("Номер сотрудника (необязательно)", "Кызматкердин номери (милдеттүү эмес)", "Employee number (optional)", "Personel numarası (isteğe bağlı)", "Xodim raqami (majburiy emas)")));
        topFields.Children.Add(_trackBox);
        topFields.Children.Add(_errorText);

        var (accessView, accessBoxes) = EmployeeAccessChecklist.Build();
        _accessBoxes = accessBoxes;
        // Доступы — в общей прокрутке тела окна: своя прокрутка чек-листа внутри неё не нужна.
        var accessPanel = (Control)accessView.Content!;
        accessView.Content = null;

        var btnCancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        btnCancel.Click += (_, _) => Close(false);
        var btnCreate = SectionCards.Primary(Tr.T("Создать", "Түзүү", "Create", "Oluştur", "Yaratish"), isDefault: true);
        btnCreate.Click += (_, _) => TryConfirm();

        var shell = SectionCards.Shell(Title,
            Tr.T("Сотрудник будет создан и на сайте app.nurcrm.kg.", "Кызматкер app.nurcrm.kg сайтында да түзүлөт.",
                "The employee will also be created on app.nurcrm.kg.", "Personel app.nurcrm.kg sitesinde de oluşturulacak.",
                "Xodim app.nurcrm.kg saytida ham yaratiladi."),
            new StackPanel { Spacing = 8, Children = { topFields, accessPanel } },
            SectionCards.Buttons(btnCancel, btnCreate));
        shell.InitialFocus = _emailBox;
        Content = shell;
    }

    private void TryConfirm()
    {
        var email = _emailBox.Text?.Trim() ?? "";
        var first = _firstNameBox.Text?.Trim() ?? "";
        var last = _lastNameBox.Text?.Trim() ?? "";
        var role = _roleBox.SelectedItem as RoleInfoDto;

        if (email.Length == 0 || first.Length == 0 || last.Length == 0 || role?.Id is null)
        {
            // 2026-10-22 (модалки, пакет 4): ошибка под полями, фокус — в первое незаполненное.
            Control firstEmpty = email.Length == 0 ? _emailBox : first.Length == 0 ? _firstNameBox : last.Length == 0 ? _lastNameBox : _roleBox;
            SectionCards.ShowError(_errorText, Tr.T(
                "Заполните Email, Имя, Фамилию и выберите роль.",
                "Email'ди, атын жана фамилиясын толтуруп, ролду тандаңыз.",
                "Fill in the email, first name and last name, and select a role.",
                "E-posta, Ad ve Soyad alanlarını doldurun ve bir rol seçin.",
                "Email, ism va familiyani kiriting hamda rolni tanlang."), firstEmpty);
            return;
        }

        // 2026-10-28: проверка формата до отправки — почта и телефон (раньше сервер отвечал сырым текстом ошибки).
        if (!EmployeeFieldValidation.IsEmail(email))
        {
            SectionCards.ShowError(_errorText, EmployeeFieldValidation.EmailError, _emailBox);
            return;
        }
        var phone = (_phoneBox.Text ?? "").Trim();
        if (phone.Length > 0 && !EmployeeFieldValidation.IsPhone(phone))
        {
            SectionCards.ShowError(_errorText, EmployeeFieldValidation.PhoneError, _phoneBox);
            return;
        }

        Email = email;
        Phone = phone;
        TrackNumber = (_trackBox.Text ?? "").Trim();
        FirstName = first;
        LastName = last;
        RoleId = role.Id;
        AccessFlags = EmployeeAccessChecklist.Read(_accessBoxes);

        Close(true);
    }
}
