using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-28: «Данные сотрудника» — правка имени, фамилии, телефона, номера и роли уже созданного сотрудника
/// (PATCH api/users/employees/{id}/). Почта после создания не меняется (в EmployeeUpdate её нет). <see cref="Changes"/> содержит
/// ТОЛЬКО изменённые поля: не тронутое владельцем сервер не получает, чужая правка с другого устройства не затирается.</summary>
public sealed class EditEmployeeDialog : Window
{
    public Dictionary<string, object?> Changes { get; private set; } = new();

    private readonly EmployeeInfoDto _current;
    private readonly TextBox _firstBox, _lastBox, _phoneBox, _trackBox;
    private readonly ComboBox _roleBox;
    private readonly TextBlock _errorText;

    public EditEmployeeDialog(EmployeeInfoDto current, IReadOnlyList<RoleInfoDto> roles)
    {
        _current = current;
        Title = Tr.T("Данные сотрудника", "Кызматкердин маалыматтары", "Employee details", "Personel bilgileri", "Xodim ma'lumotlari");
        SectionCards.AsModal(this, 512);

        TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return label;
        }

        _firstBox = SectionCards.Field(new TextBox { Text = current.FirstName ?? "", MaxLength = 64 });
        _lastBox = SectionCards.Field(new TextBox { Text = current.LastName ?? "", MaxLength = 64 });
        _phoneBox = SectionCards.Field(new TextBox { Text = current.Phone ?? "", MaxLength = 32, Watermark = "+996 700 123 456" });
        _trackBox = SectionCards.Field(new TextBox { Text = current.TrackNumber ?? "", MaxLength = 64 });
        var emailBox = SectionCards.Field(new TextBox { Text = current.Email ?? "", IsReadOnly = true });
        _roleBox = SectionCards.Field(new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = roles,
            SelectedItem = roles.FirstOrDefault(r => r.Id != null && r.Id == current.RoleId),
            PlaceholderText = current.RoleName ?? Tr.T("Выберите роль", "Ролду тандаңыз", "Select a role", "Rol seçin", "Rolni tanlang"),
        });
        _roleBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RoleInfoDto>((role, _) => new TextBlock { Text = role?.Name ?? "" });
        _errorText = SectionCards.ErrorText(this);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Label("Email"));
        panel.Children.Add(emailBox);
        panel.Children.Add(Label(Tr.T("Имя *", "Аты *", "First name *", "Ad *", "Ismi *")));
        panel.Children.Add(_firstBox);
        panel.Children.Add(Label(Tr.T("Фамилия *", "Фамилиясы *", "Last name *", "Soyad *", "Familiyasi *")));
        panel.Children.Add(_lastBox);
        panel.Children.Add(Label(Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon")));
        panel.Children.Add(_phoneBox);
        panel.Children.Add(Label(Tr.T("Номер сотрудника", "Кызматкердин номери", "Employee number", "Personel numarası", "Xodim raqami")));
        panel.Children.Add(_trackBox);
        panel.Children.Add(Label(Tr.T("Роль", "Ролу", "Role", "Rol", "Roli")));
        panel.Children.Add(_roleBox);
        panel.Children.Add(_errorText);

        var btnCancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        btnCancel.Click += (_, _) => Close(false);
        var btnSave = SectionCards.Primary(Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        btnSave.Click += (_, _) => TryConfirm();

        var shell = SectionCards.Shell(Title,
            Tr.T("Меняется и на сайте app.nurcrm.kg. Отправляются только изменённые поля.", "app.nurcrm.kg сайтында да өзгөрөт. Өзгөргөн маалыматтар гана жөнөтүлөт.",
                "Also changed on app.nurcrm.kg. Only changed fields are sent.", "app.nurcrm.kg sitesinde de değişir. Yalnızca değişen alanlar gönderilir.",
                "app.nurcrm.kg saytida ham o'zgaradi. Faqat o'zgargan maydonlar yuboriladi."),
            panel, SectionCards.Buttons(btnCancel, btnSave));
        shell.InitialFocus = _firstBox;
        Content = shell;
    }

    private void TryConfirm()
    {
        var first = (_firstBox.Text ?? "").Trim();
        var last = (_lastBox.Text ?? "").Trim();
        var phone = (_phoneBox.Text ?? "").Trim();
        var track = (_trackBox.Text ?? "").Trim();
        if (first.Length == 0 || last.Length == 0)
        {
            SectionCards.ShowError(_errorText, Tr.T("Имя и фамилия не могут быть пустыми.", "Аты жана фамилиясы бош болбошу керек.",
                "First and last name cannot be empty.", "Ad ve soyad boş olamaz.", "Ism va familiya bo'sh bo'lmasligi kerak."), first.Length == 0 ? _firstBox : _lastBox);
            return;
        }
        if (phone.Length > 0 && !EmployeeFieldValidation.IsPhone(phone))
        {
            SectionCards.ShowError(_errorText, EmployeeFieldValidation.PhoneError, _phoneBox);
            return;
        }

        var changes = new Dictionary<string, object?>();
        if (first != (_current.FirstName ?? ""))
            changes["first_name"] = first;
        if (last != (_current.LastName ?? ""))
            changes["last_name"] = last;
        if (phone != (_current.Phone ?? ""))
            changes["phone_number"] = phone;
        if (track != (_current.TrackNumber ?? ""))
            changes["track_number"] = track;
        if (_roleBox.SelectedItem is RoleInfoDto role && role.Id != null && role.Id != _current.RoleId)
            changes["custom_role"] = role.Id;
        Changes = changes;
        Close(true);
    }
}
