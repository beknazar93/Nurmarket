using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11 (живой показ 1.17.85, Д-3, и снимки владельца: «Склад → Приёмка» — всё содержимое склада уехало вниз, сверху пусто 130–330 px, кнопка «Обновить»
/// посередине справа, таблица сжата; после перехода между окнами раскладка становится правильной). Раскладка зависела от состояния: содержимое окна оставалось
/// посчитанным по прежнему размеру/вкладке и вставало по центру (у корня окна выравнивание по высоте «растянуть» при ограничении размера даёт центр).
/// Теперь: корень прижат к верху и его наименьшая высота — вся высота окна (за вычетом полей, с учётом «Масштаба интерфейса» раздела владельца): желаемая высота
/// содержимого не может оказаться меньше окна, и ставить его «по центру» больше нечему — какая бы обёртка/вкладка ни считала раскладку по желаемому размеру.
/// У встроенного раздела нет предела высоты окна, а после смены вкладки, размера окна, показа и активации раскладка окна пересчитывается заново.</summary>
public partial class WarehouseWindow
{
    private bool _relayoutPosted;

    // 2026-11-12 (Л-21): размер окна и масштаб, по которым раскладка пересчитана в последний раз.
    private Size _relayoutSize;
    private double _relayoutScale;

    internal bool LayoutKeyChanged() => ClientSize != _relayoutSize || Math.Abs(LayoutScale() - _relayoutScale) > 0.001;

    /// <summary>Масштаб интерфейса раздела владельца (обёртка LayoutTransformControl), иначе 1.</summary>
    private double LayoutScale() =>
        Content is LayoutTransformControl { LayoutTransform: Avalonia.Media.ScaleTransform st } && st.ScaleY > 0 ? st.ScaleY : 1.0;

    private void InitLayoutRefresh()
    {
        RootGrid.VerticalAlignment = VerticalAlignment.Top;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        // Д-3: пустое состояние приёмки — сразу под заголовками колонок таблицы, а не по центру всей высоты.
        ReceivingEmptyState.VerticalAlignment = VerticalAlignment.Top;
        ReceivingEmptyState.Margin = new Thickness(0, 64, 0, 0);
        ApplyReceivingEmptyDensity();
        VerticalContentAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        WarehouseTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, WarehouseTabs))
                PostRelayout();
        };
        // поля корня меняются по высоте окна (ApplyProductsCompact) — наименьшую высоту пересчитать
        RootGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property == MarginProperty)
                ApplyRootFill();
        };
        Opened += (_, _) => PostRelayout();
        // 2026-11-12 (аудит Л-21): раньше полный пересчёт раскладки шёл при КАЖДОЙ активации окна — после каждой закрытой модалки (на 15 000 товаров заметная пауза).
        // Теперь активация пересчитывает раскладку, только если изменился размер окна или масштаб (вкладка, показ и размер — свои события).
        Activated += (_, _) =>
        {
            if (LayoutKeyChanged())
                PostRelayout();
        };
        PropertyChanged += (_, e) =>
        {
            // по имени: в Android-оболочке окна (NurMarketKassa.Window) нет ClientSizeProperty
            if (e.Property.Name is "ClientSize" || e.Property == WindowStateProperty)
            {
                ApplyRootFill(); // сразу, до прохода раскладки по новому размеру
                PostRelayout();
            }
        };
    }

    /// <summary>Пересчитать раскладку окна по текущему размеру (отложенно, несколько запросов подряд — один пересчёт).</summary>
    internal void PostRelayout()
    {
        if (_relayoutPosted)
            return;
        _relayoutPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _relayoutPosted = false;
            RelayoutNow();
        }, DispatcherPriority.Loaded);
    }

    internal void RelayoutNow()
    {
        if (!IsVisible)
            return;
        _relayoutSize = ClientSize;
        _relayoutScale = LayoutScale();
        // Встроенный раздел (программа владельца): размер окна задаёт оболочка — предел высоты/ширины окна не нужен (FitToScreen из конструктора).
        if (_isOwnerSection)
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
        }
        ApplyReceivingEmptyDensity();
        ApplyRootFill();
        if (Content is Control content)
        {
            content.InvalidateMeasure();
            content.InvalidateArrange();
        }
        RootGrid.InvalidateMeasure();
        RootGrid.InvalidateArrange();
        WarehouseTabs.InvalidateMeasure();
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>Наименьшая высота корня = высота окна в точках содержимого (окно раздела владельца масштабируется обёрткой LayoutTransformControl) минус поля корня.</summary>
    internal void ApplyRootFill()
    {
        var client = ClientSize.Height;
        if (client <= 0)
            return;
        var scale = LayoutScale();
        var margin = RootGrid.Margin;
        var fill = (int)(client / scale - margin.Top - margin.Bottom) - 1; // 2026-11-12: (int) вместо Math.Floor — размер в точках, не деньги (check-money-in-views) // −1 — запас на округление точек
        var min = fill > 200 ? fill : 0;
        if (Math.Abs(RootGrid.MinHeight - min) > 0.5)
            RootGrid.MinHeight = min;
    }

    /// <summary>2026-11-11 (Д-3, 1024×600): длинная подсказка пустой приёмки (≈200 px) раздвигала таблицу, и итоги с «Оприходовать» уходили за низ окна.
    /// На низком окне — только заголовок «Отсканируйте первый товар» (значок прячется ниже 640 px, подсказка — ниже 720 px).</summary>
    private void ApplyReceivingEmptyDensity()
    {
        var height = ClientSize.Height;
        if (height <= 0)
            return;
        ReceivingEmptyHint.IsVisible = height >= 720;
        if (ReceivingEmptyState.Children.Count > 0)
            ReceivingEmptyState.Children[0].IsVisible = height >= 640;
        ReceivingEmptyState.Margin = new Thickness(0, height >= 720 ? 64 : 56, 0, 0);
    }
}
