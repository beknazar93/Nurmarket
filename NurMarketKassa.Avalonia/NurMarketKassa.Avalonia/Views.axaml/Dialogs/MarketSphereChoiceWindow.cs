using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-06, владелец: «главное не трогай продуктовый, чтобы у клиентов потом проблем не было, а новым клиентам при первом
/// запуске спрашивать сферу маркета». Один раз на новой установке (UserPreferences.MarketSphereChoicePending) после входа:
/// если у компании на сервере вид магазина ещё не задан — «Какой у вас магазин?» (Продукты / Одежда и обувь / Услуги).
/// Выбор сохраняется на сервер (у владельца — для всех касс компании; у кассира сервер отвечает 403 — тогда только в этой
/// кассе). Уже работающие кассы ничего не спрашивают. Закрыли без выбора — спросим при следующем запуске.
/// </summary>
public sealed class MarketSphereChoiceWindow : Window
{
    private static bool _running;

    public string? Chosen { get; private set; }

    public static async Task MaybeAskAsync(Window owner)
    {
        var prefs = UserPreferences.Instance;
        if (_running || !prefs.MarketSphereChoicePending)
            return;
        _running = true;
        try
        {
            // Ждём загрузки компании (вид магазина с сервера) — не дольше 30 с.
            for (var i = 0; i < 60 && CompanyInfoService.LastCompany is null; i++)
                await Task.Delay(500).ConfigureAwait(true);
            if (CompanyInfoService.LastCompany is null)
                return;
            if (MarketSphereSync.ServerSphere is { } serverSphere)
            {
                // Компания уже выбрала вид (на другой кассе или на сайте) — его применила синхронизация, не спрашиваем.
                prefs.MarketSphereChoicePending = false;
                prefs.SaveToDisk();
                PosLogger.Log($"Первый запуск: вид магазина уже задан у компании ({serverSphere}) — не спрашиваем.", "SETTINGS");
                return;
            }

            var dialog = new MarketSphereChoiceWindow();
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (dialog.Chosen is not { } sphere)
                return;

            MarketSpheres.Set(sphere);
            prefs.MarketSphereChoicePending = false;
            prefs.SaveToDisk();
            PosLogger.Log($"Первый запуск: выбран вид магазина {sphere}.", "SETTINGS");
            try
            {
                await App.GetRequiredService<ClientDebtsApiService>().SetCompanyMarketSphereAsync(sphere).ConfigureAwait(true);
                MarketSphereSync.NoteSavedOnServer(sphere);
                PosLogger.Log("Первый запуск: вид магазина сохранён на сервере для всех касс компании.", "SETTINGS");
            }
            catch (Exception ex)
            {
                // Кассир (403) или нет связи — вид остаётся в этой кассе; владелец сменит его в настройках.
                PosLogger.Log($"Первый запуск: вид магазина на сервер не сохранён ({ex.Message}) — только в этой кассе.", "SETTINGS");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Первый запуск: вопрос о виде магазина не показан ({ex.Message}).", "WARNING");
        }
        finally
        {
            _running = false;
        }
    }

    public MarketSphereChoiceWindow()
    {
        Title = Tr.T("Какой у вас магазин?", "Дүкөнүңүз кандай?", "What kind of store do you have?", "Mağazanız ne tür?", "Do'koningiz qanday?");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new StackPanel { Margin = new Thickness(26, 22), Spacing = 12 };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);
        var hint = new TextBlock
        {
            Text = Tr.T("Касса настроится под ваш вид торговли. Поменять можно в любой момент: Настройки → Операции → «Сфера магазина».",
                "Касса соода түрүңүзгө ылайыкташат. Каалаган убакта өзгөртсө болот: Жөндөөлөр → Операциялар → «Дүкөндүн тармагы».",
                "The till will adapt to your kind of trade. You can change it any time: Settings → Operations → “Store type”.",
                "Kasa ticaret türünüze göre ayarlanır. İstediğiniz zaman değiştirebilirsiniz: Ayarlar → İşlemler → «Mağaza türü».",
                "Kassa savdo turingizga moslashadi. Istalgan vaqtda o'zgartirish mumkin: Sozlamalar → Operatsiyalar → «Do'kon turi»."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(hint);

        root.Children.Add(Tile("🛒", Tr.T("Продукты", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat"),
            Tr.T("Продуктовый магазин, супермаркет: весы и весовые товары, быстрые продажи.", "Азык-түлүк дүкөнү, супермаркет: таразалар жана салмактуу товарлар, тез сатуу.",
                "Grocery store, supermarket: scales and weighed goods, fast sales.", "Market, süpermarket: teraziler ve tartılı ürünler, hızlı satış.",
                "Oziq-ovqat do'koni, supermarket: tarozilar va tortiladigan mahsulotlar, tez sotuv."),
            MarketSpheres.Grocery));
        root.Children.Add(Tile("👕", Tr.T("Одежда, обувь и похожие", "Кийим, бут кийим жана ушул сыяктуулар", "Clothing, shoes and similar", "Giyim, ayakkabı ve benzeri", "Kiyim, poyabzal va shunga o'xshash"),
            Tr.T("Размеры и цвета, этикетки размеров, обмен по закону, консультант, прокат.", "Өлчөмдөр жана түстөр, өлчөм этикеткалары, мыйзам боюнча алмаштыруу, консультант, прокат.",
                "Sizes and colours, size labels, legal exchange, consultant, rentals.", "Beden ve renkler, beden etiketleri, yasal değişim, danışman, kiralama.",
                "O'lcham va ranglar, o'lcham yorliqlari, qonun bo'yicha almashtirish, maslahatchi, prokat."),
            MarketSpheres.Clothing));
        root.Children.Add(Tile("🛠", Tr.T("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
            Tr.T("Салон, ремонт, прокат: услуги без остатка на складе, прокат вещей.", "Салон, оңдоо, прокат: кампада калдыгы жок кызматтар, буюмдарды прокатка берүү.",
                "Salon, repair, rentals: services without stock, item rentals.", "Salon, tamir, kiralama: stoksuz hizmetler, eşya kiralama.",
                "Salon, ta'mirlash, prokat: omborda qoldiqsiz xizmatlar, buyumlarni prokatga berish."),
            MarketSpheres.Services));
        Content = root;
    }

    private Border Tile(string icon, string name, string description, string sphere)
    {
        var tile = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(tile, Border.BackgroundProperty, "BrushPanel");
        Use(tile, Border.BorderBrushProperty, "BrushBorder");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(new TextBlock { Text = icon, FontSize = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var n = new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.Bold };
        Use(n, TextBlock.ForegroundProperty, "BrushText");
        var d = new TextBlock { Text = description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(d, TextBlock.ForegroundProperty, "BrushTextSoft");
        texts.Children.Add(n);
        texts.Children.Add(d);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        tile.Child = g;
        tile.PointerEntered += (_, _) => Use(tile, Border.BorderBrushProperty, "BrushAccentStrong");
        tile.PointerExited += (_, _) => Use(tile, Border.BorderBrushProperty, "BrushBorder");
        tile.PointerPressed += (_, _) =>
        {
            Chosen = sphere;
            Close();
        };
        return tile;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
