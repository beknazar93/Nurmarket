using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этапы П1–П2: «＋ Поставщик» и «Изменить» в разделе «Поставщики» — окно ModalShell с теми же полями, что
/// форма сайта NurCRM («Новый поставщик» → «Реквизиты и адрес»): название или ФИО и телефон (обязательно), email, ОсОО / компания,
/// ИНН, ОКПО, расчётный счёт, БИК, адрес. Новый — POST clients/ (type=suppliers), правка — PATCH только изменённых полей.
/// Такое же название уже есть — не создаём второй (как «+ Новый поставщик» карточки товара); такой же телефон — только подсказка.
/// Saved — сохранённый поставщик (null — окно закрыли без сохранения).</summary>
public sealed class SupplierEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly SupplierApiService _svc;
    private readonly SupplierInfo? _existing;
    private readonly IReadOnlyList<SupplierInfo> _all;
    private readonly TextBox _name;
    private readonly TextBox _phone;
    private readonly TextBox _email;
    private readonly TextBox _llc;
    private readonly TextBox _inn;
    private readonly TextBox _okpo;
    private readonly TextBox _score;
    private readonly TextBox _bik;
    private readonly TextBox _address;
    private readonly TextBlock _duplicate = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _cancel;
    private readonly Button _ok;
    private bool _saving;

    public SupplierInfo? Saved { get; private set; }

    public SupplierEditDialog(SupplierApiService svc, SupplierInfo? existing, IReadOnlyList<SupplierInfo> all)
    {
        _svc = svc;
        _existing = existing;
        _all = all;
        Title = existing is null
            ? T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi")
            : T("Реквизиты поставщика", "Жеткирүүчүнүн реквизиттери", "Supplier details", "Tedarikçi bilgileri", "Yetkazib beruvchi rekvizitlari");
        SectionCards.AsModal(this, 672);

        var panel = new StackPanel { Spacing = 10 };
        var main = Pair(panel);
        _name = Field(main, 0, 0, T("Название или ФИО *", "Аталышы же аты-жөнү *", "Name or company *", "Ad veya firma *", "Nomi yoki F.I.Sh. *"),
            T("Например: ОсОО «Шоро»", "Мисалы: ЖЧК «Шоро»", "E.g. Shoro LLC", "Örn.: Shoro Ltd.", "Masalan: «Shoro» MChJ"));
        _phone = Field(main, 0, 2, T("Телефон *", "Телефон *", "Phone *", "Telefon *", "Telefon *"), "+996 555 123 456");
        Use(_duplicate, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(_duplicate);

        var caption = new TextBlock
        {
            Text = T("Реквизиты и адрес — необязательно", "Реквизиттер жана дарек — милдеттүү эмес", "Bank details and address — optional", "Künye ve adres — isteğe bağlı",
                "Rekvizitlar va manzil — ixtiyoriy"),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0),
        };
        Use(caption, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(caption);
        var reqs = Pair(panel);
        _llc = Field(reqs, 0, 0, T("ОсОО / компания", "ЖЧК / компания", "Company (LLC)", "Şirket", "MChJ / kompaniya"), T("ОсОО «Пример»", "ЖЧК «Мисал»", "Example LLC", "Örnek Ltd.", "«Misol» MChJ"));
        _inn = Field(reqs, 0, 2, T("ИНН", "ИНН", "Tax ID (INN)", "Vergi no (INN)", "STIR (INN)"), "01234567890123");
        _okpo = Field(reqs, 1, 0, T("ОКПО", "ОКПО", "OKPO", "OKPO", "OKPO"), "12345678");
        _score = Field(reqs, 1, 2, T("Расчётный счёт", "Эсептешүү эсеби", "Bank account", "Banka hesabı", "Hisob raqami"), "1234567890123456");
        _bik = Field(reqs, 2, 0, T("БИК банка", "Банктын БИК", "Bank code (BIK)", "Banka kodu (BIK)", "Bank BIK"), "123456");
        _email = Field(reqs, 2, 2, "Email", "name@mail.kg");
        _address = Field(reqs, 3, 0, T("Адрес", "Дарек", "Address", "Adres", "Manzil"), T("Город, улица, дом", "Шаар, көчө, үй", "City, street, building", "Şehir, sokak, bina", "Shahar, ko'cha, uy"));
        if (_address.Parent is Control addressRow)
            Grid.SetColumnSpan(addressRow, 3);

        var errorBox = SectionCards.ErrorBox(this, _error);
        panel.Children.Add(errorBox);

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _phone.Text = existing.Phone;
            _email.Text = existing.Email;
            _llc.Text = existing.Llc;
            _inn.Text = existing.Inn;
            _okpo.Text = existing.Okpo;
            _score.Text = existing.Score;
            _bik.Text = existing.Bik;
            _address.Text = existing.Address;
        }

        _cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        _cancel.Click += (_, _) => { if (!_saving) Close(); };
        _ok = SectionCards.Primary(existing is null
            ? T("Добавить поставщика", "Жеткирүүчүнү кошуу", "Add supplier", "Tedarikçi ekle", "Yetkazib beruvchini qo'shish")
            : T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        _ok.IsDefault = true;
        _ok.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);

        var hint = new TextBlock
        {
            Text = T("* — обязательно. Поставщик общий с сайтом NurCRM (Клиенты → Поставщики) и приёмкой товара.",
                "* — милдеттүү. Жеткирүүчү NurCRM сайты (Кардарлар → Жеткирүүчүлөр) жана товар кабыл алуу менен жалпы.",
                "* — required. The supplier is shared with the NurCRM website (Clients → Suppliers) and stock receiving.",
                "* — zorunlu. Tedarikçi NurCRM sitesi (Müşteriler → Tedarikçiler) ve mal kabulüyle ortaktır.",
                "* — majburiy. Yetkazib beruvchi NurCRM sayti (Mijozlar → Yetkazib beruvchilar) va tovar qabuli bilan umumiy."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MinWidth = 140,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        Content = SectionCards.Shell(Title,
            T("Те же поля, что на сайте: название и телефон, ниже — реквизиты для накладных и акта сверки.",
                "Сайттагыдай талаалар: аталышы жана телефону, төмөндө — накладной жана салыштыруу актысы үчүн реквизиттер.",
                "The same fields as on the website: name and phone, below — details for invoices and reconciliation.",
                "Sitedeki alanların aynısı: ad ve telefon, altında — fatura ve mutabakat için künye.",
                "Saytdagi maydonlar: nomi va telefoni, pastda — yuk xati va solishtirish dalolatnomasi uchun rekvizitlar."),
            panel, SectionCards.Buttons(_cancel, _ok), hint);

        _name.TextChanged += (_, _) => UpdateState();
        _phone.TextChanged += (_, _) => UpdateState();
        UpdateState();
        Opened += (_, _) => _name.Focus();
        Closing += (_, e) =>
        {
            // Пока запрос идёт, окно не закрывается (✕/Esc) — иначе не узнать, сохранён ли поставщик.
            if (_saving)
                e.Cancel = true;
        };
    }

    private static Grid Pair(StackPanel panel)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        panel.Children.Add(g);
        return g;
    }

    private TextBox Field(Grid grid, int row, int col, string label, string watermark)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var box = UiKit.Input(this, watermark, 42);
        box.MaxLength = 200;
        Avalonia.Automation.AutomationProperties.SetName(box, label.TrimEnd('*', ' '));
        var cell = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8), Children = { caption, box } };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);
        grid.Children.Add(cell);
        return box;
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private SupplierInfo? SameName()
    {
        var key = PurchaseReceivingService.SupplierNameKey(_name.Text);
        return key.Length == 0 ? null : _all.FirstOrDefault(s => s.Id != _existing?.Id && PurchaseReceivingService.SupplierNameKey(s.Name) == key);
    }

    private void UpdateState()
    {
        _ok.IsEnabled = !_saving && !string.IsNullOrWhiteSpace(_name.Text) && !string.IsNullOrWhiteSpace(_phone.Text);
        _error.IsVisible = false;
        // Такой номер уже есть (последние 9 цифр) — подсказка, не запрет: у двух поставщиков бывает один агент.
        var digits = Digits(_phone.Text);
        var same = digits.Length >= 9
            ? _all.FirstOrDefault(s => s.Id != _existing?.Id && Digits(s.Phone) is { Length: >= 9 } d && d[^9..] == digits[^9..])
            : null;
        _duplicate.IsVisible = same is not null;
        if (same is not null)
            _duplicate.Text = T($"Поставщик с этим номером уже есть: {same.Name}.", $"Бул номер менен жеткирүүчү бар: {same.Name}.", $"A supplier with this number already exists: {same.Name}.",
                $"Bu numarayla bir tedarikçi zaten var: {same.Name}.", $"Bu raqamli yetkazib beruvchi allaqachon bor: {same.Name}.");
    }

    private async Task SaveAsync()
    {
        if (_saving || string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_phone.Text))
            return;
        if (SameName() is { } same)
        {
            ShowError(T($"Такой поставщик уже есть: {same.Name}. Откройте его в списке — второй не создаётся.", $"Мындай жеткирүүчү бар: {same.Name}. Аны тизмеден ачыңыз — экинчиси түзүлбөйт.",
                $"This supplier already exists: {same.Name}. Open it in the list — a second one isn't created.", $"Bu tedarikçi zaten var: {same.Name}. Listeden açın — ikincisi oluşturulmaz.",
                $"Bunday yetkazib beruvchi allaqachon bor: {same.Name}. Uni ro'yxatdan oching — ikkinchisi yaratilmaydi."));
            return;
        }
        var r = new SupplierRequisites(_name.Text ?? "", _phone.Text ?? "", _email.Text ?? "", _llc.Text ?? "", _inn.Text ?? "", _okpo.Text ?? "", _score.Text ?? "",
            _bik.Text ?? "", _address.Text ?? "");
        _saving = true;
        _ok.IsEnabled = false;
        _cancel.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var saved = _existing is null ? await _svc.CreateAsync(r, cts.Token).ConfigureAwait(true) : await _svc.UpdateAsync(_existing, r, cts.Token).ConfigureAwait(true);
            if (saved is null)
                throw new InvalidOperationException(T("сервер не вернул поставщика — обновите список", "сервер жеткирүүчүнү кайтарган жок — тизмени жаңыртыңыз",
                    "the server didn't return the supplier — refresh the list", "sunucu tedarikçiyi döndürmedi — listeyi yenileyin", "server yetkazib beruvchini qaytarmadi — ro'yxatni yangilang"));
            Saved = saved;
            _saving = false;
            Close();
        }
        catch (Exception ex)
        {
            _saving = false;
            _cancel.IsEnabled = true;
            UpdateState();
            ShowError(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ")
                      + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi")
                          : ex is InvalidOperationException ? ex.Message : ServerTelegramBotApi.DescribeFields(ex)));
            PosLogger.Log($"Поставщики: поставщик не сохранён ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
