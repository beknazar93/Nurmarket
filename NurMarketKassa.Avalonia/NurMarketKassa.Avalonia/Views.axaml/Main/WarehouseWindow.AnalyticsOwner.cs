using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса WarehouseWindow (AnalyticsOwner): перенесено из WarehouseWindow.axaml.cs без изменений логики.
public partial class WarehouseWindow
{
    #region Аналитика склада/товаров

    /// <summary>Плитка итога под таблицей товаров (RefreshWarehouseTotals).</summary>
    private sealed class KpiCardVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private static double ParsePriceValue(string? priceLine) =>
        LocalCartService.ParsePrice(priceLine ?? "");

    /// <summary>Считается локально из уже загруженного каталога. Сам расчёт и разметка — в
    /// WarehouseStockAnalyticsView (2026-09-27): тот же блок стоит в разделе «Аналитика»
    /// программы владельца.</summary>
    private void RefreshAnalytics() => StockAnalyticsView.Refresh();

    #endregion

    #region ABC склада (программа владельца)

    private bool _isOwnerSection;
    private TabItem? _ownerAbcTab;
    private AbcSectionView? _stockAbc;
    private System.Threading.CancellationTokenSource? _stockAbcCts;

    /// <summary>Вкладка «ABC» склада в программе владельца (2026-09-27, «склад у склада ABC»):
    /// один срез — «Склад по стоимости остатка». Стоит там, где в кассе стоит «Аналитика»
    /// (она сама ушла в раздел «Аналитика»). Как и раздел «ABC-анализ», на тарифе «Старт» её нет.</summary>
    private void AddOwnerAbcTab()
    {
        if (!TariffGate.CanUseSalesAnalytics)
            return;

        _stockAbc = new AbcSectionView
        {
            SliceKeys = [AnalyticsReportData.KeyStock],
            ShowSeasonality = false,
            TableMaxHeight = 520,
        };
        // Разбор товара — за последние 30 дней: у среза склада своего периода нет.
        _stockAbc.ProductAnalyticsRequested += name =>
        {
            try
            {
                new ProductAnalyticsWindow(name, DateTime.Today.AddDays(-29), DateTime.Today).Show(this);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Разбор товара не открылся: {ex}", "WARNING");
            }
        };

        var body = new Border
        {
            CornerRadius = new CornerRadius(0, 14, 14, 14),
            Padding = new Thickness(16),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = _stockAbc,
        };
        body.Bind(Border.BackgroundProperty, body.GetResourceObservable("BrushPanel"));

        _ownerAbcTab = new TabItem { Header = "ABC", Content = body };
        var index = WarehouseTabs.Items.IndexOf(AnalyticsTabItem);
        WarehouseTabs.Items.Insert(index < 0 ? WarehouseTabs.Items.Count : index, _ownerAbcTab);
    }

    /// <summary>Считается при каждом открытии вкладки: остатки меняются приёмкой, списанием и
    /// продажами, а срез — снимок на сейчас. Расчёт в фоне: на большом каталоге это заметно.</summary>
    private async System.Threading.Tasks.Task RefreshStockAbcAsync()
    {
        if (_stockAbc == null)
            return;

        _stockAbcCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _stockAbcCts = cts;
        try
        {
            var slice = await System.Threading.Tasks.Task.Run(() => AnalyticsReportData.BuildStockSlice(), cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            _stockAbc.Update(new AnalyticsReportData { AbcSlices = slice is null ? [] : [slice] });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ABC склада не построен: {ex.Message}", "WARNING");
        }
        finally
        {
            if (ReferenceEquals(_stockAbcCts, cts))
                _stockAbcCts = null;
            cts.Dispose();
        }
    }

    #endregion

    #region Window Controls & Dragging

    /// <summary>2026-10-20, память программы владельца: несохранённую приёмку или ревизию (строки живут только в окне)
    /// и проведение документа оболочка не закрывает при уходе из раздела (IOwnerSectionKeepAlive).</summary>
    public bool KeepAliveWhenHidden =>
        _viewModel.IsBusy || _viewModel.ReceivingLines.Count > 0 || RevisionKeepAlive || _bulk.HasUnsaved || _bulk.IsLoading;

    /// <summary>Склад разделом программы владельца (см. <see cref="IOwnerSection"/>): над разделом
    /// уже написано «Склад», поэтому своя шапка «Управление складом» с кнопками «свернуть» и ✕ не
    /// нужна. Подсказка про сканер остаётся — узкой строкой справа над вкладками.</summary>
    public void AsOwnerSection()
    {
        // Аналитика склада — в разделе «Аналитика» (вкладка «Склад»), здесь вместо неё «ABC».
        _isOwnerSection = true;
        RefreshPaidFeatureVisibility();
        AddOwnerAbcTab();

        WindowTitlePanel.IsVisible = false;
        MinimizeWindowButton.IsVisible = false;
        CloseWindowButton.IsVisible = false;

        WindowHeaderBorder.Background = Avalonia.Media.Brushes.Transparent;
        WindowHeaderBorder.BoxShadow = default;
        WindowHeaderBorder.Padding = new Thickness(0);
        WindowHeaderBorder.Margin = new Thickness(0, 0, 0, 8);
        ScanHintBorder.Margin = new Thickness(0);
        RootGrid.Margin = OwnerSectionLayout.Margin;
        // 2026-10-06, ТЗ ч.12, п. 2.6: кнопка «Дубли штрихкодов» — если они есть (WarehouseWindow.Duplicates.cs).
        _ = ShowBarcodeDuplicatesButtonAsync();
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // 2026-10-23: ✕ окна тоже спрашивает про несохранённый список (раньше закрывал молча; Esc спрашивал).
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => CloseByEscape();

    private void CloseByEscape()
    {
        var receiving = _viewModel.ReceivingLines.Count;
        // 2026-10-24, пакет Р2: подсчёт ревизии сохраняется черновиком сам — окно из-за него не переспрашивает.
        var bulk = _bulk.Rows.Count;
        if (receiving + bulk > 0)
        {
            var parts = new List<string>();
            if (bulk > 0)
                parts.Add(Tr.T($"массовое добавление — {bulk}", $"массалык кошуу — {bulk}", $"bulk add — {bulk}", $"toplu ekleme — {bulk}", $"ommaviy qo'shish — {bulk}"));
            if (receiving > 0)
                parts.Add(Tr.T($"приёмка — {receiving}", $"кабыл алуу — {receiving}", $"receiving — {receiving}", $"mal kabul — {receiving}", $"qabul qilish — {receiving}"));
            var close = PosConfirmDialog.Show(
                this,
                Tr.T("Закрыть склад?", "Кампаны жабасызбы?", "Close the warehouse?", "Depo kapatılsın mı?", "Omborni yopasizmi?"),
                Tr.T("Есть непроведённые строки: ", "Өткөрүлө элек саптар бар: ", "There are unposted lines: ", "İşlenmemiş satırlar var: ", "O'tkazilmagan qatorlar bor: ")
                    + string.Join(", ", parts)
                    + Tr.T(". После закрытия они пропадут.", ". Жабылгандан кийин алар жоголот.", ". They will be lost after closing.", ". Kapattığınızda bunlar kaybolacak.", ". Yopilgandan keyin ular yo'qoladi."),
                confirmText: Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
                cancelText: Tr.T("Остаться", "Калуу", "Stay", "Vazgeç", "Qolish"));
            if (!close)
                return;
        }

        Close();
    }

    private void ExpandWriteOffHistory_Click(object? sender, RoutedEventArgs e) =>
        WriteOffHistoryWindow.Open(this);

    /// <summary>
    /// Позволяет перетаскивать кастомное окно за шапку или пустые области.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 2026-10-30, владелец: «на складе сортировка при нажатии не работает». Нажатие на заголовок столбца таблицы не помечено «обработано»,
        // доходило сюда и запускало перетаскивание окна: система забирала мышь до отпускания кнопки, заголовок не получал «отпускание» — сортировка
        // (срабатывает именно на нём) не запускалась. Внутри таблицы и выпадающих списков (в т. ч. «Сортировка» в плитках) окно не тянем.
        if (e.Source is Visual src && (src.FindAncestorOfType<DataGrid>(includeSelf: true) is not null || src.FindAncestorOfType<ComboBox>(includeSelf: true) is not null))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    #endregion
}
