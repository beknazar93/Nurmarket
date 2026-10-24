using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Фильтр-чипы витрины.</summary>
public enum MarketplaceFilter { All, Features, Themes, Layouts, Paid, Installed }

/// <summary>2026-10-23, по просьбе владельца («как в Play Market… при нажатии открывался подробно с описанием и ценами»):
/// витрина Маркетплейса. Сверху — поиск и чипы категорий; ниже — ряды с горизонтальной прокруткой («Рекомендуем», «Новое»,
/// «Бесплатные», «Платные», «Скоро») либо сетка плиток (фильтр/поиск). Нажатие на плитку открывает страницу «подробно»
/// внутри этого же окна: значок, цена/состояние, описание, возможности, требования, «Похожие», кнопка действия закреплена
/// внизу. Данные — только IMarketplaceCatalogSource; действия — делегаты элементов (внутри существующая логика кассы).
///
/// 2026-10-23: файл разрезан на части (partial), чтобы витрину, страницу «подробно» и общую часть можно было править отдельно:
/// MarketplaceStoreView.cs — общее (состояние, вход, история, Esc, слой-оверлей, Fg/Section);
/// MarketplaceStoreView.Storefront.cs — витрина (поиск, чипы, ряды, плитки, значок);
/// MarketplaceStoreView.Detail.cs — страница «подробно».</summary>
public sealed partial class MarketplaceStoreView : UserControl
{
    private IMarketplaceCatalogSource? _source;
    private IReadOnlyList<MarketplaceItem> _items = [];
    private MarketplaceFilter _filter = MarketplaceFilter.All;
    private string _query = "";
    private readonly Stack<string> _history = new();
    private string? _openId;

    private readonly ContentControl _page = new();

    /// <summary>Готовый блок управления внутри страницы (например, загрузка голосовых моделей) по InlineControlId.</summary>
    public Func<string, Control?>? InlineControlProvider { get; set; }

    // 2026-10-23: слой поверх страницы (полноэкранный просмотр слайда и т.п.). Корень — Grid из страницы и слоя; слой скрыт, пока пуст.
    private readonly Grid _overlayLayer = new() { IsVisible = false };

    /// <summary>Открыт ли слой поверх страницы (Esc сначала закрывает его).</summary>
    internal bool IsOverlayOpen => _overlayLayer.IsVisible;

    /// <summary>Показать контрол поверх страницы (заменяет предыдущее содержимое слоя).</summary>
    internal void ShowOverlay(Control content)
    {
        _overlayLayer.Children.Clear();
        _overlayLayer.Children.Add(content);
        _overlayLayer.IsVisible = true;
    }

    /// <summary>Закрыть слой поверх страницы.</summary>
    internal void HideOverlay()
    {
        _overlayLayer.IsVisible = false;
        _overlayLayer.Children.Clear();
    }

    public MarketplaceStoreView()
    {
        Content = new Grid { Children = { _page, _overlayLayer } };
        BuildStorefrontShell();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsOverlayOpen)
            {
                HideOverlay();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _openId != null)
            {
                GoBack();
                e.Handled = true;
            }
        };
    }

    public void Initialize(IMarketplaceCatalogSource source)
    {
        _source = source;
        Reload();
    }

    public void SetFilter(MarketplaceFilter filter)
    {
        _filter = filter;
        _query = "";
        _search.Text = "";
        _history.Clear();
        _openId = null;
        ShowStorefront();
    }

    /// <summary>Открыть страницу «подробно» элемента по Id (например, из другого раздела программы).</summary>
    public void OpenItem(string id)
    {
        _history.Clear();
        ShowDetail(id, keepScroll: false);
    }

    /// <summary>Каталог перечитан (состояние функции изменилось, сменились язык/тема) — перерисовать, оставаясь где были.</summary>
    public void Reload()
    {
        if (_source is null)
            return;
        _items = _source.GetItems();
        if (_openId != null && _items.All(i => i.Id != _openId))
        {
            _openId = null;
            _history.Clear();
        }

        if (_openId is null)
            ShowStorefront();
        else
            ShowDetail(_openId, keepScroll: true);
    }

    // ------------------------------------------------------------------ страница «подробно»

    private void OpenFromTile(string id)
    {
        if (_openId != null)
            _history.Push(_openId);
        ShowDetail(id, keepScroll: false);
    }

    private void GoBack()
    {
        if (_history.Count > 0)
            ShowDetail(_history.Pop(), keepScroll: false);
        else
            ShowStorefront();
    }

    // ------------------------------------------------------------------ общие помощники

    private Control Section(string title, Control content)
    {
        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.Bold };
        Fg(heading, "BrushText");
        return new StackPanel { Spacing = 8, Children = { heading, content } };
    }

    /// <summary>Цвет текста берётся из ресурсов темы и следует за ней (светлая/тёмная) без пересборки.</summary>
    private static void Fg(TextBlock block, string key) =>
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(key));
}
