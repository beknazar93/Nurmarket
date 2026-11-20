using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ5; ответ Умара 19.11 вечер): в карточке товара сферы «Автозапчасти» — блок «Автозапчасть»: каталожный номер (поле «Артикул»,
// другая подпись; его нормализованный вид уходит атрибутом part_no_norm), OEM, кросс-номера, «Подходит к», состояние, ячейка (storage_location), аналоги (related_input).
// Блок виден только в этой сфере; в запрос попадает только то, что владелец менял (у нового товара — что заполнено). Чужие атрибуты карточки возвращаются как есть.
public partial class ProductEditDialog
{
    private TextBox _apOem = null!, _apCross = null!, _apFits = null!, _apLocation = null!;
    private TextBlock _apCrossHint = null!, _apRelatedText = null!;
    private readonly Dictionary<string, Button> _apStateButtons = new();
    private string _apState = "";
    private List<string> _apRelated = new();
    private AutoPartInfo? _apBase;     // то, что известно о карточке (с сервера): чужие атрибуты и исходные значения
    private string _apOrigAttrs = "", _apOrigLocation = "", _apOrigRelated = "";
    private TextBox _apWarranty = null!;
    private CheckBox _apElectric = null!, _apNoReturn = null!;
    private (int? Days, bool Electric, bool NoReturn) _apOrigMeta;
    private bool _apTouched;          // владелец менял блок — подстановка из карточки сервера его не затирает

    private bool AutoPartVisible => AutoPartsProfile.IsActive && AutoPartBlock.IsVisible;

    private void InitAutoPartBlock(CatalogProductTileVm? existing)
    {
        if (!AutoPartsProfile.IsActive)
            return;
        AutoPartBlock.IsVisible = true;
        CodeFieldLabel.Text = Tr.T("Каталожный номер", "Каталог номери", "Part number", "Katalog numarası", "Katalog raqami");

        var panel = AutoPartBlock;
        var head = new TextBlock { Text = Tr.T("Автозапчасть", "Автозапчасть", "Auto part", "Oto yedek parça", "Avtoehtiyot qism"), FontSize = 16, FontWeight = FontWeight.Bold };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        panel.Children.Add(head);

        // Состояние: цепочки-кнопки (обычный ComboBox в этом окне не открывается — см. список брендов).
        panel.Children.Add(Label(Tr.T("Состояние", "Абалы", "Condition", "Durum", "Holati")));
        var states = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var state in AutoPartsProfile.States)
        {
            var key = state;
            var chip = new Button
            {
                Content = key.Length == 0 ? "—" : AutoPartsProfile.StateName(key), Height = 40, MinHeight = 40, Margin = new Thickness(0, 0, 8, 6),
                CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 0), FontSize = 13.5, FontWeight = FontWeight.SemiBold, BorderThickness = new Thickness(1),
            };
            chip.Click += (_, _) => { _apTouched = true; SetState(key); };
            _apStateButtons[key] = chip;
            states.Children.Add(chip);
        }
        panel.Children.Add(states);

        panel.Children.Add(Label(Tr.T("Ячейка (например A-3-2)", "Уяча (мисалы A-3-2)", "Cell (e.g. A-3-2)", "Göz (örn. A-3-2)", "Yacheyka (masalan A-3-2)")));
        _apLocation = new TextBox { MaxLength = 32 };
        _apLocation.TextChanged += (_, _) => _apTouched = true;
        panel.Children.Add(_apLocation);

        panel.Children.Add(Label("OEM"));
        _apOem = new TextBox
        {
            Watermark = Tr.T("Оригинальные номера: через запятую или с новой строки", "Оригиналдуу номерлер: үтүр менен же жаңы саптан",
                "Original numbers: comma or new line", "Orijinal numaralar: virgülle veya yeni satır", "Original raqamlar: vergul yoki yangi qator"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44,
        };
        _apOem.TextChanged += (_, _) => _apTouched = true;
        panel.Children.Add(_apOem);

        panel.Children.Add(Label(Tr.T("Кросс-номера (другие производители)", "Кросс-номерлер (башка өндүрүүчүлөр)", "Cross numbers (other makers)", "Çapraz numaralar (diğer üreticiler)", "Kross-raqamlar (boshqa ishlab chiqaruvchilar)")));
        _apCross = new TextBox
        {
            Watermark = Tr.T("Например: Mann W 811/80, Knecht OC 983 — пробелы и дефисы можно", "Мисалы: Mann W 811/80, Knecht OC 983 — боштук жана дефис мүмкүн",
                "E.g. Mann W 811/80, Knecht OC 983 — spaces and dashes are fine", "Örn: Mann W 811/80, Knecht OC 983 — boşluk ve tire olabilir", "Masalan: Mann W 811/80, Knecht OC 983 — bo'shliq va tire mumkin"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
        };
        _apCross.TextChanged += (_, _) => { _apTouched = true; UpdateCrossHint(); };
        panel.Children.Add(_apCross);
        _apCrossHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _apCrossHint.Bind(TextBlock.ForegroundProperty, _apCrossHint.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(_apCrossHint);

        panel.Children.Add(Label(Tr.T("Подходит к (по строке на авто)", "Ылайыктуу (ар бир авто — өз сабында)", "Fits (one car per line)", "Uyumlu (her araç bir satır)", "Mos keladi (har avto alohida qatorda)")));
        _apFits = new TextBox
        {
            Watermark = "Toyota Camry 2012-2017", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
        };
        _apFits.TextChanged += (_, _) => _apTouched = true;
        panel.Children.Add(_apFits);

        // Аналоги: выбор карточек из каталога (related_input).
        var relatedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _apRelatedText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _apRelatedText.Bind(TextBlock.ForegroundProperty, _apRelatedText.GetResourceObservable("BrushTextSoft"));
        var pick = new Button { Content = Tr.T("Выбрать аналоги…", "Аналогдорду тандоо…", "Choose analogues…", "Muadil seç…", "Analoglarni tanlash…"), Height = 40, MinHeight = 40, Padding = new Thickness(14, 0) };
        pick.Click += async (_, _) => await PickAnalogsAsync().ConfigureAwait(true);
        Grid.SetColumn(pick, 1);
        relatedRow.Children.Add(_apRelatedText);
        relatedRow.Children.Add(pick);
        panel.Children.Add(relatedRow);

        // 2026-11-20 (склейка с исполнителем Б): местные признаки для чека/возврата — гарантия, «Электрика», «Установленные не принимаются» (AutoPartsProductMeta, файл кассы, на сервер не уходят).
        panel.Children.Add(Label(Tr.T("Гарантия, дней", "Кепилдик, күн", "Warranty, days", "Garanti, gün", "Kafolat, kun")));
        _apWarranty = new TextBox { MaxLength = 4, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Watermark = Tr.T("нет", "жок", "none", "yok", "yo'q") };
        _apWarranty.TextChanged += (_, _) =>
        {
            var digits = new string((_apWarranty.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            if (digits != (_apWarranty.Text ?? ""))
                _apWarranty.Text = digits;
        };
        panel.Children.Add(_apWarranty);
        _apElectric = new CheckBox { MinHeight = 40, Content = WrapText(Tr.T("Электрика / электроника", "Электрика / электроника", "Electrical / electronics", "Elektrik / elektronik", "Elektrika / elektronika")) };
        _apNoReturn = new CheckBox { MinHeight = 40, Content = WrapText(Tr.T("Установленные детали не принимаются", "Орнотулган тетиктер кабыл алынбайт", "Installed parts are not accepted", "Takılmış parçalar iade alınmaz", "O'rnatilgan detallar qabul qilinmaydi")) };
        panel.Children.Add(_apElectric);
        panel.Children.Add(_apNoReturn);
        var meta = existing is null ? null : AutoPartsProductMeta.Get(existing.Id);
        _apWarranty.Text = meta?.WarrantyDays?.ToString() ?? "";
        _apElectric.IsChecked = meta?.Electric ?? false;
        _apNoReturn.IsChecked = meta?.NoReturn ?? false;
        _apOrigMeta = (AutoPartsProductMeta.ParseDays(_apWarranty.Text), _apElectric.IsChecked == true, _apNoReturn.IsChecked == true);

        _apBase = existing is null ? null : PartMetaStore.Get(existing.Id)?.Clone();
        FillAutoPart(_apBase);
        RememberAutoPartOriginal();
    }

    private static TextBlock WrapText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>2026-11-20: местные признаки товара — после записи карточки (у нового — когда известен id). Пишем, только если владелец что-то поставил/изменил: без отметки чек решает по названию.</summary>
    private void SaveAutoPartMeta(string? productId)
    {
        if (!AutoPartVisible || string.IsNullOrWhiteSpace(productId))
            return;
        var now = (AutoPartsProductMeta.ParseDays(_apWarranty.Text), _apElectric.IsChecked == true, _apNoReturn.IsChecked == true);
        if (now == _apOrigMeta || (_existing is null && now == (null, false, false)))
            return;
        AutoPartsProductMeta.Set(productId, now.Item1, now.Item2, now.Item3);
        _apOrigMeta = now;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0) };
        t.Classes.Add("FieldLabel");
        return t;
    }

    private void SetState(string state)
    {
        _apState = state;
        foreach (var (key, button) in _apStateButtons)
        {
            var on = key == state;
            button.Bind(Button.BackgroundProperty, button.GetResourceObservable(on ? "BrushAccentSoft" : "BrushPanel"));
            button.Bind(Button.BorderBrushProperty, button.GetResourceObservable(on ? "BrushAccent" : "BrushBorderStrong"));
        }
    }

    private void FillAutoPart(AutoPartInfo? info)
    {
        _apOem.Text = info is null ? "" : string.Join(", ", info.Oem);
        _apCross.Text = info is null ? "" : string.Join("\n", info.Cross);
        _apFits.Text = info is null ? "" : string.Join("\n", info.Fits);
        _apLocation.Text = info?.Location ?? "";
        _apRelated = info is null ? new List<string>() : new List<string>(info.Related);
        SetState(info?.State ?? "");
        UpdateRelatedText();
        UpdateCrossHint();
    }

    private void UpdateRelatedText() =>
        _apRelatedText.Text = _apRelated.Count == 0
            ? Tr.T("Аналоги не выбраны", "Аналогдор тандалган жок", "No analogues chosen", "Muadil seçilmedi", "Analoglar tanlanmagan")
            : Tr.T($"Выбрано аналогов: {_apRelated.Count}", $"Тандалган аналогдор: {_apRelated.Count}", $"Analogues chosen: {_apRelated.Count}", $"Seçilen muadil: {_apRelated.Count}", $"Tanlangan analoglar: {_apRelated.Count}");

    /// <summary>Мягкий лимит тарифа «Старт»: больше 5 кросс-номеров — только подсказка, сохранение не блокируется.</summary>
    private void UpdateCrossHint()
    {
        var count = PartNumbers.ParseList(_apCross.Text).Count;
        _apCrossHint.IsVisible = count > 0;
        _apCrossHint.Text = Tr.T($"Номеров: {count}", $"Номерлер: {count}", $"Numbers: {count}", $"Numara: {count}", $"Raqamlar: {count}")
                            // 2026-11-20 (живая проверка): подсказка про тариф «Стандарт» показывалась и на самом «Стандарте» — теперь только на «Старте».
                            + (count > AutoPartInfo.SoftCrossLimit && TariffGate.IsStartTariff ? ". " + AutoPartsProfile.CrossLimitHint : "");
    }

    /// <summary>Данные блока из полей окна. Чужие атрибуты карточки берутся из известной записи.</summary>
    private AutoPartInfo BuildAutoPartInfo()
    {
        var info = new AutoPartInfo
        {
            PartNo = PartNumbers.Norm(_code),
            Oem = PartNumbers.ParseList(_apOem.Text).Take(AutoPartInfo.MaxOem).ToList(),
            Cross = PartNumbers.ParseList(_apCross.Text).Take(AutoPartInfo.MaxCross).ToList(),
            Fits = (_apFits.Text ?? "").Split('\n', '\r').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Length > AutoPartInfo.MaxValueLength ? x[..AutoPartInfo.MaxValueLength] : x).Take(AutoPartInfo.MaxFits).ToList(),
            Location = (_apLocation.Text ?? "").Trim(),
            Related = new List<string>(_apRelated),
            Other = _apBase?.Other.Select(o => new AutoPartAttr { Key = o.Key, Value = o.Value, Unit = o.Unit }).ToList() ?? new(),
        };
        (info.Condition, info.PartType) = AutoPartInfo.FromState(_apState);
        return info;
    }

    private static string AttrsSignature(AutoPartInfo info) => JsonSerializer.Serialize(info.BuildAttributes());

    private void RememberAutoPartOriginal()
    {
        var info = BuildAutoPartInfo();
        _apOrigAttrs = AttrsSignature(info);
        _apOrigLocation = info.Location;
        _apOrigRelated = string.Join(",", info.Related);
        _apTouched = false;
    }

    /// <summary>Карточка с сервера (после открытия окна): подставляем значения, пока владелец сам блок не трогал.</summary>
    private void ApplyAutoPartFromDetail(JsonElement card)
    {
        if (!AutoPartVisible || _apTouched || _existing is null)
            return;
        PartMetaStore.Remember(_existing.Id, card);
        var info = AutoPartInfo.Parse(card);
        _apBase = info?.Clone();
        FillAutoPart(_apBase);
        RememberAutoPartOriginal(); // подстановка из карточки — не правка владельца
    }

    /// <summary>Поля запчасти в запрос: у нового товара — что заполнено, у существующего — только что владелец менял. Не сфера «Автозапчасти» — запрос не меняется.</summary>
    private void ApplyAutoPartRequest(ProductEditRequest request)
    {
        if (!AutoPartVisible)
            return;
        var info = BuildAutoPartInfo();
        var attrs = AttrsSignature(info);
        if (_existing is null)
        {
            if (info.BuildAttributes().Count > 0)
                request.Attributes = info.BuildAttributes();
            if (info.Location.Length > 0)
            {
                request.SendStorageLocation = true;
                request.StorageLocation = info.Location;
            }
            if (info.Related.Count > 0)
                request.RelatedInput = info.Related;
            return;
        }
        if (!string.Equals(attrs, _apOrigAttrs, StringComparison.Ordinal))
            request.Attributes = info.BuildAttributes();
        if (!string.Equals(info.Location, _apOrigLocation, StringComparison.Ordinal))
        {
            request.SendStorageLocation = true;
            request.StorageLocation = info.Location;
        }
        if (!string.Equals(string.Join(",", info.Related), _apOrigRelated, StringComparison.Ordinal))
            request.RelatedInput = info.Related;
    }

    /// <summary>После записи существующей карточки: запомнить значения блока до следующей загрузки каталога.</summary>
    private void RememberAutoPartAfterSave(string productId, ProductEditRequest request)
    {
        if (!AutoPartVisible || (request.Attributes is null && !request.SendStorageLocation && request.RelatedInput is null))
            return;
        PartMetaStore.Set(productId, BuildAutoPartInfo());
        PartMetaStore.SaveIfDirty();
    }

    private async Task PickAnalogsAsync()
    {
        var picker = new AnalogPickerWindow(_existing?.Id, _apRelated);
        if (await picker.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            _apRelated = picker.Selected.ToList();
            _apTouched = true;
            UpdateRelatedText();
        }
    }
}

/// <summary>2026-11-20 (АЗ5): выбор аналогов из каталога кассы — поиск по названию, номеру и бренду; отмеченные карточки уходят в related_input (до 100).</summary>
internal sealed class AnalogPickerWindow : Window
{
    private readonly HashSet<string> _selected;
    private readonly string? _selfId;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _count = new() { FontSize = 13 };
    private readonly TextBox _search = new();

    public IReadOnlyCollection<string> Selected => _selected;

    public AnalogPickerWindow(string? selfId, IEnumerable<string> current)
    {
        _selfId = selfId;
        _selected = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        Title = Tr.T("Аналоги товара", "Товардын аналогдору", "Product analogues", "Ürün muadilleri", "Mahsulot analoglari");
        SectionCards.AsModal(this, 672);

        _search.Watermark = Tr.T("Название, номер или бренд", "Аталышы, номери же бренд", "Name, number or brand", "Ad, numara veya marka", "Nomi, raqami yoki brend");
        SectionCards.Field(_search);
        _search.TextChanged += (_, _) => Rebuild();
        _count.Bind(TextBlock.ForegroundProperty, _count.GetResourceObservable("BrushTextSoft"));
        var scroll = new ScrollViewer { MaxHeight = 340, Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(_search);
        body.Children.Add(scroll);
        body.Children.Add(_count);

        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        var ok = SectionCards.Primary(Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: false);
        ok.Click += (_, _) => Close(true);
        Content = SectionCards.Shell(Tr.T("Аналоги товара", "Товардын аналогдору", "Product analogues", "Ürün muadilleri", "Mahsulot analoglari"), null, body, SectionCards.Buttons(cancel, ok));
        Opened += (_, _) => { Rebuild(); _search.Focus(); };
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        var query = (_search.Text ?? "").Trim();
        var catalog = CatalogCacheService.Products.Where(p => !string.Equals(p.Id, _selfId, StringComparison.OrdinalIgnoreCase)).ToList();
        IEnumerable<CatalogProductTileVm> found;
        if (query.Length < 2)
            found = catalog.Where(p => _selected.Contains(p.Id));
        else
        {
            var partQuery = PartNumbers.LooksLikePart(query) ? PartNumbers.Norm(query) : "";
            found = SearchMatch.Products(catalog, query, p => partQuery.Length > 0 && p.PartRank(partQuery) < PartNumbers.NoMatch)
                .OrderBy(p => _selected.Contains(p.Id) ? 0 : 1).ThenBy(p => partQuery.Length > 0 ? p.PartRank(partQuery) : 0);
        }
        foreach (var p in found.Take(40))
        {
            var product = p;
            var line = product.Title + (product.PartLine.Length > 0 ? "  ·  " + product.PartLine : "");
            var box = new CheckBox { Content = line, IsChecked = _selected.Contains(product.Id), MinHeight = 40 };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true && _selected.Count < 100)
                    _selected.Add(product.Id);
                else
                    _selected.Remove(product.Id);
                if (box.IsChecked == true && !_selected.Contains(product.Id))
                    box.IsChecked = false; // больше 100 аналогов сервер не принимает
                UpdateCount();
            };
            _rows.Children.Add(box);
        }
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = Tr.T($"Выбрано: {_selected.Count} (не больше 100)", $"Тандалган: {_selected.Count} (100дөн ашык эмес)", $"Chosen: {_selected.Count} (max 100)", $"Seçilen: {_selected.Count} (en fazla 100)", $"Tanlangan: {_selected.Count} (100 tagacha)");
}
