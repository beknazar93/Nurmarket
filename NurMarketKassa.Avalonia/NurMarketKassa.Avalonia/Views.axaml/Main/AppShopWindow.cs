using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, запрос команды NurCRM («для Бекназара»): «экран „Магазин в приложении“ в программе владельца, чтобы
/// владельцы подключались сами» и «заметка для владельцев: „Ваш магазин в приложении NurCRM бесплатно“». Раздел программы
/// владельца: показывать ли магазин в приложении NurCRM для покупателей, название, адрес, телефон, часы работы, точка на
/// карте, бонусные баллы (включены и процент — тот же процент начисляет касса, ServerLoyalty), филиалы и предпросмотр карточки
/// магазина в приложении. Данные — GET/PATCH api/main/app-shop-settings/.</summary>
public sealed class AppShopWindow : Window, IOwnerSection
{
    private const string Path = "api/main/app-shop-settings/";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly NurMarketApiClient _api;
    private readonly Grid _root = new() { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    // 2026-10-20 (живой аудит: форма на левой половине, справа пусто): форма шириной до 900 по центру раздела (Stretch + MaxWidth).
    private readonly StackPanel _body = new() { Spacing = 12, MaxWidth = 900 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save;
    private JsonElement _data;

    private CheckBox? _show;
    private TextBox? _name;
    private TextBox? _address;
    private TextBox? _phone;
    private TextBox? _hours;
    private TextBox? _lat;
    private TextBox? _lon;
    private CheckBox? _pointsOn;
    private TextBox? _percent;
    private readonly List<(string Id, CheckBox Show, TextBox Name, TextBox Address, TextBox Phone, TextBox Hours)> _branches = new();

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Заметка для владельцев (и в «Сводке», если магазин ещё не в приложении).</summary>
    public static string FreeNote => T("Ваш магазин в приложении NurCRM — бесплатно", "Дүкөнүңүз NurCRM тиркемесинде — акысыз", "Your shop in the NurCRM app — free",
        "Mağazanız NurCRM uygulamasında — ücretsiz", "Do'koningiz NurCRM ilovasida — bepul");

    public AppShopWindow()
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Магазин в приложении", "Тиркемедеги дүкөн", "Shop in the app", "Uygulamadaki mağaza", "Ilovadagi do'kon");
        Width = 1000;
        Height = 820;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        // 2026-10-21 (пакет 4, п. 124; аудит: «галочки бирюзовые — ещё один цвет»): переключатели — как в Настройках
        // (CheckBox.ToggleSwitch из SettingsSharedStyles), а не синие галочки Fluent.
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://NurMarketKassa.Avalonia/"))
        {
            Source = new Uri("avares://NurMarketKassa.Avalonia/Views/Settings/SettingsSharedStyles.axaml"),
        });
        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(_title);
        _root.Children.Add(head);

        var scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        _root.Children.Add(scroll);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0), MaxWidth = 900 };
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        bottom.Children.Add(_status);
        _save = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Grid.SetColumn(_save, 1);
        bottom.Children.Add(_save);
        Grid.SetRow(bottom, 2);
        _root.Children.Add(bottom);
        Content = _root;

        _status.Text = T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>2026-10-20 (живой аудит: «Процент от покупки 4.00» — с точкой): процент с сервера — числом по культуре программы
    /// (4; 2,5). Сохранение и так принимает и запятую, и точку.</summary>
    private static string PercentText(string raw) =>
        double.TryParse(raw, NumberStyles.Float, Inv, out var p) ? p.ToString("0.##", CultureInfo.CurrentCulture) : raw;

    public void AsOwnerSection()
    {
        _title.IsVisible = false;
        _root.Margin = OwnerSectionLayout.Margin;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    private static bool? Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : null;

    private async Task LoadAsync()
    {
        try
        {
            _data = await _api.RequestAsync(HttpMethod.Get, Path, null, null, CancellationToken.None, TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            Render();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _status.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            PosLogger.Log($"Магазин в приложении: не загружено ({ex.Message}).", "WARNING");
        }
    }

    private void Render()
    {
        _body.Children.Clear();
        _branches.Clear();

        // Заметка: бесплатно и что это даёт.
        // 2026-10-21 (пакет 4, п. 124): плашка — светлая карточка с зелёной точкой «бесплатно», а не жёлтая: жёлтая на экране
        // одна — «Сохранить».
        var note = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), BorderThickness = new Thickness(1) };
        Use(note, Border.BackgroundProperty, "BrushPanel");
        Use(note, Border.BorderBrushProperty, "BrushBorder");
        var noteStack = new StackPanel { Spacing = 4 };
        var noteTitle = new TextBlock { Text = FreeNote, FontSize = 17, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Use(noteTitle, TextBlock.ForegroundProperty, "BrushText");
        var noteDot = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        Use(noteDot, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushSuccess");
        var noteHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        noteHead.Children.Add(noteDot);
        noteHead.Children.Add(noteTitle);
        noteStack.Children.Add(noteHead);
        noteStack.Children.Add(Hint(T("Покупатели находят ваш магазин в приложении NurCRM на карте, смотрят товары и цены, копят и тратят бонусные баллы. Подключение и работа — без оплаты.",
            "Кардарлар дүкөнүңүздү NurCRM тиркемесинен картадан табышат, товарларды жана бааларды көрүшөт, бонус упайларын топтоп, сарпташат. Кошуу жана иштөө — акысыз.",
            "Customers find your shop on the map in the NurCRM app, browse products and prices, and earn and spend bonus points. Connecting and using it costs nothing.",
            "Müşteriler mağazanızı NurCRM uygulamasında haritada bulur, ürünlere ve fiyatlara bakar, bonus puan kazanıp harcar. Bağlantı ve kullanım ücretsiz.",
            "Xaridorlar do'koningizni NurCRM ilovasida xaritada topadi, mahsulot va narxlarni ko'radi, bonus ballarini yig'ib sarflaydi. Ulash va ishlatish — bepul.")));
        note.Child = noteStack;
        _body.Children.Add(note);

        var main = Card(T("Магазин", "Дүкөн", "Shop", "Mağaza", "Do'kon"));
        _show = Toggle(T("Показывать магазин в приложении NurCRM", "Дүкөндү NurCRM тиркемесинде көрсөтүү", "Show the shop in the NurCRM app", "Mağazayı NurCRM uygulamasında göster", "Do'konni NurCRM ilovasida ko'rsatish"), Flag(_data, "show_in_app") == true, bold: true);
        main.Children.Add(_show);
        _name = Field(main, T("Название в приложении", "Тиркемедеги аталышы", "Name in the app", "Uygulamadaki ad", "Ilovadagi nomi"), Str(_data, "display_name"));
        _address = Field(main, T("Адрес (город, улица, дом)", "Дарек (шаар, көчө, үй)", "Address (city, street, building)", "Adres (şehir, sokak, bina)", "Manzil (shahar, ko'cha, uy)"), Str(_data, "address"));
        // 2026-10-21 (пакет 4, п. 124): «Телефон» и «Часы работы» — в одну строку (форма короче, не нужно листать до «Сохранить»).
        var pair = Pair(main);
        _phone = Field(pair.Left, T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), Str(_data, "phone"));
        // Пример — подсказкой в пустом поле, чтобы подписи двух полей были одной высоты.
        _hours = Field(pair.Right, T("Часы работы", "Иштөө убактысы", "Opening hours", "Çalışma saatleri", "Ish vaqti"), Str(_data, "hours"));
        _hours.Watermark = T("например: ежедневно 9:00–21:00", "мисалы: күн сайын 9:00–21:00", "e.g. daily 9:00–21:00", "ör. her gün 9:00–21:00", "masalan: har kuni 9:00–21:00");

        // 2026-10-19, этап О10 (аудит: «Точка на карте — два поля с числами без подписей»): над полями — «Широта» и «Долгота».
        var map = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var latLabel = Hint(T("Широта", "Кеңдик", "Latitude", "Enlem", "Kenglik"));
        var lonLabel = Hint(T("Долгота", "Узундук", "Longitude", "Boylam", "Uzunlik"));
        latLabel.Margin = lonLabel.Margin = new Thickness(2, 0, 0, 4);
        Grid.SetColumn(lonLabel, 2);
        map.Children.Add(latLabel);
        map.Children.Add(lonLabel);
        _lat = new TextBox { Text = Str(_data, "latitude"), Watermark = T("широта, 42.8746", "кеңдик, 42.8746", "latitude, 42.8746", "enlem, 42.8746", "kenglik, 42.8746"), MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
        _lon = new TextBox { Text = Str(_data, "longitude"), Watermark = T("долгота, 74.5698", "узундук, 74.5698", "longitude, 74.5698", "boylam, 74.5698", "uzunlik, 74.5698"), MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_lon, 2);
        Grid.SetRow(_lat, 1);
        Grid.SetRow(_lon, 1);
        var openMap = UiKit.Ghost(this, T("Открыть на карте", "Картада ачуу", "Open on the map", "Haritada aç", "Xaritada ochish"));
        // 2026-10-21 (пакет 4, п. 124): кнопка высотой с поля (было 46 — поля «Широта/Долгота» растягивались выше остальных).
        openMap.Height = openMap.MinHeight = 38;
        openMap.VerticalAlignment = VerticalAlignment.Center;
        openMap.Click += (_, _) =>
        {
            var lat = (_lat.Text ?? "").Trim().Replace(',', '.');
            var lon = (_lon.Text ?? "").Trim().Replace(',', '.');
            var url = lat.Length > 0 && lon.Length > 0
                ? $"https://www.google.com/maps?q={Uri.EscapeDataString(lat)},{Uri.EscapeDataString(lon)}"
                : $"https://www.google.com/maps/search/{Uri.EscapeDataString(_address.Text ?? "")}";
            SiteOrdersWindow.OpenUrl(url);
        };
        Grid.SetColumn(openMap, 4);
        Grid.SetRow(openMap, 1);
        map.Children.Add(_lat);
        map.Children.Add(_lon);
        map.Children.Add(openMap);
        main.Children.Add(Label(T("Точка на карте", "Картадагы чекит", "Point on the map", "Haritadaki nokta", "Xaritadagi nuqta")));
        main.Children.Add(map);
        var geo = Str(_data, "geocode_status");
        // Точка уже стоит — предупреждать не о чем, даже если сервер не нашёл адрес текстом.
        var hasPoint = Str(_data, "latitude").Length > 0 && Str(_data, "longitude").Length > 0;
        main.Children.Add(Hint(geo == "not_found" && !hasPoint
            ? T("⚠ Адрес не нашёлся на карте — уточните его (город, улица, дом) или укажите широту и долготу: откройте карту, нажмите на свой магазин и скопируйте числа.",
                "⚠ Дарек картадан табылган жок — тактаңыз (шаар, көчө, үй) же кеңдик менен узундукту жазыңыз: картаны ачып, дүкөнүңүздү басып, сандарды көчүрүңүз.",
                "⚠ The address wasn't found on the map — make it more precise (city, street, building) or enter latitude and longitude: open the map, tap your shop and copy the numbers.",
                "⚠ Adres haritada bulunamadı — netleştirin (şehir, sokak, bina) ya da enlem ve boylam girin: haritayı açın, mağazanıza dokunun ve sayıları kopyalayın.",
                "⚠ Manzil xaritada topilmadi — aniqlang (shahar, ko'cha, uy) yoki kenglik va uzunlikni kiriting: xaritani oching, do'koningizni bosing va raqamlarni nusxalang.")
            : T("Без точки на карте сервер сам ищет адрес. Точнее — указать широту и долготу.", "Картада чекит жок болсо, сервер даректи өзү издейт. Тагыраак — кеңдик менен узундукту жазуу.",
                "Without a map point the server looks the address up itself. Entering latitude and longitude is more precise.", "Harita noktası yoksa sunucu adresi kendisi arar. Enlem ve boylam girmek daha kesindir.",
                "Xaritada nuqta bo'lmasa, server manzilni o'zi qidiradi. Aniqroq — kenglik va uzunlikni kiritish.")));

        var points = Card(T("Бонусные баллы", "Бонус упайлары", "Bonus points", "Bonus puanlar", "Bonus ballari"));
        _pointsOn = Toggle(T("Начислять баллы покупателям", "Кардарларга упай берүү", "Give customers points", "Müşterilere puan ver", "Xaridorlarga ball berish"), Flag(_data, "points_enabled") != false);
        points.Children.Add(_pointsOn);
        _percent = Field(points, T("Процент от покупки, %", "Сатып алуудан пайыз, %", "Percent of the purchase, %", "Alışverişin yüzdesi, %", "Xariddan foiz, %"), PercentText(Str(_data, "points_percent")));
        _percent.Width = 140;
        _percent.HorizontalAlignment = HorizontalAlignment.Left;
        points.Children.Add(Hint(T("Этот процент начисляют все кассы магазина, покупатель видит баланс в приложении NurCRM и тратит его при оплате.",
            "Бул пайызды дүкөндүн бардык кассалары берет, кардар балансын NurCRM тиркемесинде көрүп, төлөөдө сарптайт.",
            "All of the shop's tills award this percentage; customers see their balance in the NurCRM app and spend it at checkout.",
            "Bu yüzdeyi mağazanın tüm kasaları verir; müşteri bakiyesini NurCRM uygulamasında görür ve ödemede harcar.",
            "Bu foizni do'konning barcha kassalari beradi; xaridor balansini NurCRM ilovasida ko'radi va to'lovda sarflaydi.")));

        if (_data.TryGetProperty("branches", out var branches) && branches.ValueKind == JsonValueKind.Array && branches.GetArrayLength() > 0)
        {
            var card = Card(T("Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar"));
            card.Children.Add(Hint(T("Пустые поля филиала — как у магазина.", "Филиалдын бош талаалары — дүкөндөгүдөй.", "Empty branch fields use the shop's values.", "Boş şube alanları mağazanınkini kullanır.", "Filialning bo'sh maydonlari — do'konnikidek.")));
            foreach (var b in branches.EnumerateArray())
            {
                var box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10) };
                Use(box, Border.BorderBrushProperty, "BrushBorder");
                var stack = new StackPanel { Spacing = 6 };
                // 2026-10-21 (пакет 4, п. 124): нет branch_name — имя филиала из «Названия» (было «— — показывать…»); переключатель вместо галочки.
                var branchName = Str(b, "branch_name") is { Length: > 0 } bn ? bn : Str(b, "display_name") is { Length: > 0 } dn ? dn : "—";
                var show = Toggle(branchName + " — " + T("показывать в приложении", "тиркемеде көрсөтүү", "show in the app", "uygulamada göster", "ilovada ko'rsatish"), Flag(b, "show_in_app") != false, bold: true);
                stack.Children.Add(show);
                var name = Field(stack, T("Название", "Аталышы", "Name", "Ad", "Nomi"), Str(b, "display_name"));
                var address = Field(stack, T("Адрес", "Дарек", "Address", "Adres", "Manzil"), Str(b, "address"));
                var branchPair = Pair(stack);
                var phone = Field(branchPair.Left, T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), Str(b, "phone"));
                var hours = Field(branchPair.Right, T("Часы работы", "Иштөө убактысы", "Opening hours", "Çalışma saatleri", "Ish vaqti"), Str(b, "hours"));
                if (Str(b, "geocode_status") == "not_found")
                {
                    // 2026-10-19, этап О10 (аудит: «адрес не нашёлся на карте», но нет кнопки «на карте»): рядом — поиск адреса на карте.
                    var geoRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
                    var geoHint = Hint(T("⚠ Адрес филиала не нашёлся на карте — уточните его.", "⚠ Филиалдын дареги картадан табылган жок — тактаңыз.", "⚠ The branch address wasn't found on the map — make it more precise.",
                        "⚠ Şube adresi haritada bulunamadı — netleştirin.", "⚠ Filial manzili xaritada topilmadi — aniqlang."));
                    geoHint.VerticalAlignment = VerticalAlignment.Center;
                    geoRow.Children.Add(geoHint);
                    var findOnMap = UiKit.Ghost(this, T("Найти на карте", "Картадан табуу", "Find on the map", "Haritada bul", "Xaritada topish"));
                    findOnMap.Height = 36;
                    findOnMap.Click += (_, _) => SiteOrdersWindow.OpenUrl($"https://www.google.com/maps/search/{Uri.EscapeDataString(address.Text ?? "")}");
                    Grid.SetColumn(findOnMap, 2);
                    geoRow.Children.Add(findOnMap);
                    stack.Children.Add(geoRow);
                }
                box.Child = stack;
                card.Children.Add(box);
                _branches.Add((Str(b, "branch_id"), show, name, address, phone, hours));
            }
        }

        if (_data.TryGetProperty("app_preview", out var preview) && preview.ValueKind == JsonValueKind.Array && preview.GetArrayLength() > 0)
        {
            var card = Card(T("Так покупатели видят магазин в приложении", "Кардарлар дүкөндү тиркемеде ушундай көрүшөт", "This is how customers see the shop in the app",
                "Müşteriler mağazayı uygulamada böyle görür", "Xaridorlar do'konni ilovada shunday ko'radi"));
            var wrap = new WrapPanel();
            foreach (var p in preview.EnumerateArray())
            {
                var tile = new Border { Width = 280, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 10, 10), BorderThickness = new Thickness(1) };
                Use(tile, Border.BackgroundProperty, "BrushPanelSoft");
                Use(tile, Border.BorderBrushProperty, "BrushBorder");
                var st = new StackPanel { Spacing = 3 };
                var nm = new TextBlock { Text = Str(p, "name"), FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
                Use(nm, TextBlock.ForegroundProperty, "BrushText");
                st.Children.Add(nm);
                foreach (var line in new[] { Str(p, "address"), Str(p, "phone"), Str(p, "hours") }.Where(x => x.Length > 0))
                    st.Children.Add(Hint(line));
                if (Flag(p, "pointsEnabled") == true)
                {
                    var pts = new TextBlock { Text = "★ " + T($"Баллы {Str(p, "pointsPercent")} %", $"Упайлар {Str(p, "pointsPercent")} %", $"Points {Str(p, "pointsPercent")} %", $"Puan %{Str(p, "pointsPercent")}", $"Ballar {Str(p, "pointsPercent")} %"), FontSize = 13, FontWeight = FontWeight.SemiBold };
                    // 2026-10-19, этап О10: цветом текста — жёлтый текст по светлому не читался.
                    Use(pts, TextBlock.ForegroundProperty, "BrushText");
                    st.Children.Add(pts);
                }
                tile.Child = st;
                wrap.Children.Add(tile);
            }
            card.Children.Add(wrap);
        }
    }

    private async Task SaveAsync()
    {
        if (_show is null || _name is null || _address is null || _phone is null || _hours is null || _lat is null || _lon is null || _pointsOn is null || _percent is null)
            return;
        var body = new Dictionary<string, object?>
        {
            ["show_in_app"] = _show.IsChecked == true,
            ["display_name"] = (_name.Text ?? "").Trim(),
            ["address"] = (_address.Text ?? "").Trim(),
            ["phone"] = (_phone.Text ?? "").Trim(),
            ["hours"] = (_hours.Text ?? "").Trim(),
            ["points_enabled"] = _pointsOn.IsChecked == true,
        };
        var percentText = (_percent.Text ?? "").Trim().Replace(',', '.');
        if (percentText.Length > 0)
        {
            if (!double.TryParse(percentText, NumberStyles.Float, Inv, out var percent) || percent < 0 || percent > 100)
            {
                _status.Text = T("Процент — число от 0 до 100.", "Пайыз — 0дөн 100гө чейинки сан.", "The percentage must be a number from 0 to 100.", "Yüzde 0 ile 100 arasında bir sayı olmalı.", "Foiz — 0 dan 100 gacha son.");
                return;
            }
            body["points_percent"] = percent.ToString("0.##", Inv);
        }
        var lat = (_lat.Text ?? "").Trim().Replace(',', '.');
        var lon = (_lon.Text ?? "").Trim().Replace(',', '.');
        if (lat.Length > 0 || lon.Length > 0)
        {
            if (!double.TryParse(lat, NumberStyles.Float, Inv, out var la) || !double.TryParse(lon, NumberStyles.Float, Inv, out var lo) || Math.Abs(la) > 90 || Math.Abs(lo) > 180)
            {
                _status.Text = T("Широта и долгота — числа, например 42.8746 и 74.5698.", "Кеңдик жана узундук — сандар, мисалы 42.8746 жана 74.5698.", "Latitude and longitude must be numbers, e.g. 42.8746 and 74.5698.",
                    "Enlem ve boylam sayı olmalı, ör. 42.8746 ve 74.5698.", "Kenglik va uzunlik — sonlar, masalan 42.8746 va 74.5698.");
                return;
            }
            body["latitude"] = la.ToString("0.######", Inv);
            body["longitude"] = lo.ToString("0.######", Inv);
        }
        if (_branches.Count > 0)
            body["branches"] = _branches.Select(b => new Dictionary<string, object?>
            {
                ["branch_id"] = b.Id,
                ["show_in_app"] = b.Show.IsChecked == true,
                ["display_name"] = (b.Name.Text ?? "").Trim(),
                ["address"] = (b.Address.Text ?? "").Trim(),
                ["phone"] = (b.Phone.Text ?? "").Trim(),
                ["hours"] = (b.Hours.Text ?? "").Trim(),
            }).ToList();
        _save.IsEnabled = false;
        _status.Text = T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…");
        try
        {
            _data = await _api.RequestAsync(new HttpMethod("PATCH"), Path, body, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (_data.ValueKind != JsonValueKind.Object)
                _data = await _api.RequestAsync(HttpMethod.Get, Path, null, null, CancellationToken.None, TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            Render();
            _status.Text = T("Сохранено — в приложении NurCRM магазин обновится сам.", "Сакталды — NurCRM тиркемесинде дүкөн өзү жаңырат.", "Saved — the shop updates in the NurCRM app by itself.",
                "Kaydedildi — mağaza NurCRM uygulamasında kendiliğinden güncellenir.", "Saqlandi — do'kon NurCRM ilovasida o'zi yangilanadi.");
            PosLogger.Log($"Магазин в приложении: сохранено (показывать={body["show_in_app"]}, баллы={body["points_enabled"]}).", "INFO");
            _ = ServerLoyalty.RefreshShopSettingsAsync();
        }
        catch (Exception ex)
        {
            _status.Text = T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            PosLogger.Log($"Магазин в приложении: не сохранено ({ex.Message}).", "WARNING");
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }

    private StackPanel Card(string title)
    {
        var stack = new StackPanel { Spacing = 8 };
        var caption = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold };
        Use(caption, TextBlock.ForegroundProperty, "BrushText");
        stack.Children.Add(caption);
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), BorderThickness = new Thickness(1), Child = stack };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        _body.Children.Add(card);
        return stack;
    }

    private TextBox Field(StackPanel host, string label, string value)
    {
        host.Children.Add(Label(label));
        var box = new TextBox { Text = value, MinHeight = 38, VerticalContentAlignment = VerticalAlignment.Center };
        host.Children.Add(box);
        return box;
    }

    /// <summary>2026-10-21 (пакет 4, п. 124): переключатель в стиле Настроек (CheckBox.ToggleSwitch, подпись цветом текста).</summary>
    private CheckBox Toggle(string text, bool isChecked, bool bold = false)
    {
        var check = new CheckBox { Content = text, IsChecked = isChecked, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 2, 0, 2) };
        check.Classes.Add("ToggleSwitch");
        Avalonia.Automation.AutomationProperties.SetName(check, text);
        return check;
    }

    /// <summary>2026-10-21 (пакет 4, п. 124): два поля в одну строку (левое и правое), с промежутком 12.</summary>
    private static (StackPanel Left, StackPanel Right) Pair(StackPanel host)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var left = new StackPanel { Spacing = 8 };
        var right = new StackPanel { Spacing = 8 };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        host.Children.Add(grid);
        return (left, right);
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
