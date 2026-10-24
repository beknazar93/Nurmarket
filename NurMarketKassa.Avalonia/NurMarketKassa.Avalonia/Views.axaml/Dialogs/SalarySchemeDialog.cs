using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>«Зарплата по продажам» — схема начисления одного сотрудника (2026-09-25), как окно
/// сайта MarketSaleEmployeePayProfileModal: оклад / процент от продаж / оклад + процент, те же
/// проверки и тот же запрос (POST новый профиль, PATCH существующий). Результат ShowDialog —
/// true, когда схема сохранена на сервере.</summary>
public sealed class SalarySchemeDialog : Window
{
    private readonly string _userId;
    private readonly RadioButton _salaryRadio;
    private readonly RadioButton _percentRadio;
    private readonly RadioButton _bothRadio;
    // 2026-10-05, владелец: «добавь в зарплату фиксированную стоимость от продаж, например за каждый проданный товар 10 сом»
    // (сервер добавил по ТЗ ч.13, п. 3: per_item_amount и схемы per_item / salary_plus_per_item / percent_plus_per_item).
    private readonly RadioButton _perItemRadio;
    private readonly RadioButton _salaryPerItemRadio;
    private readonly RadioButton _percentPerItemRadio;
    private readonly StackPanel _perItemPanel;
    private readonly TextBox _perItemBox;
    private readonly StackPanel _monthlyPanel;
    private readonly StackPanel _percentPanel;
    private readonly TextBox _monthlyBox;
    private readonly TextBox _percentBox;
    private readonly TextBlock _statusText;
    private readonly TextBlock _errorText;
    private readonly Button _saveButton;
    private string? _profileId;

    public SalarySchemeDialog(string userId, string employeeName)
    {
        _userId = userId;
        Title = Tr.T("Зарплата по продажам", "Сатуу боюнча эмгек акы", "Sales-based pay", "Satışa göre maaş", "Sotuv bo'yicha ish haqi");
        // 2026-10-21: общая рамка модалок ModalShell, как у остальных окон — без системной рамки Windows с цветной
        // полосой; карточка 480 + поля 2×16 под тень (пакет 3, п. 136).
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        // 2026-10-21: отмеченный кружок — цветом текста темы (тёмно-синий), а не системным бирюзовым Fluent (пакет 3, п. 136).
        if (Application.Current?.FindResource("BrushText") is IBrush checkedBrush)
        {
            foreach (var key in new[]
                     {
                         "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedFill",
                         "RadioButtonOuterEllipseCheckedStrokePointerOver", "RadioButtonOuterEllipseCheckedFillPointerOver",
                         "RadioButtonOuterEllipseCheckedStrokePressed", "RadioButtonOuterEllipseCheckedFillPressed",
                     })
                Resources[key] = checkedBrush;
        }
        if (Application.Current?.FindResource("BrushPanel") is IBrush glyphBrush)
        {
            Resources["RadioButtonCheckGlyphFill"] = glyphBrush;
            Resources["RadioButtonCheckGlyphFillPointerOver"] = glyphBrush;
            Resources["RadioButtonCheckGlyphFillPressed"] = glyphBrush;
        }

        // 2026-10-21: заголовок и имя сотрудника — в шапке ModalShell (пакет 3, п. 136).
        var panel = new StackPanel { Spacing = 10 };

        _statusText = new TextBlock
        {
            Text = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…"),
            FontSize = 12,
        };
        _statusText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(_statusText);

        panel.Children.Add(Label(Tr.T("Схема начисления", "Эсептөө схемасы", "Pay scheme", "Hesaplama şeması", "Hisoblash sxemasi")));
        _salaryRadio = SchemeRadio(SalaryWindow.SchemeName("salary"));
        _percentRadio = SchemeRadio(SalaryWindow.SchemeName("percent"));
        _bothRadio = SchemeRadio(SalaryWindow.SchemeName("salary_plus_percent"));
        _bothRadio.IsChecked = true;
        panel.Children.Add(_salaryRadio);
        panel.Children.Add(_percentRadio);
        panel.Children.Add(_bothRadio);
        _perItemRadio = SchemeRadio(SalaryWindow.SchemeName("per_item"));
        _salaryPerItemRadio = SchemeRadio(SalaryWindow.SchemeName("salary_plus_per_item"));
        _percentPerItemRadio = SchemeRadio(SalaryWindow.SchemeName("percent_plus_per_item"));
        panel.Children.Add(_perItemRadio);
        panel.Children.Add(_salaryPerItemRadio);
        panel.Children.Add(_percentPerItemRadio);

        _monthlyBox = SectionCards.Field(new TextBox { Watermark = "0" }); // 2026-10-22 (модалки, пакет 4): поля 44 px
        _monthlyPanel = new StackPanel { Spacing = 4 };
        _monthlyPanel.Children.Add(Label(Tr.T("Оклад в месяц, сом", "Айлык маяна, сом", "Monthly salary, som", "Aylık maaş, som", "Oylik maosh, so'm")));
        _monthlyPanel.Children.Add(_monthlyBox);
        panel.Children.Add(_monthlyPanel);

        _percentBox = SectionCards.Field(new TextBox { Watermark = "0" });
        _percentPanel = new StackPanel { Spacing = 4 };
        _percentPanel.Children.Add(Label(Tr.T("Процент от личных продаж, %", "Жеке сатуудан пайыз, %", "Percentage of own sales, %",
            "Kişisel satış yüzdesi, %", "Shaxsiy sotuvdan foiz, %")));
        _percentPanel.Children.Add(_percentBox);
        _percentPanel.Children.Add(SoftHint(new TextBlock
        {
            Text = Tr.T(
                "Этот же процент подставляется консультанту в окне оплаты.",
                "Ушул эле пайыз төлөм терезесинде консультантка коюлат.",
                "The same percentage is suggested for the consultant in the payment window.",
                "Aynı yüzde ödeme penceresinde danışmana otomatik olarak girilir.",
                "Xuddi shu foiz to'lov oynasida maslahatchiga qo'yiladi."),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        }));
        panel.Children.Add(_percentPanel);

        _perItemBox = SectionCards.Field(new TextBox { Watermark = "10" });
        _perItemPanel = new StackPanel { Spacing = 4 };
        _perItemPanel.Children.Add(Label(Tr.T("За каждый проданный товар, сом", "Ар бир сатылган товар үчүн, сом", "Per item sold, som", "Satılan her ürün için, som", "Har bir sotilgan mahsulot uchun, so'm")));
        _perItemPanel.Children.Add(_perItemBox);
        _perItemPanel.Children.Add(SoftHint(new TextBlock
        {
            Text = Tr.T(
                "Штучный товар — за каждую штуку в чеке, весовой — за строку чека. Возврат уменьшает начисление.",
                "Даана товар — чектеги ар бир даана үчүн, салмак товар — чектин сабы үчүн. Кайтаруу эсептөөнү азайтат.",
                "Piece goods — per unit on the receipt, weighed goods — per receipt line. Returns reduce the amount.",
                "Adetli ürün — fişteki her adet için, tartılı ürün — fiş satırı için. İade tutarı azaltır.",
                "Donali mahsulot — chekdagi har bir dona uchun, tortiladigan — chek qatori uchun. Qaytarish hisobni kamaytiradi."),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        }));
        panel.Children.Add(_perItemPanel);

        _errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _errorText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        panel.Children.Add(_errorText);

        _saveButton = new Button
        {
            Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"),
            IsDefault = true,
            IsEnabled = false,
        };
        // 2026-10-21: одна главная жёлтая «Сохранить», «Отмена» — обводкой (стили modal-* в PosDialogStyles) (пакет 3, п. 136).
        _saveButton.Classes.Add("modal-primary");
        _saveButton.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_saveButton);

        // 2026-10-21: шапка (заголовок, сотрудник, ✕ = «Отмена») и низ с кнопками — у ModalShell; середина прокручивается
        // на невысоком экране (пакет 3, п. 136).
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title, Subtitle = employeeName, Footer = buttons, Content = panel,
        };
        UpdateFields();
        Opened += async (_, _) => await LoadAsync();
    }

    // 2026-10-21: подписи — цветом темы (BrushTextSoft), а не серым Windows (пакет 3, п. 136).
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushTextSoft"));
        return label;
    }

    /// <summary>2026-10-21: пояснение под полем — цветом темы (пакет 3, п. 136).</summary>
    private static TextBlock SoftHint(TextBlock hint)
    {
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        return hint;
    }

    private RadioButton SchemeRadio(string text)
    {
        // 2026-10-22 (модалки, пакет 4): строка выбора схемы — 44 px (было ~32) для пальца.
        var radio = new RadioButton { Content = text, GroupName = "pay_scheme", MinHeight = 44 };
        radio.IsCheckedChanged += (_, _) => UpdateFields();
        return radio;
    }

    private string Scheme =>
        _salaryRadio.IsChecked == true ? "salary"
        : _percentRadio.IsChecked == true ? "percent"
        : _perItemRadio?.IsChecked == true ? "per_item"
        : _salaryPerItemRadio?.IsChecked == true ? "salary_plus_per_item"
        : _percentPerItemRadio?.IsChecked == true ? "percent_plus_per_item"
        : "salary_plus_percent";

    private static bool HasSalary(string scheme) => scheme is "salary" or "salary_plus_percent" or "salary_plus_per_item";

    private static bool HasPercent(string scheme) => scheme is "percent" or "salary_plus_percent" or "percent_plus_per_item";

    private static bool HasPerItem(string scheme) => scheme is "per_item" or "salary_plus_per_item" or "percent_plus_per_item";

    private void UpdateFields()
    {
        if (_monthlyPanel is null || _percentPanel is null || _perItemPanel is null)
            return;
        _monthlyPanel.IsVisible = HasSalary(Scheme);
        _percentPanel.IsVisible = HasPercent(Scheme);
        _perItemPanel.IsVisible = HasPerItem(Scheme);
        ShowError(null);
    }

    private async Task LoadAsync()
    {
        try
        {
            var data = await App.SalesApi.ListPayProfilesAsync(_userId);
            var rows = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r : data;
            // Как сайт: берётся первый профиль сотрудника (у одного сотрудника обычно один,
            // общий для всех филиалов).
            var profile = rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().FirstOrDefault() : default;
            if (profile.ValueKind == JsonValueKind.Object)
            {
                _profileId = SalaryWindow.Str(profile, "id");
                var scheme = SalaryWindow.Str(profile, "pay_scheme");
                _salaryRadio.IsChecked = scheme == "salary";
                _percentRadio.IsChecked = scheme == "percent";
                _perItemRadio.IsChecked = scheme == "per_item";
                _salaryPerItemRadio.IsChecked = scheme == "salary_plus_per_item";
                _percentPerItemRadio.IsChecked = scheme == "percent_plus_per_item";
                _bothRadio.IsChecked = scheme is not ("salary" or "percent" or "per_item" or "salary_plus_per_item" or "percent_plus_per_item");
                var monthly = SalaryWindow.Num(profile, "monthly_base_salary");
                var percent = SalaryWindow.Num(profile, "sales_percent");
                var perItem = SalaryWindow.Num(profile, "per_item_amount");
                _monthlyBox.Text = monthly > 0 ? monthly.ToString("0.##", CultureInfo.InvariantCulture) : "";
                _percentBox.Text = percent > 0 ? percent.ToString("0.##", CultureInfo.InvariantCulture) : "";
                _perItemBox.Text = perItem > 0 ? perItem.ToString("0.##", CultureInfo.InvariantCulture) : "";
                _statusText.IsVisible = false;
            }
            else
            {
                _statusText.Text = Tr.T("Схема ещё не настроена — будет создана.", "Схема азырынча жөндөлгөн эмес — ал түзүлөт.",
                    "No scheme yet — it will be created.", "Henüz şema yok — oluşturulacak.", "Sxema hali sozlanmagan — yaratiladi.");
            }
            _saveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _statusText.IsVisible = false;
            ShowError(Tr.T("Не удалось загрузить схему", "Схеманы жүктөө мүмкүн болгон жок", "Could not load the scheme",
                "Şema yüklenemedi", "Sxemani yuklab bo'lmadi") + ": " + ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        var scheme = Scheme;
        var monthly = Parse(_monthlyBox.Text);
        var percent = Parse(_percentBox.Text);
        var perItem = Parse(_perItemBox.Text);
        if (HasPerItem(scheme) && perItem <= 0)
        {
            ShowError(Tr.T("Укажите сумму за каждый проданный товар больше 0.", "Ар бир сатылган товар үчүн 0дөн чоң сумма көрсөтүңүз.",
                "Enter an amount per item sold greater than 0.", "Satılan her ürün için 0'dan büyük tutar girin.",
                "Har bir sotilgan mahsulot uchun 0 dan katta summa kiriting."));
            return;
        }
        if (scheme == "salary_plus_per_item" && monthly <= 0)
        {
            ShowError(Tr.T("Для схемы «Оклад + за товар» укажите оклад больше 0.", "«Айлык + товар үчүн» схемасы үчүн 0дөн чоң айлык көрсөтүңүз.",
                "For the “Salary + per item” scheme, enter a salary greater than 0.", "«Maaş + ürün başına» şeması için 0'dan büyük maaş girin.",
                "«Maosh + mahsulot uchun» sxemasi uchun 0 dan katta maosh kiriting."));
            return;
        }
        if (scheme == "percent_plus_per_item" && percent <= 0)
        {
            ShowError(Tr.T("Для схемы «Процент + за товар» укажите процент больше 0.", "«Пайыз + товар үчүн» схемасы үчүн 0дөн чоң пайыз көрсөтүңүз.",
                "For the “Percentage + per item” scheme, enter a percentage greater than 0.", "«Yüzde + ürün başına» şeması için 0'dan büyük yüzde girin.",
                "«Foiz + mahsulot uchun» sxemasi uchun 0 dan katta foiz kiriting."));
            return;
        }
        // Проверки сайта один в один.
        if (scheme == "salary" && monthly <= 0)
        {
            ShowError(Tr.T("Для схемы «Оклад» укажите оклад больше 0.", "«Айлык» схемасы үчүн 0дөн чоң айлык көрсөтүңүз.",
                "For the “Salary” scheme, enter a salary greater than 0.", "«Maaş» şeması için 0'dan büyük maaş girin.",
                "«Maosh» sxemasi uchun 0 dan katta maosh kiriting."));
            return;
        }
        if (scheme == "percent" && percent <= 0)
        {
            ShowError(Tr.T("Для схемы «Процент от продаж» укажите процент больше 0.", "«Сатуудан пайыз» схемасы үчүн 0дөн чоң пайыз көрсөтүңүз.",
                "For the “Sales percentage” scheme, enter a percentage greater than 0.", "«Satış yüzdesi» şeması için 0'dan büyük yüzde girin.",
                "«Sotuvdan foiz» sxemasi uchun 0 dan katta foiz kiriting."));
            return;
        }
        if (scheme == "salary_plus_percent" && (monthly <= 0 || percent <= 0))
        {
            ShowError(Tr.T("Для схемы «Оклад + процент» заполните оба поля больше 0.", "«Айлык + пайыз» схемасы үчүн эки талаага тең 0дөн чоң маани жазыңыз.",
                "For the “Salary + percentage” scheme, fill in both fields with values greater than 0.", "«Maaş + yüzde» şeması için her iki alana 0'dan büyük değer girin.",
                "«Maosh + foiz» sxemasi uchun ikkala maydonga 0 dan katta qiymat kiriting."));
            return;
        }
        if (percent > 100)
        {
            ShowError(Tr.T("Процент от продаж не может быть больше 100.", "Сатуудан пайыз 100дөн ашпашы керек.",
                "The sales percentage cannot exceed 100.", "Satış yüzdesi 100'ü geçemez.", "Sotuvdan foiz 100 dan oshmasligi kerak."));
            return;
        }

        var monthlyText = !HasSalary(scheme) ? "0" : monthly.ToString("0.##", CultureInfo.InvariantCulture);
        var percentText = !HasPercent(scheme) ? "0" : percent.ToString("0.##", CultureInfo.InvariantCulture);
        var perItemText = !HasPerItem(scheme) ? "0" : perItem.ToString("0.##", CultureInfo.InvariantCulture);
        _saveButton.IsEnabled = false;
        ShowError(null);
        try
        {
            await App.SalesApi.SavePayProfileAsync(_profileId, _userId, scheme, monthlyText, percentText, perItemText);
            PosLogger.Log($"Pay profile saved: user={_userId}, scheme={scheme}, base={monthlyText}, pct={percentText}, per_item={perItemText}", "INFO");
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(Tr.T("Не удалось сохранить схему", "Схеманы сактоо мүмкүн болгон жок", "Could not save the scheme",
                "Şema kaydedilemedi", "Sxemani saqlab bo'lmadi") + ": " + ex.Message);
            _saveButton.IsEnabled = true;
        }
    }

    private static double Parse(string? raw)
    {
        var text = new string((raw ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
    }

    private void ShowError(string? message)
    {
        if (_errorText is null)
            return;
        _errorText.Text = message ?? "";
        _errorText.IsVisible = !string.IsNullOrEmpty(message);
    }
}
