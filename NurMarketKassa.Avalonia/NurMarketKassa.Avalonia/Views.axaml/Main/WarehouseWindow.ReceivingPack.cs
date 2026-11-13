using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ, если поместится!»): «Склад → Приёмка» — способ приёмки на верхней панели рядом с «Наценка для всех» и «Сразу к скану»:
/// «Приём: [В штуках | Упаковками | Поштучная продажа] [Ко всем строкам] [шт в упак.]». Если сегменты не помещаются — одна кнопка «Приём: … ▾» (окно с тремя пунктами)
/// на той же панели, а на узком окне, где панель уходит в ряд кнопок (ApplyReceivingCompactLayout), — в ряду «Поставщик» (2026-11-12, аудит Д-4: отдельный ряд кнопки отнимал у таблицы
/// 52 px — на 1024×600 оставалось 1,5 строки). Панель по высоте не растёт.
/// Сам переключатель и надписи — в ReceivingPackBar (общий с «Закупки → Приём товара»); здесь только размещение и подключение к списку строк.</summary>
public partial class WarehouseWindow
{
    /// <summary>Запас справа от панели, который нужен подсказке «Как работает…» (она сжимается до «…», но не до нуля).</summary>
    private const double ReceivingHintMinWidth = 80;

    /// <summary>Гистерезис: из кнопки обратно в сегменты — только когда места на этот запас больше, чем нужно (на самом пороге вид не мигает).</summary>
    private const double ReceivingPackHysteresis = 60;

    private ReceivingPackBar? _rcvPackBar;

    /// <summary>Подключение (из InitReceivingKeys): переключатель на верхней панели, способ по умолчанию — новым строкам, «Ко всем строкам».</summary>
    private void InitReceivingPackBar()
    {
        var bar = _rcvPackBar = new ReceivingPackBar(
            lines: () =>
            {
                ReceivingGrid.CommitEdit();
                return _viewModel.ReceivingLines.ToList();
            },
            confirm: (message, ok) => PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Способ приёмки", "Кабыл алуу ыкмасы", "How to receive", "Kabul yolu", "Qabul usuli"), message, ok,
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary),
            notify: (text, _) => _viewModel.ShowReceivingInfo(text),
            changed: RefreshReceivingSummary,
            focusScan: () => ReceivingScanBox.Focus(),
            makeButton: text =>
            {
                var button = new Button { Content = text, Focusable = false };
                button.Classes.Add("SecondaryButton");
                return button;
            });
        var apply = bar.ApplyToNew;
        _viewModel.NewLineDefaults = apply;
        bar.AttachScanBox(ReceivingScanBox); // 2026-11-12 (Л-9): метка «УПАКОВКАМИ» у поля скана, пока способ не «В штуках»
        // 2026-11-12 (Л-9, решение владельца): приход поставлен в очередь («Оприходовать») — способ возвращается «В штуках». IsReceivingPosting включается ровно после постановки в очередь.
        void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_viewModel.IsReceivingPosting) && _viewModel.IsReceivingPosting)
                bar.ResetAfterPosting();
        }
        _viewModel.PropertyChanged += OnVmChanged;
        Closed += (_, _) =>
        {
            if (_viewModel.NewLineDefaults == apply)
                _viewModel.NewLineDefaults = null; // окно закрыто — модель не держит переключатель закрытого окна
            _viewModel.PropertyChanged -= OnVmChanged;
        };

        // Третья колонка панели — под переключатель; подсказка «Как работает…» (звёздочка) уходит в четвёртую и сжимается первой.
        ReceivingCtlGrid.ColumnDefinitions.Insert(2, new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(ReceivingHintText, 3);
        Grid.SetColumn(bar.Host, 2);
        bar.Host.Margin = new Thickness(14, 0, 0, 0);
        ReceivingCtlGrid.Children.Add(bar.Host);

        void Later() => Dispatcher.UIThread.Post(UpdateReceivingPackPlacement, DispatcherPriority.Background);
        ReceivingCtlPanel.SizeChanged += (_, _) => Later();
        ReceivingCtlPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                Later(); // панель спрятана/показана раскладкой по ширине (ApplyReceivingCompactLayout) — переключатель переезжает вслед
        };
        Later();
        InitReceivingEmptyFit();
    }

    /// <summary>Где и каким видом показать переключатель. Панель видна: сегменты, если они помещаются рядом с «Наценка для всех» и «Сразу к скану» (измеренная ширина, а не константа —
    /// надписи ky/uz длиннее), иначе кнопка на той же панели. Панель спрятана (узкое окно) — кнопка в ряду инструментов вместе с «Наценка для всех» и «Сразу к скану».</summary>
    private void UpdateReceivingPackPlacement()
    {
        if (_rcvPackBar is not { } bar)
            return;
        var panelVisible = ReceivingCtlPanel.IsVisible;
        Panel target = panelVisible ? ReceivingCtlGrid : ReceivingSupplierWrap; // 2026-11-12 (Д-4): узкое окно — в ряду «Поставщик», а не в отдельном ряду кнопок
        if (bar.Host.Parent is Panel old && !ReferenceEquals(old, target))
            old.Children.Remove(bar.Host);
        if (!target.Children.Contains(bar.Host))
            target.Children.Add(bar.Host);
        if (panelVisible)
        {
            Grid.SetColumn(bar.Host, 2);
            bar.Host.Margin = new Thickness(14, 0, 0, 0);
        }
        else
            bar.Host.Margin = new Thickness(6, 0, 10, 8); // 2026-11-12 (Д-6): в ряду отступ слева 6 + общий отступ ряда — «Сразу к скану» и кнопка «Приём» больше не слипаются

        var wide = false;
        if (panelVisible)
        {
            bar.NoteWideMeasured();
            if (bar.WideWidth <= 0)
                wide = true; // ещё не измеряли — начинаем с сегментов, следующая раскладка решит по факту
            else
            {
                var available = ReceivingCtlPanel.Bounds.Width - 26 - ReceivingHintMinWidth; // отступы панели 12 + 12 и рамка 2
                var used = ReceivingMarkupGroup.DesiredSize.Width + ReceivingFastNextCheck.DesiredSize.Width + 14;
                wide = available >= used + bar.WideWidth + (bar.IsWide ? 0 : ReceivingPackHysteresis);
            }
        }
        bar.ShowWide(wide);
    }

    // ------------------------------------------------------------------ пустая таблица (Д-15)

    private bool _rcvEmptyFitting;

    /// <summary>2026-11-12 (аудит Д-15): пояснение пустой таблицы («Отсканируйте первый товар…») на низком окне обрезалось справа и снизу, подсказка клавиш — пополам. Теперь по высоте ячейки
    /// показывается столько, сколько помещается ЦЕЛИКОМ: значок + заголовок + пояснение → без значка → один заголовок → ничего. Перенос по ширине — у самого текста (поля 16 + 16).
    /// Видимостью самой панели по-прежнему управляет «пусто / не пусто» (WarehouseWindow.O9.cs) — здесь только её содержимое.</summary>
    private void InitReceivingEmptyFit()
    {
        ReceivingGrid.SizeChanged += (_, _) => FitReceivingEmptyState();
        ReceivingEmptyState.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                Dispatcher.UIThread.Post(FitReceivingEmptyState, DispatcherPriority.Background);
        };
        Dispatcher.UIThread.Post(FitReceivingEmptyState, DispatcherPriority.Background);
    }

    private void FitReceivingEmptyState()
    {
        if (_rcvEmptyFitting || !ReceivingEmptyState.IsVisible || ReceivingGrid.Bounds.Height < 1)
            return;
        _rcvEmptyFitting = true;
        try
        {
            var available = ReceivingGrid.Bounds.Height - ReceivingGrid.ColumnHeaderHeight - 6;
            var width = Math.Max(120, Math.Min(540, ReceivingGrid.Bounds.Width - 32));
            var icon = ReceivingEmptyState.Children[0];
            ReceivingEmptyState.Width = width;
            // (значок, заголовок, пояснение, поле сверху)
            (bool Icon, bool Title, bool Hint, double Top)[] steps = { (true, true, true, 40), (false, true, true, 8), (false, true, false, 8), (false, false, false, 0) };
            foreach (var step in steps)
            {
                icon.IsVisible = step.Icon;
                ReceivingEmptyTitle.IsVisible = step.Title;
                ReceivingEmptyHint.IsVisible = step.Hint;
                ReceivingEmptyState.Margin = new Thickness(16, step.Top, 16, 6);
                ReceivingEmptyState.Measure(new Size(width, double.PositiveInfinity));
                if (!step.Title || ReceivingEmptyState.DesiredSize.Height <= available)
                    break;
            }
        }
        finally
        {
            _rcvEmptyFitting = false;
        }
    }
}
