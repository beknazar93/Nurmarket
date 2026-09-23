using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

public class SimpleInputDialog : Window
{
    public string Result { get; private set; } = "";
    private readonly TextBox _textBox;

    public SimpleInputDialog(string title, string message, bool optional = false)
    {
        Title = title;
        Width = 350;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var msgText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _textBox = new TextBox { Height = 28 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        if (!optional)
        {
            var btnCancel = new Button
            {
                Content = "Отмена",
                Width = 100,
                Height = 28,
            };
            btnCancel.Click += (_, _) => Close(false);
            buttons.Children.Add(btnCancel);
        }

        var btnOk = new Button
        {
            Content = optional ? "Пропустить" : "OK",
            IsDefault = true,
            Width = 100,
            Height = 28,
        };
        btnOk.Click += (_, _) =>
        {
            Result = _textBox.Text ?? "";
            Close(true);
        };
        buttons.Children.Add(btnOk);

        grid.Children.Add(msgText);
        grid.Children.Add(_textBox);
        grid.Children.Add(buttons);
        Grid.SetRow(msgText, 0);
        Grid.SetRow(_textBox, 1);
        Grid.SetRow(buttons, 2);

        Content = grid;
    }
}
