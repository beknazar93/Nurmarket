using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services; // 2026-10-29: PosDialogs (без этого сборка падала)
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса BulkImportWizardWindow (Rows): перенесено из BulkImportWizardWindow.cs без изменений логики.
public partial class BulkImportWizardWindow
{
    // ------------------------------------------------------------------ вспомогательные типы

    /// <summary>Строка таблицы окна: оболочка над строкой плана с текстами для ячеек и правкой через строку прихода (ReceivingLineVm).</summary>
    internal sealed class ImportRowVm : INotifyPropertyChanged
    {
        private readonly int _cBarcode, _cName, _cQty, _cPurchase, _cSale, _cMarkup, _cUnit;
        private bool _checked;

        public ImportRowVm(BulkImportRow row, int cBarcode, int cName, int cQty, int cPurchase, int cSale, int cMarkup, int cUnit)
        {
            Row = row;
            (_cBarcode, _cName, _cQty, _cPurchase, _cSale, _cMarkup, _cUnit) = (cBarcode, cName, cQty, cPurchase, cSale, cMarkup, cUnit);
        }

        public BulkImportRow Row { get; }

        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                Raise(nameof(IsChecked));
            }
        }

        public int FileRow => Row.FileRow;

        private string Raw(int column) => column >= 0 && column < Row.SourceCells.Length ? Row.SourceCells[column] : "";

        public string Barcode => Row.Barcode.Length > 0 ? Row.Barcode : Raw(_cBarcode);

        public string Name
        {
            get => Row.Line?.ProductName ?? Row.Name;
            set
            {
                if (Row.Line is { IsNew: true } line)
                    line.ProductName = value ?? "";
            }
        }

        public string UnitText
        {
            get => Row.Line is { } line ? (Row.ErrorField == ProductImportField.Unit && Row.Kind == BulkImportRowKind.Error ? "" : UnitCatalog.Display(line.Unit)) : Raw(_cUnit);
            set => Row.SetUnit(value);
        }

        public string QuantityText
        {
            get => Row.Line?.QuantityText ?? Raw(_cQty);
            set
            {
                if (Row.Line is { } line)
                    line.QuantityText = value ?? "";
            }
        }

        public string PurchaseText
        {
            get => Row.Line is { } line ? (line.PurchasePrice > 0 ? line.PurchasePriceText : "") : Raw(_cPurchase);
            set
            {
                if (Row.Line is { } line)
                    line.SetPurchaseText(value);
            }
        }

        public string MarkupText
        {
            get => Row.Line is { } line ? line.MarkupText : Raw(_cMarkup);
            set
            {
                if (Row.Line is { } line)
                    line.MarkupText = value ?? "";
            }
        }

        public string SaleText
        {
            get => Row.Line is { } line ? (line.SalePrice > 0 ? line.SalePriceText : "") : Raw(_cSale);
            set
            {
                if (Row.Line is { } line)
                    line.SetSaleText(value);
            }
        }

        public string StatusText
        {
            get
            {
                if (Row.Kind == BulkImportRowKind.Error)
                    return "⚠ " + Row.Error;
                var text = Row.Note;
                return string.IsNullOrEmpty(Row.Warning) ? text : text + " · " + Row.Warning;
            }
        }

        /// <summary>Текст ячейки; числа — с запятой, как во всей программе (внутри строки прихода они с точкой).</summary>
        public string Get(string path) => path switch
        {
            nameof(Barcode) => Barcode,
            nameof(Name) => Name,
            nameof(UnitText) => UnitText,
            nameof(QuantityText) => QuantityText.Replace('.', ','),
            nameof(PurchaseText) => PurchaseText.Replace('.', ','),
            nameof(MarkupText) => MarkupText.Replace('.', ','),
            nameof(SaleText) => SaleText.Replace('.', ','),
            _ => "",
        };

        public void RaiseAll()
        {
            foreach (var name in new[] { nameof(Name), nameof(UnitText), nameof(QuantityText), nameof(PurchaseText), nameof(MarkupText), nameof(SaleText), nameof(StatusText), "Row" })
                Raise(name);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Ячейка таблицы: текст строки и красная подсветка, если ошибка именно в этом поле (или в строке — для столбца статуса).</summary>
    private sealed class RowCell : Border
    {
        private readonly BulkImportWizardWindow _owner;
        private readonly Func<ImportRowVm, string> _text;
        private readonly ProductImportField? _field;
        private readonly bool _status;
        private readonly TextBlock _block = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
        private ImportRowVm? _vm;

        public RowCell(BulkImportWizardWindow owner, Func<ImportRowVm, string> text, ProductImportField? field, bool status)
        {
            _owner = owner;
            _text = text;
            _field = field;
            _status = status;
            Child = _block;
            if (status)
            {
                _block.TextWrapping = TextWrapping.Wrap;
                _block.TextTrimming = TextTrimming.None;
                _block.FontSize = 12.5;
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as ImportRowVm;
            if (_vm is not null)
                _vm.PropertyChanged += OnChanged;
            Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnChanged;
            _vm = null;
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (_vm is null)
                return;
            _block.Text = _text(_vm);
            var isError = _vm.Row.Kind == BulkImportRowKind.Error;
            var cellError = isError && (_status || (_field is not null && _vm.Row.ErrorField == _field));
            Background = cellError ? _owner.Brush("BrushDangerSoft") : null;
            var key = _status ? (isError ? "BrushDanger" : "BrushTextSoft") : "BrushText";
            _block.Foreground = _owner.Brush(cellError && !_status ? "BrushDangerOnSoft" : key);
        }
    }

    /// <summary>Выбор столбца файла: кнопка с текущим выбором и список под ней (не ComboBox/Popup: в окнах без системной рамки они не открываются).</summary>
    private sealed class ColumnChoice : UserControl
    {
        private readonly IReadOnlyList<string> _items;
        private readonly string _none;
        private readonly Button _button;
        private readonly Border _panel;
        private readonly StackPanel _list = new();
        private int _selected = -1;
        private TopLevel? _top;

        public event Action<int>? SelectedChanged;

        public ColumnChoice(IReadOnlyList<string> items, string none)
        {
            _items = items;
            _none = none;
            _button = new Button { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            _button.Classes.Add("modal-secondary");
            _button.Click += (_, _) => _panel!.IsVisible = !_panel.IsVisible && Rebuild();
            _panel = new Border
            {
                IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 0),
                Child = new ScrollViewer { MaxHeight = 220, Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            };
            _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("BrushPanel"));
            _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("BrushBorder"));
            Content = new StackPanel { Children = { _button, _panel } };
            Show();
        }

        public int Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Show();
            }
        }

        private void Show()
        {
            _button.Content = _selected >= 0 && _selected < _items.Count ? _items[_selected] : _none;
        }

        private bool Rebuild()
        {
            _list.Children.Clear();
            void Add(string text, int index)
            {
                var b = new Button { Content = text, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false };
                b.Classes.Add(index == _selected ? "modal-primary" : "modal-secondary");
                b.Margin = new Thickness(0, 0, 0, 3);
                b.Click += (_, _) =>
                {
                    _selected = index;
                    Show();
                    _panel.IsVisible = false;
                    SelectedChanged?.Invoke(index);
                };
                _list.Children.Add(b);
            }
            Add(_none, -1);
            for (var i = 0; i < _items.Count; i++)
                Add(_items[i], i);
            return true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _top = TopLevel.GetTopLevel(this);
            _top?.AddHandler(PointerPressedEvent, OnTopPressed, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _top?.RemoveHandler(PointerPressedEvent, OnTopPressed);
            _top = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTopPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (!_panel.IsVisible)
                return;
            for (var v = e.Source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            {
                if (ReferenceEquals(v, this))
                    return;
            }
            _panel.IsVisible = false;
        }
    }
}
