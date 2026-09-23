using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class NoStockDialog : Window
{
    public bool GoToSite { get; private set; }

    /// <summary>true, если кассир нажал «Показать похожие товары» (см. BtnAlternatives_Click) —
    /// вызывающий код (MainWindow.Dialogs.cs) должен после закрытия диалога проверить это
    /// отдельно от результата ShowDialog&lt;bool?&gt;, который в этом случае возвращается как false.</summary>
    public bool RequestedAlternatives { get; private set; }

    public NoStockDialog()
    {
        InitializeComponent();
    }

    public NoStockDialog(
        string productName,
        double warehouseStock,
        double quantityInCurrentCart,
        double quantityReservedElsewhere,
        double availableToAdd,
        bool mustWeigh = false,
        bool allowOverride = false,
        bool hasAlternatives = false)
        : this()
    {
        BtnAlternatives.IsVisible = hasAlternatives;
        // Раньше override разрешался ТОЛЬКО когда остаток на складе действительно нулевой —
        // если на складе, например, 3 шт, а кассир пытается добавить 4-ю, override не
        // предлагался вообще, и добавить товар сверх остатка было невозможно. Это было
        // излишне строго: сам override никогда не "обходит" проверку молча — он всегда идёт
        // через реальное пополнение склада (см. TryReplenishStockForOverrideAsync, тот же
        // серверный инвентаризационный акт, что использует "Ревизия"), поэтому риска задвоить
        // проданный остаток нет независимо от причины нехватки (нулевой остаток, остаток занят
        // другим чеком, недостаточно для запрошенного количества) — товар физически поступает
        // на склад ПЕРЕД тем, как чек продолжится.
        var canOverride = allowOverride;
        if (canOverride)
        {
            BtnCancel.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
            BtnOverride.IsVisible = true;
        }

        var unit = mustWeigh ? "кг" : "шт.";
        var warehouseText = $"{FormatQty(warehouseStock)} {unit}";

        string AppendOverrideHint(string message) => canOverride
            ? message + " " + Tr.T(
                "Можно продолжить продажу — чек попадёт в «Некорректные чеки».",
                "Сатууну улантууга болот — чек «Туура эмес чектерге» түшөт.")
            : message;

        ProductNameText.Text = $"{Tr.T("Товар:", "Товар:", "Product:", "Ürün:", "Mahsulot:")}\n{productName}";
        if (warehouseStock <= 1e-6)
        {
            TitleText.Text = Tr.T("Товар закончился на складе", "Товар складда түгөндү", "Product is out of stock", "Ürün depoda tükendi", "Mahsulot omborda tugadi");
            MessageText.Text = canOverride
                ? Tr.T(
                    "Остаток на складе равен нулю. Пожалуйста, пополните склад. Можно продолжить продажу — чек попадёт в «Некорректные чеки».",
                    "Складдагы калдык нөлгө барабар. Сураныч, кампаны толуктаңыз. Сатууну улантууга болот — чек «Туура эмес чектерге» түшөт.")
                : Tr.T(
                    "По данным каталога остаток товара действительно равен нулю.",
                    "Каталог боюнча товардын калдыгы чындап эле нөлгө барабар.");
        }
        else if (quantityReservedElsewhere <= 1e-6
                 && quantityInCurrentCart >= warehouseStock - 1e-6)
        {
            TitleText.Text = Tr.T("Остаток уже в корзине", "Калдык себетте эле бар", "Stock already in the cart", "Stok zaten sepette", "Qoldiq allaqachon savatda");
            MessageText.Text = AppendOverrideHint(Tr.T(
                $"Весь доступный остаток ({warehouseText}) уже добавлен в текущую корзину.",
                $"Жеткиликтүү калдыктын баары ({warehouseText}) азыркы себетке кошулган."));
        }
        else if (quantityReservedElsewhere > 1e-6 && availableToAdd <= 1e-6)
        {
            TitleText.Text = Tr.T("Остаток уже зарезервирован", "Калдык мурунтан эле бөлүнгөн", "Stock already reserved", "Stok zaten ayrılmış", "Qoldiq allaqachon zahiralangan");
            MessageText.Text = AppendOverrideHint(quantityInCurrentCart > 1e-6
                ? Tr.T(
                    $"Весь доступный остаток ({warehouseText}) распределён между текущим и другими открытыми или отложенными чеками.",
                    $"Жеткиликтүү калдыктын баары ({warehouseText}) азыркы жана башка ачык же калтырылган чектерге бөлүштүрүлгөн.")
                : Tr.T(
                    $"Весь доступный остаток ({warehouseText}) находится в других открытых или отложенных чеках.",
                    $"Жеткиликтүү калдыктын баары ({warehouseText}) башка ачык же калтырылган чектерде турат."));
        }
        else
        {
            TitleText.Text = Tr.T("Недостаточно остатка", "Калдык жетишсиз", "Insufficient stock", "Stok yetersiz", "Qoldiq yetarli emas");
            MessageText.Text = AppendOverrideHint(Tr.T(
                "Выбранное количество превышает количество, которое ещё можно добавить в этот чек.",
                "Тандалган саны бул чекке дагы кошууга болгон саннан ашып кетти."));
        }

        AvailableText.Text =
            $"{Tr.T("Остаток на складе", "Складдагы калдык", "Warehouse stock", "Depo stoku", "Ombordagi qoldiq")}: {warehouseText}\n" +
            $"{Tr.T("Можно добавить", "Кошсо болот", "Can add", "Eklenebilir", "Qo'shish mumkin")}: {FormatQty(availableToAdd)} {unit}";
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => CloseWithResult(false);

    private void BtnOverride_Click(object? sender, RoutedEventArgs e) => CloseWithResult(true);

    private void BtnAlternatives_Click(object? sender, RoutedEventArgs e)
    {
        RequestedAlternatives = true;
        CloseWithResult(false);
    }

    /// <summary>true = "Подтвердить и добавить" (продать несмотря на нулевой остаток), false = отмена.</summary>
    private void CloseWithResult(bool confirmed)
    {
        // Close must run synchronously on the UI thread while ShowDialog's nested loop
        // is active. Dispatcher.Post after a blocking GetResult() never runs → dialog stuck.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Close(confirmed);
            return;
        }

        Dispatcher.UIThread.Post(() => Close(confirmed));
    }
}
