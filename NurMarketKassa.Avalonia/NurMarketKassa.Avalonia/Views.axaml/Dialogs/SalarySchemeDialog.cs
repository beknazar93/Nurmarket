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
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = employeeName, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        _statusText = new TextBlock
        {
            Text = Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…"),
            FontSize = 12,
            Foreground = Brushes.Gray,
        };
        panel.Children.Add(_statusText);

        panel.Children.Add(Label(Tr.T("Схема начисления", "Эсептөө схемасы", "Pay scheme", "Ödeme şeması", "Hisoblash sxemasi")));
        _salaryRadio = SchemeRadio(SalaryWindow.SchemeName("salary"));
        _percentRadio = SchemeRadio(SalaryWindow.SchemeName("percent"));
        _bothRadio = SchemeRadio(SalaryWindow.SchemeName("salary_plus_percent"));
        _bothRadio.IsChecked = true;
        panel.Children.Add(_salaryRadio);
        panel.Children.Add(_percentRadio);
        panel.Children.Add(_bothRadio);

        _monthlyBox = new TextBox { Watermark = "0" };
        _monthlyPanel = new StackPanel { Spacing = 4 };
        _monthlyPanel.Children.Add(Label(Tr.T("Оклад в месяц, сом", "Айына айлык, сом", "Monthly salary, som", "Aylık maaş, som", "Oylik maosh, so'm")));
        _monthlyPanel.Children.Add(_monthlyBox);
        panel.Children.Add(_monthlyPanel);

        _percentBox = new TextBox { Watermark = "0" };
        _percentPanel = new StackPanel { Spacing = 4 };
        _percentPanel.Children.Add(Label(Tr.T("Процент от личных продаж, %", "Жеке сатуудан пайыз, %", "Percentage of own sales, %",
            "Kişisel satışlardan yüzde, %", "Shaxsiy sotuvdan foiz, %")));
        _percentPanel.Children.Add(_percentBox);
        _percentPanel.Children.Add(new TextBlock
        {
            Text = Tr.T(
                "Этот же процент подставляется консультанту в окне оплаты.",
                "Ушул эле пайыз төлөө терезесинде консультантка коюлат.",
                "The same percentage is suggested for the consultant in the payment window.",
                "Aynı yüzde ödeme penceresinde danışmana önerilir.",
                "Xuddi shu foiz to'lov oynasida maslahatchiga qo'yiladi."),
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_percentPanel);

        _errorText = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_errorText);

        _saveButton = new Button
        {
            Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"),
            IsDefault = true,
            IsEnabled = false,
        };
        _saveButton.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_saveButton);
        panel.Children.Add(buttons);

        Content = panel;
        UpdateFields();
        Opened += async (_, _) => await LoadAsync();
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };

    private RadioButton SchemeRadio(string text)
    {
        var radio = new RadioButton { Content = text, GroupName = "pay_scheme" };
        radio.IsCheckedChanged += (_, _) => UpdateFields();
        return radio;
    }

    private string Scheme =>
        _salaryRadio.IsChecked == true ? "salary"
        : _percentRadio.IsChecked == true ? "percent"
        : "salary_plus_percent";

    private void UpdateFields()
    {
        if (_monthlyPanel is null || _percentPanel is null)
            return;
        _monthlyPanel.IsVisible = Scheme != "percent";
        _percentPanel.IsVisible = Scheme != "salary";
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
                _bothRadio.IsChecked = scheme is not ("salary" or "percent");
                var monthly = SalaryWindow.Num(profile, "monthly_base_salary");
                var percent = SalaryWindow.Num(profile, "sales_percent");
                _monthlyBox.Text = monthly > 0 ? monthly.ToString("0.##", CultureInfo.InvariantCulture) : "";
                _percentBox.Text = percent > 0 ? percent.ToString("0.##", CultureInfo.InvariantCulture) : "";
                _statusText.IsVisible = false;
            }
            else
            {
                _statusText.Text = Tr.T("Схема ещё не настроена — будет создана.", "Схема азырынча орнотулган эмес — түзүлөт.",
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
        // Проверки сайта один в один.
        if (scheme == "salary" && monthly <= 0)
        {
            ShowError(Tr.T("Для схемы «Оклад» укажите оклад больше 0.", "«Айлык» схемасы үчүн 0дөн чоң айлык көрсөтүңүз.",
                "For the «Salary» scheme enter a salary above 0.", "«Maaş» şeması için 0'dan büyük maaş girin.",
                "«Maosh» sxemasi uchun 0 dan katta maosh kiriting."));
            return;
        }
        if (scheme == "percent" && percent <= 0)
        {
            ShowError(Tr.T("Для схемы «Процент от продаж» укажите процент больше 0.", "«Сатуудан пайыз» схемасы үчүн 0дөн чоң пайыз көрсөтүңүз.",
                "For the «Sales percentage» scheme enter a percentage above 0.", "«Satış yüzdesi» şeması için 0'dan büyük yüzde girin.",
                "«Sotuvdan foiz» sxemasi uchun 0 dan katta foiz kiriting."));
            return;
        }
        if (scheme == "salary_plus_percent" && (monthly <= 0 || percent <= 0))
        {
            ShowError(Tr.T("Для схемы «Оклад + процент» заполните оба поля больше 0.", "«Айлык + пайыз» схемасы үчүн эки талааны тең 0дөн чоң толтуруңуз.",
                "For «Salary + percentage» fill in both fields above 0.", "«Maaş + yüzde» için her iki alanı 0'dan büyük doldurun.",
                "«Maosh + foiz» uchun ikkala maydonni 0 dan katta to'ldiring."));
            return;
        }
        if (percent > 100)
        {
            ShowError(Tr.T("Процент от продаж не может быть больше 100.", "Сатуудан пайыз 100дөн ашпайт.",
                "The sales percentage cannot exceed 100.", "Satış yüzdesi 100'ü geçemez.", "Sotuvdan foiz 100 dan oshmaydi."));
            return;
        }

        var monthlyText = scheme == "percent" ? "0" : monthly.ToString("0.##", CultureInfo.InvariantCulture);
        var percentText = scheme == "salary" ? "0" : percent.ToString("0.##", CultureInfo.InvariantCulture);
        _saveButton.IsEnabled = false;
        ShowError(null);
        try
        {
            await App.SalesApi.SavePayProfileAsync(_profileId, _userId, scheme, monthlyText, percentText);
            PosLogger.Log($"Pay profile saved: user={_userId}, scheme={scheme}, base={monthlyText}, pct={percentText}", "INFO");
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
