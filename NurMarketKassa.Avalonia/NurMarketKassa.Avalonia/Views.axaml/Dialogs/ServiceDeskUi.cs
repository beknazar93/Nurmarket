using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-13: общие кирпичики окон «Ремонт и сервис» и «Trade-in / скупка» (код без XAML: цвета — только ресурсы темы, подписи — Tr.T на 5 языках).</summary>
public static class ServiceDeskUi
{
    public static void Use(Control target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key));

    public static TextBlock Text(string text, double size = 13, bool bold = false, string brush = "BrushText", bool wrap = true)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Use(tb, TextBlock.ForegroundProperty, brush);
        return tb;
    }

    public static Button Action(string name, string label, Action click, bool primary = false, bool danger = false)
    {
        var b = new Button { Name = name, Content = label, MinHeight = 40, Padding = new Thickness(14, 6), Margin = new Thickness(0, 0, 8, 8) };
        b.Classes.Add(primary ? "modal-primary" : "modal-cancel");
        if (danger)
            Use(b, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "BrushDanger");
        b.Click += (_, _) => click();
        Avalonia.Automation.AutomationProperties.SetName(b, label);
        return b;
    }

    /// <summary>Подпись над значением: «Клиент» / «Иван, +996…». Пустое значение не рисуется.</summary>
    public static void AddFact(Panel into, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        p.Children.Add(Text(label, 11.5, brush: "BrushTextSoft"));
        p.Children.Add(Text(value.Trim(), 14, true));
        into.Children.Add(p);
    }

    /// <summary>Чип статуса.</summary>
    public static Border Chip(string text, bool accent)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Left };
        Use(b, Border.BackgroundProperty, accent ? "BrushAccentSoft" : "BrushPanelSoft");
        b.Child = Text(text, 11.5, true, accent ? "BrushAccentText" : "BrushTextSoft", wrap: false);
        return b;
    }

    /// <summary>Сумма для показа: «1 500,00 сом» (форматирование — MoneyText, расчётов нет).</summary>
    public static string Som(decimal v) => MoneyText.Som(v);

    /// <summary>Разбор суммы из поля: пробелы и запятая/точка допустимы; пусто — 0; неверное — null.</summary>
    public static decimal? ParseMoney(string? text) => RepairRules.TryParseMoney(text);
}

/// <summary>Описание поля формы.</summary>
public sealed record FormField(string Key, string Label, string Initial = "", bool Multiline = false, bool Enabled = true, string Hint = "", double Width = 360, int MaxLength = 200);

/// <summary>2026-11-13: окно-форма «Новый приём в ремонт» / «Новый выкуп»: поля в два столбца, проверка перед закрытием (ошибки списком под полями), «Сохранить» / «Отмена».
/// Окно собирает значения и ничего не сохраняет — решение и запись остаются за окном журнала.</summary>
public sealed class ServiceDeskFormDialog : Window
{
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly TextBlock _error;
    private readonly Func<IReadOnlyDictionary<string, string>, List<string>> _validate;

    public Dictionary<string, string> Values { get; } = new();

    public ServiceDeskFormDialog(string title, string? hint, IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, List<string>> validate, string saveText)
    {
        _validate = validate;
        Title = title;
        Width = 800;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => this.FitToKassaScreen();

        var body = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(hint))
            body.Children.Add(ServiceDeskUi.Text(hint, 12.5, brush: "BrushTextSoft"));
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var f in fields)
        {
            var box = new TextBox
            {
                Name = "Field_" + f.Key, Text = f.Initial, Width = f.Width, MaxLength = f.MaxLength, IsEnabled = f.Enabled,
                AcceptsReturn = f.Multiline, TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = f.Multiline ? 72 : 44, FontSize = 15, VerticalContentAlignment = f.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                CornerRadius = new CornerRadius(10),
            };
            ServiceDeskUi.Use(box, BackgroundProperty, "BrushInput");
            ServiceDeskUi.Use(box, ForegroundProperty, "BrushText");
            ServiceDeskUi.Use(box, BorderBrushProperty, "BrushInputBorder");
            Avalonia.Automation.AutomationProperties.SetName(box, f.Label);
            if (!f.Multiline)
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Confirm(); } };
            _boxes[f.Key] = box;
            var cell = new StackPanel { Margin = new Thickness(0, 0, 14, 8), Width = f.Width };
            cell.Children.Add(ServiceDeskUi.Text(f.Label, 12, brush: "BrushTextSoft"));
            cell.Children.Add(box);
            if (!string.IsNullOrWhiteSpace(f.Hint))
                cell.Children.Add(ServiceDeskUi.Text(f.Hint, 11, brush: "BrushTextSoft"));
            wrap.Children.Add(cell);
        }
        body.Children.Add(wrap);
        _error = ServiceDeskUi.Text("", 12.5, brush: "BrushDanger");
        _error.Name = "FormError";
        _error.IsVisible = false;
        body.Children.Add(_error);

        var cancel = new Button { Name = "FormCancel", Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true, MinWidth = 120 };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Name = "FormSave", Content = saveText, IsDefault = false, MinWidth = 140 };
        ok.Classes.Add("modal-primary");
        ok.Click += (_, _) => Confirm();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        var shell = new ModalShell { Margin = new Thickness(16), Footer = footer, Content = body, InitialFocus = _boxes.Values.FirstOrDefault(b => b.IsEnabled) };
        shell.Title = title;
        Content = shell;
    }

    public string Get(string key) => _boxes.TryGetValue(key, out var b) ? (b.Text ?? "").Trim() : "";

    public void Set(string key, string value)
    {
        if (_boxes.TryGetValue(key, out var b))
            b.Text = value;
    }

    public TextBlock ErrorText => _error;

    public IReadOnlyDictionary<string, TextBox> Boxes => _boxes;

    /// <summary>Проверка и закрытие; ошибки — списком под полями.</summary>
    public void Confirm()
    {
        var values = _boxes.ToDictionary(p => p.Key, p => (p.Value.Text ?? "").Trim());
        var problems = _validate(values);
        if (problems.Count > 0)
        {
            _error.Text = string.Join("\n", problems);
            _error.IsVisible = true;
            return;
        }
        Values.Clear();
        foreach (var p in values)
            Values[p.Key] = p.Value;
        Close(true);
    }
}
