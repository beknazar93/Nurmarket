using Avalonia.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, просьба владельца (пример чужой кассы: вкладка «Покупки» со строками по всем документам): вкладка «Покупки» раздела «Закупки» — реестр
/// «Документы | Позиции» (PurchasesRegistryView). Здесь только стыковка с окном: показать/спрятать реестр вместо плиток и списка истории, загрузчики, открытие документа.</summary>
public sealed partial class PurchasesWindow
{
    private Border? _listCard;
    private PurchasesRegistryView? _registry;

    private PurchasesRegistryView EnsureRegistry()
    {
        if (_registry is not null)
            return _registry;
        var money = WarehouseWindow.RevisionShowPurchase; // цены, закупка и поставщик — по тому же праву, что у ревизии и «Поступлений» товара
        var view = new PurchasesRegistryView(_svc, money,
            (from, to, onPage, ct) => PurchasesRegistryLoader.LoadAsync(_svc, from, to, null, money, onPage, ct),
            (receipts, progress, ct) => PurchasesRegistryLoader.EnsureLinesAsync(_svc, receipts, progress, ct))
        {
            OpenDocument = OpenRegistryDocumentAsync,
            IsVisible = false,
        };
        Grid.SetRow(view, 3);
        Grid.SetRowSpan(view, 4); // строки 3–6: на месте плиток, фильтров, подсказки и списка истории
        _main.Children.Add(view);
        _registry = view;
        return view;
    }

    private void ShowRegistry()
    {
        SetStatus(null);
        // 2026-10-31 (живой обход 1.17.67, В-6: вкладка «Покупки» пустая): реестр создаётся спрятанным (IsVisible = false), а ApplyRegistryMode при первом
        // показе его ещё не видел (_registry == null) — вкладка открывалась пустой до второго захода. Сначала создаём, потом показываем.
        var view = EnsureRegistry();
        ApplyRegistryMode();
        view.Activate();
    }

    /// <summary>Вкладка «Покупки»: реестр виден, плитки/фильтры/список истории спрятаны; на других вкладках — наоборот (с учётом низкого экрана кассы).</summary>
    private void ApplyRegistryMode()
    {
        var on = _tab == "registry";
        if (_registry is not null)
            _registry.IsVisible = on;
        if (_listCard is null)
            return;
        if (on)
        {
            _kpi.IsVisible = false;
            _kpiLine.IsVisible = false;
            _filters.IsVisible = false;
            _status.IsVisible = false;
            _listCard.IsVisible = false;
            return;
        }
        _kpi.IsVisible = !_compact;
        _kpiLine.IsVisible = _compact;
        _filters.IsVisible = true;
        _status.IsVisible = !string.IsNullOrEmpty(_status.Text);
        _listCard.IsVisible = true;
    }

    /// <summary>Закрыли страницу «Приём товара»/«Рекомендуемый заказ» — реестр перечитываем (мог появиться новый приход).</summary>
    private void RegistryAfterSub()
    {
        if (_tab != "registry")
            return;
        ApplyRegistryMode();
        _registry?.Reload();
    }

    /// <summary>Двойной щелчок по документу или позиции реестра. Форма накладной (InvoiceDocumentWindow) подключается через
    /// <see cref="PurchasesRegistryView.InvoiceHook"/>; пока её нет — «Позиции прихода» (с подсвеченной строкой товара для позиции).</summary>
    private async Task OpenRegistryDocumentAsync(RegistryDoc doc, RegistryLine? line)
    {
        var hook = PurchasesRegistryView.InvoiceHook;
        if (hook is not null && await hook(this, doc.Id, line?.ProductId).ConfigureAwait(true))
            return;
        await ShowReceiptAsync(doc.Receipt, line?.ProductId).ConfigureAwait(true);
    }
}
