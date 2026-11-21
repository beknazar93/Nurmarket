using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-21 («Базар», модуль «Торг», ТЗ Контейнер-базар 2.3): окно торга. Сверху — сумма (чека или строки), крупные кнопки «До 10 / До 50 / До 100» (округление ВНИЗ; кнопка, которая
/// ничего бы не изменила, выключена), ниже — поле «Договорились за …». Предпросмотр: скидка в сомах и процентах, «к оплате». «Применить» возвращает договорённый итог (<see cref="Agreed"/>);
/// сама скидка, PIN руководителя и защита закупки — в окне кассы (MainWindow.Bazaar). Кнопки 52 px (сенсорный экран), окно 512 px — помещается на 1024×600.
/// </summary>
public sealed class BazaarHaggleDialog : Window
{
    private readonly decimal _total;
    private readonly TextBox _amount;
    private readonly TextBlock _preview = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly Button _ok;
    private readonly List<(Button Button, int Step)> _stepButtons = new();
    private bool _syncing;

    /// <summary>Договорённый итог (после «Применить»).</summary>
    public decimal Agreed { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public BazaarHaggleDialog(string subject, decimal total)
    {
        _total = total;
        Title = T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv");
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var totalText = new TextBlock
        {
            Text = $"{T("Сумма", "Сумма", "Amount", "Tutar", "Summa")}: {MoneyText.Som(total)}",
            FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        totalText.Bind(TextBlock.ForegroundProperty, totalText.GetResourceObservable("BrushText"));

        var stepsLabel = Label(T("Округлить вниз", "Төмөн тегеректөө", "Round down", "Aşağı yuvarla", "Pastga yaxlitlash"));
        // «До 10 / До 50 / До 100» — три равные ячейки одной строкой (Grid, а не WrapPanel: чек-лист новых окон).
        var steps = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*") };
        var column = 0;
        foreach (var step in BazaarHaggle.Steps)
        {
            var button = new Button
            {
                Height = 52, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, FontSize = 16, FontWeight = FontWeight.Bold,
                Content = T($"До {step}", $"{step} чейин", $"To {step}", $"{step}'e kadar", $"{step} gacha"),
            };
            button.Classes.Add("modal-cancel");
            var s = step;
            button.Click += (_, _) => PickStep(s);
            Grid.SetColumn(button, column);
            column += 2;
            steps.Children.Add(button);
            _stepButtons.Add((button, step));
        }

        var agreedLabel = Label(T("Договорились за …", "Макулдашылган сумма …", "Agreed for …", "Anlaşılan tutar …", "Kelishilgan summa …"));
        _amount = new TextBox { FontSize = 22, FontWeight = FontWeight.Bold, Height = 52, MaxLength = 14, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        _amount.TextChanged += (_, _) => { if (!_syncing) Refresh(); };
        _preview.Bind(TextBlock.ForegroundProperty, _preview.GetResourceObservable("BrushText"));

        var cancel = new Button { Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true, MinHeight = 44 };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        _ok = new Button { Content = T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"), IsDefault = true, MinHeight = 44, IsEnabled = false };
        _ok.Classes.Add("modal-primary");
        _ok.Click += OnOk;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title, Subtitle = subject, Footer = buttons,
            Content = new StackPanel { Spacing = 8, Children = { totalText, stepsLabel, steps, agreedLabel, _amount, _preview } },
        };

        foreach (var (button, step) in _stepButtons)
            button.IsEnabled = BazaarHaggle.RoundDown(_total, step).IsOk;
        Opened += (_, _) => { _amount.Focus(); };
        Refresh();
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushTextSoft"));
        return l;
    }

    private void PickStep(int step)
    {
        var result = BazaarHaggle.RoundDown(_total, step);
        if (!result.IsOk)
            return;
        _syncing = true;
        _amount.Text = MoneyText.Weight(result.Agreed, "0.##");
        _syncing = false;
        Refresh();
    }

    private HaggleResult Current =>
        BazaarHaggle.ParseAmount(_amount.Text) is { } v ? BazaarHaggle.Agreed(_total, v) : new HaggleResult(HaggleOutcome.InvalidAmount, _total, 0m, 0m);

    private void Refresh()
    {
        var r = Current;
        _preview.Text = r.Outcome switch
        {
            HaggleOutcome.Ok => T($"Скидка {MoneyText.Som(r.Discount)} ({r.Percent:0.#} %) · к оплате {MoneyText.Som(r.Agreed)}",
                $"Арзандатуу {MoneyText.Som(r.Discount)} ({r.Percent:0.#} %) · төлөнүүчү {MoneyText.Som(r.Agreed)}",
                $"Discount {MoneyText.Som(r.Discount)} ({r.Percent:0.#} %) · to pay {MoneyText.Som(r.Agreed)}",
                $"İndirim {MoneyText.Som(r.Discount)} (%{r.Percent:0.#}) · ödenecek {MoneyText.Som(r.Agreed)}",
                $"Chegirma {MoneyText.Som(r.Discount)} ({r.Percent:0.#} %) · to'lanadi {MoneyText.Som(r.Agreed)}"),
            HaggleOutcome.CannotRaise => T("Торг только вниз: итог не может быть больше суммы.", "Соодалашуу төмөн гана: жыйынтык суммадан чоң болбойт.",
                "Haggling only goes down: the total cannot exceed the amount.", "Pazarlık yalnızca aşağı: toplam tutardan büyük olamaz.", "Savdolashuv faqat pastga: jami summadan oshmasligi kerak."),
            HaggleOutcome.NoChange => T("Сумма та же — скидки нет.", "Сумма ошол эле — арзандатуу жок.", "Same amount — no discount.", "Tutar aynı — indirim yok.", "Summa o'sha — chegirma yo'q."),
            _ => T("Введите сумму, о которой договорились, или нажмите «До 10 / 50 / 100».", "Макулдашкан сумманы жазыңыз же «10 / 50 / 100 чейин» басыңыз.",
                "Enter the agreed sum or press “To 10 / 50 / 100”.", "Anlaşılan tutarı girin veya «10 / 50 / 100'e kadar»a basın.", "Kelishilgan summani kiriting yoki «10 / 50 / 100 gacha»ni bosing."),
        };
        _ok.IsEnabled = r.IsOk;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var r = Current;
        if (!r.IsOk)
            return;
        Agreed = r.Agreed;
        Close(true);
    }
}
