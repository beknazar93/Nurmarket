using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-08: карточка одного сотрудника открывается по клику из списка (владелец
/// просил список, а не все карточки развёрнутыми сразу) — здесь редактируются 4 персональных
/// кода доступа (см. EmployeeAccessGate) и, если сотрудник привязан к серверу (ServerId), можно
/// его удалить с сайта прямо отсюда. Правки полей применяются сразу к переданному объекту
/// EmployeeAccessCode "на лету" (как раньше в развёрнутой карточке) — сохранение на диск делает
/// кнопка "Сохранить" на самой странице Настройки → Сотрудники, эта форма её не вызывает.</summary>
public sealed class EmployeeDetailDialog : Window
{
    public bool DeleteRequested { get; private set; }

    private readonly Button _deleteButton;
    private readonly TextBlock _errorText;

    public EmployeeDetailDialog(EmployeeAccessCode employee, Func<Task>? onDelete)
    {
        Title = employee.Name.Length > 0 ? employee.Name : Tr.T("Сотрудник", "Кызматкер", "Employee", "Personel", "Xodim");
        Width = 420;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };

        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.Gray };

        var nameBox = new TextBox
        {
            Text = employee.Name,
            Watermark = Tr.T("Имя сотрудника", "Кызматкердин аты", "Employee name", "Personel adı", "Xodim ismi"),
            IsReadOnly = employee.ServerId != null,
        };
        nameBox.TextChanged += (_, _) =>
        {
            employee.Name = nameBox.Text ?? "";
            Title = employee.Name.Length > 0 ? employee.Name : Tr.T("Сотрудник", "Кызматкер", "Employee", "Personel", "Xodim");
        };

        TextBox CodeField(string label, string? value, Action<string> onChange)
        {
            panel.Children.Add(Label(label));
            var box = new TextBox { Text = value ?? "" };
            box.TextChanged += (_, _) => onChange(box.Text ?? "");
            panel.Children.Add(box);
            return box;
        }

        panel.Children.Add(Label(Tr.T("Имя", "Аты", "Name", "Ad", "Ismi")));
        panel.Children.Add(nameBox);

        if (!string.IsNullOrWhiteSpace(employee.Email))
            panel.Children.Add(BuildCopyableField(Tr.T("Логин", "Логин", "Login", "Giriş", "Login"), employee.Email));

        if (!string.IsNullOrWhiteSpace(employee.LoginPassword))
        {
            panel.Children.Add(BuildCopyableField(Tr.T("Пароль", "Пароль", "Password", "Şifre", "Parol"), employee.LoginPassword));
            panel.Children.Add(new TextBlock
            {
                Text = Tr.T(
                    "Пароль, который сервер выдал при создании — на сайте он больше не показывается.",
                    "Сервер түзгөндө берген пароль — сайтта ал кайра көрсөтүлбөйт.",
                    "The password the server issued at creation — it's no longer shown on the website.",
                    "Sunucunun oluşturma sırasında verdiği şifre — site üzerinde artık gösterilmiyor.",
                    "Server yaratishda bergan parol — saytda endi ko'rsatilmaydi."),
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -4, 0, 0),
            });
        }

        CodeField(
            Tr.T("Код на удаление из корзины", "Себеттен өчүрүү коду", "Cart-delete code", "Sepetten silme kodu", "Savatdan o'chirish kodi"),
            employee.CartDeleteCode, v => employee.CartDeleteCode = v);
        CodeField(
            Tr.T("Код на удаление со склада", "Кампадан өчүрүү коду", "Warehouse-delete code", "Depodan silme kodu", "Ombordan o'chirish kodi"),
            employee.WarehouseDeleteCode, v => employee.WarehouseDeleteCode = v);
        CodeField(
            Tr.T("Код на редактирование товара", "Товарды түзөтүү коду", "Product-edit code", "Ürün düzenleme kodu", "Mahsulotni tahrirlash kodi"),
            employee.ProductEditCode, v => employee.ProductEditCode = v);
        CodeField(
            Tr.T("Код на добавление товара", "Товар кошуу коду", "Product-add code", "Ürün ekleme kodu", "Mahsulot qo'shish kodi"),
            employee.ProductAddCode, v => employee.ProductAddCode = v);

        _errorText = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_errorText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _deleteButton = new Button
        {
            Content = Tr.T("Удалить сотрудника", "Кызматкерди өчүрүү", "Delete employee", "Personeli sil", "Xodimni o'chirish"),
            Foreground = Brushes.Red,
        };
        _deleteButton.Click += async (_, _) =>
        {
            if (onDelete is null)
            {
                DeleteRequested = true;
                Close(true);
                return;
            }

            _deleteButton.IsEnabled = false;
            _errorText.IsVisible = false;
            try
            {
                await onDelete();
                DeleteRequested = true;
                Close(true);
            }
            catch (Exception ex)
            {
                _errorText.Text = ex.Message;
                _errorText.IsVisible = true;
                _deleteButton.IsEnabled = true;
            }
        };

        var btnClose = new Button
        {
            Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"),
            IsDefault = true,
        };
        btnClose.Click += (_, _) => Close(false);

        buttons.Children.Add(_deleteButton);
        buttons.Children.Add(btnClose);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel };
    }

    private Grid BuildCopyableField(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var textPanel = new StackPanel { Spacing = 4 };
        textPanel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.Gray });
        textPanel.Children.Add(new TextBox { Text = value, IsReadOnly = true });
        Grid.SetColumn(textPanel, 0);

        var copyButton = new Button
        {
            Content = Tr.T("Копировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(copyButton, 1);
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(value);
        };

        grid.Children.Add(textPanel);
        grid.Children.Add(copyButton);
        return grid;
    }
}
