using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): карточка «Налоги и ККМ» в «Настройках → Операции» — только владельцу и администратору, только когда сервер знает фискализацию (признак fiscal_kassa).
/// Показывает режим (его задаёт сервер: выключено / пробивает касса / пробивает сервер), статус ККМ (режим server — «ККМ зарегистрирована на сервере» со сведениями с сервера или «отдельная ККМ в кассе не нужна»;
/// «ожидается документация Telemedia» — только когда пробивает касса, а локальной ККМ нет) и даёт править налоговую форму:
/// «Фискализация включена», режим налогообложения, «Плательщик НДС», ставки НДС, ставка НсП. Правки уходят на сервер (PUT settings/fiscal/) ТОЛЬКО по кнопке «Сохранить»;
/// ничего не отправляется при открытии, при вводе и при потере фокуса. Деньги — decimal (проценты, не суммы).</summary>
public sealed class FiscalSettingsCard : Border
{
    private readonly TextBlock _mode = Text("FiscalModeText", semi: true);
    private readonly TextBlock _provider = Text("FiscalProviderText");
    // 2026-11-20: сведения о ККМ, зарегистрированной на сервере (режим server): ЗН, РН, ФМ, срок ФМ, ОФД, смена, последний чек — только то, что сервер отдал.
    private readonly Grid _kkmDetails = new() { Name = "FiscalKkmDetails", ColumnDefinitions = new ColumnDefinitions("Auto,*"), IsVisible = false };
    private readonly CheckBox _enabled = new() { Name = "FiscalEnabledCheck" };
    private readonly TextBox _regime = new() { Name = "FiscalRegimeBox", MaxLength = 32 };
    private readonly CheckBox _vatPayer = new() { Name = "FiscalVatPayerCheck" };
    private readonly TextBox _rates = new() { Name = "FiscalRatesBox", MaxLength = 80 };
    private readonly TextBox _nsp = new() { Name = "FiscalNspBox", MaxLength = 8 };
    private readonly TextBlock _result = new() { Name = "FiscalSaveResult", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private bool _edited;
    private bool _filling;
    private bool _saving;

    /// <summary>Только для тестов и снимков: показывать карточку независимо от прав и режима.</summary>
    public bool ForceVisible { get; set; }

    /// <summary>Только для тестов: вместо настоящего PUT (проверка, что без «Сохранить» ничего не уходит, и что уходит по нажатию).</summary>
    public Func<FiscalSettingsForm, Task<FiscalCapabilities>>? SaveOverride { get; set; }

    /// <summary>Только для тестов: не читать настройки с сервера при показе.</summary>
    public bool SkipServerRead { get; set; }

    protected override Type StyleKeyOverride => typeof(Border);

    public FiscalSettingsCard()
    {
        Name = "FiscalSettingsCard";
        Classes.Add("SettingsCard");
        Padding = new Thickness(16);
        var stack = new StackPanel { Spacing = 10 };
        var title = new TextBlock { Name = "FiscalCardTitle", Classes = { "SettingsCardTitle" }, FontSize = 14,
            Text = Tr.T("Налоги и ККМ", "Салыктар жана ККМ", "Taxes and cash register", "Vergiler ve ÖKC", "Soliqlar va KKM") };
        stack.Children.Add(title);
        stack.Children.Add(Soft(Tr.T(
            "Режим фискализации задаёт сервер NurCRM. Здесь можно посмотреть его и задать налоговые данные магазина — они попадают в фискальный чек. Правки уходят на сервер только по кнопке «Сохранить».",
            "Фискалдаштыруу режимин NurCRM сервери белгилейт. Бул жерден аны көрүп, дүкөндүн салык маалыматтарын киргизсеңиз болот — алар фискалдык чекке кирет. Өзгөртүүлөр «Сактоо» баскычы менен гана серверге кетет.",
            "The NurCRM server sets the fiscalization mode. Here you can view it and enter the shop's tax data — it goes into the fiscal receipt. Changes are sent to the server only with the “Save” button.",
            "Mali fiş modunu NurCRM sunucusu belirler. Burada onu görebilir ve mağazanın vergi bilgilerini girebilirsiniz — mali fişe girer. Değişiklikler yalnızca «Kaydet» düğmesiyle sunucuya gider.",
            "Fiskallashtirish rejimini NurCRM serveri belgilaydi. Bu yerda uni ko'rish va do'konning soliq ma'lumotlarini kiritish mumkin — ular fiskal chekka tushadi. O'zgarishlar faqat «Saqlash» tugmasi bilan serverga ketadi.")));

        stack.Children.Add(Row(Tr.T("Режим", "Режим", "Mode", "Mod", "Rejim"), _mode));
        stack.Children.Add(Row(Tr.T("ККМ", "ККМ", "Cash register", "ÖKC", "KKM"), _provider));
        _kkmDetails.Margin = new Thickness(0, 0, 0, 2);
        stack.Children.Add(_kkmDetails);

        Toggle(_enabled, Tr.T("Фискализация включена", "Фискалдаштыруу күйгүзүлгөн", "Fiscalization is on", "Mali fiş açık", "Fiskallashtirish yoqilgan"));
        stack.Children.Add(_enabled);
        Toggle(_vatPayer, Tr.T("Плательщик НДС", "ЧКС төлөөчү", "VAT payer", "KDV mükellefi", "QQS to'lovchisi"));
        stack.Children.Add(_vatPayer);

        Field(_regime, "", 320);
        stack.Children.Add(Labeled(_regime, Tr.T("Режим налогообложения (до 32 знаков)", "Салык салуу режими (32 белгиге чейин)", "Tax regime (up to 32 characters)", "Vergi rejimi (en çok 32 karakter)", "Soliq rejimi (32 belgigacha)")));
        Field(_rates, "0; 12", 320);
        stack.Children.Add(Labeled(_rates, Tr.T("Ставки НДС, % (через «;», например 0; 12)", "ЧКС ставкалары, % («;» менен, мисалы 0; 12)", "VAT rates, % (separated by “;”, e.g. 0; 12)",
            "KDV oranları, % («;» ile, örn. 0; 12)", "QQS stavkalari, % («;» bilan, masalan 0; 12)")));
        Field(_nsp, "2", 120);
        stack.Children.Add(Labeled(_nsp, Tr.T("Ставка НсП, % (налог с продаж)", "СтН ставкасы, % (сатуудан салык)", "Sales tax rate, %", "Satış vergisi oranı, %", "Savdo solig'i stavkasi, %")));

        _save = new Button { Name = "FiscalSaveButton", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 44, Padding = new Thickness(20, 0),
            Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash") };
        _save.Classes.Add("nur-primary");
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        stack.Children.Add(_save);
        _result.Bind(TextBlock.FontSizeProperty, _result.GetResourceObservable("NurFontCaption"));
        stack.Children.Add(_result);
        Child = stack;

        foreach (var box in new[] { _regime, _rates, _nsp })
            box.TextChanged += (_, _) => MarkEdited();
        _enabled.IsCheckedChanged += (_, _) => MarkEdited();
        _vatPayer.IsCheckedChanged += (_, _) => MarkEdited();

        Fill(FiscalSettings.Current);
        UpdateVisibility();
        AttachedToVisualTree += async (_, _) =>
        {
            UpdateVisibility();
            if (IsVisible && !SkipServerRead)
                await ReloadAsync().ConfigureAwait(true);
        };
    }

    private void UpdateVisibility() => IsVisible = ForceVisible || ((FiscalSettings.Current.FeatureAvailable || FiscalSettings.ServerFiscalizes) && ActionGuard.CanChangeProtection());

    private void MarkEdited()
    {
        if (!_filling)
            _edited = true;
    }

    /// <summary>Текущая форма (для тестов).</summary>
    public FiscalSettingsForm? ReadForm(out string? error)
    {
        var (rates, ratesError) = FiscalFormRules.ParseRates(_rates.Text);
        var (nsp, nspError) = FiscalFormRules.ParseOptionalPercent(_nsp.Text);
        error = ratesError ?? nspError;
        if (error is not null)
            return null;
        var form = new FiscalSettingsForm(_enabled.IsChecked == true, (_regime.Text ?? "").Trim(), _vatPayer.IsChecked == true, rates, nsp);
        error = FiscalApi.ValidateForm(form);
        return error is null ? form : null;
    }

    /// <summary>Только для тестов и снимков: показать заданные настройки (режим, сведения о ККМ) без сервера и без записи на диск.</summary>
    public void ShowForPreview(FiscalCapabilities caps) => Fill(caps);

    private void Fill(FiscalCapabilities caps)
    {
        _filling = true;
        try
        {
            FillStatus(caps);
            _enabled.IsChecked = caps.FiscalEnabled;
            _regime.Text = caps.TaxRegime;
            _vatPayer.IsChecked = caps.VatPayer;
            _rates.Text = FiscalFormRules.FormatRates(caps.Rates);
            _nsp.Text = caps.NspRate is { } n ? FiscalFormRules.FormatPercent(n) : "";
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>2026-11-20: режим и строка «ККМ» (не поля формы — их не трогаем, пока владелец правит). В режиме server «ККМ» — состояние с сервера, а не локальной заглушки.</summary>
    private void FillStatus(FiscalCapabilities caps)
    {
        var kkm = (caps.Kkm ?? FiscalSettings.Current.Kkm) is { HasData: true } k ? k : null;
        _mode.Text = FiscalFormRules.ModeText(caps.Mode);
        _provider.Text = FiscalFormRules.KkmStatusText(caps with { Kkm = kkm }, FiscalProviders.Current);
        _kkmDetails.Children.Clear();
        _kkmDetails.RowDefinitions.Clear();
        var rows = caps.Mode == FiscalMode.Server && kkm is not null ? kkm.Rows().ToList() : new List<(string Key, string Label, string Value)>();
        for (var i = 0; i < rows.Count; i++)
        {
            _kkmDetails.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Soft(rows[i].Label);
            label.MinWidth = 90;
            label.Margin = new Thickness(0, 2, 12, 2);
            label.VerticalAlignment = VerticalAlignment.Top;
            var value = Text("FiscalKkm" + rows[i].Key + "Text");
            value.Text = rows[i].Value;
            value.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _kkmDetails.Children.Add(label);
            _kkmDetails.Children.Add(value);
        }
        _kkmDetails.IsVisible = rows.Count > 0;
    }

    /// <summary>Прочитать настройки с сервера (GET, ничего не меняет). Правки владельца, пока он их не сохранил, не затираются.</summary>
    public async Task ReloadAsync()
    {
        if (NurCrmReportsApi.ClientResolver?.Invoke() is not { } api)
            return;
        try
        {
            var caps = await FiscalApi.GetSettingsAsync(api).ConfigureAwait(true);
            var shown = caps with { FeatureAvailable = FiscalSettings.Current.FeatureAvailable };
            if (!_edited)
                Fill(shown);
            else
                FillStatus(shown); // 2026-11-20: правки владельца не затираем, но режим и «ККМ» обновляем
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Налоги и ККМ: настройки не прочитаны ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Единственное место, где настройки уходят на сервер — кнопка «Сохранить».</summary>
    public async Task SaveAsync()
    {
        if (_saving)
            return;
        var form = ReadForm(out var error);
        if (form is null)
        {
            ShowResult(error ?? "", ok: false);
            return;
        }
        _saving = true;
        _save.IsEnabled = false;
        try
        {
            FiscalCapabilities saved;
            if (SaveOverride is not null)
                saved = await SaveOverride(form).ConfigureAwait(true);
            else if (NurCrmReportsApi.ClientResolver?.Invoke() is { } api)
                saved = await FiscalApi.PutSettingsAsync(api, form).ConfigureAwait(true);
            else
                throw new ApiException(Tr.T("Нет входа в NurCRM — войдите заново.", "NurCRMге кирген жоксуз — кайра кириңиз.", "You are not signed in to NurCRM — sign in again.",
                    "NurCRM'e giriş yapılmadı — yeniden girin.", "NurCRMga kirilmagan — qayta kiring."), 401);
            _edited = false;
            Fill(saved);
            ShowResult(Tr.T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), ok: true);
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            ShowResult(Tr.T("Недостаточно прав: налоги и ККМ меняет только владелец или администратор.", "Укук жетишсиз: салыктарды жана ККМди ээси же администратор гана өзгөртөт.",
                "Not enough rights: only the owner or an administrator can change taxes and the cash register.", "Yetki yetersiz: vergileri ve ÖKC'yi yalnızca sahip veya yönetici değiştirebilir.",
                "Huquq yetarli emas: soliqlar va KKMni faqat egasi yoki administrator o'zgartiradi."), ok: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Налоги и ККМ: не сохранено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowResult(Tr.T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerOutageMonitor.UserText(ex), ok: false);
        }
        finally
        {
            _saving = false;
            _save.IsEnabled = true;
        }
    }

    private void ShowResult(string text, bool ok)
    {
        _result.Text = text;
        _result.IsVisible = text.Length > 0;
        _result.Bind(TextBlock.ForegroundProperty, _result.GetResourceObservable(ok ? "BrushSuccess" : "BrushDanger"));
    }

    // ── разметка ──

    private static TextBlock Text(string name, bool semi = false)
    {
        var t = new TextBlock { Name = name, TextWrapping = TextWrapping.Wrap, FontWeight = semi ? FontWeight.SemiBold : FontWeight.Normal };
        t.Bind(TextBlock.FontSizeProperty, t.GetResourceObservable("NurFontBody"));
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.FontSizeProperty, t.GetResourceObservable("NurFontCaption"));
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static Control Row(string label, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var l = Soft(label);
        l.MinWidth = 90;
        l.Margin = new Thickness(0, 0, 12, 0);
        l.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(value, 1);
        grid.Children.Add(l);
        grid.Children.Add(value);
        return grid;
    }

    private static void Toggle(CheckBox box, string text)
    {
        box.Classes.Add("ToggleSwitch");
        box.Content = text;
        Avalonia.Automation.AutomationProperties.SetName(box, text);
    }

    private static void Field(TextBox box, string watermark, double width)
    {
        box.Classes.Add("modal-field");
        box.MinHeight = 44;
        box.Width = width;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Watermark = watermark;
    }

    private static Control Labeled(TextBox box, string label)
    {
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Soft(label));
        stack.Children.Add(box);
        return stack;
    }
}
