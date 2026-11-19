using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-19 («Смета», ТЗ 3): «Рассчитать материал» — калькуляторы расхода по площади (краска, грунт, плитка, ламинат/линолеум, обои, гипсокартон, шпаклёвка, штукатурка, клей, затирка).
/// Ввод: размеры комнаты (длина × ширина × высота) или площадь, проёмы (вычитаются), запас %, упаковка; расход и слои — из таблицы «Нормы расхода». Результат — целые упаковки (вверх) с формулой и допущениями.
/// Товар каталога можно привязать (запоминается по материалу): тогда строка идёт с ценой и количеством в единицах товара. «Добавить» кладёт строку в смету и закрывает окно, «Добавить и ещё» — оставляет
/// окно открытым с теми же размерами комнаты (плитка → клей → затирка). Расчёт — чистые формулы (<see cref="EstimateCalc"/>), окно ничего не округляет само.</summary>
public sealed class EstimateCalcDialog : Window
{
    private readonly EstimateDeps _deps;
    private readonly EstimateDoc _doc;
    private readonly Func<EstimateLine, bool> _onAdd;
    private readonly ModalShell _shell = new();
    private readonly ComboBox _material = new() { Name = "CalcMaterial", Height = 40, MinWidth = 220 };
    private readonly ComboBox _surface = new() { Name = "CalcSurface", Height = 40, MinWidth = 200 };
    private readonly Dictionary<string, (TextBox Box, StackPanel Cell, TextBlock Label)> _f = new();
    private readonly TextBox _find = EstimateUi.Box("CalcFind", "", 300, "", maxLength: 80);
    private readonly ListBox _sugg = new() { Name = "CalcSuggest", MaxHeight = 150, IsVisible = false };
    private readonly TextBlock _linked = ServiceDeskUi.Text("", 13, true);
    private readonly TextBlock _result = ServiceDeskUi.Text("", 20, true);
    private readonly TextBlock _formula = ServiceDeskUi.Text("", 12.5, brush: "BrushTextSoft");
    private readonly TextBlock _lineInfo = ServiceDeskUi.Text("", 13);
    private readonly TextBlock _error = ServiceDeskUi.Text("", 13, brush: "BrushDanger");
    private readonly EstimateMaterial[] _materials = Enum.GetValues<EstimateMaterial>();
    private EstimateCatalogInfo? _product;
    private bool _filling;
    private EstimateCalcResult? _last;

    /// <summary>Размеры комнаты запоминаются на время работы программы: плитка → клей → затирка считаются по одной комнате.</summary>
    private static (decimal Len, decimal Wid, decimal Hgt, decimal Open)? _room;

    public EstimateMaterial CurrentMaterial => _materials[Math.Max(0, _material.SelectedIndex)];

    public EstimateCalcResult? Last => _last;

    public EstimateCalcDialog(EstimateDeps deps, EstimateDoc doc, Func<EstimateLine, bool> onAdd, EstimateMaterial start = EstimateMaterial.Paint)
    {
        _deps = deps;
        _doc = doc;
        _onAdd = onAdd;
        Title = EstimateUi.T("Рассчитать материал", "Материалды эсептөө", "Calculate material", "Malzeme hesapla", "Materialni hisoblash");
        Width = 900;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => this.FitToKassaScreen();
        BuildUi();
        _material.SelectedIndex = Array.IndexOf(_materials, start);
        ApplyDefaults();
    }

    private TextBox Field(string key, string label, double width = 150, string? watermark = null)
    {
        var box = EstimateUi.Box("Calc_" + key, "", width, label, watermark, maxLength: 10);
        box.TextChanged += (_, _) => { if (!_filling) Recalc(); };
        var text = ServiceDeskUi.Text(label, 12, brush: "BrushTextSoft");
        var cell = new StackPanel { Width = width, Margin = new Thickness(0, 0, 10, 6) };
        cell.Children.Add(text);
        cell.Children.Add(box);
        _f[key] = (box, cell, text);
        return box;
    }

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;
        _material.ItemsSource = _materials.Select(EstimateCalc.Title).ToList();
        _surface.ItemsSource = new[]
        {
            EstimateUi.T("Пол / потолок (длина × ширина)", "Пол / шып (узундук × туурасы)", "Floor / ceiling (length × width)", "Zemin / tavan (uzunluk × en)", "Pol / shift (uzunlik × eni)"),
            EstimateUi.T("Стены (периметр × высота)", "Дубалдар (периметр × бийиктик)", "Walls (perimeter × height)", "Duvarlar (çevre × yükseklik)", "Devorlar (perimetr × balandlik)"),
            EstimateUi.T("Площадь задана", "Аянт берилген", "Area is given", "Alan verildi", "Maydon berilgan"),
        };
        _material.SelectionChanged += (_, _) => { if (!_filling) ApplyDefaults(); };
        _surface.SelectionChanged += (_, _) => { if (!_filling) Recalc(); };
        Avalonia.Automation.AutomationProperties.SetName(_material, Title);

        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        _material.HorizontalAlignment = HorizontalAlignment.Stretch;
        top.Children.Add(EstimateUi.Labeled(EstimateUi.T("Материал", "Материал", "Material", "Malzeme", "Material"), _material, 250));
        _surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        top.Children.Add(EstimateUi.Labeled(EstimateUi.T("Откуда площадь", "Аянт кайдан", "Where the area comes from", "Alan nereden", "Maydon qayerdan"), _surface, 360));

        var fields = new WrapPanel { Orientation = Orientation.Horizontal };
        Field("len", EstimateUi.T("Длина, м", "Узундук, м", "Length, m", "Uzunluk, m", "Uzunlik, m"));
        Field("wid", EstimateUi.T("Ширина, м", "Туурасы, м", "Width, m", "En, m", "Eni, m"));
        Field("hgt", EstimateUi.T("Высота, м", "Бийиктик, м", "Height, m", "Yükseklik, m", "Balandlik, m"));
        Field("area", EstimateUi.T("Площадь, м²", "Аянт, м²", "Area, m²", "Alan, m²", "Maydon, m²"));
        Field("open", EstimateUi.T("Проёмы, м²", "Тешиктер, м²", "Openings, m²", "Boşluklar, m²", "Teshiklar, m²"));
        Field("res", EstimateUi.T("Запас, %", "Запас, %", "Allowance, %", "Fire payı, %", "Zaxira, %"), 120);
        Field("pack", EstimateUi.T("Упаковка: м² / л / кг", "Таңгак: м² / л / кг", "Pack: m² / l / kg", "Paket: m² / l / kg", "Qadoq: m² / l / kg"), 190);
        Field("layers", EstimateUi.T("Слоёв", "Катмар", "Coats", "Kat", "Qatlam"), 110);
        Field("perm2", EstimateUi.T("Расход на м² за слой", "1 м² үчүн сарптоо (катмарга)", "Rate per m² per coat", "m² başına kat tüketimi", "1 m² uchun sarf (qatlamga)"), 200);
        Field("rl", EstimateUi.T("Рулон: длина, м", "Түрмөк: узундук, м", "Roll: length, m", "Rulo: uzunluk, m", "Rulon: uzunlik, m"), 170);
        Field("rw", EstimateUi.T("Рулон: ширина, м", "Түрмөк: туурасы, м", "Roll: width, m", "Rulo: en, m", "Rulon: eni, m"), 170);
        Field("rap", EstimateUi.T("Раппорт, м", "Раппорт, м", "Pattern repeat, m", "Rapor, m", "Raport, m"), 140);
        foreach (var (_, cell, _) in _f.Values)
            fields.Children.Add(cell);

        _find.Watermark = EstimateUi.T("Название, штрихкод или артикул", "Аты, штрихкод же артикул", "Name, barcode or article", "Ad, barkod veya stok kodu", "Nomi, shtrix-kod yoki artikul");
        Avalonia.Automation.AutomationProperties.SetName(_find, EstimateUi.T("Привязать товар", "Товарды байлоо", "Link a product", "Ürün bağla", "Tovarni bog'lash"));
        _find.TextChanged += (_, _) => FillSuggestions();
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var hit = _sugg.IsVisible ? _sugg.SelectedItem as ListBoxItem : null;
                if (hit?.Tag is EstimateCatalogInfo info)
                    Link(info);
                else if (_deps.Catalog.Search(_find.Text ?? "", 2) is { Count: 1 } one)
                    Link(one[0]);
            }
        };
        var clear = ServiceDeskUi.Action("CalcUnlink", EstimateUi.T("Без товара", "Товарсыз", "No product", "Ürünsüz", "Tovarsiz"), () => Link(null));
        var linkRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        linkRow.Children.Add(EstimateUi.Labeled(EstimateUi.T("Товар из каталога (по желанию)", "Каталогдон товар (каалоо боюнча)", "Product from the catalog (optional)", "Katalogdan ürün (isteğe bağlı)", "Katalogdan tovar (ixtiyoriy)"), _find, 310));
        clear.VerticalAlignment = VerticalAlignment.Bottom;
        linkRow.Children.Add(clear);
        _linked.Name = "CalcLinked";
        _result.Name = "CalcResult";
        _formula.Name = "CalcFormula";
        _lineInfo.Name = "CalcLineInfo";
        _error.Name = "CalcError";

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(top);
        body.Children.Add(fields);
        body.Children.Add(linkRow);
        body.Children.Add(_sugg);
        body.Children.Add(_linked);
        body.Children.Add(_result);
        body.Children.Add(_formula);
        body.Children.Add(_lineInfo);
        body.Children.Add(_error);
        _shell.Content = body;

        var cancel = new Button { Name = "CalcCancel", Content = EstimateUi.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 110, IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (_deps.CanEditNorms())
        {
            var norms = new Button { Name = "CalcNorms", Content = EstimateUi.T("Нормы расхода…", "Сарптоо нормалары…", "Consumption rates…", "Tüketim normları…", "Sarf normalari…"), MinHeight = 40 };
            norms.Classes.Add("modal-cancel");
            norms.Click += async (_, _) => { await new HardwareCalcNormsView().ShowDialog(this).ConfigureAwait(true); ApplyDefaults(keepDims: true); };
            footer.Children.Add(norms);
        }
        var more = new Button { Name = "CalcAddMore", Content = EstimateUi.T("Добавить и ещё", "Кошуу жана дагы", "Add and more", "Ekle ve devam", "Qo'shish va yana"), MinHeight = 40 };
        more.Classes.Add("modal-cancel");
        more.Click += (_, _) => AddLine(close: false);
        var add = new Button { Name = "CalcAdd", Content = EstimateUi.T("Добавить в смету", "Сметага кошуу", "Add to the estimate", "Keşfe ekle", "Smetaga qo'shish"), MinHeight = 40 };
        add.Classes.Add("modal-primary");
        add.Click += (_, _) => AddLine(close: true);
        footer.Children.Add(cancel);
        footer.Children.Add(more);
        footer.Children.Add(add);
        _shell.Footer = footer;
        Content = _shell;
    }

    /// <summary>Подставить типовые значения материала (упаковка, запас, слои, расход из таблицы норм), размеры комнаты оставить.</summary>
    private void ApplyDefaults(bool keepDims = false)
    {
        _filling = true;
        var d = EstimateCalc.Defaults(CurrentMaterial);
        _surface.SelectedIndex = (int)d.Surface;
        if (!keepDims)
        {
            var r = _room;
            Set("len", r?.Len ?? 0m); Set("wid", r?.Wid ?? 0m); Set("hgt", r?.Hgt is > 0m ? r.Value.Hgt : d.Height); Set("open", r?.Open ?? 0m);
        }
        Set("area", 0m);
        Set("res", d.ReservePercent);
        Set("pack", d.PackSize);
        Set("layers", d.Layers);
        Set("perm2", d.PerM2);
        Set("rl", d.RollLength);
        Set("rw", d.RollWidth);
        Set("rap", d.Rapport);
        // Запомненная привязка «материал → товар».
        _product = null;
        if (_deps.Store.GetLink(EstimateCalc.Key(CurrentMaterial)) is { } linkId)
            _product = _deps.Catalog.Find(linkId);
        _find.Text = "";
        _sugg.IsVisible = false;
        _filling = false;
        UpdateVisibility();
        Recalc();
    }

    private void Set(string key, decimal value)
    {
        if (_f.TryGetValue(key, out var f))
            f.Box.Text = value == 0m ? "" : EstimateUi.Num(value);
    }

    private decimal Get(string key, ref bool bad)
    {
        var text = _f[key].Box.Text;
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (ConsumptionCalculator.TryParseNumber(text, out var v))
            return v;
        bad = true;
        return 0m;
    }

    private void UpdateVisibility()
    {
        var m = CurrentMaterial;
        var wall = m == EstimateMaterial.Wallpaper;
        var surface = (EstimateSurface)Math.Max(0, _surface.SelectedIndex);
        ((Control)_surface.Parent!).IsVisible = !wall;
        void Show(string key, bool on) => _f[key].Cell.IsVisible = on;
        Show("len", wall || surface != EstimateSurface.Direct);
        Show("wid", wall || surface != EstimateSurface.Direct);
        Show("hgt", wall || surface == EstimateSurface.Walls);
        Show("area", !wall && surface == EstimateSurface.Direct);
        Show("open", !wall);
        Show("pack", !wall);
        Show("layers", EstimateCalc.NormKey(m) is not null);
        Show("perm2", EstimateCalc.NormKey(m) is not null);
        Show("rl", wall);
        Show("rw", wall);
        Show("rap", wall);
        var unit = m is EstimateMaterial.Paint or EstimateMaterial.Primer ? EstimateUi.T("л", "л", "l", "l", "l")
            : EstimateCalc.NormKey(m) is not null ? EstimateUi.T("кг", "кг", "kg", "kg", "kg") : "м²";
        _f["perm2"].Label.Text = EstimateUi.T($"Расход, {unit} на м² за слой", $"Сарптоо, {unit} 1 м² үчүн катмарга", $"Rate, {unit} per m² per coat", $"Tüketim, m² başına kat başına {unit}", $"Sarf, 1 m² uchun qatlamga {unit}");
        _f["pack"].Label.Text = m == EstimateMaterial.Drywall ? EstimateUi.T("Лист, м²", "Барак, м²", "Sheet, m²", "Levha, m²", "List, m²")
            : EstimateUi.T($"Упаковка, {unit}", $"Таңгак, {unit}", $"Pack, {unit}", $"Paket, {unit}", $"Qadoq, {unit}");
    }

    private EstimateCalcInput? ReadInput(out bool bad)
    {
        bad = false;
        var b = false;
        var input = new EstimateCalcInput
        {
            Material = CurrentMaterial,
            Surface = CurrentMaterial == EstimateMaterial.Wallpaper ? EstimateSurface.Walls : (EstimateSurface)Math.Max(0, _surface.SelectedIndex),
            Length = Get("len", ref b), Width = Get("wid", ref b), Height = Get("hgt", ref b), Area = Get("area", ref b), Openings = Get("open", ref b),
            ReservePercent = Get("res", ref b), PackSize = Get("pack", ref b), Layers = Get("layers", ref b), PerM2 = Get("perm2", ref b),
            RollLength = Get("rl", ref b), RollWidth = Get("rw", ref b), Rapport = Get("rap", ref b),
        };
        bad = b;
        return b ? null : input;
    }

    private void Recalc()
    {
        _error.Text = "";
        _lineInfo.Text = "";
        _linked.Text = _product is null ? "" : EstimateUi.T("Товар: ", "Товар: ", "Product: ", "Ürün: ", "Tovar: ") + _product.Name + " · " + MoneyText.Som(EstimateRules.PriceFor(_product, _doc.Level)) + (string.IsNullOrWhiteSpace(_product.Unit) ? "" : " / " + _product.Unit);
        var input = ReadInput(out var bad);
        if (input is null)
        {
            _last = null;
            _result.Text = "";
            _formula.Text = "";
            _error.Text = bad ? EstimateUi.T("Проверьте числа: только цифры, запятая или точка.", "Сандарды текшериңиз: сандар, үтүр же чекит гана.", "Check the numbers: digits, comma or dot only.", "Sayıları kontrol edin: yalnızca rakam, virgül veya nokta.", "Raqamlarni tekshiring: faqat raqam, vergul yoki nuqta.") : "";
            return;
        }
        var r = EstimateCalc.Run(input);
        _last = r;
        if (!r.IsValid)
        {
            // Пустая форма — не ошибка, а ожидание ввода: текст ошибки показываем, когда уже что-то введено.
            _result.Text = "";
            _formula.Text = "";
            var empty = input.Length == 0m && input.Width == 0m && input.Area == 0m;
            _error.Text = empty ? "" : r.Formula;
            return;
        }
        _result.Text = $"{r.Raw.Packs} {r.PackWord}" + (r.Raw.PackedTotal > 0m && input.Material != EstimateMaterial.Wallpaper ? $" · {EstimateUi.Qty(r.Raw.PackedTotal)} {PackedUnit(input.Material)}" : "");
        _formula.Text = r.Formula;
        var line = EstimateCalc.ToLine(r, _product, _doc.Level);
        if (_product is not null)
            _lineInfo.Text = line is null
                ? EstimateUi.T("Единица товара не подходит к этому материалу — выберите другой товар или «Без товара».", "Товардын бирдиги бул материалга ылайык эмес — башка товарды тандаңыз же «Товарсыз».", "The product unit does not fit this material — pick another product or “No product”.", "Ürün birimi bu malzemeye uymuyor — başka ürün seçin veya «Ürünsüz».", "Tovar birligi bu materialga mos emas — boshqa tovarni tanlang yoki «Tovarsiz».")
                : EstimateUi.T("В смету: ", "Сметага: ", "To the estimate: ", "Keşfe: ", "Smetaga: ") + $"{EstimateUi.Qty(line.Qty)} {line.Unit} × {MoneyText.Amount(line.Price)} = {MoneyText.Som(EstimateRules.LineSum(line, new EstimateDoc { Lines = { line } }))}";
    }

    private static string PackedUnit(EstimateMaterial m) => m is EstimateMaterial.Paint or EstimateMaterial.Primer ? EstimateUi.T("л", "л", "l", "l", "l")
        : EstimateCalc.NormKey(m) is not null ? EstimateUi.T("кг", "кг", "kg", "kg", "kg") : "м²";

    private void FillSuggestions()
    {
        _sugg.Items.Clear();
        var q = (_find.Text ?? "").Trim();
        if (q.Length == 0)
        {
            _sugg.IsVisible = false;
            return;
        }
        foreach (var p in _deps.Catalog.Search(q, 6))
        {
            var item = new ListBoxItem { Tag = p, MinHeight = 40, Content = $"{p.Name} · {MoneyText.Som(EstimateRules.PriceFor(p, _doc.Level))}" };
            var info = p;
            item.Tapped += (_, _) => Link(info);
            _sugg.Items.Add(item);
        }
        _sugg.IsVisible = _sugg.ItemCount > 0;
        if (_sugg.ItemCount > 0)
            _sugg.SelectedIndex = 0;
    }

    /// <summary>Привязать товар (null — убрать). Размер упаковки подсказывается из названия («Плитка 1,44 м²»). Привязка запоминается по материалу.</summary>
    public void Link(EstimateCatalogInfo? info)
    {
        _product = info;
        _sugg.IsVisible = false;
        _filling = true;
        _find.Text = "";
        if (info is not null)
        {
            _deps.Store.SetLink(EstimateCalc.Key(CurrentMaterial), info.Id);
            if (CalcKindFor(CurrentMaterial) is { } kind && ConsumptionCalculator.GuessPackSize(kind, info.Name) is { } size)
                Set("pack", size);
        }
        _filling = false;
        Recalc();
    }

    private static CalcKind? CalcKindFor(EstimateMaterial m) => m switch
    {
        EstimateMaterial.Paint or EstimateMaterial.Primer => CalcKind.Paint,
        EstimateMaterial.Tile => CalcKind.Tile,
        EstimateMaterial.Laminate => CalcKind.Laminate,
        EstimateMaterial.Putty or EstimateMaterial.Plaster or EstimateMaterial.Adhesive or EstimateMaterial.Grout => CalcKind.Mix,
        _ => null,
    };

    /// <summary>Вставить введённое значение поля (для стенда).</summary>
    public void SetField(string key, string text) => _f[key].Box.Text = text;

    public void SelectMaterial(EstimateMaterial m) => _material.SelectedIndex = Array.IndexOf(_materials, m);

    /// <summary>«Добавить»: строка уходит в смету. false — расчёта нет или единица товара не подходит (текст причины в окне).</summary>
    public bool AddLine(bool close)
    {
        if (_last is not { IsValid: true } r)
        {
            _error.Text = _error.Text?.Length > 0 ? _error.Text : EstimateUi.T("Сначала введите размеры.", "Адегенде өлчөмдөрдү киргизиңиз.", "Enter the dimensions first.", "Önce ölçüleri girin.", "Avval o'lchamlarni kiriting.");
            return false;
        }
        var line = EstimateCalc.ToLine(r, _product, _doc.Level);
        if (line is null)
            return false;
        if (!_onAdd(line))
            return false;
        _room = (r.Input.Length, r.Input.Width, r.Input.Height, r.Input.Openings);
        if (close)
            Close(true);
        else
            _lineInfo.Text = EstimateUi.T("Добавлено в смету: ", "Сметага кошулду: ", "Added to the estimate: ", "Keşfe eklendi: ", "Smetaga qo'shildi: ") + line.Name;
        return true;
    }
}
