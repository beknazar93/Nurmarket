using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Ввод одной строки (например, «Новая роль» в Настройки → Сотрудники).
/// 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (S 512) вместо системной 350×200 — кнопки были 28 px и
/// без перевода («Отмена/OK/Пропустить»), без Esc и без фокуса. Теперь: подпись над полем, поле 44 px с фокусом,
/// Esc/✕ — «Отмена», Enter — главная (глагол передаёт вызывающий код, по умолчанию «Готово»), пустое поле — ошибка под ним.</summary>
public class SimpleInputDialog : Window
{
    public string Result { get; private set; } = "";
    private readonly TextBox _textBox;

    public SimpleInputDialog(string title, string message, bool optional = false, string? confirmText = null)
    {
        Title = title;
        SectionCards.AsModal(this, 512);

        var label = new TextBlock { Text = message, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _textBox = SectionCards.Field(new TextBox());
        Avalonia.Automation.AutomationProperties.SetName(_textBox, message);
        var error = SectionCards.ErrorText(this);
        _textBox.TextChanged += (_, _) => SectionCards.ShowError(error, null);

        var buttons = new System.Collections.Generic.List<Button>();
        if (!optional)
        {
            var btnCancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
            btnCancel.Click += (_, _) => Close(false);
            buttons.Add(btnCancel);
        }

        var btnOk = SectionCards.Primary(optional
            ? Tr.T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish")
            : confirmText ?? Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        // Необязательный ввод: одна кнопка — она же Esc и ✕ (как раньше, с тем, что успели ввести).
        btnOk.IsCancel = optional;
        btnOk.Click += (_, _) =>
        {
            var text = _textBox.Text ?? "";
            if (!optional && text.Trim().Length == 0)
            {
                SectionCards.ShowError(error, Tr.T("Поле не заполнено", "Талаа толтурулган жок", "The field is empty", "Alan boş", "Maydon to'ldirilmagan"), _textBox);
                return;
            }
            Result = text;
            Close(true);
        };
        buttons.Add(btnOk);

        var shell = SectionCards.Shell(title, null,
            new StackPanel { Spacing = 8, Children = { label, _textBox, error } },
            SectionCards.Buttons(buttons.ToArray()));
        shell.InitialFocus = _textBox;
        Content = shell;
    }
}
