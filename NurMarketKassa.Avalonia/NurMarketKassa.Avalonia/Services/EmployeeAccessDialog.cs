using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-21, по прямой просьбе владельца ("клик по сотруднику открывал редактор
/// доступов, как «Управление доступами» на сайте"): редактирование доступов УЖЕ созданного
/// сотрудника — в отличие от AddEmployeeDialog (создание), здесь Email/Имя/Роль не меняются,
/// только чек-лист can_view_*. Тот же общий чек-лист, что и в AddEmployeeDialog, см.
/// EmployeeAccessChecklist. Текущие значения передаёт вызывающий код (EmployeesSettingsView) —
/// свежим GET api/users/employees/ и поиском по ServerId, см. doc-comment там же.</summary>
public sealed class EmployeeAccessDialog : Window
{
    public EmployeeAccessFlags AccessFlags { get; private set; } = new();

    private readonly EmployeeAccessChecklist.Boxes _accessBoxes;

    public EmployeeAccessDialog(string employeeName, EmployeeAccessFlags current)
    {
        Title = Tr.T($"Доступы: {employeeName}", $"Доступтар: {employeeName}", $"Access: {employeeName}", $"Erişimler: {employeeName}", $"Huquqlar: {employeeName}");
        Width = 560;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var (accessView, accessBoxes) = EmployeeAccessChecklist.Build(current);
        _accessBoxes = accessBoxes;
        accessView.Margin = new Thickness(16, 16, 16, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16),
        };
        var btnCancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), Width = 100, Height = 28 };
        btnCancel.Click += (_, _) => Close(false);
        var btnSave = new Button
        {
            Content = Tr.T("Сохранить доступы", "Доступторду сактоо", "Save access", "Erişimleri kaydet", "Huquqlarni saqlash"),
            IsDefault = true,
            Width = 160,
            Height = 28,
        };
        btnSave.Click += (_, _) =>
        {
            AccessFlags = EmployeeAccessChecklist.Read(_accessBoxes);
            Close(true);
        };
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnSave);

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(accessView, 0);
        Grid.SetRow(buttons, 1);
        root.Children.Add(accessView);
        root.Children.Add(buttons);

        Content = root;
    }
}
