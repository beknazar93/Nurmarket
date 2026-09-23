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
    private readonly IVoiceControlService? _voiceControl;

    /// <summary>Кассир хоть раз тронул диалог мышью ИЛИ клавиатурой (2026-09-05, по жалобе
    /// пользователя: "отключи голос при ручном выборе") — как только это произошло,
    /// OnVoiceAnswer полностью перестаёт действовать до конца жизни этого диалога. Выставляется
    /// в двух Tunnel-обработчиках на самом окне (PointerPressed + KeyDown) — ловят клик/нажатие
    /// по чему угодно внутри (радиокнопки, стрелки/Tab-навигация, "Далее", печать в поле
    /// количества, "Добавить") ДО того, как событие дойдёт до конкретного контроля. Именно
    /// PointerPressed/KeyDown, а не Checked/IsCheckedChanged — последние срабатывают и от
    /// программной установки IsChecked из самого OnVoiceAnswer, их нельзя было бы отличить от
    /// настоящего клика.</summary>
    private bool _manualInteraction;

    /// <summary>True — выбран режим "Поштучно"; Quantity в этом случае — число штук (не пачек).</summary>
    public bool IsPieceMode { get; private set; }

    /// <summary>Введённое количество: пачки (обычный режим) или штуки (поштучный режим).</summary>
    public double Quantity { get; private set; }

    public PackageChoiceDialog() : this("", "", 0, null) { }

    /// <summary>triggeredByVoice — открыт ли этот диалог голосовой командой (AddProductByVoiceAsync)
    /// или кликом по карточке товара (AddProductFromCatalogAsync). По умолчанию false (клик):
    /// звуковая подсказка вопроса и приём голосовых ответов ("касса пачка"/"касса даана") имеют
    /// смысл только тогда, когда кассир и так уже разговаривал с кассой голосом — при обычном
    /// клике мышью включать голос было бы неожиданно и мешало бы (2026-09-05, по прямому
    /// требованию пользователя: "при выборе мышкой озвучка не должна быть! озвучка активируется
    /// только после слова касса").</summary>
    public PackageChoiceDialog(
        string productTitle,
        string wholePackPriceLine,
        double stockQuantity,
        ProductPackageOption? pieceOption,
        bool triggeredByVoice = false)
    {
        InitializeComponent();
        this.ClampToScreenHeight();
        _pieceOption = pieceOption;
        _stockQuantity = stockQuantity;
        _wholePackPrice = ParsePriceLine(wholePackPriceLine);

        Title = Tr.T("Как добавить в корзину?", "Себетке кантип кошуу керек?", "How to add to the cart?", "Sepete nasıl eklenir?", "Savatga qanday qo'shiladi?");
        HeaderTitleText.Text = productTitle;
        // Раньше здесь был обобщённый вопрос "Как добавить в корзину?" — при наличии
        // поштучной опции (единственный случай, когда это окно вообще открывается, см.
        // MainWindow.Dialogs.cs AddProductFromCatalogAsync) кассир должен сразу видеть тот же
        // вопрос, который заодно и озвучивается (VoicePromptPlayer.PlayPieceOrPackChoice).
        QuestionText.Text = pieceOption != null
            ? Tr.T("Поштучно или целая пачка?", "Даанадан же бүтүн пачкадан?", "By the piece or a whole pack?", "Adet mi, tam paket mi?", "Donalab yoki butun paket?")
            : Tr.T("Как добавить в корзину?", "Себетке кантип кошуу керек?", "How to add to the cart?", "Sepete nasıl eklenir?", "Savatga qanday qo'shiladi?");

        WholePackTitle.Text = Tr.T("Целая пачка", "Бүтүн пачка", "Whole pack", "Tam paket", "Butun paket");
        WholePackSubtitle.Text = string.IsNullOrWhiteSpace(wholePackPriceLine)
            ? ""
            : $"{wholePackPriceLine} {Tr.T("за пачку", "пачка үчүн", "per pack", "paket başına", "paket uchun")}";

        if (pieceOption != null)
        {
            PieceTitle.Text = Tr.T("Поштучно", "Даанадан", "By the piece", "Adet olarak", "Donalab");
            PieceSubtitle.Text = Tr.T(
                $"{pieceOption.PieceUnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом за шт " +
                $"({FormatQty(pieceOption.QuantityInPackage)} шт в пачке)",
                $"{pieceOption.PieceUnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом даанасы " +
                $"(пачкада {FormatQty(pieceOption.QuantityInPackage)} даана)");
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

        // Голосовая подсказка "Поштучно или целая пачка?" — только когда у товара ДЕЙСТВИТЕЛЬНО
        // есть выбор (pieceOption != null, т.е. видна опция "Поштучно"); если у товара нет
        // поштучной цены, PieceOption скрыт и озвучивать нечего — играть подсказку в этом случае
        // было бы неверно (вопрос про два варианта, когда фактически доступен только один).
        //
        // Кассир может ОТВЕТИТЬ на этот же вопрос голосом, не повторяя название товара — "касса
        // поштучно 3"/"касса пачка" (2026-09-05, по жалобе пользователя: раньше такие голые
        // команды без названия товара падали в Unknown/"товар не найден", хотя явно относились
        // к уже открытому диалогу). Подписка только на время жизни этого диалога, только когда
        // выбор вообще есть, и ТОЛЬКО когда диалог сам открыт голосом (triggeredByVoice) — при
        // обычном клике по карточке товара мышью ни подсказка, ни приём голосовых ответов не
        // включаются вообще.
        if (pieceOption != null && triggeredByVoice)
        {
            Opened += (_, _) => VoicePromptPlayer.PlayPieceOrPackChoice();

            _voiceControl = App.GetRequiredService<IVoiceControlService>();
            _voiceControl.CommandRecognized += OnVoiceAnswer;
            Closed += (_, _) => _voiceControl.CommandRecognized -= OnVoiceAnswer;
        }

        // Tunnel, а не обычный (Bubble) KeyDown: Tab-навигация может увести фокус на
        // "Кийинки"/радиокнопку, и её собственная обработка Enter/Space иначе перехватывала
        // бы событие раньше, чем оно дойдёт до окна. Tunnel гарантированно срабатывает
        // первым, независимо от того, что именно сейчас в фокусе.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);

        // См. _manualInteraction — ЛЮБОЙ физический ввод внутри диалога (клик мышью ИЛИ клавиша
        // — стрелки/Tab/Enter/печать в поле количества) навсегда отключает голосовые ответы для
        // НЕГО (не для остальной кассы, только для этого конкретного окна). Два отдельных
        // обработчика (не один общий), оба Tunnel — оба успевают сработать ДО специфической
        // обработки конкретного контрола (переключения радиокнопки, "Далее" и т.д.).
        AddHandler(PointerPressedEvent, (_, _) => _manualInteraction = true, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _manualInteraction = true, RoutingStrategies.Tunnel);
    }

    private void Option_Checked(object? sender, RoutedEventArgs e)
    {
        // Конструктор ещё не отработал при первом Checked обычной радиокнопки — поля могут быть не готовы.
        if (StockText == null)
            return;

        // Читать PieceOption.IsChecked здесь ненадёжно: когда WholePackOption только что стал
        // Checked (группа переключилась), соседний PieceOption.IsChecked ещё не успевает
        // обновиться до false в момент этого события — берём режим прямо из sender.
        SetStockText(_pieceOption != null && ReferenceEquals(sender, PieceOption));
    }

    /// <summary>Голосовой ответ на вопрос "Поштучно или целая пачка?" (2026-09-05) — "касса
    /// поштучно 3"/"касса пачка"/"касса целая пачка 2". Только команды БЕЗ названия товара
    /// (ProductQuery пуст): если кассир назвал конкретный товар голосом, это отдельная новая
    /// команда добавления, а не ответ этому диалогу — обрабатывать её здесь нельзя, иначе
    /// "касса молоко 2 пачки" на фоне открытого диалога про манго добавило бы молоко как манго.
    /// Выбирает нужный RadioButton, проходит тот же путь, что и ручной клик "Далее" → ввод
    /// количества → "Добавить" (TryCloseOk), включая проверку остатка — никакой отдельной
    /// логики добавления в обход обычной валидации.</summary>
    private void OnVoiceAnswer(VoiceCommandResult result)
    {
        // Диагностика (2026-09-05) — раньше здесь не логировалось НИЧЕГО при отказе, и когда
        // пользователь сообщил "пачка/даана не срабатывает, диалог открыт", разобраться, на
        // каком именно условии голос отсеялся, было невозможно без чтения кода. Логируем
        // причину каждого отказа, чтобы следующий такой случай был виден в логе сразу.
        if (result.Intent != VoiceIntent.AddProduct || result.UnitKind == VoiceUnitKind.None)
            return; // не команда "добавить" или единица не распознана — не ответ этому диалогу, лог не нужен (шум)

        if (_manualInteraction)
        {
            PosLogger.Log("PackageChoiceDialog: голосовой ответ проигнорирован — кассир уже взаимодействовал вручную.", "VOICE");
            return;
        }
        if (!string.IsNullOrWhiteSpace(result.ProductQuery))
            return; // назван конкретный товар — отдельная новая команда, не ответ этому диалогу (шум, не логируем)
        if (result.UnitKind == VoiceUnitKind.Piece && _pieceOption == null)
        {
            PosLogger.Log("PackageChoiceDialog: сказано \"поштучно\", но у товара нет поштучной цены — игнорируется.", "VOICE");
            return;
        }
        // Голосовой замок (2026-09-05) — чужой голос не должен уметь ответить на этот диалог
        // ("касса поштучно 3"), даже если сам вопрос был задан легитимному кассиру. null
        // (замок выключен/голос не зарегистрирован) пропускается, как и везде.
        if (result.VoiceMatched == false)
        {
            PosLogger.Log("PackageChoiceDialog: голосовой ответ отклонён голосовым замком.", "VOICE");
            return;
        }

        PosLogger.Log($"PackageChoiceDialog: голосовой ответ принят — unitKind={result.UnitKind}, qty={result.Quantity}.", "VOICE");
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
                return;
            if (_manualInteraction)
                return;

            (result.UnitKind == VoiceUnitKind.Piece ? PieceOption : WholePackOption).IsChecked = true;
            if (Step1Panel.IsVisible)
                Next_Click(this, new RoutedEventArgs());

            QuantityBox.Text = result.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
            TryCloseOk();
        });
    }

    /// <summary>В режиме "Поштучно" остаток пересчитывается в штуки (пачки × шт. в пачке) —
    /// иначе кассир видит "12 шт" и не понимает, что это 12 пачек, а не 12 отдельных штук.</summary>
    private void UpdateStockText() => SetStockText(isPieceMode: false);

    private void SetStockText(bool isPieceMode)
    {
        StockText.Text = isPieceMode
            ? $"{Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")}: {FormatQty(_stockQuantity * _pieceOption!.QuantityInPackage)} шт"
            : $"{Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")}: {FormatQty(_stockQuantity)} шт";
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        IsPieceMode = _pieceOption != null && PieceOption.IsChecked == true;

        QuantityLabel.Text = IsPieceMode
            ? Tr.T("Сколько шт добавить?", "Канча даана кошуу керек?", "How many pcs to add?", "Kaç adet eklensin?", "Nechta dona qo'shilsin?")
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

    private void QuantityBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) =>
        UpdatePriceCalc();

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
            $"Баасы: {unitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом × {(qty ?? 0).ToString("0.###", CultureInfo.InvariantCulture)} = {total.ToString("0.00", CultureInfo.InvariantCulture)} сом");

        var unit = IsPieceMode ? Tr.T("шт", "даана", "pcs", "adet", "dona") : Tr.T("пачек", "пачка", "packs", "paket", "paket");
        AvailableText.Text = Tr.T(
            $"Доступно до {FormatQty(MaxAllowedQuantity)} {unit}",
            $"{FormatQty(MaxAllowedQuantity)} {unit} чейин жеткиликтүү");
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
            PosMessageBox.Show(this, Tr.T("Укажите количество.", "Санды көрсөтүңүз.", "Specify the quantity.", "Miktarı belirtin.", "Miqdorni ko'rsating."), Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    $"На складе только {FormatQty(maxAllowed)} {unit}. Продолжить и пополнить склад?",
                    $"Складда болгону {FormatQty(maxAllowed)} {unit}. Улантып, складды толуктайсызбы?",
                    $"Only {FormatQty(maxAllowed)} {unit} in stock. Continue and replenish?",
                    $"Stokta yalnızca {FormatQty(maxAllowed)} {unit} var. Devam edip stok eklensin mi?",
                    $"Omborda faqat {FormatQty(maxAllowed)} {unit} bor. Davom etib, omborni toldirasizmi?"),
                Tr.T("Недостаточно остатка", "Калдык жетишсиз", "Insufficient stock", "Stok yetersiz", "Qoldiq yetarli emas"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        Quantity = value;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>На шаге выбора способа продажи стрелки Вверх/Вниз переключают между
    /// "Целая пачка" и "Поштучно" — сами RadioButton в обычном StackPanel такой навигации
    /// не поддерживают (в отличие от ListBox/WrapPanel в каталоге). Enter на этом шаге —
    /// то же самое, что клик по "Кийинки", и срабатывает независимо от того, что именно
    /// сейчас в фокусе (Tab мог увести фокус на что угодно внутри диалога).</summary>
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Step1Panel.IsVisible)
            return;

        if (PieceOption.IsVisible && (e.Key == Key.Up || e.Key == Key.Down))
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
