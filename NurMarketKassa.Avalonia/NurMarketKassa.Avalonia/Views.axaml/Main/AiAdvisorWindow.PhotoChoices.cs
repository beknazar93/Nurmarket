using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-06 (В13, Л4-17): часть окна Нур Советника — варианты фото товаров на выбор, загрузка фото, вспомогательные методы (строки 4877–5189 прежнего файла), без изменения логики.</summary>
public sealed partial class AiAdvisorWindow
{
    private static bool IsPhotoRequest(string text)
        => ProductActionPlan.IsExplicitPhotoWebSearchRequest(text);

    /// <summary>2026-10-05: «загрузи фото к товарам, у которых нет фото». Товары без фото → поиск по штрихкоду в
    /// открытых базах (ProductPhotoFinder) → найденные показываются с «Поставить» / «Поставить все»; остальные —
    /// с «📷 Добавить фото» (файл или камера, как на складе). На сервер фото уходит только по нажатию владельца.</summary>
    private async Task RunPhotoAssistantAsync(string question)
    {
        // 2026-10-30: «Искать и ставить фото товаров» выключено — названия товаров для поиска в интернет не уходят.
        if (!Cap("inv.photo"))
        {
            SayDisabled("inv.photo");
            return;
        }
        // 2026-10-05, владелец: «чтобы ИИ смог по команде найти фото по названию или штрихкоду товара в интернете, если в
        // базе нет». Названы товары («найди фото для кока колы», штрихкод) — ищем для них; иначе — для всех без фото.
        var named = ProductPhotoFinder.MatchRequest(question);
        // 2026-10-06, владелец: «при поиске в интернете отображай найденные фото товаров на выбор в чате». Названы 1–3 товара —
        // несколько вариантов фото каждого, ставит выбранное.
        if (named.Count is > 0 and <= 3)
        {
            await ShowPhotoChoicesAsync(named).ConfigureAwait(true);
            return;
        }
        var withoutPhoto = named.Count > 0 ? named : ProductPhotoFinder.WithoutPhoto();
        if (withoutPhoto.Count == 0)
        {
            AddBubble(T("У всех товаров склада уже есть фото.", "Кампадагы бардык товарлардын сүрөтү бар.", "Every product in the warehouse already has a photo.",
                "Depodaki tüm ürünlerin fotoğrafı var.", "Ombordagi barcha mahsulotlarning rasmi bor."), fromOwner: false);
            return;
        }
        // 2026-11-03, владелец: «найдено фото только для 10 товаров из 15 тысяч — по этапам добавляй по 20 или по 50 по очереди». Поиск идёт пачками
        // (AiAdvisorWindow.PhotoBatch.cs): интернет — для всех товаров пачки, лимит запросов в минуту, память «что уже проверено», «Поставить все» — только
        // для уверенных совпадений; фото уходит на сервер по-прежнему только по нажатию владельца.
        await StartPhotoBatchesAsync(named, withoutPhoto).ConfigureAwait(true);
    }

    /// <summary>Варианты фото товаров (до 8 на товар) — плиткой в чате, «Поставить это фото» под каждым.</summary>
    private async Task ShowPhotoChoicesAsync(IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> products)
    {
        if (!Cap("inv.photo"))
        {
            SayDisabled("inv.photo");
            return;
        }
        var status = AddBubble(T($"Ищу фото на выбор: {string.Join(", ", products.Select(p => p.Title))} — в открытых базах и на сайтах магазинов…",
            $"Тандоо үчүн сүрөт издеп жатам: {string.Join(", ", products.Select(p => p.Title))} — ачык базалардан жана дүкөн сайттарынан…",
            $"Looking for photos to choose from: {string.Join(", ", products.Select(p => p.Title))} — in open databases and shop websites…",
            $"Seçmek için fotoğraf arıyorum: {string.Join(", ", products.Select(p => p.Title))} — açık veritabanlarında ve mağaza sitelerinde…",
            $"Tanlash uchun rasm qidiryapman: {string.Join(", ", products.Select(p => p.Title))} — ochiq bazalar va do'kon saytlarida…"), fromOwner: false);
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var total = 0;
        _photoChoices.Clear();
        // 2026-10-06, владелец: «очень долго ищет фото, добавь индикатор загрузки в чат». Пока ищем — крутится значок и идут
        // секунды; товары ищутся одновременно, а не по очереди.
        var searchText = status.Text;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frame = 0;
        string[] frames = { "◐", "◓", "◑", "◒" };
        var spinner = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        spinner.Tick += (_, _) => status.Text = $"{frames[frame++ % frames.Length]} {searchText} {watch.Elapsed.TotalSeconds:0} "
                                                + T("с", "сек", "s", "sn", "s");
        spinner.Start();
        var searches = products.Select(p => Task.Run(() => ProductPhotoFinder.FindChoicesAsync(p, 8, ct), ct)).ToList();
        foreach (var (product, search) in products.Zip(searches))
        {
            List<ProductPhotoFinder.Candidate> choices;
            try
            {
                choices = await search.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                spinner.Stop();
                return;
            }
            catch
            {
                // 2026-11-06 (Л4-26): любая другая ошибка поиска — «крутилка» не должна остаться тикать и держать окно.
                spinner.Stop();
                throw;
            }
            total += choices.Count;
            var header = new TextBlock
            {
                Text = choices.Count > 0
                    ? T($"«{product.Title}» — выберите фото ({choices.Count}):", $"«{product.Title}» — сүрөт тандаңыз ({choices.Count}):", $"“{product.Title}” — choose a photo ({choices.Count}):",
                        $"«{product.Title}» — fotoğraf seçin ({choices.Count}):", $"«{product.Title}» — rasmni tanlang ({choices.Count}):")
                    : T($"«{product.Title}» — фото в интернете не нашлось. Сфотографируйте товар: «📷 Добавить фото» на складе.",
                        $"«{product.Title}» — интернеттен сүрөт табылган жок. Товарды сүрөткө тартыңыз: кампада «📷 Сүрөт кошуу».",
                        $"“{product.Title}” — no photo found online. Take a photo: “📷 Add photo” in the warehouse.",
                        $"«{product.Title}» — internette fotoğraf bulunamadı. Fotoğrafını çekin: depoda «📷 Fotoğraf ekle».",
                        $"«{product.Title}» — internetda rasm topilmadi. Suratga oling: omborda «📷 Rasm qo'shish»."),
                FontWeight = FontWeight.SemiBold, FontSize = 13.5, Margin = new Thickness(4, 4, 0, 4), TextWrapping = TextWrapping.Wrap,
            };
            Use(header, TextBlock.ForegroundProperty, "BrushText");
            _messages.Children.Add(header);
            if (choices.Count == 0)
                continue;
            var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            var buttons = new List<Button>();
            foreach (var candidate in choices)
            {
                var image = new Image { Width = 132, Height = 132, Stretch = Stretch.UniformToFill };
                Border? choiceCard = null;
                var source = new TextBlock { Text = candidate.Source, FontSize = 12, MaxWidth = 132, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(source, TextBlock.ForegroundProperty, "BrushTextSoft");
                var set = UiKit.Ghost(this, T("Поставить это фото", "Ушул сүрөттү коюу", "Use this photo", "Bu fotoğrafı kullan", "Shu rasmni qo'yish"));
                set.Height = 32;
                set.FontSize = 12;
                set.Padding = new Thickness(8, 0);
                // 2026-10-07: товара нет на складе (поиск по словам фразы) — фото только посмотреть, ставить некуда.
                set.IsVisible = candidate.Product.Id.Length > 0;
                buttons.Add(set);
                set.Click += async (_, _) =>
                {
                    foreach (var other in buttons.Where(b => b != set))
                        other.IsEnabled = false;
                    await ApplyPhotoAsync(set, candidate).ConfigureAwait(true);
                };
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
                    Child = new StackPanel { Spacing = 4, Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, source, set } },
                };
                Use(card, Border.BackgroundProperty, "BrushPanel");
                Use(card, Border.BorderBrushProperty, "BrushBorder");
                strip.Children.Add(card);
                choiceCard = card;
                if (candidate.Product.Id.Length > 0)
                    _photoChoices.Add((candidate, set, card, source));
                // Картинка в формате, который окно не показывает (avif и т. п.), — вариант убираем, а не оставляем пустую рамку.
                _ = LoadPreviewAsync(image, candidate.ImageUrl, ct).ContinueWith(t =>
                {
                    if (!t.Result && choiceCard is not null)
                    {
                        choiceCard.IsVisible = false;
                        RenumberPhotoChoices();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            _messages.Children.Add(strip);
            RenumberPhotoChoices();
            ScrollToEnd();
        }
        spinner.Stop();
        PosLogger.Log($"ИИ-советник: фото на выбор — {total} вар. за {watch.ElapsedMilliseconds} мс.", "INFO");
        // 2026-10-06: при 0 вариантов не писать «Нашёл вариантов фото: 0. Нажмите «Поставить…»» — нечего нажимать.
        if (total == 0)
        {
            status.Text = T("Фото в интернете не нашлось.", "Интернеттен сүрөт табылган жок.", "No photos found online.",
                "Internette fotoğraf bulunamadı.", "Internetda rasm topilmadi.");
            ScrollToEnd();
            return;
        }
        status.Text = T($"Нашёл вариантов фото: {total}. Нажмите «Поставить это фото» под подходящим — фото уйдёт в карточку товара на сервере.",
            $"Сүрөт варианттары: {total}. Ылайыктуусунун астындагы «Ушул сүрөттү коюу» басыңыз — сүрөт сервердеги товар карточкасына кетет.",
            $"Photo options found: {total}. Press “Use this photo” under the right one — it goes to the product card on the server.",
            $"Bulunan fotoğraf seçenekleri: {total}. Uygun olanın altındaki «Bu fotoğrafı kullan»a basın — fotoğraf sunucudaki ürün kartına gider.",
            $"Topilgan rasm variantlari: {total}. Mosining ostidagi «Shu rasmni qo'yish»ni bosing — rasm serverdagi mahsulot kartasiga ketadi.");
        ScrollToEnd();
    }

    private static async Task<bool> LoadPreviewAsync(Image image, string url, CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await ProductPhotoFinder.DownloadAsync(url, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false; // 2026-11-06 (Л4-26): поиск остановлен или окно закрыто — картинка больше не нужна
        }
        if (bytes is not { Length: > 0 })
            return false;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            image.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 224);
            return true;
        }
        catch
        {
            // картинка не читается — остаётся пустая рамка
            return false;
        }
    }

    private async Task ApplyPhotoAsync(Button button, ProductPhotoFinder.Candidate candidate)
    {
        button.IsEnabled = false;
        button.Content = T("Загружаю…", "Жүктөп жатам…", "Uploading…", "Yükleniyor…", "Yuklanmoqda…");
        bool ok;
        try
        {
            ok = await ProductPhotoFinder.ApplyAsync(candidate, CancellationToken.None).ConfigureAwait(true);
            if (ok)
                _lastPhotoProduct = candidate.Product;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото «{candidate.Product.Title}» не загружено ({ex.Message}).", "WARNING");
            ok = false;
        }
        // 2026-11-03: описание из интернета ставится вместе с фото, только если на сервере оно пустое (существующее не затираем).
        var descriptionSet = false;
        if (ok && !string.IsNullOrWhiteSpace(candidate.Description))
        {
            try
            {
                descriptionSet = await ProductPhotoFinder.ApplyDescriptionAsync(candidate, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-советник: описание «{candidate.Product.Title}» не записано ({ex.Message}).", "WARNING");
            }
        }
        button.Content = ok
            ? descriptionSet
                ? T("✓ Фото и описание", "✓ Сүрөт жана сүрөттөмө", "✓ Photo and description", "✓ Fotoğraf ve açıklama", "✓ Rasm va tavsif")
                : T("✓ Поставлено", "✓ Коюлду", "✓ Set", "✓ Ayarlandı", "✓ O'rnatildi")
            : T("Не получилось", "Болбой калды", "Failed", "Olmadı", "Bo'lmadi");
        button.IsEnabled = !ok;
    }

    private async Task PickAndUploadAsync(Button button, NurMarketKassa.Models.Pos.CatalogProductTileVm product)
    {
        var picker = App.AppHost?.Services.GetService<NurMarketKassa.Ui.Shared.ISettingsImagePicker>();
        if (picker is null)
            return;
        var file = await picker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(file))
            return;
        button.IsEnabled = false;
        bool ok;
        try
        {
            ok = await ProductPhotoFinder.UploadAsync(product, file, "снято владельцем", CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: фото «{product.Title}» не загружено ({ex.Message}).", "WARNING");
            ok = false;
        }
        button.Content = (ok ? "✓ " : "✗ ") + product.Title;
        button.IsEnabled = !ok;
    }

    /// <summary>2026-10-05, владелец: «добавь ИИ отправлять фото товара, если есть». Под ответом — фото товаров,
    /// которые советник назвал (до 4), из того же кэша фото, что у плиток каталога.</summary>
    private void AddProductPhotos(string answer) => AddProductPhotoStrip(OwnerAiContext.FindMentionedProducts(answer));

    /// <summary>Фото товаров склада в чате (2026-10-06: и по списку — для «покажи фото Mars» голосом).</summary>
    private void AddProductPhotoStrip(IReadOnlyList<NurMarketKassa.Models.Pos.CatalogProductTileVm> products)
    {
        if (products.Count == 0)
            return;
        var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var services = App.AppHost?.Services;
        foreach (var product in products)
        {
            var image = new Image { Width = 112, Height = 112, Stretch = Stretch.UniformToFill };
            image.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(product.ProductImagePath))
            {
                Source = product,
                Converter = NurMarketKassa.AvaloniaHost.Converters.AssetPathToBitmapConverter.Instance,
                ConverterParameter = "thumb",
            });
            var caption = new TextBlock
            {
                Text = product.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 112,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, Margin = new Thickness(0, 4, 0, 0),
            };
            Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
            var card = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new StackPanel { Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image }, caption } },
            };
            Use(card, Border.BackgroundProperty, "BrushPanel");
            Use(card, Border.BorderBrushProperty, "BrushBorder");
            strip.Children.Add(card);
            // Фото ещё не скачано — качаем в кэш (как плитка каталога); путь подставится в картинку сам.
            if (string.IsNullOrEmpty(product.ProductImagePath) && !string.IsNullOrWhiteSpace(product.ImageUrl) && services is not null)
            {
                _ = services.GetRequiredService<ProductThumbService>().SetThumbAsync(
                    Dispatcher.UIThread, services.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>(),
                    services.GetRequiredService<NurMarketKassa.Configuration.AppSettings>().ApiBaseUrl,
                    product.ImageUrl!, product, CancellationToken.None);
            }
        }
        _messages.Children.Add(strip);
        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    /// <summary>2026-10-21: пункт меню ⚙, который экранный диктор и UIA-автоматизация могут «нажать» (Invoke) — у MenuItem в
    /// Avalonia 11.2 этого нет (пакет 7, хвост Советника). Вид — обычного MenuItem.</summary>
    private sealed class InvokableMenuItem : MenuItem
    {
        protected override Type StyleKeyOverride => typeof(MenuItem);

        protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer : Avalonia.Automation.Peers.MenuItemAutomationPeer, Avalonia.Automation.Provider.IInvokeProvider
        {
            private readonly MenuItem _item;

            public Peer(MenuItem owner) : base(owner) => _item = owner;

            public void Invoke()
            {
                if (_item.IsEffectivelyEnabled)
                    _item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            }
        }
    }
}
