using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-12 (ХМ8, ТЗ хозмаг 4.1 п. 9): блок карточки клиента «Покупает по оптовой цене (прораб)» — локальная отметка
/// (<see cref="ClientPriceGroups"/>, общие настройки компании). Клиент с отметкой в чеке даёт опт по всем строкам с оптовой ценой
/// (плашка «Цена прораба»). Блок виден только в магазине «Хозтовары». Серверные уровни цен заменят отметку после ответа Умара (Б-55).
/// </summary>
public sealed class ClientForemanBlock : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly CheckBox _toggle = new() { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 13.5, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string? _clientId;
    private bool _suppress;
    private string _defaultHint = "";

    private static NurMarketKassa.Core.Contracts.IPermissionService? Permissions =>
        NurMarketKassa.AvaloniaHost.App.AppHost?.Services.GetService(typeof(NurMarketKassa.Core.Contracts.IPermissionService)) as NurMarketKassa.Core.Contracts.IPermissionService;

    public ClientForemanBlock()
    {
        IsVisible = false;
        _label.Text = T("Покупает по оптовой цене (прораб)", "Дүң баада сатып алат (прораб)", "Buys at the wholesale price (foreman)", "Toptan fiyatla alır (usta)", "Ulgurji narxda oladi (prorab)");
        _hint.Text = _defaultHint = T("В чеке такого клиента все товары с оптовой ценой идут по опту. Товар без оптовой цены — по рознице.",
            "Мындай кардардын чегинде дүң баасы бар бардык товарлар дүң баада болот. Дүң баасы жок товар — чекене баада.",
            "In this client's receipt all products with a wholesale price go at wholesale. A product without one stays at retail.",
            "Bu müşterinin fişinde toptan fiyatı olan tüm ürünler toptan satılır. Toptan fiyatı olmayan ürün perakende kalır.",
            "Bunday mijozning chekida ulgurji narxi bor barcha mahsulotlar ulgurji narxda. Ulgurji narxi yo'q mahsulot — chakana narxda.");
        Avalonia.Automation.AutomationProperties.SetName(_toggle, _label.Text);
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("BrushTextSoft"));
        _label.Bind(TextBlock.ForegroundProperty, _label.GetResourceObservable("BrushText"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(_label);
        Grid.SetColumn(_toggle, 1);
        row.Children.Add(_toggle);
        Content = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { row, _hint } };
        _toggle.IsCheckedChanged += (_, _) =>
        {
            if (_suppress || string.IsNullOrWhiteSpace(_clientId))
                return;
            // 2026-11-13 (проверка): без права «Изменение цены в кассе» отметку не меняем (окно «Клиенты» открыто и тем, у кого права на цену нет).
            if (!ClientPriceGroups.TrySetForeman(_clientId, _toggle.IsChecked == true, Permissions))
                SetClient(_clientId); // нет права или не поместилось в лимит — вернуть показанное
        };
    }

    /// <summary>Выбран другой клиент карточки (null — карточка закрыта).</summary>
    public void SetClient(string? clientId)
    {
        _clientId = clientId;
        IsVisible = !string.IsNullOrWhiteSpace(clientId) && MarketSpheres.IsHardware && MarketSpheres.Profile.ForemanPriceAvailable;
        _suppress = true;
        try
        {
            _toggle.IsChecked = ClientPriceGroups.IsForeman(clientId);
            // 2026-11-13: у клиента есть уровень цен с сервера — цены ведёт он (назначает владелец на сервере), локальная отметка не нужна; без права на цену переключатель недоступен.
            var level = PriceLevelPricing.LevelOf(clientId);
            var canEdit = ClientPriceGroups.CanEdit(Permissions);
            _toggle.IsEnabled = canEdit && level is null;
            _hint.Text = level is not null
                ? T("Цены этого клиента ведёт уровень цен с сервера (его назначает владелец в NurCRM). Отметка на кассе не нужна.",
                    "Бул кардардын баасын серверден баа деңгээли жүргүзөт (аны ээси NurCRM'де дайындайт). Кассада белги керек эмес.",
                    "This client's prices follow the server price level (the owner assigns it in NurCRM). No till mark is needed.",
                    "Bu müşterinin fiyatlarını sunucudaki fiyat seviyesi belirler (sahibi NurCRM'de atar). Kasada işarete gerek yok.",
                    "Bu mijozning narxlarini serverdagi narx darajasi yuritadi (uni egasi NurCRM'da belgilaydi). Kassada belgi kerak emas.")
                : !canEdit
                    ? T("Менять отметку может только сотрудник с правом «Изменение цены в кассе» (владелец и админ — всегда).",
                        "Белгини «Кассада баасын өзгөртүү» укугу бар кызматкер гана өзгөртө алат (ээси жана админ — дайыма).",
                        "Only staff with the “Price change at checkout” permission can change the mark (owner and admin always can).",
                        "İşareti yalnızca «Kasada fiyat değiştirme» yetkisi olan çalışan değiştirebilir (sahibi ve yönetici her zaman).",
                        "Belgini faqat «Kassada narxni o'zgartirish» huquqiga ega xodim o'zgartira oladi (egasi va admin — doim).")
                    : _defaultHint;
        }
        finally
        {
            _suppress = false;
        }
    }
}
