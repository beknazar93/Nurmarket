using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Views;
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
        Title = Tr.T($"Доступы: {employeeName}", $"Укуктар: {employeeName}", $"Permissions: {employeeName}", $"Erişimler: {employeeName}", $"Huquqlar: {employeeName}");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (M 672) вместо системной рамки 560×620 — кнопки были 28 px,
        // Esc не закрывал. Чек-лист прокручивается в теле, «Отмена» и «Сохранить доступы» закреплены внизу; Esc и ✕ — «Отмена».
        SectionCards.AsModal(this, 672);

        var (accessView, accessBoxes) = EmployeeAccessChecklist.Build(current);
        _accessBoxes = accessBoxes;
        // Доступы — в общей прокрутке тела окна: своя прокрутка чек-листа внутри неё не нужна.
        var accessPanel = (Control)accessView.Content!;
        accessView.Content = null;

        var btnCancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        btnCancel.Click += (_, _) => Close(false);
        var btnSave = SectionCards.Primary(Tr.T("Сохранить доступы", "Укуктарды сактоо", "Save permissions", "Erişimleri kaydet", "Huquqlarni saqlash"), isDefault: true);
        btnSave.Click += (_, _) =>
        {
            AccessFlags = EmployeeAccessChecklist.Read(_accessBoxes);
            Close(true);
        };

        var shell = SectionCards.Shell(Title,
            Tr.T("Что сотрудник видит на сайте app.nurcrm.kg и в кассе.", "Кызматкер app.nurcrm.kg сайтында жана кассада эмнени көрөт.",
                "What the employee can see on app.nurcrm.kg and at the till.", "Personelin app.nurcrm.kg sitesinde ve kasada neleri göreceği.",
                "Xodim app.nurcrm.kg saytida va kassada nimani ko'radi."),
            accessPanel, SectionCards.Buttons(btnCancel, btnSave));
        shell.InitialFocus = _accessBoxes.Cashbox;
        Content = shell;
    }
}
