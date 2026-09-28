using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>«Весы» → выгрузка весовых товаров на сетевые весы. Список берётся из уже
/// загруженного каталога (CatalogCacheService). Две ветки:
/// - Штрих-М — готовый серверный эндпоинт (сервер сам говорит с весами по LAN);
/// - Rongta — своего сетевого протокола нет (см. RongtaScaleAutomationService, почему):
///   скачиваем .txp с того же сервера, что и вкладка «Rongta» на сайте, и передаём его в
///   официальную программу RLS1000 (её запуск+«нажатие F9» автоматизированы, саму RLS1000
///   не переписываем).</summary>
public partial class ScalesPluWindow : Window
{
    private const string BrandShtrikh = "shtrikh";
    private const string BrandRongta = "rongta";

    /// <summary>Весы с распознаванием товара («AI весы»). Прямая заливка по сети пока не
    /// сделана: у этих весов нет единого протокола, как у ШТРИХ-ПРИНТ, — каждая модель
    /// идёт со своей программой, и загружать список нужно через неё. Поэтому здесь касса
    /// готовит файл, который эта программа импортирует.</summary>
    private const string BrandAi = "ai";

    /// <summary>Весы TM-30F, 2026-09-28. Сначала считались JHScale с закрытым протоколом (касса
    /// готовила файл, как для AI-весов). Вечером 28.09 выяснилось: это Dahua (TM-A / TM-F),
    /// программа «Русский масштаб», протокол восстановлен по её файлам — теперь «Отправить на весы»
    /// шлёт PLU напрямую (DahuaTmScaleService, TCP 4001), а файл для их программы — запасной путь.</summary>
    private const string BrandTm = "tm";

    /// <summary>Весы, для которых касса только готовит файл (AI). TM-30F с 28.09 (вечер) — нет:
    /// у них прямая отправка.</summary>
    private bool IsFileBrand => BrandAiRadio.IsChecked == true;

    /// <summary>2026-09-28: выбрана марка TM-30F (Dahua).</summary>
    private bool IsTm => BrandTmRadio.IsChecked == true;

    /// <summary>Идёт отправка на TM-30F — кнопка отправки в это время «Остановить».</summary>
    private CancellationTokenSource? _tmCts;
    /// <summary>Исходная надпись кнопки отправки («Отправить на весы» на языке интерфейса).
    /// Запоминаем при загрузке окна: для AI-весов кнопка называется иначе, и при возврате к
    /// Штриху нужно вернуть ровно ту надпись, что пришла из словаря, а не зашитую строку.</summary>
    private object? _sendButtonDefaultText;

    private const string RongtaSourceSite = "site";
    private const string RongtaSourceServer = "server";

    public ScalesPluWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        var brand = UserPreferences.Instance.ScaleBrand;
        BrandRongtaRadio.IsChecked = brand == BrandRongta;
        BrandAiRadio.IsChecked = brand == BrandAi;
        BrandTmRadio.IsChecked = brand == BrandTm;
        BrandShtrikhRadio.IsChecked = brand != BrandRongta && brand != BrandAi && brand != BrandTm;

        var source = UserPreferences.Instance.RongtaDataSource;
        RongtaSourceServerRadio.IsChecked = source == RongtaSourceServer;
        RongtaSourceSiteRadio.IsChecked = source != RongtaSourceServer;
        RongtaPortBox.Text = UserPreferences.Instance.RongtaServerPort.ToString(CultureInfo.InvariantCulture);

        // Сами адрес/порт/пароль читает и пишет окно настроек подключения
        // (ScaleConnectionDialog) — здесь остаётся только галочка.
        DirectLanCheck.IsChecked = UserPreferences.Instance.ShtrikhDirectLan;

        _sendButtonDefaultText = SendButton.Content;
        SearchBox.Watermark = Tr.T("Поиск: название, PLU или код", "Издөө: аталышы, PLU же код",
            "Search: name, PLU or code", "Ara: ad, PLU veya kod", "Qidirish: nomi, PLU yoki kod");

        ApplyBrandVisibility();
        ApplyRongtaSourceVisibility();
        ApplyDirectLanVisibility();
        LoadRows();
    }

    /// <summary>2026-09-28: марку могли поменять окна настроек весов («Загрузка товаров» →
    /// «Открыть окно «Весы»») — перечитываем её из настроек.</summary>
    public void ReloadBrandFromPreferences()
    {
        var brand = UserPreferences.Instance.ScaleBrand;
        BrandRongtaRadio.IsChecked = brand == BrandRongta;
        BrandAiRadio.IsChecked = brand == BrandAi;
        BrandTmRadio.IsChecked = brand == BrandTm;
        BrandShtrikhRadio.IsChecked = brand != BrandRongta && brand != BrandAi && brand != BrandTm;
        ApplyBrandVisibility();
    }

    private string SelectedBrand =>
        BrandRongtaRadio.IsChecked == true ? BrandRongta
        : BrandAiRadio.IsChecked == true ? BrandAi
        : BrandTmRadio.IsChecked == true ? BrandTm
        : BrandShtrikh;

    /// <summary>2026-09-28: «Поиск весов в сети» — просьба владельца узнать IP подключённых весов.</summary>
    private async void ScanNetwork_Click(object? sender, RoutedEventArgs e) =>
        await NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.OpenScanAsync(this, SelectedBrand).ConfigureAwait(true);

    /// <summary>2026-09-28: окно настроек весов выбранной марки (Штрих-М — прежнее окно
    /// Штрих-ПРИНТ, Rongta и TM-30F — новые).</summary>
    private async void BrandSettings_Click(object? sender, RoutedEventArgs e)
    {
        await NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.OpenBrandSettingsAsync(this, SelectedBrand).ConfigureAwait(true);
        ReloadBrandFromPreferences();
    }

    private void BrandRadio_Click(object? sender, RoutedEventArgs e)
    {
        UserPreferences.Instance.ScaleBrand =
            BrandRongtaRadio.IsChecked == true ? BrandRongta
            : BrandAiRadio.IsChecked == true ? BrandAi
            : BrandTmRadio.IsChecked == true ? BrandTm
            : BrandShtrikh;
        UserPreferences.Instance.SaveToDisk();
        ApplyBrandVisibility();
    }

    private void RongtaSourceRadio_Click(object? sender, RoutedEventArgs e)
    {
        UserPreferences.Instance.RongtaDataSource = RongtaSourceServerRadio.IsChecked == true ? RongtaSourceServer : RongtaSourceSite;
        if (int.TryParse((RongtaPortBox.Text ?? "").Trim(), out var port) && port is > 0 and <= 65535)
            UserPreferences.Instance.RongtaServerPort = port;
        UserPreferences.Instance.SaveToDisk();
        ApplyRongtaSourceVisibility();
    }

    private void ApplyRongtaSourceVisibility()
    {
        var useOwnServer = RongtaSourceServerRadio.IsChecked == true;
        RongtaPortLabel.IsVisible = useOwnServer;
        RongtaPortBox.IsVisible = useOwnServer;
    }

    /// <summary>Поля прямого подключения нужны только для Штрих-М и только когда владелец
    /// сам выбрал работу без сервера.</summary>
    /// <summary>Кнопка настроек подключения нужна только для Штрих-М и только когда владелец
    /// выбрал работу без сервера. Сами поля живут в отдельном окне (ScaleConnectionDialog):
    /// в строке они не помещались и уезжали за край экрана.</summary>
    private void ApplyDirectLanVisibility()
    {
        LanSettingsButton.IsVisible = BrandRongtaRadio.IsChecked != true
                                      && !IsFileBrand
                                      && !IsTm
                                      && DirectLanCheck.IsChecked == true;
        // 2026-09-28: у TM-30F (Dahua) своя прямая отправка — галочка Штрих-М там не нужна.
        DirectLanCheck.IsVisible = !IsTm;
        // 2026-09-28: код в ШК правится только при прямой выгрузке — серверный путь
        // (send-products) записывает на весы свои данные, и эта колонка на них не влияет.
        // Колонки DataGrid не попадают в поля по x:Name — ищем по Tag.
        var barcodeColumn = ProductsGrid.Columns.FirstOrDefault(c => Equals(c.Tag, "BarcodeCode"));
        // TM-30F (Dahua) тоже шлёт «Код товара» в ШК сам — колонка и кнопка нужны и ему.
        if (barcodeColumn is not null)
            barcodeColumn.IsVisible = LanSettingsButton.IsVisible || IsTm;
        BarcodeSettingsButton.IsVisible = LanSettingsButton.IsVisible || IsTm;
        UpdateBarcodeExample();
    }

    /// <summary>2026-09-28, просьба владельца «2000001003923 — добавь возможность редактировать
    /// штрих-код при отправке на весы». Показывает, какой весовой ШК ждёт касса по настройке
    /// компании (раскладка «по PLU» 2+5+5+1 или «по коду» 2+6+4+1), на примере первого
    /// отмеченного товара и массы 0,392 кг. Сам формат на весах меняется в окне «Настройки весов
    /// Штрих-ПРИНТ» → «Штрих-код» (кнопка «Штрих-код этикетки…»), где пример проверяется тем же
    /// разбором, что и скан этикетки.</summary>
    private void UpdateBarcodeExample()
    {
        if (BarcodeExampleText is null)
            return;
        var visible = BrandRongtaRadio.IsChecked != true && !IsFileBrand;
        BarcodeExampleText.IsVisible = visible;
        if (!visible || _allRows is not IEnumerable<ScalePluRowVm> rows)
            return;

        var row = rows.FirstOrDefault(r => r.IsSelected) ?? rows.FirstOrDefault();
        if (IsTm)
        {
            UpdateTmBarcodeExample(row);
            return;
        }
        var layout = NurMarketKassa.Core.Application.WeightBarcodeParser.Layout;
        var byWeight = !string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        var structure = ShtrikhBarcodeFormat.RecommendedStructure(layout, byWeight);
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        const int grams = 392;
        var cost = ShtrikhPrintProtocol.PriceToMde((decimal)(row?.Price ?? 100) * grams / 1000m, 2);
        var sample = ShtrikhBarcodeFormat.BuildSample(structure, byWeight ? 20 : 25, code, grams, cost);
        var isCode = string.Equals(layout, "code", StringComparison.OrdinalIgnoreCase);
        BarcodeExampleText.Text = Tr.T(
            $"Штрих-код на этикетке весов для «{row?.Name}» (0,392 кг): {sample}. Так его ждёт касса — раскладка компании «{(isCode ? "по коду" : "по PLU")}»; на весах Штрих-ПРИНТ нужна структура {structure} ({ShtrikhBarcodeFormat.Structures[structure]}) и префикс {(byWeight ? 20 : 25)}.",
            $"«{row?.Name}» үчүн тараза этикеткасындагы штрих-код (0,392 кг): {sample}. Касса аны ушундай күтөт — компаниянын раскладкасы «{(isCode ? "код боюнча" : "PLU боюнча")}»; ШТРИХ-ПРИНТ таразасында {structure} түзүлүш ({ShtrikhBarcodeFormat.Structures[structure]}) жана {(byWeight ? 20 : 25)} префикс керек.",
            $"Scale label barcode for “{row?.Name}” (0.392 kg): {sample}. This is what the till expects — company layout “{(isCode ? "by code" : "by PLU")}”; the ShTRIH-PRINT scale needs structure {structure} ({ShtrikhBarcodeFormat.Structures[structure]}) and prefix {(byWeight ? 20 : 25)}.",
            $"“{row?.Name}” için tartı etiketi barkodu (0,392 kg): {sample}. Kasa bunu böyle bekler — şirket düzeni “{(isCode ? "koda göre" : "PLU’ya göre")}”; ŞTRİH-PRİNT tartıda yapı {structure} ({ShtrikhBarcodeFormat.Structures[structure]}) ve önek {(byWeight ? 20 : 25)} gerekir.",
            $"«{row?.Name}» uchun tarozi yorlig‘idagi shtrix-kod (0,392 kg): {sample}. Kassa uni shunday kutadi — kompaniya tartibi «{(isCode ? "kod bo‘yicha" : "PLU bo‘yicha")}»; ShTRIX-PRINT tarozisida {structure} tuzilma ({ShtrikhBarcodeFormat.Structures[structure]}) va {(byWeight ? 20 : 25)} prefiks kerak.");
    }

    private void BarcodeCode_LostFocus(object? sender, RoutedEventArgs e) => UpdateBarcodeExample();

    /// <summary>Открывает «Настройки весов Штрих-ПРИНТ» на вкладке «Штрих-код» с примером для
    /// первого отмеченного товара — там формат ШК весов читается, правится и записывается.</summary>
    private async void BarcodeSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (IsTm)
        {
            // 2026-09-28: для TM-30F — окно «Настройки весов TM-30F» на вкладке «Штрих-код».
            var tmWindow = new NurMarketKassa.AvaloniaHost.Views.Dialogs.TmScaleSettingsWindow();
            tmWindow.ShowBarcodeTab();
            await tmWindow.ShowDialog(this).ConfigureAwait(true);
            UpdateBarcodeExample();
            return;
        }

        var rows = _allRows;
        var row = rows?.FirstOrDefault(r => r.IsSelected) ?? rows?.FirstOrDefault();
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        var window = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ShtrikhScaleSettingsWindow();
        window.ShowBarcodeTabFor(row?.Name ?? "", code, (decimal)(row?.Price ?? 0));
        await window.ShowDialog(this).ConfigureAwait(true);
        UpdateBarcodeExample();
    }


    private async void DirectLan_Changed(object? sender, RoutedEventArgs e)
    {
        SaveLanSettings();
        ApplyDirectLanVisibility();

        // Включили работу без сервера — сразу спрашиваем адрес и пароль: без них выгрузка
        // всё равно не пойдёт, а искать, где их ввести, владельцу не придётся.
        if (IsLoaded && DirectLanCheck.IsChecked == true)
            await OpenLanSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Поля подключения теперь в отдельном окне и сохраняются там же; здесь
    /// остаётся только сама галочка «Напрямую по кабелю».</summary>
    private void SaveLanSettings()
    {
        var prefs = UserPreferences.Instance;
        prefs.ShtrikhDirectLan = DirectLanCheck.IsChecked == true;
        prefs.SaveToDisk();
    }

    /// <summary>Открывает окно настроек подключения. Вызывается и кнопкой, и автоматически при
    /// включении галочки: владелец только что попросил работать без сервера — значит адрес и
    /// пароль нужны прямо сейчас, а не «где-то в настройках».</summary>
    private async Task OpenLanSettingsAsync()
    {
        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleConnectionDialog();
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private async void LanSettings_Click(object? sender, RoutedEventArgs e) =>
        await OpenLanSettingsAsync().ConfigureAwait(true);


    /// <summary>Создаёт драйвер по текущим полям. null и сообщение в статусе, если поля пустые
    /// или неверные.</summary>
    private ShtrikhPrintLanScaleService? CreateLanService()
    {
        var prefs = UserPreferences.Instance;
        try
        {
            return new ShtrikhPrintLanScaleService(
                prefs.ScaleNetworkIp ?? "", prefs.ScaleLanPort, prefs.ScaleLanPassword);
        }
        catch (System.Exception ex)
        {
            StatusText.Text = ex.Message;
            return null;
        }
    }


    private void ApplyBrandVisibility()
    {
        var isRongta = BrandRongtaRadio.IsChecked == true;
        var isAi = IsFileBrand;
        PluStartRow.IsVisible = !isRongta && !isAi;
        ApplyDirectLanVisibility();
        RongtaSourceRow.IsVisible = isRongta;
        SendButton.Content = isAi ? Tr.T("Сохранить файл для весов", "Файлды тараза үчүн сактоо", "Save file for the scale", "Tartı için dosyayı kaydet", "Tarozi uchun faylni saqlash") : _sendButtonDefaultText;
        TmExportButton.IsVisible = IsTm;

        if (IsTm)
        {
            // 2026-09-28 (вечер): прямая отправка на Dahua TM-30F.
            SubtitleText.Text = Tr.T(
                "TM-30F (Dahua, программа «Русский масштаб»): «Отправить на весы» шлёт отмеченные товары прямо на весы по сети (IP и порт 4001 — «Настройки весов»). Номер PLU — по порядку от «Начальный PLU»; в штрих-код этикетки весы печатают «Код в штрих-коде» (по умолчанию PLU товара — по нему касса найдёт товар). Запасной путь — «Файл для «Русского масштаба»».",
                "TM-30F (Dahua, «Русский масштаб» программасы): «Таразага жөнөтүү» белгиленген товарларды тармак аркылуу түз таразага жөнөтөт (IP жана 4001 порт — «Тараза жөндөөлөрү»). PLU номери — «Баштапкы PLU»дан тартип менен; этикетканын штрих-кодуна тараза «Штрих-коддогу код» басат (демейки — товардын PLU'су, касса товарды ушул боюнча табат). Запас жол — «Русский масштаб» үчүн файл».",
                "TM-30F (Dahua, “Russian Scale” software): “Send to scale” sends the ticked goods straight to the scale over the network (IP and port 4001 — “Scale settings”). The PLU number goes in order from “Start PLU”; the scale prints the “Code in barcode” into the label barcode (the item PLU by default — the till finds the item by it). Fallback — “File for Russian Scale”.",
                "TM-30F (Dahua, «Русский масштаб» programı): «Tartıya gönder» işaretli ürünleri ağ üzerinden doğrudan tartıya gönderir (IP ve port 4001 — «Tartı ayarları»). PLU numarası «Başlangıç PLU»dan sırayla; tartı etiket barkoduna «Barkoddaki kod»u basar (varsayılan ürünün PLU'su — kasa ürünü buna göre bulur). Yedek yol — «Русский масштаб için dosya».",
                "TM-30F (Dahua, «Русский масштаб» dasturi): «Taroziga yuborish» belgilangan tovarlarni tarmoq orqali to‘g‘ridan-to‘g‘ri taroziga yuboradi (IP va 4001 port — «Tarozi sozlamalari»). PLU raqami — «Boshlang‘ich PLU»dan tartib bilan; tarozi yorliq shtrix-kodiga «Shtrix-koddagi kod»ni chop etadi (odatiy — tovar PLU'si, kassa tovarni shu bo‘yicha topadi). Zaxira yo‘l — «Русский масштаб uchun fayl».");
            return;
        }

        if (isAi)
        {
            SubtitleText.Text = Tr.T(
                "AI весы: касса готовит файл со списком (PLU, название, единица, цена), а вы загружаете его программой весов. Прямой заливки по сети пока нет — у этих весов нет общего протокола, каждая модель идёт со своей программой.",
                "AI тараза: касса тизмеси бар файл даярдайт (PLU, аталышы, бирдиги, баасы), аны сиз тараза программасы менен жүктөйсүз. Тармак аркылуу түз жүктөө азырынча жок — бул таразалардын жалпы протоколу жок, ар бир модель өз программасы менен келет.",
                "AI scales: the till prepares a file (PLU, name, unit, price) and you load it with the scale's software. There is no direct network upload yet — these scales have no common protocol; each model comes with its own software.",
                "AI tartı: kasa listeyi (PLU, ad, birim, fiyat) dosya olarak hazırlar, siz de onu tartının kendi programıyla yüklersiniz. Ağ üzerinden doğrudan yükleme henüz yok — bu tartıların ortak protokolü yok, her model kendi programıyla gelir.",
                "AI tarozi: kassa ro'yxat faylini tayyorlaydi (PLU, nomi, birligi, narxi), siz esa uni tarozi dasturi orqali yuklaysiz. Tarmoq orqali to'g'ridan-to'g'ri yuklash hozircha yo'q — bu tarozilarning umumiy protokoli yo'q, har bir model o'z dasturi bilan keladi.");
            return;
        }

        if (isRongta)
        {
            SubtitleText.Text = Tr.T(
                "Rongta: на весы уйдёт весь список весовых товаров (выбор галочками здесь не действует) — через встроенный запуск RLS1000.",
                "Rongta: таразага бардык салмактуу товарлардын тизмеси жиберилет (бул жердеги белгилер эске алынбайт) — RLS1000 аркылуу, ал өзү иштетилет.",
                "Rongta: the whole list of weighed products will be sent to the scale (the checkboxes here are ignored) — RLS1000 is launched automatically.",
                "Rongta: tartıya tartılı ürünlerin tamamı gönderilir (buradaki işaretlemeler dikkate alınmaz) — RLS1000 yerleşik olarak başlatılır.",
                "Rongta: taroziga barcha vaznli mahsulotlar ro'yxati yuboriladi (bu yerdagi belgilashlar hisobga olinmaydi) — o'rnatilgan RLS1000 orqali.");
        }
        else if (Application.Current?.TryFindResource("scalesPlu.subtitle", ActualThemeVariant, out var value) == true
                 && value is string defaultSubtitle)
        {
            SubtitleText.Text = defaultSubtitle;
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => LoadRows();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void LoadRows()
    {
        var rows = NurMarketKassa.Services.CatalogCacheService.Products
            .Where(p => p.IsWeighted)
            .OrderBy(p => p.Title, System.StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ScalePluRowVm
            {
                Id = p.Id,
                Name = p.Title,
                PluText = p.Plu?.ToString(CultureInfo.InvariantCulture) ?? "—",
                PriceLine = p.PriceLine,
                Unit = p.Unit ?? "",
                Price = NurMarketKassa.Services.LocalCartService.ParsePrice(p.PriceLine),
                BarcodeCode = DefaultBarcodeCode(p),
                IsSelected = true,
            })
            .ToList();

        _allRows = rows;
        ApplySearch();
        StatusText.Text = "";
        UpdateBarcodeExample();
    }

    /// <summary>Все весовые товары. Таблица показывает только найденные поиском, а отправка,
    /// экспорт и «Код в ШК» работают по всему списку: отмеченный, но скрытый поиском товар
    /// тоже уйдёт на весы (2026-09-28, просьба владельца — поиск в окне «Весы»).</summary>
    private List<ScalePluRowVm> _allRows = new();

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    /// <summary>Поиск по названию, PLU и коду в ШК; несколько слов — все должны найтись.</summary>
    private void ApplySearch()
    {
        var words = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = words.Length == 0
            ? _allRows
            : _allRows.Where(r => words.All(w =>
                    (r.Name ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                    || string.Equals(r.PluText, w, StringComparison.Ordinal)
                    || (r.BarcodeCode ?? "").Contains(w, StringComparison.Ordinal)))
                .ToList();

        ProductsGrid.ItemsSource = shown;
        EmptyText.IsVisible = shown.Count == 0;
        SearchCountText.Text = words.Length == 0
            ? ""
            : Tr.T($"Найдено: {shown.Count} из {_allRows.Count}", $"Табылды: {_allRows.Count} ичинен {shown.Count}",
                   $"Found: {shown.Count} of {_allRows.Count}", $"Bulunan: {shown.Count} / {_allRows.Count}",
                   $"Topildi: {_allRows.Count} dan {shown.Count}");
    }

    /// <summary>2026-09-28: какое число весы напечатают в ШК этикетки по умолчанию — то, по
    /// которому касса потом найдёт товар (LocalCartService.FindByEmbeddedCode): при раскладке
    /// компании «по PLU» — PLU товара, при «по коду» — «Код товара»/артикул, если это число.
    /// Пусто — у товара нет ни того, ни другого; тогда при выгрузке берётся номер ячейки ПЛУ.</summary>
    private static string DefaultBarcodeCode(NurMarketKassa.Models.Pos.CatalogProductTileVm p)
    {
        if (string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in new[] { p.ProductCode, p.Article })
            {
                var digits = (candidate ?? "").Trim();
                if (digits.Length > 0 && digits.All(char.IsDigit) && long.TryParse(digits, out var n) && n is > 0 and <= 999999)
                    return n.ToString(CultureInfo.InvariantCulture);
            }
        }
        return p.Plu is > 0 ? p.Plu.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => SetAllSelected(true);

    private void SelectNone_Click(object? sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        // Только видимые строки: при поиске «Выбрать все» отмечает найденное (2026-09-28).
        if (ProductsGrid.ItemsSource is not IEnumerable<ScalePluRowVm> rows)
            return;
        foreach (var row in rows)
            row.IsSelected = selected;
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-09-28: во время отправки на TM-30F кнопка — «Остановить».
        if (_tmCts is not null)
        {
            _tmCts.Cancel();
            return;
        }

        if (IsTm)
        {
            await SendToTmAsync().ConfigureAwait(true);
            return;
        }

        if (IsFileBrand)
        {
            // Для AI-весов «отправить» — это подготовить файл: заливать напрямую пока нечем.
            await ExportPluCsvAsync(BrandTmRadio.IsChecked == true ? "tm30f-scale-plu" : "ai-scale-plu").ConfigureAwait(true);
            return;
        }

        if (BrandRongtaRadio.IsChecked == true)
        {
            await SendToRongtaAsync().ConfigureAwait(true);
            return;
        }

        await SendToShtrikhAsync().ConfigureAwait(true);
    }

    private async Task SendToShtrikhAsync()
    {
        if (_allRows is not IEnumerable<ScalePluRowVm> rows)
            return;

        var selectedIds = rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.",
                "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        if (!int.TryParse((PluStartBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pluStart) || pluStart <= 0)
            pluStart = 1;

        if (DirectLanCheck.IsChecked == true)
        {
            await SendToShtrikhOverLanAsync(selectedIds, pluStart).ConfigureAwait(true);
            return;
        }

        SendButton.IsEnabled = false;
        StatusText.Text = Tr.T("Отправка…", "Жиберилүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        try
        {
            await App.CatalogApi.SendProductsToScaleAsync(pluStart, selectedIds, CancellationToken.None).ConfigureAwait(true);
            StatusText.Text = Tr.T(
                $"Отправлено на весы: {selectedIds.Count}.",
                $"Таразага жиберилди: {selectedIds.Count}.",
                $"Sent to the scale: {selectedIds.Count}.",
                $"Tartıya gönderildi: {selectedIds.Count}.",
                $"Taroziga yuborildi: {selectedIds.Count}.");
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"SendProductsToScaleAsync failed: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>Прямая выгрузка на весы по витой паре, без участия сервера NurCRM.
    ///
    /// Положение десятичной точки читаем С САМИХ ВЕСОВ и по нему переводим цену в МДЕ: если
    /// весы настроены на 0 знаков, а прислать им тыйыны — все цены окажутся в 100 раз больше.
    ///
    /// Номер ПЛУ берём из карточки товара, если он там задан; иначе нумеруем подряд от
    /// «Начальный ПЛУ». Код товара приравниваем к номеру ПЛУ — так товар находится и при
    /// настройке весов «доступ по номеру ПЛУ», и при «доступе по коду товара».</summary>
    private async Task SendToShtrikhOverLanAsync(List<string> selectedIds, int pluStart)
    {
        using var scale = CreateLanService();
        if (scale is null)
            return;

        SendButton.IsEnabled = false;
        try
        {
            StatusText.Text = Tr.T("Опрос весов…", "Таразадан маалымат алынууда…", "Polling the scale…", "Tartı sorgulanıyor…", "Tarozi so'ralmoqda…");
            var status = await scale.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            if (!status.IsIdle)
            {
                var busyReason = status.DescribeBusyReason();
                StatusText.Text = Tr.T($"Весы заняты ({busyReason}). Выйдите на весах в обычный режим и повторите.",
                    $"Тараза бош эмес ({busyReason}). Таразаны кадимки режимге которуп, кайра аракет кылыңыз.",
                    $"The scale is busy ({busyReason}). Switch the scale back to normal mode and try again.",
                    $"Tartı meşgul ({busyReason}). Tartıda normal moda dönüp tekrar deneyin.",
                    $"Tarozi band ({busyReason}). Tarozini oddiy rejimga o'tkazib, qayta urinib ko'ring.");
                return;
            }

            var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
            // 2026-09-28: «Код в штрих-коде» из таблицы (владелец правит его перед отправкой).
            var barcodeCodes = _allRows?
                .ToDictionary(r => r.Id, r => r.BarcodeCode) ?? new Dictionary<string, string>();
            var records = new List<ShtrikhPluRecord>();
            // Что на какой клавише окажется — показываем кассиру: панель подписывают руками,
            // и без этого списка непонятно, какую наклейку куда клеить.
            var keyMap = new List<(int Plu, string Name)>();
            var nextPlu = pluStart;
            foreach (var id in selectedIds)
            {
                if (!byId.TryGetValue(id, out var product))
                    continue;

                // 2026-09-23, живой баг («программа отправляет ПЛУ, но кнопки не работают»).
                //
                // Клавиши на панели весов (120 штук на ШТРИХ-ПРИНТ) вызывают ПЛУ по его
                // НОМЕРУ и по положению: клавиша 1 — ПЛУ 1, клавиша 2 — ПЛУ 2. Раньше сюда
                // подставлялся СОБСТВЕННЫЙ номер товара из каталога, а он произвольный —
                // у «Айфона», например, 10007. Запись уходила в ПЛУ 10007, до которого ни
                // одна клавиша не дотягивается, и панель выглядела нерабочей, хотя выгрузка
                // формально проходила.
                //
                // Поэтому по умолчанию нумеруем подряд: порядок товаров в списке и есть
                // порядок клавиш. Выключить можно галочкой — если весы настроены обращаться
                // к ПЛУ по коду товара, а не по номеру.
                var sequential = SequentialPluCheck.IsChecked == true;
                var plu = sequential || product.Plu is not > 0 ? nextPlu++ : product.Plu!.Value;
                if (!sequential && product.Plu is > 0)
                    nextPlu = Math.Max(nextPlu, plu + 1);

                keyMap.Add((plu, product.Title));

                // 2026-09-28: «Код товара» записи ПЛУ — это число, которое весы печатают в
                // весовом штрих-коде (Т в структуре ШК), и по нему касса ищет товар при скане.
                // Раньше сюда шёл номер ячейки ПЛУ: при нумерации «подряд» у товара с PLU 5 в
                // первой ячейке этикетка несла код 1, и касса находила чужой товар (с PLU 1)
                // или не находила никакой. Теперь — значение колонки «Код в штрих-коде»
                // (по умолчанию PLU товара / «Код товара» — см. DefaultBarcodeCode); пустое
                // или неверное — как раньше, номер ячейки.
                var productCode = barcodeCodes.TryGetValue(id, out var codeText)
                                  && int.TryParse((codeText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode)
                                  && parsedCode is >= 1 and <= 999999
                    ? parsedCode
                    : plu;

                records.Add(ShtrikhPrintLanScaleService.CreateRecord(
                    pluNumber: plu,
                    productCode: productCode,
                    name: product.Title,
                    priceSom: (decimal)LocalCartService.ParsePrice(product.PriceLine),
                    decimalPointDigits: status.DecimalPointDigits,
                    isPiece: !product.IsWeighted));
            }

            if (records.Count == 0)
            {
                StatusText.Text = Tr.T("Не удалось собрать данные для выгрузки — обновите каталог.", "Жүктөө үчүн маалыматтарды чогултуу мүмкүн болгон жок — каталогду жаңыртыңыз.", "Could not prepare the data for upload — refresh the catalog.", "Tartıya gönderilecek veriler hazırlanamadı — kataloğu güncelleyin.", "Yuklash uchun ma'lumotlarni to'plab bo'lmadi — katalogni yangilang.");
                return;
            }

            var progress = new Progress<ShtrikhUploadProgress>(p =>
                StatusText.Text = Tr.T($"{p.Stage}: {p.Done} из {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…", $"{p.Stage}: {p.Done} of {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…"));

            var result = await scale.UploadPlusAsync(records, progress, CancellationToken.None).ConfigureAwait(true);

            var firstKeyName = keyMap.FirstOrDefault().Name;
            StatusText.Text = result.Ok
                ? Tr.T($"Выгружено на весы: {result.Sent}. Клавиша 1 — «{firstKeyName}», далее по порядку списка.",
                    $"Таразага жүктөлдү: {result.Sent}. 1-баскыч — «{firstKeyName}», андан ары тизменин тартиби боюнча.",
                    $"Uploaded to the scale: {result.Sent}. Key 1 is “{firstKeyName}”, then in list order.",
                    $"Tartıya gönderildi: {result.Sent}. 1. tuş — «{firstKeyName}», sonrakiler liste sırasıyla.",
                    $"Taroziga yuklandi: {result.Sent}. 1-tugma — «{firstKeyName}», keyin ro'yxat tartibida.")
                : Tr.T($"Выгружено: {result.Sent}, с ошибками: {result.Failed}. ",
                    $"Жүктөлдү: {result.Sent}, ийгиликсиз: {result.Failed}. ",
                    $"Uploaded: {result.Sent}, failed: {result.Failed}. ",
                    $"Gönderildi: {result.Sent}, hatalı: {result.Failed}. ",
                    $"Yuklandi: {result.Sent}, xatolik bilan: {result.Failed}. ") + string.Join(" · ", result.Errors.Take(3));

            // Печатаем раскладку в журнал: панель на 120 клавиш подписывают вручную, и владельцу
            // нужен список «номер клавиши — товар», чтобы наклеить ярлыки.
            if (result.Ok && keyMap.Count > 0)
            {
                PosLogger.Log(
                    "Весы, раскладка клавиш: " + string.Join("; ", keyMap.Select(x => $"{x.Plu} — {x.Name}")),
                    "SCALES");
            }

            foreach (var error in result.Errors)
                PosLogger.Log($"Выгрузка ПЛУ по LAN: {error}", "SCALES");
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Прямая выгрузка на весы не удалась: {ex}", "SCALES");
            StatusText.Text = ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    // =====================================================================================
    // 2026-09-28 (вечер): TM-30F (Dahua) — прямая отправка PLU по сети и файл для их программы.
    // Протокол — DahuaTmProtocol, транспорт — DahuaTmScaleService. На живых весах не проверено.
    // =====================================================================================

    /// <summary>Пример этикетки TM-30F для первого отмеченного товара: формат ШК весов и префикс
    /// из «Настроек весов TM-30F», код — колонка «Код в штрих-коде», сверка — разбором кассы.</summary>
    private void UpdateTmBarcodeExample(ScalePluRowVm? row)
    {
        var prefs = UserPreferences.Instance;
        var byWeight = !string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        var format = prefs.TmScaleDahuaBarcode is { } saved && DahuaTmBarcodeFormat.Variants.Contains(saved)
            ? saved
            : DahuaTmBarcodeFormat.Recommended(byWeight);
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        const int grams = 392;
        var decimals = Math.Clamp(prefs.TmScalePricePoint, 0, 3);
        var amount = Math.Round((decimal)(row?.Price ?? 100) * grams / 1000m, decimals, MidpointRounding.AwayFromZero);
        var sample = DahuaTmBarcodeFormat.BuildSample(format, prefs.TmScaleBarcodePrefix, code, grams, DahuaTmProtocol.ScalePrice(amount, decimals));
        var weightFirst = DahuaTmBarcodeFormat.HasWeight(format)
                          && (!DahuaTmBarcodeFormat.HasAmount(format) || format.IndexOf('N') < format.IndexOf('E'));
        var (ok, verdict) = NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.VerifyWithKassa(sample, code, weightFirst, grams, amount);
        BarcodeExampleText.Text = Tr.T(
            $"Штрих-код на этикетке TM-30F для «{row?.Name}» (0,392 кг): {sample} — формат весов {format}, префикс {prefs.TmScaleBarcodePrefix:00}. ",
            $"«{row?.Name}» үчүн TM-30F этикеткасындагы штрих-код (0,392 кг): {sample} — тараза форматы {format}, префикс {prefs.TmScaleBarcodePrefix:00}. ",
            $"TM-30F label barcode for “{row?.Name}” (0.392 kg): {sample} — scale format {format}, prefix {prefs.TmScaleBarcodePrefix:00}. ",
            $"“{row?.Name}” için TM-30F etiket barkodu (0,392 kg): {sample} — tartı biçimi {format}, önek {prefs.TmScaleBarcodePrefix:00}. ",
            $"«{row?.Name}» uchun TM-30F yorlig‘idagi shtrix-kod (0,392 kg): {sample} — tarozi formati {format}, prefiks {prefs.TmScaleBarcodePrefix:00}. ")
            + (ok ? "✓ " : "⚠ ") + verdict;
    }

    /// <summary>Собирает записи PLU для TM-30F так же, как для Штрих-М по LAN: номер по порядку
    /// от «Начальный PLU» (или PLU товара), «Код товара» — колонка «Код в штрих-коде» (иначе номер
    /// PLU), тип — весовой/штучный из карточки, префикс ШК и срок годности — из настроек.</summary>
    private (List<DahuaTmPlu> Records, List<string> Problems, List<(int Plu, string Name)> KeyMap) BuildTmRecords(List<string> selectedIds, int pluStart)
    {
        var prefs = UserPreferences.Instance;
        var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
        var barcodeCodes = _allRows.ToDictionary(r => r.Id, r => r.BarcodeCode);
        var sequential = SequentialPluCheck.IsChecked == true;
        var decimals = Math.Clamp(prefs.TmScalePricePoint, 0, 3);
        var records = new List<DahuaTmPlu>();
        var problems = new List<string>();
        var keyMap = new List<(int Plu, string Name)>();
        var nextPlu = pluStart;
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var product))
                continue;

            var plu = sequential || product.Plu is not > 0 ? nextPlu++ : product.Plu!.Value;
            if (!sequential && product.Plu is > 0)
                nextPlu = Math.Max(nextPlu, plu + 1);

            var productCode = barcodeCodes.TryGetValue(id, out var codeText)
                              && long.TryParse((codeText ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode)
                              && parsedCode is >= 1 and <= 9_999_999
                ? parsedCode
                : plu;

            var record = new DahuaTmPlu
            {
                PluNumber = plu,
                ProductCode = productCode,
                Price = (decimal)LocalCartService.ParsePrice(product.PriceLine),
                WeighMode = product.IsWeighted ? DahuaTmWeighMode.Weighed : DahuaTmWeighMode.Piece,
                ShelfLifeDays = Math.Clamp(prefs.TmScaleShelfLifeDays, 0, 999),
                BarcodePrefix = Math.Clamp(prefs.TmScaleBarcodePrefix, 0, 99),
                Name = product.Title,
            };
            var problem = DahuaTmProtocol.Validate(record, decimals);
            if (problem != DahuaTmPluProblem.None)
            {
                problems.Add($"«{product.Title}» (PLU {plu}): {TmProblemText(problem)}");
                continue;
            }
            records.Add(record);
            keyMap.Add((plu, product.Title));
        }
        return (records, problems, keyMap);
    }

    private static string TmProblemText(DahuaTmPluProblem problem) => problem switch
    {
        DahuaTmPluProblem.BadPluNumber => Tr.T($"номер PLU должен быть от 1 до {DahuaTmProtocol.MaxPluNumber}", $"PLU номери 1ден {DahuaTmProtocol.MaxPluNumber}гө чейин болушу керек", $"the PLU number must be 1 to {DahuaTmProtocol.MaxPluNumber}", $"PLU numarası 1 ile {DahuaTmProtocol.MaxPluNumber} arasında olmalı", $"PLU raqami 1 dan {DahuaTmProtocol.MaxPluNumber} gacha bo‘lishi kerak"),
        DahuaTmPluProblem.BadProductCode => Tr.T("код в штрих-коде — до 7 цифр", "штрих-коддогу код — 7 санга чейин", "the code in the barcode is up to 7 digits", "barkoddaki kod en fazla 7 hane", "shtrix-koddagi kod — 7 raqamgacha"),
        DahuaTmPluProblem.PriceDoesNotFit => Tr.T("цена не помещается в 6 цифр весов (проверьте «Цена на весах» в настройках)", "баа таразанын 6 санына батпайт (жөндөөлөрдөгү «Таразадагы баа» текшериңиз)", "the price does not fit the scale's 6 digits (check “Price on the scale” in the settings)", "fiyat tartının 6 hanesine sığmıyor (ayarlardaki «Tartıdaki fiyat»ı kontrol edin)", "narx tarozining 6 raqamiga sig‘maydi (sozlamalardagi «Tarozidagi narx»ni tekshiring)"),
        DahuaTmPluProblem.BadShelfLife => Tr.T("срок годности — от 0 до 999 дней", "жарактуулук мөөнөтү — 0дөн 999 күнгө чейин", "shelf life must be 0 to 999 days", "raf ömrü 0 ile 999 gün arasında olmalı", "yaroqlilik muddati — 0 dan 999 kungacha"),
        DahuaTmPluProblem.BadBarcodePrefix => Tr.T("префикс штрихкода — 2 цифры", "штрих-код префикси — 2 сан", "the barcode prefix is 2 digits", "barkod öneki 2 hanedir", "shtrix-kod prefiksi — 2 raqam"),
        _ => problem.ToString(),
    };

    private static string TmErrorText(DahuaTmError error) => error switch
    {
        DahuaTmError.ConnectFailed => Tr.T("нет подключения к весам (IP, порт, кабель; закройте «Русский масштаб», если он подключён к весам)", "таразага туташуу жок (IP, порт, кабель; «Русский масштаб» таразага туташып турса, аны жабыңыз)", "no connection to the scale (IP, port, cable; close “Russian Scale” if it is connected to the scale)", "tartıya bağlantı yok (IP, port, kablo; «Русский масштаб» tartıya bağlıysa kapatın)", "taroziga ulanish yo‘q (IP, port, kabel; «Русский масштаб» taroziga ulangan bo‘lsa, uni yoping)"),
        DahuaTmError.NoReply => Tr.T("весы не ответили за 2,5 с после 4 повторов", "тараза 4 кайталоодон кийин 2,5 с ичинде жооп берген жок", "the scale did not answer within 2.5 s after 4 retries", "tartı 4 tekrardan sonra 2,5 sn içinde yanıt vermedi", "tarozi 4 takrordan keyin 2,5 s ichida javob bermadi"),
        DahuaTmError.ConnectionLost => Tr.T("весы разорвали соединение", "тараза туташууну үздү", "the scale closed the connection", "tartı bağlantıyı kesti", "tarozi ulanishni uzdi"),
        DahuaTmError.SendFailed => Tr.T("не удалось отправить данные", "маалыматтарды жөнөтүү мүмкүн болгон жок", "could not send the data", "veriler gönderilemedi", "ma’lumotlarni yuborib bo‘lmadi"),
        DahuaTmError.Cancelled => Tr.T("остановлено", "токтотулду", "stopped", "durduruldu", "to‘xtatildi"),
        _ => error.ToString(),
    };

    private async Task SendToTmAsync()
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.",
                "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang.");
            return;
        }

        var prefs = UserPreferences.Instance;
        var scale = DahuaTmScaleService.TryCreate(prefs.TmScaleIp, prefs.TmScalePort);
        if (scale is null)
        {
            StatusText.Text = Tr.T("Не задан IP весов TM-30F — «Настройки весов» → «Подключение».",
                "TM-30F таразасынын IP'си коюлган эмес — «Тараза жөндөөлөрү» → «Туташуу».",
                "The TM-30F scale IP is not set — “Scale settings” → “Connection”.",
                "TM-30F tartı IP'si girilmemiş — «Tartı ayarları» → «Bağlantı».",
                "TM-30F tarozi IP'si kiritilmagan — «Tarozi sozlamalari» → «Ulanish».");
            return;
        }

        if (!int.TryParse((PluStartBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pluStart) || pluStart <= 0)
            pluStart = 1;

        var (records, problems, keyMap) = BuildTmRecords(selectedIds, pluStart);
        if (problems.Count > 0)
        {
            // Частичную выгрузку не делаем: иначе на весах окажется «половина» списка и сдвиг клавиш.
            StatusText.Text = Tr.T("Не отправлено — исправьте: ", "Жөнөтүлгөн жок — оңдоңуз: ", "Not sent — fix: ", "Gönderilmedi — düzeltin: ", "Yuborilmadi — tuzating: ")
                              + string.Join(" · ", problems.Take(3)) + (problems.Count > 3 ? $" (+{problems.Count - 3})" : "");
            return;
        }
        if (records.Count == 0)
        {
            StatusText.Text = Tr.T("Не удалось собрать данные для выгрузки — обновите каталог.", "Жүктөө үчүн маалыматтарды чогултуу мүмкүн болгон жок — каталогду жаңыртыңыз.", "Could not prepare the data for upload — refresh the catalog.", "Tartıya gönderilecek veriler hazırlanamadı — kataloğu güncelleyin.", "Yuklash uchun ma'lumotlarni to'plab bo'lmadi — katalogni yangilang.");
            return;
        }

        var mode = string.Equals(prefs.TmScaleSendMode, "batch", StringComparison.OrdinalIgnoreCase) ? DahuaTmSendMode.Batch : DahuaTmSendMode.LineByLine;
        _tmCts = new CancellationTokenSource();
        SendButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");
        StatusText.Text = Tr.T($"Подключение к весам {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} таразасына туташуу…", $"Connecting to the scale {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} tartısına bağlanılıyor…", $"{scale.Host}:{scale.Port} taroziga ulanilmoqda…");
        try
        {
            var progress = new Progress<DahuaTmUploadProgress>(p =>
                StatusText.Text = Tr.T($"Отправка на весы: {p.Done} из {p.Total}…", $"Таразага жөнөтүү: {p.Total} ичинен {p.Done}…", $"Sending to the scale: {p.Done} of {p.Total}…", $"Tartıya gönderiliyor: {p.Done} / {p.Total}…", $"Taroziga yuborilmoqda: {p.Total} dan {p.Done}…"));
            var result = await scale.UploadPlusAsync(records, Math.Clamp(prefs.TmScalePricePoint, 0, 3), mode, progress, _tmCts.Token).ConfigureAwait(true);
            PosLogger.Log($"TM-30F (Dahua): выгрузка PLU {scale.Host}:{scale.Port}: всего {result.Total}, отправлено {result.Sent}, ответов {result.Acknowledged}, без маркера {result.UnframedReplies}, повторов {result.Retries}, ошибка {result.Error} {result.Detail}", "SCALES");

            var firstKeyName = keyMap.FirstOrDefault().Name;
            if (result.Ok)
            {
                StatusText.Text = Tr.T($"Отправлено на весы: {result.Total}. PLU {keyMap[0].Plu} — «{firstKeyName}», далее по порядку списка. Проверьте товар на весах.",
                                       $"Таразага жөнөтүлдү: {result.Total}. PLU {keyMap[0].Plu} — «{firstKeyName}», андан ары тизменин тартиби боюнча. Товарды таразадан текшериңиз.",
                                       $"Sent to the scale: {result.Total}. PLU {keyMap[0].Plu} is “{firstKeyName}”, then in list order. Check the item on the scale.",
                                       $"Tartıya gönderildi: {result.Total}. PLU {keyMap[0].Plu} — «{firstKeyName}», sonrakiler liste sırasıyla. Ürünü tartıda kontrol edin.",
                                       $"Taroziga yuborildi: {result.Total}. PLU {keyMap[0].Plu} — «{firstKeyName}», keyin ro'yxat tartibida. Tovarni tarozida tekshiring.")
                                  + (result.UnframedReplies > 0
                                      ? Tr.T($" Внимание: {result.UnframedReplies} ответ(ов) весов не по ожидаемой форме — см. журнал обмена.", $" Көңүл буруңуз: таразанын {result.UnframedReplies} жообу күтүлгөн формада эмес — алмашуу журналын караңыз.", $" Note: {result.UnframedReplies} scale reply(ies) not in the expected form — see the exchange log.", $" Dikkat: tartının {result.UnframedReplies} yanıtı beklenen biçimde değil — iletişim günlüğüne bakın.", $" Diqqat: tarozining {result.UnframedReplies} javobi kutilgan shaklda emas — almashuv jurnaliga qarang.")
                                      : "");
                PosLogger.Log("TM-30F, раскладка PLU: " + string.Join("; ", keyMap.Select(x => $"{x.Plu} — {x.Name}")), "SCALES");
            }
            else
            {
                StatusText.Text = Tr.T($"Отправка не завершена: {TmErrorText(result.Error)}. Принято весами: {result.Acknowledged + result.UnframedReplies} из {result.Total}",
                                       $"Жөнөтүү аягына чыккан жок: {TmErrorText(result.Error)}. Тараза кабыл алды: {result.Total} ичинен {result.Acknowledged + result.UnframedReplies}",
                                       $"Sending did not finish: {TmErrorText(result.Error)}. Accepted by the scale: {result.Acknowledged + result.UnframedReplies} of {result.Total}",
                                       $"Gönderim tamamlanmadı: {TmErrorText(result.Error)}. Tartının kabul ettiği: {result.Acknowledged + result.UnframedReplies} / {result.Total}",
                                       $"Yuborish tugamadi: {TmErrorText(result.Error)}. Tarozi qabul qildi: {result.Total} dan {result.Acknowledged + result.UnframedReplies}")
                                  + (result.FailedPlu > 0 ? $" (PLU {result.FailedPlu})" : "")
                                  + Tr.T(". Журнал обмена: ", ". Алмашуу журналы: ", ". Exchange log: ", ". İletişim günlüğü: ", ". Almashuv jurnali: ") + DahuaTmScaleService.ExchangeLogPath;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"TM-30F (Dahua): выгрузка не удалась: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            _tmCts.Dispose();
            _tmCts = null;
            SendButton.Content = _sendButtonDefaultText;
        }
    }

    /// <summary>Окно закрыли во время отправки на TM-30F — останавливаем её.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _tmCts?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>Запасной путь: файл импорта «DIGI_TOP2000» для программы «Русский масштаб»
    /// (Настройки товаров → Импорт → Text Files → DIGI_TOP2000). Кодировка Windows-1251.</summary>
    private async void TmExport_Click(object? sender, RoutedEventArgs e)
    {
        var selectedIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
            selectedIds = _allRows.Select(r => r.Id).ToList();
        if (selectedIds.Count == 0)
        {
            StatusText.Text = Tr.T("Нечего выгружать: весовых товаров нет.", "Чыгарууга эч нерсе жок: салмактуу товарлар жок.", "Nothing to export: there are no weighed products.", "Dışa aktarılacak bir şey yok: tartılı ürün yok.", "Eksport qilish uchun hech narsa yo'q: vaznli mahsulotlar yo'q.");
            return;
        }
        if (!int.TryParse((PluStartBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pluStart) || pluStart <= 0)
            pluStart = 1;
        var (records, problems, _) = BuildTmRecords(selectedIds, pluStart);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Файл для «Русского масштаба» (DIGI_TOP2000)", "«Русский масштаб» үчүн файл (DIGI_TOP2000)", "File for “Russian Scale” (DIGI_TOP2000)", "«Русский масштаб» için dosya (DIGI_TOP2000)", "«Русский масштаб» uchun fayl (DIGI_TOP2000)"),
            SuggestedFileName = $"tm30f-digi_top2000-{DateTime.Now:yyyy-MM-dd}.txt",
            FileTypeChoices = [new FilePickerFileType("TXT") { Patterns = ["*.txt", "*.plu"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(DahuaTmProtocol.BuildDigiTop2000File(records));
        }
        catch (Exception ex)
        {
            StatusText.Text = Tr.T($"Не удалось сохранить файл: {ex.Message}", $"Файлды сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the file: {ex.Message}", $"Dosya kaydedilemedi: {ex.Message}", $"Faylni saqlab bo'lmadi: {ex.Message}");
            return;
        }
        StatusText.Text = Tr.T($"Сохранено строк: {records.Count}. В «Русском масштабе»: Настройки товаров → Импорт → Text Files → DIGI_TOP2000, затем «Скачать» на весы.",
                               $"Сакталган саптар: {records.Count}. «Русский масштаб»та: Настройки товаров → Импорт → Text Files → DIGI_TOP2000, андан кийин таразага «Скачать».",
                               $"Rows saved: {records.Count}. In “Russian Scale”: Merchandise settings → Import → Text Files → DIGI_TOP2000, then “Download” to the scale.",
                               $"Kaydedilen satır: {records.Count}. «Русский масштаб»da: Настройки товаров → Импорт → Text Files → DIGI_TOP2000, sonra tartıya «Скачать».",
                               $"Saqlangan qatorlar: {records.Count}. «Русский масштаб»da: Настройки товаров → Импорт → Text Files → DIGI_TOP2000, so‘ng taroziga «Скачать».")
                          + (problems.Count > 0 ? Tr.T($" Пропущено с ошибками: {problems.Count} — ", $" Ката менен өткөрүлдү: {problems.Count} — ", $" Skipped with errors: {problems.Count} — ", $" Hatalı atlanan: {problems.Count} — ", $" Xato bilan o‘tkazib yuborildi: {problems.Count} — ") + problems[0] : "");
    }

    private async Task SendToRongtaAsync()
    {
        if (RongtaSourceServerRadio.IsChecked == true)
        {
            await SendToRongtaViaOwnServerAsync().ConfigureAwait(true);
            return;
        }

        await SendToRongtaViaSiteAsync().ConfigureAwait(true);
    }

    /// <summary>Способ по умолчанию: скачиваем .txp с того же эндпоинта, что и вкладка
    /// «Rongta» на сайте (уже с транслитерацией кириллицы), затем находим/ставим
    /// официальную RLS1000 и "нажимаем" в ней F9. ЧЕСТНО: результат — "команда передана", не
    /// "весы обновились" (PostMessage не подтверждает выполнение).</summary>
    private async Task SendToRongtaViaSiteAsync()
    {
        SendButton.IsEnabled = false;
        try
        {
            StatusText.Text = Tr.T("Скачивание файла PLU…", "PLU файлы жүктөлүүдө…",
                "Downloading the PLU file…", "PLU dosyası indiriliyor…", "PLU fayli yuklanmoqda…");
            var txp = await App.CatalogApi.DownloadScaleExportAsync(translit: true, CancellationToken.None).ConfigureAwait(true);
            if (txp is null || txp.Length == 0)
            {
                StatusText.Text = Tr.T("Не удалось скачать файл PLU с сервера.", "Серверден PLU файлын жүктөө мүмкүн болбоду.",
                    "Could not download the PLU file from the server.", "PLU dosyası sunucudan indirilemedi.",
                    "Serverdan PLU faylini yuklab bo'lmadi.");
                return;
            }

            var exePath = await EnsureRls1000ExePathAsync().ConfigureAwait(true);
            if (exePath is null)
                return;

            var workingTxpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                NurMarketKassa.Services.AppMode.DataFolderName, "Rongta", "products.txp");

            StatusText.Text = Tr.T("Запуск RLS1000…", "RLS1000 иштетилүүдө…", "Starting RLS1000…", "RLS1000 başlatılıyor…", "RLS1000 ishga tushirilmoqda…");
            var result = await RongtaScaleAutomationService.SendPluAsync(txp, workingTxpPath, exePath, CancellationToken.None).ConfigureAwait(true);
            StatusText.Text = FormatRongtaResult(result);
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Rongta send failed: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>Способ по просьбе владельца (2026-09-19: "свой сервер... из локальной
    /// базы") — PLU строится прямо из уже загруженного каталога (CatalogCacheService), без
    /// обращения к сайту. Слушаем один входящий коннект от RLS1000 (RongtaTcpServerService,
    /// протокол RongtaTcpProtocol) и параллельно "нажимаем" F9, чтобы RLS1000 (заранее один
    /// раз настроенная на TCP/IP-режим, см. doc-comment RongtaScaleAutomationService)
    /// подключилась к нам сама. НЕ ПРОВЕРЕНО на реальном железе.</summary>
    private async Task SendToRongtaViaOwnServerAsync()
    {
        SendButton.IsEnabled = false;
        try
        {
            var products = NurMarketKassa.Services.CatalogCacheService.Products
                .Where(p => p.IsWeighted && p.Plu is > 0)
                .Select(p => (Plu: p.Plu!.Value, Name: p.Title, Price: LocalCartService.ParsePrice(p.PriceLine)))
                .ToList();

            if (products.Count == 0)
            {
                StatusText.Text = Tr.T(
                    "Нет весовых товаров с заполненным PLU в локальном каталоге.",
                    "Локалдык каталогдо PLU толтурулган салмактуу товар жок.",
                    "No weighed products with a PLU set in the local catalog.",
                    "Yerel katalogda PLU'su ayarlanmış tartılabilir ürün yok.",
                    "Mahalliy katalogda PLU to'ldirilgan vaznli mahsulotlar yo'q.");
                return;
            }

            if (!int.TryParse((RongtaPortBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port is <= 0 or > 65535)
                port = UserPreferences.Instance.RongtaServerPort;

            // 2026-09-21, по просьбе владельца: тот же протокол (RongtaTcpServerService) хочет
            // проверить и на другой модели весов (VEVOR TM-30F), у которой нет RLS1000.exe и
            // своей автоматизации F9 (см. doc-comment RongtaScaleAutomationService — она ищет
            // именно процесс "RLS1000"). Раньше отсутствие RLS1000 сразу останавливало отправку
            // ("EnsureRls1000ExePathAsync" возвращал null). Теперь это не блокирует: если
            // RLS1000 не найдена, сервер всё равно запускается — просто без автонажатия F9,
            // кассир должен сам запустить обновление PLU в СВОЕЙ программе весов (даём больше
            // времени на это — 90 секунд вместо 25).
            var exePath = RongtaSetupService.TryFindInstalledExePath();
            var connectTimeout = TimeSpan.FromSeconds(25);

            if (exePath is null)
            {
                connectTimeout = TimeSpan.FromSeconds(90);
                StatusText.Text = Tr.T(
                    $"RLS1000 не найдена — слушаем порт {port} без автозапуска. В программе ваших весов " +
                    "включите режим TCP/IP на этот компьютер и этот порт, затем запустите отправку/обновление " +
                    "PLU вручную (есть 90 секунд).",
                    $"RLS1000 табылган жок — {port} портун автоматтык жүктөөсүз угабыз. Таразаңыздын " +
                    "программасында TCP/IP режимин ушул компьютерге жана порту кошуп, PLU жиберүүнү/жаңыртууну " +
                    "өзүңүз колдонуп иштетиңиз (90 секунд бар).",
                    $"RLS1000 was not found — listening on port {port} without auto-trigger. In your scale's " +
                    "own software, enable TCP/IP mode pointing at this computer and this port, then start the " +
                    "PLU update yourself (you have 90 seconds).",
                    $"RLS1000 bulunamadı — {port} portu otomatik tetikleme olmadan dinleniyor. Tartınızın kendi " +
                    "yazılımında TCP/IP modunu bu bilgisayara ve bu porta yönlendirerek etkinleştirin, ardından " +
                    "PLU güncellemesini kendiniz başlatın (90 saniyeniz var).",
                    $"RLS1000 topilmadi — {port} porti avtomatik ishga tushirishsiz tinglanmoqda. Tarozingizning " +
                    "o'z dasturida TCP/IP rejimini shu kompyuterga va shu portga yo'naltirib yoqing, so'ngra PLU " +
                    "yangilashni o'zingiz boshlang (90 soniyangiz bor).");
            }
            else
            {
                StatusText.Text = Tr.T(
                    $"Ждём подключения RLS1000 на порт {port}…", $"RLS1000дин {port}-портко туташуусу күтүлүүдө…",
                    $"Waiting for RLS1000 to connect on port {port}…", $"RLS1000'in {port} portuna bağlanması bekleniyor…",
                    $"RLS1000ning {port} portiga ulanishi kutilmoqda…");
            }

            var serverTask = RongtaTcpServerService.RunOnceAsync(port, products, connectTimeout, CancellationToken.None);

            if (exePath is not null)
            {
                // Даём слушателю время начать Accept() до того, как F9 заставит RLS1000 подключиться.
                await Task.Delay(300).ConfigureAwait(true);
                var triggerResult = await RongtaScaleAutomationService.TriggerDownloadPluAsync(exePath, CancellationToken.None).ConfigureAwait(true);
                if (!triggerResult.IsSuccess)
                {
                    StatusText.Text = Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") + triggerResult.ErrorMessage;
                    return;
                }
            }

            var serverResult = await serverTask.ConfigureAwait(true);
            StatusText.Text = serverResult.IsSuccess
                ? Tr.T(
                    $"Отправлено на весы через свой сервер: {serverResult.RecordsSent}. Проверьте PLU на весах.",
                    $"Таразага өз сервериңиз аркылуу жиберилди: {serverResult.RecordsSent}. Таразадагы PLUну текшериңиз.",
                    $"Sent to the scale via the built-in server: {serverResult.RecordsSent}. Check the PLU on the scale.",
                    $"Kendi sunucumuz üzerinden tartıya gönderildi: {serverResult.RecordsSent}. Tartıdaki PLU'ları kontrol edin.",
                    $"O'z serverimiz orqali taroziga yuborildi: {serverResult.RecordsSent}. Tarozidagi PLUlarni tekshiring.")
                : Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") + serverResult.ErrorMessage;
        }
        catch (System.Exception ex)
        {
            PosLogger.Log($"Rongta own-server send failed: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private string FormatRongtaResult(RongtaSendResult result) =>
        result.IsSuccess
            ? Tr.T(
                "Команда передана в RLS1000 — проверьте PLU на весах.",
                "Буйрук RLS1000гө берилди — таразадагы PLUну текшериңиз.",
                "The command was sent to RLS1000 — check the PLU on the scale.",
                "Komut RLS1000'e iletildi — tartıdaki PLU'ları kontrol edin.",
                "Buyruq RLS1000 ga yuborildi — tarozidagi PLUlarni tekshiring.")
            : Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") + result.ErrorMessage;

    /// <summary>Находит установленную RLS1000, ставит её из бандла (если он появился) или
    /// сообщает кассиру, что нужна ручная установка — общая часть обеих веток Rongta.</summary>
    private async Task<string?> EnsureRls1000ExePathAsync()
    {
        var exePath = RongtaSetupService.TryFindInstalledExePath();
        if (exePath is null && File.Exists(RongtaSetupService.BundledInstallerPath))
        {
            StatusText.Text = Tr.T("Установка RLS1000…", "RLS1000 орнотулууда…", "Installing RLS1000…", "RLS1000 kuruluyor…", "RLS1000 o'rnatilmoqda…");
            await RongtaSetupService.InstallSilentlyAsync(RongtaSetupService.BundledInstallerPath, CancellationToken.None).ConfigureAwait(true);
            exePath = RongtaSetupService.TryFindInstalledExePath();
        }

        if (exePath is null)
        {
            StatusText.Text = Tr.T(
                "Программа RLS1000 не найдена и не установлена. Установите её вручную и повторите.",
                "RLS1000 программасы табылган жок жана орнотулган жок. Аны кол менен орнотуп, кайра аракет кылыңыз.",
                "RLS1000 was not found and could not be installed. Install it manually and try again.",
                "RLS1000 programı bulunamadı ve kurulamadı. Elle kurup tekrar deneyin.",
                "RLS1000 dasturi topilmadi va o'rnatilmadi. Uni qo'lda o'rnating va qayta urinib ko'ring.");
        }

        return exePath;
    }

    /// <summary>Выгружает PLU весовых товаров в CSV: PLU, название, единица, цена.
    ///
    /// Разделитель «;», а не запятая: цена в русской локали пишется через запятую, и с
    /// запятой-разделителем Excel разложил бы «160,00» на две колонки. Тот же разделитель
    /// понимает и собственный импорт кассы (ProductCsvImporter сам определяет «;» или «,»).
    /// Кодировка — UTF-8 С BOM, иначе Excel открывает кириллицу «кракозябрами».
    ///
    /// Выгружаются отмеченные строки; если не отмечено ничего — все, чтобы пустой файл не
    /// оказался неожиданностью.</summary>
    private async void ExportCsv_Click(object? sender, RoutedEventArgs e) =>
        await ExportPluCsvAsync("plu").ConfigureAwait(true);

    /// <param name="baseName">Начало имени файла: «plu» для обычной выгрузки, «ai-scale-plu»
    /// для AI-весов — чтобы в папке загрузок было видно, для чего файл.</param>
    private async Task ExportPluCsvAsync(string baseName)
    {
        if (_allRows is not IEnumerable<ScalePluRowVm> allRows)
            return;

        var list = allRows.ToList();
        var rows = list.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            rows = list;

        if (rows.Count == 0)
        {
            StatusText.Text = Tr.T(
                "Нечего выгружать: весовых товаров нет.",
                "Чыгарууга эч нерсе жок: салмактуу товарлар жок.",
                "Nothing to export: there are no weighed products.",
                "Dışa aktarılacak bir şey yok: tartılı ürün yok.",
                "Eksport qilish uchun hech narsa yo'q: vaznli mahsulotlar yo'q.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить PLU в CSV", "PLU'ну CSV'ге сактоо", "Save PLU to CSV",
                         "PLU listesini CSV olarak kaydet", "PLU ro'yxatini CSV faylga saqlash"),
            SuggestedFileName = $"{baseName}-{DateTime.Now:yyyy-MM-dd}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("PLU;Название;Единица;Цена");
        // Сортируем PLU ЧИСЛОМ: по строке получилось бы 1, 10, 1003, 111, 88 — в программе
        // весов такой файл читать невозможно. Строки без номера уходят в конец.
        foreach (var row in rows
                     .OrderBy(r => int.TryParse(r.PluText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                         ? n
                         : int.MaxValue)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // PLU без значения показывается в таблице как «—»; в файл кладём пустую ячейку,
            // иначе программа весов попытается прочитать тире как номер.
            var plu = row.PluText == "—" ? "" : row.PluText;
            sb.Append(Csv(plu)).Append(';')
              .Append(Csv(row.Name)).Append(';')
              .Append(Csv(row.Unit)).Append(';')
              .AppendLine(row.Price.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU")));
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            var bytes = new UTF8Encoding(true).GetBytes(sb.ToString());
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            StatusText.Text = Tr.T($"Не удалось сохранить файл: {ex.Message}",
                                   $"Файлды сактоо мүмкүн болгон жок: {ex.Message}", $"Could not save the file: {ex.Message}", $"Dosya kaydedilemedi: {ex.Message}", $"Faylni saqlab bo'lmadi: {ex.Message}");
            return;
        }

        var withoutPlu = rows.Count(r => r.PluText == "—");
        StatusText.Text = withoutPlu == 0
            ? Tr.T($"Выгружено строк: {rows.Count}.", $"Файлга чыгарылган саптар: {rows.Count}.",
                   $"Rows exported: {rows.Count}.", $"Dışa aktarılan satır: {rows.Count}.",
                   $"Eksport qilingan qatorlar: {rows.Count}.")
            : Tr.T($"Выгружено строк: {rows.Count}, из них без PLU: {withoutPlu} — им номер нужно задать в карточке товара.",
                   $"Файлга чыгарылган саптар: {rows.Count}, анын ичинен PLU'су жоктору: {withoutPlu} — алардын номерин товардын карточкасында коюңуз.", $"Rows exported: {rows.Count}, {withoutPlu} of them without a PLU — assign PLU numbers in their product cards.", $"Dışa aktarılan satır: {rows.Count}, bunlardan PLU'suz olan: {withoutPlu} — bunların numarasını ürün kartında girin.", $"Eksport qilingan qatorlar: {rows.Count}, shundan PLUsiz: {withoutPlu} — ularning raqamini mahsulot kartochkasida belgilang.");
    }

    /// <summary>Экранирование ячейки CSV: точка с запятой, кавычки и перенос строки внутри
    /// названия иначе сдвинут все колонки вправо.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private sealed class ScalePluRowVm : INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string PluText { get; init; } = "";
        public string PriceLine { get; init; } = "";

        /// <summary>Единица измерения товара («кг», «шт.») — нужна и в таблице, и в выгрузке:
        /// программы весов сопоставляют по ней тип товара (весовой/штучный).</summary>
        public string Unit { get; init; } = "";

        /// <summary>Цена числом. PriceLine — оформленная строка для экрана («160,00 сом»), в CSV
        /// её класть нельзя: ни Excel, ни программа весов такую ячейку числом не прочитают.</summary>
        public double Price { get; init; }

        /// <summary>2026-09-28: число, которое весы напечатают в весовом ШК («Код товара»
        /// записи ПЛУ). Правится в таблице перед прямой выгрузкой.</summary>
        public string BarcodeCode { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
