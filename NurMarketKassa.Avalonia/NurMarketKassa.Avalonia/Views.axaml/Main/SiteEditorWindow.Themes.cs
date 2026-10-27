using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-27, ТЗ «Сайт — темы для магазина одежды» (С-0, С-2): галерея готовых тем сайта вынесена из SiteEditorWindow.cs
/// (класс стал partial). Темы читает SiteThemeCatalog: 6 прежних и 10 тем одежды. В сфере «Одежда» темы одежды идут первыми и открыт
/// фильтр «Одежда и обувь». У карточки: «Посмотреть» (местный мини-сайт SiteThemePreview, без запросов на сервер) и «Применить к сайту».</summary>
public sealed partial class SiteEditorWindow
{
    private enum ThemeFilter { Clothing, Any, All }

    private ThemeFilter? _themeFilter;

    /// <summary>Галерея тем: превью, название, для какого магазина, «Посмотреть» и «Применить к сайту»; нынешняя тема отмечена.</summary>
    private void BuildThemes(StackPanel host)
    {
        host.Children.Add(Hint(T("Весь вид сайта одним нажатием: цвета, шрифт, шапка, главный блок с картинкой, карточки товаров, подвал. "
                                 + "Название, телефон, тексты и товары магазина не меняются. Покупатели увидят тему после «Опубликовать».",
            "Сайттын бүт көрүнүшү бир басуу менен: түстөр, шрифт, баш бөлүк, сүрөттүү башкы блок, товар карточкалары, төмөнкү бөлүк. "
            + "Дүкөндүн аты, телефону, тексттери жана товарлары өзгөрбөйт. Кардарлар теманы «Жарыялоодон» кийин көрүшөт.",
            "The whole website look in one click: colours, font, header, main block with a picture, product cards, footer. "
            + "The shop's name, phone, texts and products stay. Customers see the theme after “Publish”.",
            "Sitenin tüm görünümü tek tıkla: renkler, yazı tipi, üst bölüm, resimli ana blok, ürün kartları, alt bilgi. "
            + "Mağaza adı, telefon, metinler ve ürünler değişmez. Müşteriler temayı «Yayınla»dan sonra görür.",
            "Saytning butun ko'rinishi bir bosishda: ranglar, shrift, sarlavha, rasmli asosiy blok, mahsulot kartochkalari, pastki qism. "
            + "Do'kon nomi, telefoni, matnlari va mahsulotlari o'zgarmaydi. Xaridorlar mavzuni «E'lon qilish»dan keyin ko'radi.")));

        var sphere = UserPreferences.Instance.MarketSphere;
        var themes = SiteThemeCatalog.Ordered(sphere);
        _themeFilter ??= SiteThemeCatalog.All.Any(SiteThemeCatalog.IsClothing) && sphere == MarketSpheres.Clothing ? ThemeFilter.Clothing : ThemeFilter.All;

        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fill()
        {
            chips.Children.Clear();
            void AddChip(string text, ThemeFilter filter)
            {
                var chip = Chip(text, _themeFilter == filter);
                chip.Click += (_, _) => { _themeFilter = filter; Fill(); };
                chips.Children.Add(chip);
            }
            if (themes.Any(SiteThemeCatalog.IsClothing))
                AddChip(T("Одежда и обувь", "Кийим жана бут кийим", "Clothing and shoes", "Giyim ve ayakkabı", "Kiyim va poyabzal"), ThemeFilter.Clothing);
            if (themes.Any(t => t.Spheres.Count == 0))
                AddChip(T("Для любого магазина", "Ар кандай дүкөн үчүн", "For any shop", "Her mağaza için", "Har qanday do'kon uchun"), ThemeFilter.Any);
            AddChip(T("Все", "Баары", "All", "Tümü", "Hammasi"), ThemeFilter.All);
            wrap.Children.Clear();
            foreach (var theme in themes.Where(t => _themeFilter switch
                     {
                         ThemeFilter.Clothing => SiteThemeCatalog.IsClothing(t),
                         ThemeFilter.Any => t.Spheres.Count == 0,
                         _ => true,
                     }))
                wrap.Children.Add(ThemeCard(theme));
        }
        host.Children.Add(chips);
        host.Children.Add(wrap);
        Fill();
    }

    private Border ThemeCard(SiteThemeInfo theme)
    {
        var current = SiteThemeCatalog.Matches(theme, Str);
        var card = new Border { Width = 184, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(current ? 2 : 1), Margin = new Thickness(0, 0, 8, 8), ClipToBounds = true };
        // 2026-10-19, этап О10 (аудит: 5+ жёлтых «Применить»): карточка темы белая, выбранная — тёмной рамкой (как темы
        // Маркетплейса), «Применить» — обводкой; жёлтая на экране одна — «Опубликовать».
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, current ? "BrushText" : "BrushBorder");
        var stack = new StackPanel();
        try
        {
            if (AssetLoader.Exists(theme.ThumbUri))
            {
                using var thumb = AssetLoader.Open(theme.ThumbUri);
                stack.Children.Add(new Image { Source = new Bitmap(thumb), Height = 115, Stretch = Stretch.UniformToFill });
            }
            else
                stack.Children.Add(SiteThemePreview.Thumb(theme, 184, 115)); // нет файла превью — рисуем кодом
        }
        catch
        {
            // нет превью — только название
        }
        var info = new StackPanel { Spacing = 3, Margin = new Thickness(10, 8, 10, 10) };
        var name = new TextBlock { Text = theme.Name + (current ? "  ✓" : ""), FontSize = 14, FontWeight = FontWeight.Bold };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        info.Children.Add(name);
        var about = Hint(theme.Description);
        about.MinHeight = 32;
        info.Children.Add(about);
        var look = UiKit.Ghost(this, T("Посмотреть", "Көрүү", "Preview", "Önizle", "Ko'rish"));
        look.Height = 32;
        look.Margin = new Thickness(0, 6, 0, 0);
        look.HorizontalAlignment = HorizontalAlignment.Stretch;
        look.Click += async (_, _) =>
        {
            if (await PreviewThemeAsync(theme).ConfigureAwait(true))
                await ApplyThemeAsync(theme).ConfigureAwait(true);
        };
        info.Children.Add(look);
        var apply = current
            ? UiKit.Ghost(this, T("Применена", "Колдонулду", "Applied", "Uygulandı", "Qo'llangan"))
            : UiKit.Ghost(this, T("Применить к сайту", "Сайтка колдонуу", "Apply to website", "Siteye uygula", "Saytga qo'llash"));
        apply.Height = 32;
        apply.Margin = new Thickness(0, 2, 0, 0);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        apply.Click += async (_, _) => await ApplyThemeAsync(theme).ConfigureAwait(true);
        info.Children.Add(apply);
        stack.Children.Add(info);
        card.Child = stack;
        return card;
    }

    /// <summary>«Посмотреть»: мини-сайт темы в её цветах (компьютер или телефон). Только ресурсы программы — на сервер ничего не уходит.
    /// Ответ true — владелец нажал «Применить к сайту» прямо в окне просмотра.</summary>
    private async Task<bool> PreviewThemeAsync(SiteThemeInfo theme)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window { Title = Title };
        // 2026-10-27 (аудит редакторов, С-1): окно просмотра было 1100 px шириной — на экране 1024×600 «Применить к сайту» уходило за край.
        // Ширина и высота прокрутки — по рабочей области экрана, где стоит редактор.
        double screenW = 1280, screenH = 800;
        try
        {
            var sc = TopLevel.GetTopLevel(this)?.Screens is { } screens ? screens.ScreenFromVisual(this) ?? screens.Primary : null;
            if (sc is not null && sc.Scaling > 0)
            {
                screenW = sc.WorkingArea.Width / sc.Scaling;
                screenH = sc.WorkingArea.Height / sc.Scaling;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор сайта: размер экрана для просмотра темы не определён ({ex.Message}).", "DEBUG");
        }
        SectionCards.AsModal(dialog, Math.Min(1100, Math.Max(600, screenW - 32)));
        var phone = false;
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = Math.Clamp(Math.Min(Bounds.Height - 240, screenH - 300), 200, 560),
        };
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        void Redraw()
        {
            var view = SiteThemePreview.Build(theme, phone);
            view.HorizontalAlignment = HorizontalAlignment.Center;
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Child = view };
            Use(frame, Border.BorderBrushProperty, "BrushBorder");
            scroll.Content = frame;
            chips.Children.Clear();
            var desktop = Chip(T("Компьютер", "Компьютер", "Desktop", "Bilgisayar", "Kompyuter"), !phone);
            desktop.Click += (_, _) => { phone = false; Redraw(); };
            var mobile = Chip(T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), phone);
            mobile.Click += (_, _) => { phone = true; Redraw(); };
            chips.Children.Add(desktop);
            chips.Children.Add(mobile);
        }
        Redraw();
        var note = Hint(T("Так тема выглядит на сайте. Фото, цены и тексты здесь условные — на сайте будут ваши товары, название и тексты. Ничего не отправляется на сервер.",
            "Тема сайтта ушундай көрүнөт. Сүрөттөр, баалар жана тексттер бул жерде шарттуу — сайтта сиздин товарларыңыз, атыңыз жана тексттериңиз болот. Серверге эч нерсе жөнөтүлбөйт.",
            "This is how the theme looks on the website. Photos, prices and texts here are samples — the site will show your products, name and texts. Nothing is sent to the server.",
            "Tema sitede böyle görünür. Buradaki fotoğraf, fiyat ve metinler örnektir — sitede ürünleriniz, adınız ve metinleriniz olur. Sunucuya hiçbir şey gönderilmez.",
            "Mavzu saytda shunday ko'rinadi. Rasm, narx va matnlar bu yerda namunaviy — saytda sizning mahsulotlaringiz, nomingiz va matnlaringiz bo'ladi. Serverga hech narsa yuborilmaydi."));
        var content = new StackPanel { Spacing = 8, Children = { chips, scroll, note } };
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        var apply = SectionCards.Primary(T("Применить к сайту", "Сайтка колдонуу", "Apply to website", "Siteye uygula", "Saytga qo'llash"));
        apply.IsEnabled = !_locked;
        apply.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        close.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Content = SectionCards.Shell(theme.Name, theme.Description, content, SectionCards.Buttons(close, apply));
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    /// <summary>2026-10-27: тема, выбранная в Маркетплейсе («Открыть в редакторе сайта»): показываем галерею с этой темой и спрашиваем подтверждение.</summary>
    public async Task ApplyPendingThemeAsync()
    {
        var code = SiteThemeCatalog.PendingThemeCode;
        SiteThemeCatalog.PendingThemeCode = null;
        if (code is null || SiteThemeCatalog.Find(code) is not { } theme || !_loadedOnce)
            return;
        _editorScroll.Offset = default;
        await ApplyThemeAsync(theme).ConfigureAwait(true);
    }

    /// <summary>Свой баннер магазина: в черновике есть картинка главного блока, которой не было среди картинок готовых тем этого сеанса.</summary>
    private bool HasOwnBanner()
    {
        var node = Get("hero.image");
        var id = node switch { JsonValue v => v.ToString(), JsonObject o => o["id"]?.ToString() ?? "", _ => "" };
        if (string.IsNullOrWhiteSpace(id))
            return false;
        return !UploadedThemeImages.Values.Any(u => u.Values.Contains(id));
    }

    /// <summary>Применить тему: картинки темы — на сервер (showcase/media/), весь набор настроек — в черновик одним запросом.
    /// Свой баннер магазина по умолчанию остаётся (вопрос «Оставить ваш баннер?»); голосовая команда баннер не меняет.</summary>
    private async Task<bool> ApplyThemeAsync(SiteThemeInfo theme, bool confirm = true)
    {
        if (_locked)
            return false;
        var ok = !confirm || await ConfirmAsync(T($"Применить тему «{theme.Name}»? Поменяются цвета, шрифт, вид блоков и картинки. Название, телефон и товары останутся. "
                                      + "Покупатели увидят после «Опубликовать»; до этого можно всё подправить или нажать «Отменить изменения».",
            $"«{theme.Name}» темасын колдоносузбу? Түстөр, шрифт, блоктордун көрүнүшү жана сүрөттөр өзгөрөт. Аты, телефону жана товарлар калат. "
            + "Кардарлар «Жарыялоодон» кийин көрүшөт; ага чейин баарын оңдосо же «Өзгөртүүлөрдү жокко чыгаруу» басса болот.",
            $"Apply the “{theme.Name}” theme? Colours, font, block styles and pictures will change. The name, phone and products stay. "
            + "Customers see it after “Publish”; until then you can adjust anything or press “Discard changes”.",
            $"«{theme.Name}» teması uygulansın mı? Renkler, yazı tipi, blok görünümleri ve resimler değişir. Ad, telefon ve ürünler kalır. "
            + "Müşteriler «Yayınla»dan sonra görür; o zamana kadar düzenleyebilir ya da «Değişiklikleri geri al»a basabilirsiniz.",
            $"«{theme.Name}» mavzusi qo'llansinmi? Ranglar, shrift, bloklar ko'rinishi va rasmlar o'zgaradi. Nomi, telefoni va mahsulotlar qoladi. "
            + "Xaridorlar «E'lon qilish»dan keyin ko'radi; ungacha hammasini tuzatish yoki «O'zgarishlarni bekor qilish»ni bosish mumkin."),
            // 2026-10-22 (модалки, пакет 4): глагол «Применить тему» вместо «Да».
            T("Применить тему", "Теманы колдонуу", "Apply theme", "Temayı uygula", "Mavzuni qo'llash"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
        if (!ok)
            return false;
        // 2026-10-27 (ТЗ сайта, С-2): свой баннер не затираем без спроса. Стоит свой — спросить, по умолчанию «Оставить» (фокус на нём);
        // голосовая команда (confirm == false) баннер не трогает.
        var keepBanner = false;
        if (theme.Images.Any(i => i.Path == "hero.image") && HasOwnBanner())
            keepBanner = !confirm || !await ConfirmAsync(
                T("В главном блоке сайта стоит ваш баннер. Оставить его? Тема поменяет цвета и вид, а картинка останется вашей.",
                    "Сайттын башкы блогунда сиздин баннериңиз турат. Калтырасызбы? Тема түстөрдү жана көрүнүштү өзгөртөт, сүрөт өзүңүздүкү бойдон калат.",
                    "Your own banner is set in the site's main block. Keep it? The theme changes colours and look, the picture stays yours.",
                    "Sitenin ana bloğunda kendi bannerınız var. Kalsın mı? Tema renkleri ve görünümü değiştirir, resim sizin olarak kalır.",
                    "Saytning asosiy blokida o'zingizning bannerlingiz turibdi. Qoldiraymi? Mavzu ranglar va ko'rinishni o'zgartiradi, rasm o'zingizniki qoladi."),
                T("Заменить баннер", "Баннерди алмаштыруу", "Replace banner", "Banner'ı değiştir", "Bannerni almashtirish"),
                T("Оставить мой баннер", "Баннеримди калтыруу", "Keep my banner", "Bannerımı koru", "Bannerimni qoldirish"), danger: true).ConfigureAwait(true);
        _saveTimer.Stop();
        await SaveDraftAsync().ConfigureAwait(true);
        ShowBanner(T("Применяю тему…", "Тема колдонулууда…", "Applying the theme…", "Tema uygulanıyor…", "Mavzu qo'llanmoqda…"), warning: false);
        try
        {
            var patch = (JsonObject)theme.Patch.DeepClone();
            if (!UploadedThemeImages.TryGetValue(theme.Code, out var uploaded))
                UploadedThemeImages[theme.Code] = uploaded = new Dictionary<string, string>();
            foreach (var image in theme.Images)
            {
                var (path, file, kind) = (image.Path, image.File, image.Kind);
                if (keepBanner && path == "hero.image")
                    continue;
                // 2026-10-27, С-0: нет файла картинки — тема применяется без неё (свой баннер и заглушка остаются).
                var assetUri = theme.AssetUri(file);
                if (!AssetLoader.Exists(assetUri))
                    continue;
                if (!uploaded.TryGetValue(path, out var id))
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nurmarket-theme-" + file);
                    await using (var asset = AssetLoader.Open(assetUri))
                    await using (var output = System.IO.File.Create(temp))
                        await asset.CopyToAsync(output).ConfigureAwait(true);
                    var data = await _api.UploadFileAsync("api/main/showcase/media/", "file", temp, new Dictionary<string, string> { ["kind"] = kind }).ConfigureAwait(true);
                    id = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var idv) ? idv.ToString() : "";
                    if (id.Length == 0)
                        throw new InvalidOperationException(T("сервер не вернул номер картинки", "сервер сүрөттүн номерин кайтарган жок", "the server returned no picture id", "sunucu resim numarası döndürmedi", "server rasm raqamini qaytarmadi"));
                    uploaded[path] = id;
                    try
                    {
                        System.IO.File.Delete(temp);
                    }
                    catch
                    {
                        // временный файл удалит Windows
                    }
                }
                SetIn(patch, path, JsonValue.Create(id));
            }
            var result = await _api.RequestAsync(new HttpMethod("PATCH"), DesignPath + "draft/", patch, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Редактор сайта: применена тема {theme.Code}.", "INFO");
            await LoadAsync().ConfigureAwait(true);
            // 2026-10-05, владелец: «темы не применяются» — сервер тему сохраняет, а сайт витрины NurCRM оформление пока не читает
            // (docs/ТЗ для фронтенда NurCRM - витрина должна показывать оформление из редактора). Говорим об этом прямо.
            // 2026-10-07: market.nurcrm.kg оформление уже показывает (предпросмотр справа — тоже).
            ShowBanner(T($"Тема «{theme.Name}» сохранена в черновике — справа предпросмотр. Нравится — «Опубликовать»: покупатели увидят на market.nurcrm.kg.",
                $"«{theme.Name}» темасы долбоордо сакталды — оң жакта алдын ала көрүү. Жакса — «Жарыялоо»: кардарлар market.nurcrm.kg дарегинде көрүшөт.",
                $"The “{theme.Name}” theme is saved in the draft — preview on the right. Like it — “Publish”: customers will see it on market.nurcrm.kg.",
                $"«{theme.Name}» teması taslağa kaydedildi — sağda önizleme. Beğendiyseniz — «Yayınla»: müşteriler market.nurcrm.kg'de görür.",
                $"«{theme.Name}» mavzusi qoralamada saqlandi — o'ngda oldindan ko'rish. Yoqsa — «E'lon qilish»: xaridorlar market.nurcrm.kg da ko'radi."), warning: false);
            ShowWarningsAppend(result);
            return true;
        }
        catch (Exception ex)
        {
            ShowBanner(T("Тема не применилась: ", "Тема колдонулган жок: ", "The theme wasn't applied: ", "Tema uygulanmadı: ", "Mavzu qo'llanmadı: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
            PosLogger.Log($"Редактор сайта: тема {theme.Code} не применилась ({ex.Message}).", "WARNING");
            return false;
        }
    }
}
