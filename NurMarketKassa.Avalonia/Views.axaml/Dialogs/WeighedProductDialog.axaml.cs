using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class WeighedProductDialog : Window
{
    private readonly IWeightScaleService? _scale;
    private readonly bool _scaleLive;
    private readonly DispatcherTimer? _timer;

    public string QuantityNormalized { get; private set; } = "";

    public WeighedProductDialog() : this("", "", null) { }

    public WeighedProductDialog(
        string productTitle,
        string pricePerKgLine,
        IWeightScaleService? scale,
        string? initialKg = null,
        string okButtonText = "В чек",
        string? windowTitle = null)
    {
        InitializeComponent();
        _scale = scale;
        _scaleLive = HasLiveScaleConnection(scale);

        var title = string.IsNullOrEmpty(windowTitle) ? "Взвесить товар" : windowTitle;
        Title = title;

        // Присвоение значения шапке диалога
        HeaderTitleText.Text = string.IsNullOrEmpty(productTitle) ? title : $"Взвесить: {productTitle}";
        OkButton.Content = okButtonText;
        PriceBlock.Text = string.IsNullOrEmpty(pricePerKgLine) ? "" : "Цена за кг: " + pricePerKgLine;

        // Кнопка не нужна в обоих случаях: если весы подключены — вес подставляется сам
        // (см. таймер ниже); если весов нет, кнопке всё равно нечего подставлять
        // (она только сообщала "весы не подключены") — вес тогда вводится вручную с нумпада.
        if (FromScaleButton != null)
        {
            FromScaleButton.IsVisible = false;
        }
        if (LiveScaleBlock != null)
        {
            LiveScaleBlock.IsVisible = _scaleLive;
        }

        WeightBox.Text = !string.IsNullOrEmpty(initialKg) ? initialKg : "0.00";
        LiveScaleText.Text = FormatLiveScaleText();

        if (_scaleLive)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) =>
            {
                LiveScaleText.Text = FormatLiveScaleText();
                if (_scale?.LastWeight is double w && w > 0)
                    WeightBox.Text = w.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            };
            Opened += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }

        Opened += (_, _) =>
        {
            WeightBox.Focus();
            WeightBox.SelectAll();
        };
    }

    #region Обработка ввода с On-Screen клавиатуры (Нумпада)

    /// <summary>
    /// Обработчик нажатия на цифры и разделитель (,)
    /// </summary>
    private void OnNumKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        string inputChar = (btn.Tag ?? btn.Content)?.ToString() ?? "";
        if (inputChar.Length == 0) return;

        // В Avalonia используем SelectionStart и SelectionEnd
        int selStart = WeightBox.SelectionStart;
        int selEnd = WeightBox.SelectionEnd;

        int start = Math.Min(selStart, selEnd);
        int end = Math.Max(selStart, selEnd);
        int selectionLength = end - start;

        string currentText = WeightBox.Text ?? "";

        // Если в тексте уже есть точка/запятая, запрещаем ввод второй
        if ((inputChar == "," || inputChar == ".") &&
            (currentText.Contains(',') || currentText.Contains('.')) &&
            selectionLength == 0)
        {
            return;
        }

        // Если весь текст выделен или поле содержит "0.00" / "0", заменяем новым вводом
        if (selectionLength == currentText.Length || currentText == "0.00" || currentText == "0")
        {
            if (inputChar == ",") inputChar = "0,";
            WeightBox.Text = inputChar;
            WeightBox.SelectionStart = WeightBox.Text.Length;
            WeightBox.SelectionEnd = WeightBox.Text.Length;
        }
        else
        {
            // Вставляем символ с учетом текущего выделения
            string newText = currentText.Remove(start, selectionLength).Insert(start, inputChar);
            WeightBox.Text = newText;
            WeightBox.SelectionStart = start + inputChar.Length;
            WeightBox.SelectionEnd = start + inputChar.Length;
        }

        WeightBox.Focus();
    }

    /// <summary>
    /// Обработчик кнопки удаления (Backspace / ⌫)
    /// </summary>
    private void OnBackspace_Click(object? sender, RoutedEventArgs e)
    {
        string currentText = WeightBox.Text ?? "";
        int selStart = WeightBox.SelectionStart;
        int selEnd = WeightBox.SelectionEnd;

        int start = Math.Min(selStart, selEnd);
        int end = Math.Max(selStart, selEnd);
        int selectionLength = end - start;

        if (string.IsNullOrEmpty(currentText)) return;

        if (selectionLength > 0)
        {
            // Удаляем выделенный фрагмент
            WeightBox.Text = currentText.Remove(start, selectionLength);
            WeightBox.SelectionStart = start;
            WeightBox.SelectionEnd = start;
        }
        else if (start > 0)
        {
            // Удаляем один символ слева от курсора
            WeightBox.Text = currentText.Remove(start - 1, 1);
            WeightBox.SelectionStart = start - 1;
            WeightBox.SelectionEnd = start - 1;
        }

        if (string.IsNullOrEmpty(WeightBox.Text))
        {
            WeightBox.Text = "0";
            WeightBox.SelectAll();
        }

        WeightBox.Focus();
    }

    #endregion

    /// <summary>
    /// Обработчик для кнопок пресетов (0,1 кг, 0,5 кг, 1 кг и т.д.)
    /// Назначьте Click="OnQuickWeight_Click" для пресетов
    /// </summary>
    private void OnQuickWeight_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var rawContent = (btn.Tag ?? btn.Content)?.ToString()?.Trim() ?? "";
        if (rawContent.Length == 0) return;

        WeightBox.Text = rawContent;
        WeightBox.Focus();
        WeightBox.SelectAll();
    }

    #region Логика работы с весами и закрытием

    private static bool HasLiveScaleConnection(IWeightScaleService? scale)
    {
        if (!HardwareModeHelper.UsePhysicalScale())
            return false;
        if (scale is VirtualWeightScaleService)
            return false;
        return scale is ComWeightScaleService { IsAvailable: true };
    }

    private string FormatLiveScaleText()
    {
        if (!_scaleLive)
            return "—";
        return _scale?.LastWeight is double w
            ? w.ToString("0.00", CultureInfo.InvariantCulture) + " кг"
            : "0.00 кг";
    }

    private void FromScale_Click(object? sender, RoutedEventArgs e)
    {
        if (_scale == null || !_scaleLive)
        {
            PosMessageBox.Show(this, "Весы не подключены — укажите вес вручную.", "Весы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_scale.LastWeight is not double w || w <= 0)
        {
            PosMessageBox.Show(this, "Нет веса с весов.", "Весы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WeightBox.Text = w.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        WeightBox.SelectAll();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => TryCloseOk();

    private void WeightBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryCloseOk();
        }
    }

    private void TryCloseOk()
    {
        var raw = (WeightBox.Text ?? "").Trim().Replace(',', '.');
        if (raw.Length == 0)
        {
            PosMessageBox.Show(this, "Введите вес.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var kg) || kg <= 0)
        {
            PosMessageBox.Show(this, "Вес должен быть положительным числом.", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        QuantityNormalized = JsonNumericReader.FormatWeightForApi((double)kg) ?? "0";
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    #endregion
}
