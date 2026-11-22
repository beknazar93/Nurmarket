using System;
using Avalonia;
using Avalonia.Automation;
using NurMarketKassa.AvaloniaHost.Services;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21: режимы единой вкладки «Ценники и штрихкоды»: «Печать» (прежняя печать), «Редактор» (<see cref="LabelDesignerView"/>), «Шаблоны»
/// (<see cref="LabelTemplatesView"/>). Редактор — платная функция «Редактор этикетки» (входит в тариф «Старт»; иначе вместо него подсказка с вводом кода),
/// остальное — как раньше (право «Печать штрих-кодов», покупка массовой печати — внутри «Печать»).</summary>
public sealed partial class LabelsHubView
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly Border _body = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly ToggleButton[] _modeButtons = new ToggleButton[3];
    private LabelDesignerView? _designer;
    private LabelTemplatesView? _templates;
    private Border? _editorGate;
    private bool _printNeedsReload;
    // 2026-11-22: было 520 — на окне 1024×600 область под полосой режимов ≈ 412 px, а тело раздувалось до 520: список карточек и нижняя строка страниц уходили за нижний
    // край окна (тест BarcodesPerfLayout 1024×600: низ списка 632 при окне 600). 400: печать на 1024×600 заполняет область (список ≈ 176 px) без прокрутки,
    // прокрутка тела включается только на ещё более низком окне (минимум «Склада» — 550).
    private const double MinBodyHeight = 400;

    /// <summary>Редактор (создаётся при первом показе) — для проверок.</summary>
    public LabelDesignerView? Designer => _designer;

    /// <summary>Режим «Шаблоны» (создаётся при первом показе).</summary>
    public LabelTemplatesView? TemplatesView => _templates;

    /// <summary>Подпись кнопок режимов — для проверок.</summary>
    public static string ModeText(LabelsHubMode mode) => mode switch
    {
        LabelsHubMode.Print => T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"),
        LabelsHubMode.Editor => T("Редактор", "Редактор", "Editor", "Düzenleyici", "Muharrir"),
        _ => T("Шаблоны", "Шаблондор", "Templates", "Şablonlar", "Shablonlar"),
    };

    partial void InitModes()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var mode in new[] { LabelsHubMode.Print, LabelsHubMode.Editor, LabelsHubMode.Templates })
        {
            var m = mode;
            var b = new ToggleButton
            {
                Content = ModeText(m), MinHeight = 40, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(20), FontSize = 14, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            b.Classes.Add("sec-outline");
            AutomationProperties.SetName(b, ModeText(m));
            b.Click += (_, _) => ShowMode(m);
            _modeButtons[(int)m] = b;
            bar.Children.Add(b);
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        root.Children.Add(bar);
        // 2026-11-21: на низком окне (800×600: область 344 px под полосой режимов) нижняя строка «Печати» (страницы, «Назад»/«Вперёд») уходила за край и не прокручивалась.
        // Режимы лежат в прокручиваемой области: высота тела — не меньше MinBodyHeight (хватает на панели, карточки и подвал), выше — равна высоте области, прокрутки нет.
        var bodyScroll = new ScrollViewer
        {
            Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        bodyScroll.SizeChanged += (_, e) => _body.Height = Math.Max(e.NewSize.Height, MinBodyHeight);
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);
        Content = root;
        _printHost.OpenEditorRequested = () => ShowMode(LabelsHubMode.Editor);
        ApplyMode();
    }

    partial void ApplyMode()
    {
        if (_body is null)
            return;
        for (var i = 0; i < _modeButtons.Length; i++)
            if (_modeButtons[i] != null)
                _modeButtons[i].IsChecked = i == (int)Mode;
        switch (Mode)
        {
            case LabelsHubMode.Print:
                if (_printNeedsReload)
                {
                    _printNeedsReload = false;
                    _printHost.View?.ReloadTemplate();
                }
                _body.Child = _printHost;
                break;
            case LabelsHubMode.Editor:
                _body.Child = EditorOrGate();
                _designer?.Reload();
                break;
            default:
                _templates ??= new LabelTemplatesView(
                    (template, name) =>
                    {
                        ShowMode(LabelsHubMode.Editor);
                        _designer?.LoadTemplate(template, T($"Шаблон «{name}» открыт для правки. «Сохранить» сделает его рабочим.", $"«{name}» шаблону оңдоо үчүн ачылды. «Сактоо» аны иштөөчү кылат.",
                            $"Template “{name}” is open for editing. “Save” makes it the working one.", $"«{name}» şablonu düzenleme için açıldı. «Kaydet» onu çalışan şablon yapar.", $"«{name}» shabloni tahrirlash uchun ochildi. «Saqlash» uni ishchi qiladi."));
                    },
                    template =>
                    {
                        template.Item = null;
                        LabelTemplateStore.Save(template);
                        _printNeedsReload = true;
                        _designer?.Reload();
                        ShowMode(LabelsHubMode.Print);
                    });
                _body.Child = _templates;
                _templates.Refresh();
                break;
        }
    }

    private Control EditorOrGate()
    {
        if (PaidFeatureGate.IsUnlocked(PaidFeature.LabelEditor))
        {
            if (_designer is null)
            {
                _designer = new LabelDesignerView();
                _designer.TemplateSaved += () => _printNeedsReload = true;
                _designer.PrintRequested += () => ShowMode(LabelsHubMode.Print);
            }
            return _designer;
        }
        if (_editorGate is not null)
            return _editorGate;
        var title = new TextBlock { Text = T("Редактор ценников и этикеток", "Баа белгилери жана этикеткалар редактору", "Price tag and label editor", "Fiyat etiketi ve etiket düzenleyici", "Narx yorliqlari va etiketka muharriri"), FontSize = 18, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var text = new TextBlock
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Text = T("Холст в масштабе, перетаскивание элементов, свои надписи, старая цена, скидка, бренд, дата, логотип, шаблоны на все компьютеры магазина. Платная функция «Редактор этикетки» (входит в тариф «Старт»): введите серийный номер.",
                "Масштабдуу холст, элементтерди сүйрөө, өз жазуулар, эски баа, арзандатуу, бренд, дата, логотип, дүкөндүн бардык компьютерлери үчүн шаблондор. Акы төлөнүүчү «Этикетка редактору» функциясы («Старт» тарифине кирет): сериялык номерди киргизиңиз.",
                "A scaled canvas, dragging elements, your own captions, old price, discount, brand, date, logo, templates for all computers of the store. Paid feature “Label editor” (included in the “Start” plan): enter the serial number.",
                "Ölçekli tuval, öğeleri sürükleme, kendi yazılarınız, eski fiyat, indirim, marka, tarih, logo, mağazanın tüm bilgisayarları için şablonlar. Ücretli «Etiket düzenleyici» özelliği («Start» tarifesine dahil): seri numarasını girin.",
                "Masshtabli tuval, elementlarni sudrash, o'z yozuvlaringiz, eski narx, chegirma, brend, sana, logotip, do'konning barcha kompyuterlari uchun shablonlar. Pullik «Yorliq muharriri» funksiyasi («Start» tarifiga kiradi): seriya raqamini kiriting."),
        };
        var button = new Button
        {
            Content = T("Ввести серийный номер", "Сериялык номерди киргизүү", "Enter serial number", "Seri numarası gir", "Seriya raqamini kiritish"),
            HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 44, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(10), Focusable = false,
        };
        button.Classes.Add("sec-primary");
        button.Click += (_, _) =>
        {
            if (PaidFeatureGate.EnsureUnlocked(PaidFeature.LabelEditor, TopLevel.GetTopLevel(this) as Window))
            {
                _editorGate = null;
                Unlocked?.Invoke();
                ApplyMode();
            }
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
        _editorGate = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel { Spacing = 14, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { title, text, button } },
        };
        return _editorGate;
    }
}
