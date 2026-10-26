using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (пакет Q2): склад → «Приёмка» → «Оприходовать» ставит приход в BackgroundReceivingQueue
/// и не держит окно. Здесь — связь окна с очередью: завершение задач этого окна (этикетки на принятые размеры), перерисовка плиток после
/// обновления каталога очередью, текст «Оприходую…» на кнопке и точка входа «Вернуть строки в приёмку» для окна «Приходы в фоне» (Q5).
/// Само проведение, остатки плиток, каталог и долг поставщику делает очередь.</summary>
public partial class WarehouseWindow
{
    // Открытые окна склада с приёмкой: «Вернуть строки» из окна очереди вставляет строки в самое новое из них.
    private static readonly List<WarehouseWindow> OpenReceivingWindows = new();
    private bool _receivingBgInit;

    private void InitReceivingBackground()
    {
        if (_receivingBgInit)
            return;
        _receivingBgInit = true;
        OpenReceivingWindows.Add(this);
        // 2026-10-25: подтверждение «Цена продажи изменится у K товаров» перед «Оприходовать» — до постановки в очередь
        // (обработчик ConfirmSalePriceChangesAsync был написан в пакете Б1, но к модели не подключён).
        _viewModel.ConfirmSalePriceChanges ??= ConfirmSalePriceChangesAsync;
        BackgroundReceivingQueue.Instance.JobFinished += OnReceivingQueueJobFinished;
        BackgroundReceivingQueue.Instance.CatalogRefreshed += OnReceivingQueueCatalogRefreshed;
        ReceivingCommitBusyText.Text = Tr.T("Оприходую…", "Кабыл алып жатам…", "Receiving…", "Stoğa alıyorum…", "Kirim qilyapman…");
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.IsReceivingPosting) && _viewModel.IsReceivingPosting)
                ReceivingCommitBusyText.Text = Tr.T("Оприходую…", "Кабыл алып жатам…", "Receiving…", "Stoğa alıyorum…", "Kirim qilyapman…");
        };
    }

    /// <summary>Окно закрыто: отписка от очереди (задачи в очереди не теряются — они идут дальше без окна).</summary>
    private void ReceivingBackgroundOnClosed()
    {
        if (!_receivingBgInit)
            return;
        BackgroundReceivingQueue.Instance.JobFinished -= OnReceivingQueueJobFinished;
        BackgroundReceivingQueue.Instance.CatalogRefreshed -= OnReceivingQueueCatalogRefreshed;
        OpenReceivingWindows.Remove(this);
        _receivingBgInit = false;
    }

    // События очереди приходят не из потока UI.
    private void OnReceivingQueueJobFinished(ReceivingJob job) =>
        Dispatcher.UIThread.Post(() => _viewModel.OnReceivingJobFinished(job));

    private void OnReceivingQueueCatalogRefreshed() =>
        Dispatcher.UIThread.Post(() => _ = _viewModel.EnsureCatalogLoadedAsync());

    /// <summary>Точка входа для «Вернуть строки в приёмку» (окно «Приходы в фоне», пакет Q5): строки из
    /// <see cref="BackgroundReceivingQueue.TakeBackLines"/> встают в список приёмки открытого окна склада, вкладка «Приёмка» выбирается.
    /// Вызывать в потоке UI. false — окно склада не открыто (строки НЕ потеряны только если вызывающий ещё не вызвал TakeBackLines:
    /// сначала проверьте <see cref="HasOpenReceivingWindow"/>, потом забирайте строки).</summary>
    public static bool TryRestoreReceivingLines(IReadOnlyList<ReceivingLineVm> lines)
    {
        var window = OpenReceivingWindows.LastOrDefault();
        if (window is null)
            return false;
        window._viewModel.RestoreReceivingLines(lines);
        window.WarehouseTabs.SelectedItem = window.ReceivingTabItem;
        return true;
    }

    /// <summary>Есть открытое окно склада, куда можно вернуть строки (см. <see cref="TryRestoreReceivingLines"/>).</summary>
    public static bool HasOpenReceivingWindow => OpenReceivingWindows.Count > 0;
}
