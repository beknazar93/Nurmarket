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
    private readonly double _pricePerKg;
    private bool _isAmountMode;
    private bool _suppressPreviewUpdate;

    public string QuantityNormalized { get; private set; } = "";

    public WeighedProductDialog() : this("", "", null) { }

    public WeighedProductDialog(
        string productTitle,
        string pricePerKgLine,
        IWeightScaleService? scale,
        string? initialKg = null,
        string? okButtonText = null,
        string? windowTitle = null)
    {
        InitializeComponent();
        this.ClampToScreenHeight();
        _scale = scale;
        _scaleLive = HasLiveScaleConnection(scale);
        // 2026-09-14, по просьбе владельца: обратный расчёт — кассир вводит сумму (клиент хочет
        // "сахара на 100 сом"), касса сама считает нужный вес. Нужна числовая цена за кг — там,
        // где её не передают (см. AvaloniaWeightInputPrompt), pricePerKgLine пуст и _pricePerKg
        // останется 0 — переключатель тогда просто не показываем, поведение как раньше.
        _pricePerKg = LocalCartService.ParsePrice(pricePerKgLine);

        var title = string.IsNullOrEmpty(windowTitle) ? Tr.T("Взвесить товар", "Товарды тартуу", "Weigh the product", "Ürünü tart", "Mahsulotni tortish") : windowTitle;
        Title = title;

        // Присвоение значения шапке диалога
        HeaderTitleText.Text = string.IsNullOrEmpty(productTitle)
            ? title
            : Tr.T($"Взвесить: {productTitle}", $"Тартуу: {productTitle}",
                $"Weigh: {productTitle}", $"Tart: {productTitle}", $"Tortish: {productTitle}");
        OkButton.Content = okButtonText ?? Tr.T("В чек", "Чекке", "To receipt", "Fişe", "Chekka");
        PriceBlock.Text = string.IsNullOrEmpty(pricePerKgLine)
            ? ""
            : Tr.T("Цена за кг: ", "1 кг баасы: ", "Price per kg: ", "Kg başına fiyat: ", "Kg narxi: ") + pricePerKgLine;
        WeightLabel.Text = Tr.T("Вес, кг", "Салмагы, кг", "Weight, kg", "Ağırlık, kg", "Og'irlik, kg");
        LiveScaleLabel.Text = Tr.T("На весах сейчас", "Азыр таразада", "On the scale now", "Şu anda terazide", "Hozir tarozida");
        CancelButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");

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

        ModeTabsRow.IsVisible = _pricePerKg > 0;
        ByWeightTab.Content = Tr.T("По весу", "Салмагы боюнча", "By weight", "Ağırlığa göre", "Og'irlik bo'yicha");
        ByAmountTab.Content = Tr.T("По сумме", "Суммасы боюнча", "By amount", "Tutara göre", "Summa bo'yicha");
        UpdateComputedPreview();

        if (_scaleLive)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) =>
            {
                LiveScaleText.Text = FormatLiveScaleText();
                if (_scale?.LastWeight is double w && w > 0)
                    // Без TrimEnd: у целого веса формат не печатает точку, и TrimEnd('0') отрезал
                    // нули самого числа — ровно 10 кг с весов подставлялись в поле как 1.
                    WeightBox.Text = w.ToString("0.###", CultureInfo.InvariantCulture);
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

    #region Режим "По сумме" — обратный расчёт веса из суммы

    private void ByWeightTab_Click(object? sender, RoutedEventArgs e) => SetAmountMode(false);

    private void ByAmountTab_Click(object? sender, RoutedEventArgs e) => SetAmountMode(true);

    private void SetAmountMode(bool amountMode)
    {
        if (_isAmountMode == amountMode)
            return;

        _isAmountMode = amountMode;
        ByWeightTab.Classes.Set("Active", !amountMode);
        ByAmountTab.Classes.Set("Active", amountMode);
        WeightLabel.Text = amountMode
            ? Tr.T("Сумма, сом", "Суммасы, сом", "Amount, som", "Tutar, som", "Summa, so'm")
            : Tr.T("Вес, кг", "Салмагы, кг", "Weight, kg", "Ağırlık, kg", "Og'irlik, kg");
        QuickWeightRow.IsVisible = !amountMode;
        FromScaleButton.IsVisible = !amountMode && HasLiveScaleConnection(_scale);

        // Поле переиспользуется под другую величину — начинаем заново, чтобы старое значение
        // веса не читалось как сумма (или наоборот).
        _suppressPreviewUpdate = true;
        WeightBox.Text = "0.00";
        _suppressPreviewUpdate = false;

        WeightBox.Focus();
        WeightBox.SelectAll();
        UpdateComputedPreview();
    }

    private void WeightBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suppressPreviewUpdate)
            UpdateComputedPreview();
    }

    /// <summary>В режиме "По сумме" показывает, сколько кг нужно отвесить за введённую сумму
    /// (клиент хочет "сахара на 100 сом" — кассир вводит 100, видит "≈ 0.833 кг" и взвешивает
    /// столько). Считается по той же цене за кг, что показана в шапке диалога.</summary>
    private void UpdateComputedPreview()
    {
        if (!_isAmountMode || _pricePerKg <= 0)
        {
            ComputedPreviewText.IsVisible = false;
            return;
        }

        var raw = (WeightBox.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            ComputedPreviewText.IsVisible = false;
            return;
        }

        var weightKg = amount / _pricePerKg;
        ComputedPreviewText.Text = "≈ " + weightKg.ToString("0.###", CultureInfo.InvariantCulture) + " " +
            Tr.T("кг", "кг", "kg", "kg", "kg");
        ComputedPreviewText.IsVisible = true;
    }

    #endregion

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

        // Без TrimEnd — см. выше: ровно 10 кг по кнопке «С весов» давали 1.
        WeightBox.Text = w.ToString("0.###", CultureInfo.InvariantCulture);
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
            var emptyMessage = _isAmountMode
                ? Tr.T("Введите сумму.", "Суммасын киргизиңиз.", "Enter the amount.", "Tutarı girin.", "Summani kiriting.")
                : Tr.T("Введите вес.", "Салмакты киргизиңиз.", "Enter the weight.", "Ağırlığı girin.", "Og'irlikni kiriting.");
            PosMessageBox.Show(this, emptyMessage, Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var enteredValue) || enteredValue <= 0)
        {
            var invalidMessage = _isAmountMode
                ? Tr.T("Сумма должна быть положительным числом.", "Сумма оң сан болушу керек.", "Amount must be a positive number.", "Tutar pozitif bir sayı olmalıdır.", "Summa musbat son bo'lishi kerak.")
                : Tr.T("Вес должен быть положительным числом.", "Салмак оң сан болушу керек.", "Weight must be a positive number.", "Ağırlık pozitif bir sayı olmalıdır.", "Og'irlik musbat son bo'lishi kerak.");
            PosMessageBox.Show(this, invalidMessage, Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2026-09-26, стресс-тест: пока это окно открыто, сканер следующего товара печатает
        // штрихкод прямо в поле веса — «2990000000026» кг + Enter. Касса предлагала
        // «Подтвердить и добавить» такой вес сверх остатка. Столько не весит ни один товар.
        if (enteredValue >= (_isAmountMode ? 10_000_000m : 10_000m))
        {
            WeightBox.Text = "";
            PosMessageBox.Show(this,
                Tr.T("Слишком большое число — похоже, в поле попал штрихкод. Введите вес заново.",
                    "Өтө чоң сан — талаага штрихкод түшүп калган окшойт. Салмакты кайра киргизиңиз.",
                    "The number is too large — a barcode seems to have landed in the field. Enter the weight again.",
                    "Sayı çok büyük — alana barkod girilmiş gibi görünüyor. Ağırlığı yeniden girin.",
                    "Son juda katta — maydonga shtrix-kod tushib qolganga o'xshaydi. Og'irlikni qayta kiriting."),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2026-09-14: в режиме "По сумме" кассир ввёл деньги, а не кг — переводим в вес по той
        // же цене за кг, что показана в шапке диалога, и дальше всё как в обычном режиме
        // (в чек всегда идёт вес, не сумма).
        var kg = _isAmountMode && _pricePerKg > 0 ? enteredValue / (decimal)_pricePerKg : enteredValue;
        if (_isAmountMode && kg <= 0)
        {
            PosMessageBox.Show(this,
                Tr.T("Не удалось посчитать вес — не задана цена товара.", "Салмакты эсептөө мүмкүн болбоду — товардын баасы көрсөтүлгөн эмес.", "Could not compute the weight — the product price is not set.", "Ağırlık hesaplanamadı — ürün fiyatı belirtilmemiş.", "Og'irlikni hisoblab bo'lmadi — mahsulot narxi ko'rsatilmagan."),
                Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        QuantityNormalized = JsonNumericReader.FormatWeightForApi((double)kg) ?? "0";
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    #endregion
}
