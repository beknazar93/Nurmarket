using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг»): «Проверить торг» — симулятор движка по одному товару (POST telegram-bot/bargain/test/ {product_id, offer?, reset?}):
/// владелец вводит «предложение покупателя» и видит решение сервера (договорились / встречная цена / лучшая цена / цена фиксированная), цену, ответ бота и пол цены. Тест-чат в отчёт не попадает,
/// покупателям ничего не отправляется. «Начать заново» сбрасывает ступени торга тест-чата. Пол цены — только владельцу.</summary>
public sealed class BotBargainTestWindow : Window
{
    private readonly IBotBargainApi? _api;
    private readonly string _productId;
    private readonly TextBox _offer = new() { Width = 180, MaxLength = 12, HorizontalAlignment = HorizontalAlignment.Left }; // 2026-11-26 (стресс-тест): поле было по центру окна, оторвано от подписи
    private readonly CheckBox _reset = new() { Content = T("Начать торг заново", "Соодалашууну кайрадан баштоо", "Start bargaining over", "Pazarlığı baştan başlat", "Savdolashishni qaytadan boshlash") };
    private readonly Button _send = Btn(T("Предложить цену", "Баа сунуштоо", "Offer a price", "Fiyat öner", "Narx taklif qilish"), primary: true);
    private readonly TextBlock _decision = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _numbers = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private bool _busy;

    public BotBargainTestWindow(IBotBargainApi? api, string productId, string productName, decimal listPrice)
    {
        _api = api;
        _productId = productId;
        Title = T("Проверить торг", "Соодалашууну текшерүү", "Check bargaining", "Pazarlığı dene", "Savdolashishni tekshirish");
        Width = 620;
        Height = 460;
        MinWidth = 440;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        EscapeKey.Attach(this); // 2026-11-26 (стресс-тест): Esc закрывает окно, как остальные окна программы
        Theme(_decision, TextBlock.ForegroundProperty, "BrushText");
        Theme(_numbers, TextBlock.ForegroundProperty, "BrushTextSoft");
        Theme(_text, TextBlock.ForegroundProperty, "BrushText");
        Theme(_notes, TextBlock.ForegroundProperty, "BrushTextSoft");
        // 2026-11-26 (стресс-тест): пустое состояние — подсказка вместо пустой карточки
        _numbers.Text = T("Введите цену, которую предложил бы покупатель, и нажмите «Предложить цену» — здесь появится ответ сервера.",
            "Сатып алуучу сунуштай турган бааны жазып, «Баа сунуштоону» басыңыз — бул жерде сервердин жообу чыгат.",
            "Enter the price a customer would offer and press “Offer a price” — the server's answer appears here.",
            "Müşterinin önereceği fiyatı girin ve «Fiyat öner»e basın — sunucunun yanıtı burada görünür.",
            "Xaridor taklif qiladigan narxni kiriting va «Narx taklif qilish»ni bosing — server javobi shu yerda chiqadi.");
        Avalonia.Automation.AutomationProperties.SetName(_reset, _reset.Content as string ?? "");

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Heading(productName, 14));
        head.Children.Add(Soft(T("Цена в списке: ", "Тизмедеги баа: ", "List price: ", "Liste fiyatı: ", "Ro'yxatdagi narx: ") + Som(listPrice) + " · "
            + T("Это проверка: покупателям ничего не отправляется, в отчёт тест не попадает.", "Бул текшерүү: сатып алуучуларга эч нерсе жөнөтүлбөйт, отчётко кирбейт.",
                "This is a test: nothing is sent to customers and it does not appear in the report.", "Bu bir deneme: müşterilere bir şey gönderilmez, rapora girmez.", "Bu sinov: xaridorlarga hech narsa yuborilmaydi, hisobotga kirmaydi.")));
        root.Children.Add(head);

        var ask = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 10) };
        ask.Children.Add(Labeled(T("Предложение покупателя, сом", "Сатып алуучунун сунушу, сом", "Customer's offer, som", "Müşterinin teklifi, som", "Xaridorning taklifi, so'm"), _offer,
            T("Пусто — покупатель спрашивает цену, скидку не просит.", "Бош — сатып алуучу баасын гана сурайт.", "Empty — the customer only asks the price.", "Boş — müşteri yalnızca fiyatı soruyor.", "Bo'sh — xaridor faqat narxni so'raydi.")));
        ask.Children.Add(_reset);
        ask.Children.Add(_send);
        _send.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(ask, 1);
        root.Children.Add(ask);

        var result = new StackPanel { Spacing = 8 };
        result.Children.Add(_decision);
        result.Children.Add(_numbers);
        result.Children.Add(_text);
        result.Children.Add(_notes);
        var card = Card(new ScrollViewer { Content = result, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) });
        Grid.SetRow(card, 2);
        root.Children.Add(card);
        Content = root;

        _send.Click += async (_, _) => await SendAsync().ConfigureAwait(true);
        _offer.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendAsync().ConfigureAwait(true);
            }
        };
    }

    private async Task SendAsync()
    {
        if (_api == null || _busy)
            return;
        decimal? offer = ParseAmount(_offer.Text);
        if ((!string.IsNullOrWhiteSpace(_offer.Text) && offer is null) || offer is <= 0) // 2026-11-26 (стресс-тест): 0 и минус — не предложение
        {
            _decision.Text = T("Предложение — число, например 28000.", "Сунуш — сан, мисалы 28000.", "The offer is a number, e.g. 28000.", "Teklif bir sayıdır, örneğin 28000.", "Taklif — son, masalan 28000.");
            return;
        }
        _busy = true;
        _send.IsEnabled = false;
        _decision.Text = T("Считаем…", "Эсептелүүдө…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
        _numbers.Text = _text.Text = _notes.Text = "";
        try
        {
            var r = await _api.TestBargainAsync(_productId, offer, _reset.IsChecked == true).ConfigureAwait(true);
            _reset.IsChecked = false; // следующее предложение — следующая ступень
            _decision.Text = DecisionText(r.Decision);
            _numbers.Text = T("Цена: ", "Баасы: ", "Price: ", "Fiyat: ", "Narx: ") + Som(r.Price) + " · " + T("в списке: ", "тизмеде: ", "list: ", "liste: ", "ro'yxatda: ") + Som(r.ListPrice)
                + " · " + T("пол цены: ", "баанын полу: ", "price floor: ", "fiyat tabanı: ", "narx poli: ") + Som(r.Floor);
            _text.Text = r.Text;
            _notes.Text = r.Button
                ? T("Под ценой покупатель увидит кнопку «✅ Беру». Цена держится 30 минут, одна оферта — один заказ.", "Баанын астында сатып алуучу «✅ Алам» баскычын көрөт. Баа 30 мүнөт сакталат, бир оферта — бир заказ.",
                    "Under the price the customer sees the “✅ I'll take it” button. The price holds for 30 minutes, one offer — one order.", "Fiyatın altında müşteri «✅ Alıyorum» düğmesini görür. Fiyat 30 dakika geçerli, bir teklif — bir sipariş.",
                    "Narx ostida xaridor «✅ Olaman» tugmasini ko'radi. Narx 30 daqiqa amal qiladi, bitta taklif — bitta buyurtma.")
                : "";
        }
        catch (Exception ex)
        {
            _decision.Text = T("Не получилось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + BargainFailure(ex);
        }
        finally
        {
            _busy = false;
            _send.IsEnabled = true;
        }
    }
}
