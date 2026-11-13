using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-12 (ХМ5, ТЗ хозмаг 4.2): окно владельца «Нормы расхода калькулятора» — запас по умолчанию, запас при диагональной укладке и нормы
/// на м² (краска, грунт, клей, затирка, шпаклёвка, штукатурка): расход и число слоёв. «Сохранить» пишет нормы в общие настройки компании
/// (<see cref="HardwareCalcSettings.TrySaveToCompany"/> — все кассы получат их), «Сбросить» возвращает типовые. Открывает только владелец или
/// администратор (кнопка в Настройках → Операции скрыта для остальных). Поля — как в других окнах: ≥ 44 px, Enter — «Сохранить», Esc — «Отмена».
/// </summary>
internal sealed class HardwareCalcNormsView : Window
{
    private readonly HardwareCalcSettings _settings;
    private readonly TextBox _reserve = new();
    private readonly TextBox _diagonal = new();
    private readonly List<(CalcNorm Norm, TextBox PerM2, TextBox Layers)> _rows = new();
    private readonly TextBlock _error = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public HardwareCalcNormsView()
    {
        // Работаем с копией: «Отмена» ничего не меняет.
        _settings = HardwareCalcSettings.FromJson(HardwareCalcSettings.Current.ToJson());
        var title = T("Нормы расхода калькулятора", "Калькулятордун сарптоо нормалары", "Calculator consumption rates", "Hesaplayıcı tüketim normları", "Kalkulyator sarf normalari");
        Title = title;
        SectionCards.AsModal(this, 640);

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Row(T("Запас по умолчанию, %", "Демейки запас, %", "Default allowance, %", "Varsayılan fire payı, %", "Standart zaxira, %"), _reserve, null,
            Fmt(_settings.ReservePercent), null));
        root.Children.Add(Row(T("Запас при диагональной укладке, %", "Диагоналдык төшөөдө запас, %", "Allowance for diagonal laying, %", "Çapraz döşemede fire payı, %", "Diagonal yotqizishda zaxira, %"), _diagonal, null,
            Fmt(_settings.DiagonalReservePercent), null));

        var head = new TextBlock { Text = T("Норма: расход на м² за слой (л или кг) и число слоёв", "Норма: 1 м² үчүн бир катмарга сарпталышы (л же кг) жана катмарлардын саны",
            "Rate: consumption per m² per coat (l or kg) and number of coats", "Norm: m² başına kat başına tüketim (l veya kg) ve kat sayısı", "Norma: 1 m² uchun bir qatlamga sarf (l yoki kg) va qatlamlar soni"),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushTextSoft"));
        root.Children.Add(head);
        foreach (var norm in _settings.Norms)
        {
            var perM2 = new TextBox();
            var layers = new TextBox();
            _rows.Add((norm, perM2, layers));
            root.Children.Add(Row(HardwareCalcSettings.TitleOf(norm) + (norm.Kind == CalcKind.Paint ? ", " + T("л", "л", "l", "l", "l") : ", " + T("кг", "кг", "kg", "kg", "kg")), perM2, layers,
                Fmt(norm.PerM2), Fmt(norm.Layers)));
        }

        var cancel = SectionCards.Cancel(T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        var reset = SectionCards.Secondary(T("Сбросить к типовым", "Демейкиге кайтаруу", "Reset to defaults", "Varsayılana sıfırla", "Standartga qaytarish"));
        reset.Click += (_, _) =>
        {
            if (HardwareCalcSettings.ResetCompany())
                Close(true);
            else
                ShowError(T("Не удалось сбросить нормы.", "Нормаларды кайтаруу мүмкүн болгон жок.", "Could not reset the rates.", "Normlar sıfırlanamadı.", "Normalarni qaytarib bo'lmadi."));
        };
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        save.Click += (_, _) => Save();

        var errorBox = SectionCards.ErrorBox(this, _error);
        var scroll = new ScrollViewer { Content = root, MaxHeight = 480, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = SectionCards.Shell(title,
            T("Общие для магазина: сохранённые нормы получат все кассы.", "Дүкөн үчүн жалпы: сакталган нормаларды бардык кассалар алат.",
                "Shared by the store: all tills get the saved rates.", "Mağaza için ortaktır: kaydedilen normları tüm kasalar alır.", "Do'kon uchun umumiy: saqlangan normalarni barcha kassalar oladi."),
            scroll, SectionCards.Buttons(reset, cancel, save), errorBox);
    }

    private static string Fmt(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    private Control Row(string label, TextBox first, TextBox? second, string firstText, string? secondText)
    {
        var l = new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushText"));
        first.Text = firstText;
        first.MaxLength = 8;
        first.MinWidth = 96;
        SectionCards.Field(first);
        AutomationProperties.SetName(first, label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(l);
        Grid.SetColumn(first, 1);
        grid.Children.Add(first);
        if (second is not null)
        {
            second.Text = secondText;
            second.MaxLength = 4;
            second.MinWidth = 72;
            second.Margin = new Thickness(8, 0, 0, 0);
            SectionCards.Field(second);
            AutomationProperties.SetName(second, label + " — " + T("слоёв", "катмар", "coats", "kat", "qatlam"));
            Grid.SetColumn(second, 2);
            grid.Children.Add(second);
        }
        return grid;
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void Save()
    {
        if (!ConsumptionCalculator.TryParseNumber(_reserve.Text, out var reserve) || reserve > 100m
            || !ConsumptionCalculator.TryParseNumber(_diagonal.Text, out var diagonal) || diagonal > 100m)
        {
            ShowError(T("Запас — число от 0 до 100.", "Запас — 0дөн 100гө чейинки сан.", "The allowance is a number from 0 to 100.", "Fire payı 0 ile 100 arasında bir sayıdır.", "Zaxira — 0 dan 100 gacha son."));
            return;
        }
        var result = new HardwareCalcSettings { ReservePercent = reserve, DiagonalReservePercent = diagonal };
        foreach (var (norm, perM2Box, layersBox) in _rows)
        {
            if (!ConsumptionCalculator.TryParseNumber(perM2Box.Text, out var perM2) || perM2 <= 0m || perM2 > 1000m
                || !ConsumptionCalculator.TryParseNumber(layersBox.Text, out var layers) || layers <= 0m || layers > 100m)
            {
                ShowError(T($"«{HardwareCalcSettings.TitleOf(norm)}»: расход и число слоёв — числа больше нуля.", $"«{HardwareCalcSettings.TitleOf(norm)}»: сарптоо жана катмарлар — нөлдөн чоң сандар.",
                    $"“{HardwareCalcSettings.TitleOf(norm)}”: the rate and number of coats must be numbers above zero.", $"«{HardwareCalcSettings.TitleOf(norm)}»: tüketim ve kat sayısı sıfırdan büyük sayılar olmalı.",
                    $"«{HardwareCalcSettings.TitleOf(norm)}»: sarf va qatlamlar soni noldan katta sonlar bo'lishi kerak."));
                return;
            }
            norm.PerM2 = perM2;
            norm.Layers = layers;
            result.Norms.Add(norm);
        }
        if (!HardwareCalcSettings.TrySaveToCompany(result))
        {
            ShowError(T("Нормы не помещаются в лимит настроек — сократите слова.", "Нормалар жөндөөлөрдүн чегине батпайт.", "The rates do not fit the settings limit.", "Normlar ayar sınırına sığmıyor.", "Normalar sozlamalar chegarasiga sig'maydi."));
            return;
        }
        Close(true);
    }
}
