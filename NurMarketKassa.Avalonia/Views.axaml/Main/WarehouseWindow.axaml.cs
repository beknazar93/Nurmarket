using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class WarehouseWindow : Window
{
    private readonly IBarcodeInputService _barcodeInputService;
    private readonly WarehouseViewModel _viewModel;

    public string? InitialBarcode { get; set; }

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public WarehouseWindow() : this(
        ResolveService<WarehouseViewModel>(),
        ResolveService<IBarcodeInputService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public WarehouseWindow(WarehouseViewModel viewModel, IBarcodeInputService barcodeInputService)
    {
        _viewModel = viewModel;
        _barcodeInputService = barcodeInputService;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.RevisionLineAdded += OnRevisionLineAdded;
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
        try
        {
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(InitialBarcode))
                _viewModel.HandleBarcodeScan(InitialBarcode.Trim(), isRevisionTab: true);
        }
        catch (Exception ex)
        {
            PosMessageBox.Show(this, $"Не удалось загрузить каталог: {ex.Message}", "Склад",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void FocusWithBarcode(string? barcode)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
            _viewModel.HandleBarcodeScan(barcode.Trim(), isRevisionTab: true);
        Activate();
        Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        _viewModel.RevisionLineAdded -= OnRevisionLineAdded;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e) =>
        _barcodeInputService.ProcessKeyDown(e);

    private void OnBarcodeScanned(string barcode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var isRevisionTab = WarehouseTabs.SelectedIndex == 0;
            _viewModel.HandleBarcodeScan(barcode, isRevisionTab);
            if (!isRevisionTab)
                WriteOffQuantityBox?.Focus();
        });
    }

    private void OnRevisionLineAdded(RevisionLineVm line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRevisionLineAdded(line));
            return;
        }

        RevisionGrid.SelectedItem = line;
        RevisionGrid.ScrollIntoView(line, null);
    }

    #region Window Controls & Dragging

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Позволяет перетаскивать кастомное окно за шапку или пустые области.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    #endregion
}