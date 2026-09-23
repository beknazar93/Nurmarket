using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Отдельное окно ABC-анализа.
///
/// Вынесено из «Продаж» и «Финансов» в самостоятельный раздел: там ABC был зажат в нижнюю треть
/// окна рядом с таблицей чеков, а у него пять срезов, по три диаграммы и полный список товаров в
/// каждом — на такое нужно всё окно, иначе всё время приходится скроллить.
///
/// Данные считаются локально (AnalyticsReportData), поэтому раздел работает и без интернета.</summary>
public partial class AbcAnalysisWindow : Window
{
    private DateTime _from = DateTime.Today.AddDays(-30);
    private DateTime _to = DateTime.Today;
    private CancellationTokenSource? _cts;
    private bool _suppressPickerEvents;

    public AbcAnalysisWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        PosDataEvents.SalesChanged += OnSalesChangedExternally;
        ExcelButton.IsEnabled = TariffGate.CanUseAnalyticsExport;
        WordButton.IsEnabled = TariffGate.CanUseAnalyticsExport;
        SyncPickers();
        await ReloadAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await ReloadAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Today_Click(object? sender, RoutedEventArgs e) => await SetPeriodAsync(DateTime.Today, DateTime.Today);

    private async void Week_Click(object? sender, RoutedEventArgs e) => await SetPeriodAsync(DateTime.Today.AddDays(-6), DateTime.Today);

    private async void Month_Click(object? sender, RoutedEventArgs e) => await SetPeriodAsync(DateTime.Today.AddDays(-29), DateTime.Today);

    private async void Quarter_Click(object? sender, RoutedEventArgs e) => await SetPeriodAsync(DateTime.Today.AddDays(-89), DateTime.Today);

    private async void Period_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // SyncPickers сам меняет SelectedDate и снова поднял бы это событие — без флага получалась
        // бы лишняя перезагрузка на каждый программный сдвиг периода.
        if (_suppressPickerEvents)
            return;

        if (FromPicker.SelectedDate is { } from)
            _from = from.Date;
        if (ToPicker.SelectedDate is { } to)
            _to = to.Date;

        if (_from > _to)
            (_from, _to) = (_to, _from);

        SyncPickers();
        await ReloadAsync();
    }

    private async Task SetPeriodAsync(DateTime from, DateTime to)
    {
        _from = from;
        _to = to;
        SyncPickers();
        await ReloadAsync();
    }

    private void SyncPickers()
    {
        _suppressPickerEvents = true;
        FromPicker.SelectedDate = _from;
        ToPicker.SelectedDate = _to;
        _suppressPickerEvents = false;

        var days = (_to - _from).Days + 1;
        PeriodText.Text = $"период: {days} дн.";
    }

    private CancellationTokenSource? _liveCts;

    /// <summary>Пересчёт по сигналу «продажи изменились». С задержкой: за один чек сигнал
    /// приходит несколько раз, а при выгрузке офлайн-очереди — по разу на чек, и без неё
    /// раздел перезагружался бы десятки раз подряд. Сигнал приходит из фонового потока,
    /// поэтому уходим на UI-поток.</summary>
    private void OnSalesChangedExternally()
    {
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _liveCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, cts.Token).ConfigureAwait(false);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await ReloadAsync());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Живое обновление ABC не выполнено: {ex.Message}", "WARNING");
            }
        });
    }

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            ShowError(null);
            var from = _from;
            var to = _to;
            // Расчёт читает всю историю продаж за период — на большом магазине в UI-потоке это
            // заметно подвесило бы окно.
            var data = await Task.Run(() => AnalyticsReportData.Build(from, to), cts.Token)
                .ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();

            AbcSection.Update(data);
        }
        catch (OperationCanceledException)
        {
            // Период переключили, пока считался прошлый — результат уже не нужен.
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ABC-анализ не построен: {ex}", "WARNING");
            ShowError("Не удалось построить ABC-анализ: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }

    private async void ExportExcel_Click(object? sender, RoutedEventArgs e) => await ExportAsync(toWord: false);

    private async void ExportWord_Click(object? sender, RoutedEventArgs e) => await ExportAsync(toWord: true);

    private async Task ExportAsync(bool toWord)
    {
        if (!TariffGate.CanUseAnalyticsExport)
        {
            ShowError(TariffGate.AnalyticsExportLockedMessage);
            return;
        }

        var extension = toWord ? "docx" : "xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = toWord ? "Сохранить отчёт в Word" : "Сохранить отчёт в Excel",
            SuggestedFileName = $"abc-{_from:yyyy-MM-dd}_{_to:yyyy-MM-dd}.{extension}",
            FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
        });
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            ShowError("Не удалось определить путь файла — выберите папку на этом компьютере.");
            return;
        }

        try
        {
            ShowError("Готовлю отчёт…");
            var data = await Task.Run(() => AnalyticsReportData.Build(_from, _to)).ConfigureAwait(true);
            var shop = UserPreferences.Instance.StoreName;

            await Task.Run(() =>
            {
                if (toWord)
                    AnalyticsExportService.ExportToWord(path!, data, shop);
                else
                    AnalyticsExportService.ExportToExcel(path!, data, shop);
            }).ConfigureAwait(true);

            ShowError($"Отчёт сохранён: {path}");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка ABC не удалась: {ex}", "WARNING");
            ShowError("Не удалось сохранить отчёт: " + ex.Message);
        }
    }

    /// <summary>Расчёт ABC читает всю историю продаж и идёт в фоне. Без отмены при закрытии
    /// окно закрывалось, а расчёт продолжал грузить процессор и диск до конца, после чего
    /// продолжение писало в контролы уже закрытого окна.</summary>
    protected override void OnClosed(EventArgs e)
    {
        PosDataEvents.SalesChanged -= OnSalesChangedExternally;
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        _liveCts = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorBox.IsVisible = !string.IsNullOrEmpty(message);
    }
}
