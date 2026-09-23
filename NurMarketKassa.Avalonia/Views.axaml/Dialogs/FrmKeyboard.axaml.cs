using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class FrmKeyboard : Window
{
    public static FrmKeyboard? CurrentForm { get; private set; }
    public string? ResultText { get; set; }
    public static bool IsShift { get; set; }

    private bool _isEnglish = false;

    // Массивы символов для 3 буквенных рядов (RU и EN)
    private readonly string[][] _ruLayout = new string[][]
    {
        new[] { "Й", "Ц", "У", "К", "Е", "Н", "Г", "Ш", "Щ", "З", "Х", "Ъ" },
        new[] { "Ф", "Ы", "В", "А", "П", "Р", "О", "Л", "Д", "Ж", "Э" },
        new[] { "Я", "Ч", "С", "М", "И", "Т", "Ь", "Б", "Ю" }
    };

    private readonly string[][] _enLayout = new string[][]
    {
        new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]" },
        new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'" },
        new[] { "Z", "X", "C", "V", "B", "N", "M", ",", "." }
    };

    public FrmKeyboard()
    {
        InitializeComponent();
        CurrentForm = this;

        // Позволяет окну не перехватывать фокус с основного документа
        Focusable = false;
    }

    public static void ShowKeyboard(Window? owner = null, bool hideLetters = false)
    {
        if (CurrentForm == null)
        {
            CurrentForm = new FrmKeyboard();
            if (owner != null)
            {
                CurrentForm.Owner = owner;
            }
        }

        CurrentForm.Show();
    }

    public static void KillKeyboard()
    {
        CurrentForm?.Close();
        CurrentForm = null;
    }

    #region Event Handlers

    private void Key_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string text)
        {
            InsertText(text);

            // Если Shift был зажат разово — сбрасываем его после ввода буквы
            if (IsShift)
            {
                IsShift = false;
                UpdateKeyLabels();
            }
        }
    }

    private void Backspace_Click(object? sender, RoutedEventArgs e)
    {
        var textBox = GetTargetTextBox();
        if (textBox != null)
        {
            int caretIndex = textBox.CaretIndex;
            string currentText = textBox.Text ?? string.Empty;

            if (caretIndex > 0 && currentText.Length > 0)
            {
                textBox.Text = currentText.Remove(caretIndex - 1, 1);
                textBox.CaretIndex = caretIndex - 1;
            }
        }
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        var textBox = GetTargetTextBox();
        if (textBox != null)
        {
            textBox.Text = string.Empty;
            textBox.CaretIndex = 0;
        }
    }

    private void Space_Click(object? sender, RoutedEventArgs e)
    {
        InsertText(" ");
    }

    private void Enter_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void Caps_Click(object? sender, RoutedEventArgs e)
    {
        IsShift = !IsShift;
        UpdateKeyLabels();
    }

    private void ToggleLang_Click(object? sender, RoutedEventArgs e)
    {
        _isEnglish = !_isEnglish;

        var layoutLabel = this.FindControl<TextBlock>("LayoutLabel");
        if (layoutLabel != null)
        {
            layoutLabel.Text = _isEnglish ? "[EN]" : "[RU]";
        }

        UpdateKeyLayout();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    #endregion

    #region Helpers

    private void InsertText(string text)
    {
        var textBox = GetTargetTextBox();
        if (textBox != null)
        {
            int caretIndex = textBox.CaretIndex;
            string currentText = textBox.Text ?? string.Empty;

            // Корректно обрабатываем границы курсора
            if (caretIndex < 0 || caretIndex > currentText.Length)
            {
                caretIndex = currentText.Length;
            }

            textBox.Text = currentText.Insert(caretIndex, text);
            textBox.CaretIndex = caretIndex + text.Length;
        }
    }

    /// <summary>
    /// Ищет активный TextBox во владельце клавиатуры (Owner) или среди открытых окон
    /// </summary>
    private TextBox? GetTargetTextBox()
    {
        // Приводим WindowBase? к Window? с помощью явного приведения (каста)
        Window? targetWindow = Owner as Window;

        if (targetWindow == null && VisualRoot is Window window)
        {
            targetWindow = window;
        }

        // Если окно всё ещё null, пытаемся получить верхний уровень активного окна
        var topLevel = targetWindow ?? TopLevel.GetTopLevel(this) as Window;

        if (topLevel?.FocusManager?.GetFocusedElement() is TextBox focusedTextBox)
        {
            return focusedTextBox;
        }

        return null;
    }

    /// <summary>
    /// Обновление регистра текста на кнопках (Заглавные / Строчные)
    /// </summary>
    private void UpdateKeyLabels()
    {
        Grid?[] rows = new Grid?[]
        {
        this.FindControl<Grid>("Row1Grid"),
        this.FindControl<Grid>("Row2Grid"),
        this.FindControl<Grid>("Row3Grid")
        };

        foreach (var row in rows)
        {
            if (row == null) continue;

            foreach (var child in row.Children)
            {
                if (child is Button btn && btn.Classes.Contains("Key") && btn.Content is string currentText)
                {
                    btn.Content = IsShift ? currentText.ToUpper() : currentText.ToLower();
                }
            }
        }
    }

    /// <summary>
    /// Полное переключение раскладки символов (RU / EN) с учетом регистра
    /// </summary>
    private void UpdateKeyLayout()
    {
        var currentLayout = _isEnglish ? _enLayout : _ruLayout;
        Grid?[] rows = new Grid?[]
        {
        this.FindControl<Grid>("Row1Grid"),
        this.FindControl<Grid>("Row2Grid"),
        this.FindControl<Grid>("Row3Grid")
        };

        for (int r = 0; r < rows.Length; r++)
        {
            var rowGrid = rows[r];
            if (rowGrid == null) continue;

            var targetChars = currentLayout[r];
            int charIndex = 0;

            foreach (var child in rowGrid.Children)
            {
                if (child is Button btn && btn.Classes.Contains("Key"))
                {
                    if (charIndex < targetChars.Length)
                    {
                        string character = targetChars[charIndex];
                        btn.Content = IsShift ? character.ToUpper() : character.ToLower();
                        charIndex++;
                    }
                }
            }
        }
    }

    #endregion
}