using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-08: сервер сам генерирует пароль нового сотрудника и возвращает его в ответе
/// на создание (подтверждено — сайт сразу показывает окно "Логин сотрудника" с логином/паролем и
/// кнопками копирования). Этот пароль показывается только один раз, сразу после создания — если
/// его не записать/не скопировать сейчас, потом его взять неоткуда (сервер его больше не
/// показывает). Повторяем то же самое поведение здесь.</summary>
public sealed class EmployeeCredentialsDialog : Window
{
    public EmployeeCredentialsDialog(string email, string password)
    {
        Title = Tr.T("Логин сотрудника", "Кызматкердин логини", "Employee login", "Giriş bilgileri", "Xodim logini");
        // 2026-10-22 (модалки, пакет 4): общая рамка ModalShell (S 512) вместо системной: кнопки 44+ px, Esc/✕/Enter —
        // «Готово», цвета темы вместо Gray. Предупреждение «пароль больше не покажется» — подзаголовком.
        SectionCards.AsModal(this, 512);

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(BuildField(Tr.T("Логин", "Логин", "Login", "Kullanıcı adı", "Login"), email));
        panel.Children.Add(BuildField(Tr.T("Пароль", "Сырсөз", "Password", "Şifre", "Parol"), password));

        var closeButton = SectionCards.Primary(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        closeButton.IsCancel = true;
        closeButton.Click += (_, _) => Close(true);

        var shell = SectionCards.Shell(Title,
            Tr.T(
                "Запишите или скопируйте — повторно этот пароль не показывается.",
                "Жазып алыңыз же көчүрүңүз — бул сырсөз кайра көрсөтүлбөйт.",
                "Save or copy it now — this password won't be shown again.",
                "Not alın veya kopyalayın — bu şifre bir daha gösterilmeyecek.",
                "Yozib oling yoki nusxalang — bu parol boshqa ko'rsatilmaydi."),
            panel, SectionCards.Buttons(closeButton));
        shell.InitialFocus = closeButton;
        Content = shell;
    }

    private Grid BuildField(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };

        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        caption.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumnSpan(caption, 2);
        var valueBox = SectionCards.Field(new TextBox { Text = value, IsReadOnly = true });
        Avalonia.Automation.AutomationProperties.SetName(valueBox, label);
        Grid.SetRow(valueBox, 1);

        var copyText = Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash");
        var copyButton = SectionCards.Secondary(copyText);
        copyButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetRow(copyButton, 1);
        Grid.SetColumn(copyButton, 1);
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is IClipboard c)
            {
                await c.SetTextAsync(value);
                // 2026-10-22 (модалки, пакет 4): видно, что скопировано.
                copyButton.Content = Tr.T("Скопировано ✓", "Көчүрүлдү ✓", "Copied ✓", "Kopyalandı ✓", "Nusxalandi ✓");
            }
        };

        grid.Children.Add(caption);
        grid.Children.Add(valueBox);
        grid.Children.Add(copyButton);
        return grid;
    }
}
