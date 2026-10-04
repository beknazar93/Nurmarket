using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-04, владелец: «сделай редизайн админки» (Android-телефон). Было: слева колонка значков забирала
/// ~17 % ширины, у шапки — кнопки «свернуть/развернуть/закрыть», переключатель «Сегодня / Неделя» уходил за край,
/// суммы в карточках обрезались. Стало на узком экране (до 700 точек, только Android):
/// • меню спрятано — «≡» в шапке открывает его на весь экран с подписями, выбор раздела меню закрывает;
/// • кнопок окна нет (окно на Android одно, назад — системной кнопкой «Назад»);
/// • «Сводка»: заголовок и «Обновлено» в первой строке, «Сегодня / Неделя / Месяц» — во второй, карточки
///   компактнее (стили .phone в OwnerShellWindow.axaml).
/// На широком экране (планшет, терминал, телефон боком) — как в Windows.</summary>
public partial class OwnerShellWindow
{
    private const double PhoneShellWidth = 700;

    private bool _phoneLayout;
    private bool _phoneMenuOpen;
    private Button? _phoneMenuButton;
    private Border? _periodSegment;
    private Panel? _captionButtons;

    private void AttachPhoneLayout()
    {
        if (!OperatingSystem.IsAndroid())
            return;
        // Кнопки окна на Android не нужны в любом положении: окно одно, свернуть — кнопкой «Домой».
        _captionButtons = HeaderGrid.Children.OfType<StackPanel>()
            .FirstOrDefault(p => p.Children.OfType<Button>().Any(b => b.Classes.Contains("caption")));
        if (_captionButtons is not null)
            _captionButtons.IsVisible = false;
        SizeChanged += (_, _) => ApplyPhoneLayout();
    }

    private void ApplyPhoneLayout()
    {
        var phone = Bounds.Width > 0 && Bounds.Width < PhoneShellWidth;
        if (phone == _phoneLayout)
            return;
        _phoneLayout = phone;
        if (!phone)
            _phoneMenuOpen = false;
        Classes.Set("phone", phone);
        EnsurePhoneMenuButton();
        _phoneMenuButton!.IsVisible = phone;
        PlacePeriodSegment(phone);
        if (OverviewScroll.Content is StackPanel overviewStack)
            overviewStack.Margin = phone ? new Thickness(10, 6, 10, 16) : new Thickness(28, 12, 28, 28);
        PosLogger.Log($"Owner app: экран {Bounds.Width:0}×{Bounds.Height:0} — {(phone ? "телефон: меню по кнопке «≡»" : "меню слева")}.", "UI");
        // Меню перестраивается: на телефоне — всегда с подписями (оно открывается на весь экран).
        BuildNavigation();
        AdjustHeaderForPhone(_activeSection == null);
        Avalonia.Threading.Dispatcher.UIThread.Post(SyncSectionBounds, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>«≡» в шапке: колонка 0 сдвигается вправо (Auto под кнопку), остальное — как было.</summary>
    private void EnsurePhoneMenuButton()
    {
        if (_phoneMenuButton is not null)
            return;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M3,6H21V8H3V6M3,11H21V13H3V11M3,16H21V18H3V16Z"),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
        };
        icon[!Shape.FillProperty] = this.GetResourceObservable("BrushText").ToBinding();
        _phoneMenuButton = new Button
        {
            Classes = { "iconBtn" },
            Content = icon,
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _phoneMenuButton.Click += (_, _) => SetPhoneMenu(true);

        HeaderGrid.ColumnDefinitions.Insert(0, new ColumnDefinition(GridLength.Auto));
        foreach (var child in HeaderGrid.Children.OfType<Control>())
            Grid.SetColumn(child, Grid.GetColumn(child) + 1);
        Grid.SetColumn(_phoneMenuButton, 0);
        HeaderGrid.Children.Add(_phoneMenuButton);
    }

    /// <summary>«Сегодня / Неделя / Месяц / Период» — на телефоне второй строкой шапки, на широком — на месте.</summary>
    private void PlacePeriodSegment(bool phone)
    {
        _periodSegment ??= OverviewTools.Children.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("segment"));
        if (_periodSegment is null)
            return;
        if (phone && ReferenceEquals(_periodSegment.Parent, OverviewTools))
        {
            OverviewTools.Children.Remove(_periodSegment);
            HeaderGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetRow(_periodSegment, 1);
            Grid.SetColumn(_periodSegment, 0);
            Grid.SetColumnSpan(_periodSegment, HeaderGrid.ColumnDefinitions.Count);
            _periodSegment.HorizontalAlignment = HorizontalAlignment.Left;
            _periodSegment.Margin = new Thickness(0, 8, 0, 2);
            HeaderGrid.Children.Add(_periodSegment);
            OverviewTools.Margin = new Thickness(0);
        }
        else if (!phone && ReferenceEquals(_periodSegment.Parent, HeaderGrid))
        {
            HeaderGrid.Children.Remove(_periodSegment);
            HeaderGrid.RowDefinitions = new RowDefinitions();
            Grid.SetRow(_periodSegment, 0);
            Grid.SetColumnSpan(_periodSegment, 1);
            _periodSegment.Margin = default;
            OverviewTools.Children.Add(_periodSegment);
            OverviewTools.Margin = new Thickness(0, 6, 18, 0);
        }
        // На телефоне сегмент — отдельно от OverviewTools и виден только в «Сводке»; на месте — как часть OverviewTools.
        _periodSegment.IsVisible = !phone || OverviewTools.IsVisible;
    }

    /// <summary>Вызывается из ShowSection после её отступов шапки.</summary>
    private void AdjustHeaderForPhone(bool overview)
    {
        if (!_phoneLayout)
        {
            ApplyPhoneColumns();
            return;
        }
        HeaderGrid.Margin = new Thickness(6, overview ? 6 : 2, 8, overview ? 4 : 2);
        if (_periodSegment is not null)
            _periodSegment.IsVisible = overview;
        ApplyPhoneColumns();
    }

    /// <summary>Меню на весь экран (true) или спрятано (false). Окно открытого раздела — отдельный слой поверх
    /// правой части, поэтому на время меню его прячем, а при закрытии показываем снова.</summary>
    private void SetPhoneMenu(bool open)
    {
        if (!_phoneLayout || _phoneMenuOpen == open)
            return;
        _phoneMenuOpen = open;
        if (open)
            _activeSection?.Window.Hide();
        ApplyPhoneColumns();
        if (!open && _activeSection is { } section)
            ShowSection(section);
    }

    private void ApplyPhoneColumns()
    {
        var columns = RootGrid.ColumnDefinitions;
        // 2026-10-05, снимки владельца: колонка меню шириной 0 не обрезает содержимое — логотип и «≡» меню
        // рисовались поверх шапки раздела (два значка «≡» на логотипе). Закрытое меню на телефоне скрыто целиком.
        if (RootGrid.Children.OfType<Border>().FirstOrDefault(b => Grid.GetColumn(b) == 0 && Grid.GetRow(b) == 1) is { } sidebar)
            sidebar.IsVisible = !_phoneLayout || _phoneMenuOpen;
        if (!_phoneLayout)
        {
            // Ширину меню слева ставит ApplySidebarLayout; правая часть — снова на всё остальное.
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            return;
        }
        columns[0].Width = _phoneMenuOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns[1].Width = _phoneMenuOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
}
