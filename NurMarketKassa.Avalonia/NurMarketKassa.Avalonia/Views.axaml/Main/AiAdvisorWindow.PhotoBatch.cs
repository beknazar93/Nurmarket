using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-03, владелец (без фото 15 252 товара, советник нашёл для 10): «исправь это ограничение или по этапам добавляй по 50 или по 20 по
/// очереди, чтобы не нагружать систему». Поиск фото «для всех без фото» идёт пачками по 20 (или 50): пачка → результаты с «Поставить» →
/// «Искать дальше» (или само, если включено «по очереди»); что уже проверено — помнит <see cref="PhotoSearchMemory"/>, следующий запрос продолжает с
/// того места. Фото уходит на сервер только по нажатию владельца (как раньше).</summary>
public sealed partial class AiAdvisorWindow
{
    private PhotoBatchRunner<CatalogProductTileVm, ProductPhotoFinder.Candidate>? _photoRunner;
    private IReadOnlyList<CatalogProductTileVm>? _photoExplicit;
    private SelectableTextBlock? _photoStatus;
    private StackPanel? _photoControls;
    private bool _photoRunning;
    private int _photoGen;
    private int _photoPending;

    /// <summary>Пауза между пачками в режиме «по очереди».</summary>
    private static readonly TimeSpan PhotoAutoPause = TimeSpan.FromSeconds(7);

    /// <summary>Поиск Google через Gemini сейчас доступен (ключ есть, лимит поиска не исчерпан).</summary>
    private static bool PhotoWebAvailable => TelegramAiChat.IsConfigured && !TelegramAiChat.WebSearchUnavailable;

    private async Task StartPhotoBatchesAsync(IReadOnlyList<CatalogProductTileVm> named, IReadOnlyList<CatalogProductTileVm> withoutPhoto)
    {
        var memory = PhotoSearchMemory.Instance;
        _photoExplicit = named.Count > 0 ? named : null;
        _photoRunner = new PhotoBatchRunner<CatalogProductTileVm, ProductPhotoFinder.Candidate>(memory, p => p.Id, () => PhotoWebAvailable);
        _photoPending = _photoExplicit is null ? _photoRunner.CountPending(withoutPhoto, false) : withoutPhoto.Count;
        string text;
        if (_photoExplicit is not null)
            text = T($"Ищу фото: {string.Join(", ", named.Take(5).Select(p => p.Title))} — по штрихкоду, названию и в интернете…",
                $"Сүрөт издеп жатам: {string.Join(", ", named.Take(5).Select(p => p.Title))} — штрихкод, аталыш жана интернет боюнча…",
                $"Searching photos: {string.Join(", ", named.Take(5).Select(p => p.Title))} — by barcode, name and on the web…",
                $"Fotoğraf arıyorum: {string.Join(", ", named.Take(5).Select(p => p.Title))} — barkod, ad ve internette…",
                $"Rasm qidiryapman: {string.Join(", ", named.Take(5).Select(p => p.Title))} — shtrix-kod, nom va internet bo'yicha…");
        else
        {
            var size = memory.BatchSize;
            var n = withoutPhoto.Count;
            text = T($"Без фото: {n:N0} товаров. Ищу пачками по {size}; в любой момент можно остановить.",
                $"Сүрөтсүз: {n:N0} товар. {size} товардан пачкалап издейм; каалаган убакта токтотсоңуз болот.",
                $"Without a photo: {n:N0} products. Searching in batches of {size}; you can stop at any time.",
                $"Fotoğrafsız: {n:N0} ürün. {size}'lik gruplar halinde arıyorum; istediğiniz zaman durdurabilirsiniz.",
                $"Rasmsiz: {n:N0} ta mahsulot. {size} tadan guruhlab qidiryapman; istalgan vaqtda to'xtatishingiz mumkin.");
            var skipped = n - _photoPending;
            if (skipped > 0)
                text += "\n" + T($"Уже проверено раньше: {skipped:N0} — продолжаю с того места (заново — кнопка «Искать заново»).",
                    $"Мурун текшерилген: {skipped:N0} — ошол жерден улантам (кайра издөө — «Кайра издөө» баскычы).",
                    $"Already checked before: {skipped:N0} — continuing from where I stopped (start over — the “Search again” button).",
                    $"Daha önce kontrol edilen: {skipped:N0} — kaldığım yerden devam ediyorum (baştan — «Yeniden ara» düğmesi).",
                    $"Oldin tekshirilgan: {skipped:N0} — qolgan joyidan davom etaman (qaytadan — «Qaytadan qidirish» tugmasi).");
            if (TelegramAiChat.IsConfigured && TelegramAiChat.WebSearchUnavailable)
                text += "\n" + T("Поиск Google сейчас недоступен — ищу в открытых базах и Яндекс.Картинках.",
                    "Google издөөсү азыр жеткиликсиз — ачык базалардан жана Яндекс.Сүрөттөрдөн издеп жатам.",
                    "Google search is unavailable right now — searching open databases and Yandex Images.",
                    "Google araması şu an kullanılamıyor — açık veritabanlarında ve Yandex Görseller'de arıyorum.",
                    "Google qidiruvi hozir mavjud emas — ochiq bazalar va Yandex Rasmlarida qidiryapman.");
        }
        _photoStatus = AddBubble(text, fromOwner: false);
        var first = withoutPhoto;
        await RunPhotoSessionAsync(() => first).ConfigureAwait(true);
    }

    /// <summary>Пачка (или пачки подряд, если включено «по очереди») — до остановки, конца товаров или конца лимита ключа.</summary>
    private async Task RunPhotoSessionAsync(Func<IReadOnlyList<CatalogProductTileVm>>? firstLoad = null)
    {
        var runner = _photoRunner;
        if (runner is null)
            return;
        var memory = PhotoSearchMemory.Instance;
        var gen = ++_photoGen;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _photoRunning = true;
        AttachPhotoControls(running: true);
        var explicitList = _photoExplicit;
        var explicitMode = explicitList is not null;
        var preloaded = firstLoad;
        IReadOnlyList<CatalogProductTileVm> Load()
        {
            if (preloaded is not null)
            {
                var first = preloaded();
                preloaded = null;
                return first;
            }
            return explicitList ?? ProductPhotoFinder.WithoutPhoto();
        }
        try
        {
            await Task.Run(() => runner.RunAsync(Load, () => explicitMode ? explicitList!.Count : memory.BatchSize, () => !explicitMode && memory.AutoNext,
                explicitMode, PhotoAutoPause, (product, token) => ProductPhotoFinder.SearchOneAsync(product, () => PhotoWebAvailable, withDescription: true, token),
                (done, size) => Dispatcher.UIThread.Post(() =>
                {
                    if (gen == _photoGen)
                        UpdatePhotoStatus(done, size);
                }),
                async result => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (gen == _photoGen)
                        ShowPhotoBatch(result);
                }), ct), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // 2026-10-05, проверка: поиск молча обрывался — причина видна владельцу и в журнале.
            PosLogger.Log($"ИИ-советник: поиск фото прерван ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            if (gen == _photoGen && _photoStatus is not null)
                _photoStatus.Text = T("Не получилось искать фото: ", "Сүрөт издөө болбой калды: ", "Photo search failed: ", "Fotoğraf aranamadı: ", "Rasm qidirib bo'lmadi: ") + ex.Message;
        }
        finally
        {
            if (gen == _photoGen)
            {
                _photoRunning = false;
                AttachPhotoControls(running: false);
            }
        }
    }

    private string PhotoCountsLine(int doneInBatch)
    {
        var r = _photoRunner!;
        var viewed = r.Viewed + doneInBatch;
        var left = Math.Max(0, _photoPending - doneInBatch);
        return T($"Без фото: {r.Total:N0} · просмотрено {viewed:N0} · найдено {r.FoundCount:N0} · осталось ~{left:N0}",
            $"Сүрөтсүз: {r.Total:N0} · каралды {viewed:N0} · табылды {r.FoundCount:N0} · калды ~{left:N0}",
            $"Without a photo: {r.Total:N0} · checked {viewed:N0} · found {r.FoundCount:N0} · left ~{left:N0}",
            $"Fotoğrafsız: {r.Total:N0} · bakılan {viewed:N0} · bulunan {r.FoundCount:N0} · kalan ~{left:N0}",
            $"Rasmsiz: {r.Total:N0} · ko'rildi {viewed:N0} · topildi {r.FoundCount:N0} · qoldi ~{left:N0}");
    }

    private void UpdatePhotoStatus(int done, int size)
    {
        if (_photoStatus is null || _photoRunner is null)
            return;
        var batch = _photoRunner.Batches + 1;
        _photoStatus.Text = _photoExplicit is not null
            ? T($"Ищу фото… {done} из {size}", $"Сүрөт издеп жатам… {done} / {size}", $"Searching… {done} of {size}", $"Aranıyor… {done} / {size}", $"Qidiryapman… {done} / {size}")
            : T($"Ищу пачку {batch}: {done} из {size}…", $"{batch}-пачканы издеп жатам: {done} / {size}…", $"Searching batch {batch}: {done} of {size}…",
                $"{batch}. grubu arıyorum: {done} / {size}…", $"{batch}-guruhni qidiryapman: {done} / {size}…") + "\n" + PhotoCountsLine(done);
    }

    private void RemovePhotoControls()
    {
        if (_photoControls is not null)
            _messages.Children.Remove(_photoControls);
        _photoControls = null;
    }

    /// <summary>Кнопки под результатами: «Искать дальше (ещё N)» / «Остановить», размер пачки 20 | 50, «по очереди», «Искать заново».</summary>
    private void AttachPhotoControls(bool running)
    {
        RemovePhotoControls();
        var runner = _photoRunner;
        if (runner is null || _photoExplicit is not null)
            return;
        var memory = PhotoSearchMemory.Instance;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 4, 0, 8) };
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(wrap);
        Control Spaced(Control c)
        {
            c.Margin = new Thickness(0, 0, 8, 8);
            return c;
        }
        if (running)
        {
            var stop = UiKit.Danger(this, T("Остановить", "Токтотуу", "Stop", "Durdur", "To'xtatish"));
            stop.Click += (_, _) =>
            {
                stop.IsEnabled = false;
                stop.Content = T("Останавливаю…", "Токтотуп жатам…", "Stopping…", "Durduruluyor…", "To'xtatilyapti…");
                _cts?.Cancel();
            };
            wrap.Children.Add(Spaced(stop));
        }
        else if (runner.Batches == 0 || runner.Remaining > 0)
        {
            var count = runner.Batches == 0 ? memory.BatchSize : Math.Min(memory.BatchSize, runner.Remaining);
            var next = UiKit.Primary(this, T($"Искать дальше (ещё {count})", $"Андан ары издөө (дагы {count})", $"Search more ({count} more)", $"Aramaya devam ({count} tane daha)", $"Davom ettirish (yana {count} ta)"));
            next.Click += async (_, _) => await RunPhotoSessionAsync().ConfigureAwait(true);
            wrap.Children.Add(Spaced(next));
        }
        var sizeLabel = new TextBlock { Text = T("Пачка:", "Пачка:", "Batch:", "Grup:", "Guruh:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 8) };
        Use(sizeLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        wrap.Children.Add(sizeLabel);
        var b20 = UiKit.Chip(this, "20", memory.BatchSize == 20);
        var b50 = UiKit.Chip(this, "50", memory.BatchSize == 50);
        b20.Click += (_, _) =>
        {
            memory.BatchSize = 20;
            AttachPhotoControls(_photoRunning);
        };
        b50.Click += (_, _) =>
        {
            memory.BatchSize = 50;
            AttachPhotoControls(_photoRunning);
        };
        wrap.Children.Add(Spaced(b20));
        wrap.Children.Add(Spaced(b50));
        var auto = UiKit.Chip(this, T("Искать автоматически по очереди", "Автоматтык түрдө кезеги менен издөө", "Search automatically, batch after batch",
            "Otomatik olarak sırayla ara", "Avtomatik ravishda navbat bilan qidirish"), memory.AutoNext);
        auto.Click += (_, _) =>
        {
            memory.AutoNext = !memory.AutoNext;
            UiKit.SetActive(auto, memory.AutoNext);
        };
        wrap.Children.Add(Spaced(auto));
        var redo = UiKit.Ghost(this, T("Искать заново", "Кайра издөө", "Search again", "Yeniden ara", "Qaytadan qidirish"));
        redo.IsEnabled = !running;
        redo.Click += async (_, _) =>
        {
            var forgotten = memory.ResetChecked();
            _photoRunner = new PhotoBatchRunner<CatalogProductTileVm, ProductPhotoFinder.Candidate>(memory, p => p.Id, () => PhotoWebAvailable);
            _photoPending = await Task.Run(() => ProductPhotoFinder.WithoutPhoto().Count).ConfigureAwait(true);
            AddBubble(T($"Забыл, что уже проверено ({forgotten:N0}). Нажмите «Искать дальше» — начну с начала списка.",
                $"Текшерилгенди унуттум ({forgotten:N0}). «Андан ары издөө» басыңыз — тизменин башынан баштайм.",
                $"Forgot what was already checked ({forgotten:N0}). Press “Search more” — I will start from the top of the list.",
                $"Kontrol edilenleri unuttum ({forgotten:N0}). «Aramaya devam»a basın — listenin başından başlarım.",
                $"Tekshirilganlarni unutdim ({forgotten:N0}). «Davom ettirish»ni bosing — ro'yxat boshidan boshlayman."), fromOwner: false);
            AttachPhotoControls(false);
        };
        wrap.Children.Add(Spaced(redo));
        _messages.Children.Add(panel);
        _photoControls = panel;
        ScrollToEnd();
    }

    /// <summary>Результаты одной пачки: уверенные — с «Поставить все (N)», сомнительные — отдельно с пометкой «проверьте», не найденные — с «📷».</summary>
    private void ShowPhotoBatch(PhotoBatchRunner<CatalogProductTileVm, ProductPhotoFinder.Candidate>.BatchResult r)
    {
        var runner = _photoRunner;
        if (runner is null)
            return;
        RemovePhotoControls();
        _photoPending = r.Remaining;
        var explicitMode = _photoExplicit is not null;
        var good = r.Found.Where(f => !f.Result.Doubtful).Select(f => f.Result).ToList();
        var weak = r.Found.Where(f => f.Result.Doubtful).Select(f => f.Result).ToList();
        if (!explicitMode && r.Processed > 0)
        {
            var header = new TextBlock
            {
                Text = T($"Пачка {runner.Batches}: просмотрено {r.Processed}, найдено {r.Found.Count}", $"{runner.Batches}-пачка: каралды {r.Processed}, табылды {r.Found.Count}",
                    $"Batch {runner.Batches}: checked {r.Processed}, found {r.Found.Count}", $"{runner.Batches}. grup: bakılan {r.Processed}, bulunan {r.Found.Count}",
                    $"{runner.Batches}-guruh: ko'rildi {r.Processed}, topildi {r.Found.Count}"),
                FontWeight = FontWeight.SemiBold, FontSize = 13.5, Margin = new Thickness(4, 8, 0, 4),
            };
            Use(header, TextBlock.ForegroundProperty, "BrushText");
            _messages.Children.Add(header);
        }
        if (good.Count > 0)
            AddPhotoStrip(good, withAll: true);
        if (weak.Count > 0)
        {
            AddBubble(T($"Не уверен, что это тот товар ({weak.Count}) — проверьте глазами; в «Поставить все» эти фото не входят:",
                $"Бул ошол товар экенине ишенбейм ({weak.Count}) — өзүңүз текшериңиз; «Баарын коюу» бул сүрөттөрдү камтыбайт:",
                $"Not sure these are the right products ({weak.Count}) — check them yourself; “Set all” does not include them:",
                $"Bunların doğru ürün olduğundan emin değilim ({weak.Count}) — kendiniz kontrol edin; «Tümünü ayarla» bunları içermez:",
                $"Bular to'g'ri mahsulot ekaniga ishonchim komil emas ({weak.Count}) — o'zingiz tekshiring; «Hammasini o'rnatish» ularni qamramaydi:"), fromOwner: false);
            AddPhotoStrip(weak, withAll: false);
        }
        if (r.NotFound.Count > 0)
        {
            AddBubble(T($"Для {r.NotFound.Count} товаров фото в базах и интернете нет (или нет штрихкода). Сфотографируйте их — нажмите на товар ниже и выберите снимок:",
                $"{r.NotFound.Count} товардын сүрөтү базаларда жана интернетте жок (же штрихкод жок). Аларды сүрөткө тартыңыз — төмөндөгү товарды басып, сүрөттү тандаңыз:",
                $"No photo in the databases or on the web for {r.NotFound.Count} products (or no barcode). Take photos — tap a product below and pick the picture:",
                $"{r.NotFound.Count} ürün için veritabanlarında ve internette fotoğraf yok (veya barkod yok). Fotoğraflarını çekin — aşağıdaki ürüne dokunup resmi seçin:",
                $"{r.NotFound.Count} ta mahsulot uchun bazalarda va internetda rasm yo'q (yoki shtrix-kod yo'q). Ularni suratga oling — pastdagi mahsulotni bosing va rasmni tanlang:"), fromOwner: false);
            var list = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var product in r.NotFound.Take(30))
            {
                var b = UiKit.Ghost(this, "📷 " + product.Title);
                b.Height = 34;
                b.FontSize = 12.5;
                b.Padding = new Thickness(10, 0);
                b.Margin = new Thickness(0, 0, 6, 6);
                b.Click += async (_, _) => await PickAndUploadAsync(b, product).ConfigureAwait(true);
                list.Children.Add(b);
            }
            _messages.Children.Add(list);
        }
        if (_photoStatus is not null)
        {
            string line;
            if (r.QuotaStop)
                line = T("Лимит ключа Gemini исчерпан, продолжу позже. Найденное сохранено, проверенное запомнено — «Искать дальше» продолжит с того же места.",
                    "Gemini ачкычынын лимити түгөндү, кийинчерээк улантам. Табылганы сакталды, текшерилгени эсте калды — «Андан ары издөө» ошол жерден улантат.",
                    "The Gemini key limit is used up, I will continue later. What was found is kept and checked products are remembered — “Search more” continues from the same place.",
                    "Gemini anahtarının sınırı doldu, daha sonra devam edeceğim. Bulunanlar saklandı, bakılanlar hatırlandı — «Aramaya devam» aynı yerden sürdürür.",
                    "Gemini kalitining limiti tugadi, keyinroq davom etaman. Topilgani saqlandi, tekshirilgani eslab qolindi — «Davom ettirish» o'sha joydan davom etadi.");
            else if (r.Cancelled)
                line = T("Остановлено. Найденное — выше; проверенное запомнено, «Искать дальше» продолжит с того же места.",
                    "Токтотулду. Табылганы — жогоруда; текшерилгени эсте калды, «Андан ары издөө» ошол жерден улантат.",
                    "Stopped. What was found is above; checked products are remembered, “Search more” continues from the same place.",
                    "Durduruldu. Bulunanlar yukarıda; bakılanlar hatırlandı, «Aramaya devam» aynı yerden sürdürür.",
                    "To'xtatildi. Topilgani yuqorida; tekshirilgani eslab qolindi, «Davom ettirish» o'sha joydan davom etadi.");
            else if (!explicitMode && r.Remaining <= 0)
                line = T("Все товары без фото проверены. Те, где ничего не нашлось, я перепроверю не раньше чем через 30 дней (или по кнопке «Искать заново»).",
                    "Сүрөтсүз товарлардын баары текшерилди. Эч нерсе табылбагандарды 30 күндөн кийин кайра текшерем (же «Кайра издөө» баскычы менен).",
                    "All products without a photo have been checked. Those with nothing found will be rechecked after 30 days at the earliest (or with “Search again”).",
                    "Fotoğrafsız tüm ürünler kontrol edildi. Hiçbir şey bulunamayanları en erken 30 gün sonra yeniden kontrol ederim («Yeniden ara» ile de olur).",
                    "Rasmsiz barcha mahsulotlar tekshirildi. Hech narsa topilmaganlarni 30 kundan keyin qayta tekshiraman («Qaytadan qidirish» tugmasi bilan ham bo'ladi).");
            else if (!explicitMode && PhotoSearchMemory.Instance.AutoNext)
                line = T("Пачка готова. Следующая начнётся сама через несколько секунд; чтобы прекратить, нажмите «Остановить».",
                    "Пачка даяр. Кийинкиси бир нече секундадан кийин өзү башталат; токтотуу үчүн «Токтотуу» басыңыз.",
                    "Batch done. The next one starts by itself in a few seconds; press “Stop” to end.",
                    "Grup hazır. Sonraki birkaç saniye içinde kendiliğinden başlar; durdurmak için «Durdur»a basın.",
                    "Guruh tayyor. Keyingisi bir necha soniyadan keyin o'zi boshlanadi; to'xtatish uchun «To'xtatish»ni bosing.");
            else
                line = T($"Нашёл фото для {r.Found.Count} товаров. Проверьте и нажмите «Поставить» — фото (и описание, если оно нашлось) уйдёт в карточку товара на сервере.",
                    $"{r.Found.Count} товардын сүрөтү табылды. Текшерип, «Коюу» басыңыз — сүрөт (жана сүрөттөмө, табылса) сервердеги товар карточкасына кетет.",
                    $"Found photos for {r.Found.Count} products. Check them and press “Set” — the photo (and the description, if found) goes to the product card on the server.",
                    $"{r.Found.Count} ürün için fotoğraf bulundu. Kontrol edip «Ayarla»ya basın — fotoğraf (ve bulunduysa açıklama) sunucudaki ürün kartına gider.",
                    $"{r.Found.Count} ta mahsulot uchun rasm topildi. Tekshirib «O'rnatish»ni bosing — rasm (va topilsa, tavsif) serverdagi mahsulot kartasiga ketadi.");
            _photoStatus.Text = explicitMode ? line : line + "\n" + PhotoCountsLine(0);
        }
        AttachPhotoControls(_photoRunning);
        ScrollToEnd();
    }

    /// <summary>Плитки найденных фото (слева картинка, справа название, источник, описание и «Поставить»). withAll — над плитками «Поставить все (N)».</summary>
    private void AddPhotoStrip(List<ProductPhotoFinder.Candidate> items, bool withAll)
    {
        var strip = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var setButtons = new List<(Button Button, ProductPhotoFinder.Candidate Candidate)>();
        foreach (var candidate in items)
        {
            var image = new Image { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
            _ = LoadPreviewAsync(image, candidate.ImageUrl);
            var caption = new TextBlock { Text = candidate.Product.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(caption, TextBlock.ForegroundProperty, "BrushText");
            var sourceText = (candidate.Doubtful ? "⚠ " + T("проверьте", "текшериңиз", "check", "kontrol edin", "tekshiring") + " · " : "") + candidate.Source;
            var source = new TextBlock { Text = sourceText, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(source, TextBlock.ForegroundProperty, "BrushTextSoft");
            var set = UiKit.Ghost(this, string.IsNullOrWhiteSpace(candidate.Description)
                ? T("Поставить", "Коюу", "Set", "Ayarla", "O'rnatish")
                : T("Поставить фото и описание", "Сүрөттү жана сүрөттөмөнү коюу", "Set photo and description", "Fotoğraf ve açıklamayı ayarla", "Rasm va tavsifni o'rnatish"));
            set.Height = 32;
            set.FontSize = 12.5;
            set.Padding = new Thickness(8, 0);
            set.HorizontalAlignment = HorizontalAlignment.Left;
            set.Click += async (_, _) => await ApplyPhotoAsync(set, candidate).ConfigureAwait(true);
            if (withAll)
                setButtons.Add((set, candidate));
            var texts = new StackPanel { Spacing = 4, MaxWidth = 250, Children = { caption, source } };
            if (!string.IsNullOrWhiteSpace(candidate.Description))
            {
                var desc = new TextBlock { Text = candidate.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(desc, TextBlock.ForegroundProperty, "BrushText");
                ToolTip.SetTip(desc, candidate.Description + "\n" + T("Источник: ", "Булак: ", "Source: ", "Kaynak: ", "Manba: ") + candidate.DescriptionSource);
                var descSource = new TextBlock { Text = T("Описание из интернета · ", "Интернеттен сүрөттөмө · ", "Description from the web · ", "İnternetten açıklama · ", "Internetdan tavsif · ") + candidate.DescriptionSource, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(descSource, TextBlock.ForegroundProperty, "BrushTextSoft");
                texts.Children.Add(desc);
                texts.Children.Add(descSource);
            }
            texts.Children.Add(set);
            var card = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Child = image }, texts },
                },
            };
            Use(card, Border.BackgroundProperty, "BrushPanel");
            Use(card, Border.BorderBrushProperty, "BrushBorder");
            strip.Children.Add(card);
        }
        if (withAll)
        {
            var all = UiKit.Primary(this, T($"Поставить все ({items.Count})", $"Баарын коюу ({items.Count})", $"Set all ({items.Count})", $"Tümünü ayarla ({items.Count})", $"Hammasini o'rnatish ({items.Count})"));
            all.Margin = new Thickness(4, 0, 0, 8);
            all.Click += async (_, _) =>
            {
                all.IsEnabled = false;
                foreach (var (button, candidate) in setButtons.Where(b => b.Button.IsEnabled).ToList())
                    await ApplyPhotoAsync(button, candidate).ConfigureAwait(true);
            };
            _messages.Children.Add(all);
        }
        _messages.Children.Add(strip);
    }
}
