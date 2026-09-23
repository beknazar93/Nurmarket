using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
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
        Title = Tr.T("Логин сотрудника", "Кызматкердин логини", "Employee login", "Personel girişi", "Xodim login ma'lumoti");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = Tr.T(
                "Запишите или скопируйте — повторно этот пароль не показывается.",
                "Жазып алыңыз же көчүрүңүз — бул пароль кайра көрсөтүлбөйт.",
                "Save or copy it now — this password won't be shown again.",
                "Şimdi kaydedin veya kopyalayın — bu şifre tekrar gösterilmeyecek.",
                "Hozir saqlang yoki nusxalang — bu parol qayta ko'rsatilmaydi."),
            FontSize = 12,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(BuildField(Tr.T("Логин", "Логин", "Login", "Giriş", "Login"), email));
        panel.Children.Add(BuildField(Tr.T("Пароль", "Пароль", "Password", "Şifre", "Parol"), password));

        var closeButton = new Button
        {
            Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"),
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        closeButton.Click += (_, _) => Close(true);
        panel.Children.Add(closeButton);

        Content = panel;
    }

    private Grid BuildField(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.Gray });
        var valueBox = new TextBox { Text = value, IsReadOnly = true };
        textPanel.Children.Add(valueBox);
        Grid.SetColumn(textPanel, 0);

        var copyButton = new Button { Content = Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(copyButton, 1);
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is IClipboard c)
                await c.SetTextAsync(value);
        };

        grid.Children.Add(textPanel);
        grid.Children.Add(copyButton);
        return grid;
    }
}
