using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-06 («темы с сайта», ТЗ Умару — docs\ТЗ Умару — темы и виды кассы из Маркетплейса (06.11.2026).md): платные и бесплатные темы и виды кассы с сервера в Маркетплейсе.
// Элементы «servertheme:<код>» (код = theme-<slug>), вид «Тема»/«Вид кассы», состояния: «Купить на сайте» → «Установить» → «Установлено» (+«Обновить», «Удалить»).
// Всё — только при признаке сервера marketplace_themes (MarketplaceThemesClient): без него элементов нет, вкладка «Темы» показывает свои темы, как раньше. Установка — данные
// в «свои темы» (CustomThemeStore); исполняемого кода нет. Эта версия программы ставит вид «theme»; view/labels/scale-profile показываются как «Скоро».

/// <summary>Куда установщик кладёт тему с сайта: «Редактор своих тем» (CustomThemeStore).</summary>
internal sealed class CustomThemeSink : IThemeSink
{
    public string Save(string? existingId, ThemePackageData data)
    {
        var baseId = AccentThemeService.AvailableThemes.Any(t => string.Equals(t.Id, data.BaseThemeId, StringComparison.OrdinalIgnoreCase)) ? data.BaseThemeId : "classic";
        var existing = existingId is null ? null : CustomThemeStore.Find(existingId);
        var def = existing is not null ? existing.Clone() : CustomThemeStore.Create(baseId, data.Name);
        if (existing is not null)
            def.Name = data.Name; // обновление: id остаётся, выбранная тема остаётся выбранной
        def.BaseThemeId = baseId;
        def.Light = Colors(data.Light);
        def.Dark = Colors(data.Dark);
        def.CornerRadius = data.CornerRadius;
        def.FontSize = data.FontSize;
        CustomThemeStore.Save(def);
        // Save мог сделать имя уникальным — id прежний; если тема в этот момент выбрана, перекрашиваем программу под новые цвета.
        if (string.Equals(AccentThemeService.Normalize(UserPreferences.Instance.AccentTheme), def.Id, StringComparison.OrdinalIgnoreCase))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AccentThemeService.Apply(def.Id, UserPreferences.Instance.DarkTheme));
        return def.Id;
    }

    public bool Exists(string id) => CustomThemeStore.Find(id) is not null;

    public void Delete(string id) => CustomThemeStore.Delete(id);

    private static CustomThemeColors Colors(Dictionary<string, string> values)
    {
        string? V(string key) => values.TryGetValue(key, out var v) ? v : null;
        return new CustomThemeColors
        {
            Accent = V("accent"), AccentText = V("accentText"), Background = V("background"), Panel = V("panel"), Text = V("text"),
            TextSoft = V("textSoft"), Success = V("success"), Warning = V("warning"), Danger = V("danger"),
        };
    }
}

internal static class MarketplaceServerThemes
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public const string IdPrefix = "servertheme:";

    /// <summary>Общий установщик программы: каталог — MarketplaceThemesClient.Shared, записи — ServerThemeRegistry.Shared, «свои темы» — CustomThemeStore.</summary>
    public static readonly MarketplaceThemeInstaller Installer = new(MarketplaceThemesClient.Shared, ServerThemeRegistry.Shared, new CustomThemeSink());

    /// <summary>Тема в «своих темах» поставлена с сайта — плитку «Моя тема» для неё не рисуем (есть карточка «servertheme:…»).</summary>
    public static bool IsServerCustomTheme(string customId) => ServerThemeRegistry.Shared.FindByCustomId(customId) is not null;

    private static readonly string AppVersion = typeof(MarketplaceItem).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>Добавить темы и виды с сервера (перед первой темой кассы — чтобы купленное было на виду). Нет признака/каталога — ничего.</summary>
    internal static void Append(List<MarketplaceItem> items, Action<string> run)
    {
        try
        {
            var entries = MarketplaceThemesClient.Shared.Entries;
            if (entries.Count == 0)
                return;
            var made = entries.Select(e => Item(e, run)).ToList();
            var at = items.FindIndex(i => i.Kind == MarketplaceKind.Theme);
            if (at < 0)
                items.AddRange(made);
            else
                items.InsertRange(at, made);
        }
        catch (Exception ex)
        {
            // Темы с сайта — дополнение: сбой не должен ронять весь каталог.
            PosLogger.Log($"Маркетплейс: темы с сайта не добавлены: {ex.Message}", "WARNING");
        }
    }

    private static MarketplaceItem Item(MarketplaceThemeEntry e, Action<string> run)
    {
        var title = e.Name.Get() is { Length: > 0 } n ? n : e.Code;
        var description = e.Description.Get();
        var cut = description.IndexOfAny(['.', '—', ':']);
        var shortText = cut > 8 ? description[..cut] : description;
        var installable = MarketplaceThemeInstaller.CanInstall(e);
        var needsNewerApp = !MarketplaceThemesClient.MeetsMinAppVersion(e.MinAppVersion);
        var state = installable ? Installer.StateOf(e) : ThemeInstallState.NotInstalled;
        var customId = installable ? Installer.CustomIdOf(e.Code) : null;
        var current = customId is not null && string.Equals(AccentThemeService.Normalize(UserPreferences.Instance.AccentTheme), customId, StringComparison.OrdinalIgnoreCase);
        var canGet = e.Entitled || e.IsFree;
        var selectedText = T("Выбрана", "Тандалган", "Selected", "Seçili", "Tanlangan");

        MarketplaceStatus status;
        var statusText = "";
        MarketplaceAction? primary;
        var secondary = new List<MarketplaceAction>();
        var hint = "";
        var note = "";

        if (!installable)
        {
            // view / labels / scale-profile: эта версия программы их ещё не ставит.
            status = MarketplaceStatus.ComingSoon;
            primary = null;
            note = T("Этот вид пакета появится в одной из следующих версий программы — пока его нельзя установить.", "Пакеттин бул түрү программанын кийинки версияларында чыгат — азырынча орнотууга болбойт.",
                "This kind of package will come in one of the next versions — it cannot be installed yet.", "Bu paket türü programın sonraki sürümlerinden birinde gelecek — şimdilik yüklenemez.", "Paketning bu turi dasturning keyingi versiyalaridan birida chiqadi — hozircha o'rnatib bo'lmaydi.");
        }
        else if (state != ThemeInstallState.NotInstalled)
        {
            status = MarketplaceStatus.Installed;
            statusText = current ? selectedText : state == ThemeInstallState.UpdateAvailable
                ? T("Есть обновление", "Жаңыртуу бар", "Update available", "Güncelleme var", "Yangilanish bor")
                : T("Установлено", "Орнотулган", "Installed", "Yüklendi", "O'rnatilgan");
            if (state == ThemeInstallState.UpdateAvailable)
            {
                primary = new MarketplaceAction(T("Обновить", "Жаңыртуу", "Update", "Güncelle", "Yangilash"), () => run("servertheme:install:" + e.Code), !needsNewerApp);
                if (!current)
                    secondary.Add(new MarketplaceAction(T("Применить тему", "Теманы колдонуу", "Apply theme", "Temayı uygula", "Mavzuni qo'llash"), () => run("servertheme:apply:" + e.Code)));
            }
            else
            {
                primary = current
                    ? new MarketplaceAction(selectedText, () => { }, false)
                    : new MarketplaceAction(T("Применить тему", "Теманы колдонуу", "Apply theme", "Temayı uygula", "Mavzuni qo'llash"), () => run("servertheme:apply:" + e.Code));
            }
            secondary.Add(new MarketplaceAction(T("Удалить", "Өчүрүү", "Remove", "Kaldır", "O'chirish"), () => run("servertheme:remove:" + e.Code)));
        }
        else if (canGet)
        {
            status = MarketplaceStatus.Available;
            statusText = e.IsFree ? "" : T("Куплено", "Сатып алынган", "Purchased", "Satın alındı", "Sotib olingan");
            primary = new MarketplaceAction(T("Установить", "Орнотуу", "Install", "Yükle", "O'rnatish"), () => run("servertheme:install:" + e.Code), !needsNewerApp);
        }
        else
        {
            status = MarketplaceStatus.Paid;
            primary = new MarketplaceAction(T("Купить на сайте", "Сайттан сатып алуу", "Buy on the website", "Web sitesinden satın al", "Saytdan sotib olish"), () => run("servertheme:buy:" + e.Code));
            hint = T("Оплатите на сайте — тема сама появится в программе. Если не появилась — нажмите «Обновить покупки».", "Сайтта төлөңүз — тема программада өзү пайда болот. Чыкпаса — «Сатып алууларды жаңыртуу» баскычын басыңыз.",
                "Pay on the website — the theme appears in the program by itself. If it does not, press “Refresh purchases”.", "Sitede ödeyin — tema programda kendiliğinden görünür. Görünmezse «Satın alımları yenile»ye basın.", "Saytda to'lang — mavzu dasturda o'zi paydo bo'ladi. Chiqmasa — «Xaridlarni yangilash»ni bosing.");
        }
        if (needsNewerApp && installable)
            note = T($"Нужна версия программы {e.MinAppVersion} или новее — обновите программу.", $"Программанын {e.MinAppVersion} же жаңыраак версиясы керек — программаны жаңыртыңыз.",
                $"Program version {e.MinAppVersion} or newer is required — update the program.", $"Program sürümü {e.MinAppVersion} veya daha yeni olmalı — programı güncelleyin.", $"Dastur versiyasi {e.MinAppVersion} yoki yangisi kerak — dasturni yangilang.");

        var accent = Color.TryParse(e.Accent ?? "", out _) ? e.Accent! : "#FFB208";
        return new MarketplaceItem
        {
            Id = IdPrefix + e.Code,
            Kind = string.Equals(e.Kind, "view", StringComparison.OrdinalIgnoreCase) ? MarketplaceKind.Layout : MarketplaceKind.Theme,
            Title = title,
            ShortDescription = shortText.Length > 60 ? shortText[..57].TrimEnd() + "…" : shortText.Trim(),
            LongDescription = description,
            IconGlyph = "🎨",
            IconColor = accent,
            Price = e.IsFree ? null : new MarketplacePrice(e.Price, MarketplacePricePeriod.OneTime, null, e.Currency),
            Status = status,
            StatusText = statusText,
            Version = string.IsNullOrEmpty(e.Version) ? AppVersion : e.Version,
            Tags = [T("Оформление", "Жасалгалоо", "Appearance", "Görünüm", "Ko'rinish"), T("С сайта", "Сайттан", "From the website", "Web sitesinden", "Saytdan")],
            Features =
            [
                T("Тема из Маркетплейса на сайте: цвета, форма и шрифт программы", "Сайттагы Маркетплейстен тема: программанын түстөрү, формасы жана шрифти", "A theme from the website Marketplace: colours, shapes and font of the program", "Sitedeki Marketplace'ten tema: programın renkleri, şekilleri ve yazı tipi", "Saytdagi Marketpleysdan mavzu: dasturning ranglari, shakli va shrifti"),
                T("Скачивается в программу после покупки на сайте, без перезапуска", "Сайттан сатып алгандан кийин программага жүктөлөт, кайра ачуунун кереги жок", "Downloads into the program after the purchase on the website, no restart", "Sitede satın aldıktan sonra programa iner, yeniden başlatma gerekmez", "Saytda sotib olingach dasturga yuklanadi, qayta ishga tushirish shart emas"),
                T("Это только данные (цвета, шрифт, углы) — никакой код не запускается", "Бул маалымат гана (түстөр, шрифт, бурчтар) — эч кандай код иштебейт", "Data only (colours, font, corners) — no code is run", "Yalnızca veri (renk, yazı tipi, köşe) — hiçbir kod çalışmaz", "Faqat ma'lumot (rang, shrift, burchak) — hech qanday kod ishlamaydi"),
            ],
            Note = note,
            ActionHint = hint,
            Primary = primary,
            Secondary = secondary,
            IconVisual = size => ServerThemePreview.Icon(e, size, accent),
            HeroVisual = () => ServerThemePreview.Hero(e, accent),
        };
    }
}

/// <summary>Превью темы с сайта: картинка по preview_url (https, ≤ 1 МБ, без токена; в памяти, не на диске) или цветная плитка. Нет связи — плитка.</summary>
internal static class ServerThemePreview
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Requested = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Картинка загрузилась — витрина перерисовывается.</summary>
    public static event Action? Loaded;

    public static Control Icon(MarketplaceThemeEntry e, double size, string accent)
    {
        if (TryGet(e, out var bmp) && bmp is not null)
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill } };
        return Placeholder(size, size, accent);
    }

    public static Control Hero(MarketplaceThemeEntry e, string accent)
    {
        if (TryGet(e, out var bmp) && bmp is not null)
            return new Border { Width = 640, Height = 400, CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill } };
        return Placeholder(640, 400, accent);
    }

    private static Control Placeholder(double w, double h, string accent) => new Border
    {
        Width = w, Height = h, CornerRadius = new CornerRadius(w > 200 ? 12 : 10),
        Background = new SolidColorBrush(Color.TryParse(accent, out var c) ? c : Color.Parse("#FFB208")),
        Child = new TextBlock { Text = "🎨", FontSize = Math.Max(16, Math.Min(w, h) * 0.4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static bool TryGet(MarketplaceThemeEntry e, out Bitmap? bmp)
    {
        bmp = null;
        var url = e.PreviewUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (Cache.TryGetValue(url, out bmp))
            return true;
        if (Cache.Count < 100 && Requested.TryAdd(url, 0))
            _ = Task.Run(() => LoadAsync(url, uri));
        return false;
    }

    private static async Task LoadAsync(string key, Uri uri)
    {
        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength is > MaxBytes)
                return;
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxBytes)
                return;
            using var ms = new System.IO.MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(ms, 640);
            Cache[key] = bitmap;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Loaded?.Invoke());
        }
        catch (Exception ex)
        {
            // Нет связи, не картинка, слишком большая — остаётся цветная плитка; повторять в этом сеансе не будем.
            PosLogger.Log($"Маркетплейс: превью темы не загрузилось ({ex.GetType().Name}).", "INFO");
        }
    }
}
