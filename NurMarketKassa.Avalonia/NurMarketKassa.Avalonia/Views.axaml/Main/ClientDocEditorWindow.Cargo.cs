using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-21 («Базар», опт, модуль «Накладная + карго», ТЗ Контейнер-базар 3.6): карточка «Карго» в редакторе накладной. Галочка «Карго» включает колонки «Мест» и «Брутто, кг» в печатной накладной
// и блок «Карго» под итогами. В карточке: по каждой строке — места и вес брутто; на весь документ — мешков, перевозчик, город / адрес получателя, трек-номер. Всё хранится в самом документе
// (BazaarCargo: Extra документа и строк) — формат документа и обмен с телефоном не меняются. Карточка есть только у накладной при включённом модуле. Раскладка — Grid с общими колонками
// (подпись над полем, высоты полей 44, чек-лист новых окон).
public sealed partial class ClientDocEditorWindow
{
    private Border? _cargoCard;
    private readonly CheckBox _cargoOn = new() { MinHeight = 44 };
    private StackPanel? _cargoBody;
    private StackPanel? _cargoRows;
    private TextBox? _cargoBags, _cargoCarrier, _cargoCity, _cargoTrack;
    private TextBlock? _cargoTotals;

    /// <summary>Карточка «Карго» для списка карточек формы (скрыта, если не накладная или модуль выключен).</summary>
    private Border BuildCargoCard()
    {
        _cargoOn.Content = T("Карго: места и вес по строкам, перевозчик, куда", "Карго: саптар боюнча орун жана салмак, ташуучу, кайда", "Cargo: places and weight per line, carrier, destination",
            "Kargo: satır başına koli ve ağırlık, taşıyıcı, varış", "Kargo: qatorlar bo'yicha joy va og'irlik, tashuvchi, qayerga");
        _cargoOn.IsCheckedChanged += (_, _) =>
        {
            if (_filling)
                return;
            SaveCargo();
            if (_cargoBody is not null)
                _cargoBody.IsVisible = _cargoOn.IsChecked == true;
        };

        _cargoBags = Box(T("Мешков", "Баштык", "Bags", "Çuval", "Qop"), _ => SaveCargo());
        _cargoCarrier = Box(T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), _ => SaveCargo());
        _cargoCity = Box(T("Куда (город, адрес)", "Кайда (шаар, дарек)", "To (city, address)", "Nereye (şehir, adres)", "Qayerga (shahar, manzil)"), _ => SaveCargo());
        _cargoTrack = Box(T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), _ => SaveCargo());
        _cargoBags.MaxLength = 5;
        _cargoCarrier.MaxLength = 60;
        _cargoCity.MaxLength = 120;
        _cargoTrack.MaxLength = 60;
        foreach (var b in new[] { _cargoBags, _cargoCarrier, _cargoCity, _cargoTrack })
        {
            b.Height = 44;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        _cargoRows = new StackPanel { Spacing = 6 };
        _cargoTotals = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Res(_cargoTotals, TextBlock.ForegroundProperty, "BrushTextSoft");

        var head = CargoLineGrid(Th(T("Товар", "Товар", "Item", "Ürün", "Tovar")), Th(T("Мест", "Орун", "Places", "Koli", "Joy"), true), Th(T("Брутто, кг", "Брутто, кг", "Gross, kg", "Brüt, kg", "Brutto, kg"), true));
        _cargoBody = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                head, _cargoRows, _cargoTotals,
                TwoCols(Field(T("Мешков", "Баштык", "Bags", "Çuval", "Qop"), _cargoBags), Field(T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), _cargoCarrier)),
                TwoCols(Field(T("Куда (город, адрес)", "Кайда (шаар, дарек)", "To (city, address)", "Nereye (şehir, adres)", "Qayerga (shahar, manzil)"), _cargoCity),
                    Field(T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), _cargoTrack)),
            },
        };
        _cargoCard = Card(T("Карго", "Карго", "Cargo", "Kargo", "Kargo"), _cargoOn, _cargoBody);
        _cargoCard.IsVisible = _doc.Kind == ClientDocKind.Waybill && BazaarModules.InvoiceCargoOn;
        return _cargoCard;
    }

    /// <summary>Колонки карточки: товар (растягивается), «Мест» 96, «Брутто, кг» 120 — общие у заголовка и строк.</summary>
    private static Grid CargoLineGrid(params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,96,10,120") };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i == 0 ? 0 : i * 2);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    /// <summary>Заполнить карточку из документа (вызывается после FillFromDoc и после каждой перестройки строк).</summary>
    private void RefreshCargo()
    {
        if (_cargoCard is null || _cargoRows is null)
            return;
        var applicable = _doc.Kind == ClientDocKind.Waybill && BazaarModules.InvoiceCargoOn;
        _cargoCard.IsVisible = applicable;
        if (!applicable)
            return;
        var was = _filling;
        _filling = true;
        try
        {
            var info = BazaarCargo.Read(_doc);
            // Новая накладная опту — «Карго» включено сразу (опт отгружает карго), остальным — по желанию.
            if (_isNew && _doc.Extra?.ContainsKey(BazaarCargo.DocKey) != true && BazaarModules.WholesaleMode)
            {
                info = info with { Enabled = true };
                BazaarCargo.Write(_doc, info);
            }
            _cargoOn.IsChecked = info.Enabled;
            if (_cargoBody is not null)
                _cargoBody.IsVisible = info.Enabled;
            _cargoBags!.Text = info.Bags > 0 ? info.Bags.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            _cargoCarrier!.Text = info.Carrier;
            _cargoCity!.Text = info.City;
            _cargoTrack!.Text = info.Track;
            _cargoRows.Children.Clear();
            foreach (var line in _doc.Lines)
            {
                var l = line;
                var name = new TextBlock { Text = string.IsNullOrWhiteSpace(l.Name) ? "—" : l.Name, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
                Res(name, TextBlock.ForegroundProperty, "BrushText");
                var places = Box(T("Мест", "Орун", "Places", "Koli", "Joy"), _ => { });
                var gross = Box(T("Брутто, кг", "Брутто, кг", "Gross, kg", "Brüt, kg", "Brutto, kg"), _ => { });
                places.Height = gross.Height = 44;
                places.HorizontalAlignment = gross.HorizontalAlignment = HorizontalAlignment.Stretch;
                places.MaxLength = 5;
                gross.MaxLength = 9;
                places.Text = BazaarCargo.Places(l) is > 0 and var p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                gross.Text = BazaarCargo.Gross(l) is > 0 and var g ? g.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "";
                void Save()
                {
                    if (_filling)
                        return;
                    if (BazaarCargo.ParsePlaces(places.Text) is not { } pl || BazaarCargo.ParseGross(gross.Text) is not { } gr)
                        return; // пока введено не число — не записываем, ждём правильный ввод
                    BazaarCargo.SetLine(l, pl, gr);
                    UpdateCargoTotals();
                    Changed();
                }
                places.TextChanged += (_, _) => Save();
                gross.TextChanged += (_, _) => Save();
                _cargoRows.Children.Add(CargoLineGrid(name, places, gross));
            }
            UpdateCargoTotals();
        }
        finally
        {
            _filling = was;
        }
    }

    private void UpdateCargoTotals()
    {
        if (_cargoTotals is null)
            return;
        var places = BazaarCargo.TotalPlaces(_doc);
        var gross = BazaarCargo.TotalGross(_doc);
        var gross3 = MoneyText.Weight(gross); // 2026-11-27: вес одним форматтером (NumberFormatRatchet)
        _cargoTotals.Text = T($"Всего мест: {places} · брутто: {gross3} кг", $"Бардыгы орун: {places} · брутто: {gross3} кг", $"Total places: {places} · gross: {gross3} kg",
            $"Toplam koli: {places} · brüt: {gross3} kg", $"Jami joy: {places} · brutto: {gross3} kg");
    }

    private void SaveCargo()
    {
        if (_filling || _cargoBags is null || _cargoCarrier is null || _cargoCity is null || _cargoTrack is null)
            return;
        var bags = int.TryParse((_cargoBags.Text ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        BazaarCargo.Write(_doc, new CargoInfo(_cargoOn.IsChecked == true, bags, _cargoCarrier.Text ?? "", _cargoCity.Text ?? "", _cargoTrack.Text ?? ""));
        Changed();
    }
}
