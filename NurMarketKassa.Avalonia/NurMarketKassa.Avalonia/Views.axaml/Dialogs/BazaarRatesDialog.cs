using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-21 («Базар», модуль «Валюты», ТЗ Контейнер-базар 2.6, ответ сервера 20.11 В.19): окно «Курсы валют» — сколько сомов за 1 USD / RUB / CNY. Курсы общие для компании
/// (GET / PUT main/currencies/): меняет только владелец или администратор (сервер тоже проверяет), остальным поля закрыты. Пустое поле — валюта убирается (в окне оплаты её нет).
/// Перед сохранением курсы читаются заново — так видны правки с другой кассы. Подписи — отдельной строкой над полем, поля одной высоты 52 (чек-лист новых окон).
/// </summary>
public sealed class BazaarRatesDialog : Window
{
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly TextBlock _message = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly bool _canEdit;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public BazaarRatesDialog()
    {
        _canEdit = ActionGuard.IsSenior();
        Title = T("Курсы валют", "Валюта курстары", "Exchange rates", "Döviz kurları", "Valyuta kurslari");
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var hint = new TextBlock
        {
            Text = T("Сколько сомов за одну единицу валюты. В окне оплаты принимаются только валюты с курсом; сдача всегда в сомах.",
                "Бир валюта бирдигине канча сом. Төлөм терезесинде курсу бар валюталар гана кабыл алынат; кайтарым дайыма сом менен.",
                "How many som per one unit of currency. The payment window accepts only currencies that have a rate; change is always in som.",
                "Bir döviz birimi kaç som. Ödeme penceresinde yalnızca kuru olan dövizler kabul edilir; para üstü her zaman somla.",
                "Bir birlik valyuta necha so'm. To'lov oynasida faqat kursi bor valyutalar qabul qilinadi; qaytim doim so'mda."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(hint);
        var rates = BazaarCurrencies.Current;
        foreach (var code in BazaarCurrencies.PayCurrencies)
        {
            var label = new TextBlock { Text = $"1 {code} = … {T("сом", "сом", "som", "som", "so'm")}", FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushTextSoft"));
            var box = new TextBox
            {
                Height = 52, FontSize = 20, FontWeight = FontWeight.Bold, MaxLength = 12, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = _canEdit,
                Text = rates.Rates.TryGetValue(code, out var r) ? r.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU")) : "",
                Watermark = T("нет курса", "курс жок", "no rate", "kur yok", "kurs yo'q"),
            };
            _fields[code] = box;
            stack.Children.Add(label);
            stack.Children.Add(box);
        }
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(_message);
        if (!_canEdit)
            ShowMessage(T("Курсы меняет только владелец или администратор.", "Курстарды ээси же администратор гана өзгөртөт.", "Only the owner or an administrator changes the rates.",
                "Kurları yalnızca sahip veya yönetici değiştirir.", "Kurslarni faqat egasi yoki administrator o'zgartiradi."), isError: false);

        var cancel = new Button { Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), IsCancel = true, MinHeight = 44 };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        _save = new Button { Content = T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), IsDefault = true, MinHeight = 44, IsEnabled = _canEdit };
        _save.Classes.Add("modal-primary");
        _save.Click += OnSave;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } };
        Content = new ModalShell { Margin = new Thickness(16), Title = Title, Footer = buttons, Content = stack };

        // свежие курсы с сервера (если есть связь) — вдруг их поменяли на другой кассе
        Opened += async (_, _) =>
        {
            var fresh = await BazaarCurrencies.RefreshAsync().ConfigureAwait(true);
            if (fresh is null)
                return;
            foreach (var (code, box) in _fields)
                box.Text = fresh.Rates.TryGetValue(code, out var v) ? v.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU")) : "";
        };
    }

    private void ShowMessage(string text, bool isError)
    {
        _message.Text = text;
        _message.IsVisible = true;
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable(isError ? "BrushDanger" : "BrushTextSoft"));
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>();
        foreach (var (code, box) in _fields)
        {
            var text = box.Text?.Trim() ?? "";
            if (text.Length > 0 && BazaarCurrencies.ParseAmount(text) is null)
            {
                ShowMessage(T($"Курс {code}: введите число больше нуля или оставьте пустым.", $"{code} курсу: нөлдөн чоң сан жазыңыз же бош калтырыңыз.",
                    $"{code} rate: enter a number above zero or leave it empty.", $"{code} kuru: sıfırdan büyük bir sayı girin veya boş bırakın.", $"{code} kursi: noldan katta son kiriting yoki bo'sh qoldiring."), isError: true);
                return;
            }
            values[code] = text;
        }
        _save.IsEnabled = false;
        try
        {
            var (rates, error) = await BazaarCurrencies.SaveAsync(values).ConfigureAwait(true);
            if (error is not null)
            {
                ShowMessage(error, isError: true);
                if (BazaarCurrencies.Current is { } fresh)
                    foreach (var (code, box) in _fields)
                        box.Text = fresh.Rates.TryGetValue(code, out var v) ? v.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU")) : "";
                return;
            }
            PosLogger.Log("Курсы валют сохранены: " + string.Join(", ", (rates?.Rates ?? new Dictionary<string, decimal>()).Select(kv => $"{kv.Key}={kv.Value}")), "INFO");
            Close(true);
        }
        finally
        {
            _save.IsEnabled = _canEdit;
        }
    }
}
