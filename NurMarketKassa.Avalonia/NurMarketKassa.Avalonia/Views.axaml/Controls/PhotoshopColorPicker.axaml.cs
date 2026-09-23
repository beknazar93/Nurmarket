using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>Визуальный выбор цвета (квадрат насыщенность/яркость + полоса оттенка + HEX-поле),
/// как в Photoshop — переиспользуемый компонент, используется и для акцентного цвета темы, и
/// для цвета текста (см. ThemeSettingsDialog). Публичный контракт: <see cref="SetHex"/> для
/// инициализации, <see cref="TryGetHex"/>/<see cref="RawText"/> для чтения текущего ввода.</summary>
public partial class PhotoshopColorPicker : UserControl
{
    private double _hue; // 0-360
    private double _saturation = 1; // 0-1
    private double _value = 1; // 0-1
    private bool _suppressColorSync;
    private bool _draggingSv;
    private bool _draggingHue;
    private const double SvSize = 200;
    private const double HueHeight = 200;

    public PhotoshopColorPicker() => InitializeComponent();

    /// <summary>Пользователь подвинул пикер или ввёл валидный HEX (2026-09-07) — НЕ поднимается
    /// из <see cref="SetHex"/> (программная инициализация, не действие пользователя), поэтому
    /// вызывающий редактор темы может подписаться на живой предпросмотр без риска зациклиться
    /// на собственной же инициализации пикера.</summary>
    public event Action? ColorChanged;

    /// <summary>Текущий текст HEX-поля как есть (может быть пустым или невалидным во время
    /// набора) — используй для проверки "поле оставлено пустым" (оверрайд не задан).</summary>
    public string RawText => HexBox.Text ?? "";

    /// <summary>Инициализирует пикер выбранным цветом. Пустой/невалидный hex оставляет
    /// HEX-поле пустым (сохраняя семантику "оверрайд не задан"), но визуально позиционирует
    /// пикер на fallbackDisplayHex, чтобы было от чего оттолкнуться, если решат выбрать цвет.</summary>
    public void SetHex(string? hex, string fallbackDisplayHex = "#FF6B00")
    {
        var normalized = NormalizeHexInput(hex);
        Color color;
        try
        {
            color = Color.Parse(normalized.Length > 0 ? normalized : fallbackDisplayHex);
        }
        catch
        {
            color = Color.Parse(fallbackDisplayHex);
        }

        (_hue, _saturation, _value) = RgbToHsv(color.R, color.G, color.B);
        BuildHueStrip();
        UpdateSvHueLayer();
        UpdateThumbs();

        _suppressColorSync = true;
        HexBox.Text = normalized;
        _suppressColorSync = false;
        UpdateSwatch();
    }

    /// <summary>true и нормализованный HEX, если поле сейчас содержит валидный непустой цвет.</summary>
    public bool TryGetHex(out string hex)
    {
        hex = "";
        var text = NormalizeHexInput(HexBox.Text);
        if (text.Length == 0)
            return false;
        try
        {
            Color.Parse(text);
            hex = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeHexInput(string? hex)
    {
        var text = (hex ?? "").Trim();
        if (text.Length > 0 && !text.StartsWith('#'))
            text = "#" + text;
        return text;
    }

    private void HexBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateSwatch();
        if (_suppressColorSync)
            return;

        // Ручной ввод HEX тоже двигает пикер — синхронизация в обе стороны. Некорректный/
        // недописанный ввод просто не двигает пикер, без ошибки — валидация на стороне
        // вызывающего диалога (при "Применить").
        try
        {
            var color = Color.Parse(NormalizeHexInput(HexBox.Text));
            (_hue, _saturation, _value) = RgbToHsv(color.R, color.G, color.B);
            UpdateSvHueLayer();
            UpdateThumbs();
            ColorChanged?.Invoke();
        }
        catch
        {
            // Пока печатает — не мешаем.
        }
    }

    private void UpdateSwatch()
    {
        try
        {
            var text = NormalizeHexInput(HexBox.Text);
            ColorSwatch.Background = text.Length == 0 ? Brushes.Transparent : new SolidColorBrush(Color.Parse(text));
        }
        catch
        {
            ColorSwatch.Background = Brushes.Transparent;
        }
    }

    private void BuildHueStrip()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        double[] hueStops = [0, 60, 120, 180, 240, 300, 360];
        for (var i = 0; i < hueStops.Length; i++)
        {
            var (r, g, b) = HsvToRgb(hueStops[i], 1, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / (double)(hueStops.Length - 1)));
        }
        HueStrip.Fill = brush;
    }

    private void UpdateSvHueLayer()
    {
        var (r, g, b) = HsvToRgb(_hue, 1, 1);
        SvHueLayer.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(r, g, b), 1) },
        };
        SvShadeLayer.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 1) },
        };
    }

    private void UpdateThumbs()
    {
        var x = _saturation * SvSize;
        var y = (1 - _value) * SvSize;
        Canvas.SetLeft(SvThumb, Math.Clamp(x - SvThumb.Width / 2, -SvThumb.Width / 2, SvSize - SvThumb.Width / 2));
        Canvas.SetTop(SvThumb, Math.Clamp(y - SvThumb.Height / 2, -SvThumb.Height / 2, SvSize - SvThumb.Height / 2));

        var hueY = _hue / 360.0 * HueHeight;
        Canvas.SetTop(HueThumb, Math.Clamp(hueY - HueThumb.Height / 2, 0, HueHeight - HueThumb.Height));
    }

    private void SvCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggingSv = true;
        e.Pointer.Capture(SvCanvas);
        UpdateSvFromPointer(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingSv)
            UpdateSvFromPointer(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingSv = false;
        e.Pointer.Capture(null);
    }

    private void UpdateSvFromPointer(Point p)
    {
        _saturation = Math.Clamp(p.X / SvSize, 0, 1);
        _value = Math.Clamp(1 - p.Y / SvSize, 0, 1);
        UpdateThumbs();
        ApplyHsvToHex();
    }

    private void HueCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggingHue = true;
        e.Pointer.Capture(HueCanvas);
        UpdateHueFromPointer(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingHue)
            UpdateHueFromPointer(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingHue = false;
        e.Pointer.Capture(null);
    }

    private void UpdateHueFromPointer(Point p)
    {
        _hue = Math.Clamp(p.Y / HueHeight, 0, 1) * 360;
        UpdateSvHueLayer();
        UpdateThumbs();
        ApplyHsvToHex();
    }

    private void ApplyHsvToHex()
    {
        var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
        var hex = $"#{r:X2}{g:X2}{b:X2}";
        _suppressColorSync = true;
        HexBox.Text = hex;
        _suppressColorSync = false;
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ColorChanged?.Invoke();
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        double h = 0;
        if (delta > 1e-9)
        {
            if (max == rd) h = 60 * ((gd - bd) / delta % 6);
            else if (max == gd) h = 60 * ((bd - rd) / delta + 2);
            else h = 60 * ((rd - gd) / delta + 4);
        }
        if (h < 0) h += 360;

        var s = max <= 1e-9 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (rd, gd, bd) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return (
            (byte)Math.Round((rd + m) * 255),
            (byte)Math.Round((gd + m) * 255),
            (byte)Math.Round((bd + m) * 255));
    }
}
