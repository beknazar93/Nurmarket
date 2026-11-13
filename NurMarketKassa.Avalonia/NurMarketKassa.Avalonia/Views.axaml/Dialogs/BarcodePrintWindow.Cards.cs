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

// 2026-11-06: В15г — часть класса BarcodePrintView (Cards): перенесено из BarcodePrintWindow.cs без изменений логики.
public partial class BarcodePrintView
{
    // ------------------------------------------------------------------ стили и мелкие помощники

    private void AddStyles()
    {
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ComboBox>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ComboBox.CornerRadiusProperty, new CornerRadius(8)),
                new Avalonia.Styling.Setter(MinHeightProperty, 44.0),
                new Avalonia.Styling.Setter(ComboBox.BorderThicknessProperty, new Thickness(1.5)),
            },
        });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<NumericUpDown>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(NumericUpDown.CornerRadiusProperty, new CornerRadius(8)),
                new Avalonia.Styling.Setter(MinHeightProperty, 44.0),
                new Avalonia.Styling.Setter(NumericUpDown.BorderThicknessProperty, new Thickness(1.5)),
            },
        });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<CheckBox>())
        {
            Setters = { new Avalonia.Styling.Setter(MinHeightProperty, 44.0) },
        });
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    private PathIcon Icon(string key, double size, double l = 0, double t = 0, double r = 0, double b = 0)
    {
        var icon = MarketplaceIcons.Icon(key, size);
        icon.Margin = new Thickness(l, t, r, b);
        Use(icon, TemplatedControl.ForegroundProperty, "BrushTextSoft");
        return icon;
    }

    private static Button Ghost(string text)
    {
        var b = new Button
        {
            Content = text, MinHeight = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), FontSize = 13.5,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Classes.Add("sec-outline");
        return b;
    }

    private static object ButtonWithIcon(string iconKey, string text)
    {
        var icon = MarketplaceIcons.Icon(iconKey, 16);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, double width) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = 1, FormatString = "0", Width = width, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private CheckBox Check(string text, bool isChecked) => new() { Content = text, IsChecked = isChecked, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };

    private StackPanel Inline(string caption, Control control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 14, 6), VerticalAlignment = VerticalAlignment.Center };
        if (caption.Length > 0)
        {
            var label = new TextBlock { Text = caption, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
            panel.Children.Add(label);
        }
        panel.Children.Add(control);
        return panel;
    }

    private StackPanel Field(string caption, Control control)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 14, 6) };
        var label = new TextBlock { Text = caption.Length > 0 ? caption : " ", FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private void FillCategories()
    {
        _categoryCombo.Items.Clear();
        _categoryCombo.Items.Add(T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"));
        foreach (var c in BarcodePrintCatalog.Categories(CatalogCacheService.Products))
            _categoryCombo.Items.Add(c);
        _categoryCombo.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ поиск и страницы

    /// <summary>Текст строки поиска (для стенда и вызова из кода).</summary>
    public string SearchText
    {
        get => _search.Text ?? "";
        set { _search.Text = value; _searchTimer.Stop(); ApplyFilter(); }
    }

    /// <summary>Сколько товаров найдено по текущему поиску и фильтрам.</summary>
    public int FoundCount => _found.Count;

    public int SelectedCount => _selectedCount;

    public int PageNumber => _page + 1;

    private int PageCount => Math.Max(1, (_found.Count + PageSize - 1) / PageSize);

    private void ApplyFilter(bool keepPage = false)
    {
        var found = BarcodePrintCatalog.Search(CatalogCacheService.Products, _search.Text, _filter, _category);
        if (_onlyIds != null)
        {
            // 2026-11-01 (П-4): окно открыто с готовым выбором — пока поиск и фильтры пусты, показываем только эти товары.
            if (string.IsNullOrWhiteSpace(_search.Text) && _filter == BarcodeFilter.All && _category == null)
                found = found.Where(p => _onlyIds.Contains(p.Id)).ToList();
            else
                _onlyIds = null;
        }
        if (_filter == BarcodeFilter.WithProblems)
        {
            _ = ApplyProblemFilterAsync(found);
            return;
        }
        _filterVersion++;
        _found = found;
        _page = keepPage ? Math.Min(_page, PageCount - 1) : 0; // 2026-11-11: возврат на вкладку «Штрихкоды» не сбрасывает страницу
        RenderPage();
        UpdateSelectionUi();
    }

    /// <summary>2026-11-01 (П-2): фильтр «С замечаниями» считается в фоне (на 15 000 товаров интерфейс не замирает).</summary>
    private async Task ApplyProblemFilterAsync(List<CatalogProductTileVm> source)
    {
        var version = ++_filterVersion;
        var template = _template.Clone();
        SetStatus(T("Проверяю штрихкоды…", "Штрих-коддорду текшерип жатам…", "Checking barcodes…", "Barkodlar kontrol ediliyor…", "Shtrix-kodlar tekshirilmoqda…"));
        List<CatalogProductTileVm> list;
        try
        {
            list = await Task.Run(() => source.Where(p =>
            {
                var ch = BarcodePrintCatalog.Choices(p);
                return ch.Count > 0 && ProblemOf(ch[0].Code, template) != null;
            }).ToList()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: problem filter failed: {ex.GetType().Name}", "WARNING");
            list = new List<CatalogProductTileVm>();
        }
        if (version != _filterVersion)
            return; // за время проверки поиск или фильтр поменяли
        _found = list;
        _page = 0;
        RenderPage();
        UpdateSelectionUi();
        SetStatus(T($"С замечаниями: {list.Count}", $"Эскертүү менен: {list.Count}", $"With warnings: {list.Count}", $"Uyarılı: {list.Count}", $"Eslatmali: {list.Count}"));
    }

    /// <summary>2026-11-01 (П-9): поиск, фильтр, категория и страница — как были при закрытии окна. Копии и выбор не запоминаются.</summary>
    private void RestoreLast()
    {
        _restoring = true;
        try
        {
            _search.Text = _settings.LastSearch ?? "";
            if (Enum.IsDefined(typeof(BarcodeFilter), _settings.LastFilter))
                _filter = (BarcodeFilter)_settings.LastFilter;
            foreach (var kv in _filterButtons)
                UiKit.SetActive(kv.Value, kv.Key == _filter);
            if (!string.IsNullOrWhiteSpace(_settings.LastCategory))
            {
                var idx = _categoryCombo.Items.IndexOf(_settings.LastCategory); // категории больше нет — «Все категории»
                if (idx > 0)
                {
                    _category = _settings.LastCategory;
                    _categoryCombo.SelectedIndex = idx;
                }
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>2026-11-01 (П-4): отметить переданные товары и копии, показать только их.</summary>
    internal void ApplyPreset(IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items)
    {
        foreach (var s in _sel.Values)
            s.Selected = false;
        _selectedCount = 0;
        var byId = new Dictionary<string, CatalogProductTileVm>(StringComparer.Ordinal);
        foreach (var p in CatalogCacheService.Products)
            byId[p.Id] = p;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int missing = 0, noCode = 0, capped = 0;
        foreach (var it in items)
        {
            if (!byId.TryGetValue(it.ProductId ?? "", out var p))
            {
                missing++;
                continue;
            }
            if (!IsPrintable(p))
            {
                noCode++;
                continue;
            }
            var st = GetSel(p.Id);
            var choices = BarcodePrintCatalog.Choices(p);
            var idx = string.IsNullOrWhiteSpace(it.Barcode) ? -1 : choices.FindIndex(c => string.Equals(c.Code, it.Barcode.Trim(), StringComparison.OrdinalIgnoreCase));
            st.BarcodeIndex = Math.Max(idx, 0);
            st.Copies = it.Copies > 0 ? Math.Clamp(it.Copies, 1, 99) : null; // 2026-11-11: 0 — «по умолчанию» (выбранные товары из вкладки «Товары»)
            if (it.Copies > 99)
                capped++;
            SetSelected(st, true);
            ids.Add(p.Id);
        }
        _onlyIds = ids;
        var notes = new List<string>();
        if (missing > 0)
            notes.Add(T($"{missing} товаров нет в каталоге кассы — обновите Склад.", $"{missing} товар кассанын каталогунда жок — Кампаны жаңыртыңыз.", $"{missing} products are not in the register catalog — refresh the Warehouse.", $"{missing} ürün kasa kataloğunda yok — Depoyu yenileyin.", $"{missing} ta mahsulot kassa katalogida yo'q — Omborni yangilang."));
        if (noCode > 0)
            notes.Add(T($"У {noCode} товаров нет штрихкода.", $"{noCode} товардо штрих-код жок.", $"{noCode} products have no barcode.", $"{noCode} ürünün barkodu yok.", $"{noCode} ta mahsulotda shtrix-kod yo'q."));
        if (capped > 0)
            notes.Add(T($"У {capped} товаров копий больше 99 — напечатано по 99.", $"{capped} товарда көчүрмө 99дан көп — 99дан басылат.", $"{capped} products need more than 99 copies — 99 each will be printed.", $"{capped} üründe 99'dan fazla kopya var — 99'ar basılacak.", $"{capped} ta mahsulotda nusxa 99 dan ko'p — 99 tadan chop etiladi."));
        _presetNote = notes.Count == 0 ? null : string.Join(" ", notes);
        ApplyFilter();
        _status.Text = _presetNote ?? "";
    }

    public void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == _page)
            return;
        _page = page;
        RenderPage();
    }

    private void RenderPage()
    {
        _cardsPanel.Children.Clear();
        _cards.Clear();
        _pageSync.Clear();
        _copiesSync.Clear();
        _warnSync.Clear();
        _codeSync.Clear();
        var start = _page * PageSize;
        var end = Math.Min(start + PageSize, _found.Count);
        // 2026-10-31 (стресс-тест 1.17.68): 48 карточек разом — окно открывалось 0,6–0,8 с. Первые 12 строим сразу, остальные — пакетами по 12 между кадрами.
        var gen = ++_renderGen;
        var firstEnd = Math.Min(start + CardBatch, end);
        for (var i = start; i < firstEnd; i++)
        {
            var card = BuildCard(_found[i]);
            _cards.Add(card);
            _cardsPanel.Children.Add(card);
        }
        if (firstEnd < end)
            Dispatcher.UIThread.Post(() => AddCardBatch(gen, firstEnd, end), DispatcherPriority.Background);
        LayoutCards(_scroll.Bounds.Width > 0 ? _scroll.Bounds.Width : FallbackWidth);
        _scroll.Offset = default;

        var total = CatalogCacheService.Products.Count;
        _emptyText.IsVisible = _found.Count == 0;
        if (_found.Count == 0)
            _emptyText.Text = total == 0
                ? T("Каталог товаров ещё не загружен — откройте Склад и нажмите «Обновить».", "Товарлар каталогу али жүктөлө элек — Кампаны ачып, «Жаңыртуу» баскычын басыңыз.", "The product catalog is not loaded yet — open the Warehouse and press “Refresh”.", "Ürün kataloğu henüz yüklenmedi — Depoyu açıp «Yenile»ye basın.", "Mahsulotlar katalogi hali yuklanmagan — Omborni ochib, «Yangilash» tugmasini bosing.")
                : T("Ничего не найдено. Измените поиск или фильтр.", "Эч нерсе табылган жок. Издөөнү же чыпканы өзгөртүңүз.", "Nothing found. Change the search or the filter.", "Hiçbir şey bulunamadı. Aramayı veya filtreyi değiştirin.", "Hech narsa topilmadi. Qidiruv yoki filtrni o'zgartiring.");
        var from = _found.Count == 0 ? 0 : start + 1;
        _pagerText.Text = T($"Стр. {_page + 1} из {PageCount} · {from}–{end} из {_found.Count:N0}".Replace(',', ' '), $"{_page + 1}/{PageCount}-бет · {from}–{end} / {_found.Count:N0}".Replace(',', ' '),
            $"Page {_page + 1} of {PageCount} · {from}–{end} of {_found.Count:N0}", $"Sayfa {_page + 1}/{PageCount} · {from}–{end} / {_found.Count:N0}".Replace(',', ' '), $"{_page + 1}/{PageCount}-sahifa · {from}–{end} / {_found.Count:N0}".Replace(',', ' '));
        _prevButton.IsEnabled = _page > 0;
        _nextButton.IsEnabled = _page < PageCount - 1;
        ApplyTexts();
    }

    // 2026-11-11: во вкладке пакеты по 8 (в окне по 12): первые карточки появляются быстрее, поток интерфейса отдаётся чаще.
    private int CardBatch => _embedded ? 8 : 12;
    private int _renderGen;

    /// <summary>Следующий пакет карточек страницы; страница сменилась (или окно закрыто) — пакет бросается.</summary>
    private void AddCardBatch(int gen, int from, int end)
    {
        if (gen != _renderGen)
            return;
        var to = Math.Min(from + CardBatch, end);
        for (var i = from; i < to; i++)
        {
            var card = BuildCard(_found[i]);
            _cards.Add(card);
            _cardsPanel.Children.Add(card);
        }
        LayoutCards(_lastWidth > 0 ? _lastWidth : (_scroll.Bounds.Width > 0 ? _scroll.Bounds.Width : FallbackWidth));
        if (to < end)
            Dispatcher.UIThread.Post(() => AddCardBatch(gen, to, end), DispatcherPriority.Background);
    }

    private double _layoutWidth, _cardWidth;

    private void LayoutCards(double width)
    {
        if (width <= 0)
            return;
        _lastWidth = width;
        const double gap = 10;
        // 2026-11-11 (видео «у.mp4»: рабочая область пульсирует при прокрутке): колонки пересчитываются, только если список стал уже или шире больше чем на 24 px
        // (раньше — на каждое изменение ширины в 1–2 px, из-за чего число колонок и ширина карточек «ходили»); новым карточкам достаётся прежняя ширина.
        if (_layoutWidth <= 0 || width < _layoutWidth || width - _layoutWidth >= 24)
        {
            var usable = Math.Max(width - 18, 200); // 2026-11-11 («срез»): запас под полосу прокрутки 18 (было 14); ширина — фактическая ширина списка после раскладки (WidthFreeHost), не область просмотра: та могла быть устаревшей
            // 2026-11-11: во вкладке «Штрихкоды» карточка не уже 290 (в окне — прежние 250): в узкой карточке подпись «В печать» / «Басууга» рядом с «Копий» рвалась по буквам (1366: 5 карточек по 247).
            var cols = Math.Max(1, (int)((usable + gap) / ((_embedded ? 290 : 250) + gap)));
            _cardWidth = Math.Max(Math.Floor((usable - gap * (cols - 1)) / cols) - 1, 180);
            _layoutWidth = width;
        }
        var changed = false;
        foreach (var c in _cards)
        {
            if (double.IsNaN(c.Width) || Math.Abs(c.Width - _cardWidth) > 0.01) // у новой карточки Width = NaN
            {
                if (!double.IsNaN(c.Width))
                    changed = true; // у уже разложенной карточки ширина поменялась (у новой — первая раскладка, перемерка не нужна)
                c.Width = _cardWidth;
            }
            if (c.Margin.Right != gap)
                c.Margin = new Thickness(0, 0, gap, gap);
        }
        // 2026-11-13: ширина карточек меняется внутри раскладки (SizeChanged списка) — после смены масштаба 90→100→90 WrapPanel держал прежние желаемые размеры (слот 323+10 при карточке 363: наложение и срез слева);
        // перемерка панели заставляет её переразложить ряды по новой ширине; отложена (Background): синхронная, прямо в раскладке, на первом показе вкладки раскладывала панель инструментов шире вкладки.
        if (changed && !_remeasureQueued)
        {
            _remeasureQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _remeasureQueued = false;
                _cardsPanel.InvalidateMeasure();
            }, DispatcherPriority.Background);
        }
    }

    private bool _remeasureQueued;

    // ------------------------------------------------------------------ карточка товара

    private Sel GetSel(string id)
    {
        if (!_sel.TryGetValue(id, out var s))
            _sel[id] = s = new Sel();
        return s;
    }

    private bool IsPrintable(CatalogProductTileVm p) => !_template.Barcode.Enabled || BarcodePrintCatalog.HasPrintableCode(p);

    // ---- П-2: замечания по штрихкоду до печати ----

    // 2026-11-01 (контрольный аудит, С2-22): в ключ кэша замечаний добавлен «штрихкод включён/выключен» — иначе после включения блока старые «замечаний нет» молчали.
    private static string TemplateSig(LabelTemplate t) =>
        $"{t.Barcode.Enabled}|{t.BarcodeFormat}|{t.WidthMm:0.##}x{t.HeightMm:0.##}|{t.Barcode.XMm:0.##},{t.Barcode.WidthMm:0.##}x{t.Barcode.HeightMm:0.##}|{t.BarcodeMargin}|{t.BarcodeModuleDots}|{t.BarcodeShowDigits}";

    /// <summary>Замечание по штрихкоду для шаблона (кэш по «код + размеры блока + формат»); null — замечаний нет.</summary>
    private string? ProblemOf(string? code, LabelTemplate template)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var key = TemplateSig(template) + "#" + code;
        lock (_problemCache)
        {
            if (_problemCache.TryGetValue(key, out var cached))
                return cached;
        }
        var value = BarcodeLabelService.DescribeBarcodeProblem(code, template);
        lock (_problemCache)
        {
            if (_problemCache.Count > 60000)
                _problemCache.Clear();
            _problemCache[key] = value;
        }
        return value;
    }

    /// <summary>Перед печатью: у каких выбранных есть замечания по штрихкоду. «Печатать все» / «Снять их с печати» / «Отмена» (null — отмена).</summary>
    private async Task<List<BarcodePrintItem>?> ResolveProblemsAsync(List<BarcodePrintItem> items)
    {
        var template = _template.Clone();
        var bad = items.Where(i => i.Barcode != null && ProblemOf(i.Barcode, template) != null).ToList();
        if (bad.Count == 0 || !template.Barcode.Enabled)
            return items;
        var q1 = T($"У {bad.Count} товаров замечания по штрихкоду (неверный EAN — печать Code 128, кириллица, слишком длинный или плотный код). Печатать все?",
            $"{bad.Count} товардо штрих-код боюнча эскертүүлөр бар (туура эмес EAN — Code 128 басылат, кирилл, өтө узун же тыгыз код). Баарын басасызбы?",
            $"{bad.Count} products have barcode warnings (invalid EAN is printed as Code 128, Cyrillic, too long or too dense code). Print all?",
            $"{bad.Count} üründe barkod uyarısı var (geçersiz EAN Code 128 olarak basılır, Kiril, çok uzun veya çok sık kod). Hepsi yazdırılsın mı?",
            $"{bad.Count} ta mahsulotda shtrix-kod bo'yicha eslatma bor (noto'g'ri EAN Code 128 sifatida chop etiladi, kirill, juda uzun yoki zich kod). Hammasi chop etilsinmi?");
        var title = T("Замечания по штрихкоду", "Штрих-код боюнча эскертүүлөр", "Barcode warnings", "Barkod uyarıları", "Shtrix-kod eslatmalari");
        if (await AskAsync(title, q1, T("Печатать все", "Баарын басуу", "Print all", "Hepsini yazdır", "Hammasini chop etish"), T("Другое…", "Башка…", "Other…", "Diğer…", "Boshqa…")).ConfigureAwait(true))
            return items;
        var q2 = T($"Снять эти {bad.Count} товаров с печати и напечатать остальные?", $"Бул {bad.Count} товарды басуудан алып, калгандарын басасызбы?", $"Take these {bad.Count} products off the print and print the rest?",
            $"Bu {bad.Count} ürünü yazdırmadan çıkarıp kalanları yazdıralım mı?", $"Shu {bad.Count} ta mahsulotni chopdan olib tashlab, qolganlarini chop etamizmi?");
        if (!await AskAsync(title, q2, T("Снять их и печатать", "Алып салып басуу", "Remove them and print", "Çıkar ve yazdır", "Olib tashlab chop etish"), T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true))
            return null;
        var badIds = new HashSet<string>(bad.Select(b => b.Product.Id), StringComparer.Ordinal);
        foreach (var id in badIds)
            SetSelected(GetSel(id), false);
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
        return items.Where(i => !badIds.Contains(i.Product.Id)).ToList();
    }

    private async Task<bool> AskAsync(string title, string question, string ok, string cancel) =>
        ConfirmOverride != null
            ? await ConfirmOverride(question).ConfigureAwait(true)
            : await PosConfirmDialog.ShowModalAsync(Host, title, question, ok, cancel).ConfigureAwait(true);

    private void SetSelected(Sel st, bool value)
    {
        if (st.Selected == value)
            return;
        st.Selected = value;
        _selectedCount += value ? 1 : -1;
    }

    private Border BuildCard(CatalogProductTileVm p)
    {
        var choices = BarcodePrintCatalog.Choices(p);
        var st = GetSel(p.Id);
        if (st.BarcodeIndex >= choices.Count)
            st.BarcodeIndex = 0;
        var printable = IsPrintable(p);
        if (!printable && st.Selected)
            SetSelected(st, false);

        var check = new CheckBox { Content = T("В печать", "Басууга", "Print", "Yazdır", "Chopga"), IsChecked = st.Selected, IsEnabled = printable, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        // 2026-11-11 («баг в разделе штрихкоды», 15 000 товаров): NumericUpDown — самый тяжёлый элемент карточки (~14 мс на карточку, 48 карточек ≈ 0,7 с на каждый поиск и страницу).
        // Пока карточку не выбрали и в поле не нажали, вместо него лёгкая «коробка» с числом того же размера; настоящее поле появляется при выборе, наведении, нажатии или Tab.
        NumericUpDown? copies = null;
        var copiesText = new TextBlock { Text = CopiesOf(p, st).ToString(), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var copiesBox = new Border { Width = 118, MinHeight = 44, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Child = copiesText, Focusable = true, IsTabStop = true, Opacity = printable ? 1 : 0.5 };
        Use(copiesBox, Border.BackgroundProperty, "BrushPanel");
        Use(copiesBox, Border.BorderBrushProperty, "BrushInputBorder");
        Use(copiesText, TextBlock.ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(copiesBox, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
        var copiesHost = new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = copiesBox };
        var copiesLabel = new TextBlock { Text = T("Копий:", "Көчүрмө:", "Copies:", "Kopya:", "Nusxa:"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) }; // 2026-11-12 (Д-17): отступ слева
        Use(copiesLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        top.Children.Add(check);
        Grid.SetColumn(copiesLabel, 1);
        top.Children.Add(copiesLabel);
        Grid.SetColumn(copiesHost, 2);
        top.Children.Add(copiesHost);

        var caption = new TextBlock { Text = T("ШТРИХ-КОД", "ШТРИХ-КОД", "BARCODE", "BARKOD", "SHTRIX-KOD"), FontSize = 12, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        Control codeControl;
        if (choices.Count > 1)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), IsEnabled = printable };
            // 2026-11-01 (П-3): «(основной)», «(доп.) Клубничный», «(упаковка ×10)», «(размер) 44, Красный»
            foreach (var c in choices)
                combo.Items.Add(c.Code + "  " + (c.IsMain ? T("(основной)", "(негизги)", "(main)", "(ana)", "(asosiy)")
                    : c.IsSize ? T("(размер)", "(өлчөм)", "(size)", "(beden)", "(o'lcham)") + " " + c.VariantName
                    : c.Quantity > 1 ? T($"(упаковка ×{c.Quantity:0.##})", $"(таңгак ×{c.Quantity:0.##})", $"(pack ×{c.Quantity:0.##})", $"(paket ×{c.Quantity:0.##})", $"(qadoq ×{c.Quantity:0.##})") + (c.VariantName != null ? " " + c.VariantName : "")
                    : T("(доп.)", "(кошумча)", "(extra)", "(ek)", "(qo'sh.)") + (c.VariantName != null ? " " + c.VariantName : "")));
            combo.SelectedIndex = st.BarcodeIndex;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                    st.BarcodeIndex = combo.SelectedIndex;
                if (st.Selected)
                    SchedulePreview();
            };
            _codeSync.Add(() => { if (combo.SelectedIndex != st.BarcodeIndex && st.BarcodeIndex < combo.Items.Count) combo.SelectedIndex = st.BarcodeIndex; });
            codeControl = combo;
        }
        else
        {
            var codeText = new TextBlock
            {
                Text = choices.Count == 1 ? choices[0].Code : "—", FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), FontSize = 15, FontWeight = FontWeight.SemiBold,
                MinHeight = 30, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Use(codeText, TextBlock.ForegroundProperty, choices.Count == 1 ? "BrushAccentStrong" : "BrushTextSoft");
            codeControl = codeText;
        }

        Button action;
        if (choices.Count == 0 && _template.Barcode.Enabled)
        {
            action = Ghost("＋ " + T("Создать штрих-код", "Штрих-код түзүү", "Create a barcode", "Barkod oluştur", "Shtrix-kod yaratish"));
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(action, T("Сгенерировать EAN-13 и сохранить в карточку товара (после подтверждения)", "EAN-13 түзүп, товардын карточкасына сактоо (ырастоодон кийин)",
                "Generate an EAN-13 and save it to the product card (after confirmation)", "EAN-13 üretip ürün kartına kaydet (onaydan sonra)", "EAN-13 yaratib, mahsulot kartochkasiga saqlash (tasdiqlagandan so'ng)"));
            action.Click += async (_, _) => await CreateBarcodeAsync(p).ConfigureAwait(true);
        }
        else
        {
            action = new Button { Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(10), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Focusable = false, IsEnabled = printable, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            action.Classes.Add("sec-primary");
            action.Content = ButtonWithIcon("printer-outline", T("Распечатать штрих-код", "Штрих-кодду басуу", "Print barcode", "Barkodu yazdır", "Shtrix-kodni chop etish"));
            action.Click += async (_, _) => await PrintOneAsync(p).ConfigureAwait(true);
        }

        var name = new TextBlock { Text = p.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 38 };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        ToolTip.SetTip(name, p.Title);
        var price = new TextBlock { Text = BarcodePrintCatalog.LabelPriceText(p.PriceLine), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        Use(price, TextBlock.ForegroundProperty, "BrushTextSoft");

        ToolTip.SetTip(codeControl, caption.Text);
        // 2026-11-01 (П-2): значок ⚠ с замечанием по штрихкоду (неверная контрольная цифра, кириллица, слишком длинный или плотный код) — до печати.
        var warn = new TextBlock { Text = "⚠", FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
        Use(warn, TextBlock.ForegroundProperty, "BrushWarning");
        void UpdateWarnNow()
        {
            var problem = choices.Count == 0 || !_template.Barcode.Enabled ? null : ProblemOf(choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)].Code, _template);
            warn.IsVisible = problem != null;
            ToolTip.SetTip(warn, problem);
        }
        UpdateWarnNow();
        _warnSync.Add(UpdateWarnNow);
        if (codeControl is ComboBox codeCombo)
            codeCombo.SelectionChanged += (_, _) => UpdateWarnNow();
        var codeHost = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        codeHost.Children.Add(codeControl);
        Grid.SetColumn(warn, 1);
        codeHost.Children.Add(warn);
        var stack = new StackPanel { Spacing = 2, Children = { top, codeHost, action, name, price } };
        var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 10), Child = stack };
        Use(border, Border.BackgroundProperty, "BrushPanel");

        void Style()
        {
            border.BorderThickness = new Thickness(st.Selected ? 2 : 1.5);
            Use(border, Border.BorderBrushProperty, st.Selected ? "BrushAccent" : "BrushBorder");
        }
        Style();

        NumericUpDown Realize()
        {
            if (copies != null)
                return copies;
            copies = Num(1, 99, CopiesOf(p, st), 118);
            copies.IsEnabled = printable;
            Avalonia.Automation.AutomationProperties.SetName(copies, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
            copies.ValueChanged += (_, _) =>
            {
                if (_copiesGuard)
                    return;
                var v = (int)(copies.Value ?? BaseCopies(p));
                st.Copies = v == BaseCopies(p) ? null : v;
                UpdateSelectionUi();
            };
            copiesHost.Child = copies;
            return copies;
        }
        if (printable)
        {
            copiesBox.PointerEntered += (_, _) => Realize();
            copiesBox.PointerPressed += (_, _) => Realize();
            copiesBox.GotFocus += (_, _) => Realize().Focus();
        }
        if (st.Selected)
            Realize();

        check.IsCheckedChanged += (_, _) =>
        {
            SetSelected(st, check.IsChecked == true);
            if (check.IsChecked == true && printable)
                Realize();
            Style();
            UpdateSelectionUi();
            SchedulePreview();
        };
        _copiesSync.Add(() =>
        {
            if (copies != null)
                copies.Value = CopiesOf(p, st);
            else
                copiesText.Text = CopiesOf(p, st).ToString();
        });
        _pageSync.Add(() =>
        {
            if ((check.IsChecked == true) != st.Selected)
                check.IsChecked = st.Selected;
            Style();
        });
        return border;
    }

}
