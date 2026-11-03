using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, владелец: «вид чека в десктопе как в мобилке». Окно «Чек» — бумага (общий контрол SaleReceiptCardView) и панель
/// «Печать / QR / PDF / Отправить» (ReceiptActionBar), как экран «Оплачено → Посмотреть чек» мобилки. Открывается с плашки «Продажа
/// прошла» и из предпросмотра чека; длинный чек прокручивается между шапкой и кнопками.</summary>
public sealed class ReceiptViewDialog : Window
{
    private readonly ReceiptActionBar _bar = new();
    private readonly Border _paper; // 2026-11-03 (Д-2): бумагу можно заменить, когда пришёл чек сервера
    private readonly ModalShell _shell;
    private SaleReceiptCardData _data;

    internal ReceiptViewDialog(SaleReceiptCardData data, Func<Task<(bool Ok, string Message)>>? print, string? phone = null)
    {
        SectionCards.AsModal(this, 512);
        _data = data;
        Title = Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek") + " " + data.NumberText;

        _bar.GetData = () => _data;
        if (print != null)
            _bar.Print = async () =>
            {
                var (ok, message) = await print().ConfigureAwait(true);
                _bar.ShowMessage(message, ok);
            };
        _bar.Phone = phone;
        _bar.OwnerWindow = this;
        _bar.IsReady = true;

        // Бумага прокручивается сама (длинный чек в 100+ строк), панель действий закреплена под ней и всегда видна.
        _paper = new Border { Padding = new Thickness(0, 4, 0, 14), Child = SaleReceiptCardView.Build(data) };
        var scroll = new ScrollViewer
        {
            Content = _paper,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(scroll);
        Grid.SetRow(_bar, 1);
        layout.Children.Add(_bar);
        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        _shell = SectionCards.Shell(Title, data.DateTimeText.Length > 0 ? data.DateTimeText : null, layout, SectionCards.Buttons(close));
        _shell.ContentScroll = ScrollBarVisibility.Disabled;
        Content = _shell;
    }

    /// <summary>2026-11-03 (Д-2): окно открыто с чеком кассы, пока сервер медлил; пришёл чек сервера (или стало ясно, что не придёт) —
    /// бумага и номер в заголовке заменяются на месте, без второго окна. Вызывать с потока интерфейса.</summary>
    internal void ReplaceCard(SaleReceiptCardData data)
    {
        _data = data;
        Title = Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek") + " " + data.NumberText;
        _shell.Title = Title;
        _shell.Subtitle = data.DateTimeText.Length > 0 ? data.DateTimeText : null;
        _paper.Child = SaleReceiptCardView.Build(data);
    }
}
