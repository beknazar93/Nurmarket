using Avalonia;
using Avalonia.Media;

namespace NurMarketKassa;

/// <summary>2026-10-04, владелец: «оно запустилось, но оптимизации под разные экраны в Android нет».
/// Окна кассы рассчитаны на горизонтальный экран от 1000×660 (то же, что UiScaleHelper в Windows).
/// На телефоне (Xiaomi Redmi 12: 393×894 точек) окна обрезались. Теперь весь вид кассы (стопка окон
/// <see cref="WindowLayerHost"/>) равномерно уменьшается так, чтобы 1000×660 помещалось целиком; на экранах
/// не меньше этого (терминалы 1280×800, 1920×1080) — 100 %. Касса видит «экран» уже уменьшенного вида
/// (Screens.Synthesized), поэтому её собственная подгонка UiScaleHelper не уменьшает второй раз.
/// Экранная клавиатура срезает высоту — на масштаб это не влияет, иначе касса «прыгала» бы при каждом вводе.</summary>
public sealed class ScreenFitHost : LayoutTransformControl
{
    public const double DesignWidth = 1000;
    public const double DesignHeight = 660;
    /// <summary>2026-10-04, редизайн «под любое Android-устройство»: низкий горизонтальный экран (телефон боком
    /// 809×355, терминал 1024×600 → 1024×449 без панелей) уменьшал кассу под высоту 660 — 54 % и 68 %, текст
    /// ~1 мм, кнопки мельче пальца. Компактный вид кассы работает и от этой высоты (каталог — ряд товаров,
    /// чек листается), поэтому по высоте уменьшаем только под неё.</summary>
    public const double LandscapeMinDesignHeight = 480;
    public const double MinScale = 0.4;
    /// <summary>Вертикальный экран: ширина одной колонки кассы. 2026-10-04: было 500 (под CartMinimumWidth 490
    /// горизонтального вида) — на телефонах 360–411 точек касса уменьшалась до 72–82 %. Одна колонка
    /// помещается и в 430 (чек в вертикальной раскладке без минимальной ширины).</summary>
    public const double PortraitDesignWidth = 430;
    public const double PortraitMinScale = 0.6;

    private double _lastWidth = -1;
    private double _fullHeight;

    public ScreenFitHost(Control content)
    {
        Child = content;
        ClipToBounds = true;
        Current = this;
    }

    public static ScreenFitHost? Current { get; private set; }

    /// <summary>Масштаб вида кассы (1 — без уменьшения).</summary>
    public double Scale { get; private set; } = 1;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var w = e.NewSize.Width;
        var h = e.NewSize.Height;
        if (w <= 0 || h <= 0)
            return;

        // Ширина та же, высота меньше — это экранная клавиатура: масштаб считаем по полной высоте.
        if (Math.Abs(w - _lastWidth) > 1)
        {
            _lastWidth = w;
            _fullHeight = h;
        }
        else
        {
            _fullHeight = Math.Max(_fullHeight, h);
        }

        // 2026-10-04, владелец: «сделай как у других Android-программ» — экран поворачивается вместе с
        // телефоном. Вертикально касса перестраивается в одну колонку (каталог над чеком, оплата в столбик),
        // ей нужна ширина PortraitDesignWidth; высота не ограничивает.
        var portrait = _fullHeight > w * 1.05;
        var scale = portrait
            ? Math.Clamp(w / PortraitDesignWidth, PortraitMinScale, 1.0)
            : Math.Clamp(Math.Min(w / DesignWidth, _fullHeight / LandscapeMinDesignHeight), MinScale, 1.0);
        scale = Math.Round(scale, 3);
        if (Math.Abs(scale - Scale) < 0.002)
            return;
        Scale = scale;
        LayoutTransform = scale >= 0.999 ? null : new ScaleTransform(scale, scale);
        try
        {
            NurMarketKassa.Services.PosLogger.Log(
                $"Android: вид {w:0}×{h:0} точек → масштаб кассы {scale:P0} (рассчитано на {DesignWidth:0}×{DesignHeight:0}).", "INFO");
        }
        catch { /* журнал недоступен */ }
    }
}
