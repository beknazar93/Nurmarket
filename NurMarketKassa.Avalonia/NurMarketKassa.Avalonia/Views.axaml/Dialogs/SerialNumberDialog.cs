using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Электроника», ЭЛ2): окно «Серийный номер / IMEI» при добавлении в чек товара с отметкой «серийный».
/// По одному полю на каждую штуку (у 2 шт — два номера): скан или ввод. IMEI (15 цифр) проверяется контрольной цифрой (Луна),
/// повтор номера в чеке и внутри окна запрещён. «Пропустить» — продать без номера (вызывающий просит подтверждение).
/// Окно только собирает номера; чек и журнал не трогает (журнал пишется после успешной продажи, PosCheckoutService).
/// Показывается только в сфере «Электроника» (вызывает MainWindow.AddProductFromCatalogAsync).
/// </summary>
public sealed class SerialNumberDialog : Window
{
    private readonly List<TextBox> _boxes = new();
    private readonly TextBlock _error;
    private readonly HashSet<string> _inCart;

    /// <summary>Введённые номера (нормализованные) — после «Готово». Пусто при «Пропустить» и отмене.</summary>
    public List<string> Serials { get; } = new();

    /// <summary>Кассир нажал «Пропустить».</summary>
    public bool Skipped { get; private set; }

    /// <param name="productTitle">Название товара — под заголовком.</param>
    /// <param name="count">Сколько штук — столько полей.</param>
    /// <param name="alreadyInCart">Номера, уже введённые в этом чеке (нормализованные): повтор запрещён.</param>
    /// <param name="hintOverride">2026-11-13: свой текст подсказки (при возврате — «отсканируйте номера, которые принесли»); null — обычная подсказка ввода при продаже.</param>
    public SerialNumberDialog(string productTitle, int count, IEnumerable<string>? alreadyInCart = null, string? hintOverride = null)
    {
        count = Math.Clamp(count, 1, 200);
        _inCart = new HashSet<string>((alreadyInCart ?? Array.Empty<string>()).Select(ImeiValidator.Normalize), StringComparer.Ordinal);
        Title = Tr.T("Серийный номер / IMEI", "Сериялык номер / IMEI", "Serial number / IMEI", "Seri numarası / IMEI", "Seriya raqami / IMEI");
        Width = 672; // 2026-11-12: карточка 640 (три кнопки в подвале: на 512 «Готово» обрезалось)
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();

        var body = new StackPanel { Spacing = 8 };
        var name = new TextBlock { Text = productTitle, TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeight.SemiBold };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        body.Children.Add(name);
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Text = hintOverride ?? (count == 1
                ? Tr.T("Отсканируйте штрихкод с коробки или впишите IMEI / серийный номер. IMEI — 15 цифр, проверяется автоматически.",
                    "Куттун штрихкодун сканерлеңиз же IMEI / сериялык номерди жазыңыз. IMEI — 15 сан, автоматтык текшерилет.",
                    "Scan the barcode on the box or type the IMEI / serial number. IMEI is 15 digits and is checked automatically.",
                    "Kutudaki barkodu okutun veya IMEI / seri numarasını yazın. IMEI 15 hanedir ve otomatik kontrol edilir.",
                    "Qutidagi shtrix-kodni skanerlang yoki IMEI / seriya raqamini yozing. IMEI — 15 raqam, avtomatik tekshiriladi.")
                : Tr.T($"Нужно {count} номеров — по одному на каждую штуку. Скан или ввод; каждый номер должен быть разным.",
                    $"{count} номер керек — ар бир даанага бирден. Сканер же жазуу; ар бир номер ар башка болушу керек.",
                    $"{count} numbers are needed — one per unit. Scan or type; every number must be different.",
                    $"{count} numara gerekli — her adet için bir tane. Okutun veya yazın; her numara farklı olmalı.",
                    $"{count} ta raqam kerak — har bir dona uchun bittadan. Skaner yoki yozish; har bir raqam boshqacha bo'lishi kerak.")),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        body.Children.Add(hint);

        for (var i = 0; i < count; i++)
        {
            var box = new TextBox
            {
                Name = "SerialBox" + (i + 1), Height = 48, FontSize = 18, FontWeight = FontWeight.SemiBold, MaxLength = ImeiValidator.MaxSerialLength + 8,
                VerticalContentAlignment = VerticalAlignment.Center,
                Watermark = count == 1
                    ? Tr.T("IMEI или серийный номер", "IMEI же сериялык номер", "IMEI or serial number", "IMEI veya seri numarası", "IMEI yoki seriya raqami")
                    : Tr.T($"Номер {i + 1} из {count}", $"{i + 1}-номер ({count}тен)", $"Number {i + 1} of {count}", $"Numara {i + 1} / {count}", $"{i + 1}-raqam ({count} tadan)"),
            };
            box.CornerRadius = new CornerRadius(10);
            Use(box, BackgroundProperty, "BrushInput");
            Use(box, ForegroundProperty, "BrushText");
            Use(box, BorderBrushProperty, "BrushInputBorder");
            var index = i;
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true; // Enter сканера: переход к следующему полю или «Готово» на последнем
                if (index + 1 < _boxes.Count)
                    _boxes[index + 1].Focus();
                else
                    Confirm();
            };
            // 2026-11-12: ошибка прячется, когда кассир (или сканер) начал вводить заново; TextChanged для этого не годится — он срабатывает и при программной подстановке текста.
            box.AddHandler(TextInputEvent, (_, _) => _error!.IsVisible = false, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Avalonia.Automation.AutomationProperties.SetName(box, box.Watermark);
            _boxes.Add(box);
            body.Children.Add(box);
        }

        _error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "SerialError" };
        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        body.Children.Add(_error);

        var skip = new Button { Name = "SerialSkip", Content = Tr.T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish"), MinWidth = 120 };
        skip.Classes.Add("modal-cancel");
        skip.Click += (_, _) => { Skipped = true; Serials.Clear(); Close(true); };
        var cancel = new Button { Name = "SerialCancel", Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true, MinWidth = 120 };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Name = "SerialOk", Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), IsDefault = true, MinWidth = 120 };
        ok.Classes.Add("modal-primary");
        ok.Click += (_, _) => Confirm();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        footer.Children.Add(skip);
        footer.Children.Add(cancel);
        footer.Children.Add(ok);

        var shell = new ModalShell { Margin = new Thickness(16), Footer = footer, Content = body, InitialFocus = _boxes[0] };
        shell.Title = Title;
        Content = shell;
    }

    /// <summary>Проверка полей; при ошибке — текст под полями и фокус на нужном поле. Ничего не закрывает, если есть ошибка.</summary>
    public void Confirm()
    {
        var accepted = new List<string>();
        for (var i = 0; i < _boxes.Count; i++)
        {
            var verdict = ImeiValidator.Check(_boxes[i].Text, out var value);
            var label = _boxes.Count > 1 ? Tr.T($"Номер {i + 1}: ", $"{i + 1}-номер: ", $"Number {i + 1}: ", $"Numara {i + 1}: ", $"{i + 1}-raqam: ") : "";
            string? problem = verdict switch
            {
                ImeiValidator.Verdict.Empty => Tr.T("впишите или отсканируйте номер (или нажмите «Пропустить»).",
                    "номерди жазыңыз же сканерлеңиз («Өткөрүп жиберүү» баскычы да бар).", "enter or scan the number (or press Skip).",
                    "numarayı yazın veya okutun (veya Atla'ya basın).", "raqamni yozing yoki skanerlang («O'tkazib yuborish» tugmasi ham bor)."),
                ImeiValidator.Verdict.BadFormat => Tr.T($"номер должен быть от {ImeiValidator.MinSerialLength} до {ImeiValidator.MaxSerialLength} знаков (латиница, цифры, точка, подчёркивание).",
                    $"номер {ImeiValidator.MinSerialLength}–{ImeiValidator.MaxSerialLength} белгиден турушу керек (латын тамгалары, сандар, чекит, асты сызык).",
                    $"the number must be {ImeiValidator.MinSerialLength}–{ImeiValidator.MaxSerialLength} characters (Latin letters, digits, dot, underscore).",
                    $"numara {ImeiValidator.MinSerialLength}–{ImeiValidator.MaxSerialLength} karakter olmalı (Latin harfler, rakam, nokta, alt çizgi).",
                    $"raqam {ImeiValidator.MinSerialLength}–{ImeiValidator.MaxSerialLength} belgidan iborat bo'lishi kerak (lotin harflari, raqamlar, nuqta, pastki chiziq)."),
                ImeiValidator.Verdict.BadImei => Tr.T("IMEI не прошёл проверку (нужно 15 цифр, последняя — контрольная). Проверьте цифры.",
                    "IMEI текшерүүдөн өткөн жок (15 сан керек, акыркысы — текшерүүчү). Сандарды текшериңиз.",
                    "The IMEI failed the check (15 digits, the last one is a check digit). Check the digits.",
                    "IMEI kontrolü geçemedi (15 hane, sonuncusu kontrol hanesi). Rakamları kontrol edin.",
                    "IMEI tekshiruvdan o'tmadi (15 raqam, oxirgisi nazorat raqami). Raqamlarni tekshiring."),
                _ => null,
            };
            if (problem == null && (_inCart.Contains(value) || accepted.Contains(value)))
                problem = Tr.T("этот номер уже есть в чеке — у каждой штуки свой номер.",
                    "бул номер чекте бар — ар бир даананын өз номери бар.", "this number is already in the receipt — every unit has its own number.",
                    "bu numara fişte zaten var — her adedin kendi numarası olur.", "bu raqam chekda bor — har bir donaning o'z raqami bor.");
            if (problem != null)
            {
                _error.Text = label + problem;
                _error.IsVisible = true;
                _boxes[i].Focus();
                return;
            }
            accepted.Add(value);
        }

        Serials.Clear();
        Serials.AddRange(accepted);
        Close(true);
    }

    /// <summary>Только для проверок: вписать номера в поля по порядку.</summary>
    public void SetTexts(params string[] texts)
    {
        for (var i = 0; i < Math.Min(texts.Length, _boxes.Count); i++)
            _boxes[i].Text = texts[i];
    }

    public IReadOnlyList<TextBox> Boxes => _boxes;
    public TextBlock ErrorText => _error;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
