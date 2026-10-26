using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-19, этап О8 (редизайн «Клиентов», жалоба владельца): «Новый клиент» — окно ModalShell по кнопке «+ Клиент»
/// вместо формы, которая всегда висела над списком. Поля прежние (имя и телефон обязательны, адрес и email — нет); сохраняет
/// раздел «Клиенты» прежним запросом (save возвращает null — добавлен, иначе текст ошибки, окно остаётся открытым). Если клиент
/// с таким телефоном уже есть — предупреждение (не запрет: у семьи бывает один номер).</summary>
public sealed class NewClientDialog : Window
{
    private readonly Func<string, string, string, string, Task<string?>> _save;
    private readonly IReadOnlyList<ClientRow> _existing;
    private readonly TextBox _name;
    private readonly TextBox _phone;
    private readonly TextBox _address;
    private readonly TextBox _email;
    // 2026-10-21, документы клиента: «+ Новый клиент» из КП/счёта — ещё компания и ИНН (поля клиента NurCRM llc и inn).
    private readonly TextBox? _company;
    private readonly TextBox? _inn;
    private readonly bool _supplier;
    // 2026-10-23: «Выбрать существующего» при дубле телефона (аудит, круг 6); null — кнопки нет.
    private readonly Action<ClientRow>? _pickExisting;
    private readonly Button _pickExistingButton = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, IsVisible = false };
    private ClientRow? _sameRow;
    // 2026-10-24 (аудит, круг 8, С-6): как в кассе — дубль телефона красным, «Добавить клиента» выключена до «Всё равно добавить».
    private readonly Button _addAnywayButton = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    private readonly StackPanel _duplicateButtons = new() { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false };
    private bool _forceAdd;

    /// <summary>Компания и ИНН (только при companyFields) — читает сохранение.</summary>
    public string Company => (_company?.Text ?? "").Trim();
    public string Inn => (_inn?.Text ?? "").Trim();
    private readonly TextBlock _duplicate = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Border _errorBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false };
    private readonly Button _cancel;
    private readonly Button _ok;
    private bool _saving;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <param name="supplier">2026-10-20, владелец: «+ Новый поставщик» в карточке товара — то же окно, только имя и телефон
    /// (адрес и email не показываются), подписи про поставщика.</param>
    /// <param name="subtitle">2026-10-24, ТЗ «Склад» (В): своя подпись под заголовком (у поставщика из приёмки «Он будет выбран в приёмке»); null — прежняя.</param>
    public NewClientDialog(Func<string, string, string, string, Task<string?>> save, IReadOnlyList<ClientRow> existing, bool supplier = false, bool companyFields = false, Action<ClientRow>? pickExisting = null, string? subtitle = null)
    {
        _pickExisting = pickExisting;
        _save = save;
        _existing = existing;
        _supplier = supplier;
        Title = supplier
            ? T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi")
            : T("Новый клиент", "Жаңы кардар", "New client", "Yeni müşteri", "Yangi mijoz");
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Use(this, BackgroundProperty, "DialogWindowBackground");
        Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");

        var root = new StackPanel { Spacing = 12 };
        _name = supplier
            ? Field(root, T("Название или ФИО *", "Аталышы же аты-жөнү *", "Name or company *", "Ad veya firma *", "Nomi yoki F.I.Sh. *"),
                T("Например: ОсОО «Шоро»", "Мисалы: ЖЧК «Шоро»", "E.g. Shoro LLC", "Örn.: Shoro Ltd.", "Masalan: «Shoro» MChJ"))
            : Field(root, T("Имя / ФИО *", "Аты / аты-жөнү *", "Name *", "Ad soyad *", "Ism / F.I.Sh. *"),
                T("Например: Айгерим Садыкова", "Мисалы: Айгерим Садыкова", "E.g. Aigerim Sadykova", "Örn.: Aigerim Sadykova", "Masalan: Aigerim Sadikova"));
        _phone = Field(root, T("Телефон *", "Телефон *", "Phone *", "Telefon *", "Telefon *"), "+996 555 123 456");
        Use(_duplicate, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(_duplicate);
        _pickExistingButton.Classes.Add("modal-secondary");
        _pickExistingButton.Content = T("Выбрать существующего", "Бар кардарды тандоо", "Select the existing one", "Mevcut olanı seç", "Mavjudini tanlash");
        _pickExistingButton.Click += (_, _) =>
        {
            if (_sameRow is { } row && _pickExisting != null)
            {
                _pickExisting(row);
                Close();
            }
        };
        // 2026-10-24 (С-6): «Всё равно добавить» — рядом с «Выбрать существующего»; включает «Добавить клиента» до следующей правки телефона.
        _addAnywayButton.Classes.Add("modal-secondary");
        _addAnywayButton.Content = T("Всё равно добавить", "Баары бир кошуу", "Add anyway", "Yine de ekle", "Baribir qo'shish");
        _addAnywayButton.Click += (_, _) => { _forceAdd = true; UpdateState(); };
        _duplicateButtons.Children.Add(_pickExistingButton);
        _duplicateButtons.Children.Add(_addAnywayButton);
        root.Children.Add(_duplicateButtons);
        if (companyFields && !supplier)
        {
            _company = Field(root, T("Компания (необязательно)", "Компания (милдеттүү эмес)", "Company (optional)", "Şirket (isteğe bağlı)", "Kompaniya (ixtiyoriy)"),
                T("Например: ОсОО «Шоро» — пойдёт в документ", "Мисалы: ЖЧК «Шоро» — документке кирет", "E.g. Shoro LLC — goes into the document",
                    "Örn.: Shoro Ltd. — belgeye yazılır", "Masalan: «Shoro» MChJ — hujjatga yoziladi"));
            _inn = Field(root, T("ИНН (необязательно)", "ИНН (милдеттүү эмес)", "TIN (optional)", "VKN (isteğe bağlı)", "STIR (ixtiyoriy)"), "01234567890123");
            _inn.MaxLength = 20;
        }
        _address = Field(root, T("Адрес (необязательно)", "Дарек (милдеттүү эмес)", "Address (optional)", "Adres (isteğe bağlı)", "Manzil (ixtiyoriy)"),
            T("Нужен для долгов: где найти клиента", "Карыздар үчүн керек: кардарды кайдан табуу", "Useful for debts: where to find the client",
                "Borçlar için: müşteriyi nerede bulacağınız", "Qarzlar uchun kerak: mijozni qayerdan topish"));
        _email = Field(root, T("Email (необязательно)", "Email (милдеттүү эмес)", "Email (optional)", "E-posta (isteğe bağlı)", "Email (ixtiyoriy)"), "name@mail.kg");
        // 2026-10-20: у поставщика из карточки товара — только имя и телефон (короткое окно).
        if (supplier && _address.Parent is Control addressRow && _email.Parent is Control emailRow)
            addressRow.IsVisible = emailRow.IsVisible = false;

        Use(_error, TextBlock.ForegroundProperty, "BrushDangerOnSoft");
        Use(_errorBox, Border.BackgroundProperty, "BrushDangerSoft");
        _errorBox.Child = _error;
        root.Children.Add(_errorBox);

        var hint = new TextBlock
        {
            Text = supplier
                ? T("* — обязательно. Поставщик появится в NurCRM (Клиенты → Поставщики) и в приёмке товара.",
                    "* — милдеттүү. Жеткирүүчү NurCRMде (Кардарлар → Жеткирүүчүлөр) жана товар кабыл алууда пайда болот.",
                    "* — required. The supplier will appear in NurCRM (Clients → Suppliers) and in stock receiving.",
                    "* — zorunlu. Tedarikçi NurCRM'de (Müşteriler → Tedarikçiler) ve mal kabulünde görünecek.",
                    "* — majburiy. Yetkazib beruvchi NurCRM'da (Mijozlar → Yetkazib beruvchilar) va tovar qabulida paydo bo'ladi.")
                : T("* — обязательно. Клиента потом можно выбрать при продаже: бонусы и продажа в долг.",
                "* — милдеттүү. Кийин кардарды сатууда тандаса болот: бонустар жана карызга сатуу.",
                "* — required. You can then pick the client at checkout: bonuses and selling on credit.",
                "* — zorunlu. Müşteriyi sonra satışta seçebilirsiniz: bonuslar ve veresiye satış.",
                "* — majburiy. Keyin mijozni sotuvda tanlash mumkin: bonuslar va qarzga sotish."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MinWidth = 140,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        _cancel = new Button { Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        _cancel.Classes.Add("modal-cancel");
        _cancel.Click += (_, _) => { if (!_saving) Close(); };
        _ok = new Button
        {
            Content = supplier
                ? T("Добавить поставщика", "Жеткирүүчүнү кошуу", "Add supplier", "Tedarikçi ekle", "Yetkazib beruvchini qo'shish")
                : T("Добавить клиента", "Кардарды кошуу", "Add client", "Müşteri ekle", "Mijozni qo'shish"),
            IsDefault = true, IsEnabled = false,
        };
        _ok.Classes.Add("modal-primary");
        _ok.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        foreach (var b in new[] { _cancel, _ok })
            Avalonia.Automation.AutomationProperties.SetName(b, b.Content as string ?? "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _ok } };
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title,
            Subtitle = subtitle ?? (supplier
                ? T("Он сразу будет выбран поставщиком этого товара.", "Ал дароо ушул товардын жеткирүүчүсү болуп тандалат.",
                    "It will be selected as this product's supplier right away.", "Hemen bu ürünün tedarikçisi olarak seçilecek.",
                    "U darhol shu mahsulotning yetkazib beruvchisi sifatida tanlanadi.")
                : T("Имя и телефон — чтобы найти клиента в кассе и напомнить о долге.", "Аты жана телефону — кардарды кассадан табуу жана карызды эскертүү үчүн.",
                "Name and phone — to find the client at the till and remind them of a debt.", "Ad ve telefon — müşteriyi kasada bulmak ve borcu hatırlatmak için.",
                "Ism va telefon — mijozni kassada topish va qarzni eslatish uchun.")),
            Footer = buttons, Hint = hint, Content = root,
        };

        _name.TextChanged += (_, _) => UpdateState();
        _phone.TextChanged += (_, _) => { _forceAdd = false; UpdateState(); }; // 2026-10-24 (С-6): «Всё равно» действует до правки телефона
        Opened += (_, _) => _name.Focus();
        Closing += (_, e) =>
        {
            // Пока запрос идёт, окно не закрывается (✕/Esc) — иначе не узнать, добавлен ли клиент.
            if (_saving)
                e.Cancel = true;
        };
    }

    private TextBox Field(StackPanel root, string label, string watermark)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var box = UiKit.Input(this, watermark, 44);
        box.MaxLength = 200;
        Avalonia.Automation.AutomationProperties.SetName(box, label.TrimEnd('*', ' '));
        root.Children.Add(new StackPanel { Spacing = 4, Children = { caption, box } });
        return box;
    }

    private void UpdateState()
    {
        _errorBox.IsVisible = false;
        // Такой номер уже есть — подсказка с именами (сравниваем последние 9 цифр: «0555…» и «+996 555…» — один номер).
        var digits = Digits(_phone.Text);
        // 2026-10-24 (аудит, круг 8, С-6): совпавших может быть несколько — показываем всех, а не первого.
        var sameAll = digits.Length >= 9 ? _existing.Where(c => Digits(c.Phone) is { Length: >= 9 } d && d[^9..] == digits[^9..]).ToList() : new List<ClientRow>();
        var same = sameAll.FirstOrDefault();
        var names = string.Join(", ", sameAll.Select(c => c.FullName));
        _duplicate.IsVisible = same != null;
        _sameRow = same;
        // Клиент с дублем номера: «Добавить клиента» выключена, пока не нажали «Всё равно добавить» (у поставщика прежнее предупреждение).
        var blocked = same != null && !_supplier && !_forceAdd;
        _ok.IsEnabled = !_saving && !string.IsNullOrWhiteSpace(_name.Text) && !string.IsNullOrWhiteSpace(_phone.Text) && !blocked;
        // 2026-10-24, ТЗ «Склад» (В): у поставщика «Выбрать существующего» — если окно открыто с pickExisting (приёмка); «Всё равно добавить» не нужно — поставщика можно добавить и так.
        _duplicateButtons.IsVisible = same != null && (!_supplier || _pickExisting != null);
        _pickExistingButton.IsVisible = same != null && _pickExisting != null;
        _addAnywayButton.IsVisible = same != null && !_supplier && !_forceAdd;
        Use(_duplicate, TextBlock.ForegroundProperty, same != null && !_supplier ? "BrushDanger" : "BrushText");
        if (same != null && _supplier)
            _duplicate.Text = T($"Поставщик с этим номером уже есть: {same.FullName}. Его можно выбрать в списке.",
                $"Бул номер менен жеткирүүчү бар: {same.FullName}. Аны тизмеден тандаса болот.",
                $"A supplier with this number already exists: {same.FullName}. You can pick it from the list.",
                $"Bu numarayla bir tedarikçi zaten var: {same.FullName}. Listeden seçebilirsiniz.",
                $"Bu raqamli yetkazib beruvchi allaqachon bor: {same.FullName}. Uni ro'yxatdan tanlash mumkin.");
        else if (same != null)
            _duplicate.Text = T($"Клиент с этим номером уже есть: {names}.",
                $"Бул номер менен кардар бар: {names}.",
                $"A client with this number already exists: {names}.",
                $"Bu numarayla bir müşteri zaten var: {names}.",
                $"Bu raqamli mijoz allaqachon bor: {names}.");
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private async Task SaveAsync()
    {
        if (_saving || string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_phone.Text))
            return;
        _saving = true;
        _ok.IsEnabled = false;
        _cancel.IsEnabled = false;
        _errorBox.IsVisible = false;
        string? error;
        try
        {
            error = await _save(_name.Text!.Trim(), _phone.Text!.Trim(), (_email.Text ?? "").Trim(), (_address.Text ?? "").Trim()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        _saving = false;
        _cancel.IsEnabled = true;
        if (error is null)
        {
            Close();
            return;
        }
        _error.Text = error;
        _errorBox.IsVisible = true;
        UpdateState();
        _errorBox.IsVisible = true;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
