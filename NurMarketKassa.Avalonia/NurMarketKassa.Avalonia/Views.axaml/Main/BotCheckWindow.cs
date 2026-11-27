using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.AvaloniaHost.Views.BotSellerUi;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11): «Проверить бота» — вопрос покупателя и ответ ИИ так, как его увидит покупатель (POST telegram-bot/test-ai/ {"as":"customer"}).
/// Заказ не создаётся и продавец не зовётся — окно только показывает, что бот ответил бы, позвал ли бы продавца и оформил ли бы заказ. Отвечает по СОХРАНЁННЫМ настройкам.</summary>
public sealed class BotCheckWindow : Window
{
    private readonly IBotSellerApi? _api;
    private readonly TextBox _question = new() { MaxLength = 500, Watermark = T("Вопрос покупателя", "Сатып алуучунун суроосу", "Customer's question", "Müşterinin sorusu", "Xaridorning savoli") };
    private readonly Button _ask = Btn(T("Спросить", "Суроо", "Ask", "Sor", "So'rash"), primary: true);
    private readonly TextBlock _answer = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private bool _busy;

    public BotCheckWindow() : this(Api())
    {
    }

    public BotCheckWindow(IBotSellerApi? api)
    {
        _api = api;
        Title = T("Проверить бота", "Ботту текшерүү", "Check the bot", "Botu kontrol et", "Botni tekshirish");
        Width = 640;
        Height = 440;
        MinWidth = 440;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme(this, BackgroundProperty, "BrushWindowBackdrop");
        EscapeKey.Attach(this); // 2026-11-26 (стресс-тест): Esc закрывает окно, как остальные окна программы
        Avalonia.Automation.AutomationProperties.SetName(_question, T("Вопрос покупателя", "Сатып алуучунун суроосу", "Customer's question", "Müşterinin sorusu", "Xaridorning savoli"));
        Theme(_answer, TextBlock.ForegroundProperty, "BrushText");
        // 2026-11-26 (стресс-тест): пустое состояние — подсказка вместо пустой белой карточки
        _answer.Text = T("Напишите вопрос так, как его задал бы покупатель (например «Сколько стоит доставка в Ош?»), и нажмите «Спросить».",
            "Суроону сатып алуучу бергендей жазыңыз (мисалы «Ошко жеткирүү канча турат?») жана «Суроону» басыңыз.",
            "Type a question the way a customer would ask it (e.g. “How much is delivery to Osh?”) and press “Ask”.",
            "Soruyu bir müşterinin soracağı gibi yazın (örneğin «Oş'a teslimat ne kadar?») ve «Sor»a basın.",
            "Savolni xaridor beradigandek yozing (masalan «Oshga yetkazish qancha turadi?») va «So'rash»ni bosing.");
        Theme(_notes, TextBlock.ForegroundProperty, "BrushTextSoft");

        var root = new Grid { Margin = new Thickness(20, 14, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Heading(Title!, 20));
        head.Children.Add(Soft(T("Бот ответит так, как увидит покупатель. Заказ не создаётся, продавец не зовётся. Ответ — по сохранённым настройкам бота-продавца.",
            "Бот сатып алуучу көргөндөй жооп берет. Заказ түзүлбөйт, сатуучу чакырылбайт. Жооп — бот-сатуучунун сакталган жөндөөлөрү боюнча.",
            "The bot answers as the customer will see it. No order is created and no seller is called. The answer uses the saved seller bot settings.",
            "Bot, müşterinin göreceği şekilde yanıt verir. Sipariş oluşturulmaz, satıcı çağrılmaz. Yanıt, kayıtlı satıcı bot ayarlarına göredir.",
            "Bot xaridor ko'radigan tarzda javob beradi. Buyurtma yaratilmaydi, sotuvchi chaqirilmaydi. Javob — bot-sotuvchining saqlangan sozlamalari bo'yicha.")));
        root.Children.Add(head);

        var ask = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 10) };
        _ask.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_ask, 1);
        ask.Children.Add(_question);
        ask.Children.Add(_ask);
        Grid.SetRow(ask, 1);
        root.Children.Add(ask);

        var result = new StackPanel { Spacing = 8 };
        result.Children.Add(_answer);
        result.Children.Add(_notes);
        var card = Card(new ScrollViewer { Content = result, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) });
        Grid.SetRow(card, 2);
        root.Children.Add(card);
        Content = root;

        _ask.Click += async (_, _) => await AskAsync().ConfigureAwait(true);
        _question.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await AskAsync().ConfigureAwait(true);
            }
        };
    }

    private async Task AskAsync()
    {
        var text = (_question.Text ?? "").Trim();
        if (_api == null || _busy || text.Length == 0)
            return;
        _busy = true;
        _ask.IsEnabled = false;
        _answer.Text = T("Спрашиваем бота…", "Боттон сурап жатабыз…", "Asking the bot…", "Bota soruluyor…", "Botdan so'ralmoqda…");
        _notes.Text = "";
        try
        {
            var r = await _api.TestAsCustomerAsync(text).ConfigureAwait(true);
            // Сервер при перегрузе ИИ отдаёт ok с пустым ответом (см. «Проверить ИИ на сервере»).
            _answer.Text = r.Answer.Length > 0
                ? r.Answer
                : T("Бот вернул пустой ответ — ИИ перегружен, попробуйте ещё раз через минуту.", "Бот бош жооп кайтарды — ЖИ жүктөлгөн, бир мүнөттөн кийин кайра аракет кылыңыз.",
                    "The bot returned an empty answer — the AI is overloaded, try again in a minute.", "Bot boş yanıt döndürdü — yapay zekâ yoğun, bir dakika sonra tekrar deneyin.", "Bot bo'sh javob qaytardi — SI band, bir daqiqadan keyin qayta urinib ko'ring.");
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Handoff))
                notes.Add("👤 " + T("Бот позвал бы продавца: ", "Бот сатуучуну чакырмак: ", "The bot would call the seller: ", "Bot satıcıyı çağırırdı: ", "Bot sotuvchini chaqirardi: ") + ReasonText(r.Handoff!));
            if (r.OrderDraft is { } draft)
            {
                notes.Add("🧾 " + T("Бот оформил бы заказ (здесь он не создан):", "Бот заказ тариздемек (бул жерде түзүлгөн жок):", "The bot would place an order (not created here):", "Bot sipariş oluştururdu (burada oluşturulmadı):", "Bot buyurtma rasmiylashtirardi (bu yerda yaratilmadi):"));
                if (BotSellerUi.DraftText(draft) is { Length: > 0 } draftText)
                    notes.Add(draftText);
            }
            if (r.Bargain is { } bargain) // 2026-11-26 (торг, Ф2): ответ движка торга в разговоре — цену назвал сервер, не ИИ
                notes.Add("💬 " + T("Торг", "Соодалашуу", "Bargaining", "Pazarlık", "Savdolashish") + (bargain.Offer is { } offer ? " (" + T("предложение ", "сунуш ", "offer ", "teklif ", "taklif ") + Som(offer) + ")" : "") + ": " + bargain.Text);
            notes.Add(T($"Найдено товаров в каталоге: {r.CatalogMatches}", $"Каталогдон табылган товарлар: {r.CatalogMatches}", $"Products found in the catalog: {r.CatalogMatches}", $"Katalogda bulunan ürün: {r.CatalogMatches}", $"Katalogda topilgan mahsulotlar: {r.CatalogMatches}")
                + (r.Model.Length > 0 ? " · " + r.Model : ""));
            _notes.Text = string.Join("\n", notes);
        }
        catch (Exception ex)
        {
            _answer.Text = T("Бот не ответил: ", "Бот жооп берген жок: ", "The bot did not answer: ", "Bot yanıt vermedi: ", "Bot javob bermadi: ") + Failure(ex);
        }
        finally
        {
            _busy = false;
            _ask.IsEnabled = true;
        }
    }
}
