using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27: шаг 6 мастера «Предпросмотр» — пример корзины (товары из выбранных, количество и цена правятся) → POST promotions/preview/ → строки «Товар ×3 = 300 → 200 (−100)»,
/// предупреждения (скидка урезана до закупки, скидка больше строки) и подсказки; «Сохранить черновиком» / «Запустить» / «Сохранить изменения» (POST с Idempotency-Key или PATCH).</summary>
public sealed partial class PromotionsWindow
{
    private sealed class SampleRow
    {
        public string Product = "";
        public string Name = "";
        public TextBox Qty = null!;
        public TextBox Price = null!;
    }

    private readonly List<SampleRow> _sample = new();
    private readonly StackPanel _sampleHost = new() { Spacing = 6 };
    private readonly StackPanel _previewResult = new() { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private ProductPicker _samplePicker = null!;
    private Button _calc = null!;
    private int _previewGen;

    private Control BuildPreviewStep()
    {
        var p = StepPanel(T("Проверьте акцию на примере корзины: так же посчитает касса. Количество и цену можно менять. Затем сохраните черновиком или запустите.",
            "Акцияны себеттин мисалында текшериңиз: касса да ушундай эсептейт. Санын жана баасын өзгөртүүгө болот. Анан каралама катары сактаңыз же иштетиңиз.",
            "Check the promotion on a sample basket: the till calculates the same way. Quantity and price can be changed. Then save as a draft or start it.",
            "Kampanyayı örnek bir sepette kontrol edin: kasa da aynı şekilde hesaplar. Adet ve fiyat değiştirilebilir. Sonra taslak olarak kaydedin veya başlatın.",
            "Aksiyani savat misolida tekshiring: kassa ham shunday hisoblaydi. Miqdor va narxni o'zgartirish mumkin. So'ng qoralama sifatida saqlang yoki boshlang."));
        Use(_summary, TextBlock.ForegroundProperty, "BrushText");
        var summaryCard = Card(_summary);
        summaryCard.Margin = new Thickness(0, 0, 0, 10);
        p.Children.Add(summaryCard);

        p.Children.Add(Caption(T("Пример корзины", "Себеттин мисалы", "Sample basket", "Örnek sepet", "Savat misoli")));
        p.Children.Add(_sampleHost);
        _samplePicker = new ProductPicker(this, new List<string>(), T("Добавить товар в пример", "Мисалга товар кошуу", "Add a product to the sample", "Örneğe ürün ekle", "Misolga mahsulot qo'shish"), AddSample);
        _samplePicker.View.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(_samplePicker.View);

        _calc = Small(UiKit.Primary(this, T("Посчитать", "Эсептөө", "Calculate", "Hesapla", "Hisoblash")));
        AutomationProperties.SetName(_calc, (string)_calc.Content!);
        _calc.HorizontalAlignment = HorizontalAlignment.Left;
        _calc.Margin = new Thickness(0, 10, 0, 0);
        _calc.Click += async (_, _) => await RunPreviewAsync().ConfigureAwait(true);
        p.Children.Add(_calc);
        p.Children.Add(_previewResult);
        return p;
    }

    private void EnterPreviewStep()
    {
        for (var s = 1; s <= 5; s++)
            ReadStep(s, out _, out _);
        _summary.Text = (_draft.Title.Length > 0 ? "«" + _draft.Title + "» — " : "") + PromotionText.KindLabel(_draft) + "\n"
            + ScopeSummary() + "\n" + PromotionText.Period(_draft) + " · " + PromotionText.DaysHours(_draft)
            + (_draft.Audience == "clients" ? "\n" + T("Только клиентам", "Кардарларга гана", "Customers only", "Yalnızca müşterilere", "Faqat mijozlarga") : "");
        if (_sample.Count == 0)
        {
            var qty = _draft.Kind == Promotion.KindBuyNGetM ? Math.Max(1, _draft.Buy + _draft.Get) : 3;
            foreach (var id in _draft.Products.Take(3))
                AddSampleRow(id, ProductName(id), _productNames.TryGetValue(id, out var pp) ? pp.Price : null, qty, render: false);
        }
        RenderSample();
        if (_sample.Count > 0 && _previewResult.Children.Count == 0)
            _ = RunPreviewAsync();
        else if (_sample.Count == 0)
            ShowPreviewNote(T("Добавьте в пример товар, на который действует акция (поиск выше), и нажмите «Посчитать».", "Мисалга акция таасир эткен товарды кошуп (жогорудагы издөө), «Эсептөө» басыңыз.",
                "Add a product the promotion applies to (search above) and press “Calculate”.", "Kampanyanın uygulandığı bir ürünü ekleyin (yukarıdaki arama) ve «Hesapla»ya basın.",
                "Misolga aksiya amal qiladigan mahsulotni qo'shing (yuqoridagi qidiruv) va «Hisoblash»ni bosing."), "BrushTextSoft");
    }

    private string ScopeSummary()
    {
        if (_draft.ScopeAll)
            return T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha mahsulotlar")
                + (_draft.ExcludeProducts.Count > 0 ? T($", кроме {_draft.ExcludeProducts.Count} шт.", $", {_draft.ExcludeProducts.Count} даанадан башка", $", except {_draft.ExcludeProducts.Count}",
                    $", {_draft.ExcludeProducts.Count} hariç", $", {_draft.ExcludeProducts.Count} tadan tashqari") : "");
        var parts = new List<string>();
        if (_draft.Products.Count > 0) parts.Add(T("товаров: ", "товарлар: ", "products: ", "ürün: ", "mahsulotlar: ") + _draft.Products.Count);
        if (_draft.Categories.Count > 0) parts.Add(T("категорий: ", "категориялар: ", "categories: ", "kategori: ", "toifalar: ") + _draft.Categories.Count);
        if (_draft.Brands.Count > 0) parts.Add(T("брендов: ", "бренддер: ", "brands: ", "marka: ", "brendlar: ") + _draft.Brands.Count);
        if (_draft.ExcludeProducts.Count > 0) parts.Add(T("исключено: ", "чыгарылды: ", "excluded: ", "hariç: ", "istisno: ") + _draft.ExcludeProducts.Count);
        return T("На что: ", "Эмнеге: ", "Applies to: ", "Neye: ", "Nimaga: ") + (parts.Count > 0 ? string.Join(", ", parts) : "—");
    }

    private void AddSample(PromotionProduct product)
    {
        if (_sample.Any(s => s.Product == product.Id))
            return;
        AddSampleRow(product.Id, product.Name, product.Price, _draft.Kind == Promotion.KindBuyNGetM ? Math.Max(1, _draft.Buy + _draft.Get) : 3, render: true);
    }

    private void AddSampleRow(string id, string name, decimal? price, int qty, bool render)
    {
        var row = new SampleRow
        {
            Product = id,
            Name = name,
            Qty = Box("", qty.ToString(CultureInfo.InvariantCulture)),
            Price = Box(T("цена сервера", "сервердин баасы", "server price", "sunucu fiyatı", "server narxi"), price is > 0 ? PromotionText.Num(price.Value) : ""),
        };
        row.Qty.MaxLength = 7;
        row.Price.MaxLength = 10;
        AutomationProperties.SetName(row.Qty, T("Количество: ", "Саны: ", "Quantity: ", "Adet: ", "Miqdor: ") + name);
        AutomationProperties.SetName(row.Price, T("Цена: ", "Баасы: ", "Price: ", "Fiyat: ", "Narx: ") + name);
        _sample.Add(row);
        if (render)
            RenderSample();
    }

    private void RenderSample()
    {
        foreach (var r in _sample)
        {
            (r.Qty.Parent as Panel)?.Children.Remove(r.Qty);
            (r.Price.Parent as Panel)?.Children.Remove(r.Price);
        }
        _sampleHost.Children.Clear();
        if (_sample.Count == 0)
            return;
        const string cols = "*,90,120,Auto";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
        var hName = Caption(T("Товар", "Товар", "Product", "Ürün", "Mahsulot"));
        var hQty = Caption(T("Кол-во", "Саны", "Qty", "Adet", "Soni"));
        var hPrice = Caption(T("Цена, сом", "Баасы, сом", "Price, som", "Fiyat, som", "Narx, so'm"));
        hQty.Margin = new Thickness(8, 0, 0, 4);
        hPrice.Margin = new Thickness(8, 0, 0, 4);
        header.Children.Add(hName);
        Grid.SetColumn(hQty, 1);
        header.Children.Add(hQty);
        Grid.SetColumn(hPrice, 2);
        header.Children.Add(hPrice);
        _sampleHost.Children.Add(header);
        foreach (var r in _sample)
        {
            var row = r;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
            var name = new TextBlock { Text = r.Name, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            grid.Children.Add(name);
            r.Qty.Margin = new Thickness(8, 0, 0, 0);
            r.Price.Margin = new Thickness(8, 0, 0, 0);
            r.Qty.HorizontalAlignment = r.Price.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(r.Qty, 1);
            grid.Children.Add(r.Qty);
            Grid.SetColumn(r.Price, 2);
            grid.Children.Add(r.Price);
            var remove = Small(UiKit.Ghost(this, "✕"));
            remove.Width = 44;
            remove.Padding = new Thickness(0);
            remove.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(remove, T("Убрать из примера: ", "Мисалдан алып салуу: ", "Remove from sample: ", "Örnekten çıkar: ", "Misoldan olib tashlash: ") + r.Name);
            remove.Click += (_, _) =>
            {
                _sample.Remove(row);
                RenderSample();
            };
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
            _sampleHost.Children.Add(grid);
        }
    }

    private async Task RunPreviewAsync()
    {
        ClearErrorMarks();
        if (!ValidateAll())
            return;
        var items = new List<PromotionPreviewItem>();
        foreach (var r in _sample)
        {
            if (!TryNum(r.Qty.Text, out var qty) || qty <= 0)
            {
                SetWizardStatus(T("Количество в примере — число больше нуля.", "Мисалдагы сан — нөлдөн чоң сан.", "The sample quantity must be above zero.", "Örnekteki adet sıfırdan büyük olmalı.",
                    "Misoldagi miqdor — noldan katta son."), "BrushDanger");
                Mark(r.Qty);
                return;
            }
            decimal? price = null;
            if ((r.Price.Text ?? "").Trim().Length > 0)
            {
                if (!TryNum(r.Price.Text, out var pr) || pr < 0)
                {
                    SetWizardStatus(T("Цена в примере — число сом или пусто (цена сервера).", "Мисалдагы баа — сом саны же бош (сервердин баасы).", "The sample price is a number of som or empty (server price).",
                        "Örnekteki fiyat som cinsinden sayı veya boş (sunucu fiyatı) olmalı.", "Misoldagi narx — so'm soni yoki bo'sh (server narxi)."), "BrushDanger");
                    Mark(r.Price);
                    return;
                }
                price = pr;
            }
            items.Add(new PromotionPreviewItem(r.Product, qty, price));
        }
        if (items.Count == 0)
        {
            ShowPreviewNote(T("В примере нет товаров — добавьте товар через поиск выше.", "Мисалда товар жок — жогорудагы издөө аркылуу товар кошуңуз.", "The sample has no products — add one via the search above.",
                "Örnekte ürün yok — yukarıdaki aramadan ürün ekleyin.", "Misolda mahsulot yo'q — yuqoridagi qidiruv orqali qo'shing."), "BrushTextSoft");
            return;
        }
        var gen = ++_previewGen;
        _calc.IsEnabled = false;
        ShowPreviewNote(T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…"), "BrushTextSoft");
        try
        {
            var result = await _api.PreviewAsync(_draft, items, _cts.Token).ConfigureAwait(true);
            if (gen != _previewGen)
                return;
            RenderPreview(result);
            SetWizardStatus("", "BrushTextSoft");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (gen != _previewGen)
                return;
            PosLogger.Log($"Акции: предпросмотр не посчитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            _previewResult.Children.Clear();
            ShowServerError(ex, creating: false);
        }
        finally
        {
            if (gen == _previewGen)
                _calc.IsEnabled = true;
        }
    }

    private void RenderPreview(PromotionPreview r)
    {
        _previewResult.Children.Clear();
        foreach (var l in r.Lines)
        {
            var name = _sample.ElementAtOrDefault(l.Index) is { } s && s.Product == l.Product ? s.Name
                : _sample.FirstOrDefault(x => x.Product == l.Product)?.Name ?? ProductName(l.Product);
            var sub = l.Price * l.Qty;
            var text = $"{name} ×{PromotionText.Num(l.Qty)} = {PromotionText.Num(sub)} → {PromotionText.Num(l.LineTotal)}"
                + (l.Discount > 0 ? $" (−{PromotionText.Num(l.Discount)})" : " — " + T("без скидки", "арзандатуусуз", "no discount", "indirimsiz", "chegirmasiz"));
            if (l.Applied.Count > 0)
                text += "\n    " + string.Join("; ", l.Applied.Select(a => (a.Title.Length > 0 ? a.Title : a.Kind) + ": −" + PromotionText.Money(a.Amount)));
            _previewResult.Children.Add(Line(text, l.Discount > 0 ? "BrushText" : "BrushTextSoft", FontWeight.Normal));
        }
        _previewResult.Children.Add(Line(T("Итого: ", "Жыйынтыгы: ", "Total: ", "Toplam: ", "Jami: ") + $"{PromotionText.Num(r.Subtotal)} → {PromotionText.Money(r.Total)}"
            + (r.Discount > 0 ? T($" (скидка {PromotionText.Money(r.Discount)})", $" (арзандатуу {PromotionText.Money(r.Discount)})", $" (discount {PromotionText.Money(r.Discount)})",
                $" (indirim {PromotionText.Money(r.Discount)})", $" (chegirma {PromotionText.Money(r.Discount)})") : ""), "BrushText", FontWeight.Bold));
        if (r.Discount == 0 && r.Lines.Count > 0)
            _previewResult.Children.Add(Line(T("Скидки нет: проверьте, что товары примера входят в «На что действует», а условия (сумма, количество, дни и часы) выполнены.",
                "Арзандатуу жок: мисалдагы товарлар «Эмнеге таасир этет» ичинде экенин жана шарттар (сумма, сан, күндөр жана сааттар) аткарылганын текшериңиз.",
                "No discount: check that the sample products are in “Applies to” and the conditions (amount, quantity, days and hours) are met.",
                "İndirim yok: örnek ürünlerin «Neye uygulanır» içinde olduğunu ve koşulların (tutar, adet, gün ve saat) sağlandığını kontrol edin.",
                "Chegirma yo'q: misoldagi mahsulotlar «Nimaga amal qiladi» ichida ekanini va shartlar (summa, miqdor, kunlar va soatlar) bajarilganini tekshiring."), "BrushWarning", FontWeight.Normal));
        foreach (var w in r.Warnings)
        {
            var name = w.Index is { } i && r.Lines.FirstOrDefault(x => x.Index == i) is { } ln ? _sample.FirstOrDefault(x => x.Product == ln.Product)?.Name ?? ProductName(ln.Product) : "";
            _previewResult.Children.Add(Line(PromotionText.Warning(w, name), "BrushWarning", FontWeight.SemiBold));
        }
        foreach (var h in r.Hints.Where(h => h.Text.Length > 0))
            _previewResult.Children.Add(Line(h.Text, "BrushAccentText", FontWeight.Normal));
    }

    private TextBlock Line(string text, string brush, FontWeight weight)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    private void ShowPreviewNote(string text, string brush)
    {
        _previewResult.Children.Clear();
        _previewResult.Children.Add(Line(text, brush, FontWeight.Normal));
    }

    // ── Сохранение ────────────────────────────────────────────────────────────────

    /// <summary>Проверить шаги 1–5; первый неверный открывается с подсвеченным полем.</summary>
    private bool ValidateAll()
    {
        for (var s = 1; s <= 5; s++)
        {
            if (ReadStep(s, out var error, out var field))
                continue;
            if (_step != s)
            {
                _step = s;
                ShowStep();
            }
            SetWizardStatus(error, "BrushDanger");
            Mark(field);
            return false;
        }
        return true;
    }

    private async Task SaveAsync(bool start)
    {
        if (_saving)
            return;
        ClearErrorMarks();
        if (!ValidateAll())
            return;
        var creating = _draft.Id.Length == 0;
        _saving = true;
        SetSaveButtons(false);
        SetWizardStatus(T("Сохраняю…", "Сактап жатам…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…"), "BrushTextSoft");
        try
        {
            Promotion saved;
            var replayed = false;
            if (creating)
            {
                saved = await _api.CreateAsync(_draft, _idemKey, start ? Promotion.StatusActive : null, _cts.Token).ConfigureAwait(true);
                replayed = saved.Replayed;
                // Создана — дальше только PATCH (если «Запустить» ниже не пройдёт, следующее сохранение не создаст копию).
                _draft.Id = saved.Id;
                _draft.Status = saved.Status;
                _draft.Version = saved.Version;
                if (start && saved.IsDraft && saved.Id.Length > 0)
                    saved = await _api.ChangeStatusAsync(saved.Id, "start", _cts.Token).ConfigureAwait(true) ?? saved;
            }
            else
            {
                var wasDraft = _draft.IsDraft;
                saved = await _api.UpdateAsync(_draft, _cts.Token).ConfigureAwait(true);
                if (start && wasDraft)
                    saved = await _api.ChangeStatusAsync(_draft.Id, "start", _cts.Token).ConfigureAwait(true) ?? saved;
            }
            var title = _draft.Title;
            PosLogger.Log($"Акции: «{title}» ({saved.Id}) сохранена{(start ? " и запущена" : "")}{(replayed ? " (повтор ключа — сервер вернул прежнюю)" : "")}.", "INFO");
            _saving = false;
            CloseWizard();
            await LoadAsync().ConfigureAwait(true);
            var msg = start
                ? T($"Акция «{title}» запущена — касса начнёт давать скидку в указанные дни и часы.", $"«{title}» акциясы иштетилди — касса көрсөтүлгөн күндөрү жана сааттары арзандатуу берет.",
                    $"Promotion “{title}” started — the till gives the discount on the set days and hours.", $"«{title}» kampanyası başlatıldı — kasa belirtilen gün ve saatlerde indirim verir.",
                    $"«{title}» aksiyasi boshlandi — kassa belgilangan kun va soatlarda chegirma beradi.")
                : creating
                    ? T($"Черновик «{title}» сохранён. Запустите его кнопкой «Запустить» во вкладке «Черновики».", $"«{title}» караламасы сакталды. Аны «Каралама» өтмөгүндөгү «Иштетүү» баскычы менен иштетиңиз.",
                        $"Draft “{title}” saved. Start it with “Start” on the “Drafts” tab.", $"«{title}» taslağı kaydedildi. «Taslaklar» sekmesindeki «Başlat» ile başlatın.",
                        $"«{title}» qoralamasi saqlandi. Uni «Qoralamalar» yorlig'idagi «Boshlash» tugmasi bilan boshlang.")
                    : T($"Изменения акции «{title}» сохранены.", $"«{title}» акциясынын өзгөртүүлөрү сакталды.", $"Changes to “{title}” saved.", $"«{title}» değişiklikleri kaydedildi.", $"«{title}» o'zgarishlari saqlandi.");
            if (replayed)
                msg += " " + T("(Сервер уже получил это сохранение раньше — копия не создана.)", "(Сервер бул сактоону мурда алган — көчүрмө түзүлгөн жок.)", "(The server had already received this save — no copy was created.)",
                    "(Sunucu bu kaydı daha önce almıştı — kopya oluşturulmadı.)", "(Server bu saqlashni avval olgan — nusxa yaratilmadi.)");
            SetStatus(msg, "BrushSuccess");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: «{_draft.Title}» не сохранена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowServerError(ex, creating);
        }
        finally
        {
            _saving = false;
            SetSaveButtons(true);
            if (_wizardView.IsVisible)
                UpdateWizardButtons();
        }
    }

    private void ShowServerError(Exception ex, bool creating)
    {
        // 4xx — сервер точно ничего не создал: следующая попытка (после исправления) — с новым ключом, иначе сервер мог бы вернуть сохранённый отказ.
        // Сбой связи / 5xx — исход неизвестен: повтор тем же ключом, чтобы не создать вторую акцию.
        if (creating && _draft.Id.Length == 0 && ex is ApiException { StatusCode: >= 400 and < 500 })
            _idemKey = Guid.NewGuid().ToString("N");
        var e = PromotionErrors.Describe(ex);
        if (e.Step > 0 && e.Step != _step)
        {
            ReadStep(_step, out _, out _);
            _step = e.Step;
            ShowStep();
        }
        SetWizardStatus(e.Text, "BrushDanger");
        Mark(FieldControl(e.Field));
    }

    private void SetSaveButtons(bool enabled)
    {
        foreach (var b in new[] { _saveDraft, _startButton, _saveChanges, _next, _back })
            b.IsEnabled = enabled;
    }
}
