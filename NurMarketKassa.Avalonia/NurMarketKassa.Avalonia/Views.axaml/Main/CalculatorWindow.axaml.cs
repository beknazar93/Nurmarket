using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>«Помощник: калькуляция» программы владельца (2026-09-26). Пять вкладок по образцу
/// конкурентов: цена и наценка с округлением и налогом (Эвотор, Контур.Маркет), себестоимость
/// партии с распределением доп. расходов (МойСклад), проверка цен всего склада и стоимость склада
/// (Loyverse), безубыточность и цель, скидка/акция. Считает на месте и на сервер ничего не
/// пишет — цены меняются в «Складе».
///
/// Формулы: наценка = (Ц − З) / З; маржа = (Ц − З) / Ц; цена от маржи с налогом с выручки t:
/// Ц = З / (1 − М − t); безубыточность = постоянные расходы / (маржа − t); чтобы скидка d при марже
/// m принесла ту же прибыль, продавать надо в m / (m − d) раз больше.</summary>
public partial class CalculatorWindow : Window
{
    private static readonly double[] TaxPresets = { 0, 0.5, 1, 2, 4 };
    private static readonly double[] RoundSteps = { 0, 1, 5, 10 };

    private readonly ObservableCollection<BatchRow> _batchRows = new();
    private readonly List<AuditRow> _auditAll = new();
    private IReadOnlyList<CatalogProductTileVm> _products = Array.Empty<CatalogProductTileVm>();

    private string _tab = "price";
    private string _mode = "markup";
    private string _split = "sum";
    private string _auditFilter = "all";
    // 2026-09-26, «калькуляция цен неправильно»: по умолчанию цена округлялась вверх до 1 сом, и
    // 35 + 30% показывалось как 46 без расчётных 45,50. Теперь по умолчанию точная цена, округление —
    // по выбору, и тогда рядом видна цена до округления.
    private double _roundStep = 0;
    private double? _currentPrice;
    private bool _salesLoaded;
    private double? _monthRevenue;
    private double? _monthGrossProfit;
    private bool _ready;

    public CalculatorWindow()
    {
        InitializeComponent();
        for (var i = 0; i < 3; i++)
            _batchRows.Add(new BatchRow());
        BatchGrid.ItemsSource = _batchRows;
        LoadProducts();
        BuildChips();
        ApplyTexts();
        _ready = true;
        RecalcPrice();
        RecalcBatch();
        RecalcBreakEven();
        RecalcPromo();
        Tr.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        ApplyTexts();
        BuildChips();
        RecalcAll();
    });

    // ------------------------------------------------------------------ данные

    private void LoadProducts()
    {
        try
        {
            LocalProductRepository.Instance.EnsureSchema();
            _products = LocalProductRepository.Instance.LoadAllTiles();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Калькуляция: каталог не прочитан: {ex.Message}", "WARNING");
            _products = Array.Empty<CatalogProductTileVm>();
        }

        _auditAll.Clear();
        foreach (var p in _products)
        {
            if (p.IsBundle)
                continue;
            _auditAll.Add(new AuditRow(p.Title, p.PurchasePrice, LocalCartService.ParsePrice(p.PriceLine), p.Quantity, p.Unit ?? ""));
        }
    }

    // ------------------------------------------------------------------ тексты

    private void ApplyTexts()
    {
        Title = Tr.T("Калькуляция", "Калькуляция", "Pricing calculator", "Hesaplama", "Kalkulyatsiya");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T("Помощник владельца: цена, себестоимость, проверка цен, безубыточность и акции",
            "Ээсинин жардамчысы: баа, өздүк нарк, бааларды текшерүү, зыянсыздык жана акциялар",
            "Owner's helper: price, landed cost, price check, break-even and promotions",
            "Sahip yardımcısı: fiyat, maliyet, fiyat kontrolü, başabaş ve kampanyalar",
            "Egasi yordamchisi: narx, tannarx, narxlarni tekshirish, zararsizlik va aksiyalar");

        TabPriceButton.Content = Tr.T("Цена и наценка", "Баа жана үстөк", "Price & markup", "Fiyat ve kâr oranı", "Narx va ustama");
        TabBatchButton.Content = Tr.T("Себестоимость партии", "Партиянын өздүк наркы", "Batch landed cost", "Parti maliyeti", "Partiya tannarxi");
        TabAuditButton.Content = Tr.T("Проверка цен", "Бааларды текшерүү", "Price check", "Fiyat kontrolü", "Narxlarni tekshirish");
        TabBreakEvenButton.Content = Tr.T("Безубыточность и цель", "Зыянсыздык жана максат", "Break-even & goal", "Başabaş ve hedef", "Zararsizlik va maqsad");
        TabPromoButton.Content = Tr.T("Скидка и акция", "Арзандатуу жана акция", "Discount & promo", "İndirim ve kampanya", "Chegirma va aksiya");

        // 1. Цена
        PriceInputsTitle.Text = Tr.T("Исходные данные", "Баштапкы маалымат", "Inputs", "Girdiler", "Boshlang'ich ma'lumot");
        ProductSearchLabel.Text = Tr.T("Товар (необязательно) — подставит закупку и текущую цену", "Товар (милдеттүү эмес) — сатып алуу жана учурдагы бааны коёт",
            "Product (optional) — fills in cost and current price", "Ürün (isteğe bağlı) — alış ve mevcut fiyatı doldurur", "Mahsulot (ixtiyoriy) — xarid va joriy narxni qo'yadi");
        ProductSearchBox.Watermark = Tr.T("Название или штрихкод…", "Аталышы же штрихкоду…", "Name or barcode…", "Ad veya barkod…", "Nomi yoki shtrix-kodi…");
        CostLabel.Text = Tr.T("Закупочная цена (себестоимость), сом", "Сатып алуу баасы (өздүк нарк), сом", "Purchase cost, som", "Alış maliyeti, som", "Xarid narxi (tannarx), so'm");
        ModeLabel.Text = Tr.T("Как считать цену", "Бааны кантип эсептөө", "How to set the price", "Fiyat nasıl hesaplansın", "Narxni qanday hisoblash");
        ModeMarkupButton.Content = Tr.T("От наценки %", "Үстөктөн %", "From markup %", "Kâr oranından %", "Ustamadan %");
        ModeMarginButton.Content = Tr.T("От маржи %", "Маржадан %", "From margin %", "Marjdan %", "Marjadan %");
        ModePriceButton.Content = Tr.T("Проверить цену", "Бааны текшерүү", "Check a price", "Fiyatı kontrol et", "Narxni tekshirish");
        TaxLabel.Text = Tr.T("Налог с выручки, %", "Кирешеден салык, %", "Tax on revenue, %", "Ciro vergisi, %", "Tushumdan soliq, %");
        TaxHint.Text = Tr.T(
            "Кыргызстан: единый налог для торговли — 0,5% при выручке до 50 млн сом в год (выше — 4% наличные / 2% безнал); налог с продаж — 1–2%; патент — 0%. Ставку уточните у бухгалтера.",
            "Кыргызстан: соода үчүн бирдиктүү салык — жылына 50 млн сомго чейин 0,5% (андан жогору — накталай 4% / накталай эмес 2%); сатуудан салык — 1–2%; патент — 0%. Ставканы бухгалтерден тактаңыз.",
            "Kyrgyzstan: single tax for retail — 0.5% up to 50 M som a year (above that 4% cash / 2% non-cash); sales tax 1–2%; patent 0%. Check the rate with your accountant.",
            "Kırgızistan: ticarette tek vergi — yılda 50 milyon soma kadar %0,5 (üstünde nakit %4 / nakitsiz %2); satış vergisi %1–2; patent %0. Oranı muhasebecinize danışın.",
            "Qirg'iziston: savdo uchun yagona soliq — yiliga 50 mln so'mgacha 0,5% (undan yuqori — naqd 4% / naqdsiz 2%); savdo solig'i 1–2%; patent 0%. Stavkani buxgalterdan aniqlang.");
        RoundLabel.Text = Tr.T("Округлять цену вверх до", "Бааны жогору карай тегеректөө", "Round the price up to", "Fiyatı yukarı yuvarla", "Narxni yuqoriga yaxlitlash");
        PriceResultTitle.Text = Tr.T("Результат", "Жыйынтык", "Result", "Sonuç", "Natija");
        ResPriceLabel.Text = Tr.T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi");
        ResProfitLabel.Text = Tr.T("Прибыль с единицы", "Бирдиктен пайда", "Profit per unit", "Birim başına kâr", "Birlikdan foyda");
        ResTaxLabel.Text = Tr.T("Налог с единицы", "Бирдиктен салык", "Tax per unit", "Birim başına vergi", "Birlikdan soliq");
        ResMarkupLabel.Text = Tr.T("Наценка", "Үстөк", "Markup", "Kâr oranı", "Ustama");
        ResMarginLabel.Text = Tr.T("Маржа", "Маржа", "Margin", "Marj", "Marja");
        FormulaHint.Text = Tr.T(
            "Наценка = (цена − закупка) / закупка. Маржа = (цена − закупка) / цена. Наценка 30% — это маржа 23%, наценка 100% — маржа 50%.",
            "Үстөк = (баа − сатып алуу) / сатып алуу. Маржа = (баа − сатып алуу) / баа. 30% үстөк — 23% маржа, 100% үстөк — 50% маржа.",
            "Markup = (price − cost) / cost. Margin = (price − cost) / price. A 30% markup is a 23% margin; 100% markup is 50% margin.",
            "Kâr oranı = (fiyat − maliyet) / maliyet. Marj = (fiyat − maliyet) / fiyat. %30 kâr oranı %23 marj, %100 kâr oranı %50 marjdır.",
            "Ustama = (narx − xarid) / xarid. Marja = (narx − xarid) / narx. 30% ustama — 23% marja, 100% ustama — 50% marja.");

        // 2. Партия
        ExtraCostsLabel.Text = Tr.T("Доп. расходы на партию (доставка, таможня), сом", "Партияга кошумча чыгым (жеткирүү, бажы), сом",
            "Extra costs for the batch (delivery, customs), som", "Parti ek masrafları (nakliye, gümrük), som", "Partiyaga qo'shimcha xarajat (yetkazish, bojxona), so'm");
        RateLabel.Text = Tr.T("Курс валюты закупки (1 — если в сомах)", "Сатып алуу валютасынын курсу (сом болсо 1)", "Exchange rate (1 if in som)", "Döviz kuru (som ise 1)", "Valyuta kursi (so'mda bo'lsa 1)");
        BatchMarkupLabel.Text = Tr.T("Наценка для цены продажи, %", "Сатуу баасы үчүн үстөк, %", "Markup for selling price, %", "Satış fiyatı için kâr oranı, %", "Sotuv narxi uchun ustama, %");
        SplitLabel.Text = Tr.T("Расходы распределить", "Чыгымды бөлүштүрүү", "Split costs by", "Masrafı dağıt", "Xarajatni taqsimlash");
        SplitSumButton.Content = Tr.T("по сумме", "сумма боюнча", "amount", "tutara göre", "summa bo'yicha");
        SplitQtyButton.Content = Tr.T("по количеству", "саны боюнча", "quantity", "miktara göre", "soni bo'yicha");
        SplitWeightButton.Content = Tr.T("по весу", "салмагы боюнча", "weight", "ağırlığa göre", "og'irligi bo'yicha");
        var batchHeaders = new[]
        {
            Tr.T("Товар", "Товар", "Item", "Ürün", "Mahsulot"),
            Tr.T("Кол-во", "Саны", "Qty", "Miktar", "Soni"),
            Tr.T("Цена закупки", "Сатып алуу баасы", "Unit cost", "Birim alış", "Xarid narxi"),
            Tr.T("Вес, кг", "Салмагы, кг", "Weight, kg", "Ağırlık, kg", "Og'irlik, kg"),
            Tr.T("Доля расходов", "Чыгымдын үлүшү", "Cost share", "Masraf payı", "Xarajat ulushi"),
            Tr.T("Себестоимость ед.", "Бирдиктин өздүк наркы", "Landed unit cost", "Birim maliyet", "Birlik tannarxi"),
            Tr.T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"),
        };
        for (var i = 0; i < batchHeaders.Length && i < BatchGrid.Columns.Count; i++)
            BatchGrid.Columns[i].Header = batchHeaders[i];
        AddRowButton.Content = Tr.T("+ Строка", "+ Сап", "+ Row", "+ Satır", "+ Qator");
        RemoveRowButton.Content = Tr.T("Удалить строку", "Сапты өчүрүү", "Delete row", "Satırı sil", "Qatorni o'chirish");

        // 3. Проверка цен
        StockCostLabel.Text = Tr.T("Склад по закупке", "Кампа сатып алуу боюнча", "Stock at cost", "Stok (alış)", "Ombor xarid bo'yicha");
        StockRetailLabel.Text = Tr.T("Склад по цене продажи", "Кампа сатуу баасы боюнча", "Stock at retail", "Stok (satış)", "Ombor sotuv narxida");
        StockProfitLabel.Text = Tr.T("Будущая прибыль склада", "Кампанын келечектеги пайдасы", "Potential profit", "Potansiyel kâr", "Kutilayotgan foyda");
        StockMarginLabel.Text = Tr.T("Средняя маржа склада", "Кампанын орточо маржасы", "Average stock margin", "Ortalama stok marjı", "Omborning o'rtacha marjasi");
        ThresholdLabel.Text = Tr.T("Порог маржи, %", "Маржанын чеги, %", "Margin threshold, %", "Marj eşiği, %", "Marja chegarasi, %");
        AuditSearchBox.Watermark = Tr.T("Поиск товара…", "Товар издөө…", "Search product…", "Ürün ara…", "Mahsulot qidirish…");
        var auditHeaders = new[]
        {
            Tr.T("Товар", "Товар", "Item", "Ürün", "Mahsulot"),
            Tr.T("Закупка", "Сатып алуу", "Cost", "Alış", "Xarid"),
            Tr.T("Цена", "Баа", "Price", "Fiyat", "Narx"),
            Tr.T("Наценка", "Үстөк", "Markup", "Kâr oranı", "Ustama"),
            Tr.T("Маржа", "Маржа", "Margin", "Marj", "Marja"),
            Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"),
            Tr.T("Прибыль остатка", "Калдыктын пайдасы", "Stock profit", "Stok kârı", "Qoldiq foydasi"),
            Tr.T("Внимание", "Көңүл буруңуз", "Attention", "Dikkat", "Diqqat"),
        };
        for (var i = 0; i < auditHeaders.Length && i < AuditGrid.Columns.Count; i++)
            AuditGrid.Columns[i].Header = auditHeaders[i];

        // 4. Безубыточность
        CostsTitle.Text = Tr.T("Расходы в месяц и показатели", "Айлык чыгымдар жана көрсөткүчтөр", "Monthly costs and figures", "Aylık giderler ve göstergeler", "Oylik xarajatlar va ko'rsatkichlar");
        RentLabel.Text = Tr.T("Аренда, сом", "Ижара, сом", "Rent, som", "Kira, som", "Ijara, so'm");
        SalaryCostLabel.Text = Tr.T("Зарплата, сом", "Эмгек акы, сом", "Salaries, som", "Maaşlar, som", "Ish haqi, so'm");
        UtilitiesLabel.Text = Tr.T("Коммунальные, интернет, сом", "Коммуналдык, интернет, сом", "Utilities, internet, som", "Faturalar, internet, som", "Kommunal, internet, so'm");
        OtherCostsLabel.Text = Tr.T("Прочее (патент, налоги, реклама), сом", "Башка (патент, салыктар, жарнама), сом", "Other (patent, taxes, ads), som", "Diğer (patent, vergi, reklam), som", "Boshqa (patent, soliq, reklama), so'm");
        AvgMarginLabel.Text = Tr.T("Средняя маржа, %", "Орточо маржа, %", "Average margin, %", "Ortalama marj, %", "O'rtacha marja, %");
        BeTaxLabel.Text = Tr.T("Налог с выручки, %", "Кирешеден салык, %", "Tax on revenue, %", "Ciro vergisi, %", "Tushumdan soliq, %");
        AvgCheckLabel.Text = Tr.T("Средний чек, сом", "Орточо чек, сом", "Average receipt, som", "Ortalama fiş, som", "O'rtacha chek, so'm");
        WorkDaysLabel.Text = Tr.T("Рабочих дней в месяце", "Айдагы жумуш күндөрү", "Working days a month", "Aylık iş günü", "Oydagi ish kunlari");
        GoalLabel.Text = Tr.T("Желаемая чистая прибыль в месяц, сом", "Айына каалаган таза пайда, сом", "Target net profit a month, som", "Hedef aylık net kâr, som", "Oyiga kutilgan sof foyda, so'm");
        FillFromSalesButton.Content = Tr.T("Взять маржу и средний чек из продаж за 30 дней", "Маржа менен орточо чекти 30 күндүк сатуудан алуу",
            "Take margin and average receipt from the last 30 days", "Marjı ve ortalama fişi son 30 günden al", "Marja va o'rtacha chekni 30 kunlik sotuvdan olish");
        BeResultTitle.Text = Tr.T("Результат", "Жыйынтык", "Result", "Sonuç", "Natija");
        BeMonthLabel.Text = Tr.T("Выручка без убытка в месяц", "Айына зыянсыз киреше", "Break-even revenue a month", "Aylık başabaş cirosu", "Oyiga zararsiz tushum");
        GoalMonthLabel.Text = Tr.T("Выручка для желаемой прибыли", "Каалаган пайда үчүн киреше", "Revenue for the target profit", "Hedef kâr için ciro", "Kutilgan foyda uchun tushum");
        FactLabel.Text = Tr.T("Этот месяц по факту", "Бул ай иш жүзүндө", "This month so far", "Bu ay şu ana kadar", "Shu oy amalda");
        BeFormulaHint.Text = Tr.T(
            "Без убытка = расходы / (маржа − налог). Пример: расходы 100 000, маржа 25%, налог 0,5% → 408 163 сом выручки в месяц.",
            "Зыянсыз = чыгымдар / (маржа − салык). Мисал: чыгым 100 000, маржа 25%, салык 0,5% → айына 408 163 сом киреше.",
            "Break-even = costs / (margin − tax). Example: costs 100,000, margin 25%, tax 0.5% → 408,163 som revenue a month.",
            "Başabaş = giderler / (marj − vergi). Örnek: gider 100.000, marj %25, vergi %0,5 → ayda 408.163 som ciro.",
            "Zararsizlik = xarajatlar / (marja − soliq). Misol: xarajat 100 000, marja 25%, soliq 0,5% → oyiga 408 163 so'm tushum.");

        // 5. Акция
        PromoTitle.Text = Tr.T("Скидка на товар", "Товарга арзандатуу", "Discount on a product", "Ürün indirimi", "Mahsulotga chegirma");
        PromoMarginLabel.Text = Tr.T("Маржа товара сейчас, %", "Товардын азыркы маржасы, %", "Current product margin, %", "Mevcut ürün marjı, %", "Mahsulotning hozirgi marjasi, %");
        PromoDiscountLabel.Text = Tr.T("Скидка, %", "Арзандатуу, %", "Discount, %", "İndirim, %", "Chegirma, %");
        PromoHint.Text = Tr.T("Маржу товара видно на вкладке «Проверка цен».", "Товардын маржасы «Бааларды текшерүү» өтмөгүндө көрүнөт.",
            "The product margin is shown on the “Price check” tab.", "Ürün marjı “Fiyat kontrolü” sekmesinde görünür.", "Mahsulot marjasi «Narxlarni tekshirish» bo'limida ko'rinadi.");
        PromoResultTitle.Text = Tr.T("Что это значит", "Бул эмнени билдирет", "What it means", "Ne anlama gelir", "Bu nimani anglatadi");
        PromoVolumeLabel.Text = Tr.T("Чтобы заработать столько же, продавайте больше в", "Ошончо табуу үчүн көбүрөөк сатыңыз",
            "To earn the same, sell more by a factor of", "Aynı kârı elde etmek için satışı artırın", "Xuddi shuncha topish uchun ko'proq soting");
        PromoNewMarginLabel.Text = Tr.T("Маржа со скидкой", "Арзандатуу менен маржа", "Margin with the discount", "İndirimli marj", "Chegirma bilan marja");

        BuildAuditFilters();
    }

    private void BuildChips()
    {
        TaxChips.Children.Clear();
        foreach (var rate in TaxPresets)
        {
            var chip = new Button { Content = rate.ToString("0.#", UiNumber) + "%", Tag = rate, Classes = { "chip" } };
            chip.Click += (_, _) =>
            {
                TaxBox.Text = rate.ToString("0.##", CultureInfo.InvariantCulture);
                BeTaxBox.Text = TaxBox.Text;
            };
            TaxChips.Children.Add(chip);
        }

        RoundChips.Children.Clear();
        foreach (var step in RoundSteps)
        {
            var chip = new Button
            {
                Content = step == 0 ? Tr.T("без округления", "тегеректөөсүз", "no rounding", "yuvarlama yok", "yaxlitlamasiz") : $"{step:0} {Som()}",
                Tag = step,
                Classes = { "chip" },
            };
            chip.Classes.Set("active", Math.Abs(step - _roundStep) < 0.001);
            chip.Click += (_, _) =>
            {
                _roundStep = step;
                foreach (var c in RoundChips.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, chip));
                RecalcPrice();
            };
            RoundChips.Children.Add(chip);
        }
    }

    private void BuildAuditFilters()
    {
        AuditFilters.Children.Clear();
        void Add(string key, string text)
        {
            var chip = new Button { Content = text, Tag = key, Classes = { "chip" } };
            chip.Classes.Set("active", key == _auditFilter);
            chip.Click += (_, _) =>
            {
                _auditFilter = key;
                foreach (var c in AuditFilters.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, chip));
                RecalcAudit();
            };
            AuditFilters.Children.Add(chip);
        }

        Add("all", Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"));
        Add("loss", Tr.T("В убыток или в ноль", "Зыянга же нөлгө", "At or below cost", "Zararına veya başabaş", "Zarariga yoki nolga"));
        Add("low", Tr.T("Маржа ниже порога", "Маржа чектен төмөн", "Margin below threshold", "Marj eşiğin altında", "Marja chegaradan past"));
        Add("nocost", Tr.T("Без закупочной цены", "Сатып алуу баасы жок", "No cost price", "Alış fiyatı yok", "Xarid narxi yo'q"));
        Add("negative", Tr.T("Минус на складе", "Кампада минус", "Negative stock", "Eksi stok", "Omborda minus"));
    }

    // ------------------------------------------------------------------ вкладки

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab })
            return;
        _tab = tab;
        foreach (var b in new[] { TabPriceButton, TabBatchButton, TabAuditButton, TabBreakEvenButton, TabPromoButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        PricePage.IsVisible = tab == "price";
        BatchPage.IsVisible = tab == "batch";
        AuditPage.IsVisible = tab == "audit";
        BreakEvenPage.IsVisible = tab == "breakeven";
        PromoPage.IsVisible = tab == "promo";

        if (tab == "audit")
            RecalcAudit();
        if (tab == "breakeven" && !_salesLoaded)
            _ = FillFromSalesAsync();
    }

    private void RecalcAll()
    {
        RecalcPrice();
        RecalcBatch();
        RecalcAudit();
        RecalcBreakEven();
        RecalcPromo();
    }

    // ------------------------------------------------------------------ 1. цена и наценка

    private void ProductSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var q = (ProductSearchBox.Text ?? "").Trim();
        if (q.Length < 2)
        {
            ProductMatches.IsVisible = false;
            return;
        }

        var matches = _products
            .Where(p => p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(p.Barcode) && p.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(8)
            .ToList();
        ProductMatches.ItemsSource = matches.Select(p => new ProductChoice(p)).ToList();
        ProductMatches.IsVisible = matches.Count > 0;
    }

    private void ProductMatches_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProductMatches.SelectedItem is not ProductChoice choice)
            return;

        var p = choice.Product;
        _currentPrice = LocalCartService.ParsePrice(p.PriceLine);
        CostBox.Text = p.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture);
        SelectedProductText.Text = Tr.T($"Выбран: {p.Title}", $"Тандалды: {p.Title}", $"Selected: {p.Title}", $"Seçildi: {p.Title}", $"Tanlandi: {p.Title}");
        SelectedProductText.IsVisible = true;
        ProductMatches.IsVisible = false;
        ProductMatches.SelectedItem = null;
        RecalcPrice();
    }

    private void Mode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || mode == _mode)
            return;
        _mode = mode;
        foreach (var b in new[] { ModeMarkupButton, ModeMarginButton, ModePriceButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        ModeValueBox.Text = mode switch
        {
            "margin" => "25",
            "price" => _currentPrice is > 0 ? _currentPrice.Value.ToString("0.##", CultureInfo.InvariantCulture) : "0",
            _ => "30",
        };
        RecalcPrice();
    }

    private void PriceInputs_Changed(object? sender, TextChangedEventArgs e) => RecalcPrice();

    private void RecalcPrice()
    {
        if (!_ready)
            return;

        var cost = Num(CostBox.Text);
        var value = Num(ModeValueBox.Text);
        var tax = Num(TaxBox.Text) / 100;
        ResWarning.IsVisible = false;

        double price;
        double exact;
        switch (_mode)
        {
            case "margin":
            {
                // Маржа — доля прибыли в цене: цена = закупка / (1 − маржа). Налог, как и в режиме
                // наценки, вычитается из прибыли, а не прибавляется к цене — иначе введённые 25%
                // превращались в показанные 26% (было до 2026-09-26).
                var denominator = 1 - value / 100;
                if (denominator <= 0)
                {
                    ShowPriceWarning(Tr.T("Маржа не может быть 100% и больше.", "Маржа 100% же андан көп боло албайт.",
                        "Margin cannot reach 100%.", "Marj %100'e ulaşamaz.", "Marja 100% yoki undan ko'p bo'lolmaydi."));
                    ClearPriceResults();
                    return;
                }

                exact = cost / denominator;
                break;
            }
            case "price":
                exact = value;
                break;
            default:
                exact = cost * (1 + value / 100);
                break;
        }

        price = _mode == "price" ? exact : RoundUp(exact);
        var rounded = Math.Abs(price - Math.Round(exact, 2)) >= 0.005;
        ResExactPriceText.IsVisible = rounded;
        if (rounded)
            ResExactPriceText.Text = Tr.T($"без округления {Money(exact)}", $"тегеректөөсүз {Money(exact)}", $"before rounding {Money(exact)}",
                $"yuvarlamadan önce {Money(exact)}", $"yaxlitlashsiz {Money(exact)}");

        var taxPerUnit = price * tax;
        var profit = price - cost - taxPerUnit;
        ResPriceValue.Text = Money(price);
        ResProfitValue.Text = Money(profit);
        ResTaxValue.Text = Money(taxPerUnit);
        ResMarkupValue.Text = cost > 0 ? Percent((price - cost) / cost * 100) : "—";
        ResMarginValue.Text = price > 0 ? Percent((price - cost) / price * 100) : "—";

        if (price > 0 && profit <= 0)
            ShowPriceWarning(Tr.T("Цена не покрывает закупку и налог — каждая продажа в убыток.", "Баа сатып алууну жана салыкты жаппайт — ар бир сатуу зыян.",
                "The price does not cover cost and tax — every sale loses money.", "Fiyat maliyeti ve vergiyi karşılamıyor — her satış zararına.", "Narx xarid va soliqni qoplamaydi — har bir sotuv zarar."));

        if (_currentPrice is > 0 && _mode != "price")
        {
            var cp = _currentPrice.Value;
            var currentProfit = cp - cost - cp * tax;
            CurrentPriceText.Text = Tr.T(
                $"Сейчас в каталоге: {Money(cp)} — наценка {PercentOrDash(cost, cp)}, прибыль с единицы {Money(currentProfit)}",
                $"Азыр каталогдо: {Money(cp)} — үстөк {PercentOrDash(cost, cp)}, бирдиктен пайда {Money(currentProfit)}",
                $"Now in the catalog: {Money(cp)} — markup {PercentOrDash(cost, cp)}, profit per unit {Money(currentProfit)}",
                $"Katalogda şu an: {Money(cp)} — kâr oranı {PercentOrDash(cost, cp)}, birim kâr {Money(currentProfit)}",
                $"Hozir katalogda: {Money(cp)} — ustama {PercentOrDash(cost, cp)}, birlikdan foyda {Money(currentProfit)}");
            CurrentPriceBox.IsVisible = true;
        }
        else
        {
            CurrentPriceBox.IsVisible = false;
        }
    }

    private void ShowPriceWarning(string text)
    {
        ResWarning.Text = text;
        ResWarning.IsVisible = true;
    }

    private void ClearPriceResults()
    {
        foreach (var t in new[] { ResPriceValue, ResProfitValue, ResTaxValue, ResMarkupValue, ResMarginValue })
            t.Text = "—";
        ResExactPriceText.IsVisible = false;
    }

    private double RoundUp(double price) =>
        _roundStep > 0 ? Math.Ceiling(Math.Round(price, 6) / _roundStep) * _roundStep : Math.Round(price, 2);

    private static string PercentOrDash(double cost, double price) =>
        cost > 0 ? Percent((price - cost) / cost * 100) : "—";

    // ------------------------------------------------------------------ 2. себестоимость партии

    private void Batch_Changed(object? sender, TextChangedEventArgs e) => RecalcBatch();

    private void BatchGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e) => RecalcBatch();

    private void Split_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string split })
            return;
        _split = split;
        foreach (var b in new[] { SplitSumButton, SplitQtyButton, SplitWeightButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        RecalcBatch();
    }

    private void AddBatchRow_Click(object? sender, RoutedEventArgs e)
    {
        _batchRows.Add(new BatchRow());
        BatchGrid.ScrollIntoView(_batchRows[^1], null);
    }

    private void RemoveBatchRow_Click(object? sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is BatchRow row)
            _batchRows.Remove(row);
        else if (_batchRows.Count > 0)
            _batchRows.RemoveAt(_batchRows.Count - 1);
        RecalcBatch();
    }

    private void RecalcBatch()
    {
        if (!_ready)
            return;

        var extra = Num(ExtraCostsBox.Text);
        var rate = Num(RateBox.Text);
        if (rate <= 0)
            rate = 1;
        var markup = Num(BatchMarkupBox.Text) / 100;

        var rows = _batchRows.Where(r => Num(r.QuantityText) > 0).ToList();
        double Basis(BatchRow r) => _split switch
        {
            "qty" => Num(r.QuantityText),
            "weight" => Num(r.WeightText) * Num(r.QuantityText),
            _ => Num(r.QuantityText) * Num(r.PriceText) * rate,
        };
        var totalBasis = rows.Sum(Basis);
        var totalPurchase = rows.Sum(r => Num(r.QuantityText) * Num(r.PriceText) * rate);

        foreach (var r in _batchRows)
        {
            var qty = Num(r.QuantityText);
            if (qty <= 0)
            {
                r.SetResults("", "", "");
                continue;
            }

            var share = totalBasis > 0 ? extra * Basis(r) / totalBasis : 0;
            var unitCost = (Num(r.PriceText) * rate * qty + share) / qty;
            // Math.Round до 6 знаков — как в RoundUp: 70 × 1,1 в double = 77,00000000000001, и без
            // этого округление вверх давало 78 вместо 77.
            var suggested = _roundStep > 0
                ? Math.Ceiling(Math.Round(unitCost * (1 + markup), 6) / _roundStep) * _roundStep
                : unitCost * (1 + markup);
            r.SetResults(Money(share), Money(unitCost), Money(suggested));
        }

        var noWeight = _split == "weight" && rows.Any(r => Num(r.WeightText) <= 0);
        BatchTotalsText.Text = Tr.T(
            $"Закупка: {Money(totalPurchase)}  ·  расходы: {Money(extra)}  ·  себестоимость партии: {Money(totalPurchase + extra)}",
            $"Сатып алуу: {Money(totalPurchase)}  ·  чыгым: {Money(extra)}  ·  партиянын өздүк наркы: {Money(totalPurchase + extra)}",
            $"Purchase: {Money(totalPurchase)}  ·  extra: {Money(extra)}  ·  batch cost: {Money(totalPurchase + extra)}",
            $"Alış: {Money(totalPurchase)}  ·  masraf: {Money(extra)}  ·  parti maliyeti: {Money(totalPurchase + extra)}",
            $"Xarid: {Money(totalPurchase)}  ·  xarajat: {Money(extra)}  ·  partiya tannarxi: {Money(totalPurchase + extra)}")
            + (noWeight ? Tr.T("  ·  укажите вес у всех строк", "  ·  бардык саптарга салмакты жазыңыз", "  ·  enter weight for every row",
                "  ·  tüm satırlara ağırlık girin", "  ·  barcha qatorlarga og'irlikni kiriting") : "");
    }

    // ------------------------------------------------------------------ 3. проверка цен

    private void Audit_Changed(object? sender, TextChangedEventArgs e) => RecalcAudit();

    private void RecalcAudit()
    {
        if (!_ready)
            return;

        var threshold = Num(ThresholdBox.Text);
        var q = (AuditSearchBox.Text ?? "").Trim();

        // Прибыль и маржа — только по товарам с закупочной ценой: у товара без закупки вся цена
        // попадала в прибыль, будто товар достался даром.
        double stockCost = 0, stockRetail = 0, costedRetail = 0;
        foreach (var r in _auditAll)
        {
            r.Evaluate(threshold);
            if (r.Stock > 0)
            {
                stockCost += r.Cost * r.Stock;
                stockRetail += r.Price * r.Stock;
                if (r.Cost > 0)
                    costedRetail += r.Price * r.Stock;
            }
        }

        StockCostValue.Text = Money(stockCost);
        StockRetailValue.Text = Money(stockRetail);
        StockProfitValue.Text = Money(costedRetail - stockCost);
        StockMarginValue.Text = costedRetail > 0 ? Percent((costedRetail - stockCost) / costedRetail * 100) : "—";

        IEnumerable<AuditRow> rows = _auditAll;
        rows = _auditFilter switch
        {
            "loss" => rows.Where(r => r.Cost > 0 && r.Price <= r.Cost),
            "low" => rows.Where(r => r.Cost > 0 && r.Price > r.Cost && r.Margin < threshold),
            "nocost" => rows.Where(r => r.Cost <= 0),
            "negative" => rows.Where(r => r.Stock < 0),
            _ => rows,
        };
        if (q.Length > 0)
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        AuditGrid.ItemsSource = rows.OrderBy(r => r.SortKey).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ 4. безубыточность

    private void BreakEven_Changed(object? sender, TextChangedEventArgs e) => RecalcBreakEven();

    private void FillFromSales_Click(object? sender, RoutedEventArgs e) => _ = FillFromSalesAsync();

    /// <summary>Маржа и средний чек — за последние 30 дней, факт — с 1-го числа (отчёт сервера, как в сводке).</summary>
    private async Task FillFromSalesAsync()
    {
        _salesLoaded = true;
        FillFromSalesButton.IsEnabled = false;
        FillFromSalesHint.Text = Tr.T("Загрузка продаж…", "Сатуулар жүктөлүүдө…", "Loading sales…", "Satışlar yükleniyor…", "Sotuvlar yuklanmoqda…");
        try
        {
            var today = DateTime.Today;
            var last30 = await App.SalesApi.MarketSalesReportAsync(today.AddDays(-29), today).ConfigureAwait(true);
            var month = await App.SalesApi.MarketSalesReportAsync(new DateTime(today.Year, today.Month, 1), today).ConfigureAwait(true);

            if (last30.ValueKind == JsonValueKind.Object && last30.TryGetProperty("cards", out var c30))
            {
                var margin = Card(c30, "margin_percent");
                var avg = Card(c30, "avg_check");
                if (margin > 0)
                    AvgMarginBox.Text = margin.ToString("0.#", CultureInfo.InvariantCulture);
                if (avg > 0)
                    AvgCheckBox.Text = avg.ToString("0", CultureInfo.InvariantCulture);
            }

            if (month.ValueKind == JsonValueKind.Object && month.TryGetProperty("cards", out var cm))
            {
                _monthRevenue = Card(cm, "revenue");
                _monthGrossProfit = Card(cm, "gross_profit");
            }

            FillFromSalesHint.Text = Tr.T("Маржа и средний чек взяты из продаж за 30 дней, факт — с 1-го числа.",
                "Маржа жана орточо чек 30 күндүк сатуудан, факт — айдын 1-күнүнөн.",
                "Margin and average receipt are from the last 30 days; the actual figure is since the 1st.",
                "Marj ve ortalama fiş son 30 günden, gerçekleşen ayın 1'inden itibaren.",
                "Marja va o'rtacha chek 30 kunlik sotuvdan, amaldagisi — oyning 1-kunidan.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Калькуляция: продажи не загружены: {ex.Message}", "WARNING");
            FillFromSalesHint.Text = Tr.T("Нет связи с сервером — введите маржу и средний чек вручную.",
                "Сервер менен байланыш жок — маржаны жана орточо чекти кол менен жазыңыз.",
                "No connection to the server — enter margin and average receipt manually.",
                "Sunucuyla bağlantı yok — marjı ve ortalama fişi elle girin.",
                "Server bilan aloqa yo'q — marja va o'rtacha chekni qo'lda kiriting.");
        }
        finally
        {
            FillFromSalesButton.IsEnabled = true;
            RecalcBreakEven();
        }
    }

    private void RecalcBreakEven()
    {
        if (!_ready)
            return;

        var fixedCosts = Num(RentBox.Text) + Num(SalaryBox.Text) + Num(UtilitiesBox.Text) + Num(OtherBox.Text);
        var margin = Num(AvgMarginBox.Text) / 100;
        var tax = Num(BeTaxBox.Text) / 100;
        var avgCheck = Num(AvgCheckBox.Text);
        var days = Num(WorkDaysBox.Text);
        if (days <= 0)
            days = 30;
        var goal = Num(GoalBox.Text);
        var effective = margin - tax;

        BeWarning.IsVisible = false;
        FactBar.Children.Clear();
        FactBar.ColumnDefinitions.Clear();

        if (effective <= 0)
        {
            BeWarning.Text = Tr.T("Маржа не больше налога — прибыль невозможна при любой выручке.", "Маржа салыктан көп эмес — ар кандай кирешеде пайда болбойт.",
                "Margin does not exceed the tax — no revenue can make a profit.", "Marj vergiden büyük değil — hiçbir ciroda kâr olmaz.", "Marja soliqdan katta emas — har qanday tushumda foyda bo'lmaydi.");
            BeWarning.IsVisible = true;
            BeMonthValue.Text = GoalMonthValue.Text = FactValue.Text = "—";
            BeDayText.Text = GoalDayText.Text = FactText.Text = "";
            return;
        }

        var breakEven = fixedCosts / effective;
        var goalRevenue = (fixedCosts + goal) / effective;
        BeMonthValue.Text = Money(breakEven);
        BeDayText.Text = PerDayText(breakEven / days, avgCheck);
        GoalMonthValue.Text = Money(goalRevenue);
        GoalDayText.Text = PerDayText(goalRevenue / days, avgCheck);

        if (_monthRevenue is { } revenue)
        {
            var today = DateTime.Today;
            var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
            var forecast = revenue / today.Day * daysInMonth;
            var forecastProfit = forecast * effective - fixedCosts;
            FactValue.Text = Money(revenue);
            var share = breakEven > 0 ? Math.Min(1, revenue / breakEven) : 1;
            FactBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(share, 0.001), GridUnitType.Star));
            FactBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(1 - share, 0.001), GridUnitType.Star));
            var fill = new Border { CornerRadius = new CornerRadius(5) };
            fill.Bind(Border.BackgroundProperty, this.GetResourceObservable(revenue >= breakEven ? "BrushSuccess" : "BrushAccent"));
            FactBar.Children.Add(fill);
            FactText.Text = Tr.T(
                $"{Percent(breakEven > 0 ? revenue / breakEven * 100 : 100)} от безубыточности. Прогноз на месяц: {Money(forecast)}, чистая прибыль ≈ {Money(forecastProfit)}",
                $"Зыянсыздыктын {Percent(breakEven > 0 ? revenue / breakEven * 100 : 100)}. Айга болжол: {Money(forecast)}, таза пайда ≈ {Money(forecastProfit)}",
                $"{Percent(breakEven > 0 ? revenue / breakEven * 100 : 100)} of break-even. Month forecast: {Money(forecast)}, net profit ≈ {Money(forecastProfit)}",
                $"Başabaşın {Percent(breakEven > 0 ? revenue / breakEven * 100 : 100)}. Ay tahmini: {Money(forecast)}, net kâr ≈ {Money(forecastProfit)}",
                $"Zararsizlikning {Percent(breakEven > 0 ? revenue / breakEven * 100 : 100)}. Oy prognozi: {Money(forecast)}, sof foyda ≈ {Money(forecastProfit)}");
        }
        else
        {
            FactValue.Text = "—";
            FactText.Text = "";
        }
    }

    private static string PerDayText(double perDay, double avgCheck)
    {
        var checks = avgCheck > 0 ? Math.Ceiling(perDay / avgCheck) : 0;
        return avgCheck > 0
            ? Tr.T($"≈ {Money(perDay)} в день · {checks:0} чеков в день", $"≈ күнүнө {Money(perDay)} · күнүнө {checks:0} чек",
                $"≈ {Money(perDay)} a day · {checks:0} receipts a day", $"≈ günde {Money(perDay)} · günde {checks:0} fiş", $"≈ kuniga {Money(perDay)} · kuniga {checks:0} chek")
            : Tr.T($"≈ {Money(perDay)} в день", $"≈ күнүнө {Money(perDay)}", $"≈ {Money(perDay)} a day", $"≈ günde {Money(perDay)}", $"≈ kuniga {Money(perDay)}");
    }

    // ------------------------------------------------------------------ 5. скидка и акция

    private void Promo_Changed(object? sender, TextChangedEventArgs e) => RecalcPromo();

    private void RecalcPromo()
    {
        if (!_ready)
            return;

        var m = Num(PromoMarginBox.Text);
        var d = Num(PromoDiscountBox.Text);
        if (m <= 0 || m >= 100 || d < 0 || d >= 100)
        {
            PromoVolumeValue.Text = PromoNewMarginValue.Text = "—";
            PromoExplain.Text = "";
            return;
        }

        if (d >= m)
        {
            PromoVolumeValue.Text = "∞";
            PromoNewMarginValue.Text = Percent((m - d) / (100 - d) * 100);
            PromoExplain.Text = Tr.T("Скидка съедает всю наценку — каждая продажа по акции в убыток.", "Арзандатуу бардык үстөктү жеп коёт — акциядагы ар бир сатуу зыян.",
                "The discount eats the whole margin — every promo sale loses money.", "İndirim tüm marjı yiyor — her kampanya satışı zararına.", "Chegirma butun marjani yeb qo'yadi — aksiyadagi har bir sotuv zarar.");
            return;
        }

        var factor = m / (m - d);
        PromoVolumeValue.Text = factor.ToString("0.##", UiNumber) + "×";
        PromoNewMarginValue.Text = Percent((m - d) / (100 - d) * 100);
        PromoExplain.Text = Tr.T(
            $"Если раньше продавали 100 штук, со скидкой {d:0.#}% нужно продать {Math.Ceiling(100 * factor):0}, чтобы прибыль не упала (+{(factor - 1) * 100:0}%).",
            $"Мурда 100 даана сатылса, {d:0.#}% арзандатуу менен пайда азайбашы үчүн {Math.Ceiling(100 * factor):0} сатуу керек (+{(factor - 1) * 100:0}%).",
            $"If you used to sell 100 units, with a {d:0.#}% discount you need to sell {Math.Ceiling(100 * factor):0} to keep the same profit (+{(factor - 1) * 100:0}%).",
            $"Önceden 100 adet satıyorsanız, %{d:0.#} indirimle kârın düşmemesi için {Math.Ceiling(100 * factor):0} adet satmalısınız (+%{(factor - 1) * 100:0}).",
            $"Avval 100 dona sotilgan bo'lsa, {d:0.#}% chegirma bilan foyda kamaymasligi uchun {Math.Ceiling(100 * factor):0} dona sotish kerak (+{(factor - 1) * 100:0}%).");
    }

    // ------------------------------------------------------------------ окно

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ числа

    private static readonly CultureInfo UiNumber = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Число из поля: запятая и точка, пробелы между разрядами.</summary>
    internal static double Num(string? text)
    {
        var s = (text ?? "").Trim().Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
    }

    private static double Card(JsonElement cards, string name)
    {
        if (!cards.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static string Som() => Tr.T("сом", "сом", "som", "som", "so'm");

    internal static string Money(double value) =>
        (Math.Abs(value - Math.Round(value)) < 0.005 ? value.ToString("N0", UiNumber) : value.ToString("N2", UiNumber)) + " " + Som();

    internal static string Percent(double value) => value.ToString("0.#", UiNumber) + "%";

    // ------------------------------------------------------------------ строки таблиц

    private sealed record ProductChoice(CatalogProductTileVm Product)
    {
        public override string ToString() =>
            $"{Product.Title}  ·  {Tr.T("закупка", "сатып алуу", "cost", "alış", "xarid")} {Money(Product.PurchasePrice)}  ·  {Tr.T("цена", "баа", "price", "fiyat", "narx")} {Money(LocalCartService.ParsePrice(Product.PriceLine))}";
    }

    public sealed class BatchRow : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string QuantityText { get; set; } = "";
        public string PriceText { get; set; } = "";
        public string WeightText { get; set; } = "";
        public string ShareText { get; private set; } = "";
        public string UnitCostText { get; private set; } = "";
        public string SuggestedPriceText { get; private set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetResults(string share, string unitCost, string suggested)
        {
            ShareText = share;
            UnitCostText = unitCost;
            SuggestedPriceText = suggested;
            Raise(nameof(ShareText));
            Raise(nameof(UnitCostText));
            Raise(nameof(SuggestedPriceText));
        }

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class AuditRow
    {
        public AuditRow(string name, double cost, double price, double stock, string unit)
        {
            Name = name;
            Cost = cost;
            Price = price;
            Stock = stock;
            Unit = unit;
            Markup = cost > 0 ? (price - cost) / cost * 100 : double.NaN;
            Margin = price > 0 && cost > 0 ? (price - cost) / price * 100 : double.NaN;
            StockProfit = cost > 0 && stock > 0 ? (price - cost) * stock : 0;
        }

        public string Name { get; }
        public double Cost { get; }
        public double Price { get; }
        public double Stock { get; }
        public string Unit { get; }
        public double Markup { get; }
        public double Margin { get; }
        public double StockProfit { get; }
        public string Flag { get; private set; } = "";
        public int SortKey { get; private set; }

        public string CostText => Cost > 0 ? Money(Cost) : "—";
        public string PriceText => Money(Price);
        public string MarkupText => double.IsNaN(Markup) ? "—" : Percent(Markup);
        public string MarginText => double.IsNaN(Margin) ? "—" : Percent(Margin);
        public string StockText => Stock.ToString(Math.Abs(Stock % 1) < 1e-6 ? "N0" : "N3", UiNumber) + (Unit.Length > 0 ? " " + Unit : "");
        public string StockProfitText => StockProfit != 0 ? Money(StockProfit) : "—";

        internal void Evaluate(double threshold)
        {
            if (Cost > 0 && Price < Cost)
            {
                Flag = Tr.T("⚠ в убыток", "⚠ зыянга", "⚠ below cost", "⚠ zararına", "⚠ zarariga");
                SortKey = 0;
            }
            else if (Cost > 0 && Math.Abs(Price - Cost) < 0.005)
            {
                Flag = Tr.T("⚠ без наценки", "⚠ үстөксүз", "⚠ no markup", "⚠ kârsız", "⚠ ustamasiz");
                SortKey = 0;
            }
            else if (Stock < 0)
            {
                Flag = Tr.T("⚠ минус на складе", "⚠ кампада минус", "⚠ negative stock", "⚠ eksi stok", "⚠ omborda minus");
                SortKey = 1;
            }
            else if (Cost <= 0)
            {
                Flag = Tr.T("нет закупки", "сатып алуу жок", "no cost", "alış yok", "xarid yo'q");
                SortKey = 2;
            }
            else if (Margin < threshold)
            {
                Flag = Tr.T("низкая маржа", "төмөн маржа", "low margin", "düşük marj", "past marja");
                SortKey = 3;
            }
            else
            {
                Flag = "";
                SortKey = 4;
            }
        }
    }
}
