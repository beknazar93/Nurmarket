using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-28, владелец: «стартовые расходы как расход на начало бизнеса» — окно статьи вложений (название, сумма, дата, категория,
/// комментарий) в оболочке ModalShell, как «Новый поставщик». Сумма — единый разбор (запятая и точка — десятичные, дробные разрешены).
/// Saved — сохранённая статья (null — закрыли без сохранения).</summary>
public sealed class StartupCostDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly StartupCostItem _item;
    private readonly bool _isNew;
    private readonly TextBox _title;
    private readonly TextBox _amount;
    private readonly TextBox _date;
    private readonly TextBox _comment;
    private readonly WrapPanel _chipWrap = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string _category;

    public StartupCostItem? Saved { get; private set; }

    public StartupCostDialog(StartupCostItem? existing)
    {
        _isNew = existing is null;
        _item = existing?.Clone() ?? new StartupCostItem { Date = DateTime.Today };
        _category = _item.Category;
        Title = _isNew ? T("Новая статья вложений", "Жаңы салым беренеси", "New investment item", "Yeni yatırım kalemi", "Yangi sarmoya moddasi")
            : T("Статья вложений", "Салым беренеси", "Investment item", "Yatırım kalemi", "Sarmoya moddasi");
        SectionCards.AsModal(this, 672);

        var panel = new StackPanel { Spacing = 10 };
        _title = Field(panel, T("Название *", "Аталышы *", "Name *", "Ad *", "Nomi *"), T("Например: Ремонт помещения", "Мисалы: Имаратты оңдоо", "E.g. Premises repair", "Örn.: Mekan tadilatı", "Masalan: Xonani ta'mirlash"), 120);
        _title.Text = _item.Title;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        _amount = FieldIn(row, 0, T("Сумма, сом *", "Суммасы, сом *", "Amount, som *", "Tutar, som *", "Summa, so'm *"), "1500,50", 20);
        _amount.Text = _item.Amount == 0m ? "" : _item.Amount.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        _date = FieldIn(row, 2, T("Дата *", "Күнү *", "Date *", "Tarih *", "Sana *"), T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy"), 10);
        _date.Text = _item.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        panel.Children.Add(row);

        var catCaption = new TextBlock { Text = T("Категория", "Категория", "Category", "Kategori", "Toifa"), FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(catCaption, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(catCaption);
        panel.Children.Add(_chipWrap);
        BuildChips();

        _comment = Field(panel, T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), T("Необязательно", "Милдеттүү эмес", "Optional", "İsteğe bağlı", "Ixtiyoriy"), 500);
        _comment.Text = _item.Comment;
        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var ok = SectionCards.Primary(_isNew ? T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish") : T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        ok.Click += (_, _) => Save();

        var hint = new TextBlock
        {
            Text = T("Стартовые расходы не вычитаются из чистой прибыли: они считаются отдельно — для окупаемости вложений.",
                "Баштапкы чыгымдар таза пайдадан кемитилбейт: алар өзүнчө — салымдардын кайтарымы үчүн эсептелет.",
                "Startup costs are not deducted from net profit: they are counted separately, for the payback of the investment.",
                "Başlangıç giderleri net kârdan düşülmez: yatırımın geri dönüşü için ayrı hesaplanır.",
                "Boshlang'ich xarajatlar sof foydadan ayirilmaydi: ular sarmoyaning oqlanishi uchun alohida hisoblanadi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MinWidth = 140,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        Content = SectionCards.Shell(Title,
            T("Вложение на открытие бизнеса: ремонт, оборудование, мебель, первоначальная закупка, лицензии, реклама, залог за аренду.",
                "Бизнести ачуудагы салым: оңдоо, жабдуу, эмерек, баштапкы товар, лицензия, жарнама, ижара күрөөсү.",
                "An opening investment: repairs, equipment, furniture, first stock purchase, licences, advertising, rent deposit.",
                "Açılış yatırımı: tadilat, ekipman, mobilya, ilk mal alımı, lisans, reklam, kira depozitosu.",
                "Ochilishdagi sarmoya: ta'mir, uskuna, mebel, dastlabki tovar, litsenziya, reklama, ijara garovi."),
            panel, SectionCards.Buttons(cancel, ok), hint);
        Opened += (_, _) => _title.Focus();
    }

    private void BuildChips()
    {
        _chipWrap.Children.Clear();
        foreach (var (key, title) in StartupCostsStore.Categories)
        {
            var chip = UiKit.Chip(this, title, string.Equals(key, _category, StringComparison.OrdinalIgnoreCase));
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.Click += (_, _) =>
            {
                _category = key;
                BuildChips();
            };
            _chipWrap.Children.Add(chip);
        }
    }

    private TextBox Field(StackPanel parent, string label, string watermark, int maxLength)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var box = SectionCards.Field(UiKit.Input(this, watermark, 44));
        box.MaxLength = maxLength;
        Avalonia.Automation.AutomationProperties.SetName(box, label.TrimEnd('*', ' '));
        parent.Children.Add(new StackPanel { Spacing = 4, Children = { caption, box } });
        return box;
    }

    private TextBox FieldIn(Grid grid, int col, string label, string watermark, int maxLength)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        var box = SectionCards.Field(UiKit.Input(this, watermark, 44));
        box.MaxLength = maxLength;
        Avalonia.Automation.AutomationProperties.SetName(box, label.TrimEnd('*', ' '));
        var cell = new StackPanel { Spacing = 4, Children = { caption, box } };
        Grid.SetColumn(cell, col);
        grid.Children.Add(cell);
        return box;
    }

    private void Save()
    {
        var parse = CheckoutValidation.TryParseAmount(_amount.Text, out var amount);
        if (parse != CheckoutValidation.AmountParse.Ok)
        {
            Fail(parse == CheckoutValidation.AmountParse.Empty ? StartupCostsStore.Validate(new StartupCostItem { Title = "x", Amount = 0m, Date = DateTime.Today }) : CheckoutValidation.AmountProblemText(parse), _amount);
            return;
        }
        if (InvestmentsWindow.ParseDate(_date.Text) is not { } date)
        {
            Fail(T("Введите дату в виде 15.03.2026.", "Күндү 15.03.2026 түрүндө жазыңыз.", "Enter the date like 15.03.2026.", "Tarihi 15.03.2026 biçiminde girin.", "Sanani 15.03.2026 ko'rinishida kiriting."), _date);
            return;
        }
        _item.Title = _title.Text ?? "";
        _item.Amount = (decimal)Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        _item.Date = date;
        _item.Category = _category;
        _item.Comment = _comment.Text ?? "";
        var problem = StartupCostsStore.Save(_item);
        if (problem != null)
        {
            Fail(problem, string.IsNullOrWhiteSpace(_title.Text) ? _title : null);
            return;
        }
        Saved = _item;
        Close();
    }

    private void Fail(string? text, Control? field)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(text);
        field?.Focus();
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
