using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, владелец: «вид чека в десктопе как в мобилке» — панель действий под чеком, как ReceiptActionBar мобилки
/// («Печать», «QR», «PDF») плюс «Отправить» (WhatsApp / Telegram / копия текста — существующие каналы кассы, как в документах клиента)
/// и «Вернуть» там, где возврат уже есть. Печать и возврат — обработчики окна (печатная лента и деньги не затронуты); QR, PDF и
/// «Отправить» работают только с моделью чека на экране и ничего не печатают на принтер.</summary>
internal sealed class ReceiptActionBar : Border
{
    // Правый отступ последней кнопки (4) компенсируется, чтобы ряд лежал ровно по краям чека.
    private readonly UniformGrid _grid = new() { Rows = 0, Margin = new Thickness(0, 0, -4, 0) };
    private readonly Button _print, _qr, _pdf, _send, _return;
    private readonly TextBlock _message = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Что сейчас на экране (null — чек ещё не загружен: кнопки выключены).</summary>
    public Func<SaleReceiptCardData?> GetData { get; set; } = () => null;

    /// <summary>«Печать» — печатная лента как раньше (обработчик окна); null — кнопки нет.</summary>
    public Func<Task>? Print { get; set; }

    /// <summary>«Вернуть» — окно возврата; null или <see cref="ShowReturn"/> = false — кнопки нет.</summary>
    public Func<Task>? Return { get; set; }

    /// <summary>Окно, над которым открывается QR.</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>Телефон покупателя для WhatsApp (цифры); пусто — WhatsApp откроет выбор чата.</summary>
    public string? Phone { get; set; }

    /// <summary>Сообщение о результате (в окне истории — своя строка).</summary>
    public Action<string, bool>? Notify { get; set; }

    private int _count = 1;
    private bool _showReturn;
    private bool _printPrimary = true;
    private bool _enabled;

    public Button PrintButton => _print;
    public Button ReturnButton => _return;

    public ReceiptActionBar()
    {
        _print = Make(Tr.T("Печать", "Басуу", "Print", "Yazdır", "Chop etish"), Icon.Print, Tr.T("Напечатать копию чека", "Чектин көчүрмөсүн басып чыгаруу", "Print a copy of the receipt", "Fiş kopyasını yazdır", "Chek nusxasini chop etish"));
        _qr = Make("QR", Icon.Qr, Tr.T("Показать QR покупателю", "QR кодду сатып алуучуга көрсөтүү", "Show the QR to the customer", "QR'yi müşteriye göster", "QR ni xaridorga ko'rsatish"));
        _pdf = Make("PDF", Icon.Pdf, Tr.T("Сохранить чек в PDF", "Чекти PDF катары сактоо", "Save the receipt as PDF", "Fişi PDF olarak kaydet", "Chekni PDF sifatida saqlash"));
        _send = Make(Tr.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), Icon.Send, Tr.T("Отправить чек: WhatsApp, Telegram", "Чекти жөнөтүү: WhatsApp, Telegram", "Send the receipt: WhatsApp, Telegram", "Fişi gönder: WhatsApp, Telegram", "Chekni yuborish: WhatsApp, Telegram"));
        _return = Make(Tr.T("Вернуть", "Кайтаруу", "Return", "İade", "Qaytarish"), Icon.Undo, Tr.T("Вернуть товар по этому чеку", "Бул чек боюнча товарды кайтаруу", "Return goods from this receipt", "Bu fişten ürün iadesi", "Ushbu chek bo'yicha tovarni qaytarish"));

        _print.Click += async (_, _) => await Guard(_print, async () => { if (Print is { } p) await p().ConfigureAwait(true); }).ConfigureAwait(true);
        _return.Click += async (_, _) => await Guard(_return, async () => { if (Return is { } r) await r().ConfigureAwait(true); }).ConfigureAwait(true);
        _qr.Click += (_, _) => { if (GetData() is { } d) ReceiptShare.ShowQr(OwnerWindow ?? TopLevel.GetTopLevel(this) as Window, d); };
        _pdf.Click += async (_, _) => await Guard(_pdf, SavePdfAsync).ConfigureAwait(true);
        _send.Flyout = BuildSendFlyout();

        Child = new StackPanel { Children = { _grid, _message } };
        SizeChanged += (_, e) => { if (e.WidthChanged) ApplyLayout(); };
        Refresh();
    }

    /// <summary>Печать — главная (жёлтая), пока нет другой главной кнопки в окне (например, «Отправить на сервер»).</summary>
    public bool PrintPrimary { get => _printPrimary; set { _printPrimary = value; Refresh(); } }

    public bool ShowReturn { get => _showReturn; set { _showReturn = value; Refresh(); } }

    /// <summary>Чек загружен — кнопки доступны.</summary>
    public bool IsReady { get => _enabled; set { _enabled = value; Refresh(); } }

    /// <summary>Пересобрать ряд: видимые кнопки поровну; больше трёх — в два ряда (узкая колонка, длинные подписи на кыргызском).</summary>
    public void Refresh()
    {
        _grid.Children.Clear();
        var list = new System.Collections.Generic.List<Button>();
        if (Print != null) list.Add(_print);
        list.Add(_qr);
        list.Add(_pdf);
        list.Add(_send);
        if (_showReturn && Return != null) list.Add(_return);
        foreach (var b in list)
        {
            b.IsEnabled = _enabled;
            _grid.Children.Add(b);
        }
        // Один ряд всегда (в окне истории на 1024×600 каждая строка отнимает место у чека); тесно — значок над подписью (ApplyLayout).
        _grid.Columns = list.Count;
        _count = list.Count;
        ApplyLayout();
        _print.Classes.Set("sec-primary", _printPrimary);
        _print.Classes.Set("sec-outline", !_printPrimary);
    }

    /// <summary>Просторно (≥ 124 px на кнопку) — значок слева от подписи, как в мобилке; тесно — значок над подписью, ряд тот же.</summary>
    private void ApplyLayout()
    {
        var per = Bounds.Width <= 0 ? 999 : Bounds.Width / Math.Max(1, _count);
        var vertical = per < 124;
        foreach (var b in new[] { _print, _qr, _pdf, _send, _return })
        {
            if (b.Content is not StackPanel sp)
                continue;
            sp.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            sp.Spacing = vertical ? 2 : 6;
            b.Padding = vertical ? new Thickness(1, 5) : new Thickness(6, 4);
            foreach (var t in sp.Children.OfType<TextBlock>())
                t.FontSize = vertical ? 11 : 13;
        }
    }

    public void ShowMessage(string text, bool ok)
    {
        // Окно со своей строкой результата (Notify) показывает её само; иначе — строка под кнопками.
        if (Notify != null)
        {
            Notify(text, ok);
            return;
        }
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrWhiteSpace(text);
        var key = ok ? "BrushUiStatusOk" : "BrushDanger";
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable(key));
    }

    private static Button Make(string text, string icon, string tip)
    {
        var b = new Button
        {
            MinHeight = 44,
            Margin = new Thickness(0, 0, 4, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(6, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new PathIcon { Data = Geometry.Parse(icon), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        b.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static async Task Guard(Button b, Func<Task> work)
    {
        var was = b.IsEnabled;
        b.IsEnabled = false;
        try { await work().ConfigureAwait(true); }
        catch (Exception ex) { PosLogger.Log($"Панель чека: действие не удалось: {ex}", "WARNING"); }
        finally { b.IsEnabled = was; }
    }

    private async Task SavePdfAsync()
    {
        if (GetData() is not { } d)
            return;
        var path = await ReceiptShare.SavePdfAsync(d).ConfigureAwait(true);
        if (path == null)
        {
            ShowMessage(Tr.T("PDF не сохранён.", "PDF сакталган жок.", "The PDF was not saved.", "PDF kaydedilemedi.", "PDF saqlanmadi."), false);
            return;
        }
        ReceiptShare.Reveal(path);
        ShowMessage(Tr.T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path, true);
    }

    private Flyout BuildSendFlyout()
    {
        var panel = new StackPanel { Spacing = 6, Width = 300 };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        Button Item(string text)
        {
            var b = new Button { Content = text, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Classes.Add("sec-outline");
            panel.Children.Add(b);
            return b;
        }
        var wa = Item("WhatsApp");
        var tg = Item("Telegram");
        var file = Item(Tr.T("PDF и папка", "PDF жана папка", "PDF and folder", "PDF ve klasör", "PDF va papka"));
        var copy = Item(Tr.T("Скопировать текст", "Текстти көчүрүү", "Copy text", "Metni kopyala", "Matnni nusxalash"));
        var note = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = Tr.T("WhatsApp и Telegram откроются с текстом чека; PDF сохраняется в «Документы\\NurMarket\\Чеки» — перетащите файл в чат.",
                "WhatsApp жана Telegram чектин тексти менен ачылат; PDF «Документтер\\NurMarket\\Чеки» папкасына сакталат — файлды чатка сүйрөңүз.",
                "WhatsApp and Telegram open with the receipt text; the PDF is saved to “Documents\\NurMarket\\Чеки” — drag the file into the chat.",
                "WhatsApp ve Telegram fiş metniyle açılır; PDF «Belgeler\\NurMarket\\Чеки» klasörüne kaydedilir — dosyayı sohbete sürükleyin.",
                "WhatsApp va Telegram chek matni bilan ochiladi; PDF «Hujjatlar\\NurMarket\\Чеки» papkasiga saqlanadi — faylni chatga torting."),
        };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(note);

        wa.Click += async (_, _) =>
        {
            flyout.Hide();
            if (GetData() is not { } d) return;
            var digits = ReceiptShare.PhoneDigits(Phone);
            var ok = ReceiptShare.OpenUrl("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(d.CompactText(1500)));
            ShowMessage(ok ? Tr.T("WhatsApp открыт с текстом чека.", "WhatsApp чектин тексти менен ачылды.", "WhatsApp opened with the receipt text.", "WhatsApp fiş metniyle açıldı.", "WhatsApp chek matni bilan ochildi.")
                : Tr.T("WhatsApp не открылся.", "WhatsApp ачылган жок.", "WhatsApp did not open.", "WhatsApp açılmadı.", "WhatsApp ochilmadi."), ok);
            await Task.CompletedTask;
        };
        tg.Click += async (_, _) =>
        {
            flyout.Hide();
            if (GetData() is not { } d) return;
            var ok = ReceiptShare.OpenUrl("tg://msg?text=" + Uri.EscapeDataString(d.CompactText(1500)));
            if (!ok)
            {
                await ReceiptShare.CopyAsync(this, d.ToText()).ConfigureAwait(true);
                ShowMessage(Tr.T("Telegram не найден — текст чека скопирован, вставьте его в чат.", "Telegram табылган жок — чектин тексти көчүрүлдү, чатка чаптаңыз.",
                    "Telegram was not found — the receipt text is copied, paste it into a chat.", "Telegram bulunamadı — fiş metni kopyalandı, sohbete yapıştırın.",
                    "Telegram topilmadi — chek matni nusxalandi, chatga qo'ying."), false);
            }
            else
                ShowMessage(Tr.T("Telegram открыт с текстом чека.", "Telegram чектин тексти менен ачылды.", "Telegram opened with the receipt text.", "Telegram fiş metniyle açıldı.", "Telegram chek matni bilan ochildi."), true);
        };
        file.Click += async (_, _) => { flyout.Hide(); await SavePdfAsync().ConfigureAwait(true); };
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            if (GetData() is not { } d) return;
            await ReceiptShare.CopyAsync(this, d.ToText()).ConfigureAwait(true);
            ShowMessage(Tr.T("Текст чека скопирован.", "Чектин тексти көчүрүлдү.", "Receipt text copied.", "Fiş metni kopyalandı.", "Chek matni nusxalandi."), true);
        };
        return flyout;
    }

    /// <summary>Значки панели (24×24): печать, QR, PDF, отправить, вернуть — рисунком, своих иконок в шрифте значков для них нет.</summary>
    private static class Icon
    {
        public const string Print = "M6,3 H18 V8 H6 Z M4,9 H20 A2,2 0 0 1 22,11 V17 H18 V14 H6 V17 H2 V11 A2,2 0 0 1 4,9 Z M8,16 H16 V21 H8 Z";
        public const string Qr = "M3,3 H10 V10 H3 Z M5,5 V8 H8 V5 Z M14,3 H21 V10 H14 Z M16,5 V8 H19 V5 Z M3,14 H10 V21 H3 Z M5,16 V19 H8 V16 Z M14,14 H17 V17 H14 Z M18,14 H21 V17 H18 Z M14,18 H17 V21 H14 Z M18,18 H21 V21 H18 Z";
        public const string Pdf = "M6,2 H14 L20,8 V22 H6 Z M13,3.5 V9 H18.5 Z M8,13 H18 V15 H8 Z M8,17 H18 V19 H8 Z";
        public const string Send = "M2,21 L23,12 L2,3 V10 L17,12 L2,14 Z";
        public const string Undo = "M12.5,8 C9.9,8 7.5,9 5.6,10.6 L2,7 V16 H11 L7.4,12.4 C8.8,11.2 10.6,10.5 12.5,10.5 C16,10.5 19,12.8 20.1,16 L22.5,15.2 C21,11 17.1,8 12.5,8 Z";
    }
}

/// <summary>2026-10-27: общие действия чека на экране — окно QR, PDF, ссылки мессенджеров. Только файл PDF и открытие ссылок:
/// на принтер ничего не отправляется.</summary>
internal static class ReceiptShare
{
    /// <summary>Папка PDF-чеков; стенд проверки подставляет свою песочницу. По умолчанию — «Документы\NurMarket\Чеки».</summary>
    public static string? PdfFolderOverride { get; set; }

    public static string PdfFolder()
    {
        if (!string.IsNullOrWhiteSpace(PdfFolderOverride))
            return PdfFolderOverride!;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
            docs = Path.GetTempPath();
        return Path.Combine(docs, "NurMarket", "Чеки");
    }

    public static async Task<string?> SavePdfAsync(SaleReceiptCardData d)
    {
        try
        {
            // 2026-10-27 (аудит кассы, С-12): имя «Чек-{номер}.pdf» не уникально — офлайн-чеки (номера нет) писались в один «Чек-—.pdf», номера
            // повторяются, файлы молча перезаписывались. Теперь: номер, дата-время чека и короткий id (тот же чек — то же имя, другой — другое).
            var name = (d.IsReturnDoc ? Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish") : Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek")) + "-" + d.NumberText;
            name = SaleReceiptCardData.UniqueFileStem(name, d.DateTimeText, d.SaleId, d.Cashier + "|" + d.TotalText + "|" + d.Lines.Count);
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var path = Path.Combine(PdfFolder(), name + ".pdf");
            await Task.Run(() => ReceiptPaperPdf.Create(d, path)).ConfigureAwait(true);
            return path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чек: PDF не сохранён: {ex}", "WARNING");
            return null;
        }
    }

    /// <summary>Показать файл в проводнике (Windows) или открыть папку (Linux); на Android — ничего.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", "\"" + (Path.GetDirectoryName(path) ?? ".") + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чек: папка с PDF не открылась: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Открыть ссылку (wa.me, tg://); false — не открылась.</summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чек: ссылка не открылась: {ex.Message}", "WARNING");
            return false;
        }
    }

    public static string PhoneDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith('0') && digits.Length == 10)
            digits = "996" + digits[1..];
        else if (digits.Length == 9)
            digits = "996" + digits;
        return digits;
    }

    public static async Task CopyAsync(Visual from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } cb)
            await cb.SetTextAsync(text).ConfigureAwait(true);
    }

    /// <summary>Окно «QR-код чека» (как ReceiptQrScreen мобилки): покупатель наводит камеру — по ссылке открывается чек; пока сервер
    /// не выдаёт ссылку — камера покажет чек текстом. QR всегда тёмный на белом — светлый на тёмном многие сканеры не читают.</summary>
    public static void ShowQr(Window? owner, SaleReceiptCardData d)
    {
        var qr = new ReceiptQrView
        {
            Data = d.QrPayload(),
            Size = 280,
            Dark = new SolidColorBrush(Color.Parse("#0B2A3C")),
            Light = Brushes.White,
        };
        var frame = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center, Child = qr };
        var hint = new TextBlock
        {
            Text = d.QrUrl != null
                ? Tr.T("Покупатель наводит камеру телефона — по ссылке откроется электронный чек (PDF).", "Сатып алуучу телефонунун камерасын багыттайт — шилтеме боюнча электрондук чек (PDF) ачылат.",
                    "The customer points a phone camera — the e-receipt (PDF) opens by the link.", "Müşteri telefon kamerasını tutar — bağlantıdan e-fiş (PDF) açılır.", "Xaridor telefon kamerasini yo'naltiradi — havola bo'yicha elektron chek (PDF) ochiladi.")
                : Tr.T("Электронной ссылки пока нет — камера покажет чек текстом (его можно сохранить).", "Электрондук шилтеме азырынча жок — камера чекти текст катары көрсөтөт (аны сактоого болот).",
                    "There is no e-receipt link yet — the camera will show the receipt as text (it can be saved).", "Henüz e-fiş bağlantısı yok — kamera fişi metin olarak gösterir (kaydedilebilir).", "Elektron havola hozircha yo'q — kamera chekni matn sifatida ko'rsatadi (saqlash mumkin)."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        var w = new Window();
        SectionCards.AsModal(w, 440);
        w.Title = Tr.T("QR-код чека", "Чектин QR коду", "Receipt QR code", "Fiş QR kodu", "Chek QR kodi");
        w.Content = SectionCards.Shell(w.Title, Tr.T("Чек №", "Чек №", "Receipt №", "Fiş №", "Chek №") + d.NumberText + " · " + d.TotalText,
            new StackPanel { Children = { frame, hint } }, SectionCards.Buttons(close));
        close.Click += (_, _) => w.Close();
        _ = PosDialogHost.ShowAsync(w, owner);
    }
}
