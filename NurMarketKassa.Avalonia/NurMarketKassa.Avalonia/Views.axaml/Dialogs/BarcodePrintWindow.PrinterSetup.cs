using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (перенос из веб-редактора ценников, блок «Настройки принтера»). Сворачиваемый блок в «Настройке ценника»: то, что реально действует на нашем пути печати.
/// Для всех принтеров: «Рамка» (поле шаблона этикетки), сдвиг по горизонтали и вертикали в точках 203 dpi (в настройках кассы хранится в мм — те же LabelOffsetXMm/YMm, что в редакторе этикетки).
/// Только для принтера без драйвера на языке TSPL (Xprinter, TSC, Gprinter): зазор, скорость, плотность, ориентация и «Калибровка» — те же UserPreferences, что в редакторе;
/// у принтера через драйвер Windows их показывать нельзя (драйвер задаёт сам, кнопки были бы без эффекта). «Сбросить настройки» возвращает значения по умолчанию.
/// Не переносилось: «Разрешение» (у нас фиксированные 203 dpi, у драйвера — его собственное), «Растр или шрифт принтера» (всегда растр — одинаково на всех принтерах),
/// «Инверсия растра» (TSPL-битмап инвертируется кодом сам), «Ширина модуля/высота штрихкода» (уже есть как «Толщина штриха» и «Высота штрих-кода»).</summary>
public sealed partial class BarcodePrintView
{
    private Button _ptToggle = null!;
    private StackPanel _ptBody = null!;
    private StackPanel _ptTspl = null!;
    private CheckBox _frameCheck = null!;
    private NumericUpDown _offXBox = null!, _offYBox = null!, _gapBox = null!;
    private ComboBox _speedCombo = null!, _densityCombo = null!, _dirCombo = null!;
    // 2026-11-21: «Датчик» и «Размер страницы — по этикетке» жили только в старом окне «Редактор этикетки» (входов в него больше нет) — теперь здесь.
    private ComboBox _sensorCombo = null!;
    private CheckBox _pageCheck = null!;
    private Button _calibrateButton = null!;
    private TextBlock _ptNote = null!, _ptStatus = null!;
    private bool _ptInit = true;

    private static string AsOnPrinter => T("как на принтере", "принтердегидей", "as on the printer", "yazıcıdaki gibi", "printerdagidek");

    private Control BuildPrinterSetupBlock()
    {
        _ptToggle = Ghost("");
        _ptToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _ptToggle.Margin = new Thickness(0, 4, 0, 4);
        _ptBody = new StackPanel { Spacing = 2, IsVisible = false };
        _ptToggle.Click += (_, _) => { _ptBody.IsVisible = !_ptBody.IsVisible; UpdatePtToggleText(); };

        _frameCheck = Check(T("Рамка", "Алкак", "Frame", "Çerçeve", "Ramka"), _template.Frame);
        ToolTip.SetTip(_frameCheck, T("Тонкая рамка по контуру этикетки", "Этикетканын тегерегинде ичке алкак", "A thin frame around the label edge", "Etiketin kenarında ince çerçeve", "Yorliq chetida ingichka ramka"));
        _frameCheck.IsCheckedChanged += (_, _) => FlagChanged(() => _template.Frame = _frameCheck.IsChecked == true, relayout: false);

        var dotsMax = LabelPrintMath.ClampOffsetDots(int.MaxValue);
        _offXBox = new NumericUpDown { Minimum = -dotsMax, Maximum = dotsMax, Increment = 1, FormatString = "0", Width = 118, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
        _offYBox = new NumericUpDown { Minimum = -dotsMax, Maximum = dotsMax, Increment = 1, FormatString = "0", Width = 118, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(_offXBox, T("Сдвиг картинки при печати: минус — влево, плюс — вправо. 8 точек = 1 мм.", "Басууда сүрөттү жылдыруу: минус — солго, плюс — оңго. 8 чекит = 1 мм.",
            "Image shift when printing: minus = left, plus = right. 8 dots = 1 mm.", "Baskıda görüntü kaydırma: eksi = sola, artı = sağa. 8 nokta = 1 mm.", "Chop etishda rasm siljishi: minus — chapga, plyus — o'ngga. 8 nuqta = 1 mm."));
        ToolTip.SetTip(_offYBox, T("Сдвиг картинки при печати: минус — вверх, плюс — вниз. 8 точек = 1 мм.", "Басууда сүрөттү жылдыруу: минус — өйдө, плюс — төмөн. 8 чекит = 1 мм.",
            "Image shift when printing: minus = up, plus = down. 8 dots = 1 mm.", "Baskıda görüntü kaydırma: eksi = yukarı, artı = aşağı. 8 nokta = 1 mm.", "Chop etishda rasm siljishi: minus — yuqoriga, plyus — pastga. 8 nuqta = 1 mm."));
        _offXBox.ValueChanged += (_, _) => { if (!_ptInit) { UserPreferences.Instance.LabelOffsetXMm = LabelPrintMath.DotsToMm(LabelPrintMath.ClampOffsetDots((int)(_offXBox.Value ?? 0m))); SavePrinterPrefs(); } };
        _offYBox.ValueChanged += (_, _) => { if (!_ptInit) { UserPreferences.Instance.LabelOffsetYMm = LabelPrintMath.DotsToMm(LabelPrintMath.ClampOffsetDots((int)(_offYBox.Value ?? 0m))); SavePrinterPrefs(); } };

        _gapBox = new NumericUpDown { Minimum = 0, Maximum = 20, Increment = 0.5m, FormatString = "0.0", Width = 118, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
        _gapBox.ValueChanged += (_, _) => { if (!_ptInit) { UserPreferences.Instance.LabelGapMm = Math.Clamp((double)(_gapBox.Value ?? 2m), 0, 20); SavePrinterPrefs(); } };
        _speedCombo = new ComboBox { MinWidth = 190 };
        _speedCombo.Items.Add(AsOnPrinter);
        foreach (var n in Enumerable.Range(2, 5))
            _speedCombo.Items.Add(n.ToString());
        _speedCombo.SelectionChanged += (_, _) => { if (!_ptInit && _speedCombo.SelectedIndex >= 0) { UserPreferences.Instance.LabelTsplSpeed = _speedCombo.SelectedIndex > 0 ? _speedCombo.SelectedIndex + 1 : null; SavePrinterPrefs(); } };
        _densityCombo = new ComboBox { MinWidth = 190 };
        _densityCombo.Items.Add(AsOnPrinter);
        foreach (var n in Enumerable.Range(0, 16))
            _densityCombo.Items.Add(n.ToString());
        _densityCombo.SelectionChanged += (_, _) => { if (!_ptInit && _densityCombo.SelectedIndex >= 0) { UserPreferences.Instance.LabelTsplDensity = _densityCombo.SelectedIndex > 0 ? _densityCombo.SelectedIndex - 1 : null; SavePrinterPrefs(); } };
        _dirCombo = new ComboBox { MinWidth = 190 };
        _dirCombo.Items.Add(AsOnPrinter);
        _dirCombo.Items.Add(T("Обычная", "Кадимки", "Normal", "Normal", "Oddiy"));
        _dirCombo.Items.Add(T("Повёрнутая 180°", "180° бурулган", "Rotated 180°", "180° döndürülmüş", "180° aylantirilgan"));
        _dirCombo.SelectionChanged += (_, _) => { if (!_ptInit && _dirCombo.SelectedIndex >= 0) { UserPreferences.Instance.LabelTsplFlip = _dirCombo.SelectedIndex switch { 1 => false, 2 => true, _ => (bool?)null }; SavePrinterPrefs(); } };

        _sensorCombo = new ComboBox { MinWidth = 190 };
        _sensorCombo.Items.Add(AsOnPrinter);
        _sensorCombo.Items.Add(T("Зазор между этикетками", "Этикеткалардын ортосундагы боштук", "Gap between labels", "Etiketler arası boşluk", "Yorliqlar orasidagi oraliq"));
        _sensorCombo.Items.Add(T("Чёрная метка", "Кара белги", "Black mark", "Siyah işaret", "Qora belgi"));
        _sensorCombo.SelectionChanged += (_, _) => { if (!_ptInit && _sensorCombo.SelectedIndex >= 0) { UserPreferences.Instance.LabelTsplSensor = _sensorCombo.SelectedIndex switch { 1 => "gap", 2 => "bline", _ => null }; SavePrinterPrefs(); } };
        _pageCheck = Check(T("Размер страницы — по этикетке", "Барактын өлчөмү — этикетка боюнча", "Page size = label size", "Sayfa boyutu = etiket boyutu", "Sahifa o'lchami = yorliq o'lchami"), UserPreferences.Instance.LabelPageFromTemplate);
        ToolTip.SetTip(_pageCheck, T("Для принтера через драйвер Windows: размер страницы берётся из шаблона этикетки (иначе драйвер может тянуть ленту по своему размеру).", "Windows драйвери аркылуу: барактын өлчөмү этикетка шаблонунан алынат.",
            "For a printer on a Windows driver: the page size comes from the label template (otherwise the driver may feed by its own size).", "Windows sürücüsü olan yazıcı için: sayfa boyutu etiket şablonundan alınır.", "Windows drayveri orqali printer uchun: sahifa o'lchami yorliq shablonidan olinadi."));
        _pageCheck.IsCheckedChanged += (_, _) => { if (!_ptInit) { UserPreferences.Instance.LabelPageFromTemplate = _pageCheck.IsChecked != false; SavePrinterPrefs(); } };

        _calibrateButton = Ghost(T("Калибровка","Калибрлөө", "Calibration", "Kalibrasyon", "Kalibrlash"));
        ToolTip.SetTip(_calibrateButton, T("Принтер протянет несколько этикеток и запомнит зазор (или метку). Только принтер без драйвера на языке TSPL.", "Принтер бир нече этикетканы тартып, боштукту (же белгини) эстеп калат. Драйверсиз TSPL тилиндеги принтер гана.",
            "The printer feeds a few labels and remembers the gap (or mark). Only a driverless printer on the TSPL language.", "Yazıcı birkaç etiket besleyip boşluğu (veya işareti) hatırlar. Yalnızca TSPL dilinde sürücüsüz yazıcı.", "Printer bir nechta yorliqni tortib, oraliqni (yoki belgini) eslab qoladi. Faqat TSPL tilidagi drayversiz printer."));
        _calibrateButton.Click += async (_, _) => await CalibrateAsync().ConfigureAwait(true);
        var reset = Ghost(T("Сбросить настройки", "Жөндөөлөрдү кайтаруу", "Reset settings", "Ayarları sıfırla", "Sozlamalarni tiklash"));
        ToolTip.SetTip(reset, T("Сдвиги, зазор, скорость, плотность, ориентация и рамка — как по умолчанию", "Жылдыруулар, боштук, ылдамдык, тыгыздык, багыт жана алкак — демейкидей",
            "Shifts, gap, speed, density, orientation and frame back to defaults", "Kaydırmalar, boşluk, hız, yoğunluk, yön ve çerçeve varsayılana döner", "Siljishlar, oraliq, tezlik, zichlik, yo'nalish va ramka — standart holatga"));
        reset.Click += (_, _) => ResetPrinterSetup();
        _calibrateButton.Margin = new Thickness(0, 0, 8, 6);
        reset.Margin = new Thickness(0, 0, 8, 6);

        _ptNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 4) };
        Use(_ptNote, TextBlock.ForegroundProperty, "BrushTextSoft");
        _ptStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, IsVisible = false };
        Use(_ptStatus, TextBlock.ForegroundProperty, "BrushTextSoft");

        var rowA = new WrapPanel { Orientation = Orientation.Horizontal };
        rowA.Children.Add(Inline(T("Сдвиг по горизонтали, точек", "Горизонталдуу жылдыруу, чекит", "Horizontal shift, dots", "Yatay kaydırma, nokta", "Gorizontal siljish, nuqta"), _offXBox));
        rowA.Children.Add(Inline(T("Сдвиг по вертикали, точек", "Вертикалдуу жылдыруу, чекит", "Vertical shift, dots", "Dikey kaydırma, nokta", "Vertikal siljish, nuqta"), _offYBox));
        _frameCheck.Margin = new Thickness(0, 0, 12, 0);
        rowA.Children.Add(_frameCheck);
        _pageCheck.Margin = new Thickness(0, 0, 12, 0);
        rowA.Children.Add(_pageCheck);

        var tspl = new WrapPanel { Orientation = Orientation.Horizontal };
        tspl.Children.Add(Inline(T("Зазор между этикетками, мм", "Этикеткалардын ортосундагы боштук, мм", "Gap between labels, mm", "Etiketler arası boşluk, mm", "Yorliqlar orasidagi oraliq, mm"), _gapBox));
        tspl.Children.Add(Inline(T("Скорость", "Ылдамдык", "Speed", "Hız", "Tezlik"), _speedCombo));
        tspl.Children.Add(Inline(T("Плотность (темнее)", "Тыгыздык (караңгыраак)", "Density (darker)", "Yoğunluk (koyu)", "Zichlik (to'qroq)"), _densityCombo));
        tspl.Children.Add(Inline(T("Ориентация", "Багыты", "Orientation", "Yön", "Yo'nalish"), _dirCombo));
        tspl.Children.Add(Inline(T("Датчик", "Сенсор", "Sensor", "Sensör", "Sensor"), _sensorCombo));
        _ptTspl = new StackPanel { Children = { tspl } };

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _calibrateButton, reset } };
        _ptBody.Children.Add(_ptNote);
        _ptBody.Children.Add(rowA);
        _ptBody.Children.Add(_ptTspl);
        _ptBody.Children.Add(buttons);
        _ptBody.Children.Add(_ptStatus);

        SyncPrinterSetup();
        _ptInit = false;
        UpdatePtToggleText();
        UpdatePrinterSetupVisibility();
        return new StackPanel { Children = { _ptToggle, _ptBody } };
    }

    private void UpdatePtToggleText() =>
        _ptToggle.Content = (_ptBody.IsVisible ? "▴  " : "▾  ") + T("Настройки принтера", "Принтер жөндөөлөрү", "Printer settings", "Yazıcı ayarları", "Printer sozlamalari");

    /// <summary>Поля блока — по настройкам кассы (после старта, «Сбросить настройки», выбора шаблона).</summary>
    private void SyncPrinterSetup()
    {
        var was = _ptInit;
        _ptInit = true;
        try
        {
            var p = UserPreferences.Instance;
            _offXBox.Value = LabelPrintMath.ClampOffsetDots(LabelPrintMath.MmToDots(p.LabelOffsetXMm));
            _offYBox.Value = LabelPrintMath.ClampOffsetDots(LabelPrintMath.MmToDots(p.LabelOffsetYMm));
            _gapBox.Value = (decimal)Math.Clamp(p.LabelGapMm, 0, 20);
            _speedCombo.SelectedIndex = p.LabelTsplSpeed is { } sp ? Math.Clamp(sp, 2, 6) - 1 : 0;
            _densityCombo.SelectedIndex = p.LabelTsplDensity is { } dn ? Math.Clamp(dn, 0, 15) + 1 : 0;
            _dirCombo.SelectedIndex = p.LabelTsplFlip is { } fl ? (fl ? 2 : 1) : 0;
            _sensorCombo.SelectedIndex = p.LabelTsplSensor switch { "gap" => 1, "bline" => 2, _ => 0 };
            _pageCheck.IsChecked = p.LabelPageFromTemplate;
        }
        finally { _ptInit = was; }
        SyncFrameFromTemplate();
    }

    private void SyncFrameFromTemplate()
    {
        if (_frameCheck is null)
            return;
        var was = _init;
        _init = true;
        _frameCheck.IsChecked = _template.Frame;
        _init = was;
    }

    /// <summary>Параметры TSPL — только когда выбран принтер без драйвера (USB/COM/LPT/сеть) и в настройках кассы язык TSPL; иначе подсказка, почему их нет.</summary>
    private void UpdatePrinterSetupVisibility()
    {
        if (_ptTspl is null)
            return;
        var printer = _printerCombo.SelectedItem as DiscoveredPrinter;
        var raw = printer != null && BarcodeLabelService.IsRawDevicePath(printer.DevicePath);
        var tspl = raw && UserPreferences.Instance.LabelRawLanguage == "tspl";
        _ptTspl.IsVisible = tspl;
        _calibrateButton.IsVisible = tspl;
        var dpi = T($"Разрешение {LabelPrintMath.Dpi} dpi (8 точек на мм) — как в предпросмотре; у принтера через драйвер Windows разрешение берётся из драйвера.", $"Тактык {LabelPrintMath.Dpi} dpi (1 ммге 8 чекит) — алдын ала көрүүдөгүдөй; Windows драйвери аркылуу принтерде тактык драйверден алынат.",
            $"Resolution {LabelPrintMath.Dpi} dpi (8 dots per mm), as in the preview; a printer on a Windows driver uses the driver's resolution.", $"Çözünürlük {LabelPrintMath.Dpi} dpi (mm başına 8 nokta), önizlemedeki gibi; Windows sürücülü yazıcıda çözünürlük sürücüden alınır.", $"Aniqlik {LabelPrintMath.Dpi} dpi (1 mm ga 8 nuqta) — ko'rinishdagidek; Windows drayveri orqali printerda aniqlik drayverdan olinadi.");
        string why;
        if (printer is null)
            why = T("Принтер не выбран: зазор, скорость, плотность и калибровка появятся, когда выбран принтер без драйвера на языке TSPL.", "Принтер тандалган эмес: боштук, ылдамдык, тыгыздык жана калибрлөө драйверсиз TSPL тилиндеги принтер тандалганда чыгат.",
                "No printer selected: gap, speed, density and calibration appear when a driverless printer on TSPL is selected.", "Yazıcı seçilmedi: boşluk, hız, yoğunluk ve kalibrasyon, TSPL dilinde sürücüsüz yazıcı seçilince görünür.", "Printer tanlanmagan: oraliq, tezlik, zichlik va kalibrlash TSPL tilidagi drayversiz printer tanlanganda chiqadi.");
        else if (!raw)
            why = T("Принтер печатает через драйвер Windows: зазор, скорость и плотность задаются в драйвере.", "Принтер Windows драйвери аркылуу басат: боштук, ылдамдык жана тыгыздык драйверде коюлат.",
                "The printer prints through a Windows driver: gap, speed and density are set in the driver.", "Yazıcı Windows sürücüsüyle yazdırır: boşluk, hız ve yoğunluk sürücüde ayarlanır.", "Printer Windows drayveri orqali chop etadi: oraliq, tezlik va zichlik drayverda belgilanadi.");
        else if (!tspl)
            why = T("Зазор, скорость, плотность и калибровка — для принтера на языке TSPL (Xprinter, TSC, Gprinter): включите язык TSPL в мастере принтера.", "Боштук, ылдамдык, тыгыздык жана калибрлөө — TSPL тилиндеги принтер үчүн (Xprinter, TSC, Gprinter): принтер устасында TSPL тилин күйгүзүңүз.",
                "Gap, speed, density and calibration are for a TSPL printer (Xprinter, TSC, Gprinter): turn on the TSPL language in the printer wizard.", "Boşluk, hız, yoğunluk ve kalibrasyon TSPL yazıcı içindir (Xprinter, TSC, Gprinter): yazıcı sihirbazında TSPL dilini açın.", "Oraliq, tezlik, zichlik va kalibrlash — TSPL tilidagi printer uchun (Xprinter, TSC, Gprinter): printer ustasida TSPL tilini yoqing.");
        else
            why = "";
        _ptNote.Text = why.Length > 0 ? dpi + " " + why : dpi;
    }

    private static void SavePrinterPrefs()
    {
        try
        {
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label printer settings not saved: {ex.Message}", "WARNING");
        }
    }

    private void ResetPrinterSetup()
    {
        var p = UserPreferences.Instance;
        p.LabelGapMm = 2;
        p.LabelOffsetXMm = 0;
        p.LabelOffsetYMm = 0;
        p.LabelTsplDensity = null;
        p.LabelTsplSpeed = null;
        p.LabelTsplFlip = null;
        p.LabelTsplSensor = null;
        p.LabelPageFromTemplate = true;
        SavePrinterPrefs();
        _template.Frame = false;
        SyncPrinterSetup();
        TemplateChanged();
        _ptStatus.Text = T("Настройки принтера сброшены.", "Принтер жөндөөлөрү кайтарылды.", "Printer settings were reset.", "Yazıcı ayarları sıfırlandı.", "Printer sozlamalari tiklandi.");
        _ptStatus.IsVisible = true;
    }

    private async Task CalibrateAsync()
    {
        _ptStatus.IsVisible = true;
        if (!LabelPrinterSelector.PrintingSupported)
        {
            _ptStatus.Text = LabelPrinterSelector.UnsupportedText;
            return;
        }
        if (_printerCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            _ptStatus.Text = LabelPrinterSelector.MissingHint();
            return;
        }
        var path = printer.DevicePath;
        _calibrateButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => BarcodeLabelService.CalibrateSensor(path)).ConfigureAwait(true);
            _ptStatus.Text = result == LabelPrintResult.Success
                ? T("Принтер протянет несколько этикеток и запомнит зазор (или метку).", "Принтер бир нече этикетканы тартып, боштукту (же белгини) эстеп калат.", "The printer will feed a few labels and remember the gap (or mark).", "Yazıcı birkaç etiket besleyip boşluğu (veya işareti) hatırlayacak.", "Printer bir nechta yorliqni tortib, oraliqni (yoki belgini) eslab qoladi.")
                : T("Калибровка не отправлена: нужен принтер на прямом подключении (USB/COM/LPT) с языком TSPL. Подробности в журнале.", "Калибровка жөнөтүлгөн жок: TSPL тилиндеги түз туташкан принтер (USB/COM/LPT) керек. Чоо-жайы журналда.", "Calibration was not sent: a directly connected printer (USB/COM/LPT) with the TSPL language is required. See the log.", "Kalibrasyon gönderilmedi: TSPL dilinde doğrudan bağlı bir yazıcı (USB/COM/LPT) gerekir. Ayrıntılar günlükte.", "Kalibrlash yuborilmadi: TSPL tilidagi to'g'ridan-to'g'ri ulangan printer (USB/COM/LPT) kerak. Tafsilotlar jurnalda.");
        }
        finally
        {
            _calibrateButton.IsEnabled = true;
        }
    }

    /// <summary>Подпись под предпросмотром: размер этикетки в точках принтера, разрешение и модуль штрихкода (ширина узкого штриха в точках — берётся из той же сцены, по которой печатает принтер).</summary>
    private void UpdatePreviewDots(string name, string code, string price, LabelTemplate template, CatalogProductTileVm? sample)
    {
        var (w, h) = LabelPrintMath.LabelDots(template.WidthMm, template.HeightMm);
        var text = $"{w}×{h} " + T("точек", "чекит", "dots", "nokta", "nuqta") + $" · {LabelPrintMath.Dpi} dpi";
        if (template.Barcode.Enabled)
        {
            var bars = BarcodeLabelService.BuildLabelScene(name, code, price, template, sample?.Article, sample?.Unit, UserPreferences.Instance.StoreName)
                .Items.OfType<LabelSceneBars>().FirstOrDefault();
            if (bars is not null)
                text += " · " + T($"модуль {bars.Module}", $"модул {bars.Module}", $"module {bars.Module}", $"modül {bars.Module}", $"modul {bars.Module}");
        }
        _previewDots.Text = text;
        ToolTip.SetTip(_previewDots, T("Так принтер напечатает этикетку: размер в точках принтера, разрешение и ширина узкого штриха в точках.", "Принтер этикетканы ушундай басат: принтердин чекиттериндеги өлчөм, тактык жана ичке сызыктын туурасы чекитте.",
            "This is how the printer prints the label: size in printer dots, resolution and the narrow bar width in dots.", "Yazıcı etiketi böyle basar: yazıcı noktalarıyla boyut, çözünürlük ve ince çizgi genişliği (nokta).", "Printer yorliqni shunday chop etadi: printer nuqtalarida o'lcham, aniqlik va ingichka chiziq kengligi (nuqta)."));
    }
}
