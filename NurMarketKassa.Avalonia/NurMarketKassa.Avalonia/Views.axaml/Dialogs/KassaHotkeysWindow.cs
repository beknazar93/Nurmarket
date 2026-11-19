using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-13 (Н-2): шпаргалка клавиш кассы — одно окно «что нажать», открывается по «?», по F1 (если на F1 нет группы быстрых товаров),
/// из меню «⋯» шапки и из подсказки пустого чека. Клавиши действий — те, что назначены в «Горячих» (PosHotkeyService), остальные — постоянные.
/// Закрывается Esc, Enter и кнопкой; ничего не меняет.</summary>
public sealed class KassaHotkeysWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string WindowTitle => T("Клавиши кассы", "Кассанын баскычтары", "Till keys", "Kasa tuşları", "Kassa tugmalari");

    /// <summary>Строка шпаргалки: клавиша и что она делает.</summary>
    public sealed record Row(string Keys, string Action);

    /// <summary>Раздел шпаргалки.</summary>
    public sealed record Section(string Title, IReadOnlyList<Row> Rows);

    /// <summary>Содержимое шпаргалки (чистые данные — проверяются тестом). <paramref name="gesture"/> — назначенная клавиша действия («» — снята).</summary>
    public static IReadOnlyList<Section> Build(Func<PosHotkeyAction, string> gesture)
    {
        string Key(PosHotkeyAction action) => PosHotkeyService.Display(gesture(action));

        var sale = new List<Row>
        {
            new(T("Сканер", "Сканер", "Scanner", "Tarayıcı", "Skaner"),
                T("Товар в чек — из любого места экрана", "Товар чекке — экрандын каалаган жеринен", "Item to the receipt — from anywhere on the screen",
                    "Ürün fişe — ekranın her yerinden", "Mahsulot chekka — ekranning istalgan joyidan")),
            new(T("3 * скан", "3 * скан", "3 * scan", "3 * okut", "3 * skan"),
                T("Три штуки одним сканом (или «3*код» в поле ввода)", "Бир скан менен үч даана (же киргизүү талаасында «3*код»)", "Three pieces with one scan (or “3*code” in the input field)",
                    "Tek okutmayla üç adet (veya giriş alanında «3*kod»)", "Bir skan bilan uch dona (yoki kiritish maydonida «3*kod»)")),
        };
        void Add(List<Row> rows, PosHotkeyAction action, string text)
        {
            var key = Key(action);
            if (!string.IsNullOrWhiteSpace(key))
                rows.Add(new Row(key, text));
        }
        Add(sale, PosHotkeyAction.FocusProductSearch, T("Курсор в поле ввода: код или название, Enter — в чек", "Курсор киргизүү талаасына: код же аты, Enter — чекке",
            "Cursor to the input field: code or name, Enter adds to the receipt", "İmleç giriş alanına: kod veya ad, Enter — fişe", "Kursor kiritish maydoniga: kod yoki nom, Enter — chekka"));
        sale.Add(new Row("← ↑ ↓ →", T("Выбрать товар в каталоге", "Каталогдон товар тандоо", "Pick a product in the catalog", "Katalogda ürün seç", "Katalogdan mahsulot tanlash")));
        Add(sale, PosHotkeyAction.AddCatalogItem, T("Добавить выбранный товар / ещё одну штуку", "Тандалган товарды / дагы бир даана кошуу", "Add the picked product / one more piece",
            "Seçili ürünü / bir adet daha ekle", "Tanlangan mahsulotni / yana bir dona qo'shish"));
        Add(sale, PosHotkeyAction.DecreaseQuantity, T("Убавить на одну / убрать строку", "Бирди азайтуу / сапты алып салуу", "One less / remove the line", "Bir azalt / satırı kaldır", "Bittaga kamaytirish / qatorni olib tashlash"));
        sale.Add(new Row("F1 – F12", T("Группа быстрых товаров (если назначена)", "Ыкчам товарлар тобу (эгер дайындалса)", "Quick product group (if assigned)", "Hızlı ürün grubu (atanmışsa)", "Tezkor mahsulotlar guruhi (belgilangan bo'lsa)")));

        var pay = new List<Row>();
        Add(pay, PosHotkeyAction.Pay, T("Оплатить чек", "Чекти төлөө", "Pay the receipt", "Fişi öde", "Chekni to'lash"));
        pay.Add(new Row("F1 · F2 · F3 · F4", T("В окне оплаты: наличные, безнал, смешанная, в долг", "Төлөм терезесинде: накталай, накталай эмес, аралаш, карызга",
            "In the payment window: cash, cashless, mixed, on credit", "Ödeme penceresinde: nakit, kart, karma, veresiye", "To'lov oynasida: naqd, naqdsiz, aralash, qarzga")));
        pay.Add(new Row("Enter", T("В окне оплаты: провести оплату", "Төлөм терезесинде: төлөмдү өткөрүү", "In the payment window: complete the payment", "Ödeme penceresinde: ödemeyi tamamla", "To'lov oynasida: to'lovni o'tkazish")));
        pay.Add(new Row("Esc", T("Закрыть окно, выйти из поля, сбросить «3*»", "Терезени жабуу, талаадан чыгуу, «3*» тазалоо", "Close a window, leave a field, cancel “3*”", "Pencereyi kapat, alandan çık, «3*» iptal", "Oynani yopish, maydondan chiqish, «3*» bekor qilish")));

        var other = new List<Row>();
        Add(other, PosHotkeyAction.ApplyDiscount, T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chekka chegirma"));
        Add(other, PosHotkeyAction.ClearCart, T("Очистить чек", "Чекти тазалоо", "Clear the receipt", "Fişi temizle", "Chekni tozalash"));
        Add(other, PosHotkeyAction.ToggleCustomerDisplay, T("Экран покупателя", "Сатып алуучунун экраны", "Customer display", "Müşteri ekranı", "Xaridor ekrani"));
        other.Add(new Row("F5", T("Обновить товары и смену (если на F5 нет группы)", "Товарларды жана сменаны жаңыртуу (F5те топ жок болсо)", "Refresh products and shift (if F5 has no group)",
            "Ürünleri ve vardiyayı yenile (F5'te grup yoksa)", "Mahsulotlar va smenani yangilash (F5 da guruh bo'lmasa)")));
        other.Add(new Row("?", T("Эта шпаргалка", "Ушул эскертме", "This cheat sheet", "Bu kısa kılavuz", "Ushbu eslatma")));

        return
        [
            new Section(T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"), sale),
            new Section(T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), pay),
            new Section(T("Ещё", "Дагы", "More", "Diğer", "Yana"), other),
        ];
    }

    public KassaHotkeysWindow() : this(new PosHotkeyService())
    {
    }

    public KassaHotkeysWindow(PosHotkeyService hotkeys)
    {
        Title = WindowTitle;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var body = new StackPanel { Spacing = 14 };
        foreach (var section in Build(action => SafeGesture(hotkeys, action)))
        {
            var title = new TextBlock { Text = section.Title, FontWeight = FontWeight.Bold };
            title.Bind(TextBlock.FontSizeProperty, title.GetResourceObservable("NurFontSubtitle"));
            body.Children.Add(title);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("182,*") }; // у Grid в Avalonia 11.2.5 нет RowSpacing/ColumnSpacing — отступы полями элементов
            for (var i = 0; i < section.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var keyText = new TextBlock { Text = section.Rows[i].Keys, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
                keyText.Bind(TextBlock.FontSizeProperty, keyText.GetResourceObservable("NurFontBody"));
                var cap = new Border { Margin = new Thickness(0, 0, 12, 6), Padding = new Thickness(8, 3), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = keyText };
                cap.Classes.Add("hotkey-cap");
                cap.Bind(Border.CornerRadiusProperty, cap.GetResourceObservable("NurRadiusSmall"));
                cap.Bind(Border.BorderBrushProperty, cap.GetResourceObservable("BrushBorderStrong"));
                cap.Bind(Border.BackgroundProperty, cap.GetResourceObservable("BrushInputAlt"));
                var action = new TextBlock { Text = section.Rows[i].Action, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                action.Bind(TextBlock.FontSizeProperty, action.GetResourceObservable("NurFontBody"));
                Grid.SetRow(cap, i);
                Grid.SetRow(action, i);
                Grid.SetColumn(action, 1);
                grid.Children.Add(cap);
                grid.Children.Add(action);
            }
            body.Children.Add(grid);
        }

        var close = new Button { Name = "HotkeysClose", Content = T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli"), IsDefault = true, IsCancel = true };
        close.Classes.Add("nur-primary");
        close.Click += (_, _) => Close();

        Content = new ModalShell
        {
            Margin = new Thickness(16),
            Title = WindowTitle,
            Subtitle = T("Изменить клавиши: меню «⋯» → «Горячие клавиши»", "Баскычтарды өзгөртүү: «⋯» менюсу → «Ыкчам баскычтар»", "Change the keys: “⋯” menu → “Hotkeys”",
                "Tuşları değiştir: «⋯» menüsü → «Kısayol tuşları»", "Tugmalarni o'zgartirish: «⋯» menyusi → «Tezkor tugmalar»"),
            Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            Content = body,
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F1)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private static string SafeGesture(PosHotkeyService hotkeys, PosHotkeyAction action)
    {
        try
        {
            return hotkeys.GetGesture(action) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
