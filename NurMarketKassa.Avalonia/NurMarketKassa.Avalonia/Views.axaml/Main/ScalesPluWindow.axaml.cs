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
using Avalonia.Controls.Primitives;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
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

    /// <summary>2026-09-28 (редизайн): марка весов из настроек. Раньше её выбирали радиокнопками и
    /// здесь, и в Настройки → Весы — теперь только там (или «Изменить…» в шапке этого окна).</summary>
    private string _brand = BrandShtrikh;

    /// <summary>Весы, для которых касса только готовит файл (AI). TM-30F с 28.09 (вечер) — нет:
    /// у них прямая отправка.</summary>
    private bool IsFileBrand => _brand == BrandAi;

    /// <summary>2026-09-28: выбрана марка TM-30F (Dahua).</summary>
    private bool IsTm => _brand == BrandTm;

    private bool IsRongta => _brand == BrandRongta;

    /// <summary>2026-09-30: Rongta «напрямую по сети» — касса сама пишет PLU протоколом Dahua (TCP
    /// 4001, имена — RongtaNameCodec), без RLS1000. Проверено на весах владельца по этикеткам.</summary>
    private bool IsRongtaLan => IsRongta && UserPreferences.Instance.RongtaDirectLan;

    /// <summary>Весы, которые касса пишет строками «!0V» (TM-30F и Rongta напрямую).</summary>
    private bool IsDahuaWire => IsTm || IsRongtaLan;

    /// <summary>Rongta показывает цену с двумя знаками (1234 → 12,34 на весах владельца, 30.09).</summary>
    private const int RongtaPricePoint = 2;

    /// <summary>Горячих кнопок у Rongta RLS1000/1100: 112 на клавиатуре × 2 уровня (руководство весов).</summary>
    private const int RongtaHotkeyCount = 224;

    /// <summary>Штрих-ПРИНТ отправляется напрямую по сети (а не через сервер NurCRM).</summary>
    private static bool ShtrikhDirect => UserPreferences.Instance.ShtrikhDirectLan;

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
        // 2026-09-28: окно 1180×720 не должно вылезать за экран 1024×768.
        // 2026-09-29 (сенсорный монитор клиента): по экрану кассы, а не экрану покупателя, и с
        // уменьшением минимального размера, если экран меньше него.
        this.FitToKassaScreen();

        _sendButtonDefaultText = SendButton.Content;
        SearchBox.Watermark = Tr.T("Поиск: название, PLU или код", "Издөө: аталышы, PLU же код",
            "Search: name, PLU or code", "Ara: ad, PLU veya kod", "Qidirish: nomi, PLU yoki kod");

        _brand = ScaleUi.NormalizeBrand(UserPreferences.Instance.ScaleBrand);
        // 2026-09-30: «Показать» и «Лист кнопок» (ScalesPluWindow.KeySheet.cs) — до загрузки строк.
        InitKeySheetControls();
        FillProfileCombo();
        ApplyBrandVisibility();
        LoadRows();
        ApplyResponsiveLayout();
        // 2026-09-29: первый запуск после обновления — закрепить номера, которые уже на весах.
        _ = PinExistingNumbersOnceAsync();
    }

    /// <summary>2026-09-28: марку могли поменять окна настроек весов («Загрузка товаров» →
    /// «Открыть окно «Весы»», «Изменить…») — перечитываем её из настроек.</summary>
    public void ReloadBrandFromPreferences()
    {
        // 2026-09-30: способ/марку сменили во время ожидания «своего сервера» — ожидание больше не нужно.
        _rongtaServerCts?.Cancel();
        _brand = ScaleUi.NormalizeBrand(UserPreferences.Instance.ScaleBrand);
        FillProfileCombo();
        ApplyBrandVisibility();
    }

    /// <summary>2026-09-28 (просьба владельца: квадратные экраны 1024×1024, 800×600): на узком окне
    /// прячем второстепенные колонки (единица, категория), на низком — длинную подсказку марки и
    /// пример штрих-кода, чтобы таблице оставалось место и кнопки не уезжали за край.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (ProductsGrid is null)
            return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        foreach (var column in ProductsGrid.Columns)
        {
            if (Equals(column.Tag, "Unit"))
                column.IsVisible = width >= 1060;
            else if (Equals(column.Tag, "Category"))
                column.IsVisible = width >= 900;
        }
        SubtitleText.IsVisible = height >= 700;
        BarcodeExampleText.MaxLines = height >= 700 ? 0 : 1;
        // Совсем низкое окно (800×600): пример штрих-кода прячем — строка правил и кнопки остаются.
        BarcodeExampleText.MaxHeight = height >= 640 ? double.PositiveInfinity : 0;
        BarcodeExampleText.TextTrimming = height >= 700 ? Avalonia.Media.TextTrimming.None : Avalonia.Media.TextTrimming.CharacterEllipsis;
        // 2026-09-29: совсем низкое окно (1024×768 при 150 % — 472 точки) — полосу «как касса читает
        // этикетки» прячем: иначе таблице товаров остаётся одна строка. Те же настройки — в
        // Настройки → Весы → «Штрих-код: вес / сумма».
        BarcodeStrip.IsVisible = height >= 540;
    }

    private string SelectedBrand => _brand;

    /// <summary>«Изменить…» — марка, адрес и способ отправки в том же виде, что Настройки → Весы
    /// (LabelScaleSetupPanel в отдельном окне).</summary>
    private async void ChangeSetup_Click(object? sender, RoutedEventArgs e)
    {
        RememberProfileSelection();
        await ScaleUi.OpenLabelScaleSetupAsync(this).ConfigureAwait(true);
        ReloadBrandFromPreferences();
        // Могли поменяться категории весов — отмечаем их товары заново.
        ApplyProfileSelection();
        ApplySearch();
        // 2026-09-29: весы могли стать «напрямую по сети» — закрепить номера, если ещё не закреплены.
        _ = PinExistingNumbersOnceAsync();
    }

    /// <summary>«Проверить связь» с весами из шапки — та же проверка, что в настройках.</summary>
    private async void CheckConnection_Click(object? sender, RoutedEventArgs e)
    {
        CheckConnectionButton.IsEnabled = false;
        ConnectionStateText.Text = Tr.T("Проверяю связь с весами…", "Тараза менен байланыш текшерилүүдө…", "Checking the connection to the scale…", "Tartı bağlantısı kontrol ediliyor…", "Tarozi bilan aloqa tekshirilmoqda…");
        try
        {
            await ScaleUi.CheckConnectionAsync(_brand).ConfigureAwait(true);
        }
        finally
        {
            CheckConnectionButton.IsEnabled = true;
            UpdateHeader();
        }
    }

    /// <summary>«Настроить по этикетке…» — мастер и правила префиксов (ScaleBarcodeSetupPanel) в окне.</summary>
    private async void LabelWizard_Click(object? sender, RoutedEventArgs e)
    {
        var panel = new NurMarketKassa.AvaloniaHost.Views.Settings.ScaleBarcodeSetupPanel();
        var done = new Button
        {
            Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"),
            Classes = { "btn-primary" },
            MinWidth = 140,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16) };
        root.Children.Add(new ScrollViewer { Content = panel });
        Grid.SetRow(done, 1);
        root.Children.Add(done);
        var window = new Window
        {
            Title = Tr.T("Штрих-код: вес или сумма", "Штрих-код: салмак же сумма", "Barcode: weight or amount", "Barkod: ağırlık veya tutar", "Shtrix-kod: vazn yoki summa"),
            Width = 900,
            Height = 740,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };
        done.Click += (_, _) => window.Close();
        window.Opened += (_, _) => window.FitToKassaScreen(); // 2026-09-29: по экрану кассы
        await window.ShowDialog(this).ConfigureAwait(true);
        UpdateBarcodeExample();
    }

    /// <summary>Шапка окна: марка, адрес и способ отправки, последняя проверка связи, подсказка.</summary>
    private void UpdateHeader()
    {
        BrandBadgeText.Text = _brand switch
        {
            BrandRongta => "R",
            BrandTm => "TM",
            BrandAi => "AI",
            _ => "Ш",
        };
        BrandNameText.Text = ScaleUi.LabelBrandTitle(_brand);
        BrandAddressText.Text = ScaleUi.AddressLine(_brand);
        CheckConnectionButton.IsVisible = !IsFileBrand;

        // Адрес с опечаткой / из чужой сети — видно сразу, до отправки (владелец ввёл 192.169.0.150).
        var (ipLevel, ipText) = IsFileBrand ? (ScaleIpLevel.None, "") : ScaleUi.CheckScaleIp(ScaleUi.IpOf(_brand));
        var state = ScaleUi.LastCheckOf(_brand);
        if (IsFileBrand)
        {
            ConnectionStateText.Text = "";
            ConnectionStateText.IsVisible = false;
        }
        else if (ipLevel is ScaleIpLevel.Warning or ScaleIpLevel.Error)
        {
            ConnectionStateText.IsVisible = true;
            ConnectionStateText.Text = "⚠ " + ipText;
            ConnectionStateText.Foreground = ScaleUi.ThemeBrush(this, "BrushWarning", Avalonia.Media.Brushes.DarkOrange);
        }
        else
        {
            ConnectionStateText.IsVisible = true;
            ConnectionStateText.Text = ScaleUi.LastCheckText(_brand);
            ConnectionStateText.Foreground = ScaleUi.ThemeBrush(this,
                state is null ? "BrushTextSoft" : state.Ok ? "BrushSuccess" : "BrushWarning", Avalonia.Media.Brushes.Gray);
        }
    }

    /// <summary>Колонка «Код в ШК» и кнопка формата ШК на весах — только при прямой отправке:
    /// серверный путь (send-products) записывает на весы свои данные, и колонка на них не влияет.
    /// Колонки DataGrid не попадают в поля по x:Name — ищем по Tag.</summary>
    private void ApplyDirectLanVisibility()
    {
        var direct = (_brand == BrandShtrikh && ShtrikhDirect) || IsDahuaWire;
        var barcodeColumn = ProductsGrid.Columns.FirstOrDefault(c => Equals(c.Tag, "BarcodeCode"));
        if (barcodeColumn is not null)
            barcodeColumn.IsVisible = direct;
        // Клавиши быстрого доступа (B1h) — только ШТРИХ-ПРИНТ напрямую: у TM-30F, Rongta и сервера
        // NurCRM команды записи клавиш в кассе нет.
        var hotkeyColumn = ProductsGrid.Columns.FirstOrDefault(c => Equals(c.Tag, "Hotkey"));
        if (hotkeyColumn is not null)
            // 2026-09-30: и у весов «!0L» (Rongta напрямую, TM-30F) — клавиши пишутся страницами.
            // 2026-09-30: у Rongta кнопка всегда = PLU: «!0L» весы подтверждают, но раскладку не меняют
            // (проверено на весах владельца: кнопка 1 после «1 → 66» осталась на ячейке 1).
            // Вписанная кнопка у Rongta = новый PLU товара (ScalesPluWindow.Keys.cs, HotkeyEditAsPluAsync).
            hotkeyColumn.IsVisible = (_brand == BrandShtrikh && ShtrikhDirect) || IsDahuaWire;
        // 2026-09-30: у Rongta кнопка открывает «Настройки весов Rongta» → «Штрих-код».
        BarcodeSettingsButton.IsVisible = direct;
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
        UpdateBarcodeRuleText();
        var visible = (!IsRongta || IsRongtaLan) && !IsFileBrand;
        BarcodeExampleText.IsVisible = visible;
        if (!visible || _allRows is not IEnumerable<ScalePluRowVm> rows)
            return;

        var row = rows.FirstOrDefault(r => r.IsSelected) ?? rows.FirstOrDefault();
        if (IsTm)
        {
            UpdateTmBarcodeExample(row);
            return;
        }
        if (IsRongtaLan)
        {
            UpdateRongtaBarcodeExample(row);
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

    /// <summary>2026-09-28 (живой баг «сумма неправильно»): как касса прочтёт этикетки — по префиксам
    /// («20 — вес · 21 — сумма»). Для TM-30F — отдельно префикс этих весов.</summary>
    private void UpdateBarcodeRuleText()
    {
        var rules = string.Join(" · ", ScaleBarcodeRules.PrefixesInUse()
            .Select(p => $"{p} — {ScaleBarcodeRules.RuleWord(p)}"));
        BarcodeRuleText.Text = Tr.T("Касса читает этикетки: ", "Касса этикеткаларды окуйт: ", "The till reads labels as: ", "Kasa etiketleri okur: ", "Kassa yorliqlarni o‘qiydi: ")
                               + rules
                               + Tr.T(". Сумма в чеке не та — «Настроить по этикетке».", ". Чектеги сумма туура эмес болсо — «Этикетка боюнча жөндөө».", ". Wrong amount on the receipt? Use “Set up from a label”.", ". Fişteki tutar yanlışsa — «Etiketten ayarla».", ". Chekdagi summa noto‘g‘ri bo‘lsa — «Yorliq bo‘yicha sozlash».");
    }

    private void BarcodeCode_LostFocus(object? sender, RoutedEventArgs e)
    {
        // 2026-09-29: правка «Код в ШК» запоминается для этих весов (ScalesPluWindow.PinnedPlu.cs).
        if ((sender as Control)?.DataContext is ScalePluRowVm row)
            RememberBarcodeCode(row);
        UpdateBarcodeExample();
    }

    /// <summary>Открывает «Настройки весов Штрих-ПРИНТ» на вкладке «Штрих-код» с примером для
    /// первого отмеченного товара — там формат ШК весов читается, правится и записывается.</summary>
    private async void BarcodeSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (IsRongtaLan)
        {
            // 2026-09-30: Rongta напрямую — «Настройки весов Rongta» на вкладке «Штрих-код».
            var rongtaWindow = new NurMarketKassa.AvaloniaHost.Views.Dialogs.RongtaScaleSettingsWindow();
            rongtaWindow.ShowBarcodeTab();
            await rongtaWindow.ShowDialog(this).ConfigureAwait(true);
            UpdateBarcodeExample();
            return;
        }

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

    // 2026-09-28 (редизайн): галочка «Напрямую по кабелю» и кнопка «Настройки подключения…» ушли
    // из этого окна — способ отправки Штрих-ПРИНТ (через сервер / напрямую), адрес, порт и пароль
    // теперь в Настройки → Весы → «Весы с этикетками» (и «Изменить…» в шапке). Окно подключения
    // ScaleConnectionDialog не удалено, но отсюда больше не открывается.

    /// <summary>2026-09-28: пишет клавиши быстрого доступа ШТРИХ-ПРИНТ для строк, где задана
    /// «Клавиша» (1–120): B1h, функция 01h «Выбрать товар по номеру ПЛУ», значение — номер ПЛУ,
    /// под которым товар только что записан. Одна и та же клавиша у двух товаров — вторая пропускается.
    /// Возвращает строку-итог для статуса («» — клавиш не задано) и все клавиши из колонки
    /// «Клавиша» — их не трогает перевод клавиш за переехавшими товарами (RemapScaleHotkeysAsync).
    /// 2026-09-29: предел — число клавиш ЭТИХ весов (<paramref name="keyCount"/>, Приложение 8
    /// протокола: у ШТРИХ-ПРИНТ М 4.5 их 90, у 4.0–4.4 — 80), а не всегда 120; итог по клавише
    /// дописывается к итогу записи ПЛУ в строке, а не затирает его.</summary>
    private async Task<(string Note, HashSet<int> TableKeys)> WriteShtrikhHotkeysAsync(ShtrikhPrintLanScaleService scale, int keyCount,
        List<string> recordIds, List<(int Plu, string Name)> keyMap, Dictionary<string, ScalePluRowVm> rowsById)
    {
        var tableKeys = _allRows
            .Select(r => int.TryParse((r.HotkeyText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0)
            .Where(k => k > 0)
            .ToHashSet();
        var maxKey = Math.Min(MaxHotkey, keyCount);
        var used = new HashSet<int>();
        int written = 0, failed = 0;
        for (var i = 0; i < recordIds.Count; i++)
        {
            if (!rowsById.TryGetValue(recordIds[i], out var row) || row.StatusError)
                continue;
            var text = (row.HotkeyText ?? "").Trim();
            if (text.Length == 0)
                continue;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key < 1 || key > maxKey)
            {
                row.AppendStatus(" · " + Tr.T($"⚠ клавиша — число 1–{maxKey} (столько клавиш у этих весов)", $"⚠ баскыч — 1–{maxKey} сан (бул таразада ушунча баскыч)",
                    $"⚠ key must be 1–{maxKey} (keys on this scale)", $"⚠ tuş 1–{maxKey} olmalı (bu tartıdaki tuş sayısı)", $"⚠ tugma — 1–{maxKey} son (bu tarozida shuncha tugma)"), RowState.Warning);
                failed++;
                continue;
            }
            if (!used.Add(key))
            {
                row.AppendStatus(" · " + Tr.T($"⚠ клавиша {key} уже занята", $"⚠ {key}-баскыч бош эмес", $"⚠ key {key} is already used", $"⚠ {key}. tuş zaten kullanılıyor", $"⚠ {key}-tugma band"), RowState.Warning);
                failed++;
                continue;
            }
            try
            {
                await scale.SetHotkeyAsync(key, ShtrikhPrintProtocol.HotkeyPluNumber, keyMap[i].Plu, CancellationToken.None).ConfigureAwait(true);
                row.AppendStatus(" · " + Tr.T("клавиша ", "баскыч ", "key ", "tuş ", "tugma ") + key, RowState.Ok);
                written++;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Клавиша {key} (ПЛУ {keyMap[i].Plu}) не записана: {ex.Message}", "SCALES");
                row.AppendStatus(" · ✗ " + Tr.T("клавиша ", "баскыч ", "key ", "tuş ", "tugma ") + key + ": " + ex.Message, RowState.Error);
                failed++;
            }
        }
        if (written == 0 && failed == 0)
            return ("", tableKeys);
        return (Tr.T($" Клавиши: записано {written}", $" Баскычтар: {written} жазылды", $" Keys: {written} written", $" Tuşlar: {written} yazıldı", $" Tugmalar: {written} yozildi")
               + (failed > 0 ? Tr.T($", с ошибкой {failed}.", $", {failed} ката менен.", $", {failed} failed.", $", {failed} hatalı.", $", {failed} xato bilan.") : "."), tableKeys);
    }

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


    /// <summary>Шапка, подсказка и видимость частей окна под выбранную марку и способ отправки.
    /// 2026-09-28: подсказки короче прежних — подробности в настройках марки.</summary>
    private void ApplyBrandVisibility()
    {
        var isRongta = IsRongta;
        var isAi = IsFileBrand;
        // Номера PLU нужны только прямой отправке и серверу Штрих-М (у Rongta — свой файл, у AI — CSV).
        PluStartRow.IsVisible = (!isRongta || IsRongtaLan) && !isAi;
        // 2026-09-29: «Постоянный PLU за товаром» — только там, где номера раздаёт касса (сервер
        // NurCRM нумерует сам, галочка там ничего не меняла).
        SequentialPluCheck.IsVisible = KassaNumbering;
        WebPluButton.IsVisible = KassaNumbering;
        KeysButton.IsVisible = KassaNumbering;
        ApplyDirectLanVisibility();
        UpdateHeader();
        SendButton.Content = isAi ? Tr.T("Сохранить файл для весов", "Файлды тараза үчүн сактоо", "Save file for the scale", "Tartı için dosyayı kaydet", "Tarozi uchun faylni saqlash") : _sendButtonDefaultText;
        // У AI-весов главная кнопка уже сохраняет файл — вторая такая же не нужна.
        SaveFileButton.IsVisible = !isAi;
        SaveFileButton.Content = IsTm
            ? Tr.T("Сохранить файл ▾", "Файлды сактоо ▾", "Save file ▾", "Dosyayı kaydet ▾", "Faylni saqlash ▾")
            : Tr.T("Сохранить CSV", "CSV сактоо", "Save CSV", "CSV kaydet", "CSV saqlash");

        SubtitleText.Text = _brand switch
        {
            // 2026-09-29: номера PLU закреплены за товарами (ScalesPluWindow.PinnedPlu.cs) — «подряд» больше нет.
            BrandTm => Tr.T(
                "Отмеченные товары уйдут прямо на весы по сети. У каждого товара постоянный номер PLU (колонка PLU) — отправки его не меняют; в штрих-код этикетки весы напечатают «Код в ШК». Запасной путь — файл для «Русского масштаба» («Сохранить файл»).",
                "Белгиленген товарлар тармак аркылуу түз таразага кетет. Ар бир товардын туруктуу PLU номери бар (PLU тилкеси) — жөнөтүүлөр аны өзгөртпөйт; этикетканын штрих-кодуна тараза «ШКдагы код» басат. Запас жол — «Русский масштаб» үчүн файл («Файлды сактоо»).",
                "The ticked goods go straight to the scale over the network. Every product has a fixed PLU number (PLU column) that sending does not change; the scale prints the “Code in barcode” into the label barcode. Fallback — a file for “Russian Scale” (“Save file”).",
                "İşaretli ürünler ağ üzerinden doğrudan tartıya gider. Her ürünün sabit bir PLU numarası var (PLU sütunu) — gönderimler onu değiştirmez; tartı etiket barkoduna «Barkoddaki kod»u basar. Yedek yol — «Русский масштаб» için dosya («Dosyayı kaydet»).",
                "Belgilangan tovarlar tarmoq orqali to‘g‘ridan-to‘g‘ri taroziga ketadi. Har bir tovarning doimiy PLU raqami bor (PLU ustuni) — yuborishlar uni o‘zgartirmaydi; tarozi yorliq shtrix-kodiga «Shtrix-koddagi kod»ni chop etadi. Zaxira yo‘l — «Русский масштаб» uchun fayl («Faylni saqlash»)."),
            BrandAi => Tr.T(
                "Касса сохранит файл (PLU, название, единица, цена) — загрузите его программой весов. Прямой заливки нет: у AI-весов нет общего протокола.",
                "Касса файлды сактайт (PLU, аталышы, бирдиги, баасы) — аны тараза программасы менен жүктөңүз. Түз жүктөө жок: AI таразанын жалпы протоколу жок.",
                "The till saves a file (PLU, name, unit, price) — load it with the scale's software. There is no direct upload: AI scales have no common protocol.",
                "Kasa bir dosya kaydeder (PLU, ad, birim, fiyat) — onu tartı programıyla yükleyin. Doğrudan yükleme yok: AI tartıların ortak protokolü yok.",
                "Kassa fayl saqlaydi (PLU, nomi, birligi, narxi) — uni tarozi dasturi bilan yuklang. To‘g‘ridan-to‘g‘ri yuklash yo‘q: AI tarozilarning umumiy protokoli yo‘q."),
            BrandRongta when IsRongtaLan => Tr.T(
                "Отмеченные товары уйдут прямо на весы Rongta по сети, без RLS1000. У каждого товара постоянный номер PLU (колонка PLU); в штрих-код этикетки весы напечатают «Код в ШК». Буквы, которых весы не печатают, касса заменит похожими (например, «я» в конце — «Я»).",
                "Белгиленген товарлар RLS1000'сиз тармак аркылуу түз Rongta таразасына кетет. Ар бир товардын туруктуу PLU номери бар (PLU тилкеси); этикетканын штрих-кодуна тараза «ШКдагы код» басат. Тараза баса албаган тамгаларды касса окшошуна алмаштырат (мисалы, аягындагы «я» — «Я»).",
                "The ticked goods go straight to the Rongta scale over the network, without RLS1000. Every product has a fixed PLU number (PLU column); the scale prints the “Code in barcode” into the label barcode. Letters the scale cannot print are replaced with similar ones (e.g. a final «я» becomes «Я»).",
                "İşaretli ürünler RLS1000 olmadan ağ üzerinden doğrudan Rongta tartıya gider. Her ürünün sabit bir PLU numarası var (PLU sütunu); tartı etiket barkoduna «Barkoddaki kod»u basar. Tartının basamadığı harfleri kasa benzerleriyle değiştirir (ör. sondaki «я» → «Я»).",
                "Belgilangan tovarlar RLS1000'siz tarmoq orqali to‘g‘ridan-to‘g‘ri Rongta taroziga ketadi. Har bir tovarning doimiy PLU raqami bor (PLU ustuni); tarozi yorliq shtrix-kodiga «Shtrix-koddagi kod»ni chop etadi. Tarozi chop eta olmaydigan harflarni kassa o‘xshashiga almashtiradi (masalan, oxiridagi «я» — «Я»)."),
            BrandRongta => Tr.T(
                "На весы уйдёт весь список весовых товаров (галочки здесь не действуют) — через программу RLS1000.",
                "Таразага бардык салмактуу товарлардын тизмеси кетет (бул жердеги белгилер эске алынбайт) — RLS1000 программасы аркылуу.",
                "The whole list of weighed goods goes to the scale (the ticks here are ignored) — via the RLS1000 software.",
                "Tartıya tüm tartılı ürün listesi gider (buradaki işaretler dikkate alınmaz) — RLS1000 programı ile.",
                "Taroziga barcha vaznli tovarlar ro‘yxati ketadi (bu yerdagi belgilar hisobga olinmaydi) — RLS1000 dasturi orqali."),
            _ => ShtrikhDirect
                ? Tr.T(
                    "Касса сама отправит отмеченные товары на весы по сети. У каждого товара постоянный номер PLU (колонка PLU): добавление товаров, поиск и сортировка его не меняют. Клавиши весов вызывают товар по номеру PLU — клавишу товару можно задать в колонке «Клавиша».",
                    "Касса белгиленген товарларды таразага тармак аркылуу өзү жөнөтөт. Ар бир товардын туруктуу PLU номери бар (PLU тилкеси): товар кошуу, издөө жана иреттөө аны өзгөртпөйт. Тараза баскычтары товарды PLU номери боюнча чакырат — товарга баскычты «Баскыч» тилкесинде берсе болот.",
                    "The till sends the ticked goods to the scale over the network itself. Every product has a fixed PLU number (PLU column): adding products, search and sorting do not change it. Scale keys call a product by its PLU number — you can give a product a key in the “Key” column.",
                    "Kasa işaretli ürünleri tartıya ağ üzerinden kendisi gönderir. Her ürünün sabit bir PLU numarası var (PLU sütunu): ürün eklemek, arama ve sıralama onu değiştirmez. Tartı tuşları ürünü PLU numarasıyla çağırır — ürüne «Tuş» sütununda tuş verilebilir.",
                    "Kassa belgilangan tovarlarni taroziga tarmoq orqali o‘zi yuboradi. Har bir tovarning doimiy PLU raqami bor (PLU ustuni): tovar qo‘shish, qidiruv va saralash uni o‘zgartirmaydi. Tarozi tugmalari tovarni PLU raqami bo‘yicha chaqiradi — tovarga tugmani «Tugma» ustunida berish mumkin.")
                : Tr.T(
                    "Отмеченные товары уйдут на весы через сервер NurCRM — он сам передаёт данные весам по локальной сети.",
                    "Белгиленген товарлар NurCRM сервери аркылуу таразага кетет — ал маалыматты таразага жергиликтүү тармак аркылуу өзү берет.",
                    "The ticked goods go to the scale via the NurCRM server — it passes the data to the scale over the local network itself.",
                    "İşaretli ürünler NurCRM sunucusu üzerinden tartıya gider — verileri tartıya yerel ağ üzerinden kendisi iletir.",
                    "Belgilangan tovarlar NurCRM serveri orqali taroziga ketadi — u ma’lumotni taroziga mahalliy tarmoq orqali o‘zi uzatadi."),
        };
        // 2026-09-29: колонка PLU зависит от марки и способа отправки (кто нумерует ячейки).
        RefreshPluNumbers();
    }

    /// <summary>«Сохранить файл…»: CSV для любых весов; у TM-30F — выбор CSV или DIGI_TOP2000.</summary>
    private void SaveFile_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsTm)
        {
            ExportCsv_Click(sender, e);
            return;
        }

        var csv = new MenuItem { Header = Tr.T("CSV (PLU; название; единица; цена)", "CSV (PLU; аталышы; бирдиги; баасы)", "CSV (PLU; name; unit; price)", "CSV (PLU; ad; birim; fiyat)", "CSV (PLU; nomi; birligi; narxi)") };
        csv.Click += ExportCsv_Click;
        var digi = new MenuItem { Header = Tr.T("Файл для «Русского масштаба» (DIGI_TOP2000)", "«Русский масштаб» үчүн файл (DIGI_TOP2000)", "File for “Russian Scale” (DIGI_TOP2000)", "«Русский масштаб» için dosya (DIGI_TOP2000)", "«Русский масштаб» uchun fayl (DIGI_TOP2000)") };
        digi.Click += TmExport_Click;
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.Items.Add(digi);
        flyout.Items.Add(csv);
        flyout.ShowAt(SaveFileButton);
    }

    // ------------------------------------------------------------------ ход отправки по строкам

    private void ClearRowStatuses()
    {
        foreach (var row in _allRows)
            row.SetStatus("", RowState.None);
    }

    private void SetProgress(int done, int total)
    {
        SendProgress.IsVisible = total > 0;
        SendProgress.Maximum = Math.Max(1, total);
        SendProgress.Value = Math.Clamp(done, 0, Math.Max(1, total));
    }

    private void HideProgress() => SendProgress.IsVisible = false;

    private static string SentText() => Tr.T("✓ отправлено", "✓ жөнөтүлдү", "✓ sent", "✓ gönderildi", "✓ yuborildi");
    private static string NotSentText() => Tr.T("не отправлено", "жөнөтүлгөн жок", "not sent", "gönderilmedi", "yuborilmadi");

    // 2026-09-29: «Обновить с сервера» — настоящая загрузка каталога с NurCRM (ScalesPluWindow.PinnedPlu.cs).
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshFromServerAsync().ConfigureAwait(true);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void LoadRows()
    {
        var profile = LabelScaleStore.Active;
        // 2026-09-30: по умолчанию — только весовые на сайте (ScalesPluWindow.KeySheet.cs, «Показать»).
        var anyMustWeigh = NurMarketKassa.Services.CatalogCacheService.Products.Any(p => p.MustWeigh);
        var rows = NurMarketKassa.Services.CatalogCacheService.Products
            .Where(p => LoadsProduct(p, anyMustWeigh))
            .OrderBy(p => p.Title, System.StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ScalePluRowVm
            {
                Id = p.Id,
                Name = p.Title,
                // 2026-09-29: колонка PLU заполняется RefreshPluNumbers (закреплённый номер ячейки
                // или PLU из карточки — смотря кто нумерует); здесь — только PLU карточки.
                CatalogPlu = p.Plu,
                PriceLine = p.PriceLine,
                Unit = p.Unit ?? "",
                Category = (p.Category ?? "").Trim(),
                Price = NurMarketKassa.Services.LocalCartService.ParsePrice(p.PriceLine),
                DefaultCode = DefaultBarcodeCode(p),
                // 2026-09-29: «Код в ШК», исправленный владельцем для этих весов, не теряется.
                BarcodeCode = profile.BarcodeCodes.TryGetValue(p.Id, out var savedCode) ? savedCode : DefaultBarcodeCode(p),
                HotkeyText = profile.Hotkeys.TryGetValue(p.Id, out var key) ? key.ToString(CultureInfo.InvariantCulture) : "",
                IsSelected = true,
            })
            .ToList();

        _allRows = rows;
        foreach (var row in rows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ScalePluRowVm.IsSelected))
                {
                    UpdateSelectionCount();
                    // 2026-09-29: отмеченному товару без номера — показать, какой номер он получит.
                    QueuePluRefresh();
                }
            };
        ApplyProfileSelection();
        FillCategoryFilter();
        ApplySearch();
        StatusText.Text = "";
        UpdateBarcodeExample();
    }

    /// <summary>Все весовые товары. Таблица показывает только найденные поиском, а отправка,
    /// экспорт и «Код в ШК» работают по всему списку: отмеченный, но скрытый поиском товар
    /// тоже уйдёт на весы (2026-09-28, просьба владельца — поиск в окне «Весы»).</summary>
    private List<ScalePluRowVm> _allRows = new();

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    private void CategoryCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplySearch();

    /// <summary>Выбранная категория фильтра; null — все.</summary>
    private string? SelectedCategory => CategoryCombo.SelectedItem is ComboBoxItem { Tag: string c } ? c : null;

    /// <summary>2026-09-28: фильтр по категории — категории весовых товаров каталога.</summary>
    private void FillCategoryFilter()
    {
        var previous = SelectedCategory;
        var items = new List<ComboBoxItem>
        {
            new() { Content = Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar") },
        };
        foreach (var category in _allRows.Select(r => r.Category).Where(c => c.Length > 0)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = _allRows.Count(r => string.Equals(r.Category, category, StringComparison.CurrentCultureIgnoreCase));
            items.Add(new ComboBoxItem { Content = $"{category} ({count})", Tag = category });
        }
        CategoryCombo.ItemsSource = items;
        CategoryCombo.SelectedItem = items.FirstOrDefault(i => previous is not null && Equals(i.Tag, previous)) ?? items[0];
    }

    /// <summary>Поиск по названию, PLU и коду в ШК; несколько слов — все должны найтись. Плюс фильтр
    /// по категории (2026-09-28).</summary>
    private void ApplySearch()
    {
        var words = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var category = SelectedCategory;
        var shown = _allRows
            .Where(r => category is null || string.Equals(r.Category, category, StringComparison.CurrentCultureIgnoreCase))
            .Where(PassesShowFilter)
            .Where(r => words.All(w =>
                (r.Name ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(r.PluText, w, StringComparison.Ordinal)
                || (r.BarcodeCode ?? "").Contains(w, StringComparison.Ordinal)))
            .ToList();

        ProductsGrid.ItemsSource = shown;
        EmptyText.IsVisible = shown.Count == 0;
        SearchCountText.Tag = words.Length == 0 && category is null && _showMode is ShowMode.SiteWeighted or ShowMode.AllKg
            ? ""
            : Tr.T($"Найдено: {shown.Count} из {_allRows.Count}", $"Табылды: {_allRows.Count} ичинен {shown.Count}",
                   $"Found: {shown.Count} of {_allRows.Count}", $"Bulunan: {shown.Count} / {_allRows.Count}",
                   $"Topildi: {_allRows.Count} dan {shown.Count}");
        UpdateSelectionCount();
    }

    /// <summary>«Отмечено: 12 из 40» (и «найдено», если есть поиск/фильтр).</summary>
    private void UpdateSelectionCount()
    {
        var selected = _allRows.Count(r => r.IsSelected);
        var found = SearchCountText.Tag as string;
        SearchCountText.Text = (string.IsNullOrEmpty(found) ? "" : found + " · ")
            + Tr.T($"Отмечено: {selected} из {_allRows.Count}", $"Белгиленди: {_allRows.Count} ичинен {selected}",
                   $"Ticked: {selected} of {_allRows.Count}", $"İşaretli: {selected} / {_allRows.Count}",
                   $"Belgilangan: {_allRows.Count} dan {selected}");
    }

    /// <summary>2026-09-28: товары выбранных весов — их категории; если категорий нет — отмеченные
    /// вручную при прошлой отправке на эти весы; если и их нет — все весовые.</summary>
    private void ApplyProfileSelection()
    {
        var profile = LabelScaleStore.Active;
        if (profile.Categories.Count > 0)
        {
            var set = new HashSet<string>(profile.Categories, StringComparer.CurrentCultureIgnoreCase);
            foreach (var row in _allRows)
                row.IsSelected = set.Contains(row.Category);
        }
        else if (profile.ProductIds.Count > 0)
        {
            var ids = new HashSet<string>(profile.ProductIds, StringComparer.Ordinal);
            foreach (var row in _allRows)
                row.IsSelected = ids.Contains(row.Id);
        }
        else
        {
            foreach (var row in _allRows)
                row.IsSelected = true;
        }
        foreach (var row in _allRows)
        {
            row.HotkeyText = profile.Hotkeys.TryGetValue(row.Id, out var key) ? key.ToString(CultureInfo.InvariantCulture) : "";
            // 2026-09-29: «Код в ШК» и номера PLU у каждых весов свои.
            row.BarcodeCode = profile.BarcodeCodes.TryGetValue(row.Id, out var code) ? code : row.DefaultCode;
        }
        SequentialPluCheck.IsChecked = !profile.PluFromCatalog;
        RefreshPluNumbers();
    }

    /// <summary>Запоминает для выбранных весов отмеченные товары (если категорий нет) и клавиши.</summary>
    private void RememberProfileSelection()
    {
        var profile = LabelScaleStore.Active;
        if (profile.Categories.Count == 0)
            profile.ProductIds = _allRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        profile.Hotkeys = _allRows
            .Where(r => int.TryParse((r.HotkeyText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k is >= 1 and <= MaxHotkey)
            .ToDictionary(r => r.Id, r => int.Parse(r.HotkeyText.Trim(), CultureInfo.InvariantCulture));
        LabelScaleStore.Save();
    }

    /// <summary>Клавиш быстрого доступа у ШТРИХ-ПРИНТ — 120 (у исполнения с большой клавиатурой).</summary>
    private const int MaxHotkey = 120;

    // ------------------------------------------------------------------ выбор весов (несколько весов)

    private bool _fillingProfiles;

    private void FillProfileCombo()
    {
        _fillingProfiles = true;
        try
        {
            var items = LabelScaleStore.All
                .Select(p => new ComboBoxItem { Content = $"{p.Name} · {ScaleUi.LabelBrandTitle(p.Brand)}", Tag = p.Id })
                .ToList();
            ScaleProfileCombo.ItemsSource = items;
            ScaleProfileCombo.SelectedItem = items.FirstOrDefault(i => Equals(i.Tag, LabelScaleStore.Active.Id)) ?? items.FirstOrDefault();
            ScaleProfileCombo.IsVisible = items.Count > 1;
        }
        finally
        {
            _fillingProfiles = false;
        }
    }

    private void ScaleProfileCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingProfiles || ScaleProfileCombo.SelectedItem is not ComboBoxItem { Tag: string id })
            return;
        var profile = LabelScaleStore.Find(id);
        if (profile is null || profile.Id == LabelScaleStore.Active.Id)
            return;
        LabelScaleStore.Activate(profile);
        ReloadBrandFromPreferences();
        ClearRowStatuses();
        ApplyProfileSelection();
        ApplySearch();
        // 2026-09-29: у каждых весов свои закреплённые номера — у новых весов закрепить при первом открытии.
        _ = PinExistingNumbersOnceAsync();
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
        // 2026-09-30: идёт ожидание программы весов («свой сервер») — кнопка «Остановить».
        if (_rongtaServerCts is not null)
        {
            _rongtaServerCts.Cancel();
            return;
        }

        // 2026-09-28: результат по строкам — с чистого листа на каждую отправку.
        ClearRowStatuses();
        // Несколько весов: запоминаем для этих весов отмеченные товары и клавиши.
        RememberProfileSelection();

        // 2026-09-30 (владелец): PLU синхронизируются с сайтом — товарам без PLU касса присваивает его
        // на сайте, номер ячейки = PLU с сайта. Только там, где номера раздаёт касса.
        if (KassaNumbering && !OfflineModeHelper.UseLocalOperations)
        {
            SendButton.IsEnabled = false;
            try
            {
                await SyncWebPluAsync(ask: false).ConfigureAwait(true);
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        if (IsDahuaWire)
        {
            await SendToTmAsync().ConfigureAwait(true);
            return;
        }

        if (IsFileBrand)
        {
            // Для AI-весов «отправить» — это подготовить файл: заливать напрямую пока нечем.
            await ExportPluCsvAsync("ai-scale-plu").ConfigureAwait(true);
            return;
        }

        if (IsRongta)
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

        // 2026-09-28: способ отправки выбирается в Настройки → Весы («Через сервер NurCRM» /
        // «Напрямую по сети»), а не галочкой в этом окне.
        if (ShtrikhDirect)
        {
            await SendToShtrikhOverLanAsync(selectedIds, pluStart).ConfigureAwait(true);
            return;
        }

        SendButton.IsEnabled = false;
        SetProgress(0, 1);
        StatusText.Text = Tr.T("Отправка…", "Жиберилүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        var selectedRows = rows.Where(r => r.IsSelected).ToList();
        try
        {
            await App.CatalogApi.SendProductsToScaleAsync(pluStart, selectedIds, CancellationToken.None).ConfigureAwait(true);
            SetProgress(1, 1);
            foreach (var row in selectedRows)
                row.SetStatus(Tr.T("✓ передано серверу", "✓ серверге берилди", "✓ passed to the server", "✓ sunucuya iletildi", "✓ serverga uzatildi"), RowState.Ok);
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
            foreach (var row in selectedRows)
                row.SetStatus(NotSentText(), RowState.Error);
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            HideProgress();
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>Прямая выгрузка на весы по витой паре, без участия сервера NurCRM.
    ///
    /// Положение десятичной точки читаем С САМИХ ВЕСОВ и по нему переводим цену в МДЕ: если
    /// весы настроены на 0 знаков, а прислать им тыйыны — все цены окажутся в 100 раз больше.
    ///
    /// 2026-09-29: номер ПЛУ — закреплённый за товаром (ScalesPluWindow.PinnedPlu.cs), в режиме
    /// «PLU из карточки» — PLU карточки; код товара — «Код в ШК». После записи клавиши весов,
    /// вызывавшие старый номер/код переехавшего товара, переводятся на новый, старая ячейка чистится.</summary>
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

            if (status.ProductTableSize > 0)
                _shtrikhTableSize = status.ProductTableSize;

            var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
            // 2026-09-29 (живой баг клиента: «при каждой отправке ПЛУ меняются»): номер ячейки —
            // ЗАКРЕПЛЁННЫЙ за товаром (PinNumbersForSend), а не «подряд по текущему порядку
            // списка», как было с 2026-09-23. Клавиши весов вызывают ячейку по номеру, поэтому
            // сдвиг номеров при добавлении товара или отправке части списка подменял товары под
            // клавишами. «Код в ШК» и совпадения номеров/кодов проверяются до записи (CheckSendBatch).
            var numbers = PinNumbersForSend(selectedIds);
            var (codes, problems) = CheckSendBatch(selectedIds, numbers, maxCode: 999999);
            var records = new List<ShtrikhPluRecord>();
            // Что на какой клавише окажется — показываем кассиру: панель подписывают руками,
            // и без этого списка непонятно, какую наклейку куда клеить.
            var keyMap = new List<(int Plu, string Name)>();
            // 2026-09-28: id товара для каждой записи — чтобы показать результат по строкам.
            var recordIds = new List<string>();
            foreach (var id in selectedIds)
            {
                if (!byId.TryGetValue(id, out var product) || problems.ContainsKey(id) || !numbers.TryGetValue(id, out var plu))
                    continue;

                keyMap.Add((plu, product.Title));
                recordIds.Add(id);

                // 2026-09-28: «Код товара» записи ПЛУ — это число, которое весы печатают в
                // весовом штрих-коде (Т в структуре ШК), и по нему касса ищет товар при скане:
                // значение колонки «Код в ШК» (по умолчанию PLU / «Код товара» из карточки — см.
                // DefaultBarcodeCode); пустое или неверное — номер ячейки (CheckSendBatch).
                records.Add(ShtrikhPrintLanScaleService.CreateRecord(
                    pluNumber: plu,
                    productCode: (int)codes[id],
                    name: product.Title,
                    priceSom: (decimal)LocalCartService.ParsePrice(product.PriceLine),
                    decimalPointDigits: status.DecimalPointDigits,
                    isPiece: !product.IsWeighted));
            }

            if (records.Count == 0)
            {
                StatusText.Text = problems.Count > 0
                    ? Tr.T("Ничего не отправлено — исправьте строки с ⚠ (номер PLU или «Код в ШК»).", "Эч нерсе жөнөтүлгөн жок — ⚠ белгиси бар саптарды оңдоңуз (PLU номери же «ШКдагы код»).",
                        "Nothing was sent — fix the rows marked ⚠ (PLU number or “Barcode code”).", "Hiçbir şey gönderilmedi — ⚠ işaretli satırları düzeltin (PLU numarası veya «Barkod kodu»).",
                        "Hech narsa yuborilmadi — ⚠ belgili qatorlarni tuzating (PLU raqami yoki «ShKdagi kod»).")
                    : Tr.T("Не удалось собрать данные для выгрузки — обновите каталог.", "Жүктөө үчүн маалыматтарды чогултуу мүмкүн болгон жок — каталогду жаңыртыңыз.", "Could not prepare the data for upload — refresh the catalog.", "Tartıya gönderilecek veriler hazırlanamadı — kataloğu güncelleyin.", "Yuklash uchun ma'lumotlarni to'plab bo'lmadi — katalogni yangilang.");
                return;
            }

            var progress = new Progress<ShtrikhUploadProgress>(p =>
            {
                SetProgress(p.Done, p.Total);
                StatusText.Text = Tr.T($"{p.Stage}: {p.Done} из {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…", $"{p.Stage}: {p.Done} of {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…", $"{p.Stage}: {p.Done} / {p.Total}…");
            });

            var result = await scale.UploadPlusAsync(records, progress, CancellationToken.None).ConfigureAwait(true);

            // 2026-09-28: результат по строкам. Ошибки драйвер пишет с названием товара в «»;
            // без ошибок и отказов — строка принята.
            var rowsById = (_allRows ?? new List<ScalePluRowVm>()).ToDictionary(r => r.Id);
            // 2026-09-29: что весы точно приняли — для «что на весах» (старые ячейки, клавиши).
            var written = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
            for (var i = 0; i < recordIds.Count; i++)
            {
                if (!rowsById.TryGetValue(recordIds[i], out var row))
                    continue;
                // Драйвер пишет ошибку с номером ПЛУ и названием (обрезанным до 28 байт, как на весах).
                var name1 = records[i].Name1;
                var error = result.Errors.FirstOrDefault(er => er.Contains($"«{name1}»", StringComparison.Ordinal)
                                                               || er.StartsWith($"ПЛУ {keyMap[i].Plu} «", StringComparison.Ordinal));
                if (error is not null)
                    row.SetStatus("✗ " + error, RowState.Error);
                else if (result.Failed == 0)
                {
                    row.SetStatus(SentText() + $" · PLU {keyMap[i].Plu}", RowState.Ok);
                    written[recordIds[i]] = new ScaleSentPlu
                    {
                        Plu = keyMap[i].Plu,
                        Code = records[i].ProductCode.ToString(CultureInfo.InvariantCulture),
                        Name = keyMap[i].Name,
                    };
                }
                else
                    row.SetStatus(Tr.T("не подтверждено", "ырасталган жок", "not confirmed", "onaylanmadı", "tasdiqlanmadi"), RowState.Warning);
            }

            // 2026-09-29 (баг владельца «если менять ПЛУ, назначения клавиш не работают»): что
            // переехало с прошлой отправки — номер (владелец исправил PLU) или код в ШК.
            var profile = LabelScaleStore.Active;
            var plan = ScalePluPlanner.PlanMoves(profile.SentPlus, written);
            var keyCount = status.HotkeyCount > 0 ? Math.Min(status.HotkeyCount, 255) : MaxHotkey;

            // 2026-09-28 (просьба владельца «товары на клавиши весов»): для строк с номером клавиши
            // пишем клавишу быстрого доступа командой B1h «Выбрать товар по номеру ПЛУ» (код функции
            // 01h, байты сверены с драйвером Штрих-М — см. ShtrikhPrintProtocol.HotkeyPluNumber).
            // 2026-09-29: и переводим на новый номер клавиши, запрограммированные на самих весах,
            // которые вызывали старый номер переехавшего товара; затем чистим его старую ячейку.
            var (hotkeyNote, tableKeys) = await WriteShtrikhHotkeysAsync(scale, keyCount, recordIds, keyMap, rowsById).ConfigureAwait(true);
            var (keysMoved, keysFailed) = await RemapScaleHotkeysAsync(scale, keyCount, plan, tableKeys, rowsById).ConfigureAwait(true);
            var cleared = await ClearOldShtrikhSlotsAsync(scale, plan, profile.SentPlus, rowsById).ConfigureAwait(true);
            RememberSent(written, cleared);

            StatusText.Text = result.Ok
                ? Tr.T($"Выгружено на весы: {result.Sent}. У каждого товара свой постоянный номер PLU — см. колонку PLU.",
                    $"Таразага жүктөлдү: {result.Sent}. Ар бир товардын өз туруктуу PLU номери бар — PLU тилкесин караңыз.",
                    $"Uploaded to the scale: {result.Sent}. Every product keeps its own fixed PLU number — see the PLU column.",
                    $"Tartıya gönderildi: {result.Sent}. Her ürünün kendi sabit PLU numarası var — PLU sütununa bakın.",
                    $"Taroziga yuklandi: {result.Sent}. Har bir tovarning o‘z doimiy PLU raqami bor — PLU ustuniga qarang.")
                : Tr.T($"Выгружено: {result.Sent}, с ошибками: {result.Failed}. ",
                    $"Жүктөлдү: {result.Sent}, ийгиликсиз: {result.Failed}. ",
                    $"Uploaded: {result.Sent}, failed: {result.Failed}. ",
                    $"Gönderildi: {result.Sent}, hatalı: {result.Failed}. ",
                    $"Yuklandi: {result.Sent}, xatolik bilan: {result.Failed}. ") + string.Join(" · ", result.Errors.Take(3));

            StatusText.Text += hotkeyNote;
            if (keysMoved > 0 || keysFailed > 0 || plan.PluMoves.Count > 0)
            {
                StatusText.Text += Tr.T($" Переехало товаров: {plan.PluMoves.Count}, клавиш весов переведено: {keysMoved}, старых ячеек очищено: {cleared.Count}.",
                                        $" Көчкөн товарлар: {plan.PluMoves.Count}, которулган тараза баскычтары: {keysMoved}, тазаланган эски уячалар: {cleared.Count}.",
                                        $" Products moved: {plan.PluMoves.Count}, scale keys moved: {keysMoved}, old slots cleared: {cleared.Count}.",
                                        $" Taşınan ürün: {plan.PluMoves.Count}, taşınan tartı tuşu: {keysMoved}, temizlenen eski hücre: {cleared.Count}.",
                                        $" Ko‘chgan tovarlar: {plan.PluMoves.Count}, o‘tkazilgan tarozi tugmalari: {keysMoved}, tozalangan eski kataklar: {cleared.Count}.")
                                  + (keysFailed > 0
                                      ? Tr.T($" Клавиш с ошибкой: {keysFailed} — см. строки.", $" Ката менен баскычтар: {keysFailed} — саптарды караңыз.", $" Keys failed: {keysFailed} — see the rows.",
                                             $" Hatalı tuş: {keysFailed} — satırlara bakın.", $" Xato bilan tugmalar: {keysFailed} — qatorlarga qarang.")
                                      : "");
            }
            if (problems.Count > 0)
                StatusText.Text += Tr.T($" Не отправлено (⚠ в строках): {problems.Count}.", $" Жөнөтүлгөн жок (саптарда ⚠): {problems.Count}.", $" Not sent (⚠ in rows): {problems.Count}.",
                                        $" Gönderilmedi (satırlarda ⚠): {problems.Count}.", $" Yuborilmadi (qatorlarda ⚠): {problems.Count}.");

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
            foreach (var row in _allRows.Where(r => r.IsSelected))
                row.SetStatus(NotSentText(), RowState.Error);
            StatusText.Text = ex.Message;
        }
        finally
        {
            HideProgress();
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

    /// <summary>2026-09-30: пример этикетки Rongta (напрямую) для первого отмеченного товара: тип ШК
    /// весов и отдел/префикс — из «Настроек весов Rongta» → «Штрих-код», код — «Код в ШК», сверка —
    /// разбором кассы. Сам тип меняется только на весах.</summary>
    private void UpdateRongtaBarcodeExample(ScalePluRowVm? row)
    {
        var prefs = UserPreferences.Instance;
        var type = RongtaBarcodeFormat.Find(prefs.RongtaBarcodeType) ?? RongtaBarcodeFormat.Find(2)!;
        var code = row is not null && long.TryParse(row.BarcodeCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        const int grams = 392;
        var amount = Math.Round((decimal)(row?.Price ?? 100) * grams / 1000m, 2, MidpointRounding.AwayFromZero);
        var sample = RongtaBarcodeFormat.BuildSample(type, prefs.RongtaLanBarcodePrefix, code, grams, amount);
        var name = row is null ? "" : RongtaNameCodec.Preview(row.Name);
        var head = Tr.T($"Этикетка Rongta для «{name}» (0,392 кг): {sample ?? "—"} — тип {type.Type:00} ({type.Pattern}), отдел {prefs.RongtaLanBarcodePrefix:00}. ",
            $"«{name}» үчүн Rongta этикеткасы (0,392 кг): {sample ?? "—"} — {type.Type:00} түрү ({type.Pattern}), бөлүм {prefs.RongtaLanBarcodePrefix:00}. ",
            $"Rongta label for “{name}” (0.392 kg): {sample ?? "—"} — type {type.Type:00} ({type.Pattern}), department {prefs.RongtaLanBarcodePrefix:00}. ",
            $"“{name}” için Rongta etiketi (0,392 kg): {sample ?? "—"} — tür {type.Type:00} ({type.Pattern}), reyon {prefs.RongtaLanBarcodePrefix:00}. ",
            $"«{name}» uchun Rongta yorlig‘i (0,392 kg): {sample ?? "—"} — {type.Type:00} turi ({type.Pattern}), bo‘lim {prefs.RongtaLanBarcodePrefix:00}. ");
        if (sample is null || type.Value == RongtaBarcodeFormat.ValueKind.None)
        {
            BarcodeExampleText.Text = head + "⚠ " + Tr.T("в этом типе нет веса/суммы, которые касса может прочесть — выберите другой тип на весах.",
                "бул түрдө касса окуй турган салмак/сумма жок — таразада башка түр тандаңыз.",
                "this type has no weight/amount the till can read — choose another type on the scale.",
                "bu türde kasanın okuyabileceği ağırlık/tutar yok — tartıda başka tür seçin.",
                "bu turda kassa o‘qiy oladigan vazn/summa yo‘q — tarozida boshqa tur tanlang.");
            return;
        }
        var (ok, verdict) = NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.VerifyWithKassa(sample, code, type.Value == RongtaBarcodeFormat.ValueKind.Weight, grams, amount);
        BarcodeExampleText.Text = head + (ok ? "✓ " : "⚠ ") + verdict;
    }

    /// <summary>Собирает записи PLU для TM-30F так же, как для Штрих-М по LAN: номер — закреплённый
    /// за товаром (2026-09-29; раньше — подряд от «Начальный PLU»), в режиме «PLU из карточки» — PLU
    /// товара; «Код товара» — колонка «Код в штрих-коде» (иначе номер PLU), тип — весовой/штучный из
    /// карточки, префикс ШК и срок годности — из настроек.</summary>
    private (List<DahuaTmPlu> Records, List<string> Problems, List<(int Plu, string Name)> KeyMap) BuildTmRecords(List<string> selectedIds, int pluStart)
    {
        var prefs = UserPreferences.Instance;
        var byId = NurMarketKassa.Services.CatalogCacheService.Products.ToDictionary(p => p.Id);
        var decimals = DahuaPricePoint;
        // 2026-09-30: у Rongta напрямую — свои префикс ШК и срок годности (окно «Настройки весов Rongta»).
        var shelfLife = IsRongtaLan ? prefs.RongtaLanShelfLifeDays : prefs.TmScaleShelfLifeDays;
        var barcodePrefix = IsRongtaLan ? prefs.RongtaLanBarcodePrefix : prefs.TmScaleBarcodePrefix;
        var records = new List<DahuaTmPlu>();
        var problems = new List<string>();
        var keyMap = new List<(int Plu, string Name)>();
        var rowsById = _allRows.ToDictionary(r => r.Id);
        _tmRecordIds.Clear();
        // 2026-09-29: закреплённые номера и проверка номеров/кодов — общие со Штрих-ПРИНТ
        // (ScalesPluWindow.PinnedPlu.cs); pluStart — только начало для новых товаров (PluStart).
        var numbers = PinNumbersForSend(selectedIds);
        var (codes, batchProblems) = CheckSendBatch(selectedIds, numbers, maxCode: 9_999_999);
        foreach (var (id, text) in batchProblems)
            problems.Add($"«{(rowsById.TryGetValue(id, out var r) ? r.Name : id)}»: {text}");
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var product) || batchProblems.ContainsKey(id) || !numbers.TryGetValue(id, out var plu))
                continue;

            var productCode = codes[id];

            var record = new DahuaTmPlu
            {
                PluNumber = plu,
                ProductCode = productCode,
                Price = (decimal)LocalCartService.ParsePrice(product.PriceLine),
                WeighMode = product.IsWeighted ? DahuaTmWeighMode.Weighed : DahuaTmWeighMode.Piece,
                ShelfLifeDays = Math.Clamp(shelfLife, 0, 999),
                BarcodePrefix = Math.Clamp(barcodePrefix, 0, 99),
                Name = product.Title,
            };
            var problem = DahuaTmProtocol.Validate(record, decimals);
            if (problem != DahuaTmPluProblem.None)
            {
                problems.Add($"«{product.Title}» (PLU {plu}): {TmProblemText(problem)}");
                // 2026-09-28: что не так — прямо в строке таблицы.
                if (rowsById.TryGetValue(id, out var badRow))
                    badRow.SetStatus("⚠ " + TmProblemText(problem), RowState.Warning);
                continue;
            }
            records.Add(record);
            keyMap.Add((plu, product.Title));
            _tmRecordIds.Add(id);
        }
        return (records, problems, keyMap);
    }

    /// <summary>Знаков после запятой в цене на весах: TM-30F — из настроек, Rongta — 2.</summary>
    private int DahuaPricePoint => IsRongtaLan ? RongtaPricePoint : Math.Clamp(UserPreferences.Instance.TmScalePricePoint, 0, 3);

    /// <summary>id товара для каждой записи последнего BuildTmRecords (в том же порядке) — для
    /// результата по строкам.</summary>
    private readonly List<string> _tmRecordIds = new();

    /// <summary>Отмечает строки TM-30F: первые <paramref name="done"/> записей отправлены.</summary>
    private void MarkTmRowsSent(int done)
    {
        var rowsById = _allRows.ToDictionary(r => r.Id);
        for (var i = 0; i < _tmRecordIds.Count && i < done; i++)
        {
            if (rowsById.TryGetValue(_tmRecordIds[i], out var row) && !row.StatusOk)
                row.SetStatus(SentText(), RowState.Ok);
        }
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

    private static string TmErrorText(DahuaTmError error, bool rongta = false) => error switch
    {
        DahuaTmError.ConnectFailed when rongta => Tr.T("нет подключения к весам Rongta (IP, Wi-Fi/кабель, порт 4001; весы включены и не в меню настроек)", "Rongta таразасына туташуу жок (IP, Wi-Fi/кабель, 4001 порт; тараза күйүк жана жөндөө менюсунда эмес)", "no connection to the Rongta scale (IP, Wi-Fi/cable, port 4001; the scale is on and not in its settings menu)", "Rongta tartıya bağlantı yok (IP, Wi-Fi/kablo, port 4001; tartı açık ve ayar menüsünde değil)", "Rongta taroziga ulanish yo‘q (IP, Wi-Fi/kabel, 4001 port; tarozi yoqilgan va sozlamalar menyusida emas)"),
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
        var rongta = IsRongtaLan;
        // 2026-09-30: Rongta напрямую — тот же протокол на порту 4001, имена парами (RongtaNameCodec).
        var scale = rongta
            ? DahuaTmScaleService.TryCreate(prefs.RongtaScaleIp, DahuaTmProtocol.DefaultPort, DahuaTmNameCodec.Rongta)
            : DahuaTmScaleService.TryCreate(prefs.TmScaleIp, prefs.TmScalePort);
        var brandLog = rongta ? "Rongta (напрямую)" : "TM-30F (Dahua)";
        if (scale is null && rongta)
        {
            StatusText.Text = Tr.T("Не задан IP весов Rongta — «Настройки весов Rongta» → «Подключение».",
                "Rongta таразасынын IP'си коюлган эмес — «Rongta таразасынын жөндөөлөрү» → «Туташуу».",
                "The Rongta scale IP is not set — “Rongta scale settings” → “Connection”.",
                "Rongta tartı IP'si girilmemiş — «Rongta tartı ayarları» → «Bağlantı».",
                "Rongta tarozi IP'si kiritilmagan — «Rongta tarozi sozlamalari» → «Ulanish».");
            return;
        }
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

        // 2026-09-28 (просьба владельца): формат ШК весов с суммой без веса (FFWWWWWEEEEEC) — префикс
        // этих весов сам получает правило «сумма», иначе касса прочтёт сумму с этикетки как вес.
        var autoAmountPrefix = rongta ? null : ScaleBarcodeRules.EnsureTmAmountRule();
        if (autoAmountPrefix is not null)
            UpdateBarcodeExample();

        _tmCts = new CancellationTokenSource();
        SendButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");
        StatusText.Text = Tr.T($"Подключение к весам {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} таразасына туташуу…", $"Connecting to the scale {scale.Host}:{scale.Port}…", $"{scale.Host}:{scale.Port} tartısına bağlanılıyor…", $"{scale.Host}:{scale.Port} taroziga ulanilmoqda…");
        SetProgress(0, records.Count);
        try
        {
            var progress = new Progress<DahuaTmUploadProgress>(p =>
            {
                SetProgress(p.Done, p.Total);
                MarkTmRowsSent(p.Done);
                StatusText.Text = Tr.T($"Отправка на весы: {p.Done} из {p.Total}…", $"Таразага жөнөтүү: {p.Total} ичинен {p.Done}…", $"Sending to the scale: {p.Done} of {p.Total}…", $"Tartıya gönderiliyor: {p.Done} / {p.Total}…", $"Taroziga yuborilmoqda: {p.Total} dan {p.Done}…");
            });
            var result = await scale.UploadPlusAsync(records, DahuaPricePoint, mode, progress, _tmCts.Token).ConfigureAwait(true);
            PosLogger.Log($"{brandLog}: выгрузка PLU {scale.Host}:{scale.Port}: всего {result.Total}, отправлено {result.Sent}, ответов {result.Acknowledged}, без маркера {result.UnframedReplies}, повторов {result.Retries}, ошибка {result.Error} {result.Detail}", "SCALES");

            // Результат по строкам: принятые — ✓, строка, на которой остановились, — ✗, остальные — не отправлены.
            var accepted = result.Ok ? records.Count : result.Acknowledged + result.UnframedReplies;
            MarkTmRowsSent(accepted);
            var rowsById = _allRows.ToDictionary(r => r.Id);
            for (var i = accepted; i < _tmRecordIds.Count; i++)
            {
                if (!rowsById.TryGetValue(_tmRecordIds[i], out var row))
                    continue;
                if (result.FailedPlu > 0 && keyMap[i].Plu == result.FailedPlu)
                    row.SetStatus("✗ " + TmErrorText(result.Error, rongta), RowState.Error);
                else
                    row.SetStatus(NotSentText(), RowState.Warning);
            }

            // 2026-09-29: принятые весами записи — в «что на весах» (закреплённые PLU). Очищать
            // ячейки и переводить клавиши на TM-30F касса не умеет (протокол «!0L» не разобран) —
            // если номер товара сменился, честно говорим, что старую ячейку и клавиши надо проверить
            // в «Русском масштабе».
            var written = new Dictionary<string, ScaleSentPlu>(StringComparer.Ordinal);
            for (var i = 0; i < accepted && i < _tmRecordIds.Count; i++)
            {
                written[_tmRecordIds[i]] = new ScaleSentPlu
                {
                    Plu = records[i].PluNumber,
                    Code = records[i].ProductCode.ToString(CultureInfo.InvariantCulture),
                    Name = records[i].Name,
                };
            }
            // 2026-09-30: клавиши быстрого вызова из колонки «Клавиша» — страницами «!0L» (клавиша → PLU
            // ячейки товара). Только если товары записаны и клавиши заданы.
            var hotkeyNote = "";
            if (result.Ok)
            {
                // Кнопка = PLU; вписанная в «Клавиша» — важнее (ScalesPluWindow.KeySheet.cs).
                // 2026-09-30: у Rongta кнопки вызывают ячейки по таблице «!0L» (224 кнопки = 112 × 2); после
                // чужих раскладок кнопки показывали «ПЛУ не привязан к горячей клавише» — касса каждый раз
                // пишет «кнопка N → ячейка N» для всех 224 (товар на кнопку = PLU товара, KeyIsPlu).
                var pluByKey = rongta
                    ? new Dictionary<int, int> { [RongtaHotkeyCount] = RongtaHotkeyCount }
                    : BuildDahuaKeyMap(_tmRecordIds, records.Select(r => r.PluNumber).ToList());
                if (pluByKey.Count > 0)
                {
                    StatusText.Text = Tr.T("Запись клавиш весов…", "Тараза баскычтары жазылууда…", "Writing the scale keys…", "Tartı tuşları yazılıyor…", "Tarozi tugmalari yozilmoqda…");
                    var (pages, keyError) = await scale.UploadHotkeysAsync(pluByKey, _tmCts.Token).ConfigureAwait(true);
                    PosLogger.Log($"{brandLog}: клавиши {string.Join(", ", pluByKey.OrderBy(k => k.Key).Select(k => $"{k.Key}→{k.Value}"))}; страниц {pages}, ошибка {keyError}", "SCALES");
                    hotkeyNote = keyError == DahuaTmError.None
                        ? Tr.T($" Клавиши записаны: {pluByKey.Count}.", $" Баскычтар жазылды: {pluByKey.Count}.", $" Keys written: {pluByKey.Count}.", $" Tuşlar yazıldı: {pluByKey.Count}.", $" Tugmalar yozildi: {pluByKey.Count}.")
                        : Tr.T(" Клавиши не записаны: ", " Баскычтар жазылган жок: ", " Keys not written: ", " Tuşlar yazılmadı: ", " Tugmalar yozilmadi: ") + TmErrorText(keyError, rongta) + ".";
                }
            }

            var tmPlan = ScalePluPlanner.PlanMoves(LabelScaleStore.Active.SentPlus, written);
            RememberSent(written, Array.Empty<int>());
            var movesText = string.Join(", ", tmPlan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"));
            var movedNote = tmPlan.PluMoves.Count == 0
                ? ""
                : rongta
                ? Tr.T($" Сменился PLU у товаров: {movesText}. В старых ячейках Rongta остались прежние товары — удалять ячейки касса пока не умеет.",
                       $" PLU өзгөргөн товарлар: {movesText}. Rongta'нын эски уячаларында мурунку товарлар калды — уячаларды өчүрүүнү касса азырынча билбейт.",
                       $" PLU changed for: {movesText}. The old Rongta slots still hold the previous goods — the till cannot delete slots yet.",
                       $" PLU'su değişen ürünler: {movesText}. Rongta'nın eski hücrelerinde önceki ürünler kaldı — kasa henüz hücre silemiyor.",
                       $" PLU o‘zgargan tovarlar: {movesText}. Rongta eski kataklarida oldingi tovarlar qoldi — kassa hozircha kataklarni o‘chira olmaydi.")
                : Tr.T($" Сменился PLU у товаров: {string.Join(", ", tmPlan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. Старые ячейки и клавиши TM-30F проверьте в «Русском масштабе».",
                       $" PLU өзгөргөн товарлар: {string.Join(", ", tmPlan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. TM-30F'тин эски уячаларын жана баскычтарын «Русский масштаб»та текшериңиз.",
                       $" PLU changed for: {string.Join(", ", tmPlan.PluMoves.Select(m => $"“{rowsById.GetValueOrDefault(m.ProductId)?.Name}” {m.From} → {m.To}"))}. Check the old slots and keys of the TM-30F in “Russian Scale”.",
                       $" PLU'su değişen ürünler: {string.Join(", ", tmPlan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. TM-30F'in eski hücrelerini ve tuşlarını «Русский масштаб»da kontrol edin.",
                       $" PLU o‘zgargan tovarlar: {string.Join(", ", tmPlan.PluMoves.Select(m => $"«{rowsById.GetValueOrDefault(m.ProductId)?.Name}» {m.From} → {m.To}"))}. TM-30F eski kataklari va tugmalarini «Русский масштаб»da tekshiring.");

            var autoRuleNote = _codeReplaceNote + hotkeyNote + movedNote + (autoAmountPrefix is null
                ? ""
                : Tr.T($" Формат весов — с суммой: этикетки с префиксом {autoAmountPrefix} касса теперь читает как СУММУ.",
                       $" Тараза форматы — сумма менен: {autoAmountPrefix} префикстүү этикеткаларды касса эми СУММА катары окуйт.",
                       $" The scale format carries the amount: the till now reads labels with prefix {autoAmountPrefix} as AMOUNT.",
                       $" Tartı biçimi tutarlı: kasa artık {autoAmountPrefix} önekli etiketleri TUTAR olarak okur.",
                       $" Tarozi formati — summali: kassa endi {autoAmountPrefix} prefiksli yorliqlarni SUMMA sifatida o‘qiydi."));

            if (result.Ok)
            {
                // 2026-09-29: номера закреплены за товарами — «далее по порядку списка» больше не так.
                StatusText.Text = Tr.T($"Отправлено на весы: {result.Total}. У каждого товара свой постоянный номер PLU — см. колонку PLU. Проверьте товар на весах.",
                                       $"Таразага жөнөтүлдү: {result.Total}. Ар бир товардын өз туруктуу PLU номери бар — PLU тилкесин караңыз. Товарды таразадан текшериңиз.",
                                       $"Sent to the scale: {result.Total}. Every product keeps its own fixed PLU number — see the PLU column. Check the item on the scale.",
                                       $"Tartıya gönderildi: {result.Total}. Her ürünün kendi sabit PLU numarası var — PLU sütununa bakın. Ürünü tartıda kontrol edin.",
                                       $"Taroziga yuborildi: {result.Total}. Har bir tovarning o‘z doimiy PLU raqami bor — PLU ustuniga qarang. Tovarni tarozida tekshiring.")
                                  + (result.UnframedReplies > 0
                                      ? Tr.T($" Внимание: {result.UnframedReplies} ответ(ов) весов не по ожидаемой форме — см. журнал обмена.", $" Көңүл буруңуз: таразанын {result.UnframedReplies} жообу күтүлгөн формада эмес — алмашуу журналын караңыз.", $" Note: {result.UnframedReplies} scale reply(ies) not in the expected form — see the exchange log.", $" Dikkat: tartının {result.UnframedReplies} yanıtı beklenen biçimde değil — iletişim günlüğüne bakın.", $" Diqqat: tarozining {result.UnframedReplies} javobi kutilgan shaklda emas — almashuv jurnaliga qarang.")
                                      : "")
                                  + autoRuleNote;
                PosLogger.Log((rongta ? ScalePluPlanner.RongtaLayoutMarker : ScalePluPlanner.TmLayoutMarker) + string.Join("; ", keyMap.Select(x => $"{x.Plu} — {x.Name}")), "SCALES");
                if (rongta)
                {
                    // 2026-09-30: этикетки Rongta с суммой (тип ШК 02 и т. п.) — префикс этих весов = «сумма»
                    // здесь и в соседней программе (касса ↔ программа владельца), иначе «21 — вес» читал
                    // сумму как вес.
                    var rongtaType = RongtaBarcodeFormat.Find(prefs.RongtaBarcodeType);
                    if (rongtaType is null || rongtaType.Value == RongtaBarcodeFormat.ValueKind.Price)
                    {
                        var prefix = Math.Clamp(prefs.RongtaLanBarcodePrefix, 0, 99).ToString("00", CultureInfo.InvariantCulture);
                        ScaleBarcodeRules.ApplyAndSave(prefix, NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Amount);
                        ScaleLabelCodeRegistry.PublishAmountPrefix(prefix);
                    }
                }
                if (rongta)
                    PosLogger.Log("Rongta, имена на весах: " + string.Join("; ", records.Take(20).Select(r => $"{r.PluNumber} «{RongtaNameCodec.Preview(r.Name)}»")), "SCALES");
            }
            else
            {
                var errorText = TmErrorText(result.Error, rongta);
                StatusText.Text = Tr.T($"Отправка не завершена: {errorText}. Принято весами: {result.Acknowledged + result.UnframedReplies} из {result.Total}",
                                       $"Жөнөтүү аягына чыккан жок: {errorText}. Тараза кабыл алды: {result.Total} ичинен {result.Acknowledged + result.UnframedReplies}",
                                       $"Sending did not finish: {errorText}. Accepted by the scale: {result.Acknowledged + result.UnframedReplies} of {result.Total}",
                                       $"Gönderim tamamlanmadı: {errorText}. Tartının kabul ettiği: {result.Acknowledged + result.UnframedReplies} / {result.Total}",
                                       $"Yuborish tugamadi: {errorText}. Tarozi qabul qildi: {result.Total} dan {result.Acknowledged + result.UnframedReplies}")
                                  + (result.FailedPlu > 0 ? $" (PLU {result.FailedPlu})" : "")
                                  + Tr.T(". Журнал обмена: ", ". Алмашуу журналы: ", ". Exchange log: ", ". İletişim günlüğü: ", ". Almashuv jurnali: ") + DahuaTmScaleService.ExchangeLogPath
                                  + autoRuleNote;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"{brandLog}: выгрузка не удалась: {ex}", "SCALES");
            StatusText.Text = Tr.T("Ошибка отправки: ", "Жиберүү катасы: ", "Send error: ", "Gönderme hatası: ", "Yuborish xatosi: ") + ex.Message;
        }
        finally
        {
            _tmCts.Dispose();
            _tmCts = null;
            HideProgress();
            SendButton.Content = _sendButtonDefaultText;
        }
    }

    /// <summary>Окно закрыли во время отправки на TM-30F — останавливаем её.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _tmCts?.Cancel();
        _rongtaServerCts?.Cancel();
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
        // 2026-09-28: способ загрузки Rongta («через сайт» / «свой сервер») — в Настройки → Весы.
        if (string.Equals(UserPreferences.Instance.RongtaDataSource, RongtaSourceServer, StringComparison.OrdinalIgnoreCase))
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
    /// <summary>2026-09-30, живой баг владельца: «свой сервер» ждал программу весов 90 с с серой
    /// кнопкой, и переключение на «напрямую по сети» в это время ничего не давало — кнопка оставалась
    /// серой, а в строке висело «RLS1000 не найдена…». Теперь ожидание можно остановить кнопкой, и оно
    /// прерывается само при смене способа/марки и закрытии окна.</summary>
    private CancellationTokenSource? _rongtaServerCts;

    private async Task SendToRongtaViaOwnServerAsync()
    {
        _rongtaServerCts = new CancellationTokenSource();
        var serverCt = _rongtaServerCts.Token;
        SendButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");
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

            var port = UserPreferences.Instance.RongtaServerPort;
            if (port is <= 0 or > 65535)
                port = 5001;

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

            var serverTask = RongtaTcpServerService.RunOnceAsync(port, products, connectTimeout, serverCt);

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
            if (serverCt.IsCancellationRequested)
            {
                StatusText.Text = Tr.T("Ожидание программы весов остановлено.", "Тараза программасын күтүү токтотулду.", "Waiting for the scale software was stopped.", "Tartı programını bekleme durduruldu.", "Tarozi dasturini kutish to‘xtatildi.");
                return;
            }
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
            _rongtaServerCts?.Dispose();
            _rongtaServerCts = null;
            SendButton.IsEnabled = true;
            SendButton.Content = _sendButtonDefaultText;
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

        // 2026-09-29: у неотмеченного товара без закреплённого номера колонка PLU пустая, а не «—».
        var withoutPlu = rows.Count(r => r.PluText == "—" || string.IsNullOrEmpty(r.PluText));
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

        /// <summary>2026-09-29: PLU из карточки товара (null — не задан).</summary>
        public int? CatalogPlu { get; set; } // 2026-09-30: set — «PLU как на сайте» присваивает PLU на сайте

        private string _pluText = "";

        /// <summary>Колонка PLU. 2026-09-29: закреплённый номер ячейки на весах (правится) или PLU
        /// карточки — заполняет ScalesPluWindow.RefreshPluNumbers.</summary>
        public string PluText
        {
            get => _pluText;
            set
            {
                if (_pluText == value)
                    return;
                _pluText = value;
                Notify(nameof(PluText));
                Notify(nameof(PluSortKey));
            }
        }

        /// <summary>Что показали в колонке PLU (чтобы при потере фокуса понять, правили ли номер).</summary>
        public string PluShown { get; private set; } = "";

        /// <summary>Номер ещё не закреплён — закрепится при отправке (курсив).</summary>
        public bool PluTentative { get; private set; }

        public bool PluReadOnly { get; private set; } = true;

        public string PluHint { get; private set; } = "";

        /// <summary>Сортировка колонки PLU числом (строкой вышло бы 1, 10, 2).</summary>
        public int PluSortKey => int.TryParse(PluText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

        public void SetPlu(string text, bool tentative, bool readOnly, string hint)
        {
            PluShown = text;
            PluText = text;
            PluTentative = tentative;
            PluReadOnly = readOnly;
            PluHint = hint;
            Notify(nameof(PluTentative));
            Notify(nameof(PluReadOnly));
            Notify(nameof(PluHint));
        }

        /// <summary>2026-09-29: «Код в ШК» по умолчанию (PLU / код из карточки) — к нему возвращает
        /// пустое поле.</summary>
        public string DefaultCode { get; init; } = "";

        public string PriceLine { get; init; } = "";

        /// <summary>Единица измерения товара («кг», «шт.») — нужна и в таблице, и в выгрузке:
        /// программы весов сопоставляют по ней тип товара (весовой/штучный).</summary>
        public string Unit { get; init; } = "";

        /// <summary>Цена числом. PriceLine — оформленная строка для экрана («160,00 сом»), в CSV
        /// её класть нельзя: ни Excel, ни программа весов такую ячейку числом не прочитают.</summary>
        public double Price { get; init; }

        private string _barcodeCode = "";

        /// <summary>2026-09-28: число, которое весы напечатают в весовом ШК («Код товара»
        /// записи ПЛУ). Правится в таблице перед прямой выгрузкой. 2026-09-29: с уведомлением —
        /// поле меняется и из кода (другие весы, возврат к коду по умолчанию).</summary>
        public string BarcodeCode
        {
            get => _barcodeCode;
            set
            {
                if (_barcodeCode == value)
                    return;
                _barcodeCode = value;
                Notify(nameof(BarcodeCode));
            }
        }

        /// <summary>2026-09-28: категория товара (фильтр и «категории весов»).</summary>
        public string Category { get; init; } = "";

        private string _hotkeyText = "";

        /// <summary>2026-09-28: клавиша быстрого доступа на весах (1–120), пусто — без клавиши.</summary>
        private string _hotkeyAuto = "";

        /// <summary>2026-09-30: серая подсказка в пустой «Клавише» — кнопка = PLU (весы «!0L»).</summary>
        public string HotkeyAuto
        {
            get => _hotkeyAuto;
            set
            {
                if (_hotkeyAuto == value)
                    return;
                _hotkeyAuto = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HotkeyAuto)));
            }
        }

        public string HotkeyText
        {
            get => _hotkeyText;
            set
            {
                if (_hotkeyText == value)
                    return;
                _hotkeyText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HotkeyText)));
            }
        }

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

        /// <summary>2026-09-28: результат последней отправки по этой строке (колонка «Результат»).</summary>
        public string Status { get; private set; } = "";
        public bool StatusOk { get; private set; }
        public bool StatusError { get; private set; }
        public bool StatusWarn { get; private set; }

        public void SetStatus(string text, RowState state)
        {
            Status = text;
            StatusOk = state == RowState.Ok;
            StatusError = state == RowState.Error;
            StatusWarn = state == RowState.Warning;
            foreach (var name in new[] { nameof(Status), nameof(StatusOk), nameof(StatusError), nameof(StatusWarn) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>2026-09-29: дописать к результату строки (клавиша, старая ячейка) — цвет по
        /// худшему: ошибка важнее предупреждения, предупреждение важнее «отправлено».</summary>
        public void AppendStatus(string text, RowState state)
        {
            var worst = StatusError || state == RowState.Error ? RowState.Error
                : StatusWarn || state == RowState.Warning ? RowState.Warning
                : StatusOk || state == RowState.Ok ? RowState.Ok
                : RowState.None;
            SetStatus(Status + text, worst);
        }

        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private enum RowState
    {
        None,
        Ok,
        Error,
        Warning,
    }
}
