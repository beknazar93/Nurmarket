using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (аудит «Закупки — удобство и дизайн», Ф0: БП-5, БП-6 + решение владельца «простая таблица, подробности по раскрытию»): «Склад → Приёмка» —
/// горячие клавиши (F2/Ctrl+F — поле скана, F9/Ctrl+Enter — «Оприходовать», Del — убрать строку с «Вернуть» 5 с, Ctrl+Z), строка подсказок клавиш,
/// крупное «Итого приход» у кнопки, «Подробнее ▸» (колонки «Закупка изменилась», «Как посчитана цена», «Прибыль с шт.» свёрнуты по умолчанию при ширине таблицы
/// меньше 1500, выбор запоминается). Отдельный файл: в остальных — по одной строке подключения.</summary>
public partial class WarehouseWindow
{
    /// <summary>Таблица уже этой ширины — подробные колонки по умолчанию свёрнуты.</summary>
    public const double ReceivingDetailsAutoBelow = 1500;

    /// <summary>Окно ниже этой высоты — строка подсказок клавиш не показывается (она в подсказке кнопки «Оприходовать»).</summary>
    public const double ReceivingKeysHintMinHeight = 700;

    private TextBlock? _rcvTotalValue;
    private Border? _rcvUndoBar;
    private TextBlock? _rcvUndoText;
    private Button? _rcvDetailsButton;
    private (ReceivingLineVm Line, int Index)? _rcvRemoved;
    private DispatcherTimer? _rcvUndoTimer;
    private double _rcvLastWidth;

    /// <summary>Строка подсказок клавиш приёмки (общая с «Закупками»).</summary>
    public static string ReceivingKeysHintText => PurchasesWindow.ReceiveKeysHintText;

    /// <summary>Подключение (из InitReceivingBackground): итог у кнопки, строка клавиш, «Вернуть», «Подробнее ▸», обработчик клавиш вкладки.</summary>
    private void InitReceivingKeys()
    {
        // БП-6 и строка клавиш — в итоговую панель: «Итого приход» перед «Оприходовать», подсказки — второй строкой.
        if (ReceivingCommitButton.Parent is Grid sumGrid)
        {
            var caption = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Text = Tr.T("Итого приход", "Жалпы кирүү", "Receipt total", "Giriş toplamı", "Jami kirim") };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));
            _rcvTotalValue = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
            _rcvTotalValue.Classes.Add("num");
            _rcvTotalValue.Bind(TextBlock.ForegroundProperty, _rcvTotalValue.GetResourceObservable("BrushAccentText"));
            var total = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Children = { caption, _rcvTotalValue } };
            Avalonia.Automation.AutomationProperties.SetName(total, caption.Text);
            sumGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(ReceivingCommitButton, sumGrid.ColumnDefinitions.Count - 1);
            Grid.SetColumn(total, sumGrid.ColumnDefinitions.Count - 2);
            sumGrid.Children.Add(total);
            sumGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            var hint = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = ReceivingKeysHintText };
            hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
            Grid.SetRow(hint, 1);
            Grid.SetColumnSpan(hint, sumGrid.ColumnDefinitions.Count);
            sumGrid.Children.Add(hint);
            // Низкий экран (касса 1024×600): строка клавиш уходит в подсказку кнопки — место под строки таблицы важнее.
            ToolTip.SetTip(ReceivingCommitButton, ReceivingKeysHintText);
            void FitHint(double height) => hint.IsVisible = height <= 0 || height >= ReceivingKeysHintMinHeight;
            SizeChanged += (_, e) => FitHint(e.NewSize.Height);
            FitHint(Bounds.Height);
        }

        // Плашка «Строка убрана · Вернуть» — над таблицей (панель поставщика, ряд 3).
        if (ReceivingGrid.Parent is Grid rcvRoot && rcvRoot.Children.OfType<StackPanel>().FirstOrDefault(p => Grid.GetRow(p) == 3) is { } row3)
        {
            _rcvUndoText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var undo = new Button { Content = Tr.T("Вернуть", "Кайтаруу", "Bring back", "Geri al", "Qaytarish"), Margin = new Thickness(12, 0, 0, 0), MinHeight = 36 };
            undo.Classes.Add("SecondaryButton");
            Avalonia.Automation.AutomationProperties.SetName(undo, Tr.T("Вернуть убранную строку", "Алынган сапты кайтаруу", "Bring back the removed line", "Kaldırılan satırı geri al", "Olib tashlangan qatorni qaytarish"));
            undo.Click += (_, _) => ReceivingUndoRemove();
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _rcvUndoText, undo } };
            Grid.SetColumn(undo, 1);
            _rcvUndoBar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1), IsVisible = false, Child = g };
            _rcvUndoBar.Bind(Border.BackgroundProperty, _rcvUndoBar.GetResourceObservable("BrushPanelSoft"));
            _rcvUndoBar.Bind(Border.BorderBrushProperty, _rcvUndoBar.GetResourceObservable("BrushBorder"));
            row3.Children.Add(_rcvUndoBar);
        }

        // «Подробнее ▸» — в ряд инструментов.
        _rcvDetailsButton = new Button();
        _rcvDetailsButton.Classes.Add("SecondaryButton");
        _rcvDetailsButton.Classes.Add("tool-quiet");
        _rcvDetailsButton.Click += (_, _) =>
        {
            ReceivingViewPrefs.SetDetails(!ReceivingDetailsShown(_rcvLastWidth));
            ApplyReceivingDetails(_rcvLastWidth, relayout: true);
        };
        ReceivingToolsWrap.Children.Add(_rcvDetailsButton);
        SyncReceivingDetailsButton();

        // Содержимое вкладки показывает ContentPresenter TabControl (не сам TabItem) — обработчик вешаем на содержимое.
        if (ReceivingTabItem.Content is Control rcvContent)
            rcvContent.AddHandler(KeyDownEvent, ReceivingHotKey, RoutingStrategies.Tunnel);

        // 2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ»): способ приёмки на верхней панели (WarehouseWindow.ReceivingPack.cs).
        InitReceivingPackBar();
    }

    /// <summary>F2/Ctrl+F, F9/Ctrl+Enter, Del (вне правки ячейки), Ctrl+Z.</summary>
    private void ReceivingHotKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var focused = FocusManager?.GetFocusedElement();
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None || e.Key == Key.F && ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ReceivingGrid.CommitEdit();
            ReceivingScanBox.Focus();
            ReceivingScanBox.SelectAll();
            return;
        }
        if (e.Key == Key.F9 && e.KeyModifiers == KeyModifiers.None || e.Key == Key.Enter && ctrl)
        {
            e.Handled = true;
            ReceivingGrid.CommitEdit();
            if (ReceivingCommitButton.IsEffectivelyEnabled && _viewModel.CommitReceivingCommand.CanExecute(null))
                _viewModel.CommitReceivingCommand.Execute(null);
            return;
        }
        if (e.Key == Key.Z && ctrl && focused is not TextBox && _rcvRemoved is not null)
        {
            e.Handled = true;
            ReceivingUndoRemove();
            return;
        }
        if (e.Key == Key.Delete && focused is not TextBox && ReceivingGrid.SelectedItem is ReceivingLineVm line && ReceivingGrid.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            ReceivingRemoveWithUndo(line);
        }
    }

    /// <summary>Убрать строку (✕ или Del) с плашкой «Вернуть» на 5 с.</summary>
    private void ReceivingRemoveWithUndo(ReceivingLineVm line)
    {
        ReceivingGrid.CancelEdit(); // как «Убрать»: сначала отменить начатую правку (иначе строка вернётся при уходе с вкладки)
        var index = _viewModel.ReceivingLines.IndexOf(line);
        if (index < 0)
            return;
        _viewModel.ReceivingLines.Remove(line);
        RefreshReceivingSummary();
        _rcvRemoved = (line, index);
        if (_rcvUndoBar is null || _rcvUndoText is null)
            return;
        var name = string.IsNullOrWhiteSpace(line.ProductName) ? line.Barcode : line.ProductName;
        _rcvUndoText.Text = Tr.T($"Строка «{name}» убрана.", $"«{name}» сабы алынды.", $"The line “{name}” was removed.", $"«{name}» satırı kaldırıldı.", $"«{name}» qatori olib tashlandi.");
        _rcvUndoBar.IsVisible = true;
        _rcvUndoTimer?.Stop();
        _rcvUndoTimer = new DispatcherTimer { Interval = PurchasesWindow.ReceiveUndoTime };
        _rcvUndoTimer.Tick += (_, _) => ReceivingForgetRemoved();
        _rcvUndoTimer.Start();
    }

    private void ReceivingUndoRemove()
    {
        if (_rcvRemoved is not { } removed)
            return;
        ReceivingForgetRemoved();
        if (!_viewModel.ReceivingLines.Contains(removed.Line))
            _viewModel.ReceivingLines.Insert(Math.Clamp(removed.Index, 0, _viewModel.ReceivingLines.Count), removed.Line);
        RefreshReceivingSummary();
    }

    private void ReceivingForgetRemoved()
    {
        _rcvUndoTimer?.Stop();
        _rcvUndoTimer = null;
        _rcvRemoved = null;
        if (_rcvUndoBar is not null)
            _rcvUndoBar.IsVisible = false;
    }

    /// <summary>БП-6: «Итого приход» (вызов из RefreshReceivingSummary).</summary>
    private void RefreshReceivingTotal(double purchase)
    {
        if (_rcvTotalValue is not null)
            _rcvTotalValue.Text = MoneyText.Som(purchase);
    }

    /// <summary>Показаны ли подробные колонки: выбор кассира (запомнен), без выбора — только на широкой таблице.</summary>
    private static bool ReceivingDetailsShown(double width) => ReceivingViewPrefs.Details ?? width >= ReceivingDetailsAutoBelow;

    /// <summary>Свернуть подробные колонки (вызов в конце раскладки таблицы по ширине — WarehouseWindow.O9.cs). relayout — после «Подробнее ▸»: раскладка заново.</summary>
    private void ApplyReceivingDetails(double width, bool relayout = false)
    {
        if (width > 0)
            _rcvLastWidth = width;
        if (relayout && _rcvLastWidth > 0)
        {
            ApplyReceivingColumns(_rcvLastWidth); // сама вызовет ApplyReceivingDetails
            return;
        }
        if (!ReceivingDetailsShown(_rcvLastWidth))
        {
            RcvColChange.IsVisible = false;
            RcvColAuto.IsVisible = false;
            RcvColProfit.IsVisible = false;
        }
        SyncReceivingDetailsButton();
    }

    private void SyncReceivingDetailsButton()
    {
        if (_rcvDetailsButton is null)
            return;
        var shown = ReceivingDetailsShown(_rcvLastWidth);
        _rcvDetailsButton.Content = shown
            ? Tr.T("Скрыть подробности ◂", "Толугун жашыруу ◂", "Hide details ◂", "Ayrıntıları gizle ◂", "Tafsilotlarni yashirish ◂")
            : Tr.T("Подробнее ▸", "Толугураак ▸", "More details ▸", "Ayrıntılar ▸", "Batafsil ▸");
        ToolTip.SetTip(_rcvDetailsButton, Tr.T("Колонки «Закупка изменилась», «Как посчитана цена», «Прибыль с шт.». Выбор запоминается.",
            "«Сатып алуу өзгөрдү», «Баа кантип эсептелди», «Даанадан пайда» тилкелери. Тандоо эстелет.",
            "Columns “Purchase changed”, “How the price was set”, “Profit per pc”. The choice is remembered.",
            "«Alış değişti», «Fiyat nasıl hesaplandı», «Adet başı kâr» sütunları. Seçim hatırlanır.",
            "«Xarid o'zgardi», «Narx qanday hisoblandi», «Donadan foyda» ustunlari. Tanlov eslab qolinadi."));
    }
}
