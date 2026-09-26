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
        BrandShtrikhRadio.IsChecked = brand != BrandRongta && brand != BrandAi;

        var source = UserPreferences.Instance.RongtaDataSource;
        RongtaSourceServerRadio.IsChecked = source == RongtaSourceServer;
        RongtaSourceSiteRadio.IsChecked = source != RongtaSourceServer;
        RongtaPortBox.Text = UserPreferences.Instance.RongtaServerPort.ToString(CultureInfo.InvariantCulture);

        // Сами адрес/порт/пароль читает и пишет окно настроек подключения
        // (ScaleConnectionDialog) — здесь остаётся только галочка.
        DirectLanCheck.IsChecked = UserPreferences.Instance.ShtrikhDirectLan;

        _sendButtonDefaultText = SendButton.Content;

        ApplyBrandVisibility();
        ApplyRongtaSourceVisibility();
        ApplyDirectLanVisibility();
        LoadRows();
    }

    private void BrandRadio_Click(object? sender, RoutedEventArgs e)
    {
        UserPreferences.Instance.ScaleBrand =
            BrandRongtaRadio.IsChecked == true ? BrandRongta
            : BrandAiRadio.IsChecked == true ? BrandAi
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
    private void ApplyDirectLanVisibility() =>
        LanSettingsButton.IsVisible = BrandRongtaRadio.IsChecked != true
                                      && BrandAiRadio.IsChecked != true
                                      && DirectLanCheck.IsChecked == true;


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
        var isAi = BrandAiRadio.IsChecked == true;
        PluStartRow.IsVisible = !isRongta && !isAi;
        ApplyDirectLanVisibility();
        RongtaSourceRow.IsVisible = isRongta;
        SendButton.Content = isAi ? "Сохранить файл для весов" : _sendButtonDefaultText;

        if (isAi)
        {
            SubtitleText.Text = Tr.T(
                "AI весы: касса готовит файл со списком (PLU, название, единица, цена), а вы загружаете его программой весов. Прямой заливки по сети пока нет — у этих весов нет общего протокола, каждая модель идёт со своей программой.",
                "AI тараза: касса тизме менен файл даярдайт (PLU, аталышы, бирдиги, баасы), аны тараза программасы менен жүктөйсүз. Тармак аркылуу түз жүктөө азырынча жок.",
                "AI scales: the till prepares a file (PLU, name, unit, price) and you load it with the scale's own software. Direct upload over the network is not available yet - these scales have no common protocol.",
                "AI terazi: kasa listeyi dosya olarak hazırlar (PLU, ad, birim, fiyat), siz de tartının kendi yazılımıyla yüklersiniz. Ağ üzerinden doğrudan yükleme henüz yok.",
                "AI tarozi: kassa ro'yxatni fayl qilib tayyorlaydi (PLU, nomi, birligi, narxi), siz uni tarozi dasturi bilan yuklaysiz. Tarmoq orqali to'g'ridan-to'g'ri yuklash hozircha yo'q.");
            return;
        }

        if (isRongta)
        {
            SubtitleText.Text = Tr.T(
                "Rongta: на весы уйдёт весь список весовых товаров (выбор галочками здесь не действует) — через встроенный запуск RLS1000.",
                "Rongta: таразага бардык салмактуу товарлар жиберилет (белгилер бул жерде таасир этпейт) — RLS1000 аркылуу.",
                "Rongta: the entire weighed-product list is sent to the scale (checkboxes here don't apply) — via the built-in RLS1000 launch.",
                "Rongta: tartıya tüm tartılabilir ürün listesi gönderilir (buradaki onay kutuları etkisizdir) — yerleşik RLS1000 başlatma yoluyla.",
                "Rongta: tarozga barcha vazn mahsulotlari ro'yxati yuboriladi (bu yerdagi katakchalar ta'sir qilmaydi) — o'rnatilgan RLS1000 orqali.");
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
                IsSelected = true,
            })
            .ToList();

        ProductsGrid.ItemsSource = rows;
        EmptyText.IsVisible = rows.Count == 0;
        StatusText.Text = "";
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => SetAllSelected(true);

    private void SelectNone_Click(object? sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        if (ProductsGrid.ItemsSource is not IEnumerable<ScalePluRowVm> rows)
            return;
        foreach (var row in rows)
            row.IsSelected = selected;
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (BrandAiRadio.IsChecked == true)
        {
            // Для AI-весов «отправить» — это подготовить файл: заливать напрямую пока нечем.
            await ExportPluCsvAsync("ai-scale-plu").ConfigureAwait(true);
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
        if (ProductsGrid.ItemsSource is not IEnumerable<ScalePluRowVm> rows)
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
                $"Teraziye gönderildi: {selectedIds.Count}.",
                $"Tarozga yuborildi: {selectedIds.Count}.");
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
            StatusText.Text = "Опрос весов…";
            var status = await scale.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            if (!status.IsIdle)
            {
                StatusText.Text = $"Весы заняты ({status.DescribeBusyReason()}). Выйдите на весах в обычный режим и повторите.";
                return;
            }

            var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
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

                records.Add(ShtrikhPrintLanScaleService.CreateRecord(
                    pluNumber: plu,
                    productCode: plu,
                    name: product.Title,
                    priceSom: (decimal)LocalCartService.ParsePrice(product.PriceLine),
                    decimalPointDigits: status.DecimalPointDigits,
                    isPiece: !product.IsWeighted));
            }

            if (records.Count == 0)
            {
                StatusText.Text = "Не удалось собрать данные для выгрузки — обновите каталог.";
                return;
            }

            var progress = new Progress<ShtrikhUploadProgress>(p =>
                StatusText.Text = $"{p.Stage}: {p.Done} из {p.Total}…");

            var result = await scale.UploadPlusAsync(records, progress, CancellationToken.None).ConfigureAwait(true);

            StatusText.Text = result.Ok
                ? $"Выгружено на весы: {result.Sent}. Клавиша 1 — «{keyMap.FirstOrDefault().Name}», далее по порядку списка."
                : $"Выгружено: {result.Sent}, с ошибками: {result.Failed}. " + string.Join(" · ", result.Errors.Take(3));

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
                    "Локалдык каталогдо PLUсу бар салмактуу товар жок.",
                    "No weighed products with a PLU set in the local catalog.",
                    "Yerel katalogda PLU'su ayarlanmış tartılabilir ürün yok.",
                    "Lokal katalogda PLU o'rnatilgan vazn mahsulotlari yo'q.");
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
                    $"Ждём подключения RLS1000 на порт {port}…", $"RLS1000нун {port}-портко туташуусун күтүүдө…",
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
                    $"Өз сервери аркылуу жиберилди: {serverResult.RecordsSent}. Таразадагы PLUну текшериңиз.",
                    $"Sent to the scale via the built-in server: {serverResult.RecordsSent}. Check the PLU on the scale.",
                    $"Kendi sunucumuz üzerinden gönderildi: {serverResult.RecordsSent}. Tartıdaki PLU'yu kontrol edin.",
                    $"O'z serverimiz orqali yuborildi: {serverResult.RecordsSent}. Tarozdagi PLUni tekshiring.")
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
                "Komut RLS1000'e gönderildi — tartıdaki PLU'yu kontrol edin.",
                "Buyruq RLS1000ga yuborildi — tarozdagi PLUni tekshiring.")
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
                "RLS1000 was not found or installed. Install it manually and try again.",
                "RLS1000 bulunamadı veya kurulmadı. Manuel olarak kurun ve tekrar deneyin.",
                "RLS1000 topilmadi yoki o'rnatilmadi. Uni qo'lda o'rnating va qayta urinib ko'ring.");
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
        if (ProductsGrid.ItemsSource is not IEnumerable<ScalePluRowVm> allRows)
            return;

        var list = allRows.ToList();
        var rows = list.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            rows = list;

        if (rows.Count == 0)
        {
            StatusText.Text = Tr.T(
                "Нечего выгружать: весовых товаров нет.",
                "Жүктөөгө эч нерсе жок: тараза товарлары жок.",
                "Nothing to export: there are no weighed products.",
                "Dışa aktarılacak bir şey yok: tartılan ürün yok.",
                "Yuklash uchun hech narsa yo'q: tarozi mahsulotlari yo'q.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить PLU в CSV", "PLU'ну CSV'ге сактоо", "Save PLU to CSV",
                         "PLU'yu CSV olarak kaydet", "PLU'ni CSV ga saqlash"),
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
                                   $"Файлды сактоо мүмкүн болгон жок: {ex.Message}");
            return;
        }

        var withoutPlu = rows.Count(r => r.PluText == "—");
        StatusText.Text = withoutPlu == 0
            ? Tr.T($"Выгружено строк: {rows.Count}.", $"Жүктөлгөн саптар: {rows.Count}.",
                   $"Rows exported: {rows.Count}.", $"Dışa aktarılan satır: {rows.Count}.",
                   $"Yuklangan satrlar: {rows.Count}.")
            : Tr.T($"Выгружено строк: {rows.Count}, из них без PLU: {withoutPlu} — им номер нужно задать в карточке товара.",
                   $"Жүктөлгөн саптар: {rows.Count}, алардын PLU'су жоктору: {withoutPlu}.");
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
