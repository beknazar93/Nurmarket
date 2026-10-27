using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-27, ТЗ «Сайт — темы для магазина одежды», пакет С-3: готовые темы сайта (SiteThemeCatalog) в Маркетплейсе — элементы
// «sitetheme:<код>», вид «Тема сайта», группа «Темы», метка «Сайт». Бесплатно, как и прежние темы (цены нет, «Куплено» не нужно).
// Видны только при подключённой «Онлайн витрине» (ShowcaseApiService.IsShowcaseConnected). В сфере «Одежда» темы одежды идут первыми
// и отмечены «Новое»/«Рекомендуем»; в других сферах они остаются в списке, но в конце и без отметок — тема одежды не навязывается.
// Главная кнопка — «Открыть в редакторе сайта» (только программа владельца): раздел открывается, тема ждёт подтверждения там.
// В кассе кнопка недоступна, рядом пояснение. Сети здесь нет: превью — картинка из ресурсов или мини-сайт SiteThemePreview.
internal static class MarketplaceSiteThemes
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly string AppVersion = typeof(MarketplaceItem).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>Добавить темы сайта в конец списка (или перед первой темой кассы — в сфере «Одежда»). Вызывается из GetItems() до MarketplaceContent.Enrich.</summary>
    internal static void Append(List<MarketplaceItem> items, Action<string> run)
    {
        try
        {
            if (!ShowcaseApiService.IsShowcaseConnected)
                return;
            var sphere = UserPreferences.Instance.MarketSphere;
            var clothing = sphere == MarketSpheres.Clothing;
            var made = SiteThemeCatalog.Ordered(sphere).Select(t => Item(t, run, clothing && SiteThemeCatalog.IsClothing(t))).ToList();
            var at = clothing ? items.FindIndex(i => i.Kind == MarketplaceKind.Theme) : -1;
            if (at < 0)
                items.AddRange(made);
            else
                items.InsertRange(at, made);
        }
        catch (Exception ex)
        {
            // Темы сайта — дополнение: сбой не должен ронять весь каталог.
            PosLogger.Log($"Маркетплейс: темы сайта не добавлены: {ex.Message}", "WARNING");
        }
    }

    private static MarketplaceItem Item(SiteThemeInfo theme, Action<string> run, bool promoted)
    {
        var colors = theme.Patch["theme"]?["colors"] as JsonObject;
        var accent = colors?["accent"]?.ToString() ?? "#FFB208";
        var description = theme.Description;
        var cut = description.IndexOfAny(['.', '—', ':']);
        var shortText = cut > 8 ? description[..cut] : description;
        var isOwner = AppMode.IsOwner;
        return new MarketplaceItem
        {
            Id = "sitetheme:" + theme.Code,
            Kind = MarketplaceKind.Theme,
            Title = theme.Name,
            ShortDescription = shortText.Length > 60 ? shortText[..57].TrimEnd() + "…" : shortText.Trim(),
            LongDescription = description,
            IconGlyph = "🌐",
            IconColor = Color.TryParse(accent, out _) ? accent : "#FFB208",
            Status = MarketplaceStatus.Available,
            Version = AppVersion,
            Tags = Tags(theme),
            Features =
            [
                T("Весь вид сайта одним нажатием: цвета, шрифт, шапка, главный блок, карточки товаров, подвал", "Сайттын бүт көрүнүшү бир басуу менен: түстөр, шрифт, баш бөлүк, башкы блок, товар карточкалары, төмөнкү бөлүк",
                    "The whole website look in one click: colours, font, header, main block, product cards, footer", "Sitenin tüm görünümü tek tıkla: renkler, yazı tipi, üst bölüm, ana blok, ürün kartları, alt bilgi",
                    "Saytning butun ko'rinishi bir bosishda: ranglar, shrift, sarlavha, asosiy blok, mahsulot kartochkalari, pastki qism"),
                T("Название, телефон, тексты и товары магазина не меняются", "Дүкөндүн аты, телефону, тексттери жана товарлары өзгөрбөйт",
                    "The shop's name, phone, texts and products stay", "Mağaza adı, telefon, metinler ve ürünler değişmez", "Do'kon nomi, telefoni, matnlari va mahsulotlari o'zgarmaydi"),
                T("Сначала — в черновик: покупатели увидят после «Опубликовать»", "Адегенде — долбоорго: кардарлар «Жарыялоодон» кийин көрүшөт",
                    "First into the draft: customers see it after “Publish”", "Önce taslağa: müşteriler «Yayınla»dan sonra görür", "Avval qoralamaga: xaridorlar «E'lon qilish»dan keyin ko'radi"),
            ],
            Requirements = [T("Подключённая услуга «Онлайн витрина»", "Туташкан «Онлайн витрина» кызматы", "The “Online showcase” service connected", "Bağlı «Online vitrin» hizmeti", "Ulangan «Onlayn vitrina» xizmati")],
            Note = isOwner
                ? T("Тема сначала попадёт в черновик сайта — вы увидите предпросмотр и решите, публиковать ли.", "Тема адегенде сайттын долбооруна түшөт — алдын ала көрүп, жарыялоону өзүңүз чечесиз.",
                    "The theme goes into the site draft first — you see a preview and decide whether to publish.", "Tema önce site taslağına girer — önizlemeyi görür ve yayınlamaya siz karar verirsiniz.", "Mavzu avval sayt qoralamasiga tushadi — oldindan ko'rib, e'lon qilishni o'zingiz hal qilasiz.")
                : T("Темы сайта применяются в программе владельца: откройте Маркетплейс там.", "Сайттын темалары ээсинин программасында колдонулат: Маркетплейсти ошол жерден ачыңыз.",
                    "Website themes are applied in the owner program: open the Marketplace there.", "Site temaları sahip programında uygulanır: Marketplace'i orada açın.", "Sayt mavzulari egasi dasturida qo'llanadi: Marketpleysni o'sha yerda oching."),
            IsNew = promoted,
            IsFeatured = promoted,
            Primary = new MarketplaceAction(T("Открыть в редакторе сайта", "Сайт редакторунда ачуу", "Open in the website editor", "Site düzenleyicide aç", "Sayt muharririda ochish"),
                () => run("sitetheme:" + theme.Code), isOwner),
            IconVisual = size => Icon(theme, size),
            HeroVisual = () => Hero(theme),
        };
    }

    private static IReadOnlyList<string> Tags(SiteThemeInfo theme)
    {
        var tags = new List<string> { T("Оформление", "Жасалгалоо", "Appearance", "Görünüm", "Ko'rinish"), T("Сайт", "Сайт", "Website", "Web sitesi", "Sayt") };
        if (SiteThemeCatalog.IsClothing(theme))
            tags.Add(T("Одежда", "Кийим", "Clothing", "Giyim", "Kiyim"));
        return tags;
    }

    /// <summary>Значок плитки: готовое превью темы (_thumb.png из ресурсов); нет файла — мини-сайт кодом.</summary>
    private static Control Icon(SiteThemeInfo theme, double size)
    {
        var s = Math.Max(16, size - 8);
        try
        {
            if (AssetLoader.Exists(theme.ThumbUri))
            {
                using var stream = AssetLoader.Open(theme.ThumbUri);
                return new Image { Source = Bitmap.DecodeToWidth(stream, (int)Math.Ceiling(s * 2.2)), Width = size, Height = size, Stretch = Stretch.UniformToFill };
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: превью темы сайта «{theme.Code}»: {ex.Message}", "WARNING");
        }
        return SiteThemePreview.Thumb(theme, size, size);
    }

    /// <summary>Крупный предпросмотр на странице «подробно»: верх мини-сайта темы (шапка, главный блок, категории, карточки) и подпись.</summary>
    private static Control Hero(SiteThemeInfo theme)
    {
        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(1), Child = SiteThemePreview.Thumb(theme, 760, 475) };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BrushBorderStrong"));
        var caption = new TextBlock
        {
            Text = T("Так выглядит сайт в этой теме — фото, цены и тексты условные", "Сайт ушул темада ушундай көрүнөт — сүрөттөр, баалар жана тексттер шарттуу",
                "How the website looks in this theme — photos, prices and texts are samples", "Site bu temada böyle görünür — fotoğraf, fiyat ve metinler örnektir", "Sayt shu mavzuda shunday ko'rinadi — rasm, narx va matnlar namunaviy"),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));
        return new StackPanel { Spacing = 10, MaxWidth = 760, Children = { frame, caption } };
    }
}

internal static partial class MarketplaceContent
{
    /// <summary>Страница «подробно» темы сайта: значок, шаги, «для кого», выгоды, условия цены.</summary>
    private static void SiteTheme(MarketplaceItem i)
    {
        i.IconKey = "hanger";
        var theme = SiteThemeCatalog.Find(i.Id["sitetheme:".Length..]);
        var font = theme?.Patch["theme"]?["font"]?["family"]?.ToString() ?? "";
        i.Tagline = i.ShortDescription;
        i.KeyFacts = [T("Сайт", "Сайт", "Website", "Web sitesi", "Sayt"), font, T("Фото 3:4", "Сүрөт 3:4", "Photo 3:4", "Fotoğraf 3:4", "Rasm 3:4")];
        i.HowTo =
        [
            T("Нажмите «Открыть в редакторе сайта» — откроется раздел «Редактор сайта».", "«Сайт редакторунда ачуу» баскычын басыңыз — «Сайттын редактору» бөлүмү ачылат.",
                "Press “Open in the website editor” — the “Website editor” section opens.", "«Site düzenleyicide aç»a basın — «Web sitesi düzenleyici» bölümü açılır.", "«Sayt muharririda ochish» tugmasini bosing — «Sayt muharriri» bo'limi ochiladi."),
            T("Подтвердите тему — она попадёт в черновик, справа появится предпросмотр.", "Теманы ырастаңыз — ал долбоорго түшөт, оң жакта алдын ала көрүү чыгат.",
                "Confirm the theme — it goes into the draft and a preview appears on the right.", "Temayı onaylayın — taslağa girer, sağda önizleme görünür.", "Mavzuni tasdiqlang — u qoralamaga tushadi, o'ngda oldindan ko'rish paydo bo'ladi."),
            T("Нравится — нажмите «Опубликовать»: покупатели увидят на сайте.", "Жакса — «Жарыялоо» баскычын басыңыз: кардарлар сайттан көрүшөт.",
                "Like it — press “Publish”: customers will see it on the website.", "Beğendiyseniz «Yayınla»ya basın: müşteriler sitede görür.", "Yoqsa — «E'lon qilish»ni bosing: xaridorlar saytda ko'radi."),
        ];
        var description = theme?.Description ?? i.LongDescription;
        var forWhom = description.LastIndexOf(". ", StringComparison.Ordinal) is var dot and > 0 ? description[(dot + 2)..].TrimEnd('.') : description.TrimEnd('.');
        i.Audience = [forWhom];
        i.Benefits =
        [
            B("palette-outline", T("Цвета и шрифт", "Түстөр жана шрифт", "Colours and font", "Renkler ve yazı tipi", "Ranglar va shrift"),
                T("Подобраны и проверены на читаемость (AA)", "Окулушу боюнча тандалып, текшерилген (AA)", "Chosen and checked for readability (AA)", "Okunabilirlik için seçildi ve denendi (AA)", "O'qilishi bo'yicha tanlangan va tekshirilgan (AA)")),
            B("image-outline", T("Фото 3:4", "Сүрөт 3:4", "Photos 3:4", "Fotoğraf 3:4", "Rasm 3:4"),
                T("Карточки под одежду: вертикальное фото", "Кийимге ылайыктуу карточкалар: тик сүрөт", "Cards made for clothes: a vertical photo", "Giyime uygun kartlar: dikey fotoğraf", "Kiyimga mos kartochkalar: tik rasm")),
            B("cellphone-message", T("Телефон и компьютер", "Телефон жана компьютер", "Phone and desktop", "Telefon ve bilgisayar", "Telefon va kompyuter"),
                T("Свои колонки для телефона и компьютера", "Телефон жана компьютер үчүн өзүнчө мамычалар", "Separate column counts for phone and desktop", "Telefon ve bilgisayar için ayrı sütunlar", "Telefon va kompyuter uchun alohida ustunlar")),
        ];
        i.PriceTerms = T("Бесплатно. Нужна подключённая «Онлайн витрина».", "Акысыз. Туташкан «Онлайн витрина» керек.", "Free. Requires the “Online showcase” service.", "Ücretsiz. Bağlı «Online vitrin» gerekir.", "Bepul. Ulangan «Onlayn vitrina» kerak.");
    }
}
