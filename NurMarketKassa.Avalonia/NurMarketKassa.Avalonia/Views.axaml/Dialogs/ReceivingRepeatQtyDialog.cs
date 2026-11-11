using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-09 (владелец: «при повторном скане того же товара остановить сканер и спросить количество — и это же на закупках в десктопе»):
/// окно «Сколько штук?» для «Склад → Приёмка» и «Закупки → Приём товара». Показывается, когда тот же код отсканирован снова сразу после добавления
/// (<see cref="ReceivingRepeatScanGuard"/>). Название, «Уже в приёмке: N шт. Сколько добавить?», поле количества (в фокусе, число выделено), «−» / «+»;
/// Enter — добавить, Esc — отмена. Штуки и упаковки — целые, л / кг / м — дробные (3 знака). Результат ShowDialog: сколько добавить, null — «Отмена» (ничего не менять).
/// Скан во время окна ИГНОРИРУЕТСЯ: символы сканера в поле отбрасываются и поле возвращается к прежнему числу, Enter сканера окно не принимает
/// (ScannerEnterGuard) — иначе штрихкод стал бы количеством, а скан — подряд идущим добавлением.</summary>
public sealed class ReceivingRepeatQtyDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly bool _fractional;
    private readonly TextBox _box;
    private readonly Button _ok;
    private readonly TextBlock _error = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool _closed;
    private bool _restoring;
    private string _prevText = "";
    private string _stableText = "1";
    private long _lastChangeAt;
    private const int BurstGapMs = 60; // как у ScannerEnterGuard: символы сканера идут чаще раза в 60 мс

    /// <summary>Сколько сканов отброшено, пока окно открыто (для проверки).</summary>
    public int IgnoredScans { get; private set; }

    /// <summary>Поле количества (для проверки).</summary>
    public TextBox QuantityBox => _box;

    public ReceivingRepeatQtyDialog(ReceivingLineVm line, double initial = 1)
    {
        _fractional = ReceivingRepeatScan.IsFractional(line);
        var title = ReceivingRepeatScan.Title(line);
        Title = title;
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var message = new TextBlock { Text = ReceivingRepeatScan.Message(line), FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        message.Bind(TextBlock.ForegroundProperty, message.GetResourceObservable("BrushText"));

        var start = initial > 0 ? ReceivingRepeatScan.Format(initial) : "1";
        _box = new TextBox
        {
            Name = "RepeatQtyBox", Text = start, FontSize = 22, FontWeight = FontWeight.Bold, Height = 52, MinWidth = 120, MaxLength = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(_box, T("Сколько штук", "Канча даана", "How many", "Kaç adet", "Nechta dona"));
        _prevText = _stableText = start;
        _box.TextChanged += (_, _) => OnTextChanged();

        var unit = new TextBlock { Text = ReceivingRepeatScan.UnitLabel(line), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        unit.Bind(TextBlock.ForegroundProperty, unit.GetResourceObservable("BrushTextSoft"));
        var minus = Step("RepeatQtyMinus", "−", T("Меньше", "Азыраак", "Less", "Daha az", "Kamroq"), -ReceivingRepeatScan.Step(_fractional));
        var plus = Step("RepeatQtyPlus", "+", T("Больше", "Көбүрөөк", "More", "Daha çok", "Ko'proq"), ReceivingRepeatScan.Step(_fractional));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        _box.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(minus, 0);
        Grid.SetColumn(_box, 1);
        Grid.SetColumn(unit, 2);
        Grid.SetColumn(plus, 3);
        row.Children.Add(minus);
        row.Children.Add(_box);
        row.Children.Add(unit);
        row.Children.Add(plus);
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));

        var cancel = new Button { Name = "RepeatQtyCancel", Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Finish(null);
        _ok = new Button { Name = "RepeatQtyAdd", Content = T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish"), IsDefault = true };
        _ok.Classes.Add("modal-primary");
        _ok.Click += (_, _) => { if (Value is { } v) Finish(v); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = title,
            Subtitle = T("Повторный скан", "Кайра сканерлөө", "Repeat scan", "Tekrar okutma", "Qayta skanerlash"),
            Footer = buttons, InitialFocus = _box,
            Content = new StackPanel { Spacing = 8, Children = { message, row, _error } },
        };
        // Enter сканера окно не получает; серию символов сканера из поля убираем (поле возвращается к числу до серии).
        ScannerEnterGuard.Attach(this, onBlocked: () =>
        {
            IgnoredScans++;
            Restore();
        });
        Opened += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        Validate();
    }

    private Button Step(string name, string glyph, string tip, double delta)
    {
        var b = new Button { Name = name, Content = glyph, Width = 52, Height = 52, FontSize = 24, FontWeight = FontWeight.Bold, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => Bump(delta);
        return b;
    }

    /// <summary>Число из поля; null — не годится.</summary>
    private double? Value => ReceivingRepeatScan.Parse(_box.Text, _fractional);

    private void Bump(double delta)
    {
        var current = ReceivingRepeatScan.Parse(_box.Text, _fractional) ?? 0;
        var next = ReceivingRepeatScan.Bump(current, delta, _fractional); // 2026-11-11: расчёт перенесён в ReceivingRepeatScan (окно без округлений)
        SetText(ReceivingRepeatScan.Format(next));
        _box.Focus();
        _box.SelectAll();
    }

    private void SetText(string text)
    {
        _restoring = true;
        try
        {
            _box.Text = text;
            _prevText = _stableText = text;
        }
        finally
        {
            _restoring = false;
        }
        Validate();
    }

    private void Restore() => SetText(_stableText);

    private void OnTextChanged()
    {
        if (_restoring)
            return;
        // Штучный товар — только цифры; дробный — цифры и один разделитель.
        var text = _box.Text ?? "";
        var clean = Clean(text);
        if (clean != text)
        {
            _restoring = true;
            try { _box.Text = clean; }
            finally { _restoring = false; }
            text = clean;
        }
        var now = Environment.TickCount64;
        if (now - _lastChangeAt > BurstGapMs)
            _stableText = _prevText; // начало нового набора: то, что было до него, — «устойчивое» число
        _lastChangeAt = now;
        _prevText = text;
        Validate();
    }

    private string Clean(string text)
    {
        var sb = new System.Text.StringBuilder();
        var sep = false;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
                sb.Append(ch);
            else if (_fractional && !sep && (ch == ',' || ch == '.'))
            {
                sb.Append(ch);
                sep = true;
            }
        }
        return sb.ToString();
    }

    private void Validate()
    {
        var ok = Value is not null;
        _ok.IsEnabled = ok;
        _error.Text = ok ? "" : T("Введите число больше нуля.", "Нөлдөн чоң сан киргизиңиз.", "Enter a number above zero.", "Sıfırdan büyük bir sayı girin.", "Noldan katta son kiriting.");
        _error.IsVisible = !ok;
    }

    private void Finish(double? value)
    {
        if (_closed)
            return; // Enter и кнопка подряд — закрыть один раз
        _closed = true;
        Close(value);
    }

    /// <summary>Спросить «Сколько штук?» над <paramref name="owner"/>. null — «Отмена».</summary>
    public static async Task<double?> AskAsync(Window? owner, ReceivingLineVm line, double initial = 1)
    {
        var dialog = new ReceivingRepeatQtyDialog(line, initial);
        PosLogger.Log($"[UI] Приёмка: повторный скан «{ReceivingRepeatScan.Title(line)}» — окно «Сколько штук?».", "UI");
        return await dialog.ShowDialog<double?>(PosDialogHost.ResolveOwner(owner)).ConfigureAwait(true);
    }
}
