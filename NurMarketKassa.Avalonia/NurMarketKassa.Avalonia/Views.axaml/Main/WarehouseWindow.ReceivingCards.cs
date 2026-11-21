using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21 (владелец: «Массовое добавление и Приёмку ОБЪЕДИНИТЬ! И дизайн сделать как на фото»): строки «Склад → Приёмка» — карточки (слева галочка, штрихкод и источник, широкое название, ниже в одну линию поля
/// «Единица / Закупка / Наценка / Цена продажи / Категория», справа «Кол-во» с «−» «+» и «×»). Источник строк — те же <c>ReceivingLines</c>, что и раньше: сумма, партии, единицы, штрихкоды и проведение
/// (WarehouseViewModel.CommitReceivingAsync) не менялись, меняется только вид. Карточку строит <see cref="BuildBulkRow"/> (общий код с бывшей вкладкой «Массовое добавление»), обёртка <see cref="BulkScanRow"/> даёт
/// галочку, источник и категорию. Прежняя таблица ReceivingGrid скрыта и больше строк не показывает.</summary>
public partial class WarehouseWindow
{
    private readonly Dictionary<ReceivingLineVm, BulkScanRow> _rcvRows = new();

    private BulkScanRow RcvRow(ReceivingLineVm line)
    {
        if (!_rcvRows.TryGetValue(line, out var row))
            _rcvRows[line] = row = new BulkScanRow(line);
        return row;
    }

    /// <summary>Строки для «Действий над отмеченными» — все строки приёмки.</summary>
    private List<BulkScanRow> BulkOpsRows() => _viewModel.ReceivingLines.Select(RcvRow).ToList();

    private ReceivingLineVm? _rcvSelectedLine;
    private event Action? _rcvDetailsChanged;

    private void RcvRemoveRow(BulkScanRow row) => ReceivingRemoveWithUndo(row.Line);

    private void InitReceivingCards(Control bulkBody)
    {
        // Поле «Действия над отмеченными», строка итога последнего действия и шапка колонок берутся из собранного тела бывшей вкладки и переезжают в «Приёмку».
        static void Detach(Control c) => (c.Parent as Panel)?.Children.Remove(c);
        Detach(_bulkOpsToggle);
        Detach(_bulkOpsPanel);
        Detach(_bulkLastScan);
        Detach(_bulkHeader);
        ReceivingSupplierWrap.Children.Add(_bulkOpsToggle); // в ряду «Поставщик» — экономим строку на низком окне
        ReceivingSupplierBlock.Children.Add(_bulkOpsPanel);
        ReceivingSupplierBlock.Children.Add(_bulkLastScan);
        _bulkOpsToggle.IsVisible = false;
        _bulkHeader.IsVisible = false;

        ReceivingCards.ItemTemplate = new FuncDataTemplate<ReceivingLineVm>((line, _) => line is null ? new Control() : BuildBulkRow(RcvRow(line)));
        ReceivingCards.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ReceivingCards.Template = new FuncControlTemplate<ItemsControl>((_, scope) =>
        {
            var presenter = new ItemsPresenter { Name = "PART_ItemsPresenter", ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) }.RegisterInNameScope(scope);
            var scroll = new ScrollViewer
            {
                Name = "ReceivingCardsScroll",
                Content = presenter,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
            var dock = new DockPanel();
            DockPanel.SetDock(_bulkHeader, Dock.Top);
            dock.Children.Add(_bulkHeader);
            dock.Children.Add(scroll);
            return dock;
        });
        // 2026-11-21: узкая раскладка (панель «Наценка для всех» уезжает в ряд кнопок, итоги короче) раньше включалась по ширине таблицы — теперь по ширине списка карточек.
        ReceivingCards.SizeChanged += (_, e) => ApplyReceivingColumns(e.NewSize.Width);
        _viewModel.ReceivingLines.CollectionChanged += (_, e) =>
        {
            var any = _viewModel.ReceivingLines.Count > 0;
            _bulkOpsToggle.IsVisible = any;
            _bulkHeader.IsVisible = any;
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace && e.OldItems is { } old)
                foreach (var l in old.OfType<ReceivingLineVm>())
                    _rcvRows.Remove(l);
            else if (e.Action == NotifyCollectionChangedAction.Reset)
                _rcvRows.Clear();
            BulkSelectionInfo();
        };
        ReceivingCards.AddHandler(InputElement.GotFocusEvent, (_, _) =>
        {
            if (FocusedReceivingLine() is { } focusedLine)
                _rcvSelectedLine = focusedLine;
        }, RoutingStrategies.Bubble);
        // Enter / Tab в поле карточки — цепочка прежней таблицы: название → «Принимаем» → («шт/упак.») → закупка → цена продажи → поле скана; Esc — откат правки поля (как в ячейке таблицы).
        ReceivingCards.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Source is not TextBox box || box.Tag is not string field || e.KeyModifiers != KeyModifiers.None || RcvCardOf(box) is not { } card || card.DataContext is not BulkScanRow row)
                return;
            var line = row.Line;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                line.CancelCellEdit();
                line.BeginCellEdit(); // курсор остался в поле — правка продолжается с прежнего значения
                RefreshReceivingSummary();
                box.SelectAll();
                return;
            }
            if (e.Key is not (Key.Enter or Key.Tab))
                return;
            string[] order = ["name", "qty", "pieces", "purchase", "markup", "sale"];
            var at = Array.IndexOf(order, field);
            if (field == "barcode")
                at = -1; // из штрихкода — в название
            else if (at < 0)
                return;
            e.Handled = true;
            EndReceivingCardEdit(box);
            TextBox? next = null;
            for (var i = at + 1; i < order.Length && next is null; i++)
            {
                if (order[i] == "markup")
                    continue; // наценку в цепочке не трогаем (как в таблице): после закупки — цена продажи
                next = card.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, order[i]) && t.IsEffectivelyVisible && !t.IsReadOnly);
            }
            if (field == "barcode")
                next = card.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, "name") && t.IsEffectivelyVisible && !t.IsReadOnly);
            RefreshReceivingSummary();
            Dispatcher.UIThread.Post(() =>
            {
                if (next is null)
                {
                    ReceivingScanBox.Focus();
                    ReceivingScanBox.SelectAll();
                }
                else
                {
                    next.Focus();
                    next.SelectAll();
                }
            }, DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Внешняя рамка карточки, в которой лежит элемент (рамка с <see cref="BulkScanRow"/> в DataContext, чей родитель уже не карточка).</summary>
    private static Border? RcvCardOf(Visual v) => v.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is BulkScanRow && b.GetVisualParent()?.DataContext is not BulkScanRow);

    /// <summary>Строка приёмки, в карточке которой сейчас курсор; null — курсор вне карточек.</summary>
    private ReceivingLineVm? FocusedReceivingLine() =>
        FocusManager?.GetFocusedElement() is Visual v && RcvCardOf(v)?.DataContext is BulkScanRow row ? row.Line : null;

    /// <summary>Поле карточки получило курсор: значения строки запоминаются (для Esc и для пустого поля при выходе), число выделяется — набор заменяет его.</summary>
    private void HookReceivingCell(TextBox box, ReceivingLineVm line)
    {
        box.GotFocus += (_, _) =>
        {
            line.BeginCellEdit();
            box.SelectAll();
            // нажатие мышью ставит курсор уже после получения фокуса и снимает выделение — выделяем число ещё раз после обработки нажатия (набор заменяет число)
            Dispatcher.UIThread.Post(() =>
            {
                if (box.IsFocused)
                    box.SelectAll();
            }, DispatcherPriority.Input);
        };
        box.LostFocus += (_, _) => EndReceivingCardEdit(box);
    }

    /// <summary>Выход из поля карточки (то же, что окончание правки ячейки таблицы): отказ от штрихкода/великана в числе, пустое поле — прежнее значение, пустое название, итоги.</summary>
    private void EndReceivingCardEdit(TextBox box)
    {
        if (RcvCardOf(box)?.DataContext is not BulkScanRow row || box.Tag is not string field)
            return;
        var line = row.Line;
        if (!line.InCellEdit)
        {
            line.CommitText();
            return;
        }
        var rejectedCode = line.TakeRejectedBarcode();
        if (rejectedCode is not null)
        {
            line.CancelCellEdit(); // медленный сканер напечатал код в поле числа: правка откатывается, код обрабатывается как обычный скан
            Dispatcher.UIThread.Post(() => _ = ScanIntoReceivingAsync(rejectedCode), DispatcherPriority.Background);
        }
        else
        {
            if (line.PriceProblemText is { Length: > 0 } rejected
                && (rejected == ReceivingLineVm.BarcodeInCellText || rejected == ReceivingLineVm.PriceTooBigText
                    || rejected == ServerPriceLimits.SaleTooBigText || rejected == ServerPriceLimits.PurchaseTooBigText))
                _viewModel.ShowReceivingInfo(rejected);
            line.EndCellEdit();
            if (field == "qty")
                _viewModel.OnReceivingQuantityEdited(line); // ручная правка «Принимаем» не сбрасывает «тот же код подряд»
        }
        if (field == "name" && line.CommitName())
            _viewModel.ShowReceivingInfo(Tr.T("Название не может быть пустым — вернули прежнее.", "Аты бош болбошу керек — мурункусу кайтарылды.",
                "The name cannot be empty — the previous one was restored.", "Ad boş olamaz — önceki geri yüklendi.", "Nom bo'sh bo'lmasligi kerak — avvalgisi qaytarildi."));
        RefreshReceivingSummary();
    }

    /// <summary>Дописать правку поля, в котором курсор (перед «Оприходовать», F2, «+ Новый товар»): раньше это делала таблица (CommitEdit).</summary>
    private void CommitReceivingEdit()
    {
        if (FocusManager?.GetFocusedElement() is TextBox box && RcvCardOf(box) is not null)
            EndReceivingCardEdit(box);
    }

    /// <summary>Отменить начатую правку (перед удалением строки): снимок правки забывается, строка уйдёт без отката.</summary>
    private void CancelReceivingEdit() => FocusedReceivingLine()?.ForgetCellEdit();

    /// <summary>Курсор в поле карточки строки (тег: name / qty / pieces / purchase / markup / sale); карточку ждём, пока таблица её построит (виртуализация).</summary>
    private void FocusReceivingCard(ReceivingLineVm line, string tag, int attempt = 0)
    {
        if (!_viewModel.ReceivingLines.Contains(line))
            return;
        var card = ReceivingCards.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.DataContext is BulkScanRow r && ReferenceEquals(r.Line, line));
        var box = card?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, tag) && t.IsEffectivelyVisible && !t.IsReadOnly);
        if (box is null)
        {
            if (attempt < 8)
                Dispatcher.UIThread.Post(() => FocusReceivingCard(line, tag, attempt + 1), DispatcherPriority.Background);
            return;
        }
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
    }
}
