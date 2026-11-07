using NurMarketKassa.Core.Application;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15 — часть класса ClientDocEditorWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — ClientDocEditorWindow.cs
public sealed partial class ClientDocEditorWindow
{
    internal void RebuildRows()
    {
        RowsPanel.Children.Clear();
        for (var i = 0; i < _doc.Lines.Count; i++)
            RowsPanel.Children.Add(LineRow(_doc.Lines[i], i + 1));
        if (_doc.Lines.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Строк пока нет. Начните вводить название товара или отсканируйте штрихкод — Enter добавит строку.",
                    "Саптар азырынча жок. Товардын атын жаза баштаңыз же штрихкодду сканерлеңиз — Enter сап кошот.",
                    "No lines yet. Start typing a product name or scan a barcode — Enter adds a line.",
                    "Henüz satır yok. Ürün adını yazmaya başlayın veya barkodu okutun — Enter satır ekler.",
                    "Hali qatorlar yo'q. Tovar nomini yozing yoki shtrix-kodni skanerlang — Enter qator qo'shadi."),
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 14),
            };
            Res(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            RowsPanel.Children.Add(empty);
        }
    }

    private Control LineRow(ClientDocLine line, int index)
    {
        var no = new TextBlock { Text = index.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Res(no, TextBlock.ForegroundProperty, "BrushTextSoft");
        var sum = new TextBlock { Text = ClientDocsUi.Money(line.Sum), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        Res(sum, TextBlock.ForegroundProperty, "BrushText");
        var basePrice = line.BasePrice;
        // 2026-10-21 (ревью документов): телефон при смене цены снимает pr0/dp — без цены до скидки скидки строки нет
        // (иначе «dp» без «pr0» применился бы второй раз к уже сниженной цене).
        var disc = line.ListPrice != null ? line.DiscountPercent ?? 0 : 0;
        void Recalc()
        {
            line.SetPrice(basePrice, disc);
            sum.Text = ClientDocsUi.Money(line.Sum);
            UpdateTotals();
            Changed();
        }
        var name = Cell(line.Name, T("Наименование", "Аталышы", "Item", "Ürün", "Nomi"), v => { line.Name = v.Trim(); Changed(); });
        name.TextWrapping = TextWrapping.Wrap;
        var unit = Cell(line.Unit, T("Ед.", "Бирд.", "Unit", "Birim", "Birl."), v => { line.Unit = string.IsNullOrWhiteSpace(v) ? "шт" : v.Trim(); Changed(); });
        unit.HorizontalContentAlignment = HorizontalAlignment.Center;
        var qty = Cell(line.Qty.ToString("0.###", CultureInfo.InvariantCulture), T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), v =>
        {
            if (TryNum(v, out var q) && q >= 0)
            {
                line.Qty = q;
                Recalc();
            }
        }, right: true);
        var price = Cell(basePrice.ToString("0.##", CultureInfo.InvariantCulture), T("Цена", "Баасы", "Price", "Fiyat", "Narxi"), v =>
        {
            if (TryNum(v, out var p) && p >= 0)
            {
                basePrice = p;
                Recalc();
            }
        }, right: true);
        var discBox = Cell(disc > 0 ? disc.ToString("0.##", CultureInfo.InvariantCulture) : "", T("Скидка, %", "Арзандатуу, %", "Discount, %", "İndirim, %", "Chegirma, %"), v =>
        {
            var t = (v ?? "").Trim().TrimEnd('%');
            if (t.Length == 0)
                disc = 0;
            else if (TryNum(t, out var d) && d is >= 0 and <= 100)
                disc = d;
            else
                return;
            Recalc();
        }, right: true);
        discBox.Watermark = "0";
        // 2026-10-22 (модалки, пакет 2): ✕ строки 44×44 (было 32).
        var del = new Button { Content = "✕", Width = 44, Height = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        del.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(del, T($"Удалить строку {index}", $"{index}-сапты өчүрүү", $"Delete line {index}", $"{index}. satırı sil", $"{index}-qatorni o'chirish"));
        ToolTip.SetTip(del, T("Удалить строку", "Сапты өчүрүү", "Delete line", "Satırı sil", "Qatorni o'chirish"));
        del.Click += (_, _) =>
        {
            _doc.Lines.Remove(line);
            RebuildRows();
            UpdateTotals();
            Changed();
            AddBox.Focus();
        };
        var boxes = new[] { name, unit, qty, price, discBox };
        // Enter в ячейке — к следующей ячейке, из последней — в поле добавления товара. Строку Enter не удаляет и не добавляет.
        for (var i = 0; i < boxes.Length; i++)
        {
            var next = i + 1 < boxes.Length ? boxes[i + 1] : null;
            boxes[i].KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                if (next != null)
                {
                    next.Focus();
                    next.SelectAll();
                }
                else
                    AddBox.Focus();
            };
        }
        var grid = LineGrid(no, name, unit, qty, price, discBox, sum, del);
        var border = new Border { Padding = new Thickness(8, 4), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        Res(border, Border.BorderBrushProperty, "BrushBorder");
        return border;
    }

    private static Grid LineGrid(params Control[] cells)
    {
        // 2026-10-22 (модалки, пакет 2): последняя колонка 44 (было 38) — под ✕ строки 44×44, иначе кнопка вылезала за край строки.
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,62,78,96,70,108,44") };
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i].Margin = i is 0 or 6 or 7 ? cells[i].Margin : new Thickness(0, 0, 6, 0);
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    private TextBox Cell(string text, string name, Action<string> changed, bool right = false)
    {
        // MinWidth = 0: у TextBox темы Fluent свой минимум ширины — узкие колонки (ед., скидка) раздвигали строку, и последняя
        // строка таблицы съезжала вправо (снимки стенда 21.10).
        var b = new TextBox { Text = text, MinHeight = 36, MinWidth = 0, Padding = new Thickness(8, 6), FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center };
        if (right)
            b.HorizontalContentAlignment = HorizontalAlignment.Right;
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        b.TextChanged += (_, _) =>
        {
            if (!_filling)
                changed(b.Text ?? "");
        };
        return b;
    }

    private async Task ShowSalesAsync()
    {
        if (string.IsNullOrWhiteSpace(_doc.ClientId))
        {
            ShowMessage(T("Сначала выберите клиента — покажу его чеки.", "Адегенде кардарды тандаңыз — анын чектерин көрсөтөм.", "Choose a client first — I'll show their receipts.",
                "Önce müşteriyi seçin — fişlerini göstereceğim.", "Avval mijozni tanlang — uning cheklarini ko'rsataman."));
            return;
        }
        var list = new StackPanel { Spacing = 4, Width = 420 };
        var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 420 }, Placement = PlacementMode.BottomEdgeAlignedRight };
        var loading = new TextBlock { Text = T("Загружаю чеки клиента…", "Кардардын чектери жүктөлүүдө…", "Loading client receipts…", "Müşteri fişleri yükleniyor…", "Mijoz cheklari yuklanmoqda…"), Margin = new Thickness(6) };
        list.Children.Add(loading);
        flyout.ShowAt(_fromSale);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var sales = await App.SalesApi.PosSalesByClientAsync(_doc.ClientId!, maxPages: 2, cts.Token).ConfigureAwait(true);
            list.Children.Clear();
            if (sales.Count == 0)
                list.Children.Add(new TextBlock { Text = T("У клиента нет чеков.", "Кардардын чектери жок.", "The client has no receipts.", "Müşterinin fişi yok.", "Mijozda cheklar yo'q."), Margin = new Thickness(6) });
            foreach (var sale in sales.Take(30))
            {
                var id = ClientDocsUi.Str(sale, "id") ?? "";
                var when = DateTimeOffset.TryParse(ClientDocsUi.Str(sale, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var w) ? w.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
                var number = ReceiptNumber(sale);
                var total = double.TryParse(ClientDocsUi.Str(sale, "total"), NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 0;
                var b = UiKit.Ghost(this, $"{when} · № {number} · {ClientDocsUi.Money(total)} {ClientDocsUi.Som}");
                b.Height = 44;
                b.Focusable = true;
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Click += async (_, _) =>
                {
                    flyout.Hide();
                    try
                    {
                        var detail = await App.SalesApi.PosSaleGetAsync(id).ConfigureAwait(true);
                        var before = _doc.Lines.Count;
                        ClientDocsUi.AppendSaleLines(_doc, detail);
                        if (string.IsNullOrWhiteSpace(_doc.BasedOn))
                        {
                            _doc.BasedOn = T("чек № ", "чек № ", "receipt No. ", "fiş No. ", "chek № ") + number;
                            _filling = true;
                            _basedOn.Text = _doc.BasedOn;
                            _filling = false;
                        }
                        RebuildRows();
                        UpdateTotals();
                        Changed();
                        ShowMessage(T($"Добавлено строк из чека: {_doc.Lines.Count - before}", $"Чектен кошулган саптар: {_doc.Lines.Count - before}", $"Lines added from the receipt: {_doc.Lines.Count - before}",
                            $"Fişten eklenen satır: {_doc.Lines.Count - before}", $"Chekdan qo'shilgan qatorlar: {_doc.Lines.Count - before}"));
                    }
                    catch (Exception ex)
                    {
                        ShowMessage(T("Чек не загрузился: ", "Чек жүктөлгөн жок: ", "The receipt did not load: ", "Fiş yüklenemedi: ", "Chek yuklanmadi: ") + ServerOutageMonitor.FriendlyError(ex));
                    }
                };
                list.Children.Add(b);
            }
        }
        catch (Exception ex)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = T("Чеки не загрузились: ", "Чектер жүктөлгөн жок: ", "Receipts did not load: ", "Fişler yüklenemedi: ", "Cheklar yuklanmadi: ") + ServerOutageMonitor.FriendlyError(ex), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) });
        }
    }

    internal static string ReceiptNumber(JsonElement sale)
    {
        foreach (var key in new[] { "receipt_number", "receipt_no", "check_number", "check_no", "sale_number", "number" })
            if (ClientDocsUi.Str(sale, key) is { Length: > 0 } v)
                return v.Trim();
        var id = (ClientDocsUi.Str(sale, "id") ?? "").Replace("-", "");
        return id.Length >= 8 ? id[..8].ToUpperInvariant() : "—";
    }

    // ───────────────────────────── предпросмотр ─────────────────────────────

    private ClientDocRenderer.Input MakeInput()
    {
        var path = _profile.LogoPath;
        if (_logo.Path != path)
        {
            byte[]? bytes = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Документ клиента: логотип не прочитан ({ex.Message}).", "WARNING");
            }
            _logo = (path, bytes);
        }
        return new ClientDocRenderer.Input(_doc.Clone(), _profile.Clone(), _doc.Template ?? ClientDocRenderer.DefaultTemplate(_doc.Kind), ClientDocsUi.AccentHex(this), _logo.Bytes);
    }

    /// <summary>Перерисовать листы (в фоне; устаревший результат отбрасывается).</summary>
    internal async Task RenderPreviewAsync()
    {
        var gen = ++_renderGen;
        var input = MakeInput();
        List<byte[]> pages;
        try
        {
            pages = await Task.Run(() => ClientDocRenderer.PageImages(input, 110)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ клиента: предпросмотр не построен ({ex}).", "WARNING");
            if (gen == _renderGen)
                _previewInfo.Text = T("Предпросмотр не построен", "Алдын ала көрүү түзүлгөн жок", "Preview failed", "Önizleme oluşturulamadı", "Ko'rinish yaratilmadi");
            return;
        }
        if (gen != _renderGen)
            return;
        _pages.Children.Clear();
        foreach (var png in pages)
        {
            using var ms = new MemoryStream(png);
            var img = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform };
            var sheet = new Border { Child = img, Background = Brushes.White, BoxShadow = BoxShadows.Parse("0 2 10 0 #33000000") };
            _pages.Children.Add(sheet);
        }
        PageCount = pages.Count;
        _previewInfo.Text = pages.Count == 1
            ? T("1 страница", "1 бет", "1 page", "1 sayfa", "1 sahifa")
            : T($"страниц: {pages.Count}", $"беттер: {pages.Count}", $"{pages.Count} pages", $"{pages.Count} sayfa", $"sahifalar: {pages.Count}");
    }

    /// <summary>Сколько листов в последнем предпросмотре (для стенда).</summary>
    public int PageCount { get; private set; }

    private WrapPanel? _headLeft;
    private WrapPanel? _headRight;

    private void ApplyLayout(double width)
    {
        if (width <= 0 || double.IsNaN(width))
            return;
        // 2026-10-31: узкая шапка (800×480): бланк и «Документ/Предпросмотр» — второй строкой под номером, на всю ширину.
        if (_headLeft is not null && _headRight is not null)
        {
            var tight = width < 900;
            Grid.SetRow(_headRight, tight ? 1 : 0);
            Grid.SetColumn(_headRight, tight ? 0 : 1);
            Grid.SetColumnSpan(_headRight, tight ? 2 : 1);
            Grid.SetColumnSpan(_headLeft, tight ? 2 : 1);
            _headRight.HorizontalAlignment = tight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        _narrow = width < 1180;
        _viewSeg.IsVisible = _narrow;
        _tplLabel.IsVisible = !_narrow;
        _main.ColumnDefinitions.Clear();
        if (_narrow)
        {
            _main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(_formScroll, 0);
            Grid.SetColumn(_previewCard, 0);
            _formScroll.IsVisible = !_showPreviewNarrow;
            _previewCard.IsVisible = _showPreviewNarrow;
        }
        else
        {
            _main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _main.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Clamp(width * 0.36, 420, 560))));
            Grid.SetColumn(_formScroll, 0);
            Grid.SetColumn(_previewCard, 1);
            _formScroll.IsVisible = true;
            _previewCard.IsVisible = true;
        }
    }

    /// <summary>Узкий экран (1024): «Документ» или «Предпросмотр».</summary>
    public void SetNarrowView(bool preview)
    {
        _showPreviewNarrow = preview;
        UiKit.SetActive(_viewForm, !preview);
        UiKit.SetActive(_viewPreview, preview);
        if (preview)
        {
            _previewTimer.Stop();
            _ = RenderPreviewAsync();
        }
        ApplyLayout(Bounds.Width > 0 ? Bounds.Width : Width);
    }

    // ───────────────────────────── вывод ─────────────────────────────

    private string FileBaseName()
    {
        var kind = _doc.Kind switch { ClientDocKind.Quote => "КП", ClientDocKind.Invoice => "Счёт", _ => "Накладная" };
        var name = $"{kind} {_doc.Number} {_doc.Client}".Trim();
        var safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return safe.Length > 80 ? safe[..80] : safe;
    }

    private async Task SavePdfAsync()
    {
        SaveNow();
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Сохранить PDF", "PDF сактоо", "Save PDF", "PDF kaydet", "PDF saqlash"),
                SuggestedFileName = FileBaseName() + ".pdf",
                FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
            }).ConfigureAwait(true);
            // 2026-11-05 (А3-05): Android — без локального пути: временный файл + копия через OpenWriteAsync
            var path = file is null ? null : ExportFileBridge.PathFor(file);
            if (file is null || string.IsNullOrEmpty(path))
                return;
            var input = MakeInput();
            await Task.Run(() => ClientDocRenderer.SavePdf(input, path)).ConfigureAwait(true);
            path = await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);
            _doc.AddHistory("PDF");
            Changed();
            ShowFile(path, T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: "));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ клиента: PDF не сохранён ({ex}).", "WARNING");
            ShowMessage(T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + ex.Message);
        }
    }

    private async Task SaveWordAsync()
    {
        SaveNow();
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Сохранить в Word", "Word'ко сактоо", "Save to Word", "Word olarak kaydet", "Word'ga saqlash"),
                SuggestedFileName = FileBaseName() + ".docx",
                FileTypeChoices = [new FilePickerFileType("Word") { Patterns = ["*.docx"] }],
            }).ConfigureAwait(true);
            var path = file is null ? null : ExportFileBridge.PathFor(file); // 2026-11-05 (А3-05)
            if (file is null || string.IsNullOrEmpty(path))
                return;
            var input = MakeInput();
            await Task.Run(() => ClientDocWord.Save(input, path)).ConfigureAwait(true);
            path = await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);
            ShowFile(path, T("Word сохранён: ", "Word сакталды: ", "Word saved: ", "Word kaydedildi: ", "Word saqlandi: "));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ клиента: Word не сохранён ({ex}).", "WARNING");
            ShowMessage(T("Word не сохранён: ", "Word сакталган жок: ", "Word not saved: ", "Word kaydedilemedi: ", "Word saqlanmadi: ") + ex.Message);
        }
    }

    /// <summary>PDF в «Документы\NurMarket\Документы клиентов» — для отправки (без окна выбора файла).</summary>
    private async Task<string?> AutoPdfAsync()
    {
        SaveNow();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NurMarket", "Документы клиентов");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileBaseName() + ".pdf");
            var input = MakeInput();
            await Task.Run(() => ClientDocRenderer.SavePdf(input, path)).ConfigureAwait(true);
            return path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ клиента: PDF для отправки не сохранён ({ex}).", "WARNING");
            ShowMessage(T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + ex.Message);
            return null;
        }
    }

    private string ShareText()
    {
        var sb = new StringBuilder();
        sb.Append(ClientDocsUi.KindLong(_doc.Kind)).Append(" № ").Append(_doc.Number).Append(' ')
          .Append(T("от ", "", "of ", "", "")).Append(_doc.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).AppendLine();
        if (!string.IsNullOrWhiteSpace(_profile.Name))
            sb.AppendLine(_profile.Name);
        sb.Append(T("Сумма: ", "Сумма: ", "Amount: ", "Tutar: ", "Summa: ")).Append(ClientDocsUi.Money(_doc.Total)).Append(' ').AppendLine(ClientDocsUi.Som);
        if (_doc.Kind == ClientDocKind.Invoice)
            sb.Append(T("Оплатить до ", "Төлөө мөөнөтү ", "Pay by ", "Son ödeme ", "To'lash muddati ")).AppendLine(_doc.Until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        else if (_doc.Kind == ClientDocKind.Quote)
            sb.Append(T("Предложение действует до ", "Сунуш жарактуу ", "Valid until ", "Geçerlilik ", "Amal qiladi ")).AppendLine(_doc.Until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        return sb.ToString().TrimEnd();
    }

    private void MarkSent()
    {
        var sentText = T("Отправлен", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi");
        // 2026-10-24 (аудит, круг 8, С-17): пустой документ «Отправленным» не становится.
        // 2026-10-24 (приёмка, ПР-7): пустая строка — не строка.
        if (!HasRealLines && _doc.Status is "draft" or "issued")
            return;
        if (_doc.Status is "draft" or "issued")
            SetStatus("sent", sentText);
        else
        {
            _doc.AddHistory(sentText);
            Changed();
        }
    }

    private Flyout SendFlyout()
    {
        var panel = new StackPanel { Spacing = 6, Width = 330 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        var note = new TextBlock
        {
            Text = T("PDF сохранится в «Документы\\NurMarket\\Документы клиентов». WhatsApp откроет чат клиента с текстом — перетащите файл в чат. Статус станет «Отправлен».",
                "PDF «Документтер\\NurMarket\\Документы клиентов» папкасына сакталат. WhatsApp кардардын чатын текст менен ачат — файлды чатка сүйрөңүз. Статус «Жөнөтүлдү» болот.",
                "The PDF is saved to “Documents\\NurMarket\\Документы клиентов”. WhatsApp opens the client's chat with text — drag the file into the chat. The status becomes “Sent”.",
                "PDF «Belgeler\\NurMarket\\Документы клиентов» klasörüne kaydedilir. WhatsApp müşterinin sohbetini metinle açar — dosyayı sohbete sürükleyin. Durum «Gönderildi» olur.",
                "PDF «Hujjatlar\\NurMarket\\Документы клиентов» papkasiga saqlanadi. WhatsApp mijoz chatini matn bilan ochadi — faylni chatga torting. Holat «Yuborildi» bo'ladi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        Res(note, TextBlock.ForegroundProperty, "BrushTextSoft");
        var wa = Btn("WhatsApp");
        var folder = Btn(T("PDF и папка", "PDF жана папка", "PDF and folder", "PDF ve klasör", "PDF va papka"));
        var copy = Btn(T("Скопировать текст", "Текстти көчүрүү", "Copy text", "Metni kopyala", "Matnni nusxalash"));
        foreach (var b in new[] { wa, folder, copy })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(b);
        }
        panel.Children.Add(note);
        wa.Click += async (_, _) =>
        {
            flyout.Hide();
            var path = await AutoPdfAsync().ConfigureAwait(true);
            if (path == null)
                return;
            RevealFile(path);
            var digits = new string(_doc.Phone.Where(char.IsDigit).ToArray());
            if (digits.StartsWith('0') && digits.Length == 10)
                digits = "996" + digits[1..];
            else if (digits.Length == 9)
                digits = "996" + digits;
            SiteOrdersWindow.OpenUrl("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(ShareText()));
            MarkSent();
            ShowFile(path, T("Перетащите файл в чат WhatsApp: ", "Файлды WhatsApp чатына сүйрөңүз: ", "Drag the file into the WhatsApp chat: ", "Dosyayı WhatsApp sohbetine sürükleyin: ", "Faylni WhatsApp chatiga torting: "));
        };
        folder.Click += async (_, _) =>
        {
            flyout.Hide();
            var path = await AutoPdfAsync().ConfigureAwait(true);
            if (path == null)
                return;
            RevealFile(path);
            MarkSent();
            ShowFile(path, T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: "));
        };
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            if (GetTopLevel(this)?.Clipboard is { } cb)
                await cb.SetTextAsync(ShareText()).ConfigureAwait(true);
            ShowMessage(T("Текст скопирован.", "Текст көчүрүлдү.", "Text copied.", "Metin kopyalandı.", "Matn nusxalandi."));
        };
        return flyout;
    }

    private Flyout PrintFlyout()
    {
        var panel = new StackPanel { Spacing = 8, Width = 330 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        var printers = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(printers, T("Принтер", "Принтер", "Printer", "Yazıcı", "Printer"));
        var go = UiKit.Primary(this, T("Печатать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"));
        go.Height = 44;
        go.Focusable = true;
        go.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(Label(T("Принтер (A4)", "Принтер (A4)", "Printer (A4)", "Yazıcı (A4)", "Printer (A4)")));
        panel.Children.Add(printers);
        panel.Children.Add(go);
        flyout.Opened += (_, _) =>
        {
            printers.Items.Clear();
            if (!ClientDocPrint.DirectPrintSupported) // 2026-11-06 (В18, АЛ-5): Windows и Linux (CUPS)
                return;
            var list = ClientDocPrint.Printers();
            var def = ClientDocPrint.DefaultPrinter();
            foreach (var p in list)
                printers.Items.Add(p);
            printers.SelectedIndex = Math.Max(0, list.ToList().IndexOf(def ?? ""));
        };
        go.Click += async (_, _) =>
        {
            flyout.Hide();
            SaveNow();
            if (!ClientDocPrint.DirectPrintSupported || printers.SelectedItem is not string printer)
            {
                // Нет прямой печати (Android) или принтеров нет — PDF откроется в программе просмотра, печать оттуда.
                var path = await AutoPdfAsync().ConfigureAwait(true);
                if (path != null)
                    OpenPath(path);
                return;
            }
            var input = MakeInput();
            ShowMessage(T("Печатаю…", "Басылууда…", "Printing…", "Yazdırılıyor…", "Chop etilmoqda…"));
            var error = await Task.Run(() => ClientDocPrint.Print(printer, FileBaseName(), ClientDocRenderer.PageImages(input, 200))).ConfigureAwait(true);
            ShowMessage(error ?? T("Отправлено на печать: ", "Басып чыгарууга жөнөтүлдү: ", "Sent to print: ", "Yazdırmaya gönderildi: ", "Chop etishga yuborildi: ") + printer);
            if (error == null)
            {
                _doc.AddHistory(T("Напечатан", "Басылды", "Printed", "Yazdırıldı", "Chop etildi"));
                Changed();
            }
        };
        return flyout;
    }

    private Flyout PayFlyout()
    {
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        var amount = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right };
        Avalonia.Automation.AutomationProperties.SetName(amount, T("Сумма оплаты", "Төлөм суммасы", "Payment amount", "Ödeme tutarı", "To'lov summasi"));
        var ok = UiKit.Primary(this, T("Отметить", "Белгилөө", "Mark", "İşaretle", "Belgilash"));
        ok.Height = 44;
        ok.Focusable = true;
        ok.HorizontalAlignment = HorizontalAlignment.Stretch;
        var note = new TextBlock
        {
            Text = T("Только отметка в документе: деньги в кассу и долг клиента не меняются. Настоящая оплата — продажей или «Принять оплату» долга.",
                "Документте белги гана: кассадагы акча жана кардардын карызы өзгөрбөйт. Чыныгы төлөм — сатуу же карыздын «Төлөмдү кабыл алуу».",
                "Only a mark in the document: the till money and the client's debt don't change. Real payment — via a sale or “Accept payment” for a debt.",
                "Yalnızca belgede bir işaret: kasadaki para ve müşteri borcu değişmez. Gerçek ödeme — satış veya borç için «Ödemeyi al» ile.",
                "Faqat hujjatdagi belgi: kassadagi pul va mijoz qarzi o'zgarmaydi. Haqiqiy to'lov — sotuv yoki qarz uchun «To'lovni qabul qilish» orqali."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        Res(note, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(Label(T("Оплачено, сом", "Төлөндү, сом", "Paid, som", "Ödenen, som", "To'landi, so'm")));
        panel.Children.Add(amount);
        // 2026-10-22 (модалки, пакет 2): неверная сумма — текстом под полем (раньше «Отметить» молча ничего не делал).
        var amountError = SectionCards.ErrorText(this);
        amount.TextChanged += (_, _) => SectionCards.ShowError(amountError, null);
        panel.Children.Add(amountError);
        panel.Children.Add(ok);
        panel.Children.Add(note);
        flyout.Opened += (_, _) =>
        {
            amount.Text = _doc.Left.ToString("0.##", CultureInfo.InvariantCulture);
            amount.Focus();
            amount.SelectAll();
        };
        void Apply()
        {
            if (!TryNum(amount.Text, out var sum) || sum <= 0)
            {
                SectionCards.ShowError(amountError, T("Введите сумму больше нуля.", "Нөлдөн чоң сумма жазыңыз.", "Enter an amount above zero.",
                    "Sıfırdan büyük bir tutar girin.", "Noldan katta summa kiriting."), amount);
                return;
            }
            flyout.Hide();
            _doc.Paid = ClientDoc.Round2(Math.Min(_doc.Total, _doc.Paid + sum));
            var paid = _doc.Left <= 0.004;
            SetStatus(paid ? "paid" : "partial", T("Оплата: ", "Төлөм: ", "Payment: ", "Ödeme: ", "To'lov: ") + ClientDocsUi.Money(sum));
            UpdateTotals();
            ShowMessage(paid ? T("Счёт отмечен оплаченным.", "Эсеп төлөндү деп белгиленди.", "The invoice is marked paid.", "Fatura ödendi olarak işaretlendi.", "Hisob to'langan deb belgilandi.")
                : T($"Отмечено. Осталось оплатить {ClientDocsUi.Money(_doc.Left)} сом.", $"Белгиленди. {ClientDocsUi.Money(_doc.Left)} сом калды.", $"Marked. {ClientDocsUi.Money(_doc.Left)} som left.",
                    $"İşaretlendi. {ClientDocsUi.Money(_doc.Left)} som kaldı.", $"Belgilandi. {ClientDocsUi.Money(_doc.Left)} so'm qoldi."));
        }
        ok.Click += (_, _) => Apply();
        amount.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Apply();
            }
        };
        return flyout;
    }

    private Flyout MoreFlyout()
    {
        var panel = new StackPanel { Spacing = 6, Width = 260 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedLeft };
        var word = Btn(T("Сохранить в Word", "Word'ко сактоо", "Save to Word", "Word olarak kaydet", "Word'ga saqlash"));
        var copy = Btn(T("Копия документа", "Документтин көчүрмөсү", "Duplicate", "Belgeyi kopyala", "Hujjat nusxasi"));
        var del = Btn(T("Удалить документ", "Документти өчүрүү", "Delete document", "Belgeyi sil", "Hujjatni o'chirish"));
        del.Classes.Add("danger");
        foreach (var b in new[] { word, copy, del })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(b);
        }
        word.Click += async (_, _) =>
        {
            flyout.Hide();
            await SaveWordAsync().ConfigureAwait(true);
        };
        copy.Click += (_, _) =>
        {
            flyout.Hide();
            SaveNow();
            var dup = _doc.Clone();
            dup.Id = ClientDoc.NewId();
            dup.Number = ClientDocsStore.NextNumber(_doc.Kind);
            dup.Status = "draft";
            dup.Paid = 0;
            dup.History = new();
            dup.Date = DateTime.Now;
            dup.Until = DateTime.Today.AddDays(_doc.Kind == ClientDocKind.Quote ? 7 : 3);
            ClientDocsStore.Save(dup);
            if (Owner is Window owner)
            {
                ClientDocsUi.Open(owner, dup, isNew: false);
                Close();
            }
        };
        del.Click += (_, _) =>
        {
            flyout.Hide();
            if (!PosDialogs.ConfirmYesNo(this, T($"Удалить {ClientDocsUi.KindName(_doc.Kind)} {_doc.Number}?", $"{ClientDocsUi.KindName(_doc.Kind)} {_doc.Number} өчүрүлсүнбү?",
                    $"Delete {ClientDocsUi.KindName(_doc.Kind)} {_doc.Number}?", $"{ClientDocsUi.KindName(_doc.Kind)} {_doc.Number} silinsin mi?", $"{ClientDocsUi.KindName(_doc.Kind)} {_doc.Number} o'chirilsinmi?"),
                    // 2026-10-22 (модалки, пакет 2): глагол и красная кнопка вместо «Да/Нет».
                    confirmText: T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), danger: true))
                return;
            _dirty = false;
            if (!_isNew)
                ClientDocsStore.Delete(_doc.Id);
            _isNew = true;
            Close();
        };
        return flyout;
    }

    /// <summary>КП → «Сделать счёт»; счёт → «Сделать накладную». Новый документ с теми же строками и клиентом, основание — этот.</summary>
    private void MakeNext()
    {
        // 2026-10-24 (аудит, круг 8, С-17): из пустого КП/счёта дальше документ не делаем; нужен покупатель.
        // 2026-10-24 (приёмка, ПР-7): считаем строками только заполненные (название и сумма > 0); предупреждение — Info.
        if (!HasRealLines)
        {
            PosDialogs.Info(this, EmptyDocText());
            return;
        }
        if (string.IsNullOrWhiteSpace(_doc.ClientId) && string.IsNullOrWhiteSpace(_doc.Client))
        {
            PosDialogs.Warning(this, T("Укажите покупателя.", "Сатып алуучуну көрсөтүңүз.", "Specify the buyer.", "Alıcıyı belirtin.", "Xaridorni ko'rsating."));
            return;
        }
        SaveNow();
        var kind = _doc.Kind == ClientDocKind.Quote ? ClientDocKind.Invoice : ClientDocKind.Waybill;
        var next = ClientDocsUi.NewDoc(kind, null);
        next.ClientId = _doc.ClientId;
        next.Client = _doc.Client;
        next.Phone = _doc.Phone;
        next.Inn = _doc.Inn;
        next.Address = _doc.Address;
        next.Lines = _doc.Lines.Where(IsRealLine).Select(l => l.Clone()).ToList(); // 2026-10-24 (приёмка, ПР-7): пустые строки не переносим
        next.Discount = _doc.Discount;
        next.VatPercent = _doc.VatPercent;
        next.Lang = _doc.Lang;
        // Основание — номер документа, как у телефона (basedOn = номер КП); на бланке — «коммерческое предложение КП-0014».
        next.BasedOn = _doc.Number;
        if (kind == ClientDocKind.Invoice)
            next.Status = "issued";
        ClientDocsStore.Save(next);
        var made = T($"{ClientDocsUi.KindName(kind)} {next.Number}", $"{ClientDocsUi.KindName(kind)} {next.Number}", $"{ClientDocsUi.KindName(kind)} {next.Number}", $"{ClientDocsUi.KindName(kind)} {next.Number}", $"{ClientDocsUi.KindName(kind)} {next.Number}");
        if (_doc.Kind == ClientDocKind.Quote && _doc.Status is "draft" or "sent")
            SetStatus("accepted", T("Принято, выставлен ", "Кабыл алынды, берилди ", "Accepted, issued ", "Kabul edildi, düzenlendi ", "Qabul qilindi, berildi ") + made);
        else
        {
            _doc.AddHistory(T("Создан ", "Түзүлдү ", "Created ", "Oluşturuldu ", "Yaratildi ") + made);
            Changed();
        }
        SaveNow();
        if (Owner is Window owner)
        {
            ClientDocsUi.Open(owner, next, isNew: false);
            Close();
        }
    }

    // ───────────────────────────── файлы ─────────────────────────────

    private async Task ChooseLogoAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Логотип магазина", "Дүкөндүн логотиби", "Shop logo", "Mağaza logosu", "Do'kon logotipi"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(T("Картинка", "Сүрөт", "Image", "Resim", "Rasm")) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
            }).ConfigureAwait(true);
            var path = await NurMarketKassa.AvaloniaHost.Services.ImportFileBridge.TemporaryPathAsync(files.FirstOrDefault()).ConfigureAwait(true); // 2026-11-05 (А4-06): Android — копия (ниже она копируется в папку данных)
            if (string.IsNullOrEmpty(path))
                return;
            // Копия рядом с документами — исходный файл можно удалить/переместить.
            var dir = Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "client-docs");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "logo" + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, copy, overwrite: true);
            _profile.LogoPath = copy;
            _logo = (null, null);
            ProfileChanged();
            UpdateSellerSummary();
            if (_doc.Template == ClientDocRenderer.Minimal)
                ShowMessage(T("Логотип печатается в шаблонах «Классический» и «Современный» (в «Минимал» — без картинок).", "Логотип «Классикалык» жана «Заманбап» бланктарда басылат («Минималда» — сүрөтсүз).",
                    "The logo prints in “Classic” and “Modern” (no images in “Minimal”).", "Logo «Klasik» ve «Modern» şablonlarda basılır («Minimal»de resim yok).", "Logotip «Klassik» va «Zamonaviy»da chiqadi («Minimal»da rasm yo'q)."));
        }
        catch (Exception ex)
        {
            ShowMessage(T("Логотип не выбран: ", "Логотип тандалган жок: ", "Logo not set: ", "Logo ayarlanamadı: ", "Logotip tanlanmadi: ") + ex.Message);
        }
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
        _openFile.IsVisible = false;
    }

    private void ShowFile(string path, string prefix)
    {
        _lastFile = path;
        _message.Text = prefix + Path.GetFileName(path);
        _message.IsVisible = true;
        _openFile.IsVisible = true;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        // 2026-11-06: В4 А5-05 — общий запуск файла (на Android — Intent).
        if (!PlatformLauncher.OpenFile(path))
            PosLogger.Log("Документ клиента: файл не открылся.", "WARNING");
    }

    private static void RevealFile(string path)
    {
        // 2026-11-06: В4 А5-05 — общий запуск (Windows: проводник, macOS: Finder, Linux: папка; на Android — ничего).
        if (!PlatformLauncher.Reveal(path) && !OperatingSystem.IsAndroid())
            PosLogger.Log("Документ клиента: папка не открылась.", "WARNING");
    }

    // ───────────────────────────── мелочи вёрстки ─────────────────────────────

    // 2026-10-21 (ревью документов, мелочи): + предел 999 999 999 999,99 (сумма прописью и итоги не ломаются).
    private static bool TryNum(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value) && Math.Abs(value) <= MoneyWords.MaxAmount;

    private static void Res(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is Control c)
            c.Bind(property, c.GetResourceObservable(key));
    }

    private Button Btn(string text)
    {
        var b = UiKit.Ghost(this, text);
        b.Height = 42;
        b.Focusable = true;
        return b;
    }

    private TextBox Box(string name, Action<string> changed)
    {
        var b = new TextBox { MinHeight = 40, Padding = new Thickness(10, 6), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        b.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            changed(b.Text ?? "");
            Changed();
        };
        return b;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Res(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private StackPanel Field(string label, Control input) => new() { Spacing = 4, Children = { Label(label), input } };

    private static Grid TwoCols(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        g.Children.Add(a);
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
        return g;
    }

    private Border Card(string title, params Control[] children)
    {
        var head = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold };
        Res(head, TextBlock.ForegroundProperty, "BrushText");
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(head);
        foreach (var c in children)
            panel.Children.Add(c);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), Child = panel };
        Res(card, Border.BackgroundProperty, "BrushPanel");
        Res(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    private static TextBlock Th(string text, bool right = false)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
