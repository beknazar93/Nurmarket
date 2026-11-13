using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-06: В15г — часть класса BarcodePrintView (Settings): перенесено из BarcodePrintWindow.cs без изменений логики.
public partial class BarcodePrintView
{
    // ------------------------------------------------------------------ сканер «скан → в печать» (П-5)

    private void ScanToQueue(string raw)
    {
        var code = raw.Trim();
        CatalogProductTileVm? product = null;
        string? matchedCode = null;
        try
        {
            var hit = ProductCodeLookup.FindLocal(code, allowWeightLabel: false);
            product = hit?.Product;
            if (hit?.Variant?.Barcode is { Length: > 0 } variantCode)
                matchedCode = variantCode.Trim();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: scan lookup failed: {ex.GetType().Name}", "WARNING");
        }
        if (product is null)
        {
            SetStatus(T($"Не найден: {code}", $"Табылган жок: {code}", $"Not found: {code}", $"Bulunamadı: {code}", $"Topilmadi: {code}"));
            _search.SelectAll();
            return;
        }
        var choices = BarcodePrintCatalog.Choices(product);
        if (choices.Count == 0 || !IsPrintable(product))
        {
            SetStatus(T($"У товара «{product.Title}» нет штрихкода для печати.", $"«{product.Title}» товарында басуу үчүн штрих-код жок.", $"“{product.Title}” has no barcode to print.", $"«{product.Title}» ürününün yazdırılacak barkodu yok.", $"«{product.Title}» mahsulotida chop etish uchun shtrix-kod yo'q."));
            _search.SelectAll();
            return;
        }
        var idx = choices.FindIndex(c => string.Equals(c.Code, matchedCode ?? code, StringComparison.OrdinalIgnoreCase));
        var st = GetSel(product.Id);
        st.BarcodeIndex = Math.Max(idx, 0);
        if (!st.Selected)
        {
            SetSelected(st, true);
            st.Copies = 1;
        }
        else
            st.Copies = Math.Min(99, CopiesOf(product, st) + 1);
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        foreach (var s in _pageSync)
            s();
        foreach (var s in _codeSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
        _scanClearing = true;
        try { _search.Text = ""; }
        finally { _scanClearing = false; }
        SetStatus($"＋ {product.Title} · " + T($"копий: {CopiesOf(product, st)}", $"көчүрмө: {CopiesOf(product, st)}", $"copies: {CopiesOf(product, st)}", $"kopya: {CopiesOf(product, st)}", $"nusxa: {CopiesOf(product, st)}"));
    }

    // ------------------------------------------------------------------ выбор

    public void SelectPage()
    {
        var start = _page * PageSize;
        var end = Math.Min(start + PageSize, _found.Count);
        for (var i = start; i < end; i++)
            if (IsPrintable(_found[i]))
                SetSelected(GetSel(_found[i].Id), true);
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    public void SelectFound()
    {
        foreach (var p in _found)
            if (IsPrintable(p))
                SetSelected(GetSel(p.Id), true);
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    public void ClearSelection()
    {
        foreach (var s in _sel.Values)
            s.Selected = false;
        _selectedCount = 0;
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    private int TotalLabels()
    {
        if (_selectedCount == 0)
            return 0;
        var n = 0;
        foreach (var p in CatalogCacheService.Products)
            if (_sel.TryGetValue(p.Id, out var s) && s.Selected)
                n += CopiesOf(p, s);
        return n;
    }

    private string? _printButtonText;

    private void UpdateSelectionUi()
    {
        var labels = TotalLabels();
        _selectionText.Text = T($"Выбрано: {_selectedCount}", $"Тандалды: {_selectedCount}", $"Selected: {_selectedCount}", $"Seçildi: {_selectedCount}", $"Tanlandi: {_selectedCount}")
            + (_selectedCount > 0 ? T($" · этикеток: {labels}", $" · этикетка: {labels}", $" · labels: {labels}", $" · etiket: {labels}", $" · yorliq: {labels}") : "");
        // 2026-11-01 (П-1): «Печать выбранных (N · M эт.)», если этикеток больше, чем товаров
        var nm = labels > _selectedCount ? $"{_selectedCount} · {labels} " + T("эт.", "эт.", "lbl.", "et.", "yor.") : _selectedCount.ToString();
        // 2026-11-11: подпись и значок кнопки заменяются, только если текст поменялся (раньше новый значок создавался на каждый пересчёт — кнопки на кадр становились пустыми рамками, подсказка сбрасывалась).
        var printText = _compact
            ? T($"Печать ({nm})", $"Басуу ({nm})", $"Print ({nm})", $"Yazdır ({nm})", $"Chop etish ({nm})")
            : T($"Печать выбранных ({nm})", $"Тандалгандарды басуу ({nm})", $"Print selected ({nm})", $"Seçilenleri yazdır ({nm})", $"Tanlanganlarni chop etish ({nm})");
        if (_printButtonText != printText)
        {
            _printButtonText = printText;
            _printButton.Content = ButtonWithIcon("printer-outline", printText);
            ToolTip.SetTip(_printButton, T("Печать выбранных на термопринтер этикеток", "Тандалгандарды этикетка термопринтерине басуу", "Print the selected on the label thermal printer", "Seçilenleri etiket termal yazıcısında yazdır", "Tanlanganlarni yorliq termoprinterida chop etish"));
        }
        _printButton.IsEnabled = _selectedCount > 0;
        _resetCopiesButton.IsVisible = _sel.Values.Any(s => s.Copies != null);
        _a4Button.IsEnabled = _selectedCount > 0;
        _clearButton.IsEnabled = _selectedCount > 0;
        UpdateGenerateUi(); // 2026-11-12: «Сгенерировать штрихкоды (N)»
    }

    /// <summary>Выбранные товары в порядке каталога: товар, штрихкод (выбранный на карточке) и число копий.</summary>
    public List<BarcodePrintItem> BuildItems()
    {
        var list = new List<BarcodePrintItem>();
        foreach (var p in CatalogCacheService.Products)
        {
            if (!_sel.TryGetValue(p.Id, out var st) || !st.Selected)
                continue;
            var choices = BarcodePrintCatalog.Choices(p);
            var choice = choices.Count == 0 ? null : choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)];
            var code = choice?.Code;
            if (code == null && _template.Barcode.Enabled)
                continue;
            // 2026-11-01 (проверка массовой печати, С-2): у доп. штрихкода (упаковка/размер) на этикетке — его подпись.
            list.Add(new BarcodePrintItem(p, code, CopiesOf(p, st), choice is { IsMain: false } ? choice.VariantName : null, choice is { IsMain: false } ? choice.Quantity : 0, choice?.Price));
        }
        return list;
    }

    // ------------------------------------------------------------------ настройки этикетки

    private int FindPresetIndex()
    {
        for (var i = 0; i < SizePresets.Length; i++)
            if (Math.Abs(SizePresets[i].W - _template.WidthMm) < 0.05 && Math.Abs(SizePresets[i].H - _template.HeightMm) < 0.05)
                return i;
        return SizePresets.Length;
    }

    /// <summary>2026-11-12 (аудит Fable, Д-2: полоса «40×30 мм · …» растянута в пустой блок ~215 px): прокручиваемая рамка и предельная высота — только у ОТКРЫТЫХ настроек во вкладке;
    /// свёрнутые — обычная одна строка по содержимому.</summary>
    private void ApplySettingsContainer()
    {
        if (!_embedded)
            return;
        var open = _settingsBody.IsVisible;
        if (open && !ReferenceEquals(_settingsCard.Child, _settingsScroll))
        {
            _settingsCard.Child = null;
            _settingsScroll.Content = _settingsGrid;
            _settingsCard.Child = _settingsScroll;
        }
        else if (!open && !ReferenceEquals(_settingsCard.Child, _settingsGrid))
        {
            _settingsCard.Child = null;
            _settingsScroll.Content = null;
            _settingsCard.Child = _settingsGrid;
        }
        _settingsCard.MaxHeight = open ? _settingsMaxHeight : double.PositiveInfinity;
    }

    private void SetSettingsOpen(bool open, bool initial = false)
    {
        _settingsBody.IsVisible = open;
        _previewCard.IsVisible = open;
        _settingsSummary.IsVisible = !open;
        ApplySettingsContainer();
        UpdateToggleText();
        _customSizePanel.IsVisible = _sizeCombo.SelectedIndex == SizePresets.Length;
    }

    private void UpdateToggleText()
    {
        var open = _settingsBody.IsVisible;
        var text = (open ? "▴  " : "▾  ") + (_compact ? T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar")
            : T("Настройки этикетки", "Этикетка жөндөөлөрү", "Label settings", "Etiket ayarları", "Yorliq sozlamalari"));
        if (!Equals(_settingsToggle.Content, text))
            _settingsToggle.Content = text;
    }

    /// <summary>Подписи кнопок выбора: на узком окне (1024) — короткие, на широком — как на сайте.</summary>
    private string? _textsKey;

    private void ApplyTexts()
    {
        UpdateToggleText();
        UpdateSelectionUi();
        var n = _found.Count.ToString("N0");
        // 2026-11-11: подписи и подсказки переписываются, только если режим или число найденных изменились.
        var key = (_compact ? "c" : "w") + n;
        if (key == _textsKey)
            return;
        _textsKey = key;
        _selectPageButton.Content = _compact ? T("На странице", "Баракта", "On page", "Sayfada", "Sahifada")
            : T("Выбрать все на странице", "Баракта бардыгын тандоо", "Select all on page", "Sayfadakilerin tümünü seç", "Sahifadagilarni tanlash");
        _selectFoundButton.Content = _compact ? T($"Все найденные ({n})", $"Баары ({n})", $"All found ({n})", $"Tümü ({n})", $"Hammasi ({n})")
            : T($"Выбрать все найденные ({n})", $"Табылгандардын бардыгын тандоо ({n})", $"Select all found ({n})", $"Bulunanların tümünü seç ({n})", $"Topilganlarning barchasini tanlash ({n})");
        _clearButton.Content = _compact ? T("Снять", "Алуу", "Clear", "Kaldır", "Bekor") : T("Снять выбор", "Тандоону алып салуу", "Clear selection", "Seçimi kaldır", "Tanlovni bekor qilish");
        ToolTip.SetTip(_selectPageButton, T("Выбрать все на странице", "Баракта бардыгын тандоо", "Select all on page", "Sayfadakilerin tümünü seç", "Sahifadagilarni tanlash"));
        ToolTip.SetTip(_selectFoundButton, T("Выбрать все найденные", "Табылгандардын бардыгын тандоо", "Select all found", "Bulunanların tümünü seç", "Topilganlarning barchasini tanlash"));
    }

    private void UpdateSummary()
    {
        var parts = new List<string> { $"{_template.WidthMm:0.#}×{_template.HeightMm:0.#} " + T("мм", "мм", "mm", "mm", "mm") };
        var shown = new List<string>();
        if (_template.Barcode.Enabled) shown.Add(T("штрих-код", "штрих-код", "barcode", "barkod", "shtrix-kod"));
        if (_template.ProductName.Enabled) shown.Add(T("название", "аталышы", "name", "ad", "nomi"));
        if (_template.Price.Enabled) shown.Add(T("цена", "баасы", "price", "fiyat", "narx"));
        parts.Add(string.Join(", ", shown));
        parts.Add(_settings.CopiesMode == "stock"
            ? T("копий = остаток", "көчүрмө = калдык", "copies = stock", "kopya = stok", "nusxa = qoldiq")
            : T($"копий {_settings.DefaultCopies}", $"көчүрмө {_settings.DefaultCopies}", $"copies {_settings.DefaultCopies}", $"kopya {_settings.DefaultCopies}", $"nusxa {_settings.DefaultCopies}"));
        parts.Add(_printerCombo.SelectedItem is DiscoveredPrinter pr ? pr.Label : T("принтер не подключён", "принтер туташкан эмес", "no printer", "yazıcı bağlı değil", "printer ulanmagan"));
        _settingsSummary.Text = string.Join(" · ", parts.Where(s => s.Length > 0));
    }

    private void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_init || _sizeCombo.SelectedIndex < 0)
            return;
        var custom = _sizeCombo.SelectedIndex == SizePresets.Length;
        _customSizePanel.IsVisible = custom;
        if (custom)
        {
            _widthBox.Value = (decimal)_template.WidthMm;
            _heightBox.Value = (decimal)_template.HeightMm;
            return;
        }
        var (w, h) = SizePresets[_sizeCombo.SelectedIndex];
        ApplySize(w, h);
    }

    private void CustomSize_Changed()
    {
        if (_init || _sizeCombo.SelectedIndex != SizePresets.Length)
            return;
        ApplySize((double)(_widthBox.Value ?? 40m), (double)(_heightBox.Value ?? 30m));
    }

    private void ApplySize(double w, double h)
    {
        w = Math.Clamp(w, 15, 150);
        h = Math.Clamp(h, 10, 150);
        if (Math.Abs(_template.WidthMm - w) < 0.01 && Math.Abs(_template.HeightMm - h) < 0.01)
            return;
        // Ручной размер шрифта (если задан в редакторе) растёт и убывает вместе с этикеткой — иначе на малой этикетке он не помещается.
        // 2026-11-08: среднее геометрическое вместо меньшего коэффициента — при наборе «Своего размера» по цифрам (40 → 15 → 58) шрифт
        // уменьшался на первом шаге и больше не возвращался (меньший коэффициент второго шага = 1).
        var k = Math.Sqrt(w / Math.Max(_template.WidthMm, 1) * (h / Math.Max(_template.HeightMm, 1)));
        _template.WidthMm = w;
        _template.HeightMm = h;
        foreach (var e in new[] { _template.ProductName, _template.Price, _template.Sku, _template.Unit, _template.StoreName, _template.Variant })
            if (e.FontSizePx is > 0)
                e.FontSizePx = Math.Max(4, Math.Round(e.FontSizePx.Value * k, 1));
        if (_template.FontSizePx > 0)
            _template.FontSizePx = Math.Max(4, Math.Round(_template.FontSizePx * k, 1));
        Relayout();
    }

    /// <summary>2026-11-01 (П-4): число копий «по умолчанию» для товара: общее или остаток товара (режим «= остаток»).</summary>
    private int BaseCopies(CatalogProductTileVm p) =>
        _settings.CopiesMode == "stock" ? BarcodePrintCatalog.StockCopies(p, out _) : _settings.DefaultCopies;

    private int CopiesOf(CatalogProductTileVm p, Sel st) => Math.Clamp(st.Copies ?? BaseCopies(p), 1, 99);

    private void CopiesMode_Changed()
    {
        if (_init)
            return;
        _settings.CopiesMode = _copiesModeCombo.SelectedIndex == 1 ? "stock" : "default";
        _copiesBox.IsEnabled = _settings.CopiesMode != "stock";
        BarcodePrintSettingsStore.Save(_settings);
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        UpdateSelectionUi();
        UpdateSummary();
    }

    private void ResetItemCopies()
    {
        foreach (var s in _sel.Values)
            s.Copies = null;
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        UpdateSelectionUi();
    }

    private void DefaultCopies_Changed()
    {
        if (_init)
            return;
        _settings.DefaultCopies = (int)(_copiesBox.Value ?? 1);
        BarcodePrintSettingsStore.Save(_settings);
        // Карточки без своего числа копий показывают новое общее значение.
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        UpdateSelectionUi();
        UpdateSummary();
    }

    private void FlagChanged(Action apply, bool relayout, bool barcodeToggled = false)
    {
        if (_init)
            return;
        apply();
        if (relayout)
            Relayout(); // перерисует и страницу, и предпросмотр
        else
            TemplateChanged();
        if (barcodeToggled)
        {
            // Штрих-код включили: у выбранных товаров без штрихкода печатать нечего — выбор снимается; выключили — их карточки снова доступны.
            var dropped = 0;
            if (_template.Barcode.Enabled)
                foreach (var p in CatalogCacheService.Products)
                    if (_sel.TryGetValue(p.Id, out var st) && st.Selected && !BarcodePrintCatalog.HasPrintableCode(p)) { SetSelected(st, false); dropped++; }
            RenderPage();
            UpdateSelectionUi();
            if (dropped > 0)
                SetStatus(T($"Снят выбор у {dropped} товаров без штрихкода.", $"Штрих-коду жок {dropped} товардын тандоосу алынды.", $"Deselected {dropped} products without a barcode.", $"Barkodsuz {dropped} ürünün seçimi kaldırıldı.", $"Shtrix-kodsiz {dropped} ta mahsulot tanlovi bekor qilindi."));
        }
    }

    private void Relayout()
    {
        var eff = LabelAutoLayout.Apply(_template, _barPx);
        _barHeightHint.IsVisible = _template.Barcode.Enabled && eff < _barPx - 1;
        var effMm = LabelAutoLayout.PxToMm(eff).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        _barHeightHint.Text = T($"Фактически {effMm} мм: этикетка низкая — остальное заняло место.", $"Иш жүзүндө {effMm} мм: этикетка аз бийик.", $"Actually {effMm} mm: the label is short.", $"Gerçekte {effMm} mm: etiket alçak.", $"Aslida {effMm} mm: yorliq past.");
        UpdateBarDots();
        TemplateChanged();
    }

    private static decimal BarMmFromPx(int px) =>
        Math.Clamp((decimal)Math.Round(LabelAutoLayout.PxToMm(px) * 2) / 2, 4m, 80m);

    private int BarPxFromBox() => LabelAutoLayout.MmToPx((double)(_barHeightBox.Value ?? BarMmFromPx(_barPx)));

    /// <summary>«≈ N точек 203 dpi» — сколько точек займёт высота штрихкода на принтере 203 dpi.</summary>
    private void UpdateBarDots()
    {
        var mm = (double)(_barHeightBox.Value ?? BarMmFromPx(_barPx));
        var dots = (int)Math.Round(mm * 203 / 25.4);
        _barDotsHint.Text = T($"≈ {dots} точек 203 dpi", $"≈ {dots} чекит 203 dpi", $"≈ {dots} dots at 203 dpi", $"≈ {dots} nokta 203 dpi", $"≈ {dots} nuqta 203 dpi");
    }

    private void TemplateChanged()
    {
        foreach (var s in _warnSync)
            s(); // П-2: значки ⚠ зависят от размера и формата штрихкода
        _templateDirty = true;
        UpdateSummary();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshPreview()
    {
        try
        {
            CatalogProductTileVm? sample = null;
            foreach (var p in CatalogCacheService.Products)
                if (_sel.TryGetValue(p.Id, out var st) && st.Selected) { sample = p; break; }
            sample ??= _found.FirstOrDefault(BarcodePrintCatalog.HasBarcode) ?? _found.FirstOrDefault();
            var name = sample?.Title ?? T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi");
            var code = "4870145004807";
            var previewTemplate = _template;
            if (sample != null)
            {
                var ch = BarcodePrintCatalog.Choices(sample);
                if (ch.Count > 0)
                {
                    var picked = ch[Math.Clamp(GetSel(sample.Id).BarcodeIndex, 0, ch.Count - 1)];
                    code = picked.Code;
                    if (picked.IsSize && CodeMode != LabelCodeMode.AsTemplate)
                        previewTemplate = LabelCodeModes.Apply(_template, CodeMode); // 2026-11-06: размер — в выбранном формате кода
                }
            }
            var price = sample != null ? BarcodePrintCatalog.LabelPrice(sample) : "85,00 сом";
            // 2026-10-29: Android/Linux — System.Drawing недоступен, картинку рисует Avalonia (LabelSceneRenderer).
            var next = LabelSceneRenderer.LabelPreview(name, code, price, previewTemplate, sample?.Article, sample?.Unit, UserPreferences.Instance.StoreName);
            _previewImage.Source = next;
            _previewBitmap?.Dispose();
            _previewBitmap = next;
            _previewCaption.Text = sample != null ? name : T("Образец", "Үлгү", "Sample", "Örnek", "Namuna");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: preview failed: {ex}", "WARNING");
        }
    }

    private async Task OpenEditorAsync()
    {
        if (!BarcodePrintAccess.Ensure(Host)) // 2026-11-01 (П-10)
            return;
        // 2026-10-27 (аудит редакторов, Б-2): редактор этикеток — отдельная покупка, из массовой печати он открывался без проверки.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.LabelEditor, Host))
            return;
        SaveState();
        var sample = BuildItems().FirstOrDefault() ?? (_found.Count > 0 ? new BarcodePrintItem(_found[0], BarcodePrintCatalog.Choices(_found[0]).FirstOrDefault()?.Code, 1) : null);
        var editor = new LabelTemplateEditorDialog(_template, sample?.Product.Title ?? T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"),
            sample?.Barcode ?? "4870145004807", sample != null ? BarcodePrintCatalog.LabelPrice(sample.Product) : "85,00 сом",
            LabelTemplateStore.Save); // со своим saveAction — кнопки «Печать штрихкодов…» в редакторе нет (мы уже в этом окне)
        if (Host is not { } editorOwner) // 2026-11-12 (Л-27)
            return;
        await editor.ShowDialog(editorOwner).ConfigureAwait(true);
        if (!editor.Saved)
            return;
        _template = LabelTemplateStore.Load();
        _templateDirty = false;
        _init = true;
        _showName.IsChecked = _template.ProductName.Enabled;
        _showPrice.IsChecked = _template.Price.Enabled;
        _showBarcode.IsChecked = _template.Barcode.Enabled;
        _showDigits.IsChecked = _template.BarcodeShowDigits;
        _showSku.IsChecked = _template.Sku.Enabled;
        _showUnit.IsChecked = _template.Unit.Enabled;
        _showStore.IsChecked = _template.StoreName.Enabled;
        _barPx = LabelAutoLayout.BarcodeHeightPx(_template);
        _barHeightBox.Value = BarMmFromPx(_barPx);
        _barHeightHint.IsVisible = false;
        _thicknessCombo.SelectedIndex = Math.Clamp(_template.BarcodeModuleDots, 0, 4);
        _sizeCombo.SelectedIndex = FindPresetIndex();
        _customSizePanel.IsVisible = _sizeCombo.SelectedIndex == SizePresets.Length;
        _widthBox.Value = (decimal)_template.WidthMm;
        _heightBox.Value = (decimal)_template.HeightMm;
        _init = false;
        UpdateSummary();
        RenderPage();
        RefreshPreview();
    }

    /// <summary>Шаблон этикетки и настройки — на диск, если менялись.</summary>
    internal void SaveState()
    {
        if (_templateDirty)
        {
            LabelTemplateStore.Save(_template);
            _templateDirty = false;
        }
        // 2026-11-11: во вкладке/разделе поиск, фильтр и страница не запоминаются между запусками (вкладка открывалась с забытым поиском — «всего 1 товар»).
        if (_onlyIds == null && !_embedded)
        {
            // 2026-11-01 (П-9): запоминаем поиск, фильтр, категорию и страницу (не копии и не выбор).
            var q = _search.Text ?? "";
            _settings.LastSearch = q.Length > 200 ? q[..200] : q;
            _settings.LastFilter = (int)_filter;
            _settings.LastCategory = _category;
            _settings.LastPage = _page;
        }
        BarcodePrintSettingsStore.Save(_settings);
    }

    // ------------------------------------------------------------------ принтер

    private void PrinterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 2026-10-27 (приёмка 26.10): PDF/XPS/OneNote/факс как принтер этикеток в настройках не запоминаем
        if (_printerCombo.SelectedItem is DiscoveredPrinter p && !_init && !p.IsVirtual
            && !LabelPrinterSelector.IsSameAsReceiptPrinter(p.DevicePath)) // 2026-11-05 (аудит №3, С2-29): принтер чеков не запоминаем как принтер этикеток (после этого «одна кнопка» слала бы этикетки на чековый); в этом окне печать на нём остаётся доступной.
        {
            UserPreferences.Instance.LabelPrinterDevicePath = p.DevicePath;
            UserPreferences.Instance.SaveToDisk();
        }
        _live = null;
        UpdatePrinterChip();
        UpdateSummary();
        if (!_init)
            _ = RefreshLiveStatusAsync();
    }

}
