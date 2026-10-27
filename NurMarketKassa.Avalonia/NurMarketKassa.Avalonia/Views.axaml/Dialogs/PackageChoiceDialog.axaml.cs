using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Выбор способа продажи товара с упаковкой: целая пачка (обычная цена) или
/// поштучно (цена и остаток пересчитываются от package.quantity_in_package/piece_unit_price).
/// Двухшаговый мастер, как в веб-версии: сначала способ продажи, затем количество с живым
/// расчётом суммы.</summary>
public partial class PackageChoiceDialog : Window
{
    private readonly ProductPackageOption? _pieceOption;
    private readonly double _stockQuantity;
    private readonly double _wholePackPrice;
    // 2026-10-27: голосовое управление кассой убрано (есть ИИ-ассистент) — голосовые ответы диалога и подсказка удалены.

    /// <summary>True — выбран режим "Поштучно"; Quantity в этом случае — число штук (не пачек).</summary>
    public bool IsPieceMode { get; private set; }

    /// <summary>Введённое количество: пачки (обычный режим) или штуки (поштучный режим).</summary>
    public double Quantity { get; private set; }

    public PackageChoiceDialog() : this("", "", 0, null) { }

    public PackageChoiceDialog(
        string productTitle,
        string wholePackPriceLine,
        double stockQuantity,
        ProductPackageOption? pieceOption)
    {
        InitializeComponent();
        this.ClampToScreenHeight();
        _pieceOption = pieceOption;
        _stockQuantity = stockQuantity;
        _wholePackPrice = ParsePriceLine(wholePackPriceLine);

        Title = Tr.T("Как добавить в корзину?", "Себетке кантип кошуу керек?", "How should it be added to the cart?", "Sepete nasıl eklenir?", "Savatga qanday qo'shilsin?");
        // 2026-10-17, этап 3: товар и остаток — в шапке общей оболочки модалки (Shell.Title / Shell.Subtitle).
        Shell.Title = productTitle;
        // Раньше здесь был обобщённый вопрос "Как добавить в корзину?" — при наличии
        // поштучной опции (единственный случай, когда это окно вообще открывается, см.
        // MainWindow.Dialogs.cs AddProductFromCatalogAsync) кассир должен сразу видеть тот же
        // вопрос, который заодно и озвучивается .
        QuestionText.Text = pieceOption != null
            ? Tr.T("Поштучно или целая пачка?", "Даанадан же бүтүн пачкадан?", "By the piece or a whole pack?", "Adet mi, tam paket mi?", "Donalabmi yoki butun paketmi?")
            : Tr.T("Как добавить в корзину?", "Себетке кантип кошуу керек?", "How should it be added to the cart?", "Sepete nasıl eklenir?", "Savatga qanday qo'shilsin?");

        WholePackTitle.Text = Tr.T("Целая пачка", "Бүтүн пачка", "Whole pack", "Tam paket", "Butun paket");
        WholePackSubtitle.Text = string.IsNullOrWhiteSpace(wholePackPriceLine)
            ? ""
            // 2026-10-20, живая проверка: «20.00 сом за пачку» — сумма тем же видом, что в чеке («20,00 сом»).
            : $"{MoneyText.SomFromText(wholePackPriceLine)} {Tr.T("за пачку", "пачка үчүн", "per pack", "paket başına", "paket uchun")}";

        if (pieceOption != null)
        {
            PieceTitle.Text = Tr.T("Поштучно", "Даанадан", "By the piece", "Adet olarak", "Donalab");
            // 2026-10-20 (живой аудит кассы, К18): «15,00 сом за шт» (MoneyText), как «20,00 сом за пачку» выше.
            PieceSubtitle.Text = Tr.T(
                $"{MoneyText.Som(pieceOption.PieceUnitPrice)} за шт " +
                $"({FormatQty(pieceOption.QuantityInPackage)} шт в пачке)",
                $"{MoneyText.Som(pieceOption.PieceUnitPrice)} даанасы " +
                $"(пачкада {FormatQty(pieceOption.QuantityInPackage)} даана)", $"{MoneyText.Som(pieceOption.PieceUnitPrice)} per pc ({FormatQty(pieceOption.QuantityInPackage)} pcs per pack)", $"{MoneyText.Som(pieceOption.PieceUnitPrice)}/adet (pakette {FormatQty(pieceOption.QuantityInPackage)} adet)", $"Donasi {MoneyText.Som(pieceOption.PieceUnitPrice)} (paketda {FormatQty(pieceOption.QuantityInPackage)} dona)");
        }
        else
        {
            PieceOption.IsVisible = false;
        }

        CancelButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
        NextButton.Content = Tr.T("Далее", "Кийинки", "Next", "İleri", "Keyingi");
        BackButton.Content = Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga");
        OkButton.Content = Tr.T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish");

        UpdateStockText();

        Opened += (_, _) => WholePackOption.Focus();

        // 2026-10-22 (модалки, пакет 1): окно открывается сканом — Enter сканера (серия быстрых клавиш) не нажимает «Далее»
        // и не добавляет количество (раньше Enter конца кода сразу проводил выбор). Подключено раньше обработчика окна.

        // Tunnel, а не обычный (Bubble) KeyDown: Tab-навигация может увести фокус на
        // "Кийинки"/радиокнопку, и её собственная обработка Enter/Space иначе перехватывала
        // бы событие раньше, чем оно дойдёт до окна. Tunnel гарантированно срабатывает
        // первым, независимо от того, что именно сейчас в фокусе.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
        // 2026-10-22 (модалки, пакет 1): охранник скана подключён ПОСЛЕ обработчика окна: у Tunnel-маршрута Avalonia обработчики одного окна идут в обратном порядке, и охранник должен сработать раньше OnDialogKeyDown.
        ScannerEnterGuard.Attach(this, () => ShowQuantityError(Tr.T(
            "Скан штрихкода не принят. Выберите вариант и нажмите «Далее» или Enter.",
            "Штрихкод кабыл алынган жок. Вариантты тандап, «Кийинки» же Enter басыңыз.",
            "The barcode scan was ignored. Choose an option and press Next or Enter.",
            "Barkod okutması kabul edilmedi. Bir seçenek seçip «İleri» veya Enter'a basın.",
            "Shtrix-kod qabul qilinmadi. Variantni tanlang va «Keyingi» yoki Enter tugmasini bosing.")));
    }

    private void Option_Checked(object? sender, RoutedEventArgs e)
    {
        // Конструктор ещё не отработал при первом Checked обычной радиокнопки — поля могут быть не готовы.
        if (Shell == null)
            return;

        // Читать PieceOption.IsChecked здесь ненадёжно: когда WholePackOption только что стал
        // Checked (группа переключилась), соседний PieceOption.IsChecked ещё не успевает
        // обновиться до false в момент этого события — берём режим прямо из sender.
        SetStockText(_pieceOption != null && ReferenceEquals(sender, PieceOption));
    }

    /// <summary>В режиме "Поштучно" остаток пересчитывается в штуки (пачки × шт. в пачке) —
    /// иначе кассир видит "12 шт" и не понимает, что это 12 пачек, а не 12 отдельных штук.</summary>
    private void UpdateStockText() => SetStockText(isPieceMode: false);

    private void SetStockText(bool isPieceMode)
    {
        Shell.Subtitle = isPieceMode
            ? $"{Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")}: {FormatQty(_stockQuantity * _pieceOption!.QuantityInPackage)} {Tr.T("шт", "даана", "pcs", "adet", "dona")}"
            : $"{Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")}: {FormatQty(_stockQuantity)} {Tr.T("шт", "даана", "pcs", "adet", "dona")}";
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        IsPieceMode = _pieceOption != null && PieceOption.IsChecked == true;

        QuantityLabel.Text = IsPieceMode
            ? Tr.T("Сколько шт добавить?", "Канча даана кошуу керек?", "How many pieces to add?", "Kaç adet eklensin?", "Nechta dona qo'shilsin?")
            : Tr.T("Сколько пачек добавить?", "Канча пачка кошуу керек?", "How many packs to add?", "Kaç paket eklensin?", "Nechta paket qo'shilsin?");
        QuantityUnitText.Text = IsPieceMode ? Tr.T("шт", "даана", "pcs", "adet", "dona") : Tr.T("пачек", "пачка", "packs", "paket", "paket");

        QuantityBox.Text = "1";
        UpdatePriceCalc();

        Step1Panel.IsVisible = false;
        Step2Panel.IsVisible = true;
        QuantityBox.Focus();
        QuantityBox.SelectAll();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        Step2Panel.IsVisible = false;
        Step1Panel.IsVisible = true;
    }

    private void QuantityBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        QuantityErrorText.IsVisible = false; // 2026-10-22 (модалки, пакет 1)
        UpdatePriceCalc();
    }

    private double MaxAllowedQuantity =>
        IsPieceMode ? _stockQuantity * _pieceOption!.QuantityInPackage : _stockQuantity;

    private double UnitPriceForMode =>
        IsPieceMode ? _pieceOption!.PieceUnitPrice : _wholePackPrice;

    private void UpdatePriceCalc()
    {
        var qty = ParseQuantity();
        var unitPrice = UnitPriceForMode;
        var total = (qty ?? 0) * unitPrice;

        PriceCalcText.Text = Tr.T(
            $"Цена: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} сом",
            $"Баасы: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} сом", $"Price: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} som × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} som", $"Fiyat: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} som × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} som", $"Narxi: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} so'm × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} so'm");

        var unit = IsPieceMode ? Tr.T("шт", "даана", "pcs", "adet", "dona") : Tr.T("пачек", "пачка", "packs", "paket", "paket");
        AvailableText.Text = Tr.T(
            $"Доступно до {FormatQty(MaxAllowedQuantity)} {unit}",
            $"Эң көп: {FormatQty(MaxAllowedQuantity)} {unit}", $"Up to {FormatQty(MaxAllowedQuantity)} {unit} available", $"En fazla {FormatQty(MaxAllowedQuantity)} {unit} eklenebilir", $"Ko'pi bilan: {FormatQty(MaxAllowedQuantity)} {unit}");
    }

    private double? ParseQuantity()
    {
        var raw = (QuantityBox.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) && qty > 0
            ? qty
            : null;
    }

    private static double ParsePriceLine(string priceLine)
    {
        var digits = new string(priceLine.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void QuantityBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryCloseOk();
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => TryCloseOk();

    private void TryCloseOk()
    {
        var qty = ParseQuantity();
        if (qty is not { } value)
        {
            // 2026-10-22 (модалки, пакет 1): под полем, а не всплывающим окном.
            ShowQuantityError(Tr.T("Укажите количество.", "Санды көрсөтүңүз.", "Enter the quantity.", "Miktarı belirtin.", "Miqdorni ko'rsating."));
            return;
        }

        var maxAllowed = MaxAllowedQuantity;
        if (value > maxAllowed + 1e-6)
        {
            var unit = IsPieceMode ? Tr.T("шт", "даана", "pcs", "adet", "dona") : Tr.T("пачек", "пачка", "packs", "paket", "paket");

            // Раньше здесь был тупик: сообщение с одной кнопкой «Закрыть» и return. Из-за
            // этого кассир НИКОГДА не видел предложения пополнить склад, хотя оно есть и
            // работает — вызывающий код (MainWindow.Dialogs.cs) сам проверяет остаток и, если
            // его не хватает, показывает «Подтвердить и добавить» с последующим актом
            // пополнения (TryReplenishStockForOverrideAsync). Просто до той ветки дело не
            // доходило: этот диалог не пропускал количество дальше.
            // Теперь спрашиваем — и при согласии отдаём количество наверх, где пополнение и
            // предлагается. Кассир по-прежнему предупреждён, но больше не заперт.
            var answer = PosMessageBox.Show(
                this,
                Tr.T(
                    $"На складе только {FormatQty(maxAllowed)} {unit}. Можно добавить в чек больше и пополнить склад.",
                    $"Кампада болгону {FormatQty(maxAllowed)} {unit}. Чекке көбүрөөк кошуп, кампаны толуктаса болот.",
                    $"Only {FormatQty(maxAllowed)} {unit} in stock. You can add more to the receipt and restock.",
                    $"Depoda yalnızca {FormatQty(maxAllowed)} {unit} var. Fişe daha fazla ekleyip stoğu tamamlayabilirsiniz.",
                    $"Omborda faqat {FormatQty(maxAllowed)} {unit} bor. Chekka ko'proq qo'shib, omborni to'ldirish mumkin."),
                Tr.T("Недостаточно остатка", "Калдык жетишсиз", "Not enough stock", "Stok yetersiz", "Qoldiq yetarli emas"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                // 2026-10-22 (модалки, пакет 1): глагол вместо «Продолжить?» / «Да»; ответ и дальнейшая логика прежние.
                confirmText: Tr.T("Добавить и пополнить склад", "Кошуп, кампаны толуктоо", "Add and restock", "Ekle ve stoğu tamamla", "Qo'shish va omborni to'ldirish"));

            if (answer != MessageBoxResult.Yes)
                return;
        }

        Quantity = value;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>2026-10-22 (модалки, пакет 1): ошибка — красной строкой под полем количества (на шаге 1 — под вариантами);
    /// на шаге 2 фокус в поле, значение выделено. Снимается, когда кассир меняет количество.</summary>
    private void ShowQuantityError(string message)
    {
        QuantityErrorText.Text = message;
        QuantityErrorText.IsVisible = true;
        if (Step2Panel.IsVisible)
        {
            QuantityBox.Focus();
            QuantityBox.SelectAll();
        }
    }

    /// <summary>На шаге выбора способа продажи стрелки переключают между "Целая пачка" и
    /// "Поштучно" — сами RadioButton в обычном StackPanel такой навигации не поддерживают.
    /// Вверх/Вниз — по раскладке, Влево/Вправо (2026-09-27) — так же, как в окне оплаты, где
    /// ими выбирают способ оплаты: кассиру не нужно помнить, в каком окне какие стрелки. Enter на этом шаге —
    /// то же самое, что клик по "Кийинки", и срабатывает независимо от того, что именно
    /// сейчас в фокусе (Tab мог увести фокус на что угодно внутри диалога).</summary>
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Step1Panel.IsVisible)
            return;

        if (PieceOption.IsVisible && (e.Key is Key.Up or Key.Down or Key.Left or Key.Right))
        {
            e.Handled = true;
            var target = WholePackOption.IsChecked == true ? PieceOption : WholePackOption;
            target.IsChecked = true;
            target.Focus();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Next_Click(this, e);
        }
    }
}
