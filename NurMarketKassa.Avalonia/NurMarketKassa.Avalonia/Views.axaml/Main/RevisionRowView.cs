using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-24, ТЗ «Склад», пакет Р2: одна строка списка ревизии (название, штрихкод, учёт, факт, расхождение; в окне итога — ещё «В акт»).
/// Список только для чтения — факт вводится в карточке над ним (ТЗ 2.2), поэтому ячеек для правки здесь нет. Строка сама подписана на
/// <see cref="RevisionRow"/> только пока находится в окне (подписка снимается при уходе из визуального дерева — прокрутка 15 000 строк не копит
/// обработчики) и обновляется на потоке окна, даже если число изменил фоновый «Применить». Недостача — красным, излишек — зелёным.</summary>
internal sealed class RevisionRowView : Border
{
    public const double BarcodeWidth = 130;
    public const double NumberWidth = 84;
    public const double RowHeight = 36;

    private readonly TextBlock _name = new();
    private readonly TextBlock _barcode = new();
    private readonly TextBlock _book = new();
    private readonly TextBlock _fact = new();
    private readonly TextBlock _diff = new();
    private readonly TextBlock? _send;
    private readonly IReadOnlyDictionary<string, double>? _sendMap;
    private readonly RevisionRow _row;

    public RevisionRow Row => _row;

    public RevisionRowView(RevisionRow row, IReadOnlyDictionary<string, double>? sendMap = null)
    {
        _row = row;
        _sendMap = sendMap;
        Height = RowHeight;
        Background = Brushes.Transparent; // чтобы нажатие попадало в строку, а не сквозь пустоту
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(sendMap is null ? "*,Auto,Auto,Auto,Auto" : "*,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(8, 0, 16, 0) };
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.FontSize = 14;
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.Margin = new Thickness(0, 0, 8, 0);
        _name.Bind(TextBlock.ForegroundProperty, _name.GetResourceObservable("BrushText"));
        Configure(_barcode, BarcodeWidth, HorizontalAlignment.Left, 12.5);
        _barcode.Bind(TextBlock.ForegroundProperty, _barcode.GetResourceObservable("BrushTextSoft"));
        Configure(_book, NumberWidth, HorizontalAlignment.Right, 14);
        _book.Bind(TextBlock.ForegroundProperty, _book.GetResourceObservable("BrushTextSoft"));
        Configure(_fact, NumberWidth, HorizontalAlignment.Right, 14);
        Configure(_diff, NumberWidth, HorizontalAlignment.Right, 14);
        grid.Children.Add(_name);
        Grid.SetColumn(_barcode, 1);
        Grid.SetColumn(_book, 2);
        Grid.SetColumn(_fact, 3);
        Grid.SetColumn(_diff, 4);
        grid.Children.Add(_barcode);
        grid.Children.Add(_book);
        grid.Children.Add(_fact);
        grid.Children.Add(_diff);
        if (sendMap is not null)
        {
            _send = new TextBlock();
            Configure(_send, NumberWidth, HorizontalAlignment.Right, 14);
            _send.Bind(TextBlock.ForegroundProperty, _send.GetResourceObservable("BrushText"));
            Grid.SetColumn(_send, 5);
            grid.Children.Add(_send);
        }

        Child = grid;
        Update();
    }

    private static void Configure(TextBlock t, double width, HorizontalAlignment align, double size)
    {
        t.Width = width;
        t.FontSize = size;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    /// <summary>Шапка столбцов над списком (те же ширины, что у строки).</summary>
    public static Control BuildHeader(bool withSend)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(withSend ? "*,Auto,Auto,Auto,Auto,Auto" : "*,Auto,Auto,Auto,Auto"), Margin = new Thickness(8, 0, 16, 0) };
        void Add(string text, int col, double width, TextAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (col > 0)
                t.Width = width;
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }

        Add(Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi"), 0, 0, TextAlignment.Left);
        Add(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 1, BarcodeWidth, TextAlignment.Left);
        Add(Tr.T("Учёт", "Эсеп", "Book", "Sistem", "Hisob"), 2, NumberWidth, TextAlignment.Right);
        Add(Tr.T("Факт", "Факт", "Counted", "Sayılan", "Haqiqiy"), 3, NumberWidth, TextAlignment.Right);
        Add(Tr.T("Разница", "Айырма", "Diff.", "Fark", "Farq"), 4, NumberWidth, TextAlignment.Right);
        if (withSend)
            Add(Tr.T("В акт", "Актыга", "In act", "Tutanağa", "Aktga"), 5, NumberWidth, TextAlignment.Right);
        var box = new Border { Height = 30, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("BrushBorder"));
        return box;
    }

    private static string Qty(double v) => ReportFormat.Quantity(v);

    private IBrush? Res(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    public void Update()
    {
        var r = _row;
        _name.Text = r.IsReadOnly
            ? r.Name + "  ·  " + Tr.T("по размерам", "өлчөмдөр боюнча", "by sizes", "bedenlere göre", "o'lchamlar bo'yicha")
            : r.Name;
        _barcode.Text = r.Barcode;
        _book.Text = Qty(r.IsCounted ? r.ExpectedAtCount : r.Expected);
        _fact.Text = r.Fact is { } f ? Qty(f) : "—";
        if (r.IsCounted)
        {
            var d = r.Difference;
            _diff.Text = d > 0.0005 ? "+" + Qty(d) : Math.Abs(d) <= 0.0005 ? "0" : Qty(d);
            var color = d < -0.0005 ? Res("BrushDanger") : d > 0.0005 ? Res("BrushSuccess") : Res("BrushText");
            _diff.Foreground = color;
            _fact.Foreground = color;
            var bold = r.HasDifference ? FontWeight.Bold : FontWeight.SemiBold;
            _diff.FontWeight = bold;
            _fact.FontWeight = bold;
        }
        else
        {
            _diff.Text = "";
            _fact.Foreground = Res("BrushTextSoft");
            _fact.FontWeight = FontWeight.Normal;
        }

        Opacity = r.IsReadOnly ? 0.6 : 1;
        if (_send is not null)
            _send.Text = _sendMap!.TryGetValue(r.ProductId, out var s) ? Qty(s) : "";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Update();
        else
            Dispatcher.UIThread.Post(Update);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _row.PropertyChanged -= OnRowChanged;
        _row.PropertyChanged += OnRowChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Update(); // пока строки не было в окне, её значения могли поменяться
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _row.PropertyChanged -= OnRowChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Update();
}
