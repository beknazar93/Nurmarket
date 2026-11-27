using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 («создай то, чего у нас нет»): окна «Экспорт в 1С» (OneCExportWindow) и «Загрузим вашу базу под ключ» (FillBaseRequestWindow) — на 1024×600 и 800×600, ru/ky/uz:
/// без обрезанного текста, без налезания полей и подписей (FormFieldsOverlapUiTests.FindViolations), всё в ширине окна; сквозные сценарии: выгрузка в файл через подмену диалога,
/// форма услуги → ссылка wa.me с готовым текстом (без автоотправки); карточки «Экспорт в 1С» и «Загрузим под ключ» в «Операциях» программы владельца.</summary>
[Collection("UiScenarios")]
public sealed class OneCExportUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedUnlocked = UserPreferences.Instance.AnalyticsExportUnlocked;

    // 2026-11-26 (стресс-тест): у каждого теста свой файл последних настроек — выгрузка в одном тесте не меняет отметки по умолчанию в другом
    public OneCExportUiTests() =>
        OneCExportWindow.PrefsPathForTests = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onec-prefs-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _savedIsOwner);
        UserPreferences.Instance.AnalyticsExportUnlocked = _savedUnlocked;
        try { if (OneCExportWindow.PrefsPathForTests is { } prefs) File.Delete(prefs); } catch { /* временный файл */ }
        OneCExportWindow.PrefsPathForTests = null;
        OneCExportWindow.BeforeWriteForTests = null;
        OneCExportWindow.SavePathOverride = null;
        FillBaseRequestWindow.OpenUrlOverride = null;
        ModalShell.WorkingAreaForTests = null;
    }

    public static IEnumerable<object[]> Matrix() =>
        new (int W, int H)[] { (1024, 600), (800, 600) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static T ById<T>(Window w, string id) where T : Control =>
        w.GetVisualDescendants().OfType<T>().Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == id);

    private static void AssertFits(Window window, string name, int w, int h, AppLanguage lang)
    {
        var screen = new UiSize(w, h);
        ServiceFormsFitUiTests.Save(window, $"{name}-{lang}-{screen}.png"); // снимок для глаз: %TEMP%\nurmarket-tests\ui-actual
        ServiceFormsFitUiTests.FitProblems(window, screen, formButtons: false).Should().BeEmpty($"{name} вписывается в экран {screen} ({lang})");
        FormFieldsOverlapUiTests.FindViolations(window, name).Should().BeEmpty($"поля и подписи {name} не налезают ({screen} {lang})");
        TextClipChecker.Find(window, name, lang.ToString()).Should().BeEmpty($"текст {name} не обрезан ({screen} {lang})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task OneCExportWindow_Layout_NoOverlapNoClip(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new OneCExportWindow();
        try
        {
            ServiceFormsFitUiTests.ShowLikeInApp(window, new UiSize(w, h));
            ById<Button>(window, "OneCExportButton").IsVisible.Should().BeTrue();
            ById<CheckBox>(window, "OneCPartSales").IsChecked.Should().BeTrue();
            ById<RadioButton>(window, "OneCFormatExcel").IsChecked.Should().BeTrue("Excel — формат по умолчанию");
            AssertFits(window, "onec-export", w, h, lang);
            // 2026-11-27 (живой проход 1920×1080): подпись «Возвраты от клиентов» упиралась в квадратик соседней галочки — между колонками нужен зазор
            var returns = ById<CheckBox>(window, "OneCPartReturns");
            var receipts = ById<CheckBox>(window, "OneCPartReceipts");
            var label = returns.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
            var labelRight = Avalonia.VisualExtensions.TranslatePoint(label, new Avalonia.Point(label.Bounds.Width, 0), window)!.Value.X;
            var nextLeft = Avalonia.VisualExtensions.TranslatePoint(receipts, new Avalonia.Point(0, 0), window)!.Value.X;
            (nextLeft - labelRight).Should().BeGreaterThanOrEqualTo(8, $"подпись «Возвраты» не упирается в соседнюю галочку ({w}×{h} {lang})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task FillBaseWindow_Layout_NoOverlapNoClip(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new FillBaseRequestWindow();
        try
        {
            ServiceFormsFitUiTests.ShowLikeInApp(window, new UiSize(w, h));
            ById<Button>(window, "FillBaseSendButton").IsVisible.Should().BeTrue();
            AssertFits(window, "fill-base", w, h, lang);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OneCExport_SavesCsvThroughPickerOverride_AndShowsStatus()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.AnalyticsExportUnlocked = true; // на «Старте» выгрузка за модулем — здесь проверяется сам файл
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onec-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string? suggested = null;
        OneCExportWindow.SavePathOverride = name => { suggested = name; return path; };
        var window = new OneCExportWindow();
        try
        {
            window.Show();
            UiShot.Settle(4);
            foreach (var part in new[] { "Stock", "Sales" })
                ById<CheckBox>(window, "OneCPart" + part).IsChecked = false; // остаётся «Номенклатура»: одна таблица — один CSV, без сервера
            ById<RadioButton>(window, "OneCFormatCsv").IsChecked = true;
            ById<Button>(window, "OneCExportButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            var until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(path) && DateTime.UtcNow < until)
                UiShot.Settle(2, 50);
            UiShot.Settle(6, 50);

            suggested.Should().EndWith(".csv");
            File.Exists(path).Should().BeTrue("файл записан по пути из диалога");
            var bytes = File.ReadAllBytes(path);
            bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "CSV — UTF-8 с BOM, как ждут Excel и 1С");
            UiDrive.AllText(window).Should().Contain("Готово").And.Contain(path);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { File.Delete(path); } catch { /* временный файл */ }
        }
    }

    [AvaloniaFact]
    public async Task OneCExport_NoPartsOrBadPeriod_ShowsMessage_AndWritesNothing()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.AnalyticsExportUnlocked = true;
        var picked = false;
        OneCExportWindow.SavePathOverride = _ => { picked = true; return null; };
        var window = new OneCExportWindow();
        try
        {
            window.Show();
            UiShot.Settle(4);
            foreach (var box in window.GetVisualDescendants().OfType<CheckBox>())
                box.IsChecked = false;
            ById<Button>(window, "OneCExportButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Отметьте, что выгрузить");
            picked.Should().BeFalse("диалог сохранения не открывался");

            ById<CheckBox>(window, "OneCPartSales").IsChecked = true;
            ById<CalendarDatePicker>(window, "OneCFrom").SelectedDate = DateTime.Today.AddDays(5);
            ById<Button>(window, "OneCExportButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Проверьте период");
            picked.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ 2026-11-26: стресс-тест окна

    [AvaloniaTheory]
    [InlineData(1024, 600)]
    [InlineData(800, 600)]
    public async Task OneCExport_MainControlsVisibleWithoutScrolling(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
        var window = new OneCExportWindow();
        try
        {
            ServiceFormsFitUiTests.ShowLikeInApp(window, new UiSize(w, h));
            UiShot.Settle(4);
            foreach (var id in new[] { "OneCFrom", "OneCLastMonth", "OneCPartCounterparties", "OneCFormatXml" })
            {
                var c = window.GetVisualDescendants().OfType<Control>().Single(x => Avalonia.Automation.AutomationProperties.GetAutomationId(x) == id);
                var scroll = c.GetVisualAncestors().OfType<ScrollViewer>().First();
                var bottom = Avalonia.VisualExtensions.TranslatePoint(c, new Avalonia.Point(0, c.Bounds.Height), scroll)!.Value.Y;
                bottom.Should().BeLessThanOrEqualTo(scroll.Viewport.Height + 1, $"«{id}» виден без прокрутки на {w}×{h} (раньше «Формат» уходил вниз)");
            }
            foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && (Avalonia.Automation.AutomationProperties.GetAutomationId(b) ?? "").StartsWith("OneC", StringComparison.Ordinal)))
                b.Bounds.Height.Should().BeGreaterThanOrEqualTo(40, "сенсорная цель не меньше 40 px");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OneCExport_LastMonthButton_And_SettingsRememberedForNextTime()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.AnalyticsExportUnlocked = true;
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onec-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OneCExportWindow.SavePathOverride = _ => path;
        var window = new OneCExportWindow();
        try
        {
            window.Show();
            UiShot.Settle(4);
            ById<Button>(window, "OneCLastMonth").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var (lf, lt) = OneCExport.LastMonth(DateTime.Today);
            ById<CalendarDatePicker>(window, "OneCFrom").SelectedDate.Should().Be(lf);
            ById<CalendarDatePicker>(window, "OneCTo").SelectedDate.Should().Be(lt);

            foreach (var part in new[] { "Stock", "Sales" })
                ById<CheckBox>(window, "OneCPart" + part).IsChecked = false;
            ById<RadioButton>(window, "OneCFormatCsv").IsChecked = true;
            ById<Button>(window, "OneCExportButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(path) && DateTime.UtcNow < until)
                UiShot.Settle(2, 50);
            UiShot.Settle(6, 50);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { File.Delete(path); } catch { /* временный файл */ }
        }

        var again = new OneCExportWindow();
        try
        {
            again.Show();
            UiShot.Settle(4);
            ById<CheckBox>(again, "OneCPartProducts").IsChecked.Should().BeTrue("в прошлый раз выгружали номенклатуру");
            ById<CheckBox>(again, "OneCPartSales").IsChecked.Should().BeFalse("продажи в прошлый раз сняли — окно это помнит");
            ById<RadioButton>(again, "OneCFormatCsv").IsChecked.Should().BeTrue("формат запомнился");
        }
        finally
        {
            again.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OneCExport_CancelDuringExport_KeepsWindowAndOldFile()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.AnalyticsExportUnlocked = true;
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onec-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "прежняя выгрузка");
        OneCExportWindow.SavePathOverride = _ => path;
        var reached = false;
        OneCExportWindow.BeforeWriteForTests = async ct => { reached = true; await Task.Delay(Timeout.Infinite, ct); };
        var window = new OneCExportWindow();
        try
        {
            window.Show();
            UiShot.Settle(4);
            foreach (var part in new[] { "Stock", "Sales" })
                ById<CheckBox>(window, "OneCPart" + part).IsChecked = false;
            ById<RadioButton>(window, "OneCFormatCsv").IsChecked = true;
            ById<Button>(window, "OneCExportButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!reached && DateTime.UtcNow < until)
                UiShot.Settle(2, 50);
            reached.Should().BeTrue();
            var close = ById<Button>(window, "OneCCloseButton");
            UiDrive.LabelOf(close).Should().Contain("Отменить", "во время выгрузки «Закрыть» становится «Отменить»");
            ById<CheckBox>(window, "OneCPartProducts").IsEnabled.Should().BeFalse("поля не меняются во время выгрузки");
            close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8, 50);

            window.IsVisible.Should().BeTrue("отмена не закрывает окно");
            UiDrive.AllText(window).Should().Contain("отменена");
            UiDrive.LabelOf(close).Should().Contain("Закрыть");
            File.ReadAllText(path).Should().Be("прежняя выгрузка", "отменённая выгрузка не трогает файл");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { File.Delete(path); } catch { /* временный файл */ }
        }
    }

    [AvaloniaFact]
    public async Task FillBase_SendOpensWaMeWithReadyText_AndRejectsIncompleteForm()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var opened = new List<string>();
        FillBaseRequestWindow.OpenUrlOverride = url => { opened.Add(url); return true; };
        var window = new FillBaseRequestWindow();
        try
        {
            window.Show();
            UiShot.Settle(4);
            ById<TextBox>(window, "FillBaseShop").Text = "Магазин «Нур»";
            ById<TextBox>(window, "FillBasePhone").Text = "12"; // слишком короткий
            ById<Button>(window, "FillBaseSendButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4);
            opened.Should().BeEmpty("форма не заполнена — WhatsApp не открывается");
            UiDrive.AllText(window).Should().Contain("телефон");

            ById<TextBox>(window, "FillBasePhone").Text = "0700 123 456";
            ById<CheckBox>(window, "FillBaseOneC").IsChecked = true;
            ById<TextBox>(window, "FillBaseLink").Text = "https://drive.google.com/drive/folders/abc";
            ById<Button>(window, "FillBaseSendButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4);

            opened.Should().ContainSingle();
            opened[0].Should().StartWith("https://wa.me/996221068796?text=");
            var text = Uri.UnescapeDataString(opened[0][(opened[0].IndexOf("?text=", StringComparison.Ordinal) + 6)..]);
            text.Should().Contain("Магазин «Нур»").And.Contain("0700 123 456").And.Contain("https://drive.google.com/drive/folders/abc");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OperationsCards_OwnerSeesExportAndTurnkey_KassaWithoutPermissionDoesNot()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        try
        {
            IsOwnerProp.SetValue(null, true);
            var owner = new OperationsSettingsView();
            var host = new Window { Content = owner, Width = 1100, Height = 700 };
            try
            {
                host.Show();
                UiShot.Settle(4);
                owner.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OneCExportCard").IsVisible.Should().BeTrue("в программе владельца карточка «Экспорт в 1С» есть");
                ById<Button>(host, "OneCExportOpenButton").Should().NotBeNull();
                ById<Button>(host, "FillBaseServiceButton").Should().NotBeNull("рядом с переносом данных — услуга «под ключ»");
            }
            finally
            {
                host.Close();
                UiShot.Pump();
            }

            IsOwnerProp.SetValue(null, false);
            var kassa = new OperationsSettingsView();
            var kassaHost = new Window { Content = kassa, Width = 1100, Height = 700 };
            try
            {
                kassaHost.Show();
                UiShot.Settle(4);
                kassa.GetVisualDescendants().OfType<Border>().Should().NotContain(b => b.Name == "TransferDataCard", "перенос данных — только у владельца");
            }
            finally
            {
                kassaHost.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
        }
    }
}
